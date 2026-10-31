using System.Globalization;
using System.Net;

namespace AutoSourcarr.Core.Repair;

/// <summary>Public website identities only. The isolated transport must resolve and pin every connection again.</summary>
public static class WebsiteAddress
{
    public static string PublicUrl(string value) => Normalize(value, allowQuery: false);
    public static string Origin(string value)
    {
        var normalized = PublicUrl(value); var slash = normalized.IndexOf('/', normalized.IndexOf("://", StringComparison.Ordinal) + 3);
        return normalized[..slash];
    }
    public static string Resolve(string current, string relative) => PublicUrl(new Uri(new Uri(current), relative).AbsoluteUri);
    public static Uri ObservationUrl(string value) => new(Normalize(value, allowQuery: true), UriKind.Absolute);
    private static string Normalize(string value, bool allowQuery)
    {
        if (string.IsNullOrEmpty(value) || SourceManifest.CodePoints(value) > 2048 || value.Any(character => char.IsWhiteSpace(character) || character < 32 || character == 127) || value.Contains('\\'))
            throw new InvalidDataException("Invalid public website address");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") || parsed.Host.Length == 0 || parsed.UserInfo.Length != 0 ||
            parsed.Fragment.Length != 0 || !allowQuery && parsed.Query.Length != 0)
            throw new InvalidDataException("Website addresses cannot contain credentials, query values or fragments");
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        if (authorityStart < 3) throw new InvalidDataException("Invalid public website address");
        var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart); if (authorityEnd < 0) authorityEnd = value.Length;
        if (value.AsSpan(authorityStart, authorityEnd - authorityStart).Contains('@')) throw new InvalidDataException("Website credentials are not allowed");
        var host = parsed.IdnHost.Trim('[', ']').ToLowerInvariant().TrimEnd('.');
        if (!IPAddress.TryParse(host, out _)) host = new IdnMapping().GetAscii(host);
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal))
            throw new InvalidDataException("Local website addresses are not allowed");
        if (IPAddress.TryParse(host, out var address) && !RawCodeRules.IsGlobalAddress(address)) throw new InvalidDataException("Private/reserved website addresses are not allowed");
        var port = parsed.Port == 0 ? parsed.Scheme == "https" ? 443 : 80 : parsed.Port;
        if (port is not (80 or 443)) throw new InvalidDataException("Only standard public HTTP/HTTPS ports are allowed");
        var authority = host.Contains(':') ? "[" + host + "]" : host;
        if (port != (parsed.Scheme == "https" ? 443 : 80)) authority += ":" + port.ToString(CultureInfo.InvariantCulture);
        var path = RawCodeRules.OriginalUrlPath(value); if (path.Length == 0) path = "/";
        return parsed.Scheme + "://" + authority + path + (allowQuery ? parsed.Query : "");
    }
}
