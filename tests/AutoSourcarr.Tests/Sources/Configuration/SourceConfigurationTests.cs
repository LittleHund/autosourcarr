using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources.Configuration;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Tests;

public static class SourceConfigurationTests
{
    private const string Source = "-9223372036854775808", Other = "6084907896154116083";
    public static void Register(TestSuite suite)
    {
        suite.Add("source configuration: 74 released SQLite snapshot/group/restore transitions", StatefulOracle);
        suite.Add("source configuration: 38 released matching validation/revision/inheritance cases", MatchingOracle);
        suite.Add("source configuration: complete snapshots detach inputs and reject malformed observations before writes", SnapshotAdmission);
        suite.Add("source configuration: failed native snapshot transaction preserves availability and choices", SnapshotRollback);
        suite.Add("source configuration: malformed preference/rule restore rolls back every source/group change", RestoreRollback);
        suite.Add("source configuration: persisted direction overrides/CAS survive reopen and inherit independently", MatchingPersistence);
        suite.Add("source configuration: concurrent matching edits with one revision have exactly one winner", ConcurrentMatching);
        suite.Add("source configuration: concurrent group/member priority writes retain one authoritative value", ConcurrentGroup);
        suite.Add("source configuration: older SQLite read cannot publish over current source configuration", OldRead);
        suite.Add("source configuration: committed effects require an owner and never claim scheduled jobs", Effects);
    }

