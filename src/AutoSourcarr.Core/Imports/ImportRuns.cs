using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Matching.Manual;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Imports;

/// <summary>An import action that conflicts with the item's state (answered 409).</summary>
public sealed class ImportConflictException(string message) : InvalidOperationException(message);

/// <summary>The import needs a part of the app this host does not run (answered 503).</summary>
public sealed class ImportUnavailableException(string message) : InvalidOperationException(message);

/// <summary>A matching rule or a manual "different work" decision refuses this binding (the item stays unresolved).</summary>
public sealed class ImportPolicyBlockedException(string message) : InvalidOperationException(message);

/// <summary>
/// Phase 9 "Imports → ImportRuns / ImportResolution": Mihon/Tachiyomi backup imports and the Suwayomi library plan.
/// Rows live in import_runs / import_items (0.4.4 schema); the user's decisions in import_match_rejections and
/// import_manual_autoresolve survive history clears and catalogue resets and change only on their explicit reset.
/// Every item action carries a decision revision: Match, Search (live suggestions), Skip and Retry claim a new revision,
/// so the latest click wins and an older queued action that wakes up later changes nothing (IMP-5). Slow work runs as
/// jobs; a crash leaves rows in resolving/retrying which <see cref="RecoverInterrupted"/> returns to a resumable state.
/// </summary>
public sealed partial class ImportRuns
{
    /// <summary>States an item can be in.</summary>
    public static readonly IReadOnlyList<string> States = ["unresolved", "resolving", "retrying", "added", "existing", "failed", "skipped"];
    public static readonly IReadOnlyList<string> ReviewFilters = ["all", "has_candidates", "no_candidates", "searching", "has_reference", "no_reference", "has_english", "no_english"];
    internal const string DiagnosticsKey = "_autosourcarr_source_diagnostics";
    internal const string MetadataKey = "_autosourcarr_metadata_identity";
    internal const string KindKey = "_autosourcarr_import_kind";

    public static readonly JobType ImportJob = new("backup-import", "Import Mihon/Tachiyomi backup", JobPriority.Normal, JobFlags.LongRunning,
        StallTimeout: TimeSpan.FromMinutes(30));
    public static readonly JobType ResolveJob = new("import-resolve", "Resolve imported entry", JobPriority.High, RunLimit: 2,
        StallTimeout: TimeSpan.FromMinutes(10));
    public static readonly JobType RejectJob = new("import-reject", "Reject imported match", JobPriority.High, StallTimeout: TimeSpan.FromMinutes(10));
    public static readonly JobType DiscoveryJob = new("import-discovery", "Find import match candidates", JobPriority.Low, JobFlags.LongRunning,
        StallTimeout: TimeSpan.FromMinutes(30));
    public static readonly JobType RecoveryJob = new("import-recovery", "Resume interrupted import work", JobPriority.Normal, StallTimeout: TimeSpan.FromMinutes(10));

    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly IDecisionLog _log;
    private readonly EntryIngest _ingest;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Task> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DatabaseRow> _restorePoints = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _ingestLock = new(1, 1);
    private readonly SemaphoreSlim _libraryLock = new(1, 1);

    public ImportRuns(SqliteDatabase database, object sourceWriteGate, IDecisionLog log, EntryIngest ingest, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate);
        ArgumentNullException.ThrowIfNull(log); ArgumentNullException.ThrowIfNull(ingest);
        _database = database; _gate = sourceWriteGate; _log = log; _ingest = ingest; _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Manual bindings (_persist_manual_binding) for remembered manual import matches; null = not persisted there.</summary>
    public ManualDecisions? Manual { get; init; }
    /// <summary>The main Suwayomi library; null = adding answers 503 and imports cannot start.</summary>
    public IImportLibrary? Library { get; init; }
    /// <summary>Live source search; null = only the local catalogue is used.</summary>
    public IImportSourceSearch? Sources { get; init; }
    /// <summary>External metadata catalogues; null = no metadata identity evidence.</summary>
    public IImportMetadataSearch? Metadata { get; init; }
    /// <summary>Identity-link writer for accepted metadata; null = metadata stays evidence only.</summary>
    public IImportIdentitySink? Identities { get; init; }
    /// <summary>
    /// Phase 10: the candidates (of those passed) whose covers decode to the backup row's exact cover pixels (item,
    /// metadata bundle, candidate ids); null = no thumbnail tie-break. Off when EXACT_THUMBNAIL_MATCHING is off.
    /// </summary>
    public Func<JsonObject, JsonObject?, IReadOnlyList<long>, CancellationToken, Task<IReadOnlyList<long>>>? ExactThumbnails { get; init; }
    /// <summary>The job queue (Tasks); null = work runs on the thread pool (tests).</summary>
    public JobQueue? Jobs { get; init; }
    public Func<ImportOptions> Options { get; init; } = () => new ImportOptions();
    /// <summary>Where uploaded backups wait until their import finished (&lt;data&gt;/imports/&lt;id&gt;/).</summary>
    public string? DataDirectory { get; init; }

