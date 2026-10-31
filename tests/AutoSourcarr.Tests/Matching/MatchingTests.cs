using System.Text.Json;
using AutoSourcarr.Core.Matching;

namespace AutoSourcarr.Tests;

public static class MatchingTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("matching: differential released function corpus", () =>
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(ConfigurationTests.Fixture("matching-contract.json")));
            var count = 0;
            foreach (var test in fixture.RootElement.EnumerateArray())
            {
                var name = test.GetProperty("function").GetString()!;
                var args = test.GetProperty("arguments");
                string S(string key) => args.GetProperty(key).GetString() ?? "";
                string[] A(string key) => args.GetProperty(key).EnumerateArray().Select(value => value.GetString()!).ToArray();
                object result = name switch
                {
                    "normalize_title" => TextMatcher.NormalizeTitle(S("value")),
                    "normalize_description" => TextMatcher.NormalizeDescription(S("value")),
                    "clean_summary" => TextMatcher.CleanSummary(S("value")),
                    "is_meaningful_title" => TextMatcher.IsMeaningfulTitle(S("value")),
                    "is_meaningful_summary" => TextMatcher.IsMeaningfulSummary(S("value")),
                    "is_alias_fragment" => TextMatcher.IsAliasFragment(S("value")),
                    "is_single_word_title" => TextMatcher.IsSingleWordTitle(S("value")),
                    "is_match_evidence_title" => TextMatcher.IsMatchEvidenceTitle(S("value"), S("title_type"), args.GetProperty("primary").GetBoolean()),
                    "title_color_variant" => TextMatcher.TitleColorVariant(S("value")),
                    "title_media_variant" => TextMatcher.TitleMediaVariant(S("value")),
                    "color_base_title" => TextMatcher.ColorBaseTitle(S("value")),
                    "media_base_title" => TextMatcher.MediaBaseTitle(S("value")),
                    "normalized_color_base" => TextMatcher.NormalizedColorBase(S("value")),
                    "normalized_media_base" => TextMatcher.NormalizedMediaBase(S("value")),
                    "automatic_title_similarity" => TextMatcher.AutomaticTitleSimilarity(S("a"), S("b")),
                    "title_similarity" => TextMatcher.TitleSimilarity(S("a"), S("b")),
                    "description_similarity" => TextMatcher.DescriptionSimilarity(S("a"), S("b")),
                    "name_similarity" => TextMatcher.NameSimilarity(S("a"), S("b")),
                    "looks_english" => TextMatcher.LooksEnglish(S("title")),
                    "looks_preferred_english" => TextMatcher.LooksPreferredEnglish(S("title")),
                    "looks_romanized_japanese" => TextMatcher.LooksRomanizedJapanese(S("title")),
                    "extract_alt_titles" => TextMatcher.ExtractAltTitles(S("description")),
                    "strip_known_source_suffix" => TextMatcher.StripKnownSourceSuffix(S("value"), A("source_names")),
                    "best_description_score" => TextMatcher.BestDescriptionScore(S("candidate"), A("existing")),
                    "best_title_score" => TitleScore(A("left"), A("right")),
                    "best_automatic_title_score" => AutomaticScore(A("left"), A("right")),
                    "score_match" => TextMatcher.ScoreMatch(A("candidate_titles"), A("entry_titles"), S("candidate_author"), S("entry_author"), S("candidate_description"), A("entry_descriptions")),
                    _ => throw new InvalidOperationException("Untranslated matcher function in corpus: " + name)
                };
                Equal(test.GetProperty("expected"), JsonSerializer.SerializeToElement(result), name);
                count++;
            }
            Check.True(count >= 294);
            Console.WriteLine("Matching parity cases " + count);
        });
        suite.Add("matching: short containment and editions cannot become exact automatic matches", () =>
        {
            foreach (var (left, right) in new[] { ("King", "Demon King"), ("God of War", "The God of War"), ("Solo Leveling", "Solo Leveling Side Story") })
                Check.True(TextMatcher.AutomaticTitleSimilarity(left, right) < .94);
            Check.Equal("unknown", TextMatcher.TitleColorVariant("Colored Pencil"));
            Check.Equal("Colored Pencil", TextMatcher.ColorBaseTitle("Colored Pencil"));
            Check.Equal("colored", TextMatcher.TitleColorVariant("Real Work (Full Color)"));
            Check.Equal("manga", TextMatcher.TitleMediaVariant("Real Work [Manga]"));
            Check.Equal("webtoon", TextMatcher.TitleMediaVariant("Real Work [Webtoon]"));
            Check.True(!TextMatcher.IsMatchEvidenceTitle("Exact Work Title", "translated-ja"));
            Check.True(!TextMatcher.IsMatchEvidenceTitle("King", "manual"));
            Check.True(TextMatcher.IsMatchEvidenceTitle("King", "source"));
        });
    }

    private static object[] TitleScore(string[] left, string[] right)
    {
        var (score, strong) = TextMatcher.BestTitleScore(left, right);
        return [score, strong];
    }

    private static object[] AutomaticScore(string[] left, string[] right)
    {
        var (score, a, b) = TextMatcher.BestAutomaticTitleScore(left, right);
        return [score, a, b];
    }

    private static void Equal(JsonElement expected, JsonElement actual, string path)
    {
        Check.Equal(expected.ValueKind, actual.ValueKind);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Number:
                if (Math.Abs(expected.GetDouble() - actual.GetDouble()) > 1e-12)
                    throw new InvalidOperationException("Matcher number differs at " + path + ": " + expected + " vs " + actual);
                break;
            case JsonValueKind.Object:
                Check.Equal(expected.EnumerateObject().Count(), actual.EnumerateObject().Count());
                foreach (var property in expected.EnumerateObject()) Equal(property.Value, actual.GetProperty(property.Name), path + "." + property.Name);
                break;
            case JsonValueKind.Array:
                Check.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                for (var i = 0; i < expected.GetArrayLength(); i++) Equal(expected[i], actual[i], path + "[" + i + "]");
                break;
            default:
                if (expected.GetRawText() != actual.GetRawText() && expected.ToString() != actual.ToString())
                    throw new InvalidOperationException("Matcher value differs at " + path + ": " + expected + " vs " + actual);
                break;
        }
    }
}
