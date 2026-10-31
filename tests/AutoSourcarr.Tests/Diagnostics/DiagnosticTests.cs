using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Privacy;

namespace AutoSourcarr.Tests;

public static class DiagnosticTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("diagnostics: released grouping retains original members, status and exact presentation keys", () =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ConfigurationTests.Fixture("diagnostic-groups.json")));
            foreach (var row in document.RootElement.EnumerateArray())
            {
                var input = JsonNode.Parse(row.GetProperty("items").GetRawText())!.AsArray().OfType<JsonObject>().ToArray();
                Check.JsonEqual(row.GetProperty("expected"), JsonSerializer.SerializeToElement(DiagnosticGroups.Group(input)));
            }
        });
        suite.Add("diagnostics: deduplicated errors reopen, retain occurrence count and resolve independently", () =>
        {
            var registry = new ErrorRegistry(RuntimeIdentity.Create("fixture"), new SupportPrivacy());
            var id = registry.Record("Source", "failed", "source:key");
            Check.Equal(id, registry.Record("Source", "failed again", "source:key"));
            Check.Equal(1, registry.Snapshot()["active_count"]!.GetValue<int>());
            Check.Equal(2L, registry.Snapshot()["items"]![0]!["count"]!.GetValue<long>());
            Check.Equal(1, registry.Resolve(key: "source:key"));
            Check.Equal(0, registry.Snapshot()["active_count"]!.GetValue<int>());
            Check.Equal(id, registry.Record("Source", "reopened", "source:key"));
            Check.Equal(3L, registry.Snapshot()["items"]![0]!["count"]!.GetValue<long>());
            Check.Equal(0, registry.Clear()); Check.Equal(1, registry.Clear(false));
        });
        suite.Add("diagnostics: retention prefers resolved then unpinned records", () =>
        {
            var registry = new ErrorRegistry(RuntimeIdentity.Create("fixture"), new SupportPrivacy(), 20);
            var pinned = registry.Record("Source", "pinned", "pinned", pinned: true);
            for (var i = 0; i < 19; i++) registry.Record("Source", "failure " + i, "error:" + i);
            registry.Resolve(key: "error:10"); registry.Record("Source", "replacement", "replacement");
            var snapshot = registry.Snapshot(); Check.Equal(20, snapshot["history_count"]!.GetValue<int>());
            Check.Equal(pinned, snapshot["items"]![0]!["id"]!.GetValue<string>());
            Check.True(snapshot["items"]!.AsArray().All(row => row!["key"]!.GetValue<string>() != "error:10"));
        });
        suite.Add("diagnostics: memory, log callbacks and bounded exception chains are redacted", () =>
        {
            var privacy = new SupportPrivacy(); privacy.Register(new Dictionary<string, string?> { ["api_key"] = "sentinel-private" });
            var logs = new List<JsonObject>(); var registry = new ErrorRegistry(RuntimeIdentity.Create("fixture"), privacy, recordLog: logs.Add);
            Exception exception = new InvalidOperationException("sentinel-private");
            for (var i = 0; i < 12; i++) exception = new IOException("password=sentinel-private", exception);
            registry.Record("App", "failed sentinel-private", key: "unhandled|GET|/api/v1/fail|type", detail: "Authorization: Bearer sentinel-private", exception: exception,
                context: new() { ["cookie"] = "sentinel-private" }, sourceContext: new() { ["source_id"] = "-1", ["cookie"] = "sentinel-private" });
            var snapshot = registry.Snapshot(); var all = snapshot.ToJsonString() + JsonSerializer.Serialize(logs);
            Check.True(!all.Contains("sentinel-private", StringComparison.Ordinal));
            Check.Equal(6, snapshot["items"]![0]!["exception_chain"]!.AsArray().Count);
            Check.Equal(1, registry.ResolvePrefix("unhandled|GET|/api/v1/fail|"));
        });
    }
}
