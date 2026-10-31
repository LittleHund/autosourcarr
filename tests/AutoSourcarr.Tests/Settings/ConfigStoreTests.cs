using System.Text;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests;

public static class ConfigStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    // Values chosen to break naive converters: quotes, inline '#', spaces, Unicode, empty,
    // an unknown key, an unknown secret-looking key, and a key without '='.
    private const string Additions = """

        ADMIN_TOKEN="admin token # with hash and spaces"
        AUTOSOURCARR_READER_API_TOKEN='reader-Ωmega-🔑'
        TRANSLATION_API_KEY=
        SOURCE_LANGUAGES=en,ja,all
        CUSTOM_FROM_A_USER="kept as-is"
        MY_SERVICE_PASSWORD=hunter2
        DECLARED_WITHOUT_VALUE
        export EXPORTED_EXTRA=exported value
        """;

    public static void Register(TestSuite suite)
    {
        suite.Add("settings config: a 0.4.x env converts to config.json with no lost value", () =>
        {
            using var temp = new Sandbox();
            var root = Path.Combine(temp.Path, "config");
            Directory.CreateDirectory(root);
            var main = Encoding.UTF8.GetBytes(File.ReadAllText(Env("autosourcarr.env.example")) + Additions);
            var host = File.ReadAllBytes(Env("source-host.env.example"));
            File.WriteAllBytes(Path.Combine(root, EnvConversion.MainEnv), main);
            File.WriteAllBytes(Path.Combine(root, EnvConversion.SourceHostEnv), host);

            var report = EnvConversion.EnsureConverted(root, Now) ?? throw new InvalidOperationException("Expected a conversion.");
            var loaded = new ConfigStore(root).Load();

            // Every key that had a value is present with the identical text.
            var expectedMain = DotEnv.Parse(Encoding.UTF8.GetString(main));
            var known = AutoSourcarrSettings.Definitions.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in expectedMain.Where(pair => pair.Value is not null))
            {
                var section = known.Contains(key) ? loaded.Settings : loaded.Extra;
                Check.True(section.TryGetValue(key, out var stored) && stored == value, "Value changed or lost: " + key);
            }
            foreach (var (key, value) in DotEnv.Parse(Encoding.UTF8.GetString(host)).Where(pair => pair.Value is not null))
                Check.True(loaded.SourceHost.TryGetValue(key, out var stored) && stored == value, "Source-host value changed or lost: " + key);
            Check.Equal(expectedMain.Count(pair => pair.Value is not null), loaded.Settings.Count + loaded.Extra.Count);

            // Secrets are encrypted at rest; plain values are readable.
            var text = File.ReadAllText(Path.Combine(root, ConfigStore.FileName));
            foreach (var secret in new[] { "admin token # with hash and spaces", "reader-Ωmega-🔑", "hunter2" })
                Check.True(!text.Contains(secret, StringComparison.Ordinal), "A secret is stored in plain text.");
            Check.True(text.Contains("\"SOURCE_LANGUAGES\": \"en,ja,all\"", StringComparison.Ordinal), "Plain settings stay readable.");
            Check.True(report.EncryptedKeys.Contains("ADMIN_TOKEN") && report.EncryptedKeys.Contains("MY_SERVICE_PASSWORD"), "Report lists encrypted keys.");
            Check.True(report.ExtraKeys.Contains("CUSTOM_FROM_A_USER") && report.ExtraKeys.Contains("EXPORTED_EXTRA"), "Unknown keys are kept.");
            Check.True(report.KeysWithoutValue.Contains("DECLARED_WITHOUT_VALUE"), "Keys without a value are reported.");

            // Originals stay in place and a byte-identical copy is kept.
            Check.True(main.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, EnvConversion.MainEnv))), "autosourcarr.env must not change.");
            Check.True(main.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, report.BackupDirectory, EnvConversion.MainEnv))), "Backup must be identical.");
            Check.True(host.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, report.BackupDirectory, EnvConversion.SourceHostEnv))), "Backup must be identical.");

            // Conversion happens once.
            var before = File.ReadAllBytes(Path.Combine(root, ConfigStore.FileName));
            Check.True(EnvConversion.EnsureConverted(root, Now.AddDays(1)) is null, "A second start must not convert again.");
            Check.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, ConfigStore.FileName))), "config.json must not change on the second start.");
            if (!OperatingSystem.IsWindows())
            {
                Check.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, ConfigStore.FileName)));
                Check.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, SettingsSecrets.KeyFileName)));
            }
        });

        suite.Add("settings config: startup reads config.json below the container environment", () =>
        {
            using var temp = new Sandbox();
            var root = Path.Combine(temp.Path, "config");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, EnvConversion.MainEnv), "SOURCE_LANGUAGES=en,ja\nADMIN_TOKEN=from-config\n");
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["SETTINGS_CONFIG_DIR"] = root };

            var first = StartupConfigurationLoader.Load(temp.Path, environment, convertLegacyEnvironment: true, now: Now);
            Check.True(first.Conversion is not null, "The final host converts on its first start.");
            File.Delete(Path.Combine(root, EnvConversion.MainEnv)); // only config.json remains as input
            var fromConfig = StartupConfigurationLoader.Load(temp.Path, environment);
            Check.Equal("en,ja", fromConfig.Settings.SourceLanguages);
            Check.Equal("from-config", fromConfig.Settings.AdminToken);

            environment["SOURCE_LANGUAGES"] = "ko";
            Check.Equal("ko", StartupConfigurationLoader.Load(temp.Path, environment).Settings.SourceLanguages);
        });

        suite.Add("settings config: secrets are bound to their key and need config.key", () =>
        {
            using var temp = new Sandbox();
            var secrets = SettingsSecrets.Open(temp.Path, create: true);
            var sealedValue = secrets.Protect("ADMIN_TOKEN", "value");
            Check.Equal("value", SettingsSecrets.Open(temp.Path, create: false).Unprotect("ADMIN_TOKEN", sealedValue));
            Check.Throws<ConfigurationException>(() => secrets.Unprotect("TRANSLATION_API_KEY", sealedValue));
            var tampered = sealedValue[..^4] + (sealedValue[^4] == 'A' ? "B" : "A") + sealedValue[^3..];
            Check.Throws<ConfigurationException>(() => secrets.Unprotect("ADMIN_TOKEN", tampered));
            Check.True(SettingsSecrets.IsSecretKey("SOME_API_KEY") && SettingsSecrets.IsSecretKey("ADMIN_TOKEN") && !SettingsSecrets.IsSecretKey("SOURCE_LANGUAGES"));

            var store = new ConfigStore(temp.Path);
            store.WriteDocument(new ConfigDocument { Settings = ConfigStore.Encrypt(new Dictionary<string, string> { ["ADMIN_TOKEN"] = "x" }, secrets) });
            File.Delete(Path.Combine(temp.Path, SettingsSecrets.KeyFileName));
            var error = Check.Throws<ConfigurationException>(() => store.Load());
            Check.True(error.Message.Contains("config.key", StringComparison.Ordinal), error.Message);
        });

        suite.Add("settings config: newer or damaged config.json is refused unchanged", () =>
        {
            using var temp = new Sandbox();
            var path = Path.Combine(temp.Path, ConfigStore.FileName);
            File.WriteAllText(path, """{ "format": 2, "settings": { "values": {}, "secrets": {} } }""");
            Check.Throws<ConfigurationException>(() => new ConfigStore(temp.Path).Load());
            File.WriteAllText(path, "{ not json");
            Check.Throws<ConfigurationException>(() => new ConfigStore(temp.Path).Load());
            File.WriteAllText(path, """{ "format": 1, "settings": { "values": { "ADMIN_TOKEN": "plain" }, "secrets": {} } }""");
            Check.Throws<ConfigurationException>(() => new ConfigStore(temp.Path).Load());
            Check.Equal("""{ "format": 1, "settings": { "values": { "ADMIN_TOKEN": "plain" }, "secrets": {} } }""", File.ReadAllText(path));
        });

        suite.Add("settings config: change list names keys only, never values", () =>
        {
            var before = new ConfigValues(Values(("TZ", "UTC"), ("ADMIN_TOKEN", "old-secret")), Values(), Values(("GONE", "x")));
            var after = new ConfigValues(Values(("TZ", "Europe/Amsterdam"), ("ADMIN_TOKEN", "new-secret"), ("SOURCE_LANGUAGES", "en")), Values(), Values());
            var changes = ConfigChanges.Compare(before, after);
            Check.Equal("settings.ADMIN_TOKEN: changed (secret, value hidden)|settings.SOURCE_LANGUAGES: added|settings.TZ: changed|extra.GONE: removed",
                string.Join('|', changes.Select(change => change.ToString())));
            Check.True(!string.Join('|', changes).Contains("secret-", StringComparison.Ordinal));
            Check.Equal(0, ConfigChanges.Compare(after, after).Count);
        });
    }

    private static string Env(string name) => ConfigurationTests.Fixture(Path.Combine("env", name));

    private static Dictionary<string, string?> Values(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);
}
