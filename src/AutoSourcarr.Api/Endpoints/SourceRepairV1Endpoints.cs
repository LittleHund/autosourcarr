using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Repair.Migration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the source repair routes need from the running app (null answers 503).</summary>
public sealed class SourceRepairServices
{
    /// <summary>Domain repair of installed extensions (rewrite, structure check, install, content check, rollback).</summary>
    public required DomainRepair Domain { get; init; }
    /// <summary>Records a failure of background work started by a route when no job queue runs it.</summary>
    public Action<Exception>? RecordError { get; init; }
}

/// <summary>
/// Phase 6e source repairs under /api/v1: a domain migration of an installed extension as a job, its rollback to the
/// last kept working jar, and the package's last repair report.
/// </summary>
internal static class SourceRepairV1Endpoints
{
    /// <summary>One repair per package at a time (same type and key merge); it waits for the shared source admin lock itself.</summary>
    public static readonly JobType DomainRepairJob = new("source-domain-repair", "Repair source domain", JobPriority.High,
        StallTimeout: TimeSpan.FromMinutes(10));
    public static readonly JobType RollbackJob = new("source-repair-rollback", "Restore source jar", JobPriority.High,
        StallTimeout: TimeSpan.FromMinutes(5));

    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, SourceRepairServices? repair)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Source repairs are not available in this host.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        string Package(HttpContext context) => context.Request.RouteValues["pkgName"]?.ToString() ?? "";
        bool Integrated() => services.Settings().SourceEngineMode == "integrated";

        group.MapPost("/source-repairs/{pkgName}/domain", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (repair is null) return Unavailable();
                if (!Integrated()) return Problem(409, "Integrated engine required");
                var package = Package(context);
                if (!DomainRepair.IsPackageName(package)) return Problem(400, "A plain extension package name is required");
                JsonObject body;
                try
                {
                    body = context.Request.ContentLength is null or 0 && !context.Request.Headers.ContainsKey("Transfer-Encoding")
                        ? new JsonObject() : await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
                }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                string? Optional(string name) => body[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length > 0 ? text.Trim() : null;
                foreach (var name in new[] { "new_host", "old_host", "query" })
                    if (body[name] is not null && !(body[name] is JsonValue item && item.TryGetValue<string>(out _))) return Problem(400, name + " must be a string");
                if (body["https"] is not null && !(body["https"] is JsonValue flag && flag.TryGetValue<bool>(out _))) return Problem(400, "https must be a boolean");
                var options = new DomainRepairOptions(Optional("new_host"), Optional("old_host"), Optional("query"),
                    body["https"] is JsonValue https && https.TryGetValue<bool>(out var upgrade) && upgrade);
                if (options.NewHost is { } newHost)
                {
                    try { DomainMigration.NormalizeHost(newHost); }
                    catch (DomainMigrationException error) { return Problem(400, error.Message); }
                }
                var domain = repair.Domain; var record = repair.RecordError;
                var title = "Repair source domain " + package;
                if (services.Jobs is { } jobs)
                {
                    JobHandle handle;
                    try
                    {
                        handle = jobs.Enqueue(DomainRepairJob, package, async job =>
                        {
                            var report = await domain.RunAsync(package, options, text => job.Report(message: text), job.CancellationToken).ConfigureAwait(false);
                            var message = report["message"]?.GetValue<string>() ?? "";
                            if (report["state"]?.GetValue<string>() != "repaired") throw new InvalidOperationException(message);
                            job.Result(message);
                        }, origin: "manual", title: title, context: package);
                    }
                    catch (Exception error) when (error is ObjectDisposedException or ArgumentException)
                    { return Problem(503, "The job queue is stopping; nothing was started."); }
                    return Answer(new JsonObject { ["queued"] = true, ["task_id"] = handle.Id, ["merged"] = handle.Merged, ["package"] = package }, 202);
                }
                _ = Task.Run(async () =>
                {
                    try { await domain.RunAsync(package, options, null, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException) { record?.Invoke(error); }
                });
                return Answer(new JsonObject { ["queued"] = true, ["task_id"] = null, ["merged"] = false, ["package"] = package }, 202);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("repairSourceDomain")
            .WithSummary("Repair an installed source whose website moved: keep its jar, rewrite the old host in its compiled strings (new_host, or the observed redirect), check the structure, install, check content, and roll back when the check fails. Runs as a job.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/source-repairs/{pkgName}/rollback", (Func<HttpContext, IResult>)(context =>
            {
                if (repair is null) return Unavailable();
                if (!Integrated()) return Problem(409, "Integrated engine required");
                var package = Package(context);
                if (!DomainRepair.IsPackageName(package)) return Problem(400, "A plain extension package name is required");
                var domain = repair.Domain; var record = repair.RecordError;
                if (!Directory.Exists(domain.LastWorkingDirectory(package))) return Problem(404, "No working jar of " + package + " has been kept");
                if (services.Jobs is { } jobs)
                {
                    JobHandle handle;
                    try
                    {
                        handle = jobs.Enqueue(RollbackJob, package, async job =>
                        {
                            job.Report(message: "Installing the last kept jar of " + package);
                            var report = await domain.RollbackAsync(package, job.CancellationToken).ConfigureAwait(false);
                            var message = report["message"]?.GetValue<string>() ?? "";
                            if (report["state"]?.GetValue<string>() != "rolled_back") throw new InvalidOperationException(message);
                            job.Result(message);
                        }, origin: "manual", title: "Restore source jar " + package, context: package);
                    }
                    catch (Exception error) when (error is ObjectDisposedException or ArgumentException)
                    { return Problem(503, "The job queue is stopping; nothing was started."); }
                    return Answer(new JsonObject { ["queued"] = true, ["task_id"] = handle.Id, ["merged"] = handle.Merged, ["package"] = package }, 202);
                }
                _ = Task.Run(async () =>
                {
                    try { await domain.RollbackAsync(package, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException) { record?.Invoke(error); }
                });
                return Answer(new JsonObject { ["queued"] = true, ["task_id"] = null, ["merged"] = false, ["package"] = package }, 202);
            }))
            .WithName("rollbackSourceDomainRepair")
            .WithSummary("Install the last kept working jar of a package again (as a job).")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapGet("/source-repairs/{pkgName}", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (repair is null) return Unavailable();
                var package = Package(context);
                if (!DomainRepair.IsPackageName(package)) return Problem(400, "A plain extension package name is required");
                JsonObject? report;
                try { report = repair.Domain.LastReport(package); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                { return Problem(409, "The repair report is unavailable: " + error.Message); }
                return report is null ? Problem(404, "No repair has run for " + package) : Answer(report);
            }))
            .WithName("getSourceRepair")
            .WithSummary("The last repair or rollback report of a package (state repaired, refused, failed, rolled_back or rollback_failed, with the migration, structure and content checks).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);
    }
}
