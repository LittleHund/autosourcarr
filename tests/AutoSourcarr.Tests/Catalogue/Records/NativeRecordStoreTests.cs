using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Catalogue.Records;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Catalogue.Tags;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Tests;

public static class NativeRecordStoreTests
{
    private static readonly Lazy<JsonObject> Fixture = new(() =>
    {
        var bytes = File.ReadAllBytes(ConfigurationTests.Fixture("native-records.json"));
        Check.Equal("cdc4a86d4e63a88f7de0dd819c6dfbd56c00aa11eb28c28d50848cde9f160ed4", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var value = JsonNode.Parse(bytes)!.AsObject(); Check.Equal("native-records-factored-v1", S(value, "format"));
        Check.Equal("3bedb110210bc64b0c80aae5c4ff9d837e5255ae0cb137f13b181e33909c16c9", S(value["source"]!, "sha256"));
        Check.Equal(14847386L, L(value["source"]!, "bytes")); Check.Equal(190, value["snapshots"]!.AsArray().Count); Check.Equal(230, value["table_arrays"]!.AsArray().Count);
        return value;
    });
    private static readonly Lazy<JsonObject> TextFixture = new(() =>
    {
        var bytes = File.ReadAllBytes(ConfigurationTests.Fixture("native-record-text-types.json")); Check.True(bytes.Length < 4 * 1024 * 1024);
        Check.Equal("2901f2c972dda1f5383a6eca38d319a8c47cf4e84b7195d9ac72be7e6ef7ceaa", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var value = JsonNode.Parse(bytes)!.AsObject(); Check.Equal("native-record-text-types-projected-v1", S(value, "format"));
        Check.Equal("f53c07c25ac9342499e66c4d19c573acfea119e03d941e16db7dcf7e4280569b", S(value["source"]!, "sha256"));
        Check.Equal(8804329L, L(value["source"]!, "bytes")); Check.Equal(96, value["oracle"]!["cases"]!.AsArray().Count); return value;
    });
    private static JsonObject Corpus => Fixture.Value["oracle"]!.AsObject();
    public static void Register(TestSuite suite)
    {
        suite.Add("native records:33 released status and source-genre coercions preserve types and errors", () =>
        {
            var count = 0; var failures = 0;
            foreach (var value in Corpus["pure"]!.AsArray())
            {
                try
                {
                    JsonNode? result = null; var input = value!["input"]?.DeepClone();
                    if (value["error"] is not null) { Check.Throws<ArgumentException>(() => NativeRecordStore.Status(input)); failures++; }
                    else { result = S(value, "kind") == "status" ? JsonValue.Create(NativeRecordStore.Status(input)) : JsonValue.Create(NativeRecordStore.GenreText(input)); Equal(value["result"]!, result!); }
                    count++;
                }
                catch (Exception error) { throw new InvalidOperationException(S(value!, "name"), error); }
            }
            Check.Equal(33, count); Check.Equal(1, failures);
        });
        suite.Add("native records:62 real SQLite scenarios preserve112 original prefix operations and partial failures", () =>
        {
            var count = 0; var operations = 0; var failures = 0; var phases = 0;
            foreach (var value in Corpus["cases"]!.AsArray())
            {
                using var f = new Setup(value!.AsObject()); var index = 0;
                foreach (var step in value["steps"]!.AsArray())
                {
                    try { f.Step(step!.AsObject()); operations++; if (step["error"] is not null) failures++; phases += step["phases"]!.AsArray().Count; }
                    catch (Exception error) { throw new InvalidOperationException(S(value, "name") + " step " + index, error); }
                    index++;
                }
                count++;
            }
            Check.Equal(62, count); Check.Equal(112, operations); Check.Equal(5, failures); Check.Equal(515, phases);
        });
        suite.Add("native records:96 original text-type outcomes preserve SQLite affinity, errors and committed prefixes", () =>
        {
            var count = 0; var contexts = 0; var errors = new Dictionary<string, int>(StringComparer.Ordinal); var phases = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var node in TextFixture.Value["oracle"]!["cases"]!.AsArray())
            {
                var value = node!.AsObject(); var input = value["input"]!;
                try
                {
                    using var f = new Setup(S(input, "table") == "source_matches" ? First() : Named("variant-owned-refresh-full")); f.TextTypes(value);
                    var phase = S(input, "phase"); phases[phase] = phases.GetValueOrDefault(phase) + 1;
                    if (value["error"] is JsonObject error) { var type = S(error, "type"); errors[type] = errors.GetValueOrDefault(type) + 1; }
                    contexts += value["phases"]!.AsArray().Count; count++;
                }
                catch (Exception error) { throw new InvalidOperationException(S(input, "name"), error); }
            }
            Check.Equal(96, count); Check.Equal(346, contexts); Check.Equal(45, phases["cached-prefix"]); Check.Equal(45, phases["authoritative-phase"]); Check.Equal(6, phases["background-prefix"]);
            Check.Equal(3, errors.Count); Check.Equal(11, errors["ProgrammingError"]); Check.Equal(4, errors["AttributeError"]); Check.Equal(7, errors["TypeError"]);
        });
        suite.Add("native records:current source role, profile and exact row fingerprint fence a caller-owned write", () =>
        {
            foreach (var mutation in new[] { "UPDATE source_matches SET match_type='manual',locked=1", "UPDATE source_config SET display_name='Changed after capture'", "UPDATE source_matches SET metadata_json='{}'", "UPDATE source_matches SET active=0" })
            {
                using var f = new Setup(First()); var token = f.Store.Capture("17", 111)!; f.C(c => c.Execute(mutation)); var before = f.Snapshot(DatabaseFile.Catalog);
                lock (f.Gate) f.Database.Write(DatabaseFile.Catalog, c => { Check.Throws<NativeTagConflictException>(() => f.Store.WriteCached(c, token, Input(First()))); return 0; });
                Equal(before, f.Snapshot(DatabaseFile.Catalog));
            }
        });
        suite.Add("native records:ambiguous physical roles cannot promote, attach or delete a retained variant", () =>
        {
            using var f = new Setup(First());
            f.C(c => c.Execute("INSERT INTO source_variants(id,entry_id,source_id,source_manga_id,source_title,last_checked) VALUES(41,1,'17',111,'Retained same-owner role','old')"));
            var before = f.Snapshot(DatabaseFile.Catalog); Check.Throws<NativeTagConflictException>(() => f.Store.RefreshIfCurrent(1, "17", 111, Input(First()))); Equal(before, f.Snapshot(DatabaseFile.Catalog));
            f.C(c => c.Execute("UPDATE source_variants SET active=0")); before = f.Snapshot(DatabaseFile.Catalog);
            Check.Throws<NativeTagConflictException>(() => f.Store.RefreshIfCurrent(1, "17", 111, Input(First()))); Equal(before, f.Snapshot(DatabaseFile.Catalog));
            f.C(c => c.Execute("DELETE FROM source_variants; UPDATE source_matches SET active=0 WHERE source_id='17'")); before = f.Snapshot(DatabaseFile.Catalog);
            Check.True(!f.Store.RefreshIfCurrent(1, "17", 111, Input(First())).Accepted); Check.True(f.Store.StoreBackgroundPrefix("17", 111, Input(First())).EntryId is null); Equal(before, f.Snapshot(DatabaseFile.Catalog));
            Check.True(!f.Store.RefreshIfCurrent(2, "17", 999, new JsonObject { ["id"] = 999, ["title"] = "Imported fact cannot attach" }).Accepted); Equal(before, f.Snapshot(DatabaseFile.Catalog));
        });
        suite.Add("native records:standalone phases refuse ambient catalogue ownership before any commit claim", () =>
        {
            using var f = new Setup(First()); f.Database.Write(DatabaseFile.Catalog, c =>
            {
                var before = Snapshot(c);
                Check.Throws<InvalidOperationException>(() => f.Store.RefreshIfCurrent(1, "17", 111, Input(First())));
                Check.Throws<InvalidOperationException>(() => f.Store.StoreBackgroundPrefix("17", 111, Input(First())));
                Check.Throws<InvalidOperationException>(() => f.Store.RefreshTitles(1, "17", Input(First()))); Equal(before, Snapshot(c)); return 0;
            });
            var receipt = f.Store.RefreshIfCurrent(1, "17", 111, Input(First())); Check.True(receipt.Accepted && receipt.CachedStored && receipt.TitlesRefreshed); Check.True(!receipt.AuthoritativeStored);
        });
        suite.Add("native records:phase API requires actual gate and this instance's current catalogue session", () =>
        {
            using var f = new Setup(First()); using var other = new Setup(First()); var token = f.Store.Capture("17", 111)!; var before = f.Snapshot(DatabaseFile.Catalog); var foreignBefore = other.Snapshot(DatabaseFile.Catalog);
            f.Database.Write(DatabaseFile.Catalog, c => { Check.Throws<InvalidOperationException>(() => f.Store.WriteCached(c, token, Input(First()))); return 0; });
            DatabaseSession? finished = null; f.Database.Read(DatabaseFile.Catalog, c => { finished = c; return 0; });
            lock (f.Gate) lock (other.Gate) f.Database.Write(DatabaseFile.Catalog, c => other.Database.Write(DatabaseFile.Catalog, foreign =>
            {
                Check.Throws<InvalidOperationException>(() => f.Store.WriteCached(foreign, token, Input(First()))); Check.Throws<InvalidOperationException>(() => f.Store.WriteAuthoritative(foreign, token, Input(First())));
                Check.Throws<InvalidOperationException>(() => f.Store.WriteCached(finished!, token, Input(First())));
                return f.Database.Write(DatabaseFile.Origins, origins => { Check.Throws<InvalidOperationException>(() => f.Store.WriteCached(origins, token, Input(First()))); return 0; });
            }));
            Equal(before, f.Snapshot(DatabaseFile.Catalog)); Equal(foreignBefore, other.Snapshot(DatabaseFile.Catalog));
            Check.Throws<IOException>(() => { lock (f.Gate) f.Database.Write<int>(DatabaseFile.Catalog, c => { f.Store.WriteCached(c, token, new JsonObject { ["id"] = 111, ["title"] = "Uncommitted native work", ["initialized"] = true, ["genre"] = new JsonArray("Uncommitted tag") }); throw new IOException("caller fails after real native row/tag/identity phase"); }); });
            Equal(before, f.Snapshot(DatabaseFile.Catalog));
        });
        suite.Add("native records:cancelled native phase physically drains and rolls back row, tags and lineage", () =>
        {
            using var clock = new BlockingClock(2); using var f = new Setup(Named("owned-forced-gallery-true"), clock); var before = f.Snapshot(DatabaseFile.Catalog); using var cancel = new CancellationTokenSource();
            var work = Task.Run(() => Catch(() => f.Store.RefreshIfCurrent(1, "17", 111, Input(Named("owned-forced-gallery-true")), cancel.Token))); Exception? observed;
            try { Check.True(clock.Entered.Wait(TimeSpan.FromSeconds(5)), "Actual gallery tag writer did not reach its timestamp"); cancel.Cancel(); Check.True(!work.IsCompleted); Check.Throws<TimeoutException>(() => f.Database.Quiescent(_ => 0, TimeSpan.FromMilliseconds(30))); }
            finally { clock.Release.Set(); observed = work.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            Check.True(observed is OperationCanceledException, observed?.ToString() ?? "Expected cancellation"); Equal(before, f.Snapshot(DatabaseFile.Catalog));
            Check.True(f.Store.RefreshIfCurrent(1, "17", 111, Input(Named("owned-forced-gallery-true"))).Accepted);
        });
        suite.Add("native records:cancellation after committed aliases preserves earlier prefix phases without whole-task success", () =>
        {
            using var clock = new BlockingClock(2); using var f = new Setup(Named("primary-background-detail-short-detail"), clock); var origin = f.Snapshot(DatabaseFile.Origins); using var cancel = new CancellationTokenSource();
            var input = Input(Named("primary-background-detail-short-detail")); var work = Task.Run(() => Catch(() => f.Store.StoreBackgroundPrefix("17", 111, input, cancel.Token))); Exception? observed;
            try { Check.True(clock.Entered.Wait(TimeSpan.FromSeconds(5)), "Authoritative phase did not start"); cancel.Cancel(); Check.True(!work.IsCompleted); }
            finally { clock.Release.Set(); observed = work.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            Check.True(observed is OperationCanceledException); Check.True((long)observed!.Data[NativeRecordStore.AttemptedChangesKey]! > 0);
            Check.Equal("The Quiet Garden Revised", f.Scalar("SELECT source_title FROM source_matches WHERE source_id='17'") as string);
            Check.True((f.Scalar("SELECT description FROM source_matches WHERE source_id='17'") as string)!.Length > 100);
            Check.Equal(1L, f.Scalar("SELECT COUNT(*) FROM titles WHERE title_type='matched-alias' AND title='The Quiet Garden Revised'")!); Equal(origin, f.Snapshot(DatabaseFile.Origins));
            var retry = f.Store.StoreBackgroundPrefix("17", 111, input); Check.True(retry.AuthoritativeStored); Check.Equal("Short **raw** authoritative synopsis.", f.Scalar("SELECT description FROM source_matches WHERE source_id='17'") as string);
        });
        suite.Add("native records:pre-cancelled work never waits on another source writer", () =>
        {
            using var f = new Setup(First()); var before = f.Snapshot(DatabaseFile.Catalog); using var cancel = new CancellationTokenSource(); cancel.Cancel(); Task<Exception?> work;
            lock (f.Gate) { work = Task.Run(() => Catch(() => f.Store.RefreshIfCurrent(1, "17", 111, Input(First()), cancel.Token))); Check.True(work.Wait(TimeSpan.FromSeconds(5))); }
            Check.True(work.GetAwaiter().GetResult() is OperationCanceledException); Equal(before, f.Snapshot(DatabaseFile.Catalog));
        });
    }

    private static JsonObject First() => Corpus["cases"]![0]!.AsObject();
    private static JsonObject Named(string name) => Corpus["cases"]!.AsArray().Single(value => S(value!, "name") == name)!.AsObject();
    private static JsonObject Input(JsonObject value) => (JsonObject)value["steps"]![0]!["input"]!["manga"]!.DeepClone();
    private static string S(JsonNode value, string name) => value[name]!.GetValue<string>();
    private static long L(JsonNode value, string name) => value[name]!.GetValue<long>();
    private static Exception? Catch(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
    private static void Equal(JsonNode expected, JsonNode actual) { using var left = JsonDocument.Parse(expected.ToJsonString()); using var right = JsonDocument.Parse(actual.ToJsonString()); Check.JsonEqual(left.RootElement, right.RootElement); }
    private static JsonObject Expected(JsonNode reference)
        => Expected(Fixture.Value, reference);
    private static JsonObject Expected(JsonObject fixture, JsonNode reference)
    {
        var snapshot = fixture["snapshots"]![reference.GetValue<int>()]!.AsObject(); var result = new JsonObject();
        foreach (var pair in snapshot) result[pair.Key] = fixture["table_arrays"]![pair.Value!.GetValue<int>()]!.DeepClone();
        return result;
    }
    private static JsonObject Snapshot(DatabaseSession c)
    {
        var result = new JsonObject(); foreach (var table in c.Query("SELECT name FROM sqlite_master WHERE type='table' AND (name NOT LIKE 'sqlite_%' OR name='sqlite_sequence') AND name<>'VersionInfo' ORDER BY name"))
            result[table.Text("name")] = new JsonArray(c.Query("SELECT * FROM " + table.Text("name") + " ORDER BY rowid").Select(row => JsonSerializer.SerializeToNode(row.ToDictionary(pair => pair.Key, pair => pair.Value))).ToArray());
        return result;
    }
    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _sandbox = new(); private readonly JsonObject _fixture; private readonly MutableClock? _mutableClock; private readonly TimeProvider _clock;
        public SqliteDatabase Database { get; private set; } public NativeRecordStore Store { get; private set; } public object Gate { get; } = new();
        public Setup(JsonObject fixture, TimeProvider? clock = null)
        {
            _fixture = fixture; _clock = clock ?? (_mutableClock = new MutableClock()); Database = NewDatabase();
            Create(Database.Paths.Catalog, ["catalog.sql", "library.sql", "gallery-policy.sql", "catalogue-identity.sql"]); Create(Database.Paths.Origins, ["origins.sql"]);
            foreach (var statement in fixture["seed"]!.AsArray()) C(c => c.Execute(statement!.GetValue<string>()));
            foreach (var statement in fixture["origin_seed"]!.AsArray()) Database.Write(DatabaseFile.Origins, c => c.Execute(statement!.GetValue<string>())); Store = new(Database, Gate, _clock);
        }
        private SqliteDatabase NewDatabase() => new(new AutoSourcarrSettings { DataDir = _sandbox.Path });
        private static void Create(string path, string[] names)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()); connection.Open();
            foreach (var name in names) { using var source = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("AutoSourcarr.Core.Persistence.Schema." + name)!; using var text = new StreamReader(source); using var command = connection.CreateCommand(); command.CommandText = text.ReadToEnd(); command.ExecuteNonQuery(); }
        }
        public void Step(JsonObject step)
        {
            Equal(Expected(step["before"]!), Snapshot(DatabaseFile.Catalog)); _mutableClock!.Now = DateTimeOffset.Parse(S(step, "stamp"), CultureInfo.InvariantCulture);
            var input = step["input"]!; var kind = S(input, "kind"); var manga = input["manga"] is JsonObject payload ? (JsonObject)payload.DeepClone() : new JsonObject(); var unchanged = manga.DeepClone();
            var entry = input["entry_id"]?.GetValue<long>() ?? 1; var source = input["source_id"]?.ToString() ?? "17"; var mid = input["source_manga_id"]?.GetValue<long>() ?? 111;
            JsonNode? result = null; long attempted = 0; Exception? caught = null;
            try
            {
                if (kind == "owned-refresh") { var receipt = Store.RefreshIfCurrent(entry, source, mid, manga); attempted = receipt.AttemptedChanges; result = JsonValue.Create(receipt.Accepted); }
                else if (kind == "background-detail") { var receipt = Store.StoreBackgroundPrefix(source, mid, manga); attempted = receipt.AttemptedChanges; result = receipt.EntryId is long id ? JsonValue.Create(id) : null; }
                else if (kind == "titles") attempted = Store.RefreshTitles(entry, source, manga);
                else if (kind == "sql") C(c => { var before = Convert.ToInt64(c.Scalar("SELECT total_changes()"), CultureInfo.InvariantCulture); c.Execute(S(input, "sql")); attempted = Convert.ToInt64(c.Scalar("SELECT total_changes()"), CultureInfo.InvariantCulture) - before; return 0; });
                else if (kind == "close") { Database.Dispose(); Database = NewDatabase(); Store = new(Database, Gate, _clock); }
                else throw new InvalidDataException("Unknown original record operation");
            }
            catch (Exception error) { caught = error; if (error.Data[NativeRecordStore.AttemptedChangesKey] is long changes) attempted = changes; }
            if (step["error"] is JsonObject expected)
            {
                Check.True(caught is not null, "Original failure became a success"); var type = S(expected, "type");
                Check.True(type switch { "IntegrityError" => caught is SqliteException sql && sql.SqliteErrorCode == 19, "JSONDecodeError" => caught is InvalidDataException, "AttributeError" => caught is InvalidDataException, _ => false }, "Unexpected error: " + caught);
                if (type == "IntegrityError") Check.True(caught!.Message.Contains(S(expected, "message"), StringComparison.Ordinal));
            }
            else { if (caught is not null) throw new InvalidOperationException("Unexpected prefix failure", caught); if (step["result"] is null) Check.True(result is null); else Equal(step["result"]!, result!); }
            Check.Equal(L(step, "attempted_total_changes"), attempted); Equal(Expected(step["after"]!), Snapshot(DatabaseFile.Catalog)); Equal(Expected(_fixture["origins_unchanged"]!), Snapshot(DatabaseFile.Origins));
            Equal(unchanged, manga); // Public storage methods own a clone of the remote frame.
        }
        public void TextTypes(JsonObject value)
        {
            Equal(Expected(TextFixture.Value, value["before"]!), Snapshot(DatabaseFile.Catalog));
            Equal(Expected(TextFixture.Value, value["origins_unchanged"]!), Snapshot(DatabaseFile.Origins));
            var input = value["input"]!; var table = S(input, "table"); var phase = S(input, "phase");
            Equal(value["sqlite_row_before"]!, TypedRow(table)); _mutableClock!.Now = DateTimeOffset.Parse(S(value, "stamp"), CultureInfo.InvariantCulture);
            var manga = (JsonObject)value["manga"]!.DeepClone(); var unchanged = manga.DeepClone(); JsonNode? result = null; Exception? caught = null; long attempted = 0;
            try
            {
                if (phase == "cached-prefix") { var receipt = Store.RefreshIfCurrent(1, "17", 111, manga); attempted = receipt.AttemptedChanges; result = JsonValue.Create(receipt.Accepted); }
                else if (phase == "background-prefix") { var receipt = Store.StoreBackgroundPrefix("17", 111, manga); attempted = receipt.AttemptedChanges; result = receipt.EntryId is long id ? JsonValue.Create(id) : null; }
                else if (phase == "authoritative-phase")
                {
                    var owner = Store.Capture("17", 111)!;
                    lock (Gate) Database.Write(DatabaseFile.Catalog, c => { var receipt = Store.WriteAuthoritative(c, owner, manga); attempted = receipt.AttemptedChanges; return 0; });
                }
                else throw new InvalidDataException("Unknown original text-type operation");
            }
            catch (Exception error) { caught = error; if (error.Data[NativeRecordStore.AttemptedChangesKey] is long changes) attempted = changes; }
            if (value["error"] is JsonObject expected)
            {
                Check.True(caught is InvalidDataException, "Original source type failure was lost or changed: " + caught);
                Check.True(S(expected, "type") is "ProgrammingError" or "AttributeError" or "TypeError");
            }
            else { if (caught is not null) throw new InvalidOperationException("Unexpected source text-type failure", caught); if (value["result"] is null) Check.True(result is null); else Equal(value["result"]!, result!); }
            Check.Equal(L(value, "attempted_total_changes"), attempted); Equal(unchanged, manga);
            // Observe durable commits and rollbacks after closing every owned connection.
            Database.Dispose(); Database = NewDatabase(); Store = new(Database, Gate, _clock);
            Equal(Expected(TextFixture.Value, value["after"]!), Snapshot(DatabaseFile.Catalog)); Equal(value["sqlite_row_after"]!, TypedRow(table));
            Equal(Expected(TextFixture.Value, value["origins_unchanged"]!), Snapshot(DatabaseFile.Origins));
        }
        private JsonObject TypedRow(string table)
        {
            Check.True(table is "source_matches" or "source_variants");
            var columns = new[] { "source_manga_url", "source_title", "thumbnail_url", "description", "author", "artist", "real_url" };
            return Database.Read(DatabaseFile.Catalog, c => JsonSerializer.SerializeToNode(c.Query("SELECT *," + string.Join(',', columns.Select(column => "typeof(" + column + ") AS " + column + "__sqlite_type")) + " FROM " + table + " WHERE source_id='17' AND source_manga_id=111").Single().ToDictionary(pair => pair.Key, pair => pair.Value))!.AsObject());
        }
        public void C(Func<DatabaseSession, int> action) { lock (Gate) Database.Write(DatabaseFile.Catalog, action); }
        public object? Scalar(string sql) => Database.Read(DatabaseFile.Catalog, c => c.Scalar(sql));
        public JsonObject Snapshot(DatabaseFile file) => Database.Read(file, NativeRecordStoreTests.Snapshot);
        public void Dispose() { Database.Dispose(); _sandbox.Dispose(); }
    }
    private sealed class MutableClock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-21T00:00:00+00:00", CultureInfo.InvariantCulture); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class BlockingClock(int blockAt) : TimeProvider, IDisposable
    {
        private int _calls; public ManualResetEventSlim Entered { get; } = new(); public ManualResetEventSlim Release { get; } = new();
        public override DateTimeOffset GetUtcNow() { if (Interlocked.Increment(ref _calls) == blockAt) { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controlled record phase did not drain"); } return DateTimeOffset.Parse("2026-10-21T00:00:00+00:00", CultureInfo.InvariantCulture); }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
