using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Tests;

public static class CatalogueMetadataTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("catalogue metadata:102 released populated native title/metadata/edition SQLite cases", Oracle);
        suite.Add("catalogue metadata:96 released concrete-title vote and Unicode tie cases", Votes);
        suite.Add("catalogue metadata: native recompute ignores display locks and retains source/identity/title rows", () =>
        {
            using var f = new Fixture(); var before = f.Evidence();
            f.Database.Write(DatabaseFile.Catalog, c => c.Execute("INSERT INTO display_metadata VALUES(11,1,'{\"author\":{\"mode\":\"locked\",\"value\":\"Display author\"}}','stamp')"));
            lock (f.Gate) f.Database.Write(DatabaseFile.Catalog, c => { f.Metadata.Recompute(c, 11, Stamp); return 0; });
            Check.Equal(before, f.Evidence());
            Check.Equal("Higher priority author", f.Database.Read(DatabaseFile.Catalog, c => (string)c.Scalar("SELECT author FROM entries WHERE id=11")!));
            var display = new DisplayMetadata(f.Database, f.Gate);
            var entry = f.Database.Read(DatabaseFile.Catalog, c => JsonSerializer.SerializeToNode(c.Query("SELECT * FROM entries WHERE id=11").Single())!.AsObject());
            Check.Equal("Display author", display.Overlay(entry)["author"]!.GetValue<string>());
        });
        suite.Add("catalogue metadata: transaction rollback retains peer labels and edition consensus atomically", () =>
        {
            using var f = new Fixture(); var before = f.Entries().ToJsonString();
            Check.Throws<InvalidOperationException>(() => f.Database.Write<int>(DatabaseFile.Catalog, c =>
            { f.Metadata.Recompute(c, 12, Stamp); c.Execute("UPDATE entries SET manual_preferred_title=1 WHERE id=13"); throw new InvalidOperationException("Injected catalogue failure"); }));
            Check.Equal(before, f.Entries().ToJsonString());
            f.Database.Write(DatabaseFile.Catalog, c => { f.Metadata.Recompute(c, 12, Stamp); return 0; });
            Check.Equal("Twin Saga (manga)", f.Database.Read(DatabaseFile.Catalog, c => (string)c.Scalar("SELECT preferred_title FROM entries WHERE id=12")!));
            Check.Equal("Twin Saga (webtoon)", f.Database.Read(DatabaseFile.Catalog, c => (string)c.Scalar("SELECT preferred_title FROM entries WHERE id=13")!));
        });
        suite.Add("catalogue metadata: configured color confidence changes consensus without touching native observations", () =>
        {
            using var f = new Fixture(); var before = f.Evidence();
            f.Minimum = .7; var both = f.Database.Write(DatabaseFile.Catalog, c => f.Metadata.RecomputeColor(c, 11));
            Check.Equal("mixed", both["color_mode"]!.GetValue<string>());
            f.Minimum = .9; var mono = f.Database.Write(DatabaseFile.Catalog, c => f.Metadata.RecomputeColor(c, 11));
            Check.Equal("monochrome", mono["color_mode"]!.GetValue<string>()); Check.Equal(before, f.Evidence());
        });
    }
    private const string Stamp = "2026-10-21T16:00:00+00:00";
    private static JsonObject Data() => JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("catalogue-metadata.json")))!.AsObject();
    private static void Oracle()
    {
        var cases = Data()["cases"]!.AsArray(); Check.Equal(102, cases.Count);
        foreach (var item in cases.OfType<JsonObject>())
        {
            using var f = new Fixture(); f.Minimum = item["minimum"]!.GetValue<double>(); var operation = item["operation"]!.GetValue<string>(); var id = item["args"]![0]!.GetValue<long>();
            var sql = item["mutate"]!.GetValue<string>(); if (sql.Length != 0) f.Database.Write(DatabaseFile.Catalog, c => c.Execute(sql));
            var result = f.Database.Write(DatabaseFile.Catalog, c =>
            {
                switch (operation)
                {
                    case "_choose_metadata": f.Metadata.ChooseMetadata(c, id, Stamp); return null;
                    case "_sync_preferred_title_from_bound_records": return (JsonNode?)JsonValue.Create(f.Metadata.SyncPreferredTitle(c, id, Stamp));
                    case "recompute": f.Metadata.Recompute(c, id, Stamp); return null;
                    case "_recompute_entry_color_profile": return f.Metadata.RecomputeColor(c, id);
                    case "_recompute_entry_media_mode": return JsonValue.Create(f.Metadata.RecomputeMedia(c, id));
                    case "_entry_title_rows": return JsonSerializer.SerializeToNode(CatalogueMetadata.NativeTitleRows(c, id));
                    default: throw new InvalidDataException(operation);
                }
            });
            try
            {
                Check.JsonEqual(JsonSerializer.SerializeToElement(item["expected"]), JsonSerializer.SerializeToElement(result));
                Check.JsonEqual(JsonSerializer.SerializeToElement(item["entries"]), JsonSerializer.SerializeToElement(f.Entries()));
            }
            catch (Exception error) { throw new InvalidOperationException("Released native metadata case " + operation + " / " + id + " / " + sql, error); }
        }
        Console.WriteLine("Catalogue metadata parity cases " + cases.Count);
    }
    private static void Votes()
    {
        var cases = Data()["votes"]!.AsArray(); var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            foreach (var item in cases.OfType<JsonObject>())
            {
                var votes = item["records"]!.AsArray().OfType<JsonObject>().Select(row => new NativeTitleVote(row["source_title"]!.GetValue<string>(), row["lang"]!.GetValue<string>(),
                    row["priority"]!.GetValue<long>(), row["is_variant"]!.GetValue<int>() != 0, row["is_identity"]!.GetValue<int>() != 0));
                Check.Equal(item["expected"]!.GetValue<string>(), CatalogueTitleConsensus.MostBoundTitle(votes, item["current"]!.GetValue<string>()));
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Check.Equal(96, cases.Count); Console.WriteLine("Concrete native title parity cases " + cases.Count);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly Sandbox _temp = new(); public object Gate { get; } = new(); public SqliteDatabase Database { get; } public CatalogueMetadata Metadata { get; }
        public double Minimum { get; set; } = .78;
        public Fixture()
        {
            Database = new(new AutoSourcarrSettings { DataDir = _temp.Path }); new SchemaMigrator(Database).Initialize();
            Database.Write(DatabaseFile.Catalog, c => c.Execute(Data()["catalog_seed"]!.GetValue<string>())); Metadata = new(() => Minimum);
        }
        public JsonNode Entries() => Database.Read(DatabaseFile.Catalog, c => JsonSerializer.SerializeToNode(c.Query("SELECT * FROM entries ORDER BY id"))!);
        public string Evidence() => Database.Read(DatabaseFile.Catalog, c => string.Join('\n', new[] { "source_matches", "source_variants", "titles", "identity_links", "source_color_profiles" }
            .Select(table => table + ":" + JsonSerializer.Serialize(c.Query("SELECT * FROM " + table + " ORDER BY 1")))));
        public void Dispose() { Database.Dispose(); _temp.Dispose(); }
    }
}
