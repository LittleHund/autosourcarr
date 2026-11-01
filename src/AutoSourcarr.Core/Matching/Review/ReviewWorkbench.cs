using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Core.Matching.Review;

/// <summary>
/// The review workbench (review_workbench.py): one audit list over the decision log, and reviewed, revision-bound batches of
/// matching changes (separate, merge, split, manual match, title choice) that can be undone as a batch while the works they
/// touched are unchanged.
/// <para>
/// A batch is logged as one <see cref="BatchKind"/> decision whose After holds, per item, the action, its Before state, its
/// After state and the item's own decision id. Undo checks that every item's current state still equals its After state, then
/// applies the inverse of each item in reverse order with UndoOf = the batch id, and logs one <see cref="UndoKind"/> decision:
/// separate → restore the previous pair review (or remove it); merge → split the merged-in records back out (into a new entry);
/// split → merge the new entry back; manual match → re-bind to the previous entry, or clear the manual binding when there was
/// none; title → restore the previous manual title, or clear the manual title when it was automatic.
/// </para>
/// </summary>
public sealed class ReviewWorkbench(IReviewActions actions, IDecisionLog log, IReviewHistory history)
{
    public const string BatchKind = "review.workbench";
    public const string UndoKind = "review.workbench.undo";
    public const int MaxItems = 100;
    public const string Separate = "separate", MergeKind = "merge", SplitKind = "split", ManualMatchKind = "manual.match", TitleKind = "title";
    private static readonly Regex RecordPattern = new(@"\A[^:\s]{1,64}:\S{1,256}\z", RegexOptions.CultureInvariant);
    private readonly Lock _gate = new();

    private sealed record ReviewAction(string Kind, long Entry, long Other, IReadOnlyList<string> Records, string? Title);

    // ------------------------------------------------------------------ audit

