using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Duplicates;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Tests.Matching;

public static class DuplicateTests
{
    private static readonly DecisionActor Person = DecisionActor.User("tester");
    private static long N(JsonNode? node) => long.Parse((node ?? throw new InvalidOperationException("missing number")).ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture);

    public static void Register(TestSuite suite)
    {
        suite.Add("duplicates: synthetic duplicate test set scores no wrong merges", () =>
        {
            var result = DuplicateTestSet.Evaluate(DuplicateTestSet.SyntheticPath);
            Console.WriteLine(DuplicateTestSet.Report(result));
            Check.True(result.Pairs >= 60, "The synthetic set keeps at least 60 pairs");
            Check.True(result.WrongMerges.Count == 0, "Wrong merges: " + string.Join("; ", result.WrongMerges));
            Check.True(result.Recall >= 0.5, "Recall on same-work pairs fell to " + result.Recall.ToString("P1", CultureInfo.InvariantCulture));
            // The same pairs through match-on-ingest (side a, then side b): a different work never attaches.
            Check.True(result.Ingest is { } ingest && ingest.Pairs == result.Pairs, "the ingest path is evaluated over the same pairs");
            Check.True(result.Ingest!.WrongMerges.Count == 0, "Ingest wrong merges: " + string.Join("; ", result.Ingest!.WrongMerges));
        });
        var privateSet = Environment.GetEnvironmentVariable(DuplicateTestSet.EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(privateSet) && File.Exists(privateSet))
            suite.Add("duplicates: private duplicate test set scores no wrong merges", () =>
            {
                var result = DuplicateTestSet.Evaluate(privateSet);
                Console.WriteLine(DuplicateTestSet.Report(result));
                Check.True(result.WrongMerges.Count == 0, "Wrong merges: " + result.WrongMerges.Count.ToString(CultureInfo.InvariantCulture));
            });

        suite.Add("duplicates: stand-in gate blocks editions, adaptations, sequels and other creators", () =>
        {
            var gate = new TextAutomaticMatchGate();
            static JsonObject S(string[] titles, string author = "Same Author", long? year = null, string color = "unknown", string media = "unknown")
            {
                var value = new JsonObject
                {
                    ["titles"] = new JsonArray(titles.Select(title => (JsonNode?)title).ToArray()), ["author"] = author, ["color_mode"] = color, ["media_mode"] = media,
                    ["normalized_titles"] = new JsonArray(titles.Select(title => (JsonNode?)AutoSourcarr.Core.Matching.TextMatcher.NormalizeTitle(title)).ToArray()),
                };
                if (year is { } y) value["year"] = y;
                return value;
            }
            Check.True(gate.Decide(S(["The Long Journey Home", "Nagai Tabiji no Kaeri"]), S(["Nagai Tabiji no Kaeri", "The Long Journey Home"])).Eligible, "two exact distinctive titles merge");
            Check.True(gate.Decide(S(["The Long Journey Home"]), S(["The Long Journey Home!"])).Eligible, "punctuation variant with the same creator merges");
            Check.True(!gate.Decide(S(["The Long Journey Home"]), S(["The Long Journey Home"], "Someone Else Entirely")).Eligible, "different creators never merge");
            Check.True(!gate.Decide(S(["The Long Journey Home"], year: 2001), S(["The Long Journey Home"], year: 2012)).Eligible, "different years never merge");
            Check.True(!gate.Decide(S(["The Long Journey Home"]), S(["The Long Journey Home (Colored)"])).Eligible, "colored edition never merges with the plain edition");
            Check.True(!gate.Decide(S(["The Long Journey Home"], color: "colored"), S(["The Long Journey Home"], color: "monochrome")).Eligible, "colored/monochrome edition modes never merge");
            Check.True(!gate.Decide(S(["The Long Journey Home"]), S(["The Long Journey Home (Light Novel)"])).Eligible, "novel adaptation never merges");
            Check.True(!gate.Decide(S(["The Long Journey Home 2"]), S(["The Long Journey Home 3"])).Eligible, "sequel numbers never merge");
            Check.True(!gate.Decide(S(["First Love"]), S(["First Love"])).Eligible, "generic exact titles are not proof");
            var trusted = S(["Generic"]); trusted["identity_details"] = new JsonArray(new JsonObject { ["provider"] = "anilist", ["external_id"] = "7", ["match_type"] = "manual", ["locked"] = true });
            var other = S(["Other Name"], "Different Person"); other["identity_details"] = trusted["identity_details"]!.DeepClone();
            Check.True(gate.Decide(trusted, other).Eligible, "a locked/manual shared identity is trusted proof");
            Check.True(!gate.Decide(S(["The Long Journey Home"], "A, B"), S(["The Long Journey Home"], "A, C")).Eligible, "two adaptations with different artists never merge");
            // Exact titles without creators on one side prove a merge only when one of them is specific (4+ words / 24+ chars).
            Check.True(!gate.Decide(S(["Blue Flag", "Ao no Flag"], ""), S(["Blue Flag", "Ao no Flag"])).Eligible, "short exact titles with a creator missing are not proof");
            Check.True(gate.Decide(S(["The Long Journey Home", "Nagai Tabiji no Kaeri"], ""), S(["Nagai Tabiji no Kaeri", "The Long Journey Home"])).Eligible, "specific exact titles still merge");
            var scored = new AutoSourcarr.Core.Matching.Scoring.ScoredAutomaticMatchGate(gate);
            Check.True(!scored.Decide(S(["The Long Journey Home", "Nagai Tabiji no Kaeri"], "Writer Lee"), S(["The Long Journey Home", "Nagai Tabiji no Kaeri"], "Writer Park")).Eligible,
                "the production gate refuses two different writers sharing name parts");
        });

        suite.Add("duplicates: automatic consolidation is non-transitive and logged as automatic", () =>
        {
            using var f = new DuplicateFixture();
            var a = f.Entry(["The Crimson Lantern Society", "Akai Chouchin no Kai"], ["Mori Takeshi"], 2010);
            var b = f.Entry(["The Crimson Lantern Society", "Akai Chouchin no Kai", "Lanterns of the Crimson Street", "Beniiro Machi no Toumyou"], ["Mori Takeshi"], 2010);
            var c = f.Entry(["Lanterns of the Crimson Street", "Beniiro Machi no Toumyou"], ["Mori Takeshi"], 2010);
            var lone = f.Entry(["A Completely Unrelated Story Here"], ["Someone Else"], 1999);
            f.Duplicates.RepairCatalogDuplicates(DecisionActor.Automatic);
            Check.Equal(f.Canonical(a), f.Canonical(b));
            Check.True(f.Canonical(c) != f.Canonical(a), "A-B and B-C never imply A-C");
            Check.Equal(lone, f.Canonical(lone));
            Check.Equal(1L, f.Scalar("SELECT COUNT(*) FROM automatic_merge_events"));
            Check.Equal(2L, f.Scalar("SELECT COUNT(*) FROM automatic_merge_lineage"));
            Check.True(f.Log.Events.Any(e => e.Kind == "merge.automatic" && e.Actor == DecisionActor.Automatic), "automatic merge recorded with the automatic actor");
            var history = f.Duplicates.History();
            Check.Equal(1L, N(history["total"]));
            var detail = f.Duplicates.HistoryDetail(N(history["items"]![0]!["id"]));
            Check.Equal(2, detail["before"]!.AsArray().Count);
            Check.Throws<KeyNotFoundException>(() => f.Duplicates.HistoryDetail(424242));
        });

        suite.Add("duplicates: reviews win over automation and are logged with the person", () =>
        {
            using var f = new DuplicateFixture();
            var a = f.Entry(["The Silver Archive of Dawn", "Gin no Shoko"], ["Hara Yuu"], 2015);
            var b = f.Entry(["The Silver Archive of Dawn", "Gin no Shoko"], ["Hara Yuu"], 2015);
            var review = f.Duplicates.Review(a, b, "not_duplicate", Person);
            Check.Equal("not_duplicate", review["decision"]!.GetValue<string>());
            f.Duplicates.RepairCatalogDuplicates(DecisionActor.Automatic);
            Check.True(f.Canonical(a) != f.Canonical(b), "a not-duplicate review is never merged automatically");
            Check.True(f.Log.Events.Any(e => e.Kind == "review.duplicate" && e.Actor == Person), "review recorded with the person");
            f.Duplicates.Review(a, b, "clear", Person);
            Check.Equal(0L, f.Scalar("SELECT COUNT(*) FROM duplicate_reviews"));
            var merged = f.Duplicates.Review(a, b, "duplicate", Person);
            Check.True(merged["merged"]!.GetValue<bool>(), "a duplicate review merges");
            Check.Equal(f.Canonical(a), f.Canonical(b));
            Check.Equal(2L, f.Scalar("SELECT COUNT(*) FROM manual_match_bindings"));
            Check.Equal(2L, f.Scalar("SELECT COUNT(*) FROM source_matches WHERE locked=1 AND match_type='manual'") + f.Scalar("SELECT COUNT(*) FROM source_variants WHERE locked=1 AND match_type='manual'"));
            Check.True(f.Log.Events.Any(e => e.Kind == "merge.manual" && e.Actor == Person && e.Records.Count == 2), "manual merge recorded with both records");
        });

        suite.Add("duplicates: a workbench merge is a reviewed duplicate: records locked, one manual group, one transaction", () =>
        {
            using var f = new DuplicateFixture();
            var a = f.Entry(["The Paper Moon Courier"], ["Sato Ken"], 2012);
            var b = f.Entry(["Paper Moon Courier"], ["Sato Ken"], 2012);
            var merged = f.Duplicates.MergeReviewed(a, b, Person, "workbench", "manual-review-workbench");
            Check.Equal(f.Canonical(a), f.Canonical(b));
            Check.True(merged["manual_group_id"] is JsonValue, "the reviewed records form one manual group");
            Check.Equal(2L, f.Scalar("SELECT COUNT(*) FROM manual_match_bindings WHERE group_id IS NOT NULL"));
            Check.Equal(2L, f.Scalar("SELECT COUNT(*) FROM source_matches WHERE locked=1 AND match_type='manual' AND active=1") + f.Scalar("SELECT COUNT(*) FROM source_variants WHERE locked=1 AND match_type='manual' AND active=1"));
            Check.Equal(1L, f.Scalar("SELECT COUNT(*) FROM duplicate_review_history WHERE decision='duplicate'"));
            Check.True(f.Log.Events.Count(e => e.Kind == "merge.manual" && e.Actor == Person) == 1, "one manual merge decision with the person");
            Check.Throws<InvalidOperationException>(() => f.Duplicates.MergeReviewed(a, b, Person, "again"));
        });

        suite.Add("duplicates: a merge and its rollback event, lineage and decision commit together or not at all", () =>
        {
            using var f = new DuplicateFixture();
            var a = f.Entry(["The Crimson Lantern Society", "Akai Chouchin no Kai"], ["Mori Takeshi"], 2010);
            var b = f.Entry(["The Crimson Lantern Society", "Akai Chouchin no Kai"], ["Mori Takeshi"], 2010);
            var failing = new DuplicateMatching(f.Database, f.Gate, f.Structure, f.Evidence.Snapshot, new FailingDecisionLog(), () => f.Settings);
            var decision = new JsonObject { ["eligible"] = true, ["reason"] = "2 exact native titles" };
            Check.Throws<InvalidOperationException>(() => failing.MergeAutomatic(a, b, decision, DecisionActor.Automatic));
            Check.True(f.Canonical(a) != f.Canonical(b), "the merge rolled back with its decision");
            Check.Equal(0L, f.Scalar("SELECT COUNT(*) FROM automatic_merge_events"));
            Check.Equal(0L, f.Scalar("SELECT COUNT(*) FROM automatic_merge_lineage"));
            Check.Equal(0L, f.Scalar("SELECT COUNT(*) FROM entry_aliases"));
            Check.Equal(0L, f.Scalar("SELECT COUNT(*) FROM pending_entry_merges"));
            Check.Throws<InvalidOperationException>(() => failing.Review(a, b, "duplicate", Person));
            Check.True(f.Canonical(a) != f.Canonical(b), "a reviewed merge rolls back with its group and decision");
            Check.Equal(0L, f.Scalar("SELECT COUNT(*) FROM manual_match_bindings"));
            Check.Equal(0L, f.Scalar("SELECT COUNT(*) FROM duplicate_review_history"));
            var target = f.Duplicates.MergeAutomatic(a, b, decision, DecisionActor.Automatic);
            Check.Equal(target, f.Canonical(a));
            Check.Equal(1L, f.Scalar("SELECT COUNT(*) FROM automatic_merge_events"));
        });

        suite.Add("duplicates: automatic merges never drop manual titles; manual merges keep them on the survivor", () =>
        {
            using var f = new DuplicateFixture();
            var curated = f.Entry(["The Hollow Bell Garden", "Utsuro Kane no Niwa"], ["Abe Rin"], 2014);
            var reliable = f.Entry(["The Hollow Bell Garden", "Utsuro Kane no Niwa"], ["Abe Rin"], 2014, locked: true);
            f.Database.Write(DatabaseFile.Catalog, c =>
            {
                c.Execute("INSERT INTO titles(entry_id,title,normalized,title_type) VALUES($p0,'Hollow Bell','hollow bell','manual')", ("$p0", curated));
                return c.Execute("INSERT INTO preferred_title_overrides(entry_id,title,updated_at) VALUES($p0,'Hollow Bell','2026-01-01')", ("$p0", curated));
            });
            var evidence = f.Duplicates.GroupEvidence(curated, reliable);
            Check.True(!evidence["eligible"]!.GetValue<bool>() && evidence["reason"]!.GetValue<string>() == "manual-titles", evidence.ToJsonString());
            Check.Throws<InvalidOperationException>(() => f.Duplicates.MergeAutomatic(curated, reliable, new JsonObject { ["reason"] = "test" }, DecisionActor.Automatic));
            Check.True(f.Canonical(curated) != f.Canonical(reliable), "nothing was merged");
            f.Duplicates.Review(curated, reliable, "duplicate", Person);
            Check.Equal(reliable, f.Canonical(curated));
            Check.Equal(1L, f.Scalar("SELECT COUNT(*) FROM titles WHERE title_type='manual' AND entry_id=" + reliable.ToString(CultureInfo.InvariantCulture)));
            Check.Equal(1L, f.Scalar("SELECT COUNT(*) FROM preferred_title_overrides WHERE entry_id=" + reliable.ToString(CultureInfo.InvariantCulture)));
        });

        suite.Add("duplicates: unmatch, unbind and redo split records without touching manual ones", () =>
        {
            using var f = new DuplicateFixture();
            var entry = f.Entry(["The Glass Garden Chronicle"], ["Aoi Sora"], 2018);
            var wrong = f.Attach(entry, "Swordmaster of the Endless Sky");
            var kept = f.Attach(entry, "The Glass Garden Chronicle", "manual", locked: true);
            var split = f.Duplicates.UnmatchRecords(entry, [wrong], Person);
            var created = N(split["new_entry_id"]);
            Check.True(created != entry, "unmatch creates a separate entry");
            Check.Equal("not_duplicate", f.Database.Read(DatabaseFile.Catalog, c => DuplicateEvidence.Review(c, entry, created)));
            Check.Equal(1L, f.Scalar("SELECT COUNT(*) FROM manual_separate_records"));
            Check.True(f.Log.Events.Any(e => e.Kind == "split" && e.Actor == Person), "split recorded with the person");

            var other = f.Entry(["The Bronze Harbor Tales"], ["Kai Ren"], 2019);
            var automatic = f.Attach(other, "Totally Different Mecha Story");
            f.Attach(other, "The Bronze Harbor Tales", "manual", locked: true);
            var unbound = f.Duplicates.UnbindAutomaticRecords(other, Person);
            Check.Equal(1L, N(unbound["split_records"]));
            var owner = f.Database.Read(DatabaseFile.Catalog, c => c.Query("SELECT entry_id FROM source_matches WHERE source_id=$p0 AND active=1 UNION ALL SELECT entry_id FROM source_variants WHERE source_id=$p0 AND active=1", ("$p0", automatic.SourceId)).Single().Integer("entry_id"));
            Check.True(owner != other, "the automatic record left the entry");
            Check.Equal(2L, f.Database.Read(DatabaseFile.Catalog, c => Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM (SELECT 1 FROM source_matches WHERE entry_id=$p0 AND active=1 UNION ALL SELECT 1 FROM source_variants WHERE entry_id=$p0 AND active=1)", ("$p0", other)), CultureInfo.InvariantCulture)));

            var third = f.Entry(["The Iron Lotus Saga"], ["Mei Lan"], 2020, locked: true);
            f.Attach(third, "An Entirely Unrelated Cooking Manga");
            var manual = f.Attach(third, "The Iron Lotus Saga", "manual", locked: true);
            var redo = f.Duplicates.PrepareRedo(third, Person);
            Check.Equal(1L, N(redo["split_records"]));
            Check.True(N(redo["preserved_records"]) >= 2, "anchor and manual record stay");
            Check.Equal(third, f.Database.Read(DatabaseFile.Catalog, c => DuplicateEvidence.Canonical(c, c.Query("SELECT entry_id FROM source_matches WHERE source_id=$p0 AND active=1 UNION ALL SELECT entry_id FROM source_variants WHERE source_id=$p0 AND active=1", ("$p0", manual.SourceId)).Single().Integer("entry_id"))));
            Check.True(f.Log.Events.Any(e => e.Kind == "split.redo"), "redo recorded");
            _ = kept;
        });

        suite.Add("duplicates: review snapshot feeds candidates, pruning and status counts", () =>
        {
            using var f = new DuplicateFixture();
            var a = f.Entry(["The Midnight Cartographer"], ["Ito Ren"], 2016);
            var b = f.Entry(["The Midnight Cartographer"], ["Someone Different"], 2016);
            Check.True(f.Duplicates.GroupsCached() is null, "no snapshot before the first scan");
            var groups = f.Duplicates.Groups(force: true);
            Check.Equal(1, groups.Count);
            var pair = groups[0]!["pairs"]![0]!;
            Check.True(!pair["auto_merge_eligible"]!.GetValue<bool>(), "different creators are shown for review, not merged");
            var summary = f.Duplicates.SnapshotSummary();
            Check.True(summary["ready"]!.GetValue<bool>(), "snapshot ready");
            Check.Equal(2L, N(summary["entries"]));
            var cached = f.Duplicates.CandidatesCached([a])!;
            Check.True(cached[a.ToString(CultureInfo.InvariantCulture)] is JsonObject, "cached candidates for the entry");
            var stats = new CatalogueStatusStats(f.Database, () => f.Settings, duplicates: f.Duplicates.SnapshotSummary).Build();
            Check.Equal(2L, N(stats["catalog"]!["possible_duplicates"]));
            Check.Equal(1L, N(stats["catalog"]!["duplicate_groups"]));
            var reviewed = f.Duplicates.Review(a, b, "not_duplicate", Person);
            Check.Equal(0, reviewed["groups"]!.AsArray().Count);
            var filtered = DuplicateMatching.Filter(groups, _ => false);
            Check.Equal(0, filtered.Count);
            Check.Equal(0L, N(f.Duplicates.AutoMergePreview()["pair_count"]));
        });

        suite.Add("duplicates: automatic merge reasons exclude manual, import and split merges", () =>
        {
            Check.True(DuplicateMatching.AutomaticMergeReason("merge:automatic-safe:2 exact native titles"), "automatic-safe");
            Check.True(DuplicateMatching.AutomaticMergeReason("merge:automatic-consolidation"), "consolidation");
            Check.True(!DuplicateMatching.AutomaticMergeReason("merge:manual-duplicate-review"), "manual");
            Check.True(!DuplicateMatching.AutomaticMergeReason("merge:import-source-bind"), "import");
            Check.True(!DuplicateMatching.AutomaticMergeReason(""), "empty");
        });

        suite.Add("duplicates: pausing automatic matching defers sweeps until every reason ends", () =>
        {
            using var f = new DuplicateFixture();
            using (f.Duplicates.PauseAutomatic("rebuild"))
            {
                using (f.Duplicates.PauseAutomatic("rebuild")) Check.True(f.Duplicates.Paused, "paused");
                Check.True(f.Duplicates.Paused, "still paused by the outer scope");
                var deferred = f.Duplicates.RunAutomaticAsync().GetAwaiter().GetResult();
                Check.True(deferred["deferred"]!.GetValue<bool>(), "sweep deferred while paused");
            }
            Check.True(!f.Duplicates.Paused, "resumed");
            Check.Equal(0, f.Duplicates.PauseReasons().Count);
        });
    }

    private sealed class FailingDecisionLog : IDecisionLog
    {
        public long Record(DecisionEvent decision) => throw new InvalidOperationException("decision log unavailable");
    }
}
