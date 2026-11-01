using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Duplicates;
using AutoSourcarr.Core.Matching.Manual;
using AutoSourcarr.Core.Matching.Review;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Matching.Wiring;

/// <summary>
/// The review workbench's writes (<see cref="IReviewActions"/>) over the phase-8 services: duplicate reviews through
/// DuplicateMatching, merges and splits through the native structure, manual matches through ManualDecisions and main
/// titles through CatalogueTitles. Every write runs inside a <see cref="ScopedDecisionLog"/> scope, so the decision the
/// owning service records carries the reviewer, the reason and the undo reference, and its id is returned. A write that
/// records nothing or targets a changed work is refused with <see cref="ReviewException"/>.
/// </summary>
public sealed class ReviewActions : IReviewActions
{
    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly ScopedDecisionLog _log;
    private readonly DuplicateMatching _duplicates;
    private readonly IDuplicateStructure _structure;
    private readonly ManualDecisions _manual;
    private readonly CatalogueTitles _titles;
    private readonly TimeProvider _clock;

    public ReviewActions(SqliteDatabase database, object sourceWriteGate, ScopedDecisionLog log, DuplicateMatching duplicates,
        IDuplicateStructure structure, ManualDecisions manual, CatalogueTitles titles, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate); ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(duplicates); ArgumentNullException.ThrowIfNull(structure); ArgumentNullException.ThrowIfNull(manual);
        ArgumentNullException.ThrowIfNull(titles);
        _database = database; _gate = sourceWriteGate; _log = log; _duplicates = duplicates; _structure = structure; _manual = manual;
        _titles = titles; _clock = clock ?? TimeProvider.System;
    }

    private static string Key(string sourceId, long mangaId) => sourceId + ":" + mangaId.ToString(CultureInfo.InvariantCulture);

    private static NativeSourceRecord Parse(string record)
    {
        var colon = (record ?? "").IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !long.TryParse(record![(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var manga) || manga <= 0)
            throw new ReviewException(400, "Invalid source record " + record);
        return new NativeSourceRecord(record[..colon], manga);
    }

    private static IReadOnlyList<string> Records(DatabaseSession c, long entryId) => c.Query("""
        SELECT source_id,source_manga_id FROM source_matches WHERE entry_id=$id AND active=1
        UNION SELECT source_id,source_manga_id FROM source_variants WHERE entry_id=$id AND active=1
        """, ("$id", entryId)).Select(row => Key(row.Text("source_id"), row.Integer("source_manga_id"))).Order(StringComparer.Ordinal).ToArray();

    public ReviewEntrySnapshot? Entry(long entryId) => _database.Read(DatabaseFile.Catalog, c =>
    {
        var canonical = PortableIdentityResolver.Canonical(c, entryId);
        var row = c.Query("SELECT preferred_title,manual_preferred_title,updated_at FROM entries WHERE id=$id", ("$id", canonical)).FirstOrDefault();
        return row is null ? null : new ReviewEntrySnapshot(entryId, canonical, row.Text("preferred_title"), row.Flag("manual_preferred_title"),
            row.Text("updated_at"), Records(c, canonical));
    });

    public string? PairDecision(long left, long right) => _database.Read(DatabaseFile.Catalog, c =>
    {
        var (a, b) = Ordered(PortableIdentityResolver.Canonical(c, left), PortableIdentityResolver.Canonical(c, right));
        return c.Scalar("SELECT decision FROM duplicate_reviews WHERE left_entry_id=$left AND right_entry_id=$right", ("$left", a), ("$right", b)) as string;
    });

    public long? RecordEntry(string record)
    {
        var key = Parse(record);
        return _database.Read(DatabaseFile.Catalog, c =>
        {
            var owner = c.Query("""
                SELECT entry_id FROM source_matches WHERE source_id=$source AND source_manga_id=$manga AND active=1
                UNION ALL SELECT entry_id FROM source_variants WHERE source_id=$source AND source_manga_id=$manga AND active=1
                """, ("$source", key.SourceId), ("$manga", key.MangaId)).FirstOrDefault();
            return owner is null ? (long?)null : PortableIdentityResolver.Canonical(c, owner.Integer("entry_id"));
        });
    }

    private static (long Left, long Right) Ordered(long left, long right) => left < right ? (left, right) : (right, left);

    private long Canonical(long entryId) => _database.Read(DatabaseFile.Catalog, c => PortableIdentityResolver.Canonical(c, entryId));

    private ReviewEntrySnapshot Current(long entryId) => Entry(entryId) is { } snapshot && snapshot.CanonicalId == entryId ? snapshot
        : throw new ReviewException(409, "Work " + entryId.ToString(CultureInfo.InvariantCulture) + " is no longer a current work; reload the review");

    private static long Recorded(ScopedDecisionLog.Scope scope, string what) => scope.Last
        ?? throw new ReviewException(409, what + " changed nothing; reload the review");

    /// <summary>Runs a write of another service and maps its refusals to review answers.</summary>
    private static T Refused<T>(Func<T> write)
    {
        try { return write(); }
        catch (KeyNotFoundException error) { throw new ReviewException(404, error.Message); }
        catch (CatalogueTitleException error) { throw new ReviewException(error.Status, error.Message); }
        catch (ManualDecisionUnavailableException error) { throw new ReviewException(503, error.Message); }
        catch (ManualDecisionConflictException error) { throw new ReviewException(409, error.Message); }
    }

    public long ReviewPair(long left, long right, string? decision, ReviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var value = decision ?? "clear";
        if (value is not ("duplicate" or "not_duplicate" or "clear")) throw new ReviewException(400, "Unsupported duplicate decision");
        var (a, b) = Ordered(Canonical(left), Canonical(right));
        if (a == b) throw new ReviewException(409, "The two works are one entry now; reload the review");
        using var scope = _log.Begin(context.Actor, context.Reason, context.UndoOf);
        if (value != "duplicate")
        {
            // Separate (not_duplicate) or forget: DuplicateMatching writes the review row and its decision (never a merge).
            _ = Refused(() => _duplicates.Review(a, b, value, context.Actor));
            return Recorded(scope, "The review");
        }
        // "duplicate" is only ever restored by an undo here: the row alone, without merging (the merge is its own action).
        var stamp = DecisionStamp.Now(_clock);
        lock (_gate)
        {
            var id = _database.Write(DatabaseFile.Catalog, c =>
            {
                if (c.Scalar("SELECT 1 FROM entries WHERE id=$a", ("$a", a)) is null || c.Scalar("SELECT 1 FROM entries WHERE id=$b", ("$b", b)) is null)
                    throw new ReviewException(404, "Entry not found");
                var previous = c.Scalar("SELECT decision FROM duplicate_reviews WHERE left_entry_id=$a AND right_entry_id=$b", ("$a", a), ("$b", b)) as string ?? "";
                c.Execute("INSERT INTO duplicate_review_history(left_entry_id,right_entry_id,decision,reviewed_at) VALUES($a,$b,'duplicate',$at)", ("$a", a), ("$b", b), ("$at", stamp));
                c.Execute("""
                    INSERT INTO duplicate_reviews(left_entry_id,right_entry_id,decision,reviewed_at) VALUES($a,$b,'duplicate',$at)
                    ON CONFLICT(left_entry_id,right_entry_id) DO UPDATE SET decision=excluded.decision,reviewed_at=excluded.reviewed_at
                    """, ("$a", a), ("$b", b), ("$at", stamp));
                return _log.Record(new DecisionEvent("review.duplicate", context.Actor, [a, b], [], context.Reason,
                    new JsonObject { ["decision"] = previous }, new JsonObject { ["decision"] = "duplicate" }, context.UndoOf));
            });
            _duplicates.Invalidate();
            return id;
        }
    }

    public long Merge(long target, long source, ReviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var into = Current(target); var from = Current(source);
        if (into.CanonicalId == from.CanonicalId) throw new ReviewException(409, "The two works are one entry already");
        var survivor = Refused(() => _structure.MergeEntries(into.CanonicalId, from.CanonicalId, "manual-review-workbench"));
        var id = _log.Record(new DecisionEvent("merge.manual", context.Actor, [survivor, survivor == into.CanonicalId ? from.CanonicalId : into.CanonicalId],
            into.Records.Concat(from.Records).Distinct(StringComparer.Ordinal).ToArray(), context.Reason,
            new JsonObject { ["target"] = Brief(into), ["source"] = Brief(from) },
            new JsonObject { ["canonical_entry_id"] = survivor }, context.UndoOf));
        _duplicates.Invalidate();
        return id;
    }

    private static JsonObject Brief(ReviewEntrySnapshot entry) => new()
    {
        ["id"] = entry.CanonicalId, ["preferred_title"] = entry.Title, ["records"] = ReviewJson.Texts(entry.Records),
    };

    public ReviewSplitResult Split(long entryId, IReadOnlyList<string> records, ReviewContext context)
    {
        ArgumentNullException.ThrowIfNull(records); ArgumentNullException.ThrowIfNull(context);
        var entry = Current(entryId);
        var keys = records.Select(Parse).Distinct().ToArray();
        if (keys.Length == 0) throw new ReviewException(400, "Choose the source records to split");
        var owned = entry.Records.ToHashSet(StringComparer.Ordinal);
        if (keys.Any(key => !owned.Contains(Key(key.SourceId, key.MangaId)))) throw new ReviewException(409, "A chosen source record is no longer on this work");
        if (keys.Length >= owned.Count) throw new ReviewException(409, "A split must leave at least one record on the work");
        // A manual split: the moved records stay bound together and apart from the original (automation keeps them apart).
        var created = Refused(() => _structure.SplitRecords(entry.CanonicalId, keys, manual: true, "split-duplicate"));
        if (created <= 0) throw new ReviewException(409, "Nothing was split; reload the review");
        var id = _log.Record(new DecisionEvent("split.manual", context.Actor, [entry.CanonicalId, created], keys.Select(key => Key(key.SourceId, key.MangaId)).ToArray(),
            context.Reason, new JsonObject { ["records"] = ReviewJson.Texts(entry.Records) },
            new JsonObject { ["original_entry_id"] = entry.CanonicalId, ["new_entry_id"] = created }, context.UndoOf));
        _duplicates.Invalidate();
        return new ReviewSplitResult(id, created);
    }

    public long ManualMatch(long entryId, string record, ReviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var entry = Current(entryId);
        var key = Parse(record);
        // A record already in the catalogue commits locally from its stored row; an unknown one is fetched from its source.
        JsonObject? candidate = null;
        if (_manual.ManualRecordRow(key.SourceId, key.MangaId) is { } row)
        {
            candidate = (JsonObject)row.DeepClone();
            candidate["id"] = key.MangaId;
            if (candidate["title"] is null) candidate["title"] = row["source_title"]?.DeepClone();
        }
        using var scope = _log.Begin(context.Actor, context.Reason, context.UndoOf);
        _ = Refused(() => _manual.ManualMatchAsync(entry.CanonicalId, key.SourceId, key.MangaId, "add", candidate, deferHydration: candidate is not null, context.Actor)
            .GetAwaiter().GetResult());
        _duplicates.Invalidate();
        return Recorded(scope, "The manual match");
    }

    public long ClearManualMatch(long entryId, string record, ReviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var entry = Current(entryId);
        var key = Parse(record);
        if (!entry.Records.Contains(Key(key.SourceId, key.MangaId), StringComparer.Ordinal))
            throw new ReviewException(409, "The source record is no longer on this work");
        var stamp = DecisionStamp.Now(_clock);
        lock (_gate)
        {
            _ = _manual.ForgetManualBinding(key.SourceId, key.MangaId);
            _database.Write(DatabaseFile.Catalog, c =>
            {
                foreach (var table in new[] { "source_matches", "source_variants" })
                    c.Execute("UPDATE " + table + " SET locked=0,match_type='auto',last_checked=$at WHERE entry_id=$entry AND source_id=$source AND source_manga_id=$manga AND active=1",
                        ("$at", stamp), ("$entry", entry.CanonicalId), ("$source", key.SourceId), ("$manga", key.MangaId));
                return 0;
            });
            // The record leaves the work into its own entry (an entry's only record already is its own entry).
            var created = entry.Records.Count > 1
                ? Refused(() => _structure.SplitRecords(entry.CanonicalId, [key], manual: false, "auto"))
                : 0;
            var id = _log.Record(new DecisionEvent("manual.match.clear", context.Actor, [entry.CanonicalId, created], [Key(key.SourceId, key.MangaId)], context.Reason,
                new JsonObject { ["entry_id"] = entry.CanonicalId }, new JsonObject { ["new_entry_id"] = created }, context.UndoOf));
            _duplicates.Invalidate();
            return id;
        }
    }

    public long SetPreferredTitle(long entryId, string? title, ReviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var entry = Current(entryId);
        if (title is not null)
        {
            using var scope = _log.Begin(context.Actor, context.Reason, context.UndoOf);
            _ = Refused(() => _titles.SetPreferredTitle(entry.CanonicalId, title, context.Actor));
            _duplicates.Invalidate();
            return Recorded(scope, "The title change");
        }
        // Clear only the manual main title (aliases stay): the automatic title consensus applies again.
        var stamp = DecisionStamp.Now(_clock);
        long id;
        lock (_gate)
            id = _database.Write(DatabaseFile.Catalog, c =>
            {
                var before = c.Query("SELECT preferred_title,manual_preferred_title FROM entries WHERE id=$id", ("$id", entry.CanonicalId)).FirstOrDefault()
                    ?? throw new ReviewException(404, "Work not found");
                c.Execute("DELETE FROM preferred_title_overrides WHERE entry_id=$id", ("$id", entry.CanonicalId));
                c.Execute("DELETE FROM manual_title_scopes WHERE entry_id=$id", ("$id", entry.CanonicalId));
                c.Execute("UPDATE entries SET manual_preferred_title=0,updated_at=$at WHERE id=$id", ("$at", stamp), ("$id", entry.CanonicalId));
                var after = CatalogueTitles.SyncPreferredTitle(c, entry.CanonicalId, stamp);
                return _log.Record(new DecisionEvent("title.preferred.clear", context.Actor, [entry.CanonicalId], [], context.Reason,
                    new JsonObject { ["preferred_title"] = before.Text("preferred_title"), ["manual"] = before.Flag("manual_preferred_title") },
                    new JsonObject { ["preferred_title"] = after, ["manual"] = false }, context.UndoOf));
            });
        _duplicates.Invalidate();
        return id;
    }
}

