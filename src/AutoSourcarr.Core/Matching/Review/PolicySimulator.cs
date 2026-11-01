using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Sources.Configuration;

namespace AutoSourcarr.Core.Matching.Review;

/// <summary>
/// The policy simulator (reimagined_api.py policy-simulator/options, preview, apply over decision_preview.py). A preview runs the
/// real matcher with the current and the draft policy and lists which works would merge or split:
/// <list type="bullet">
/// <item>{draft, pairs:[[l,r],...]}: up to 32 chosen canonical work pairs, answered at once;</item>
/// <item>{draft, sample:{offset, limit}}: one page (at most 200) of the catalogue pairs the policy can affect, answered at once
/// with next_offset;</item>
/// <item>{draft, scan:true}: every affected catalogue pair (at most 20000) as a background job in Tasks; the answer names the
/// simulation, and {simulation} reads its progress and, when done, its result.</item>
/// </list>
/// Apply saves the draft policy when the preview revision still matches; it never merges or splits works now (automatic matching
/// applies it to future work), and is logged as a "policy.write" decision with the actor.
/// </summary>
public sealed class PolicySimulator(IPolicyStore store, IPolicyEvaluator evaluator, JobQueue? jobs = null)
{
    public static readonly JobType ScanJob = new("policy-simulation", "Simulate matching policy", JobPriority.Low, StallTimeout: TimeSpan.FromMinutes(10));
    public const int MaxPairs = 32, MaxSample = 200, ScanPage = 200, MaxScan = 20000, MaxListed = 500, KeptSimulations = 8;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, Simulation> _simulations = new(StringComparer.Ordinal);

    private sealed class Simulation(string id, PolicyDraft draft, DateTimeOffset created)
    {
        public string Id { get; } = id;
        public PolicyDraft Draft { get; } = draft;
        public DateTimeOffset Created { get; } = created;
        public string State { get; set; } = "queued";
        public int Evaluated { get; set; }
        public string? TaskId { get; set; }
        public string? Error { get; set; }
        public JsonObject? Result { get; set; }
    }

    /// <summary>Sources, groups, saved rules (with their revision) and presets for the simulator form.</summary>
    public JsonObject Options()
    {
        var options = store.Options();
        var rules = store.Rules();
        return new JsonObject
        {
            ["sources"] = options.Sources.DeepClone(),
            ["groups"] = options.Groups.DeepClone(),
            ["rules"] = new JsonArray(rules.Select(pair => (JsonNode?)new JsonObject
            {
                ["scope"] = pair.Key.Scope, ["subject"] = pair.Key.Subject, ["direction"] = pair.Key.Direction, ["rule"] = pair.Value?.DeepClone(),
            }).ToArray()),
            ["revision"] = MatchingRuleConfiguration.Revision(rules),
            ["presets"] = options.Presets.DeepClone(),
        };
    }

    /// <summary>Previews a draft (pairs, sample or scan), or reads a scan simulation by id.</summary>
    public JsonObject Preview(JsonObject body)
    {
        if (body["simulation"] is not null) return Read(Text(body["simulation"]) ?? "");
        var draft = DecisionPreview.Draft(body["draft"], store);
        if (body["scan"] is JsonValue scan && scan.TryGetValue<bool>(out var on) && on) return StartScan(draft);
        if (body["sample"] is JsonObject sample) return Sample(draft, sample);
        return Pairs(draft, ParsePairs(body["pairs"]));
    }

