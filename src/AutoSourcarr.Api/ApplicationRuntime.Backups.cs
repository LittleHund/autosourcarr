using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Backup;

namespace AutoSourcarr.Api;

internal sealed partial class ApplicationRuntime
{
    private OldFileCleanup? _oldFiles;

    /// <summary>SET-3 old-file removal over the app folder and the app's own temporary files.</summary>
    private OldFileCleanup? OldFiles()
    {
        if (_dataDirectory is null || _database is null || _backups is null) return null;
        return _oldFiles ??= new OldFileCleanup(() => _appRoot ?? AppContext.BaseDirectory, _backups.ConfigurationRoot, _dataDirectory,
            _database.Paths.Directory, Version);
    }

    /// <summary>SET-9: each automatic backup and rollback of the start-time update check appears in Tasks and the event stream.</summary>
    private void RecordUpgrade()
    {
        foreach (var line in _upgradeLog) Logs.Record("Update", line);
        if (_upgrade is not { Backup: { } backup } outcome) return;
        var task = Tasks.Create("Automatic backup before update", origin: "automatic", kind: "instance-backup", context: backup.Id);
        Tasks.Complete(task, (backup.Reason ?? "Before an update") + ": backup of " + backup.CreatedAt + " with "
            + backup.Entries.ToString(CultureInfo.InvariantCulture) + " entries kept in System → Backups.");
        if (outcome.RolledBack)
        {
            var rollback = Tasks.Create("Update rolled back", origin: "automatic", kind: "instance-rollback", context: backup.Id);
            var message = "The update to " + outcome.ToVersion + " failed (" + Privacy.Text(outcome.Error) + "); both databases were restored from the backup of "
                + backup.CreatedAt + ". Startup maintenance did not run; see System → Backups.";
            Tasks.Fail(rollback, Privacy.Text(outcome.Error), message);
            Errors.Record("Update", message, "update-rollback");
        }
        Events.Publish("backups", new JsonObject { ["change"] = outcome.Status, ["backup_id"] = backup.Id });
    }
}
