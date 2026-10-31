using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AutoSourcarr.Core.Settings;

/// <summary>One setting as the settings page, the wizard, the Compose docs and the update notice see it (SET-7).</summary>
public sealed record SettingSchemaEntry(
    string Key,
    string Group,
    string Label,
    string Description,
    string Type,
    IReadOnlyList<string> AllowedValues,
    IReadOnlyList<SettingChoice> Choices,
    string? Default,
    bool Secret,
    string ComposeVariable,
    bool ComposeRecommended,
    string? WizardStep,
    bool Advanced,
    double? Minimum,
    double? Maximum,
    bool AllowEmpty,
    bool Editable);

public sealed record SettingChoice(string Value, string Label);

/// <summary>
/// The one settings schema (SET-7): AutoSourcarrSettings.Definitions (name, type, secret, allowed values,
/// default) joined with setup-fields.json (wizard step, label, wizard description, limits) and
/// settings-schema.json (group, description of every other setting, Compose role). Validation,
/// redaction, the Compose variable docs and the update change list all read this one list.
/// </summary>
public static class SettingsSchema
{
    private static readonly Lazy<IReadOnlyList<SettingSchemaEntry>> Built = new(Build);

    public static IReadOnlyList<SettingSchemaEntry> Entries => Built.Value;

