using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Catalogue.Structure.StructureSql;

namespace AutoSourcarr.Core.Catalogue.Structure;

public sealed partial class CatalogueStructure
{
    public JsonObject RecoveryStatus() => _database.Read(DatabaseFile.Catalog, c => new JsonObject
    {
        ["merges"] = Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM pending_entry_merges"), CultureInfo.InvariantCulture),
        ["splits"] = Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM pending_entry_splits"), CultureInfo.InvariantCulture)
    });
    public void ReconcilePending(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var merges = _database.Read(DatabaseFile.Catalog, c => c.Query("SELECT duplicate_entry_id FROM pending_entry_merges ORDER BY created_at,duplicate_entry_id").Select(row => row.Integer("duplicate_entry_id")).ToArray());
            var splits = _database.Read(DatabaseFile.Catalog, c => c.Query("SELECT id FROM pending_entry_splits ORDER BY created_at,id").Select(row => row.Integer("id")).ToArray()); var completed = 0; var failed = 0;
            foreach (var id in merges) { cancellationToken.ThrowIfCancellationRequested(); try { FinishMerge(id); completed++; } catch (Exception error) when (error is not OperationCanceledException) { failed++; } }
            foreach (var id in splits) { cancellationToken.ThrowIfCancellationRequested(); try { FinishSplit(id); completed++; } catch (Exception error) when (error is not OperationCanceledException) { failed++; } }
            var status = RecoveryStatus(); var remaining = status["merges"]!.GetValue<long>() + status["splits"]!.GetValue<long>();
            if (failed != 0 || remaining != 0) throw new CatalogueStructureRecoveryException("Catalogue chapter recovery remains pending; inspect its retained local journal before retrying", new JsonObject { ["checked"] = merges.Length + splits.Length, ["completed"] = completed, ["failed"] = failed, ["remaining"] = remaining });
        }
    }
    private static void Scan(DatabaseSession o, long owner, DatabaseRow scan)
    {
        o.Execute("""
            INSERT INTO source_record_scans(entry_id,source_id,source_manga_id,checked_at,chapter_count,error)
            VALUES($p0,$p1,$p2,$p3,$p4,$p5) ON CONFLICT(entry_id,source_id,source_manga_id) DO UPDATE SET
            checked_at=CASE WHEN excluded.checked_at>source_record_scans.checked_at THEN excluded.checked_at ELSE source_record_scans.checked_at END,
            chapter_count=CASE WHEN excluded.checked_at>=source_record_scans.checked_at THEN excluded.chapter_count ELSE source_record_scans.chapter_count END,
            error=CASE WHEN excluded.checked_at>=source_record_scans.checked_at THEN excluded.error ELSE source_record_scans.error END
            """, P(owner, scan.Text("source_id"), scan.Integer("source_manga_id"), scan.Text("checked_at"), scan.Integer("chapter_count"), scan.Text("error")));
    }
    private void FinishMerge(long duplicate)
    {
        var pending = _database.Read(DatabaseFile.Catalog, c => c.Query("SELECT * FROM pending_entry_merges WHERE duplicate_entry_id=$p0", P(duplicate)).FirstOrDefault()); if (pending is null) return;
        try
        {
            var target = _database.Read(DatabaseFile.Catalog, c => { var id = Canonical(c, pending.Integer("target_entry_id")); if (!Exists(c, id)) throw new InvalidOperationException("Chapter merge recovery target no longer exists"); return id; });
            var snapshot = _database.Read(DatabaseFile.Origins, o =>
            {
                var pairs = o.Query("SELECT DISTINCT cc.source_id,cc.source_manga_id FROM chapter_candidates cc JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id WHERE vc.entry_id=$p0", P(duplicate)).Select(row => new NativeSourceRecord(row.Text("source_id"), row.Integer("source_manga_id"))).ToList();
                var scans = o.Query("SELECT source_id,source_manga_id,checked_at,chapter_count,error FROM source_record_scans WHERE entry_id=$p0", P(duplicate)); return (Pairs: pairs, Scans: scans);
            });
            var owners = _database.Read(DatabaseFile.Catalog, c => Owners(c, snapshot.Pairs.Concat(snapshot.Scans.Select(row => new NativeSourceRecord(row.Text("source_id"), row.Integer("source_manga_id"))))));
            _database.Write(DatabaseFile.Origins, o =>
            {
                foreach (var pair in snapshot.Pairs) if (!owners.TryGetValue(pair, out var owner) || owner != target) o.Execute("UPDATE chapter_candidates SET active=0 WHERE source_id=$p0 AND source_manga_id=$p1 AND virtual_chapter_id IN (SELECT id FROM virtual_chapters WHERE entry_id=$p2)", P(pair.SourceId, pair.MangaId, duplicate));
                MergeOrigins(o, target, duplicate);
                foreach (var scan in snapshot.Scans) if (owners.TryGetValue(new(scan.Text("source_id"), scan.Integer("source_manga_id")), out var owner)) Scan(o, owner, scan);
                o.Execute("DELETE FROM source_record_scans WHERE entry_id=$p0", P(duplicate)); return 0;
            });
            ReconcileEntry(target); _invalidate([target, duplicate]);
            _database.Write(DatabaseFile.Catalog, c => c.Execute("DELETE FROM pending_entry_merges WHERE duplicate_entry_id=$p0", P(duplicate)));
        }
        catch (Exception error)
        { LastError("pending_entry_merges", "duplicate_entry_id", duplicate, error); throw; }
    }
    private void MergeOrigins(DatabaseSession o, long target, long duplicate)
    {
        foreach (var chapter in o.Query("SELECT * FROM virtual_chapters WHERE entry_id=$p0 ORDER BY id", P(duplicate)))
        {
            var old = chapter.Integer("id"); var existing = o.Scalar("SELECT id FROM virtual_chapters WHERE entry_id=$p0 AND chapter_key=$p1", P(target, chapter.Text("chapter_key")));
            if (existing is null)
            {
                var number = chapter.Number("chapter_number");
                if (chapter.Text("chapter_key").StartsWith("g:", StringComparison.Ordinal) && o.Scalar("SELECT 1 FROM virtual_chapters WHERE entry_id=$p0 AND chapter_number=$p1", P(target, number)) is not null)
                { number = Convert.ToDouble(o.Scalar("SELECT COALESCE(MAX(chapter_number),0)+1 FROM virtual_chapters WHERE entry_id=$p0", P(target)), CultureInfo.InvariantCulture); o.Execute("UPDATE chapter_candidates SET chapter_number=$p0 WHERE virtual_chapter_id=$p1", P(number, old)); }
                o.Execute("UPDATE virtual_chapters SET entry_id=$p0,chapter_number=$p1,updated_at=$p2 WHERE id=$p3", P(target, number, Stamp(), old)); continue;
            }
            var id = Convert.ToInt64(existing, CultureInfo.InvariantCulture); o.Execute("UPDATE chapter_candidates SET virtual_chapter_id=$p0 WHERE virtual_chapter_id=$p1", P(id, old));
            var oldServed = o.Scalar("SELECT 1 FROM served_origin WHERE virtual_chapter_id=$p0", P(old)) is not null; var newServed = o.Scalar("SELECT 1 FROM served_origin WHERE virtual_chapter_id=$p0", P(id)) is not null;
            if (oldServed && !newServed) o.Execute("UPDATE served_origin SET virtual_chapter_id=$p0 WHERE virtual_chapter_id=$p1", P(id, old)); else o.Execute("DELETE FROM served_origin WHERE virtual_chapter_id=$p0", P(old));
            o.Execute("UPDATE origin_history SET virtual_chapter_id=$p0 WHERE virtual_chapter_id=$p1", P(id, old)); o.Execute("DELETE FROM virtual_chapters WHERE id=$p0", P(old));
        }
    }
    private void FinishSplit(long splitId)
    {
        var pending = _database.Read(DatabaseFile.Catalog, c => c.Query("SELECT * FROM pending_entry_splits WHERE id=$p0", P(splitId)).FirstOrDefault()); if (pending is null) return;
        try
        {
            var pairs = ParsePairs(pending.Text("records_json"));
            var gallery = _database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT 1 FROM gallery_layout_candidates WHERE split_id=$p0", P(splitId)) is not null);
            if (gallery && _galleryReplay is null) throw new InvalidOperationException("Gallery chapter replay owner is unavailable; the original split journal and candidate intent remain retained");
            var owners = _database.Read(DatabaseFile.Catalog, c => Owners(c, pairs));
            var candidates = _database.Read(DatabaseFile.Origins, o => pairs.SelectMany(pair => o.Query("SELECT cc.id,cc.source_id,cc.source_manga_id,cc.active,vc.entry_id FROM chapter_candidates cc JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id WHERE cc.source_id=$p0 AND cc.source_manga_id=$p1", P(pair.SourceId, pair.MangaId))).ToArray());
            var entries = _database.Read(DatabaseFile.Catalog, c => candidates.Select(row => row.Integer("entry_id")).Distinct().ToDictionary(id => id, id => Canonical(c, id)));
            _database.Write(DatabaseFile.Origins, o =>
            {
                foreach (var row in candidates) if (row.Flag("active") && (!owners.TryGetValue(new(row.Text("source_id"), row.Integer("source_manga_id")), out var owner) || entries[row.Integer("entry_id")] != owner)) o.Execute("UPDATE chapter_candidates SET active=0 WHERE id=$p0 AND active=1", P(row.Integer("id")));
                foreach (var pair in pairs)
                {
                    var known = owners.TryGetValue(pair, out var owner);
                    foreach (var scan in o.Query("SELECT * FROM source_record_scans WHERE source_id=$p0 AND source_manga_id=$p1 ORDER BY checked_at", P(pair.SourceId, pair.MangaId))) if (known && scan.Integer("entry_id") != owner) Scan(o, owner, scan);
                    if (!known) o.Execute("DELETE FROM source_record_scans WHERE source_id=$p0 AND source_manga_id=$p1", P(pair.SourceId, pair.MangaId));
                    else o.Execute("DELETE FROM source_record_scans WHERE source_id=$p0 AND source_manga_id=$p1 AND entry_id<>$p2", P(pair.SourceId, pair.MangaId, owner));
                }
                return 0;
            });
            _database.Write(DatabaseFile.Catalog, c =>
            {
                foreach (var pair in pairs)
                {
                    if (!owners.TryGetValue(pair, out var owner)) continue; var exclusions = c.Query("SELECT target_entry_id FROM manual_separate_records WHERE source_id=$p0 AND source_manga_id=$p1", P(pair.SourceId, pair.MangaId));
                    c.Execute("UPDATE manual_separate_records SET separate_entry_id_hint=$p0,updated_at=$p1 WHERE source_id=$p2 AND source_manga_id=$p3", P(owner, Stamp(), pair.SourceId, pair.MangaId));
                    foreach (var exclusion in exclusions) { var target = Canonical(c, exclusion.Integer("target_entry_id")); if (target != owner && Exists(c, target) && Review(c, target, owner) != "not_duplicate") SeparateReview(c, target, owner, Stamp()); }
                }
                return 0;
            });
            if (gallery) _galleryReplay!.Restore(splitId, pairs, owners);
            var affected = _database.Read(DatabaseFile.Catalog, c => owners.Values.Append(Canonical(c, pending.Integer("original_entry_id"))).Distinct().Where(id => Exists(c, id)).Order().ToArray());
            foreach (var id in affected) ReconcileEntry(id);
            _invalidate(affected);
            _database.Write(DatabaseFile.Catalog, c => { c.Execute("DELETE FROM gallery_layout_candidates WHERE split_id=$p0", P(splitId)); return c.Execute("DELETE FROM pending_entry_splits WHERE id=$p0", P(splitId)); });
        }
        catch (Exception error) { LastError("pending_entry_splits", "id", splitId, error); throw; }
    }
    private void ReconcileEntry(long id)
    { _database.Write(DatabaseFile.Catalog, c => { _metadata(c, id, Stamp()); return 0; }); _visibility(id); }
    private static List<NativeSourceRecord> ParsePairs(string raw)
    {
        var array = JsonNode.Parse(raw) as JsonArray ?? throw new InvalidDataException("Retained split source records are invalid"); var result = new List<NativeSourceRecord>();
        foreach (var pair in array)
        {
            if (pair is not JsonArray { Count: 2 } values || values[0]?.GetValueKind() != System.Text.Json.JsonValueKind.String || values[1]?.GetValueKind() != System.Text.Json.JsonValueKind.Number || !long.TryParse(values[1]!.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var manga)) throw new InvalidDataException("Retained split source identity is invalid");
            result.Add(new(values[0]!.GetValue<string>(), manga));
        }
        return result.Distinct().ToList();
    }
    private void LastError(string table, string key, long id, Exception error)
    {
        var message = string.Concat(error.Message.EnumerateRunes().Take(1000).Select(rune => rune.ToString()));
        _database.Write(DatabaseFile.Catalog, c => c.Execute($"UPDATE {table} SET last_error=$p0 WHERE {key}=$p1", P(message, id)));
    }
}
