using System.Collections.Concurrent;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair.Runner.Isolation;

namespace AutoSourcarr.Core.Repair.Runner.Execution;

public sealed record HardwareSessionBudget(TimeSpan StartupTimeout, TimeSpan RequestTimeout, TimeSpan MaximumLifetime)
{
    public static HardwareSessionBudget Default { get; } = new(TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));
    public void Validate()
    {
        if (StartupTimeout <= TimeSpan.Zero || StartupTimeout > TimeSpan.FromMinutes(2) || RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(20) ||
            MaximumLifetime < StartupTimeout || MaximumLifetime > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(StartupTimeout));
    }
}

/// <summary>One owned hardware repair VM per instance. No public factory accepts executables, asset roots or a readiness flag.</summary>
public sealed class HardwareVmRuntime : IAsyncDisposable
{
    private readonly IHardwareEnvironment _environment;
    private readonly IHardwareProcessFactory _processes;
    private readonly Action<HardwareVmSession>? _sessionCreated;
    private readonly OperationOwner _owner = new();
    private readonly SemaphoreSlim _single = new(1, 1);
    private readonly ConcurrentDictionary<HardwareVmSession, byte> _sessions = new();
    private readonly ConcurrentQueue<Exception> _cleanupErrors = new();
    private readonly object _gate = new();
    private Task? _stopping;
    public HardwareVmRuntime(string instanceDataRoot) : this(new LinuxHardwareEnvironment(instanceDataRoot), new FixedHardwareProcessFactory()) { }
    internal HardwareVmRuntime(IHardwareEnvironment environment, IHardwareProcessFactory processes, Action<HardwareVmSession>? sessionCreated = null)
    { _environment = environment; _processes = processes; _sessionCreated = sessionCreated; }
    public async Task<HardwareVmSession> StartAsync(string jobId, VmBudget? machineBudget = null, HardwareSessionBudget? sessionBudget = null, CancellationToken cancellationToken = default)
    {
        LinuxHardwareEnvironment.ValidateJob(jobId); var budget = machineBudget ?? new VmBudget(); budget.Validate(); var time = sessionBudget ?? HardwareSessionBudget.Default; time.Validate();
        var lease = _owner.Enter(); var admitted = false; var transferred = false; PreparedHardwareJob? prepared = null; HardwareVmSession? session = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken);
        try
        {
            await _single.WaitAsync(linked.Token).ConfigureAwait(false); admitted = true;
            prepared = await _environment.PrepareAsync(jobId, budget, linked.Token).ConfigureAwait(false); linked.Token.ThrowIfCancellationRequested();
            // A cancellation cannot abandon physical Process.Start. Its resulting PID is
            // adopted immediately, then killed/joined if cancellation raced with startup.
            var process = await Task.Run(() => _processes.Start(prepared.Plan), CancellationToken.None).ConfigureAwait(false);
            session = new HardwareVmSession(process, prepared, time, error =>
            {
                if (session is not null) _sessions.TryRemove(session, out _);
                if (error is not null) _cleanupErrors.Enqueue(error);
                _single.Release(); lease.Dispose();
            });
            transferred = true; _sessions.TryAdd(session, 0); _sessionCreated?.Invoke(session); session.ArmLifetime();
            await session.InitializeAsync(linked.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_owner.IsStopping || session.State != HardwareSessionState.GuestQualified) throw new OperationCanceledException(_owner.CancellationToken);
            linked.Token.ThrowIfCancellationRequested(); return session;
        }
        catch
        {
            if (session is not null) await session.StopAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!transferred) { prepared?.Dispose(); if (admitted) _single.Release(); lease.Dispose(); }
        }
    }
    public Task StopAsync()
    {
        lock (_gate) return _stopping ??= StopCoreAsync();
    }
    private async Task StopCoreAsync()
    {
        await _owner.StopAsync(() => { foreach (var session in _sessions.Keys) session.RequestStop(); }).ConfigureAwait(false);
        if (!_cleanupErrors.IsEmpty) throw new AggregateException("Hardware repair cleanup retained failures", _cleanupErrors);
    }
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally { await _owner.DisposeAsync().ConfigureAwait(false); _single.Dispose(); }
    }
}
