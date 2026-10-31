using System.Globalization;
using System.Numerics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Catalogue.Sharing;

public sealed record PortableReference(string Scope, string Provider, string ExternalId, string? Path = null);

/// <summary>ASC Media ID v1. Public anchors never contain local manga or reader numbers.</summary>
public static partial class PortableIdentity
{
    public const int MaximumReferenceLength = 6000;
    public static IReadOnlyDictionary<string, string> Providers { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["anilist"] = "work", ["myanimelist"] = "work", ["kitsu"] = "work", ["mangaupdates"] = "edition", ["mangadex"] = "edition", ["comicvine"] = "edition" };
    private static readonly Regex Sensitive = new("password|passwd|token|secret|authorization|api[_-]?key|session|cookie|signature", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static bool Match(string pattern, string value) => Regex.IsMatch(value, "\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant);
    public static string SourcePath(string value)
    {
        if (string.IsNullOrEmpty(value) || value.EnumerateRunes().Count() > 4096 || value.Any(c => c < 33 || c == 127)) throw new ArgumentException("Invalid public source URL");
        var fragment = value.IndexOf('#');
        if (fragment >= 0)
        {
            if (fragment != value.Length - 1) throw new ArgumentException("Ambiguous source URL");
            value = value[..fragment]; // urlsplit retains an empty fragment without treating it as private data.
        }
        if (value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\')) throw new ArgumentException("Ambiguous source URL");
        var path = value;
        var scheme = Regex.Match(value, "\\A([a-zA-Z][a-zA-Z0-9+.-]*):");
        if (scheme.Success)
        {
            if (scheme.Groups[1].Value.ToLowerInvariant() is not ("http" or "https") || !value[scheme.Length..].StartsWith("//", StringComparison.Ordinal)) throw new ArgumentException("Invalid source URL scheme");
            var begin = scheme.Length + 2;
            var end = value.IndexOfAny(['/', '?'], begin);
            if (end < 0) end = value.Length;
            var authority = value[begin..end];
            if (authority.Length == 0 || authority.Contains('@')) throw new ArgumentException("Ambiguous source URL");
            ValidateAuthority(authority);
            path = value[end..];
        }
        if (!path.StartsWith('/')) throw new ArgumentException("Source identity may contain private values");
        var query = path.IndexOf('?');
        if (query >= 0)
            foreach (var pair in path[(query + 1)..].Split('&'))
            {
                var separator = pair.IndexOf('=');
                if (separator >= 0 && separator < pair.Length - 1 && Sensitive.IsMatch(Uri.UnescapeDataString(pair[..separator].Replace('+', ' ')))) throw new ArgumentException("Source identity may contain private values");
            }
        return path;
    }
    private static void ValidateAuthority(string authority)
    {
        // Retain urlsplit's hostname checks without asking Uri to rewrite paths, escape Unicode,
        // reject an unused nonnumeric port, or otherwise change the stable source anchor.
        var check = authority.Replace(":", "", StringComparison.Ordinal).Replace("@", "", StringComparison.Ordinal).Replace("#", "", StringComparison.Ordinal).Replace("?", "", StringComparison.Ordinal);
        var normalized = check.Normalize(NormalizationForm.FormKC);
        if (normalized != check && normalized.IndexOfAny(['/', '?', '#', '@', ':']) >= 0) throw new ArgumentException("Invalid source URL authority");
        if (authority.Contains('[') || authority.Contains(']'))
        {
            var close = authority.IndexOf(']');
            if (!authority.StartsWith('[') || close <= 1 || close < authority.Length - 1 && authority[close + 1] != ':' || authority[(close + 1)..].Contains(']')) throw new ArgumentException("Invalid source URL authority");
            var host = authority[1..close];
            if (host.StartsWith('v'))
            { if (!Match("v[0-9a-fA-F]+\\..+", host)) throw new ArgumentException("Invalid source URL authority"); }
            else
            {
                var scope = host.IndexOf('%'); var address = scope < 0 ? host : host[..scope];
                if (scope >= 0 && (scope == host.Length - 1 || host[(scope + 1)..].Contains('%')) || !IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetworkV6) throw new ArgumentException("Invalid source URL authority");
            }
        }
        else if (authority.Split(':')[0].Length == 0) throw new ArgumentException("Invalid source URL scheme");
    }
    public static string ExternalId(string provider, string value)
    {
        if (value is null || value.EnumerateRunes().Count() > 100) throw new ArgumentException("Invalid external catalogue ID");
        if (provider == "mangadex")
        {
            if (!Guid.TryParse(value, out var guid) || guid == Guid.Empty) throw new ArgumentException("Empty MangaDex identity");
            return guid.ToString("D");
        }
        if (!Match("[0-9]{1,20}", value) || !BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) throw new ArgumentException("Invalid numeric catalogue ID");
        return number.ToString(CultureInfo.InvariantCulture);
    }
    public static string StableSource(string source, bool canonical = false)
    {
        if (source is null || !Match("-?[0-9]{1,20}", source)) throw new ArgumentException("Invalid stable source ID");
        var value = BigInteger.Parse(source, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        if (value is "0" or "-1" || canonical && source != value) throw new ArgumentException("Local/unknown sources have no shared public identity");
        return value;
    }
    public static string RecordId(string source, string url)
    {
        var sid = StableSource(source);
        var path = SourcePath(url);
        string encoded;
        try { encoded = Convert.ToBase64String(Utf8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        catch (EncoderFallbackException exception) { throw new ArgumentException("Invalid public source URL", exception); }
        var result = $"asc1:record:tachiyomi:{sid}:{encoded}";
        if (result.Length > MaximumReferenceLength) throw new ArgumentException("Source reference exceeds the portable ID limit");
        return result;
    }
    public static PortableReference Parse(string reference)
    {
        if (string.IsNullOrEmpty(reference) || reference.EnumerateRunes().Count() > MaximumReferenceLength) throw new ArgumentException("Enter a complete ASC Media ID");
        var parts = reference.Split(':');
        if (parts.Length == 4 && parts[0] == "asc1" && parts[1] == "entry" && parts[2] == "autosourcarr")
        { CatalogueFormat.CheckUid(reference); return new("entry", "autosourcarr", parts[3]); }
        if (parts.Length == 4 && parts[0] == "asc1" && (Providers.TryGetValue(parts[2], out var scope) && scope == parts[1] || parts[1] == "gallery" && parts[2] is "nhentai" or "ehentai" or "hitomi"))
        {
            if (ExternalId(parts[2], parts[3]) != parts[3]) throw new ArgumentException("Use the canonical catalogue ID");
            return new(parts[1], parts[2], parts[3]);
        }
        if (parts.Length == 5 && parts[0] == "asc1" && parts[1] == "record" && parts[2] == "tachiyomi")
        {
            if (!Match("[A-Za-z0-9_-]+", parts[4])) throw new ArgumentException("Invalid source reference");
            try
            {
                var base64 = parts[4].Replace('-', '+').Replace('_', '/');
                var path = Utf8.GetString(Convert.FromBase64String(base64 + new string('=', (4 - base64.Length % 4) % 4)));
                if (RecordId(parts[3], path) != reference) throw new ArgumentException("Use the canonical source reference");
                return new("record", "tachiyomi", parts[3], path);
            }
            catch (Exception exception) when (exception is FormatException or DecoderFallbackException) { throw new ArgumentException("Invalid source reference", exception); }
        }
        throw new ArgumentException("Unsupported ASC Media ID namespace");
    }
    public static string? GalleryReference(string? sourceUrl, string? realUrl = null, string? baseUrl = null)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in new[] { realUrl, sourceUrl })
        {
            if (string.IsNullOrEmpty(raw)) continue;
            try
            {
                SourcePath(raw);
                Uri? uri = null;
                if ((!Uri.TryCreate(raw, UriKind.Absolute, out uri) || uri.Scheme is not ("http" or "https")) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var parent)) Uri.TryCreate(parent, raw, out uri);
                if (uri is null || uri.Scheme is not ("https" or "http") || uri.Query.Length > 1) continue;
                var host = uri.Host.ToLowerInvariant();
                var path = SourcePath(uri.AbsoluteUri).Split('?')[0];
                var provider = host is "nhentai.net" or "www.nhentai.net" ? "nhentai" : host is "e-hentai.org" or "exhentai.org" ? "ehentai" : host == "hitomi.la" ? "hitomi" : null;
                if (provider is null) continue;
                var pattern = provider switch { "nhentai" => "/g/([1-9][0-9]*)/?", "ehentai" => "/g/([1-9][0-9]*)/[A-Za-z0-9]+/?", _ => @"/(?:galleries/([1-9][0-9]*)|(?:manga|doujinshi|artistcg|gamecg|imageset)/[^/]+-([1-9][0-9]*))\.html" };
                var match = Regex.Match(path, "\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant);
                if (match.Success) found.Add($"asc1:gallery:{provider}:{ExternalId(provider, match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value)}");
            }
            catch (ArgumentException) { }
        }
        return found.Count == 1 ? found.Single() : null;
    }
}
