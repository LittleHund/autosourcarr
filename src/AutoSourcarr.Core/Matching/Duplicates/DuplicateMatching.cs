using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using static AutoSourcarr.Core.Matching.Duplicates.DuplicateJson;

namespace AutoSourcarr.Core.Matching.Duplicates;

/// <summary>Progress of long duplicate work (current, total, message); jobs forward it to <see cref="JobContext.Report"/>.</summary>
public delegate void DuplicateProgress(int? current, int? total, string? message);

/// <summary>
/// Phase 8C duplicate review and automatic merging (port of the service.py "DuplicateReview / AutomaticMerge" group):
/// the candidate shortlist and review snapshot, manual reviews, non-transitive automatic merges through an
/// <see cref="IAutomaticMatchGate"/>, consolidation, record unmatching, unbinding and Redo automation, and the
/// automatic-merge safety repair. Merges and splits use the native CatalogueMerge / CatalogueSplit primitives through
/// <see cref="IDuplicateStructure"/>. Every merge, split and review is written to the decision log with its actor.
/// Manual decisions (reviews, manual groups, separated records, locked records) are never undone automatically.
/// </summary>
public sealed partial class DuplicateMatching
{
    public static readonly JobType AutomaticJob = new("duplicate-automatic", "Automatic duplicate matching", JobPriority.Low, JobFlags.LongRunning, StallTimeout: TimeSpan.FromMinutes(30));
    public static readonly JobType ConsolidateJob = new("duplicate-consolidate", "Auto-merge duplicates", JobPriority.Normal, JobFlags.LongRunning, StallTimeout: TimeSpan.FromMinutes(30));
    public static readonly JobType SnapshotJob = new("duplicate-snapshot", "Refresh duplicate review", JobPriority.Low, JobFlags.LongRunning, StallTimeout: TimeSpan.FromMinutes(30));
    public static readonly JobType RedoJob = new("entry-automation-redo", "Redo entry automation", JobPriority.Normal, StallTimeout: TimeSpan.FromMinutes(20));
    public const string SnapshotSetting = "duplicate_review_snapshot_v1";
    public const string SafetyRepairVersion = "0.2.69";

    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly IDuplicateStructure _structure;
    private readonly DuplicateEvidence _evidence;
    private readonly IDecisionLog _decisions;
    private readonly Func<AutoSourcarrSettings> _settings;
    private readonly IAutomaticMatchGate _matchGate;
    private readonly IDuplicateManualView _manual;
    private readonly SettingsRepository _stored;
    private readonly TimeProvider _clock;
    private readonly Action<DatabaseSession, long, string>? _chooseMetadata;
    private readonly object _pauseLock = new();
    private readonly Dictionary<string, int> _pauses = new(StringComparer.Ordinal);
    private readonly object _snapshotLock = new();
    private readonly SemaphoreSlim _scan = new(1, 1);
    private (long At, JsonArray Groups)? _snapshot;
    private bool _snapshotStale;
    private long _snapshotRevision;

