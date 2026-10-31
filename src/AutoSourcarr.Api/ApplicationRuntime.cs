using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources.Runtime;
using AutoSourcarr.Core.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

[assembly: InternalsVisibleTo("AutoSourcarr.Tests")]

namespace AutoSourcarr.Api;

/// <summary>One app owner for the existing storage, source and HTTP components.
/// Additional domain routes remain migration work; this is not default-runtime promotion.</summary>
internal sealed class ApplicationRuntime : IAsyncDisposable
{
    internal const string Version = "0.4.3-migration";
    private readonly object _closeGate = new();
    private readonly object _catalogueWriteGate = new();
    private readonly OperationOwner _http = new();
    private readonly CancellationTokenSource _logStop = new(), _watchStop = new();
    private readonly BoundedWorkScheduler _healthIo = new(1, 4), _logIo = new(1, 16);
    private readonly SemaphoreSlim _admin = new(1, 1);
    private readonly TaskCompletionSource<Exception> _fatal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private InstanceRoots? _roots;
    private readonly List<InstanceRoots> _movedRoots = [];

    private InstanceRoots KeepRoot(InstanceRoots root) { lock (_movedRoots) _movedRoots.Add(root); return root; }
    private SqliteDatabase? _database;
    private SettingsRepository? _preferences;
    private SupportLogStore? _logs;
    private NativeSourceOwnerConnection? _native;
    private NativeSourceSnapshots? _snapshots;
    private SourceEngineController? _source;
    private SourceAttention? _attention;
    private AutoSourcarr.Api.Security.AccessControlServices? _access;
    private AutoSourcarr.Core.Sources.Repositories.SourceRepositoryStore? _repositories;
    private AutoSourcarr.Core.Sources.Repositories.SourceRepositoryPreview? _repositoryPreview;
    private AutoSourcarr.Core.Repair.PublicSourceFetcher? _publicFetcher;
    private SourceHealthCoordinator? _health;
    private AutoSourcarr.Core.Sources.Configuration.SourceConfigurationStore? _sourceConfiguration;
    private AutoSourcarr.Core.Sources.Controls.SourcePreferenceService? _sourcePreferences;
    private Task? _healthMonitor;
    private readonly CancellationTokenSource _healthStop = new();
    internal AutoSourcarr.Api.Security.AccessControlServices? Access => _access;
    private CatalogueRuntime? _catalogue;
    private string? _dataDirectory;
    private SuwayomiClient? _main;
    private SourceEngineCapabilityContract? _mainCapabilities;
    private PhysicalFileProvider? _static;
    private ApiV1Services? _apiV1;
    private TaskJournal? _journal;
    private Task? _writer, _sourceWatch, _close;
    private bool _configured;

    private ApplicationRuntime(AutoSourcarrSettings settings)
    {
        Settings = settings; Identity = RuntimeIdentity.Create(Version);
        Tasks = new(); Lanes = new(settings, Tasks); Errors = new(Identity, Privacy);
        // PERF-8: one event stream; task and error changes are coalesced so a busy list does not flood the browser.
        Tasks.Changed += () => Events.Pulse("tasks", TimeSpan.FromMilliseconds(250));
        Errors.Changed += () => Events.Pulse("errors", TimeSpan.FromMilliseconds(500));
        Jobs = new(Tasks, Events, Errors);
    }
    internal EventStream Events { get; } = new();
    internal JobQueue Jobs { get; }
    internal AutoSourcarrSettings Settings { get; }
    internal RuntimeIdentity Identity { get; }
    internal SupportPrivacy Privacy { get; } = new();
    internal ErrorRegistry Errors { get; }
    internal TaskRegistry Tasks { get; }
    internal WholeTaskCoordinator Lanes { get; }
    internal RequestAdmissionGate Admission { get; } = new();
    internal BoundedWorkScheduler Io { get; } = new(4, 64);
    internal SqliteDatabase Database => _database ?? throw new InvalidOperationException("Storage is not initialized.");
    internal SettingsRepository Preferences => _preferences ?? throw new InvalidOperationException("Preferences are not initialized.");
    internal SupportLogStore Logs => _logs ?? throw new InvalidOperationException("Diagnostics are not initialized.");
    internal NativeSourceSnapshots? SourceSnapshots => _snapshots;
    internal SourceEngineController SourceRuntime => _source ?? throw new InvalidOperationException("Source runtime is not initialized.");
    internal CatalogueRuntime Catalogue => _catalogue ?? throw new InvalidOperationException("Catalogue is not initialized.");
    internal object CatalogueWriteGate => _catalogueWriteGate;
    internal Task<Exception> FatalFailure => _fatal.Task;
    internal bool Stopping => _http.IsStopping;
    /// <summary>Set by the host: asks the web host to stop normally (setup restart in the integrated image).</summary>
    internal Action? RequestStop { get; set; }

