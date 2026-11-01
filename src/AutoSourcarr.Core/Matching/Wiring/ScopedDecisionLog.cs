using AutoSourcarr.Core.Matching.Decisions;

namespace AutoSourcarr.Core.Matching.Wiring;

/// <summary>
/// The decision log every matching service of the running app writes through. Outside a scope it passes each event on
/// unchanged. Inside <see cref="Begin"/> (the review workbench applying or undoing one change) every event recorded on
/// that logical call flow takes the scope's actor, reason and undo reference, and its id is collected, so a reviewed
/// change made by another service (duplicate review, manual match, title) reports the decision it actually wrote.
/// </summary>
public sealed class ScopedDecisionLog(IDecisionLog inner) : IDecisionLog
{
    private static readonly AsyncLocal<Scope?> Current = new();

    public long Record(DecisionEvent decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var scope = Current.Value;
        if (scope is not null)
            decision = decision with
            {
                Actor = scope.Actor,
                Reason = scope.Reason.Length > 0 ? scope.Reason : decision.Reason,
                UndoOf = scope.UndoOf ?? decision.UndoOf,
            };
        var id = inner.Record(decision);
        scope?.Recorded.Add(id);
        return id;
    }

    /// <summary>Starts collecting the events of this call flow; dispose to end (scopes nest).</summary>
    public Scope Begin(DecisionActor actor, string reason, string? undoOf)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var scope = new Scope(actor, reason ?? "", undoOf, Current.Value);
        Current.Value = scope;
        return scope;
    }

    public sealed class Scope : IDisposable
    {
        private readonly Scope? _outer;
        private int _closed;

        internal Scope(DecisionActor actor, string reason, string? undoOf, Scope? outer)
        {
            Actor = actor; Reason = reason; UndoOf = undoOf; _outer = outer;
        }

        public DecisionActor Actor { get; }
        public string Reason { get; }
        public string? UndoOf { get; }
        internal List<long> Recorded { get; } = [];

        /// <summary>The id of the last event recorded in this scope, or null when nothing was recorded.</summary>
        public long? Last => Recorded.Count == 0 ? null : Recorded[^1];

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            if (ReferenceEquals(Current.Value, this)) Current.Value = _outer;
        }
    }
}
