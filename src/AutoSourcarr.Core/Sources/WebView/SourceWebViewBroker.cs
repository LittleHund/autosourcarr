using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Sources.WebView;

/// <summary>Where the source host listens and the per-run token it wants as X-Source-Host-Token.</summary>
public sealed record SourceHostEndpoint(Uri BaseUrl, string Token);

/// <summary>Bounds of interactive WebView sessions (SRC-5): one per administrator, idle 5 minutes, 15 minutes in all, about 5 frames a second.</summary>
public sealed record SourceWebViewOptions
{
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    /// <summary>How long the page has to connect with its one-time ticket after the session was opened.</summary>
    public TimeSpan TicketLifetime { get; init; } = TimeSpan.FromMinutes(1);
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 800;
}

/// <summary>An open interactive WebView: the host's session id and who opened it.</summary>
public sealed class SourceWebViewSession
{
    internal SourceWebViewSession(string id, string owner, Uri? start, DateTimeOffset created, byte[] ticketHash, DateTimeOffset ticketExpires)
    {
        Id = id; Owner = owner; Start = start; Created = created; TicketHash = ticketHash; TicketExpires = ticketExpires; LastActivity = created;
    }

    public string Id { get; }
    public string Owner { get; }
    /// <summary>The first address, or null for an empty page where the administrator types one.</summary>
    public Uri? Start { get; }
    public DateTimeOffset Created { get; }
    public DateTimeOffset LastActivity { get; internal set; }
    internal byte[] TicketHash { get; }
    internal DateTimeOffset TicketExpires { get; }
    internal int Claimed;
    internal CancellationTokenSource Closing { get; } = new();
}

/// <summary>A refusal the routes report with its status code and a plain message.</summary>
public sealed class SourceWebViewException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// SRC-5 "Open in WebView" between the browser and the source host. The host renders the page off screen in its
/// Chromium and offers a framed stream (one type byte, a 4-byte big-endian length, the payload: 'F' JPEG frame,
/// 'E' JSON event, 'H' heartbeat) plus an input route; the broker relays that stream to the administrator's
/// WebSocket (frames as binary messages, events as text) and checks and forwards the input the page sends back.
/// Cookies the page gets land in the host cookie jar (that is how a solved image CAPTCHA unblocks a source).
/// One session per administrator: opening another closes the first. The page connects once, with a one-time
/// ticket (the browser cannot send the admin token on a WebSocket).
/// </summary>
public sealed class SourceWebViewBroker : IAsyncDisposable
{
    public const string SubProtocol = "autosourcarr.webview";
    public const string TicketPrefix = "ticket.";
    private const int MaxHostMessage = 8 * 1024 * 1024;
    private const int MaxInputMessage = 16 * 1024;
    private static readonly HashSet<string> InputTypes = new(StringComparer.Ordinal)
        { "click", "mousedown", "mouseup", "mousemove", "wheel", "key", "keydown", "keyup", "text", "navigate", "back", "forward", "reload", "resize", "sync" };

    private readonly Func<SourceHostEndpoint?> _host;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, SourceWebViewSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _openGate = new(1, 1);

    public SourceWebViewBroker(Func<SourceHostEndpoint?> host, HttpMessageHandler? handler = null, SourceWebViewOptions? options = null, TimeProvider? time = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan; // every call carries its own deadline
        Options = options ?? new SourceWebViewOptions();
        _time = time ?? TimeProvider.System;
    }

    public SourceWebViewOptions Options { get; }

    public int Count => _sessions.Count;

    /// <summary>The source host's port when no running host is known (ApplicationRuntime starts it on 4567).</summary>
    public const int DefaultHostPort = 4567;

    /// <summary>Cloud metadata services: never opened (they hand out instance credentials).</summary>
    private static readonly HashSet<string> MetadataHosts = new(StringComparer.Ordinal)
        { "metadata", "metadata.google.internal", "metadata.goog", "instance-data", "instance-data.ec2.internal", "100.100.100.200", "fd00:ec2::254" };

