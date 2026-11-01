using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Scoring;

namespace AutoSourcarr.Tests.Matching;

/// <summary>Result of scoring a labelled pair file.</summary>
public sealed record PairScoringReport(int Pairs, int WrongMerges, int MissedMatches, IReadOnlyList<string> Lines);

/// <summary>
/// The scorer's decision over a JSON list of labelled pairs (the test runner's <c>--score-pairs &lt;file&gt;</c> mode).
/// File: {"pairs":[{"a":{"titles":[...],"authors":[...],"year":2001},"b":{...},"same":true|false}]}; a side may also
/// carry cover, phash, color, media, animated and description (see MatchCandidate.FromJson). Prints every wrong merge
/// (automatic match of a pair labelled different) and missed match (labelled same, not matched) and a summary.
/// </summary>
public static class PairScoringTool
{
    /// <summary>Returns 0 when nothing merged wrongly, 1 on any wrong merge, 2 when the file cannot be read.</summary>
    public static int Run(string path, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        PairScoringReport report;
        try { report = Score(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            output.WriteLine("Cannot score pairs from " + path + ": " + error.Message);
            return 2;
        }
        foreach (var line in report.Lines) output.WriteLine(line);
        return report.WrongMerges > 0 ? 1 : 0;
    }

    public static PairScoringReport Score(string path, MatchScoringOptions? options = null)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("The pair file must be a JSON object.");
        if (root["pairs"] is not JsonArray pairs) throw new InvalidDataException("The pair file needs a \"pairs\" array.");
        var lines = new List<string>(); var wrong = 0; var missed = 0; var index = 0;
        foreach (var node in pairs)
        {
            index++;
            if (node is not JsonObject pair || pair["a"] is not JsonObject a || pair["b"] is not JsonObject b
                || pair["same"] is not JsonValue label || !label.TryGetValue<bool>(out var same))
                throw new InvalidDataException("Pair " + index.ToString(CultureInfo.InvariantCulture) + " needs objects a and b and a true/false same.");
            var decision = MatchScorer.Score(MatchCandidate.FromJson(a), MatchCandidate.FromJson(b), options);
            if (decision.AutomaticMatch == same) continue;
            var kind = same ? "MISSED MATCH" : "WRONG MERGE";
            if (same) missed++; else wrong++;
            lines.Add(kind + " #" + index.ToString(CultureInfo.InvariantCulture) + ": " + Describe(a) + " | " + Describe(b)
                + " — score " + decision.Score.ToString("0.0000", CultureInfo.InvariantCulture) + ", " + decision.Reason);
        }
        lines.Add(pairs.Count.ToString(CultureInfo.InvariantCulture) + " pairs: " + wrong.ToString(CultureInfo.InvariantCulture) + " wrong merges, "
            + missed.ToString(CultureInfo.InvariantCulture) + " missed matches.");
        return new(pairs.Count, wrong, missed, lines);
    }

    private static string Describe(JsonObject side) => string.Join(" / ", MatchCandidate.FromJson(side).Titles);
}
