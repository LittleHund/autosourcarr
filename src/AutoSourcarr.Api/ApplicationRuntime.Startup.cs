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
        CatalogueDiscovery Discovery, SuwayomiLibrary Library, IResetHintIngest Hints, ContinuousCatalogueCrawler Crawler);

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
        // SET-3: the first start of a new version moves the app's own old files to the trash (0.4.x did this after setup).
        if (_upgrade?.Status == "updated")
            After(TimeSpan.FromSeconds(30), () => _ = Task.Run(() =>
            {
                try { OldFiles()?.RunAutomatic("update"); }
                catch (Exception error) when (error is not OutOfMemoryException) { Logs.Record("Cleanup", "Old-file cleanup after the update failed (" + error.GetType().Name + ")."); }
            }));
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

        if (settings.ContinuousCatalogDiscovery) ScheduleCrawler(work.Crawler);

        // A reset that stopped before its rebuild finished left its hints: replay them (low priority, before speculative growth).
        var hints = Path.Combine(_dataDirectory!, CatalogueStructure.ResetHintFile);
        if (File.Exists(hints))
            try { _ = work.Structure.ScheduleResetHintReplay(Jobs, hints, work.Hints); }
            // An unreadable hint file is kept (it is the only copy of the pre-reset records) and reported.
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            { Errors.Record("catalogue", "The reset rebuild hints could not be replayed: " + error.Message, key: "reset-hint-replay"); }
    }

    /// <summary>
    /// Keeps the continuous crawler running like 0.4.5 (self-healing): a failed or frozen run starts again after 5 s; a run
    /// stopped for a reset/restore pause starts again once the queue resumes; a run a person cancelled stays off until the
    /// next start.
    /// </summary>
    private void ScheduleCrawler(ContinuousCatalogueCrawler crawler)
    {
        JobHandle? handle;
        try { handle = crawler.Schedule(Jobs); }
        catch (ObjectDisposedException) { return; }
        if (handle is null || handle.Merged) return;
        var stop = _healthStop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await handle.Completion.WaitAsync(stop).ConfigureAwait(false);
                if (crawler.StoppedByUser) return;
                do await Task.Delay(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
                while (Jobs.Paused || Jobs.ExclusivePending);
                ScheduleCrawler(crawler);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
        });
    }

    /// <summary>The crawler's enrichment fan-out for genuinely new records (metadata repair, additional records), bounded per page.</summary>
    private void ScheduleCrawlEnrichment(CatalogueDiscovery discovery, IReadOnlyList<long> ids)
    {
        var settings = Settings;
        try
        {
            _metadataEnrichment?.Schedule(ids.Take(Math.Max(1, settings.MetadataEnrichmentEntriesPerFeed)).ToArray());
            if (settings.AutoVariantDiscovery)
            {
                var dropped = 0;
                lock (_crawlScanGate)
                    foreach (var id in ids.Take(Math.Max(1, settings.AutoVariantEntriesPerFeed)))
                    {
                        if (_crawlScanQueued.Contains(id)) continue;
                        if (_crawlScanPending.Count >= CrawlScanPendingLimit) { dropped++; continue; }
                        _crawlScanPending.Enqueue(id); _crawlScanQueued.Add(id);
                    }
                // The crawler pauses on this backlog long before the limit; a drop is recorded, never silent.
                if (dropped > 0) Errors.Record("Discovery", dropped.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " new entries were not queued for the extra-record scan (the waiting list is full).", "crawl-scan-full", severity: "warning");
                ScheduleCrawlScan(discovery);
            }
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { _ = error; }
    }

    // 0.5.5: the crawler's extra-record scans share one job and one list (one queued job per page flooded the task list).
    private const int CrawlScanPendingLimit = 5000;
    /// <summary>Entries one run scans before it ends, so other scans of this type (a manual one, the start-up one) get their turn.</summary>
    private const int CrawlScanRunLimit = 50;
    private readonly object _crawlScanGate = new();
    private readonly Queue<long> _crawlScanPending = new();
    private readonly HashSet<long> _crawlScanQueued = [];
    private long _crawlScanRun;

    /// <summary>The waiting list of the crawler's extra-record scan in crawled pages (the unit the crawler's backlog limit counts).</summary>
    internal int CrawlScanBacklog()
    {
        var perPage = Math.Max(1, Settings.AutoVariantEntriesPerFeed);
        lock (_crawlScanGate) return (_crawlScanPending.Count + perPage - 1) / perPage;
    }

    private void ScheduleCrawlScan(CatalogueDiscovery discovery)
    {
        JobHandle handle;
        // Which run owns the waiting list: a cancelled run clears the list only while no newer run was queued for it.
        var run = Interlocked.Increment(ref _crawlScanRun);
        try
        {
            handle = Jobs.Enqueue(Endpoints.DiscoveryV1Endpoints.AdditionalScanJob, "crawl", async job =>
            {
                var done = 0; var added = 0L; var failed = 0L;
                while (done < CrawlScanRunLimit)
                {
                    var batch = new List<long>(10); int left;
                    lock (_crawlScanGate)
                    {
                        while (batch.Count < 10 && _crawlScanPending.TryDequeue(out var id)) { _crawlScanQueued.Remove(id); batch.Add(id); }
                        left = _crawlScanPending.Count;
                    }
                    if (batch.Count == 0) break;
                    var offset = done; var total = Math.Min(CrawlScanRunLimit, done + batch.Count + left);
                    var result = await discovery.ScanAdditionalAsync(batch, (current, _, message) => job.Report(offset + current, total, message), job.CancellationToken).ConfigureAwait(false);
                    done += batch.Count; added += result["added"]?.GetValue<int>() ?? 0; failed += result["failed"]?.GetValue<int>() ?? 0;
                }
                job.Result("Scanned " + done.ToString(System.Globalization.CultureInfo.InvariantCulture) + " new entries; " + added.ToString(System.Globalization.CultureInfo.InvariantCulture) + " record(s) added"
                    + (failed > 0 ? " · " + failed.ToString(System.Globalization.CultureInfo.InvariantCulture) + " entr" + (failed == 1 ? "y" : "ies") + " could not be scanned" : ""));
            }, JobPriority.Low, origin: "automatic", title: "Find extra source records");
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { _ = error; return; }
        if (handle.Merged) { Interlocked.Decrement(ref _crawlScanRun); return; }
        var stop = _healthStop.Token;
        // Entries added while the job was finishing would wait for the next page; pick them up now.
        _ = handle.Completion.ContinueWith(task =>
        {
            bool more;
            lock (_crawlScanGate)
            {
                // A cancelled scan (by the user or a reset, which renumbers entries) drops its list, unless a newer run
                // was queued meanwhile: then the list is that run's.
                if ((!task.IsCompletedSuccessfully || task.Result != JobOutcome.Completed) && Interlocked.Read(ref _crawlScanRun) == run)
                { _crawlScanPending.Clear(); _crawlScanQueued.Clear(); }
                more = _crawlScanPending.Count > 0;
            }
            if (more && !stop.IsCancellationRequested) ScheduleCrawlScan(discovery);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
