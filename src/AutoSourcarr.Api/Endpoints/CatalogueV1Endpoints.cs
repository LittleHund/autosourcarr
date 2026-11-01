using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Metadata.Hydration;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Imaging;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static AutoSourcarr.Api.Endpoints.ManualDecisionsV1Endpoints;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the remaining catalogue routes need from the running app (null members answer 503 on their routes).</summary>
public sealed class CatalogueV1Services
{
    public required SqliteDatabase Database { get; init; }
    /// <summary>Local catalogue reads: the entry list, card summaries and the admin entry view.</summary>
    public required CatalogueQueries Queries { get; init; }
    /// <summary>entry_exists (EntryIngest.EntryExists; aliases resolve).</summary>
    public required Func<long, bool> EntryExists { get; init; }
    /// <summary>entry_history (EntryIngest.EntryHistory).</summary>
    public Func<long, JsonObject>? History { get; init; }
    public CoverHints? Covers { get; init; }
    /// <summary>CoverCache.ClearKeysAsync.</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<int>>? ClearCovers { get; init; }
    public EntryMetadataRefresh? MetadataRefresh { get; init; }
    /// <summary>identity_provider_status (MetadataProviderStatus.Status over MetadataProviderSet.Current()).</summary>
    public Func<JsonArray>? MetadataProviders { get; init; }
    /// <summary>resolve_entry_identity (MetadataIdentityResolver.ResolveAsync).</summary>
    public Func<long, bool, CancellationToken, Task<JsonObject>>? ResolveIdentity { get; init; }
    /// <summary>discover_all_source_records for one entry (CatalogueDiscovery.DiscoverAllAsync, exhaustive).</summary>
    public Func<long, CancellationToken, Task<JsonObject>>? DiscoverAll { get; init; }
    /// <summary>
    /// The federated browse of the Catalogue/Browse area (federated / source_browse): request {mode, q, page, min_matches,
    /// match_count, sources, sort, page_size, force_live, source_id}; null answers 503.
    /// </summary>
    public Func<JsonObject, CancellationToken, Task<JsonObject>>? Browse { get; init; }
    /// <summary>The source engine contract status (SourceEngineCapabilityContract.Status) for the research status.</summary>
    public Func<JsonObject>? Engine { get; init; }
    public Func<RuntimeMetricsSnapshot>? Metrics { get; init; }
}

