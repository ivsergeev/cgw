using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CorpGateway.Models;

namespace CorpGateway.Services;

/// <summary>
/// Lightweight HTTP server on localhost that exposes skill execution to the CLI agent.
/// Authentication: static bearer token loaded from config.
/// Skill requests are proxied through the browser via CDP fetch() — the browser handles all auth.
/// </summary>
public class LocalApiServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly SkillsRepository _repo;
    private readonly ChromeCdpService? _cdpService;
    private CancellationTokenSource? _cts;
    private string _bearerToken = "";

    public int Port { get; private set; }
    public bool IsRunning { get; private set; }

    public LocalApiServer(SkillsRepository repo, ChromeCdpService? cdpService = null)
    {
        _repo = repo;
        _cdpService = cdpService;
    }

    public void Start(int port, string bearerToken)
    {
        Port = port;
        _bearerToken = bearerToken;
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        IsRunning = true;

        _cts = new CancellationTokenSource();
        Task.Run(() => ListenAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener.Stop();
        IsRunning = false;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(ctx), ct);
            }
            catch (HttpListenerException) { break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        resp.ContentType = "application/json";
        resp.Headers.Add("Access-Control-Allow-Origin", "http://localhost");

        // Auth check
        var auth = req.Headers["Authorization"] ?? "";
        if (!auth.Equals($"Bearer {_bearerToken}", StringComparison.Ordinal))
        {
            await WriteJsonAsync(resp, 401, new { error = "Unauthorized" });
            return;
        }

        try
        {
            var path = req.Url?.AbsolutePath ?? "/";

            // GET /skills  - list all skills (compact format for agent)
            if (req.HttpMethod == "GET" && path == "/skills")
            {
                var compact = _repo.ExportCompact();
                await WriteJsonAsync(resp, 200, new { skills = compact });
                return;
            }

            // GET /skills/{name}/schema  - usage-relevant schema for one skill
            if (req.HttpMethod == "GET" && path.StartsWith("/skills/") && path.EndsWith("/schema"))
            {
                var skillName = path.Split('/')[2];
                var skill = _repo.GetEnabledSkills().FirstOrDefault(s =>
                    s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
                if (skill == null)
                {
                    await WriteJsonAsync(resp, 404, new { error = "Skill not found" });
                    return;
                }
                // Return only what an agent needs to call the skill correctly
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
                await WriteJsonAsync(resp, 200, schema);
                return;
            }

            // POST /invoke  - execute a skill
            if (req.HttpMethod == "POST" && path == "/invoke")
            {
                // Limit request body to 1 MB
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                var bodyChars = new char[1024 * 1024];
                var read = await reader.ReadAsync(bodyChars, 0, bodyChars.Length);
                var body = new string(bodyChars, 0, read);
                var invokeReq = JsonSerializer.Deserialize<InvokeRequest>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (invokeReq == null || string.IsNullOrEmpty(invokeReq.Skill))
                {
                    await WriteJsonAsync(resp, 400, new { error = "Missing skill name" });
                    return;
                }

                var result = await InvokeSkillAsync(invokeReq);
                await WriteJsonAsync(resp, result.StatusCode, result.Body);
                return;
            }

            // GET /health
            if (req.HttpMethod == "GET" && path == "/health")
            {
                var cdpStatus = _cdpService?.IsConnected == true ? "connected" : "disconnected";
                await WriteJsonAsync(resp, 200, new { status = "ok", skills = _repo.GetEnabledSkills().Count, cdp = cdpStatus });
                return;
            }

            await WriteJsonAsync(resp, 404, new { error = "Not found" });
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(resp, 500, new { error = ex.Message });
        }
    }

    private async Task<(int StatusCode, object Body)> InvokeSkillAsync(InvokeRequest req)
    {
        var skill = _repo.GetEnabledSkills().FirstOrDefault(s =>
            s.Name.Equals(req.Skill, StringComparison.OrdinalIgnoreCase));

        if (skill == null)
            return (404, new { error = $"Skill '{req.Skill}' not found" });

        // Validate CDP connection
        if (_cdpService?.IsConnected != true)
            return (503, new { error = "CDP not connected. Connect to Chrome in the Gateway UI." });

        // Build URL with path and query params
        var url = skill.Url;
        var parameters = req.Parameters ?? new Dictionary<string, string>();

        // Validate required params (must exist and be non-empty)
        foreach (var p in skill.Parameters.Where(p => p.Required))
        {
            if (!parameters.TryGetValue(p.Name, out var val) || string.IsNullOrWhiteSpace(val))
                return (400, new { error = $"Missing required parameter: {p.Name}" });
        }

        // Substitute path parameters: {name} in URL (case-insensitive)
        var remainingParams = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in parameters)
        {
            var placeholder = $"{{{kv.Key}}}";
            if (url.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                url = url.Replace(placeholder, Uri.EscapeDataString(kv.Value),
                    StringComparison.OrdinalIgnoreCase);
                remainingParams.Remove(kv.Key);
            }
        }

        // Build body for POST/PUT/PATCH
        string? bodyJson = null;
        if (skill.HttpMethod != "GET" && skill.HttpMethod != "DELETE")
        {
            if (!string.IsNullOrWhiteSpace(skill.BodyTemplate))
            {
                bodyJson = skill.BodyTemplate;
                var paramDefs = skill.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var kv in remainingParams)
                {
                    var paramType = paramDefs.TryGetValue(kv.Key, out var def) ? def.Type : ParameterType.String;
                    string substitution;
                    switch (paramType)
                    {
                        case ParameterType.Integer:
                            substitution = long.TryParse(kv.Value.Trim(), out var intVal)
                                ? intVal.ToString()
                                : "0";
                            break;
                        case ParameterType.Float:
                            substitution = double.TryParse(kv.Value.Trim(),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var floatVal)
                                ? floatVal.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                : "0";
                            break;
                        case ParameterType.Boolean:
                            // Normalize to JSON boolean literal
                            substitution = kv.Value.Trim().ToLowerInvariant() switch
                            {
                                "true" or "1" or "yes" => "true",
                                _ => "false"
                            };
                            break;
                        default:
                            // String/Date — JSON-escape the value
                            substitution = JsonSerializer.Serialize(kv.Value)[1..^1];
                            break;
                    }
                    bodyJson = bodyJson.Replace($"{{{{{kv.Key}}}}}", substitution);
                }
            }
            else
            {
                bodyJson = JsonSerializer.Serialize(remainingParams);
            }
        }

        // Build query string for GET/DELETE
        if (skill.HttpMethod == "GET" || skill.HttpMethod == "DELETE")
        {
            if (remainingParams.Count > 0)
            {
                var query = string.Join("&", remainingParams.Select(kv =>
                    $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
                url = $"{url}?{query}";
            }
        }

        // Build headers (skill-defined)
        var headers = new Dictionary<string, string>(skill.Headers, StringComparer.OrdinalIgnoreCase);
        // Content-Type auto-set is handled in BuildFetchJs (JS-side, case-insensitive check)

        // Execute via browser CDP fetch()
        var originOverride = string.IsNullOrWhiteSpace(skill.FetchOrigin) ? null : skill.FetchOrigin.TrimEnd('/');
        var fetchResult = await _cdpService!.ExecuteFetchAsync(url, skill.HttpMethod, bodyJson, headers, originOverride: originOverride);

        if (fetchResult.Error != null)
            return (502, new { error = $"Browser fetch error: {fetchResult.Error}" });

        var respBody = fetchResult.Body;

        // Apply response filter if configured
        if (!string.IsNullOrWhiteSpace(skill.ResponseFilter) && fetchResult.IsSuccess)
            respBody = JsonFilterHelper.ApplyFilter(respBody, skill.ResponseFilter);

        // Ensure valid HTTP status code (must be 3 digits: 100-599)
        var statusCode = fetchResult.StatusCode;
        if (statusCode < 100 || statusCode > 599)
            statusCode = 502;

        if (string.IsNullOrWhiteSpace(respBody))
            return (statusCode, new { });

        try
        {
            var parsed = JsonSerializer.Deserialize<object>(respBody);
            return (statusCode, parsed!);
        }
        catch
        {
            return (statusCode, new { raw = respBody });
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, int status, object body)
    {
        resp.StatusCode = status;
        resp.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        await using var output = resp.OutputStream;
        await output.WriteAsync(bytes);
    }

    public void Dispose()
    {
        Stop();
    }
}

public class InvokeRequest
{
    public string Skill { get; set; } = "";
    public Dictionary<string, string>? Parameters { get; set; }
}
