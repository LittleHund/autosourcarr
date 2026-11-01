using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Catalogue.Tags;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static AutoSourcarr.Api.Endpoints.ManualDecisionsV1Endpoints;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the tag routes need from the running app (null answers 503).</summary>
public sealed class TagsV1Services
{
    public required TagCatalogue Tags { get; init; }
    public required NativeTagStore Store { get; init; }
}

/// <summary>
/// Phase 10 tags (0.4.5 TagEndpoints): the indexed source tags with their equivalence groups, spelling suggestions, the
/// reviewed pairs, a Same/Separate/Clear review (admin, recorded in the decision log with the caller; a review never
/// merges entries), the saved-record reindex job, and the tags of one entry.
/// </summary>
internal static class TagsV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, TagsV1Services? tags)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Tags are not available in this host.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);

        group.MapGet("/tags", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (tags is null) return Unavailable();
                int limit, offset;
                try
                {
                    limit = ApiQuery.Integer(context.Request, "limit", 100, 1, 200);
                    offset = ApiQuery.Integer(context.Request, "offset", 0, 0, int.MaxValue);
                }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                var search = context.Request.Query["search"].LastOrDefault() ?? "";
                if (search.Length > 200) return Problem(422, "The search text is too long");
                var catalogue = tags.Tags;
                return Answer(await Task.Run(() => catalogue.List(search, limit, offset), context.RequestAborted).ConfigureAwait(false));
            }))
            .WithName("listTags")
            .WithSummary("One page of indexed source tags with their canonical tag, equivalent aliases and how many sources and entries use them. Optional query: search (namespace and name), limit (≤ 200), offset.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/tags/suggestions", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (tags is null) return Unavailable();
                var catalogue = tags.Tags;
                return Answer(await Task.Run(() => catalogue.Suggestions(), context.RequestAborted).ConfigureAwait(false));
            }))
            .WithName("listTagSuggestions")
            .WithSummary("Tags in one namespace with a similar spelling that are not yet reviewed or equivalent, best first; review them before treating them as the same.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapGet("/tags/reviews", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (tags is null) return Unavailable();
                var catalogue = tags.Tags;
                return Answer(await Task.Run(() => catalogue.Reviews(), context.RequestAborted).ConfigureAwait(false));
            }))
            .WithName("listTagReviews")
            .WithSummary("Every reviewed tag pair (same or separate), newest first.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/tags/review", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (tags is null) return Unavailable();
                JsonObject body;
                try { body = await Body(context).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                var left = BodyLong(body, "left_id"); var right = BodyLong(body, "right_id"); var decision = BodyText(body, "decision");
                if (left <= 0 || right <= 0) return Problem(422, "left_id and right_id must be tag ids");
                if (decision is not ("same" or "different" or "clear")) return Problem(422, "decision is same, different or clear");
                var actor = Actor(context);
                var catalogue = tags.Tags;
                try { return Answer(await Task.Run(() => catalogue.Review(left, right, decision, actor), context.RequestAborted).ConfigureAwait(false)); }
                catch (TagReviewException error) { return Problem(409, error.Message); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("reviewTags")
            .WithSummary("Review two tags, body {left_id, right_id, decision}: same (one namespace only), different (kept separate) or clear. Tag equivalence is matching evidence only and never merges entries. Recorded with the caller as actor.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/tags/reindex", (Func<HttpContext, IResult>)(_ =>
            {
                if (tags is null) return Unavailable();
                if (services.Jobs is not { } jobs) return Problem(503, "The job queue is not available; nothing was scheduled.");
                try { return Answer(tags.Tags.QueueReindex(jobs, tags.Store), 202); }
                catch (Exception error) when (error is ObjectDisposedException or ArgumentException)
                { return Problem(503, "The job queue is not available; nothing was scheduled."); }
            }))
            .WithName("reindexTags")
            .WithSummary("Index the tags of every active source record again from its saved metadata (no website requests), as one job; a second request joins the running one.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/entries/{entryId}/tags", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (tags is null) return Unavailable();
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                var catalogue = tags.Tags; var store = tags.Store;
                try { return Answer(await Task.Run(() => catalogue.EntryTags(store, entryId), context.RequestAborted).ConfigureAwait(false)); }
                catch (KeyNotFoundException) { return Problem(404, "Entry not found"); }
            }))
            .WithName("getEntryTags")
            .WithSummary("The source tags and gallery metadata of an entry's active records; metadata_basis says whether gallery source metadata exists.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);
    }
}
