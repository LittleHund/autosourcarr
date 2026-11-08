using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Accounts;
using AutoSourcarr.Core.Backup;
using AutoSourcarr.Core.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using static AutoSourcarr.Api.Endpoints.ManualDecisionsV1Endpoints;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the backup routes need (null answers 503).</summary>
public sealed class BackupV1Services
{
    public required InstanceBackupStore Store { get; init; }
    /// <summary>One page of the portable catalogue share export (CatalogueExchange.Export(after, 5000)).</summary>
    public Func<long, JsonObject>? SharePage { get; init; }
    /// <summary>SET-3 old-file removal (absent in safe mode).</summary>
    public OldFileCleanup? Cleanup { get; init; }
    /// <summary>Drops in-memory catalogue caches.</summary>
    public Action? Invalidate { get; init; }
    /// <summary>The app runs in its minimal safe mode after a failed or blocked update.</summary>
    public bool SafeMode { get; init; }
    /// <summary>Tells the app that a restore is armed (changes are refused until the restart) or cancelled.</summary>
    public Action<bool>? RestoreArmed { get; init; }
}

/// <summary>
/// Phase 12 backup and rollback under /api/v1 (0.4.5 /api/admin/instance/backups and /restore): list, create (a job),
/// download, delete, restore preview (upload or a saved backup) and restore apply, which arms the restore for the next
/// start (applied before any database opens; until then changes are refused), its cancellation, the SET-9 notices
/// ("Keep current", "Retry the update", "Migrate without a backup") and SET-3 old-file removal. Every route is
/// admin-level in AccessPolicy; creating, downloading, deleting, restoring and the safe-mode actions also need an owner
/// or admin account (not a script key), as the 0.4.5 instance routes did.
/// </summary>
internal static class BackupV1Endpoints
{
    /// <summary>
    /// 0.5.3: starts at once (long-running: it takes no worker slot and does not wait for running jobs to finish, which
    /// long background jobs never do); the database copy itself drains database work (Quiescent). Cancellable: Cancel in
    /// Tasks, and stopping the app, end it without keeping a partial backup.
    /// </summary>
    public static readonly JobType CreateJob = new("instance-backup", "Create backup", JobPriority.High,
        JobFlags.LongRunning, StallTimeout: TimeSpan.FromHours(1), Cancellable: true);
    public static readonly JobType RetryJob = new("instance-update-retry", "Retry the update", JobPriority.High,
        JobFlags.Exclusive | JobFlags.NeedsDisk, StallTimeout: TimeSpan.FromHours(1), Cancellable: false);

