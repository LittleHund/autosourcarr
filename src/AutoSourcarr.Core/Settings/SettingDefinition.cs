namespace AutoSourcarr.Core.Settings;

public enum SettingKind
{
    Text,
    Boolean,
    Integer,
    Number,
    Path,
    NullablePath
}

public sealed record SettingDefinition(
    string Key,
    string PropertyName,
    SettingKind Kind,
    bool Secret,
    IReadOnlyList<string> AllowedValues);

public sealed class ConfigurationException(string message) : Exception(message);
