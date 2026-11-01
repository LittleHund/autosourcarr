using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Catalogue.Metadata.Hydration;

/// <summary>What background metadata enrichment uses from other areas.</summary>
public sealed class MetadataEnrichmentHooks
{
    /// <summary>schedule_entry_identity (MetadataIdentityResolver.Schedule).</summary>
    public required Func<long, bool> ScheduleIdentity { get; init; }
    /// <summary>enrich_entry_metadata (CatalogueDiscovery.EnrichEntryAsync): answers whether source detail was applied.</summary>
    public required Func<long, CancellationToken, Task<bool>> Enrich { get; init; }
    /// <summary>Background failures (logged by the host).</summary>
    public Action<long, Exception>? Failed { get; init; }
}

/// <summary>
/// Port-map "Catalogue/Metadata → MetadataHydration" (the parts phase 9 did not port): _plan_metadata_enrichment,
/// _queue_feed_metadata_enrichment / schedule_metadata_enrichment and _manga_metadata_is_sparse. A feed or import that
/// shows entries plans, off the request, which of them still need a provider identity (no identity link, at most
/// METADATA_IDENTITY_ENTRIES_PER_FEED) and which are sparse (at most METADATA_ENRICHMENT_ENTRIES_PER_FEED); identities are
/// scheduled on the resolver, sparse entries are enriched in the background (METADATA_ENRICHMENT_CONCURRENCY at a time,
/// one run per entry, at most 512 waiting). Nothing runs after <see cref="DisposeAsync"/>.
/// </summary>
public sealed class MetadataEnrichment : IAsyncDisposable
{
    private const int MaximumPending = 512;
    private readonly SqliteDatabase _database;
    private readonly Func<AutoSourcarrSettings> _settings;
    private readonly MetadataEnrichmentHooks _hooks;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Task> _running = [];

    public MetadataEnrichment(SqliteDatabase database, Func<AutoSourcarrSettings> settings, MetadataEnrichmentHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(hooks);
        _database = database; _settings = settings; _hooks = hooks;
        var slots = Math.Clamp(settings().MetadataEnrichmentConcurrency, 1, 8);
        _slots = new SemaphoreSlim(slots, slots);
    }

    /// <summary>_manga_metadata_is_sparse: a source payload without detail (cover, 40+ character summary, creators, genres, status).</summary>
    public static bool IsSparse(JsonObject manga)
    {
        ArgumentNullException.ThrowIfNull(manga);
        static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";
        var genres = manga["genre"] ?? manga["genres"];
        var hasGenres = genres is JsonArray { Count: > 0 } || Text(genres).Length > 0;
        var initialized = manga["initialized"] is JsonValue flag && flag.TryGetValue<bool>(out var known) && known;
        var status = AutoSourcarr.Core.Imaging.ThumbnailFingerprints.JsonNumber(manga["status"]);
        if (status == 0) status = MetadataFields.ProviderStatus(Text(manga["status"]));
        return !initialized || Text(manga["thumbnailUrl"]).Length == 0 || Text(manga["description"]).Length < 40 || Text(manga["author"]).Length == 0
            || Text(manga["artist"]).Length == 0 || !hasGenres || status == 0;
    }

    /// <summary>_plan_metadata_enrichment: (entries needing an identity, sparse entries) among the given ones, canonical and in order.</summary>
    public (IReadOnlyList<long> Identities, IReadOnlyList<long> Sparse) Plan(IEnumerable<long> entryIds, int? maxEntries = null)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var settings = _settings();
        return _database.Read(DatabaseFile.Catalog, s =>
        {
            var ids = entryIds.Select(id => CatalogueTitles.Canonical(s, id)).Distinct()
                .Where(id => s.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", id)) is not null).ToArray();
            IReadOnlyList<long> identities = settings.MetadataIdentityEnabled
                ? ids.Take(Math.Max(0, settings.MetadataIdentityEntriesPerFeed))
                    .Where(id => s.Scalar("SELECT 1 FROM identity_links WHERE entry_id=$id LIMIT 1", ("$id", id)) is null).ToArray()
                : [];
            IReadOnlyList<long> sparse = ids.Take(Math.Max(0, maxEntries ?? settings.MetadataEnrichmentEntriesPerFeed)).Where(id => NeedsMetadata(s, id)).ToArray();
            return (identities, sparse);
        });
    }

    /// <summary>_entry_needs_metadata over the stored entry.</summary>
    internal static bool NeedsMetadata(DatabaseSession s, long id)
    {
        var row = s.Query("SELECT thumbnail_url,description,author,artist,genre,status FROM entries WHERE id=$id", ("$id", id)).SingleOrDefault();
        return row is not null && (row.Text("thumbnail_url").Trim().Length == 0 || row.Text("description").Trim().Length < 40 || row.Text("author").Trim().Length == 0
            || row.Text("artist").Trim().Length == 0 || row.Text("genre").Trim().Length == 0 || row.Integer("status") == 0);
    }

    /// <summary>
    /// _queue_feed_metadata_enrichment: plans and schedules in the background so a feed answer never waits on SQLite or a
    /// provider. Answers the number of sparse entries newly scheduled (0 when stopping).
    /// </summary>
    public Task<int> QueueFeedAsync(IReadOnlyList<long> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        if (_stopping.IsCancellationRequested || entryIds.Count == 0) return Task.FromResult(0);
        var ids = entryIds.ToArray();
        return Task.Run(() => Schedule(ids), CancellationToken.None);
    }

    /// <summary>schedule_metadata_enrichment: identity first, then background enrichment of each sparse entry not already running.</summary>
    public int Schedule(IReadOnlyList<long> entryIds, int? maxEntries = null)
    {
        if (_stopping.IsCancellationRequested) return 0;
        var (identities, sparse) = Plan(entryIds, maxEntries);
        foreach (var id in identities) _hooks.ScheduleIdentity(id);
        var queued = 0;
        lock (_gate)
        {
            foreach (var id in sparse)
            {
                if (_running.ContainsKey(id) || _running.Count >= MaximumPending) continue;
                _running[id] = RunAsync(id);
                queued++;
            }
        }
        return queued;
    }

    /// <summary>Entries whose enrichment is queued or running.</summary>
    public int Pending { get { lock (_gate) return _running.Count; } }

    private async Task RunAsync(long id)
    {
        await Task.Yield();
        var token = _stopping.Token;
        try
        {
            await _slots.WaitAsync(token).ConfigureAwait(false);
            try { await _hooks.Enrich(id, token).ConfigureAwait(false); }
            finally { _slots.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { _hooks.Failed?.Invoke(id, error); }
        finally { lock (_gate) _running.Remove(id); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        Task[] running;
        lock (_gate) running = _running.Values.ToArray();
        try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { } // a provider call past its own deadline; it stops at its next cancellation check
    }
}
