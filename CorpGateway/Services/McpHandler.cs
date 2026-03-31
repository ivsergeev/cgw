using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CorpGateway.Models;

namespace CorpGateway.Services;

/// <summary>
/// MCP (Model Context Protocol) Streamable HTTP handler.
/// Implements JSON-RPC 2.0 with 5 meta-tools: cgw_groups, cgw_list, cgw_schema, cgw_invoke, cgw_health.
/// Spec: https://modelcontextprotocol.io/specification/2025-03-26
/// </summary>
public class McpHandler
{
    private readonly SkillsRepository _repo;
    private readonly LocalApiServer _apiServer;
    private readonly ChromeCdpService? _cdpService;
    private readonly AppConfig _config;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public McpHandler(SkillsRepository repo, LocalApiServer apiServer, ChromeCdpService? cdpService, AppConfig config)
    {
        _repo = repo;
        _apiServer = apiServer;
        _cdpService = cdpService;
        _config = config;
    }

    /// <summary>
    /// Handle a JSON-RPC request. Returns (statusCode, responseJson).
    /// Notifications return (202, null).
    /// </summary>
    public async Task<(int StatusCode, string? ResponseJson)> HandleAsync(string requestBody)
    {
        JsonElement json;
        try { json = JsonSerializer.Deserialize<JsonElement>(requestBody); }
        catch { return (400, JsonRpcError(null, -32700, "Parse error")); }

        var method = json.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        var id = json.TryGetProperty("id", out var idProp) ? (object?)idProp.Clone() : null;
        var @params = json.TryGetProperty("params", out var p) ? p : default;

        // Notifications (no id) → 202 Accepted
        if (id == null)
        {
            return method switch
            {
                "notifications/initialized" => (202, null),
                "notifications/cancelled" => (202, null),
                _ => (202, null)
            };
        }

        // Requests (have id) → JSON-RPC response
        var result = method switch
        {
            "initialize" => HandleInitialize(id),
            "tools/list" => HandleToolsList(id),
            "tools/call" => await HandleToolsCall(id, @params),
            "ping" => JsonRpcResult(id, new { }),
            _ => JsonRpcError(id, -32601, $"Method not found: {method}")
        };

        return (200, result);
    }

    // ── initialize ──────────────────────────────────────────────────────

    private string HandleInitialize(object? id)
    {
        return JsonRpcResult(id, new
        {
            protocolVersion = "2025-03-26",
            capabilities = new
            {
                tools = new { }
            },
            serverInfo = new
            {
                name = "CorpGateway",
                version = "1.0.0"
            },
            instructions = _config.McpInstructions.Trim()
        });
    }

    // ── tools/list ──────────────────────────────────────────────────────

    private string HandleToolsList(object? id)
    {
        var emptySchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray()
        };

