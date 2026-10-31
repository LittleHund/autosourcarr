using System.Numerics;
using System.Text;
using System.Text.Json;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests;

public static class StartupConfigurationTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static void Register(TestSuite suite)
    {
        suite.Add("startup composition: canonical root is resolved before stale full settings validation", () =>
        {
            using var temp = new Sandbox();
            var root = Path.Combine(temp.Path, "configuration");
            var files = new DurableConfigurationFiles(root);
            files.Write("autosourcarr.env", Encoding.UTF8.GetBytes("BROWSE_PAGE_SIZE=40\nSOURCE_ENGINE_UID=3000\nSOURCE_ENGINE_GID=3000\n"));
            files.Write(DurableConfigurationFiles.State, JsonSerializer.SerializeToUtf8Bytes(new
            { managed_keys = new[] { "BROWSE_PAGE_SIZE", "SOURCE_ENGINE_UID", "SOURCE_ENGINE_GID" } }));
            files.Write("source-host.env", Encoding.UTF8.GetBytes("SOURCE_ENGINE_UID=4000\nSOURCE_ENGINE_GID=4000\n"));
            File.WriteAllText(Path.Combine(temp.Path, ".env"), "SETTINGS_CONFIG_DIR=configuration\nSOURCE_ENGINE_UID=1000\nSOURCE_ENGINE_GID=2000\n", new UTF8Encoding(false));
            var loaded = StartupConfigurationLoader.Load(temp.Path, new Dictionary<string, string?>
            { ["BROWSE_PAGE_SIZE"] = "invalid-stale-value", ["source_engine_uid"] = "4294967294" });
            Check.Equal(Path.GetFullPath(root), loaded.ConfigurationRoot);
            Check.Equal(40, loaded.Settings.BrowsePageSize);
            Check.Equal(new BigInteger(4294967294), loaded.Settings.SourceEngineUid);
            Check.Equal(new BigInteger(2000), loaded.Settings.SourceEngineGid);
            Check.True(!loaded.ToString().Contains("invalid-stale-value", StringComparison.Ordinal));
        });
        suite.Add("startup composition: CWD dotenv interpolation uses prior file values and explicit environment", () =>
        {
            using var temp = new Sandbox();
            var environment = new Dictionary<string, string?> { ["REFERENCE_UID"] = "2000", ["REFERENCE_GID"] = "3000" };
            var text = "SETTINGS_CONFIG_DIR=.\nREFERENCE_UID=1000\nSOURCE_ENGINE_UID='${REFERENCE_UID:-568}'\nSOURCE_ENGINE_GID=\"${REFERENCE_GID:-1000}\"\n";
            File.WriteAllText(Path.Combine(temp.Path, ".env"), text, new UTF8Encoding(false));
            var loaded = StartupConfigurationLoader.Load(temp.Path, environment);
            Check.Equal(new BigInteger(1000), loaded.Settings.SourceEngineUid);
            Check.Equal(new BigInteger(3000), loaded.Settings.SourceEngineGid);
            Check.Equal(2, environment.Count);
            Check.True(!environment.ContainsKey("SOURCE_ENGINE_UID"));
            // An explicit empty setting remains invalid; it cannot fall through to the file.
            Check.Throws<ConfigurationException>(() => StartupConfigurationLoader.Load(temp.Path,
                new Dictionary<string, string?>(environment) { ["SOURCE_ENGINE_UID"] = "" }));
        });
        suite.Add("startup composition: missing dotenv, canonical root precedence and invalid UTF8", () =>
        {
            using var temp = new Sandbox();
            var root = Path.Combine(temp.Path, "chosen");
            var loaded = StartupConfigurationLoader.Load(temp.Path,
                new Dictionary<string, string?> { ["settings_config_dir"] = root, ["SOURCE_ENGINE_MODE"] = "external", ["SOURCE_ENGINE_UID"] = "-1" }, bundledImage: true);
            Check.Equal(root, loaded.ConfigurationRoot);
            Check.Equal("integrated", loaded.Settings.AppServicesMode);
            Check.Equal("external", loaded.SourceRequest.Mode);
            Check.Equal(new BigInteger(-1), loaded.SourceRequest.Uid);
            Check.True(!Directory.Exists(root));
            File.WriteAllBytes(Path.Combine(temp.Path, ".env"), [0x53, 0xFF, 0x0A]);
            Check.Throws<DecoderFallbackException>(() => StartupConfigurationLoader.Load(temp.Path,
                new Dictionary<string, string?> { ["SETTINGS_CONFIG_DIR"] = root }));
        });
        suite.Add("startup handoff: versioned canonical request round trips exact IDs with a boot nonce", () =>
        {
            var request = new SourceStartupRequest("integrated", BigInteger.Parse("4294967294"), BigInteger.Parse("18446744073709551616"));
            var encoded = request.Encode(Nonce);
            var decoded = SourceStartupRequest.Decode(encoded, Nonce);
            Check.Equal(request, decoded);
            Check.True(encoded.All(value => value < 128));
            Check.Equal(request, SourceStartupRequest.FromSettings(new()
            { SourceEngineUid = request.Uid, SourceEngineGid = request.Gid }));
            request.RequireSameRequest(decoded);
            Check.Throws<ConfigurationException>(() => request.RequireSameRequest(decoded with { Gid = 568 }));
            Check.Throws<ConfigurationException>(() => request.RequireSameRequest(decoded with { Mode = "external" }));
        });
        suite.Add("startup handoff: forged nonce, malformed/noncanonical frames and arbitrary fields are refused", () =>
        {
            var request = new SourceStartupRequest("external", -1, BigInteger.Pow(10, 100));
            var frame = Encoding.ASCII.GetString(request.Encode(Nonce));
            Check.Throws<InvalidDataException>(() => SourceStartupRequest.Decode(Encoding.ASCII.GetBytes(frame), new string('f', 64)));
            foreach (var changed in new[]
            {
                frame.Replace("STARTUP-1", "STARTUP-2", StringComparison.Ordinal), frame + "EXEC=/bin/sh\n",
                frame.Replace("external\n", "External\n", StringComparison.Ordinal), frame.Replace("\n-1\n", "\n+1\n", StringComparison.Ordinal),
                frame.Replace("\n-1\n", "\n01\n", StringComparison.Ordinal), frame.Replace("\n-1\n", "\n-0\n", StringComparison.Ordinal),
                frame.Replace("\n-1\n", "\n1.0\n", StringComparison.Ordinal), frame.Replace("\n-1\n", "\n1e0\n", StringComparison.Ordinal),
                frame.Replace("\n-1\n", "\n1_0\n", StringComparison.Ordinal), frame.Replace("\n", "\r\n", StringComparison.Ordinal),
                frame.TrimEnd('\n'), frame + "\0", frame + new string('0', SourceStartupRequest.MaximumFrameBytes)
            })
                Check.Throws<InvalidDataException>(() => SourceStartupRequest.Decode(Encoding.UTF8.GetBytes(changed), Nonce));
            var nonAscii = Encoding.UTF8.GetBytes(frame.Replace("\n-1\n", "\n١\n", StringComparison.Ordinal));
            Check.Throws<InvalidDataException>(() => SourceStartupRequest.Decode(nonAscii, Nonce));
            Check.Throws<ArgumentException>(() => request.Encode("not-a-boot-nonce"));
        });
        suite.Add("startup handoff: serialization carries no general configuration or native acceptance", () =>
        {
            var settings = new AutoSourcarrSettings
            { SourceEngineMode = "external", SourceEngineUid = -1, SourceEngineGid = BigInteger.Pow(10, 100), AdminToken = "synthetic-secret" };
            var request = SourceStartupRequest.FromSettings(settings);
            var frame = request.Encode(Nonce);
            Check.Equal(request, SourceStartupRequest.Decode(frame, Nonce));
            Check.True(!Encoding.ASCII.GetString(frame).Contains("synthetic-secret", StringComparison.Ordinal));
            Check.True(!Encoding.ASCII.GetString(frame).Contains("/config", StringComparison.Ordinal));
            // Request preservation is intentionally separate from the later owned-child
            // positive/width/sentinel checks, directory proof and actual Linux syscalls.
            Check.Equal(new BigInteger(-1), request.Uid);
        });
    }
}
