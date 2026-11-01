using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Chapters;
using AutoSourcarr.Core.Catalogue.Serving;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static AutoSourcarr.Api.Endpoints.ManualDecisionsV1Endpoints;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the chapter routes need from the running app (null members answer 503 on their routes).</summary>
public sealed class ChaptersV1Services
{
    public required SqliteDatabase Database { get; init; }
    /// <summary>Entry lists and chapter coverage (CatalogueQueries over the catalogue write gate).</summary>
    public required CatalogueQueries Queries { get; init; }
    public ChapterCandidateReview? Review { get; init; }
    public CandidateQuality? Quality { get; init; }
    public ChapterRefresh? Refresh { get; init; }
    /// <summary>The last main-library (Subbed) snapshot for the Matrix subbed view: {ok, entry_ids, …}.</summary>
    public Func<JsonObject>? Subscription { get; init; }
    /// <summary>source_discovery_outcomes for the Matrix rows (owned by discovery); null answers {}.</summary>
    public Func<IReadOnlyList<long>, JsonObject>? DiscoveryOutcomes { get; init; }
    /// <summary>CoverCache.ClearKeys for refresh-all.</summary>
    public Func<IReadOnlyList<string>, int>? ClearCovers { get; init; }
    /// <summary>Queues a metadata refresh of one record (NativeMetadataHydration.Schedule) for refresh-all.</summary>
    public Action<string, long>? HydrateDetails { get; init; }
    /// <summary>Queues a forced provider-identity refresh of the work (schedule_entry_identity) for refresh-all.</summary>
    public Action<long>? ScheduleIdentity { get; init; }
}

