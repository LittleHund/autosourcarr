using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Sources;

internal static class SourceResponseContract
{
    public static List<JsonObject> ConnectionNodes(JsonNode? value)
    {
        JsonArray? nodes;
        if (value is JsonArray array) nodes = array;
        else if (value is JsonObject connection && connection.ContainsKey("nodes")) nodes = connection["nodes"] as JsonArray;
        else if (value is JsonObject edgesConnection && edgesConnection["edges"] is JsonArray edges)
        {
            if (edges.Any(edge => edge is not JsonObject || edge["node"] is not JsonObject))
                throw new SuwayomiException("Invalid GraphQL connection edges");
            return edges.Select(edge => (JsonObject)edge!["node"]!.DeepClone()).ToList();
        }
        else throw new SuwayomiException("Incomplete GraphQL connection");
        if (nodes is null || nodes.Any(node => node is not JsonObject))
            throw new SuwayomiException("Invalid GraphQL connection nodes");
        return nodes.Select(node => (JsonObject)node!.DeepClone()).ToList();
    }

    public static bool TryLong(JsonNode? value, out long number)
    {
        number = 0;
        return value?.GetValueKind() == JsonValueKind.Number
            && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);
    }

    public static bool PositiveId(JsonNode? value, out long number) => TryLong(value, out number) && number > 0;
    public static string? Identity(JsonNode? value) => value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>()
        : TryLong(value, out var number) ? number.ToString(CultureInfo.InvariantCulture) : null;
    public static bool Boolean(JsonNode? value, out bool result)
    {
        result = false;
        return value is JsonValue node && node.TryGetValue(out result);
    }
    public static string Text(JsonNode? value) => value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : "";
    public static JsonArray Array(IEnumerable<JsonObject> values) => new(values.Select(value => (JsonNode?)value.DeepClone()).ToArray());
}
