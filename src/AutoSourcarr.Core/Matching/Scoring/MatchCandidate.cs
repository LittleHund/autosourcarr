using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Matching.Scoring;

/// <summary>
/// One side of a comparison: the whole titles and alternative titles (never split), creators, first-publication year,
/// cover fingerprints and edition hints that come from more than the title (page colour analysis, source media type,
/// animated flag). Empty / null fields are "unknown", never "different".
/// </summary>
public sealed record MatchCandidate
{
    public IReadOnlyList<string> Titles { get; init; } = [];
    public IReadOnlyList<string> Authors { get; init; } = [];
    public int? Year { get; init; }
    /// <summary>Exact decoded-pixel fingerprint (thumbnail_fingerprints.fingerprint): equal means the same cover.</summary>
    public string CoverFingerprint { get; init; } = "";
    /// <summary>Optional 64-bit perceptual hash (16 hex digits): close means a similar cover, far means a different cover.</summary>
    public string CoverPerceptualHash { get; init; } = "";
    /// <summary>"colored", "monochrome" or "" (from page analysis / entries.color_mode).</summary>
    public string ColorHint { get; init; } = "";
    /// <summary>"manga", "webtoon", "novel" or "" (from source metadata / entries.media_mode).</summary>
    public string MediaHint { get; init; } = "";
    /// <summary>True for animated / motion-comic records, false for still pages, null when unknown.</summary>
    public bool? Animated { get; init; }
    public string Description { get; init; } = "";
    public string SourceId { get; init; } = "";

    /// <summary>
    /// Reads the pair-file shape: {"titles":[...],"authors":[...],"year":2001,"cover":"..","phash":"..","color":"..",
    /// "media":"..","animated":true,"description":"..","source":".."}. A single "title"/"author" text is accepted too.
    /// </summary>
    public static MatchCandidate FromJson(JsonObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new()
        {
            Titles = Texts(value["titles"]).Concat(Texts(value["title"])).ToArray(),
            Authors = Texts(value["authors"]).Concat(Texts(value["author"])).Concat(Texts(value["artist"])).ToArray(),
            Year = YearOf(value["year"]),
            CoverFingerprint = Text(value["cover"]),
            CoverPerceptualHash = Text(value["phash"]),
            ColorHint = Text(value["color"]),
            MediaHint = Text(value["media"]),
            Animated = value["animated"] is JsonValue animated && animated.TryGetValue<bool>(out var flag) ? flag : null,
            Description = Text(value["description"]),
            SourceId = Text(value["source"]),
        };
    }

    internal static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";

    internal static IEnumerable<string> Texts(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
                if (Text(item) is { Length: > 0 } text) yield return text;
        }
        else if (Text(node) is { Length: > 0 } single) yield return single;
    }

    internal static int? YearOf(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.GetValueKind() == JsonValueKind.Number && int.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return number is > 0 and < 10000 ? number : null;
        if (value.TryGetValue<string>(out var text) && text.Length >= 4 && int.TryParse(text.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed is > 0 and < 10000 ? parsed : null;
        return null;
    }
}

/// <summary>One piece of evidence or one blocker: a stable kind ("title", "author", "year", "cover", "edition-colour", ...) and a short detail.</summary>
public sealed record MatchEvidence(string Kind, string Detail, double Value = 0);

/// <summary>
/// The scorer's answer (the phase 8 "MatchDecision"): <see cref="Score"/> is the title score raised by agreeing identity
/// evidence, <see cref="FloorPassed"/> says the title alone reached the floor (0.94, or the higher floor generic titles
/// need), <see cref="Blockers"/> are reasons an automatic match is refused whatever the score. Only
/// <see cref="AutomaticMatch"/> may attach or merge without a person.
/// </summary>
public sealed record ScoredMatchDecision(double Score, bool FloorPassed, double TitleScore, double Floor, string MatchedLeft, string MatchedRight,
    IReadOnlyList<MatchEvidence> Evidence, IReadOnlyList<MatchEvidence> Blockers)
{
    public bool AutomaticMatch => FloorPassed && Blockers.Count == 0;

    public string Reason => Blockers.Count > 0 ? string.Join("; ", Blockers.Select(blocker => blocker.Kind + ": " + blocker.Detail))
        : FloorPassed ? "title " + Percent(TitleScore) + (Evidence.Count > 1 ? " + " + string.Join(", ", Evidence.Skip(1).Select(item => item.Kind)) : "")
        : "title " + Percent(TitleScore) + " is below " + Percent(Floor);

    internal static string Percent(double value) => (value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    public JsonObject ToJson() => new()
    {
        ["score"] = Math.Round(Score, 6),
        ["floor_passed"] = FloorPassed,
        ["automatic_match"] = AutomaticMatch,
        ["title_score"] = Math.Round(TitleScore, 6),
        ["floor"] = Floor,
        ["matched_titles"] = new JsonArray(MatchedLeft, MatchedRight),
        ["reason"] = Reason,
        ["evidence"] = new JsonArray(Evidence.Select(item => (JsonNode)Item(item)).ToArray()),
        ["blockers"] = new JsonArray(Blockers.Select(item => (JsonNode)Item(item)).ToArray()),
    };

    private static JsonObject Item(MatchEvidence item) => new() { ["kind"] = item.Kind, ["detail"] = item.Detail, ["value"] = Math.Round(item.Value, 6) };
}
