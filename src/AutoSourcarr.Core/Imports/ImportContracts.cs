using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Scoring;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Library;

namespace AutoSourcarr.Core.Imports;

/// <summary>The settings the import pipeline reads on every use.</summary>
public sealed record ImportOptions
{
    /// <summary>Automatic acceptance floor (AUTO_MATCH_THRESHOLD, never below 0.94).</summary>
    public double AutoMatchThreshold { get; init; } = MatchScorer.MinimumFloor;
    public bool MetadataEnabled { get; init; } = true;
    public double MetadataMinScore { get; init; } = MatchScorer.MinimumFloor;
    public int MetadataQueryLimit { get; init; } = 3;
    /// <summary>How many enabled search sources a live discovery asks (in priority order).</summary>
    public int LiveSources { get; init; } = 8;

    public double Floor => Math.Max(MatchScorer.MinimumFloor, AutoMatchThreshold);

    public static ImportOptions From(AutoSourcarrSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new()
        {
            AutoMatchThreshold = settings.AutoMatchThreshold, MetadataEnabled = settings.MetadataIdentityEnabled,
            MetadataMinScore = settings.MetadataIdentityMinScore, MetadataQueryLimit = Math.Max(1, settings.MetadataIdentityQueryLimit),
        };
    }
}

/// <summary>What adding or removing a main-library manga did: "added", "existing", "superseded", "removed" or "preserved".</summary>
public sealed record ImportLibraryOutcome(string State, string Reason);

/// <summary>The main Suwayomi library (subscriptions) as the import pipeline uses it.</summary>
public interface IImportLibrary
{
    /// <summary>The canonical entry ids already in the main library. Throws when the library cannot be read.</summary>
    Task<IReadOnlySet<long>> SubscribedAsync(CancellationToken cancellationToken);
    /// <summary>Adds the entry (materialized through the AutoSourcarr source) unless already there; never undone when superseded.</summary>
    Task<ImportLibraryOutcome> AddAsync(long entryId, IReadOnlyList<string> titles, Func<bool> superseded, CancellationToken cancellationToken);
    /// <summary>Removes an entry an import added (a rejected match).</summary>
    Task<ImportLibraryOutcome> RemoveAsync(long entryId, IReadOnlyList<string> titles, Func<bool> superseded, CancellationToken cancellationToken);
}

/// <summary>Live source access: one search page and one manga's details (source runtime).</summary>
public interface IImportSourceSearch
{
    Task<IReadOnlyList<JsonObject>> SearchAsync(string sourceId, string query, CancellationToken cancellationToken);
    /// <summary>The full manga record; null when unavailable.</summary>
    Task<JsonObject?> DetailsAsync(long mangaId, CancellationToken cancellationToken);
}

/// <summary>External metadata catalogues (MangaDex, AniList, MangaUpdates) used as identity evidence for unresolved imports.</summary>
public interface IImportMetadataSearch
{
    /// <summary>Enabled provider keys in priority order.</summary>
    IReadOnlyList<string> Providers { get; }
    Task<IReadOnlyList<MetadataCandidate>> SearchAsync(string provider, string query, CancellationToken cancellationToken);
}

/// <summary>
/// Writes an accepted provider record onto an entry as an identity link (_upsert_identity_link + crossrefs); implemented by
/// the metadata area. The candidate is MetadataCandidate.ToJson(). Answers whether a link was written.
/// </summary>
public interface IImportIdentitySink
{
    bool Apply(long entryId, JsonObject candidate, double confidence, string reason);
}

