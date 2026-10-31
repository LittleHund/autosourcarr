using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoSourcarr.Core.Settings;

/// <summary>One group of string values; secrets are stored encrypted (see <see cref="SettingsSecrets"/>).</summary>
public sealed class ConfigSection
{
    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("secrets")]
    public Dictionary<string, string> Secrets { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>Where the values came from when an older env-based install was converted.</summary>
public sealed class ConfigConversionRecord
{
    [JsonPropertyName("converted_at")]
    public string ConvertedAt { get; init; } = "";

    [JsonPropertyName("sources")]
    public Dictionary<string, string> SourceSha256 { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("backup_directory")]
    public string BackupDirectory { get; init; } = "";
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
    public int Format { get; init; } = CurrentFormat;

    [JsonPropertyName("settings")]
    public ConfigSection Settings { get; init; } = new();

    [JsonPropertyName("source_host")]
    public ConfigSection SourceHost { get; init; } = new();

    [JsonPropertyName("extra")]
    public ConfigSection Extra { get; init; } = new();

    [JsonPropertyName("conversion")]
    public ConfigConversionRecord? Conversion { get; init; }
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
        return document;
    }

    public void WriteDocument(ConfigDocument document) =>
        ConfigFiles.WriteAtomic(FilePath, JsonSerializer.SerializeToUtf8Bytes(Sorted(document), ConfigJsonContext.Default.ConfigDocument));

    /// <summary>Decrypted values; empty when config.json does not exist.</summary>
    public ConfigValues Load()
    {
        var document = ReadDocument();
        if (document is null)
            return ConfigValues.Empty;
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
        Conversion = document.Conversion
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
