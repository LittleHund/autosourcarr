using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Imports.Backup;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Decisions;

namespace AutoSourcarr.Core.Imports;

/// <summary>
/// The import pass (schedule_backup_import / import_readable_backup), candidate discovery
/// (schedule_import_translation_enrichment without machine translation) and the Suwayomi library plan (CON-3).
/// Existing-first and local-first: subscribed works are recognised without any source search; only unresolved rows
/// look at their original source. Each row is stored at its position, so an interrupted import resumes where it stopped
/// and never redecides a stored row.
/// </summary>
public sealed partial class ImportRuns
{
    /// <summary>The import kind of a Suwayomi library plan run (its rows are already in the main library).</summary>
    public const string SuwayomiKind = "suwayomi";
    private const string RawFolder = "imports";

    /// <summary>
    /// admin_import_mihon_backup: checks the file name and import id, decodes the backup (so a wrong file is refused at
    /// once), keeps the upload under &lt;data&gt;/imports/&lt;id&gt;/ until the import finished and queues the import job. A
    /// repeated upload with the same id answers the earlier receipt.
    /// </summary>
    public JsonObject AcceptUpload(byte[] data, string? filename, string? importId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(data); ArgumentNullException.ThrowIfNull(actor);
        var name = CheckedFilename(filename);
        var id = (importId ?? "").Trim().ToLowerInvariant();
        if (id.Length == 0) id = Guid.NewGuid().ToString("N");
        if (!ImportIdPattern().IsMatch(id)) throw new ArgumentException("Invalid import identity");
        JsonObject SameReceipt(JsonObject existing) =>
            Str(existing, "filename") == name ? existing : throw new ImportConflictException("This import identity already belongs to another filename");
        if (Receipt(id) is { } earlier) return SameReceipt(earlier);
        if (data.Length == 0) throw new BackupDecodeException("Backup file is empty");
        var library = MihonBackupReader.Read(data);
        if (Library is null) throw new ImportUnavailableException("MAIN_SUWAYOMI_URL is not configured");
        // The receipt check above is only the fast path: the claim is atomic, so a concurrent upload with the same id
        // that got here first owns the run, its upload folder and its job.
        if (!TryCreateRun(id, name))
            return Receipt(id) is { } winner ? SameReceipt(winner) : throw new ImportConflictException("This import identity is being created; retry");
        var stored = StoreUpload(id, name, data);
        Log("import.upload", actor, [], "Backup uploaded: " + name, after: new JsonObject
        {
            ["import_id"] = id, ["filename"] = name, ["format"] = library.Format, ["version"] = library.Version, ["manga"] = library.Manga.Count,
            ["favorites"] = library.FavoriteCount,
        });
        var rows = library.Manga.Select(manga => manga.ToJson()).ToList();
        var taskId = Launch(ImportJob, id, "Import Mihon/Tachiyomi backup · " + name, "manual", RunKey(id),
            progress => RunImportJobAsync(id, rows, alreadyInLibrary: false, stored, progress));
        return new JsonObject
        {
            ["queued"] = true, ["task_id"] = taskId, ["import_id"] = id, ["filename"] = name, ["format"] = library.Format,
            ["version"] = library.Version, ["version_label"] = MihonBackupReader.Label(library.Version), ["compression"] = library.Compression,
            ["manga"] = library.Manga.Count, ["favorites"] = library.FavoriteCount,
        };
    }