    private string Now() => DecisionStamp.Now(_clock);
    private T Read<T>(Func<DatabaseSession, T> operation) => _database.Read(DatabaseFile.Catalog, operation);
    private T Write<T>(Func<DatabaseSession, T> operation) { lock (_gate) return _database.Write(DatabaseFile.Catalog, operation); }
    private static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string ItemKey(string importId, long itemId) => importId + ":" + Id(itemId);

    private void Log(string kind, DecisionActor actor, IEnumerable<long> entries, string reason, JsonObject? before = null, JsonObject? after = null,
        IEnumerable<string>? records = null) =>
        _log.Record(new DecisionEvent(kind, actor, entries.Where(id => id > 0).Distinct().ToArray(),
            (records ?? []).Where(record => record.Length > 0).Distinct(StringComparer.Ordinal).ToArray(), reason, before, after));

    /// <summary>"source_id:source_manga_id" of the record a binding attached (empty when none was).</summary>
    private static string[] BoundRecord(JsonObject binding) =>
        Str(binding, "source_id") is { Length: > 0 } source && binding["source_manga_id"] is JsonValue manga && manga.TryGetValue<long>(out var mangaId)
            ? [source + ":" + Id(mangaId)] : [];

    /// <summary>Completes when the item's last scheduled action finished (tests and the recovery pass).</summary>
    public Task Settled(string importId, long itemId)
    {
        var owner = ItemKey(importId, itemId);
        return Task.WhenAll(_running.Where(pair => RunningOwner(pair.Key) == owner).Select(pair => pair.Value).ToArray());
    }

    /// <summary>The item or run a launch belongs to (the part of its running key before '#').</summary>
    private static string RunningOwner(string key) => key.IndexOf('#', StringComparison.Ordinal) is var cut and >= 0 ? key[..cut] : key;

    /// <summary>Completes when every import task of this instance finished, including work those tasks queued meanwhile.</summary>
    public async Task Idle()
    {
        while (_running.Values.Where(task => !task.IsCompleted).ToArray() is { Length: > 0 } pending)
            await Task.WhenAll(pending).ConfigureAwait(false);
    }

    // ---- runs ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Claims a new import identity in one write: the run row is created only when no run with this id exists, so two
    /// uploads with the same X-Import-Id create one run and one job (the loser answers the winner's receipt).
    /// </summary>
    internal bool TryCreateRun(string importId, string filename) => Write(c => c.Execute("""
        INSERT INTO import_runs(id,filename,state,total,added,existing,unresolved,failed,skipped,created_at,completed_at)
        VALUES($id,$file,'running',0,0,0,0,0,0,$now,'')
        ON CONFLICT(id) DO NOTHING
        """, ("$id", importId), ("$file", filename.Length > 0 ? filename : "backup.tachibk"), ("$now", Now())) > 0);

    /// <summary>_create_import_run.</summary>
    public void CreateRun(string importId, string filename) => Write(c => c.Execute("""
        INSERT INTO import_runs(id,filename,state,total,added,existing,unresolved,failed,skipped,created_at,completed_at)
        VALUES($id,$file,'running',0,0,0,0,0,0,$now,'')
        ON CONFLICT(id) DO UPDATE SET filename=excluded.filename,state='running',completed_at=''
        """, ("$id", importId), ("$file", filename.Length > 0 ? filename : "backup.tachibk"), ("$now", Now())));

    /// <summary>_recount_import_run: counters from the item states; resolving/retrying still count as unresolved.</summary>
    public JsonObject Recount(string importId, string? state = null) => Write(c => RecountIn(c, importId, state));

    private JsonObject RecountIn(DatabaseSession c, string importId, string? state)
    {
        var counts = c.Query("SELECT state,COUNT(*) n FROM import_items WHERE import_id=$id GROUP BY state", ("$id", importId))
            .ToDictionary(row => row.Text("state"), row => row.Integer("n"), StringComparer.Ordinal);
        long Count(string name) => counts.TryGetValue(name, out var value) ? value : 0;
        var total = counts.Values.Sum();
        var unresolved = Count("unresolved") + Count("resolving") + Count("retrying");
        var current = c.Scalar("SELECT state FROM import_runs WHERE id=$id", ("$id", importId)) as string;
        var runState = state ?? current ?? "running";
        var completed = runState is "completed" or "failed" or "cancelled" ? Now() : "";
        c.Execute("""
            UPDATE import_runs SET state=$state,total=$total,added=$added,existing=$existing,unresolved=$unresolved,failed=$failed,skipped=$skipped,
              completed_at=CASE WHEN $done<>'' THEN $done ELSE completed_at END WHERE id=$id
            """, ("$state", runState), ("$total", total), ("$added", Count("added")), ("$existing", Count("existing")), ("$unresolved", unresolved),
            ("$failed", Count("failed")), ("$skipped", Count("skipped")), ("$done", completed), ("$id", importId));
        return new JsonObject
        {
            ["id"] = importId, ["state"] = runState, ["total"] = total, ["added"] = Count("added"), ["existing"] = Count("existing"),
            ["unresolved"] = unresolved, ["failed"] = Count("failed"), ["skipped"] = Count("skipped"),
        };
    }

