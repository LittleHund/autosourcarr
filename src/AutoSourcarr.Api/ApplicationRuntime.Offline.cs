using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Api;

internal sealed partial class ApplicationRuntime
{
    private bool _offline;

    /// <summary>True for the cutover tool's offline open (no source engine, no network, job queue held).</summary>
    internal bool Offline => _offline;

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
    /// The one-time steps of the 0.4.5 startup sequence that run on local data only, in their real order: interrupted
    /// chapter changes, then the merge safety repair, startup catalogue maintenance, main-title consensus and the Subbed
    /// orphan aliases (the last needs the main Suwayomi, so it reports "skipped" and runs at the first real start). Each
    /// stores its version as at a real start. Startup dedup, feed refresh, metadata repair and crawling are recurring
    /// network work and are left to the real start.
    /// </summary>
    internal async Task<JsonObject> RunOfflineStartupAsync(CancellationToken cancellationToken = default)
    {
        if (!_offline) throw new InvalidOperationException("Only the offline open runs the startup steps directly.");
        var result = new JsonObject
        {
            ["upgrade"] = _upgrade is { } upgrade
                ? new JsonObject { ["status"] = upgrade.Status, ["from"] = upgrade.FromVersion, ["to"] = upgrade.ToVersion, ["backup_id"] = upgrade.Backup?.Id, ["error"] = Privacy.Text(upgrade.Error) }
                : null,
            ["upgrade_log"] = new JsonArray(_upgradeLog.Select(line => (JsonNode)Privacy.Text(line)).ToArray()),
        };
        if (_upgrade?.SafeMode == true)
        {
            result["safe_mode"] = true;
            return result;
        }
        var work = _startup ?? throw new InvalidOperationException("The startup work is not composed.");
        await Task.Run(() =>
        {
            if (work.Structure.PendingEntryOriginCount() > 0) result["origin_recovery"] = work.Structure.ReconcilePendingOrigins(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            result["merge_safety_repair"] = work.Maintenance.RunAutomaticMergeSafetyRepair();
            cancellationToken.ThrowIfCancellationRequested();
            result["startup_catalog_maintenance"] = work.Maintenance.RunStartupCatalogMaintenance();
            cancellationToken.ThrowIfCancellationRequested();
            result["title_consensus"] = work.Titles.RunBoundTitleConsensusUpgrade();
        }, cancellationToken).ConfigureAwait(false);
        result["subbed_orphan_aliases"] = await work.Library.RepairOrphanAliasesUpgradeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        result["deferred_to_first_start"] = new JsonArray("startup dedup", "browse feed refresh", "metadata repair", "additional-record scan", "continuous crawl",
            "Subbed orphan aliases (needs the main Suwayomi)", "old-file cleanup (SET-3)", "queued background jobs");
        return result;
    }
}
