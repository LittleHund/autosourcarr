using System.Globalization;
using System.Reflection;

namespace AutoSourcarr.Core.Settings;

/// <summary>
/// Managed canonical values override stale Compose injections before parsing.
/// Inputs are explicit so tests never read the developer's active environment.
/// </summary>
public static class SettingsLoader
{
    private static readonly IReadOnlyDictionary<string, PropertyInfo> Properties =
        typeof(AutoSourcarrSettings).GetProperties().ToDictionary(p => p.Name, StringComparer.Ordinal);

    public static AutoSourcarrSettings Load(
        IReadOnlyDictionary<string, string?>? dotenv = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        IReadOnlyDictionary<string, string?>? managedOverrides = null,
        bool bundledImage = false,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        // Precedence, lowest first: .env in the working directory, config.json,
        // container environment (Compose overrides), then values saved in the app
        // (config.json managed_keys; before the conversion, the 0.4.x wizard values).
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Overlay(values, dotenv);
        Overlay(values, configuration);
        Overlay(values, environment);
        Overlay(values, managedOverrides);

        var settings = new AutoSourcarrSettings();
        foreach (var definition in AutoSourcarrSettings.Definitions)
        {
            if (!values.TryGetValue(definition.Key, out var value) || value is null)
                continue;

            object? parsed = definition.Kind switch
            {
                SettingKind.Boolean => ParseBoolean(definition.Key, value),
                SettingKind.Integer when definition.Key is "SOURCE_ENGINE_UID" or "SOURCE_ENGINE_GID"
                    => SourceIdentityInteger.TryParse(value, out var identity) ? identity : throw Invalid(definition.Key),
                SettingKind.Integer => ParseInteger(definition.Key, value),
                SettingKind.Number => ParseNumber(definition.Key, value),
                _ => value
            };
            if (definition.AllowedValues.Count > 0 && !definition.AllowedValues.Contains(value, StringComparer.Ordinal))
                throw Invalid(definition.Key);
            Properties[definition.PropertyName].SetValue(settings, parsed);
        }

        // This is an image-owned fact, never a hostname or user-controlled setting.
        return bundledImage ? settings with { AppServicesMode = "integrated" } : settings;
    }

    public static IReadOnlyDictionary<string, string> ToEnvironment(AutoSourcarrSettings settings, bool includeSecrets = false)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in AutoSourcarrSettings.Definitions)
        {
            if (definition.Secret && !includeSecrets)
                continue;
            var value = Properties[definition.PropertyName].GetValue(settings);
            if (value is null)
                continue;
            output[definition.Key] = value switch
            {
                bool flag => flag ? "true" : "false",
                double number => number.ToString("R", CultureInfo.InvariantCulture),
                IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? ""
            };
        }
        return output;
    }

    private static void Overlay(Dictionary<string, string?> target, IReadOnlyDictionary<string, string?>? source)
    {
        if (source is null)
            return;
        foreach (var pair in source)
            if (pair.Value is not null)
                target[pair.Key] = pair.Value;
    }

    private static bool ParseBoolean(string key, string value) => value.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "t" or "on" or "yes" or "y" => true,
        "0" or "false" or "f" or "off" or "no" or "n" => false,
        _ => throw Invalid(key)
    };

    private static int ParseInteger(string key, string value)
    {
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && decimal.Truncate(number) == number && number is >= int.MinValue and <= int.MaxValue)
            return (int)number;
        throw Invalid(key);
    }

    private static double ParseNumber(string key, string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
            return number;
        throw Invalid(key);
    }

    // Never put a failed value in an error: it could be a secret in a wrong field.
    private static ConfigurationException Invalid(string key) => new($"Invalid configuration value for {key}.");
}
