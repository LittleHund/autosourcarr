using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Privacy;

namespace AutoSourcarr.Core.Jobs;

public sealed record RepairTaskSupplement(JsonObject Histories, JsonArray PendingWorkflow, JsonArray PendingRaws);

/// <summary>Tasks use observed summaries; rendering never launches tools or probes a sandbox.</summary>
public sealed class RepairTaskProjection(SupportPrivacy privacy, Func<double>? monotonic = null)
{
    private static readonly HashSet<string> Terminal = ["ready", "installed", "rolled-back", "blocked", "failed", "cancelled", "interrupted", "discarded", "raws-retained", "confirmed", "reported-not-working", "reverted"];
    private static readonly HashSet<string> Failed = ["blocked", "failed", "cancelled", "interrupted", "recovery-needed", "rollback-failed", "reported-not-working"];
    private readonly object _gate = new();
    private readonly Func<double> _clock = monotonic ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    private Dictionary<string, JsonObject> _jobs = new(StringComparer.Ordinal);
    private Dictionary<string, Signature> _hidden = new(StringComparer.Ordinal);
    private bool? _reachable;
    private double _observed;
    private string _captured = "";
    private JsonNode? _isolation;
    private readonly record struct Signature(string State, double? Updated, string Version);

    public void Observe(JsonNode? value)
    {
        if (value is not JsonObject source) return;
        lock (_gate)
        {
            var rows = source["jobs"] as JsonArray; var full = rows is not null;
            var previous = _jobs;
            if (full) _jobs = new(StringComparer.Ordinal);
            if (!full && !source.ContainsKey("id"))
            { if (source.ContainsKey("isolation")) _isolation = RepairEvidence.Isolation(source["isolation"], privacy); return; }
            foreach (var row in full ? rows!.Take(100) : new[] { source })
            {
                var item = RepairEvidence.Job(row, privacy); if (item is null) continue;
                var id = D.Text(item, "id");
                _jobs[id] = previous.TryGetValue(id, out var old) && Number(old, "updated_at") > Number(item, "updated_at") ? old : item;
            }
            if (full && Numeric(source["captured_at"]) is { } captured)
                foreach (var (id, item) in previous) if (Number(item, "created_at") > captured && !_jobs.ContainsKey(id)) _jobs[id] = item;
            _jobs = _jobs.OrderByDescending(pair => Number(pair.Value, "created_at")).Take(100).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            _hidden = _hidden.Where(pair => _jobs.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (source.ContainsKey("isolation")) _isolation = RepairEvidence.Isolation(source["isolation"], privacy);
            if (full) { _reachable = true; _observed = _clock(); _captured = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture); }
        }
    }
    public void Unavailable() { lock (_gate) _reachable = false; }
    public bool NeedsRefresh { get { lock (_gate) return _clock() - _observed >= 5; } }
    public JsonArray Jobs() { lock (_gate) return new(_jobs.Values.Select(row => (JsonNode?)row.DeepClone()).ToArray()); }

