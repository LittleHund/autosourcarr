using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Catalogue.Structure.StructureSql;

namespace AutoSourcarr.Core.Catalogue.Structure;

/// <summary>Explicit native catalogue mutations and durable cross-file recovery. Never contacts an upstream service.</summary>
public sealed partial class CatalogueStructure : ICatalogueReviewedStructure
{
    private sealed record Owned(DatabaseRow Row, string Storage)
    { public NativeSourceRecord Key => new(Row.Text("source_id"), Row.Integer("source_manga_id")); public long EntryId => Row.Integer("entry_id"); }
    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly TaskRegistry _tasks;
    private readonly GalleryEvidenceService _gallery;
    private readonly GalleryPolicyRepository _policies;
    private readonly Action<DatabaseSession, long, string> _metadata;
    private readonly Action<long> _visibility;
    private readonly Action<DatabaseSession, long, long, string> _display;
    private readonly Func<DatabaseSession, long, JsonObject> _snapshot;
    private readonly ICatalogueGalleryReplay? _galleryReplay;
    private readonly Action<IReadOnlyList<long>> _invalidate;
    private readonly TimeProvider _clock;
    public CatalogueStructure(SqliteDatabase database, object sourceWriteGate, TaskRegistry tasks, GalleryEvidenceService gallery,
        Action<DatabaseSession, long, string> recomputeMetadata, Action<long> reconcileVisibility,
        Action<DatabaseSession, long, long, string> reparentDisplay, Func<DatabaseSession, long, JsonObject> nativeSnapshot,
        Action<IReadOnlyList<long>> invalidate, ICatalogueGalleryReplay? galleryReplay = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(recomputeMetadata); ArgumentNullException.ThrowIfNull(reconcileVisibility); ArgumentNullException.ThrowIfNull(reparentDisplay); ArgumentNullException.ThrowIfNull(nativeSnapshot); ArgumentNullException.ThrowIfNull(invalidate);
        _database = database; _gate = sourceWriteGate; _tasks = tasks; _gallery = gallery; _policies = new(database, sourceWriteGate);
        _metadata = recomputeMetadata; _visibility = reconcileVisibility; _display = reparentDisplay; _snapshot = nativeSnapshot; _galleryReplay = galleryReplay; _invalidate = invalidate; _clock = clock ?? TimeProvider.System;
    }
    private string Stamp() => _clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    private static long Canonical(DatabaseSession c, long id) => PortableIdentityResolver.Canonical(c, id);
    private static bool Exists(DatabaseSession c, long id) => c.Scalar("SELECT 1 FROM entries WHERE id=$p0", P(id)) is not null;
    public void ApplyReviewed(string decisionUid, string decisionDigest, string revision, JsonObject decision, IReadOnlyList<CatalogueNativeRecord> records, IReadOnlyList<long> targetIds, string taskId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var task = _tasks.PersistentSnapshot().SingleOrDefault(row => row.Id == taskId);
            if (task is null || task.State != "running" || task.Origin != "manual" || task.Kind != "catalogue-choice") throw new InvalidOperationException("A running visible catalogue-choice task owns structural review");
            ReconcilePending(cancellationToken);
            AutoSourcarr.Core.Catalogue.Sharing.CatalogueFormat.Decision(decision);
            if (decision["uid"]!.GetValue<string>() != decisionUid || AutoSourcarr.Core.Catalogue.Sharing.CatalogueFormat.Digest(decision) != decisionDigest) throw new InvalidOperationException("Reviewed catalogue decision identity changed");
            var current = _database.Read(DatabaseFile.Catalog, c =>
            {
                var result = new List<Owned>();
                foreach (var record in records)
                {
                    if (!decision["records"]!.AsArray().Any(value => Text(value, "source_id") == record.SourceId && Text(value, "reference") == Text(record.Fact, "reference"))) throw new InvalidOperationException("Reviewed decision does not identify this exact source record");
                    var owned = Owners(c, [new(record.SourceId, record.MangaId)]);
                    if (!owned.TryGetValue(new(record.SourceId, record.MangaId), out var owner)) throw new InvalidOperationException("A reviewed source record is no longer attached");
                    var row = Records(c, owner).FirstOrDefault(value => value.Key == new NativeSourceRecord(record.SourceId, record.MangaId)) ?? throw new InvalidOperationException("Reviewed source ownership changed");
                    var reference = AutoSourcarr.Core.Catalogue.Sharing.PortableIdentity.GalleryReference(row.Row.Text("source_manga_url"), row.Row.Text("real_url"), row.Row.Text("base_url"))
                        ?? AutoSourcarr.Core.Catalogue.Sharing.PortableIdentity.RecordId(row.Key.SourceId, row.Row.Text("source_manga_url"));
                    if (reference != record.Fact["reference"]!.GetValue<string>()) throw new InvalidOperationException("Reviewed public source anchor changed"); result.Add(row);
                }
                return result.DistinctBy(row => row.Key).ToList();
            });
            if (current.Count == 0 || current.Count != decision["records"]!.AsArray().Count) throw new InvalidOperationException("Resolve every reviewed source record exactly once");
            var mapped = _database.Read(DatabaseFile.Catalog, c => CatalogueIdentity.Targets(c, decision["entries"]!.AsArray().Select(value => value!.GetValue<string>())));
            if (!mapped.SequenceEqual(targetIds.Order())) throw new InvalidOperationException("Reviewed local target mapping changed");
            var kind = decision["kind"]!.GetValue<string>(); var owners = current.Select(row => row.EntryId).Distinct().Order().ToArray();
            if (owners.Length > 32 || kind == "group" && current.Count > 64) throw new InvalidOperationException("Review the records in smaller local groups");
            if (kind == "group")
            {
                _database.Read(DatabaseFile.Catalog, c =>
                {
                    foreach (var record in current) if (c.Scalar("SELECT 1 FROM manual_match_bindings WHERE source_id=$p0 AND source_manga_id=$p1", P(record.Key.SourceId, record.Key.MangaId)) is not null) throw new InvalidOperationException("A local manual source binding remains authoritative");
                    for (var i = 0; i < owners.Length; i++) for (var j = i + 1; j < owners.Length; j++)
                    {
                        if (Review(c, owners[i], owners[j]) == "not_duplicate") throw new InvalidOperationException("A local Separate decision is retained");
                        EditionGate(NativeSnapshot(c, owners[i]), NativeSnapshot(c, owners[j]));
                    }
                    return 0;
                });
                var payloads = current.Select(row => _gallery.CleanNative(row.Key.SourceId, GalleryPayload(row.Row))).ToArray(); GalleryPolicy.GroupingGate(payloads, _policies.Effective);
                if (payloads.Any(payload => payload["_gallery_metadata"] is JsonObject))
                    for (var i = 0; i < current.Count; i++) for (var j = i + 1; j < current.Count; j++)
                        if (_database.Read(DatabaseFile.Catalog, c => NativeGalleryPair(c, payloads[i], payloads[j]))?["eligible"]?.GetValue<bool>() != true) throw new InvalidOperationException("Current native gallery evidence does not support the reviewed group");
                var target = owners[0]; foreach (var other in owners.Skip(1)) { cancellationToken.ThrowIfCancellationRequested(); target = Merge(target, other, "reviewed-catalogue-group"); }
                _database.Write(DatabaseFile.Catalog, c => { CatalogueIdentity.Link(c, target, decision["entries"]!.AsArray().Select(value => value!.GetValue<string>())); return 0; });
            }
            else if (kind == "source-match")
            {
                if (mapped.Count != 1 || current.Count != 1) throw new InvalidOperationException("Choose one mapped local source target");
                var target = mapped[0]; var record = current[0];
                if (target != record.EntryId)
                {
                    var allowed = _database.Read(DatabaseFile.Catalog, c => CatalogueIdentity.Unverified(c, target) && c.Scalar("SELECT 1 FROM catalogue_source_facts WHERE entry_id=$p0 AND source_id=$p1 AND reference=$p2", P(target, record.Key.SourceId, records[0].Fact["reference"]!.GetValue<string>())) is not null);
                    if (!allowed) throw new InvalidOperationException("This move needs an explicit local Match matrix review"); Merge(target, record.EntryId, "reviewed-catalogue-source");
                }
            }
            else if (kind == "separate")
            {
                if (mapped.Count < 1 || current.Count != 1) throw new InvalidOperationException("Choose a mapped target and one source record");
                if (current[0].EntryId == mapped[0]) Split(mapped[0], [current[0].Key], manual: true);
            }
            else throw new InvalidOperationException("Unsupported reviewed structural choice");
        }
    }
    private JsonObject? NativeGalleryPair(DatabaseSession c, JsonObject left, JsonObject right)
    {
        var tags = new Dictionary<long, JsonObject>(); var reviews = new List<JsonObject>();
        foreach (var row in c.Query("SELECT r.*,l.namespace lns,l.name lname,t.namespace rns,t.name rname FROM tag_reviews r JOIN tags l ON l.id=r.left_id JOIN tags t ON t.id=r.right_id ORDER BY r.left_id,r.right_id"))
        {
            var first = row.Integer("left_id"); var second = row.Integer("right_id"); tags[first] = new() { ["id"] = first, ["namespace"] = row.Text("lns"), ["name"] = row.Text("lname") }; tags[second] = new() { ["id"] = second, ["namespace"] = row.Text("rns"), ["name"] = row.Text("rname") };
            reviews.Add(new() { ["left_id"] = first, ["right_id"] = second, ["decision"] = row.Text("decision") });
        }
        var mapping = GalleryTagEquivalence.Mapping(tags.Values.ToArray(), reviews);
        JsonObject Source(string sid) { var row = c.Query("SELECT * FROM source_config WHERE source_id=$p0", P(sid)).FirstOrDefault(); return row is null ? new() : Json(row); }
        return GalleryEvidence.Compose(left, [right], Source, _policies.Effective, values => GalleryTagEquivalence.CanonicalKeys(values, mapping));
    }
    private static string Review(DatabaseSession c, long left, long right)
    { left = Canonical(c, left); right = Canonical(c, right); if (left == right) return "duplicate"; if (left > right) (left, right) = (right, left); return c.Scalar("SELECT decision FROM duplicate_reviews WHERE left_entry_id=$p0 AND right_entry_id=$p1", P(left, right)) as string ?? ""; }
    private static void EditionGate(JsonObject left, JsonObject right)
    {
        var lc = Text(left, "color_mode", "unknown"); var rc = Text(right, "color_mode", "unknown");
        if (lc == "mixed" || rc == "mixed" || lc is "colored" or "monochrome" && rc is "colored" or "monochrome" && lc != rc) throw new InvalidOperationException("Colored/monochrome editions are separate works");
        var lm = Text(left, "media_mode", "unknown"); var rm = Text(right, "media_mode", "unknown");
        if (lm == "mixed" || rm == "mixed" || lm is "manga" or "webtoon" && rm is "manga" or "webtoon" && lm != rm) throw new InvalidOperationException("Manga/webtoon adaptations are separate works");
    }
    private static List<Owned> Records(DatabaseSession c, long id)
    {
        var result = new List<Owned>(); foreach (var table in new[] { "source_matches", "source_variants" })
            result.AddRange(c.Query($"SELECT r.*,COALESCE(NULLIF(sc.display_name,''),sc.name,r.source_id) source_name,sc.base_url FROM {table} r LEFT JOIN source_config sc ON sc.source_id=r.source_id WHERE r.entry_id=$p0 AND r.active=1 ORDER BY r.id", P(id)).Select(row => new Owned(row, table)));
        return result;
    }
    private static Dictionary<NativeSourceRecord, long> Owners(DatabaseSession c, IEnumerable<NativeSourceRecord> records)
    {
        var result = new Dictionary<NativeSourceRecord, long>();
        foreach (var record in records.Distinct())
        {
            var owners = c.Query("SELECT entry_id FROM source_matches WHERE source_id=$p0 AND source_manga_id=$p1 AND active=1 UNION ALL SELECT entry_id FROM source_variants WHERE source_id=$p0 AND source_manga_id=$p1 AND active=1", P(record.SourceId, record.MangaId)).Select(row => Canonical(c, row.Integer("entry_id"))).Distinct().ToArray();
            if (owners.Length > 1) throw new InvalidOperationException("Native source ownership is ambiguous; recovery remains pending"); if (owners.Length == 1) result.Add(record, owners[0]);
        }
        return result;
    }
    private void PersistEdition(DatabaseSession c, long id)
    {
        var snapshot = NativeSnapshot(c, id); var color = Text(snapshot, "color_mode"); var media = Text(snapshot, "media_mode");
        if (color is not ("unknown" or "colored" or "monochrome" or "mixed") || media is not ("unknown" or "manga" or "webtoon" or "mixed")) throw new InvalidDataException("Native edition snapshot is malformed");
        if (snapshot["color_confidence"]?.GetValueKind() != System.Text.Json.JsonValueKind.Number || !double.TryParse(snapshot["color_confidence"]!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence) || !double.IsFinite(confidence) || confidence is < 0 or > 1) throw new InvalidDataException("Native color confidence is invalid");
        c.Execute("UPDATE entries SET color_mode=$p0,color_confidence=$p1,media_mode=$p2 WHERE id=$p3", P(color, confidence, media, id));
    }
    private JsonObject NativeSnapshot(DatabaseSession c, long id)
    {
        var snapshot = _snapshot(c, id) ?? throw new InvalidDataException("Native snapshot owner returned no evidence");
        if (!long.TryParse(snapshot["entry_id"]?.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var owner) || owner != id || snapshot["color_mode"]?.GetValueKind() != System.Text.Json.JsonValueKind.String || snapshot["media_mode"]?.GetValueKind() != System.Text.Json.JsonValueKind.String) throw new InvalidDataException("Native snapshot identity or edition evidence is incomplete");
        return snapshot;
    }
}
