using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Metadata;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Sources.Discovery;

/// <summary>The source reads discovery needs (SuwayomiClient through the source host, which paces every request per host).</summary>
public interface ISourceCatalogueClient
{
    /// <summary>fetch_source_manga: {mangas: [...], hasNextPage} of one POPULAR / LATEST / SEARCH page.</summary>
    Task<JsonObject> BrowseAsync(string sourceId, string mode, int page, string query, CancellationToken cancellationToken);
    /// <summary>fetch_manga_and_chapters: {manga, chapters}.</summary>
    Task<JsonObject> MangaAsync(long mangaId, bool details, bool chapters, CancellationToken cancellationToken);
}

/// <summary>The live adapter.</summary>
public sealed class SuwayomiCatalogueClient(SuwayomiClient client) : ISourceCatalogueClient
{
    public Task<JsonObject> BrowseAsync(string sourceId, string mode, int page, string query, CancellationToken cancellationToken) =>
        client.FetchSourceMangaAsync(sourceId, mode, page, query, cancellationToken);
    public Task<JsonObject> MangaAsync(long mangaId, bool details, bool chapters, CancellationToken cancellationToken) =>
        client.FetchMangaAndChaptersAsync(mangaId, details, chapters, cancellationToken);
}

/// <summary>Progress of a discovery job (current, total, message); null leaves a value unchanged.</summary>
public delegate void DiscoveryProgress(int? current, int? total, string? message);

/// <summary>
/// The catalogue-wide jobs moved from MaintenanceEndpoints (phase 9 "Sources/Discovery"): the feed crawl (POPULAR / LATEST
/// pages of every enabled source into entries through EntryIngest and into browse_cache), metadata enrichment of sparse
/// entries (provider identity, then source detail of the best records), the additional-record scan (missing sources
/// and further records on matched sources) and the Subbed search-all / repair-unmatched. Every source request goes through
/// the source host (paced per host) with its own deadline; work is sequential per entry, checks cancellation between
/// requests, and commits each found record at once, so a cancelled or timed-out run keeps what it found. Discovery
/// outcomes are saved per entry and source (source_discovery_outcomes), and an exhaustive retry visits untouched sources
/// first (resumable like 0.4.5). Every attachment re-checks ownership, manual separation, rejection and the full strict
/// evidence under the catalogue gate (EntryIngest.AttachAutomaticRecordIfCurrent); nothing here merges entries.
/// </summary>
public sealed class CatalogueDiscovery
{
    private const double PromisingTitle = 0.74;
    private const int InspectedPerSource = 3;
    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly EntryIngest _ingest;
    private readonly SourceDiscovery _discovery;
    private readonly ISourceCatalogueClient _client;
    private readonly Func<AutoSourcarrSettings> _settings;
    private readonly TimeProvider _clock;

    public CatalogueDiscovery(SqliteDatabase database, object sourceWriteGate, EntryIngest ingest, ISourceCatalogueClient client,
        Func<AutoSourcarrSettings> settings, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate); ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(settings);
        _database = database; _gate = sourceWriteGate; _ingest = ingest; _discovery = new SourceDiscovery(ingest); _client = client;
        _settings = settings; _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Provider identity resolution before metadata enrichment and source discovery (null: none).</summary>
    public MetadataIdentityResolver? Identity { get; init; }
    /// <summary>_choose_metadata after source detail was applied (CatalogueMetadata.ChooseMetadata in its own write).</summary>
    public Action<long>? ChooseMetadata { get; init; }
    /// <summary>After a record was attached: load its chapters (the chapter area's refresh).</summary>
    public Action<long, string, long>? RecordAttached { get; init; }
    /// <summary>_record_source_result: a source answered (null) or failed (the error), for source health.</summary>
    public Action<string, Exception?>? SourceResult { get; init; }
    /// <summary>
    /// MAT-20: machine-translated extra search names for the additional-record search (names, genre, source language);
    /// they only widen the search, every candidate is still proved against the entry's trusted evidence. Null: none.
    /// </summary>
    public Func<IReadOnlyList<SearchName>, string, string, CancellationToken, Task<IReadOnlyList<SearchName>>>? TranslateNames { get; init; }

    // ---- feed crawl --------------------------------------------------------------------------------------------------

    /// <summary>The feeds a crawl visits: POPULAR and LATEST × BROWSE_PREFETCH_PAGES.</summary>
    public int PlannedFeeds => 2 * Math.Max(1, _settings().BrowsePrefetchPages);

