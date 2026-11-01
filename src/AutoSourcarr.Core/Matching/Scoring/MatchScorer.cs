using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Matching.Scoring;

/// <summary>
/// Thresholds and weights one matching profile uses. Every value is clamped by <see cref="Normalized"/>: the title floor
/// never goes below 0.94, a year gap above one year always blocks, and editions always stay separate.
/// </summary>
public sealed record MatchScoringOptions
{
    public double Floor { get; init; } = MatchScorer.MinimumFloor;
    /// <summary>Floor for generic titles (one or two short words): at least max(Floor, 0.985), and they also need identity evidence.</summary>
    public double GenericFloor { get; init; } = MatchScorer.GenericTitleFloor;
    /// <summary>When true every automatic match needs agreeing author, year, cover or summary evidence, not only generic titles.</summary>
    public bool AlwaysRequireIdentityEvidence { get; init; }
    /// <summary>Largest allowed year gap (0 or 1).</summary>
    public int YearTolerance { get; init; } = 1;
    public double AuthorAgreement { get; init; } = .85;
    /// <summary>Below this similarity two Latin-script creator names are a confident mismatch and block.</summary>
    public double AuthorConflict { get; init; } = .5;
    public int CoverAgreementDistance { get; init; } = 8;
    public int CoverConflictDistance { get; init; } = 22;
    public double SummaryCorroboration { get; init; } = .82;
    /// <summary>Required author similarity (matching rule "author" check); missing creators then block.</summary>
    public double? RequiredAuthor { get; init; }
    /// <summary>Required summary similarity (matching rule "summary" check); missing summaries then block.</summary>
    public double? RequiredSummary { get; init; }
    /// <summary>Confidence added to the score for each agreeing kind of evidence (advanced profiles change these weights).</summary>
    public double AuthorWeight { get; init; } = .01;
    public double YearWeight { get; init; } = .005;
    public double CoverWeight { get; init; } = .01;
    public double SummaryWeight { get; init; } = .005;

    public static MatchScoringOptions Default { get; } = new();

    public MatchScoringOptions Normalized()
    {
        static double Clamp(double value, double low, double high, double fallback) => double.IsFinite(value) ? Math.Clamp(value, low, high) : fallback;
        var floor = Clamp(Floor, MatchScorer.MinimumFloor, 1, MatchScorer.MinimumFloor);
        return this with
        {
            Floor = floor,
            GenericFloor = Math.Max(Math.Max(floor, MatchScorer.GenericTitleFloor), Clamp(GenericFloor, 0, 1, MatchScorer.GenericTitleFloor)),
            YearTolerance = Math.Clamp(YearTolerance, 0, 1),
            AuthorAgreement = Clamp(AuthorAgreement, .7, 1, .85),
            AuthorConflict = Clamp(AuthorConflict, .35, .7, .5),
            CoverAgreementDistance = Math.Clamp(CoverAgreementDistance, 0, 12),
            CoverConflictDistance = Math.Clamp(CoverConflictDistance, 16, 32),
            SummaryCorroboration = Clamp(SummaryCorroboration, .7, 1, .82),
            RequiredAuthor = RequiredAuthor is { } author && double.IsFinite(author) ? Math.Clamp(author, .01, 1) : null,
            RequiredSummary = RequiredSummary is { } summary && double.IsFinite(summary) ? Math.Clamp(summary, .01, 1) : null,
            AuthorWeight = Clamp(AuthorWeight, 0, .05, .01),
            YearWeight = Clamp(YearWeight, 0, .05, .005),
            CoverWeight = Clamp(CoverWeight, 0, .05, .01),
            SummaryWeight = Clamp(SummaryWeight, 0, .05, .005),
        };
    }
}

