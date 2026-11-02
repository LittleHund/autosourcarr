using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Backup;

/// <summary>What the start-time update check did (SET-9).</summary>
public sealed record UpgradeOutcome(string Status, string? FromVersion, string ToVersion, BackupSummary? Backup, string? Error)
{
    /// <summary>fresh, unchanged, updated or rolled-back.</summary>
    public bool RolledBack => Status == "rolled-back";
}

/// <summary>
/// SET-9: before the databases are migrated at start, the guard compares the app version and the latest migration of
/// each file with what the data was last opened with (instance-backups/upgrade-state.json). When either differs, it
/// takes an automatic backup (SET-8 format, newest <see cref="InstanceBackupStore.AutomaticKeep"/> kept) and then
/// migrates. When a migration or the post-update start check (integrity, stamp, migration level, required tables)
/// fails, both databases are put back from that backup automatically and the app starts in a safe state (no startup
/// maintenance, upgrades or crawls) with a notice; System → Backups offers "Roll back to the backup from …" and "Keep current".
/// </summary>
public static class UpgradeGuard
{
    public const string StateFile = "upgrade-state.json";

    /// <summary>Tests only: runs after the migrations and may throw, standing in for a failing migration. Never set in the app.</summary>
    public static Action<SqliteDatabase>? FaultAfterMigrate { get; set; }

    public static string StatePath(InstanceBackupStore store) => Path.Combine(store.Root, StateFile);

    public static JsonObject ReadState(InstanceBackupStore store)
    {
        try { return File.Exists(StatePath(store)) ? JsonNode.Parse(File.ReadAllText(StatePath(store))) as JsonObject ?? [] : []; }
        catch (Exception error) when (error is JsonException or IOException) { _ = error; return []; }
    }

    public static void WriteState(InstanceBackupStore store, JsonObject state)
    {
        var path = StatePath(store);
        File.WriteAllText(path + ".pending", state.ToJsonString());
        File.Move(path + ".pending", path, overwrite: true);
    }

    /// <summary>The notice of the last update, unless dismissed ("Keep current").</summary>
    public static JsonObject? Notice(InstanceBackupStore store) =>
        ReadState(store)["update"] is JsonObject update && update["dismissed"]?.GetValue<bool>() != true ? (JsonObject)update.DeepClone() : null;

    public static bool Dismiss(InstanceBackupStore store)
    {
        var state = ReadState(store);
        if (state["update"] is not JsonObject update) return false;
        update["dismissed"] = true;
        WriteState(store, state);
        return true;
    }

    public static UpgradeOutcome Run(InstanceBackupStore store, SqliteDatabase database, Action migrate, Action<string>? log = null)
    {
        var state = ReadState(store);
        var paths = database.Paths;
        var existing = BackupDatabases.Files.Where(item => File.Exists(BackupDatabases.PathOf(paths, item.File)) && new FileInfo(BackupDatabases.PathOf(paths, item.File)).Length > 0).ToList();
        var previous = state["last_opened_version"]?.GetValue<string>();
        var to = store.AppVersion;
        if (existing.Count == 0)
        {
            migrate();
            Record(state, to, "fresh");
            WriteState(store, state);
            return new UpgradeOutcome("fresh", null, to, null, null);
        }
        var pending = existing.Any(item => BackupDatabases.Facts(BackupDatabases.PathOf(paths, item.File), integrity: false).Migration < SchemaMigrator.LatestMigration(item.File));
        var versionChanged = previous is not null && previous != to;
        if (!pending && !versionChanged)
        {
            migrate();
            Record(state, to, previous is null ? "first-guarded" : "unchanged");
            WriteState(store, state);
            return new UpgradeOutcome("unchanged", previous, to, null, null);
        }

        var reason = versionChanged ? $"Before the update from {previous} to {to}" : "Before a database migration";
        log?.Invoke(reason + ": taking an automatic backup.");
        var backup = store.Create("automatic", reason);
        try
        {
            migrate();
            FaultAfterMigrate?.Invoke(database);
            foreach (var item in BackupDatabases.Files)
            {
                var facts = BackupDatabases.Facts(BackupDatabases.PathOf(database.Paths, item.File));
                if (!facts.Intact) throw new InvalidOperationException(item.Name + " failed SQLite's integrity check after the update.");
                if (facts.ApplicationId != SchemaMigrator.SchemaApplicationId || facts.Migration != SchemaMigrator.LatestMigration(item.File))
                    throw new InvalidOperationException(item.Name + " is not at this version's schema after the update.");
                if (!facts.Rows.ContainsKey(item.RequiredTable)) throw new InvalidOperationException(item.Name + " is missing " + item.RequiredTable + " after the update.");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var message = error.GetType().Name + ": " + error.Message;
            log?.Invoke("The update failed (" + message + "); restoring the databases from the backup of " + backup.CreatedAt + ".");
            store.RestoreDatabases(backup.Id, migrate: false);
            state["update"] = Update("rolled-back", previous, to, backup, message);
            WriteState(store, state);
            return new UpgradeOutcome("rolled-back", previous, to, backup, message);
        }
        Record(state, to, "updated");
        state["update"] = Update("updated", previous, to, backup, null);
        WriteState(store, state);
        return new UpgradeOutcome("updated", previous, to, backup, null);
    }

    private static void Record(JsonObject state, string version, string status)
    {
        state["last_opened_version"] = version;
        state["opened_at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        state["last_status"] = status;
        state["migrations"] = new JsonObject
        {
            ["catalog"] = SchemaMigrator.LatestMigration(DatabaseFile.Catalog), ["origins"] = SchemaMigrator.LatestMigration(DatabaseFile.Origins),
        };
    }

    private static JsonObject Update(string status, string? from, string to, BackupSummary backup, string? error) => new()
    {
        ["status"] = status, ["from_version"] = from, ["to_version"] = to, ["at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        ["backup_id"] = backup.Id, ["backup_created_at"] = backup.CreatedAt, ["error"] = error, ["dismissed"] = false,
        ["message"] = status == "rolled-back"
            ? $"The update to {to} failed and both databases were restored from the automatic backup of {backup.CreatedAt}. Startup maintenance did not run. Run the previous version, or keep this version and roll back to the backup from System → Backups."
            : $"Updated to {to}. An automatic backup from {backup.CreatedAt} was taken first; roll back to it if this version does not work for you.",
    };
}
