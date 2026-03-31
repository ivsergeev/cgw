using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CorpGatewayCli;

public class GatewayConfig
{
    public int ApiPort { get; set; } = 9876;
    public string ApiToken { get; set; } = "";
    public string McpInstructions { get; set; } = "";

    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CorpGateway", "config.json");

    public static GatewayConfig Load(string? path = null)
    {
        // Priority: --config flag > env vars > default AppData path
        var configPath = path
            ?? Environment.GetEnvironmentVariable("CGW_CONFIG")
            ?? DefaultPath;

        // Env vars override file values
        var portEnv = Environment.GetEnvironmentVariable("CGW_PORT");
        var tokenEnv = Environment.GetEnvironmentVariable("CGW_TOKEN");

        GatewayConfig cfg;

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                cfg = JsonSerializer.Deserialize<GatewayConfig>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new GatewayConfig();
            }
            catch
            {
                cfg = new GatewayConfig();
            }
        }
        else
        {
            cfg = new GatewayConfig();
        }

        if (portEnv != null && int.TryParse(portEnv, out var port))
            cfg.ApiPort = port;

        if (tokenEnv != null)
            cfg.ApiToken = tokenEnv;

        return cfg;
    }

    [JsonIgnore]
    public string BaseUrl => $"http://localhost:{ApiPort}";
}
