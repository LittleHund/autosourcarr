using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Jobs;

public sealed class TaskJournal(SqliteDatabase database, TaskRegistry registry, BoundedWorkScheduler io, SupportPrivacy privacy) : IAsyncDisposable
{
    private readonly Channel<bool> _changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly SemaphoreSlim _flush = new(1, 1);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly OperationOwner _owner = new();
    private readonly object _stopGate = new();
    private Task? _writer;
    private Task? _dispose;
    private bool _available;
    private string _error = "";
    public bool Available => Volatile.Read(ref _available);
    public string Error => Volatile.Read(ref _error);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _owner.Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken);
        await _lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_writer is not null) return;
            registry.HistoryStatus = () => (Available, Error);
            IReadOnlyList<TaskRecord> records;
            try { records = await io.RunAsync(Load, linked.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            { Volatile.Write(ref _error, "Task history could not be loaded; existing rows retained. Check diagnostics and storage."); return; }
            registry.Restore(records); registry.Changed += Changed;
            Volatile.Write(ref _available, true);
            _writer = RunAsync(); Changed();
        }
        finally { _lifecycle.Release(); }
    }
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _owner.Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken);
        if (!Available) throw new InvalidOperationException("Task history is unavailable; existing rows are retained.");
        await FlushCoreAsync(linked.Token).ConfigureAwait(false);
    }
    public ValueTask DisposeAsync() { lock (_stopGate) return new(_dispose ??= StopAsync()); }
    private async Task StopAsync()
    {
        await _owner.StopAsync().ConfigureAwait(false);
        registry.Changed -= Changed; _changed.Writer.TryComplete();
        try
        {
            if (_writer is not null) { await _writer.ConfigureAwait(false); await FlushCoreAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        finally
        {
            Volatile.Write(ref _available, false); registry.HistoryStatus = null;
            await _owner.DisposeAsync().ConfigureAwait(false); _flush.Dispose(); _lifecycle.Dispose();
        }
    }
    private void Changed() => _changed.Writer.TryWrite(true);
    private async Task RunAsync()
    {
        await foreach (var signal in _changed.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await Task.Delay(150).ConfigureAwait(false);
            while (_changed.Reader.TryRead(out _)) { }
            try { await FlushCoreAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { Volatile.Write(ref _error, error.GetType().Name + ": task history could not be saved"); }
        }
    }
    private IReadOnlyList<TaskRecord> Load() => database.Read(DatabaseFile.Catalog, session => session.Query(
        "SELECT record_json FROM general_task_history ORDER BY updated_at DESC LIMIT 300").Select(row =>
    {
        var value = JsonSerializer.Deserialize<TaskRecord>(row.Text("record_json"), TaskRegistry.JsonOptions);
        if (value is null || value.Id.Length == 0 || value.State is not ("running" or "completed" or "failed")) throw new JsonException("Invalid general task history");
        return value;
    }).ToArray());
    private async Task FlushCoreAsync(CancellationToken token)
    {
        await _flush.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var records = registry.PersistentSnapshot();
            await io.RunAsync(() => Write(records), token).ConfigureAwait(false);
            Volatile.Write(ref _error, "");
        }
        finally { _flush.Release(); }
    }
    private int Write(IReadOnlyList<TaskRecord> records) => database.Write(DatabaseFile.Catalog, session =>
    {
        var ids = JsonSerializer.Serialize(records.Select(row => row.Id));
        session.Execute("DELETE FROM general_task_history WHERE id NOT IN(SELECT value FROM json_each($ids))", ("$ids", ids));
        foreach (var row in records)
        {
            var safe = privacy.Scrub(JsonSerializer.SerializeToNode(row, TaskRegistry.JsonOptions))!.ToJsonString();
            session.Execute("""
                INSERT INTO general_task_history(id,record_json,updated_at) VALUES($id,$json,$at)
                ON CONFLICT(id) DO UPDATE SET record_json=excluded.record_json,updated_at=excluded.updated_at
                WHERE general_task_history.record_json<>excluded.record_json
                """, ("$id", row.Id), ("$json", safe), ("$at", row.UpdatedAt));
        }
        return records.Count;
    });
}
