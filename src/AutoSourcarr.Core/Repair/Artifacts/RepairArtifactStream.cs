namespace AutoSourcarr.Core.Repair.Artifacts;

/// <summary>File IO and close remain with the depot's bounded physical scheduler even after cancellation.</summary>
internal sealed class RepairArtifactStream(RepairArtifactDepot owner, FileStream file, long maximum, bool writable, IDisposable admission) : Stream
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _closing;
    public override bool CanRead => !writable; public override bool CanWrite => writable; public override bool CanSeek => false;
    public override long Length => maximum; public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (writable) throw new NotSupportedException();
        return await WithFileAsync(input => { owner.Physical("read"); return input.Read(buffer.Span); }, token).ConfigureAwait(false);
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        if (!writable) throw new NotSupportedException();
        await WithFileAsync(output => { owner.Physical("write"); if (output.Position + buffer.Length > maximum) throw new InvalidDataException("Output bytes exceed the exact private slot"); output.Write(buffer.Span); return 0; }, token).ConfigureAwait(false);
    }
    internal async Task<T> WithFileAsync<T>(Func<FileStream, T> action, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token); var held = false;
        try { await _serial.WaitAsync(linked.Token).ConfigureAwait(false); held = true; owner.EnsureUsable(); return await owner.IOAsync(() => action(file), linked.Token).ConfigureAwait(false); }
        catch { await owner.PoisonAsync().ConfigureAwait(false); throw; }
        finally { if (held) _serial.Release(); }
    }
    public override Task FlushAsync(CancellationToken token) => WithFileAsync(output => { output.Flush(true); return 0; }, token);
    public override int Read(byte[] bytes, int offset, int count) => ReadAsync(bytes.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] bytes, int offset, int count) => WriteAsync(bytes.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() => FlushAsync(default).GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    public override ValueTask DisposeAsync()
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
            try { await owner.IOAsync(() => { try { owner.Physical("close"); } finally { file.Dispose(); } return 0; }, default).ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
            finally { _serial.Release(); }
            if (errors.Count != 0)
            {
                var error = new AggregateException("Artifact stream close failed after joining physical IO", errors); owner.CloseFailed(error); await owner.PoisonAsync().ConfigureAwait(false);
            }
            owner.Untrack(this); admission.Dispose(); _stop.Dispose(); _serial.Dispose();
            if (errors.Count == 0) completion.TrySetResult(); else completion.TrySetException(errors);
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
    protected override void Dispose(bool disposing) { if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult(); base.Dispose(disposing); }
}
