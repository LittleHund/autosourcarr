using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Chapters;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Duplicates;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Matching.Manual;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Core.Matching.Wiring;

/// <summary>
/// The catalogue writes manual decisions and maintenance need (<see cref="IManualCatalogueOperations"/>), over the
/// phase-8 services of the other areas: EntryIngest (create, primary and additional records), the native structure
/// (CatalogueMerge / CatalogueSplit through <see cref="IDuplicateStructure"/>), CatalogueTitles, CatalogueMetadata, the
/// native chapter writer and the source engine. Anything the host does not provide fails with
/// <see cref="ManualDecisionUnavailableException"/> (the routes answer 503) and a source failure with
/// <see cref="ManualDecisionConflictException"/> (409), never silently.
/// </summary>
public sealed class ManualCatalogueOperations : IManualCatalogueOperations
{
    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly Func<EntryIngest> _ingest;
    private readonly CatalogueTitles _titles;
    private readonly IDuplicateStructure _structure;
    private readonly CatalogueStructure _native;
    private readonly CatalogueMetadata _metadata;
    private readonly NativeChapterWriter? _chapters;
    private readonly Func<long, bool, CancellationToken, Task<JsonObject>>? _fetch;
    private readonly Action<IReadOnlyCollection<string>>? _invalidate;
    private readonly TimeProvider _clock;

    /// <param name="ingest">Late-bound because EntryIngest reads the manual decisions that use these operations.</param>
    /// <param name="fetch">source manga id, with chapters → {"manga": {...}, "chapters": [...]} (fetchMangaAndChapters).</param>
    /// <param name="invalidate">Cover keys ("entry-1", "source-2") whose caches are dropped with the structure-dependent caches.</param>
    public ManualCatalogueOperations(SqliteDatabase database, object sourceWriteGate, Func<EntryIngest> ingest, CatalogueTitles titles,
        CatalogueStructure structure, CatalogueMetadata metadata, NativeChapterWriter? chapters = null,
        Func<long, bool, CancellationToken, Task<JsonObject>>? fetch = null, Action<IReadOnlyCollection<string>>? invalidate = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate); ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(titles); ArgumentNullException.ThrowIfNull(structure); ArgumentNullException.ThrowIfNull(metadata);
        _database = database; _gate = sourceWriteGate; _ingest = ingest; _titles = titles; _native = structure; _structure = structure;
        _metadata = metadata; _chapters = chapters; _fetch = fetch; _invalidate = invalidate; _clock = clock ?? TimeProvider.System;
    }

    public long CreateEntry(string sourceId, JsonObject manga) => _ingest().CreateEntry(manga, sourceId);

    public void UpsertPrimary(long entryId, string sourceId, JsonObject manga, double confidence, string matchType, bool locked)
        => _ = _ingest().UpsertMatch(entryId, sourceId, manga, confidence, matchType, locked);

    public void UpsertVariant(long entryId, string sourceId, JsonObject manga, double confidence, string matchType, bool locked)
        => _ = _ingest().UpsertSourceVariant(entryId, sourceId, manga, confidence, matchType, locked);

    public long MergeEntries(long target, long duplicate, string reason) => _structure.MergeEntries(target, duplicate, reason);

    /// <summary>A user's split (Separate entry): the moved records stay bound and apart from the original (manual split).</summary>
    public long SplitRecords(long entryId, IReadOnlyList<NativeSourceRecord> records) => _structure.SplitRecords(entryId, records, manual: true, "split-duplicate");

    public void PurgeSourceRecordTitles(long entryId, string sourceId, IReadOnlyCollection<long> sourceMangaIds)
        => _ = _titles.PurgeSourceRecordTitles(entryId, sourceId, sourceMangaIds, dropPreferredOverride: true);

    public void SyncPreferredTitle(long entryId) => _ = _titles.SyncPreferredTitleFromBoundRecords(entryId);

    /// <summary>0.4.5 scopes a manual title to its entry; nothing is inherited through a record (CatalogueTitles answers "").</summary>
    public void ApplySourceTitleOverride(long entryId, string sourceId, long sourceMangaId)
        => _ = CatalogueTitles.ApplySourceTitleOverride(entryId, sourceId, sourceMangaId);

    public void ChooseMetadata(long entryId)
    {
        var stamp = DecisionStamp.Now(_clock);
        lock (_gate)
            _database.Write(DatabaseFile.Catalog, c =>
            {
                var id = PortableIdentityResolver.Canonical(c, entryId);
                if (c.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", id)) is not null) _metadata.ChooseMetadata(c, id, stamp);
                return 0;
            });
    }

    public void ReconcileChapters(long entryId) => _native.ReconcileSplitChapters(entryId);

    public void IngestChapters(long entryId, string sourceId, long sourceMangaId, JsonArray chapters)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        // An empty list is "no chapters were read", not "the source has none": a complete refresh would deactivate them all.
        if (chapters.Count == 0) return;
        var writer = _chapters ?? throw new ManualDecisionUnavailableException("Chapter storage is not available in this host (pending phase 10); nothing was changed.");
        try
        {
            var batch = new ChapterBatch(entryId, sourceId, sourceMangaId, chapters.OfType<JsonObject>().Select(NativeChapter.FromJson).ToArray());
            _ = writer.Write(batch);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException and not ManualDecisionConflictException)
        {
            throw new ManualDecisionConflictException("The chapters of " + sourceId + ":" + sourceMangaId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " could not be stored: " + error.Message);
        }
    }

    public async Task<JsonObject?> FetchMangaAsync(long sourceMangaId, bool withChapters, CancellationToken cancellationToken)
    {
        var fetch = _fetch ?? throw new ManualDecisionUnavailableException("The source engine is not available in this host; nothing was changed.");
        try { return await fetch(sourceMangaId, withChapters, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is SuwayomiException or SourceWorkDeferredException or HttpRequestException or TimeoutException)
        {
            throw new ManualDecisionConflictException("The source did not return this record (" + error.Message + "); nothing was changed.");
        }
    }

    public void Invalidate(IReadOnlyCollection<string> coverKeys) => _invalidate?.Invoke(coverKeys);
}
