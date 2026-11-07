using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Connections.Types;

/// <summary>
/// 0.5.1: a LibreTranslate-compatible translator in the Connections menu (CON-1), like Sonarr's connection lists. Title
/// translation only widens searches (MAT-20). The first enabled translator wins over TRANSLATION_API_URL. 0.5.x has no
/// built-in translator (it returns in 0.6.0). The Test is read-only: <c>GET {base}/languages</c>.
/// </summary>
public sealed class TranslationConnectionType : IConnectionType
{
    public const string UrlField = "url";
    public const string ApiKeyField = "api_key";

    public static TranslationConnectionType LibreTranslate { get; } = new();

    public string Type => "libretranslate";
    public string Label => "LibreTranslate";
    public string Purpose => "Translates titles to find works under their original-language names (search hints only, never titles).";
    public bool Multiple => true;
    public IReadOnlyList<ConnectionField> Fields { get; } =
    [
        new(UrlField, "LibreTranslate address", ConnectionFieldKind.Url, Required: true, Default: null,
            Help: "Base address, for example http://libretranslate:5000 for a container in the same Docker network."),
        new(ApiKeyField, "API key", ConnectionFieldKind.Secret, Required: false, Default: null,
            Help: "Only if your LibreTranslate server requires a key."),
    ];

    /// <summary>True for the translation connection types.</summary>
    public static bool IsTranslator(string? type) => type == LibreTranslate.Type;

    /// <summary>
    /// The translator base URL: http(s), a host, no user name, query or fragment, trailing slash removed. Loopback
    /// addresses are refused: inside the container they point at AutoSourcarr itself, where 0.4.x ran its built-in
    /// translator (http://127.0.0.1:5000) and 0.5.x runs none. Null when the value is not such an address.
    /// </summary>
    public static string? BaseUrl(string? value)
    {
        var text = (value ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.Host.Length == 0
            || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0 || IsLoopback(url))
            return null;
        return text;
    }

    /// <summary>Whether the address points at this container (localhost, 127.0.0.0/8, ::1).</summary>
    public static bool IsLoopback(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.IsLoopback || url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ConnectionTestResult> TestAsync(ConnectionConfig connection, HttpMessageHandler http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(http);
        var raw = connection.Value(UrlField);
        if (BaseUrl(raw) is not { } baseUrl)
            return ConnectionTestResult.Fail(Uri.TryCreate((raw ?? "").Trim(), UriKind.Absolute, out var given) && IsLoopback(given)
                ? "This address points at the AutoSourcarr container itself, which has no translator in 0.5. Use the translator's own address, for example http://libretranslate:5000."
                : "Enter the translator's http or https base address, without a user name, a query or a fragment.");
        using var client = new HttpClient(http, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        var details = new JsonObject { ["url"] = new Uri(baseUrl).GetLeftPart(UriPartial.Authority) };
        using var list = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/languages");
        var (status, body, failure) = await SendAsync(client, list, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return ConnectionTestResult.Fail("No answer from " + details["url"] + " (" + failure + "). Check the address, port and Docker network.", details);
        if (status is < 200 or > 299)
            return ConnectionTestResult.Fail("The translator answered HTTP " + status.ToString(CultureInfo.InvariantCulture) + " for /languages.", details);
        JsonNode? parsed;
        try { parsed = body.Length > 1024 * 1024 ? null : JsonNode.Parse(body); }
        catch (JsonException) { parsed = null; }
        if (parsed is not JsonArray languages)
            return ConnectionTestResult.Fail("The address answered, but not like LibreTranslate (no language list from /languages).", details);
        details["languages"] = languages.Count;

        // /languages needs no key: one tiny translation proves the key (if the server requires one) is accepted.
        var payload = new JsonObject { ["q"] = "hello", ["source"] = "en", ["target"] = "ja", ["format"] = "text" };
        if (connection.Value(ApiKeyField) is { Length: > 0 } key) payload["api_key"] = key;
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/translate")
        { Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
        (status, _, failure) = await SendAsync(client, request, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return ConnectionTestResult.Fail("The language list loaded, but a test translation got no answer (" + failure + ").", details);
        if (status is 401 or 403)
            return ConnectionTestResult.Fail("The translator refused the test translation (HTTP " + status.ToString(CultureInfo.InvariantCulture) + "): enter or correct its API key.", details);
        if (status is < 200 or > 299)
            return ConnectionTestResult.Fail("The translator answered HTTP " + status.ToString(CultureInfo.InvariantCulture) + " for a test translation (en to ja); check that it has the Japanese model.", details);
        return ConnectionTestResult.Pass("LibreTranslate is ready (" + languages.Count.ToString(CultureInfo.InvariantCulture) + " languages; a test translation worked).", details);
    }

    /// <summary>One bounded request: status, body (at most 1 MiB read) or why there was no answer.</summary>
    private static async Task<(int Status, string Body, string? Failure)> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var body = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) : "";
            return ((int)response.StatusCode, body, null);
        }
        catch (HttpRequestException error) { return (0, "", error.HttpRequestError.ToString()); }
        catch (IOException error) { return (0, "", error.GetType().Name); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return (0, "", "no answer within 15 s"); }
    }
}
