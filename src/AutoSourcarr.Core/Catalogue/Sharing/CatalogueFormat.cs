using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Compatibility;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Catalogue.Sharing;

/// <summary>A decoded catalogue file: the validated version-1 body every import step works on, and the file header
/// (format_version, min_reader_version, app_version, schema_version, created_at, legacy).</summary>
public sealed record CatalogueFile(JsonObject Body, JsonObject Header);

/// <summary>A catalogue file needs a newer reader. The message is exactly "Version mismatch: version X required".</summary>
public sealed class CatalogueVersionMismatchException(string requiredVersion) : ArgumentException(CatalogueFormat.MismatchMessage(requiredVersion))
{
    public string RequiredVersion { get; } = requiredVersion;
}

/// <summary>Bounded UTF-8 .ascatalog JSON. No SQL, archive paths, executable code or local bindings.
/// Files carry a versioned header (share format 2); header-less 0.4.2-0.4.5 files (share format 1) are still read.</summary>
public static class CatalogueFormat
{
    public const string Format = "autosourcarr-catalogue";
    /// <summary>The share format this build writes (compatibility.json share_format.version).</summary>
    public const int FileVersion = 2;
    /// <summary>The oldest share format a reader must understand to read what this build writes.</summary>
    public const int MinimumReaderVersion = 2;
    /// <summary>The header-less format of 0.4.2-0.4.5; still the internal body shape (format, version 1, origin, entries, decisions).</summary>
    public const int LegacyVersion = 1;
    private static readonly string[] HeaderFields = ["format", "format_version", "min_reader_version", "app_version", "schema_version", "created_at", "origin", "entries", "decisions"];
    /// <summary>Header fields a current-format file may carry in addition (files written before they existed stay readable).</summary>
    private static readonly string[] OptionalHeaderFields = ["min_reader_app_version"];
    public static string MismatchMessage(string requiredVersion) => "Version mismatch: version " + requiredVersion + " required";
    public const int MaximumBytes = 64 * 1024 * 1024;
    public const int MaximumMembers = 2000;
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(["title", "group", "source-match", "separate", "entry-separate", "import-choice", "import-rejection", "identity-review"], StringComparer.Ordinal);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static string Uid(string kind = "entry") => $"asc1:{kind}:autosourcarr:{Guid.NewGuid():D}";
    public static string CheckUid(string value, string kind = "entry")
    {
        var prefix = $"asc1:{kind}:autosourcarr:";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Unsupported shared identity namespace");
        var tail = value[prefix.Length..];
        if (!Guid.TryParseExact(tail, "D", out var id) || id == Guid.Empty || id.ToString("D") != tail) throw new ArgumentException("Use a canonical shared identity");
        return value;
    }
    public static string Canonical(JsonNode? value)
    {
        var text = new StringBuilder(); Write(text, value); return text.ToString();
    }
    public static string Digest(JsonNode? value) => Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(Canonical(value))));
    private static void Write(StringBuilder output, JsonNode? node)
    {
        switch (node)
        {
            case null: output.Append("null"); break;
            case JsonObject obj:
                output.Append('{'); var first = true;
                foreach (var pair in obj.OrderBy(pair => pair.Key, UnicodeCodePointComparer.Instance))
                { if (!first) output.Append(','); first = false; String(output, pair.Key); output.Append(':'); Write(output, pair.Value); }
                output.Append('}'); break;
            case JsonArray array:
                output.Append('[');
                for (var i = 0; i < array.Count; i++) { if (i != 0) output.Append(','); Write(output, array[i]); }
                output.Append(']'); break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String: String(output, value.GetValue<string>()); break;
            case JsonValue: output.Append(CanonicalJson.Text(node)); break;
            default: throw new ArgumentException("Unsupported catalogue JSON value");
        }
    }
    private static void String(StringBuilder output, string value)
    {
        Utf8.GetByteCount(value); output.Append('"');
        foreach (var c in value) output.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", < ' ' => "\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture), _ => c.ToString() });
        output.Append('"');
    }
    public static string Text(JsonNode? value, int limit = 16000)
    {
        if (value is not JsonValue scalar || scalar.GetValueKind() != JsonValueKind.String) throw new ArgumentException("Invalid or oversized catalogue text");
        var text = scalar.GetValue<string>();
        if (text.EnumerateRunes().Count() > limit || text.Contains('\0')) throw new ArgumentException("Invalid or oversized catalogue text");
        try { Utf8.GetByteCount(text); } catch (EncoderFallbackException exception) { throw new ArgumentException("Catalogue text must be valid UTF-8", exception); }
        return text;
    }
    public static void Keys(JsonNode? value, params string[] required)
    {
        if (value is not JsonObject obj || !obj.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(required)) throw new ArgumentException("Unsupported catalogue fields");
    }
    public static JsonArray Array(JsonNode? value, int maximum, string name)
    { if (value is not JsonArray array || array.Count > maximum) throw new ArgumentException("Too many catalogue " + name); return array; }
    public static bool PublicIdentityText(string value) => !Regex.IsMatch(value, @"(?:[?&](?:auth|key|sig|signature|token|password|secret|session|api[_-]?key)=|https?://[^\s/]+@|/g/[1-9][0-9]*/[A-Za-z0-9]{8,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static string Reference(JsonNode? value)
    {
        var reference = Text(value, PortableIdentity.MaximumReferenceLength); var parsed = PortableIdentity.Parse(reference);
        if (parsed.Scope is not ("work" or "edition" or "gallery" or "record")) throw new ArgumentException("Use a public catalogue, gallery or source reference");
        if (parsed.Path is not null && !PublicIdentityText(parsed.Path)) throw new ArgumentException("Source identity contains a private access parameter");
        return reference;
    }
    public static JsonObject Metadata(JsonNode? value)
    {
        Keys(value, "description", "author", "artist", "genre", "status"); var obj = (JsonObject)value!;
        foreach (var key in new[] { "description", "author", "artist", "genre" }) Text(obj[key], key == "description" ? 64000 : 16000);
        if (!Integer(obj["status"], out var status) || status is < 0 or > 100) throw new ArgumentException("Invalid catalogue publication status");
        return obj;
    }
    public static JsonObject Locator(JsonNode? value)
    {
        Keys(value, "reference", "source_id", "title", "raw_title", "gallery", "metadata"); var obj = (JsonObject)value!;
        var reference = Reference(obj["reference"]); Text(obj["title"], 2000); Text(obj["raw_title"], 2000); Metadata(obj["metadata"]);
        var source = PortableIdentity.StableSource(Text(obj["source_id"]), canonical: true); var parsed = PortableIdentity.Parse(reference);
        if (parsed.Scope is not ("record" or "gallery") || parsed.Scope == "record" && source != parsed.ExternalId) throw new ArgumentException("Source reference does not belong to its declared source");
        if (obj["gallery"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Invalid gallery profile hint");
        return obj;
    }
    public static JsonObject Entry(JsonNode? value)
    {
        Keys(value, "uid", "aliases", "lineage", "title", "titles", "metadata", "references", "sources"); var obj = (JsonObject)value!;
        var uid = CheckUid(Text(obj["uid"])); Text(obj["title"], 2000);
        foreach (var item in Array(obj["aliases"], 512, "aliases")) CheckUid(Text(item));
        foreach (var item in Array(obj["titles"], 256, "titles")) Text(item, 2000);
        foreach (var item in Array(obj["references"], MaximumMembers, "references")) Reference(item);
        var sources = Array(obj["sources"], MaximumMembers, "sources");
        var seen = new HashSet<(string, string)>();
        foreach (var item in sources)
        { var fact = Locator(item); if (!seen.Add((Text(fact["source_id"]), Text(fact["reference"])))) throw new ArgumentException("Duplicate source record in one entry version"); }
        var aliases = ((JsonArray)obj["aliases"]!).Select(item => Text(item)).Append(uid).ToHashSet(StringComparer.Ordinal);
        foreach (var item in Array(obj["lineage"], 513, "lineage identities"))
        {
            Keys(item, "uid", "records"); var line = (JsonObject)item!;
            if (!aliases.Contains(CheckUid(Text(line["uid"])))) throw new ArgumentException("Lineage must describe this entry or a retained alias");
            foreach (var anchor in Array(line["records"], MaximumMembers, "lineage source references"))
            {
                Keys(anchor, "source_id", "reference"); var source = PortableIdentity.StableSource(Text(anchor!["source_id"]), canonical: true);
                var parsed = PortableIdentity.Parse(Reference(anchor["reference"]));
                if (parsed.Scope is not ("record" or "gallery") || parsed.Scope == "record" && source != parsed.ExternalId) throw new ArgumentException("Invalid lineage source reference");
            }
        }
        Metadata(obj["metadata"]); return obj;
    }
    public static JsonObject Decision(JsonNode? value)
    {
        Keys(value, "uid", "kind", "entries", "records", "value"); var obj = (JsonObject)value!;
        CheckUid(Text(obj["uid"]), "decision"); var kind = Text(obj["kind"]);
        if (!Kinds.Contains(kind)) throw new ArgumentException("Unsupported manual decision");
        var entries = Array(obj["entries"], MaximumMembers, "decision entries"); var records = Array(obj["records"], MaximumMembers, "decision records");
        foreach (var item in entries) CheckUid(Text(item)); foreach (var item in records) Locator(item);
        var allowed = new HashSet<string>(["title", "group_title", "note", "reference", "decision", "backup_key", "backup_title", "backup_source", "backup_source_id", "backup_source_url", "rejected_title", "signature", "grouping_suppressed"], StringComparer.Ordinal);
        if (obj["value"] is not JsonObject data || data.Any(pair => !allowed.Contains(pair.Key))) throw new ArgumentException("Unsupported decision fields");
        foreach (var pair in data) Text(pair.Value, pair.Key == "note" ? 64000 : 6000);
        string Get(string key) => data[key] is null ? "" : Text(data[key]);
        if (Get("reference").Length != 0) Reference(data["reference"]);
        if (Get("decision").Length != 0 && Get("decision") is not ("confirmed" or "excluded" or "needs-review")) throw new ArgumentException("Unsupported identity review choice");
        if (Get("backup_source_url").Length != 0) PortableIdentity.SourcePath(Get("backup_source_url"));
        if (new[] { "backup_key", "backup_source_url", "signature" }.Any(key => !PublicIdentityText(Get(key)))) throw new ArgumentException("Import decision contains a private access parameter");
        if (Get("signature").Length != 0 && !new[] { "source-url:", "source-title:", "entry-title:" }.Any(prefix => Get("signature").StartsWith(prefix, StringComparison.Ordinal))) throw new ArgumentException("Use a portable import rejection signature");
        if (data.ContainsKey("grouping_suppressed") && Get("grouping_suppressed") is not ("0" or "1")) throw new ArgumentException("Invalid manual grouping choice");
        if (kind == "title" && (entries.Count != 1 || Get("title").Length == 0)) throw new ArgumentException("A title decision needs one entry and a title");
        if (kind == "entry-separate" && entries.Count != 2) throw new ArgumentException("A separation needs two entry identities");
        if (kind == "identity-review" && (entries.Count != 1 || Get("reference").Length == 0 || Get("decision").Length == 0)) throw new ArgumentException("An identity review needs one entry, a reference and a decision");
        if (kind is "source-match" or "import-choice" && (entries.Count > 1 || records.Count > 1)) throw new ArgumentException("This decision must identify one target source record");
        if (kind == "separate" && (entries.Count is < 1 or > 2 || records.Count > 1)) throw new ArgumentException("A source separation needs a target and at most one source record");
        return obj;
    }
    public static JsonObject Validate(JsonNode? value)
    {
        Keys(value, "format", "version", "origin", "entries", "decisions"); var obj = (JsonObject)value!;
        if (Text(obj["format"]) != Format || !Integer(obj["version"], out var version) || version != 1) throw new ArgumentException("Unsupported catalogue file version");
        CheckUid(Text(obj["origin"]), "origin"); var seen = new HashSet<(string, string)>(); long members = 0;
        foreach (var item in Array(obj["entries"], 100000, "entries"))
        {
            var entry = Entry(item); if (!seen.Add((Text(entry["uid"]), Digest(entry)))) throw new ArgumentException("Duplicate entry version in one catalogue file");
            members += new[] { "sources", "references", "titles", "aliases" }.Sum(key => ((JsonArray)entry[key]!).Count) + ((JsonArray)entry["lineage"]!).Sum(line => ((JsonArray)line!["records"]!).Count);
        }
        seen.Clear();
        foreach (var item in Array(obj["decisions"], 200000, "decisions"))
        { var choice = Decision(item); if (!seen.Add((Text(choice["uid"]), Digest(choice)))) throw new ArgumentException("Duplicate decision version in one catalogue file"); members += ((JsonArray)choice["entries"]!).Count + ((JsonArray)choice["records"]!).Count; }
        if (members > 1000000) throw new ArgumentException("Catalogue collection limit exceeded; export smaller batches");
        return obj;
    }
    /// <summary>The validated body of a catalogue file (see <see cref="DecodeFile"/>).</summary>
    public static JsonObject Decode(ReadOnlySpan<byte> raw) => DecodeFile(raw).Body;

    /// <summary>
    /// Reads a catalogue file. A file whose min_reader_version is newer than this build is refused before its entries
    /// are read, with exactly "Version mismatch: version X required" (X from the compatibility list, else the writer's
    /// app_version). A header-less share format 1 file is upgraded: its body is the current body shape.
    /// </summary>
    public static CatalogueFile DecodeFile(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty || raw.Length > MaximumBytes) throw new ArgumentException("Choose a nonempty catalogue file up to 64 MiB");
        try
        {
            var json = Utf8.GetString(raw);
            using (var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 1000 })) CheckDuplicates(document.RootElement);
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 1000 });
            if (root is JsonObject file && file.ContainsKey("format_version")) return ReadVersioned(file);
            return new CatalogueFile(Validate(root), Header(LegacyVersion, LegacyVersion, "", 0, "", legacy: true));
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or InvalidOperationException)
        { throw new ArgumentException("Choose an exported UTF-8 AutoSourcarr catalogue file", exception); }
    }

    /// <summary>Puts the versioned header in front of a validated body. The body's origin, entries and decisions move to the file.</summary>
    public static JsonObject Wrap(JsonObject body, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(body);
        var list = CompatibilityList.Current;
        var file = new JsonObject
        {
            ["format"] = Format, ["format_version"] = FileVersion, ["min_reader_version"] = MinimumReaderVersion, ["app_version"] = CompatibilityList.RunningVersion,
            // The first app version that reads min_reader_version: what an older reader names in its "Version mismatch".
            ["min_reader_app_version"] = list.ShareReaderAppVersion(MinimumReaderVersion) ?? CompatibilityList.RunningVersion,
            ["schema_version"] = list.CatalogSchema, ["created_at"] = createdAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var key in new[] { "origin", "entries", "decisions" }) { var node = body[key]; body.Remove(key); file[key] = node; }
        return file;
    }

    private static CatalogueFile ReadVersioned(JsonObject file)
    {
        if (file["format"] is not JsonValue format || format.GetValueKind() != JsonValueKind.String || format.GetValue<string>() != Format) throw new ArgumentException("Choose an exported AutoSourcarr catalogue file");
        if (!Integer(file["format_version"], out var version) || version < FileVersion) throw new ArgumentException("Unsupported catalogue file version");
        if (!Integer(file["min_reader_version"], out var minimum) || minimum < 1 || minimum > version) throw new ArgumentException("Invalid catalogue file header");
        var declared = VersionField(file, "app_version");
        var readerApp = VersionField(file, "min_reader_app_version");
        if (minimum > FileVersion)
        {
            // X of "Version mismatch: version X required": the file's own min_reader_app_version, else what the local
            // compatibility list knows for that format, else the writer's app_version, else the format number itself.
            var known = minimum <= int.MaxValue ? CompatibilityList.Current.ShareReaderAppVersion((int)minimum) : null;
            throw new CatalogueVersionMismatchException(readerApp ?? known ?? declared ?? minimum.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        // A newer format that older readers may still read (min_reader_version <= ours) can add header fields; ours must all be there.
        if (version == FileVersion)
        {
            if (HeaderFields.Any(key => !file.ContainsKey(key)) || file.Any(pair => !HeaderFields.Contains(pair.Key) && !OptionalHeaderFields.Contains(pair.Key)))
                throw new ArgumentException("Unsupported catalogue fields");
            if (file.ContainsKey("min_reader_app_version") && readerApp is null) throw new ArgumentException("Invalid catalogue file header");
        }
        else if (HeaderFields.Any(key => !file.ContainsKey(key))) throw new ArgumentException("Unsupported catalogue fields");
        if (declared is null || !Integer(file["schema_version"], out var schema) || schema < 0) throw new ArgumentException("Invalid catalogue file header");
        var created = Text(file["created_at"], 64);
        var body = new JsonObject { ["format"] = Format, ["version"] = LegacyVersion };
        foreach (var key in new[] { "origin", "entries", "decisions" }) { var node = file[key]; file.Remove(key); body[key] = node; }
        var header = Header(version, minimum, declared, schema, created, legacy: false);
        if (readerApp is not null) header["min_reader_app_version"] = readerApp;
        return new CatalogueFile(Validate(body), header);
    }

    /// <summary>A header version text ("0.5.0", "1.2.0-beta+x"), or null when missing or malformed.</summary>
    private static string? VersionField(JsonObject file, string name) =>
        file[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String && VersionText(value.GetValue<string>()) ? value.GetValue<string>() : null;

    private static bool VersionText(string value) => Regex.IsMatch(value, @"\A[0-9A-Za-z][0-9A-Za-z.+-]{0,63}\z", RegexOptions.CultureInvariant);

    private static JsonObject Header(long version, long minimum, string app, long schema, string created, bool legacy) => new()
    {
        ["format_version"] = version, ["min_reader_version"] = minimum, ["app_version"] = app, ["schema_version"] = schema, ["created_at"] = created, ["legacy"] = legacy,
    };
    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new ArgumentException("Duplicate JSON field in catalogue file"); CheckDuplicates(property.Value); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
    }
    private static bool Integer(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue scalar || scalar.GetValueKind() != JsonValueKind.Number) return false;
        var raw = scalar.ToJsonString();
        return !raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E') && long.TryParse(raw, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
