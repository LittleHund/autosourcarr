using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Core.Backup;

/// <summary>What a database file is: its ASC1 stamp, latest FluentMigrator migration and row count per table.</summary>
public sealed record DatabaseFacts(long ApplicationId, long UserVersion, long Migration, IReadOnlyDictionary<string, long> Rows, bool Intact)
{
    public JsonObject ToJson()
    {
        var rows = new JsonObject();
        foreach (var (table, count) in Rows.OrderBy(pair => pair.Key, StringComparer.Ordinal)) rows[table] = count;
        return new JsonObject { ["application_id"] = ApplicationId, ["user_version"] = UserVersion, ["migration"] = Migration, ["rows"] = rows };
    }

    /// <summary>Same stamp, migration and rows (integrity is checked separately).</summary>
    public bool SameData(DatabaseFacts other) => ApplicationId == other.ApplicationId && UserVersion == other.UserVersion && Migration == other.Migration
        && Rows.Count == other.Rows.Count && Rows.All(pair => other.Rows.TryGetValue(pair.Key, out var count) && count == pair.Value);
}

/// <summary>The two databases inside a backup and the SQLite steps on them (snapshot, facts, scrub, swap).</summary>
public static class BackupDatabases
{
    public sealed record Item(DatabaseFile File, string Name, string Entry, string RequiredTable);

    public static readonly IReadOnlyList<Item> Files =
    [
        new(DatabaseFile.Catalog, "catalog.sqlite", "databases/catalog.sqlite", "entries"),
        new(DatabaseFile.Origins, "chapter-origins.sqlite", "databases/chapter-origins.sqlite", "virtual_chapters"),
    ];

    public static string PathOf(DatabasePaths paths, DatabaseFile file) => file == DatabaseFile.Catalog ? paths.Catalog : paths.Origins;

    private static SqliteConnection Connect(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    { DataSource = path, Mode = mode, Pooling = false }.ToString());

    /// <summary>A consistent copy through SQLite's online backup, as one self-contained file (no -wal), checked.</summary>
    public static void Snapshot(string source, string target)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("The database to back up is missing.", source);
        if (File.Exists(target)) File.Delete(target);
        using (var from = Connect(source, SqliteOpenMode.ReadOnly))
        using (var to = Connect(target, SqliteOpenMode.ReadWriteCreate))
        {
            from.Open(); to.Open();
            from.BackupDatabase(to);
            Execute(to, "PRAGMA journal_mode=DELETE");
            if (!string.Equals(Text(to, "PRAGMA quick_check"), "ok", StringComparison.Ordinal))
                throw new BackupRefusedException("The database snapshot failed SQLite's check; no backup was written.", 409);
        }
    }

    /// <summary>Hard rule 11: rows of the catalogue's settings table whose name marks a credential are removed from the copy (secure delete).</summary>
    public static int ScrubSecrets(string catalogCopy)
    {
        using var connection = Connect(catalogCopy, SqliteOpenMode.ReadWrite);
        connection.Open();
        if (!HasTable(connection, "settings")) return 0;
        var keys = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key FROM settings";
            using var reader = command.ExecuteReader();
            while (reader.Read()) if (!reader.IsDBNull(0) && SettingsSecrets.IsSecretKey(reader.GetString(0))) keys.Add(reader.GetString(0));
        }
        if (keys.Count == 0) return 0;
        Execute(connection, "PRAGMA secure_delete=ON");
        foreach (var key in keys)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM settings WHERE key=$key";
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
        }
        Execute(connection, "VACUUM");
        return keys.Count;
    }

    public static DatabaseFacts Facts(string path, bool integrity = true)
    {
        using var connection = Connect(path, SqliteOpenMode.ReadOnly);
        connection.Open();
        var intact = !integrity || string.Equals(Text(connection, "PRAGMA integrity_check"), "ok", StringComparison.Ordinal);
        var applicationId = long.Parse(Text(connection, "PRAGMA application_id"), CultureInfo.InvariantCulture);
        var userVersion = long.Parse(Text(connection, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        var migration = HasTable(connection, "VersionInfo") && Text(connection, "SELECT MAX(Version) FROM VersionInfo") is { Length: > 0 } text
            ? long.Parse(text, CultureInfo.InvariantCulture) : 0;
        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in Tables(connection))
            rows[table] = long.Parse(Text(connection, "SELECT COUNT(*) FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""), CultureInfo.InvariantCulture);
        return new DatabaseFacts(applicationId, userVersion, migration, rows, intact);
    }

    /// <summary>Checks one database of a backup before it may replace a live one: intact, ours, not newer than this build.</summary>
    public static DatabaseFacts Check(string path, Item item, string writerVersion)
    {
        DatabaseFacts facts;
        try { facts = Facts(path); }
        catch (SqliteException error) { throw new BackupRefusedException($"{item.Name} in the backup is not a readable SQLite database.", 400, error); }
        if (!facts.Intact) throw new BackupRefusedException($"{item.Name} in the backup failed SQLite's integrity check.");
        if (facts.ApplicationId is not (0 or SchemaMigrator.SchemaApplicationId))
            throw new BackupRefusedException($"{item.Name} in the backup is not an AutoSourcarr database.");
        if (facts.UserVersion > SchemaMigrator.SchemaRevision || facts.Migration > SchemaMigrator.LatestMigration(item.File))
            throw new BackupRefusedException(Catalogue.Sharing.CatalogueFormat.MismatchMessage(writerVersion)
                + $" ({item.Name} has a newer database schema than this AutoSourcarr understands; nothing was changed).");
        if (!facts.Rows.ContainsKey(item.RequiredTable))
            throw new BackupRefusedException($"{item.Name} in the backup is missing the {item.RequiredTable} table.");
        return facts;
    }

    /// <summary>
    /// Replaces a live database file by a checked file (caller holds the drained storage lease). The live -wal/-shm
    /// are removed first: the pre-restore backup already holds their content.
    /// </summary>
    public static void Place(string source, string target)
    {
        var temporary = target + ".restoring";
        File.Copy(source, temporary, overwrite: true);
        using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(flushToDisk: true);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(target + suffix)) File.Delete(target + suffix);
        File.Move(temporary, target, overwrite: true);
    }

    /// <summary>The placed file goes back to WAL, as the app opens it.</summary>
    public static void UseWal(string path)
    {
        using var connection = Connect(path, SqliteOpenMode.ReadWrite);
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL");
    }

    /// <summary>Every row of each named table that exists, as JSON objects (profiles export).</summary>
    public static JsonObject TableRows(string path, IEnumerable<string> tables)
    {
        using var connection = Connect(path, SqliteOpenMode.ReadOnly);
        connection.Open();
        var result = new JsonObject();
        foreach (var table in tables)
        {
            if (!HasTable(connection, table)) continue;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table + "\"";
            using var reader = command.ExecuteReader();
            var rows = new JsonArray();
            while (reader.Read())
            {
                var row = new JsonObject();
                for (var index = 0; index < reader.FieldCount; index++)
                    row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index) switch
                    {
                        long number => JsonValue.Create(number),
                        double number => JsonValue.Create(number),
                        byte[] blob => JsonValue.Create(Convert.ToBase64String(blob)),
                        var value => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
                    };
                rows.Add(row);
            }
            result[table] = rows;
        }
        return result;
    }

    private static bool HasTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static List<string> Tables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }
}
