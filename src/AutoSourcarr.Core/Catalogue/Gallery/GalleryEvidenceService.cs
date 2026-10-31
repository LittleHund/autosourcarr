using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Unicode;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Gallery;

public enum GalleryAnchorContext { AutomaticEntry, LocalAttachment }

/// <summary>Current source-owned evidence; imported catalogue facts never enter native anchor selection.</summary>
public sealed class GalleryEvidenceService(SqliteDatabase database, object sourceWriteGate)
{
    /// <summary>Share sourceWriteGate with every source binding, Separate/tag review and policy writer. Reinvoke inside the final writer gate after any delayed work.</summary>
    public JsonObject? EvaluateNative(long entryId, string candidateSourceId, JsonObject candidate, GalleryAnchorContext context = GalleryAnchorContext.AutomaticEntry, double threshold = .94)
    {
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, session =>
        {
            var snapshot = Snapshot(session); var trusted = NativePayloads(session, entryId, context, snapshot);
            var payload = GalleryEvidence.CleanNativePayload(candidateSourceId, candidate, snapshot.Source(candidateSourceId), snapshot.Policy(candidateSourceId));
            return GalleryEvidence.Compose(payload, trusted, snapshot.Source, snapshot.Policy, snapshot.Canonicalize, threshold);
        });
    }
    public IReadOnlyList<JsonObject> TrustedNativePayloads(long entryId, GalleryAnchorContext context = GalleryAnchorContext.AutomaticEntry)
    {
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, session => NativePayloads(session, entryId, context, Snapshot(session)));
    }
    public JsonObject CleanNative(string sourceId, JsonObject payload)
    {
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, session => CaptureNativeProfiles(session).CleanNative(sourceId, payload));
    }

    /// <summary>One synchronous native projection uses one real source/policy view, without the unrelated tag-review graph.</summary>
    internal NativeSourceProfiles CaptureNativeProfiles(DatabaseSession session) => NativeSourceProfiles.Capture(database, sourceWriteGate, session);

    /// <summary>Metadata cleaning data only. Kept private to one native Read; never a binding or matching authorization.</summary>
    internal sealed class NativeSourceProfiles
    {
        private readonly IReadOnlyDictionary<string, JsonObject> _sources;
        private readonly IReadOnlyDictionary<string, JsonObject> _policies;
        private NativeSourceProfiles(IReadOnlyDictionary<string, JsonObject> sources, IReadOnlyDictionary<string, JsonObject> policies)
        { _sources = sources; _policies = policies; }
        internal static NativeSourceProfiles Capture(SqliteDatabase owner, object gate, DatabaseSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            if (!Monitor.IsEntered(gate) || !owner.IsCurrentThreadSession(DatabaseFile.Catalog, session))
                throw new InvalidOperationException("Native source-profile cleaning requires this instance's shared gate and current catalogue session.");
            return session.Savepoint(c => new NativeSourceProfiles(
                c.Query("SELECT * FROM source_config").ToDictionary(row => row.Text("source_id"), GalleryStorage.Json, StringComparer.Ordinal),
                c.Query("SELECT source_id,policy_json FROM gallery_policies").ToDictionary(row => row.Text("source_id"),
                    row => JsonNode.Parse(row.Text("policy_json")) as JsonObject ?? throw new InvalidDataException("Invalid saved gallery policy"), StringComparer.Ordinal)));
        }
        internal JsonObject CleanNative(string sourceId, JsonObject payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            var source = _sources.TryGetValue(sourceId, out var current) ? (JsonObject)current.DeepClone() : new JsonObject();
            var policy = _policies.TryGetValue(sourceId, out var saved) ? saved : GalleryPolicy.Default;
            return GalleryEvidence.CleanNativePayload(sourceId, payload, source, policy);
        }
    }

    /// <summary>For portable-file metadata partitioning only. This delegate never creates a source binding or authorizes a native merge/split.</summary>
    public CatalogueGalleryEvidence CatalogueAdapter => EvaluateCatalogueMetadata;
    public bool? EvaluateCatalogueMetadata(JsonObject candidateFact, IReadOnlyList<JsonObject> existingFacts)
    {
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, session =>
        {
            var snapshot = Snapshot(session);
            JsonObject Payload(JsonObject fact)
            {
                var sid = GalleryStorage.Text(fact["source_id"]); var payload = fact["metadata"] is JsonObject metadata ? (JsonObject)metadata.DeepClone() : new JsonObject();
                payload["sourceId"] = sid; var raw = GalleryStorage.Text(fact["raw_title"]); payload["title"] = raw.Length > 0 ? raw : GalleryStorage.Text(fact["title"]);
                var source = snapshot.Source(sid); if (GalleryStorage.Flag(fact["gallery"])) source["metadata_profile"] = "gallery";
                return GalleryMetadata.Enrich(payload, GalleryPolicy.SourceProfile(source, snapshot.Policy(sid)));
            }
            var evidence = GalleryEvidence.Compose(Payload(candidateFact), existingFacts.Select(Payload).ToArray(), snapshot.Source, snapshot.Policy, snapshot.Canonicalize);
            return evidence is null ? (bool?)null : GalleryStorage.Flag(evidence["eligible"]);
        });
    }

    private static IReadOnlyList<JsonObject> NativePayloads(DatabaseSession session, long entryId, GalleryAnchorContext context, DecisionSnapshot snapshot)
    {
        if (context is not (GalleryAnchorContext.AutomaticEntry or GalleryAnchorContext.LocalAttachment)) throw new ArgumentOutOfRangeException(nameof(context));
        entryId = Canonical(session, entryId);
        if (session.Scalar("SELECT 1 FROM entries WHERE id=$entry", ("$entry", entryId)) is null) throw new KeyNotFoundException("Entry not found");
        var records = session.Query("""
            SELECT id record_id,0 is_variant,source_id,source_manga_id,source_title,description,author,artist,genre,metadata_json,match_type,locked
            FROM source_matches WHERE entry_id=$entry AND active=1
            UNION ALL SELECT id record_id,1 is_variant,source_id,source_manga_id,source_title,description,author,artist,genre,metadata_json,match_type,locked
            FROM source_variants WHERE entry_id=$entry AND active=1
            """, ("$entry", entryId));
        var primaries = records.Where(row => !row.Flag("is_variant")).ToArray(); var anchor = (primaries.Length > 0 ? primaries : records).MinBy(row => row.Integer("record_id"));
        var result = new List<JsonObject>();
        foreach (var row in records)
        {
            var matchType = UnicodeCaseFold.Fold(row.Text("match_type"));
            if (!ReferenceEquals(row, anchor) && !row.Flag("locked") && matchType is not ("manual" or "split-duplicate") && !(context == GalleryAnchorContext.AutomaticEntry && matchType == "import")) continue;
            JsonObject payload;
            try { payload = JsonNode.Parse(row.Text("metadata_json", "{}")) as JsonObject ?? new(); } catch (JsonException) { payload = new(); }
            void Default(string name, JsonNode? value) { if (!payload.ContainsKey(name)) payload[name] = value; }
            Default("title", JsonValue.Create(row.Text("source_title"))); Default("id", JsonValue.Create(row.Integer("source_manga_id")));
            foreach (var field in new[] { "description", "author", "artist", "genre" }) Default(field, JsonValue.Create(row.Text(field)));
            var sid = row.Text("source_id"); result.Add(GalleryEvidence.CleanNativePayload(sid, payload, snapshot.Source(sid), snapshot.Policy(sid)));
        }
        return result;
    }
    private static DecisionSnapshot Snapshot(DatabaseSession session)
    {
        var sources = session.Query("SELECT * FROM source_config").ToDictionary(row => row.Text("source_id"), GalleryStorage.Json, StringComparer.Ordinal);
        var policies = session.Query("SELECT source_id,policy_json FROM gallery_policies").ToDictionary(row => row.Text("source_id"), row => JsonNode.Parse(row.Text("policy_json")) as JsonObject ?? throw new InvalidDataException("Invalid saved gallery policy"), StringComparer.Ordinal);
        // One statement observes an internally consistent review/tag snapshot. No process cache can be repopulated by an older reader transaction.
        var joined = session.Query("""
            SELECT r.*,l.namespace lns,l.name lname,t.namespace rns,t.name rname FROM tag_reviews r
            JOIN tags l ON l.id=r.left_id JOIN tags t ON t.id=r.right_id ORDER BY r.left_id,r.right_id
            """);
        var tags = new Dictionary<long, JsonObject>(); var reviews = new List<JsonObject>();
        foreach (var row in joined)
        {
            var left = row.Integer("left_id"); var right = row.Integer("right_id");
            tags[left] = new() { ["id"] = left, ["namespace"] = row.Text("lns"), ["name"] = row.Text("lname") };
            tags[right] = new() { ["id"] = right, ["namespace"] = row.Text("rns"), ["name"] = row.Text("rname") };
            reviews.Add(new() { ["left_id"] = left, ["right_id"] = right, ["decision"] = row.Text("decision") });
        }
        return new(sources, policies, GalleryTagEquivalence.Mapping(tags.Values.ToArray(), reviews));
    }
    private static long Canonical(DatabaseSession session, long entryId)
    {
        var seen = new HashSet<long>();
        while (seen.Add(entryId))
        {
            var value = session.Scalar("SELECT canonical_entry_id FROM entry_aliases WHERE old_entry_id=$entry", ("$entry", entryId));
            if (value is null) return entryId; entryId = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        throw new InvalidDataException("Entry alias cycle; catalogue identity requires repair");
    }
    private sealed record DecisionSnapshot(IReadOnlyDictionary<string, JsonObject> Sources, IReadOnlyDictionary<string, JsonObject> Policies, IReadOnlyDictionary<string, string> Mapping)
    {
        public JsonObject Source(string id) => Sources.TryGetValue(id, out var source) ? (JsonObject)source.DeepClone() : new();
        public JsonObject Policy(string id) => Policies.TryGetValue(id, out var policy) ? policy : GalleryPolicy.Default;
        public IReadOnlySet<string> Canonicalize(IEnumerable<JsonNode?> tags) => GalleryTagEquivalence.CanonicalKeys(tags, Mapping);
    }
}
