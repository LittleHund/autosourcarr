using System.Reflection;
using System.Runtime.ExceptionServices;

namespace AutoSourcarr.Core.Concurrency;

public sealed class WorkDeferredException() : Exception("Local work capacity is busy; the operation was deferred without contacting a source.");

/// <summary>Hands already-started returned work through synchronous resource unwind to its outside owner.</summary>
/// <remarks>Capture consumes a direct ValueTask once. It does not detect async void, nested tasks or detached work.
/// A direct synchronous caller must join Completion only after all thread-affine resource scopes have unwound.</remarks>
public sealed class SynchronousCallbackViolationException : InvalidOperationException
{
    public readonly Task Completion;

    private SynchronousCallbackViolationException(Task completion)
        : base("A synchronous callback returned asynchronous work; its completion must be joined after resource unwind.")
    { Completion = completion; }

    public static SynchronousCallbackViolationException? Capture(object? result)
    {
        if (result is Task task) return new(task);
        if (result is ValueTask valueTask) return new(valueTask.AsTask());
        if (result is null) return null;
        var type = result.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(ValueTask<>)) return null;
        // Only this exact platform struct is reflected. Return its actual AsTask
        // result, preserving a backing Task's complete multi-fault graph.
        try
        {
            var completion = (Task)type.GetMethod(nameof(ValueTask.AsTask), Type.EmptyTypes)!.Invoke(result, null)!;
            return new(completion);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException!).Throw();
            throw;
        }
    }
}

/// <summary>Bounded synchronous I/O whose owner cannot finish before its physical worker.</summary>
public sealed class BoundedWorkScheduler : IAsyncDisposable
{
    private readonly SemaphoreSlim _capacity;
    private readonly SemaphoreSlim _workers;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private TaskCompletionSource _drained = CompleteSignal();
    private int _admitted;
    private int _running;
    private bool _closed;
    private bool _resourcesDisposed;
    private Task? _stopTask;

    public BoundedWorkScheduler(int workers, int queueCapacity)
    {
        if (workers is < 1 or > 64 || queueCapacity is < 0 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(workers));
        _workers = new(workers, workers);
        _capacity = new(workers + queueCapacity, workers + queueCapacity);
    }

    public (int Admitted, int Running) Snapshot
    {
        get { lock (_gate) return (_admitted, _running); }
    }

    public async Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken = default)
    {
        var type = typeof(T);
        if (typeof(Task).IsAssignableFrom(type) || type == typeof(ValueTask)
            || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            throw new ArgumentException("A synchronous work lease cannot own a returned awaitable.", nameof(operation));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_closed)
                throw new ObjectDisposedException(nameof(BoundedWorkScheduler));
            if (!_capacity.Wait(0, cancellationToken))
                throw new WorkDeferredException();
            if (_admitted++ == 0)
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var ownsWorker = false;
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        try
        {
            await _workers.WaitAsync(admission.Token).ConfigureAwait(false);
            ownsWorker = true;
            admission.Token.ThrowIfCancellationRequested();
            lock (_gate) _running++;
            T result;
            try
            {
                try
                {
                    // The callback's thread-affine scopes fully unwind before a
                    // thrown carrier reaches this outside physical-work owner.
                    result = await Task.Run(operation, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    var completions = FindCompletions(error);
                    if (completions.Length != 0)
                        await JoinAndRejectAsync(error, completions).ConfigureAwait(false);
                    // Preserve the ordinary synchronous callback cancellation policy;
                    // it must not erase a carrier, cleanup fault or late task fault.
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);
                    throw;
                }
                var violation = SynchronousCallbackViolationException.Capture(result);
                if (violation is not null)
                    await JoinAndRejectAsync(violation, [violation.Completion]).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) _running--;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            if (ownsWorker)
                _workers.Release();
            lock (_gate)
            {
                _capacity.Release();
                if (--_admitted == 0)
                    _drained.TrySetResult();
            }
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null)
                return _stopTask;
            _closed = true;
            _stopTask = DrainAsync(_stopping.CancelAsync(), _drained.Task);
            return _stopTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (_resourcesDisposed)
                return;
            _resourcesDisposed = true;
            _capacity.Dispose();
            _workers.Dispose();
            _stopping.Dispose();
        }
    }

    private static TaskCompletionSource CompleteSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private static async Task DrainAsync(Task cancel, Task drain)
    {
        await cancel.ConfigureAwait(false);
        await drain.ConfigureAwait(false);
    }

    private static Task[] FindCompletions(Exception primary)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var seenTasks = new HashSet<Task>(ReferenceEqualityComparer.Instance);
        var completions = new List<Task>();
        pending.Push(primary);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current is SynchronousCallbackViolationException violation)
            {
                if (seenTasks.Add(violation.Completion)) completions.Add(violation.Completion);
            }
            else if (current is AggregateException aggregate)
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                    pending.Push(aggregate.InnerExceptions[index]);
        }
        return completions.ToArray();
    }

    private static async Task JoinAndRejectAsync(Exception primary, IReadOnlyList<Task> completions)
    {
        var errors = new List<Exception>();
        foreach (var completion in completions)
        {
            try { await completion.ConfigureAwait(false); }
            catch (Exception error) { errors.Add(completion.Exception ?? error); }
        }
        if (errors.Count != 0)
            throw new AggregateException("A synchronous callback violation retained failures after returned work was joined.",
                new[] { primary }.Concat(errors));
        ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
