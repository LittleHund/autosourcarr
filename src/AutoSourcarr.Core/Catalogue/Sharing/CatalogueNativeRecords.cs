using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Catalogue.Tags;
using static AutoSourcarr.Core.Catalogue.Sharing.SharingJson;

namespace AutoSourcarr.Core.Catalogue.Sharing;

public sealed record CatalogueNativeRecord(DatabaseRow Row, string Storage, JsonObject Fact)
{
    public long EntryId => Row.Integer("entry_id");
    public string SourceId => Row.Text("source_id");
    public long MangaId => Row.Integer("source_manga_id");
}

/// <summary>The full native gallery evidence engine supplies this predicate. Null means ordinary records.</summary>
public delegate bool? CatalogueGalleryEvidence(JsonObject candidateFact, IReadOnlyList<JsonObject> existingFacts);

/// <summary>Structural work is owned by the application's visible task/journal pipeline, outside the exchange transaction.</summary>
public interface ICatalogueReviewedStructure
{
    // Complete actual catalogue-committed origin work before a retried choice can become ready.
    // Throw and retain the journals on failure or unavailable domain recovery.
    void ReconcilePending(CancellationToken cancellationToken);
    // Must recheck local Separate/edition/gallery rules and exact source ownership, use journalled
    // merge/split operations, preserve aliases/pages, and reconcile a previously started operation.
    // Throw on failure or uncertainty. Never return before physical owned work is drained.
    void ApplyReviewed(string decisionUid, string decisionDigest, string revision, JsonObject decision, IReadOnlyList<CatalogueNativeRecord> records, IReadOnlyList<long> targetIds, string taskId, CancellationToken cancellationToken);
}

