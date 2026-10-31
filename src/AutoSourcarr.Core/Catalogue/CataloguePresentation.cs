using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Text;

namespace AutoSourcarr.Core.Catalogue;

public sealed record CatalogueSourceDisplay(string Name, string BaseUrl, string Language, long Priority);

/// <summary>Released catalogue display semantics; these labels never establish identity.</summary>
public static class CataloguePresentation
{
    public static string StatusLabel(long value) => value switch
    {
        1 => "Ongoing", 2 => "Completed", 3 => "Licensed", 4 => "Publishing finished",
        5 => "Cancelled", 6 => "Hiatus", _ => "Unknown"
    };

    public static IReadOnlyList<string> MissingMetadata(JsonObject entry)
    {
        var missing = new List<string>();
        if (UnicodeText.Trim(Text(entry, "thumbnail_url")).Length == 0) missing.Add("thumbnail");
        if (UnicodeText.Trim(Text(entry, "description")).EnumerateRunes().Count() < 40) missing.Add("description");
        foreach (var field in new[] { "author", "artist", "genre" })
            if (UnicodeText.Trim(Text(entry, field)).Length == 0) missing.Add(field);
        if (Integer(entry, "status") == 0) missing.Add("status");
        return missing;
    }

    public static IReadOnlyList<string> SourceFamilies(IEnumerable<CatalogueSourceDisplay> sources)
    {
        var groups = new Dictionary<(string Name, string Base), Family>();
        foreach (var source in sources)
        {
            var key = (UnicodeCaseFold.Fold(source.Name), UnicodeCaseFold.Fold(source.BaseUrl));
            if (!groups.TryGetValue(key, out var family)) groups.Add(key, family = new(source.Name, source.Priority));
            if (source.Language.Length != 0) family.Languages.Add(source.Language);
            family.Priority = Math.Min(family.Priority, source.Priority);
        }
        return groups.Values.OrderBy(family => family.Priority)
            .ThenBy(family => UnicodeCaseFold.Fold(family.Name), UnicodeCodePointComparer.Instance)
            .Select(family => family.Languages.Count == 0 ? family.Name : family.Name + " (" + string.Join(", ", family.Languages) + ")").ToArray();
    }

    public static string DescriptionForExtension(JsonObject entry)
    {
        var preferred = TextMatcher.NormalizeTitle(Text(entry, "preferred_title"));
        var seen = new HashSet<string>(StringComparer.Ordinal); var aliases = new List<string>();
        foreach (var title in (entry["titles"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Text(title, "title_type").StartsWith("translated-", StringComparison.Ordinal)) continue;
            var normalized = Text(title, "normalized");
            if (normalized.Length != 0 && normalized != preferred && seen.Add(normalized)) aliases.Add(Text(title, "title"));
        }
        var sources = SourceFamilies((entry["matches"] as JsonArray ?? []).OfType<JsonObject>().Select(match =>
        {
            var name = Text(match, "source_name"); var priority = Integer(match, "source_priority");
            return new CatalogueSourceDisplay(name.Length == 0 ? Text(match, "source_id") : name,
                Text(match, "base_url"), Text(match, "source_lang"), priority == 0 ? 1000 : priority);
        }));
        var parts = new List<string> { UnicodeText.Trim(Text(entry, "description")) };
        if (aliases.Count != 0) parts.Add("Alternate titles\n" + string.Join('\n', aliases));
        if (sources.Count != 0) parts.Add("Sources\n" + string.Join('\n', sources));
        return string.Join("\n\n────────────\n\n", parts.Where(part => part.Length != 0));
    }

    private static string Text(JsonObject value, string name) => value[name] is JsonValue text && text.TryGetValue<string>(out var result) ? result : "";
    private static long Integer(JsonObject value, string name)
    {
        if (value[name] is not JsonValue number) return 0;
        if (number.TryGetValue<long>(out var result)) return result;
        if (number.TryGetValue<int>(out var integer)) return integer;
        return 0;
    }
    private sealed class Family(string name, long priority)
    {
        public string Name { get; } = name;
        public long Priority { get; set; } = priority;
        public SortedSet<string> Languages { get; } = new(UnicodeCodePointComparer.Instance);
    }
}
