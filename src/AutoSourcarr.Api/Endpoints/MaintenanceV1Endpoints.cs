using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Accounts;
using AutoSourcarr.Core.Catalogue.Maintenance;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Manual;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static AutoSourcarr.Api.Endpoints.ManualDecisionsV1Endpoints;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the maintenance routes need (null answers 503).</summary>
public sealed class MaintenanceV1Services
{
    public required CatalogueMaintenance Maintenance { get; init; }
    public required ManualDecisions Manual { get; init; }
    /// <summary>cached_subbed_payload: the last main-library snapshot (entry deletion refuses subscribed entries unless forced).</summary>
    public Func<JsonObject>? SubbedPayload { get; init; }
    /// <summary>Asks for a background refresh of the main-library snapshot when it is missing or stale.</summary>
    public Action? RefreshSubbed { get; init; }
    /// <summary>Clears every cached cover (reset-covers); answers the files removed.</summary>
    public Func<CancellationToken, Task<int>>? ClearCovers { get; init; }
}

/// <summary>
/// Phase 8 catalogue maintenance under /api/v1: previewed and confirmed manual resets (reset-all-manual-actions,
/// reset-manual-groups, reset-manual-matches), the database resets (as exclusive jobs), cover reset, and entry
/// maintenance (delete, detach or promote records, clear a source match). Resets are owner/admin only; cross-site
/// writes are refused for all /api/v1 writes by CrossSiteWritePolicy.
/// </summary>
internal static class MaintenanceV1Endpoints
{
    /// <summary>Exclusive and not cancellable: nothing else writes while the catalogue is cleared and rebuilt.</summary>
    public static readonly JobType DatabaseResetJob = new("database-reset", "Reset database", JobPriority.High,
        JobFlags.Exclusive | JobFlags.NeedsDisk, StallTimeout: TimeSpan.FromHours(2), Cancellable: false);
    public static readonly JobType FullResetJob = new("database-full-reset", "Full reset", JobPriority.High,
        JobFlags.Exclusive | JobFlags.NeedsDisk, StallTimeout: TimeSpan.FromHours(2), Cancellable: false);

