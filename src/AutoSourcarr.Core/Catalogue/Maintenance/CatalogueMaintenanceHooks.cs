using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;

namespace AutoSourcarr.Core.Catalogue.Maintenance;

/// <summary>
/// Work of other areas that the database resets and the startup repairs run in 0.4.5's order. Every member is optional:
/// a missing one is skipped and reported as "unavailable" in the result, never guessed.
/// </summary>
/// <remarks>
/// Implemented by the runtime wiring: covers (Imaging CoverCache.ClearAllAsync), rebuild hints (CatalogueSplit
/// capture_reset_rebuild_hints / reset_rebuild_hints_for_entries / schedule_reset_hint_replay), source preferences
/// (SourcePreferenceService capture/restore), source sync (SourceInventory), discovery (schedule_continuous_catalog_discovery,
/// discover_missing_source_matches + refresh_entry for the subscribed rebuild), titles (CatalogueTitleConsensus repairs),
/// colour/media (ColorProfile repairs, _refresh_media_variants), automatic merges (DuplicateReview _repair_unsafe_automatic_merges
/// and the strict automatic evidence of EntryIngest).
/// </remarks>
public sealed class CatalogueMaintenanceHooks
{
    // ---- resets
    public Func<CancellationToken, Task<int>>? ClearCovers { get; init; }
    /// <summary>reset_rebuild_hints_for_entries: old source records per (canonical) entry, used as rebuild hints.</summary>
    public Func<IReadOnlyList<long>, IReadOnlyDictionary<long, JsonArray>>? RebuildHintsForEntries { get; init; }
    /// <summary>capture_reset_rebuild_hints: writes the catalogue-wide hint file; answers {"records": n, "path": "..."}.</summary>
    public Func<JsonObject>? CaptureRebuildHints { get; init; }
    public Func<JsonObject>? CaptureSourcePreferences { get; init; }
    public Func<JsonObject, int>? RestoreSourcePreferences { get; init; }
    public Func<CancellationToken, Task>? SyncSources { get; init; }
    public Func<string, bool>? ScheduleHintReplay { get; init; }
    public Func<bool>? ScheduleContinuousDiscovery { get; init; }
    /// <summary>Rebuilds one subscribed entry from its hints (discover_missing_source_matches with priority, then refresh); answers its match count.</summary>
    public Func<long, JsonArray, CancellationToken, Task<long>>? RebuildSubscribedEntry { get; init; }
    /// <summary>Drops in-memory caches after a reset (alias, source, cover hints, search, duplicate snapshot, subbed, page info).</summary>
    public Action? ResetCaches { get; init; }
    /// <summary>
    /// After a reset's rebuild finished (the subscribed rebuild job, or at once when none was queued): the imports recheck
    /// their accepted rows whose entry is gone against the current rules (ImportRuns.RecheckAfterReset, IMP-9), with the
    /// reset's actor.
    /// </summary>
    public Func<DecisionActor, JsonObject>? AfterResetRebuild { get; init; }

    // ---- startup repairs (run_startup_catalog_maintenance)
    public Func<JsonObject>? RepairEntryAliasIntegrity { get; init; }
    public Func<JsonObject>? RepairSourceTitleSuffixes { get; init; }
    public Func<JsonObject>? RepairColoredDisplayTitles { get; init; }
    public Func<JsonObject>? RepairLegacyColorMarkerProfiles { get; init; }
    public Func<IReadOnlySet<long>, JsonObject>? RepairLegacyColorFalseAttachments { get; init; }
    public Func<JsonObject>? RepairUnsafeAutomaticMerges { get; init; }
    public Func<JsonObject>? RefreshMediaVariants { get; init; }
    /// <summary>
    /// _trusted_automatic_entry_evidence + _strict_automatic_title_evidence_base for one automatic record of an entry
    /// (payload, kind primary/additional): null when the record is one of the entry's trusted records, otherwise
    /// {"eligible", "title_score", "summary_similarity", "author_similarity", "distinctive_title"}.
    /// </summary>
    public Func<long, JsonObject, string, JsonObject?>? AutomaticRecordEvidence { get; init; }
}
