using System.Globalization;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Collections;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Core.Reader;

public sealed class ChapterPagesBlockedException(string name, string detail, int retryAfter, JsonObject candidate)
    : Exception($"{name}: chapter-page lookup is temporarily blocked: {detail}")
{
    public int RetryAfter { get; } = retryAfter;
    public JsonObject SourceContext { get; } = (JsonObject)candidate.DeepClone();
}

/// <summary>Owned, coalesced page resolution and image streaming over exact persisted source provenance.</summary>
public sealed class ReaderPageService(ReaderRepository repository, IReaderPageSource source, BoundedWorkScheduler io,
    TimeSpan pageCacheTtl, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly OperationOwner _owner = new();
    private readonly object _gate = new();
    private readonly Dictionary<long, Lookup> _lookups = [];
    private readonly HashSet<ReaderImageStream> _images = [];
    private readonly Dictionary<(string Source, long Chapter), (DateTimeOffset Until, string Detail)> _failures = [];
    private Task? _stop;

    public async Task<JsonObject> ResolvePagesAsync(long chapterId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Lookup lookup;
        lock (_gate)
        {
            if (_owner.IsStopping) throw new SourceEngineUnavailableException("Chapter page lookup is unavailable during shutdown or reset; retry shortly");
            if (!_lookups.TryGetValue(chapterId, out lookup!))
            {
                lookup = new(CancellationTokenSource.CreateLinkedTokenSource(_owner.CancellationToken));
                _lookups.Add(chapterId, lookup);
                lookup.Work = ResolveOwnedAsync(chapterId, lookup.Cancellation.Token);
                _ = lookup.Work.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            lookup.Waiters++;
        }
        try
        {
            var result = await lookup.Work.WaitAsync(cancellationToken).ConfigureAwait(false);
            return (JsonObject)result.DeepClone();
        }
        finally
        {
            var last = false;
            lock (_gate)
            {
                if (--lookup.Waiters == 0)
                {
                    last = true;
                    if (_lookups.GetValueOrDefault(chapterId) == lookup) _lookups.Remove(chapterId);
                }
            }
            if (last)
            {
                try
                {
                    try { await lookup.Cancellation.CancelAsync().ConfigureAwait(false); }
                    finally { try { await lookup.Work.ConfigureAwait(false); } catch (Exception) { /* Callers observe the original error; this joins physical cleanup. */ } }
                }
                finally { lookup.Cancellation.Dispose(); }
            }
        }
    }

    public async Task<ReaderImageStream> OpenImageAsync(long candidateId, int pageIndex, CancellationToken cancellationToken = default)
    {
        var lease = _owner.Enter();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken);
        ReaderImageStream? upstream = null;
        ReaderImageStream? published = null;
        try
        {
            await using var operation = (source as IReaderScopedPageSource)?.OpenOperation(linked.Token);
            var currentSource = operation?.Source ?? source;
            var info = await io.RunAsync(() => repository.PageFetchInfo(candidateId, pageIndex), linked.Token).ConfigureAwait(false);
            operation?.ThrowIfRetired();
            var index = checked((int)ReaderValue.Integer(info, "source_chapter_index", -1));
            if (index == -1)
            {
                try { index = await currentSource.ChapterIndexAsync(ReaderValue.Integer(info, "source_manga_id"), ReaderValue.Integer(info, "source_chapter_id"), linked.Token).ConfigureAwait(false); }
                catch (SourceWorkDeferredException) { index = -1; }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { index = -2; }
                operation?.ThrowIfRetired();
                if (index != -1) await io.RunAsync(() => { operation?.ThrowIfRetired(); repository.SetChapterIndex(candidateId, index); return 0; }, operation?.CancellationToken ?? linked.Token).ConfigureAwait(false);
                else info = await io.RunAsync(() => repository.PageFetchInfo(candidateId, pageIndex), linked.Token).ConfigureAwait(false);
            }
            upstream = index >= 0
                ? await currentSource.OpenPageAsync(ReaderValue.Integer(info, "source_manga_id"), index, pageIndex, linked.Token).ConfigureAwait(false)
                : await currentSource.OpenBinaryAsync(ReaderValue.Text(info, "private_url"), linked.Token).ConfigureAwait(false);
            // Header acquisition can await while a split/detach changes membership.
            var current = await io.RunAsync(() => repository.PageFetchInfo(candidateId, pageIndex), linked.Token).ConfigureAwait(false);
            RequirePageSnapshot(info, current);
            operation?.ThrowIfRetired();
            var owned = upstream; ReaderImageStream? result = null;
            var transfer = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, owned.CancellationToken);
            result = new(token => owned.OpenBodyAsync(token), () => owned.DisposeAsync(), upstream.ContentType, upstream.ContentLength, transfer.Token,
                () => { lock (_gate) _images.Remove(result!); transfer.Dispose(); linked.Dispose(); lease.Dispose(); },
                async token => RequirePageSnapshot(info, await io.RunAsync(() => repository.PageFetchInfo(candidateId, pageIndex), token).ConfigureAwait(false)));
            published = result; upstream = null;
            lock (_gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                _images.Add(result);
            }
            return result;
        }
        catch (Exception primary)
        {
            var errors = new List<Exception> { primary };
            try { if (published is not null) await published.DisposeAsync().ConfigureAwait(false); else if (upstream is not null) await upstream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { errors.Add(cleanup); }
            finally
            {
                try { linked.Dispose(); } catch (Exception cleanup) { errors.Add(cleanup); }
                try { lease.Dispose(); } catch (Exception cleanup) { errors.Add(cleanup); }
            }
            if (errors.Count == 1) throw;
            throw new AggregateException("Discarded reader image cleanup failed", errors);
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            // Fence synchronously, but never enter an image's disposal gate while holding this gate.
            _stop ??= StopCoreAsync(_owner.StopAsync()); return _stop;
        }
    }
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); await _owner.DisposeAsync().ConfigureAwait(false); }

    private async Task StopCoreAsync(Task drain)
    {
        await Task.Yield();
        ReaderImageStream[] images; lock (_gate) images = _images.ToArray();
        var errors = new List<Exception>();
        try { foreach (var image in images) { try { await image.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); } } }
        finally { await drain.ConfigureAwait(false); }
        if (errors.Count > 0) throw new AggregateException("Reader image cleanup failed", errors);
    }

    private async Task<JsonObject> ResolveOwnedAsync(long chapterId, CancellationToken token)
    {
        using var lease = _owner.Enter();
        await using var operation = (source as IReaderScopedPageSource)?.OpenOperation(token);
        var currentSource = operation?.Source ?? source;
        var snapshot = await io.RunAsync(() => repository.PageSnapshot(chapterId), token).ConfigureAwait(false);
        Exception? lastError = null;
        foreach (var candidate in snapshot.Candidates)
        {
            var cached = snapshot.Cached.GetValueOrDefault(ReaderValue.Integer(candidate, "id"));
            if (cached is null || cached.Count == 0 || Age(ReaderValue.Text(cached[^1], "updated_at")) > pageCacheTtl) continue;
            try { await currentSource.CheckAttentionAsync(ReaderValue.Integer(candidate, "source_chapter_id"), token).ConfigureAwait(false); }
            catch (HumanAssistanceRequiredException error) { lastError = error; continue; }
            operation?.ThrowIfRetired();
            return await CachedAsync(chapterId, candidate, cached, false, null, operation?.CancellationToken ?? token).ConfigureAwait(false);
        }
        foreach (var candidate in snapshot.Candidates)
        {
            var key = (ReaderValue.Text(candidate, "source_id"), ReaderValue.Integer(candidate, "source_chapter_id"));
            lock (_gate)
            {
                if (_failures.TryGetValue(key, out var blocked))
                {
                    var remaining = (int)Math.Ceiling((blocked.Until - _clock.GetUtcNow()).TotalSeconds);
                    if (remaining > 0) { lastError = new ChapterPagesBlockedException(ReaderValue.Text(candidate, "source_name", "Source"), blocked.Detail, remaining, candidate); continue; }
                    _failures.Remove(key);
                }
            }
            IReadOnlyList<string> pages;
            try
            {
                pages = await currentSource.FetchPagesAsync(ReaderValue.Integer(candidate, "source_chapter_id"), token).ConfigureAwait(false);
                operation?.ThrowIfRetired();
                if (pages.Count == 0) throw new InvalidOperationException("Source returned no chapter pages");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                operation?.ThrowIfRetired();
                lastError = error; var delay = SourceFailures.ChapterRetryDelay(error);
                if (delay > 0)
                {
                    var status = SourceFailures.HttpStatus(error); var text = (status is 403 or 429 or 521 or 522 or 523 or 524 ? "HTTP " + status + ": " : "") + error.Message;
                    if (text.Length > 6000) text = text[..6000];
                    lock (_gate)
                    {
                        _failures.Remove(key); _failures.Add(key, (_clock.GetUtcNow().AddSeconds(delay), text));
                        while (_failures.Count > 256) _failures.Remove(_failures.Keys.First());
                    }
                    lastError = new ChapterPagesBlockedException(ReaderValue.Text(candidate, "source_name", "Source"), text, delay, candidate);
                }
                if (error is SourceWorkDeferredException and not HumanAssistanceRequiredException || ConnectionFailed(error)) break;
                continue;
            }
            lock (_gate) _failures.Remove(key);
            var chapterIndex = checked((int)ReaderValue.Integer(candidate, "source_chapter_index", -1));
            if (chapterIndex < 0)
            {
                try { chapterIndex = await currentSource.ChapterIndexAsync(ReaderValue.Integer(candidate, "source_manga_id"), ReaderValue.Integer(candidate, "source_chapter_id"), token).ConfigureAwait(false); }
                catch (SourceWorkDeferredException) { /* Local deferral retains the unresolved mapping. */ }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { chapterIndex = -2; }
            }
            operation?.ThrowIfRetired();
            await io.RunAsync(() => { operation?.ThrowIfRetired(); repository.PersistPages(chapterId, candidate, pages, chapterIndex); return 0; }, operation?.CancellationToken ?? token).ConfigureAwait(false);
            operation?.ThrowIfRetired();
            return new() { ["candidate"] = candidate.DeepClone(), ["pages"] = new JsonArray(Enumerable.Range(0, pages.Count).Select(index => JsonValue.Create(PageUrl(ReaderValue.Integer(candidate, "id"), index))).ToArray()), ["page_cache_hit"] = false, ["page_cache_stale"] = false };
        }
        foreach (var candidate in snapshot.Candidates)
        {
            var cached = snapshot.Cached.GetValueOrDefault(ReaderValue.Integer(candidate, "id"));
            if (cached is null || cached.Count == 0) continue;
            try { await currentSource.CheckAttentionAsync(ReaderValue.Integer(candidate, "source_chapter_id"), token).ConfigureAwait(false); }
            catch (HumanAssistanceRequiredException error) { lastError = error; continue; }
            operation?.ThrowIfRetired();
            return await CachedAsync(chapterId, candidate, cached, true, lastError, operation?.CancellationToken ?? token).ConfigureAwait(false);
        }
        if (lastError is SourceWorkDeferredException or ChapterPagesBlockedException) throw lastError;
        throw new InvalidOperationException("Private source host could not resolve chapter pages" + (lastError is null ? "" : ": " + lastError.Message), lastError);
    }

    private async Task<JsonObject> CachedAsync(long chapterId, JsonObject candidate, IReadOnlyList<JsonObject> cached, bool stale, Exception? warning, CancellationToken token)
    {
        await io.RunAsync(() => { repository.RecordServed(chapterId, candidate); return 0; }, token).ConfigureAwait(false);
        var result = new JsonObject { ["candidate"] = candidate.DeepClone(), ["pages"] = new JsonArray(cached.Select(row => JsonValue.Create(PageUrl(ReaderValue.Integer(candidate, "id"), checked((int)ReaderValue.Integer(row, "page_index"))))).ToArray()), ["page_cache_hit"] = true, ["page_cache_stale"] = stale };
        if (stale)
        {
            result["source_warning"] = "Fresh source-host page lookup failed: " + (warning?.Message ?? "unknown source error");
            result["source_warning_context"] = warning is HumanAssistanceRequiredException assistance ? assistance.SourceContext.DeepClone() : warning is ChapterPagesBlockedException blocked ? blocked.SourceContext.DeepClone() : new JsonObject();
            result["source_warning_deferred"] = warning is SourceWorkDeferredException or WorkDeferredException;
        }
        return result;
    }
    private TimeSpan Age(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ? TimeSpan.FromSeconds(Math.Max(0, (_clock.GetUtcNow() - timestamp).TotalSeconds)) : TimeSpan.MaxValue;
    private static bool ConnectionFailed(Exception error) => error is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } || error.InnerException is SocketException;
    private static string PageUrl(long candidateId, int index) => FormattableString.Invariant($"/api/v1/pages/{candidateId}/{index}");
    private static void RequirePageSnapshot(JsonObject expected, JsonObject current)
    {
        if (ReaderValue.Integer(current, "source_manga_id") != ReaderValue.Integer(expected, "source_manga_id") || ReaderValue.Integer(current, "source_chapter_id") != ReaderValue.Integer(expected, "source_chapter_id")
            || ReaderValue.Text(current, "private_url") != ReaderValue.Text(expected, "private_url")) throw new KeyNotFoundException("Page provenance changed during image lookup");
    }
    private sealed class Lookup(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<JsonObject> Work { get; set; } = null!;
        public int Waiters;
    }
}
