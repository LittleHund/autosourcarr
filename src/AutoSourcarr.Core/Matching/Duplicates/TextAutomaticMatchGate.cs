using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Settings;
using static AutoSourcarr.Core.Matching.Duplicates.DuplicateJson;

namespace AutoSourcarr.Core.Matching.Duplicates;

/// <summary>
/// Stand-in automatic merge gate until the phase 8A scorer is wired: the released strict native proof
/// (_auto_merge_snapshot_evidence_base: trusted identity, identity + distinctive exact title, enough exact native titles,
/// summary or exact thumbnail with a distinctive exact title) plus TextMatcher at the 0.94 floor with creator
/// corroboration, behind hard identity checks: shared-unverified entries, colored/monochrome and manga/webtoon editions,
/// novel/anime adaptations, different part/sequel numbers, different creators and different years never merge.
/// Generated metadata titles are never evidence (snapshots hold native titles only).
/// </summary>
public sealed partial class TextAutomaticMatchGate(Func<AutoSourcarrSettings>? settings = null,
    Func<JsonObject, JsonArray, JsonObject?>? galleryEvidence = null, IDuplicateMatchingPolicy? policy = null) : IAutomaticMatchGate
{
    /// <summary>The automatic title floor (never lower).</summary>
    public const double TitleFloor = 0.94;
    private static readonly HashSet<string> FormatTokens = new(StringComparer.Ordinal) { "novel", "novels", "ln", "wn", "anime", "animation" };
    private static readonly HashSet<string> RomanNumbers = new(StringComparer.Ordinal) { "ii", "iii", "iv", "vi", "vii", "viii", "ix", "xi", "xii" };

    public AutomaticMatchDecision Decide(JsonObject left, JsonObject right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        var decision = Base(left, right);
        if (policy is null) return decision;
        var applied = policy.Apply((JsonObject)decision.Evidence.DeepClone(), left, right);
        var eligible = Bool(applied["eligible"]); var reason = Str(applied["reason"], decision.Reason);
        applied["eligible"] = eligible; applied["reason"] = reason;
        return new(eligible, reason, applied);
    }

    private AutomaticMatchDecision Base(JsonObject left, JsonObject right)
    {
        var current = settings?.Invoke();
        var minimumExact = Math.Max(1, current?.DuplicateAutoMergeMinExactTitles ?? 2);
        var summaryThreshold = Math.Clamp(current?.DuplicateAutoMergeSummaryThreshold ?? 0.7, 0, 1);
        if (Bool(left["shared_unverified"]) || Bool(right["shared_unverified"])) return AutomaticMatchDecision.No("Shared catalogue entry needs local source verification");
        var edition = EditionConflict(left, right);
        if (edition.Length != 0) return AutomaticMatchDecision.No(edition);
        var leftTitles = Strings(left["titles"]); var rightTitles = Strings(right["titles"]);
        var markers = MarkerConflict(leftTitles, rightTitles);
        if (markers.Length != 0) return AutomaticMatchDecision.No(markers);

        var gallery = GalleryPayloads(left).Concat(GalleryPayloads(right)).Any(payload => payload["_gallery_metadata"] is JsonObject);
        if (gallery)
        {
            if (galleryEvidence is null) return AutomaticMatchDecision.No("gallery source evidence is not available to automatic duplicate matching");
            var rightGallery = new JsonArray(GalleryPayloads(right).Select(payload => (JsonNode?)payload.DeepClone()).ToArray());
            JsonObject? best = null;
            foreach (var payload in GalleryPayloads(left).DefaultIfEmpty(new JsonObject()))
            {
                var result = galleryEvidence(payload, rightGallery);
                if (result is null || !Bool(result["eligible"])) return AutomaticMatchDecision.No(Str(result?["reason"], "gallery source evidence missing"), new JsonObject { ["applies"] = true, ["exact_title_count"] = 0, ["summary_similarity"] = 0.0 });
                if (best is null || Num(result["score"]) > Num(best["score"])) best = result;
            }
            var evidence = (JsonObject)best!.DeepClone();
            evidence["exact_title_count"] = 1; evidence["summary_similarity"] = 0.0; evidence["best_title_similarity"] = Num(best["title_score"]);
            return new(true, Str(best["reason"], "gallery source evidence"), evidence);
        }

        var leftNorm = Strings(left["normalized_titles"]).ToHashSet(StringComparer.Ordinal);
        var rightNorm = Strings(right["normalized_titles"]).ToHashSet(StringComparer.Ordinal);
        foreach (var title in leftTitles) { var n = TextMatcher.NormalizeTitle(title); if (n.Length != 0) leftNorm.Add(n); }
        foreach (var title in rightTitles) { var n = TextMatcher.NormalizeTitle(title); if (n.Length != 0) rightNorm.Add(n); }
        var exact = leftNorm.Intersect(rightNorm, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var distinctive = exact.Where(Distinctive).ToArray();
        var summary = 0.0;
        foreach (var a in Strings(left["descriptions"])) foreach (var b in Strings(right["descriptions"])) summary = Math.Max(summary, TextMatcher.DescriptionSimilarity(a, b));
        var lids = Identities(left, trustedOnly: false); var rids = Identities(right, trustedOnly: false);
        var shared = lids.Intersect(rids).Order().ToArray();
        var trusted = Identities(left, trustedOnly: true).Intersect(Identities(right, trustedOnly: true)).Order().ToArray();
        var thumbs = Strings(left["thumbnail_fingerprints"]).Intersect(Strings(right["thumbnail_fingerprints"]), StringComparer.Ordinal).Any();
        var (bestTitle, bestLeft, bestRight) = BestComparableTitle(leftTitles, rightTitles);
        var rawBest = 0.0;
        foreach (var a in leftTitles) foreach (var b in rightTitles) rawBest = Math.Max(rawBest, TextMatcher.TitleSimilarity(a, b));

        var evidence2 = new JsonObject
        {
            ["exact_title_count"] = exact.Length, ["exact_titles"] = JArray(exact.Take(8)), ["distinctive_exact_titles"] = JArray(distinctive.Take(8)),
            ["summary_similarity"] = summary, ["summary_threshold"] = summaryThreshold,
            ["shared_identities"] = new JsonArray(shared.Select(pair => (JsonNode?)new JsonArray(pair.Provider, pair.Id)).ToArray()),
            ["trusted_shared_identities"] = new JsonArray(trusted.Select(pair => (JsonNode?)new JsonArray(pair.Provider, pair.Id)).ToArray()),
            ["exact_thumbnail_match"] = thumbs, ["best_title_similarity"] = Math.Max(bestTitle, exact.Length != 0 ? 1.0 : 0.0),
        };

        // Identity checks: a trusted (locked / manual / source-exact) shared identity is the user's or the source's own
        // statement and outranks the creator/year heuristics; everything else is blocked by a creator or year conflict.
        if (trusted.Length == 0)
        {
            var creators = CreatorScore(left, right);
            if (creators is { } score && score < 0.5) return AutomaticMatchDecision.No("different creators (" + score.ToString("0.00", CultureInfo.InvariantCulture) + ")", evidence2);
            // Two adaptations of one novel share its author but not their artists: each side naming a creator the
            // other lacks is a different work (one side merely naming fewer creators is not).
            if (CreatorsDiverge(left, right)) return AutomaticMatchDecision.No("different adaptation creators", evidence2);
            var ly = Year(left); var ry = Year(right);
            if (ly is { } a && ry is { } b && Math.Abs(a - b) > 1) return AutomaticMatchDecision.No("different publication years (" + a.ToString(CultureInfo.InvariantCulture) + "/" + b.ToString(CultureInfo.InvariantCulture) + ")", evidence2);
            if (exact.Length == 0 && rawBest >= TitleFloor && bestTitle < TitleFloor) return AutomaticMatchDecision.No("titles differ only in a part, volume or sequel number", evidence2);
        }

        var identityOk = trusted.Length != 0 || shared.Length != 0 && distinctive.Length >= 1;
        // Exact titles alone may prove a merge only when both sides name creators, or when one exact title is specific
        // (four or more words / 24+ characters): two short exact titles with a creator missing are not proof.
        var creatorsKnown = CreatorScore(left, right) is not null;
        var titleOk = exact.Length >= minimumExact && distinctive.Length >= 1 && (creatorsKnown || exact.Any(Specific));
        var summaryOk = summary >= summaryThreshold && distinctive.Length >= 1;
        var thumbnailOk = thumbs && distinctive.Length >= 1;
        var creatorsAgree = CreatorScore(left, right) is { } agree && agree >= 0.92;
        var fuzzyOk = bestTitle >= TitleFloor && (Distinctive(TextMatcher.NormalizeTitle(bestLeft)) || Distinctive(TextMatcher.NormalizeTitle(bestRight))) && creatorsAgree;
        string reason;
        if (trusted.Length != 0) reason = "trusted shared " + trusted[0].Provider + " identity " + trusted[0].Id;
        else if (identityOk) reason = "shared " + shared[0].Provider + " identity " + shared[0].Id + " + exact native title";
        else if (titleOk) reason = exact.Length.ToString(CultureInfo.InvariantCulture) + " exact native titles";
        else if (summaryOk) reason = "exact native title + source summary " + summary.ToString("P0", CultureInfo.InvariantCulture);
        else if (thumbnailOk) reason = "exact native title + exact decoded thumbnail pixels";
        else if (fuzzyOk) reason = "native title " + bestTitle.ToString("0.000", CultureInfo.InvariantCulture) + " >= " + TitleFloor.ToString("0.00", CultureInfo.InvariantCulture) + " + same creators";
        else reason = "needs distinctive exact native-title proof; generic titles and generated metadata cannot prove a merge";
        var eligible = identityOk || titleOk || summaryOk || thumbnailOk || fuzzyOk;
        evidence2["eligible"] = eligible; evidence2["reason"] = reason;
        return new(eligible, reason, evidence2);
    }

    /// <summary>A title specific enough to prove a merge without creators: four or more words, or at least 24 characters.</summary>
    public static bool Specific(string normalized) => normalized.Length != 0 && (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4 || normalized.Length >= 24);

    /// <summary>Released _automatic_title_is_distinctive: three or more words, or at least 18 characters.</summary>
    public static bool Distinctive(string normalized) => normalized.Length != 0 && (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3 || normalized.Length >= 18);

    /// <summary>Released _snapshot_edition_conflict over the entries' recomputed edition modes.</summary>
    public static string EditionConflict(JsonObject left, JsonObject right)
    {
        var lc = Str(left["color_mode"], "unknown"); var rc = Str(right["color_mode"], "unknown");
        if (lc == "mixed" || rc == "mixed" || lc is "colored" or "monochrome" && rc is "colored" or "monochrome" && lc != rc) return "colored/monochrome editions are separate works";
        var lm = Str(left["media_mode"], "unknown"); var rm = Str(right["media_mode"], "unknown");
        if (lm == "mixed" || rm == "mixed" || lm is "manga" or "webtoon" && rm is "manga" or "webtoon" && lm != rm) return "manga/webtoon adaptations are separate works";
        return "";
    }

    /// <summary>Edition and format markers carried by the titles themselves ("(Colored)", "(Webtoon)", "Light Novel", "Anime").</summary>
    public static IReadOnlySet<string> Markers(IEnumerable<string> titles)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var title in titles)
        {
            if (TextMatcher.TitleColorVariant(title) is var color && color != "unknown") result.Add("color:" + color);
            if (TextMatcher.TitleMediaVariant(title) is var media && media != "unknown") result.Add("media:" + media);
            var tokens = TextMatcher.NormalizeTitle(title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Any(FormatTokens.Contains)) result.Add("format:" + (tokens.Any(token => token is "anime" or "animation") ? "anime" : "novel"));
        }
        return result;
    }

    private static string MarkerConflict(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return "";
        var a = Markers(left); var b = Markers(right);
        if (a.SetEquals(b)) return "";
        var all = a.Union(b).ToArray();
        if (all.Any(marker => marker.StartsWith("format:", StringComparison.Ordinal) && !(a.Contains(marker) && b.Contains(marker)))) return "novel/anime adaptations are separate works";
        if (all.Any(marker => marker.StartsWith("color:", StringComparison.Ordinal) && !(a.Contains(marker) && b.Contains(marker)))) return "colored/monochrome editions are separate works";
        return "manga/webtoon adaptations are separate works";
    }

    /// <summary>Numbers in a title (digit runs and roman numerals II..XII): different numbers mean a different part or sequel.</summary>
    public static string NumberKey(string title)
    {
        var normalized = TextMatcher.NormalizeTitle(title);
        var numbers = Digits().Matches(normalized).Select(match => long.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture) : match.Value)
            .Concat(normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(RomanNumbers.Contains).Select(Roman));
        return string.Join(",", numbers.Order(StringComparer.Ordinal));
    }

    private static string Roman(string value) => value switch
    {
        "ii" => "2", "iii" => "3", "iv" => "4", "vi" => "6", "vii" => "7", "viii" => "8", "ix" => "9", "xi" => "11", "xii" => "12", _ => value,
    };

    private static (double Score, string Left, string Right) BestComparableTitle(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var best = (Score: 0.0, Left: "", Right: "");
        foreach (var a in left)
            foreach (var b in right)
            {
                if (NumberKey(a) != NumberKey(b)) continue;
                var score = TextMatcher.AutomaticTitleSimilarity(a, b);
                if (score > best.Score) best = (score, a, b);
            }
        return best;
    }

    private static IEnumerable<JsonObject> GalleryPayloads(JsonObject snapshot) => snapshot["gallery_payloads"] is JsonArray array ? array.OfType<JsonObject>() : [];

    private static HashSet<(string Provider, string Id)> Identities(JsonObject snapshot, bool trustedOnly)
    {
        var result = new HashSet<(string, string)>();
        if (!trustedOnly && snapshot["identities"] is JsonArray pairs)
            foreach (var pair in pairs.OfType<JsonArray>())
                if (pair.Count == 2 && Str(pair[0]) is { Length: > 0 } provider && Str(pair[1]) is { Length: > 0 } id) result.Add((provider, id));
        if (snapshot["identity_details"] is JsonArray details)
            foreach (var row in details.OfType<JsonObject>())
            {
                var provider = Str(row["provider"]); var id = Str(row["external_id"]); var kind = Str(row["match_type"]).ToLowerInvariant();
                if (provider.Length == 0 || id.Length == 0) continue;
                if (!trustedOnly || Bool(row["locked"]) || kind == "source-exact" || kind.StartsWith("manual", StringComparison.Ordinal)) result.Add((provider, id));
            }
        return result;
    }

    /// <summary>All creator names of a snapshot (entry author plus <c>authors</c>), split on , ; &amp; / and "and".</summary>
    public static IReadOnlyList<string> Creators(JsonObject snapshot)
    {
        var raw = new List<string> { Str(snapshot["author"]) }; raw.AddRange(Strings(snapshot["authors"]));
        return raw.SelectMany(value => CreatorSplit().Split(value)).Select(value => value.Trim()).Where(value => TextMatcher.NormalizeTitle(value).Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Best creator-name similarity, or null when either side names no creator.</summary>
    public static double? CreatorScore(JsonObject left, JsonObject right)
    {
        var a = Creators(left); var b = Creators(right);
        if (a.Count == 0 || b.Count == 0) return null;
        var best = 0.0;
        foreach (var x in a) foreach (var y in b) best = Math.Max(best, TextMatcher.NameSimilarity(x, y));
        return best;
    }

    /// <summary>True when each side names a creator with no counterpart (name similarity below 0.75) on the other side.</summary>
    public static bool CreatorsDiverge(JsonObject left, JsonObject right)
    {
        var a = Creators(left); var b = Creators(right);
        if (a.Count == 0 || b.Count == 0) return false;
        bool Unmatched(IReadOnlyList<string> from, IReadOnlyList<string> to) => from.Any(x => to.All(y => TextMatcher.NameSimilarity(x, y) < 0.75));
        return Unmatched(a, b) && Unmatched(b, a);
    }

    private static long? Year(JsonObject snapshot)
    {
        var year = Long(snapshot["year"]);
        return year is >= 1800 and <= 2200 ? year : null;
    }

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();
    [GeneratedRegex(@"\s*(?:[,;&/×]|\band\b)\s*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CreatorSplit();
}
