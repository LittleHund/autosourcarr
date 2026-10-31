using System.Diagnostics;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Jobs;

namespace AutoSourcarr.Core.Concurrency;

/// <summary>Whole-task lanes remain independent of browser idle and per-request source capacity.</summary>
public sealed class WholeTaskCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly OperationOwner _owner = new();
    private readonly SemaphoreSlim _manual;
    private readonly SemaphoreSlim _background;
    private readonly SemaphoreSlim _backgroundSource;
    private readonly TaskRegistry _tasks;
    private readonly double _quietSeconds;
    private readonly Func<double> _monotonic;
    private Task? _dispose;
    private int _interactive;
    private int _manualWaiting;
    private int _manualActive;
    private int _backgroundWaiting;
    private int _backgroundActive;
    private double _lastActivity;

    public WholeTaskCoordinator(AutoSourcarrSettings settings, TaskRegistry tasks, Func<double>? monotonic = null)
    {
        _tasks = tasks; _quietSeconds = Math.Max(0, settings.BackgroundIdleSeconds);
        _monotonic = monotonic ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        _lastActivity = _monotonic();
        var sourceCapacity = Math.Max(1, settings.SourceHostWorkConcurrency);
        var backgroundCapacity = Math.Max(1, Math.Min(settings.BackgroundSourceConcurrency, Math.Max(1, sourceCapacity - 1)));
        ManualCapacity = Math.Max(1, Math.Min(Math.Max(1, settings.ImportResolveConcurrency), Math.Max(1, sourceCapacity - backgroundCapacity - 1)));
        BackgroundCapacity = Math.Max(1, settings.BackgroundTaskConcurrency);
        _manual = new(ManualCapacity, ManualCapacity); _background = new(BackgroundCapacity, BackgroundCapacity);
        _backgroundSource = new(backgroundCapacity, backgroundCapacity);
    }
    public int ManualCapacity { get; }
    public int BackgroundCapacity { get; }
    public bool Stopping => _owner.IsStopping;
    public (int Interactive, int ManualWaiting, int ManualActive, int BackgroundWaiting, int BackgroundActive) Snapshot
    { get { lock (_gate) return (_interactive, _manualWaiting, _manualActive, _backgroundWaiting, _backgroundActive); } }
    public void NoteActivity() { lock (_gate) _lastActivity = _monotonic(); }
    public IDisposable BeginInteractive()
    {
        var lease = _owner.Enter();
        lock (_gate) { _interactive++; _lastActivity = _monotonic(); }
        return new Scope(() => { lock (_gate) { _interactive--; _lastActivity = _monotonic(); } lease.Dispose(); });
    }
    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default, double? quietSeconds = null, Func<bool>? promoted = null)
    {
        using var lease = _owner.Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken);
        var quiet = Math.Max(0, quietSeconds ?? _quietSeconds);
        while (quiet > 0 && promoted?.Invoke() != true)
        {
            double remaining;
            lock (_gate) remaining = _interactive > 0 ? .05 : quiet - (_monotonic() - _lastActivity);
            if (remaining <= 0) return;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(.25, Math.Max(.05, remaining))), linked.Token).ConfigureAwait(false);
        }
        linked.Token.ThrowIfCancellationRequested();
    }
    public Task<T> RunManualAsync<T>(Func<CancellationToken, Task<T>> operation, string? taskId = null, CancellationToken cancellationToken = default)
        => RunAsync(operation, true, false, false, taskId, cancellationToken);
    public Task<T> RunBackgroundAsync<T>(Func<CancellationToken, Task<T>> operation, bool waitForIdle = false, bool sourceLane = false,
        string? taskId = null, CancellationToken cancellationToken = default)
        => RunAsync(operation, false, waitForIdle, sourceLane, taskId, cancellationToken);

    /// <summary>Source subwork shares the actual background source capacity while
    /// remaining independent of whole-task scheduling and browser-idle admission.</summary>
    public async Task<T> RunBackgroundSourceAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var lease = _owner.Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken);
        await _backgroundSource.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            using var priority = SourceWorkContext.Background(true);
            return await operation(linked.Token).ConfigureAwait(false);
        }
        finally { _backgroundSource.Release(); }
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, bool manual, bool waitForIdle, bool sourceLane,
        string? taskId, CancellationToken cancellationToken)
    {
        using var lease = _owner.Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken);
        var token = linked.Token;
        if (taskId is not null) _tasks.MarkWaiting(taskId, manual ? "Waiting for another manual action" : "Waiting for a background task slot", queued: true);
        if (!manual && waitForIdle) await WaitForIdleAsync(token).ConfigureAwait(false);
        var semaphore = manual ? _manual : _background;
        lock (_gate) { if (manual) _manualWaiting++; else _backgroundWaiting++; }
        try { await semaphore.WaitAsync(token).ConfigureAwait(false); }
        finally { lock (_gate) { if (manual) _manualWaiting--; else _backgroundWaiting--; } }
        var ownsSource = false;
        lock (_gate) { if (manual) _manualActive++; else _backgroundActive++; }
        try
        {
            if (!manual && sourceLane)
            {
                if (taskId is not null) _tasks.MarkWaiting(taskId, "Waiting for source capacity");
                await _backgroundSource.WaitAsync(token).ConfigureAwait(false); ownsSource = true;
            }
            if (taskId is not null) _tasks.MarkActive(taskId, message: manual ? "Running manual action" : null);
            using var priority = SourceWorkContext.Background(!manual);
            return await operation(token).ConfigureAwait(false);
        }
        finally
        {
            if (ownsSource) _backgroundSource.Release();
            lock (_gate) { if (manual) _manualActive--; else _backgroundActive--; }
            semaphore.Release();
        }
    }
    public ValueTask DisposeAsync() { lock (_gate) return new(_dispose ??= CloseAsync()); }
    private async Task CloseAsync()
    {
        await _owner.DisposeAsync().ConfigureAwait(false);
        _manual.Dispose(); _background.Dispose(); _backgroundSource.Dispose();
    }
    private sealed class Scope(Action close) : IDisposable
    {
        private Action? _close = close;
        public void Dispose() => Interlocked.Exchange(ref _close, null)?.Invoke();
    }
}
