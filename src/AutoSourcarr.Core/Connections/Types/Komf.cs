using System.Globalization;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Connections.Types;

/// <summary>One Komf metadata search result (komf-api-models KomfMetadataSeriesSearchResult).</summary>
public sealed record KomfSearchResult(string Title, string Provider, string ResultId, string Url, string ImageUrl);

/// <summary>The media server Komf talks to, from GET /api/{server}/media-server/connected.</summary>
public sealed record KomfMediaServerStatus(bool Success, int? HttpStatusCode, string ErrorMessage);

/// <summary>
/// Komf (https://github.com/Snd-R/komf) HTTP API, read-only parts, under /api/{komga|kavita}:
/// GET metadata/providers (enabled provider names), GET metadata/search?name= (series candidates across its providers),
/// GET media-server/connected. Komf has no authentication of its own; put it behind your proxy if needed.
/// </summary>
public sealed class KomfClient : IDisposable
{
    public static readonly IReadOnlyList<string> MediaServers = ["komga", "kavita"];
    private readonly HttpClient _client;
    private readonly Uri _api;

    public KomfClient(Uri baseUri, HttpMessageHandler handler, string mediaServer = "komga", TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        var server = (mediaServer ?? "").Trim().ToLowerInvariant();
        if (server.Length == 0) server = "komga";
        if (!MediaServers.Contains(server)) throw new ArgumentException("The Komf media server is komga or kavita.", nameof(mediaServer));
        MediaServer = server;
        var root = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
        _api = new Uri(root, "api/" + server + "/");
        _client = ConnectionHttp.Client(handler, timeout ?? TimeSpan.FromSeconds(30));
    }

    public string MediaServer { get; }

    /// <summary>Names of the metadata providers Komf has enabled (e.g. MANGA_UPDATES, MANGADEX, ANILIST).</summary>
    public async Task<IReadOnlyList<string>> ProvidersAsync(string? libraryId = null, CancellationToken cancellationToken = default)
    {
        var path = "metadata/providers" + (string.IsNullOrWhiteSpace(libraryId) ? "" : "?libraryId=" + Uri.EscapeDataString(libraryId));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_api, path));
        var answer = await ConnectionHttp.SendJsonAsync(_client, request, "Komf", cancellationToken).ConfigureAwait(false);
        if (answer is not JsonArray list || list.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out _)))
            throw new ConnectionHttpException("The server answered, but not like Komf: metadata/providers is not a list of provider names.", null);
        return list.Select(JsonRead.Text).ToList();
    }

    /// <summary>Series candidates for a title from every provider Komf has enabled.</summary>
    public async Task<IReadOnlyList<KomfSearchResult>> SearchAsync(string name, string? libraryId = null, CancellationToken cancellationToken = default)
    {
        var query = (name ?? "").Trim();
        if (query.Length == 0) return [];
        var path = "metadata/search?name=" + Uri.EscapeDataString(query) + (string.IsNullOrWhiteSpace(libraryId) ? "" : "&libraryId=" + Uri.EscapeDataString(libraryId));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_api, path));
        var answer = await ConnectionHttp.SendJsonAsync(_client, request, "Komf", cancellationToken).ConfigureAwait(false);
        if (answer is not JsonArray list) throw new ConnectionHttpException("Komf search did not answer with a list.", null);
        return list.OfType<JsonObject>()
            .Select(row => new KomfSearchResult(JsonRead.Text(row["title"]), JsonRead.Text(row["provider"]), JsonRead.Text(row["resultId"]),
                JsonRead.Text(row["url"]), JsonRead.Text(row["imageUrl"])))
            .Where(row => row.Title.Length > 0 && row.ResultId.Length > 0).ToList();
    }

    /// <summary>Whether Komf reaches its media server (Komga/Kavita).</summary>
    public async Task<KomfMediaServerStatus> MediaServerStatusAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_api, "media-server/connected"));
        var answer = await ConnectionHttp.SendJsonAsync(_client, request, "Komf", cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ConnectionHttpException("Komf media-server/connected did not answer with an object.", null);
        return new KomfMediaServerStatus(JsonRead.Bool(answer["success"]), JsonRead.Int(answer["httpStatusCode"]), JsonRead.Text(answer["errorMessage"]));
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Komf metadata server (phase 9 uses it for series identification through its providers).</summary>
public sealed class KomfConnectionType : IConnectionType
{
    public const string UrlField = "url";
    public const string MediaServerField = "media_server";

    public string Type => "komf";
    public string Label => "Komf";
    public string Purpose => "Uses a Komf metadata server and the providers enabled in it to identify series.";
    public bool Multiple => false;

    public IReadOnlyList<ConnectionField> Fields =>
    [
        new(UrlField, "Komf URL", ConnectionFieldKind.Url, true, null, "For example http://komf:8085"),
        new(MediaServerField, "Media server", ConnectionFieldKind.Text, false, "komga", "komga or kavita: the media server Komf is configured for."),
    ];

    public static KomfClient CreateClient(ConnectionConfig connection, HttpMessageHandler http)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!ConnectionHttp.TryHttpUri(connection.Value(UrlField), out var uri, out var error)) throw new ArgumentException(error);
        return new KomfClient(uri, http, connection.Value(MediaServerField, "komga"), TimeSpan.FromSeconds(15));
    }

    public Task<ConnectionTestResult> TestAsync(ConnectionConfig connection, HttpMessageHandler http, CancellationToken cancellationToken)
        => ConnectionTesting.RunAsync(Label, async watch =>
        {
            using var client = CreateClient(connection, http);
            var providers = await client.ProvidersAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var details = new JsonObject
            {
                ["latency_ms"] = watch.ElapsedMilliseconds,
                ["media_server"] = client.MediaServer,
                ["providers"] = new JsonArray(providers.Select(name => (JsonNode)JsonValue.Create(name)!).ToArray()),
            };
            // Informative only: Komf itself is usable for searches even when its media server is down.
            try
            {
                var server = await client.MediaServerStatusAsync(cancellationToken).ConfigureAwait(false);
                details["media_server_connected"] = server.Success;
                if (server.ErrorMessage.Length > 0) details["media_server_error"] = server.ErrorMessage;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is ConnectionHttpException or HttpRequestException or TimeoutException)
            { details["media_server_connected"] = null; }
            if (providers.Count == 0)
                return ConnectionTestResult.Pass("Komf answered, but no metadata providers are enabled in it.", details);
            return ConnectionTestResult.Pass("Komf answered in " + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms with "
                + providers.Count.ToString(CultureInfo.InvariantCulture) + " providers: " + string.Join(", ", providers) + ".", details);
        }, cancellationToken);
}