    /// <summary>list_import_runs (newest first, 1-100).</summary>
    public JsonObject ListRuns(int limit = 20) => Read(c => new JsonObject
    {
        ["runs"] = new JsonArray(c.Query("SELECT * FROM import_runs ORDER BY created_at DESC LIMIT $limit", ("$limit", Math.Clamp(limit, 1, 100)))
            .Select(row => (JsonNode)RowJson(row)).ToArray()),
    });

    /// <summary>The receipt of an accepted upload (the run row), or null.</summary>
    public JsonObject? Receipt(string importId) => Read(c =>
    {
        var row = c.Query("SELECT id,filename,state FROM import_runs WHERE id=$id", ("$id", importId)).FirstOrDefault();
        return row is null ? null : new JsonObject { ["queued"] = true, ["import_id"] = row.Text("id"), ["filename"] = row.Text("filename"), ["state"] = row.Text("state") };
    });

    /// <summary>clear_import_history: deletes finished runs and their items; running imports and every remembered decision stay.</summary>
    public JsonObject ClearHistory(DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var result = Write(c =>
        {
            var runs = c.QueryAs<string>("SELECT id FROM import_runs WHERE state<>'running'").ToArray();
            var running = Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM import_runs WHERE state='running'"), CultureInfo.InvariantCulture);
            if (runs.Length == 0) return new JsonObject { ["runs_cleared"] = 0, ["items_cleared"] = 0, ["running_preserved"] = running };
            var ids = JsonSerializer.Serialize(runs);
            var items = Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM import_items WHERE import_id IN (SELECT value FROM json_each($ids))", ("$ids", ids)), CultureInfo.InvariantCulture);
            c.Execute("DELETE FROM import_items WHERE import_id IN (SELECT value FROM json_each($ids))", ("$ids", ids));
            c.Execute("DELETE FROM import_runs WHERE id IN (SELECT value FROM json_each($ids))", ("$ids", ids));
            Log("import.history.clear", actor, [], "Import history cleared; remembered import decisions kept", after: new JsonObject { ["runs"] = runs.Length, ["items"] = items });
            return new JsonObject { ["runs_cleared"] = runs.Length, ["items_cleared"] = items, ["running_preserved"] = running, ["cleared_ids"] = new JsonArray(runs.Select(run => (JsonNode?)JsonValue.Create(run)).ToArray()) };
        });
        // The kept uploads of the cleared runs (a cancelled or failed run never resumes) go with their history.
        string[] cleared = result["cleared_ids"] is JsonArray clearedIds ? clearedIds.Select(item => item?.GetValue<string>() ?? "").ToArray() : [];
        result.Remove("cleared_ids");
        if (DataDirectory is { Length: > 0 } root)
            foreach (var importId in cleared)
                if (importId.Length > 0 && ImportIdPattern().IsMatch(importId)) RemoveUpload(Path.Combine(root, RawFolder, importId, "upload.bin"));
        return result;
    }

    // ---- rows ------------------------------------------------------------------------------------------------------

    internal static JsonObject RowJson(DatabaseRow row)
    {
        var result = new JsonObject();
        foreach (var (column, value) in row) result[column] = Node(value);
        return result;
    }

