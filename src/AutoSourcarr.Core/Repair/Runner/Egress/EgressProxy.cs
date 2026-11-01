using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AutoSourcarr.Core.Repair.Runner.Egress;

/// <summary>Test-only seams. Production never resolves through a hook, reaches a non-global
/// address or uses a port other than 80 (plain HTTP) and 443 (CONNECT).</summary>
internal sealed record EgressTestHooks(Func<string, CancellationToken, Task<IPAddress[]>>? Resolve = null,
    bool AllowNonGlobalTargets = false, bool AllowAnyPort = false);

/// <summary>
/// Domain-filtering HTTP proxy for the isolated repair runner, on 127.0.0.1 with an ephemeral port.
/// CONNECT host:443 is tunnelled without inspection; absolute-URI http GET/HEAD/POST is forwarded
/// with forwarding headers removed and the honest User-Agent set. It never adds a Via header.
/// A host is allowed when it equals an allowed entry or is a subdomain of an entry written as
/// "*.example.com". The proxy resolves DNS itself, refuses any answer containing a private,
/// loopback, link-local or other non-global address, and connects only to the pinned answers.
/// </summary>
public sealed class EgressProxy : IAsyncDisposable
{
    public const int MaximumHeadBytes = 16 * 1024, MaximumResponseHeadBytes = 64 * 1024, MaximumHeaders = 100;
    public const int MaximumConnections = 64, MaximumRefusedHosts = 256, MaximumAllowedHosts = 512;
    public const long MaximumRequestBodyBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(10), ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2), ConnectionLifetime = TimeSpan.FromMinutes(30);
    private static readonly IdnMapping Idn = new();
    private static readonly byte[] HeadEnd = "\r\n\r\n"u8.ToArray();
    private static readonly HashSet<string> PlainMethods = new(StringComparer.Ordinal) { "GET", "HEAD", "POST" };
    private static readonly HashSet<string> DroppedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "User-Agent", "Via", "Forwarded", "X-Real-IP", "Connection", "Keep-Alive", "TE", "Trailer", "Upgrade",
        "Transfer-Encoding", "Expect"
    };

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaximumConnections, MaximumConnections);
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    private readonly List<string> _suffixes = [];
    private readonly string _userAgent;
    private readonly Action<string>? _log;
    private readonly EgressTestHooks _hooks;
    private readonly Lock _refusedLock = new();
    private readonly List<string> _refusedHosts = [];
    private Task _acceptLoop = Task.CompletedTask;
    private long _allowed, _refused, _failed;
    private int _disposed;

    private EgressProxy(IReadOnlyCollection<string> allowedHosts, string userAgent, Action<string>? log, EgressTestHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        if (allowedHosts.Count > MaximumAllowedHosts) throw new ArgumentException("Too many allowed egress hosts.", nameof(allowedHosts));
        foreach (var entry in allowedHosts)
        {
            var wildcard = entry?.StartsWith("*.", StringComparison.Ordinal) == true;
            var host = NormalizeHost(wildcard ? entry![2..] : entry ?? "");
            if (host is null || IPAddress.TryParse(host, out _) || wildcard && !host.Contains('.', StringComparison.Ordinal))
                throw new ArgumentException("Invalid allowed egress host: " + Printable(entry ?? ""), nameof(allowedHosts));
            if (wildcard) _suffixes.Add("." + host); else _exact.Add(host);
        }
        if (string.IsNullOrEmpty(userAgent) || userAgent.Length > 256 || userAgent.Any(c => c is < ' ' or > '~'))
            throw new ArgumentException("The egress User-Agent must be 1-256 printable ASCII characters.", nameof(userAgent));
        _userAgent = userAgent;
        _log = log;
        _hooks = hooks;
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    public int Port { get; private set; }
    public long Allowed => Interlocked.Read(ref _allowed);
    public long Refused => Interlocked.Read(ref _refused);
    /// <summary>Allowed requests whose upstream could not be resolved or reached.</summary>
    public long Failed => Interlocked.Read(ref _failed);
    public IReadOnlyList<string> RefusedHosts { get { lock (_refusedLock) return [.. _refusedHosts]; } }

    public static Task<EgressProxy> StartAsync(IReadOnlyCollection<string> allowedHosts, string userAgent, Action<string>? log) =>
        StartAsync(allowedHosts, userAgent, log, new EgressTestHooks());

    internal static Task<EgressProxy> StartAsync(IReadOnlyCollection<string> allowedHosts, string userAgent, Action<string>? log, EgressTestHooks hooks)
    {
        var proxy = new EgressProxy(allowedHosts, userAgent, log, hooks);
        try
        {
            proxy._listener.Start(MaximumConnections);
            proxy.Port = ((IPEndPoint)proxy._listener.LocalEndpoint).Port;
            proxy._acceptLoop = Task.Run(proxy.AcceptLoopAsync);
            return Task.FromResult(proxy);
        }
        catch
        {
            proxy._listener.Stop(); proxy._stop.Dispose(); proxy._slots.Dispose();
            throw;
        }
    }

    internal bool IsAllowedHost(string host)
    {
        var normalized = NormalizeHost(host);
        if (normalized is null || IPAddress.TryParse(normalized, out _)) return false;
        return _exact.Contains(normalized) || _suffixes.Any(suffix => normalized.Length > suffix.Length && normalized.EndsWith(suffix, StringComparison.Ordinal));
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (_stop.IsCancellationRequested) { break; }
            catch (SocketException) { continue; }
            if (!_slots.Wait(0))
            {
                // Over the cap: answer without waiting on the client and close.
                try { client.Client.Blocking = false; client.Client.Send("HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8); }
                catch (SocketException) { }
                client.Dispose();
                continue;
            }
            var task = Task.Run(() => ServeAsync(client));
            _connections.TryAdd(task, 0);
            _ = task.ContinueWith(done => _connections.TryRemove(done, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                lifetime.CancelAfter(ConnectionLifetime);
                client.NoDelay = true;
                await HandleAsync(client.GetStream(), lifetime.Token).ConfigureAwait(false);
                await CloseGracefullyAsync(client, lifetime.Token).ConfigureAwait(false);
            }
        }
        // InvalidOperationException: the peer is already gone when the half-close starts (no stream on a closed socket).
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException
            or InvalidOperationException) { }
        finally { _slots.Release(); }
    }

    /// <summary>Unread request bytes would turn close() into a reset that can destroy the answer
    /// before the client reads it: half-close, then drain briefly.</summary>
    private static async Task CloseGracefullyAsync(TcpClient client, CancellationToken token)
    {
        if (!client.Connected) return;
        client.Client.Shutdown(SocketShutdown.Send);
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(token);
        drain.CancelAfter(TimeSpan.FromSeconds(2));
        var buffer = new byte[8192];
        for (var total = 0; total < 256 * 1024;)
        {
            var read = await client.GetStream().ReadAsync(buffer, drain.Token).ConfigureAwait(false);
            if (read == 0) return;
            total += read;
        }
    }

    private async Task HandleAsync(NetworkStream client, CancellationToken token)
    {
        Head? head;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(HeadTimeout);
            head = await ReadHeadAsync(client, MaximumHeadBytes, timeout.Token).ConfigureAwait(false);
        }
        if (head is null) { await RespondAsync(client, 431, "Request Header Fields Too Large", "request head is incomplete or too large", token).ConfigureAwait(false); return; }
        var request = ParseHead(head.Text, response: false);
        if (request is null) { await RespondAsync(client, 400, "Bad Request", "malformed request", token).ConfigureAwait(false); return; }
        if (request.Start[0] == "CONNECT") await TunnelAsync(client, request, head.Rest, token).ConfigureAwait(false);
        else await ForwardAsync(client, request, head.Rest, token).ConfigureAwait(false);
    }

    private async Task TunnelAsync(NetworkStream client, Message request, ReadOnlyMemory<byte> early, CancellationToken token)
    {
        var target = ParseAuthority(request.Start[1], defaultPort: null);
        if (target is null) { await RespondAsync(client, 400, "Bad Request", "CONNECT needs host:port", token).ConfigureAwait(false); return; }
        using var upstream = await OpenAsync(client, target.Value.Host, target.Value.Port, 443, "CONNECT", token).ConfigureAwait(false);
        if (upstream is null) return;
        await WriteAsync(client, "HTTP/1.1 200 Connection established\r\n\r\n", token).ConfigureAwait(false);
        using var upstreamStream = new NetworkStream(upstream, ownsSocket: false);
        if (!early.IsEmpty) await upstreamStream.WriteAsync(early, token).ConfigureAwait(false);
        await RelayAsync(client, upstreamStream, upstream, token).ConfigureAwait(false);
    }

    private async Task ForwardAsync(NetworkStream client, Message request, ReadOnlyMemory<byte> early, CancellationToken token)
    {
        var method = request.Start[0];
        var target = request.Start[1];
        if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var reason = target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "https needs CONNECT" : "only absolute http:// targets are proxied";
            await RespondAsync(client, 400, "Bad Request", reason, token).ConfigureAwait(false);
            return;
        }
        var rest = target[7..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authorityText = end < 0 ? rest : rest[..end];
        var path = end < 0 ? "/" : rest[end..];
        var fragment = path.IndexOf('#', StringComparison.Ordinal);
        if (fragment >= 0) path = path[..fragment];
        if (path.Length == 0 || path[0] == '?') path = "/" + path;
        var authority = ParseAuthority(authorityText, defaultPort: 80);
        if (authority is null) { await RespondAsync(client, 400, "Bad Request", "invalid target authority", token).ConfigureAwait(false); return; }
        if (!PlainMethods.Contains(method))
        {
            await RefuseAsync(client, authority.Value.Host, authority.Value.Port, "method " + method + " is not proxied", token).ConfigureAwait(false);
            return;
        }
        long length = 0;
        var lengths = request.Headers.Where(header => header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Select(header => header.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (request.Headers.Any(header => header.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)))
        { await RespondAsync(client, 411, "Length Required", "chunked request bodies are not proxied", token).ConfigureAwait(false); return; }
        if (lengths.Length > 1 || lengths.Length == 1 && (!long.TryParse(lengths[0], NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > MaximumRequestBodyBytes))
        { await RespondAsync(client, 400, "Bad Request", "invalid or oversized Content-Length", token).ConfigureAwait(false); return; }

        using var upstream = await OpenAsync(client, authority.Value.Host, authority.Value.Port, 80, method, token).ConfigureAwait(false);
        if (upstream is null) return;
        var hostHeader = authority.Value.Port == 80 ? authority.Value.Host : authority.Value.Host + ":" + authority.Value.Port.ToString(CultureInfo.InvariantCulture);
        var connectionTokens = request.Headers.Where(header => header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        builder.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\nHost: ").Append(hostHeader).Append("\r\nUser-Agent: ").Append(_userAgent).Append("\r\n");
        foreach (var (name, value) in request.Headers)
        {
            if (DroppedHeaders.Contains(name) || connectionTokens.Contains(name) || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            builder.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        if (lengths.Length == 1) builder.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        builder.Append("Connection: close\r\n\r\n");

        using var upstreamStream = new NetworkStream(upstream, ownsSocket: false);
        if (length > 0 && request.Headers.Any(header => header.Name.Equals("Expect", StringComparison.OrdinalIgnoreCase) && header.Value.Equals("100-continue", StringComparison.OrdinalIgnoreCase)))
            await WriteAsync(client, "HTTP/1.1 100 Continue\r\n\r\n", token).ConfigureAwait(false);
        await upstreamStream.WriteAsync(Encoding.Latin1.GetBytes(builder.ToString()), token).ConfigureAwait(false);
        // Exactly the declared body: later pipelined bytes never reach the origin unrewritten.
        var body = early[..(int)Math.Min(early.Length, length)];
        if (!body.IsEmpty) await upstreamStream.WriteAsync(body, token).ConfigureAwait(false);
        await CopyExactlyAsync(client, upstreamStream, length - body.Length, token).ConfigureAwait(false);

        Head? response;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(IdleTimeout);
            response = await ReadHeadAsync(upstreamStream, MaximumResponseHeadBytes, timeout.Token).ConfigureAwait(false);
        }
        var parsed = response is null ? null : ParseHead(response.Text, response: true);
        if (response is null || parsed is null)
        {
            Interlocked.Increment(ref _failed);
            await RespondAsync(client, 502, "Bad Gateway", "invalid upstream response", token).ConfigureAwait(false);
            return;
        }
        var statusEnd = response.Text.IndexOf("\r\n", StringComparison.Ordinal);
        var head = new StringBuilder(statusEnd < 0 ? response.Text : response.Text[..statusEnd]).Append("\r\n");
        foreach (var (name, value) in parsed.Headers)
            if (!name.Equals("Connection", StringComparison.OrdinalIgnoreCase) && !name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase))
                head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");
        await client.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), token).ConfigureAwait(false);
        if (!response.Rest.IsEmpty) await client.WriteAsync(response.Rest, token).ConfigureAwait(false);
        await PumpAsync(upstreamStream, client, token).ConfigureAwait(false);
    }

    /// <summary>Applies policy, resolves and connects. Writes the refusal or failure itself and returns null.</summary>
    private async Task<Socket?> OpenAsync(NetworkStream client, string host, int port, int expectedPort, string method, CancellationToken token)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out _) || host.StartsWith('['))
        { await RefuseAsync(client, host, port, "IP literal targets are not allowed", token).ConfigureAwait(false); return null; }
        var normalized = NormalizeHost(host);
        if (normalized is null || !IsAllowedHost(normalized))
        { await RefuseAsync(client, normalized ?? host, port, "host is not on the egress allow-list", token).ConfigureAwait(false); return null; }
        if (port != expectedPort && !_hooks.AllowAnyPort)
        { await RefuseAsync(client, normalized, port, "port " + port.ToString(CultureInfo.InvariantCulture) + " is not allowed", token).ConfigureAwait(false); return null; }
        IPAddress[] addresses;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ConnectTimeout);
            addresses = _hooks.Resolve is { } resolve ? await resolve(normalized, timeout.Token).ConfigureAwait(false)
                : await Dns.GetHostAddressesAsync(normalized, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException && !token.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failed);
            Log("egress failed " + normalized + ": DNS resolution failed");
            await RespondAsync(client, 502, "Bad Gateway", "could not resolve " + normalized, token).ConfigureAwait(false);
            return null;
        }
        if (addresses.Length == 0 || addresses.Length > 64 || !_hooks.AllowNonGlobalTargets && addresses.Any(address => !RawCodeRules.IsGlobalAddress(address)))
        { await RefuseAsync(client, normalized, port, "DNS answer includes a private or reserved address", token).ConfigureAwait(false); return null; }
        Interlocked.Increment(ref _allowed);
        Log("egress allowed " + method + " " + normalized + ":" + port.ToString(CultureInfo.InvariantCulture));
        foreach (var address in addresses.Take(4))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ConnectTimeout);
                await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token).ConfigureAwait(false);
                return socket;
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException && !token.IsCancellationRequested) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        Interlocked.Increment(ref _failed);
        Log("egress failed " + normalized + ": upstream unreachable");
        await RespondAsync(client, 502, "Bad Gateway", "could not reach " + normalized, token).ConfigureAwait(false);
        return null;
    }

    private async Task RefuseAsync(NetworkStream client, string host, int port, string reason, CancellationToken token)
    {
        var shown = Printable(host);
        Interlocked.Increment(ref _refused);
        lock (_refusedLock)
            if (_refusedHosts.Count < MaximumRefusedHosts && !_refusedHosts.Contains(shown, StringComparer.Ordinal)) _refusedHosts.Add(shown);
        Log("egress refused " + shown + ":" + port.ToString(CultureInfo.InvariantCulture) + " (" + reason + ")");
        await RespondAsync(client, 403, "Forbidden", "AutoSourcarr egress refused " + shown + ": " + reason, token).ConfigureAwait(false);
    }

    private void Log(string message)
    {
        try { _log?.Invoke(message); }
        catch (Exception) { /* A failing log sink must not change the egress decision. */ }
    }

    private static async Task RelayAsync(NetworkStream client, NetworkStream upstream, Socket upstreamSocket, CancellationToken token)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(token);
        var outbound = PumpAsync(client, upstream, both.Token);
        var inbound = PumpAsync(upstream, client, both.Token);
        var first = await Task.WhenAny(outbound, inbound).ConfigureAwait(false);
        try
        {
            if (first == outbound) upstreamSocket.Shutdown(SocketShutdown.Send);
            else client.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        both.CancelAfter(TimeSpan.FromSeconds(30));
        try { await Task.WhenAll(outbound, inbound).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static async Task PumpAsync(Stream source, Stream target, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        while (true)
        {
            idle.CancelAfter(IdleTimeout);
            var read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            if (read == 0) return;
            await target.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false);
        }
    }

    private static async Task CopyExactlyAsync(Stream source, Stream target, long remaining, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        while (remaining > 0)
        {
            idle.CancelAfter(IdleTimeout);
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), idle.Token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The client closed before sending its declared body.");
            await target.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static Task RespondAsync(Stream client, int status, string phrase, string reason, CancellationToken token)
    {
        var body = Encoding.ASCII.GetBytes(Printable(reason) + "\n");
        return WriteAsync(client, "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + phrase + "\r\nContent-Type: text/plain; charset=us-ascii\r\nContent-Length: "
            + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n" + Encoding.ASCII.GetString(body), token);
    }

    private static async Task WriteAsync(Stream stream, string text, CancellationToken token) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), token).ConfigureAwait(false);

    private sealed record Head(string Text, ReadOnlyMemory<byte> Rest);
    private sealed record Message(string[] Start, List<(string Name, string Value)> Headers);

    /// <summary>Reads through the blank line. Returns null on EOF or when the head exceeds the bound.</summary>
    private static async Task<Head?> ReadHeadAsync(Stream stream, int maximum, CancellationToken token)
    {
        var buffer = new byte[maximum + HeadEnd.Length];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0) return null;
            var searchFrom = Math.Max(0, length - (HeadEnd.Length - 1));
            length += read;
            var end = buffer.AsSpan(searchFrom, length - searchFrom).IndexOf(HeadEnd);
            if (end >= 0)
            {
                end += searchFrom;
                if (end > maximum) return null;
                return new Head(Encoding.Latin1.GetString(buffer, 0, end), buffer.AsMemory(end + HeadEnd.Length, length - end - HeadEnd.Length));
            }
        }
        return null;
    }

    private static Message? ParseHead(string text, bool response)
    {
        var lines = text.Split("\r\n");
        if (lines.Length - 1 > MaximumHeaders || lines.Any(line => line.Contains('\r', StringComparison.Ordinal) || line.Contains('\n', StringComparison.Ordinal) || line.Contains('\0', StringComparison.Ordinal)))
            return null;
        var start = lines[0].Split(' ', response ? 3 : 4);
        if (response)
        {
            if (start.Length < 2 || !start[0].StartsWith("HTTP/1.", StringComparison.Ordinal) || start[1].Length != 3 || !start[1].All(char.IsAsciiDigit)) return null;
        }
        else if (start.Length != 3 || start[0].Length == 0 || !start[0].All(char.IsAsciiLetterUpper) || start[2] is not ("HTTP/1.1" or "HTTP/1.0")
                 || start[1].Length == 0 || start[1].Any(c => c is <= ' ' or > '~')) return null;
        var headers = new List<(string, string)>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) return null;
            var name = line[..colon];
            if (!name.All(IsTokenCharacter)) return null; // also rejects obs-fold and space before the colon
            var value = line[(colon + 1)..].Trim(' ', '\t');
            if (value.Any(c => c < ' ' && c != '\t' || c == '\x7f')) return null;
            headers.Add((name, value));
        }
        return new Message(start, headers);
    }

    private static bool IsTokenCharacter(char c) => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal);

    private static (string Host, int Port)? ParseAuthority(string text, int? defaultPort)
    {
        if (text.Length is 0 or > 300 || text.Contains('@', StringComparison.Ordinal)) return null;
        string host; string? portText;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']', StringComparison.Ordinal);
            if (close < 0) return null;
            host = text[..(close + 1)];
            portText = close + 1 < text.Length ? (text[close + 1] == ':' ? text[(close + 2)..] : null) : "";
            if (portText is null) return null;
        }
        else
        {
            var colon = text.LastIndexOf(':');
            host = colon < 0 ? text : text[..colon];
            portText = colon < 0 ? "" : text[(colon + 1)..];
        }
        if (host.Length == 0) return null;
        if (portText.Length == 0)
            return defaultPort is { } fallback ? (host, fallback) : null;
        if (portText.Length > 5 || !portText.All(char.IsAsciiDigit)) return null;
        var port = int.Parse(portText, NumberStyles.None, CultureInfo.InvariantCulture);
        return port is >= 1 and <= 65535 ? (host, port) : null;
    }

    internal static string? NormalizeHost(string value)
    {
        if (value.Length is 0 or > 255) return null;
        var host = value.EndsWith('.') ? value[..^1] : value;
        try { host = Idn.GetAscii(host); }
        catch (ArgumentException) { return null; }
        host = host.ToLowerInvariant();
        if (host.Length is 0 or > 253) return null;
        foreach (var label in host.Split('.'))
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' || !label.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                return null;
        return host;
    }

    private static string Printable(string value)
    {
        var shown = new string(value.Where(c => c is >= ' ' and <= '~').Take(255).ToArray());
        return shown.Length == 0 ? "?" : shown;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { }
        try { await Task.WhenAll(_connections.Keys).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        _stop.Dispose();
        _slots.Dispose();
    }
}