    public DuplicateMatching(SqliteDatabase database, object sourceWriteGate, IDuplicateStructure structure,
        Func<DatabaseSession, long, JsonObject> nativeSnapshot, IDecisionLog decisions, Func<AutoSourcarrSettings> settings,
        IAutomaticMatchGate? gate = null, IDuplicateManualView? manual = null, Action<DatabaseSession, long, string>? chooseMetadata = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate); ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(nativeSnapshot); ArgumentNullException.ThrowIfNull(decisions); ArgumentNullException.ThrowIfNull(settings);
        _database = database; _gate = sourceWriteGate; _structure = structure; _evidence = new DuplicateEvidence(nativeSnapshot);
        _decisions = decisions; _settings = settings; _clock = clock ?? TimeProvider.System; _chooseMetadata = chooseMetadata;
        _matchGate = gate ?? new TextAutomaticMatchGate(settings);
        _manual = manual ?? new CatalogueDuplicateManualView(database, _clock);
        _stored = new SettingsRepository(database);
    }

    /// <summary>The gate automatic merges use (replaceable at construction by the phase 8A scorer).</summary>
    public IAutomaticMatchGate Gate => _matchGate;
    public DuplicateEvidence Evidence => _evidence;
    /// <summary>Optional queue for snapshot refreshes and automatic sweeps scheduled by this service.</summary>
    public JobQueue? Jobs { get; set; }
    /// <summary>Optional identity / thumbnail hydration before automatic sweeps.</summary>
    public IDuplicateHydration? Hydration { get; set; }
    /// <summary>Optional re-search and refresh after Redo automation.</summary>
    public IEntryAutomationRedo? Redo { get; set; }
    /// <summary>Optional CatalogueMaintenance repair of unsafe automatic source attachments (run before the merge repair).</summary>
    public Func<JsonObject>? SourceAttachmentRepair { get; set; }
    /// <summary>Set when catalogue evidence changed while a sweep could not run; the next sweep clears it.</summary>
    public bool RescanRequested { get; private set; }

    private string Stamp() => _clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    private T Read<T>(Func<DatabaseSession, T> work) => _database.Read(DatabaseFile.Catalog, work);
    private T Write<T>(Func<DatabaseSession, T> work) => _database.Write(DatabaseFile.Catalog, work);
    private static string Key(NativeSourceRecord record) => record.SourceId + ":" + record.MangaId.ToString(CultureInfo.InvariantCulture);

    // ---- pause (automatic_duplicate_pause_reasons / begin / end / pause_automatic_duplicate_matching) ----

    public IReadOnlyList<string> PauseReasons() { lock (_pauseLock) return _pauses.Keys.Order(StringComparer.Ordinal).ToArray(); }
    public bool Paused { get { lock (_pauseLock) return _pauses.Count != 0; } }

    public string BeginPause(string reason)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? "maintenance" : reason.Trim();
        lock (_pauseLock) _pauses[reason] = _pauses.GetValueOrDefault(reason) + 1;
        RescanRequested = true;
        return reason;
    }

    public void EndPause(string reason, bool schedule = true)
    {
        bool still;
        lock (_pauseLock)
        {
            var count = _pauses.GetValueOrDefault(reason);
            if (count > 1) _pauses[reason] = count - 1; else _pauses.Remove(reason);
            still = _pauses.Count != 0;
        }
        if (schedule && !still && _settings().AutomaticDuplicateMatching) { RescanRequested = false; ScheduleAutomatic(TimeSpan.FromSeconds(1)); }
    }

    /// <summary>Stops automatic sweeps while authoritative rebuilds run; dispose to resume.</summary>
    public IDisposable PauseAutomatic(string reason) => new PauseScope(this, BeginPause(reason));

    private sealed class PauseScope(DuplicateMatching owner, string reason) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) owner.EndPause(reason); }
    }

    public JsonObject AutomaticStatus()
    {
        var settings = _settings();
        long Number(string key) => Long(_stored.Get(key));
        var running = Jobs?.Snapshot()["running"] is JsonArray active && active.OfType<JsonObject>().Any(job => Str(job["type"]) == AutomaticJob.Name);
        var reasons = PauseReasons();
        return new JsonObject
        {
            ["enabled"] = settings.AutomaticDuplicateMatching, ["running"] = running, ["paused"] = reasons.Count != 0, ["pause_reasons"] = JArray(reasons),
            ["last_run"] = Str(_stored.Get("automatic_duplicate_last_run")), ["last_checked"] = Number("automatic_duplicate_last_checked"),
            ["last_merged"] = Number("automatic_duplicate_last_merged"), ["last_error"] = Str(_stored.Get("automatic_duplicate_last_error")),
            ["summary_threshold"] = Math.Clamp(settings.DuplicateAutoMergeSummaryThreshold, 0, 1),
        };
    }

    // ---- reviews (review_duplicate / _review_duplicate_locked / _remap is done by CatalogueMerge) ----

    /// <summary>Records a person's duplicate decision: "duplicate" merges and binds the records as one manual group,
    /// "not_duplicate" keeps them apart for good, "clear" forgets an earlier decision.</summary>
    public JsonObject Review(long leftId, long rightId, string decision, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (decision is not ("duplicate" or "not_duplicate" or "clear")) throw new ArgumentException("Unsupported duplicate decision", nameof(decision));
        lock (_gate)
        {
            var (left, right) = Read(c => (DuplicateEvidence.Canonical(c, leftId), DuplicateEvidence.Canonical(c, rightId)));
            if (!Read(c => Exists(c, left) && Exists(c, right))) throw new KeyNotFoundException("Entry not found");
            var result = ReviewLocked(left, right, decision, actor, "Duplicate review");
            Invalidate();
            result["groups"] = Prune([left, right], Str(result["decision"]));
            result["pending"] = true;
            return result;
        }
    }

    private JsonObject ReviewLocked(long leftId, long rightId, string decision, DecisionActor actor, string why)
    {
        var (left, right) = Read(c => Pair(DuplicateEvidence.Canonical(c, leftId), DuplicateEvidence.Canonical(c, rightId)));
        if (left == right) return new JsonObject { ["decision"] = "duplicate", ["canonical_entry_id"] = left };
        var stamp = Stamp();
        if (decision == "clear")
        {
            var previous = Write(c =>
            {
                var old = c.Scalar("SELECT decision FROM duplicate_reviews WHERE left_entry_id=$p0 AND right_entry_id=$p1", P(left, right)) as string ?? "";
                c.Execute("DELETE FROM duplicate_reviews WHERE left_entry_id=$p0 AND right_entry_id=$p1", P(left, right));
                return old;
            });
            _decisions.Record(new DecisionEvent("review.duplicate.clear", actor, [left, right], [], why, new JsonObject { ["decision"] = previous }, new JsonObject { ["decision"] = "" }));
            Invalidate();
            return new JsonObject { ["decision"] = "clear", ["left_entry_id"] = left, ["right_entry_id"] = right };
        }
        if (decision == "duplicate")
        {
            var records = Read(c => ActiveRecords(c, left).Concat(ActiveRecords(c, right)).Select(row => new NativeSourceRecord(row.Text("source_id"), row.Integer("source_manga_id"))).Distinct().ToArray());
            var before = Read(c => new JsonObject { ["left"] = Brief(c, left), ["right"] = Brief(c, right) });
            var canonical = _structure.MergeEntries(left, right, "manual-duplicate-review");
            Write(c => c.Execute("INSERT INTO duplicate_review_history(left_entry_id,right_entry_id,decision,reviewed_at) VALUES($p0,$p1,'duplicate',$p2)", P(left, right, stamp)));
            var group = _manual.PersistReviewedGroup(canonical, records);
            _decisions.Record(new DecisionEvent("merge.manual", actor, [canonical, canonical == left ? right : left], records.Select(Key).ToArray(), why, before,
                new JsonObject { ["canonical_entry_id"] = canonical, ["manual_group_id"] = group["manual_group_id"]?.DeepClone() }));
            Invalidate();
            return new JsonObject { ["decision"] = decision, ["canonical_entry_id"] = canonical, ["merged"] = true, ["manual_group_id"] = group["manual_group_id"]?.DeepClone(), ["manual_group_members"] = records.Length };
        }
        var prior = Write(c =>
        {
            var old = c.Scalar("SELECT decision FROM duplicate_reviews WHERE left_entry_id=$p0 AND right_entry_id=$p1", P(left, right)) as string ?? "";
            c.Execute("INSERT INTO duplicate_review_history(left_entry_id,right_entry_id,decision,reviewed_at) VALUES($p0,$p1,$p2,$p3)", P(left, right, decision, stamp));
            c.Execute("INSERT INTO duplicate_reviews(left_entry_id,right_entry_id,decision,reviewed_at) VALUES($p0,$p1,$p2,$p3) ON CONFLICT(left_entry_id,right_entry_id) DO UPDATE SET decision=excluded.decision,reviewed_at=excluded.reviewed_at", P(left, right, decision, stamp));
            return old;
        });
        _decisions.Record(new DecisionEvent("review.duplicate", actor, [left, right], [], why, new JsonObject { ["decision"] = prior }, new JsonObject { ["decision"] = decision }));
        Invalidate();
        return new JsonObject { ["decision"] = decision, ["left_entry_id"] = left, ["right_entry_id"] = right, ["merged"] = false };
    }

    // ---- separate / allow a source record (allow_source_record_match / separate_source_record) ----

    /// <summary>Forgets an explicit "keep separate" for this record (and the matching not-duplicate review).</summary>
    public JsonObject AllowSourceRecord(long entryId, string sourceId, long sourceMangaId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            var target = Read(c => DuplicateEvidence.Canonical(c, entryId));
            if (!Read(c => Exists(c, target))) throw new KeyNotFoundException("Entry not found");
            var other = Write(c =>
            {
                var hint = c.Query("SELECT separate_entry_id_hint FROM manual_separate_records WHERE target_entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2", P(target, sourceId, sourceMangaId)).FirstOrDefault()?.Integer("separate_entry_id_hint") ?? 0;
                c.Execute("DELETE FROM manual_separate_records WHERE target_entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2", P(target, sourceId, sourceMangaId));
                return hint;
            });
            _decisions.Record(new DecisionEvent("separate.allow", actor, [target], [Key(new(sourceId, sourceMangaId))], "Allow source record match"));
            if (other != 0 && Read(c => Exists(c, DuplicateEvidence.Canonical(c, other)))) ReviewLocked(target, other, "clear", actor, "Allow source record match");
            Invalidate();
            return new JsonObject { ["entry_id"] = target, ["source_id"] = sourceId, ["source_manga_id"] = sourceMangaId, ["allowed"] = true };
        }
    }

    /// <summary>
    /// Keeps a source record apart from an entry and remembers it (_commit_separate_source_record). An attached record
    /// is split away as a manual decision; a record owned by another entry only gets the separation and a not-duplicate
    /// review; an unknown record needs <paramref name="createUnowned"/> (EntryIngest creates its entry).
    /// </summary>
    public JsonObject SeparateSourceRecord(long entryId, string sourceId, long sourceMangaId, string sourceTitle, DecisionActor actor, Func<long>? createUnowned = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            var target = Read(c => DuplicateEvidence.Canonical(c, entryId));
            if (!Read(c => Exists(c, target))) throw new KeyNotFoundException("Entry not found");
            var record = new NativeSourceRecord(sourceId, sourceMangaId);
            var owner = Read(c => Owner(c, record));
            long separate;
            if (owner == target)
            {
                if (Read(c => ActiveRecords(c, target).Count) <= 1) throw new InvalidOperationException("This is the entry's only source record, so it cannot be split away from itself");
                separate = _structure.SplitRecords(target, [record], manual: true, "split-duplicate");
            }
            else if (owner is { } existing) separate = existing;
            else separate = createUnowned?.Invoke() ?? throw new InvalidOperationException("Source record is not in the catalogue yet; open it from a source first");
            if (separate != 0 && separate != target)
            {
                var stamp = Stamp();
                Write(c => c.Execute("""
                    INSERT INTO manual_separate_records(target_entry_id,source_id,source_manga_id,separate_entry_id_hint,source_title,created_at,updated_at) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p5)
                    ON CONFLICT(target_entry_id,source_id,source_manga_id) DO UPDATE SET separate_entry_id_hint=COALESCE(excluded.separate_entry_id_hint,manual_separate_records.separate_entry_id_hint),
                      source_title=CASE WHEN excluded.source_title<>'' THEN excluded.source_title ELSE manual_separate_records.source_title END,updated_at=excluded.updated_at
                    """, P(target, sourceId, sourceMangaId, separate, sourceTitle, stamp)));
                ReviewLocked(target, separate, "not_duplicate", actor, "Separate source record");
            }
            _decisions.Record(new DecisionEvent("separate", actor, [target, separate], [Key(record)], "Keep source record separate", null, new JsonObject { ["separate_entry_id"] = separate }));
            Invalidate();
            return new JsonObject { ["entry_id"] = target, ["separate_entry_id"] = separate, ["source_id"] = sourceId, ["source_manga_id"] = sourceMangaId, ["separate"] = true };
        }
    }

    // ---- automatic merge evidence (_auto_merge_group_evidence / _auto_merge_evidence / _merge_entries_automatic) ----

    /// <summary>
    /// Anti-snowball gate: every original member on both sides must prove the relationship directly
    /// (A-B and B-C never imply A-C). Manual not-duplicate reviews and separated records always win.
    /// </summary>
    public JsonObject GroupEvidence(long leftId, long rightId, IDictionary<long, List<JsonObject>>? memberCache = null)
    {
        var (left, right) = Read(c => (DuplicateEvidence.Canonical(c, leftId), DuplicateEvidence.Canonical(c, rightId)));
        if (left == right) return new JsonObject { ["eligible"] = true, ["reason"] = "same-entry", ["direct_pairs"] = 0 };
        if (Read(c => DuplicateEvidence.Review(c, left, right)) == "not_duplicate" || _manual.ReviewedDifferent(left, right))
            return new JsonObject { ["eligible"] = false, ["reason"] = "reviewed-not-duplicate", ["direct_pairs"] = 0 };
        var separated = Read(c => ActiveRecords(c, right).Any(row => _manual.SeparatedRecord(left, row.Text("source_id"), row.Integer("source_manga_id")))
            || ActiveRecords(c, left).Any(row => _manual.SeparatedRecord(right, row.Text("source_id"), row.Integer("source_manga_id"))));
        if (separated) return new JsonObject { ["eligible"] = false, ["reason"] = "a source record was manually kept separate", ["direct_pairs"] = 0 };
        List<JsonObject> Members(long id)
        {
            if (memberCache is not null && memberCache.TryGetValue(id, out var cached)) return cached;
            var value = Read(c => _evidence.MemberSnapshots(c, id));
            if (memberCache is not null) memberCache[id] = value;
            return value;
        }
        var leftMembers = Members(left); var rightMembers = Members(right);
        if (leftMembers.Concat(rightMembers).Any(snapshot => !snapshot.ContainsKey("gallery_payloads")))
        {
            var current = _matchGate.Decide(Read(c => _evidence.Snapshot(c, left)), Read(c => _evidence.Snapshot(c, right)));
            if (Bool(current.Evidence["applies"]) && !current.Eligible) return Merge(current.Evidence, new JsonObject { ["direct_pairs"] = 0 });
        }
        var evaluated = new JsonArray();
        foreach (var a in leftMembers)
            foreach (var b in rightMembers)
            {
                var decision = _matchGate.Decide(a, b);
                var item = (JsonObject)decision.Evidence.DeepClone(); item["left_entry_id"] = a["entry_id"]?.DeepClone(); item["right_entry_id"] = b["entry_id"]?.DeepClone();
                evaluated.Add(item);
                if (!decision.Eligible)
                    return new JsonObject
                    {
                        ["eligible"] = false, ["reason"] = "non-transitive direct proof failed: " + (decision.Reason.Length == 0 ? "insufficient evidence" : decision.Reason),
                        ["direct_pairs"] = evaluated.Count, ["failed_pair"] = item.DeepClone(),
                    };
            }
        var strongest = evaluated.Count != 0 ? Str(evaluated[0]!["reason"], "direct proof") : "direct proof";
        return new JsonObject
        {
            ["eligible"] = true, ["reason"] = "direct proof across " + evaluated.Count.ToString(CultureInfo.InvariantCulture) + " original member pair" + (evaluated.Count != 1 ? "s" : "") + ": " + strongest,
            ["direct_pairs"] = evaluated.Count, ["pair_evidence"] = new JsonArray(evaluated.Take(64).Select(item => item!.DeepClone()).ToArray()), ["pair_evidence_truncated"] = evaluated.Count > 64,
        };
    }

    private static JsonObject Merge(JsonObject evidence, JsonObject extra)
    {
        var result = (JsonObject)evidence.DeepClone();
        foreach (var (key, value) in extra) result[key] = value?.DeepClone();
        return result;
    }

    /// <summary>Merges one proven pair and keeps rollback data and the frozen original members (automatic_merge_events / lineage).</summary>
    public long MergeAutomatic(long leftId, long rightId, JsonObject decision, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(decision); ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            var (left, right) = Read(c => (DuplicateEvidence.Canonical(c, leftId), DuplicateEvidence.Canonical(c, rightId)));
            if (left == right) return left;
            var reason = Str(decision["reason"], "direct-proof");
            var capture = Read(c => (
                Members: _evidence.MemberSnapshots(c, left).Concat(_evidence.MemberSnapshots(c, right)).ToList(),
                Left: _evidence.RestoreSnapshot(c, left), Right: _evidence.RestoreSnapshot(c, right),
                Records: ActiveRecords(c, left).Concat(ActiveRecords(c, right)).Select(row => Key(new(row.Text("source_id"), row.Integer("source_manga_id")))).Distinct(StringComparer.Ordinal).ToArray()));
            var target = _structure.MergeEntries(left, right, "automatic-safe:" + reason);
            var duplicate = target == left ? right : left;
            var evidence = (JsonObject)decision.DeepClone(); evidence["recorded_reason"] = reason;
            var stamp = Stamp();
            var eventId = Write(c =>
            {
                var targetSnapshot = target == left ? capture.Left : capture.Right; var duplicateSnapshot = target == left ? capture.Right : capture.Left;
                if (HasEvidenceColumn(c))
                    c.Execute("INSERT INTO automatic_merge_events(target_entry_id,duplicate_entry_id,target_snapshot_json,duplicate_snapshot_json,reason,merged_at,evidence_json) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)",
                        P(target, duplicate, targetSnapshot.ToJsonString(), duplicateSnapshot.ToJsonString(), reason, stamp, evidence.ToJsonString()));
                else
                    c.Execute("INSERT INTO automatic_merge_events(target_entry_id,duplicate_entry_id,target_snapshot_json,duplicate_snapshot_json,reason,merged_at) VALUES($p0,$p1,$p2,$p3,$p4,$p5)",
                        P(target, duplicate, targetSnapshot.ToJsonString(), duplicateSnapshot.ToJsonString(), reason, stamp));
                // The rollback row's id: the review workbench lists automatic merges by it (decision After["event_id"]).
                var mergeEvent = Convert.ToInt64(c.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
                c.Execute("DELETE FROM automatic_merge_lineage WHERE canonical_entry_id IN ($p0,$p1)", P(target, duplicate));
                foreach (var member in capture.Members)
                {
                    var id = Long(member["entry_id"]);
                    if (id > 0) c.Execute("INSERT OR REPLACE INTO automatic_merge_lineage(canonical_entry_id,member_entry_id,snapshot_json,merged_at) VALUES($p0,$p1,$p2,$p3)", P(target, id, member.ToJsonString(), stamp));
                }
                return mergeEvent;
            });
            _decisions.Record(new DecisionEvent("merge.automatic", actor, [target, duplicate], capture.Records, reason,
                new JsonObject { ["left"] = Brief(capture.Left), ["right"] = Brief(capture.Right) }, new JsonObject { ["canonical_entry_id"] = target, ["event_id"] = eventId, ["evidence"] = evidence }));
            Invalidate();
            return target;
        }
    }

    private static bool HasEvidenceColumn(DatabaseSession c) => c.Scalar("SELECT 1 FROM pragma_table_info('automatic_merge_events') WHERE name='evidence_json'") is not null;

    // ---- helpers ----

    internal static bool Exists(DatabaseSession c, long id) => c.Scalar("SELECT 1 FROM entries WHERE id=$p0", P(id)) is not null;

    internal static List<DatabaseRow> ActiveRecords(DatabaseSession c, long entryId)
    {
        var rows = new List<DatabaseRow>();
        rows.AddRange(c.Query("SELECT 'primary' kind,r.*,COALESCE(NULLIF(sc.display_name,''),sc.name,r.source_id) source_name FROM source_matches r LEFT JOIN source_config sc ON sc.source_id=r.source_id WHERE r.entry_id=$p0 AND r.active=1 ORDER BY r.id", P(entryId)));
        rows.AddRange(c.Query("SELECT 'additional' kind,r.*,COALESCE(NULLIF(sc.display_name,''),sc.name,r.source_id) source_name FROM source_variants r LEFT JOIN source_config sc ON sc.source_id=r.source_id WHERE r.entry_id=$p0 AND r.active=1 ORDER BY r.id", P(entryId)));
        return rows;
    }

    private static long? Owner(DatabaseSession c, NativeSourceRecord record)
    {
        var owners = c.Query("SELECT entry_id FROM source_matches WHERE source_id=$p0 AND source_manga_id=$p1 AND active=1 UNION ALL SELECT entry_id FROM source_variants WHERE source_id=$p0 AND source_manga_id=$p1 AND active=1", P(record.SourceId, record.MangaId))
            .Select(row => DuplicateEvidence.Canonical(c, row.Integer("entry_id"))).Distinct().ToArray();
        if (owners.Length > 1) throw new InvalidOperationException("Source record ownership is ambiguous");
        return owners.Length == 1 ? owners[0] : null;
    }

    private static JsonObject Brief(DatabaseSession c, long id)
    {
        var row = c.Query("SELECT id,preferred_title,author FROM entries WHERE id=$p0", P(id)).SingleOrDefault();
        return row is null ? new JsonObject { ["id"] = id } : new JsonObject { ["id"] = id, ["preferred_title"] = row.Text("preferred_title"), ["author"] = row.Text("author") };
    }

    private static JsonObject Brief(JsonObject restore)
    {
        var entry = restore["entry"] as JsonObject;
        return new JsonObject { ["id"] = entry?["id"]?.DeepClone(), ["preferred_title"] = entry?["preferred_title"]?.DeepClone(), ["author"] = entry?["author"]?.DeepClone() };
    }

    private void Metadata(IEnumerable<long> ids)
    {
        if (_chooseMetadata is not { } choose) return;
        var stamp = Stamp();
        foreach (var id in ids.Distinct())
            Write(c => { var canonical = DuplicateEvidence.Canonical(c, id); if (Exists(c, canonical)) choose(c, canonical, stamp); return 0; });
    }
}
