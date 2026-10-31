using AutoSourcarr.Core.Repair.Runner.Protocol;

namespace AutoSourcarr.Core.Repair.Runner.Guest;

/// <summary>Owns its native file until every physical operation and close has joined.</summary>
internal sealed class GuestStorageLease(GuestArtifactStore owner, string id, GuestArtifactDescriptor descriptor, FileStream file, bool receiving, IDisposable admission) : IAsyncDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _closing;
    private long _offset;
    private bool _complete;
    internal async Task AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        if (!receiving) throw new InvalidOperationException("Not a receiving artifact");
        await WithFileAsync(output =>
        {
            owner.Physical("receive-write"); if (_complete || _offset + bytes.Length > descriptor.Length) throw new InvalidDataException("Guest input exceeded its exact length or was already sealed");
            output.Write(bytes.Span); _offset += bytes.Length; return 0;
        }, token).ConfigureAwait(false);
    }
    internal async Task<int> ReadAsync(Memory<byte> bytes, CancellationToken token)
    {
        if (receiving) throw new InvalidOperationException("Not a produced artifact");
        return await WithFileAsync(input =>
        {
            owner.Physical("produce-read"); if (_complete) throw new InvalidDataException("Guest output is already delivered");
            var wanted = (int)Math.Min(bytes.Length, descriptor.Length + 1 - _offset); var read = input.Read(bytes.Span[..wanted]); _offset += read;
            if (_offset > descriptor.Length) throw new InvalidDataException("Guest output grew beyond its exact length"); return read;
        }, token).ConfigureAwait(false);
    }
    internal async Task CompleteAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token); var held = false;
        try
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false); held = true; owner.EnsureUsable();
            if (_complete || _offset != descriptor.Length) throw new InvalidDataException("Guest artifact did not complete its exact length");
            if (receiving) await owner.CompleteReceiveAsync(id, descriptor, file, linked.Token).ConfigureAwait(false);
            _complete = true;
        }
        catch { await owner.PoisonAsync().ConfigureAwait(false); throw; }
        finally { if (held) _serial.Release(); }
    }
    private async Task<T> WithFileAsync<T>(Func<FileStream, T> action, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token); var held = false;
        try { await _serial.WaitAsync(linked.Token).ConfigureAwait(false); held = true; owner.EnsureUsable(); return await owner.IOAsync(() => action(file), linked.Token).ConfigureAwait(false); }
        catch { await owner.PoisonAsync().ConfigureAwait(false); throw; }
        finally { if (held) _serial.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate) { if (_closing is not null) return new(_closing); completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _closing = completion.Task; }
        _ = CloseAsync(completion); return new(completion.Task);
    }
    private async Task CloseAsync(TaskCompletionSource completion)
    {
        var errors = new List<Exception>();
        try
        {
            try { await _stop.CancelAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            await _serial.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_complete) await owner.PoisonAsync().ConfigureAwait(false);
                try { await owner.IOAsync(() => { try { owner.Physical("file-close"); } finally { file.Dispose(); } return 0; }, default).ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            }
            finally { _serial.Release(); }
            if (errors.Count != 0) { var error = new AggregateException("Guest file close failed after joining physical IO", errors); owner.CloseFailed(error); await owner.PoisonAsync().ConfigureAwait(false); }
            owner.Untrack(this); admission.Dispose(); _stop.Dispose(); _serial.Dispose();
            if (errors.Count == 0) completion.TrySetResult(); else completion.TrySetException(errors);
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
