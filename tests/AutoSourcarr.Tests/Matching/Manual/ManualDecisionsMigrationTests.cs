using System.Globalization;
using AutoSourcarr.Core.Catalogue.Maintenance;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Manual;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Tests.Matching;

/// <summary>
/// Phase 8 "done when … manual decisions survive a migration". The released 0.4.4 fixture (Fixtures/legacy/catalog-0.4.4.sql)
/// has no manual rows, so a copy gets them added the way 0.4.4 wrote them (a manual group of two bound records,
/// a separate-entry rule, a different-works review, a preferred-title override with its scope, a manual alias and
/// locked manual records) before the migrations run. Every decision must still be there and honoured afterwards,
/// survive a normal database reset (and be restored), and only a full reset may remove them.
/// </summary>
public static class ManualDecisionsMigrationTests
{
    private static readonly DecisionActor Owner = DecisionActor.User("owner");

    public static void Register(TestSuite suite)
    {
        suite.Add("manual decisions: a 0.4.4 catalogue keeps every manual decision through the migrations and a normal reset", () =>
        {
            using var temp = new Sandbox();
            Load(Path.Combine(temp.Path, "catalog.sqlite"), "catalog-0.4.4.sql", ManualRows);
            Load(Path.Combine(temp.Path, "chapter-origins.sqlite"), "origins-0.4.4.sql", null);
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();

            // The rows are all still there after the migrations.
            foreach (var (table, expected) in new[] { ("manual_match_groups", 1L), ("manual_match_bindings", 2L), ("manual_separate_records", 1L), ("duplicate_reviews", 1L),
                         ("preferred_title_overrides", 1L), ("manual_title_scopes", 1L) })
                Check.True(ManualFixture.Count(database, "SELECT COUNT(*) FROM " + table) == expected, table + " kept by the migrations");
            Check.Equal(1L, ManualFixture.Count(database, "SELECT COUNT(*) FROM titles WHERE title_type='manual'"));
            Check.Equal(1L, ManualFixture.Count(database, "SELECT manual_preferred_title FROM entries WHERE id=1"));
            Check.Equal(0L, ManualFixture.Count(database, "SELECT COUNT(*) FROM decision_log"));

            // ... and honoured by ManualDecisions.
            var log = new InMemoryDecisionLog();
            var operations = new ManualFakeCatalogueOperations(database);
            var manual = new ManualDecisions(database, new object(), log, operations);
            Check.True(manual.IsManuallyBound("src-a", 101, out var bound) && bound == 1, "Manual binding honoured");
            Check.True(manual.IsManuallyBound("src-b", 201, out _), "Second group member bound");
            Check.True(!manual.IsManuallyBound("src-a", 102, out _), "Automatic record is not bound");
            Check.True(manual.IsSeparated("src-a", 102) && manual.IsSeparatedFrom(1, "src-a", 102), "Separate-entry rule honoured");
            Check.True(manual.IsRejected(2, 1), "Different-works review honoured");
            Check.Equal(0, manual.CaptureExistingManualMatches());
            var groups = manual.ManualGroupsPayload();
            Check.Equal(1, groups["group_count"]!.GetValue<int>());
            Check.Equal(1L, groups["groups"]![0]!["entry_id"]!.GetValue<long>());
            var actions = manual.ManualActionsPayload();
            Check.Equal(2, actions["counts"]!["matches"]!.GetValue<int>());
            Check.Equal(1, actions["counts"]!["titles"]!.GetValue<int>());
            Check.Equal(1, actions["counts"]!["separate_entries"]!.GetValue<int>());
            Check.Equal(1, actions["counts"]!["duplicate_decisions"]!.GetValue<int>());

            // A normal database reset clears the rebuildable catalogue but keeps the durable decisions...
            var maintenance = new CatalogueMaintenance(database, new object(), log, manual);
            maintenance.ClearTables(full: false, Owner);
            Check.Equal(0L, ManualFixture.Count(database, "SELECT COUNT(*) FROM entries"));
            foreach (var table in new[] { "manual_match_groups", "manual_match_bindings", "manual_separate_records", "preferred_title_overrides", "manual_title_scopes" })
                Check.True(ManualFixture.Count(database, "SELECT COUNT(*) FROM " + table) > 0, table + " kept by a normal reset");
            // ... and restores the manual matches locally: both group members on one entry, locked as manual.
            var restored = manual.RestorePersistentManualMatches(Owner);
            Check.Equal(2, restored["restored"]!.GetValue<int>());
            Check.True(manual.IsManuallyBound("src-a", 101, out var anchor) && anchor > 0, "Binding points at the restored entry");
            Check.True(manual.IsManuallyBound("src-b", 201, out var member) && member == anchor, "The group stays together");
            Check.Equal(2L, ManualFixture.Count(database, "SELECT COUNT(*) FROM source_matches WHERE entry_id=" + anchor.ToString(CultureInfo.InvariantCulture) + " AND match_type='manual' AND locked=1 AND active=1"));
            Check.True(manual.IsSeparatedFrom(anchor, "src-a", 102), "The separate rule follows the recreated entry");
            Check.Equal(1, manual.ManualGroupsPayload()["group_count"]!.GetValue<int>());
            Check.True(log.Kinds.Contains("reset.database") && log.Kinds.Contains("manual.restore"), "Reset and restore are logged");

            // Only a full reset forgets them.
            maintenance.ClearTables(full: true, Owner);
            foreach (var table in new[] { "manual_match_groups", "manual_match_bindings", "manual_separate_records", "preferred_title_overrides", "manual_title_scopes", "duplicate_reviews" })
                Check.Equal(0L, ManualFixture.Count(database, "SELECT COUNT(*) FROM " + table));
            Check.True(!manual.IsManuallyBound("src-a", 101, out _), "Full reset forgets manual matches");
            Check.Equal(1L, ManualFixture.Count(database, "SELECT COUNT(*) FROM source_config WHERE source_id='src-a'"));
        });
    }