internal sealed class CatalogueNativeRecords
{
    private readonly SupportPrivacy _privacy;
    private readonly GalleryEvidenceService _gallery;
    public CatalogueNativeRecords(SupportPrivacy privacy, GalleryEvidenceService gallery)
    { _privacy = privacy; _gallery = gallery; }
    public string Clean(string? value) => Regex.Replace(_privacy.Text(value ?? ""), @"\[(?:host|email|ip)-[0-9a-f]{10}\]", "[redacted]", RegexOptions.CultureInvariant);
    public JsonObject Metadata(DatabaseRow row) => new()
    { ["description"] = Clean(row.Text("description")), ["author"] = Clean(row.Text("author")), ["artist"] = Clean(row.Text("artist")), ["genre"] = Clean(row.Text("genre")), ["status"] = row.Integer("status") };
    private JsonObject Fact(DatabaseRow row, GalleryEvidenceService.NativeSourceProfiles profiles)
    {
        var sid = row.Text("source_id"); var url = row.Text("source_manga_url");
        var reference = PortableIdentity.GalleryReference(url, row.Text("real_url"), row.Text("base_url")) ?? PortableIdentity.RecordId(sid, url);
        JsonObject payload;
        try { payload = JsonNode.Parse(row.Text("metadata_json", "{}")) as JsonObject ?? new(); } catch (JsonException) { payload = new(); }
        // Original _fact overrides only native id/title before source-owned cleaning.
        // Row URLs define the public reference above; they are not injected as profile proof.
        payload["id"] = row.Integer("source_manga_id");
        payload["title"] = row.Text("source_title").Length != 0 ? JsonValue.Create(row.Text("source_title")) : payload["title"]?.DeepClone() ?? JsonValue.Create("");
        var native = profiles.CleanNative(sid, payload); var galleryValue = native["_gallery_metadata"]; var gallery = NativeTagJson.Truth(galleryValue);
        if (gallery && galleryValue is not JsonObject) throw new InvalidDataException("Retained native gallery metadata is not an object.");
        static string Text(JsonNode? value) => NativeTagJson.Truth(value) ? value!.GetValue<string>() : "";
        static string First(params string[] values) => values.FirstOrDefault(value => value.Length != 0) ?? "";
        var title = First(row.Text("source_title"), Text(native["title"]));
        var raw = First(Text((galleryValue as JsonObject)?["raw_title"]), Text(native["_autosourcarr_raw_title"]), row.Text("source_title"));
        var result = new JsonObject { ["reference"] = reference, ["source_id"] = sid, ["title"] = Clean(title), ["raw_title"] = Clean(raw), ["gallery"] = gallery, ["metadata"] = Metadata(row) };
        return CatalogueFormat.Locator(result);
    }
    public List<CatalogueNativeRecord> Read(DatabaseSession c, bool writeIndex = false) => c.Savepoint(session =>
    {
        var profiles = _gallery.CaptureNativeProfiles(session); var records = new List<CatalogueNativeRecord>();
        foreach (var table in new[] { "source_matches", "source_variants" })
            foreach (var row in session.Query($"SELECT r.*,sc.name source_name,sc.display_name source_display_name,sc.base_url FROM {table} r LEFT JOIN source_config sc ON sc.source_id=r.source_id WHERE r.active=1"))
            {
                try
                {
                    var fact = Fact(row, profiles); records.Add(new(row, table, fact));
                    if (writeIndex) CatalogueIdentity.RecordBinding(session, row.Integer("entry_id"), row.Text("source_id"), row.Integer("source_manga_id"), fact.S("reference"));
                }
                catch (ArgumentException) { /* Unshareable/private public locators are omitted, never replaced with local IDs. */ }
            }
        return records;
    });
    public static List<CatalogueNativeRecord> Resolve(IEnumerable<CatalogueNativeRecord> records, JsonObject locator) => records
        .Where(row => row.SourceId == locator.S("source_id") && row.Fact.S("reference") == locator.S("reference"))
        .DistinctBy(row => (row.SourceId, row.MangaId, row.EntryId)).ToList();
    public static bool GalleryEnabled(DatabaseSession c, string sourceId)
    {
        var raw = c.Scalar("SELECT policy_json FROM gallery_policies WHERE source_id=$p0", P(sourceId)) as string;
        return raw is null || JsonNode.Parse(raw)!["enabled"]!.GetValue<bool>();
    }
    public static JsonArray Policies(DatabaseSession c) => new(c.Query("SELECT source_id,revision FROM gallery_policies ORDER BY source_id").Select(row => (JsonNode?)new JsonArray(row.Text("source_id"), row.Integer("revision"))).ToArray());
    public static JsonObject RecordPayload(CatalogueNativeRecord record)
    {
        JsonObject result;
        try { result = JsonNode.Parse(record.Row.Text("metadata_json", "{}")) as JsonObject ?? new(); } catch (JsonException) { result = new(); }
        result["id"] = record.MangaId; result["url"] = record.Row.Text("source_manga_url"); result["title"] = record.Row.Text("source_title"); return result;
    }
    public static JsonObject FallbackBindingFact(DatabaseRow binding)
    {
        JsonObject payload;
        try { payload = JsonNode.Parse(binding.Text("record_json", "{}")) as JsonObject ?? new(); } catch (JsonException) { payload = new(); }
        var sourceId = binding.Text("source_id"); var title = binding.Text("source_title", payload.S("title"));
        return CatalogueFormat.Locator(new JsonObject
        { ["reference"] = PortableIdentity.RecordId(sourceId, payload.S("url")), ["source_id"] = sourceId, ["title"] = title, ["raw_title"] = title, ["gallery"] = false,
          ["metadata"] = new JsonObject { ["description"] = payload.S("description"), ["author"] = payload.S("author"), ["artist"] = payload.S("artist"), ["genre"] = payload["genre"] is JsonArray genres ? string.Join(", ", genres.Select(item => item?.GetValue<string>() ?? "")) : payload.S("genre"), ["status"] = payload["status"]?.GetValue<int>() ?? 0 } });
    }
}
