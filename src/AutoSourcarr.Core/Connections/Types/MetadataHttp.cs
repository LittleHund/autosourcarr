using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Connections.Types;

/// <summary>
/// The HTTP core of the metadata clients: one rate-limit token per request, Accept/User-Agent/bearer headers, rate-limit
/// headers fed back into the limiter, 429/503 retried after Retry-After while that stays within the configured bound,
/// a bounded JSON body. The handler belongs to the caller; it is never disposed here.
/// </summary>
internal sealed class MetadataHttp : IDisposable
{
    public const int MaxBody = 8 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly MetadataClientOptions _options;
    private readonly object _gate = new();
    private int? _limit, _remaining;

    public MetadataHttp(string provider, HttpMessageHandler handler, MetadataClientOptions options, TokenBucketLimiter limiter)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Provider = provider;
        _options = options;
        Limiter = limiter;
        _client = new HttpClient(handler, disposeHandler: false) { Timeout = options.Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(15) : options.Timeout };
    }

    public string Provider { get; }
    public TokenBucketLimiter Limiter { get; }

    public MetadataRateStatus RateStatus
    {
        get { lock (_gate) return new MetadataRateStatus(_limit, _remaining, Limiter.Available, Limiter.Capacity); }
    }

    /// <summary>Sends the request built by <paramref name="build"/> (rebuilt for a retry) and returns its JSON body;
    /// null for 404 when <paramref name="notFoundIsNull"/>.</summary>
    public async Task<JsonNode?> SendAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken, bool notFoundIsNull = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            await Limiter.WaitAsync(Provider, _options.MaxRateWait, cancellationToken).ConfigureAwait(false);
            using var request = build();
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent.Length > 0 ? _options.UserAgent : MetadataText.DefaultUserAgent);
            if (_options.Token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
            HttpResponseMessage response;
            try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
            catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException(Provider + ": no answer within " + _client.Timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s", error); }
            using (response)
            {
                ObserveRateHeaders(response);
                if (response.StatusCode is HttpStatusCode.TooManyRequests
                    || (response.StatusCode is HttpStatusCode.ServiceUnavailable && response.Headers.RetryAfter is not null))
                {
                    var wait = RetryAfter(response) ?? TimeSpan.FromSeconds(5);
                    Limiter.BlockFor(wait);
                    if (attempt >= _options.MaxRetries || wait > _options.MaxRateWait) throw new MetadataRateLimitedException(Provider, wait);
                    continue; // the limiter now holds every caller (this one included) until Retry-After passes
                }
                if (notFoundIsNull && response.StatusCode is HttpStatusCode.NotFound) return null;
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await ErrorDetailAsync(response, cancellationToken).ConfigureAwait(false);
                    throw new MetadataProviderException(Provider, "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                        + (detail.Length > 0 ? " (" + detail + ")" : ""), response.StatusCode);
                }
                return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxBody) throw new MetadataProviderException(Provider, "response is larger than 8 MiB", response.StatusCode);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBody) throw new MetadataProviderException(Provider, "response is larger than 8 MiB", response.StatusCode);
            }
            try { return JsonNode.Parse(buffer.ToArray()) ?? throw new MetadataProviderException(Provider, "returned an empty JSON body", response.StatusCode); }
            catch (JsonException error) { throw new MetadataProviderException(Provider, "returned a body that is not JSON", response.StatusCode, error); }
        }
    }

    private static async Task<string> ErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            text = MetadataText.Plain(text.Length > 2000 ? text[..2000] : text);
            return text.Length > 200 ? text[..200] : text;
        }
        catch (Exception error) when (error is HttpRequestException or IOException) { return ""; }
    }

    private void ObserveRateHeaders(HttpResponseMessage response)
    {
        var limit = HeaderInt(response, "X-RateLimit-Limit");
        var remaining = HeaderInt(response, "X-RateLimit-Remaining");
        lock (_gate)
        {
            if (limit is not null) _limit = limit;
            if (remaining is not null) _remaining = remaining;
        }
        if (remaining is not null && response.StatusCode is not HttpStatusCode.TooManyRequests)
            Limiter.Observe(remaining, ResetIn(response));
    }

    /// <summary>X-RateLimit-Reset (AniList: unix seconds) or X-RateLimit-Retry-After (MangaDex: unix seconds).</summary>
    private static TimeSpan? ResetIn(HttpResponseMessage response)
    {
        var stamp = HeaderLong(response, "X-RateLimit-Reset") ?? HeaderLong(response, "X-RateLimit-Retry-After");
        if (stamp is not { } seconds || seconds <= 0) return null;
        var delay = DateTimeOffset.FromUnixTimeSeconds(Math.Min(seconds, 253402300799)) - DateTimeOffset.UtcNow;
        return delay > TimeSpan.Zero ? delay : null;
    }

    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } header)
        {
            if (header.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            if (header.Date is { } date) { var delay = date - DateTimeOffset.UtcNow; return delay > TimeSpan.Zero ? delay : TimeSpan.Zero; }
        }
        return ResetIn(response);
    }

    private static int? HeaderInt(HttpResponseMessage response, string name)
        => HeaderLong(response, name) is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;

    private static long? HeaderLong(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
            && long.TryParse(values.FirstOrDefault()?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public void Dispose() => _client.Dispose();
}
