using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Privacy;

namespace AutoSourcarr.Core.Diagnostics;

public sealed record CachedDiagnosticSources(JsonObject Runtime, JsonNode? Health, JsonObject SourceEngine, JsonObject InternalServices, JsonObject Tasks);

public interface IDiagnosticSources
{
    CachedDiagnosticSources CaptureCached();
    JsonObject ImportContext(JsonArray errors);
}

/// <summary>Local evidence export; remote probes and private worker bodies have no role here.</summary>
public sealed class DiagnosticExporter(BoundedWorkScheduler readLane, RuntimeIdentity identity, ErrorRegistry errors,
    SupportLogStore logs, Func<AutoSourcarrSettings> settings, IDiagnosticSources sources)
{
    private static readonly HashSet<string> TaskFields = ["id", "name", "state", "current", "total", "message", "created_at", "started_at", "updated_at", "finished_at", "error",
        "cancellable", "cancel_requested", "activity", "waiting_since", "context", "origin", "kind", "interrupted", "elapsed_seconds", "waiting_seconds", "eta_seconds", "rate_per_second",
        "repair_id", "repair_state", "repair_version", "repair_reviewable", "stale", "repair_inputs_available", "address_migration_count", "raw_first", "repair_raw_available", "raw_package",
        "repair_history_revision", "installed_sha256", "repair_can_revert", "repair_is_current", "repair_can_feedback", "repair_working_at", "repair_installed_at"];
    private static readonly string[] Counts = ["running", "active", "queued", "waiting", "completed", "failed", "history_error", "durable"];

    public async Task<JsonObject> ExportAsync(CancellationToken cancellationToken = default)
    {
        var current = settings();
        logs.Privacy.Register(SettingsLoader.ToEnvironment(current, includeSecrets: true).ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal));
        var context = await readLane.RunAsync(() => BuildContext(current), cancellationToken).ConfigureAwait(false);
        context["logs"] = await logs.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        return context;
    }

    private JsonObject BuildContext(AutoSourcarrSettings current)
    {
        var snapshot = errors.Snapshot(100, includeResolved: true);
        var partial = new JsonArray(); CachedDiagnosticSources cached;
        try { cached = sources.CaptureCached(); }
        catch (Exception error)
        {
            cached = new(new() { ["app_version"] = identity.Version, ["instance_id"] = identity.InstanceId, ["started_at"] = identity.StartedAt },
                null, new() { ["state"] = "unknown" }, new() { ["state"] = "unknown" }, new());
            partial.Add(Partial("cached_context", error));
        }
        var rawTasks = cached.Tasks["items"] as JsonArray ?? new();
        var tasks = new JsonArray();
        foreach (var task in rawTasks.Take(100).OfType<JsonObject>())
        {
            var item = new JsonObject();
            foreach (var (key, value) in task)
                if (TaskFields.Contains(key) && (value is null || value is JsonValue)) item[key] = value?.DeepClone();
            tasks.Add(item);
        }
        var counts = new JsonObject();
        foreach (var name in Counts)
            if (cached.Tasks[name] is JsonValue value) counts[name] = value.DeepClone();
        if (cached.Tasks["source_repairs"] is JsonObject repairs)
        {
            var safe = new JsonObject();
            foreach (var key in new[] { "reachable", "captured_at", "stale" }) if (repairs[key] is JsonValue scalar) safe[key] = scalar.DeepClone();
            safe["isolation"] = RepairEvidence.Isolation(repairs["isolation"], logs.Privacy); counts["source_repairs"] = safe;
        }
        var context = new JsonObject { ["schema_version"] = 3, ["runtime"] = cached.Runtime.DeepClone(), ["errors"] = snapshot,
            ["health"] = cached.Health?.DeepClone(), ["source_engine"] = cached.SourceEngine.DeepClone(), ["internal_services"] = cached.InternalServices.DeepClone(),
            ["task_counts"] = counts, ["tasks"] = tasks, ["tasks_omitted"] = Math.Max(0, rawTasks.Count - 100),
            ["limits"] = new JsonObject { ["max_source_concurrency"] = current.MaxSourceConcurrency, ["source_host_work_concurrency"] = current.SourceHostWorkConcurrency,
                ["background_source_concurrency"] = current.BackgroundSourceConcurrency, ["import_resolve_concurrency"] = current.ImportResolveConcurrency,
                ["manual_search_timeout"] = current.ManualSearchTimeout, ["manual_search_overall_timeout"] = current.ManualSearchOverallTimeout,
                ["source_request_timeout"] = current.SourceRequestTimeout } };
        try { context["imports"] = sources.ImportContext(snapshot["items"]!.AsArray()); }
        catch (Exception error) { context["partial_error"] = Partial("imports", error); }
        if (partial.Count > 0) context["partial_errors"] = partial;
        return logs.Privacy.Scrub(context)!.AsObject();
    }
    private JsonObject Partial(string section, Exception error) => new() { ["section"] = section, ["error"] = logs.Privacy.Text(error.Message.Length > 0 ? error.Message : error.GetType().Name),
        ["exception_chain"] = DiagnosticContext.ExceptionChain(error, logs.Privacy) };
}
