using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Manual;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Tests.Matching;

/// <summary>Phase 8: manual matches, groups, separate records, titles and the previewed manual resets.</summary>
public static class ManualDecisionsTests
{
    private static readonly DecisionActor Owner = DecisionActor.User("owner");

    public static void Register(TestSuite suite)
    {
        suite.Add("manual decisions: a manual match binds the exact record, forms a group with the second one and is logged", () =>
        {
            using var temp = new Sandbox();
            using var database = ManualFixture.Open(temp);
            ManualFixture.Source(database, "a", "Source A"); ManualFixture.Source(database, "b", "Source B");
            var entry = ManualFixture.Entry(database, "Alpha");
            var other = ManualFixture.Entry(database, "Elsewhere");
            ManualFixture.Record(database, other, "b", 20, "Alpha (B)");
            var operations = new ManualFakeCatalogueOperations(database);
            var log = new InMemoryDecisionLog();
            var manual = new ManualDecisions(database, new object(), log, operations);

            var first = manual.ManualMatchAsync(entry, "a", 10, "replace", new JsonObject { ["id"] = 10, ["title"] = "Alpha (A)" }, true, Owner).GetAwaiter().GetResult();
            Check.True(ManualDecisions.NeedsHydration(first), "A search card commits locally and asks for hydration");
            Check.True(manual.IsManuallyBound("a", 10, out var bound) && bound == entry, "Binding remembered");
            Check.Equal("manual", database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT match_type FROM source_matches WHERE entry_id=$e AND source_id='a'", ("$e", entry)) as string));

            // The record automation attached elsewhere moves: manual intent wins over automatic ownership.
            var second = manual.ManualMatchAsync(entry, "b", 20, "replace", null, false, Owner).GetAwaiter().GetResult();
            Check.Equal(other, second["forced_from_entry_ids"]!.AsArray().Single()!.GetValue<long>());
            Check.True(operations.Calls.Contains("fetch 20"), "Without a card the details are fetched first");
            var groups = manual.ManualGroupsPayload();
            Check.Equal(1, groups["group_count"]!.GetValue<int>());
            Check.Equal(2, groups["groups"]![0]!["member_count"]!.GetValue<int>());
            Check.Equal(entry, groups["groups"]![0]!["entry_id"]!.GetValue<long>());
            Check.Equal("manual.match,manual.match", string.Join(',', log.Kinds));
            Check.Equal("owner", log.Entries[0].Actor.Name);
            Check.True(manual.HydrateManualMatchAsync(entry, "a", 10).GetAwaiter().GetResult(), "Hydration stores details of a still-current match");

            var groupId = groups["groups"]![0]!["id"]!.GetValue<string>();
            var removed = manual.RemoveManualGroupMember(groupId, "b", 20, Owner);
            Check.Equal(0, removed["group_count"]!.GetValue<int>());
            Check.Equal(2, removed["manual_match_count"]!.GetValue<int>());
            Check.Equal(1L, ManualFixture.Count(database, "SELECT COUNT(*) FROM manual_match_bindings WHERE grouping_suppressed=1"));
            Check.True(manual.IsManuallyBound("b", 20, out _), "The record stays bound outside the group");
        });

        suite.Add("manual decisions: separate entry splits the record away, marks different works and can be allowed again", () =>
        {
            using var temp = new Sandbox();
            using var database = ManualFixture.Open(temp);
            var entry = ManualFixture.Entry(database, "Alpha");
            ManualFixture.Record(database, entry, "a", 10, "Alpha");
            ManualFixture.Record(database, entry, "b", 20, "Alpha Colour");
            var manual = new ManualDecisions(database, new object(), new InMemoryDecisionLog(), new ManualFakeCatalogueOperations(database));
            var result = manual.SeparateSourceRecordAsync(entry, "b", 20, null, Owner).GetAwaiter().GetResult();
            var separate = result["separate_entry_id"]!.GetValue<long>();
            Check.True(separate > 0 && separate != entry, "A new entry holds the record");
            Check.True(manual.IsSeparated("b", 20) && manual.IsSeparatedFrom(entry, "b", 20), "Separate rule stored");
            Check.True(manual.IsRejected(separate, entry), "Different works");
            Check.Throws<ManualDecisionConflictException>(() => manual.SeparateSourceRecordAsync(entry, "a", 10, null, Owner).GetAwaiter().GetResult());
            manual.AllowSourceRecordMatch(entry, "b", 20, Owner);
            Check.True(!manual.IsSeparatedFrom(entry, "b", 20) && !manual.IsRejected(separate, entry), "Allow forgets the rule and its review");
            Check.Throws<KeyNotFoundException>(() => manual.AllowSourceRecordMatch(9999, "b", 20, Owner));
        });

        suite.Add("manual decisions: without catalogue operations only durable decisions change", () =>
        {
            using var temp = new Sandbox();
            using var database = ManualFixture.Open(temp);
            var entry = ManualFixture.Entry(database, "Alpha");
            var manual = new ManualDecisions(database, new object(), new InMemoryDecisionLog());
            Check.Throws<ManualDecisionUnavailableException>(() => manual.ManualMatchAsync(entry, "a", 10, "replace", null, false, Owner).GetAwaiter().GetResult());
            Check.Equal(0, manual.ManualActionsPayload()["counts"]!["matches"]!.GetValue<int>());
            Check.Equal(0, manual.RestorePersistentManualMatches(Owner)["restored"]!.GetValue<int>());
        });

        suite.Add("manual decisions: manual titles are listed with their scope and cleared on request", () =>
        {
            using var temp = new Sandbox();
            using var database = ManualFixture.Open(temp);
            var entry = ManualFixture.Entry(database, "Alpha Manual");
            database.Write(DatabaseFile.Catalog, c =>
            {
                c.Execute("UPDATE entries SET manual_preferred_title=1 WHERE id=$e", ("$e", entry));
                c.Execute("INSERT INTO preferred_title_overrides(entry_id,title,updated_at) VALUES($e,'Alpha Manual','2026-01-01')", ("$e", entry));
                c.Execute("INSERT INTO manual_title_scopes(entry_id,source_id,source_manga_id,title,captured_at) VALUES($e,'a',10,'Alpha Manual','2026-01-01')", ("$e", entry));
                return c.Execute("INSERT INTO titles(entry_id,title,normalized,title_type) VALUES($e,'Alpha Alias','alpha alias','manual')", ("$e", entry));
            });
            var log = new InMemoryDecisionLog();
            var operations = new ManualFakeCatalogueOperations(database);
            var manual = new ManualDecisions(database, new object(), log, operations);
            var payload = manual.ManualActionsPayload();
            Check.Equal(1, payload["counts"]!["titles"]!.GetValue<int>());
            Check.Equal(1, payload["titles"]![0]!["scope"]!.AsArray().Count);
            Check.Equal("Alpha Alias", payload["titles"]![0]!["aliases"]![0]!["title"]!.GetValue<string>());
            manual.ClearManualTitles(entry, Owner);
            Check.Equal(0L, ManualFixture.Count(database, "SELECT COUNT(*) FROM preferred_title_overrides") + ManualFixture.Count(database, "SELECT COUNT(*) FROM titles WHERE title_type='manual'"));
            Check.True(operations.Calls.Contains("sync-title " + entry) && log.Kinds.Contains("title.manual.clear"), "Title consensus asked for and logged");
        });

        suite.Add("manual decisions: resets need the current preview token and never touch other decisions", () =>
        {
            using var temp = new Sandbox();
            using var database = ManualFixture.Open(temp);
            var entry = ManualFixture.Entry(database, "Alpha");
            var other = ManualFixture.Entry(database, "Beta");
            ManualFixture.Record(database, entry, "a", 10, "Alpha", matchType: "manual", locked: true);
            ManualFixture.Record(database, entry, "b", 20, "Alpha B", matchType: "manual", locked: true);
            var log = new InMemoryDecisionLog();
            var manual = new ManualDecisions(database, new object(), log, new ManualFakeCatalogueOperations(database));
            Check.Equal(2, manual.CaptureExistingManualMatches());
            Check.Equal(0, manual.CaptureExistingManualMatches());
            Check.Equal(1, manual.ManualGroupsPayload()["group_count"]!.GetValue<int>());
            database.Write(DatabaseFile.Catalog, c => c.Execute("INSERT INTO duplicate_reviews(left_entry_id,right_entry_id,decision,reviewed_at) VALUES($l,$r,'not_duplicate','2026-01-01')", ("$l", entry), ("$r", other)));

            var groups = manual.PreviewReset(ManualResetScope.Groups);
            var token = groups["preview_token"]!.GetValue<string>();
            Check.Equal(64, token.Length);
            Check.Throws<StaleManualPreviewException>(() => manual.Reset(ManualResetScope.Groups, new string('0', 64), Owner));
            Check.Equal(1L, manual.Reset(ManualResetScope.Groups, token, Owner)["groups_removed"]!.GetValue<long>());
            Check.Equal(2L, ManualFixture.Count(database, "SELECT COUNT(*) FROM manual_match_bindings"));
            Check.Throws<StaleManualPreviewException>(() => manual.Reset(ManualResetScope.Groups, token, Owner));

            var matches = manual.PreviewReset(ManualResetScope.Matches);
            // A decision made after the preview makes it stale.
            manual.PersistManualBinding(other, "c", new JsonObject { ["id"] = 30, ["title"] = "Beta C" }, Owner);
            Check.Throws<StaleManualPreviewException>(() => manual.Reset(ManualResetScope.Matches, matches["preview_token"]!.GetValue<string>(), Owner));
            var fresh = manual.PreviewReset(ManualResetScope.Matches);
            Check.Equal(3L, manual.Reset(ManualResetScope.Matches, fresh["preview_token"]!.GetValue<string>(), Owner)["manual_matches_removed"]!.GetValue<long>());
            Check.Equal(0L, ManualFixture.Count(database, "SELECT COUNT(*) FROM source_matches WHERE locked=1 OR match_type='manual'"));
            Check.True(manual.IsRejected(entry, other), "A manual-match reset keeps different-work reviews");

            var all = manual.PreviewReset(ManualResetScope.All);
            Check.True(all["counts"]!.AsArray().Any(row => row!["key"]!.GetValue<string>() == "duplicate_reviews" && row["count"]!.GetValue<int>() == 1), "Preview lists the review");
            manual.Reset(ManualResetScope.All, all["preview_token"]!.GetValue<string>(), Owner);
            Check.True(!manual.IsRejected(entry, other), "Reset all forgets every decision");
            Check.Equal("reset.manual.groups,reset.manual.matches,reset.manual", string.Join(',', log.Kinds.Where(kind => kind.StartsWith("reset.", StringComparison.Ordinal))));
        });
    }
}
