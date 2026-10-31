using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Catalogue.Gallery;

public static class GalleryMetadata
{
    private const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly HashSet<string> Names = ["ehentai", "exhentai", "imhentai", "luscious"];
    private static readonly HashSet<string> Hosts = ["e-hentai.org", "exhentai.org", "imhentai.xxx", "luscious.net"];
    private static readonly HashSet<string> Namespaces = ["artist", "author", "circle", "group", "parody", "character", "language", "category", "type", "genre", "tag", "male", "female", "mixed", "other", "cosplayer", "uploader"];
    private static readonly Dictionary<string, string> Languages = new(StringComparer.Ordinal)
    {
        ["english"]="English",["eng"]="English",["en"]="English",["japanese"]="Japanese",["jpn"]="Japanese",["ja"]="Japanese",["chinese"]="Chinese",["zh"]="Chinese",["korean"]="Korean",["ko"]="Korean",["spanish"]="Spanish",["es"]="Spanish",["french"]="French",["fr"]="French",["german"]="German",["de"]="German",["dutch"]="Dutch",["nl"]="Dutch",["portuguese"]="Portuguese",["pt"]="Portuguese",["russian"]="Russian",["ru"]="Russian",["thai"]="Thai",["th"]="Thai"
    };
    private static readonly HashSet<string> Categories = ["doujinshi", "manga", "artist cg", "game cg", "western", "imageset", "image set", "cosplay", "asian porn", "non-h", "comic", "comics"];
    public static IReadOnlySet<string> GenericTags { get; } = new HashSet<string>(["english", "japanese", "translated", "translation", "manga", "comic", "comics", "doujinshi", "color", "colored", "colour", "full color", "full colour", "uncensored", "censored", "digital", "scan", "scanned", "adult", "18+", "non-h"], StringComparer.Ordinal);

