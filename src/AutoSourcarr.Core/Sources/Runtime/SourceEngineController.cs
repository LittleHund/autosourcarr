using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Core.Sources.Runtime;

/// <summary>
/// The source engine the app talks to. Integrated mode: the app's own source host (<see cref="SourceHostProcess"/>, which
/// supervises and restarts itself). External mode: a configured Suwayomi, unmanaged. Phase 13 removed the native PID 1
/// launcher and its owner channel (the 0.5.0 image runs <c>tini -- dotnet AutoSourcarr.Api.dll</c>), so there is no
/// native generation, native start/stop or owner retirement any more.
/// </summary>
internal sealed class SourceEngineController : IManagedSourceRuntime, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly AutoSourcarrSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly OperationOwner _requests = new();
    private readonly TaskCompletionSource<Exception> _fatal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SuwayomiClient? _client;
    private SourceHostProcess? _host;
    private SourceEngineCapabilityContract? _capabilities;
    private Task? _stop, _dispose;
    private bool _started, _stopping;
    private string _state;
    internal SuwayomiClient Client => _client ?? throw new InvalidOperationException("Source transport construction is incomplete");
    internal SourceEngineCapabilityContract CapabilityService => _capabilities ?? throw new InvalidOperationException("Source capability construction is incomplete");
    /// <summary>Completes only on a fatal engine failure; the source host restarts itself, so this stays open in 0.5.0.</summary>
    internal Task<Exception> FatalFailure => _fatal.Task;
    public bool BrowserMaintenance => false;

    internal sealed record Snapshot(string Mode, string State, bool Ready, int Restarts, string Error, string ObservedVersion,
        bool SourceObserverAvailable, string ChallengeState, JsonObject? CapabilityReport, bool CapabilityCurrent, string CapabilityError)
    { public override string ToString() => "Source engine status (private configuration omitted)."; }

    /// <summary>External mode: a configured Suwayomi. Creates no local process.</summary>
    internal static Task<SourceEngineController> CreateExternalAsync(AutoSourcarrSettings settings, SourceEngineEnvironment environment,
        IReadOnlyDictionary<string, string?> parentEnvironment, SemaphoreSlim sharedSourceAdmin, BoundedWorkScheduler io,
        SettingsRepository settingsStore, SupportPrivacy privacy, ErrorRegistry errors, string username = "", string password = "")
        => CreateAsync(settings, environment, parentEnvironment, sharedSourceAdmin, io, settingsStore, privacy, errors, TimeProvider.System, null, username, password);

    /// <summary>
    /// Integrated mode: the app's own source host (already started, or retrying on its back-off ladder) answers on
    /// 127.0.0.1 with the host protocol. The controller owns the host and stops it on dispose.
    /// </summary>
    internal static Task<SourceEngineController> CreateHostedAsync(AutoSourcarrSettings settings, SourceHostProcess host,
        SourceEngineEnvironment environment, IReadOnlyDictionary<string, string?> parentEnvironment, SemaphoreSlim sharedSourceAdmin,
        BoundedWorkScheduler io, SettingsRepository settingsStore, SupportPrivacy privacy, ErrorRegistry errors)
    {
        ArgumentNullException.ThrowIfNull(host);
        return CreateAsync(settings, environment, parentEnvironment, sharedSourceAdmin, io, settingsStore, privacy, errors, TimeProvider.System, null, "", "", host);
    }

    private SourceEngineController(AutoSourcarrSettings settings)
    {
        _settings = settings; _state = "external";
    }

    private static async Task<SourceEngineController> CreateAsync(AutoSourcarrSettings settings, SourceEngineEnvironment environment,
        IReadOnlyDictionary<string, string?> parentEnvironment, SemaphoreSlim sharedSourceAdmin, BoundedWorkScheduler io,
        SettingsRepository settingsStore, SupportPrivacy privacy, ErrorRegistry errors, TimeProvider clock, ISourceTransportFactory? factory,
        string username, string password, SourceHostProcess? host = null)
    {
        SourceEngineController? controller = null;
        try
        {
            ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(environment);
            ArgumentNullException.ThrowIfNull(parentEnvironment); ArgumentNullException.ThrowIfNull(sharedSourceAdmin);
            ArgumentNullException.ThrowIfNull(io); ArgumentNullException.ThrowIfNull(settingsStore);
            ArgumentNullException.ThrowIfNull(privacy); ArgumentNullException.ThrowIfNull(errors); ArgumentNullException.ThrowIfNull(clock);
            if (settings.SourceEngineMode is not ("integrated" or "external") || (settings.SourceEngineMode == "integrated") != (host is not null))
                throw new ArgumentException("Integrated mode needs the app's own source host; external mode has none");
            environment.RequirePrivacy(privacy);
            var identity = host is not null ? SourceEngineCapabilityIdentity.ConfiguredExternal(host.BaseUrl)
                : SourceEngineCapabilityIdentity.ConfiguredExternal(settings.EffectiveSourceUrl, username, password);
            controller = new(settings);
            if (host is not null)
            {
                controller._host = host;
                controller._state = "source-host";
                controller._client = SuwayomiClient.ForSourceHost(settings, host.BaseUrl, host.Token,
                    workLimit: settings.SourceHostWorkConcurrency, runtime: controller, privacy: privacy, transportFactory: factory);
            }
            else controller._client = new(settings, settings.EffectiveSourceUrl, workLimit: settings.SourceHostWorkConcurrency, runtime: controller,
                username: username, password: password, privacy: privacy, transportFactory: factory);
            controller._capabilities = new(settingsStore, controller.Client, identity, privacy, "source", clock);
            await Task.CompletedTask.ConfigureAwait(false);
            return controller;
        }
        catch
        {
            if (controller is not null) await controller.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal Snapshot Cached()
    {
        lock (_gate)
        {
            // External mode reports no local managed readiness (its HTTP still proceeds); the host reports its own state.
            var ready = _host is not null && _host.Running && !_stopping;
            return new(_settings.SourceEngineMode, _state, ready, _host?.Restarts ?? 0, _host?.LastError ?? "", "", false, "waiting", null, false, "");
        }
    }

    internal Task StartAsync()
    {
        lock (_gate)
        {
            if (_stopping) throw new OperationCanceledException(_lifetime.Token);
            _started = true;
            return Task.CompletedTask;
        }
    }

    public Task EnsureAvailableAsync(string requestPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) if (_stopping) throw new OperationCanceledException(_lifetime.Token);
        return Task.CompletedTask; // the host and an external engine answer (or refuse) for themselves
    }

    /// <summary>Only a direct typed socket refusal proves no listener; messages, HTTP failures and deadlines do not.</summary>
    internal static bool DirectRefusal(Exception error) => error is HttpRequestException { StatusCode: null, InnerException: SocketException socket }
        && socket.SocketErrorCode == SocketError.ConnectionRefused;

    internal Task StopAsync()
    {
        lock (_gate)
        {
            if (_stop is not null) return _stop;
            _stopping = true;
            return _stop = StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        await Task.Yield(); var errors = new List<Exception>();
        try { await _lifetime.CancelAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { await _requests.StopAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (_capabilities is not null) try { await _capabilities.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (_client is not null) try { await _client.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (_host is not null) try { await _host.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        lock (_gate) _state = _host is not null ? "stopped" : "external";
        if (errors.Count > 0) throw new AggregateException("Source controller shutdown failed", errors);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_dispose ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield(); Exception? failure = null;
        try { await StopAsync().ConfigureAwait(false); } catch (Exception error) { failure = error; }
        try { await _requests.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        try { _lifetime.Dispose(); } catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Controlled external-mode construction for tests (fake transport, own clock).</summary>
    internal sealed class ControlledHarness : IAsyncDisposable
    {
        private readonly SourceEngineController _controller;
        private ControlledHarness(SourceEngineController controller) => _controller = controller;
        internal static async Task<ControlledHarness> CreateExternalAsync(AutoSourcarrSettings settings, SourceEngineEnvironment environment,
            IReadOnlyDictionary<string, string?> parent, SemaphoreSlim admin, BoundedWorkScheduler io, SettingsRepository store,
            SupportPrivacy privacy, ErrorRegistry errors, TimeProvider clock, ISourceTransportFactory factory)
            => new(await SourceEngineController.CreateAsync(settings, environment, parent, admin, io, store, privacy, errors, clock, factory, "", "").ConfigureAwait(false));
        internal SuwayomiClient Client => _controller.Client;
        internal Snapshot Status => _controller.Cached();
        internal bool Started { get { lock (_controller._gate) return _controller._started; } }
        internal Task StartAsync() => _controller.StartAsync();
        internal Task StopAsync() => _controller.StopAsync();
        public ValueTask DisposeAsync() => _controller.DisposeAsync();
    }
}
