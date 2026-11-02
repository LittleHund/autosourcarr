using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Backup;

/// <summary>What the start-time update check did (SET-9).</summary>
public sealed record UpgradeOutcome(string Status, string? FromVersion, string ToVersion, BackupSummary? Backup, string? Error)
{
    /// <summary>fresh, unchanged or updated: the app starts normally. Anything else starts the minimal safe mode.</summary>
    public bool SafeMode => Status is not ("fresh" or "unchanged" or "updated");
    public bool RolledBack => Status == "rolled-back";
}

/// <summary>
/// SET-9: before the databases are migrated at start, the guard compares the app version and the latest migration of
/// each file with what the data was last opened with (instance-backups/upgrade-state.json). When either differs, it
/// takes an automatic backup (SET-8 format, newest <see cref="InstanceBackupStore.AutomaticKeep"/> kept) and a
/// pre-update copy of each database, then migrates. When a migration or the post-update check (integrity, stamp,
/// migration level, required tables) fails, the pre-update copies are swapped back with <see cref="DatabaseSwap"/>
/// (journalled renames) and a marker <c>instance-backups/rolled-back.json</c> is written. While the marker names the
/// running version, the next starts neither retry nor take new backups: the app starts in its minimal safe mode
/// (writes refused) until the admin chooses "Retry the update" (a fresh backup, then the next start migrates) or runs
/// the previous image. When the automatic backup itself fails (disk full, a missing database), nothing is migrated and
/// the marker says <c>backup-failed</c>; the owner may then choose "Migrate without a backup" (confirm + token).
/// </summary>
public static class UpgradeGuard
{
    public const string StateFile = "upgrade-state.json";
    public const string MarkerFile = "rolled-back.json";
    private const string PreUpdate = ".pre-update";

    public static string StatePath(InstanceBackupStore store) => Path.Combine(store.Root, StateFile);
    public static string MarkerPath(InstanceBackupStore store) => Path.Combine(store.Root, MarkerFile);

