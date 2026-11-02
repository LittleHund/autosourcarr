using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Backup;
using AutoSourcarr.Core.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using static AutoSourcarr.Api.Endpoints.ManualDecisionsV1Endpoints;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the backup routes need (null answers 503).</summary>
public sealed class BackupV1Services
{
    public required InstanceBackupStore Store { get; init; }
    /// <summary>One page of the portable catalogue share export (CatalogueExchange.Export(after, 5000)).</summary>
    public Func<long, JsonObject>? SharePage { get; init; }
    /// <summary>SET-3 old-file removal.</summary>
    public OldFileCleanup? Cleanup { get; init; }
    /// <summary>Drops in-memory catalogue caches after a restore.</summary>
    public Action? Invalidate { get; init; }
}

/// <summary>
/// Phase 12 backup and rollback under /api/v1 (0.4.5 /api/admin/instance/backups and /restore): list, create (a job),
/// download, delete, restore preview (upload or a saved backup) and restore apply (preview token + confirm, a job), the
/// update notice of SET-9 ("Keep current") and SET-3 old-file removal (preview + confirm). Every route is admin-level in
/// AccessPolicy; creating, downloading, deleting and restoring also need an owner or admin account (not a script key),
/// as the 0.4.5 instance routes did.
/// </summary>
internal static class BackupV1Endpoints
{
    public static readonly JobType CreateJob = new("instance-backup", "Create backup", JobPriority.High,
        JobFlags.Exclusive | JobFlags.NeedsDisk, StallTimeout: TimeSpan.FromHours(1), Cancellable: false);
    public static readonly JobType RestoreJob = new("instance-restore", "Restore backup", JobPriority.High,
        JobFlags.Exclusive | JobFlags.NeedsDisk, StallTimeout: TimeSpan.FromHours(2), Cancellable: false);
    private static readonly TimeSpan UploadDeadline = TimeSpan.FromMinutes(15);

    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, BackupV1Services? backups)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Backups are not available in this host.");
        IResult Forbidden() => Problem(403, "Only the owner or an admin account can create, download, delete or restore backups.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
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

        group.MapGet("/backups", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (backups is null) return Unavailable();
                return Answer(new JsonObject
                {
                    ["items"] = new JsonArray(backups.Store.List().Select(row => (JsonNode?)row.ToJson()).ToArray()),
                    ["update"] = UpgradeGuard.Notice(backups.Store),
                    ["limits"] = new JsonObject
                    {
                        ["manual"] = InstanceBackupStore.ManualLimit, ["automatic_kept"] = InstanceBackupStore.AutomaticKeep,
                        ["max_bytes"] = InstanceBackupStore.MaxArchiveBytes, ["format_version"] = InstanceBackupStore.FormatVersion,
                    },
                    ["always_excluded"] = new JsonArray(InstanceBackupStore.AlwaysExcluded.Select(name => (JsonNode?)name).ToArray()),
                });
            }))
            .WithName("listBackups")
            .WithSummary("Saved backups (manual, automatic before an update, pre-restore), newest first; the update notice of SET-9 while it is not dismissed.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/backups", (Func<HttpContext, Task<IResult>>)(context => Guard(async () =>
            {
                if (backups is null) return Unavailable();
                if (!MaintenanceV1Endpoints.OwnerOrAdmin(context)) return Forbidden();
                if (services.Jobs is not { } jobs) return Problem(503, "The job queue is not available in this host.");
                var body = await Body(context).ConfigureAwait(false);
                // Never stored or logged: it lives in this job's closure until the file is written.
                var passphrase = body["passphrase"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
                if (passphrase is not null) BackupCrypto.CheckPassphrase(passphrase);
                var store = backups.Store; var share = backups.SharePage;
                var handle = jobs.Enqueue(CreateJob, "", async job =>
                {
                    await Task.Yield();
                    var summary = store.Create("manual", "Created from the Web UI", passphrase, share, report: message => job.Report(message: message));
                    job.Result("Backup created: " + summary.Files.ToString(CultureInfo.InvariantCulture) + " files, "
                        + (summary.Bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB" + (summary.Protected ? ", password protected" : ""));
                    Publish("created", new JsonObject { ["id"] = summary.Id });
                }, origin: "manual", title: "Create backup");
                return Answer(new JsonObject { ["queued"] = true, ["task_id"] = handle.Id, ["merged"] = handle.Merged }, 202);
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("createBackup")
            .WithSummary("Create a backup as a job (body {passphrase?}: 12 to 1024 characters protects it with AES-GCM; never stored). Answers 202 with the task id.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status403Forbidden);

        group.MapGet("/backups/{backupId}/download", (Func<HttpContext, IResult>)(context =>
            {
                if (backups is null) return Unavailable();
                if (!MaintenanceV1Endpoints.OwnerOrAdmin(context)) return Forbidden();
                var id = Route(context, "backupId");
                string path;
                try { path = backups.Store.FilePath(id); }
                catch (KeyNotFoundException error) { return Problem(404, error.Message); }
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(path, "application/octet-stream", "autosourcarr-" + id + InstanceBackupStore.Extension);
            }))
            .WithName("downloadBackup")
            .WithSummary("Download one backup file (.ascbackup).")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapDelete("/backups/{backupId}", (Func<HttpContext, Task<IResult>>)(context => Guard(() =>
            {
                if (backups is null) return Task.FromResult(Unavailable());
                if (!MaintenanceV1Endpoints.OwnerOrAdmin(context)) return Task.FromResult(Forbidden());
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
                if (!MaintenanceV1Endpoints.OwnerOrAdmin(context)) return Forbidden();
                context.Response.Headers.CacheControl = "no-store";
                var request = context.Request;
                JsonObject preview;
                if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var body = await Body(context).ConfigureAwait(false);
                    var id = BodyText(body, "backup_id");
                    if (id.Length == 0) return Problem(400, "Send {backup_id} of a saved backup, or upload a backup file as the request body.");
                    var passphrase = body["passphrase"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
                    preview = await Task.Run(() => backups.Store.StageSaved(id, passphrase)).ConfigureAwait(false);
                }
                else
                {
                    string? passphrase = null;
                    var header = request.Headers["X-Backup-Passphrase"].ToString();
                    if (header.Length > 0)
                    {
                        try { passphrase = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(header)); }
                        catch (Exception error) when (error is FormatException or DecoderFallbackException) { return Problem(400, "X-Backup-Passphrase must be base64 of the UTF-8 passphrase."); }
                    }
                    var data = await ReadUploadAsync(context).ConfigureAwait(false);
                    if (data.Length == 0) return Problem(400, "Choose a backup file.");
                    preview = await Task.Run(() => backups.Store.Stage(data, passphrase, "upload")).ConfigureAwait(false);
                }
                return Answer(preview);
            })))
            .WithMetadata(ApiJson.BoundedUploadMetadata)
            .Accepts<JsonObject>("application/json", "application/octet-stream")
            .WithName("previewBackupRestore")
            .WithSummary("Check a backup and show what a restore would change: upload the file as the body (optional header X-Backup-Passphrase, base64), or send {backup_id, passphrase?} for a saved one. Refuses damaged, tampered, wrong-passphrase and newer-format files (\"Version mismatch: version X required\"). Answers stage_id and preview_token.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status413PayloadTooLarge);

        group.MapPost("/backups/restore/apply", (Func<HttpContext, Task<IResult>>)(context => Guard(async () =>
            {
                if (backups is null) return Unavailable();
                if (!MaintenanceV1Endpoints.OwnerOrAdmin(context)) return Forbidden();
                if (services.Jobs is not { } jobs) return Problem(503, "The job queue is not available in this host; nothing was restored.");
                var body = await Body(context).ConfigureAwait(false);
                var stage = BodyText(body, "stage_id"); var token = BodyText(body, "preview_token");
                // Hard rule 1: never automatic; the confirm flag and the token of the preview the admin saw.
                if (!BodyFlag(body, "confirm") || stage.Length == 0 || token.Length == 0)
                    return Problem(400, "Confirm the restore: send {\"stage_id\": \"...\", \"preview_token\": \"...\", \"confirm\": true} from its preview.");
                if (!InstanceBackupStore.ValidId(stage)) return Problem(409, "Unknown staged backup; preview it again.");
                var store = backups.Store; var invalidate = backups.Invalidate;
                var handle = jobs.Enqueue(RestoreJob, "", async job =>
                {
                    job.Report(message: "Stopping background work");
                    await jobs.StopBackgroundAsync("Restore", keep: job.Id).ConfigureAwait(false);
                    try
                    {
                        var result = await Task.Run(() => store.Apply(stage, token, message => job.Report(message: message))).ConfigureAwait(false);
                        invalidate?.Invoke();
                        Publish("restored", new JsonObject { ["pre_restore_backup"] = result["pre_restore_backup"]?.DeepClone() });
                        job.Result(result["message"]?.GetValue<string>() ?? "Restored");
                        if (services.CanRestart && Interlocked.Exchange(ref services.Restarting, 1) == 0)
                        {
                            var stop = services.Stop!;
                            _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => stop(), TaskScheduler.Default);
                        }
                    }
                    catch (BackupRefusedException)
                    {
                        Publish("restore-failed");
                        throw;
                    }
                    finally { jobs.ResumeBackground(); }
                }, origin: "manual", title: "Restore backup");
                return Answer(new JsonObject { ["queued"] = true, ["task_id"] = handle.Id, ["merged"] = handle.Merged, ["restart_required"] = true }, 202);
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("applyBackupRestore")
            .WithSummary("Restore a previewed backup as an exclusive job (body {stage_id, preview_token, confirm: true}): background work stops, a pre-restore backup is kept, both databases are swapped and checked, older schemas upgraded, settings merged (this instance's secrets kept); any failure rolls back. Answers 202 with the task id.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/backups/update-notice/dismiss", (Func<HttpContext, IResult>)(context =>
            {
                if (backups is null) return Unavailable();
                if (!MaintenanceV1Endpoints.OwnerOrAdmin(context)) return Forbidden();
                var dismissed = UpgradeGuard.Dismiss(backups.Store);
                Publish("update-notice-dismissed");
                return Answer(new JsonObject { ["dismissed"] = dismissed });
            }))
            .WithName("dismissBackupUpdateNotice")
            .WithSummary("\"Keep current\": hide the notice of the last update or rollback (SET-9); the automatic backup stays in the list.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapGet("/maintenance/old-files", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (backups?.Cleanup is not { } cleanup) return Unavailable();
                if (!MaintenanceV1Endpoints.OwnerOrAdmin(context)) return Forbidden();
                var preview = cleanup.Preview();
                preview["last"] = cleanup.Last();
                return Answer(preview);
            }))
            .WithName("previewOldFileCleanup")
            .WithSummary("SET-3: old files the app itself left (old reports, orphaned bytecode, abandoned temporary files) and the preview_token to move them to the trash.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/maintenance/old-files", (Func<HttpContext, Task<IResult>>)(context => Guard(async () =>
            {
                if (backups?.Cleanup is not { } cleanup) return Unavailable();
                if (!MaintenanceV1Endpoints.OwnerOrAdmin(context)) return Forbidden();
                var body = await Body(context).ConfigureAwait(false);
                var token = BodyText(body, "preview_token");
                if (!BodyFlag(body, "confirm") || token.Length == 0)
                    return Problem(400, "Confirm the cleanup: send {\"confirm\": true, \"preview_token\": \"...\"} from its preview.");
                return Answer(cleanup.Apply(token));
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("applyOldFileCleanup")
            .WithSummary("Move the previewed old files to trash/<date> under the data folder (body {confirm: true, preview_token}); nothing is deleted.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);
    }

    /// <summary>The raw upload body, bounded by the backup size limit and one absolute deadline.</summary>
    private static async Task<byte[]> ReadUploadAsync(HttpContext context)
    {
        var request = context.Request;
        var maximum = InstanceBackupStore.MaxArchiveBytes + 64;
        if (request.ContentLength > maximum) throw new ApiProblem(413, "The backup is larger than 1 GiB.");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { } feature)
        {
            if (feature.IsReadOnly) throw new ApiProblem(409, "Upload request body already started; choose the file again.");
            feature.MaxRequestBodySize = null;
        }
        using var deadline = new CancellationTokenSource(UploadDeadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            while (true)
            {
                var read = await request.Body.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > maximum) throw new ApiProblem(413, "The backup is larger than 1 GiB.");
                output.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        { throw new ApiProblem(408, "The backup upload did not finish in time; choose the file again."); }
        return output.ToArray();
    }
}
