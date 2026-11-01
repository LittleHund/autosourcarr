using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Matching.Manual.ManualSql;

namespace AutoSourcarr.Core.Matching.Manual;

/// <summary>The decision cannot be applied in the current catalogue state (0.4.5 ValueError; the routes answer 409).</summary>
public sealed class ManualDecisionConflictException(string message) : InvalidOperationException(message);

/// <summary>The decision needs catalogue operations this host does not provide (the routes answer 503).</summary>
public sealed class ManualDecisionUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Phase 8 "Matching → ManualDecisions" (0.4.5 service.py manual_* / _persist_manual_* / separate records, manual_reset.py):
/// the durable user intent that survives a normal database reset. manual_match_bindings remember which exact source
/// record the user chose (and its group), manual_separate_records which record must stay apart from an entry,
/// duplicate_reviews which entries are different works, preferred_title_overrides / manual_title_scopes / manual titles
/// the user's titles. These rows are only removed by an explicit, previewed and confirmed reset (or a full reset).
/// Every change is written to the decision log with its actor.
/// </summary>
/// <remarks>
/// Writes hold the shared source-write gate (0.4.5 _serialized_source_write) and run as short catalogue transactions,
/// in the 0.4.5 order; the catalogue operations of other areas (<see cref="IManualCatalogueOperations"/>) are called
/// between them, never inside one, because they open their own writes.
/// </remarks>
public sealed partial class ManualDecisions
{
    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly IDecisionLog _log;
    private readonly IManualCatalogueOperations? _operations;
    private readonly TimeProvider _clock;

    public ManualDecisions(SqliteDatabase database, object sourceWriteGate, IDecisionLog log, IManualCatalogueOperations? operations = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate); ArgumentNullException.ThrowIfNull(log);
        _database = database; _gate = sourceWriteGate; _log = log; _operations = operations; _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The catalogue operations this host provides (null: only durable decisions can change).</summary>
    public IManualCatalogueOperations? Operations => _operations;

    private string Stamp() => DecisionStamp.Now(_clock);
    private T Read<T>(Func<DatabaseSession, T> operation) => _database.Read(DatabaseFile.Catalog, operation);
    private T Write<T>(Func<DatabaseSession, T> operation) { lock (_gate) return _database.Write(DatabaseFile.Catalog, operation); }
    private IManualCatalogueOperations RequireOperations() => _operations
        ?? throw new ManualDecisionUnavailableException("Catalogue operations are not available in this host; nothing was changed.");
    private void Log(string kind, DecisionActor actor, IEnumerable<long> entries, IEnumerable<string> records, string reason, JsonObject? before = null, JsonObject? after = null)
        => _log.Record(new DecisionEvent(kind, actor, entries.Where(id => id > 0).Distinct().ToArray(), records.Distinct(StringComparer.Ordinal).ToArray(), reason, before, after));

    // ---- the view area 8A reads while matching (IManualDecisionsView)

    /// <summary>The user bound this exact record to an entry; <paramref name="entryId"/> is that entry (canonical), or 0 when it no longer exists.</summary>
    public bool IsManuallyBound(string sourceId, long sourceMangaId, out long entryId)
    {
        var found = Read<(bool Bound, long Entry)>(c =>
        {
            var rows = c.Query("SELECT entry_id_hint FROM manual_match_bindings WHERE source_id=$p0 AND source_manga_id=$p1", P(sourceId, sourceMangaId));
            if (rows.Count == 0) return (false, 0L);
            var hint = rows[0].Integer("entry_id_hint");
            var canonical = hint > 0 ? Canonical(c, hint) : 0;
            if (canonical > 0 && EntryExists(c, canonical)) return (true, canonical);
            return (true, Owner(c, sourceId, sourceMangaId) ?? 0);
        });
        entryId = found.Entry;
        return found.Bound;
    }

    /// <summary>The user kept this record apart from at least one entry ("Separate entry").</summary>
    public bool IsSeparated(string sourceId, long sourceMangaId) => Read(c =>
        c.Scalar("SELECT 1 FROM manual_separate_records WHERE source_id=$p0 AND source_manga_id=$p1 LIMIT 1", P(sourceId, sourceMangaId)) is not null);

    /// <summary>
    /// _is_manual_separate_record: the user kept this record apart from this entry. Targets are compared by canonical id,
    /// so the rule still holds after the entry was merged or recreated under a new id by a reset (0.4.5 compared raw ids).
    /// </summary>
    public bool IsSeparatedFrom(long entryId, string sourceId, long sourceMangaId) => Read(c =>
    {
        var target = Canonical(c, entryId);
        return c.QueryAs<long>("SELECT target_entry_id FROM manual_separate_records WHERE source_id=@sourceId AND source_manga_id=@sourceMangaId",
            new { sourceId, sourceMangaId }).Any(id => id == target || Canonical(c, id) == target);
    });

