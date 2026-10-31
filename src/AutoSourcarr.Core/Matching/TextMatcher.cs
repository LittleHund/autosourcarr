using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Collections;
using Unidecode.NET;

namespace AutoSourcarr.Core.Matching;

public sealed record MatchDecision(
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("title_score")] double TitleScore = 0,
    [property: JsonPropertyName("description_score")] double DescriptionScore = 0);

public static partial class TextMatcher
{
    private static readonly BoundedLruCache<string, string> Titles = new(32768);
    private static readonly HashSet<string> TrustedSingleWordKinds = ["source", "source-variant", "main-library"];
    private static readonly HashSet<string> Continuations = ["a", "an", "the", "of", "at", "to", "in", "for", "with", "from", "by", "and", "or", "as", "on", "into", "about"];
    private static readonly HashSet<string> Pronouns = ["i", "you", "he", "she", "we", "they", "it", "didn", "didnt", "isn", "isnt", "aren", "arent", "wasn", "wasnt", "right"];
    private static readonly HashSet<string> RomajiHints = ["no", "wo", "o", "ni", "wa", "ga", "kara", "made", "desu", "suki", "ageru", "boku", "watashi", "ore", "kanojo", "kare", "sekai", "isekai", "tensei", "shoujo", "shojo", "shounen", "shonen", "san", "kun", "chan", "sama", "sensei", "senpai", "kouhai"];
    private const string EditionTrim = " -–—:[]()（）【】";
    private static Regex Pattern(string name) => TextPatterns.Get(name);
    private static string Nfkc(string? value) => (value ?? "").Normalize(NormalizationForm.FormKC);
    private static string Ascii(string value) => Unidecoder.Unidecode(value, UnidecodeAlgorithm.Complete, null).ToLowerInvariant();
    private static string Collapse(string value) => Pattern("SPACE_RE").Replace(value, " ").Trim();

    public static string NormalizeTitle(string? value) => Titles.GetOrAdd(value ?? "", raw =>
        Collapse(Pattern("PUNCT_RE").Replace(Ascii(Nfkc(raw)).Replace("&", " and ", StringComparison.Ordinal), " ")));

    public static string TitleMediaVariant(string? value)
    {
        var raw = Nfkc(value);
        var webtoon = Pattern("WEBTOON_TITLE_RE").IsMatch(raw);
        var manga = Pattern("MANGA_TITLE_RE").IsMatch(raw);
        return webtoon == manga ? "unknown" : webtoon ? "webtoon" : "manga";
    }

    public static string MediaBaseTitle(string? value)
    {
        var raw = Pattern("MEDIA_SUFFIX_RE").Replace(Nfkc(value), "");
        raw = Pattern("WEBTOON_TITLE_RE").Replace(raw, " ");
        raw = Pattern("MANGA_TITLE_RE").Replace(raw, " ");
        return MultipleSpaces().Replace(raw, " ").Trim(EditionTrim.ToCharArray()).Trim();
    }

    public static string NormalizedMediaBase(string? value) => NormalizeTitle(MediaBaseTitle(value));

    public static string TitleColorVariant(string? value)
    {
        var raw = Nfkc(value);
        var mono = Pattern("MONO_BRACKET_RE").IsMatch(raw) || Pattern("MONO_SUFFIX_RE").IsMatch(raw);
        var color = Pattern("COLOR_BRACKET_RE").IsMatch(raw) || Pattern("COLOR_SUFFIX_RE").IsMatch(raw);
        return mono == color ? "unknown" : mono ? "monochrome" : "colored";
    }

    public static string ColorBaseTitle(string? value)
    {
        var raw = Nfkc(value);
        if (TitleColorVariant(raw) == "unknown") return raw.Trim();
        raw = Pattern("COLOR_BRACKET_RE").Replace(raw, " ");
        raw = Pattern("MONO_BRACKET_RE").Replace(raw, " ");
        raw = Pattern("COLOR_SUFFIX_RE").Replace(raw, "");
        raw = Pattern("MONO_SUFFIX_RE").Replace(raw, "");
        return MultipleSpaces().Replace(raw, " ").Trim(EditionTrim.ToCharArray()).Trim();
    }

    public static string NormalizedColorBase(string? value) => NormalizeTitle(ColorBaseTitle(value));
    public static bool IsMeaningfulTitle(string? value)
    {
        var normalized = NormalizeTitle(value);
        return normalized.Length >= 2 && !Pattern("PLACEHOLDER_TITLE_RE").IsMatch(normalized);
    }
    public static bool IsSingleWordTitle(string? value)
    {
        var normalized = NormalizeTitle(value);
        return normalized.Length > 0 && !normalized.Contains(' ');
    }
    public static bool IsMatchEvidenceTitle(string? value, string? titleType = "", bool primary = false)
    {
        if (!IsMeaningfulTitle(value)) return false;
        var kind = (titleType ?? "").Trim().ToLowerInvariant();
        if (kind is "matched-alias" or "cached" or "cached-title" || kind.StartsWith("translated-", StringComparison.Ordinal))
            return false;
        return !IsSingleWordTitle(value) || primary || TrustedSingleWordKinds.Contains(kind);
    }

