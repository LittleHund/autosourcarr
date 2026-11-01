using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Matching.Duplicates;
using AutoSourcarr.Core.Matching.Review;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources.Configuration;

namespace AutoSourcarr.Core.Matching.Wiring;

/// <summary>
/// Port of matching_policies.apply for the automatic duplicate gate (<see cref="IDuplicateMatchingPolicy"/>): per source
/// (or its group) and direction (internal = same source, external = different sources), a disabled rule blocks the
/// automatic merge and every required check (title, summary, author, tags) must reach its threshold on the payloads of
/// exactly that source pair. A check without evidence blocks. Rules are read from the store (cached briefly, dropped on
/// every rule write) unless <see cref="Override"/> substitutes a draft map (the policy simulator).
/// </summary>
/// <remarks>
/// Tags are compared as lower-cased "namespace:name" keys of the payloads' genre and tags (language, category, type and
/// uploader namespaces excluded); 0.4.5 additionally folded reviewed tag equivalences (tag_store.canonical_keys).
/// </remarks>
public sealed class MatchingPolicyApplication : IDuplicateMatchingPolicy
{
    private static readonly AsyncLocal<IReadOnlyDictionary<MatchingRuleKey, JsonObject?>?> Draft = new();
    private static readonly string[] Checks = ["title", "summary", "author", "tags"];
    private static readonly HashSet<string> IgnoredTagNamespaces = new(StringComparer.Ordinal) { "language", "category", "type", "uploader" };
    private readonly Func<IReadOnlyDictionary<MatchingRuleKey, JsonObject?>> _rules;
    private readonly Func<IReadOnlyDictionary<string, JsonObject>> _sources;
    private readonly TimeProvider _clock;
    private readonly Lock _cacheGate = new();
    private (long At, IReadOnlyDictionary<MatchingRuleKey, JsonObject?> Rules, IReadOnlyDictionary<string, JsonObject> Sources)? _cache;

    /// <param name="rules">The saved rules (MatchingPolicyStore.Rules).</param>
    /// <param name="sources">source_config rows by source id (for the group of a source).</param>
    public MatchingPolicyApplication(Func<IReadOnlyDictionary<MatchingRuleKey, JsonObject?>> rules, Func<IReadOnlyDictionary<string, JsonObject>> sources, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(rules); ArgumentNullException.ThrowIfNull(sources);
        _rules = rules; _sources = sources; _clock = clock ?? TimeProvider.System;
    }

    /// <summary>source_config by source id from the catalogue (the shape MatchingRuleConfiguration.Effective reads).</summary>
    public static Func<IReadOnlyDictionary<string, JsonObject>> CatalogueSources(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return () => database.Read(DatabaseFile.Catalog, session => SourceConfigurationStore.Sources(session))
            .GroupBy(source => SourceConfigurationJson.Text(source["source_id"]), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    /// <summary>Drops the cached rules (called after every rule write).</summary>
    public void Invalidate() { lock (_cacheGate) _cache = null; }

    /// <summary>Evaluates with <paramref name="rules"/> instead of the saved rules on this call flow until disposed.</summary>
    public IDisposable Override(IReadOnlyDictionary<MatchingRuleKey, JsonObject?> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var previous = Draft.Value;
        Draft.Value = rules;
        return new Restore(previous);
    }

    private sealed class Restore(IReadOnlyDictionary<MatchingRuleKey, JsonObject?>? previous) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Draft.Value = previous; }
    }

    private (IReadOnlyDictionary<MatchingRuleKey, JsonObject?> Rules, IReadOnlyDictionary<string, JsonObject> Sources) Current()
    {
        var now = _clock.GetTimestamp();
        lock (_cacheGate)
            if (_cache is { } cached && _clock.GetElapsedTime(cached.At, now) < TimeSpan.FromSeconds(10))
                return (Draft.Value ?? cached.Rules, cached.Sources);
        var rules = _rules(); var sources = _sources();
        lock (_cacheGate) _cache = (now, rules, sources);
        return (Draft.Value ?? rules, sources);
    }

