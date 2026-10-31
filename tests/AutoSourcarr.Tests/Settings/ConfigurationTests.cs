using System.Globalization;
using System.Text.Json;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Tests;

public static class ConfigurationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("settings: all149 released defaults", () =>
        {
            using var expected = JsonDocument.Parse(File.ReadAllText(Fixture("settings-defaults.json")));
            var actual = SettingsLoader.ToEnvironment(SettingsLoader.Load(), includeSecrets: true);
            Check.Equal(154, AutoSourcarrSettings.Definitions.Count);
            foreach (var field in expected.RootElement.EnumerateObject())
            {
                if (field.Value.ValueKind == JsonValueKind.Null)
                {
                    Check.True(!actual.ContainsKey(field.Name));
                    continue;
                }
                Check.True(actual.ContainsKey(field.Name), "Missing setting " + field.Name);
                if (field.Value.ValueKind == JsonValueKind.Number)
                    Check.Equal(field.Value.GetDecimal(), decimal.Parse(actual[field.Name], CultureInfo.InvariantCulture));
                else
                    Check.Equal(field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString()! : field.Value.GetRawText(), actual[field.Name]);
            }
        });
        suite.Add("settings: canonical values override invalid stale injections", () =>
        {
            var settings = SettingsLoader.Load(
                dotenv: new Dictionary<string, string?> { ["BROWSE_PAGE_SIZE"] = "20" },
                environment: new Dictionary<string, string?> { ["browse_page_size"] = "30", ["SOURCE_ENGINE_MODE"] = "stale-invalid" },
                managedOverrides: new Dictionary<string, string?> { ["BROWSE_PAGE_SIZE"] = "40", ["SOURCE_ENGINE_MODE"] = "integrated" });
            Check.Equal(40, settings.BrowsePageSize);
            Check.Equal("http://127.0.0.1:4567", settings.EffectiveSourceUrl);
            Check.Equal("integrated", SettingsLoader.Load(bundledImage: true).AppServicesMode);
        });
        suite.Add("settings: secret-safe serialization and validation", () =>
        {
            var settings = SettingsLoader.Load(environment: new Dictionary<string, string?>
            {
                ["ADMIN_TOKEN"] = "sentinel-admin-secret", ["REPAIR_AI_API_KEY"] = "sentinel-provider-secret",
                ["MAIN_SUWAYOMI_PASSWORD"] = "sentinel-reader-password"
            });
            Check.True(!settings.ToString().Contains("sentinel", StringComparison.Ordinal));
            Check.True(!JsonSerializer.Serialize(settings).Contains("sentinel", StringComparison.Ordinal));
            Check.True(!SettingsLoader.ToEnvironment(settings).ContainsKey("ADMIN_TOKEN"));
            var error = Check.Throws<ConfigurationException>(() => SettingsLoader.Load(environment:
                new Dictionary<string, string?> { ["HTTP_MAX_CONNECTIONS"] = "sentinel-secret-in-wrong-field" }));
            Check.True(!error.Message.Contains("sentinel", StringComparison.Ordinal));
        });
        suite.Add("settings: lists, booleans and integral compatibility", () =>
        {
            var settings = SettingsLoader.Load(environment: new Dictionary<string, string?>
            {
                ["CONTINUOUS_CATALOG_MODES"] = "invalid, latest, popular", ["SOURCE_LANGUAGES"] = "EN, ja, EN",
                ["BROWSE_PAGE_SIZE"] = "50.0", ["SOURCE_AUTO_CHALLENGES"] = "OFF"
            });
            Check.Equal("LATEST,POPULAR", string.Join(',', settings.ContinuousCatalogModesList));
            Check.True(settings.AllowedLanguages.SetEquals(["en", "ja"]));
            Check.Equal(50, settings.BrowsePageSize);
            Check.True(!settings.SourceAutoChallenges);
        });
        suite.Add("storage paths: missing separate mount cannot create empty replacements", () =>
        {
            using var temp = new Sandbox();
            var data = Path.Combine(temp.Path, "primary");
            var separate = Path.Combine(temp.Path, "missing-mounted-dataset");
            Check.Throws<ConfigurationException>(() => DatabasePaths.Resolve(new() { DataDir = data, DatabaseDir = separate }));
            Check.True(!Directory.Exists(separate));
            Directory.CreateDirectory(separate);
            File.WriteAllBytes(Path.Combine(separate, "catalog.sqlite"), []);
            Check.Throws<ConfigurationException>(() => DatabasePaths.Resolve(new() { DataDir = data, DatabaseDir = separate }));
            Check.True(!File.Exists(Path.Combine(separate, "chapter-origins.sqlite")));
        });
    }

    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
