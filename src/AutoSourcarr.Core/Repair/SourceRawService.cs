using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Jobs;

namespace AutoSourcarr.Core.Repair;

public sealed record RawInstalledSource(JsonObject Extension, string BinarySha256, string IdentitySha256, IReadOnlyList<string> SourceIds);
public interface IRawInstalledSources
{
    bool Ready { get; }
    Task<IReadOnlyList<JsonObject>> InstalledAsync(CancellationToken cancellationToken);
    Task<RawInstalledSource?> ObserveAsync(string package, string version, CancellationToken cancellationToken);
}

/// <summary>Owns acquisition tasks; automatic idle admission is separate from filesystem and source capacity.</summary>
public sealed class SourceRawService : IAsyncDisposable
{
    private sealed class Job(string id, string? key, CancellationTokenSource cancellation, IDisposable lease)
    {
        public string Id { get; } = id;
        public string? Key { get; } = key;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public IDisposable Lease { get; } = lease;
        public TaskCompletionSource Start { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Work { get; set; } = Task.CompletedTask;
        public Task Observer { get; set; } = Task.CompletedTask;
    }
    private readonly object _gate = new();
    private readonly Func<SourceRawCatalog> _factory;
    private readonly IRawInstalledSources _installed;
    private readonly SemaphoreSlim _admin;
    private readonly Func<IReadOnlyList<string>> _indexes;
    private readonly Func<JsonArray> _admissions;
    private readonly BoundedWorkScheduler _io;
    private readonly WholeTaskCoordinator _lanes;
    private readonly TaskRegistry _tasks;
    private readonly RepairHistory? _history;
    private readonly SupportPrivacy _privacy;
    private readonly Action<string> _log;
    private readonly OperationOwner _owner = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal), _keys = new(StringComparer.Ordinal);
    private SourceRawCatalog? _catalog;
    private string _error = "";
    private bool _started;
    private Task? _stopped;
    public SourceRawService(Func<SourceRawCatalog> factory, IRawInstalledSources installed, SemaphoreSlim sharedSourceAdmin,
        Func<IReadOnlyList<string>> configuredIndexes, Func<JsonArray> cachedAdmissions, BoundedWorkScheduler io, WholeTaskCoordinator lanes,
        TaskRegistry tasks, SupportPrivacy privacy, Action<string> log, RepairHistory? history = null)
    {
        _factory = factory; _installed = installed; _admin = sharedSourceAdmin; _indexes = configuredIndexes; _admissions = cachedAdmissions;
        _io = io; _lanes = lanes; _tasks = tasks; _privacy = privacy; _log = log; _history = history;
    }
    public SourceRawCatalog? Catalog { get { lock (_gate) return _catalog; } }
    public string StorageError { get { lock (_gate) return _error; } }
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _owner.Enter(); using var linked = Lifetime(cancellationToken);
        lock (_gate) { if (_started) return; _started = true; }
        SourceRawCatalog catalog;
        try { catalog = await _io.RunAsync(_factory, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            lock (_gate) _error = "Source-code storage could not be opened (" + error.GetType().Name + "). Existing files were retained; check diagnostics and storage permissions.";
            _log(StorageError); return;
        }
        linked.Token.ThrowIfCancellationRequested();
        lock (_gate) { if (_owner.IsStopping) throw new OperationCanceledException(_owner.CancellationToken); _catalog = catalog; _error = ""; }
        foreach (var intent in catalog.Cached().Select(pair => pair.Value).OfType<JsonObject>()) if (Text(intent, "state") == "acquiring") ScheduleIntent(intent);
        Queue(BackfillAsync, "Installed source inventory", "inventory", "automatic", waitForEngine: true);
    }
    public JsonObject? Schedule(JsonObject extension, bool? manual = null, string origin = "manual")
    {
        if (Catalog is null || _owner.IsStopping) return null;
        var row = (JsonObject)extension.DeepClone(); var package = RawCodeRules.Package(Text(row, "pkgName")); var version = RawCodeRules.Version(Text(row, "versionName"));
        var key = manual == true ? null : "capture:" + package + ":" + version + ":" + manual;
        return Queue(token => CaptureAsync(row, manual, origin == "automatic", token), package, key, origin);
    }
    public JsonObject? ScheduleIntent(JsonObject intent, string origin = "manual")
    {
        if (Catalog is null || _owner.IsStopping) return null;
        var copy = (JsonObject)intent.DeepClone(); var package = RawCodeRules.Package(Text(copy, "package"));
        return Queue(async token => { await Catalog!.AcquireAsync(copy, token).ConfigureAwait(false); }, package, "acquire:" + package + ":" + copy["revision"]!.ToJsonString(), origin);
    }
    public JsonObject? DiscoverIntent(JsonObject intent, JsonObject extension)
    {
        if (Catalog is null || _owner.IsStopping) return null;
        var copy = (JsonObject)intent.DeepClone(); var installed = (JsonObject)extension.DeepClone(); var package = RawCodeRules.Package(Text(copy, "package"));
        return Queue(async token =>
        {
            var indexes = await _io.RunAsync(_indexes, token).ConfigureAwait(false);
            var result = await Catalog!.DiscoverAsync(copy, installed, indexes, token).ConfigureAwait(false);
            if (Text(result, "state") == "acquiring") await Catalog.AcquireAsync(result, token).ConfigureAwait(false);
        }, package, "discovery:" + package + ":" + copy["revision"]!.ToJsonString(), "manual");
    }
    public JsonArray PendingItems()
    {
        var active = _admissions().OfType<JsonObject>().Where(row => row["raw_only"]?.GetValue<bool>() == true && SourceOrigin.Optional(row, "state") is "admitting" or "accepted")
            .Select(row => Text(row, "package")).ToHashSet(StringComparer.Ordinal);
        var result = new JsonArray((Catalog?.PendingItems() ?? []).OfType<JsonObject>().Where(row => !active.Contains(Text(row, "raw_package"))).Select(row => (JsonNode?)row.DeepClone()).ToArray());
        var error = StorageError;
        if (error.Length != 0) result.Add(new JsonObject { ["id"] = "source-raw-storage", ["kind"] = "source-raw-acquisition", ["name"] = "Source code archive unavailable", ["state"] = "failed", ["activity"] = "waiting", ["context"] = "Source code storage", ["origin"] = "automatic", ["message"] = error, ["current"] = 0, ["total"] = 0, ["cancellable"] = false, ["created_at"] = "", ["updated_at"] = "" });
        foreach (var row in result.OfType<JsonObject>()) row["message"] = Clip(_privacy.Text(Text(row, "message")), 1800); return result;
    }
    public async Task DrainAsync()
    {
        while (true)
        {
            Task[] pending; lock (_gate) pending = _jobs.Values.Where(job => !job.Work.IsCompleted).Select(job => job.Work).ToArray();
            if (pending.Length == 0) return; await Task.WhenAll(pending.Select(ObserveCompletionAsync)).ConfigureAwait(false);
        }
    }
    public Task StopAsync()
    {
        TaskCompletionSource finished;
        lock (_gate)
        {
            if (_stopped is not null) return _stopped;
            finished = new(TaskCreationOptions.RunContinuationsAsynchronously); _stopped = finished.Task;
        }
        _ = StopOwnedAsync(finished); return finished.Task;
    }
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); await _owner.DisposeAsync().ConfigureAwait(false); }
    private async Task CaptureAsync(JsonObject extension, bool? manual, bool automatic, CancellationToken token)
    {
        var package = Text(extension, "pkgName"); var version = Text(extension, "versionName"); RawInstalledSource? current; JsonObject observed;
        if (automatic && HasRawAdmission(package)) return;
        await _admin.WaitAsync(token).ConfigureAwait(false);
        try
        {
            current = await _installed.ObserveAsync(package, version, token).ConfigureAwait(false);
            if (current is null || Text(current.Extension, "versionName") != version) return;
            if (Text(current.Extension, "pkgName") != package) throw new InvalidDataException("Installed source package changed during raw capture");
            RawCodeRules.Hash(current.BinarySha256); RawCodeRules.Hash(current.IdentitySha256);
            observed = (JsonObject)current.Extension.DeepClone();
            if (_history is not null)
                await _io.RunAsync(() => _history.Observe(package, new JsonObject { ["binary_sha256"] = current.BinarySha256, ["raw_id"] = null, ["version"] = version,
                    ["entrypoint"] = null, ["extension_lib"] = null, ["source_ids"] = new JsonArray(current.SourceIds.Order(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                    ["identity_sha256"] = current.IdentitySha256, ["job_id"] = null }), token).ConfigureAwait(false);
        }
        finally { _admin.Release(); }
        if (automatic && HasRawAdmission(package)) return;
        var catalog = Catalog!;
        var intent = await _io.RunAsync(() => catalog.Begin(observed, manual, current.BinarySha256, force: manual == true), token).ConfigureAwait(false);
        if (SourceRawCatalog.NeedsDiscovery(intent, observed, manual) && !(automatic && HasRawAdmission(package)))
        {
            var indexes = await _io.RunAsync(_indexes, token).ConfigureAwait(false);
            intent = await catalog.DiscoverAsync(intent, observed, indexes, token).ConfigureAwait(false);
        }
        if (Text(intent, "state") == "acquiring") await catalog.AcquireAsync(intent, token).ConfigureAwait(false);
    }
    private async Task BackfillAsync(CancellationToken token)
    {
        var rows = await _installed.InstalledAsync(token).ConfigureAwait(false);
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested(); var package = Text(row, "pkgName"); if (HasRawAdmission(package)) continue;
            var old = Catalog!.Status(package);
            if (old is null || Text(old, "version") != Text(row, "versionName") || Text(old, "state") == "removed" || SourceRawCatalog.NeedsDiscovery(old, row)) Schedule(row, origin: "automatic");
        }
    }
    private bool HasRawAdmission(string package) => _admissions().OfType<JsonObject>().Any(row => Text(row, "package") == package
        && row["raw_only"] is JsonValue flag && flag.TryGetValue<bool>(out var rawOnly) && rawOnly && SourceOrigin.Optional(row, "state") is "admitting" or "accepted");
    private JsonObject Queue(Func<CancellationToken, Task> operation, string package, string? key, string origin, bool waitForEngine = false)
    {
        if (origin is not ("manual" or "automatic")) throw new ArgumentException("Invalid raw-capture origin", nameof(origin));
        var lease = _owner.Enter();
        try
        {
            lock (_gate)
            {
                if (key is not null && _keys.TryGetValue(key, out var previous) && !previous.Work.IsCompleted) return new() { ["queued"] = false, ["task_id"] = previous.Id, ["coalesced"] = true };
                foreach (var old in _jobs.Values.Where(job => job.Observer.IsCompleted && job.Work.IsCompleted).Take(Math.Max(0, _jobs.Count - 128)).ToArray()) { old.Cancellation.Dispose(); _jobs.Remove(old.Id); }
                var id = _tasks.Create("Archive source code", message: "Queued source-code capture", activity: "queued", context: package, origin: origin, kind: "source-raw-acquisition");
                var job = new Job(id, key, Lifetime(CancellationToken.None), lease); job.Work = RunAsync(operation, origin, job, waitForEngine); _jobs[id] = job;
                if (key is not null) _keys[key] = job; job.Observer = _tasks.BindRuntimeAsync(id, job.Work, job.Cancellation); job.Start.TrySetResult(); lease = null!;
                return new() { ["queued"] = true, ["task_id"] = id };
            }
        }
        finally { lease?.Dispose(); }
    }
    private async Task RunAsync(Func<CancellationToken, Task> operation, string origin, Job job, bool waitForEngine)
    {
        var token = job.Cancellation.Token;
        try
        {
            await job.Start.Task.ConfigureAwait(false);
            if (waitForEngine)
                for (var wait = 0; !_installed.Ready; wait++)
                {
                    if (wait >= 90) { _tasks.Complete(job.Id, "Source-code inventory deferred until the source engine is ready."); return; }
                    _tasks.MarkWaiting(job.Id, "Waiting for the source engine before inventory capture"); await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
            if (origin == "automatic") { _tasks.MarkWaiting(job.Id, "Waiting for an idle browser before archiving source code"); await _lanes.WaitForIdleAsync(token).ConfigureAwait(false); }
            if (_serial.CurrentCount == 0) _tasks.MarkWaiting(job.Id, "Waiting for the current source-code capture");
            await _serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _tasks.MarkActive(job.Id);
                async Task<int> Run(CancellationToken workToken) { await operation(workToken).ConfigureAwait(false); return 0; }
                if (origin == "manual") await _lanes.RunManualAsync(Run, job.Id, token).ConfigureAwait(false);
                else await _lanes.RunBackgroundAsync(Run, taskId: job.Id, cancellationToken: token).ConfigureAwait(false);
            }
            finally { _serial.Release(); }
            _tasks.Complete(job.Id, "Raw-code capture finished; missing code appears in Tasks → Repairs.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { _tasks.Fail(job.Id, "Cancelled"); throw; }
        catch (Exception error) { var message = Clip(_privacy.Text(error.GetType().Name + ": " + error.Message), 1000); _log(message); _tasks.Fail(job.Id, message); }
        finally
        {
            lock (_gate) if (job.Key is not null && _keys.TryGetValue(job.Key, out var current) && ReferenceEquals(job, current)) _keys.Remove(job.Key);
            job.Lease.Dispose();
        }
    }
    private async Task StopOwnedAsync(TaskCompletionSource finished)
    {
        try
        {
            // Called after releasing the service lock; close admission synchronously
            // before any await, and invoke cancellation callbacks outside that lock.
            await _owner.StopAsync().ConfigureAwait(false);
            Job[] jobs; lock (_gate) jobs = _jobs.Values.ToArray();
            foreach (var job in jobs) { await job.Observer.ConfigureAwait(false); job.Cancellation.Dispose(); }
            _serial.Dispose(); finished.TrySetResult();
        }
        catch (Exception error) { finished.TrySetException(error); }
    }
    private static async Task ObserveCompletionAsync(Task task) { try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { } }
    private CancellationTokenSource Lifetime(CancellationToken token) => CancellationTokenSource.CreateLinkedTokenSource(token, _owner.CancellationToken);
    private static string Text(JsonObject row, string key) => RawCodeRules.Text(row, key);
    private static string Clip(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
