using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Accounts;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Manual;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the manual-decision and decision-log routes need (null answers 503).</summary>
public sealed class ManualDecisionsV1Services
{
    public required ManualDecisions Manual { get; init; }
    public required IDecisionLogReader Log { get; init; }
    /// <summary>The entry payload (CatalogueQueries.Entry) answered after a match, separate or title change, as 0.4.5 did.</summary>
    public Func<long, JsonObject>? Entry { get; init; }
}

/// <summary>
/// Phase 8 manual decisions under /api/v1 (0.4.5 /api/admin/manual-actions, manual-groups, entries/{id}/manual-match,
/// separate-source, manual-titles) and the decision log (/decisions). Every change records its actor from the caller.
/// </summary>
internal static class ManualDecisionsV1Endpoints
{
    /// <summary>0.4.5 _manual_task_slot: manual hydrations run at most two at a time, ahead of automatic work.</summary>
    public static readonly JobType ManualHydrationJob = new("manual-match-hydration", "Load manual match details", JobPriority.High,
        RunLimit: 2, StallTimeout: TimeSpan.FromMinutes(5));

    /// <summary>The decision actor of a request: the signed-in account, the API key, or the admin token.</summary>
    internal static DecisionActor Actor(HttpContext context)
    {
        var caller = AccessControlMiddleware.CallerOf(context);
        return caller.Via switch
        {
            "key" => DecisionActor.Key(caller.Name.Length > 0 ? caller.Name : caller.KeyId ?? "key"),
            "admin-token" => DecisionActor.Key("admin-token"),
            "none" => DecisionActor.User("admin"),
            _ => DecisionActor.User(caller.Name.Length > 0 ? caller.Name : Roles.Name(caller.Role)),
        };
    }

