using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Maintenance;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Duplicates;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Discovery;
using AutoSourcarr.Core.Sources.Library;

namespace AutoSourcarr.Api;

internal sealed partial class ApplicationRuntime
{
    /// <summary>What the 0.4.5 startup sequence (main.py lifespan) runs on; built by OpenMatching, started after the source engine.</summary>
    private sealed record StartupWork(CatalogueMaintenance Maintenance, CatalogueTitles Titles, CatalogueStructure Structure, DuplicateMatching Duplicates,
        CatalogueDiscovery Discovery, SuwayomiLibrary Library, IResetHintIngest Hints);

    private StartupWork? _startup;

    private static readonly JobType OriginRecoveryJob = new("origin-recovery", "Recover interrupted chapter changes", JobPriority.High, JobFlags.LongRunning,
        StallTimeout: TimeSpan.FromMinutes(15));
    private static readonly JobType StartupMaintenanceJob = new("startup-catalogue-maintenance", "Catalogue maintenance after an upgrade", JobPriority.Low,
        JobFlags.LongRunning, StallTimeout: TimeSpan.FromMinutes(30));
    private static readonly JobType StartupDedupJob = new("startup-dedup", "Repair duplicate entries", JobPriority.Low, JobFlags.LongRunning,
        StallTimeout: TimeSpan.FromMinutes(30));

    /// <summary>
    /// The 0.4.5 startup sequence as jobs (visible in Tasks, cancellable, stopped with the job queue): interrupted chapter
    /// changes first, then after a quiet start the one-time catalogue upgrades (merge safety repair, startup maintenance,
    /// title consensus, Subbed orphan aliases), startup dedup (BACKGROUND_DEDUP_ON_STARTUP), sparse metadata
    /// (METADATA_ENRICHMENT_ON_STARTUP), additional-record discovery (AUTO_VARIANT_STARTUP_ENTRIES), the feed crawl
    /// (BACKGROUND_REFRESH_ON_STARTUP) and the post-reset hint replay. The delays keep this off the first page loads.
    /// Each upgrade stores its own version, so a restart does not repeat finished work.
    /// </summary>
    private void ScheduleStartupWork()
    {
        if (_startup is not { } work) return;
        var settings = Settings;
        if (work.Structure.PendingEntryOriginCount() > 0)
            Jobs.Enqueue(OriginRecoveryJob, "catalogue", async job =>
            {
                await Task.Yield();
                var result = work.Structure.ReconcilePendingOrigins(job.CancellationToken);
                var failed = result["failed"]?.GetValue<int>() ?? 0;
                if (failed > 0) throw new InvalidOperationException("Chapter recovery is still pending for " + failed.ToString(CultureInfo.InvariantCulture) + " entries");
                job.Result("Recovered " + result["completed"]?.ToJsonString() + " interrupted chapter changes");
            }, origin: "automatic");

        After(TimeSpan.FromSeconds(Math.Max(10, settings.BackgroundIdleSeconds)), () => Jobs.Enqueue(StartupMaintenanceJob, "catalogue", async job =>
        {
            job.Report(0, 4, "Repairing unsafe automatic merges");
            var safety = work.Maintenance.RunAutomaticMergeSafetyRepair();
            job.Report(1, 4, "Startup catalogue maintenance");
            var maintenance = work.Maintenance.RunStartupCatalogMaintenance();
            job.Report(2, 4, "Main-title consensus");
            var titles = work.Titles.RunBoundTitleConsensusUpgrade();
            job.Report(3, 4, "Subbed orphan aliases");
            var aliases = await work.Library.RepairOrphanAliasesUpgradeAsync(cancellationToken: job.CancellationToken).ConfigureAwait(false);
            var ran = new[] { safety, maintenance, titles, aliases }.Count(step => step["skipped"]?.GetValue<bool>() != true);
            job.Result(ran == 0 ? "Nothing to upgrade" : ran.ToString(CultureInfo.InvariantCulture) + " upgrade step(s) ran");
        }, origin: "automatic"));

        if (settings.BackgroundDedupOnStartup)
        {
            if (settings.AutomaticDuplicateMatching) work.Duplicates.ScheduleAutomatic(TimeSpan.FromSeconds(30));
            else After(TimeSpan.FromSeconds(30), () => Jobs.Enqueue(StartupDedupJob, "catalogue", async job =>
            {
                await Task.Yield();
                var result = work.Duplicates.RepairCatalogDuplicates(DecisionActor.Automatic, progress: (current, total, message) => job.Report(current, total, message),
                    cancellationToken: job.CancellationToken);
                job.Result("Duplicate repair complete; " + (result["merged"]?.ToJsonString() ?? "0") + " merged");
            }, origin: "automatic"));
        }

        if (settings.MetadataEnrichmentOnStartup && settings.MetadataEnrichmentStartupLimit > 0)
            After(TimeSpan.FromSeconds(Math.Max(0, settings.MetadataEnrichmentStartupDelaySeconds)), () =>
            {
                var ids = work.Discovery.SparseEntries(settings.MetadataEnrichmentStartupLimit);
                if (ids.Count == 0) return;
                Jobs.Enqueue(Endpoints.DiscoveryV1Endpoints.MetadataRepairJob, "Repair missing metadata", async job =>
                {
                    var result = await work.Discovery.EnrichMetadataAsync(ids, (current, total, message) => job.Report(current, total, message), job.CancellationToken).ConfigureAwait(false);
                    job.Result("Metadata repair complete; " + result["improved"]?.ToJsonString() + " entries got source detail");
                }, origin: "automatic");
            });

        if (settings.AutoVariantDiscovery && settings.AutoVariantStartupEntries > 0)
            After(TimeSpan.FromSeconds(Math.Max(0, settings.AutoVariantStartupDelaySeconds)), () =>
            {
                var ids = work.Discovery.AdditionalScanEntries(settings.AutoVariantStartupEntries);
                if (ids.Count == 0) return;
                Jobs.Enqueue(Endpoints.DiscoveryV1Endpoints.AdditionalScanJob, "Find extra source records", async job =>
                {
                    var result = await work.Discovery.ScanAdditionalAsync(ids, (current, total, message) => job.Report(current, total, message), job.CancellationToken).ConfigureAwait(false);
                    job.Result("Additional-record scan complete; " + result["added"]?.ToJsonString() + " record(s) added");
                }, origin: "automatic");
            });

        if (settings.BackgroundRefreshOnStartup)
            After(TimeSpan.FromSeconds(Math.Max(0, settings.StartupRefreshDelaySeconds)), () => Jobs.Enqueue(Endpoints.DiscoveryV1Endpoints.FeedRefreshJob,
                "Refresh browse feeds", async job =>
                {
                    var result = await work.Discovery.RefreshFeedsAsync((current, total, message) => job.Report(current, total, message), job.CancellationToken).ConfigureAwait(false);
                    job.Result("Refreshed " + result["feeds"]?.ToJsonString() + " feeds; " + result["cataloged_entries"]?.ToJsonString() + " entries");
                }, origin: "automatic", title: "Refresh browse feeds"));

        // A reset that stopped before its rebuild finished left its hints: replay them (low priority, before speculative growth).
        var hints = Path.Combine(_dataDirectory!, CatalogueStructure.ResetHintFile);
        if (File.Exists(hints)) _ = work.Structure.ScheduleResetHintReplay(Jobs, hints, work.Hints);
    }

