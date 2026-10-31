using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Repair;

namespace AutoSourcarr.Core.Repair;

/// <summary>Bounded observations through an explicitly supplied isolated transport; creates no host HTTP client.</summary>
public sealed class WebsiteProbe : IWebsiteObservationClient, IDisposable
{
    private readonly IWebsiteObservationTransport _transport;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _deadline, _pace;
    private readonly SemaphoreSlim _responseGate = new(1, 1);
    private TimeSpan? _last;
    private int _requests;
    public int Requests => Volatile.Read(ref _requests);
    public WebsiteProbe(IWebsiteObservationTransport transport, TimeSpan? pace = null, TimeSpan? deadline = null,
        Func<TimeSpan>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        var started = Stopwatch.GetTimestamp(); _clock = clock ?? (() => Stopwatch.GetElapsedTime(started));
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token)); _pace = pace ?? TimeSpan.FromSeconds(3);
        var duration = deadline ?? TimeSpan.FromSeconds(60);
        if (_pace < TimeSpan.Zero || _pace > TimeSpan.FromMinutes(1) || duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(80)) throw new ArgumentOutOfRangeException(nameof(deadline));
        _deadline = _clock() + duration;
    }
    public async Task<WebsiteObservationResponse> ResponseAsync(string url, string? accept = null, CancellationToken cancellationToken = default)
    {
        if (accept is not (null or "application/vnd.github.sha")) throw new ArgumentException("Unsupported public observation response type", nameof(accept));
        var target = WebsiteAddress.ObservationUrl(url);
        await _responseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_requests >= WebsiteObservationLimits.MaximumRequests || _clock() >= _deadline) throw new TimeoutException("Website observation budget reached");
            if (_last is not null)
            {
                var remaining = _last.Value + _pace - _clock(); if (remaining > TimeSpan.Zero) await _delay(remaining, cancellationToken).ConfigureAwait(false);
            }
            var now = _clock(); if (now >= _deadline) throw new TimeoutException("Website observation deadline reached");
            _transport.RequireIsolation(); _requests++; _last = now;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var budget = _deadline - now; if (budget > TimeSpan.FromSeconds(10)) budget = TimeSpan.FromSeconds(10); timeout.CancelAfter(budget);
            WebsiteTransportResponse response;
            try { response = await _transport.ObserveAsync(new(target, WebsiteObservationLimits.MaximumBody, budget, accept), timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Website observation deadline reached"); }
            cancellationToken.ThrowIfCancellationRequested(); if (_clock() >= _deadline) throw new TimeoutException("Website observation deadline reached");
            var kind = response.ContentType.Split(';', 2)[0].ToLowerInvariant().Trim(); var encoding = response.ContentEncoding.ToLowerInvariant().Trim();
            var readable = encoding is "" or "identity";
            if (response.Body.Length > WebsiteObservationLimits.MaximumBody) throw new InvalidDataException("Website response transport exceeded its raw body bound");
            var body = !readable || WebsiteObservationLimits.Redirect(response.StatusCode) ? ReadOnlyMemory<byte>.Empty : response.Body;
            return new(response.StatusCode, response.Location, kind, body, readable);
        }
        finally { _responseGate.Release(); }
    }
    public async Task<JsonObject> OneAsync(string value, string? allowedOrigin = null, CancellationToken cancellationToken = default)
    {
        string requested;
        try { requested = WebsiteAddress.PublicUrl(value); }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException) { return new() { ["state"] = "inconclusive", ["reason"] = "invalid-public-address" }; }
        var current = requested; var chain = new List<string>();
        try
        {
            for (var redirect = 0; redirect < 5; redirect++)
            {
                var response = await ResponseAsync(current, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (WebsiteObservationLimits.Redirect(response.StatusCode))
                {
                    var target = WebsiteAddress.Resolve(current, response.Location);
                    if (allowedOrigin is not null && WebsiteAddress.Origin(target) != allowedOrigin) throw new InvalidDataException("API redirect changed origin");
                    if (response.Location.Length == 0 || target == requested || chain.Contains(target, StringComparer.Ordinal)) throw new InvalidDataException("Redirect loop or missing target");
                    if (new Uri(current).Scheme == "https" && new Uri(target).Scheme != "https") throw new InvalidDataException("HTTPS downgrade is not allowed");
                    chain.Add(target); current = target; continue;
                }
                var text = Encoding.UTF8.GetString(response.Body.Span); var lower = text.ToLowerInvariant(); var status = response.StatusCode; var kind = response.ContentType;
                var challenge = new[] { "cf-chl-", "cloudflare challenge", "checking your browser before accessing" }.Any(word => lower.Contains(word, StringComparison.Ordinal)) ||
                    Regex.IsMatch(lower, @"<title[^>]*>\s*(?:just a moment|attention required|captcha|human verification|verify you are human)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
                    (status is 401 or 403 or 503) && new[] { "g-recaptcha", "hcaptcha", "verify you are human" }.Any(word => lower.Contains(word, StringComparison.Ordinal));
                var state = status == 429 ? "rate_limited" : challenge ? "verification" : status is 404 or 410 ? "not_found" :
                    (status is >= 200 and < 300) && response.Readable && (kind is "text/html" or "application/json" or "application/ld+json") && !response.Body.IsEmpty ? "working" : "inconclusive";
                var result = new JsonObject { ["requested_url"] = requested, ["final_url"] = current, ["redirects"] = WebsiteEvidence.Strings(chain),
                    ["http_status"] = status, ["state"] = state, ["content_kind"] = kind.Contains("json", StringComparison.Ordinal) ? "json" : kind == "text/html" ? "html" : "other" };
                if (!response.Readable) result["reason"] = "encoded-response-not-decoded";
                if (state == "working" && kind == "text/html")
                {
                    var targets = new List<string>();
                    foreach (var candidate in WebsiteMarkup.ApiLinks(text))
                    {
                        try { var target = WebsiteAddress.Resolve(current, candidate); if (WebsiteAddress.Origin(target) == WebsiteAddress.Origin(current) && !targets.Contains(target, StringComparer.Ordinal)) targets.Add(target); }
                        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException) { }
                    }
                    result["_api_targets"] = WebsiteEvidence.Strings(targets.Take(2));
                }
                if (state == "working" && (kind is "application/json" or "application/ld+json"))
                {
                    var valid = false;
                    try { using var json = JsonDocument.Parse(response.Body, new JsonDocumentOptions { MaxDepth = 128 }); valid = json.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array; }
                    catch (JsonException) { }
                    if (!valid) { result["state"] = "inconclusive"; result["reason"] = "invalid-or-truncated-json"; }
                }
                return result;
            }
            throw new InvalidDataException("Redirect limit reached");
        }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or HttpRequestException or TimeoutException)
        { return new() { ["requested_url"] = requested, ["final_url"] = current, ["redirects"] = WebsiteEvidence.Strings(chain), ["state"] = "inconclusive", ["reason"] = "connection-redirect-or-budget" }; }
    }
    public async Task<JsonArray> CheckAsync(IEnumerable<JsonObject> sources, CancellationToken cancellationToken = default)
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var source in sources.Take(200))
        {
            var value = WebsiteEvidence.Text(source, "baseUrl"); if (value.Length == 0) continue; string key;
            try { key = WebsiteAddress.PublicUrl(value); } catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException) { continue; }
            if (!grouped.ContainsKey(key) && grouped.Count >= WebsiteObservationLimits.MaximumSites) continue;
            var id = WebsiteEvidence.SourceId(source); if (!KnownPatterns.SourceId().IsMatch(id)) continue;
            if (!grouped.TryGetValue(key, out var ids)) grouped.Add(key, ids = []); ids.Add(id);
        }
        var result = new JsonArray();
        foreach (var (value, ids) in grouped)
        {
            var item = await OneAsync(value, cancellationToken: cancellationToken).ConfigureAwait(false); item["source_ids"] = WebsiteEvidence.Strings(ids.Take(200));
            var targets = (item["_api_targets"] as JsonArray)?.Select(node => node!.GetValue<string>()).ToArray() ?? [];
            item.Remove("_api_targets"); var apis = new JsonArray(); item["apis"] = apis;
            foreach (var target in targets)
            {
                var final = WebsiteEvidence.Text(item, "final_url"); var expectedOrigin = WebsiteAddress.Origin(final.Length != 0 ? final : value);
                var api = await OneAsync(target, expectedOrigin, cancellationToken).ConfigureAwait(false); api.Remove("_api_targets");
                var apiFinal = WebsiteEvidence.Text(api, "final_url");
                if (apiFinal.Length != 0 && WebsiteAddress.Origin(apiFinal) != expectedOrigin) { api["state"] = "inconclusive"; api["reason"] = "api-origin-changed"; }
                apis.Add(api);
            }
            result.Add(item);
        }
        return result;
    }
    public void Dispose() => _responseGate.Dispose();
}
