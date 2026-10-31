using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Tests;

public static class RepairHistoryTests
{
    private const string Package = "eu.kanade.tachiyomi.extension.en.example";
    private static readonly string JobA = new('a', 32);
    private static readonly string JobB = new('b', 32);
    public static void Register(TestSuite suite)
    {
        suite.Add("repair history: released install/Fixed/Retry/Revert and recovery transition bytes", () =>
        {
            var cases = JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("repair-history.json")))!.AsArray(); var transitions = 0;
            foreach (var item in cases)
            {
                using var temp = new Sandbox(); var history = new RepairHistory(new PrivateDocumentStore(temp.Path), () => 1000.0);
                foreach (var step in item!["steps"]!.AsArray())
                {
                    var args = step!["args"]!.AsArray(); var kwargs = step["kwargs"]!.AsObject();
                    string S(int index) => args[index]!.GetValue<string>();
                    JsonObject? O(int index) => args[index] as JsonObject;
                    var revision = kwargs["expected_revision"]?.GetValue<long>() ?? 0; var observed = kwargs["observed_sha256"]?.GetValue<string>();
                    var operation = step["operation"]!.GetValue<string>();
                    var actual = operation switch
                    {
                        "admit" => history.Admit(S(0), S(1), O(2)),
                        "plan_install" => history.PlanInstall(S(0), S(1), O(2)!, revision),
                        "commit" => history.Commit(O(0)!, observed),
                        "confirm" => history.Confirm(S(0), S(1), revision, observed),
                        "plan_revert" => history.PlanRevert(S(0), S(1), revision, observed),
                        "report_failed" => history.ReportFailed(S(0), S(1), revision, observed, kwargs["feedback"]?.GetValue<string>() ?? ""),
                        "finish" => history.Finish(S(0), S(1), S(2)),
                        "bind_baseline_code" => history.BindBaselineCode(S(0), S(1), S(2)),
                        "restore_before" => history.RestoreBefore(O(0)!, observed),
                        "snapshot" => history.Snapshot(S(0)),
                        _ => throw new InvalidOperationException("Unrecognized released history operation")
                    };
                    Check.JsonEqual(JsonSerializer.SerializeToElement(step["expected"]), JsonSerializer.SerializeToElement(actual));
                    Check.Equal(step["canonical"]!.GetValue<string>(), CanonicalJson.Text(actual)); transitions++;
                }
            }
            Console.WriteLine("Repair history transition parity cases " + transitions);
        });
        suite.Add("repair history: changed identities, stale revisions and wrong binaries cannot promote or confirm", () =>
        {
            using var temp = new Sandbox(); var history = new RepairHistory(new PrivateDocumentStore(temp.Path), () => 1000.0);
            var started = history.Admit(Package, JobA, Reference('1', 'a'));
            foreach (var (field, value) in new (string, JsonNode)[] { ("extension_lib", JsonValue.Create("1.6")!), ("entrypoint", JsonValue.Create(Package + ".Other")!),
                ("source_ids", new JsonArray("2")), ("identity_sha256", JsonValue.Create(new string('8', 64))!) })
            {
                var candidate = Reference('2', 'b'); candidate[field] = value;
                Check.Throws<RepairHistoryConflictException>(() => history.PlanInstall(Package, JobA, candidate, Revision(started)));
                Check.JsonEqual(JsonSerializer.SerializeToElement(started), JsonSerializer.SerializeToElement(history.Snapshot(Package)));
            }
            var plan = history.PlanInstall(Package, JobA, Reference('2', 'b'), Revision(started)); var state = history.Commit(plan, new string('2', 64));
            Check.Throws<RepairHistoryConflictException>(() => history.Confirm(Package, JobA, 0, new string('2', 64)));
            Check.Throws<RepairHistoryConflictException>(() => history.Confirm(Package, JobB, Revision(state), new string('2', 64)));
            Check.Throws<RepairHistoryConflictException>(() => history.Confirm(Package, JobA, Revision(state), new string('3', 64)));
            var tampered = (JsonObject)plan.DeepClone(); tampered["target"]!["raw_id"] = new string('c', 64);
            Check.Throws<RepairHistoryConflictException>(() => history.Commit(tampered, new string('2', 64)));
        });
        suite.Add("repair history: original JSON plans reopen and corruption cannot reset instance state", () =>
        {
            using var first = new Sandbox(); using var second = new Sandbox();
            var fixture = JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("repair-history.json")))!.AsArray()[0]!;
            var plan = fixture["steps"]!.AsArray().First(step => step!["operation"]!.GetValue<string>() == "plan_install")!["expected"]!.AsObject();
            var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Package))) + ".json";
            File.WriteAllBytes(Path.Combine(first.Path, name), CanonicalJson.Bytes(plan["before"]));
            var history = new RepairHistory(new PrivateDocumentStore(first.Path), () => 1000.0);
            var installed = history.Commit(plan, new string('2', 64)); var reopened = new RepairHistory(new PrivateDocumentStore(first.Path), () => 1000.0);
            Check.JsonEqual(JsonSerializer.SerializeToElement(installed), JsonSerializer.SerializeToElement(reopened.Snapshot(Package)));
            Check.True(new RepairHistory(new PrivateDocumentStore(second.Path)).Snapshot(Package)["current"] is null);
            File.WriteAllText(Path.Combine(first.Path, name), "{CORRUPTED");
            Check.Throws<RepairHistoryConflictException>(() => reopened.Snapshot(Package)); Check.Equal("{CORRUPTED", File.ReadAllText(Path.Combine(first.Path, name)));
            File.WriteAllBytes(Path.Combine(first.Path, name), new byte[RepairHistory.MaximumHistoryBytes + 1]);
            Check.Throws<RepairHistoryConflictException>(() => reopened.Snapshot(Package));
            Check.Equal(RepairHistory.MaximumHistoryBytes + 1L, new FileInfo(Path.Combine(first.Path, name)).Length);
        });
        suite.Add("repair history: absent baselines are idempotent and invalid optional hashes are refused", () =>
        {
            using var temp = new Sandbox(); var history = new RepairHistory(new PrivateDocumentStore(temp.Path), () => 1000.0);
            var started = history.Admit(Package, JobA, null); Check.JsonEqual(JsonSerializer.SerializeToElement(started), JsonSerializer.SerializeToElement(history.Admit(Package, JobA, null)));
            Check.Throws<RepairHistoryConflictException>(() => history.Admit(Package, JobB, null)); history.Finish(Package, JobA, "cancelled");
            var bad = Reference('1', 'a'); bad["raw_id"] = false;
            Check.Throws<RepairHistoryConflictException>(() => history.Admit(Package, JobB, bad));
            bad = Reference('1', 'a'); bad["path"] = "/config/secret";
            Check.Throws<RepairHistoryConflictException>(() => history.Admit(Package, JobB, bad));
        });
    }
    private static long Revision(JsonObject state) => state["revision"]!.GetValue<long>();
    private static JsonObject Reference(char binary, char raw) => new() { ["binary_sha256"] = new string(binary, 64), ["raw_id"] = new string(raw, 64), ["version"] = "1.4.23",
        ["entrypoint"] = Package + ".Example", ["extension_lib"] = "1.4", ["source_ids"] = new JsonArray("-9223372036854775808", "1"), ["identity_sha256"] = new string('9', 64) };
}