    internal static Task<ApplicationRuntime> CreateNativeAsync(StartupConfigurationSnapshot startup,
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken = default)
    {
        // No production switch bypasses the actual native identity/channel checks.
        NativeSourceOwnerConnection.RequirePostExecPrivacyBeforeConfiguration();
        return OpenAsync(startup, environment, acceptInherited: true, cancellationToken);
    }

    /// <summary>Controlled external-only composition. It cannot create a native connection,
    /// snapshot reader, process generation or repair-isolation proof.</summary>
    internal static Task<ApplicationRuntime> CreateExternalForTestsAsync(AutoSourcarrSettings settings,
        string configurationRoot, CancellationToken cancellationToken = default)
    {
        if (settings.SourceEngineMode != "external") throw new ArgumentException("The controlled host supports external mode only.");
        return OpenAsync(new(settings, Path.GetFullPath(configurationRoot)), new Dictionary<string, string?>(),
            acceptInherited: false, cancellationToken);
    }

    /// <summary>
    /// Phase 4 image path: no native launcher and no Suwayomi. In integrated mode the app starts its own source host
    /// (<see cref="SourceHostProcess"/>); external mode is unchanged.
    /// </summary>
    internal static Task<ApplicationRuntime> CreateHostedAsync(StartupConfigurationSnapshot startup,
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken = default)
        => OpenAsync(startup, environment, acceptInherited: false, cancellationToken, hosted: true);

