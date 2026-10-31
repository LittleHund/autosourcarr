using System.Text.Json;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair.Runner.Isolation;
using AutoSourcarr.Core.Repair.Runner.Protocol;

namespace AutoSourcarr.Core.Repair.Runner.Execution;

public enum HardwareSessionState { Starting, NativeRestricted, GuestQualified, Stopping, Stopped, Faulted }

/// <summary>Owns native proof, guest protocol, cancellation and every physical process/stream until joined.</summary>
public sealed class HardwareVmSession : IAsyncDisposable
{
    private readonly IHardwareProcess _process;
    private readonly PreparedHardwareJob _prepared;
    private readonly HardwareSessionBudget _budget;
    private readonly Action<Exception?> _released;
    private readonly OperationOwner _operations = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _exit;
    private readonly object _gate = new();
    private CancellationTokenRegistration _lifetimeRegistration;
    private GuestConversation? _conversation;
    private Task? _stderr;
    private Task? _monitor;
    private Task? _stopping;
    private NativeLauncherProof? _native;
    private GuestSelfTestReport? _guest;
    private Exception? _failure;
    private int _state = (int)HardwareSessionState.Starting;
    private int _disposed;
    private int _physicalJoined;
    internal HardwareVmSession(IHardwareProcess process, PreparedHardwareJob prepared, HardwareSessionBudget budget, Action<Exception?> released)
    {
        _process = process; _prepared = prepared; _budget = budget; _released = released;
        _exit = Task.Run(() => process.WaitForExitAsync());
    }
    public HardwareSessionState State => (HardwareSessionState)Volatile.Read(ref _state);
    public int ProcessId => _process.Id;
    public NativeLauncherProof NativeProof => _native ?? throw new InvalidOperationException("Native isolation has not been established");
    public GuestSelfTestReport GuestReport => _guest ?? throw new InvalidOperationException("The current guest has not supplied its required self-test report");
    public GuestArtifactSession ArtifactSession => State == HardwareSessionState.GuestQualified && _conversation is not null ? _conversation.ArtifactSession : throw new InvalidOperationException("The current guest has no active qualified artifact identity");
    internal void ArmLifetime()
    {
        _lifetimeRegistration = _lifetime.Token.Register(static state => { var session = (HardwareVmSession)state!; session._failure = new TimeoutException("The hardware repair session reached its maximum lifetime"); session.RequestStop(); }, this);
        _lifetime.CancelAfter(_budget.MaximumLifetime);
        _monitor = MonitorExitAsync();
    }
    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var timer = new CancellationTokenSource(_budget.StartupTimeout);
        try
        {
            using var operation = _operations.Enter(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operations.CancellationToken, timer.Token);
            using var killOnCancellation = timeout.Token.Register(static state => ((HardwareVmSession)state!).RequestStop(), this);
            _native = await NativeLauncherProof.ReadAsync(_process.Error, _process.Id, _prepared.UserId, _prepared.Plan.ProofNonce, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested(); Publish(HardwareSessionState.NativeRestricted);
            _stderr = DrainErrorsAsync();
            _conversation = new GuestConversation(_process.Output, _process.Input);
            _guest = await _conversation.HandshakeAsync(_prepared.Assets, Path.GetFileName(_prepared.Plan.Workspace), _prepared.Budget, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested(); Publish(HardwareSessionState.GuestQualified);
        }
        catch (Exception error)
        {
            var expired = timer.IsCancellationRequested;
            _failure = error; await StopAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (expired) throw new TimeoutException("Hardware repair startup did not complete within its owned budget", error);
            throw;
        }
    }
    public async Task<JsonElement> RequestAsync(HardwareGuestOperation operation, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        using var timer = new CancellationTokenSource(_budget.RequestTimeout);
        try
        {
            using var owned = _operations.Enter(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operations.CancellationToken, timer.Token);
            using var killOnCancellation = timeout.Token.Register(static state => ((HardwareVmSession)state!).RequestStop(), this);
            if (State != HardwareSessionState.GuestQualified || _conversation is null || _native is null || _guest is null) throw new InvalidOperationException("The current hardware guest is not qualified for private inputs");
            var result = await _conversation.RequestAsync(operation, arguments, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (State != HardwareSessionState.GuestQualified) throw new OperationCanceledException(_operations.CancellationToken);
            return result;
        }
        catch (Exception error)
        {
            var expired = timer.IsCancellationRequested; var wasStopping = _operations.IsStopping;
            if (!wasStopping && !cancellationToken.IsCancellationRequested) _failure = error;
            await StopAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (expired) throw new TimeoutException("Hardware repair request reached its owned deadline", error);
            if (wasStopping) throw new OperationCanceledException(_operations.CancellationToken);
            throw;
        }
    }
    internal void RequestStop() { _ = StopAsync(); }
    public Task<GuestArtifactReceipt> UploadArtifactAsync(GuestArtifactDescriptor descriptor, IGuestArtifactLeaseProvider leases, CancellationToken cancellationToken = default) =>
        ArtifactAsync((conversation, token) => conversation.UploadArtifactAsync(descriptor, leases, token), cancellationToken);
    public Task<GuestArtifactReceipt> DownloadArtifactAsync(string artifactId, GuestArtifactDescriptor descriptor, IGuestArtifactLeaseProvider leases, CancellationToken cancellationToken = default) =>
        ArtifactAsync((conversation, token) => conversation.DownloadArtifactAsync(artifactId, descriptor, leases, token), cancellationToken);
    private async Task<GuestArtifactReceipt> ArtifactAsync(Func<GuestConversation, CancellationToken, Task<GuestArtifactReceipt>> transfer, CancellationToken cancellationToken)
    {
        using var timer = new CancellationTokenSource(_budget.RequestTimeout);
        try
        {
            using var owned = _operations.Enter(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operations.CancellationToken, timer.Token);
            using var killOnCancellation = timeout.Token.Register(static state => ((HardwareVmSession)state!).RequestStop(), this);
            if (State != HardwareSessionState.GuestQualified || _native is null || _guest is null || _conversation is null) throw new InvalidOperationException("Private artifact transfer requires the current native and guest contracts");
            var receipt = await transfer(_conversation, timeout.Token).ConfigureAwait(false); timeout.Token.ThrowIfCancellationRequested();
            if (State != HardwareSessionState.GuestQualified) throw new OperationCanceledException(_operations.CancellationToken);
            return receipt;
        }
        catch (Exception error)
        {
            var expired = timer.IsCancellationRequested; var wasStopping = _operations.IsStopping;
            if (!wasStopping && !cancellationToken.IsCancellationRequested) _failure = error;
            await StopAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (expired) throw new TimeoutException("Hardware artifact transfer reached its owned deadline", error);
            if (wasStopping) throw new OperationCanceledException(_operations.CancellationToken);
            throw;
        }
    }
    public Task StopAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_stopping is not null) return _stopping;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _stopping = completion.Task;
        }
        _ = CompleteStopAsync(completion); return completion.Task;
    }
    private void Publish(HardwareSessionState state)
    { lock (_gate) { if (_stopping is not null || _operations.IsStopping) throw new OperationCanceledException(_operations.CancellationToken); Volatile.Write(ref _state, (int)state); } }
    private async Task CompleteStopAsync(TaskCompletionSource completion)
    { try { await FinishStopAsync().ConfigureAwait(false); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } }
    private async Task FinishStopAsync()
    {
        Volatile.Write(ref _state, (int)HardwareSessionState.Stopping); var errors = new List<Exception>(); var joined = false;
        try
        {
            // Kill happens before joining protocol operations. Killing the owned peer
            // closes its pipe endpoints; cancelling an await alone would not do that.
            try { await _operations.StopAsync(_process.Kill).ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { await _exit.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            // A failed wait is not an exited PID. Keep the admission/physical ownership
            // until the owned process supplies actual exit evidence, even after an error.
            if (!_process.HasExited) await _process.WaitForExitAsync().ConfigureAwait(false);
            if (!_process.HasExited) await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.ConfigureAwait(false);
            joined = true;
            try { await (_stderr ?? DrainErrorsAsync()).ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { await DrainAsync(_process.Output, 1024 * 1024).ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { _conversation?.Dispose(); } catch (Exception error) { errors.Add(error); }
            foreach (var stream in new[] { _process.Input, _process.Output, _process.Error })
                try { await stream.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { _process.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { _prepared.Dispose(); } catch (Exception error) { errors.Add(error); }
            Volatile.Write(ref _physicalJoined, 1);
            try { await DisposeManagementAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        }
        finally
        {
            Volatile.Write(ref _state, (int)(errors.Count == 0 && _failure is null ? HardwareSessionState.Stopped : HardwareSessionState.Faulted));
            var failure = errors.Count == 0 ? null : new AggregateException("Hardware process cleanup failed after ownership was joined", errors);
            if (joined) _released(failure);
        }
        if (errors.Count != 0) throw new AggregateException("Hardware process cleanup failed", errors);
    }
    private async Task MonitorExitAsync()
    {
        try { await _exit.ConfigureAwait(false); }
        catch (Exception error) { _failure = error; }
        RequestStop();
    }
    private async Task DrainErrorsAsync()
    {
        try { await DrainAsync(_process.Error, 64 * 1024).ConfigureAwait(false); }
        catch (Exception error) { _failure = error; RequestStop(); throw; }
    }
    private static async Task DrainAsync(Stream stream, int maximum)
    {
        var buffer = new byte[4096]; var total = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false); if (count == 0) return;
            total = checked(total + count); if (total > maximum) throw new InvalidDataException("Hardware process diagnostic or remaining pipe data exceeds its discard bound");
        }
    }
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); if (_monitor is not null) await _monitor.ConfigureAwait(false); }
        finally
        {
            if (Volatile.Read(ref _physicalJoined) != 0) await DisposeManagementAsync().ConfigureAwait(false);
        }
    }
    private async Task DisposeManagementAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Unregister is non-blocking when maximum-lifetime cancellation itself initiated
        // this stop. Waiting synchronously for that same callback would deadlock it.
        _lifetimeRegistration.Unregister(); _lifetime.Dispose();
        await _operations.DisposeAsync().ConfigureAwait(false);
    }
}
