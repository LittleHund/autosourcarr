using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Catalogue.Sharing.SharingJson;

namespace AutoSourcarr.Core.Catalogue.Sharing;

/// <summary>Portable lineage lives beside native IDs and never grants native matching authority.</summary>
public static class CatalogueIdentity
{
    public static string Origin(DatabaseSession c)
    {
        if (c.Scalar("SELECT uid FROM catalogue_origin WHERE id=1") is string value) return value;
        value = CatalogueFormat.Uid("origin"); c.Execute("INSERT INTO catalogue_origin VALUES(1,$p0)", P(value)); return value;
    }
    public static string Ensure(DatabaseSession c, long entryId, string? preferred = null)
    {
        if (c.Scalar("SELECT uid FROM catalogue_entry_ids WHERE entry_id=$p0 AND primary_id=1", P(entryId)) is string value) return value;
        value = preferred is null ? CatalogueFormat.Uid() : CatalogueFormat.CheckUid(preferred);
        c.Execute("UPDATE catalogue_entry_ids SET primary_id=0 WHERE uid=$p0 AND primary_id=1 AND NOT EXISTS(SELECT 1 FROM entries e WHERE e.id=catalogue_entry_ids.entry_id)", P(value));
        c.Execute("INSERT INTO catalogue_entry_ids VALUES($p0,$p1,1) ON CONFLICT(uid,entry_id) DO UPDATE SET primary_id=1", P(value, entryId)); return value;
    }
    public static void Link(DatabaseSession c, long entryId, IEnumerable<string> values)
    { foreach (var value in values.Distinct(StringComparer.Ordinal)) c.Execute("INSERT OR IGNORE INTO catalogue_entry_ids VALUES($p0,$p1,0)", P(CatalogueFormat.CheckUid(value), entryId)); }
    public static IReadOnlyList<string> Ids(DatabaseSession c, long entryId) => c.Query("SELECT uid FROM catalogue_entry_ids WHERE entry_id=$p0 ORDER BY primary_id DESC,uid", P(entryId)).Select(row => row.Text("uid")).ToArray();
    public static List<long> Targets(DatabaseSession c, IEnumerable<string> values)
    {
        var result = new SortedSet<long>();
        foreach (var value in values.Distinct(StringComparer.Ordinal))
        {
            var primary = c.Query("SELECT i.entry_id FROM catalogue_entry_ids i JOIN entries e ON e.id=i.entry_id WHERE i.uid=$p0 AND i.primary_id=1", P(value));
            if (primary.Count != 0) { result.UnionWith(primary.Select(row => row.Integer("entry_id"))); continue; }
            var semantics = c.Query("SELECT source_id,reference FROM catalogue_uid_records WHERE uid=$p0", P(value));
            if (semantics.Count == 0)
            { result.UnionWith(c.Query("SELECT i.entry_id FROM catalogue_entry_ids i JOIN entries e ON e.id=i.entry_id WHERE i.uid=$p0", P(value)).Select(row => row.Integer("entry_id"))); continue; }
            foreach (var anchor in semantics)
            {
                var owners = c.Query("""
                    SELECT b.entry_id FROM catalogue_record_bindings b WHERE b.source_id=$p0 AND b.reference=$p1 AND
                    (EXISTS(SELECT 1 FROM source_matches m WHERE m.entry_id=b.entry_id AND m.source_id=b.source_id AND m.source_manga_id=b.source_manga_id AND m.active=1)
                    OR EXISTS(SELECT 1 FROM source_variants v WHERE v.entry_id=b.entry_id AND v.source_id=b.source_id AND v.source_manga_id=b.source_manga_id AND v.active=1))
                    """, P(anchor.Text("source_id"), anchor.Text("reference")));
                if (owners.Count == 0) owners = c.Query("SELECT f.entry_id FROM catalogue_source_facts f JOIN entries e ON e.id=f.entry_id WHERE f.source_id=$p0 AND f.reference=$p1", P(anchor.Text("source_id"), anchor.Text("reference")));
                result.UnionWith(owners.Select(row => row.Integer("entry_id")));
            }
        }
        return result.ToList();
    }
    public static void RecordBinding(DatabaseSession c, long entryId, string sourceId, long mangaId, string reference)
    {
        c.Execute("DELETE FROM catalogue_record_bindings WHERE source_id=$p0 AND source_manga_id=$p1 AND reference<>$p2", P(sourceId, mangaId, reference));
        c.Execute("INSERT INTO catalogue_record_bindings VALUES($p0,$p1,$p2,$p3) ON CONFLICT(source_id,source_manga_id,reference) DO UPDATE SET entry_id=excluded.entry_id WHERE entry_id<>excluded.entry_id", P(sourceId, mangaId, reference, entryId));
    }
    public static void BindNative(DatabaseSession c, long entryId, string sourceId, long mangaId, string sourceUrl, string? realUrl = null)
    {
        var baseUrl = c.Scalar("SELECT base_url FROM source_config WHERE source_id=$p0", P(sourceId)) as string;
        string reference;
        try { reference = PortableIdentity.GalleryReference(sourceUrl, realUrl, baseUrl) ?? PortableIdentity.RecordId(sourceId, sourceUrl); CatalogueFormat.Reference(JsonValue.Create(reference)); }
        catch (ArgumentException) { return; }
        RecordBinding(c, entryId, sourceId, mangaId, reference);
        c.Execute("INSERT OR IGNORE INTO catalogue_uid_records VALUES($p0,$p1,$p2)", P(Ensure(c, entryId), sourceId, reference));
    }
    public static JsonArray Lineage(DatabaseSession c, IEnumerable<string> values) => Objects(values.Select(value => new JsonObject
    { ["uid"] = value, ["records"] = Objects(c.Query("SELECT source_id,reference FROM catalogue_uid_records WHERE uid=$p0 ORDER BY source_id,reference", P(value)).Select(Row)) }));
    public static bool Unverified(DatabaseSession c, long entryId) => c.Scalar("""
        SELECT 1 FROM catalogue_shared_entries s JOIN entries e ON e.id=s.entry_id WHERE s.entry_id=$p0
        AND NOT EXISTS(SELECT 1 FROM source_matches m WHERE m.entry_id=s.entry_id AND m.active=1)
        AND NOT EXISTS(SELECT 1 FROM source_variants v WHERE v.entry_id=s.entry_id AND v.active=1)
        """, P(entryId)) is not null;
    public static string DecisionUid(DatabaseSession c, string kind, string key) => "asc1:decision:autosourcarr:" + PortableUuid.V5(Guid.Parse(Origin(c).Split(':')[^1]), kind + ":" + key).ToString("D");
    public static void RefreshGroups(DatabaseSession c, IEnumerable<string> roots)
    {
        foreach (var root in roots.Distinct(StringComparer.Ordinal))
        {
            var primary = c.Query("SELECT entry_id FROM catalogue_entry_ids WHERE uid=$p0 AND primary_id=1", P(root)).FirstOrDefault();
            if (primary is null) continue;
            c.Execute("UPDATE catalogue_entry_ids SET primary_id=0 WHERE uid=$p0", P(root)); var owners = Targets(c, [root]);
            if (owners.Count < 2) { c.Execute("UPDATE catalogue_entry_ids SET primary_id=1 WHERE uid=$p0 AND entry_id=$p1", P(root, primary.Integer("entry_id"))); continue; }
            foreach (var entryId in owners)
            {
                var keys = SourceKeys(c, entryId); var child = PortableUuid.Entry(root, CatalogueFormat.Canonical(keys));
                if (c.Scalar("SELECT 1 FROM catalogue_entry_ids WHERE entry_id=$p0 AND primary_id=1", P(entryId)) is null)
                { var occupied = c.Scalar("SELECT 1 FROM catalogue_entry_ids i JOIN entries e ON e.id=i.entry_id WHERE i.uid=$p0 AND i.primary_id=1", P(child)) is not null; Ensure(c, entryId, occupied ? null : child); }
                foreach (var key in keys) c.Execute("INSERT OR IGNORE INTO catalogue_uid_records VALUES($p0,$p1,$p2)", P(child, key![0]!.GetValue<string>(), key[1]!.GetValue<string>()));
            }
        }
    }
    private static JsonArray SourceKeys(DatabaseSession c, long entryId) => new(c.Query("SELECT source_id,reference FROM catalogue_source_facts WHERE entry_id=$p0 UNION SELECT source_id,reference FROM catalogue_record_bindings WHERE entry_id=$p0 ORDER BY source_id,reference", P(entryId)).Select(row => (JsonNode?)new JsonArray(row.Text("source_id"), row.Text("reference"))).ToArray());
    /// <summary>Call only inside the existing journalled merge owner transaction.</summary>
    public static void Reparent(DatabaseSession c, long target, long duplicate)
    {
        var primary = c.Scalar("SELECT uid FROM catalogue_entry_ids WHERE entry_id=$p0 AND primary_id=1", P(target)) as string;
        var rows = c.Query("SELECT uid,primary_id FROM catalogue_entry_ids WHERE entry_id=$p0", P(duplicate)); c.Execute("UPDATE catalogue_entry_ids SET primary_id=0 WHERE entry_id=$p0", P(duplicate));
        foreach (var row in rows)
        { var chosen = primary is null && row.Flag("primary_id") ? 1 : 0; c.Execute("INSERT INTO catalogue_entry_ids VALUES($p0,$p1,$p2) ON CONFLICT(uid,entry_id) DO UPDATE SET primary_id=MAX(primary_id,excluded.primary_id)", P(row.Text("uid"), target, chosen)); if (chosen == 1) primary = row.Text("uid"); }
        c.Execute("DELETE FROM catalogue_entry_ids WHERE entry_id=$p0", P(duplicate)); c.Execute("UPDATE catalogue_record_bindings SET entry_id=$p0 WHERE entry_id=$p1", P(target, duplicate));
        foreach (var (table, columns) in new[] { ("catalogue_public_refs", "reference"), ("catalogue_source_facts", "reference,source_id,fact_json") })
        { c.Execute($"INSERT OR IGNORE INTO {table} SELECT $p0,{columns} FROM {table} WHERE entry_id=$p1", P(target, duplicate)); c.Execute($"DELETE FROM {table} WHERE entry_id=$p0", P(duplicate)); }
        if (c.Scalar("SELECT 1 FROM catalogue_shared_entries WHERE entry_id=$p0", P(duplicate)) is not null)
        { c.Execute("INSERT OR IGNORE INTO catalogue_shared_entries VALUES($p0)", P(target)); c.Execute("DELETE FROM catalogue_shared_entries WHERE entry_id=$p0", P(duplicate)); }
        c.Execute("INSERT OR IGNORE INTO catalogue_uid_records SELECT $p0,source_id,reference FROM catalogue_record_bindings WHERE entry_id=$p1", P(Ensure(c, target), target));
    }
    /// <summary>Move lineage only inside the existing journalled split owner's catalogue transaction.</summary>
    public static void Split(DatabaseSession c, long original, long newId, IReadOnlySet<(string SourceId, long MangaId)> sourcePairs)
    {
        var parent = Ensure(c, original); var moved = new HashSet<(string SourceId, string Reference)>();
        foreach (var row in c.Query("SELECT * FROM catalogue_record_bindings WHERE entry_id=$p0", P(original)))
        {
            if (!sourcePairs.Contains((row.Text("source_id"), row.Integer("source_manga_id")))) continue;
            c.Execute("UPDATE catalogue_record_bindings SET entry_id=$p0 WHERE source_id=$p1 AND source_manga_id=$p2 AND reference=$p3", P(newId, row.Text("source_id"), row.Integer("source_manga_id"), row.Text("reference"))); moved.Add((row.Text("source_id"), row.Text("reference")));
        }
        foreach (var (sid, reference) in moved)
        {
            c.Execute("INSERT OR IGNORE INTO catalogue_source_facts SELECT $p0,reference,source_id,fact_json FROM catalogue_source_facts WHERE entry_id=$p1 AND source_id=$p2 AND reference=$p3", P(newId, original, sid, reference));
            c.Execute("DELETE FROM catalogue_source_facts WHERE entry_id=$p0 AND source_id=$p1 AND reference=$p2", P(original, sid, reference));
            c.Execute("INSERT OR IGNORE INTO catalogue_public_refs VALUES($p0,$p1)", P(newId, reference));
            if (c.Scalar("SELECT 1 FROM catalogue_record_bindings WHERE entry_id=$p0 AND reference=$p1 UNION ALL SELECT 1 FROM catalogue_source_facts WHERE entry_id=$p0 AND reference=$p1 LIMIT 1", P(original, reference)) is null) c.Execute("DELETE FROM catalogue_public_refs WHERE entry_id=$p0 AND reference=$p1", P(original, reference));
        }
        foreach (var uid in Ids(c, original))
        {
            var semantics = c.Query("SELECT source_id,reference FROM catalogue_uid_records WHERE uid=$p0", P(uid)).Select(row => (row.Text("source_id"), row.Text("reference"))).ToHashSet();
            if (semantics.Count != 0 && semantics.IsSubsetOf(moved)) c.Execute("UPDATE catalogue_entry_ids SET entry_id=$p0,primary_id=0 WHERE entry_id=$p1 AND uid=$p2", P(newId, original, uid));
            else if (semantics.Overlaps(moved)) Link(c, newId, [uid]);
        }
        foreach (var id in new[] { original, newId })
        {
            var members = c.Query("SELECT source_id,reference FROM catalogue_record_bindings WHERE entry_id=$p0 ORDER BY source_id,reference", P(id));
            if (members.Count == 0) { Ensure(c, id); continue; }
            var keys = new JsonArray(members.Select(row => (JsonNode?)new JsonArray(row.Text("source_id"), row.Text("reference"))).ToArray()); var child = PortableUuid.Entry(parent, CatalogueFormat.Canonical(keys));
            c.Execute("UPDATE catalogue_entry_ids SET primary_id=0 WHERE entry_id=$p0", P(id));
            if (c.Scalar("SELECT 1 FROM catalogue_entry_ids i JOIN entries e ON e.id=i.entry_id WHERE i.uid=$p0 AND i.primary_id=1 AND i.entry_id<>$p1", P(child, id)) is not null) { Ensure(c, id); Link(c, id, [child]); }
            else Ensure(c, id, child);
            foreach (var row in members) c.Execute("INSERT OR IGNORE INTO catalogue_uid_records VALUES($p0,$p1,$p2)", P(child, row.Text("source_id"), row.Text("reference")));
        }
    }
}
