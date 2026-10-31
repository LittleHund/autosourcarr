using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Tests;

public static class SettingsRepositoryTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("settings storage: retained JSON values, null and legacy raw text", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            var store = new SettingsRepository(database);
            store.Set("null", null);
            Check.True(store.Get("null", new JsonObject()) is null);
            store.Set("large", JsonValue.Create(9007199254741003L));
            Check.Equal(9007199254741003L, store.Get("large")!.GetValue<long>());
            database.Write(DatabaseFile.Catalog, session => session.Execute("INSERT INTO settings(key,value) VALUES('legacy','not json')"));
            Check.Equal("not json", store.Get("legacy")!.GetValue<string>());
        });
        suite.Add("settings storage: preference merges are atomic and enforce UTF8 byte limits", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            var store = new SettingsRepository(database);
            store.SetUserPreferences(null, new() { ["view"] = "grid" });
            store.SetUserPreferences(null, new() { ["theme"] = "dark" });
            Check.Equal(2, store.UserPreferences(null).Count);
            Check.Throws<ArgumentException>(() => store.SetUserPreferences(null, new() { ["large"] = new string('界', 65536) }));
            Check.Equal(2, store.UserPreferences(null).Count);
            var only = store.SetUserPreferences(null, new() { ["theme"] = "light" }, false);
            Check.Equal(1, only.Count);
            Check.Equal("light", store.UserPreferences(null)["theme"]!.GetValue<string>());
        });
    }
}
