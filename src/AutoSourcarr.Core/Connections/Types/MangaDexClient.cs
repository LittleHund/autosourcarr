using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Connections.Types;

/// <summary>
/// MangaDex API (https://api.mangadex.org): GET /manga (title search) and GET /manga/{id}, with author, artist and cover
/// relationships (metadata_catalog.py _search_mangadex). MangaDex asks for at most 5 requests per second per client and
/// an identifying User-Agent; a shared 5/s bucket per host and the client's User-Agent do both.
/// </summary>
public sealed partial class MangaDexClient : IMetadataProviderClient
{
    public static readonly Uri DefaultBaseUri = new("https://api.mangadex.org/");
    public const string CoverHost = "https://uploads.mangadex.org";
    private static readonly string[] Includes = ["author", "artist", "cover_art"];
    private static readonly string[] AllRatings = ["safe", "suggestive", "erotica", "pornographic"];
    private static readonly string[] SafeRatings = ["safe", "suggestive", "erotica"];
    private readonly MetadataHttp _http;
    private readonly MetadataClientOptions _options;
    private readonly Uri _base;

    public MangaDexClient(HttpMessageHandler handler, MetadataClientOptions? options = null)
    {
        _options = options ?? new MetadataClientOptions();
        var root = _options.BaseUri ?? DefaultBaseUri;
        _base = root.AbsoluteUri.EndsWith('/') ? root : new Uri(root.AbsoluteUri + "/");
        _http = new MetadataHttp(Provider, handler, _options,
            _options.Limiter ?? TokenBucketLimiter.ForKey("mangadex|" + _base.Authority, 5, TimeSpan.FromSeconds(1)));
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex MangaId();

    public string Provider => "mangadex";
    public MetadataRateStatus RateStatus => _http.RateStatus;

    public async Task<IReadOnlyList<MetadataCandidate>> SearchAsync(string title, int limit = 6, CancellationToken cancellationToken = default)
    {
        var query = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (query.Length == 0) return [];
        var parameters = new List<(string, string)> { ("title", query), ("limit", Math.Clamp(limit, 1, 20).ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        parameters.AddRange(Includes.Select(include => ("includes[]", include)));
        parameters.Add(("order[relevance]", "desc"));
        foreach (var rating in _options.IncludeAdult ? AllRatings : SafeRatings)
            parameters.Add(("contentRating[]", rating));
        var uri = new Uri(_base, "manga?" + Query(parameters));
        var answer = await _http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken).ConfigureAwait(false);
        var root = Checked(answer);
        if (root["data"] is not JsonArray rows) throw new MetadataProviderException(Provider, "search answer has no data list");
        return rows.OfType<JsonObject>().Select(Map).Where(candidate => candidate.ExternalId.Length > 0).ToList();
    }

    public async Task<MetadataCandidate?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var key = (id ?? "").Trim().ToLowerInvariant();
        if (!MangaId().IsMatch(key)) throw new ArgumentException("A MangaDex manga id is a UUID.", nameof(id));
        var uri = new Uri(_base, "manga/" + key + "?" + Query(Includes.Select(include => ("includes[]", include))));
        var answer = await _http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken, notFoundIsNull: true).ConfigureAwait(false);
        if (answer is null) return null;
        return Checked(answer)["data"] is JsonObject row ? Map(row) : throw new MetadataProviderException(Provider, "manga answer has no data object");
    }

    private JsonObject Checked(JsonNode? answer)
    {
        if (answer is not JsonObject root) throw new MetadataProviderException(Provider, "answer is not a JSON object");
        if (JsonRead.Text(root["result"]) == "error")
        {
            var detail = string.Join("; ", JsonRead.Objects(root["errors"]).Select(error => JsonRead.Text(error["detail"]) is { Length: > 0 } text ? text : JsonRead.Text(error["title"])).Take(3));
            throw new MetadataProviderException(Provider, "error: " + (detail.Length > 0 ? detail : "unknown"));
        }
        return root;
    }

    private static string Query(IEnumerable<(string Key, string Value)> parameters)
    {
        var text = new StringBuilder();
        foreach (var (key, value) in parameters)
        {
            if (text.Length > 0) text.Append('&');
            text.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }
        return text.ToString();
    }

    private MetadataCandidate Map(JsonObject row)
    {
        var attributes = row["attributes"] as JsonObject ?? [];
        var titleMap = attributes["title"] as JsonObject ?? [];
        var primary = MetadataText.FirstText([JsonRead.Text(titleMap["en"]), .. titleMap.Select(pair => JsonRead.Text(pair.Value))]);
        var values = new List<(string?, string, string)>();
        values.AddRange(titleMap.Select(pair => ((string?)JsonRead.Text(pair.Value), pair.Key, "primary")));
        foreach (var block in JsonRead.Objects(attributes["altTitles"]))
            values.AddRange(block.Select(pair => ((string?)JsonRead.Text(pair.Value), pair.Key, "alias")));
        var descriptions = attributes["description"] as JsonObject ?? [];
        var description = MetadataText.FirstText([JsonRead.Text(descriptions["en"]), .. descriptions.Select(pair => JsonRead.Text(pair.Value))]);
        var authors = new List<string>();
        var artists = new List<string>();
        var coverFile = "";
        foreach (var relation in JsonRead.Objects(row["relationships"]))
        {
            var related = relation["attributes"] as JsonObject ?? [];
            switch (JsonRead.Text(relation["type"]))
            {
                case "author": authors.Add(JsonRead.Text(related["name"])); break;
                case "artist": artists.Add(JsonRead.Text(related["name"])); break;
                case "cover_art": if (JsonRead.Text(related["fileName"]) is { Length: > 0 } file) coverFile = file; break;
            }
        }
        var tags = JsonRead.Objects(attributes["tags"]).Select(tag =>
        {
            var names = (tag["attributes"] as JsonObject)?["name"] as JsonObject ?? [];
            return MetadataText.FirstText([JsonRead.Text(names["en"]), .. names.Select(pair => JsonRead.Text(pair.Value))]);
        });
        var linkMap = attributes["links"] as JsonObject ?? [];
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, provider) in new[] { ("al", "anilist"), ("mal", "myanimelist"), ("mu", "mangaupdates"), ("kt", "kitsu") })
            if (JsonRead.Text(linkMap[key]) is { Length: > 0 } value) links[provider] = value;
        var id = JsonRead.Text(row["id"]);
        return new MetadataCandidate
        {
            Provider = Provider,
            ExternalId = id,
            Url = id.Length > 0 ? "https://mangadex.org/title/" + id : "",
            Title = primary,
            Titles = MetadataText.TitleRows(primary, values),
            Description = description,
            CoverUrl = id.Length > 0 && coverFile.Length > 0 ? CoverHost + "/covers/" + id + "/" + coverFile + ".512.jpg" : "",
            Status = MetadataText.Status(JsonRead.Text(attributes["status"])),
            Authors = MetadataText.Unique(authors),
            Artists = MetadataText.Unique(artists),
            Genres = MetadataText.Unique(tags),
            MediaType = JsonRead.Text(attributes["publicationDemographic"]),
            Country = JsonRead.Text(attributes["originalLanguage"]),
            Year = MetadataText.Year(attributes["year"]),
            Adult = JsonRead.Text(attributes["contentRating"]) == "pornographic",
            Links = links,
            Raw = (JsonObject)row.DeepClone(),
        };
    }

    public void Dispose() => _http.Dispose();
}
