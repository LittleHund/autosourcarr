using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Core.Persistence;

public sealed record DatabaseMoveResult(string FromDirectory, string TargetDirectory, long CatalogBytes, long OriginsBytes, string Message);

/// <summary>
/// SET-5: moves both SQLite databases to another mounted folder while the app runs (port of 0.4.x
/// stage/activate relocation). Database work is drained, each file is copied with SQLite's online backup,
/// checked (integrity_check, stamp, row count of every table), renamed into place, the new folder is saved
/// as DATABASE_DIR and the running app switches to it. The old files stay where they were as a safety copy.
/// Any failure removes the copies and leaves the app on the old files.
/// </summary>
public static class DatabaseRelocation
{
    private static readonly string[] Files = ["catalog.sqlite", "chapter-origins.sqlite"];

    public static DatabaseMoveResult Move(SqliteDatabase database, string target, Action<string> saveDirectory, TimeSpan? drainTimeout = null,
        Func<string, IDisposable>? claimTarget = null)
    {
        ArgumentNullException.ThrowIfNull(saveDirectory);
        target = CheckTarget(database, target);
        // The instance lock on the new folder is taken before copying, so a second AutoSourcarr cannot use it.
        IDisposable? claim;
        try { claim = claimTarget?.Invoke(target); }
        catch (IOException error) { throw new ArgumentException("Another AutoSourcarr instance already uses that folder.", error); }
        return MoveChecked(database, target, saveDirectory, drainTimeout, claim);
    }

    /// <summary>Checks a move target and returns it normalized; <see cref="ArgumentException"/> explains a refusal.</summary>
    public static string CheckTarget(SqliteDatabase database, string target)
    {
        ArgumentNullException.ThrowIfNull(database);
        target = (target ?? "").Trim();
        if (target.Length == 0 || !Path.IsPathFullyQualified(target))
            throw new ArgumentException("Enter an absolute folder path mounted into this container, for example /database.");
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (!Directory.Exists(target))
            throw new ArgumentException("That folder does not exist inside the container. Mount it first; an empty replacement is never created.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(database.Paths.Directory));
        if (string.Equals(current, target, comparison))
            throw new ArgumentException("The databases already use this folder.");
        foreach (var name in Files)
            if (File.Exists(Path.Combine(target, name)) || File.Exists(Path.Combine(target, name + "-wal")))
                throw new ArgumentException($"{target} already contains {name}. Choose an empty folder; existing databases are never overwritten.");
        var probe = Path.Combine(target, ".autosourcarr-write-test-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllBytes(probe, []); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new ArgumentException("AutoSourcarr cannot write to that folder. Check its permissions.", error); }
        finally { TryDelete(probe); }
        // The same checks startup makes (no symlinked path parts), so a folder accepted here also starts after a restart.
        try { PrivateDocumentStore.ValidateDirectory(target); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { throw new ArgumentException("That folder or one of its parent folders is a link (symlink); AutoSourcarr refuses linked database folders at startup, so it cannot be used.", error); }
        return target;
    }

    private static DatabaseMoveResult MoveChecked(SqliteDatabase database, string target, Action<string> saveDirectory, TimeSpan? drainTimeout, IDisposable? claim)
    {
        var switched = false;
        try
        {
            var result = database.Quiescent(paths =>
        {
            var copies = new List<string>();
            try
            {
                var sizes = new long[Files.Length];
                for (var index = 0; index < Files.Length; index++)
                {
                    var source = index == 0 ? paths.Catalog : paths.Origins;
                    var temporary = Path.Combine(target, "." + Files[index] + ".moving");
                    copies.Add(temporary);
                    Copy(source, temporary);
                    Verify(source, temporary, Files[index]);
                    sizes[index] = new FileInfo(temporary).Length;
                }
                for (var index = 0; index < Files.Length; index++)
                {
                    var final = Path.Combine(target, Files[index]);
                    File.Move(copies[index], final);
                    copies[index] = final;
                }
                foreach (var final in copies)
                    UseWal(final);
                saveDirectory(target);
                database.SwitchPaths(new DatabasePaths(target, Path.Combine(target, Files[0]), Path.Combine(target, Files[1])));
                return new DatabaseMoveResult(paths.Directory, target, sizes[0], sizes[1],
                    $"Databases copied, checked and switched to {target}. The old files in {paths.Directory} were kept as a safety copy; delete them once you have checked the app.");
            }
            catch
            {
                foreach (var copy in copies)
                    TryDelete(copy);
                throw;
            }
        }, drainTimeout ?? TimeSpan.FromSeconds(60));
            switched = true;
            return result;
        }
        finally
        {
            if (!switched)
                claim?.Dispose();
        }
    }

    /// <summary>The copy is written as a self-contained rollback-journal file; the placed file goes back to WAL.</summary>
    private static void UseWal(string path)
    {
        using var connection = Connect(path, SqliteOpenMode.ReadWrite);
        connection.Open();
        if (!string.Equals(Text(connection, "PRAGMA journal_mode=WAL"), "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Path.GetFileName(path)} could not be switched to WAL mode; nothing was switched.");
    }

    private static SqliteConnection Connect(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = mode, Pooling = false
    }.ToString());

    private static void Copy(string source, string destination)
    {
        using var from = Connect(source, SqliteOpenMode.ReadOnly);
        using var to = Connect(destination, SqliteOpenMode.ReadWriteCreate);
        from.Open();
        to.Open();
        from.BackupDatabase(to);
        using var command = to.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE"; // a self-contained file while it is checked; UseWal switches the placed file back
        command.ExecuteNonQuery();
    }

    private static void Verify(string source, string copy, string name)
    {
        using var from = Connect(source, SqliteOpenMode.ReadOnly);
        using var to = Connect(copy, SqliteOpenMode.ReadOnly);
        from.Open();
        to.Open();
        if (!string.Equals(Text(to, "PRAGMA integrity_check"), "ok", StringComparison.Ordinal))
            throw new InvalidDataException($"The copy of {name} failed SQLite's integrity check; nothing was switched.");
        foreach (var pragma in new[] { "PRAGMA application_id", "PRAGMA user_version" })
            if (!string.Equals(Text(from, pragma), Text(to, pragma), StringComparison.Ordinal))
                throw new InvalidDataException($"The copy of {name} has a different schema stamp; nothing was switched.");
        var tables = Tables(from);
        if (!tables.SequenceEqual(Tables(to), StringComparer.Ordinal))
            throw new InvalidDataException($"The copy of {name} has different tables; nothing was switched.");
        foreach (var table in tables)
        {
            var sql = "SELECT COUNT(*) FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            if (!string.Equals(Text(from, sql), Text(to, sql), StringComparison.Ordinal))
                throw new InvalidDataException($"The copy of {name} has a different row count in {table}; nothing was switched.");
        }
    }

    private static string[] Tables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static string Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
