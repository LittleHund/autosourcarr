using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Imports.Backup;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Imports;

/// <summary>
/// Crash safety and the after-reset recheck. A process that stops in the middle of an import leaves rows in resolving or
/// retrying (their job is gone), a run in "running" and discovery half done. <see cref="RecoverInterrupted"/> runs at
/// startup (schedule_import_discovery_resume + schedule_import_candidate_pipeline_upgrade): interrupted actions return to
/// unresolved and are re-dispatched at their own revision (a newer decision wins), interrupted runs resume from their
/// stored upload, unfinished discovery resumes. <see cref="RecheckAfterReset"/> keeps IMP-9: after a catalogue reset the
/// accepted rows whose entry is gone are rechecked against the current rules through the kept merge/decision log.
/// </summary>
public sealed partial class ImportRuns
{
    /// <summary>Bumped when candidate discovery changes so that existing unresolved rows are searched once more.</summary>
    public const string CandidatePipelineVersion = "phase9-1";
    private const string PipelineVersionKey = "import_candidate_pipeline_version";

    /// <summary>The decision log reader for the merge-log recheck (DecisionLog); null = only the row's own source URL is used.</summary>
    public IDecisionLogReader? History { get; init; }

    /// <summary>Queues <see cref="RecoverInterrupted"/> as a job (startup).</summary>
    public string? ScheduleStartupRecovery() =>
        Launch(RecoveryJob, "startup", "Resume interrupted import work", "automatic", null, progress =>
        {
            var result = RecoverInterrupted();
            progress.Result("Recovered " + Str(result, "recovered_actions") + " interrupted actions; resumed " + Str(result, "resumed_runs")
                + " imports; rechecked " + Str(result, "rechecked") + " matches after a reset; discovery for " + Str(result, "discovery_runs") + " runs");
            return Task.CompletedTask;
        });

