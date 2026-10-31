using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Unicode;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Catalogue.Structure.StructureSql;

namespace AutoSourcarr.Core.Catalogue.Chapters;

/// <summary>Complete record-wide native chapter persistence. Does not request sources, create attachments or schedule analysis.</summary>
public sealed class NativeChapterWriter(SqliteDatabase database, object sourceWriteGate, GalleryEvidenceService gallery,
    string privateWorkDirectory, TimeProvider? clock = null)
{
    internal sealed record Context(long EntryId, string SourceId, long MangaId, long MatchId, string SourceName, JsonObject Gallery, DatabaseRow Native, string Storage);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly GalleryEvidenceService _gallery = gallery ?? throw new ArgumentNullException(nameof(gallery));
    internal bool Owns(SqliteDatabase candidate, object gate) => ReferenceEquals(database, candidate) && ReferenceEquals(sourceWriteGate, gate);
    private static readonly Regex NamedNative = new(@"^\s*(?:chapter|ch\.?|episode|ep\.?)\s*[:#.-]?\s*\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    /// <summary>Disk-backed IDs keep pre-split activity bounded independently of
    /// chapter count. Only this exact writer can consume the owned snapshot.</summary>
    internal sealed class CachedGalleryCandidates(NativeChapterWriter owner, OwnedReplayFiles files, ReplayIdIndex active) : IDisposable
    {
        internal NativeChapterWriter Owner { get; } = owner;
        internal OwnedReplayFiles Files { get; } = files;
        internal ReplayIdIndex Active { get; } = active;
        public void Dispose() => Files.Dispose();
    }
    internal CachedGalleryCandidates CaptureActiveGalleryCandidates(long entryId)
    {
        if (!Monitor.IsEntered(sourceWriteGate)) throw new InvalidOperationException("Cached gallery capture requires the shared native writer gate.");
        var files = new OwnedReplayFiles(privateWorkDirectory);
        try
        {
            var sort = new ReplaySort(files, CancellationToken.None);
            database.Read(DatabaseFile.Origins, o => o.Savepoint(c =>
            {
                long cursor = 0; var first = true;
                while (true)
                {
                    var page = c.Query("""
                        SELECT cc.id FROM chapter_candidates cc JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id
                        WHERE vc.entry_id=$p0 AND cc.active=1 AND ($p1=1 OR cc.id>$p2) ORDER BY cc.id LIMIT 256
                        """, P(entryId, first ? 1 : 0, cursor));
                    if (page.Count == 0) break; first = false;
                    foreach (var row in page) { cursor = row.Integer("id"); sort.Add(ReplaySort.Integer(cursor)); }
                }
                return 0;
            }));
            var result = sort.Finish(); return new(this, files, new(result.File, result.Count));
        }
        catch { files.Dispose(); throw; }
    }
    internal void ReplayCachedGallery(long entryId, string sourceId, long mangaId, CachedGalleryCandidates saved)
    {
        if (!Monitor.IsEntered(sourceWriteGate) || !ReferenceEquals(saved.Owner, this)) throw new InvalidOperationException("Cached gallery replay requires its actual native writer and shared gate.");
        var input = saved.Files.Create(); var frozen = new FrozenChapterBatch(input);
        try
        {
            database.Read(DatabaseFile.Origins, outer => outer.Savepoint(o =>
            {
                // Freeze the complete eligible source record before deactivation.
                // Rejections remain facts and pass through Apply as inactive rows.
                long cursor = 0; var first = true;
                while (true)
                {
                    var page = o.Query("""
                        SELECT cc.*,vc.chapter_key FROM chapter_candidates cc JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id
                        WHERE cc.source_id=$p0 AND cc.source_manga_id=$p1 AND ($p2=1 OR cc.id>$p3) ORDER BY cc.id LIMIT 256
                        """, P(sourceId, mangaId, first ? 1 : 0, cursor));
                    if (page.Count == 0) break; first = false;
                    foreach (var row in page)
                    {
                        cursor = row.Integer("id");
                        if (!row.Flag("active") && !saved.Active.Contains(cursor) && o.Scalar("SELECT 1 FROM chapter_candidate_rejections WHERE source_id=$p0 AND source_chapter_id=$p1", P(sourceId, row.Integer("source_chapter_id"))) is null) continue;
                        var chapter = new NativeChapter(row.Integer("source_chapter_id"), row.Text("chapter_key").StartsWith("g:", StringComparison.Ordinal) ? -1 : row.Number("chapter_number"), row.Text("name"), row.Text("source_chapter_url"), row.Integer("source_order"), row.Number("volume_number"), row.Integer("upload_date"), row.Text("scanlator"), row.Integer("page_count"));
                        Validate(chapter); frozen.Append(new(chapter, Old(row)));
                    }
                }
                return 0;
            }));
            // Released reconciliation never asks for a match owner when this
            // source has no eligible cached chapters. Do not manufacture one.
            if (frozen.Count == 0) return;
            var context = Prepare(entryId, sourceId, mangaId); Verify(context);
            database.Write(DatabaseFile.Origins, o => Apply(o, context, frozen, saved.Files, updateScan: false, collectEffects: false, CancellationToken.None));
        }
        finally { saved.Files.Release(input); }
    }
    public ChapterWriteResult Write(ChapterBatch batch, bool updateScan = true, CancellationToken cancellationToken = default)
    {
        lock (sourceWriteGate)
        {
            cancellationToken.ThrowIfCancellationRequested(); var context = Prepare(batch.EntryId, batch.SourceId, batch.MangaId);
            using var files = new OwnedReplayFiles(privateWorkDirectory);
            Verify(context);
            return database.Write(DatabaseFile.Origins, o =>
            {
                var frozen = new FrozenChapterBatch(files.Create());
                foreach (var ch in batch.Chapters)
                {
                    cancellationToken.ThrowIfCancellationRequested(); Validate(ch);
                    var old = o.Query("SELECT id,source_manga_id,source_order,virtual_chapter_id FROM chapter_candidates WHERE source_id=$p0 AND source_manga_id=$p1 AND source_chapter_id=$p2", P(context.SourceId, context.MangaId, ch.Id)).FirstOrDefault();
                    frozen.Append(new(ch, old is null ? null : Old(old)));
                }
                return Apply(o, context, frozen, files, updateScan, collectEffects: true, cancellationToken);
            });
        }
    }
    internal Context Prepare(long entryId, string sourceId, long mangaId)
    {
        var context = database.Read(DatabaseFile.Catalog, c =>
        {
            entryId = PortableIdentityResolver.Canonical(c, entryId);
            var records = c.Query("SELECT 'source_matches' storage,* FROM source_matches WHERE source_id=$p0 AND source_manga_id=$p1 AND active=1 UNION ALL SELECT 'source_variants' storage,* FROM source_variants WHERE source_id=$p0 AND source_manga_id=$p1 AND active=1", P(sourceId, mangaId));
            var owners = records.Select(row => PortableIdentityResolver.Canonical(c, row.Integer("entry_id"))).Distinct().ToArray();
            if (owners.Length != 1 || owners[0] != entryId || c.Scalar("SELECT 1 FROM entries WHERE id=$p0", P(entryId)) is null) throw new InvalidOperationException("Chapter record is detached, moved or ambiguously owned");
            var match = c.Scalar("SELECT id FROM source_matches WHERE entry_id=$p0 AND source_id=$p1", P(entryId, sourceId));
            if (match is null) throw new InvalidOperationException("Native chapter record has no actual primary source-match owner");
            var row = records[0]; var name = c.Scalar("SELECT name FROM source_config WHERE source_id=$p0", P(sourceId)) as string;
            return new Context(entryId, sourceId, mangaId, Convert.ToInt64(match, CultureInfo.InvariantCulture), string.IsNullOrEmpty(name) ? sourceId : name, new(), row, row.Text("storage"));
        });
        var cleaned = _gallery.CleanNative(sourceId, GalleryPayload(context.Native));
        return context with { Gallery = cleaned["_gallery_metadata"] is JsonObject meta ? (JsonObject)meta.DeepClone() : new() };
    }
    internal void Verify(Context context)
    {
        database.Read(DatabaseFile.Catalog, c =>
        {
            var owners = c.Query("SELECT entry_id FROM source_matches WHERE source_id=$p0 AND source_manga_id=$p1 AND active=1 UNION ALL SELECT entry_id FROM source_variants WHERE source_id=$p0 AND source_manga_id=$p1 AND active=1", P(context.SourceId, context.MangaId)).Select(row => PortableIdentityResolver.Canonical(c, row.Integer("entry_id"))).Distinct().ToArray();
            var current = c.Query($"SELECT * FROM {context.Storage} WHERE id=$p0 AND active=1", P(context.Native.Integer("id"))).FirstOrDefault();
            if (owners.Length != 1 || owners[0] != context.EntryId || current is null || new[] { "entry_id", "source_id", "source_manga_id", "source_title", "metadata_json", "locked", "match_type" }.Any(name => !Equals(current[name], context.Native[name])))
                throw new InvalidOperationException("Native chapter ownership/metadata changed before persistence");
            return 0;
        });
    }
    internal static OldChapter Old(DatabaseRow row) => new(row.Integer("id"), row.Integer("source_manga_id"), row.Integer("source_order"), row.Integer("virtual_chapter_id"));
    internal static void Validate(NativeChapter chapter)
    { if (!double.IsFinite(chapter.Number) || !double.IsFinite(chapter.Volume)) throw new InvalidDataException("Native chapter/volume number is not finite"); }
    internal ChapterWriteResult Apply(DatabaseSession o, Context context, FrozenChapterBatch batch, OwnedReplayFiles files,
        bool updateScan, bool collectEffects, CancellationToken cancellationToken)
    {
        var stamp = _clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        var galleryMetadata = context.Gallery; var isGallery = galleryMetadata.Count != 0; var sort = new ReplaySort(files, cancellationToken);
        var allKeys = true; (string Key, double Number) first = ("t:", -1); NativeChapter? single = null;
        foreach (var item in batch.Read())
        {
            cancellationToken.ThrowIfCancellationRequested(); Validate(item.Chapter); var key = ChapterNumbers.Key(item.Chapter.Number, item.Chapter.Name);
            if (single is null) { single = item.Chapter; first = key; }
            allKeys &= key.Key.Length != 0 && key.Key != "t:"; sort.Add(Encoding.UTF8.GetBytes(key.Key));
        }
        var proof = sort.Finish(); var nativeList = batch.Count > 1 && allKeys && proof.Count == batch.Count;
        files.Release(proof.File);
        var explicitNumber = galleryMetadata["chapter_number"] is null ? (double?)null : ChapterNumbers.Float(galleryMetadata["chapter_number"]);
        if (explicitNumber.HasValue && !double.IsFinite(explicitNumber.Value)) throw new InvalidDataException("Native gallery chapter number is not finite");
        if (batch.Count == 1 && explicitNumber is null && first.Key.StartsWith("n:", StringComparison.Ordinal) && first.Number is not (0 or 1) && single is { } only && (only.Number >= 0 || NamedNative.IsMatch(ChapterNumbers.RegexText(only.Name)))) nativeList = true;
        var touched = collectEffects ? new HashSet<long>() : null; var invalidated = collectEffects ? new HashSet<long>() : null;
        o.Execute("UPDATE chapter_candidates SET active=0 WHERE source_id=$p0 AND source_manga_id=$p1 AND active=1", P(context.SourceId, context.MangaId));
        foreach (var item in batch.Read())
        {
            cancellationToken.ThrowIfCancellationRequested(); var ch = item.Chapter; var old = item.Old;
            if (isGallery && !nativeList) ch = ch with { Name = Text(galleryMetadata, "raw_title").Length != 0 ? Text(galleryMetadata, "raw_title") : ch.Name.Length != 0 ? ch.Name : "Gallery" };
            var rejected = o.Scalar("SELECT 1 FROM chapter_candidate_rejections WHERE source_id=$p0 AND source_chapter_id=$p1", P(context.SourceId, ch.Id)) is not null;
            var (key, number) = ChapterNumbers.Key(ch.Number, ch.Name);
            if (isGallery && old is not null)
            { var actual = o.Scalar("SELECT virtual_chapter_id FROM chapter_candidates WHERE id=$p0", P(old.Id)); if (actual is not null) old = old with { VirtualId = Convert.ToInt64(actual, CultureInfo.InvariantCulture) }; }
            if (isGallery && !nativeList)
            {
                if (explicitNumber.HasValue && batch.Count == 1) { number = explicitNumber.Value; key = "n:" + ChapterNumbers.Format(number); }
                else
                {
                    key = $"g:{context.SourceId}:{context.MangaId}:{ch.Id}";
                    var known = o.Scalar("SELECT chapter_number FROM virtual_chapters WHERE entry_id=$p0 AND chapter_key=$p1", P(context.EntryId, key));
                    number = known is not null ? Convert.ToDouble(known, CultureInfo.InvariantCulture) : Math.Max(1, Math.Floor(Convert.ToDouble(o.Scalar("SELECT COALESCE(MAX(chapter_number),0)+1 FROM virtual_chapters WHERE entry_id=$p0", P(context.EntryId)), CultureInfo.InvariantCulture)));
                }
            }
            if (key.Length == 0 || key == "t:") continue;
            var existing = o.Scalar("SELECT id FROM virtual_chapters WHERE entry_id=$p0 AND chapter_key=$p1", P(context.EntryId, key)); long? vid = existing is null ? null : Convert.ToInt64(existing, CultureInfo.InvariantCulture);
            if (isGallery && vid is null && old is not null)
            {
                var oldVirtual = o.Query("SELECT * FROM virtual_chapters WHERE id=$p0", P(old.VirtualId)).FirstOrDefault();
                var occupants = Convert.ToInt64(o.Scalar("SELECT COUNT(*) FROM chapter_candidates WHERE virtual_chapter_id=$p0", P(old.VirtualId)), CultureInfo.InvariantCulture);
                var served = o.Scalar("SELECT candidate_id FROM served_origin WHERE virtual_chapter_id=$p0", P(old.VirtualId));
                var firstCandidate = o.Scalar("SELECT MIN(id) FROM chapter_candidates WHERE virtual_chapter_id=$p0", P(old.VirtualId));
                var ownsOld = served is not null ? Convert.ToInt64(served, CultureInfo.InvariantCulture) == old.Id : firstCandidate is not null && Convert.ToInt64(firstCandidate, CultureInfo.InvariantCulture) == old.Id;
                if (oldVirtual is not null && (occupants == 1 || ownsOld && !oldVirtual.Text("chapter_key").StartsWith("g:", StringComparison.Ordinal)))
                {
                    o.Execute("UPDATE virtual_chapters SET entry_id=$p0,chapter_key=$p1,chapter_number=$p2 WHERE id=$p3", P(context.EntryId, key, number, old.VirtualId)); vid = old.VirtualId;
                    if (occupants > 1)
                    {
                        o.Execute("INSERT INTO virtual_chapters(entry_id,chapter_key,chapter_number,volume_number,display_name,hidden,created_at,updated_at) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)", P(oldVirtual.Integer("entry_id"), oldVirtual.Text("chapter_key"), oldVirtual.Number("chapter_number"), oldVirtual.Number("volume_number"), oldVirtual.Text("display_name"), oldVirtual.Integer("hidden"), oldVirtual.Text("created_at"), stamp));
                        var remainder = Convert.ToInt64(o.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture); long cursor = 0; var firstPage = true;
                        while (true)
                        {
                            var page = o.Query("SELECT id FROM chapter_candidates WHERE virtual_chapter_id=$p0 AND id<>$p1 AND ($p2=1 OR id>$p3) ORDER BY id LIMIT 256", P(old.VirtualId, old.Id, firstPage ? 1 : 0, cursor)); if (page.Count == 0) break; firstPage = false;
                            foreach (var row in page) { cancellationToken.ThrowIfCancellationRequested(); cursor = row.Integer("id"); MoveHistory(o, cursor, old.VirtualId, remainder); }
                        }
                        o.Execute("UPDATE chapter_candidates SET virtual_chapter_id=$p0 WHERE virtual_chapter_id=$p1 AND id<>$p2", P(remainder, old.VirtualId, old.Id));
                    }
                }
            }
            var display = ch.Name.Length != 0 ? ch.Name : "Chapter " + number.ToString("G6", CultureInfo.InvariantCulture).Replace('E', 'e');
            // Changed rows only (PERF-5): an unchanged chapter keeps its row and updated_at.
            if (vid.HasValue) o.Execute("UPDATE virtual_chapters SET display_name=$p0,chapter_number=$p1,volume_number=$p2,updated_at=$p3 WHERE id=$p4 AND (display_name,chapter_number,volume_number) IS NOT ($p0,$p1,$p2)", P(display, number, ch.Volume, stamp, vid.Value));
            else
            {
                o.Execute("INSERT INTO virtual_chapters(entry_id,chapter_key,chapter_number,volume_number,display_name,created_at,updated_at) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p5)", P(context.EntryId, key, number, ch.Volume, ch.Name.Length != 0 ? ch.Name : number >= 0 ? display : "Chapter", stamp)); vid = Convert.ToInt64(o.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
            }
            touched?.Add(vid.Value);
            if (isGallery && old is not null) MoveHistory(o, old.Id, old.VirtualId, vid.Value);
            if (item.Old is { } initial && (initial.MangaId != context.MangaId || initial.Order != ch.SourceOrder)) { o.Execute("DELETE FROM candidate_pages WHERE candidate_id=$p0", P(initial.Id)); invalidated?.Add(initial.Id); }
            // PERF-5: an unchanged chapter is not rewritten, so last_seen means "last changed"; the scan time per source title is
            // source_record_scans.checked_at below. Nothing decides on last_seen.
            o.Execute("""
                INSERT INTO chapter_candidates(virtual_chapter_id,source_id,source_name,source_match_id,source_manga_id,source_chapter_id,
                  source_chapter_url,source_order,source_chapter_index,name,chapter_number,volume_number,upload_date,scanlator,page_count,active,first_seen,last_seen)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,-1,$p8,$p9,$p10,$p11,$p12,$p13,$p14,$p15,$p15)
                ON CONFLICT(source_id,source_chapter_id) DO UPDATE SET virtual_chapter_id=excluded.virtual_chapter_id,source_name=excluded.source_name,source_match_id=excluded.source_match_id,
                  source_manga_id=excluded.source_manga_id,source_chapter_url=excluded.source_chapter_url,source_order=excluded.source_order,
                  source_chapter_index=CASE WHEN chapter_candidates.source_manga_id=excluded.source_manga_id AND chapter_candidates.source_order=excluded.source_order THEN chapter_candidates.source_chapter_index ELSE -1 END,
                  name=excluded.name,chapter_number=excluded.chapter_number,volume_number=excluded.volume_number,upload_date=excluded.upload_date,
                  scanlator=excluded.scanlator,page_count=CASE WHEN excluded.page_count>0 THEN excluded.page_count ELSE chapter_candidates.page_count END,active=excluded.active,last_seen=excluded.last_seen
                WHERE (chapter_candidates.virtual_chapter_id,chapter_candidates.source_name,chapter_candidates.source_match_id,chapter_candidates.source_manga_id,
                       chapter_candidates.source_chapter_url,chapter_candidates.source_order,chapter_candidates.name,chapter_candidates.chapter_number,
                       chapter_candidates.volume_number,chapter_candidates.upload_date,chapter_candidates.scanlator,chapter_candidates.active)
                  IS NOT (excluded.virtual_chapter_id,excluded.source_name,excluded.source_match_id,excluded.source_manga_id,
                       excluded.source_chapter_url,excluded.source_order,excluded.name,excluded.chapter_number,
                       excluded.volume_number,excluded.upload_date,excluded.scanlator,excluded.active)
                  OR (excluded.page_count>0 AND excluded.page_count IS NOT chapter_candidates.page_count)
                """, P(vid.Value, context.SourceId, context.SourceName, context.MatchId, context.MangaId, ch.Id, ch.Url, ch.SourceOrder, ch.Name.Length == 0 ? "Chapter" : ch.Name, number, ch.Volume, ch.UploadDate, ch.Scanlator, ch.PageCount, rejected ? 0 : 1, stamp));
        }
        if (updateScan) o.Execute("INSERT INTO source_record_scans VALUES($p0,$p1,$p2,$p3,$p4,'') ON CONFLICT(entry_id,source_id,source_manga_id) DO UPDATE SET checked_at=excluded.checked_at,chapter_count=excluded.chapter_count,error=''", P(context.EntryId, context.SourceId, context.MangaId, stamp, batch.Count));
        return new(touched?.Order().ToArray() ?? [], invalidated?.Order().ToArray() ?? []);
    }
    private static void MoveHistory(DatabaseSession o, long candidate, long old, long target)
    {
        if (old == target) return;
        o.Execute("UPDATE origin_history SET virtual_chapter_id=$p0 WHERE candidate_id=$p1 AND virtual_chapter_id=$p2", P(target, candidate, old));
        var served = o.Query("SELECT * FROM served_origin WHERE virtual_chapter_id=$p0 AND candidate_id=$p1", P(old, candidate)).FirstOrDefault(); if (served is null) return;
        var selected = o.Scalar("SELECT selected_at FROM served_origin WHERE virtual_chapter_id=$p0", P(target)) as string;
        if (selected is null || UnicodeCodePointComparer.Instance.Compare(served.Text("selected_at"), selected) > 0) o.Execute("INSERT OR REPLACE INTO served_origin VALUES($p0,$p1,$p2,$p3)", P(target, candidate, served.Text("selected_at"), served.Text("reason")));
        o.Execute("DELETE FROM served_origin WHERE virtual_chapter_id=$p0 AND candidate_id=$p1", P(old, candidate));
    }
}