/// <summary>The main library through SuwayomiLibrary (_add_import_entry_to_main_library / _remove_import_added_entry_from_main_library).</summary>
public sealed class SuwayomiImportLibrary(SuwayomiLibrary library, Func<SuwayomiClient?> main) : IImportLibrary
{
    public async Task<IReadOnlySet<long>> SubscribedAsync(CancellationToken cancellationToken)
    {
        var payload = await library.SubbedEntryIdsAsync(force: true, cancellationToken).ConfigureAwait(false);
        if (!(payload["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var fine) && fine))
            throw new InvalidOperationException(payload["error"] is JsonValue error && error.TryGetValue<string>(out var text) && text.Length > 0 ? text : "Unable to read main Suwayomi library");
        return Ids(payload);
    }

    public async Task<ImportLibraryOutcome> AddAsync(long entryId, IReadOnlyList<string> titles, Func<bool> superseded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(superseded);
        if (superseded()) return Superseded;
        if (Ids(library.CachedSubbedPayload()).Contains(entryId)) return new("existing", "Already in main Suwayomi library");
        var sourceId = await library.MainAutoSourcarrSourceIdAsync(cancellationToken).ConfigureAwait(false);
        var manga = await library.MainMangaForEntryAsync(entryId, sourceId, titles, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AutoSourcarr entry could not be materialized in main Suwayomi");
        if (!(manga["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var mangaId)))
            throw new InvalidOperationException("Main Suwayomi answered a manga without an id");
        if (manga["inLibrary"] is JsonValue member && member.TryGetValue<bool>(out var inLibrary) && inLibrary)
            return new("existing", "Already in main Suwayomi library");
        if (superseded()) return Superseded;
        var confirmed = await library.EnsureMainLibraryAsync(mangaId, superseded, cancellationToken).ConfigureAwait(false);
        if (confirmed["superseded"] is JsonValue flag && flag.TryGetValue<bool>(out var wasSuperseded) && wasSuperseded) return Superseded;
        return new("added", "Added to main Suwayomi library");
    }

    public async Task<ImportLibraryOutcome> RemoveAsync(long entryId, IReadOnlyList<string> titles, Func<bool> superseded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(superseded);
        if (main() is not { } client) return new("preserved", "MAIN_SUWAYOMI_URL is not configured");
        var sourceId = await library.MainAutoSourcarrSourceIdAsync(cancellationToken).ConfigureAwait(false);
        var manga = await library.MainMangaForEntryAsync(entryId, sourceId, titles, cancellationToken).ConfigureAwait(false);
        if (manga is null || !(manga["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var mangaId)))
            return new("preserved", "Rejected entry is no longer materialized in main Suwayomi");
        if (superseded()) return new("preserved", "A newer import decision superseded this action");
        var result = await client.SetMangaInLibraryAsync(mangaId, false, cancellationToken).ConfigureAwait(false);
        library.Invalidate();
        var still = result["inLibrary"] is JsonValue value && value.TryGetValue<bool>(out var member) && member;
        return still ? new("preserved", "Main Suwayomi kept the manga in its library") : new("removed", "Removed rejected import from main Suwayomi library");
    }

    private static readonly ImportLibraryOutcome Superseded = new("superseded", "A newer import decision superseded this action");

    private static HashSet<long> Ids(JsonObject payload)
    {
        var ids = new HashSet<long>();
        if (payload["entry_ids"] is JsonArray values)
            foreach (var value in values)
                if (value is JsonValue number && number.TryGetValue<long>(out var id)) ids.Add(id);
        return ids;
    }
}

/// <summary>Live source access through the source runtime client delegates (FetchSourceMangaAsync / FetchMangaAndChaptersAsync).</summary>
public sealed class DelegateImportSourceSearch(
    Func<string, string, int, string, CancellationToken, Task<JsonObject>> fetchSourceManga,
    Func<long, bool, bool, CancellationToken, Task<JsonObject>> fetchMangaAndChapters) : IImportSourceSearch
{
    public async Task<IReadOnlyList<JsonObject>> SearchAsync(string sourceId, string query, CancellationToken cancellationToken)
    {
        var payload = await fetchSourceManga(sourceId, "SEARCH", 1, query, cancellationToken).ConfigureAwait(false);
        return payload["mangas"] is JsonArray mangas ? mangas.OfType<JsonObject>().Select(manga => (JsonObject)manga.DeepClone()).ToList() : [];
    }

    public async Task<JsonObject?> DetailsAsync(long mangaId, CancellationToken cancellationToken)
    {
        var payload = await fetchMangaAndChapters(mangaId, true, false, cancellationToken).ConfigureAwait(false);
        return payload["manga"] is JsonObject manga ? (JsonObject)manga.DeepClone() : null;
    }
}

/// <summary>The enabled metadata provider clients (Connections) as import evidence; the list is read on every search.</summary>
public sealed class MetadataClientsImportSearch(Func<IReadOnlyList<IMetadataProviderClient>> clients, int resultsPerProvider = 6) : IImportMetadataSearch
{
    public IReadOnlyList<string> Providers => clients().Select(client => client.Provider).Distinct(StringComparer.Ordinal).ToArray();

    public async Task<IReadOnlyList<MetadataCandidate>> SearchAsync(string provider, string query, CancellationToken cancellationToken)
    {
        var client = clients().FirstOrDefault(item => item.Provider == provider)
            ?? throw new InvalidOperationException("Metadata provider " + provider + " is not enabled");
        return await client.SearchAsync(query, Math.Clamp(resultsPerProvider, 1, 20), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A job's progress sink (no-op outside the job queue).</summary>
public sealed class ImportProgress(JobContext? context)
{
    public static readonly ImportProgress None = new(null);
    public CancellationToken CancellationToken => context?.CancellationToken ?? CancellationToken.None;
    public void Report(int? current = null, int? total = null, string? message = null) => context?.Report(current, total, message);
    public void Result(string message) => context?.Result(message);
    internal static string Count(int value, string noun) => value.ToString(CultureInfo.InvariantCulture) + " " + noun + (value == 1 ? "" : "s");
}
