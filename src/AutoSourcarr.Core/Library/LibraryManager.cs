using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Library;

/// <summary>
/// AutoSourcarr's own library (CHAP-7, port of library_manager.py for the standalone case): membership, monitoring,
/// read-only observations (downloaded vs. catalogue chapters, source coverage), queueing missing chapters and the
/// download queue controls. 0.4.5 kept membership and downloads in main Suwayomi; here they live in the catalogue
/// database and the CBZ library folder. Every user change is recorded in the decision log with its actor; monitoring
/// runs as "library-monitor" jobs (task kind used by the safe replay) and queues new chapters automatically.
/// </summary>
public sealed class LibraryManager : IAsyncDisposable
{
    public const string MonitorKind = "library-monitor";
    public static readonly JobType MonitorJob = new(MonitorKind, "Monitor library downloads", JobPriority.Low, StallTimeout: TimeSpan.FromMinutes(15));
    /// <summary>Most chapters one "download missing" queues at once (the rest follow on the next check).</summary>
    public const int EnqueueBatch = 500;

    private readonly SqliteDatabase _database;
    private readonly LibraryStore _store;
    private readonly ILibraryCatalogue _catalogue;
    private readonly LibraryDownloads _downloads;
    private readonly JobQueue _jobs;
    private readonly IDecisionLog _decisions;
    private readonly LibraryOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _admin = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public LibraryManager(SqliteDatabase database, LibraryStore store, ILibraryCatalogue catalogue, LibraryDownloads downloads, JobQueue jobs,
        IDecisionLog decisions, TimeProvider? time = null)
    {
        _database = database; _store = store; _catalogue = catalogue; _downloads = downloads; _jobs = jobs; _decisions = decisions;
        _options = downloads.Options; _time = time ?? TimeProvider.System;
    }

    public LibraryStore Store => _store;
    public LibraryDownloads Downloads => _downloads;
    /// <summary>Overlay of locked display metadata on library rows (optional).</summary>
    public DisplayMetadata? Display { get; init; }
    /// <summary>Entry ids currently in duplicate review (the "duplicates" library filter; optional).</summary>
    public Func<IReadOnlyCollection<long>>? Duplicates { get; init; }

    /// <summary>Restart recovery of the queue, the download lanes and the monitoring loop.</summary>
    public void Start()
    {
        _downloads.Start();
        _loop ??= Task.Run(() => LoopAsync(_stop.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* Stopped. */ }
        }
        await _downloads.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
        _admin.Dispose();
    }

    // ---- reads -----------------------------------------------------------------------------------------------------

    /// <summary>GET /library: the filtered library page.</summary>
    public JsonObject Page(LibraryQuery query)
    {
        var members = _store.Members();
        var subscription = new JsonObject
        {
            ["ok"] = true, ["refreshing"] = false, ["source"] = "library", ["count"] = members.Count, ["library_root"] = _options.Root,
        };
        return LibraryProjection.Page(_database, query, members, Duplicates?.Invoke() ?? [], subscription, Display);
    }

    /// <summary>GET /library/{id}/controls: tracking, membership, the series folder and download counts.</summary>
    public JsonObject Controls(long entryId)
    {
        entryId = Canonical(entryId);
        var result = _store.Tracking(entryId);
        var series = _store.Series(entryId);
        result["in_library"] = series?.InLibrary ?? false;
        result["series_folder"] = series?.Folder;
        result["downloaded"] = _store.Downloaded(entryId).Count;
        result["queued"] = _store.Queue().Count(item => item.EntryId == entryId);
        result["library_root"] = _options.Root;
        return result;
    }

    // ---- membership and monitoring -----------------------------------------------------------------------------------

