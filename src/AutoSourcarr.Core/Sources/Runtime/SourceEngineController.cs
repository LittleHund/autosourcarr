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
/// Owns the integrated engine through an already accepted native connection. A native launch,
/// HTTP readiness, API shape, source observation and a working source remain separate facts.
/// External mode preserves the original non-managed behavior and owns no local process.
/// </summary>
internal sealed class SourceEngineController : IManagedSourceRuntime, IAsyncDisposable
{
    private const string IntegratedUrl = "http://127.0.0.1:4567";
    private readonly object _gate = new();
    private readonly AutoSourcarrSettings _settings;
    private readonly NativeCommands? _native;
    private readonly SourceEngineEnvironment _environment;
    private readonly IReadOnlyDictionary<string, string?> _parent;
    private readonly SemaphoreSlim _admin, _wake = new(0, 1);
    private readonly BoundedWorkScheduler _io;
    private readonly SupportPrivacy _privacy;
    private readonly ErrorRegistry _errors;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly OperationOwner _requests = new();
    private readonly TaskCompletionSource<Exception> _fatal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Exception> _cleanup = [];
    private SuwayomiClient? _client;
    private SourceEngineCapabilityContract? _capabilities;
    private Task? _loop, _retirement, _capability, _stop, _dispose;
    private Request? _request;
    private bool _started, _stopping, _disposed, _hold, _ready, _maintenance;
    private int _restarts;
    private long _revision;
    private ulong _generation;
    private string _state, _error = "", _observedVersion = "", _capabilityError = "";
    private Exception? _fatalError;
    private JsonObject? _capabilityReport;
    private ulong? _capabilityGeneration;
    private ulong? _capabilityRequestedGeneration;
    private string _challengeState = "waiting";
    private IReadOnlyDictionary<string, string>? _intendedChallenges;
    internal SuwayomiClient Client => _client ?? throw new InvalidOperationException("Source transport construction is incomplete");
    internal SourceEngineCapabilityContract CapabilityService => _capabilities ?? throw new InvalidOperationException("Source capability construction is incomplete");
    internal Task<Exception> FatalFailure => _fatal.Task;
    public bool BrowserMaintenance { get { lock (_gate) return _maintenance; } }

    internal sealed record Snapshot(string Mode, string State, bool Ready, ulong? NativeGeneration,
        int Restarts, string Error, string ObservedVersion, bool SourceObserverAvailable, string ChallengeState,
        JsonObject? CapabilityReport, bool CapabilityCurrent, string CapabilityError)
    { public override string ToString() => "Source engine status (private configuration omitted)."; }
    internal sealed record EnginePause(Snapshot Status, NativeSourceOwnerResponseMessage? NativeStop)
    { public override string ToString() => "Source engine lifecycle observation."; }
    private enum RequestKind { Restart, Pause }
    private sealed class Request(RequestKind kind, IDisposable lease) : IDisposable
    {
        internal RequestKind Kind { get; } = kind;
        internal TaskCompletionSource<EnginePause> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => lease.Dispose();
    }
    private sealed class SupersededAttempt : Exception { }
    private sealed class SetupRequired(string message, Exception? inner = null) : Exception(message, inner);
    private sealed class PortUnavailable(string message, Exception? inner = null) : IOException(message, inner);
    private sealed class NativeRetired(Exception inner) : IOException("Native source ownership retired; the instance must exit.", inner);

    // These adapters are private and have no callback-based readiness, stop, or native-proof seam.
    private abstract class NativeCommands
    {
        internal abstract SourceStartupRequest Startup { get; }
        internal abstract NativeSourceOwnerProofMessage Proof { get; }
        internal abstract Task<Exception?> Retirement { get; }
        internal abstract Task<NativeSourceOwnerResponseMessage> Start(IReadOnlyList<KeyValuePair<string, string>> environment);
        internal abstract Task<NativeSourceOwnerResponseMessage> Status();
        internal abstract Task<NativeSourceOwnerResponseMessage> Stop();
        internal abstract Task Shutdown();
        internal abstract void RequirePrivacy(SupportPrivacy privacy);
    }
    private sealed class AcceptedCommands(NativeSourceOwnerConnection connection) : NativeCommands
    {
        internal override SourceStartupRequest Startup => connection.StartupRequest;
        internal override NativeSourceOwnerProofMessage Proof => connection.OwnerMessage;
        internal override Task<Exception?> Retirement => connection.RetirementStarted;
        internal override Task<NativeSourceOwnerResponseMessage> Start(IReadOnlyList<KeyValuePair<string, string>> environment) => connection.StartAsync(environment, CancellationToken.None);
        internal override Task<NativeSourceOwnerResponseMessage> Status() => connection.StatusAsync(CancellationToken.None);
        internal override Task<NativeSourceOwnerResponseMessage> Stop() => connection.StopAsync();
        internal override async Task Shutdown() => _ = await connection.ShutdownAsync().ConfigureAwait(false);
        internal override void RequirePrivacy(SupportPrivacy privacy) => connection.RequireDiagnosticPrivacy(privacy);
    }
    private sealed class ControlledCommands(NativeSourceOwnerConnection.ControlledHarness harness, NativeSourceOwnerConnection.BootMessage boot) : NativeCommands
    {
        internal override SourceStartupRequest Startup => boot.StartupRequest;
        internal override NativeSourceOwnerProofMessage Proof => boot.ExpectedOwner ?? throw new InvalidDataException("Controlled integrated boot needs an owner");
        internal override Task<Exception?> Retirement => harness.RetirementStarted;
        internal override Task<NativeSourceOwnerResponseMessage> Start(IReadOnlyList<KeyValuePair<string, string>> environment) => harness.StartAsync(environment, CancellationToken.None);
        internal override Task<NativeSourceOwnerResponseMessage> Status() => harness.StatusAsync(CancellationToken.None);
        internal override Task<NativeSourceOwnerResponseMessage> Stop() => harness.StopAsync();
        internal override async Task Shutdown() => _ = await harness.ShutdownAsync().ConfigureAwait(false);
        internal override void RequirePrivacy(SupportPrivacy privacy) => harness.RequireDiagnosticPrivacy(privacy);
    }

