using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Matching.Duplicates;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Core.Catalogue.Structure
{
    /// <summary>Duplicate review's view of the native structure: the existing CatalogueMerge / CatalogueSplit primitives.</summary>
    public sealed partial class CatalogueStructure : IDuplicateStructure
    {
        long IDuplicateStructure.MergeEntries(long left, long right, string reason)
        {
            lock (_gate)
            {
                if (_database.HasCurrentThreadSession(DatabaseFile.Catalog) || _database.HasCurrentThreadSession(DatabaseFile.Origins))
                    throw new InvalidOperationException("Entry merges require standalone catalogue work.");
                return Merge(left, right, reason);
            }
        }

        long IDuplicateStructure.SplitRecords(long entryId, IReadOnlyList<NativeSourceRecord> records, bool manual, string matchType)
        {
            ArgumentNullException.ThrowIfNull(records);
            lock (_gate)
            {
                if (_database.HasCurrentThreadSession(DatabaseFile.Catalog) || _database.HasCurrentThreadSession(DatabaseFile.Origins))
                    throw new InvalidOperationException("Entry splits require standalone catalogue work.");
                return Split(entryId, records, manual, matchType);
            }
        }

        void IDuplicateStructure.ReconcilePending(CancellationToken cancellationToken)
        {
            lock (_gate) ReconcilePending(cancellationToken);
        }

        /// <summary>
        /// Gallery-source evidence for the automatic duplicate gate (TextAutomaticMatchGate's galleryEvidence): the same
        /// composition as the reviewed native group check (source policies and reviewed tag equivalences), of one
        /// candidate payload against the other side's trusted gallery payloads. Null when neither side is a gallery.
        /// </summary>
        public JsonObject? AutomaticGalleryEvidence(JsonObject candidate, JsonArray trusted)
        {
            ArgumentNullException.ThrowIfNull(candidate); ArgumentNullException.ThrowIfNull(trusted);
            var anchors = trusted.OfType<JsonObject>().ToArray();
            return _database.Read(DatabaseFile.Catalog, c =>
            {
                var tags = new Dictionary<long, JsonObject>(); var reviews = new List<JsonObject>();
                foreach (var row in c.Query("SELECT r.*,l.namespace lns,l.name lname,t.namespace rns,t.name rname FROM tag_reviews r JOIN tags l ON l.id=r.left_id JOIN tags t ON t.id=r.right_id ORDER BY r.left_id,r.right_id"))
                {
                    var first = row.Integer("left_id"); var second = row.Integer("right_id");
                    tags[first] = new() { ["id"] = first, ["namespace"] = row.Text("lns"), ["name"] = row.Text("lname") };
                    tags[second] = new() { ["id"] = second, ["namespace"] = row.Text("rns"), ["name"] = row.Text("rname") };
                    reviews.Add(new() { ["left_id"] = first, ["right_id"] = second, ["decision"] = row.Text("decision") });
                }
                var mapping = AutoSourcarr.Core.Catalogue.Gallery.GalleryTagEquivalence.Mapping(tags.Values.ToArray(), reviews);
                JsonObject Source(string sid)
                {
                    var row = c.Query("SELECT * FROM source_config WHERE source_id=$p0", StructureSql.P(sid)).FirstOrDefault();
                    return row is null ? new() : StructureSql.Json(row);
                }
                return AutoSourcarr.Core.Catalogue.Gallery.GalleryEvidence.Compose(candidate, anchors, Source, _policies.Effective,
                    values => AutoSourcarr.Core.Catalogue.Gallery.GalleryTagEquivalence.CanonicalKeys(values, mapping));
            });
        }
    }
}

namespace AutoSourcarr.Core.Matching.Duplicates
{
    /// <summary>
    /// Database-backed <see cref="IDuplicateManualView"/> over the released manual tables (duplicate_reviews,
    /// manual_match_bindings, manual_separate_records). Area 8B's ManualDecisions may replace it.
    /// </summary>
    public sealed class CatalogueDuplicateManualView(SqliteDatabase database, TimeProvider? clock = null) : IDuplicateManualView
    {
        private readonly TimeProvider _clock = clock ?? TimeProvider.System;
        private static (string Name, object? Value)[] P(params object?[] values) => DuplicateJson.P(values);

        public bool ReviewedDifferent(long left, long right) => database.Read(DatabaseFile.Catalog, c => DuplicateEvidence.Review(c, left, right) == "not_duplicate");

        public bool ManuallyGrouped(long left, long right) => database.Read(DatabaseFile.Catalog, c =>
        {
            left = DuplicateEvidence.Canonical(c, left); right = DuplicateEvidence.Canonical(c, right);
            if (left == right) return true;
            HashSet<string> Groups(long id) => c.Query("""
                SELECT DISTINCT b.group_id FROM manual_match_bindings b
                WHERE b.group_id IS NOT NULL AND (b.entry_id_hint=$p0 OR EXISTS(SELECT 1 FROM source_matches m WHERE m.entry_id=$p0 AND m.active=1 AND m.source_id=b.source_id AND m.source_manga_id=b.source_manga_id)
                   OR EXISTS(SELECT 1 FROM source_variants v WHERE v.entry_id=$p0 AND v.active=1 AND v.source_id=b.source_id AND v.source_manga_id=b.source_manga_id))
                """, P(id)).Select(row => row.Text("group_id")).Where(value => value.Length != 0).ToHashSet(StringComparer.Ordinal);
            return Groups(left).Overlaps(Groups(right));
        });

