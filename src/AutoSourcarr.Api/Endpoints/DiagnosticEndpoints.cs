using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

public static class DiagnosticEndpoints
{
    public static RouteGroupBuilder MapDiagnostics(this IEndpointRouteBuilder endpoints, DiagnosticExporter exporter, Func<AutoSourcarrSettings> settings)
    {
        var group = endpoints.MapGroup("/api/admin/diagnostics").AddEndpointFilter(new AdminAuthorizationFilter(settings));
        group.MapGet("", (Func<HttpContext, Task<IResult>>)(async context =>
        {
            var payload = await exporter.ExportAsync(context.RequestAborted).ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(payload);
        }));
        return group;
    }
}
