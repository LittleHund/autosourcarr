namespace AutoSourcarr.Core.Persistence;

/// <summary>A callback-only view; it cannot transfer ownership of SQLite's thread-affine handle.</summary>
internal sealed class ScopedBlobStream(Stream stream, Action checkOwner) : Stream
{
    private bool _closed;

    public override bool CanRead { get { Check(); return true; } }
    public override bool CanSeek { get { Check(); return true; } }
    public override bool CanWrite { get { Check(); return false; } }
    public override long Length { get { Check(); return stream.Length; } }
    public override long Position
    {
        get { Check(); return stream.Position; }
        set { Check(); stream.Position = value; }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Check();
        return stream.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        Check();
        return stream.Read(buffer);
    }

    public override int ReadByte()
    {
        Check();
        return stream.ReadByte();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Check();
        return stream.Seek(offset, origin);
    }

    public override void Flush() => Check();
    public override void SetLength(long value) => throw Refused();
    public override void Write(byte[] buffer, int offset, int count) => throw Refused();
    public override void Write(ReadOnlySpan<byte> buffer) => throw Refused();
    public override void WriteByte(byte value) => throw Refused();
    public override Task FlushAsync(CancellationToken cancellationToken) => throw Refused();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Refused();
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw Refused();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Refused();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw Refused();
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) => throw Refused();
    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) => throw Refused();
    public override int EndRead(IAsyncResult asyncResult) => throw Refused();
    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) => throw Refused();
    public override void EndWrite(IAsyncResult asyncResult) => throw Refused();
    public override ValueTask DisposeAsync() => throw Refused();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            checkOwner();
            _closed = true;
        }
        // DatabaseSession alone disposes the underlying handle on the owner thread.
        base.Dispose(disposing);
    }

    private void Check()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        checkOwner();
    }

    private NotSupportedException Refused()
    {
        Check();
        return new NotSupportedException("Database blob streams support synchronous, read-only work inside their callback.");
    }
}
