using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Core.Catalogue.Tags;

/// <summary>Only the legacy tag store's JSON truth, sequence and text-emission rules.</summary>
internal static class NativeTagJson
{
    public static bool Truth(JsonNode? value) => value switch
    {
        null => false, JsonArray array => array.Count != 0, JsonObject obj => obj.Count != 0,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text.Length != 0,
        JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag,
        JsonValue scalar when scalar.GetValueKind() == JsonValueKind.Number => double.Parse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture) != 0,
        _ => true
    };

    // Python's `v not in ('', None, [], {})` retains false and numeric zero.
    public static bool OverlayValue(JsonNode? value) => value switch
    {
        null => false, JsonArray array => array.Count != 0, JsonObject obj => obj.Count != 0,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text.Length != 0, _ => true
    };

    public static IEnumerable<JsonNode?> Sequence(JsonNode? value)
    {
        if (!Truth(value)) return [];
        if (value is JsonArray array) return array;
        if (value is JsonObject obj) return obj.Select(pair => (JsonNode?)JsonValue.Create(pair.Key));
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            return text.EnumerateRunes().Select(rune => (JsonNode?)JsonValue.Create(rune.ToString()));
        throw new InvalidDataException("Retained gallery tags are not an iterable JSON value.");
    }

    public static string Timestamp(TimeProvider clock)
    {
        var value = clock.GetUtcNow().ToUniversalTime(); var micros = value.Ticks % TimeSpan.TicksPerSecond / 10;
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            + (micros == 0 ? "" : "." + micros.ToString("D6", CultureInfo.InvariantCulture)) + "+00:00";
    }

    /// <summary>Original json.dumps(ensure_ascii=False), with its spaces and insertion order.</summary>
    public static string Text(JsonNode? value)
    {
        var output = new StringBuilder();
        void String(string text)
        {
            output.Append('"');
            foreach (var character in text)
                output.Append(character switch
                {
                    '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                    < ' ' => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture), _ => character.ToString()
                });
            output.Append('"');
        }
        void Write(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                output.Append('{'); var first = true;
                foreach (var pair in obj) { if (!first) output.Append(", "); first = false; String(pair.Key); output.Append(": "); Write(pair.Value); }
                output.Append('}');
            }
            else if (node is JsonArray array)
            {
                output.Append('['); for (var index = 0; index < array.Count; index++) { if (index != 0) output.Append(", "); Write(array[index]); } output.Append(']');
            }
            else if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text)) String(text);
            // The existing canonical writer supplies the original scalar float
            // spelling (2.0, negative zero, exponents), not its object/string rules.
            else output.Append(CanonicalJson.Text(node));
        }
        Write(value); return output.ToString();
    }
}
