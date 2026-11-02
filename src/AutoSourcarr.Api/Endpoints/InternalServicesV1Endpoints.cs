using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>
/// Phase 11: the 0.5.0 status read of the Status page's internal services panel (0.4.x /api/admin/services). 0.5.0 runs
/// no helper Compose services; the read reports the app's own parts (source host, WebView display, challenge handling,
/// repair runner and its egress proxy, translation, metadata providers, main Suwayomi, prebuilt reader extension) from
/// state the app already holds, without network waits. Nothing is started from it (<c>actions</c> is empty).
/// </summary>
internal static class InternalServicesV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiJsonContext json, Func<JsonObject>? read)
    {
        group.MapGet("/internal-services", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (read is null) return Results.Json(new ProblemResponse("Internal service status is not available in this host."), json.ProblemResponse, statusCode: 503);
                return Results.Json(read(), json.JsonObject);
            }))
            .WithName("getInternalServices")
            .WithSummary("Status page: the app's own services (source host, WebView, challenge handling, repair runner and egress proxy, translation, metadata providers, main Suwayomi, reader extension) as {mode, actions, services: {name: {label, state, message}}, reader_build}; read from cached state, never waits on the network.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);
    }
}
