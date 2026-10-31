using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Jobs;

/// <summary>One row of due_work: when a kind of recurring work for one key is next due and how the last runs went.</summary>
public sealed record DueState(string Kind, string WorkKey, string NextDue, string LastAttempt, string LastSuccess, string LastComplete,
    string Outcome, long Failures, string Reason, string ConfigRevision, string CompleteRevision);

/// <summary>
/// Port of due_scheduler.py: independent "complete observation" clocks per (kind, key) in catalog.due_work.
/// A complete run waits the full interval, a deferred run retries after 60 s, an incomplete run backs off
/// 60 s × 2^failures (at most one hour). Timestamps use the 0.4.x format so old and new rows compare as text.
/// </summary>
public sealed class DueScheduler(SqliteDatabase database, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public DueState State(string kind, string key) => database.Read(DatabaseFile.Catalog, c => Load(c, kind, key));

    public void Attempted(string kind, string key, string configuration = "") => database.Write(DatabaseFile.Catalog, c => c.Execute("""
        INSERT INTO due_work(kind,work_key,last_attempt,outcome,config_revision) VALUES($kind,$key,$time,'running',$config)
        ON CONFLICT(kind,work_key) DO UPDATE SET last_attempt=excluded.last_attempt,outcome='running',config_revision=excluded.config_revision
        """, ("$kind", kind), ("$key", key), ("$time", Iso(Now())), ("$config", configuration)));

    public DueState Finished(string kind, string key, bool complete = false, bool success = false, bool deferred = false, string reason = "", int interval = 3600) =>
        database.Write(DatabaseFile.Catalog, c =>
        {
            var old = Load(c, kind, key);
            var failures = complete ? 0 : old.Failures + (deferred ? 0 : 1);
            var delay = complete ? interval : deferred ? 60 : (int)Math.Min(3600, 60 * Math.Pow(2, Math.Min(failures, 6)));
            var now = Now();
            c.Execute("""
                INSERT INTO due_work VALUES($kind,$key,$next,$attempt,$success,$complete,$outcome,$failures,$reason,$config,$completeRevision)
                ON CONFLICT(kind,work_key) DO UPDATE SET next_due=excluded.next_due,last_success=excluded.last_success,last_complete=excluded.last_complete,
                  outcome=excluded.outcome,failures=excluded.failures,reason=excluded.reason,complete_revision=excluded.complete_revision
                """, ("$kind", kind), ("$key", key), ("$next", Iso(now.AddSeconds(delay))), ("$attempt", old.LastAttempt),
                ("$success", success ? Iso(now) : old.LastSuccess), ("$complete", complete ? Iso(now) : old.LastComplete),
                ("$outcome", complete ? "complete" : deferred ? "deferred" : "incomplete"), ("$failures", failures),
                ("$reason", Cut(reason, 240)), ("$config", old.ConfigRevision), ("$completeRevision", complete ? old.ConfigRevision : old.CompleteRevision));
            return Load(c, kind, key);
        });

    /// <summary>Due when the configuration changed since the last attempt, when never scheduled, or when its time has come.</summary>
    public bool Eligible(string kind, string key, string? configuration = null)
    {
        var value = State(kind, key);
        return configuration is not null && configuration != value.ConfigRevision || value.NextDue.Length == 0
            || string.CompareOrdinal(value.NextDue, Iso(Now())) <= 0;
    }

    public IReadOnlyList<DueState> Snapshot(int limit = 100) => database.Read(DatabaseFile.Catalog, c =>
        c.Query("SELECT * FROM due_work ORDER BY next_due,kind,work_key LIMIT $limit", ("$limit", Math.Clamp(limit, 1, 100))).Select(Row).ToArray());

    /// <summary>crawl_lane: the recent and backlog crawls of one source pair share a turn; the one due first (recent on ties) goes.</summary>
    public string CrawlLane(string pairKey, string configuration)
    {
        var clocks = new[] { "recent", "backlog" }.ToDictionary(lane => lane, lane => State("catalog_" + lane, pairKey), StringComparer.Ordinal);
        return clocks.OrderBy(pair => pair.Value.ConfigRevision == configuration ? pair.Value.NextDue : "", StringComparer.Ordinal)
            .ThenBy(pair => pair.Key == "recent" ? 0 : 1).First().Key;
    }

    private static DueState Load(DatabaseSession c, string kind, string key)
    {
        var row = c.Query("SELECT * FROM due_work WHERE kind=$kind AND work_key=$key", ("$kind", kind), ("$key", key));
        return row.Count > 0 ? Row(row[0]) : new DueState(kind, key, "", "", "", "", "unknown", 0, "", "", "");
    }

    private static DueState Row(DatabaseRow row) => new(row.Text("kind"), row.Text("work_key"), row.Text("next_due"), row.Text("last_attempt"),
        row.Text("last_success"), row.Text("last_complete"), row.Text("outcome"), row.Integer("failures"), row.Text("reason"),
        row.Text("config_revision"), row.Text("complete_revision"));

    private DateTimeOffset Now() => _time.GetUtcNow();

    /// <summary>Python's datetime.isoformat(): microseconds only when not zero, "+00:00" for UTC.</summary>
    internal static string Iso(DateTimeOffset value)
    {
        value = value.ToUniversalTime();
        return value.ToString(value.Ticks % TimeSpan.TicksPerSecond / 10 == 0 ? "yyyy-MM-dd'T'HH:mm:ss'+00:00'" : "yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);
    }

    private static string Cut(string text, int length) => string.Concat((text ?? "").EnumerateRunes().Take(length).Select(rune => rune.ToString()));
}

/// <summary>
/// Port of CrawlPages: a fetched source page stays in crawl_pending until every record on it succeeded. Failed records
/// retry on their own (60 s × 2^attempts, at most one hour) while the rest move on; full or invalid pages are refused
/// before any cursor moves.
/// </summary>
public sealed class CrawlPages(SqliteDatabase database, TimeProvider? time = null)
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
        { "id", "sourceId", "title", "url", "thumbnailUrl", "description", "author", "artist", "genre", "status", "inLibrary", "initialized", "lastFetchedAt" };
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public JsonObject? Load(string key) => database.Read(DatabaseFile.Catalog, c => Load(c, key));

    public JsonObject SavePage(string key, long page, JsonObject payload)
    {
        if (payload["mangas"] is not JsonArray rows || payload["hasNextPage"] is not JsonValue next || next.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False) || rows.Count > 500)
            throw new InvalidDataException("Incomplete or oversized source page; cursor retained");
        if (rows.Any(row => row is not JsonObject item || item["id"] is not JsonValue id || id.GetValueKind() != JsonValueKind.Number
                || !id.TryGetValue<long>(out var value) || value <= 0))
            throw new InvalidDataException("Invalid feed identity; cursor retained");
        var items = new JsonArray(rows.Select(row =>
        {
            var manga = new JsonObject();
            foreach (var (name, value) in row!.AsObject())
                if (Fields.Contains(name)) manga[name] = value?.DeepClone();
            return (JsonNode?)new JsonObject { ["manga"] = manga, ["attempts"] = 0, ["retry_at"] = "" };
        }).ToArray());
        var body = new JsonObject { ["has_next"] = next.GetValue<bool>(), ["items"] = items }.ToJsonString();
        if (System.Text.Encoding.UTF8.GetByteCount(body) > 1024 * 1024)
            throw new InvalidDataException("Source page exceeds 1 MiB pending limit; cursor retained");
        return database.Write(DatabaseFile.Catalog, c =>
        {
            if (Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM crawl_pending"), CultureInfo.InvariantCulture) >= 64
                && c.Scalar("SELECT 1 FROM crawl_pending WHERE work_key=$key", ("$key", key)) is null)
                throw new InvalidDataException("Pending feed capacity deferred; cursor retained");
            c.Execute("INSERT INTO crawl_pending VALUES($key,$page,$body,$time) ON CONFLICT(work_key) DO NOTHING",
                ("$key", key), ("$page", page), ("$body", body), ("$time", DueScheduler.Iso(_time.GetUtcNow())));
            return Load(c, key)!;
        });
    }

    public IReadOnlyList<JsonObject> Ready(string key, int budget)
    {
        var state = Load(key);
        if (state is null) return [];
        var now = DueScheduler.Iso(_time.GetUtcNow());
        return state["items"]!.AsArray().Select(item => item!.AsObject())
            .Where(item => item["retry_at"]!.GetValue<string>() is var retry && (retry.Length == 0 || string.CompareOrdinal(retry, now) <= 0))
            .Take(Math.Max(1, budget)).Select(item => item["manga"]!.AsObject()).ToArray();
    }

    public void Acknowledge(string key, long mangaId, bool success) => database.Write(DatabaseFile.Catalog, c =>
    {
        if (c.Scalar("SELECT payload_json FROM crawl_pending WHERE work_key=$key", ("$key", key)) is not string text) return 0;
        var state = JsonNode.Parse(text)!.AsObject();
        var remaining = new JsonArray();
        foreach (var node in state["items"]!.AsArray().ToArray())
        {
            var item = node!.AsObject();
            if (item["manga"]!["id"]!.GetValue<long>() == mangaId)
            {
                if (success) continue;
                var attempts = Math.Min(20, item["attempts"]!.GetValue<int>() + 1);
                item["attempts"] = attempts;
                item["retry_at"] = DueScheduler.Iso(_time.GetUtcNow().AddSeconds(Math.Min(3600, 60 * Math.Pow(2, Math.Min(attempts, 6)))));
            }
            item.Parent?.AsArray().Remove(item);
            remaining.Add(item);
        }
        state["items"] = remaining;
        return c.Execute("UPDATE crawl_pending SET payload_json=$body WHERE work_key=$key", ("$body", state.ToJsonString()), ("$key", key));
    });

    /// <summary>When the page is fully done: removes it and returns the next page (1 when the feed wrapped); otherwise null.</summary>
    public long? Finish(string key)
    {
        var state = Load(key);
        if (state is null || state["items"]!.AsArray().Count > 0) return null;
        database.Write(DatabaseFile.Catalog, c => c.Execute("DELETE FROM crawl_pending WHERE work_key=$key", ("$key", key)));
        return state["has_next"]!.GetValue<bool>() ? state["page"]!.GetValue<long>() + 1 : 1;
    }

    private static JsonObject? Load(DatabaseSession c, string key)
    {
        var rows = c.Query("SELECT page,payload_json FROM crawl_pending WHERE work_key=$key", ("$key", key));
        if (rows.Count == 0) return null;
        var state = JsonNode.Parse(rows[0].Text("payload_json"))!.AsObject();
        var result = new JsonObject { ["page"] = rows[0].Integer("page") };
        foreach (var (name, value) in state.ToArray()) { state.Remove(name); result[name] = value; }
        return result;
    }
}
