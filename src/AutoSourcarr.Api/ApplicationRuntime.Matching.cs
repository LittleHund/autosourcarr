using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Catalogue.Maintenance;
using AutoSourcarr.Core.Catalogue.Metadata.Hydration;
using AutoSourcarr.Core.Catalogue.Serving;
using AutoSourcarr.Core.Imaging;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Catalogue.Metadata;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Catalogue.Tags;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Imports;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Duplicates;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Matching.Manual;
using AutoSourcarr.Core.Matching.Mixed;
using AutoSourcarr.Core.Matching.Review;
using AutoSourcarr.Core.Matching.Scoring;
using AutoSourcarr.Core.Matching.Wiring;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources.Discovery;
using AutoSourcarr.Core.Sources.Library;

namespace AutoSourcarr.Api;

internal sealed partial class ApplicationRuntime
{
    /// <summary>The phase-8 services the /api/v1 matching routes use.</summary>
    private sealed record MatchingWiring(MatchingTitlesV1Services Titles, ManualDecisionsV1Services Manual, MaintenanceV1Services Maintenance,
        DuplicateMatching Duplicates, MixedEntriesV1Services Mixed, TagSynonymsV1Services TagSynonyms, ReviewV1Services Review,
        IDecisionLog Decisions, MetadataV1Services Metadata, DiscoveryV1Services Discovery, ImportsV1Services Imports, ReadingWiring Reading);

