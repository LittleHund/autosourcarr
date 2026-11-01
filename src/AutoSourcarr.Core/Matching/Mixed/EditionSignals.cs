using System.Text.RegularExpressions;
using AutoSourcarr.Core.Catalogue.Tags;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Matching.Mixed;

/// <summary>One piece of edition evidence. Axis is "color" (colored/monochrome) or "media" (manga/webtoon);
/// Evidence names where it came from ("title-marker", "chapter-pages", "tag:full colour", "source-type:tapas.io", "description").</summary>
public sealed record EditionSignal(string Axis, string Mode, double Confidence, string Evidence);

/// <summary>The saved page analysis of one source record (source_color_profiles).</summary>
public sealed record EditionPageProfile(string ColorMode, double Confidence, string Evidence, int SampledPages);

/// <summary>Everything known locally about one source record that can decide its edition. No field requires a website request.</summary>
public sealed record EditionRecordFacts(string SourceId, long MangaId, string Title, string Genre, string Description,
    IReadOnlyList<string> Tags, string SourceName, string SourceBaseUrl, EditionPageProfile? Pages)
{
    public string Key => SourceId + ":" + MangaId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A decided mode ("colored"/"monochrome"/"manga"/"webtoon"/"unknown"/"mixed") with its confidence and the evidence used.</summary>
public sealed record EditionDecision(string Mode, double Confidence, IReadOnlyList<EditionSignal> Evidence)
{
    public static EditionDecision Unknown { get; } = new("unknown", 0, []);
    public bool Strong(double minimum) => Mode is "colored" or "monochrome" or "manga" or "webtoon" && Confidence >= minimum;
}

/// <summary>
/// MAT-3: colored/monochrome and manga/webtoon editions stay separate, decided from more than the title. Evidence per
/// source record: the strict title marker (0.995, as released), saved chapter-page analysis (its own confidence; tall
/// strips mean webtoon), tags/genres through the synonym map, a known webtoon-only source, and explicit description
/// phrases. Independent kinds of evidence for the same mode add up (1 − Π(1 − c)); opposite evidence subtracts half of
/// its weight, so a weak hint can never override a page analysis or an explicit title.
/// </summary>
public static partial class EditionSignals
{
    /// <summary>Hosts that publish only vertical-scroll webtoons.</summary>
    public static IReadOnlySet<string> WebtoonHosts { get; } = new HashSet<string>(StringComparer.Ordinal)
    { "webtoons.com", "tapas.io", "tappytoon.com", "lezhin.com", "toomics.com", "manta.net", "comic.naver.com", "webtoon.kakao.com", "pocketcomics.com" };

    private static readonly string[] ColoredTags = ["full colour", "color", "colored"];
    private static readonly string[] MonochromeTags = ["monochrome", "grayscale"];
    private static readonly string[] WebtoonTags = ["webtoon"];
    private static readonly string[] MangaTags = ["manga", "japanese manga"];

    public static IReadOnlyList<EditionSignal> Color(EditionRecordFacts facts, TagSynonymMap? synonyms = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var result = new List<EditionSignal>();
        var title = TextMatcher.TitleColorVariant(facts.Title);
        if (title != "unknown") result.Add(new("color", title, .995, "title-marker"));
        if (facts.Pages is { } pages && pages.ColorMode is "colored" or "monochrome" && pages.Evidence.StartsWith("chapter-pages", StringComparison.Ordinal))
            result.Add(new("color", pages.ColorMode, Math.Clamp(pages.Confidence, 0, .99), "chapter-pages"));
        var map = synonyms ?? TagSynonymMap.Empty;
        foreach (var tag in Canonical(facts, map))
        {
            if (ColoredTags.Any(name => tag == map.Canonical("tag", name))) result.Add(new("color", "colored", .75, "tag:" + Name(tag)));
            else if (MonochromeTags.Any(name => tag == map.Canonical("tag", name))) result.Add(new("color", "monochrome", .7, "tag:" + Name(tag)));
        }
        var description = Fold(facts.Description);
        if (ColoredDescription().IsMatch(description)) result.Add(new("color", "colored", .6, "description"));
        else if (MonochromeDescription().IsMatch(description)) result.Add(new("color", "monochrome", .6, "description"));
        return result;
    }

    public static IReadOnlyList<EditionSignal> Media(EditionRecordFacts facts, TagSynonymMap? synonyms = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var result = new List<EditionSignal>();
        var title = TextMatcher.TitleMediaVariant(facts.Title);
        if (title != "unknown") result.Add(new("media", title, .995, "title-marker"));
        var map = synonyms ?? TagSynonymMap.Empty;
        foreach (var tag in Canonical(facts, map))
        {
            // Released behaviour: a "Webtoon"/"Manga" genre names the format (0.85 keeps it above the default 0.78 floor).
            if (WebtoonTags.Any(name => tag == map.Canonical("tag", name))) result.Add(new("media", "webtoon", .85, "tag:" + Name(tag)));
            else if (MangaTags.Any(name => tag == map.Canonical("tag", name))) result.Add(new("media", "manga", .85, "tag:" + Name(tag)));
        }
        var host = Host(facts.SourceBaseUrl);
        if (host.Length != 0 && WebtoonHosts.Any(known => host == known || host.EndsWith("." + known, StringComparison.Ordinal)))
            result.Add(new("media", "webtoon", .8, "source-type:" + host));
        if (facts.Pages is { } pages && pages.Evidence.StartsWith("chapter-pages", StringComparison.Ordinal) && pages.SampledPages > 0)
        {
            if (pages.Evidence.Contains(";long-strip", StringComparison.Ordinal)) result.Add(new("media", "webtoon", .9, "chapter-pages:long-strip"));
            else if (pages.Evidence.Contains(";paged", StringComparison.Ordinal)) result.Add(new("media", "manga", .6, "chapter-pages:paged"));
        }
        var description = Fold(facts.Description);
        if (WebtoonDescription().IsMatch(description)) result.Add(new("media", "webtoon", .6, "description"));
        else if (MangaDescription().IsMatch(description)) result.Add(new("media", "manga", .6, "description"));
        return result;
    }

    /// <summary>Decides one record (or one candidate) from its signals.</summary>
    public static EditionDecision Decide(IReadOnlyList<EditionSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        if (signals.Count == 0) return EditionDecision.Unknown;
        var weights = signals.GroupBy(signal => signal.Mode, StringComparer.Ordinal).ToDictionary(group => group.Key, group =>
        {
            // One value per kind of evidence: two tags saying the same thing are not independent.
            var kinds = group.GroupBy(signal => Kind(signal.Evidence), StringComparer.Ordinal).Select(kind => kind.Max(signal => signal.Confidence));
            return Math.Min(.995, 1 - kinds.Aggregate(1.0, (rest, value) => rest * (1 - Math.Clamp(value, 0, 1))));
        }, StringComparer.Ordinal);
        var ordered = weights.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var top = ordered[0]; var other = ordered.Length > 1 ? ordered[1].Value : 0;
        var confidence = Math.Round(Math.Max(0, top.Value - other / 2), 4);
        if (confidence <= 0 || ordered.Length > 1 && top.Value - other < .05) return new("unknown", 0, signals);
        return new(top.Key, confidence, signals);
    }

    /// <summary>Combines record decisions into the entry's mode: several strong modes make it "mixed" (the released rule).</summary>
    public static EditionDecision Combine(IEnumerable<EditionDecision> records, double minimum)
    {
        ArgumentNullException.ThrowIfNull(records);
        var all = records.ToArray(); var strong = all.Where(item => item.Strong(minimum)).ToArray();
        var evidence = all.SelectMany(item => item.Evidence).ToArray();
        var modes = strong.Select(item => item.Mode).Distinct(StringComparer.Ordinal).ToArray();
        if (modes.Length > 1) return new("mixed", strong.Min(item => item.Confidence), evidence);
        return modes.Length == 1 ? new EditionDecision(modes[0], strong.Max(item => item.Confidence), evidence) : new EditionDecision("unknown", 0, evidence);
    }

    private static string Kind(string evidence) { var index = evidence.IndexOf(':', StringComparison.Ordinal); return index < 0 ? evidence : evidence[..index]; }
    private static string Name(string key) { var index = key.IndexOf(':', StringComparison.Ordinal); return index < 0 ? key : key[(index + 1)..]; }
    private static string Fold(string text) => UnicodeCaseFold.Fold(text.Length > 4000 ? text[..4000] : text);

    private static IEnumerable<string> Canonical(EditionRecordFacts facts, TagSynonymMap map)
    {
        var values = facts.Tags.Concat(GenreParts().Split(facts.Genre)).Select(value => UnicodeText.Trim(value)).Where(value => value.Length != 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var index = value.IndexOf(':', StringComparison.Ordinal);
            var key = index > 0 && index < value.Length - 1 && NamespacePrefix().IsMatch(value[..index])
                ? map.CanonicalKey(System.Text.Json.Nodes.JsonValue.Create(value)) : map.Canonical("tag", value);
            if (key is not null && seen.Add(key)) yield return key;
        }
    }

    internal static string Host(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)) return "";
        var host = uri.IdnHost.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host.StartsWith("m.", StringComparison.Ordinal) ? host[2..] : host;
    }

    [GeneratedRegex(@"[,;/|]")] private static partial Regex GenreParts();
    [GeneratedRegex(@"^[a-zA-Z _-]{1,30}$")] private static partial Regex NamespacePrefix();
    [GeneratedRegex(@"\b(?:(?:full[\s-]?)?colou?red|full[\s-]?colou?r)\s+(?:version|edition|release)\b|\bdigital\s+colou?red\s+comics?\b|\bin\s+full\s+colou?r\b")]
    private static partial Regex ColoredDescription();
    [GeneratedRegex(@"\b(?:black[\s-]*(?:and|&)[\s-]*white|monochrome|gr[ae]yscale)\s+(?:version|edition|release)\b")]
    private static partial Regex MonochromeDescription();
    [GeneratedRegex(@"\b(?:official\s+)?webtoon\s+(?:version|edition|format|release)\b|\bvertical[\s-]scroll(?:ing)?\b|\blong[\s-]strip\s+(?:format|comic)\b")]
    private static partial Regex WebtoonDescription();
    [GeneratedRegex(@"\b(?:print|paged|tank(?:o|ō|ou)bon)\s+(?:manga\s+)?(?:version|edition|format|release)\b")]
    private static partial Regex MangaDescription();
}
