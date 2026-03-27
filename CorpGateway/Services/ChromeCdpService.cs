using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CorpGateway.Models;

namespace CorpGateway.Services;

public class ChromeCdpService
{
    private ClientWebSocket? _ws;
    private int _nextId;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, DomainAuthData> _authCache = new();
    private CancellationTokenSource? _receiveCts;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public bool IsConnected { get; private set; }
    public int ConnectedPort { get; private set; }
    public event Action<string>? ConnectionLost;

    public int AuthTtlSeconds { get; set; } = 30;

    /// <summary>
    /// Connect to Chrome via CDP. Chrome must be running with --remote-debugging-port={port}.
    /// </summary>
    public async Task<bool> ConnectAsync(int port)
    {
        try
        {
            // Discover WebSocket URL
            var versionJson = await _http.GetStringAsync($"http://localhost:{port}/json/version");
            var version = JsonSerializer.Deserialize<JsonElement>(versionJson);
            var wsUrl = version.GetProperty("webSocketDebuggerUrl").GetString();
            if (string.IsNullOrEmpty(wsUrl))
                return false;

            // Connect WebSocket
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

            IsConnected = true;
            ConnectedPort = port;

            // Start receive loop
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
        if (_ws is { State: WebSocketState.Open })
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
        }
        _ws?.Dispose();
        _ws = null;
        IsConnected = false;
        ConnectedPort = 0;
        _authCache.Clear();
    }

    /// <summary>
    /// Get auth data (cookies + captured auth headers) for a domain.
    /// Returns null if not connected.
    /// </summary>
    public async Task<DomainAuthData?> GetAuthForDomainAsync(string domain)
    {
        if (!IsConnected || _ws == null)
            return null;

        // Check cache
        if (_authCache.TryGetValue(domain, out var cached) &&
            (DateTime.UtcNow - cached.FetchedAt).TotalSeconds < AuthTtlSeconds)
        {
            return cached;
        }

        try
        {
            var authData = new DomainAuthData
            {
                Domain = domain,
                FetchedAt = DateTime.UtcNow
            };

            // 1. Get cookies for this domain
            var cookieResult = await SendCommandAsync("Network.getCookies", new
            {
                urls = new[] { $"https://{domain}", $"http://{domain}" }
            });

            if (cookieResult.TryGetProperty("cookies", out var cookiesArr))
            {
                foreach (var cookie in cookiesArr.EnumerateArray())
                {
                    var name = cookie.GetProperty("name").GetString() ?? "";
                    var value = cookie.GetProperty("value").GetString() ?? "";
                    if (!string.IsNullOrEmpty(name))
                        authData.Cookies[name] = value;
                }
            }

            // 2. Try to capture auth headers from a matching tab
            await CaptureAuthHeadersAsync(domain, authData);

            _authCache[domain] = authData;
            return authData;
        }
        catch
        {
            return null;
        }
    }

    private async Task CaptureAuthHeadersAsync(string domain, DomainAuthData authData)
    {
        try
        {
            // Find a target (tab) matching the domain
            var targets = await SendCommandAsync("Target.getTargets");
            string? targetId = null;

            if (targets.TryGetProperty("targetInfos", out var infos))
            {
                foreach (var info in infos.EnumerateArray())
                {
                    var type = info.GetProperty("type").GetString();
                    var url = info.GetProperty("url").GetString() ?? "";
                    if (type == "page" && url.Contains(domain, StringComparison.OrdinalIgnoreCase))
                    {
                        targetId = info.GetProperty("targetId").GetString();
                        break;
                    }
                }
            }

            if (targetId == null)
                return; // No matching tab — cookies-only mode

            // Attach to target
            var attach = await SendCommandAsync("Target.attachToTarget", new
            {
                targetId,
                flatten = true
            });

            var sessionId = attach.GetProperty("sessionId").GetString();
            if (string.IsNullOrEmpty(sessionId))
                return;

            try
            {
                // Enable network on this session
                await SendSessionCommandAsync(sessionId, "Network.enable");

                // Wait for a requestWillBeSent event with auth headers (up to 3 seconds)
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var tcs = new TaskCompletionSource<JsonElement>();

                void Handler(JsonElement evt)
                {
                    if (cts.Token.IsCancellationRequested) return;
                    if (evt.TryGetProperty("method", out var m) &&
                        m.GetString() == "Network.requestWillBeSent" &&
                        evt.TryGetProperty("sessionId", out var sid) &&
                        sid.GetString() == sessionId)
                    {
                        tcs.TrySetResult(evt);
                    }
                }

                _eventReceived += Handler;
                try
                {
                    // Trigger navigation to capture a request
                    await SendSessionCommandAsync(sessionId, "Page.reload");

                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(-1, cts.Token));
                    if (completed == tcs.Task)
                    {
                        var evt = tcs.Task.Result;
                        if (evt.TryGetProperty("params", out var p) &&
                            p.TryGetProperty("request", out var req) &&
                            req.TryGetProperty("headers", out var headers))
                        {
                            foreach (var headerName in new[] { "Authorization", "X-CSRF-Token", "X-Auth-Token" })
                            {
                                if (headers.TryGetProperty(headerName, out var val))
                                {
                                    var v = val.GetString();
                                    if (!string.IsNullOrEmpty(v))
                                        authData.AuthHeaders[headerName] = v;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _eventReceived -= Handler;
                }
            }
            finally
            {
                // Detach from target
                try
                {
                    await SendCommandAsync("Target.detachFromTarget", new { sessionId });
                }
                catch { }
            }
        }
        catch
        {
            // Auth header capture is best-effort; cookies are the primary mechanism
        }
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        cts.Token.Register(() => tcs.TrySetCanceled());

        var result = await tcs.Task;
        return result;
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        cts.Token.Register(() => tcs.TrySetCanceled());

        return await tcs.Task;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
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

                // Route response by ID
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
                    // Event (no id) — dispatch to listeners
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
            if (wasConnected)
                ConnectionLost?.Invoke("WebSocket connection closed");

            // Cancel all pending requests
            foreach (var kv in _pending)
            {
                kv.Value.TrySetCanceled();
                _pending.TryRemove(kv.Key, out _);
            }
        }
    }
}
