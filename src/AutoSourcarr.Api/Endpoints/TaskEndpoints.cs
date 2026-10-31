using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

public interface IRepairTaskContext
{
    (JsonObject Operation, RepairTaskSupplement Supplement) CaptureCached();
    Task<bool> RequestCancelAsync(string repairId, CancellationToken cancellationToken);
}

public static class TaskEndpoints
{
    public static RouteGroupBuilder MapTasks(this IEndpointRouteBuilder endpoints, TaskRegistry registry, RepairTaskProjection repairs,
        IRepairTaskContext repairContext, BoundedWorkScheduler readLane, Func<AutoSourcarrSettings> settings)
    {
        var group = endpoints.MapGroup("/api/admin/tasks").AddEndpointFilter(new AdminAuthorizationFilter(settings));
        group.MapGet("", (Func<HttpContext, Task<IResult>>)(async context => Results.Json(await readLane.RunAsync(() =>
        {
            var cached = repairContext.CaptureCached();
            return repairs.Merge(registry.Snapshot(), cached.Operation, cached.Supplement);
        }, context.RequestAborted).ConfigureAwait(false))));
        group.MapPost("/clear-history", (Func<HttpContext, Task<IResult>>)(async context => Results.Json(await readLane.RunAsync(() =>
        {
            var cached = repairContext.CaptureCached(); var local = registry.ClearHistory(); var source = repairs.ClearHistory(cached.Operation, cached.Supplement);
            var result = new JsonObject { ["ok"] = true, ["kept_for_review"] = source["kept_for_review"]!.DeepClone() };
            foreach (var field in new[] { "cleared", "completed", "failed" }) result[field] = local[field]!.GetValue<int>() + source[field]!.GetValue<int>();
            return result;
        }, context.RequestAborted).ConfigureAwait(false))));
        group.MapPost("/{task_id}/cancel", (Func<HttpContext, Task<IResult>>)(async context =>
        {
            var id = context.Request.RouteValues["task_id"]?.ToString() ?? "";
            if (id.StartsWith("source-repair:", StringComparison.Ordinal))
            {
                var repairId = id["source-repair:".Length..];
                if (!Regex.IsMatch(repairId, "\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant)) throw new ApiProblem(404, "Invalid repair task identifier");
                if (!await repairContext.RequestCancelAsync(repairId, context.RequestAborted).ConfigureAwait(false)) throw new ApiProblem(409, "Repair is not running; refresh its status");
                return Results.Json(new { ok = true, task_id = id, cancel_requested = true });
            }
            if (!await registry.RequestCancelAsync(id).ConfigureAwait(false)) throw new ApiProblem(409, "Task is not running or cannot be cancelled safely");
            return Results.Json(new { ok = true, task_id = id, state = "failed", error = "Cancelled by user" });
        }));
        return group;
    }
}
