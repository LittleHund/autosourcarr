using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Backup;

/// <summary>
/// Journalled replacement of both databases (and optionally config.json), run only at start before any database is
/// opened (0.4.5 instance_backup apply_pending design). Steps, recorded in <c>instance-backups/swap-journal.json</c>:
/// <list type="number">
/// <item><c>aside</c>: each live file (<c>db</c>, <c>db-wal</c>, <c>db-shm</c>) is renamed to <c>db.swap-old</c>,
/// <c>db.swap-old-wal</c>, <c>db.swap-old-shm</c>; config.json is copied to <c>config.json.swap-old</c>.</item>
/// <item><c>placed</c>: the incoming files are copied next to the target (<c>db.swap-new</c>, flushed) and renamed in place;
/// the new config.json is written atomically.</item>
/// <item><c>verified</c>: the caller's check passed (integrity, stamp, rows).</item>
/// </list>
/// A crash before <c>verified</c> is undone on the next start by renaming the <c>swap-old</c> files back; after it the
/// swap is finished (old files removed). A failure is undone at once. Nothing is re-extracted to roll back.
/// </summary>
public static class DatabaseSwap
{
    public const string JournalFile = "swap-journal.json";
    private const string Old = ".swap-old", New = ".swap-new";

    public sealed record Result(bool Swapped, string? Error);

    private static string Journal(string root) => Path.Combine(root, JournalFile);

    private static void Write(string root, JsonObject journal)
    {
        var path = Journal(root);
        File.WriteAllText(path + ".pending", journal.ToJsonString());
        using (var stream = new FileStream(path + ".pending", FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(flushToDisk: true);
        File.Move(path + ".pending", path, overwrite: true);
    }

    /// <summary>Finishes or undoes a swap a crash interrupted; answers what was done (null when no journal).</summary>
    public static string? Recover(string journalRoot, DatabasePaths paths, string configurationRoot)
    {
        var path = Journal(journalRoot);
        if (!File.Exists(path)) return null;
        JsonObject? journal = null;
        try { journal = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { }
        var step = journal?["step"]?.GetValue<string>() ?? "begin";
        var kind = journal?["kind"]?.GetValue<string>() ?? "swap";
        if (step == "verified")
        {
            Finish(paths, configurationRoot);
            File.Delete(path);
            return $"An interrupted {kind} was completed.";
        }
        Undo(paths, configurationRoot);
        File.Delete(path);
        return $"An interrupted {kind} was undone; the databases are as they were before it.";
    }

    /// <summary>
    /// Replaces both databases by <paramref name="incoming"/> (file name → source file) and, when given, writes
    /// <paramref name="config"/> as config.json. <paramref name="verify"/> runs on the placed files; any failure undoes
    /// the swap by renaming and rethrows the original error.
    /// </summary>
    public static void Swap(string journalRoot, DatabasePaths paths, string configurationRoot, IReadOnlyDictionary<string, string> incoming,
        byte[]? config, string kind, Action verify)
    {
        Directory.CreateDirectory(journalRoot);
        var journal = new JsonObject { ["kind"] = kind, ["step"] = "begin", ["at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) };
        Write(journalRoot, journal);
        try
        {
            // Leftovers of an older finished swap must not be mistaken for this one's.
            foreach (var item in BackupDatabases.Files) foreach (var suffix in Suffixes) Delete(Target(paths, item) + Old + suffix);
            foreach (var item in BackupDatabases.Files)
            {
                var target = Target(paths, item);
                foreach (var suffix in Suffixes)
                    if (File.Exists(target + suffix)) File.Move(target + suffix, target + Old + suffix);
            }
            var configPath = Path.Combine(configurationRoot, ConfigStore.FileName);
            Delete(configPath + Old);
            if (config is not null && File.Exists(configPath)) File.Copy(configPath, configPath + Old);
            journal["step"] = "aside"; Write(journalRoot, journal);

            foreach (var item in BackupDatabases.Files)
            {
                var target = Target(paths, item);
                File.Copy(incoming[item.Name], target + New, overwrite: true);
                using (var stream = new FileStream(target + New, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(flushToDisk: true);
                File.Move(target + New, target);
            }
            if (config is not null) lock (SettingsEditor.Gate(configurationRoot)) ConfigFiles.WriteAtomic(configPath, config);
            journal["step"] = "placed"; Write(journalRoot, journal);
            verify();
            journal["step"] = "verified"; Write(journalRoot, journal);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            try { Undo(paths, configurationRoot); File.Delete(Journal(journalRoot)); }
            catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
            {
                // The journal stays: the next start undoes it again before opening anything.
                throw new AggregateException("The swap failed and could not be undone yet; the next start finishes the undo.", error, undo);
            }
            throw;
        }
        Finish(paths, configurationRoot);
        File.Delete(Journal(journalRoot));
    }

    private static readonly string[] Suffixes = ["", "-wal", "-shm"];

    private static string Target(DatabasePaths paths, BackupDatabases.Item item) => BackupDatabases.PathOf(paths, item.File);

    private static void Undo(DatabasePaths paths, string configurationRoot)
    {
        foreach (var item in BackupDatabases.Files)
        {
            var target = Target(paths, item);
            Delete(target + New);
            if (!File.Exists(target + Old)) continue; // never moved aside: the live file is untouched
            foreach (var suffix in Suffixes) Delete(target + suffix);
            Delete(target + "-journal");
            foreach (var suffix in Suffixes)
                if (File.Exists(target + Old + suffix)) File.Move(target + Old + suffix, target + suffix);
        }
        var configPath = Path.Combine(configurationRoot, ConfigStore.FileName);
        if (File.Exists(configPath + Old)) File.Move(configPath + Old, configPath, overwrite: true);
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
