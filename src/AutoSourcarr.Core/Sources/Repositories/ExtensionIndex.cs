using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Sources.Repositories.Generated;
using Google.Protobuf;

namespace AutoSourcarr.Core.Sources.Repositories;

/// <summary>One extension repository read as an indexer: its name and every installable extension it lists.</summary>
public sealed record RepositoryIndex(string Name, string BadgeLabel, bool IsLegacy, IReadOnlyList<IndexedExtension> Extensions);

/// <summary>
/// One extension row. <see cref="ApkUrl"/>, <see cref="IconUrl"/> and <see cref="JarUrl"/> are absolute http(s) URLs
/// resolved against the index URL. <see cref="ContentWarning"/> is the raw value (index.pb field 7, or legacy <c>nsfw</c>);
/// <see cref="Nsfw"/> follows parse_index: values 2 and 3 for index.pb, any non-zero/true value for legacy JSON.
/// </summary>
public sealed record IndexedExtension(
    string Name, string Package, string Lang, string ApkUrl, string? IconUrl, string? JarUrl,
    string LibraryVersion, long VersionCode, string VersionName, int ContentWarning, bool Nsfw,
    IReadOnlyList<IndexedSource> Sources);

/// <summary>One source inside an extension. <see cref="Id"/> is the signed 64-bit source id in invariant decimal.</summary>
public sealed record IndexedSource(string Id, string Name, string Lang, string BaseUrl);

/// <summary>
/// Reads Mihon/Keiyoushi extension repository indexes: index.pb (generated protobuf parser) and the legacy
/// index.min.json array, following source-host ExtensionRepos.kt. Listing an extension says nothing about trust.
/// </summary>
public static class ExtensionIndex
{
    private const string TachiyomiPrefix = "Tachiyomi: ";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>
    /// Parses an index body fetched from <paramref name="indexUrl"/>. Gzip is expanded with the same strict, bounded
    /// reader as <see cref="RepositoryManifest"/>. Like ExtensionRepos.kt, a body starting with '[' from a URL that does
    /// not end in .pb is the legacy JSON format; everything else is index.pb.
    /// </summary>
    /// <exception cref="InvalidDataException">The body is empty, too large, or not a readable extension index.</exception>
    public static RepositoryIndex Parse(byte[] body, Uri indexUrl)
    {
        var data = Prepare(body, indexUrl);
        if (data[0] == (byte)'[' && !indexUrl.AbsolutePath.EndsWith(".pb", StringComparison.OrdinalIgnoreCase))
            return ReadLegacy(data, indexUrl);
        return ReadProtobuf(data, indexUrl);
    }

    /// <summary>Parses a legacy index.min.json array; APKs live in apk/ and icons in icon/ next to the index.</summary>
    /// <exception cref="InvalidDataException">The body is empty, too large, or not a legacy extension index.</exception>
    public static RepositoryIndex FromLegacyJson(byte[] body, Uri indexUrl) => ReadLegacy(Prepare(body, indexUrl), indexUrl);

