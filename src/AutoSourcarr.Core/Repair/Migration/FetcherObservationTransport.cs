using System.Text;

namespace AutoSourcarr.Core.Repair.Migration;

/// <summary>
/// A website observation transport on top of <see cref="IPublicSourceFetcher"/> (public HTTPS on port 443, DNS pinned
/// to public addresses at connect time, no cookies or credentials, identity encoding). The fetcher follows redirects
/// itself, so a redirect is reported the way <see cref="WebsiteProbe"/> expects it: when the fetch ended on another
/// URL, the answer is a 301 with that URL as Location (no body), and the probe then requests the target, which the
/// fetcher answers directly. That is enough for an old domain that 301-redirects to a new one to be detected.
/// The fetcher does not return a content type, so HTML and JSON are recognised from the body.
/// RequireIsolation is a no-op: the fetcher is the controller's public-only reader, not the repair-runner sandbox.
/// </summary>
public sealed class FetcherObservationTransport(IPublicSourceFetcher fetcher) : IWebsiteObservationTransport
{
    /// <summary>Bodies up to this size are read; the observation keeps the first <see cref="WebsiteObservationLimits.MaximumBody"/> bytes.</summary>
    public const int MaximumFetch = 4 * 1024 * 1024;

    public void RequireIsolation() { }

    public async Task<WebsiteTransportResponse> ObserveAsync(WebsiteObservationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timeout = request.Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : request.Timeout > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : request.Timeout;
        PublicSourceResponse response;
        try { response = await fetcher.FetchAsync(new(request.Url, MaximumFetch, timeout, request.Accept), cancellationToken).ConfigureAwait(false); }
        catch (PublicSourceFetchException error) { return new(error.StatusCode, "", "", "", ReadOnlyMemory<byte>.Empty); }
        if (!SameUrl(response.ResolvedUrl, request.Url))
            return new(301, response.ResolvedUrl.AbsoluteUri, "", "", ReadOnlyMemory<byte>.Empty);
        var limit = Math.Min(request.MaximumBytes, WebsiteObservationLimits.MaximumBody);
        var body = response.Body.Length > limit ? response.Body.AsMemory(0, limit) : response.Body.AsMemory();
        return new(200, "", Sniff(body.Span), "", body);
    }

    private static bool SameUrl(Uri first, Uri second) =>
        string.Equals(first.GetLeftPart(UriPartial.Query), second.GetLeftPart(UriPartial.Query), StringComparison.OrdinalIgnoreCase);

    internal static string Sniff(ReadOnlySpan<byte> body)
    {
        var head = Encoding.UTF8.GetString(body[..Math.Min(body.Length, 1024)]).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (head.StartsWith('{') || head.StartsWith('[')) return "application/json";
        var lower = head.ToLowerInvariant();
        if (lower.StartsWith("<!doctype html", StringComparison.Ordinal) || lower.Contains("<html", StringComparison.Ordinal)
            || lower.Contains("<head", StringComparison.Ordinal) || lower.Contains("<body", StringComparison.Ordinal)) return "text/html";
        return "application/octet-stream";
    }
}
