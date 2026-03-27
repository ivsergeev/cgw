using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CorpGatewayCli;

public class GatewayClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly GatewayConfig _cfg;

    private static readonly JsonSerializerOptions _pretty = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions _compact = new() { WriteIndented = false };

    public GatewayClient(GatewayConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { BaseAddress = new Uri(cfg.BaseUrl) };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {cfg.ApiToken}");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    // GET /health
    public async Task<JsonNode?> HealthAsync()
    {
        var resp = await _http.GetAsync("/health");
        return await ParseJsonAsync(resp);
    }

    // GET /skills  → compact text block
    public async Task<string> ListCompactAsync()
    {
        var resp = await _http.GetAsync("/skills");
        resp.EnsureSuccessStatusCode();
        var json = await ParseJsonAsync(resp);
        return json?["skills"]?.GetValue<string>() ?? "";
    }

    // GET /skills/{name}/schema  → full JSON
    public async Task<JsonNode?> SchemaAsync(string skillName)
    {
        var resp = await _http.GetAsync($"/skills/{Uri.EscapeDataString(skillName)}/schema");
        return await ParseJsonAsync(resp);
    }

    // POST /invoke  → JSON response from corporate endpoint
    public async Task<(int StatusCode, JsonNode? Body)> InvokeAsync(
        string skillName,
        Dictionary<string, string> parameters)
    {
        var payload = new { skill = skillName, parameters };
        var content = new StringContent(
            JsonSerializer.Serialize(payload, _compact),
            Encoding.UTF8,
            "application/json");

        var resp = await _http.PostAsync("/invoke", content);
        var body = await ParseJsonAsync(resp);
        return ((int)resp.StatusCode, body);
    }

    private static async Task<JsonNode?> ParseJsonAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch { return JsonValue.Create(text); }
    }

    public void Dispose() => _http.Dispose();
}