    /// <summary>
    /// Phase 8 "Matching": one decision log (every service records through it; the review workbench scopes it to a
    /// reviewer), one matching policy store, one set of manual decisions, one duplicate review service, one edition
    /// profile / mixed-entry service and the review workbench, all on the catalogue write gate. An unfinished mixed-entry
    /// check resumes as a job. Phase 9 adds metadata identity and the per-field chooser (choose metadata runs the chooser
    /// inside the same write), catalogue-wide discovery and the imports, on the same decision log and write gate. Their jobs
    /// stop with the job queue; the identity resolver (background lookups) and the provider clients are disposed at shutdown.
    /// </summary>
    private static MatchingWiring OpenMatching(ApplicationRuntime owner, SuwayomiLibrary library)
    {
        var database = owner.Database;
        var writeGate = owner._catalogueWriteGate;
        var catalogue = owner.Catalogue;
        var structure = catalogue.Structure;
        var decisionLog = new DecisionLog(database);
        var decisions = new ScopedDecisionLog(decisionLog);

        MatchingPolicyApplication? policy = null;
        var policies = new MatchingPolicyStore(database, writeGate, decisions, committed: _ =>
        {
            // A rule change: the automatic gate re-reads the rules and the duplicate review snapshot is rebuilt.
            policy?.Invalidate();
            owner._duplicates?.Invalidate();
        });
        // A tags check compares through the tag synonyms (built-in, user and reviewed equivalences).
        policy = new MatchingPolicyApplication(policies.Rules, MatchingPolicyApplication.CatalogueSources(database)) { Synonyms = new TagSynonyms(database).Load };
        var presets = new MatchingPresetStore(database, writeGate, decisions);

        var titles = new CatalogueTitles(database, writeGate, decisions) { TitlesChanged = _ => owner._duplicates?.Invalidate() };
        var evidence = new NativeCatalogueEvidence(catalogue.NativeMetadata, new GalleryEvidenceService(database, writeGate),
            () => owner.Settings.DuplicateAutoMergeMinSummaryChars, () => owner.Settings.ExactThumbnailMatching);
        // Phase 10 (10C): decoded thumbnail fingerprints (exact-pixel identity evidence) and remembered source cover hints.
        var thumbnails = new ThumbnailFingerprints(database, () => owner.Settings, new SuwayomiThumbnailSource(owner.SourceRuntime.Client));
        var coverHints = new CoverHints(database, () => owner.Settings, (sourceId, mangaId) => catalogue.Hydration.Schedule(sourceId, mangaId, eager: true));
        // MAT-20: machine translations only widen searches (manual source search, additional-record discovery).
        var translator = new Core.Translation.TranslationClient(database, () => owner.Settings);
        owner._translator = translator;
        var translatedNames = new Core.Translation.TranslatedSearchNames(translator, () => owner.Settings);

        // Phase 9 metadata: _choose_metadata in its own catalogue write (the chooser runs inside it as the field policy).
        void Choose(long id)
        {
            lock (writeGate)
                database.Write(DatabaseFile.Catalog, s =>
                {
                    catalogue.NativeMetadata.ChooseMetadata(s, id, DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
                    return 0;
                });
        }
        IReadOnlyList<Core.Connections.ConnectionConfig> EnabledConnections(string type)
        {
            // A damaged connection store means no provider connection (public endpoints still follow METADATA_IDENTITY_PROVIDERS).
            try { return owner._connections?.Enabled(type) ?? []; }
            catch (Exception error) when (error is Core.Settings.ConfigurationException or IOException or UnauthorizedAccessException) { return []; }
        }
        var providers = new MetadataProviderSet(EnabledConnections, () => owner.Settings);
        owner._metadataProviders = providers;
        var identity = new MetadataIdentityResolver(database, writeGate, () => owner.Settings, providers.Current, evidence.Snapshot)
        {
            AddTitle = (id, title, language, kind, source) => titles.AddTitle(id, title, language, kind, source),
            IdentitiesChanged = id => { Choose(id); owner._duplicates?.Invalidate(); },
            Failed = (_, error) => owner.Logs.Record("Metadata", "Identity lookup failed (" + error.GetType().Name + ")."),
            Fingerprints = thumbnails.HydrateAsync,
        };
        owner._metadataIdentity = identity;
        var chooser = new MetadataChooser(database, writeGate, decisions, titles)
        {
            Changed = id =>
            {
                owner._duplicates?.Invalidate();
                _ = catalogue.Covers.ClearKeys([FormattableString.Invariant($"entry-{id}")]);
            },
        };
        catalogue.NativeMetadata.FieldPolicy = chooser;

        EntryIngest? ingest = null;
        var operations = new ManualCatalogueOperations(database, writeGate,
            () => ingest ?? throw new InvalidOperationException("Matching is still starting."), titles, structure, catalogue.NativeMetadata,
            catalogue.Chapters,
            fetch: (mangaId, withChapters, token) => owner.SourceRuntime.Client.FetchMangaAndChaptersAsync(mangaId, true, withChapters, token),
            invalidate: keys =>
            {
                owner._duplicates?.Invalidate();
                if (keys.Count > 0) _ = catalogue.Covers.ClearKeys(keys);
            });
        var manual = new ManualDecisions(database, writeGate, decisions, operations);
        ingest = new EntryIngest(database, writeGate, decisions, new ManualDecisionsView(manual),
            // Phase 10: a new source record also seeds identities from its source ids and remembers its cover hint.
            followUps: new SourceIdentitySeed(database, writeGate,
                new MetadataIngestFollowUps(identity) { Choose = Choose, Hydrate = (sourceId, mangaId) => catalogue.Hydration.Schedule(sourceId, mangaId) }, coverHints)
            { IdentityAdded = id => { Choose(id); owner._duplicates?.Invalidate(); } },
            autoMatchThreshold: () => owner.Settings.AutoMatchThreshold, identityOnIngest: () => owner.Settings.MetadataIdentityOnIngest)
        { Policies = policies, Presets = presets };
        Func<long, JsonObject> entryView = new Core.Catalogue.CatalogueQueries(database, writeGate, _ => false).Entry;

        var duplicates = new DuplicateMatching(database, writeGate, structure, evidence.Snapshot, decisions, () => owner.Settings,
            // Two confident identities of one provider with different ids are different works, whatever the titles say.
            gate: new MetadataIdentityGate(new ScoredAutomaticMatchGate(new TextAutomaticMatchGate(() => owner.Settings, structure.AutomaticGalleryEvidence, policy))
            {
                // The matching profile behind the involved sources' rules sets the scorer's identity checks.
                OptionsFor = (left, right) => policy is null ? null : MatchProfiles.Combine(policy.RulesFor(left, right).Select(presets.OptionsForRule)),
            }),
            manual: new ManualDuplicateView(database, manual), chooseMetadata: catalogue.NativeMetadata.ChooseMetadata)
        { Jobs = owner.Jobs };
        duplicates.Hydration = identity;
        owner._duplicates = duplicates;
        duplicates.LoadPersisted();

        // Phase 9 imports (9A): backup imports and the Suwayomi library plan through the matching pipeline.
        var imports = new ImportRuns(database, writeGate, decisions, ingest)
        {
            Manual = manual, History = decisionLog, Jobs = owner.Jobs, DataDirectory = owner._dataDirectory,
            Options = () => ImportOptions.From(owner.Settings),
            // Adding to the main library needs MAIN_SUWAYOMI_URL (read at startup, like the Subbed routes).
            Library = string.IsNullOrWhiteSpace(owner.Settings.MainSuwayomiUrl) ? null : new SuwayomiImportLibrary(library, () => owner._main),
            Sources = new DelegateImportSourceSearch(
                (sourceId, mode, page, query, token) => owner.SourceRuntime.Client.FetchSourceMangaAsync(sourceId, mode, page, query, token),
                (mangaId, details, chapters, token) => owner.SourceRuntime.Client.FetchMangaAndChaptersAsync(mangaId, details, chapters, token)),
            Metadata = new MetadataClientsImportSearch(providers.Current),
            Identities = identity,
            ExactThumbnails = thumbnails.ImportExactEntryIdsAsync,
        };
        _ = imports.ScheduleStartupRecovery();

        // Built below; the startup maintenance and reset hooks run only after OpenMatching returned.
        EditionProfiles? editionsLate = null; MixedEntries? mixedLate = null;
        IResetHintIngest hintIngest = new ResetHintIngest(ingest, () => owner.SourceRuntime.Client);
        var maintenance = new CatalogueMaintenance(database, writeGate, decisions, manual, new CatalogueMaintenanceHooks
        {
            // One-time upgrade steps (run_startup_catalog_maintenance): unwired steps would be skipped for good.
            RepairEntryAliasIntegrity = titles.RepairEntryAliasIntegrity,
            RepairSourceTitleSuffixes = titles.RepairSourceTitleSuffixes,
            RepairColoredDisplayTitles = () => new JsonObject { ["changed"] = editionsLate!.RepairColoredDisplayTitles() },
            RepairLegacyColorMarkerProfiles = () => editionsLate!.RepairLegacyColorMarkerProfiles(),
            RepairLegacyColorFalseAttachments = ids => mixedLate!.RepairLegacyColorFalseAttachments(ids),
            // A reset's rebuild hints are replayed as a job (schedule_reset_hint_replay).
            ScheduleHintReplay = path => structure.ScheduleResetHintReplay(owner.Jobs, path, hintIngest) is not null,
            ClearCovers = catalogue.Covers.ClearAllAsync,
            RepairUnsafeAutomaticMerges = duplicates.RepairUnsafeAutomaticMerges,
            ResetCaches = () => { duplicates.Invalidate(); titles.LoadAliasCache(); },
            AfterResetRebuild = imports.RecheckAfterReset,
        }, library, owner.Jobs, entryView, owner._dataDirectory);
        duplicates.SourceAttachmentRepair = () => maintenance.RepairUnsafeAutomaticSourceAttachments();

        // Phase 10: colour detection samples real pages of the served candidates (IPageImageSource).
        var candidatePages = new ReaderCandidatePages(database, writeGate, new SuwayomiReaderPageSource(owner.SourceRuntime.Client), catalogue.Pages);
        var editions = new EditionProfiles(database, () =>
        {
            var settings = owner.Settings;
            return new EditionOptions(settings.ColorDetectionEnabled, settings.ColorDetectionMinConfidence, settings.ColorDetectionColoredPixelFraction,
                settings.ColorDetectionSamplePages, settings.ColorDetectionTimeout);
        }, new ReaderPageImages(database, candidatePages, new MagickPageRasterDecoder()));
        var mixed = new MixedEntries(database, editions, decisions, structure, catalogue.NativeMetadata);
        editionsLate = editions; mixedLate = mixed;
        // A check interrupted by a restart continues from its saved cursor.
        var check = mixed.CheckStatus();
        if (check["run_id"] is not null && check["completed_at"] is null)
            _ = mixed.EnqueueCheck(owner.Jobs, restart: false, autoSplit: false, origin: "automatic");

        Core.Catalogue.Chapters.ChapterRefresh? chapterRefresh = null;
        var discovery = new CatalogueDiscovery(database, writeGate, ingest, new SuwayomiCatalogueClient(owner.SourceRuntime.Client), () => owner.Settings)
        {
            Identity = identity, ChooseMetadata = Choose,
            // A newly attached record gets its details and (phase 10) its chapters, as a low-priority job per work.
            RecordAttached = (entryId, sourceId, mangaId) =>
            {
                catalogue.Hydration.Schedule(sourceId, mangaId);
                chapterRefresh?.ScheduleEntry(owner.Jobs, entryId, "automatic", Core.Jobs.JobPriority.Low);
            },
            TranslateNames = (names, genre, language, token) => translatedNames.ExpandAsync(names, genre, language, token),
        };

        var reading = OpenReading(owner, library, new ReadingInputs(decisions, identity, ingest, discovery, providers, candidatePages, coverHints,
            translator, translatedNames));
        chapterRefresh = reading.Chapters.Refresh;
        owner._startup = new StartupWork(maintenance, titles, structure, duplicates, discovery, library, hintIngest);

        var review = new ReviewV1Services
        {
            Workbench = new ReviewWorkbench(new ReviewActions(database, writeGate, decisions, duplicates, structure, manual, titles), decisions,
                new DecisionLogHistory(database, decisionLog)),
            Simulator = new PolicySimulator(policies, new DuplicatePolicyEvaluator(database, duplicates, policy), owner.Jobs),
        };
        return new MatchingWiring(
            new MatchingTitlesV1Services
            {
                Titles = titles, Ingest = ingest, Presets = presets, Policies = policies,
                SearchSource = (sourceId, query, page, token) => owner.SourceRuntime.Client.FetchSourceMangaAsync(sourceId, "SEARCH", page, query, token),
                TranslateNames = (names, genre, language, token) => translatedNames.ExpandAsync(names, genre, language, token),
            },
            new ManualDecisionsV1Services { Manual = manual, Log = decisionLog, Entry = entryView },
            new MaintenanceV1Services
            {
                Maintenance = maintenance, Manual = manual,
                SubbedPayload = string.IsNullOrWhiteSpace(owner.Settings.MainSuwayomiUrl) ? null : new Func<JsonObject>(library.CachedSubbedPayload),
                RefreshSubbed = () => library.ScheduleStatusRefresh(force: true),
                ClearCovers = catalogue.Covers.ClearAllAsync,
            },
            duplicates,
            new MixedEntriesV1Services { Mixed = mixed },
            new TagSynonymsV1Services { Synonyms = new TagSynonyms(database), Decisions = decisions },
            review,
            decisions,
            new MetadataV1Services { Chooser = chooser },
            new DiscoveryV1Services { Discovery = discovery, SubbedEntryIds = (force, token) => library.SubbedEntryIdsAsync(force, token) },
            new ImportsV1Services { Runs = imports },
            reading);
    }
}