    /// <summary>
    /// Transfers ownership of the actual accepted channel. No JVM, network or repair readiness wait
    /// occurs here. Any construction failure is fatal startup; cleanup must not be caught to continue.
    /// </summary>
    internal static Task<SourceEngineController> CreateIntegratedAsync(AutoSourcarrSettings settings, NativeSourceOwnerConnection accepted,
        SourceEngineEnvironment environment, IReadOnlyDictionary<string, string?> parentEnvironment, SemaphoreSlim sharedSourceAdmin,
        BoundedWorkScheduler io, SettingsRepository settingsStore, SupportPrivacy privacy, ErrorRegistry errors)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        return CreateAsync(settings, new AcceptedCommands(accepted), environment, parentEnvironment, sharedSourceAdmin,
            io, settingsStore, privacy, errors, TimeProvider.System, null, "", "");
    }
    /// <summary>After the host's valid external boot acceptance. Creates no local process or native generation.</summary>
    internal static Task<SourceEngineController> CreateExternalAsync(AutoSourcarrSettings settings, SourceEngineEnvironment environment,
        IReadOnlyDictionary<string, string?> parentEnvironment, SemaphoreSlim sharedSourceAdmin, BoundedWorkScheduler io,
        SettingsRepository settingsStore, SupportPrivacy privacy, ErrorRegistry errors, string username = "", string password = "")
        => CreateAsync(settings, null, environment, parentEnvironment, sharedSourceAdmin, io, settingsStore,
            privacy, errors, TimeProvider.System, null, username, password);

    private SourceEngineController(AutoSourcarrSettings settings, NativeCommands? native, SourceEngineEnvironment environment,
        IReadOnlyDictionary<string, string?> parentEnvironment, SemaphoreSlim sharedSourceAdmin, BoundedWorkScheduler io,
        SupportPrivacy privacy, ErrorRegistry errors, TimeProvider clock)
    {
        _settings = settings; _native = native; _environment = environment;
        _parent = parentEnvironment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        _admin = sharedSourceAdmin; _io = io; _privacy = privacy; _errors = errors; _clock = clock;
        _state = native is null ? "external" : "stopped";
    }
    private static async Task<SourceEngineController> CreateAsync(AutoSourcarrSettings settings, NativeCommands? native,
        SourceEngineEnvironment environment, IReadOnlyDictionary<string, string?> parentEnvironment, SemaphoreSlim sharedSourceAdmin,
        BoundedWorkScheduler io, SettingsRepository settingsStore, SupportPrivacy privacy, ErrorRegistry errors, TimeProvider clock,
        ISourceTransportFactory? factory, string username, string password)
    {
        SourceEngineController? controller = null;
        try
        {
            ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(environment);
            ArgumentNullException.ThrowIfNull(parentEnvironment); ArgumentNullException.ThrowIfNull(sharedSourceAdmin);
            ArgumentNullException.ThrowIfNull(io); ArgumentNullException.ThrowIfNull(settingsStore);
            ArgumentNullException.ThrowIfNull(privacy); ArgumentNullException.ThrowIfNull(errors); ArgumentNullException.ThrowIfNull(clock);
            if (settings.SourceEngineMode is not ("integrated" or "external")
                || (settings.SourceEngineMode == "integrated") != (native is not null))
                throw new ArgumentException("Native ownership must match the selected source mode");
            native?.Startup.RequireSameRequest(SourceStartupRequest.FromSettings(settings));
            environment.RequirePrivacy(privacy); native?.RequirePrivacy(privacy);
            var identity = native is null ? SourceEngineCapabilityIdentity.ConfiguredExternal(settings.EffectiveSourceUrl, username, password)
                : SourceEngineCapabilityIdentity.IntegratedSource();
            controller = new(settings, native, environment, parentEnvironment, sharedSourceAdmin, io, privacy, errors, clock);
            controller._client = new(settings, native is null ? settings.EffectiveSourceUrl : IntegratedUrl,
                workLimit: settings.SourceHostWorkConcurrency, runtime: controller,
                username: native is null ? username : "", password: native is null ? password : "", privacy: privacy, transportFactory: factory);
            controller._capabilities = new(settingsStore, controller.Client, identity, privacy, "source", clock);
            if (native is not null) _ = controller.Client.BeginTransportPause();
            return controller;
        }
        catch (Exception primary)
        {
            try
            {
                if (controller is not null) await controller.DisposeAsync().ConfigureAwait(false);
                else if (native is not null) await native.Shutdown().ConfigureAwait(false);
            }
            catch (Exception cleanup) { throw new AggregateException("Source controller construction and cleanup failed", primary, cleanup); }
            throw;
        }
    }
    internal Snapshot Cached()
    {
        lock (_gate)
        {
            var ready = _ready && !_stopping && _native is not null && !_native.Retirement.IsCompleted;
            return new(_settings.SourceEngineMode, _state, ready, _native is null ? null : _generation,
                _restarts, _error, _observedVersion, false, _challengeState,
                _capabilityReport is null ? null : (JsonObject)_capabilityReport.DeepClone(),
                ready && _capabilityGeneration == _generation, _capabilityError);
        }
    }
    /// <summary>Integrated consumers only. External mode has no fabricated local generation.</summary>
    internal (bool Ready, long Generation) IntegratedCurrentState()
    {
        lock (_gate)
        {
            if (_native is null) throw new SourceEngineUnavailableException("External mode has no local native generation");
            if (_generation > long.MaxValue) throw new SourceEngineUnavailableException("Native generation exceeds this consumer's supported range");
            return (_ready && !_stopping && !_native.Retirement.IsCompleted, checked((long)_generation));
        }
    }
    /// <summary>Private configuration intent for a future real challenge owner; never a configured/working observation.</summary>
    internal IReadOnlyDictionary<string, string> ChallengeIntent(ulong expectedGeneration)
    {
        lock (_gate)
        {
            if (_native is null || !_ready || _stopping || _native.Retirement.IsCompleted || _generation != expectedGeneration || _intendedChallenges is null)
                throw new SourceEngineUnavailableException("Challenge configuration intent is not current");
            return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                _intendedChallenges.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }
    }
    internal Task StartAsync()
    {
        lock (_gate)
        {
            if (_stopping) throw new OperationCanceledException(_lifetime.Token);
            if (_started) return Task.CompletedTask;
            _started = true;
            if (_native is not null) { _state = "starting"; _loop = RunAsync(); _retirement = WatchRetirementAsync(); }
            return Task.CompletedTask;
        }
    }
    public Task EnsureAvailableAsync(string requestPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); bool ready, retired;
        lock (_gate)
        {
            if (_stopping) throw new OperationCanceledException(_lifetime.Token);
            if (_native is null) return Task.CompletedTask; // original external mode bypasses local management
            retired = _native.Retirement.IsCompleted; ready = _ready && !retired;
        }
        if (retired) Terminal(new NativeRetired(new IOException("Native source ownership retired")));
        // This runs from an already admitted HTTP call. Never wait for that same epoch to drain.
        if (!ready)
        {
            RecordUnavailable(requestPath);
            Wake(); // advisory/coalesced; only a changed request revision can supersede an attempt
            throw new SourceEngineUnavailableException("Integrated source engine is starting or unavailable; cached data remains available");
        }
        return Task.CompletedTask;
    }
    /// <summary>Cancellation is checked before admission; admitted transitions stay owned through physical completion.</summary>
    internal Task<EnginePause> RestartAsync(CancellationToken caller = default) => RequestAsync(RequestKind.Restart, caller);
    internal Task<EnginePause> PauseAndStopEngineAsync(CancellationToken caller = default) => RequestAsync(RequestKind.Pause, caller);
    private Task<EnginePause> RequestAsync(RequestKind kind, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested(); _ = StartAsync(); Request admitted;
        lock (_gate)
        {
            if (_stopping) throw new OperationCanceledException(_lifetime.Token);
            if (_native is null) throw new NotSupportedException("External mode owns no local engine to restart or stop");
            if (_request is { } pending)
            {
                if (pending.Kind != kind) throw new SourceWorkDeferredException("Another source lifecycle request is already admitted");
                return pending.Completion.Task;
            }
            admitted = _request = new(kind, _requests.Enter()); checked { _revision++; }
            _ready = false; _maintenance = true;
        }
        try { _ = Client.BeginTransportPause(); }
        catch (Exception error) { Terminal(error); }
        Wake(); return admitted.Completion.Task;
    }
    private void Wake()
    {
        lock (_gate) if (!_stopping && !_disposed && _wake.CurrentCount == 0) _wake.Release();
    }
    private void RecordCleanup(Exception error)
    { lock (_gate) if (!_cleanup.Any(existing => ReferenceEquals(existing, error))) _cleanup.Add(error); }
    private void FenceForStop()
    {
        if (_client is null || _native is null) return;
        try { _ = Client.BeginTransportPause(); } catch (Exception error) { RecordCleanup(error); }
    }
    private async Task<SourceTransportDrain> PauseHttpAsync()
    {
        var pause = Client.BeginTransportPause();
        return await Client.DrainTransportAsync(pause).ConfigureAwait(false);
    }
    private async Task ResumeHttpAsync(SourceTransportDrain drain, long revision)
    {
        // Canceling only the waiter would detach a possible new pool publication. Join it, then fence stale work.
        await Client.ResumeTransportAsync(drain).ConfigureAwait(false);
        try { RequireRevision(revision); }
        catch (Exception primary)
        {
            try { _ = Client.BeginTransportPause(); }
            catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
            throw;
        }
    }
    private void RequireRevision(long revision)
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_fatalError is not null) throw new NativeRetired(_fatalError);
            if (_native!.Retirement.IsCompleted) throw new NativeRetired(new IOException("Native source ownership retired"));
            if (_revision != revision) throw new SupersededAttempt();
        }
    }
    private NativeSourceOwnerResponseMessage ValidateStop(NativeSourceOwnerResponseMessage response)
    {
        if (_native is null || response.Root != _native.Proof.Root || response.Result != NativeSourceOwnerResult.Ok
            || response.State != NativeSourceOwnerState.Idle || response.Diagnostic != NativeSourceOwnerDiagnostic.None
            || (response.Facts & (NativeSourceOwnerFacts.NoChildren | NativeSourceOwnerFacts.StreamsDrained)) != (NativeSourceOwnerFacts.NoChildren | NativeSourceOwnerFacts.StreamsDrained)
            || (response.Facts & (NativeSourceOwnerFacts.Failed | NativeSourceOwnerFacts.PhysicalTest)) != 0
            || (response.LeaderPid != 0 && (response.Facts & NativeSourceOwnerFacts.Exit) == 0))
            throw new IOException("Native engine stop did not establish its physical facts");
        lock (_gate)
        {
            if (response.Generation < _generation) throw new IOException("Native engine generation regressed");
            _generation = response.Generation;
        }
        return response;
    }
    private async Task<(SourceTransportDrain Drain, NativeSourceOwnerResponseMessage Stop)> StopEngineAsync()
    {
        SourceTransportDrain? drain = null; NativeSourceOwnerResponseMessage? stopped = null; Exception? failure = null;
        try { drain = await PauseHttpAsync().ConfigureAwait(false); } catch (Exception error) { failure = error; }
        // A failed drain still joins the epoch's physical closure. Stop the owned tree, retaining both failures;
        // no drain-error path is allowed to launch/resume or claim a clean transition.
        try { stopped = ValidateStop(await _native!.Stop().ConfigureAwait(false)); }
        catch (Exception error) { failure = Retain(failure, error); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return (drain ?? throw new IOException("HTTP drain is unavailable"), stopped ?? throw new IOException("Native stop observation is unavailable"));
    }
    private async Task<JsonNode?> AboutAsync(TimeSpan budget)
    {
        SourceTransportSession? session = null; JsonNode? result = null; Exception? failure = null;
        using var timer = new CancellationTokenSource(budget, _clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, _lifetime.Token);
        try
        {
            session = Client.OpenSession(_lifetime.Token);
            result = await session.ReadAboutAsync(null, null, deadline.Token).ConfigureAwait(false);
            session.ThrowIfRetired(); deadline.Token.ThrowIfCancellationRequested();
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (session is not null) try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { failure = Retain(failure, cleanup); }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
    /// <summary>Only a direct typed socket refusal proves no listener; messages, HTTP failures and deadlines do not.</summary>
    internal static bool DirectRefusal(Exception error) => error is HttpRequestException { StatusCode: null, InnerException: SocketException socket }
        && socket.SocketErrorCode == SocketError.ConnectionRefused;
    private async Task RequireVacantAsync(long revision)
    {
        try { _ = await AboutAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (Exception error) when (DirectRefusal(error)) { RequireRevision(revision); return; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            RequireRevision(revision);
            throw new PortUnavailable("Loopback source port is occupied or its state is unknown; no second engine was started", error);
        }
        RequireRevision(revision);
        throw new PortUnavailable("Loopback source port is already in use; no second engine was started");
    }
    private async Task RunAsync()
    {
        await Task.Yield(); var backoff = 2;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                Request? request; bool held; long revision;
                lock (_gate) { request = _request; held = _hold; revision = _revision; }
                if (held && request is null) { await WaitAsync(Timeout.InfiniteTimeSpan, revision).ConfigureAwait(false); continue; }
                if (request?.Kind == RequestKind.Pause)
                {
                    await PauseRequestAsync(request, revision).ConfigureAwait(false); continue;
                }
                var started = _clock.GetTimestamp(); var superseded = false; var setupRequired = false; Exception? primary = null;
                try { await AttemptAsync(request, revision).ConfigureAwait(false); }
                catch (SupersededAttempt) { superseded = true; }
                catch (SetupRequired error) { primary = error; setupRequired = true; State("setup-required", error); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    primary = error;
                    if (error is NativeRetired || _native!.Retirement.IsCompleted) Terminal(error);
                    else State("restarting", error);
                }
                finally
                {
                    try { await CleanupEngineAsync().ConfigureAwait(false); }
                    catch (Exception cleanup) { Terminal(Retain(primary, cleanup)); }
                }
                if (_lifetime.IsCancellationRequested) break;
                lock (_gate) superseded |= _revision != revision;
                if (superseded) continue; // a later request is completed only by its own transition
                if (setupRequired)
                {
                    CompleteRequest(request, primary ?? new SetupRequired("The fixed source launcher is unavailable"));
                    await WaitAsync(Timeout.InfiniteTimeSpan, revision).ConfigureAwait(false); backoff = 2; continue;
                }
                if (_clock.GetElapsedTime(started) > TimeSpan.FromSeconds(120)) backoff = 2;
                await WaitAsync(TimeSpan.FromSeconds(backoff), revision).ConfigureAwait(false);
                lock (_gate) if (_revision == revision) _restarts++;
                backoff = Math.Min(backoff * 2, 60);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Terminal(error); }
        finally
        {
            try { await CleanupEngineAsync().ConfigureAwait(false); } catch (Exception error) { RecordCleanup(error); }
            Request? pending; lock (_gate) pending = _request;
            CompleteRequest(pending, _fatalError ?? new OperationCanceledException(_lifetime.Token));
        }
    }
    private async Task PauseRequestAsync(Request request, long revision)
    {
        await _admin.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            RequireRevision(revision); var result = await StopEngineAsync().ConfigureAwait(false); RequireRevision(revision);
            lock (_gate)
            {
                if (_revision != revision || _stopping) throw new SupersededAttempt();
                _hold = true; _ready = false; _maintenance = true; _state = "stopped"; _error = "";
                CompleteRequest(request, null, result.Stop);
            }
        }
        finally { _admin.Release(); }
    }
    private async Task AttemptAsync(Request? request, long revision)
    {
        var native = _native ?? throw new InvalidOperationException("Integrated lifecycle has no accepted native owner");
        lock (_gate)
        {
            if (_revision != revision) throw new SupersededAttempt();
            _ready = false; _maintenance = true; _hold = false; _state = "starting"; _error = "";
        }
        NativeSourceOwnerResponseMessage launched;
        await _admin.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            RequireRevision(revision); var previous = await StopEngineAsync().ConfigureAwait(false);
            await ResumeHttpAsync(previous.Drain, revision).ConfigureAwait(false);
            await RequireVacantAsync(revision).ConfigureAwait(false);
            var probeDrain = await PauseHttpAsync().ConfigureAwait(false);
            SourceEngineLaunchEnvironment environment;
            try { environment = await _io.RunAsync(() => _environment.BuildNativeLaunch(_parent, _settings), _lifetime.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException)
            { throw new SetupRequired("The fixed source launch environment is invalid; correct source settings before retrying", error); }
            RequireRevision(revision); native.Startup.RequireSameRequest(SourceStartupRequest.FromSettings(_settings));
            // Native requests have their own finite actor budget. Do not cancel START into a native STOP before HTTP drain.
            launched = await native.Start(environment.Launch.ToArray()).ConfigureAwait(false); RequireRevision(revision);
            if (launched.Result == NativeSourceOwnerResult.Unavailable)
            {
                if (launched.Root != native.Proof.Root || launched.Generation != _generation || launched.State != NativeSourceOwnerState.Idle
                    || launched.Diagnostic != NativeSourceOwnerDiagnostic.Asset || (launched.Facts & (NativeSourceOwnerFacts.Failed | NativeSourceOwnerFacts.PhysicalTest)) != 0)
                    throw new NativeRetired(new IOException("Native unavailable-launch identity or state is inconsistent"));
                throw new SetupRequired("The fixed source launcher or checked environment is unavailable; rebuild/redeploy the qualified image or correct source settings");
            }
            if (launched.Result != NativeSourceOwnerResult.Ok || launched.State != NativeSourceOwnerState.Running
                || launched.Root != native.Proof.Root || launched.Diagnostic != NativeSourceOwnerDiagnostic.None
                || launched.LeaderPid <= 1 || launched.ProcessGroupId != launched.LeaderPid
                || (launched.Facts & (NativeSourceOwnerFacts.Failed | NativeSourceOwnerFacts.PhysicalTest | NativeSourceOwnerFacts.NoChildren)) != 0)
                throw new NativeRetired(new IOException("Native engine launch did not establish a running replacement"));
            lock (_gate)
            {
                if (launched.Generation <= _generation) throw new NativeRetired(new IOException("Native engine launch did not advance its generation"));
                _generation = launched.Generation;
                _intendedChallenges = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                    environment.IntendedChallenges.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            }
            await ResumeHttpAsync(probeDrain, revision).ConfigureAwait(false);
        }
        finally { _admin.Release(); }
        while (true)
        {
            RequireRevision(revision); var observed = await native.Status().ConfigureAwait(false);
            RequireSameNativeGeneration(observed, launched);
            if (observed.State == NativeSourceOwnerState.Idle) throw new IOException("Source engine exited with code " + observed.ExitStatus + "; source data was retained");
            if (observed.State != NativeSourceOwnerState.Running || observed.Result != NativeSourceOwnerResult.Ok) throw new IOException("Source engine is not running");
            try
            {
                var about = await AboutAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var version = SourceEngineCapabilityContract.IdentifyVersion(about); // exact narrow hook listed separately
                var after = await native.Status().ConfigureAwait(false); RequireRevision(revision);
                RequireSameNativeGeneration(after, launched);
                if (after.State != NativeSourceOwnerState.Running || after.Result != NativeSourceOwnerResult.Ok)
                    throw new IOException("Source engine exited during readiness observation");
                bool first;
                lock (_gate)
                {
                    if (_revision != revision || _stopping) throw new SupersededAttempt();
                    if (native.Retirement.IsCompleted) throw new NativeRetired(new IOException("Native source owner retired during readiness observation"));
                    first = !_ready; _ready = true; _maintenance = false; _state = "ready"; _error = ""; _observedVersion = version;
                    if (!_settings.SourceAutoChallenges) _challengeState = "disabled";
                    if (first) CompleteRequest(request, null);
                }
                try { _errors.Resolve(key: "integrated-source-engine"); } catch (Exception diagnostic) { RecordCleanup(diagnostic); }
                if (first) BeginCapabilities(revision, launched.Generation);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
            catch (SupersededAttempt) { throw; }
            catch (NativeRetired) { throw; }
            catch (Exception error)
            {
                RequireRevision(revision);
                lock (_gate)
                {
                    // The released RequestError path leaves an already unresponsive engine unresponsive.
                    if (error is HttpRequestException { StatusCode: null } or TimeoutException or OperationCanceledException)
                    { if (_state == "ready") _state = "unresponsive"; }
                    else _state = _state == "ready" ? "unresponsive" : "starting";
                    _ready = false; _error = Safe(error);
                }
            }
            _ = await WaitAsync(TimeSpan.FromSeconds(3), revision).ConfigureAwait(false);
            // Wakeups carry no authority. Revision, not semaphore timing, supersedes this attempt.
        }
    }
    private void RequireSameNativeGeneration(NativeSourceOwnerResponseMessage response, NativeSourceOwnerResponseMessage launched)
    {
        if (_native is null || response.Root != _native.Proof.Root || response.Generation != launched.Generation
            || response.State == NativeSourceOwnerState.Running && (response.LeaderPid != launched.LeaderPid || response.ProcessGroupId != launched.ProcessGroupId
                || response.Diagnostic != NativeSourceOwnerDiagnostic.None || (response.Facts & (NativeSourceOwnerFacts.Failed | NativeSourceOwnerFacts.PhysicalTest | NativeSourceOwnerFacts.NoChildren)) != 0))
            throw new NativeRetired(new IOException("Native source identity/generation changed outside its owner"));
    }
    private void BeginCapabilities(long revision, ulong generation)
    {
        lock (_gate)
        {
            // A completed observation discarded while unready is not a current report.
            // This method is called only at a real transition back to readiness; keep
            // active checks serialized and reuse a valid report for this generation.
            if (_stopping || _capabilityGeneration == generation
                || _capabilityRequestedGeneration == generation && _capability is { IsCompleted: false }) return;
            _capabilityRequestedGeneration = generation;
            _capability = CheckCapabilitiesAsync(_capability, revision, generation);
        }
    }
    private async Task CheckCapabilitiesAsync(Task? previous, long revision, ulong generation)
    {
        await Task.Yield();
        try
        {
            if (previous is not null) await previous.ConfigureAwait(false);
            RequireRevision(revision);
            lock (_gate) if (!_ready || _generation != generation || _stopping) return;
            var report = await _capabilities!.CheckAsync(_lifetime.Token).ConfigureAwait(false);
            lock (_gate) if (_revision == revision && _ready && _generation == generation && !_stopping)
            { _capabilityReport = (JsonObject)report.DeepClone(); _capabilityGeneration = generation; _capabilityError = ""; }
        }
        catch (Exception error)
        {
            // Shape absence is separate evidence, never a readiness success or source outage claim.
            lock (_gate) if (_revision == revision && !_stopping) _capabilityError = Safe(error);
        }
    }
    private async Task CleanupEngineAsync()
    {
        lock (_gate) { _ready = false; _maintenance = true; _intendedChallenges = null; }
        // This path is also reached by automatic exit/error handling, without RequestAsync or StopAsync.
        // An admin holder may itself be awaiting engine HTTP: fence/cancel that epoch before waiting
        // for admin admission. A failed fence is retained and makes this transition terminal.
        Exception? failure = null;
        try { _ = Client.BeginTransportPause(); } catch (Exception error) { failure = error; }
        var entered = false;
        try
        {
            await _admin.WaitAsync().ConfigureAwait(false); entered = true;
            try { _ = await StopEngineAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { failure = Retain(failure, cleanup); }
        }
        catch (Exception admission) { failure = Retain(failure, admission); }
        finally
        {
            if (entered) try { _admin.Release(); } catch (Exception release) { failure = Retain(failure, release); }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private void State(string state, Exception error)
    {
        lock (_gate) { _ready = false; _state = state; _error = Safe(error); }
        RecordUnavailable();
    }
    private void RecordUnavailable(string path = "")
    {
        string state, error; lock (_gate) { state = _state; error = _error; }
        try
        {
            _errors.Record("Integrated source engine", error.Length == 0 ? "Integrated source engine is starting; retry shortly." : error,
                key: "integrated-source-engine", pinned: true, statusCode: 503,
                errorType: state == "setup-required" ? "MissingIntegratedRuntime" : "SourceEngineUnavailable", path: path,
                detail: "Source requests are deferred locally. Cached data remains available. This shared runtime problem does not indicate a failure of individual source websites.");
        }
        catch (Exception diagnostic) { RecordCleanup(diagnostic); }
    }
    private string Safe(Exception error) => Clip(_privacy.Text(error.GetType().Name + ": "
        + (error is SetupRequired or PortUnavailable or NativeRetired ? error.Message
            : "Source engine observation or lifecycle operation failed; cached data remains available.")), 1800);
    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length];
    private static Exception Retain(Exception? primary, Exception cleanup) => primary is null ? cleanup
        : ReferenceEquals(primary, cleanup) ? primary : new AggregateException(primary, cleanup);
    private void CompleteRequest(Request? expected, Exception? error, NativeSourceOwnerResponseMessage? stopped = null)
    {
        if (expected is null) return;
        EnginePause? result = null;
        lock (_gate)
        {
            if (!ReferenceEquals(_request, expected)) return;
            if (error is null) result = new(Cached(), stopped);
            _request = null;
        }
        try
        {
            if (error is null) expected.Completion.TrySetResult(result!);
            else if (error is OperationCanceledException canceled) expected.Completion.TrySetCanceled(canceled.CancellationToken);
            else expected.Completion.TrySetException(error);
        }
        finally { expected.Dispose(); }
    }
    private async Task<bool> WaitAsync(TimeSpan delay, long revision)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var timer = Task.Delay(delay, _clock, cancel.Token); Task? wake = null;
        try
        {
            while (true)
            {
                lock (_gate) if (_revision != revision) return true;
                wake = _wake.WaitAsync(cancel.Token);
                var winner = await Task.WhenAny(timer, wake).ConfigureAwait(false);
                _lifetime.Token.ThrowIfCancellationRequested();
                if (ReferenceEquals(winner, timer)) { await timer.ConfigureAwait(false); return false; }
                await wake.ConfigureAwait(false); wake = null;
                // Ignore signals for a revision already captured by this operation.
            }
        }
        finally
        {
            await cancel.CancelAsync().ConfigureAwait(false);
            try { await timer.ConfigureAwait(false); } catch (OperationCanceledException) { }
            if (wake is not null) try { await wake.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }
    private async Task WatchRetirementAsync()
    {
        await Task.Yield(); var error = await _native!.Retirement.ConfigureAwait(false);
        lock (_gate) if (_stopping) return;
        Terminal(new NativeRetired(error ?? new IOException("Native source owner retired outside host shutdown")));
    }
    private void Terminal(Exception error)
    {
        lock (_gate)
        {
            _fatalError = Retain(_fatalError, error); _ready = false; _state = "stopped"; _error = Safe(error);
        }
        _fatal.TrySetResult(error);
        RecordUnavailable();
        _ = StopAsync(); // tracked stop joins this loop; it never waits under the gate
    }
    internal Task StopAsync()
    {
        TaskCompletionSource complete;
        lock (_gate)
        {
            if (_stop is not null) return _stop;
            _stopping = true; _ready = false; _maintenance = true; unchecked { _revision++; }
            complete = new(TaskCreationOptions.RunContinuationsAsynchronously); _stop = complete.Task;
        }
        FenceForStop(); _ = StopCoreAsync(complete); return complete.Task;
    }
    private async Task StopCoreAsync(TaskCompletionSource complete)
    {
        await Task.Yield(); var errors = new List<Exception>();
        try
        {
            try { await _lifetime.CancelAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            Task? loop, capability; lock (_gate) { loop = _loop; capability = _capability; }
            if (loop is not null) try { await loop.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            else if (_native is not null && _client is not null)
                try { await CleanupEngineAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            Request? pending; lock (_gate) pending = _request;
            CompleteRequest(pending, new OperationCanceledException(_lifetime.Token));
            if (capability is not null) try { await capability.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { await _requests.StopAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            if (_capabilities is not null) try { await _capabilities.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            if (_client is not null) try { await _client.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            if (_native is not null) try { await _native.Shutdown().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            Task? retirement; lock (_gate) retirement = _retirement;
            if (retirement is not null) try { await retirement.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            lock (_gate)
            {
                if (_fatalError is not null) errors.Add(_fatalError);
                errors.AddRange(_cleanup); _state = _native is null ? "external" : "stopped";
            }
            if (errors.Count == 0) complete.TrySetResult();
            else complete.TrySetException(new AggregateException("Source controller shutdown failed", errors));
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_dispose ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await Task.Yield(); Exception? failure = null;
        try { await StopAsync().ConfigureAwait(false); } catch (Exception error) { failure = error; }
        try { await _requests.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failure = Retain(failure, error); }
        lock (_gate) _disposed = true;
        try { _lifetime.Dispose(); } catch (Exception error) { failure = Retain(failure, error); }
        try { _wake.Dispose(); } catch (Exception error) { failure = Retain(failure, error); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Uses the actual controlled actor and fresh controlled HTTP; cannot mint native authority.</summary>
    internal sealed class ControlledHarness : IAsyncDisposable
    {
        private readonly SourceEngineController _controller;
        private ControlledHarness(SourceEngineController controller) => _controller = controller;
        internal static async Task<ControlledHarness> CreateAsync(AutoSourcarrSettings settings, NativeSourceOwnerConnection.ControlledHarness owner,
            NativeSourceOwnerConnection.BootMessage boot, SourceEngineEnvironment environment, IReadOnlyDictionary<string, string?> parent,
            SemaphoreSlim admin, BoundedWorkScheduler io, SettingsRepository store, SupportPrivacy privacy, ErrorRegistry errors, TimeProvider clock, ISourceTransportFactory factory)
        {
            await owner.Acceptance.ConfigureAwait(false);
            return new(await SourceEngineController.CreateAsync(settings, new ControlledCommands(owner, boot), environment,
                parent, admin, io, store, privacy, errors, clock, factory, "", "").ConfigureAwait(false));
        }
        internal static async Task<ControlledHarness> CreateExternalAsync(AutoSourcarrSettings settings, SourceEngineEnvironment environment,
            IReadOnlyDictionary<string, string?> parent, SemaphoreSlim admin, BoundedWorkScheduler io, SettingsRepository store,
            SupportPrivacy privacy, ErrorRegistry errors, TimeProvider clock, ISourceTransportFactory factory)
            => new(await SourceEngineController.CreateAsync(settings, null, environment, parent, admin, io, store,
                privacy, errors, clock, factory, "", "").ConfigureAwait(false));
        internal SuwayomiClient Client => _controller.Client;
        internal Snapshot Status => _controller.Cached();
        internal Task<Exception> FatalFailure => _controller.FatalFailure;
        // Read-only controlled test barrier. It neither starts an observation nor grants native/readiness authority.
        internal Task CapabilityObservation { get { lock (_controller._gate) return _controller._capability ?? Task.CompletedTask; } }
        internal Task StartAsync() => _controller.StartAsync();
        internal Task<EnginePause> RestartAsync(CancellationToken caller = default) => _controller.RestartAsync(caller);
        internal Task<EnginePause> PauseAsync(CancellationToken caller = default) => _controller.PauseAndStopEngineAsync(caller);
        internal Task StopAsync() => _controller.StopAsync();
        internal (bool Ready, long Generation) State() => _controller.IntegratedCurrentState();
        internal IReadOnlyDictionary<string, string> ChallengeIntent(ulong generation) => _controller.ChallengeIntent(generation);
        public ValueTask DisposeAsync() => _controller.DisposeAsync();
    }
}
