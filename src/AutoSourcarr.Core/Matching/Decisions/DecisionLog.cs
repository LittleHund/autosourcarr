using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Matching.Decisions;

/// <summary>One stored decision (a <see cref="DecisionEvent"/> with its id and time).</summary>
public sealed record DecisionLogEntry(long Id, string At, string Kind, DecisionActor Actor, IReadOnlyList<long> Entries,
    IReadOnlyList<string> Records, string Reason, JsonObject? Before, JsonObject? After, string? UndoOf)
{
    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["at"] = At, ["kind"] = Kind,
        ["actor"] = new JsonObject { ["kind"] = Actor.Kind, ["name"] = Actor.Name },
        ["entries"] = new JsonArray(Entries.Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
        ["records"] = new JsonArray(Records.Select(key => (JsonNode)JsonValue.Create(key)).ToArray()),
        ["reason"] = Reason, ["before"] = Before?.DeepClone(), ["after"] = After?.DeepClone(), ["undo_of"] = UndoOf
    };
}

/// <summary>
/// A filter for the decision log. <see cref="Kind"/> matches the kind itself and every dotted sub-kind ("merge" matches
/// "merge.automatic"); <see cref="Record"/> is "source_id:source_manga_id". Pages go backwards in time: pass the
/// previous page's <see cref="DecisionLogPage.NextBefore"/> as <see cref="BeforeId"/>.
/// </summary>
public sealed record DecisionLogQuery(string? Kind = null, long? EntryId = null, string? ActorKind = null, string? ActorName = null,
    string? Record = null, long? BeforeId = null, int Limit = 50);

public sealed record DecisionLogPage(IReadOnlyList<DecisionLogEntry> Items, bool HasMore, long? NextBefore)
{
    public JsonObject ToJson() => new()
    {
        ["items"] = new JsonArray(Items.Select(item => (JsonNode)item.ToJson()).ToArray()),
        ["has_more"] = HasMore, ["next_before"] = NextBefore
    };
}

/// <summary>Reads the decision log (the Decisions view and /api/v1/decisions).</summary>
public interface IDecisionLogReader
{
    DecisionLogPage Read(DecisionLogQuery query);
    DecisionLogEntry? Get(long id);
}

/// <summary>
/// The decision log over the catalogue database (table <c>decision_log</c>, migration 2026110180). <see cref="Record"/>
/// joins the caller's open catalogue write on this thread (as a savepoint), so a decision and its change commit together.
/// </summary>
public sealed class DecisionLog : IDecisionLog, IDecisionLogReader
{
    public const string Table = "decision_log";
    private readonly SqliteDatabase _database;
    private readonly TimeProvider _clock;

    public DecisionLog(SqliteDatabase database, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database; _clock = clock ?? TimeProvider.System;
    }

    public long Record(DecisionEvent decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var stamp = DecisionStamp.Now(_clock);
        return _database.Write(DatabaseFile.Catalog, session => Insert(session, decision, stamp));
    }

