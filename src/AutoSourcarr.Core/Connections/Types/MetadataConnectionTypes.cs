using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Connections.Types;

/// <summary>
/// A metadata provider as a connection (CON-1): an optional base-URL override (tests, self-hosted mirrors) and an optional
/// token. Test = one cheap read (search "Berserk", limit 1) reporting latency and the remaining rate limit.
/// </summary>
public abstract class MetadataConnectionType : IConnectionType
{
    public const string BaseUrlField = "base_url";
    public const string TokenField = "api_token";
    public const string TestTitle = "Berserk";

    public abstract string Type { get; }
    public abstract string Label { get; }
    public abstract string Purpose { get; }
    public bool Multiple => false;
    protected abstract Uri DefaultBaseUri { get; }
    protected abstract string TokenHelp { get; }

    public IReadOnlyList<ConnectionField> Fields =>
    [
        new(BaseUrlField, "Base URL", ConnectionFieldKind.Url, false, DefaultBaseUri.AbsoluteUri, "Leave as is unless you use a mirror or a test server."),
        new(TokenField, "API token (optional)", ConnectionFieldKind.Secret, false, null, TokenHelp),
    ];

    /// <summary>A client for this connection; the handler stays the caller's.</summary>
    public IMetadataProviderClient CreateClient(ConnectionConfig connection, HttpMessageHandler http, MetadataClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var configured = connection.Value(BaseUrlField, DefaultBaseUri.AbsoluteUri);
        if (!ConnectionHttp.TryHttpUri(configured, out var baseUri, out var error)) throw new ArgumentException(error);
        options = (options ?? new MetadataClientOptions()) with { BaseUri = baseUri, Token = connection.Value(TokenField).Trim() };
        return Create(http, options);
    }

    protected abstract IMetadataProviderClient Create(HttpMessageHandler http, MetadataClientOptions options);

    public Task<ConnectionTestResult> TestAsync(ConnectionConfig connection, HttpMessageHandler http, CancellationToken cancellationToken)
        => TestAsync(connection, http, null, cancellationToken);

    /// <summary>The Test with explicit client options (tests pass their own limiter).</summary>
    public Task<ConnectionTestResult> TestAsync(ConnectionConfig connection, HttpMessageHandler http, MetadataClientOptions? options, CancellationToken cancellationToken)
        => ConnectionTesting.RunAsync(Label, async watch =>
        {
            // A Test never queues behind a long rate-limit wait: it reports the limit instead.
            using var client = CreateClient(connection, http, (options ?? new MetadataClientOptions()) with { MaxRateWait = TimeSpan.FromSeconds(5), MaxRetries = 0 });
            var results = await client.SearchAsync(TestTitle, 1, cancellationToken).ConfigureAwait(false);
            var details = new JsonObject
            {
                ["latency_ms"] = watch.ElapsedMilliseconds,
                ["results"] = results.Count,
                ["rate_limit"] = client.RateStatus.ToJson(),
            };
            if (results.Count == 0)
                return ConnectionTestResult.Fail(Label + " answered, but a search for \"" + TestTitle + "\" found nothing; check the base URL.", details);
            details["sample"] = new JsonObject { ["id"] = results[0].ExternalId, ["title"] = results[0].Title };
            var remaining = client.RateStatus.Remaining is { } left ? ", " + left.ToString(System.Globalization.CultureInfo.InvariantCulture) + " requests left this window" : "";
            return ConnectionTestResult.Pass(Label + " answered in " + watch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms" + remaining + ".", details);
        }, cancellationToken);
}

public sealed class AniListConnectionType : MetadataConnectionType
{
    public override string Type => "anilist";
    public override string Label => "AniList";
    public override string Purpose => "Looks up AniList titles, alternative names, authors and covers to identify catalogue entries (30 requests per minute).";
    protected override Uri DefaultBaseUri => AniListClient.DefaultBaseUri;
    protected override string TokenHelp => "An AniList access token; searches work without one.";
    protected override IMetadataProviderClient Create(HttpMessageHandler http, MetadataClientOptions options) => new AniListClient(http, options);
}

public sealed class MangaUpdatesConnectionType : MetadataConnectionType
{
    public override string Type => "mangaupdates";
    public override string Label => "MangaUpdates";
    public override string Purpose => "Looks up MangaUpdates series, associated names and authors to identify catalogue entries.";
    protected override Uri DefaultBaseUri => MangaUpdatesClient.DefaultBaseUri;
    protected override string TokenHelp => "A MangaUpdates session token (from account login); searches work without one.";
    protected override IMetadataProviderClient Create(HttpMessageHandler http, MetadataClientOptions options) => new MangaUpdatesClient(http, options);
}

public sealed class MangaDexConnectionType : MetadataConnectionType
{
    public override string Type => "mangadex";
    public override string Label => "MangaDex";
    public override string Purpose => "Looks up MangaDex titles, alternative names, authors, covers and cross-links to identify catalogue entries.";
    protected override Uri DefaultBaseUri => MangaDexClient.DefaultBaseUri;
    protected override string TokenHelp => "A MangaDex personal-client access token; searches work without one.";
    protected override IMetadataProviderClient Create(HttpMessageHandler http, MetadataClientOptions options) => new MangaDexClient(http, options);
}
