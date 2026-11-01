using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Diagnostics;

namespace AutoSourcarr.Core.Jobs;

public enum JobPriority { High = 0, Normal = 1, Low = 2 }

[Flags]
public enum JobFlags
{
    None = 0,
    /// <summary>Reads or writes a lot of disk; only one such job runs at a time.</summary>
    NeedsDisk = 1,
    /// <summary>Runs alone: it waits until nothing else runs, and nothing starts while it runs.</summary>
    Exclusive = 2,
    /// <summary>Runs for a long time; it does not take one of the worker slots, so short jobs keep flowing.</summary>
    LongRunning = 4,
}

/// <summary>
/// PERF-2: one entry per kind of heavy work. <see cref="RunLimit"/> caps how many of this type run at once,
/// <see cref="StallTimeout"/> is the watchdog limit without progress (PERF-3), <see cref="MaxAttempts"/> and
/// <see cref="RetryBase"/> give the back-off for failed or frozen runs (base, doubling per failure, at most one hour).
/// </summary>
public sealed record JobType(string Name, string Title, JobPriority Priority = JobPriority.Normal, JobFlags Flags = JobFlags.None,
    int RunLimit = 1, TimeSpan? StallTimeout = null, int MaxAttempts = 1, TimeSpan? RetryBase = null, bool Cancellable = true)
{
    public TimeSpan Stall => StallTimeout ?? TimeSpan.FromMinutes(10);
    public TimeSpan Retry => RetryBase ?? TimeSpan.FromSeconds(60);
}

public sealed record JobQueueOptions
{
    /// <summary>Worker slots for jobs that are not long-running (Sonarr uses 3).</summary>
    public int Workers { get; init; } = 3;
    /// <summary>A job waiting this long moves up one priority level, so Low work is never starved by a stream of High work.</summary>
    public TimeSpan Aging { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>After a frozen or cancelled job is told to stop, its slot is released at the latest after this time.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromHours(1);
}

public enum JobOutcome { Completed, Failed, Frozen, Cancelled }

public sealed record JobHandle(string Id, bool Merged, Task<JobOutcome> Completion);

/// <summary>What a running job sees: its cancellation, its attempt number and the progress/heartbeat calls the watchdog watches.</summary>
public sealed class JobContext
{
    private readonly JobQueue _queue;
    private readonly JobQueue.Job _job;
    internal JobContext(JobQueue queue, JobQueue.Job job, int attempt, CancellationToken token) { _queue = queue; _job = job; Attempt = attempt; CancellationToken = token; }
    public string Id => _job.Id;
    public string Key => _job.Key;
    public int Attempt { get; }
    public CancellationToken CancellationToken { get; }
    /// <summary>Progress for the task list (also a heartbeat).</summary>
    public void Report(int? current = null, int? total = null, string? message = null) => _queue.Progress(_job, Attempt, current, total, message);
    /// <summary>Tells the watchdog the job is still alive without changing what the task list shows.</summary>
    public void Heartbeat() => _queue.Progress(_job, Attempt, null, null, null);
    /// <summary>The message the finished task keeps in the Completed list.</summary>
    public void Result(string message) => _job.ResultMessage = message;
    /// <summary>
    /// True when this job's cancellation came from a person (Cancel in Tasks): not a shutdown (DisposeAsync), a
    /// maintenance pause (StopBackgroundAsync, which records its reason) or the watchdog stopping a frozen job.
    /// </summary>
    public bool CancelledByUser => _job.CancelRequested && _job.CancelReason is null && !_job.Frozen;
}

/// <summary>
/// PERF-1/2/3 job queue with Sonarr's rules: a fixed number of workers; the next job is the highest priority then the
/// oldest; an identical job (same type and key) that is queued or running is merged instead of added; needs-disk jobs
/// run one at a time; an exclusive job runs alone; long-running jobs do not take a worker slot. On top: a run limit and
/// an ETA per type, aging so no type is starved, a watchdog that turns jobs without progress into errors, and back-off
/// retries. Every job is mirrored into the <see cref="TaskRegistry"/> (Ongoing/Completed/Failed) and every change is
/// published once on the <see cref="EventStream"/>.
/// </summary>
public sealed class JobQueue : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<Job> _queued = [];
    private readonly List<Job> _running = [];
    private readonly Dictionary<string, TypeStats> _stats = new(StringComparer.Ordinal);
    private readonly TaskRegistry _tasks;
    private readonly EventStream? _events;
    private readonly ErrorRegistry? _errors;
    private readonly JobQueueOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _dispatcher;
    private long _sequence;
    private bool _paused;

