using System.Diagnostics;

namespace AutoSourcarr.Core.Diagnostics;

public sealed record RuntimePhaseSample(string Phase, long Count, int Samples, double? P50Ms, double? P95Ms);

public sealed record RuntimeDecodeSample(long Active, long PeakActive, long EncodedBytes, long PeakEncodedBytes,
    long ActiveRgbaBytes, long PeakRgbaBytes);

public sealed record RuntimeMetricsSnapshot(IReadOnlyList<RuntimePhaseSample> Phases, RuntimeDecodeSample Decode, int SamplesPerPhase, string MemoryMeasure);

/// <summary>
/// Small per-process timing samples per work phase (port of runtime_observability.py). Fixed phase names,
/// no URLs, titles, tokens or other unbounded labels; the last 128 durations per phase.
/// </summary>
public sealed class RuntimeMetrics
{
    public const int SamplesPerPhase = 128;
    public static IReadOnlyList<string> Phases { get; } =
        ["admission", "crawl", "job_run", "journal", "metadata", "observation", "projection", "quality_decode", "source"];
    public static RuntimeMetrics Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<double>> _samples = Phases.ToDictionary(name => name, _ => new Queue<double>(SamplesPerPhase), StringComparer.Ordinal);
    private readonly Dictionary<string, long> _counts = Phases.ToDictionary(name => name, _ => 0L, StringComparer.Ordinal);
    private long _active, _peakActive, _encoded, _peakEncoded, _decoded, _peakDecoded;

    /// <summary>Times one unit of work; dispose the result when it ends.</summary>
    public IDisposable Phase(string name, long encodedBytes = 0)
    {
        if (!_counts.ContainsKey(name))
            throw new ArgumentException("Unknown metric phase.", nameof(name));
        if (name == "quality_decode")
            lock (_gate)
            {
                _active++; _encoded += encodedBytes;
                _peakActive = Math.Max(_peakActive, _active); _peakEncoded = Math.Max(_peakEncoded, _encoded);
            }
        return new Timing(this, name, encodedBytes, Stopwatch.GetTimestamp());
    }

    /// <summary>Counts an estimated decoded image (4 bytes per pixel) while it is in memory.</summary>
    public IDisposable DecodedPixels(long pixels)
    {
        var estimate = Math.Max(0, pixels) * 4;
        lock (_gate) { _decoded += estimate; _peakDecoded = Math.Max(_peakDecoded, _decoded); }
        return new Release(() => { lock (_gate) _decoded -= estimate; });
    }

    public RuntimeMetricsSnapshot Snapshot()
    {
        lock (_gate)
        {
            var rows = Phases.Select(name =>
            {
                var values = _samples[name].Order().ToArray();
                return new RuntimePhaseSample(name, _counts[name], values.Length,
                    values.Length == 0 ? null : values[values.Length / 2],
                    values.Length == 0 ? null : values[Math.Min(values.Length - 1, (int)(values.Length * 0.95))]);
            }).ToArray();
            return new(rows, new(_active, _peakActive, _encoded, _peakEncoded, _decoded, _peakDecoded), SamplesPerPhase,
                "Owned encoded buffers plus 4-byte-per-pixel estimates; native decoder RSS requires a host benchmark");
        }
    }

    private void Finish(string name, long encodedBytes, long started)
    {
        var elapsed = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 3);
        lock (_gate)
        {
            _counts[name]++;
            var queue = _samples[name];
            if (queue.Count == SamplesPerPhase)
                queue.Dequeue();
            queue.Enqueue(elapsed);
            if (name == "quality_decode") { _active--; _encoded -= encodedBytes; }
        }
    }

    private sealed class Timing(RuntimeMetrics owner, string name, long encodedBytes, long started) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                owner.Finish(name, encodedBytes, started);
        }
    }

    private sealed class Release(Action action) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                action();
        }
    }
}
