using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Sources;

/// <summary>
/// Phase 4: the AutoSourcarr source host replaces Suwayomi as source engine. Its API is REST with Suwayomi's field
/// names and ids, so this client keeps one contract: in host mode each GraphQL operation the app sends is answered by
/// the matching host REST call and returned in the same "data" shape. Callers, response checks and the attention /
/// verification retry logic stay unchanged. Operations the host does not offer fail clearly instead of guessing.
/// The main Suwayomi library (MAIN_SUWAYOMI_URL) still speaks real GraphQL; only the source engine uses this mode.
/// </summary>
public sealed partial class SuwayomiClient
{
    private static readonly Regex OperationName = new(@"^\s*(query|mutation)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);

    /// <summary>True when this client talks to the AutoSourcarr source host (REST) instead of Suwayomi GraphQL.</summary>
    public bool SourceHost { get; private init; }

    private string _hostToken = "";

    /// <summary>A client for the AutoSourcarr source host; <paramref name="token"/> is sent as X-Source-Host-Token when set.</summary>
    public static SuwayomiClient ForSourceHost(Settings.AutoSourcarrSettings settings, string baseUrl, string token = "", int workLimit = 0,
        IManagedSourceRuntime? runtime = null, Privacy.SupportPrivacy? privacy = null, HttpMessageHandler? handler = null,
        ISourceTransportFactory? transportFactory = null)
    {
        var client = new SuwayomiClient(settings, baseUrl, workLimit, runtime, privacy: privacy, handler: handler, transportFactory: transportFactory)
        { SourceHost = true };
        client._hostToken = token;
        if (token.Length > 0) client._privacy.Register(new Dictionary<string, string?> { ["source_host_token"] = token });
        return client;
    }

    /// <summary>A host-only REST call (source browser, reload, health) for the source runtime routes.</summary>
    public async Task<JsonNode?> HostAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
    {
        if (!SourceHost) throw new SourceEngineUnavailableException("This source engine is not the AutoSourcarr source host.");
        await using var session = OpenSession(cancellationToken);
        return await HostCoreAsync(session, method, path, body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One REST call to the host; a non-2xx answer becomes a SuwayomiException with the host's detail text.</summary>
    internal async Task<JsonNode?> HostCoreAsync(SourceTransportSession session, HttpMethod method, string path, JsonNode? body,
        CancellationToken cancellationToken, byte[]? raw = null, string? rawType = null, TimeSpan? timeout = null)
    {
        session.ThrowIfRetired(); _ = session.EngineForRequest();
        var bytes = raw ?? (body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body));
        await using var response = await SendAsync(session, method, new Uri(BaseUrl + path), bytes, raw is not null ? rawType : bytes is null ? null : "application/json",
            null, timeout, followRedirects: false, cancellationToken, requireRuntimeOrigin: true, allowErrorStatus: true).ConfigureAwait(false);
        var json = await ReadJsonAsync(response).ConfigureAwait(false);
        session.ThrowIfRetired();
        if (response.StatusCode is < 200 or > 299)
        {
            var detail = (json as JsonObject)?["detail"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture);
            var kind = (json as JsonObject)?["error_type"] is JsonValue type && type.TryGetValue<string>(out var name) ? name : "";
            if (kind == "SourceBackedOff") throw new SourceWorkDeferredException(_privacy.Text(detail));
            throw new SuwayomiException(Limit(_privacy.Text(detail), 1000));
        }
        return json;
    }

    /// <summary>Answers a GraphQL operation of this app with the host's REST API, in GraphQL's data shape.</summary>
    private async Task<JsonObject> HostOperationAsync(SourceTransportSession session, string query, JsonObject? variables, CancellationToken cancellationToken)
    {
        var name = OperationName.Match(query) is { Success: true } match ? match.Groups[2].Value
            : query.Contains("extensions { nodes {", StringComparison.Ordinal) ? "AutoSourcarrExtensions" : "";
        var input = variables?["input"] as JsonObject ?? new JsonObject();
        string Id(JsonNode? node) => SourceResponseContract.Identity(node) ?? throw new SuwayomiException("Missing identifier for " + name);
        async Task<JsonNode?> Call(HttpMethod method, string path, JsonNode? body = null) => await HostCoreAsync(session, method, path, body, cancellationToken).ConfigureAwait(false);
        switch (name)
        {
            case "AutoSourcarrHealth":
                await Call(HttpMethod.Get, "/v1/health").ConfigureAwait(false);
                return new() { ["__typename"] = "Query" };
            case "AutoSourcarrSources":
                return new() { ["sources"] = new JsonObject { ["nodes"] = await Call(HttpMethod.Get, "/v1/sources").ConfigureAwait(false) } };
            case "AutoSourcarrExtensions":
                var list = await Call(HttpMethod.Get, "/v1/extensions").ConfigureAwait(false) as JsonObject ?? throw new SuwayomiException("Incomplete extension list");
                return new() { ["extensions"] = new JsonObject { ["nodes"] = list["extensions"]?.DeepClone() } };
            case "AutoSourcarrFetchExtensions":
                var fetched = await Call(HttpMethod.Post, "/v1/extensions/fetch").ConfigureAwait(false) as JsonObject ?? throw new SuwayomiException("Incomplete extension list");
                return new() { ["fetchExtensions"] = new JsonObject { ["extensions"] = fetched["extensions"]?.DeepClone(), ["extensionStores"] = fetched["extensionStores"]?.DeepClone() } };
            case "AutoSourcarrUpdateExtension":
            {
                var action = (input["patch"] as JsonObject)?.Select(pair => pair.Key).FirstOrDefault(key => key is "install" or "update" or "uninstall")
                    ?? throw new SuwayomiException("Unsupported extension change");
                var changed = await Call(HttpMethod.Post, "/v1/extensions/" + Uri.EscapeDataString(Id(input["id"])) + "/" + action).ConfigureAwait(false) as JsonObject;
                return new() { ["updateExtension"] = new JsonObject { ["extension"] = changed?["extension"]?.DeepClone() } };
            }
            case "AutoSourcarrUpdateExtensions":
            {
                var results = new JsonArray();
                foreach (var id in (input["ids"] as JsonArray ?? new JsonArray()).Select(Id))
                    results.Add((await Call(HttpMethod.Post, "/v1/extensions/" + Uri.EscapeDataString(id) + "/update").ConfigureAwait(false) as JsonObject)?["extension"]?.DeepClone());
                return new() { ["updateExtensions"] = new JsonObject { ["extensions"] = results } };
            }
            case "AutoSourcarrBrowse":
            {
                var path = "/v1/sources/" + Uri.EscapeDataString(Id(input["source"])) + "/mangas?type=" + Uri.EscapeDataString(SourceResponseContract.Text(input["type"]))
                    + "&page=" + Id(input["page"]) + "&query=" + Uri.EscapeDataString(SourceResponseContract.Text(input["query"]));
                return new() { ["fetchSourceManga"] = await Call(HttpMethod.Get, path).ConfigureAwait(false) };
            }
            case "AutoSourcarrManga":
                return new() { ["fetchMangaAndChapters"] = await Call(HttpMethod.Post, "/v1/mangas/" + Id(input["id"]) + "/fetch",
                    new JsonObject { ["fetchManga"] = input["fetchManga"]?.DeepClone() ?? true, ["fetchChapters"] = input["fetchChapters"]?.DeepClone() ?? true }).ConfigureAwait(false) };
            case "AutoSourcarrPages":
                var pages = await Call(HttpMethod.Post, "/v1/chapters/" + Id(input["chapterId"]) + "/pages").ConfigureAwait(false) as JsonObject;
                return new() { ["fetchChapterPages"] = new JsonObject { ["pages"] = pages?["pages"]?.DeepClone() } };
            case "AutoSourcarrLibrary":
            {
                var first = SourceResponseContract.Identity(variables?["first"]) ?? "500";
                var after = SourceResponseContract.Identity(variables?["after"]);
                var page = await Call(HttpMethod.Get, "/v1/library?first=" + first + (string.IsNullOrEmpty(after) ? "" : "&after=" + Uri.EscapeDataString(after))).ConfigureAwait(false);
                return new() { ["mangas"] = page };
            }
            case "AutoSourcarrSetLibrary":
                var manga = await Call(HttpMethod.Put, "/v1/mangas/" + Id(input["id"]) + "/library",
                    new JsonObject { ["inLibrary"] = (input["patch"] as JsonObject)?["inLibrary"]?.DeepClone() }).ConfigureAwait(false);
                return new() { ["updateManga"] = new JsonObject { ["manga"] = manga } };
            case "AutoSourcarrSourcePreferences":
            {
                var id = Id(variables?["id"]);
                var source = await Call(HttpMethod.Get, "/v1/sources/" + Uri.EscapeDataString(id)).ConfigureAwait(false) as JsonObject ?? throw new SuwayomiException("Unknown source");
                var prefs = await Call(HttpMethod.Get, "/v1/sources/" + Uri.EscapeDataString(id) + "/preferences").ConfigureAwait(false) as JsonArray ?? new JsonArray();
                return new() { ["source"] = new JsonObject { ["id"] = source["id"]?.DeepClone(), ["name"] = source["name"]?.DeepClone(),
                    ["isConfigurable"] = source["isConfigurable"]?.DeepClone(), ["preferences"] = HostPreferences(prefs) } };
            }
            case "AutoSourcarrSourcePreference":
            {
                var id = Id(input["source"]);
                var change = input["change"] as JsonObject ?? throw new SuwayomiException("Missing preference change");
                var value = change.Where(pair => pair.Key.EndsWith("State", StringComparison.Ordinal)).Select(pair => pair.Value).FirstOrDefault();
                var source = await Call(HttpMethod.Get, "/v1/sources/" + Uri.EscapeDataString(id)).ConfigureAwait(false) as JsonObject ?? throw new SuwayomiException("Unknown source");
                var prefs = await Call(HttpMethod.Put, "/v1/sources/" + Uri.EscapeDataString(id) + "/preferences",
                    new JsonObject { ["position"] = change["position"]?.DeepClone(), ["value"] = value?.DeepClone() }).ConfigureAwait(false) as JsonArray ?? new JsonArray();
                return new() { ["updateSourcePreference"] = new JsonObject {
                    ["source"] = new JsonObject { ["id"] = source["id"]?.DeepClone(), ["name"] = source["name"]?.DeepClone(), ["isConfigurable"] = source["isConfigurable"]?.DeepClone() },
                    ["preferences"] = HostPreferences(prefs) } };
            }
            case "ReimaginedEngineContract":
            {
                // The operations this client maps onto the host's REST API. Chapter downloads stay with the main library.
                var fields = new JsonArray(new[] { "fetchExtensions", "fetchMangaAndChapters", "fetchChapterPages", "fetchSourceManga",
                        "updateExtension", "updateExtensions", "updateManga", "updateSourcePreference" }
                    .Select(item => (JsonNode?)new JsonObject { ["name"] = item, ["args"] = new JsonArray(new JsonObject { ["name"] = "input" }) }).ToArray());
                return new() { ["__schema"] = new JsonObject { ["mutationType"] = new JsonObject { ["fields"] = fields },
                    ["subscriptionType"] = new JsonObject { ["fields"] = new JsonArray() } } };
            }
            default:
                throw new SuwayomiException($"The source host does not offer the operation {(name.Length == 0 ? "(unnamed)" : name)}.");
        }
    }

    /// <summary>Host preference rows in the GraphQL shape SourcePreferenceContract reads (typename, aliased value/default).</summary>
    private static JsonArray HostPreferences(JsonArray rows)
    {
        var result = new JsonArray();
        foreach (var row in rows.OfType<JsonObject>())
        {
            var type = SourceResponseContract.Text(row["type"]);
            var (typename, kind) = type switch
            {
                "SwitchPreferenceCompat" or "SwitchPreference" => ("SwitchPreference", "switch"),
                "CheckBoxPreference" => ("CheckBoxPreference", "checkbox"),
                "EditTextPreference" => ("EditTextPreference", "text"),
                "ListPreference" => ("ListPreference", "list"),
                "MultiSelectListPreference" => ("MultiSelectListPreference", "multiselect"),
                _ => (type, "unsupported"),
            };
            var item = new JsonObject { ["__typename"] = typename, ["key"] = row["key"]?.DeepClone(), ["title"] = row["title"]?.DeepClone(),
                ["summary"] = row["summary"]?.DeepClone(), ["visible"] = row["visible"]?.DeepClone(), ["enabled"] = row["enabled"]?.DeepClone() };
            if (kind != "unsupported")
            {
                item[kind + "_value"] = row["currentValue"]?.DeepClone();
                item[kind + "_default"] = row["defaultValue"]?.DeepClone();
            }
            if (row["entries"] is JsonArray entries) item["entries"] = entries.DeepClone();
            if (row["entryValues"] is JsonArray values) item["entryValues"] = values.DeepClone();
            if (kind is "text" or "multiselect") { item["dialogTitle"] = row["dialogTitle"]?.DeepClone(); item["dialogMessage"] = row["dialogMessage"]?.DeepClone(); }
            result.Add(item);
        }
        return result;
    }
}
