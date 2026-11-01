using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoSourcarr.Core.Settings;

/// <summary>One group of string values; secrets are stored encrypted (see <see cref="SettingsSecrets"/>).</summary>
public sealed class ConfigSection
{
    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("secrets")]
    public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Where the values came from when an older env-based install was converted.</summary>
public sealed class ConfigConversionRecord
{
    [JsonPropertyName("converted_at")]
    public string ConvertedAt { get; set; } = "";

    [JsonPropertyName("sources")]
    public Dictionary<string, string> SourceSha256 { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("backup_directory")]
    public string BackupDirectory { get; set; } = "";

    [JsonPropertyName("setting_keys")]
    public List<string> SettingKeys { get; set; } = [];

    [JsonPropertyName("source_host_keys")]
    public List<string> SourceHostKeys { get; set; } = [];

    [JsonPropertyName("extra_keys")]
    public List<string> ExtraKeys { get; set; } = [];

    [JsonPropertyName("encrypted_keys")]
    public List<string> EncryptedKeys { get; set; } = [];

    [JsonPropertyName("invalid_keys")]
    public List<string> InvalidKeys { get; set; } = [];

    [JsonPropertyName("keys_without_value")]
    public List<string> KeysWithoutValue { get; set; } = [];
}

/// <summary>
/// config.json, format 1. Values keep their exact env text so nothing is lost or reinterpreted:
/// <c>settings</c> holds AutoSourcarr settings, <c>source_host</c> the extension-host settings
/// (former source-host.env) and <c>extra</c> every other key found in autosourcarr.env.
/// </summary>
public sealed class ConfigDocument
{
    public const int CurrentFormat = 1;

    [JsonPropertyName("format")]
    public int Format { get; set; } = CurrentFormat;

    [JsonPropertyName("settings")]
    public ConfigSection Settings { get; set; } = new();

    [JsonPropertyName("source_host")]
    public ConfigSection SourceHost { get; set; } = new();

    [JsonPropertyName("extra")]
    public ConfigSection Extra { get; set; } = new();

    [JsonPropertyName("conversion")]
    public ConfigConversionRecord? Conversion { get; set; }

    /// <summary>
    /// Settings you saved in the Settings page or setup wizard. They win over a Compose variable of the same
    /// name (as wizard values did in 0.4.x), so a leftover env_file cannot silently undo a change made in the UI.
    /// Resetting a setting to its default removes it from this list and lets Compose decide again.
    /// </summary>
    [JsonPropertyName("managed_keys")]
    public List<string> ManagedKeys { get; set; } = [];

    [JsonPropertyName("setup")]
    public ConfigSetupRecord? Setup { get; set; }

    [JsonPropertyName("update")]
    public ConfigUpdateRecord? Update { get; set; }

    /// <summary>Saved external-service connections (CON-1/CON-5); absent until the first one is added. Written by ConnectionStore only.</summary>
    [JsonPropertyName("connections")]
    public List<ConfigConnection>? Connections { get; set; }
}

/// <summary>
/// One saved connection in config.json. Secret field values are kept in <see cref="Secrets"/>, encrypted with
/// <see cref="SettingsSecrets"/> under the key <c>connections.&lt;id&gt;.&lt;field&gt;</c>, so a ciphertext cannot move to another connection.
/// </summary>
public sealed class ConfigConnection
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("secrets")]
    public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The last finished setup wizard run; Finish with the same transaction id is answered from here.</summary>
public sealed class ConfigSetupRecord
{
    [JsonPropertyName("completed")]
    public bool Completed { get; set; }

    [JsonPropertyName("completed_at")]
    public string CompletedAt { get; set; } = "";

    [JsonPropertyName("transaction")]
    public string Transaction { get; set; } = "";

    [JsonPropertyName("changed")]
    public List<string> Changed { get; set; } = [];

    [JsonPropertyName("restart_required")]
    public bool RestartRequired { get; set; }
}

/// <summary>What the last start knew, so the next version can say exactly what an update changed (ARC-7).</summary>
public sealed class ConfigUpdateRecord
{
    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = "";

    /// <summary>Built-in defaults of non-secret settings at that version.</summary>
    [JsonPropertyName("defaults")]
    public Dictionary<string, string> Defaults { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("notice")]
    public UpdateNotice? Notice { get; set; }
}

/// <summary>Shown after an update until dismissed. Holds key names and built-in defaults only, never your values.</summary>
public sealed class UpdateNotice
{
    [JsonPropertyName("from_version")]
    public string FromVersion { get; set; } = "";

    [JsonPropertyName("to_version")]
    public string ToVersion { get; set; } = "";

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("items")]
    public List<UpdateNoticeItem> Items { get; set; } = [];
}

