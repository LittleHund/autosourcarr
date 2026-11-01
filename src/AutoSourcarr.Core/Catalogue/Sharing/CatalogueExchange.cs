using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Catalogue.Gallery;
using static AutoSourcarr.Core.Catalogue.Sharing.SharingJson;

namespace AutoSourcarr.Core.Catalogue.Sharing;

/// <summary>Additive catalogue exchange. All mutations join one owned SQLite transaction.</summary>
public sealed partial class CatalogueExchange
{
    private readonly SqliteDatabase _database;
    private readonly object _sourceWriteGate;
    private readonly CatalogueNativeRecords _native;
    private readonly TimeProvider _clock;
    private readonly CatalogueGalleryEvidence? _galleryEvidence;
    private readonly ICatalogueReviewedStructure? _structure;
    private readonly Action? _invalidate;
    public CatalogueExchange(SqliteDatabase database, object sourceWriteGate, SupportPrivacy privacy, TimeProvider? clock = null,
        CatalogueGalleryEvidence? galleryEvidence = null, ICatalogueReviewedStructure? structure = null, Action? invalidate = null)
    { _database = database; _sourceWriteGate = sourceWriteGate; _native = new(privacy, new GalleryEvidenceService(database, sourceWriteGate)); _clock = clock ?? TimeProvider.System; _galleryEvidence = galleryEvidence; _structure = structure; _invalidate = invalidate; }
    /// <summary>The phase-8 decision log. Imports, applied shared choices and record/identity reviews record here, with
    /// their actor, inside the same catalogue write as the change (DecisionLog joins the open write).</summary>
    public IDecisionLog? Log { get; init; }
    private void Record(string kind, DecisionActor? actor, IEnumerable<long> entries, IEnumerable<string> records, string reason, JsonObject? after = null)
        => Log?.Record(new DecisionEvent(kind, actor ?? DecisionActor.Automatic, entries.Distinct().ToArray(), records.Distinct(StringComparer.Ordinal).ToArray(), reason, After: after));
    public void Initialize() => InitializeSchema(_database);
    /// <summary>The shared native/portable catalogue schema prerequisite. Does not
    /// import facts, create a match or run structural decisions.</summary>
    public static void InitializeSchema(SqliteDatabase database)
    { foreach (var schema in new[] { "catalogue-identity.sql", "gallery-policy.sql", "identity-review.sql" }) new SchemaMigrator(database).EnsureFeatureSchema(schema); }
    private double Now => _clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
    public JsonObject Export(long after = 0, int limit = 5000, bool includeDecisions = true)
    {
        if (after < 0 || limit is < 1 or > 5000) throw new ArgumentException("Choose an export size from 1 to 5000 entries");
        lock (_sourceWriteGate) return _database.Write(DatabaseFile.Catalog, c =>
        {
            var origin = CatalogueIdentity.Origin(c); var all = c.Query("SELECT id FROM entries ORDER BY id").Select(row => row.Integer("id")).ToList();
            foreach (var id in all) CatalogueIdentity.Ensure(c, id);
            var selected = all.Where(id => id > after).Take(limit).ToHashSet(); var native = _native.Read(c, writeIndex: true); var entries = new List<JsonObject>();
            foreach (var id in selected.Order())
            {
                var row = c.Query("SELECT * FROM entries WHERE id=$p0", P(id)).Single();
                var refs = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var provider in c.Query("SELECT provider,external_id,identity_scope FROM identity_links WHERE entry_id=$p0", P(id)))
                {
                    if (!PortableIdentity.Providers.TryGetValue(provider.Text("provider"), out var scope) || scope != provider.Text("identity_scope")) continue;
                    try { refs.Add($"asc1:{scope}:{provider.Text("provider")}:{PortableIdentity.ExternalId(provider.Text("provider"), provider.Text("external_id"))}"); } catch (ArgumentException) { }
                }
                refs.UnionWith(c.Query("SELECT reference FROM catalogue_public_refs WHERE entry_id=$p0", P(id)).Select(value => value.Text("reference")));
                var facts = new Dictionary<(string, string), JsonObject>();
                foreach (var record in native.Where(record => record.EntryId == id)) facts[(record.Fact.S("reference"), record.SourceId)] = record.Fact;
                foreach (var retained in c.Query("SELECT fact_json FROM catalogue_source_facts WHERE entry_id=$p0", P(id)))
                { var fact = JsonNode.Parse(retained.Text("fact_json"))!.AsObject(); facts.TryAdd((fact.S("reference"), fact.S("source_id")), fact); }
                refs.UnionWith(facts.Values.Select(fact => fact.S("reference"))); var primary = CatalogueIdentity.Ensure(c, id);
                foreach (var fact in facts.Values) c.Execute("INSERT OR IGNORE INTO catalogue_uid_records VALUES($p0,$p1,$p2)", P(primary, fact.S("source_id"), fact.S("reference")));
                var aliases = CatalogueIdentity.Ids(c, id);
                entries.Add(new() { ["uid"] = primary, ["aliases"] = Strings(aliases), ["lineage"] = CatalogueIdentity.Lineage(c, aliases), ["title"] = _native.Clean(row.Text("preferred_title")),
                    ["titles"] = Strings(c.Query("SELECT title FROM titles WHERE entry_id=$p0 ORDER BY id", P(id)).Select(title => _native.Clean(title.Text("title"))).Distinct(StringComparer.Ordinal)),
                    ["metadata"] = _native.Metadata(row), ["references"] = Strings(refs), ["sources"] = Objects(facts.Values) });
            }
            var decisions = includeDecisions ? CatalogueDecisionExport.Collect(c, _native, native, selected, after == 0) : new JsonArray();
            var seen = entries.Select(entry => (entry.S("uid"), CatalogueFormat.Digest(entry))).ToHashSet();
            foreach (var row in c.Query("SELECT uid,digest,record_json FROM catalogue_import_records WHERE state<>'ignored' ORDER BY uid,digest"))
            {
                var original = JsonNode.Parse(row.Text("record_json"))!.AsObject(); var item = ResolvedRecord(original, Resolution(c, original));
                var owners = CatalogueIdentity.Targets(c, [item.S("uid")]);
                if (owners.Count != 0 && !owners.Any(selected.Contains) || owners.Count == 0 && after != 0) continue;
                if (seen.Add((item.S("uid"), CatalogueFormat.Digest(item)))) entries.Add(item);
            }
            var bundle = CatalogueFormat.Validate(new JsonObject { ["format"] = CatalogueFormat.Format, ["version"] = CatalogueFormat.LegacyVersion, ["origin"] = origin, ["entries"] = Objects(entries), ["decisions"] = decisions });
            var file = CatalogueFormat.Wrap(bundle, _clock.GetUtcNow());
            if (Encoding.UTF8.GetByteCount(CatalogueFormat.Canonical(file)) > CatalogueFormat.MaximumBytes) throw new ArgumentException("Export exceeds 64 MiB; choose a smaller entry batch");
            var cursor = selected.Count != 0 && all.Any(id => id > selected.Max()) ? (long?)selected.Max() : null;
            return new JsonObject { ["catalogue"] = file, ["next_cursor"] = cursor, ["total"] = all.Count, ["entries"] = entries.Count, ["decisions"] = decisions.Count };
        });
    }
    private static JsonArray SourceKeys(JsonObject record) => new(record.A("sources").Select(fact => (fact!.S("source_id"), fact.S("reference"))).OrderBy(pair => pair.Item1, StringComparer.Ordinal).ThenBy(pair => pair.Item2, StringComparer.Ordinal).Select(pair => (JsonNode?)new JsonArray(pair.Item1, pair.Item2)).ToArray());
    private static JsonObject? Resolution(DatabaseSession c, JsonObject record)
    {
        if (c.Scalar("SELECT resolution_json FROM catalogue_record_resolutions WHERE uid=$p0 AND digest=$p1", P(record.S("uid"), CatalogueFormat.Digest(record))) is string raw) return JsonNode.Parse(raw)!.AsObject();
        var keys = CatalogueFormat.Canonical(SourceKeys(record));
        foreach (var row in c.Query("SELECT resolution_json FROM catalogue_record_resolutions WHERE uid=$p0 ORDER BY rowid DESC LIMIT 64", P(record.S("uid"))))
        { var value = JsonNode.Parse(row.Text("resolution_json"))!.AsObject(); if (value["remap"] is JsonObject && CatalogueFormat.Canonical(value["source_keys"]) == keys) return value; }
        return null;
    }
    private static JsonObject ResolvedRecord(JsonObject record, JsonObject? resolution)
    { var result = record.Copy(); if (resolution?["remap"] is JsonObject remap) foreach (var pair in remap) result[pair.Key] = pair.Value?.DeepClone(); return result; }
    private List<JsonObject> Units(DatabaseSession c, JsonObject entry)
    {
        var sources = entry.A("sources").Select(fact => fact!.AsObject()).ToList();
        if (sources.Count < 2)
        {
            var result = entry.Copy();
            if (sources.Count == 1 && sources[0]["gallery"]!.GetValue<bool>() && !CatalogueNativeRecords.GalleryEnabled(c, sources[0].S("source_id"))) result["title"] = Nonempty(sources[0].S("raw_title"), sources[0].S("title"), entry.S("title"));
            return [result];
        }
        var groups = new List<List<JsonObject>>();
        foreach (var fact in sources)
        {
            List<JsonObject>? chosen = null;
            foreach (var group in groups)
            {
                var galleries = group.Append(fact).Where(value => value["gallery"]!.GetValue<bool>()).ToList();
                if (galleries.Any(value => !CatalogueNativeRecords.GalleryEnabled(c, value.S("source_id")))) continue;
                if (galleries.Count != 0 && _galleryEvidence is null) throw new InvalidOperationException("Native gallery metadata/evidence provider is required to evaluate this imported group; no grouping was applied.");
                // Current source profiles can identify a gallery even when the foreign hint is false.
                // The provider returns null for ordinary records; every pair must reach that check.
                if (_galleryEvidence?.Invoke(fact.Copy(), group.Select(value => value.Copy()).ToArray()) is not false) { chosen = group; break; }
            }
            if (chosen is null) { chosen = []; groups.Add(chosen); } chosen.Add(fact);
        }
        if (groups.Count == 1) return [entry.Copy()];
        var units = new List<JsonObject>();
        foreach (var facts in groups)
        {
            var value = entry.Copy(); var factEntry = new JsonObject { ["sources"] = Objects(facts) }; var key = PortableUuid.Entry(entry.S("uid"), CatalogueFormat.Canonical(SourceKeys(factEntry)));
            var refs = facts.Select(fact => fact.S("reference")).ToHashSet(StringComparer.Ordinal);
            value["uid"] = key; value["aliases"] = Strings(entry.A("aliases").Select(item => item!.GetValue<string>()).Append(entry.S("uid")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            var lineage = (JsonArray)entry.A("lineage").DeepClone(); lineage.Add(new JsonObject { ["uid"] = key, ["records"] = Objects(facts.Select(fact => new JsonObject { ["source_id"] = fact.S("source_id"), ["reference"] = fact.S("reference") })) }); value["lineage"] = lineage;
            value["title"] = facts.Count == 1 ? Nonempty(facts[0].S("raw_title"), facts[0].S("title"), entry.S("title")) : entry.S("title");
            value["sources"] = Objects(facts); value["references"] = Strings(entry.A("references").Select(item => item!.GetValue<string>()).Where(reference => PortableIdentity.Parse(reference).Scope is "work" or "edition" || refs.Contains(reference)).Concat(refs).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            value["metadata"] = (facts.Count == 1 ? facts[0]["metadata"] : entry["metadata"])!.DeepClone(); units.Add(value);
        }
        return units;
    }
    private static string Nonempty(params string[] values) => values.FirstOrDefault(value => value.Length != 0) ?? "";
    private long Add(DatabaseSession c, JsonObject record)
    {
        var value = record.O("metadata"); var stamp = Stamp();
        c.Execute("INSERT INTO entries(preferred_title,description,author,artist,genre,status,created_at,updated_at) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p6)", P(record.S("title"), value.S("description"), value.S("author"), value.S("artist"), value.S("genre"), long.Parse(value["status"]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture), stamp));
        var id = (long)c.Scalar("SELECT last_insert_rowid()")!; CatalogueIdentity.Ensure(c, id, record.S("uid")); c.Execute("INSERT INTO catalogue_shared_entries VALUES($p0)", P(id)); return id;
    }
    private static void Associate(DatabaseSession c, long id, JsonObject record)
    {
        CatalogueIdentity.Ensure(c, id); CatalogueIdentity.Link(c, id, record.A("aliases").Select(uid => uid!.GetValue<string>()).Prepend(record.S("uid")));
        foreach (var line in record.A("lineage")) foreach (var anchor in line.A("records")) c.Execute("INSERT OR IGNORE INTO catalogue_uid_records VALUES($p0,$p1,$p2)", P(line.S("uid"), anchor.S("source_id"), anchor.S("reference")));
        c.Execute("INSERT OR IGNORE INTO catalogue_uid_records SELECT $p0,source_id,reference FROM catalogue_record_bindings WHERE entry_id=$p1", P(CatalogueIdentity.Ensure(c, id), id));
        foreach (var reference in record.A("references").Select(item => item!.GetValue<string>()).Concat(record.A("sources").Select(fact => fact.S("reference"))).Distinct(StringComparer.Ordinal)) c.Execute("INSERT OR IGNORE INTO catalogue_public_refs VALUES($p0,$p1)", P(id, reference));
        foreach (var fact in record.A("sources")) c.Execute("INSERT OR IGNORE INTO catalogue_source_facts VALUES($p0,$p1,$p2,$p3)", P(id, fact.S("reference"), fact.S("source_id"), CatalogueFormat.Canonical(fact)));
        foreach (var title in record.A("titles").Select(item => item!.GetValue<string>()).Prepend(record.S("title")).Distinct(StringComparer.Ordinal)) if (title.Trim().Length != 0) c.Execute("INSERT OR IGNORE INTO titles(entry_id,title,normalized,title_type) VALUES($p0,$p1,$p2,'cached')", P(id, title, TextMatcher.NormalizeTitle(title)));
    }
}
