using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Browse;
using AutoSourcarr.Core.Catalogue.Chapters;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Catalogue.Records;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Imaging;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Sources;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api;

/// <summary>The app's reader/cache/profile/display lifetime. These services share
/// actual storage and one catalogue write gate; no feed or native proof is invented.</summary>
internal sealed class CatalogueRuntime : IAsyncDisposable
{
    private readonly object _closeGate = new();
    private CoverCache? _covers;
    private ReaderPageService? _pages;
    private ReaderRepository? _repository;
    private ReaderCatalogue? _catalogue;
    private ServingProfileRepository? _profiles;
    private DisplayMetadata? _display;
    private CatalogueMetadata? _nativeMetadata;
    private CatalogueStructure? _structure;
    private NativeChapterWriter? _chapters;
    private NativeMetadataHydration? _hydration;
    private Task? _close;

    private CatalogueRuntime() { }
    internal CoverCache Covers => _covers ?? throw new InvalidOperationException("Catalogue cache is not initialized.");
    internal CatalogueMetadata NativeMetadata => _nativeMetadata ?? throw new InvalidOperationException("Native metadata is not initialized.");
    internal CatalogueStructure Structure => _structure ?? throw new InvalidOperationException("Native structure is not initialized.");
    internal NativeMetadataHydration Hydration => _hydration ?? throw new InvalidOperationException("Native hydration is not initialized.");
    /// <summary>The record-wide native chapter writer (manual matches store the chapters of a just attached record).</summary>
    internal NativeChapterWriter Chapters => _chapters ?? throw new InvalidOperationException("Native chapter writer is not initialized.");
    /// <summary>Phase 10: the reader stores the chapters/serving/library services build on.</summary>
    internal ReaderRepository Repository => _repository ?? throw new InvalidOperationException("Reader repository is not initialized.");
    internal ReaderPageService Pages => _pages ?? throw new InvalidOperationException("Reader pages are not initialized.");
    internal ServingProfileRepository Profiles => _profiles ?? throw new InvalidOperationException("Serving profiles are not initialized.");
    internal IReaderCatalogueRead ReaderRead => _catalogue ?? throw new InvalidOperationException("Catalogue is not initialized.");
    internal DisplayMetadata Display => _display ?? throw new InvalidOperationException("Display metadata is not initialized.");
    /// <summary>Phase 10 reader browse (cached feeds, chapter freshness); set by the app before the endpoints are mapped.</summary>
    internal ReaderBrowse? Browse { get; set; }
    internal GalleryReconciliationResult ReconcileGallery(long entryId, CancellationToken cancellationToken = default)
        => Structure.ReconcileGallery(entryId, _chapters ?? throw new InvalidOperationException("Native chapter writer is not initialized."), cancellationToken);

    internal static async Task<CatalogueRuntime> CreateAsync(SqliteDatabase database, object sourceWriteGate,
        string dataDirectory, Func<AutoSourcarrSettings> settings, BoundedWorkScheduler io, SuwayomiClient source, TaskRegistry tasks,
        WholeTaskCoordinator lanes, SupportLogStore logs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate);
        ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(io); ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(lanes); ArgumentNullException.ThrowIfNull(logs);
        var owner = new CatalogueRuntime();
        try
        {
            await io.RunAsync(() =>
            {
                // Capture each physical construction before returning through the
                // scheduler, so cancellation still owns its eventual close.
                owner._covers = new(dataDirectory, settings, io);
                owner._repository = new(database, sourceWriteGate);
                owner._catalogue = new(database, sourceWriteGate, owner._covers, io);
                owner._profiles = new(database, sourceWriteGate);
                owner._display = new(database, sourceWriteGate);
                owner._pages = new(owner._repository, new SuwayomiReaderPageSource(source), io,
                    TimeSpan.FromSeconds(settings().ChapterPageCacheTtlSeconds));
                owner._nativeMetadata = new(() => settings().ColorDetectionMinConfidence);
                CatalogueExchange.InitializeSchema(database);
                var gallery = new GalleryEvidenceService(database, sourceWriteGate);
                var evidence = new NativeCatalogueEvidence(owner._nativeMetadata, gallery,
                    () => settings().DuplicateAutoMergeMinSummaryChars, () => settings().ExactThumbnailMatching);
                var work = Path.Combine(dataDirectory, "catalogue-work");
                _ = new PrivateDocumentStore(work);
                owner._chapters = new NativeChapterWriter(database, sourceWriteGate, gallery, work);
                var replay = new SavedGalleryReplay(database, sourceWriteGate, owner._chapters, work);
                owner._structure = new(database, sourceWriteGate, tasks, gallery, owner._nativeMetadata.Recompute,
                    owner._repository.ReconcileVisibility, DisplayMetadata.Reparent, evidence.Snapshot,
                    ids => { _ = owner._covers.ClearKeys(ids.Select(id => FormattableString.Invariant($"entry-{id}"))); }, replay);
                owner._hydration = new(database, sourceWriteGate, source, io, lanes, owner._nativeMetadata, owner._structure,
                    owner._chapters, settings, logs);
                return 0;
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return owner;
        }
        catch (Exception primary)
        {
            try { await owner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("Catalogue construction and cleanup failed.", primary, cleanup); }
            throw;
        }
    }

    internal void MapEndpoints(IEndpointRouteBuilder endpoints, BoundedWorkScheduler io,
        Func<AutoSourcarrSettings> settings, SemaphoreSlim admin)
    {
        // Phase 10: with browse wired the reader reads cached feeds and refreshes stale chapters; the routes,
        // the reader key protection and the /api/reader/v1 alias are the same either way.
        if (Browse is { } browse)
        {
            endpoints.MapReaderCatalogue(browse);
            endpoints.MapReaderServing(_repository!, _pages!, io, browse.ChaptersRead);
        }
        else
        {
            endpoints.MapReaderCatalogueRead(_catalogue ?? throw new InvalidOperationException("Catalogue is not initialized."));
            endpoints.MapReaderServing(_repository!, _pages!, io);
        }
        endpoints.MapServingProfiles(_profiles!, io, settings, admin);
        endpoints.MapDisplayMetadata(_display!, io, settings);
    }

    public ValueTask DisposeAsync() { lock (_closeGate) return new(_close ??= CloseAsync()); }
    private async Task CloseAsync()
    {
        await Task.Yield();
        List<Exception> errors = [];
        if (_hydration is not null)
            try { await _hydration.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (_pages is not null)
            try { await _pages.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (_covers is not null)
            try { await _covers.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException("Catalogue shutdown retained cleanup failures.", errors);
    }
}