public sealed class UpdateNoticeItem
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary>default_changed, removed or must_change.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("old_default")]
    public string? OldDefault { get; set; }

    [JsonPropertyName("new_default")]
    public string? NewDefault { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>Decrypted values of one config.json; never printed with values.</summary>
public sealed class ConfigValues(
    IReadOnlyDictionary<string, string?> settings,
    IReadOnlyDictionary<string, string?> sourceHost,
    IReadOnlyDictionary<string, string?> extra)
{
    public static ConfigValues Empty { get; } = new(
        new Dictionary<string, string?>(StringComparer.Ordinal),
        new Dictionary<string, string?>(StringComparer.Ordinal),
        new Dictionary<string, string?>(StringComparer.Ordinal));

    public IReadOnlyDictionary<string, string?> Settings { get; } = settings;
    public IReadOnlyDictionary<string, string?> SourceHost { get; } = sourceHost;
    public IReadOnlyDictionary<string, string?> Extra { get; } = extra;

    public IEnumerable<(string Section, IReadOnlyDictionary<string, string?> Values)> Sections()
    {
        yield return ("settings", Settings);
        yield return ("source_host", SourceHost);
        yield return ("extra", Extra);
    }

    public override string ToString() => "AutoSourcarr config.json values (redacted)";
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ConfigDocument))]
[JsonSerializable(typeof(List<ConfigConnection>))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>Reads and writes config.json in the configuration root (SETTINGS_CONFIG_DIR).</summary>
public sealed class ConfigStore(string root)
{
    public const string FileName = "config.json";

    public string Root { get; } = Path.GetFullPath(root);
    public string FilePath => Path.Combine(Root, FileName);
    public bool Exists => File.Exists(FilePath);

    public ConfigDocument? ReadDocument()
    {
        var raw = ConfigFiles.ReadBounded(FilePath);
        if (raw is null)
            return null;
        ConfigDocument? document;
        try { document = JsonSerializer.Deserialize(raw, ConfigJsonContext.Default.ConfigDocument); }
        catch (JsonException) { throw new ConfigurationException("config.json is not valid; it was not changed. Fix it or restore it from a backup."); }
        if (document is null)
            throw new ConfigurationException("config.json is empty; it was not changed. Restore it from a backup.");
        if (document.Format > ConfigDocument.CurrentFormat)
            throw new ConfigurationException($"config.json uses format {document.Format}; this build supports up to {ConfigDocument.CurrentFormat}. Run the newer AutoSourcarr image or restore a matching backup.");
        if (document.Format < 1)
            throw new ConfigurationException("config.json has no valid format number; it was not changed.");
        if (document.ManagedKeys is null || document.ManagedKeys.Any(key => key is null))
            throw new ConfigurationException("config.json has an empty (null) managed_keys entry; it was not changed. Restore it from a backup.");
        // An explicit null ("settings": null, "KEY": null) is damage, not an empty value.
        foreach (var section in new[] { document.Settings, document.SourceHost, document.Extra })
            if (section is null || section.Values is null || section.Secrets is null
                || section.Values.Values.Any(value => value is null) || section.Secrets.Values.Any(value => value is null))
                throw new ConfigurationException("config.json has an empty (null) section or value; it was not changed. Restore it from a backup.");
        if (document.Connections is { } connections && connections.Any(connection => connection is null || connection.Id is null || connection.Type is null
                || connection.Label is null || connection.Values is null || connection.Secrets is null
                || connection.Values.Values.Any(value => value is null) || connection.Secrets.Values.Any(value => value is null)))
            throw new ConfigurationException("config.json has an empty (null) connection or connection value; it was not changed. Restore it from a backup.");
        return document;
    }

    public void WriteDocument(ConfigDocument document) =>
        ConfigFiles.WriteAtomic(FilePath, JsonSerializer.SerializeToUtf8Bytes(Sorted(document), ConfigJsonContext.Default.ConfigDocument));

    /// <summary>Decrypted values; empty when config.json does not exist.</summary>
    public ConfigValues Load()
    {
        var document = ReadDocument();
        return document is null ? ConfigValues.Empty : Load(document);
    }

    /// <summary>Decrypted values of an already read document (one read for value and revision).</summary>
    public ConfigValues Load(ConfigDocument document)
    {
        var hasSecrets = document.Settings.Secrets.Count + document.SourceHost.Secrets.Count + document.Extra.Secrets.Count > 0;
        var secrets = hasSecrets ? SettingsSecrets.Open(Root, create: false) : null;
        return new(Decrypt(document.Settings, secrets), Decrypt(document.SourceHost, secrets), Decrypt(document.Extra, secrets));
    }

    /// <summary>Encrypts every secret key of the given plain values into a new section.</summary>
    public static ConfigSection Encrypt(IReadOnlyDictionary<string, string> values, SettingsSecrets secrets)
    {
        var section = new ConfigSection();
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (SettingsSecrets.IsSecretKey(key))
                section.Secrets[key] = secrets.Protect(key, value);
            else
                section.Values[key] = value;
        }
        return section;
    }

    private static Dictionary<string, string?> Decrypt(ConfigSection section, SettingsSecrets? secrets)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in section.Values)
        {
            if (SettingsSecrets.IsSecretKey(key))
                throw new ConfigurationException($"Secret {key} is stored unencrypted in config.json; move it to the secrets list or enter it again.");
            values[key] = value;
        }
        foreach (var (key, value) in section.Secrets)
        {
            if (values.ContainsKey(key))
                throw new ConfigurationException($"{key} appears twice in config.json.");
            values[key] = secrets!.Unprotect(key, value);
        }
        return values;
    }

    private static ConfigDocument Sorted(ConfigDocument document) => new()
    {
        Format = document.Format,
        Settings = Sorted(document.Settings),
        SourceHost = Sorted(document.SourceHost),
        Extra = Sorted(document.Extra),
        Conversion = document.Conversion,
        ManagedKeys = document.ManagedKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
        Setup = document.Setup,
        Update = document.Update,
        Connections = document.Connections?.Select(connection => new ConfigConnection
        {
            Id = connection.Id, Type = connection.Type, Label = connection.Label, Enabled = connection.Enabled,
            Values = new Dictionary<string, string>(connection.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal), StringComparer.Ordinal),
            Secrets = new Dictionary<string, string>(connection.Secrets.OrderBy(pair => pair.Key, StringComparer.Ordinal), StringComparer.Ordinal)
        }).ToList()
    };

    private static ConfigSection Sorted(ConfigSection section)
    {
        var sorted = new ConfigSection();
        foreach (var (key, value) in section.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            sorted.Values[key] = value;
        foreach (var (key, value) in section.Secrets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            sorted.Secrets[key] = value;
        return sorted;
    }
}