    /// <summary>
    /// Arming a restore, retrying an update and migrating without a backup are a person's explicit choice: when the
    /// instance has accounts, only a signed-in owner or admin account may make it (not the admin token, not an API key).
    /// A token-only instance without accounts keeps the admin token.
    /// </summary>
    internal static bool AccountChoice(HttpContext context, ApiV1Services services)
    {
        if (!MaintenanceV1Endpoints.OwnerOrAdmin(context)) return false;
        if (services.Access?.Accounts.Accounts().Count is not > 0) return true;
        if (!context.Items.ContainsKey(AccessControlMiddleware.CallerItem)) return false;
        var caller = AccessControlMiddleware.CallerOf(context);
        return caller.Role is Role.Owner or Role.Admin && caller.Via is "session" or "basic" or "external";
    }
    private static readonly TimeSpan UploadDeadline = TimeSpan.FromMinutes(60);
    /// <summary>One restore preview at a time (each can stage gigabytes).</summary>
    private static readonly SemaphoreSlim PreviewGate = new(1, 1);

    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, BackupV1Services? backups)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Backups are not available in this host.");
        IResult Forbidden() => Problem(403, "Only the owner or an admin account can create, download, delete or restore backups.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        bool Allowed(HttpContext context) => MaintenanceV1Endpoints.OwnerOrAdmin(context);
        IResult NotAccount() => Problem(403, "Sign in with the owner or an admin account to make this choice; the admin token and API keys cannot.");
        async Task<IResult> Guard(Func<Task<IResult>> action)
        {
            try { return await action().ConfigureAwait(false); }
            catch (BackupRefusedException error) { return Problem(error.Status, error.Message); }
            catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
            catch (KeyNotFoundException error) { return Problem(404, error.Message); }
            catch (ArgumentException error) { return Problem(400, error.Message); }
        }
        void Publish(string change, JsonObject? extra = null)
        {
            var data = extra ?? new JsonObject();
            data["change"] = change;
            services.Events?.Publish("backups", data);
        }
        JsonObject Restart(JsonObject answer)
        {
            var restarting = services.CanRestart && Interlocked.Exchange(ref services.Restarting, 1) == 0;
            if (restarting)
            {
                var stop = services.Stop!;
                _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => stop(), TaskScheduler.Default);
            }
            answer["restarting"] = restarting;
            answer["restart_required"] = true;
            answer["message"] ??= restarting ? "AutoSourcarr restarts now." : "Restart AutoSourcarr (its container) to continue.";
            return answer;
        }

        group.MapGet("/backups", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (backups is null) return Unavailable();
                var store = backups.Store;
                var safe = UpgradeGuard.SafeModeNotice(store);
                // The migrate-without-backup token goes only to an owner or admin who can use it.
                if (safe is not null && !AccountChoice(context, services)) safe.Remove("preview_token");
                return Answer(new JsonObject
                {
                    ["items"] = new JsonArray(store.List().Select(row => (JsonNode?)row.ToJson()).ToArray()),
                    ["update"] = backups.SafeMode ? null : UpgradeGuard.Notice(store),
                    ["safe_mode"] = safe,
                    ["restore"] = store.RestoreNotice(),
                    ["restore_pending"] = store.PendingRestore(),
                    ["limits"] = new JsonObject
                    {
                        ["manual"] = InstanceBackupStore.ManualLimit, ["automatic_kept"] = InstanceBackupStore.AutomaticKeep,
                        ["max_upload_bytes"] = InstanceBackupStore.MaxUploadBytes, ["format_version"] = InstanceBackupStore.FormatVersion,
                    },
                    ["always_excluded"] = new JsonArray(InstanceBackupStore.AlwaysExcluded.Select(name => (JsonNode?)name).ToArray()),
                });
            }))
            .WithName("listBackups")
            .WithSummary("Saved backups (manual, automatic before an update, pre-restore), newest first; the update notice, the safe-mode notice after a failed update, the result of the last restore at start and an armed restore.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/backups", (Func<HttpContext, Task<IResult>>)(context => Guard(async () =>
            {
                if (backups is null) return Unavailable();
                if (!Allowed(context)) return Forbidden();
                if (services.Jobs is not { } jobs) return Problem(503, "Backups are created as jobs; the job queue is not running (safe mode). The automatic backup is listed.");
                var body = await Body(context).ConfigureAwait(false);
                // Never stored or logged: it lives in this job's closure until the file is written.
                var passphrase = body["passphrase"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
                if (passphrase is not null) BackupCrypto.CheckPassphrase(passphrase);
                var includeShare = BodyFlag(body, "share_export");
                var store = backups.Store; var share = backups.SharePage;
                var handle = jobs.Enqueue(CreateJob, "", async job =>
                {
                    await Task.Yield();
                    // 0.5.3: the portable catalogue export is optional (share_export: true); the databases already hold
                    // everything, and the export is slow on a large catalogue.
                    var summary = store.Create("manual", "Created from the Web UI", passphrase, includeShare ? share : null,
                        report: message => job.Report(message: message), cancellationToken: job.CancellationToken);
                    job.Result("Backup created: " + summary.Files.ToString(CultureInfo.InvariantCulture) + " files, "
                        + (summary.Bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB" + (summary.Protected ? ", password protected" : ""));
                    Publish("created", new JsonObject { ["id"] = summary.Id });
                }, origin: "manual", title: "Create backup");
                return Answer(new JsonObject { ["queued"] = true, ["task_id"] = handle.Id, ["merged"] = handle.Merged }, 202);
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("createBackup")
            .WithSummary("Create a backup as a job (body {passphrase?, share_export?}: a passphrase of 12 to 1024 characters protects it with chunked AES-GCM and is never stored; share_export also adds the portable catalogue, which is slow on large catalogues). Starts at once and can be cancelled. Answers 202 with the task id.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status403Forbidden);

        group.MapGet("/backups/{backupId}/download", (Func<HttpContext, IResult>)(context =>
            {
                if (backups is null) return Unavailable();
                if (!Allowed(context)) return Forbidden();
                var id = Route(context, "backupId");
                string path;
                try { path = backups.Store.FilePath(id); }
                catch (KeyNotFoundException error) { return Problem(404, error.Message); }
                context.Response.Headers.CacheControl = "no-store";
                // Streamed from disk; the UI downloads through a plain link (no browser-side buffering).
                return Results.File(path, "application/octet-stream", "autosourcarr-" + id + InstanceBackupStore.Extension, enableRangeProcessing: true);
            }))
            .WithName("downloadBackup")
            .WithSummary("Download one backup file (.ascbackup), streamed.")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapDelete("/backups/{backupId}", (Func<HttpContext, Task<IResult>>)(context => Guard(() =>
            {
                if (backups is null) return Task.FromResult(Unavailable());
                if (!Allowed(context)) return Task.FromResult(Forbidden());
                var id = Route(context, "backupId");
                backups.Store.Delete(id);
                Publish("deleted", new JsonObject { ["id"] = id });
                return Task.FromResult(Answer(new JsonObject { ["deleted"] = id }));
            })))
            .WithName("deleteBackup")
            .WithSummary("Delete one saved backup file.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/backups/restore/preview", (Func<HttpContext, Task<IResult>>)(context => Guard(async () =>
            {
                if (backups is null) return Unavailable();
                if (!Allowed(context)) return Forbidden();
                context.Response.Headers.CacheControl = "no-store";
                // 0.5.3: a running backup holds the store; answer at once instead of waiting for it to finish.
                if (backups.Store.Creating) return Problem(409, "A backup is being created; wait until it finishes or cancel it in Tasks, then preview the restore.");
                if (!await PreviewGate.WaitAsync(0).ConfigureAwait(false)) return Problem(409, "Another backup preview is running; wait until it finishes.");
                try
                {
                    var store = backups.Store;
                    var type = context.Request.ContentType ?? "";
                    JsonObject preview;
                    if (type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                    {
                        var body = await Body(context).ConfigureAwait(false);
                        var id = BodyText(body, "backup_id");
                        if (id.Length == 0) return Problem(400, "Send {backup_id} of a saved backup, or upload a backup file.");
                        var passphrase = body["passphrase"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
                        preview = await Task.Run(() => store.StageSaved(id, passphrase)).ConfigureAwait(false);
                    }
                    else
                    {
                        var upload = store.UploadPath();
                        try
                        {
                            var passphrase = await ReceiveAsync(context, upload).ConfigureAwait(false);
                            if (new FileInfo(upload).Length == 0) return Problem(400, "Choose a backup file.");
                            preview = await Task.Run(() => store.Stage(upload, passphrase, "upload", consume: true)).ConfigureAwait(false);
                        }
                        finally { if (File.Exists(upload)) File.Delete(upload); }
                    }
                    return Answer(preview);
                }
                finally { PreviewGate.Release(); }
            })))
            .WithMetadata(ApiJson.BoundedUploadMetadata)
            .Accepts<JsonObject>("application/json", "multipart/form-data", "application/octet-stream")
            .WithName("previewBackupRestore")
            .WithSummary("Check a backup and show what a restore would change: multipart/form-data with fields passphrase (optional, first) and file, a raw unprotected file, or {backup_id, passphrase?} for a saved one. Streamed to disk. Refuses damaged, tampered, wrong-passphrase and newer-format files (\"Version mismatch: version X required\"). Answers stage_id and preview_token.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status413PayloadTooLarge);

        group.MapPost("/backups/restore/apply", (Func<HttpContext, Task<IResult>>)(context => Guard(async () =>
            {
                if (backups is null) return Unavailable();
                if (!Allowed(context)) return Forbidden();
                if (!AccountChoice(context, services)) return NotAccount();
                var body = await Body(context).ConfigureAwait(false);
                var stage = BodyText(body, "stage_id"); var token = BodyText(body, "preview_token");
                // Hard rule 1: never automatic; the confirm flag and the token of the preview the admin saw.
                if (!BodyFlag(body, "confirm") || stage.Length == 0 || token.Length == 0)
                    return Problem(400, "Confirm the restore: send {\"stage_id\": \"...\", \"preview_token\": \"...\", \"confirm\": true} from its preview.");
                // 0.5.3: stop (and cancel a running backup) first: a backup holds the store until it ends, so arming
                // after it would wait for the whole backup. Nothing armed means the queue resumes.
                if (services.Jobs is { } jobs) await jobs.StopBackgroundAsync("A restore is armed for the next start").ConfigureAwait(false);
                JsonObject armed;
                try { armed = await Task.Run(() => backups.Store.Arm(stage, token)).ConfigureAwait(false); }
                catch { services.Jobs?.ResumeBackground(); throw; }
                backups.RestoreArmed?.Invoke(true);
                Publish("restore-armed");
                return Answer(Restart(new JsonObject
                {
                    ["armed"] = true, ["pending"] = armed,
                    ["message"] = services.CanRestart ? "Restore armed. AutoSourcarr restarts and applies it before opening the databases."
                        : "Restore armed. Restart AutoSourcarr (its container) to apply it; until then changes are refused.",
                }));
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("applyBackupRestore")
            .WithSummary("Arm a previewed restore for the next start (body {stage_id, preview_token, confirm: true}). It is applied before any database opens: a pre-restore backup, then both databases and the settings are swapped with a journal (this instance's secrets kept); a crash or failed check undoes it. Until the restart changes are refused; the integrated image restarts itself.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapDelete("/backups/restore/pending", (Func<HttpContext, IResult>)(context =>
            {
                if (backups is null) return Unavailable();
                if (!Allowed(context)) return Forbidden();
                var cancelled = backups.Store.Disarm();
                backups.RestoreArmed?.Invoke(false);
                services.Jobs?.ResumeBackground();
                Publish("restore-cancelled");
                return Answer(new JsonObject { ["cancelled"] = cancelled });
            }))
            .WithName("cancelBackupRestore")
            .WithSummary("Cancel an armed restore before the restart; changes are accepted again.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/backups/update-notice/dismiss", (Func<HttpContext, IResult>)(context =>
            {
                if (backups is null) return Unavailable();
                if (!Allowed(context)) return Forbidden();
                var dismissed = UpgradeGuard.Dismiss(backups.Store) | backups.Store.DismissRestoreNotice();
                Publish("update-notice-dismissed");
                return Answer(new JsonObject { ["dismissed"] = dismissed });
            }))
            .WithName("dismissBackupUpdateNotice")
            .WithSummary("\"Keep current\": hide the notice of the last update or restore; the backups stay in the list.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/backups/safe-mode/retry", (Func<HttpContext, Task<IResult>>)(context => Guard(async () =>
            {
                if (backups is null) return Unavailable();
                if (!Allowed(context)) return Forbidden();
                if (!AccountChoice(context, services)) return NotAccount();
                var body = await Body(context).ConfigureAwait(false);
                if (!BodyFlag(body, "confirm")) return Problem(400, "Confirm the retry: send {\"confirm\": true}.");
                if (services.Jobs is not { } jobs) return Problem(503, "The job queue is not available in this host.");
                if (UpgradeGuard.Marker(backups.Store) is null) return Problem(409, "No update is waiting to be retried.");
                var store = backups.Store;
                // The fresh backup can take minutes: a job (Tasks shows it), then the restart.
                var handle = jobs.Enqueue(RetryJob, "", async job =>
                {
                    await Task.Yield();
                    job.Report(message: "Taking a fresh backup before the retry");
                    var backup = UpgradeGuard.Retry(store);
                    Publish("update-retry", new JsonObject { ["backup_id"] = backup.Id });
                    var answer = Restart(new JsonObject());
                    job.Result("Fresh backup " + backup.CreatedAt + " taken; " + (answer["restarting"]?.GetValue<bool>() == true
                        ? "AutoSourcarr restarts and runs the update again." : "restart AutoSourcarr to run the update again."));
                }, origin: "manual", title: "Retry the update");
                return Answer(new JsonObject
                {
                    ["queued"] = true, ["task_id"] = handle.Id, ["merged"] = handle.Merged,
                    ["message"] = "Taking a fresh backup (see Tasks); then the next start runs the update again.",
                }, 202);
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("retryBackupUpdate")
            .WithSummary("\"Retry the update\" after a rolled-back or blocked update, as a job (202 + task id): takes a fresh backup, then the next start migrates again (body {confirm: true}). Needs an owner or admin account when the instance has accounts.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status403Forbidden)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/backups/safe-mode/migrate-without-backup", (Func<HttpContext, Task<IResult>>)(context => Guard(async () =>
            {
                if (backups is null) return Unavailable();
                if (!Allowed(context)) return Forbidden();
                if (!AccountChoice(context, services)) return NotAccount();
                var body = await Body(context).ConfigureAwait(false);
                var token = BodyText(body, "preview_token");
                if (!BodyFlag(body, "confirm") || token.Length == 0)
                    return Problem(400, "Confirm: send {\"confirm\": true, \"preview_token\": \"...\"} from the safe-mode notice.");
                UpgradeGuard.AllowWithoutBackup(backups.Store, token);
                Publish("update-without-backup");
                return Answer(Restart(new JsonObject { ["allowed"] = true, ["message"] = "The next start migrates without a backup." }));
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("migrateWithoutBackup")
            .WithSummary("The owner's explicit choice after the automatic backup failed: the next start migrates without a backup (body {confirm: true, preview_token} from the safe-mode notice).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapGet("/maintenance/old-files", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (backups?.Cleanup is not { } cleanup) return Unavailable();
                if (!Allowed(context)) return Forbidden();
                var preview = cleanup.Preview();
                preview["last"] = cleanup.Last();
                return Answer(preview);
            }))
            .WithName("previewOldFileCleanup")
            .WithSummary("SET-3: old files the app itself left (old reports, orphaned bytecode, abandoned temporary files and previews, expired trash) and the preview_token to clean them.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/maintenance/old-files", (Func<HttpContext, Task<IResult>>)(context => Guard(async () =>
            {
                if (backups?.Cleanup is not { } cleanup) return Unavailable();
                if (!Allowed(context)) return Forbidden();
                var body = await Body(context).ConfigureAwait(false);
                var token = BodyText(body, "preview_token");
                if (!BodyFlag(body, "confirm") || token.Length == 0)
                    return Problem(400, "Confirm the cleanup: send {\"confirm\": true, \"preview_token\": \"...\"} from its preview.");
                return Answer(cleanup.Apply(token));
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("applyOldFileCleanup")
            .WithSummary("Move the previewed old files to trash/<date> under the data folder and delete expired trash (body {confirm: true, preview_token}).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Streams the upload to <paramref name="target"/> (size limit, free space, one absolute deadline) and answers the
    /// passphrase from a multipart field (never a header: proxies may log headers).
    /// </summary>
    private static async Task<string?> ReceiveAsync(HttpContext context, string target)
    {
        var request = context.Request;
        if (request.ContentLength > InstanceBackupStore.MaxUploadBytes) throw new ApiProblem(413, "The backup is larger than the upload limit.");
        if (request.Headers.ContainsKey("X-Backup-Passphrase"))
            throw new ApiProblem(400, "Send the passphrase as the multipart field \"passphrase\", not as a header.");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { } feature)
        {
            if (feature.IsReadOnly) throw new ApiProblem(409, "Upload request body already started; choose the file again.");
            feature.MaxRequestBodySize = null;
        }
        using var deadline = new CancellationTokenSource(UploadDeadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
        try
        {
            if (!(request.ContentType ?? "").StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                await CopyAsync(request.Body, target, linked.Token).ConfigureAwait(false);
                return null;
            }
            var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(request.ContentType).Boundary).Value;
            if (string.IsNullOrEmpty(boundary)) throw new ApiProblem(400, "The multipart upload has no boundary.");
            var reader = new MultipartReader(boundary, request.Body) { HeadersLengthLimit = 16 * 1024 };
            string? passphrase = null; var received = false;
            while (await reader.ReadNextSectionAsync(linked.Token).ConfigureAwait(false) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)) continue;
                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                if (name == "passphrase")
                {
                    using var text = new StreamReader(section.Body, new UTF8Encoding(false, true));
                    var buffer = new char[1025];
                    var length = await text.ReadBlockAsync(buffer.AsMemory(), linked.Token).ConfigureAwait(false);
                    if (length > 1024) throw new ApiProblem(400, "The passphrase is longer than 1024 characters.");
                    passphrase = length == 0 ? null : new string(buffer, 0, length);
                }
                else if (name == "file" && !received)
                {
                    await CopyAsync(section.Body, target, linked.Token).ConfigureAwait(false);
                    received = true;
                }
            }
            if (!received) throw new ApiProblem(400, "The multipart upload has no \"file\" field.");
            return passphrase;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        { throw new ApiProblem(408, "The backup upload did not finish in time; choose the file again."); }
        catch (InvalidDataException error) { throw new ApiProblem(400, "The multipart upload could not be read: " + error.Message); }
    }

    private static async Task CopyAsync(Stream input, string target, CancellationToken token)
    {
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > InstanceBackupStore.MaxUploadBytes) throw new ApiProblem(413, "The backup is larger than the upload limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        await output.FlushAsync(token).ConfigureAwait(false);
    }
}
