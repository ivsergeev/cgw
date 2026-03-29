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
///   cgw groups                       List available groups
///   cgw list [--group &lt;id&gt;]          List skills (all or by group)
///   cgw schema &lt;skill&gt;              Parameter schema for a skill
///   cgw invoke &lt;skill&gt; [key=val ...] Invoke a skill
///   cgw health                       Check gateway status
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
                "groups" => await GroupsCmd(client),
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

    // groups command
    static async Task<int> GroupsCmd(GatewayClient client)
    {
        var result = await client.GroupsAsync();
        if (result?["groups"] is JsonArray groups)
        {
            Console.WriteLine("# Available groups");
            foreach (var g in groups)
            {
                var id = g?["id"]?.GetValue<string>() ?? "";
                var name = g?["name"]?.GetValue<string>() ?? "";
                var desc = g?["description"]?.GetValue<string>() ?? "";
                Console.WriteLine(!string.IsNullOrEmpty(desc)
                    ? $"{id}  // {desc}"
                    : $"{id}  // {name}");
            }
        }
        return 0;
    }

    // list command (optional: --group <id>)
    static async Task<int> ListCmd(GatewayClient client, List<string> args)
    {
        string? groupId = null;
        for (int i = 1; i < args.Count; i++)
        {
            if (args[i] == "--group" && i + 1 < args.Count)
            { groupId = args[++i]; break; }
        }
        var compact = await client.ListCompactAsync(groupId);
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
    "cgw_groups": {
      "description": "List available skill groups with descriptions. Call first if many groups.",
      "command": "cgw groups"
    },
    "cgw_list": {
      "description": "List available skills (all or filtered). Optional: --group <id>.",
      "command": "cgw list"
    },
    "cgw_schema": {
      "description": "Parameter schema for a skill. Argument: skill name.",
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
        Console.WriteLine("cgw_groups / cgw_list / cgw_schema / cgw_invoke / cgw_health are registered as tools.");
        return 0;
    }

    // Shared instruction content used by init commands
    static string BuildAgentInstructions(GatewayConfig cfg, string header, string footer) => $$"""
{{header}}

You have access to corporate internal APIs via the `cgw` CLI.

## Commands

```bash
cgw groups                            # list available groups
cgw list                              # list all skills
cgw list --group <id>                 # list skills in a specific group
cgw schema <skill>                    # parameter details for a skill
cgw invoke <skill> key=value ...      # call a skill, returns JSON
cgw health                            # check if gateway is running
```

## Workflow

1. Run `cgw groups` to see available groups, or `cgw list` to see all skills
2. If many groups, use `cgw list --group <id>` to focus on a specific group
3. Run `cgw schema <skill>` if you need parameter details
4. Run `cgw invoke <skill> key=value ...` to call a skill
5. Omit optional parameters; never pass empty strings

## Rules

- Only call skills from `cgw list`. Never guess skill names.
- Always call `cgw invoke` for fresh data; do not cache results.
- Confirm with the user before calling write operations.
- Report errors to the user verbatim.

{{footer}}
""";

    static void PrintHelp() => Console.WriteLine("""
cgw - CorpGateway CLI

Commands:
  cgw groups                      List available groups
  cgw list [--group <id>]         List skills (all or filtered by group)
  cgw schema <skill>              Parameter schema for a skill
  cgw invoke <skill> [k=v ...]    Invoke a skill; returns JSON
  cgw health                      Check gateway status
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
