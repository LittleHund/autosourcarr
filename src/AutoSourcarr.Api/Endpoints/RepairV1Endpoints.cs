using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Repair.Migration;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the /api/v1/source-maintenance repair routes need besides <see cref="ApiV1Services.SourceRepair"/>.</summary>
public sealed class RepairV1Services
{
    /// <summary>The app's &lt;data&gt;/source-maintenance folder (feedback.jsonl lives here).</summary>
    public required string MaintenanceRoot { get; init; }
    /// <summary>Source-code storage for the raws routes; null answers 503.</summary>
    public SourceRawRoutes? Raws { get; init; }
    /// <summary>Self-check of the isolated repair runner for control/check; null answers 409 (no runner check in this host).</summary>
    public Func<CancellationToken, Task<JsonObject>>? RunnerCheck { get; init; }
    /// <summary>Reads the jar the source host runs for a package (repaired-code download when the jar was not kept); null skips it.</summary>
    public Func<string, CancellationToken, Task<byte[]>>? ReadInstalledJar { get; init; }
}

/// <summary>The source-code storage the raws routes reuse (the same handlers as <see cref="SourceRawEndpoints"/>).</summary>
public sealed class SourceRawRoutes
{
    public required SourceRawService Service { get; init; }
    public required IRawInstalledSources Installed { get; init; }
    public required SemaphoreSlim AdminLock { get; init; }
    public required BoundedWorkScheduler Io { get; init; }
    public RepairHistory? History { get; init; }
    /// <summary>Reconstruction of a source whose code is unavailable needs an AI provider; until Connections it refuses.</summary>
    public IRawReconstructionCommands Reconstruction { get; init; } = NoRawReconstruction.Instance;
}

/// <summary>There is no external repair worker and no AI provider yet: reconstruction answers 409 with the reason.</summary>
public sealed class NoRawReconstruction : IRawReconstructionCommands
{
    public static NoRawReconstruction Instance { get; } = new();
    public Task EnsureReadyAsync(CancellationToken cancellationToken) =>
        throw new ApiProblem(409, "Rebuilding a source without its code needs an AI provider, which is not configured (it arrives with Connections). Provide the source code or its repository instead.");
    public Task<JsonObject> ReconstructAsync(RawInstalledSource installed, long rawRevision, string? previousJobId, CancellationToken cancellationToken) =>
        throw new ApiProblem(409, "Rebuilding a source without its code is not available in this host.");
}