    /// <summary>Appends inside an explicit catalogue session (used by code that already holds one).</summary>
    public long Record(DatabaseSession session, DecisionEvent decision)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(decision);
        return Insert(session, decision, DecisionStamp.Now(_clock));
    }

    private static long Insert(DatabaseSession session, DecisionEvent decision, string stamp)
    {
        session.Execute("""
            INSERT INTO decision_log(at,kind,actor_kind,actor_name,entries_json,records_json,reason,before_json,after_json,undo_of)
            VALUES($at,$kind,$actorKind,$actorName,$entries,$records,$reason,$before,$after,$undo)
            """,
            ("$at", stamp), ("$kind", decision.Kind), ("$actorKind", decision.Actor.Kind), ("$actorName", decision.Actor.Name),
            ("$entries", JsonSerializer.Serialize(decision.Entries.Distinct().ToArray())),
            ("$records", JsonSerializer.Serialize(decision.Records.Distinct(StringComparer.Ordinal).ToArray())),
            ("$reason", decision.Reason), ("$before", decision.Before?.ToJsonString()), ("$after", decision.After?.ToJsonString()),
            ("$undo", decision.UndoOf));
        return Convert.ToInt64(session.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
    }

    public DecisionLogPage Read(DecisionLogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, 500);
        var kind = (query.Kind ?? "").Trim();
        var rows = _database.Read(DatabaseFile.Catalog, session => session.Query("""
            SELECT * FROM decision_log
            WHERE ($kind='' OR kind=$kind OR kind LIKE $kindPrefix ESCAPE '\')
              AND ($actorKind='' OR actor_kind=$actorKind)
              AND ($actorName='' OR actor_name=$actorName)
              AND ($before IS NULL OR id<$before)
              AND ($entry IS NULL OR EXISTS(SELECT 1 FROM json_each(decision_log.entries_json) WHERE value=$entry))
              AND ($record='' OR EXISTS(SELECT 1 FROM json_each(decision_log.records_json) WHERE value=$record))
            ORDER BY id DESC LIMIT $limit
            """,
            ("$kind", kind), ("$kindPrefix", EscapeLike(kind) + ".%"), ("$actorKind", (query.ActorKind ?? "").Trim()),
            ("$actorName", (query.ActorName ?? "").Trim()), ("$before", query.BeforeId), ("$entry", query.EntryId),
            ("$record", (query.Record ?? "").Trim()), ("$limit", limit + 1)));
        var items = rows.Take(limit).Select(Map).ToArray();
        var more = rows.Count > limit;
        return new DecisionLogPage(items, more, more ? items[^1].Id : null);
    }

    public DecisionLogEntry? Get(long id)
    {
        var rows = _database.Read(DatabaseFile.Catalog, session => session.Query("SELECT * FROM decision_log WHERE id=$id", ("$id", id)));
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    /// <summary>Full reset: the log goes with the rest of the learned state (the reset itself is logged afterwards).</summary>
    public static void Clear(DatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Execute("DELETE FROM decision_log");
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static DecisionLogEntry Map(DatabaseRow row)
    {
        static JsonObject? ParseObject(string text)
        {
            if (text.Length == 0) return null;
            try { return JsonNode.Parse(text) as JsonObject; } catch (JsonException) { return null; }
        }
        long[] entries; string[] records;
        try { entries = JsonSerializer.Deserialize<long[]>(row.Text("entries_json", "[]")) ?? []; } catch (JsonException) { entries = []; }
        try { records = JsonSerializer.Deserialize<string[]>(row.Text("records_json", "[]")) ?? []; } catch (JsonException) { records = []; }
        var undo = row.Text("undo_of");
        return new DecisionLogEntry(row.Integer("id"), row.Text("at"), row.Text("kind"), new DecisionActor(row.Text("actor_kind"), row.Text("actor_name")),
            entries, records, row.Text("reason"), ParseObject(row.Text("before_json")), ParseObject(row.Text("after_json")), undo.Length == 0 ? null : undo);
    }
}

/// <summary>An in-memory decision log for tests (any area's tests may use it).</summary>
public sealed class InMemoryDecisionLog : IDecisionLog, IDecisionLogReader
{
    private readonly List<DecisionLogEntry> _entries = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;

    public InMemoryDecisionLog(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Everything recorded so far, oldest first.</summary>
    public IReadOnlyList<DecisionLogEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }

    /// <summary>The recorded kinds, oldest first (handy for assertions).</summary>
    public IReadOnlyList<string> Kinds { get { lock (_gate) return _entries.Select(entry => entry.Kind).ToArray(); } }

    public long Record(DecisionEvent decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (_gate)
        {
            var entry = new DecisionLogEntry(_entries.Count + 1, DecisionStamp.Now(_clock), decision.Kind, decision.Actor,
                decision.Entries.Distinct().ToArray(), decision.Records.Distinct(StringComparer.Ordinal).ToArray(), decision.Reason,
                (JsonObject?)decision.Before?.DeepClone(), (JsonObject?)decision.After?.DeepClone(), decision.UndoOf);
            _entries.Add(entry);
            return entry.Id;
        }
    }

    public DecisionLogPage Read(DecisionLogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, 500);
        var kind = (query.Kind ?? "").Trim();
        DecisionLogEntry[] matching;
        lock (_gate)
            matching = _entries.AsEnumerable().Reverse().Where(entry =>
                (kind.Length == 0 || entry.Kind == kind || entry.Kind.StartsWith(kind + ".", StringComparison.Ordinal))
                && (string.IsNullOrWhiteSpace(query.ActorKind) || entry.Actor.Kind == query.ActorKind.Trim())
                && (string.IsNullOrWhiteSpace(query.ActorName) || entry.Actor.Name == query.ActorName.Trim())
                && (query.BeforeId is not long before || entry.Id < before)
                && (query.EntryId is not long id || entry.Entries.Contains(id))
                && (string.IsNullOrWhiteSpace(query.Record) || entry.Records.Contains(query.Record.Trim(), StringComparer.Ordinal))).ToArray();
        var items = matching.Take(limit).ToArray();
        var more = matching.Length > limit;
        return new DecisionLogPage(items, more, more ? items[^1].Id : null);
    }

    public DecisionLogEntry? Get(long id) { lock (_gate) return _entries.FirstOrDefault(entry => entry.Id == id); }
}

/// <summary>Timestamps in the 0.4.x form (datetime.now(timezone.utc).isoformat()).</summary>
public static class DecisionStamp
{
    public static string Now(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);
    }
}
