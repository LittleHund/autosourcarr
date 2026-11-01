using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Connections.Types;

/// <summary>Address and Basic credentials of the user's main Suwayomi (MAIN_SUWAYOMI_URL / USERNAME / PASSWORD).</summary>
public sealed record SuwayomiEndpoint(Uri BaseUri, string Username = "", string Password = "")
{
    public AuthenticationHeaderValue? Authorization => Username.Length == 0 ? null
        : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Username + ":" + Password)));

    public Uri GraphQl => new(Root, "api/graphql");

    /// <summary>ws:// or wss:// address of the GraphQL subscription endpoint (same path as HTTP).</summary>
    public Uri WebSocket => new UriBuilder(GraphQl) { Scheme = BaseUri.Scheme == "https" ? "wss" : "ws" }.Uri;

    private Uri Root => BaseUri.AbsoluteUri.EndsWith('/') ? BaseUri : new Uri(BaseUri.AbsoluteUri + "/");

    public static SuwayomiEndpoint FromConnection(ConnectionConfig connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!ConnectionHttp.TryHttpUri(connection.Value(SuwayomiConnectionType.UrlField), out var uri, out var error)) throw new ArgumentException(error);
        return new SuwayomiEndpoint(uri, connection.Value(SuwayomiConnectionType.UsernameField), connection.Value(SuwayomiConnectionType.PasswordField));
    }
}

/// <summary>Minimal read-only GraphQL-over-HTTP client for the main Suwayomi (Test, change-feed polling, import read).</summary>
public sealed class SuwayomiGraphQlHttp : IDisposable
{
    private readonly HttpClient _client;

    public SuwayomiGraphQlHttp(SuwayomiEndpoint endpoint, HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _client = ConnectionHttp.Client(handler, timeout ?? TimeSpan.FromSeconds(20));
    }

    public SuwayomiEndpoint Endpoint { get; }

    public async Task<JsonObject> QueryAsync(string query, JsonObject? variables, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["query"] = query, ["variables"] = variables?.DeepClone() ?? new JsonObject() }.ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint.GraphQl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = Endpoint.Authorization;
        request.Headers.Accept.ParseAdd("application/json");
        var answer = await ConnectionHttp.SendJsonAsync(_client, request, "Suwayomi", cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ConnectionHttpException("Suwayomi did not answer the GraphQL request with an object; is this a Suwayomi server?", null);
        if (answer["errors"] is JsonArray { Count: > 0 } errors)
        {
            var message = string.Join("; ", errors.OfType<JsonObject>().Select(error => JsonRead.Text(error["message"])).Where(text => text.Length > 0).Take(3));
            throw new ConnectionHttpException("Suwayomi GraphQL error: " + (message.Length > 0 ? message : "unknown") + ".", null);
        }
        return answer["data"] as JsonObject ?? throw new ConnectionHttpException("Suwayomi answered without a GraphQL data object.", null);
    }

    /// <summary>
    /// Every library manga with the given selection, page by page (500 per page, at most 100 pages). Stops with an error
    /// on a missing or repeated cursor rather than returning an incomplete library.
    /// </summary>
    public async Task<List<JsonObject>> LibraryAsync(string fields, CancellationToken cancellationToken, int pageSize = 500)
    {
        var query = "query AutoSourcarrLibraryFeed($first: Int!, $after: Cursor) { mangas(condition: {inLibrary: true}, first: $first, after: $after) { nodes { "
            + fields + " } pageInfo { hasNextPage endCursor } } }";
        var items = new List<JsonObject>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        for (var page = 0; page < 100; page++)
        {
            var data = await QueryAsync(query, new JsonObject { ["first"] = pageSize, ["after"] = after }, cancellationToken).ConfigureAwait(false);
            if (data["mangas"] is not JsonObject connection || connection["nodes"] is not JsonArray nodes)
                throw new ConnectionHttpException("Suwayomi library answer has no mangas.nodes list.", null);
            items.AddRange(nodes.OfType<JsonObject>().Select(node => (JsonObject)node.DeepClone()));
            var info = connection["pageInfo"] as JsonObject;
            if (info is null || !JsonRead.Bool(info["hasNextPage"])) return items;
            after = JsonRead.Text(info["endCursor"]);
            if (after.Length == 0 || !cursors.Add(after)) throw new ConnectionHttpException("Suwayomi library paging repeated or lost its cursor.", null);
        }
        throw new ConnectionHttpException("Suwayomi library has more than 100 pages; refusing an incomplete read.", null);
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// CON-3: the user's main Suwayomi as a connection (one only). Test = GraphQL aboutServer (name/version/revision) and the
/// library size, both read-only. The change feed (<see cref="SuwayomiChangeFeed"/>) and the one-time import
/// (<see cref="SuwayomiImport"/>) use the same address and credentials.
/// </summary>
public sealed class SuwayomiConnectionType : IConnectionType
{
    public const string UrlField = "url";
    public const string UsernameField = "username";
    public const string PasswordField = "password";
    public const string TestQuery = "query AutoSourcarrConnectionTest { aboutServer { name version revision buildType } mangas(condition: {inLibrary: true}, first: 1) { totalCount } }";

    public string Type => "suwayomi";
    public string Label => "Suwayomi";
    public string Purpose => "Your main Suwayomi library: imported once, then its changes show up here within seconds.";
    public bool Multiple => false;

    public IReadOnlyList<ConnectionField> Fields =>
    [
        new(UrlField, "Suwayomi URL", ConnectionFieldKind.Url, true, null, "The address of your own Suwayomi-Server (MAIN_SUWAYOMI_URL), e.g. http://suwayomi:4567"),
        new(UsernameField, "Username", ConnectionFieldKind.Text, false, null, "Only when Suwayomi uses basic authentication."),
        new(PasswordField, "Password", ConnectionFieldKind.Secret, false, null, null),
    ];

    public Task<ConnectionTestResult> TestAsync(ConnectionConfig connection, HttpMessageHandler http, CancellationToken cancellationToken)
        => ConnectionTesting.RunAsync(Label, async watch =>
        {
            using var client = new SuwayomiGraphQlHttp(SuwayomiEndpoint.FromConnection(connection), http, TimeSpan.FromSeconds(15));
            var data = await client.QueryAsync(TestQuery, null, cancellationToken).ConfigureAwait(false);
            var about = data["aboutServer"] as JsonObject;
            var version = JsonRead.Text(about?["version"]);
            if (about is null || version.Length == 0)
                return ConnectionTestResult.Fail("The server answered GraphQL but not like Suwayomi (no aboutServer version).",
                    new JsonObject { ["latency_ms"] = watch.ElapsedMilliseconds });
            var count = JsonRead.Int((data["mangas"] as JsonObject)?["totalCount"]);
            var name = JsonRead.Text(about["name"]);
            var details = new JsonObject
            {
                ["latency_ms"] = watch.ElapsedMilliseconds, ["name"] = name, ["version"] = version,
                ["revision"] = JsonRead.Text(about["revision"]), ["build_type"] = JsonRead.Text(about["buildType"]), ["library_count"] = count,
            };
            return ConnectionTestResult.Pass((name.Length > 0 ? name : "Suwayomi") + " " + version + " answered in "
                + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms"
                + (count is { } total ? "; " + total.ToString(CultureInfo.InvariantCulture) + " manga in its library." : "."), details);
        }, cancellationToken);
}
