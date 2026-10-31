using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests;

public static class ConfigurationRecoveryTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("configuration: dotenv matches released parser fixtures", () =>
        {
            using var fixtures = JsonDocument.Parse(File.ReadAllText(ConfigurationTests.Fixture("dotenv-contract.json")));
            foreach (var fixture in fixtures.RootElement.EnumerateArray())
                Check.JsonEqual(fixture.GetProperty("expected"), JsonSerializer.SerializeToElement(DotEnv.Parse(fixture.GetProperty("input").GetString()!)));
        });
        suite.Add("configuration: uncommitted save restores all files and pins effective values", () =>
        {
            using var temp = new Sandbox();
            var files = new DurableConfigurationFiles(temp.Path);
            var old = Before(files);
            var transaction = Guid.NewGuid().ToString();
            Journal(files, old, transaction, new JsonObject { ["MAIN_SUWAYOMI_URL"] = "http://effective-before:30058" });
            files.Write("autosourcarr.env", Encoding.UTF8.GetBytes("MAIN_SUWAYOMI_URL=http://partial-save:30058\n"));
            files.Write("source-host.env", Encoding.UTF8.GetBytes("EXTENSION_STORES=[]\n"));
            var store = new CanonicalConfiguration(temp.Path);
            var restored = store.StartupOverrides();
            Check.Equal("http://effective-before:30058", restored["MAIN_SUWAYOMI_URL"]);
            Check.Equal("http://effective-before:30058", store.EnvironmentFile("autosourcarr.env")["MAIN_SUWAYOMI_URL"]);
            Check.True(old["source-host.env"]!.SequenceEqual(files.Read("source-host.env")!));
            Check.True(store.State()["managed_keys"]!.AsArray().Any(key => key!.GetValue<string>() == "MAIN_SUWAYOMI_URL"));
            Check.True(files.Read(DurableConfigurationFiles.Journal) is null);
        });
        suite.Add("configuration: commit marker retains new values on recovery", () =>
        {
            using var temp = new Sandbox();
            var files = new DurableConfigurationFiles(temp.Path);
            var old = Before(files);
            var transaction = Guid.NewGuid().ToString();
            Journal(files, old, transaction, new());
            files.Write("autosourcarr.env", Encoding.UTF8.GetBytes("MAIN_SUWAYOMI_URL=http://committed:30058\n"));
            files.Write(DurableConfigurationFiles.State, JsonSerializer.SerializeToUtf8Bytes(new { transaction, managed_keys = new[] { "MAIN_SUWAYOMI_URL" } }));
            var store = new CanonicalConfiguration(temp.Path);
            Check.Equal("http://committed:30058", store.StartupOverrides()["MAIN_SUWAYOMI_URL"]);
            Check.True(files.Read(DurableConfigurationFiles.Journal) is null);
        });
        suite.Add("configuration: invalid recovery blob cannot partially overwrite files", () =>
        {
            using var temp = new Sandbox();
            var files = new DurableConfigurationFiles(temp.Path);
            var before = Before(files);
            var invalid = new JsonObject
            {
                ["transaction"] = Guid.NewGuid().ToString(),
                ["before"] = new JsonObject
                {
                    ["autosourcarr.env"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("WRONG=value\n")),
                    ["source-host.env"] = Convert.ToBase64String(before["source-host.env"]!),
                    [DurableConfigurationFiles.State] = "invalid@@base64"
                }
            };
            files.Write(DurableConfigurationFiles.Journal, JsonSerializer.SerializeToUtf8Bytes(invalid));
            Check.Throws<InvalidDataException>(() => new CanonicalConfiguration(temp.Path).Recover());
            foreach (var name in DurableConfigurationFiles.ManagedFiles)
                Check.True(before[name]!.SequenceEqual(files.Read(name)!));
        });
        suite.Add("configuration: journal cannot choose paths or unknown managed keys", () =>
        {
            using var temp = new Sandbox();
            var files = new DurableConfigurationFiles(temp.Path);
            var before = Before(files);
            Journal(files, before, Guid.NewGuid().ToString(), new JsonObject { ["UNSUPPORTED_SETTING"] = "not allowed" });
            Check.Throws<InvalidDataException>(() => new CanonicalConfiguration(temp.Path).Recover());
            Check.Throws<ArgumentException>(() => files.Read("../outside"));
            foreach (var name in DurableConfigurationFiles.ManagedFiles)
                Check.True(before[name]!.SequenceEqual(files.Read(name)!));
        });
        suite.Add("configuration: patching preserves literal credentials and rejects duplicates", () =>
        {
            const string secret = "a$literal'\\value#without-interpolation";
            var bytes = CanonicalConfiguration.PatchEnvironment(Encoding.UTF8.GetBytes("# retained comment\nBROWSE_PAGE_SIZE=20\n"),
                new Dictionary<string, JsonNode?> { ["MAIN_SUWAYOMI_PASSWORD"] = JsonValue.Create(secret) });
            var parsed = DotEnv.Parse(Encoding.UTF8.GetString(bytes));
            Check.Equal(secret, parsed["MAIN_SUWAYOMI_PASSWORD"]);
            Check.True(Encoding.UTF8.GetString(bytes).StartsWith("# retained comment\n", StringComparison.Ordinal));
            Check.Throws<InvalidDataException>(() => CanonicalConfiguration.PatchEnvironment(
                Encoding.UTF8.GetBytes("BROWSE_PAGE_SIZE=20\nexport BROWSE_PAGE_SIZE=30\n"),
                new Dictionary<string, JsonNode?> { ["BROWSE_PAGE_SIZE"] = JsonValue.Create(40) }));
        });
        suite.Add("configuration: background cleanup does not invalidate user drafts", () =>
        {
            using var temp = new Sandbox();
            var files = new DurableConfigurationFiles(temp.Path);
            Before(files);
            var store = new CanonicalConfiguration(temp.Path);
            var revision = store.Revision();
            var state = store.State();
            state["cleanup"] = new JsonObject { ["status"] = "finished" };
            files.Write(DurableConfigurationFiles.State, JsonSerializer.SerializeToUtf8Bytes(state));
            Check.Equal(revision, store.Revision());
        });
    }

    private static Dictionary<string, byte[]?> Before(DurableConfigurationFiles files)
    {
        files.Write("autosourcarr.env", Encoding.UTF8.GetBytes("MAIN_SUWAYOMI_URL=http://old:30058\nBROWSE_PAGE_SIZE=20\n"));
        files.Write("source-host.env", Encoding.UTF8.GetBytes("EXTENSION_STORES=[]\n"));
        files.Write(DurableConfigurationFiles.State, JsonSerializer.SerializeToUtf8Bytes(new { completed = true, managed_keys = Array.Empty<string>() }));
        return DurableConfigurationFiles.ManagedFiles.ToDictionary(name => name, files.Read);
    }

    private static void Journal(DurableConfigurationFiles files, Dictionary<string, byte[]?> before, string transaction, JsonObject effective)
    {
        var encoded = new JsonObject();
        foreach (var (name, bytes) in before)
            encoded[name] = bytes is null ? null : Convert.ToBase64String(bytes);
        files.Write(DurableConfigurationFiles.Journal, JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["transaction"] = transaction, ["before"] = encoded, ["effective_before"] = effective
        }));
    }
}
