using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Text;

namespace AutoSourcarr.Core.Catalogue.Sharing;

/// <summary>Bounded UTF-8 .ascatalog JSON. No SQL, archive paths, executable code or local bindings.</summary>
public static class CatalogueFormat
{
    public const string Format = "autosourcarr-catalogue";
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
    public static JsonObject Decode(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty || raw.Length > MaximumBytes) throw new ArgumentException("Choose a nonempty catalogue file up to 64 MiB");
        try
        {
            var json = Utf8.GetString(raw);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 1000 });
            CheckDuplicates(document.RootElement);
            return Validate(JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 1000 }));
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or InvalidOperationException)
        { throw new ArgumentException("Choose an exported UTF-8 AutoSourcarr catalogue file", exception); }
    }
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
