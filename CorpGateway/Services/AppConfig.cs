using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace CorpGateway.Services;

public class AppConfig
{
    public int ApiPort { get; set; } = 9876;
    public string ApiToken { get; set; } = Guid.NewGuid().ToString("N");
    public bool StartMinimized { get; set; } = false;
    public bool StartWithSystem { get; set; } = false;
    public string Theme { get; set; } = "System"; // System / Light / Dark

    // CDP (Chrome DevTools Protocol) settings
    public int CdpPort { get; set; } = 9222;
    public bool CdpAutoConnect { get; set; } = true;

    // MCP server instructions (sent to agent on initialize)
    public string McpInstructions { get; set; } = """
        Corporate API gateway for accessing internal corporate systems.

        Workflow:
        1. Call cgw_groups to see available groups
        2. Call cgw_list (optionally with group filter) to see skills
        3. Call cgw_schema if you need parameter details
        4. Call cgw_invoke to execute a skill

        Rules:
        - Only call skills from cgw_list. Never guess skill names.
        - Always call cgw_invoke for fresh data; do not cache results.
        - Confirm with user before calling write operations.
        """;

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CorpGateway", "config.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static async Task<AppConfig> LoadAsync()
    {
        if (!File.Exists(ConfigPath))
        {
            var def = new AppConfig();
            await def.SaveAsync();
            return def;
        }
        var json = await File.ReadAllTextAsync(ConfigPath);
        return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = JsonSerializer.Serialize(this, Options);
        await File.WriteAllTextAsync(ConfigPath, json);
    }
}