    /// <summary>The uploaded file name: no control characters, at most 240 UTF-8 bytes, .tachibk, .proto.gz or .gz.</summary>
    public static string CheckedFilename(string? submitted)
    {
        var value = submitted ?? "";
        if (value.Length == 0) value = "backup.tachibk";
        if (value.Any(character => character < 32 || character == 127)) throw new ArgumentException("Invalid backup filename");
        if (Encoding.UTF8.GetByteCount(value) > 240) throw new ArgumentException("Backup filename is too long");
        var name = Path.GetFileName(value.Replace('\\', '/'));
        if (name.Length == 0) name = "backup.tachibk";
        var lowered = name.ToLowerInvariant();
        if (!(lowered.EndsWith(".tachibk", StringComparison.Ordinal) || lowered.EndsWith(".proto.gz", StringComparison.Ordinal) || lowered.EndsWith(".gz", StringComparison.Ordinal)))
            throw new ArgumentException("Expected a .tachibk or .proto.gz backup");
        return name;
    }

    private string? StoreUpload(string importId, string filename, byte[] data)
    {
        if (DataDirectory is not { Length: > 0 } root) return null;
        var folder = Path.Combine(root, RawFolder, importId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "upload.bin");
        Settings.ConfigFiles.WriteAtomic(path, data);
        File.WriteAllText(Path.Combine(folder, "filename.txt"), filename);
        return path;
    }

