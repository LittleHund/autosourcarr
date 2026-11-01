using System.Diagnostics;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Jobs;

namespace AutoSourcarr.Core.Sources;

public interface IRepairHealthObservations
{
    Task<(JsonArray Jobs, JsonObject Operation, JsonObject Histories)> ReadAsync(CancellationToken cancellationToken);
}
public sealed class SourceCheckConflictException(string message) : Exception(message);

/// <summary>Daily and explicit installed-source checks share per-site pacing, not UI admission. <c>onWorking</c> is told the
/// package of every source whose check passed (it must return quickly; REP-1 queues the last-working keep).</summary>
public sealed class SourceHealthCoordinator : IAsyncDisposable
{
    private sealed class Job(string taskId, HashSet<string> ids, CancellationTokenSource cancel, IDisposable lease)
    {
        public string TaskId { get; } = taskId;
        public HashSet<string> Ids { get; } = ids;
        public CancellationTokenSource Cancel { get; } = cancel;
        public IDisposable Lease { get; } = lease;
        public TaskCompletionSource Start { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Work { get; set; } = Task.CompletedTask;
        public Task Observer { get; set; } = Task.CompletedTask;
    }
    private readonly object _gate = new();
    private readonly SourceHealthStore _store;
    private readonly SuwayomiClient _client;
    private readonly SourceHealthProbe _probe;
    private readonly BoundedWorkScheduler _io;
    private readonly WholeTaskCoordinator _lanes;
    private readonly TaskRegistry _tasks;
    private readonly IRepairHealthObservations _repairs;
    private readonly Func<bool> _engineReady;
    private readonly SupportPrivacy _privacy;
    private readonly Func<double> _clock;
    private readonly TimeSpan _pacing;
    private readonly Action<string>? _onWorking;
    private readonly OperationOwner _owner = new();
    private readonly SemaphoreSlim _tick = new(1, 1);
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _siteLocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _siteLast = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _checking = new(StringComparer.Ordinal);
    private Task? _dispose;

    public SourceHealthCoordinator(SourceHealthStore store, SuwayomiClient client, BoundedWorkScheduler io, WholeTaskCoordinator lanes,
        TaskRegistry tasks, IRepairHealthObservations repairs, Func<bool> engineReady, SupportPrivacy privacy,
        Func<double>? clock = null, TimeSpan? pacing = null, Action<string>? onWorking = null)
    {
        _onWorking = onWorking;
        _store = store; _client = client; _io = io; _lanes = lanes; _tasks = tasks; _repairs = repairs; _engineReady = engineReady; _privacy = privacy;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0); _pacing = pacing ?? TimeSpan.FromSeconds(3);
        _probe = new(client, io, _pacing);
    }