    /// <summary>
    /// Returns every row left in resolving/retrying by a stopped process to "unresolved" (same revision), re-dispatches its
    /// Match/Retry with that revision as the recovery guard, resumes interrupted runs from their stored upload (or marks
    /// them failed when the upload is gone), rechecks rows after a reset and resumes discovery. No row stays stuck: a
    /// Match whose entry no longer exists stays unresolved with a reason and paused discovery.
    /// </summary>
    public JsonObject RecoverInterrupted()
    {
        var owned = _running.Where(pair => !pair.Value.IsCompleted).Select(pair => RunningOwner(pair.Key)).ToHashSet(StringComparer.Ordinal);
        var actions = Write(c =>
        {
            var recovered = new List<(string ImportId, long ItemId, string State, long? EntryId, long Revision)>();
            foreach (var row in c.Query("SELECT import_id,id,entry_id,state,decision_revision FROM import_items WHERE state IN ('resolving','retrying') ORDER BY import_id,position"))
            {
                var importId = row.Text("import_id"); var itemId = row.Integer("id");
                if (owned.Contains(ItemKey(importId, itemId))) continue;
                var changed = c.Execute("""
                    UPDATE import_items SET state='unresolved',reason='Recovered interrupted import action after restart',suggestion_checked=0,
                      suggestion_state='queued',updated_at=$now WHERE id=$id AND state=$state AND decision_revision=$rev
                    """, ("$now", Now()), ("$id", itemId), ("$state", row.Text("state")), ("$rev", row.Integer("decision_revision")));
                if (changed > 0)
                    recovered.Add((importId, itemId, row.Text("state"), row["entry_id"] is long entry ? (long?)entry : null, row.Integer("decision_revision")));
            }
            foreach (var importId in recovered.Select(action => action.ImportId).Distinct(StringComparer.Ordinal)) RecountIn(c, importId, null);
            return recovered;
        });
        int dispatched = 0, dispatchFailed = 0;
        foreach (var action in actions)
        {
            try
            {
                var result = action.State == "resolving" && action.EntryId is { } entry
                    ? ScheduleMatch(action.ImportId, action.ItemId, entry, DecisionActor.Automatic, recoveryRevision: action.Revision)
                    : ScheduleRetry(action.ImportId, action.ItemId, DecisionActor.Automatic, recoveryRevision: action.Revision);
                if (result["queued"] is not null) dispatched++;
            }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
            {
                dispatchFailed++;
                var message = action.State == "resolving" && action.EntryId is { } missing
                    ? "The selected match (entry " + Id(missing) + ") is no longer available. Choose a new Match or Retry."
                    : "Interrupted " + (action.State == "resolving" ? "Match" : "Retry") + " could not resume. Use Retry or choose a new Match.";
                Write(c => c.Execute("""
                    UPDATE import_items SET reason=$reason,suggestion_state='paused',updated_at=$now
                    WHERE import_id=$run AND id=$id AND state='unresolved' AND decision_revision=$rev
                    """, ("$reason", message), ("$now", Now()), ("$run", action.ImportId), ("$id", action.ItemId), ("$rev", action.Revision)));
            }
        }

        var resumedRuns = ResumeInterruptedRuns();
        var recheck = RecheckAfterReset(DecisionActor.Automatic);

        // One-time candidate pipeline upgrade (also after a reset, which clears the settings table): search every unresolved row again.
        var upgrade = Read(c => c.Scalar("SELECT value FROM settings WHERE key=$key", ("$key", PipelineVersionKey)) as string) != "\"" + CandidatePipelineVersion + "\"";
        var discoveryRuns = Write(c =>
        {
            if (upgrade)
            {
                // Rows whose discovery a person or a failed recovery paused stay paused.
                c.Execute("UPDATE import_items SET suggestion_checked=0,suggestion_state='queued',updated_at=$now WHERE state IN ('unresolved','failed') AND suggestion_state<>'paused'",
                    ("$now", Now()));
                c.Execute("INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                    ("$key", PipelineVersionKey), ("$value", "\"" + CandidatePipelineVersion + "\""));
            }
            return c.QueryAs<string>("""
                SELECT DISTINCT i.import_id FROM import_items i JOIN import_runs r ON r.id=i.import_id
                WHERE r.state<>'running' AND i.state IN ('unresolved','failed') AND i.suggestion_checked=0
                  AND i.suggestion_state IN ('queued','searching','found','local','pending') ORDER BY i.import_id
                """).ToArray();
        });
        var discovery = discoveryRuns.Count(run => ScheduleDiscovery(run, resumeOnly: true) is not null);
        return new JsonObject
        {
            ["recovered_actions"] = actions.Count, ["dispatched"] = dispatched, ["dispatch_failed"] = dispatchFailed, ["resumed_runs"] = resumedRuns,
            ["rechecked"] = recheck["rechecked"]?.DeepClone(), ["recheck"] = recheck, ["discovery_runs"] = discovery, ["pipeline_upgrade"] = upgrade,
        };
    }

    /// <summary>Runs left "running" without a job: resumed from their kept upload, otherwise counted and marked failed.</summary>
    private int ResumeInterruptedRuns()
    {
        var runs = Read(c => c.Query("SELECT id,filename FROM import_runs WHERE state='running'"));
        var resumed = 0;
        var plan = DataDirectory is { Length: > 0 } dataRoot ? LoadPlan(dataRoot) : null;
        foreach (var run in runs)
        {
            var importId = run.Text("id");
            if (_running.Any(pair => RunningOwner(pair.Key) == RunKey(importId) && !pair.Value.IsCompleted)) continue;
            if (plan is not null && PlanImportId(plan) == importId)
            {
                var planRows = plan.Items.Select(PlanRow).ToList();
                var planRoot = DataDirectory!;
                Connections.Types.SuwayomiImportPlan current = plan;
                Launch(ImportJob, importId, "Resume the Suwayomi library plan import", "automatic", RunKey(importId), async progress =>
                {
                    await RunImportJobAsync(importId, planRows, alreadyInLibrary: true, null, progress).ConfigureAwait(false);
                    Connections.Types.SuwayomiImport.Save(planRoot, current with { Status = Connections.Types.SuwayomiImport.Imported });
                });
                resumed++;
                continue;
            }
            var upload = DataDirectory is { Length: > 0 } uploadRoot ? Path.Combine(uploadRoot, RawFolder, importId, "upload.bin") : null;
            if (upload is not null && File.Exists(upload) && Library is not null)
            {
                try
                {
                    var library = MihonBackupReader.Read(File.ReadAllBytes(upload));
                    var rows = library.Manga.Select(manga => manga.ToJson()).ToList();
                    Launch(ImportJob, importId, "Resume Mihon/Tachiyomi backup import · " + run.Text("filename"), "automatic", RunKey(importId),
                        progress => RunImportJobAsync(importId, rows, alreadyInLibrary: false, upload, progress));
                    resumed++;
                    continue;
                }
                catch (Exception error) when (error is BackupDecodeException or IOException or UnauthorizedAccessException) { RemoveUpload(upload); }
            }
            Recount(importId, "failed");
        }
        return resumed;
    }

    /// <summary>
    /// IMP-9 merge-log recheck. For every accepted row (added/existing) whose entry no longer exists (a catalogue reset or a
    /// deletion): a remembered manual match is restored (manual decisions never change); otherwise the records the earlier
    /// decisions and merges of that entry named (decision log, kept by the normal reset) and the row's own source URL are
    /// followed to their current entry, which must still pass the current rules (exact source URL, or the strict import
    /// gate with today's matching rules). A row that no longer passes returns to unresolved for review; nothing is merged.
    /// </summary>
    public JsonObject RecheckAfterReset(DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var rows = Read(c => c.Query("SELECT * FROM import_items WHERE state IN ('added','existing') AND entry_id IS NOT NULL ORDER BY import_id,position"));
        var dangling = rows.Where(row => !_ingest.EntryExists(row.Integer("entry_id"))).ToList();
        int kept = 0, restored = 0, reopened = 0, manualPending = 0;
        foreach (var row in dangling)
        {
            var importId = row.Text("import_id"); var itemId = row.Integer("id"); var oldEntry = row.Integer("entry_id");
            var revision = row.Integer("decision_revision");
            var item = ItemPayload(row);
            var hasManual = Read(c => c.Scalar("SELECT 1 FROM import_manual_autoresolve WHERE backup_key=$key", ("$key", RejectionKey(item))) is not null);
            if (hasManual)
            {
                var (manualEntry, _) = ManualAutoresolveEntry(item, actor);
                if (manualEntry is { } target)
                {
                    if (Repoint(importId, itemId, revision, target, "Remembered manual import match restored after a catalogue reset"))
                    {
                        restored++;
                        Log("import.recheck.restored", actor, [target], "Remembered manual import match restored after a catalogue reset",
                            before: new JsonObject { ["entry_id"] = oldEntry }, after: Decision(importId, itemId, revision, item));
                    }
                }
                else manualPending++;
                continue;
            }
            var (candidate, reason) = RecheckCandidate(item, oldEntry);
            if (candidate is { } current && Repoint(importId, itemId, revision, current, "Rechecked after a catalogue reset: " + reason))
            {
                kept++;
                Log("import.recheck.kept", DecisionActor.Automatic, [current], "Earlier import match still passes the current rules: " + reason,
                    before: new JsonObject { ["entry_id"] = oldEntry }, after: Decision(importId, itemId, revision, item));
                continue;
            }
            var changed = Write(c =>
            {
                var count = c.Execute("""
                    UPDATE import_items SET state='unresolved',entry_id=NULL,reason=$reason,candidate_ids_json='[]',suggestion_checked=0,suggestion_state='queued',
                      library_add_verified=0,decision_revision=decision_revision+1,updated_at=$now
                    WHERE import_id=$run AND id=$id AND decision_revision=$rev AND state IN ('added','existing')
                    """, ("$reason", "Rechecked after a catalogue reset: the earlier match no longer passes the current rules (" + reason + "). Choose Match, Search or Skip."),
                    ("$now", Now()), ("$run", importId), ("$id", itemId), ("$rev", revision));
                if (count > 0) RecountIn(c, importId, null);
                return count;
            });
            if (changed == 0) continue;
            reopened++;
            Log("import.recheck.reopened", DecisionActor.Automatic, [], "Earlier import match no longer passes the current rules: " + reason,
                before: new JsonObject { ["entry_id"] = oldEntry }, after: Decision(importId, itemId, revision + 1, item));
        }
        return new JsonObject
        {
            ["rechecked"] = dangling.Count, ["kept"] = kept, ["restored"] = restored, ["reopened"] = reopened, ["manual_waiting"] = manualPending,
        };
    }

    /// <summary>The one current entry an earlier decision's records lead to, if it still passes; else null and why not.</summary>
    private (long? EntryId, string Reason) RecheckCandidate(JsonObject item, long oldEntry)
    {
        var sourceId = CurrentSourceId(item);
        List<long> byUrl = sourceId.Length > 0 ? FilterRejected(item, EntriesBySourceUrl(sourceId, Str(item, "url"))) : [];
        if (byUrl.Count == 1) return (byUrl[0], "exact source URL");
        var records = new HashSet<string>(StringComparer.Ordinal);
        if (History is { } history)
        {
            long? before = null;
            for (var page = 0; page < 10; page++)
            {
                var result = history.Read(new DecisionLogQuery(EntryId: oldEntry, BeforeId: before, Limit: 200));
                foreach (var entry in result.Items)
                {
                    if (!(entry.Kind.StartsWith("merge", StringComparison.Ordinal) || entry.Kind.StartsWith("match", StringComparison.Ordinal)
                        || entry.Kind.StartsWith("import", StringComparison.Ordinal) || entry.Kind.StartsWith("manual", StringComparison.Ordinal))) continue;
                    foreach (var record in entry.Records) records.Add(record);
                    if (entry.After is { } after && Str(after, "source_id").Length > 0 && after["source_manga_id"] is JsonValue manga && manga.TryGetValue<long>(out var mangaId))
                        records.Add(Str(after, "source_id") + ":" + Id(mangaId));
                }
                if (!result.HasMore || result.NextBefore is null) break;
                before = result.NextBefore;
            }
        }
        var owners = new List<long>();
        foreach (var record in records)
        {
            var split = record.LastIndexOf(':');
            if (split <= 0 || !long.TryParse(record[(split + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mangaId)) continue;
            if (_ingest.FindExistingBySource(record[..split], mangaId) is { } owner) owners.Add(owner);
        }
        var candidates = FilterRejected(item, owners);
        if (candidates.Count == 0) return (null, records.Count == 0 ? "no earlier record of this match is known" : "its records are no longer catalogued");
        if (candidates.Count > 1) return (null, "its records now belong to " + Id(candidates.Count) + " different entries");
        var evidence = CandidateEvidence(item, candidates[0]);
        return evidence.Eligible ? ((long?)candidates[0], evidence.Reason) : (null, evidence.Reason);
    }

    private static string RunKey(string importId) => "run:" + importId;

    private static Connections.Types.SuwayomiImportPlan? LoadPlan(string root)
    {
        try { return Connections.Types.SuwayomiImport.Load(root); }
        catch (Exception error) when (error is InvalidDataException or IOException or System.Text.Json.JsonException) { return null; }
    }

    private bool Repoint(string importId, long itemId, long revision, long entryId, string reason)
    {
        var snapshot = Snapshot(entryId);
        return Write(c => c.Execute("""
            UPDATE import_items SET entry_id=$entry,matched_snapshot_json=$snapshot,reason=$reason,updated_at=$now
            WHERE import_id=$run AND id=$id AND decision_revision=$rev AND state IN ('added','existing')
            """, ("$entry", entryId), ("$snapshot", snapshot.ToJsonString()), ("$reason", reason), ("$now", Now()), ("$run", importId), ("$id", itemId),
            ("$rev", revision)) > 0);
    }
}