    /// <summary>Saves the previewed draft when the preview revision still matches the current rules and evidence.</summary>
    public JsonObject Apply(JsonObject body, DecisionActor actor)
    {
        var revision = Text(body["revision"]) is { Length: > 0 and <= 64 } given ? given : throw new ReviewException(400, "Preview the policy first");
        lock (_gate)
        {
            JsonObject result;
            if (body["simulation"] is not null)
            {
                var simulation = Find(Text(body["simulation"]) ?? "");
                result = simulation.Result ?? throw new ReviewException(409, "The simulation has not finished");
                if (body["draft"] is not null && !JsonNode.DeepEquals(DecisionPreview.DraftJson(DecisionPreview.Draft(body["draft"], store)), result["draft"]))
                    throw new ReviewException(409, "The draft differs from the simulated draft; preview again");
                if (MatchingRuleConfiguration.Revision(store.Rules()) != Text(result["before_policy"]))
                    throw new ReviewException(409, "Policy or evidence changed; preview again");
            }
            else
            {
                var draft = DecisionPreview.Draft(body["draft"], store);
                result = body["sample"] is JsonObject sample ? Sample(draft, sample) : Pairs(draft, ParsePairs(body["pairs"]));
            }
            if (Text(result["revision"]) != revision) throw new ReviewException(409, "Policy or evidence changed; preview again");
            var counts = result["counts"]!;
            var key = new MatchingRuleKey(Text(result["draft"]!["scope"])!, Text(result["draft"]!["subject"])!, Text(result["draft"]!["direction"])!);
            var rule = result["draft"]!["rule"] is JsonObject saved ? (JsonObject)saved.DeepClone() : null;
            var reason = "Policy simulator: " + Count(counts, DecisionPreview.WouldMerge) + " pair(s) would merge, "
                + Count(counts, DecisionPreview.WouldSplit) + " would split, of " + Count(counts, "evaluated") + " evaluated";
            long decision;
            try { decision = store.Write(key, rule, Text(result["before_policy"])!, actor, reason); }
            catch (MatchingRuleConflictException error) { throw new ReviewException(409, error.Message); }
            catch (KeyNotFoundException) { throw new ReviewException(409, "Policy subject is unavailable"); }
            return new JsonObject
            {
                ["applied"] = true, ["decision"] = decision, ["policy_revision"] = result["after_policy"]?.DeepClone(),
                ["evaluated_pairs"] = counts["evaluated"]?.DeepClone(),
                ["note"] = "Saved for future automatic matching; no work was merged or split now.",
            };
        }
    }

    // ------------------------------------------------------------- previews

    private JsonObject Pairs(PolicyDraft draft, List<CandidatePair> pairs)
    {
        foreach (var pair in pairs)
            if (!evaluator.IsCanonical(pair.Left) || !evaluator.IsCanonical(pair.Right)) throw new ReviewException(409, "Select two current canonical works");
        var rules = store.Rules();
        var after = DecisionPreview.With(rules, draft);
        var rows = pairs.Select(pair => DecisionPreview.Pair(pair, evaluator.Evaluate(pair, rules), evaluator.Evaluate(pair, after))).ToList();
        return Output("pairs", draft, rules, after, rows, rows, null);
    }

    private JsonObject Sample(PolicyDraft draft, JsonObject sample)
    {
        var offset = Integer(sample["offset"], 0, int.MaxValue, 0);
        var limit = Integer(sample["limit"], 1, MaxSample, MaxSample);
        var rules = store.Rules();
        var after = DecisionPreview.With(rules, draft);
        var candidates = evaluator.Candidates(draft.Key, offset, limit).Take(limit).ToList();
        var rows = candidates.Select(pair => DecisionPreview.Pair(pair, evaluator.Evaluate(pair, rules), evaluator.Evaluate(pair, after))).ToList();
        var output = Output("sample", draft, rules, after, rows, rows.Where(Changed), candidates.Count == limit ? offset + limit : null);
        output["offset"] = offset;
        return output;
    }

    private JsonObject StartScan(PolicyDraft draft)
    {
        if (jobs is null) throw new ReviewException(503, "The job queue is not running; use a sample preview instead");
        var simulation = new Simulation(Guid.NewGuid().ToString("N"), draft, DateTimeOffset.UtcNow);
        _simulations[simulation.Id] = simulation;
        foreach (var old in _simulations.Values.OrderByDescending(row => row.Created).Skip(KeptSimulations).ToList()) _simulations.TryRemove(old.Id, out _);
        try
        {
            var handle = jobs.Enqueue(ScanJob, simulation.Id, job =>
            {
                Scan(simulation, job);
                job.Result(simulation.Result?["counts"] is { } counts
                    ? Count(counts, DecisionPreview.WouldMerge) + " would merge, " + Count(counts, DecisionPreview.WouldSplit) + " would split, of " + Count(counts, "evaluated") + " pairs"
                    : simulation.Error ?? "Simulation finished");
                return Task.CompletedTask;
            }, origin: "manual", title: "Simulate matching policy " + draft.Key.Scope + " " + draft.Key.Subject + " (" + draft.Key.Direction + ")", context: simulation.Id);
            simulation.TaskId = handle.Id;
        }
        catch (Exception error) when (error is ObjectDisposedException or ArgumentException)
        {
            _simulations.TryRemove(simulation.Id, out _);
            throw new ReviewException(503, "The job queue is stopping; nothing was started.");
        }
        return Status(simulation);
    }

