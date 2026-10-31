using Microsoft.AspNetCore.Http;

namespace AutoSourcarr.Api.Http;

/// <summary>
/// Refuses cross-site browser writes before any admin or catalogue route runs (0.4.4 fix #1).
/// Browsers always send Origin on writes and Sec-Fetch-Site on modern engines; scripts, the
/// extension host and internal tools send neither and are unaffected. The Host/X-Forwarded-Host
/// comparison is the same rule the Python app's request_origin.py applies behind reverse proxies.
/// </summary>
public static class CrossSiteWritePolicy
{
    public const string Detail = "Open this action from AutoSourcarr itself";
    private static readonly string[] ProtectedPrefixes = ["/api/admin/", "/api/v1/"];

    public static bool IsCrossSiteWrite(string method, string path, IHeaderDictionary headers) =>
        IsCrossSiteWrite(method, path, headers["Sec-Fetch-Site"].ToString(), headers.Origin.ToString(),
            headers.Host.ToString(), headers["X-Forwarded-Host"].ToString());

    /// <summary>Pure rule, so the policy can be checked without an HTTP context.</summary>
    public static bool IsCrossSiteWrite(string method, string path, string secFetchSite, string origin, string host, string forwardedHost)
    {
        if (method is "GET" or "HEAD" or "OPTIONS" || !ProtectedPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
            return false;
        if (string.Equals(secFetchSite.Trim(), "cross-site", StringComparison.OrdinalIgnoreCase))
            return true;
        origin = origin.Trim();
        if (origin.Length == 0)
            return false;
        if (string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) || parsed.Authority.Length == 0)
            return true;
        var authority = parsed.IsDefaultPort && !origin.Contains(parsed.Host + ":" + parsed.Port, StringComparison.OrdinalIgnoreCase)
            ? parsed.Host.ToLowerInvariant()
            : parsed.Authority.ToLowerInvariant();
        var forwarded = forwardedHost.Split(',')[0].Trim().ToLowerInvariant();
        var hosts = new HashSet<string>(StringComparer.Ordinal) { host.Trim().ToLowerInvariant(), forwarded };
        hosts.Remove("");
        return !hosts.Contains(authority);
    }
}
