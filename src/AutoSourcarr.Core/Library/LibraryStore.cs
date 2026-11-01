using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Library;

/// <summary>A work's place in the library: membership and its fixed series folder (folder names never move once given).</summary>
public sealed record LibrarySeries(long EntryId, bool InLibrary, string SeriesName, string Folder);

/// <summary>One queued chapter.</summary>
public sealed record DownloadItem(long Id, long EntryId, long ChapterId, string SourceId, string SourceName, long Position, string State,
    int Tries, double Progress, string Error, LibraryChapterInfo Chapter, string QueuedAt);

/// <summary>
/// Persistence of the CBZ library in the catalogue database: library_series, download_queue, library_downloads,
/// download_history and library_state (migration 2026110192), plus the 0.4.x library_tracking / due_work rows that
/// keep monitoring and observations (port of library_manager.py tracking/save_tracking).
/// </summary>
public sealed class LibraryStore
{
    public const string DueKind = "library";
    private readonly SqliteDatabase database;
    private readonly TimeProvider _time;
    private readonly DueScheduler _due;

    public LibraryStore(SqliteDatabase database, TimeProvider? time = null)
    {
        this.database = database;
        _time = time ?? TimeProvider.System;
        _due = new DueScheduler(database, time);
    }

    public DueScheduler Due => _due;
    private string Now() => DueScheduler.Iso(_time.GetUtcNow());

    // ---- tracking (library_manager.tracking / save_tracking) ---------------------------------------------------------

    public JsonObject Tracking(long entryId) => database.Read(DatabaseFile.Catalog, c => Tracking(c, entryId));

