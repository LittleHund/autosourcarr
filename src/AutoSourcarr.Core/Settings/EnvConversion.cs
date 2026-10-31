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
        var encrypted = settings.Keys.Concat(sourceHost.Keys.Select(key => SourceHostEnv + ":" + key)).Concat(extra.Keys)
            .Where(key => SettingsSecrets.IsSecretKey(key.Contains(':', StringComparison.Ordinal) ? key[(key.IndexOf(':', StringComparison.Ordinal) + 1)..] : key))
            .Order(StringComparer.Ordinal).ToList();
        var invalid = InvalidSettings(settings);
        var conversion = new ConfigConversionRecord
        {
            ConvertedAt = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            SourceSha256 = hashes,
            BackupDirectory = Path.GetRelativePath(store.Root, backupDirectory).Replace('\\', '/'),
            SettingKeys = settings.Keys.Order(StringComparer.Ordinal).ToList(),
            SourceHostKeys = sourceHost.Keys.Order(StringComparer.Ordinal).ToList(),
            ExtraKeys = extra.Keys.Order(StringComparer.Ordinal).ToList(),
            EncryptedKeys = encrypted,
            InvalidKeys = invalid.ToList(),
            KeysWithoutValue = withoutValue.ToList()
        };
        var (setup, wizardKeys) = PreviousSetup(store.Root, conversion.ConvertedAt);
        var document = new ConfigDocument
        {
            Settings = ConfigStore.Encrypt(settings, secrets),
            SourceHost = ConfigStore.Encrypt(sourceHost, secrets),
            Extra = ConfigStore.Encrypt(extra, secrets),
            Conversion = conversion,
            // 0.4.x wizard values beat stale Compose injections; they keep doing so from config.json.
            ManagedKeys = wizardKeys.Where(settings.ContainsKey).Order(StringComparer.Ordinal).ToList(),
            Setup = setup,
            // Shown in Settings until dismissed. The app version is filled in by the first start (UpdateNotices).
            Update = new ConfigUpdateRecord
            {
                AppVersion = "",
                Defaults = SettingsSchema.Entries.Where(entry => !entry.Secret)
                    .ToDictionary(entry => entry.Key, entry => entry.Default ?? "", StringComparer.Ordinal),
                Notice = ConversionNotice(conversion)
            }
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
            SettingKeys = conversion.SettingKeys,
            SourceHostKeys = conversion.SourceHostKeys,
            ExtraKeys = conversion.ExtraKeys,
            EncryptedKeys = encrypted,
            InvalidKeys = invalid,
            KeysWithoutValue = conversion.KeysWithoutValue
        };
    }

    /// <summary>The persisted conversion record as a report (key names only), or null when this install never converted.</summary>
    public static ConversionReport? Saved(string root)
    {
        var record = new ConfigStore(root).ReadDocument()?.Conversion;
        return record is null ? null : new ConversionReport
        {
            ConvertedAt = record.ConvertedAt, BackupDirectory = record.BackupDirectory, SettingKeys = record.SettingKeys,
            SourceHostKeys = record.SourceHostKeys, ExtraKeys = record.ExtraKeys, EncryptedKeys = record.EncryptedKeys,
            InvalidKeys = record.InvalidKeys, KeysWithoutValue = record.KeysWithoutValue
        };
    }

    private static UpdateNotice ConversionNotice(ConfigConversionRecord conversion)
    {
        var items = new List<UpdateNoticeItem>
        {
            new()
            {
                Key = MainEnv, Kind = "converted",
                Message = $"Your settings were moved from {MainEnv} into config.json ({conversion.SettingKeys.Count} settings, {conversion.EncryptedKeys.Count} secrets encrypted with config.key). "
                    + $"The old files were left in place and copied to {conversion.BackupDirectory}. Back up config.json and config.key together."
            },
            new()
            {
                Key = "env_file", Kind = "action",
                Message = "Remove the old env_file line from your Compose file. Variables it injects count as Compose overrides; settings you save in this app still win, but every other setting keeps the old file's value."
            }
        };
        foreach (var key in conversion.InvalidKeys)
            items.Add(new() { Key = key, Kind = "must_change", Message = $"{key}: the value copied from {MainEnv} is not accepted. Open Settings and choose a new value." });
        if (conversion.ExtraKeys.Count > 0)
            items.Add(new() { Key = "extra", Kind = "kept", Message = "Kept but not used by this version: " + string.Join(", ", conversion.ExtraKeys) + "." });
        return new UpdateNotice { FromVersion = "0.4.x (" + MainEnv + ")", ToVersion = "config.json", CreatedAt = conversion.ConvertedAt, Items = items };
    }

    /// <summary>A finished 0.4.x setup stays finished, and its wizard-owned keys stay owned by the app.</summary>
    private static (ConfigSetupRecord? Setup, IReadOnlyList<string> ManagedKeys) PreviousSetup(string root, string convertedAt)
    {
        try
        {
            var raw = ConfigFiles.ReadBounded(Path.Combine(root, "setup-state.json"));
            if (raw is null)
                return (null, []);
            using var state = System.Text.Json.JsonDocument.Parse(raw);
            if (state.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return (null, []);
            var keys = state.RootElement.TryGetProperty("managed_keys", out var managed) && managed.ValueKind == System.Text.Json.JsonValueKind.Array
                ? managed.EnumerateArray().Where(item => item.ValueKind == System.Text.Json.JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                : [];
            var finished = state.RootElement.TryGetProperty("completed", out var completed) && completed.ValueKind == System.Text.Json.JsonValueKind.True;
            return (finished ? new ConfigSetupRecord { Completed = true, CompletedAt = convertedAt, Transaction = "converted-from-0.4" } : null, keys);
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or ConfigurationException)
        {
            return (null, []); // an unreadable old state only means the wizard is offered again
        }
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

    private static IReadOnlyList<string> InvalidSettings(IReadOnlyDictionary<string, string> settings)
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
