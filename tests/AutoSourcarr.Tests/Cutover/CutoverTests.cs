using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Api.Cutover;
using AutoSourcarr.Core.Backup;
using AutoSourcarr.Core.Cutover;
using AutoSourcarr.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Tests.Cutover;

/// <summary>
/// Phase 14 cutover tool: a 0.4.x snapshot (copied folders or a 0.4.5 instance backup) is migrated on a copy and a parity
/// report proves no manual decision, subscription or setting was lost. All data here is synthetic.
/// </summary>
public static class CutoverTests
{
    private const string Passphrase = "synthetic backup passphrase 0451";
    private const string AdminSecret = "synthetic-admin-secret-0451";
    private const string UrlSecret = "synthetic-url-pass-0451";

    public static void Register(TestSuite suite)
    {
        suite.Add("phase 14 cutover: the 0.4.4 legacy fixtures migrate offline with PARITY OK; the input is not modified", () => Run(LegacyFixture).GetAwaiter().GetResult());
        suite.Add("phase 14 cutover: a synthetic 0.4.x set with every manual-decision kind keeps them all (PARITY OK, IMP-9 dry run, no secret in the report)",
            () => Run(Synthetic).GetAwaiter().GetResult());
        suite.Add("phase 14 cutover: a migration that drops a manual row and a subscription is reported as lost (PARITY FAILED)", () => Run(Broken).GetAwaiter().GetResult());
        suite.Add("phase 14 cutover: a 0.4.5 instance backup (ASCBK2) round-trips through the reader and migrates; wrong passphrase and tampering are refused",
            () => Run(BackupRoundTrip).GetAwaiter().GetResult());
        suite.Add("phase 14 cutover: a non-empty output folder, a recently written journal and a wrong layout are refused", () => Run(Refusals).GetAwaiter().GetResult());
    }

    private static async Task Run(Func<Task> test) => await test().ConfigureAwait(false);

    // ---- tests -----------------------------------------------------------------------------------------------------

    private static async Task LegacyFixture()
    {
        using var temp = new Sandbox();
        var input = Path.Combine(temp.Path, "in");
        MakeInstance(input, extraCatalog: null, extraOrigins: null, env: Env(secrets: false));
        var before = Inventory(input);
        var outcome = await LegacyMigrationTool.MigrateAsync(input, Path.Combine(temp.Path, "out"), () => throw new InvalidOperationException("no passphrase for folders"), TextWriter.Null);
        Check.True(outcome.Report.Ok, outcome.Report.Verdict);
        Check.Equal("PARITY OK: 0 lost manual decisions, 0 lost subscriptions", outcome.Report.Verdict);
        Check.True(Inventory(input).SequenceEqual(before), "the input folder is unchanged");
        Check.True(File.Exists(Path.Combine(temp.Path, "out", "parity-report.md")) && File.Exists(Path.Combine(temp.Path, "out", "parity-report.json")));
        Check.True(File.Exists(Path.Combine(outcome.ConfigDirectory, "config.json")), "env converted to config.json");
        Check.True(File.Exists(Path.Combine(outcome.ConfigDirectory, "autosourcarr.env")), "the env file is kept for a rollback");
        using (var migrated = new LegacyDatabase(Path.Combine(outcome.DataDirectory, "catalog.sqlite")))
            Check.True(migrated.HasTable("decision_log") && migrated.HasTable("mixed_entry_findings"), "the 0.5.0 migrations ran");
        Check.True(outcome.Report.Merges.DecisionLogPresent, "IMP-9: the merge log is present");
        Check.True(outcome.Report.Counts.Any(row => row.Table == "entries" && row.Original == 2 && row.Migrated == 2), "row counts compared");
        Check.True(outcome.Report.Notices.Any(line => line.Key == "KOMF_BROWSER_URL" && line.Kind == "dropped"), "the retired Komf link is listed as dropped");
        Check.True(outcome.Report.Notices.Any(line => line.Key == "AUTH_METHOD"), "the sign-in change behind Authelia is listed");
        Check.True(outcome.Report.Run["startup"]?["upgrade"]?["status"]?.GetValue<string>() == "updated", "the update guard ran (automatic backup + migrations)");
    }

