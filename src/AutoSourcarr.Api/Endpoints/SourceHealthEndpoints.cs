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
        group.MapGet("", (Func<HttpContext, Task<IResult>>)(async context => Results.Json(await health.SnapshotAsync(context.RequestAborted).ConfigureAwait(false))));
        group.MapPut("/daily", (Func<HttpContext, Task<IResult>>)(async context =>
        {
            var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
            if (body["enabled"] is not JsonValue enabledValue || !enabledValue.TryGetValue<bool>(out var enabled)) throw new ApiProblem(400, "Expected enabled boolean");
            await adminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try { return Results.Json(await health.ConfigureDailyAsync(enabled, context.RequestAborted).ConfigureAwait(false)); }
            finally { adminLock.Release(); }
        }));
        group.MapPost("/check", (Func<HttpContext, Task<IResult>>)(async context =>
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
        return group;
    }
}
