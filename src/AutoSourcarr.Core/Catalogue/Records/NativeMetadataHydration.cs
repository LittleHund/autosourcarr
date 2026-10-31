using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Chapters;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources;
using static AutoSourcarr.Core.Catalogue.Structure.StructureSql;
using StructuralCatalogue = AutoSourcarr.Core.Catalogue.Structure.CatalogueStructure;

namespace AutoSourcarr.Core.Catalogue.Records;

internal sealed record NativeHydrationResult(string State, long? EntryId = null, string FailureTypes = "");
internal sealed record NativeCoverHint(string SourceId, string ThumbnailUrl, string BaseUrl);

/// <summary>One instance's actual existing-record detail hydration and source cover
/// hints. Does not attach unknown records, borrow manual authority or infer health.</summary>
internal sealed class NativeMetadataHydration : IAsyncDisposable
{
    internal const int MaximumPendingJobs = 1024;
    private sealed class Job(NativeSourceRecord key, bool eager, IDisposable lease)
    {
        internal NativeSourceRecord Key { get; } = key;
        internal int Eager = eager ? 1 : 0;
        internal string Stage = "waiting-idle";
        internal IDisposable Lease { get; } = lease;
        internal TaskCompletionSource<NativeHydrationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly SqliteDatabase _database;
    private readonly object _sourceGate, _jobsGate = new(), _hintsGate = new();
    private readonly SuwayomiClient _source;
    private readonly BoundedWorkScheduler _io;
    private readonly WholeTaskCoordinator _lanes;
    private readonly CatalogueMetadata _metadata;
    private readonly StructuralCatalogue _structure;
    private readonly NativeChapterWriter _chapters;
    private readonly GalleryEvidenceService _gallery;
    private readonly NativeRecordStore _records;
    private readonly Func<AutoSourcarrSettings> _settings;
    private readonly SupportLogStore _logs;
    private readonly OperationOwner _owner = new();
    private readonly SemaphoreSlim _metadataSlots;
    private readonly Dictionary<NativeSourceRecord, Job> _jobs = [];
    private readonly Dictionary<long, LinkedListNode<(long Id, NativeCoverHint Hint)>> _hints = [];
    private readonly LinkedList<(long Id, NativeCoverHint Hint)> _hintOrder = [];
    private bool _stopping;
    private Task? _close;