    public static bool IsGallerySource(JsonObject? source, JsonObject? manga = null)
    {
        if (GalleryJson.String(source?["metadata_profile"]).ToLowerInvariant() == "gallery") return true;
        foreach (var value in new[] { source?["name"], source?["display_name"], manga?["sourceName"] })
        {
            var name = Regex.Replace(GalleryJson.Text(value), @"\([^)]*\)|\[[^]]*\]", "");
            if (Names.Contains(Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", ""))) return true;
        }
        foreach (var value in new[] { source?["base_url"], source?["baseUrl"], manga?["realUrl"], manga?["url"] })
        {
            if (!Uri.TryCreate(GalleryJson.String(value), UriKind.Absolute, out var uri)) continue;
            var host = uri.Host.ToLowerInvariant(); if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
            if (Hosts.Contains(host)) return true;
        }
        return false;
    }

    public static JsonArray SourceTags(JsonObject manga)
    {
        var result = new JsonArray(); var seen = new HashSet<(string, string)>();
        void Add(string ns, JsonNode? value)
        {
            var name = GalleryJson.Text(value); ns = GalleryJson.Text(JsonValue.Create(ns)).ToLowerInvariant(); if (ns.Length == 0) ns = "tag";
            ns = ns switch { "artists" => "artist", "authors" => "author", "groups" or "group" => "circle", "languages" => "language", "characters" => "character", "parodies" => "parody", "genres" or "genre" or "tags" or "general" => "tag", "type" => "category", _ => ns };
            if (name.Length > 0 && seen.Add((ns, UnicodeCaseFold.Fold(name)))) result.Add(new JsonObject { ["namespace"] = ns, ["name"] = name });
        }
        void Parse(JsonNode? value, string ns = "tag")
        {
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.TrimStart().StartsWithAny('[', '{'))
            {
                try { var decoded = JsonNode.Parse(text); if (decoded is JsonArray or JsonObject) { Parse(decoded, ns); return; } } catch (JsonException) { }
            }
            if (value is JsonObject obj)
            {
                if (obj.ContainsKey("name") || obj["tag"] is JsonValue tag && tag.TryGetValue<string>(out _))
                    Add(GalleryJson.First(obj["namespace"], obj["type"], JsonValue.Create(ns)), GalleryJson.Truth(obj["name"]) ? obj["name"] : obj["tag"]);
                else foreach (var pair in obj) Parse(pair.Value, pair.Key);
            }
            else if (value is JsonArray list) foreach (var item in list) Parse(item, ns);
            else foreach (var name in Regex.Split(GalleryJson.String(value), "[,;\n]"))
            {
                var match = Regex.Match(name, @"^\s*([\w -]+)\s*:\s*(.+)$");
                if (match.Success && Namespaces.Contains(match.Groups[1].Value.Trim().ToLowerInvariant())) Add(match.Groups[1].Value, JsonValue.Create(match.Groups[2].Value));
                else Add(ns, JsonValue.Create(name));
            }
        }
        foreach (var field in new[] { "tags", "genre", "genres" }) Parse(manga[field]);
        foreach (var ns in new[] { "artist", "author", "circle", "parody", "character", "language", "category" }) if (GalleryJson.Truth(manga[ns])) Parse(manga[ns], ns);
        return result;
    }

    public static JsonObject Enrich(JsonObject manga, JsonObject? source = null)
    {
        var output = (JsonObject)manga.DeepClone(); if (!IsGallerySource(source, output)) return output;
        var previous = output["_gallery_metadata"] as JsonObject ?? new();
        var raw = GalleryJson.First(previous["raw_title"], output["_autosourcarr_raw_title"], output["title"]); var title = GalleryJson.Text(JsonValue.Create(raw));
        var tags = SourceTags(output); var provenance = new JsonObject { ["title"] = "source title", ["tags"] = "source fields" };
        var data = new JsonObject { ["animated"] = false, ["chapter_number"] = null, ["work_title"] = "", ["raw_title"] = raw, ["title"] = title, ["artist"] = "", ["circle"] = "", ["language"] = "", ["category"] = "", ["parody"] = "", ["kind"] = "work", ["tags"] = tags, ["provenance"] = provenance, ["annotations"] = new JsonArray() };
        if (source is { Count: > 0 }) data["source_name"] = GalleryJson.First(source["name"], source["display_name"]);
        data["source_id"] = GalleryJson.First(output["sourceId"], source?["source_id"]); data["source_manga_id"] = GalleryJson.Truth(output["id"]) ? output["id"]!.DeepClone() : JsonValue.Create(0);
        void Record(string ns, string value, string origin)
        {
            value = GalleryJson.Text(JsonValue.Create(value));
            if (value.Length > 0 && !tags.OfType<JsonObject>().Any(tag => GalleryJson.String(tag["namespace"]) == ns && UnicodeCaseFold.Fold(GalleryJson.String(tag["name"])) == UnicodeCaseFold.Fold(value))) tags.Add(new JsonObject { ["namespace"] = ns, ["name"] = value });
            if (data.ContainsKey(ns) && value.Length > 0 && !GalleryJson.Truth(data[ns])) { data[ns] = value; provenance[ns] = origin; }
        }
        foreach (var tag in tags.OfType<JsonObject>().ToArray())
        {
            var ns = GalleryJson.String(tag["namespace"]); ns = ns switch { "group" => "circle", "type" => "category", _ => ns };
            if (ns is "artist" or "circle" or "language" or "category" or "parody") Record(ns, GalleryJson.String(tag["name"]), "source field");
        }
        var animated = tags.OfType<JsonObject>().Any(tag => (GalleryJson.String(tag["namespace"]) is "tag" or "other" or "category" or "mixed") && (UnicodeCaseFold.Fold(GalleryJson.Text(tag["name"])) is "animated" or "animation" or "animated gallery"));
        var animation = new Regex(@"\s*[\[(]\s*(?:animated|animation)\s*[\])]", IgnoreCase);
        if (animation.IsMatch(title)) { animated = true; title = animation.Replace(title, "").Trim(); } data["animated"] = animated;
        string StripChapter(string value)
        {
            var serial = Regex.Match(value, @"(?:\s+|^)[\[(]?(?:chapter|chap\.?|ch\.?|part|pt\.?|episode|ep\.?)\s*[:#.-]?\s*(\d+(?:\.\d+)?)[\])]?(?:\s*)$", IgnoreCase);
            if (serial.Success && value[..serial.Index].Trim(" -:–—".ToCharArray()).Length > 0)
            { data["chapter_number"] = double.Parse(serial.Groups[1].Value, CultureInfo.InvariantCulture); provenance["chapter_number"] = "source title"; return value[..serial.Index].Trim(" -:–—".ToCharArray()); }
            return value;
        }
        var bracket = Regex.Match(title, @"^\[(artist|circle|collection)\]\s*(.+)$", IgnoreCase);
        var explicitCollection = Regex.Match(title, @"^(artist|circle|collection|image\s*set)(?:\s*[:–—-]+\s*|\s+)(.+)$", IgnoreCase);
        var collection = bracket.Success ? bracket : explicitCollection;
        if (collection.Success)
        {
            data["kind"] = "collection"; var label = collection.Groups[1].Value.ToLowerInvariant(); var name = GalleryJson.TrimCharacters(collection.Groups[2].Value, " ♥❤♡💕💖💗💓💞💘💝❣️:–—-");
            var update = Regex.Match(name, "(?:\\s+|^)[\"'“”‘’]?updates?(?:d)?[\"'“”‘’]?\\s*[:–—-]?\\s*\\d{1,4}[./-]\\d{1,2}[./-]\\d{1,4}\\s*$", IgnoreCase);
            if (update.Success) { data["annotations"]!.AsArray().Add(name[update.Index..].Trim()); name = name[..update.Index].Trim(); }
            while (true)
            {
                var suffix = Regex.Match(name, @"\s*\[([^\[\]]+)\]\s*$"); if (!suffix.Success) break; var key = UnicodeCaseFold.Fold(suffix.Groups[1].Value.Trim());
                if (Languages.TryGetValue(key, out var language)) Record("language", language, "title suffix");
                else if (Categories.Contains(key)) Record("category", suffix.Groups[1].Value, "title suffix"); else break;
                name = name[..suffix.Index].Trim();
            }
            name = StripChapter(name); data["collection_name"] = name; title = name; if (label is "artist" or "circle") Record(label, name, "title label");
        }
        if (Regex.IsMatch(title, @"\b(?:artist\s+(?:collection|pack)|complete\s+(?:works|collection)|image\s*set|collection|updates?(?:d)?\b.*\d|updates?\s*$)", IgnoreCase)) data["kind"] = "collection";
        var eventLabel = Regex.Match(title, @"^\((C\d{2,3}|COMIC\d+|COMITIA\d+)\)\s*", IgnoreCase);
        if (eventLabel.Success) { data["annotations"]!.AsArray().Add(eventLabel.Groups[1].Value); title = title[(eventLabel.Index + eventLabel.Length)..]; }
        var creator = Regex.Match(title, @"^\[([^\[\]]+)\]\s*(.+)$");
        if (creator.Success && !collection.Success && !Languages.ContainsKey(UnicodeCaseFold.Fold(creator.Groups[1].Value)) && !Categories.Contains(UnicodeCaseFold.Fold(creator.Groups[1].Value)) && !creator.Groups[1].Value.Contains(':'))
        {
            var value = creator.Groups[1].Value; var combined = Regex.Match(value, @"^(.+?)\s*\(([^()]+)\)$");
            if (combined.Success) { Record("circle", combined.Groups[1].Value, "title prefix"); Record("artist", combined.Groups[2].Value, "title prefix"); }
            else Record("artist", value, "title prefix"); title = creator.Groups[2].Value;
        }
        while (true)
        {
            var suffix = Regex.Match(title, @"\s*\[([^\[\]]+)\]\s*$"); if (!suffix.Success) break;
            var label = suffix.Groups[1].Value.Trim(); var key = UnicodeCaseFold.Fold(label);
            if (Languages.TryGetValue(key, out var language)) Record("language", language, "title suffix");
            else if (Categories.Contains(key)) Record("category", label, "title suffix");
            else if (key is "digital" or "translated" or "translation") Record("tag", label, "title suffix"); else break;
            title = title[..suffix.Index].Trim();
        }
        title = StripChapter(title); var parody = Regex.Match(title, @"\s+\(([^()]+)\)\s*$");
        if (parody.Success && !Regex.IsMatch(parody.Groups[1].Value, @"\b(?:colou?r|mono|manga|webtoon|chapter|volume|part|episode|ver(?:sion)?|edition|revised|uncensored|censored|digital|special|extra|omake|bonus|prologue|epilogue|rewrite|textless|decensored|mosaic|anthology)\b|\d", IgnoreCase))
        { Record("parody", parody.Groups[1].Value, "title suffix"); title = StripChapter(title[..parody.Index].Trim()); }
        data["work_title"] = title.Length > 0 ? title : GalleryJson.Text(JsonValue.Create(raw));
        if (animated) title += " (animated)";
        if (title.Length > 0 && title != GalleryJson.Text(JsonValue.Create(raw)))
        { if (!output.ContainsKey("_autosourcarr_raw_title")) output["_autosourcarr_raw_title"] = raw; output["title"] = title; provenance["title"] = "parsed source title"; }
        data["title"] = title.Length > 0 ? title : GalleryJson.Text(JsonValue.Create(raw));
        if (GalleryJson.Truth(data["parody"]) && TextMatcher.NormalizeTitle(GalleryJson.String(data["title"])) == TextMatcher.NormalizeTitle(GalleryJson.String(data["parody"]))) data["kind"] = "collection";
        if (UnicodeCaseFold.Fold(GalleryJson.String(data["category"])) is "artist cg" or "imageset" or "image set") data["kind"] = "collection";
        if (!GalleryJson.Truth(output["artist"]) && GalleryJson.Truth(data["artist"])) output["artist"] = data["artist"]!.DeepClone();
        foreach (var field in new[] { "artist", "circle", "language", "category", "parody" })
            if (GalleryJson.Truth(data[field]) && GalleryJson.String(data[field]) == GalleryJson.String(previous[field]) && previous["provenance"] is JsonObject old && GalleryJson.Truth(old[field])) provenance[field] = old[field]!.DeepClone();
        output["_gallery_metadata"] = data; return output;
    }
}

internal static class GalleryJson
{
    public static string String(JsonNode? value) => !Truth(value) ? "" : value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value is JsonValue boolean && boolean.TryGetValue<bool>(out var flag) ? flag ? "True" : "" : value!.ToJsonString();
    public static string First(params JsonNode?[] values) => values.Where(Truth).Select(String).FirstOrDefault() ?? "";
    public static string Text(JsonNode? value) => Regex.Replace(String(value).Normalize(NormalizationForm.FormKC), @"[\s\x1c-\x1f]+", " ").Trim();
    public static bool Truth(JsonNode? value) => value switch { null => false, JsonArray array => array.Count > 0, JsonObject obj => obj.Count > 0, JsonValue scalar when scalar.TryGetValue<string>(out var text) => text.Length > 0, JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag, JsonValue scalar when scalar.GetValueKind() == JsonValueKind.Number => Number(scalar) != 0, _ => true };
    public static double Number(JsonNode? value, double fallback = 0) => value is JsonValue scalar ? scalar.TryGetValue<double>(out var number) ? number : scalar.TryGetValue<long>(out var integer) ? integer : scalar.TryGetValue<int>(out var small) ? small : fallback : fallback;
    public static long Integer(JsonNode? value) => value is JsonValue scalar ? scalar.TryGetValue<long>(out var number) ? number : scalar.TryGetValue<int>(out var small) ? small : 0 : 0;
    public static bool StartsWithAny(this string value, char left, char right) => value.Length > 0 && (value[0] == left || value[0] == right);
    public static string TrimCharacters(string value, string characters)
    {
        var remove = characters.EnumerateRunes().ToHashSet(); var runes = value.EnumerateRunes().ToArray(); var first = 0; var last = runes.Length;
        while (first < last && remove.Contains(runes[first])) first++; while (last > first && remove.Contains(runes[last - 1])) last--;
        return string.Concat(runes.Skip(first).Take(last - first).Select(rune => rune.ToString()));
    }
}