    public JobQueue(TaskRegistry tasks, EventStream? events = null, ErrorRegistry? errors = null, JobQueueOptions? options = null, TimeProvider? time = null)
    {
        _tasks = tasks; _events = events; _errors = errors; _options = options ?? new();
        if (_options.Workers < 1) throw new ArgumentOutOfRangeException(nameof(options), "At least one worker is required.");
        _time = time ?? TimeProvider.System;
        _dispatcher = Task.Run(DispatchAsync);
    }

    public int Workers => _options.Workers;

    public JobHandle Enqueue(JobType type, string key, Func<JobContext, Task> work, JobPriority? priority = null,
        string origin = "automatic", string? title = null, string context = "")
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(work);
        if (type.RunLimit < 1 || type.MaxAttempts < 1) throw new ArgumentException("Run limit and attempts must be at least 1.", nameof(type));
        key ??= "";
        Job job;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
            var level = priority ?? type.Priority;
            // A job that is being cancelled or was frozen is ending; a new request must not be merged into it.
            var existing = _queued.Concat(_running).FirstOrDefault(item => item.Type.Name == type.Name && item.Key == key
                && !item.Finished && !item.CancelRequested && !item.Frozen);
            if (existing is not null)
            {
                if (level < existing.Priority && _queued.Contains(existing)) existing.Priority = level;
                Stats(type).Merged++;
                return new JobHandle(existing.Id, true, existing.Done.Task);
            }
            var now = Now();
            var id = _tasks.Create(title ?? type.Title, activity: "queued", message: "Waiting for a free worker", context: context, origin: origin, kind: type.Name);
            job = new Job(id, type, key, work, level, now, ++_sequence);
            _queued.Add(job);
            Stats(type);
        }
        // Work that cannot stop halfway (a database move) is not offered for cancellation.
        if (type.Cancellable) _tasks.BindCanceller(job.Id, () => { Cancel(job); return ValueTask.CompletedTask; });
        Publish("queued", job);
        Signal();
        return new JobHandle(job.Id, false, job.Done.Task);
    }

    /// <summary>
    /// 0.4.x stop_background_tasks / cancel_background_tasks: before a reset or restore, cancel every queued and running
    /// job and wait until running work has physically ended (at most <paramref name="grace"/>). Nothing starts until
    /// <see cref="ResumeBackground"/>; jobs added meanwhile wait in the queue.
    /// </summary>
    public async Task<bool> StopBackgroundAsync(string reason, TimeSpan? grace = null)
    {
        Job[] queued, running;
        lock (_gate)
        {
            _paused = true;
            queued = _queued.ToArray(); _queued.Clear();
            running = _running.ToArray();
            foreach (var job in queued) { job.CancelRequested = true; job.Finished = true; }
            foreach (var job in running.Where(job => job.Type.Cancellable)) { job.CancelRequested = true; job.CancelReason = reason; job.StoppedAt = Now(); }
        }
        foreach (var job in queued)
        {
            _tasks.Fail(job.Id, reason, "Not started: " + reason);
            Publish("cancelled", job, new JsonObject { ["reason"] = reason });
            job.Finish(JobOutcome.Cancelled);
        }
        foreach (var job in running.Where(job => job.Type.Cancellable)) job.Cancel();
        // Work that cannot be cancelled is still waited for, so nothing runs during the reset.
        var drained = Task.WhenAll(running.Select(job => (Task)job.Done.Task));
        var finished = await Task.WhenAny(drained, Task.Delay(grace ?? _options.CancelGrace, _time)).ConfigureAwait(false) == drained;
        _events?.Publish("jobs", new JsonObject { ["change"] = "paused", ["reason"] = reason });
        return finished;
    }

    /// <summary>0.4.x resume_background_tasks: queued work starts again.</summary>
    public void ResumeBackground()
    {
        lock (_gate) _paused = false;
        _events?.Publish("jobs", new JsonObject { ["change"] = "resumed" });
        Signal();
    }

    public bool Paused { get { lock (_gate) return _paused; } }

    /// <summary>Queue, running jobs, per-type limits/averages and an ETA for each job.</summary>
    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            var now = Now();
            var order = Ordered(now);
            var running = new JsonArray();
            foreach (var job in _running)
            {
                var stats = Stats(job.Type);
                var elapsed = (now - job.StartedAt).TotalSeconds;
                running.Add(Describe(job, now, stats.Average is { } average ? Math.Max(0, average - elapsed) : null));
            }
            // Expected start of each queued job: work ahead of it spread over the workers.
            var queued = new JsonArray();
            var ahead = _running.Where(job => !job.Type.Flags.HasFlag(JobFlags.LongRunning))
                .Sum(job => Math.Max(0, (Stats(job.Type).Average ?? 0) - (now - job.StartedAt).TotalSeconds));
            var position = 0;
            foreach (var job in order)
            {
                var waitingFor = job.NotBefore > now ? (job.NotBefore - now).TotalSeconds : 0;
                var start = Math.Max(waitingFor, ahead / _options.Workers);
                var item = Describe(job, now, null);
                item["position"] = ++position;
                item["eta_start_seconds"] = (long)Math.Round(start);
                if (Stats(job.Type).Average is { } duration)
                    item["eta_seconds"] = (long)Math.Round(start + duration);
                queued.Add(item);
                if (!job.Type.Flags.HasFlag(JobFlags.LongRunning)) ahead += Stats(job.Type).Average ?? 0;
            }
            var types = new JsonArray();
            foreach (var (name, stats) in _stats.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                types.Add(new JsonObject
                {
                    ["type"] = name, ["title"] = stats.Type.Title, ["priority"] = stats.Type.Priority.ToString().ToLowerInvariant(),
                    ["flags"] = Flags(stats.Type.Flags), ["run_limit"] = stats.Type.RunLimit,
                    ["running"] = _running.Count(job => job.Type.Name == name), ["queued"] = _queued.Count(job => job.Type.Name == name),
                    ["completed"] = stats.Completed, ["failed"] = stats.Failed, ["frozen"] = stats.Frozen, ["merged"] = stats.Merged,
                    ["average_seconds"] = stats.Average is { } average ? JsonValue.Create(Math.Round(average, 3)) : null,
                    ["stall_timeout_seconds"] = (long)stats.Type.Stall.TotalSeconds, ["max_attempts"] = stats.Type.MaxAttempts,
                });
            return new JsonObject
            {
                ["workers"] = _options.Workers, ["paused"] = _paused,
                ["busy_workers"] = _running.Count(job => !job.Type.Flags.HasFlag(JobFlags.LongRunning)),
                ["running"] = running, ["queued"] = queued, ["types"] = types,
            };
        }
    }

    /// <summary>Adds job fields (type, priority, ETA from the type's average) to the matching rows of a task snapshot.</summary>
    public JsonObject Annotate(JsonObject taskSnapshot)
    {
        var jobs = Snapshot();
        var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var list in new[] { jobs["running"]!.AsArray(), jobs["queued"]!.AsArray() })
            foreach (var node in list)
                byId[node!["id"]!.GetValue<string>()] = node.AsObject();
        if (taskSnapshot["items"] is JsonArray items)
            foreach (var node in items)
                if (node is JsonObject row && row["id"]?.GetValue<string>() is { } id && byId.TryGetValue(id, out var job))
                {
                    row["job_type"] = job["type"]!.DeepClone();
                    row["priority"] = job["priority"]!.DeepClone();
                    row["attempt"] = job["attempt"]!.DeepClone();
                    if (job["position"] is { } position) row["queue_position"] = position.DeepClone();
                    if (job["eta_start_seconds"] is { } start) row["eta_start_seconds"] = start.DeepClone();
                    if (row["eta_seconds"] is null && job["eta_seconds"] is { } eta) row["eta_seconds"] = eta.DeepClone();
                }
        taskSnapshot["jobs"] = new JsonObject { ["workers"] = jobs["workers"]!.DeepClone(), ["busy_workers"] = jobs["busy_workers"]!.DeepClone(), ["types"] = jobs["types"]!.DeepClone() };
        return taskSnapshot;
    }

    public async ValueTask DisposeAsync()
    {
        Job[] running;
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            _stop.Cancel();
            foreach (var job in _queued)
            {
                _tasks.Fail(job.Id, "AutoSourcarr stopped before this job ran", "Not started: AutoSourcarr stopped");
                job.Finish(JobOutcome.Cancelled);
            }
            _queued.Clear();
            running = _running.ToArray();
        }
        foreach (var job in running) job.Cancel();
        Signal();
        try { await _dispatcher.ConfigureAwait(false); } catch (OperationCanceledException) { }
        var deadline = Task.Delay(_options.CancelGrace, _time);
        await Task.WhenAny(Task.WhenAll(running.Select(job => job.Runtime ?? Task.CompletedTask)), deadline).ConfigureAwait(false);
        _stop.Dispose();
    }

    internal void Progress(Job job, int attempt, int? current, int? total, string? message)
    {
        lock (_gate)
        {
            if (job.Finished || job.Frozen || job.Attempt != attempt || !_running.Contains(job)) return;
            job.Heartbeat = Now();
        }
        if (current is not null || total is not null || message is not null)
        {
            _tasks.Update(job.Id, current, total, message);
            Publish("progress", job);
        }
    }

    private async Task DispatchAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                StartReady();
                Watch();
                var wait = NextWake();
                await _wake.WaitAsync(wait, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                // The dispatcher must never die; record and keep going.
                _errors?.Record("Jobs", "Job dispatcher error: " + error.Message, key: "jobs-dispatcher");
                try { await Task.Delay(TimeSpan.FromMilliseconds(250), _time, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private TimeSpan NextWake()
    {
        lock (_gate)
        {
            var now = Now();
            var wake = _options.WatchdogInterval;
            foreach (var job in _queued.Where(job => job.NotBefore > now))
                wake = TimeSpan.FromTicks(Math.Min(wake.Ticks, (job.NotBefore - now).Ticks));
            return wake < TimeSpan.FromMilliseconds(5) ? TimeSpan.FromMilliseconds(5) : wake;
        }
    }

    private void StartReady()
    {
        var started = new List<(Job Job, int Attempt)>();
        lock (_gate)
        {
            var now = Now();
            while (Next(now) is { } job)
            {
                _queued.Remove(job);
                _running.Add(job);
                job.StartedAt = now; job.Heartbeat = now; job.Attempt++;
                started.Add((job, job.Attempt));
            }
        }
        foreach (var (job, attempt) in started)
        {
            _tasks.MarkActive(job.Id, message: attempt > 1 ? $"Attempt {attempt} of {job.Type.MaxAttempts}" : "Running");
            Publish("started", job);
            job.Runtime = Task.Run(() => RunAsync(job, attempt));
        }
    }

    /// <summary>Sonarr's selection plus run limits and aging. Caller holds the lock.</summary>
    private Job? Next(DateTimeOffset now)
    {
        if (_paused || _running.Any(job => job.Type.Flags.HasFlag(JobFlags.Exclusive))) return null;
        var slots = _options.Workers - _running.Count(job => !job.Type.Flags.HasFlag(JobFlags.LongRunning));
        var first = true;
        foreach (var job in Ordered(now))
        {
            var head = first; first = false;
            if (job.NotBefore > now) continue;
            if (job.Type.Flags.HasFlag(JobFlags.Exclusive))
            {
                if (_running.Count == 0) return job;
                // An exclusive job at the head holds back new starts so the running ones drain and it can run.
                if (head) return null;
                continue;
            }
            if (!job.Type.Flags.HasFlag(JobFlags.LongRunning) && slots <= 0) continue;
            if (job.Type.Flags.HasFlag(JobFlags.NeedsDisk) && _running.Any(other => other.Type.Flags.HasFlag(JobFlags.NeedsDisk))) continue;
            if (_running.Count(other => other.Type.Name == job.Type.Name) >= job.Type.RunLimit) continue;
            return job;
        }
        return null;
    }

    private List<Job> Ordered(DateTimeOffset now) => _queued
        .OrderBy(job => Effective(job, now))
        .ThenBy(job => job.QueuedAt)
        .ThenBy(job => job.Sequence)
        .ToList();

    private int Effective(Job job, DateTimeOffset now)
    {
        var level = (int)job.Priority;
        if (_options.Aging > TimeSpan.Zero && level > 0)
            level -= (int)Math.Min(level, (now - job.QueuedAt).Ticks / _options.Aging.Ticks);
        return level;
    }

    private async Task RunAsync(Job job, int attempt)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        job.Bind(cancellation);
        var context = new JobContext(this, job, attempt, cancellation.Token);
        Exception? failure = null;
        using (RuntimeMetrics.Shared.Phase("job_run"))
        {
            try { await job.Work(context).ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
        }
        job.Unbind(cancellation);
        Finished(job, failure, attempt);
    }

    private void Finished(Job job, Exception? failure, int attempt)
    {
        bool retry; TimeSpan delay = TimeSpan.Zero; JobOutcome outcome;
        lock (_gate)
        {
            // A run the watchdog already released (or a later attempt of the same job) is not finished twice.
            if (job.Attempt != attempt || !_running.Remove(job)) return;
            var stats = Stats(job.Type);
            // A cancel (user or stop) wins over a freeze, and cancelled work is never retried.
            if (job.CancelRequested) { outcome = JobOutcome.Cancelled; }
            // A frozen job that still finished its work (it was only slow) completes; the freeze stays recorded.
            else if (job.Frozen && failure is not null) { outcome = JobOutcome.Frozen; }
            else if (failure is OperationCanceledException && _stop.IsCancellationRequested) { outcome = JobOutcome.Cancelled; }
            else if (failure is null)
            {
                job.Frozen = false;
                outcome = JobOutcome.Completed; stats.Completed++;
                stats.Observe((Now() - job.StartedAt).TotalSeconds);
            }
            else { outcome = JobOutcome.Failed; stats.Failed++; }
            retry = outcome is JobOutcome.Failed or JobOutcome.Frozen && !job.CancelRequested && job.Attempt < job.Type.MaxAttempts && !_stop.IsCancellationRequested;
            if (retry)
            {
                delay = Backoff(job);
                job.Frozen = false; job.StoppedAt = null; job.ResetCancel(); job.NotBefore = Now() + delay;
                _queued.Add(job);
            }
            else job.Finished = true;
        }
        var reason = outcome == JobOutcome.Frozen ? job.FrozenReason : failure?.Message is { Length: > 0 } text ? text : failure?.GetType().Name ?? "";
        if (retry)
        {
            _tasks.MarkWaiting(job.Id, $"Retrying in {Seconds(delay)} after: {reason}", queued: true);
            Publish("retry", job, new JsonObject { ["reason"] = reason, ["retry_in_seconds"] = (long)delay.TotalSeconds });
        }
        else
        {
            switch (outcome)
            {
                case JobOutcome.Completed: _tasks.Complete(job.Id, job.ResultMessage is { Length: > 0 } result ? result : "Done"); break;
                case JobOutcome.Cancelled: _tasks.Cancel(job.Id, job.CancelReason ?? "Cancelled by user"); break;
                default:
                    _tasks.Fail(job.Id, reason);
                    // A freeze was already recorded when the watchdog saw it; one freeze is one error.
                    if (outcome == JobOutcome.Failed) _errors?.Record("Jobs", $"{job.Type.Title} failed: {reason}", key: "job-failed:" + job.Type.Name);
                    break;
            }
            Publish(outcome.ToString().ToLowerInvariant(), job, reason.Length > 0 ? new JsonObject { ["reason"] = reason } : null);
            job.Finish(outcome);
        }
        Signal();
    }

    private void Watch()
    {
        var frozen = new List<Job>(); var abandoned = new List<Job>();
        lock (_gate)
        {
            var now = Now();
            foreach (var job in _running)
            {
                if (!job.Frozen && now - job.Heartbeat > job.Type.Stall)
                {
                    job.Frozen = true; job.StoppedAt = now;
                    job.FrozenReason = $"Frozen: no progress for {Seconds(job.Type.Stall)}; stopped by the watchdog";
                    Stats(job.Type).Frozen++;
                    frozen.Add(job);
                }
                // An exclusive job is never abandoned: releasing it would let other work run beside it.
                else if ((job.Frozen || job.CancelRequested) && !job.Type.Flags.HasFlag(JobFlags.Exclusive)
                         && job.StoppedAt is { } stopped && now - stopped > _options.CancelGrace)
                    abandoned.Add(job);
            }
        }
        foreach (var job in frozen)
        {
            // PERF-3: a frozen job is an error the moment it is detected, not when it finally ends.
            _errors?.Record("Jobs", $"{job.Type.Title}: {job.FrozenReason}", key: "job-frozen:" + job.Type.Name);
            _tasks.Update(job.Id, message: job.FrozenReason);
            Publish("frozen", job, new JsonObject { ["reason"] = job.FrozenReason });
            job.Cancel();
        }
        foreach (var job in abandoned)
        {
            // The work ignored cancellation; free its slot so the queue keeps moving and keep the evidence.
            _errors?.Record("Jobs", $"{job.Type.Title} did not stop within {Seconds(_options.CancelGrace)} after it was cancelled; its slot was released",
                key: "job-abandoned:" + job.Type.Name);
            Finished(job, new TimeoutException(job.FrozenReason.Length > 0 ? job.FrozenReason : "Stopped by the watchdog"), job.Attempt);
        }
    }

    private void Cancel(Job job)
    {
        bool wasQueued;
        lock (_gate)
        {
            if (job.Finished) return;
            wasQueued = _queued.Remove(job);
            job.CancelRequested = true;
            if (wasQueued) job.Finished = true;
            else job.StoppedAt = Now();
        }
        if (wasQueued) { Publish("cancelled", job); job.Finish(JobOutcome.Cancelled); }
        else job.Cancel();
        Signal();
    }

    private TimeSpan Backoff(Job job)
    {
        var failures = Math.Min(job.Attempt, 6);
        var ticks = Math.Min(_options.MaxRetryDelay.Ticks, job.Type.Retry.Ticks * (1L << failures) / 2);
        return TimeSpan.FromTicks(Math.Max(job.Type.Retry.Ticks, ticks));
    }

    private TypeStats Stats(JobType type)
    {
        if (!_stats.TryGetValue(type.Name, out var stats)) _stats[type.Name] = stats = new TypeStats(type);
        else stats.Type = type;
        return stats;
    }

    private JsonObject Describe(Job job, DateTimeOffset now, double? remaining) => new()
    {
        ["id"] = job.Id, ["type"] = job.Type.Name, ["key"] = job.Key, ["priority"] = job.Priority.ToString().ToLowerInvariant(),
        ["effective_priority"] = ((JobPriority)Effective(job, now)).ToString().ToLowerInvariant(), ["flags"] = Flags(job.Type.Flags),
        ["attempt"] = job.Attempt, ["queued_at"] = job.QueuedAt.ToString("O", CultureInfo.InvariantCulture),
        ["not_before"] = job.NotBefore > now ? job.NotBefore.ToString("O", CultureInfo.InvariantCulture) : null,
        ["eta_seconds"] = remaining is { } value ? JsonValue.Create((long)Math.Round(value)) : null,
        ["frozen"] = job.Frozen,
    };

    private void Publish(string change, Job job, JsonObject? extra = null)
    {
        if (_events is null) return;
        var data = new JsonObject { ["change"] = change, ["id"] = job.Id, ["type"] = job.Type.Name, ["key"] = job.Key, ["attempt"] = job.Attempt };
        if (extra is not null) foreach (var (name, value) in extra) data[name] = value?.DeepClone();
        _events.Publish("jobs", data);
    }

    private static JsonArray Flags(JobFlags flags) => new(Enum.GetValues<JobFlags>().Where(flag => flag != JobFlags.None && flags.HasFlag(flag))
        .Select(flag => (JsonNode?)JsonValue.Create(flag switch { JobFlags.NeedsDisk => "needs_disk", JobFlags.Exclusive => "exclusive", _ => "long_running" })).ToArray());

    private static string Seconds(TimeSpan span) => span.TotalSeconds >= 120
        ? Math.Round(span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min"
        : Math.Max(0, Math.Round(span.TotalSeconds, span.TotalSeconds < 10 ? 1 : 0)).ToString(CultureInfo.InvariantCulture) + " s";

    private DateTimeOffset Now() => _time.GetUtcNow();
    private void Signal() { try { _wake.Release(); } catch (ObjectDisposedException) { } }

    internal sealed class Job(string id, JobType type, string key, Func<JobContext, Task> work, JobPriority priority, DateTimeOffset queuedAt, long sequence)
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _cancellation;
        private bool _cancelled;
        public string Id { get; } = id;
        public JobType Type { get; } = type;
        public string Key { get; } = key;
        public Func<JobContext, Task> Work { get; } = work;
        public JobPriority Priority { get; set; } = priority;
        public DateTimeOffset QueuedAt { get; } = queuedAt;
        public long Sequence { get; } = sequence;
        public DateTimeOffset NotBefore { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset Heartbeat { get; set; }
        public DateTimeOffset? StoppedAt { get; set; }
        public int Attempt { get; set; }
        public bool Frozen { get; set; }
        public string FrozenReason { get; set; } = "";
        public string? ResultMessage { get; set; }
        public string? CancelReason { get; set; }
        public bool CancelRequested { get; set; }
        public bool Finished { get; set; }
        public Task? Runtime { get; set; }
        public TaskCompletionSource<JobOutcome> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Bind(CancellationTokenSource cancellation)
        {
            lock (_gate) { _cancellation = cancellation; if (_cancelled) cancellation.Cancel(); }
        }
        public void Unbind(CancellationTokenSource cancellation) { lock (_gate) if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null; }
        public void Cancel()
        {
            lock (_gate) { _cancelled = true; try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { } }
        }
        public void ResetCancel() { lock (_gate) _cancelled = false; }
        public void Finish(JobOutcome outcome) => Done.TrySetResult(outcome);
    }

    private sealed class TypeStats(JobType type)
    {
        public JobType Type { get; set; } = type;
        public long Completed, Failed, Frozen, Merged;
        public double? Average { get; private set; }
        // Exponential average of successful run times: recent runs count most, one slow run does not dominate.
        public void Observe(double seconds) => Average = Average is { } current ? current * 0.7 + seconds * 0.3 : seconds;
    }
}
