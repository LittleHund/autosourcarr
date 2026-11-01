using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

namespace AutoSourcarr.Core.Library;

/// <summary>
/// ComicInfo.xml (Anansi ComicInfo schema v2.0 plus the v2.1 Translator element, in the schema's sequence order) inside every CBZ, and the
/// series details.json of Mihon's local source. Kavita and Komga read Series, Number, Volume, Title, Summary,
/// Writer, Penciller, Genre, Web, LanguageISO and Format from the archive; Mihon reads Title (chapter name), Number,
/// Translator (scanlator) and Year/Month/Day per chapter and copies the series fields of the first archive when the
/// series folder has no metadata of its own.
/// </summary>
public static class ComicInfoDocument
{
    private static readonly JsonSerializerOptions DetailsJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The ComicInfo v2.1 element order (xs:sequence); the CBZ checker validates against the same list.</summary>
    public static readonly IReadOnlyList<string> ElementOrder =
    [
        "Title", "Series", "Number", "Count", "Volume", "AlternateSeries", "AlternateNumber", "AlternateCount", "Summary", "Notes",
        "Year", "Month", "Day", "Writer", "Penciller", "Inker", "Colorist", "Letterer", "CoverArtist", "Editor", "Translator",
        "Publisher", "Imprint", "Genre", "Tags", "Web", "PageCount", "LanguageISO", "Format", "BlackAndWhite", "Manga",
        "Characters", "Teams", "Locations", "ScanInformation", "StoryArc", "StoryArcNumber", "SeriesGroup", "AgeRating",
        "Pages", "CommunityRating", "MainCharacterOrTeam", "Review", "GTIN",
    ];

    /// <summary>The chapter title readers show: the catalogue display name, or "Chapter N" when it has none.</summary>
    public static string ChapterTitle(LibraryChapterInfo chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        var title = Clean(chapter.Title).Trim();
        if (title.Length > 0) return title;
        return LibraryNames.Known(chapter.Number) ? "Chapter " + LibraryNames.PlainNumber(chapter.Number) : "Special";
    }

    public static byte[] Build(LibrarySeriesInfo series, LibraryChapterInfo chapter, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(chapter);
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, OmitXmlDeclaration = false };
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("ComicInfo");
            writer.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            writer.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");
            void Element(string name, string? value)
            {
                var text = Clean(value).Trim();
                if (text.Length > 0) writer.WriteElementString(name, text);
            }
            Element("Title", ChapterTitle(chapter));
            Element("Series", series.Title);
            if (LibraryNames.Known(chapter.Number)) Element("Number", LibraryNames.PlainNumber(chapter.Number));
            if (LibraryNames.Known(chapter.Volume)) Element("Volume", ((long)Math.Floor(chapter.Volume)).ToString(CultureInfo.InvariantCulture));
            Element("Summary", series.Summary);
            if (chapter.UploadDate > 0)
            {
                var date = DateTimeOffset.FromUnixTimeMilliseconds(chapter.UploadDate > 100_000_000_000 ? chapter.UploadDate : chapter.UploadDate * 1000).UtcDateTime;
                if (date.Year is >= 1900 and <= 2200)
                {
                    Element("Year", date.Year.ToString(CultureInfo.InvariantCulture));
                    Element("Month", date.Month.ToString(CultureInfo.InvariantCulture));
                    Element("Day", date.Day.ToString(CultureInfo.InvariantCulture));
                }
            }
            Element("Writer", series.Author);
            Element("Penciller", series.Artist.Length > 0 ? series.Artist : series.Author);
            Element("Translator", chapter.Scanlator);
            Element("Genre", string.Join(", ", series.Genres.Select(genre => Clean(genre).Trim()).Where(genre => genre.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)));
            Element("Web", series.Web);
            Element("PageCount", pageCount.ToString(CultureInfo.InvariantCulture));
            Element("LanguageISO", chapter.Language);
            if (!LibraryNames.Known(chapter.Number)) Element("Format", "Special");
            Element("Manga", "Yes");
            Element("ScanInformation", chapter.SourceName.Length > 0 ? "AutoSourcarr: " + chapter.SourceName : "AutoSourcarr");
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return buffer.ToArray();
    }

    /// <summary>Mihon local source details.json (title, author, artist, description, genre[], status as a string code).</summary>
    public static byte[] Details(LibrarySeriesInfo series)
    {
        ArgumentNullException.ThrowIfNull(series);
        var details = new JsonObject
        {
            ["title"] = Clean(series.Title).Trim(),
            ["author"] = Clean(series.Author).Trim(),
            ["artist"] = Clean(series.Artist.Length > 0 ? series.Artist : series.Author).Trim(),
            ["description"] = Clean(series.Summary).Trim(),
            ["genre"] = new JsonArray(series.Genres.Select(genre => Clean(genre).Trim()).Where(genre => genre.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(genre => (JsonNode?)JsonValue.Create(genre)).ToArray()),
            ["status"] = (series.Status is >= 0 and <= 6 ? series.Status : 0).ToString(CultureInfo.InvariantCulture),
            ["_status values"] = new JsonArray("0 = Unknown", "1 = Ongoing", "2 = Completed", "3 = Licensed", "4 = Publishing finished", "5 = Cancelled", "6 = On hiatus"),
        };
        return Encoding.UTF8.GetBytes(details.ToJsonString(DetailsJson));
    }

    /// <summary>Drops characters XML 1.0 cannot carry (control characters, lone surrogates).</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value == 0xFFFD && !value.Contains('�', StringComparison.Ordinal)) continue;
            if (rune.Value is 0x9 or 0xA or 0xD || rune.Value >= 0x20 && rune.Value is not (>= 0xFFFE and <= 0xFFFF)) builder.Append(rune.ToString());
        }
        return builder.ToString();
    }
}