    /// <summary>Manual decisions the way 0.4.4 stored them, added to a copy of the released fixture before migrating.</summary>
    private const string ManualRows = """
        INSERT INTO source_config(source_id,name,lang,last_seen) VALUES('src-a','Source A','en','2026-01-01'),('src-b','Source B','en','2026-01-01');
        INSERT INTO entries(id,preferred_title,manual_preferred_title,created_at,updated_at) VALUES(3,'Gamma',0,'2026-01-01','2026-01-01');
        UPDATE entries SET preferred_title='Alpha Manual',manual_preferred_title=1 WHERE id=1;
        INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,match_type,locked,active,metadata_json,last_checked) VALUES
          (1,'src-a',101,'Alpha','manual',1,1,'{"title":"Alpha"}','2026-01-01'),
          (1,'src-b',201,'Alpha B','manual',1,1,'{"title":"Alpha B"}','2026-01-01'),
          (2,'src-a',102,'Beta','auto',0,1,'{}','2026-01-01'),
          (3,'src-b',202,'Gamma','auto',0,1,'{}','2026-01-01');
        INSERT INTO manual_match_groups(id,title,created_at,updated_at) VALUES('g1','Alpha','2026-01-01','2026-01-01');
        INSERT INTO manual_match_bindings(source_id,source_manga_id,group_id,entry_id_hint,source_name,source_title,record_json,created_at,updated_at) VALUES
          ('src-a',101,'g1',1,'Source A','Alpha','{"id":101,"title":"Alpha"}','2026-01-01','2026-01-01'),
          ('src-b',201,'g1',1,'Source B','Alpha B','{"id":201,"title":"Alpha B"}','2026-01-02','2026-01-02');
        INSERT INTO manual_separate_records(target_entry_id,source_id,source_manga_id,separate_entry_id_hint,source_title,created_at,updated_at)
          VALUES(1,'src-a',102,2,'Beta','2026-01-01','2026-01-01');
        INSERT INTO duplicate_reviews(left_entry_id,right_entry_id,decision,reviewed_at) VALUES(1,2,'not_duplicate','2026-01-01');
        INSERT INTO preferred_title_overrides(entry_id,title,updated_at) VALUES(1,'Alpha Manual','2026-01-01');
        INSERT INTO manual_title_scopes(entry_id,source_id,source_manga_id,title,captured_at) VALUES(1,'src-a',101,'Alpha Manual','2026-01-01');
        INSERT INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES(1,'Alpha Alias','alpha alias','','manual','',0);
        """;

    private static void Load(string path, string fixture, string? extra)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = File.ReadAllText(ConfigurationTests.Fixture(Path.Combine("legacy", fixture)));
        command.ExecuteNonQuery();
        if (extra is not null)
        {
            command.CommandText = extra;
            command.ExecuteNonQuery();
        }
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA application_id={SchemaMigrator.SchemaApplicationId}; PRAGMA user_version=1;");
        command.ExecuteNonQuery();
    }
}
