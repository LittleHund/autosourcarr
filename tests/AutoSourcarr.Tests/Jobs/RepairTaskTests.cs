using System.Text.Json.Nodes;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Jobs;

namespace AutoSourcarr.Tests;

public static class RepairTaskTests
{
    private static readonly string JobA = new('a', 32);
    private static readonly string JobB = new('b', 32);
    private static readonly string JobC = new('c', 32);
    private static readonly string JobD = new('d', 32);
    public static void Register(TestSuite suite)
    {
        suite.Add("repair tasks: queued, code-required, ready and blocked jobs occupy their actual groups", () =>
        {
            var projection = new RepairTaskProjection(new SupportPrivacy(), () => 100);
            projection.Observe(new JsonObject { ["jobs"] = new JsonArray(Job(JobA, "queued"), Job(JobB, "awaiting-raws"), Job(JobC, "ready"), Job(JobD, "blocked")), ["active"] = JobA });
            var view = projection.Merge(new TaskRegistry().Snapshot(), new(), Empty());
            Check.Equal(2, view["running"]!.GetValue<int>()); Check.Equal(1, view["queued"]!.GetValue<int>()); Check.Equal(1, view["waiting"]!.GetValue<int>());
            Check.Equal(1, view["completed"]!.GetValue<int>()); Check.Equal(1, view["failed"]!.GetValue<int>());
            var code = view["items"]!.AsArray().Single(row => row!["repair_id"]!.GetValue<string>() == JobB)!;
            Check.Equal("eu.kanade.example", code["raw_package"]!.GetValue<string>()); Check.Equal("waiting", code["activity"]!.GetValue<string>());
            Check.True(view["items"]!.AsArray().Single(row => row!["repair_id"]!.GetValue<string>() == JobC)!["repair_reviewable"]!.GetValue<bool>());
        });
        suite.Add("repair tasks: clearing history retains reviewable and current versions and new attempts reappear", () =>
        {
            var projection = new RepairTaskProjection(new SupportPrivacy(), () => 100);
            projection.Observe(new JsonObject { ["jobs"] = new JsonArray(Job(JobA, "blocked"), Job(JobB, "ready")) });
            var result = projection.ClearHistory(new(), Empty()); Check.Equal(1, result["failed"]!.GetValue<int>()); Check.Equal(1, result["kept_for_review"]!.GetValue<int>());
            Check.Equal(1, projection.Merge(new TaskRegistry().Snapshot(), new(), Empty())["items"]!.AsArray().Count);
            var changed = Job(JobA, "failed"); changed["updated_at"] = 101; projection.Observe(changed);
            Check.Equal(2, projection.Merge(new TaskRegistry().Snapshot(), new(), Empty())["items"]!.AsArray().Count);
        });
        suite.Add("repair tasks: old full snapshots cannot erase a newly created or newer job", () =>
        {
            var projection = new RepairTaskProjection(new SupportPrivacy(), () => 100);
            var newer = Job(JobA, "running"); newer["created_at"] = 100; newer["updated_at"] = 101; projection.Observe(newer);
            projection.Observe(new JsonObject { ["jobs"] = new JsonArray(), ["captured_at"] = 90 }); Check.Equal(1, projection.Jobs().Count);
            projection.Observe(new JsonObject { ["jobs"] = new JsonArray(Job(JobA, "queued")), ["captured_at"] = 110 });
            Check.Equal("running", projection.Jobs()[0]!["state"]!.GetValue<string>());
            projection.Observe(new JsonObject { ["jobs"] = new JsonArray(), ["captured_at"] = 110 }); Check.Equal(0, projection.Jobs().Count);
        });
        suite.Add("repair tasks: confirmation, pending retry and revert remain distinct from installation", () =>
        {
            var projection = new RepairTaskProjection(new SupportPrivacy(), () => 100); projection.Observe(new JsonObject { ["jobs"] = new JsonArray(Job(JobA, "installed")) });
            var current = new JsonObject { ["job_id"] = JobA, ["status"] = "confirmed", ["binary_sha256"] = new string('a', 64), ["installed_at"] = 100 };
            var history = new JsonObject { ["revision"] = 2, ["current"] = current, ["can_revert"] = true, ["working_at"] = 120, ["pending"] = null, ["jobs"] = new JsonObject() };
            var supplement = new RepairTaskSupplement(new JsonObject { ["eu.kanade.example"] = history }, [], []);
            var item = projection.Merge(new TaskRegistry().Snapshot(), new(), supplement)["items"]![0]!;
            Check.Equal("confirmed", item["repair_state"]!.GetValue<string>()); Check.True(item["repair_can_feedback"]!.GetValue<bool>());
            history["pending"] = JobB;
            item = projection.Merge(new TaskRegistry().Snapshot(), new(), supplement)["items"]![0]!; Check.True(!item["repair_can_feedback"]!.GetValue<bool>());
            current["status"] = "reported-not-working";
            Check.Equal("failed", projection.Merge(new TaskRegistry().Snapshot(), new(), supplement)["items"]![0]!["state"]!.GetValue<string>());
            current["job_id"] = JobB; history["jobs"]![JobA] = "reverted";
            item = projection.Merge(new TaskRegistry().Snapshot(), new(), supplement)["items"]!.AsArray().Single(row => row!["repair_id"]!.GetValue<string>() == JobA)!;
            Check.Equal("reverted", item["repair_state"]!.GetValue<string>()); Check.Equal("completed", item["state"]!.GetValue<string>());
        });
        suite.Add("repair tasks: stale work waits and duplicate source-code requests stay single", () =>
        {
            double now = 100; var projection = new RepairTaskProjection(new SupportPrivacy(), () => now);
            projection.Observe(new JsonObject { ["jobs"] = new JsonArray(Job(JobA, "awaiting-raws")) });
            var pending = new JsonObject { ["id"] = "raws:one", ["kind"] = "source-repair", ["raw_package"] = "eu.kanade.example", ["state"] = "running", ["activity"] = "waiting" };
            var supplement = new RepairTaskSupplement(new(), [], new JsonArray(pending));
            Check.Equal(1, projection.Merge(new TaskRegistry().Snapshot(), new(), supplement)["items"]!.AsArray().Count);
            now += 21;
            var item = projection.Merge(new TaskRegistry().Snapshot(), new(), supplement)["items"]![0]!;
            Check.True(item["stale"]!.GetValue<bool>()); Check.True(!item["cancellable"]!.GetValue<bool>());
            Check.True(RepairTaskProjection.IsolationBlocker(new JsonObject { ["isolation"] = new JsonObject { ["available"] = false, ["reason"] = "not-checked" } }) is null);
            Check.Equal("blocked", RepairTaskProjection.IsolationBlocker(new JsonObject { ["isolation"] = new JsonObject { ["available"] = false, ["reason"] = "mount-denied", ["message"] = "blocked" } }));
        });
    }
    private static RepairTaskSupplement Empty() => new(new(), [], []);
    private static JsonObject Job(string id, string state) => new() { ["id"] = id, ["pkg_name"] = "eu.kanade.example", ["state"] = state, ["created_at"] = 1, ["updated_at"] = 2, ["version"] = "0.4.3", ["message"] = state };
}
