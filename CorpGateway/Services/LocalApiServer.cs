using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CorpGateway.Models;

namespace CorpGateway.Services;

/// <summary>
/// Lightweight HTTP server on localhost that exposes skill execution to the CLI agent.
/// Authentication: static bearer token loaded from config.
/// </summary>
public class LocalApiServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly SkillsRepository _repo;
    private readonly ChromeCdpService? _cdpService;
    private readonly HttpClient _http = new();
    private readonly Dictionary<string, (string Json, DateTime ExpiresAt)> _cache = new();
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

            // GET /skills/{name}/schema  - full schema for one skill
            if (req.HttpMethod == "GET" && path.StartsWith("/skills/") && path.EndsWith("/schema"))
            {
                var skillName = path.Split('/')[2];
                var skill = _repo.GetSkills().FirstOrDefault(s =>
                    s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
                if (skill == null)
                {
                    await WriteJsonAsync(resp, 404, new { error = "Skill not found" });
                    return;
                }
                await WriteJsonAsync(resp, 200, skill);
                return;
            }

            // POST /invoke  - execute a skill
            if (req.HttpMethod == "POST" && path == "/invoke")
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
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
                await WriteJsonAsync(resp, 200, new { status = "ok", skills = _repo.GetSkills().Count });
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
        var skill = _repo.GetSkills().FirstOrDefault(s =>
            s.Name.Equals(req.Skill, StringComparison.OrdinalIgnoreCase));

        if (skill == null)
            return (404, new { error = $"Skill '{req.Skill}' not found" });

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
        var remainingParams = new Dictionary<string, string>(parameters);
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

        // Cache check
        if (skill.CacheEnabled)
        {
            var cacheKey = $"{skill.Id}:{JsonSerializer.Serialize(parameters)}";
            if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
                return (200, JsonSerializer.Deserialize<object>(cached.Json)!);
        }

        // Build HTTP request
        var httpReq = new HttpRequestMessage
        {
            RequestUri = new Uri(url),
            Method = new HttpMethod(skill.HttpMethod)
        };

        if (skill.HttpMethod == "GET" || skill.HttpMethod == "DELETE")
        {
            // GET/DELETE: remaining params go as query string
            if (remainingParams.Count > 0)
            {
                var query = string.Join("&", remainingParams.Select(kv =>
                    $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
                httpReq.RequestUri = new Uri($"{url}?{query}");
            }
        }
        else
        {
            // POST/PUT/PATCH: use body template or flat JSON
            string bodyJson;
            if (!string.IsNullOrWhiteSpace(skill.BodyTemplate))
            {
                // Substitute {{param}} placeholders in template with JSON-escaped values
                bodyJson = skill.BodyTemplate;
                foreach (var kv in remainingParams)
                {
                    var escaped = JsonSerializer.Serialize(kv.Value)[1..^1]; // strip surrounding quotes
                    bodyJson = bodyJson.Replace($"{{{{{kv.Key}}}}}", escaped);
                }
            }
            else
            {
                bodyJson = JsonSerializer.Serialize(remainingParams);
            }
            httpReq.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        }

        foreach (var h in skill.Headers)
            httpReq.Headers.TryAddWithoutValidation(h.Key, h.Value);

        // Merge browser auth (cookies + headers) from CDP if connected
        if (_cdpService?.IsConnected == true && httpReq.RequestUri != null)
        {
            var domain = httpReq.RequestUri.Host;
            var authData = await _cdpService.GetAuthForDomainAsync(domain);
            if (authData != null)
            {
                // Merge cookies
                if (authData.Cookies.Count > 0)
                {
                    var cdpCookies = string.Join("; ",
                        authData.Cookies.Select(c => $"{c.Key}={c.Value}"));
                    if (httpReq.Headers.TryGetValues("Cookie", out var existing))
                    {
                        cdpCookies = string.Join("; ", existing) + "; " + cdpCookies;
                        httpReq.Headers.Remove("Cookie");
                    }
                    httpReq.Headers.TryAddWithoutValidation("Cookie", cdpCookies);
                }

                // Merge auth headers (skill-defined take precedence)
                foreach (var h in authData.AuthHeaders)
                {
                    if (!skill.Headers.ContainsKey(h.Key))
                        httpReq.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }
        }

        var httpResp = await _http.SendAsync(httpReq);
        var respBody = await httpResp.Content.ReadAsStringAsync();

        if (skill.CacheEnabled && httpResp.IsSuccessStatusCode)
        {
            var cacheKey = $"{skill.Id}:{JsonSerializer.Serialize(parameters)}";
            _cache[cacheKey] = (respBody, DateTime.UtcNow.AddSeconds(skill.CacheTtlSeconds));
        }

        var statusCode = (int)httpResp.StatusCode;
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
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.OutputStream.Close();
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}

public class InvokeRequest
{
    public string Skill { get; set; } = "";
    public Dictionary<string, string>? Parameters { get; set; }
}