    private static JsonNode? Node(object? value) => value switch
    {
        null => null,
        long number => number,
        int number => number,
        double number => number,
        string text => text,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>The backup row of an item (raw_json; the visible columns when it cannot be read).</summary>
    internal static JsonObject ItemPayload(DatabaseRow row)
    {
        try
        {
            if (JsonNode.Parse(row.Text("raw_json", "{}")) is JsonObject parsed) return parsed;
        }
        catch (JsonException) { }
        return new JsonObject { ["title"] = row.Text("title"), ["source"] = row.Text("backup_source"), ["url"] = row.Text("source_url") };
    }

    internal static string Str(JsonObject item, string key)
    {
        if (item[key] is not JsonValue value) return "";
        if (value.TryGetValue<string>(out var text)) return text;
        return value.GetValueKind() == JsonValueKind.Number ? value.ToJsonString() : "";
    }

    internal static bool Favorite(JsonObject item) => !(item["favorite"] is JsonValue value && value.TryGetValue<bool>(out var flag) && !flag);

    private DatabaseRow ReadItem(string importId, long itemId) =>
        Read(c => c.Query("SELECT * FROM import_items WHERE import_id=$run AND id=$id", ("$run", importId), ("$id", itemId)).FirstOrDefault())
        ?? throw new KeyNotFoundException("Import item not found");

    /// <summary>_import_match_snapshot: a small durable card of the accepted entry.</summary>
    internal JsonObject Snapshot(long entryId)
    {
        var canonical = _ingest.CanonicalEntryId(entryId);
        var summary = _ingest.EntrySummaries([canonical]).FirstOrDefault();
        var title = summary is null ? "Entry " + Id(canonical) : Str(summary, "preferred_title");
        return new JsonObject
        {
            ["id"] = summary?["id"]?.DeepClone() ?? (JsonNode)canonical, ["canonical_id"] = canonical,
            ["preferred_title"] = title.Length > 0 ? title : "Entry " + Id(canonical),
            ["sources"] = summary?["sources"] is JsonArray sources ? new JsonArray(sources.Take(12).Select(node => node?.DeepClone()).ToArray()) : new JsonArray(),
            ["description"] = summary is null ? "" : Str(summary, "description"),
            ["author"] = summary is null ? "" : Str(summary, "author"),
            ["artist"] = summary is null ? "" : Str(summary, "artist"),
            ["cover_url"] = "/api/v1/covers/" + Id(canonical),
        };
    }

    /// <summary>_store_import_item: one backup row at its position (an upsert, so a resumed import continues where it stopped).</summary>
    internal long StoreItem(string importId, int position, JsonObject item, string state, string reason, long? entryId,
        long? referenceEntryId = null, string suggestionState = "pending")
    {
        var sourceId = CurrentSourceId(item);
        string referenceTitle = "", referenceDescription = "", referenceCover = "";
        if (referenceEntryId is { } reference && _ingest.EntrySummaries([reference]).FirstOrDefault() is { } summary)
        {
            referenceTitle = Str(summary, "preferred_title"); referenceDescription = Str(summary, "description"); referenceCover = Str(summary, "cover_url");
        }
        var snapshot = entryId is { } chosen && state is "added" or "existing" ? Snapshot(chosen) : new JsonObject();
        return Write(c =>
        {
            c.Execute("""
                INSERT INTO import_items(import_id,position,title,backup_source,source_id,source_url,state,reason,entry_id,raw_json,candidate_ids_json,
                  english_title,reference_entry_id,reference_title,reference_description,reference_thumbnail_url,translation_checked,suggestion_checked,
                  suggestion_state,matched_snapshot_json,updated_at,library_add_verified)
                VALUES($run,$position,$title,$backup,$source,$url,$state,$reason,$entry,$raw,'[]','',$reference,$refTitle,$refDescription,$refCover,0,0,
                  $suggestion,$snapshot,$now,$verified)
                ON CONFLICT(import_id,position) DO UPDATE SET title=excluded.title,backup_source=excluded.backup_source,source_id=excluded.source_id,
                  source_url=excluded.source_url,state=excluded.state,reason=excluded.reason,entry_id=excluded.entry_id,raw_json=excluded.raw_json,
                  reference_entry_id=excluded.reference_entry_id,reference_title=excluded.reference_title,reference_description=excluded.reference_description,
                  reference_thumbnail_url=excluded.reference_thumbnail_url,suggestion_state=excluded.suggestion_state,
                  matched_snapshot_json=excluded.matched_snapshot_json,updated_at=excluded.updated_at,library_add_verified=excluded.library_add_verified
                """, ("$run", importId), ("$position", position), ("$title", Str(item, "title")), ("$backup", Str(item, "source")), ("$source", sourceId),
                ("$url", Str(item, "url")), ("$state", state), ("$reason", reason), ("$entry", entryId), ("$raw", item.ToJsonString()),
                ("$reference", referenceEntryId), ("$refTitle", referenceTitle), ("$refDescription", referenceDescription), ("$refCover", referenceCover),
                ("$suggestion", suggestionState), ("$snapshot", snapshot.ToJsonString()), ("$now", Now()), ("$verified", state == "added" ? 1 : 0));
            return Convert.ToInt64(c.Scalar("SELECT id FROM import_items WHERE import_id=$run AND position=$position", ("$run", importId), ("$position", position)),
                CultureInfo.InvariantCulture);
        });
    }

    /// <summary>_import_live_id_map: candidate ids to their current canonical entry (missing ones left out).</summary>
    private Dictionary<long, long> LiveIds(IEnumerable<long> values)
    {
        var requested = values.Where(id => id > 0).Distinct().ToArray();
        if (requested.Length == 0) return [];
        return Read(c =>
        {
            var result = new Dictionary<long, long>();
            foreach (var id in requested)
            {
                var canonical = CatalogueTitles.Canonical(c, id);
                if (c.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", canonical)) is not null) result[id] = canonical;
            }
            return result;
        });
    }

    internal static IReadOnlyList<long> CandidateIds(DatabaseRow row)
    {
        try
        {
            if (JsonNode.Parse(row.Text("candidate_ids_json", "[]")) is not JsonArray values) return [];
            return values.Select(value => value is JsonValue number && number.TryGetValue<long>(out var id) ? id : 0).Where(id => id > 0).Distinct().ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static Dictionary<string, bool> ReviewFlags(DatabaseRow row, IReadOnlyDictionary<long, long> live)
    {
        var reviewable = row.Text("state") is "unresolved" or "failed";
        var candidates = CandidateIds(row).Any(live.ContainsKey);
        var reference = row["reference_entry_id"] is long referenceId && live.ContainsKey(referenceId);
        var english = row.Text("english_title").Trim();
        var hasEnglish = english.Length > 0 && !string.Equals(english, row.Text("title").Trim(), StringComparison.OrdinalIgnoreCase);
        var checkedRow = row.Integer("suggestion_checked") > 0;
        var paused = row.Text("suggestion_state") == "paused";
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["all"] = true, ["has_candidates"] = reviewable && candidates, ["no_candidates"] = reviewable && checkedRow && !candidates,
            ["searching"] = reviewable && !checkedRow && !paused, ["has_reference"] = reviewable && reference, ["no_reference"] = reviewable && !reference,
            ["has_english"] = reviewable && hasEnglish, ["no_english"] = reviewable && !hasEnglish,
        };
    }

    /// <summary>_hydrate_import_rows: display fields only (a read never writes).</summary>
    private List<JsonObject> Hydrate(IReadOnlyList<DatabaseRow> rows)
    {
        var wanted = new List<long>();
        foreach (var row in rows)
        {
            wanted.AddRange(CandidateIds(row));
            if (row["entry_id"] is long entry) wanted.Add(entry);
            if (row["reference_entry_id"] is long reference) wanted.Add(reference);
        }
        var live = LiveIds(wanted);
        var summaries = _ingest.EntrySummaries(live.Values.Distinct()).ToDictionary(summary => summary["canonical_id"]?.GetValue<long>() ?? 0);
        var sources = Read(c => c.Query("SELECT source_id,name,display_name,lang FROM source_config").ToDictionary(row => row.Text("source_id"), StringComparer.Ordinal));
        var output = new List<JsonObject>(rows.Count);
        foreach (var row in rows)
        {
            var item = RowJson(row);
            var raw = ItemPayload(row);
            var candidates = CandidateIds(row).Where(live.ContainsKey).Select(id => live[id]).Distinct().ToArray();
            item["candidate_ids"] = new JsonArray(candidates.Select(id => (JsonNode)id).ToArray());
            item["suggestions"] = new JsonArray(candidates.Where(summaries.ContainsKey).Select(id => (JsonNode)summaries[id].DeepClone()).ToArray());
            sources.TryGetValue(row.Text("source_id"), out var source);
            var rawSource = row.Text("backup_source");
            var name = source is null ? "" : source.Text("display_name").Length > 0 ? source.Text("display_name") : source.Text("name");
            if (name.Length == 0) name = Str(raw, "sourceName");
            if (name.Length == 0) name = rawSource.Length > 0 ? "Unknown source (ID " + rawSource + ")" : "Unknown backup source";
            item["source_name"] = name;
            item["source_language"] = source?.Text("lang") ?? "";
            item["backup_thumbnail_url"] = Str(raw, "thumbnailUrl");
            item["backup_description"] = Str(raw, "description");
            item["backup_author"] = Str(raw, "author").Length > 0 ? Str(raw, "author") : Str(raw, "artist");
            item["metadata_identity"] = raw[MetadataKey] is JsonObject bundle ? bundle.DeepClone() : new JsonObject();
            item["import_kind"] = Str(raw, KindKey).Length > 0 ? Str(raw, KindKey) : "backup";
            item["source_diagnostics"] = raw[DiagnosticsKey] is JsonObject diagnostics
                ? new JsonArray(diagnostics.Select(pair => pair.Value?.DeepClone()).Where(node => node is JsonObject).ToArray())
                : new JsonArray();
            var reason = row.Text("reason");
            item["reason"] = reason.Length > 600 ? reason[..600] + "…" : reason;
            item["reason_detail"] = reason.Length > 600 ? reason : "";
            long? referenceLive = row["reference_entry_id"] is long reference && live.TryGetValue(reference, out var current) ? current : null;
            item["reference_entry_id"] = referenceLive;
            if (referenceLive is { } shown && row.Text("reference_thumbnail_url").Length == 0) item["reference_thumbnail_url"] = "/api/v1/covers/" + Id(shown);
            JsonObject? stored = null;
            try { stored = JsonNode.Parse(row.Text("matched_snapshot_json", "{}")) as JsonObject; } catch (JsonException) { }
            if (stored is { Count: 0 }) stored = null;
            JsonObject? matched = row["entry_id"] is long entryId && live.TryGetValue(entryId, out var liveEntry) && summaries.TryGetValue(liveEntry, out var summary)
                ? (JsonObject)summary.DeepClone() : null;
            item["applied_match"] = stored?.DeepClone();
            item["matched_entry"] = matched ?? stored?.DeepClone();
            item["historical_snapshot_missing"] = stored is null && row.Text("state") is "added" or "existing";
            item.Remove("candidate_ids_json"); item.Remove("matched_snapshot_json"); item.Remove("raw_json");
            output.Add(item);
        }
        return output;
    }

    /// <summary>import_run_items: one page of a run's items with review counts over the whole run/state.</summary>
    public JsonObject Items(string importId, string state = "", int limit = 1000, int offset = 0, string review = "all")
    {
        var wanted = (state ?? "").Trim().ToLowerInvariant();
        review = string.IsNullOrWhiteSpace(review) ? "all" : review.Trim();
        if (!ReviewFilters.Contains(review)) throw new ArgumentException("Unknown import review filter");
        var size = Math.Clamp(limit, 1, 5000);
        var (run, lean) = Read(c =>
        {
            var found = c.Query("SELECT * FROM import_runs WHERE id=$id", ("$id", importId)).FirstOrDefault() ?? throw new KeyNotFoundException("Import run not found");
            var leanRows = c.Query("""
                SELECT id,position,state,title,english_title,candidate_ids_json,reference_entry_id,suggestion_checked,suggestion_state,translation_checked
                FROM import_items WHERE import_id=$id AND ($state='' OR $state='all' OR state=$state) ORDER BY position,id
                """, ("$id", importId), ("$state", wanted));
            return (found, leanRows);
        });
        var ids = new List<long>();
        foreach (var row in lean)
        {
            ids.AddRange(CandidateIds(row));
            if (row["reference_entry_id"] is long reference) ids.Add(reference);
        }
        var live = LiveIds(ids);
        var counts = ReviewFilters.ToDictionary(name => name, _ => 0L, StringComparer.Ordinal);
        var selected = new List<long>();
        var pending = 0;
        foreach (var row in lean)
        {
            var flags = ReviewFlags(row, live);
            foreach (var (name, yes) in flags) if (yes) counts[name]++;
            if (flags[review]) selected.Add(row.Integer("id"));
            if (row.Text("state") is "unresolved" or "failed" && row.Text("suggestion_state") != "paused" && row.Integer("suggestion_checked") == 0) pending++;
        }
        var total = selected.Count;
        var start = total == 0 ? 0 : Math.Min(Math.Max(0, offset), (total - 1) / size * size);
        var page = selected.Skip(start).Take(size).ToArray();
        IReadOnlyList<DatabaseRow> rows = page.Length == 0 ? Array.Empty<DatabaseRow>() : Read(c => c.Query("SELECT * FROM import_items WHERE import_id=$id AND id IN (SELECT value FROM json_each($ids)) ORDER BY position,id",
            ("$id", importId), ("$ids", JsonSerializer.Serialize(page))));
        var reviewCounts = new JsonObject();
        foreach (var (name, count) in counts) reviewCounts[name] = count;
        return new JsonObject
        {
            ["run"] = RowJson(run), ["items"] = new JsonArray(Hydrate(rows).Select(item => (JsonNode)item).ToArray()), ["enrichment_pending"] = pending,
            ["review_counts"] = reviewCounts, ["total"] = total, ["state_total"] = lean.Count, ["offset"] = start, ["limit"] = size,
            ["has_more"] = start + rows.Count < total,
        };
    }

    /// <summary>import_run_item.</summary>
    public JsonObject Item(string importId, long itemId)
    {
        var (run, row) = Read(c => (c.Query("SELECT * FROM import_runs WHERE id=$id", ("$id", importId)).FirstOrDefault(),
            c.Query("SELECT * FROM import_items WHERE import_id=$run AND id=$id", ("$run", importId), ("$id", itemId)).FirstOrDefault()));
        if (run is null || row is null) throw new KeyNotFoundException("Import item not found");
        return new JsonObject { ["run"] = RowJson(run), ["item"] = Hydrate([row])[0] };
    }

    // ---- decision revisions (latest click wins) --------------------------------------------------------------------

    /// <summary>
    /// _claim_import_decision: a new revision for this item. With <paramref name="expectedRevision"/> (restart recovery) the
    /// claim succeeds only while the row is still unresolved at that revision; null then means a newer decision exists.
    /// </summary>
    internal long? Claim(string importId, long itemId, long? expectedRevision = null) => Write(c => ClaimIn(c, importId, itemId, expectedRevision));

    private static long? ClaimIn(DatabaseSession c, string importId, long itemId, long? expectedRevision)
    {
        var changed = expectedRevision is { } expected
            ? c.Execute("UPDATE import_items SET decision_revision=decision_revision+1 WHERE import_id=$run AND id=$id AND state='unresolved' AND decision_revision=$rev",
                ("$run", importId), ("$id", itemId), ("$rev", expected))
            : c.Execute("UPDATE import_items SET decision_revision=decision_revision+1 WHERE import_id=$run AND id=$id", ("$run", importId), ("$id", itemId));
        if (changed == 0)
        {
            if (expectedRevision is not null) return (long?)null;
            throw new KeyNotFoundException("Import item not found");
        }
        return Convert.ToInt64(c.Scalar("SELECT decision_revision FROM import_items WHERE import_id=$run AND id=$id", ("$run", importId), ("$id", itemId)),
            CultureInfo.InvariantCulture);
    }

    /// <summary>_import_decision_superseded: true when a newer decision (or a deletion) replaced this revision.</summary>
    internal bool Superseded(string importId, long itemId, long revision) => Read(c =>
        c.Scalar("SELECT decision_revision FROM import_items WHERE import_id=$run AND id=$id", ("$run", importId), ("$id", itemId)) is not long current || current != revision);

    internal static JsonObject SupersededAnswer(string importId, long itemId) => new()
    {
        ["ok"] = false, ["superseded"] = true, ["import_id"] = importId, ["item_id"] = itemId, ["reason"] = "A newer import decision superseded this action",
    };

    /// <summary>
    /// _update_import_item_state: the state, reason and entry (with its snapshot), guarded by the expected states and/or
    /// revision; a new decision (Skip) also advances the revision. A guard miss answers superseded.
    /// </summary>
    internal JsonObject UpdateState(string importId, long itemId, string state, string reason, long? entryId,
        IReadOnlyCollection<string>? expectedStates = null, long? expectedRevision = null, bool newDecision = false)
    {
        var snapshot = entryId is { } chosen && state is "added" or "existing" or "resolving" ? Snapshot(chosen) : new JsonObject();
        return Write(c =>
        {
            var changed = c.Execute("""
                UPDATE import_items SET state=$state,reason=$reason,entry_id=$entry,matched_snapshot_json=$snapshot,updated_at=$now,library_add_verified=$verified,
                  decision_revision=decision_revision+$new
                WHERE import_id=$run AND id=$id AND ($states='' OR state IN (SELECT value FROM json_each($states))) AND ($rev IS NULL OR decision_revision=$rev)
                """, ("$state", state), ("$reason", reason), ("$entry", entryId), ("$snapshot", snapshot.ToJsonString()), ("$now", Now()),
                ("$verified", state == "added" ? 1 : 0), ("$new", newDecision ? 1 : 0), ("$run", importId), ("$id", itemId),
                ("$states", expectedStates is { Count: > 0 } ? JsonSerializer.Serialize(expectedStates) : ""), ("$rev", expectedRevision));
            var revision = c.Scalar("SELECT decision_revision FROM import_items WHERE import_id=$run AND id=$id", ("$run", importId), ("$id", itemId));
            if (changed == 0)
            {
                if (revision is not null && (expectedStates is { Count: > 0 } || expectedRevision is not null)) return SupersededAnswer(importId, itemId);
                throw new KeyNotFoundException("Import item not found");
            }
            RecountIn(c, importId, null);
            return new JsonObject
            {
                ["ok"] = true, ["import_id"] = importId, ["item_id"] = itemId, ["state"] = state, ["reason"] = reason, ["entry_id"] = entryId,
                ["applied_match"] = snapshot.Count > 0 ? snapshot : null, ["decision_revision"] = Convert.ToInt64(revision, CultureInfo.InvariantCulture),
            };
        });
    }

    /// <summary>_restore_cancelled_import_resolution: puts back the settled row this action replaced (only while the action still owns it).</summary>
    private void RestoreCancelled(string importId, long itemId, DatabaseRow previous, string expectedState, long revision) => Write(c =>
    {
        c.Execute("""
            UPDATE import_items SET state=$state,reason='Import resolution cancelled',entry_id=$entry,matched_snapshot_json=$snapshot,updated_at=$now,
              library_add_verified=$verified WHERE import_id=$run AND id=$id AND state=$expected AND decision_revision=$rev
            """, ("$state", previous.Text("state", "unresolved") is "resolving" or "retrying" ? "unresolved" : previous.Text("state", "unresolved")),
            ("$entry", previous["entry_id"]), ("$snapshot", previous.Text("matched_snapshot_json", "{}")), ("$now", Now()),
            ("$verified", previous.Integer("library_add_verified")), ("$run", importId), ("$id", itemId), ("$expected", expectedState), ("$rev", revision));
        RecountIn(c, importId, null);
        return 0;
    });

    /// <summary>_write_import_discovery_fields guarded by the revision that started the discovery.</summary>
    private bool WriteDiscovery(string importId, long itemId, long? revision, JsonArray? candidates, int? suggestionChecked, string? suggestionState,
        JsonObject? raw = null, long? referenceEntryId = null, string? reason = null) => Write(c => c.Execute("""
        UPDATE import_items SET
          candidate_ids_json=COALESCE($candidates,candidate_ids_json),
          suggestion_checked=COALESCE($checked,suggestion_checked),
          suggestion_state=COALESCE($suggestion,suggestion_state),
          raw_json=COALESCE($raw,raw_json),
          reference_entry_id=COALESCE($reference,reference_entry_id),
          reason=COALESCE($reason,reason),
          updated_at=$now
        WHERE import_id=$run AND id=$id AND ($rev IS NULL OR decision_revision=$rev)
        """, ("$candidates", candidates?.ToJsonString()), ("$checked", suggestionChecked), ("$suggestion", suggestionState), ("$raw", raw?.ToJsonString()),
        ("$reference", referenceEntryId), ("$reason", reason), ("$now", Now()), ("$run", importId), ("$id", itemId), ("$rev", revision)) > 0);

    // ---- sources of a backup row -----------------------------------------------------------------------------------

    private sealed record SourceRow(string SourceId, string Name, string DisplayName, string Language);

    private List<SourceRow> AvailableSources() => Read(c => c.Query(
        "SELECT source_id,name,display_name,lang FROM source_config WHERE available=1 AND enabled_language=1 ORDER BY priority,source_id")
        .Select(row => new SourceRow(row.Text("source_id"), row.Text("name"), row.Text("display_name"), row.Text("lang"))).ToList());

    /// <summary>_backup_expected_source_name: the backup's own name, the configured name of its id, or "Unknown source (ID n)".</summary>
    internal string ExpectedSourceName(JsonObject item)
    {
        var explicitName = Str(item, "sourceName").Trim();
        if (explicitName.Length == 0) explicitName = Str(item, "source_name").Trim();
        if (explicitName.Length > 0) return explicitName;
        var candidates = ImportQueries.SourceIdCandidates(Str(item, "source"));
        var name = Read(c =>
        {
            foreach (var candidate in candidates)
                if (c.Query("SELECT name,display_name FROM source_config WHERE source_id=$id", ("$id", candidate)).FirstOrDefault() is { } row)
                    return row.Text("display_name").Trim().Length > 0 ? row.Text("display_name").Trim() : row.Text("name").Trim();
            return "";
        });
        if (name.Length > 0) return name;
        var raw = Str(item, "source").Trim();
        return raw.Length > 0 ? "Unknown source (ID " + raw + ")" : "Unknown source";
    }

    /// <summary>
    /// _current_source_id_for_backup: the enabled source of a backup row: its numeric id (signed or unsigned spelling), else a
    /// unique exact name, else a unique family name whose language tag agrees. Empty when none.
    /// </summary>
    internal string CurrentSourceId(JsonObject item)
    {
        var candidates = ImportQueries.SourceIdCandidates(Str(item, "source"));
        var rows = AvailableSources();
        foreach (var candidate in candidates)
            if (rows.Any(row => row.SourceId == candidate)) return candidate;
        var wanted = ImportQueries.Collapse(Str(item, "sourceName").Length > 0 ? Str(item, "sourceName") : Str(item, "source_name"));
        if (wanted.Length == 0) return "";
        var exact = rows.Where(row => string.Equals(ImportQueries.Collapse(row.DisplayName), wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ImportQueries.Collapse(row.Name), wanted, StringComparison.OrdinalIgnoreCase)).Select(row => row.SourceId).Distinct().ToArray();
        if (exact.Length == 1) return exact[0];
        var (wantedBase, wantedLanguage) = ImportQueries.SplitLanguageTag(wanted);
        var family = rows.Where(row =>
            {
                var bases = new[] { row.DisplayName, row.Name }.Where(name => name.Length > 0)
                    .Select(name => ImportQueries.SplitLanguageTag(name).Base).ToArray();
                if (wantedBase.Length == 0 || !bases.Any(name => string.Equals(name, wantedBase, StringComparison.OrdinalIgnoreCase))) return false;
                return wantedLanguage.Length == 0 || row.Language.ToLowerInvariant() is "" or "all" || row.Language.Equals(wantedLanguage, StringComparison.OrdinalIgnoreCase);
            }).Select(row => row.SourceId).Distinct().ToArray();
        return family.Length == 1 ? family[0] : "";
    }

    /// <summary>The source search the import may use for a source id (enabled search, available).</summary>
    private List<string> SearchSources(int limit) => Read(c => c.QueryAs<string>(
        "SELECT source_id FROM source_config WHERE available=1 AND enabled_language=1 AND enabled_search=1 ORDER BY priority,source_id LIMIT " +
        Id(Math.Clamp(limit, 1, 64))).ToList());
}
