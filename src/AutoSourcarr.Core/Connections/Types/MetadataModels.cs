using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Connections.Types;

/// <summary>One title of a provider record: the text, its language (may be empty) and kind (primary, alias, romanized, native).</summary>
public sealed record MetadataTitle(string Title, string Language, string Kind);

/// <summary>
/// One normalized search/get result of a read-only metadata provider (metadata_catalog.py _result): the provider's id and
/// page, every title with alias rows, year, people, cover and cross-links to other providers. <see cref="Raw"/> keeps the
/// provider's own record for later phases (identity evidence, phase 8/9).
/// </summary>
public sealed record MetadataCandidate
{
    public required string Provider { get; init; }
    public required string ExternalId { get; init; }
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public IReadOnlyList<MetadataTitle> Titles { get; init; } = [];
    public string Description { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    /// <summary>ongoing, completed, hiatus, cancelled or unknown.</summary>
    public string Status { get; init; } = "unknown";
    public IReadOnlyList<string> Authors { get; init; } = [];
    public IReadOnlyList<string> Artists { get; init; } = [];
    public IReadOnlyList<string> Genres { get; init; } = [];
    public string MediaType { get; init; } = "";
    public string Country { get; init; } = "";
    public int? Year { get; init; }
    public bool Adult { get; init; }
    /// <summary>Cross-links to other providers by key (anilist, myanimelist, mangaupdates, kitsu).</summary>
    public IReadOnlyDictionary<string, string> Links { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public JsonObject? Raw { get; init; }

    /// <summary>Alternative titles: every title except the primary one.</summary>
    public IEnumerable<string> AlternativeTitles => Titles.Select(row => row.Title).Where(title => !string.Equals(title, Title, StringComparison.OrdinalIgnoreCase));

    public JsonObject ToJson(bool includeRaw = false)
    {
        var links = new JsonObject();
        foreach (var (key, value) in Links) links[key] = value;
        var result = new JsonObject
        {
            ["provider"] = Provider, ["external_id"] = ExternalId, ["url"] = Url, ["title"] = Title,
            ["titles"] = new JsonArray(Titles.Select(row => (JsonNode)new JsonObject { ["title"] = row.Title, ["language"] = row.Language, ["type"] = row.Kind }).ToArray()),
            ["description"] = Description, ["cover_url"] = CoverUrl, ["status"] = Status,
            ["authors"] = Strings(Authors), ["artists"] = Strings(Artists), ["genres"] = Strings(Genres),
            ["media_type"] = MediaType, ["country"] = Country, ["year"] = Year, ["adult"] = Adult, ["links"] = links,
        };
        if (includeRaw && Raw is not null) result["raw"] = Raw.DeepClone();
        return result;
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
}

/// <summary>A typed metadata provider client (AniList, MangaUpdates, MangaDex): title search and get-by-id, both read-only.</summary>
public interface IMetadataProviderClient : IDisposable
{
    /// <summary>Provider key as stored in identity_links.provider: "anilist", "mangaupdates", "mangadex".</summary>
    string Provider { get; }
    Task<IReadOnlyList<MetadataCandidate>> SearchAsync(string title, int limit = 6, CancellationToken cancellationToken = default);
    /// <summary>The record with this provider id; null when the provider does not know it.</summary>
    Task<MetadataCandidate?> GetAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>What the last response said about the provider's rate limit (and the local limiter's view).</summary>
    MetadataRateStatus RateStatus { get; }
}

/// <summary>Rate-limit view: the provider's last X-RateLimit-Limit/Remaining headers and the local limiter's free tokens.</summary>
public sealed record MetadataRateStatus(int? Limit, int? Remaining, int? LocalAvailable, int? LocalCapacity)
{
    public JsonObject ToJson() => new()
    {
        ["limit"] = Limit, ["remaining"] = Remaining, ["local_available"] = LocalAvailable, ["local_capacity"] = LocalCapacity,
    };
}

/// <summary>Options shared by the metadata clients. <see cref="BaseUri"/> overrides the public endpoint (tests, mirrors).</summary>
public sealed record MetadataClientOptions
{
    public Uri? BaseUri { get; init; }
    /// <summary>Optional bearer token (AniList OAuth access token, MangaDex session token, MangaUpdates session token).</summary>
    public string Token { get; init; } = "";
    /// <summary>Per-request timeout; the caller's token still cancels earlier.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Longest wait for a rate-limit slot or a Retry-After before failing clearly instead of waiting.</summary>
    public TimeSpan MaxRateWait { get; init; } = TimeSpan.FromSeconds(65);
    /// <summary>How many times a 429/503 answer is retried after its Retry-After.</summary>
    public int MaxRetries { get; init; } = 2;
    /// <summary>Null uses the per-process shared limiter of the provider (per host).</summary>
    public TokenBucketLimiter? Limiter { get; init; }
    public bool IncludeAdult { get; init; } = true;
    public string UserAgent { get; init; } = MetadataText.DefaultUserAgent;
}

/// <summary>A provider answered with an error, an unexpected body or a status the client does not accept.</summary>
public class MetadataProviderException : Exception
{
    public MetadataProviderException() { }
    public MetadataProviderException(string message) : base(message) { }
    public MetadataProviderException(string message, Exception inner) : base(message, inner) { }
    public MetadataProviderException(string provider, string message, HttpStatusCode? status = null, Exception? inner = null)
        : base(provider + ": " + message, inner) { Provider = provider; StatusCode = status; }
    public string Provider { get; } = "";
    public HttpStatusCode? StatusCode { get; }
}

/// <summary>The provider's rate limit (Retry-After or the local limiter) would need a longer wait than allowed.</summary>
public sealed class MetadataRateLimitedException : MetadataProviderException
{
    public MetadataRateLimitedException() { }
    public MetadataRateLimitedException(string message) : base(message) { }
    public MetadataRateLimitedException(string message, Exception inner) : base(message, inner) { }
    public MetadataRateLimitedException(string provider, TimeSpan retryAfter)
        : base(provider, "rate limited; retry in " + Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " s",
            HttpStatusCode.TooManyRequests) => RetryAfter = retryAfter;
    public TimeSpan RetryAfter { get; }
}

/// <summary>Text helpers mirroring metadata_catalog.py (_plain, _uniq, _title_rows, _status).</summary>
public static partial class MetadataText
{
    public const string DefaultUserAgent = "AutoSourcarr (metadata identity; self-hosted; +https://github.com/LittleHund/autosourcarr)";

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    public static string Plain(string? value)
    {
        var text = WebUtility.HtmlDecode(value ?? "");
        text = Tags().Replace(text, " ");
        return Spaces().Replace(text, " ").Trim();
    }

    public static string FirstText(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = Plain(value);
            if (text.Length > 0) return text;
        }
        return "";
    }

    public static IReadOnlyList<string> Unique(IEnumerable<string?> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var text = Plain(value);
            if (text.Length > 0 && seen.Add(text)) result.Add(text);
        }
        return result;
    }

