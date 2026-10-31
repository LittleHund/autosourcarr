using System.Text;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Collections;

namespace AutoSourcarr.Core.Matching;

public static partial class TextMatcher
{
    private static readonly BoundedLruCache<string, string> Summaries = new(8192);
    private static readonly BoundedLruCache<string, string> Descriptions = new(8192);

    public static string CleanSummary(string? value) => Summaries.GetOrAdd(value ?? "", CleanSummaryCore);

    private static string CleanSummaryCore(string value)
    {
        value = Nfkc(value);
        if (value.Trim().Length == 0) return "";
        value = HtmlBreak().Replace(value, "\n").Replace("\r", "", StringComparison.Ordinal);
        var kept = new List<string>();
        var skippingAliases = false;
        foreach (var rawLine in value.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) { skippingAliases = false; continue; }
            if (Pattern("ALT_HEADER_RE").IsMatch(line)) { skippingAliases = true; continue; }
            if (skippingAliases)
            {
                if (Pattern("STOP_HEADER_RE").IsMatch(line)) skippingAliases = false;
                else continue;
            }
            var hard = Pattern("HARD_SUMMARY_SECTION_RE").Match(line);
            var alias = Pattern("INLINE_ALIAS_SECTION_RE").Match(line);
            var markers = new[] { hard, alias }.Where(match => match.Success).OrderBy(match => match.Index).ToArray();
            if (markers.Length > 0)
            {
                var prefix = CreditFooter().Replace(StripMarkdown(line[..markers[0].Index].Trim()).Trim(), "").Trim();
                if (prefix.Length > 0) kept.Add(prefix);
                break;
            }
            if (Pattern("LINE_METADATA_RE").IsMatch(line)) continue;
            var simplified = StripMarkdown(line).Trim();
            if (simplified.Length == 0 || Pattern("CREDIT_LINE_RE").IsMatch(simplified)) continue;
            simplified = CreditFooter().Replace(simplified, "").Trim();
            if (simplified.Length > 0) kept.Add(simplified);
        }
        var text = Pattern("SPACE_RE").Replace(string.Join(' ', kept), " ").Trim(" \t-•".ToCharArray());
        if (text.Length == 0 || Pattern("PLACEHOLDER_TITLE_RE").IsMatch(NormalizeTitle(text))) return "";
        var words = UnicodeProperties.Words(text);
        if (words.Length >= 20)
        {
            var normalized = words.Select(NormalizeTitle).Where(word => word.Length > 0).ToArray();
            if (normalized.Length > 0 && normalized.Distinct(StringComparer.Ordinal).Count() / (double)normalized.Length < .12)
                return "";
        }
        return text;
    }

    public static bool IsMeaningfulSummary(string? value)
    {
        var text = CleanSummary(value);
        if (UnicodeProperties.Length(text) < 40) return false;
        var words = UnicodeProperties.Words(text);
        if (words.Length < 8) return false;
        var runes = text.EnumerateRunes().ToArray();
        var letters = runes.Count(UnicodeProperties.IsLetter);
        var visible = runes.Count(rune => !Rune.IsWhiteSpace(rune) && rune.Value is not (>= 0x1c and <= 0x1f));
        if (visible > 0 && letters / (double)visible < .45) return false;
        var normalized = words.Select(NormalizeTitle).Where(word => word.Length > 0).ToArray();
        return normalized.Length < 12 || normalized.Distinct(StringComparer.Ordinal).Count() / (double)normalized.Length >= .22;
    }

    public static string NormalizeDescription(string? value) => Descriptions.GetOrAdd(value ?? "", raw =>
    {
        var text = CleanSummary(raw);
        return text.Length == 0 ? "" : Collapse(Pattern("PUNCT_RE").Replace(Ascii(text), " "));
    });

    public static double DescriptionSimilarity(string? left, string? right)
    {
        if (!IsMeaningfulSummary(left) || !IsMeaningfulSummary(right)) return 0;
        var a = NormalizeDescription(left); var b = NormalizeDescription(right);
        if (a.Length < 40 || b.Length < 40) return 0;
        if (a == b) return 1;
        var direct = IndelSimilarity.Ratio(a, b);
        var lengthRatio = Math.Min(a.Length, b.Length) / (double)Math.Max(a.Length, b.Length);
        return Math.Max(direct, lengthRatio >= .75 ? IndelSimilarity.TokenSet(a, b) : 0);
    }

    public static double BestDescriptionScore(string? candidate, IEnumerable<string> existing) =>
        existing.Where(value => !string.IsNullOrEmpty(value)).Select(value => DescriptionSimilarity(candidate, value)).DefaultIfEmpty(0).Max();

    private static string StripMarkdown(string value)
    {
        value = Pattern("MARKDOWN_IMAGE_RE").Replace(value, " ");
        value = Pattern("MARKDOWN_LINK_RE").Replace(value, "$1");
        value = Pattern("URL_RE").Replace(value, " ");
        value = Pattern("HTML_TAG_RE").Replace(value, " ");
        return Pattern("MARKDOWN_DECORATION_RE").Replace(value, "");
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlBreak();
    [GeneratedRegex(@"\s*\(?\s*(?:summary\s+)?from\s+(?:line\s+webtoon|webtoon|mangadex|comick|mangaupdates)\s*\)?\s*\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreditFooter();
}
