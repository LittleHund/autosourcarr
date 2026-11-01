using System.Globalization;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Translation;

namespace AutoSourcarr.Core.Catalogue.Metadata.Hydration;

/// <summary>
/// enrich_entry_translations / schedule_translation_enrichment / schedule_startup_translation_enrichment: warms the
/// translation cache with the machine-translated search names of entries a feed or import showed (at most
/// BACKGROUND_TRANSLATION_ENTRIES_PER_FEED), and once after startup (TRANSLATION_STARTUP_DELAY_SECONDS, the
/// BACKGROUND_TRANSLATION_ENTRIES_PER_STARTUP most recent entries). Only translation_cache is written: translations stay
/// search hints (MAT-20) and never become titles, aliases or duplicate evidence. Runs as one low-priority job.
/// </summary>
public sealed class TranslationEnrichment(SqliteDatabase database, Func<AutoSourcarrSettings> settings, TranslatedSearchNames translations,
    Func<long, IReadOnlyList<SearchName>> names)
{
    public static readonly JobType Job = new("translation-enrichment", "Prepare translated search names", JobPriority.Low, StallTimeout: TimeSpan.FromMinutes(5));

    /// <summary>enrich_entry_translations: the entry's translated search names (cached for later searches).</summary>
    public async Task<int> EnrichAsync(long entryId, CancellationToken cancellationToken)
    {
        var (id, genre) = database.Read(DatabaseFile.Catalog, s =>
        {
            var canonical = CatalogueTitles.Canonical(s, entryId);
            return (canonical, s.Scalar("SELECT genre FROM entries WHERE id=$id", ("$id", canonical)) as string ?? "");
        });
        var own = names(id);
        var expanded = await translations.ExpandAsync(own, genre, "", cancellationToken).ConfigureAwait(false);
        return expanded.Count(name => name.Kind.StartsWith("translated-", StringComparison.Ordinal));
    }

    /// <summary>schedule_translation_enrichment: answers the task id, or null when disabled or nothing to do.</summary>
    public string? Schedule(JobQueue jobs, IReadOnlyList<long> entryIds, string origin = "automatic")
    {
        ArgumentNullException.ThrowIfNull(jobs); ArgumentNullException.ThrowIfNull(entryIds);
        var options = settings();
        if (!options.BackgroundTranslationOnDiscovery) return null;
        var ids = database.Read(DatabaseFile.Catalog, s => entryIds.Select(id => CatalogueTitles.Canonical(s, id)).Distinct()
            .Take(Math.Max(0, options.BackgroundTranslationEntriesPerFeed)).ToArray());
        if (ids.Length == 0) return null;
        return jobs.Enqueue(Job, string.Join(',', ids.Select(id => id.ToString(CultureInfo.InvariantCulture))), job => RunAsync(job, ids), origin: origin).Id;
    }

    /// <summary>schedule_startup_translation_enrichment: after the startup delay, the most recently updated entries.</summary>
    public string? ScheduleStartup(JobQueue jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var options = settings();
        if (!options.BackgroundTranslationOnDiscovery || options.BackgroundTranslationEntriesPerStartup <= 0) return null;
        return jobs.Enqueue(Job, "startup", async job =>
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, settings().TranslationStartupDelaySeconds)), job.CancellationToken).ConfigureAwait(false);
            var ids = database.Read(DatabaseFile.Catalog, s => s.Query("SELECT id FROM entries ORDER BY updated_at DESC LIMIT $limit",
                ("$limit", Math.Max(0, settings().BackgroundTranslationEntriesPerStartup))).Select(row => row.Integer("id")).ToArray());
            await RunAsync(job, ids).ConfigureAwait(false);
        }, origin: "automatic").Id;
    }

    private async Task RunAsync(JobContext job, IReadOnlyList<long> ids)
    {
        var done = 0; var found = 0;
        foreach (var id in ids)
        {
            job.CancellationToken.ThrowIfCancellationRequested();
            job.Report(done, ids.Count, "Translating search names");
            try { found += await EnrichAsync(id, job.CancellationToken).ConfigureAwait(false); }
            catch (KeyNotFoundException) { }
            done++;
        }
        job.Result("Prepared " + found.ToString(CultureInfo.InvariantCulture) + " translated search names for " + done.ToString(CultureInfo.InvariantCulture) + " entries");
    }
}
