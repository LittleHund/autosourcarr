using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Catalogue.Maintenance;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Catalogue.Tags;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Duplicates;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Matching.Manual;
using AutoSourcarr.Core.Matching.Mixed;
using AutoSourcarr.Core.Matching.Review;
using AutoSourcarr.Core.Matching.Scoring;
using AutoSourcarr.Core.Matching.Wiring;
using AutoSourcarr.Core.Sources.Library;

namespace AutoSourcarr.Api;

internal sealed partial class ApplicationRuntime
{
    /// <summary>The phase-8 services the /api/v1 matching routes use.</summary>
    private sealed record MatchingWiring(MatchingTitlesV1Services Titles, ManualDecisionsV1Services Manual, MaintenanceV1Services Maintenance,
        DuplicateMatching Duplicates, MixedEntriesV1Services Mixed, TagSynonymsV1Services TagSynonyms, ReviewV1Services Review);

    /// <summary>
    /// Phase 8 "Matching": one decision log (every service records through it; the review workbench scopes it to a
    /// reviewer), one matching policy store, one set of manual decisions, one duplicate review service, one edition
    /// profile / mixed-entry service and the review workbench, all on the catalogue write gate. An unfinished mixed-entry
    /// check resumes as a job. None of these services own a disposable resource; their jobs stop with the job queue.
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
            autoMatchThreshold: () => owner.Settings.AutoMatchThreshold) { Policies = policies, Presets = presets };
        Func<long, JsonObject> entryView = new Core.Catalogue.CatalogueQueries(database, writeGate, _ => false).Entry;

        var evidence = new NativeCatalogueEvidence(catalogue.NativeMetadata, new GalleryEvidenceService(database, writeGate),
            () => owner.Settings.DuplicateAutoMergeMinSummaryChars, () => owner.Settings.ExactThumbnailMatching);
        var duplicates = new DuplicateMatching(database, writeGate, structure, evidence.Snapshot, decisions, () => owner.Settings,
            gate: new ScoredAutomaticMatchGate(new TextAutomaticMatchGate(() => owner.Settings, structure.AutomaticGalleryEvidence, policy))
            {
                // The matching profile behind the involved sources' rules sets the scorer's identity checks.
                OptionsFor = (left, right) => policy is null ? null : MatchProfiles.Combine(policy.RulesFor(left, right).Select(presets.OptionsForRule)),
            },
            manual: new ManualDuplicateView(database, manual), chooseMetadata: catalogue.NativeMetadata.ChooseMetadata)
        { Jobs = owner.Jobs };
        owner._duplicates = duplicates;
        duplicates.LoadPersisted();

        var maintenance = new CatalogueMaintenance(database, writeGate, decisions, manual, new CatalogueMaintenanceHooks
        {
            ClearCovers = catalogue.Covers.ClearAllAsync,
            RepairUnsafeAutomaticMerges = duplicates.RepairUnsafeAutomaticMerges,
            ResetCaches = () => { duplicates.Invalidate(); titles.LoadAliasCache(); },
        }, library, owner.Jobs, entryView, owner._dataDirectory);
        duplicates.SourceAttachmentRepair = () => maintenance.RepairUnsafeAutomaticSourceAttachments();

        var editions = new EditionProfiles(database, () =>
        {
            var settings = owner.Settings;
            return new EditionOptions(settings.ColorDetectionEnabled, settings.ColorDetectionMinConfidence, settings.ColorDetectionColoredPixelFraction,
                settings.ColorDetectionSamplePages, settings.ColorDetectionTimeout);
        });
        var mixed = new MixedEntries(database, editions, decisions, structure, catalogue.NativeMetadata);
        // A check interrupted by a restart continues from its saved cursor.
        var check = mixed.CheckStatus();
        if (check["run_id"] is not null && check["completed_at"] is null)
            _ = mixed.EnqueueCheck(owner.Jobs, restart: false, autoSplit: false, origin: "automatic");

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
            review);
    }
}
