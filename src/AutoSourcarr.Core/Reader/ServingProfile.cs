using System.Text;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Reader;

/// <summary>Preferences only rank a candidate set already admitted by identity and edition gates.</summary>
public sealed record ServingProfile(IReadOnlyList<string> Languages, IReadOnlyList<string> Sources, string Quality)
{
    public static ServingProfile Default => new([], [], "default");
    public bool IsDefault => Languages.Count == 0 && Sources.Count == 0 && Quality == "default";
    public JsonObject ToJson() => new()
    {
        ["languages"] = new JsonArray(Languages.Select(item => JsonValue.Create(item)).ToArray()),
        ["sources"] = new JsonArray(Sources.Select(item => JsonValue.Create(item)).ToArray()), ["quality"] = Quality
    };

    public static ServingProfile Parse(JsonNode? value)
    {
        if (value is not JsonObject obj || obj.Any(pair => pair.Key is not ("languages" or "sources" or "quality")))
            throw new ArgumentException("Unknown profile field");
        var languages = Identifiers(obj, "languages"); var sources = Identifiers(obj, "sources");
        var quality = !obj.ContainsKey("quality") ? "default" : obj["quality"] is JsonValue item && item.TryGetValue<string>(out var text) ? text : "";
        if (quality is not ("default" or "quality" or "complete")) throw new ArgumentException("Unknown quality preference");
        return new(languages, sources, quality);
    }

    public void Rank(IReadOnlyList<JsonObject> candidates, IReadOnlyDictionary<string, string> sourceLanguages)
    {
        if (candidates.Count == 0 || IsDefault) return;
        var maximum = candidates.Max(row => Math.Max(0, ReaderValue.Integer(row, "page_count")));
        foreach (var row in candidates)
        {
            var source = ReaderValue.Text(row, "source_id");
            var score = ReaderValue.Number(row, "resolver_score");
            score += Preference(Languages, sourceLanguages.GetValueOrDefault(source, ""), 1000);
            score += Preference(Sources, source, 100);
            if (Quality == "quality") score += Math.Clamp(ReaderValue.Number(row, "quality_score"), 0, 100) / 100 * 20;
            if (Quality == "complete" && maximum > 0) score += 20.0 * Math.Max(0, ReaderValue.Integer(row, "page_count")) / maximum;
            row["resolver_score"] = score;
        }
    }

    private static string[] Identifiers(JsonObject value, string name)
    {
        if (!value.ContainsKey(name)) return [];
        if (value[name] is not JsonArray array || array.Count > 32) throw new ArgumentException("Use at most 32 language or source identifiers");
        var result = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in array)
        {
            if (node is not JsonValue item || !item.TryGetValue<string>(out var text) || text.Length == 0 || text.EnumerateRunes().Count() > 100)
                throw new ArgumentException("Use at most 32 language or source identifiers");
            if (seen.Add(text)) result.Add(text);
        }
        return result.ToArray();
    }

    private static double Preference(IReadOnlyList<string> values, string value, double weight)
    {
        for (var index = 0; index < values.Count; index++) if (values[index] == value) return weight * (values.Count - index) / values.Count;
        return 0;
    }
}

public static class ReaderValue
{
    public static string Text(JsonObject row, string key, string fallback = "") => row[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
    public static long Integer(JsonObject row, string key, long fallback = 0) => row[key] is JsonValue value ? value.TryGetValue<long>(out var number) ? number : value.TryGetValue<int>(out var integer) ? integer : fallback : fallback;
    public static double Number(JsonObject row, string key, double fallback = 0) => row[key] is JsonValue value ? value.TryGetValue<double>(out var number) ? number : value.TryGetValue<long>(out var integer) ? integer : value.TryGetValue<int>(out var small) ? small : fallback : fallback;
    public static bool Flag(JsonObject row, string key) => row[key] is JsonValue value && (value.TryGetValue<bool>(out var boolean) ? boolean : value.TryGetValue<long>(out var number) && number != 0);
}
