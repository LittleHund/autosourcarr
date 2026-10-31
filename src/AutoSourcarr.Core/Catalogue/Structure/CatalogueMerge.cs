using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Catalogue.Structure.StructureSql;

namespace AutoSourcarr.Core.Catalogue.Structure;

public sealed partial class CatalogueStructure
{
    private static readonly string[] SourceColumns = ["entry_id", "source_id", "source_manga_id", "source_manga_url", "source_title", "confidence", "match_type", "locked", "active", "initialized", "thumbnail_url", "description", "author", "artist", "genre", "status", "real_url", "metadata_json", "last_checked"];
    private static void InsertSource(DatabaseSession c, string table, long id, DatabaseRow row, string? matchType = null, bool? locked = null, bool upsert = false)
    {
        var values = SourceColumns.Select(name => name switch { "entry_id" => (object)id, "active" => 1, "match_type" when matchType is not null => matchType, "locked" when locked is not null => locked.Value ? 1 : 0, _ => row[name] }).ToArray();
        var update = upsert ? " ON CONFLICT(source_id,source_manga_id) DO UPDATE SET " + string.Join(',', SourceColumns.Where(name => name is not ("source_id" or "source_manga_id")).Select(name => name + "=excluded." + name)) : "";
        c.Execute($"INSERT INTO {table}({string.Join(',', SourceColumns)}) VALUES({string.Join(',', values.Select((_, i) => "$p" + i))})" + update, P(values));
    }
    private static (long Locked, long Matches, int Description, long Titles, long Tie) Reliability(DatabaseSession c, long id)
    {
        var row = c.Query("SELECT SUM(CASE WHEN locked=1 THEN 1 ELSE 0 END) locked_count,COUNT(*) match_count FROM source_matches WHERE entry_id=$p0 AND active=1", P(id)).Single();
        return (row.Integer("locked_count"), row.Integer("match_count"), Length(c.Scalar("SELECT description FROM entries WHERE id=$p0", P(id)) as string ?? ""), Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM titles WHERE entry_id=$p0", P(id)), System.Globalization.CultureInfo.InvariantCulture), -id);
    }
    private long Merge(long left, long right, string reason)
    {
        var pair = _database.Read(DatabaseFile.Catalog, c => (Left: Canonical(c, left), Right: Canonical(c, right))); left = pair.Left; right = pair.Right; if (left == right) return left;
        var payloads = _database.Read(DatabaseFile.Catalog, c => Records(c, left).Concat(Records(c, right)).Select(row => _gallery.CleanNative(row.Key.SourceId, GalleryPayload(row.Row))).ToArray()); GalleryPolicy.GroupingGate(payloads, _policies.Effective);
        var target = _database.Write(DatabaseFile.Catalog, c =>
        {
            var chosen = Reliability(c, left).CompareTo(Reliability(c, right)) >= 0 ? left : right; var duplicate = chosen == left ? right : left;
            if (!Exists(c, chosen) || !Exists(c, duplicate)) throw new KeyNotFoundException("Merge entry no longer exists");
            var lineage = c.Scalar("SELECT 1 FROM automatic_merge_lineage WHERE canonical_entry_id IN ($p0,$p1) LIMIT 1", P(left, right)) is null ? [] : MemberSnapshots(c, left).Concat(MemberSnapshots(c, right)).ToList();
            var retained = new List<DatabaseRow>();
            foreach (var match in c.Query("SELECT * FROM source_matches WHERE entry_id=$p0 AND active=1 ORDER BY id", P(duplicate)))
            {
                var conflict = c.Query("SELECT * FROM source_matches WHERE entry_id=$p0 AND source_id=$p1", P(chosen, match.Text("source_id"))).FirstOrDefault();
                if (conflict is not null && !conflict.Flag("active")) { c.Execute("DELETE FROM source_matches WHERE id=$p0", P(conflict.Integer("id"))); conflict = null; }
                if (conflict is null) { c.Execute("UPDATE source_matches SET entry_id=$p0 WHERE id=$p1", P(chosen, match.Integer("id"))); continue; }
                static (long, double, long, int) Rank(DatabaseRow row) => (row.Integer("locked"), row.Number("confidence"), row.Integer("initialized"), Length(row.Text("description")));
                if (Rank(match).CompareTo(Rank(conflict)) > 0) { retained.Add(conflict); c.Execute("DELETE FROM source_matches WHERE id=$p0", P(conflict.Integer("id"))); c.Execute("UPDATE source_matches SET entry_id=$p0 WHERE id=$p1", P(chosen, match.Integer("id"))); }
                else { retained.Add(match); c.Execute("DELETE FROM source_matches WHERE id=$p0", P(match.Integer("id"))); }
            }
            c.Execute("UPDATE source_variants SET entry_id=$p0 WHERE entry_id=$p1", P(chosen, duplicate));
            foreach (var row in retained)
            {
                var owners = Owners(c, [new(row.Text("source_id"), row.Integer("source_manga_id"))]);
                if (owners.TryGetValue(new(row.Text("source_id"), row.Integer("source_manga_id")), out var owner) && owner != chosen) throw new InvalidOperationException("Merged source record is owned by another entry");
                if (c.Scalar("SELECT 1 FROM source_matches WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2 AND active=1", P(chosen, row.Text("source_id"), row.Integer("source_manga_id"))) is null) InsertSource(c, "source_variants", chosen, row, upsert: true);
            }
            PersistEdition(c, chosen); MoveProviderIdentities(c, chosen, duplicate);
            foreach (var title in c.Query("SELECT * FROM titles WHERE entry_id=$p0", P(duplicate)))
                if (title.Text("title_type") != "manual") c.Execute("INSERT OR IGNORE INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)", P(chosen, title.Text("title"), title.Text("normalized"), title.Text("language"), title.Text("title_type"), title.Text("source_id"), title.Integer("source_manga_id")));
            c.Execute("DELETE FROM preferred_title_overrides WHERE entry_id=$p0", P(duplicate)); c.Execute("DELETE FROM manual_title_scopes WHERE entry_id=$p0", P(duplicate));
            var stamp = Stamp(); c.Execute("UPDATE manual_match_bindings SET entry_id_hint=$p0,updated_at=$p1 WHERE entry_id_hint=$p2", P(chosen, stamp, duplicate));
            c.Execute("INSERT INTO match_history(entry_id,source_id,old_source_manga_id,new_source_manga_id,change_type,changed_at) VALUES($p0,'*',$p1,$p0,$p2,$p3)", P(chosen, duplicate, "merge:" + reason, stamp));
            if (lineage.Count != 0)
            {
                c.Execute("DELETE FROM automatic_merge_lineage WHERE canonical_entry_id IN ($p0,$p1)", P(chosen, duplicate));
                foreach (var snapshot in lineage)
                { var id = snapshot["entry_id"]!.GetValue<long>(); if (id > 0) c.Execute("INSERT OR REPLACE INTO automatic_merge_lineage VALUES($p0,$p1,$p2,$p3)", P(chosen, id, CanonicalJson.Text(snapshot), stamp)); }
            }
            Alias(c, duplicate, chosen, "merge:" + reason, stamp); RemapReviews(c, duplicate, chosen);
            c.Execute("INSERT INTO pending_entry_merges VALUES($p0,$p1,$p2,'')", P(duplicate, chosen, stamp));
            CataloguePreferenceLineage.Reparent(c, chosen, duplicate, stamp, _display); c.Execute("DELETE FROM entries WHERE id=$p0", P(duplicate)); return (Target: chosen, Duplicate: duplicate);
        });
        FinishMerge(target.Duplicate); return target.Target;
    }
    private List<JsonObject> MemberSnapshots(DatabaseSession c, long id)
    {
        var rows = c.Query("SELECT member_entry_id,snapshot_json FROM automatic_merge_lineage WHERE canonical_entry_id=$p0 ORDER BY member_entry_id", P(id));
        if (rows.Count == 0)
        {
            var snapshot = NativeSnapshot(c, id);
            foreach (var name in new[] { "titles", "normalized_titles", "descriptions", "identities", "identity_details", "gallery_payloads", "policy_payloads", "source_ids", "thumbnail_fingerprints" }) if (snapshot[name] is not JsonArray) throw new InvalidDataException("Native member snapshot owner is unavailable; automatic lineage was not replaced");
            return [snapshot];
        }
        return rows.Select(row =>
        {
            var value = JsonNode.Parse(row.Text("snapshot_json")) as JsonObject ?? throw new InvalidDataException("Retained automatic lineage is invalid; merge was not applied");
            if (value.Count == 0) throw new InvalidDataException("Retained automatic lineage is empty; merge was not applied"); if (!value.ContainsKey("entry_id")) value["entry_id"] = row.Integer("member_entry_id"); return value;
        }).ToList();
    }
    private static void MoveProviderIdentities(DatabaseSession c, long target, long duplicate)
    {
        foreach (var incoming in c.Query("SELECT * FROM identity_links WHERE entry_id=$p0 ORDER BY confidence DESC,last_checked DESC", P(duplicate)))
        {
            var current = c.Query("SELECT * FROM identity_links WHERE entry_id=$p0 AND provider=$p1", P(target, incoming.Text("provider"))).FirstOrDefault();
            if (current is null) { c.Execute("UPDATE identity_links SET entry_id=$p0 WHERE id=$p1", P(target, incoming.Integer("id"))); continue; }
            static (long, double, int, int) Rank(DatabaseRow row) => (row.Integer("locked"), row.Number("confidence"), Length(row.Text("description")), Length(row.Text("metadata_json")));
            if (Rank(incoming).CompareTo(Rank(current)) > 0)
            {
                string[] names = ["provider_name", "external_id", "external_url", "identity_scope", "confidence", "match_type", "locked", "title", "description", "thumbnail_url", "status", "author", "artist", "genre", "media_type", "country", "year", "adult", "metadata_json", "last_checked"];
                var values = names.Select(name => incoming[name]).Append(current["id"]).ToArray(); c.Execute("UPDATE identity_links SET " + string.Join(',', names.Select((name, index) => name + "=$p" + index)) + " WHERE id=$p" + names.Length, P(values));
            }
            c.Execute("DELETE FROM identity_links WHERE id=$p0", P(incoming.Integer("id")));
        }
    }
    private static void Alias(DatabaseSession c, long oldId, long target, string reason, string stamp)
    {
        c.Execute("INSERT INTO entry_aliases VALUES($p0,$p1,$p2,$p3) ON CONFLICT(old_entry_id) DO UPDATE SET canonical_entry_id=excluded.canonical_entry_id,reason=excluded.reason,merged_at=excluded.merged_at", P(oldId, target, reason, stamp));
        c.Execute("UPDATE entry_aliases SET canonical_entry_id=$p0 WHERE canonical_entry_id=$p1 AND old_entry_id<>$p0", P(target, oldId));
        foreach (var row in c.Query("SELECT * FROM manual_separate_records WHERE target_entry_id=$p0", P(oldId))) RememberSeparate(c, target, new(row.Text("source_id"), row.Integer("source_manga_id")), row.Integer("separate_entry_id_hint") == 0 ? null : row.Integer("separate_entry_id_hint"), row.Text("source_title"), stamp, row.Text("created_at"));
        c.Execute("DELETE FROM manual_separate_records WHERE target_entry_id=$p0", P(oldId)); c.Execute("UPDATE manual_separate_records SET separate_entry_id_hint=$p0,updated_at=$p1 WHERE separate_entry_id_hint=$p2", P(target, stamp, oldId));
    }
    private static void RemapReviews(DatabaseSession c, long oldId, long target)
    {
        var rows = c.Query("SELECT * FROM duplicate_reviews WHERE left_entry_id=$p0 OR right_entry_id=$p0", P(oldId)); c.Execute("DELETE FROM duplicate_reviews WHERE left_entry_id=$p0 OR right_entry_id=$p0", P(oldId));
        foreach (var row in rows)
        {
            var other = Canonical(c, row.Integer("left_entry_id") == oldId ? row.Integer("right_entry_id") : row.Integer("left_entry_id")); if (other == target) continue; var left = Math.Min(other, target); var right = Math.Max(other, target);
            var choice = Review(c, left, right) == "not_duplicate" ? "not_duplicate" : row.Text("decision");
            c.Execute("INSERT INTO duplicate_reviews VALUES($p0,$p1,$p2,$p3) ON CONFLICT(left_entry_id,right_entry_id) DO UPDATE SET decision=excluded.decision,reviewed_at=excluded.reviewed_at", P(left, right, choice, row.Text("reviewed_at")));
        }
    }
}
