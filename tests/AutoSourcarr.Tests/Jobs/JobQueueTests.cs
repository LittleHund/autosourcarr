using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Privacy;

namespace AutoSourcarr.Tests.Jobs;

/// <summary>Phase 3: Sonarr's queue rules, run limits, aging (no starvation), watchdog, back-off, ETA and cancellation.</summary>
public static class JobQueueTests
{
    private static readonly JobType Fast = new("fast", "Fast work", JobPriority.Normal, RunLimit: 10);

    public static void Register(TestSuite suite)
    {
        suite.Add("jobs: highest priority first, then oldest; identical jobs are merged and can be raised", () =>
        {
            using var fixture = new Fixture(new JobQueueOptions { Workers = 1 });
            var gate = new TaskCompletionSource();
            var order = new ConcurrentQueue<string>();
            fixture.Queue.Enqueue(Fast, "blocker", _ => gate.Task);
            Wait(() => fixture.Running() == 1);
            fixture.Queue.Enqueue(Fast, "low-old", Record(order, "low-old"), JobPriority.Low);
            fixture.Queue.Enqueue(Fast, "normal", Record(order, "normal"));
            fixture.Queue.Enqueue(Fast, "high", Record(order, "high"), JobPriority.High);
            fixture.Queue.Enqueue(Fast, "normal-later", Record(order, "normal-later"));
            var first = fixture.Queue.Enqueue(Fast, "low-raised", Record(order, "low-raised"), JobPriority.Low);
            var merged = fixture.Queue.Enqueue(Fast, "low-raised", Record(order, "duplicate"), JobPriority.High);
            Check.True(merged.Merged && merged.Id == first.Id, "The identical queued job is merged into the existing one.");
            var running = fixture.Queue.Enqueue(Fast, "blocker", _ => Task.CompletedTask);
            Check.True(running.Merged, "An identical running job is merged too (Sonarr: queued or started).");
            gate.SetResult();
            Wait(() => order.Count == 5);
            Check.Equal("high,low-raised,normal,normal-later,low-old", string.Join(",", order));
            Check.Equal(JobOutcome.Completed, merged.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Check.True(fixture.Tasks.Snapshot()["completed"]!.GetValue<int>() == 6, "Every job is mirrored as one task.");
        });

        suite.Add("jobs: three workers by default; long-running jobs do not take a worker slot", () =>
        {
            using var fixture = new Fixture(new JobQueueOptions());
            Check.Equal(3, fixture.Queue.Workers);
            var gate = new TaskCompletionSource();
            var longType = new JobType("long", "Long scan", Flags: JobFlags.LongRunning, RunLimit: 2);
            fixture.Queue.Enqueue(longType, "a", _ => gate.Task);
            fixture.Queue.Enqueue(longType, "b", _ => gate.Task);
            fixture.Queue.Enqueue(longType, "c", _ => gate.Task);
            for (var index = 0; index < 5; index++) fixture.Queue.Enqueue(Fast, "f" + index, _ => gate.Task);
            Wait(() => fixture.Running() == 5);
            Thread.Sleep(100);
            var snapshot = fixture.Queue.Snapshot();
            Check.Equal(5, fixture.Running());
            Check.Equal(3, snapshot["busy_workers"]!.GetValue<int>());
            Check.Equal(2, snapshot["running"]!.AsArray().Count(row => row!["type"]!.GetValue<string>() == "long"));
            gate.SetResult();
            Wait(() => fixture.Running() == 0 && fixture.Queued() == 0);
        });

        suite.Add("jobs: needs-disk jobs run one at a time and the run limit per type holds", () =>
        {
            using var fixture = new Fixture(new JobQueueOptions { Workers = 3 });
            var disk = new JobType("disk", "Disk work", Flags: JobFlags.NeedsDisk, RunLimit: 5);
            var limited = new JobType("limited", "Limited work", RunLimit: 1);
            var active = 0; var peakDisk = 0; var activeLimited = 0; var peakLimited = 0;
            Func<JobContext, Task> Work(bool isDisk) => async _ =>
            {
                if (isDisk) { var now = Interlocked.Increment(ref active); InterlockedMax(ref peakDisk, now); }
                else { var now = Interlocked.Increment(ref activeLimited); InterlockedMax(ref peakLimited, now); }
                await Task.Delay(30);
                if (isDisk) Interlocked.Decrement(ref active); else Interlocked.Decrement(ref activeLimited);
            };
            var handles = new List<JobHandle>();
            for (var index = 0; index < 4; index++) handles.Add(fixture.Queue.Enqueue(disk, "d" + index, Work(true)));
            for (var index = 0; index < 4; index++) handles.Add(fixture.Queue.Enqueue(limited, "l" + index, Work(false)));
            Task.WaitAll(handles.Select(handle => (Task)handle.Completion).ToArray(), TimeSpan.FromSeconds(10));
            Check.Equal(1, peakDisk);
            Check.Equal(1, peakLimited);
        });

        suite.Add("jobs: an exclusive job runs alone and, at the head of the queue, holds back new starts", () =>
        {
            using var fixture = new Fixture(new JobQueueOptions { Workers = 3 });
            var exclusive = new JobType("exclusive", "Exclusive", JobPriority.High, JobFlags.Exclusive);
            var gate = new TaskCompletionSource();
            var log = new ConcurrentQueue<string>();
            var exclusiveSawOthers = false;
            fixture.Queue.Enqueue(Fast, "running", async _ => { log.Enqueue("running-start"); await gate.Task; log.Enqueue("running-end"); });
            Wait(() => fixture.Running() == 1);
            var alone = fixture.Queue.Enqueue(exclusive, "x", _ => { exclusiveSawOthers = fixture.Running() != 1; log.Enqueue("exclusive"); return Task.CompletedTask; });
            fixture.Queue.Enqueue(Fast, "after", _ => { log.Enqueue("after"); return Task.CompletedTask; });
            Thread.Sleep(150);
            Check.Equal(1, fixture.Running()); // "after" waits behind the exclusive job instead of passing it
            gate.SetResult();
            Wait(() => log.Count == 4);
            Check.Equal("running-start,running-end,exclusive,after", string.Join(",", log));
            Check.True(!exclusiveSawOthers, "Nothing else ran next to the exclusive job.");
            Check.Equal(JobOutcome.Completed, alone.Completion.GetAwaiter().GetResult());
        });

        suite.Add("jobs: load test, a stream of High work cannot starve Low work (aging, PERF-2)", () =>
        {
            using var fixture = new Fixture(new JobQueueOptions { Workers = 3, Aging = TimeSpan.FromMilliseconds(150) });
            var high = new JobType("burst", "Burst", JobPriority.High, RunLimit: 3);
            var low = new JobType("background", "Background", JobPriority.Low, RunLimit: 3);
            var normal = new JobType("normal", "Normal", JobPriority.Normal, RunLimit: 3);
            var lows = Enumerable.Range(0, 6).Select(index => fixture.Queue.Enqueue(low, "l" + index, _ => Task.Delay(5))).ToArray();
            var normals = Enumerable.Range(0, 6).Select(index => fixture.Queue.Enqueue(normal, "n" + index, _ => Task.Delay(5))).ToArray();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            var produced = 0;
            var producer = Task.Run(async () =>
            {
                // Keeps the queue full of High work for the whole run: without aging nothing else would ever start.
                while (!stop.IsCancellationRequested)
                {
                    while (fixture.Queued("burst") < 6) fixture.Queue.Enqueue(high, "h" + Interlocked.Increment(ref produced), _ => Task.Delay(10));
                    await Task.Delay(2);
                }
            });
            var allDone = Task.WhenAll(lows.Concat(normals).Select(handle => handle.Completion));
            // Without aging this would never finish; with it the Low/Normal work completes within seconds even on a slow runner.
            var finished = allDone.Wait(TimeSpan.FromSeconds(8));
            stop.Cancel(); producer.Wait();
            Check.True(finished, "Low and Normal jobs all finished while High work kept arriving.");
            Check.True(produced > 20, "The burst really kept the queue busy (" + produced + " High jobs).");
            var types = fixture.Queue.Snapshot()["types"]!.AsArray();
            foreach (var name in new[] { "background", "normal" })
                Check.Equal(6L, types.Single(row => row!["type"]!.GetValue<string>() == name)!["completed"]!.GetValue<long>());
        });

        suite.Add("jobs: a job without progress is frozen, recorded as an error at once and retried with back-off (PERF-3)", () =>
        {
            using var fixture = new Fixture(new JobQueueOptions { Workers = 1, WatchdogInterval = TimeSpan.FromMilliseconds(20), CancelGrace = TimeSpan.FromMilliseconds(200) });
            var stuck = new JobType("stuck", "Stuck work", StallTimeout: TimeSpan.FromMilliseconds(600), MaxAttempts: 2, RetryBase: TimeSpan.FromMilliseconds(100));
            var attempts = 0;
            var handle = fixture.Queue.Enqueue(stuck, "k", async job =>
            {
                Interlocked.Increment(ref attempts);
                if (job.Attempt == 1) await Task.Delay(Timeout.Infinite, job.CancellationToken); // never reports progress
                for (var step = 0; step < 4; step++) { await Task.Delay(50); job.Report(step + 1, 4); } // reports: not frozen
            });
            Wait(() => fixture.Errors.Snapshot(10)["items"]!.AsArray().Any(row => row!["key"]!.GetValue<string>() == "job-frozen:stuck"));
            Check.True(fixture.Tasks.IsRunning(handle.Id), "The frozen attempt is retried, so the task is still open.");
            Check.Equal(JobOutcome.Completed, handle.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Check.Equal(2, attempts);
            var events = fixture.Events.Since(0)!.Select(item => item.Data["change"]?.GetValue<string>()).ToArray();
            Check.True(events.Contains("frozen") && events.Contains("retry") && events.Contains("completed"), string.Join(",", events));
        });

        suite.Add("jobs: a job that ignores cancellation is released after the grace time so the queue keeps moving", () =>
        {
            using var fixture = new Fixture(new JobQueueOptions { Workers = 1, WatchdogInterval = TimeSpan.FromMilliseconds(20), CancelGrace = TimeSpan.FromMilliseconds(100) });
            var stubborn = new JobType("stubborn", "Stubborn work", StallTimeout: TimeSpan.FromMilliseconds(100));
            var release = new TaskCompletionSource();
            var stuck = fixture.Queue.Enqueue(stubborn, "k", _ => release.Task); // ignores its token
            var next = fixture.Queue.Enqueue(Fast, "next", _ => Task.CompletedTask);
            Check.Equal(JobOutcome.Frozen, stuck.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Check.Equal(JobOutcome.Completed, next.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Check.True(fixture.Errors.Snapshot(10)["items"]!.AsArray().Any(row => row!["key"]!.GetValue<string>() == "job-abandoned:stubborn"));
            release.SetResult();
        });

        suite.Add("jobs: failures retry with doubling back-off, the last failure is a grouped error", () =>
        {
            using var fixture = new Fixture(new JobQueueOptions { Workers = 1 });
            var flaky = new JobType("flaky", "Flaky work", MaxAttempts: 3, RetryBase: TimeSpan.FromMilliseconds(80));
            var starts = new ConcurrentQueue<DateTimeOffset>();
            var handle = fixture.Queue.Enqueue(flaky, "k", _ => { starts.Enqueue(DateTimeOffset.UtcNow); throw new InvalidOperationException("source answered 500"); });
            Check.Equal(JobOutcome.Failed, handle.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            var times = starts.ToArray();
            Check.Equal(3, times.Length);
            Check.True((times[1] - times[0]).TotalMilliseconds >= 70, "First retry waits the base delay.");
            Check.True((times[2] - times[1]).TotalMilliseconds >= 150, "Second retry waits twice as long.");
            var task = fixture.Tasks.Find(handle.Id)!;
            Check.Equal("failed", task.State);
            Check.True(task.Error.Contains("source answered 500", StringComparison.Ordinal));
            var error = fixture.Errors.Snapshot(10)["items"]!.AsArray().Single(row => row!["key"]!.GetValue<string>() == "job-failed:flaky")!;
            Check.Equal(1L, error["count"]!.GetValue<long>());
        });

        suite.Add("jobs: queued and running jobs are cancelled through the task list", () =>
        {
            using var fixture = new Fixture(new JobQueueOptions { Workers = 1 });
            var started = new TaskCompletionSource();
            var running = fixture.Queue.Enqueue(Fast, "running", async job => { started.SetResult(); await Task.Delay(Timeout.Infinite, job.CancellationToken); });
            var queued = fixture.Queue.Enqueue(Fast, "queued", _ => Task.CompletedTask);
            started.Task.Wait(TimeSpan.FromSeconds(5));
            Check.True(fixture.Tasks.RequestCancelAsync(queued.Id).GetAwaiter().GetResult());
            Check.Equal(JobOutcome.Cancelled, queued.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Check.True(fixture.Tasks.RequestCancelAsync(running.Id).GetAwaiter().GetResult());
            Check.Equal(JobOutcome.Cancelled, running.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Check.Equal("Cancelled by user", fixture.Tasks.Find(running.Id)!.Error);
            Check.True(fixture.Errors.Snapshot(10)["items"]!.AsArray().Count == 0, "A user cancellation is not an error.");
        });

        suite.Add("jobs: ETA per type comes from the average run time and is added to the task list", () =>
        {
            using var fixture = new Fixture(new JobQueueOptions { Workers = 1 });
            var timed = new JobType("timed", "Timed work", RunLimit: 1);
            for (var index = 0; index < 3; index++)
                fixture.Queue.Enqueue(timed, "warm" + index, _ => Task.Delay(60)).Completion.Wait(TimeSpan.FromSeconds(5));
            var gate = new TaskCompletionSource();
            fixture.Queue.Enqueue(timed, "running", _ => gate.Task);
            var waiting = fixture.Queue.Enqueue(timed, "waiting", _ => Task.CompletedTask);
            Wait(() => fixture.Running() == 1);
            var type = fixture.Queue.Snapshot()["types"]!.AsArray().Single()!;
            var average = type["average_seconds"]!.GetValue<double>();
            Check.True(average is > 0.04 and < 1, "Average run time " + average);
            var tasks = fixture.Queue.Annotate(fixture.Tasks.Snapshot());
            var row = tasks["items"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == waiting.Id)!;
            Check.Equal("timed", row["job_type"]!.GetValue<string>());
            Check.Equal(1, row["queue_position"]!.GetValue<int>());
            Check.True(row["eta_seconds"] is not null && row["eta_start_seconds"] is not null, row.ToJsonString());
            Check.Equal(1, tasks["jobs"]!["workers"]!.GetValue<int>());
            gate.SetResult();
        });

        suite.Add("jobs: a cancel wins over a freeze (no hidden retry) and a new request is not merged into a dying job", () =>
        {
            using var fixture = new Fixture(new JobQueueOptions { Workers = 1, WatchdogInterval = TimeSpan.FromMilliseconds(20), CancelGrace = TimeSpan.FromSeconds(5) });
            var stuck = new JobType("stuck2", "Stuck work", StallTimeout: TimeSpan.FromMilliseconds(200), MaxAttempts: 3, RetryBase: TimeSpan.FromMilliseconds(50));
            var release = new TaskCompletionSource();
            var runs = 0;
            var first = fixture.Queue.Enqueue(stuck, "k", async _ => { Interlocked.Increment(ref runs); await release.Task; });
            Wait(() => fixture.Errors.Snapshot(10)["items"]!.AsArray().Any(row => row!["key"]!.GetValue<string>() == "job-frozen:stuck2"));
            var again = fixture.Queue.Enqueue(stuck, "k", _ => Task.CompletedTask);
            Check.True(!again.Merged, "A frozen job does not absorb a new request.");
            Check.True(fixture.Tasks.RequestCancelAsync(first.Id).GetAwaiter().GetResult() || fixture.Tasks.Find(first.Id)!.State == "failed");
            release.SetResult();
            Check.Equal(JobOutcome.Cancelled, first.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Check.Equal(JobOutcome.Completed, again.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Thread.Sleep(200);
            Check.Equal(1, runs);
            Check.Equal(1L, fixture.Errors.Snapshot(10)["items"]!.AsArray().Single(row => row!["key"]!.GetValue<string>() == "job-frozen:stuck2")!["count"]!.GetValue<long>());
        });

        suite.Add("jobs: a non-cancellable exclusive job cannot be cancelled and is never abandoned", () =>
        {
            using var fixture = new Fixture(new JobQueueOptions { Workers = 2, WatchdogInterval = TimeSpan.FromMilliseconds(20), CancelGrace = TimeSpan.FromMilliseconds(100) });
            var move = new JobType("move", "Move", JobPriority.High, JobFlags.Exclusive, StallTimeout: TimeSpan.FromMilliseconds(100), Cancellable: false);
            var release = new TaskCompletionSource();
            var handle = fixture.Queue.Enqueue(move, "k", _ => release.Task);
            var other = fixture.Queue.Enqueue(Fast, "other", _ => Task.CompletedTask);
            Wait(() => fixture.Running() == 1);
            Check.True(!fixture.Tasks.RequestCancelAsync(handle.Id).GetAwaiter().GetResult(), "The task list offers no cancel.");
            Thread.Sleep(400); // past stall + grace
            Check.True(!other.Completion.IsCompleted, "Nothing ran beside the exclusive job after its stall time.");
            release.SetResult();
            Check.Equal(JobOutcome.Completed, other.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        });

        suite.Add("jobs: stopping background work cancels queued and running jobs, waits for them, and resume starts new work", () =>
        {
            using var fixture = new Fixture(new JobQueueOptions { Workers = 1 });
            var ended = false;
            var running = fixture.Queue.Enqueue(Fast, "running", async job =>
            {
                try { await Task.Delay(Timeout.Infinite, job.CancellationToken); }
                finally { await Task.Delay(50); ended = true; }
            });
            var queued = fixture.Queue.Enqueue(Fast, "queued", _ => Task.CompletedTask);
            Wait(() => fixture.Running() == 1);
            Check.True(fixture.Queue.StopBackgroundAsync("Database reset", TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Check.True(ended, "Stop waits until running work has really ended.");
            Check.Equal(JobOutcome.Cancelled, running.Completion.GetAwaiter().GetResult());
            Check.Equal(JobOutcome.Cancelled, queued.Completion.GetAwaiter().GetResult());
            var later = fixture.Queue.Enqueue(Fast, "later", _ => Task.CompletedTask);
            Thread.Sleep(100);
            Check.True(!later.Completion.IsCompleted && fixture.Queue.Paused, "Nothing starts while background work is stopped.");
            fixture.Queue.ResumeBackground();
            Check.Equal(JobOutcome.Completed, later.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        });

        suite.Add("jobs: finished tasks keep the job's result message and stopping fails queued work without running it", () =>
        {
            var fixture = new Fixture(new JobQueueOptions { Workers = 1 });
            var done = fixture.Queue.Enqueue(Fast, "result", job => { job.Result("Copied 2 files"); return Task.CompletedTask; });
            done.Completion.Wait(TimeSpan.FromSeconds(5));
            Check.Equal("Copied 2 files", fixture.Tasks.Find(done.Id)!.Message);
            var gate = new TaskCompletionSource();
            fixture.Queue.Enqueue(Fast, "busy", job => gate.Task.WaitAsync(job.CancellationToken));
            var never = fixture.Queue.Enqueue(Fast, "never", _ => throw new InvalidOperationException("must not run"));
            Wait(() => fixture.Running() == 1);
            fixture.Dispose();
            Check.Equal(JobOutcome.Cancelled, never.Completion.GetAwaiter().GetResult());
            Check.Equal("failed", fixture.Tasks.Find(never.Id)!.State);
        });
    }

    private static Func<JobContext, Task> Record(ConcurrentQueue<string> order, string name) => _ => { order.Enqueue(name); return Task.CompletedTask; };

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    internal static void Wait(Func<bool> condition, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached in time.");
            Thread.Sleep(10);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private bool _disposed;
        public Fixture(JobQueueOptions options)
        {
            Errors = new ErrorRegistry(RuntimeIdentity.Create("fixture"), new SupportPrivacy());
            Queue = new JobQueue(Tasks, Events, Errors, options);
        }
        public TaskRegistry Tasks { get; } = new();
        public EventStream Events { get; } = new();
        public ErrorRegistry Errors { get; }
        public JobQueue Queue { get; }
        public int Running() => Queue.Snapshot()["running"]!.AsArray().Count;
        public int Queued(string? type = null) => Queue.Snapshot()["queued"]!.AsArray().Count(row => type is null || row!["type"]!.GetValue<string>() == type);
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Queue.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        }
    }
}

/// <summary>PERF-8: the single event stream (ids, resume, resync, topic filter, overflow, coalescing).</summary>
public static class EventStreamTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("events: ids increase, a reconnect resumes after its last id without gaps or duplicates", () =>
        {
            var stream = new EventStream();
            for (var index = 0; index < 5; index++) stream.Publish("tasks", new JsonObject { ["n"] = index });
            Check.Equal(5L, stream.LastId);
            using var subscription = stream.Subscribe(lastId: 3);
            stream.Publish("tasks", new JsonObject { ["n"] = 5 });
            var seen = new List<long>();
            for (var index = 0; index < 3; index++) seen.Add(subscription.NextAsync(TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult()!.Id);
            Check.Equal("4,5,6", string.Join(",", seen));
            Check.True(subscription.NextAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None).GetAwaiter().GetResult() is null, "Nothing more: a heartbeat would be sent.");
            // An id from before a server restart (newer than anything held) must make the browser reload.
            Check.True(stream.Since(5000) is null);
            using var restarted = stream.Subscribe(lastId: 5000);
            Check.Equal("resync", restarted.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None).GetAwaiter().GetResult()!.Topic);
            // A reconnect far behind replays everything still held instead of overflowing.
            for (var index = 0; index < 600; index++) stream.Publish("jobs");
            using var behind = stream.Subscribe(lastId: 6);
            for (var index = 0; index < 600; index++) behind.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None).GetAwaiter().GetResult();
        });

        suite.Add("events: a reader whose gap is no longer held gets a resync event", () =>
        {
            var stream = new EventStream();
            for (var index = 0; index < EventStream.Capacity + 10; index++) stream.Publish("jobs");
            Check.True(stream.Since(5) is null);
            Check.Equal(EventStream.Capacity, stream.Since(10)!.Count);
            using var subscription = stream.Subscribe(lastId: 5);
            Check.Equal("resync", subscription.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None).GetAwaiter().GetResult()!.Topic);
        });

        suite.Add("events: topics filter a subscription and a slow reader is told to resync instead of blocking publishers", () =>
        {
            var stream = new EventStream();
            using var errorsOnly = stream.Subscribe(topics: new HashSet<string>(StringComparer.Ordinal) { "errors" });
            stream.Publish("tasks"); stream.Publish("errors");
            Check.Equal("errors", errorsOnly.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None).GetAwaiter().GetResult()!.Topic);
            using var slow = stream.Subscribe();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var index = 0; index < 1000; index++) stream.Publish("tasks");
            Check.True(watch.ElapsedMilliseconds < 2000, "Publishing never waits for a reader.");
            Check.Throws<EventStreamOverflowException>(() =>
            {
                for (var index = 0; index < 1000; index++) slow.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None).GetAwaiter().GetResult();
            });
        });

        suite.Add("events: many changes inside the window are coalesced into one leading and one trailing event", () =>
        {
            var stream = new EventStream();
            for (var index = 0; index < 50; index++) stream.Pulse("tasks", TimeSpan.FromMilliseconds(100));
            Check.Equal(1L, stream.LastId);
            JobQueueTests.Wait(() => stream.LastId == 2, 3);
            Thread.Sleep(250);
            Check.Equal(2L, stream.LastId);
        });

        suite.Add("events: task and error changes reach the stream (ErrorRegistry.Changed, TaskRegistry.Changed)", () =>
        {
            var stream = new EventStream();
            var tasks = new TaskRegistry();
            var errors = new ErrorRegistry(RuntimeIdentity.Create("fixture"), new SupportPrivacy());
            tasks.Changed += () => stream.Pulse("tasks", TimeSpan.FromMilliseconds(50));
            errors.Changed += () => stream.Pulse("errors", TimeSpan.FromMilliseconds(50));
            tasks.Create("Something");
            errors.Record("Jobs", "broken");
            var topics = stream.Since(0)!.Select(item => item.Topic).ToArray();
            Check.True(topics.Contains("tasks") && topics.Contains("errors"), string.Join(",", topics));
        });
    }
}