    public static IReadOnlyDictionary<string, SettingSchemaEntry> ByKey { get; } =
        Entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);

    /// <summary>Wizard-only fields that are not AutoSourcarr settings (EXTENSION_STORES lives in the source_host section).</summary>
    public static IReadOnlyList<string> WizardOnlyKeys { get; } =
        SetupFields.ByKey.Keys.Where(key => !Entries.Any(entry => entry.Key == key)).Order(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<SettingSchemaEntry> Build()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AutoSourcarr.Core.Settings.settings-schema.json")
            ?? throw new InvalidDataException("Missing settings schema.");
        using var document = JsonDocument.Parse(stream);
        var extra = document.RootElement.GetProperty("settings");
        var defaults = SettingsLoader.ToEnvironment(new AutoSourcarrSettings(), includeSecrets: false);
        var entries = new List<SettingSchemaEntry>();
        foreach (var definition in AutoSourcarrSettings.Definitions)
        {
            if (!extra.TryGetProperty(definition.Key, out var item))
                throw new InvalidDataException("settings-schema.json has no entry for " + definition.Key + ".");
            SetupFields.ByKey.TryGetValue(definition.Key, out var wizard);
            var hasWizard = wizard.ValueKind == JsonValueKind.Object;
            var description = hasWizard ? Text(wizard, "description") : Text(item, "description");
            if (string.IsNullOrWhiteSpace(description))
                throw new InvalidDataException("Setting " + definition.Key + " has no description.");
            var type = hasWizard ? Text(wizard, "kind")! : definition.Kind switch
            {
                SettingKind.Boolean => "boolean",
                SettingKind.Integer => "integer",
                SettingKind.Number => "number",
                SettingKind.Path or SettingKind.NullablePath => "path",
                _ => definition.AllowedValues.Count > 0 ? "choice" : definition.Secret ? "secret" : "text"
            };
            var choices = hasWizard && wizard.TryGetProperty("options", out var options)
                ? options.EnumerateArray().Select(pair => new SettingChoice(pair[0].GetString()!, pair[1].GetString()!)).ToArray()
                : definition.AllowedValues.Select(value => new SettingChoice(value, value)).ToArray();
            entries.Add(new SettingSchemaEntry(
                definition.Key,
                Text(item, "group") ?? throw new InvalidDataException("Setting " + definition.Key + " has no group."),
                hasWizard ? Text(wizard, "label")! : Label(definition.Key),
                description!,
                type,
                definition.AllowedValues,
                choices,
                definition.Secret ? null : defaults.TryGetValue(definition.Key, out var value) ? value : null,
                definition.Secret,
                definition.Key,
                Text(item, "compose") is "recommended" or "only",
                hasWizard ? Text(wizard, "step") : null,
                hasWizard && wizard.TryGetProperty("advanced", out var advanced) && advanced.GetBoolean(),
                hasWizard && wizard.TryGetProperty("minimum", out var minimum) ? minimum.GetDouble() : null,
                hasWizard && wizard.TryGetProperty("maximum", out var maximum) ? maximum.GetDouble() : null,
                hasWizard && wizard.TryGetProperty("allow_empty", out var allowEmpty) && allowEmpty.GetBoolean(),
                Text(item, "compose") != "only"));
        }
        var known = entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var property in extra.EnumerateObject())
            if (!known.Contains(property.Name))
                throw new InvalidDataException("settings-schema.json names an unknown setting: " + property.Name + ".");
        return entries;
    }

    /// <summary>Markdown reference of every setting and its Compose variable (ARC-5). Regenerated by CI and diffed.</summary>
    public static string Markdown()
    {
        var text = new StringBuilder();
        text.Append("# AutoSourcarr settings\n\n");
        text.Append("Generated from the settings schema (`src/AutoSourcarr.Core/Settings`); do not edit by hand. CI regenerates this file and fails when it differs.\n\n");
        text.Append("Settings are stored in `config.json` in the config folder and edited on the Settings page or in the setup wizard. ");
        text.Append("Secrets are stored encrypted with `config.key`; keep both files together in backups.\n\n");
        text.Append("## Compose overrides\n\n");
        text.Append("Any setting can be set by an environment variable with the same name in Compose. ");
        text.Append("A Compose variable wins over the built-in default and over values in `config.json` that you never changed in the app; ");
        text.Append("a value you saved on the Settings page or in the setup wizard wins over Compose (as wizard values did in 0.4.x), and the Settings page shows which settings Compose currently decides. ");
        text.Append("Use the page's *Use default* to hand a setting back to Compose. ");
        text.Append("Settings marked *Compose only* can only be set in Compose, because they name container folders or identities. ");
        text.Append("Keep Compose small: set only the variables marked *Compose* or *Compose only*, and remove the old `env_file:` line once `autosourcarr.env` has been converted.\n\n");
        text.Append("Precedence, lowest first: built-in default, `config.json`, Compose environment variable, value saved in the app.\n\n");
        foreach (var group in Entries.GroupBy(entry => entry.Group).OrderBy(group => GroupOrder(group.Key)).ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            text.Append("## ").Append(group.Key).Append("\n\n");
            text.Append("| Variable | Type | Default | Description | Compose | Wizard step |\n");
            text.Append("| --- | --- | --- | --- | --- | --- |\n");
            foreach (var entry in group.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var type = entry.Secret ? "secret" : entry.Type;
                if (entry.AllowedValues.Count > 0)
                    type += " (" + string.Join(", ", entry.AllowedValues) + ")";
                text.Append("| `").Append(entry.Key).Append("` | ").Append(Cell(type)).Append(" | ")
                    .Append(entry.Secret ? "—" : entry.Default is { Length: > 0 } value ? "`" + Cell(value) + "`" : "empty").Append(" | ")
                    .Append(Cell(entry.Description)).Append(" | ")
                    .Append(!entry.Editable ? "Compose only" : entry.ComposeRecommended ? "Compose" : "").Append(" | ")
                    .Append(entry.WizardStep ?? "").Append(" |\n");
            }
            text.Append('\n');
        }
        return text.ToString();
    }

    private static int GroupOrder(string group) => group switch
    {
        "Runtime" => 0,
        "Security" => 1,
        "Connections & languages" => 2,
        _ => 10
    };

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string Label(string key)
    {
        var words = key.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
        var text = string.Join(' ', words.Select(word => word switch
        {
            "http" or "url" or "ttl" or "id" or "uid" or "gid" or "api" or "mb" or "ms" or "ai" or "tz" => word.ToUpperInvariant(),
            _ => word
        }));
        return char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
    }
}