    private void RemoveUpload(string? path)
    {
        if (path is null) return;
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The import job: runs the pass, marks the run completed / cancelled / failed, queues discovery, drops the upload.</summary>
    private async Task RunImportJobAsync(string importId, IReadOnlyList<JsonObject> rows, bool alreadyInLibrary, string? stored, ImportProgress progress)
    {
        try
        {
            var result = await ImportRowsAsync(importId, rows, alreadyInLibrary, progress).ConfigureAwait(false);
            Recount(importId, "completed");
            ScheduleDiscovery(importId, resumeOnly: false, origin: "manual");
            progress.Result("Added " + Str(result, "added") + " · existing " + Str(result, "existing") + " · unresolved " + Str(result, "unresolved")
                + " · failed " + Str(result, "failed"));
            RemoveUpload(stored);
        }
        catch (OperationCanceledException)
        {
            // Only a person's Cancel ends the run. A shutdown (JobQueue.DisposeAsync), a maintenance pause or a watchdog
            // stop keeps it "running" with its upload, so startup recovery (or the retry) resumes it from the stored rows.
            if (progress.CancelledByUser)
            {
                Recount(importId, "cancelled");
                RemoveUpload(stored);
            }
            else Recount(importId, "running");
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Recount(importId, "failed");
            RemoveUpload(stored);
            throw;
        }
    }

    private sealed record ExistingIndex(Dictionary<string, HashSet<long>> Titles, Dictionary<string, HashSet<long>> Urls);

    /// <summary>_backup_existing_index: URL and title lookups over the subscribed entries.</summary>
    private ExistingIndex BuildExistingIndex(IEnumerable<long> entryIds)
    {
        var titles = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        var urls = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        var ids = _ingest.CanonicalEntryIds(entryIds).Distinct().ToArray();
        if (ids.Length == 0) return new(titles, urls);
        void Add(Dictionary<string, HashSet<long>> map, string key, long id)
        {
            if (key.Length == 0) return;
            if (!map.TryGetValue(key, out var set)) map[key] = set = [];
            set.Add(id);
        }
        Read(c =>
        {
            var json = System.Text.Json.JsonSerializer.Serialize(ids);
            foreach (var row in c.Query("SELECT id,preferred_title FROM entries WHERE id IN (SELECT value FROM json_each($ids))", ("$ids", json)))
                Add(titles, TextMatcher.NormalizeTitle(row.Text("preferred_title")), row.Integer("id"));
            foreach (var row in c.Query("SELECT entry_id,title FROM titles WHERE entry_id IN (SELECT value FROM json_each($ids))", ("$ids", json)))
                Add(titles, TextMatcher.NormalizeTitle(row.Text("title")), row.Integer("entry_id"));
            foreach (var row in c.Query("""
                SELECT entry_id,source_id,source_manga_url,source_title FROM source_matches WHERE active=1 AND entry_id IN (SELECT value FROM json_each($ids))
                UNION ALL SELECT entry_id,source_id,source_manga_url,source_title FROM source_variants WHERE active=1 AND entry_id IN (SELECT value FROM json_each($ids))
                """, ("$ids", json)))
            {
                Add(titles, TextMatcher.NormalizeTitle(row.Text("source_title")), row.Integer("entry_id"));
                var url = ImportQueries.BackupUrlKey(row.Text("source_manga_url"));
                if (url.Length > 0) Add(urls, row.Text("source_id") + "\n" + url, row.Integer("entry_id"));
            }
            return 0;
        });
        return new(titles, urls);
    }

    /// <summary>_backup_existing_from_index: a subscribed entry by exact source URL, or one exact title that passes the gate.</summary>
    private (long? EntryId, string Reason) ExistingFromIndex(JsonObject item, ExistingIndex index)
    {
        var sourceId = CurrentSourceId(item);
        var url = ImportQueries.BackupUrlKey(Str(item, "url"));
        if (sourceId.Length > 0 && url.Length > 0 && index.Urls.TryGetValue(sourceId + "\n" + url, out var byUrl))
        {
            var hits = FilterRejected(item, byUrl);
            if (hits.Count == 1) return (hits[0], "existing exact source URL");
        }
        var title = TextMatcher.NormalizeTitle(Str(item, "title"));
        if (title.Length > 0 && index.Titles.TryGetValue(title, out var byTitle))
        {
            var hits = FilterRejected(item, byTitle);
            if (hits.Count == 1 && CandidateEvidence(item, hits[0]).Eligible) return (hits[0], "existing exact known title");
        }
        return (null, "");
    }

    /// <summary>The library step of a row: already-subscribed plan rows only get their catalogue entry.</summary>
    private async Task<ImportLibraryOutcome> LibraryStepAsync(long entryId, JsonObject item, Func<bool> superseded, CancellationToken cancellationToken)
    {
        if (Str(item, KindKey) == SuwayomiKind) return new("existing", "In the main Suwayomi library; catalogue entry linked");
        return await AddToLibraryAsync(entryId, Str(item, "title"), superseded, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// import_readable_backup: for each library row not stored yet: a remembered manual match, else a subscribed entry
    /// (no source search), else the local/source resolution; accepted rows are bound to their original source and added
    /// to the main library. Policy refusals stay unresolved, errors become failed rows; nothing aborts the pass.
    /// </summary>
    internal async Task<JsonObject> ImportRowsAsync(string importId, IReadOnlyList<JsonObject> rows, bool alreadyInLibrary, ImportProgress progress)
    {
        var token = progress.CancellationToken;
        var items = rows.Where(Favorite).ToList();
        var stored = Write(c =>
        {
            c.Execute("UPDATE import_runs SET total=$total,state='running' WHERE id=$id", ("$total", items.Count), ("$id", importId));
            return c.QueryAs<long>("SELECT position FROM import_items WHERE import_id=@importId", new { importId }).ToHashSet();
        });
        progress.Report(0, items.Count, "Decoded " + ImportProgress.Count(items.Count, "library entry"));
        IReadOnlySet<long> subscribed = alreadyInLibrary ? new HashSet<long>()
            : await (Library ?? throw new ImportUnavailableException("MAIN_SUWAYOMI_URL is not configured")).SubscribedAsync(token).ConfigureAwait(false);
        var already = subscribed.ToHashSet();
        var index = BuildExistingIndex(already);
        int added = 0, existing = 0, unresolved = 0, failed = 0;
        for (var position = 1; position <= items.Count; position++)
        {
            token.ThrowIfCancellationRequested();
            if (stored.Contains(position)) continue;
            var item = items[position - 1];
            var title = Str(item, "title").Trim().Length > 0 ? Str(item, "title").Trim() : "Backup entry " + position.ToString(CultureInfo.InvariantCulture);
            var state = "unresolved"; var reason = ""; long? entryId = null; long? reference = null; var automatic = true;
            string[] bound = [];
            try
            {
                var (remembered, rememberedReason) = ManualAutoresolveEntry(item, DecisionActor.Automatic);
                if (remembered is { } rememberedId)
                {
                    automatic = false;
                    var binding = await BindBackupSourceAsync(rememberedId, item, manual: true, token).ConfigureAwait(false);
                    var canonical = _ingest.CanonicalEntryId(binding["entry_id"]?.GetValue<long>() ?? rememberedId);
                    entryId = canonical; reference = canonical;
                    var outcome = await LibraryStepAsync(canonical, item, () => false, token).ConfigureAwait(false);
                    state = outcome.State; reason = rememberedReason + "; " + outcome.Reason;
                }
                else if (ExistingFromIndex(item, index) is ({ } existingId, var existingReason))
                {
                    var binding = await BindBackupSourceAsync(existingId, item, manual: false, token).ConfigureAwait(false);
                    bound = BoundRecord(binding);
                    var canonical = _ingest.CanonicalEntryId(binding["entry_id"]?.GetValue<long>() ?? existingId);
                    entryId = canonical; reference = canonical;
                    state = "existing"; reason = existingReason;
                }
                else
                {
                    var (resolution, _) = await ResolveDetailsAsync(item, "", useMetadata: false, token).ConfigureAwait(false);
                    reason = resolution.Reason; reference = resolution.ReferenceEntryId;
                    if (resolution.EntryId is { } resolved)
                    {
                        var binding = await BindBackupSourceAsync(resolved, item, manual: false, token).ConfigureAwait(false);
                        bound = BoundRecord(binding);
                        var canonical = _ingest.CanonicalEntryId(binding["entry_id"]?.GetValue<long>() ?? resolved);
                        entryId = canonical;
                        reference ??= canonical;
                        if (already.Contains(canonical)) { state = "existing"; reason = "Already in main Suwayomi library"; }
                        else
                        {
                            var outcome = await LibraryStepAsync(canonical, item, () => false, token).ConfigureAwait(false);
                            state = outcome.State; reason = resolution.Reason + "; " + outcome.Reason;
                            already.Add(canonical);
                            var fresh = BuildExistingIndex([canonical]);
                            foreach (var (key, ids) in fresh.Titles) { if (!index.Titles.TryGetValue(key, out var set)) index.Titles[key] = set = []; set.UnionWith(ids); }
                            foreach (var (key, ids) in fresh.Urls) { if (!index.Urls.TryGetValue(key, out var set)) index.Urls[key] = set = []; set.UnionWith(ids); }
                        }
                    }
                }
            }
            catch (ImportPolicyBlockedException error) { state = "unresolved"; entryId = null; reason = error.Message; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { state = "failed"; reason = error.Message; }
            switch (state)
            {
                case "added": added++; break;
                case "existing": existing++; break;
                case "failed": failed++; break;
                default: unresolved++; break;
            }
            var itemId = StoreItem(importId, position, item, state, reason, entryId, reference);
            if (entryId is { } accepted && state is "added" or "existing" && automatic)
                Log("import.match.automatic", DecisionActor.Automatic, [accepted], reason, after: Decision(importId, itemId, 0, item), records: bound);
            if (position % 20 == 0 || position == items.Count) Recount(importId);
            progress.Report(position, items.Count, "Added " + added.ToString(CultureInfo.InvariantCulture) + " · existing " + existing.ToString(CultureInfo.InvariantCulture)
                + " · unresolved " + unresolved.ToString(CultureInfo.InvariantCulture) + " · failed " + failed.ToString(CultureInfo.InvariantCulture) + " · " + title);
        }
        Recount(importId);
        return new JsonObject
        {
            ["backup_manga"] = rows.Count, ["library_entries"] = items.Count, ["added"] = added, ["existing"] = existing, ["skipped_existing"] = existing,
            ["unresolved"] = unresolved, ["failed"] = failed,
        };
    }

    // ---- discovery -------------------------------------------------------------------------------------------------

    /// <summary>
    /// schedule_import_translation_enrichment (without machine translation, phase 10): finds candidates for every
    /// unresolved row of a run and accepts a row only through the strict gate. A row whose decision changed meanwhile is
    /// left alone. Answers the task id, or null when nothing needs discovery.
    /// </summary>
    public string? ScheduleDiscovery(string importId, bool resumeOnly, string origin = "automatic")
    {
        var rows = Write(c =>
        {
            var clause = resumeOnly ? " AND suggestion_checked=0 AND suggestion_state IN ('queued','searching','found','local','pending')" : "";
            var found = c.Query("SELECT id,decision_revision FROM import_items WHERE import_id=$run AND state IN ('unresolved','failed')" + clause + " ORDER BY position",
                ("$run", importId)).Select(row => (Id: row.Integer("id"), Revision: row.Integer("decision_revision"))).ToList();
            c.Execute("UPDATE import_items SET suggestion_checked=0,suggestion_state='queued' WHERE import_id=$run AND state IN ('unresolved','failed')" + clause, ("$run", importId));
            return found;
        });
        if (rows.Count == 0) return null;
        return Launch(DiscoveryJob, importId, "Find import match candidates", origin, null, async progress =>
        {
            var token = progress.CancellationToken;
            var done = 0; var accepted = 0;
            try
            {
                foreach (var (itemId, revision) in rows)
                {
                    token.ThrowIfCancellationRequested();
                    progress.Report(done, rows.Count, "Searching candidates");
                    if (Superseded(importId, itemId, revision)) { done++; continue; }
                    WriteDiscovery(importId, itemId, revision, null, null, "searching");
                    try
                    {
                        var live = await LiveSuggestionsAsync(importId, itemId, "", automatic: true, revision: revision, cancellationToken: token).ConfigureAwait(false);
                        if (live["auto_match_entry_id"] is JsonValue value && value.TryGetValue<long>(out var auto)
                            && await AcceptAutomaticAsync(importId, itemId, auto, Str(live, "auto_match_reason"), revision, token).ConfigureAwait(false))
                            accepted++;
                    }
                    catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                    {
                        WriteDiscovery(importId, itemId, revision, null, 1, "done", reason: "Candidate search failed: " + error.Message);
                    }
                    done++;
                }
                progress.Result("Checked " + ImportProgress.Count(done, "unresolved import") + "; " + ImportProgress.Count(accepted, "safe match") + " accepted");
            }
            catch (OperationCanceledException)
            {
                PauseDiscovery(importId);
                throw;
            }
        });
    }

    /// <summary>_pause_import_discovery: a stopped discovery is not silently restarted for rows still waiting.</summary>
    private void PauseDiscovery(string importId) => Write(c => c.Execute("""
        UPDATE import_items SET suggestion_state='paused',updated_at=$now WHERE import_id=$run AND state IN ('unresolved','failed') AND suggestion_checked=0
          AND suggestion_state IN ('queued','searching','found','local','pending')
        """, ("$now", Now()), ("$run", importId)));

    /// <summary>A strict automatic acceptance found by discovery: claims the row only if nobody decided since (expected revision).</summary>
    private async Task<bool> AcceptAutomaticAsync(string importId, long itemId, long entryId, string reason, long expectedRevision, CancellationToken cancellationToken)
    {
        // Provider ids on both sides that disagree: never an automatic acceptance, whichever path proposed it.
        if (IdentityConflict(ItemPayload(ReadItem(importId, itemId)), entryId) is not null) return false;
        if (Claim(importId, itemId, expectedRevision) is not { } revision) return false;
        var item = ItemPayload(ReadItem(importId, itemId));
        try
        {
            var binding = await BindBackupSourceAsync(entryId, item, manual: false, cancellationToken).ConfigureAwait(false);
            if (Superseded(importId, itemId, revision)) return false;
            var entry = _ingest.CanonicalEntryId(binding["entry_id"]?.GetValue<long>() ?? entryId);
            var outcome = await LibraryStepAsync(entry, item, () => Superseded(importId, itemId, revision), cancellationToken).ConfigureAwait(false);
            if (outcome.State == "superseded") return false;
            var result = UpdateState(importId, itemId, outcome.State, reason + "; " + outcome.Reason, entry, expectedRevision: revision);
            if (result["ok"]?.GetValue<bool>() != true) return false;
            Log("import.match.automatic", DecisionActor.Automatic, [entry], reason, after: Decision(importId, itemId, revision, item), records: BoundRecord(binding));
            return true;
        }
        catch (ImportPolicyBlockedException error)
        {
            UpdateState(importId, itemId, "unresolved", error.Message, null, expectedRevision: revision);
            return false;
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            UpdateState(importId, itemId, "failed", error.Message, entryId, expectedRevision: revision);
            return false;
        }
    }

    // ---- Suwayomi library plan (CON-3) -----------------------------------------------------------------------------

    /// <summary>One plan row as an import row (kind "suwayomi").</summary>
    public static JsonObject PlanRow(SuwayomiImportItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new JsonObject
        {
            ["source"] = row.SourceId, ["url"] = row.Url, ["title"] = row.Title, ["thumbnailUrl"] = row.ThumbnailUrl, ["favorite"] = true,
            ["sourceName"] = row.SourceName, ["suwayomi_manga_id"] = row.MangaId, ["realUrl"] = row.RealUrl, [KindKey] = SuwayomiKind,
        };
    }

    /// <summary>The run id of a plan: stable per plan (server and capture time), so a repeated request resumes the same run.</summary>
    public static string PlanImportId(SuwayomiImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("suwayomi-plan\n" + plan.ServerUrl + "\n" + plan.CreatedAt)))[..32];
    }

    /// <summary>
    /// Imports the stored Suwayomi library plan (phase 7's "awaiting-matching") through the same pipeline: catalogue entries
    /// are created or matched for every row; nothing is added to the main library (the rows are its own). When the run
    /// finishes, the plan is saved with status "imported".
    /// </summary>
    public JsonObject ImportSuwayomiPlan(DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var root = DataDirectory ?? throw new ImportUnavailableException("The data directory is not available in this host.");
        var plan = SuwayomiImport.Load(root) ?? throw new KeyNotFoundException("No Suwayomi library plan is stored yet. Run the Suwayomi connection import first.");
        var id = PlanImportId(plan);
        var name = "Suwayomi library (" + plan.ServerUrl + ")";
        if (Receipt(id) is { } existing && Str(existing, "state") == "running")
        {
            existing["already_running"] = true;
            return existing;
        }
        CreateRun(id, name);
        Log("import.suwayomi-plan", actor, [], "Suwayomi library plan imported into the catalogue", after: new JsonObject
        {
            ["import_id"] = id, ["server_url"] = plan.ServerUrl, ["created_at"] = plan.CreatedAt, ["manga"] = plan.Total,
        });
        var rows = plan.Items.Select(PlanRow).ToList();
        var taskId = Launch(ImportJob, id, "Import the Suwayomi library plan", "manual", RunKey(id), async progress =>
        {
            await RunImportJobAsync(id, rows, alreadyInLibrary: true, null, progress).ConfigureAwait(false);
            SuwayomiImport.Save(root, plan with { Status = SuwayomiImport.Imported });
        });
        return new JsonObject
        {
            ["queued"] = true, ["task_id"] = taskId, ["import_id"] = id, ["filename"] = name, ["manga"] = plan.Total, ["status"] = plan.Status,
            ["note"] = SuwayomiImport.ImportedNote,
        };
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ImportIdPattern();
}
