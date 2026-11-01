using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Duplicates;

namespace AutoSourcarr.Core.Matching.Scoring;

/// <summary>
/// Puts <see cref="MatchScorer"/>'s identity and edition checks (MAT-2 / MAT-3) in front of an automatic merge gate:
/// a pair the inner gate allows is still refused when creators, years or covers confidently differ or the editions
/// differ. It never allows a pair the inner gate refused. Snapshot shape: NativeCatalogueEvidence (titles, author /
/// authors, year, thumbnail_fingerprints, color_mode, media_mode, descriptions).
/// </summary>
public sealed class ScoredAutomaticMatchGate(IAutomaticMatchGate inner, MatchScoringOptions? options = null) : IAutomaticMatchGate
{
    private static readonly HashSet<string> IdentityBlockers = new(StringComparer.Ordinal) { "edition-colour", "edition-media", "edition-animated", "author", "year", "cover" };

    /// <summary>
    /// The matching-profile options for one snapshot pair (the profile behind the sources' matching rules: identity-evidence
    /// requirement, year tolerance, cover distances, weights); null uses the constructor options.
    /// </summary>
    public Func<JsonObject, JsonObject, MatchScoringOptions?>? OptionsFor { get; init; }

    public AutomaticMatchDecision Decide(JsonObject left, JsonObject right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        var decision = inner.Decide(left, right);
        if (!decision.Eligible) return decision;
        var (a, b) = Candidates(left, right);
        var effective = OptionsFor?.Invoke(left, right) ?? options;
        var scored = MatchScorer.Score(a, b, effective);
        var evidence = (JsonObject)decision.Evidence.DeepClone();
        evidence["scorer"] = scored.ToJson();
        // A profile that always asks for identity evidence refuses a pair without agreeing creator, year, cover or summary.
        var blocker = scored.Blockers.FirstOrDefault(item => IdentityBlockers.Contains(item.Kind)
            || item.Kind == "identity-evidence" && effective?.AlwaysRequireIdentityEvidence == true);
        if (blocker is null) return new(true, decision.Reason, evidence);
        var reason = "scorer: " + blocker.Kind + " (" + blocker.Detail + ")";
        evidence["eligible"] = false; evidence["reason"] = reason;
        return AutomaticMatchDecision.No(reason, evidence);
    }

    /// <summary>Scorer candidates for two snapshots (a shared exact thumbnail fingerprint becomes cover agreement).</summary>
    public static (MatchCandidate Left, MatchCandidate Right) Candidates(JsonObject left, JsonObject right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        var leftCovers = MatchCandidate.Texts(left["thumbnail_fingerprints"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shared = MatchCandidate.Texts(right["thumbnail_fingerprints"]).FirstOrDefault(leftCovers.Contains) ?? "";
        return (Candidate(left, shared), Candidate(right, shared));
    }

    private static MatchCandidate Candidate(JsonObject snapshot, string sharedCover) => new()
    {
        Titles = MatchCandidate.Texts(snapshot["titles"]).ToArray(),
        Authors = MatchCandidate.Texts(snapshot["authors"]).Concat(MatchCandidate.Texts(snapshot["author"])).Distinct(StringComparer.Ordinal).ToArray(),
        Year = MatchCandidate.YearOf(snapshot["year"]),
        CoverFingerprint = sharedCover,
        ColorHint = Known(MatchCandidate.Text(snapshot["color_mode"]), "colored", "monochrome"),
        MediaHint = Known(MatchCandidate.Text(snapshot["media_mode"]), "manga", "webtoon", "novel"),
        Description = MatchCandidate.Texts(snapshot["descriptions"]).FirstOrDefault() ?? "",
    };

    private static string Known(string value, params string[] allowed) => allowed.Contains(value, StringComparer.Ordinal) ? value : "";
}
