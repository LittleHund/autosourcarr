using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Connections.Types;

/// <summary>
/// AniList GraphQL (https://graphql.anilist.co), manga only (metadata_catalog.py _search_anilist). Every request takes a
/// token from one per-process bucket of 30 requests per minute (AniList's current limit) shared by all clients of the
/// same host; X-RateLimit-Remaining lowers it further and a 429 is retried after its Retry-After within a bound.
/// </summary>
public sealed class AniListClient : IMetadataProviderClient
{
    public static readonly Uri DefaultBaseUri = new("https://graphql.anilist.co/");
    public const int RequestsPerMinute = 30;

    private const string Fields = """
        id idMal siteUrl format status countryOfOrigin isAdult genres synonyms chapters volumes
        startDate { year }
        title { romaji english native userPreferred }
        coverImage { extraLarge large medium }
        description(asHtml: false)
        tags { name isAdult rank }
        staff(perPage: 12) { edges { role node { name { full } } } }
        """;
    private const string SearchQuery = "query ($search: String!, $perPage: Int!) { Page(page: 1, perPage: $perPage) { media(search: $search, type: MANGA, sort: [SEARCH_MATCH, POPULARITY_DESC]) { " + Fields + " } } }";
    private const string GetQuery = "query ($id: Int!) { Media(id: $id, type: MANGA) { " + Fields + " } }";

    private static readonly string[] AuthorRoles = ["story", "original", "creator", "author"];
    private static readonly string[] ArtistRoles = ["art", "illustration", "artist"];
    private readonly MetadataHttp _http;
    private readonly MetadataClientOptions _options;
    private readonly Uri _endpoint;

    public AniListClient(HttpMessageHandler handler, MetadataClientOptions? options = null)
    {
        _options = options ?? new MetadataClientOptions();
        _endpoint = _options.BaseUri ?? DefaultBaseUri;
        _http = new MetadataHttp(Provider, handler, _options, _options.Limiter ?? SharedLimiter(_endpoint));
    }

    /// <summary>The per-process limiter for an AniList host: 30 requests per minute.</summary>
    public static TokenBucketLimiter SharedLimiter(Uri endpoint) => TokenBucketLimiter.ForKey("anilist|" + endpoint.Authority, RequestsPerMinute, TimeSpan.FromMinutes(1));

    public string Provider => "anilist";
    public MetadataRateStatus RateStatus => _http.RateStatus;

    public async Task<IReadOnlyList<MetadataCandidate>> SearchAsync(string title, int limit = 6, CancellationToken cancellationToken = default)
    {
        var query = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (query.Length == 0) return [];
        var data = await PostAsync(SearchQuery, new JsonObject { ["search"] = query, ["perPage"] = Math.Clamp(limit, 1, 20) }, cancellationToken).ConfigureAwait(false);
        if (data?["Page"] is not JsonObject page || page["media"] is not JsonArray media)
            throw new MetadataProviderException(Provider, "search answer has no Page.media list");
        return media.OfType<JsonObject>().Where(row => _options.IncludeAdult || !JsonRead.Bool(row["isAdult"])).Select(Map).ToList();
    }

    public async Task<MetadataCandidate?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new ArgumentException("An AniList id is a positive number.", nameof(id));
        var data = await PostAsync(GetQuery, new JsonObject { ["id"] = number }, cancellationToken, notFoundIsNull: true).ConfigureAwait(false);
        return data?["Media"] is JsonObject media ? Map(media) : null;
    }

    private async Task<JsonObject?> PostAsync(string query, JsonObject variables, CancellationToken cancellationToken, bool notFoundIsNull = false)
    {
        var body = new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString();
        var answer = await _http.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") },
            cancellationToken, notFoundIsNull).ConfigureAwait(false);
        if (answer is null) return null;
        if (answer is not JsonObject root) throw new MetadataProviderException(Provider, "answer is not a JSON object");
        if (root["errors"] is JsonArray { Count: > 0 } errors)
        {
            // AniList reports an unknown id as a GraphQL error with status 404.
            if (notFoundIsNull && errors.OfType<JsonObject>().Any(error => JsonRead.Int(error["status"]) == 404)) return null;
            var message = string.Join("; ", errors.OfType<JsonObject>().Select(error => JsonRead.Text(error["message"])).Where(text => text.Length > 0).Take(3));
            throw new MetadataProviderException(Provider, "GraphQL error: " + (message.Length > 0 ? message : "unknown"));
        }
        return root["data"] as JsonObject ?? throw new MetadataProviderException(Provider, "answer has no data object");
    }

    private MetadataCandidate Map(JsonObject row)
    {
        var title = row["title"] as JsonObject ?? [];
        var primary = MetadataText.FirstText(JsonRead.Text(title["english"]), JsonRead.Text(title["userPreferred"]), JsonRead.Text(title["romaji"]), JsonRead.Text(title["native"]));
        var values = new List<(string?, string, string)>
        {
            (JsonRead.Text(title["english"]), "en", "primary"), (JsonRead.Text(title["romaji"]), "", "romanized"),
            (JsonRead.Text(title["native"]), "", "native"), (JsonRead.Text(title["userPreferred"]), "", "primary"),
        };
        values.AddRange(JsonRead.Strings(row["synonyms"]).Select(alias => ((string?)alias, "", "alias")));
        var authors = new List<string>();
        var artists = new List<string>();
        foreach (var edge in JsonRead.Objects((row["staff"] as JsonObject)?["edges"]))
        {
            var role = JsonRead.Text(edge["role"]).ToLowerInvariant();
            var name = JsonRead.Text(((edge["node"] as JsonObject)?["name"] as JsonObject)?["full"]);
            if (AuthorRoles.Any(word => role.Contains(word, StringComparison.Ordinal))) authors.Add(name);
            if (ArtistRoles.Any(word => role.Contains(word, StringComparison.Ordinal))) artists.Add(name);
        }
        var genres = JsonRead.Strings(row["genres"]).ToList();
        genres.AddRange(JsonRead.Objects(row["tags"])
            .Where(tag => (_options.IncludeAdult || !JsonRead.Bool(tag["isAdult"])) && (JsonRead.Int(tag["rank"]) ?? 0) >= 60)
            .Select(tag => JsonRead.Text(tag["name"])));
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        if (JsonRead.Long(row["idMal"]) is long mal && mal > 0) links["myanimelist"] = mal.ToString(CultureInfo.InvariantCulture);
        var cover = row["coverImage"] as JsonObject ?? [];
        return new MetadataCandidate
        {
            Provider = Provider,
            ExternalId = JsonRead.Text(row["id"]),
            Url = JsonRead.Text(row["siteUrl"]),
            Title = primary,
            Titles = MetadataText.TitleRows(primary, values),
            Description = MetadataText.Plain(JsonRead.Text(row["description"])),
            CoverUrl = MetadataText.FirstText(JsonRead.Text(cover["extraLarge"]), JsonRead.Text(cover["large"]), JsonRead.Text(cover["medium"])),
            Status = MetadataText.Status(JsonRead.Text(row["status"])),
            Authors = MetadataText.Unique(authors),
            Artists = MetadataText.Unique(artists),
            Genres = MetadataText.Unique(genres),
            MediaType = JsonRead.Text(row["format"]).ToLowerInvariant(),
            Country = JsonRead.Text(row["countryOfOrigin"]),
            Year = MetadataText.Year((row["startDate"] as JsonObject)?["year"]),
            Adult = JsonRead.Bool(row["isAdult"]),
            Links = links,
            Raw = (JsonObject)row.DeepClone(),
        };
    }

    public void Dispose() => _http.Dispose();
}
