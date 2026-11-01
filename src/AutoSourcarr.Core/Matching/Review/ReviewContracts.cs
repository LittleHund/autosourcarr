using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;

namespace AutoSourcarr.Core.Matching.Review;

/// <summary>A refused review, preview or undo; <see cref="StatusCode"/> is the HTTP answer (400 invalid, 404 unknown, 409 changed).</summary>
public sealed class ReviewException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Who asks for a reviewed change and why; <see cref="UndoOf"/> is set when the change undoes a logged decision.</summary>
public sealed record ReviewContext(DecisionActor Actor, string Reason, string? UndoOf = null);

/// <summary>
/// One entry as the workbench sees it. <see cref="CanonicalId"/> differs from <see cref="Id"/> when the entry was merged away;
/// <see cref="Records"/> are the source records bound to it ("source_id:source_manga_id", sorted ordinally).
/// </summary>
public sealed record ReviewEntrySnapshot(long Id, long CanonicalId, string Title, bool TitleManual, string UpdatedAt, IReadOnlyList<string> Records);

/// <summary>The result of a reviewed split: its decision id and the entry that now holds the moved records.</summary>
public sealed record ReviewSplitResult(long Decision, long NewEntry);

/// <summary>
/// The matching changes the review workbench applies and undoes. Implemented by an adapter over area 8B (manual matches),
/// 8C (duplicate reviews, merge, split) and 8A (titles). Every write records its own <see cref="DecisionEvent"/> through the
/// decision log with <see cref="ReviewContext.Actor"/>, <see cref="ReviewContext.Reason"/> and <see cref="ReviewContext.UndoOf"/>,
/// and returns that event's id. Writes throw <see cref="ReviewException"/> (or ArgumentException/InvalidOperationException)
/// when the change is refused; a refused write must change nothing.
/// </summary>
public interface IReviewActions
{
    /// <summary>The entry, or null when the id does not exist. Merged-away ids answer with their canonical id.</summary>
    ReviewEntrySnapshot? Entry(long entryId);

    /// <summary>The saved duplicate review of a work pair (left &lt; right): "not_duplicate", "duplicate" or null when none.</summary>
    string? PairDecision(long left, long right);

    /// <summary>The canonical entry a source record is bound to, or null when the record is not bound.</summary>
    long? RecordEntry(string record);

    /// <summary>How the record is bound now: "manual" (a manual binding, manual match type or lock), "automatic", or null when unbound.</summary>
    string? RecordBinding(string record);

    /// <summary>
    /// Saves (or with null removes) the duplicate review row of a work pair (left &lt; right) only: it never merges or splits.
    /// "not_duplicate" is the Separate decision (review_duplicate(left, right, 'not_duplicate')).
    /// </summary>
    long ReviewPair(long left, long right, string? decision, ReviewContext context);

    /// <summary>Manual merge of <paramref name="source"/> into <paramref name="target"/> (merge_entries; both canonical).</summary>
    long Merge(long target, long source, ReviewContext context);

    /// <summary>Moves the given records of an entry into one new entry (manual split, remembered so automation keeps them apart).</summary>
    ReviewSplitResult Split(long entryId, IReadOnlyList<string> records, ReviewContext context);

    /// <summary>Manual match: binds the source record to the entry (manual_match; persistent manual binding).</summary>
    long ManualMatch(long entryId, string record, ReviewContext context);

    /// <summary>Moves the record back to the entry as an automatic (unlocked, unbound) record — the undo of a manual match
    /// that took an automatically attached record away from its entry.</summary>
    long RestoreAutomaticMatch(long entryId, string record, ReviewContext context);

    /// <summary>Removes the manual binding of the record from the entry; the record leaves the entry (into its own entry).</summary>
    long ClearManualMatch(long entryId, string record, ReviewContext context);

    /// <summary>Sets the manual preferred title; null clears the manual title so the automatic title consensus applies again.</summary>
    long SetPreferredTitle(long entryId, string? title, ReviewContext context);
}

/// <summary>One logged decision with its id and time (the stored form behind GET /api/v1/decisions).</summary>
public sealed record RecordedDecision(long Id, DateTimeOffset At, DecisionEvent Decision);

/// <summary>Read side of the decision log the workbench needs (implemented by the decision log of area 8B).</summary>
public interface IReviewHistory
{
    /// <summary>The logged decision, or null when unknown.</summary>
    RecordedDecision? Find(long id);

    /// <summary>The id of the newest decision whose UndoOf names this decision, or null when it was not undone.</summary>
    long? UndoneBy(long id);

    /// <summary>Newest first, at most <paramref name="limit"/>; filtered by touched entry and/or exact kind when given.</summary>
    IReadOnlyList<RecordedDecision> Recent(long? entryId, string? kind, int limit);
}

/// <summary>JSON shapes shared by the review and decision routes.</summary>
public static class ReviewJson
{
    public static JsonObject Actor(DecisionActor actor) => new() { ["kind"] = actor.Kind, ["name"] = actor.Name };

    /// <summary>The GET /api/v1/decisions item shape: {id, at, kind, actor, entries, records, reason, undo_of}.</summary>
    public static JsonObject Decision(RecordedDecision row) => new()
    {
        ["id"] = row.Id,
        ["at"] = row.At.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["kind"] = row.Decision.Kind,
        ["actor"] = Actor(row.Decision.Actor),
        ["entries"] = Numbers(row.Decision.Entries),
        ["records"] = Texts(row.Decision.Records),
        ["reason"] = row.Decision.Reason,
        ["undo_of"] = row.Decision.UndoOf,
    };

    public static JsonArray Numbers(IEnumerable<long> values) => new(values.Select(value => (JsonNode?)value).ToArray());
    public static JsonArray Texts(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)value).ToArray());
}
