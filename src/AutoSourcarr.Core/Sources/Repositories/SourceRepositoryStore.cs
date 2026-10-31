using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Sources.Repositories;

public sealed class SourceRepositoryConflictException() : ArgumentException("Repository configuration changed. Refresh before saving");

/// <summary>Repository edits of one explicitly selected instance's source-host.env, through its canonical writer.</summary>
public sealed class SourceRepositoryStore(CanonicalConfiguration configuration)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly CanonicalConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    public const string KeiyoushiUrl = "https://github.com/keiyoushi/extensions/raw/repo/index.pb";

    public JsonObject Snapshot() => _configuration.RepositorySnapshot();
    public JsonObject Change(string url, string revision, bool remove = false)
    {
        // A legacy persisted value must remain removable without passing today's add/preview URL rules.
        if (!remove) url = NormalizeUrl(url);
        ArgumentNullException.ThrowIfNull(url);
        return _configuration.ChangeRepository(url, revision, remove);
    }

    public static string NormalizeUrl(string? value)
    {
        if (value is null || value.EnumerateRunes().Count() > 2048 || value.Any(c => c < 33 || c == 127))
            throw new ArgumentException("Enter a repository manifest URL without spaces or control characters");
        var match = Regex.Match(value, @"\A([A-Za-z][A-Za-z0-9+.-]*)://([^/?#]*)([^?#]*)(?:\?([^#]*))?(?:#(.*))?\z", RegexOptions.CultureInvariant);
        if (!match.Success || !match.Groups[1].Value.Equals("https", StringComparison.OrdinalIgnoreCase) || match.Groups[5].Value.Length != 0)
            throw new ArgumentException("Use an HTTPS manifest URL without credentials or a fragment");
        var authority = match.Groups[2].Value; var hostPort = authority;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            if (authority[..at].Trim(':').Length != 0) throw new ArgumentException("Use an HTTPS manifest URL without credentials or a fragment");
            hostPort = authority[(at + 1)..];
        }
        string host, port = "";
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']');
            if (close < 0 || !System.Net.IPAddress.TryParse(hostPort[1..close], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                throw new ArgumentException("Invalid repository HTTPS host");
            host = hostPort[1..close];
            if (close + 1 < hostPort.Length)
            {
                if (hostPort[close + 1] != ':') throw new ArgumentException("Invalid repository HTTPS host");
                port = hostPort[(close + 2)..];
            }
        }
        else
        {
            var colon = hostPort.LastIndexOf(':'); host = colon < 0 ? hostPort : hostPort[..colon];
            if (colon >= 0) port = hostPort[(colon + 1)..];
            if (host.Contains(':') || host.Contains('[') || host.Contains(']')) throw new ArgumentException("Invalid repository HTTPS host");
        }
        if (host.Length == 0) throw new ArgumentException("Use an HTTPS manifest URL without credentials or a fragment");
        if (port.Length > 0 && (port.Any(c => c is < '0' or > '9') || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number != 443))
            throw new ArgumentException("Repository previews support the standard HTTPS port");
        var path = match.Groups[3].Value; var query = match.Groups[4].Value;
        return "https://" + authority.ToLowerInvariant() + (path.Length == 0 ? "/" : path) + (query.Length == 0 ? "" : "?" + query);
    }

    internal static JsonObject Parse(byte[] bytes)
    {
        var text = Decode(bytes); var seen = new HashSet<string>(StringComparer.Ordinal); string raw = "[]";
        var errors = new List<string>(); var lines = Lines(text);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = Trim(lines[index]); if (line.Length == 0 || line.StartsWith('#')) continue;
            var equals = lines[index].IndexOf('=');
            if (equals < 0) { errors.Add($"Line {index + 1}: expected KEY=value"); continue; }
            var key = Trim(lines[index][..equals]);
            if (!Regex.IsMatch(key, @"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant)) { errors.Add($"Line {index + 1}: invalid environment key"); continue; }
            if (!seen.Add(key)) { errors.Add($"Line {index + 1}: duplicate key {key}"); continue; }
            if (key == "EXTENSION_STORES") raw = Trim(lines[index][(equals + 1)..]);
        }
        if (errors.Count > 0) throw new ArgumentException("Source host settings need correction: " + string.Join("; ", errors));
        if (raw.Length >= 2 && raw[0] == raw[^1] && raw[0] is '\'' or '"') raw = raw[1..^1];
        JsonArray urls;
        try
        {
            urls = JsonNode.Parse(raw, documentOptions: new() { MaxDepth = 32 }) as JsonArray ?? throw InvalidStores();
            if (urls.Any(url => url is not JsonValue scalar || !scalar.TryGetValue<string>(out _))) throw InvalidStores();
        }
        catch (JsonException) { throw InvalidStores(); }
        var repositories = new JsonArray();
        foreach (var url in urls.Select(url => url!.GetValue<string>()).Distinct(StringComparer.Ordinal))
        {
            var familiar = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Host is "github.com" or "raw.githubusercontent.com")
                && uri.AbsolutePath.Contains("/keiyoushi/", StringComparison.OrdinalIgnoreCase);
            repositories.Add(new JsonObject { ["url"] = url, ["name"] = familiar ? "Keiyoushi" : "Extension repository", ["state"] = "Saved configuration" });
        }
        return new() { ["revision"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["repositories"] = repositories };
    }

    internal static byte[] Edit(byte[] bytes, string url, string revision, bool remove)
    {
        var before = Parse(bytes);
        if (string.IsNullOrEmpty(revision) || before["revision"]!.GetValue<string>() != revision) throw new SourceRepositoryConflictException();
        var urls = before["repositories"]!.AsArray().Select(row => row!["url"]!.GetValue<string>()).ToList();
        if (remove) urls.RemoveAll(item => item == url); else if (!urls.Contains(url, StringComparer.Ordinal)) urls.Add(url);
        var value = CanonicalJson.Text(new JsonArray(urls.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()));
        var lines = Lines(Decode(bytes)); var found = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var equals = lines[index].IndexOf('=');
            if (equals < 0 || Trim(lines[index]).StartsWith('#') || Trim(lines[index][..equals]) != "EXTENSION_STORES") continue;
            lines[index] = "EXTENSION_STORES=" + value; found = true;
        }
        if (!found)
        {
            if (lines.Count > 0 && lines[^1] != "") lines.Add("");
            lines.Add("EXTENSION_STORES=" + value);
        }
        return Utf8.GetBytes(string.Join('\n', lines) + "\n");
    }

    private static string Decode(byte[] bytes)
    {
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new ArgumentException("Source host settings must be valid UTF-8"); }
    }
    private static List<string> Lines(string text)
    {
        if (text.Length == 0) return [];
        var lines = Regex.Split(text, "\\r\\n|[\\n\\v\\f\\r\\x1c-\\x1e\\u0085\\u2028\\u2029]", RegexOptions.CultureInvariant).ToList();
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
    private static string Trim(string value) => Regex.Replace(value, @"\A[\s\x1c-\x1f]+|[\s\x1c-\x1f]+\z", "", RegexOptions.CultureInvariant);
    private static ArgumentException InvalidStores() => new("EXTENSION_STORES must be a JSON array of repository URLs");
}
