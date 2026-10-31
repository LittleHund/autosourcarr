using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Privacy;

namespace AutoSourcarr.Core.Diagnostics;

/// <summary>Observed summaries only; downloading diagnostics never contacts a repair worker.</summary>
public sealed class RepairDiagnosticCache(SupportPrivacy privacy, Action<string, string> record)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonObject> _jobs = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = [];
    private JsonObject? _status;
    private JsonObject? _operation;
    private JsonObject? _account;
    private static readonly HashSet<string> ControlReasons = ["unclassified", "device-login-disabled", "device-endpoint-unavailable", "rate-limited", "request-denied", "service-unavailable", "http-error", "tls-error", "dns-error", "network-error", "invalid-response", "tool-startup", "resource-limit", "configuration-error", "timed-out", "process-signal", "no-code-timeout", "runtime-error", "credential-save"];
    private static readonly HashSet<string> ResourceLimits = ["nproc_soft", "nproc_hard", "address_space_soft_bytes", "address_space_hard_bytes", "open_files_soft", "open_files_hard", "cgroup_pids_max", "cgroup_memory_max_bytes"];
    private static readonly string[] CurrentResources = ["cgroup_pids_current", "cgroup_memory_current_bytes"];

    public void ObserveRepairs(JsonNode? value)
    {
        if (value is not JsonObject source) return;
        var many = source["jobs"] as JsonArray;
        IEnumerable<JsonNode?> rows = many is null ? new[] { source } : many.Take(50).Reverse();
        var changed = new List<JsonObject>();
        foreach (var row in rows)
        {
            var safe = RepairEvidence.Job(row, privacy); if (safe is null) continue;
            var id = D.Text(safe, "id");
            lock (_gate)
            {
                var previous = _jobs.GetValueOrDefault(id); _jobs[id] = safe; _order.Remove(id); _order.AddLast(id);
                while (_jobs.Count > 50) { var oldest = _order.First!.Value; _order.RemoveFirst(); _jobs.Remove(oldest); }
                if (!JsonNode.DeepEquals(previous, safe)) changed.Add(safe);
            }
        }
        if (many is not null)
        {
            var active = source["active"];
            var safe = new JsonObject { ["captured_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["active"] = active is null && source.ContainsKey("active") ? null : D.Text(active) is { } id && Regex.IsMatch(id, "\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant) ? JsonValue.Create(id) : JsonValue.Create("unknown"),
                ["jobs_omitted_from_response"] = Math.Max(0, many.Count - 50), ["isolation"] = RepairEvidence.Isolation(source["isolation"], privacy) };
            lock (_gate) _status = safe;
        }
        foreach (var row in changed)
        {
            var fields = new JsonObject();
            foreach (var key in new[] { "id", "pkg_name", "state", "message", "baseline_issue" }) if (row.ContainsKey(key)) fields[key] = row[key]?.DeepClone();
            record("repair-job", fields.ToJsonString());
        }
    }
    public void ObserveOperation(JsonNode? value)
    {
        var safe = RepairEvidence.Operation(value, privacy); if (safe is null) return;
        bool changed; lock (_gate) { changed = !JsonNode.DeepEquals(_operation, safe); _operation = safe; }
        if (changed) record("orphan-repair", safe.ToJsonString());
    }
    public void ObserveAccount(JsonNode? value)
    {
        if (value is not JsonObject source) return;
        privacy.Register(source.ToDictionary(pair => pair.Key, pair => pair.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null, StringComparer.Ordinal));
        var isolation = RepairEvidence.Isolation(source["isolation"], privacy);
        if (isolation is not null)
        {
            lock (_gate)
            {
                _status ??= new() { ["captured_at"] = null, ["active"] = "unknown", ["jobs_omitted_from_response"] = 0 };
                _status["isolation"] = isolation; _status["isolation_captured_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            }
        }
        var safe = new JsonObject { ["state"] = RepairEvidence.Choice(source["state"], ["idle", "running", "ready", "failed", "completed", "cancelled", "cancelling"]),
            ["action"] = RepairEvidence.Choice(source["action"], ["prepare", "login", "login-status", "logout", "cancel-login", "retry-cleanup"]), ["busy"] = D.Flag(source, "busy") };
        if (source["diagnostics"] is JsonObject diagnostics)
        {
            var fields = new JsonObject { ["reason"] = RepairEvidence.Choice(diagnostics["reason"], ControlReasons, "unclassified"),
                ["phase"] = RepairEvidence.Choice(diagnostics["phase"], ["starting", "requesting-code", "authorizing", "checking-account", "signing-out", "saving-account"]) };
            foreach (var key in new[] { "exit_code", "http_status" })
            {
                if (diagnostics[key] is null) fields[key] = null;
                else if (RepairEvidence.Integer(diagnostics, key, -255, 599) is { } number) fields[key] = number;
            }
            if (diagnostics["code_received"] is JsonValue received && received.TryGetValue<bool>(out var flag)) fields["code_received"] = flag;
            if (D.Text(diagnostics, "launcher_stage") is "exec" or "parent") fields["launcher_stage"] = diagnostics["launcher_stage"]!.DeepClone();
            if (RepairEvidence.Integer(diagnostics, "os_error", 0, 4095) is { } osError) fields["os_error"] = osError;
            if (AccountResources(diagnostics["resources"]) is { } resources) fields["resources"] = resources;
            safe["diagnostics"] = fields;
        }
        bool changed; lock (_gate) { changed = !JsonNode.DeepEquals(_account, safe); _account = safe; }
        if (changed) record("repair-account", safe.ToJsonString());
    }

    public (JsonObject Repairs, JsonNode? Account) Snapshot()
    {
        lock (_gate) return (new JsonObject { ["status"] = _status?.DeepClone(), ["jobs"] = new JsonArray(_order.Reverse().Select(id => (JsonNode?)_jobs[id].DeepClone()).ToArray()),
            ["orphan_operation"] = _operation?.DeepClone(), ["job_limit"] = 50,
            ["scope"] = "Cached observed helper responses and local preparation only; no export-time probes. Private prompts, patches, conversations and package bodies are excluded." }, _account?.DeepClone());
    }
    private static JsonObject? AccountResources(JsonNode? value)
    {
        if (value is not JsonObject source) return null;
        var result = new JsonObject();
        foreach (var key in ResourceLimits.Concat(CurrentResources))
        {
            var text = D.Text(source, key);
            if (Regex.IsMatch(text, "\\A(?:0|[1-9][0-9]{0,19})\\z", RegexOptions.CultureInvariant) && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || text == "unknown" || text == "unlimited" && ResourceLimits.Contains(key)) result[key] = text;
        }
        if (D.Text(source, "cgroup_scope") is "unknown" or "visible-v2-only") result["cgroup_scope"] = source["cgroup_scope"]!.DeepClone();
        if (D.Text(source, "ancestor_limits") == "unknown") result["ancestor_limits"] = "unknown";
        return result.Count > 0 ? result : null;
    }
}
