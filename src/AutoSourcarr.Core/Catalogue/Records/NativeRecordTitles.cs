using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Text;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Catalogue.Tags;

namespace AutoSourcarr.Core.Catalogue.Records;

/// <summary>Source-scoped search aliases. The record store owns the gate, proof and separate commit phases.</summary>
internal static class NativeRecordTitles
{
    internal sealed record Alias(string Title, string Normalized, string Language, string Type);

    public static bool HasDescription(JsonObject manga) => UnicodeText.Trim(NativeRecordStore.SourceText(manga["description"])).Length > 0;
    public static void Clear(DatabaseSession catalog, NativeTagRecord owner, bool hasDescription)
    {
        catalog.Execute("DELETE FROM titles WHERE entry_id=$entry AND source_id=$source AND source_manga_id=$manga AND title_type='matched-alias'",
            ("$entry", owner.CanonicalEntryId), ("$source", owner.SourceId), ("$manga", owner.SourceMangaId));
        if (hasDescription) catalog.Execute("DELETE FROM titles WHERE entry_id=$entry AND source_id=$source AND source_manga_id=$manga AND title_type='description-alias'",
            ("$entry", owner.CanonicalEntryId), ("$source", owner.SourceId), ("$manga", owner.SourceMangaId));
    }
    public static IEnumerable<Alias> Aliases(JsonObject manga, string language, bool hasDescription)
    {
        static Alias? Make(string title, string language, string type)
        {
            title = UnicodeText.Trim(title); if (!TextMatcher.IsMeaningfulTitle(title)) return null;
            var normalized = TextMatcher.NormalizeTitle(title); return normalized.Length == 0 ? null : new(title, normalized, language, type);
        }
        // Keep these checks in the iterator: the original clear phase has already
        // committed, and a description failure follows any committed title alias.
        if (Make(AliasText(manga["title"]), language, "matched-alias") is { } matched) yield return matched;
        if (hasDescription) foreach (var title in TextMatcher.ExtractAltTitles(AliasText(manga["description"])))
            if (Make(title, language, "description-alias") is { } alias) yield return alias;
    }
    private static string AliasText(JsonNode? value)
    {
        if (!NativeTagJson.Truth(value)) return "";
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return text;
        throw new InvalidDataException("A source search alias requires native text; the earlier row and title-clear phases remain committed.");
    }
    public static void Add(DatabaseSession catalog, NativeTagRecord owner, Alias alias) => catalog.Execute("""
        INSERT INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id)
        VALUES($entry,$title,$normalized,$language,$type,$source,$manga)
        ON CONFLICT(entry_id,normalized,source_id,title_type) DO UPDATE SET
          title=excluded.title,
          language=CASE WHEN excluded.language<>'' THEN excluded.language ELSE titles.language END,
          source_manga_id=CASE WHEN excluded.source_manga_id<>0 THEN excluded.source_manga_id ELSE titles.source_manga_id END
        """, ("$entry", owner.CanonicalEntryId), ("$title", alias.Title), ("$normalized", alias.Normalized), ("$language", alias.Language),
        ("$type", alias.Type), ("$source", owner.SourceId), ("$manga", owner.SourceMangaId));
}
