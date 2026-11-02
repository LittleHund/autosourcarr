using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Accounts;
using AutoSourcarr.Core.Backup;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests.Backup;

/// <summary>
/// Phase 12 "Backup and rollback". Done when (1): a backup of a populated instance (the released 0.4.4 fixture plus
/// manual decisions, profiles and chapter provenance) restores on a clean instance with the same catalogue rows, the
/// settings minus secrets, the profiles and the provenance; password protection round-trips; tampered, newer-format,
/// 0.4.x and wrong-passphrase files are refused. Done when (2): a failing migration at an update start rolls the data
/// back to the automatic backup (same stamp, migration and rows), the app starts paused with the notice, and the
/// one-click rollback route works. Also SET-3 (old files to the trash), SET-4 (retired link settings dropped) and SET-6
/// (a full reset keeps config.json and the library).
/// </summary>
public static class BackupTests
{
    private const string SecretA = "instance-a-suwayomi-password-58d1";
    private const string SecretB = "instance-b-suwayomi-password-20c7";
    private const string ConnectionSecret = "connection-key-a-77e0b1";
    private const string Passphrase = "correct horse battery staple";

    public static void Register(TestSuite suite)
    {
        suite.Add("phase 12 backup: a populated 0.4.4 instance restores on a clean instance at the next start (rows, settings minus secrets, profiles, provenance); no secret is in the file",
            RoundTrip);
        suite.Add("phase 12 backup: password-protected round trip; wrong or missing passphrase, tampered, newer-format and 0.4.x files are refused",
            Refusals);
        suite.Add("phase 12 backup crypto: chunked AES-GCM streams any size, refuses truncation and appended data, still reads ASCBK3", Crypto);
        suite.Add("phase 12 swap journal: a failed check or a crash mid-swap (mixed databases) is undone on the next start; after the check it is finished", SwapCrash);
        suite.Add("phase 12 config.json: unknown top-level keys survive rewrites and never enter a backup", UnknownConfigKeys);
        suite.Add("phase 12 update kills (re-audit 1/7): a kill at every update and rollback step ends rolled back from the kept copies, in safe mode, without a second backup or migration", UpdateKills);
        suite.Add("phase 12 restore kills (re-audit 3/6): a kill at every restore step is decided from the journal, never applied twice; a config.json that did not exist is removed on undo", RestoreKills);
        suite.Add("phase 12 swap pending (re-audit 2/5/10): an unreadable journal or marker, or set-aside files, mean safe mode, never a fresh catalogue; a live -wal is never deleted on undo", SwapPending);
        suite.Add("phase 12 update guard (SET-9): a failing migration swaps the databases back unchanged, no retry loop, Retry the update, backup failure never migrates",
            GuardCore);
        suite.Add("phase 12 update rollback (SET-9): a real failing migration on a real older schema starts the full runtime in minimal safe mode (writes refused), no restart loop, retry",
            () => GuardRuntime().GetAwaiter().GetResult());
        suite.Add("phase 12 backup routes: list/create/download/delete/preview (multipart)/arm/cancel on /api/v1; writes refused while armed; the restore applies at the next start",
            () => Routes().GetAwaiter().GetResult());
        suite.Add("phase 12 old files (SET-3): only the app's own old reports, bytecode, temporary files and previews move to a dated trash; expired trash is deleted", OldFiles);
        suite.Add("phase 12 link settings (SET-4): Komf and AI-dashboard links are gone from the schema and dropped silently from config.json", RetiredLinks);
        suite.Add("phase 12 full reset (SET-6): the full reset keeps config.json and the library files",
            () => FullResetKeeps().GetAwaiter().GetResult());
    }

    // ---- fixtures --------------------------------------------------------------------------------------------

    private const string ManualRows = """
        INSERT INTO source_config(source_id,name,lang,last_seen) VALUES('src-a','Source A','en','2026-01-01'),('src-b','Source B','en','2026-01-01');
        INSERT INTO entries(id,preferred_title,manual_preferred_title,created_at,updated_at) VALUES(3,'Gamma',0,'2026-01-01','2026-01-01');
        UPDATE entries SET preferred_title='Alpha Manual',manual_preferred_title=1 WHERE id=1;
        INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,match_type,locked,active,metadata_json,last_checked) VALUES
          (1,'src-a',101,'Alpha','manual',1,1,'{"title":"Alpha"}','2026-01-01'),
          (1,'src-b',201,'Alpha B','manual',1,1,'{"title":"Alpha B"}','2026-01-01'),
          (2,'src-a',102,'Beta','auto',0,1,'{}','2026-01-01');
        INSERT INTO manual_match_groups(id,title,created_at,updated_at) VALUES('g1','Alpha','2026-01-01','2026-01-01');
        INSERT INTO manual_match_bindings(source_id,source_manga_id,group_id,entry_id_hint,source_name,source_title,record_json,created_at,updated_at) VALUES
          ('src-a',101,'g1',1,'Source A','Alpha','{"id":101}','2026-01-01','2026-01-01'),
          ('src-b',201,'g1',1,'Source B','Alpha B','{"id":201}','2026-01-02','2026-01-02');
        INSERT INTO manual_separate_records(target_entry_id,source_id,source_manga_id,separate_entry_id_hint,source_title,created_at,updated_at)
          VALUES(1,'src-a',102,2,'Beta','2026-01-01','2026-01-01');
        INSERT INTO duplicate_reviews(left_entry_id,right_entry_id,decision,reviewed_at) VALUES(1,2,'not_duplicate','2026-01-01');
        INSERT INTO preferred_title_overrides(entry_id,title,updated_at) VALUES(1,'Alpha Manual','2026-01-01');
        INSERT INTO matching_presets(id,name,description,rule_json,updated_at) VALUES('strict','Strict','','{"enabled":true}','2026-01-01');
        """;

    private const string ProvenanceRows = """
        INSERT INTO virtual_chapters(id,entry_id,chapter_key,chapter_number,display_name,created_at,updated_at) VALUES(1,1,'c1',1,'Chapter 1','2026-01-01','2026-01-01');
        INSERT INTO chapter_candidates(id,virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,name,first_seen,last_seen)
          VALUES(1,1,'src-a','Source A',101,9001,'Chapter 1','2026-01-01','2026-01-01');
        INSERT INTO served_origin(virtual_chapter_id,candidate_id,selected_at,reason) VALUES(1,1,'2026-01-01','manual');
        INSERT INTO origin_history(virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,served_at,reason)
          VALUES(1,'src-a','Source A',101,9001,'2026-01-01','manual');
        """;

    private static readonly string[] Compared =
    [
        "entries", "source_matches", "manual_match_groups", "manual_match_bindings", "manual_separate_records", "duplicate_reviews",
        "preferred_title_overrides", "matching_presets", "serving_profiles", "source_config",
    ];

    private static readonly string[] ComparedOrigins = ["virtual_chapters", "chapter_candidates", "served_origin", "origin_history"];

