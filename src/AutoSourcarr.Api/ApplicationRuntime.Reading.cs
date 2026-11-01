using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Browse;
using AutoSourcarr.Core.Catalogue.Chapters;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Catalogue.Metadata;
using AutoSourcarr.Core.Catalogue.Metadata.Hydration;
using AutoSourcarr.Core.Catalogue.Serving;
using AutoSourcarr.Core.Catalogue.Tags;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Imaging;
using AutoSourcarr.Core.Library;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Reader.Extension;
using AutoSourcarr.Core.Sources.Discovery;
using AutoSourcarr.Core.Sources.Library;
using AutoSourcarr.Core.Translation;

namespace AutoSourcarr.Api;

internal sealed partial class ApplicationRuntime
{
    /// <summary>The phase-10 services the /api/v1 chapter, serving, library, catalogue and tag routes use.</summary>
    private sealed record ReadingWiring(ChaptersV1Services Chapters, ServingV1Services Serving, LibraryDownloadsV1Services LibraryDownloads,
        CatalogueV1Services CatalogueViews, TagsV1Services Tags);

    /// <summary>What phase 10 takes from the matching composition (same decision log, identity resolver and ingest).</summary>
    private sealed record ReadingInputs(IDecisionLog Decisions, MetadataIdentityResolver Identity, EntryIngest Ingest,
        CatalogueDiscovery Discovery, MetadataProviderSet Providers, ReaderCandidatePages CandidatePages, CoverHints CoverHints,
        TranslationClient Translator, TranslatedSearchNames TranslatedNames);

