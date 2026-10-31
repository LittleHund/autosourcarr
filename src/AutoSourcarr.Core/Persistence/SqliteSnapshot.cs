using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Core.Persistence;

/// <summary>One-file WAL-aware snapshot; paired publication belongs to its recovery journal.</summary>
public static class SqliteSnapshot
{
    public static void CopyToNew(string source, string target)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        if (!File.Exists(source))
            throw new FileNotFoundException("The source database is missing; no empty replacement will be created.", source);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException("A snapshot target already exists; it will not be overwritten.");
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, ".autosourcarr-snapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var input = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = source,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString()))
            using (var output = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporary,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                input.Open();
                output.Open();
                input.BackupDatabase(output);
                using var check = output.CreateCommand();
                check.CommandText = "PRAGMA quick_check";
                if (check.ExecuteScalar() as string != "ok")
                    throw new InvalidDataException("The staged SQLite snapshot failed verification.");
            }
            using (var file = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                file.Flush(flushToDisk: true);
            File.Move(temporary, target, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
