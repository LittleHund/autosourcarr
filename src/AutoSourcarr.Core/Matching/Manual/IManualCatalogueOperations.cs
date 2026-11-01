using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Structure;

namespace AutoSourcarr.Core.Matching.Manual;

/// <summary>
/// The catalogue writes manual decisions and catalogue maintenance need but do not own (0.4.5 service.py helpers that
/// belong to other phase-8 areas). ManualDecisions and CatalogueMaintenance take it as an optional dependency: without
/// it the purely durable parts (bindings, groups, separate records, reviews, titles, resets, the log) still work, and
/// operations that must create or move catalogue rows answer "not available".
/// </summary>
/// <remarks>
/// Implemented by the runtime wiring over: EntryIngest (CreateEntry = _create_entry + _seed_identity_from_source,
/// UpsertPrimary = _upsert_match, UpsertVariant = _upsert_source_variant, IngestChapters = _ingest_chapters,
/// FetchMangaAsync = source_host.fetch_manga_and_chapters), DuplicateReview/AutomaticMerge (MergeEntries =
/// merge_entries, SplitRecords = _split_duplicate_records), CatalogueTitleConsensus (PurgeSourceRecordTitles,
/// SyncPreferredTitle, ApplySourceTitleOverride), CatalogueMetadata (ChooseMetadata), CatalogueSplit
/// (ReconcileChapters = _reconcile_split_chapters) and the caches (Invalidate).
/// </remarks>
public interface IManualCatalogueOperations
{
    /// <summary>Creates a new entry for one source record (no match row yet) and returns its id.</summary>
    long CreateEntry(string sourceId, JsonObject manga);
    /// <summary>Writes the entry's primary record for this source (moving the exact record away from other entries).</summary>
    void UpsertPrimary(long entryId, string sourceId, JsonObject manga, double confidence, string matchType, bool locked);
    /// <summary>Writes an additional record of this source on the entry.</summary>
    void UpsertVariant(long entryId, string sourceId, JsonObject manga, double confidence, string matchType, bool locked);
    /// <summary>Merges <paramref name="duplicate"/> into <paramref name="target"/>; returns the surviving id.</summary>
    long MergeEntries(long target, long duplicate, string reason);
    /// <summary>Moves the records to a new entry; returns the new entry id (0 when nothing was split).</summary>
    long SplitRecords(long entryId, IReadOnlyList<NativeSourceRecord> records);
    /// <summary>Drops titles owned by detached records (and a preferred-title override that came from them).</summary>
    void PurgeSourceRecordTitles(long entryId, string sourceId, IReadOnlyCollection<long> sourceMangaIds);
    /// <summary>Returns an automatic main title to the bound-record consensus.</summary>
    void SyncPreferredTitle(long entryId);
    /// <summary>Re-applies a persistent per-record title override after a record was attached again.</summary>
    void ApplySourceTitleOverride(long entryId, string sourceId, long sourceMangaId);
    /// <summary>Rebuilds the unified metadata of an entry from its records.</summary>
    void ChooseMetadata(long entryId);
    /// <summary>Re-checks chapter ownership after records were attached or detached.</summary>
    void ReconcileChapters(long entryId);
    /// <summary>Stores chapters of a just attached record.</summary>
    void IngestChapters(long entryId, string sourceId, long sourceMangaId, JsonArray chapters);
    /// <summary>Source manga details (and chapters when asked) from the source engine: {"manga": {...}, "chapters": [...]}.</summary>
    Task<JsonObject?> FetchMangaAsync(long sourceMangaId, bool withChapters, CancellationToken cancellationToken);
    /// <summary>Drops in-memory caches that depend on catalogue structure (duplicate snapshot, search, subbed, covers of these keys).</summary>
    void Invalidate(IReadOnlyCollection<string> coverKeys);
}
