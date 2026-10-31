using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>Existing Web UI compatibility observation wire; actual configured services retain report persistence and transport ownership.</summary>
public static class EngineCompatibilityEndpoints
{
    /// <param name="adminLock">The instance's shared admin semaphore, also used by source mutations and lifecycle maintenance.</param>
    /// <param name="source">Actual configured source-engine capability service, or null when not configured.</param>
    /// <param name="main">Actual configured main-engine capability service, or null when not configured.</param>
    public static RouteGroupBuilder MapEngineCompatibility(this IEndpointRouteBuilder endpoints,
        SourceEngineCapabilityContract? source, SourceEngineCapabilityContract? main, Func<AutoSourcarrSettings> settings, SemaphoreSlim adminLock)
    {
        ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(adminLock);
        var group = endpoints.MapGroup("/api/admin/engine").AddEndpointFilter(new AdminAuthorizationFilter(settings));
        group.MapPost("/compatibility", (Func<HttpContext, Task<IResult>>)(context => CheckAsync(context, source, main, adminLock)));
        return group;
    }

    private static async Task<IResult> CheckAsync(HttpContext context, SourceEngineCapabilityContract? source, SourceEngineCapabilityContract? main, SemaphoreSlim adminLock)
    {
        // FastAPI's scalar query parameter takes the final value when a key repeats.
        var values = context.Request.Query["target"]; var target = values.Count == 0 ? "source" : values[^1];
        context.Response.Headers.CacheControl = "no-store";
        if (target is not ("source" or "main")) return Results.Json(new { detail = "Select source or main engine" }, statusCode: 422);
        await adminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            try
            {
                var service = (target == "source" ? source : main) ?? throw new InvalidOperationException("Configure this engine first");
                // Do not cancel only the await: the real check joins its owned transport before this lane is released.
                return Results.Json(await service.CheckAsync(context.RequestAborted).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                // An owned-service stop may cancel even when the caller has not disconnected.
                // Mark the HTTP request aborted so the shared pipeline propagates that cancellation too.
                context.Abort(); throw;
            }
            catch (Exception error)
            {
                // Released route intentionally reports incomplete409, including typed local deferral.
                // Retain the real exception type internally; no message, credentials or health mutation is exposed.
                return Results.Json(new { detail = "Engine compatibility check incomplete: " + error.GetType().Name }, statusCode: 409);
            }
        }
        finally { adminLock.Release(); }
    }
}
