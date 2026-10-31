using System.Net.Http.Headers;
using AutoSourcarr.Core.Concurrency;

namespace AutoSourcarr.Core.Sources;

/// <summary>Retains its client lease through actual body creation, reads and physical closure.</summary>
public sealed class OwnedUpstreamResponse : IDisposable, IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly HttpRequestMessage _request;
    private readonly CancellationTokenSource _deadline;
    private readonly Action<Exception?> _release;
    private readonly OperationOwner _operations = new();
    private readonly object _gate = new();
    private Task<Stream>? _body;
    private Task? _close;
    private CancellationTokenRegistration _callerCancellation, _rootCancellation;
    private Exception? _lateBodyCloseError;

    internal OwnedUpstreamResponse(HttpResponseMessage response, HttpRequestMessage request,
        CancellationTokenSource deadline, CancellationToken callerToken, Action<Exception?> release)
    {
        _response = response;
        _request = request;
        _deadline = deadline;
        _release = release;
        CancellationToken = deadline.Token;
        CallerCancellationToken = callerToken;
        ContentHeaders = response.Content.Headers;
    }

    // Exposing HttpResponseMessage/HttpContent would bypass body ownership.
    public HttpContentHeaders ContentHeaders { get; }
    public CancellationToken CancellationToken { get; }
    public CancellationToken CallerCancellationToken { get; }
    internal bool IsClosing => _operations.IsStopping;
    internal void ArmCallerCancellation(CancellationToken caller, CancellationToken root)
    {
        lock (_gate)
        {
            _callerCancellation = caller.UnsafeRegister(static state => ((OwnedUpstreamResponse)state!).Dispose(), this);
            if (root != caller) _rootCancellation = root.UnsafeRegister(static state => ((OwnedUpstreamResponse)state!).Dispose(), this);
        }
    }

    public Task<Stream> OpenBodyAsync()
    {
        lock (_gate)
        {
            if (_operations.IsStopping) throw new OperationCanceledException(_operations.CancellationToken);
            return _body ??= OpenCoreAsync();
        }
    }

    private async Task<Stream> OpenCoreAsync()
    {
        using var lease = _operations.Enter();
        var body = await _response.Content.ReadAsStreamAsync(CancellationToken).ConfigureAwait(false);
        if (_operations.IsStopping || CancellationToken.IsCancellationRequested)
        {
            // HttpContent can finish creating a stream after it was disposed.
            try { body.Dispose(); }
            catch (Exception error) { lock (_gate) _lateBodyCloseError = error; throw; }
            throw new OperationCanceledException(_operations.CancellationToken);
        }
        return new OwnedUpstreamBody(body, _operations, CancellationToken, Dispose, DisposeAsync);
    }

    /// <summary>Closes admission immediately. DisposeAsync/client shutdown joins physical closure.</summary>
    public void Dispose() => _ = BeginClose();
    public ValueTask DisposeAsync() => new(BeginClose());

    /// <summary>The caller is discarding its handle. Keep its original failure and join every physical close before reporting the outcome.</summary>
    internal async Task<Exception> DiscardAsync(Exception primary)
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { return new AggregateException("Discarded upstream response cleanup failed", primary, cleanup); }
        return primary;
    }

    private Task BeginClose()
    {
        lock (_gate)
        {
            if (_close is not null) return _close;
            var drain = _operations.StopAsync();
            Task cancel;
            try { cancel = _deadline.CancelAsync(); }
            catch (Exception error) { cancel = Task.FromException(error); }
            _close = CloseCoreAsync(drain, cancel);
            _ = _close.ContinueWith(task => _ = task.Exception, System.Threading.CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return _close;
        }
    }

    private async Task CloseCoreAsync(Task drain, Task cancel)
    {
        await Task.Yield(); // Physical stream disposal can itself block.
        List<Exception> errors = [];
        try { _response.Dispose(); } catch (Exception error) { errors.Add(error); }
        try { _request.Dispose(); } catch (Exception error) { errors.Add(error); }
        try { await cancel.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { await drain.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { await _operations.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { if (errors.Count == 0) errors.Add(error); }
        lock (_gate) if (_lateBodyCloseError is not null) errors.Add(_lateBodyCloseError);
        try { _callerCancellation.Dispose(); } catch (Exception error) { errors.Add(error); }
        try { _rootCancellation.Dispose(); } catch (Exception error) { errors.Add(error); }
        try { _deadline.Dispose(); } catch (Exception error) { errors.Add(error); }
        Exception? failure = errors.Count == 0 ? null : new AggregateException(errors);
        _release(failure);
        if (failure is not null) throw failure;
    }
}