    public JsonObject Apply(JsonObject evidence, JsonObject left, JsonObject right)
    {
        ArgumentNullException.ThrowIfNull(evidence); ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        if (!Flag(evidence["eligible"])) return evidence;
        var (values, sources) = Current();
        if (values.Count == 0) return evidence;
        var leftPayloads = Payloads(left); var rightPayloads = Payloads(right);
        var leftIds = SourceIds(left, leftPayloads); var rightIds = SourceIds(right, rightPayloads);
        var required = new List<(string Left, string Right, string Direction, List<(JsonObject Rule, string Origin)> Pair)>();
        foreach (var l in leftIds.Order(StringComparer.Ordinal))
            foreach (var r in rightIds.Order(StringComparer.Ordinal))
            {
                var direction = l.Length != 0 && l == r ? "internal" : "external";
                var pair = new List<(JsonObject, string)>();
                foreach (var sid in new[] { l, r }.Distinct(StringComparer.Ordinal))
                {
                    var (rule, origin) = MatchingRuleConfiguration.Effective(values, sources, sid, direction);
                    if (rule is { } found) pair.Add((found, origin));
                }
                if (pair.Count != 0) required.Add((l, r, direction, pair));
            }
        if (required.Count == 0) return evidence;
        var checks = new JsonArray();
        JsonObject Blocked(string reason)
        {
            var result = (JsonObject)evidence.DeepClone();
            result["eligible"] = false; result["policy_blocked"] = true; result["reason"] = reason; result["policy_checks"] = checks.DeepClone();
            return result;
        }
        foreach (var (l, r, direction, pair) in required)
        {
            var lp = leftPayloads.Where(payload => PayloadSource(payload) == l).ToArray();
            var rp = rightPayloads.Where(payload => PayloadSource(payload) == r).ToArray();
            Dictionary<string, double?>? scores = null;
            foreach (var (rule, origin) in pair)
            {
                var label = direction + " matching · " + origin;
                if (!Flag(rule["enabled"])) return Blocked(label + " is disabled");
                scores ??= Metrics(lp, rp, pair.Where(item => Flag(item.Rule["enabled"]))
                    .SelectMany(item => (item.Rule["checks"] as JsonObject ?? new JsonObject()).Select(check => check.Key)).ToHashSet(StringComparer.Ordinal));
                foreach (var (key, node) in rule["checks"] as JsonObject ?? new JsonObject())
                {
                    var threshold = Number(node) ?? 1;
                    var score = scores.GetValueOrDefault(key);
                    var passed = score is { } value && value + 1e-12 >= threshold;
                    checks.Add(new JsonObject
                    {
                        ["scope"] = origin, ["direction"] = direction, ["check"] = key, ["threshold"] = threshold, ["score"] = score, ["passed"] = passed,
                    });
                    if (!passed)
                        return Blocked(label + ": " + key + " " + (score is { } found ? Percent(found) + " is below " + Percent(threshold) : "evidence missing"));
                }
            }
        }
        var allowed = (JsonObject)evidence.DeepClone();
        allowed["policy_checks"] = checks; allowed["policy_revision"] = MatchingRuleConfiguration.Revision(values);
        return allowed;
    }

    private static Dictionary<string, double?> Metrics(IReadOnlyList<JsonObject> left, IReadOnlyList<JsonObject> right, IReadOnlySet<string> required)
    {
        var result = Checks.ToDictionary(key => key, _ => (double?)null, StringComparer.Ordinal);
        if (left.Count != 0 && right.Count != 0)
        {
            if (required.Contains("title"))
            {
                var lt = Titles(left); var rt = Titles(right);
                result["title"] = lt.Count != 0 && rt.Count != 0 ? TextMatcher.BestAutomaticTitleScore(lt, rt).Score : null;
            }
            if (required.Contains("summary"))
            {
                var ld = left.Select(payload => Text(payload["description"])).Where(text => text.Length != 0).ToArray();
                var rd = right.Select(payload => Text(payload["description"])).Where(text => text.Length != 0).ToArray();
                result["summary"] = rd.Length != 0 && ld.Length != 0 ? ld.Max(description => TextMatcher.BestDescriptionScore(description, rd)) : null;
            }
            if (required.Contains("author"))
            {
                static string Creator(JsonObject payload) => Text(payload["author"]) is { Length: > 0 } author ? author : Text(payload["artist"]);
                var la = left.Select(Creator).Where(text => text.Length != 0).ToArray();
                var ra = right.Select(Creator).Where(text => text.Length != 0).ToArray();
                result["author"] = la.Length != 0 && ra.Length != 0 ? la.SelectMany(a => ra.Select(b => TextMatcher.NameSimilarity(a, b))).Max() : null;
            }
        }
        if (required.Contains("tags"))
        {
            var lt = left.Select(TagKeys).ToArray(); var rt = right.Select(TagKeys).ToArray();
            var ratios = lt.SelectMany(a => rt.Where(b => a.Count != 0 && b.Count != 0).Select(b => (double)a.Intersect(b).Count() / a.Union(b).Count())).ToArray();
            result["tags"] = ratios.Length != 0 ? ratios.Max() : null;
        }
        return result;
    }

