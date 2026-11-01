using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Reader.Extension;

/// <summary>One file of the reader extension repository, ready to send.</summary>
public sealed record ReaderRepositoryFile(byte[] Body, string ContentType, string FileName);

/// <summary>
/// READ-1: the prebuilt reader extension and its own Mihon/Tachiyomi repository, written by
/// scripts/build-reader-extension.sh (index.min.json, repo.json, build.json, apk/, icon/, jar/) and served unchanged at
/// <see cref="RoutePrefix"/>. Readers add the repository by URL without custom headers, so these files are public; they
/// hold no address, key or catalogue data. Files are read on every request, so a new build is served at once.
/// </summary>
public sealed partial class ReaderExtensionRepository(string root)
{
    /// <summary>The reader extension's package name (Keiyoushi module all/autosourcarr).</summary>
    public const string Package = "eu.kanade.tachiyomi.extension.all.autosourcarr";
    /// <summary>Public, unauthenticated repository path; the reverse-proxy examples allow it next to the reader API.</summary>
    public const string RoutePrefix = "/api/reader/repo";
    /// <summary>Server slots of the release build (build.gradle.kts default).</summary>
    public const int DefaultSlots = 5;
    private const long MaximumFile = 64L * 1024 * 1024;

    public string Root { get; } = Path.GetFullPath(root);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();

    /// <summary>
    /// The repository file at <paramref name="relativePath"/> (index.min.json, repo.json, apk/*.apk, icon/*.png, jar/*.jar),
    /// or null when it is not one of those or does not exist.
    /// </summary>
    public ReaderRepositoryFile? Read(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath.Length > 300) return null;
        var parts = relativePath.Split('/');
        string? contentType = parts switch
        {
            ["index.min.json"] or ["repo.json"] => "application/json",
            ["apk", var name] when name.EndsWith(".apk", StringComparison.Ordinal) => "application/vnd.android.package-archive",
            ["icon", var name] when name.EndsWith(".png", StringComparison.Ordinal) => "image/png",
            ["jar", var name] when name.EndsWith(".jar", StringComparison.Ordinal) => "application/java-archive",
            _ => null
        };
        if (contentType is null || parts.Any(part => !SafeName().IsMatch(part) || part.Contains("..", StringComparison.Ordinal))) return null;
        var path = Path.GetFullPath(Path.Combine([Root, .. parts]));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > MaximumFile) return null;
        try { return new(File.ReadAllBytes(path), contentType, parts[^1]); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The extension row of index.min.json, or null when no build is present.</summary>
    public JsonObject? Extension()
    {
        if (Read("index.min.json") is not { } index) return null;
        try
        {
            return (JsonNode.Parse(index.Body) as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(row => Text(row["pkg"]) == Package);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>What the System page shows about the served build: version, slots, signing and the files.</summary>
    public JsonObject Status()
    {
        var extension = Extension();
        if (extension is null)
            return new()
            {
                ["available"] = false,
                ["slots"] = DefaultSlots,
                ["message"] = "No reader extension build is installed in " + Root + ". Release images include it; otherwise run scripts/build-reader-extension.sh."
            };
        JsonObject build = [];
        try { build = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "build.json"))) as JsonObject ?? []; }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { build = []; }
        var sources = (extension["sources"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var slots = new JsonArray();
        for (var index = 0; index < sources.Count; index++)
            slots.Add(new JsonObject
            {
                ["slot"] = index + 1,
                ["name"] = Text(sources[index]["name"]),
                ["source_id"] = Text(sources[index]["id"])
            });
        var apk = Text(extension["apk"]);
        var jar = Text(extension["jar"]);
        return new()
        {
            ["available"] = true,
            ["version_name"] = Text(extension["version"]),
            ["version_code"] = Text(extension["code"]),
            ["slots"] = sources.Count == 0 ? DefaultSlots : sources.Count,
            ["sources"] = slots,
            ["apk"] = apk.Length == 0 ? null : RoutePrefix + "/apk/" + apk,
            ["jar"] = jar.Length == 0 ? null : RoutePrefix + "/jar/" + jar,
            ["apk_sha256"] = Text(extension["sha256"]),
            ["jar_sha256"] = Text(extension["jar_sha256"]),
            ["signed_for_release"] = build["signed_for_release"] is JsonValue signed && signed.TryGetValue<bool>(out var value) && value,
            ["signing_fingerprint"] = Text(build["signing_fingerprint"]),
            ["autosourcarr_version"] = Text(build["autosourcarr_version"])
        };
    }

    internal static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text
        : node is JsonValue number && number.TryGetValue<long>(out var integer) ? integer.ToString(CultureInfo.InvariantCulture) : "";
}
