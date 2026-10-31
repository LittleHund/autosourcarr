using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Unicode;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Sources.Configuration;

internal static class SourceConfigurationJson
{
    public static JsonObject Json(DatabaseRow row) => JsonSerializer.SerializeToNode(row.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))!.AsObject();
    public static bool Flag(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out var boolean) && boolean;
    public static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
    public static string First(params JsonNode?[] values) => values.Where(Truth).Select(String).FirstOrDefault() ?? "";
    public static string String(JsonNode? value) => value is null ? "None" : value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value is JsonValue boolean && boolean.TryGetValue<bool>(out var flag) ? flag ? "True" : "False" : value.ToJsonString();
    public static string Identity(JsonNode? value) => value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String ? scalar.GetValue<string>()
        : value is JsonValue number && number.GetValueKind() == JsonValueKind.Number && !number.ToJsonString().ContainsAny('.', 'e', 'E') ? number.ToJsonString() : "";
    public static bool Truth(JsonNode? value) => value switch { null => false, JsonObject obj => obj.Count > 0, JsonArray array => array.Count > 0,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text.Length > 0,
        JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag,
        JsonValue scalar when scalar.GetValueKind() == JsonValueKind.Number => Number(scalar) != 0, _ => true };
    public static double Number(JsonNode value) => double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
    public static long Integer(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<bool>(out var boolean)) return boolean ? 1 : 0;
        if (value is JsonValue number && number.GetValueKind() == JsonValueKind.Number && (number.ToJsonString().ContainsAny('.', 'e', 'E')))
            return checked((long)Math.Truncate(Number(number)));
        if (long.TryParse(String(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        throw new ArgumentException("Expected an integer");
    }
    public static JsonArray Array(IEnumerable<JsonObject> values) => new(values.Select(value => (JsonNode)value).ToArray());
    public static string Timestamp(TimeProvider clock)
    {
        var now = clock.GetUtcNow().ToUniversalTime(); var micros = now.Ticks % TimeSpan.TicksPerSecond / 10;
        return now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (micros == 0 ? "" : "." + micros.ToString("D6", CultureInfo.InvariantCulture)) + "+00:00";
    }
    public static string PythonJson(JsonNode? value, bool sortKeys = false, bool compact = false)
    {
        var output = new StringBuilder();
        void Write(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                output.Append('{'); var first = true; IEnumerable<KeyValuePair<string, JsonNode?>> pairs = sortKeys ? obj.OrderBy(pair => pair.Key, UnicodeCodePointComparer.Instance) : obj;
                foreach (var pair in pairs) { if (!first) output.Append(compact ? "," : ", "); first = false; output.Append(CanonicalJson.String(pair.Key)).Append(compact ? ":" : ": "); Write(pair.Value); }
                output.Append('}');
            }
            else if (node is JsonArray array)
            { output.Append('['); for (var index = 0; index < array.Count; index++) { if (index > 0) output.Append(compact ? "," : ", "); Write(array[index]); } output.Append(']'); }
            else output.Append(CanonicalJson.Text(node));
        }
        Write(value); return output.ToString();
    }
    private static bool ContainsAny(this string value, params char[] characters) => value.IndexOfAny(characters) >= 0;
}
