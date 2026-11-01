using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Matching.Decisions;

/// <summary>Who made a matching decision: a signed-in account, a key, or the app itself (automatic rules, upgrades).</summary>
public sealed record DecisionActor(string Kind, string Name)
{
    public static readonly DecisionActor Automatic = new("automatic", "AutoSourcarr");
    public static DecisionActor Upgrade(string step) => new("upgrade", step);
    public static DecisionActor User(string name) => new("user", name);
    public static DecisionActor Key(string name) => new("key", name);
}

/// <summary>
/// One entry of the single decision log (phase 8, "one decision log with actor"): every merge, split, separate, manual
/// match/group change, title choice, policy change, review and reset, automatic or manual, with its actor.
/// <see cref="Kind"/> is a stable dotted name ("merge.automatic", "merge.manual", "split", "separate", "manual.match",
/// "manual.group.add", "title.preferred", "policy.write", "review.duplicate", "reset.manual", ...);
/// <see cref="Entries"/> and <see cref="Records"/> name what it touched ("source_id:source_manga_id" for records);
/// <see cref="Before"/>/<see cref="After"/> hold enough to show and, where supported, undo it.
/// </summary>
public sealed record DecisionEvent(string Kind, DecisionActor Actor, IReadOnlyList<long> Entries, IReadOnlyList<string> Records,
    string Reason, JsonObject? Before = null, JsonObject? After = null, string? UndoOf = null);

/// <summary>The decision log (implemented by DecisionLog over the catalogue database; tests use an in-memory one).</summary>
public interface IDecisionLog
{
    /// <summary>Appends the event and returns its id. Must be called inside the same write as the change it records when one is open.</summary>
    long Record(DecisionEvent decision);
}
