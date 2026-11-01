using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Inventory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the orphan and inventory routes need from the running app (null answers 503).</summary>
public sealed class SourceInventoryServices
{
    public const string SeedResource = "AutoSourcarr.Api.source-orphans-seed.json";
    /// <summary>Orphan labels, archive discovery and archive installation (source_orphans.py).</summary>
    public required SourceOrphans Orphans { get; init; }
    /// <summary>The daily-check switch and the last repair promotion (source_maintenance.py inventory part).</summary>
    public required SourceMaintenanceState Maintenance { get; init; }
    /// <summary>Daily automatic repair of moved sources (REP-6); null when the source host does not run here.</summary>
    public AutoSourcarr.Core.Repair.Migration.AutoRepairWatcher? AutoRepair { get; init; }
    public required SuwayomiClient Client { get; init; }
    public required SemaphoreSlim AdminLock { get; init; }
    /// <summary>Original APK evidence for repairs; null answers 409 until the installed-source reader is wired.</summary>
    public SourceBaselineStore? Baselines { get; init; }
    /// <summary>Records a failure of background work started by a route when no job queue runs it.</summary>
    public Action<Exception>? RecordError { get; init; }

    /// <summary>The bundled seed list (0.4.x source_orphans_seed.json), embedded in this assembly.</summary>
    public static byte[]? BundledSeed()
    {
        using var stream = typeof(SourceInventoryServices).Assembly.GetManifestResourceStream(SeedResource);
        if (stream is null) return null;
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>
/// Phase 6d: the orphan sources page (Installed and Not installed orphan groups, mark/unmark, history discovery and
/// archive installation) and the source-maintenance inventory routes (snapshot, watch, installed, rollback, original
/// APK) under /api/v1. Repairs and their AI flow arrive with the repair runner (phase 6e).
/// </summary>
internal static class SourceInventoryV1Endpoints
{
    private static readonly Regex OriginalPackage = new(@"\Aeu\.kanade\.tachiyomi\.extension\.[a-z0-9]+\.[a-z0-9_]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex ReviewedVersion = new(@"\A[0-9][A-Za-z0-9.+_-]{0,99}\z", RegexOptions.CultureInvariant);

    /// <summary>Exclusive of nothing: reads public repository history (at most four minutes) and saves orphans.json.</summary>
    public static readonly JobType DiscoveryJob = new("source-orphan-discovery", "Collect original source history", JobPriority.Low,
        StallTimeout: TimeSpan.FromMinutes(6));
    /// <summary>Downloads, checks and installs one original archive under the shared source admin lock.</summary>
    public static readonly JobType InstallJob = new("source-orphan-install", "Install archived source", JobPriority.High,
        StallTimeout: TimeSpan.FromMinutes(6));

    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Source orphans are not available in this host.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        bool Integrated() => services.Settings().SourceEngineMode == "integrated";
        bool WorkerConfigured() => !string.IsNullOrWhiteSpace(services.Settings().RepairWorkerUrl);
        string Package(HttpContext context) => context.Request.RouteValues["pkgName"]?.ToString() ?? "";
        async Task<IResult> Guard(Func<Task<IResult>> action)
        {
            try { return await action().ConfigureAwait(false); }
            catch (SourceOrphanException error) { return Problem(error.StatusCode, error.Message); }
            catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
            catch (SourceRawConflictException error) { return Problem(409, error.Message); }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            { return Problem(409, "Source orphan state is unavailable: " + error.Message); }
            catch (Exception error) when (error is SuwayomiException or SourceWorkDeferredException or HttpRequestException)
            { return Problem(503, "The source engine did not answer: " + error.Message); }
        }

        // ------------------------------------------------------------ orphans

        group.MapGet("/source-orphans", (Func<HttpContext, Task<IResult>>)(context => Guard(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services.SourceInventory is not { } inventory) return Task.FromResult(Unavailable());
                // Installed orphans are repaired by the in-app domain repair (integrated engine); there is no external worker.
                var snapshot = inventory.Orphans.Snapshot(services.SourceRepair is not null && Integrated(), ConfiguredRepositories(services), inventory.Maintenance.Operation());
                return Task.FromResult(Answer(snapshot));
            })))
            .WithName("getSourceOrphans")
            .WithSummary("Orphaned sources under two headers, Installed and Not installed, each with its own orphan group (SRC-3); items lists them all, with discovery and installation state.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/source-orphans/refresh", (Func<HttpContext, Task<IResult>>)(_ => Guard(() =>
            {
                if (services.SourceInventory is not { } inventory) return Task.FromResult(Unavailable());
                var (start, answer) = inventory.Orphans.BeginDiscovery();
                if (start) Run(services, inventory, DiscoveryJob, "discovery", "Collect original source history", async (report, token) =>
                {
                    var result = await inventory.Orphans.DiscoverAsync(report, token).ConfigureAwait(false);
                    var message = result["message"]?.GetValue<string>() ?? "";
                    if (result["state"]?.GetValue<string>() == "failed") throw new InvalidOperationException(message);
                    return message;
                }, error => inventory.Orphans.AbandonDiscovery("Discovery could not be queued: " + error.Message));
                return Task.FromResult(Answer(answer, 202));
            })))
            .WithName("refreshSourceOrphans")
            .WithSummary("Collect removed sources from sampled original Keiyoushi history (two years) in the background; at most once a minute.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status429TooManyRequests);

        foreach (var (verb, orphaned) in new[] { ("mark", true), ("unmark", false) })
        {
            group.MapPost("/source-orphans/{pkgName}/" + verb, (Func<HttpContext, Task<IResult>>)(context => Guard(async () =>
                {
                    if (services.SourceInventory is not { } inventory) return Unavailable();
                    var result = await inventory.Orphans.SetOrphanedAsync(Package(context), orphaned, inventory.AdminLock,
                        ConfiguredRepositories(services), context.RequestAborted).ConfigureAwait(false);
                    return Answer(result);
                })))
                .WithName(orphaned ? "markSourceOrphan" : "unmarkSourceOrphan")
                .WithSummary(orphaned ? "Mark an installed source as orphaned (only when it is missing from the configured repositories)."
                    : "Return an installed orphan to the Installed list.")
                .Produces<JsonObject>(StatusCodes.Status200OK)
                .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
                .Produces<ProblemResponse>(StatusCodes.Status409Conflict);
        }

        group.MapPost("/source-orphans/{pkgName}/install", (Func<HttpContext, Task<IResult>>)(context => Guard(() =>
            {
                if (services.SourceInventory is not { } inventory) return Task.FromResult(Unavailable());
                if (!Integrated()) return Task.FromResult(Problem(409, "Integrated engine required"));
                var package = Package(context);
                inventory.Orphans.BeginInstall(package);
                Run(services, inventory, InstallJob, package, "Install archived source " + package, async (_, token) =>
                {
                    var result = await inventory.Orphans.InstallAsync(package, inventory.AdminLock, token).ConfigureAwait(false);
                    var message = result["message"]?.GetValue<string>() ?? "";
                    if (result["state"]?.GetValue<string>() != "installed") throw new InvalidOperationException(message);
                    return message;
                }, error => inventory.Orphans.AbandonInstall("Archive installation could not be queued: " + error.Message));
                return Task.FromResult(Answer(new JsonObject { ["queued"] = true }, 202));
            })))
            .WithName("installSourceOrphan")
            .WithSummary("Install the original archived package of a removed source (digest and package identity checked; never replaces an installed source).")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        // The first repair kind is the domain migration (phase 6e); this route keeps its old address and forwards
        // with 307 so the method and body are kept.
        group.MapPost("/source-orphans/{pkgName}/repair", (Func<string, IResult>)(pkgName =>
                services.SourceRepair is null ? Unavailable()
                    : Results.Redirect("/api/v1/source-repairs/" + Uri.EscapeDataString(pkgName) + "/domain", permanent: false, preserveMethod: true)))
            .WithName("repairSourceOrphan")
            .WithSummary("Repair an installed orphan by moving it to its new web address (forwards to /api/v1/source-repairs/{pkgName}/domain).")
            .Produces(StatusCodes.Status307TemporaryRedirect);

        // ------------------------------------------------------------ source maintenance (inventory part)

        group.MapGet("/source-maintenance", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services.SourceInventory is not { } inventory) return Unavailable();
                var settings = services.Settings();
                var configured = WorkerConfigured();
                var result = new JsonObject
                {
                    ["configured"] = configured, ["reachable"] = false, ["jobs"] = new JsonArray(),
                    ["mode"] = settings.AppServicesMode, ["storage_error"] = "",
                    ["service"] = services.SourceRepair is null
                        ? new JsonObject { ["state"] = "unavailable", ["message"] = "Source repair is not available in this mode." }
                        : new JsonObject { ["state"] = "ready", ["kinds"] = new JsonArray("domain"), ["message"] = "Domain-move repairs run in the app; code rebuilds need the repair runner." },
                    ["operation"] = inventory.Maintenance.PublicOperation(), ["watch"] = inventory.Maintenance.Watch()
                };
                return Answer(result);
            }))
            .WithName("getSourceMaintenance")
            .WithSummary("Source repair status: worker configuration, the daily check switch and the last repair promotion and which repair kinds can run.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPut("/source-maintenance/watch", (Func<HttpContext, Task<IResult>>)(context => Guard(async () =>
            {
                if (services.SourceInventory is not { } inventory) return Unavailable();
                var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
                if (body["enabled"] is not JsonValue value || !value.TryGetValue<bool>(out var enabled)) return Problem(400, "Expected enabled boolean");
                if (enabled && !WorkerConfigured() && services.SourceRepair is null) return Problem(409, "Automatic source repair is not available in this mode.");
                var saved = inventory.Maintenance.SetWatch(enabled);
                if (enabled) inventory.AutoRepair?.Trigger();
                return Answer(saved);
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("setSourceMaintenanceWatch")
            .WithSummary("Turn automatic repair of moved sources on or off (domain repairs need no worker).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/source-maintenance/auto-repair/run", (Func<IResult>)(() =>
            {
                if (services.SourceInventory?.AutoRepair is not { } watcher) return Unavailable();
                watcher.Trigger();
                return Answer(new JsonObject { ["queued"] = true }, 202);
            }))
            .WithName("runSourceAutoRepair")
            .WithSummary("Start an automatic repair pass of moved sources now (REP-6).")
            .Produces<JsonObject>(StatusCodes.Status202Accepted);

        group.MapGet("/source-maintenance/auto-repair", (Func<IResult>)(() =>
                services.SourceInventory?.AutoRepair is { } watcher
                    ? Answer(new JsonObject { ["running"] = watcher.Running, ["state"] = watcher.State() }) : Unavailable()))
            .WithName("getSourceAutoRepair")
            .WithSummary("Last automatic repair pass and the attempts per package.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapGet("/source-maintenance/installed", (Func<HttpContext, Task<IResult>>)(context => Guard(async () =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services.SourceInventory is not { } inventory) return Unavailable();
                var rows = await inventory.Orphans.ReadInventoryAsync(context.RequestAborted).ConfigureAwait(false);
                var installed = rows.Where(row => row["isInstalled"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on)
                    .Select(row => (JsonNode?)row.DeepClone()).ToArray();
                return Answer(new JsonObject { ["items"] = new JsonArray(installed) });
            })))
            .WithName("getInstalledSourceExtensions")
            .WithSummary("Installed source extensions with their package, version, repository and source identities.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/source-maintenance/rollback", (Func<HttpContext, Task<IResult>>)(context => Guard(async () =>
            {
                if (services.SourceInventory is not { } inventory) return Unavailable();
                if (!Integrated()) return Problem(409, "Integrated engine required");
                var body = context.Request.ContentLength is null or 0 && !context.Request.Headers.ContainsKey("Transfer-Encoding")
                    ? new JsonObject() : await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
                string package;
                if (body["pkg_name"] is not null)
                {
                    package = body["pkg_name"] is JsonValue named && named.TryGetValue<string>(out var text) ? text.Trim() : "";
                    if (!AutoSourcarr.Core.Repair.Migration.DomainRepair.IsPackageName(package)) return Problem(400, "pkg_name must be a plain extension package name");
                }
                else
                {
                    // 0.4.x sent no body: the last installed repair record names the package.
                    var operation = inventory.Maintenance.Operation();
                    if (operation.ContainsKey("history_plan")) return Problem(409, "Use this installed repair’s Revert button in Tasks → Repairs");
                    if (operation["state"] is not JsonValue state || !state.TryGetValue<string>(out var name) || name != "installed")
                        return Problem(409, "There is no installed repair to roll back; name the package (pkg_name)");
                    package = (operation["extension"] as JsonObject)?["pkgName"]?.ToString() ?? operation["pkg_name"]?.ToString() ?? "";
                    if (!AutoSourcarr.Core.Repair.Migration.DomainRepair.IsPackageName(package))
                        return Problem(409, "The last repair record names no package; name the package (pkg_name)");
                }
                var handle = RepairV1Endpoints.QueueRollback(services, package, "manual");
                return Answer(new JsonObject { ["queued"] = true, ["task_id"] = handle.Id, ["merged"] = handle.Merged, ["package"] = package }, 202);
            })))
            .Accepts<JsonObject>(true, "application/json")
            .WithName("rollbackSourceRepair")
            .WithSummary("Install the working jar kept before the last repair of a package again, as a job (body pkg_name; without a body, the package of the last installed repair record).")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/source-maintenance/original-apk/{pkgName}", (Func<HttpContext, Task<IResult>>)(context => Guard(async () =>
            {
                if (services.SourceInventory is not { } inventory) return Unavailable();
                if (!Integrated()) return Problem(409, "Original APK evidence requires the integrated engine");
                var package = Package(context);
                if (!OriginalPackage.IsMatch(package)) return Problem(400, "Select the installed original extension package");
                if (context.Request.Headers["X-AutoSourcarr-Original-Apk-Confirmed"].ToString() != "true")
                    return Problem(400, "Confirm that this is the original APK for the selected installed version");
                var version = context.Request.Headers["X-AutoSourcarr-Original-Apk-Version"].ToString();
                if (!ReviewedVersion.IsMatch(version)) return Problem(400, "Refresh installed sources and confirm the displayed version");
                var data = await ApiJson.ReadUploadAsync(context.Request, SourceOrphans.MaximumPackage, "Original APK exceeds 32 MiB",
                    "The original APK upload did not finish in time; choose the file again").ConfigureAwait(false);
                if (inventory.Baselines is not { } baselines)
                    return Problem(409, "Original APK evidence needs the installed-source reader (SourceBaselineStore), which this host does not run. Nothing was saved.");
                await inventory.AdminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                try
                {
                    var rows = await inventory.Orphans.ReadInventoryAsync(context.RequestAborted).ConfigureAwait(false);
                    var extension = rows.FirstOrDefault(row => row["pkgName"]?.ToString() == package && row["isInstalled"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on);
                    if (extension is null) return Problem(404, "Select an installed source package");
                    if (extension["versionName"]?.ToString() != version)
                        return Problem(409, "Installed source version changed. Refresh sources and confirm its original APK again.");
                    try { return Answer(await baselines.AttachOriginalAsync(package, version, data, confirmed: true, context.RequestAborted).ConfigureAwait(false)); }
                    catch (InvalidDataException error) { return Problem(400, error.Message); }
                }
                finally { inventory.AdminLock.Release(); }
            })))
            .WithMetadata(ApiJson.BoundedUploadMetadata)
            .WithName("attachOriginalSourceApk")
            .WithSummary("Keep the original APK of an installed source as repair evidence (headers confirm the original and its version).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);
    }

    /// <summary>The configured repository indexes, or null when unknown (unknown is not proof that a source left them).</summary>
    internal static IReadOnlyCollection<string>? ConfiguredRepositories(ApiV1Services services)
    {
        try
        {
            var list = services.Editor.ExtensionRepositories();
            return list.Count == 0 ? null : list.ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Runs background work as a job (task list, watchdog) when the queue exists, otherwise on the thread pool.</summary>
    private static void Run(ApiV1Services services, SourceInventoryServices inventory, JobType type, string key, string title,
        Func<Action<string>, CancellationToken, Task<string>> work, Action<Exception> refused)
    {
        if (services.Jobs is { } jobs)
        {
            try
            {
                jobs.Enqueue(type, key, async job =>
                {
                    var message = await work(text => job.Report(message: text), job.CancellationToken).ConfigureAwait(false);
                    job.Result(message);
                }, origin: "manual", title: title, context: key);
            }
            catch (Exception error) when (error is ObjectDisposedException or ArgumentException) { refused(error); throw new SourceOrphanException(503, "The job queue is stopping; nothing was started."); }
            return;
        }
        _ = Task.Run(async () =>
        {
            try { await work(_ => { }, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { inventory.RecordError?.Invoke(error); }
        });
    }
}