/// <summary>
/// Phase 10 chapter routes under /api/v1 (0.4.5 /api/admin/candidates/*, /matrix, /matrix/chapter-stats,
/// /entries/{id}/source-records/…/check-chapters and /entries/{id}/refresh-all). GET /api/v1/chapters/{chapter_id}
/// (chapter details, the 0.4.5 admin_chapter) and GET/POST /api/v1/entries/{entry_id}/refresh are the reader routes of
/// ReaderEndpoints; the reader alias reaches only GET routes there. Changes record the caller in the decision log;
/// chapter checks run as jobs in Tasks.
/// </summary>
internal static class ChaptersV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, ChaptersV1Services? chapters)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Chapter management is not available in this host.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        static bool Flag(HttpContext context, string name, bool fallback) =>
            context.Request.Query.TryGetValue(name, out var values) && values.Count > 0 ? values[^1] is "1" or "true" or "True" or "yes" : fallback;

        group.MapPost("/candidates/{candidateId}/quality", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (chapters?.Quality is null) return Unavailable();
                if (!TryRouteLong(context, "candidateId", out var candidateId)) return Problem(400, "Invalid candidate id.");
                JsonObject body;
                try { body = await Body(context).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                var samples = BodyLong(body, "sample_count") is var count and > 0 ? (int)Math.Min(12, count) : 5;
                try { return Answer(await chapters.Quality.AnalyzeAsync(candidateId, samples, Actor(context), context.RequestAborted).ConfigureAwait(false)); }
                catch (KeyNotFoundException error) { return Problem(404, error.Message); }
                catch (InvalidOperationException error) { return Problem(409, error.Message); }
                catch (SourceWorkDeferredException error) { return Problem(503, error.Message); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("sampleCandidateQuality")
            .WithSummary("Sample a chapter candidate's pages (body {sample_count}, 2-12, default 5) and score its quality from page size, file size and broken pages. A run that cannot observe every sampled page keeps the old score; every attempt is recorded.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        foreach (var (verb, name, summary) in new[]
                 {
                     ("reject", "rejectChapterCandidate", "Reject one chapter candidate: it stays rejected after source refreshes and is not served until restored. Answers the chapter details."),
                     ("restore", "restoreChapterCandidate", "Restore a rejected chapter candidate so the resolver may serve it again. Answers the chapter details."),
                 })
        {
            var reject = verb == "reject";
            group.MapPost("/candidates/{candidateId}/" + verb, (Func<HttpContext, IResult>)(context =>
                {
                    if (chapters?.Review is null) return Unavailable();
                    if (!TryRouteLong(context, "candidateId", out var candidateId)) return Problem(400, "Invalid candidate id.");
                    try { return Answer(reject ? chapters.Review.Reject(candidateId, Actor(context)) : chapters.Review.Restore(candidateId, Actor(context))); }
                    catch (KeyNotFoundException) { return Problem(404, "Chapter candidate not found"); }
                }))
                .WithName(name)
                .WithSummary(summary)
                .Produces<JsonObject>(StatusCodes.Status200OK)
                .Produces<ProblemResponse>(StatusCodes.Status404NotFound);
        }

        group.MapGet("/matrix", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (chapters is null) return Unavailable();
                var request = context.Request;
                var search = request.Query["search"].LastOrDefault() ?? "";
                var limit = int.TryParse(request.Query["limit"].LastOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 1, 500) : 100;
                var subbed = Flag(context, "subbed", false); var stats = Flag(context, "include_chapter_stats", true);
                var sourceId = request.Query["source_id"].LastOrDefault() ?? "";
                var result = await Task.Run(() => Matrix(chapters, search, limit, subbed, stats, sourceId), context.RequestAborted).ConfigureAwait(false);
                return Answer(result);
            }))
            .WithName("getMatrix")
            .WithSummary("The catalogue Matrix from stored data: entries (optional query search, limit 1-500, subbed, source_id, include_chapter_stats), their primary and additional records with chapter coverage, the sources and the last main-library snapshot. Duplicate scores load separately.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/matrix/chapter-stats", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (chapters is null) return Unavailable();
                JsonObject body;
                try { body = await Body(context).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (body["ids"] is not JsonArray values || values.Count > 500) return Problem(400, "ids must be an array of at most 500 entry IDs");
                var ids = values.Select(value => value is JsonValue item ? item.TryGetValue<long>(out var number) ? number
                        : item.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromText) ? fromText : 0 : 0)
                    .Where(id => id > 0).Distinct().ToArray();
                var result = await Task.Run(() => ChapterStats(chapters.Queries, ids), context.RequestAborted).ConfigureAwait(false);
                return Answer(result);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("getMatrixChapterStats")
            .WithSummary("Chapter coverage for up to 500 entries (body {ids}): served chapter count and latest chapter per entry, and per attached record its chapter count, latest chapter, missing whole chapters and last check.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest);

        group.MapPost("/entries/{entryId}/source-records/{sourceId}/{sourceMangaId}/check-chapters", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (chapters?.Refresh is null) return Unavailable();
                if (!TryRouteLong(context, "entryId", out var entryId) || !TryRouteLong(context, "sourceMangaId", out var mangaId)) return Problem(400, "Invalid entry or source manga id.");
                var sourceId = Route(context, "sourceId");
                var refresh = chapters.Refresh;
                if (services.Jobs is null)
                {
                    try { return Answer(await refresh.RefreshRecordAsync(entryId, sourceId, mangaId, context.RequestAborted).ConfigureAwait(false)); }
                    catch (KeyNotFoundException) { return Problem(404, "Attached source record not found"); }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
                    catch (Exception error) { return Problem(502, "Unable to refresh chapters: " + error.Message); }
                }
                JsonObject? result = null; Exception? failure = null;
                var handle = services.Jobs.Enqueue(ChapterRefresh.CheckJob, "chapter-check:" + entryId.ToString(CultureInfo.InvariantCulture) + ":" + sourceId + ":" + mangaId.ToString(CultureInfo.InvariantCulture), async job =>
                {
                    try
                    {
                        result = await refresh.RefreshRecordAsync(entryId, sourceId, mangaId, job.CancellationToken).ConfigureAwait(false);
                        job.Result(result["chapters_found"]!.ToJsonString() + " chapter(s) found");
                    }
                    catch (Exception error) when (error is not OperationCanceledException) { failure = error; throw; }
                }, origin: "manual", context: sourceId);
                if (handle.Merged) return Answer(new JsonObject { ["ok"] = true, ["queued"] = false, ["already_running"] = true, ["task_id"] = handle.Id }, 202);
                var outcome = await handle.Completion.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                if (failure is KeyNotFoundException) return Problem(404, "Attached source record not found");
                if (outcome != JobOutcome.Completed || result is null) return Problem(502, "Unable to refresh chapters: " + (failure?.Message ?? outcome.ToString()));
                result["task_id"] = handle.Id;
                return Answer(result);
            }))
            .WithName("checkSourceRecordChapters")
            .WithSummary("Fetch the chapters of one attached source record now (a job in Tasks) and answer its chapter coverage. A failed check is remembered on the record without touching its stored chapters.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status502BadGateway);

        group.MapPost("/entries/{entryId}/refresh-all", (Func<HttpContext, IResult>)(context =>
            {
                if (chapters?.Refresh is null) return Unavailable();
                if (services.Jobs is null) return Problem(503, "The job queue is not running; nothing was started.");
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                JobHandle handle;
                try { handle = chapters.Refresh.ScheduleEntry(services.Jobs, entryId); }
                catch (KeyNotFoundException) { return Problem(404, "Entry not found"); }
                var records = chapters.Refresh.Records(entryId);
                var canonical = records.Count > 0 ? records[0].EntryId : entryId;
                var keys = new HashSet<string>(StringComparer.Ordinal) { "entry-" + entryId.ToString(CultureInfo.InvariantCulture), "entry-" + canonical.ToString(CultureInfo.InvariantCulture) };
                foreach (var record in records) keys.Add("source-" + record.MangaId.ToString(CultureInfo.InvariantCulture));
                var removed = chapters.ClearCovers?.Invoke(keys.ToArray()) ?? 0;
                foreach (var record in records) chapters.HydrateDetails?.Invoke(record.SourceId, record.MangaId);
                chapters.ScheduleIdentity?.Invoke(canonical);
                return Answer(new JsonObject
                {
                    ["queued"] = !handle.Merged, ["already_running"] = handle.Merged, ["entry_id"] = canonical, ["task_id"] = handle.Id,
                    ["removed_cover_files"] = removed, ["metadata_records"] = chapters.HydrateDetails is null ? 0 : records.Count,
                }, 202);
            }))
            .WithName("refreshEntryAll")
            .WithSummary("Refresh the whole work: clear its cached covers, queue a chapter refresh of every record (a job in Tasks), a metadata refresh of every record and a forced provider-identity refresh.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>0.4.5 admin_matrix_chapter_stats: entry_stats keyed by entry id, record_stats as a list.</summary>
    internal static JsonObject ChapterStats(CatalogueQueries queries, IReadOnlyList<long> ids)
    {
        var entries = new JsonObject();
        foreach (var (id, stat) in queries.EntryChapterStats(ids)) entries[id.ToString(CultureInfo.InvariantCulture)] = stat.DeepClone();
        var records = new JsonArray();
        foreach (var (key, stat) in queries.RecordChapterStats(ids))
        {
            var row = new JsonObject { ["entry_id"] = key.EntryId, ["source_id"] = key.SourceId, ["source_manga_id"] = key.MangaId };
            foreach (var (name, value) in stat) row[name] = value?.DeepClone();
            records.Add(row);
        }
        return new JsonObject { ["entry_stats"] = entries, ["record_stats"] = records };
    }

    private static JsonObject Matrix(ChaptersV1Services chapters, string search, int limit, bool subbed, bool includeStats, string sourceId)
    {
        var queries = chapters.Queries;
        var subscription = chapters.Subscription?.Invoke() ?? new JsonObject { ["ok"] = null, ["entry_ids"] = new JsonArray() };
        var subscribed = subbed ? (subscription["entry_ids"] as JsonArray ?? new JsonArray()).OfType<JsonValue>()
            .Select(value => value.TryGetValue<long>(out var id) ? id : 0).Where(id => id > 0).ToArray() : null;
        IReadOnlyList<JsonObject> entries;
        if (sourceId.Length > 0)
        {
            var sources = new[] { sourceId };
            var ids = search.Trim().Length > 0 ? queries.SearchEntryIds(search, limit, subscribed, sources)
                : queries.SourceScopedEntryIds(subscribed ?? SourceEntries(chapters.Database, sourceId, limit), sources).Take(limit).ToArray();
            entries = queries.Summaries(ids);
        }
        else entries = subscribed is not null ? queries.ByIds(subscribed, search, limit) : queries.List(search, limit);
        var entryIds = entries.Select(entry => ReaderValue.Integer(entry, "id")).Where(id => id > 0).Distinct().ToArray();
        IReadOnlyDictionary<long, JsonObject> entryStats = includeStats ? queries.EntryChapterStats(entryIds) : new Dictionary<long, JsonObject>();
        foreach (var entry in entries)
        {
            if (!includeStats) { entry["chapter_stats_pending"] = true; continue; }
            var stat = entryStats.GetValueOrDefault(ReaderValue.Integer(entry, "id"));
            entry["served_chapter_count"] = stat is null ? 0 : ReaderValue.Integer(stat, "served_chapter_count");
            entry["served_latest_chapter"] = stat?["served_latest_chapter"]?.DeepClone();
            entry["chapter_records_checked"] = stat is not null && ReaderValue.Flag(stat, "chapter_records_checked");
        }
        var (matches, variants) = Records(chapters.Database, entryIds);
        IReadOnlyDictionary<(long EntryId, string SourceId, long MangaId), JsonObject> recordStats = includeStats && entryIds.Length > 0 ? queries.RecordChapterStats(entryIds) : new Dictionary<(long EntryId, string SourceId, long MangaId), JsonObject>();
        foreach (var row in matches.Concat(variants))
        {
            if (!includeStats) { row["chapter_stats_pending"] = true; continue; }
            var stat = recordStats.GetValueOrDefault((ReaderValue.Integer(row, "entry_id"), ReaderValue.Text(row, "source_id"), ReaderValue.Integer(row, "source_manga_id")));
            row["chapter_count"] = stat is null ? 0 : ReaderValue.Integer(stat, "chapter_count");
            row["latest_chapter"] = stat?["latest_chapter"]?.DeepClone();
            row["missing_chapter_count"] = stat is null ? 0 : ReaderValue.Integer(stat, "missing_chapter_count");
            row["chapters_checked"] = stat is not null && ReaderValue.Flag(stat, "chapters_checked");
            row["chapter_checked_at"] = stat?["chapter_checked_at"]?.DeepClone();
            row["chapter_check_error"] = stat is null ? "" : ReaderValue.Text(stat, "chapter_check_error");
        }
        var result = new JsonObject
        {
            ["entries"] = new JsonArray(entries.Select(entry => (JsonNode)entry).ToArray()),
            ["sources"] = new JsonArray(queries.Sources().Select(source => (JsonNode)source).ToArray()),
            ["matches"] = new JsonArray(matches.Select(row => (JsonNode)row).ToArray()),
            ["variants"] = new JsonArray(variants.Select(row => (JsonNode)row).ToArray()),
            ["duplicates"] = new JsonObject(), ["duplicates_pending"] = entryIds.Length > 0,
            ["discovery_outcomes"] = chapters.DiscoveryOutcomes?.Invoke(entryIds) ?? new JsonObject(),
            ["subscription"] = subscription,
        };
        if (!includeStats) result["chapter_stats_pending"] = entryIds.Length > 0;
        return result;
    }

    private static long[] SourceEntries(SqliteDatabase database, string sourceId, int limit) => database.Read(DatabaseFile.Catalog, session => session.Query("""
        SELECT e.id FROM entries e WHERE e.id IN (SELECT entry_id FROM source_matches WHERE source_id=$source AND active=1
          UNION SELECT entry_id FROM source_variants WHERE source_id=$source AND active=1) ORDER BY e.updated_at DESC,e.id DESC LIMIT $limit
        """, ("$source", sourceId), ("$limit", limit)).Select(row => row.Integer("id")).ToArray());

    private static (JsonObject[] Matches, JsonObject[] Variants) Records(SqliteDatabase database, IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return (Array.Empty<JsonObject>(), Array.Empty<JsonObject>());
        return database.Read(DatabaseFile.Catalog, session =>
        {
            var marks = string.Join(",", ids.Select((_, index) => "$e" + index.ToString(CultureInfo.InvariantCulture)));
            var parameters = ids.Select((id, index) => ("$e" + index.ToString(CultureInfo.InvariantCulture), (object?)id)).ToArray();
            JsonObject[] Rows(string table, string alias, string idName) => session.Query($"""
                SELECT {alias}.id {idName},{alias}.entry_id,{alias}.source_id,{alias}.source_title,{alias}.confidence,{alias}.match_type,{alias}.locked,
                  {alias}.source_manga_id,{alias}.thumbnail_url,{alias}.description,{alias}.author,{alias}.artist,{alias}.real_url,
                  sc.name source_name,sc.lang source_lang,sc.base_url base_url,sc.priority
                FROM {table} {alias} LEFT JOIN source_config sc ON sc.source_id={alias}.source_id
                WHERE {alias}.active=1 AND {alias}.entry_id IN ({marks})
                """, parameters).Select(row => System.Text.Json.JsonSerializer.SerializeToNode(row.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))!.AsObject()).ToArray();
            return (Rows("source_matches", "sm", "match_id"), Rows("source_variants", "sv", "variant_id"));
        });
    }
}
