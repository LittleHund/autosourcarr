using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Jobs;

namespace AutoSourcarr.Core.Catalogue.Sharing;

/// <summary>Visible, cancellable manual review jobs. Shutdown joins physical SQLite/domain work.</summary>
public sealed class CatalogueReviewTasks(CatalogueExchange exchange, TaskRegistry tasks, WholeTaskCoordinator lanes, BoundedWorkScheduler io) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _running = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private bool _closed;
    private Task? _stop;
    public JsonObject Schedule(string uid, string digest, string revision, string action = "apply")
    {
        CatalogueFormat.CheckUid(uid, "decision"); if (action is not ("apply" or "ignore")) throw new ArgumentException("Choose Apply or Keep local");
        var key = uid + ":" + digest;
        lock (_gate)
        {
            if (_closed) return new() { ["queued"] = false, ["error"] = "Database maintenance or shutdown is in progress" };
            if (_running.TryGetValue(key, out var prior) && !prior.IsCompleted) return new() { ["queued"] = false, ["already_running"] = true };
            var id = tasks.Create("Review shared catalogue choice", total: 1, origin: "manual", context: uid, kind: "catalogue-choice");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            var begin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runtime = RunAsync(key, id, uid, digest, revision, action, begin.Task, cancellation);
            _running[key] = runtime; _ = tasks.BindRuntimeAsync(id, runtime, cancellation); begin.SetResult();
            return new() { ["queued"] = true, ["task_id"] = id };
        }
    }
    private async Task RunAsync(string key, string id, string uid, string digest, string revision, string action, Task begin, CancellationTokenSource cancellation)
    {
        await begin.ConfigureAwait(false);
        try
        {
            var result = await lanes.RunManualAsync(token => io.RunAsync(() => exchange.ApplyDecision(uid, digest, revision, action, id, token), token), id, cancellation.Token).ConfigureAwait(false);
            var state = result["state"]?.GetValue<string>();
            if (state is "applied" or "ignored") tasks.Complete(id, result["message"]?.GetValue<string>() ?? "Shared choice reviewed");
            else tasks.Fail(id, result["message"]?.GetValue<string>() ?? "The shared choice remains pending local review");
        }
        catch (OperationCanceledException) { tasks.Cancel(id); }
        catch (Exception error) { tasks.Fail(id, error.Message); }
        finally { lock (_gate) _running.Remove(key); cancellation.Dispose(); }
    }
    public Task StopAsync()
    {
        Task[] running;
        lock (_gate)
        {
            if (_stop is not null) return _stop;
            _closed = true; running = _running.Values.ToArray();
            _stop = StopOwnedAsync(running); return _stop;
        }
    }
    private async Task StopOwnedAsync(Task[] running)
    { await _stopping.CancelAsync().ConfigureAwait(false); await Task.WhenAll(running).ConfigureAwait(false); }
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _stopping.Dispose(); }
}
