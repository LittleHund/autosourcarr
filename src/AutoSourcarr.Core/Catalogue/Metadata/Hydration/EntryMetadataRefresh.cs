using System.Text.Json.Nodes;
using AutoSourcarr.Core.Jobs;

namespace AutoSourcarr.Core.Catalogue.Metadata.Hydration;

/// <summary>
/// The "Refresh metadata" action of one entry (admin_refresh_entry_metadata): details of every active record again
/// (refresh_entry without chapters), then, while the entry is still sparse, enrich_entry_metadata (provider identity, then
/// source detail). Answers {entry, still_sparse}. As a job it appears in Tasks; one per entry at a time.
/// </summary>
public sealed class EntryMetadataRefresh
{
    public static readonly JobType Job = new("entry-metadata-refresh", "Refresh entry metadata", JobPriority.High, StallTimeout: TimeSpan.FromMinutes(5), RunLimit: 2);

    /// <summary>refresh_entry(fetch_details, no chapters) answering the entry (EntryIngest.RefreshEntryAsync).</summary>
    public required Func<long, CancellationToken, Task<JsonObject>> RefreshDetails { get; init; }
    /// <summary>_entry_needs_metadata (CatalogueDiscovery.NeedsMetadata).</summary>
    public required Func<long, bool> NeedsMetadata { get; init; }
    /// <summary>enrich_entry_metadata (CatalogueDiscovery.EnrichEntryAsync).</summary>
    public required Func<long, CancellationToken, Task<bool>> Enrich { get; init; }
    /// <summary>get_entry (CatalogueQueries.Entry).</summary>
    public required Func<long, JsonObject> Entry { get; init; }

    public async Task<JsonObject> RefreshAsync(long entryId, CancellationToken cancellationToken)
    {
        var entry = await RefreshDetails(entryId, cancellationToken).ConfigureAwait(false);
        if (NeedsMetadata(entryId) && await Enrich(entryId, cancellationToken).ConfigureAwait(false)) entry = Entry(entryId);
        return new JsonObject { ["entry"] = entry, ["still_sparse"] = NeedsMetadata(entryId) };
    }

    /// <summary>The same as one job (key = entry id); answers the handle (Merged when one already runs for the entry).</summary>
    public JobHandle Enqueue(JobQueue jobs, long entryId, string origin = "manual")
    {
        ArgumentNullException.ThrowIfNull(jobs);
        return jobs.Enqueue(Job, entryId.ToString(System.Globalization.CultureInfo.InvariantCulture), async job =>
        {
            job.Report(0, 1, "Refreshing details");
            var result = await RefreshAsync(entryId, job.CancellationToken).ConfigureAwait(false);
            job.Result(result["still_sparse"]?.GetValue<bool>() == true ? "Metadata refreshed; some fields are still missing" : "Metadata refreshed");
        }, origin: origin, title: "Refresh metadata of entry " + entryId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
