using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Backup;

/// <summary>
/// Journalled replacement of both databases (and optionally config.json), run only at start before any database is
/// opened (0.4.5 instance_backup apply_pending design). Steps, recorded in <c>instance-backups/swap-journal.json</c>
/// together with the incoming files, which databases and config.json existed, and what to do after a crash:
/// <list type="number">
/// <item><c>aside</c>: each live file is renamed to <c>*.swap-old</c> (<c>-wal</c> and <c>-shm</c> before the database
/// itself, so a crash in between never leaves a live log without its database); config.json is copied aside.</item>
/// <item><c>placed</c>: the incoming files are copied next to the target (<c>*.swap-new</c>, flushed) and renamed in
/// place; the new config.json is written atomically.</item>
/// <item><c>verified</c>: the caller's check passed (integrity, stamp, rows).</item>
/// </list>
/// After a crash before <c>verified</c>, a restore is undone (the <c>swap-old</c> files go back; only files that
/// belong to the new databases are deleted), while an update rollback (<c>forward</c>) is completed from its incoming
/// copies, because the files it set aside are the failed ones. After <c>verified</c> the swap is finished.
/// </summary>
public static class DatabaseSwap
{
    public const string JournalFile = "swap-journal.json";
    private const string Old = ".swap-old", New = ".swap-new";
    private static readonly string[] Suffixes = ["-wal", "-shm", ""];

    private static string Journal(string root) => Path.Combine(root, JournalFile);

