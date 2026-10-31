using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Tests;

public static class CatalogueNativeFactTests
{
    private static readonly Lazy<JsonObject> Corpus = new(() =>
    {
        var bytes = File.ReadAllBytes(ConfigurationTests.Fixture("native-fact-projections.json")); Check.True(bytes.Length < 4 * 1024 * 1024);
        Check.Equal("0502318ef9a8493e61f4f5f3f85279f47741d3fbe7f650d9238c498a3a18f832", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var data = JsonNode.Parse(bytes)!.AsObject(); Check.Equal("native-fact-projections-v1", Text(data, "format")); Check.Equal(24, data["cases"]!.AsArray().Count);
        Check.Equal("4196d8c60f9cd85c014e909903147c8295da3f3ba4eda5b54c577341c3f52313", Text(data["oracles"]![0]!, "sha256"));
        Check.Equal("b7bd0a17b9e4eaef0b8b652197d90fc6d2ff44e917dfe44ff1aa2cdb2aa3e574", Text(data["oracles"]![1]!, "sha256")); return data;
    });
    public static void Register(TestSuite suite)
    {
        suite.Add("native facts:24 original facts through actual native Read preserve both SQLite files", () =>
        {
            var count = 0;
            Each((f, value) =>
            {
                var before = f.Snapshot(DatabaseFile.Catalog); var origins = f.Snapshot(DatabaseFile.Origins); var changes = f.Changes();
                var record = f.Read().Single(); Equal(value["fact"]!, record.Fact); Check.Equal(111L, record.MangaId); Check.Equal("17", record.SourceId); Check.Equal(1L, record.EntryId);
                Check.Equal(f.SourceDisplay(), record.Row.Text("source_display_name")); Equal(before, f.Snapshot(DatabaseFile.Catalog)); Equal(origins, f.Snapshot(DatabaseFile.Origins)); Check.Equal(changes, f.Changes()); count++;
            }); Check.Equal(24, count);
        });
        suite.Add("native facts:16 actual original cleaner frames use current source policy and immutable caller inputs", () =>
        {
            var count = 0;
            Each((f, value) =>
            {
                foreach (var call in value["cleaner_calls"]!.AsArray())
                {
                    var input = (JsonObject)call!["input"]!.DeepClone(); var copy = input.DeepClone(); var before = f.Snapshot(DatabaseFile.Catalog); var origins = f.Snapshot(DatabaseFile.Origins);
                    Equal(call["output"]!, f.Gallery.CleanNative(Text(call, "source_id"), input)); Equal(copy, input); Equal(before, f.Snapshot(DatabaseFile.Catalog)); Equal(origins, f.Snapshot(DatabaseFile.Origins)); count++;
                }
            }); Check.Equal(16, count);
        });
        suite.Add("native facts:24 real Export and Preview preserve exact facts and actual local source IDs", () =>
        {
            var count = 0;
            Each((f, value) =>
            {
                var before = f.Snapshot(DatabaseFile.Catalog); var origins = f.Snapshot(DatabaseFile.Origins);
                var exported = f.Exchange.Export(limit: 1, includeDecisions: false); var bundle = exported["catalogue"]!.AsObject(); var entry = bundle["entries"]!.AsArray().Single()!;
                Equal(value["fact"]!, entry["sources"]!.AsArray().Single()!); Check.Equal(1, exported["entries"]!.GetValue<int>());
                var stage = f.Exchange.Preview(Encoding.UTF8.GetBytes(bundle.ToJsonString())); Check.Equal(0, stage["added"]!.GetValue<int>()); Check.Equal(1, stage["reused"]!.GetValue<int>());
                Check.Equal("preview", Text(f.Exchange.Stage(Text(stage, "id")), "state")); Check.Equal(0L, f.Scalar("SELECT COUNT(*) FROM catalogue_record_bindings WHERE source_id='999' OR source_manga_id=999")!);
                Check.Equal(111L, f.Scalar("SELECT source_manga_id FROM source_matches")!); Check.Equal("17", f.Scalar("SELECT source_id FROM source_matches") as string);
                SameNativeState(before, f.Snapshot(DatabaseFile.Catalog)); Equal(origins, f.Snapshot(DatabaseFile.Origins)); count++;
            }); Check.Equal(24, count);
        });
        suite.Add("native facts:row-only gallery URI, disabled profile and retained provenance keep distinct meanings", () =>
        {
            using (var f = new World(Named("profiles/gallery-real-url-only-in-row-not-cleaning-payload")))
            { var fact = f.Read().Single().Fact; Check.True(Text(fact, "reference").StartsWith("asc1:gallery:", StringComparison.Ordinal)); Check.True(!fact["gallery"]!.GetValue<bool>()); }
            using (var f = new World(Named("profiles/forced-gallery-disabled-still-describes-gallery")))
            { Check.True(f.Read().Single().Fact["gallery"]!.GetValue<bool>()); Check.True(!f.Policies.Effective("17")["enabled"]!.GetValue<bool>()); }
            using (var f = new World(Named("profiles/nonempty-gallery-provenance-survives-source-rename")))
            { var fact = f.Read().Single().Fact; Check.True(fact["gallery"]!.GetValue<bool>()); Check.Equal("Retained Gallery Raw Heading", Text(fact, "raw_title")); }
        });
        suite.Add("native facts:cleaning requires source and policy data without reading an unused tag-review graph", () =>
        {
            using var f = new World(Named("profiles/current-display-name-detects-gallery")); var input = (JsonObject)f.Value["cleaner_calls"]![0]!["input"]!.DeepClone();
            f.Write(c => c.Execute("DROP TABLE tag_reviews")); var before = f.Snapshot(DatabaseFile.Catalog); var origins = f.Snapshot(DatabaseFile.Origins);
            Equal(f.Value["cleaner_calls"]![0]!["output"]!, f.Gallery.CleanNative("17", input)); Equal(f.Value["fact"]!, f.Read().Single().Fact);
            Equal(f.Value["fact"]!, f.Exchange.Export(limit: 1, includeDecisions: false)["catalogue"]!["entries"]![0]!["sources"]![0]!);
            SameNativeState(before, f.Snapshot(DatabaseFile.Catalog)); Equal(origins, f.Snapshot(DatabaseFile.Origins));
            // Matching evidence retains its real equivalence dependency; this is not a fake graph.
            Check.Throws<SqliteException>(() => f.Gallery.EvaluateNative(1, "17", input));
        });
        suite.Add("native facts:the same exchange observes new display profiles and real policy CAS revisions", () =>
        {
            using var f = new World(Named("falsey/false")); Check.True(!f.Read().Single().Fact["gallery"]!.GetValue<bool>());
            f.Write(c => c.Execute("UPDATE source_config SET display_name='E-Hentai' WHERE source_id='17'")); Check.True(f.Read().Single().Fact["gallery"]!.GetValue<bool>());
            var policy = GalleryPolicy.Default; policy["profile"] = "gallery"; policy["enabled"] = false; f.Policies.Save("17", policy, 0);
            var bundle = f.Exchange.Export(limit: 1, includeDecisions: false)["catalogue"]!; Check.True(bundle["entries"]![0]!["sources"]![0]!["gallery"]!.GetValue<bool>());
            var stage = f.Exchange.Preview(Encoding.UTF8.GetBytes(bundle.ToJsonString())); f.Policies.Save("17", policy, 1);
            Check.Equal("stale", Text(f.Exchange.Stage(Text(stage, "id")), "state")); var before = f.Snapshot(DatabaseFile.Catalog);
            Check.Throws<ArgumentException>(() => f.Exchange.Apply(Text(stage, "id"), Text(stage, "revision"))); Equal(before, f.Snapshot(DatabaseFile.Catalog));
            policy["profile"] = "auto"; policy["enabled"] = true; f.Policies.Save("17", policy, 2); f.Write(c => c.Execute("UPDATE source_config SET display_name='' WHERE source_id='17'"));
            Check.True(!f.Read().Single().Fact["gallery"]!.GetValue<bool>());
        });
        suite.Add("native facts:source-profile capture requires this instance's current session and shared gate", () =>
        {
            using var f = new World(Named("falsey/false")); using var other = new World(Named("falsey/false"));
            f.Database.Read(DatabaseFile.Catalog, c => { Check.Throws<InvalidOperationException>(() => Capture(f.Gallery, c)); return 0; });
            DatabaseSession? finished = null; f.Database.Read(DatabaseFile.Catalog, c => { finished = c; return 0; });
            lock (f.Gate) lock (other.Gate) f.Database.Read(DatabaseFile.Catalog, c => other.Database.Read(DatabaseFile.Catalog, foreign =>
            {
                Check.Throws<InvalidOperationException>(() => Capture(f.Gallery, foreign)); Check.Throws<InvalidOperationException>(() => Capture(f.Gallery, finished!));
                return f.Database.Read(DatabaseFile.Origins, origins => { Check.Throws<InvalidOperationException>(() => Capture(f.Gallery, origins)); return 0; });
            }));
        });
    }
    private static object Capture(GalleryEvidenceService owner, DatabaseSession c) => Invoke(owner, owner.GetType().GetMethod("CaptureNativeProfiles", BindingFlags.NonPublic | BindingFlags.Instance)!, [c])!;
    private static object? Invoke(object owner, MethodInfo method, object?[] values)
    { try { return method.Invoke(owner, values); } catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; } }
    private static void Each(Action<World, JsonObject> action)
    {
        foreach (var value in Corpus.Value["cases"]!.AsArray())
        {
            var item = value!.AsObject();
            try { using var world = new World(item); action(world, item); }
            catch (Exception error) { throw new InvalidOperationException(Text(item, "name"), error); }
        }
    }
    private static JsonObject Named(string name) => Corpus.Value["cases"]!.AsArray().Single(value => Text(value!, "name") == name)!.AsObject();
    private static string Text(JsonNode value, string field) => value[field]!.GetValue<string>();
    private static void Equal(JsonNode expected, JsonNode actual) => Check.JsonEqual(JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(actual));
    private static void SameNativeState(JsonObject before, JsonObject after)
    {
        var bookkeeping = new HashSet<string>(["catalogue_origin", "catalogue_entry_ids", "catalogue_record_bindings", "catalogue_uid_records", "catalogue_exchange_stages"], StringComparer.Ordinal);
        foreach (var pair in before) if (!bookkeeping.Contains(pair.Key)) Equal(pair.Value!, after[pair.Key]!);
    }
    private static JsonObject Snapshot(DatabaseSession c)
    {
        var value = new JsonObject(); foreach (var table in c.Query("SELECT name FROM sqlite_master WHERE type='table' AND (name NOT LIKE 'sqlite_%' OR name='sqlite_sequence') AND name<>'VersionInfo' ORDER BY name"))
            value[table.Text("name")] = new JsonArray(c.Query("SELECT * FROM " + table.Text("name") + " ORDER BY rowid").Select(row => JsonSerializer.SerializeToNode(row.ToDictionary(pair => pair.Key, pair => pair.Value))).ToArray());
        return value;
    }
    private sealed class World : IDisposable
    {
        private readonly Sandbox _box = new(); public JsonObject Value { get; } public SqliteDatabase Database { get; } public object Gate { get; } = new();
        public GalleryEvidenceService Gallery { get; } public GalleryPolicyRepository Policies { get; } public CatalogueExchange Exchange { get; }
        public World(JsonObject value)
        {
            Value = value; Database = new(new AutoSourcarrSettings { DataDir = _box.Path });
            Create(Database.Paths.Catalog, ["catalog.sql", "library.sql", "gallery-policy.sql", "catalogue-identity.sql", "identity-review.sql"]); Create(Database.Paths.Origins, ["origins.sql"]);
            Write(c =>
            {
                c.Execute("INSERT INTO entries(id,preferred_title,manual_preferred_title,created_at,updated_at) VALUES(1,'Entry local title',1,'seed','seed')");
                foreach (var source in value["source_config"]!.AsArray())
                {
                    var row = (JsonObject)source!.DeepClone();
                    if (Text(value, "oracle") == "falsey")
                    {
                        // The first-eight projection retains only cleaning inputs. The actual
                        // tag_store_oracle.py CaseRunner seed (line365), reused by NativeCase,
                        // supplied this column before research_sharing_falsey.py's source update.
                        Check.True(!row.ContainsKey("last_seen")); row["last_seen"] = "seed";
                    }
                    Insert(c, "source_config", row);
                }
                foreach (var policy in value["gallery_policies"]!.AsArray()) Insert(c, "gallery_policies", policy!.AsObject());
                Insert(c, "source_matches", value["native_row"]!.AsObject(), ["source_name", "base_url"]);
                c.Execute("INSERT INTO titles(entry_id,title,normalized,title_type,source_id,source_manga_id) VALUES(1,'Local decision remains','local decision remains','manual','',0)");
                c.Execute("INSERT INTO preferred_title_overrides VALUES(1,'Entry local title','human-proof')");
                c.Execute("INSERT INTO manual_separate_records(target_entry_id,source_id,source_manga_id,created_at,updated_at) VALUES(1,'18',222,'human-proof','human-proof')"); return 0;
            });
            var origins = Corpus.Value["origins"]![value["origins_index"]!.GetValue<int>()]!.AsObject();
            Database.Write(DatabaseFile.Origins, c =>
            {
                foreach (var name in new[] { "virtual_chapters", "chapter_candidates", "candidate_pages", "source_record_scans", "served_origin", "origin_history", "chapter_candidate_rejections", "quality_samples" })
                    foreach (var row in origins[name]!.AsArray()) Insert(c, name, row!.AsObject()); return 0;
            });
            Gallery = new(Database, Gate); Policies = new(Database, Gate); Exchange = new(Database, Gate, new SupportPrivacy(), galleryEvidence: Gallery.CatalogueAdapter);
        }
        private static void Create(string path, string[] names)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()); connection.Open();
            foreach (var name in names) { using var stream = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("AutoSourcarr.Core.Persistence.Schema." + name)!; using var text = new StreamReader(stream); using var command = connection.CreateCommand(); command.CommandText = text.ReadToEnd(); command.ExecuteNonQuery(); }
        }
        private static void Insert(DatabaseSession c, string table, JsonObject row, string[]? ignored = null)
        {
            var columns = c.Query("PRAGMA table_info(" + table + ")").Select(value => value.Text("name")).ToHashSet(StringComparer.Ordinal); var pairs = row.Where(pair => ignored is null || !ignored.Contains(pair.Key, StringComparer.Ordinal)).ToArray();
            Check.True(pairs.All(pair => columns.Contains(pair.Key)), "Unknown original fixture column");
            c.Execute("INSERT INTO " + table + "(" + string.Join(',', pairs.Select(pair => pair.Key)) + ") VALUES(" + string.Join(',', pairs.Select((_, index) => "$p" + index)) + ")", pairs.Select((pair, index) => ("$p" + index, ScalarValue(pair.Value))).ToArray());
        }
        private static object? ScalarValue(JsonNode? value) => value is null ? null : value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text
            : value is JsonValue flag && flag.TryGetValue<bool>(out var enabled) ? enabled ? 1L : 0L : value is JsonValue integer && integer.TryGetValue<long>(out var number) ? number
            : value is JsonValue real && real.TryGetValue<double>(out var fraction) ? fraction : throw new InvalidDataException("Non-scalar original fixture SQL value");
        public IReadOnlyList<CatalogueNativeRecord> Read()
        {
            lock (Gate) return Database.Read(DatabaseFile.Catalog, c =>
            {
                var owner = typeof(CatalogueExchange).GetField("_native", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Exchange)!;
                return (IReadOnlyList<CatalogueNativeRecord>)Invoke(owner, owner.GetType().GetMethod("Read", BindingFlags.Public | BindingFlags.Instance)!, [c, false])!;
            });
        }
        public string SourceDisplay() => Database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT display_name FROM source_config WHERE source_id='17'") as string ?? "");
        public void Write(Func<DatabaseSession, int> action) { lock (Gate) Database.Write(DatabaseFile.Catalog, action); }
        public JsonObject Snapshot(DatabaseFile file) => Database.Read(file, CatalogueNativeFactTests.Snapshot);
        public (long Catalog, long Origins) Changes() => (Database.Read(DatabaseFile.Catalog, c => Convert.ToInt64(c.Scalar("SELECT total_changes()"), CultureInfo.InvariantCulture)), Database.Read(DatabaseFile.Origins, c => Convert.ToInt64(c.Scalar("SELECT total_changes()"), CultureInfo.InvariantCulture)));
        public object? Scalar(string sql) => Database.Read(DatabaseFile.Catalog, c => c.Scalar(sql));
        public void Dispose() { Database.Dispose(); _box.Dispose(); }
    }
}
