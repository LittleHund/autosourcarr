using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Text;

namespace AutoSourcarr.Core.Sources.Controls;

public sealed class SourcePreferencesConflictException(string message) : ArgumentException(message);

/// <summary>Released native source-preference wire contract. No engine operation or secret persistence occurs here.</summary>
public static class SourcePreferenceContract
{
    public const string PreferenceFields = """
        __typename
        ... on SwitchPreference { key title summary visible enabled switch_value: currentValue switch_default: default }
        ... on CheckBoxPreference { key title summary visible enabled checkbox_value: currentValue checkbox_default: default }
        ... on EditTextPreference { key title summary visible enabled text_value: currentValue text_default: default dialogTitle dialogMessage }
        ... on ListPreference { key title summary visible enabled list_value: currentValue list_default: default entries entryValues }
        ... on MultiSelectListPreference { key title summary visible enabled multiselect_value: currentValue multiselect_default: default entries entryValues dialogTitle dialogMessage }
        """;
    public static string SnapshotQuery => "query AutoSourcarrSourcePreferences($id: LongString!) { source(id: $id) { id name isConfigurable preferences { " + PreferenceFields + " } } }";
    public static string MutationQuery => "mutation AutoSourcarrSourcePreference($input: UpdateSourcePreferenceInput!) { updateSourcePreference(input: $input) { source { id name isConfigurable } preferences { " + PreferenceFields + " } } }";
    private static readonly IReadOnlyDictionary<string, (string Kind, string Field)> Types = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["SwitchPreference"] = ("switch", "switchState"), ["CheckBoxPreference"] = ("checkbox", "checkBoxState"), ["EditTextPreference"] = ("text", "editTextState"),
        ["ListPreference"] = ("list", "listState"), ["MultiSelectListPreference"] = ("multiselect", "multiSelectState")
    };
    private static readonly Regex Secret = new(@"password|passwd|secret|token|authorization|cookie|credential|api.?key", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string SourceId(string value) => SourceId(JsonValue.Create(value));
    public static string SourceId(JsonNode? value)
    {
        var raw = value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : CanonicalJson.Text(value);
        if (!Regex.IsMatch(raw, "^-?[0-9]{1,19}$", RegexOptions.CultureInvariant) || !long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) throw new ArgumentException("Invalid source ID");
        return number.ToString(CultureInfo.InvariantCulture);
    }
    public static JsonObject Normalize(JsonObject source, JsonArray raw)
    {
        if (source["name"] is { } sourceName) RequireKind(sourceName, JsonValueKind.String);
        if (source["isConfigurable"] is { } configurable) RequireKind(configurable, JsonValueKind.True, JsonValueKind.False);
        var rows = new JsonArray();
        for (var index = 0; index < raw.Count; index++)
        {
            var pref = raw[index] as JsonObject ?? throw new ArgumentException("Malformed source preference metadata");
            var kind = Types.TryGetValue(Text(pref["__typename"]), out var type) ? type.Kind : "unsupported";
            foreach (var field in new[] { "title", "summary", "dialogTitle", "dialogMessage" }) if (pref[field] is { } textValue) RequireKind(textValue, JsonValueKind.String);
            if (pref["key"] is { } keyValue && keyValue.GetValueKind() != JsonValueKind.String) throw new ArgumentException("Malformed source preference metadata");
            var key = pref["key"]?.DeepClone(); var title = First(pref["title"], key, JsonValue.Create("Source setting"));
            var defaultValue = pref[kind + "_default"]?.DeepClone(); var value = pref[kind + "_value"]?.DeepClone() ?? defaultValue?.DeepClone();
            value ??= kind == "multiselect" ? new JsonArray() : kind is "switch" or "checkbox" ? JsonValue.Create(false) : JsonValue.Create("");
            if (kind == "multiselect") { value = SortedStrings(value); defaultValue = defaultValue is null ? null : SortedStrings(defaultValue); }
            else if (kind is "switch" or "checkbox") { RequireKind(value, JsonValueKind.True, JsonValueKind.False); if (defaultValue is not null) RequireKind(defaultValue, JsonValueKind.True, JsonValueKind.False); }
            else if (kind is "text" or "list") { RequireKind(value, JsonValueKind.String); if (defaultValue is not null) RequireKind(defaultValue, JsonValueKind.String); }
            if (pref["enabled"] is { } enabled) RequireKind(enabled, JsonValueKind.True, JsonValueKind.False);
            if (pref["visible"] is { } visible) RequireKind(visible, JsonValueKind.True, JsonValueKind.False);
            var enabledFlag = (!pref.ContainsKey("enabled") || Flag(pref["enabled"])) && Truth(key) && kind != "unsupported";
            rows.Add(new JsonObject { ["index"] = index, ["key"] = key, ["type"] = kind, ["title"] = title,
                ["summary"] = First(pref["summary"]), ["visible"] = pref.ContainsKey("visible") ? pref["visible"]?.DeepClone() : JsonValue.Create(true), ["enabled"] = enabledFlag,
                ["value"] = value, ["default"] = defaultValue, ["entries"] = Strings(pref["entries"]), ["entry_values"] = Strings(pref["entryValues"]),
                ["dialog_title"] = First(pref["dialogTitle"]), ["dialog_message"] = First(pref["dialogMessage"]), ["secret"] = Secret.IsMatch((key is null ? "None" : Text(key)) + " " + title) });
        }
        var id = source["id"] ?? throw new ArgumentException("Source identity is missing");
        var result = new JsonObject { ["source_id"] = id.GetValueKind() == JsonValueKind.String ? id.GetValue<string>() : CanonicalJson.Text(id),
            ["name"] = First(source["name"], JsonValue.Create("Source")), ["configurable"] = Flag(source["isConfigurable"]), ["preferences"] = rows };
        result["revision"] = Convert.ToHexString(SHA256.HashData(CanonicalJson.Bytes(result))).ToLowerInvariant(); return result;
    }

    public static JsonObject Change(JsonObject snapshot, JsonNode? change)
    {
        if (change is not JsonObject obj) throw new ArgumentException("Provide one source setting change");
        if (Text(obj["revision"]) != Text(snapshot["revision"])) throw new SourcePreferencesConflictException("Source settings changed. Reload them before saving your changes.");
        var preferences = snapshot["preferences"] as JsonArray ?? throw new ArgumentException("Malformed source preference snapshot");
        if (!Index(obj["index"], out var index) || index < 0 || index >= preferences.Count) throw new SourcePreferencesConflictException("Source setting position changed. Reload its settings.");
        var pref = preferences[(int)index]!.AsObject();
        if (!JsonNode.DeepEquals(obj["key"], pref["key"]) || Text(obj["type"]) != Text(pref["type"])) throw new SourcePreferencesConflictException("Source setting identity changed. Reload its settings.");
        if (!Flag(pref["enabled"]) || !Flag(pref["visible"])) throw new ArgumentException("This source setting is currently unavailable");
        var kind = Text(pref["type"]); var value = obj["value"]?.DeepClone();
        if (kind is "switch" or "checkbox")
        { if (value?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("This setting requires an on/off value"); }
        else if (kind is "text" or "list")
        {
            if (value?.GetValueKind() != JsonValueKind.String || value.GetValue<string>().EnumerateRunes().Count() > 8192 || value.GetValue<string>().Contains('\0')) throw new ArgumentException("This setting requires text of at most 8192 characters");
            if (kind == "list" && !(pref["entry_values"] as JsonArray ?? new()).Any(item => JsonNode.DeepEquals(item, value))) throw new ArgumentException("Choose one of the source setting options");
        }
        else if (kind == "multiselect")
        {
            var options = pref["entry_values"] as JsonArray ?? new();
            if (value is not JsonArray selected || selected.Count > options.Count || selected.Any(item => item?.GetValueKind() != JsonValueKind.String || !options.Any(option => JsonNode.DeepEquals(option, item)))) throw new ArgumentException("Choose only the available source setting options");
            value = new JsonArray(selected.Select(item => item!.GetValue<string>()).Distinct(StringComparer.Ordinal).Select(item => JsonValue.Create(item)).ToArray());
        }
        else throw new ArgumentException("This source setting type is not supported by the source engine");
        var field = Types.Values.First(type => type.Kind == kind).Field; return new() { ["position"] = index, [field] = value };
    }
    internal static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
    internal static bool Flag(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) && flag;
    private static string First(params JsonNode?[] values) => values.Where(Truth).Select(Text).FirstOrDefault() ?? "";
    private static bool Truth(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text.Length > 0 : value is not null;
    private static bool Index(JsonNode? node, out long index)
    {
        index = -1; if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return false;
        if (value.TryGetValue<long>(out index)) return true; if (value.TryGetValue<int>(out var small)) { index = small; return true; } return false;
    }
    private static JsonArray Strings(JsonNode? node)
    {
        if (node is null) return new(); if (node is not JsonArray array || array.Any(value => value?.GetValueKind() != JsonValueKind.String)) throw new ArgumentException("Malformed source preference metadata");
        return (JsonArray)array.DeepClone();
    }
    private static JsonArray SortedStrings(JsonNode node) => new(Strings(node).Select(value => value!.GetValue<string>()).Distinct(StringComparer.Ordinal).Order(UnicodeCodePointComparer.Instance).Select(value => JsonValue.Create(value)).ToArray());
    private static void RequireKind(JsonNode value, params JsonValueKind[] kinds)
    { if (!kinds.Contains(value.GetValueKind())) throw new ArgumentException("Malformed source preference metadata"); }
}