    public JsonObject Merge(JsonObject general, JsonObject operation, RepairTaskSupplement supplement)
    {
        lock (_gate)
        {
            var result = (JsonObject)general.DeepClone();
            var items = general["items"] is JsonArray existing ? existing.Select(value => value!.DeepClone()).ToList() : [];
            items.AddRange(Items(operation, supplement.Histories).Select(row => (JsonNode)row));
            items.AddRange(supplement.PendingWorkflow.Select(row => row!.DeepClone()));
            var waiting = items.OfType<JsonObject>().Where(row => D.Text(row, "repair_state") == "awaiting-raws").Select(row => D.Text(row, "context")).ToHashSet(StringComparer.Ordinal);
            items.AddRange(supplement.PendingRaws.OfType<JsonObject>().Where(row => !waiting.Contains(D.Text(row, "raw_package"))).Select(row => row.DeepClone()));
            var running = items.OfType<JsonObject>().Where(row => D.Text(row, "state") == "running").ToArray();
            result["items"] = new JsonArray(items.ToArray()); result["running"] = running.Length;
            result["active"] = running.Count(row => D.Text(row, "activity", "running") == "running");
            result["queued"] = running.Count(row => D.Text(row, "activity") == "queued"); result["waiting"] = running.Count(row => D.Text(row, "activity") == "waiting");
            result["completed"] = items.OfType<JsonObject>().Count(row => D.Text(row, "state") == "completed");
            result["failed"] = items.OfType<JsonObject>().Count(row => D.Text(row, "state") == "failed");
            result["source_repairs"] = new JsonObject { ["reachable"] = _reachable is { } known ? JsonValue.Create(known) : null, ["captured_at"] = _captured,
                ["stale"] = Stale(), ["isolation"] = _isolation?.DeepClone() };
            return result;
        }
    }
    public JsonObject DiagnosticSnapshot(JsonObject general, JsonObject operation, RepairTaskSupplement supplement)
    {
        var result = Merge(general, operation, supplement);
        result["items"] = new JsonArray(result["items"]!.AsArray().OfType<JsonObject>().OrderByDescending(row => D.Text(row, "state") == "running")
            .ThenByDescending(row => D.Text(row, "updated_at", D.Text(row, "created_at")), StringComparer.Ordinal).Select(row => row.DeepClone()).ToArray());
        return result;
    }
    public JsonObject ClearHistory(JsonObject operation, RepairTaskSupplement supplement)
    {
        lock (_gate)
        {
            var completed = 0; var failed = 0; var kept = 0;
            foreach (var item in Items(operation, supplement.Histories))
            {
                if (D.Flag(item, "repair_reviewable") || D.Flag(item, "repair_is_current")) kept++;
                else if (D.Text(item, "state") != "running")
                {
                    var id = D.Text(item, "repair_id"); var job = _jobs[id];
                    _hidden[id] = new(D.Text(item, "repair_state"), Numeric(job["updated_at"]), D.Text(job, "version"));
                    if (D.Text(item, "state") == "completed") completed++; else failed++;
                }
            }
            return new() { ["cleared"] = completed + failed, ["completed"] = completed, ["failed"] = failed, ["kept_for_review"] = kept };
        }
    }
    public static string? IsolationBlocker(JsonObject state)
    {
        var isolation = state["isolation"] as JsonObject;
        return isolation is not null && isolation["available"] is JsonValue available && available.TryGetValue<bool>(out var flag) && !flag
            && isolation["reason"] is not null && D.Text(isolation, "reason") != "not-checked" ? D.Text(isolation, "message", "Repair isolation is unavailable") : null;
    }

