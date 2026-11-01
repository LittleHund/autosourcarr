using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Matching.Mixed;

/// <summary>One source record as the mixed-entry review shows it.</summary>
public sealed record MixedRecord(string Key, string SourceId, long SourceMangaId, string SourceName, string Lang, string Title, string Description,
    string Author, string MediaMode, string ColorMode, bool Locked, string MatchType)
{
    public JsonObject Json() => new()
    {
        ["key"] = Key, ["source_id"] = SourceId, ["source_manga_id"] = SourceMangaId, ["source_name"] = SourceName, ["lang"] = Lang,
        ["title"] = Title, ["description"] = Description, ["author"] = Author, ["media_mode"] = MediaMode, ["color_mode"] = ColorMode,
        ["locked"] = Locked, ["match_type"] = MatchType, ["cover_url"] = "/api/v1/source-covers/" + SourceMangaId.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>Two records that look like different works, and why.</summary>
public sealed record MixedPairEvidence(string Left, string Right, IReadOnlyList<string> Reasons)
{
    public JsonObject Json() => new() { ["left"] = Left, ["right"] = Right, ["reasons"] = new JsonArray(Reasons.Select(reason => (JsonNode)reason).ToArray()) };
}

/// <summary>
/// Port of mixed_entries.py: a local, advisory review of records that may belong to different works. It never changes
/// matching gates or bindings; missing metadata is not a conflict, and a review applies only to the exact evidence
/// fingerprint it inspected. At most 512 pairs and 12 findings per entry are examined (the released bound).
/// </summary>
public static partial class MixedEntryAssessment
{
    public const int MaximumPairs = 512;
    public const int MaximumFindings = 12;
    private static readonly HashSet<string> Stop = new("this that with from while their there they them then when where what which into about after before have has been will would could should she her his him and the for are was were not but who how new one all can only some through every more than each life world story".Split(' '), StringComparer.Ordinal);

    /// <summary>The released "mixed-v1" fingerprint over the inspected evidence (sha256 of the sorted evidence tuples).</summary>
    public static string Fingerprint(IEnumerable<MixedRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        // Byte-identical to json.dumps(['mixed-v1', sorted(evidence)], ensure_ascii=False) so 0.4.x reviews stay valid.
        var comparer = UnicodeCodePointComparer.Instance;
        var ordered = records.OrderBy(record => record.SourceId, comparer).ThenBy(record => record.SourceMangaId).ThenBy(record => record.Title, comparer)
            .ThenBy(record => record.Description, comparer).ThenBy(record => record.Author, comparer).ThenBy(record => record.MediaMode, comparer)
            .ThenBy(record => record.ColorMode, comparer).ThenBy(record => record.Lang, comparer);
        var text = new StringBuilder("[\"mixed-v1\", [");
        var first = true;
        foreach (var record in ordered)
        {
            if (!first) text.Append(", ");
            first = false;
            text.Append('[').Append(PythonString(record.SourceId)).Append(", ").Append(record.SourceMangaId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var field in new[] { record.Title, record.Description, record.Author, record.MediaMode, record.ColorMode, record.Lang }) text.Append(", ").Append(PythonString(field));
            text.Append(']');
        }
        text.Append("]]");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static string PythonString(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var character in value)
            text.Append(character switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\b' => "\\b", '\f' => "\\f",
                < ' ' => "\\u" + ((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
                _ => character.ToString(),
            });
        return text.Append('"').ToString();
    }

    public static IReadOnlyList<string> PairReasons(MixedRecord a, MixedRecord b)
    {
        ArgumentNullException.ThrowIfNull(a); ArgumentNullException.ThrowIfNull(b);
        var reasons = new List<string>();
        if (a.MediaMode != "unknown" && b.MediaMode != "unknown" && a.MediaMode != b.MediaMode) reasons.Add("Different publication formats: manga / webtoon");
        if (a.ColorMode != "unknown" && b.ColorMode != "unknown" && a.ColorMode != b.ColorMode) reasons.Add("Different color editions");
        // Native alternate titles may explain different translations of the same work.
        var titlesA = TextMatcher.ExtractAltTitles(a.Description).Prepend(a.Title).Take(20).Where(value => value.Length != 0).ToArray();
        var titlesB = TextMatcher.ExtractAltTitles(b.Description).Prepend(b.Title).Take(20).Where(value => value.Length != 0).ToArray();
        var titleScore = titlesA.Length == 0 || titlesB.Length == 0 ? 1 : titlesA.Max(x => titlesB.Max(y => TextMatcher.TitleSimilarity(x, y)));
        var left = TextMatcher.CleanSummary(a.Description); var right = TextMatcher.CleanSummary(b.Description);
        // Short, absent or cross-language summaries are never contrary evidence.
        if (a.Lang == "en" && b.Lang == "en" && left.Length >= 100 && right.Length >= 100)
        {
            var wordsA = Words(left); var wordsB = Words(right);
            var smaller = Math.Min(wordsA.Count, wordsB.Count);
            var overlap = (double)wordsA.Intersect(wordsB).Count() / Math.Max(1, smaller);
            if (smaller < 10 || overlap >= .10) return reasons;
            var summaryScore = TextMatcher.DescriptionSimilarity(left, right);
            if (titleScore < .55 && summaryScore < .50) reasons.Add("Source titles and detailed summaries describe different works");
            else if (titleScore < .80 && summaryScore < .50 && a.Author.Length != 0 && b.Author.Length != 0 && TextMatcher.NameSimilarity(a.Author, b.Author) < .40)
                reasons.Add("Different authors, titles and detailed summaries");
        }
        return reasons;
    }

    /// <summary>Bounded pairwise assessment: (findings, partial).</summary>
    public static (IReadOnlyList<MixedPairEvidence> Evidence, bool Partial) Assess(IReadOnlyList<MixedRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var evidence = new List<MixedPairEvidence>(); var examined = 0;
        for (var i = 0; i < records.Count; i++)
            for (var j = i + 1; j < records.Count; j++)
            {
                if (examined >= MaximumPairs) return (evidence, true);
                examined++;
                var reasons = PairReasons(records[i], records[j]);
                if (reasons.Count != 0) evidence.Add(new(records[i].Key, records[j].Key, reasons));
                if (evidence.Count >= MaximumFindings) return (evidence, true);
            }
        return (evidence, false);
    }

    private static HashSet<string> Words(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in WordPattern().Matches(UnicodeCaseFold.Fold(text))) if (!Stop.Contains(match.Value)) result.Add(match.Value);
        return result;
    }

    [GeneratedRegex("[a-z]{3,}")] private static partial Regex WordPattern();
}
