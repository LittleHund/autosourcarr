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
/// takes an automatic backup and a pre-update copy of each database, writes the marker
/// <c>instance-backups/rolled-back.json</c> with status <c>migrating</c>, and migrates.
/// <list type="bullet">
/// <item>Success: the update state is recorded, then the marker and the copies are removed.</item>
/// <item>Failure: the marker says <c>rolling-back</c>, the copies are swapped back (<see cref="DatabaseSwap"/>,
/// completed forward after a crash), then the marker says <c>rolled-back</c> and the copies go.</item>
/// <item>A kill during migrate or rollback: the next start finds <c>migrating</c>/<c>rolling-back</c>, completes the
/// rollback from the kept copies and starts in safe mode; it never backs up the half-migrated data.</item>
/// </list>
/// While a marker names the running version (or cannot be read), starts neither retry nor take new backups: the app
/// runs its minimal safe mode until the admin chooses "Retry the update" (a fresh backup, then the next start migrates)
/// or restores a backup. When the automatic backup fails, nothing is migrated (<c>backup-failed</c>); the owner may
/// then choose "Migrate without a backup". An unfinished database swap always means safe mode (<c>swap-pending</c>).
/// </summary>
public static class UpgradeGuard
{
    public const string StateFile = "upgrade-state.json";
    public const string MarkerFile = "rolled-back.json";
    public const string PreUpdate = ".pre-update";
    private static readonly string[] Unfinished = ["migrating", "rolling-back"];

    public static string StatePath(InstanceBackupStore store) => Path.Combine(store.Root, StateFile);
    public static string MarkerPath(InstanceBackupStore store) => Path.Combine(store.Root, MarkerFile);