    public static bool IsAliasFragment(string? value)
    {
        var raw = Collapse(value ?? "").Trim(" \t-•".ToCharArray());
        if (raw.Length == 0) return true;
        var tokens = NormalizeTitle(raw).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 5) return false;
        var wrapped = "~～〜".Contains(raw[^1], StringComparison.Ordinal) || "~～〜".Contains(raw[0], StringComparison.Ordinal);
        var question = Interrogative().IsMatch(raw);
        if ((wrapped || question) && Pattern("_ALIAS_FRAGMENT_RE").IsMatch(raw)) return true;
        if (tokens.Length <= 4 && question && Pronouns.Contains(tokens.FirstOrDefault() ?? "")) return true;
        return tokens.Length is >= 2 and <= 12 && Continuations.Contains(tokens[^1]);
    }

    public static double TitleSimilarity(string? left, string? right)
    {
        if (!IsMeaningfulTitle(left) || !IsMeaningfulTitle(right)) return 0;
        var a = NormalizeTitle(left); var b = NormalizeTitle(right);
        if (a == b) return 1;
        var ratio = IndelSimilarity.Ratio(a, b);
        return Math.Min(a.Split(' ').Length, b.Split(' ').Length) <= 2 ? ratio : Math.Max(ratio, IndelSimilarity.TokenSet(a, b));
    }

    public static double AutomaticTitleSimilarity(string? left, string? right) =>
        !IsMeaningfulTitle(left) || !IsMeaningfulTitle(right) ? 0 : IndelSimilarity.Ratio(NormalizeTitle(left), NormalizeTitle(right));

    public static (double Score, string Left, string Right) BestAutomaticTitleScore(IEnumerable<string> left, IEnumerable<string> right)
    {
        var best = (Score: 0.0, Left: "", Right: "");
        var candidates = right.ToArray();
        foreach (var a in left.Where(IsMeaningfulTitle))
            foreach (var b in candidates.Where(IsMeaningfulTitle))
            {
                var score = AutomaticTitleSimilarity(a, b);
                if (score > best.Score) best = (score, a, b);
            }
        return best;
    }

    public static double NameSimilarity(string? left, string? right)
    {
        var a = NormalizeTitle(left); var b = NormalizeTitle(right);
        return a.Length == 0 || b.Length == 0 ? 0 : Math.Max(IndelSimilarity.Ratio(a, b), IndelSimilarity.TokenSet(a, b));
    }

    public static (double Score, int Strong) BestTitleScore(IEnumerable<string> left, IEnumerable<string> right)
    {
        var a = left.Where(IsMeaningfulTitle).ToArray();
        var b = right.Where(IsMeaningfulTitle).ToArray();
        var best = 0.0; var second = 0.0; var strong = 0;
        foreach (var l in a)
            foreach (var r in b)
            {
                var score = TitleSimilarity(l, r);
                if (score >= .97) strong++;
                if (score > best) { second = best; best = score; }
                else if (score > second) second = score;
            }
        if (a.Length == 0 || b.Length == 0) return (0, 0);
        return (strong >= 2 ? Math.Max(best, .995) : second >= .92 ? Math.Min(1, best + .025) : best, strong);
    }

    public static bool LooksEnglish(string? value)
    {
        var letters = (value ?? "").EnumerateRunes().Where(UnicodeProperties.IsLetter).ToArray();
        return letters.Length > 0 && letters.Count(UnicodeProperties.IsLatin) / (double)letters.Length >= .85;
    }
    public static bool LooksRomanizedJapanese(string? value) => NormalizeTitle(value).Split(' ').ToHashSet(StringComparer.Ordinal).Count(RomajiHints.Contains) >= 2;
    public static bool LooksPreferredEnglish(string? value) => LooksEnglish(value) && !LooksRomanizedJapanese(value);

    public static string StripKnownSourceSuffix(string? value, IEnumerable<string>? sourceNames = null)
    {
        var raw = Collapse(Nfkc(value));
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in sourceNames ?? [])
            foreach (var variant in new[] { Collapse(name), Pattern("_SOURCE_LANG_SUFFIX_RE").Replace(Collapse(name), "").Trim() })
            {
                var normalized = NormalizeTitle(variant);
                if (normalized.Length == 0) continue;
                wanted.Add(normalized);
                if (normalized.EndsWith(" scans", StringComparison.Ordinal)) wanted.Add(normalized[..^6].Trim());
            }
        if (raw.Length == 0 || wanted.Count == 0) return raw;
        var match = Pattern("_SOURCE_SUFFIX_SEPARATOR_RE").Match(raw);
        if (!match.Success || !wanted.Contains(NormalizeTitle(match.Groups[1].Value))) return raw;
        var cleaned = raw[..match.Index].Trim(" \t-–—|·•".ToCharArray());
        return IsMeaningfulTitle(cleaned) ? cleaned : raw;
    }

    public static MatchDecision ScoreMatch(IEnumerable<string> candidateTitles, IEnumerable<string> entryTitles,
        string candidateAuthor = "", string entryAuthor = "", string candidateDescription = "", IEnumerable<string>? entryDescriptions = null)
    {
        var (title, strong) = BestTitleScore(candidateTitles, entryTitles);
        var description = BestDescriptionScore(candidateDescription, entryDescriptions ?? []);
        var score = title;
        var reason = strong >= 2 ? "two-title corroboration" : "title";
        if (description >= .90) { score = Math.Max(score, .995); reason = strong < 2 ? "description>=90%" : reason + "+description>=90%"; }
        else if (description >= .75) { score = Math.Min(1, score + .02); reason += "+description"; }
        if (candidateAuthor.Length > 0 && entryAuthor.Length > 0)
        {
            var author = NameSimilarity(candidateAuthor, entryAuthor);
            if (author >= .92) { score = Math.Min(1, score + .025); reason += "+author"; }
            else if (author < .35) { score = Math.Max(0, score - .08); reason += "-author"; }
        }
        return new(score, reason, title, description);
    }

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultipleSpaces();
    [GeneratedRegex(@"[?!][~～〜]?$", RegexOptions.CultureInvariant)]
    private static partial Regex Interrogative();
}
