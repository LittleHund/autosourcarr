using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Maintenance;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Manual;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources.Library;

namespace AutoSourcarr.Tests.Matching;

/// <summary>Phase 8: entry maintenance (delete, detach, promote, clear) and the startup repairs.</summary>
public static class CatalogueMaintenanceTests
{
    private static readonly DecisionActor Owner = DecisionActor.User("owner");

    public static void Register(TestSuite suite)
    {
        suite.Add("catalogue maintenance: deleting an entry tombstones its records and forgets their bindings", () =>
        {
            using var temp = new Sandbox();
            using var database = ManualFixture.Open(temp);
            var entry = ManualFixture.Entry(database, "Alpha");
            ManualFixture.Record(database, entry, "a", 10, "Alpha", matchType: "manual", locked: true);
            ManualFixture.Record(database, entry, "b", 20, "Alpha B", variant: true);
            var log = new InMemoryDecisionLog();
            var manual = new ManualDecisions(database, new object(), log, new ManualFakeCatalogueOperations(database));
            manual.CaptureExistingManualMatches();
            database.Write(DatabaseFile.Catalog, c => c.Execute("INSERT INTO browse_cache(cache_key,mode,page,entry_ids_json,updated_at,last_accessed) VALUES('k','POPULAR',1,$ids,'x','x')",
                ("$ids", "[" + entry + "," + entry + "]")));
            var maintenance = new CatalogueMaintenance(database, new object(), log, manual);
            var result = maintenance.DeleteEntry(entry, Owner);
            Check.Equal(2, result["source_records_suppressed"]!.GetValue<int>());
            Check.Equal(2L, ManualFixture.Count(database, "SELECT COUNT(*) FROM deleted_source_records"));
            Check.Equal(0L, ManualFixture.Count(database, "SELECT COUNT(*) FROM manual_match_bindings"));
            Check.Equal(0L, ManualFixture.Count(database, "SELECT COUNT(*) FROM entries"));
            Check.Equal("[]", database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT entry_ids_json FROM browse_cache") as string));
            Check.True(log.Kinds.Contains("entry.delete"), "Deletion logged");
            Check.Throws<KeyNotFoundException>(() => maintenance.DeleteEntry(entry, Owner));
        });

        suite.Add("catalogue maintenance: detaching, promoting and clearing records keep the catalogue consistent", () =>
        {
            using var temp = new Sandbox();
            using var database = ManualFixture.Open(temp);
            var entry = ManualFixture.Entry(database, "Alpha");
            ManualFixture.Record(database, entry, "a", 10, "Alpha", matchType: "manual", locked: true);
            ManualFixture.Record(database, entry, "a", 11, "Alpha Extra", variant: true);
            ManualFixture.Record(database, entry, "a", 12, "Alpha Third", variant: true);
            var operations = new ManualFakeCatalogueOperations(database);
            var manual = new ManualDecisions(database, new object(), new InMemoryDecisionLog(), operations);
            var maintenance = new CatalogueMaintenance(database, new object(), new InMemoryDecisionLog(), manual,
                entry: id => new JsonObject { ["id"] = id, ["entry_view"] = true });
            Check.True(maintenance.ClearSourceVariant(entry, "a", 12, Owner)["entry_view"]!.GetValue<bool>(), "Answers the entry view");
            Check.Equal(0L, ManualFixture.Count(database, "SELECT active FROM source_variants WHERE source_manga_id=12"));
            Check.Throws<KeyNotFoundException>(() => maintenance.ClearSourceVariant(entry, "a", 12, Owner));
            maintenance.PromoteVariantToPrimary(entry, "a", 11, Owner);
            Check.Equal(11L, ManualFixture.Count(database, "SELECT source_manga_id FROM source_matches WHERE entry_id=" + entry + " AND active=1"));
            Check.Equal(1L, ManualFixture.Count(database, "SELECT COUNT(*) FROM source_variants WHERE source_manga_id=10 AND active=1"));
            maintenance.ClearMatch(entry, "a", Owner);
            Check.Equal("cleared", database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT match_type FROM source_matches WHERE entry_id=$e", ("$e", entry)) as string));
            Check.Equal(0L, ManualFixture.Count(database, "SELECT COUNT(*) FROM source_variants WHERE active=1"));
            Check.True(operations.Calls.Contains("chapters " + entry), "Chapter ownership re-checked");
        });

        suite.Add("catalogue maintenance: subscribed reset seeds come back under their ids and source-less clones are aliased", () =>
        {
            using var temp = new Sandbox();
            using var database = ManualFixture.Open(temp);
            var io = new BoundedWorkScheduler(1, 4);
            var library = new SuwayomiLibrary(database, new SettingsRepository(database), () => new AutoSourcarrSettings(), () => null, io);
            try
            {
                var snapshot = library.SnapshotResetSeedsAsync().GetAwaiter().GetResult();
                Check.True(snapshot["ok"]!.GetValue<bool>() == false && snapshot["seeds"]!.AsArray().Count == 0, "No main Suwayomi: no seeds");
                var restored = library.RestoreResetSeeds([
                    new JsonObject { ["entry_id"] = 50, ["canonical_entry_id"] = 60, ["title"] = "Seed Work", ["manual_title"] = false },
                    new JsonObject { ["entry_id"] = 60, ["canonical_entry_id"] = 60, ["title"] = "Seed Work", ["description"] = "Longer description" },
                ]);
                Check.Equal(1, restored);
                Check.Equal("Seed Work", database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT preferred_title FROM entries WHERE id=60") as string));
                Check.Equal(60L, ManualFixture.Count(database, "SELECT canonical_entry_id FROM entry_aliases WHERE old_entry_id=50"));
                Check.Equal(1L, ManualFixture.Count(database, "SELECT COUNT(*) FROM titles WHERE entry_id=60 AND title_type='main-library'"));

                database.Write(DatabaseFile.Catalog, c => c.Execute("""
                    INSERT INTO entries(id,preferred_title,thumbnail_url,created_at,updated_at) VALUES(70,'Clone Work','http://cover/x','now','now'),(71,'Clone Work','http://cover/x','now','now');
                    INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,last_checked) VALUES(71,'a',5,'Clone Work','now');
                    """));
                var repaired = library.RepairOrphanAliases([70, 71]);
                Check.Equal(1, repaired["aliased"]!.GetValue<int>());
                Check.Equal(71L, ManualFixture.Count(database, "SELECT canonical_entry_id FROM entry_aliases WHERE old_entry_id=70"));
            }
            finally
            {
                library.DisposeAsync().AsTask().GetAwaiter().GetResult();
                io.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });

        suite.Add("catalogue maintenance: startup repairs clean titles once and compare records conservatively", () =>
        {
            using var temp = new Sandbox();
            using var database = ManualFixture.Open(temp);
            var entry = ManualFixture.Entry(database, "Alpha");
            ManualFixture.Source(database, "a", "Source A");
            ManualFixture.Record(database, entry, "a", 10, "Alpha Story");
            database.Write(DatabaseFile.Catalog, c =>
            {
                c.Execute("INSERT INTO titles(entry_id,title,normalized,title_type) VALUES($e,'Old alias','old alias','alias')", ("$e", entry));
                return c.Execute("INSERT INTO titles(entry_id,title,normalized,title_type) VALUES($e,'Kept Manual Title','kept manual title','manual')", ("$e", entry));
            });
            var log = new InMemoryDecisionLog();
            var manual = new ManualDecisions(database, new object(), log);
            var maintenance = new CatalogueMaintenance(database, new object(), log, manual);
            var first = maintenance.RunStartupCatalogMaintenance();
            Check.Equal(false, first["skipped"]!.GetValue<bool>());
            Check.Equal("unavailable", first["alias_repair"]!.GetValue<string>());
            Check.Equal(1L, ManualFixture.Count(database, "SELECT COUNT(*) FROM titles WHERE title_type='manual'"));
            Check.Equal(0L, ManualFixture.Count(database, "SELECT COUNT(*) FROM titles WHERE title_type='alias'"));
            Check.Equal(1L, ManualFixture.Count(database, "SELECT COUNT(*) FROM titles WHERE title_type='matched-alias' AND title='Alpha Story'"));
            Check.True(maintenance.RunStartupCatalogMaintenance()["skipped"]!.GetValue<bool>(), "Runs once per version");
            Check.True(log.Kinds.Contains("maintenance.startup"), "Logged with its upgrade actor");

            static RecordRepairSnapshot Snapshot(string title, string description, string color = "unknown") =>
                new("a", 1, title, AutoSourcarr.Core.Matching.TextMatcher.NormalizeTitle(title), description, "", "", color, "unknown", false);
            Check.True(CatalogueMaintenance.RecordsDirectlyCompatible(Snapshot("Same Title", ""), Snapshot("Same Title", "")), "Same normalised title");
            Check.True(!CatalogueMaintenance.RecordsDirectlyCompatible(Snapshot("Same Title", "", "colored"), Snapshot("Same Title", "", "monochrome")), "Never across colour editions");
            Check.True(!CatalogueMaintenance.RecordsDirectlyCompatible(Snapshot("Completely Different", ""), Snapshot("Another Work", "")), "Unrelated records");
            Check.Equal("webtoon", CatalogueMaintenance.MediaMode("Alpha", "Action, Webtoon"));
            Check.Equal("unknown", CatalogueMaintenance.MediaMode("Alpha", "Manga; Webtoon"));
            Check.Equal("skipped", maintenance.RepairUnsafeAutomaticSourceAttachments()["skipped"] is null ? "ran" : "skipped");
        });

        suite.Add("catalogue maintenance: a normal reset keeps different-works reviews and manual titles and re-keys them to the restored works", () =>
        {
            using var temp = new Sandbox();
            using var database = ManualFixture.Open(temp);
            var alpha = ManualFixture.Entry(database, "Alpha"); ManualFixture.Record(database, alpha, "a", 10, "Alpha");
            var beta = ManualFixture.Entry(database, "Beta"); ManualFixture.Record(database, beta, "b", 20, "Beta");
            var gone = ManualFixture.Entry(database, "Gone"); ManualFixture.Record(database, gone, "c", 30, "Gone");
            database.Write(DatabaseFile.Catalog, c =>
            {
                c.Execute("INSERT INTO duplicate_reviews(left_entry_id,right_entry_id,decision,reviewed_at) VALUES($a,$b,'not_duplicate','t'),($a,$g,'not_duplicate','t'),($b,$g,'duplicate','t')",
                    ("$a", alpha), ("$b", beta), ("$g", gone));
                c.Execute("INSERT INTO titles(entry_id,title,normalized,title_type) VALUES($a,'Alpha Alias','alpha alias','manual')", ("$a", alpha));
                c.Execute("INSERT INTO preferred_title_overrides(entry_id,title,updated_at) VALUES($a,'Alpha Prime','t')", ("$a", alpha));
                c.Execute("INSERT INTO manual_separate_records(target_entry_id,source_id,source_manga_id,separate_entry_id_hint,created_at,updated_at) VALUES($a,'b',20,$b,'t','t'),($g,'a',10,NULL,'t','t')",
                    ("$a", alpha), ("$b", beta), ("$g", gone));
                return c.Execute("INSERT INTO deleted_source_records(source_id,source_manga_id,deleted_at) VALUES('d',40,'t')");
            });
            var log = new InMemoryDecisionLog();
            var manual = new ManualDecisions(database, new object(), log);
            var maintenance = new CatalogueMaintenance(database, new object(), log, manual);
            var cleared = maintenance.ClearTables(full: false, Owner);
            Check.True(cleared["summary"]!.GetValue<string>().Contains("dropped", StringComparison.Ordinal), "the summary states what a normal reset drops");
            Check.Equal(0L, ManualFixture.Count(database, "SELECT COUNT(*) FROM entries"));
            Check.Equal(2L, ManualFixture.Count(database, "SELECT COUNT(*) FROM duplicate_reviews WHERE decision='not_duplicate'"));
            Check.Equal(0L, ManualFixture.Count(database, "SELECT COUNT(*) FROM duplicate_reviews WHERE decision='duplicate'"));
            Check.Equal(1L, ManualFixture.Count(database, "SELECT COUNT(*) FROM deleted_source_records"));
            // The rebuild brings Alpha and Beta back (their records reattach); Gone does not come back.
            var alpha2 = ManualFixture.Entry(database, "Alpha"); ManualFixture.Record(database, alpha2, "a", 10, "Alpha");
            var beta2 = ManualFixture.Entry(database, "Beta"); ManualFixture.Record(database, beta2, "b", 20, "Beta");
            var rekeyed = maintenance.RekeyKeptDecisions();
            Check.Equal(1, rekeyed["different_works_reviews"]!["kept"]!.GetValue<int>());
            Check.Equal(1, rekeyed["different_works_reviews"]!["dropped"]!.GetValue<int>());
            Check.Equal(1, rekeyed["separate_rules"]!["dropped"]!.GetValue<int>());
            Check.True(manual.IsRejected(alpha2, beta2), "the different-works review follows the restored works");
            Check.True(manual.IsSeparatedFrom(alpha2, "b", 20), "the separate rule follows the restored work");
            Check.Equal(0L, ManualFixture.Count(database, "SELECT COUNT(*) FROM manual_separate_records r WHERE NOT EXISTS(SELECT 1 FROM entries e WHERE e.id=r.target_entry_id)"));
            Check.Equal(1L, ManualFixture.Count(database, "SELECT COUNT(*) FROM titles WHERE title_type='manual' AND title='Alpha Alias' AND entry_id=" + alpha2.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Check.Equal("Alpha Prime", database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT preferred_title FROM entries WHERE manual_preferred_title=1") as string));
            Check.Equal(0L, ManualFixture.Count(database, "SELECT COUNT(*) FROM settings WHERE key='" + CatalogueMaintenance.CarrySetting + "'"));
            Check.True(maintenance.RekeyKeptDecisions()["pending"]!.GetValue<bool>() == false, "re-keying runs once per reset");
        });
    }
}
