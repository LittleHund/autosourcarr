using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Backup;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Http;

namespace AutoSourcarr.Api;

internal sealed partial class ApplicationRuntime
{
    private OldFileCleanup? _oldFiles;
    private volatile bool _restoreArmed;

    /// <summary>Safe mode (a failed or blocked update) or an armed restore: changes are refused with this reason.</summary>
    internal string? WritesRefused => _upgrade?.SafeMode == true
        ? "AutoSourcarr runs in safe mode after a failed update; changes are refused. See System → Backup & restore."
        : _restoreArmed ? "A restore is armed for the next start; changes are refused until AutoSourcarr restarts (or the restore is cancelled)." : null;

    internal bool SafeMode => _upgrade?.SafeMode == true;

    /// <summary>SET-3 old-file removal over the app folder and the app's own temporary files.</summary>
    private OldFileCleanup? OldFiles()
    {
        if (_dataDirectory is null || _database is null || _backups is null) return null;
        return _oldFiles ??= new OldFileCleanup(() => _appRoot ?? AppContext.BaseDirectory, _backups.ConfigurationRoot, _dataDirectory,
            _database.Paths.Directory, Version);
    }

    private BackupV1Services BackupServices(Func<long, JsonObject>? sharePage) => new()
    {
        Store = _backups!, SharePage = sharePage, Cleanup = SafeMode ? null : OldFiles(),
        Invalidate = () => _duplicates?.Invalidate(), SafeMode = SafeMode,
        RestoreArmed = armed => _restoreArmed = armed,
    };

    /// <summary>
    /// The minimal safe mode (phase 12 audit): after a rolled-back or blocked update nothing that reads or writes the
    /// catalogue is built — no source engine, catalogue, matching, reader, library, jobs or feature-schema writers. Only
    /// sign-in, status/health, diagnostics, settings reads and System → Backup & restore are served; every change except
    /// sign-in and the backup actions is refused (<see cref="WriteGateAsync"/>).
    /// </summary>
    private void OpenSafeMode(StartupConfigurationSnapshot startup)
    {
        _access = OpenAccounts(this, startup);
        _apiV1 = new ApiV1Services(() => Settings, startup.ConfigurationRoot, startup.Conversion, startup.Environment,
            values => Privacy.Register(values), status: null, stop: () => RequestStop?.Invoke(), health: SafeHealthAsync, databasePaths: () => Database.Paths)
        {
            Tasks = Tasks, Events = Events, Errors = Errors, Access = _access,
            Diagnostics = new DiagnosticExporter(Io, Identity, Errors, Logs, () => Settings, new RuntimeDiagnosticSources(this)),
            Backups = BackupServices(null),
        };
        _writer = WriteLogsAsync();
        RecordUpgrade();
        Logs.Record("Update", "Started in safe mode (" + _upgrade!.Status + ").");
    }

    private Task<JsonObject> SafeHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new JsonObject
    {
        ["ok"] = false, ["status"] = "safe-mode", ["version"] = Version, ["safe_mode"] = _backups is null ? null : UpgradeGuard.SafeModeNotice(_backups),
        ["message"] = "AutoSourcarr runs in safe mode after a failed update. Open System → Backup & restore.",
    });

    /// <summary>Refuses changes while in safe mode or while a restore is armed; sign-in and the backup actions stay open.</summary>
    private async Task WriteGateAsync(HttpContext context, RequestDelegate next)
    {
        var method = context.Request.Method;
        if (WritesRefused is { } reason && !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            && !WriteAllowed(AccessPolicy.Canonical(context.Request.Path.Value ?? "")))
        {
            context.Response.Headers.RetryAfter = "30";
            await Http.ApiJson.ProblemAsync(context, 503, reason).ConfigureAwait(false);
            return;
        }
        await next(context).ConfigureAwait(false);
    }

    internal static bool WriteAllowed(string path) => path.StartsWith("/api/v1/auth/", StringComparison.Ordinal) || path == "/login"
        || path == "/api/v1/backups" || path.StartsWith("/api/v1/backups/", StringComparison.Ordinal);

    /// <summary>SET-9: each automatic backup, rollback, safe-mode start and restore at start appears in Tasks and the event stream.</summary>
    private void RecordUpgrade()
    {
        foreach (var line in _upgradeLog) Logs.Record("Update", line);
        if (_restoreResult is { } restore)
        {
            var task = Tasks.Create("Restore at start", origin: "manual", kind: "instance-restore");
            var message = restore["message"]?.GetValue<string>() ?? "";
            if (restore["status"]?.GetValue<string>() is "restored" or "recovered") Tasks.Complete(task, message);
            else Tasks.Fail(task, Privacy.Text(restore["error"]?.GetValue<string>()), message);
            Events.Publish("backups", new JsonObject { ["change"] = "restore-" + restore["status"]?.GetValue<string>() });
        }
        if (_upgrade is not { } outcome) return;
        if (outcome.Backup is { } backup && outcome.Status != "safe-mode")
        {
            var task = Tasks.Create("Automatic backup before update", origin: "automatic", kind: "instance-backup", context: backup.Id);
            Tasks.Complete(task, (backup.Reason ?? "Before an update") + ": backup of " + backup.CreatedAt + " with "
                + backup.Entries.ToString(CultureInfo.InvariantCulture) + " entries kept in System → Backups.");
        }
        if (outcome.SafeMode)
        {
            var title = outcome.Status switch
            {
                "rolled-back" => "Update rolled back", "backup-failed" => "Update not started: the automatic backup failed",
                "safe-mode" => "Update paused (safe mode)", _ => "Update failed",
            };
            var task = Tasks.Create(title, origin: "automatic", kind: "instance-rollback", context: outcome.Backup?.Id ?? "");
            var message = (_backups is null ? null : UpgradeGuard.SafeModeNotice(_backups)?["message"]?.GetValue<string>()) ?? title;
            Tasks.Fail(task, Privacy.Text(outcome.Error), message);
            Errors.Record("Update", message, "update-" + outcome.Status);
        }
        if (outcome.Status != "unchanged" && outcome.Status != "fresh")
            Events.Publish("backups", new JsonObject { ["change"] = outcome.Status, ["backup_id"] = outcome.Backup?.Id });
    }
}
