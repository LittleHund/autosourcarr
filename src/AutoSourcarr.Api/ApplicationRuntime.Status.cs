using System.Globalization;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Api;

internal sealed partial class ApplicationRuntime
{
    /// <summary>
    /// Phase 11: the Status page's "internal services" panel. 0.4.x listed the integrated image's helper services
    /// (/api/admin/services: repair worker, reader builder, translation, solver); 0.5.0 has no helper services, so this
    /// reports what runs inside the app itself, from state the app already holds. It never waits on the network: the
    /// source host, translation and main Suwayomi are reported from their last cached result.
    /// Each service: {label, state, message}; state is running, ready, idle, off, degraded or unavailable.
    /// </summary>
    internal JsonObject InternalServices()
    {
        var settings = Settings;
        var integrated = _hostProcess is not null;
        var services = new JsonObject
        {
            ["source_host"] = SourceHostService(),
            ["webview"] = WebViewService(),
            ["solver"] = SolverService(),
            ["repair"] = RepairService(integrated),
            ["egress_proxy"] = EgressService(integrated),
            ["translation"] = TranslationService(),
            ["metadata"] = MetadataService(),
            ["suwayomi"] = SuwayomiService(settings),
            ["builder"] = ReaderExtensionService(),
        };
        return new JsonObject
        {
            ["mode"] = integrated ? "integrated" : "external",
            ["captured_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            // No service is started from this panel in 0.5.0: everything starts with the app or on demand.
            ["actions"] = new JsonArray(),
            ["services"] = services,
            ["reader_build"] = _apiV1 is { } api ? System.Text.Json.JsonSerializer.SerializeToNode(api.Reader(api.Editor.Effective()),
                Endpoints.ApiJsonContext.Default.ReaderConfigResult) : null,
        };
    }

    private static JsonObject Service(string label, string state, string message) =>
        new() { ["label"] = label, ["state"] = state, ["message"] = message };

    private JsonObject SourceHostService()
    {
        if (_source is null) return Service("Source host", "unavailable", "The source engine is not initialized.");
        var status = _source.Cached();
        var restarts = status.Restarts > 0 ? " Restarted " + status.Restarts.ToString(CultureInfo.InvariantCulture) + " time(s)." : "";
        if (_hostProcess is null)
            return Service("Source engine", status.Ready ? "running" : "degraded",
                (status.Ready ? "External source engine answers." : "External source engine is not ready" + Error(status.Error) + ".") + restarts);
        return status.Ready
            ? Service("Source host", "running", "The built-in source host (extension sources) is running." + restarts)
            : Service("Source host", _hostProcess.Running ? "starting" : "degraded",
                (_hostProcess.Running ? "The source host is starting" : "The source host is not running; it is restarted automatically") + Error(status.Error) + "." + restarts);
    }

    private string Error(string? error) => string.IsNullOrWhiteSpace(error) ? "" : " (" + Privacy.Text(error) + ")";

    private JsonObject WebViewService()
    {
        if (_hostProcess is not { } host) return Service("WebView", "unavailable", "The built-in WebView runs in the source host (integrated mode only).");
        if (!host.Running) return Service("WebView", "idle", "Starts with the source host.");
        return host.Display is { } display
            ? Service("WebView", "ready", "WebView (Chromium) has a display (" + display + "); verification pages open in Sources → WebView.")
            : Service("WebView", "unavailable", "No X display: Xvfb is not installed or did not start, so sources that need a WebView cannot load.");
    }

    private JsonObject SolverService()
    {
        var solvers = EnabledSolvers();
        var count = solvers?.Count ?? 0;
        var webview = _hostProcess is { } host && host.Display is not null;
        var external = count == 0 ? "no external solver connection" : count.ToString(CultureInfo.InvariantCulture) + " external solver connection(s) as fallback";
        if (solvers is null) return Service("Challenge handling", "degraded", "The solver connections could not be read; " + (webview ? "the built-in WebView still solves challenges." : "no solver is available."));
        if (webview) return Service("Challenge handling", "ready", "Built-in WebView solves challenges first; " + external + ".");
        return count > 0 ? Service("Challenge handling", "ready", "No built-in WebView; " + external + ".")
            : Service("Challenge handling", "off", "No built-in WebView and no external solver connection (Connections → FlareSolverr/Byparr).");
    }

    private JsonObject RepairService(bool integrated)
    {
        if (!integrated) return Service("Source repair", "unavailable", "Repairs run against the built-in source host (integrated mode only).");
        var isolation = _repairIsolation?.Probe();
        return Service("Source repair", "ready", "Domain-move repairs are available (Tasks → Repairs). "
            + (isolation is { Available: true } ? isolation.Message : "Isolation unavailable: " + (isolation?.Message ?? "repairs that run code are unavailable.")));
    }

    private JsonObject EgressService(bool integrated) =>
        integrated && _repairIsolation?.Probe() is { Available: true }
            ? Service("Repair egress proxy", "idle", "Started for each isolated repair run; it is that run's only network path and allows listed domains only.")
            : Service("Repair egress proxy", "unavailable", "Only used by the isolated repair runner, which is not available here.");

    private JsonObject TranslationService()
    {
        if (_translationHealth is not { } health) return Service("Title translation", "off", "Translation is not configured (TRANSLATION_MODE, TRANSLATION_API_URL).");
        var snapshot = health.Snapshot(_healthStop.Token);
        var configured = snapshot["configured"] is JsonValue value && value.TryGetValue<bool>(out var set) && set;
        if (!configured) return Service("Title translation", "off", "Translation is off or not configured (TRANSLATION_MODE, TRANSLATION_API_URL).");
        var ok = snapshot["ok"] is JsonValue okValue && okValue.TryGetValue<bool>(out var fine) ? fine : (bool?)null;
        return ok switch
        {
            true => Service("Title translation", "ready", "The translation service answered its last check."),
            false => Service("Title translation", "degraded", "The translation service failed its last check: " + Privacy.Text(snapshot["warning"]?.ToString() ?? "no answer") + "."),
            _ => Service("Title translation", "idle", "Not checked yet; a background check has started."),
        };
    }

    private JsonObject MetadataService()
    {
        if (_metadataProviders is not { } providers) return Service("Metadata providers", "unavailable", "Metadata providers are not initialized.");
        IReadOnlyList<AutoSourcarr.Core.Connections.Types.IMetadataProviderClient> current;
        try { current = providers.Current(); }
        catch (Exception error) when (error is Core.Settings.ConfigurationException or IOException or UnauthorizedAccessException or ArgumentException)
        { return Service("Metadata providers", "degraded", "The metadata provider connections could not be read."); }
        if (current.Count == 0) return Service("Metadata providers", "off", "No metadata provider is enabled (Settings: METADATA_IDENTITY_PROVIDERS, or Connections).");
        return Service("Metadata providers", "ready", string.Join(", ", current.Select(client => client.Provider).Distinct(StringComparer.Ordinal)) + ".");
    }

    private JsonObject SuwayomiService(Core.Settings.AutoSourcarrSettings settings)
    {
        var health = MainSuwayomiHealth(settings);
        var configured = health["configured"] is JsonValue value && value.TryGetValue<bool>(out var set) && set;
        if (!configured) return Service("Main Suwayomi", "off", "Optional: no main Suwayomi is connected (Connections → Suwayomi).");
        var ok = health["ok"] is JsonValue okValue && okValue.TryGetValue<bool>(out var fine) ? fine : (bool?)null;
        var feed = " Change feed: " + (health["feed"]?.ToString() ?? "off") + ".";
        return ok switch
        {
            true => Service("Main Suwayomi", "running", "Connected." + feed),
            false => Service("Main Suwayomi", "degraded", "The last connection test failed" + (health["warning"] is { } warning ? ": " + warning : "") + "." + feed),
            _ => Service("Main Suwayomi", "idle", "Not tested yet; a background test has started." + feed),
        };
    }

    private JsonObject ReaderExtensionService()
    {
        if (_apiV1?.ReaderExtension?.Repository is not { } repository) return Service("Reader extension", "unavailable", "The reader extension repository is not available.");
        var extension = repository.Extension();
        if (extension is null)
            return Service("Reader extension", "unavailable", "No prebuilt reader extension is installed in this image (release images include it).");
        var version = extension["version"]?.ToString() ?? "";
        return Service("Reader extension", "ready", "Prebuilt reader extension " + version + " is served from this instance's repository (/api/reader/repo/).");
    }
}
