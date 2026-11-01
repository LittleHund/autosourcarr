using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Catalogue.Tags;

namespace AutoSourcarr.Core.Matching.Scoring;

/// <summary>
/// The evidence of a matching rule's "tags" check: canonical tag keys of a record's genres and tags through the tag
/// synonym map (built-in, user and reviewed equivalences), language / category / type / uploader namespaces excluded,
/// compared as the best Jaccard overlap of two records.
/// </summary>
public static class PolicyTags
{
    private static readonly HashSet<string> IgnoredNamespaces = new(StringComparer.Ordinal) { "language", "category", "type", "uploader" };

    /// <summary>Canonical keys of a payload's genre (array or comma-separated text) and tags (strings or {namespace,name}).</summary>
    public static HashSet<string> Keys(JsonNode? genre, JsonNode? tags, TagSynonymMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var nodes = new List<JsonNode?>();
        switch (genre)
        {
            case JsonArray genres: nodes.AddRange(genres); break;
            case JsonValue text when text.TryGetValue<string>(out var joined):
                nodes.AddRange(joined.Split(',').Select(name => name.Trim()).Where(name => name.Length > 0).Select(name => (JsonNode?)JsonValue.Create(name)));
                break;
        }
        if (tags is JsonArray list) nodes.AddRange(list);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (GalleryTagEquivalence.Tag(node) is not { } parsed || IgnoredNamespaces.Contains(parsed["namespace"]?.GetValue<string>() ?? "")) continue;
            if (map.CanonicalKey(node) is { } key) keys.Add(key);
        }
        return keys;
    }

    /// <summary>The best overlap (shared / all keys) of any non-empty left and right key set; null when either side has no tags.</summary>
    public static double? Overlap(IEnumerable<IReadOnlySet<string>> left, IEnumerable<IReadOnlySet<string>> right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        var a = left.Where(set => set.Count > 0).ToArray(); var b = right.Where(set => set.Count > 0).ToArray();
        if (a.Length == 0 || b.Length == 0) return null;
        return a.SelectMany(x => b.Select(y => (double)x.Intersect(y).Count() / x.Union(y).Count())).Max();
    }
}