/// <summary>
/// Phase 6 RepairEndpoints (23 routes) under /api/v1/source-maintenance. Repair runs inside the app: the external
/// worker, its compose/SSH connection setup and the host-policy bundle are retired (410). Repair jobs are domain repairs
/// (<see cref="DomainRepair"/>) queued on the job queue; their task id is the job id. Raw-code routes reuse the
/// <see cref="SourceRawEndpoints"/> handlers.
/// </summary>
internal static class RepairV1Endpoints
{
    /// <summary>Task kinds that are repair jobs, and the short kind the repair pages show.</summary>
    internal static readonly IReadOnlyDictionary<string, string> RepairKinds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SourceRepairV1Endpoints.DomainRepairJob.Name] = "domain",
        [SourceRepairV1Endpoints.RollbackJob.Name] = "rollback",
        [AutoRepairWatcher.AttemptJob.Name] = "auto",
    };
    internal const int FeedbackFileLimit = 256 * 1024;
    internal const int FeedbackTextLimit = 2000;
    private static readonly object FeedbackGate = new();
    private static readonly Regex JobId = new(@"\A[0-9a-f]{12,32}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AccountActions = ["login", "login-status", "logout", "cancel-login", "retry-cleanup"];

    private const string Retired = "Repair runs inside this AutoSourcarr instance. External repair workers and their Compose/SSH setup were removed; refresh the page.";
    private const string NoProvider = "No AI provider is configured. AI-assisted repair arrives with Connections; domain-move repairs run in the app without AI.";

    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        bool Integrated() => services.Settings().SourceEngineMode == "integrated";
        string Route(HttpContext context, string name) => context.Request.RouteValues[name]?.ToString() ?? "";
        async Task<IResult> Guard(HttpContext context, Func<Task<IResult>> action)
        {
            try { return await action().ConfigureAwait(false); }
            catch (ApiProblem error)
            {
                if (error.StatusCode is 413 or 408) context.Response.Headers.CacheControl = "no-store";
                return Problem(error.StatusCode, error.Message);
            }
            catch (DomainRepairException error) { return Problem(error.Status, error.Message); }
            catch (SourceRawConflictException error) { return Problem(409, error.Message); }
            catch (RepairHistoryConflictException error) { return Problem(409, error.Message); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            { return Problem(409, "Repair state is unavailable: " + error.Message); }
        }
        IResult Gone() => Problem(StatusCodes.Status410Gone, Retired);

        // ------------------------------------------------------------ connection and control (in-app state)

        group.MapGet("/source-maintenance/connection", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Answer(new JsonObject
                {
                    ["mode"] = "in-app", ["external_worker"] = false, ["url"] = "", ["token_saved"] = false, ["saved_in_ui"] = false,
                    ["configured"] = services.SourceRepair is not null,
                    ["repair_kinds"] = services.SourceRepair is null ? new JsonArray() : new JsonArray("domain"),
                    ["message"] = services.SourceRepair is null ? "Source repair is not available in this host."
                        : "Repair runs inside this instance; there is no separate repair worker to connect.",
                });
            }))
            .WithName("getRepairConnection")
            .WithSummary("Where repairs run: always inside this instance (no external worker); which repair kinds are available.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPut("/source-maintenance/connection", (Func<HttpContext, IResult>)(_ => Gone()))
            .WithName("setRepairConnection")
            .WithSummary("Retired: external repair worker connections were removed (410).")
            .Produces<ProblemResponse>(StatusCodes.Status410Gone);

        group.MapPost("/source-maintenance/setup-preview", (Func<HttpContext, IResult>)(_ => Gone()))
            .WithName("previewRepairSetup")
            .WithSummary("Retired: Compose/SSH repair worker setup was removed (410).")
            .Produces<ProblemResponse>(StatusCodes.Status410Gone);

        group.MapGet("/source-maintenance/host-setup", (Func<HttpContext, IResult>)(_ => Gone()))
            .WithName("getRepairHostSetup")
            .WithSummary("Retired: the repair host-policy bundle was removed with the external worker (410).")
            .Produces<ProblemResponse>(StatusCodes.Status410Gone);

        group.MapGet("/source-maintenance/control", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var busy = RunningRepairs(services) > 0;
                var available = services.SourceRepair is not null;
                return Answer(new JsonObject
                {
                    ["busy"] = false, ["action"] = "", ["repair_busy"] = busy, ["blocked"] = false, ["auth_saved"] = false,
                    ["provider"] = "none", ["provider_configured"] = false,
                    ["domain_repair"] = available, ["runner_check"] = services.Repair?.RunnerCheck is not null,
                    ["message"] = (available ? "Domain-move repairs run in this instance. " : "Source repair is not available in this host. ") + NoProvider,
                    ["isolation"] = new JsonObject
                    {
                        ["available"] = false, ["reason"] = "not-checked",
                        ["message"] = "Domain-move repairs do not run source code, so they need no isolation check. Code rebuilds are not available yet.",
                    },
                });
            }))
            .WithName("getRepairControl")
            .WithSummary("In-app repair state: domain repair availability, whether a repair is running, AI provider (not configured until Connections) and the runner check.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapGet("/source-maintenance/provider", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Answer(new JsonObject
                {
                    ["provider"] = "none", ["model"] = "not configured", ["configured"] = false, ["state"] = "not-configured", ["message"] = NoProvider,
                });
            }))
            .WithName("getRepairProvider")
            .WithSummary("The repair AI provider; answers state not-configured until AI providers arrive with Connections.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/source-maintenance/provider/test", (Func<HttpContext, IResult>)(_ => Problem(409, NoProvider)))
            .WithName("testRepairProvider")
            .WithSummary("Test the repair AI provider (409 while none is configured).")
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/source-maintenance/control/{action}", (Func<HttpContext, Task<IResult>>)(context => Guard(context, async () =>
            {
                var action = Route(context, "action");
                if (action is "check" or "prepare")
                {
                    if (services.Repair?.RunnerCheck is not { } check)
                        return Problem(409, "This host has no repair runner self-check. Domain-move repairs need no runner.");
                    var result = await check(context.RequestAborted).ConfigureAwait(false);
                    result["action"] ??= action;
                    result["busy"] ??= false;
                    return Answer(result, 202);
                }
                if (AccountActions.Contains(action)) return Problem(409, NoProvider);
                return Problem(404, "Unknown repair action");
            })))
            .WithName("runRepairControl")
            .WithSummary("Repair control actions: check (or prepare) runs the repair runner self-check; account actions answer 409 until an AI provider is configured.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        // ------------------------------------------------------------ raw code (SourceRawEndpoints handlers)

        var rawRoutes = services.Repair?.Raws;
        var rawHandler = rawRoutes is null ? null
            : new SourceRawEndpoints.Handler(rawRoutes.Service, rawRoutes.Installed, rawRoutes.Reconstruction, rawRoutes.AdminLock, rawRoutes.Io, rawRoutes.History);
        Task<IResult> RawWrap(HttpContext context, Func<SourceRawEndpoints.Handler, Task<IResult>> action) =>
            rawHandler is { } handler ? Guard(context, () => action(handler))
                : Task.FromResult(Problem(503, "Source-code storage is not available in this host."));
        var raw = SourceRawEndpoints.MapRoutes(group, "/source-maintenance/raws", () => rawHandler, RawWrap);
        raw[0].WithName("getSourceRaw")
            .WithSummary("Raw-code state of an installed source (needs-input, acquiring, available, reconstructing); adopts retained repair code that matches the installed binary.")
            .Produces<JsonObject>(StatusCodes.Status200OK).Produces<ProblemResponse>(StatusCodes.Status404NotFound).Produces<ProblemResponse>(StatusCodes.Status409Conflict);
        raw[1].WithName("uploadSourceRawProject")
            .WithSummary("Upload the source project ZIP of an installed source (headers X-Raw-Revision, X-Source-Version, optional X-Source-Module; at most 32 MiB). Kept private.")
            .Produces<JsonObject>(StatusCodes.Status200OK).Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict).Produces<ProblemResponse>(StatusCodes.Status413PayloadTooLarge);
        raw[2].Accepts<JsonObject>("application/json").WithName("requestSourceRawInput")
            .WithSummary("Ask for new source code input again (body: revision).")
            .Produces<JsonObject>(StatusCodes.Status200OK).Produces<ProblemResponse>(StatusCodes.Status409Conflict);
        raw[3].WithName("downloadSourceRawCode")
            .WithSummary("Download the retained source code of an installed source as a portable ZIP.")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip").Produces<ProblemResponse>(StatusCodes.Status409Conflict);
        raw[4].Accepts<JsonObject>("application/json").WithName("setSourceRawOrigin")
            .WithSummary("Fetch the source code from its original public repository (body: revision, origin {repository, module, revision, ecosystem}).")
            .Produces<JsonObject>(StatusCodes.Status202Accepted).Produces<ProblemResponse>(StatusCodes.Status400BadRequest).Produces<ProblemResponse>(StatusCodes.Status409Conflict);
        raw[5].Accepts<JsonObject>("application/json").WithName("discoverSourceRawOrigin")
            .WithSummary("Look up the source's repository in the configured indexes (body: revision); runs in the background.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted).Produces<ProblemResponse>(StatusCodes.Status400BadRequest).Produces<ProblemResponse>(StatusCodes.Status409Conflict);
        raw[6].Accepts<JsonObject>("application/json").WithName("reconstructSourceRaw")
            .WithSummary("The code is unavailable: rebuild the source from its installed binary (body: revision, optional job_id). Needs an AI provider; 409 until Connections.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted).Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        // ------------------------------------------------------------ repair jobs (domain repairs on the job queue)

        group.MapPost("/source-maintenance/jobs", (Func<HttpContext, Task<IResult>>)(context => Guard(context, async () =>
            {
                if (!Integrated()) return Problem(409, "Source repair requires the integrated engine");
                if (services.SourceRepair is null) return Problem(503, "Source repairs are not available in this host.");
                var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
                var package = body["pkg_name"] is JsonValue name && name.TryGetValue<string>(out var text) ? text.Trim() : "";
                if (!DomainRepair.IsPackageName(package)) return Problem(400, "Select an installed source package (pkg_name)");
                var options = Options(body);
                var handle = QueueRepair(services, package, options, "manual");
                return Answer(new JsonObject
                {
                    ["id"] = handle.Id, ["task_id"] = handle.Id, ["queued"] = true, ["merged"] = handle.Merged, ["package"] = package,
                    ["kind"] = "domain", ["state"] = "queued",
                }, 202);
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("createSourceRepairJob")
            .WithSummary("Start a domain-move repair of an installed source (body: pkg_name, optional new_host, old_host, query, https) as a job; answers the job's task id.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/source-maintenance/jobs/{jobId}", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (Find(services, Route(context, "jobId")) is not { } job) return Problem(404, "Repair job not found");
                return Answer(Details(job));
            }))
            .WithName("getSourceRepairJob")
            .WithSummary("A repair job: its task (state, progress, message) and the matching repair report (migration, structure and content checks, rollback).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/source-maintenance/jobs/{jobId}/inputs", (Func<HttpContext, Task<IResult>>)(context => Guard(context, async () =>
            {
                if (Find(services, Route(context, "jobId")) is not { } job) return Problem(404, "Repair job not found");
                var report = job.Report;
                var sha = report?["kept"]?["sha256"] is JsonValue value && value.TryGetValue<string>(out var kept) ? kept : "";
                if (services.SourceRepair is null || !Sha256.IsMatch(sha)) return Problem(404, "This repair kept no input jar");
                var bytes = await ReadKeptAsync(services.SourceRepair.Domain, job.Package, sha, context.RequestAborted).ConfigureAwait(false);
                if (bytes is null) return Problem(404, "The kept input jar is no longer retained");
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(bytes, "application/zip", "AutoSourcarr-repair-inputs-" + job.Id + ".jar", enableRangeProcessing: false);
            })))
            .WithName("downloadSourceRepairInputs")
            .WithSummary("Download the working jar that was kept before the repair (a JAR is a ZIP).")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/source-maintenance/jobs/{jobId}/code", (Func<HttpContext, Task<IResult>>)(context => Guard(context, async () =>
            {
                if (Find(services, Route(context, "jobId")) is not { } job) return Problem(404, "Repair job not found");
                var report = job.Report;
                var sha = report?["installed_sha256"] is JsonValue value && value.TryGetValue<string>(out var installed) ? installed : "";
                if (services.SourceRepair is null || !Sha256.IsMatch(sha)) return Problem(404, "This job installed no repaired jar");
                var bytes = await ReadKeptAsync(services.SourceRepair.Domain, job.Package, sha, context.RequestAborted).ConfigureAwait(false);
                if (bytes is null && services.Repair?.ReadInstalledJar is { } read)
                {
                    var current = await read(job.Package, context.RequestAborted).ConfigureAwait(false);
                    if (DomainMigration.Sha(current) == sha) bytes = current;
                }
                if (bytes is null) return Problem(404, "The repaired jar is no longer installed or kept");
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(bytes, "application/zip", "AutoSourcarr-source-code-" + job.Id + ".jar", enableRangeProcessing: false);
            })))
            .WithName("downloadSourceRepairCode")
            .WithSummary("Download the repaired jar a domain repair installed (kept copy, or the installed jar when its digest still matches); 404 otherwise.")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/source-maintenance/jobs/{jobId}/cancel", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (Find(services, Route(context, "jobId")) is not { } job) return Problem(404, "Repair job not found");
                if (!await services.Tasks!.RequestCancelAsync(job.Id).ConfigureAwait(false))
                    return Problem(409, "This repair is not running or cannot be cancelled safely");
                return Answer(new JsonObject { ["ok"] = true, ["id"] = job.Id, ["task_id"] = job.Id, ["cancel_requested"] = true }, 202);
            }))
            .WithName("cancelSourceRepairJob")
            .WithSummary("Cancel a queued or running repair job; a repair that already changed the source rolls back itself.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/source-maintenance/jobs/{jobId}/install", (Func<HttpContext, IResult>)(context =>
            {
                if (Find(services, Route(context, "jobId")) is not { } job) return Problem(404, "Repair job not found");
                if (!Integrated()) return Problem(409, "Integrated engine required");
                if (job.Kind != "rollback" && RepairState(job.Row, job.Report) == "repaired")
                    return Answer(new JsonObject
                    {
                        ["installed"] = true, ["queued"] = false, ["id"] = job.Id,
                        ["message"] = "This repair installed itself after its content check passed; nothing else to install.",
                        ["report"] = job.Report?.DeepClone(),
                    });
                return Problem(409, "Only a finished, repaired job is installed. Domain repairs install themselves when their content check passes.");
            }))
            .WithName("installSourceRepairJob")
            .WithSummary("Domain repairs install themselves: for a repaired job this answers 200 with its report and changes nothing; otherwise 409.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/source-maintenance/jobs/{jobId}/discard", (Func<HttpContext, Task<IResult>>)(context => Guard(context, () =>
            {
                if (Find(services, Route(context, "jobId")) is not { } job) return Task.FromResult(Problem(404, "Repair job not found"));
                var state = RepairState(job.Row, job.Report);
                if (state is "queued" or "waiting" or "running") return Task.FromResult(Problem(409, "This repair is still running; cancel it instead"));
                if (job.Kind == "rollback" || state != "repaired")
                    return Task.FromResult(Answer(new JsonObject
                    {
                        ["discarded"] = true, ["queued"] = false, ["id"] = job.Id, ["message"] = "This job left no repair installed; nothing was changed.",
                    }));
                if (!Integrated()) return Task.FromResult(Problem(409, "Integrated engine required"));
                var handle = QueueRollback(services, job.Package, "manual");
                return Task.FromResult(Answer(new JsonObject
                {
                    ["discarded"] = true, ["queued"] = true, ["id"] = job.Id, ["task_id"] = handle.Id, ["merged"] = handle.Merged,
                    ["message"] = "Restoring the jar kept before this repair.",
                }, 202));
            })))
            .WithName("discardSourceRepairJob")
            .WithSummary("Discard a repair: when it installed a repaired jar, the jar kept before it is installed again (a rollback job); otherwise nothing changes.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/source-maintenance/jobs/{jobId}/feedback", (Func<HttpContext, Task<IResult>>)(context => Guard(context, async () =>
            {
                if (Find(services, Route(context, "jobId")) is not { } job) return Problem(404, "Repair job not found");
                var body = await ApiJson.ReadObjectAsync(context.Request, 8192).ConfigureAwait(false);
                var action = body["action"] is JsonValue value && value.TryGetValue<string>(out var chosen) ? chosen : "";
                if (action is not ("fixed" or "retry" or "revert")) return Problem(400, "Select Fixed, Retry or Revert");
                if (body["feedback"] is not null && !(body["feedback"] is JsonValue note && note.TryGetValue<string>(out _)))
                    return Problem(400, "feedback must be text");
                if (job.Kind == "rollback" || RepairState(job.Row, job.Report) != "repaired" || !job.Current)
                    return Problem(409, "Only the current installed repair takes feedback; refresh Tasks");
                var installed = job.Report?["installed_sha256"]?.GetValue<string>() ?? "";
                if (body["installed_sha256"]?.ToString() != installed) return Problem(409, "Source binary changed; refresh before acting");
                var text = body["feedback"]?.GetValue<string>() ?? "";
                if (text.Length > FeedbackTextLimit) text = text[..FeedbackTextLimit];
                AppendFeedback(services.Repair?.MaintenanceRoot ?? throw new ApiProblem(503, "Repair feedback is not available in this host."), new JsonObject
                {
                    ["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), ["task_id"] = job.Id, ["package"] = job.Package,
                    ["action"] = action, ["installed_sha256"] = installed, ["feedback"] = text,
                });
                if (action == "revert")
                {
                    if (!Integrated()) return Problem(409, "Integrated engine required");
                    var handle = QueueRollback(services, job.Package, "manual");
                    return Answer(new JsonObject { ["recorded"] = true, ["queued"] = true, ["task_id"] = handle.Id, ["merged"] = handle.Merged }, 202);
                }
                return Answer(new JsonObject
                {
                    ["recorded"] = true, ["queued"] = false, ["action"] = action,
                    ["message"] = action == "fixed" ? "Recorded as working."
                        : "Recorded. A domain repair cannot retry by itself: use Revert, or start a new repair with the correct new address.",
                }, 202);
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("sendSourceRepairFeedback")
            .WithSummary("Feedback on the current installed repair (action fixed, retry or revert; installed_sha256 must match). Kept in feedback.jsonl (bounded); revert queues a rollback.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);
    }

    // ------------------------------------------------------------------ jobs

    /// <summary>Queues a domain repair (same job type and key as /source-repairs/{pkg}/domain, so requests merge).</summary>
    internal static JobHandle QueueRepair(ApiV1Services services, string package, DomainRepairOptions options, string origin)
    {
        var domain = services.SourceRepair?.Domain ?? throw new ApiProblem(503, "Source repairs are not available in this host.");
        var jobs = services.Jobs ?? throw new ApiProblem(503, "The job queue is not available in this host.");
        try
        {
            return jobs.Enqueue(SourceRepairV1Endpoints.DomainRepairJob, package, async job =>
            {
                var report = await domain.RunAsync(package, options, text => job.Report(message: text), job.CancellationToken).ConfigureAwait(false);
                var message = report["message"]?.GetValue<string>() ?? "";
                if (report["state"]?.GetValue<string>() != "repaired") throw new InvalidOperationException(message);
                job.Result(message);
            }, origin: origin, title: "Repair source domain " + package, context: package);
        }
        catch (Exception error) when (error is ObjectDisposedException or ArgumentException)
        { throw new ApiProblem(503, "The job queue is stopping; nothing was started."); }
    }

    /// <summary>Queues the rollback to the last kept working jar (same job type and key as /source-repairs/{pkg}/rollback).</summary>
    internal static JobHandle QueueRollback(ApiV1Services services, string package, string origin)
    {
        var domain = services.SourceRepair?.Domain ?? throw new ApiProblem(503, "Source repairs are not available in this host.");
        var jobs = services.Jobs ?? throw new ApiProblem(503, "The job queue is not available in this host.");
        if (!DomainRepair.IsPackageName(package)) throw new ApiProblem(400, "A plain extension package name is required");
        if (!Directory.Exists(domain.LastWorkingDirectory(package))) throw new ApiProblem(404, "No working jar of " + package + " has been kept");
        try
        {
            return jobs.Enqueue(SourceRepairV1Endpoints.RollbackJob, package, async job =>
            {
                job.Report(message: "Installing the last kept jar of " + package);
                var report = await domain.RollbackAsync(package, job.CancellationToken).ConfigureAwait(false);
                var message = report["message"]?.GetValue<string>() ?? "";
                if (report["state"]?.GetValue<string>() != "rolled_back") throw new InvalidOperationException(message);
                job.Result(message);
            }, origin: origin, title: "Restore source jar " + package, context: package);
        }
        catch (Exception error) when (error is ObjectDisposedException or ArgumentException)
        { throw new ApiProblem(503, "The job queue is stopping; nothing was started."); }
    }

    private static DomainRepairOptions Options(JsonObject body)
    {
        foreach (var name in new[] { "new_host", "old_host", "query" })
            if (body[name] is not null && !(body[name] is JsonValue item && item.TryGetValue<string>(out _))) throw new ApiProblem(400, name + " must be a string");
        if (body["https"] is not null && !(body["https"] is JsonValue flag && flag.TryGetValue<bool>(out _))) throw new ApiProblem(400, "https must be a boolean");
        string? Optional(string name) => body[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length > 0 ? text.Trim() : null;
        var options = new DomainRepairOptions(Optional("new_host"), Optional("old_host"), Optional("query"),
            body["https"] is JsonValue https && https.TryGetValue<bool>(out var upgrade) && upgrade);
        if (options.NewHost is { } newHost)
        {
            try { DomainMigration.NormalizeHost(newHost); }
            catch (DomainMigrationException error) { throw new ApiProblem(400, error.Message); }
        }
        return options;
    }

    private sealed record RepairJob(string Id, string Kind, string Package, JsonObject Row, JsonObject? Report, bool Current);

    /// <summary>A repair task by id (task ids are the job ids), with the package's last report when it belongs to this task.</summary>
    private static RepairJob? Find(ApiV1Services services, string id)
    {
        if (!JobId.IsMatch(id) || services.Tasks?.Find(id) is not { } record || !RepairKinds.TryGetValue(record.Kind, out var kind)) return null;
        var task = JsonSerializer.SerializeToNode(record, TaskRegistry.JsonOptions)!.AsObject();
        var package = record.Context;
        var report = DomainRepair.IsPackageName(package) ? LastReport(services, package) : null;
        var matching = Matches(task, kind, report) ? report : null;
        return new RepairJob(id, kind, package, task, matching, matching is not null && Latest(services, record));
    }

    /// <summary>Whether no newer repair task of the same package exists (the last report then describes this one).</summary>
    private static bool Latest(ApiV1Services services, TaskRecord record)
    {
        if (services.Tasks is null) return false;
        var items = services.Tasks.Snapshot()["items"] as JsonArray ?? [];
        return !items.OfType<JsonObject>().Any(row => Text(row, "id") != record.Id && RepairKinds.ContainsKey(Text(row, "kind"))
            && Text(row, "context") == record.Context && string.CompareOrdinal(Text(row, "created_at"), record.CreatedAt) > 0);
    }

    private static JsonObject? LastReport(ApiV1Services services, string package)
    {
        if (services.SourceRepair is null) return null;
        try { return services.SourceRepair.Domain.LastReport(package); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or DomainRepairException) { return null; }
    }

    /// <summary>The package's last report belongs to a task when its kind matches and it started while the task existed.</summary>
    private static bool Matches(JsonObject task, string kind, JsonObject? report)
    {
        if (report is null) return false;
        var expected = kind == "rollback" ? "domain-repair-rollback" : "domain-repair";
        if (Text(report, "kind") != expected) return false;
        if (!Time(Text(report, "started_at"), out var started) || !Time(Text(task, "created_at"), out var created)) return false;
        if (started < created - TimeSpan.FromSeconds(5)) return false;
        return !Time(Text(task, "finished_at"), out var finished) || started <= finished + TimeSpan.FromSeconds(5);
    }

    /// <summary>What the repair pages show: queued/waiting/running while the job runs, then the report's outcome.</summary>
    internal static string RepairState(JsonObject task, JsonObject? report)
    {
        var state = Text(task, "state");
        if (state == "running")
        {
            var activity = Text(task, "activity");
            return activity is "queued" or "waiting" ? activity : "running";
        }
        if (task["cancel_requested"] is JsonValue cancelled && cancelled.TryGetValue<bool>(out var wasCancelled) && wasCancelled) return "cancelled";
        if (task["interrupted"] is JsonValue interrupted && interrupted.TryGetValue<bool>(out var wasInterrupted) && wasInterrupted) return "interrupted";
        if (report is not null && Text(report, "state") is { Length: > 0 } outcome && outcome != "running") return outcome.Replace('_', '-');
        return state == "completed" ? "completed" : "failed";
    }

    private static JsonObject Details(RepairJob job)
    {
        var task = job.Row;
        var result = new JsonObject
        {
            ["id"] = job.Id, ["task_id"] = job.Id, ["kind"] = job.Kind, ["job_type"] = Text(task, "kind"), ["package"] = job.Package,
            ["name"] = Text(task, "name"), ["state"] = RepairState(task, job.Report), ["task_state"] = Text(task, "state"),
            ["activity"] = Text(task, "activity"), ["message"] = Text(task, "message"), ["error"] = Text(task, "error"),
            ["origin"] = Text(task, "origin"), ["created_at"] = Text(task, "created_at"), ["started_at"] = Text(task, "started_at"),
            ["finished_at"] = task["finished_at"]?.DeepClone(), ["cancellable"] = task["cancellable"]?.DeepClone() ?? JsonValue.Create(false),
            ["current"] = job.Current, ["address_migrations"] = new JsonArray(),
            ["report"] = job.Report?.DeepClone(),
        };
        if (job.Report?["installed_sha256"] is { } installed) result["candidate_sha256"] = installed.DeepClone();
        return result;
    }

    private static async Task<byte[]?> ReadKeptAsync(DomainRepair domain, string package, string sha, CancellationToken cancellationToken)
    {
        var path = Path.Combine(domain.LastWorkingDirectory(package), sha + ".jar");
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length > AutoSourcarr.Core.Sources.SuwayomiClient.MaximumExtensionJar) return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return DomainMigration.Sha(bytes) == sha ? bytes : null;
    }

    private static int RunningRepairs(ApiV1Services services) =>
        (services.Tasks?.Snapshot()["items"] as JsonArray ?? []).OfType<JsonObject>()
            .Count(row => Text(row, "state") == "running" && RepairKinds.ContainsKey(Text(row, "kind")));

    // ------------------------------------------------------------------ task projection (REP-7)

    /// <summary>
    /// REP-7: repair jobs in GET /api/v1/tasks. Rows of the repair kinds become kind <c>source-repair</c> (the Tasks →
    /// Repairs list), keep their job kind in <c>repair_job_type</c>, and gain the package, a readable repair state from the
    /// matching report, and the actions the repair list offers.
    /// </summary>
    internal static JsonObject ProjectTasks(JsonObject snapshot, ApiV1Services services)
    {
        if (snapshot["items"] is not JsonArray items) return snapshot;
        var rows = items.OfType<JsonObject>().Where(row => RepairKinds.ContainsKey(Text(row, "kind"))).ToList();
        var reports = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        var newest = rows.GroupBy(row => Text(row, "context"), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(row => Text(row, "created_at")).Max(StringComparer.Ordinal) ?? "", StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var jobKind = Text(row, "kind");
            var kind = RepairKinds[jobKind];
            var package = Text(row, "context");
            JsonObject? report = null;
            if (DomainRepair.IsPackageName(package))
            {
                if (!reports.TryGetValue(package, out report)) reports[package] = report = LastReport(services, package);
                if (!Matches(row, kind, report)) report = null;
            }
            var state = RepairState(row, report);
            var current = report is not null && kind != "rollback" && state == "repaired" && newest.GetValueOrDefault(package) == Text(row, "created_at");
            var installed = report?["installed_sha256"] is JsonValue sha && sha.TryGetValue<string>(out var digest) ? digest : "";
            var kept = report?["kept"]?["sha256"] is JsonValue keptSha && keptSha.TryGetValue<string>(out var keptDigest) && Sha256.IsMatch(keptDigest);
            row["repair_job_type"] = jobKind;
            row["kind"] = "source-repair";
            row["repair_kind"] = kind;
            row["repair_id"] = Text(row, "id");
            row["package"] = package;
            row["repair_state"] = state;
            row["repair_outcome"] = report is null ? null : report["message"]?.DeepClone();
            row["repair_is_current"] = current;
            row["repair_can_feedback"] = current;
            row["repair_can_revert"] = current && kept;
            row["repair_inputs_available"] = kept;
            row["repair_reviewable"] = false;
            row["repair_history_revision"] = 0;
            row["installed_sha256"] = current ? installed : "";
            row["address_migration_count"] = 0;
        }
        snapshot["source_repairs"] = new JsonObject
        {
            ["stale"] = false, ["in_app"] = true, ["count"] = rows.Count,
            ["running"] = rows.Count(row => Text(row, "state") == "running"),
        };
        return snapshot;
    }

    // ------------------------------------------------------------------ feedback

    /// <summary>Appends one feedback line to &lt;root&gt;/feedback.jsonl; the file stays under <see cref="FeedbackFileLimit"/> by dropping the oldest lines.</summary>
    internal static void AppendFeedback(string root, JsonObject entry)
    {
        var line = entry.ToJsonString() + "\n";
        var size = Encoding.UTF8.GetByteCount(line);
        if (size > FeedbackFileLimit / 4) throw new ApiProblem(413, "Feedback is too large");
        lock (FeedbackGate)
        {
            var path = Path.Combine(Path.GetFullPath(root), "feedback.jsonl");
            var info = new FileInfo(path);
            if (info.LinkTarget is not null) throw new IOException("feedback.jsonl must be a regular file, not a link.");
            var lines = new List<string>();
            // A file that is somehow much larger than the bound is started again rather than read whole.
            if (info.Exists && info.Length <= FeedbackFileLimit * 2L)
                lines.AddRange(File.ReadAllText(path, Encoding.UTF8).Split('\n', StringSplitOptions.RemoveEmptyEntries));
            var kept = new List<string>();
            long total = size;
            for (var index = lines.Count - 1; index >= 0; index--)
            {
                var length = Encoding.UTF8.GetByteCount(lines[index]) + 1;
                if (total + length > FeedbackFileLimit) break;
                total += length;
                kept.Add(lines[index]);
            }
            kept.Reverse();
            var builder = new StringBuilder();
            foreach (var item in kept) builder.Append(item).Append('\n');
            builder.Append(line);
            ConfigFiles.WriteAtomic(path, Encoding.UTF8.GetBytes(builder.ToString()));
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string Text(JsonObject value, string key) =>
        value[key] is JsonValue item && item.TryGetValue<string>(out var text) ? text : "";

    private static bool Time(string value, out DateTimeOffset time) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time) && value.Length > 0;
}
