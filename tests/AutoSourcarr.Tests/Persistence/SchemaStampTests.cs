using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Tests;

public static class SchemaStampTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("schema stamp: new files get application_id ASC1 and user_version 1; reopen keeps them", () =>
        {
            using var temp = new Sandbox();
            using (var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path }))
            {
                new SchemaMigrator(database).Initialize();
                foreach (var file in new[] { DatabaseFile.Catalog, DatabaseFile.Origins })
                {
                    Check.Equal((long)SchemaMigrator.SchemaApplicationId, database.Read(file, c => (long)c.Scalar("PRAGMA application_id")!));
                    Check.Equal((long)SchemaMigrator.SchemaRevision, database.Read(file, c => (long)c.Scalar("PRAGMA user_version")!));
                }
                new SchemaMigrator(database).Initialize();
                Check.Equal(1L, database.Read(DatabaseFile.Catalog, c => (long)c.Scalar("PRAGMA user_version")!));
            }
        });
        suite.Add("schema stamp: a newer revision or a foreign application_id is refused unchanged", () =>
        {
            using var temp = new Sandbox();
            var catalog = Path.Combine(temp.Path, "catalog.sqlite");
            Stamp(catalog, SchemaMigrator.SchemaApplicationId, SchemaMigrator.SchemaRevision + 1);
            var before = File.ReadAllBytes(catalog);
            using (var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path }))
            {
                var error = Check.Throws<SchemaRevisionException>(() => new SchemaMigrator(database).Initialize());
                Check.True(error.Message.Contains("schema revision 2", StringComparison.Ordinal), error.Message);
            }
            Check.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(catalog)), "The refused file must not be modified.");
            Stamp(catalog, 0x4B4E4F57, 0);
            using (var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path }))
                Check.Throws<SchemaRevisionException>(() => new SchemaMigrator(database).Initialize());
        });
    }

    private static void Stamp(string path, int applicationId, int revision)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA application_id={applicationId}; PRAGMA user_version={revision}; CREATE TABLE IF NOT EXISTS marker(x);";
        command.ExecuteNonQuery();
    }
}
