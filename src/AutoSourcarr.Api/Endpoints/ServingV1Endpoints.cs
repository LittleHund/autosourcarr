using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Catalogue.Serving;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Reader;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static AutoSourcarr.Api.Endpoints.ManualDecisionsV1Endpoints;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the serving routes need from the running app (null members answer 503 on their routes).</summary>
public sealed class ServingV1Services
{
    public GalleryPolicyRepository? GalleryPolicies { get; init; }
    public GalleryLayout? GalleryLayout { get; init; }
    public ServingUpgrades? Upgrades { get; init; }
    public ServingProfileRepository? Profiles { get; init; }
    /// <summary>Records gallery-policy and serving-profile changes with the caller (upgrades record their own).</summary>
    public IDecisionLog? Decisions { get; init; }
    /// <summary>The instance admin lock (0.4.5 admin_lock) taken by policy writes.</summary>
    public SemaphoreSlim? AdminLock { get; init; }
}

/// <summary>
/// Phase 10 serving rules under /api/v1 (0.4.5 /api/admin/gallery-policies/*, /serving-upgrades/*, /profiles/*): gallery
/// source policies and their layout job, the opt-in quality-upgrade policy per work with preview/apply (CHAP-6), and the
/// per-work serving profile (languages, sources, quality preference). All admin; writes record the caller.
/// </summary>
internal static class ServingV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, ServingV1Services? serving)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable(string what) => Problem(503, what + " is not available in this host.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        async Task<IResult> Locked(HttpContext context, Func<IResult> action)
        {
            if (serving?.AdminLock is not { } gate) return action();
            await gate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try { return action(); }
            finally { gate.Release(); }
        }
        static bool Revision(JsonObject body, out long revision)
        {
            revision = 0;
            return body["revision"] is JsonValue value && value.TryGetValue<long>(out revision) && revision >= 0;
        }

        // ---- gallery source policies ---------------------------------------------------------------------------------
        group.MapGet("/gallery-policies/{sourceId}", (Func<HttpContext, IResult>)(context =>
            {
                if (serving?.GalleryPolicies is null) return Unavailable("Gallery policies");
                try { return Answer(serving.GalleryPolicies.Read(Route(context, "sourceId"))); }
                catch (KeyNotFoundException) { return Problem(404, "Source not found"); }
            }))
            .WithName("getGalleryPolicy")
            .WithSummary("A source's gallery policy (profile, combining, criteria, title minimum, tag thresholds), its revision, whether it looks like a gallery source, how many works combine its records and the last layout result.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPut("/gallery-policies/{sourceId}", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (serving?.GalleryPolicies is null) return Unavailable("Gallery policies");
                JsonObject body;
                try { body = await Body(context).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (!Revision(body, out var revision) || body["policy"] is not JsonObject policy) return Problem(422, "Expected policy object and revision integer");
                var sourceId = Route(context, "sourceId"); var actor = Actor(context);
                return await Locked(context, () =>
                {
                    try
                    {
                        var before = serving.GalleryPolicies.Read(sourceId);
                        var result = serving.GalleryPolicies.Save(sourceId, policy, revision);
                        serving.Decisions?.Record(new DecisionEvent("gallery.policy", actor, [], [sourceId], "Gallery policy saved for source " + sourceId,
                            before["policy"]?.DeepClone() as JsonObject, result["policy"]?.DeepClone() as JsonObject));
                        if (serving.GalleryLayout is not null && services.Jobs is not null) result["task"] = serving.GalleryLayout.Schedule(services.Jobs, sourceId);
                        return Answer(result);
                    }
                    catch (KeyNotFoundException) { return Problem(404, "Source not found"); }
                    catch (ArgumentException error) { return Problem(409, error.Message); }
                }).ConfigureAwait(false);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("saveGalleryPolicy")
            .WithSummary("Save a source's gallery policy (body {revision, policy}) and queue the layout job that applies it to cached records. A stale revision answers 409.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/gallery-policies/{sourceId}/apply", (Func<HttpContext, IResult>)(context =>
            {
                if (serving?.GalleryLayout is null) return Unavailable("Gallery layout");
                if (services.Jobs is null) return Problem(503, "The job queue is not running; nothing was started.");
                try { return Answer(serving.GalleryLayout.Schedule(services.Jobs, Route(context, "sourceId")), 202); }
                catch (KeyNotFoundException) { return Problem(404, "Source not found"); }
            }))
            .WithName("applyGalleryPolicy")
            .WithSummary("Queue the gallery layout job for a source: cached works holding its records are reconciled with the saved policy (no website requests).")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        // ---- quality upgrades (CHAP-6) -------------------------------------------------------------------------------
        group.MapGet("/serving-upgrades/{entryId}", (Func<HttpContext, IResult>)(context =>
            {
                if (serving?.Upgrades is null) return Unavailable("Quality upgrades");
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                try { return Answer(serving.Upgrades.Read(entryId)); }
                catch (KeyNotFoundException) { return Problem(404, "Entry not found"); }
            }))
            .WithName("getServingUpgradePolicy")
            .WithSummary("A work's quality-upgrade policy (enabled, cutoff, minimum_improvement, cooldown_hours, sample_max_age_hours), its revision and the retained earlier policies.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPut("/serving-upgrades/{entryId}", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (serving?.Upgrades is null) return Unavailable("Quality upgrades");
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                JsonObject body;
                try { body = await Body(context).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (!Revision(body, out var revision)) return Problem(422, "Expected policy object and revision integer");
                var actor = Actor(context);
                return await Locked(context, () =>
                {
                    try { return Answer(serving.Upgrades.Save(entryId, body["policy"], revision, actor)); }
                    catch (KeyNotFoundException) { return Problem(404, "Entry not found"); }
                    catch (ServingUpgradeException error) { return Problem(409, error.Message); }
                }).ConfigureAwait(false);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("saveServingUpgradePolicy")
            .WithSummary("Save a work's quality-upgrade policy (body {revision, policy}); upgrades stay off until enabled. A stale revision or an invalid threshold answers 409.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        foreach (var apply in new[] { false, true })
        {
            group.MapPost("/serving-upgrades/{entryId}/" + (apply ? "apply" : "preview"), (Func<HttpContext, Task<IResult>>)(async context =>
                {
                    if (serving?.Upgrades is null) return Unavailable("Quality upgrades");
                    if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                    JsonObject body;
                    try { body = await Body(context).ConfigureAwait(false); }
                    catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                    var chapterId = BodyLong(body, "chapter_id"); var candidateId = BodyLong(body, "candidate_id"); var revision = BodyText(body, "revision");
                    if (chapterId <= 0 || candidateId <= 0 || apply && revision.Length == 0) return Problem(422, apply ? "Expected chapter_id, candidate_id and revision" : "Expected chapter_id and candidate_id");
                    var actor = Actor(context);
                    return await Locked(context, () =>
                    {
                        try { return Answer(apply ? serving.Upgrades.Apply(entryId, chapterId, candidateId, revision, actor) : serving.Upgrades.Preview(entryId, chapterId, candidateId)); }
                        catch (KeyNotFoundException) { return Problem(404, "Entry or chapter not found"); }
                        catch (ServingUpgradeException error) { return Problem(409, error.Message); }
                    }).ConfigureAwait(false);
                }))
                .Accepts<JsonObject>("application/json")
                .WithName(apply ? "applyServingUpgrade" : "previewServingUpgrade")
                .WithSummary(apply
                    ? "Apply a previewed quality upgrade (body {chapter_id, candidate_id, revision}): the candidate serves this chapter from now on; files, progress and origin history are kept. A changed candidate, sample or policy answers 409."
                    : "Preview serving another candidate for one chapter (body {chapter_id, candidate_id}): quality before/after, the blockers (disabled, locked record, stale samples, cutoff, minimum improvement, cooldown) and the revision to apply.")
                .Produces<JsonObject>(StatusCodes.Status200OK)
                .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
                .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
                .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);
        }

        // ---- serving profiles ----------------------------------------------------------------------------------------
        group.MapGet("/profiles/{entryId}", (Func<HttpContext, IResult>)(context =>
            {
                if (serving?.Profiles is null) return Unavailable("Serving profiles");
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                try { return Answer(serving.Profiles.Read(entryId)); }
                catch (KeyNotFoundException) { return Problem(404, "Work no longer exists"); }
            }))
            .WithName("getServingProfile")
            .WithSummary("A work's serving profile: preferred languages and sources in order and the quality preference (default, quality, complete), with its revision and the profiles retained from merged works.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPut("/profiles/{entryId}", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (serving?.Profiles is null) return Unavailable("Serving profiles");
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                JsonObject body;
                try { body = await Body(context).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (body["profile"] is not JsonObject profile || !Revision(body, out var revision)) return Problem(422, "Expected profile object and revision integer");
                var actor = Actor(context);
                return await Locked(context, () =>
                {
                    try
                    {
                        var before = serving.Profiles.Read(entryId, includeRetained: false);
                        var result = serving.Profiles.Save(entryId, profile, revision);
                        serving.Decisions?.Record(new DecisionEvent("serving.profile", actor, [entryId], [], "Serving profile saved",
                            before["profile"]?.DeepClone() as JsonObject, result["profile"]?.DeepClone() as JsonObject));
                        return Answer(result);
                    }
                    catch (KeyNotFoundException) { return Problem(404, "Work no longer exists"); }
                    catch (ArgumentException error) { return Problem(409, error.Message); }
                }).ConfigureAwait(false);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("saveServingProfile")
            .WithSummary("Save a work's serving profile (body {revision, profile: {languages, sources, quality}}). Preferences only rank candidates already admitted by identity and edition checks; a stale revision answers 409.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);
    }
}
