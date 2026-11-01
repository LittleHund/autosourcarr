using System.Globalization;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Matching;

namespace AutoSourcarr.Core.Imports;

/// <summary>
/// The search hints and stable keys of backup imports (service.py module helpers _fallback_title_queries,
/// _source_url_title_queries, _backup_source_search_queries and the static _backup_url_key,
/// _import_rejection_source_family, _backup_source_id_candidates). Search hints never become identity evidence: a
/// result found through a fallback query still has to match by source URL or the strict import gate.
/// </summary>
public static partial class ImportQueries
{
    /// <summary>
    /// IMP-7 second-pass queries: the title without edition and format labels such as (Colored), (Webtoon), (Manga),
    /// (Official) and without a leading "The" (only for titles of four or more words, so "The Hero Returns" keeps its
    /// article). At most three; the exact title itself is never repeated.
    /// </summary>
    public static IReadOnlyList<string> FallbackTitleQueries(string? title)
    {
        var raw = Collapse(title);
        if (raw.Length == 0) return [];
        var output = new List<string>();
        var rawKey = TextMatcher.NormalizeTitle(raw);
        void Add(string value)
        {
            value = Collapse(value);
            var key = TextMatcher.NormalizeTitle(value);
            if (value.Length == 0 || key.Length == 0 || key == rawKey) return;
            if (output.All(existing => TextMatcher.NormalizeTitle(existing) != key)) output.Add(value);
        }
        var labelFree = WithoutLabels(raw);
        var allowArticle = WordPattern().Matches(labelFree).Count >= 4;
        var withoutThe = LeadingThe().Replace(raw, "").Trim();
        if (allowArticle && withoutThe != raw) Add(withoutThe);
        if (labelFree != raw)
        {
            Add(labelFree);
            if (allowArticle) Add(LeadingThe().Replace(labelFree, "").Trim());
        }
        return output.Take(3).ToArray();
    }

    /// <summary>The title without colour, media and the extra edition labels (repeated until stable).</summary>
    public static string WithoutLabels(string? title)
    {
        var current = Collapse(title);
        for (var round = 0; round < 4; round++)
        {
            var next = TextMatcher.ColorBaseTitle(current);
            if (TextMatcher.TitleMediaVariant(next) != "unknown") next = TextMatcher.MediaBaseTitle(next);
            next = Collapse(ExtraLabel().Replace(next, ""));
            if (next.Length == 0 || next == current) break;
            current = next;
        }
        return current;
    }

    /// <summary>A search hint from the backup URL slug (MangaFire-style URLs keep the romanized title); empty when too short.</summary>
    public static IReadOnlyList<string> SourceUrlTitleQueries(string? value)
    {
        var raw = (value ?? "").Trim();
        if (raw.Length == 0) return [];
        raw = Uri.UnescapeDataString(raw);
        var path = raw;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") path = absolute.AbsolutePath;
        else
        {
            var cut = path.IndexOfAny(['?', '#']);
            if (cut >= 0) path = path[..cut];
        }
        var leaf = path.TrimEnd('/');
        var slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];
        leaf = FileExtension().Replace(leaf, "");
        leaf = Separators().Replace(leaf, " ");
        leaf = Collapse(leaf).Trim(' ', '.', '-', '_');
        return leaf.Length < 4 || !Letters().IsMatch(leaf) ? [] : [leaf];
    }

    /// <summary>
    /// _backup_source_search_queries: the preferred (or backup) title first, descriptive URL slugs, its fallbacks, the
    /// other URL hints, then the backup's own title and fallbacks when a different query was given. At most eight.
    /// </summary>
    public static IReadOnlyList<string> BackupSourceSearchQueries(string? backupTitle, string? backupUrl, string? preferred = "")
    {
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string value)
        {
            value = Collapse(value);
            var key = TextMatcher.NormalizeTitle(value);
            if (key.Length == 0 || !seen.Add(key)) return;
            output.Add(value);
        }
        var original = (backupTitle ?? "").Trim();
        var primary = (preferred ?? "").Trim().Length > 0 ? preferred!.Trim() : original;
        Add(primary);
        var hints = SourceUrlTitleQueries(backupUrl);
        foreach (var hint in hints.Where(hint => TextMatcher.NormalizeTitle(hint).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3)) Add(hint);
        foreach (var fallback in FallbackTitleQueries(primary)) Add(fallback);
        foreach (var hint in hints) Add(hint);
        if ((preferred ?? "").Trim().Length > 0 && TextMatcher.NormalizeTitle(original) != TextMatcher.NormalizeTitle(preferred))
        {
            Add(original);
            foreach (var fallback in FallbackTitleQueries(original)) Add(fallback);
        }
        return output.Take(8).ToArray();
    }

    /// <summary>_backup_url_key: the source-relative URL (path and query of an absolute one) without a trailing slash.</summary>
    public static string BackupUrlKey(string? value)
    {
        var raw = (value ?? "").Trim();
        if (raw.Length == 0) return "";
        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute) && absolute.Host.Length > 0 && raw.Contains("://", StringComparison.Ordinal))
            raw = absolute.AbsolutePath + absolute.Query;
        return raw.TrimEnd('/');
    }

    /// <summary>_import_rejection_source_family: the source name without a trailing language tag, normalized.</summary>
    public static string RejectionSourceFamily(string? value) => TextMatcher.NormalizeTitle(LanguageTag().Replace(Collapse(value), "").Trim());

    /// <summary>The source name without a trailing "(EN)"-style language tag, and that tag (lower case, empty when none).</summary>
    public static (string Base, string Language) SplitLanguageTag(string? value)
    {
        var raw = Collapse(value);
        var match = LanguageTagCapture().Match(raw);
        return match.Success ? (raw[..match.Index].Trim(), match.Groups[1].Value.ToLowerInvariant()) : (raw, "");
    }

    /// <summary>_backup_source_id_candidates: the signed id and, for a negative one, its unsigned spelling.</summary>
    public static IReadOnlyList<string> SourceIdCandidates(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
            return signed < 0 ? [text, unchecked((ulong)signed).ToString(CultureInfo.InvariantCulture)] : [signed.ToString(CultureInfo.InvariantCulture)];
        if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var unsigned))
            return [text, unchecked((long)unsigned).ToString(CultureInfo.InvariantCulture)];
        return [];
    }

    internal static string Collapse(string? value) => Spaces().Replace(value ?? "", " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
    [GeneratedRegex(@"^the\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingThe();
    [GeneratedRegex(@"[\w’'-]+")]
    private static partial Regex WordPattern();
    [GeneratedRegex(@"\s*[\(\[]\s*(?:official|digital|uncensored|english|remastered|full[\s-]?colou?r|colou?red|webtoon|manga|manhwa|manhua|comic)\s*[\)\]]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExtraLabel();
    [GeneratedRegex(@"\.[a-z0-9]{3,16}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileExtension();
    [GeneratedRegex(@"[-_+]+")]
    private static partial Regex Separators();
    [GeneratedRegex(@"[A-Za-z\u0080-￿]")]
    private static partial Regex Letters();
    [GeneratedRegex(@"\s*[\[(]\s*[a-z]{2,3}\s*[\])]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTag();
    [GeneratedRegex(@"\s*[\[(]\s*([a-z]{2,3})\s*[\])]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTagCapture();
}
