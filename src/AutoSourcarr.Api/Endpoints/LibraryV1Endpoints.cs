using System.Text.Json.Nodes;
using AutoSourcarr.Core.Sources.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the main-library routes need from the running app (null answers 503).</summary>
public sealed class LibraryV1Services
{
    /// <summary>The main Suwayomi library view (Subbed set, cached status, download size).</summary>
    public required SuwayomiLibrary Library { get; init; }
}

/// <summary>
/// Phase 6: the main-library (Subbed) route under /api/v1, ported from main.py GET /api/admin/subbed. The Search all
/// and Repair unmatched actions (POST /api/admin/subbed/search-all, /repair-unmatched) arrive with matching in phase 8.
/// </summary>
internal static class LibraryV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json)
    {
        group.MapGet("/subbed", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services.Library is not { } library) return Unavailable();
                // Same as 0.4.5: served from the snapshot while it is younger than SUBBED_CACHE_TTL_SECONDS, otherwise read live.
                return Results.Json(await library.Library.SubbedEntryIdsAsync(false, context.RequestAborted).ConfigureAwait(false), json.JsonObject);
            }))
            .WithName("getSubbedLibrary")
            .WithSummary("Main Suwayomi library entries from the AutoSourcarr source, mapped to catalogue entries: entry_ids, items, stats_by_entry (chapter and downloaded counts), ok and error.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult Unavailable() =>
        Results.Json(new ProblemResponse("The main library view is not available in this host."), ApiJsonContext.Default.ProblemResponse, statusCode: 503);
}
