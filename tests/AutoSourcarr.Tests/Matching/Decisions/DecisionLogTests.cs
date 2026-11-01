using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Tests.Matching;

/// <summary>Phase 8: the single decision log with actor (database table and the in-memory log for tests).</summary>
public static class DecisionLogTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("matching decisions: the decision log table comes from its own migration", () =>
        {
            using var temp = new Sandbox();
            using var database = ManualFixture.Open(temp);
            Check.True(ManualFixture.Count(database, "SELECT COUNT(*) FROM VersionInfo WHERE Version=2026110180") == 1, "Decision log migration applied");
            Check.Equal(2026110180L, SchemaMigrator.LatestMigration(DatabaseFile.Catalog) >= 2026110180L ? 2026110180L : 0L);
            Check.True(ManualFixture.Count(database, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='decision_log'") == 1);
        });

        suite.Add("matching decisions: the database log stores actor, entries, records and state, filters and pages", () =>
        {
            using var temp = new Sandbox();
            using var database = ManualFixture.Open(temp);
            Exercise(new DecisionLog(database));
        });

        suite.Add("matching decisions: the in-memory log behaves like the database log", () => Exercise(new InMemoryDecisionLog()));

        suite.Add("matching decisions: a decision joins the caller's write and rolls back with it", () =>
        {
            using var temp = new Sandbox();
            using var database = ManualFixture.Open(temp);
            var log = new DecisionLog(database);
            Check.Throws<InvalidOperationException>(() => database.Write<int>(DatabaseFile.Catalog, c =>
            {
                log.Record(new DecisionEvent("merge.manual", DecisionActor.User("owner"), [1, 2], [], "inside"));
                throw new InvalidOperationException("rolled back");
            }));
            Check.Equal(0L, ManualFixture.Count(database, "SELECT COUNT(*) FROM decision_log"));
        });
    }

    private static void Exercise<T>(T log) where T : IDecisionLog, IDecisionLogReader
    {
        var first = log.Record(new DecisionEvent("merge.automatic", DecisionActor.Automatic, [1, 2], ["src:10"], "same work",
            new JsonObject { ["entries"] = 2 }, new JsonObject { ["entry_id"] = 1 }));
        var second = log.Record(new DecisionEvent("manual.match", DecisionActor.User("owner"), [3], ["src:11"], "chosen"));
        var third = log.Record(new DecisionEvent("merge.manual", DecisionActor.Key("script"), [1], [], "undo", UndoOf: "7"));
        Check.True(first < second && second < third, "Ids increase");
        var all = log.Read(new DecisionLogQuery());
        Check.Equal(3, all.Items.Count);
        Check.Equal(third, all.Items[0].Id);
        Check.Equal(false, all.HasMore);
        Check.Equal(2, log.Read(new DecisionLogQuery(Kind: "merge")).Items.Count);
        Check.Equal(0, log.Read(new DecisionLogQuery(Kind: "mer")).Items.Count);
        Check.Equal(1, log.Read(new DecisionLogQuery(Kind: "merge.automatic")).Items.Count);
        Check.Equal(2, log.Read(new DecisionLogQuery(EntryId: 1)).Items.Count);
        Check.Equal(second, log.Read(new DecisionLogQuery(ActorKind: "user", ActorName: "owner")).Items.Single().Id);
        Check.Equal(first, log.Read(new DecisionLogQuery(Record: "src:10")).Items.Single().Id);
        var page = log.Read(new DecisionLogQuery(Limit: 2));
        Check.Equal(2, page.Items.Count);
        Check.True(page.HasMore && page.NextBefore == second, "Next page starts before the last shown item");
        Check.Equal(first, log.Read(new DecisionLogQuery(BeforeId: page.NextBefore, Limit: 2)).Items.Single().Id);
        var stored = log.Get(first)!;
        Check.Equal("automatic", stored.Actor.Kind);
        Check.Equal(2, stored.Before!["entries"]!.GetValue<int>());
        Check.Equal("7", log.Get(third)!.UndoOf);
        var json = stored.ToJson();
        Check.Equal("AutoSourcarr", json["actor"]!["name"]!.GetValue<string>());
        Check.Equal(2, json["entries"]!.AsArray().Count);
        Check.True(log.Get(999) is null);
    }
}