    /// <summary>
    /// An address the WebView may open: absolute http or https with a host, no user name or password, and not refused
    /// by <see cref="Refusal"/>. Null otherwise.
    /// </summary>
    public static Uri? CheckUrl(string? value, Uri? hostBase = null)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.Host.Length == 0 || url.UserInfo.Length > 0)
            return null;
        return Refusal(url, hostBase) is null ? url : null;
    }

    /// <summary>
    /// Why the WebView must not open <paramref name="url"/>, or null: link-local addresses (169.254.0.0/16, fe80::/10),
    /// cloud metadata hosts and the source host's own port on loopback (<paramref name="hostBase"/>, else
    /// <see cref="DefaultHostPort"/>). Loopback and private (LAN) addresses stay allowed on purpose: the WebView is
    /// admin-only and a source may run on the administrator's network. Names are checked as written, without a DNS
    /// lookup; the source host checks typed addresses (navigate) the same way.
    /// </summary>
    public static string? Refusal(Uri url, Uri? hostBase = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        var host = url.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (MetadataHosts.Contains(host)) return "The WebView does not open cloud metadata addresses.";
        IPAddress? address = null;
        if (url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && IPAddress.TryParse(host, out var parsed))
            address = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        if (address is not null && MetadataHosts.Contains(address.ToString().ToLowerInvariant()))
            return "The WebView does not open cloud metadata addresses.";
        if (address is not null && (address.IsIPv6LinkLocal
            || address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, _, _]))
            return "The WebView does not open link-local addresses (169.254.x.x, fe80::).";
        var loopback = host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)
            || address is not null && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any));
        if (loopback && url.Port == (hostBase?.Port ?? DefaultHostPort)) return "The WebView does not open the source host itself.";
        return null;
    }

    /// <summary>True when <paramref name="url"/> is on the source's own website: the same host, or one is a subdomain of the other.</summary>
    public static bool OnSourceSite(Uri url, Uri sourceBase)
    {
        ArgumentNullException.ThrowIfNull(url); ArgumentNullException.ThrowIfNull(sourceBase);
        static string Bare(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        var a = Bare(url.IdnHost.ToLowerInvariant());
        var b = Bare(sourceBase.IdnHost.ToLowerInvariant());
        return a == b || a.EndsWith("." + b, StringComparison.Ordinal) || (b.EndsWith("." + a, StringComparison.Ordinal) && a.Contains('.', StringComparison.Ordinal));
    }

    /// <summary>
    /// Opens a session on the host for <paramref name="owner"/> (closing the owner's earlier one) at <paramref name="url"/>
    /// (null: an empty page) and returns it with its one-time ticket.
    /// </summary>
    public async Task<(SourceWebViewSession Session, string Ticket)> OpenAsync(string owner, Uri? url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (url is not null && CheckUrl(url.AbsoluteUri, _host()?.BaseUrl) is null)
            throw new SourceWebViewException(400, Refusal(url, _host()?.BaseUrl) ?? "The WebView opens http and https addresses only.");
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The owner's earlier session, and sessions whose page never connected in time.
            var start = _time.GetUtcNow();
            foreach (var earlier in _sessions.Values.Where(session => session.Owner == owner
                         || Volatile.Read(ref session.Claimed) == 0 && start > session.TicketExpires).ToList())
                await CloseAsync(earlier.Id).ConfigureAwait(false);
            var body = new JsonObject { ["width"] = Options.Width, ["height"] = Options.Height };
            if (url is not null) body["url"] = url.AbsoluteUri;
            var answer = await HostJsonAsync(HttpMethod.Post, "/v1/webview/sessions", body, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            var id = answer?["id"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length is > 0 and <= 64 && text.All(char.IsAsciiLetterOrDigit)
                ? text : throw new SourceWebViewException(502, "The source host did not open a WebView session.");
            var ticket = Base64Url(RandomNumberGenerator.GetBytes(32));
            var now = _time.GetUtcNow();
            var session = new SourceWebViewSession(id, owner, url, now, SHA256.HashData(Encoding.ASCII.GetBytes(ticket)), now + Options.TicketLifetime);
            _sessions[id] = session;
            return (session, ticket);
        }
        finally { _openGate.Release(); }
    }

    /// <summary>The session for a WebSocket: the ticket matches, is unused and not expired. Each ticket works once.</summary>
    public SourceWebViewSession? Claim(string? sessionId, string? ticket)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(ticket) || ticket.Length > 128) return null;
        if (!_sessions.TryGetValue(sessionId, out var session)) return null;
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(ticket));
        if (!CryptographicOperations.FixedTimeEquals(hash, session.TicketHash) || _time.GetUtcNow() > session.TicketExpires) return null;
        return Interlocked.Exchange(ref session.Claimed, 1) == 0 ? session : null;
    }

    /// <summary>The ticket the page offers as a WebSocket subprotocol ("ticket.&lt;value&gt;"), or null.</summary>
    public static string? TicketFrom(IEnumerable<string> protocols)
    {
        ArgumentNullException.ThrowIfNull(protocols);
        var list = protocols.SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToList();
        if (!list.Contains(SubProtocol, StringComparer.Ordinal)) return null;
        return list.FirstOrDefault(value => value.StartsWith(TicketPrefix, StringComparison.Ordinal))?[TicketPrefix.Length..];
    }

    /// <summary>Closes a session here and on the host (the host copies its cookies into the jar first).</summary>
    public async Task CloseAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return;
        try { await session.Closing.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        // Removed from the list above, so this runs once per session; a relay still linked to it is already cancelled.
        session.Closing.Dispose();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await HostJsonAsync(HttpMethod.Delete, "/v1/webview/sessions/" + Uri.EscapeDataString(sessionId), null, TimeSpan.FromSeconds(10), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or SourceWebViewException or JsonException)
        {
            // The host forgets a session on its own after 5 idle minutes; nothing else to do here.
            _ = error;
        }
    }

    /// <summary>
    /// Relays one claimed session until the page closes, the session times out (idle or in all) or the host ends it;
    /// then tells the page why (a "closed" event), closes the WebSocket and the host session. The page's receive is
    /// never cancelled while the socket is healthy (that would abort it): the close handshake ends it, with a 5-second
    /// fallback.
    /// </summary>
    public async Task RelayAsync(SourceWebViewSession session, WebSocket socket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(socket);
        CancellationToken closing;
        try { closing = session.Closing.Token; }
        catch (ObjectDisposedException) { closing = new CancellationToken(canceled: true); } // closed between Claim and here
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closing);
        using var pageStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var sendGate = new SemaphoreSlim(1, 1);
        var ended = 0;
        session.LastActivity = _time.GetUtcNow();
        async Task Send(ReadOnlyMemory<byte> data, WebSocketMessageType type)
        {
            await sendGate.WaitAsync(stop.Token).ConfigureAwait(false);
            try { await socket.SendAsync(data, type, endOfMessage: true, stop.Token).ConfigureAwait(false); }
            finally { sendGate.Release(); }
        }
        Task SendEvent(JsonObject value) => Send(Encoding.UTF8.GetBytes(value.ToJsonString()), WebSocketMessageType.Text);
        async Task EndAsync(string reason)
        {
            if (Interlocked.Exchange(ref ended, 1) != 0) return;
            await stop.CancelAsync().ConfigureAwait(false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var entered = false;
            try
            {
                await sendGate.WaitAsync(deadline.Token).ConfigureAwait(false);
                entered = true;
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(Encoding.UTF8.GetBytes(new JsonObject { ["type"] = "closed", ["reason"] = reason }.ToJsonString()),
                        WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { }
            finally { if (entered) sendGate.Release(); }
            // The page did not answer the close: stop waiting for it.
            try { pageStop.CancelAfter(TimeSpan.FromSeconds(5)); } catch (ObjectDisposedException) { }
        }

        var fromHost = Task.Run(async () =>
        {
            string reason;
            try { reason = await PumpFromHostAsync(session, Send, stop.Token).ConfigureAwait(false) ?? "The source host ended the WebView session."; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { reason = error is SourceWebViewException refused ? refused.Message : "The connection to the source host was lost."; }
            await EndAsync(reason).ConfigureAwait(false);
        }, CancellationToken.None);
        var fromPage = Task.Run(async () =>
        {
            string? reason;
            try { reason = await PumpFromPageAsync(session, socket, SendEvent, pageStop.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { reason = null; }
            await EndAsync(reason ?? "The WebView page was closed.").ConfigureAwait(false);
        }, CancellationToken.None);
        var limits = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var now = _time.GetUtcNow();
                    if (now - session.Created >= Options.MaxLifetime)
                    { await EndAsync("The WebView time limit was reached. Open WebView again to continue.").ConfigureAwait(false); return; }
                    if (now - session.LastActivity >= Options.IdleTimeout)
                    { await EndAsync("The WebView was idle too long and was closed. Open WebView again to continue.").ConfigureAwait(false); return; }
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(10, Options.IdleTimeout.TotalMilliseconds / 4))), _time, stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Ended here already (no-op), or closed from outside: another WebView was opened, or the app is stopping.
                await EndAsync("This WebView was closed because another one was opened or AutoSourcarr is stopping.").ConfigureAwait(false);
            }
        }, CancellationToken.None);
        try { await Task.WhenAll(fromHost, fromPage, limits).ConfigureAwait(false); }
        finally { await CloseAsync(session.Id).ConfigureAwait(false); }
    }

    /// <summary>Host stream to page: frames (binary, at most one per frame interval) and events (text). Returns a close reason or null.</summary>
    private async Task<string?> PumpFromHostAsync(SourceWebViewSession session, Func<ReadOnlyMemory<byte>, WebSocketMessageType, Task> send, CancellationToken cancellationToken)
    {
        var endpoint = _host() ?? throw new SourceWebViewException(503, "The source host is not running.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.BaseUrl, "/v1/webview/sessions/" + Uri.EscapeDataString(session.Id) + "/stream"));
        Authorize(request, endpoint);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return "The source host closed this WebView session (HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ").";
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var header = new byte[5];
            var lastFrame = DateTimeOffset.MinValue;
            while (!cancellationToken.IsCancellationRequested)
            {
                try { await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false); }
                catch (EndOfStreamException) { return null; }
                var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                if (length is < 0 or > MaxHostMessage) return "The source host sent a malformed WebView stream.";
                var payload = new byte[length];
                await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
                switch ((char)header[0])
                {
                    case 'F':
                        // The host caps the rate too; this keeps the page at the configured rate whatever the host does.
                        var wait = lastFrame + Options.FrameInterval - _time.GetUtcNow();
                        if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                        await send(payload, WebSocketMessageType.Binary).ConfigureAwait(false);
                        lastFrame = _time.GetUtcNow();
                        break;
                    case 'E':
                        JsonObject? value;
                        try { value = JsonNode.Parse(payload) as JsonObject; }
                        catch (JsonException) { value = null; }
                        if (value is null) break;
                        // The host's own close (timeout there) becomes the one "closed" event RelayAsync sends.
                        if (value["type"]?.GetValueKind() == JsonValueKind.String && value["type"]!.GetValue<string>() == "closed")
                            return value["reason"]?.GetValueKind() == JsonValueKind.String ? value["reason"]!.GetValue<string>() : null;
                        await send(payload, WebSocketMessageType.Text).ConfigureAwait(false);
                        break;
                }
            }
        }
        return null;
    }

    /// <summary>Page to host: checked input events; a refused one is answered with an error event. Returns a close reason or null.</summary>
    private async Task<string?> PumpFromPageAsync(SourceWebViewSession session, WebSocket socket, Func<JsonObject, Task> sendEvent, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxInputMessage];
        while (!cancellationToken.IsCancellationRequested)
        {
            var count = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (count == buffer.Length) return "The page sent a message larger than 16 KB.";
                result = await socket.ReceiveAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return "The WebView page was closed.";
                count += result.Count;
            } while (!result.EndOfMessage);
            if (result.MessageType != WebSocketMessageType.Text) continue;
            JsonObject? message;
            try { message = JsonNode.Parse(buffer.AsSpan(0, count)) as JsonObject; }
            catch (JsonException) { message = null; }
            if (CheckInput(message, _host()?.BaseUrl) is not { } input)
            {
                await sendEvent(new JsonObject { ["type"] = "error", ["message"] = "That input was not accepted." }).ConfigureAwait(false);
                continue;
            }
            if (input["type"]!.GetValue<string>() == "close") return "The WebView was closed.";
            if (input["type"]!.GetValue<string>() == "ping") { await sendEvent(new JsonObject { ["type"] = "pong" }).ConfigureAwait(false); continue; }
            // Only real input counts as activity: an open but unattended page still closes after the idle time.
            session.LastActivity = _time.GetUtcNow();
            try
            {
                await HostJsonAsync(HttpMethod.Post, "/v1/webview/sessions/" + Uri.EscapeDataString(session.Id) + "/input", input, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch (SourceWebViewException error) when (error.Status is 404 or 410) { return "The source host closed this WebView session."; }
            catch (SourceWebViewException error)
            {
                await sendEvent(new JsonObject { ["type"] = "error", ["message"] = error.Message }).ConfigureAwait(false);
            }
        }
        return null;
    }

    /// <summary>
    /// The input shapes the page may send, rebuilt from checked fields only: click/mousedown/mouseup/mousemove {x,y,button?},
    /// wheel {x,y,deltaY}, key/keydown/keyup {key, ctrl?, shift?, meta?}, text {text}, navigate {url (http/https)},
    /// back, forward, reload, resize {width,height}, sync, ping, close. Null when refused.
    /// </summary>
    public static JsonObject? CheckInput(JsonObject? message, Uri? hostBase = null)
    {
        if (message?["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type)) return null;
        if (type is "ping" or "close") return new JsonObject { ["type"] = type };
        if (!InputTypes.Contains(type)) return null;
        var result = new JsonObject { ["type"] = type };
        static double? Number(JsonObject source, string key) =>
            source[key] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;
        static string? Text(JsonObject source, string key) =>
            source[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        switch (type)
        {
            case "click" or "mousedown" or "mouseup" or "mousemove" or "wheel":
                if (Number(message, "x") is not { } x || Number(message, "y") is not { } y || x < 0 || y < 0 || x > 4096 || y > 4096) return null;
                result["x"] = Math.Round(x); result["y"] = Math.Round(y);
                if (Number(message, "button") is { } button && button is 0 or 1 or 2) result["button"] = (int)button;
                if (type == "wheel")
                {
                    if (Number(message, "deltaY") is not { } delta) return null;
                    result["deltaY"] = Math.Clamp(Math.Round(delta), -2000, 2000);
                }
                break;
            case "key" or "keydown" or "keyup":
                if (Text(message, "key") is not { Length: > 0 and <= 32 } key) return null;
                result["key"] = key;
                foreach (var flag in new[] { "ctrl", "shift", "meta" })
                    if (message[flag] is JsonValue value && value.TryGetValue<bool>(out var on) && on) result[flag] = true;
                break;
            case "text":
                if (Text(message, "text") is not { Length: > 0 and <= 2000 } text) return null;
                result["text"] = text;
                break;
            case "navigate":
                // The administrator typed this address: any http(s) website is allowed, but not link-local, cloud
                // metadata or the source host itself (see Refusal); the host checks it again.
                if (CheckUrl(Text(message, "url"), hostBase) is not { } url) return null;
                result["url"] = url.AbsoluteUri;
                break;
            case "resize":
                if (Number(message, "width") is not { } width || Number(message, "height") is not { } height) return null;
                result["width"] = (int)Math.Clamp(width, 320, 1920); result["height"] = (int)Math.Clamp(height, 240, 1920);
                break;
        }
        return result;
    }

    private async Task<JsonNode?> HostJsonAsync(HttpMethod method, string path, JsonNode? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var endpoint = _host() ?? throw new SourceWebViewException(503, "The source host is not running, so the WebView is not available.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, new Uri(endpoint.BaseUrl, path));
        Authorize(request, endpoint);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
        JsonNode? answer = null;
        try { if (bytes.Length is > 0 and < 1024 * 1024) answer = JsonNode.Parse(bytes); }
        catch (JsonException) { }
        if (!response.IsSuccessStatusCode)
        {
            var detail = answer?["detail"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            var status = (int)response.StatusCode is 400 or 404 or 410 or 503 ? (int)response.StatusCode : 502;
            throw new SourceWebViewException(status, "The source host refused: " + detail);
        }
        return answer;
    }

    private static void Authorize(HttpRequestMessage request, SourceHostEndpoint endpoint)
    {
        if (endpoint.Token.Length > 0) request.Headers.TryAddWithoutValidation("X-Source-Host-Token", endpoint.Token);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList()) await CloseAsync(id).ConfigureAwait(false);
        _http.Dispose();
        _openGate.Dispose();
    }
}
