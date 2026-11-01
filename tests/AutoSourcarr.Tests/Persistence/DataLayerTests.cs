using System.Globalization;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Tests;

/// <summary>Phase 2: FluentMigrator migrations, 0.3.x/0.4.4 upgrades, Dapper sessions, PERF-5 speed layer, database move.</summary>
public static class DataLayerTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("data layer: new databases are created by FluentMigrator, stamped, and a second start changes nothing", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            Check.Equal("2026103101,2026103102,2026110180,2026110184", Versions(database, DatabaseFile.Catalog));
            Check.Equal("2026103111", Versions(database, DatabaseFile.Origins));
            Check.Equal(2026110184L, SchemaMigrator.LatestMigration(DatabaseFile.Catalog));
            Check.Equal(2026103111L, SchemaMigrator.LatestMigration(DatabaseFile.Origins));
            foreach (var file in new[] { DatabaseFile.Catalog, DatabaseFile.Origins })
            {
                Check.Equal((long)SchemaMigrator.SchemaApplicationId, Scalar(database, file, "PRAGMA application_id"));
                Check.Equal((long)SchemaMigrator.SchemaRevision, Scalar(database, file, "PRAGMA user_version"));
            }
            Check.True(Scalar(database, DatabaseFile.Catalog, "SELECT COUNT(*) FROM sqlite_master WHERE name='gallery_layout_candidates'") == 1, "Feature tables are part of the migrations.");
            var schema = Schema(database, DatabaseFile.Catalog) + Schema(database, DatabaseFile.Origins);
            new SchemaMigrator(database).Initialize();
            Check.Equal(schema, Schema(database, DatabaseFile.Catalog) + Schema(database, DatabaseFile.Origins));
            Check.Equal("2026103101,2026103102,2026110180,2026110184", Versions(database, DatabaseFile.Catalog));
        });

        foreach (var version in new[] { "0.3.27", "0.4.4" })
            suite.Add($"data layer: a {version} database upgrades cleanly and keeps its rows", () =>
            {
                using var fresh = new Sandbox();
                using var reference = new SqliteDatabase(new AutoSourcarrSettings { DataDir = fresh.Path });
                new SchemaMigrator(reference).Initialize();

                using var temp = new Sandbox();
                var stamp = version == "0.4.4" ? SchemaMigrator.SchemaApplicationId : 0;
                Load(Path.Combine(temp.Path, "catalog.sqlite"), "catalog-" + version + ".sql", stamp);
                Load(Path.Combine(temp.Path, "chapter-origins.sqlite"), "origins-" + version + ".sql", stamp);
                using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
                new SchemaMigrator(database).Initialize();

                foreach (var file in new[] { DatabaseFile.Catalog, DatabaseFile.Origins })
                {
                    var expected = Columns(reference, file);
                    var actual = Columns(database, file);
                    foreach (var (table, columns) in expected)
                    {
                        Check.True(actual.TryGetValue(table, out var present), $"{version}: table {table} missing after upgrade");
                        var missing = columns.Except(present!, StringComparer.Ordinal).ToArray();
                        Check.True(missing.Length == 0, $"{version}: {table} misses {string.Join(',', missing)}");
                    }
                    var indexes = Names(reference, file, "index");
                    var missingIndexes = indexes.Except(Names(database, file, "index"), StringComparer.Ordinal).ToArray();
                    Check.True(missingIndexes.Length == 0, $"{version}: indexes missing: {string.Join(',', missingIndexes)}");
                    Check.Equal("ok", database.Read(file, c => c.Scalar("PRAGMA integrity_check") as string));
                    Check.Equal(0, database.Read(file, c => c.Query("PRAGMA foreign_key_check").Count));
                    Check.Equal((long)SchemaMigrator.SchemaApplicationId, Scalar(database, file, "PRAGMA application_id"));
                }
                Check.Equal("Alpha,Beta", string.Join(',', database.Read(DatabaseFile.Catalog, c => c.QueryAs<string>("SELECT preferred_title FROM entries ORDER BY id"))));
                Check.Equal(0L, Scalar(database, DatabaseFile.Catalog, "SELECT COUNT(*) FROM entry_preferred_titles WHERE normalized IS NULL"));
                Check.Equal("2026103101,2026103102,2026110180,2026110184", Versions(database, DatabaseFile.Catalog));
            });

        suite.Add("data layer: read sessions cannot write; a Write nested in a Read may; Dapper maps typed rows and pages", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            Check.Throws<InvalidOperationException>(() => database.Read(DatabaseFile.Catalog, c => c.Execute("INSERT INTO settings(key,value) VALUES('x','1')")));
            database.Read(DatabaseFile.Catalog, c => database.Write(DatabaseFile.Catalog, w => w.Execute("INSERT INTO settings(key,value) VALUES('nested','1')")));
            Check.Equal("1", database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT value FROM settings WHERE key='nested'") as string));
            database.Write(DatabaseFile.Catalog, c =>
            {
                for (var id = 1; id <= 7; id++)
                    c.ExecuteWith("INSERT INTO entries(id,preferred_title,status,created_at,updated_at) VALUES(@id,@title,0,'t',@updated)",
                        new { id, title = "Title " + id, updated = "2026-10-" + (10 + id).ToString(CultureInfo.InvariantCulture) });
                return 0;
            });
            var page = database.Read(DatabaseFile.Catalog, c => c.QueryPage<EntryRow>(
                "SELECT id AS Id, preferred_title AS Title, updated_at AS UpdatedAt FROM entries ORDER BY updated_at DESC, id DESC", null, 3));
            Check.Equal("7,6,5", string.Join(',', page.Items.Select(row => row.Id)));
            Check.True(page.HasMore);
            var last = page.Items[^1];
            var next = database.Read(DatabaseFile.Catalog, c => c.QueryPage<EntryRow>(
                "SELECT id AS Id, preferred_title AS Title, updated_at AS UpdatedAt FROM entries WHERE (updated_at,id) < (@at,@id) ORDER BY updated_at DESC, id DESC",
                new { at = last.UpdatedAt, id = last.Id }, 10));
            Check.Equal("4,3,2,1", string.Join(',', next.Items.Select(row => row.Id)));
            Check.True(!next.HasMore);
            Check.Throws<ArgumentException>(() => database.Read(DatabaseFile.Catalog, c => c.QueryPage<EntryRow>("SELECT 1 LIMIT 5", null, 3)));
        });

        suite.Add("data layer: unchanged values are not written again (PERF-5 changed rows only)", () =>
        {
            using var temp = new Sandbox();
            var settings = new AutoSourcarrSettings { DataDir = temp.Path };
            using var database = new SqliteDatabase(settings);
            new SchemaMigrator(database).Initialize();
            var repository = new SettingsRepository(database);
            repository.Set("speed", System.Text.Json.Nodes.JsonValue.Create(1));
            using var observer = new SqliteConnection("Data Source=" + DatabasePaths.Resolve(settings).Catalog + ";Pooling=False");
            observer.Open();
            long DataVersion() { using var command = observer.CreateCommand(); command.CommandText = "PRAGMA data_version"; return (long)command.ExecuteScalar()!; }
            var before = DataVersion();
            repository.Set("speed", System.Text.Json.Nodes.JsonValue.Create(1));
            Check.Equal(before, DataVersion());
            repository.Set("speed", System.Text.Json.Nodes.JsonValue.Create(2));
            Check.True(DataVersion() != before, "A real change is written.");
        });

        suite.Add("data layer: the hot lookups use indexes, not full table scans", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            foreach (var (file, sql) in new[]
            {
                (DatabaseFile.Catalog, "SELECT id FROM entries ORDER BY updated_at DESC, id DESC LIMIT 50"),
                (DatabaseFile.Catalog, "SELECT entry_id FROM titles WHERE normalized='x'"),
                (DatabaseFile.Catalog, "SELECT * FROM source_matches WHERE entry_id=1 AND active=1"),
                (DatabaseFile.Catalog, "SELECT * FROM source_variants WHERE entry_id=1 AND active=1"),
                (DatabaseFile.Origins, "SELECT * FROM virtual_chapters WHERE entry_id=1 AND hidden=0 ORDER BY chapter_number"),
                (DatabaseFile.Origins, "SELECT * FROM chapter_candidates WHERE virtual_chapter_id=1"),
                (DatabaseFile.Origins, "SELECT * FROM chapter_candidates WHERE source_id='1' AND source_chapter_id=2")
            })
            {
                var plan = string.Join(" | ", database.Read(file, c => c.Query("EXPLAIN QUERY PLAN " + sql)).Select(row => row.Text("detail")));
                Check.True(!plan.Contains("SCAN ", StringComparison.Ordinal) || plan.Contains("USING INDEX", StringComparison.Ordinal) || plan.Contains("USING COVERING INDEX", StringComparison.Ordinal),
                    "Full scan for: " + sql + " → " + plan);
            }
        });

        suite.Add("data layer: databases move to another folder, checked, switched live, old files kept (SET-5)", () =>
        {
            using var temp = new Sandbox();
            var data = Path.Combine(temp.Path, "data");
            var target = Path.Combine(temp.Path, "database");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(target);
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = data });
            new SchemaMigrator(database).Initialize();
            database.Write(DatabaseFile.Catalog, c => c.Execute("INSERT INTO entries(id,preferred_title,status,created_at,updated_at) VALUES(1,'Moved',0,'t','t')"));
            string? saved = null;
            Check.Throws<ArgumentException>(() => DatabaseRelocation.Move(database, "relative/path", directory => saved = directory));
            Check.Throws<ArgumentException>(() => DatabaseRelocation.Move(database, Path.Combine(temp.Path, "missing"), directory => saved = directory));
            Check.Throws<ArgumentException>(() => DatabaseRelocation.Move(database, data, directory => saved = directory));
            // A symlinked folder is refused here because startup refuses it too.
            if (!OperatingSystem.IsWindows())
            {
                var linked = Path.Combine(temp.Path, "linked");
                Directory.CreateSymbolicLink(linked, target);
                Check.Throws<ArgumentException>(() => DatabaseRelocation.Move(database, linked, directory => saved = directory));
            }
            // A folder another instance holds is refused before anything is copied.
            Check.Throws<ArgumentException>(() => DatabaseRelocation.Move(database, target, directory => saved = directory, claimTarget: _ => throw new IOException("locked")));
            Check.True(saved is null && !File.Exists(Path.Combine(target, "catalog.sqlite")));
            var claim = new Claim();
            var result = DatabaseRelocation.Move(database, target, directory => saved = directory, claimTarget: _ => claim);
            Check.True(!claim.Disposed, "The instance lock on the new folder is kept after a successful move.");
            Check.Equal(target, saved);
            foreach (var name in new[] { "catalog.sqlite", "chapter-origins.sqlite" })
                using (var moved = new SqliteConnection("Data Source=" + Path.Combine(target, name) + ";Pooling=False"))
                {
                    moved.Open();
                    using var mode = moved.CreateCommand();
                    mode.CommandText = "PRAGMA journal_mode";
                    Check.Equal("wal", (mode.ExecuteScalar() as string)?.ToLowerInvariant());
                }
            Check.Equal(target, database.Paths.Directory);
            Check.True(File.Exists(Path.Combine(data, "catalog.sqlite")) && File.Exists(Path.Combine(target, "catalog.sqlite")) && File.Exists(Path.Combine(target, "chapter-origins.sqlite")));
            Check.True(result.CatalogBytes > 0 && result.Message.Contains("safety copy", StringComparison.Ordinal));
            Check.Equal("Moved", database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT preferred_title FROM entries WHERE id=1") as string));
            database.Write(DatabaseFile.Catalog, c => c.Execute("UPDATE entries SET preferred_title='After' WHERE id=1"));
            using (var old = new SqliteConnection("Data Source=" + Path.Combine(data, "catalog.sqlite") + ";Mode=ReadOnly;Pooling=False"))
            {
                old.Open();
                using var command = old.CreateCommand();
                command.CommandText = "SELECT preferred_title FROM entries WHERE id=1";
                Check.Equal("Moved", command.ExecuteScalar() as string);
            }
            // A folder that already holds databases is never overwritten.
            Check.Throws<ArgumentException>(() => DatabaseRelocation.Move(database, data, directory => saved = directory));
            // A failing save of the new folder leaves everything as it was.
            var third = Path.Combine(temp.Path, "third");
            Directory.CreateDirectory(third);
            var failedClaim = new Claim();
            Check.Throws<IOException>(() => DatabaseRelocation.Move(database, third, _ => throw new IOException("config.json is read-only"), claimTarget: _ => failedClaim));
            Check.True(failedClaim.Disposed, "The lock on a folder that was not switched to is released.");
            Check.Equal(target, database.Paths.Directory);
            Check.True(!File.Exists(Path.Combine(third, "catalog.sqlite")), "Copies are removed after a failure.");
        });

        suite.Add("data layer: a write nested in a read writes only for its own call", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            database.Read(DatabaseFile.Catalog, outer =>
            {
                Check.True(!outer.CanWrite);
                database.Write(DatabaseFile.Catalog, inner => inner.Execute("INSERT INTO entries(id,preferred_title,status,created_at,updated_at) VALUES(7,'Nested',0,'t','t')"));
                Check.True(!outer.CanWrite, "The outer read stays read-only after the nested write.");
                Check.Throws<InvalidOperationException>(() => outer.Execute("DELETE FROM entries"));
                return 0;
            });
            Check.Equal("Nested", database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT preferred_title FROM entries WHERE id=7") as string));
        });

        suite.Add("data layer: the moved folder is saved as an app value that wins over Compose", () =>
        {
            using var temp = new Sandbox();
            var editor = new SettingsEditor(temp.Path, () => new AutoSourcarrSettings(), new Dictionary<string, string?> { ["DATABASE_DIR"] = "/old" });
            editor.SaveDatabaseDirectory("/database");
            Check.Equal("/database", new ConfigStore(temp.Path).Load().Settings["DATABASE_DIR"]);
            Check.True(editor.ManagedKeys().Contains("DATABASE_DIR") && !editor.OverriddenByEnvironment("DATABASE_DIR"));
        });
    }

    private sealed class Claim : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed record EntryRow(long Id, string Title, string UpdatedAt);

    private static void Load(string path, string fixture, int applicationId)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = File.ReadAllText(ConfigurationTests.Fixture(Path.Combine("legacy", fixture)));
        command.ExecuteNonQuery();
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA application_id={applicationId}; PRAGMA user_version={(applicationId == 0 ? 0 : 1)};");
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteDatabase database, DatabaseFile file, string sql) =>
        Convert.ToInt64(database.Read(file, c => c.Scalar(sql)), CultureInfo.InvariantCulture);

    private static string Versions(SqliteDatabase database, DatabaseFile file) =>
        string.Join(',', database.Read(file, c => c.QueryAs<long>("SELECT Version FROM VersionInfo ORDER BY Version")));

    private static string Schema(SqliteDatabase database, DatabaseFile file) =>
        string.Join('\n', database.Read(file, c => c.QueryAs<string>("SELECT type||' '||name||' '||IFNULL(sql,'') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name")));

    private static string[] Names(SqliteDatabase database, DatabaseFile file, string type) =>
        database.Read(file, c => c.QueryAs<string>("SELECT name FROM sqlite_master WHERE type=@type AND name NOT LIKE 'sqlite_%'", new { type })).ToArray();

    private static Dictionary<string, string[]> Columns(SqliteDatabase database, DatabaseFile file) =>
        database.Read(file, c => Names(database, file, "table").ToDictionary(table => table,
            table => c.Query($"PRAGMA table_info(\"{table}\")").Select(row => row.Text("name")).ToArray(), StringComparer.Ordinal));
}
