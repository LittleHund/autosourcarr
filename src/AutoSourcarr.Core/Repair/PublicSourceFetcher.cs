using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair;

namespace AutoSourcarr.Core.Repair;

public sealed class PublicSourceFetchException(int statusCode) : IOException("Original repository returned HTTP " + statusCode)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Controller-owned HTTPS reads, public DNS pinned at socket connection, without cookies or account headers.</summary>
public sealed class PublicSourceFetcher(Func<HttpMessageHandler>? trustedTestTransport = null) : IPublicSourceFetcher, IAsyncDisposable
{
    private readonly OperationOwner _owner = new();
    private readonly ConcurrentDictionary<HttpClient, byte> _clients = new();
    public async Task<PublicSourceResponse> FetchAsync(PublicSourceRequest request, CancellationToken cancellationToken = default)
    {
        if (request.MaximumBytes is < 1 or > RawCodeRules.MaximumRepositoryZip || request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Accept is not (null or "application/vnd.github.sha")) throw new ArgumentException("Unsupported public-source Accept header", nameof(request));
        using var lease = _owner.Enter(); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken); deadline.CancelAfter(request.Timeout);
        var token = deadline.Token; var uri = request.Url;
        try
        {
            for (var redirect = 0; redirect < 4; redirect++)
            {
                ValidateUrl(uri); token.ThrowIfCancellationRequested();
                using var client = new HttpClient(trustedTestTransport?.Invoke() ?? Handler()) { Timeout = Timeout.InfiniteTimeSpan };
                _clients.TryAdd(client, 0);
                try
                {
                    if (_owner.IsStopping) throw new OperationCanceledException(_owner.CancellationToken);
                    using var message = new HttpRequestMessage(HttpMethod.Get, uri);
                    message.Headers.UserAgent.ParseAdd("AutoSourcarr/0.4.3-source-code");
                    if (request.Accept is not null) message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(request.Accept));
                    message.Headers.AcceptEncoding.ParseAdd("identity");
                    using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                    {
                        if (redirect == 3 || response.Headers.Location is null) throw new InvalidDataException("Original repository redirect limit or location is invalid");
                        uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location); continue;
                    }
                    if (response.StatusCode != HttpStatusCode.OK) throw new PublicSourceFetchException((int)response.StatusCode);
                    if (response.Content.Headers.ContentEncoding.Any(value => value.Length != 0 && !value.Equals("identity", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Original repository returned unsupported compressed content");
                    if (response.Content.Headers.ContentLength > request.MaximumBytes) throw new InvalidDataException("Original repository response exceeds its byte limit");
                    await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    using var output = new MemoryStream(); var buffer = new byte[32768];
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, request.MaximumBytes + 1 - checked((int)output.Length))), token).ConfigureAwait(false);
                        if (count == 0) break; output.Write(buffer, 0, count);
                        if (output.Length > request.MaximumBytes) throw new InvalidDataException("Original repository response exceeds its decoded byte limit");
                    }
                    if (output.Length == 0) throw new InvalidDataException("Original repository returned an empty response");
                    return new(output.ToArray(), uri);
                }
                finally { _clients.TryRemove(client, out _); }
            }
            throw new InvalidDataException("Original repository redirect limit exceeded");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_owner.CancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Original repository read reached its deadline"); }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || _owner.CancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(token); }
        catch (Exception error) when (error is HttpRequestException or SocketException)
        { throw new IOException("Original repository connection failed", error); }
    }
    public Task StopAsync() => _owner.StopAsync(() => { foreach (var client in _clients.Keys) client.Dispose(); });
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); await _owner.DisposeAsync().ConfigureAwait(false); }
    public static void ValidateUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.OriginalString.Contains('\\') || uri.OriginalString.Any(c => c < 33 || c == 127))
            throw new InvalidDataException("Source downloads require public HTTPS without credentials");
        var host = uri.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal) || IPAddress.TryParse(host, out var address) && !RawCodeRules.IsGlobalAddress(address))
            throw new InvalidDataException("Private source download address refused");
    }
    public static void ValidateAddresses(IPAddress[] addresses)
    {
        if (addresses.Length == 0 || addresses.Length > 64 || addresses.Any(address => !RawCodeRules.IsGlobalAddress(address))) throw new InvalidDataException("Repository DNS did not resolve exclusively to public addresses");
    }
    private static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, MaxResponseHeadersLength = 64,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectCallback = async (context, token) =>
        {
            var url = context.InitialRequestMessage.RequestUri ?? throw new InvalidDataException("Missing original repository target"); ValidateUrl(url);
            if (context.DnsEndPoint.Port != 443 || !context.DnsEndPoint.Host.Equals(url.IdnHost, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Repository socket authority changed");
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false); ValidateAddresses(addresses);
            Exception? last = null;
            foreach (var address in addresses)
            {
                token.ThrowIfCancellationRequested(); var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, 443), token).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
                catch (Exception error) when (error is SocketException or OperationCanceledException) { socket.Dispose(); last = error; if (token.IsCancellationRequested) throw; }
            }
            throw new IOException("No public repository address accepted the connection", last);
        },
    };
}
