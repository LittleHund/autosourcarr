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
        suite.Add("phase 14 cutover: every compared kind, damaged in the migrated copy, fails the parity (incl. merged pairs, re-attached separates, merged subscriptions)",
            () => Run(EveryKindDamaged).GetAwaiter().GetResult());
        suite.Add("phase 14 cutover: a gone entry, a dropped table, a missing key file or config.key fail the parity", () => Run(EntriesTablesKeys).GetAwaiter().GetResult());
        suite.Add("phase 14 cutover: an update that ends in safe mode is MIGRATION FAILED (exit 1, do not deploy); a crash leaves MIGRATION-INCOMPLETE and no report",
            () => Run(MigrationFailures).GetAwaiter().GetResult());
        suite.Add("phase 14 cutover: a 0.3.27 catalogue migrates; kinds its schema lacks are reported as not comparable, never a crash", () => Run(OldSchema).GetAwaiter().GetResult());
        suite.Add("phase 14 cutover: DATABASE_DIR layouts (inside, outside, ambiguous) and Compose overrides", () => Run(DatabaseLayouts).GetAwaiter().GetResult());
        suite.Add("phase 14 cutover: URL tokens are masked and Windows device names are refused as backup members", Masking);
        suite.Add("phase 14 cutover: a backup made by the real Python 0.4.5 InstanceBackups.create (committed fixture) migrates with PARITY OK", () => Run(PythonFixture).GetAwaiter().GetResult());
        suite.Add("phase 14 cutover: merges by 0.5.0, relabelled merges, emptied entries, added keep-out rows, NULL vs '', violations and unanswerable kinds fail",
            () => Run(ViolationsAndMerges).GetAwaiter().GetResult());
        suite.Add("phase 14 cutover: a missing or changed repair package, state file, upload or reset hint fails; allowlisted logs do not", () => Run(FilesKeptOrFailed).GetAwaiter().GetResult());
        suite.Add("phase 14 cutover: a one-time step that runs with an unavailable sub-step (no 0.4.x marker) is MIGRATION FAILED", () => Run(MarkerlessData).GetAwaiter().GetResult());
        suite.Add("phase 14 cutover: a write-ahead log that cannot be checkpointed is MIGRATION FAILED", () => Run(BusyCheckpoint).GetAwaiter().GetResult());
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
            Check.True(result.Migrated >= result.Original || result.Kind.Settled is not null, result.Kind.Name + " kept its rows");
        }
        // Subscriptions through merge aliases: subscribed id 4 was merged into entry 1 and still opens it.
        var subbed = report.Decisions.Single(result => result.Kind.Group == ParityGroup.Subscription && result.Kind.Table == "settings");
        Check.Equal(3, subbed.Original);
        // IMP-9 dry run: one manual merge kept, one automatic merge holds, one would not hold under the current rules; nothing applied.
        Check.True(report.Merges.Merges >= 3, "merges rechecked");
        Check.Equal(2, report.Merges.ManualMerges);
        Check.Equal(1, report.Merges.Hold);
        Check.Equal(1, report.Merges.WouldNotHold);
        using (var migrated = new LegacyDatabase(Path.Combine(outcome.DataDirectory, "catalog.sqlite")))
            Check.Equal(4L, migrated.Count("entry_aliases")); // the recheck never applies a split (4 = the 0.4.x merges)
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
        Check.True(report.Verdict.Contains("Separate entries", StringComparison.Ordinal) && report.Verdict.Contains("Served chapter origins", StringComparison.Ordinal), report.Verdict);
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

    /// <summary>How each kind is damaged by deletion in a copy of the migrated files (default: all rows of its table deleted). "O:" = chapter-origins.sqlite.</summary>
    private static readonly Dictionary<string, string> Damage = new(StringComparer.Ordinal)
    {
        ["Subbed (main-library subscriptions)"] = "DELETE FROM settings WHERE key='subbed_display_snapshot_v1'",
        ["Manual titles and aliases"] = "DELETE FROM titles WHERE title_type IN ('manual','main-library')",
        ["Manual preferred titles"] = "UPDATE entries SET manual_preferred_title=0",
        ["Legacy source-keyed title overrides"] = "DELETE FROM preferred_title_override_sources;;DELETE FROM manual_title_scopes",
        ["Manual and locked source records (matches)"] = "UPDATE source_matches SET locked=0,match_type='auto';;UPDATE source_variants SET locked=0,match_type='auto'",
        ["Pending merges (interrupted)"] = "DELETE FROM pending_entry_merges;;DELETE FROM entry_aliases WHERE old_entry_id=7;;INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(7,'Gamma Copy','x','x')",
        ["Pending splits (interrupted)"] = "DELETE FROM pending_entry_splits;;DELETE FROM entries WHERE id=8",
        ["Source choices"] = "UPDATE source_config SET official_manual=1-official_manual",
        ["App state in the settings table"] = "DELETE FROM settings WHERE key='source_health_daily_v1'",
        ["Manual or locked identity links"] = "UPDATE identity_links SET locked=0,match_type='auto'",
        ["Served chapter origins"] = "O:DELETE FROM served_origin",
        ["Chapter candidate rejections"] = "O:DELETE FROM chapter_candidate_rejections",
        ["Origin history"] = "O:DELETE FROM origin_history",
    };

    /// <summary>Value changes for the kinds whose rows are not a plain "SELECT * FROM table" (joined, expanded or chosen columns).</summary>
    private static readonly Dictionary<string, string[]> ValueDamage = new(StringComparer.Ordinal)
    {
        ["Subbed (main-library subscriptions)"] = ["UPDATE settings SET value=replace(value,'[1,2,4]','[1,2,999]') WHERE key='subbed_display_snapshot_v1'"],
        ["Manual preferred titles"] = ["UPDATE entries SET preferred_title=preferred_title||'-x' WHERE manual_preferred_title=1"],
        ["Legacy source-keyed title overrides"] = ["UPDATE manual_title_scopes SET title=title||'-x'"],
        ["Manual and locked source records (matches)"] = [.. new[] { "entry_id=entry_id+1000", "source_manga_url=source_manga_url||'-x'", "source_title=source_title||'-x'", "confidence=confidence+1",
            "match_type=match_type||'-x'", "locked=locked+1000", "active=active+1000" }.Select(set => "UPDATE source_variants SET " + set + " WHERE locked=1")],
        ["Tag reviews"] = ["UPDATE tag_reviews SET decision='different'"],
        ["Manual or locked identity links"] = [.. new[] { "provider_name=provider_name||'-x'", "external_url=external_url||'-x'", "identity_scope=identity_scope||'-x'",
            "confidence=confidence+1", "match_type=match_type||'-x'", "locked=locked+1000" }.Select(set => "UPDATE identity_links SET " + set)],
        ["App state in the settings table"] = ["UPDATE settings SET value=value||'x' WHERE key='source_health_daily_v1'",
            "UPDATE settings SET value='\"0.1\"' WHERE key='startup_catalog_maintenance_version'"],
        ["Served chapter origins"] = ["O:UPDATE served_origin SET reason=reason||'-x'", "O:UPDATE chapter_candidates SET source_chapter_id=9999 WHERE id=1",
            "O:UPDATE chapter_candidates SET source_manga_id=9999 WHERE id=1", "O:UPDATE chapter_candidates SET source_id='src-z' WHERE id=1",
            "O:UPDATE virtual_chapters SET entry_id=2 WHERE id=1", "O:UPDATE virtual_chapters SET chapter_key='zz' WHERE id=1", "O:DELETE FROM virtual_chapters WHERE id=1",
            "O:DELETE FROM chapter_candidates WHERE id=1", "O:UPDATE served_origin SET candidate_id=2"],
        ["Origin history"] = ["O:UPDATE origin_history SET reason=reason||'-x'", "O:UPDATE origin_history SET source_name=source_name||'-x'", "O:UPDATE origin_history SET served_at='x'",
            "O:UPDATE chapter_candidates SET source_chapter_id=9999 WHERE id=1", "O:UPDATE virtual_chapters SET entry_id=2 WHERE id=1"],
        ["Serving choices"] = ["UPDATE serving_choices SET revision=revision+1", "UPDATE serving_choices SET entry_id=entry_id+1000",
            "O:UPDATE chapter_candidates SET source_chapter_id=9999 WHERE id=1", "O:UPDATE virtual_chapters SET chapter_key='zz' WHERE id=1"],
        ["Serving upgrade history"] = ["UPDATE serving_upgrade_history SET changed_at='x'", "O:UPDATE chapter_candidates SET source_chapter_id=9999 WHERE id=1"],
    };

    /// <summary>A generic value change of one column: numbers move by 1000, text gets a suffix, NULL becomes text.</summary>
    private static string Change(string table, string column) =>
        $"UPDATE \"{table}\" SET \"{column}\"=CASE WHEN \"{column}\" IS NULL THEN 'damaged' WHEN typeof(\"{column}\") IN ('integer','real') THEN \"{column}\"+1000 ELSE \"{column}\"||'-damaged' END";

    private static async Task EveryKindDamaged()
    {
        using var temp = new Sandbox();
        var input = Path.Combine(temp.Path, "in");
        MakeInstance(input, ManualCatalog, ManualOrigins, Env(secrets: false));
        var outcome = await LegacyMigrationTool.MigrateAsync(input, Path.Combine(temp.Path, "out"), () => "", TextWriter.Null);
        Check.True(outcome.Report.Ok, outcome.Report.Verdict + "\n" + Describe(outcome.Report));
        var original = Path.Combine(temp.Path, "out", "original-0.4", "data");
        var kinds = outcome.Report.Decisions.Select(result => result.Kind).ToList();
        var deletions = 0; var changes = 0;
        foreach (var result in outcome.Report.Decisions)
        {
            var kind = result.Kind;
            // Deletion of the kind's rows.
            var sql = Damage.TryGetValue(kind.Name, out var special) ? special : (kind.Origins ? "O:" : "") + "DELETE FROM " + kind.Table;
            var damaged = Analyze(temp.Path, original, outcome.DataDirectory, sql, [kind]).Kinds.Single();
            Check.True(damaged.Lost > 0, kind.Name + " was damaged (" + sql + ") but not reported as lost");
            deletions++;
            // A value change of every compared column, one at a time.
            if (result.Migrated == 0) continue; // an interrupted operation the migration completed (nothing left to change)
            string[] statements;
            if (ValueDamage.TryGetValue(kind.Name, out var specific)) statements = specific;
            else
            {
                Check.True(kind.Sql.StartsWith("SELECT * FROM", StringComparison.Ordinal), kind.Name + " needs a ValueDamage entry");
                using var schema = new LegacyDatabase(Path.Combine(outcome.DataDirectory, kind.Origins ? "chapter-origins.sqlite" : "catalog.sqlite"));
                var tableColumns = schema.Schema(kind.Table).Columns;
                statements = [.. result.Columns.Where(tableColumns.Contains).Select(column => (kind.Origins ? "O:" : "") + Change(kind.Table, column))];
                Check.True(statements.Length > 0 || result.Columns.Count == 0, kind.Name + " has columns to change");
            }
            foreach (var statement in statements)
            {
                var changed = Analyze(temp.Path, original, outcome.DataDirectory, statement, [kind]).Kinds.Single();
                Check.True(changed.Lost > 0, kind.Name + ": the value change (" + statement + ") was not reported");
                changes++;
            }
        }
        Check.True(deletions >= 55 && changes >= 150, "every kind was damaged (" + deletions.ToString(CultureInfo.InvariantCulture) + " deletions, "
            + changes.ToString(CultureInfo.InvariantCulture) + " value changes)");
        var clean = Analyze(temp.Path, original, outcome.DataDirectory, "SELECT 1", kinds);
        Check.True(clean.Ok, "the undamaged copy compares clean");
    }

    private static async Task ViolationsAndMerges()
    {
        using var temp = new Sandbox();
        var input = Path.Combine(temp.Path, "in");
        MakeInstance(input, ManualCatalog, ManualOrigins, Env(secrets: false));
        var outcome = await LegacyMigrationTool.MigrateAsync(input, Path.Combine(temp.Path, "out"), () => "", TextWriter.Null);
        var original = Path.Combine(temp.Path, "out", "original-0.4", "data");
        var kinds = outcome.Report.Decisions.Select(result => result.Kind).ToList();
        ParityKindResult Kind(DamageResult result, string table) => result.Kinds.First(item => item.Kind.Table == table);

        // (1) a merge 0.5.0 made of a subscribed and tracked entry (2 into 3): the merge itself fails, and so do the rows that point at entry 2.
        var merged = Analyze(temp.Path, original, outcome.DataDirectory,
            "INSERT INTO entry_aliases(old_entry_id,canonical_entry_id,reason,merged_at) VALUES(2,3,'automatic duplicate merge:x','2026-02-01');;DELETE FROM entries WHERE id=2", kinds);
        Check.True(!merged.Ok && merged.Merges.Any(merge => merge.Entry == 2 && merge.Into == 3 && merge.Fails), "a merge by 0.5.0 of a subscribed entry fails");
        Check.True(merged.Kinds.First(item => item.Kind.Name.StartsWith("Subbed", StringComparison.Ordinal)).Lost > 0, "the subscription now opens another entry");
        Check.True(Kind(merged, "library_tracking").Lost > 0, "the tracked work now belongs to another entry");
        Check.True(merged.Unresolved.Count == 0, "a merged entry is listed as merged, not as gone");
        // ... an untouched entry merged by 0.5.0 is listed, not failed.
        var quiet = Analyze(temp.Path, original, outcome.DataDirectory,
            "INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(50,'Lonely','x','x')", kinds);
        Check.True(quiet.Ok, "an entry 0.5.0 added is not a loss");
        // (4) a manual merge relabelled automatic.
        var relabel = Analyze(temp.Path, original, outcome.DataDirectory, "UPDATE entry_aliases SET reason='automatic duplicate merge:x' WHERE old_entry_id=4", kinds);
        Check.True(Kind(relabel, "entry_aliases").Changed.Count > 0, "a manual merge relabelled automatic is changed");
        // (6) an entry that lost every active record.
        var emptied = Analyze(temp.Path, original, outcome.DataDirectory, "UPDATE source_matches SET active=0 WHERE entry_id=3;;UPDATE source_variants SET active=0 WHERE entry_id=3", kinds);
        Check.True(!emptied.Ok && emptied.Emptied.Contains(3), "an entry without records fails");
        // (8) rows added to kinds that keep records out.
        foreach (var (table, sql) in new[]
                 {
                     ("deleted_source_records", "INSERT INTO deleted_source_records(source_id,source_manga_id,deleted_at) VALUES('src-a',101,'x')"),
                     ("manual_separate_records", "INSERT INTO manual_separate_records(target_entry_id,source_id,source_manga_id,created_at,updated_at) VALUES(3,'src-a',101,'x','x')"),
                     ("duplicate_reviews", "INSERT INTO duplicate_reviews(left_entry_id,right_entry_id,decision,reviewed_at) VALUES(1,3,'not_duplicate','x')"),
                     ("import_match_rejections", "INSERT INTO import_match_rejections(backup_key,signature,rejected_at) VALUES('bk-9','sig-9','x')"),
                     ("chapter_candidate_rejections", "O:INSERT INTO chapter_candidate_rejections(source_id,source_chapter_id,rejected_at) VALUES('src-a',1001,'x')"),
                 })
        {
            var added = Analyze(temp.Path, original, outcome.DataDirectory, sql, kinds);
            Check.True(!added.Ok && Kind(added, table).Added.Count > 0, table + ": an added keep-out row fails");
        }
        // (9) NULL and '' stay distinct.
        var blank = Analyze(temp.Path, original, outcome.DataDirectory, "UPDATE import_items SET entry_id='' WHERE entry_id IS NULL", kinds);
        Check.True(Kind(blank, "import_items").Changed.Count > 0, "NULL changed to '' is a change");
        // Violations that keep every row.
        var pair = Analyze(temp.Path, original, outcome.DataDirectory,
            "INSERT INTO entry_aliases(old_entry_id,canonical_entry_id,reason,merged_at) VALUES(2,1,'automatic duplicate merge:x','2026-02-01');;DELETE FROM entries WHERE id=2;;DELETE FROM library_tracking WHERE entry_id=2", kinds);
        Check.True(Kind(pair, "duplicate_reviews").Changed.Any(item => item.After!.StartsWith("VIOLATED", StringComparison.Ordinal)), "a merged different-works pair is violated");
        var attached = Analyze(temp.Path, original, outcome.DataDirectory,
            "UPDATE source_matches SET active=0 WHERE source_id='src-a' AND source_manga_id=102;;INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,match_type,locked,active,last_checked) VALUES(1,'src-a',102,'Beta','auto',0,1,'x')", kinds);
        Check.True(Kind(attached, "manual_separate_records").Changed.Any(item => item.After!.StartsWith("VIOLATED", StringComparison.Ordinal)), "a separated record attached again is violated");
        // (10) a kind the schema cannot answer while its table has rows is not OK.
        using (var oldCatalog = new LegacyDatabase(Path.Combine(original, "catalog.sqlite")))
        using (var oldOrigins = new LegacyDatabase(Path.Combine(original, "chapter-origins.sqlite")))
        {
            var broken = new ParityKind("Broken query", ParityGroup.Manual, false, "manual_match_groups", "SELECT no_such_column FROM manual_match_groups", ["id"], [], "test");
            var result = DecisionParity.Compare((oldCatalog, oldOrigins), (oldCatalog, oldOrigins), [broken]).Single();
            Check.True(!result.Compared && result.Lost > 0, "rows that cannot be compared are not OK");
            var empty = new ParityKind("Broken empty", ParityGroup.Manual, false, "gallery_layout_candidates", "SELECT no_such_column FROM gallery_layout_candidates", ["split_id"], [], "test");
            Check.True(DecisionParity.Compare((oldCatalog, oldOrigins), (oldCatalog, oldOrigins), [empty]).Single().Lost == 0, "an empty table with an older schema is fine");
        }
    }

    private sealed record DamageResult(List<ParityKindResult> Kinds, List<long> Unresolved, List<NewMerge> Merges, List<long> Emptied, bool Ok);

    /// <summary>Copies the migrated databases, applies <paramref name="statements"/> (";;"-separated, "O:" = origins) and compares (the coordinator's harness).</summary>
    private static DamageResult Analyze(string root, string original, string migrated, string statements, IReadOnlyList<ParityKind> kinds)
    {
        var copy = Path.Combine(root, "damage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        foreach (var name in new[] { "catalog.sqlite", "chapter-origins.sqlite" }) File.Copy(Path.Combine(migrated, name), Path.Combine(copy, name));
        foreach (var statement in statements.Split(";;"))
        {
            var origins = statement.StartsWith("O:", StringComparison.Ordinal);
            Execute(Path.Combine(copy, origins ? "chapter-origins.sqlite" : "catalog.sqlite"), origins || statement.StartsWith("C:", StringComparison.Ordinal) ? statement[2..] : statement);
        }
        using var oldCatalog = new LegacyDatabase(Path.Combine(original, "catalog.sqlite"));
        using var oldOrigins = new LegacyDatabase(Path.Combine(original, "chapter-origins.sqlite"));
        using var newCatalog = new LegacyDatabase(Path.Combine(copy, "catalog.sqlite"));
        using var newOrigins = new LegacyDatabase(Path.Combine(copy, "chapter-origins.sqlite"));
        var results = DecisionParity.Compare((oldCatalog, oldOrigins), (newCatalog, newOrigins), kinds);
        var merges = DecisionParity.NewMerges(oldCatalog, newCatalog, DecisionParity.DecisionEntries(oldCatalog, oldOrigins, kinds), new HashSet<long>());
        var unresolved = DecisionParity.UnresolvedEntries(oldCatalog, newCatalog);
        var emptied = DecisionParity.EmptiedEntries(oldCatalog, newCatalog);
        var dropped = DecisionParity.DroppedTables(oldCatalog, newCatalog, "catalog").Concat(DecisionParity.DroppedTables(oldOrigins, newOrigins, "origins")).ToList();
        return new(results, unresolved, merges, emptied, results.All(item => item.Lost == 0) && unresolved.Count == 0 && emptied.Count == 0 && dropped.Count == 0 && !merges.Any(merge => merge.Fails));
    }

    /// <summary>The state files of a 0.4.x instance the migration must carry over byte for byte (synthetic contents).</summary>
    private static readonly string[] StateFiles =
    [
        "data/source-maintenance/archives/eu.kanade.tachiyomi.extension.en.example/last-working.jar", "data/source-maintenance/orphans.json",
        "config/source-maintenance.json", "config/source-browser.json", "config/repair-connection.json", "config/setup-state.json",
        "data/imports/imp-1/library.tachibk", "data/reset-rebuild-hints.json",
    ];

    private static async Task FilesKeptOrFailed()
    {
        using var temp = new Sandbox();
        var input = Path.Combine(temp.Path, "in");
        MakeInstance(input, ManualCatalog, ManualOrigins, Env(secrets: false));
        var old = DateTime.UtcNow.AddHours(-2);
        foreach (var file in StateFiles.Append("data/diagnostics/support.jsonl"))
        {
            var path = Path.Combine(input, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.EndsWith(".json", StringComparison.Ordinal) ? "{\"synthetic\":true}" : "synthetic " + file);
            File.SetLastWriteTimeUtc(path, old);
        }
        var clean = await LegacyMigrationTool.MigrateAsync(input, Path.Combine(temp.Path, "clean"), () => "", TextWriter.Null);
        Check.True(clean.Report.Ok, clean.Report.Verdict + "\n" + string.Join("\n", clean.Report.Files.Lost.Select(item => item.Path + " " + item.Status)));
        foreach (var (index, file) in StateFiles.Select((file, index) => (index, file)))
        {
            var removed = index % 2 == 0;
            var outcome = await LegacyMigrationTool.MigrateAsync(input, Path.Combine(temp.Path, "o" + index.ToString(CultureInfo.InvariantCulture)), () => "", TextWriter.Null,
                afterMigration: (_, config) =>
                {
                    var target = Path.Combine(Path.GetDirectoryName(config)!, file.Replace('/', Path.DirectorySeparatorChar));
                    if (removed) File.Delete(target); else File.AppendAllText(target, "changed");
                });
            Check.True(!outcome.Report.Ok && outcome.Report.Files.Lost.Any(item => item.Path == file), file + (removed ? " removed" : " changed") + " must fail: " + outcome.Report.Verdict);
        }
        var logs = await LegacyMigrationTool.MigrateAsync(input, Path.Combine(temp.Path, "logs"), () => "", TextWriter.Null,
            afterMigration: (_, config) => File.AppendAllText(Path.Combine(Path.GetDirectoryName(config)!, "data", "diagnostics", "support.jsonl"), "more"));
        Check.True(logs.Report.Ok && logs.Report.Files.Allowed.Any(item => item.Path == "data/diagnostics/support.jsonl"), "an allowlisted log may change");
    }

    private static async Task MarkerlessData()
    {
        using var temp = new Sandbox();
        var input = Path.Combine(temp.Path, "in");
        MakeInstance(input, null, null, Env(secrets: false), markers: false);
        var outcome = await LegacyMigrationTool.MigrateAsync(input, Path.Combine(temp.Path, "out"), () => "", TextWriter.Null);
        Check.True(outcome.Report.Verdict.StartsWith("MIGRATION FAILED", StringComparison.Ordinal) && outcome.Report.Verdict.Contains("unavailable", StringComparison.Ordinal), outcome.Report.Verdict);
    }

    private static async Task BusyCheckpoint()
    {
        using var temp = new Sandbox();
        var input = Path.Combine(temp.Path, "in");
        MakeInstance(input, null, null, Env(secrets: false));
        SqliteConnection? reader = null;
        try
        {
            var outcome = await LegacyMigrationTool.MigrateAsync(input, Path.Combine(temp.Path, "out"), () => "", TextWriter.Null,
                afterStartup: _ =>
                {
                    // A reader keeps its snapshot open while a writer appends: the checkpoint cannot reset the log.
                    var catalog = Path.Combine(temp.Path, "out", "data", "catalog.sqlite");
                    reader = new SqliteConnection("Data Source=" + catalog + ";Pooling=False");
                    reader.Open();
                    using (var begin = reader.CreateCommand()) { begin.CommandText = "BEGIN; SELECT COUNT(*) FROM entries;"; begin.ExecuteScalar(); }
                    Execute(catalog, "INSERT INTO settings(key,value) VALUES('wal-probe','1')");
                },
                afterMigration: (_, _) => { reader?.Dispose(); reader = null; });
            Check.True(outcome.Report.Verdict.StartsWith("MIGRATION FAILED", StringComparison.Ordinal) && outcome.Report.Verdict.Contains("write-ahead log", StringComparison.Ordinal), outcome.Report.Verdict);
        }
        finally { reader?.Dispose(); }
    }

    private static async Task EntriesTablesKeys()
    {
        using var temp = new Sandbox();
        var input = Path.Combine(temp.Path, "in");
        MakeInstance(input, ManualCatalog, ManualOrigins, Env(secrets: false));
        var outcome = await LegacyMigrationTool.MigrateAsync(input, Path.Combine(temp.Path, "out"), () => "", TextWriter.Null, afterMigration: (database, config) =>
        {
            Execute(Path.Combine(database, "catalog.sqlite"), "DELETE FROM entries WHERE id=3; DROP TABLE general_task_history;");
            File.Delete(Path.Combine(config, "signing", "reader-extension.keystore"));
            File.Delete(Path.Combine(config, "config.key"));
        });
        var report = outcome.Report;
        Check.True(!report.Ok, "must fail");
        Check.True(report.UnresolvedEntries.Contains(3), "entry 3 is gone");
        Check.True(report.DroppedTables.Contains("catalog.general_task_history"), "the dropped table is listed");
        Check.True(report.MissingKeyFiles == 1, "the missing keystore is counted");
        Check.True(report.Verdict.StartsWith("MIGRATION FAILED", StringComparison.Ordinal) && report.Verdict.Contains("config.key", StringComparison.Ordinal), report.Verdict);
    }

    private static async Task MigrationFailures()
    {
        using var temp = new Sandbox();
        var input = Path.Combine(temp.Path, "in");
        MakeInstance(input, ManualCatalog, ManualOrigins, Env(secrets: false));
        // An update marker that cannot be read: 0.5.0 starts in its minimal safe mode and migrates nothing.
        Directory.CreateDirectory(Path.Combine(input, "data", "instance-backups"));
        var marker = Path.Combine(input, "data", "instance-backups", "rolled-back.json");
        File.WriteAllText(marker, "{ not json");
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddHours(-2));
        using var console = new StringWriter();
        var exit = await LegacyMigrationTool.RunAsync([input, Path.Combine(temp.Path, "out")], console);
        var text = console.ToString();
        Check.Equal(1, exit);
        Check.True(text.Contains("MIGRATION FAILED", StringComparison.Ordinal) && text.Contains("safe mode", StringComparison.Ordinal) && text.Contains("DO NOT DEPLOY", StringComparison.Ordinal), text);
        Check.True(!text.Contains("PARITY OK", StringComparison.Ordinal) && !text.Contains("Mount at /data", StringComparison.Ordinal), text);
        Check.True(File.ReadAllText(Path.Combine(temp.Path, "out", "parity-report.md")).Contains("**MIGRATION FAILED", StringComparison.Ordinal), "the report says MIGRATION FAILED");

        // A crash half-way: no report, a marker, the exception surfaces (exit 3 through RunAsync).
        var crashed = Path.Combine(temp.Path, "crash");
        var input2 = Path.Combine(temp.Path, "in2");
        MakeInstance(input2, null, null, Env(secrets: false));
        Exception? failure = null;
        try { await LegacyMigrationTool.MigrateAsync(input2, crashed, () => "", TextWriter.Null, afterStartup: _ => throw new InvalidDataException("synthetic crash")); }
        catch (InvalidDataException error) { failure = error; }
        Check.True(failure is not null, "the crash surfaces");
        Check.True(File.Exists(Path.Combine(crashed, LegacyMigrationTool.IncompleteMarker)), "the output is marked incomplete");
        Check.True(!File.Exists(Path.Combine(crashed, "parity-report.md")) && !File.Exists(Path.Combine(crashed, "parity-report.json")), "no report for an incomplete run");
    }

    private static async Task OldSchema()
    {
        using var temp = new Sandbox();
        var input = Path.Combine(temp.Path, "in");
        MakeInstance(input, null, null, Env(secrets: false), version: "0.3.27");
        var outcome = await LegacyMigrationTool.MigrateAsync(input, Path.Combine(temp.Path, "out"), () => "", TextWriter.Null);
        Check.True(outcome.Report.Ok, outcome.Report.Verdict + "\n" + Describe(outcome.Report));
        Check.True(outcome.Report.Decisions.Any(result => !result.Compared && result.Note is { Length: > 0 }), "kinds a 0.3.27 schema lacks are named");
    }

    private static async Task DatabaseLayouts()
    {
        using var temp = new Sandbox();
        // DATABASE_DIR inside DATA_DIR (/data/db) through a Compose override.
        var inside = Path.Combine(temp.Path, "inside");
        MakeInstance(inside, null, null, Env(secrets: false));
        Directory.CreateDirectory(Path.Combine(inside, "data", "db"));
        foreach (var name in new[] { "catalog.sqlite", "chapter-origins.sqlite" }) File.Move(Path.Combine(inside, "data", name), Path.Combine(inside, "data", "db", name));
        var compose = Path.Combine(temp.Path, "compose.yml");
        File.WriteAllText(compose, "services:\n  autosourcarr:\n    image: x\n    environment:\n      TZ: Europe/Amsterdam\n      DATABASE_DIR: /data/db\n    volumes:\n      - /a:/data\n"
            + "  private-autosourcarr:\n    environment:\n      - DATABASE_DIR=/data/private-db\n");
        await Refused(() => LegacyMigrationTool.MigrateAsync(inside, Path.Combine(temp.Path, "o1"), () => "", TextWriter.Null, options: new MigrationOptions(compose)), "--service");
        var outcome = await LegacyMigrationTool.MigrateAsync(inside, Path.Combine(temp.Path, "o2"), () => "", TextWriter.Null, options: new MigrationOptions(compose, "autosourcarr"));
        Check.True(outcome.Report.Ok, outcome.Report.Verdict);
        Check.True(File.Exists(Path.Combine(outcome.DataDirectory, "db", "catalog.sqlite")), "the databases stay in data/db");
        Check.True(outcome.Report.Run["paths"]!["source"]!.GetValue<string>().Contains("compose.yml", StringComparison.Ordinal), "the report names where DATABASE_DIR came from");
        Check.True(outcome.Report.Settings.Any(item => item.File == "compose environment" && item.Key == "TZ" && item.Status.StartsWith("Compose override", StringComparison.Ordinal)),
            "the Compose environment of the chosen service is compared");
        // Without the Compose file the databases are not where the env file says: refused, not guessed.
        await Refused(() => LegacyMigrationTool.MigrateAsync(inside, Path.Combine(temp.Path, "o3"), () => "", TextWriter.Null), "was not found");

        // DATABASE_DIR outside DATA_DIR: needs database/, and refuses databases in both places.
        var outside = Path.Combine(temp.Path, "outside");
        MakeInstance(outside, null, null, Env(secrets: false).Replace("DATABASE_DIR=/data", "DATABASE_DIR=/db", StringComparison.Ordinal));
        await Refused(() => LegacyMigrationTool.MigrateAsync(outside, Path.Combine(temp.Path, "o4"), () => "", TextWriter.Null), "copy that folder");
        Directory.CreateDirectory(Path.Combine(outside, "database"));
        foreach (var name in new[] { "catalog.sqlite", "chapter-origins.sqlite" }) File.Copy(Path.Combine(outside, "data", name), Path.Combine(outside, "database", name));
        await Refused(() => LegacyMigrationTool.MigrateAsync(outside, Path.Combine(temp.Path, "o5"), () => "", TextWriter.Null), "Both 'data' and 'database'");
        foreach (var name in new[] { "catalog.sqlite", "chapter-origins.sqlite" }) File.Delete(Path.Combine(outside, "data", name));
        var moved = await LegacyMigrationTool.MigrateAsync(outside, Path.Combine(temp.Path, "o6"), () => "", TextWriter.Null);
        Check.True(moved.Report.Ok, moved.Report.Verdict);
        Check.True(moved.DatabaseDirectory is { } database && File.Exists(Path.Combine(database, "catalog.sqlite")), "the databases go to <out>/database");
        Check.True(moved.Report.Warnings.Any(text => text.Contains("mount", StringComparison.Ordinal)), "the report says where to mount it");
    }

    /// <summary>scripts/make-legacy-backup-fixture.py (public test passphrase, synthetic data).</summary>
    private const string FixturePassphrase = "cutover fixture passphrase 0451";

    private static async Task PythonFixture()
    {
        using var temp = new Sandbox();
        var file = ConfigurationTests.Fixture(Path.Combine("legacy", "instance-0.4.5.ascbackup"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        var outcome = await LegacyMigrationTool.MigrateAsync(file, Path.Combine(temp.Path, "out"), () => FixturePassphrase, TextWriter.Null);
        var report = outcome.Report;
        Check.True(report.Ok, report.Verdict + Environment.NewLine + Describe(report));
        Check.Equal("0.4.5", report.FromVersion);
        foreach (var table in new[] { "manual_separate_records", "duplicate_reviews", "library_tracking", "source_matches" })
            Check.True(report.Decisions.First(result => result.Kind.Table == table).Original > 0, table + " came through the Python backup");
        Check.Equal(2, report.Decisions.Single(result => result.Kind.Name.StartsWith("Subbed", StringComparison.Ordinal)).Original);
        Check.True(report.Settings.Any(item => item.Key == "KOMF_BROWSER_URL" && item.Status.StartsWith("dropped", StringComparison.Ordinal)));
        Check.True(!File.ReadAllText(outcome.ReportPath).Contains("fixture-not-a-secret-0451", StringComparison.Ordinal), "the token value is not in the report");
        Check.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
    }

    private static void Masking()
    {
        foreach (var (value, hidden) in new[]
                 {
                     ("http://owner:pw-0451@host:30058/x", "pw-0451"), ("https://api.example/v1?apikey=abc0451def&x=1", "abc0451def"),
                     ("https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789", "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789"),
                     ("https://hooks.example/notify#token=zz0451", "zz0451"),
                 })
        {
            var masked = SettingsParity.Mask(value);
            Check.True(!masked.Contains(hidden, StringComparison.Ordinal), value + " -> " + masked);
        }
        Check.Equal("http://suwayomi:30058", SettingsParity.Mask("http://suwayomi:30058"));
        Check.Equal("/data/library", SettingsParity.Mask("/data/library"));
        foreach (var name in new[] { "CON", "nul.txt", "COM1", "lpt9.json", "aux", "file.", "a?b" }) Check.True(LegacyBackupReader.WindowsUnsafe(name), name);
        foreach (var name in new[] { "config.json", "console.log", "COM10", "catalog.sqlite" }) Check.True(!LegacyBackupReader.WindowsUnsafe(name), name);
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

    /// <summary>The one-time upgrade markers every running 0.4.5 has stored.</summary>
    private const string Markers = """
        INSERT OR REPLACE INTO settings(key,value) VALUES('startup_catalog_maintenance_version','"0.2.69"'),('automatic_merge_safety_repair_version','"0.2.69"'),
          ('bound_title_consensus_version','"0.2.78"'),('subbed_orphan_alias_repair_version','"0.2.80"');
        """;

    private static void MakeInstance(string root, string? extraCatalog, string? extraOrigins, string env, string version = "0.4.4", bool markers = true)
    {
        Directory.CreateDirectory(Path.Combine(root, "data", "cover-cache"));
        Directory.CreateDirectory(Path.Combine(root, "config", "signing"));
        Load(Path.Combine(root, "data", "catalog.sqlite"), "catalog-" + version + ".sql", (extraCatalog ?? "") + (markers ? Markers : ""));
        Load(Path.Combine(root, "data", "chapter-origins.sqlite"), "origins-" + version + ".sql", extraOrigins);
        File.WriteAllText(Path.Combine(root, "config", "signing", "reader-extension.keystore"), "synthetic keystore bytes");
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
        using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<string> Inventory(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) + ":" + File.GetLastWriteTimeUtc(file).Ticks).ToList();

    private static string Describe(ParityReport report) => string.Join("\n", report.Decisions.SelectMany(result => result.Missing.Concat(result.Changed)
        .Select(item => result.Kind.Name + ": " + item.Key + " " + item.Before + " -> " + item.After)).Concat(report.Settings.Where(item => item.Lost).Select(item => "setting " + item.Key + " " + item.Status)).Concat(report.Warnings));

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
        INSERT INTO serving_profiles(entry_id,profile_json,revision,updated_at) VALUES(1,'{}',1,'2026-01-01');
        INSERT INTO serving_upgrade_policies(entry_id,revision,policy_json,updated_at) VALUES(1,1,'{"upgrade":true}','2026-01-01');
        INSERT INTO user_ui_preferences(user_key,values_json,updated_at) VALUES('owner','{"theme":"dark"}','2026-01-01');
        INSERT INTO import_runs(id,filename,state,total,added,created_at) VALUES('imp-1','library.tachibk','completed',2,1,'2026-01-01');
        INSERT INTO import_items(import_id,position,title,state,entry_id,updated_at) VALUES('imp-1',0,'Alpha','added',1,'2026-01-01'),('imp-1',1,'Unknown','unresolved',NULL,'2026-01-01');
        INSERT INTO import_match_rejections(backup_key,signature,backup_title,rejected_title,rejected_at) VALUES('bk-1','sig-1','Alpha','Wrong Alpha','2026-01-01');
        INSERT INTO import_manual_autoresolve(backup_key,target_source_id,target_source_manga_id,created_at,updated_at) VALUES('bk-2','src-a',101,'2026-01-01','2026-01-01');
        INSERT INTO import_manual_autoresolve_state(key,value) VALUES('version','1');
        INSERT INTO serving_choices(virtual_chapter_id,entry_id,candidate_id,revision,changed_at) VALUES(1,1,1,1,'2026-01-01');
        UPDATE import_items SET reference_entry_id=2,suggestion_state='accepted' WHERE position=1;
        UPDATE source_config SET official_manual=1 WHERE source_id='src-a';
        -- Interrupted chapter work the way 0.4.x journals it: the catalogue merge/split is done, the chapter move is pending.
        INSERT INTO entry_aliases(old_entry_id,canonical_entry_id,reason,merged_at) VALUES(7,3,'manual-merge','2026-01-04');
        INSERT INTO pending_entry_merges(duplicate_entry_id,target_entry_id,created_at) VALUES(7,3,'2026-01-04');
        INSERT INTO entries(id,preferred_title,manual_preferred_title,created_at,updated_at) VALUES(8,'Alpha Split',0,'2026-01-01','2026-01-01');
        INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,match_type,locked,active,metadata_json,last_checked) VALUES(8,'src-c',801,'Alpha Split','split',0,1,'{}','2026-01-01');
        INSERT INTO pending_entry_splits(id,original_entry_id,new_entry_id,records_json,created_at) VALUES(1,1,8,'[["src-c",801]]','2026-01-04');
        INSERT INTO display_metadata(entry_id,revision,fields_json,updated_at) VALUES(1,1,'{"title":{"value":"Alpha Manual","locked":true}}','2026-01-01');
        INSERT INTO display_metadata_history(entry_id,old_entry_id,fields_json,reason,created_at) VALUES(1,1,'{}','manual','2026-01-01');
        INSERT INTO gallery_policies(source_id,revision,policy_json,updated_at) VALUES('src-a',1,'{"profile":"manual"}','2026-01-01');
        INSERT INTO matching_review_actions(id,action_json,created_at) VALUES('act-1','{"kind":"merge"}','2026-01-01');
        INSERT INTO settings(key,value) VALUES('source_health_daily_v1','{"2026-01-01":{"src-a":"ok"}}'),('source_human_attention_v1','{"src-b":true}');
        INSERT INTO duplicate_review_history(left_entry_id,right_entry_id,decision,reviewed_at) VALUES(1,2,'not_duplicate','2026-01-01');
        INSERT INTO duplicate_split_history(original_entry_id,new_entry_id,split_at) VALUES(1,3,'2026-01-01');
        INSERT INTO match_history(entry_id,source_id,new_source_manga_id,change_type,changed_at) VALUES(1,'src-a',101,'manual','2026-01-01');
        INSERT INTO entry_preference_history(canonical_entry_id,old_entry_id,title,profile_json,tracking_json,saved_at) VALUES(1,4,'Alpha','{}','{}','2026-01-01');
        INSERT INTO portable_identity_review_history(entry_id,old_entry_id,reference,decision,evidence_sha256,note,reason,created_at) VALUES(1,1,'ref-1','same','abc','','manual','2026-01-01');
        INSERT INTO serving_policy_history(entry_id,old_entry_id,policy_json,reason,created_at) VALUES(1,1,'{}','manual','2026-01-01');
        INSERT INTO serving_upgrade_history(entry_id,virtual_chapter_id,old_candidate_id,candidate_id,changed_at) VALUES(1,1,1,1,'2026-01-01');
        INSERT INTO deleted_entries_history(old_entry_id,preferred_title,source_records,deleted_at) VALUES(9,'Gone',1,'2026-01-01');
        INSERT INTO automatic_merge_lineage(canonical_entry_id,member_entry_id,merged_at) VALUES(2,5,'2026-01-02');
        INSERT INTO policy_preview_history(preview_json,created_at) VALUES('{}','2026-01-01');
        INSERT INTO catalogue_origin(id,uid) VALUES(1,'uid-origin');
        INSERT INTO catalogue_entry_ids(uid,entry_id,primary_id) VALUES('uid-1',1,1);
        INSERT INTO catalogue_uid_records(uid,source_id,reference) VALUES('uid-1','src-a','ref-1');
        INSERT INTO catalogue_shared_entries(entry_id) VALUES(1);
        INSERT INTO catalogue_public_refs(entry_id,reference) VALUES(1,'ref-1');
        INSERT INTO catalogue_import_records(uid,digest,record_json,state) VALUES('uid-1','digest-1','{}','applied');
        INSERT INTO catalogue_record_resolutions(uid,digest,resolution_json) VALUES('uid-1','digest-1','{}');
        INSERT INTO catalogue_exchange_receipts(digest,result_json,applied_at) VALUES('digest-1','{}','2026-01-01');
        INSERT INTO catalogue_source_facts(entry_id,reference,source_id,fact_json) VALUES(1,'ref-1','src-a','{}');
        INSERT INTO catalogue_exchange_stages(id,payload_json,revision,created_at,result_json) VALUES('stage-1','{"entries":[]}','r1',1.5,'');
        INSERT INTO source_health(source_id,state,message,last_repaired_at,repair_job_id,history_json) VALUES('src-a','failing','timeout',1700000000.5,'job-1','[{"state":"ok"}]');
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
