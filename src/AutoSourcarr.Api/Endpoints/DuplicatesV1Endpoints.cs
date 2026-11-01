using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Accounts;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Duplicates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>
/// Phase 8C duplicate review and automatic merging under /api/v1 (0.4.5 /api/admin/duplicates*, /duplicate-candidates,
/// /maintenance/consolidate*, /maintenance/unmatch-duplicates and the entry unmatch / unbind / redo actions).
/// Long work (automatic sweep, consolidation, Redo) runs as jobs; every change is written to the decision log with the caller.
/// </summary>
internal static class DuplicatesV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, DuplicateMatching? duplicates)
    {
        if (duplicates is not null && services.Jobs is not null) duplicates.Jobs ??= services.Jobs;
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Duplicate review is not available in this host.");
        IResult Answer(JsonNode value, int status = 200) => Results.Json(value is JsonObject obj ? obj : new JsonObject { ["items"] = value }, json.JsonObject, statusCode: status);
        static bool EntryId(HttpContext context, out long id) =>
            long.TryParse(context.Request.RouteValues["entryId"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0;
        async Task<(JsonObject? Body, IResult? Error)> Body(HttpContext context, int maximum = 65536)
        {
            if (context.Request.ContentLength is null or 0 && !context.Request.Headers.ContainsKey("Transfer-Encoding")) return (new JsonObject(), null);
            try { return (await ApiJson.ReadObjectAsync(context.Request, maximum).ConfigureAwait(false), null); }
            catch (ApiProblem error) { return (null, Problem(error.StatusCode, error.Message)); }
        }
        static long[]? Ids(JsonNode? node)
        {
            if (node is not JsonArray array || array.Count > 500) return null;
            var result = new List<long>();
            foreach (var item in array)
            {
                if (item is not JsonValue value) return null;
                long id;
                if (value.TryGetValue<long>(out var number)) id = number;
                else if (value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) id = parsed;
                else return null;
                if (id > 0 && !result.Contains(id)) result.Add(id);
            }
            return result.ToArray();
        }
        JsonObject Cached(DuplicateMatching owner, long[] ids)
        {
            if (ids.Length == 0) return new JsonObject { ["duplicates"] = new JsonObject(), ["pending"] = false };
            var map = owner.CandidatesCached(ids);
            var summary = owner.SnapshotSummary();
            if (map is null) { owner.ScheduleSnapshotRefresh(); return new JsonObject { ["duplicates"] = new JsonObject(), ["pending"] = true }; }
            var stale = summary["stale"] is JsonValue flag && flag.TryGetValue<bool>(out var isStale) && isStale;
            if (stale) owner.ScheduleSnapshotRefresh();
            return new JsonObject { ["duplicates"] = map, ["pending"] = stale };
        }

        group.MapGet("/duplicates", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (duplicates is null) return Unavailable();
                var result = await Task.Run(() =>
                {
                    var groups = duplicates.GroupsCached(); var summary = duplicates.SnapshotSummary();
                    var stale = summary["stale"] is JsonValue flag && flag.TryGetValue<bool>(out var isStale) && isStale;
                    if (groups is null || stale) duplicates.ScheduleSnapshotRefresh();
                    return new JsonObject
                    {
                        ["groups"] = groups ?? new JsonArray(), ["pending"] = groups is null || stale, ["stale"] = groups is not null && stale,
                        ["subbed_entry_ids"] = new JsonArray(), ["subscription_ok"] = null, ["subscription_pending"] = false, ["subscription_error"] = "",
                        ["automatic_matching"] = duplicates.AutomaticStatus(),
                    };
                }, context.RequestAborted).ConfigureAwait(false);
                return Answer(result);
            }))
            .WithName("listDuplicates")
            .WithSummary("The duplicate review snapshot: groups of entries that may be one work, each pair with title/merge scores and whether the automatic gate would merge it; pending/stale while a refresh job runs, plus the automatic matching status.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/duplicates/automatic", (Func<HttpContext, IResult>)(context =>
            {
                if (duplicates is null) return Unavailable();
                if (duplicates.Jobs is null) return Problem(503, "The job queue is not running; nothing was started.");
                var handle = duplicates.EnqueueAutomatic(TimeSpan.Zero, "manual");
                return Answer(new JsonObject
                {
                    ["queued"] = handle is { Merged: false }, ["already_running"] = handle is { Merged: true }, ["task_id"] = handle?.Id,
                    ["paused"] = duplicates.Paused, ["status"] = duplicates.AutomaticStatus(),
                }, handle is null ? 200 : 202);
            }))
            .WithName("runAutomaticDuplicates")
            .WithSummary("Run automatic duplicate matching now as a job: merge only pairs whose every original member passes the automatic gate (non-transitive); reviewed and separated pairs are never merged.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/duplicates/review", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (duplicates is null) return Unavailable();
                var (body, error) = await Body(context).ConfigureAwait(false);
                if (error is not null) return error;
                long Number(string name) => body![name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
                var left = Number("left_entry_id"); var right = Number("right_entry_id");
                var decision = body!["decision"] is JsonValue d && d.TryGetValue<string>(out var text) ? text : "";
                if (left <= 0 || right <= 0) return Problem(400, "left_entry_id and right_entry_id are required");
                if (decision is not ("duplicate" or "not_duplicate" or "clear")) return Problem(400, "decision must be duplicate, not_duplicate or clear");
                var actor = Actor(context);
                try
                {
                    var result = await Task.Run(() => duplicates.Review(left, right, decision, actor), CancellationToken.None).ConfigureAwait(false);
                    duplicates.ScheduleSnapshotRefresh();
                    return Answer(result);
                }
                catch (KeyNotFoundException)
                {
                    // A stale page can race with a background merge: answer with the current queue instead of failing.
                    var groups = duplicates.GroupsCached();
                    return Answer(new JsonObject { ["decision"] = "stale", ["groups"] = groups ?? new JsonArray(), ["pending"] = groups is null });
                }
                catch (InvalidOperationException failure) { return Problem(409, failure.Message); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("reviewDuplicate")
            .WithSummary("Review a duplicate pair (left_entry_id, right_entry_id, decision): duplicate merges them and keeps the records together as a manual group, not_duplicate keeps them apart permanently, clear forgets the decision.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapGet("/duplicate-candidates", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (duplicates is null) return Unavailable();
                var raw = context.Request.Query["ids"].ToString();
                var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length > 500) return Problem(400, "ids must be at most 500 entry IDs");
                var ids = parts.Select(part => long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(id => id > 0).Distinct().ToArray();
                return Answer(Cached(duplicates, ids));
            }))
            .WithName("getDuplicateCandidates")
            .WithSummary("Duplicate review badges for entries (?ids=1,2,...; at most 500) from the completed snapshot, never rescoring; pending while a refresh runs.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest);

        group.MapPost("/duplicate-candidates", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (duplicates is null) return Unavailable();
                var (body, error) = await Body(context).ConfigureAwait(false);
                if (error is not null) return error;
                var ids = Ids(body!["ids"]);
                if (ids is null) return Problem(400, "ids must be an array of at most 500 entry IDs");
                return Answer(Cached(duplicates, ids));
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("postDuplicateCandidates")
            .WithSummary("Duplicate review badges for the entries in {\"ids\": [...]} (at most 500) from the completed snapshot, never rescoring.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/duplicates/history", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (duplicates is null) return Unavailable();
                static int Query(HttpContext context, string name, int fallback) => int.TryParse(context.Request.Query[name].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
                var offset = Query(context, "offset", 0); var limit = Query(context, "limit", 50); var query = context.Request.Query["q"].ToString();
                if (offset is < 0 or > 1_000_000 || limit is < 1 or > 100 || query.Length > 200) return Problem(422, "offset 0-1000000, limit 1-100 and q up to 200 characters");
                return Answer(duplicates.History(offset, limit, query));
            }))
            .WithName("listDuplicateHistory")
            .WithSummary("Recorded automatic merges, newest first (offset, limit, q); older merges that kept only a reason are listed as legacy.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/duplicates/history/{eventId}", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (duplicates is null) return Unavailable();
                if (!long.TryParse(context.Request.RouteValues["eventId"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return Problem(400, "A numeric event id is required");
                try { return Answer(duplicates.HistoryDetail(id)); }
                catch (KeyNotFoundException) { return Problem(404, "Merge event not found"); }
            }))
            .WithName("getDuplicateHistoryEvent")
            .WithSummary("One recorded merge: both entries as they were before it, its evidence, and the entry it lives on now.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/maintenance/consolidate-preview", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (duplicates is null) return Unavailable();
                return Answer(await Task.Run(() => duplicates.AutoMergePreview(10000, context.RequestAborted), context.RequestAborted).ConfigureAwait(false));
            }))
            .WithName("previewConsolidateDuplicates")
            .WithSummary("Exactly which duplicate pairs the automatic gate would merge now (evidence only; the merge re-checks every pair).")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/maintenance/consolidate", (Func<HttpContext, IResult>)(context =>
            {
                if (duplicates is null) return Unavailable();
                if (services.Jobs is not { } jobs) return Problem(503, "The job queue is not running; nothing was started.");
                var actor = Actor(context);
                JobHandle handle;
                try
                {
                    handle = jobs.Enqueue(DuplicateMatching.ConsolidateJob, "catalogue", job =>
                    {
                        var result = duplicates.RepairCatalogDuplicates(actor, 10000, (current, total, message) => job.Report(current, total, message), job.CancellationToken);
                        var merged = result["merged"] is JsonValue count && count.TryGetValue<int>(out var number) ? number : 0;
                        job.Result("Merged " + merged.ToString(CultureInfo.InvariantCulture) + " duplicate entr" + (merged == 1 ? "y" : "ies"));
                        duplicates.ScheduleSnapshotRefresh();
                        return Task.CompletedTask;
                    }, origin: "manual", title: "Auto-merge duplicates");
                }
                catch (Exception failure) when (failure is ObjectDisposedException or ArgumentException)
                { return Problem(503, "The job queue is stopping; nothing was started."); }
                return Answer(new JsonObject { ["queued"] = !handle.Merged, ["already_running"] = handle.Merged, ["task_id"] = handle.Id }, 202);
            }))
            .WithName("consolidateDuplicates")
            .WithSummary("Auto-merge duplicates across the catalogue as a job: recovers pending chapter work, then merges only pairs proven across every original member.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/maintenance/unmatch-duplicates", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (duplicates is null) return Unavailable();
                return Answer(new JsonObject { ["items"] = await Task.Run(() => duplicates.UnmatchCandidates(), context.RequestAborted).ConfigureAwait(false) });
            }))
            .WithName("listUnmatchDuplicates")
            .WithSummary("Merged entries whose source records can be split apart again, with every record and its match type.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/entries/{entryId}/unmatch-duplicates", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (duplicates is null) return Unavailable();
                if (!EntryId(context, out var entryId)) return Problem(400, "A numeric entry id is required");
                var (body, error) = await Body(context).ConfigureAwait(false);
                if (error is not null) return error;
                if (body!["records"] is not JsonArray items || items.Count == 0 || items.Count > 500) return Problem(400, "records must list 1-500 {source_id, source_manga_id}");
                var records = new List<NativeSourceRecord>();
                foreach (var item in items)
                {
                    if (item is not JsonObject record || record["source_id"] is not JsonValue source || !source.TryGetValue<string>(out var sourceId) || sourceId.Length == 0
                        || record["source_manga_id"] is not JsonValue manga || !manga.TryGetValue<long>(out var mangaId) || mangaId <= 0)
                        return Problem(400, "Each record needs source_id and source_manga_id");
                    records.Add(new NativeSourceRecord(sourceId, mangaId));
                }
                var actor = Actor(context);
                try { return Answer(await Task.Run(() => duplicates.UnmatchRecords(entryId, records, actor), CancellationToken.None).ConfigureAwait(false)); }
                catch (KeyNotFoundException) { return Problem(404, "Entry not found"); }
                catch (InvalidOperationException failure) { return Problem(409, failure.Message); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("unmatchDuplicateRecords")
            .WithSummary("Split the chosen source records off an entry into one new entry; the split is remembered as a manual decision so automation never re-joins them.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/entries/{entryId}/unbind-auto-matches", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (duplicates is null) return Unavailable();
                if (!EntryId(context, out var entryId)) return Problem(400, "A numeric entry id is required");
                var actor = Actor(context);
                try { return Answer(await Task.Run(() => duplicates.UnbindAutomaticRecords(entryId, actor), CancellationToken.None).ConfigureAwait(false)); }
                catch (KeyNotFoundException) { return Problem(404, "Entry not found"); }
                catch (InvalidOperationException failure) { return Problem(409, failure.Message); }
            }))
            .WithName("unbindAutomaticMatches")
            .WithSummary("Split every automatic source attachment off an entry (each to its own entry) and keep them apart; manual, imported, split and locked records stay.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/entries/{entryId}/redo-automation", (Func<HttpContext, IResult>)(context =>
            {
                if (duplicates is null) return Unavailable();
                if (!EntryId(context, out var entryId)) return Problem(400, "A numeric entry id is required");
                if (duplicates.Jobs is null) return Problem(503, "The job queue is not running; nothing was started.");
                try
                {
                    var handle = duplicates.ScheduleRedo(entryId, Actor(context));
                    return Answer(new JsonObject { ["queued"] = !handle.Merged, ["already_running"] = handle.Merged, ["task_id"] = handle.Id, ["entry_id"] = entryId }, 202);
                }
                catch (KeyNotFoundException) { return Problem(404, "Entry not found"); }
                catch (Exception failure) when (failure is ObjectDisposedException or ArgumentException or InvalidOperationException)
                { return Problem(503, "The job queue is stopping; nothing was started."); }
            }))
            .WithName("redoEntryAutomation")
            .WithSummary("Redo an entry's automation as a job: keep the best-supported native record and manual/locked records, split every other automatic attachment back out, drop generated identities and titles, then search and refresh again.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
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
