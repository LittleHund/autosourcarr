using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

public static class SourceHealthEndpoints
{
    public static RouteGroupBuilder MapSourceHealth(this IEndpointRouteBuilder endpoints, SourceHealthCoordinator health, Func<AutoSourcarrSettings> settings, SemaphoreSlim adminLock,
        string prefix = "/api/admin/source-health")
    {
        var group = endpoints.MapGroup(prefix).AddEndpointFilter(new AdminAuthorizationFilter(settings));
        var snapshot = group.MapGet("", (Func<HttpContext, Task<IResult>>)(async context => Results.Json(await health.SnapshotAsync(context.RequestAborted).ConfigureAwait(false))));
        var daily = group.MapPut("/daily", (Func<HttpContext, Task<IResult>>)(async context =>
        {
            var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
            if (body["enabled"] is not JsonValue enabledValue || !enabledValue.TryGetValue<bool>(out var enabled)) throw new ApiProblem(400, "Expected enabled boolean");
            await adminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try { return Results.Json(await health.ConfigureDailyAsync(enabled, context.RequestAborted).ConfigureAwait(false)); }
            finally { adminLock.Release(); }
        }));
        var check = group.MapPost("/check", (Func<HttpContext, Task<IResult>>)(async context =>
        {
            var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
            await adminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                var snapshot = await health.SnapshotAsync(context.RequestAborted).ConfigureAwait(false);
                var rows = snapshot["items"]!.AsArray().OfType<JsonObject>().ToArray();
                if (body["all_enabled"] is JsonValue all && all.TryGetValue<bool>(out var enabled) && enabled)
                    rows = rows.Where(row => row["eligible"]?.GetValue<bool>() == true).ToArray();
                else if (body["source_ids"] is JsonArray ids && ids.Count is >= 1 and <= 500 && ids.All(id => id is JsonValue value && value.TryGetValue<string>(out var sid) && Regex.IsMatch(sid, @"^-?[0-9]{1,20}$", RegexOptions.CultureInvariant)))
                {
                    var selected = ids.Select(id => id!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                    rows = rows.Where(row => selected.Contains(row["source_id"]!.GetValue<string>())).ToArray();
                    if (!rows.Select(row => row["source_id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(selected)) throw new ApiProblem(404, "Installed source not found; refresh sources");
                }
                else throw new ApiProblem(400, "Select installed sources or all_enabled");
                if (rows.Length == 0) throw new ApiProblem(409, "No enabled installed sources to check; sync sources first");
                return Results.Json(health.Queue(rows, "manual"), statusCode: 202);
            }
            finally { adminLock.Release(); }
        }));
        // Phase 6: under /api/v1 the routes join the documented API (the /api/admin wires stay undocumented and unnamed).
        if (prefix.StartsWith("/api/v1/", StringComparison.Ordinal))
        {
            snapshot.WithMetadata(ApiContractMetadata.Instance).WithName("getSourceHealth")
                .WithSummary("Source health (SRC-7): each installed source with its last check, the daily check switch and the running checks.")
                .Produces<JsonObject>(StatusCodes.Status200OK);
            daily.WithMetadata(ApiContractMetadata.Instance).Accepts<JsonObject>("application/json").WithName("setSourceHealthDaily")
                .WithSummary("Turn the daily source health check on or off (body: enabled).")
                .Produces<JsonObject>(StatusCodes.Status200OK)
                .Produces<ProblemResponse>(StatusCodes.Status400BadRequest);
            check.WithMetadata(ApiContractMetadata.Instance).Accepts<JsonObject>("application/json").WithName("checkSourceHealth")
                .WithSummary("Check installed sources now (body: source_ids, or all_enabled true); answers the queued checks.")
                .Produces<JsonObject>(StatusCodes.Status202Accepted)
                .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
                .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
                .Produces<ProblemResponse>(StatusCodes.Status409Conflict);
        }
        return group;
    }
}
