using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Chapters;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Catalogue.Browse;

/// <summary>When reader data is considered old enough to refresh in the background (READ-3).</summary>
public sealed record ReaderFreshness(TimeSpan Feeds, TimeSpan Chapters, int PageSize)
{
    public static ReaderFreshness Default { get; } = new(TimeSpan.FromMinutes(10), TimeSpan.FromHours(6), 50);
}

/// <summary>
/// The reader catalogue (Sources, Entry, Popular/Latest/Search, Refresh) under READ-3: everything the catalogue already
/// holds is served at once, and old data only schedules a background refresh. Popular/Latest serve the saved feed pages
/// (browse_cache, written by the feed crawl job) and queue a crawl when a page is missing or older than
/// <see cref="ReaderFreshness.Feeds"/>; a missing page answers "preparing" (503 + Retry-After) instead of an empty or
/// unrelated list. Search ranks the local catalogue by title similarity (live per-source search stays with the source
/// browse of the matching tools). Opening an entry queues a chapter refresh of its records when their last check is older
/// than <see cref="ReaderFreshness.Chapters"/>; Refresh fetches the records' chapters now.
/// </summary>
public sealed class ReaderBrowse(SqliteDatabase database, object sourceWriteGate, IReaderCatalogueRead read, ReaderRepository chapters,
    ChapterRefresh refresh, BoundedWorkScheduler io, Func<ReaderFreshness>? freshness = null, TimeProvider? clock = null) : IReaderCatalogue
{
    private CatalogueQueries Queries => new(database, sourceWriteGate, _ => false);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<ReaderFreshness> _freshness = freshness ?? (() => ReaderFreshness.Default);

    /// <summary>Queues the Popular/Latest crawl (the discovery feed job); true when queued or already running.</summary>
    public Func<bool>? ScheduleFeedRefresh { get; init; }
    /// <summary>Where background chapter refreshes run; none means stored data only.</summary>
    public JobQueue? Jobs { get; init; }
    /// <summary>Refreshes one record's details (metadata hydration) for Refresh with fetch_details.</summary>
    public Action<string, long>? HydrateDetails { get; init; }

    public Task<JsonObject> SourcesAsync(CancellationToken cancellationToken) => read.SourcesAsync(cancellationToken);

    public async Task<JsonObject> EntryAsync(long entryId, CancellationToken cancellationToken)
    {
        var entry = await read.EntryAsync(entryId, cancellationToken).ConfigureAwait(false);
        ChaptersRead(entryId);
        return entry;
    }

    /// <summary>READ-3 for chapter lists: the stored list is served; old records get a background refresh job.</summary>
    public void ChaptersRead(long entryId)
    {
        // A background refresh that cannot be queued (deleted work, shutdown) never fails the read it follows.
        try { refresh.ScheduleIfStale(Jobs, entryId, _freshness().Chapters); }
        catch (Exception error) when (error is KeyNotFoundException or ObjectDisposedException or InvalidOperationException) { }
    }

    public async Task<JsonObject> RefreshAsync(long entryId, bool fetchDetails, bool fetchChapters, CancellationToken cancellationToken)
    {
        _ = await read.EntryAsync(entryId, cancellationToken).ConfigureAwait(false); // 404 for unknown works
        if (fetchDetails && HydrateDetails is { } hydrate)
            foreach (var record in refresh.Records(entryId)) hydrate(record.SourceId, record.MangaId);
        if (fetchChapters) await refresh.RefreshEntryAsync(entryId, null, cancellationToken).ConfigureAwait(false);
        var entry = await read.EntryAsync(entryId, cancellationToken).ConfigureAwait(false);
        var list = await io.RunAsync(() => chapters.ListChapters(entryId), cancellationToken).ConfigureAwait(false);
        return new JsonObject { ["entry"] = entry, ["chapters"] = new JsonArray(list.Select(row => (JsonNode)row).ToArray()) };
    }

    public Task<JsonObject> BrowseAsync(ReaderBrowseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return io.RunAsync(() => Browse(request), cancellationToken);
    }

    /// <summary>The admin catalogue browse: {mode, q, page, min_matches, match_count, sources, sort, page_size}.</summary>
    public Task<JsonObject> BrowseAsync(JsonObject request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string Text(string name, string fallback) => request[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
        int Number(string name, int fallback) => request[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;
        var sources = request["sources"] is JsonArray array ? array.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : "").Where(text => text.Length > 0).ToArray() : null;
        int? exact = request["match_count"] is JsonValue match && match.TryGetValue<int>(out var count) ? count : null;
        var specification = new ReaderBrowseRequest(Text("mode", "POPULAR").ToUpperInvariant(), Math.Max(1, Number("page", 1)), Text("q", ""), sources,
            Math.Max(0, Number("min_matches", 1)), exact, Text("sort", "relevance"));
        return io.RunAsync(() => Browse(specification, Math.Clamp(Number("page_size", _freshness().PageSize), 1, 500)), cancellationToken);
    }

    private JsonObject Browse(ReaderBrowseRequest request, int? pageSize = null)
    {
        var size = Math.Clamp(pageSize ?? _freshness().PageSize, 1, 500);
        var mode = request.Mode.ToUpperInvariant();
        var query = UnicodeText.Trim(request.Query);
        if (mode == "SEARCH" && query.Length == 0) mode = "POPULAR"; // an empty search is a filtered Popular feed
        if (mode == "SEARCH") return Search(request, query, size);
        if (mode is not ("POPULAR" or "LATEST")) throw new ArgumentException("Unknown browse mode");
        var saved = database.Read(DatabaseFile.Catalog, session => session.Query("""
            SELECT cache_key,entry_ids_json,has_next_page,source_errors_json,updated_at FROM browse_cache
            WHERE mode=$mode AND page=$page AND query='' AND source_ids_json='[]' AND min_matches=1 AND exact_match_count IS NULL AND sort='relevance'
            ORDER BY updated_at DESC
            """, ("$mode", mode), ("$page", request.Page)).FirstOrDefault(row => Ids(row.Text("entry_ids_json")).Count > 0));
        if (saved is null)
        {
            ScheduleFeedRefresh?.Invoke();
            throw new ReaderBrowsePendingException();
        }
        var age = Age(saved.Text("updated_at"));
        var refreshing = age > _freshness().Feeds && ScheduleFeedRefresh?.Invoke() == true;
        IReadOnlyList<long> ids = Canonical(Ids(saved.Text("entry_ids_json")));
        if (request.SourceIds is { Count: > 0 }) ids = Queries.SourceScopedEntryIds(ids, request.SourceIds);
        var items = Filter(Queries.Summaries(ids), request).Take(size).ToArray();
        return new JsonObject
        {
            ["items"] = new JsonArray(items.Select(item => (JsonNode)item).ToArray()), ["page_size"] = size,
            ["has_next_page"] = saved.Flag("has_next_page"), ["source_errors"] = JsonNode.Parse(saved.Text("source_errors_json", "[]")) ?? new JsonArray(),
            ["cache_hit"] = true, ["persistent_cache"] = true, ["cache_updated_at"] = saved.Text("updated_at"),
            ["cache_age_seconds"] = double.IsFinite(age.TotalSeconds) ? Math.Round(age.TotalSeconds, 3) : null, ["refreshing"] = refreshing, ["cataloged_entries"] = 0,
        };
    }

    private JsonObject Search(ReaderBrowseRequest request, string query, int size)
    {
        var ids = Queries.SearchEntryIds(query, 200, null, request.SourceIds is { Count: > 0 } ? request.SourceIds : null);
        var titles = database.Read(DatabaseFile.Catalog, session => ids.ToDictionary(id => id, id => session.Query(
            "SELECT title FROM titles WHERE entry_id=$id UNION SELECT preferred_title FROM entries WHERE id=$id", ("$id", id)).Select(row => row.Text("title")).ToArray()));
        var scored = Filter(Queries.Summaries(ids), request).Select(item =>
        {
            var similarity = titles.GetValueOrDefault(ReaderValue.Integer(item, "canonical_id")) is { Length: > 0 } names ? names.Max(name => TextMatcher.TitleSimilarity(query, name)) : 0;
            item["match_confidence"] = similarity;
            item["discovery_score"] = similarity * 1500 + ReaderValue.Integer(item, "match_count") * 300;
            return item;
        }).ToList();
        var title = (JsonObject item) => UnicodeCaseFold.Fold(ReaderValue.Text(item, "preferred_title"));
        IEnumerable<JsonObject> ordered = request.Sort switch
        {
            "sources" => scored.OrderByDescending(item => ReaderValue.Integer(item, "match_count")).ThenByDescending(item => ReaderValue.Number(item, "discovery_score")).ThenBy(title, StringComparer.Ordinal),
            "title" => scored.OrderBy(title, StringComparer.Ordinal),
            _ => scored.OrderByDescending(item => ReaderValue.Number(item, "discovery_score")).ThenBy(title, StringComparer.Ordinal),
        };
        var items = ordered.Skip((request.Page - 1) * size).Take(size).ToArray();
        return new JsonObject
        {
            ["items"] = new JsonArray(items.Select(item => (JsonNode)item).ToArray()), ["page_size"] = size,
            ["has_next_page"] = scored.Count > request.Page * size, ["source_errors"] = new JsonArray(), ["cache_hit"] = true,
            ["persistent_cache"] = false, ["catalog_fallback"] = true, ["refreshing"] = false, ["cataloged_entries"] = 0,
        };
    }

    private static IEnumerable<JsonObject> Filter(IEnumerable<JsonObject> items, ReaderBrowseRequest request) => items.Where(item =>
    {
        var matches = ReaderValue.Integer(item, "match_count");
        return request.ExactMatchCount is { } exact ? matches == exact : matches >= request.MinimumMatches;
    });

    private List<long> Canonical(IReadOnlyList<long> ids) => database.Read(DatabaseFile.Catalog, session =>
        ids.Select(id => Sharing.PortableIdentityResolver.Canonical(session, id)).Distinct().ToList());

    private static List<long> Ids(string json)
    {
        try { return (JsonNode.Parse(json) as JsonArray ?? new JsonArray()).OfType<JsonValue>().Select(value => value.TryGetValue<long>(out var id) ? id : 0).Where(id => id > 0).ToList(); }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private TimeSpan Age(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp)
        ? TimeSpan.FromSeconds(Math.Max(0, (_clock.GetUtcNow() - stamp).TotalSeconds)) : TimeSpan.MaxValue;
}
