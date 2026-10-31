namespace AutoSourcarr.Core.Concurrency;

public sealed class RequestAdmissionPausedException() : Exception("Database reset in progress; retry when it finishes");

/// <summary>Maintenance stops new catalogue handlers and joins every admitted response.</summary>
public sealed class RequestAdmissionGate
{
    private readonly object _gate = new();
    private TaskCompletionSource _drained = Completed();
    private int _active;
    private bool _exclusive;
    public (int Active, bool Exclusive) Snapshot { get { lock (_gate) return (_active, _exclusive); } }
    public IDisposable Enter()
    {
        lock (_gate)
        {
            if (_exclusive) throw new RequestAdmissionPausedException();
            if (_active++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Scope(() => { lock (_gate) if (--_active == 0) _drained.TrySetResult(); });
        }
    }
    public async Task<IDisposable> BeginExclusiveAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        Task drain;
        lock (_gate)
        {
            if (_exclusive) throw new InvalidOperationException("Another exclusive catalogue operation already owns admission");
            _exclusive = true; drain = _drained.Task;
        }
        try { await drain.WaitAsync(timeout ?? TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false); }
        catch
        {
            lock (_gate) _exclusive = false;
            throw;
        }
        return new Scope(() => { lock (_gate) _exclusive = false; });
    }
    private static TaskCompletionSource Completed()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); completion.SetResult(); return completion;
    }
    private sealed class Scope(Action close) : IDisposable
    {
        private Action? _close = close;
        public void Dispose() => Interlocked.Exchange(ref _close, null)?.Invoke();
    }
}
