using AutoSourcarr.Core.Concurrency;

namespace AutoSourcarr.Core.Sources;

/// <summary>A read-only body view. Each physical read stays owned until it actually returns.</summary>
internal sealed class OwnedUpstreamBody(Stream body, OperationOwner operations, CancellationToken deadline,
    Action close, Func<ValueTask> closeAsync) : Stream
{
    public override bool CanRead => !operations.IsStopping && body.CanRead;
    public override bool CanSeek => !operations.IsStopping && body.CanSeek;
    public override bool CanWrite => false;
    public override long Length { get { using var lease = Enter(); return body.Length; } }
    public override long Position
    {
        get { using var lease = Enter(); return body.Position; }
        set { using var lease = Enter(); body.Position = value; }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        using var lease = Enter();
        var read = body.Read(buffer, offset, count);
        CheckCancellation();
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        using var lease = Enter();
        var read = body.Read(buffer);
        CheckCancellation();
        return read;
    }

    public override int ReadByte()
    {
        using var lease = Enter();
        var value = body.ReadByte();
        CheckCancellation();
        return value;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var lease = Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline, operations.CancellationToken, cancellationToken);
        var read = await body.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override long Seek(long offset, SeekOrigin origin)
    {
        using var lease = Enter();
        return body.Seek(offset, origin);
    }

    public override void Flush() { using var lease = Enter(); }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
    public override void WriteByte(byte value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) close(); base.Dispose(disposing); }
    public override ValueTask DisposeAsync() => closeAsync();

    private IDisposable Enter()
    {
        var lease = operations.Enter();
        try { CheckCancellation(); return lease; }
        catch { lease.Dispose(); throw; }
    }

    private void CheckCancellation()
    {
        operations.CancellationToken.ThrowIfCancellationRequested();
        deadline.ThrowIfCancellationRequested();
    }
}
