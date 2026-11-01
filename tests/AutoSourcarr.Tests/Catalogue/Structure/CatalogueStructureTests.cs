using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Tests;

public static class CatalogueStructureTests
{
    private const string Stamp = "2026-10-21T00:00:00+00:00";
    public static void Register(TestSuite suite)
    {
        suite.Add("catalogue structure: released origins SQL corpus preserves collisions, pages and served history", OriginsOracle);
        suite.Add("catalogue structure: released reliability ranking retains Unicode scalar lengths and stable survivors", RankingOracle);
        suite.Add("catalogue structure: actual reviewed group preserves native records, entry aliases and numeric reader identity", () =>
        {
            using var f = new Setup(); f.Native(9007199254740993, "17", 111, "The Native Chronicle"); f.Native(9007199254740994, "18", 222, "The Native Chronicle");
            var choice = f.Choice("group", [9007199254740993], [("17", 111), ("18", 222)]); var result = f.Apply(choice);
            Check.Equal("applied", S(result, "state")); Check.Equal(1L, f.Count("entries")); Check.Equal(2L, f.Count("source_matches")); Check.Equal(9007199254740993L, f.Canonical(9007199254740994)); Check.Equal(0L, f.Count("pending_entry_merges")); Check.Equal(2L, f.Count("manual_match_bindings")); Check.True(f.MetadataCalls > 0 && f.VisibilityCalls > 0);
        });
        suite.Add("catalogue structure: same-source merge retains the richer primary and losing variant by named columns", () =>
        {
            using var f = new Setup(); f.Native(1, "17", 111, "The First Native", locked: true); f.Native(2, "17", 222, "The Second Native");
            f.C(c => c.Execute("UPDATE source_matches SET confidence=.99,initialized=1,description='Rich retained metadata' WHERE entry_id=2"));
            f.Apply(f.Choice("group", [1], [("17", 111), ("17", 222)])); Check.Equal(111L, f.Scalar("SELECT source_manga_id FROM source_matches")!); Check.Equal(222L, f.Scalar("SELECT source_manga_id FROM source_variants")!);
            Check.Equal("Rich retained metadata", f.Scalar("SELECT description FROM source_variants") as string); Check.Equal(1L, f.Scalar("SELECT entry_id FROM source_variants")!);
        });
        suite.Add("catalogue structure: inactive primary tombstones cannot discard an incoming active record", () =>
        {
            using var f = new Setup(); f.Native(1, "17", 111, "Target native title", locked: true); f.Native(2, "18", 222, "Incoming native title");
            f.C(c => c.Execute("INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,active,last_checked) VALUES(1,'18',999,'Old tombstone',0,'now')")); f.Apply(f.Choice("group", [1], [("17", 111), ("18", 222)]));
            Check.Equal(2L, f.Count("source_matches")); Check.Equal(0L, f.Scalar("SELECT COUNT(*) FROM source_matches WHERE source_manga_id=999")!); Check.Equal(1L, f.Scalar("SELECT entry_id FROM source_matches WHERE source_manga_id=222")!);
        });
        suite.Add("catalogue structure: provider ranking and losing entry-local manual titles remain scoped", () =>
        {
            using var f = new Setup(); f.Native(1, "17", 111, "Surviving user title", locked: true); f.Native(2, "18", 222, "Absorbed user title");
            f.C(c =>
            {
                c.Execute("UPDATE entries SET manual_preferred_title=1 WHERE id IN (1,2)"); c.Execute("INSERT INTO preferred_title_overrides VALUES(1,'Surviving user title','now'),(2,'Absorbed user title','now')");
                c.Execute("INSERT INTO titles(entry_id,title,normalized,title_type) VALUES(2,'Do not inherit manual alias','do not inherit manual alias','manual'),(2,'Retained source alias','retained source alias','source')");
                c.Execute("INSERT INTO identity_links(entry_id,provider,external_id,confidence,locked,title,last_checked) VALUES(1,'anilist','1',.94,0,'Provider original','now'),(2,'anilist','2',.99,1,'Provider stronger','now')"); return 0;
            });
            var result = f.Apply(f.Choice("group", [1], [("17", 111), ("18", 222)])); Check.True(S(result, "state") == "applied", result.ToJsonString()); Check.Equal("Surviving user title", f.Scalar("SELECT preferred_title FROM entries") as string); Check.Equal("2", f.Scalar("SELECT external_id FROM identity_links") as string);
            // Released export/_associate also round-trip text as cached facts. The losing
            // manual row must disappear, while retained portable text cannot become native proof.
            Check.Equal(0L, f.Scalar("SELECT COUNT(*) FROM titles WHERE title='Do not inherit manual alias' AND title_type<>'cached'")!); Check.Equal(1L, f.Scalar("SELECT COUNT(*) FROM titles WHERE title='Do not inherit manual alias' AND title_type='cached'")!);
            Check.Equal(1L, f.Scalar("SELECT COUNT(*) FROM titles WHERE title='Retained source alias' AND title_type='source'")!); Check.Equal(1L, f.Scalar("SELECT COUNT(*) FROM titles WHERE title='Retained source alias' AND title_type='cached'")!); Check.Equal(1L, f.Count("preferred_title_overrides"));
            var snapshot = f.Database.Read(DatabaseFile.Catalog, c => f.Evidence.Snapshot(c, 1)); Check.True(!snapshot["titles"]!.AsArray().Any(value => value!.GetValue<string>() == "Do not inherit manual alias"), snapshot.ToJsonString());
        });
        suite.Add("catalogue structure: profile, tracking, display, identity and portable lineage retain history", () =>
        {
            using var f = new Setup(); f.Native(1, "17", 111, "Target entry", locked: true); f.Native(2, "18", 222, "Absorbed entry");
            f.C(c =>
            {
                c.Execute("INSERT INTO serving_profiles VALUES(1,'{\"quality\":\"quality\"}',4,'now'),(2,'{\"quality\":\"complete\"}',7,'now')");
                c.Execute("INSERT INTO library_tracking VALUES(1,0,3,1001,'{\"download_state\":\"checked\",\"coverage_state\":\"checked\"}','now','now'),(2,1,8,1002,'{}','now','now')");
                c.Execute("INSERT INTO display_metadata VALUES(1,1,'{\"description\":\"Display target\"}','now'),(2,1,'{\"description\":\"Display absorbed\"}','now')");
                c.Execute("INSERT INTO portable_identity_reviews VALUES(2,'asc1:work:anilist:12','confirmed','proof','Retained note','now')"); return 0;
            });
            var before = f.Database.Write(DatabaseFile.Catalog, c => CatalogueIdentity.Ensure(c, 2)); f.Apply(f.Choice("group", [1], [("17", 111), ("18", 222)]));
            Check.Equal("{\"quality\":\"quality\"}", f.Scalar("SELECT profile_json FROM serving_profiles") as string); Check.Equal(1L, f.Count("entry_preference_history")); Check.Equal(1001L, f.Scalar("SELECT main_manga_id FROM library_tracking")!); Check.Equal(0L, f.Scalar("SELECT monitored FROM library_tracking")!);
            Check.Equal("unknown", S(JsonNode.Parse((string)f.Scalar("SELECT observation_json FROM library_tracking")!)!, "download_state")); Check.True(((string)f.Scalar("SELECT fields_json FROM display_metadata")!).Contains("Display target", StringComparison.Ordinal));
            Check.Equal(1L, f.Count("display_metadata_history")); Check.Equal("needs-review", f.Scalar("SELECT decision FROM portable_identity_reviews") as string); Check.Equal(1L, f.Count("portable_identity_review_history")); Check.Equal(1L, f.Database.Read(DatabaseFile.Catalog, c => CatalogueIdentity.Targets(c, [before]).Single()));
        });
        suite.Add("catalogue structure: catalogue alias failure rolls back ownership and publishes no committed invalidation", () =>
        {
            using var f = new Setup(); f.Native(1, "17", 111, "First native entry"); f.Native(2, "18", 222, "Second native entry"); var choice = f.Choice("group", [1], [("17", 111), ("18", 222)]);
            f.C(c => c.Execute("CREATE TRIGGER fixture_alias_abort BEFORE INSERT ON entry_aliases BEGIN SELECT RAISE(ABORT,'fixture alias failure'); END"));
            Check.Throws<SqliteException>(() => f.Apply(choice)); Check.Equal(2L, f.Count("entries")); Check.Equal(0L, f.Count("entry_aliases")); Check.Equal(0L, f.Count("pending_entry_merges")); Check.Equal(2L, f.Scalar("SELECT entry_id FROM source_matches WHERE source_id='18'")!); Check.Equal(0, f.Invalidated.Count);
        });
        suite.Add("catalogue structure: committed merge and failed origins reopen and recover without losing pages", () =>
        {
            using var f = new Setup(); f.Native(1, "17", 111, "First native entry"); f.Native(2, "18", 222, "Second native entry"); f.Chapter(2, 20, 200, "18", 222); var choice = f.Choice("group", [1], [("17", 111), ("18", 222)]);
            f.O(o => o.Execute("CREATE TRIGGER fixture_origin_abort BEFORE UPDATE OF entry_id ON virtual_chapters BEGIN SELECT RAISE(ABORT,'fixture origin failure'); END")); Check.Throws<SqliteException>(() => f.Apply(choice));
            Check.Equal(1L, f.Count("entries")); Check.Equal(1L, f.Count("pending_entry_merges")); Check.Equal(1L, f.Count("candidate_pages", DatabaseFile.Origins)); Check.Equal(1L, f.Count("origin_history", DatabaseFile.Origins));
            f.O(o => o.Execute("DROP TRIGGER fixture_origin_abort")); using var reopened = new SqliteDatabase(new AutoSourcarrSettings { DataDir = f.Path }); var owner = f.NewOwner(reopened); owner.ReconcilePending(default); owner.ReconcilePending(default);
            Check.Equal(0L, f.Count("pending_entry_merges")); Check.Equal(1L, f.Scalar("SELECT entry_id FROM virtual_chapters", DatabaseFile.Origins)!); Check.Equal(1L, f.Count("origin_history", DatabaseFile.Origins)); Check.Equal(1L, f.Count("match_history"));
        });
        suite.Add("catalogue structure: journal acknowledgement retry preserves newer scans and one catalogue audit", () =>
        {
            using var f = new Setup(); f.Native(1, "17", 111, "Target native entry"); f.Native(2, "18", 222, "Other native entry"); f.Chapter(2, 20, 200, "18", 222);
            f.C(c => c.Execute("CREATE TRIGGER fixture_ack_abort BEFORE DELETE ON pending_entry_merges BEGIN SELECT RAISE(ABORT,'fixture ack failure'); END")); Check.Throws<SqliteException>(() => f.Apply(f.Choice("group", [1], [("17", 111), ("18", 222)])));
            f.O(o => o.Execute("UPDATE source_record_scans SET checked_at='2099',chapter_count=99,error='newer'; INSERT INTO source_record_scans VALUES(2,'18',222,'2000',7,'older')")); f.C(c => c.Execute("DROP TRIGGER fixture_ack_abort")); f.Owner.ReconcilePending(default);
            Check.Equal(1L, f.Count("match_history")); Check.Equal(99L, f.Scalar("SELECT chapter_count FROM source_record_scans", DatabaseFile.Origins)!); Check.Equal("newer", f.Scalar("SELECT error FROM source_record_scans", DatabaseFile.Origins) as string);
        });
        suite.Add("catalogue structure: failed invalidation remains journalled across reopen until its owner succeeds", () =>
        {
            using var f = new Setup(); f.Native(1, "17", 111, "Target native entry"); f.Native(2, "18", 222, "Other native entry"); f.Chapter(2, 20, 200, "18", 222); f.CachedEntries.Add(1); f.CachedEntries.Add(2);
            var choice = f.Choice("group", [1], [("17", 111), ("18", 222)]); f.FailInvalidation = true;
            Check.Throws<InvalidOperationException>(() => f.Apply(choice)); Check.Equal(1L, f.Count("pending_entry_merges")); Check.Equal(1L, f.Canonical(2)); Check.Equal(1L, f.Scalar("SELECT entry_id FROM virtual_chapters", DatabaseFile.Origins)!); Check.Equal(1, f.InvalidationCalls); Check.Equal(0, f.Invalidated.Count); Check.Equal(2, f.CachedEntries.Count);
            Check.True(((string)f.Scalar("SELECT last_error FROM pending_entry_merges")!).Contains("fixture cache invalidation failure", StringComparison.Ordinal));
            using var reopened = new SqliteDatabase(new AutoSourcarrSettings { DataDir = f.Path }); var owner = f.NewOwner(reopened);
            Check.Throws<CatalogueStructureRecoveryException>(() => owner.ReconcilePending(default)); Check.Equal(1L, f.Count("pending_entry_merges")); Check.Equal(2, f.InvalidationCalls); Check.Equal(2, f.CachedEntries.Count); Check.Equal(0, f.Invalidated.Count);
            f.FailInvalidation = false; owner.ReconcilePending(default); Check.Equal(0L, f.Count("pending_entry_merges")); Check.Equal(3, f.InvalidationCalls); Check.True(f.Invalidated.SequenceEqual([1L, 2L])); Check.Equal(0, f.CachedEntries.Count); Check.True(f.MetadataCalls >= 3 && f.VisibilityCalls >= 3);
            owner.ReconcilePending(default); Check.Equal(3, f.InvalidationCalls); Check.Equal(1L, f.Count("match_history")); Check.Equal(1L, f.Count("candidate_pages", DatabaseFile.Origins));
        });
        suite.Add("catalogue structure: ready sharing retry cannot bypass pending metadata or visibility recovery", () =>
        {
            using var f = new Setup(); f.Native(1, "17", 111, "First native entry"); f.Native(2, "18", 222, "Second native entry"); var choice = f.Choice("group", [1], [("17", 111), ("18", 222)]); f.FailMetadata = true;
            Check.Throws<InvalidOperationException>(() => f.Apply(choice)); Check.Equal(1L, f.Count("pending_entry_merges")); var ready = f.Exchange.Decision(S(choice, "uid"), S(choice, "digest")); Check.Equal("ready", S(ready, "state"));
            Check.Throws<CatalogueStructureRecoveryException>(() => f.Apply(ready)); Check.Equal(0L, f.Count("manual_match_bindings")); Check.Equal("pending", f.Scalar("SELECT state FROM catalogue_decisions") as string);
            f.FailMetadata = false; f.FailVisibility = true; Check.Throws<CatalogueStructureRecoveryException>(() => f.Apply(ready)); Check.Equal(1L, f.Count("pending_entry_merges"));
            f.FailVisibility = false; Check.Equal("applied", S(f.Apply(ready), "state")); Check.Equal(0L, f.Count("pending_entry_merges")); Check.Equal(2L, f.Count("manual_match_bindings"));
        });
        suite.Add("catalogue structure: actual manual split keeps stable original title and deactivates detached history", () =>
        {
            using var f = new Setup(); f.Native(1, "18", 222, "Second native record", locked: true); f.Native(2, "17", 111, "First native record");
            // A real earlier merge retains the absorbed record's source-scoped UID.
            // It still uniquely identifies the surviving source after the next split.
            var survivingUid = f.Database.Write(DatabaseFile.Catalog, c => { CatalogueIdentity.BindNative(c, 2, "17", 111, "/manga/111"); return CatalogueIdentity.Ensure(c, 2); });
            var grouped = f.Apply(f.Choice("group", [1], [("17", 111), ("18", 222)])); Check.True(S(grouped, "state") == "applied", grouped.ToJsonString()); Check.Equal(1L, f.Canonical(2));
            f.Chapter(1, 10, 100, "18", 222); f.C(c => c.Execute("UPDATE entries SET preferred_title='My original manual title',manual_preferred_title=1 WHERE id=1; INSERT INTO preferred_title_overrides VALUES(1,'My original manual title','now')"));
            var result = f.Apply(f.Choice("separate", [1], [("18", 222)], [survivingUid])); Check.True(S(result, "state") == "applied", result.ToJsonString()); Check.Equal(2L, f.Count("entries")); Check.Equal("My original manual title", f.Scalar("SELECT preferred_title FROM entries WHERE id=1") as string); Check.True((string)f.Scalar("SELECT preferred_title FROM entries WHERE id=3")! != "My original manual title");
            Check.Equal(0L, f.Scalar("SELECT active FROM chapter_candidates", DatabaseFile.Origins)!); Check.Equal(1L, f.Count("candidate_pages", DatabaseFile.Origins)); Check.Equal(1L, f.Count("served_origin", DatabaseFile.Origins)); Check.Equal(1L, f.Count("origin_history", DatabaseFile.Origins)); Check.Equal(3L, f.Scalar("SELECT entry_id FROM source_record_scans", DatabaseFile.Origins)!); Check.Equal(0L, f.Count("pending_entry_splits")); Check.Equal(1L, f.Count("duplicate_split_history")); Check.Equal(1L, f.Count("manual_separate_records")); Check.Equal(1L, f.Database.Read(DatabaseFile.Catalog, c => CatalogueIdentity.Targets(c, [survivingUid]).Single()));
            Check.Throws<KeyNotFoundException>(() => f.Reader.PageFetchInfo(100, 0));
        });
        suite.Add("catalogue structure: a former parent UID remains ambiguous after a completed source split", () =>
        {
            using var f = new Setup(); f.Native(1, "17", 111, "First native record"); f.Native(1, "18", 222, "Second native record"); var parent = f.Database.Write(DatabaseFile.Catalog, c => CatalogueIdentity.Ensure(c, 1)); var choice = f.Choice("separate", [1], [("18", 222)]);
            var result = f.Apply(choice); Check.True(S(result, "state") == "pending", result.ToJsonString()); Check.Equal("Needs a local entry or source identity before this choice can be applied.", S(result, "message")); Check.True(f.Database.Read(DatabaseFile.Catalog, c => CatalogueIdentity.Targets(c, [parent])).SequenceEqual([1L, 2L]));
            Check.Equal(2L, f.Count("entries")); Check.Equal(0L, f.Count("pending_entry_splits")); Check.Equal(1L, f.Count("duplicate_split_history")); Check.Equal(2L, f.Scalar("SELECT entry_id FROM source_matches WHERE source_id='18'")!); Check.Equal(1L, f.Count("manual_separate_records"));
            var retained = f.Exchange.Decision(S(choice, "uid"), S(choice, "digest")); result = f.Apply(retained); Check.True(S(result, "state") == "pending", result.ToJsonString()); Check.Equal(1L, f.Count("duplicate_split_history")); Check.Equal(0L, f.Count("pending_entry_splits"));
        });
        suite.Add("catalogue structure: split primary promotion and manual group detachment retain remaining intent", () =>
        {
            using var f = new Setup(); f.Native(1, "17", 111, "Primary native record"); f.Native(1, "17", 112, "Remaining native variant", variant: true); f.Native(1, "18", 222, "Third native record");
            f.C(c => c.Execute("INSERT INTO manual_match_groups VALUES('existing','User group','now','now'); INSERT INTO manual_match_bindings(source_id,source_manga_id,group_id,entry_id_hint,source_title,created_at,updated_at) VALUES('17',111,'existing',1,'Primary native record','now','now'),('17',112,'existing',1,'Remaining native variant','now','now'),('18',222,'existing',1,'Third native record','now','now')"));
            f.Apply(f.Choice("separate", [1], [("17", 111)])); Check.Equal(112L, f.Scalar("SELECT source_manga_id FROM source_matches WHERE entry_id=1 AND source_id='17'")!); Check.Equal(1L, f.Scalar("SELECT COUNT(*) FROM manual_match_groups WHERE id='existing'")!); Check.Equal(2L, f.Scalar("SELECT COUNT(*) FROM manual_match_bindings WHERE group_id='existing'")!); Check.Equal(2L, f.Scalar("SELECT entry_id_hint FROM manual_match_bindings WHERE source_id='17' AND source_manga_id=111")!);
        });
        suite.Add("catalogue structure: split recovery follows later actual ownership and does not resurrect a source", () =>
        {
            using var f = new Setup(); f.Native(1, "17", 111, "First native record"); f.Native(1, "18", 222, "Second native record"); f.Chapter(1, 10, 100, "18", 222);
            f.O(o => o.Execute("CREATE TRIGGER fixture_split_abort BEFORE UPDATE OF active ON chapter_candidates WHEN NEW.active=0 BEGIN SELECT RAISE(ABORT,'fixture split failure'); END")); Check.Throws<SqliteException>(() => f.Apply(f.Choice("separate", [1], [("18", 222)]))); Check.Equal(1L, f.Count("pending_entry_splits"));
            f.O(o => o.Execute("DROP TRIGGER fixture_split_abort")); f.C(c => c.Execute("UPDATE source_matches SET entry_id=1 WHERE source_id='18'; DELETE FROM manual_separate_records WHERE source_id='18'; DELETE FROM duplicate_reviews")); f.Owner.ReconcilePending(default);
            Check.Equal(1L, f.Scalar("SELECT active FROM chapter_candidates", DatabaseFile.Origins)!); Check.Equal(1L, f.Scalar("SELECT entry_id FROM source_record_scans", DatabaseFile.Origins)!); Check.Equal(1L, f.Count("duplicate_split_history"));
        });
        suite.Add("catalogue structure: missing gallery replay keeps original intent and journal pending", () =>
        {
            using var f = new Setup(); f.Native(1, "17", 111, "First native record"); f.C(c => c.Execute("INSERT INTO pending_entry_splits VALUES(1,1,2,'[[\"17\",111]]','now',''); INSERT INTO gallery_layout_candidates VALUES(1,'[100]')"));
            Check.Throws<CatalogueStructureRecoveryException>(() => f.Owner.ReconcilePending(default)); Check.Equal(1L, f.Count("gallery_layout_candidates")); Check.Equal(1L, f.Count("pending_entry_splits")); Check.True(((string)f.Scalar("SELECT last_error FROM pending_entry_splits")!).Contains("unavailable", StringComparison.Ordinal));
        });
        suite.Add("catalogue structure: local Separate and current native edition proof block foreign grouping", () =>
        {
            foreach (var edition in new[] { false, true })
            {
                using var f = new Setup(); f.Native(1, "17", 111, "First native title"); f.Native(2, "18", 222, "Second native title");
                if (edition) f.C(c => c.Execute("INSERT INTO source_color_profiles VALUES('17',111,'colored',.99,1,'fixture',1,'now'),('18',222,'monochrome',.99,1,'fixture',0,'now')")); else f.C(c => c.Execute("INSERT INTO duplicate_reviews VALUES(1,2,'not_duplicate','now')"));
                // A local Separate is refused up front by CatalogueExchange.Protect (ArgumentException, decision stays pending);
                // current native edition proof is refused by the structure owner (InvalidOperationException).
                if (edition) Check.Throws<InvalidOperationException>(() => f.Apply(f.Choice("group", [1], [("17", 111), ("18", 222)])));
                else Check.True(Check.Throws<ArgumentException>(() => f.Apply(f.Choice("group", [1], [("17", 111), ("18", 222)]))).Message.StartsWith("A local Separate decision is retained", StringComparison.Ordinal), "Separate refusal message");
                Check.Equal(2L, f.Count("entries")); Check.Equal(0L, f.Count("pending_entry_merges"));
            }
        });
        suite.Add("catalogue structure: full native snapshot excludes shared/display aliases and retains exact lineage evidence", () =>
        {
            using var f = new Setup(); f.Native(1, "17", 111, "Monster"); f.C(c => c.Execute("INSERT INTO titles(entry_id,title,normalized,title_type) VALUES(1,'Private manual alias','private manual alias','manual'),(1,'Shared cached alias','shared cached alias','cached'),(1,'Provider alias','provider alias','identity-alias'); INSERT INTO display_metadata VALUES(1,1,'{\"description\":\"Display alias\"}','now'); INSERT INTO identity_links(entry_id,provider,external_id,confidence,match_type,locked,last_checked) VALUES(1,'anilist','123',.99,'manual',1,'now')"));
            var snapshot = f.Database.Read(DatabaseFile.Catalog, c => f.Evidence.Snapshot(c, 1)); Check.True(snapshot["titles"]!.AsArray().Any(value => value!.GetValue<string>() == "Monster")); Check.True(!snapshot["titles"]!.AsArray().Any(value => value!.GetValue<string>() is "Shared cached alias" or "Display alias" or "Provider alias")); Check.Equal("123", S(snapshot["identity_details"]![0]!, "external_id")); Check.True(snapshot["source_ids"]!.AsArray().Single()!.GetValue<string>() == "17");
            f.Native(2, "18", 222, "Sibling native title"); f.C(c => c.Execute("INSERT INTO automatic_merge_lineage VALUES(1,77,'{\"entry_id\":77,\"titles\":[\"Frozen original\"]}','now')")); f.Apply(f.Choice("group", [1], [("17", 111), ("18", 222)])); Check.Equal(2L, f.Count("automatic_merge_lineage")); Check.True(((string)f.Scalar("SELECT snapshot_json FROM automatic_merge_lineage WHERE member_entry_id=77")!).Contains("Frozen original", StringComparison.Ordinal)); Check.Equal(0L, f.Count("automatic_merge_events"));
        });
        suite.Add("catalogue structure: source match joins only an exact unverified counterpart and otherwise needs Match matrix", () =>
        {
            using (var f = new Setup())
            {
                f.Native(2, "17", 111, "Actual native counterpart"); var exported = f.Exchange.Export()["catalogue"]!["entries"]!.AsArray().Single()!; var fact = exported["sources"]![0]!;
                f.C(c => { c.Execute("INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(1,'Shared unverified work','now','now'); INSERT INTO catalogue_shared_entries VALUES(1)"); CatalogueIdentity.Ensure(c, 1); return c.Execute("INSERT INTO catalogue_source_facts VALUES(1,$p0,'17',$p1)", ("$p0", S(fact, "reference")), ("$p1", CatalogueFormat.Canonical(fact))); });
                Check.Equal("applied", S(f.Apply(f.Choice("source-match", [1], [("17", 111)])), "state")); Check.Equal(2L, f.Canonical(1)); Check.Equal(1L, f.Count("entries")); Check.Equal(1L, f.Count("source_matches"));
            }
            using (var f = new Setup())
            {
                f.Native(1, "17", 111, "Native target work"); f.Native(2, "18", 222, "Other native work"); Check.Throws<InvalidOperationException>(() => f.Apply(f.Choice("source-match", [1], [("18", 222)]))); Check.Equal(2L, f.Count("entries")); Check.Equal(0L, f.Count("pending_entry_merges"));
            }
        });
    }
    private static string S(JsonNode node, string key) => node[key]!.GetValue<string>();
    private static void OriginsOracle()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("catalogue-structure.json")))!.AsObject(); var cases = 0;
        foreach (var row in fixture["origins"]!.AsArray())
        {
            using var f = new Setup(); f.Native(1, "17", 111, "Target"); f.Native(1, "17", 112, "Old record", variant: true); foreach (var sql in row!["seed"]!.AsArray()) f.O(o => o.Execute(sql!.GetValue<string>())); f.C(c => c.Execute("INSERT INTO pending_entry_merges VALUES(2,1,'now','')"));
            f.Owner.ReconcilePending(default); var actual = f.Database.Read(DatabaseFile.Origins, Snapshot);
            using var expectedJson = JsonDocument.Parse(row["after"]!.ToJsonString()); using var actualJson = JsonDocument.Parse(actual.ToJsonString()); Check.JsonEqual(expectedJson.RootElement, actualJson.RootElement, "origin case " + S(row, "name")); cases++;
        }
        Check.Equal(4, cases); Console.WriteLine("Catalogue structure original origins SQL cases " + cases);
    }
    private static void RankingOracle()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("catalogue-structure.json")))!.AsObject(); var cases = 0;
        foreach (var row in fixture["ranking"]!.AsArray())
        {
            using var f = new Setup(); foreach (var (id, key) in new[] { (1L, "left"), (2L, "right") })
            {
                var value = row![key]!.AsObject(); f.Native(id, (16 + id).ToString(CultureInfo.InvariantCulture), id, "Fixture " + id, value["locked"]?.GetValue<int>() == 1);
                f.C(c => c.Execute("UPDATE entries SET description=$p0 WHERE id=$p1", ("$p0", value["description"]?.GetValue<string>() ?? ""), ("$p1", id)));
                for (var number = 0; number < (value["titles"]?.GetValue<int>() ?? 0); number++) f.C(c => c.Execute("INSERT INTO titles(entry_id,title,normalized,title_type) VALUES($p0,$p1,$p1,'alias')", ("$p0", id), ("$p1", "Title " + number)));
            }
            f.Apply(f.Choice("group", [1], [("17", 1), ("18", 2)])); Check.Equal(row!["survivor"]!.GetValue<long>(), f.Canonical(1)); Check.Equal(f.Canonical(1), f.Canonical(2)); cases++;
        }
        Check.Equal(4, cases);
    }
    private static JsonObject Snapshot(DatabaseSession c)
    {
        var result = new JsonObject(); foreach (var row in c.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name<>'VersionInfo' ORDER BY name"))
        { var table = row.Text("name"); result[table] = new JsonArray(c.Query("SELECT * FROM " + table + " ORDER BY rowid").Select(value => System.Text.Json.JsonSerializer.SerializeToNode(value.ToDictionary(pair => pair.Key, pair => pair.Value))).ToArray()); }
        return result;
    }
    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _sandbox = new(); public string Path => _sandbox.Path; public SqliteDatabase Database { get; } public object Gate { get; } = new(); public TaskRegistry Tasks { get; } = new(); public GalleryEvidenceService Gallery { get; } public CatalogueMetadata Metadata { get; } = new(() => .9); public NativeCatalogueEvidence Evidence { get; } public CatalogueStructure Owner { get; } public CatalogueExchange Exchange { get; } public ReaderRepository Reader { get; }
        public bool FailMetadata, FailVisibility, FailInvalidation; public int MetadataCalls, VisibilityCalls, InvalidationCalls; public List<long> Invalidated { get; } = []; public HashSet<long> CachedEntries { get; } = [];
        public Setup() { Database = new(new AutoSourcarrSettings { DataDir = Path }); new SchemaMigrator(Database).Initialize(); Gallery = new(Database, Gate); Exchange = new(Database, Gate, new SupportPrivacy()); Exchange.Initialize(); Reader = new(Database, Gate); Evidence = new(Metadata, Gallery, () => 24, () => true); Owner = NewOwner(Database); Exchange = new(Database, Gate, new SupportPrivacy(), structure: Owner, galleryEvidence: Gallery.CatalogueAdapter); }
        public CatalogueStructure NewOwner(SqliteDatabase database)
        {
            var gallery = new GalleryEvidenceService(database, Gate); var evidence = new NativeCatalogueEvidence(Metadata, gallery, () => 24, () => true); var reader = new ReaderRepository(database, Gate);
            return new(database, Gate, Tasks, gallery, (c, id, stamp) => { MetadataCalls++; if (FailMetadata) throw new InvalidOperationException("fixture native metadata failure"); Metadata.Recompute(c, id, stamp); }, id => { VisibilityCalls++; if (FailVisibility) throw new InvalidOperationException("fixture visibility failure"); reader.ReconcileVisibility(id); }, DisplayMetadata.Reparent, evidence.Snapshot, ids => { InvalidationCalls++; if (FailInvalidation) throw new InvalidOperationException("fixture cache invalidation failure"); foreach (var id in ids) CachedEntries.Remove(id); Invalidated.AddRange(ids); }, clock: new FixedClock());
        }
        public void C(Func<DatabaseSession, int> action) => Database.Write(DatabaseFile.Catalog, action); public void O(Func<DatabaseSession, int> action) => Database.Write(DatabaseFile.Origins, action);
        public object? Scalar(string sql, DatabaseFile file = DatabaseFile.Catalog) => Database.Read(file, c => c.Scalar(sql)); public long Count(string table, DatabaseFile file = DatabaseFile.Catalog) => Convert.ToInt64(Scalar("SELECT COUNT(*) FROM " + table, file), CultureInfo.InvariantCulture); public long Canonical(long id) => Reader.CanonicalEntryId(id);
        public void Native(long id, string source, long manga, string title, bool locked = false, bool variant = false)
        {
            C(c => { c.Execute("INSERT OR IGNORE INTO entries(id,preferred_title,created_at,updated_at) VALUES($p0,$p1,'now','now')", ("$p0", id), ("$p1", title)); c.Execute("INSERT OR IGNORE INTO source_config(source_id,name,last_seen) VALUES($p0,'Fixture source','now')", ("$p0", source)); return c.Execute($"INSERT INTO {(variant ? "source_variants" : "source_matches")}(entry_id,source_id,source_manga_id,source_manga_url,source_title,confidence,locked,last_checked) VALUES($p0,$p1,$p2,$p3,$p4,1,$p5,'now')", ("$p0", id), ("$p1", source), ("$p2", manga), ("$p3", "/manga/" + manga), ("$p4", title), ("$p5", locked ? 1 : 0)); });
        }
        public void Chapter(long entry, long chapter, long candidate, string source, long manga)
        { O(o => { o.Execute("INSERT INTO virtual_chapters(id,entry_id,chapter_key,chapter_number,display_name,created_at,updated_at) VALUES($p0,$p1,'n:1',1,'Chapter','now','now')", ("$p0", chapter), ("$p1", entry)); o.Execute("INSERT INTO chapter_candidates(id,virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,name,first_seen,last_seen) VALUES($p0,$p1,$p2,'Fixture',$p3,999,'Chapter','now','now')", ("$p0", candidate), ("$p1", chapter), ("$p2", source), ("$p3", manga)); o.Execute("INSERT INTO candidate_pages VALUES($p0,0,'https://images.invalid/page','now')", ("$p0", candidate)); o.Execute("INSERT INTO served_origin VALUES($p0,$p1,'now','fixture')", ("$p0", chapter), ("$p1", candidate)); o.Execute("INSERT INTO origin_history(virtual_chapter_id,candidate_id,source_id,source_name,source_manga_id,source_chapter_id,served_at,reason) VALUES($p0,$p1,$p2,'Fixture',$p3,999,'now','fixture')", ("$p0", chapter), ("$p1", candidate), ("$p2", source), ("$p3", manga)); return o.Execute("INSERT INTO source_record_scans VALUES($p0,$p1,$p2,'now',1,'')", ("$p0", entry), ("$p1", source), ("$p2", manga)); }); }
        public JsonObject Choice(string kind, long[] entries, (string Source, long Manga)[] records, string[]? targetUids = null)
        {
            var bundle = Exchange.Export()["catalogue"]!.AsObject(); var uid = CatalogueFormat.Uid("decision"); var locators = bundle["entries"]!.AsArray().SelectMany(value => value!["sources"]!.AsArray()).Where(fact => records.Any(record => S(fact!, "source_id") == record.Source && S(fact!, "reference") == PortableIdentity.RecordId(record.Source, "/manga/" + record.Manga))).Select(fact => fact!.DeepClone()).DistinctBy(fact => CatalogueFormat.Digest(fact)).ToArray();
            var targets = targetUids is null ? Database.Write(DatabaseFile.Catalog, c => entries.Select(id => (JsonNode?)JsonValue.Create(CatalogueIdentity.Ensure(c, id))).ToArray()) : targetUids.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray();
            var choice = new JsonObject { ["uid"] = uid, ["kind"] = kind, ["entries"] = new JsonArray(targets), ["records"] = new JsonArray(locators), ["value"] = new JsonObject() }; var digest = CatalogueFormat.Digest(choice); bundle["decisions"]!.AsArray().Add(choice); var preview = Exchange.Preview(Encoding.UTF8.GetBytes(CatalogueFormat.Canonical(bundle))); Exchange.Apply(S(preview, "id"), S(preview, "revision")); return Exchange.Decision(uid, digest);
        }
        public JsonObject Apply(JsonObject choice)
        {
            choice = Exchange.Decision(S(choice, "uid"), S(choice, "digest")); var task = Tasks.Create("Fixture reviewed catalogue choice", origin: "manual", kind: "catalogue-choice");
            try { var result = Exchange.ApplyDecision(S(choice, "uid"), S(choice, "digest"), S(choice, "revision"), taskId: task); if (S(result, "state") is "applied" or "ignored") Tasks.Complete(task); else Tasks.Fail(task, "Choice remains pending"); return result; } catch (Exception error) { Tasks.Fail(task, error.Message); throw; }
        }
        public void Dispose() { Database.Dispose(); _sandbox.Dispose(); }
    }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse(Stamp, CultureInfo.InvariantCulture); }
}
