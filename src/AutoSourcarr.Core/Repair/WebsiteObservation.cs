using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Core.Repair;

public static class WebsiteObservationLimits
{
    public const int MaximumBody = 128 * 1024, MaximumRequests = 16, MaximumSites = 8;
    public static bool Redirect(int status) => status is 301 or 302 or 303 or 307 or 308;
}

public sealed record WebsiteObservationRequest(Uri Url, int MaximumBytes, TimeSpan Timeout, string? Accept = null);
public sealed record WebsiteTransportResponse(int StatusCode, string Location, string ContentType, string ContentEncoding, ReadOnlyMemory<byte> Body);
public sealed record WebsiteObservationResponse(int StatusCode, string Location, string ContentType, ReadOnlyMemory<byte> Body, bool Readable);

/// <summary>A single raw response inside the qualified repair boundary. No ambient host-network fallback.
/// Implementations must check isolation, public DNS/IP pinning, TLS and standard ports on every request,
/// send only the fixed agent/identity-encoding and optional GitHub SHA headers, use no credentials/cookies,
/// disable redirects/decompression, and bound raw streaming before allocation. Encoded/redirect bodies are not read.</summary>
public interface IWebsiteObservationTransport
{
    void RequireIsolation();
    Task<WebsiteTransportResponse> ObserveAsync(WebsiteObservationRequest request, CancellationToken cancellationToken = default);
}

public interface IWebsiteObservationClient
{
    Task<WebsiteObservationResponse> ResponseAsync(string url, string? accept = null, CancellationToken cancellationToken = default);
    Task<JsonObject> OneAsync(string value, string? allowedOrigin = null, CancellationToken cancellationToken = default);
    Task<JsonArray> CheckAsync(IEnumerable<JsonObject> sources, CancellationToken cancellationToken = default);
}

/// <summary>Released evidence JSON and small wire helpers. Object insertion order and Python escaping remain stable.</summary>
public static class WebsiteEvidence
{
    public static string Text(JsonObject row, string key) => row[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    public static string SourceId(JsonObject row) => row["id"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : row["id"]?.ToJsonString() ?? "";
    public static bool Flag(JsonObject row, string key) => row[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    public static IEnumerable<JsonObject> Rows(JsonObject row, string key) => row[key] is JsonArray array ? array.OfType<JsonObject>() : [];
    public static bool HasSource(JsonObject row, string id) => row["source_ids"] is JsonArray ids && ids.Any(node => node is JsonValue value && value.TryGetValue<string>(out var text) && text == id);
    public static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    public static string Json(JsonNode? node, bool spaces = false)
    {
        var text = new StringBuilder(); Write(text, node, spaces); return text.ToString();
    }
    private static void Write(StringBuilder output, JsonNode? node, bool spaces)
    {
        var comma = spaces ? ", " : ","; var colon = spaces ? ": " : ":";
        if (node is JsonObject obj)
        {
            output.Append('{'); var first = true;
            foreach (var (name, value) in obj) { if (!first) output.Append(comma); first = false; output.Append(CanonicalJson.String(name)).Append(colon); Write(output, value, spaces); }
            output.Append('}');
        }
        else if (node is JsonArray array)
        {
            output.Append('['); for (var index = 0; index < array.Count; index++) { if (index != 0) output.Append(comma); Write(output, array[index], spaces); } output.Append(']');
        }
        else output.Append(CanonicalJson.Text(node));
    }
}
