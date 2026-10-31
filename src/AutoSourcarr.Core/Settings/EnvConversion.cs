using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AutoSourcarr.Core.Settings;

/// <summary>What a one-time env conversion did. Holds key names only, never values.</summary>
public sealed class ConversionReport
{
    public required string ConvertedAt { get; init; }
    public required string BackupDirectory { get; init; }
    public required IReadOnlyList<string> SettingKeys { get; init; }
    public required IReadOnlyList<string> SourceHostKeys { get; init; }
    public required IReadOnlyList<string> ExtraKeys { get; init; }
    public required IReadOnlyList<string> EncryptedKeys { get; init; }
    /// <summary>Known settings whose current value would be rejected at startup; kept unchanged for you to fix.</summary>
    public required IReadOnlyList<string> InvalidKeys { get; init; }
    /// <summary>Lines like <c>KEY</c> without <c>=</c>; they never had a value, so nothing is stored.</summary>
    public required IReadOnlyList<string> KeysWithoutValue { get; init; }
}

/// <summary>
/// One-time move from config/autosourcarr.env (+ source-host.env) to config.json.
/// The env files stay where they are and a timestamped copy is kept in backups/, so a
/// rollback to 0.4.x keeps working. Every value is verified after writing; on any
/// mismatch config.json is removed again and the env files remain the active configuration.
/// </summary>
public static class EnvConversion
{
    public const string MainEnv = "autosourcarr.env";
    public const string SourceHostEnv = "source-host.env";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Converts when config.json is absent and at least one env file exists; otherwise returns null.</summary>
    public static ConversionReport? EnsureConverted(string root, DateTimeOffset now)
    {
        var store = new ConfigStore(root);
        if (store.Exists)
            return null;
        var main = ConfigFiles.ReadBounded(Path.Combine(store.Root, MainEnv));
        var host = ConfigFiles.ReadBounded(Path.Combine(store.Root, SourceHostEnv));
        if (main is null && host is null)
            return null;
        return Convert(store, main, host, now);
    }

    public static ConversionReport Convert(ConfigStore store, byte[]? main, byte[]? host, DateTimeOffset now)
    {
        if (store.Exists)
            throw new ConfigurationException("config.json already exists; the env files were not converted again.");
        var mainValues = Parse(main, MainEnv);
        var hostValues = Parse(host, SourceHostEnv);
        var known = AutoSourcarrSettings.Definitions.Select(definition => definition.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceHost = new Dictionary<string, string>(StringComparer.Ordinal);
        var withoutValue = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in mainValues)
        {
            if (value is null) { withoutValue.Add(key); continue; }
            (known.Contains(key) ? settings : extra)[key] = value;
        }
        foreach (var (key, value) in hostValues)
        {
            if (value is null) { withoutValue.Add(SourceHostEnv + ":" + key); continue; }
            sourceHost[key] = value;
        }

        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var backupDirectory = Path.Combine(store.Root, "backups", "config-conversion-" + stamp);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, raw) in new[] { (MainEnv, main), (SourceHostEnv, host) })
        {
            if (raw is null)
                continue;
            hashes[name] = System.Convert.ToHexStringLower(SHA256.HashData(raw));
            ConfigFiles.WriteAtomic(Path.Combine(backupDirectory, name), raw);
        }

        var secrets = SettingsSecrets.Open(store.Root, create: true);
        var conversion = new ConfigConversionRecord
        {
            ConvertedAt = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            SourceSha256 = hashes,
            BackupDirectory = Path.GetRelativePath(store.Root, backupDirectory).Replace('\\', '/')
        };
        var document = new ConfigDocument
        {
            Settings = ConfigStore.Encrypt(settings, secrets),
            SourceHost = ConfigStore.Encrypt(sourceHost, secrets),
            Extra = ConfigStore.Encrypt(extra, secrets),
            Conversion = conversion
        };
        store.WriteDocument(document);
        try
        {
            var written = store.Load();
            Require(settings, written.Settings, "settings");
            Require(sourceHost, written.SourceHost, "source_host");
            Require(extra, written.Extra, "extra");
        }
        catch
        {
            File.Delete(store.FilePath);
            throw;
        }

        return new ConversionReport
        {
            ConvertedAt = conversion.ConvertedAt,
            BackupDirectory = conversion.BackupDirectory,
            SettingKeys = settings.Keys.Order(StringComparer.Ordinal).ToArray(),
            SourceHostKeys = sourceHost.Keys.Order(StringComparer.Ordinal).ToArray(),
            ExtraKeys = extra.Keys.Order(StringComparer.Ordinal).ToArray(),
            EncryptedKeys = settings.Keys.Concat(sourceHost.Keys.Select(key => SourceHostEnv + ":" + key)).Concat(extra.Keys)
                .Where(key => SettingsSecrets.IsSecretKey(key.Contains(':', StringComparison.Ordinal) ? key[(key.IndexOf(':', StringComparison.Ordinal) + 1)..] : key))
                .Order(StringComparer.Ordinal).ToArray(),
            InvalidKeys = InvalidSettings(settings),
            KeysWithoutValue = withoutValue.ToArray()
        };
    }

    private static IReadOnlyDictionary<string, string?> Parse(byte[]? raw, string name)
    {
        if (raw is null)
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        string text;
        try { text = StrictUtf8.GetString(raw); }
        catch (DecoderFallbackException) { throw new ConfigurationException(name + " is not valid UTF-8; it was not converted."); }
        // Same reader and rules as the canonical setup files: no variable interpolation.
        return DotEnv.Parse(text);
    }

    private static void Require(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string?> actual, string section)
    {
        if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var value) || !string.Equals(value, pair.Value, StringComparison.Ordinal)))
            throw new ConfigurationException($"config.json verification failed for {section}; the conversion was undone and the env files stay active.");
    }

    private static string[] InvalidSettings(IReadOnlyDictionary<string, string> settings)
    {
        var invalid = new List<string>();
        foreach (var (key, value) in settings)
        {
            try { SettingsLoader.Load(configuration: new Dictionary<string, string?>(StringComparer.Ordinal) { [key] = value }); }
            catch (ConfigurationException) { invalid.Add(key); }
        }
        return invalid.Order(StringComparer.Ordinal).ToArray();
    }
}
