using System.Text.Json.Nodes;
using AutoSourcarr.Core.Connections;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Catalogue.Metadata;

/// <summary>
/// The metadata provider clients identity resolution uses: for AniList, MangaUpdates and MangaDex the first enabled
/// connection of that type (phase 7 ConnectionStore), otherwise the public endpoint when the provider is listed in
/// METADATA_IDENTITY_PROVIDERS. Clients are rebuilt when the connections change; replaced clients are disposed with the
/// set (a lookup may still be using one). Each client paces itself with its provider's shared rate limiter.
/// </summary>
public sealed class MetadataProviderSet : IDisposable
{
    private static readonly MetadataConnectionType[] Types = [new AniListConnectionType(), new MangaUpdatesConnectionType(), new MangaDexConnectionType()];
    private readonly Func<string, IReadOnlyList<ConnectionConfig>> _enabled;
    private readonly Func<AutoSourcarrSettings> _settings;
    private readonly HttpMessageHandler _handler;
    private readonly Lock _gate = new();
    private readonly List<IMetadataProviderClient> _retired = [];
    private string _signature = "";
    private IReadOnlyList<IMetadataProviderClient> _clients = [];
    private bool _disposed;

    /// <param name="enabled">ConnectionStore.Enabled.</param>
    public MetadataProviderSet(Func<string, IReadOnlyList<ConnectionConfig>> enabled, Func<AutoSourcarrSettings> settings, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(enabled); ArgumentNullException.ThrowIfNull(settings);
        _enabled = enabled; _settings = settings; _handler = handler ?? ConnectionStore.DefaultHandler();
    }

    public IReadOnlyList<IMetadataProviderClient> Current()
    {
        var listed = (_settings().MetadataIdentityProviders ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var chosen = new List<(MetadataConnectionType Type, ConnectionConfig Connection)>();
        foreach (var type in Types)
        {
            var connection = _enabled(type.Type).FirstOrDefault();
            if (connection is null && listed.Contains(type.Type))
                connection = new ConnectionConfig("default-" + type.Type, type.Type, type.Label, true, new Dictionary<string, string>(StringComparer.Ordinal));
            if (connection is not null) chosen.Add((type, connection));
        }
        var signature = string.Join("|", chosen.Select(item => item.Connection.Id + "=" + new JsonObject(item.Connection.Values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))).ToJsonString()));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (signature == _signature) return _clients;
            var built = new List<IMetadataProviderClient>();
            foreach (var (type, connection) in chosen)
            {
                try { built.Add(type.CreateClient(connection, _handler, new MetadataClientOptions { IncludeAdult = _settings().MetadataIdentityIncludeAdult })); }
                catch (ArgumentException) { } // An invalid base URL: that provider is not used until the connection is fixed.
            }
            _retired.AddRange(_clients);
            _clients = built; _signature = signature;
            return _clients;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var client in _clients.Concat(_retired)) client.Dispose();
            _retired.Clear(); _clients = [];
        }
        _handler.Dispose();
    }
}

/// <summary>
/// The metadata part of match-on-ingest (IEntryIngestFollowUps): ScheduleIdentity resolves the entry's provider identity
/// in the background; ChooseMetadata and ScheduleHydration use the given actions. Every other follow-up goes to
/// <paramref name="inner"/> (tags, colour, cache invalidation of the other areas).
/// </summary>
public sealed class MetadataIngestFollowUps(MetadataIdentityResolver? identity, IEntryIngestFollowUps? inner = null) : IEntryIngestFollowUps
{
    private readonly IEntryIngestFollowUps _inner = inner ?? NoEntryIngestFollowUps.Instance;

    /// <summary>_choose_metadata for one entry (CatalogueMetadata.ChooseMetadata in its own catalogue write).</summary>
    public Action<long>? Choose { get; init; }
    /// <summary>schedule_source_metadata_hydration (NativeMetadataHydration.Schedule).</summary>
    public Action<string, long>? Hydrate { get; init; }

    public void RecordWritten(long entryId, string sourceId, long sourceMangaId, JsonObject manga) => _inner.RecordWritten(entryId, sourceId, sourceMangaId, manga);
    public void ChooseMetadata(long entryId) { if (Choose is { } choose) choose(entryId); else _inner.ChooseMetadata(entryId); }
    public void ScheduleHydration(string sourceId, long sourceMangaId) { if (Hydrate is { } hydrate) hydrate(sourceId, sourceMangaId); else _inner.ScheduleHydration(sourceId, sourceMangaId); }
    public void ScheduleIdentity(long entryId) { if (identity is not null) identity.Schedule(entryId); else _inner.ScheduleIdentity(entryId); }
    public void Invalidate(long entryId) => _inner.Invalidate(entryId);
}