    /// <summary>The user reviewed these two entries as different works (duplicate_reviews not_duplicate).</summary>
    public bool IsRejected(long entryA, long entryB) => Read(c =>
    {
        var (left, right) = Pair(Canonical(c, entryA), Canonical(c, entryB));
        return left != right && c.Scalar("SELECT 1 FROM duplicate_reviews WHERE left_entry_id=$p0 AND right_entry_id=$p1 AND decision='not_duplicate'", P(left, right)) is not null;
    });

    /// <summary>The entry that currently owns the record was reviewed as a different work than this entry (attaching it here was rejected).</summary>
    public bool IsRecordRejected(long entryId, string sourceId, long sourceMangaId) => Read(c =>
    {
        var target = Canonical(c, entryId);
        if (Owner(c, sourceId, sourceMangaId) is not long owner || owner == target) return false;
        var (left, right) = Pair(target, owner);
        return c.Scalar("SELECT 1 FROM duplicate_reviews WHERE left_entry_id=$p0 AND right_entry_id=$p1 AND decision='not_duplicate'", P(left, right)) is not null;
    });

    /// <summary>The entry the user bound this exact record to (canonical), or null (IManualDecisionsView.ManualOwner).</summary>
    public long? ManualOwner(string sourceId, long sourceMangaId) => IsManuallyBound(sourceId, sourceMangaId, out var entryId) && entryId > 0 ? (long?)entryId : null;

    // ---- separate records (_remember/_forget_manual_separate_record, allow_source_record_match, separate_source_record)

    internal void RememberSeparate(DatabaseSession c, long entryId, string sourceId, long sourceMangaId, long? separateEntryId, string sourceTitle)
    {
        var stamp = Stamp();
        c.Execute("""
            INSERT INTO manual_separate_records(target_entry_id,source_id,source_manga_id,separate_entry_id_hint,source_title,created_at,updated_at)
            VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p5)
            ON CONFLICT(target_entry_id,source_id,source_manga_id) DO UPDATE SET
              separate_entry_id_hint=COALESCE(excluded.separate_entry_id_hint,manual_separate_records.separate_entry_id_hint),
              source_title=CASE WHEN excluded.source_title<>'' THEN excluded.source_title ELSE manual_separate_records.source_title END,
              updated_at=excluded.updated_at
            """, P(Canonical(c, entryId), sourceId, sourceMangaId, separateEntryId is > 0 ? separateEntryId : null, sourceTitle ?? "", stamp));
    }

    /// <summary>Keeps the record apart from the entry (used by split code of other areas; logs nothing itself).</summary>
    public void RememberSeparate(long entryId, string sourceId, long sourceMangaId, long? separateEntryId = null, string sourceTitle = "")
        => Write(c => { RememberSeparate(c, entryId, sourceId, sourceMangaId, separateEntryId, sourceTitle); return 0; });

    /// <summary>allow_source_record_match: forgets "Separate entry" for this record and the pair review it created.</summary>
    public JsonObject AllowSourceRecordMatch(long entryId, string sourceId, long sourceMangaId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return Write(c =>
        {
            var target = Canonical(c, entryId);
            if (!EntryExists(c, target)) throw new KeyNotFoundException("Entry not found");
            var before = c.Query("SELECT * FROM manual_separate_records WHERE target_entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2", P(target, sourceId, sourceMangaId));
            var other = ForgetSeparate(c, target, sourceId, sourceMangaId);
            if (before.Count > 0)
                Log("separate.allow", actor, [target, other], [RecordKey(sourceId, sourceMangaId)], "Separate-entry decision removed", Json(before[0]));
            return new JsonObject { ["entry_id"] = target, ["source_id"] = sourceId, ["source_manga_id"] = sourceMangaId, ["allowed"] = true };
        });
    }

    private long ForgetSeparate(DatabaseSession c, long target, string sourceId, long sourceMangaId)
    {
        var hint = c.Scalar("SELECT separate_entry_id_hint FROM manual_separate_records WHERE target_entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2", P(target, sourceId, sourceMangaId));
        c.Execute("DELETE FROM manual_separate_records WHERE target_entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2", P(target, sourceId, sourceMangaId));
        var other = hint is null ? 0 : Convert.ToInt64(hint, CultureInfo.InvariantCulture);
        if (other > 0 && EntryExists(c, Canonical(c, other))) ReviewPair(c, target, other, "clear");
        return other;
    }

