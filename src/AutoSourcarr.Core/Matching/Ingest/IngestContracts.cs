using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Matching.Ingest;

/// <summary>
/// What match-on-ingest must respect from manual decisions (implemented by area 8B's ManualDecisions; the
/// <see cref="CatalogueManualDecisionsView"/> fallback reads the 0.4.4 tables directly). Ids are canonical entry ids.
/// </summary>
public interface IManualDecisionsView
{
    /// <summary>The entry the user bound this exact record to (manual match / manual group), or null.</summary>
    long? ManualOwner(string sourceId, long sourceMangaId);
    /// <summary>The user kept this record separate from this entry ("Separate entry").</summary>
    bool IsSeparated(long entryId, string sourceId, long sourceMangaId);
    /// <summary>The user rejected attaching this record to this entry (a rejected match); automation must not attach it.</summary>
    bool IsRejected(long entryId, string sourceId, long sourceMangaId);
}

/// <summary>
/// Follow-up work around an ingest write that belongs to other areas (tags, colour profile, media mode and mixed-entry
/// split, metadata choice, hydration, identity resolution, cache invalidation). Every member is optional: the ingest
/// itself is complete without them.
/// </summary>
public interface IEntryIngestFollowUps
{
    /// <summary>After a record row was written (sync record tags, recompute colour profile / media mode, split a mixed entry).</summary>
    void RecordWritten(long entryId, string sourceId, long sourceMangaId, JsonObject manga);
    /// <summary>_choose_metadata.</summary>
    void ChooseMetadata(long entryId);
    /// <summary>schedule_source_metadata_hydration for sparse payloads.</summary>
    void ScheduleHydration(string sourceId, long sourceMangaId);
    /// <summary>schedule_entry_identity (metadata identity on ingest).</summary>
    void ScheduleIdentity(long entryId);
    /// <summary>Duplicate snapshot, discovery and cover caches.</summary>
    void Invalidate(long entryId);
}

/// <summary>No follow-ups (tests, or hosts that do not run the other areas yet).</summary>
public sealed class NoEntryIngestFollowUps : IEntryIngestFollowUps
{
    public static readonly NoEntryIngestFollowUps Instance = new();
    public void RecordWritten(long entryId, string sourceId, long sourceMangaId, JsonObject manga) { }
    public void ChooseMetadata(long entryId) { }
    public void ScheduleHydration(string sourceId, long sourceMangaId) { }
    public void ScheduleIdentity(long entryId) { }
    public void Invalidate(long entryId) { }
}

/// <summary>
/// Reads manual decisions straight from the catalogue: manual_match_bindings (entry_id_hint of a bound record),
/// manual_separate_records, and locked manual rows of another entry. 0.4.4 had no per-record rejection table, so a
/// rejection is a "not_duplicate" review between the record's current separate entry and the target.
/// </summary>
public sealed class CatalogueManualDecisionsView(SqliteDatabase database) : IManualDecisionsView
{
    public long? ManualOwner(string sourceId, long sourceMangaId) => database.Read(DatabaseFile.Catalog, session =>
    {
        var row = session.Query("""
            SELECT b.entry_id_hint FROM manual_match_bindings b WHERE b.source_id=$source AND b.source_manga_id=$manga AND b.entry_id_hint IS NOT NULL
            """, ("$source", sourceId), ("$manga", sourceMangaId)).FirstOrDefault();
        return row is null ? (long?)null : Canonical(session, row.Integer("entry_id_hint"));
    });

    public bool IsSeparated(long entryId, string sourceId, long sourceMangaId) => database.Read(DatabaseFile.Catalog, session =>
        session.Scalar("SELECT 1 FROM manual_separate_records WHERE target_entry_id=$entry AND source_id=$source AND source_manga_id=$manga",
            ("$entry", entryId), ("$source", sourceId), ("$manga", sourceMangaId)) is not null);

    public bool IsRejected(long entryId, string sourceId, long sourceMangaId) => database.Read(DatabaseFile.Catalog, session =>
    {
        var owner = session.Scalar("""
            SELECT entry_id FROM source_matches WHERE source_id=$source AND source_manga_id=$manga AND active=1
            UNION ALL SELECT entry_id FROM source_variants WHERE source_id=$source AND source_manga_id=$manga AND active=1 LIMIT 1
            """, ("$source", sourceId), ("$manga", sourceMangaId));
        if (owner is not long current || current == entryId) return false;
        var (left, right) = current < entryId ? (current, entryId) : (entryId, current);
        return session.Scalar("SELECT 1 FROM duplicate_reviews WHERE left_entry_id=$left AND right_entry_id=$right AND decision='not_duplicate'",
            ("$left", left), ("$right", right)) is not null;
    });

    private static long Canonical(DatabaseSession session, long entryId) => Catalogue.Titles.CatalogueTitles.Canonical(session, entryId);
}
