using System.Collections.Concurrent;
using System.Globalization;
using AutoSourcarr.Core.Jobs;
using ImageMagick;

namespace AutoSourcarr.Core.Library;

/// <summary>
/// The CBZ downloader (CHAP-7, "per-source downloads"): one job per source on the shared JobQueue (job key
/// "source:&lt;id&gt;", so a source never downloads two chapters at once while different sources run side by side up to
/// the job type's run limit), chapters in queue order, a pause between pages and between chapters, page retries with
/// back-off, resumable staging and atomic files. Pausing stops every lane; a running chapter is put back in the queue
/// and resumes from its staged pages.
/// </summary>
public sealed class LibraryDownloads : IAsyncDisposable
{
    /// <summary>Per-source download lanes: at most four sources at once, never two chapters of one source.</summary>
    public static readonly JobType DownloadJob = new("library-download", "Download chapters", JobPriority.Low, JobFlags.LongRunning,
        RunLimit: 4, StallTimeout: TimeSpan.FromMinutes(10));

    private readonly LibraryStore _store;
    private readonly ILibraryCatalogue _catalogue;
    private readonly IChapterPageSource _pages;
    private readonly JobQueue _jobs;
    private readonly LibraryOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    /// <summary>One writer per series folder: lanes of different sources may finish chapters of one series at once.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _folders = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _pause = new();
    private bool _disposed;

    public LibraryDownloads(LibraryStore store, ILibraryCatalogue catalogue, IChapterPageSource pages, JobQueue jobs, LibraryOptions options, TimeProvider? time = null)
    {
        _store = store; _catalogue = catalogue; _pages = pages; _jobs = jobs; _options = options; _time = time ?? TimeProvider.System;
    }

    public LibraryOptions Options => _options;

    /// <summary>Restart recovery and first start: interrupted chapters go back to the queue, then every lane starts.</summary>
    public void Start()
    {
        _store.ResetInterrupted();
        Kick();
    }

    /// <summary>Starts (or joins) the lane of every source with queued chapters, unless downloads are paused.</summary>
    public void Kick()
    {
        if (_store.Paused) return;
        foreach (var source in _store.QueuedSources()) Ensure(source);
    }

    public bool Paused => _store.Paused;

    /// <summary>Stops all lanes; running chapters return to the queue and resume later.</summary>
    public void Pause()
    {
        _store.Paused = true;
        CancellationTokenSource old;
        lock (_gate) { old = _pause; _pause = new CancellationTokenSource(); }
        // Not disposed: a lane may still be linking to it; cancelling is what stops the lanes.
        old.Cancel();
    }

    public void Resume()
    {
        _store.Paused = false;
        Kick();
    }

