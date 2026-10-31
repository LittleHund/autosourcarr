using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Repair;

/// <summary>Public source provenance only. Validation is not execution or trust approval.</summary>
public static class RawCodeRules
{
    public const int MaximumZip = 32 * 1024 * 1024, MaximumRepositoryZip = 128 * 1024 * 1024;
    public const int MaximumExpanded = 160 * 1024 * 1024, MaximumFile = 8 * 1024 * 1024;
    public const int MaximumFiles = 8192, MaximumManifest = 2 * 1024 * 1024;
    public const string PortableName = ".autosourcarr-source.json";
    private const string PackagePattern = @"[A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*){2,}";
    private static readonly HashSet<string> MetadataKeys = ["package", "version", "module", "ecosystem", "recipe", "kind", "origin", "parent", "extension_lib", "upstream_module", "overlay_sources"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string Package(string value) => Match(value, PackagePattern, 200) ? value : throw Invalid("Invalid source package identity");
    public static string Hash(string value) => Match(value, "[0-9a-f]{64}", 64) ? value : throw Invalid("Invalid raw object fingerprint");
    public static string Commit(string value) => Match(value, "[0-9a-f]{40}", 40) ? value : throw Invalid("Source revision must be a full commit hash");
    public static string Version(string value) => Match(value, "[0-9][A-Za-z0-9.+_-]{0,99}", 100) ? value : throw Invalid("Installed source version is required for raw provenance");
    public static string RelativeName(string value)
    {
        if (string.IsNullOrEmpty(value) || !WithinScalarLength(value, 512) || value.StartsWith('/') || value.Contains('\\') || value.Contains(':') || value.Any(c => c < 32 || c == 127))
            throw Invalid("Unsafe raw source path");
        var parts = value.Split('/');
        if (parts.Any(p => p is "." or "..")) throw Invalid("Raw source paths must stay inside their project");
        var result = string.Join('/', parts.Where(p => p.Length != 0));
        if (result.Length == 0) throw Invalid("Unsafe raw source path");
        // A corrupt UTF-16 name must never become a replacement-character identity.
        try { _ = StrictUtf8.GetByteCount(result); } catch (EncoderFallbackException) { throw Invalid("Invalid raw source path encoding"); }
        return result;
    }
    public static string Module(string value)
    {
        if (value == ".") return value;
        value = RelativeName(value);
        return value.Split('/').Length <= 8 && Match(value, "[A-Za-z0-9_/-]+", 512) ? value : throw Invalid("Invalid source project module");
    }
    public static string PublicReference(string value)
    {
        if (string.IsNullOrEmpty(value) || !WithinScalarLength(value, 2048) || value.Any(c => c < 33 || c == 127) || value.Contains('\\') ||
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Port != 443)
            throw Invalid("Source origins require public HTTPS without credentials or query values");
        var host = uri.IdnHost.TrimEnd('.').Trim('[', ']').ToLowerInvariant();
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal)) throw Invalid("Private source origin refused");
        if (IPAddress.TryParse(host, out var address) && !IsGlobalAddress(address)) throw Invalid("Private source origin refused");
        return value;
    }
    public static string OriginalUrlPath(string value)
    {
        var start = value.IndexOf('/', value.IndexOf("://", StringComparison.Ordinal) + 3);
        var suffix = value.IndexOfAny(['?', '#']);
        if (start < 0 || suffix >= 0 && start > suffix) return "";
        return suffix < 0 ? value[start..] : value[start..suffix];
    }
    // Provenance URL validation only; actual egress must resolve/pin every address again.
    public static bool IsGlobalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsGlobalAddress(address.MapToIPv4());
        var b = address.GetAddressBytes();
        if (b.Length == 4)
            return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
                !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2)) &&
                !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)) && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        // Globally routed IPv6 unicast, excluding documentation and special-use ranges.
        return b.Length == 16 && (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] < 2) &&
            !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8) && !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0);
    }
    public static JsonObject Metadata(JsonObject value)
    {
        if (value.Any(pair => !MetadataKeys.Contains(pair.Key))) throw Invalid("Unsupported raw source metadata");
        var result = new JsonObject { ["package"] = Package(Text(value, "package")), ["version"] = Version(Text(value, "version")), ["module"] = Module(Text(value, "module")),
            ["ecosystem"] = Choice(Text(value, "ecosystem"), ["tachiyomi", "keiyoushi", "mihon", "suwayomi"]),
            ["recipe"] = Choice(Text(value, "recipe"), ["gradle", "jvm"]), ["kind"] = Choice(Text(value, "kind"), ["upstream", "provided", "reconstructed", "repaired"]) };
        var origin = value["origin"] is null ? new JsonObject() : value["origin"] as JsonObject ?? throw Invalid("Unsupported source origin metadata");
        if (origin.Any(pair => pair.Key is not ("repository" or "revision" or "index_url" or "basis"))) throw Invalid("Unsupported source origin metadata");
        var cleaned = new JsonObject();
        foreach (var key in new[] { "repository", "index_url" }) if (Present(origin[key])) cleaned[key] = PublicReference(Text(origin, key));
        if (Present(origin["revision"])) cleaned["revision"] = Commit(Text(origin, "revision"));
        if (Present(origin["basis"])) cleaned["basis"] = Choice(Text(origin, "basis"), ["known-repository", "repository-candidate", "user-provided", "binary-reconstruction", "derived-code"]);
        result["origin"] = cleaned;
        if (Present(value["parent"])) result["parent"] = Hash(Text(value, "parent"));
        if (Present(value["extension_lib"]))
        {
            var library = Text(value, "extension_lib");
            result["extension_lib"] = Match(library, @"[0-9]{1,3}\.[0-9]{1,3}", 7) ? library : throw Invalid("Invalid extension library metadata");
        }
        if (Present(value["upstream_module"])) result["upstream_module"] = Module(Text(value, "upstream_module"));
        if (value.ContainsKey("overlay_sources"))
        {
            var prefix = (Text(result, "module") == "." ? "" : Text(result, "module") + "/") + "src/";
            if (Text(result, "recipe") != "jvm" || value["overlay_sources"] is not JsonArray rows || rows.Count is < 1 or > 30) throw Invalid("Invalid reconstructed Java overlay source list");
            var names = rows.Select(row => Text(row)).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(name => !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".java", StringComparison.Ordinal)))
                throw Invalid("Invalid reconstructed Java overlay source list");
            result["overlay_sources"] = new JsonArray(names.Select(RelativeName).Order(UnicodeCodePointComparer.Instance).Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        }
        return result;
    }
    public static string Text(JsonObject value, string key) => Text(value[key]);
    public static string Text(JsonNode? node) => node is JsonValue scalar && scalar.TryGetValue<string>(out var value) ? value : throw Invalid("Invalid source metadata value");
    public static bool Present(JsonNode? value) => value is not null && (value is not JsonValue scalar || !(scalar.TryGetValue<string>(out var s) && s.Length == 0 || scalar.TryGetValue<bool>(out var b) && !b || scalar.TryGetValue<long>(out var n) && n == 0));
    private static string Choice(string value, string[] choices) => choices.Contains(value, StringComparer.Ordinal) ? value : throw Invalid("Unsupported raw source provenance value");
    internal static bool Match(string? value, string pattern, int maximum) => value is not null && value.Length <= maximum && Regex.IsMatch(value, "\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static bool WithinScalarLength(string value, int maximum)
    {
        if (value.Length <= maximum) return true; if (value.Length > maximum * 2) return false;
        var count = 0; foreach (var rune in value.EnumerateRunes()) { _ = rune; if (++count > maximum) return false; } return true;
    }
    internal static InvalidDataException Invalid(string message) => new(message);
}