    /// <summary>_review_duplicate_locked for the two decisions manual code writes itself: not_duplicate and clear.</summary>
    internal void ReviewPair(DatabaseSession c, long leftId, long rightId, string decision)
    {
        var (left, right) = Pair(Canonical(c, leftId), Canonical(c, rightId));
        if (left == right) return;
        if (decision == "clear")
        {
            c.Execute("DELETE FROM duplicate_reviews WHERE left_entry_id=$p0 AND right_entry_id=$p1", P(left, right));
            return;
        }
        var stamp = Stamp();
        c.Execute("INSERT INTO duplicate_review_history(left_entry_id,right_entry_id,decision,reviewed_at) VALUES($p0,$p1,$p2,$p3)", P(left, right, decision, stamp));
        c.Execute("""
            INSERT INTO duplicate_reviews(left_entry_id,right_entry_id,decision,reviewed_at) VALUES($p0,$p1,$p2,$p3)
            ON CONFLICT(left_entry_id,right_entry_id) DO UPDATE SET decision=excluded.decision,reviewed_at=excluded.reviewed_at
            """, P(left, right, decision, stamp));
    }

    /// <summary>
    /// separate_source_record: keeps the record as its own entry and remembers the decision. A record of the entry itself
    /// is split away; a record nobody owns becomes a new entry from <paramref name="candidate"/> (the manual-search card)
    /// or the source details.
    /// </summary>
    public async Task<JsonObject> SeparateSourceRecordAsync(long entryId, string sourceId, long sourceMangaId, JsonObject? candidate,
        DecisionActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var owned = Read(c => Owner(c, sourceId, sourceMangaId));
        var manga = new JsonObject();
        if (owned is null)
        {
            manga = ManualCandidateManga(sourceMangaId, candidate);
            if (manga.Count == 0)
            {
                var fetched = await RequireOperations().FetchMangaAsync(sourceMangaId, false, cancellationToken).ConfigureAwait(false);
                manga = fetched?["manga"] is JsonObject details ? (JsonObject)details.DeepClone() : new JsonObject();
            }
            if (manga.Count == 0) throw new ManualDecisionConflictException("Source manga details are unavailable");
        }
        return CommitSeparate(entryId, sourceId, sourceMangaId, candidate ?? new JsonObject(), manga, actor);
    }

    private JsonObject CommitSeparate(long entryId, string sourceId, long sourceMangaId, JsonObject candidate, JsonObject manga, DecisionActor actor)
    {
        lock (_gate)
        {
            var (target, owner, records) = Read(c =>
            {
                var id = Canonical(c, entryId);
                if (!EntryExists(c, id)) throw new KeyNotFoundException("Entry not found");
                var count = Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM (SELECT 1 FROM source_matches WHERE entry_id=$p0 AND active=1 UNION ALL SELECT 1 FROM source_variants WHERE entry_id=$p0 AND active=1)", P(id)), CultureInfo.InvariantCulture);
                return (id, Owner(c, sourceId, sourceMangaId) ?? 0, count);
            });
            var separate = owner;
            if (separate == target)
            {
                if (records <= 1) throw new ManualDecisionConflictException("This is the entry's only source record, so it cannot be split away from itself");
                var operations = RequireOperations();
                Write(c => ForgetBinding(c, sourceId, sourceMangaId, dissolveSmallGroup: true));
                separate = operations.SplitRecords(target, [new NativeSourceRecord(sourceId, sourceMangaId)]);
            }
            else if (separate <= 0)
            {
                if (manga.Count == 0) throw new ManualDecisionConflictException("Source ownership changed; retry Separate entry");
                var operations = RequireOperations();
                separate = operations.CreateEntry(sourceId, manga);
                operations.UpsertPrimary(separate, sourceId, manga, 1.0, "separate-anchor", false);
                operations.ChooseMetadata(separate);
            }
            if (separate > 0 && separate != target)
            {
                Write(c =>
                {
                    RememberSeparate(c, target, sourceId, sourceMangaId, separate, Text(candidate["title"]));
                    ReviewPair(c, target, separate, "not_duplicate");
                    Log("separate", actor, [target, separate], [RecordKey(sourceId, sourceMangaId)], "Kept the source record as a separate entry",
                        after: new JsonObject { ["target_entry_id"] = target, ["separate_entry_id"] = separate });
                    return 0;
                });
            }
            _operations?.Invalidate([]);
            return new JsonObject
            {
                ["entry_id"] = target, ["separate_entry_id"] = separate, ["source_id"] = sourceId, ["source_manga_id"] = sourceMangaId, ["separate"] = true
            };
        }
    }

    // ---- durable bindings and groups

    private static string SourceName(DatabaseSession c, string sourceId, string fallback)
    {
        var rows = c.Query("SELECT name,display_name FROM source_config WHERE source_id=$p0", P(sourceId));
        if (rows.Count > 0 && rows[0].Text("display_name") is { Length: > 0 } display) return display;
        if (rows.Count > 0 && rows[0].Text("name") is { Length: > 0 } name) return name;
        return fallback.Length > 0 ? fallback : sourceId;
    }

