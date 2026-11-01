using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Inventory;

namespace AutoSourcarr.Core.Repair.Migration;

/// <summary>An installed package that may need a repair: one of its website addresses and why it was picked.</summary>
public sealed record AutoRepairCandidate(string Package, string BaseUrl, string Reason);

/// <summary>
/// Runs one automatic repair attempt of a package as a visible job (REP-7) and answers the repair report.
/// <paramref name="work"/> is the repair itself: it receives a progress callback and the job's cancellation.
/// </summary>
public delegate Task<JsonObject> AutoRepairJobRunner(string package, Func<Action<string>?, CancellationToken, Task<JsonObject>> work,
    CancellationToken cancellationToken);

/// <summary>Limits of one automatic repair pass.</summary>
public sealed record AutoRepairOptions
{
    /// <summary>Most packages repaired (attempted) in one pass.</summary>
    public int MaxPerPass { get; init; } = 5;
    /// <summary>Most old addresses observed in one pass (each observation is one paced public request chain).</summary>
    public int MaxObservations { get; init; } = 20;
    /// <summary>A package attempted (repaired, refused, failed or rolled back) within this time is skipped.</summary>
    public TimeSpan Skip { get; init; } = TimeSpan.FromDays(7);
    /// <summary>Time between two scheduled passes.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromDays(1);
    /// <summary>Wait after start before the first scheduled pass (when no pass is overdue sooner).</summary>
    public TimeSpan FirstDelay { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// REP-6 automatic orphan repair (opt-in through the source maintenance watch switch). Once a day, and on demand, a
/// pass collects installed packages whose sources failed their health check or that are installed orphans; for each
/// one whose old website address now redirects to a different site it runs the domain repair on the discovery path
/// (no explicit new host). At most <see cref="AutoRepairOptions.MaxPerPass"/> packages per pass, a package attempted in
/// the last seven days is skipped, two passes never run at once and every pass ends in a summary. State lives in
/// &lt;maintenance&gt;/auto-repair.json. <see cref="RunAsync"/> is the background loop: it never throws and ends when
/// its token is cancelled.
/// </summary>
public sealed class AutoRepairWatcher
{
    public const string StateFile = "auto-repair.json";
    /// <summary>The job type of one automatic attempt (Tasks → Repairs shows it as kind "auto").</summary>
    public static readonly JobType AttemptJob = new("source-auto-repair", "Repair moved source automatically", JobPriority.Low,
        StallTimeout: TimeSpan.FromMinutes(10));
    /// <summary>Task.Delay's largest wait is about 24.8 days; waits are clamped well below it.</summary>
    private static readonly TimeSpan MaximumWait = TimeSpan.FromDays(7);
    private const int MaximumState = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly HashSet<string> FailingStates = new(StringComparer.Ordinal) { "failed", "inconclusive" };

    private readonly string _root;
    private readonly Func<bool> _enabled;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AutoRepairCandidate>>> _candidates;
    private readonly Func<string, CancellationToken, Task<JsonObject>> _observe;
    private readonly Func<string, DomainRepairOptions, Action<string>?, CancellationToken, Task<JsonObject>> _repair;
    private readonly AutoRepairOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<JsonObject>? _record;
    private readonly AutoRepairJobRunner? _runner;
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();

    /// <param name="maintenanceRoot">The app's &lt;data&gt;/source-maintenance folder (auto-repair.json is kept there).</param>
    /// <param name="enabled">The opt-in switch (SourceMaintenanceState.Watch().enabled in production).</param>
    /// <param name="candidates">Failing installed sources and installed orphans; see <see cref="Candidates"/>.</param>
    /// <param name="observe">Observes one public URL (DomainRepair.ObserveAsync's JSON: state, final_url).</param>
    /// <param name="repair">Runs the domain repair of one package (DomainRepair.RunAsync).</param>
    /// <param name="record">Receives every finished pass summary (for the app log); must not throw.</param>
    /// <param name="runner">Runs each attempt as a job (<see cref="JobRunner"/> in production); null runs the repair directly.</param>
    public AutoRepairWatcher(string maintenanceRoot, Func<bool> enabled,
        Func<CancellationToken, Task<IReadOnlyList<AutoRepairCandidate>>> candidates,
        Func<string, CancellationToken, Task<JsonObject>> observe,
        Func<string, DomainRepairOptions, Action<string>?, CancellationToken, Task<JsonObject>> repair,
        AutoRepairOptions? options = null, TimeProvider? time = null, Action<JsonObject>? record = null, AutoRepairJobRunner? runner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(maintenanceRoot);
        _root = Path.GetFullPath(maintenanceRoot);
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _observe = observe ?? throw new ArgumentNullException(nameof(observe));
        _repair = repair ?? throw new ArgumentNullException(nameof(repair));
        _options = options ?? new();
        _time = time ?? TimeProvider.System;
        _record = record;
        _runner = runner;
    }

    /// <summary>Production wiring: the watch switch, the health store and orphan inventory, the public fetcher and the domain repair.
    /// <paramref name="ready"/> (the source engine is ready) gates passes like the switch does.</summary>
    public static AutoRepairWatcher Create(string maintenanceRoot, SourceMaintenanceState maintenance, SourceHealthStore health,
        SourceOrphans orphans, DomainRepair repair, IPublicSourceFetcher fetcher, Func<bool>? ready = null, Action<JsonObject>? record = null,
        AutoRepairJobRunner? runner = null)
    {
        ArgumentNullException.ThrowIfNull(maintenance); ArgumentNullException.ThrowIfNull(repair); ArgumentNullException.ThrowIfNull(fetcher);
        return new AutoRepairWatcher(maintenanceRoot, () => WatchEnabled(maintenance.Watch()) && (ready?.Invoke() ?? true), Candidates(health, orphans),
            (url, token) => DomainRepair.ObserveAsync(fetcher, url, token),
            (package, options, progress, token) => repair.RunAsync(package, options, progress, token), record: record, runner: runner);
    }

    /// <summary>
    /// REP-7 production runner: each attempt is a job of <see cref="AttemptJob"/> (key and task context = the package,
    /// origin automatic) on <paramref name="jobs"/>, so it shows in Tasks → Repairs with progress, cancel and the
    /// watchdog. A repaired or refused attempt completes the task; any other outcome fails it with the report's message.
    /// The pass waits for the job; when the job ends without a report (cancelled, frozen, merged) a failed report is answered.
    /// </summary>
    public static AutoRepairJobRunner JobRunner(JobQueue jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        return async (package, work, cancellationToken) =>
        {
            JsonObject? result = null;
            var handle = jobs.Enqueue(AttemptJob, package, async job =>
            {
                var report = await work(text => job.Report(message: text), job.CancellationToken).ConfigureAwait(false);
                result = report;
                var state = SourceResponseContract.Text(report["state"]);
                var message = SourceResponseContract.Text(report["message"]);
                if (state is not ("repaired" or "refused")) throw new InvalidOperationException(message.Length > 0 ? message : "Automatic repair ended in state " + state);
                job.Result(message);
            }, origin: "automatic", title: "Automatic repair of source " + package, context: package);
            var outcome = await handle.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result is not null) return result;
            return new JsonObject
            {
                ["state"] = handle.Merged ? "refused" : "failed",
                ["message"] = handle.Merged ? "An automatic repair of this package was already running."
                    : "The automatic repair job ended without a report (" + outcome.ToString().ToLowerInvariant() + ").",
            };
        };
    }

    public static bool WatchEnabled(JsonObject? watch) =>
        watch?["enabled"] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled;

    /// <summary>
    /// The candidates of a pass: sources whose last health check failed or was inconclusive (with the package the check
    /// recorded) and every website address of installed orphans. Packages without an address are left out.
    /// </summary>
    public static Func<CancellationToken, Task<IReadOnlyList<AutoRepairCandidate>>> Candidates(SourceHealthStore health, SourceOrphans orphans)
    {
        ArgumentNullException.ThrowIfNull(health); ArgumentNullException.ThrowIfNull(orphans);
        return async token =>
        {
            var result = new List<AutoRepairCandidate>();
            var snapshot = await Task.Run(() => health.Snapshot(), token).ConfigureAwait(false);
            foreach (var item in (snapshot["items"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var state = SourceResponseContract.Text(item["state"]);
                var package = SourceResponseContract.Text(item["package_name"]);
                var baseUrl = SourceResponseContract.Text(item["base_url"]);
                if (FailingStates.Contains(state) && DomainRepair.IsPackageName(package) && baseUrl.Length > 0)
                    result.Add(new(package, baseUrl, "health-" + state));
            }
            var rows = await orphans.ReadInventoryAsync(token).ConfigureAwait(false);
            foreach (var row in orphans.Annotate(rows, null))
            {
                if (!(SourceResponseContract.Boolean(row["isInstalled"], out var installed) && installed)) continue;
                if (!(row["is_orphaned"] is JsonValue flag && flag.TryGetValue<bool>(out var orphaned) && orphaned)) continue;
                var package = SourceResponseContract.Text(row["pkgName"]);
                if (!DomainRepair.IsPackageName(package)) continue;
                foreach (var source in SourceResponseContract.ConnectionNodes(row["source"]))
                {
                    var baseUrl = SourceResponseContract.Text(source["baseUrl"]);
                    if (baseUrl.Length > 0) result.Add(new(package, baseUrl, "orphan"));
                }
            }
            return result;
        };
    }

    public string StatePath => Path.Combine(_root, StateFile);

    /// <summary>The saved state: packages (last attempt per package) and last_pass (the last summary).</summary>
    public JsonObject State()
    {
        lock (_gate) return (JsonObject)ReadState().DeepClone();
    }

    /// <summary>True while a pass runs.</summary>
    public bool Running => _pass.CurrentCount == 0;

    /// <summary>Wakes <see cref="RunAsync"/> for a pass now (the on-demand trigger); a pending wake is not doubled.</summary>
    public void Trigger()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// The background loop: the first pass when one is due (24 h after the last saved pass, at least
    /// <see cref="AutoRepairOptions.FirstDelay"/> after start), then once per interval or when triggered. Never throws;
    /// unexpected errors go to <paramref name="recordError"/>.
    /// </summary>
    public async Task RunAsync(Action<Exception>? recordError, CancellationToken cancellationToken)
    {
        try
        {
            var wait = TimeSpan.Zero;
            try { wait = FirstWait(); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                wait = Clamp(_options.FirstDelay);
                try { recordError?.Invoke(error); }
                catch (Exception) { }
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                await _wake.WaitAsync(Clamp(wait), cancellationToken).ConfigureAwait(false);
                try { await RunOnceAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    try { recordError?.Invoke(error); }
                    catch (Exception) { }
                }
                wait = Clamp(_options.Interval);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            try { recordError?.Invoke(error); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// One pass. Answers its summary (state done, off, busy or failed). A pass already running is not waited for
    /// (state busy); with the switch off nothing is read or changed (state off). Throws only on cancellation.
    /// </summary>
    public async Task<JsonObject> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_enabled()) return new JsonObject { ["kind"] = "auto-repair-pass", ["state"] = "off", ["message"] = "Automatic orphan repair is off." };
        if (!await _pass.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return new JsonObject { ["kind"] = "auto-repair-pass", ["state"] = "busy", ["message"] = "An automatic repair pass is already running." };
        try { return await PassAsync(cancellationToken).ConfigureAwait(false); }
        finally { _pass.Release(); }
    }

    private async Task<JsonObject> PassAsync(CancellationToken cancellationToken)
    {
        var summary = new JsonObject { ["kind"] = "auto-repair-pass", ["started_at"] = Now(), ["state"] = "running" };
        var results = new JsonArray(); var recent = new JsonArray(); var notMoved = new JsonArray();
        summary["results"] = results; summary["skipped_recent"] = recent; summary["not_moved"] = notMoved;
        IReadOnlyList<AutoRepairCandidate> candidates;
        try { candidates = await _candidates(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Finish(summary, "failed", "Failing sources could not be read: " + Short(error.Message));
        }

        // One entry per package, its distinct hosts in the order found (health failures first).
        var packages = new List<(string Package, List<(string Host, string BaseUrl)> Hosts, string Reason)>();
        foreach (var candidate in candidates)
        {
            var host = HostOf(candidate.BaseUrl);
            if (host.Length == 0 || !DomainRepair.IsPackageName(candidate.Package)) continue;
            var index = packages.FindIndex(item => item.Package == candidate.Package);
            if (index < 0) { packages.Add((candidate.Package, new List<(string Host, string BaseUrl)> { (host, candidate.BaseUrl) }, candidate.Reason)); continue; }
            if (!packages[index].Hosts.Any(item => item.Host == host)) packages[index].Hosts.Add((host, candidate.BaseUrl));
        }
        summary["candidates"] = packages.Count;

        var attempted = 0; var observed = 0; var repaired = 0;
        foreach (var (package, hosts, reason) in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempted >= _options.MaxPerPass) { summary["capped"] = true; break; }
            if (observed >= _options.MaxObservations) { summary["capped"] = true; break; }
            if (!_enabled()) { summary["stopped"] = "The switch was turned off during the pass."; break; }
            if (RecentlyAttempted(package)) { recent.Add(package); continue; }

            string? oldHost = null; string newHost = "";
            foreach (var (host, baseUrl) in hosts)
            {
                if (observed >= _options.MaxObservations) break;
                observed++;
                JsonObject observation;
                try { observation = await _observe(OriginOf(baseUrl), cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                { continue; }
                var target = RedirectTarget(observation, host);
                if (target is null) continue;
                oldHost = host; newHost = target; break;
            }
            if (oldHost is null) { notMoved.Add(package); continue; }

            attempted++;
            var entry = new JsonObject { ["package"] = package, ["reason"] = reason, ["old_host"] = oldHost, ["observed_new_host"] = newHost };
            string state, message;
            try
            {
                var attemptOptions = new DomainRepairOptions(OldHost: oldHost);
                var report = _runner is null
                    ? await _repair(package, attemptOptions, null, cancellationToken).ConfigureAwait(false)
                    : await _runner(package, (progress, token) => _repair(package, attemptOptions, progress, token), cancellationToken).ConfigureAwait(false);
                state = SourceResponseContract.Text(report["state"]); message = SourceResponseContract.Text(report["message"]);
                if (report["new_host"] is JsonValue repairedHost) entry["new_host"] = repairedHost.DeepClone();
                if (state.Length == 0) state = "failed";
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                state = "failed"; message = Short(error.Message);
            }
            entry["state"] = state; entry["message"] = message;
            results.Add(entry);
            if (state == "repaired") repaired++;
            Remember(package, state, message);
        }
        summary["observed"] = observed; summary["attempted"] = attempted; summary["repaired"] = repaired;
        var text = attempted == 0
            ? packages.Count == 0 ? "No failing or orphaned installed sources." : "No failing source has moved to a new address."
            : repaired.ToString(CultureInfo.InvariantCulture) + " of " + attempted.ToString(CultureInfo.InvariantCulture) + " moved source(s) repaired.";
        return Finish(summary, "done", text);
    }

    private JsonObject Finish(JsonObject summary, string state, string message)
    {
        summary["state"] = state; summary["message"] = message; summary["finished_at"] = Now();
        lock (_gate)
        {
            var saved = ReadState();
            saved["last_pass"] = summary.DeepClone();
            saved["last_pass_at"] = summary["finished_at"]?.DeepClone();
            WriteState(saved);
        }
        try { _record?.Invoke((JsonObject)summary.DeepClone()); }
        catch (Exception) { }
        return summary;
    }

    private bool RecentlyAttempted(string package)
    {
        lock (_gate)
        {
            var at = (ReadState()["packages"] as JsonObject)?[package] is JsonObject row ? Stamp(row["at"]) : null;
            return at is not null && _time.GetUtcNow() - at.Value < _options.Skip;
        }
    }

    private void Remember(string package, string state, string message)
    {
        lock (_gate)
        {
            var saved = ReadState();
            if (saved["packages"] is not JsonObject packages) saved["packages"] = packages = new JsonObject();
            packages[package] = new JsonObject { ["at"] = Now(), ["state"] = state, ["message"] = Short(message) };
            // Attempts older than four skip windows no longer matter.
            var horizon = _time.GetUtcNow() - _options.Skip * 4;
            foreach (var old in packages.Where(pair => pair.Value is not JsonObject row || Stamp(row["at"]) is not { } stamp || stamp < horizon)
                .Select(pair => pair.Key).ToList())
                packages.Remove(old);
            WriteState(saved);
        }
    }

    private TimeSpan FirstWait()
    {
        DateTimeOffset? last;
        lock (_gate) last = Stamp(ReadState()["last_pass_at"]);
        var first = Clamp(_options.FirstDelay);
        if (last is null) return first;
        // A saved stamp from the future (clock skew, restored backup) must not become a huge Task.Delay.
        var due = Clamp(last.Value + _options.Interval - _time.GetUtcNow());
        return due > first ? Clamp(due) : first;
    }

    /// <summary>Waits between zero and <see cref="MaximumWait"/> (Task.Delay rejects more than about 24.8 days).</summary>
    internal static TimeSpan Clamp(TimeSpan wait) =>
        wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaximumWait ? MaximumWait : wait;

    // ------------------------------------------------------------------ helpers

    /// <summary>The new host when the observation ended on a working page of another site, else null. A redirect to or
    /// through a domain parking or for-sale service (<see cref="DomainRepair.ParkingHost"/>) is not a move.</summary>
    public static string? RedirectTarget(JsonObject observation, string oldHost)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (SourceResponseContract.Text(observation["state"]) != "working") return null;
        if (DomainRepair.ParkingHost(observation) is not null) return null;
        var final = HostOf(SourceResponseContract.Text(observation["final_url"]));
        if (final.Length == 0 || DomainMigration.SameSite(final, oldHost)) return null;
        return final;
    }

    private static string HostOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "";
        try { return DomainMigration.NormalizeHost(uri.IdnHost); }
        catch (DomainMigrationException) { return ""; }
    }

    private static string OriginOf(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : baseUrl;

    private JsonObject ReadState()
    {
        var info = new FileInfo(StatePath);
        if (!info.Exists || info.LinkTarget is not null || info.Length > MaximumState) return new JsonObject();
        try { return JsonNode.Parse(File.ReadAllBytes(info.FullName)) as JsonObject ?? new JsonObject(); }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return new JsonObject(); }
    }

    private void WriteState(JsonObject state)
    {
        try { ConfigFiles.WriteAtomic(StatePath, Encoding.UTF8.GetBytes(state.ToJsonString(Indented))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private string Now() => _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Stamp(JsonNode? value) =>
        value is JsonValue text && text.TryGetValue<string>(out var raw)
        && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stamp) ? stamp : null;

    private static string Short(string value) => value.Length <= 600 ? value : value[..600];
}