    private static async Task Synthetic()
    {
        using var temp = new Sandbox();
        var input = Path.Combine(temp.Path, "in");
        MakeInstance(input, ManualCatalog, ManualOrigins, Env(secrets: true));
        var before = Inventory(input);
        var outcome = await LegacyMigrationTool.MigrateAsync(input, Path.Combine(temp.Path, "out"), () => "", TextWriter.Null);
        var report = outcome.Report;
        Check.True(report.Ok, report.Verdict + "\n" + Describe(report));
        Check.True(Inventory(input).SequenceEqual(before), "the input folder is unchanged");
        // Every kind was actually populated and compared (a kind with zero rows would prove nothing).
        foreach (var result in report.Decisions)
        {
            Check.True(result.Compared, result.Kind.Name + " compared");
            Check.True(result.Original > 0, result.Kind.Name + " has rows in the synthetic 0.4.x set");
            Check.True(result.Migrated >= result.Original, result.Kind.Name + " kept its rows");
        }
        // Subscriptions through merge aliases: subscribed id 4 was merged into entry 1 and still opens it.
        var subbed = report.Decisions.Single(result => result.Kind.Group == ParityGroup.Subscription && result.Kind.Table == "settings");
        Check.Equal(3, subbed.Original);
        // IMP-9 dry run: one manual merge kept, one automatic merge holds, one would not hold under the current rules; nothing applied.
        Check.Equal(3, report.Merges.Merges);
        Check.Equal(1, report.Merges.ManualMerges);
        Check.Equal(1, report.Merges.Hold);
        Check.Equal(1, report.Merges.WouldNotHold);
        using (var migrated = new LegacyDatabase(Path.Combine(outcome.DataDirectory, "catalog.sqlite")))
            Check.Equal(3L, migrated.Count("entry_aliases")); // the recheck never applies a split
        // Secrets are compared by presence only and never appear in the report.
        var md = File.ReadAllText(Path.Combine(temp.Path, "out", "parity-report.md"));
        var json = File.ReadAllText(Path.Combine(temp.Path, "out", "parity-report.json"));
        foreach (var secret in new[] { AdminSecret, UrlSecret, Passphrase, "synthetic-host-secret" })
            Check.True(!md.Contains(secret, StringComparison.Ordinal) && !json.Contains(secret, StringComparison.Ordinal), "a secret value reached the report");
        Check.True(report.Settings.Any(item => item.Key == "ADMIN_TOKEN" && item.Secret && item.Before is null && item.Status.StartsWith("kept", StringComparison.Ordinal)));
        Check.True(report.Settings.Any(item => item.Key == "MAIN_SUWAYOMI_URL" && item.Before!.Contains("***@", StringComparison.Ordinal)), "URL credentials are masked");
        Check.True(md.Contains("entry 6 → 3 Gamma", StringComparison.Ordinal), "the IMP-9 recheck names the work by its title");
    }

    private static async Task Broken()
    {
        using var temp = new Sandbox();
        var input = Path.Combine(temp.Path, "in");
        MakeInstance(input, ManualCatalog, ManualOrigins, Env(secrets: false));
        var outcome = await LegacyMigrationTool.MigrateAsync(input, Path.Combine(temp.Path, "out"), () => "", TextWriter.Null, afterMigration: (database, _) =>
        {
            // A deliberately broken migration: one separate-entry rule, one monitored work and one served origin disappear.
            Execute(Path.Combine(database, "catalog.sqlite"), "DELETE FROM manual_separate_records; DELETE FROM library_tracking WHERE entry_id=1; UPDATE manual_match_groups SET title='renamed';");
            Execute(Path.Combine(database, "chapter-origins.sqlite"), "DELETE FROM served_origin;");
        });
        var report = outcome.Report;
        Check.True(!report.Ok, "a lost decision must fail the parity");
        Check.True(report.Verdict.StartsWith("PARITY FAILED: 3 lost manual decisions, 1 lost subscriptions", StringComparison.Ordinal), report.Verdict);
        Check.True(report.Verdict.Contains("Separate entries", StringComparison.Ordinal) && report.Verdict.Contains("Served chapter origins", StringComparison.Ordinal));
        Check.Equal(1, report.Decisions.Single(result => result.Kind.Table == "manual_match_groups").Changed.Count);
        Check.True(File.ReadAllText(Path.Combine(temp.Path, "out", "parity-report.md")).Contains("PARITY FAILED", StringComparison.Ordinal));
    }

