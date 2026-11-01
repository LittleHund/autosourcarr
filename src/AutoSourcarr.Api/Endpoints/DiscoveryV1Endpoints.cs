using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Sources.Discovery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static AutoSourcarr.Api.Endpoints.ManualDecisionsV1Endpoints;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the catalogue-wide discovery routes need from the running app (null answers 503).</summary>
public sealed class DiscoveryV1Services
{
    public required CatalogueDiscovery Discovery { get; init; }
    /// <summary>SuwayomiLibrary.SubbedEntryIdsAsync: {ok, error, entry_ids} of the main library (force = fresh); null without a main Suwayomi.</summary>
    public Func<bool, CancellationToken, Task<JsonObject>>? SubbedEntryIds { get; init; }
}

/// <summary>
/// Phase 9 catalogue-wide jobs (moved from MaintenanceEndpoints and the Subbed routes): feed refresh, sparse-metadata
/// repair, the additional-record scan and the Subbed search-all / repair-unmatched. Each runs as one cancellable job in
/// Tasks with progress; a second request while one runs joins it (already_running). Source requests are paced by the
/// source host per host. Admin only (every /api/v1 write needs admin reach).
/// </summary>
internal static class DiscoveryV1Endpoints
{
    public static readonly JobType FeedRefreshJob = new("catalogue-feed-refresh", "Refresh browse feeds", JobPriority.Low,
        JobFlags.LongRunning, StallTimeout: TimeSpan.FromMinutes(10));
    public static readonly JobType MetadataRepairJob = new("catalogue-metadata-repair", "Repair missing metadata", JobPriority.Low,
        JobFlags.LongRunning, StallTimeout: TimeSpan.FromMinutes(10));
    public static readonly JobType AdditionalScanJob = new("catalogue-additional-records", "Find extra source records", JobPriority.Low,
        JobFlags.LongRunning, StallTimeout: TimeSpan.FromMinutes(15));
    /// <summary>One Subbed discovery at a time: search-all and repair-unmatched share the key.</summary>
    public static readonly JobType SubbedSearchJob = new("subbed-search-all", "Search all Subbed entries", JobPriority.Normal,
        JobFlags.LongRunning, StallTimeout: TimeSpan.FromMinutes(15));

    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, DiscoveryV1Services? discovery)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Catalogue discovery is not available in this host.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        static void Report(JobContext job, int? current, int? total, string? message) => job.Report(current, total, message);

        JobHandle? Enqueue(JobType type, string title, Func<JobContext, Task> work)
        {
            if (services.Jobs is not { } jobs) return null;
            try { return jobs.Enqueue(type, "", work, origin: "manual", title: title); }
            catch (Exception error) when (error is ObjectDisposedException or ArgumentException) { return null; }
        }
        async Task<(JsonObject? Body, IResult? Error)> Read(HttpContext context)
        {
            try { return (await Body(context).ConfigureAwait(false), null); }
            catch (ApiProblem error) { return (null, Problem(error.StatusCode, error.Message)); }
        }
        static int Limit(JsonObject body, int fallback)
        {
            var value = BodyLong(body, "limit");
            return value <= 0 ? fallback : (int)Math.Min(5000, value);
        }

        group.MapPost("/maintenance/refresh-feeds", (Func<HttpContext, Task<IResult>>)(context =>
            {
                if (discovery is null) return Task.FromResult(Unavailable());
                var catalogue = discovery.Discovery;
                var handle = Enqueue(FeedRefreshJob, "Refresh browse feeds", async job =>
                {
                    var result = await catalogue.RefreshFeedsAsync((current, total, message) => Report(job, current, total, message), job.CancellationToken).ConfigureAwait(false);
                    job.Result("Refreshed " + result["feeds"]?.ToJsonString() + " feeds; " + result["cataloged_entries"]?.ToJsonString() + " entries");
                });
                if (handle is null) return Task.FromResult(Problem(503, "The job queue is not available; nothing was scheduled."));
                return Task.FromResult(Answer(new JsonObject
                {
                    ["queued"] = !handle.Merged, ["already_running"] = handle.Merged, ["scheduled"] = catalogue.PlannedFeeds, ["task_id"] = handle.Id,
                }, 202));
            }))
            .WithName("refreshBrowseFeeds")
            .WithSummary("Crawl every enabled source's Popular and Latest pages (BROWSE_PREFETCH_PAGES each) into the catalogue and the saved browse feeds, as one cancellable job.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/maintenance/refresh-metadata", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (discovery is null) return Unavailable();
                var (body, error) = await Read(context).ConfigureAwait(false);
                if (body is null) return error!;
                var catalogue = discovery.Discovery;
                var ids = catalogue.SparseEntries(Limit(body, 500));
                if (ids.Count == 0) return Answer(new JsonObject { ["candidates"] = 0, ["queued"] = 0, ["task_id"] = null });
                var handle = Enqueue(MetadataRepairJob, "Repair missing metadata", async job =>
                {
                    var result = await catalogue.EnrichMetadataAsync(ids, (current, total, message) => Report(job, current, total, message), job.CancellationToken).ConfigureAwait(false);
                    var failed = result["failed"]?.GetValue<int>() ?? 0;
                    if (failed > 0 && failed == ids.Count) throw new InvalidOperationException("Metadata repair failed for every entry: " + result["first_error"]?.GetValue<string>());
                    job.Result("Metadata repair complete; " + result["improved"]?.ToJsonString() + " entries got source detail, " + failed.ToString(CultureInfo.InvariantCulture) + " failed");
                });
                if (handle is null) return Problem(503, "The job queue is not available; nothing was scheduled.");
                return Answer(new JsonObject
                {
                    ["candidates"] = ids.Count, ["queued"] = handle.Merged ? 0 : ids.Count, ["already_running"] = handle.Merged, ["task_id"] = handle.Id,
                }, 202);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("repairMissingMetadata")
            .WithSummary("Repair entries missing a cover, summary, creators, genres or status (body {limit}, default 500, at most 5000): provider identity first, then the detail of their best source records, paced, as one cancellable job.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/maintenance/discover-additional", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (discovery is null) return Unavailable();
                var (body, error) = await Read(context).ConfigureAwait(false);
                if (body is null) return error!;
                if (!services.Settings().AutoVariantDiscovery) return Answer(new JsonObject { ["scheduled"] = false, ["entries"] = 0, ["disabled"] = true });
                var catalogue = discovery.Discovery;
                var ids = catalogue.AdditionalScanEntries(Limit(body, 200));
                var handle = Enqueue(AdditionalScanJob, "Find extra source records", async job =>
                {
                    var result = await catalogue.ScanAdditionalAsync(ids, (current, total, message) => Report(job, current, total, message), job.CancellationToken).ConfigureAwait(false);
                    job.Result("Additional-record scan complete; " + result["added"]?.ToJsonString() + " record(s) added");
                });
                if (handle is null) return Problem(503, "The job queue is not available; nothing was scheduled.");
                return Answer(new JsonObject
                {
                    ["scheduled"] = !handle.Merged, ["already_running"] = handle.Merged, ["entries"] = ids.Count, ["task_id"] = handle.Id,
                }, 202);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("discoverAdditionalRecords")
            .WithSummary("Scan matched entries for missing sources and further records on their sources (body {limit}, default 200, at most 5000), as one cancellable job; found records are re-proved before they attach.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        foreach (var (path, unmatchedOnly, name, title) in new[]
        {
            ("/subbed/search-all", false, "subbedSearchAll", "Search all Subbed entries"),
            ("/subbed/repair-unmatched", true, "subbedRepairUnmatched", "Repair unmatched Subbed entries"),
        })
        {
            group.MapPost(path, (Func<HttpContext, Task<IResult>>)(async context =>
                {
                    if (discovery is null) return Unavailable();
                    if (discovery.SubbedEntryIds is not { } subbed) return Problem(503, "No main Suwayomi library is configured.");
                    JsonObject subscription;
                    try { subscription = await subbed(true, context.RequestAborted).ConfigureAwait(false); }
                    catch (Exception error) when (error is HttpRequestException or TimeoutException or InvalidOperationException or InvalidDataException)
                    { return Problem(503, "Unable to load Subbed library: " + error.Message); }
                    if (!BodyFlag(subscription, "ok"))
                        return Problem(503, "Unable to load Subbed library: " + (BodyText(subscription, "error") is { Length: > 0 } reason ? reason : "main Suwayomi unavailable"));
                    var entries = (subscription["entry_ids"] as JsonArray ?? new JsonArray()).OfType<JsonValue>()
                        .Select(value => value.TryGetValue<long>(out var id) ? id : 0).Where(id => id > 0);
                    var catalogue = discovery.Discovery;
                    var ids = catalogue.SubbedOrder(entries, unmatchedOnly);
                    var handle = Enqueue(SubbedSearchJob, title, async job =>
                    {
                        var result = await catalogue.SearchSubbedAsync(ids, unmatchedOnly, (current, total, message) => Report(job, current, total, message), job.CancellationToken).ConfigureAwait(false);
                        job.Result(result["message"]?.GetValue<string>() ?? "Done");
                    });
                    if (handle is null) return Problem(503, "The job queue is not available; nothing was scheduled.");
                    return Answer(new JsonObject
                    {
                        ["queued"] = !handle.Merged, ["already_running"] = handle.Merged, ["entries"] = ids.Count, ["task_id"] = handle.Id,
                    }, 202);
                }))
                .WithName(name)
                .WithSummary(unmatchedOnly
                    ? "Search every enabled source for the subscribed entries that have no source record yet (stops per entry at the first verified record), as one cancellable job; a retry visits the sources not reached first."
                    : "Search every enabled source for every subscribed entry (source-less entries first) for missing and additional records, as one cancellable job; a retry visits the sources not reached first.")
                .Produces<JsonObject>(StatusCodes.Status202Accepted)
                .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