    private static JsonObject Read(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : []; }
        catch (Exception error) when (error is JsonException or IOException) { _ = error; return []; }
    }

    public static JsonObject ReadState(InstanceBackupStore store) => Read(StatePath(store));

    public static void WriteState(InstanceBackupStore store, JsonObject state) =>
        InstanceBackupStore.WriteAtomic(StatePath(store), Encoding.UTF8.GetBytes(state.ToJsonString()));

    /// <summary>The safe-mode marker, or null.</summary>
    public static JsonObject? Marker(InstanceBackupStore store) => File.Exists(MarkerPath(store)) ? Read(MarkerPath(store)) : null;

    private static void WriteMarker(InstanceBackupStore store, JsonObject marker) =>
        InstanceBackupStore.WriteAtomic(MarkerPath(store), Encoding.UTF8.GetBytes(marker.ToJsonString()));

    /// <summary>The token an owner sends back to migrate without a backup (it names this exact marker).</summary>
    public static string MarkerToken(JsonObject marker) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("migrate-without-backup|" + marker.ToJsonString())));

    /// <summary>The notice of the last successful update, unless dismissed ("Keep current").</summary>
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

    /// <summary>"Retry the update": a fresh backup now, then the marker allows the next start to migrate again.</summary>
    public static BackupSummary Retry(InstanceBackupStore store)
    {
        var marker = Marker(store) ?? throw new BackupRefusedException("No update is waiting to be retried.", 409);
        var backup = store.Create("automatic", "Before retrying the update to " + (marker["to_version"]?.GetValue<string>() ?? store.AppVersion));
        marker["retry"] = true; marker["skip_backup"] = false; marker["retry_backup_id"] = backup.Id;
        WriteMarker(store, marker);
        return backup;
    }

    /// <summary>The owner's explicit choice after the automatic backup failed: the next start migrates without one.</summary>
    public static void AllowWithoutBackup(InstanceBackupStore store, string token)
    {
        var marker = Marker(store);
        if (marker?["status"]?.GetValue<string>() != "backup-failed") throw new BackupRefusedException("Migrating without a backup is only offered after the automatic backup failed.", 409);
        if (!string.Equals(MarkerToken(marker), token, StringComparison.Ordinal)) throw new BackupRefusedException("The notice changed; open it again.", 409);
        marker["retry"] = true; marker["skip_backup"] = true;
        WriteMarker(store, marker);
    }

    public static UpgradeOutcome Run(InstanceBackupStore store, SqliteDatabase database, Action migrate, Action<string>? log = null)
    {
        var state = ReadState(store);
        var paths = database.Paths;
        var to = store.AppVersion;
        var previous = state["last_opened_version"]?.GetValue<string>();
        var marker = Marker(store);
        var skipBackup = false;
        if (marker is not null)
        {
            var markedFor = marker["to_version"]?.GetValue<string>();
            if (markedFor == to && marker["retry"]?.GetValue<bool>() != true)
            {
                // No retry loop: the failed update stays failed until the admin decides.
                log?.Invoke("The update to " + to + " is paused after " + marker["status"]?.GetValue<string>() + "; starting in safe mode.");
                return new UpgradeOutcome("safe-mode", marker["from_version"]?.GetValue<string>(), to, store.Find(marker["backup_id"]?.GetValue<string>() ?? ""),
                    marker["error"]?.GetValue<string>());
            }
            skipBackup = markedFor == to && marker["skip_backup"]?.GetValue<bool>() == true;
            File.Delete(MarkerPath(store)); // a retry, or another version than the one that failed
        }
        var existing = BackupDatabases.Files.Where(item => File.Exists(BackupDatabases.PathOf(paths, item.File)) && new FileInfo(BackupDatabases.PathOf(paths, item.File)).Length > 0).ToList();
        if (existing.Count == 0)
        {
            migrate();
            Record(state, to, "fresh");
            WriteState(store, state);
            return new UpgradeOutcome("fresh", null, to, null, null);
        }
        var applied = BackupDatabases.Files.ToDictionary(item => item.File, item => existing.Contains(item)
            ? BackupDatabases.Facts(BackupDatabases.PathOf(paths, item.File), integrity: false).Migration : 0);
        var pendingMigrations = new JsonArray(BackupDatabases.Files.SelectMany(item => SchemaMigrator.PendingMigrations(item.File, applied[item.File]))
            .Distinct().Order().Select(version => (JsonNode?)version).ToArray());
        var versionChanged = previous is not null && previous != to;
        if (pendingMigrations.Count == 0 && !versionChanged)
        {
            migrate();
            Record(state, to, previous is null ? "first-guarded" : "unchanged");
            WriteState(store, state);
            return new UpgradeOutcome("unchanged", previous, to, null, null);
        }

        var reason = versionChanged ? $"Before the update from {previous} to {to}" : "Before a database migration";
        JsonObject Mark(string status, string error, BackupSummary? backup) => new()
        {
            ["status"] = status, ["from_version"] = previous, ["to_version"] = to, ["pending_migrations"] = pendingMigrations.DeepClone(),
            ["error"] = error, ["backup_id"] = backup?.Id, ["backup_created_at"] = backup?.CreatedAt, ["retry"] = false,
            ["at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        };
        BackupSummary? backup = null;
        var copies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!skipBackup)
        {
            log?.Invoke(reason + ": taking an automatic backup.");
            try
            {
                backup = store.Create("automatic", reason);
                // The rollback source: a checked copy of each database beside it (renamed back by DatabaseSwap on failure).
                foreach (var item in BackupDatabases.Files)
                {
                    var copy = BackupDatabases.PathOf(paths, item.File) + PreUpdate;
                    BackupDatabases.Snapshot(BackupDatabases.PathOf(paths, item.File), copy);
                    copies[item.Name] = copy;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                foreach (var copy in copies.Values) TryDelete(copy);
                var message = error.GetType().Name + ": " + error.Message;
                log?.Invoke("The automatic backup before the update failed (" + message + "); nothing was migrated.");
                WriteMarker(store, Mark("backup-failed", message, backup));
                return new UpgradeOutcome("backup-failed", previous, to, backup, message);
            }
        }
        try
        {
            migrate();
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
            if (copies.Count == 0)
            {
                log?.Invoke("The update failed (" + message + ") and no backup was taken; starting in safe mode.");
                WriteMarker(store, Mark("failed-without-backup", message, null));
                return new UpgradeOutcome("failed-without-backup", previous, to, null, message);
            }
            log?.Invoke("The update failed (" + message + "); swapping back the databases from before it.");
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DatabaseSwap.Swap(store.Root, database.Paths, store.ConfigurationRoot, copies, null, "update rollback", () =>
                {
                    foreach (var item in BackupDatabases.Files)
                    {
                        var facts = BackupDatabases.Facts(BackupDatabases.PathOf(database.Paths, item.File));
                        if (!facts.Intact || facts.Migration != applied[item.File]) throw new InvalidOperationException(item.Name + " did not verify after the rollback.");
                        BackupDatabases.UseWal(BackupDatabases.PathOf(database.Paths, item.File));
                    }
                });
            }
            catch (Exception rollback) when (rollback is not OutOfMemoryException)
            {
                var both = message + " / rollback: " + rollback.GetType().Name + ": " + rollback.Message;
                WriteMarker(store, Mark("rollback-failed", both, backup));
                return new UpgradeOutcome("rollback-failed", previous, to, backup, both);
            }
            finally { foreach (var copy in copies.Values) TryDelete(copy); }
            WriteMarker(store, Mark("rolled-back", message, backup));
            return new UpgradeOutcome("rolled-back", previous, to, backup, message);
        }
        foreach (var copy in copies.Values) TryDelete(copy);
        Record(state, to, "updated");
        if (backup is not null)
            state["update"] = new JsonObject
            {
                ["status"] = "updated", ["from_version"] = previous, ["to_version"] = to, ["at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["backup_id"] = backup.Id, ["backup_created_at"] = backup.CreatedAt, ["dismissed"] = false,
                ["message"] = $"Updated to {to}. An automatic backup from {backup.CreatedAt} was taken first.",
            };
        WriteState(store, state);
        return new UpgradeOutcome("updated", previous, to, backup, null);
    }

    /// <summary>What the safe-mode notice says and offers.</summary>
    public static JsonObject? SafeModeNotice(InstanceBackupStore store)
    {
        var marker = Marker(store);
        if (marker is null) return null;
        var status = marker["status"]?.GetValue<string>() ?? "";
        var from = marker["from_version"]?.GetValue<string>();
        var to = marker["to_version"]?.GetValue<string>();
        var notice = (JsonObject)marker.DeepClone();
        notice["message"] = status switch
        {
            "rolled-back" => $"The update to {to} failed and both databases were put back as they were before it. AutoSourcarr runs in safe mode: changes are refused and no background work runs.",
            "backup-failed" => $"The automatic backup before the update to {to} could not be taken, so nothing was migrated. AutoSourcarr runs in safe mode.",
            "failed-without-backup" => $"The update to {to} failed and no backup was taken. AutoSourcarr runs in safe mode; restore a backup or run the previous version.",
            _ => $"The update to {to} failed and the rollback did not complete. AutoSourcarr runs in safe mode; restore a backup.",
        };
        notice["previous_version_hint"] = from is null ? "Run the previous AutoSourcarr image again." : $"Run AutoSourcarr {from} again (the previous image); your data is at its schema.";
        notice["retry_pending"] = marker["retry"]?.GetValue<bool>() == true;
        notice["actions"] = new JsonArray(new[] { "retry", status == "backup-failed" ? "migrate-without-backup" : null }.Where(item => item is not null).Select(item => (JsonNode?)item).ToArray());
        if (status == "backup-failed") notice["preview_token"] = MarkerToken(marker);
        return notice;
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