    private static async Task BackupRoundTrip()
    {
        using var temp = new Sandbox();
        var instance = Path.Combine(temp.Path, "instance");
        MakeInstance(instance, ManualCatalog, ManualOrigins, Env(secrets: true));
        var file = Path.Combine(temp.Path, "0451.ascbackup");
        File.WriteAllBytes(file, Protect(Zip(instance), Passphrase));
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));

        // The reader itself: wrong passphrase, a flipped byte and a short passphrase are refused.
        Check.Throws<BackupRefusedException>(() => LegacyBackupReader.Extract(file, "a wrong passphrase 0451", Path.Combine(temp.Path, "x1")));
        var tampered = File.ReadAllBytes(file); tampered[^20] ^= 1;
        Check.Throws<BackupRefusedException>(() => LegacyBackupReader.Decrypt(tampered, Passphrase));
        Check.Throws<BackupRefusedException>(() => LegacyBackupReader.Decrypt(File.ReadAllBytes(file), "short"));
        var manifest = LegacyBackupReader.Extract(file, Passphrase, Path.Combine(temp.Path, "x2"));
        Check.Equal("0.4.5", manifest.Version);
        Check.True(File.Exists(Path.Combine(temp.Path, "x2", "db", "catalog.sqlite")) && File.Exists(Path.Combine(temp.Path, "x2", "config", "autosourcarr.env")));

        var asked = 0;
        var outcome = await LegacyMigrationTool.MigrateAsync(file, Path.Combine(temp.Path, "out"), () => { asked++; return Passphrase; }, TextWriter.Null);
        Check.Equal(1, asked);
        Check.True(outcome.Report.Ok, outcome.Report.Verdict + "\n" + Describe(outcome.Report));
        Check.Equal("0.4.5", outcome.Report.FromVersion);
        Check.True(outcome.Report.Decisions.All(result => result.Original > 0), "every kind came through the backup");
        Check.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
        Check.True(File.Exists(Path.Combine(outcome.DataDirectory, "catalog.sqlite")), "the backup's databases land in the migrated data folder");
        var md = File.ReadAllText(outcome.ReportPath);
        Check.True(!md.Contains(Passphrase, StringComparison.Ordinal) && !md.Contains(AdminSecret, StringComparison.Ordinal));
    }

    private static async Task Refusals()
    {
        using var temp = new Sandbox();
        var input = Path.Combine(temp.Path, "in");
        MakeInstance(input, null, null, Env(secrets: false));
        var output = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "keep.txt"), "x");
        await Refused(() => LegacyMigrationTool.MigrateAsync(input, output, () => "", TextWriter.Null), "not empty");
        await Refused(() => LegacyMigrationTool.MigrateAsync(input, Path.Combine(input, "out"), () => "", TextWriter.Null), "outside");
        var layout = Path.Combine(temp.Path, "layout"); Directory.CreateDirectory(layout);
        await Refused(() => LegacyMigrationTool.MigrateAsync(layout, Path.Combine(temp.Path, "o2"), () => "", TextWriter.Null), "'data' and 'config'");
        File.WriteAllText(Path.Combine(input, "data", "catalog.sqlite-wal"), "recent");
        await Refused(() => LegacyMigrationTool.MigrateAsync(input, Path.Combine(temp.Path, "o3"), () => "", TextWriter.Null), "running instance");
        Check.True(!Directory.Exists(Path.Combine(temp.Path, "o3")), "nothing is written for a refused input");
        Check.Equal(2, await LegacyMigrationTool.RunAsync(["only-one"], TextWriter.Null));
    }

    // ---- synthetic 0.4.x instance ------------------------------------------------------------------------------------

    private static async Task Refused(Func<Task> run, string text)
    {
        try { await run(); }
        catch (MigrationRefusedException error) { Check.True(error.Message.Contains(text, StringComparison.Ordinal), error.Message); return; }
        throw new InvalidOperationException("The tool accepted an input it must refuse (" + text + ").");
    }

    private static string Env(bool secrets)
    {
        var text = File.ReadAllText(ConfigurationTests.Fixture(Path.Combine("env", "autosourcarr.env.example"))).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!secrets) return text;
        text = Regex.Replace(text, "(?m)^ADMIN_TOKEN=.*$", "ADMIN_TOKEN=" + AdminSecret);
        return Regex.Replace(text, "(?m)^MAIN_SUWAYOMI_URL=.*$", "MAIN_SUWAYOMI_URL=http://owner:" + UrlSecret + "@suwayomi:30058");
    }

    private static void MakeInstance(string root, string? extraCatalog, string? extraOrigins, string env)
    {
        Directory.CreateDirectory(Path.Combine(root, "data", "cover-cache"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Load(Path.Combine(root, "data", "catalog.sqlite"), "catalog-0.4.4.sql", extraCatalog);
        Load(Path.Combine(root, "data", "chapter-origins.sqlite"), "origins-0.4.4.sql", extraOrigins);
        File.WriteAllText(Path.Combine(root, "config", "autosourcarr.env"), env);
        File.WriteAllText(Path.Combine(root, "config", "source-host.env"), "SOURCE_PASSWORD=synthetic-host-secret\nKEEP_ME=1\n");
        File.WriteAllText(Path.Combine(root, "data", "cover-cache", "entry-1.webp"), "synthetic cover");
        var old = DateTime.UtcNow.AddHours(-2);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, old);
    }

    private static void Load(string path, string fixture, string? extra)
    {
        using (var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = File.ReadAllText(ConfigurationTests.Fixture(Path.Combine("legacy", fixture)));
            command.ExecuteNonQuery();
            if (extra is not null) { command.CommandText = extra; command.ExecuteNonQuery(); }
            command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA application_id={SchemaMigrator.SchemaApplicationId}; PRAGMA user_version=1;");
            command.ExecuteNonQuery();
        }
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<string> Inventory(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) + ":" + File.GetLastWriteTimeUtc(file).Ticks).ToList();

    private static string Describe(ParityReport report) => string.Join("\n", report.Decisions.SelectMany(result => result.Missing.Concat(result.Changed)
        .Select(item => result.Kind.Name + ": " + item.Key + " " + item.Before + " -> " + item.After)).Concat(report.Settings.Where(item => item.Lost).Select(item => "setting " + item.Key + " " + item.Status)));

    /// <summary>Every manual or user-owned kind, written the way 0.4.4/0.4.5 stored it (entries 1 Alpha and 2 Beta come from the fixture).</summary>
    private const string ManualCatalog = """
        INSERT INTO source_config(source_id,name,lang,last_seen,enabled_search,priority) VALUES('src-a','Source A','en','2026-01-01',1,10),('src-b','Source B','en','2026-01-01',0,20);
        INSERT INTO source_groups(id,name,priority,created_at,updated_at) VALUES(1,'Main',5,'2026-01-01','2026-01-01');
        INSERT INTO entries(id,preferred_title,manual_preferred_title,created_at,updated_at) VALUES(3,'Gamma',0,'2026-01-01','2026-01-01');
        UPDATE entries SET preferred_title='Alpha Manual',manual_preferred_title=1 WHERE id=1;
        INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,match_type,locked,active,metadata_json,last_checked) VALUES
          (1,'src-a',101,'Alpha','manual',1,1,'{"title":"Alpha"}','2026-01-01'),
          (1,'src-b',201,'Alpha B','manual',1,1,'{"title":"Alpha B"}','2026-01-01'),
          (2,'src-a',102,'Beta','auto',0,1,'{}','2026-01-01'),
          (3,'src-b',202,'Gamma','auto',0,1,'{}','2026-01-01');
        INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,match_type,locked,active,last_checked) VALUES(1,'src-a',103,'Alpha Extra','manual',1,1,'2026-01-01');
        INSERT INTO manual_match_groups(id,title,created_at,updated_at) VALUES('g1','Alpha','2026-01-01','2026-01-01');
        INSERT INTO manual_match_bindings(source_id,source_manga_id,group_id,entry_id_hint,source_name,source_title,record_json,created_at,updated_at) VALUES
          ('src-a',101,'g1',1,'Source A','Alpha','{"id":101,"title":"Alpha"}','2026-01-01','2026-01-01'),
          ('src-b',201,'g1',1,'Source B','Alpha B','{"id":201,"title":"Alpha B"}','2026-01-02','2026-01-02');
        INSERT INTO manual_separate_records(target_entry_id,source_id,source_manga_id,separate_entry_id_hint,source_title,created_at,updated_at)
          VALUES(1,'src-a',102,2,'Beta','2026-01-01','2026-01-01');
        INSERT INTO duplicate_reviews(left_entry_id,right_entry_id,decision,reviewed_at) VALUES(1,2,'not_duplicate','2026-01-01');
        INSERT INTO preferred_title_overrides(entry_id,title,updated_at) VALUES(1,'Alpha Manual','2026-01-01');
        INSERT INTO preferred_title_override_sources(source_id,source_manga_id,title,updated_at) VALUES('src-a',101,'Alpha Manual','2026-01-01');
        INSERT INTO manual_title_scopes(entry_id,source_id,source_manga_id,title,captured_at) VALUES(1,'src-a',101,'Alpha Manual','2026-01-01');
        INSERT INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES(1,'Alpha Alias','alpha alias','','manual','',0),
          (2,'Beta Library','beta library','','main-library','',0);
        INSERT INTO entry_aliases(old_entry_id,canonical_entry_id,reason,merged_at) VALUES(4,1,'manual-merge','2026-01-01'),
          (5,2,'automatic duplicate merge:exact titles','2026-01-02'),(6,3,'automatic duplicate merge:summary','2026-01-03');
        INSERT INTO automatic_merge_events(target_entry_id,duplicate_entry_id,target_snapshot_json,duplicate_snapshot_json,reason,merged_at) VALUES
          (2,5,'{"matches":[{"source_id":"src-a","source_manga_id":102,"source_title":"Beta","description":"","author":"","genre":""}]}',
             '{"matches":[{"source_id":"src-c","source_manga_id":502,"source_title":"Beta","description":"","author":"","genre":""}]}','automatic duplicate merge:exact titles','2026-01-02'),
          (3,6,'{"matches":[{"source_id":"src-b","source_manga_id":202,"source_title":"Gamma","description":"A cook travels.","author":"Ann","genre":""}]}',
             '{"matches":[{"source_id":"src-c","source_manga_id":602,"source_title":"Entirely Different Story","description":"Robots fight in space.","author":"Bob","genre":""}]}','automatic duplicate merge:summary','2026-01-03');
        -- The one-time upgrade versions a running 0.4.5 has already stored.
        INSERT INTO settings(key,value) VALUES('startup_catalog_maintenance_version','"0.2.69"'),('automatic_merge_safety_repair_version','"0.2.69"'),
          ('bound_title_consensus_version','"0.2.78"'),('subbed_orphan_alias_repair_version','"0.2.80"');
        INSERT INTO settings(key,value) VALUES('subbed_display_snapshot_v1','{"origin":"http://suwayomi:30058","observed_at":"2026-01-01","payload":{"entry_ids":[1,2,4],"stats_by_entry":{}}}');
        INSERT INTO library_tracking(entry_id,monitored,revision,main_manga_id,observation_json,observed_at,updated_at) VALUES(1,1,1,77,'{}','',''),(2,0,1,78,'{}','','');
        INSERT INTO tags(id,namespace,name,normalized) VALUES(1,'genre','Action','action'),(2,'genre','Fighting','fighting');
        INSERT INTO tag_reviews(left_id,right_id,decision,updated_at) VALUES(1,2,'same','2026-01-01');
        INSERT INTO mixed_entry_reviews(fingerprint,entry_id_hint,reviewed_at) VALUES('fp-1',1,'2026-01-01');
        INSERT INTO identity_links(entry_id,provider,external_id,match_type,locked,last_checked) VALUES(1,'mangadex','md-1','manual',1,'2026-01-01');
        INSERT INTO portable_identity_reviews(entry_id,reference,decision,evidence_sha256,note,updated_at) VALUES(1,'ref-1','same','abc','','2026-01-01');
        INSERT INTO deleted_source_records(source_id,source_manga_id,source_title,deleted_entry_title,deleted_at) VALUES('src-z',9,'Gone','Gone','2026-01-01');
        INSERT INTO catalogue_record_bindings(source_id,source_manga_id,reference,entry_id) VALUES('src-a',101,'ref-1',1);
        INSERT INTO catalogue_decisions(uid,digest,decision_json,state) VALUES('uid-1','digest-1','{}','applied');
        INSERT INTO matching_policies(scope,subject,direction,rule_json,updated_at) VALUES('source','src-a','internal','{"floor":0.97}','2026-01-01');
        INSERT INTO matching_presets(id,name,rule_json,updated_at) VALUES('preset-1','Strict','{"floor":0.98}','2026-01-01');
        INSERT INTO serving_profiles(entry_id,profile_json,revision,updated_at) VALUES(1,'{"prefer":"src-a"}',1,'2026-01-01');
        INSERT INTO serving_upgrade_policies(entry_id,revision,policy_json,updated_at) VALUES(1,1,'{"upgrade":true}','2026-01-01');
        INSERT INTO user_ui_preferences(user_key,values_json,updated_at) VALUES('owner','{"theme":"dark"}','2026-01-01');
        INSERT INTO import_runs(id,filename,state,total,added,created_at) VALUES('imp-1','library.tachibk','completed',2,1,'2026-01-01');
        INSERT INTO import_items(import_id,position,title,state,entry_id,updated_at) VALUES('imp-1',0,'Alpha','added',1,'2026-01-01'),('imp-1',1,'Unknown','unresolved',NULL,'2026-01-01');
        INSERT INTO import_match_rejections(backup_key,signature,backup_title,rejected_title,rejected_at) VALUES('bk-1','sig-1','Alpha','Wrong Alpha','2026-01-01');
        INSERT INTO import_manual_autoresolve(backup_key,target_source_id,target_source_manga_id,created_at,updated_at) VALUES('bk-2','src-a',101,'2026-01-01','2026-01-01');
        INSERT INTO import_manual_autoresolve_state(key,value) VALUES('version','1');
        INSERT INTO serving_choices(virtual_chapter_id,entry_id,candidate_id,revision,changed_at) VALUES(1,1,1,1,'2026-01-01');
        """;

    private const string ManualOrigins = """
        INSERT INTO virtual_chapters(id,entry_id,chapter_key,display_name,hidden,created_at,updated_at) VALUES(1,1,'c1','Chapter 1',0,'2026-01-01','2026-01-01'),
          (2,1,'c2','Chapter 2',1,'2026-01-01','2026-01-01');
        INSERT INTO chapter_candidates(id,virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,name,first_seen,last_seen) VALUES
          (1,1,'src-a','Source A',101,1001,'Chapter 1','2026-01-01','2026-01-01');
        INSERT INTO served_origin(virtual_chapter_id,candidate_id,selected_at,reason) VALUES(1,1,'2026-01-01','manual');
        INSERT INTO origin_history(virtual_chapter_id,candidate_id,source_id,source_name,source_manga_id,source_chapter_id,served_at,reason) VALUES(1,1,'src-a','Source A',101,1001,'2026-01-01','manual');
        INSERT INTO chapter_candidate_rejections(source_id,source_chapter_id,source_manga_id,source_name,name,rejected_at) VALUES('src-b',2002,201,'Source B','Chapter 1','2026-01-01');
        """;

    // ---- the 0.4.5 backup format, re-implemented from instance_backup.py (InstanceBackups.create + protect) ------------

    private static byte[] Zip(string instance)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(instance, "config"), "*", SearchOption.AllDirectories))
            files["config/" + Path.GetRelativePath(Path.Combine(instance, "config"), file).Replace('\\', '/')] = file;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(instance, "data"), "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Path.Combine(instance, "data"), file).Replace('\\', '/');
            if (relative.StartsWith("cover-cache/", StringComparison.Ordinal)) continue; // excluded by 0.4.5
            files[(relative is "catalog.sqlite" or "chapter-origins.sqlite" ? "db/" : "data/") + relative] = file;
        }
        var manifest = new JsonObject
        {
            ["format"] = 2, ["version"] = "0.4.5", ["created_at"] = "2026-11-03T10:00:00+00:00",
            ["paths"] = new JsonObject { ["config"] = "/config", ["data"] = "/data", ["db"] = "/data" },
            ["excluded"] = new JsonArray("cover-cache", "instance-backups"), ["files"] = new JsonObject(), ["directories"] = new JsonObject(),
        };
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, path) in files)
            {
                var bytes = File.ReadAllBytes(path);
                using (var entry = zip.CreateEntry(name, CompressionLevel.Fastest).Open()) entry.Write(bytes);
                manifest["files"]![name] = new JsonObject { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["bytes"] = bytes.Length, ["mode"] = 384, ["uid"] = 568, ["gid"] = 568 };
            }
            using var writer = new StreamWriter(zip.CreateEntry("instance.json").Open(), new UTF8Encoding(false));
            writer.Write(manifest.ToJsonString());
        }
        return buffer.ToArray();
    }

    /// <summary>instance_backup.protect: MAGIC | salt16 | nonce12 | AESGCM(key=PBKDF2-SHA256 600000, aad=MAGIC+salt) incl. tag.</summary>
    private static byte[] Protect(byte[] content, string password)
    {
        var magic = "ASCBK2\n"u8.ToArray();
        var salt = RandomNumberGenerator.GetBytes(16); var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 600000, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[content.Length]; var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, content, cipher, tag, (byte[])[.. magic, .. salt]);
        return [.. magic, .. salt, .. nonce, .. cipher, .. tag];
    }
}