/// <summary>
/// Phase 10 catalogue routes (the rest of 0.4.5 CatalogueEndpoints): the entry list and admin entry view (under
/// /catalogue/ because GET /api/v1/entries/{id} is the guest/reader entry), card summaries, chapter-origin history, cover
/// refresh, metadata refresh, provider identity refresh, the per-entry source discovery job, the metadata provider list,
/// the federated browse (owned by the browse area) and the research-improvements status. Long work runs as jobs in Tasks.
/// </summary>
internal static class CatalogueV1Endpoints
{
    public static readonly JobType DiscoverJob = new("entry-source-discovery", "Find missing and extra source records", JobPriority.Normal,
        StallTimeout: TimeSpan.FromMinutes(10), RunLimit: 2);

    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, CatalogueV1Services? catalogue)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable(string what = "Catalogue reads are") => Problem(503, what + " not available in this host.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        static JsonArray Array(IEnumerable<JsonObject> rows) => new(rows.Select(row => (JsonNode)row).ToArray());

        bool Exists(long id) => catalogue!.EntryExists(id);
        IResult Queued(JobHandle? handle, long entryId) => handle is null
            ? Problem(503, "The job queue is not available; nothing was scheduled.")
            : Answer(new JsonObject { ["queued"] = !handle.Merged, ["already_running"] = handle.Merged, ["entry_id"] = entryId, ["task_id"] = handle.Id }, 202);
        JobHandle? Enqueue(JobType type, string key, string title, Func<JobContext, Task> work)
        {
            if (services.Jobs is not { } jobs) return null;
            try { return jobs.Enqueue(type, key, work, origin: "manual", title: title); }
            catch (Exception error) when (error is ObjectDisposedException or ArgumentException) { return null; }
        }
        static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);

        group.MapGet("/catalogue/entries", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (catalogue is null) return Unavailable();
                int limit;
                try { limit = ApiQuery.Integer(context.Request, "limit", 200, 1, 1000); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                var search = context.Request.Query["search"].LastOrDefault() ?? "";
                if (search.Length > 500) return Problem(422, "The search text is too long");
                var rows = await Task.Run(() => catalogue.Queries.List(search, limit), context.RequestAborted).ConfigureAwait(false);
                return Answer(new JsonObject { ["items"] = Array(rows) });
            }))
            .WithName("listCatalogueEntries")
            .WithSummary("Catalogue entries as cards, newest first or matching the optional query 'search' (local titles only), at most 'limit' (default 200, at most 1000).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/catalogue/entries/summaries", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (catalogue is null) return Unavailable();
                JsonObject body;
                try { body = await Body(context).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (body["ids"] is not JsonArray raw) return body.ContainsKey("ids") ? Problem(400, "ids must be a JSON array") : Answer(new JsonObject { ["items"] = new JsonArray() });
                var ids = new List<long>();
                foreach (var node in raw)
                {
                    var id = ThumbnailFingerprints.JsonNumber(node);
                    if (id > 0 && !ids.Contains(id)) ids.Add(id);
                    if (ids.Count >= 500) break;
                }
                var rows = await Task.Run(() => catalogue.Queries.Summaries(ids), context.RequestAborted).ConfigureAwait(false);
                return Answer(new JsonObject { ["items"] = Array(rows) });
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("catalogueEntrySummaries")
            .WithSummary("Compact card summaries (cover, title, metadata completeness, sources) of up to 500 entries, body {ids}; lets open pages pick up metadata found in the background without a reload.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/catalogue/entries/{entryId}", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (catalogue is null) return Unavailable();
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                try { return Answer(await Task.Run(() => catalogue.Queries.Entry(entryId), context.RequestAborted).ConfigureAwait(false)); }
                catch (KeyNotFoundException) { return Problem(404, "Entry not found"); }
            }))
            .WithName("getCatalogueEntry")
            .WithSummary("The admin view of one entry: card fields, every title, active and additional source records with chapter statistics, provider identities and the description shown in the extension.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/entries/{entryId}/history", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (catalogue?.History is not { } history) return Unavailable("Chapter history is");
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                if (!Exists(entryId)) return Problem(404, "Entry not found");
                return Answer(await Task.Run(() => history(entryId), context.RequestAborted).ConfigureAwait(false));
            }))
            .WithName("getEntryHistory")
            .WithSummary("The entry's chapters and which source served each of them when (newest first).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/entries/{entryId}/refresh-covers", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (catalogue?.Covers is not { } covers || catalogue.ClearCovers is not { } clear) return Unavailable("Cover refresh is");
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                CoverRefreshPlan plan;
                try { plan = covers.RefreshPlan(entryId); }
                catch (KeyNotFoundException) { return Problem(404, "Entry not found"); }
                var removed = await clear(plan.Keys, context.RequestAborted).ConfigureAwait(false);
                return Answer(new JsonObject
                {
                    ["ok"] = true, ["entry_id"] = plan.EntryId, ["removed_files"] = removed,
                    ["source_manga_ids"] = new JsonArray(plan.SourceMangaIds.Select(id => (JsonNode)id).ToArray()),
                });
            }))
            .WithName("refreshEntryCovers")
            .WithSummary("Clear the cached unified cover and every active record's cover of an entry; they are fetched again on the next view.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/entries/{entryId}/refresh-metadata", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (catalogue?.MetadataRefresh is not { } refresh) return Unavailable("Metadata refresh is");
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                bool background;
                try { background = ApiQuery.Boolean(context.Request, "background", true); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (!Exists(entryId)) return Problem(404, "Entry not found");
                if (!background)
                {
                    try { return Answer(await refresh.RefreshAsync(entryId, context.RequestAborted).ConfigureAwait(false)); }
                    catch (KeyNotFoundException) { return Problem(404, "Entry not found"); }
                }
                JobHandle? handle = null;
                if (services.Jobs is { } jobs)
                    try { handle = refresh.Enqueue(jobs, entryId); }
                    catch (Exception error) when (error is ObjectDisposedException or ArgumentException) { handle = null; }
                return Queued(handle, entryId);
            }))
            .WithName("refreshEntryMetadata")
            .WithSummary("Fetch the details of every active record again (no chapters) and, while fields are still missing, look the entry up at the metadata providers. Runs as a job (202) unless background=false, which answers {entry, still_sparse}.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/metadata-providers", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (catalogue?.MetadataProviders is not { } providers) return Unavailable("Metadata providers are");
                return Answer(new JsonObject { ["items"] = providers() });
            }))
            .WithName("listMetadataProviders")
            .WithSummary("Every metadata provider with its identity scope (work or edition), priority, whether it is configured and in use, and its last rate-limit view.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/entries/{entryId}/identity/refresh", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (catalogue?.ResolveIdentity is not { } resolve) return Unavailable("Metadata identity is");
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                bool force;
                try { force = ApiQuery.Boolean(context.Request, "force", false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (!Exists(entryId)) return Problem(404, "Entry not found");
                var result = await resolve(entryId, force, context.RequestAborted).ConfigureAwait(false);
                try { result["entry"] = await Task.Run(() => catalogue.Queries.Entry(entryId), context.RequestAborted).ConfigureAwait(false); }
                catch (KeyNotFoundException) { return Problem(404, "Entry not found"); }
                return Answer(result);
            }))
            .WithName("refreshEntryIdentity")
            .WithSummary("Look the entry up at the enabled metadata providers now (force=true ignores the cached answers); a provider record is accepted only on strict native evidence. Answers the result with the entry.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/entries/{entryId}/discover-additional", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (catalogue?.DiscoverAll is not { } discover) return Unavailable("Source discovery is");
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                bool background;
                try { background = ApiQuery.Boolean(context.Request, "background", true); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (!Exists(entryId)) return Problem(404, "Entry not found");
                if (!background) return Answer(await discover(entryId, context.RequestAborted).ConfigureAwait(false));
                var handle = Enqueue(DiscoverJob, Id(entryId), "Find source records for entry " + Id(entryId), async job =>
                {
                    job.Report(0, 1, "Searching enabled sources");
                    var result = await discover(entryId, job.CancellationToken).ConfigureAwait(false);
                    job.Result("Added " + (result["primary_added"]?.ToJsonString() ?? "0") + " missing and " + (result["additional_added"]?.ToJsonString() ?? "0")
                        + " extra source records");
                });
                return Queued(handle, entryId);
            }))
            .WithName("discoverEntrySourceRecords")
            .WithSummary("Search every enabled source for the entry's missing records, then further records on its sources; found records are re-proved before they attach. Runs as a job (202) unless background=false.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/catalogue/browse", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (catalogue?.Browse is not { } browse) return Unavailable("The federated browse is");
                var query = context.Request.Query;
                var mode = (query["mode"].LastOrDefault() ?? "POPULAR").ToUpperInvariant();
                var sort = query["sort"].LastOrDefault() ?? "relevance";
                if (mode is not ("POPULAR" or "LATEST" or "SEARCH")) return Problem(422, "mode is POPULAR, LATEST or SEARCH");
                if (sort is not ("relevance" or "sources" or "title")) return Problem(422, "sort is relevance, sources or title");
                var request = new JsonObject
                {
                    ["mode"] = mode, ["q"] = query["q"].LastOrDefault() ?? "", ["sort"] = sort, ["source_id"] = query["source_id"].LastOrDefault() ?? "",
                    ["sources"] = new JsonArray(query["sources"].Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                };
                try
                {
                    request["page"] = ApiQuery.Integer(context.Request, "page", 1, 1, 100000);
                    request["min_matches"] = ApiQuery.Integer(context.Request, "min_matches", 0, 0, 1000);
                    request["match_count"] = query.ContainsKey("match_count") ? ApiQuery.Integer(context.Request, "match_count", 0, 0, 1000) : null;
                    request["page_size"] = query.ContainsKey("page_size") ? ApiQuery.Integer(context.Request, "page_size", 0, 1, 500) : null;
                    request["force_live"] = ApiQuery.Boolean(context.Request, "force_live", false);
                }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                try { return Answer(await browse(request, context.RequestAborted).ConfigureAwait(false)); }
                catch (AutoSourcarr.Core.Reader.ReaderBrowsePendingException pending)
                {
                    context.Response.Headers.RetryAfter = "5";
                    return Problem(503, pending.Message);
                }
                catch (ArgumentException error) { return Problem(422, error.Message); }
            }))
            .WithName("browseCatalogue")
            .WithSummary("The companion browser over the federated discovery pipeline the extension uses. Optional query: mode (POPULAR, LATEST, SEARCH), q, page, min_matches, match_count, sources (repeatable), sort (relevance, sources, title), page_size, force_live, source_id (one connected source).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/research-improvements/status", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (catalogue is null) return Unavailable();
                var database = catalogue.Database;
                var status = await Task.Run(() => database.Read(DatabaseFile.Catalog, s => new JsonObject
                {
                    ["due"] = new JsonArray(s.Query("SELECT * FROM due_work ORDER BY next_due,kind,work_key LIMIT 100").Select(row => (JsonNode)Row(row)).ToArray()),
                    ["pending_feed_pages"] = Convert.ToInt64(s.Scalar("SELECT COUNT(*) FROM crawl_pending"), CultureInfo.InvariantCulture),
                    ["quality_attempts"] = new JsonArray(s.Query("SELECT * FROM quality_attempts ORDER BY attempted_at DESC LIMIT 20").Select(row => (JsonNode)Row(row)).ToArray()),
                }), context.RequestAborted).ConfigureAwait(false);
                status["metrics"] = catalogue.Metrics is { } metrics ? JsonSerializer.SerializeToNode(metrics(), json.RuntimeMetricsSnapshot) : null;
                status["engine"] = catalogue.Engine?.Invoke();
                return Answer(status);
            }))
            .WithName("getResearchImprovementsStatus")
            .WithSummary("Scheduled catalogue work (due times, outcomes), feed pages still being ingested, the last 20 chapter quality attempts, runtime metrics and the source engine contract status.")
            .Produces<JsonObject>(StatusCodes.Status200OK);
    }

    private static JsonObject Row(DatabaseRow row)
    {
        var value = new JsonObject();
        foreach (var (key, cell) in row)
            value[key] = cell switch
            {
                null => null, long number => number, double real => real, string text => text, byte[] => null,
                _ => JsonValue.Create(Convert.ToString(cell, CultureInfo.InvariantCulture)),
            };
        return value;
    }
}