/// <summary>The workbench's read side of the decision log (<see cref="IReviewHistory"/>) over decision_log.</summary>
public sealed class DecisionLogHistory(SqliteDatabase database, IDecisionLogReader log) : IReviewHistory
{
    private static RecordedDecision Map(DecisionLogEntry entry)
    {
        var at = DateTimeOffset.TryParse(entry.At, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed : DateTimeOffset.UnixEpoch;
        return new RecordedDecision(entry.Id, at, new DecisionEvent(entry.Kind, entry.Actor, entry.Entries, entry.Records, entry.Reason, entry.Before, entry.After, entry.UndoOf));
    }

    public RecordedDecision? Find(long id) => log.Get(id) is { } entry ? Map(entry) : null;

    public long? UndoneBy(long id) => database.Read(DatabaseFile.Catalog, c =>
        c.Query("SELECT id FROM decision_log WHERE undo_of=$undo ORDER BY id DESC LIMIT 1", ("$undo", id.ToString(CultureInfo.InvariantCulture)))
            .FirstOrDefault() is { } row ? row.Integer("id") : (long?)null);

    public IReadOnlyList<RecordedDecision> Recent(long? entryId, string? kind, int limit)
    {
        var ids = database.Read(DatabaseFile.Catalog, c => c.Query("""
            SELECT id FROM decision_log
            WHERE ($kind IS NULL OR kind=$kind)
              AND ($entry IS NULL OR EXISTS(SELECT 1 FROM json_each(decision_log.entries_json) WHERE value=$entry))
            ORDER BY id DESC LIMIT $limit
            """, ("$kind", kind), ("$entry", entryId), ("$limit", Math.Clamp(limit, 1, 500))).Select(row => row.Integer("id")).ToArray());
        return ids.Select(id => log.Get(id)).OfType<DecisionLogEntry>().Select(Map).ToArray();
    }
}
