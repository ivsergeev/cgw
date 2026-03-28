using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CorpGateway.Services;

/// <summary>
/// Chrome DevTools Protocol service.
/// Executes HTTP requests inside the browser via fetch() — the browser handles all auth.
/// Maintains a pool of persistent background tabs (one per origin).
/// Intercepts Authorization headers from the SPA's own requests via monkey-patching.
/// </summary>
public class ChromeCdpService
{
    private ClientWebSocket? _ws;
    private int _nextId;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, OriginSession> _originPool = new();
    private readonly SemaphoreSlim _poolLock = new(1, 1);
    private CancellationTokenSource? _receiveCts;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public bool IsConnected { get; private set; }
    public int ConnectedPort { get; private set; }
    public event Action<string>? ConnectionLost;

    // JS to install on each origin tab — intercepts Authorization from all fetch/XHR calls
    private const string MonkeyPatchJs = """
        (function() {
            if (window._cgwPatched) return 'already_patched';
            window._cgwPatched = true;
            window._cgwAuthHeader = null;

            // Patch fetch
            const _origFetch = window.fetch;
            window.fetch = function(...args) {
                try {
                    const init = args[1] || {};
                    const h = init.headers;
                    if (h) {
                        let auth = null;
                        if (h instanceof Headers) { auth = h.get('Authorization'); }
                        else if (typeof h === 'object') { auth = h['Authorization'] || h['authorization']; }
                        if (auth) window._cgwAuthHeader = auth;
                    }
                } catch(e) {}
                return _origFetch.apply(this, args);
            };

            // Patch XMLHttpRequest
            const _origOpen = XMLHttpRequest.prototype.open;
            const _origSetHeader = XMLHttpRequest.prototype.setRequestHeader;
            XMLHttpRequest.prototype.open = function(...args) {
                this._cgwHeaders = {};
                return _origOpen.apply(this, args);
            };
            XMLHttpRequest.prototype.setRequestHeader = function(name, value) {
                if (name.toLowerCase() === 'authorization') {
                    window._cgwAuthHeader = value;
                }
                return _origSetHeader.apply(this, arguments);
            };

            return 'patched';
        })()
        """;

    public async Task<bool> ConnectAsync(int port)
    {
        try
        {
            var versionJson = await _http.GetStringAsync($"http://localhost:{port}/json/version");
            var version = JsonSerializer.Deserialize<JsonElement>(versionJson);
            var wsUrl = version.GetProperty("webSocketDebuggerUrl").GetString();
            if (string.IsNullOrEmpty(wsUrl))
                return false;

            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

            IsConnected = true;
            ConnectedPort = port;

            _receiveCts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));

