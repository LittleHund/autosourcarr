using AutoSourcarr.Core.Concurrency;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;

namespace AutoSourcarr.Tests;

public static class OwnershipTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("ownership: stop callbacks release resources outside admission locks", () =>
        {
            var owner = new OperationOwner();
            var lease = owner.Enter();
            var callbackRan = false;
            var stop = owner.StopAsync(() =>
            {
                Task.Run(() => Check.Throws<OperationCanceledException>(() => owner.Enter()))
                    .WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                callbackRan = true;
                lease.Dispose();
            });
            stop.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check.True(callbackRan);
            Check.True(ReferenceEquals(stop, owner.StopAsync()));
            owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Check.True(owner.CancellationToken.IsCancellationRequested);
        });
        suite.Add("ownership: failing cancellation and close callbacks still join owned work", () =>
        {
            var owner = new OperationOwner();
            using var registration = owner.CancellationToken.Register(() => throw new InvalidOperationException("cancel failure"));
            var lease = owner.Enter();
            var stop = owner.StopAsync(() => throw new InvalidOperationException("close failure"));
            Check.True(!stop.IsCompleted);
            lease.Dispose();
            Check.Throws<Exception>(() => stop.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
            Check.Throws<Exception>(() => owner.DisposeAsync().AsTask().GetAwaiter().GetResult());
            Check.True(owner.CancellationToken.IsCancellationRequested);
        });
        suite.Add("ownership: cancellation waits for physical writer completion", () =>
        {
            var scheduler = new BoundedWorkScheduler(1, 1);
            using var cancel = new CancellationTokenSource();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var wrote = false;
            var work = scheduler.RunAsync(() =>
            {
                entered.Set();
                Check.True(release.Wait(TimeSpan.FromSeconds(5)));
                wrote = true;
                return 1;
            }, cancel.Token);
            try
            {
                Check.True(entered.Wait(TimeSpan.FromSeconds(5)));
                cancel.Cancel();
                Check.True(!work.IsCompleted);
                Check.Equal((1, 1), scheduler.Snapshot);
            }
            finally
            {
                release.Set();
                Check.Throws<OperationCanceledException>(() => work.GetAwaiter().GetResult());
                scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            Check.True(wrote);
        });
        suite.Add("ownership: cancelled queued work never starts or consumes a new source request", () =>
        {
            var scheduler = new BoundedWorkScheduler(1, 1);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var cancel = new CancellationTokenSource();
            var first = scheduler.RunAsync(() => { entered.Set(); release.Wait(); return 1; });
            var invoked = false;
            try
            {
                Check.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var queued = scheduler.RunAsync(() => { invoked = true; return 2; }, cancel.Token);
                Check.Equal((2, 1), scheduler.Snapshot);
                Check.Throws<WorkDeferredException>(() => scheduler.RunAsync(() => 3).GetAwaiter().GetResult());
                cancel.Cancel();
                Check.Throws<OperationCanceledException>(() => queued.GetAwaiter().GetResult());
                Check.True(!invoked);
            }
            finally
            {
                release.Set();
                first.GetAwaiter().GetResult();
                scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
        suite.Add("ownership: shutdown joins running work and cancels waiting admissions", () =>
        {
            var scheduler = new BoundedWorkScheduler(1, 1);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var first = scheduler.RunAsync(() => { entered.Set(); release.Wait(); return 1; });
            try
            {
                Check.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var queued = scheduler.RunAsync(() => 2);
                var stop = scheduler.StopAsync();
                Check.Throws<OperationCanceledException>(() => queued.GetAwaiter().GetResult());
                Check.True(!stop.IsCompleted);
                release.Set();
                Check.Equal(1, first.GetAwaiter().GetResult());
                stop.GetAwaiter().GetResult();
                Check.Equal((0, 0), scheduler.Snapshot);
                Check.Throws<ObjectDisposedException>(() => scheduler.RunAsync(() => 3).GetAwaiter().GetResult());
            }
            finally
            {
                release.Set();
                first.GetAwaiter().GetResult();
                scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
        suite.Add("ownership: typed awaitable guards and ordinary runtime values preserve their contracts", () =>
        {
            var scheduler = new BoundedWorkScheduler(1, 0);
            var invoked = false;
            try
            {
                Check.Throws<ArgumentException>(() => scheduler.RunAsync<Task>(() => { invoked = true; return Task.CompletedTask; }).GetAwaiter().GetResult());
                Check.Throws<ArgumentException>(() => scheduler.RunAsync<ValueTask>(() => { invoked = true; return ValueTask.CompletedTask; }).GetAwaiter().GetResult());
                Check.Throws<ArgumentException>(() => scheduler.RunAsync<ValueTask<int>>(() => { invoked = true; return ValueTask.FromResult(1); }).GetAwaiter().GetResult());
                Check.True(!invoked);
                Check.Equal((0, 0), scheduler.Snapshot);
                var ordinary = new { Value = 3, NestedTask = Task.CompletedTask };
                Check.True(ReferenceEquals(ordinary, scheduler.RunAsync<object>(() => ordinary).GetAwaiter().GetResult()));
                Check.True(scheduler.RunAsync<object?>(() => null).GetAwaiter().GetResult() is null);
                Check.True(SynchronousCallbackViolationException.Capture(ordinary) is null);
                Check.True(SynchronousCallbackViolationException.Capture(null) is null);
                var completed = Task.CompletedTask;
                Check.True(ReferenceEquals(completed, SynchronousCallbackViolationException.Capture(completed)!.Completion));
                Check.Throws<SynchronousCallbackViolationException>(() => scheduler.RunAsync<IDisposable>(() => completed).GetAwaiter().GetResult());
                foreach (var returned in new object[] { Task.CompletedTask, ValueTask.CompletedTask, ValueTask.FromResult(31) })
                    Check.Throws<SynchronousCallbackViolationException>(() => scheduler.RunAsync<object>(() => returned).GetAwaiter().GetResult());
            }
            finally { scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("ownership: direct hidden platform awaitables retain worker capacity until physical completion", () =>
        {
            foreach (var form in AwaitableForms)
                ExerciseHiddenAsync(form, cancelCaller: false).GetAwaiter().GetResult();
        });
        suite.Add("ownership: caller cancellation and stop cannot abandon returned hidden work", () =>
        {
            foreach (var form in AwaitableForms)
                ExerciseHiddenAsync(form, cancelCaller: true).GetAwaiter().GetResult();
        });
        suite.Add("ownership: carrier cleanup graphs and late multi faults survive cancelled physical joins", () =>
        {
            foreach (var form in AwaitableForms)
            {
                ExerciseHiddenAsync(form, cancelCaller: true, terminal: "fault").GetAwaiter().GetResult();
                ExerciseHiddenAsync(form, cancelCaller: true, terminal: "cancel").GetAwaiter().GetResult();
            }
            ExerciseAggregateAsync().GetAwaiter().GetResult();
            var scheduler = new BoundedWorkScheduler(1, 0);
            try
            {
                var first = new IOException("controlled first completed fault");
                var second = new InvalidDataException("controlled second completed fault");
                var physical = Task.WhenAll(Task.FromException(first), Task.FromException(second));
                var failure = Check.Throws<AggregateException>(() => scheduler.RunAsync<object>(() => new ValueTask(physical)).GetAwaiter().GetResult());
                Check.True(Contains(failure, first) && Contains(failure, second));
                Check.True(Exceptions(failure).Any(error => error is SynchronousCallbackViolationException));
                var genericPhysical = Task.WhenAll(Task.FromException<int>(first), Task.FromException<int>(second));
                var genericFailure = Check.Throws<AggregateException>(() => scheduler.RunAsync<object>(() => new ValueTask<int[]>(genericPhysical)).GetAwaiter().GetResult());
                Check.True(Contains(genericFailure, first) && Contains(genericFailure, second));
                using var completedCancellation = new CancellationTokenSource(); completedCancellation.Cancel();
                var cancelled = Task.FromCanceled<int>(completedCancellation.Token);
                var cancelledFailure = Check.Throws<AggregateException>(() => scheduler.RunAsync<object>(() => new ValueTask<int>(cancelled)).GetAwaiter().GetResult());
                Check.True(Exceptions(cancelledFailure).Any(error => error is SynchronousCallbackViolationException));
                Check.True(Exceptions(cancelledFailure).OfType<OperationCanceledException>()
                    .Any(error => error.CancellationToken == completedCancellation.Token));
            }
            finally { scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("ownership: source backed generic ValueTask is captured and consumed exactly once", () =>
        {
            ExerciseValueSourceAsync(throwCarrier: false).GetAwaiter().GetResult();
            ExerciseValueSourceAsync(throwCarrier: true).GetAwaiter().GetResult();
        });
    }

    private static readonly string[] AwaitableForms = ["task", "task-result", "value-task", "value-task-result"];

    private static object Box(string form, Task physical) => form switch
    {
        "task" or "task-result" => physical,
        "value-task" => new ValueTask(physical),
        "value-task-result" => new ValueTask<int>((Task<int>)physical),
        _ => throw new InvalidOperationException("Unknown controlled awaitable form")
    };

    private static async Task ExerciseHiddenAsync(string form, bool cancelCaller, string terminal = "success")
    {
        var scheduler = new BoundedWorkScheduler(1, 0);
        using var cancellation = new CancellationTokenSource();
        using var physicalCancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new IOException("controlled hidden work late fault");
        var sideEffect = false;
        async Task Body()
        {
            await release.Task.ConfigureAwait(false);
            sideEffect = true;
            if (terminal == "fault") throw late;
            if (terminal == "cancel") physicalCancellation.Token.ThrowIfCancellationRequested();
        }
        async Task<int> ResultBody() { await Body().ConfigureAwait(false); return 17; }
        var work = scheduler.RunAsync<object>(() =>
        {
            Task physical = form is "task-result" or "value-task-result" ? ResultBody() : Body();
            entered.TrySetResult(physical);
            return Box(form, physical);
        }, cancellation.Token);
        Exception? primary = null, workFailure = null;
        var cleanup = new List<Exception>();
        Task? physicalTask = null;
        Task<bool>? workFinishedEarly = null, stopFinishedEarly = null;
        try
        {
            physicalTask = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var actualPhysical = physicalTask;
            workFinishedEarly = work.ContinueWith(_ => !actualPhysical.IsCompleted, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Check.True(!physicalTask.IsCompleted && !work.IsCompleted);
            Check.Equal((1, 1), scheduler.Snapshot);
            Check.Throws<WorkDeferredException>(() => scheduler.RunAsync(() => 1).GetAwaiter().GetResult());
            if (cancelCaller) cancellation.Cancel();
            if (terminal == "cancel") physicalCancellation.Cancel();
            Check.True(!work.IsCompleted);
            var stop = scheduler.StopAsync();
            stopFinishedEarly = stop.ContinueWith(_ => !actualPhysical.IsCompleted, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Check.True(!stop.IsCompleted);
            Check.Equal((1, 1), scheduler.Snapshot);
            Check.Throws<ObjectDisposedException>(() => scheduler.RunAsync(() => 2).GetAwaiter().GetResult());
        }
        catch (Exception error) { primary = error; }
        finally
        {
            release.TrySetResult();
            workFailure = await ObserveAsync(work, cleanup).ConfigureAwait(false);
            if (physicalTask is not null) _ = await ObserveAsync(physicalTask, cleanup).ConfigureAwait(false);
            try { await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception error) { cleanup.Add(error); }
        }
        ThrowFixtureFailure(primary, cleanup);
        Check.True(sideEffect);
        Check.True(workFailure is not null && Exceptions(workFailure).Any(error => error is SynchronousCallbackViolationException));
        Check.True(workFinishedEarly is not null && !await workFinishedEarly.ConfigureAwait(false));
        Check.True(stopFinishedEarly is not null && !await stopFinishedEarly.ConfigureAwait(false));
        Check.Equal((0, 0), scheduler.Snapshot);
        if (terminal == "fault") Check.True(Contains(workFailure!, late));
        if (terminal == "cancel") Check.True(Exceptions(workFailure!).OfType<OperationCanceledException>()
            .Any(error => error.CancellationToken == physicalCancellation.Token));
    }

    private static async Task ExerciseAggregateAsync()
    {
        var scheduler = new BoundedWorkScheduler(1, 0);
        using var cancellation = new CancellationTokenSource();
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<Task[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFault = new IOException("controlled synchronous unwind fault");
        var firstFault = new IOException("controlled first physical fault");
        var secondFault = new InvalidDataException("controlled second physical fault");
        async Task Physical(Task gate, Exception failure) { await gate.ConfigureAwait(false); throw failure; }
        var work = scheduler.RunAsync<object>(() =>
        {
            var first = Physical(firstGate.Task, firstFault);
            var second = Physical(secondGate.Task, secondFault);
            var firstCarrier = SynchronousCallbackViolationException.Capture(first)!;
            var secondCarrier = SynchronousCallbackViolationException.Capture(new ValueTask(second))!;
            entered.TrySetResult([first, second]);
            throw new AggregateException(cleanupFault, new AggregateException(firstCarrier, firstCarrier, secondCarrier));
        }, cancellation.Token);
        Exception? primary = null, workFailure = null;
        var cleanup = new List<Exception>();
        Task[] physical = [];
        try
        {
            physical = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            cancellation.Cancel();
            var stop = scheduler.StopAsync();
            Check.True(!stop.IsCompleted && !work.IsCompleted);
            firstGate.TrySetResult();
            _ = await ObserveAsync(physical[0], cleanup).ConfigureAwait(false);
            Check.True(!physical[1].IsCompleted && !work.IsCompleted && !stop.IsCompleted);
            Check.Equal((1, 1), scheduler.Snapshot);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            firstGate.TrySetResult(); secondGate.TrySetResult();
            workFailure = await ObserveAsync(work, cleanup).ConfigureAwait(false);
            foreach (var item in physical) _ = await ObserveAsync(item, cleanup).ConfigureAwait(false);
            try { await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception error) { cleanup.Add(error); }
        }
        ThrowFixtureFailure(primary, cleanup);
        Check.True(workFailure is not null && Contains(workFailure, cleanupFault)
            && Contains(workFailure, firstFault) && Contains(workFailure, secondFault));
        Check.Equal((0, 0), scheduler.Snapshot);
    }

    private static async Task ExerciseValueSourceAsync(bool throwCarrier)
    {
        var scheduler = new BoundedWorkScheduler(1, 0);
        using var cancellation = new CancellationTokenSource();
        var source = new SingleConsumptionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = scheduler.RunAsync<object>(() =>
        {
            object value = source.Value;
            if (throwCarrier)
            {
                var violation = SynchronousCallbackViolationException.Capture(value)!;
                entered.TrySetResult();
                throw new AggregateException(violation, new AggregateException(violation));
            }
            entered.TrySetResult();
            return value;
        }, cancellation.Token);
        Exception? primary = null, workFailure = null;
        var cleanup = new List<Exception>();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            cancellation.Cancel();
            var stop = scheduler.StopAsync();
            Check.True(!work.IsCompleted && !stop.IsCompleted);
            Check.Equal(0, source.ResultCalls);
            Check.Equal((1, 1), scheduler.Snapshot);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            source.Complete();
            workFailure = await ObserveAsync(work, cleanup).ConfigureAwait(false);
            try { await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception error) { cleanup.Add(error); }
        }
        ThrowFixtureFailure(primary, cleanup);
        Check.True(workFailure is not null && Exceptions(workFailure).Any(error => error is SynchronousCallbackViolationException));
        Check.Equal(1, source.ResultCalls);
        Check.Equal((0, 0), scheduler.Snapshot);
    }

    private sealed class SingleConsumptionSource : IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> _source = new() { RunContinuationsAsynchronously = true };
        private int _completed, _resultCalls;
        public int ResultCalls => Volatile.Read(ref _resultCalls);
        public ValueTask<int> Value => new(this, _source.Version);
        public void Complete() { if (Interlocked.Exchange(ref _completed, 1) == 0) _source.SetResult(23); }
        public int GetResult(short token) { Interlocked.Increment(ref _resultCalls); return _source.GetResult(token); }
        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _source.OnCompleted(continuation, state, token, flags);
    }

    private static async Task<Exception?> ObserveAsync(Task task, List<Exception> cleanup)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); return null; }
        catch (TimeoutException error) { cleanup.Add(error); return error; }
        catch (Exception error) { return error; }
    }

    private static IEnumerable<Exception> Exceptions(Exception root)
    {
        var pending = new Stack<Exception>(); var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            yield return current;
            if (current is AggregateException aggregate) foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException is not null) pending.Push(current.InnerException);
        }
    }
    private static bool Contains(Exception root, Exception expected) => Exceptions(root).Any(item => ReferenceEquals(item, expected));
    private static void ThrowFixtureFailure(Exception? primary, List<Exception> cleanup)
    {
        if (cleanup.Count != 0)
        {
            var failures = new List<Exception>();
            if (primary is not null) failures.Add(primary);
            failures.AddRange(cleanup);
            throw new AggregateException("Controlled ownership fixture failed after gate release/cleanup", failures);
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