    private static void StatefulOracle()
    {
        var fixture = Fixture(); var total = 0;
        foreach (var scenario in fixture["scenarios"]!.AsArray().OfType<JsonObject>())
        {
            using var setup = new Setup(scenario["allowed"]!.AsArray().Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal));
            var snapshots = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var step in scenario["steps"]!.AsArray().OfType<JsonObject>())
            {
                setup.Trace.Clear(); var operation = step["operation"]!.AsObject(); JsonNode? actual = null; Exception? failure = null;
                try { actual = Execute(setup, operation, snapshots); } catch (Exception error) { failure = error; }
                var expected = step["outcome"]!.AsObject();
                if (expected["valid"]!.GetValue<bool>()) { if (failure is not null) throw new InvalidOperationException("Oracle operation failed: " + operation.ToJsonString(), failure); Equal(expected["result"], actual); }
                else Check.True(failure is ArgumentException or KeyNotFoundException, "Expected a rejected operation: " + operation.ToJsonString());
                Equal(step["state"], State(setup.Database)); Equal(step["invalidations"], new JsonArray(setup.Trace.Select(value => (JsonNode)value.DeepClone()).ToArray())); total++;
            }
        }
        Check.Equal(74, total);
    }

    private static JsonNode? Execute(Setup setup, JsonObject operation, IDictionary<string, JsonObject> snapshots)
    {
        var args = operation["args"] as JsonArray ?? new(); var changes = operation["kwargs"] as JsonObject ?? new(); var action = Text(operation["action"]);
        switch (action)
        {
            case "_persist_source_snapshot": return Array(setup.Store.PersistSnapshot(CompletedSourceSnapshot.FromCompleteNativeList(args[0]!.AsArray().OfType<JsonObject>().ToArray())));
            case "update_source": return setup.Store.UpdateSource(Text(args[0]), changes);
            case "create_source_group": return setup.Store.CreateGroup(Text(args[0]), changes["priority"] is null ? 1000 : Integer(changes["priority"]));
            case "update_source_group": return setup.Store.UpdateGroup(Integer(args[0]), changes["name"]?.GetValue<string>(), changes["priority"] is null ? null : Integer(changes["priority"]));
            case "delete_source_group": return setup.Store.DeleteGroup(Integer(args[0]));
            case "list_sources": return Array(setup.Store.ListSources());
            case "list_source_groups": return Array(setup.Store.ListGroups());
            case "capture":
                var captured = setup.Store.CapturePreferences(); snapshots[Text(operation["key"], "snapshot")] = (JsonObject)captured.DeepClone(); return captured;
            case "restore": return setup.Store.RestorePreferences(snapshots[Text(operation["key"], "snapshot")]);
            case "sql":
                setup.Database.Write(DatabaseFile.Catalog, session =>
                {
                    foreach (var statement in operation["statements"]!.AsArray())
                    {
                        var sequence = statement!.AsArray(); var index = 0; var sql = System.Text.RegularExpressions.Regex.Replace(Text(sequence[0]), @"\?", _ => "$p" + index++);
                        var parameters = sequence[1]!.AsArray().Select((value, position) => (Name: "$p" + position, Value: Value(value))).ToArray(); session.Execute(sql, parameters);
                    }
                    return 0;
                }); return null;
            default: throw new InvalidDataException("Unknown source configuration oracle action " + action);
        }
    }

    private static void MatchingOracle()
    {
        var count = 0;
        foreach (var row in Fixture()["pure"]!.AsArray().OfType<JsonObject>())
        {
            JsonNode? actual;
            switch (Text(row["kind"]))
            {
                case "matching-policy":
                    if (!row["valid"]!.GetValue<bool>()) { Check.Throws<ArgumentException>(() => MatchingRuleConfiguration.Validate(row["value"])); count++; continue; }
                    actual = MatchingRuleConfiguration.Validate(row["value"]); break;
                case "matching-revision": actual = JsonValue.Create(MatchingRuleConfiguration.Revision(Rules(row["rules"]!.AsArray()))); break;
                case "matching-effective":
                    var sources = row["sources"]!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value!.AsObject(), StringComparer.Ordinal);
                    var result = MatchingRuleConfiguration.Effective(Rules(row["rules"]!.AsArray()), sources, Text(row["source_id"]), Text(row["direction"]));
                    actual = new JsonObject { ["rule"] = result.Rule, ["origin"] = result.Origin }; break;
                default: throw new InvalidDataException("Unported adjacent operation entered source configuration fixture");
            }
            Equal(row["result"], actual); count++;
        }
        Check.Equal(38, count);
    }

    private static void SnapshotAdmission()
    {
        using var setup = new Setup(); var input = SourceRow(Source, "Original"); var snapshot = CompletedSourceSnapshot.FromCompleteNativeList([input]); input["name"] = "Changed later";
        setup.Store.PersistSnapshot(snapshot); Check.Equal("Original", Text(setup.Store.GetSource(Source)["name"])); var before = State(setup.Database); setup.Trace.Clear();
        setup.Store.PersistSnapshot(CompletedSourceSnapshot.FromCompleteNativeList([])); Equal(before, State(setup.Database)); Check.Equal(0, setup.Trace.Count);
        foreach (var rows in new IReadOnlyList<JsonObject>[] { [SourceRow(), SourceRow()], [new()], [new JsonObject { ["id"] = Source, ["lang"] = 7 }], [new JsonObject { ["id"] = Source, ["supportsLatest"] = "true" }] })
            Check.Throws<ArgumentException>(() => setup.Store.PersistSnapshot(CompletedSourceSnapshot.FromCompleteNativeList(rows)));
        Equal(before, State(setup.Database)); Check.Equal(0, setup.Trace.Count);
        Check.True(setup.Store.GetSource(Source)["available"]!.GetValue<bool>());
    }

    private static void SnapshotRollback()
    {
        using var setup = new Setup(); setup.Seed(); setup.Store.UpdateSource(Source, new JsonObject { ["enabled_search"] = false });
        setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("UPDATE source_config SET group_id=999 WHERE source_id=$id", ("$id", Source)));
        var before = State(setup.Database); setup.Trace.Clear();
        Check.Throws<SqliteException>(() => setup.Store.PersistSnapshot(CompletedSourceSnapshot.FromCompleteNativeList([SourceRow(Source, "Changed")])));
        Equal(before, State(setup.Database)); Check.Equal(0, setup.Trace.Count);
    }

    private static void RestoreRollback()
    {
        using var setup = new Setup(); setup.Seed(); setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("INSERT INTO gallery_policies(source_id,revision,policy_json,updated_at) VALUES($id,4,'{\"retained\":true}','earlier')", ("$id", Source)));
        var snapshot = setup.Store.CapturePreferences(); snapshot["groups"] = new JsonArray(new JsonObject { ["id"] = 7, ["name"] = "New group", ["priority"] = 21 });
        snapshot["sources"]![0]!["enabled_search"] = 0; snapshot["sources"]![0]!["group_id"] = 7;
        snapshot["matching_policies"] = new JsonArray(new JsonObject { ["scope"] = "source", ["subject"] = Source, ["direction"] = "external", ["rule_json"] = "{\"enabled\":true,\"checks\":{\"title\":0.1}}" });
        var before = State(setup.Database); setup.Trace.Clear(); Check.Throws<ArgumentException>(() => setup.Store.RestorePreferences(snapshot));
        Equal(before, State(setup.Database)); Check.Equal(0, setup.Trace.Count);
        Check.Throws<ArgumentException>(() => setup.Store.RestorePreferences(new JsonObject { ["sources"] = new JsonArray(1) })); Equal(before, State(setup.Database));
    }

    private static void MatchingPersistence()
    {
        using var setup = new Setup(); setup.Seed(); var group = setup.Store.CreateGroup("Shared", 31); var id = Integer(group["id"]); setup.Store.UpdateSource(Source, new JsonObject { ["group_id"] = id });
        var first = setup.Matching.Read(); setup.Matching.Save("group", id.ToString(CultureInfo.InvariantCulture), "external", Rule(false), Text(first["revision"]));
        setup.Matching.Save("source", Source, "internal", Rule(true, .97), Text(setup.Matching.Read()["revision"]));
        var sources = setup.Store.ListSources().ToDictionary(row => Text(row["source_id"]), StringComparer.Ordinal); var rules = setup.Matching.Rules();
        Check.True(!MatchingRuleConfiguration.Effective(rules, sources, Source, "external").Rule!["enabled"]!.GetValue<bool>());
        Check.True(MatchingRuleConfiguration.Effective(rules, sources, Source, "internal").Rule!["enabled"]!.GetValue<bool>());
        var revision = Text(setup.Matching.Read()["revision"]); var before = State(setup.Database); Check.Throws<MatchingRuleConflictException>(() => setup.Matching.Save("source", Other, "external", Rule(false), Text(first["revision"]))); Equal(before, State(setup.Database));
        setup.Matching.Save("source", Source, "external", Rule(true, .99), revision); setup.Matching.Save("source", Source, "external", null, Text(setup.Matching.Read()["revision"]));
        setup.Store.PersistSnapshot(CompletedSourceSnapshot.FromCompleteNativeList([SourceRow()]));
        setup.Matching.Save("source", Other, "internal", Rule(false), Text(setup.Matching.Read()["revision"]));
        var saved = setup.Matching.Read(); using var reopened = new SqliteDatabase(new AutoSourcarrSettings { DataDir = setup.Path });
        Equal(saved, new MatchingRuleConfiguration(reopened, setup.Gate, _ => { }, setup.Clock).Read());
        Check.True(!setup.Store.GetSource(Other)["available"]!.GetValue<bool>());
    }

    private static void ConcurrentMatching()
    {
        using var setup = new Setup(); setup.Seed(); var revision = Text(setup.Matching.Read()["revision"]);
        bool Save(string id) { try { setup.Matching.Save("source", id, "external", Rule(false), revision); return true; } catch (MatchingRuleConflictException) { return false; } }
        var results = Task.WhenAll(Task.Run(() => Save(Source)), Task.Run(() => Save(Other))).GetAwaiter().GetResult(); Check.Equal(1, results.Count(value => value));
        Check.Equal(1, setup.Matching.Rules().Count);
    }

    private static void ConcurrentGroup()
    {
        using var setup = new Setup(); setup.Seed(); var group = Integer(setup.Store.CreateGroup("Shared", 77)["id"]);
        setup.Store.UpdateSource(Source, new JsonObject { ["group_id"] = group }); setup.Store.UpdateSource(Other, new JsonObject { ["group_id"] = group });
        Task.WhenAll(Task.Run(() => setup.Store.UpdateSource(Source, new JsonObject { ["priority"] = 11 })), Task.Run(() => setup.Store.UpdateGroup(group, priority: 29))).GetAwaiter().GetResult();
        var rows = new CatalogueQueries(setup.Database, setup.Gate, _ => false).Sources(); Check.Equal(1, rows.Select(row => Integer(row["priority"])).Distinct().Count());
        Check.True(rows.All(row => Integer(row["priority"]) == Integer(row["group_priority"])));
    }

    private static void OldRead()
    {
        using var setup = new Setup(); setup.Seed(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var old = Task.Run(() => setup.Database.Read(DatabaseFile.Catalog, session => session.Savepoint(reader =>
        {
            var value = reader.Query("SELECT enabled_search FROM source_config WHERE source_id=$id", ("$id", Source))[0].Flag("enabled_search");
            entered.Set(); Check.True(release.Wait(TimeSpan.FromSeconds(5))); return value;
        })));
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(3))); setup.Store.UpdateSource(Source, new JsonObject { ["enabled_search"] = false });
            Check.True(!setup.Store.GetSource(Source)["enabled_search"]!.GetValue<bool>()); release.Set(); Check.True(old.GetAwaiter().GetResult());
            var current = new CatalogueQueries(setup.Database, setup.Gate, _ => false).Sources().Single(row => Text(row["source_id"]) == Source);
            Check.True(!current["enabled_search"]!.GetValue<bool>());
        }
        finally { release.Set(); old.GetAwaiter().GetResult(); }
    }

    private static void Effects()
    {
        using var setup = new Setup(); Check.Throws<ArgumentNullException>(() => new SourceConfigurationStore(setup.Database, setup.Gate, () => new HashSet<string>(), null!));
        Check.Throws<ArgumentNullException>(() => new MatchingRuleConfiguration(setup.Database, setup.Gate, null!)); setup.Seed(); setup.Trace.Clear();
        var result = setup.Store.UpdateSource(Source, new JsonObject { ["enabled_search"] = false }); Check.Equal(1, setup.Trace.Count); Check.True(setup.Trace[0]["hard"]!.GetValue<bool>());
        Check.True(!result.ContainsKey("queued") && !result.ContainsKey("task_id"));
        var committed = false; var store = new SourceConfigurationStore(setup.Database, setup.Gate, () => new HashSet<string>(), _ =>
        { committed = !setup.Database.Read(DatabaseFile.Catalog, session => session.Query("SELECT enabled_search FROM source_config WHERE source_id=$id", ("$id", Source))[0].Flag("enabled_search")); }, setup.Clock);
        store.UpdateSource(Source, new JsonObject { ["enabled_search"] = false }); Check.True(committed);
    }

    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _temp = new(); public string Path => _temp.Path; public object Gate { get; } = new(); public FixedClock Clock { get; } = new();
        public SqliteDatabase Database { get; } public SourceConfigurationStore Store { get; } public MatchingRuleConfiguration Matching { get; } public List<JsonObject> Trace { get; } = [];
        public Setup(IReadOnlySet<string>? allowed = null)
        {
            Database = new(new AutoSourcarrSettings { DataDir = _temp.Path }); var schema = new SchemaMigrator(Database); schema.Initialize(); schema.EnsureFeatureSchema("gallery-policy.sql");
            void Effect(SourceConfigurationEffect effect) { lock (Trace) Trace.Add(effect.ToJson()); }
            Store = new(Database, Gate, () => allowed ?? new HashSet<string>(["en"], StringComparer.Ordinal), Effect, Clock); Matching = new(Database, Gate, Effect, Clock);
        }
        public void Seed() => Store.PersistSnapshot(CompletedSourceSnapshot.FromCompleteNativeList([SourceRow(), SourceRow(Other, "Other")]));
        public void Dispose() { Database.Dispose(); _temp.Dispose(); }
    }
    private sealed class FixedClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-20T00:00:00+00:00", CultureInfo.InvariantCulture); }
    private static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("source-configuration.json")))!.AsObject();
    private static JsonObject SourceRow(string id = Source, string name = "Family") => new() { ["id"] = id, ["name"] = name, ["lang"] = "en", ["homeUrl"] = "https://family.invalid" };
    private static JsonObject Rule(bool enabled, double title = .94) => new() { ["enabled"] = enabled, ["checks"] = enabled ? new JsonObject { ["title"] = title } : new JsonObject() };
    private static IReadOnlyDictionary<MatchingRuleKey, JsonObject?> Rules(JsonArray rows) => rows.OfType<JsonObject>().ToDictionary(row => new MatchingRuleKey(Text(row["scope"]), Text(row["subject"]), Text(row["direction"])), row => row["rule"] as JsonObject);
    private static JsonObject State(SqliteDatabase database) => database.Read(DatabaseFile.Catalog, session =>
    {
        var result = new JsonObject(); foreach (var table in new[] { "source_config", "source_groups", "matching_policies", "gallery_policies" }) result[table] = Array(session.Query("SELECT * FROM " + table + " ORDER BY rowid").Select(row => JsonSerializer.SerializeToNode(row.ToDictionary(pair => pair.Key, pair => pair.Value))!.AsObject())); return result;
    });
    private static object? Value(JsonNode? value) => value is null ? null : value.GetValueKind() switch { JsonValueKind.String => value.GetValue<string>(), JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Number => value.AsValue().TryGetValue<long>(out var number) ? (object)number : value.GetValue<double>(), _ => throw new InvalidDataException("Oracle SQL parameter is not scalar") };
    private static string Text(JsonNode? value, string fallback = "") => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : fallback;
    private static long Integer(JsonNode? value) => value is JsonValue scalar ? scalar.TryGetValue<long>(out var number) ? number : scalar.TryGetValue<int>(out var small) ? small : 0 : 0;
    private static JsonArray Array(IEnumerable<JsonObject> values) => new(values.Select(value => (JsonNode)value).ToArray());
    private static void Equal(JsonNode? expected, JsonNode? actual) => Check.JsonEqual(JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(actual));
}
