using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Sharing;

internal static class SharingJson
{
    public static string S(this JsonNode? node, string key, string fallback = "") => node?[key]?.GetValue<string>() ?? fallback;
    public static JsonArray A(this JsonNode? node, string key) => (JsonArray)node![key]!;
    public static JsonObject O(this JsonNode? node, string key) => (JsonObject)node![key]!;
    public static JsonObject Copy(this JsonObject node) => (JsonObject)node.DeepClone();
    public static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    public static JsonArray Numbers(IEnumerable<long> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    public static JsonArray Objects(IEnumerable<JsonObject> values) => new(values.Select(value => (JsonNode?)value.DeepClone()).ToArray());
    public static JsonObject Row(DatabaseRow row) => JsonSerializer.SerializeToNode(row.ToDictionary(pair => pair.Key, pair => pair.Value))!.AsObject();
    public static (string Name, object? Value)[] P(params object?[] values) => values.Select((value, index) => ("$p" + index, value)).ToArray();
    public static string Stamp() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
}
