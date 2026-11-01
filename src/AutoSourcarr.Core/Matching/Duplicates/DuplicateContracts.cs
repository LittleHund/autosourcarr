using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Matching.Duplicates;

/// <summary>
/// The answer of an automatic duplicate gate for one pair of original member snapshots.
/// <see cref="Evidence"/> always carries <c>eligible</c> and <c>reason</c> plus the scores it used
/// (exact_title_count, exact_titles, summary_similarity, best_title_similarity, shared_identities, ...).
/// </summary>
public sealed record AutomaticMatchDecision(bool Eligible, string Reason, JsonObject Evidence)
{
    public static AutomaticMatchDecision No(string reason, JsonObject? evidence = null)
    {
        var value = evidence ?? new JsonObject { ["exact_title_count"] = 0, ["summary_similarity"] = 0.0 };
        value["eligible"] = false; value["reason"] = reason;
        return new(false, reason, value);
    }
}

/// <summary>
/// Automatic merge gate (phase 8C): may two entries be merged without a person? Inputs are the frozen native evidence
/// snapshots of single original members (NativeCatalogueEvidence.Snapshot shape: titles, normalized_titles, author,
/// descriptions, identities, identity_details, thumbnail_fingerprints, color_mode, media_mode, shared_unverified,
/// gallery_payloads, policy_payloads, source_ids; DuplicateEvidence adds <c>authors</c> and <c>year</c>).
/// The caller applies it to every original member pair (non-transitive). The scorer of area 8A
/// (Matching/Scoring/MatchScorer) is expected to implement this; <see cref="TextAutomaticMatchGate"/> is the stand-in.
/// </summary>
public interface IAutomaticMatchGate
{
    AutomaticMatchDecision Decide(JsonObject left, JsonObject right);
}

/// <summary>
/// Read side of the manual decisions duplicate review needs (area 8B's ManualDecisions adapts to it).
/// All ids are canonicalised by the implementation.
/// </summary>
public interface IDuplicateManualView
{
    /// <summary>A person said these two entries are different works (duplicate_reviews not_duplicate).</summary>
    bool ReviewedDifferent(long left, long right);
    /// <summary>Both entries hold records of one manual match group (manual_match_bindings.group_id).</summary>
    bool ManuallyGrouped(long left, long right);
    /// <summary>The record was explicitly kept separate from this entry (manual_separate_records).</summary>
    bool SeparatedRecord(long entryId, string sourceId, long sourceMangaId);
    /// <summary>After a reviewed "same work" merge: lock and bind the reviewed records as one manual group.</summary>
    JsonObject PersistReviewedGroup(long canonicalEntryId, IReadOnlyList<NativeSourceRecord> records);
}

/// <summary>The structural primitives duplicate review uses (implemented by CatalogueStructure: CatalogueMerge / CatalogueSplit).</summary>
public interface IDuplicateStructure
{
    /// <summary>Merges two entries (the more reliable survives), finishes chapter recovery; returns the surviving id.</summary>
    long MergeEntries(long left, long right, string reason);
    /// <summary>
    /// Merges like <see cref="MergeEntries(long, long, string)"/> and runs <paramref name="commit"/> (session, survivor,
    /// merged-away entry) inside the merge's own catalogue transaction, so the caller's rollback event, lineage, manual
    /// group and decision commit or fail together with the merge. <paramref name="automatic"/> refuses (InvalidOperationException
    /// "manual-titles") to merge away an entry with manual titles.
    /// </summary>
    long MergeEntries(long left, long right, string reason, bool automatic, Action<DatabaseSession, long, long> commit);
    /// <summary>Moves the records to one new entry; <paramref name="manual"/> also persists the user's split decision.</summary>
    long SplitRecords(long entryId, IReadOnlyList<NativeSourceRecord> records, bool manual, string matchType);
    /// <summary>Retries committed merges/splits whose chapter work is still pending.</summary>
    void ReconcilePending(CancellationToken cancellationToken);
}

/// <summary>Optional source matching rules (matching_policies.apply) applied to a gate result; null = no rules.</summary>
public interface IDuplicateMatchingPolicy
{
    JsonObject Apply(JsonObject evidence, JsonObject left, JsonObject right);
}

/// <summary>
/// Optional network work done before an automatic sweep: resolve provider identities and decode thumbnail fingerprints
/// for shortlisted entries (EntryIngest / metadata area). Returns how many entries gained evidence.
/// </summary>
public interface IDuplicateHydration
{
    Task<int> ResolveIdentitiesAsync(IReadOnlyList<long> entryIds, CancellationToken cancellationToken);
    Task<int> FingerprintThumbnailsAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>The follow-up of Redo automation: search every source again and refresh one entry (SourceDiscovery / EntryIngest).</summary>
public interface IEntryAutomationRedo
{
    Task<long> RediscoverAsync(long entryId, CancellationToken cancellationToken);
    Task RefreshAsync(long entryId, CancellationToken cancellationToken);
}

internal static class DuplicateJson
{
    public static string Str(JsonNode? node, string fallback = "")
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text)) return text;
            if (value.GetValueKind() is System.Text.Json.JsonValueKind.Number or System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) return value.ToJsonString();
        }
        return fallback;
    }
    public static double Num(JsonNode? node, double fallback = 0)
    {
        if (node is not JsonValue) return fallback;
        var raw = node.ToJsonString().Trim('"');
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;
    }
    public static long Long(JsonNode? node, long fallback = 0)
    {
        if (node is not JsonValue) return fallback;
        var raw = node.ToJsonString().Trim('"');
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && Math.Abs(number) < 9e15 ? (long)number : fallback;
    }
    public static bool Bool(JsonNode? node) => node is JsonValue value && (value.TryGetValue<bool>(out var flag) ? flag : Long(node) != 0);
    public static IReadOnlyList<string> Strings(JsonNode? node) => node is JsonArray array
        ? array.Select(item => Str(item)).Where(text => text.Length != 0).ToArray() : [];
    public static JsonArray JArray(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    public static JsonArray JArray(IEnumerable<long> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    public static (string Name, object? Value)[] P(params object?[] values) => values.Select((value, index) => ("$p" + index.ToString(CultureInfo.InvariantCulture), value)).ToArray();
    public static (long Left, long Right) Pair(long left, long right) => left < right ? (left, right) : (right, left);
}