    /// <summary>Runs <paramref name="start"/> after <paramref name="delay"/> unless the app is stopping first.</summary>
    private void After(TimeSpan delay, Action start)
    {
        var stop = _healthStop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, stop).ConfigureAwait(false);
                start();
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            { Errors.Record("Startup", "A startup job could not be queued: " + error.Message, "startup-schedule|" + error.GetType().Name); }
        });
    }

    /// <summary>
    /// The reset rebuild's source fetches (0.4.5 schedule_reset_hint_replay): a hint's record is fetched live and ingested with
    /// the current matcher, or re-proved against one entry's trusted evidence before it attaches ("reset-revalidated").
    /// A record another work owns is never moved.
    /// </summary>
    private sealed class ResetHintIngest(EntryIngest ingest, Func<SuwayomiClient> client) : IResetHintIngest
    {
        public async Task<long?> FetchAndIngestAsync(ResetHint hint, CancellationToken cancellationToken)
        {
            if (await MangaAsync(hint, cancellationToken).ConfigureAwait(false) is not { } manga) return null;
            return ingest.IngestManga(hint.SourceId, manga).EntryId;
        }

        public async Task<bool> RevalidateForEntryAsync(long entryId, ResetHint hint, CancellationToken cancellationToken)
        {
            if (await MangaAsync(hint, cancellationToken).ConfigureAwait(false) is not { } manga) return false;
            var current = ingest.CanonicalEntryId(entryId);
            if (ingest.FindExistingBySource(hint.SourceId, hint.MangaId) is { } owner && ingest.CanonicalEntryId(owner) != current) return false;
            var evidence = new SourceDiscovery(ingest).SourceMatchEvidence(current, hint.SourceId, manga);
            return evidence.AutomaticMatch && ingest.AttachAutomaticRecordIfCurrent(current, hint.SourceId, manga, evidence.Score, "reset-revalidated", hint.IsVariant);
        }

        private async Task<JsonObject?> MangaAsync(ResetHint hint, CancellationToken cancellationToken)
        {
            var payload = await client().FetchMangaAndChaptersAsync(hint.MangaId, true, false, cancellationToken).ConfigureAwait(false);
            if (payload["manga"] is not JsonObject manga) return null;
            manga["sourceId"] ??= hint.SourceId;
            return manga;
        }
    }
}
