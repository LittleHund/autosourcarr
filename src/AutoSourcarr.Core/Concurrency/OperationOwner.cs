namespace AutoSourcarr.Core.Concurrency;

/// <summary>Stops admission, cancels owned I/O, and joins every admitted operation.</summary>
public sealed class OperationOwner : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _token;
    private TaskCompletionSource _drained = Completed();
    private Task? _stopped;
    private int _active;
    private bool _closing;
    private int _disposed;

    public OperationOwner() => _token = _stop.Token;
    public CancellationToken CancellationToken => _token;
    public bool IsStopping => Volatile.Read(ref _closing);

    public IDisposable Enter()
    {
        lock (_gate)
        {
            if (_closing) throw new OperationCanceledException(_token);
            if (_active++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Lease(this);
        }
    }

    public Task StopAsync(Action? closeResources = null)
    {
        TaskCompletionSource finished;
        Task drain;
        lock (_gate)
        {
            if (_stopped is not null) return _stopped;
            Volatile.Write(ref _closing, true);
            finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopped = finished.Task;
            drain = _drained.Task;
        }
        // Resource callbacks can release leases or acquire their own locks.
        // Never call them while holding the admission lock.
        _ = FinishStopAsync(finished, drain, closeResources);
        return finished.Task;
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally { if (Interlocked.Exchange(ref _disposed, 1) == 0) _stop.Dispose(); }
    }

    private void Leave()
    {
        lock (_gate)
            if (--_active == 0) _drained.TrySetResult();
    }

    private sealed class Lease(OperationOwner owner) : IDisposable
    {
        private OperationOwner? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Leave();
    }

    private static TaskCompletionSource Completed()
    {
        var task = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        task.SetResult();
        return task;
    }
    private async Task FinishStopAsync(TaskCompletionSource finished, Task drain, Action? closeResources)
    {
        var errors = new List<Exception>();
        Task cancel = Task.CompletedTask;
        try { cancel = _stop.CancelAsync(); }
        catch (Exception error) { errors.Add(error); }
        try { closeResources?.Invoke(); }
        catch (Exception error) { errors.Add(error); }
        try { await cancel.ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        // Failed cancellation callbacks do not release ownership of physical I/O.
        await drain.ConfigureAwait(false);
        if (errors.Count == 0) finished.TrySetResult();
        else finished.TrySetException(errors);
    }
}
