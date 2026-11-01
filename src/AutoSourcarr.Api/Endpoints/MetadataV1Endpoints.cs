using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Catalogue.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static AutoSourcarr.Api.Endpoints.ManualDecisionsV1Endpoints;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the metadata routes need from the running app (null answers 503).</summary>
public sealed class MetadataV1Services
{
    public required MetadataChooser Chooser { get; init; }
}

/// <summary>
/// Phase 9 per-field metadata under /api/v1: the chosen value of every field with its source and the ranked alternatives
/// (official sources, then AniList / MangaUpdates / MangaDex, then other sources by health and priority), and per-field
/// overrides that always win. Reading is a catalogue read (guests included; who set an override is shown to admins only);
/// overrides are admin writes recorded in the decision log with the caller as actor.
/// </summary>
internal static class MetadataV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiJsonContext json, MetadataV1Services? metadata)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Per-field metadata is not available in this host.");
        IResult Answer(JsonObject value) => Results.Json(value, json.JsonObject);
        IResult Guard(Func<JsonObject> action)
        {
            try { return Answer(action()); }
            catch (MetadataChoiceException error) { return Problem(error.Status, error.Message); }
        }
        static bool Admin(HttpContext context) => !context.Items.ContainsKey(AccessControlMiddleware.CallerItem) || AccessControlMiddleware.CallerOf(context).AdminReach;

        group.MapGet("/entries/{entryId}/metadata", (Func<HttpContext, Task<IResult>>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (metadata is null) return Task.FromResult(Unavailable());
                if (!TryRouteLong(context, "entryId", out var entryId)) return Task.FromResult(Problem(400, "Invalid entry id."));
                return Task.FromResult(Guard(() => metadata.Chooser.Read(entryId, includePrivate: Admin(context))));
            }))
            .WithName("getEntryMetadata")
            .WithSummary("Every metadata field (title, description, authors, artists, genres, status, year, cover) with the chosen value, where it came from, whether it is overridden, and the alternatives in rank order.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPut("/entries/{entryId}/metadata/{field}", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (metadata is null) return Unavailable();
                if (!Admin(context)) return Problem(403, "Only an admin can override metadata.");
                if (!TryRouteLong(context, "entryId", out var entryId)) return Problem(400, "Invalid entry id.");
                JsonObject body;
                try { body = await Body(context).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                var actor = Actor(context);
                return Guard(() => metadata.Chooser.Override(entryId, Route(context, "field"), body, actor));
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("overrideEntryMetadataField")
            .WithSummary("Override one field: body {value} (status as a word or 0-6, year as a number, names as text or a list), or {source_id, source_manga_id} to follow an attached record (description, cover, authors, artists). The title override is the entry's main title. Recorded with the caller as actor.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status403Forbidden)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapDelete("/entries/{entryId}/metadata/{field}", (Func<HttpContext, Task<IResult>>)(context =>
            {
                if (metadata is null) return Task.FromResult(Unavailable());
                if (!Admin(context)) return Task.FromResult(Problem(403, "Only an admin can clear a metadata override."));
                if (!TryRouteLong(context, "entryId", out var entryId)) return Task.FromResult(Problem(400, "Invalid entry id."));
                var actor = Actor(context);
                return Task.FromResult(Guard(() => metadata.Chooser.ClearOverride(entryId, Route(context, "field"), actor)));
            }))
            .WithName("clearEntryMetadataOverride")
            .WithSummary("Clear one field's override; the field returns to the ranked choice. Clearing the title keeps manual alternate titles.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);
    }
}
