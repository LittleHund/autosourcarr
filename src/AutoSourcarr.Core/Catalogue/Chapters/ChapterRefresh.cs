using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Chapters;

/// <summary>
/// Chapter refreshes (0.4.5 refresh_source_record_chapters, refresh_entry's chapter part, schedule_entry_refresh_job):
/// one attached source record, or every record of a work whose source serves chapters, fetched from the source and
/// written as one complete record batch (<see cref="NativeChapterWriter"/>). A failed check is remembered on the record
/// (source_record_scans.error) without touching its chapters. READ-3: <see cref="ScheduleIfStale"/> lets readers get the
/// stored chapters at once while a refresh job brings newer source data.
/// </summary>
public sealed class ChapterRefresh(SqliteDatabase database, object sourceWriteGate, NativeChapterWriter writer,
    Func<long, bool, bool, CancellationToken, Task<JsonObject>> fetch, Func<TimeSpan>? sourceTimeout = null, TimeProvider? clock = null)
{
    public static readonly JobType CheckJob = new("chapter-check", "Check chapters", JobPriority.High, RunLimit: 2, StallTimeout: TimeSpan.FromMinutes(5));
    public static readonly JobType EntryJob = new("entry-chapter-refresh", "Refresh chapters", JobPriority.Normal, RunLimit: 2, StallTimeout: TimeSpan.FromMinutes(10));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private CatalogueQueries Queries => new(database, sourceWriteGate, _ => false);

    /// <summary>Runs after chapters of a record were stored (metadata hydration, colour analysis …); errors are the caller's.</summary>
    public Action<long, string, long>? Stored { get; init; }

    public sealed record Record(long EntryId, string SourceId, long MangaId, string SourceName);

    /// <summary>Refreshes one attached record; <see cref="KeyNotFoundException"/> when it is not attached to the work.</summary>
    public async Task<JsonObject> RefreshRecordAsync(long entryId, string sourceId, long mangaId, CancellationToken cancellationToken = default)
    {
        var record = Attached(entryId, sourceId, mangaId) ?? throw new KeyNotFoundException("Attached source record not found");
        var count = await StoreAsync(record, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["ok"] = true, ["entry_id"] = record.EntryId, ["source_id"] = sourceId, ["source_manga_id"] = mangaId, ["chapters_found"] = count,
            ["stats"] = Queries.RecordChapterStats([record.EntryId]).GetValueOrDefault((record.EntryId, sourceId, mangaId))?.DeepClone() ?? new JsonObject(),
            ["entry_stats"] = Queries.EntryChapterStats([record.EntryId]).GetValueOrDefault(record.EntryId)?.DeepClone() ?? new JsonObject(),
        };
    }

    /// <summary>Refreshes every chapter-serving record of a work; one failing record does not stop the others.</summary>
    public async Task<JsonObject> RefreshEntryAsync(long entryId, JobContext? job = null, CancellationToken cancellationToken = default)
    {
        var records = Records(entryId);
        job?.Report(0, records.Count, "Checking " + records.Count.ToString(CultureInfo.InvariantCulture) + " source record(s)");
        var failures = new JsonArray(); var done = 0; var found = 0L;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { found += await StoreAsync(record, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { failures.Add(new JsonObject { ["source_id"] = record.SourceId, ["source_manga_id"] = record.MangaId, ["error"] = error.Message }); }
            job?.Report(++done, records.Count, record.SourceName);
        }
        var canonical = records.Count > 0 ? records[0].EntryId : Canonical(entryId);
        return new JsonObject
        {
            ["entry_id"] = canonical, ["records"] = records.Count, ["chapters_found"] = found, ["failures"] = failures,
            ["entry_stats"] = Queries.EntryChapterStats([canonical]).GetValueOrDefault(canonical)?.DeepClone() ?? new JsonObject(),
        };
    }

    /// <summary>Queues a whole-work chapter refresh as a job (merged with one already queued for the work).</summary>
    public JobHandle ScheduleEntry(JobQueue jobs, long entryId, string origin = "manual", JobPriority? priority = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var canonical = Canonical(entryId);
        var title = database.Read(DatabaseFile.Catalog, session => session.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", canonical)) as string)
            ?? throw new KeyNotFoundException("Entry not found");
        return jobs.Enqueue(EntryJob, "entry-chapter-refresh:" + canonical.ToString(CultureInfo.InvariantCulture), async job =>
        {
            var result = await RefreshEntryAsync(canonical, job, job.CancellationToken).ConfigureAwait(false);
            job.Result(result["chapters_found"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture) + " chapter(s) found, "
                + result["failures"]!.AsArray().Count.ToString(CultureInfo.InvariantCulture) + " record(s) failed");
        }, priority, origin, "Refresh chapters", title);
    }

    /// <summary>
    /// READ-3: when any chapter-serving record of the work was never checked or was last checked longer ago than
    /// <paramref name="maximumAge"/>, a low-priority refresh job is queued; the caller answers from stored data meanwhile.
    /// Returns whether a refresh was queued (or already waiting).
    /// </summary>
    public bool ScheduleIfStale(JobQueue? jobs, long entryId, TimeSpan maximumAge)
    {
        if (jobs is null || maximumAge <= TimeSpan.Zero) return false;
        var records = Records(entryId);
        if (records.Count == 0) return false;
        var canonical = records[0].EntryId;
        var scans = database.Read(DatabaseFile.Origins, session => session.Query("SELECT source_id,source_manga_id,checked_at FROM source_record_scans WHERE entry_id=$id", ("$id", canonical)))
            .ToDictionary(row => (row.Text("source_id"), row.Integer("source_manga_id")), row => row.Text("checked_at"));
        var now = _clock.GetUtcNow();
        var stale = records.Any(record => !scans.TryGetValue((record.SourceId, record.MangaId), out var checkedAt)
            || !DateTimeOffset.TryParse(checkedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp) || now - stamp > maximumAge);
        if (!stale) return false;
        ScheduleEntry(jobs, canonical, "automatic", JobPriority.Low);
        return true;
    }

    /// <summary>The work's active records whose source is available and serves chapters (0.4.5 refresh_entry), by priority.</summary>
    public IReadOnlyList<Record> Records(long entryId) => database.Read(DatabaseFile.Catalog, session =>
    {
        var canonical = Sharing.PortableIdentityResolver.Canonical(session, entryId);
        return (IReadOnlyList<Record>)session.Query("""
            SELECT r.source_id,r.source_manga_id,COALESCE(sc.name,r.source_id) source_name FROM (
              SELECT source_id,source_manga_id FROM source_matches WHERE entry_id=$id AND active=1
              UNION SELECT source_id,source_manga_id FROM source_variants WHERE entry_id=$id AND active=1) r
            JOIN source_config sc ON sc.source_id=r.source_id
            WHERE sc.available=1 AND sc.enabled_language=1 AND sc.enabled_chapters=1 ORDER BY sc.priority,r.source_id,r.source_manga_id
            """, ("$id", canonical)).Select(row => new Record(canonical, row.Text("source_id"), row.Integer("source_manga_id"), row.Text("source_name"))).ToArray();
    });

    private async Task<long> StoreAsync(Record record, CancellationToken cancellationToken)
    {
        JsonArray chapters;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var limit = sourceTimeout?.Invoke() ?? TimeSpan.FromSeconds(12);
            timeout.CancelAfter(limit <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : limit);
            var payload = await fetch(record.MangaId, false, true, timeout.Token).ConfigureAwait(false);
            chapters = payload["chapters"] as JsonArray ?? new JsonArray();
            var batch = new ChapterBatch(record.EntryId, record.SourceId, record.MangaId, chapters.OfType<JsonObject>().Select(NativeChapter.FromJson).ToArray());
            writer.Write(batch, updateScan: true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Failed(record, error is OperationCanceledException ? "Source did not answer in time" : error.Message);
            throw;
        }
        Stored?.Invoke(record.EntryId, record.SourceId, record.MangaId);
        return chapters.Count;
    }

    private void Failed(Record record, string message)
    {
        if (Attached(record.EntryId, record.SourceId, record.MangaId) is null) return;
        var text = message.Length > 2000 ? message[..2000] : message;
        lock (sourceWriteGate)
            database.Write(DatabaseFile.Origins, session => session.Execute("""
                INSERT INTO source_record_scans(entry_id,source_id,source_manga_id,checked_at,chapter_count,error) VALUES($entry,$source,$manga,$time,0,$error)
                ON CONFLICT(entry_id,source_id,source_manga_id) DO UPDATE SET checked_at=excluded.checked_at,error=excluded.error
                """, ("$entry", record.EntryId), ("$source", record.SourceId), ("$manga", record.MangaId),
                ("$time", _clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)), ("$error", text)));
    }

    private Record? Attached(long entryId, string sourceId, long mangaId) => database.Read(DatabaseFile.Catalog, session =>
    {
        var canonical = Sharing.PortableIdentityResolver.Canonical(session, entryId);
        var row = session.Query("""
            SELECT COALESCE(sc.name,r.source_id) source_name FROM (
              SELECT source_id FROM source_matches WHERE entry_id=$id AND source_id=$source AND source_manga_id=$manga AND active=1
              UNION ALL SELECT source_id FROM source_variants WHERE entry_id=$id AND source_id=$source AND source_manga_id=$manga AND active=1) r
            LEFT JOIN source_config sc ON sc.source_id=r.source_id LIMIT 1
            """, ("$id", canonical), ("$source", sourceId), ("$manga", mangaId)).FirstOrDefault();
        return row is null ? null : new Record(canonical, sourceId, mangaId, row.Text("source_name"));
    });

    private long Canonical(long entryId) => database.Read(DatabaseFile.Catalog, session => Sharing.PortableIdentityResolver.Canonical(session, entryId));
}
