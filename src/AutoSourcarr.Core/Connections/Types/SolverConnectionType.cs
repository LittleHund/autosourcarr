using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Connections.Types;

/// <summary>
/// CON-2: a FlareSolverr-compatible Cloudflare solver, used by the source host after its built-in WebView fails a
/// challenge. Two types share this class: FlareSolverr and Byparr (Byparr implements the FlareSolverr v1 API). The
/// host takes only the solver's cookies and user agent (cookies only, cached per website host); the solver's page
/// is never used as content. The Test is read-only: <c>GET /health</c> and <c>GET /</c>, never a browse.
/// </summary>
public sealed class SolverConnectionType : IConnectionType
{
    public const string UrlField = "url";
    public const string TimeoutField = "max_timeout";
    public const int DefaultTimeoutSeconds = 60;
    public const int MinTimeoutSeconds = 10;
    public const int MaxTimeoutSeconds = 300;

    public static SolverConnectionType FlareSolverr { get; } = new("flaresolverr", "FlareSolverr", "http://flaresolverr:8191",
        "Solves Cloudflare checks the built-in WebView could not pass (FlareSolverr, cookies only).");

    public static SolverConnectionType Byparr { get; } = new("byparr", "Byparr", "http://byparr:8191",
        "Solves Cloudflare checks the built-in WebView could not pass (Byparr, FlareSolverr-compatible, cookies only).");

    /// <summary>Both solver types, in the order the Connections menu lists them.</summary>
    public static IReadOnlyList<SolverConnectionType> All { get; } = [FlareSolverr, Byparr];

    private SolverConnectionType(string type, string label, string example, string purpose)
    {
        Type = type; Label = label; Purpose = purpose;
        Fields =
        [
            new(UrlField, label + " address", ConnectionFieldKind.Url, Required: true, Default: null,
                Help: "Base address without /v1, for example " + example + "."),
            new(TimeoutField, "Most seconds per challenge", ConnectionFieldKind.Integer, Required: false,
                Default: DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                Help: "How long the solver may work on one challenge (" + MinTimeoutSeconds.ToString(CultureInfo.InvariantCulture)
                    + " to " + MaxTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + ")."),
        ];
    }

    public string Type { get; }
    public string Label { get; }
    public string Purpose { get; }
    public bool Multiple => true;
    public IReadOnlyList<ConnectionField> Fields { get; }

    /// <summary>True for the solver connection types (flaresolverr, byparr).</summary>
    public static bool IsSolver(string? type) => All.Any(solver => solver.Type == type);

    /// <summary>
    /// The solver base URL: http(s), a host, no user name or password, no query or fragment; a trailing slash or /v1
    /// is removed. Null when the value is not such an address.
    /// </summary>
    public static Uri? BaseUrl(string? value)
    {
        var text = (value ?? "").Trim().TrimEnd('/');
        if (text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) text = text[..^3].TrimEnd('/');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.Host.Length == 0
            || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
            return null;
        return url;
    }

    /// <summary>Seconds per challenge, clamped to 10-300; 60 when unset or not a whole number.</summary>
    public static int TimeoutSeconds(ConnectionConfig connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return int.TryParse(connection.Value(TimeoutField), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Clamp(seconds, MinTimeoutSeconds, MaxTimeoutSeconds) : DefaultTimeoutSeconds;
    }

    /// <summary>
    /// AUTOSOURCARR_SOLVERS for the source host: a JSON array of <c>{"url","timeoutMs","type"}</c> for every enabled
    /// solver connection with a valid address, in the order given (the host asks them in that order). "[]" when none.
    /// </summary>
    public static string HostEnvironment(IEnumerable<ConnectionConfig> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var list = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            if (connection is null || !connection.Enabled || !IsSolver(connection.Type)) continue;
            if (BaseUrl(connection.Value(UrlField)) is not { } url) continue;
            var text = url.AbsoluteUri.TrimEnd('/');
            if (!seen.Add(text)) continue;
            list.Add(new JsonObject
            {
                ["url"] = text,
                ["timeoutMs"] = TimeoutSeconds(connection) * 1000,
                ["type"] = connection.Type,
            });
        }
        return list.ToJsonString();
    }

    public async Task<ConnectionTestResult> TestAsync(ConnectionConfig connection, HttpMessageHandler http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(http);
        if (BaseUrl(connection.Value(UrlField)) is not { } baseUrl)
            return ConnectionTestResult.Fail("Enter the solver's http or https base address, without /v1, a user name, a query or a fragment.");
        using var client = new HttpClient(http, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        var root = baseUrl.AbsoluteUri.TrimEnd('/');
        var authority = baseUrl.GetLeftPart(UriPartial.Authority);
        var details = new JsonObject { ["url"] = authority };

        // FlareSolverr and Byparr answer GET /health with {"status":"ok"}; FlareSolverr's root also names its version.
        var health = await GetJsonAsync(client, root + "/health", cancellationToken).ConfigureAwait(false);
        var home = await GetJsonAsync(client, root + "/", cancellationToken).ConfigureAwait(false);
        var serverError = health.Status >= 500 || (home.Status >= 500 && health.Status is 0 or >= 400);
        if (serverError)
            return ConnectionTestResult.Fail(Label + " answered HTTP " + Math.Max(health.Status, home.Status).ToString(CultureInfo.InvariantCulture)
                + ". Check its logs; it may be starting or out of memory.", details);
        var healthy = health.Body is JsonObject healthBody && Text(healthBody, "status") is { } status && status.Equals("ok", StringComparison.OrdinalIgnoreCase);
        var message = home.Body is JsonObject homeBody ? Text(homeBody, "msg") : null;
        var version = home.Body is JsonObject homeVersion ? Text(homeVersion, "version") : null;
        var ready = message is not null && (message.Contains("ready", StringComparison.OrdinalIgnoreCase) || message.Contains("working", StringComparison.OrdinalIgnoreCase));
        if (version is not null) details["version"] = version;
        if (message is not null) details["message"] = message;
        if (healthy || ready)
            return ConnectionTestResult.Pass(Label + " is ready" + (version is null ? "." : " (version " + version + ")."), details);
        if (health.Status == 0 && home.Status == 0)
            return ConnectionTestResult.Fail("No answer from " + authority + ". Check the address, port and Docker network.", details);
        return ConnectionTestResult.Fail("The address answered, but not like a FlareSolverr-compatible solver (no {\"status\":\"ok\"} from /health). "
            + "Check that the address belongs to " + Label + ".", details);
    }

    private static string? Text(JsonObject body, string key) =>
        body[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    /// <summary>One bounded GET: status (0 when the address cannot be reached) and the body when it is a JSON value.</summary>
    private static async Task<(int Status, JsonNode? Body)> GetJsonAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try { response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { return (0, null); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return (0, null); }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) return (status, null);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[64 * 1024];
                var total = 0;
                int read;
                while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0) total += read;
                try { return (status, JsonNode.Parse(buffer.AsMemory(0, total).Span)); }
                catch (JsonException) { return (status, null); }
            }
        }
    }
}
