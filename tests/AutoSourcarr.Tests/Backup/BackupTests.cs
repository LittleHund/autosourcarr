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
        suite.Add("phase 12 backup: a populated 0.4.4 instance restores on a clean instance (rows, settings minus secrets, profiles, provenance); no secret is in the file",
            RoundTrip);
        suite.Add("phase 12 backup: password-protected round trip; wrong or missing passphrase, tampered, newer-format and 0.4.x files are refused",
            Refusals);
        suite.Add("phase 12 update guard (SET-9): a failing migration rolls both databases back to the automatic backup unchanged; a good update keeps 3 automatic backups",
            GuardCore);
        suite.Add("phase 12 update rollback (SET-9): the app starts in a safe state with the rollback notice, the rollback is in Tasks, and the one-click rollback route restores the backup",
            () => GuardRuntime().GetAwaiter().GetResult());
        suite.Add("phase 12 backup routes: list/create/download/delete/preview/apply on /api/v1 with admin access, 202 jobs, preview token + confirm",
            () => Routes().GetAwaiter().GetResult());
        suite.Add("phase 12 old files (SET-3): only the app's own old reports, bytecode and temporary files move to a dated trash after a preview", OldFiles);
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
        // Hard rule 1: a wrong token changes nothing.
        Check.Throws<BackupRefusedException>(() => storeB.Apply(preview["stage_id"]!.GetValue<string>(), "wrong-token"));
        Check.Equal(0L, BackupDatabases.Facts(b.Paths.Catalog).Rows["entries"]);
        var result = storeB.Apply(preview["stage_id"]!.GetValue<string>(), preview["preview_token"]!.GetValue<string>());
        Check.True(result["restored"]!.GetValue<bool>() && storeB.Find(result["pre_restore_backup"]!.GetValue<string>())?.Kind == "pre-restore", "restored with a pre-restore backup");

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
        Check.True(Check.Throws<BackupRefusedException>(() => store.Stage(tampered, null, "upload")).Message.Contains("checksum", StringComparison.Ordinal), "tampered refused");
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
        var outcome = UpgradeGuard.Run(next, database, () =>
        {
            new SchemaMigrator(database).Initialize();
            Execute(database.Paths.Catalog, "INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(99,'Half migrated','2026-01-01','2026-01-01'); DELETE FROM manual_match_bindings;");
            throw new InvalidOperationException("migration 2026119999 failed");
        });
        Check.True(outcome.RolledBack && outcome.Backup?.Kind == "automatic" && outcome.Error!.Contains("2026119999", StringComparison.Ordinal), "rolled back from the automatic backup");
        Check.True(before.Item1.SameData(BackupDatabases.Facts(database.Paths.Catalog)) && before.Item2.SameData(BackupDatabases.Facts(database.Paths.Origins)),
            "same stamp, migration and row counts after the rollback");
        Check.True(BackupDatabases.Facts(database.Paths.Catalog).Intact && BackupDatabases.Facts(database.Paths.Origins).Intact, "intact after the rollback");
        Check.True(catalogDump.SequenceEqual(Compared.Select(table => Dump(database.Paths.Catalog, table))), "same rows after the rollback");
        var notice = UpgradeGuard.Notice(next)!;
        Check.True(notice["status"]!.GetValue<string>() == "rolled-back" && notice["backup_id"]!.GetValue<string>() == outcome.Backup!.Id, "rollback notice");
        Check.Equal("0.5.0-test", UpgradeGuard.ReadState(next)["last_opened_version"]!.GetValue<string>());

        // The fixed version updates; every update keeps an automatic backup, the newest 3 stay.
        foreach (var version in new[] { "0.5.1-test", "0.5.2-test", "0.5.3-test", "0.5.4-test" })
        {
            var store2 = new InstanceBackupStore(data, config, database, version);
            Check.Equal("updated", UpgradeGuard.Run(store2, database, () => new SchemaMigrator(database).Initialize()).Status);
        }
        Check.Equal(InstanceBackupStore.AutomaticKeep, store.List().Count(row => row.Kind == "automatic"));
        Check.True(UpgradeGuard.Dismiss(store) && UpgradeGuard.Notice(store) is null, "Keep current dismisses the notice");
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

    private static async Task GuardRuntime()
    {
        using var temp = new Sandbox();
        var settings = Settings(temp);
        var app = await StartAsync(temp, settings);
        app.Runtime.Database.Write(DatabaseFile.Catalog, session => session.Execute(
            "INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(7,'Kept entry','2026-01-01','2026-01-01')"));
        var store = new InstanceBackupStore(settings.DataDir, settings.SettingsConfigDir, app.Runtime.Database, ApplicationRuntime.Version);
        await StopAsync(app);
        var entries = BackupDatabases.Facts(Path.Combine(settings.DataDir, "catalog.sqlite")).Rows["entries"];

        // Simulate an update: the data was last opened by an older version, and the new migration fails.
        var state = UpgradeGuard.ReadState(store);
        state["last_opened_version"] = "0.4.9-older";
        UpgradeGuard.WriteState(store, state);
        UpgradeGuard.FaultAfterMigrate = database =>
        {
            database.Write(DatabaseFile.Catalog, session => session.Execute("DELETE FROM entries"));
            throw new InvalidOperationException("injected migration failure");
        };
        try { app = await StartAsync(temp, settings); }
        finally { UpgradeGuard.FaultAfterMigrate = null; }
        try
        {
            Check.True(app.Runtime.Upgrade?.RolledBack == true, "the start rolled back");
            Check.Equal(entries, BackupDatabases.Facts(app.Runtime.Database.Paths.Catalog, integrity: false).Rows["entries"]);
            var tasks = app.Runtime.Tasks.Snapshot().ToJsonString();
            Check.True(tasks.Contains("Automatic backup before update", StringComparison.Ordinal) && tasks.Contains("Update rolled back", StringComparison.Ordinal), "recorded in Tasks");
            using var admin = app.Client(Phase11WiringTests.AdminToken);
            var list = await Json(await admin.GetAsync("/api/v1/backups"), HttpStatusCode.OK);
            var update = list["update"]!.AsObject();
            Check.True(update["status"]!.GetValue<string>() == "rolled-back" && update["from_version"]!.GetValue<string>() == "0.4.9-older", "notice in System → Backups");
            var backupId = update["backup_id"]!.GetValue<string>();
            Check.True(list["items"]!.AsArray().Any(row => row!["id"]!.GetValue<string>() == backupId && row["kind"]!.GetValue<string>() == "automatic"), "automatic backup listed");

            // One-click rollback to that backup: preview (token) then confirmed apply.
            app.Runtime.Database.Write(DatabaseFile.Catalog, session => session.Execute("INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(8,'After','2026-01-01','2026-01-01')"));
            var preview = await Json(await admin.PostAsJsonAsync("/api/v1/backups/restore/preview", new { backup_id = backupId }), HttpStatusCode.OK);
            using (var refused = await admin.PostAsJsonAsync("/api/v1/backups/restore/apply", new { stage_id = preview["stage_id"]!.GetValue<string>(), preview_token = preview["preview_token"]!.GetValue<string>() }))
                Check.Equal(HttpStatusCode.BadRequest, refused.StatusCode); // no confirm
            var queued = await Json(await admin.PostAsJsonAsync("/api/v1/backups/restore/apply", new
            { stage_id = preview["stage_id"]!.GetValue<string>(), preview_token = preview["preview_token"]!.GetValue<string>(), confirm = true }), HttpStatusCode.Accepted);
            var task = await Finished(app, queued["task_id"]!.GetValue<string>());
            Check.True(task.State == "completed", "rollback task: " + task.State + " " + task.Error);
            Check.Equal(entries, BackupDatabases.Facts(app.Runtime.Database.Paths.Catalog, integrity: false).Rows["entries"]);
            var dismissed = await Json(await admin.PostAsync("/api/v1/backups/update-notice/dismiss", null), HttpStatusCode.OK);
            Check.True(dismissed["dismissed"]!.GetValue<bool>(), "Keep current");
            Check.True((await Json(await admin.GetAsync("/api/v1/backups"), HttpStatusCode.OK))["update"] is null, "notice gone");
        }
        finally { await StopAsync(app); }
    }

    // ---- routes ------------------------------------------------------------------------------------------------

    internal static readonly (string Method, string Path)[] RouteList =
    [
        ("GET", "/api/v1/backups"), ("POST", "/api/v1/backups"), ("GET", "/api/v1/backups/{backupId}/download"), ("DELETE", "/api/v1/backups/{backupId}"),
        ("POST", "/api/v1/backups/restore/preview"), ("POST", "/api/v1/backups/restore/apply"), ("POST", "/api/v1/backups/update-notice/dismiss"),
        ("GET", "/api/v1/maintenance/old-files"), ("POST", "/api/v1/maintenance/old-files"),
    ];

    private static async Task Routes()
    {
        using var temp = new Sandbox();
        var settings = Settings(temp) with { AuthMethod = "forms" };
        var app = await StartAsync(temp, settings);
        try
        {
            app.Runtime.Access!.Accounts.CreateOwner("owner", "owner-password");
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
            // The 0.4.5 /api/admin/instance routes are gone (phase 12 moved them).
            using var admin = app.Client(Phase11WiringTests.AdminToken);
            using (var old = await admin.GetAsync("/api/admin/instance/backups")) Check.Equal(HttpStatusCode.NotFound, old.StatusCode);

            var created = await Json(await admin.PostAsJsonAsync("/api/v1/backups", new { passphrase = Passphrase }), HttpStatusCode.Accepted);
            var task = await Finished(app, created["task_id"]!.GetValue<string>());
            Check.True(task.State == "completed", "create task: " + task.Error);
            Check.True(!app.Runtime.Tasks.Snapshot().ToJsonString().Contains(Passphrase, StringComparison.Ordinal), "the passphrase is never in the task list");
            using (var tooShort = await admin.PostAsJsonAsync("/api/v1/backups", new { passphrase = "short" })) Check.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
            var list = await Json(await admin.GetAsync("/api/v1/backups"), HttpStatusCode.OK);
            var item = list["items"]!.AsArray().Single()!.AsObject();
            Check.True(item["protected"]!.GetValue<bool>() && item["kind"]!.GetValue<string>() == "manual", "listed as a protected manual backup");
            var id = item["id"]!.GetValue<string>();
            var file = await admin.GetByteArrayAsync("/api/v1/backups/" + id + "/download");
            Check.True(BackupCrypto.IsProtected(file), "download is the protected file");

            // Upload preview with the passphrase header; wrong passphrase answers 400.
            using (var wrong = new ByteArrayContent(file))
            {
                wrong.Headers.ContentType = new("application/octet-stream");
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/backups/restore/preview") { Content = wrong };
                request.Headers.Add("X-Backup-Passphrase", Convert.ToBase64String(Encoding.UTF8.GetBytes("not the passphrase")));
                using var response = await admin.SendAsync(request);
                Check.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
            JsonObject preview;
            using (var content = new ByteArrayContent(file))
            {
                content.Headers.ContentType = new("application/octet-stream");
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/backups/restore/preview") { Content = content };
                request.Headers.Add("X-Backup-Passphrase", Convert.ToBase64String(Encoding.UTF8.GetBytes(Passphrase)));
                preview = await Json(await admin.SendAsync(request), HttpStatusCode.OK);
            }
            using (var stale = await admin.PostAsJsonAsync("/api/v1/backups/restore/apply", new { stage_id = preview["stage_id"]!.GetValue<string>(), preview_token = "0000", confirm = true }))
            {
                var stalled = await Json(stale, HttpStatusCode.Accepted);
                var failed = await Finished(app, stalled["task_id"]!.GetValue<string>());
                Check.True(failed.State == "failed", "a wrong token fails the job and changes nothing");
            }
            var applied = await Json(await admin.PostAsJsonAsync("/api/v1/backups/restore/apply", new
            { stage_id = preview["stage_id"]!.GetValue<string>(), preview_token = preview["preview_token"]!.GetValue<string>(), confirm = true }), HttpStatusCode.Accepted);
            var restoreTask = await Finished(app, applied["task_id"]!.GetValue<string>());
            Check.True(restoreTask.State == "completed", "restore task: " + restoreTask.Error);
            var after = await Json(await admin.GetAsync("/api/v1/backups"), HttpStatusCode.OK);
            Check.True(after["items"]!.AsArray().Any(row => row!["kind"]!.GetValue<string>() == "pre-restore"), "pre-restore backup kept");
            await Json(await admin.DeleteAsync("/api/v1/backups/" + id), HttpStatusCode.OK);
            using (var gone = await admin.GetAsync("/api/v1/backups/" + id + "/download")) Check.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
        finally { await StopAsync(app); }
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
        var fresh = Path.Combine(config, ".config.json." + new string('b', 32) + ".tmp");
        File.WriteAllText(fresh, "{}"); // still being written: kept
        File.WriteAllText(Path.Combine(config, "config.json"), "{}");
        File.WriteAllText(Path.Combine(config, "autosourcarr.env"), "A=1");
        var cleanup = new OldFileCleanup(() => app, config, data, data, "0.5.0");
        var preview = cleanup.Preview();
        var paths = preview["items"]!.AsArray().Select(row => row!["path"]!.GetValue<string>()).ToArray();
        Check.True(paths.SequenceEqual(new[] { "app/UPGRADE-0.4.1.md", "app/app/__pycache__/gone.cpython-312.pyc", "config/" + Path.GetFileName(stale) }), "candidates: " + string.Join(",", paths));
        Check.Throws<BackupRefusedException>(() => cleanup.Apply("stale-token"));
        var result = cleanup.Apply(preview["preview_token"]!.GetValue<string>());
        Check.Equal(3, result["removed"]!.AsArray().Count);
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Check.True(File.Exists(Path.Combine(data, "trash", day, "app", "UPGRADE-0.4.1.md")) && !File.Exists(Path.Combine(app, "UPGRADE-0.4.1.md")), "moved to the dated trash, not deleted");
        foreach (var kept in new[] { Path.Combine(app, "UPGRADE-0.9.0.md"), Path.Combine(app, "UPGRADE-0.3.0.md"), Path.Combine(app, "README.md"), fresh,
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