    private List<JsonObject> Items(JsonObject operation, JsonObject histories)
    {
        var stale = Stale(); var items = new List<JsonObject>(); var jobs = _jobs.Values.Select(row => (JsonObject)row.DeepClone()).ToList();
        foreach (var (package, value) in histories)
        {
            if (value is not JsonObject history || history["current"] is not JsonObject current || D.Text(current, "job_id") is not { Length: > 0 } id || _jobs.ContainsKey(id)) continue;
            jobs.Add(new() { ["id"] = id, ["pkg_name"] = package, ["state"] = "installed", ["version"] = "unknown", ["raw_first"] = true,
                ["created_at"] = current["installed_at"]?.DeepClone() ?? JsonValue.Create(0), ["updated_at"] = current["installed_at"]?.DeepClone() ?? JsonValue.Create(0), ["origin"] = "manual" });
        }
        foreach (var job in jobs)
        {
            var stage = D.Text(job, "state"); var message = D.Text(job, "message"); var id = D.Text(job, "id"); var package = D.Text(job, "pkg_name");
            var history = histories[package] as JsonObject ?? new(); var current = history["current"] as JsonObject ?? new();
            if ((history["jobs"] as JsonObject)?[id] is JsonValue status && status.TryGetValue<string>(out var previous) && previous == "reverted")
            { stage = "reverted"; message = "Repair reverted. The prior binary, code reference and working confirmation were restored."; }
            var isCurrent = D.Text(current, "job_id") == id;
            if (isCurrent)
            {
                stage = D.Text(current, "status") is "confirmed" or "reported-not-working" ? D.Text(current, "status") : "installed";
                message = stage switch { "confirmed" => "User confirmed this installed source works.", "reported-not-working" => "Reported as not working. Retry starts from this attempt’s retained code.", _ => "Repair installed; confirm Fixed or report the remaining problem." };
            }
            var changing = D.Text(operation, "id") == id;
            var changeState = D.Text(operation, "state");
            if (changing && stage is not ("confirmed" or "reported-not-working" or "reverted") && (changeState is "queued" or "installing" or "installed" or "recovered" or "rolled-back" || Failed.Contains(changeState)))
            {
                stage = changeState switch { "queued" => "install-queued", "recovered" => "installed", _ => changeState };
                message = Text.UnicodeCaseFold.Truncate(privacy.Text(D.Text(operation, "message", message)), 1200);
            }
            var state = Failed.Contains(stage) ? "failed" : Terminal.Contains(stage) ? "completed" : "running";
            var signature = new Signature(stage, Numeric(job["updated_at"]), D.Text(job, "version"));
            if (_hidden.GetValueOrDefault(id) == signature) continue;
            var activity = stage is "queued" or "install-queued" ? "queued" : "running";
            if (stale && state == "running" || stage is "unknown" or "awaiting-raws") activity = "waiting";
            var item = new JsonObject { ["id"] = "source-repair:" + id, ["kind"] = "source-repair", ["repair_id"] = id, ["name"] = "Repair source", ["context"] = package,
                ["state"] = state, ["repair_state"] = stage, ["repair_version"] = D.Text(job, "version", "unknown"), ["repair_reviewable"] = D.Text(job, "state") == "ready" && stage is not ("install-queued" or "installing" or "installed"),
                ["origin"] = D.Text(job, "origin"), ["message"] = message, ["activity"] = activity, ["created_at"] = Iso(job["created_at"]), ["updated_at"] = Iso(job["updated_at"]),
                ["finished_at"] = state == "running" ? null : JsonValue.Create(Iso(job["updated_at"])), ["cancellable"] = state == "running" && stage != "unknown" && !changing && !stale,
                ["stale"] = stale, ["current"] = 0, ["total"] = 0, ["repair_inputs_available"] = D.Text(job, "baseline_sha256").Length > 0 && D.Text(job, "source_apk_sha256").Length > 0,
                ["address_migration_count"] = job["address_migration_count"]?.DeepClone() ?? JsonValue.Create(0), ["raw_first"] = D.Flag(job, "raw_first"), ["repair_raw_available"] = D.Flag(job, "raw_code_available") };
            if (stage == "awaiting-raws") item["raw_package"] = package;
            if (isCurrent)
            {
                item["repair_history_revision"] = history["revision"]?.DeepClone(); item["installed_sha256"] = current["binary_sha256"]?.DeepClone();
                item["repair_can_revert"] = history["can_revert"]?.DeepClone(); item["repair_is_current"] = true;
                item["repair_can_feedback"] = !D.Truth(history["pending"]); item["repair_working_at"] = history["working_at"]?.DeepClone(); item["repair_installed_at"] = current["installed_at"]?.DeepClone();
            }
            items.Add(item);
        }
        return items;
    }
    private bool Stale() => _reachable != true || _clock() - _observed > 20;
    private static double Number(JsonObject row, string key) => Numeric(row[key]) ?? 0;
    private static double? Numeric(JsonNode? value) => value?.GetValueKind() == JsonValueKind.Number && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    private static string Iso(JsonNode? value)
    {
        if (Numeric(value) is not { } stamp) return "";
        try { return DateTimeOffset.UnixEpoch.AddSeconds(stamp).ToString("O", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return ""; }
    }
}
