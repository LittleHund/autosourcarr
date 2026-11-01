using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the library and download routes need from the running app (null answers 503).</summary>
public sealed class LibraryDownloadsV1Services
{
    public required LibraryManager Manager { get; init; }
}

/// <summary>
/// Phase 10 CBZ library (CHAP-7) under /api/v1, ported from reimagined_api.py (0.4.5 /api/admin/library...,
/// /api/admin/downloads, /api/admin/activity/history): the library page, per-work controls (membership, monitoring,
/// observe, download missing), the download queue and its controls, and the activity history. All routes are
/// admin-only (the /api/v1 group filter); every change records its actor in the decision log. The library page keeps
/// its filters in the query string (as in 0.4.5); the other routes take their input in the body.
/// </summary>
internal static class LibraryDownloadsV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, LibraryDownloadsV1Services? library)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Answer(JsonObject value) => Results.Json(value, json.JsonObject);
        async Task<IResult> Guard(HttpContext context, bool body, Func<LibraryManager, JsonObject, Task<IResult>> action)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (library is null) return Problem(503, "The library is not available in this host.");
            try
            {
                var request = body ? await ManualDecisionsV1Endpoints.Body(context).ConfigureAwait(false) : new JsonObject();
                return await action(library.Manager, request).ConfigureAwait(false);
            }
            catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
            catch (KeyNotFoundException error) { return Problem(404, error.Message); }
            catch (LibraryConflictException error) { return Problem(409, error.Message); }
            catch (ArgumentException error) { return Problem(400, error.Message); }
        }
        long EntryId(HttpContext context) =>
            ManualDecisionsV1Endpoints.TryRouteLong(context, "entryId", out var id) ? id : throw new ApiProblem(400, "Invalid work id");

        group.MapGet("/library", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (manager, _) =>
            {
                var query = context.Request.Query;
                string Value(string name, string fallback = "") => query[name].LastOrDefault() is { } value ? value : fallback;
                var limit = int.TryParse(Value("limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 60;
                if (limit is < 1 or > 120) throw new ApiProblem(400, "limit must be between 1 and 120");
                foreach (var name in new[] { "search", "source", "cursor" })
                    if (Value(name).Length > (name == "cursor" ? 2048 : 200)) throw new ApiProblem(400, name + " is too long");
                var request = new LibraryQuery
                {
                    Search = Value("search"), Source = Value("source"), Scope = Value("scope", "all"), Sort = Value("sort", "updated"),
                    Publication = Value("publication", "any"), TitleState = Value("title_state", "any"), MatchStatus = Value("match_status", "any"),
                    SourceCount = Value("source_count"), RecordCount = Value("record_count"), Cursor = Value("cursor"), Limit = limit,
                };
                return Task.FromResult(Answer(manager.Page(request)));
            })))
            .WithName("getLibraryPage")
            .WithSummary("AutoSourcarr's own library, filtered and paged (query: search, source, scope = all | catalog | missing_downloads | source_gaps | unmatched | verification | monitored, sort = title | updated | matches, publication, title_state, match_status, source_count, record_count, cursor, limit 1-120).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapGet("/library/{entryId}/controls", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (manager, _) =>
                Task.FromResult(Answer(manager.Controls(EntryId(context)))))))
            .WithName("getLibraryControls")
            .WithSummary("A work's library controls: membership, series folder, monitoring, revision, the last observation and its monitoring clock.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPut("/library/{entryId}/membership", (Func<HttpContext, Task<IResult>>)(context => Guard(context, true, async (manager, body) =>
                Answer(await manager.SetMembershipAsync(EntryId(context), Flag(body, "in_library", required: true), Flag(body, "monitored"), Revision(body),
                    ManualDecisionsV1Endpoints.Actor(context), context.RequestAborted).ConfigureAwait(false)))))
            .Accepts<JsonObject>("application/json")
            .WithName("setLibraryMembership")
            .WithSummary("Add a work to the library or remove it ({revision, in_library, monitored}). Removing keeps downloaded chapters and drops only queued ones.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPut("/library/{entryId}/monitoring", (Func<HttpContext, Task<IResult>>)(context => Guard(context, true, async (manager, body) =>
                Answer(await manager.SetMonitoringAsync(EntryId(context), Flag(body, "monitored", required: true), Revision(body),
                    ManualDecisionsV1Endpoints.Actor(context), context.RequestAborted).ConfigureAwait(false)))))
            .Accepts<JsonObject>("application/json")
            .WithName("setLibraryMonitoring")
            .WithSummary("Turn monitoring of new chapters on or off ({revision, monitored}); a monitored work downloads new chapters automatically.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/library/{entryId}/observe", (Func<HttpContext, Task<IResult>>)(context => Guard(context, true, async (manager, body) =>
            {
                var refresh = Flag(body, "refresh") || string.Equals(context.Request.Query["refresh"].LastOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
                return Answer(await manager.ObserveAsync(EntryId(context), refresh, context.RequestAborted).ConfigureAwait(false));
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("observeLibraryEntry")
            .WithSummary("Check which chapters are downloaded ({refresh: true} first fetches the source chapter lists again). Read-only otherwise; safe to repeat.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/library/{entryId}/download", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, async (manager, _) =>
                Answer(await manager.EnqueueMissingAsync(EntryId(context), ManualDecisionsV1Endpoints.Actor(context), context.RequestAborted).ConfigureAwait(false)))))
            .WithName("downloadLibraryEntry")
            .WithSummary("Queue every chapter of the work that has no CBZ yet (failed ones are retried); answers queued and remaining.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapGet("/downloads", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (manager, _) =>
                Task.FromResult(Answer(manager.Queue())))))
            .WithName("getDownloadQueue")
            .WithSummary("The download queue: state (STARTED or STOPPED), queued chapters in order with source, progress and errors, and its revision.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/downloads", (Func<HttpContext, Task<IResult>>)(context => Guard(context, true, async (manager, body) =>
            {
                var action = ManualDecisionsV1Endpoints.BodyText(body, "action");
                var revision = ManualDecisionsV1Endpoints.BodyText(body, "revision");
                if (action.Length is 0 or > 20 || revision.Length is 0 or > 64) throw new ApiProblem(400, "Send the action and the queue revision.");
                int? to = body["to"] is JsonValue position && position.TryGetValue<int>(out var index) ? index : null;
                return Answer(await manager.QueueActionAsync(action, revision, ManualDecisionsV1Endpoints.BodyLong(body, "chapter_id"), to,
                    ManualDecisionsV1Endpoints.Actor(context), context.RequestAborted).ConfigureAwait(false));
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("changeDownloadQueue")
            .WithSummary("Control the queue against its revision: {action: start | stop | remove | move | retry, revision, chapter_id, to}.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapGet("/activity/history", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (manager, _) =>
            {
                var query = context.Request.Query;
                var offset = int.TryParse(query["offset"].LastOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var start) ? start : 0;
                var limit = int.TryParse(query["limit"].LastOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ? size : 50;
                if (offset is < 0 or > 300 || limit is < 1 or > 100) throw new ApiProblem(400, "offset 0-300 and limit 1-100");
                var result = services.Tasks?.History(offset, limit) ?? new JsonObject { ["items"] = new JsonArray(), ["total"] = 0, ["offset"] = offset, ["limit"] = limit, ["note"] = "No task history in this host." };
                result["downloads"] = manager.Store.DownloadHistory(0, 50);
                return Task.FromResult(Answer(result));
            })))
            .WithName("getActivityHistory")
            .WithSummary("Finished general tasks (up to 300, seven days; query offset and limit) plus the 50 newest download events (downloaded, failed, removed).")
            .Produces<JsonObject>(StatusCodes.Status200OK);
    }

    private static long Revision(JsonObject body) => body["revision"] is JsonValue value && value.TryGetValue<long>(out var revision) && revision >= 0
        ? revision : throw new ApiProblem(400, "Send the revision of the controls you saw.");

    private static bool Flag(JsonObject body, string name, bool required = false)
    {
        if (body[name] is JsonValue value && value.TryGetValue<bool>(out var flag)) return flag;
        if (required) throw new ApiProblem(400, "Send " + name + " as true or false.");
        return false;
    }
}
