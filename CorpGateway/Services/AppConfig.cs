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
    public bool CdpAutoConnect { get; set; } = false;

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
