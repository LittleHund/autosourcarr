using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Sources.Configuration;

namespace AutoSourcarr.Core.Matching.Review;

/// <summary>What the policy simulator offers: sources [{source_id,name,lang}], groups [{id,name}] and presets [{id,name,description,rule}].</summary>
public sealed record PolicyOptions(JsonArray Sources, JsonArray Groups, JsonArray Presets);

/// <summary>
/// Matching policy storage (owned by area 8A: matching_policies / matching_presets over MatchingRuleConfiguration).
/// Rules use <see cref="MatchingRuleConfiguration.Validate"/>'s contract; their revision is <see cref="MatchingRuleConfiguration.Revision"/>.
/// </summary>
public interface IPolicyStore
{
    /// <summary>The saved rules (fresh, never a stale cache).</summary>
    IReadOnlyDictionary<MatchingRuleKey, JsonObject?> Rules();

    /// <summary>True when the policy subject exists (source_config.source_id for "source", source_groups.id for "group").</summary>
    bool SubjectExists(MatchingRuleKey key);

    PolicyOptions Options();

    /// <summary>
    /// Saves (or with null removes) one rule when the saved rules still have <paramref name="expectedRevision"/>
    /// (MatchingRuleConfiguration.Save), records a "policy.write" decision with the actor and reason, and returns its id.
    /// Throws MatchingRuleConflictException when the rules changed.
    /// </summary>
    long Write(MatchingRuleKey key, JsonObject? rule, string expectedRevision, DecisionActor actor, string reason);
}

/// <summary>
/// Two works the simulator evaluates. <see cref="Merged"/> pairs are original members currently joined by an automatic merge
/// (a policy that no longer allows them would split them); other pairs are separate canonical works.
/// </summary>
public sealed record CandidatePair(long Left, long Right, bool Merged = false);

/// <summary>The automatic matcher's verdict for one pair: eligible to (stay) merged, the reason, and optional evidence.</summary>
public sealed record PairEvidence(bool Eligible, string Reason, JsonObject? Detail = null);

/// <summary>
/// Runs the real automatic merge evidence with a substituted policy map (implemented by area 8C over
/// _auto_merge_group_evidence: the same identity, edition, rejection, separate and 0.94 title-floor gates as automation; only
/// the policy map changes, never settings). Read-only.
/// </summary>
public interface IPolicyEvaluator
{
    /// <summary>True when the id is a current canonical work.</summary>
    bool IsCanonical(long entryId);

    /// <summary>Evaluates one pair under the given rules; throws ReviewException(400) for lineages too large to evaluate.</summary>
    PairEvidence Evaluate(CandidatePair pair, IReadOnlyDictionary<MatchingRuleKey, JsonObject?> rules);

    /// <summary>
    /// A stable page of catalogue pairs the policy key can affect (duplicate shortlist pairs and automatically merged original
    /// members involving the key's source or group). An empty page ends the scan.
    /// </summary>
    IReadOnlyList<CandidatePair> Candidates(MatchingRuleKey key, int offset, int limit);
}