    /// <summary>PUT /library/{id}/membership. Removing keeps downloaded files and drops only queued chapters.</summary>
    public async Task<JsonObject> SetMembershipAsync(long entryId, bool inLibrary, bool monitored, long revision, DecisionActor actor, CancellationToken cancellationToken = default)
    {
        await _admin.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            entryId = Canonical(entryId);
            var series = _catalogue.Series(entryId) ?? throw new KeyNotFoundException("Work no longer exists");
            var before = Controls(entryId);
            if (before["revision"]!.GetValue<long>() != revision) throw new LibraryConflictException("Library controls changed; reload before applying");
            var place = _store.SetMembership(entryId, inLibrary, series.Title);
            _store.SaveTracking(entryId, expected: revision, monitored: inLibrary && monitored);
            var dropped = inLibrary ? 0 : _store.RemoveEntry(entryId, "Removed from library");
            var after = Controls(entryId);
            _decisions.Record(new DecisionEvent(inLibrary ? "library.add" : "library.remove", actor, [entryId], [],
                inLibrary ? "Added to the library as " + place.Folder : "Removed from the library; downloaded chapters are kept" + (dropped > 0 ? FormattableString.Invariant($", {dropped} queued chapters dropped") : ""),
                Summary(before), Summary(after)));
            after["reconciled"] = false;
            return after;
        }
        finally { _admin.Release(); }
    }

    /// <summary>PUT /library/{id}/monitoring.</summary>
    public async Task<JsonObject> SetMonitoringAsync(long entryId, bool monitored, long revision, DecisionActor actor, CancellationToken cancellationToken = default)
    {
        await _admin.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            entryId = Canonical(entryId);
            var before = Controls(entryId);
            if (monitored && before["in_library"]?.GetValue<bool>() != true) throw new LibraryConflictException("Add the work to your library before monitoring");
            _store.SaveTracking(entryId, expected: revision, monitored: monitored);
            var after = Controls(entryId);
            _decisions.Record(new DecisionEvent("library.monitoring", actor, [entryId], [], monitored ? "Monitoring new chapters" : "Monitoring off", Summary(before), Summary(after)));
            return after;
        }
        finally { _admin.Release(); }
    }

    // ---- observation -------------------------------------------------------------------------------------------------

    /// <summary>
    /// POST /library/{id}/observe (and the monitoring check): compares the catalogue chapters with the CBZ files on
    /// disk. Read-only towards sources unless <paramref name="refresh"/> asks for a fresh source chapter list first;
    /// safe to replay. An observation that fails never keeps presenting older counts as current.
    /// </summary>
    public async Task<JsonObject> ObserveAsync(long entryId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        entryId = Canonical(entryId);
        var key = entryId.ToString(CultureInfo.InvariantCulture);
        _store.Due.Attempted(LibraryStore.DueKind, key, Configuration());
        try
        {
            var observation = await ObserveCoreAsync(entryId, refresh, cancellationToken).ConfigureAwait(false);
            var complete = LibraryStore.Text(observation, "download_state") == "checked" && LibraryStore.Text(observation, "coverage_state") == "checked";
            _store.Due.Finished(LibraryStore.DueKind, key, complete: complete, success: true,
                reason: complete ? "" : "Source chapter observation remains incomplete", interval: _options.MonitorRecheckSeconds);
            return Controls(entryId);
        }
        catch (Exception error)
        {
            try
            {
                var current = _store.Tracking(entryId)["observation"]!.AsObject();
                var state = error is OperationCanceledException ? "deferred" : "unknown";
                var failed = (JsonObject)current.DeepClone();
                failed["download_state"] = state; failed["coverage_state"] = "verification"; failed["incomplete_reason"] = error.GetType().Name;
                _store.SaveTracking(entryId, observation: failed);
                _store.Due.Finished(LibraryStore.DueKind, key, deferred: state == "deferred", reason: error.GetType().Name);
            }
            catch (KeyNotFoundException) { /* The work is gone; nothing to record. */ }
            throw;
        }
    }

    private async Task<JsonObject> ObserveCoreAsync(long entryId, bool refresh, CancellationToken cancellationToken)
    {
        var place = _store.Series(entryId);
        if (place is not { InLibrary: true }) throw new LibraryConflictException("Add this work to the library before checking downloads");
        var started = _time.GetUtcNow();
        if (refresh) await _catalogue.RefreshAsync(entryId, cancellationToken).ConfigureAwait(false);
        var chapters = _catalogue.Chapters(entryId);
        var downloaded = PresentDownloads(entryId);
        var queued = _store.Queue().Where(item => item.EntryId == entryId).Select(item => item.ChapterId).ToHashSet();
        var missing = chapters.Count(chapter => !downloaded.Contains(chapter.ChapterId));
        var coverage = _catalogue.Coverage(entryId);
        var coverageChecked = coverage.Checked;
        if (refresh && coverageChecked)
            coverageChecked = DateTimeOffset.TryParse(coverage.CheckedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var checkedAt) && checkedAt >= started.AddSeconds(-1);
        var observation = new JsonObject
        {
            ["download_state"] = "checked", ["chapter_count"] = chapters.Count, ["downloaded"] = chapters.Count - missing,
            ["missing_downloads"] = missing, ["queued"] = chapters.Count(chapter => queued.Contains(chapter.ChapterId)),
            ["coverage_state"] = coverageChecked ? "checked" : "verification", ["source_gaps"] = coverage.SourceGaps,
            ["coverage_checked_at"] = coverage.CheckedAt, ["series_folder"] = place.Folder,
        };
        _store.SaveTracking(entryId, observation: observation);
        return observation;
    }

    /// <summary>Downloaded chapters whose CBZ is still on disk; vanished files are forgotten so they download again.</summary>
    private HashSet<long> PresentDownloads(long entryId)
    {
        var present = new HashSet<long>();
        foreach (var (chapterId, file) in _store.Downloaded(entryId))
        {
            string path;
            try { path = LibraryNames.Inside(_options.Root, file.Split('/')); }
            catch (ArgumentException) { _store.ForgetDownload(chapterId); continue; }
            if (File.Exists(path)) present.Add(chapterId);
            else _store.ForgetDownload(chapterId);
        }
        return present;
    }

    // ---- downloads -----------------------------------------------------------------------------------------------------

    /// <summary>POST /library/{id}/download: queues every catalogue chapter without a CBZ (failed ones are retried).</summary>
    public async Task<JsonObject> EnqueueMissingAsync(long entryId, DecisionActor actor, CancellationToken cancellationToken = default)
    {
        entryId = Canonical(entryId);
        var place = _store.Series(entryId);
        if (place is not { InLibrary: true }) throw new LibraryConflictException("Work is not in the library");
        var downloaded = PresentDownloads(entryId);
        var missing = _catalogue.Chapters(entryId).Where(chapter => !downloaded.Contains(chapter.ChapterId))
            .OrderBy(chapter => LibraryNames.Known(chapter.Number) ? 0 : 1).ThenBy(chapter => chapter.Volume).ThenBy(chapter => chapter.Number).ThenBy(chapter => chapter.ChapterId).ToArray();
        var batch = missing.Take(EnqueueBatch).ToArray();
        await _admin.WaitAsync(cancellationToken).ConfigureAwait(false);
        int queued;
        try { queued = _store.Enqueue(entryId, batch); }
        finally { _admin.Release(); }
        if (queued > 0)
            _decisions.Record(new DecisionEvent("library.download", actor, [entryId], [],
                FormattableString.Invariant($"Queued {queued} chapter downloads into {place.Folder}")));
        _downloads.Kick();
        return new JsonObject { ["queued"] = queued, ["remaining"] = Math.Max(0, missing.Length - batch.Length), ["missing"] = missing.Length };
    }

    /// <summary>GET /downloads.</summary>
    public JsonObject Queue() => _downloads.Snapshot(Titles());

    /// <summary>POST /downloads: start | stop | remove | move | retry, against the revision the person saw.</summary>
    public async Task<JsonObject> QueueActionAsync(string action, string revision, long chapterId, int? to, DecisionActor actor, CancellationToken cancellationToken = default)
    {
        await _admin.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = Queue();
            if (LibraryStore.Text(before, "revision") != revision) throw new LibraryConflictException("Queue changed; refresh before applying");
            var item = chapterId > 0 ? _store.Queued(chapterId) : null;
            switch (action)
            {
                case "start": _downloads.Resume(); break;
                case "stop": _downloads.Pause(); break;
                case "remove":
                    if (item is null) throw new LibraryConflictException("Chapter is no longer queued");
                    if (item.State == "downloading") throw new LibraryConflictException("Pause downloads before removing the chapter that is downloading");
                    _store.Remove(chapterId, "Removed from the queue by " + actor.Name);
                    break;
                case "move":
                    if (item is null) throw new LibraryConflictException("Chapter is no longer queued");
                    var count = before["queue"]!.AsArray().Count;
                    if (to is not { } index || index < 0 || index >= count) throw new LibraryConflictException("Invalid queue position");
                    _store.Move(chapterId, index);
                    break;
                case "retry":
                    if (item is null || item.State != "failed") throw new LibraryConflictException("Only a failed chapter can be retried");
                    _store.Retry(chapterId);
                    _downloads.Kick();
                    break;
                default: throw new ArgumentException("Unknown queue action");
            }
            _decisions.Record(new DecisionEvent("download.queue." + action, actor, item is null ? [] : [item.EntryId], [],
                item is null ? "Downloader " + (action == "start" ? "resumed" : "paused") : action + " " + ComicInfoDocument.ChapterTitle(item.Chapter)));
            var after = Queue();
            after["reconciled"] = false;
            return after;
        }
        finally { _admin.Release(); }
    }

    // ---- monitoring ----------------------------------------------------------------------------------------------------

    /// <summary>Queues a "library-monitor" job for every monitored work that is due; returns their handles.</summary>
    public IReadOnlyList<JobHandle> MonitorDue()
    {
        var handles = new List<JobHandle>();
        foreach (var entryId in _store.DueMonitored(Configuration()))
        {
            var id = entryId;
            handles.Add(_jobs.Enqueue(MonitorJob, id.ToString(CultureInfo.InvariantCulture), context => MonitorOneAsync(id, context),
                context: id.ToString(CultureInfo.InvariantCulture)));
        }
        return handles;
    }

    /// <summary>One monitoring check: refresh the source chapters, observe, queue whatever is new.</summary>
    public async Task MonitorOneAsync(long entryId, JobContext? job)
    {
        var token = job?.CancellationToken ?? _stop.Token;
        var tracking = _store.Tracking(entryId);
        if (tracking["monitored"]?.GetValue<bool>() != true) { job?.Result("Monitoring was turned off before this check started"); return; }
        job?.Report(0, 2, "Checking for new chapters");
        await ObserveAsync(entryId, refresh: true, token).ConfigureAwait(false);
        job?.Report(1, 2, "Queueing missing chapters");
        var queued = await EnqueueMissingAsync(entryId, DecisionActor.Automatic, token).ConfigureAwait(false);
        job?.Result(FormattableString.Invariant($"{queued["queued"]!.GetValue<int>()} new chapter downloads queued"));
    }

    /// <summary>Safe replay of an interrupted "library-monitor" task: only the read-only observation, nothing is queued.</summary>
    public async Task<JsonObject> SafeReplayAsync(TaskRecord task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!long.TryParse(task.Context, NumberStyles.None, CultureInfo.InvariantCulture, out var entryId) || entryId <= 0)
            throw new LibraryConflictException("Task identity is unavailable");
        var observed = await ObserveAsync(entryId, refresh: false, cancellationToken).ConfigureAwait(false);
        return new JsonObject { ["observed"] = observed, ["note"] = "Read-only observation retried; no previous change was replayed" };
    }

    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_options.MonitorInterval, _time);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            try { MonitorDue(); }
            catch (Exception error) when (error is not OperationCanceledException and not ObjectDisposedException)
            {
                /* A failed scan is retried on the next tick; individual checks report through their tasks. */
            }
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private long Canonical(long entryId) => entryId > 0 ? _catalogue.CanonicalEntryId(entryId) : throw new KeyNotFoundException("Work no longer exists");

    /// <summary>Monitoring clock revision: a change of the enabled sources makes every monitored work due (0.4.5 configuration()).</summary>
    private string Configuration()
    {
        var text = _database.Read(DatabaseFile.Catalog, c => string.Join(';', c.Query(
            "SELECT source_id,available,enabled_language,enabled_search,enabled_chapters,group_id FROM source_config ORDER BY source_id")
            .Select(row => string.Join(',', row.Values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))))));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("library-due-v2|" + text)))[..32];
    }

    private Func<long, string> Titles()
    {
        var titles = _database.Read(DatabaseFile.Catalog, c => c.Query(
            "SELECT e.id,e.preferred_title FROM entries e WHERE e.id IN (SELECT DISTINCT entry_id FROM download_queue)")
            .ToDictionary(row => row.Integer("id"), row => row.Text("preferred_title")));
        return id => titles.GetValueOrDefault(id, "Unknown work");
    }

    private static JsonObject Summary(JsonObject controls) => new()
    {
        ["in_library"] = controls["in_library"]?.DeepClone(), ["monitored"] = controls["monitored"]?.DeepClone(),
        ["series_folder"] = controls["series_folder"]?.DeepClone(), ["revision"] = controls["revision"]?.DeepClone(),
    };
}
