using System.Diagnostics;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Core.Persistence;

/// <summary>An owned file-handle lock can span async network work without a thread-affine monitor.</summary>
internal sealed class SourceFileLease(FileStream handle, SemaphoreSlim stripe) : IAsyncDisposable
{
    private static readonly SemaphoreSlim[] Stripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private int _disposed;
    public static async Task<SourceFileLease> AcquireAsync(string path, TimeSpan timeout, CancellationToken token)
    {
        path = Path.GetFullPath(path); PrivateDocumentStore.ValidateDirectory(Path.GetDirectoryName(path)!);
        var key = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path; var stripe = Stripes[(int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)Stripes.Length)];
        var watch = Stopwatch.StartNew();
        if (!await stripe.WaitAsync(timeout, token).ConfigureAwait(false)) throw new SourceWorkDeferredException("The source repository cache is busy; availability remains unknown.");
        FileStream? stream = null;
        try
        {
            stream = PrivateDocumentStore.OpenRegular(path, writable: true, create: true, maximum: 64);
            if (stream.Length == 0) { stream.WriteByte((byte)'0'); stream.Flush(flushToDisk: true); }
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (PrivateDocumentStore.TryExclusiveLock(stream)) return new(stream, stripe);
                if (watch.Elapsed >= timeout) throw new SourceWorkDeferredException("The source repository cache is busy; availability remains unknown.");
                await Task.Delay(TimeSpan.FromMilliseconds(20), token).ConfigureAwait(false);
            }
        }
        catch { stream?.Dispose(); stripe.Release(); throw; }
    }
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        { try { PrivateDocumentStore.ReleaseExclusiveLock(handle); } finally { handle.Dispose(); stripe.Release(); } }
        return ValueTask.CompletedTask;
    }
}
