using System.Net;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Sources;

public enum SourceTransportPlane { Engine, Assets, CapabilityAbout }

/// <summary>Returns a new, exclusively owned handler/pool on every call. A throwing factory must join cleanup of its partial allocations.</summary>
public interface ISourceTransportFactory
{
    HttpMessageHandler Create(SourceTransportPlane plane);
}

internal sealed class DefaultSourceTransportFactory(AutoSourcarrSettings settings) : ISourceTransportFactory
{
    public HttpMessageHandler Create(SourceTransportPlane plane) => plane == SourceTransportPlane.CapabilityAbout
        ? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.None, MaxResponseHeadersLength = 64,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        }
        : SuwayomiClient.CreateTransport(settings);
}

/// <summary>Opaque authority for one fenced transport. This is neither a native-process drain nor readiness evidence.</summary>
public sealed class SourceTransportPause
{
    internal SourceTransportPause(SourceTransportManager owner, SourceTransportEpoch epoch) { Owner = owner; Epoch = epoch; }
    internal SourceTransportManager Owner { get; }
    internal SourceTransportEpoch Epoch { get; }
    internal Task<SourceTransportDrain> Work { get; set; } = null!;
    internal SourceTransportDrain? Drain { get; set; }
}

/// <summary>In-memory authority for one genuinely joined HTTP retirement. Cannot be reconstructed or reused after successful resume.</summary>
public sealed class SourceTransportDrain
{
    internal SourceTransportDrain(SourceTransportPause pause) => Pause = pause;
    internal SourceTransportPause Pause { get; }
    internal bool Consumed { get; set; }
}

/// <summary>A legacy test about handler transfers ownership to the epoch on first use and cannot be reused by another epoch.</summary>
internal sealed class SourceAboutTransportRegistration(HttpMessageHandler handler)
{
    private readonly object _gate = new();
    private SourceTransportEpoch? _epoch;
    private HttpClient? _client;
    private bool _disposed;
    public HttpClient Client(SourceTransportEpoch epoch)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SourceAboutTransportRegistration));
            if (_epoch is not null && !ReferenceEquals(_epoch, epoch)) throw new NotSupportedException("A one-shot about handler cannot be reused after transport retirement.");
            _epoch = epoch;
            return _client ??= new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            // Once used, the epoch is the sole physical disposer, including on capability-service shutdown.
            if (_epoch is null) handler.Dispose();
        }
    }
}
