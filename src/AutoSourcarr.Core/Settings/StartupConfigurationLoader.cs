using System.Text;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Settings;

/// <summary>One explicit startup composition shared by the resolver and final host.</summary>
public static class StartupConfigurationLoader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static StartupConfigurationSnapshot Load(string workingDirectory,
        IReadOnlyDictionary<string, string?> environment, bool bundledImage = false,
        bool convertLegacyEnvironment = false, DateTimeOffset? now = null)
    {
        var directory = Path.GetFullPath(workingDirectory);
        // Capture the caller-supplied environment once. Tests and reusable loading
        // never consult ambient process variables, change CWD or set TZ.
        var inherited = environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var path = Path.Combine(directory, ".env");
        byte[] raw;
        try { raw = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { raw = []; }
        var dotenv = DotEnv.Parse(StrictUtf8.GetString(raw), interpolate: true, environment: inherited);

        // Bootstrap ONLY the canonical root before validating the full model:
        // recovered wizard-owned settings may replace stale invalid injections.
        var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in new[] { dotenv, inherited })
            foreach (var (key, value) in input)
                if (value is not null && key.Equals("SETTINGS_CONFIG_DIR", StringComparison.OrdinalIgnoreCase))
                    roots[key] = value;
        var configuredRoot = roots.TryGetValue("SETTINGS_CONFIG_DIR", out var root) ? root! : "/config";
        var configurationRoot = Path.GetFullPath(configuredRoot.Length == 0 ? "." : configuredRoot, directory);
        var managed = new CanonicalConfiguration(configurationRoot).StartupOverrides();
        // Only the final host converts; the unprivileged resolver and tests stay read-only.
        var conversion = convertLegacyEnvironment
            ? EnvConversion.EnsureConverted(configurationRoot, now ?? DateTimeOffset.UtcNow)
            : null;
        var store = new ConfigStore(configurationRoot);
        var document = store.ReadDocument();
        var configuration = document is null ? ConfigValues.Empty : store.Load(document);
        // Once config.json exists it is the only saved configuration: the 0.4.x wizard values were copied into
        // it by the conversion (and stay "managed"), and Settings/setup now save there. Values you saved in the
        // app win over Compose variables, like 0.4.x wizard values did; everything else in config.json is
        // below Compose (ARC-5). The env-file recovery above still ran, so a half-finished 0.4.x save is undone first.
        if (document is not null)
            managed = configuration.Settings
                .Where(pair => pair.Value is not null && document.ManagedKeys.Contains(pair.Key, StringComparer.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var settings = SettingsLoader.Load(dotenv, inherited, managed, bundledImage, configuration.Settings);
        return new(settings, configurationRoot, conversion, inherited);
    }
}

public sealed class StartupConfigurationSnapshot
{
    internal StartupConfigurationSnapshot(AutoSourcarrSettings settings, string configurationRoot, ConversionReport? conversion = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        Settings = settings;
        Environment = environment ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        ConfigurationRoot = configurationRoot;
        Conversion = conversion;
        SourceRequest = SourceStartupRequest.FromSettings(settings);
    }

    public AutoSourcarrSettings Settings { get; }
    public string ConfigurationRoot { get; }
    /// <summary>Set when this start converted autosourcarr.env to config.json.</summary>
    public ConversionReport? Conversion { get; }
    public SourceStartupRequest SourceRequest { get; }
    /// <summary>The container environment as it was at start (before the app sets TZ), for "overridden by Compose".</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }
    public override string ToString() => "AutoSourcarr startup configuration (values redacted)";
}
