using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Matching;

namespace AutoSourcarr.Core.Reader;

public static class ChapterResolver
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex Season = new(@"(?:^|[\s\[(])(?:s(?:eason)?\s*[-_.:]?\s*)(\d{1,3})(?=$|[\s\])_.: -])|(?:season\s+(\d{1,3}))", Options);
    private const string Unofficial = @"unofficial|fan[ -]?(?:translation|tl)|scanlation|fanmade|fan[ -]?made|machine[ -]?translation|mtl";
    private const string Official = @"official(?:\s+(?:translation|release|chapter|english))?|licensed(?:\s+(?:translation|release))?|publisher(?:\s+release)?";
    private const string UnofficialMarker = @"unofficial|fan[ -]?(?:translation|tl)|scanlation|machine[ -]?translation|mtl";
    private const string OfficialMarker = @"official(?:\s+(?:translation|release|chapter|english))?|licensed(?:\s+(?:translation|release))?";

    public static int SeasonNumber(string name)
    {
        var match = Season.Match(name.Trim());
        return match.Success ? Math.Max(1, int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value, CultureInfo.InvariantCulture)) : 1;
    }

    public static string DisplayName(double number, string name)
    {
        var raw = KnownPatterns.Whitespace().Replace(name.Trim(), " ");
        if (number < 0) return raw.Length > 0 ? raw : "Chapter";
        var text = number.ToString("0.######", CultureInfo.InvariantCulture);
        var cleaned = Regex.Replace(raw, @"^\s*[\[(]?\s*(?:s(?:eason)?\s*[-_.:]?\s*\d{1,3}|season\s+\d{1,3})\s*[\])]?[\s:.-]*", "", Options);
        var escaped = Regex.Escape(text);
        cleaned = Regex.Replace(cleaned, @"^\s*(?:chapter|ch\.?|episode|ep\.?)\s*#?\s*" + escaped + @"(?:\.0+)?\s*(?:[-:–—.]\s*)?", "", Options).Trim();
        cleaned = Regex.Replace(cleaned, @"^\s*" + escaped + @"(?:\.0+)?\s*[-:–—]\s*", "", Options).Trim();
        return cleaned.Length == 0 || TextMatcher.NormalizeTitle(cleaned) == TextMatcher.NormalizeTitle("Chapter " + text) || TextMatcher.NormalizeTitle(cleaned) == TextMatcher.NormalizeTitle("Episode " + text)
            ? "Chapter " + text : "Chapter " + text + ": " + cleaned;
    }

    public static bool ProbablyOfficialSource(string name, string baseUrl)
    {
        var label = Regex.Replace(AutoSourcarr.Core.Unicode.UnicodeCaseFold.Fold(name), "[^a-z0-9]+", " ").Trim();
        string[] labels = ["manga plus", "mangaplus", "webtoon", "comikey", "tapas", "tappytoon", "pocket comics", "pocketcomics", "manta", "lezhin", "toomics", "azuki", "manga up", "mangaup", "k manga", "kmanga", "viz manga", "manga plaza", "mangaplaza", "renta", "coolmic", "inkr"];
        if (labels.Contains(label, StringComparer.Ordinal)) return true;
        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var url) ? url.Host.ToLowerInvariant() : "";
        string[] domains = ["mangaplus.shueisha.co.jp", "webtoons.com", "comikey.com", "tapas.io", "tappytoon.com", "pocketcomics.com", "manta.net", "lezhinus.com", "toomics.com", "azuki.co", "global.manga-up.com", "kmanga.kodansha.com", "viz.com", "mangaplaza.com", "renta.papy.co.jp", "coolmic.me", "inkr.com"];
        return domains.Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
    }

    public static bool OfficialRelease(JsonObject candidate, IReadOnlySet<string>? officialSources = null)
    {
        var scanlator = ReaderValue.Text(candidate, "scanlator").Trim();
        if (Regex.IsMatch(scanlator, @"\b(?:" + Unofficial + @")\b", Options)) return false;
        if (Regex.IsMatch(scanlator, @"\b(?:" + Official + @")\b", Options)) return true;
        foreach (var field in new[] { "name", "source_entry_title" })
        {
            var value = ReaderValue.Text(candidate, field).Trim();
            if (Regex.IsMatch(value, Marker(UnofficialMarker), Options)) return false;
            if (Regex.IsMatch(value, Marker(OfficialMarker), Options)) return true;
        }
        return officialSources?.Contains(ReaderValue.Text(candidate, "source_id")) == true;
    }

    public static double Score(JsonObject candidate, IReadOnlyDictionary<string, long> priorities, long maximumPages = 0, IReadOnlySet<string>? officialSources = null)
    {
        var priority = priorities.GetValueOrDefault(ReaderValue.Text(candidate, "source_id"), 1000);
        var pages = ReaderValue.Integer(candidate, "page_count"); var score = 100000.0 - priority * 10;
        if (ReaderValue.Flag(candidate, "official") || OfficialRelease(candidate, officialSources)) score += 300000;
        var season = SeasonNumber(ReaderValue.Text(candidate, "name"));
        if (season > 1) score -= (season - 1) * 1000000.0;
        if (maximumPages > 0 && pages > 0) { score += Math.Min(1, (double)pages / maximumPages) * 25000; if (pages == maximumPages) score += 5000; }
        else if (pages > 0) score += Math.Min(2500, pages * 20.0);
        if (candidate["quality_score"] is not null) score += ReaderValue.Number(candidate, "quality_score") * 50;
        return score;
    }

    private static string Marker(string terms) => @"(?:[\[(（【]\s*(?:" + terms + @")\s*[\])）】]|(?:\s[-:–—]\s|^)(?:" + terms + @")\s*$)";
}