    public static IReadOnlyList<MetadataTitle> TitleRows(string primary, IEnumerable<(string? Title, string Language, string Kind)> values)
    {
        var rows = new List<MetadataTitle>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value, string language, string kind)
        {
            var text = Plain(value);
            if (text.Length > 0 && seen.Add(text)) rows.Add(new MetadataTitle(text, language, kind));
        }
        Add(primary, "", "primary");
        foreach (var (title, language, kind) in values) Add(title, language, kind);
        return rows;
    }

    public static string Status(string? value)
    {
        var raw = (value ?? "").Trim().ToLowerInvariant();
        return raw switch
        {
            "releasing" or "publishing" or "current" or "ongoing" => "ongoing",
            "finished" or "completed" or "complete" => "completed",
            "hiatus" or "on_hiatus" or "on hiatus" => "hiatus",
            "cancelled" or "canceled" or "discontinued" => "cancelled",
            _ => "unknown",
        };
    }

    /// <summary>A year from a number or a text starting with four digits ("1989", "1989-08-25").</summary>
    public static int? Year(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var number)) return number is > 0 and < 10000 ? number : null;
            if (value.TryGetValue<long>(out var wide)) return wide is > 0 and < 10000 ? (int)wide : null;
            if (value.TryGetValue<string>(out var text) && text.Length >= 4
                && int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0) return parsed;
        }
        return null;
    }
}

/// <summary>Tolerant readers for provider JSON.</summary>
internal static class JsonRead
{
    public static string Text(JsonNode? node)
    {
        if (node is not JsonValue value) return "";
        if (value.TryGetValue<string>(out var text)) return text;
        return value.GetValueKind() is System.Text.Json.JsonValueKind.Number ? value.ToJsonString() : "";
    }

    public static bool Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    public static int? Int(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<long>(out var wide) && wide is >= int.MinValue and <= int.MaxValue) return (int)wide;
        if (value.TryGetValue<double>(out var real) && real is >= int.MinValue and <= int.MaxValue && Math.Abs(real % 1) < double.Epsilon) return (int)real;
        return value.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    public static long? Long(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var number)) return number;
        if (value.TryGetValue<double>(out var real) && Math.Abs(real % 1) < double.Epsilon && real is >= long.MinValue and <= long.MaxValue) return (long)real;
        return value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    public static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];

    public static IEnumerable<string> Strings(JsonNode? node) => node is JsonArray array ? array.Select(Text).Where(text => text.Length > 0) : [];
}
