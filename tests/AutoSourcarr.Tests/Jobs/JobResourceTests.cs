using System.Collections.Concurrent;
using AutoSourcarr.Core.Jobs;

namespace AutoSourcarr.Tests.Jobs;

/// <summary>Phase 13 item 4 (PERF-2): minimum shares per job type and CPU/memory limits for job processes.</summary>
public static class JobResourceTests
{
    private sealed class FakeLimitHost : IProcessLimitHost
    {
        public bool Linux { get; init; } = true;
        public string? Cgroup { get; init; } = "/sys/fs/cgroup/app/main";
        public bool Writable { get; init; } = true;
        public ConcurrentDictionary<string, string> Written { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = [];
        public string? OwnCgroup() => Cgroup;
        public bool TryWrite(string path, string value) { if (!Writable) return false; Written[path.Replace('\\', '/')] = value; return true; }
        public bool TryCreateDirectory(string path) => Writable;
        public bool Exists(string path) => path.Replace('\\', '/').EndsWith("cgroup.subtree_control", StringComparison.Ordinal);
        public string? Read(string path) => "cpuset cpu io memory pids";
        public bool SetNice(int processId, int nice) { Calls.Add("nice " + nice); return true; }
        public bool LimitAddressSpace(int processId, long bytes) { Calls.Add("as " + bytes); return true; }
    }

    public static void Register(TestSuite suite)
    {
        suite.Add("jobs: a type with a minimum share keeps its slots while a flood of another type waits (PERF-2)", () =>
        {
            var tasks = new TaskRegistry();
            var queue = new JobQueue(tasks, options: new JobQueueOptions { Workers = 3 });
            try
            {
                var flood = new JobType("flood", "Flood", JobPriority.High, RunLimit: 10);
                var health = new JobType("health", "Health", JobPriority.Low, RunLimit: 2) { MinimumShare = 1 };
                var gate = new TaskCompletionSource();
                var running = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
                Func<JobContext, Task> Work(string name) => async _ => { running.AddOrUpdate(name, 1, (_, value) => value + 1); await gate.Task; };
                // The share is held only while the type has work waiting: queue the health job first, then the flood.
                queue.Enqueue(health, "h1", Work("health"));
                for (var index = 0; index < 6; index++) queue.Enqueue(flood, "f" + index, Work("flood"));
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (running.Values.Sum() < 3 && DateTime.UtcNow < deadline) Thread.Sleep(20);
                Thread.Sleep(100);
                Check.Equal(1, running.GetValueOrDefault("health"));
                Check.Equal(2, running.GetValueOrDefault("flood"));
                var types = queue.Snapshot()["types"]!.AsArray();
                Check.Equal(1, types.Single(row => row!["type"]!.GetValue<string>() == "health")!["minimum_share"]!.GetValue<int>());
                gate.SetResult();
            }
            finally { queue.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });

        suite.Add("jobs: long-running work watched by ExclusiveYield ends so a waiting exclusive job runs (audit 6: raw archive, health)", () =>
        {
            var queue = new JobQueue(new TaskRegistry());
            try
            {
                var yielded = false;
                var longType = new JobType("long-capture", "Capture", JobPriority.Low, JobFlags.LongRunning);
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var capture = queue.Enqueue(longType, "c", async job =>
                {
                    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(job.CancellationToken);
                    var run = Task.Delay(Timeout.Infinite, cancel.Token);
                    started.TrySetResult();
                    _ = ExclusiveYield.WatchAsync(() => queue.ExclusivePending, () => { yielded = true; cancel.Cancel(); }, run);
                    try { await run; } catch (OperationCanceledException) when (yielded) { job.Result("yielded"); }
                });
                started.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                var exclusive = queue.Enqueue(new JobType("backup-test", "Backup", JobPriority.High, JobFlags.Exclusive), "b", _ => Task.CompletedTask);
                Check.Equal(JobOutcome.Completed, exclusive.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
                Check.Equal(JobOutcome.Completed, capture.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
                Check.True(yielded, "the long job ended for the exclusive one");
            }
            finally { queue.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });

        suite.Add("jobs: two types with shares on one free slot do not block each other (no reservation deadlock)", () =>
        {
            var queue = new JobQueue(new TaskRegistry(), options: new JobQueueOptions { Workers = 1 });
            try
            {
                var a = new JobType("share-a", "A", RunLimit: 1) { MinimumShare = 1 };
                var b = new JobType("share-b", "B", RunLimit: 1) { MinimumShare = 1 };
                var first = queue.Enqueue(a, "a", _ => Task.CompletedTask);
                var second = queue.Enqueue(b, "b", _ => Task.CompletedTask);
                Check.Equal(JobOutcome.Completed, first.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
                Check.Equal(JobOutcome.Completed, second.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
            }
            finally { queue.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });

        suite.Add("jobs: process limits use a writable cgroup v2 (cpu.weight, memory.max), else nice and an address-space limit", () =>
        {
            var host = new FakeLimitHost();
            var applied = new ProcessResourceLimits(host).Apply(1234, "repair runner", new ProcessLimits(CpuWeight: 50, MemoryBytes: 3L << 30, Nice: 10));
            Check.Equal("cgroup", applied.Mode);
            Check.Equal("50", host.Written["/sys/fs/cgroup/app/autosourcarr-repair-runner/cpu.weight"]);
            Check.Equal((3L << 30).ToString(System.Globalization.CultureInfo.InvariantCulture), host.Written["/sys/fs/cgroup/app/autosourcarr-repair-runner/memory.max"]);
            Check.Equal("1234", host.Written["/sys/fs/cgroup/app/autosourcarr-repair-runner/cgroup.procs"]);
            var readOnly = new FakeLimitHost { Writable = false };
            var fallback = new ProcessResourceLimits(readOnly).Apply(1234, "repair-runner", new ProcessLimits(Nice: 10, AddressSpaceBytes: 8L << 30));
            Check.Equal("nice", fallback.Mode);
            Check.True(readOnly.Calls.SequenceEqual(new[] { "nice 10", "as " + (8L << 30).ToString(System.Globalization.CultureInfo.InvariantCulture) }), string.Join(",", readOnly.Calls));
            var rootNamespace = new FakeLimitHost { Cgroup = null, Writable = false };
            Check.Equal("none", new ProcessResourceLimits(rootNamespace).Apply(1234, "x", new ProcessLimits()).Mode);
            Check.Equal("none", new ProcessResourceLimits(new FakeLimitHost { Linux = false }).Apply(1234, "x", new ProcessLimits(Nice: 5)).Mode);
        });
    }
}