    /// <summary>
    /// _persist_manual_binding: remembers the exact record the user chose for the entry, outside the resettable catalogue
    /// rows, so a normal reset can rebuild it. Optionally forms the entry's manual group once two bindings share it.
    /// </summary>
    internal JsonObject PersistBinding(DatabaseSession c, long entryId, string sourceId, JsonObject manga, string sourceName = "",
        string? groupId = null, bool ensureGroup = true, bool regroup = true)
    {
        entryId = Canonical(c, entryId);
        var mangaId = Integer(manga["id"]);
        if (mangaId <= 0) mangaId = Integer(manga["source_manga_id"]);
        if (mangaId <= 0) throw new ArgumentException("Manual match has no source manga id", nameof(manga));
        var title = (Text(manga["title"]) is { Length: > 0 } primary ? primary : Text(manga["source_title"])).Trim();
        var name = SourceName(c, sourceId, sourceName);
        var stamp = Stamp();
        var old = c.Query("SELECT group_id FROM manual_match_bindings WHERE source_id=$p0 AND source_manga_id=$p1", P(sourceId, mangaId));
        var chosen = groupId ?? (old.Count > 0 ? old[0].Text("group_id") : "");
        c.Execute("""
            INSERT INTO manual_match_bindings(source_id,source_manga_id,group_id,entry_id_hint,source_name,source_title,record_json,created_at,updated_at)
            VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p7)
            ON CONFLICT(source_id,source_manga_id) DO UPDATE SET
              group_id=CASE WHEN excluded.group_id<>'' THEN excluded.group_id ELSE manual_match_bindings.group_id END,
              entry_id_hint=excluded.entry_id_hint,
              source_name=CASE WHEN excluded.source_name<>'' THEN excluded.source_name ELSE manual_match_bindings.source_name END,
              source_title=CASE WHEN excluded.source_title<>'' THEN excluded.source_title ELSE manual_match_bindings.source_title END,
              record_json=CASE WHEN length(excluded.record_json)>2 THEN excluded.record_json ELSE manual_match_bindings.record_json END,
              grouping_suppressed=CASE WHEN $p8 THEN 0 ELSE manual_match_bindings.grouping_suppressed END,
              updated_at=excluded.updated_at
            """, P(sourceId, mangaId, chosen.Length == 0 ? null : chosen, entryId, name, title, manga.ToJsonString(), stamp, regroup ? 1 : 0));
        if (ensureGroup) EnsureGroup(c, entryId);
        return new JsonObject { ["source_id"] = sourceId, ["source_manga_id"] = mangaId, ["entry_id"] = entryId };
    }

    /// <summary>_persist_manual_binding for other areas (logged as manual.bind).</summary>
    public JsonObject PersistManualBinding(long entryId, string sourceId, JsonObject manga, DecisionActor actor, string sourceName = "",
        string? groupId = null, bool ensureGroup = true, bool regroup = true)
    {
        ArgumentNullException.ThrowIfNull(manga); ArgumentNullException.ThrowIfNull(actor);
        return Write(c =>
        {
            var result = PersistBinding(c, entryId, sourceId, manga, sourceName, groupId, ensureGroup, regroup);
            Log("manual.bind", actor, [Integer(result["entry_id"])], [RecordKey(sourceId, Integer(result["source_manga_id"]))], "Manual source binding saved");
            return result;
        });
    }

    internal bool ForgetBinding(DatabaseSession c, string sourceId, long sourceMangaId, bool dissolveSmallGroup)
    {
        var rows = c.Query("SELECT group_id FROM manual_match_bindings WHERE source_id=$p0 AND source_manga_id=$p1", P(sourceId, sourceMangaId));
        if (rows.Count == 0) return false;
        var group = rows[0].Text("group_id");
        c.Execute("DELETE FROM manual_match_bindings WHERE source_id=$p0 AND source_manga_id=$p1", P(sourceId, sourceMangaId));
        if (group.Length > 0 && dissolveSmallGroup) DissolveIfSmall(c, group);
        return true;
    }

    /// <summary>_forget_manual_binding (no log entry: the caller logs the change that made the binding obsolete).</summary>
    public bool ForgetManualBinding(string sourceId, long sourceMangaId, bool dissolveSmallGroup = true)
        => Write(c => ForgetBinding(c, sourceId, sourceMangaId, dissolveSmallGroup));