        var tools = new JsonArray
        {
            BuildTool("cgw_groups",
                "List available skill groups with descriptions. Call first to discover what corporate systems are connected.",
                (JsonObject)emptySchema.DeepClone()),
            BuildTool("cgw_list",
                "List available skills. Returns compact text with skill signatures and descriptions. Optionally filter by group.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["group"] = new JsonObject { ["type"] = "string", ["description"] = "Group ID to filter by (from cgw_groups). Omit for all skills." }
                    },
                    ["required"] = new JsonArray()
                }),
            BuildTool("cgw_schema",
                "Get parameter details for a specific skill. Returns name, description, and typed parameters.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["skill"] = new JsonObject { ["type"] = "string", ["description"] = "Skill name (from cgw_list)." }
                    },
                    ["required"] = new JsonArray("skill")
                }),
            BuildTool("cgw_invoke",
                "Call a corporate skill and return the JSON response. Confirm with user before calling write operations.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["skill"] = new JsonObject { ["type"] = "string", ["description"] = "Skill name." },
                        ["params"] = new JsonObject { ["type"] = "object", ["description"] = "Key-value parameters. All values as strings.", ["additionalProperties"] = new JsonObject { ["type"] = "string" } }
                    },
                    ["required"] = new JsonArray("skill")
                }),
            BuildTool("cgw_health",
                "Check if CorpGateway is running and Chrome CDP is connected.",
                (JsonObject)emptySchema.DeepClone())
        };

        return JsonRpcResult(id, new { tools });
    }

    private static JsonObject BuildTool(string name, string description, JsonObject inputSchema)
    {
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = inputSchema
        };
    }

    // ── tools/call ──────────────────────────────────────────────────────

    private async Task<string> HandleToolsCall(object? id, JsonElement @params)
    {
        var toolName = @params.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var args = @params.TryGetProperty("arguments", out var a) ? a : default;

        try
        {
            var result = toolName switch
            {
                "cgw_groups" => CallGroups(),
                "cgw_list" => CallList(args),
                "cgw_schema" => CallSchema(args),
                "cgw_invoke" => await CallInvoke(args),
                "cgw_health" => CallHealth(),
                _ => throw new Exception($"Unknown tool: {toolName}")
            };

            return JsonRpcResult(id, new
            {
                content = new[] { new { type = "text", text = result } },
                isError = false
            });
        }
        catch (Exception ex)
        {
            return JsonRpcResult(id, new
            {
                content = new[] { new { type = "text", text = $"Error: {ex.Message}" } },
                isError = true
            });
        }
    }

    // ── Tool implementations ────────────────────────────────────────────

    private string CallGroups()
    {
        var groups = _repo.GetGroups().Where(g => g.Enabled).Select(g => new
        {
            id = g.Id,
            name = g.Name,
            description = g.Description
        });
        return JsonSerializer.Serialize(new { groups }, _jsonOpts);
    }

    private string CallList(JsonElement args)
    {
        string? groupId = null;
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("group", out var g) &&
            g.ValueKind == JsonValueKind.String)
        {
            groupId = g.GetString();
        }
        return _repo.ExportCompact(groupId);
    }

    private string CallSchema(JsonElement args)
    {
        var skillName = args.ValueKind == JsonValueKind.Object &&
                        args.TryGetProperty("skill", out var s) &&
                        s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? ""
            : throw new Exception("Missing required parameter: skill");

        var skill = _repo.GetEnabledSkills().FirstOrDefault(sk =>
            sk.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase))
            ?? throw new Exception($"Skill not found: {skillName}");

        var schema = new
        {
            name = skill.Name,
            description = skill.Description,
            parameters = skill.Parameters.ConvertAll(p => new
            {
                name = p.Name,
                type = p.Type.ToString().ToLowerInvariant(),
                required = p.Required,
                description = p.Description
            })
        };
        return JsonSerializer.Serialize(schema, _jsonOpts);
    }

    private async Task<string> CallInvoke(JsonElement args)
    {
        var skillName = args.ValueKind == JsonValueKind.Object &&
                        args.TryGetProperty("skill", out var s) &&
                        s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? ""
            : throw new Exception("Missing required parameter: skill");

        var parameters = new Dictionary<string, string>();
        if (args.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in p.EnumerateObject())
                parameters[kv.Name] = kv.Value.ToString();
        }

        var req = new InvokeRequest { Skill = skillName, Parameters = parameters };
        var (statusCode, body) = await _apiServer.InvokeSkillAsync(req);

        var json = JsonSerializer.Serialize(body, _jsonOpts);
        if (statusCode >= 400)
            throw new Exception($"HTTP {statusCode}: {json}");

        return json;
    }

    private string CallHealth()
    {
        var cdpStatus = _cdpService?.IsConnected == true ? "connected" : "disconnected";
        return JsonSerializer.Serialize(new
        {
            status = "ok",
            skills = _repo.GetEnabledSkills().Count,
            cdp = cdpStatus
        }, _jsonOpts);
    }

    // ── JSON-RPC helpers ────────────────────────────────────────────────

    private static string JsonRpcResult(object? id, object result)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result
        }, _jsonOpts);
    }

    private static string JsonRpcError(object? id, int code, string message)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code, message }
        }, _jsonOpts);
    }
}
