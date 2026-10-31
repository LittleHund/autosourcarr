using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Tests;

public static class CatalogueQueryTests
{
    private const long Big = 9007199254740993;
    public static void Register(TestSuite suite)
    {
        suite.Add("catalogue queries: 82 released summaries, entry, search, alias, description and coverage cases", Oracle);
        suite.Add("catalogue queries: derived title refresh preserves all logical catalogue and origin rows", () =>
        {
            using var fixture = new Fixture(); var before = Snapshot(fixture.Database);
            Check.Equal(11L, fixture.Queries.ExactSearchEntryIds("Alpha Origin").Last());
            Check.Equal(before, Snapshot(fixture.Database));
            Check.True(fixture.Database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT normalized FROM entry_preferred_titles WHERE entry_id=11")) is string);
            fixture.Database.Write(DatabaseFile.Catalog, c => c.Execute("UPDATE entries SET preferred_title='Replaced current title' WHERE id=11"));
            Check.True(fixture.Database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT normalized FROM entry_preferred_titles WHERE entry_id=11")) is null);
            Check.Equal(11L, fixture.Queries.ExactSearchEntryIds("Replaced current title").Single());
        });
        suite.Add("catalogue queries: exact source filters use parameters and retain active variants", () =>
        {
            using var fixture = new Fixture();
            Check.True(fixture.Queries.SourceScopedEntryIds([Big, 11, 12, 11], ["-2"]).SequenceEqual(new long[] { 11, 12, 11 }));
            Check.Equal(0, fixture.Queries.SearchEntryIds("Alpha", sourceIds: ["-2'); DROP TABLE entries;--"]).Count);
            Check.Equal(8L, fixture.Database.Read(DatabaseFile.Catalog, c => (long)c.Scalar("SELECT COUNT(*) FROM entries")!));
            Check.Equal(3, fixture.Queries.Sources().Count); Check.Equal(4L, fixture.Queries.SourceGroups().Single()["member_variants"]!.GetValue<long>());
            Check.True(fixture.Queries.Sources().All(row => row["available"]!.GetValue<bool>()));
        });
        suite.Add("catalogue queries: current alias changes are visible and corrupt cycles cannot return another work", () =>
        {
            using var fixture = new Fixture(); Check.Equal(Big, fixture.Queries.Summaries([90]).Single()["canonical_id"]!.GetValue<long>());
            fixture.Database.Write(DatabaseFile.Catalog, c => c.Execute("UPDATE entry_aliases SET canonical_entry_id=12 WHERE old_entry_id=91"));
            Check.Equal(12L, fixture.Queries.Summaries([90]).Single()["canonical_id"]!.GetValue<long>());
            fixture.Database.Write(DatabaseFile.Catalog, c => c.Execute("UPDATE entry_aliases SET canonical_entry_id=90 WHERE old_entry_id=91"));
            Check.Throws<InvalidDataException>(() => fixture.Queries.Summaries([90]));
            Check.Equal(8L, fixture.Database.Read(DatabaseFile.Catalog, c => (long)c.Scalar("SELECT COUNT(*) FROM entries")!));
        });
        suite.Add("catalogue queries: Unicode metadata length, culture and returned objects stay independent", () =>
        {
            using var fixture = new Fixture(); var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                var rows = fixture.Queries.Summaries([15, 16, 17]);
                Check.True(rows[0]["missing_metadata"]!.AsArray().Any(value => value!.GetValue<string>() == "description"));
                Check.True(rows[1]["metadata_complete"]!.GetValue<bool>()); Check.Equal("Unknown", rows[2]["status_label"]!.GetValue<string>());
                var original = fixture.Queries.Entry(11); original["matches"]!.AsArray().Clear(); original["preferred_title"] = "caller edited";
                Check.Equal(3, fixture.Queries.Entry(11)["matches"]!.AsArray().Count);
                Check.Equal("Alpha Origin", fixture.Queries.Entry(11)["preferred_title"]!.GetValue<string>());
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
        suite.Add("catalogue queries: cancelled read admission joins real SQLite and shared-gate work", () => PhysicalRead().GetAwaiter().GetResult());
    }

    private static void Oracle()
    {
        var root = Data(); var cases = root["cases"]!.AsArray(); Check.Equal(82, cases.Count);
        foreach (var item in cases.OfType<JsonObject>())
        {
            using var fixture = new Fixture(); var args = item["args"]!.AsArray(); var operation = item["operation"]!.GetValue<string>();
            var mutation = item["mutate"]!.GetValue<string>();
            if (mutation.Length != 0) fixture.Database.Write(DatabaseFile.Catalog, c => c.Execute(mutation));
            try
            {
                JsonNode? result = operation switch
                {
                    "_entry_summaries" => JsonSerializer.SerializeToNode(fixture.Queries.Summaries(Ids(args[0]))),
                    "get_entry" => fixture.Queries.Entry(args[0]!.GetValue<long>()),
                    "_search_entry_ids" => JsonSerializer.SerializeToNode(fixture.Queries.SearchEntryIds(args[0]!.GetValue<string>(), args[1]!.GetValue<int>(), args[2] is null ? null : Ids(args[2]), args[3] is null ? null : Strings(args[3]))),
                    "_exact_search_entry_ids" => JsonSerializer.SerializeToNode(fixture.Queries.ExactSearchEntryIds(args[0]!.GetValue<string>(), args[1]!.GetValue<int>(), args[2] is null ? null : Strings(args[2]))),
                    "_source_scoped_entry_ids" => JsonSerializer.SerializeToNode(fixture.Queries.SourceScopedEntryIds(Ids(args[0]), Strings(args[1]))),
                    "entry_list" => JsonSerializer.SerializeToNode(fixture.Queries.List(args[0]!.GetValue<string>(), args[1]!.GetValue<int>())),
                    "entries_by_ids" => JsonSerializer.SerializeToNode(fixture.Queries.ByIds(Ids(args[0]), args[1]!.GetValue<string>(), args[2]!.GetValue<int>())),
                    "source_record_chapter_stats" => RecordStats(fixture.Queries.RecordChapterStats(Ids(args[0]))),
                    "entry_chapter_stats" => EntryStats(fixture.Queries.EntryChapterStats(Ids(args[0]))),
                    "description_for_extension" => JsonValue.Create(CataloguePresentation.DescriptionForExtension(args[0]!.AsObject())),
                    _ => throw new InvalidDataException("Unknown released query case")
                };
                Check.JsonEqual(JsonSerializer.SerializeToElement(item["expected"]), JsonSerializer.SerializeToElement(result));
            }
            catch (Exception error) { throw new InvalidOperationException("Released catalogue case " + operation + " " + args.ToJsonString(), error); }
        }
        Console.WriteLine("Catalogue query parity cases " + cases.Count);
    }

    private static async Task PhysicalRead()
    {
        using var fixture = new Fixture(); await using var io = new BoundedWorkScheduler(1, 4); using var cancel = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var blocker = Task.Run(() => { lock (fixture.Gate) { entered.Set(); release.Wait(); } });
        Task<JsonObject>? request = null;
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(3))); request = io.RunAsync(() => fixture.Queries.Entry(Big), cancel.Token);
            Check.True(SpinWait.SpinUntil(() => io.Snapshot.Running > 0, TimeSpan.FromSeconds(3))); cancel.Cancel();
            await Task.Delay(20); Check.True(!request.IsCompleted); release.Set();
            try { await request; throw new InvalidOperationException("Cancelled catalogue read published a result"); } catch (OperationCanceledException) { }
        }
        finally { release.Set(); await blocker; if (request is not null) try { await request; } catch (OperationCanceledException) { } }
    }

    private static string Snapshot(SqliteDatabase database)
    {
        var parts = new List<string>();
        foreach (var file in new[] { DatabaseFile.Catalog, DatabaseFile.Origins }) database.Read(file, c =>
        {
            foreach (var table in c.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name<>'entry_preferred_titles' ORDER BY name"))
            {
                var name = table.Text("name"); var escaped = name.Replace("\"", "\"\"", StringComparison.Ordinal);
                parts.Add(file + ":" + name + ":" + string.Join('|', c.Query("SELECT * FROM \"" + escaped + "\"").Select(row => JsonSerializer.Serialize(row)).Order(StringComparer.Ordinal)));
            }
            return 0;
        });
        return string.Join('\n', parts);
    }
    private static JsonArray RecordStats(IReadOnlyDictionary<(long EntryId, string SourceId, long MangaId), JsonObject> stats)
        => new(stats.OrderBy(pair => pair.Key.EntryId).ThenBy(pair => pair.Key.SourceId, StringComparer.Ordinal).ThenBy(pair => pair.Key.MangaId).Select(pair =>
        { var row = (JsonObject)pair.Value.DeepClone(); row["entry_id"] = pair.Key.EntryId; row["source_id"] = pair.Key.SourceId; row["source_manga_id"] = pair.Key.MangaId; return (JsonNode)row; }).ToArray());
    private static JsonArray EntryStats(IReadOnlyDictionary<long, JsonObject> stats) => new(stats.OrderBy(pair => pair.Key).Select(pair =>
        { var row = (JsonObject)pair.Value.DeepClone(); row["entry_id"] = pair.Key; return (JsonNode)row; }).ToArray());
    private static long[] Ids(JsonNode? value) => value!.AsArray().Select(item => item!.GetValue<long>()).ToArray();
    private static string[] Strings(JsonNode? value) => value!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
    private static JsonObject Data() => JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("catalogue-queries.json")))!.AsObject();
    private sealed class Fixture : IDisposable
    {
        private readonly Sandbox _temp = new();
        public object Gate { get; } = new();
        public SqliteDatabase Database { get; }
        public CatalogueQueries Queries { get; }
        public Fixture()
        {
            var data = Data(); Database = new(new AutoSourcarrSettings { DataDir = _temp.Path }); new SchemaMigrator(Database).Initialize();
            Database.Write(DatabaseFile.Catalog, c => c.Execute(data["catalog_seed"]!.GetValue<string>()));
            Database.Write(DatabaseFile.Origins, c => c.Execute(data["origins_seed"]!.GetValue<string>()));
            var covers = data["cached_covers"]!.AsArray().Select(value => value!.GetValue<long>()).ToHashSet(); Queries = new(Database, Gate, covers.Contains);
        }
        public void Dispose() { Database.Dispose(); _temp.Dispose(); }
    }
}
