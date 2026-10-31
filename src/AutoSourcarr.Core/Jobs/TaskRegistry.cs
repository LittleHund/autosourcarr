using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Jobs;

/// <summary>User-visible progress; runtime owners retain the actual task until it drains.</summary>
public sealed class TaskRegistry(Func<DateTimeOffset>? clock = null)
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _runtimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<ValueTask>> _cancellers = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    public event Action? Changed;
    public Func<(bool Available, string Error)>? HistoryStatus { get; set; }

    public string Create(string name, int total = 0, string message = "", string activity = "running", string context = "", string origin = "automatic", string kind = "")
    {
        if (activity is not ("running" or "queued" or "waiting") || origin is not ("manual" or "automatic"))
            throw new ArgumentException("Invalid task activity or origin");
        var now = Now(); var id = Guid.NewGuid().ToString("N")[..12];
        var row = new TaskRecord { Id = id, Name = name.Trim() is { Length: > 0 } title ? title : "Background task", Total = Math.Max(0, total),
            Message = message, CreatedAt = now, UpdatedAt = now, StartedAt = activity == "running" ? now : "", WaitingSince = activity == "running" ? "" : now,
            Activity = activity, Context = Limit(context, 240), Origin = origin, Kind = Limit(kind, 64) };
        lock (_gate) { _records.Add(id, row); PruneLocked(); }
        Changed?.Invoke(); return id;
    }

    public void MarkWaiting(string id, string message, bool queued = false) => UpdateRunning(id, row => row with
    { WaitingSince = row.WaitingSince.Length > 0 ? row.WaitingSince : Now(), Activity = queued ? "queued" : "waiting", Message = message });
    public void MarkActive(string id, bool resetEstimate = true, string? message = null) => UpdateRunning(id, row => row with
    { Activity = "running", WaitingSince = "", StartedAt = resetEstimate || row.StartedAt.Length == 0 ? Now() : row.StartedAt, Message = message ?? row.Message });
    public void Update(string id, int? current = null, int? total = null, string? message = null) => UpdateRunning(id, row => row with
    { Current = current is { } value ? Math.Max(0, value) : row.Current, Total = total is { } count ? Math.Max(0, count) : row.Total, Message = message ?? row.Message });
    public void Advance(string id, int amount = 1, string? message = null) => UpdateRunning(id, row => row with
    { Current = (int)Math.Clamp((long)row.Current + amount, 0, row.Total > 0 ? row.Total : int.MaxValue), Message = message ?? row.Message });
    public void Complete(string id, string? message = null) => Finish(id, row => row with
    { State = "completed", Current = row.Total > 0 ? row.Total : row.Current, Message = message ?? row.Message });
    public void Fail(string id, string error, string? message = null) => Finish(id, row => row with
    { State = "failed", Error = error.Length > 0 ? error : "Task failed", Message = message ?? row.Message });
    public void Cancel(string id, string message = "Cancelled by user") => Finish(id, row => row with
    { State = "failed", CancelRequested = true, Error = message, Message = message });
    public bool IsRunning(string id) { lock (_gate) return _records.TryGetValue(id, out var row) && row.State == "running"; }
    public bool IsCancellationRequested(string id) { lock (_gate) return _records.TryGetValue(id, out var row) && row.CancelRequested; }

    public Task BindRuntimeAsync(string id, Task runtime, CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var row) || row.State != "running") return ObserveAsync(id, runtime);
            _runtimes[id] = cancellation; _records[id] = row with { Cancellable = true, UpdatedAt = Now() };
        }
        Changed?.Invoke(); return ObserveAsync(id, runtime);
    }
    public void BindCanceller(string id, Func<ValueTask> callback)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var row) || row.State != "running") return;
            _cancellers[id] = callback; _records[id] = row with { Cancellable = true, UpdatedAt = Now() };
        }
        Changed?.Invoke();
    }
    public async Task<bool> RequestCancelAsync(string id)
    {
        CancellationTokenSource? runtime;
        Func<ValueTask>? callback;
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var row) || row.State != "running" || !row.Cancellable) return false;
            runtime = _runtimes.GetValueOrDefault(id); callback = _cancellers.GetValueOrDefault(id);
            _records[id] = row with { CancelRequested = true };
        }
        // Visibility changes immediately; the supervisor still joins physical work.
        Fail(id, "Cancelled by user", "Cancelled by user");
        if (callback is not null) try { await callback().ConfigureAwait(false); } catch (Exception) { }
        if (runtime is not null) try { await runtime.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        return true;
    }

    public JsonObject ClearHistory()
    {
        int completed; int failed;
        lock (_gate)
        {
            var removable = _records.Values.Where(row => row.State != "running").ToArray();
            completed = removable.Count(row => row.State == "completed"); failed = removable.Count(row => row.State == "failed");
            foreach (var row in removable) RemoveLocked(row.Id);
        }
        Changed?.Invoke(); return new() { ["cleared"] = completed + failed, ["completed"] = completed, ["failed"] = failed };
    }

    public JsonObject Snapshot()
    {
        TaskRecord[] rows;
        lock (_gate) { PruneLocked(); rows = _records.Values.ToArray(); }
        var running = rows.Where(row => row.State == "running").OrderBy(row => row.Activity switch { "queued" => 1, "waiting" => 2, _ => 0 }).ThenBy(row => row.CreatedAt, StringComparer.Ordinal).ToArray();
        var completed = rows.Where(row => row.State == "completed").OrderByDescending(row => row.UpdatedAt, StringComparer.Ordinal).ToArray();
        var failed = rows.Where(row => row.State == "failed").OrderByDescending(row => row.UpdatedAt, StringComparer.Ordinal).ToArray();
        var status = HistoryStatus?.Invoke() ?? (false, "");
        return new() { ["running"] = running.Length, ["active"] = running.Count(row => row.Activity == "running"), ["queued"] = running.Count(row => row.Activity == "queued"),
            ["waiting"] = running.Count(row => row.Activity == "waiting"), ["completed"] = completed.Length, ["failed"] = failed.Length,
            ["items"] = new JsonArray(running.Concat(completed.Take(20)).Concat(failed.Take(20)).Select(row => (JsonNode?)WithTiming(row)).ToArray()),
            ["durable"] = status.Item1, ["history_error"] = status.Item2 };
    }

    public JsonObject History(int offset = 0, int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(limit));
        TaskRecord[] rows;
        lock (_gate) { PruneLocked(); rows = _records.Values.Where(row => row.State != "running").OrderByDescending(row => row.UpdatedAt, StringComparer.Ordinal).ToArray(); }
        return new() { ["items"] = JsonSerializer.SerializeToNode(rows.Skip(offset).Take(limit), JsonOptions), ["total"] = rows.Length, ["offset"] = offset, ["limit"] = limit,
            ["note"] = "Up to 300 finished general tasks retained for seven days. Interrupted work is never replayed automatically." };
    }

    public IReadOnlyList<TaskRecord> PersistentSnapshot()
    {
        lock (_gate)
        {
            PruneLocked();
            return _records.Values.Where(row => row.State == "running").Concat(_records.Values.Where(row => row.State != "running")
                .OrderByDescending(row => row.UpdatedAt, StringComparer.Ordinal).Take(300)).ToArray();
        }
    }
    public void Restore(IEnumerable<TaskRecord> records)
    {
        lock (_gate)
        {
            foreach (var value in records)
            {
                if (_records.ContainsKey(value.Id)) continue;
                var row = value with { Cancellable = false };
                if (row.State == "running") row = row with { State = "failed", Activity = "interrupted", Interrupted = true, FinishedAt = Now(), UpdatedAt = Now(),
                    Message = "Interrupted by restart; review the original operation before retrying", Error = "Previous process ended before completion was recorded" };
                _records.Add(row.Id, row);
            }
            PruneLocked();
        }
        Changed?.Invoke();
    }

    private async Task ObserveAsync(string id, Task runtime)
    {
        try { await runtime.ConfigureAwait(false); }
        catch (OperationCanceledException) { Cancel(id); }
        catch (Exception error) { Fail(id, error.Message.Length > 0 ? error.Message : error.GetType().Name); }
    }
    private void UpdateRunning(string id, Func<TaskRecord, TaskRecord> update)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var row) || row.State != "running") return;
            _records[id] = update(row) with { UpdatedAt = Now() };
        }
        Changed?.Invoke();
    }
    private void Finish(string id, Func<TaskRecord, TaskRecord> update)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var row) || row.State != "running") return;
            var now = Now(); _records[id] = update(row) with { FinishedAt = now, UpdatedAt = now, Cancellable = false };
            _runtimes.Remove(id); _cancellers.Remove(id); PruneLocked();
        }
        Changed?.Invoke();
    }
    private JsonObject WithTiming(TaskRecord row)
    {
        var result = JsonSerializer.SerializeToNode(row, JsonOptions)!.AsObject();
        var waiting = row.Activity is "queued" or "waiting";
        var raw = waiting ? (row.WaitingSince.Length > 0 ? row.WaitingSince : row.CreatedAt) : (row.StartedAt.Length > 0 ? row.StartedAt : row.CreatedAt);
        var elapsed = DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start)
            ? Math.Max(0, ((DateTimeOffset.TryParse(row.FinishedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end) ? end : _clock()) - start).TotalSeconds) : 0;
        double? rate = null; double? eta = null;
        if (row.State == "running" && !waiting && row.Total > 0 && row.Current > 0 && elapsed >= .25)
        { rate = row.Current / elapsed; eta = row.Current >= row.Total ? 0 : Math.Min(30 * 86400, Math.Max(0, (row.Total - row.Current) / rate.Value)); }
        result["elapsed_seconds"] = waiting ? 0 : (long)Math.Round(elapsed); result["waiting_seconds"] = waiting ? (long)Math.Round(elapsed) : 0;
        result["eta_seconds"] = eta is { } remaining ? JsonValue.Create((long)Math.Round(remaining)) : null;
        result["rate_per_second"] = rate is { } speed ? JsonValue.Create(Math.Round(speed, 6)) : null;
        return result;
    }
    private void PruneLocked()
    {
        var expired = _records.Values.Where(row => row.State != "running" && DateTimeOffset.TryParse(row.FinishedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end)
            && (_clock() - end).TotalDays > 7).Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in _records.Values.Where(row => row.State != "running" && !expired.Contains(row.Id)).OrderByDescending(row => row.UpdatedAt, StringComparer.Ordinal).Skip(300)) expired.Add(row.Id);
        foreach (var id in expired) RemoveLocked(id);
    }
    private void RemoveLocked(string id) { _records.Remove(id); _runtimes.Remove(id); _cancellers.Remove(id); }
    private string Now() => _clock().ToString("O", CultureInfo.InvariantCulture);
    private static string Limit(string text, int length) => string.Concat(text.EnumerateRunes().Take(length).Select(rune => rune.ToString()));
}