    /// <summary>The queue in the shape of the 0.4.5 download page (state, queue rows with manga/chapter, revision).</summary>
    public System.Text.Json.Nodes.JsonObject Snapshot(Func<long, string> title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var items = _store.Queue();
        var started = !_store.Paused;
        var rows = new System.Text.Json.Nodes.JsonArray();
        var position = 0;
        foreach (var item in items)
        {
            rows.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["state"] = item.State switch { "downloading" => "DOWNLOADING", "failed" => "ERROR", _ => "QUEUED" },
                ["progress"] = item.Progress, ["tries"] = item.Tries, ["position"] = position++, ["error"] = item.Error,
                ["source"] = new System.Text.Json.Nodes.JsonObject { ["id"] = item.SourceId, ["name"] = item.SourceName },
                ["chapter"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = item.ChapterId, ["name"] = ComicInfoDocument.ChapterTitle(item.Chapter),
                    ["number"] = LibraryNames.Known(item.Chapter.Number) ? item.Chapter.Number : null,
                },
                ["manga"] = new System.Text.Json.Nodes.JsonObject { ["id"] = item.EntryId, ["title"] = title(item.EntryId) },
                ["queued_at"] = item.QueuedAt,
            });
        }
        return new System.Text.Json.Nodes.JsonObject
        {
            ["state"] = started ? "STARTED" : "STOPPED", ["queue"] = rows, ["revision"] = LibraryStore.QueueRevision(started, items),
            ["library_root"] = _options.Root,
        };
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource pause;
        lock (_gate) { if (_disposed) return; _disposed = true; pause = _pause; }
        await pause.CancelAsync().ConfigureAwait(false);
        pause.Dispose();
    }

    private void Ensure(string sourceId)
    {
        lock (_gate) if (_disposed) return;
        var name = _store.Queue().FirstOrDefault(item => item.SourceId == sourceId)?.SourceName ?? "";
        JobHandle handle;
        try
        {
            handle = _jobs.Enqueue(DownloadJob, "source:" + sourceId, context => DrainAsync(sourceId, context),
                title: name.Length > 0 ? "Download chapters · " + name : "Download chapters", context: sourceId);
        }
        catch (ObjectDisposedException) { return; }
        // A chapter queued while the lane was finishing merges into the ending job; look again once it has ended.
        _ = handle.Completion.ContinueWith(task =>
        {
            try
            {
                lock (_gate) if (_disposed) return;
                if (task.Status == TaskStatus.RanToCompletion && task.Result == JobOutcome.Completed && !_store.Paused && _store.HasQueued(sourceId)) Ensure(sourceId);
            }
            catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { /* Shutting down. */ }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private CancellationToken PauseToken() { lock (_gate) return _pause.Token; }

    private async Task DrainAsync(string sourceId, JobContext job)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.CancellationToken, PauseToken());
        var token = linked.Token;
        var done = 0;
        while (!token.IsCancellationRequested && !_store.Paused)
        {
            var item = _store.Next(sourceId);
            if (item is null) break;
            if (!_store.Start(item.ChapterId)) continue;
            job.Report(done, null, "Downloading " + ComicInfoDocument.ChapterTitle(item.Chapter));
            try
            {
                await DownloadAsync(item, job, token).ConfigureAwait(false);
                done++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _store.Requeue(item.ChapterId);
                if (job.CancellationToken.IsCancellationRequested) throw;
                break;
            }
            catch (Exception error)
            {
                // Includes source timeouts (a cancellation that is not ours): the chapter fails and the lane moves on.
                _store.Fail(item, error.Message);
            }
            try { await Task.Delay(_options.ChapterDelay, _time, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!job.CancellationToken.IsCancellationRequested) { break; }
        }
        job.CancellationToken.ThrowIfCancellationRequested();
        job.Result(done == 1 ? "1 chapter downloaded" : done.ToString(CultureInfo.InvariantCulture) + " chapters downloaded");
    }

    /// <summary>Downloads one chapter into the library (also used directly by tests and the safe pipeline check).</summary>
    internal async Task DownloadAsync(DownloadItem item, JobContext? job, CancellationToken token)
    {
        var series = _catalogue.Series(item.EntryId) ?? throw new KeyNotFoundException("Work no longer exists");
        var place = _store.Series(item.EntryId) ?? throw new LibraryConflictException("Work is not in the library");
        var chapter = _catalogue.Chapters(item.EntryId).FirstOrDefault(row => row.ChapterId == item.ChapterId) ?? item.Chapter;
        var resolved = await _pages.ResolveAsync(item.ChapterId, token).ConfigureAwait(false);
        if (resolved.Count <= 0) throw new InvalidDataException("Source returned no chapter pages");

        var staging = Path.Combine(_options.StagingRoot, item.ChapterId.ToString(CultureInfo.InvariantCulture));
        PrepareStaging(staging, resolved);
        var files = new List<string>(resolved.Count);
        for (var index = 0; index < resolved.Count; index++)
        {
            if (CbzFiles.StagedPage(staging, index) is { } staged) { files.Add(staged); continue; }
            var image = await FetchPageAsync(resolved, index, token).ConfigureAwait(false);
            files.Add(CbzFiles.StagePage(staging, index, image));
            _store.Progress(item.ChapterId, (index + 1) / (double)resolved.Count);
            job?.Report(index + 1, resolved.Count, ComicInfoDocument.ChapterTitle(chapter));
            if (index + 1 < resolved.Count) await Task.Delay(_options.PageDelay, _time, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();

        var named = series with { Title = place.SeriesName };
        var folder = LibraryNames.Inside(_options.Root, place.Folder);
        // The folder step runs alone per series: the partial-file cleanup must not remove another lane's chapter in
        // progress, and cover.jpg / details.json are written by one lane at a time.
        var writer = _folders.GetOrAdd(place.Folder, _ => new SemaphoreSlim(1, 1));
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WriteChapterAsync(item, place, named, chapter, folder, files, resolved, token).ConfigureAwait(false);
        }
        finally { writer.Release(); }
        CbzFiles.TryDeleteFolder(staging);
    }

    private async Task WriteChapterAsync(DownloadItem item, LibrarySeries place, LibrarySeriesInfo named, LibraryChapterInfo chapter, string folder,
        List<string> files, LibraryChapterPages resolved, CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        CbzFiles.RemovePartials(folder);
        var fileName = LibraryNames.ChapterFile(place.Folder, chapter.Number, chapter.Volume, ComicInfoDocument.ChapterTitle(chapter));
        var relative = place.Folder + "/" + fileName;
        if (_store.FileOwner(relative) is { } owner && owner != item.ChapterId)
        {
            fileName = LibraryNames.ChapterFile(place.Folder, chapter.Number, chapter.Volume, ComicInfoDocument.ChapterTitle(chapter), item.ChapterId);
            relative = place.Folder + "/" + fileName;
        }
        var target = LibraryNames.Inside(_options.Root, place.Folder, fileName);
        var bytes = CbzFiles.Pack(target, files, ComicInfoDocument.Build(named, chapter, files.Count));
        await WriteSeriesFilesAsync(place, named, files[0], token).ConfigureAwait(false);

        // A renumbered chapter keeps one file: the old name goes once the new one is in place.
        if (_store.Downloaded(item.EntryId).TryGetValue(item.ChapterId, out var previous) && previous != relative)
            DeleteRelative(previous);
        _store.Complete(item, relative, files.Count, bytes, resolved.CandidateId);
    }

    private static void PrepareStaging(string staging, LibraryChapterPages resolved)
    {
        // Staged pages belong to one serving candidate; a different candidate (or page count) starts over.
        var marker = Path.Combine(staging, "candidate.txt");
        var expected = resolved.CandidateId.ToString(CultureInfo.InvariantCulture) + ":" + resolved.Count.ToString(CultureInfo.InvariantCulture);
        if (Directory.Exists(staging) && (!File.Exists(marker) || File.ReadAllText(marker).Trim() != expected)) CbzFiles.TryDeleteFolder(staging);
        Directory.CreateDirectory(staging);
        if (!File.Exists(marker)) CbzFiles.WriteAtomic(marker, System.Text.Encoding.ASCII.GetBytes(expected));
    }

    private async Task<LibraryImage> FetchPageAsync(LibraryChapterPages resolved, int index, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var image = await _pages.PageAsync(resolved, index, token).ConfigureAwait(false);
                if (image.Content.Length == 0 || image.Content.Length > _options.MaximumImageBytes)
                    throw new InvalidDataException(FormattableString.Invariant($"Page {index + 1} is empty or larger than the page limit"));
                if (!CbzFiles.IsImage(image.Content)) throw new InvalidDataException(FormattableString.Invariant($"Page {index + 1} is not an image"));
                return image;
            }
            catch (Exception error) when (error is not OperationCanceledException && attempt < Math.Max(1, _options.PageAttempts))
            {
                var delay = TimeSpan.FromTicks(_options.RetryDelay.Ticks * (1L << Math.Min(attempt - 1, 6)));
                await Task.Delay(delay, _time, token).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteSeriesFilesAsync(LibrarySeries place, LibrarySeriesInfo series, string firstPage, CancellationToken token)
    {
        var cover = LibraryNames.Inside(_options.Root, place.Folder, CbzFiles.CoverName);
        if (!File.Exists(cover))
        {
            LibraryImage? image = null;
            try { image = await _pages.CoverAsync(place.EntryId, token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { image = null; }
            if (image is null || image.Content.Length == 0 || !CbzFiles.IsImage(image.Content))
                image = new LibraryImage(await File.ReadAllBytesAsync(firstPage, token).ConfigureAwait(false), "");
            CbzFiles.WriteAtomic(cover, Jpeg(image.Content));
        }
        var details = LibraryNames.Inside(_options.Root, place.Folder, CbzFiles.DetailsName);
        var content = ComicInfoDocument.Details(series);
        // Mihon converts details.json into a series ComicInfo.xml and deletes it; once that exists, leave the folder alone.
        if (File.Exists(LibraryNames.Inside(_options.Root, place.Folder, CbzFiles.ComicInfoName))) return;
        if (!File.Exists(details) || !File.ReadAllBytes(details).AsSpan().SequenceEqual(content)) CbzFiles.WriteAtomic(details, content);
    }

    /// <summary>cover.jpg is a JPEG for every reader; other formats are converted, and kept as they are if that fails.</summary>
    private static byte[] Jpeg(byte[] content)
    {
        if (CbzFiles.Extension(content) == "jpg") return content;
        try
        {
            using var image = new MagickImage(content);
            image.Format = MagickFormat.Jpeg;
            return image.ToByteArray();
        }
        catch (MagickException) { return content; }
    }

    private void DeleteRelative(string relative)
    {
        var parts = relative.Split('/');
        try { CbzFiles.TryDelete(LibraryNames.Inside(_options.Root, parts)); }
        catch (ArgumentException) { /* Not a library path: nothing to remove. */ }
    }
}