    private static void Load(string path, string fixture)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = File.ReadAllText(ConfigurationTests.Fixture(Path.Combine("legacy", fixture)));
        command.ExecuteNonQuery();
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA application_id={SchemaMigrator.SchemaApplicationId}; PRAGMA user_version=1;");
        command.ExecuteNonQuery();
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Dump(string path, string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM \"" + table + "\" ORDER BY 1,2";
        using var reader = command.ExecuteReader();
        var text = new StringBuilder();
        while (reader.Read())
        {
            for (var index = 0; index < reader.FieldCount; index++) text.Append(Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)).Append('|');
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>A populated instance: the 0.4.4 fixture migrated, manual decisions, a profile and chapter provenance.</summary>
    private static SqliteDatabase Populated(string data)
    {
        Directory.CreateDirectory(data);
        Load(Path.Combine(data, "catalog.sqlite"), "catalog-0.4.4.sql");
        Load(Path.Combine(data, "chapter-origins.sqlite"), "origins-0.4.4.sql");
        var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = data });
        new SchemaMigrator(database).Initialize();
        Execute(database.Paths.Catalog, ManualRows + "INSERT INTO serving_profiles(entry_id,profile_json,revision,updated_at) VALUES(1,'{\"quality\":\"best\"}',1,'2026-01-01');");
        Execute(database.Paths.Origins, ProvenanceRows);
        return database;
    }

    private static SqliteDatabase Clean(string data)
    {
        Directory.CreateDirectory(data);
        var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = data });
        new SchemaMigrator(database).Initialize();
        return database;
    }

    private static void WriteConfig(string root, string languages, string secret, bool connection)
    {
        Directory.CreateDirectory(root);
        var secrets = SettingsSecrets.Open(root, create: true);
        var document = new ConfigDocument { ManagedKeys = ["SOURCE_LANGUAGES"] };
        document.Settings.Values["SOURCE_LANGUAGES"] = languages;
        document.Settings.Values["MAIN_SUWAYOMI_URL"] = "http://suwayomi.test:4567";
        document.Settings.Secrets["MAIN_SUWAYOMI_PASSWORD"] = secrets.Protect("MAIN_SUWAYOMI_PASSWORD", secret);
        if (connection)
            document.Connections =
            [
                new ConfigConnection
                {
                    Id = "c1", Type = "malwarebazaar", Label = "Bazaar", Values = new(StringComparer.Ordinal) { ["url"] = "https://bazaar.test" },
                    Secrets = new(StringComparer.Ordinal) { ["api_key"] = secrets.Protect("connections.c1.api_key", ConnectionSecret) },
                },
            ];
        new ConfigStore(root).WriteDocument(document);
    }

    private static CatalogueExchange Exchange(SqliteDatabase database)
    {
        var exchange = new CatalogueExchange(database, new object(), new SupportPrivacy());
        exchange.Initialize();
        return exchange;
    }

    // ---- done when (1) ---------------------------------------------------------------------------------------

    private static void RoundTrip()
    {
        using var temp = new Sandbox();
        using var a = Populated(Path.Combine(temp.Path, "a-data"));
        var configA = Path.Combine(temp.Path, "a-config");
        WriteConfig(configA, "en,ja", SecretA, connection: true);
        var storeA = new InstanceBackupStore(Path.Combine(temp.Path, "a-data"), configA, a, "0.5.0-test");
        var exchange = Exchange(a);
        var summary = storeA.Create("manual", "test", sharePage: after => exchange.Export(after, 5000));
        Check.True(summary.ShareExport && summary.Entries == 3 && summary.Chapters == 1, "summary counts and share export");
        var file = storeA.FilePath(summary.Id);
        var raw = File.ReadAllBytes(file);

        // Hard rule: no secret value, ciphertext or config.key content is in the file.
        var text = Encoding.UTF8.GetString(Unzipped(raw));
        foreach (var forbidden in new[] { SecretA, ConnectionSecret, "enc:v1:", File.ReadAllText(Path.Combine(configA, "config.key")).Trim() })
            Check.True(!text.Contains(forbidden, StringComparison.Ordinal), "no secret in the backup: " + forbidden[..Math.Min(8, forbidden.Length)]);
        using (var zip = new ZipArchive(new MemoryStream(raw)))
        {
            var names = zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
            Check.True(names.SequenceEqual(new[] { "catalogue/share-0001.json", "databases/catalog.sqlite", "databases/chapter-origins.sqlite", "manifest.json",
                "profiles/profiles.json", "settings/config.json" }), "backup layout: " + string.Join(",", names));
            var manifest = JsonNode.Parse(zip.GetEntry("manifest.json")!.Open())!.AsObject();
            Check.Equal(InstanceBackupStore.FormatVersion, manifest["format_version"]!.GetValue<int>());
            Check.True(manifest["compatibility"]?["share_format"]?["version"] is not null && manifest["compatibility"]?["backup_format"]?["version"]?.GetValue<int>() == 3, "PIPE-4 ids in the manifest");
            Check.True(manifest["schema"]!["catalog"]!["migration"]!.GetValue<long>() == SchemaMigrator.LatestMigration(DatabaseFile.Catalog), "schema version in the manifest");
            var omitted = manifest["not_included"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
            Check.True(omitted.Contains("settings.MAIN_SUWAYOMI_PASSWORD") && omitted.Contains("connections.c1.api_key"), "secret fields listed as not included");
            var profiles = JsonNode.Parse(zip.GetEntry("profiles/profiles.json")!.Open())!["tables"]!;
            Check.True(profiles["matching_presets"]!.AsArray().Count == 1 && profiles["serving_profiles"]!.AsArray().Count == 1, "profiles exported");
        }

        // A clean instance with its own config.key and its own secret.
        using var b = Clean(Path.Combine(temp.Path, "b-data"));
        var configB = Path.Combine(temp.Path, "b-config");
        WriteConfig(configB, "de", SecretB, connection: false);
        var keyB = File.ReadAllBytes(Path.Combine(configB, "config.key"));
        var storeB = new InstanceBackupStore(Path.Combine(temp.Path, "b-data"), configB, b, "0.5.0-test");
        var preview = storeB.Stage(raw, null, "upload");
        Check.True(preview["settings"]!["changed"]!.AsArray().Any(node => node!.GetValue<string>() == "settings.SOURCE_LANGUAGES"), "preview names the changed setting");
        Check.True(preview["settings"]!["secrets_kept_from_this_instance"]!.AsArray().Any(node => node!.GetValue<string>() == "settings.MAIN_SUWAYOMI_PASSWORD"), "preview keeps B's secret");
        Check.True(preview["settings"]!["secrets_to_enter_again"]!.AsArray().Any(node => node!.GetValue<string>() == "connections.c1.api_key"), "preview lists the connection key to enter again");
        Check.True(preview["databases"]!.AsArray().Count == 2 && preview["version_check"]!["ok"]!.GetValue<bool>(), "preview databases and version check");
        // Hard rule 1: a wrong token changes nothing; a manual decision saved after the preview invalidates it (audit 7).
        var stage = preview["stage_id"]!.GetValue<string>(); var token = preview["preview_token"]!.GetValue<string>();
        Check.Throws<BackupRefusedException>(() => storeB.Arm(stage, "wrong-token"));
        Execute(b.Paths.Catalog, "INSERT INTO manual_match_groups(id,title,created_at,updated_at) VALUES('late','Late','2026-12-01','2026-12-01')");
        Check.True(Check.Throws<BackupRefusedException>(() => storeB.Arm(stage, token)).Message.Contains("saved decisions", StringComparison.Ordinal), "stale after a new decision");
        // Re-audit 8: chapter provenance decisions in chapter-origins.sqlite count too.
        preview = storeB.Stage(raw, null, "upload");
        Execute(b.Paths.Origins, "INSERT INTO chapter_candidate_rejections(source_id,source_chapter_id,rejected_at) VALUES('src-a',5,'2026-12-01')");
        Check.Throws<BackupRefusedException>(() => storeB.Arm(preview["stage_id"]!.GetValue<string>(), preview["preview_token"]!.GetValue<string>()));
        Check.True(storeB.PendingRestore() is null, "nothing armed");
        preview = storeB.Stage(raw, null, "upload");
        stage = preview["stage_id"]!.GetValue<string>(); token = preview["preview_token"]!.GetValue<string>();
        // Nothing is replaced when arming: the restore waits for the next start.
        storeB.Arm(stage, token);
        Check.True(storeB.PendingRestore()?["stage_id"]?.GetValue<string>() == stage, "armed");
        Check.Equal(0L, BackupDatabases.Facts(b.Paths.Catalog).Rows["entries"]);
        SqliteConnection.ClearAllPools();
        var result = storeB.ApplyAtStart()!;
        Check.True(result["status"]!.GetValue<string>() == "restored" && storeB.Find(result["pre_restore_backup"]!.GetValue<string>())?.Kind == "pre-restore",
            "restored at start with a pre-restore backup: " + result.ToJsonString());
        Check.True(storeB.PendingRestore() is null && !Directory.Exists(Path.Combine(storeB.Staged, stage)), "armed restore consumed");
        Check.True(!File.Exists(b.Paths.Catalog + ".swap-old") && !File.Exists(Path.Combine(storeB.Root, DatabaseSwap.JournalFile)), "swap finished cleanly");

        foreach (var table in Compared) Check.Equal(Dump(a.Paths.Catalog, table), Dump(b.Paths.Catalog, table));
        foreach (var table in ComparedOrigins) Check.Equal(Dump(a.Paths.Origins, table), Dump(b.Paths.Origins, table));
        Check.True(BackupDatabases.Facts(b.Paths.Catalog).Intact && BackupDatabases.Facts(b.Paths.Origins).Intact, "restored files intact");

        var restored = new ConfigStore(configB).Load();
        Check.Equal("en,ja", restored.Settings["SOURCE_LANGUAGES"]);
        Check.Equal("http://suwayomi.test:4567", restored.Settings["MAIN_SUWAYOMI_URL"]);
        Check.Equal(SecretB, restored.Settings["MAIN_SUWAYOMI_PASSWORD"]); // never overwritten with nothing, never A's
        var document = new ConfigStore(configB).ReadDocument()!;
        Check.True(document.Connections?.Single().Id == "c1" && document.Connections.Single().Secrets.Count == 0, "connection restored without its secret");
        Check.True(keyB.SequenceEqual(File.ReadAllBytes(Path.Combine(configB, "config.key"))), "config.key untouched");

        // A backup that points MAIN_SUWAYOMI_URL elsewhere never gets this instance's Suwayomi password (phase 7 rule).
        var moved = BackupSettings.Sanitize(new ConfigStore(configA).ReadDocument()!).Document;
        moved.Settings.Values["MAIN_SUWAYOMI_URL"] = "http://elsewhere.test";
        moved.Connections!.Single().Values["url"] = "https://other-bazaar.test";
        var current = new ConfigStore(configA).ReadDocument()!;
        var merged = BackupSettings.Merge(moved, current);
        Check.True(!merged.Document.Settings.Secrets.ContainsKey("MAIN_SUWAYOMI_PASSWORD") && merged.MissingSecrets.Contains("settings.MAIN_SUWAYOMI_PASSWORD"),
            "secret not carried to a changed address");
        Check.True(merged.Document.Connections!.Single().Secrets.Count == 0 && merged.MissingSecrets.Contains("connections.c1.api_key"), "connection secret not carried to a changed address");
    }

    private static byte[] Unzipped(byte[] raw)
    {
        using var zip = new ZipArchive(new MemoryStream(raw));
        using var output = new MemoryStream();
        foreach (var entry in zip.Entries) using (var stream = entry.Open()) stream.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Rezip(byte[] raw, Func<string, byte[], byte[]> change)
    {
        using var input = new ZipArchive(new MemoryStream(raw));
        using var buffer = new MemoryStream();
        using (var output = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in input.Entries)
            {
                using var source = entry.Open(); using var copy = new MemoryStream(); source.CopyTo(copy);
                var target = output.CreateEntry(entry.FullName);
                using var stream = target.Open(); stream.Write(change(entry.FullName, copy.ToArray()));
            }
        return buffer.ToArray();
    }

    private static void Refusals()
    {
        using var temp = new Sandbox();
        using var a = Populated(Path.Combine(temp.Path, "data"));
        var config = Path.Combine(temp.Path, "config");
        WriteConfig(config, "en", SecretA, connection: false);
        var store = new InstanceBackupStore(Path.Combine(temp.Path, "data"), config, a, "0.5.0-test");
        var sealedBackup = store.Create("manual", passphrase: Passphrase);
        Check.True(sealedBackup.Protected, "protected");
        var bytes = File.ReadAllBytes(store.FilePath(sealedBackup.Id));
        Check.True(BackupCrypto.IsProtected(bytes) && !Encoding.Latin1.GetString(bytes).Contains("manifest.json", StringComparison.Ordinal), "encrypted file hides the layout");
        Check.True(Check.Throws<BackupRefusedException>(() => store.Stage(bytes, null, "upload")).Message.Contains("password protected", StringComparison.Ordinal), "missing passphrase");
        Check.True(Check.Throws<BackupRefusedException>(() => store.Stage(bytes, "a wrong passphrase!", "upload")).Message.Contains("Wrong passphrase", StringComparison.Ordinal), "wrong passphrase");
        Check.Throws<BackupRefusedException>(() => store.Create("manual", passphrase: "short"));
        var flipped = (byte[])bytes.Clone(); flipped[^20] ^= 0x40;
        Check.Throws<BackupRefusedException>(() => store.Stage(flipped, Passphrase, "upload"));
        var preview = store.Stage(bytes, Passphrase, "upload");
        Check.True(preview["integrity"]!.GetValue<string>() == "ok", "correct passphrase opens the backup");

        var plain = File.ReadAllBytes(store.FilePath(store.Create("manual").Id));
        // Tampered content: the manifest checksum no longer matches.
        var tampered = Rezip(plain, (name, data) => name == "profiles/profiles.json" ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("Strict", "Loose", StringComparison.Ordinal)) : data);
        Check.True(Check.Throws<BackupRefusedException>(() => store.Stage(tampered, null, "upload")).Message.Contains("tampered", StringComparison.Ordinal), "tampered (size) refused");
        // Same size, different content: only the checksum can tell.
        var sameSize = Rezip(plain, (name, data) => name == "profiles/profiles.json" ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("Strict", "Strong", StringComparison.Ordinal)) : data);
        Check.True(Check.Throws<BackupRefusedException>(() => store.Stage(sameSize, null, "upload")).Message.Contains("checksum", StringComparison.Ordinal), "tampered (content) refused");
        // A manifest that declares more than the cap is refused before anything is written (no disk fill).
        var huge = Rezip(plain, (name, data) =>
        {
            if (name != "manifest.json") return data;
            var manifest = JsonNode.Parse(data)!.AsObject();
            manifest["contents"]![0]!["bytes"] = InstanceBackupStore.MaxDeclaredBytes + 1;
            return Encoding.UTF8.GetBytes(manifest.ToJsonString());
        });
        Check.Equal(413, Check.Throws<BackupRefusedException>(() => store.Stage(huge, null, "upload")).Status);
        // An extra file the manifest does not list.
        Check.Throws<BackupRefusedException>(() => store.Stage(WithEntry(plain), null, "upload"));
        // A newer format: "Version mismatch: version X required".
        var newer = Rezip(plain, (name, data) =>
        {
            if (name != "manifest.json") return data;
            var manifest = JsonNode.Parse(data)!.AsObject();
            manifest["format_version"] = 4; manifest["min_reader_version"] = 4; manifest["app_version"] = "9.9.9";
            return Encoding.UTF8.GetBytes(manifest.ToJsonString());
        });
        var message = Check.Throws<BackupRefusedException>(() => store.Stage(newer, null, "upload")).Message;
        Check.True(message.StartsWith("Version mismatch: version 9.9.9 required", StringComparison.Ordinal), "newer format message: " + message);
        // A newer database schema than this build is refused the same way.
        var future = Path.Combine(temp.Path, "future.sqlite");
        File.Copy(a.Paths.Catalog, future);
        Execute(future, "INSERT INTO VersionInfo(Version,AppliedOn,Description) VALUES(2099010101,'2099-01-01','future')");
        Check.True(Check.Throws<BackupRefusedException>(() => BackupDatabases.Check(future, BackupDatabases.Files[0], "9.9.7")).Message.StartsWith("Version mismatch: version 9.9.7 required", StringComparison.Ordinal), "newer schema refused");
        // A 0.4.x encrypted instance backup (ASCBK2) and a non-backup file.
        Check.True(Check.Throws<BackupRefusedException>(() => store.Stage([.. "ASCBK2\n"u8, .. new byte[64]], Passphrase, "upload")).Message.Contains("0.4.x", StringComparison.Ordinal), "0.4.x refused");
        Check.Throws<BackupRefusedException>(() => store.Stage(Encoding.UTF8.GetBytes("not a backup"), null, "upload"));
        // A settings file with a secret planted by hand is refused (backups never carry secrets).
        var planted = Rezip(plain, (name, data) => name == "settings/config.json"
            ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("\"secrets\": {}", "\"secrets\": {\"ADMIN_TOKEN\": \"x\"}", StringComparison.Ordinal)) : data);
        Check.Throws<BackupRefusedException>(() => store.Stage(FixChecksums(planted), null, "upload"));
    }

    private static byte[] WithEntry(byte[] raw)
    {
        var buffer = new MemoryStream();
        buffer.Write(raw);
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        using (var stream = zip.CreateEntry("catalogue/share-0099.json").Open()) stream.Write("{}"u8);
        return buffer.ToArray();
    }

    /// <summary>Rewrites the manifest checksums so only the content check (not the hash check) can refuse the file.</summary>
    private static byte[] FixChecksums(byte[] raw)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var zip = new ZipArchive(new MemoryStream(raw)))
            foreach (var entry in zip.Entries) { using var s = entry.Open(); using var m = new MemoryStream(); s.CopyTo(m); files[entry.FullName] = m.ToArray(); }
        var manifest = JsonNode.Parse(files["manifest.json"])!.AsObject();
        foreach (var row in manifest["contents"]!.AsArray().OfType<JsonObject>())
        {
            var data = files[row["path"]!.GetValue<string>()];
            row["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)); row["bytes"] = data.Length;
        }
        files["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        return Rezip(raw, (name, _) => files[name]);
    }

    // ---- done when (2) ---------------------------------------------------------------------------------------

    private static readonly string[] RollbackPoints =
        ["guard:migrating", "guard:rolling-back", "swap:aside:catalog.sqlite", "swap:aside:chapter-origins.sqlite", "swap:aside", "swap:placed:catalog.sqlite",
         "swap:placed", "guard:rollback-verified", "swap:verified", "guard:rolled-back"];

    private static void UpdateKills()
    {
        foreach (var point in RollbackPoints.Concat(["guard:migrated", "guard:updated"]))
        {
            using var temp = new Sandbox();
            var data = Path.Combine(temp.Path, "data");
            using var database = Populated(data);
            var config = Path.Combine(temp.Path, "config");
            WriteConfig(config, "en", SecretA, connection: false);
            UpgradeGuard.Run(new InstanceBackupStore(data, config, database, "0.5.0-test"), database, () => new SchemaMigrator(database).Initialize());
            SqliteConnection.ClearAllPools();
            var before = (BackupDatabases.Facts(database.Paths.Catalog), BackupDatabases.Facts(database.Paths.Origins));
            var succeeds = point is "guard:migrated" or "guard:updated";
            var migrations = 0;
            void Migrate()
            {
                migrations++;
                new SchemaMigrator(database).Initialize();
                if (succeeds) return;
                Execute(database.Paths.Catalog, "INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(98,'Half','2026-01-01','2026-01-01')");
                throw new InvalidOperationException("migration failed");
            }
            var store = new InstanceBackupStore(data, config, database, "0.5.1-test");
            var crashed = false;
            CrashPoints.Hook = reached => { if (reached == point && !crashed) { crashed = true; throw new SimulatedCrash(reached); } };
            try { UpgradeGuard.Run(store, database, Migrate); }
            catch (SimulatedCrash) { }
            finally { CrashPoints.Hook = null; }
            Check.True(crashed, "reached " + point);
            SqliteConnection.ClearAllPools();
            var backups = store.List().Count(row => row.Kind == "automatic");
            var migratedBefore = migrations;

            // The next start: the swap journal first, then the update check.
            var next = new InstanceBackupStore(data, config, database, "0.5.1-test");
            next.ApplyAtStart();
            var outcome = UpgradeGuard.Run(next, database, Migrate);
            SqliteConnection.ClearAllPools();
            if (point == "guard:updated")
            {
                Check.True(outcome.Status == "unchanged" && !outcome.SafeMode, point + ": the finished update stands (" + outcome.Status + ")");
            }
            else
            {
                Check.True(outcome.SafeMode && UpgradeGuard.Text(UpgradeGuard.Marker(next), "status") == "rolled-back", point + ": rolled back, safe mode (" + outcome.Status + ")");
                Check.True(before.Item1.SameData(BackupDatabases.Facts(database.Paths.Catalog)) && before.Item2.SameData(BackupDatabases.Facts(database.Paths.Origins)),
                    point + ": the data from before the update");
                Check.Equal(migratedBefore, migrations); // never migrated again after the kill
                Check.True(UpgradeGuard.Run(next, database, Migrate).Status == "safe-mode" && migrations == migratedBefore, point + ": no loop on the following start");
            }
            Check.True(next.List().Count(row => row.Kind == "automatic") == backups, point + ": no second backup of half-migrated data");
            Check.True(BackupDatabases.Facts(database.Paths.Catalog).Intact, point + ": intact");
            Check.True(!File.Exists(database.Paths.Catalog + UpgradeGuard.PreUpdate) && !File.Exists(database.Paths.Catalog + ".swap-old")
                && !File.Exists(Path.Combine(next.Root, DatabaseSwap.JournalFile)), point + ": copies and journal cleaned up after the final marker");
        }

        // Re-audit 7: a rollback that cannot finish keeps the copies; the notice offers a restore, not a retry.
        using (var temp = new Sandbox())
        {
            var data = Path.Combine(temp.Path, "data");
            using var database = Populated(data);
            var config = Path.Combine(temp.Path, "config");
            WriteConfig(config, "en", SecretA, connection: false);
            UpgradeGuard.Run(new InstanceBackupStore(data, config, database, "0.5.0-test"), database, () => new SchemaMigrator(database).Initialize());
            SqliteConnection.ClearAllPools();
            var store = new InstanceBackupStore(data, config, database, "0.5.1-test");
            CrashPoints.Hook = reached => { if (reached == "guard:rollback-verified") throw new IOException("disk error while checking"); };
            UpgradeOutcome outcome;
            try { outcome = UpgradeGuard.Run(store, database, () => throw new InvalidOperationException("migration failed")); }
            finally { CrashPoints.Hook = null; }
            Check.True(outcome.Status == "rollback-failed" && File.Exists(database.Paths.Catalog + UpgradeGuard.PreUpdate), "rollback-failed keeps the copies");
            var notice = UpgradeGuard.SafeModeNotice(store)!;
            var actions = notice["actions"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
            Check.True(actions.Contains("restore") && !actions.Contains("retry"), "restore offered, retry not: " + string.Join(",", actions));
            Check.Throws<BackupRefusedException>(() => UpgradeGuard.Retry(store));
        }
    }

    private static void RestoreKills()
    {
        foreach (var point in new[] { "restore:applying", "restore:pre-restore", "swap:aside:catalog.sqlite", "swap:placed:catalog.sqlite", "swap:placed", "swap:verified", "restore:swapped" })
        {
            using var temp = new Sandbox();
            using var a = Populated(Path.Combine(temp.Path, "a-data"));
            var configA = Path.Combine(temp.Path, "a-config");
            WriteConfig(configA, "en,ja", SecretA, connection: false);
            var raw = File.ReadAllBytes(new InstanceBackupStore(Path.Combine(temp.Path, "a-data"), configA, a, "0.5.0-test").FilePath(
                new InstanceBackupStore(Path.Combine(temp.Path, "a-data"), configA, a, "0.5.0-test").Create("manual").Id));
            using var b = Clean(Path.Combine(temp.Path, "b-data"));
            var configB = Path.Combine(temp.Path, "b-config"); Directory.CreateDirectory(configB); // no config.json before the restore
            var store = new InstanceBackupStore(Path.Combine(temp.Path, "b-data"), configB, b, "0.5.0-test");
            var preview = store.Stage(raw, null, "upload");
            store.Arm(preview["stage_id"]!.GetValue<string>(), preview["preview_token"]!.GetValue<string>());
            SqliteConnection.ClearAllPools();
            var original = BackupDatabases.Facts(b.Paths.Catalog);
            var restored = point is "swap:verified" or "restore:swapped";
            var crashed = false;
            CrashPoints.Hook = reached => { if (reached == point && !crashed) { crashed = true; throw new SimulatedCrash(reached); } };
            try { store.ApplyAtStart(); }
            catch (SimulatedCrash) { }
            finally { CrashPoints.Hook = null; }
            Check.True(crashed, "reached " + point);
            SqliteConnection.ClearAllPools();
            var next = new InstanceBackupStore(Path.Combine(temp.Path, "b-data"), configB, b, "0.5.0-test");
            var result = next.ApplyAtStart()!;
            SqliteConnection.ClearAllPools();
            Check.True(next.PendingRestore() is null && next.ApplyAtStart() is null, point + ": never applied a second time");
            var now = BackupDatabases.Facts(b.Paths.Catalog);
            if (restored)
            {
                Check.True(result["status"]!.GetValue<string>() == "restored" && now.Rows["entries"] == 3, point + ": restored: " + result.ToJsonString());
                Check.True(File.Exists(Path.Combine(configB, "config.json")), point + ": settings restored");
            }
            else
            {
                Check.True(result["status"]!.GetValue<string>() == "failed" && now.SameData(original), point + ": undone: " + result.ToJsonString());
                Check.True(!File.Exists(Path.Combine(configB, "config.json")), point + ": the config.json that did not exist before is gone");
                Check.Equal("restore-failed", UpgradeGuard.Text(UpgradeGuard.Marker(next), "status"));
                Check.True(UpgradeGuard.Run(next, b, () => new SchemaMigrator(b).Initialize()).SafeMode, point + ": safe mode after an interrupted restore");
            }
            Check.True(!DatabaseSwap.Pending(next.Root, b.Paths, configB), point + ": nothing left aside");
        }
    }

    private static void SwapPending()
    {
        using var temp = new Sandbox();
        var data = Path.Combine(temp.Path, "data");
        using var database = Populated(data);
        var config = Path.Combine(temp.Path, "config");
        WriteConfig(config, "en", SecretA, connection: false);
        var store = new InstanceBackupStore(data, config, database, "0.5.0-test");
        UpgradeGuard.Run(store, database, () => new SchemaMigrator(database).Initialize());
        SqliteConnection.ClearAllPools();
        var catalog = File.ReadAllBytes(database.Paths.Catalog);

        // Both databases set aside and an unreadable journal: safe mode, never the "fresh" path on empty databases.
        foreach (var path in new[] { database.Paths.Catalog, database.Paths.Origins }) File.Move(path, path + ".swap-old");
        File.WriteAllText(Path.Combine(store.Root, DatabaseSwap.JournalFile), "{not json");
        Check.Equal("swap-pending", store.ApplyAtStart()!["status"]!.GetValue<string>());
        var migrated = false;
        var outcome = UpgradeGuard.Run(store, database, () => migrated = true);
        Check.True(outcome.Status == "swap-pending" && outcome.SafeMode && !migrated && !File.Exists(database.Paths.Catalog), "swap pending: safe mode, nothing created");
        Check.True(UpgradeGuard.PublicNotice(store)!.Count == 2 && UpgradeGuard.PublicNotice(store)!["error"] is null, "guests get status and message only");
        // Once the files are back (by hand or a later version), the next start is normal again.
        File.Delete(Path.Combine(store.Root, DatabaseSwap.JournalFile));
        foreach (var path in new[] { database.Paths.Catalog, database.Paths.Origins }) File.Move(path + ".swap-old", path);
        Check.Equal("unchanged", UpgradeGuard.Run(store, database, () => new SchemaMigrator(database).Initialize()).Status);
        SqliteConnection.ClearAllPools();
        catalog = File.ReadAllBytes(database.Paths.Catalog);

        // Re-audit 5: a crash between setting aside -wal and the database: the live -wal is put back, never deleted.
        File.WriteAllText(Path.Combine(store.Root, DatabaseSwap.JournalFile), "{\"kind\":\"restore\",\"step\":\"begin\",\"existed\":{\"catalog.sqlite\":true,\"chapter-origins.sqlite\":true}}");
        File.WriteAllBytes(database.Paths.Catalog + ".swap-old-wal", [1, 2, 3]);
        Check.True(DatabaseSwap.Recover(store.Root, database.Paths, config)!.Contains("undone", StringComparison.Ordinal), "undone");
        Check.True(File.ReadAllBytes(database.Paths.Catalog + "-wal").SequenceEqual(new byte[] { 1, 2, 3 }) && catalog.SequenceEqual(File.ReadAllBytes(database.Paths.Catalog)),
            "the -wal went back next to its database");
        // ... and a live -wal without a set-aside counterpart (the swap had not reached it) is kept.
        File.WriteAllText(Path.Combine(store.Root, DatabaseSwap.JournalFile), "{\"kind\":\"restore\",\"step\":\"aside\"}");
        Check.True(DatabaseSwap.Recover(store.Root, database.Paths, config) is not null && File.Exists(database.Paths.Catalog + "-wal"), "live -wal kept");
        File.Delete(database.Paths.Catalog + "-wal");

        // Re-audit 10: an unreadable marker or one with wrong field types means safe mode, never a silent retry or a crash.
        var next = new InstanceBackupStore(data, config, database, "0.5.1-test");
        foreach (var text in new[] { "{bad", "[]", "{\"status\":5}", "{\"status\":\"rolled-back\",\"to_version\":7,\"retry\":\"yes\"}", "{\"status\":\"rolled-back\",\"levels\":3}" })
        {
            File.WriteAllText(UpgradeGuard.MarkerPath(next), text);
            var calls = 0;
            var result = UpgradeGuard.Run(next, database, () => calls++);
            Check.True(result.Status == "unreadable" && result.SafeMode && calls == 0, "unreadable marker " + text + ": " + result.Status);
            Check.True(UpgradeGuard.SafeModeNotice(next)!["actions"]!.AsArray().Any(node => node!.GetValue<string>() == "retry"), "retry offered for " + text);
        }
        File.Delete(UpgradeGuard.MarkerPath(next));
    }

    private static void GuardCore()
    {
        using var temp = new Sandbox();
        var data = Path.Combine(temp.Path, "data");
        using var database = Populated(data);
        var config = Path.Combine(temp.Path, "config");
        WriteConfig(config, "en", SecretA, connection: false);
        var store = new InstanceBackupStore(data, config, database, "0.5.0-test");
        // First guarded start on current data: nothing pending, no backup.
        Check.Equal("unchanged", UpgradeGuard.Run(store, database, () => new SchemaMigrator(database).Initialize()).Status);
        Check.Equal(0, store.List().Count);
        var before = (BackupDatabases.Facts(database.Paths.Catalog), BackupDatabases.Facts(database.Paths.Origins));
        var catalogDump = Compared.Select(table => Dump(database.Paths.Catalog, table)).ToArray();

        // The next version: its migration writes, then fails.
        var next = new InstanceBackupStore(data, config, database, "0.5.1-test");
        var calls = 0;
        void Failing()
        {
            calls++;
            new SchemaMigrator(database).Initialize();
            Execute(database.Paths.Catalog, "INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(99,'Half migrated','2026-01-01','2026-01-01'); DELETE FROM manual_match_bindings;");
            throw new InvalidOperationException("migration 2026119999 failed");
        }
        var outcome = UpgradeGuard.Run(next, database, Failing);
        Check.True(outcome.RolledBack && outcome.SafeMode && outcome.Backup?.Kind == "automatic" && outcome.Error!.Contains("2026119999", StringComparison.Ordinal), "rolled back");
        Check.True(before.Item1.SameData(BackupDatabases.Facts(database.Paths.Catalog)) && before.Item2.SameData(BackupDatabases.Facts(database.Paths.Origins)),
            "same stamp, migration and row counts after the rollback");
        Check.True(BackupDatabases.Facts(database.Paths.Catalog).Intact && BackupDatabases.Facts(database.Paths.Origins).Intact, "intact after the rollback");
        Check.True(catalogDump.SequenceEqual(Compared.Select(table => Dump(database.Paths.Catalog, table))), "same rows after the rollback");
        Check.True(!File.Exists(database.Paths.Catalog + ".pre-update") && !File.Exists(database.Paths.Catalog + ".swap-old"), "rollback copies cleaned up");
        var marker = UpgradeGuard.Marker(next)!;
        Check.True(marker["status"]!.GetValue<string>() == "rolled-back" && marker["to_version"]!.GetValue<string>() == "0.5.1-test", "marker written");

        // No restart loop: the next start of the same version neither retries nor backs up again.
        var backups = next.List().Count;
        var again = UpgradeGuard.Run(next, database, Failing);
        Check.True(again.Status == "safe-mode" && calls == 1 && next.List().Count == backups, "no retry and no new backup while the marker stands");
        Check.True(UpgradeGuard.SafeModeNotice(next)!["actions"]!.AsArray().Any(node => node!.GetValue<string>() == "retry"), "retry offered");

        // "Retry the update": a fresh backup now, then the next start migrates (this time successfully).
        var fresh = UpgradeGuard.Retry(next);
        Check.True(fresh.Kind == "automatic" && UpgradeGuard.Marker(next)!["retry"]!.GetValue<bool>(), "retry armed after a fresh backup");
        Check.Equal("updated", UpgradeGuard.Run(next, database, () => new SchemaMigrator(database).Initialize()).Status);
        Check.True(UpgradeGuard.Marker(next) is null, "marker cleared");

        // Further updates keep the newest 3 automatic backups.
        foreach (var version in new[] { "0.5.2-test", "0.5.3-test", "0.5.4-test" })
            Check.Equal("updated", UpgradeGuard.Run(new InstanceBackupStore(data, config, database, version), database, () => new SchemaMigrator(database).Initialize()).Status);
        Check.Equal(InstanceBackupStore.AutomaticKeep, store.List().Count(row => row.Kind == "automatic"));
        Check.True(UpgradeGuard.Dismiss(store) && UpgradeGuard.Notice(store) is null, "Keep current dismisses the notice");

        // Audit 3: a failing automatic backup (here a full disk) never migrates and never aborts the start.
        SqliteConnection.ClearAllPools();
        var blocked = new InstanceBackupStore(data, config, database, "0.5.5-test");
        var migrated = false;
        CrashPoints.Hook = point => { if (point == "guard:backup") throw new IOException("No space left on device"); };
        UpgradeOutcome failed;
        try { failed = UpgradeGuard.Run(blocked, database, () => migrated = true); }
        finally { CrashPoints.Hook = null; }
        Check.True(failed.Status == "backup-failed" && failed.SafeMode && !migrated, "backup failure: safe mode, nothing migrated");
        Check.True(UpgradeGuard.Run(blocked, database, () => migrated = true).Status == "safe-mode" && !migrated, "still not retried");
        var notice = UpgradeGuard.SafeModeNotice(blocked)!;
        Check.True(notice["actions"]!.AsArray().Any(node => node!.GetValue<string>() == "migrate-without-backup"), "migrate without a backup offered");
        Check.Throws<BackupRefusedException>(() => UpgradeGuard.AllowWithoutBackup(blocked, "wrong-token"));
        UpgradeGuard.AllowWithoutBackup(blocked, notice["preview_token"]!.GetValue<string>());
        var automatic = blocked.List().Count(row => row.Kind == "automatic");
        Check.Equal("updated", UpgradeGuard.Run(blocked, database, () => { migrated = true; new SchemaMigrator(database).Initialize(); }).Status);
        Check.True(migrated && blocked.List().Count(row => row.Kind == "automatic") == automatic, "migrated once, without a new backup");

        // Audit 11: an instance with only the catalogue (fresh origins) is backed up with what exists.
        SqliteConnection.ClearAllPools();
        File.Move(database.Paths.Origins, database.Paths.Origins + ".away");
        var partial = blocked.Create("manual", "catalogue only");
        Check.True(partial.Entries > 0 && partial.Chapters == 0, "catalogue-only backup");
        File.Move(database.Paths.Origins + ".away", database.Paths.Origins);
        var staged = blocked.StageSaved(partial.Id, null);
        Check.True(staged["databases"]!.AsArray().Count == 1, "a catalogue-only backup stages");
    }

    /// <summary>A real test-only migration (registered only by this test): partial schema change, then a failure.</summary>
    [FluentMigrator.Migration(2099010101, FluentMigrator.TransactionBehavior.None, "test only: fails after a partial change")]
    [FluentMigrator.Tags(SchemaMigrator.CatalogTag)]
    public sealed class FailingTestMigration : FluentMigrator.ForwardOnlyMigration
    {
        public override void Up() => Execute.WithConnection((connection, transaction) =>
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE phase12_partial(id INTEGER PRIMARY KEY); INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(990,'Half','2026-01-01','2026-01-01');";
                command.ExecuteNonQuery();
            }
            throw new InvalidOperationException("test migration 2099010101 failed");
        });
    }

    private static async Task<Phase11WiringTests.App> StartAsync(Sandbox temp, AutoSourcarrSettings settings)
    {
        var runtime = await ApplicationRuntime.CreateExternalForTestsAsync(settings, settings.SettingsConfigDir);
        var assets = Path.Combine(temp.Path, "assets", "static"); Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "index.html"), "app-page"); File.WriteAllText(Path.Combine(assets, "login.html"), "login-page");
        var host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => services.AddRouting())
            .Configure(app => runtime.ConfigureHttp(app, assets))).Build();
        await host.StartAsync();
        return new Phase11WiringTests.App(runtime, host, temp, settings);
    }

    private static async Task StopAsync(Phase11WiringTests.App app)
    {
        app.Host.Dispose();
        await app.Runtime.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    private static AutoSourcarrSettings Settings(Sandbox temp) => new()
    {
        DataDir = Path.Combine(temp.Path, "data"), SettingsConfigDir = Path.Combine(temp.Path, "config"), SourceEngineMode = "external",
        SourceSuwayomiUrl = "http://127.0.0.1:1", AdminToken = Phase11WiringTests.AdminToken, LibraryDir = Path.Combine(temp.Path, "library"),
    };

    private static async Task<JsonObject> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Check.True(response.StatusCode == expected, (int)response.StatusCode + " " + response.RequestMessage?.RequestUri + ": " + text);
        return JsonNode.Parse(text)!.AsObject();
    }

    private static async Task<AutoSourcarr.Core.Jobs.TaskRecord> Finished(Phase11WiringTests.App app, string id)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            if (app.Runtime.Tasks.Find(id) is { State: not ("running" or "queued") } row) return row;
            await Task.Delay(50);
        }
        throw new TimeoutException("Task " + id + " did not finish.");
    }

    private static int Automatic(AutoSourcarrSettings settings) =>
        Directory.EnumerateFiles(Path.Combine(settings.DataDir, "instance-backups"), "*.json")
            .Count(file => File.ReadAllText(file).Contains("\"kind\":\"automatic\"", StringComparison.Ordinal));

    private static async Task GuardRuntime()
    {
        using var temp = new Sandbox();
        var settings = Settings(temp);
        var app = await StartAsync(temp, settings);
        app.Runtime.Database.Write(DatabaseFile.Catalog, session => session.Execute(
            "INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(7,'Kept entry','2026-01-01','2026-01-01')"));
        await StopAsync(app);
        var catalog = Path.Combine(settings.DataDir, "catalog.sqlite");
        var before = BackupDatabases.Facts(catalog);

        // "New code": the same build plus one real catalogue migration that fails; the data is at the previous level.
        SchemaMigrator.MigrationAssemblies = [typeof(SchemaMigrator).Assembly, typeof(FailingTestMigration).Assembly];
        try
        {
            app = await StartAsync(temp, settings);
            try
            {
                Check.True(app.Runtime.SafeMode && app.Runtime.Upgrade?.RolledBack == true, "the full runtime starts in safe mode after the rollback");
                var after = BackupDatabases.Facts(catalog);
                Check.True(before.SameData(after) && after.Intact && !after.Rows.ContainsKey("phase12_partial"), "back at the previous migration level, no partial table");
                Check.True(after.Migration < SchemaMigrator.LatestMigration(DatabaseFile.Catalog), "the data is really at the older schema");
                Check.Equal(1, Automatic(settings));
                using var admin = app.Client(Phase11WiringTests.AdminToken);
                var list = await Json(await admin.GetAsync("/api/v1/backups"), HttpStatusCode.OK);
                var safe = list["safe_mode"]!.AsObject();
                Check.True(safe["status"]!.GetValue<string>() == "rolled-back" && safe["pending_migrations"]!.AsArray().Any(node => node!.GetValue<long>() == 2099010101)
                    && safe["error"]!.GetValue<string>().Contains("2099010101", StringComparison.Ordinal), "safe-mode notice names the failed migration");
                Check.True(list["update"] is null, "no rollback-to-backup button on a rolled-back instance (the data already is that backup)");
                var health = await Json(await admin.GetAsync("/api/v1/health"), HttpStatusCode.OK);
                Check.Equal("safe-mode", health["status"]!.GetValue<string>());
                // Re-audit 4: health is guest-readable: status and a short message, no errors, ids, migrations or tokens.
                var exposed = health["safe_mode"]!.AsObject();
                Check.True(exposed.Count == 2 && !health.ToJsonString().Contains("2099010101", StringComparison.Ordinal)
                    && !health.ToJsonString().Contains(settings.DataDir.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal), "health hides the details");
                using (var diagnostics = await admin.GetAsync("/api/v1/diagnostics")) Check.Equal(HttpStatusCode.OK, diagnostics.StatusCode);
                // Writes are refused (settings, UI preferences, errors), catalogue services are not even built.
                using (var put = await admin.PutAsJsonAsync("/api/v1/settings", new { revision = "x", values = new { } })) Check.Equal(HttpStatusCode.ServiceUnavailable, put.StatusCode);
                using (var clear = await admin.PostAsJsonAsync("/api/v1/errors/clear", new { })) Check.Equal(HttpStatusCode.ServiceUnavailable, clear.StatusCode);
                using (var stats = await admin.GetAsync("/api/v1/status/stats")) Check.Equal(HttpStatusCode.ServiceUnavailable, stats.StatusCode);
                var tasks = app.Runtime.Tasks.Snapshot().ToJsonString();
                Check.True(tasks.Contains("Automatic backup before update", StringComparison.Ordinal) && tasks.Contains("Update rolled back", StringComparison.Ordinal), "recorded in Tasks");
            }
            finally { await StopAsync(app); }

            // No restart loop: the same build starts in safe mode again without a new backup or a second attempt.
            app = await StartAsync(temp, settings);
            try
            {
                Check.True(app.Runtime.SafeMode && app.Runtime.Upgrade?.Status == "safe-mode", "safe mode again");
                Check.Equal(1, Automatic(settings));
                Check.True(!BackupDatabases.Facts(catalog).Rows.ContainsKey("phase12_partial"), "the failing migration did not run again");
                using var admin = app.Client(Phase11WiringTests.AdminToken);
                using (var refused = await admin.PostAsJsonAsync("/api/v1/backups/safe-mode/retry", new { })) Check.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                // Re-audit 11: the fresh backup runs as a job (token-only instance: the admin token may choose).
                var retry = await Json(await admin.PostAsJsonAsync("/api/v1/backups/safe-mode/retry", new { confirm = true }), HttpStatusCode.Accepted);
                var job = await Finished(app, retry["task_id"]!.GetValue<string>());
                Check.True(job.State == "completed" && job.Name == "Retry the update", "retry job: " + job.State + " " + job.Error);
                Check.True(UpgradeGuard.Flag(UpgradeGuard.Marker(new InstanceBackupStore(settings.DataDir, settings.SettingsConfigDir, app.Runtime.Database, ApplicationRuntime.Version)), "retry"), "retry armed");
                Check.Equal(2, Automatic(settings));
            }
            finally { await StopAsync(app); }
        }
        finally { SchemaMigrator.MigrationAssemblies = [typeof(SchemaMigrator).Assembly]; }

        // The fixed build (the failing migration gone) starts normally after the retry.
        app = await StartAsync(temp, settings);
        try
        {
            Check.True(!app.Runtime.SafeMode, "normal start after the retry with a fixed build");
            Check.True(UpgradeGuard.Marker(new InstanceBackupStore(settings.DataDir, settings.SettingsConfigDir, app.Runtime.Database, ApplicationRuntime.Version)) is null, "marker cleared");
            Check.Equal(before.Rows["entries"], BackupDatabases.Facts(catalog, integrity: false).Rows["entries"]);
        }
        finally { await StopAsync(app); }
    }

    // ---- routes ------------------------------------------------------------------------------------------------

    internal static readonly (string Method, string Path)[] RouteList =
    [
        ("GET", "/api/v1/backups"), ("POST", "/api/v1/backups"), ("GET", "/api/v1/backups/{backupId}/download"), ("DELETE", "/api/v1/backups/{backupId}"),
        ("POST", "/api/v1/backups/restore/preview"), ("POST", "/api/v1/backups/restore/apply"), ("DELETE", "/api/v1/backups/restore/pending"),
        ("POST", "/api/v1/backups/update-notice/dismiss"), ("POST", "/api/v1/backups/safe-mode/retry"), ("POST", "/api/v1/backups/safe-mode/migrate-without-backup"),
        ("GET", "/api/v1/maintenance/old-files"), ("POST", "/api/v1/maintenance/old-files"),
    ];

    private static MultipartFormDataContent Upload(byte[] file, string? passphrase)
    {
        var form = new MultipartFormDataContent();
        if (passphrase is not null) form.Add(new StringContent(passphrase), "passphrase");
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new("application/octet-stream");
        form.Add(content, "file", "backup.ascbackup");
        return form;
    }

    private static async Task Routes()
    {
        using var temp = new Sandbox();
        var settings = Settings(temp) with { AuthMethod = "forms" };
        var app = await StartAsync(temp, settings);
        try
        {
            var owner = app.Runtime.Access!.Accounts.CreateOwner("owner", "owner-password");
            var mapped = app.Host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Select(endpoint => (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET") + " " + endpoint.RoutePattern.RawText)
                .ToHashSet(StringComparer.Ordinal);
            var guest = app.Runtime.Access.Accounts.CreateUser("guest", "guest-password", Role.Guest);
            using var anonymous = app.Client();
            using var guestClient = app.Client(session: app.Runtime.Access.Accounts.IssueSession(guest.Id));
            foreach (var (method, path) in RouteList)
            {
                Check.True(mapped.Contains(method + " " + path), "mapped: " + method + " " + path);
                var concrete = path.Replace("{backupId}", "0123456789abcdef0123456789abcdef", StringComparison.Ordinal);
                Check.True(AccessPolicy.Required(method, concrete) == AccessLevel.Admin, "admin level: " + path);
                using (var response = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), concrete))) Check.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                using (var response = await guestClient.SendAsync(new HttpRequestMessage(new HttpMethod(method), concrete))) Check.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
            using var admin = app.Client(Phase11WiringTests.AdminToken);
            using (var old = await admin.GetAsync("/api/admin/instance/backups")) Check.Equal(HttpStatusCode.NotFound, old.StatusCode);

            var created = await Json(await admin.PostAsJsonAsync("/api/v1/backups", new { passphrase = Passphrase }), HttpStatusCode.Accepted);
            var task = await Finished(app, created["task_id"]!.GetValue<string>());
            Check.True(task.State == "completed", "create task: " + task.Error);
            Check.True(!app.Runtime.Tasks.Snapshot().ToJsonString().Contains(Passphrase, StringComparison.Ordinal), "the passphrase is never in the task list");
            using (var tooShort = await admin.PostAsJsonAsync("/api/v1/backups", new { passphrase = "short" })) Check.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
            var list = await Json(await admin.GetAsync("/api/v1/backups"), HttpStatusCode.OK);
            var item = list["items"]!.AsArray().Single()!.AsObject();
            Check.True(item["protected"]!.GetValue<bool>() && item["kind"]!.GetValue<string>() == "manual" && item["schema_matches_build"]!.GetValue<bool>(), "listed");
            var id = item["id"]!.GetValue<string>();
            var file = await admin.GetByteArrayAsync("/api/v1/backups/" + id + "/download");
            Check.True(BackupCrypto.Detect(file) == BackupContainer.Protected4, "download is the chunked protected file");

            // Audit 9: the passphrase travels in the body (multipart), never in a header.
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/backups/restore/preview") { Content = new ByteArrayContent(file) })
            {
                request.Content.Headers.ContentType = new("application/octet-stream");
                request.Headers.Add("X-Backup-Passphrase", Convert.ToBase64String(Encoding.UTF8.GetBytes(Passphrase)));
                using var response = await admin.SendAsync(request);
                Check.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
            using (var wrong = await admin.PostAsync("/api/v1/backups/restore/preview", Upload(file, "not the passphrase"))) Check.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            var preview = await Json(await admin.PostAsync("/api/v1/backups/restore/preview", Upload(file, Passphrase)), HttpStatusCode.OK);

            // Re-audit 9: with accounts, arming, retrying and migrating without a backup need an owner or admin account.
            foreach (var (path, body) in new (string, object)[]
            {
                ("/api/v1/backups/restore/apply", new { stage_id = preview["stage_id"]!.GetValue<string>(), preview_token = preview["preview_token"]!.GetValue<string>(), confirm = true }),
                ("/api/v1/backups/safe-mode/retry", new { confirm = true }), ("/api/v1/backups/safe-mode/migrate-without-backup", new { confirm = true, preview_token = "x" }),
            })
                using (var token = await admin.PostAsJsonAsync(path, body)) Check.True(token.StatusCode == HttpStatusCode.Forbidden, "admin token refused for " + path + ": " + (int)token.StatusCode);
            using var ownerClient = app.Client(session: app.Runtime.Access.Accounts.IssueSession(owner.Id));
            using (var stale = await ownerClient.PostAsJsonAsync("/api/v1/backups/restore/apply", new { stage_id = preview["stage_id"]!.GetValue<string>(), preview_token = "0000", confirm = true }))
                Check.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var armed = await Json(await ownerClient.PostAsJsonAsync("/api/v1/backups/restore/apply", new
            { stage_id = preview["stage_id"]!.GetValue<string>(), preview_token = preview["preview_token"]!.GetValue<string>(), confirm = true }), HttpStatusCode.OK);
            Check.True(armed["armed"]!.GetValue<bool>() && armed["restart_required"]!.GetValue<bool>() && !armed["restarting"]!.GetValue<bool>(), "armed, restart required");
            // Until the restart, changes are refused (audit 4/5/8); the backup routes stay open.
            using (var refused = await admin.PostAsJsonAsync("/api/v1/errors/clear", new { })) Check.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Check.True((await Json(await admin.GetAsync("/api/v1/backups"), HttpStatusCode.OK))["restore_pending"] is JsonObject, "pending restore listed");
            await Json(await admin.DeleteAsync("/api/v1/backups/restore/pending"), HttpStatusCode.OK);
            using (var allowed = await admin.PostAsJsonAsync("/api/v1/errors/clear", new { })) Check.Equal(HttpStatusCode.OK, allowed.StatusCode);
            preview = await Json(await admin.PostAsync("/api/v1/backups/restore/preview", Upload(file, Passphrase)), HttpStatusCode.OK);
            await Json(await ownerClient.PostAsJsonAsync("/api/v1/backups/restore/apply", new
            { stage_id = preview["stage_id"]!.GetValue<string>(), preview_token = preview["preview_token"]!.GetValue<string>(), confirm = true }), HttpStatusCode.OK);
        }
        finally { await StopAsync(app); }

        // The next start applies it before anything opens the databases.
        app = await StartAsync(temp, settings);
        try
        {
            using var admin = app.Client(Phase11WiringTests.AdminToken);
            var after = await Json(await admin.GetAsync("/api/v1/backups"), HttpStatusCode.OK);
            Check.True(after["restore"]?["status"]?.GetValue<string>() == "restored" && after["restore_pending"] is null, "restored at start: " + after["restore"]?.ToJsonString());
            Check.True(after["items"]!.AsArray().Any(row => row!["kind"]!.GetValue<string>() == "pre-restore"), "pre-restore backup kept");
            Check.True(app.Runtime.Tasks.Snapshot().ToJsonString().Contains("Restore at start", StringComparison.Ordinal), "restore in Tasks");
            await Json(await admin.PostAsync("/api/v1/backups/update-notice/dismiss", null), HttpStatusCode.OK);
            Check.True((await Json(await admin.GetAsync("/api/v1/backups"), HttpStatusCode.OK))["restore"] is null, "notice dismissed");
            var id = after["items"]!.AsArray().First(row => row!["kind"]!.GetValue<string>() == "manual")!["id"]!.GetValue<string>();
            await Json(await admin.DeleteAsync("/api/v1/backups/" + id), HttpStatusCode.OK);
            using (var gone = await admin.GetAsync("/api/v1/backups/" + id + "/download")) Check.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
        finally { await StopAsync(app); }
    }

    // ---- crash safety, streaming, config keys --------------------------------------------------------------------

    private static void SwapCrash()
    {
        using var temp = new Sandbox();
        var data = Path.Combine(temp.Path, "data");
        using var database = Populated(data);
        SqliteConnection.ClearAllPools();
        var config = Path.Combine(temp.Path, "config");
        WriteConfig(config, "en", SecretA, connection: false);
        var journalRoot = Path.Combine(data, "instance-backups");
        var original = (File.ReadAllBytes(database.Paths.Catalog), File.ReadAllBytes(database.Paths.Origins), File.ReadAllBytes(Path.Combine(config, "config.json")));
        using var other = Clean(Path.Combine(temp.Path, "other"));
        SqliteConnection.ClearAllPools();
        var incoming = new Dictionary<string, string?>(StringComparer.Ordinal) { ["catalog.sqlite"] = other.Paths.Catalog, ["chapter-origins.sqlite"] = other.Paths.Origins };

        // A failing check undoes the swap at once: the live files are byte-for-byte as before.
        Check.Throws<InvalidOperationException>(() => DatabaseSwap.Swap(journalRoot, database.Paths, config, incoming, "{}"u8.ToArray(), "test", () => throw new InvalidOperationException("check failed")));
        Check.True(original.Item1.SequenceEqual(File.ReadAllBytes(database.Paths.Catalog)) && original.Item2.SequenceEqual(File.ReadAllBytes(database.Paths.Origins))
            && original.Item3.SequenceEqual(File.ReadAllBytes(Path.Combine(config, "config.json"))), "undone byte-for-byte");

        // A crash after the catalogue was replaced but before the origins were (the audit's mixed state): the journal says
        // "aside", the catalogue is new, the origins file is gone. The next start undoes it before opening anything.
        Directory.CreateDirectory(journalRoot);
        File.WriteAllText(Path.Combine(journalRoot, DatabaseSwap.JournalFile), "{\"kind\":\"restore\",\"step\":\"aside\"}");
        foreach (var path in new[] { database.Paths.Catalog, database.Paths.Origins }) File.Move(path, path + ".swap-old");
        File.Copy(other.Paths.Catalog, database.Paths.Catalog);
        File.Copy(Path.Combine(config, "config.json"), Path.Combine(config, "config.json.swap-old"));
        File.WriteAllText(Path.Combine(config, "config.json"), "{}");
        var store = new InstanceBackupStore(data, config, database, "0.5.0-test");
        var result = store.ApplyAtStart()!;
        Check.True(result["status"]!.GetValue<string>() == "recovered" && result["message"]!.GetValue<string>().Contains("undone", StringComparison.Ordinal), "recovered: " + result.ToJsonString());
        Check.True(original.Item1.SequenceEqual(File.ReadAllBytes(database.Paths.Catalog)) && original.Item2.SequenceEqual(File.ReadAllBytes(database.Paths.Origins))
            && original.Item3.SequenceEqual(File.ReadAllBytes(Path.Combine(config, "config.json"))), "the mixed state was undone");
        Check.True(!File.Exists(Path.Combine(journalRoot, DatabaseSwap.JournalFile)), "journal removed");

        // After the check passed ("verified") a crash is finished, not undone.
        File.WriteAllText(Path.Combine(journalRoot, DatabaseSwap.JournalFile), "{\"kind\":\"restore\",\"step\":\"verified\"}");
        File.WriteAllText(database.Paths.Catalog + ".swap-old", "old");
        Check.True(DatabaseSwap.Recover(journalRoot, database.Paths, config)!.Contains("completed", StringComparison.Ordinal), "finished");
        Check.True(!File.Exists(database.Paths.Catalog + ".swap-old") && original.Item1.SequenceEqual(File.ReadAllBytes(database.Paths.Catalog)), "old copy removed, live kept");
    }

    private static void Crypto()
    {
        foreach (var size in new[] { 0, 10, BackupCrypto.ChunkSize, BackupCrypto.ChunkSize * 2 + 1234 })
        {
            using var temp = new Sandbox();
            var plain = new byte[size]; System.Security.Cryptography.RandomNumberGenerator.Fill(plain);
            var sealedFile = Path.Combine(temp.Path, "sealed");
            using (var input = new MemoryStream(plain)) using (var output = File.Create(sealedFile)) BackupCrypto.Encrypt(input, output, Passphrase);
            var opened = Path.Combine(temp.Path, "opened");
            BackupCrypto.Decrypt(sealedFile, opened, Passphrase);
            Check.True(plain.AsSpan().SequenceEqual(File.ReadAllBytes(opened)), "chunked round trip of " + size + " bytes");
            var bytes = File.ReadAllBytes(sealedFile);
            // Truncation (drop the last chunk) and appended data are refused.
            if (size > BackupCrypto.ChunkSize)
            {
                var truncated = Path.Combine(temp.Path, "truncated");
                File.WriteAllBytes(truncated, bytes[..(7 + 16 + 8 + 4 + BackupCrypto.ChunkSize + 16)]);
                Check.Throws<BackupRefusedException>(() => BackupCrypto.Decrypt(truncated, Path.Combine(temp.Path, "t-out"), Passphrase));
            }
            var appended = Path.Combine(temp.Path, "appended");
            File.WriteAllBytes(appended, [.. bytes, 1, 2, 3]);
            Check.Throws<BackupRefusedException>(() => BackupCrypto.Decrypt(appended, Path.Combine(temp.Path, "a-out"), Passphrase));
            Check.True(!File.Exists(Path.Combine(temp.Path, "a-out")), "no partial output left");
        }
        // The first phase 12 container (ASCBK3, one AES-GCM block) is still read.
        using (var temp = new Sandbox())
        {
            var plain = Encoding.UTF8.GetBytes("PK legacy container body");
            var salt = new byte[16]; var nonce = new byte[12];
            System.Security.Cryptography.RandomNumberGenerator.Fill(salt); System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
            var key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Passphrase), salt, BackupCrypto.Iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
            var cipher = new byte[plain.Length]; var tag = new byte[16];
            byte[] magic = [.. "ASCBK3\n"u8];
            using (var aes = new System.Security.Cryptography.AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, (byte[])[.. magic, .. salt]);
            var file = Path.Combine(temp.Path, "v3");
            File.WriteAllBytes(file, [.. magic, .. salt, .. nonce, .. cipher, .. tag]);
            BackupCrypto.Decrypt(file, Path.Combine(temp.Path, "v3-out"), Passphrase);
            Check.True(plain.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(temp.Path, "v3-out"))), "ASCBK3 still read");
        }
    }

    private static void UnknownConfigKeys()
    {
        using var temp = new Sandbox();
        var config = Path.Combine(temp.Path, "config");
        WriteConfig(config, "en", SecretA, connection: false);
        var path = Path.Combine(config, "config.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["future_section"] = new JsonObject { ["kept"] = true };
        node["settings"]!["values"]!["KOMF_BROWSER_URL"] = "http://komf.test";
        node["update"] = new JsonObject { ["app_version"] = "0.4.5", ["defaults"] = new JsonObject() };
        File.WriteAllText(path, node.ToJsonString());
        UpdateNotices.Check(config, "0.5.0-test"); // rewrites config.json (drops the retired link)
        var after = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Check.True(after["future_section"]?["kept"]?.GetValue<bool>() == true && after["settings"]!["values"]!["KOMF_BROWSER_URL"] is null, "unknown top-level key kept on rewrite");
        var clean = BackupSettings.Sanitize(new ConfigStore(config).ReadDocument()!);
        Check.True(clean.Document.Unknown is null && clean.NotIncluded.Contains("config.future_section"), "unknown keys never go into a backup");
        var merged = BackupSettings.Merge(clean.Document, new ConfigStore(config).ReadDocument());
        Check.True(merged.Document.Unknown?.ContainsKey("future_section") == true, "a restore keeps this instance's unknown keys");
    }

    // ---- SET-3 / SET-4 / SET-6 ---------------------------------------------------------------------------------

    private static void OldFiles()
    {
        using var temp = new Sandbox();
        var app = Path.Combine(temp.Path, "app"); var config = Path.Combine(temp.Path, "config"); var data = Path.Combine(temp.Path, "data");
        foreach (var folder in new[] { app, config, data, Path.Combine(app, "app", "__pycache__") }) Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(app, "UPGRADE-0.4.1.md"), "# AutoSourcarr 0.4.1 upgrade notes\n");
        File.WriteAllText(Path.Combine(app, "UPGRADE-0.9.0.md"), "# AutoSourcarr 0.9.0 upgrade notes\n"); // newer: kept
        File.WriteAllText(Path.Combine(app, "UPGRADE-0.3.0.md"), "# Something else 0.3.0\n"); // no product header: kept
        File.WriteAllText(Path.Combine(app, "README.md"), "# AutoSourcarr\n");
        File.WriteAllText(Path.Combine(app, "app", "__pycache__", "gone.cpython-312.pyc"), "x");
        File.WriteAllText(Path.Combine(app, "app", "kept.py"), "x");
        File.WriteAllText(Path.Combine(app, "app", "__pycache__", "kept.cpython-312.pyc"), "x");
        var stale = Path.Combine(config, ".config.json." + new string('a', 32) + ".tmp");
        File.WriteAllText(stale, "{}"); File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));
        var keyTemp = Path.Combine(config, ".config.key." + new string('c', 32) + ".tmp"); // a key file: never moved
        File.WriteAllText(keyTemp, "key"); File.SetLastWriteTimeUtc(keyTemp, DateTime.UtcNow.AddDays(-3));
        var fresh = Path.Combine(config, ".config.json." + new string('b', 32) + ".tmp");
        File.WriteAllText(fresh, "{}"); // still being written: kept
        File.WriteAllText(Path.Combine(config, "config.json"), "{}");
        File.WriteAllText(Path.Combine(config, "autosourcarr.env"), "A=1");
        var staged = Path.Combine(data, "instance-backups", "staged", new string('d', 32));
        Directory.CreateDirectory(staged); File.WriteAllText(Path.Combine(staged, "manifest.json"), "{}");
        Directory.SetLastWriteTimeUtc(staged, DateTime.UtcNow.AddDays(-2));
        var armed = Path.Combine(data, "instance-backups", "staged", new string('e', 32));
        Directory.CreateDirectory(armed); Directory.SetLastWriteTimeUtc(armed, DateTime.UtcNow.AddDays(-2));
        File.WriteAllText(Path.Combine(data, "instance-backups", InstanceBackupStore.PendingFile), "{\"stage_id\":\"" + new string('e', 32) + "\"}");
        var oldTrash = Path.Combine(data, "trash", DateTime.UtcNow.AddDays(-40).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(oldTrash); File.WriteAllText(Path.Combine(oldTrash, "x.md"), "x");
        var cleanup = new OldFileCleanup(() => app, config, data, data, "0.5.0");
        var preview = cleanup.Preview();
        var paths = preview["items"]!.AsArray().Select(row => row!["path"]!.GetValue<string>()).ToArray();
        Check.True(paths.SequenceEqual(new[] { "app/UPGRADE-0.4.1.md", "app/app/__pycache__/gone.cpython-312.pyc", "config/" + Path.GetFileName(stale),
            "data/instance-backups/staged/" + new string('d', 32), "data/trash/" + Path.GetFileName(oldTrash) }), "candidates: " + string.Join(",", paths));
        Check.True(preview["items"]!.AsArray().Last()!["action"]!.GetValue<string>() == "delete", "expired trash is deleted");
        Check.Throws<BackupRefusedException>(() => cleanup.Apply("stale-token"));
        cleanup.MarkPending("setup");
        Check.Equal("pending", cleanup.Last()!["status"]!.GetValue<string>());
        var result = cleanup.Apply(preview["preview_token"]!.GetValue<string>());
        Check.Equal(5, result["removed"]!.AsArray().Count);
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Check.True(File.Exists(Path.Combine(data, "trash", day, "app", "UPGRADE-0.4.1.md")) && !File.Exists(Path.Combine(app, "UPGRADE-0.4.1.md")), "moved to the dated trash");
        Check.True(!Directory.Exists(oldTrash) && !Directory.Exists(staged) && Directory.Exists(armed), "expired trash deleted, armed preview kept");
        foreach (var kept in new[] { Path.Combine(app, "UPGRADE-0.9.0.md"), Path.Combine(app, "UPGRADE-0.3.0.md"), Path.Combine(app, "README.md"), fresh, keyTemp,
                     Path.Combine(config, "config.json"), Path.Combine(config, "autosourcarr.env"), Path.Combine(app, "app", "__pycache__", "kept.cpython-312.pyc") })
            Check.True(File.Exists(kept), "kept: " + kept);
        Check.True(cleanup.Last()?["status"]?.GetValue<string>() == "completed", "the wizard's cleanup status");
        Check.Equal(0, cleanup.Preview()["items"]!.AsArray().Count);
    }

    private static void RetiredLinks()
    {
        foreach (var key in UpdateNotices.RetiredLinkSettings)
        {
            Check.True(!SettingsSchema.ByKey.ContainsKey(key) && !SetupFields.ByKey.ContainsKey(key), key + " removed from the schema and the wizard");
            Check.True(AutoSourcarrSettings.Definitions.All(definition => definition.Key != key), key + " is not a setting");
        }
        using var temp = new Sandbox();
        var document = new ConfigDocument { ManagedKeys = ["KOMF_BROWSER_URL", "SOURCE_LANGUAGES"] };
        document.Settings.Values["KOMF_BROWSER_URL"] = "http://komf.test"; document.Settings.Values["SOURCE_LANGUAGES"] = "en";
        document.Extra.Values["REPAIR_AI_BROWSER_URL"] = "http://ai.test";
        document.Update = new ConfigUpdateRecord { AppVersion = "0.4.5" };
        new ConfigStore(temp.Path).WriteDocument(document);
        var notice = UpdateNotices.Check(temp.Path, "0.5.0-test");
        var after = new ConfigStore(temp.Path).ReadDocument()!;
        Check.True(!after.Settings.Values.ContainsKey("KOMF_BROWSER_URL") && !after.Extra.Values.ContainsKey("REPAIR_AI_BROWSER_URL")
            && after.ManagedKeys.SequenceEqual(["SOURCE_LANGUAGES"]) && after.Settings.Values["SOURCE_LANGUAGES"] == "en", "dropped from config.json, the rest kept");
        Check.True(notice?.Items.Any(item => UpdateNotices.RetiredLinkSettings.Contains(item.Key)) != true, "dropped silently (no notice item)");
        // A Compose variable of that name is ignored.
        SettingsLoader.Load(environment: new Dictionary<string, string?> { ["KOMF_BROWSER_URL"] = "http://komf.test" });
    }

    private static async Task FullResetKeeps()
    {
        using var temp = new Sandbox();
        var settings = Settings(temp);
        WriteConfig(settings.SettingsConfigDir, "en", SecretA, connection: true);
        var configBytes = File.ReadAllBytes(Path.Combine(settings.SettingsConfigDir, "config.json"));
        var keyBytes = File.ReadAllBytes(Path.Combine(settings.SettingsConfigDir, "config.key"));
        var chapter = Path.Combine(settings.LibraryDir!, "Alpha", "Chapter 1.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(chapter)!);
        File.WriteAllBytes(chapter, [1, 2, 3, 4]);
        var app = await StartAsync(temp, settings);
        try
        {
            app.Runtime.Database.Write(DatabaseFile.Catalog, session => session.Execute(
                "INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(5,'Reset me','2026-01-01','2026-01-01')"));
            using var admin = app.Client(Phase11WiringTests.AdminToken);
            var preview = await Json(await admin.GetAsync("/api/v1/maintenance/full-reset"), HttpStatusCode.OK);
            var queued = await Json(await admin.PostAsJsonAsync("/api/v1/maintenance/full-reset", new { confirm = true, preview_token = preview["preview_token"]!.GetValue<string>() }), HttpStatusCode.Accepted);
            var task = await Finished(app, queued["task_id"]!.GetValue<string>());
            Check.True(task.State == "completed", "full reset: " + task.State + " " + task.Error);
            Check.Equal(0L, BackupDatabases.Facts(app.Runtime.Database.Paths.Catalog, integrity: false).Rows["entries"]);
            Check.True(configBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(settings.SettingsConfigDir, "config.json"))), "config.json unchanged by the full reset");
            Check.True(keyBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(settings.SettingsConfigDir, "config.key"))), "config.key unchanged by the full reset");
            Check.True(File.Exists(chapter) && File.ReadAllBytes(chapter).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "library files kept by the full reset");
        }
        finally { await StopAsync(app); }
    }
}
