using AutoSourcarr.Core.Collections;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Imaging;

public sealed record CoverData(ReadOnlyMemory<byte> Content, string ContentType);

/// <summary>Bounded per-key cover fills with independent inline waiting, disk freshness and physical ownership.</summary>
public sealed class CoverCache : IAsyncDisposable
{
    public const int MaximumImageBytes = 32 * 1024 * 1024;
    private readonly CoverCacheFiles _files;
    private readonly Func<AutoSourcarrSettings> _settings;
    private readonly BoundedWorkScheduler _io;
    private readonly TimeProvider _clock;
    private readonly Func<double> _monotonic;
    private readonly object _gate = new(), _write = new();
    private readonly OperationOwner _owner = new();
    private readonly SemaphoreSlim _loaders, _clears = new(1, 1);
    private readonly Dictionary<string, Fill> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _generations = new(StringComparer.Ordinal);
    private readonly BoundedLruCache<string, double> _failures = new(2048);
    private readonly BoundedLruCache<string, Presence> _presence = new(4096);
    private readonly int _maximumPending;
    private readonly TimeSpan _loaderDeadline;
    private long _epoch;
    private int _pending, _active;
    private bool _clearing, _closed;
    private Task? _closing;
    private int _disposed;

    public CoverCache(string dataDirectory, Func<AutoSourcarrSettings> settings, BoundedWorkScheduler io, int maximumPending = 64,
        TimeSpan? loaderDeadline = null, TimeProvider? clock = null, Func<double>? monotonic = null)
    {
        _files = new(dataDirectory); _settings = settings; _io = io; _clock = clock ?? TimeProvider.System;
        _monotonic = monotonic ?? (() => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency);
        _maximumPending = Math.Max(1, maximumPending); _loaderDeadline = loaderDeadline ?? TimeSpan.FromSeconds(30);
        if (_loaderDeadline < TimeSpan.FromMilliseconds(10)) _loaderDeadline = TimeSpan.FromMilliseconds(10);
        if (_loaderDeadline.TotalMilliseconds > 4294967294) throw new ArgumentOutOfRangeException(nameof(loaderDeadline));
        _loaders = new(Math.Max(1, settings().CoverFetchConcurrency));
    }
    public (int Pending, int Active, int Failed, int Presence) Snapshot
    { get { lock (_gate) return (_pending, _active, _failures.Count, _presence.Count); } }

    public CoverData? Read(string key)
    {
        using var owned = _owner.Enter(); return ReadCore(key);
    }
    public Task<CoverData?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
        OwnedIoAsync(() => ReadCore(key), cancellationToken);
    private CoverData? ReadCore(string key) => _files.Read(key, _settings().CoverCacheTtlSeconds, _clock.GetUtcNow());

    public void Write(string key, CoverData value)
    {
        using var owned = _owner.Enter(); lock (_write) { lock (_gate) if (_clearing || _closed) return; WriteCore(key, value); }
    }
    public Task WriteAsync(string key, CoverData value, CancellationToken cancellationToken = default) => OwnedIoAsync(() =>
    { lock (_write) { lock (_gate) if (_clearing || _closed) return 0; WriteCore(key, value); } return 0; }, cancellationToken);
    private void WriteCore(string key, CoverData value)
    {
        if (!_files.Write(key, value)) return;
        lock (_gate) { _epoch++; var ttl = _settings().CoverCacheTtlSeconds; Remember(key, true, Math.Min(30, ttl), ttl); }
    }

    /// <summary>A short-lived card hint only. Actual reads still validate their own regular file, bytes and TTL.</summary>
    public bool IsCached(string key)
    {
        IDisposable owned;
        try { owned = _owner.Enter(); } catch (OperationCanceledException) { return false; }
        using (owned)
        {
            long epoch; var ttl = _settings().CoverCacheTtlSeconds; var now = _monotonic();
            lock (_gate)
            {
                if (_presence.TryGet(key, out var known) && known.Deadline > now && known.Ttl == ttl) return known.Present;
                epoch = _epoch;
            }
            var remaining = _files.Remaining(key, ttl, _clock.GetUtcNow()); var present = remaining >= 0;
            lock (_gate)
            {
                if (epoch == _epoch) Remember(key, present, present ? Math.Min(30, remaining) : 5, ttl);
                else return _presence.TryGet(key, out var newer) && newer.Deadline > _monotonic() && newer.Ttl == ttl && newer.Present;
            }
            return present;
        }
    }