    private static List<string> Titles(IEnumerable<JsonObject> payloads)
    {
        var values = new List<string>();
        foreach (var payload in payloads)
        {
            var gallery = payload["_gallery_metadata"] as JsonObject;
            var title = Text(gallery?["work_title"]) is { Length: > 0 } work ? work : Text(gallery?["title"]) is { Length: > 0 } named ? named : Text(payload["title"]);
            values.Add(title);
            values.AddRange(TextMatcher.ExtractAltTitles(Text(payload["description"])));
        }
        return values.Where(value => value.Length != 0).ToList();
    }

    private static HashSet<string> TagKeys(JsonObject payload)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Add(string space, string name)
        {
            space = space.Trim().ToLowerInvariant(); name = name.Trim().ToLowerInvariant();
            if (name.Length == 0 || IgnoredTagNamespaces.Contains(space)) return;
            keys.Add(space + ":" + name);
        }
        switch (payload["genre"])
        {
            case JsonArray genres: foreach (var item in genres) Add("", Text(item)); break;
            case JsonValue text: foreach (var item in Text(text).Split(',')) Add("", item); break;
        }
        if (payload["tags"] is JsonArray tags)
            foreach (var tag in tags)
            {
                if (tag is JsonObject named) Add(Text(named["namespace"]), Text(named["name"]));
                else Add("", Text(tag));
            }
        return keys;
    }

    private static JsonObject[] Payloads(JsonObject snapshot) => snapshot["policy_payloads"] is JsonArray array ? array.OfType<JsonObject>().ToArray() : [];

    private static HashSet<string> SourceIds(JsonObject snapshot, IEnumerable<JsonObject> payloads)
    {
        var ids = snapshot["source_ids"] is JsonArray listed
            ? listed.Select(Text).Where(id => id.Length != 0).ToHashSet(StringComparer.Ordinal)
            : payloads.Select(PayloadSource).Where(id => id.Length != 0).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) ids.Add("");
        return ids;
    }

    private static string PayloadSource(JsonObject payload) => Text(payload["sourceId"]) is { Length: > 0 } id ? id : Text(payload["source_id"]);

    private static string Percent(double value) => Math.Round(value * 100, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture) + "%";

    internal static string Text(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<string>(out var text) ? text
        : value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : ""
        : "";

    internal static bool Flag(JsonNode? node) => node is JsonValue value && (value.TryGetValue<bool>(out var flag) ? flag : value.TryGetValue<long>(out var number) && number != 0);

    private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;
}