    /// <summary>
    /// The source host gets the same source settings Suwayomi got (source-host.env, EXTENSION_STORES, FlareSolverr with
    /// the solver timeout), minus the Suwayomi-only server keys and the user-agent java agent; the host sets its own
    /// user agent.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> SourceHostEnvironment(AutoSourcarrSettings settings, SourceEngineEnvironment launch,
        IReadOnlyDictionary<string, string?> environment)
    {
        var result = launch.BuildLaunch(environment, settings).Launch.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var key in new[] { "JAVA_TOOL_OPTIONS", "BIND_IP", "BIND_PORT", "AUTH_MODE", "BASIC_AUTH_ENABLED", "WEB_UI_ENABLED",
            "INITIAL_OPEN_IN_BROWSER_ENABLED", "AUTOSOURCARR_SOURCE_UA_REVISION", "AUTOSOURCARR_SOURCE_UA_STATUS" })
            result.Remove(key);
        return result;
    }

    private static async Task<ApplicationRuntime> OpenAsync(StartupConfigurationSnapshot startup,
        IReadOnlyDictionary<string, string?> environment, bool acceptInherited, CancellationToken cancellationToken, bool hosted = false)
    {
        ArgumentNullException.ThrowIfNull(startup); ArgumentNullException.ThrowIfNull(environment);
        var owner = new ApplicationRuntime(startup.Settings);
        try
        {
            owner.Privacy.Register(environment);
            owner.Privacy.Register(SettingsLoader.ToEnvironment(startup.Settings, includeSecrets: true)
                .ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal));
            // CON-5: every secret kept in config.json is redacted too, not only the ones the settings model reads
            // (source-host and kept extra values, e.g. old Authelia passwords copied by the conversion).
            foreach (var (_, section) in new ConfigStore(startup.ConfigurationRoot).Load().Sections())
                owner.Privacy.Register(section.Where(pair => SettingsSecrets.IsSecretKey(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            cancellationToken.ThrowIfCancellationRequested();
            await owner.Io.RunAsync(() =>
            {
                // Reuse the existing ~ expansion and strict external DATABASE_DIR checks.
                // An absent configured database mount must never become an empty replacement.
                var data = DatabasePaths.Resolve(startup.Settings with { DatabaseDir = null }).Directory;
                owner._dataDirectory = data;
                var paths = DatabasePaths.Resolve(startup.Settings);
                owner._roots = InstanceRoots.Acquire([data, startup.ConfigurationRoot, paths.Directory]);
                owner._database = new(startup.Settings);
                new SchemaMigrator(owner.Database).Initialize();
                owner._preferences = new(owner.Database);
                owner._logs = new(owner._logIo, owner.Identity, owner.Privacy, data);
                return 0;
            }, cancellationToken).ConfigureAwait(false);
            if (acceptInherited)
                owner._native = await NativeSourceOwnerConnection.AcceptInheritedAsync(startup.SourceRequest, cancellationToken, owner.Logs).ConfigureAwait(false);
            var useHost = hosted && startup.Settings.SourceEngineMode == "integrated";
            if (!useHost && (startup.Settings.SourceEngineMode == "integrated") != (owner._native is not null))
                throw new InvalidDataException("Source mode and native boot ownership disagree.");
            if (owner._native is not null)
                owner._snapshots = await NativeSourceSnapshots.CreateAsync(owner._native, owner.Io, cancellationToken).ConfigureAwait(false);
            var launch = new SourceEngineEnvironment(startup.ConfigurationRoot, "/home/suwayomi/.local/share/Tachidesk", owner.Privacy);
            if (useHost)
            {
                var (java, jar) = SourceHostProcess.Locate(environment);
                // Built for every launch: a restarted host gets the saved browser identity and repositories.
                var process = new SourceHostProcess(java, jar, Path.Combine(owner._dataDirectory!, "source-host"), 4567,
                    () => SourceHostEnvironment(owner.Settings, launch, environment), line => owner.Logs.Record("Source host", line));
                owner.Privacy.Register(new Dictionary<string, string?> { ["source_host_token"] = process.Token });
                try { await process.StartAsync(TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Like the native engine: the app starts on its cached data, the status shows the source engine
                    // error and the host keeps being started on the back-off ladder.
                    owner.Logs.Record("Source host", "The source host did not start: " + error.Message);
                    owner.Errors.Record("Source engine", "The source host did not start: " + error.Message, "source-host-start");
                }
                catch { await process.DisposeAsync().ConfigureAwait(false); throw; }
                owner._source = await SourceEngineController.CreateHostedAsync(startup.Settings, process, launch, environment, owner._admin,
                    owner.Io, owner.Preferences, owner.Privacy, owner.Errors).ConfigureAwait(false);
            }
            else owner._source = owner._native is null
                ? await SourceEngineController.CreateExternalAsync(startup.Settings, launch, environment, owner._admin, owner.Io,
                    owner.Preferences, owner.Privacy, owner.Errors).ConfigureAwait(false)
                : await SourceEngineController.CreateIntegratedAsync(startup.Settings, owner._native, launch, environment, owner._admin, owner.Io,
                    owner.Preferences, owner.Privacy, owner.Errors).ConfigureAwait(false);
            // Task history survives restarts (0.4.x task_journal): finished tasks are reloaded, interrupted ones are marked.
            owner._journal = new TaskJournal(owner.Database, owner.Tasks, owner.Io, owner.Privacy);
            await owner._journal.StartAsync(cancellationToken).ConfigureAwait(false);
            owner._attention = new(owner.Database, owner.Io, privacy: owner.Privacy);
            owner._source.Client.Attention = owner._attention;
            owner._catalogue = await CatalogueRuntime.CreateAsync(owner.Database, owner._catalogueWriteGate,
                owner._dataDirectory!, () => owner.Settings, owner.Io, owner._source.Client, owner.Tasks, owner.Lanes, owner.Logs, cancellationToken).ConfigureAwait(false);
            owner._access = OpenAccounts(owner, startup);
            owner._sourceConfiguration = new AutoSourcarr.Core.Sources.Configuration.SourceConfigurationStore(owner.Database, owner._catalogueWriteGate,
                () => owner.Settings.SourceLanguages.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal),
                // Discovery and duplicate invalidations are acted on by the matching phase (8); recorded until then.
                effect => owner.Logs.Record("Sources", "Source choices changed (" + effect.Kind + ")."));
            owner._sourcePreferences = new AutoSourcarr.Core.Sources.Controls.SourcePreferenceService(owner._source.Client, owner._admin,
                () => (owner._source.Cached().Ready || owner.Settings.SourceEngineMode == "external", 0), owner.Privacy);
            owner._repositories = new(new CanonicalConfiguration(startup.ConfigurationRoot));
            owner._publicFetcher = new();
            owner._repositoryPreview = new(owner._publicFetcher);
            owner._health = new SourceHealthCoordinator(new SourceHealthStore(owner.Database, owner.Privacy), owner._source.Client, owner.Io, owner.Lanes,
                owner.Tasks, new NoRepairObservations(), () => owner._source.Cached().Ready || owner.Settings.SourceEngineMode == "external", owner.Privacy);
            owner._healthMonitor = Task.Run(() => owner._health.RunMonitorAsync(error => owner.Errors.Record("Source health", error.Message, "source-health-monitor"),
                owner._healthStop.Token));
            owner._apiV1 = new ApiV1Services(() => owner.Settings, startup.ConfigurationRoot, startup.Conversion, startup.Environment,
                values => owner.Privacy.Register(values), new Core.Catalogue.CatalogueStatusStats(owner.Database, () => owner.Settings),
                () => owner.RequestStop?.Invoke(), health: owner.HealthAsync, databasePaths: () => owner.Database.Paths,
                moveDatabase: path => DatabaseRelocation.Move(owner.Database, path, directory => owner._apiV1!.Editor.SaveDatabaseDirectory(directory),
                    claimTarget: target => owner.KeepRoot(InstanceRoots.Acquire([target]))))
            {
                CheckDatabaseTarget = path => DatabaseRelocation.CheckTarget(owner.Database, path),
                Tasks = owner.Tasks, Jobs = owner.Jobs, Events = owner.Events, Errors = owner.Errors, Access = owner._access,
                SourceInventory = new SourceInventoryServices
                {
                    Orphans = new AutoSourcarr.Core.Sources.Inventory.SourceOrphans(Path.Combine(owner._dataDirectory!, "source-maintenance"),
                        owner._source.Client, owner._publicFetcher!, SourceInventoryServices.BundledSeed, scrub: owner.Privacy.Text),
                    Maintenance = new AutoSourcarr.Core.Sources.Inventory.SourceMaintenanceState(Path.Combine(owner._dataDirectory!, "source-maintenance"), startup.ConfigurationRoot),
                    Client = owner._source.Client, AdminLock = owner._admin,
                    RecordError = error => owner.Errors.Record("Source orphans", error.Message, "source-orphans|" + error.GetType().Name)
                },
                SourceRepair = new SourceRepairServices
                {
                    Domain = new AutoSourcarr.Core.Repair.Migration.DomainRepair(Path.Combine(owner._dataDirectory!, "source-maintenance"),
                        owner._source.Client, owner._admin, owner._publicFetcher),
                    RecordError = error => owner.Errors.Record("Source repair", error.Message, "source-repair|" + error.GetType().Name)
                },
                Sources = new SourceServices
                {
                    Configuration = owner._sourceConfiguration!, Client = owner._source.Client, Preferences = owner._sourcePreferences!, AdminLock = owner._admin
                },
                SourceRuntime = new SourceRuntimeServices
                {
                    Engine = owner.SourceEngineJson, Client = owner._source.Client, Attention = owner._attention,
                    SourceCapabilities = () => owner._source?.CapabilityService, MainCapabilities = () => owner._mainCapabilities, AdminLock = owner._admin,
                    RecordError = error => owner.Errors.Record("Source attention", error.Message, "attention-retry|" + error.GetType().Name)
                },
                Diagnostics = new DiagnosticExporter(owner.Io, owner.Identity, owner.Errors, owner.Logs, () => owner.Settings, new RuntimeDiagnosticSources(owner)),
            };
            if (!string.IsNullOrWhiteSpace(startup.Settings.MainSuwayomiUrl))
            {
                owner._main = new(startup.Settings, startup.Settings.MainSuwayomiUrl,
                    username: startup.Settings.MainSuwayomiUsername, password: startup.Settings.MainSuwayomiPassword, privacy: owner.Privacy);
                owner._mainCapabilities = new(owner.Preferences, owner._main,
                    SourceEngineCapabilityIdentity.ConfiguredExternal(startup.Settings.MainSuwayomiUrl,
                        startup.Settings.MainSuwayomiUsername, startup.Settings.MainSuwayomiPassword), owner.Privacy, "main");
            }
            cancellationToken.ThrowIfCancellationRequested();
            owner._writer = owner.WriteLogsAsync();
            owner._sourceWatch = owner.WatchSourceAsync();
            await owner._source.StartAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return owner;
        }
        catch (Exception primary)
        {
            owner._logs?.Record("Startup", "Application composition failed (" + primary.GetType().Name + ").");
            try { await owner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("Application startup and owned cleanup failed.", primary, cleanup); }
            throw;
        }
    }

    internal Task BeginHttpStopAsync() => _http.StopAsync();

    internal void ConfigureHttp(IApplicationBuilder app, string staticRoot)
    {
        if (_configured || Stopping) throw new InvalidOperationException("The application HTTP lifetime cannot be configured again.");
        _configured = true;
        _static = new(Path.GetFullPath(staticRoot));
        app.Use(next => context => OwnRequestAsync(context, next));
        app.UseAutoSourcarrHttp(Version, () => Settings, RecordUnexpected, Admission, Lanes, Tasks, Privacy,
            prefix => Errors.ResolvePrefix(prefix), _access);
        app.UseStaticFiles(new StaticFileOptions { RequestPath = "/static", FileProvider = _static });
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGet("/", (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(Path.Combine(_static.Root, "index.html"), "text/html; charset=utf-8");
            });
            endpoints.MapMethods("/api/v1/live", ["GET", "HEAD"], (Func<HttpContext, Task<IResult>>)LivenessAsync);
            endpoints.MapGet("/login", (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(Path.Combine(_static.Root, "login.html"), "text/html; charset=utf-8");
            });
            endpoints.MapUiPreferences(Preferences, Io, () => Settings);
            endpoints.MapErrors(Errors, Io, () => Settings);
            endpoints.MapEngineCompatibility(_source!.CapabilityService, _mainCapabilities, () => Settings, _admin);
            Catalogue.MapEndpoints(endpoints, Io, () => Settings, _admin);
            endpoints.MapApiV1(_apiV1!);
            // Phase 6: repositories and source health under /api/v1 (same handlers as the stage-26 /api/admin wires).
            endpoints.MapSourceRepositories(_repositories!, _repositoryPreview!, Io, () => Settings, "/api/v1/source-repositories");
            endpoints.MapSourceHealth(_health!, () => Settings, _admin, "/api/v1/source-health");
        });
    }

    private async Task OwnRequestAsync(HttpContext context, RequestDelegate next)
    {
        IDisposable lease;
        try { lease = _http.Enter(); }
        catch (OperationCanceledException)
        {
            context.Response.Headers.RetryAfter = "5";
            await Results.Json(new { detail = "The application is shutting down." }, statusCode: 503).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        using (lease)
        {
            var original = context.RequestAborted;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(original, _http.CancellationToken);
            context.RequestAborted = linked.Token;
            try { await next(context).ConfigureAwait(false); }
            finally { context.RequestAborted = original; }
        }
    }

    /// <summary>
    /// /api/v1/health for the Status page (port of main.py health). Databases, counts and errors are real; the
    /// source engine is checked (phase 4); main Suwayomi and translation checks belong to phase 7, so they are reported
    /// as not checked (checked=false) instead of guessing.
    /// </summary>
    internal async Task<JsonObject> HealthAsync(CancellationToken cancellationToken)
    {
        var settings = Settings;
        JsonObject stats;
        try { stats = await _apiV1!.Status!.SnapshotAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        { stats = new JsonObject { ["error"] = "Status counts are not available (" + error.GetType().Name + ")." }; }
        var errors = Errors.Snapshot(5, includeResolved: false);
        var source = await SourceHealthAsync(settings, cancellationToken).ConfigureAwait(false);
        static JsonObject NotChecked(string url, bool configured, string phase) => new()
        {
            ["ok"] = null, ["checked"] = false, ["configured"] = configured, ["url"] = url,
            ["warning"] = "Not checked by this version; the check returns with conversion phase " + phase + "."
        };
        return new JsonObject
        {
            ["ok"] = true,
            ["version"] = Identity.Version,
            ["captured_at"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["instance_id"] = Identity.InstanceId,
            ["source_host"] = source,
            ["main_suwayomi"] = NotChecked(settings.MainSuwayomiUrl, !string.IsNullOrWhiteSpace(settings.MainSuwayomiUrl), "7"),
            ["translation"] = NotChecked(settings.TranslationApiUrl, !string.IsNullOrWhiteSpace(settings.TranslationApiUrl), "7"),
            ["library"] = new JsonObject { ["ok"] = null, ["checked"] = false },
            ["stats"] = stats,
            ["errors"] = new JsonObject
            {
                ["active"] = errors["grouped_active_count"]?.DeepClone() ?? errors["active_count"]?.DeepClone(),
                ["active_errors"] = errors["grouped_active_error_count"]?.DeepClone() ?? errors["active_error_count"]?.DeepClone(),
                ["active_warnings"] = errors["grouped_active_warning_count"]?.DeepClone() ?? errors["active_warning_count"]?.DeepClone(),
                ["items"] = errors["items"]?.DeepClone() ?? new JsonArray(),
                ["groups"] = errors["groups"]?.DeepClone() ?? new JsonArray()
            }
        };
    }

    /// <summary>
    /// Phase 5: accounts and keys (config/accounts), the security log (data folder), the one-time owner reset
    /// (AUTH_RESET_OWNER, AUTH-10) and the external owner binding (AUTH_OWNER_EXTERNAL_USER, AUTH-2).
    /// </summary>
    private static AutoSourcarr.Api.Security.AccessControlServices OpenAccounts(ApplicationRuntime owner, StartupConfigurationSnapshot startup)
    {
        var settings = startup.Settings;
        var accounts = new AutoSourcarr.Core.Accounts.AccountStore(startup.ConfigurationRoot);
        var log = new AutoSourcarr.Core.Accounts.SecurityLog(owner._dataDirectory!);
        owner.Privacy.Register(new Dictionary<string, string?> { ["automatic_admin_token"] = accounts.AutomaticAdminToken });
        // Applied once per use of the variable (not on every restart while it stays set); the one-time setup code
        // goes to the server log only, so a visitor cannot claim the owner account while it has no password.
        if (!settings.AuthResetOwner) accounts.ClearResetMarker();
        else if (!accounts.ResetApplied && accounts.ResetOwnerLogin() is { } code)
        {
            log.Record("owner-reset", "server", "", "AUTH_RESET_OWNER: the owner's password was cleared.");
            Console.WriteLine("AutoSourcarr: owner sign-in reset by AUTH_RESET_OWNER. Open the login page and enter setup code " + code
                + " with a new password, then remove AUTH_RESET_OWNER.");
        }
        if (settings.AuthMethod == "external" && settings.AuthOwnerExternalUser.Trim().Length > 0 && accounts.SetupRequired)
        {
            var bound = accounts.BindExternalOwner(settings.AuthOwnerExternalUser);
            log.Record("owner-created", bound.Name, "", "bound to the external account");
        }
        return new AutoSourcarr.Api.Security.AccessControlServices
        {
            Accounts = accounts, Log = log, Settings = () => owner.Settings,
            RegisterSecret = secret => owner.Privacy.Register(new Dictionary<string, string?> { ["automatic_admin_token"] = secret }),
            Burst = message => owner.Errors.Record("Security", message, "failed-login-burst", severity: "warning")
        };
    }

    /// <summary>The Status page's source engine check: the source host's own health (or the external engine's).</summary>
    private async Task<JsonObject> SourceHealthAsync(AutoSourcarrSettings settings, CancellationToken cancellationToken)
    {
        var engine = SourceEngineJson();
        var url = SourceRuntime.Client.SourceHost ? "built-in source host" : settings.EffectiveSourceUrl;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var detail = SourceRuntime.Client.SourceHost
                ? await SourceRuntime.Client.HostAsync(HttpMethod.Get, "/v1/health", null, timeout.Token).ConfigureAwait(false) as JsonObject ?? new JsonObject()
                : await SourceRuntime.Client.HealthAsync(timeout.Token).ConfigureAwait(false);
            return new JsonObject { ["ok"] = true, ["checked"] = true, ["configured"] = true, ["url"] = url, ["engine"] = engine, ["detail"] = detail.DeepClone() };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new JsonObject { ["ok"] = false, ["checked"] = true, ["configured"] = true, ["url"] = url, ["engine"] = engine, ["warning"] = "The source engine did not answer within 5 s." }; }
        catch (Exception error) when (error is SuwayomiException or SourceWorkDeferredException or HttpRequestException)
        { return new JsonObject { ["ok"] = false, ["checked"] = true, ["configured"] = true, ["url"] = url, ["engine"] = engine, ["warning"] = Privacy.Text(error.Message) }; }
    }

    /// <summary>Source engine status for /api/v1/source-engine and the health page.</summary>
    internal JsonObject SourceEngineJson()
    {
        var status = SourceRuntime.Cached();
        return new JsonObject
        {
            ["mode"] = status.Mode, ["state"] = status.State, ["ready"] = status.Ready, ["restarts"] = status.Restarts,
            ["error"] = status.Error, ["engine"] = SourceRuntime.Client.SourceHost ? "autosourcarr-source-host" : "suwayomi"
        };
    }

    private async Task<IResult> LivenessAsync(HttpContext context)
    {
        try
        {
            await _healthIo.RunAsync(() =>
            {
                _ = Database.Read(DatabaseFile.Catalog, connection => connection.Scalar("SELECT 1"));
                _ = Database.Read(DatabaseFile.Origins, connection => connection.Scalar("SELECT 1"));
                return 0;
            }, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(new { ok = true, version = Version });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            _ = RecordUnexpected(context, error);
            return Results.Json(new { detail = "Database health check incomplete: " + error.GetType().Name }, statusCode: 503);
        }
    }
    private string RecordUnexpected(HttpContext context, Exception error)
    {
        Logs.Record("AutoSourcarr", error.Message);
        return Errors.Record("AutoSourcarr", error.Message,
            $"unhandled|{context.Request.Method}|{context.Request.Path}|{error.GetType().Name}|{error.Message}",
            path: context.Request.Path.Value ?? "", method: context.Request.Method, errorType: error.GetType().Name, exception: error);
    }
    private async Task WatchSourceAsync()
    {
        try { _fatal.TrySetResult(await _source!.FatalFailure.WaitAsync(_watchStop.Token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (_watchStop.IsCancellationRequested) { }
    }
    private async Task WriteLogsAsync()
    {
        try
        {
            while (!_logStop.IsCancellationRequested)
            {
                try { await Logs.RunWriterAsync(_logStop.Token).ConfigureAwait(false); }
                catch (WorkDeferredException) when (!_logStop.IsCancellationRequested)
                { await Task.Delay(TimeSpan.FromSeconds(1), _logStop.Token).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) when (_logStop.IsCancellationRequested) { }
        catch (Exception error) { _fatal.TrySetResult(error); throw; }
    }

    public ValueTask DisposeAsync() { lock (_closeGate) return new(_close ??= CloseAsync()); }
    private async Task CloseAsync()
    {
        // Publish the cached close task before callbacks can re-enter this owner.
        await Task.Yield();
        List<Exception> errors = [];
        async Task Attempt(string stage, Func<Task> close)
        {
            try { await close().ConfigureAwait(false); }
            catch (Exception error)
            {
                errors.Add(error);
                _logs?.Record("Shutdown", stage + " failed (" + error.GetType().Name + ").");
            }
        }
        await Attempt("HTTP drain", BeginHttpStopAsync).ConfigureAwait(false);
        await Attempt("Job queue", () => Jobs.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt("Task lanes", () => Lanes.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_catalogue is not null) await Attempt("Catalogue lifetime", () => _catalogue.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_attention is not null) await Attempt("Source attention", () => _attention.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt("Source health monitor stop", () => _healthStop.CancelAsync()).ConfigureAwait(false);
        if (_healthMonitor is not null)
            await Attempt("Source health monitor", async () => { try { await _healthMonitor.ConfigureAwait(false); } catch (OperationCanceledException) { } }).ConfigureAwait(false);
        if (_health is not null) await Attempt("Source health", () => _health.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_sourcePreferences is not null) await Attempt("Source preferences", () => _sourcePreferences.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_publicFetcher is not null) await Attempt("Public fetcher", () => _publicFetcher.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_mainCapabilities is not null) await Attempt("Main observations", () => _mainCapabilities.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_main is not null) await Attempt("Main transport", () => _main.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_source is not null) await Attempt("Source lifetime", () => _source.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_native is not null) await Attempt("Native lifetime", () => _native.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt("Source status watch cancellation", () => _watchStop.CancelAsync()).ConfigureAwait(false);
        if (_sourceWatch is not null) await Attempt("Source status watch", () => _sourceWatch).ConfigureAwait(false);
        // Last task changes (stopped jobs, finished lifetimes) are flushed before the I/O lane and the database close.
        if (_journal is not null) await Attempt("Task history", () => _journal.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt("General physical I/O", () => Io.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_database is not null)
            await Attempt("Database close", async () =>
            { await _healthIo.RunAsync(() => { _database.Dispose(); return 0; }, CancellationToken.None).ConfigureAwait(false); }).ConfigureAwait(false);
        await Attempt("Health physical I/O", () => _healthIo.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt("Log writer cancellation", () => _logStop.CancelAsync()).ConfigureAwait(false);
        if (_writer is not null) await Attempt("Log writer", () => _writer).ConfigureAwait(false);
        if (_logs is not null) await Attempt("Diagnostic store", () => _logs.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt("Diagnostic physical I/O", () => _logIo.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt("HTTP ownership", () => _http.DisposeAsync().AsTask()).ConfigureAwait(false);
        try { _static?.Dispose(); } catch (Exception error) { errors.Add(error); }
        lock (_movedRoots)
            foreach (var root in _movedRoots)
                try { root.Dispose(); } catch (Exception error) { errors.Add(error); }
        try { _roots?.Dispose(); } catch (Exception error) { errors.Add(error); }
        _admin.Dispose(); _watchStop.Dispose(); _logStop.Dispose();
        if (errors.Count > 0) throw new AggregateException("Application shutdown retained cleanup failures.", errors);
    }

    /// <summary>What the diagnostics download reads from the running app (cached values only; no remote probes).</summary>
    private sealed class RuntimeDiagnosticSources(ApplicationRuntime owner) : IDiagnosticSources
    {
        public CachedDiagnosticSources CaptureCached()
        {
            var runtime = new JsonObject
            {
                ["app_version"] = owner.Identity.Version, ["instance_id"] = owner.Identity.InstanceId,
                ["source_engine_mode"] = owner.Settings.SourceEngineMode, ["app_services_mode"] = owner.Settings.AppServicesMode,
                ["database_dir"] = owner._database?.Paths.Directory, ["event_subscribers"] = owner.Events.SubscriberCount,
                ["last_event_id"] = owner.Events.LastId,
            };
            var jobs = owner.Jobs.Snapshot();
            // Job keys can hold paths or titles; the download keeps type, state and timing only.
            foreach (var list in new[] { jobs["running"]!.AsArray(), jobs["queued"]!.AsArray() })
                foreach (var row in list) row!.AsObject().Remove("key");
            var source = new JsonObject { ["mode"] = owner.Settings.SourceEngineMode, ["checked"] = false,
                ["note"] = "Source engine health returns with conversion phase 4." };
            var services = new JsonObject { ["jobs"] = jobs };
            return new(runtime, null, source, services, owner.Jobs.Annotate(owner.Tasks.Snapshot()));
        }
        public JsonObject ImportContext(JsonArray errors) => new() { ["note"] = "Import context returns with conversion phase 9." };
    }

    /// <summary>Held regular-file locks, not deletion/recreation markers. Raw-source storage
    /// is deliberately excluded so independent instances may share only their raw archives.</summary>
    internal sealed class InstanceRoots : IDisposable
    {
        private readonly List<FileStream> _files = [];
        private int _closed;
        internal static InstanceRoots Acquire(IEnumerable<string> roots)
        {
            var held = new InstanceRoots();
            try
            {
                var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                foreach (var root in roots.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).Distinct(comparer).Order(comparer))
                {
                    PrivateDocumentStore.ValidateDirectory(root);
                    var file = PrivateDocumentStore.OpenRegular(Path.Combine(root, ".autosourcarr-instance.lock"), writable: true, create: true, maximum: 64);
                    var locked = false;
                    try
                    {
                        if (!PrivateDocumentStore.TryExclusiveLock(file)) throw new IOException("Another AutoSourcarr process already owns this instance storage.");
                        locked = true; held._files.Add(file);
                        if (file.Length == 0) { file.WriteByte((byte)'0'); file.Flush(flushToDisk: true); }
                    }
                    finally { if (!locked) file.Dispose(); }
                }
                return held;
            }
            catch (Exception primary)
            {
                try { held.Dispose(); }
                catch (Exception cleanup) { throw new AggregateException("Instance admission and lock cleanup failed.", primary, cleanup); }
                throw;
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            List<Exception> errors = [];
            foreach (var file in _files.AsEnumerable().Reverse())
            {
                try { PrivateDocumentStore.ReleaseExclusiveLock(file); } catch (Exception error) { errors.Add(error); }
                try { file.Dispose(); } catch (Exception error) { errors.Add(error); }
            }
            if (errors.Count > 0) throw new AggregateException("Instance lock cleanup failed.", errors);
        }
    }
}

/// <summary>Repair observations for source health arrive with the repair workflow (phase 6e); until then none.</summary>
internal sealed class NoRepairObservations : IRepairHealthObservations
{
    public Task<(System.Text.Json.Nodes.JsonArray Jobs, System.Text.Json.Nodes.JsonObject Operation, System.Text.Json.Nodes.JsonObject Histories)> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult((new System.Text.Json.Nodes.JsonArray(), new System.Text.Json.Nodes.JsonObject(), new System.Text.Json.Nodes.JsonObject()));
}
