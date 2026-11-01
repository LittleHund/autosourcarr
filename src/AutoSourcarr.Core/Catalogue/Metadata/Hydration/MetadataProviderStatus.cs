using System.Text.Json.Nodes;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Catalogue.Metadata.Hydration;

/// <summary>
/// identity_provider_status (metadata_catalog.provider_status): every known metadata provider with its label, identity
/// scope (work or edition), priority, whether it is configured (METADATA_IDENTITY_PROVIDERS or an enabled connection) and
/// whether a client is actually in use, plus the provider's last rate-limit view. Kitsu, MyAnimeList and Comic Vine are
/// listed but have no client in this version.
/// </summary>
public static class MetadataProviderStatus
{
    private static readonly (string Key, string Label, string Scope, int Priority, bool NeedsKey)[] Specs =
    [
        ("mangadex", "MangaDex", "edition", 10, false), ("anilist", "AniList", "work", 20, false), ("mangaupdates", "MangaUpdates", "edition", 30, false),
        ("kitsu", "Kitsu", "work", 40, false), ("myanimelist", "MyAnimeList (Jikan)", "work", 50, false), ("comicvine", "Comic Vine", "edition", 60, true),
    ];

    /// <param name="clients">The clients in use (MetadataProviderSet.Current()).</param>
    public static JsonArray Status(AutoSourcarrSettings settings, IReadOnlyList<IMetadataProviderClient> clients)
    {
        ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(clients);
        var listed = (settings.MetadataIdentityProviders ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var items = new JsonArray();
        foreach (var (key, label, scope, priority, needsKey) in Specs)
        {
            var client = clients.FirstOrDefault(item => item.Provider == key);
            var configured = listed.Contains(key) || client is not null;
            var available = settings.MetadataIdentityEnabled && client is not null;
            var reason = available ? ""
                : !configured ? "disabled"
                : !settings.MetadataIdentityEnabled ? "metadata identity is off (METADATA_IDENTITY_ENABLED)"
                : needsKey && settings.ComicvineApiKey.Trim().Length == 0 ? "API key required"
                : client is null ? "not available in this version" : "";
            items.Add(new JsonObject
            {
                ["key"] = key, ["name"] = label, ["identity_scope"] = scope, ["priority"] = priority, ["configured"] = configured,
                ["available"] = available, ["reason"] = reason, ["rate"] = client?.RateStatus.ToJson(),
            });
        }
        return items;
    }
}