    public async Task<JsonObject> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _owner.Enter();
        using var linked = Lifetime(cancellationToken);
        var repair = await _repairs.ReadAsync(linked.Token).ConfigureAwait(false);
        var snapshot = await _io.RunAsync(() => { _store.UpdateRepairObservations(repair.Jobs, repair.Operation); return _store.Snapshot(); }, linked.Token).ConfigureAwait(false);
        foreach (var item in snapshot["items"]!.AsArray().OfType<JsonObject>())
        {
            var package = SourceResponseContract.Text(item["package_name"]);
            var history = repair.Histories[package] as JsonObject;
            history ??= repair.Histories.Select(pair => pair.Value).OfType<JsonObject>().FirstOrDefault(candidate =>
                (candidate["current"] as JsonObject)?["source_ids"] is JsonArray ids && ids.Any(id => SourceResponseContract.Identity(id) == SourceResponseContract.Identity(item["source_id"])));
            item["last_confirmed_working_at"] = history?["working_at"]?.DeepClone() ?? JsonValue.Create(0);
            item["repair_confirmation"] = (history?["current"] as JsonObject)?["status"]?.DeepClone() ?? JsonValue.Create("unknown");
        }
        lock (_gate)
        {
            snapshot["active"] = new JsonObject(_jobs.Where(pair => !pair.Value.Work.IsCompleted).Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value.TaskId))));
            snapshot["checking"] = new JsonArray(_checking.Keys.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        }
        return snapshot;
    }

    public async Task<JsonObject> ConfigureDailyAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        using var lease = _owner.Enter();
        using var linked = Lifetime(cancellationToken);
        await _io.RunAsync(() => { _store.ConfigureDaily(enabled); return 0; }, linked.Token).ConfigureAwait(false);
        if (!enabled)
        {
            Job? job; lock (_gate) job = _jobs.GetValueOrDefault("automatic");
            if (job is not null && !job.Work.IsCompleted)
            {
                try { await job.Cancel.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
                try { await job.Work.ConfigureAwait(false); } catch (OperationCanceledException) { }
                await job.Observer.ConfigureAwait(false);
            }
        }
        return await SnapshotAsync(linked.Token).ConfigureAwait(false);
    }

    public JsonObject Queue(IReadOnlyList<JsonObject> rows, string origin)
    {
        if (origin is not ("manual" or "automatic") || rows.Count == 0) throw new ArgumentException("A non-empty installed source selection and valid origin are required");
        var lease = _owner.Enter();
        try
        {
            lock (_gate)
            {
                if (_lanes.Stopping) throw new SourceWorkDeferredException("Source checks paused for maintenance");
                var ids = rows.Select(row => SourceResponseContract.Identity(row["source_id"]) ?? throw new ArgumentException("Source identity missing")).ToHashSet(StringComparer.Ordinal);
                if (_jobs.TryGetValue(origin, out var old))
                {
                    if (!old.Work.IsCompleted)
                    {
                        if (origin == "manual" && !ids.IsSubsetOf(old.Ids)) throw new SourceCheckConflictException("A manual source check is already running; finish or cancel it in Tasks");
                        return new() { ["queued"] = false, ["task_id"] = old.TaskId, ["coalesced"] = true };
                    }
                    old.Cancel.Dispose();
                }
                var taskId = _tasks.Create("Check source health", rows.Count, "Queued source samples", "queued", $"{rows.Count} source variant(s)", origin);
                var job = new Job(taskId, ids, Lifetime(CancellationToken.None), lease);
                job.Work = RunAsync(rows.Select(row => (JsonObject)row.DeepClone()).ToArray(), origin, job);
                _jobs[origin] = job;
                job.Observer = _tasks.BindRuntimeAsync(taskId, job.Work, job.Cancel);
                job.Start.TrySetResult(); lease = null!;
                return new() { ["queued"] = true, ["task_id"] = taskId };
            }
        }
        finally { lease?.Dispose(); }
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _owner.Enter();
        using var linked = Lifetime(cancellationToken);
        await _tick.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_lanes.Stopping || !_engineReady()) return;
            var snapshot = await SnapshotAsync(linked.Token).ConfigureAwait(false);
            if (snapshot["daily"]?["enabled"]?.GetValue<bool>() != true) return;
            var due = snapshot["items"]!.AsArray().OfType<JsonObject>().Where(row => row["eligible"]?.GetValue<bool>() == true && Number(row, "next_check_at") <= _clock()).ToArray();
            if (due.Length > 0) Queue(due, "automatic");
        }
        finally { _tick.Release(); }
    }
    public async Task RunMonitorAsync(Action<Exception> recordError, CancellationToken cancellationToken)
    {
        using var lease = _owner.Enter();
        using var linked = Lifetime(cancellationToken);
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), linked.Token).ConfigureAwait(false);
            try { await TickAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (Exception error) { recordError(error); }
        }
    }
    public ValueTask DisposeAsync() { lock (_gate) return new(_dispose ??= CloseAsync()); }
    private async Task CloseAsync()
    {
        await _owner.StopAsync().ConfigureAwait(false);
        Job[] jobs; lock (_gate) jobs = _jobs.Values.ToArray();
        foreach (var job in jobs) { await job.Observer.ConfigureAwait(false); job.Cancel.Dispose(); }
        foreach (var semaphore in _siteLocks.Values) semaphore.Dispose();
        _tick.Dispose(); await _owner.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(IReadOnlyList<JsonObject> rows, string origin, Job job)
    {
        var token = job.Cancel.Token; var states = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            await job.Start.Task.ConfigureAwait(false);
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (origin == "automatic" && !await _io.RunAsync(_store.DailyEnabled, token).ConfigureAwait(false)) break;
                var sid = SourceResponseContract.Identity(row["source_id"])!;
                if (origin == "automatic")
                {
                    _tasks.MarkWaiting(job.TaskId, "Waiting for an idle browser before daily source checks");
                    await _lanes.WaitForIdleAsync(token).ConfigureAwait(false);
                }
                var site = Uri.TryCreate(SourceResponseContract.Text(row["base_url"]), UriKind.Absolute, out var uri) ? uri.Host : sid;
                SemaphoreSlim siteLock;
                lock (_gate)
                {
                    if (!_siteLocks.TryGetValue(site, out siteLock!)) _siteLocks[site] = siteLock = new(1, 1);
                }
                if (siteLock.CurrentCount == 0) _tasks.MarkWaiting(job.TaskId, "Waiting for this website’s current source check");
                await siteLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var current = await _io.RunAsync(() => _store.Source(sid), token).ConfigureAwait(false);
                    if (!Due(current, origin)) { _tasks.Advance(job.TaskId); continue; }
                    double last; lock (_gate) last = _siteLast.GetValueOrDefault(site);
                    var delay = Math.Max(0, last + _pacing.TotalSeconds - Monotonic());
                    if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
                    async Task<int> Sample(CancellationToken workToken)
                    {
                        var available = await _io.RunAsync(() => _store.Source(sid), workToken).ConfigureAwait(false);
                        if (!Eligible(available, origin)) return 0;
                        await using var session = _client.OpenSession(workToken);
                        using var observationLifetime = CancellationTokenSource.CreateLinkedTokenSource(workToken, session.CancellationToken);
                        var observationToken = observationLifetime.Token;
                        var began = _clock(); var checks = new JsonArray(); string package = "", version = ""; string state, message;
                        lock (_gate) _checking[sid] = job.TaskId;
                        _tasks.Update(job.TaskId, message: "Checking " + SourceResponseContract.Text(row["name"]) + " · " + SourceResponseContract.Text(row["lang"]));
                        try
                        {
                            await _io.RunAsync(() => { session.ThrowIfRetired(); _store.Reserve(sid); return 0; }, observationToken).ConfigureAwait(false);
                            JsonObject inventory;
                            try
                            {
                                using var metadataDeadline = CancellationTokenSource.CreateLinkedTokenSource(observationToken);
                                metadataDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                                inventory = await session.ExtensionsAsync(cancellationToken: metadataDeadline.Token).ConfigureAwait(false);
                            }
                            catch (Exception error) when (!workToken.IsCancellationRequested && error is not OperationCanceledException)
                            { session.ThrowIfRetired(); throw new SourceWorkDeferredException("Installed source metadata is unavailable; no website sample was requested"); }
                            catch (OperationCanceledException) when (!workToken.IsCancellationRequested)
                            { session.ThrowIfRetired(); throw new SourceWorkDeferredException("Installed source metadata is unavailable; no website sample was requested"); }
                            var extension = inventory["extensions"]!.AsArray().OfType<JsonObject>().FirstOrDefault(item => item["isInstalled"]?.GetValue<bool>() == true
                                && (item["source"] as JsonObject)?["nodes"] is JsonArray sources && sources.OfType<JsonObject>().Any(source => SourceResponseContract.Identity(source["id"]) == sid));
                            if (extension is null) throw new InconclusiveSourceCheckException("No compatible installed extension is recorded for this source; sync sources first");
                            package = Limit(SourceResponseContract.Text(extension["pkgName"]), 240); version = Limit(SourceResponseContract.Text(extension["versionName"]), 100);
                            using var priority = SourceWorkContext.Background(origin == "automatic");
                            using var sampleDeadline = CancellationTokenSource.CreateLinkedTokenSource(observationToken);
                            sampleDeadline.CancelAfter(TimeSpan.FromMinutes(3));
                            try { await _probe.CheckAsync(session, row, checks, sampleDeadline.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (!workToken.IsCancellationRequested) { session.ThrowIfRetired(); throw new SourceBudgetExceededException(); }
                            session.ThrowIfRetired();
                            state = "working"; message = "Supported catalogs, search, metadata, cover and one chapter image sample passed.";
                        }
                        catch (OperationCanceledException) when (workToken.IsCancellationRequested)
                        {
                            if (checks.Count > 0) checks[^1]!["state"] = "cancelled";
                            await _io.RunAsync(() => { _store.Finish(sid, "cancelled", "Source check cancelled", checks, origin, startedAt: began); return 0; }, CancellationToken.None).ConfigureAwait(false);
                            throw;
                        }
                        catch (Exception error)
                        {
                            if (session.Engine?.IsRetired == true)
                            { state = "deferred"; message = "Source engine transport retired during this check; no website outage or missing-source evidence was recorded."; }
                            else { state = SourceHealthFailures.Classify(error); message = SourceFailures.Summary(error, SourceResponseContract.Text(row["name"])); }
                            if (checks.Count > 0) checks[^1]!["state"] = state;
                        }
                        finally { lock (_gate) { _checking.Remove(sid); _siteLast[site] = Monotonic(); } }
                        await _io.RunAsync(() =>
                        {
                            if (session.Engine?.IsRetired == true)
                            { state = "deferred"; message = "Source engine transport retired during this check; native evidence remains unknown."; if (checks.Count > 0) checks[^1]!["state"] = state; }
                            _store.Finish(sid, state, message, checks, origin, startedAt: began, package: package, version: version); return 0;
                        }, workToken).ConfigureAwait(false);
                        // REP-1: a passed check (SRC-7) lets the owner keep this package's jar as the last working version.
                        if (state == "working" && package.Length > 0 && _onWorking is { } onWorking)
                        {
                            try { onWorking(package); }
                            catch (Exception) { }
                        }
                        states[state] = states.GetValueOrDefault(state) + 1; return 0;
                    }
                    if (origin == "manual") await _lanes.RunManualAsync(Sample, job.TaskId, token).ConfigureAwait(false);
                    else await _lanes.RunBackgroundAsync(Sample, taskId: job.TaskId, cancellationToken: token).ConfigureAwait(false);
                }
                finally { siteLock.Release(); }
                _tasks.Advance(job.TaskId);
            }
            var message = states.Count == 0 ? "No source samples were due" : string.Join(", ", states.Select(pair => pair.Value + " " + pair.Key.Replace('_', ' ')));
            if (states.Any(pair => pair.Key != "working")) _tasks.Fail(job.TaskId, message, message);
            else _tasks.Complete(job.TaskId, message);
        }
        catch (OperationCanceledException) { _tasks.Cancel(job.TaskId, "Source health check cancelled"); throw; }
        catch (Exception error) { _tasks.Fail(job.TaskId, _privacy.Text(error.Message)); throw; }
        finally { job.Lease.Dispose(); }
    }
    private bool Due(JsonObject? value, string origin) => Eligible(value, origin) && (origin != "automatic" || Number(value!, "next_check_at") <= _clock());
    private static bool Eligible(JsonObject? value, string origin) => value is not null && SourceResponseContract.TryLong(value["available"], out var available) && available != 0
        && (origin != "automatic" || value["eligible"]?.GetValue<bool>() == true);
    private CancellationTokenSource Lifetime(CancellationToken token) => CancellationTokenSource.CreateLinkedTokenSource(token, _owner.CancellationToken);
    private static double Number(JsonObject row, string key) => row[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number
        : SourceResponseContract.TryLong(row[key], out var whole) ? whole : 0;
    private static double Monotonic() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    private static string Limit(string value, int size) => value[..Math.Min(size, value.Length)];
}
