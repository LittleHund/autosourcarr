using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Accounts;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Mixed;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the mixed-entry routes need from the running app (null answers 503).</summary>
public sealed class MixedEntriesV1Services
{
    public required MixedEntries Mixed { get; init; }
}

/// <summary>
/// Phase 8D mixed entries under /api/v1 (0.4.5 /api/admin/mixed-entries*): the review list from the saved findings of
/// the scaled check, one entry's live evidence with its edition profile and split proposals, the keep/clear review,
/// and the check itself as a resumable job. A review never changes source bindings or matching safeguards.
/// </summary>
internal static class MixedEntriesV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, MixedEntriesV1Services? mixed)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Mixed-entry review is not available in this host.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        static bool EntryId(HttpContext context, out long id) =>
            long.TryParse(context.Request.RouteValues["entryId"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0;
        async Task<(JsonObject? Body, IResult? Error)> Body(HttpContext context)
        {
            if (context.Request.ContentLength is null or 0 && !context.Request.Headers.ContainsKey("Transfer-Encoding")) return (new JsonObject(), null);
            try { return (await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false), null); }
            catch (ApiProblem error) { return (null, Problem(error.StatusCode, error.Message)); }
        }
        static string? Text(JsonObject body, string name) => body[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        static bool? Flag(JsonObject body, string name) => body[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

        group.MapGet("/mixed-entries", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (mixed is null) return Unavailable();
                string state; string query; int offset; int limit;
                try
                {
                    // Optional for the UI; the generated client gets the defaults (needs review, first page).
                    state = context.Request.Query["state"].LastOrDefault() ?? "needs_review";
                    query = context.Request.Query["q"].LastOrDefault() ?? "";
                    offset = ApiQuery.Integer(context.Request, "offset", 0, 0, int.MaxValue);
                    limit = ApiQuery.Integer(context.Request, "limit", 50, 1, 100);
                }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (state is not ("needs_review" or "reviewed" or "no_conflict" or "all")) return Problem(422, "state is needs_review, reviewed, no_conflict or all");
                if (query.Length > 200) return Problem(422, "The search text is too long");
                var owner = mixed.Mixed;
                return Answer(await Task.Run(() => owner.Candidates(state, query, offset, limit), context.RequestAborted).ConfigureAwait(false));
            }))
            .WithName("listMixedEntries")
            .WithSummary("Entries whose source records may be different works (different formats, color editions, titles with unrelated detailed summaries), from the saved findings of the last mixed-entry check, with counts and the check's progress. Optional query: state, q, offset, limit.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/mixed-entries/{entryId}", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (mixed is null) return Unavailable();
                if (!EntryId(context, out var entryId)) return Problem(400, "A positive entry id is required");
                var owner = mixed.Mixed;
                try { return Answer(await Task.Run(() => owner.Detail(entryId), context.RequestAborted).ConfigureAwait(false)); }
                catch (KeyNotFoundException error) { return Problem(404, error.Message); }
            }))
            .WithName("getMixedEntry")
            .WithSummary("One entry's source records with the pairwise evidence, its color/media edition profile (with every signal: title marker, page analysis, tags, source type, description) and the automatic split proposals.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/mixed-entries/{entryId}/review", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (mixed is null) return Unavailable();
                if (!EntryId(context, out var entryId)) return Problem(400, "A positive entry id is required");
                var (body, error) = await Body(context).ConfigureAwait(false);
                if (body is null) return error!;
                var fingerprint = Text(body, "fingerprint") ?? ""; var decision = Text(body, "decision") ?? "";
                if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)) return Problem(422, "fingerprint must be the 64-character evidence fingerprint");
                if (decision is not ("keep" or "clear")) return Problem(422, "decision is keep or clear");
                var owner = mixed.Mixed; var actor = Actor(context);
                try { return Answer(await Task.Run(() => owner.Review(entryId, fingerprint, decision, actor), CancellationToken.None).ConfigureAwait(false)); }
                catch (KeyNotFoundException failure) { return Problem(404, failure.Message); }
                catch (InvalidOperationException failure) { return Problem(409, failure.Message); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("reviewMixedEntry")
            .WithSummary("Keep this exact combination of source records together (decision keep) or forget that review (clear). The fingerprint must match the current evidence; nothing else changes.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/mixed-entries/check", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (mixed is null) return Unavailable();
                var (body, error) = await Body(context).ConfigureAwait(false);
                if (body is null) return error!;
                foreach (var name in new[] { "restart", "auto_split" })
                    if (body[name] is not null && Flag(body, name) is null) return Problem(400, name + " must be a boolean");
                var batch = MixedEntries.DefaultBatchSize;
                if (body["batch_size"] is not null && !(body["batch_size"] is JsonValue size && size.TryGetValue<int>(out batch) && batch is >= 1 and <= MixedEntries.MaximumBatchSize))
                    return Problem(400, "batch_size is 1 to " + MixedEntries.MaximumBatchSize.ToString(CultureInfo.InvariantCulture));
                var restart = Flag(body, "restart") == true; var autoSplit = Flag(body, "auto_split") == true;
                if (services.Jobs is not { } jobs) return Problem(503, "The job queue is not running; nothing was started.");
                JobHandle handle;
                try { handle = mixed.Mixed.EnqueueCheck(jobs, restart, autoSplit, batch); }
                catch (Exception failure) when (failure is ObjectDisposedException or ArgumentException) { return Problem(503, "The job queue is stopping; nothing was started."); }
                return Answer(new JsonObject { ["queued"] = true, ["task_id"] = handle.Id, ["merged"] = handle.Merged, ["check"] = mixed.Mixed.CheckStatus() }, 202);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("checkMixedEntries")
            .WithSummary("Start (or resume) the mixed-entry check as a job: the whole catalogue in bounded keyset batches, resumable after a restart. Body: restart (start over), auto_split (also split high-confidence automatic color/media mixtures), batch_size.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>The decision-log actor of a request: the signed-in account, the key, or the local admin when access control is off.</summary>
    internal static DecisionActor Actor(HttpContext context)
    {
        var caller = AccessControlMiddleware.CallerOf(context);
        if (caller.Role == Role.None || caller.Name.Length == 0) return DecisionActor.User("local admin");
        return caller.Via is "key" or "admin-token" or "reader-token" ? DecisionActor.Key(caller.Name) : DecisionActor.User(caller.Name);
    }
}