    private void Scan(Simulation simulation, JobContext job)
    {
        simulation.State = "running";
        try
        {
            var rules = store.Rules();
            var after = DecisionPreview.With(rules, simulation.Draft);
            var all = new List<JsonObject>();
            for (var offset = 0; offset < MaxScan; offset += ScanPage)
            {
                job.CancellationToken.ThrowIfCancellationRequested();
                var page = evaluator.Candidates(simulation.Draft.Key, offset, ScanPage).Take(ScanPage).ToList();
                foreach (var pair in page) all.Add(DecisionPreview.Pair(pair, evaluator.Evaluate(pair, rules), evaluator.Evaluate(pair, after)));
                simulation.Evaluated = all.Count;
                job.Report(all.Count, null, all.Count.ToString(CultureInfo.InvariantCulture) + " pairs evaluated");
                if (page.Count < ScanPage) break;
            }
            var output = Output("scan", simulation.Draft, rules, after, all, all.Where(Changed), null);
            output["simulation"] = simulation.Id;
            output["revision"] = DecisionPreview.Revision(output);
            simulation.Result = output;
            simulation.State = "complete";
        }
        catch (OperationCanceledException)
        {
            simulation.State = "cancelled";
            throw;
        }
        catch (Exception error)
        {
            simulation.State = "failed";
            simulation.Error = error.Message;
            throw;
        }
    }

    private JsonObject Read(string id)
    {
        var simulation = Find(id);
        return simulation.Result is { } result ? (JsonObject)result.DeepClone() : Status(simulation);
    }

    private Simulation Find(string id) =>
        _simulations.TryGetValue(id, out var simulation) ? simulation : throw new ReviewException(404, "Simulation is unavailable; preview again");

    private static JsonObject Status(Simulation simulation) => new()
    {
        ["simulation"] = simulation.Id, ["state"] = simulation.State, ["task_id"] = simulation.TaskId, ["evaluated"] = simulation.Evaluated,
        ["error"] = simulation.Error, ["draft"] = DecisionPreview.DraftJson(simulation.Draft),
    };

    private static JsonObject Output(string mode, PolicyDraft draft, IReadOnlyDictionary<MatchingRuleKey, JsonObject?> before,
        IReadOnlyDictionary<MatchingRuleKey, JsonObject?> after, List<JsonObject> evaluated, IEnumerable<JsonObject> listed, int? next)
    {
        var shown = listed.ToList();
        var output = new JsonObject
        {
            ["engine"] = DecisionPreview.Engine,
            ["mode"] = mode,
            ["draft"] = DecisionPreview.DraftJson(draft),
            ["counts"] = DecisionPreview.Counts(evaluated.Select(row => Text(row["effect"]) ?? DecisionPreview.Unchanged)),
            ["pairs"] = new JsonArray(shown.Take(MaxListed).Select(row => (JsonNode?)row.DeepClone()).ToArray()),
            ["listed_truncated"] = shown.Count > MaxListed,
            ["next_offset"] = next,
            ["before_policy"] = MatchingRuleConfiguration.Revision(before),
            ["after_policy"] = MatchingRuleConfiguration.Revision(after),
            ["scope_note"] = DecisionPreview.ScopeNote,
        };
        output["revision"] = DecisionPreview.Revision(output);
        return output;
    }

    // -------------------------------------------------------------- helpers

    private static bool Changed(JsonObject row) => row["changed"] is JsonValue value && value.TryGetValue<bool>(out var changed) && changed;

    private static List<CandidatePair> ParsePairs(JsonNode? node)
    {
        if (node is not JsonArray pairs || pairs.Count is < 1 or > MaxPairs) throw new ReviewException(400, "Choose 1 to 32 representative pairs");
        var list = new List<CandidatePair>();
        foreach (var item in pairs)
        {
            if (item is not JsonArray pair || pair.Count != 2) throw new ReviewException(400, "Invalid review pair");
            var left = ReviewWorkbench.Id(pair[0], "Invalid review pair");
            var right = ReviewWorkbench.Id(pair[1], "Invalid review pair");
            if (left == right) throw new ReviewException(400, "Select two current canonical works");
            list.Add(new CandidatePair(left, right));
        }
        return list;
    }

    private static int Integer(JsonNode? node, int minimum, int maximum, int fallback)
    {
        if (node is null) return fallback;
        if (node is not JsonValue value || !value.TryGetValue<long>(out var number) || number < minimum || number > maximum)
            throw new ReviewException(400, "Sample offset and limit must be whole numbers (limit 1 to " + MaxSample.ToString(CultureInfo.InvariantCulture) + ")");
        return (int)number;
    }

    private static string Count(JsonNode counts, string key) => (counts[key]?.GetValue<int>() ?? 0).ToString(CultureInfo.InvariantCulture);
    private static string? Text(JsonNode? node) => ReviewWorkbench.Text(node);
}