    /// <summary>Resets need an owner or admin account (not a script key); without accounts the admin token was already checked.</summary>
    private static bool OwnerOrAdmin(HttpContext context) => !context.Items.ContainsKey(AccessControlMiddleware.CallerItem)
        || AccessControlMiddleware.CallerOf(context).Role is Role.Owner or Role.Admin;

    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, MaintenanceV1Services? maintenance)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Catalogue maintenance is not available in this host.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        IResult Forbidden() => Problem(403, "Only the owner or an admin account can reset decisions or the database.");
        IResult Guard(Func<IResult> action)
        {
            try { return action(); }
            catch (KeyNotFoundException error) { return Problem(404, error.Message); }
            catch (StaleManualPreviewException error) { return Problem(409, error.Message); }
            catch (ManualDecisionUnavailableException error) { return Problem(503, error.Message); }
            catch (ManualDecisionConflictException error) { return Problem(409, error.Message); }
            catch (ArgumentException error) { return Problem(400, error.Message); }
        }

        foreach (var (path, scope, name, title) in new[]
        {
            ("reset-all-manual-actions", ManualResetScope.All, "AllManualActions", "every saved manual decision (matches, groups, titles, separate rules, reviews, rejected chapters and imports, deleted-record suppression)"),
            ("reset-manual-groups", ManualResetScope.Groups, "ManualGroups", "the manual groups (matches stay attached and bound)"),
            ("reset-manual-matches", ManualResetScope.Matches, "ManualMatches", "the manual matches and groups (records stay attached as automatic ones)"),
        })
        {
            group.MapGet("/maintenance/" + path, (Func<HttpContext, IResult>)(context =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    if (maintenance is null) return Unavailable();
                    return OwnerOrAdmin(context) ? Guard(() => Answer(maintenance.Manual.PreviewReset(scope))) : Forbidden();
                }))
                .WithName("preview" + name + "Reset")
                .WithSummary("Preview of resetting " + title + ": counts per kind and the preview_token the confirmation must send back.")
                .Produces<JsonObject>(StatusCodes.Status200OK)
                .Produces<ProblemResponse>(StatusCodes.Status403Forbidden);

            group.MapPost("/maintenance/" + path, (Func<HttpContext, Task<IResult>>)(async context =>
                {
                    if (maintenance is null) return Unavailable();
                    if (!OwnerOrAdmin(context)) return Forbidden();
                    JsonObject body;
                    try { body = await Body(context).ConfigureAwait(false); }
                    catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                    // Never automatic: an explicit confirm plus the token of the preview the user saw (0.4.5 sent it as revision).
                    var token = BodyText(body, "preview_token") is { Length: > 0 } given ? given : BodyText(body, "revision");
                    if (!BodyFlag(body, "confirm") || token.Length == 0)
                        return Problem(400, "Confirm the reset: send {\"confirm\": true, \"preview_token\": \"...\"} from its preview.");
                    var actor = Actor(context);
                    return Guard(() => Answer(maintenance.Manual.Reset(scope, token, actor)));
                }))
                .Accepts<JsonObject>("application/json")
                .WithName("reset" + name)
                .WithSummary("Forget " + title + " after the preview was confirmed: body {confirm: true, preview_token}. Refused (409) when the decisions changed since the preview.")
                .Produces<JsonObject>(StatusCodes.Status200OK)
                .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
                .Produces<ProblemResponse>(StatusCodes.Status409Conflict);
        }

        foreach (var (path, full, name) in new[] { ("reset-database", false, "resetDatabase"), ("full-reset", true, "fullResetDatabase") })
        {
            group.MapPost("/maintenance/" + path, (Func<HttpContext, Task<IResult>>)(async context =>
                {
                    if (maintenance is null) return Unavailable();
                    if (!OwnerOrAdmin(context)) return Forbidden();
                    JsonObject body;
                    try { body = await Body(context).ConfigureAwait(false); }
                    catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                    if (!BodyFlag(body, "confirm")) return Problem(400, "Confirm the reset: send {\"confirm\": true}.");
                    if (services.Jobs is not { } jobs) return Problem(503, "The job queue is not available in this host; nothing was reset.");
                    var actor = Actor(context); var catalogue = maintenance.Maintenance;
                    JobHandle handle;
                    try
                    {
                        handle = jobs.Enqueue(full ? FullResetJob : DatabaseResetJob, "", async job =>
                        {
                            var result = await catalogue.ResetAsync(full, actor, message => job.Report(message: message), CancellationToken.None).ConfigureAwait(false);
                            job.Result((full ? "Full reset done" : "Database reset done") + ": " + result.ToJsonString());
                        }, origin: "manual", title: full ? "Full reset" : "Reset database");
                    }
                    catch (Exception error) when (error is ObjectDisposedException or ArgumentException)
                    { return Problem(503, "The job queue is stopping; nothing was reset."); }
                    return Answer(new JsonObject { ["queued"] = true, ["task_id"] = handle.Id, ["merged"] = handle.Merged, ["full_reset"] = full }, 202);
                }))
                .Accepts<JsonObject>("application/json")
                .WithName(name)
                .WithSummary(full
                    ? "Full reset as an exclusive job (body {confirm: true}): clears the catalogue and every decision, keeps configuration and main-library subscriptions."
                    : "Database reset as an exclusive job (body {confirm: true}): clears the rebuildable catalogue, keeps manual decisions and subscriptions, and restores manual matches.")
                .Produces<JsonObject>(StatusCodes.Status202Accepted)
                .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
                .Produces<ProblemResponse>(StatusCodes.Status403Forbidden);
        }

        group.MapPost("/maintenance/reset-covers", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (maintenance?.ClearCovers is not { } clear) return Unavailable();
                if (!OwnerOrAdmin(context)) return Forbidden();
                var removed = await clear(context.RequestAborted).ConfigureAwait(false);
                return Answer(new JsonObject { ["ok"] = true, ["removed_files"] = removed });
            }))
            .WithName("resetCovers")
            .WithSummary("Invalidate every cached cover; source thumbnails are fetched again when shown.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapDelete("/entries/{entryId}", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (maintenance is null) return Unavailable();
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                JsonObject body;
                try { body = await Body(context).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (!BodyFlag(body, "force"))
                {
                    // Deleting must not walk main Suwayomi: use the completed snapshot, refresh it when stale and fail safely.
                    var snapshot = maintenance.SubbedPayload?.Invoke();
                    var age = snapshot?["cache_age_seconds"] is JsonValue value && value.TryGetValue<double>(out var seconds) ? seconds : double.MaxValue;
                    var limit = Math.Max(5.0, services.Settings().SubbedCacheTtlSeconds);
                    if (snapshot is not null && (snapshot["ok"] is not JsonValue ok || !ok.TryGetValue<bool>(out var fresh) || !fresh || age > limit))
                    {
                        maintenance.RefreshSubbed?.Invoke();
                        return Problem(409, "Main Suwayomi library state is refreshing. Wait a moment and retry, or confirm with force to delete anyway.");
                    }
                    if (snapshot?["entry_ids"] is JsonArray subscribed && subscribed.OfType<JsonValue>().Any(id => id.TryGetValue<long>(out var subscribedId) && subscribedId == entryId))
                        return Problem(409, "This entry is in the main Suwayomi library. Repeat with force to delete it anyway.");
                }
                return Guard(() => Answer(maintenance.Maintenance.DeleteEntry(entryId, Actor(context))));
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("deleteCatalogueEntry")
            .WithSummary("Delete an entry and suppress its source records so a refresh does not bring it back. Refused for main-library subscriptions unless the body says {force: true}.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapDelete("/entries/{entryId}/variants/{sourceId}/{sourceMangaId}", (Func<HttpContext, IResult>)(context =>
            {
                if (maintenance is null) return Unavailable();
                if (!TryRouteLong(context, "entryId", out var entryId) || !TryRouteLong(context, "sourceMangaId", out var mangaId)) return Problem(400, "Invalid entry or source manga id.");
                return Guard(() => Answer(maintenance.Maintenance.ClearSourceVariant(entryId, Route(context, "sourceId"), mangaId, Actor(context))));
            }))
            .WithName("clearSourceVariant")
            .WithSummary("Remove an additional source record from an entry.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/entries/{entryId}/variants/{sourceId}/{sourceMangaId}/promote", (Func<HttpContext, IResult>)(context =>
            {
                if (maintenance is null) return Unavailable();
                if (!TryRouteLong(context, "entryId", out var entryId) || !TryRouteLong(context, "sourceMangaId", out var mangaId)) return Problem(400, "Invalid entry or source manga id.");
                return Guard(() => Answer(maintenance.Maintenance.PromoteVariantToPrimary(entryId, Route(context, "sourceId"), mangaId, Actor(context))));
            }))
            .WithName("promoteSourceVariant")
            .WithSummary("Use an additional record as the primary record of its source; the old primary stays as an additional record.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapDelete("/entries/{entryId}/matches/{sourceId}", (Func<HttpContext, IResult>)(context =>
            {
                if (maintenance is null) return Unavailable();
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                return Guard(() => Answer(maintenance.Maintenance.ClearMatch(entryId, Route(context, "sourceId"), Actor(context))));
            }))
            .WithName("clearSourceMatch")
            .WithSummary("Detach every record of one source from the entry (their manual bindings are forgotten).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);
    }
}