    internal NativeMetadataHydration(SqliteDatabase database, object sourceGate, SuwayomiClient source,
        BoundedWorkScheduler io, WholeTaskCoordinator lanes, CatalogueMetadata metadata, StructuralCatalogue structure,
        NativeChapterWriter chapters, Func<AutoSourcarrSettings> settings, SupportLogStore logs)
    {
        _database = database; _sourceGate = sourceGate; _source = source; _io = io; _lanes = lanes;
        _metadata = metadata; _structure = structure; _chapters = chapters; _settings = settings; _logs = logs;
        if (!chapters.Owns(database, sourceGate)) throw new ArgumentException("Hydration requires its actual native chapter writer.", nameof(chapters));
        _gallery = new(database, sourceGate); _records = new(database, sourceGate);
        var capacity = Math.Max(1, settings().MetadataEnrichmentConcurrency); _metadataSlots = new(capacity, capacity);
    }
    internal int Pending { get { lock (_jobsGate) return _jobs.Count; } }
    internal string? StageOf(string sourceId, long mangaId)
    { lock (_jobsGate) return _jobs.TryGetValue(new(sourceId, mangaId), out var job) ? Volatile.Read(ref job.Stage) : null; }
    internal bool Schedule(string sourceId, long mangaId, bool eager = false) => TrySchedule(sourceId, mangaId, out _, eager);
    internal bool TrySchedule(string sourceId, long mangaId, out Task<NativeHydrationResult>? completion, bool eager = false)
    {
        completion = null;
        if (string.IsNullOrEmpty(sourceId) || mangaId <= 0) return false;
        Job job;
        lock (_jobsGate)
        {
            if (_stopping || _lanes.Stopping) return false;
            var key = new NativeSourceRecord(sourceId, mangaId);
            if (_jobs.TryGetValue(key, out var existing))
            {
                completion = existing.Completion.Task;
                if (eager) Volatile.Write(ref existing.Eager, 1);
                return eager;
            }
            if (_jobs.Count >= MaximumPendingJobs) return false; // Local admission deferral, never source failure.
            job = new(key, eager, _owner.Enter()); _jobs.Add(key, job); completion = job.Completion.Task;
        }
        _ = RunAsync(job); return true;
    }
    private async Task RunAsync(Job job)
    {
        await Task.Yield(); NativeHydrationResult result;
        try
        {
            await _lanes.WaitForIdleAsync(_owner.CancellationToken, promoted: () => Volatile.Read(ref job.Eager) != 0).ConfigureAwait(false);
            Volatile.Write(ref job.Stage, "waiting-source");
            result = await _lanes.RunBackgroundSourceAsync(token => FetchAndPersistAsync(job, token), _owner.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { result = new("cancelled"); }
        catch (Exception error) when (error is SourceWorkDeferredException or WorkDeferredException) { result = new("deferred", FailureTypes: FaultTypes(error)); }
        catch (TimeoutException error) { result = new("timeout", FailureTypes: FaultTypes(error)); }
        catch (Exception error)
        {
            var types = FaultTypes(error);
            try { _logs.Record("Metadata", "Native source detail hydration failed (" + types + ")."); }
            catch (Exception logging) { types += ";log=" + logging.GetType().Name; }
            result = new("failed", FailureTypes: types);
        }
        lock (_jobsGate)
        {
            if (_jobs.TryGetValue(job.Key, out var current) && ReferenceEquals(current, job)) _jobs.Remove(job.Key);
            // The caller's outcome is published before releasing the physical owner.
            job.Completion.TrySetResult(result); job.Lease.Dispose();
        }
    }
    private async Task<NativeHydrationResult> FetchAndPersistAsync(Job job, CancellationToken token)
    {
        var key = job.Key; Volatile.Write(ref job.Stage, "waiting-metadata");
        await _metadataSlots.WaitAsync(token).ConfigureAwait(false);
        SourceTransportSession? session = null; Exception? failure = null; NativeHydrationResult? result = null;
        try
        {
            Volatile.Write(ref job.Stage, "fetching");
            session = _source.OpenSession(token); JsonObject payload;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, session.CancellationToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings().MetadataEnrichmentTimeout)));
                try
                {
                    payload = await session.FetchMangaAndChaptersAsync(key.MangaId, true, false, deadline.Token).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested(); session.ThrowIfRetired();
                }
                catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !token.IsCancellationRequested && !session.CancellationToken.IsCancellationRequested)
                { throw new TimeoutException("Native source metadata deadline expired.", error); }
            }
            if (payload["manga"] is not JsonObject manga || !SourceResponseContract.TryLong(manga["id"], out var id) || id != key.MangaId
                || SourceResponseContract.Identity(manga["sourceId"]) != key.SourceId)
                throw new InvalidDataException("Native metadata response does not name the exact requested source record.");
            // The logical source session remains owned through physical database
            // work. Check retirement inside the real writer gate before admission;
            // once admitted, preserve committed work and its actual failure outcome.
            Volatile.Write(ref job.Stage, "persisting");
            result = await _io.RunAsync(() => Persist(key, manga, session), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        try { if (session is not null) await session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException("Metadata work and source-session cleanup failed.", failure, cleanup); }
        finally { _metadataSlots.Release(); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }
    private NativeHydrationResult Persist(NativeSourceRecord key, JsonObject manga, SourceTransportSession session)
    {
        lock (_sourceGate)
        {
            session.ThrowIfRetired();
            var initial = _records.Capture(key.SourceId, key.MangaId);
            if (initial is null) return new("detached");
            var entryId = initial.CanonicalEntryId; var cleaned = _gallery.CleanNative(key.SourceId, manga);
            var prefix = _records.RefreshIfCurrent(entryId, key.SourceId, key.MangaId, cleaned);
            if (!prefix.Accepted) return new("detached");
            NativeMediaSplit? split = null;
            var current = _records.Capture(key.SourceId, key.MangaId);
            if (current is null || current.CanonicalEntryId != entryId) throw new InvalidOperationException("Native record ownership changed during cached metadata publication.");
            if (prefix.CachedStored)
            {
                var mode = _database.Write(DatabaseFile.Catalog, c =>
                {
                    _metadata.SyncPreferredTitle(c, entryId, Stamp()); _metadata.RecomputeColor(c, entryId);
                    var value = _metadata.RecomputeMedia(c, entryId);
                    if (value != "mixed") _metadata.SyncRelatedMediaTitles(c, entryId, Stamp());
                    return value;
                });
                if (mode == "mixed") split = _structure.SeparateMixedMedia(entryId, _metadata);
            }
            current = _records.Capture(key.SourceId, key.MangaId);
            if (current is null) throw new InvalidOperationException("Native record detached during derived metadata publication.");
            if (current.CanonicalEntryId != entryId && !(split is not null && split.NewEntryId == current.CanonicalEntryId && split.Records.Contains(key)))
                throw new InvalidOperationException("An unrelated native owner cannot inherit delayed authoritative detail.");
            // A real media split may have moved this exact pair. Follow only that
            // verified local result, rather than applying detail to its former role.
            _database.Write(DatabaseFile.Catalog, c => _records.WriteAuthoritative(c, current, cleaned));
            var detailOwner = current.CanonicalEntryId;
            if (cleaned["_gallery_metadata"]?["animated"]?.GetValue<bool>() == true && HasStillAnchor(detailOwner))
                _structure.ReconcileGallery(detailOwner, _chapters);
            current = _records.Capture(key.SourceId, key.MangaId);
            if (current is null) throw new InvalidOperationException("Native record detached during gallery reconciliation.");
            RememberHint(key.SourceId, key.MangaId, NativeRecordStore.SourceText(cleaned["thumbnailUrl"]));
            _database.Write(DatabaseFile.Catalog, c =>
            {
                _metadata.ChooseMetadata(c, entryId, Stamp());
                if (detailOwner != entryId) _metadata.ChooseMetadata(c, detailOwner, Stamp());
                if (current.CanonicalEntryId != entryId && current.CanonicalEntryId != detailOwner) _metadata.ChooseMetadata(c, current.CanonicalEntryId, Stamp());
                return 0;
            });
            return new("applied", current.CanonicalEntryId);
        }
    }
    private bool HasStillAnchor(long entryId) => _database.Read(DatabaseFile.Catalog, c =>
    {
        foreach (var row in c.Query("SELECT metadata_json FROM source_matches WHERE entry_id=$p0 AND active=1 UNION ALL SELECT metadata_json FROM source_variants WHERE entry_id=$p0 AND active=1", P(entryId)))
        {
            JsonObject? native; try { native = JsonNode.Parse(row.Text("metadata_json", "{}")) as JsonObject; } catch (JsonException) { continue; }
            if (native is null) continue;
            var identity = native["_gallery_metadata"] as JsonObject ?? GalleryMetadata.Enrich(native, new JsonObject { ["metadata_profile"] = "gallery" })["_gallery_metadata"] as JsonObject;
            if (identity?["animated"]?.GetValue<bool>() != true) return true;
        }
        return false;
    });
    internal NativeCoverHint? SourceCoverHint(long mangaId)
    {
        using var lease = _owner.Enter();
        lock (_hintsGate)
            if (_hints.TryGetValue(mangaId, out var node)) { _hintOrder.Remove(node); _hintOrder.AddLast(node); return node.Value.Hint; }
        lock (_sourceGate)
        {
            _owner.CancellationToken.ThrowIfCancellationRequested();
            var row = _database.Read(DatabaseFile.Catalog, c =>
            {
                foreach (var table in new[] { "source_matches", "source_variants" })
                {
                    var value = c.Query($"SELECT r.source_id,r.thumbnail_url,COALESCE(sc.base_url,'') base_url FROM {table} r LEFT JOIN source_config sc ON sc.source_id=r.source_id WHERE r.source_manga_id=$p0 AND r.active=1 ORDER BY COALESCE(sc.priority,1000) LIMIT 1", P(mangaId)).FirstOrDefault();
                    if (value is not null) return value;
                }
                return null;
            });
            if (row is null) return null;
            var hint = new NativeCoverHint(row.Text("source_id"), row.Text("thumbnail_url"), row.Text("base_url")); StoreHint(mangaId, hint); return hint;
        }
    }
    private void RememberHint(string sourceId, long mangaId, string thumbnail)
    {
        var root = _database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT base_url FROM source_config WHERE source_id=$p0", P(sourceId)) as string ?? "");
        StoreHint(mangaId, new(sourceId, thumbnail, root));
    }
    private void StoreHint(long mangaId, NativeCoverHint hint)
    {
        lock (_hintsGate)
        {
            if (_hints.Remove(mangaId, out var old)) _hintOrder.Remove(old);
            _hints[mangaId] = _hintOrder.AddLast((mangaId, hint));
            while (_hints.Count > Math.Max(128, _settings().CoverHintCacheMaxEntries)) { var first = _hintOrder.First!; _hints.Remove(first.Value.Id); _hintOrder.RemoveFirst(); }
        }
    }
    private static string Stamp() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    private static string FaultTypes(Exception error)
    {
        var types = new List<string>(); var pending = new Stack<Exception>(); pending.Push(error);
        while (pending.TryPop(out var current) && types.Count < 8)
        {
            types.Add(current.GetType().Name);
            if (current is AggregateException multiple) foreach (var inner in multiple.InnerExceptions.Reverse()) pending.Push(inner);
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        return string.Join("/", types);
    }
    public ValueTask DisposeAsync()
    {
        lock (_jobsGate) { _stopping = true; return new(_close ??= CloseAsync()); }
    }
    private async Task CloseAsync()
    {
        await Task.Yield();
        try { await _owner.DisposeAsync().ConfigureAwait(false); }
        finally { _metadataSlots.Dispose(); lock (_hintsGate) { _hints.Clear(); _hintOrder.Clear(); } }
    }
}
