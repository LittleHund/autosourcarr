using System.Globalization;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Reader;

namespace AutoSourcarr.Core.Catalogue.Chapters;

/// <summary>What a chapter label says about its place: the chapter number, a part within it (0 = none), the volume
/// (-1 = none), the season (1 when unmarked) and which kind of word gave the number.</summary>
public sealed record ParsedChapterName(double Number, int Part, double Volume, int Season, ChapterNumberWord Word);

public enum ChapterNumberWord { Chapter, Part, Volume }

/// <summary>
/// CHAP-1: chapter numbers from any numbering word (Chapter, Ch., Episode, Ep., Act, Stage, No., #, Part, Vol., 第…話,
/// …화 and similar), and parts kept apart: "Chapter 10 Part 2" is chapter 10.2, after part 1 (10.1) and the whole
/// chapter 10. CHAP-2: seasons keep counting (<see cref="SeasonContinuation"/>).
/// </summary>
public static class ChapterNumbering
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);
    // A number that does not continue another number or follow a minus sign ("Episode -2" stays unnumbered).
    private const string Number = @"(?<![\d\-]|\d[.,])(\d{1,6}(?:\.\d{1,6})?)";
    private const string Words = @"chapter|chapitre|chap|capítulo|capitulo|cap|kapitel|episode|épisode|episodio|ep|ch|act|stage|lesson|round|bölüm|chương|глава|no\.|nº|n°|number|\#|c(?=\d)|e(?=\d)";
    private const string Separator = @"\.?(?:\s*[#:.]\s*|\s+-\s+|\s*)";
    private const string Start = @"(?:^|[^\p{L}\d])";
    private static readonly Regex ChapterWord = new(Start + "(?:" + Words + ")" + Separator + Number + @"(?:([a-h])(?![\p{L}\d]))?(?![\p{L}\d]|[.,]\d)", Options, Limit);
    private static readonly Regex CjkChapter = new(@"[第제]\s*" + Number + @"\s*[話话章回集篇節节화]|" + Number + @"\s*[話话화回]", Options, Limit);
    private static readonly Regex PartWord = new(Start + @"(?:part|pt|parte|partie|teil)\.?\s*" + Number + @"(?![\p{L}\d])|(前[編篇])|(後[編篇]|后篇)", Options, Limit);
    private static readonly Regex VolumeWord = new(Start + @"(?:volume|vol|tome|tomo|band|v(?=\d))\.?\s*" + Number + @"(?![\p{L}\d])|第\s*" + Number + @"\s*[巻卷권]|" + Number + @"\s*[巻卷권]", Options, Limit);
    // Same season marker as the released resolver (_SEASON_MARKER_RE); removed before reading the chapter number.
    private static readonly Regex SeasonMarker = new(@"(?:^|[\s\[(])(?:s(?:eason)?\s*[-_.:]?\s*)(\d{1,3})(?=$|[\s\])_.: -])|(?:season\s+(\d{1,3}))", Options, Limit);
    private static readonly Regex LeadingSeason = new(@"^\s*[\[(]?\s*(?:s(?:eason)?\s*[-_.:]?\s*\d{1,3}|season\s+\d{1,3})\s*[\])]?[\s:.-]*", Options, Limit);
    private static readonly Regex LeadingVolume = new(@"^\s*[\[(]?\s*(?:volume|vol|tome|v(?=\d))\.?\s*\d{1,6}(?:\.\d{1,6})?\s*[\])]?[\s:,.\-–—]*|^\s*第?\s*\d{1,6}\s*[巻卷권]\s*", Options, Limit);
    private static readonly Regex LeadingChapter = new(@"^\s*[\[(]?\s*(?:" + Words + ")" + Separator + @"\d{1,6}(?:\.\d{1,6})?[a-h]?(?![\p{L}\d])\s*[\])]?|^\s*[第제]?\s*\d{1,6}(?:\.\d{1,6})?\s*[話话章回集篇節节화]", Options, Limit);
    private static readonly Regex LeadingPart = new(@"^[\s,:\-–—]*[\[(]?\s*(?:part|pt|parte|partie|teil)\.?\s*\d{1,6}\s*[\])]?", Options, Limit);

    /// <summary>Reads a chapter label; null when it names no number at all.</summary>
    public static ParsedChapterName? Parse(string? name)
    {
        var text = ChapterNumbers.RegexText(name ?? "");
        var season = ChapterResolver.SeasonNumber(text);
        var stripped = SeasonMarker.Replace(text, " ");
        var volume = -1.0;
        var volumeMatch = VolumeWord.Match(stripped);
        if (volumeMatch.Success) volume = Value(First(volumeMatch, 1, 2, 3));
        var part = 0;
        var partMatch = PartWord.Match(stripped);
        if (partMatch.Success)
            part = partMatch.Groups[2].Success ? 1 : partMatch.Groups[3].Success ? 2 : (int)Math.Min(int.MaxValue, Value(partMatch.Groups[1].Value));
        var chapter = ChapterWord.Match(stripped);
        if (chapter.Success)
        {
            // A part named before the chapter is an arc ("Part 4 Chapter 10"), not a part of this chapter.
            var letter = chapter.Groups[2].Success ? char.ToLowerInvariant(chapter.Groups[2].Value[0]) - 'a' + 1 : 0;
            return new(Value(chapter.Groups[1].Value), letter > 0 ? letter : partMatch.Success && partMatch.Index > chapter.Index ? part : 0, volume, season, ChapterNumberWord.Chapter);
        }
        var cjk = CjkChapter.Match(stripped);
        if (cjk.Success) return new(Value(First(cjk, 1, 2)), partMatch.Success && partMatch.Index > cjk.Index ? part : 0, volume, season, ChapterNumberWord.Chapter);
        if (partMatch.Success && partMatch.Groups[1].Success) return new(part, 0, volume, season, ChapterNumberWord.Part);
        if (volume >= 0) return new(volume, part, volume, season, ChapterNumberWord.Volume);
        return null;
    }

    /// <summary>The number a chapter part is filed under: part 1..9 of chapter 10 → 10.1..10.9, later parts 10.91…10.99.</summary>
    public static double WithPart(double number, int part)
    {
        if (part <= 0 || number < 0 || number != Math.Floor(number)) return number;
        var fraction = part <= 9 ? part / 10.0 : 0.9 + Math.Min(part - 9, 9) / 100.0;
        return Math.Round(number + fraction, 6);
    }

    /// <summary>The chapter number to file under: the source's own number (refined by an explicit part of that same
    /// number), or the number the label names; -1 when neither has one.</summary>
    public static double Resolve(double number, string name)
    {
        var parsed = Parse(name);
        if (number >= 0)
            return parsed is { Word: ChapterNumberWord.Chapter, Part: > 0 } p && Math.Abs(p.Number - number) < 1e-9 ? WithPart(number, p.Part) : number;
        if (parsed is null || parsed.Number < 0) return -1;
        return parsed.Word == ChapterNumberWord.Chapter ? WithPart(parsed.Number, parsed.Part) : parsed.Number;
    }

    /// <summary>The label with its season, volume, chapter-number and part prefix removed (the chapter's own title).</summary>
    public static string Title(string? name)
    {
        var text = KnownPatterns.Whitespace().Replace(ChapterNumbers.RegexText(name ?? "").Trim(), " ");
        text = LeadingSeason.Replace(text, "");
        text = LeadingVolume.Replace(text, "");
        var chapter = LeadingChapter.Match(text);
        if (chapter.Success) text = text[chapter.Length..];
        text = LeadingPart.Replace(text, "");
        return text.Trim().TrimStart(':', '-', '–', '—', '.', '|', ',', ')', ']').Trim();
    }

    /// <summary>
    /// The reader's chapter name. A continued season reads "Chapter 41: S2 Ch.1: Title"; a part reads
    /// "Chapter 10 Part 2: Title"; everything else keeps the released naming (<see cref="ChapterResolver.DisplayName"/>).
    /// </summary>
    public static string DisplayName(double number, string? name)
    {
        var raw = name ?? "";
        if (number < 0) return ChapterResolver.DisplayName(number, raw);
        var parsed = Parse(raw);
        if (parsed is { Word: ChapterNumberWord.Chapter, Season: > 1 } season && season.Number >= 0)
        {
            var own = WithPart(season.Number, season.Part);
            if (number > own + 1e-9)
            {
                var title = Title(raw);
                return "Chapter " + ChapterNumbers.Format(number) + ": S" + season.Season.ToString(CultureInfo.InvariantCulture) + " Ch." + ChapterNumbers.Format(own) + (title.Length == 0 ? "" : ": " + title);
            }
        }
        if (parsed is { Word: ChapterNumberWord.Chapter, Part: > 0 } part && Math.Abs(WithPart(part.Number, part.Part) - number) < 1e-9 && part.Number == Math.Floor(part.Number))
        {
            var title = Title(raw);
            return "Chapter " + ChapterNumbers.Format(part.Number) + " Part " + part.Part.ToString(CultureInfo.InvariantCulture) + (title.Length == 0 ? "" : ": " + title);
        }
        return ChapterResolver.DisplayName(number, raw);
    }

    /// <summary>Orders chapters for reading: by number (part 1 before part 2), unnumbered chapters last.</summary>
    public static int Compare(double left, double right) => (left < 0, right < 0) switch
    {
        (true, true) => 0,
        (true, false) => 1,
        (false, true) => -1,
        _ => left.CompareTo(right),
    };

    private static string First(Match match, params int[] groups)
    {
        foreach (var group in groups) if (match.Groups[group].Success) return match.Groups[group].Value;
        return "";
    }

    private static double Value(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : -1;
}

