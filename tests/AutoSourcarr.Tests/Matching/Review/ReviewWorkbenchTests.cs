using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Review;
using AutoSourcarr.Core.Sources.Configuration;

namespace AutoSourcarr.Tests.Matching;

/// <summary>Phase 8E: review workbench preview/apply/undo with actors, and the policy simulator over a small catalogue.</summary>
public static class ReviewWorkbenchTests
{
    private static readonly DecisionActor Alice = DecisionActor.User("alice");
    private static readonly DecisionActor Script = DecisionActor.Key("nightly");

    public static void Register(TestSuite suite)
    {
        suite.Add("review: separate preview, apply and undo round trip keeps actors in the log", () =>
        {
            var (log, actions, workbench) = Fixture();
            actions.Add(1, "Alpha", "s:1");
            actions.Add(2, "Alpha Remix", "s:2");
            var preview = workbench.Preview(Body("""{"pairs":[[2,1]]}"""));
            Check.Equal("1,2", Pair(preview["items"]![0]!["pair"]));
            Check.True(preview["items"]![0]!["before"]!["decision"] is null, "No review existed before");
            var applied = workbench.Apply(Body("""{"pairs":[[1,2]],"revision":"REV"}""".Replace("REV", Str(preview["revision"]), StringComparison.Ordinal)), Alice);
            Check.Equal(1, applied["applied"]!.GetValue<int>());
            Check.Equal("not_duplicate", actions.PairDecision(1, 2));
            var batch = applied["id"]!.GetValue<long>();
            var audit = workbench.Audit(null);
            var listed = audit["bulk_actions"]![0]!;
            Check.Equal(batch, listed["id"]!.GetValue<long>());
            Check.Equal("alice", Str(listed["actor"]!["name"]));
            Check.True(listed["undone_at"] is null, "Not undone yet");
            Check.True(audit["decisions"]!.AsArray().Any(row => Str(row!["kind"]) == "review.duplicate" && Str(row["actor"]!["name"]) == "alice"),
                "The item decision carries the actor");

            var undo = workbench.Undo(batch, Script);
            Check.Equal(1, undo["undone"]!.GetValue<int>());
            Check.True(actions.PairDecision(1, 2) is null, "Undo removes the review that did not exist before");
            Check.True(log.Rows.Any(row => row.Decision.UndoOf == batch.ToString(CultureInfo.InvariantCulture) && row.Decision.Actor == Script), "Undo is logged with UndoOf and its actor");
            var after = workbench.Audit(null)["bulk_actions"]![0]!;
            Check.Equal("nightly", Str(after["undone_by"]!["name"]));
            Check.Equal("key", Str(after["undone_by"]!["kind"]));
            Check.Equal(409, Check.Throws<ReviewException>(() => workbench.Undo(batch, Alice)).StatusCode);
        });

        suite.Add("review: merge, split, manual match and title batch undoes back to the previous state", () =>
        {
            var (_, actions, workbench) = Fixture();
            actions.Add(1, "One", "a:1");
            actions.Add(2, "One again", "a:2");
            actions.Add(3, "Three", "a:3", "a:4");
            actions.Add(4, "Four");
            actions.Add(5, "Five", "c:1");
            actions.Add(6, "Six", "c:2");
            const string Items = """{"actions":[{"kind":"merge","target":1,"source":2},{"kind":"split","entry":3,"records":["a:4"]},{"kind":"manual.match","entry":5,"record":"c:2"},{"kind":"title","entry":4,"title":" Fourth "}]""";
            var preview = workbench.Preview(Body(Items + "}"));
            Check.Equal(4, preview["items"]!.AsArray().Count);
            var applied = workbench.Apply(Body(Items + ",\"revision\":\"" + Str(preview["revision"]) + "\"}"), Alice);
            Check.Equal(4, applied["applied"]!.GetValue<int>());
            Check.Equal("a:1,a:2", string.Join(",", actions.Entry(1)!.Records));
            Check.Equal(1L, actions.Entry(2)!.CanonicalId);
            Check.Equal("a:3", string.Join(",", actions.Entry(3)!.Records));
            Check.Equal(5L, actions.RecordEntry("c:2")!.Value);
            Check.Equal("Fourth", actions.Entry(4)!.Title);
            Check.True(actions.Entry(4)!.TitleManual, "Title choice is manual");

            workbench.Undo(applied["id"]!.GetValue<long>(), Alice);
            Check.Equal("a:1", string.Join(",", actions.Entry(1)!.Records));
            Check.True(actions.RecordEntry("a:2") is { } moved && moved != 1, "Merged-in record leaves the target again");
            Check.Equal("a:3,a:4", string.Join(",", actions.Entry(3)!.Records));
            Check.Equal(6L, actions.RecordEntry("c:2")!.Value);
            Check.True(!actions.Entry(4)!.TitleManual, "Automatic title applies again");
            Check.True(actions.Calls.Where(call => call.UndoOf is not null).All(call => call.UndoOf == applied["id"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture)),
                "Every inverse action names the batch it undoes");
        });

        suite.Add("review: stale previews, changed works and invalid items are refused", () =>
        {
            var (_, actions, workbench) = Fixture();
            actions.Add(1, "One", "a:1");
            actions.Add(2, "Two", "a:2");
            actions.Add(3, "Three", "a:3");
            var preview = workbench.Preview(Body("""{"pairs":[[1,2]]}"""));
            actions.ReviewPair(1, 2, "duplicate", new ReviewContext(Alice, "elsewhere"));
            Check.Equal(409, Check.Throws<ReviewException>(() => workbench.Apply(Body("""{"pairs":[[1,2]],"revision":"REV"}""".Replace("REV", Str(preview["revision"]), StringComparison.Ordinal)), Alice)).StatusCode);

            var fresh = workbench.Preview(Body("""{"pairs":[[1,2]]}"""));
            Check.Equal("duplicate", Str(fresh["items"]![0]!["before"]!["decision"]));
            var batch = workbench.Apply(Body("""{"pairs":[[1,2]],"revision":"REV"}""".Replace("REV", Str(fresh["revision"]), StringComparison.Ordinal)), Alice)["id"]!.GetValue<long>();
            actions.ReviewPair(1, 2, null, new ReviewContext(Alice, "elsewhere"));
            Check.Equal(409, Check.Throws<ReviewException>(() => workbench.Undo(batch, Alice)).StatusCode);
            Check.Equal(404, Check.Throws<ReviewException>(() => workbench.Undo(9999, Alice)).StatusCode);

            Check.Equal(400, Check.Throws<ReviewException>(() => workbench.Preview(Body("""{"pairs":[[1,1]]}"""))).StatusCode);
            Check.Equal(400, Check.Throws<ReviewException>(() => workbench.Preview(Body("""{"pairs":[[1,2],[2,1]]}"""))).StatusCode);
            Check.Equal(400, Check.Throws<ReviewException>(() => workbench.Preview(Body("""{"pairs":[]}"""))).StatusCode);
            Check.Equal(400, Check.Throws<ReviewException>(() => workbench.Preview(Body("""{"actions":[{"kind":"merge","target":1,"source":2},{"kind":"title","entry":2,"title":"x"}]}"""))).StatusCode);
            Check.Equal(400, Check.Throws<ReviewException>(() => workbench.Preview(Body("""{"actions":[{"kind":"split","entry":3,"records":["a:3"]}]}"""))).StatusCode);
            actions.Merge(1, 3, new ReviewContext(Alice, "elsewhere"));
            Check.Equal(409, Check.Throws<ReviewException>(() => workbench.Preview(Body("""{"pairs":[[2,3]]}"""))).StatusCode);
            Check.Equal(404, Check.Throws<ReviewException>(() => workbench.Audit(77)).StatusCode);
            Check.Equal("a:1,a:3", string.Join(",", workbench.Audit(1)["entry"]!["records"]!.AsArray().Select(Str)));
        });

        suite.Add("policy simulator: pairs, sample and scan over a small catalogue; apply saves the policy with the actor", () =>
        {
            var log = new MemoryDecisionLog();
            var store = new FakePolicyStore(log);
            store.Values[Key] = (JsonObject)JsonNode.Parse("""{"enabled":true,"checks":{"title":0.98}}""")!;
            var evaluator = new FakeEvaluator();
            var simulator = new PolicySimulator(store, evaluator);
            var options = simulator.Options();
            Check.Equal("title", Str(options["presets"]![0]!["id"]));
            Check.Equal(1, options["rules"]!.AsArray().Count);

            static string Loose(string rest) => """{"draft":{"scope":"source","subject":"10","direction":"external","rule":{"enabled":true,"checks":{"title":0.95}}}""" + rest + "}";
            var pairs = simulator.Preview(Body(Loose(",\"pairs\":[[3,4],[1,2]]")));
            Check.Equal(DecisionPreview.WouldMerge, Str(pairs["pairs"]![0]!["effect"]));
            Check.Equal(DecisionPreview.Unchanged, Str(pairs["pairs"]![1]!["effect"]));
            Check.True(!pairs["pairs"]![0]!["before"]!["eligible"]!.GetValue<bool>() && pairs["pairs"]![0]!["after"]!["eligible"]!.GetValue<bool>(), "Before blocked, draft eligible");

            var page = simulator.Preview(Body(Loose(",\"sample\":{\"offset\":0,\"limit\":2}")));
            Check.Equal(2, page["counts"]!["evaluated"]!.GetValue<int>());
            Check.Equal(2, page["next_offset"]!.GetValue<int>());

            var strict = simulator.Preview(Body("""{"draft":{"scope":"source","subject":"10","direction":"external","preset":"title"},"sample":{}}"""));
            Check.Equal(4, strict["counts"]!["evaluated"]!.GetValue<int>());
            Check.Equal(1, strict["counts"]![DecisionPreview.WouldSplit]!.GetValue<int>());
            Check.Equal("7,8", Pair(strict["pairs"]!.AsArray().Single(row => Str(row!["effect"]) == DecisionPreview.WouldSplit)!["pair"]));

            Check.Equal(400, Check.Throws<ReviewException>(() => simulator.Preview(Body("""{"draft":{"scope":"source","subject":"10","direction":"external","rule":{"enabled":true,"checks":{"title":0.5}}},"pairs":[[1,2]]}"""))).StatusCode);
            Check.Equal(409, Check.Throws<ReviewException>(() => simulator.Preview(Body("""{"draft":{"scope":"source","subject":"99","direction":"external","rule":null},"pairs":[[1,2]]}"""))).StatusCode);
            Check.Equal(409, Check.Throws<ReviewException>(() => simulator.Preview(Body(Loose(",\"pairs\":[[3,99]]")))).StatusCode);
            Check.Equal(503, Check.Throws<ReviewException>(() => simulator.Preview(Body(Loose(",\"scan\":true")))).StatusCode);

            var apply = Body(Loose(",\"pairs\":[[3,4],[1,2]],\"revision\":\"" + Str(pairs["revision"]) + "\""));
            var saved = simulator.Apply(apply, Alice);
            Check.True(saved["applied"]!.GetValue<bool>(), "Policy applied");
            Check.Equal(0.95, store.Values[Key]!["checks"]!["title"]!.GetValue<double>());
            var written = log.Rows.Single(row => row.Decision.Kind == "policy.write");
            Check.Equal(Alice, written.Decision.Actor);
            Check.Equal(written.Id, saved["decision"]!.GetValue<long>());
            Check.Equal(409, Check.Throws<ReviewException>(() => simulator.Apply(apply, Alice)).StatusCode);

            var queue = new JobQueue(new TaskRegistry());
            try
            {
                var scanning = new PolicySimulator(store, evaluator, queue);
                var started = scanning.Preview(Body("""{"draft":{"scope":"source","subject":"10","direction":"external","preset":"title"},"scan":true}"""));
                var id = Str(started["simulation"]);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                JsonObject result;
                while ((result = scanning.Preview(Body("{\"simulation\":\"" + id + "\"}")))["counts"] is null)
                {
                    if (DateTime.UtcNow > deadline || Str(result["state"]) == "failed") throw new InvalidOperationException("Scan did not finish: " + result.ToJsonString());
                    Thread.Sleep(10);
                }
                Check.Equal(4, result["counts"]!["evaluated"]!.GetValue<int>());
                Check.Equal(1, result["counts"]![DecisionPreview.WouldSplit]!.GetValue<int>());
                var done = scanning.Apply(Body("{\"simulation\":\"" + id + "\",\"revision\":\"" + Str(result["revision"]) + "\"}"), Script);
                Check.True(done["applied"]!.GetValue<bool>(), "Scanned policy applied");
                Check.Equal(0.97, store.Values[Key]!["checks"]!["title"]!.GetValue<double>());
            }
            finally { queue.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)); }
        });
    }

    private static readonly MatchingRuleKey Key = new("source", "10", "external");

    private static (MemoryDecisionLog Log, FakeReviewActions Actions, ReviewWorkbench Workbench) Fixture()
    {
        var log = new MemoryDecisionLog();
        var actions = new FakeReviewActions(log);
        return (log, actions, new ReviewWorkbench(actions, log, log));
    }

    private static JsonObject Body(string text) => JsonNode.Parse(text)!.AsObject();
    private static string Str(JsonNode? node) => node!.GetValue<string>();
    private static string Pair(JsonNode? node) => string.Join(",", node!.AsArray().Select(item => item!.GetValue<long>()));

    /// <summary>In-memory decision log (write and read side).</summary>
    internal sealed class MemoryDecisionLog : IDecisionLog, IReviewHistory
    {
        public List<RecordedDecision> Rows { get; } = [];
        public long Record(DecisionEvent decision)
        {
            var row = new RecordedDecision(Rows.Count + 1, DateTimeOffset.UtcNow, decision);
            Rows.Add(row);
            return row.Id;
        }
        public RecordedDecision? Find(long id) => Rows.FirstOrDefault(row => row.Id == id);
        public long? UndoneBy(long id) => Rows.LastOrDefault(row => row.Decision.UndoOf == id.ToString(CultureInfo.InvariantCulture))?.Id;
        public IReadOnlyList<RecordedDecision> Recent(long? entryId, string? kind, int limit) => Rows.AsEnumerable().Reverse()
            .Where(row => (entryId is null || row.Decision.Entries.Contains(entryId.Value)) && (kind is null || row.Decision.Kind == kind)).Take(limit).ToList();
    }

    /// <summary>A tiny catalogue: entries with records, merges, pair reviews and titles; every write is logged.</summary>
    internal sealed class FakeReviewActions(MemoryDecisionLog log) : IReviewActions
    {
        private sealed class Item { public long MergedInto { get; set; } public string Title { get; set; } = ""; public bool Manual { get; set; } public SortedSet<string> Records { get; } = new(StringComparer.Ordinal); }
        private readonly Dictionary<long, Item> _entries = [];
        private readonly Dictionary<(long, long), string> _reviews = [];
        private long _next = 100;
        public List<ReviewContext> Calls { get; } = [];

        public void Add(long id, string title, params string[] records)
        {
            var item = new Item { Title = title };
            foreach (var record in records) item.Records.Add(record);
            _entries[id] = item;
        }

        private long Canonical(long id) { while (_entries[id].MergedInto != 0) id = _entries[id].MergedInto; return id; }

        public ReviewEntrySnapshot? Entry(long entryId)
        {
            if (!_entries.ContainsKey(entryId)) return null;
            var canonical = Canonical(entryId);
            var shown = _entries[canonical];
            return new ReviewEntrySnapshot(entryId, canonical, shown.Title, shown.Manual, "2026-01-01T00:00:00Z", shown.Records.ToList());
        }
        public string? PairDecision(long left, long right) => _reviews.GetValueOrDefault((left, right));
        public long? RecordEntry(string record) => _entries.Where(pair => pair.Value.MergedInto == 0 && pair.Value.Records.Contains(record)).Select(pair => (long?)pair.Key).FirstOrDefault();

        private long Log(string kind, ReviewContext context, long[] entries, string[] records)
        {
            Calls.Add(context);
            return log.Record(new DecisionEvent(kind, context.Actor, entries, records, context.Reason, UndoOf: context.UndoOf));
        }

        public long ReviewPair(long left, long right, string? decision, ReviewContext context)
        {
            if (decision is null) _reviews.Remove((left, right)); else _reviews[(left, right)] = decision;
            return Log("review.duplicate", context, [left, right], []);
        }
        public long Merge(long target, long source, ReviewContext context)
        {
            var records = _entries[source].Records.ToArray();
            foreach (var record in records) _entries[target].Records.Add(record);
            _entries[source].Records.Clear();
            _entries[source].MergedInto = target;
            return Log("merge.manual", context, [target, source], records);
        }
        public ReviewSplitResult Split(long entryId, IReadOnlyList<string> records, ReviewContext context)
        {
            var id = ++_next;
            Add(id, _entries[entryId].Title + " (split)", [.. records]);
            foreach (var record in records) _entries[entryId].Records.Remove(record);
            return new ReviewSplitResult(Log("split", context, [entryId, id], [.. records]), id);
        }
        public long ManualMatch(long entryId, string record, ReviewContext context)
        {
            if (RecordEntry(record) is { } holder) _entries[holder].Records.Remove(record);
            _entries[entryId].Records.Add(record);
            return Log("manual.match", context, [entryId], [record]);
        }
        public long ClearManualMatch(long entryId, string record, ReviewContext context)
        {
            _entries[entryId].Records.Remove(record);
            Add(++_next, record, record);
            return Log("manual.clear", context, [entryId], [record]);
        }
        public long SetPreferredTitle(long entryId, string? title, ReviewContext context)
        {
            _entries[entryId].Title = title ?? "Automatic " + entryId.ToString(CultureInfo.InvariantCulture);
            _entries[entryId].Manual = title is not null;
            return Log("title.preferred", context, [entryId], []);
        }
    }

    private sealed class FakePolicyStore(MemoryDecisionLog log) : IPolicyStore
    {
        public Dictionary<MatchingRuleKey, JsonObject?> Values { get; } = [];
        public IReadOnlyDictionary<MatchingRuleKey, JsonObject?> Rules() => Values.ToDictionary(pair => pair.Key, pair => (JsonObject?)pair.Value?.DeepClone());
        public bool SubjectExists(MatchingRuleKey key) => key.Scope == "source" && key.Subject == "10";
        public PolicyOptions Options() => new(
            (JsonArray)JsonNode.Parse("""[{"source_id":"10","name":"Ten","lang":"en"}]""")!,
            new JsonArray(),
            (JsonArray)JsonNode.Parse("""[{"id":"title","name":"High title similarity","rule":{"enabled":true,"checks":{"title":0.97}}}]""")!);
        public long Write(MatchingRuleKey key, JsonObject? rule, string expectedRevision, DecisionActor actor, string reason)
        {
            if (MatchingRuleConfiguration.Revision(Rules()) != expectedRevision) throw new MatchingRuleConflictException();
            if (rule is null) Values.Remove(key); else Values[key] = rule;
            return log.Record(new DecisionEvent("policy.write", actor, [], [], reason));
        }
    }

    /// <summary>Pairs with a title score; eligible when the effective rule's title threshold (0.94 floor) is met.</summary>
    private sealed class FakeEvaluator : IPolicyEvaluator
    {
        private static readonly (CandidatePair Pair, double Score)[] Catalogue =
            [(new(1, 2), .99), (new(3, 4), .95), (new(5, 6), .90), (new(7, 8, Merged: true), .96)];
        public bool IsCanonical(long entryId) => entryId is >= 1 and <= 8;
        public PairEvidence Evaluate(CandidatePair pair, IReadOnlyDictionary<MatchingRuleKey, JsonObject?> rules)
        {
            var score = Catalogue.Single(row => row.Pair.Left == pair.Left && row.Pair.Right == pair.Right).Score;
            var rule = rules.GetValueOrDefault(Key);
            var threshold = rule?["checks"]?["title"]?.GetValue<double>() ?? .94;
            var enabled = rule?["enabled"]?.GetValue<bool>() ?? true;
            var eligible = enabled && score >= threshold;
            return new PairEvidence(eligible, eligible ? "Title " + score.ToString(CultureInfo.InvariantCulture) : "Title below " + threshold.ToString(CultureInfo.InvariantCulture));
        }
        public IReadOnlyList<CandidatePair> Candidates(MatchingRuleKey key, int offset, int limit) => Catalogue.Select(row => row.Pair).Skip(offset).Take(limit).ToList();
    }
}