    private JsonObject Tracking(DatabaseSession c, long entryId)
    {
        if (c.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", entryId)) is null) throw new KeyNotFoundException("Work no longer exists");
        var row = c.Query("SELECT * FROM library_tracking WHERE entry_id=$id", ("$id", entryId)).FirstOrDefault();
        var complete = c.Query("SELECT * FROM library_complete_observations WHERE entry_id=$id", ("$id", entryId)).FirstOrDefault();
        var due = _due.State(DueKind, entryId.ToString(CultureInfo.InvariantCulture));
        JsonObject? last = null;
        if (complete is not null)
        {
            last = ParseObject(complete.Text("observation_json"));
            last["observed_at"] = complete.Text("observed_at");
        }
        return new JsonObject
        {
            ["entry_id"] = entryId,
            ["revision"] = row?.Integer("revision") ?? 0,
            ["monitored"] = row is not null && row.Flag("monitored"),
            ["main_manga_id"] = row is not null && row["main_manga_id"] is not null ? JsonValue.Create(row.Integer("main_manga_id")) : null,
            ["observation"] = ParseObject(row?.Text("observation_json", "{}") ?? "{}"),
            ["observed_at"] = row?.Text("observed_at") ?? "",
            ["updated_at"] = row?.Text("updated_at") ?? "",
            ["due"] = new JsonObject
            {
                ["kind"] = due.Kind, ["work_key"] = due.WorkKey, ["next_due"] = due.NextDue, ["last_attempt"] = due.LastAttempt,
                ["last_success"] = due.LastSuccess, ["last_complete"] = due.LastComplete, ["outcome"] = due.Outcome,
                ["failures"] = due.Failures, ["reason"] = due.Reason,
            },
            ["last_complete_observation"] = last,
        };
    }

    /// <summary>Saves monitoring and/or an observation; a stale <paramref name="expected"/> revision is a conflict.</summary>
    public JsonObject SaveTracking(long entryId, long? expected = null, bool? monitored = null, JsonObject? observation = null) =>
        database.Write(DatabaseFile.Catalog, c =>
        {
            var current = Tracking(c, entryId);
            var revision = current["revision"]!.GetValue<long>();
            if (expected is { } wanted && wanted != revision) throw new LibraryConflictException("Library controls changed; reload before applying");
            var wasMonitored = current["monitored"]!.GetValue<bool>();
            if (monitored is { } flag && flag != wasMonitored)
                c.Execute("DELETE FROM due_work WHERE kind=$kind AND work_key=$key", ("$kind", DueKind), ("$key", entryId.ToString(CultureInfo.InvariantCulture)));
            var now = Now();
            var stored = observation ?? current["observation"]!.AsObject();
            c.Execute("""
                INSERT INTO library_tracking(entry_id,monitored,revision,main_manga_id,observation_json,observed_at,updated_at)
                VALUES($id,$monitored,$revision,NULL,$observation,$observed,$now)
                ON CONFLICT(entry_id) DO UPDATE SET monitored=excluded.monitored,revision=excluded.revision,
                  observation_json=excluded.observation_json,observed_at=excluded.observed_at,updated_at=excluded.updated_at
                """, ("$id", entryId), ("$monitored", (monitored ?? wasMonitored) ? 1 : 0), ("$revision", revision + 1),
                ("$observation", stored.ToJsonString()), ("$observed", observation is not null ? now : current["observed_at"]!.GetValue<string>()), ("$now", now));
            if (observation is not null && Text(observation, "download_state") == "checked" && Text(observation, "coverage_state") == "checked")
                c.Execute("INSERT OR REPLACE INTO library_complete_observations(entry_id,observation_json,observed_at) VALUES($id,$observation,$now)",
                    ("$id", entryId), ("$observation", observation.ToJsonString()), ("$now", now));
            return Tracking(c, entryId);
        });

    /// <summary>Monitored works whose monitoring clock is due (oldest first), at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<long> DueMonitored(string configuration, int limit = 20) => database.Read(DatabaseFile.Catalog, c => c.Query("""
        SELECT lt.entry_id FROM library_tracking lt JOIN entries e ON e.id=lt.entry_id
        JOIN library_series ls ON ls.entry_id=lt.entry_id AND ls.in_library=1
        LEFT JOIN due_work d ON d.kind='library' AND d.work_key=CAST(lt.entry_id AS TEXT)
        WHERE lt.monitored=1 AND (d.next_due IS NULL OR d.next_due='' OR d.next_due<=$now OR d.config_revision<>$config)
        ORDER BY COALESCE(d.next_due,''),COALESCE(d.last_complete,''),lt.entry_id LIMIT $limit
        """, ("$now", Now()), ("$config", configuration), ("$limit", limit)).Select(row => row.Integer("entry_id")).ToArray());

    // ---- membership ------------------------------------------------------------------------------------------------

    public LibrarySeries? Series(long entryId) => database.Read(DatabaseFile.Catalog, c => Series(c, entryId));

    private static LibrarySeries? Series(DatabaseSession c, long entryId) =>
        c.Query("SELECT * FROM library_series WHERE entry_id=$id", ("$id", entryId)).Select(SeriesRow).FirstOrDefault();

    /// <summary>Adds or removes the work; the first add gives it a series folder no other work uses (title, then "title [id]").</summary>
    public LibrarySeries SetMembership(long entryId, bool inLibrary, string title) => database.Write(DatabaseFile.Catalog, c =>
    {
        var now = Now();
        var existing = Series(c, entryId);
        if (existing is not null)
        {
            c.Execute("UPDATE library_series SET in_library=$in,updated_at=$now,added_at=CASE WHEN $in=1 AND in_library=0 THEN $now ELSE added_at END WHERE entry_id=$id",
                ("$in", inLibrary ? 1 : 0), ("$now", now), ("$id", entryId));
            return Series(c, entryId)!;
        }
        var name = title.Trim().Length > 0 ? title.Trim() : "Work " + entryId.ToString(CultureInfo.InvariantCulture);
        var folder = LibraryNames.SeriesFolder(name);
        if (c.Scalar("SELECT 1 FROM library_series WHERE folder_key=$key", ("$key", FolderKey(folder))) is not null)
        {
            name += " [" + entryId.ToString(CultureInfo.InvariantCulture) + "]";
            folder = LibraryNames.SeriesFolder(name);
        }
        c.Execute("INSERT INTO library_series(entry_id,in_library,series_name,folder,folder_key,added_at,updated_at) VALUES($id,$in,$name,$folder,$key,$added,$now)",
            ("$id", entryId), ("$in", inLibrary ? 1 : 0), ("$name", name), ("$folder", folder), ("$key", FolderKey(folder)), ("$added", inLibrary ? now : ""), ("$now", now));
        return Series(c, entryId)!;
    });

    public IReadOnlyList<long> Members() => database.Read(DatabaseFile.Catalog, c =>
        c.Query("SELECT entry_id FROM library_series WHERE in_library=1 ORDER BY entry_id").Select(row => row.Integer("entry_id")).ToArray());

    // ---- queue -----------------------------------------------------------------------------------------------------

    /// <summary>Queues chapters not queued yet (failed ones go back to queued); returns how many were (re)queued.</summary>
    public int Enqueue(long entryId, IEnumerable<LibraryChapterInfo> chapters) => database.Write(DatabaseFile.Catalog, c =>
    {
        var now = Now(); var count = 0;
        var position = Convert.ToInt64(c.Scalar("SELECT COALESCE(MAX(position),0) FROM download_queue") ?? 0L, CultureInfo.InvariantCulture);
        foreach (var chapter in chapters)
        {
            var existing = c.Query("SELECT state FROM download_queue WHERE chapter_id=$chapter", ("$chapter", chapter.ChapterId)).FirstOrDefault();
            if (existing is not null)
            {
                if (existing.Text("state") != "failed") continue;
                c.Execute("UPDATE download_queue SET state='queued',error='',tries=0,progress=0,source_id=$source,source_name=$name,chapter_json=$json,updated_at=$now WHERE chapter_id=$chapter",
                    ("$source", chapter.SourceId), ("$name", chapter.SourceName), ("$json", ChapterJson(chapter).ToJsonString()), ("$now", now), ("$chapter", chapter.ChapterId));
                count++;
                continue;
            }
            c.Execute("""
                INSERT INTO download_queue(entry_id,chapter_id,source_id,source_name,position,state,chapter_json,queued_at,updated_at)
                VALUES($entry,$chapter,$source,$name,$position,'queued',$json,$now,$now)
                """, ("$entry", entryId), ("$chapter", chapter.ChapterId), ("$source", chapter.SourceId), ("$name", chapter.SourceName),
                ("$position", ++position), ("$json", ChapterJson(chapter).ToJsonString()), ("$now", now));
            count++;
        }
        return count;
    });

    /// <summary>The first queued chapter of one source (queue order).</summary>
    public DownloadItem? Next(string sourceId) => database.Read(DatabaseFile.Catalog, c => c.Query(
        "SELECT * FROM download_queue WHERE source_id=$source AND state='queued' ORDER BY position,id LIMIT 1", ("$source", sourceId)).Select(Item).FirstOrDefault());

    public IReadOnlyList<string> QueuedSources() => database.Read(DatabaseFile.Catalog, c =>
        c.Query("SELECT source_id,MIN(position) first FROM download_queue WHERE state='queued' GROUP BY source_id ORDER BY first").Select(row => row.Text("source_id")).ToArray());

    public bool HasQueued(string sourceId) => database.Read(DatabaseFile.Catalog, c =>
        c.Scalar("SELECT 1 FROM download_queue WHERE source_id=$source AND state='queued' LIMIT 1", ("$source", sourceId)) is not null);

    public IReadOnlyList<DownloadItem> Queue() => database.Read(DatabaseFile.Catalog, c =>
        c.Query("SELECT * FROM download_queue ORDER BY position,id").Select(Item).ToArray());

    public DownloadItem? Queued(long chapterId) => database.Read(DatabaseFile.Catalog, c =>
        c.Query("SELECT * FROM download_queue WHERE chapter_id=$chapter", ("$chapter", chapterId)).Select(Item).FirstOrDefault());

    /// <summary>Marks a queued chapter as downloading; false when it is gone or no longer queued.</summary>
    public bool Start(long chapterId) => database.Write(DatabaseFile.Catalog, c =>
        c.Execute("UPDATE download_queue SET state='downloading',tries=tries+1,progress=0,updated_at=$now WHERE chapter_id=$chapter AND state='queued'",
            ("$now", Now()), ("$chapter", chapterId)) == 1);

    public void Progress(long chapterId, double progress) => database.Write(DatabaseFile.Catalog, c =>
        c.Execute("UPDATE download_queue SET progress=$progress WHERE chapter_id=$chapter AND state='downloading'", ("$progress", Math.Clamp(progress, 0, 1)), ("$chapter", chapterId)));

    /// <summary>Puts an interrupted download back in the queue (its staged pages stay, so it resumes).</summary>
    public void Requeue(long chapterId) => database.Write(DatabaseFile.Catalog, c =>
        c.Execute("UPDATE download_queue SET state='queued',updated_at=$now WHERE chapter_id=$chapter AND state='downloading'", ("$now", Now()), ("$chapter", chapterId)));

    public void Fail(DownloadItem item, string error) => database.Write(DatabaseFile.Catalog, c =>
    {
        var text = error.Length > 2000 ? error[..2000] : error;
        c.Execute("UPDATE download_queue SET state='failed',error=$error,updated_at=$now WHERE chapter_id=$chapter", ("$error", text), ("$now", Now()), ("$chapter", item.ChapterId));
        History(c, item.EntryId, item.ChapterId, "failed", text);
        return 0;
    });

    /// <summary>Restart recovery: anything left "downloading" by a stopped process is queued again.</summary>
    public int ResetInterrupted() => database.Write(DatabaseFile.Catalog, c =>
        c.Execute("UPDATE download_queue SET state='queued',updated_at=$now WHERE state='downloading'", ("$now", Now())));

    public bool Remove(long chapterId, string reason) => database.Write(DatabaseFile.Catalog, c =>
    {
        var item = c.Query("SELECT * FROM download_queue WHERE chapter_id=$chapter", ("$chapter", chapterId)).Select(Item).FirstOrDefault();
        if (item is null) return false;
        c.Execute("DELETE FROM download_queue WHERE chapter_id=$chapter", ("$chapter", chapterId));
        History(c, item.EntryId, chapterId, "removed", reason);
        return true;
    });

    public int RemoveEntry(long entryId, string reason) => database.Write(DatabaseFile.Catalog, c =>
    {
        var ids = c.Query("SELECT chapter_id FROM download_queue WHERE entry_id=$entry AND state<>'downloading'", ("$entry", entryId)).Select(row => row.Integer("chapter_id")).ToArray();
        foreach (var id in ids)
        {
            c.Execute("DELETE FROM download_queue WHERE chapter_id=$chapter", ("$chapter", id));
            History(c, entryId, id, "removed", reason);
        }
        return ids.Length;
    });

    public void Retry(long chapterId) => database.Write(DatabaseFile.Catalog, c =>
        c.Execute("UPDATE download_queue SET state='queued',error='',tries=0,progress=0,updated_at=$now WHERE chapter_id=$chapter AND state='failed'", ("$now", Now()), ("$chapter", chapterId)));

    /// <summary>Moves a chapter to queue index <paramref name="to"/> (0 = first) and renumbers the queue.</summary>
    public void Move(long chapterId, int to) => database.Write(DatabaseFile.Catalog, c =>
    {
        var order = c.Query("SELECT chapter_id FROM download_queue ORDER BY position,id").Select(row => row.Integer("chapter_id")).ToList();
        if (!order.Remove(chapterId)) throw new LibraryConflictException("Chapter is no longer queued");
        order.Insert(Math.Clamp(to, 0, order.Count), chapterId);
        for (var index = 0; index < order.Count; index++)
            c.Execute("UPDATE download_queue SET position=$position WHERE chapter_id=$chapter", ("$position", index + 1), ("$chapter", order[index]));
        return 0;
    });

    // ---- downloaded chapters ---------------------------------------------------------------------------------------

    /// <summary>Downloaded chapters of a work: chapter id → relative file ("Series/Ch.001.cbz").</summary>
    public IReadOnlyDictionary<long, string> Downloaded(long entryId) => database.Read(DatabaseFile.Catalog, c =>
        c.Query("SELECT chapter_id,file FROM library_downloads WHERE entry_id=$entry", ("$entry", entryId)).ToDictionary(row => row.Integer("chapter_id"), row => row.Text("file")));

    /// <summary>The chapter that owns a relative file, if any.</summary>
    public long? FileOwner(string file) => database.Read<long?>(DatabaseFile.Catalog, c =>
        c.Scalar("SELECT chapter_id FROM library_downloads WHERE file=$file", ("$file", file)) is { } value ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : null);

    /// <summary>Records a finished chapter, removes it from the queue and logs it, in one write.</summary>
    public void Complete(DownloadItem item, string file, int pages, long bytes, long candidateId) => database.Write(DatabaseFile.Catalog, c =>
    {
        var now = Now();
        c.Execute("""
            INSERT INTO library_downloads(chapter_id,entry_id,file,pages,bytes,candidate_id,source_id,downloaded_at) VALUES($chapter,$entry,$file,$pages,$bytes,$candidate,$source,$now)
            ON CONFLICT(chapter_id) DO UPDATE SET entry_id=excluded.entry_id,file=excluded.file,pages=excluded.pages,bytes=excluded.bytes,
              candidate_id=excluded.candidate_id,source_id=excluded.source_id,downloaded_at=excluded.downloaded_at
            """, ("$chapter", item.ChapterId), ("$entry", item.EntryId), ("$file", file), ("$pages", pages), ("$bytes", bytes),
            ("$candidate", candidateId), ("$source", item.SourceId), ("$now", now));
        c.Execute("DELETE FROM download_queue WHERE chapter_id=$chapter", ("$chapter", item.ChapterId));
        History(c, item.EntryId, item.ChapterId, "downloaded", file);
        return 0;
    });

    /// <summary>Forgets a downloaded chapter whose file is gone (an observation found it missing).</summary>
    public void ForgetDownload(long chapterId) => database.Write(DatabaseFile.Catalog, c =>
        c.Execute("DELETE FROM library_downloads WHERE chapter_id=$chapter", ("$chapter", chapterId)));

    public JsonObject DownloadHistory(int offset, int limit) => database.Read(DatabaseFile.Catalog, c =>
    {
        var total = Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM download_history") ?? 0L, CultureInfo.InvariantCulture);
        var rows = c.Query("SELECT * FROM download_history ORDER BY id DESC LIMIT $limit OFFSET $offset", ("$limit", limit), ("$offset", offset));
        return new JsonObject
        {
            ["items"] = new JsonArray(rows.Select(row => (JsonNode?)new JsonObject
            {
                ["id"] = row.Integer("id"), ["entry_id"] = row.Integer("entry_id"), ["chapter_id"] = row.Integer("chapter_id"),
                ["event"] = row.Text("event"), ["detail"] = row.Text("detail"), ["created_at"] = row.Text("created_at"),
            }).ToArray()),
            ["total"] = total, ["offset"] = offset, ["limit"] = limit,
        };
    });

    // ---- state -----------------------------------------------------------------------------------------------------

    public bool Paused
    {
        get => database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT value FROM library_state WHERE key='downloads_paused'") is string value && value == "1");
        set => database.Write(DatabaseFile.Catalog, c => c.Execute("INSERT OR REPLACE INTO library_state(key,value) VALUES('downloads_paused',$value)", ("$value", value ? "1" : "0")));
    }

    /// <summary>Revision of the queue order and states (progress excluded, so a running download does not invalidate a click).</summary>
    public static string QueueRevision(bool started, IReadOnlyList<DownloadItem> items)
    {
        var text = new StringBuilder(started ? "STARTED" : "STOPPED");
        foreach (var item in items) text.Append('|').Append(item.ChapterId.ToString(CultureInfo.InvariantCulture)).Append(':').Append(item.State);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    internal static string FolderKey(string folder) => folder.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    private void History(DatabaseSession c, long entryId, long chapterId, string kind, string detail)
    {
        c.Execute("INSERT INTO download_history(entry_id,chapter_id,event,detail,created_at) VALUES($entry,$chapter,$event,$detail,$now)",
            ("$entry", entryId), ("$chapter", chapterId), ("$event", kind), ("$detail", detail.Length > 2000 ? detail[..2000] : detail), ("$now", Now()));
        // Keep the activity log bounded (newest 5000 events).
        c.Execute("DELETE FROM download_history WHERE id <= (SELECT id FROM download_history ORDER BY id DESC LIMIT 1 OFFSET 5000)");
    }

    private static LibrarySeries SeriesRow(DatabaseRow row) => new(row.Integer("entry_id"), row.Flag("in_library"), row.Text("series_name"), row.Text("folder"));

    private static DownloadItem Item(DatabaseRow row)
    {
        var json = ParseObject(row.Text("chapter_json", "{}"));
        var chapter = new LibraryChapterInfo(row.Integer("chapter_id"), row.Integer("entry_id"), Number(json, "number"), Number(json, "volume"),
            Text(json, "title"), row.Text("source_id"), row.Text("source_name"), Text(json, "scanlator"), (long)Number(json, "upload_date", 0), Text(json, "language"));
        return new(row.Integer("id"), row.Integer("entry_id"), row.Integer("chapter_id"), row.Text("source_id"), row.Text("source_name"), row.Integer("position"),
            row.Text("state"), (int)row.Integer("tries"), row.Number("progress"), row.Text("error"), chapter, row.Text("queued_at"));
    }

    private static JsonObject ChapterJson(LibraryChapterInfo chapter) => new()
    {
        ["number"] = chapter.Number, ["volume"] = chapter.Volume, ["title"] = chapter.Title, ["scanlator"] = chapter.Scanlator,
        ["upload_date"] = chapter.UploadDate, ["language"] = chapter.Language,
    };

    internal static JsonObject ParseObject(string text)
    {
        try { return JsonNode.Parse(text.Length == 0 ? "{}" : text) as JsonObject ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    internal static string Text(JsonObject row, string key) => row[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static double Number(JsonObject row, string key, double fallback = -1) => row[key] is JsonValue value
        ? value.TryGetValue<double>(out var number) ? number : value.TryGetValue<long>(out var integer) ? integer : fallback : fallback;
}