    private static void Write(string root, JsonObject journal)
    {
        var path = Journal(root);
        File.WriteAllText(path + ".pending", journal.ToJsonString());
        using (var stream = new FileStream(path + ".pending", FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(flushToDisk: true);
        File.Move(path + ".pending", path, overwrite: true);
    }

    /// <summary>An unfinished swap: a journal or any set-aside file. The app must not run (or create fresh databases) then.</summary>
    public static bool Pending(string journalRoot, DatabasePaths paths, string configurationRoot) =>
        File.Exists(Journal(journalRoot)) || File.Exists(Path.Combine(configurationRoot, ConfigStore.FileName) + Old)
        || BackupDatabases.Files.Any(item => Suffixes.Any(suffix => File.Exists(Target(paths, item) + Old + suffix)));

    /// <summary>
    /// Finishes or undoes a swap a crash interrupted; answers what was done (null when no journal). Throws when it
    /// cannot (the caller then starts in safe mode and leaves every file in place).
    /// </summary>
    public static string? Recover(string journalRoot, DatabasePaths paths, string configurationRoot)
    {
        var path = Journal(journalRoot);
        if (!File.Exists(path)) return null;
        JsonObject journal;
        try { journal = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("The swap journal is not an object."); }
        catch (JsonException error) { throw new InvalidDataException("The swap journal cannot be read.", error); }
        var step = Text(journal, "step") ?? "begin";
        var kind = Text(journal, "kind") ?? "swap";
        if (step == "verified")
        {
            Finish(paths, configurationRoot);
            File.Delete(path);
            return $"An interrupted {kind} was completed.";
        }
        if (Flag(journal, "forward"))
        {
            Forward(journal, paths);
            Finish(paths, configurationRoot);
            File.Delete(path);
            return $"An interrupted {kind} was completed from its copies.";
        }
        Undo(journal, step, paths, configurationRoot);
        File.Delete(path);
        return $"An interrupted {kind} was undone; the databases are as they were before it.";
    }

    /// <summary>
    /// Replaces both databases by <paramref name="incoming"/> (file name → source file, or null to leave none) and, when
    /// given, writes <paramref name="config"/> as config.json. <paramref name="verify"/> runs on the placed files; any
    /// failure undoes the swap by renaming and rethrows the original error. With <paramref name="forward"/> a crash is
    /// completed from <paramref name="incoming"/> on the next start instead of undone.
    /// </summary>
    public static void Swap(string journalRoot, DatabasePaths paths, string configurationRoot, IReadOnlyDictionary<string, string?> incoming,
        byte[]? config, string kind, Action verify, bool forward = false)
    {
        Directory.CreateDirectory(journalRoot);
        var configPath = Path.Combine(configurationRoot, ConfigStore.FileName);
        var journal = new JsonObject
        {
            ["kind"] = kind, ["step"] = "begin", ["forward"] = forward, ["at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["incoming"] = new JsonObject(BackupDatabases.Files.Select(item => KeyValuePair.Create(item.Name, (JsonNode?)incoming.GetValueOrDefault(item.Name)))),
            ["existed"] = new JsonObject(BackupDatabases.Files.Select(item => KeyValuePair.Create(item.Name, (JsonNode?)File.Exists(Target(paths, item))))),
            ["config_new"] = config is not null, ["config_existed"] = File.Exists(configPath),
        };
        Write(journalRoot, journal);
        var step = "begin";
        try
        {
            // Leftovers of an older finished swap must not be mistaken for this one's.
            foreach (var item in BackupDatabases.Files) foreach (var suffix in Suffixes) Delete(Target(paths, item) + Old + suffix);
            Delete(configPath + Old);
            foreach (var item in BackupDatabases.Files)
            {
                var target = Target(paths, item);
                foreach (var suffix in Suffixes)
                    if (File.Exists(target + suffix))
                    {
                        File.Move(target + suffix, target + Old + suffix);
                        CrashPoints.Reach("swap:aside:" + item.Name + suffix);
                    }
            }
            if (config is not null && File.Exists(configPath)) File.Copy(configPath, configPath + Old);
            journal["step"] = step = "aside"; Write(journalRoot, journal);
            CrashPoints.Reach("swap:aside");

            foreach (var item in BackupDatabases.Files)
            {
                if (incoming.GetValueOrDefault(item.Name) is not { } source) continue;
                Place(source, Target(paths, item));
                CrashPoints.Reach("swap:placed:" + item.Name);
            }
            if (config is not null) lock (SettingsEditor.Gate(configurationRoot)) ConfigFiles.WriteAtomic(configPath, config);
            journal["step"] = step = "placed"; Write(journalRoot, journal);
            CrashPoints.Reach("swap:placed");
            verify();
            journal["step"] = step = "verified"; Write(journalRoot, journal);
            CrashPoints.Reach("swap:verified");
        }
        catch (Exception error) when (CrashPoints.Handled(error))
        {
            try { Undo(journal, step, paths, configurationRoot); File.Delete(Journal(journalRoot)); }
            catch (Exception undo) when (CrashPoints.Handled(undo))
            {
                // The journal stays: the next start sees the unfinished swap and starts in safe mode.
                throw new AggregateException("The swap failed and could not be undone; the next start keeps every file and starts in safe mode.", error, undo);
            }
            throw;
        }
        Finish(paths, configurationRoot);
        File.Delete(Journal(journalRoot));
    }

    private static void Place(string source, string target)
    {
        File.Copy(source, target + New, overwrite: true);
        using (var stream = new FileStream(target + New, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(flushToDisk: true);
        File.Move(target + New, target, overwrite: true);
    }

    private static string Target(DatabasePaths paths, BackupDatabases.Item item) => BackupDatabases.PathOf(paths, item.File);

    private static string? Text(JsonObject node, string name) => node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool Flag(JsonObject node, string name) => node[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>
    /// Puts the set-aside files back. A live -wal/-shm is deleted only when its set-aside counterpart exists, or when
    /// the new databases were already placed (then it belongs to them); a database that did not exist before is removed.
    /// </summary>
    private static void Undo(JsonObject journal, string step, DatabasePaths paths, string configurationRoot)
    {
        var placed = step is "placed" or "verified";
        foreach (var item in BackupDatabases.Files)
        {
            var target = Target(paths, item);
            Delete(target + New);
            var existed = journal["existed"]?[item.Name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : true;
            if (placed)
                foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                    if (!File.Exists(target + Old + suffix)) Delete(target + suffix);
            foreach (var suffix in Suffixes)
                if (File.Exists(target + Old + suffix))
                {
                    Delete(target + suffix);
                    File.Move(target + Old + suffix, target + suffix);
                }
            if (!existed && !File.Exists(target + Old))
                foreach (var suffix in Suffixes) Delete(target + suffix);
        }
        var configPath = Path.Combine(configurationRoot, ConfigStore.FileName);
        if (File.Exists(configPath + Old)) File.Move(configPath + Old, configPath, overwrite: true);
        else if (Flag(journal, "config_new") && journal["config_existed"] is JsonValue had && had.TryGetValue<bool>(out var configExisted) && !configExisted)
            Delete(configPath); // the restored config.json goes; none existed before
    }

    /// <summary>Completes an update rollback from its incoming copies (they are kept until its final marker).</summary>
    private static void Forward(JsonObject journal, DatabasePaths paths)
    {
        foreach (var item in BackupDatabases.Files)
        {
            var source = journal["incoming"]?[item.Name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            if (source is not null && !File.Exists(source))
                throw new FileNotFoundException("The copy to finish the interrupted rollback is missing.", Path.GetFileName(source));
        }
        foreach (var item in BackupDatabases.Files)
        {
            var target = Target(paths, item);
            var source = journal["incoming"]?[item.Name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            foreach (var suffix in Suffixes) Delete(target + suffix);
            Delete(target + "-journal");
            if (source is not null) Place(source, target);
        }
    }

    private static void Finish(DatabasePaths paths, string configurationRoot)
    {
        foreach (var item in BackupDatabases.Files)
        {
            var target = Target(paths, item);
            Delete(target + New);
            foreach (var suffix in Suffixes) Delete(target + Old + suffix);
        }
        Delete(Path.Combine(configurationRoot, ConfigStore.FileName) + Old);
    }

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