    /// <summary>Recent decisions (optionally for one work) with their actors, the reviewed batches and their undo state.</summary>
    public JsonObject Audit(long? entryId, int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 100);
        JsonObject? entry = null;
        if (entryId is { } id)
        {
            var snapshot = actions.Entry(id) ?? throw new ReviewException(404, "Work not found");
            entry = EntryJson(snapshot);
        }
        var decisions = history.Recent(entryId, null, limit);
        var merges = new JsonArray();
        foreach (var row in decisions.Where(row => row.Decision.Kind.StartsWith("merge.automatic", StringComparison.Ordinal)))
        {
            if (row.Decision.After?["event_id"] is not JsonValue eventId || eventId.GetValueKind() != JsonValueKind.Number) continue;
            merges.Add(new JsonObject
            {
                ["id"] = eventId.DeepClone(), ["merged_at"] = Time(row.At), ["state"] = row.Decision.Kind,
                ["current_entry_id"] = row.Decision.Entries.Count > 0 ? (long?)row.Decision.Entries[0] : null, ["reason"] = row.Decision.Reason,
            });
        }
        var batches = new JsonArray();
        foreach (var row in history.Recent(null, BatchKind, 20))
        {
            var undo = history.UndoneBy(row.Id) is { } undoId ? history.Find(undoId) : null;
            batches.Add(new JsonObject
            {
                ["id"] = row.Id, ["created_at"] = Time(row.At), ["actor"] = ReviewJson.Actor(row.Decision.Actor), ["reason"] = row.Decision.Reason,
                ["items"] = (row.Decision.After?["items"] as JsonArray)?.Count ?? 0,
                ["undone_at"] = undo is null ? null : Time(undo.At), ["undone_by"] = undo is null ? null : ReviewJson.Actor(undo.Decision.Actor),
            });
        }
        return new JsonObject
        {
            ["decisions"] = new JsonArray(decisions.Select(row => (JsonNode?)ReviewJson.Decision(row)).ToArray()),
            ["bulk_actions"] = batches,
            ["automatic_merges"] = new JsonObject { ["items"] = merges },
            ["entry"] = entry,
            ["note"] = "Every merge, split, separate, manual match and title choice is listed with who made it. "
                + "Undo here applies to reviewed workbench batches while the works they touched are unchanged.",
        };
    }

    // ---------------------------------------------------------------- preview

    /// <summary>Checks the selected items against the current works and returns their effects and a revision to apply.</summary>
    public JsonObject Preview(JsonObject body) => Build(Parse(body));

    private JsonObject Build(IReadOnlyList<ReviewAction> list)
    {
        var items = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in list)
        {
            var item = ActionJson(action);
            if (!seen.Add(CanonicalJson.Text(item))) throw new ReviewException(400, "Duplicate or identical review item");
            if (action.Kind != Separate && Touched(action).Any(id => list.Any(other => !ReferenceEquals(other, action) && Touched(other).Contains(id))))
                throw new ReviewException(400, "A work may be changed by only one merge, split, manual match or title item per review");
            item["entries"] = new JsonArray(Touched(action).Select(id => (JsonNode?)Brief(Canonical(id))).ToArray());
            item["before"] = Before(action);
            item["effect"] = Effect(action);
            items.Add(item);
        }
        return new JsonObject
        {
            ["items"] = items,
            ["revision"] = Revision(items),
            ["operation"] = string.Join(",", list.Select(action => action.Kind).Distinct(StringComparer.Ordinal)),
            ["undo"] = "Only while these exact decisions and work identities remain unchanged",
        };
    }

    // ------------------------------------------------------------------ apply

    /// <summary>Applies a previewed batch when its revision still matches, logging one batch decision with the actor.</summary>
    public JsonObject Apply(JsonObject body, DecisionActor actor)
    {
        var revision = body["revision"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length is > 0 and <= 64
            ? text : throw new ReviewException(400, "Preview the selected items first");
        var list = Parse(body);
        lock (_gate)
        {
            var current = Build(list);
            if (current["revision"]!.GetValue<string>() != revision) throw new ReviewException(409, "Review changed; preview the selected items again");
            var items = current["items"]!.AsArray();
            var reason = "Reviewed " + list.Count.ToString(CultureInfo.InvariantCulture) + " matching decision" + (list.Count == 1 ? "" : "s") + " in the review workbench";
            var context = new ReviewContext(actor, reason);
            var applied = new JsonArray();
            var done = new List<(ReviewAction Action, JsonObject Item, long? Created)>();
            string? failure = null;
            for (var index = 0; index < list.Count; index++)
            {
                var action = list[index];
                try
                {
                    var (decision, created) = Run(action, context);
                    var item = items[index]!.DeepClone().AsObject();
                    item["decision"] = decision;
                    if (created is { } newEntry) item["new_entry"] = newEntry;
                    done.Add((action, item, created));
                    item["after"] = State(action, created);
                    applied.Add(item);
                }
                catch (Exception error) when (Refusal(error))
                {
                    failure = error.Message;
                    break;
                }
            }
            if (failure is not null)
            {
                // All or nothing: the items applied before the failure are undone again and no batch is logged.
                var stopped = "Item " + (done.Count + 1).ToString(CultureInfo.InvariantCulture) + " failed: " + failure;
                if (done.Count == 0) throw new ReviewException(409, "Nothing was applied. " + stopped);
                var rollback = new ReviewContext(actor, "Rolled back: " + reason + " (" + stopped + ")");
                var remaining = done.ToList();
                string? rollbackFailure = null;
                for (var index = done.Count - 1; index >= 0; index--)
                {
                    var (action, item, created) = done[index];
                    try { _ = Inverse(action, item["before"] as JsonObject ?? new JsonObject(), created, rollback); remaining.RemoveAt(index); }
                    catch (Exception error) when (Refusal(error)) { rollbackFailure = error.Message; break; }
                }
                if (rollbackFailure is null)
                    throw new ReviewException(409, "Nothing was applied. " + stopped + "; the " + done.Count.ToString(CultureInfo.InvariantCulture) + " item(s) applied before it were rolled back");
                // The rollback itself was refused: the items still applied stay undoable as one logged batch.
                var kept = new JsonArray(remaining.Select(entry => (JsonNode?)entry.Item.DeepClone()).ToArray());
                var partialBatch = log.Record(new DecisionEvent(BatchKind, actor, TouchedEntries(kept), TouchedRecords(kept), reason,
                    Before: new JsonObject { ["items"] = items.DeepClone() },
                    After: new JsonObject { ["items"] = kept, ["total"] = list.Count, ["failure"] = stopped + "; rollback stopped: " + rollbackFailure }));
                throw new ReviewException(409, stopped + ". Rolling back the applied items stopped (" + rollbackFailure + "); "
                    + remaining.Count.ToString(CultureInfo.InvariantCulture) + " item(s) stay applied as reviewed batch #" + partialBatch.ToString(CultureInfo.InvariantCulture) + ", which can be undone");
            }
            var id = log.Record(new DecisionEvent(BatchKind, actor, TouchedEntries(applied), TouchedRecords(applied), reason,
                Before: new JsonObject { ["items"] = items.DeepClone() },
                After: new JsonObject { ["items"] = applied, ["total"] = list.Count, ["failure"] = null }));
            return new JsonObject
            {
                ["id"] = id, ["applied"] = applied.Count, ["total"] = list.Count, ["complete"] = true, ["failure"] = null,
                ["undo_available"] = true,
            };
        }
    }

    // ------------------------------------------------------------------- undo

    /// <summary>Undoes a logged workbench batch when every item is still exactly as the batch left it.</summary>
    public JsonObject Undo(long identifier, DecisionActor actor)
    {
        lock (_gate)
        {
            var row = history.Find(identifier) ?? throw new ReviewException(404, "Review action is unavailable");
            if (row.Decision.Kind != BatchKind)
                throw new ReviewException(409, "Only reviewed workbench batches are undone here; other decisions are undone where they were made");
            if (history.UndoneBy(identifier) is not null) throw new ReviewException(409, "Review action is unavailable or already undone");
            var items = row.Decision.After?["items"] as JsonArray ?? throw new ReviewException(409, "Review action has no recorded items");
            var parsed = new List<(ReviewAction Action, JsonObject Item, long? Created)>();
            foreach (var node in items)
            {
                if (node is not JsonObject item) throw new ReviewException(409, "Review action has an unreadable item");
                var action = ParseItem(item);
                long? created = item["new_entry"] is null ? null : Id(item["new_entry"], "Review action has an unreadable item");
                parsed.Add((action, item, created));
            }
            foreach (var (action, item, created) in parsed)
                if (!JsonNode.DeepEquals(State(action, created), item["after"]))
                    throw new ReviewException(409, "Work or decision changed after this review; scoped undo is unavailable");
            var key = identifier.ToString(CultureInfo.InvariantCulture);
            var reason = "Undo of reviewed batch #" + key;
            var context = new ReviewContext(actor, reason, key);
            var undone = new JsonArray();
            var reverted = new List<(int Index, JsonObject Result)>();
            string? failure = null;
            for (var index = parsed.Count - 1; index >= 0; index--)
            {
                var (action, item, created) = parsed[index];
                try
                {
                    var result = Inverse(action, item["before"] as JsonObject ?? new JsonObject(), created, context);
                    undone.Add(result);
                    reverted.Add((index, result));
                }
                catch (Exception error) when (Refusal(error))
                {
                    failure = error.Message;
                    break;
                }
            }
            if (failure is not null)
            {
                // All or nothing: what was already undone is applied again; the batch is not logged as undone.
                var restore = new ReviewContext(actor, "Restored after a failed undo of reviewed batch #" + key + ": " + failure);
                string? restoreFailure = null;
                for (var index = reverted.Count - 1; index >= 0; index--)
                {
                    try { Redo(parsed[reverted[index].Index].Action, reverted[index].Result, restore); }
                    catch (Exception error) when (Refusal(error)) { restoreFailure = error.Message; break; }
                }
                throw new ReviewException(409, restoreFailure is null
                    ? "Nothing was undone: " + failure + (reverted.Count == 0 ? "" : "; the " + reverted.Count.ToString(CultureInfo.InvariantCulture) + " item(s) already undone were applied again")
                    : "Undo stopped (" + failure + ") and re-applying the undone items also stopped (" + restoreFailure + "); the batch is not marked undone, review its works");
            }
            var id = log.Record(new DecisionEvent(UndoKind, actor, row.Decision.Entries, row.Decision.Records, reason,
                Before: new JsonObject { ["items"] = items.DeepClone() },
                After: new JsonObject { ["items"] = undone, ["total"] = parsed.Count, ["failure"] = null }, UndoOf: key));
            return new JsonObject
            {
                ["id"] = id, ["undone"] = undone.Count, ["total"] = parsed.Count, ["complete"] = true, ["failure"] = null,
            };
        }
    }

    private JsonObject Inverse(ReviewAction action, JsonObject before, long? created, ReviewContext context)
    {
        var result = new JsonObject { ["kind"] = action.Kind };
        switch (action.Kind)
        {
            case Separate:
                // Both works must still be current: a merged-away or deleted work refuses (409) instead of reviewing its survivor.
                _ = Canonical(action.Entry); _ = Canonical(action.Other);
                result["decision"] = actions.ReviewPair(action.Entry, action.Other, Text(before["decision"]), context);
                break;
            case MergeKind:
                var records = Strings(before["source_records"]);
                var split = actions.Split(action.Entry, records, context);
                result["decision"] = split.Decision;
                result["new_entry"] = split.NewEntry;
                break;
            case SplitKind:
                result["decision"] = actions.Merge(action.Entry, created ?? throw new ReviewException(409, "Split result is unknown"), context);
                break;
            case ManualMatchKind:
                var record = action.Records[0];
                long? previous = before["entry"] is null ? null : Id(before["entry"], "Previous binding is unreadable");
                // The record goes back the way it was bound: an automatic attachment stays automatic.
                var automatic = Text(before["binding"]) == "automatic";
                result["decision"] = previous is { } entry && actions.Entry(entry) is { } snapshot && snapshot.CanonicalId == entry
                    ? automatic ? actions.RestoreAutomaticMatch(entry, record, context) : actions.ManualMatch(entry, record, context)
                    : actions.ClearManualMatch(action.Entry, record, context);
                break;
            case TitleKind:
                var manual = before["manual"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
                result["decision"] = actions.SetPreferredTitle(action.Entry, manual ? Text(before["title"]) : null, context);
                break;
            default:
                throw new ReviewException(409, "Unknown review item");
        }
        return result;
    }

    /// <summary>Re-applies an item after its undo was itself undone (the inverse's result names entries it created).</summary>
    private void Redo(ReviewAction action, JsonObject inverse, ReviewContext context)
    {
        switch (action.Kind)
        {
            case Separate: _ = actions.ReviewPair(action.Entry, action.Other, "not_duplicate", context); break;
            case MergeKind: _ = actions.Merge(action.Entry, Id(inverse["new_entry"], "The undone merge left no entry to merge back"), context); break;
            case SplitKind: _ = actions.Split(action.Entry, action.Records, context); break;
            case ManualMatchKind: _ = actions.ManualMatch(action.Entry, action.Records[0], context); break;
            case TitleKind: _ = actions.SetPreferredTitle(action.Entry, action.Title, context); break;
            default: throw new ReviewException(409, "Unknown review item");
        }
    }

    private static bool Refusal(Exception error) => error is ReviewException or ArgumentException or InvalidOperationException or KeyNotFoundException;

    // ---------------------------------------------------------------- actions

    private (long Decision, long? Created) Run(ReviewAction action, ReviewContext context)
    {
        switch (action.Kind)
        {
            case Separate: return (actions.ReviewPair(action.Entry, action.Other, "not_duplicate", context), null);
            case MergeKind: return (actions.Merge(action.Entry, action.Other, context), null);
            case SplitKind:
                var split = actions.Split(action.Entry, action.Records, context);
                return (split.Decision, split.NewEntry);
            case ManualMatchKind: return (actions.ManualMatch(action.Entry, action.Records[0], context), null);
            case TitleKind: return (actions.SetPreferredTitle(action.Entry, action.Title, context), null);
            default: throw new ReviewException(400, "Unknown review item");
        }
    }

    private JsonObject Before(ReviewAction action)
    {
        switch (action.Kind)
        {
            case Separate:
                return new JsonObject { ["decision"] = actions.PairDecision(action.Entry, action.Other) };
            case MergeKind:
                var target = Canonical(action.Entry);
                var source = Canonical(action.Other);
                return new JsonObject
                {
                    ["target_records"] = ReviewJson.Texts(target.Records), ["source_records"] = ReviewJson.Texts(source.Records), ["source_title"] = source.Title,
                };
            case SplitKind:
                var entry = Canonical(action.Entry);
                var missing = action.Records.FirstOrDefault(record => !entry.Records.Contains(record, StringComparer.Ordinal));
                if (missing is not null) throw new ReviewException(409, "Record " + missing + " is not part of work #" + Number(action.Entry));
                if (action.Records.Count >= entry.Records.Count) throw new ReviewException(400, "Keep at least one record in the work; select fewer records to split");
                return new JsonObject { ["records"] = ReviewJson.Texts(entry.Records) };
            case ManualMatchKind:
                var previous = actions.RecordEntry(action.Records[0]);
                if (previous == action.Entry) throw new ReviewException(409, "Record " + action.Records[0] + " is already part of work #" + Number(action.Entry));
                return new JsonObject { ["entry"] = previous, ["binding"] = previous is null ? null : actions.RecordBinding(action.Records[0]) };
            case TitleKind:
                var titled = Canonical(action.Entry);
                return new JsonObject { ["title"] = titled.Title, ["manual"] = titled.TitleManual };
            default:
                throw new ReviewException(400, "Unknown review item");
        }
    }

    /// <summary>The state an applied item leaves, re-read for the undo check (same shape as the logged After).</summary>
    private JsonObject State(ReviewAction action, long? created)
    {
        switch (action.Kind)
        {
            case Separate:
                return new JsonObject { ["decision"] = actions.PairDecision(action.Entry, action.Other) };
            case MergeKind:
                return new JsonObject
                {
                    ["target_records"] = ReviewJson.Texts(actions.Entry(action.Entry)?.Records ?? []),
                    ["source_canonical"] = actions.Entry(action.Other)?.CanonicalId,
                };
            case SplitKind:
                return new JsonObject
                {
                    ["records"] = ReviewJson.Texts(actions.Entry(action.Entry)?.Records ?? []),
                    ["new_records"] = ReviewJson.Texts(created is { } id ? actions.Entry(id)?.Records ?? [] : []),
                };
            case ManualMatchKind:
                return new JsonObject { ["entry"] = actions.RecordEntry(action.Records[0]) };
            case TitleKind:
                var entry = actions.Entry(action.Entry);
                return new JsonObject { ["title"] = entry?.Title, ["manual"] = entry?.TitleManual, ["canonical"] = entry?.CanonicalId };
            default:
                throw new ReviewException(400, "Unknown review item");
        }
    }

    private static string Effect(ReviewAction action) => action.Kind switch
    {
        Separate => "Remember these as separate works; no manga, chapters or downloads are removed",
        MergeKind => "Merge work #" + Number(action.Other) + " into #" + Number(action.Entry) + "; its source records, chapters and downloads move to #" + Number(action.Entry),
        SplitKind => "Move " + action.Records.Count.ToString(CultureInfo.InvariantCulture) + " source record(s) out of work #" + Number(action.Entry) + " into a new work and keep them apart",
        ManualMatchKind => "Bind " + action.Records[0] + " to work #" + Number(action.Entry) + " as a manual match",
        TitleKind => action.Title is null
            ? "Clear the manual title of work #" + Number(action.Entry) + "; the automatic title applies again"
            : "Use \"" + action.Title + "\" as the manual title of work #" + Number(action.Entry),
        _ => "",
    };

    // ---------------------------------------------------------------- parsing

    private static List<ReviewAction> Parse(JsonObject body)
    {
        var list = new List<ReviewAction>();
        if (body["pairs"] is JsonArray pairs)
            foreach (var pair in pairs) list.Add(Pair(pair));
        if (body["actions"] is JsonArray items)
            foreach (var item in items) list.Add(ParseItem(item as JsonObject ?? throw new ReviewException(400, "Each review item must be an object")));
        if (list.Count is < 1 or > MaxItems) throw new ReviewException(400, "Select 1 to 100 review items");
        return list;
    }

    private static ReviewAction Pair(JsonNode? node)
    {
        if (node is not JsonArray pair || pair.Count != 2) throw new ReviewException(400, "Use exact positive work IDs");
        var a = Id(pair[0], "Use exact positive work IDs");
        var b = Id(pair[1], "Use exact positive work IDs");
        if (a == b) throw new ReviewException(400, "Duplicate or identical work pair");
        return new ReviewAction(Separate, Math.Min(a, b), Math.Max(a, b), [], null);
    }

    private static ReviewAction ParseItem(JsonObject item)
    {
        var kind = Text(item["kind"]);
        switch (kind)
        {
            case Separate:
                return Pair(item["pair"]);
            case MergeKind:
                var target = Id(item["target"], "Use exact positive work IDs");
                var source = Id(item["source"], "Use exact positive work IDs");
                if (target == source) throw new ReviewException(400, "Choose two different works to merge");
                return new ReviewAction(MergeKind, target, source, [], null);
            case SplitKind:
                var records = Strings(item["records"]);
                if (records.Count is < 1 or > 500 || records.Any(record => !RecordPattern.IsMatch(record)) || records.Distinct(StringComparer.Ordinal).Count() != records.Count)
                    throw new ReviewException(400, "Choose 1 to 500 distinct source records (source_id:source_manga_id)");
                return new ReviewAction(SplitKind, Id(item["entry"], "Use an exact positive work ID"), 0, records.Order(StringComparer.Ordinal).ToList(), null);
            case ManualMatchKind:
                var record = Text(item["record"]) ?? "";
                if (!RecordPattern.IsMatch(record)) throw new ReviewException(400, "Choose one source record (source_id:source_manga_id)");
                return new ReviewAction(ManualMatchKind, Id(item["entry"], "Use an exact positive work ID"), 0, [record], null);
            case TitleKind:
                var title = item["title"] is null ? null : Text(item["title"])?.Trim() ?? throw new ReviewException(400, "Title must be text or null");
                if (title is { Length: < 1 or > 500 }) throw new ReviewException(400, "Use a title of 1 to 500 characters, or null to clear the manual title");
                return new ReviewAction(TitleKind, Id(item["entry"], "Use an exact positive work ID"), 0, [], title);
            default:
                throw new ReviewException(400, "Unknown review item kind; use separate, merge, split, manual.match or title");
        }
    }

    private static JsonObject ActionJson(ReviewAction action)
    {
        var item = new JsonObject { ["kind"] = action.Kind };
        switch (action.Kind)
        {
            case Separate: item["pair"] = ReviewJson.Numbers([action.Entry, action.Other]); break;
            case MergeKind: item["target"] = action.Entry; item["source"] = action.Other; break;
            case SplitKind: item["entry"] = action.Entry; item["records"] = ReviewJson.Texts(action.Records); break;
            case ManualMatchKind: item["entry"] = action.Entry; item["record"] = action.Records[0]; break;
            case TitleKind: item["entry"] = action.Entry; item["title"] = action.Title; break;
        }
        return item;
    }

    // ---------------------------------------------------------------- helpers

    private static long[] Touched(ReviewAction action) => action.Kind is Separate or MergeKind ? [action.Entry, action.Other] : [action.Entry];

    private ReviewEntrySnapshot Canonical(long id)
    {
        var entry = actions.Entry(id);
        if (entry is null || entry.CanonicalId != id) throw new ReviewException(409, "Work identities changed; reload review");
        return entry;
    }

    private static JsonObject Brief(ReviewEntrySnapshot entry) => new() { ["id"] = entry.Id, ["preferred_title"] = entry.Title, ["updated_at"] = entry.UpdatedAt };

    private static JsonObject EntryJson(ReviewEntrySnapshot entry) => new()
    {
        ["id"] = entry.Id, ["canonical_id"] = entry.CanonicalId, ["preferred_title"] = entry.Title, ["title_manual"] = entry.TitleManual,
        ["updated_at"] = entry.UpdatedAt, ["records"] = ReviewJson.Texts(entry.Records),
    };

    private static List<long> TouchedEntries(JsonArray applied)
    {
        var ids = new SortedSet<long>();
        foreach (var item in applied.OfType<JsonObject>())
        {
            foreach (var entry in item["entries"] as JsonArray ?? new JsonArray()) if (entry?["id"] is JsonValue id && id.TryGetValue<long>(out var value)) ids.Add(value);
            if (item["new_entry"] is JsonValue created && created.TryGetValue<long>(out var newEntry)) ids.Add(newEntry);
        }
        return [.. ids];
    }

    private static List<string> TouchedRecords(JsonArray applied)
    {
        var records = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in applied.OfType<JsonObject>())
        {
            foreach (var record in Strings(item["records"])) records.Add(record);
            if (Text(item["record"]) is { } single) records.Add(single);
            foreach (var record in Strings(item["before"]?["source_records"])) records.Add(record);
        }
        return [.. records];
    }

    private static string Revision(JsonNode value) => Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Bytes(value)));
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static long Id(JsonNode? node, string message) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<long>(out var id) && id > 0
            ? id : throw new ReviewException(400, message);

    internal static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(item => Text(item) ?? throw new ReviewException(400, "Records must be text")).ToList() : [];
}