    /// <summary>
    /// schedule_feed_refresh → federated(force_live): every enabled source's POPULAR and LATEST pages 1..BROWSE_PREFETCH_PAGES
    /// ingested (FEDERATED_ITEMS_PER_SOURCE per source page) and saved as the persistent default browse feeds. A failed
    /// revalidation keeps the last good feed.
    /// </summary>
    public async Task<JsonObject> RefreshFeedsAsync(DiscoveryProgress? progress, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var pages = Math.Max(1, settings.BrowsePrefetchPages);
        var sources = Read(session => session.Query("""
            SELECT source_id,COALESCE(NULLIF(display_name,''),name) name,priority,supports_latest FROM source_config
             WHERE available=1 AND enabled_language=1 AND enabled_search=1 ORDER BY priority,name,lang
            """).Select(row => (Id: row.Text("source_id"), Name: row.Text("name"), Priority: row.Integer("priority", 1000), Latest: row.Flag("supports_latest"))).ToArray());
        var feeds = 0; var cataloged = new HashSet<long>(); var failures = 0;
        var total = pages * 2 * Math.Max(1, sources.Length); var step = 0;
        foreach (var mode in new[] { "POPULAR", "LATEST" })
            for (var page = 1; page <= pages; page++)
            {
                var scores = new Dictionary<long, double>(); var errors = new JsonArray(); var next = false;
                foreach (var source in sources.Where(source => mode == "POPULAR" || source.Latest))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Invoke(step++, total, mode + " page " + page.ToString(CultureInfo.InvariantCulture) + " · " + source.Name);
                    JsonObject payload;
                    try
                    {
                        using var metric = Diagnostics.RuntimeMetrics.Shared.Phase("crawl");
                        payload = await Deadline(token => _client.BrowseAsync(source.Id, mode, page, "", token), settings.FederatedSourceTimeout, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        failures++; SourceResult?.Invoke(source.Id, error);
                        errors.Add(new JsonObject { ["source_id"] = source.Id, ["source"] = source.Name, ["error"] = Message(error) });
                        continue;
                    }
                    SourceResult?.Invoke(source.Id, null);
                    next |= payload["hasNextPage"] is JsonValue more && more.TryGetValue<bool>(out var flag) && flag;
                    var rank = 0;
                    foreach (var manga in (payload["mangas"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(Math.Max(1, settings.FederatedItemsPerSource)))
                    {
                        IngestResult result;
                        try { result = _ingest.IngestManga(source.Id, (JsonObject)manga.DeepClone()); }
                        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
                        {
                            errors.Add(new JsonObject { ["source_id"] = source.Id, ["source"] = source.Name, ["error"] = "ingest: " + error.Message });
                            rank++; continue;
                        }
                        if (result.EntryId is { } entryId)
                        {
                            scores[entryId] = scores.GetValueOrDefault(entryId) + Math.Max(0, 1000.0 - rank * 10 - source.Priority * 0.05);
                            cataloged.Add(entryId);
                        }
                        rank++;
                    }
                }
                SaveFeed(mode, page, scores, next, errors, settings.BrowsePageSize);
                feeds++;
            }
        progress?.Invoke(total, total, "Refreshed " + feeds.ToString(CultureInfo.InvariantCulture) + " feeds · " + cataloged.Count.ToString(CultureInfo.InvariantCulture) + " entries");
        return new JsonObject { ["feeds"] = feeds, ["sources"] = sources.Length, ["cataloged_entries"] = cataloged.Count, ["source_failures"] = failures };
    }

    /// <summary>_browse_cache_key of a default feed (json.dumps(sort_keys=True, separators=(",", ":"))).</summary>
    public static string FeedCacheKey(string mode, int page, int pageSize) =>
        "{\"exact_match_count\":null,\"min_matches\":1,\"mode\":\"" + mode.ToUpperInvariant() + "\",\"page\":" + page.ToString(CultureInfo.InvariantCulture)
        + ",\"page_size\":" + Math.Clamp(pageSize, 1, 500).ToString(CultureInfo.InvariantCulture) + ",\"query\":\"\",\"sort\":\"relevance\",\"source_ids\":[]}";

    private void SaveFeed(string mode, int page, IReadOnlyDictionary<long, double> scores, bool next, JsonArray errors, int pageSize)
    {
        var key = FeedCacheKey(mode, page, pageSize);
        var stamp = Stamp();
        Write(session =>
        {
            // Rank like federated(): feed position plus 300 per attached source; canonical ids, each once.
            var ranked = new Dictionary<long, (double Score, string Title)>();
            foreach (var (entry, score) in scores)
            {
                var id = CatalogueTitles.Canonical(session, entry);
                var row = session.Query("SELECT preferred_title,(SELECT COUNT(DISTINCT source_id) FROM source_matches WHERE entry_id=$id AND active=1) matches FROM entries WHERE id=$id",
                    ("$id", id)).SingleOrDefault();
                if (row is null || row.Integer("matches") < 1) continue;
                var total = score + row.Integer("matches") * 300.0 + (ranked.TryGetValue(id, out var known) ? known.Score : 0);
                ranked[id] = (total, row.Text("preferred_title"));
            }
            var ids = ranked.OrderByDescending(pair => pair.Value.Score).ThenBy(pair => UnicodeCaseFold.Fold(pair.Value.Title), StringComparer.Ordinal)
                .Take(Math.Clamp(pageSize, 1, 500)).Select(pair => pair.Key).ToArray();
            if (ids.Length == 0 && errors.Count != 0
                && session.Scalar("SELECT entry_ids_json FROM browse_cache WHERE cache_key=$key", ("$key", key)) is string saved && saved.Trim() is not ("[]" or ""))
            {
                // A failed revalidation is not an empty feed: keep the last good ids and show the failure.
                session.Execute("UPDATE browse_cache SET source_errors_json=$errors WHERE cache_key=$key", ("$errors", errors.ToJsonString()), ("$key", key));
                return 0;
            }
            session.Execute("""
                INSERT INTO browse_cache(cache_key,mode,page,query,source_ids_json,min_matches,exact_match_count,sort,entry_ids_json,has_next_page,source_errors_json,updated_at,last_accessed)
                VALUES($key,$mode,$page,'','[]',1,NULL,'relevance',$ids,$next,$errors,$stamp,$stamp)
                ON CONFLICT(cache_key) DO UPDATE SET mode=excluded.mode,page=excluded.page,query=excluded.query,source_ids_json=excluded.source_ids_json,
                  min_matches=excluded.min_matches,exact_match_count=excluded.exact_match_count,sort=excluded.sort,entry_ids_json=excluded.entry_ids_json,
                  has_next_page=excluded.has_next_page,source_errors_json=excluded.source_errors_json,updated_at=excluded.updated_at,last_accessed=excluded.last_accessed
                """, ("$key", key), ("$mode", mode), ("$page", page), ("$ids", "[" + string.Join(", ", ids.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]"),
                ("$next", next ? 1 : 0), ("$errors", errors.ToJsonString()), ("$stamp", stamp));
            return 0;
        });
    }

    // ---- metadata enrichment -----------------------------------------------------------------------------------------

    /// <summary>queue_sparse_metadata_enrichment: entries missing a cover, a 40+ character summary, creators, genres or status, oldest first.</summary>
    public IReadOnlyList<long> SparseEntries(int limit) => Read(session => session.Query("""
        SELECT id FROM entries
         WHERE length(trim(COALESCE(thumbnail_url,'')))=0 OR length(trim(COALESCE(description,'')))<40 OR length(trim(COALESCE(author,'')))=0
            OR length(trim(COALESCE(artist,'')))=0 OR length(trim(COALESCE(genre,'')))=0 OR COALESCE(status,0)=0
         ORDER BY updated_at,id LIMIT $limit
        """, ("$limit", Math.Clamp(limit, 1, 5000))).Select(row => row.Integer("id")).ToArray());

    /// <summary>_entry_needs_metadata.</summary>
    public bool NeedsMetadata(long entryId) => Read(session =>
    {
        var row = session.Query("SELECT thumbnail_url,description,author,artist,genre,status FROM entries WHERE id=$id", ("$id", CatalogueTitles.Canonical(session, entryId))).SingleOrDefault();
        return row is not null && (row.Text("thumbnail_url").Trim().Length == 0 || row.Text("description").Trim().Length < 40 || row.Text("author").Trim().Length == 0
            || row.Text("artist").Trim().Length == 0 || row.Text("genre").Trim().Length == 0 || row.Integer("status") == 0);
    });

    /// <summary>
    /// schedule_metadata_enrichment over the given entries, METADATA_ENRICHMENT_CONCURRENCY at a time: provider identity
    /// first, then (while still sparse) the detail of up to METADATA_ENRICHMENT_SOURCE_LIMIT attached records.
    /// </summary>
    public async Task<JsonObject> EnrichMetadataAsync(IReadOnlyList<long> entryIds, DiscoveryProgress? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var settings = _settings();
        var completed = 0; var failed = 0; var improved = 0; var firstError = "";
        progress?.Invoke(0, entryIds.Count, "Repairing sparse entries");
        await Parallel.ForEachAsync(entryIds.Distinct().ToArray(), new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(settings.MetadataEnrichmentConcurrency, 1, 8), CancellationToken = cancellationToken,
        }, async (entryId, token) =>
        {
            try
            {
                if (await EnrichEntryAsync(entryId, token).ConfigureAwait(false)) Interlocked.Increment(ref improved);
                Interlocked.Increment(ref completed);
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                Interlocked.Increment(ref failed);
                Interlocked.CompareExchange(ref firstError, Message(error), "");
            }
            var done = Volatile.Read(ref completed) + Volatile.Read(ref failed);
            progress?.Invoke(done, entryIds.Count, "Processed " + done.ToString(CultureInfo.InvariantCulture) + "/" + entryIds.Count.ToString(CultureInfo.InvariantCulture)
                + " entries; " + Volatile.Read(ref failed).ToString(CultureInfo.InvariantCulture) + " failed");
        }).ConfigureAwait(false);
        return new JsonObject { ["entries"] = entryIds.Count, ["completed"] = completed, ["failed"] = failed, ["improved"] = improved, ["first_error"] = firstError };
    }

    /// <summary>enrich_entry_metadata; answers whether source detail was applied.</summary>
    public async Task<bool> EnrichEntryAsync(long entryId, CancellationToken cancellationToken)
    {
        using var metric = Diagnostics.RuntimeMetrics.Shared.Phase("metadata");
        var settings = _settings();
        var id = Read(session => CatalogueTitles.Canonical(session, entryId));
        if (Identity is { } identity && settings.MetadataIdentityEnabled)
        {
            try { await identity.ResolveAsync(id, force: false, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { }
        }
        if (!NeedsMetadata(id)) return false;
        var records = Read(session => session.Query("""
            SELECT sm.source_id,sm.source_manga_id,sm.last_checked,COALESCE(sc.priority,1000) priority,0 is_variant
              FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id WHERE sm.entry_id=$id AND sm.active=1 AND COALESCE(sc.available,1)=1
            UNION ALL
            SELECT sv.source_id,sv.source_manga_id,sv.last_checked,COALESCE(sc.priority,1000) priority,1 is_variant
              FROM source_variants sv LEFT JOIN source_config sc ON sc.source_id=sv.source_id WHERE sv.entry_id=$id AND sv.active=1 AND COALESCE(sc.available,1)=1
            ORDER BY priority,is_variant,last_checked DESC
            """, ("$id", id)).Select(row => (Source: row.Text("source_id"), Manga: row.Integer("source_manga_id"))).Take(Math.Max(1, settings.MetadataEnrichmentSourceLimit)).ToArray());
        var applied = false;
        foreach (var (source, manga) in records)
        {
            if (!NeedsMetadata(id)) break;
            cancellationToken.ThrowIfCancellationRequested();
            JsonObject payload;
            try { payload = await Deadline(token => _client.MangaAsync(manga, true, false, token), settings.MetadataEnrichmentTimeout, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { SourceResult?.Invoke(source, error); continue; }
            SourceResult?.Invoke(source, null);
            if (payload["manga"] is not JsonObject detail || detail.Count == 0) continue;
            if (_ingest.ApplySourceDetailIfCurrent(id, source, manga, detail)) { applied = true; ChooseMetadata?.Invoke(id); }
        }
        ChooseMetadata?.Invoke(id);
        return applied;
    }

    // ---- record discovery --------------------------------------------------------------------------------------------

    /// <summary>schedule_catalog_additional_record_scan: matched entries, those without additional records first, newest first.</summary>
    public IReadOnlyList<long> AdditionalScanEntries(int limit) => Read(session => session.Query("""
        SELECT e.id FROM entries e WHERE EXISTS(SELECT 1 FROM source_matches sm WHERE sm.entry_id=e.id AND sm.active=1)
         ORDER BY CASE WHEN EXISTS(SELECT 1 FROM source_variants sv WHERE sv.entry_id=e.id AND sv.active=1) THEN 1 ELSE 0 END, e.updated_at DESC
         LIMIT $limit
        """, ("$limit", Math.Clamp(limit, 1, 5000))).Select(row => row.Integer("id")).ToArray());

    /// <summary>The catalogue-wide additional-record scan: <see cref="DiscoverAllAsync"/> for each entry.</summary>
    public async Task<JsonObject> ScanAdditionalAsync(IReadOnlyList<long> entryIds, DiscoveryProgress? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var added = 0; var index = 0; var failed = 0;
        foreach (var entryId in entryIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { added += Count(await DiscoverAllAsync(entryId, exhaustive: false, cancellationToken).ConfigureAwait(false), "added"); }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { failed++; }
            index++;
            progress?.Invoke(index, entryIds.Count, "Scanned entry " + entryId.ToString(CultureInfo.InvariantCulture) + " · " + added.ToString(CultureInfo.InvariantCulture) + " record(s) added");
        }
        return new JsonObject { ["entries"] = entryIds.Count, ["added"] = added, ["failed"] = failed };
    }

    /// <summary>
    /// The Subbed search-all: every subscribed entry (entries without records first), or only the source-less ones
    /// (<paramref name="unmatchedOnly"/>, a priority rebuild that stops at the first found record), each within
    /// SUBBED_SEARCH_ENTRY_TIMEOUT_SECONDS. An exhaustive retry visits the sources the last run did not reach first.
    /// </summary>
    public async Task<JsonObject> SearchSubbedAsync(IReadOnlyList<long> entryIds, bool unmatchedOnly, DiscoveryProgress? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var settings = _settings();
        var added = 0; var partial = 0; var errors = 0;
        for (var index = 0; index < entryIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryId = entryIds[index];
            var title = Read(session => session.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", CatalogueTitles.Canonical(session, entryId))) as string) ?? "Entry " + entryId.ToString(CultureInfo.InvariantCulture);
            progress?.Invoke(index, entryIds.Count, "Searching " + title);
            var before = RecordCount(entryId); var note = "";
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.01, settings.SubbedSearchEntryTimeoutSeconds)));
            try
            {
                var result = unmatchedOnly
                    ? await DiscoverMissingAsync(entryId, exhaustive: true, priorityRebuild: true, budget.Token).ConfigureAwait(false)
                    : await DiscoverAllAsync(entryId, exhaustive: true, budget.Token).ConfigureAwait(false);
                added += Count(result, "added");
                if (result["errors"] is JsonArray { Count: > 0 } || Count(result, "deferred_sources") > 0) { partial++; note = " · partial search; retry available"; }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Found records are already committed; Retry continues with the sources not reached.
                added += Math.Max(0, RecordCount(entryId) - before); partial++; note = " · time budget reached; retry to continue";
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                errors++; note = " · search failed; retry available";
            }
            progress?.Invoke(index + 1, entryIds.Count, title + " · " + added.ToString(CultureInfo.InvariantCulture) + " record(s) added" + note);
        }
        var message = (unmatchedOnly ? "Repair unmatched" : "Search all") + " complete · " + added.ToString(CultureInfo.InvariantCulture) + " record(s) added";
        if (partial != 0 || errors != 0)
            message += " · partial: " + partial.ToString(CultureInfo.InvariantCulture) + " incomplete, " + errors.ToString(CultureInfo.InvariantCulture) + " failed · Retry to continue";
        return new JsonObject { ["entries"] = entryIds.Count, ["added"] = added, ["partial"] = partial, ["failed"] = errors, ["message"] = message };
    }

    /// <summary>schedule_subbed_search_all ordering: canonical ids once; source-less first, or only them.</summary>
    public IReadOnlyList<long> SubbedOrder(IEnumerable<long> entryIds, bool unmatchedOnly)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        return Read(session =>
        {
            var ids = entryIds.Where(id => id > 0).Select(id => CatalogueTitles.Canonical(session, id)).Distinct()
                .Where(id => session.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", id)) is not null).ToArray();
            var covered = ids.Where(id => session.Scalar("SELECT 1 FROM source_matches WHERE entry_id=$id AND active=1 UNION ALL SELECT 1 FROM source_variants WHERE entry_id=$id AND active=1 LIMIT 1",
                ("$id", id)) is not null).ToHashSet();
            return unmatchedOnly ? ids.Where(id => !covered.Contains(id)).ToArray() : ids.OrderBy(id => covered.Contains(id)).ToArray();
        });
    }

    /// <summary>discover_all_source_records: missing primary records first, then additional records on matched sources.</summary>
    public async Task<JsonObject> DiscoverAllAsync(long entryId, bool exhaustive, CancellationToken cancellationToken)
    {
        var missing = await DiscoverMissingAsync(entryId, exhaustive, priorityRebuild: false, cancellationToken).ConfigureAwait(false);
        var additional = await DiscoverAdditionalAsync(entryId, cancellationToken).ConfigureAwait(false);
        var errors = new JsonArray();
        foreach (var node in (missing["errors"] as JsonArray ?? new JsonArray()).Concat(additional["errors"] as JsonArray ?? new JsonArray())) errors.Add(node?.DeepClone());
        return new JsonObject
        {
            ["entry_id"] = Read(session => CatalogueTitles.Canonical(session, entryId)),
            ["primary_added"] = Count(missing, "added"), ["additional_added"] = Count(additional, "added"),
            ["added"] = Count(missing, "added") + Count(additional, "added"), ["checked"] = Count(missing, "checked") + Count(additional, "checked"),
            ["searched_sources"] = Count(missing, "searched_sources"), ["errors"] = errors, ["deferred_sources"] = Count(missing, "deferred_sources"),
        };
    }

    /// <summary>
    /// discover_missing_source_matches: searches the enabled sources the entry has no record on (AUTO_MISSING_SOURCE_SOURCE_LIMIT
    /// of them unless exhaustive) with its names, inspects up to three promising hits per source with live detail and
    /// attaches the first one that passes the strict evidence. Each source's outcome is saved.
    /// </summary>
    public async Task<JsonObject> DiscoverMissingAsync(long entryId, bool exhaustive, bool priorityRebuild, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var id = Read(session => CatalogueTitles.Canonical(session, entryId));
        if (!settings.AutoMissingSourceDiscovery) return Empty(id, disabled: true);
        if (Identity is { } identity && settings.MetadataIdentityEnabled && !Read(session => session.Scalar("SELECT 1 FROM identity_links WHERE entry_id=$id LIMIT 1", ("$id", id)) is not null))
        {
            try { await identity.ResolveAsync(id, force: false, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { }
        }
        if (!_ingest.EntryExists(id)) return Empty(id, disabled: false);
        var (sources, checkedAt) = Read(session =>
        {
            var active = session.Query("SELECT source_id FROM source_matches WHERE entry_id=$id AND active=1 UNION SELECT source_id FROM source_variants WHERE entry_id=$id AND active=1", ("$id", id))
                .Select(row => row.Text("source_id")).ToHashSet(StringComparer.Ordinal);
            var rows = session.Query("""
                SELECT source_id,COALESCE(NULLIF(display_name,''),name) name FROM source_config
                 WHERE available=1 AND enabled_language=1 AND enabled_search=1 ORDER BY priority,name,lang
                """).Select(row => (Id: row.Text("source_id"), Name: row.Text("name"))).Where(row => !active.Contains(row.Id)).ToList();
            var outcomes = session.Query("SELECT source_id,checked_at FROM source_discovery_outcomes WHERE entry_id=$id", ("$id", id))
                .ToDictionary(row => row.Text("source_id"), row => row.Text("checked_at"), StringComparer.Ordinal);
            return (rows, outcomes);
        });
        // A bounded run may stop early: an exhaustive retry visits the sources not reached last time first.
        if (exhaustive || priorityRebuild) sources = sources.OrderBy(source => checkedAt.GetValueOrDefault(source.Id, ""), StringComparer.Ordinal).ToList();
        else sources = sources.Take(Math.Max(1, settings.AutoMissingSourceSourceLimit)).ToList();
        var trusted = _ingest.TrustedAutomaticEntryEvidence(id);
        var names = SearchNames(id, settings);
        var added = 0; var checkedSources = 0; var searched = 0; var deferred = 0; var errors = new JsonArray();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            searched++;
            var outcome = await SearchSourceAsync(id, source.Id, source.Name, names, trusted, variantOnly: false, settings, cancellationToken).ConfigureAwait(false);
            if (outcome.Error is not null) errors.Add(outcome.Error); else checkedSources++;
            if (outcome.Deferred) deferred++;
            added += outcome.Added;
            if (priorityRebuild && added > 0) break;
        }
        return new JsonObject
        {
            ["entry_id"] = Read(session => CatalogueTitles.Canonical(session, id)), ["added"] = added, ["checked"] = checkedSources, ["searched_sources"] = searched,
            ["errors"] = errors, ["deferred_sources"] = deferred,
        };
    }

    /// <summary>
    /// discover_additional_source_records: on every source the entry already has a primary record on, further strong
    /// matches are kept as additional records (no fixed cap), each re-proved against the entry's trusted evidence.
    /// </summary>
    public async Task<JsonObject> DiscoverAdditionalAsync(long entryId, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var id = Read(session => CatalogueTitles.Canonical(session, entryId));
        if (!settings.AutoVariantDiscovery) return Empty(id, disabled: true);
        var sources = Read(session => session.Query("""
            SELECT sm.source_id,COALESCE(NULLIF(sc.display_name,''),sc.name) name,sc.lang FROM source_matches sm JOIN source_config sc ON sc.source_id=sm.source_id
             WHERE sm.entry_id=$id AND sm.active=1 AND sc.available=1 AND sc.enabled_language=1 AND sc.enabled_search=1 ORDER BY sc.priority,sc.name
            """, ("$id", id)).Select(row => (Id: row.Text("source_id"), Name: row.Text("name"), Language: row.Text("lang"))).ToArray());
        if (sources.Length == 0) return Empty(id, disabled: false);
        var trusted = _ingest.TrustedAutomaticEntryEvidence(id);
        var names = SearchNames(id, settings);
        var added = 0; var checkedSources = 0; var errors = new JsonArray();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceNames = await TranslatedNamesAsync(id, names, source.Language, settings, cancellationToken).ConfigureAwait(false);
            var outcome = await SearchSourceAsync(id, source.Id, source.Name, sourceNames, trusted, variantOnly: true, settings, cancellationToken).ConfigureAwait(false);
            if (outcome.Error is not null) errors.Add(outcome.Error); else checkedSources++;
            added += outcome.Added;
        }
        return new JsonObject { ["entry_id"] = id, ["added"] = added, ["checked"] = checkedSources, ["errors"] = errors };
    }

    private sealed record SourceOutcome(int Added, string? Error, bool Deferred);

    private async Task<SourceOutcome> SearchSourceAsync(long entryId, string sourceId, string sourceName, IReadOnlyList<string> names, TrustedEntryEvidence trusted,
        bool variantOnly, AutoSourcarrSettings settings, CancellationToken cancellationToken)
    {
        using var clock = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        clock.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.01, settings.SourceRecoveryTimeoutSeconds)));
        var titles = trusted.Titles.Concat(names).Where(title => title.Length != 0).Distinct(StringComparer.Ordinal).ToArray();
        var state = "no_results"; var message = "No search results for the attempted titles."; long? ownerHint = null;
        var added = 0; var hits = 0;
        try
        {
            var seen = new HashSet<long>(); var inspected = 0;
            foreach (var name in names)
            {
                if (inspected >= InspectedPerSource || added > 0) break;
                var page = await Deadline(token => _client.BrowseAsync(sourceId, "SEARCH", 1, name, token), settings.FederatedSourceTimeout, clock.Token).ConfigureAwait(false);
                SourceResult?.Invoke(sourceId, null);
                foreach (var card in (page["mangas"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    if (inspected >= InspectedPerSource) break;
                    var mangaId = CatalogueTitles.MangaId(card["id"]);
                    if (mangaId <= 0 || !seen.Add(mangaId)) continue;
                    hits++;
                    var cardTitle = card["title"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
                    if (titles.Select(title => TextMatcher.TitleSimilarity(cardTitle, title)).DefaultIfEmpty(0).Max() < PromisingTitle) continue;
                    var owner = _ingest.FindExistingBySource(sourceId, mangaId);
                    if (owner is { } owned)
                    {
                        if (_ingest.CanonicalEntryId(owned) != entryId) { ownerHint = _ingest.CanonicalEntryId(owned); state = "owned"; message = "Verified candidate belongs to another catalogue entry; review the duplicate or move it manually."; }
                        continue;
                    }
                    inspected++;
                    // Never prove a record from its thin search card: live detail first.
                    var detail = await Deadline(token => _client.MangaAsync(mangaId, true, false, token), settings.RefreshSourceTimeout, clock.Token).ConfigureAwait(false);
                    if (detail["manga"] is not JsonObject manga || CatalogueTitles.MangaId(manga["id"] ?? mangaId) != mangaId) continue;
                    manga["id"] = mangaId;
                    if (state == "no_results") { state = "verification"; message = "Candidates need independent identity/edition verification."; }
                    var evidence = _discovery.SourceMatchEvidence(entryId, sourceId, manga, trusted);
                    if (!evidence.AutomaticMatch) continue;
                    if (!_ingest.AttachAutomaticRecordIfCurrent(entryId, sourceId, manga, evidence.Score, variantOnly ? "auto-variant" : "auto-discovered", variantOnly)) continue;
                    added++; state = "matched"; message = "Source recovered."; ownerHint = null;
                    RecordAttached?.Invoke(entryId, sourceId, mangaId);
                    if (!variantOnly) break;
                }
            }
            if (hits != 0 && state == "no_results") { state = "verification"; message = "Search results did not resemble this entry's titles."; }
            SaveOutcome(entryId, sourceId, state, message, ownerHint);
            return new SourceOutcome(added, null, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SaveOutcome(entryId, sourceId, "deferred", "Source verification interrupted; retry to continue.", null);
            throw;
        }
        catch (Exception error)
        {
            var timedOut = error is OperationCanceledException or TimeoutException;
            var deferred = timedOut || error is SourceWorkDeferredException;
            var text = sourceName + ": " + (timedOut ? "source recovery time budget reached; retry to continue" : Message(error));
            if (!deferred) SourceResult?.Invoke(sourceId, error);
            SaveOutcome(entryId, sourceId, deferred ? "deferred" : "unavailable", text, null);
            return new SourceOutcome(added, text, deferred);
        }
    }

    /// <summary>The entry's search names (_expanded_search_names), at most AUTO_VARIANT_QUERY_LIMIT (≥ 1, ≤ 6).</summary>
    private IReadOnlyList<string> SearchNames(long entryId, AutoSourcarrSettings settings) =>
        _ingest.ExpandedSearchNames(entryId).Where(name => name.Kind != "transliteration" || name.Query.Length >= 3).Select(name => name.Query)
            .Take(Math.Clamp(settings.AutoVariantQueryLimit, 1, 6)).ToArray();

    /// <summary>
    /// 0.4.5 discover_additional_source_records: the entry's names plus the translated names for this source's language
    /// (at most MANUAL_TRANSLATION_QUERY_LIMIT more). A failed translation only means no extra names.
    /// </summary>
    private async Task<IReadOnlyList<string>> TranslatedNamesAsync(long entryId, IReadOnlyList<string> names, string sourceLanguage, AutoSourcarrSettings settings,
        CancellationToken cancellationToken)
    {
        if (TranslateNames is not { } translate || settings.ManualTranslationQueryLimit <= 0) return names;
        var genre = Read(session => session.Scalar("SELECT COALESCE(genre,'') FROM entries WHERE id=$id", ("$id", entryId)) as string ?? "");
        var own = _ingest.ExpandedSearchNames(entryId);
        var expanded = await translate(own, genre, sourceLanguage, cancellationToken).ConfigureAwait(false);
        var extra = expanded.Where(name => name.Kind.StartsWith("translated", StringComparison.Ordinal)).Select(name => name.Query)
            .Where(query => !names.Contains(query, StringComparer.Ordinal)).Take(settings.ManualTranslationQueryLimit);
        return [.. names, .. extra];
    }

    /// <summary>_save_source_discovery_outcome.</summary>
    private void SaveOutcome(long entryId, string sourceId, string state, string message, long? candidate) => Write(session =>
    {
        var id = CatalogueTitles.Canonical(session, entryId);
        if (session.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", id)) is null) return 0;
        return session.Execute("""
            INSERT INTO source_discovery_outcomes(entry_id,source_id,state,message,candidate_entry_id,checked_at) VALUES($id,$source,$state,$message,$candidate,$stamp)
            ON CONFLICT(entry_id,source_id) DO UPDATE SET state=excluded.state,message=excluded.message,candidate_entry_id=excluded.candidate_entry_id,checked_at=excluded.checked_at
            """, ("$id", id), ("$source", sourceId), ("$state", state), ("$message", message.Length > 600 ? message[..600] : message), ("$candidate", candidate), ("$stamp", Stamp()));
    });

    private int RecordCount(long entryId) => Read(session =>
    {
        var id = CatalogueTitles.Canonical(session, entryId);
        return Convert.ToInt32(session.Scalar("SELECT (SELECT COUNT(*) FROM source_matches WHERE entry_id=$id AND active=1)+(SELECT COUNT(*) FROM source_variants WHERE entry_id=$id AND active=1)",
            ("$id", id)), CultureInfo.InvariantCulture);
    });

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static async Task<JsonObject> Deadline(Func<CancellationToken, Task<JsonObject>> call, double seconds, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.5, seconds)));
        try { return await call(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The source did not answer in time.", error); }
    }

    private static JsonObject Empty(long entryId, bool disabled) => new()
    {
        ["entry_id"] = entryId, ["added"] = 0, ["checked"] = 0, ["searched_sources"] = 0, ["errors"] = new JsonArray(), ["deferred_sources"] = 0, ["disabled"] = disabled,
    };

    private static int Count(JsonObject result, string key) => result[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
    private static string Message(Exception error) => error.Message.Length != 0 ? error.Message : error.GetType().Name;
    private T Read<T>(Func<DatabaseSession, T> operation) { lock (_gate) return _database.Read(DatabaseFile.Catalog, operation); }
    private T Write<T>(Func<DatabaseSession, T> operation) { lock (_gate) return _database.Write(DatabaseFile.Catalog, operation); }
    private string Stamp() => _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
}