    public async Task<CoverData?> GetAsync(string key, Func<CancellationToken, Task<CoverData>> loader, TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        using var request = _owner.Enter(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken);
        var cached = await _io.RunAsync(() => ReadCore(key), linked.Token).ConfigureAwait(false); if (cached is not null) return cached;
        Fill fill;
        lock (_gate)
        {
            if (_clearing || _closed) return null;
            if (_failures.TryGet(key, out var failure) && failure > _monotonic()) return null; _failures.Remove(key);
            if (!_tasks.TryGetValue(key, out fill!))
            {
                // Count physical fills, including invalidated ones still draining, rather than only visible dictionary entries.
                if (_pending >= _maximumPending) return null;
                var lease = _owner.Enter(); var generation = new object(); var stop = CancellationTokenSource.CreateLinkedTokenSource(_owner.CancellationToken);
                fill = new(generation, stop); _pending++; _generations[key] = generation; _tasks[key] = fill;
                fill.Work = FillAsync(key, fill, loader, lease); fill.Published.TrySetResult();
            }
        }
        var inline = wait ?? TimeSpan.FromSeconds(Math.Max(0, _settings().CoverInlineWait)); if (inline <= TimeSpan.Zero) return null;
        try { return await fill.Work.WaitAsync(inline, linked.Token).ConfigureAwait(false) ?? await _io.RunAsync(() => ReadCore(key), linked.Token).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
    }

    private async Task<CoverData?> FillAsync(string key, Fill fill, Func<CancellationToken, Task<CoverData>> loader, IDisposable lease)
    {
        await fill.Published.Task.ConfigureAwait(false); var active = false;
        using var deadline = new CancellationTokenSource(_loaderDeadline); using var linked = CancellationTokenSource.CreateLinkedTokenSource(fill.Stop.Token, deadline.Token);
        try
        {
            await _loaders.WaitAsync(linked.Token).ConfigureAwait(false); active = true; lock (_gate) _active++;
            CoverData value;
            try { value = await loader(linked.Token).ConfigureAwait(false); }
            finally { lock (_gate) _active--; _loaders.Release(); active = false; }
            linked.Token.ThrowIfCancellationRequested();
            if (value.Content.IsEmpty) throw new InvalidDataException("Empty cover response");
            var current = await _io.RunAsync(() =>
            {
                lock (_write)
                {
                    lock (_gate) if (_closed || _clearing || !_generations.TryGetValue(key, out var known) || !ReferenceEquals(known, fill.Generation)) return false;
                    WriteCore(key, value); return true;
                }
            }, linked.Token).ConfigureAwait(false);
            return current ? value : null;
        }
        catch (OperationCanceledException)
        {
            if (deadline.IsCancellationRequested && !fill.Stop.IsCancellationRequested) RememberFailure(key, fill.Generation); return null;
        }
        catch (Exception) { RememberFailure(key, fill.Generation); return null; }
        finally
        {
            if (active) { lock (_gate) _active--; _loaders.Release(); }
            lock (_gate)
            {
                if (_tasks.TryGetValue(key, out var current) && ReferenceEquals(current, fill)) _tasks.Remove(key);
                if (_generations.TryGetValue(key, out var generation) && ReferenceEquals(generation, fill.Generation)) _generations.Remove(key);
                _pending--;
            }
            fill.Stop.Dispose(); lease.Dispose();
        }
    }

    public int ClearKeys(IEnumerable<string> keys)
    {
        using var owned = _owner.Enter(); return ClearKeysCore(Keys(keys));
    }
    private int ClearKeysCore(IReadOnlyList<string> keys)
    {
        lock (_write)
        {
            lock (_gate) { foreach (var key in keys) { _generations.Remove(key); _failures.Remove(key); _presence.Remove(key); } _epoch++; }
            var removed = _files.Clear(keys);
            lock (_gate) { _epoch++; foreach (var key in keys) Remember(key, false, 5, _settings().CoverCacheTtlSeconds); }
            return removed;
        }
    }
    public async Task<int> ClearKeysAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        using var owned = _owner.Enter(); var selected = Keys(keys); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken);
        await _clears.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Fill[] captured;
            lock (_gate)
            {
                captured = selected.Select(key => _tasks.GetValueOrDefault(key)).OfType<Fill>().ToArray();
                foreach (var key in selected) { _tasks.Remove(key); _generations.Remove(key); }
            }
            var errors = await CancelAndDrainAsync(captured).ConfigureAwait(false);
            var result = await _io.RunAsync(() => ClearKeysCore(selected), CancellationToken.None).ConfigureAwait(false);
            if (errors.Count != 0) throw new AggregateException("Cover invalidation drained work with cancellation errors", errors);
            cancellationToken.ThrowIfCancellationRequested(); return result;
        }
        finally { _clears.Release(); }
    }
    public async Task<int> ClearAllAsync(CancellationToken cancellationToken = default)
    {
        using var owned = _owner.Enter(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken);
        await _clears.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Fill[] captured;
            lock (_gate) { _clearing = true; captured = _tasks.Values.ToArray(); _tasks.Clear(); _generations.Clear(); _failures.Clear(); _presence.Clear(); _epoch++; }
            var errors = await CancelAndDrainAsync(captured).ConfigureAwait(false);
            var result = await _io.RunAsync(() => { lock (_write) { var removed = _files.ClearAll(); lock (_gate) { _presence.Clear(); _epoch++; } return removed; } }, CancellationToken.None).ConfigureAwait(false);
            if (errors.Count != 0) throw new AggregateException("Cover reset drained work with cancellation errors", errors);
            cancellationToken.ThrowIfCancellationRequested(); return result;
        }
        finally { lock (_gate) _clearing = false; _clears.Release(); }
    }
    public Task CloseAsync()
    {
        lock (_gate)
        {
            if (_closing is not null) return _closing; _closed = true; _generations.Clear();
            // StopAsync invokes cancellation outside its own lock and joins every physical lease.
            _closing = _owner.StopAsync(); return _closing;
        }
    }
    public async ValueTask DisposeAsync()
    {
        try { await CloseAsync().ConfigureAwait(false); }
        finally
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                try { await _owner.DisposeAsync().ConfigureAwait(false); }
                finally { _loaders.Dispose(); _clears.Dispose(); }
        }
    }

    private async Task<T> OwnedIoAsync<T>(Func<T> operation, CancellationToken token)
    {
        using var owned = _owner.Enter(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _owner.CancellationToken);
        return await _io.RunAsync(operation, linked.Token).ConfigureAwait(false);
    }
    private void RememberFailure(string key, object generation)
    { lock (_gate) if (_generations.TryGetValue(key, out var known) && ReferenceEquals(known, generation)) _failures.Set(key, _monotonic() + 30); }
    private void Remember(string key, bool present, double seconds, double ttl) => _presence.Set(key, new(_monotonic() + Math.Max(0, seconds), present, ttl));
    private static string[] Keys(IEnumerable<string> keys) => keys.Where(key => !string.IsNullOrEmpty(key)).Distinct(StringComparer.Ordinal).ToArray();
    private static async Task<IReadOnlyList<Exception>> CancelAndDrainAsync(IEnumerable<Fill> fills)
    {
        var captured = fills.ToArray(); var errors = new List<Exception>();
        foreach (var fill in captured) try { await fill.Stop.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { } catch (Exception error) { errors.Add(error); }
        foreach (var fill in captured) try { await fill.Work.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        return errors;
    }
    private sealed class Fill(object generation, CancellationTokenSource stop)
    {
        public object Generation { get; } = generation; public CancellationTokenSource Stop { get; } = stop;
        public TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CoverData?> Work { get; set; } = Task.FromResult<CoverData?>(null);
    }
    private sealed record Presence(double Deadline, bool Present, double Ttl);
}