    private static byte[] Prepare(byte[] body, Uri indexUrl)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(indexUrl);
        if (!indexUrl.IsAbsoluteUri) throw new ArgumentException("The extension index URL must be absolute", nameof(indexUrl));
        if (body.Length == 0 || body.Length > RepositoryManifest.MaximumBytes) throw Invalid("Extension index is empty or too large");
        if (body.Length > 2 && body[0] == 0x1f && body[1] == 0x8b) body = RepositoryManifest.ExpandGzip(body);
        if (body.Length == 0) throw Invalid("Extension index is empty");
        return body;
    }

    private static RepositoryIndex ReadProtobuf(byte[] data, Uri indexUrl)
    {
        IndexDocument document;
        try { document = IndexDocument.Parser.ParseFrom(data); }
        catch (InvalidProtocolBufferException) { throw Invalid("Extension index could not be decoded as index.pb protobuf"); }
        if (document.ExtensionList is null) throw Invalid("Extension index does not contain its extension list (field 101)");

        var extensions = new List<IndexedExtension>(document.ExtensionList.Extensions.Count);
        foreach (var row in document.ExtensionList.Extensions)
        {
            var resources = row.Resources;
            // ExtensionRepos.kt skips rows without a package or APK; an APK that is not http(s) cannot be installed either.
            if (row.Pkg.Length == 0 || resources is null || Resolve(indexUrl, resources.ApkUrl) is not { } apk) continue;
            var sources = row.Sources
                .Select(source => new IndexedSource(source.Id.ToString(CultureInfo.InvariantCulture), source.Name, source.Lang, source.BaseUrl))
                .ToArray();
            var langs = sources.Select(source => source.Lang).Distinct(StringComparer.Ordinal).ToArray();
            extensions.Add(new IndexedExtension(
                StripPrefix(row.Name), row.Pkg, langs.Length == 1 ? langs[0] : "all", apk,
                Resolve(indexUrl, resources.IconUrl), Resolve(indexUrl, resources.JarUrl),
                row.Lib, row.Code, row.Version, row.ContentWarning, row.ContentWarning is 2 or 3, sources));
        }
        var name = document.Name.Length > 0 ? document.Name : indexUrl.AbsoluteUri;
        return new RepositoryIndex(name, document.BadgeLabel, IsLegacy: false, extensions);
    }

    private static RepositoryIndex ReadLegacy(byte[] data, Uri indexUrl)
    {
        JsonArray rows;
        try
        {
            rows = JsonNode.Parse(Utf8.GetString(data), documentOptions: new() { MaxDepth = 32 }) as JsonArray
                ?? throw Invalid("Legacy extension index must be a JSON array");
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw Invalid("Legacy extension index could not be decoded"); }

        var extensions = new List<IndexedExtension>(rows.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index] is not JsonObject row) throw Invalid($"Legacy extension index row {index} is not an object");
            string Required(string key) => Primitive(row[key]) ?? throw Invalid($"Legacy extension index row {index} is missing '{key}'");
            var apk = Required("apk"); var version = Required("version"); var pkg = Required("pkg"); var name = Required("name");
            var apkUrl = Resolve(indexUrl, "apk/" + apk) ?? throw Invalid($"Legacy extension index row {index} has an unusable APK name");
            // kotlinx intOrNull: out-of-range values read as absent (0).
            var warning = LongOrNull(row["nsfw"]) is { } level && level is >= int.MinValue and <= int.MaxValue ? (int)level : 0;
            var nsfwFlag = row["nsfw"] is JsonValue flagValue && flagValue.TryGetValue<bool>(out var flag) && flag;
            var sources = new List<IndexedSource>();
            if (row["sources"] is JsonArray sourceRows)
                foreach (var node in sourceRows)
                {
                    if (node is not JsonObject source) throw Invalid($"Legacy extension index row {index} has a source that is not an object");
                    // parse_index keeps only decimal int64 ids; the canonical decimal form is stored.
                    if (LongOrNull(source["id"]) is not { } id) continue;
                    sources.Add(new IndexedSource(id.ToString(CultureInfo.InvariantCulture), Primitive(source["name"]) ?? "",
                        Primitive(source["lang"]) ?? "", Primitive(source["baseUrl"]) ?? ""));
                }
            var dot = version.LastIndexOf('.');
            extensions.Add(new IndexedExtension(
                StripPrefix(name), pkg, Primitive(row["lang"]) ?? "", apkUrl, Resolve(indexUrl, "icon/" + pkg + ".png"), null,
                dot < 0 ? version : version[..dot], LongOrNull(row["code"]) ?? 0, version, warning, warning != 0 || nsfwFlag, sources));
        }
        var directory = new Uri(indexUrl, ".").AbsoluteUri;
        return new RepositoryIndex(directory, "", IsLegacy: true, extensions);
    }

    private static string StripPrefix(string name) => name.StartsWith(TachiyomiPrefix, StringComparison.Ordinal) ? name[TachiyomiPrefix.Length..] : name;

    private static string? Resolve(Uri indexUrl, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || !Uri.TryCreate(indexUrl, reference.Trim(), out var resolved)) return null;
        return resolved.IsAbsoluteUri && (resolved.Scheme == Uri.UriSchemeHttps || resolved.Scheme == Uri.UriSchemeHttp) ? resolved.AbsoluteUri : null;
    }

    /// <summary>kotlinx jsonPrimitive.content: string text, or the literal text of a number/boolean; null for absent, null or structured values.</summary>
    private static string? Primitive(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        return value.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? value.ToJsonString() : null;
    }

    /// <summary>kotlinx longOrNull: the primitive's content parsed as a signed 64-bit integer.</summary>
    private static long? LongOrNull(JsonNode? node) =>
        Primitive(node) is { } text && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static InvalidDataException Invalid(string message) => new(message);
}