    /// <summary>
    /// Phase 10 "Chapters and reader": chapter refresh and the reader browse (cached feeds, READ-3 freshness), candidate
    /// review and quality (CHAP-5), serving upgrades and gallery layout (CHAP-3, CHAP-6), the CBZ library and downloads
    /// (CHAP-7), translation and metadata enrichment, tags and the catalogue views. Everything records on the matching
    /// decision log and writes under the catalogue write gate. Long work runs as jobs; the library manager, the metadata
    /// enrichment and the translation client are disposed at shutdown.
    /// </summary>
    private static ReadingWiring OpenReading(ApplicationRuntime owner, SuwayomiLibrary library, ReadingInputs inputs)
    {
        var database = owner.Database;
        var writeGate = owner._catalogueWriteGate;
        var catalogue = owner.Catalogue;
        var (decisions, identity, ingest, discovery, providers, candidatePages, coverHints, translator, translatedNames) = inputs;

        // 10A: chapter refresh, browse and the reader catalogue (mapped by CatalogueRuntime.MapEndpoints after this).
        var refresh = new ChapterRefresh(database, writeGate, catalogue.Chapters,
            (mangaId, details, chapters, token) => owner.SourceRuntime.Client.FetchMangaAndChaptersAsync(mangaId, details, chapters, token),
            () => TimeSpan.FromSeconds(owner.Settings.RefreshSourceTimeout));
        catalogue.Browse = new ReaderBrowse(database, writeGate, catalogue.ReaderRead, catalogue.Repository, refresh, owner.Io,
            () => new ReaderFreshness(TimeSpan.FromSeconds(owner.Settings.BrowseCacheRefreshSeconds), TimeSpan.FromSeconds(owner.Settings.MetadataTtlSeconds),
                owner.Settings.BrowsePageSize))
        {
            Jobs = owner.Jobs,
            HydrateDetails = (sourceId, mangaId) => catalogue.Hydration.Schedule(sourceId, mangaId),
            ScheduleFeedRefresh = () =>
            {
                owner.Jobs.Enqueue(DiscoveryV1Endpoints.FeedRefreshJob, "Refresh browse feeds", async job =>
                {
                    var result = await discovery.RefreshFeedsAsync((current, total, message) => job.Report(current, total, message), job.CancellationToken).ConfigureAwait(false);
                    job.Result("Refreshed " + result["feeds"]?.ToJsonString() + " feeds; " + result["cataloged_entries"]?.ToJsonString() + " entries");
                }, origin: "automatic", title: "Refresh browse feeds");
                return true;
            },
        };
        var decoder = new MagickPageRasterDecoder();
        var chapters = new ChaptersV1Services
        {
            Database = database, Queries = new CatalogueQueries(database, writeGate, _ => false),
            Review = new ChapterCandidateReview(database, writeGate, catalogue.Repository.ChapterDetails, catalogue.Repository.ReconcileVisibility, decisions),
            Quality = new CandidateQuality(database, writeGate, candidatePages, decoder,
                () => new QualityOptions(owner.Settings.QualityAnalysisConcurrency, TimeSpan.FromSeconds(owner.Settings.QualitySampleTimeout),
                    TimeSpan.FromSeconds(owner.Settings.QualityOverallTimeout)), decisions),
            Refresh = refresh,
            // The Subbed view needs MAIN_SUWAYOMI_URL (read at startup, like the maintenance routes).
            Subscription = string.IsNullOrWhiteSpace(owner.Settings.MainSuwayomiUrl) ? null : new Func<JsonObject>(library.CachedSubbedPayload),
            ClearCovers = keys => catalogue.Covers.ClearKeys(keys),
            HydrateDetails = (sourceId, mangaId) => catalogue.Hydration.Schedule(sourceId, mangaId),
            ScheduleIdentity = id => identity.Schedule(id),
        };
        var policies = new GalleryPolicyRepository(database, writeGate);
        var serving = new ServingV1Services
        {
            GalleryPolicies = policies, GalleryLayout = new GalleryLayout(database, policies, catalogue.ReconcileGallery),
            Upgrades = new ServingUpgrades(database, writeGate, catalogue.Repository, decisions),
            Profiles = catalogue.Profiles, Decisions = decisions, AdminLock = owner._admin,
        };

        // 10B: the CBZ library (LIBRARY_DIR) with its download queue and monitoring loop.
        var libraryStore = new LibraryStore(database);
        var libraryCatalogue = new CatalogueLibrary(database, catalogue.Repository, new CatalogueQueries(database, writeGate, _ => false),
            refresh: (id, token) => refresh.RefreshEntryAsync(id, null, token), display: catalogue.Display);
        var downloads = new LibraryDownloads(libraryStore, libraryCatalogue, new ReaderLibraryPageSource(catalogue.Pages, catalogue.Covers), owner.Jobs,
            LibraryOptions.For(owner.Settings));
        var manager = new LibraryManager(database, libraryStore, libraryCatalogue, downloads, owner.Jobs, decisions) { Display = catalogue.Display };
        owner._libraryManager = manager;
        manager.Start();

        // 10C: translation health and enrichment (MAT-20: search names only), metadata enrichment, orphan identity repair and tags.
        owner._translationHealth = new TranslationHealthCheck(translator);
        owner._metadataEnrichment = new MetadataEnrichment(database, () => owner.Settings, new MetadataEnrichmentHooks
        {
            ScheduleIdentity = id => identity.Schedule(id), Enrich = discovery.EnrichEntryAsync,
        });
        _ = new TranslationEnrichment(database, () => owner.Settings, translatedNames, id => ingest.ExpandedSearchNames(id)).ScheduleStartup(owner.Jobs);
        _ = new OrphanIdentityRepair(database, () => owner.Settings, identity.ResolveAsync, () => providers.Current().Count > 0).Schedule(owner.Jobs);
        var tagCatalogue = new TagCatalogue(database, writeGate, decisions) { Changed = () => owner._duplicates?.Invalidate() };
        var tagStore = new NativeTagStore(database, writeGate);
        if (!tagCatalogue.IndexCurrent()) _ = tagCatalogue.QueueReindex(owner.Jobs, tagStore, "automatic");
        var queries = new CatalogueQueries(database, writeGate, id => catalogue.Covers.IsCached(FormattableString.Invariant($"entry-{id}")));
        var views = new CatalogueV1Services
        {
            Database = database, Queries = queries, EntryExists = ingest.EntryExists, History = ingest.EntryHistory, Covers = coverHints,
            ClearCovers = (keys, token) => catalogue.Covers.ClearKeysAsync(keys, token),
            MetadataRefresh = new EntryMetadataRefresh
            {
                RefreshDetails = (id, token) => ingest.RefreshEntryAsync(id,
                    async (mangaId, details, withChapters, fetchToken) => (JsonObject?)await owner.SourceRuntime.Client
                        .FetchMangaAndChaptersAsync(mangaId, details, withChapters, fetchToken).ConfigureAwait(false),
                    fetchDetails: true, fetchChapters: false, cancellationToken: token),
                NeedsMetadata = discovery.NeedsMetadata, Enrich = discovery.EnrichEntryAsync, Entry = queries.Entry,
            },
            MetadataProviders = () => MetadataProviderStatus.Status(owner.Settings, providers.Current()),
            ResolveIdentity = identity.ResolveAsync,
            DiscoverAll = (id, token) => discovery.DiscoverAllAsync(id, exhaustive: true, token),
            Browse = catalogue.Browse.BrowseAsync,
            Engine = () => owner._mainCapabilities?.Status() ?? new JsonObject(),
            Metrics = RuntimeMetrics.Shared.Snapshot,
        };
        return new ReadingWiring(chapters, serving, new LibraryDownloadsV1Services { Manager = manager }, views,
            new TagsV1Services { Tags = tagCatalogue, Store = tagStore });
    }

    /// <summary>
    /// Phase 10 (10D): AutoSourcarr's own reader extension repository (built by the pipeline into reader-extension/, or
    /// AUTOSOURCARR_READER_EXTENSION_DIR) and the APK/JAR downloads of installed sources (READ-2).
    /// </summary>
    private static ReaderExtensionServices OpenReaderExtension(ApplicationRuntime owner, IReadOnlyDictionary<string, string?> environment)
    {
        var root = environment.TryGetValue("AUTOSOURCARR_READER_EXTENSION_DIR", out var configured) && !string.IsNullOrWhiteSpace(configured)
            ? configured : Path.Combine(AppContext.BaseDirectory, "reader-extension");
        var repository = new ReaderExtensionRepository(root);
        return new ReaderExtensionServices
        {
            Repository = repository,
            Downloads = new InstalledExtensionDownloads(new SourceHostReaderExtensionHost(() => owner._source?.Client), owner._publicFetcher!, repository),
        };
    }
}