        public bool SeparatedRecord(long entryId, string sourceId, long sourceMangaId) => database.Read(DatabaseFile.Catalog, c =>
            c.Scalar("SELECT 1 FROM manual_separate_records WHERE target_entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2", P(DuplicateEvidence.Canonical(c, entryId), sourceId, sourceMangaId)) is not null);

        /// <summary>Port of _persist_duplicate_review_manual_group: lock each reviewed record, bind it, and group the entry's bindings.</summary>
        public JsonObject PersistReviewedGroup(long canonicalEntryId, IReadOnlyList<NativeSourceRecord> records)
        {
            ArgumentNullException.ThrowIfNull(records);
            var stamp = _clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
            return database.Write(DatabaseFile.Catalog, c =>
            {
                var entry = DuplicateEvidence.Canonical(c, canonicalEntryId); var persisted = 0;
                foreach (var record in records.Distinct())
                {
                    if (record.SourceId.Length == 0 || record.MangaId <= 0) continue;
                    DatabaseRow? row = null;
                    foreach (var table in new[] { "source_matches", "source_variants" })
                    {
                        c.Execute($"UPDATE {table} SET match_type='manual',locked=1,last_checked=$p0 WHERE entry_id=$p1 AND source_id=$p2 AND source_manga_id=$p3 AND active=1", P(stamp, entry, record.SourceId, record.MangaId));
                        row ??= c.Query($"SELECT r.*,COALESCE(NULLIF(sc.display_name,''),sc.name,r.source_id) source_name FROM {table} r LEFT JOIN source_config sc ON sc.source_id=r.source_id WHERE r.entry_id=$p0 AND r.source_id=$p1 AND r.source_manga_id=$p2 AND r.active=1", P(entry, record.SourceId, record.MangaId)).FirstOrDefault();
                    }
                    if (row is null) continue;
                    c.Execute("""
                        INSERT INTO manual_match_bindings(source_id,source_manga_id,group_id,entry_id_hint,source_name,source_title,record_json,created_at,updated_at)
                        VALUES($p0,$p1,NULL,$p2,$p3,$p4,$p5,$p6,$p6)
                        ON CONFLICT(source_id,source_manga_id) DO UPDATE SET entry_id_hint=excluded.entry_id_hint,
                          source_name=CASE WHEN excluded.source_name<>'' THEN excluded.source_name ELSE manual_match_bindings.source_name END,
                          source_title=CASE WHEN excluded.source_title<>'' THEN excluded.source_title ELSE manual_match_bindings.source_title END,
                          record_json=excluded.record_json,grouping_suppressed=0,updated_at=excluded.updated_at
                        """, P(record.SourceId, record.MangaId, entry, row.Text("source_name"), row.Text("source_title"), CanonicalJson.Text(Catalogue.Structure.StructureSql.Payload(row)), stamp));
                    persisted++;
                }
                var bindings = c.Query("SELECT group_id FROM manual_match_bindings WHERE entry_id_hint=$p0 AND grouping_suppressed=0 ORDER BY created_at,source_id,source_manga_id", P(entry));
                string? group = null;
                if (bindings.Count >= 2)
                {
                    group = bindings.Select(row => row.Text("group_id")).FirstOrDefault(value => value.Length != 0) ?? Guid.NewGuid().ToString("N");
                    var title = c.Scalar("SELECT preferred_title FROM entries WHERE id=$p0", P(entry)) as string ?? "Manual group";
                    c.Execute("INSERT INTO manual_match_groups(id,title,created_at,updated_at) VALUES($p0,$p1,$p2,$p2) ON CONFLICT(id) DO UPDATE SET updated_at=excluded.updated_at", P(group, title, stamp));
                    foreach (var other in bindings.Select(row => row.Text("group_id")).Where(value => value.Length != 0 && value != group).Distinct(StringComparer.Ordinal).ToArray())
                    {
                        c.Execute("UPDATE manual_match_bindings SET group_id=$p0,updated_at=$p1 WHERE group_id=$p2", P(group, stamp, other));
                        c.Execute("DELETE FROM manual_match_groups WHERE id=$p0", P(other));
                    }
                    c.Execute("UPDATE manual_match_bindings SET group_id=$p0,updated_at=$p1 WHERE entry_id_hint=$p2 AND grouping_suppressed=0", P(group, stamp, entry));
                }
                return new JsonObject { ["manual_group_id"] = group, ["manual_group_members"] = persisted };
            });
        }
    }
}