            return true;
        }
        catch
        {
            IsConnected = false;
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        _receiveCts?.Cancel();

        // Snapshot keys to avoid iterating while ReceiveLoop modifies the dictionary
        var sessions = _originPool.Values.ToArray();
        _originPool.Clear();

        foreach (var session in sessions)
        {
            try { await SendCommandAsync("Target.closeTarget", new { targetId = session.TargetId }); }
            catch { }
        }

        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { }
        }
        _ws?.Dispose();
        _ws = null;
        IsConnected = false;
        ConnectedPort = 0;

        // Cancel all pending requests
        foreach (var kv in _pending)
        {
            kv.Value.TrySetCanceled();
            _pending.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>
    /// Execute a fetch() inside the browser with automatic auth handling.
    /// </summary>
    public async Task<CdpFetchResult> ExecuteFetchAsync(string url, string method, string? bodyJson,
        Dictionary<string, string>? headers, int timeoutSeconds = 30, string? originOverride = null)
    {
        if (!IsConnected || _ws == null)
            return new CdpFetchResult { Error = "CDP not connected" };

        try
        {
            var uri = new Uri(url);
            var origin = !string.IsNullOrEmpty(originOverride)
                ? originOverride
                : $"{uri.Scheme}://{uri.Authority}";

            var session = await GetOrCreateOriginSessionAsync(origin);
            if (session == null)
                return new CdpFetchResult { Error = "Failed to create browser session (SSO timeout?)" };

            // Execute fetch with captured auth header
            var result = await DoFetchAsync(session.SessionId, url, method, bodyJson, headers);

            // On 401 — token expired. Reload page to trigger SPA token refresh, then retry.
            if (result.StatusCode == 401)
            {
                var refreshed = await RefreshSessionAuthAsync(origin, session);
                if (refreshed)
                {
                    // Session might have been replaced
                    if (_originPool.TryGetValue(origin, out var newSession))
                        return await DoFetchAsync(newSession.SessionId, url, method, bodyJson, headers);
                }
            }

            // On exception/session error — evict and retry once
            if (result.Error != null)
            {
                if (_originPool.TryRemove(origin, out var dead))
                {
                    try { await SendCommandAsync("Target.closeTarget", new { targetId = dead.TargetId }); }
                    catch { }
                }

                var newSession = await GetOrCreateOriginSessionAsync(origin);
                if (newSession != null)
                    return await DoFetchAsync(newSession.SessionId, url, method, bodyJson, headers);
            }

            return result;
        }
        catch (Exception ex)
        {
            return new CdpFetchResult { Error = ex.Message };
        }
    }

    private async Task<CdpFetchResult> DoFetchAsync(string sessionId, string url, string method,
        string? bodyJson, Dictionary<string, string>? headers)
    {
        try
        {
            // Re-install monkey-patch in case page navigated (SPA redirect, etc.)
            await SendSessionCommandAsync(sessionId, "Runtime.evaluate", new
            {
                expression = MonkeyPatchJs
            });

            // If no auth header captured yet, give SPA a moment to make a request
            var authCheck = await SendSessionCommandAsync(sessionId, "Runtime.evaluate", new
            {
                expression = "window._cgwAuthHeader"
            });
            var hasAuth = authCheck.TryGetProperty("result", out var ar) &&
                          ar.TryGetProperty("value", out var av) &&
                          av.ValueKind == JsonValueKind.String &&
                          !string.IsNullOrEmpty(av.GetString());
            if (!hasAuth)
            {
                // Wait briefly for SPA to fire a request with Authorization
                await Task.Delay(2000);
            }

            var fetchJs = BuildFetchJs(url, method, bodyJson, headers);

            var evalResult = await SendSessionCommandAsync(sessionId, "Runtime.evaluate", new
            {
                expression = fetchJs,
                awaitPromise = true,
                returnByValue = true
            });

            if (evalResult.TryGetProperty("exceptionDetails", out var exception))
            {
                var errorMsg = "fetch() error";
                if (exception.TryGetProperty("exception", out var exc) &&
                    exc.TryGetProperty("description", out var desc))
                    errorMsg = desc.GetString() ?? errorMsg;
                return new CdpFetchResult { Error = errorMsg };
            }

            if (evalResult.TryGetProperty("result", out var result) &&
                result.TryGetProperty("value", out var value))
            {
                return new CdpFetchResult
                {
                    StatusCode = value.TryGetProperty("status", out var st) ? st.GetInt32() : 0,
                    Body = value.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                    ContentType = value.TryGetProperty("contentType", out var ct) ? ct.GetString() ?? "" : ""
                };
            }

            return new CdpFetchResult { Error = "Unexpected CDP response format" };
        }
        catch (Exception ex)
        {
            return new CdpFetchResult { Error = ex.Message };
        }
    }

    /// <summary>
    /// Reload the page to trigger SPA token refresh, wait for a new auth header.
    /// </summary>
    private async Task<bool> RefreshSessionAuthAsync(string origin, OriginSession session)
    {
        try
        {
            // Clear captured auth header
            await SendSessionCommandAsync(session.SessionId, "Runtime.evaluate", new
            {
                expression = "window._cgwAuthHeader = null"
            });

            // Reload the page
            await SendSessionCommandAsync(session.SessionId, "Page.reload");

            // Wait for the page to land back on origin and get a new auth header
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(800, cts.Token).ContinueWith(_ => { });

                try
                {
                    // Re-install monkey-patch (reload clears it)
                    await SendSessionCommandAsync(session.SessionId, "Runtime.evaluate", new
                    {
                        expression = MonkeyPatchJs
                    });

                    // Check if new auth header captured
                    var check = await SendSessionCommandAsync(session.SessionId, "Runtime.evaluate", new
                    {
                        expression = "window._cgwAuthHeader"
                    });

                    if (check.TryGetProperty("result", out var r) &&
                        r.TryGetProperty("value", out var v) &&
                        v.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(v.GetString()))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Page might be mid-navigation (SSO redirect), session could break.
                    // If session died, evict and recreate.
                    _originPool.TryRemove(origin, out _);
                    try { await SendCommandAsync("Target.closeTarget", new { targetId = session.TargetId }); }
                    catch { }

                    var newSession = await GetOrCreateOriginSessionAsync(origin);
                    return newSession != null;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task<OriginSession?> GetOrCreateOriginSessionAsync(string origin)
    {
        if (_originPool.TryGetValue(origin, out var existing))
            return existing;

        await _poolLock.WaitAsync();
        try
        {
            if (_originPool.TryGetValue(origin, out existing))
                return existing;

            // Step 1: Create background tab, DO NOT attach.
            // Let browser handle SSO redirects freely.
            var createResult = await SendCommandAsync("Target.createTarget", new
            {
                url = origin + "/",
                background = true
            });

            var targetId = createResult.GetProperty("targetId").GetString();
            if (string.IsNullOrEmpty(targetId))
                return null;

            // Step 2: Poll Target.getTargets until tab URL is on our origin.
            using var pollCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var onOrigin = false;

            while (!pollCts.Token.IsCancellationRequested)
            {
                await Task.Delay(800, pollCts.Token).ContinueWith(_ => { });

                try
                {
                    var targets = await SendCommandAsync("Target.getTargets");
                    if (targets.TryGetProperty("targetInfos", out var infos))
                    {
                        foreach (var info in infos.EnumerateArray())
                        {
                            var tid = info.GetProperty("targetId").GetString();
                            if (tid != targetId) continue;

                            var tabUrl = info.GetProperty("url").GetString() ?? "";
                            if (string.IsNullOrEmpty(tabUrl) || tabUrl == "about:blank") continue;

                            try
                            {
                                var tabUri = new Uri(tabUrl);
                                var tabOrigin = $"{tabUri.Scheme}://{tabUri.Authority}";
                                if (tabOrigin.Equals(origin, StringComparison.OrdinalIgnoreCase))
                                    onOrigin = true;
                            }
                            catch { }
                            break;
                        }
                    }
                }
                catch { }

                if (onOrigin) break;
            }

            if (!onOrigin)
            {
                try { await SendCommandAsync("Target.closeTarget", new { targetId }); }
                catch { }
                return null;
            }

            // Step 3: SSO done. Attach.
            var attach = await SendCommandAsync("Target.attachToTarget", new
            {
                targetId,
                flatten = true
            });

            var sessionId = attach.GetProperty("sessionId").GetString();
            if (string.IsNullOrEmpty(sessionId))
            {
                try { await SendCommandAsync("Target.closeTarget", new { targetId }); }
                catch { }
                return null;
            }

            // Step 4: Install monkey-patch to intercept Authorization headers from SPA.
            await SendSessionCommandAsync(sessionId, "Runtime.evaluate", new
            {
                expression = MonkeyPatchJs
            });

            // Step 5: Wait for SPA to make a request with Authorization (up to 10s).
            // If SPA doesn't use Authorization headers, we proceed with cookies only.
            using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!authCts.Token.IsCancellationRequested)
            {
                await Task.Delay(500, authCts.Token).ContinueWith(_ => { });

                try
                {
                    var check = await SendSessionCommandAsync(sessionId, "Runtime.evaluate", new
                    {
                        expression = "window._cgwAuthHeader"
                    });

                    if (check.TryGetProperty("result", out var r) &&
                        r.TryGetProperty("value", out var v) &&
                        v.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(v.GetString()))
                    {
                        break; // Got auth header
                    }
                }
                catch { }
            }

            var session = new OriginSession(targetId, sessionId, origin);
            _originPool[origin] = session;
            return session;
        }
        finally
        {
            _poolLock.Release();
        }
    }

    /// <summary>
    /// Build JS that reads the captured auth header and executes fetch() with it.
    /// </summary>
    private static string BuildFetchJs(string url, string method, string? bodyJson,
        Dictionary<string, string>? headers)
    {
        var sb = new StringBuilder();
        sb.Append("(async () => { try { ");

        // Read captured auth header
        sb.Append("const _authHdr = window._cgwAuthHeader; ");
        sb.Append("const _headers = {}; ");

        // Add captured Authorization if available
        sb.Append("if (_authHdr) _headers['Authorization'] = _authHdr; ");

        // Add explicit headers (skill-defined take precedence)
        if (headers != null)
        {
            foreach (var kv in headers)
            {
                sb.Append($"_headers[{JsonSerializer.Serialize(kv.Key)}] = {JsonSerializer.Serialize(kv.Value)}; ");
            }
        }

        // Set Content-Type for requests with body if not explicitly provided by skill headers
        if (!string.IsNullOrEmpty(bodyJson) && method != "GET" && method != "DELETE")
        {
            sb.Append("if (!Object.keys(_headers).some(k => k.toLowerCase() === 'content-type')) ");
            sb.Append("_headers['Content-Type'] = 'application/json'; ");
        }

        // Detect cross-origin: 'include' conflicts with Access-Control-Allow-Origin: *
        // For cross-origin requests, always use 'same-origin' (no cookies sent cross-origin).
        // For same-origin, use 'include' to send cookies when no Authorization header is present.
        sb.Append($"const _isCrossOrigin = new URL({JsonSerializer.Serialize(url)}).origin !== location.origin; ");
        sb.Append("const _creds = _isCrossOrigin ? 'same-origin' : (_authHdr ? 'same-origin' : 'include'); ");

        sb.Append("const resp = await fetch(");
        sb.Append(JsonSerializer.Serialize(url));
        sb.Append(", { method: ");
        sb.Append(JsonSerializer.Serialize(method));
        sb.Append(", credentials: _creds");
        sb.Append(", headers: _headers");

        if (!string.IsNullOrEmpty(bodyJson) && method != "GET" && method != "DELETE")
        {
            sb.Append(", body: ");
            sb.Append(JsonSerializer.Serialize(bodyJson));
        }

        sb.Append(" }); const text = await resp.text(); ");
        sb.Append("return { status: resp.status, body: text, contentType: resp.headers.get('content-type') || '' }; ");
        sb.Append("} catch(e) { return { status: 0, body: e.message, contentType: 'error' }; } })()");
        return sb.ToString();
    }

    private event Action<JsonElement>? _eventReceived;

    private async Task<JsonElement> SendCommandAsync(string method, object? @params = null)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
            throw new InvalidOperationException("Not connected");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>();
        _pending[id] = tcs;

        var msg = @params != null
            ? JsonSerializer.Serialize(new { id, method, @params })
            : JsonSerializer.Serialize(new { id, method });

        var bytes = Encoding.UTF8.GetBytes(msg);
        await _sendLock.WaitAsync();
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => { tcs.TrySetCanceled(); _pending.TryRemove(id, out _); });

        return await tcs.Task;
    }

    private async Task<JsonElement> SendSessionCommandAsync(string sessionId, string method, object? @params = null)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
            throw new InvalidOperationException("Not connected");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>();
        _pending[id] = tcs;

        string msg;
        if (@params != null)
            msg = JsonSerializer.Serialize(new { id, method, @params, sessionId });
        else
            msg = JsonSerializer.Serialize(new { id, method, sessionId });

        var bytes = Encoding.UTF8.GetBytes(msg);
        await _sendLock.WaitAsync();
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => { tcs.TrySetCanceled(); _pending.TryRemove(id, out _); });

        return await tcs.Task;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024]; // 1 MB buffer for large CDP messages
        var sb = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested && _ws is { State: WebSocketState.Open })
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                var json = JsonSerializer.Deserialize<JsonElement>(sb.ToString());

                if (json.TryGetProperty("id", out var idProp))
                {
                    var id = idProp.GetInt32();
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        if (json.TryGetProperty("result", out var res))
                            tcs.TrySetResult(res);
                        else if (json.TryGetProperty("error", out var err))
                            tcs.TrySetException(new Exception(err.ToString()));
                        else
                            tcs.TrySetResult(json);
                    }
                }
                else
                {
                    var eventMethod = "";
                    if (json.TryGetProperty("method", out var m))
                        eventMethod = m.GetString() ?? "";

                    // Detect tab closed externally
                    if (eventMethod == "Target.targetDestroyed" &&
                        json.TryGetProperty("params", out var p) &&
                        p.TryGetProperty("targetId", out var tid))
                    {
                        var destroyedId = tid.GetString();
                        foreach (var kv in _originPool)
                        {
                            if (kv.Value.TargetId == destroyedId)
                            {
                                _originPool.TryRemove(kv.Key, out _);
                                break;
                            }
                        }
                    }

                    _eventReceived?.Invoke(json);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            var wasConnected = IsConnected;
            IsConnected = false;
            ConnectedPort = 0;
            _originPool.Clear();
            if (wasConnected)
                ConnectionLost?.Invoke("WebSocket connection closed");

            foreach (var kv in _pending)
            {
                kv.Value.TrySetCanceled();
                _pending.TryRemove(kv.Key, out _);
            }
        }
    }
}

public record OriginSession(string TargetId, string SessionId, string Origin);

public class CdpFetchResult
{
    public int StatusCode { get; set; }
    public string Body { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string? Error { get; set; }
    public bool IsSuccess => Error == null && StatusCode >= 200 && StatusCode < 300;
}
