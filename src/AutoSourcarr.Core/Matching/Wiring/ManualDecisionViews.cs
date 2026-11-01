using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Matching.Duplicates;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Matching.Manual;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Matching.Wiring;

/// <summary>What match-on-ingest (8A) reads from the manual decisions of 8B.</summary>
public sealed class ManualDecisionsView(ManualDecisions manual) : IManualDecisionsView
{
    private readonly ManualDecisions _manual = manual ?? throw new ArgumentNullException(nameof(manual));

    public long? ManualOwner(string sourceId, long sourceMangaId) => _manual.ManualOwner(sourceId, sourceMangaId);
    public bool IsSeparated(long entryId, string sourceId, long sourceMangaId) => _manual.IsSeparatedFrom(entryId, sourceId, sourceMangaId);
    public bool IsRejected(long entryId, string sourceId, long sourceMangaId) => _manual.IsRecordRejected(entryId, sourceId, sourceMangaId);
}

/// <summary>
/// What duplicate review (8C) reads from the manual decisions. Reviews and separated records come from ManualDecisions,
/// which compares canonical ids on both sides (a rule survives merges of its target entry). Manual group membership and
/// the reviewed-group persistence keep 8C's database implementation: ManualDecisions has no group-overlap query, and
/// its PersistDuplicateReviewManualGroup logs a second decision (and needs an actor) while DuplicateMatching already
/// logs the reviewed merge with the caller's actor.
/// </summary>
public sealed class ManualDuplicateView(SqliteDatabase database, ManualDecisions manual) : IDuplicateManualView
{
    private readonly ManualDecisions _manual = manual ?? throw new ArgumentNullException(nameof(manual));
    private readonly CatalogueDuplicateManualView _tables = new(database);

    public bool ReviewedDifferent(long left, long right) => _manual.IsRejected(left, right);
    public bool ManuallyGrouped(long left, long right) => _tables.ManuallyGrouped(left, right);
    public bool SeparatedRecord(long entryId, string sourceId, long sourceMangaId) => _manual.IsSeparatedFrom(entryId, sourceId, sourceMangaId);
    public JsonObject PersistReviewedGroup(long canonicalEntryId, IReadOnlyList<NativeSourceRecord> records) => _tables.PersistReviewedGroup(canonicalEntryId, records);
}
