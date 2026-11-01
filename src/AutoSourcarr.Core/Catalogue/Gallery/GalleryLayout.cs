using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Gallery;

/// <summary>
/// Applying a saved gallery source policy to the cached catalogue (0.4.5 apply_gallery_layout / schedule_gallery_layout)
/// as a job: every work holding a record of the source is reconciled from cached records and chapters (no website
/// request), separating gallery records the policy and native evidence no longer allow together. The completed policy
/// revision is acknowledged with its counts; a policy saved meanwhile stops the run and the newer revision is applied
/// next. Combining newly compatible works stays with duplicate review (automatic merging re-reads the policy).
/// </summary>
public sealed class GalleryLayout(SqliteDatabase database, GalleryPolicyRepository policies, Func<long, CancellationToken, GalleryReconciliationResult> reconcile)
{
    private const int MaximumPasses = 5;

    public static readonly JobType LayoutJob = new("gallery-layout", "Apply gallery source rules", JobPriority.Normal, JobFlags.LongRunning,
        RunLimit: 1, StallTimeout: TimeSpan.FromMinutes(15));

    public JsonObject Schedule(JobQueue jobs, string sourceId, string origin = "manual")
    {
        ArgumentNullException.ThrowIfNull(jobs);
        _ = policies.Read(sourceId); // unknown sources answer KeyNotFoundException before anything is queued
        var handle = jobs.Enqueue(LayoutJob, "gallery-layout:" + sourceId, async job =>
        {
            await Task.Yield();
            // A policy saved while this runs merges into this job (same key), so the job itself applies the newer rules:
            // a changed revision restarts the pass, and a policy still pending after a pass runs one more (at most five).
            var splits = 0;
            for (var pass = 1; ; pass++)
            {
                try { splits += Apply(sourceId, job, job.CancellationToken)["splits"]!.GetValue<int>(); }
                catch (GalleryPolicyChangedException) when (pass < MaximumPasses) { continue; }
                if (pass >= MaximumPasses || !policies.Pending().Contains(sourceId, StringComparer.Ordinal)) break;
            }
            job.Result(splits.ToString(CultureInfo.InvariantCulture) + " gallery records separated");
        }, origin: origin, context: sourceId);
        return new JsonObject { ["queued"] = !handle.Merged, ["already_running"] = handle.Merged, ["task_id"] = handle.Id, ["source_id"] = sourceId };
    }

    public JsonObject Apply(string sourceId, JobContext? job = null, CancellationToken cancellationToken = default)
    {
        var revision = GalleryStorage.Integer(policies.Read(sourceId)["revision"]);
        var ids = database.Read(DatabaseFile.Catalog, session => session.Query("""
            SELECT entry_id FROM source_matches WHERE source_id=$source AND active=1
            UNION SELECT entry_id FROM source_variants WHERE source_id=$source AND active=1 ORDER BY entry_id
            """, ("$source", sourceId)).Select(row => row.Integer("entry_id")).ToArray());
        int splits = 0, deferred = 0, records = 0, checkedEntries = 0;
        job?.Report(0, ids.Length, "Applying gallery source rules to cached records");
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GalleryStorage.Integer(policies.Read(sourceId)["revision"]) != revision)
                throw new GalleryPolicyChangedException();
            var exists = database.Read(DatabaseFile.Catalog, session => session.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", id)) is not null);
            if (exists)
            {
                var result = reconcile(id, cancellationToken);
                splits += result.Splits; deferred += result.Deferred; records += result.Records; checkedEntries++;
            }
            job?.Report(checkedEntries, ids.Length, "Checked " + checkedEntries.ToString(CultureInfo.InvariantCulture) + " gallery entries; " + splits.ToString(CultureInfo.InvariantCulture) + " separated records");
        }
        var summary = new JsonObject
        {
            ["source_id"] = sourceId, ["splits"] = splits, ["merged"] = 0, ["checked"] = checkedEntries, ["records"] = ids.Length,
            ["reconciled_records"] = records, ["deferred"] = deferred,
        };
        policies.RecordLayout(sourceId, revision, summary);
        return summary;
    }
}

/// <summary>The source's gallery policy changed while a layout pass ran; the pass stops before applying old rules.</summary>
public sealed class GalleryPolicyChangedException() : InvalidOperationException("Gallery policy changed during layout; apply the newest rules");
