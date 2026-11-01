using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Sources.Configuration;

namespace AutoSourcarr.Core.Matching.Review;

/// <summary>A matching policy change under review: one rule key and its draft rule (null restores the inherited rule).</summary>
public sealed record PolicyDraft(MatchingRuleKey Key, JsonObject? Rule);

/// <summary>
/// decision_preview.py: runs the actual automatic matcher on pairs with the current and with a draft policy map, without
/// changing any evidence, and says per pair what the draft would do (would_merge, would_split, would_not_merge, unchanged).
/// </summary>
public static class DecisionPreview
{
    public const string Engine = "autosourcarr-policy-preview-v2";
    public const string WouldMerge = "would_merge", WouldSplit = "would_split", WouldNotMerge = "would_not_merge", Unchanged = "unchanged";
    public const string ScopeNote = "Evaluates these pairs only; applying saves the policy for future automatic matching and never merges or splits works now.";

    /// <summary>Reads {scope, subject, direction, rule} or {scope, subject, direction, preset}; the rule is validated.</summary>
    public static PolicyDraft Draft(JsonNode? node, IPolicyStore store)
    {
        if (node is not JsonObject draft) throw new ReviewException(400, "Select one policy and its draft rule");
        var keys = draft.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        if (!(keys.SetEquals(["scope", "subject", "direction", "rule"]) || keys.SetEquals(["scope", "subject", "direction", "preset"])))
            throw new ReviewException(400, "Select one policy and its draft rule");
        var scope = ReviewWorkbench.Text(draft["scope"]);
        var direction = ReviewWorkbench.Text(draft["direction"]);
        if (scope is not ("source" or "group") || direction is not ("internal" or "external")) throw new ReviewException(400, "Unknown policy scope");
        var subject = draft["subject"] switch
        {
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            JsonValue value when value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<long>(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => "",
        };
        if (subject.Length is < 1 or > 200) throw new ReviewException(400, "Select a policy subject");
        JsonNode? rule = draft["rule"];
        if (keys.Contains("preset"))
        {
            var preset = ReviewWorkbench.Text(draft["preset"]);
            rule = store.Options().Presets.OfType<JsonObject>().FirstOrDefault(row => ReviewWorkbench.Text(row["id"]) == preset)?["rule"]
                ?? throw new ReviewException(400, "Unknown matching preset");
        }
        JsonObject? validated;
        try { validated = MatchingRuleConfiguration.Validate(rule); }
        catch (ArgumentException error) { throw new ReviewException(400, error.Message); }
        var key = new MatchingRuleKey(scope, subject, direction);
        if (!store.SubjectExists(key)) throw new ReviewException(409, "Policy subject is unavailable");
        return new PolicyDraft(key, validated);
    }

    /// <summary>The saved rules with the draft substituted for its key.</summary>
    public static Dictionary<MatchingRuleKey, JsonObject?> With(IReadOnlyDictionary<MatchingRuleKey, JsonObject?> rules, PolicyDraft draft)
    {
        var after = rules.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (draft.Rule is null) after.Remove(draft.Key);
        else after[draft.Key] = draft.Rule;
        return after;
    }

    public static string Effect(CandidatePair pair, PairEvidence before, PairEvidence after) => pair.Merged
        ? (after.Eligible ? Unchanged : WouldSplit)
        : before.Eligible == after.Eligible ? Unchanged : after.Eligible ? WouldMerge : WouldNotMerge;

    public static JsonObject Pair(CandidatePair pair, PairEvidence before, PairEvidence after)
    {
        var effect = Effect(pair, before, after);
        return new JsonObject
        {
            ["pair"] = ReviewJson.Numbers([pair.Left, pair.Right]),
            ["merged"] = pair.Merged,
            ["before"] = Evidence(before),
            ["after"] = Evidence(after),
            ["changed"] = effect != Unchanged,
            ["effect"] = effect,
        };
    }

    public static JsonObject DraftJson(PolicyDraft draft) => new()
    {
        ["scope"] = draft.Key.Scope, ["subject"] = draft.Key.Subject, ["direction"] = draft.Key.Direction, ["rule"] = draft.Rule?.DeepClone(),
    };

    /// <summary>Counts of evaluated pairs by effect.</summary>
    public static JsonObject Counts(IEnumerable<string> effects)
    {
        var counts = new JsonObject { ["evaluated"] = 0, [WouldMerge] = 0, [WouldSplit] = 0, [WouldNotMerge] = 0, [Unchanged] = 0 };
        foreach (var effect in effects)
        {
            counts["evaluated"] = counts["evaluated"]!.GetValue<int>() + 1;
            counts[effect] = counts[effect]!.GetValue<int>() + 1;
        }
        return counts;
    }

    /// <summary>The revision of a preview: SHA-256 of its canonical JSON without the revision field.</summary>
    public static string Revision(JsonObject output)
    {
        var copy = output.DeepClone().AsObject();
        copy.Remove("revision");
        return Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Bytes(copy)));
    }

    private static JsonObject Evidence(PairEvidence evidence)
    {
        var value = new JsonObject { ["eligible"] = evidence.Eligible, ["reason"] = evidence.Reason };
        if (evidence.Detail is not null) value["detail"] = evidence.Detail.DeepClone();
        return value;
    }
}
