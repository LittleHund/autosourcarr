using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Api;

internal sealed partial class ApplicationRuntime
{
    private bool _offline;

    /// <summary>True for the cutover tool's offline open (no source engine, no network, job queue held).</summary>
    internal bool Offline => _offline;

    /// <summary>First-start work the offline run cannot cover because it needs a source, a provider or Suwayomi.</summary>
    internal static readonly string[] NetworkFirstStartWork =
    [
        "Subbed orphan aliases (one-time 0.2.80; needs the main Suwayomi library)",
        "identity resolution and cover fingerprints before the automatic duplicate sweep (the sweep itself ran offline on the stored evidence)",
        "metadata repair of sparse entries (METADATA_ENRICHMENT_ON_STARTUP)",
        "additional-record scan (AUTO_VARIANT_STARTUP_ENTRIES)",
        "browse feed refresh (BACKGROUND_REFRESH_ON_STARTUP) and the continuous crawl",
        "translation enrichment and orphan identity repair",
        "resuming interrupted backup imports and reset-hint replays (they fetch from sources)",
        "tag index rebuild (derived index; no decision involved)",
    ];

    /// <summary>
    /// Phase 14 cutover tool: the full composition on migrated data, offline. The startup snapshot must use the external
    /// source mode with an unreachable loopback engine (nothing leaves the PC) and no main Suwayomi; the update guard,
    /// migrations, env-converted configuration and every store open exactly as at the real first start. Nothing is
    /// started that talks to a source, a provider or Suwayomi: the job queue is held, the health monitor, change feed and
    /// source watches stay off, and the delayed startup schedule is replaced by <see cref="RunOfflineStartupAsync"/>.
    /// </summary>
    internal static Task<ApplicationRuntime> CreateOfflineAsync(StartupConfigurationSnapshot startup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startup);
        if (startup.Settings.SourceEngineMode != "external" || !string.IsNullOrWhiteSpace(startup.Settings.MainSuwayomiUrl))
            throw new ArgumentException("The offline open needs the external source mode and no main Suwayomi.");
        if (!Uri.TryCreate(startup.Settings.SourceSuwayomiUrl, UriKind.Absolute, out var engine) || !engine.IsLoopback)
            throw new ArgumentException("The offline open needs a loopback source engine address.");
        return OpenAsync(startup, new Dictionary<string, string?>(StringComparer.Ordinal), hosted: false, cancellationToken, offline: true);
    }

    /// <summary>
    /// Every network-free step of the 0.4.5 first start, in its real order: interrupted chapter changes, the merge safety
    /// repair, startup catalogue maintenance and main-title consensus (one-time; each stores its version as at a real
    /// start, so the first real start skips them), then the startup duplicate sweep on the stored evidence
    /// (BACKGROUND_DEDUP_ON_STARTUP; it runs again at every start and finds the same pairs unless new evidence arrives).
    /// The result's <c>problems</c> lists what keeps the migration from counting as complete (any → MIGRATION FAILED).
    /// </summary>
    internal async Task<JsonObject> RunOfflineStartupAsync(CancellationToken cancellationToken = default)
    {
        if (!_offline) throw new InvalidOperationException("Only the offline open runs the startup steps directly.");
        var problems = new JsonArray();
        var warnings = new JsonArray();
        var result = new JsonObject
        {
            ["upgrade"] = _upgrade is { } upgrade
                ? new JsonObject { ["status"] = upgrade.Status, ["from"] = upgrade.FromVersion, ["to"] = upgrade.ToVersion, ["backup_id"] = upgrade.Backup?.Id, ["error"] = Privacy.Text(upgrade.Error) }
                : null,
            ["upgrade_log"] = new JsonArray(_upgradeLog.Select(line => (JsonNode)Privacy.Text(line)).ToArray()),
            ["problems"] = problems, ["warnings"] = warnings,
            ["not_covered"] = new JsonArray(NetworkFirstStartWork.Select(item => (JsonNode)item).ToArray()),
        };
        if (_upgrade is null) problems.Add("the update guard did not run");
        else if (_upgrade.SafeMode) problems.Add("0.5.0 would start in safe mode (update status " + _upgrade.Status + (_upgrade.Error is { Length: > 0 } error ? ": " + Privacy.Text(error) : "") + ")");
        else if (_upgrade.Status is not ("updated" or "unchanged")) problems.Add("unexpected update status " + _upgrade.Status + " (the 0.4.x databases were not found or not migrated)");
        if (problems.Count > 0) return result;
        var work = _startup ?? throw new InvalidOperationException("The startup work is not composed.");
        void Step(string name, Func<JsonNode?> run)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var node = run();
                result[name] = node;
                if (node is JsonObject step && step["skipped"] is JsonValue skipped && skipped.TryGetValue<bool>(out var flag) && flag && step["error"] is not null)
                    problems.Add(name + " did not run: " + Privacy.Text(step["error"]!.ToString()));
                if (node?.ToJsonString().Contains("unavailable", StringComparison.Ordinal) == true)
                    warnings.Add(name + ": a sub-step reported \"unavailable\" in this build (see the step result); it reports the same at a real start.");
            }
            catch (Exception failure) when (failure is not OperationCanceledException and not OutOfMemoryException)
            {
                problems.Add(name + " failed (" + failure.GetType().Name + "): " + Privacy.Text(failure.Message));
            }
        }
        await Task.Run(() =>
        {
            Step("origin_recovery", () => work.Structure.PendingEntryOriginCount() > 0 ? work.Structure.ReconcilePendingOrigins(cancellationToken) : new JsonObject { ["pending"] = 0 });
            Step("merge_safety_repair", () => work.Maintenance.RunAutomaticMergeSafetyRepair());
            Step("startup_catalog_maintenance", () => work.Maintenance.RunStartupCatalogMaintenance());
            Step("title_consensus", () => work.Titles.RunBoundTitleConsensusUpgrade());
            if (Settings.BackgroundDedupOnStartup)
                Step("startup_dedup", () => work.Duplicates.RepairCatalogDuplicates(DecisionActor.Automatic, cancellationToken: cancellationToken));
            else result["startup_dedup"] = "off (BACKGROUND_DEDUP_ON_STARTUP=false)";
        }, cancellationToken).ConfigureAwait(false);
        // Needs the main Suwayomi: skipped here by design, stores no version, so the first real start runs it.
        result["subbed_orphan_aliases"] = await work.Library.RepairOrphanAliasesUpgradeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return result;
    }
}