/// <summary>
/// The one automatic-matching scorer (MAT-1..MAT-4). Titles are compared whole (each title against each alternative
/// title, never split) with the released indel similarity plus a symmetric missing-word penalty: a word of either title
/// that the other title does not cover lowers the score (numbers, sequel and edition words strongly). Identity evidence
/// (creators, year, cover, summary) can raise confidence, and a confident mismatch blocks even a 1.0 title. Editions
/// (colored/monochrome, manga/webtoon/novel, animated/still) are decided from hints and title labels and never match.
/// </summary>
public static partial class MatchScorer
{
    public const double MinimumFloor = .94;
    public const double GenericTitleFloor = .985;
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal) { "the", "a", "an", "of", "no", "wa", "ga", "o", "to", "ni", "de", "and", "x" };
    private static readonly HashSet<string> Markers = new(StringComparer.Ordinal)
    {
        "part", "season", "vol", "volume", "side", "story", "gaiden", "spin", "off", "sequel", "ex", "re", "colored", "color", "colour",
        "coloured", "webtoon", "novel", "anthology", "remake", "ii", "iii", "iv", "vi", "vii", "viii", "ix", "next", "generations",
        "after", "before", "zero", "origin", "final", "special", "extra", "returns", "chapter", "arc", "movie", "animated", "motion",
    };
    private const string EditionTrim = " -–—:[]()（）【】";

    public static ScoredMatchDecision Score(MatchCandidate left, MatchCandidate right, MatchScoringOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        var settings = (options ?? MatchScoringOptions.Default).Normalized();
        var blockers = new List<MatchEvidence>();
        var evidence = new List<MatchEvidence>();

        // Editions first: they are a property of the record, not of the score.
        var (leftColor, rightColor) = (ColorEdition(left), ColorEdition(right));
        if (leftColor != rightColor && (leftColor == "colored" || rightColor == "colored"))
            blockers.Add(new("edition-colour", leftColor + " / " + rightColor));
        var (leftMedia, rightMedia) = (MediaEdition(left), MediaEdition(right));
        if (leftMedia != rightMedia) blockers.Add(new("edition-media", leftMedia + " / " + rightMedia));
        var (leftAnimated, rightAnimated) = (IsAnimated(left), IsAnimated(right));
        if (leftAnimated != rightAnimated) blockers.Add(new("edition-animated", (leftAnimated ? "animated" : "still") + " / " + (rightAnimated ? "animated" : "still")));

        var best = 0.0; var matchedLeft = ""; var matchedRight = "";
        foreach (var a in left.Titles)
            foreach (var b in right.Titles)
            {
                var value = TitleScore(a, b);
                if (value > best) { best = value; matchedLeft = a; matchedRight = b; }
            }
        evidence.Add(new("title", matchedLeft.Length == 0 ? "no comparable titles" : matchedLeft + " ↔ " + matchedRight, best));

        var agreements = 0; var boost = 0.0;
        var (authorState, authorScore) = AuthorEvidence(left.Authors, right.Authors, settings);
        if (authorState == "agree") { agreements++; boost += settings.AuthorWeight; evidence.Add(new("author", "creators agree", authorScore)); }
        else if (authorState == "conflict") blockers.Add(new("author", "different creators", authorScore));
        // Two adaptations of one novel share its author but not their artists (MAT-2): each side naming a creator the
        // other lacks is a different work; one side merely naming fewer creators is not.
        if (authorState != "conflict" && CreatorsDiverge(left.Authors, right.Authors)) blockers.Add(new("author", "different creators: each side names someone the other does not", authorScore));
        if (settings.RequiredAuthor is { } requiredAuthor)
        {
            if (authorState == "missing") blockers.Add(new("author-required", "creator evidence missing"));
            else if (authorScore + 1e-12 < requiredAuthor) blockers.Add(new("author-required", Percent(authorScore) + " is below " + Percent(requiredAuthor), authorScore));
        }

        if (left.Year is int leftYear && leftYear > 0 && right.Year is int rightYear && rightYear > 0)
        {
            var gap = Math.Abs(leftYear - rightYear);
            if (gap > settings.YearTolerance) blockers.Add(new("year", leftYear.ToString(CultureInfo.InvariantCulture) + " / " + rightYear.ToString(CultureInfo.InvariantCulture), gap));
            else { agreements++; boost += settings.YearWeight; evidence.Add(new("year", "years agree", gap)); }
        }

        if (left.CoverFingerprint.Length > 0 && string.Equals(left.CoverFingerprint, right.CoverFingerprint, StringComparison.OrdinalIgnoreCase))
        { agreements++; boost += settings.CoverWeight; evidence.Add(new("cover", "identical cover", 1)); }
        else if (CoverDistance(left.CoverPerceptualHash, right.CoverPerceptualHash) is { } distance)
        {
            if (distance <= settings.CoverAgreementDistance) { agreements++; boost += settings.CoverWeight; evidence.Add(new("cover", "similar cover", distance)); }
            else if (distance >= settings.CoverConflictDistance) blockers.Add(new("cover", "different cover", distance));
        }

        var summary = left.Description.Length > 0 && right.Description.Length > 0 ? TextMatcher.BestDescriptionScore(left.Description, [right.Description]) : (double?)null;
        if (summary is { } summaryScore && summaryScore >= settings.SummaryCorroboration)
        { agreements++; boost += settings.SummaryWeight; evidence.Add(new("summary", "summaries agree", summaryScore)); }
        if (settings.RequiredSummary is { } requiredSummary)
        {
            if (summary is null) blockers.Add(new("summary-required", "summary evidence missing"));
            else if (summary.Value + 1e-12 < requiredSummary) blockers.Add(new("summary-required", Percent(summary.Value) + " is below " + Percent(requiredSummary), summary.Value));
        }

        var distinctive = matchedLeft.Length > 0 && IsDistinctive(matchedLeft) && IsDistinctive(matchedRight);
        var floor = distinctive ? settings.Floor : settings.GenericFloor;
        var floorPassed = best + 1e-12 >= floor;
        if (floorPassed && agreements == 0 && (!distinctive || settings.AlwaysRequireIdentityEvidence))
            blockers.Add(new("identity-evidence", distinctive ? "this profile needs agreeing author, year, cover or summary evidence"
                : "a generic title needs agreeing author, year, cover or summary evidence"));
        return new(Math.Min(1, best + (best > 0 ? boost : 0)), floorPassed, best, floor, matchedLeft, matchedRight, evidence, blockers);
    }

    /// <summary>Whole-title similarity on edition-free titles: indel ratio minus the symmetric missing-word penalty.</summary>
    public static double TitleScore(string? left, string? right)
    {
        if (!TextMatcher.IsMeaningfulTitle(left) || !TextMatcher.IsMeaningfulTitle(right)) return 0;
        var a = TextMatcher.NormalizeTitle(EditionBaseTitle(left!)); var b = TextMatcher.NormalizeTitle(EditionBaseTitle(right!));
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var ratio = IndelSimilarity.Ratio(a, b);
        // Spacing / punctuation only ("SPY×FAMILY", "Onepunch-Man", "DAN DA DAN").
        if (a.Replace(" ", "", StringComparison.Ordinal) == b.Replace(" ", "", StringComparison.Ordinal)) return Math.Max(ratio, .99);
        return Math.Max(0, ratio - MissingWordPenalty(a, b));
    }

    /// <summary>
    /// Symmetric token coverage of two normalized titles: every word of either side that the other side does not cover
    /// costs 0.03 (0.01 for particles/articles, 0.08 for numbers, roman numerals, sequel and edition words). Romanisation
    /// variants (ou/oo/uu, wo/o), compounds ("full metal" / "fullmetal") and close spellings count as covered.
    /// </summary>
    public static double MissingWordPenalty(string normalizedLeft, string normalizedRight)
    {
        var a = Tokens(normalizedLeft); var b = Tokens(normalizedRight);
        var setA = a.ToHashSet(StringComparer.Ordinal); var setB = b.ToHashSet(StringComparer.Ordinal);
        var compactA = string.Concat(a); var compactB = string.Concat(b);
        var penalty = 0.0;
        foreach (var token in a) if (!Covered(token, setB, compactB)) penalty += Weight(token);
        foreach (var token in b) if (!Covered(token, setA, compactA)) penalty += Weight(token);
        return penalty;
    }

    private static List<string> Tokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Select(Fold).ToList();

    /// <summary>Romanisation fold of one ASCII token (Hepburn long vowels and the "wo" particle).</summary>
    internal static string Fold(string token)
    {
        if (token == "wo") return "o";
        return token.Replace("ou", "o", StringComparison.Ordinal).Replace("oo", "o", StringComparison.Ordinal)
            .Replace("uu", "u", StringComparison.Ordinal).Replace("aa", "a", StringComparison.Ordinal);
    }

    private static bool IsNumber(string token) => token.Length > 0 && token.All(char.IsAsciiDigit);
    private static bool IsMarker(string token) => IsNumber(token) || Markers.Contains(token);
    private static double Weight(string token) => IsMarker(token) ? .08 : Stopwords.Contains(token) ? .01 : .03;

    private static bool Covered(string token, HashSet<string> other, string otherCompact)
    {
        if (other.Contains(token)) return true;
        if (IsMarker(token)) return false;
        if (token.Length >= 4 && otherCompact.Contains(token, StringComparison.Ordinal)) return true;
        if (token.Length >= 4)
            foreach (var candidate in other)
                if (candidate.Length >= 4 && !IsNumber(candidate) && IndelSimilarity.Ratio(token, candidate) >= .8) return true;
        return false;
    }

    /// <summary>The title without colour, media, novel or animation labels (editions are judged separately).</summary>
    public static string EditionBaseTitle(string title)
    {
        var raw = title;
        if (TextMatcher.TitleColorVariant(raw) != "unknown") raw = Nonempty(TextMatcher.ColorBaseTitle(raw), raw);
        if (TextMatcher.TitleMediaVariant(raw) != "unknown") raw = Nonempty(TextMatcher.MediaBaseTitle(raw), raw);
        var stripped = AnimationLabel().Replace(NovelLabel().Replace(raw, " "), " ");
        stripped = MultipleSpaces().Replace(stripped, " ").Trim(EditionTrim.ToCharArray()).Trim();
        return stripped.Length > 0 ? stripped : raw;
    }

    /// <summary>Distinctive titles may match on title alone: three or more words, or 18+ normalized characters.</summary>
    public static bool IsDistinctive(string title)
    {
        var normalized = TextMatcher.NormalizeTitle(EditionBaseTitle(title));
        return normalized.Length > 0 && (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3 || normalized.Length >= 18);
    }

    public static string ColorEdition(MatchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.ColorHint is "colored" or "monochrome") return candidate.ColorHint;
        var found = candidate.Titles.Select(TextMatcher.TitleColorVariant).Where(mode => mode != "unknown").Distinct(StringComparer.Ordinal).ToArray();
        return found.Length == 1 ? found[0] : "unknown";
    }

    public static string MediaEdition(MatchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.MediaHint is "manga" or "webtoon" or "novel") return candidate.MediaHint;
        if (candidate.Titles.Any(title => NovelLabel().IsMatch(title))) return "novel";
        var found = candidate.Titles.Select(TextMatcher.TitleMediaVariant).Where(mode => mode != "unknown").Distinct(StringComparer.Ordinal).ToArray();
        return found.Length == 1 ? found[0] : "unknown";
    }

    public static bool IsAnimated(MatchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.Animated ?? candidate.Titles.Any(title => AnimationLabel().IsMatch(title));
    }

    /// <summary>"agree", "conflict", "neutral" or "missing", with the best name similarity.</summary>
    public static (string State, double Score) AuthorEvidence(IReadOnlyList<string> left, IReadOnlyList<string> right, MatchScoringOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        var settings = (options ?? MatchScoringOptions.Default).Normalized();
        var a = SplitNames(left); var b = SplitNames(right);
        if (a.Count == 0 || b.Count == 0) return ("missing", 0);
        var best = 0.0; var comparable = false;
        foreach (var x in a)
            foreach (var y in b)
            {
                var (keyX, keyY) = (AuthorKey(x), AuthorKey(y));
                if (keyX.Length == 0 || keyY.Length == 0) continue;
                best = Math.Max(best, IndelSimilarity.Ratio(keyX, keyY));
                if (LatinName(x) && LatinName(y)) comparable = true;
            }
        if (best >= settings.AuthorAgreement) return ("agree", best);
        if (comparable && best < settings.AuthorConflict) return ("conflict", best);
        return ("neutral", best);
    }

    /// <summary>
    /// True when each side names a Latin-script creator who is nobody on the other side (<see cref="SamePerson"/>).
    /// Sides naming a non-Latin creator are never judged (no transliteration).
    /// </summary>
    public static bool CreatorsDiverge(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        var a = SplitNames(left); var b = SplitNames(right);
        if (a.Count == 0 || b.Count == 0) return false;
        static bool Unmatched(List<string> from, List<string> to) => to.All(LatinName) && from.Any(x => LatinName(x) && !to.Any(y => SamePerson(x, y)));
        return Unmatched(a, b) && Unmatched(b, a);
    }

    /// <summary>
    /// Two creator names that are one person: every name part of the shorter name is a part of the longer one (exact after
    /// the romanisation fold, or 80% similar for parts of four or more letters, any order), or the names written without
    /// spaces are 90% similar ("Nekokurage" / "Neko Kurage"). Shared generic parts alone never make one person
    /// ("Writer Lee" / "Writer Park").
    /// </summary>
    public static bool SamePerson(string left, string right)
    {
        var a = Parts(left); var b = Parts(right);
        if (a.Count == 0 || b.Count == 0) return false;
        if (IndelSimilarity.Ratio(string.Concat(a), string.Concat(b)) >= .9) return true;
        var (shorter, longer) = a.Count <= b.Count ? (a, b) : (b, a);
        var free = longer.ToList();
        foreach (var part in shorter)
        {
            var index = free.FindIndex(other => other == part || part.Length >= 4 && other.Length >= 4 && IndelSimilarity.Ratio(part, other) >= .8);
            if (index < 0) return false;
            free.RemoveAt(index);
        }
        return true;
    }

    private static List<string> Parts(string name) => TextMatcher.NormalizeTitle(name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token != "sensei").Select(Fold).ToList();

    private static List<string> SplitNames(IEnumerable<string> names) =>
        names.SelectMany(name => NameSeparator().Split(name)).Select(name => name.Trim()).Where(name => name.Length > 0).ToList();

    /// <summary>Order- and romanisation-tolerant name key: "Gotōge Koyoharu" and "Koyoharu Gotouge" give the same key.</summary>
    internal static string AuthorKey(string name) => string.Join(' ', TextMatcher.NormalizeTitle(name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token != "sensei").Select(Fold).Order(StringComparer.Ordinal));

    /// <summary>Latin-script names only can be confidently different; "芥見下々" vs "Gege Akutami" stays neutral.</summary>
    private static bool LatinName(string name)
    {
        var letters = name.Where(char.IsLetter).ToArray();
        return letters.Length > 0 && letters.Count(letter => letter < 'ɐ') / (double)letters.Length >= .85;
    }

    internal static int? CoverDistance(string left, string right)
    {
        if (left.Length is 0 or > 16 || right.Length is 0 or > 16) return null;
        if (!ulong.TryParse(left, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var a)
            || !ulong.TryParse(right, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b)) return null;
        return BitOperations.PopCount(a ^ b);
    }

    private static string Nonempty(string value, string fallback) => value.Length == 0 ? fallback : value;
    private static string Percent(double value) => ScoredMatchDecision.Percent(value);

    [GeneratedRegex(@"(?:[\[(（【]\s*(?:light\s+novel|web\s+novel|novel|ln|wn)\s*[\])）】]|\b(?:light|web)\s+novel\b|\bnovel\s+(?:version|edition|adaptation)\b|\s[-:–—]\s*novel\s*$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NovelLabel();
    [GeneratedRegex(@"(?:[\[(（【]\s*(?:animated|animation|motion\s+comic)\s*[\])）】]|\bmotion\s+comic\b|\banimated\s+(?:version|edition)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnimationLabel();
    [GeneratedRegex(@"\s*(?:[,;/&×、・]|\band\b)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex NameSeparator();
    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultipleSpaces();
}
