using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CorpGatewayCli;

/// cgw - CorpGateway CLI
///
/// Commands:
///   cgw list                         List all skills (compact)
///   cgw list --group &lt;n&gt;            List skills in a group
///   cgw schema &lt;skill&gt;              Full JSON schema for a skill
///   cgw invoke &lt;skill&gt; [key=val ...] Invoke a skill
///   cgw health                       Check gateway status
///   cgw init-claude                  Generate CLAUDE.md for Claude Code
///   cgw init-opencode                Generate AGENTS.md + opencode.json for OpenCode
class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        // Parse global flags
        string? configPath = null;
        bool jsonOutput = false;
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i]; break;
                case "--json":
                    jsonOutput = true; break;
                case "-h" or "--help":
                    PrintHelp(); return 0;
                default:
                    positional.Add(args[i]); break;
            }
        }

        if (positional.Count == 0) { PrintHelp(); return 1; }

        var command = positional[0];

        // init-* commands don't need a running server
        if (command == "init-claude")
        {
            var cfg = GatewayConfig.Load(configPath);
            return GenerateClaudeMd(cfg);
        }

        if (command == "init-opencode")
        {
            var cfg = GatewayConfig.Load(configPath);
            return GenerateOpenCode(cfg);
        }

        var config = GatewayConfig.Load(configPath);

        if (string.IsNullOrEmpty(config.ApiToken))
        {
            Err("No API token found. Start CorpGateway first, or set CGW_TOKEN env var.");
            return 1;
        }

        using var client = new GatewayClient(config);

        try
        {
            return command switch
            {
                "health" => await HealthCmd(client, jsonOutput),
                "list"   => await ListCmd(client, positional),
                "schema" => await SchemaCmd(client, positional),
                "invoke" => await InvokeCmd(client, positional),
                _        => UnknownCommand(command)
            };
        }
        catch (HttpRequestException ex) when (
            ex.Message.Contains("actively refused") ||
            ex.Message.Contains("Connection refused"))
        {
            Err($"Cannot connect to CorpGateway on port {config.ApiPort}.");
            Err("Make sure the tray app is running.");
            return 2;
        }
        catch (Exception ex)
        {
            Err($"Error: {ex.Message}");
            return 1;
        }
    }

    // health command
    static async Task<int> HealthCmd(GatewayClient client, bool jsonOutput)
    {
        var result = await client.HealthAsync();
        if (jsonOutput)
        {
            Console.WriteLine(result?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"Status: {result?["status"]?.GetValue<string>() ?? "unknown"}");
            Console.WriteLine($"Skills: {result?["skills"]?.GetValue<int>() ?? 0}");
        }
        return 0;
    }

    // list command
    static async Task<int> ListCmd(GatewayClient client, List<string> args)
    {
        var compact = await client.ListCompactAsync();
        Console.Write(compact);
        return 0;
    }

    // schema command
    static async Task<int> SchemaCmd(GatewayClient client, List<string> args)
    {
        if (args.Count < 2) { Err("Usage: cgw schema <skill-name>"); return 1; }
        var schema = await client.SchemaAsync(args[1]);
        Console.WriteLine(schema?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    // invoke command
    static async Task<int> InvokeCmd(GatewayClient client, List<string> args)
    {
        if (args.Count < 2) { Err("Usage: cgw invoke <skill-name> [param=value ...]"); return 1; }

        var skillName = args[1];
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 2; i < args.Count; i++)
        {
            var eq = args[i].IndexOf('=');
            if (eq > 0)
            {
                parameters[args[i][..eq]] = args[i][(eq + 1)..];
            }
            else
            {
                Err($"Parameter '{args[i]}' must be key=value format.");
                return 1;
            }
        }

        var (statusCode, body) = await client.InvokeAsync(skillName, parameters);
        var json = body?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";

        if (statusCode >= 400)
        {
            Err($"HTTP {statusCode}:");
            Console.Error.WriteLine(json);
            return 1;
        }

        Console.WriteLine(json);
        return 0;
    }

    // init-claude: generates CLAUDE.md for Claude Code
    static int GenerateClaudeMd(GatewayConfig cfg)
    {
        var outputPath = Path.Combine(Directory.GetCurrentDirectory(), "CLAUDE.md");
        File.WriteAllText(outputPath, BuildAgentInstructions(cfg,
            header: "# CorpGateway — tool instructions (Claude Code)",
            footer: """
## Note

This file is auto-generated by `cgw init-claude`.
Commit it to your repository so all Claude Code sessions pick it up automatically.
"""));

        Console.WriteLine($"Generated: {outputPath}");
        Console.WriteLine("Claude Code reads CLAUDE.md automatically from the project root.");
        return 0;
    }

    // init-opencode: generates AGENTS.md + opencode.json for OpenCode
    static int GenerateOpenCode(GatewayConfig cfg)
    {
        var cwd = Directory.GetCurrentDirectory();

        // AGENTS.md - OpenCode reads this as system instructions
        var agentsMd = Path.Combine(cwd, "AGENTS.md");
        File.WriteAllText(agentsMd, BuildAgentInstructions(cfg,
            header: "# CorpGateway — tool instructions (OpenCode)",
            footer: """
## Note

This file is auto-generated by `cgw init-opencode`.
Commit it to your repository so all OpenCode sessions pick it up automatically.
"""));

        // opencode.json - registers cgw commands as named tools
        var opencodeJson = Path.Combine(cwd, "opencode.json");
        File.WriteAllText(opencodeJson, $$"""
{
  "$schema": "https://opencode.ai/config.schema.json",
  "instructions": ["AGENTS.md"],
  "tools": {
    "cgw_list": {
      "description": "List all available corporate skills. Call at session start.",
      "command": "cgw list"
    },
    "cgw_schema": {
      "description": "Full JSON schema for a skill. Argument: skill name.",
      "command": "cgw schema"
    },
    "cgw_invoke": {
      "description": "Invoke a corporate skill. Arguments: skill_name key=value ...",
      "command": "cgw invoke"
    },
    "cgw_health": {
      "description": "Check if CorpGateway is running.",
      "command": "cgw health"
    }
  }
}
""");

        Console.WriteLine($"Generated: {agentsMd}");
        Console.WriteLine($"Generated: {opencodeJson}");
        Console.WriteLine();
        Console.WriteLine("OpenCode reads AGENTS.md as system instructions automatically.");
        Console.WriteLine("cgw_list / cgw_schema / cgw_invoke / cgw_health are registered as tools.");
        return 0;
    }

    // Shared instruction content used by both init commands
    static string BuildAgentInstructions(GatewayConfig cfg, string header, string footer) => $$"""
{{header}}

You have access to corporate internal endpoints via the `cgw` CLI tool.
Always use `cgw` to interact with corporate data. Do not make HTTP requests directly.

## Commands

```bash
cgw list                              # all available skills (call at session start)
cgw schema <skill-name>               # full parameter schema for one skill
cgw invoke <skill-name> key=value ... # call a skill, returns JSON
cgw health                            # verify CorpGateway is running
```

## Gateway

Port: `{{cfg.ApiPort}}`. Bearer token authentication is handled automatically by `cgw`.

## Workflow

1. Call `cgw list` at the start of every session to discover available skills
2. Use `cgw schema <n>` when you need full parameter details for a skill
3. Call `cgw invoke <n> key=val ...` to execute a skill; output is always JSON
4. Omit optional parameters entirely; never pass empty strings

## Parameter handling

Skills support GET, POST, PUT, PATCH and DELETE methods. Parameter routing is automatic:
- Path parameters: `{id}` in URL is substituted from provided parameters
- Query parameters: remaining params go as query string (GET/DELETE)
- Body: remaining params go as JSON body (POST/PUT/PATCH)
- Body template: if a skill has a JSON body template, parameter placeholders are substituted into it

Use `cgw schema <skill>` to see the HTTP method, URL pattern, body template and parameter types.

## Rules

- Only call skills that appear in `cgw list`. Never guess skill names.
- Skills may perform both read and write operations (GET, POST, PUT, PATCH, DELETE).
- Report non-200 errors to the user verbatim.
- Never cache results across turns; always call `cgw invoke` for fresh data.
- Confirm with the user before invoking skills that mutate data (POST/PUT/PATCH/DELETE).

## Example

```
$ cgw list
# Available skills
get_employee(id:int)                    // Employee record by ID
update_status(key:str, status:str)      // Update issue status (PUT)

$ cgw invoke get_employee id=42
{
  "id": 42,
  "name": "Jane Smith",
  "dept": "Engineering"
}
```

{{footer}}
""";

    static void PrintHelp() => Console.WriteLine("""
cgw - CorpGateway CLI

Commands:
  cgw list                        List skills (compact, token-efficient)
  cgw list --group <n>            List skills in a group
  cgw schema <skill>              Full JSON schema for a skill
  cgw invoke <skill> [k=v ...]    Invoke a skill; returns JSON
  cgw health                      Check gateway server status
  cgw init-claude                 Generate CLAUDE.md for Claude Code
  cgw init-opencode               Generate AGENTS.md + opencode.json for OpenCode

Global flags:
  --config <path>   Config file path (default: %APPDATA%/CorpGateway/config.json)
  --json            Force JSON output where applicable
  -h, --help        Show this help

Environment variables:
  CGW_PORT    Override gateway port
  CGW_TOKEN   Override bearer token
  CGW_CONFIG  Override config file path
""");

    static void Err(string msg) => Console.Error.WriteLine($"cgw: {msg}");

    static int UnknownCommand(string cmd)
    {
        Err($"unknown command '{cmd}'. Run 'cgw --help'.");
        return 1;
    }
}