/// <summary>
/// CHAP-2: a source that starts counting again at a new season ("S2 Ep. 1") keeps counting on from the previous season:
/// season 2 chapter 1 follows the last chapter of season 1. Planned from one source record's complete chapter list.
/// Seasons that already continue the numbering, or whose previous season is missing, keep their own numbers.
/// </summary>
public sealed class SeasonContinuation
{
    private readonly IReadOnlyDictionary<int, double> _offsets;
    private SeasonContinuation(IReadOnlyDictionary<int, double> offsets) => _offsets = offsets;
    public static SeasonContinuation None { get; } = new(new Dictionary<int, double>());
    public bool Continues => _offsets.Count > 0;

    public static SeasonContinuation Plan(IEnumerable<(double Number, string Name)> chapters)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        var ranges = new SortedDictionary<int, (double Minimum, double Maximum)>();
        foreach (var (number, name) in chapters)
        {
            if (number < 0 || !double.IsFinite(number)) continue;
            var season = ChapterResolver.SeasonNumber(ChapterNumbers.RegexText(name ?? ""));
            ranges[season] = ranges.TryGetValue(season, out var range) ? (Math.Min(range.Minimum, number), Math.Max(range.Maximum, number)) : (number, number);
        }
        var offsets = new Dictionary<int, double>();
        var previousOffset = 0.0;
        foreach (var (season, range) in ranges)
        {
            if (season <= 1) continue;
            if (!ranges.TryGetValue(season - 1, out var previous)) break;
            double offset;
            if (range.Minimum <= previous.Maximum) offset = previousOffset + Math.Floor(previous.Maximum);
            else offset = range.Minimum > previous.Maximum + previousOffset ? 0 : previousOffset;
            if (offset > 0) offsets[season] = offset;
            previousOffset = offset;
        }
        return offsets.Count == 0 ? None : new(offsets);
    }

    /// <summary>The continued number of one chapter of the planned list (unchanged when its season is not continued).</summary>
    public double Apply(double number, string name)
    {
        if (number < 0 || _offsets.Count == 0) return number;
        var season = ChapterResolver.SeasonNumber(ChapterNumbers.RegexText(name ?? ""));
        return _offsets.TryGetValue(season, out var offset) ? Math.Round(number + offset, 6) : number;
    }
}
