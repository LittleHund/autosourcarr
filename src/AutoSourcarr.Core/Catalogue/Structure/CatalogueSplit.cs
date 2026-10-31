using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Chapters;
using AutoSourcarr.Core.Catalogue.Records;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Catalogue.Tags;
using AutoSourcarr.Core.Catalogue.Gallery;
using static AutoSourcarr.Core.Catalogue.Structure.StructureSql;

namespace AutoSourcarr.Core.Catalogue.Structure;

public sealed record NativeMediaSplit(long OriginalEntryId, long NewEntryId, IReadOnlyList<NativeSourceRecord> Records);
public sealed record GalleryReconciliationResult(int Records, int Splits, int Deferred);

public sealed partial class CatalogueStructure
{
    /// <summary>Native automatic manga/webtoon separation. Reads current physical
    /// membership, never imported catalogue facts or a fabricated review task.</summary>
    public NativeMediaSplit? SeparateMixedMedia(long entryId, CatalogueMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_database.HasCurrentThreadSession(DatabaseFile.Catalog) || _database.HasCurrentThreadSession(DatabaseFile.Origins))
                throw new InvalidOperationException("Automatic media separation requires standalone catalogue work.");
            var state = _database.Read(DatabaseFile.Catalog, c =>
            {
                var canonical = Canonical(c, entryId);
                // Retain the released query order: first classified primary wins;
                // all-variant count ties retain their first encountered mode.
                var rows = c.Query("""
                    SELECT 'primary' kind,source_id,source_manga_id,source_title,genre,match_type,locked
                    FROM source_matches WHERE entry_id=$entry AND active=1
                    UNION ALL
                    SELECT 'additional' kind,source_id,source_manga_id,source_title,genre,match_type,locked
                    FROM source_variants WHERE entry_id=$entry AND active=1
                    """, ("$entry", canonical));
                if (rows.Select(row => new NativeSourceRecord(row.Text("source_id"), row.Integer("source_manga_id"))).Distinct().Count() != rows.Count)
                    throw new InvalidOperationException("Automatic media separation requires unambiguous physical source records.");
                var classified = rows.Select(row => (Row: row, Mode: CatalogueMetadata.CandidateMediaMode(row.Text("source_title"), row.Text("genre"))))
                    .Where(value => value.Mode is "manga" or "webtoon").ToArray();
                if (classified.Select(value => value.Mode).Distinct(StringComparer.Ordinal).Count() < 2)
                    return (Entry: canonical, Selected: Array.Empty<NativeSourceRecord>());
                var primary = Array.FindIndex(classified, value => value.Row.Text("kind") == "primary");
                var keep = primary >= 0 ? classified[primary].Mode
                    : classified.GroupBy(value => value.Mode, StringComparer.Ordinal).OrderByDescending(group => group.Count()).First().Key;
                var selected = classified.Where(value => value.Mode != keep).ToArray();
                if (selected.Length == 0 || selected.Length >= rows.Count
                    || selected.Any(value => value.Row.Flag("locked") || value.Row.Text("match_type").ToLowerInvariant() is "manual" or "split-duplicate"))
                    return (Entry: canonical, Selected: Array.Empty<NativeSourceRecord>());
                return (Entry: canonical, Selected: selected.Select(value => new NativeSourceRecord(value.Row.Text("source_id"), value.Row.Integer("source_manga_id"))).ToArray());
            });
            if (state.Selected.Length == 0) return null;
            cancellationToken.ThrowIfCancellationRequested();
            // Once mutation is admitted, finish the real cross-file journal and
            // metadata work. Cancellation cannot abandon a committed split.
            var created = Split(state.Entry, state.Selected, manual: false);
            _database.Write(DatabaseFile.Catalog, c =>
            {
                metadata.RecomputeMedia(c, state.Entry); metadata.RecomputeMedia(c, created);
                metadata.SyncRelatedMediaTitles(c, state.Entry, Stamp()); metadata.SyncRelatedMediaTitles(c, created, Stamp());
                return 0;
            });
            return new(state.Entry, created, Array.AsReadOnly(state.Selected));
        }
    }

    /// <summary>Upgrade current native gallery metadata and cached chapter layout,
    /// including automatic animation separation, without requesting a website.</summary>
    public GalleryReconciliationResult ReconcileGallery(long entryId, NativeChapterWriter chapters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        if (!chapters.Owns(_database, _gate)) throw new ArgumentException("Gallery reconciliation requires this instance's actual native chapter writer.", nameof(chapters));
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_database.HasCurrentThreadSession(DatabaseFile.Catalog) || _database.HasCurrentThreadSession(DatabaseFile.Origins))
                throw new InvalidOperationException("Gallery reconciliation requires standalone catalogue work.");
            entryId = _database.Read(DatabaseFile.Catalog, c => Canonical(c, entryId));
            var rows = _database.Read(DatabaseFile.Catalog, c => GalleryRows(c, entryId));
            if (rows.Select(row => row.Key).Distinct().Count() != rows.Count)
                throw new InvalidOperationException("Gallery reconciliation requires unambiguous physical source records.");
            var profiles = _database.Read(DatabaseFile.Catalog, GalleryProfiles);
            var expected = rows.Select(row => Json(row.Row)).ToArray();
            var galleries = new List<Owned>(); var stale = false;
            var tags = new NativeTagStore(_database, _gate, _clock); var records = new NativeRecordStore(_database, _gate, _clock);
            // From the first admitted write onward complete physical synchronous
            // phases. A cancelled await cannot abandon a committed split/replay.
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i]; var payload = _gallery.CleanNative(row.Key.SourceId, GalleryPayload(row.Row));
                if (payload["_gallery_metadata"] is not JsonObject) continue;
                if (!long.TryParse(payload["id"]?.ToJsonString().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mangaId) || mangaId != row.Key.MangaId)
                    throw new InvalidDataException("Retained gallery payload does not name its actual native source record.");
                var metadata = CanonicalJson.Text(payload); var title = Text(payload, "title"); if (title.Length == 0) title = row.Row.Text("source_title");
                var changed = _database.Write(DatabaseFile.Catalog, c =>
                {
                    var count = c.Execute($"""
                        UPDATE {row.Storage} SET source_title=$p0,metadata_json=$p1 WHERE id=$p2 AND entry_id=$p3 AND active=1
                        AND metadata_json=$p4 AND source_title=$p5 AND locked=$p6 AND match_type=$p7
                        """, P(title, metadata, row.Row.Integer("id"), entryId, row.Row["metadata_json"], row.Row["source_title"], row.Row["locked"], row.Row["match_type"]));
                    if (count != 0)
                    {
                        var role = row.Storage == "source_matches" ? NativeTagTable.Matches : NativeTagTable.Variants;
                        tags.Synchronize(c, tags.Capture(c, role, row.Row.Integer("id")));
                    }
                    return count;
                });
                if (changed == 0) { stale = true; continue; }
                expected[i]["metadata_json"] = metadata; expected[i]["source_title"] = title;
                galleries.Add(row); records.RefreshTitles(entryId, row.Key.SourceId, payload);
            }
            // Keep the released optimistic ownership/manual recheck, with physical
            // role/title and current source-policy changes also causing deferral.
            var current = _database.Read(DatabaseFile.Catalog, c => (Rows: GalleryRows(c, entryId), Profiles: GalleryProfiles(c)));
            if (stale || profiles != current.Profiles || !GalleryFingerprint(expected).SequenceEqual(GalleryFingerprint(current.Rows.Select(row => Json(row.Row)))))
                return new(galleries.Count, 0, 1);
            if (galleries.Count == 0) return new(0, 0, 0);
            var animated = new List<NativeSourceRecord>(); var still = false;
            foreach (var row in current.Rows)
            {
                var payload = _gallery.CleanNative(row.Key.SourceId, GalleryPayload(row.Row));
                if (payload["_gallery_metadata"] is JsonObject gallery)
                { if (gallery["animated"]?.GetValue<bool>() == true) animated.Add(row.Key); else still = true; }
                else if (GalleryMetadata.Enrich(payload, new JsonObject { ["metadata_profile"] = "gallery" })["_gallery_metadata"]?["animated"]?.GetValue<bool>() != true)
                    still = true;
            }
            using var replay = chapters.CaptureActiveGalleryCandidates(entryId);
            var durableManual = _database.Read(DatabaseFile.Catalog, c => c.Scalar("""
                SELECT 1 FROM manual_match_bindings mb JOIN source_matches sm USING(source_id,source_manga_id) WHERE sm.entry_id=$p0 AND sm.active=1
                UNION ALL SELECT 1 FROM manual_match_bindings mb JOIN source_variants sv USING(source_id,source_manga_id) WHERE sv.entry_id=$p0 AND sv.active=1 LIMIT 1
                """, P(entryId)) is not null);
            var created = 0L;
            if (animated.Count != 0 && still && !durableManual && !current.Rows.Any(row => row.Row.Flag("locked") || row.Row.Text("match_type").ToLowerInvariant() is "manual" or "split-duplicate" or "separate-anchor"))
                created = Split(entryId, animated, manual: false, splitMatchType: "auto-animation-split");
            foreach (var row in galleries)
            {
                var owners = _database.Read(DatabaseFile.Catalog, c => Owners(c, [row.Key]));
                if (owners.TryGetValue(row.Key, out var owner)) chapters.ReplayCachedGallery(owner, row.Key.SourceId, row.Key.MangaId, replay);
            }
            if (galleries.Count != 0) { _database.Write(DatabaseFile.Catalog, c => { _metadata(c, entryId, Stamp()); return 0; }); _visibility(entryId); }
            if (created != 0) { _database.Write(DatabaseFile.Catalog, c => { _metadata(c, created, Stamp()); return 0; }); _visibility(created); }
            return new(galleries.Count, created == 0 ? 0 : 1, 0);
        }
    }
    private static List<Owned> GalleryRows(DatabaseSession c, long entryId) => c.Query("""
        SELECT 'source_matches' storage,id,entry_id,source_id,source_manga_id,source_title,artist,genre,metadata_json,locked,match_type FROM source_matches WHERE entry_id=$p0 AND active=1
        UNION ALL SELECT 'source_variants' storage,id,entry_id,source_id,source_manga_id,source_title,artist,genre,metadata_json,locked,match_type FROM source_variants WHERE entry_id=$p0 AND active=1
        """, P(entryId)).Select(row => new Owned(row, row.Text("storage"))).ToList();
    private static IEnumerable<string> GalleryFingerprint(IEnumerable<JsonObject> rows) => rows.Select(CanonicalJson.Text).Order(StringComparer.Ordinal);
    private static string GalleryProfiles(DatabaseSession c) => CanonicalJson.Text(new JsonObject
    {
        ["sources"] = new JsonArray(c.Query("SELECT * FROM source_config ORDER BY source_id").Select(row => (JsonNode)Json(row)).ToArray()),
        ["policies"] = new JsonArray(c.Query("SELECT * FROM gallery_policies ORDER BY source_id").Select(row => (JsonNode)Json(row)).ToArray())
    });

    private long Split(long original, IReadOnlyList<NativeSourceRecord> requested, bool manual, string splitMatchType = "split-duplicate")
    {
        var result = _database.Write(DatabaseFile.Catalog, c =>
        {
            original = Canonical(c, original); if (!Exists(c, original)) throw new KeyNotFoundException("Split entry no longer exists"); var wanted = requested.ToHashSet();
            var records = Records(c, original); var selected = records.Where(row => wanted.Contains(row.Key)).OrderByDescending(row => row.Row.Integer("locked")).ThenByDescending(row => row.Row.Integer("initialized")).ThenByDescending(row => Length(row.Row.Text("description"))).ThenByDescending(row => row.Row.Number("confidence")).ToList();
            if (wanted.Count == 0 || selected.Select(row => row.Key).ToHashSet().Count != wanted.Count) throw new InvalidOperationException("Selected source records are no longer attached");
            if (selected.Count >= records.Count) throw new InvalidOperationException("Leave at least one source record on the original entry");
            foreach (var record in records) CatalogueIdentity.BindNative(c, original, record.Key.SourceId, record.Key.MangaId, record.Row.Text("source_manga_url"), record.Row.Text("real_url"));
            var seed = selected[0]; var payload = _gallery.CleanNative(seed.Key.SourceId, Payload(seed.Row)); var title = Text(payload, "title"); var raw = title;
            if (TextMatcher.TitleColorVariant(title) != "unknown") title = TextMatcher.ColorBaseTitle(title);
            if (TextMatcher.TitleMediaVariant(title) != "unknown") title = TextMatcher.MediaBaseTitle(title); if (title.Length == 0) title = raw;
            var stamp = Stamp(); var genre = payload["genre"] is JsonArray genres ? string.Join(", ", genres.Select(value => value?.GetValue<string>() ?? "")) : Text(payload, "genre");
            var status = payload["status"] is not null && long.TryParse(payload["status"]!.ToJsonString().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            c.Execute("INSERT INTO entries(preferred_title,description,thumbnail_url,status,author,artist,genre,created_at,updated_at) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p7)", P(title, TextMatcher.CleanSummary(Text(payload, "description")), Text(payload, "thumbnailUrl"), status, Text(payload, "author"), Text(payload, "artist"), genre, stamp));
            var id = Convert.ToInt64(c.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture); CatalogueIdentity.Ensure(c, id);
            foreach (var record in selected) c.Execute($"DELETE FROM {record.Storage} WHERE id=$p0", P(record.Row.Integer("id")));
            var primary = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in selected)
            {
                InsertSource(c, primary.Add(record.Key.SourceId) ? "source_matches" : "source_variants", id, record.Row, splitMatchType, manual || record.Row.Flag("locked")); RefreshTitles(c, id, record.Key.SourceId, Payload(record.Row));
            }
            foreach (var sid in selected.Select(row => row.Key.SourceId).Distinct(StringComparer.Ordinal))
            {
                if (c.Scalar("SELECT 1 FROM source_matches WHERE entry_id=$p0 AND source_id=$p1 AND active=1", P(original, sid)) is not null) continue;
                var variant = c.Query("SELECT * FROM source_variants WHERE entry_id=$p0 AND source_id=$p1 AND active=1 ORDER BY locked DESC,confidence DESC,initialized DESC,length(description) DESC,id LIMIT 1", P(original, sid)).FirstOrDefault();
                if (variant is null) continue; c.Execute("DELETE FROM source_variants WHERE id=$p0", P(variant.Integer("id"))); c.Execute("DELETE FROM source_matches WHERE entry_id=$p0 AND source_id=$p1 AND active=0", P(original, sid)); InsertSource(c, "source_matches", original, variant, variant.Text("match_type").Length == 0 ? "auto" : variant.Text("match_type"), variant.Flag("locked"));
            }
            var selectedNames = selected.SelectMany(row => Titles(Payload(row.Row))).ToHashSet(StringComparer.Ordinal); var remainingNames = Records(c, original).SelectMany(row => Titles(Payload(row.Row))).ToHashSet(StringComparer.Ordinal);
            foreach (var row in selected) c.Execute("DELETE FROM titles WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2 AND title_type IN ('source','source-variant','description-alias','matched-alias')", P(original, row.Key.SourceId, row.Key.MangaId));
            foreach (var name in selectedNames.Except(remainingNames, StringComparer.Ordinal)) c.Execute("DELETE FROM titles WHERE entry_id=$p0 AND normalized=$p1 AND title_type IN ('source','source-variant','description-alias','matched-alias')", P(original, name));
            var pairs = new JsonArray(wanted.OrderBy(key => key.SourceId, StringComparer.Ordinal).ThenBy(key => key.MangaId).Select(key => (JsonNode?)new JsonArray(key.SourceId, key.MangaId)).ToArray());
            c.Execute("INSERT INTO duplicate_split_history(original_entry_id,new_entry_id,records_json,split_at) VALUES($p0,$p1,$p2,$p3)", P(original, id, CanonicalJson.Text(pairs), stamp));
            if (manual) RememberManualSplit(c, original, id, records, selected, stamp);
            PersistEdition(c, original); PersistEdition(c, id);
            c.Execute("INSERT INTO pending_entry_splits(original_entry_id,new_entry_id,records_json,created_at,last_error) VALUES($p0,$p1,$p2,$p3,'')", P(original, id, CanonicalJson.Text(pairs), stamp)); var journal = Convert.ToInt64(c.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
            CatalogueIdentity.Split(c, original, id, wanted.Select(key => (key.SourceId, key.MangaId)).ToHashSet()); return (NewId: id, Journal: journal);
        });
        FinishSplit(result.Journal); return result.NewId;
    }
    private static IEnumerable<string> Titles(JsonObject payload) => TextMatcher.ExtractAltTitles(Text(payload, "description")).Prepend(Text(payload, "title")).Select(TextMatcher.NormalizeTitle).Where(value => value.Length != 0);
    private static void RefreshTitles(DatabaseSession c, long id, string sourceId, JsonObject payload)
    {
        var manga = payload["id"]!.GetValue<long>(); var description = Text(payload, "description");
        c.Execute("DELETE FROM titles WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2 AND title_type='matched-alias'", P(id, sourceId, manga));
        if (description.Trim().Length != 0) c.Execute("DELETE FROM titles WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2 AND title_type='description-alias'", P(id, sourceId, manga));
        var language = c.Scalar("SELECT lang FROM source_config WHERE source_id=$p0", P(sourceId)) as string ?? "";
        foreach (var (name, kind) in new[] { (Text(payload, "title"), "matched-alias") }.Concat(description.Trim().Length == 0 ? [] : TextMatcher.ExtractAltTitles(description).Select(name => (name, "description-alias"))))
        {
            var title = name.Trim(); if (!TextMatcher.IsMeaningfulTitle(title)) continue; var normalized = TextMatcher.NormalizeTitle(title); if (normalized.Length == 0) continue;
            c.Execute("INSERT INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6) ON CONFLICT(entry_id,normalized,source_id,title_type) DO UPDATE SET title=excluded.title,language=CASE WHEN excluded.language<>'' THEN excluded.language ELSE titles.language END,source_manga_id=CASE WHEN excluded.source_manga_id<>0 THEN excluded.source_manga_id ELSE titles.source_manga_id END", P(id, title, normalized, language, kind, sourceId, manga));
        }
    }
    private static void RememberSeparate(DatabaseSession c, long target, NativeSourceRecord record, long? owner, string title, string stamp, string? created = null)
        => c.Execute("INSERT INTO manual_separate_records VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6) ON CONFLICT(target_entry_id,source_id,source_manga_id) DO UPDATE SET separate_entry_id_hint=COALESCE(excluded.separate_entry_id_hint,manual_separate_records.separate_entry_id_hint),source_title=CASE WHEN excluded.source_title<>'' THEN excluded.source_title ELSE manual_separate_records.source_title END,updated_at=excluded.updated_at", P(target, record.SourceId, record.MangaId, owner, title, created ?? stamp, stamp));
    private static void SeparateReview(DatabaseSession c, long first, long second, string stamp)
    {
        first = Canonical(c, first); second = Canonical(c, second); if (first == second) return; var left = Math.Min(first, second); var right = Math.Max(first, second);
        c.Execute("INSERT INTO duplicate_review_history(left_entry_id,right_entry_id,decision,reviewed_at) VALUES($p0,$p1,'not_duplicate',$p2)", P(left, right, stamp));
        c.Execute("INSERT INTO duplicate_reviews VALUES($p0,$p1,'not_duplicate',$p2) ON CONFLICT(left_entry_id,right_entry_id) DO UPDATE SET decision=excluded.decision,reviewed_at=excluded.reviewed_at", P(left, right, stamp));
    }
    private static void PersistBinding(DatabaseSession c, long entryId, Owned row, string stamp, bool regroup = true)
    {
        c.Execute("INSERT INTO manual_match_bindings(source_id,source_manga_id,group_id,entry_id_hint,source_name,source_title,record_json,created_at,updated_at) VALUES($p0,$p1,NULL,$p2,$p3,$p4,$p5,$p6,$p6) ON CONFLICT(source_id,source_manga_id) DO UPDATE SET entry_id_hint=excluded.entry_id_hint,source_name=CASE WHEN excluded.source_name<>'' THEN excluded.source_name ELSE manual_match_bindings.source_name END,source_title=CASE WHEN excluded.source_title<>'' THEN excluded.source_title ELSE manual_match_bindings.source_title END,record_json=excluded.record_json,grouping_suppressed=CASE WHEN $p7 THEN 0 ELSE manual_match_bindings.grouping_suppressed END,updated_at=excluded.updated_at", P(row.Key.SourceId, row.Key.MangaId, entryId, row.Row.Text("source_name"), row.Row.Text("source_title"), CanonicalJson.Text(Payload(row.Row)), stamp, regroup ? 1 : 0));
    }
    private static void RememberManualSplit(DatabaseSession c, long original, long id, IReadOnlyList<Owned> records, IReadOnlyList<Owned> selected, string stamp)
    {
        var keys = selected.Select(row => row.Key).ToHashSet(); var anchor = records.Where(row => !keys.Contains(row.Key)).OrderBy(row => row.Storage != "source_matches").ThenBy(row => row.Row.Integer("id")).First(); var oldGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in selected)
        {
            var group = c.Scalar("SELECT group_id FROM manual_match_bindings WHERE source_id=$p0 AND source_manga_id=$p1", P(row.Key.SourceId, row.Key.MangaId)) as string; if (!string.IsNullOrEmpty(group)) oldGroups.Add(group);
            c.Execute("UPDATE manual_match_bindings SET group_id=NULL,entry_id_hint=$p0,updated_at=$p1 WHERE source_id=$p2 AND source_manga_id=$p3", P(id, stamp, row.Key.SourceId, row.Key.MangaId));
            foreach (var table in new[] { "source_matches", "source_variants" }) c.Execute($"UPDATE {table} SET locked=1 WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2 AND active=1", P(id, row.Key.SourceId, row.Key.MangaId));
            PersistBinding(c, id, row, stamp); RememberSeparate(c, original, row.Key, id, row.Row.Text("source_title"), stamp);
        }
        if (c.Scalar("SELECT 1 FROM manual_match_bindings WHERE source_id=$p0 AND source_manga_id=$p1", P(anchor.Key.SourceId, anchor.Key.MangaId)) is null)
        {
            PersistBinding(c, original, anchor, stamp, regroup: false);
            foreach (var table in new[] { "source_matches", "source_variants" }) c.Execute($"UPDATE {table} SET locked=1 WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2", P(original, anchor.Key.SourceId, anchor.Key.MangaId));
        }
        foreach (var group in oldGroups)
            if (Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM manual_match_bindings WHERE group_id=$p0", P(group)), CultureInfo.InvariantCulture) < 2) { c.Execute("UPDATE manual_match_bindings SET group_id=NULL,updated_at=$p0 WHERE group_id=$p1", P(stamp, group)); c.Execute("DELETE FROM manual_match_groups WHERE id=$p0", P(group)); }
        EnsureGroup(c, id, stamp); SeparateReview(c, original, id, stamp);
    }
    private static void EnsureGroup(DatabaseSession c, long entryId, string stamp)
    {
        var bindings = c.Query("SELECT * FROM manual_match_bindings WHERE entry_id_hint=$p0 AND grouping_suppressed=0 ORDER BY created_at,source_id,source_manga_id", P(entryId)); if (bindings.Count < 2) return;
        var groups = bindings.Select(row => row.Text("group_id")).Where(group => group.Length != 0).ToArray(); var chosen = groups.FirstOrDefault() ?? Guid.NewGuid().ToString("N");
        var title = c.Scalar("SELECT preferred_title FROM entries WHERE id=$p0", P(entryId)) as string ?? bindings[0].Text("source_title", "Manual group");
        c.Execute("INSERT INTO manual_match_groups VALUES($p0,$p1,$p2,$p2) ON CONFLICT(id) DO UPDATE SET title=CASE WHEN manual_match_groups.title='' THEN excluded.title ELSE manual_match_groups.title END,updated_at=excluded.updated_at", P(chosen, title, stamp));
        foreach (var group in groups.Distinct(StringComparer.Ordinal).Where(group => group != chosen)) { c.Execute("UPDATE manual_match_bindings SET group_id=$p0,updated_at=$p1 WHERE group_id=$p2", P(chosen, stamp, group)); c.Execute("DELETE FROM manual_match_groups WHERE id=$p0", P(group)); }
        c.Execute("UPDATE manual_match_bindings SET group_id=$p0,entry_id_hint=$p1,updated_at=$p2 WHERE entry_id_hint=$p1 AND grouping_suppressed=0", P(chosen, entryId, stamp));
    }
}
