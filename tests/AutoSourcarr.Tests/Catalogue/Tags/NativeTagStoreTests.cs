using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Unicode;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Catalogue.Tags;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Tests;

public static class NativeTagStoreTests
{
    private static readonly Lazy<JsonObject> Corpus = new(() =>
    {
        // Exact oracle bytes are split only to respect the source-index file bound.
        using var joined = new MemoryStream();
        joined.Write(File.ReadAllBytes(ConfigurationTests.Fixture("native-tags-01.txt")));
        joined.Write(File.ReadAllBytes(ConfigurationTests.Fixture("native-tags-02.txt")));
        var bytes = joined.ToArray();
        Check.Equal("10a6d489de7a72e468c3c1e722667686a3b937cfdadc2ac608394ed202294f4a", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return JsonNode.Parse(bytes)!.AsObject();
    });
    public static void Register(TestSuite suite)
    {
        suite.Add("native tags:15 original payload cases retain raw order, scalar bounds and first500", () =>
        {
            var count = 0;
            foreach (var value in Corpus.Value["payloads"]!.AsArray())
            {
                try
                {
                    var input = value!["input"]!.AsObject(); var before = input.DeepClone();
                    Equal(value["result"]!, new JsonArray(NativeTagStore.PayloadTags(input).Select(tag => (JsonNode)tag).ToArray())); Equal(before, input); count++;
                }
                catch (Exception error) { throw new InvalidOperationException(S(value!, "name"), error); }
            }
            Check.Equal(15, count);
        });
        suite.Add("native tags:53 original populated SQLite scenarios retain198 operations and exact changes", () =>
        {
            var cases = 0; var operations = 0; var failures = 0;
            foreach (var value in Corpus.Value["cases"]!.AsArray())
            {
                using var f = new Setup(value!.AsObject()); var index = 0;
                foreach (var step in value["steps"]!.AsArray())
                {
                    try { f.Step(step!.AsObject()); if (step["error"] is not null) failures++; operations++; }
                    catch (Exception error) { throw new InvalidOperationException(S(value, "name") + " step " + index, error); }
                    index++;
                }
                cases++;
            }
            Check.Equal(53, cases); Check.Equal(198, operations); Check.Equal(5, failures);
        });
        suite.Add("native tags: actual row, source profile and manual ownership changes invalidate a captured writer", () =>
        {
            foreach (var mutation in new[] {
                "UPDATE source_matches SET metadata_json='{\"initialized\":true,\"genre\":[\"Changed native frame\"]}'",
                "UPDATE source_matches SET locked=1,match_type='manual'", "UPDATE source_matches SET active=0",
                "UPDATE source_config SET display_name='Changed source name'", "UPDATE source_matches SET entry_id=2",
                "DELETE FROM source_matches" })
            {
                using var f = new Setup(First()); f.C(c => c.Execute("INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(2,'Other native entry','old','old')"));
                var token = f.Store.Capture(NativeTagTable.Matches, 1); f.C(c => c.Execute(mutation)); var before = f.Snapshot(DatabaseFile.Catalog);
                Check.Throws<NativeTagConflictException>(() => f.Store.Synchronize(token)); Equal(before, f.Snapshot(DatabaseFile.Catalog));
            }
            using var current = new Setup(First()); current.SetFrame(1, "{\"initialized\":true,\"genre\":[\"Current native frame\"]}");
            var fresh = current.Store.Capture(NativeTagTable.Matches, 1); current.Store.Synchronize(fresh); Check.Equal("Current native frame", current.Scalar("SELECT name FROM tags") as string);
        });
        suite.Add("native tags: current unique native ownership, aliases and exact large IDs control writes", () =>
        {
            using (var f = new Setup(First()))
            {
                f.SetFrame(1, "{\"initialized\":true,\"genre\":[\"Native fact\"]}"); var token = f.Store.Capture(NativeTagTable.Matches, 1);
                f.C(c => c.Execute("INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(2,'Foreign owner','old','old'); INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,last_checked) VALUES(2,'17',111,'Collision','old')"));
                var before = f.Snapshot(DatabaseFile.Catalog); Check.Throws<NativeTagConflictException>(() => f.Store.Synchronize(token)); Equal(before, f.Snapshot(DatabaseFile.Catalog));
                f.C(c => c.Execute("INSERT INTO entry_aliases VALUES(2,1,'actual retained alias','old')")); var current = f.Store.Capture(NativeTagTable.Matches, 1); f.Store.Synchronize(current); Check.Equal(1L, f.Count("source_record_tags"));
                f.C(c => c.Execute("INSERT INTO entry_aliases VALUES(1,2,'fixture cycle','old')")); before = f.Snapshot(DatabaseFile.Catalog); Check.Throws<NativeTagConflictException>(() => f.Store.Capture(NativeTagTable.Matches, 1)); Equal(before, f.Snapshot(DatabaseFile.Catalog));
            }
            using (var f = new Setup(First()))
            {
                f.C(c => c.Execute("INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(2,'Shared only','old','old'); INSERT INTO catalogue_shared_entries VALUES(2); INSERT INTO catalogue_source_facts VALUES(2,'foreign-reference','17','{\"genre\":[\"Foreign value\"]}')"));
                var before = f.Snapshot(DatabaseFile.Catalog); Check.Throws<NativeTagConflictException>(() => f.Store.Capture(NativeTagTable.Variants, 9007199254740993)); Equal(before, f.Snapshot(DatabaseFile.Catalog));
                f.C(c => c.Execute("INSERT INTO source_config(source_id,name,last_seen) VALUES('9007199254740995','Exact local source','old'); INSERT INTO source_variants(id,entry_id,source_id,source_manga_id,source_title,metadata_json,last_checked) VALUES(9007199254740993,1,'9007199254740995',9007199254740997,'Exact local work','{\"initialized\":true,\"genre\":[\"Exact local tag\"]}','old')"));
                var token = f.Store.Capture(NativeTagTable.Variants, 9007199254740993); Check.Equal(9007199254740997L, token.SourceMangaId); Check.Equal("9007199254740995", token.SourceId); f.Store.Synchronize(token);
                Check.Equal(9007199254740997L, (long)f.Scalar("SELECT source_manga_id FROM source_record_tags")!); Check.Equal(0, f.Store.EntryMetadata(2)["tags"]!.AsArray().Count);
            }
        });
        suite.Add("native tags: caller-owned sessions require the shared gate and preserve complete rollback", () =>
        {
            using var f = new Setup(First()); var before = f.Snapshot(DatabaseFile.Catalog);
            f.Database.Write(DatabaseFile.Catalog, c => { Check.Throws<InvalidOperationException>(() => f.Store.Capture(c, NativeTagTable.Matches, 1)); return 0; });
            Equal(before, f.Snapshot(DatabaseFile.Catalog));
            Check.Throws<IOException>(() =>
            {
                lock (f.Gate) f.Database.Write<int>(DatabaseFile.Catalog, c =>
                {
                    c.Execute("UPDATE source_matches SET metadata_json='{\"initialized\":true,\"genre\":[\"Uncommitted fact\"]}'");
                    f.Store.Synchronize(c, f.Store.Capture(c, NativeTagTable.Matches, 1)); throw new IOException("fixture native caller failure after tag write");
                });
            });
            Equal(before, f.Snapshot(DatabaseFile.Catalog));
            f.SetFrame(1, "{\"initialized\":true,\"genre\":[\"Committed fact\"]}"); f.Store.Synchronize(f.Store.Capture(NativeTagTable.Matches, 1)); Check.Equal("Committed fact", f.Scalar("SELECT name FROM tags") as string);
            using var foreign = new Setup(First()); foreign.SetFrame(1, "{\"initialized\":true,\"genre\":[\"Committed fact\"]}"); var token = f.Store.Capture(NativeTagTable.Matches, 1); before = f.Snapshot(DatabaseFile.Catalog); var foreignBefore = foreign.Snapshot(DatabaseFile.Catalog);
            lock (f.Gate) lock (foreign.Gate) f.Database.Write(DatabaseFile.Catalog, own => foreign.Database.Write(DatabaseFile.Catalog, other =>
            {
                Check.True(f.Database.HasCurrentThreadSession(DatabaseFile.Catalog)); Check.True(!f.Database.IsCurrentThreadSession(DatabaseFile.Catalog, other));
                Check.Throws<InvalidOperationException>(() => f.Store.Capture(other, NativeTagTable.Matches, 1)); Check.Throws<InvalidOperationException>(() => f.Store.Synchronize(other, token)); Check.Throws<InvalidOperationException>(() => f.Store.EntryMetadata(other, 1)); return 0;
            }));
            Equal(before, f.Snapshot(DatabaseFile.Catalog)); Equal(foreignBefore, foreign.Snapshot(DatabaseFile.Catalog));
            var originsBefore = f.Snapshot(DatabaseFile.Origins); DatabaseSession? finished = null; f.Database.Read(DatabaseFile.Catalog, c => { finished = c; return 0; });
            lock (f.Gate) f.Database.Write(DatabaseFile.Catalog, own =>
            {
                Check.True(!f.Database.IsCurrentThreadSession(DatabaseFile.Catalog, finished!)); Check.Throws<InvalidOperationException>(() => f.Store.Capture(finished!, NativeTagTable.Matches, 1)); Check.Throws<InvalidOperationException>(() => f.Store.Synchronize(finished!, token)); Check.Throws<InvalidOperationException>(() => f.Store.EntryMetadata(finished!, 1));
                return f.Database.Write(DatabaseFile.Origins, origins =>
                {
                    Check.True(!f.Database.IsCurrentThreadSession(DatabaseFile.Catalog, origins)); Check.Throws<InvalidOperationException>(() => f.Store.Capture(origins, NativeTagTable.Matches, 1)); Check.Throws<InvalidOperationException>(() => f.Store.Synchronize(origins, token)); Check.Throws<InvalidOperationException>(() => f.Store.EntryMetadata(origins, 1)); return 0;
                });
            });
            Equal(before, f.Snapshot(DatabaseFile.Catalog)); Equal(originsBefore, f.Snapshot(DatabaseFile.Origins));
        });
        suite.Add("native tags: reindex refuses nested cursor acknowledgement and retries only after real commit", () =>
        {
            using var f = new Setup(First()); f.C(c => c.Execute("INSERT INTO settings VALUES('tag_index_version','retained-version')"));
            f.Database.Write(DatabaseFile.Catalog, c =>
            {
                c.Execute("UPDATE source_matches SET metadata_json='{\"initialized\":true,\"genre\":[\"Committed reindex fact\"]}'"); var before = Snapshot(c);
                Check.True(f.Database.HasCurrentThreadSession(DatabaseFile.Catalog)); Check.Throws<InvalidOperationException>(() => f.Reindex.Run("source_matches", 0)); Equal(before, Snapshot(c)); return 0;
            });
            Check.True(!f.Database.HasCurrentThreadSession(DatabaseFile.Catalog)); var result = f.Reindex.Run("source_matches", 0);
            Check.Equal(1L, result.Cursor); Check.Equal(1L, result.Count); Check.Equal(2L, result.Changes); Check.Equal(0L, f.Reindex.Run("source_matches", 0).Changes); Check.Equal("retained-version", f.Scalar("SELECT value FROM settings WHERE key='tag_index_version'") as string);
        });
        suite.Add("native tags: bounded SQL pages process every requested row without truncating a batch", () =>
        {
            using var f = new Setup(First()); f.C(c =>
            {
                for (var i = 1; i <= 257; i++) c.Execute("INSERT INTO source_variants(id,entry_id,source_id,source_manga_id,source_title,metadata_json,last_checked) VALUES($id,1,'17',$mid,'Native variant','{\"initialized\":true,\"genre\":[\"One factual tag\"]}','old')", ("$id", i), ("$mid", 1000 + i)); return 0;
            });
            var result = f.Reindex.Run("source_variants", 0, 257); Check.Equal(257L, result.Cursor); Check.Equal(257L, result.Count); Check.Equal(258L, result.Changes); Check.Equal(257L, f.Count("source_record_tags"));
            var all = f.Reindex.Run("source_variants", 0, -1); Check.Equal(257L, all.Cursor); Check.Equal(257L, all.Count); Check.Equal(0L, all.Changes); Check.Equal(0L, f.Reindex.Run("source_variants", 257).Count);
            Check.Equal(0L, f.Reindex.Run("source_variants", 0, 0).Count); Check.Equal(0L, f.Scalar("SELECT COUNT(*) FROM settings WHERE key='tag_index_version'")!);
        });
        suite.Add("native tags: cancellation retains physical writer ownership until rollback finishes", () =>
        {
            using var clock = new BlockingClock(1); using var f = new Setup(First(), clock); f.SetFrame(1, "{\"sourceName\":\"E-Hentai\",\"initialized\":true,\"genre\":[\"Uncommitted gallery fact\"]}");
            var before = f.Snapshot(DatabaseFile.Catalog); var token = f.Store.Capture(NativeTagTable.Matches, 1); using var cancel = new CancellationTokenSource();
            var work = Task.Run(() => CaptureError(() => f.Store.Synchronize(token, cancel.Token))); Exception? observed;
            try
            {
                Check.True(clock.Entered.Wait(TimeSpan.FromSeconds(5)), "Writer did not reach its actual gallery timestamp boundary"); cancel.Cancel(); Check.True(!work.IsCompleted, "Cancellation returned before the owned synchronous write drained");
                Check.Equal(0L, f.Count("tags")); Check.Throws<TimeoutException>(() => f.Database.Quiescent(_ => 0, TimeSpan.FromMilliseconds(30)));
            }
            finally { clock.Release.Set(); observed = work.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            Check.True(observed is OperationCanceledException); Equal(before, f.Snapshot(DatabaseFile.Catalog)); Check.Equal(0L, f.Reindex.Run("source_matches", 0, 0).Count);
        });
        suite.Add("native tags: cancelled reindex rolls back earlier records and publishes no cursor", () =>
        {
            using var clock = new BlockingClock(2); using var f = new Setup(Named("atomic-reindex-cursor-retry"), clock);
            f.C(c => c.Execute("UPDATE source_matches SET metadata_json='{\"sourceName\":\"E-Hentai\",\"initialized\":true,\"genre\":[\"Admitted gallery fact\"]}'"));
            var before = f.Snapshot(DatabaseFile.Catalog); using var cancel = new CancellationTokenSource();
            var work = Task.Run(() => CaptureError(() => f.Reindex.Run("source_matches", 0, 2, cancel.Token))); Exception? observed;
            try { Check.True(clock.Entered.Wait(TimeSpan.FromSeconds(5))); cancel.Cancel(); Check.True(!work.IsCompleted); Check.Equal(0L, f.Count("source_record_tags")); }
            finally { clock.Release.Set(); observed = work.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            Check.True(observed is OperationCanceledException); Equal(before, f.Snapshot(DatabaseFile.Catalog));
            var retry = f.Reindex.Run("source_matches", 0, 2); Check.Equal(2L, retry.Cursor); Check.Equal(2L, retry.Count); Check.True(retry.Changes > 0); Check.Equal(0L, f.Scalar("SELECT COUNT(*) FROM settings WHERE key='tag_index_version'")!);
        });
        suite.Add("native tags: pre-cancelled admission takes no gate or SQLite mutation", () =>
        {
            using var f = new Setup(First()); var token = f.Store.Capture(NativeTagTable.Matches, 1); var before = f.Snapshot(DatabaseFile.Catalog); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Task<Exception?> work; lock (f.Gate) { work = Task.Run(() => CaptureError(() => f.Store.Synchronize(token, cancel.Token))); Check.True(work.Wait(TimeSpan.FromSeconds(5)), "Cancelled worker waited for the unavailable source gate"); }
            Check.True(work.GetAwaiter().GetResult() is OperationCanceledException); Check.Throws<OperationCanceledException>(() => f.Reindex.Run("source_matches", 0, cancellationToken: cancel.Token)); Equal(before, f.Snapshot(DatabaseFile.Catalog));
        });
    }

    private static JsonObject First() => Corpus.Value["cases"]![0]!.AsObject();
    private static JsonObject Named(string name) => Corpus.Value["cases"]!.AsArray().Single(value => S(value!, "name") == name)!.AsObject();
    private static string S(JsonNode value, string name) => value[name]!.GetValue<string>();
    private static long L(JsonNode value, string name) => value[name]!.GetValue<long>();
    private static Exception? CaptureError(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
    private static void Equal(JsonNode expected, JsonNode actual) { using var left = JsonDocument.Parse(expected.ToJsonString()); using var right = JsonDocument.Parse(actual.ToJsonString()); Check.JsonEqual(left.RootElement, right.RootElement); }
    private static JsonObject Snapshot(DatabaseSession c)
    {
        var result = new JsonObject();
        foreach (var table in c.Query("SELECT name FROM sqlite_master WHERE type='table' AND (name NOT LIKE 'sqlite_%' OR name='sqlite_sequence') AND name<>'VersionInfo' ORDER BY name"))
            result[table.Text("name")] = new JsonArray(c.Query("SELECT * FROM " + table.Text("name") + " ORDER BY rowid").Select(Row).ToArray());
        return result;
    }
    private static JsonNode Row(DatabaseRow row) => JsonSerializer.SerializeToNode(row.ToDictionary(pair => pair.Key, pair => pair.Value))!;
    private static long Changes(DatabaseSession c) => Convert.ToInt64(c.Scalar("SELECT total_changes()"), CultureInfo.InvariantCulture);

    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _sandbox = new(); private readonly JsonObject _fixture; public object Gate { get; } = new(); public SqliteDatabase Database { get; private set; }
        private readonly TimeProvider _clock; private readonly MutableClock? _mutableClock; public NativeTagStore Store { get; private set; } public NativeTagReindex Reindex { get; private set; }
        public Setup(JsonObject fixture, TimeProvider? clock = null)
        {
            _fixture = fixture; _clock = clock ?? (_mutableClock = new MutableClock()); Database = NewDatabase();
            Create(Database.Paths.Catalog, ["catalog.sql", "library.sql", "gallery-policy.sql", "catalogue-identity.sql"]); Create(Database.Paths.Origins, ["origins.sql"]);
            foreach (var statement in fixture["seed"]!.AsArray()) C(c => c.Execute(statement!.GetValue<string>()));
            foreach (var statement in fixture["origin_seed"]!.AsArray()) Database.Write(DatabaseFile.Origins, c => c.Execute(statement!.GetValue<string>()));
            Store = new(Database, Gate, _clock); Reindex = new(Store);
        }
        private SqliteDatabase NewDatabase() => new(new AutoSourcarrSettings { DataDir = _sandbox.Path });
        private static void Create(string path, string[] names)
        {
            // Schema-only setup must not leave a second pool whose connection
            // string is outside the application's actual per-file pool owner.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()); connection.Open();
            foreach (var name in names)
            {
                using var source = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("AutoSourcarr.Core.Persistence.Schema." + name)!; using var text = new StreamReader(source); using var command = connection.CreateCommand(); command.CommandText = text.ReadToEnd(); command.ExecuteNonQuery();
            }
        }
        public void Step(JsonObject step)
        {
            Equal(step["before"]!, Snapshot(DatabaseFile.Catalog)); _mutableClock!.Now = DateTimeOffset.Parse(S(step, "stamp"), CultureInfo.InvariantCulture);
            var input = step["input"]!; var kind = S(input, "kind"); JsonNode? result = null; long attempted = 0; long native = 0; Exception? caught = null;
            try
            {
                if (kind == "sync")
                {
                    var record = _fixture["native_records"]!.AsArray().Single(row => S(row!, "source_id") == S(input, "source_id") && L(row!, "source_manga_id") == L(input, "source_manga_id"))!; var table = S(record, "table");
                    lock (Gate) Database.Write(DatabaseFile.Catalog, c =>
                    {
                        var initial = Changes(c);
                        try
                        {
                            var text = step["staged_native_sql"]![0]!["parameters"]![0]!.GetValue<string>();
                            c.Execute("UPDATE " + table + " SET metadata_json=$text WHERE source_id=$source AND source_manga_id=$manga AND metadata_json<>$text", ("$text", text), ("$source", S(input, "source_id")), ("$manga", L(input, "source_manga_id"))); native = Changes(c) - initial;
                            var row = c.Query("SELECT id FROM " + table + " WHERE source_id=$source AND source_manga_id=$manga", ("$source", S(input, "source_id")), ("$manga", L(input, "source_manga_id"))).Single();
                            Store.Synchronize(c, Store.Capture(c, table == "source_matches" ? NativeTagTable.Matches : NativeTagTable.Variants, row.Integer("id"))); return 0;
                        }
                        finally { attempted = Changes(c) - initial; }
                    });
                }
                else if (kind == "reindex")
                {
                    var batch = Reindex.Run(S(input, "table"), L(input, "after"), L(input, "size")); attempted = batch.Changes; result = new JsonArray(batch.Cursor, batch.Count);
                }
                else if (kind == "read") result = Store.EntryMetadata(L(input, "entry_id"));
                else if (kind == "sql") C(c => { var before = Changes(c); c.Execute(S(input, "sql")); attempted = Changes(c) - before; return 0; });
                else if (kind == "close") { Database.Dispose(); Database = NewDatabase(); Store = new(Database, Gate, _clock); Reindex = new(Store); }
                else throw new InvalidDataException("Unknown oracle operation");
            }
            catch (Exception error) { caught = error; if (kind == "reindex" && error.Data[NativeTagReindex.AttemptedChangesKey] is long changes) attempted = changes; }
            if (step["error"] is JsonObject expected)
            {
                Check.True(caught is not null, "Original failure was silently acknowledged"); var type = S(expected, "type");
                Check.True(type switch { "IntegrityError" => caught is SqliteException sql && sql.SqliteErrorCode == 19, "KeyError" => caught is KeyNotFoundException, "ValueError" => caught is ArgumentException, _ => false }, "Unexpected error: " + caught);
                if (type == "IntegrityError") Check.True(caught!.Message.Contains(S(expected, "message"), StringComparison.Ordinal));
            }
            else { if (caught is not null) throw new InvalidOperationException("Unexpected native tag failure", caught); if (step["result"] is null) Check.True(result is null); else Equal(step["result"]!, result!); }
            Check.Equal(L(step, "attempted_total_changes"), attempted); Check.Equal(L(step, "staged_native_changes"), native); Check.Equal(L(step, "legacy_attempted_changes"), attempted - native);
            Equal(step["after"]!, Snapshot(DatabaseFile.Catalog)); Equal(_fixture["origins_unchanged"]!, Snapshot(DatabaseFile.Origins));
            var canonical = Database.Read(DatabaseFile.Catalog, c =>
            {
                var joined = c.Query("SELECT r.*,l.namespace lns,l.name lname,t.namespace rns,t.name rname FROM tag_reviews r JOIN tags l ON l.id=r.left_id JOIN tags t ON t.id=r.right_id ORDER BY r.left_id,r.right_id"); var tags = new Dictionary<long, JsonObject>(); var reviews = new List<JsonObject>();
                foreach (var row in joined) { var left = row.Integer("left_id"); var right = row.Integer("right_id"); tags[left] = new() { ["id"] = left, ["namespace"] = row.Text("lns"), ["name"] = row.Text("lname") }; tags[right] = new() { ["id"] = right, ["namespace"] = row.Text("rns"), ["name"] = row.Text("rname") }; reviews.Add(Row(row).AsObject()); }
                return GalleryTagEquivalence.CanonicalKeys([JsonValue.Create("sci-fi"), JsonValue.Create("science fiction"), new JsonObject { ["namespace"] = "artist", ["name"] = "sci-fi" }, JsonValue.Create("spaceship"), JsonValue.Create("starship")], GalleryTagEquivalence.Mapping(tags.Values.ToArray(), reviews));
            });
            Equal(step["canonical_probe"]!, new JsonArray(canonical.Order(UnicodeCodePointComparer.Instance).Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()));
        }
        public void SetFrame(long id, string text) => C(c => c.Execute("UPDATE source_matches SET metadata_json=$text WHERE id=$id", ("$text", text), ("$id", id)));
        public void C(Func<DatabaseSession, int> action) { lock (Gate) Database.Write(DatabaseFile.Catalog, action); }
        public object? Scalar(string sql) => Database.Read(DatabaseFile.Catalog, c => c.Scalar(sql)); public long Count(string table) => Convert.ToInt64(Scalar("SELECT COUNT(*) FROM " + table), CultureInfo.InvariantCulture);
        public JsonObject Snapshot(DatabaseFile file) => Database.Read(file, NativeTagStoreTests.Snapshot);
        public void Dispose() { Database.Dispose(); _sandbox.Dispose(); }
    }
    private sealed class MutableClock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-21T00:00:00+00:00", CultureInfo.InvariantCulture); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class BlockingClock(int blockAt) : TimeProvider, IDisposable
    {
        private int _calls; public ManualResetEventSlim Entered { get; } = new(); public ManualResetEventSlim Release { get; } = new();
        public override DateTimeOffset GetUtcNow() { if (Interlocked.Increment(ref _calls) == blockAt) { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controlled native tag timestamp did not drain"); } return DateTimeOffset.Parse("2026-10-21T00:00:00+00:00", CultureInfo.InvariantCulture); }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
