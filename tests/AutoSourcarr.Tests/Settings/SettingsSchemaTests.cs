using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests;

public static class SettingsSchemaTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("settings schema: every setting has group, description, type, Compose variable; wizard fields keep their step", () =>
        {
            Check.Equal(AutoSourcarrSettings.Definitions.Count, SettingsSchema.Entries.Count);
            foreach (var entry in SettingsSchema.Entries)
            {
                Check.True(entry.Group.Length > 0 && entry.Description.Length > 10 && entry.Label.Length > 0 && entry.Type.Length > 0, entry.Key);
                Check.Equal(entry.Key, entry.ComposeVariable);
                Check.True(!entry.Secret || entry.Default is null, "Secret defaults are never shown: " + entry.Key);
                if (SetupFields.ByKey.ContainsKey(entry.Key))
                    Check.True(entry.WizardStep is { Length: > 0 }, "Wizard step for " + entry.Key);
            }
            Check.Equal("EXTENSION_STORES", string.Join(',', SettingsSchema.WizardOnlyKeys));
            Check.True(SettingsSchema.ByKey["TZ"].ComposeRecommended && !SettingsSchema.ByKey["BROWSE_PAGE_SIZE"].ComposeRecommended);
            Check.Equal("0.94", SettingsSchema.ByKey["AUTO_MATCH_THRESHOLD"].Default);
            Check.True(SettingsSchema.ByKey["ADMIN_TOKEN"].Secret && SettingsSchema.ByKey["ADMIN_TOKEN"].Type == "secret");
            var markdown = SettingsSchema.Markdown();
            Check.True(markdown.Contains("| `TZ` |", StringComparison.Ordinal) && markdown.Contains("## Compose overrides", StringComparison.Ordinal));
            Check.True(markdown.Split('\n').Count(line => line.StartsWith("| `", StringComparison.Ordinal)) == SettingsSchema.Entries.Count, "One row per setting.");
        });

        suite.Add("settings editor: values are normalized by type and limits before anything is written", () =>
        {
            static string? N(string key, string json) => SettingsEditor.Normalize(SettingsSchema.ByKey[key], JsonDocument.Parse(json).RootElement);
            Check.Equal("true", N("AUTOMATIC_DUPLICATE_MATCHING", "true"));
            Check.Equal("false", N("BACKGROUND_DEDUP_ON_STARTUP", "\"off\""));
            Check.Equal("75", N("BROWSE_PAGE_SIZE", "75"));
            Check.Equal("75", N("BROWSE_PAGE_SIZE", "\"75\""));
            Check.Throws<SettingsValidationException>(() => N("BROWSE_PAGE_SIZE", "1.5"));
            Check.Throws<SettingsValidationException>(() => N("BROWSE_PAGE_SIZE", "501"));
            Check.Equal("0.97", N("AUTO_MATCH_THRESHOLD", "0.97"));
            Check.Throws<SettingsValidationException>(() => N("AUTO_MATCH_THRESHOLD", "0.9"));
            Check.Equal("http://suwayomi:4567", N("MAIN_SUWAYOMI_URL", "\" http://suwayomi:4567/ \""));
            Check.Throws<SettingsValidationException>(() => N("MAIN_SUWAYOMI_URL", "\"http://user:pass@suwayomi\""));
            Check.Throws<SettingsValidationException>(() => N("MAIN_SUWAYOMI_URL", "\"http://suwayomi/?token=1\""));
            Check.Equal("en,ja", N("SOURCE_LANGUAGES", "\"EN, ja, en\""));
            Check.Throws<SettingsValidationException>(() => N("METADATA_IDENTITY_PROVIDERS", "\"mangadex,unknown\""));
            Check.Throws<SettingsValidationException>(() => N("REPAIR_AI_PROVIDER", "\"somebody\""));
            Check.Throws<SettingsValidationException>(() => N("AUTOSOURCARR_READER_API_TOKEN", "\"short\""));
            Check.Throws<SettingsValidationException>(() => N("APP_SERVICES_MODE", "\"sideways\""));
            Check.Throws<SettingsValidationException>(() => N("TZ", "\"line\\nbreak\""));
            Check.Equal(null, N("TZ", "null"));
        });

        suite.Add("settings editor: one transaction saves once; stale revisions and combination rules refuse without writing", () =>
        {
            using var temp = new Sandbox();
            var running = new AutoSourcarrSettings { AppServicesMode = "external" };
            var editor = new SettingsEditor(temp.Path, () => running, new Dictionary<string, string?> { ["TZ"] = "UTC" });
            static Dictionary<string, JsonElement> Changes(string json) =>
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
            var first = editor.Revision();
            var transaction = Guid.NewGuid().ToString();
            var saved = editor.Save(Changes("""{"TZ":"Europe/Berlin","FUZZY_REVIEW_THRESHOLD":0.8}"""), first, transaction);
            Check.Equal("FUZZY_REVIEW_THRESHOLD,TZ", string.Join(',', saved.ChangedKeys));
            Check.Equal("TZ", string.Join(',', saved.OverriddenByEnvironment));
            Check.Equal("settings.FUZZY_REVIEW_THRESHOLD: added|settings.TZ: added", string.Join('|', saved.Changes));
            var again = editor.Save(Changes("""{"TZ":"Europe/Berlin"}"""), first, transaction);
            Check.Equal(saved.Revision, again.Revision);
            Check.Equal(transaction, editor.SetupState()!.Transaction);
            Check.Throws<SettingsConflictException>(() => editor.Save(Changes("""{"TZ":"UTC"}"""), first));
            var revision = editor.Revision();
            Check.Throws<SettingsValidationException>(() => editor.Save(Changes("""{"FUZZY_REVIEW_THRESHOLD":0.99}"""), revision));
            Check.Throws<SettingsValidationException>(() => editor.Save(Changes("""{"REPAIR_AI_PROVIDER":"openai"}"""), revision));
            Check.Throws<SettingsValidationException>(() => editor.Save(Changes("""{"BACKGROUND_SOURCE_CONCURRENCY":8}"""), revision));
            Check.Equal(revision, editor.Revision());
            var unchanged = editor.Save(Changes("""{"TZ":"Europe/Berlin"}"""), revision);
            Check.Equal(0, unchanged.ChangedKeys.Count);
        });

        suite.Add("settings startup: once config.json exists, old env-file wizard values no longer override it", () =>
        {
            using var temp = new Sandbox();
            var root = Path.Combine(temp.Path, "config");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "autosourcarr.env"), "TZ=Asia/Tokyo\n");
            File.WriteAllText(Path.Combine(root, "setup-state.json"), """{"completed":true,"managed_keys":["TZ"]}""");
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["SETTINGS_CONFIG_DIR"] = root };
            Check.Equal("Asia/Tokyo", StartupConfigurationLoader.Load(temp.Path, environment).Settings.Tz);
            var editor = new SettingsEditor(root, () => new AutoSourcarrSettings(), environment);
            editor.Save(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"TZ":"Europe/Lisbon"}""")!, editor.Revision());
            Check.Equal("Europe/Lisbon", StartupConfigurationLoader.Load(temp.Path, environment).Settings.Tz);
        });

        suite.Add("settings update notice: changed defaults, removed and now-invalid settings are listed by key only", () =>
        {
            using var temp = new Sandbox();
            var store = new ConfigStore(temp.Path);
            Check.True(UpdateNotices.Check(temp.Path, "0.5.0") is null, "No config.json: nothing to report.");
            var defaults = SettingsSchema.Entries.Where(entry => !entry.Secret).ToDictionary(entry => entry.Key, entry => entry.Default ?? "", StringComparer.Ordinal);
            defaults["BROWSE_PAGE_SIZE"] = "40";
            defaults["AUTO_MATCH_THRESHOLD"] = "0.90";
            store.WriteDocument(new ConfigDocument
            {
                Settings = new ConfigSection { Values = { ["AUTO_MATCH_THRESHOLD"] = "0.97", ["OLD_SETTING"] = "x", ["SOURCE_ENGINE_MODE"] = "sideways" } },
                Update = new ConfigUpdateRecord { AppVersion = "0.4.4", Defaults = defaults }
            });
            var notice = UpdateNotices.Check(temp.Path, "0.5.0") ?? throw new InvalidOperationException("Expected a notice.");
            Check.Equal("BROWSE_PAGE_SIZE:default_changed|OLD_SETTING:removed|SOURCE_ENGINE_MODE:must_change",
                string.Join('|', notice.Items.Select(item => item.Key + ":" + item.Kind)));
            Check.True(notice.Items.All(item => !item.Message.Contains("sideways", StringComparison.Ordinal)), "Saved values never appear.");
            Check.True(UpdateNotices.Check(temp.Path, "0.5.0") is null, "Same version: no new notice.");
            Check.Equal("0.4.4", UpdateNotices.Current(temp.Path)!.FromVersion);
            Check.True(UpdateNotices.Dismiss(temp.Path) && UpdateNotices.Current(temp.Path) is null);
            Check.Equal("0.97", store.Load().Settings["AUTO_MATCH_THRESHOLD"]);
        });

        suite.Add("settings reader check: same codes as the 0.4.x reader build contract", () =>
        {
            var token = new string('a', 40);
            var ok = ReaderConfigCheck.Evaluate(new Dictionary<string, string?> { ["AUTOSOURCARR_READER_API_TOKEN"] = token, ["AUTOSOURCARR_PUBLIC_URL"] = "https://manga.example.org" });
            Check.True(ok.Ready && ok.ExitCode == 0 && ok.Errors.Count == 0);
            var missing = ReaderConfigCheck.Evaluate(new Dictionary<string, string?>());
            Check.Equal("12,14", string.Join(',', missing.Errors.Select(error => error.Code)));
            var docker = ReaderConfigCheck.Evaluate(new Dictionary<string, string?> { ["AUTOSOURCARR_READER_API_TOKEN"] = token, ["AUTOSOURCARR_PUBLIC_URL"] = "https://autosourcarr" });
            Check.Equal(11, docker.ExitCode);
            var quoted = ReaderConfigCheck.Evaluate(new Dictionary<string, string?> { ["AUTOSOURCARR_READER_API_TOKEN"] = token, ["AUTOSOURCARR_PUBLIC_URL"] = "https://x.org/$HOME" });
            Check.Equal(14, quoted.ExitCode);
            var port = ReaderConfigCheck.Evaluate(new Dictionary<string, string?> { ["AUTOSOURCARR_READER_API_TOKEN"] = token, ["AUTOSOURCARR_PUBLIC_URL"] = "https://manga.example.org", ["AUTOSOURCARR_INTERNAL_URL"] = "http://autosourcarr:9000" });
            Check.True(port.Ready && port.Warnings.Count == 1);
        });

        suite.Add("status stats and runtime metrics: counts from both databases, bounded timing samples", () =>
        {
            using var temp = new Sandbox();
            var settings = new AutoSourcarrSettings { DataDir = temp.Path };
            using var database = new SqliteDatabase(settings);
            new SchemaMigrator(database).Initialize();
            database.Write(DatabaseFile.Catalog, session => session.Execute(
                "INSERT INTO entries(id, preferred_title, status, created_at, updated_at) VALUES (1,'A',1,'t','t'),(2,'B',2,'t','t')"));
            Directory.CreateDirectory(Path.Combine(temp.Path, "cover-cache"));
            File.WriteAllBytes(Path.Combine(temp.Path, "cover-cache", "1.img"), new byte[10]);
            var stats = new CatalogueStatusStats(database, () => settings).SnapshotAsync().GetAwaiter().GetResult();
            Check.Equal(2L, stats["catalog"]!["entries"]!.GetValue<long>());
            Check.Equal(2L, stats["catalog"]!["unmatched_entries"]!.GetValue<long>());
            Check.Equal(1L, stats["catalog"]!["publication_statuses"]!["Ongoing"]!.GetValue<long>());
            Check.Equal(10L, stats["cache"]!["cover_bytes"]!.GetValue<long>());
            Check.Equal(0L, stats["chapters"]!["virtual_chapters"]!.GetValue<long>());
            Check.True(stats["storage"]!["database_bytes"]!.GetValue<long>() > 0);

            var metrics = new RuntimeMetrics();
            for (var index = 0; index < 200; index++)
                using (metrics.Phase("projection")) { }
            using (metrics.Phase("quality_decode", 100))
                Check.Equal(100L, metrics.Snapshot().Decode.EncodedBytes);
            var row = metrics.Snapshot().Phases.Single(phase => phase.Phase == "projection");
            Check.True(row.Count == 200 && row.Samples == RuntimeMetrics.SamplesPerPhase && row.P50Ms is not null);
            Check.Equal(0L, metrics.Snapshot().Decode.Active);
            Check.Throws<ArgumentException>(() => metrics.Phase("https://example.org"));
        });
    }
}
