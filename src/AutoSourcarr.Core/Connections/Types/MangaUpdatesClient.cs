using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Connections.Types;

/// <summary>
/// MangaUpdates API v1 (https://api.mangaupdates.com/v1): POST series/search and GET series/{id}
/// (metadata_catalog.py _search_mangaupdates). MangaUpdates publishes no fixed limit; a small shared bucket
/// (2 requests per second per host) keeps bulk matching polite, and 429/Retry-After is honoured like every provider.
/// </summary>
public sealed class MangaUpdatesClient : IMetadataProviderClient
{
    public static readonly Uri DefaultBaseUri = new("https://api.mangaupdates.com/v1/");
    private readonly MetadataHttp _http;
    private readonly MetadataClientOptions _options;
    private readonly Uri _base;

    public MangaUpdatesClient(HttpMessageHandler handler, MetadataClientOptions? options = null)
    {
        _options = options ?? new MetadataClientOptions();
        _base = WithSlash(_options.BaseUri ?? DefaultBaseUri);
        _http = new MetadataHttp(Provider, handler, _options,
            _options.Limiter ?? TokenBucketLimiter.ForKey("mangaupdates|" + _base.Authority, 2, TimeSpan.FromSeconds(1)));
    }

    public string Provider => "mangaupdates";
    public MetadataRateStatus RateStatus => _http.RateStatus;

    public async Task<IReadOnlyList<MetadataCandidate>> SearchAsync(string title, int limit = 6, CancellationToken cancellationToken = default)
    {
        var query = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (query.Length == 0) return [];
        var body = new JsonObject { ["search"] = query, ["perpage"] = Math.Clamp(limit, 1, 20) }.ToJsonString();
        var answer = await _http.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "series/search"))
            { Content = new StringContent(body, Encoding.UTF8, "application/json") }, cancellationToken).ConfigureAwait(false);
        if (answer is not JsonObject root || (root["results"] ?? root["data"]) is not JsonArray rows)
            throw new MetadataProviderException(Provider, "search answer has no results list");
        var result = new List<MetadataCandidate>();
        foreach (var wrapper in rows.OfType<JsonObject>())
        {
            var row = wrapper["record"] as JsonObject ?? wrapper;
            if (Map(row) is { } candidate && (_options.IncludeAdult || !candidate.Adult)) result.Add(candidate);
        }
        return result;
    }

    public async Task<MetadataCandidate?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new ArgumentException("A MangaUpdates series id is a positive number.", nameof(id));
        var answer = await _http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "series/" + number.ToString(CultureInfo.InvariantCulture))),
            cancellationToken, notFoundIsNull: true).ConfigureAwait(false);
        if (answer is null) return null;
        if (answer is not JsonObject row) throw new MetadataProviderException(Provider, "series answer is not a JSON object");
        return Map(row) ?? throw new MetadataProviderException(Provider, "series answer has no series_id");
    }

    private MetadataCandidate? Map(JsonObject row)
    {
        var id = JsonRead.Text(row["series_id"] ?? row["id"]);
        if (id.Length == 0) return null;
        var primary = MetadataText.FirstText(JsonRead.Text(row["title"]), JsonRead.Text(row["name"]));
        var aliases = new List<string>();
        foreach (var key in new[] { "associated", "associated_names", "alt_titles" })
            switch (row[key])
            {
                case JsonArray array: aliases.AddRange(array.Select(item => item is JsonObject named ? JsonRead.Text(named["title"]) : JsonRead.Text(item))); break;
                case JsonObject map: aliases.AddRange(map.Select(pair => JsonRead.Text(pair.Value))); break;
                case JsonValue single: aliases.Add(JsonRead.Text(single)); break;
            }
        var cover = "";
        if (row["image"] is JsonObject image)
        {
            // v1: image.url is { original, thumb }; older answers used flat fields.
            var urls = image["url"] as JsonObject ?? [];
            cover = MetadataText.FirstText(JsonRead.Text(urls["original"]), JsonRead.Text(image["url"]), JsonRead.Text(image["original"]),
                JsonRead.Text(urls["thumb"]), JsonRead.Text(image["thumb_url"]));
        }
        var genres = (row["genres"] as JsonArray ?? []).Select(item => item is JsonObject named ? MetadataText.FirstText(JsonRead.Text(named["genre"]), JsonRead.Text(named["name"])) : JsonRead.Text(item)).ToList();
        var adult = genres.Any(genre => genre.Contains("adult", StringComparison.OrdinalIgnoreCase) || genre.Contains("hentai", StringComparison.OrdinalIgnoreCase));
        var authors = new List<string>();
        var artists = new List<string>();
        foreach (var person in JsonRead.Objects(row["authors"]))
        {
            var name = JsonRead.Text(person["name"]);
            if (JsonRead.Text(person["type"]).Equals("Artist", StringComparison.OrdinalIgnoreCase)) artists.Add(name); else authors.Add(name);
        }
        if (authors.Count == 0) authors.Add(JsonRead.Text(row["author"]));
        if (artists.Count == 0) artists.Add(JsonRead.Text(row["artist"]));
        var url = JsonRead.Text(row["url"]);
        return new MetadataCandidate
        {
            Provider = Provider,
            ExternalId = id,
            Url = url.Length > 0 ? url : "https://www.mangaupdates.com/series/" + id,
            Title = primary,
            Titles = MetadataText.TitleRows(primary, aliases.Select(alias => ((string?)alias, "", "alias"))),
            Description = MetadataText.FirstText(JsonRead.Text(row["description"]), JsonRead.Text(row["summary"])),
            CoverUrl = cover,
            Status = Status(JsonRead.Text(row["status"])),
            Authors = MetadataText.Unique(authors),
            Artists = MetadataText.Unique(artists),
            Genres = MetadataText.Unique(genres),
            MediaType = JsonRead.Text(row["type"]).ToLowerInvariant(),
            Year = MetadataText.Year(row["year"]),
            Adult = adult,
            Raw = (JsonObject)row.DeepClone(),
        };
    }

    /// <summary>MangaUpdates status is free text such as "37 Volumes (Ongoing)".</summary>
    private static string Status(string text)
    {
        var plain = MetadataText.Status(text);
        if (plain != "unknown") return plain;
        foreach (var (word, status) in new[] { ("ongoing", "ongoing"), ("complete", "completed"), ("hiatus", "hiatus"), ("cancel", "cancelled"), ("discontinued", "cancelled") })
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) return status;
        return "unknown";
    }

    private static Uri WithSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    public void Dispose() => _http.Dispose();
}