    internal bool DissolveIfSmall(DatabaseSession c, string groupId)
    {
        if (string.IsNullOrEmpty(groupId)) return false;
        var count = Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM manual_match_bindings WHERE group_id=$p0", P(groupId)), CultureInfo.InvariantCulture);
        if (count >= 2) return false;
        c.Execute("UPDATE manual_match_bindings SET group_id=NULL,updated_at=$p1 WHERE group_id=$p0", P(groupId, Stamp()));
        c.Execute("DELETE FROM manual_match_groups WHERE id=$p0", P(groupId));
        return true;
    }

    /// <summary>_dissolve_manual_group_if_small.</summary>
    public bool DissolveManualGroupIfSmall(string groupId) => Write(c => DissolveIfSmall(c, groupId));

    internal string? EnsureGroup(DatabaseSession c, long entryId)
    {
        entryId = Canonical(c, entryId);
        var bindings = c.Query("SELECT * FROM manual_match_bindings WHERE entry_id_hint=$p0 AND grouping_suppressed=0 ORDER BY created_at,source_id,source_manga_id", P(entryId));
        if (bindings.Count < 2) return null;
        var existing = bindings.Select(row => row.Text("group_id")).Where(id => id.Length > 0).ToList();
        var groupId = existing.Count > 0 ? existing[0] : Guid.NewGuid().ToString("N");
        var entry = c.Query("SELECT preferred_title FROM entries WHERE id=$p0", P(entryId));
        var title = entry.Count > 0 ? entry[0].Text("preferred_title") : bindings[0].Text("source_title", "Manual group");
        var stamp = Stamp();
        c.Execute("""
            INSERT INTO manual_match_groups(id,title,created_at,updated_at) VALUES($p0,$p1,$p2,$p2)
            ON CONFLICT(id) DO UPDATE SET title=CASE WHEN manual_match_groups.title='' THEN excluded.title ELSE manual_match_groups.title END,
                                          updated_at=excluded.updated_at
            """, P(groupId, title, stamp));
        foreach (var other in existing.Where(id => id != groupId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            c.Execute("UPDATE manual_match_bindings SET group_id=$p0,updated_at=$p1 WHERE group_id=$p2", P(groupId, stamp, other));
            c.Execute("DELETE FROM manual_match_groups WHERE id=$p0", P(other));
        }
        c.Execute("UPDATE manual_match_bindings SET group_id=$p0,entry_id_hint=$p1,updated_at=$p2 WHERE entry_id_hint=$p1 AND grouping_suppressed=0", P(groupId, entryId, stamp));
        return groupId;
    }

    /// <summary>_ensure_manual_group_for_entry: forms or extends a durable group once two explicit matches share one entry.</summary>
    public string? EnsureManualGroupForEntry(long entryId) => Write(c => EnsureGroup(c, entryId));

    private const string RecordColumns = "SELECT $p0 kind,r.*,COALESCE(sc.display_name,sc.name,r.source_id) source_name FROM {0} r LEFT JOIN source_config sc ON sc.source_id=r.source_id";

    private static string Records(string table, string where) => string.Format(CultureInfo.InvariantCulture, RecordColumns, table) + " WHERE " + where;

    /// <summary>
    /// _capture_existing_manual_matches (startup): binds every active record marked manual that has no binding for its
    /// entry yet, in batches of 100, then completes groups a stop left half-formed. Returns the records captured.
    /// </summary>
    public int CaptureExistingManualMatches()
    {
        var rows = Read(c => new[] { ("source_matches", "primary"), ("source_variants", "variant") }.SelectMany(item => c.Query(Records(item.Item1,
            """
            r.active=1 AND lower(r.match_type)='manual'
            AND NOT EXISTS(SELECT 1 FROM manual_match_bindings mb WHERE mb.source_id=r.source_id AND mb.source_manga_id=r.source_manga_id AND mb.entry_id_hint=r.entry_id)
            """), P(item.Item2))).ToList());
        for (var offset = 0; offset < rows.Count; offset += 100)
        {
            var batch = rows.Skip(offset).Take(100).ToList();
            Write(c =>
            {
                var touched = new SortedSet<long>();
                foreach (var row in batch)
                {
                    PersistBinding(c, row.Integer("entry_id"), row.Text("source_id"), ManualSql.Payload(row), row.Text("source_name"), ensureGroup: false, regroup: false);
                    touched.Add(Canonical(c, row.Integer("entry_id")));
                }
                foreach (var entry in touched) EnsureGroup(c, entry);
                if (batch.Count > 0)
                    Log("manual.capture", DecisionActor.Upgrade("capture-existing-manual-matches"), touched,
                        batch.Select(row => RecordKey(row.Text("source_id"), row.Integer("source_manga_id"))), "Recovered explicit matches made before persistent bindings");
                return 0;
            });
        }
        var incomplete = Read(c => c.QueryAs<long>("""
            SELECT mb.entry_id_hint FROM manual_match_bindings mb JOIN entries e ON e.id=mb.entry_id_hint
            WHERE mb.grouping_suppressed=0 GROUP BY mb.entry_id_hint
            HAVING COUNT(*)>=2 AND (COUNT(NULLIF(mb.group_id,''))<COUNT(*) OR COUNT(DISTINCT mb.group_id)>1)
            """).ToList());
        for (var offset = 0; offset < incomplete.Count; offset += 100)
        {
            var batch = incomplete.Skip(offset).Take(100).ToList();
            Write(c => { foreach (var entry in batch) EnsureGroup(c, entry); return 0; });
        }
        return rows.Count;
    }

    internal static DatabaseRow? RecordRow(DatabaseSession c, string sourceId, long sourceMangaId)
    {
        foreach (var (table, kind) in new[] { ("source_matches", "primary"), ("source_variants", "variant") })
        {
            var rows = c.Query(Records(table, "r.source_id=$p1 AND r.source_manga_id=$p2 AND r.active=1 LIMIT 1"), P(kind, sourceId, sourceMangaId));
            if (rows.Count > 0) return rows[0];
        }
        return null;
    }

    /// <summary>_manual_record_row: the active record (primary first) with its kind and source name, or null.</summary>
    public JsonObject? ManualRecordRow(string sourceId, long sourceMangaId) => Read(c => RecordRow(c, sourceId, sourceMangaId) is { } row ? Json(row) : null);

    /// <summary>_duplicate_review_manual_records: the active records a same-work review covers (snapshot before the merge).</summary>
    public IReadOnlyList<JsonObject> DuplicateReviewManualRecords(IEnumerable<long> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var requested = entryIds.ToArray();
        return Read<IReadOnlyList<JsonObject>>(c =>
        {
            var ids = requested.Select(id => Canonical(c, id)).Distinct().ToArray();
            if (ids.Length == 0) return System.Array.Empty<JsonObject>();
            var list = System.Text.Json.JsonSerializer.Serialize(ids);
            return new[] { ("source_matches", "primary"), ("source_variants", "variant") }
                .SelectMany(item => c.Query(Records(item.Item1, "r.active=1 AND r.entry_id IN (SELECT value FROM json_each($p1))"), P(item.Item2, list)))
                .Select(Json).ToArray();
        });
    }

    /// <summary>_persist_duplicate_review_manual_group: locks and binds the records a same-work review merged, then groups them.</summary>
    public JsonObject PersistDuplicateReviewManualGroup(long canonicalEntryId, IReadOnlyList<JsonObject> records, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(records); ArgumentNullException.ThrowIfNull(actor);
        return Write(c =>
        {
            var target = Canonical(c, canonicalEntryId);
            var persisted = 0; var keys = new List<string>();
            foreach (var row in records)
            {
                var sourceId = Text(row["source_id"]); var mangaId = Integer(row["source_manga_id"]);
                if (sourceId.Length == 0 || mangaId <= 0) continue;
                var stamp = Stamp();
                foreach (var table in new[] { "source_matches", "source_variants" })
                    c.Execute($"UPDATE {table} SET match_type='manual',locked=1,last_checked=$p0 WHERE entry_id=$p1 AND source_id=$p2 AND source_manga_id=$p3 AND active=1", P(stamp, target, sourceId, mangaId));
                var payload = RowPayload(row);
                PersistBinding(c, target, sourceId, payload, Text(row["source_name"]), ensureGroup: false);
                persisted++; keys.Add(RecordKey(sourceId, mangaId));
            }
            var group = EnsureGroup(c, target);
            if (persisted > 0) Log("manual.group.review", actor, [target], keys, "Same-work review kept as a manual group", after: new JsonObject { ["manual_group_id"] = group });
            return new JsonObject { ["manual_group_id"] = group, ["manual_group_members"] = persisted };
        });
    }

    /// <summary>A JSON record row (as returned by <see cref="DuplicateReviewManualRecords"/>) as a source manga payload.</summary>
    private static JsonObject RowPayload(JsonObject row)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in row)
        {
            object? cell = null;
            if (value is JsonValue item)
            {
                if (item.TryGetValue<long>(out var number)) cell = number;
                else if (item.TryGetValue<double>(out var real)) cell = real;
                else if (item.TryGetValue<bool>(out var flag)) cell = flag ? 1L : 0L;
                else cell = Text(item);
            }
            values[key] = cell;
        }
        return ManualSql.Payload(new DatabaseRow(values));
    }

    /// <summary>
    /// _claim_manual_source_record: inside the caller's catalogue write, deactivates the exact record on every other entry
    /// so the user's choice wins over stale automatic ownership. Returns the entries it was taken from.
    /// </summary>
    public IReadOnlySet<long> ClaimManualSourceRecord(DatabaseSession c, long entryId, string sourceId, long sourceMangaId, string stamp)
    {
        ArgumentNullException.ThrowIfNull(c);
        entryId = Canonical(c, entryId);
        var displaced = new HashSet<long>();
        foreach (var table in new[] { "source_matches", "source_variants" })
            displaced.UnionWith(c.QueryAs<long>($"SELECT entry_id FROM {table} WHERE source_id=@sid AND source_manga_id=@mid AND active=1 AND entry_id<>@entry",
                new { sid = sourceId, mid = sourceMangaId, entry = entryId }));
        if (displaced.Count > 0)
            foreach (var table in new[] { "source_matches", "source_variants" })
                c.Execute($"UPDATE {table} SET active=0,locked=0,match_type='manual-moved',last_checked=$p0 WHERE source_id=$p1 AND source_manga_id=$p2 AND active=1 AND entry_id<>$p3",
                    P(stamp, sourceId, sourceMangaId, entryId));
        return displaced;
    }

    /// <summary>_cleanup_manual_source_displacement: removes titles and chapter candidates a forced manual move left behind.</summary>
    public void CleanupManualSourceDisplacement(IEnumerable<long> entryIds, string sourceId, long sourceMangaId)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var owners = Read(c => entryIds.Where(id => id > 0).Select(id => Canonical(c, id)).Distinct().Order().ToArray());
        foreach (var owner in owners)
        {
            _operations?.PurgeSourceRecordTitles(owner, sourceId, [sourceMangaId]);
            DeactivateCandidates(owner, sourceId, sourceMangaId);
            try { _operations?.ReconcileChapters(owner); _operations?.ChooseMetadata(owner); }
            catch (KeyNotFoundException) { }
            _operations?.Invalidate(["entry-" + owner.ToString(CultureInfo.InvariantCulture), "source-" + sourceMangaId.ToString(CultureInfo.InvariantCulture)]);
        }
    }

    /// <summary>Deactivates the chapter candidates of one record (or of the whole source when <paramref name="sourceMangaId"/> is null) on an entry.</summary>
    internal void DeactivateCandidates(long entryId, string sourceId, long? sourceMangaId) => _database.Write(DatabaseFile.Origins, o => o.Execute("""
        UPDATE chapter_candidates SET active=0
        WHERE source_id=$p0 AND ($p1 IS NULL OR source_manga_id=$p1) AND virtual_chapter_id IN (SELECT id FROM virtual_chapters WHERE entry_id=$p2)
        """, P(sourceId, sourceMangaId, entryId)));

    /// <summary>
    /// _persist_manual_split_decision: after the user split <paramref name="selected"/> records off into <paramref name="newId"/>,
    /// binds them there (out of their old groups), keeps them separate from the original, anchors the original by one
    /// remaining record and marks the two entries as different works.
    /// </summary>
    public void PersistManualSplitDecision(long originalId, long newId, IReadOnlyList<JsonObject> rows, IReadOnlyList<JsonObject> selected, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(rows); ArgumentNullException.ThrowIfNull(selected); ArgumentNullException.ThrowIfNull(actor);
        var chosen = selected.Select(row => (Text(row["source_id"]), Integer(row["source_manga_id"]))).ToHashSet();
        var remaining = rows.Where(row => !chosen.Contains((Text(row["source_id"]), Integer(row["source_manga_id"])))).ToList();
        if (remaining.Count == 0) throw new ManualDecisionConflictException("A split must leave at least one record on the original entry");
        var anchor = remaining.OrderBy(row => Text(row["kind"]) != "primary").ThenBy(row => Integer(row["id"])).First();
        Write(c =>
        {
            var oldGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in selected)
            {
                var sourceId = Text(row["source_id"]); var mangaId = Integer(row["source_manga_id"]);
                if (c.Scalar("SELECT group_id FROM manual_match_bindings WHERE source_id=$p0 AND source_manga_id=$p1", P(sourceId, mangaId)) is string group && group.Length > 0)
                    oldGroups.Add(group);
                c.Execute("UPDATE manual_match_bindings SET group_id=NULL,entry_id_hint=$p0,updated_at=$p1 WHERE source_id=$p2 AND source_manga_id=$p3", P(newId, Stamp(), sourceId, mangaId));
                foreach (var table in new[] { "source_matches", "source_variants" })
                    c.Execute($"UPDATE {table} SET locked=1 WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2 AND active=1", P(newId, sourceId, mangaId));
                PersistBinding(c, newId, sourceId, RowPayload(row), ensureGroup: false);
                RememberSeparate(c, originalId, sourceId, mangaId, newId, Text(row["source_title"]));
            }
            var anchorSource = Text(anchor["source_id"]); var anchorManga = Integer(anchor["source_manga_id"]);
            if (c.Scalar("SELECT 1 FROM manual_match_bindings WHERE source_id=$p0 AND source_manga_id=$p1", P(anchorSource, anchorManga)) is null)
            {
                PersistBinding(c, originalId, anchorSource, RowPayload(anchor), ensureGroup: false, regroup: false);
                foreach (var table in new[] { "source_matches", "source_variants" })
                    c.Execute($"UPDATE {table} SET locked=1 WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2", P(originalId, anchorSource, anchorManga));
            }
            foreach (var group in oldGroups) DissolveIfSmall(c, group);
            EnsureGroup(c, newId);
            ReviewPair(c, originalId, newId, "not_duplicate");
            Log("split.manual", actor, [originalId, newId], selected.Select(row => RecordKey(Text(row["source_id"]), Integer(row["source_manga_id"]))),
                "Records split into a separate entry", after: new JsonObject { ["original_entry_id"] = originalId, ["new_entry_id"] = newId });
            return 0;
        });
    }

    /// <summary>clear_manual_titles: removes title overrides and manual aliases; the main title returns to the bound-record consensus.</summary>
    public JsonObject ClearManualTitles(long entryId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var target = Write(c =>
        {
            var id = Canonical(c, entryId);
            var entry = c.Query("SELECT preferred_title,manual_preferred_title FROM entries WHERE id=$p0", P(id));
            if (entry.Count == 0) throw new KeyNotFoundException("Entry not found");
            var before = new JsonObject
            {
                ["preferred_title"] = entry[0].Text("preferred_title"), ["manual_preferred_title"] = entry[0].Flag("manual_preferred_title"),
                ["override"] = c.Scalar("SELECT title FROM preferred_title_overrides WHERE entry_id=$p0", P(id)) as string,
                ["aliases"] = JsonList(c.QueryAs<string>("SELECT title FROM titles WHERE entry_id=@id AND title_type='manual' ORDER BY id", new { id }).Select(title => (JsonNode?)title)),
            };
            c.Execute("DELETE FROM preferred_title_overrides WHERE entry_id=$p0", P(id));
            c.Execute("DELETE FROM manual_title_scopes WHERE entry_id=$p0", P(id));
            c.Execute("DELETE FROM titles WHERE entry_id=$p0 AND title_type='manual'", P(id));
            // Legacy source-keyed title inheritance is purged too (0.4.5).
            c.Execute("DELETE FROM preferred_title_override_sources");
            c.Execute("UPDATE entries SET manual_preferred_title=0,updated_at=$p0 WHERE id=$p1", P(Stamp(), id));
            Log("title.manual.clear", actor, [id], [], "Manual titles removed", before);
            return id;
        });
        _operations?.SyncPreferredTitle(target);
        _operations?.ChooseMetadata(target);
        _operations?.Invalidate([]);
        return new JsonObject { ["entry_id"] = target, ["cleared"] = true };
    }

    /// <summary>
    /// _manual_candidate_manga: reduces a manual-search card to the source manga shape so a click can commit locally;
    /// empty when the card is for another record or has no title.
    /// </summary>
    public static JsonObject ManualCandidateManga(long sourceMangaId, JsonObject? candidate)
    {
        if (candidate is null) return new JsonObject();
        var id = Integer(candidate["id"]);
        if (id == 0) id = Integer(candidate["source_manga_id"]);
        var title = (Text(candidate["title"]) is { Length: > 0 } primary ? primary : Text(candidate["source_title"])).Trim();
        if (id != sourceMangaId || title.Length == 0) return new JsonObject();
        static string Pick(JsonObject card, string first, string second) => Text(card[first]) is { Length: > 0 } value ? value : Text(card[second]);
        var genre = candidate["genre"] switch
        {
            JsonArray array => (JsonArray)array.DeepClone(),
            JsonValue value when value.TryGetValue<string>(out var text) => JsonList(text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(item => (JsonNode?)item)),
            _ => new JsonArray(),
        };
        return new JsonObject
        {
            ["id"] = sourceMangaId, ["title"] = title, ["url"] = Pick(candidate, "url", "source_manga_url"), ["thumbnailUrl"] = Pick(candidate, "thumbnailUrl", "thumbnail_url"),
            ["description"] = Text(candidate["description"]), ["author"] = Text(candidate["author"]), ["artist"] = Text(candidate["artist"]),
            ["genre"] = genre, ["status"] = Integer(candidate["status"]), ["initialized"] = candidate["initialized"] is null || Truth(candidate["initialized"]),
            ["realUrl"] = Pick(candidate, "realUrl", "real_url"),
        };
    }
}
