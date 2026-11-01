using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Imports;

/// <summary>
/// Match / Retry / Reject / Skip (IMP-1, IMP-4, IMP-5). Each action commits the person's choice and a new decision revision
/// immediately (so the row shows it at once and the latest click wins), then runs the slow part (source binding, main
/// library) as a job that re-checks its revision before every write. A cancelled job puts back the row it replaced.
/// </summary>
public sealed partial class ImportRuns
{
    /// <summary>Runs import work as a job (or on the thread pool without a queue) and remembers it per item.</summary>
    private string? Launch(JobType type, string key, string title, string origin, string? itemKey, Func<ImportProgress, Task> work)
    {
        Task completion;
        string? taskId = null;
        if (Jobs is { } jobs)
        {
            var handle = jobs.Enqueue(type, key, context => work(new ImportProgress(context)), origin: origin, title: title);
            completion = handle.Completion;
            taskId = handle.Id;
        }
        else completion = Task.Run(async () =>
        {
            // Without a job queue the failure is already on the row (failed / restored); nothing else observes it.
            try { await work(ImportProgress.None).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        });
        // One key per launch ("owner#unique"): a newer action of the same item never hides an older one still finishing.
        var runningKey = (itemKey ?? "job") + "#" + Guid.NewGuid().ToString("N");
        _running[runningKey] = completion;
        _ = completion.ContinueWith(_ => _running.TryRemove(new KeyValuePair<string, Task>(runningKey, completion)), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return taskId;
    }

    /// <summary>The settled row a replacing action restores on cancellation (_import_resolution_restore_row).</summary>
    private DatabaseRow RestorePoint(string importId, long itemId, DatabaseRow row)
    {
        var key = ItemKey(importId, itemId);
        if (row.Text("state") is "resolving" or "retrying" && _restorePoints.TryGetValue(key, out var previous)) return previous;
        _restorePoints[key] = row;
        return row;
    }

    private IReadOnlyList<string> EntryTitles(long entryId, string backupTitle)
    {
        var titles = new List<string>();
        if (backupTitle.Trim().Length > 0) titles.Add(backupTitle.Trim());
        titles.AddRange(Facts(entryId).Titles);
        return titles.Distinct(StringComparer.Ordinal).Take(4).ToArray();
    }

    /// <summary>_add_import_entry_to_main_library: one reconciliation at a time; never claims a superseded decision.</summary>
    private async Task<ImportLibraryOutcome> AddToLibraryAsync(long entryId, string backupTitle, Func<bool> superseded, CancellationToken cancellationToken)
    {
        var library = Library ?? throw new ImportUnavailableException("MAIN_SUWAYOMI_URL is not configured");
        await _libraryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (superseded()) return new("superseded", "A newer import decision superseded this action");
            return await library.AddAsync(_ingest.CanonicalEntryId(entryId), EntryTitles(entryId, backupTitle), superseded, cancellationToken).ConfigureAwait(false);
        }
        finally { _libraryLock.Release(); }
    }

    // ---- Match -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// schedule_import_item_match: the chosen entry is remembered for this backup identity (an explicit Match also lifts an
    /// earlier Reject of the same work), the row becomes "resolving" at a new revision, and the binding and library add run
    /// as a job. <paramref name="recoveryRevision"/> re-dispatches an interrupted action only if nothing newer happened.
    /// </summary>
    public JsonObject ScheduleMatch(string importId, long itemId, long entryId, DecisionActor actor, long? recoveryRevision = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var row = ReadItem(importId, itemId);
        if (!_ingest.EntryExists(entryId)) throw new KeyNotFoundException("Entry not found");
        entryId = _ingest.CanonicalEntryId(entryId);
        var restore = RestorePoint(importId, itemId, row);
        var item = ItemPayload(row);
        var snapshot = Snapshot(entryId);
        var memory = recoveryRevision is null ? PrepareManualMemory(item) : null;
        // One transaction: the lifted rejection, the remembered match (with its manual binding), the new revision and the
        // resolving row commit together, so two racing Match clicks can never leave the remembered match of one click on
        // the row of the other: the last commit owns both.
        var (claimed, bound) = Write(c =>
        {
            (long EntryId, string Record)? binding = null;
            if (memory is not null)
            {
                ClearRejectionIn(c, memory.Key, entryId);
                binding = RememberManualAutoresolveIn(c, memory, entryId).Bound;
            }
            if (ClaimIn(c, importId, itemId, recoveryRevision) is not { } next) return ((long?)null, binding);
            c.Execute("""
                UPDATE import_items SET state='resolving',reason='Selected match accepted; adding/binding in the background…',entry_id=$entry,
                  matched_snapshot_json=$snapshot,updated_at=$now WHERE import_id=$run AND id=$id AND decision_revision=$rev
                """, ("$entry", entryId), ("$snapshot", snapshot.ToJsonString()), ("$now", Now()), ("$run", importId), ("$id", itemId), ("$rev", next));
            RecountIn(c, importId, null);
            return ((long?)next, binding);
        });
        LogManualBinding(bound, actor);
        if (claimed is not { } revision) return SupersededAnswer(importId, itemId);
        if (recoveryRevision is null)
            Log("import.match.manual", actor, [entryId], "Import matched by hand: " + Str(item, "title"), after: Decision(importId, itemId, revision, item));
        var title = Str(item, "title");
        var taskId = Launch(ResolveJob, ItemKey(importId, itemId) + ":" + revision, "Resolve imported entry · " + title, "manual", ItemKey(importId, itemId),
            async progress =>
            {
                try
                {
                    var result = await MatchAsync(importId, itemId, entryId, revision, actor, progress).ConfigureAwait(false);
                    progress.Result(result["superseded"] is not null ? "Superseded by a newer import decision" : Str(result, "reason"));
                }
                catch (OperationCanceledException)
                {
                    RestoreCancelled(importId, itemId, restore, "resolving", revision);
                    throw;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    UpdateState(importId, itemId, "failed", error.Message, entryId, expectedRevision: revision);
                    throw;
                }
                finally { _restorePoints.TryRemove(new KeyValuePair<string, DatabaseRow>(ItemKey(importId, itemId), restore)); }
            });
        return new JsonObject
        {
            ["decision_revision"] = revision, ["queued"] = true, ["import_id"] = importId, ["item_id"] = itemId, ["entry_id"] = entryId,
            ["task_id"] = taskId, ["state"] = "resolving",
        };
    }

    private static JsonObject Decision(string importId, long itemId, long revision, JsonObject item) => new()
    {
        ["import_id"] = importId, ["item_id"] = itemId, ["decision_revision"] = revision, ["title"] = Str(item, "title"),
        ["source"] = Str(item, "source"), ["url"] = Str(item, "url"),
    };

    /// <summary>match_import_item: bind the backup source (manual), add to the main library, settle the row (revision-guarded).</summary>
    internal async Task<JsonObject> MatchAsync(string importId, long itemId, long entryId, long revision, DecisionActor actor, ImportProgress progress)
    {
        var token = progress.CancellationToken;
        if (Superseded(importId, itemId, revision)) return SupersededAnswer(importId, itemId);
        var item = ItemPayload(ReadItem(importId, itemId));
        progress.Report(0, 3, "Binding original source and metadata");
        var binding = await BindBackupSourceAsync(entryId, item, manual: true, token).ConfigureAwait(false);
        if (Superseded(importId, itemId, revision)) return SupersededAnswer(importId, itemId);
        var final = _ingest.CanonicalEntryId(binding["entry_id"]?.GetValue<long>() ?? entryId);
        progress.Report(1, 3, "Adding entry to main Suwayomi library");
        var outcome = await AddToLibraryAsync(final, Str(item, "title"), () => Superseded(importId, itemId, revision), token).ConfigureAwait(false);
        if (outcome.State == "superseded") return SupersededAnswer(importId, itemId);
        progress.Report(2, 3, "Saving resolved import state");
        var reason = outcome.Reason + (binding["deferred"] is not null ? "; " + Str(binding, "reason") : "");
        var result = UpdateState(importId, itemId, outcome.State, reason, final, expectedRevision: revision);
        if (result["ok"]?.GetValue<bool>() == true)
            Log("import.resolved", actor, [final], reason, after: Decision(importId, itemId, revision, item), records: BoundRecord(binding));
        progress.Report(3, 3, reason);
        return result;
    }

    // ---- Retry -----------------------------------------------------------------------------------------------------

    /// <summary>schedule_import_item_retry: the row becomes "retrying" at a new revision; the resolution runs as a job.</summary>
    public JsonObject ScheduleRetry(string importId, long itemId, DecisionActor actor, long? recoveryRevision = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var row = ReadItem(importId, itemId);
        var restore = RestorePoint(importId, itemId, row);
        long? previousEntry = restore["entry_id"] is long entry ? entry : null;
        if (Claim(importId, itemId, recoveryRevision) is not { } revision)
            return SupersededAnswer(importId, itemId);
        Write(c =>
        {
            c.Execute("UPDATE import_items SET state='retrying',reason='Retry accepted; resolving in the background…',updated_at=$now WHERE import_id=$run AND id=$id AND decision_revision=$rev",
                ("$now", Now()), ("$run", importId), ("$id", itemId), ("$rev", revision));
            return RecountIn(c, importId, null);
        });
        var item = ItemPayload(row);
        if (recoveryRevision is null) Log("import.retry", actor, [], "Import retry: " + Str(item, "title"), after: Decision(importId, itemId, revision, item));
        var taskId = Launch(ResolveJob, ItemKey(importId, itemId) + ":" + revision, "Retry unresolved import · " + Str(item, "title"), "manual", ItemKey(importId, itemId),
            async progress =>
            {
                try
                {
                    var result = await RetryAsync(importId, itemId, revision, actor, progress).ConfigureAwait(false);
                    progress.Result(result["superseded"] is not null ? "Superseded by a newer import decision"
                        : Str(result, "state") == "unresolved" ? "Still unresolved · " + Str(result, "reason") : Str(result, "reason"));
                }
                catch (OperationCanceledException)
                {
                    RestoreCancelled(importId, itemId, restore, "retrying", revision);
                    throw;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    UpdateState(importId, itemId, "failed", error.Message, previousEntry, expectedRevision: revision);
                    throw;
                }
                finally { _restorePoints.TryRemove(new KeyValuePair<string, DatabaseRow>(ItemKey(importId, itemId), restore)); }
            });
        return new JsonObject
        {
            ["decision_revision"] = revision, ["queued"] = true, ["import_id"] = importId, ["item_id"] = itemId, ["task_id"] = taskId, ["state"] = "retrying",
        };
    }

    /// <summary>
    /// retry_import_item: a remembered manual match first; otherwise the full resolution (with metadata identity), then
    /// live discovery over the enabled sources; only the strict gate accepts. Unresolved rows keep their candidates.
    /// </summary>
    internal async Task<JsonObject> RetryAsync(string importId, long itemId, long revision, DecisionActor actor, ImportProgress progress)
    {
        var token = progress.CancellationToken;
        if (Superseded(importId, itemId, revision)) return SupersededAnswer(importId, itemId);
        var row = ReadItem(importId, itemId);
        var item = ItemPayload(row);
        var title = Str(item, "title");
        progress.Report(0, 4, "Resolving backup and metadata identity");
        var (remembered, rememberedReason) = ManualAutoresolveEntry(item, actor);
        if (remembered is { } rememberedId)
        {
            try
            {
                progress.Report(2, 4, "Binding original source and metadata");
                var binding = await BindBackupSourceAsync(rememberedId, item, manual: true, token).ConfigureAwait(false);
                if (Superseded(importId, itemId, revision)) return SupersededAnswer(importId, itemId);
                WriteDiscovery(importId, itemId, revision, null, null, null, item);
                var entry = _ingest.CanonicalEntryId(binding["entry_id"]?.GetValue<long>() ?? rememberedId);
                progress.Report(3, 4, "Adding entry to main Suwayomi library");
                var outcome = await AddToLibraryAsync(entry, title, () => Superseded(importId, itemId, revision), token).ConfigureAwait(false);
                if (outcome.State == "superseded") return SupersededAnswer(importId, itemId);
                var reason = rememberedReason + "; " + outcome.Reason + (binding["deferred"] is not null ? "; " + Str(binding, "reason") : "");
                return UpdateState(importId, itemId, outcome.State, reason, entry, expectedRevision: revision);
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                return UpdateState(importId, itemId, "failed", error.Message, rememberedId, expectedRevision: revision);
            }
        }
        var englishTitle = row.Text("english_title");
        var (resolution, _) = await ResolveDetailsAsync(item, englishTitle, useMetadata: true, token).ConfigureAwait(false);
        if (Superseded(importId, itemId, revision)) return SupersededAnswer(importId, itemId);
        WriteDiscovery(importId, itemId, revision, null, null, null, item);
        var entryId = resolution.EntryId;
        var resolvedReason = resolution.Reason;
        var reference = resolution.ReferenceEntryId;
        var liveCandidates = new List<long>();
        if (entryId is null)
        {
            progress.Report(1, 4, "Searching enabled sources with backup and metadata titles");
            var discoveryError = "";
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromMinutes(3));
                var live = await LiveSuggestionsAsync(importId, itemId, title, automatic: true, skipBackupReference: true, revision: revision, cancellationToken: limit.Token).ConfigureAwait(false);
                if (live["superseded"] is not null) return SupersededAnswer(importId, itemId);
                liveCandidates.AddRange((live["suggestions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Select(summary => summary["canonical_id"]?.GetValue<long>() ?? 0).Where(id => id > 0));
                if (live["reference_entry_id"] is JsonValue liveReference && liveReference.TryGetValue<long>(out var referenceId)) reference = referenceId;
                if (live["auto_match_entry_id"] is JsonValue autoValue && autoValue.TryGetValue<long>(out var auto))
                {
                    entryId = auto;
                    resolvedReason = Str(live, "auto_match_reason").Length > 0 ? Str(live, "auto_match_reason") : "Compatible live-source match";
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                discoveryError = "Live-source search reached its time limit; partial candidates were retained.";
            }
            if (Superseded(importId, itemId, revision)) return SupersededAnswer(importId, itemId);
            item = ItemPayload(ReadItem(importId, itemId));
            if (entryId is null)
            {
                var (auto, autoReason) = AutoResolveCandidate(item, englishTitle, reference, candidateIds: liveCandidates);
                entryId = auto;
                resolvedReason = auto is not null ? autoReason : (Bundle(item)["matches"] is JsonArray { Count: > 0 } ? "Metadata identified; " : "")
                    + "no safe AutoSourcarr match found." + SourceWarnings(item) + (discoveryError.Length > 0 ? " " + discoveryError : "");
            }
        }
        if (Superseded(importId, itemId, revision)) return SupersededAnswer(importId, itemId);
        if (entryId is not { } chosen)
        {
            var referenceDescription = reference is { } referenceEntry && _ingest.EntrySummaries([referenceEntry]).FirstOrDefault() is { } summary ? Str(summary, "description") : "";
            var ranked = RankedCandidates(item, limit: 8, englishTitle: englishTitle, referenceEntryId: reference, referenceDescription: referenceDescription)
                .Select(pair => pair.EntryId);
            var ids = FilterRejected(item, liveCandidates.Concat(ranked)).Take(24).ToArray();
            Write(c =>
            {
                c.Execute("""
                    UPDATE import_items SET state='unresolved',reason=$reason,candidate_ids_json=$ids,reference_entry_id=$reference,updated_at=$now
                    WHERE import_id=$run AND id=$id AND decision_revision=$rev
                    """, ("$reason", resolvedReason), ("$ids", new JsonArray(ids.Select(id => (JsonNode)id).ToArray()).ToJsonString()), ("$reference", reference),
                    ("$now", Now()), ("$run", importId), ("$id", itemId), ("$rev", revision));
                return RecountIn(c, importId, null);
            });
            return new JsonObject { ["ok"] = false, ["state"] = "unresolved", ["reason"] = resolvedReason, ["entry_id"] = null, ["reference_entry_id"] = reference };
        }
        try
        {
            progress.Report(2, 4, "Binding original source and metadata");
            var binding = await BindBackupSourceAsync(chosen, item, manual: false, token).ConfigureAwait(false);
            if (Superseded(importId, itemId, revision)) return SupersededAnswer(importId, itemId);
            WriteDiscovery(importId, itemId, revision, null, null, null, item);
            var entry = _ingest.CanonicalEntryId(binding["entry_id"]?.GetValue<long>() ?? chosen);
            progress.Report(3, 4, "Adding entry to main Suwayomi library");
            var outcome = await AddToLibraryAsync(entry, title, () => Superseded(importId, itemId, revision), token).ConfigureAwait(false);
            if (outcome.State == "superseded") return SupersededAnswer(importId, itemId);
            var reason = outcome.Reason + (binding["deferred"] is not null ? "; " + Str(binding, "reason") : "");
            var result = UpdateState(importId, itemId, outcome.State, reason, entry, expectedRevision: revision);
            if (result["ok"]?.GetValue<bool>() == true)
                Log("import.match.automatic", DecisionActor.Automatic, [entry], resolvedReason, after: Decision(importId, itemId, revision, item), records: BoundRecord(binding));
            return result;
        }
        catch (ImportPolicyBlockedException error)
        {
            return UpdateState(importId, itemId, "unresolved", error.Message, null, expectedRevision: revision);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            return UpdateState(importId, itemId, "failed", error.Message, chosen, expectedRevision: revision);
        }
    }

    private static string SourceWarnings(JsonObject item)
    {
        if (item[DiagnosticsKey] is not JsonObject diagnostics) return "";
        var messages = diagnostics.Select(pair => pair.Value is JsonObject row ? Str(row, "message") : "").Where(message => message.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (messages.Length == 0) return "";
        return " " + string.Join(" ", messages.Take(2)) + (messages.Length > 2 ? " " + (messages.Length - 2).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more source errors in diagnostics." : "");
    }

    // ---- Reject ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// schedule_import_item_reject: an accepted Added/Existing match is wrong. The rejection is remembered for this backup
    /// identity (and the learned manual match forgotten) at once; the job removes a verified import-added library item that
    /// no other accepted import uses, then searches alternatives.
    /// </summary>
    public JsonObject ScheduleReject(string importId, long itemId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var row = ReadItem(importId, itemId);
        var previousState = row.Text("state");
        if (previousState is not ("added" or "existing") || row["entry_id"] is not long rawEntry)
            throw new ImportConflictException("Only an accepted Added/Existing import match can be rejected");
        var entryId = _ingest.CanonicalEntryId(rawEntry);
        var verifiedAdd = row.Integer("library_add_verified") != 0;
        var item = ItemPayload(row);
        var revision = Claim(importId, itemId) ?? throw new KeyNotFoundException("Import item not found");
        var remembered = RememberRejection(item, entryId);
        ForgetManualAutoresolve(item);
        var rejectedTitle = Str(remembered, "rejected_title");
        Write(c =>
        {
            c.Execute("""
                UPDATE import_items SET state='unresolved',reason=$reason,entry_id=NULL,candidate_ids_json='[]',matched_snapshot_json='{}',library_add_verified=0,
                  reference_entry_id=NULL,reference_title='',reference_description='',reference_thumbnail_url='',suggestion_checked=0,suggestion_state='queued',
                  updated_at=$now WHERE import_id=$run AND id=$id AND decision_revision=$rev
                """, ("$reason", "Rejected previous match: " + rejectedTitle + ". Looking for another match…"), ("$now", Now()), ("$run", importId),
                ("$id", itemId), ("$rev", revision));
            return RecountIn(c, importId, null);
        });
        Log("import.reject", actor, [entryId], "Import match rejected: " + Str(item, "title") + " ≠ " + rejectedTitle, after: Decision(importId, itemId, revision, item));
        var taskId = Launch(RejectJob, ItemKey(importId, itemId) + ":" + revision, "Reject imported match · " + Str(item, "title"), "manual", ItemKey(importId, itemId),
            async progress =>
            {
                var token = progress.CancellationToken;
                if (Superseded(importId, itemId, revision)) { progress.Result("Superseded by a newer import decision"); return; }
                if (previousState == "added" && verifiedAdd)
                {
                    try
                    {
                        var removal = await RemoveFromLibraryAsync(entryId, itemId, () => Superseded(importId, itemId, revision), token).ConfigureAwait(false);
                        progress.Report(1, 2, removal.Reason);
                    }
                    catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                    {
                        progress.Report(1, 2, "Rejected match remembered; library cleanup could not finish: " + error.Message);
                    }
                }
                else progress.Report(1, 2, "Library item preserved: no verified new import addition");
                if (Superseded(importId, itemId, revision)) { progress.Result("Superseded by a newer import decision"); return; }
                try
                {
                    var live = await LiveSuggestionsAsync(importId, itemId, Str(item, "title"), automatic: true, revision: revision, cancellationToken: token).ConfigureAwait(false);
                    var count = (live["suggestions"] as JsonArray)?.Count ?? 0;
                    progress.Result("Rejected match remembered · " + ImportProgress.Count(count, "alternative suggestion") + " prepared");
                }
                catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                {
                    WriteDiscovery(importId, itemId, revision, null, 1, null);
                    progress.Result("Rejected match remembered · alternative search failed: " + error.Message);
                }
            });
        return new JsonObject
        {
            ["decision_revision"] = revision, ["queued"] = true, ["import_id"] = importId, ["item_id"] = itemId, ["state"] = "unresolved", ["entry_id"] = null,
            ["task_id"] = taskId, ["rejected_entry_id"] = entryId, ["rejected_title"] = rejectedTitle,
        };
    }

    /// <summary>_remove_import_added_entry_from_main_library: never removes a work another accepted import row still uses.</summary>
    private async Task<ImportLibraryOutcome> RemoveFromLibraryAsync(long entryId, long itemId, Func<bool> superseded, CancellationToken cancellationToken)
    {
        if (Library is not { } library) return new("preserved", "MAIN_SUWAYOMI_URL is not configured");
        // Rows may still hold an entry id that was merged since: compare canonical ids, not the stored ones.
        bool InUse() => Read(c =>
        {
            var target = CatalogueTitles.Canonical(c, entryId);
            return c.Query("""
                SELECT DISTINCT entry_id FROM import_items WHERE id<>$item AND entry_id IS NOT NULL AND state IN ('added','existing','resolving','retrying')
                """, ("$item", itemId)).Any(row => CatalogueTitles.Canonical(c, row.Integer("entry_id")) == target);
        });
        await _libraryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (superseded()) return new("preserved", "A newer import decision superseded this action");
            if (InUse()) return new("preserved", "Another accepted import still uses this library entry");
            return await library.RemoveAsync(entryId, EntryTitles(entryId, ""), () => superseded() || InUse(), cancellationToken).ConfigureAwait(false);
        }
        finally { _libraryLock.Release(); }
    }

    // ---- Skip ------------------------------------------------------------------------------------------------------

    /// <summary>skip_import_item: a new decision (revision) that any queued Match/Retry of the row then loses to.</summary>
    public JsonObject Skip(string importId, long itemId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var item = ItemPayload(ReadItem(importId, itemId));
        var result = UpdateState(importId, itemId, "skipped", "Skipped by user", null, newDecision: true);
        Log("import.skip", actor, [], "Import skipped: " + Str(item, "title"), after: Decision(importId, itemId, result["decision_revision"]?.GetValue<long>() ?? 0, item));
        return result;
    }
}