    internal static async Task<JsonObject> Body(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength is 0 || request.ContentLength is null && !request.Headers.ContainsKey("Transfer-Encoding")) return new JsonObject();
        return await ApiJson.ReadObjectAsync(request, 262144).ConfigureAwait(false);
    }

    internal static string Route(HttpContext context, string name) => context.Request.RouteValues[name]?.ToString() ?? "";

    internal static bool TryRouteLong(HttpContext context, string name, out long value) =>
        long.TryParse(Route(context, name), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    internal static string BodyText(JsonObject body, string name) => body[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";

    internal static long BodyLong(JsonObject body, string name) => body[name] is JsonValue value
        ? value.TryGetValue<long>(out var number) ? number
        : value.TryGetValue<string>(out var text) && long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0
        : 0;

    internal static bool BodyFlag(JsonObject body, string name) => body[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, ManualDecisionsV1Services? manual)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Manual decisions are not available in this host.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        // 0.4.5 answered the changed entry itself with the decision's fields added.
        JsonObject WithEntry(JsonObject result, string key, bool nest)
        {
            if (manual?.Entry is not { } view || result[key] is not JsonValue id || !id.TryGetValue<long>(out var entryId)) return result;
            JsonObject entry;
            try { entry = view(entryId); }
            catch (KeyNotFoundException) { return result; }
            if (nest) { result["entry"] = entry; return result; }
            foreach (var (name, value) in result.ToArray()) if (!entry.ContainsKey(name)) entry[name] = value?.DeepClone();
            return entry;
        }
        IResult Guard(Func<IResult> action)
        {
            try { return action(); }
            catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
            catch (KeyNotFoundException error) { return Problem(404, error.Message); }
            catch (ManualDecisionUnavailableException error) { return Problem(503, error.Message); }
            catch (ManualDecisionConflictException error) { return Problem(409, error.Message); }
            catch (ArgumentException error) { return Problem(400, error.Message); }
        }

        group.MapGet("/manual-actions", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return manual is null ? Unavailable() : Guard(() => Answer(manual.Manual.ManualActionsPayload()));
            }))
            .WithName("getManualActions")
            .WithSummary("Every saved user decision by kind: manual matches and groups, manual titles with their frozen record scope, separate-entry rules, rejected chapters and different-work reviews.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/manual-groups", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return manual is null ? Unavailable() : Guard(() => Answer(manual.Manual.ManualGroupsPayload()));
            }))
            .WithName("getManualGroups")
            .WithSummary("Persistent manual groups (two or more chosen records kept on one entry) and the individual manual matches.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/manual-groups/{groupId}/candidates", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (manual is null) return Unavailable();
                JsonObject body;
                try { body = await Body(context).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                var limit = BodyLong(body, "limit");
                return Guard(() => Answer(manual.Manual.ManualGroupCandidates(Route(context, "groupId"), BodyText(body, "q"), limit > 0 ? (int)Math.Min(limit, 100) : 60)));
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("searchManualGroupCandidates")
            .WithSummary("Catalogue records that could join a manual group: body {q, limit (1-100, default 60)}; best title match first.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/manual-groups/{groupId}/members", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (manual is null) return Unavailable();
                JsonObject body;
                try { body = await Body(context).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                var sourceId = BodyText(body, "source_id"); var mangaId = BodyLong(body, "source_manga_id");
                if (sourceId.Length == 0 || mangaId <= 0) return Problem(400, "Send source_id and source_manga_id.");
                return Guard(() => Answer(manual.Manual.AddManualGroupMember(Route(context, "groupId"), sourceId, mangaId, Actor(context))));
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("addManualGroupMember")
            .WithSummary("Add an attached source record to a manual group: its entry is merged into the group's entry and the record is bound there.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapDelete("/manual-groups/{groupId}/members/{sourceId}/{sourceMangaId}", (Func<HttpContext, IResult>)(context =>
            {
                if (manual is null) return Unavailable();
                if (!TryRouteLong(context, "sourceMangaId", out var mangaId)) return Problem(400, "Invalid source manga id.");
                return Guard(() => Answer(manual.Manual.RemoveManualGroupMember(Route(context, "groupId"), Route(context, "sourceId"), mangaId, Actor(context))));
            }))
            .WithName("removeManualGroupMember")
            .WithSummary("Take a record out of its manual group; it stays attached but later rebuilds no longer keep it in the group.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/entries/{entryId}/separate-source", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (manual is null) return Unavailable();
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                JsonObject body;
                try { body = await Body(context).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                var sourceId = BodyText(body, "source_id"); var mangaId = BodyLong(body, "source_manga_id");
                if (sourceId.Length == 0 || mangaId <= 0) return Problem(400, "Send source_id and source_manga_id.");
                try
                {
                    var result = await manual.Manual.SeparateSourceRecordAsync(entryId, sourceId, mangaId, body["candidate"] as JsonObject, Actor(context), context.RequestAborted)
                        .ConfigureAwait(false);
                    return Answer(WithEntry(result, "entry_id", nest: true));
                }
                catch (KeyNotFoundException error) { return Problem(404, error.Message); }
                catch (ManualDecisionConflictException error) { return Problem(409, error.Message); }
                catch (ManualDecisionUnavailableException error) { return Problem(503, error.Message); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("separateSourceRecord")
            .WithSummary("Keep a source record as its own entry and remember it (body {source_id, source_manga_id, candidate}); the two entries are marked as different works.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapDelete("/entries/{entryId}/separate-source/{sourceId}/{sourceMangaId}", (Func<HttpContext, IResult>)(context =>
            {
                if (manual is null) return Unavailable();
                if (!TryRouteLong(context, "entryId", out var entryId) || !TryRouteLong(context, "sourceMangaId", out var mangaId)) return Problem(400, "Invalid entry or source manga id.");
                return Guard(() => Answer(manual.Manual.AllowSourceRecordMatch(entryId, Route(context, "sourceId"), mangaId, Actor(context))));
            }))
            .WithName("allowSourceRecordMatch")
            .WithSummary("Forget a separate-entry decision, so matching may attach the record to this entry again.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/entries/{entryId}/manual-match", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (manual is null) return Unavailable();
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                JsonObject body;
                try { body = await Body(context).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                var sourceId = BodyText(body, "source_id"); var mangaId = BodyLong(body, "source_manga_id");
                var mode = BodyText(body, "mode") is { Length: > 0 } chosen ? chosen : "replace";
                if (sourceId.Length == 0 || mangaId <= 0) return Problem(400, "Send source_id and source_manga_id.");
                JsonObject result;
                try
                {
                    result = await manual.Manual.ManualMatchAsync(entryId, sourceId, mangaId, mode, body["candidate"] as JsonObject, BodyFlag(body, "defer_hydration"),
                        Actor(context), context.RequestAborted).ConfigureAwait(false);
                }
                catch (KeyNotFoundException error) { return Problem(404, error.Message); }
                catch (ManualDecisionConflictException error) { return Problem(409, error.Message); }
                catch (ManualDecisionUnavailableException error) { return Problem(503, error.Message); }
                catch (ArgumentException error) { return Problem(400, error.Message); }
                if (ManualDecisions.NeedsHydration(result) && services.Jobs is { } jobs)
                {
                    var decisions = manual.Manual; var target = result["entry_id"]!.GetValue<long>();
                    try
                    {
                        var handle = jobs.Enqueue(ManualHydrationJob, sourceId + ":" + mangaId.ToString(CultureInfo.InvariantCulture), async job =>
                        {
                            job.Report(message: "Loading details and chapters of the chosen record");
                            var stored = await decisions.HydrateManualMatchAsync(target, sourceId, mangaId, job.CancellationToken).ConfigureAwait(false);
                            job.Result(stored ? "Manual match details loaded" : "The match changed meanwhile; nothing was loaded");
                        }, origin: "manual", context: target.ToString(CultureInfo.InvariantCulture));
                        result["hydration_task_id"] = handle.Id;
                    }
                    catch (Exception error) when (error is ObjectDisposedException or ArgumentException) { result["hydration_task_id"] = null; }
                }
                return Answer(WithEntry(result, "entry_id", nest: false));
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("manualMatch")
            .WithSummary("Bind the exact source record the user chose to the entry: body {source_id, source_manga_id, mode replace|add, candidate, defer_hydration}. With defer_hydration and the search card the choice commits at once and details load as a job.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapDelete("/entries/{entryId}/manual-titles", (Func<HttpContext, IResult>)(context =>
            {
                if (manual is null) return Unavailable();
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                return Guard(() => Answer(WithEntry(manual.Manual.ClearManualTitles(entryId, Actor(context)), "entry_id", nest: false)));
            }))
            .WithName("clearManualTitles")
            .WithSummary("Remove the manual main title and manual alternate titles of an entry; its main title returns to the most-used title of its records.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        IResult Decisions(DecisionLogQuery query) => manual is null ? Unavailable() : Answer(manual.Log.Read(query).ToJson());

        group.MapGet("/decisions", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Decisions(new DecisionLogQuery());
            }))
            .WithName("getDecisions")
            .WithSummary("The newest 50 decisions of the decision log (merges, splits, separates, manual matches and groups, titles, reviews, resets) with their actor.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/decisions", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                JsonObject body;
                try { body = await Body(context).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                var entry = BodyLong(body, "entry_id"); var before = BodyLong(body, "before"); var limit = BodyLong(body, "limit");
                return Decisions(new DecisionLogQuery(BodyText(body, "kind"), entry > 0 ? entry : null, BodyText(body, "actor_kind"), BodyText(body, "actor_name"),
                    BodyText(body, "record"), before > 0 ? before : null, limit > 0 ? (int)Math.Min(limit, 500) : 50));
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("queryDecisions")
            .WithSummary("Filter the decision log: body {kind (also matches dotted sub-kinds), entry_id, actor_kind, actor_name, record \"source_id:source_manga_id\", before (next_before of the previous page), limit (1-500)}.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapGet("/decisions/{eventId}", (Func<HttpContext, IResult>)(context =>
            {
                if (manual is null) return Unavailable();
                if (!TryRouteLong(context, "eventId", out var id)) return Problem(400, "Invalid decision id.");
                return manual.Log.Get(id) is { } entry ? Answer(entry.ToJson()) : Problem(404, "Decision not found");
            }))
            .WithName("getDecision")
            .WithSummary("One decision with its before/after state.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);
    }
}