/// <summary>
/// The policy simulator's evaluator (<see cref="IPolicyEvaluator"/>) over the real automatic merge evidence of
/// DuplicateMatching with a substituted policy map: separate works go through GroupEvidence (the same identity, edition,
/// review, separate-record and 0.94 title-floor gates as automation, non-transitive over original members); members of an
/// automatic merge are re-checked from their frozen lineage snapshots with the same gate. Read-only.
/// </summary>
public sealed class DuplicatePolicyEvaluator(SqliteDatabase database, DuplicateMatching duplicates, MatchingPolicyApplication policy, TimeProvider? clock = null) : IPolicyEvaluator
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private (MatchingRuleKey Key, long At, IReadOnlyList<CandidatePair> Pairs)? _candidates;

    public bool IsCanonical(long entryId) => entryId > 0 && database.Read(DatabaseFile.Catalog, c =>
        PortableIdentityResolver.Canonical(c, entryId) == entryId && c.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", entryId)) is not null);

    public PairEvidence Evaluate(CandidatePair pair, IReadOnlyDictionary<MatchingRuleKey, JsonObject?> rules)
    {
        ArgumentNullException.ThrowIfNull(pair); ArgumentNullException.ThrowIfNull(rules);
        using var draft = policy.Override(rules);
        if (pair.Merged)
        {
            var (left, right) = database.Read(DatabaseFile.Catalog, c => (Member(c, pair.Left), Member(c, pair.Right)));
            if (left is null || right is null) return new(false, "The frozen original member snapshot is not available", null);
            var decision = duplicates.Gate.Decide(left, right);
            return new(decision.Eligible, decision.Reason, (JsonObject)decision.Evidence.DeepClone());
        }
        var evidence = duplicates.GroupEvidence(pair.Left, pair.Right);
        return new(MatchingPolicyApplication.Flag(evidence["eligible"]), MatchingPolicyApplication.Text(evidence["reason"]), evidence);
    }

    public IReadOnlyList<CandidatePair> Candidates(MatchingRuleKey key, int offset, int limit)
    {
        if (limit <= 0 || offset < 0) return [];
        IReadOnlyList<CandidatePair> pairs;
        lock (_gate)
            pairs = _candidates is { } cached && cached.Key == key && _clock.GetElapsedTime(cached.At) < TimeSpan.FromMinutes(2) ? cached.Pairs : [];
        if (pairs.Count == 0 || offset == 0)
        {
            pairs = Build(key);
            lock (_gate) _candidates = (key, _clock.GetTimestamp(), pairs);
        }
        return pairs.Skip(offset).Take(limit).ToArray();
    }

    private List<CandidatePair> Build(MatchingRuleKey key)
    {
        var (sources, merged, entries) = database.Read(DatabaseFile.Catalog, c =>
        {
            var ids = key.Scope == "source" ? new HashSet<string>(StringComparer.Ordinal) { key.Subject }
                : c.Query("SELECT source_id FROM source_config WHERE CAST(group_id AS TEXT)=$group", ("$group", key.Subject))
                    .Select(row => row.Text("source_id")).ToHashSet(StringComparer.Ordinal);
            var lineage = new List<CandidatePair>();
            foreach (var group in c.Query("SELECT canonical_entry_id,member_entry_id,snapshot_json FROM automatic_merge_lineage ORDER BY canonical_entry_id,member_entry_id")
                .GroupBy(row => row.Integer("canonical_entry_id")))
            {
                var members = group.Select(row => (Id: row.Integer("member_entry_id"), Sources: SnapshotSources(row.Text("snapshot_json")))).ToArray();
                if (!members.Any(member => member.Sources.Overlaps(ids))) continue;
                for (var i = 0; i < members.Length; i++)
                    for (var j = i + 1; j < members.Length; j++)
                        lineage.Add(new CandidatePair(members[i].Id, members[j].Id, Merged: true));
            }
            var owners = new HashSet<long>();
            foreach (var source in ids)
                foreach (var row in c.Query("SELECT entry_id FROM source_matches WHERE source_id=$source AND active=1 UNION SELECT entry_id FROM source_variants WHERE source_id=$source AND active=1", ("$source", source)))
                    owners.Add(PortableIdentityResolver.Canonical(c, row.Integer("entry_id")));
            return (ids, lineage, owners);
        });
        var result = new List<CandidatePair>(merged);
        if (sources.Count != 0 && entries.Count != 0)
            result.AddRange(duplicates.Shortlist(entries).Pairs.Keys
                .Where(pair => entries.Contains(pair.Left) || entries.Contains(pair.Right))
                .OrderBy(pair => pair.Left).ThenBy(pair => pair.Right)
                .Select(pair => new CandidatePair(pair.Left, pair.Right)));
        return result;
    }

    private static HashSet<string> SnapshotSources(string json)
    {
        try
        {
            return JsonNode.Parse(json.Length == 0 ? "{}" : json) is JsonObject snapshot && snapshot["source_ids"] is JsonArray ids
                ? ids.Select(MatchingPolicyApplication.Text).Where(id => id.Length != 0).ToHashSet(StringComparer.Ordinal) : [];
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private static JsonObject? Member(DatabaseSession c, long memberId)
    {
        var row = c.Query("SELECT snapshot_json FROM automatic_merge_lineage WHERE member_entry_id=$id ORDER BY merged_at DESC LIMIT 1", ("$id", memberId)).FirstOrDefault();
        if (row is null) return null;
        try { return JsonNode.Parse(row.Text("snapshot_json", "{}")) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