    internal static string? Text(JsonObject? node, string name) => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    internal static bool Flag(JsonObject? node, string name) => node?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static bool TypeIs(JsonObject node, string name, JsonValueKind kind) => node[name] is null || node[name] is JsonValue value && value.GetValueKind() == kind;
    private static bool Bool(JsonObject node, string name) => node[name] is null || node[name] is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    private static JsonObject Read(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : []; }
        catch (Exception error) when (error is JsonException or IOException) { _ = error; return []; }
    }

    /// <summary>The saved state; a state file that exists but cannot be read answers {"_unreadable": true} (an instance
    /// that ran before, whose version is unknown), never the empty state of a fresh instance.</summary>
    public static JsonObject ReadState(InstanceBackupStore store)
    {
        var path = StatePath(store);
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject { ["_unreadable"] = true } : []; }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { _ = error; return new JsonObject { ["_unreadable"] = true }; }
    }

    public static void WriteState(InstanceBackupStore store, JsonObject state) =>
        InstanceBackupStore.WriteAtomic(StatePath(store), Encoding.UTF8.GetBytes(state.ToJsonString()));

    /// <summary>The safe-mode marker, or null. A marker that cannot be read is answered as status "unreadable" (safe mode).</summary>
    public static JsonObject? Marker(InstanceBackupStore store)
    {
        var path = MarkerPath(store);
        if (!File.Exists(path)) return null;
        try
        {
            // Wrong field types count as unreadable too: a marker is never misread into a silent retry.
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject marker && Text(marker, "status") is { Length: > 0 }
                && TypeIs(marker, "to_version", JsonValueKind.String) && TypeIs(marker, "from_version", JsonValueKind.String)
                && TypeIs(marker, "backup_id", JsonValueKind.String) && Bool(marker, "retry") && Bool(marker, "skip_backup") && Bool(marker, "copies")
                && (marker["levels"] is null or JsonObject))
                return marker;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { _ = error; }
        return new JsonObject { ["status"] = "unreadable", ["to_version"] = store.AppVersion, ["error"] = "instance-backups/" + MarkerFile + " cannot be read." };
    }

    internal static void WriteMarker(InstanceBackupStore store, JsonObject marker) =>
        InstanceBackupStore.WriteAtomic(MarkerPath(store), Encoding.UTF8.GetBytes(marker.ToJsonString()));

    /// <summary>The token an owner sends back to migrate without a backup (it names this exact marker).</summary>
    public static string MarkerToken(JsonObject marker) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("migrate-without-backup|" + marker.ToJsonString())));

    /// <summary>The notice of the last successful update, unless dismissed ("Keep current").</summary>
    public static JsonObject? Notice(InstanceBackupStore store) =>
        ReadState(store)["update"] is JsonObject update && !Flag(update, "dismissed") ? (JsonObject)update.DeepClone() : null;

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
        if (Text(marker, "status") is "migrating" or "rolling-back" or "rollback-failed" or "failed-without-backup" or "swap-pending")
            throw new BackupRefusedException("The databases may be half-migrated or a swap is unfinished; restore a backup (or run the previous image) before retrying.", 409);
        var backup = store.Create("automatic", "Before retrying the update to " + (Text(marker, "to_version") ?? store.AppVersion));
        if (Text(marker, "status") == "unreadable") marker = new JsonObject { ["status"] = "retry", ["to_version"] = store.AppVersion };
        marker["retry"] = true; marker["skip_backup"] = false; marker["retry_backup_id"] = backup.Id;
        WriteMarker(store, marker);
        return backup;
    }

    /// <summary>The owner's explicit choice after the automatic backup failed: the next start migrates without one.</summary>
    public static void AllowWithoutBackup(InstanceBackupStore store, string token)
    {
        var marker = Marker(store);
        if (Text(marker, "status") != "backup-failed") throw new BackupRefusedException("Migrating without a backup is only offered after the automatic backup failed.", 409);
        if (!string.Equals(MarkerToken(marker!), token, StringComparison.Ordinal)) throw new BackupRefusedException("The notice changed; open it again.", 409);
        marker!["retry"] = true; marker["skip_backup"] = true;
        WriteMarker(store, marker);
    }

    /// <summary>Copies set aside before an update (kept until the final marker).</summary>
    private static Dictionary<string, string?> Copies(DatabasePaths paths) =>
        BackupDatabases.Files.ToDictionary(item => item.Name, item => File.Exists(BackupDatabases.PathOf(paths, item.File) + PreUpdate)
            ? BackupDatabases.PathOf(paths, item.File) + PreUpdate : null, StringComparer.Ordinal);

    private static void DeleteCopies(DatabasePaths paths)
    {
        foreach (var item in BackupDatabases.Files) { TryDelete(BackupDatabases.PathOf(paths, item.File) + PreUpdate); TryDelete(BackupDatabases.PathOf(paths, item.File) + PreUpdate + ".partial"); }
    }

    public static UpgradeOutcome Run(InstanceBackupStore store, SqliteDatabase database, Action migrate, Action<string>? log = null)
    {
        var state = ReadState(store);
        var paths = database.Paths;
        var to = store.AppVersion;
        var previous = Text(state, "last_opened_version");
        // A damaged state file: this instance ran before with an unknown version, so the update path (with its backup) runs.
        var stateUnreadable = Flag(state, "_unreadable");
        state.Remove("_unreadable");
        UpgradeOutcome Safe(string status, JsonObject? marker) =>
            new(status, Text(marker, "from_version"), to, store.Find(Text(marker, "backup_id") ?? ""), Text(marker, "error"));

        // An unfinished swap (journal or set-aside files) is never run over, and never taken for a fresh instance.
        if (DatabaseSwap.Pending(store.Root, paths, store.ConfigurationRoot))
        {
            var pending = Marker(store) is { } existingMarker && Text(existingMarker, "status") == "swap-pending" ? existingMarker : new JsonObject
            {
                ["status"] = "swap-pending", ["to_version"] = to, ["from_version"] = previous,
                ["error"] = "A database swap did not finish; its files are kept as they are.", ["at"] = Now(),
            };
            WriteMarker(store, pending);
            log?.Invoke("An unfinished database swap was found; starting in safe mode.");
            return Safe("swap-pending", pending);
        }

        var marker = Marker(store);
        var skipBackup = false;
        if (marker is not null)
        {
            var status = Text(marker, "status");
            if (status == "swap-pending") { File.Delete(MarkerPath(store)); marker = null; } // the swap was finished meanwhile
            else if (status is "migrating" or "rolling-back")
                return FinishRollback(store, database, marker, log);
            else if (status == "unreadable")
            {
                log?.Invoke("The update marker cannot be read; starting in safe mode.");
                return Safe("unreadable", marker);
            }
            else
            {
                var markedFor = Text(marker, "to_version");
                if (markedFor == to && !Flag(marker, "retry"))
                {
                    // No retry loop: the failed update stays failed until the admin decides. Copies left by a kill right
                    // after the final rolled-back marker are no longer needed.
                    if (status == "rolled-back") DeleteCopies(paths);
                    log?.Invoke("The update to " + to + " is paused after " + status + "; starting in safe mode.");
                    return Safe("safe-mode", marker);
                }
                skipBackup = markedFor == to && Flag(marker, "skip_backup");
            }
        }

        var existing = BackupDatabases.Files.Where(item => File.Exists(BackupDatabases.PathOf(paths, item.File)) && new FileInfo(BackupDatabases.PathOf(paths, item.File)).Length > 0).ToList();
        if (existing.Count == 0 && (previous is not null || stateUnreadable))
        {
            // This instance opened its databases before: none now means DATABASE_DIR (or DATA_DIR) is not mounted or was
            // moved, never a fresh start. Empty databases would hide the catalogue (and Mihon would see it emptied).
            log?.Invoke("No database files were found in " + Path.GetDirectoryName(BackupDatabases.PathOf(paths, BackupDatabases.Files.First().File)) + ", but this instance ran before; starting in safe mode.");
            return Safe("databases-missing", new JsonObject
            {
                ["from_version"] = previous, ["error"] = "The databases are missing from " + Path.GetDirectoryName(BackupDatabases.PathOf(paths, BackupDatabases.Files.First().File))
                    + " although this instance ran before (version " + (previous ?? "unknown") + "). Check that DATABASE_DIR/DATA_DIR are mounted; to start over on purpose, remove "
                    + StateFile + " from the instance-backups folder.",
            });
        }
        if (existing.Count == 0)
        {
            migrate();
            Record(state, to, "fresh");
            WriteState(store, state);
            if (marker is not null) File.Delete(MarkerPath(store));
            return new UpgradeOutcome("fresh", null, to, null, null);
        }
        var applied = BackupDatabases.Files.ToDictionary(item => item.File, item => existing.Contains(item)
            ? BackupDatabases.Facts(BackupDatabases.PathOf(paths, item.File), integrity: false).Migration : 0);
        var pendingMigrations = new JsonArray(BackupDatabases.Files.SelectMany(item => SchemaMigrator.PendingMigrations(item.File, applied[item.File]))
            .Distinct().Order().Select(version => (JsonNode?)version).ToArray());
        var versionChanged = previous is not null && previous != to || stateUnreadable;
        if (pendingMigrations.Count == 0 && !versionChanged)
        {
            migrate();
            Record(state, to, previous is null ? "first-guarded" : "unchanged");
            WriteState(store, state);
            if (marker is not null) File.Delete(MarkerPath(store));
            DeleteCopies(paths);
            return new UpgradeOutcome("unchanged", previous, to, null, null);
        }

        var reason = versionChanged ? $"Before the update from {previous} to {to}" : "Before a database migration";
        JsonObject Mark(string status, string? error, BackupSummary? backup) => new()
        {
            ["status"] = status, ["from_version"] = previous, ["to_version"] = to, ["pending_migrations"] = pendingMigrations.DeepClone(),
            ["levels"] = new JsonObject { ["catalog"] = applied[DatabaseFile.Catalog], ["origins"] = applied[DatabaseFile.Origins] },
            ["error"] = error is null ? null : store.Scrub(error), ["backup_id"] = backup?.Id, ["backup_created_at"] = backup?.CreatedAt, ["retry"] = false,
            ["copies"] = !skipBackup, ["at"] = Now(),
        };
        BackupSummary? backup = null;
        if (!skipBackup)
        {
            log?.Invoke(reason + ": taking an automatic backup.");
            try
            {
                CrashPoints.Reach("guard:backup");
                backup = store.Create("automatic", reason);
                // The rollback source: a checked copy of each database (written aside first, so a crash never leaves a
                // half copy under the final name), kept until the final marker.
                DeleteCopies(paths);
                foreach (var item in existing)
                {
                    var copy = BackupDatabases.PathOf(paths, item.File) + PreUpdate;
                    BackupDatabases.Snapshot(BackupDatabases.PathOf(paths, item.File), copy + ".partial");
                    File.Move(copy + ".partial", copy);
                    CrashPoints.Reach("guard:copy:" + item.Name);
                }
            }
            catch (Exception error) when (CrashPoints.Handled(error))
            {
                DeleteCopies(paths);
                var message = error.GetType().Name + ": " + error.Message;
                log?.Invoke("The automatic backup before the update failed (" + store.Scrub(message) + "); nothing was migrated.");
                var failed = Mark("backup-failed", message, backup);
                failed["copies"] = false;
                WriteMarker(store, failed);
                return new UpgradeOutcome("backup-failed", previous, to, backup, store.Scrub(message));
            }
        }
        WriteMarker(store, Mark("migrating", null, backup));
        CrashPoints.Reach("guard:migrating");
        try
        {
            migrate();
            CrashPoints.Reach("guard:migrated");
            foreach (var item in BackupDatabases.Files)
            {
                var facts = BackupDatabases.Facts(BackupDatabases.PathOf(database.Paths, item.File));
                if (!facts.Intact) throw new InvalidOperationException(item.Name + " failed SQLite's integrity check after the update.");
                if (facts.ApplicationId != SchemaMigrator.SchemaApplicationId || facts.Migration != SchemaMigrator.LatestMigration(item.File))
                    throw new InvalidOperationException(item.Name + " is not at this version's schema after the update.");
                if (!facts.Rows.ContainsKey(item.RequiredTable)) throw new InvalidOperationException(item.Name + " is missing " + item.RequiredTable + " after the update.");
            }
        }
        catch (Exception error) when (CrashPoints.Handled(error))
        {
            var message = error.GetType().Name + ": " + error.Message;
            if (skipBackup)
            {
                log?.Invoke("The update failed (" + store.Scrub(message) + ") and no backup was taken; starting in safe mode.");
                var failed = Mark("failed-without-backup", message, null);
                failed["copies"] = false;
                WriteMarker(store, failed);
                return new UpgradeOutcome("failed-without-backup", previous, to, null, store.Scrub(message));
            }
            var rolling = Mark("rolling-back", message, backup);
            WriteMarker(store, rolling);
            CrashPoints.Reach("guard:rolling-back");
            return FinishRollback(store, database, rolling, log);
        }
        Record(state, to, "updated");
        if (backup is not null)
            state["update"] = new JsonObject
            {
                ["status"] = "updated", ["from_version"] = previous, ["to_version"] = to, ["at"] = Now(),
                ["backup_id"] = backup.Id, ["backup_created_at"] = backup.CreatedAt, ["dismissed"] = false,
                ["message"] = $"Updated to {to}. An automatic backup from {backup.CreatedAt} was taken first.",
            };
        WriteState(store, state);
        File.Delete(MarkerPath(store));
        CrashPoints.Reach("guard:updated");
        DeleteCopies(paths);
        return new UpgradeOutcome("updated", previous, to, backup, null);
    }

    /// <summary>
    /// Puts the pre-update copies back (after a failed migration, or at the next start after a kill during the
    /// migration or the rollback) and leaves the marker at rolled-back; the copies go only after that marker.
    /// </summary>
    private static UpgradeOutcome FinishRollback(InstanceBackupStore store, SqliteDatabase database, JsonObject marker, Action<string>? log)
    {
        var paths = database.Paths;
        var to = store.AppVersion;
        var backup = store.Find(Text(marker, "backup_id") ?? "");
        var copies = Copies(paths);
        var levels = marker["levels"] as JsonObject;
        if (!Flag(marker, "copies") || copies.Values.All(path => path is null))
        {
            marker["status"] = "failed-without-backup";
            marker["error"] ??= "The update was interrupted and no pre-update copies exist.";
            WriteMarker(store, marker);
            return new UpgradeOutcome("failed-without-backup", Text(marker, "from_version"), to, backup, Text(marker, "error"));
        }
        if (Text(marker, "status") != "rolling-back") { marker["status"] = "rolling-back"; marker["error"] ??= "The update was interrupted."; WriteMarker(store, marker); }
        log?.Invoke("Swapping back the databases from before the update.");
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DatabaseSwap.Swap(store.Root, paths, store.ConfigurationRoot, copies, null, "update rollback", () =>
            {
                foreach (var item in BackupDatabases.Files)
                {
                    var path = BackupDatabases.PathOf(paths, item.File);
                    if (copies[item.Name] is null) continue;
                    var facts = BackupDatabases.Facts(path);
                    var level = levels?[item.File == DatabaseFile.Catalog ? "catalog" : "origins"] is JsonValue value && value.TryGetValue<long>(out var number) ? number : facts.Migration;
                    if (!facts.Intact || facts.Migration != level) throw new InvalidOperationException(item.Name + " did not verify after the rollback.");
                    BackupDatabases.UseWal(path);
                }
                CrashPoints.Reach("guard:rollback-verified");
            }, forward: true);
        }
        catch (Exception rollback) when (CrashPoints.Handled(rollback))
        {
            // The copies stay: they are the only clean state left.
            marker["status"] = "rollback-failed";
            marker["error"] = store.Scrub((Text(marker, "error") ?? "") + " / rollback: " + rollback.GetType().Name + ": " + rollback.Message);
            WriteMarker(store, marker);
            return new UpgradeOutcome("rollback-failed", Text(marker, "from_version"), to, backup, Text(marker, "error"));
        }
        marker["status"] = "rolled-back";
        WriteMarker(store, marker);
        CrashPoints.Reach("guard:rolled-back");
        DeleteCopies(paths);
        return new UpgradeOutcome("rolled-back", Text(marker, "from_version"), to, backup, Text(marker, "error"));
    }

    /// <summary>What the safe-mode notice says and offers (for an owner or admin; see <see cref="PublicNotice"/>).</summary>
    public static JsonObject? SafeModeNotice(InstanceBackupStore store)
    {
        var marker = Marker(store);
        if (marker is null) return null;
        var status = Text(marker, "status") ?? "";
        var from = Text(marker, "from_version");
        var to = Text(marker, "to_version");
        var notice = (JsonObject)marker.DeepClone();
        var restored = Text(marker, "restored_at") is not null;
        notice["message"] = status switch
        {
            "rolled-back" => $"The update to {to} failed and both databases were put back as they were before it. AutoSourcarr runs in safe mode: changes are refused and no background work runs.",
            "backup-failed" => $"The automatic backup before the update to {to} could not be taken, so nothing was migrated. AutoSourcarr runs in safe mode.",
            "failed-without-backup" => $"The update to {to} failed or was interrupted and no copy from before it exists. Restore a backup (Restore at the next start) or run the previous version.",
            "rollback-failed" => $"The update to {to} failed and putting the databases back did not finish; they may be half-migrated. The copies from before the update are kept. Restore the automatic backup or run the previous version.",
            "swap-pending" => "A database swap (a restore or a rollback) did not finish and its files are kept as they are. AutoSourcarr runs in safe mode; check the data folder or restore a backup.",
            "unreadable" => "The update marker (instance-backups/rolled-back.json) cannot be read. AutoSourcarr runs in safe mode; retry the update or restore a backup.",
            "restored" => $"A backup was restored after the failed update to {to}. Choose Retry the update to migrate it, or run the previous version.",
            "restore-failed" => "An armed restore was interrupted; the databases are as they were before it. Restore again, or choose Retry the update.",
            _ => $"The update to {to} is paused. AutoSourcarr runs in safe mode.",
        };
        if (restored && status != "restored") notice["message"] = notice["message"]!.GetValue<string>() + " A backup was restored since.";
        notice["previous_version_hint"] = from is null ? "Run the previous AutoSourcarr image again." : $"Run AutoSourcarr {from} again (the previous image); your data is at its schema.";
        notice["retry_pending"] = Flag(marker, "retry");
        var actions = new List<string>();
        if (status is "rolled-back" or "backup-failed" or "unreadable" or "restored" or "restore-failed" or "retry") actions.Add("retry");
        if (status == "backup-failed") actions.Add("migrate-without-backup");
        if (status is "rollback-failed" or "failed-without-backup" or "swap-pending" or "restore-failed" && Text(marker, "backup_id") is not null) actions.Add("restore");
        notice["actions"] = new JsonArray(actions.Select(item => (JsonNode?)item).ToArray());
        if (status == "backup-failed") notice["preview_token"] = MarkerToken(marker);
        return notice;
    }

    /// <summary>What a guest may read (health): the state and a short message, no errors, ids, migrations or tokens.</summary>
    public static JsonObject? PublicNotice(InstanceBackupStore store) => Marker(store) is { } marker
        ? new JsonObject { ["status"] = Text(marker, "status"), ["message"] = "AutoSourcarr runs in safe mode after a failed or interrupted update. An admin can see the details in System → Backup & restore." }
        : null;

    /// <summary>A restore at start replaced the data the marker is about: the notice says so and offers Retry.</summary>
    internal static void NoteRestored(InstanceBackupStore store)
    {
        if (Marker(store) is not { } marker || Text(marker, "status") == "unreadable") return;
        marker["restored_at"] = Now();
        if (Text(marker, "status") is "rolled-back" or "rollback-failed" or "failed-without-backup" or "backup-failed" or "restore-failed")
        {
            marker["status"] = "restored"; marker["retry"] = false;
        }
        WriteMarker(store, marker);
    }

    private static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static void Record(JsonObject state, string version, string status)
    {
        state["last_opened_version"] = version;
        state["opened_at"] = Now();
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
