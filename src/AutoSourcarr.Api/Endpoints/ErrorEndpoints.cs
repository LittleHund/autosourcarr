using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

public static class ErrorEndpoints
{
    public static RouteGroupBuilder MapErrors(this IEndpointRouteBuilder endpoints, ErrorRegistry errors, BoundedWorkScheduler readLane,
        Func<AutoSourcarrSettings> settings)
    {
        var group = endpoints.MapGroup("/api/admin/errors").AddEndpointFilter(new AdminAuthorizationFilter(settings));
        group.MapGet("", (Func<HttpContext, Task<IResult>>)(async context =>
        {
            var limit = ApiQuery.Integer(context.Request, "limit", 50, 1, 100);
            var includeResolved = ApiQuery.Boolean(context.Request, "include_resolved", true);
            return Results.Json(await readLane.RunAsync(() => errors.Snapshot(limit, includeResolved), context.RequestAborted).ConfigureAwait(false));
        }));
        group.MapPost("/clear", (HttpContext context) => Results.Json(new { ok = true, cleared = errors.Clear(ApiQuery.Boolean(context.Request, "resolved_only", true)) }));
        return group;
    }
}
