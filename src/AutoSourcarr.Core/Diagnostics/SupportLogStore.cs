using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Diagnostics;

public enum CapturedOutputKind { OperationalText, PrivateProtocol }

/// <summary>Per-instance diagnostics are sanitized before entering bounded memory or disk.</summary>
public sealed class SupportLogStore : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Regex PrivateBegin = new("-----BEGIN [^-\\r\\n]*PRIVATE KEY-----", RegexOptions.CultureInvariant);
    private static readonly Regex PrivateEnd = new("-----END [^-\\r\\n]*PRIVATE KEY-----", RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly object _diskGate = new();
    private readonly object _captureGate = new();
    private readonly object _stopGate = new();
    private readonly Queue<string> _pending = new();
    private readonly Queue<string> _memory = new();
    private readonly HashSet<Stream> _captures = [];
    private readonly BoundedWorkScheduler _io;
    private readonly RuntimeIdentity _identity;
    private readonly DiskLogFiles? _files;
    private readonly OperationOwner _owner = new();
    private readonly int _maximumBytes;
    private readonly int _fileCount;
    private readonly int _pendingLimit;
    private long _dropped;
    private int _writerActive;
    private string _storageError = "";
    private Task? _dispose;

    public SupportLogStore(BoundedWorkScheduler io, RuntimeIdentity identity, SupportPrivacy privacy, string? dataDirectory = null,
        int maximumFileBytes = 512 * 1024, int fileCount = 3, int pendingLimit = 512)
    {
        _io = io; _identity = identity; Privacy = privacy;
        _maximumBytes = Math.Max(8192, maximumFileBytes); _fileCount = Math.Clamp(fileCount, 1, 5); _pendingLimit = Math.Clamp(pendingLimit, 1, 4096);
        if (dataDirectory is not null)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("A persistent data directory is required", nameof(dataDirectory));
            _files = new(Path.Combine(Path.GetFullPath(dataDirectory), "diagnostics"), _maximumBytes, _fileCount);
        }
        Repair = new(privacy, Record);
    }
    public SupportPrivacy Privacy { get; }
    public RepairDiagnosticCache Repair { get; }

    public void Record(string source, string message)
    {
        string text;
        try { text = Utf8Prefix(Privacy.Text(message), 4096); }
        catch (Exception) { text = "[diagnostic omitted: redaction failed]"; }
        var row = new JsonObject { ["id"] = Guid.NewGuid().ToString("N")[..12], ["time"] = DateTimeOffset.UtcNow.ToString("O"),
            ["app_version"] = _identity.Version, ["instance_id"] = _identity.InstanceId, ["source"] = UnicodeCaseFold.Truncate(Privacy.Text(source), 80), ["message"] = text };
        var encoded = row.ToJsonString(JsonOptions) + "\n";
        lock (_gate)
        {
            if (_pending.Count == _pendingLimit) { _pending.Dequeue(); _dropped++; }
            _pending.Enqueue(encoded);
            if (_memory.Count == _pendingLimit) _memory.Dequeue();
            _memory.Enqueue(encoded);
        }
    }
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _owner.Enter(); using var lifetime = Lifetime(cancellationToken);
        await _io.RunAsync(() => { FlushCore(); return 0; }, lifetime.Token).ConfigureAwait(false);
    }
    public async Task<JsonObject> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _owner.Enter(); using var lifetime = Lifetime(cancellationToken);
        return await _io.RunAsync(SnapshotCore, lifetime.Token).ConfigureAwait(false);
    }
    public async Task RunWriterAsync(CancellationToken cancellationToken)
    {
        using var lease = _owner.Enter(); using var lifetime = Lifetime(cancellationToken);
        if (Interlocked.CompareExchange(ref _writerActive, 1, 0) != 0) throw new InvalidOperationException("The diagnostic writer is already running");
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token).ConfigureAwait(false);
                bool pending; lock (_gate) pending = _pending.Count > 0;
                if (pending) await _io.RunAsync(() => { FlushCore(); return 0; }, lifetime.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            try { await _io.RunAsync(() => { FlushCore(); return 0; }, CancellationToken.None).ConfigureAwait(false); }
            finally { Volatile.Write(ref _writerActive, 0); }
        }
    }

    public async Task CaptureAsync(Stream input, string source, CapturedOutputKind kind, CancellationToken cancellationToken = default)
    {
        using var lease = _owner.Enter(); using var lifetime = Lifetime(cancellationToken);
        if (kind is not (CapturedOutputKind.OperationalText or CapturedOutputKind.PrivateProtocol)) throw new ArgumentOutOfRangeException(nameof(kind));
        lock (_captureGate)
        {
            if (_owner.IsStopping) throw new OperationCanceledException(_owner.CancellationToken);
            _captures.Add(input);
        }
        try
        {
            if (kind == CapturedOutputKind.PrivateProtocol)
            {
                Record(source, "[private tool protocol output omitted]");
                var discard = new byte[16384];
                while (await input.ReadAsync(discard, lifetime.Token).ConfigureAwait(false) != 0) { }
                return;
            }
            var buffer = new byte[16384];
            using var lines = new ProcessLines(this, source);
            try
            {
                int count;
                while ((count = await input.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false)) > 0)
                    lines.Write(buffer.AsSpan(0, count));
                lines.Complete();
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        catch (Exception error) when (lifetime.IsCancellationRequested && error is IOException or ObjectDisposedException)
        { throw new OperationCanceledException(lifetime.Token); }
        finally { lock (_captureGate) _captures.Remove(input); }
    }

    // The stream reader and native source relay share the same bounded parser.
    // A native capture gap suppresses this whole generation: a missing fragment
    // may contain a private-key BEGIN marker, so a newline cannot restore trust.
    private sealed class ProcessLines(SupportLogStore store, string source) : IDisposable
    {
        private readonly MemoryStream _pending = new();
        private byte[] _markerTail = [];
        private bool _omitted, _privateBlock, _privateEnd, _quarantined, _disposed;

        public void Write(ReadOnlySpan<byte> bytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_quarantined) return;
            var start = 0;
            while (start < bytes.Length)
            {
                var end = bytes[start..].IndexOfAny((byte)'\r', (byte)'\n');
                var length = end < 0 ? bytes.Length - start : end + 1;
                if (end >= 0 && bytes[start + length - 1] == '\r' && start + length < bytes.Length && bytes[start + length] == '\n') length++;
                var part = bytes.Slice(start, length);
                var probe = new byte[_markerTail.Length + length];
                try
                {
                    _markerTail.CopyTo(probe, 0); part.CopyTo(probe.AsSpan(_markerTail.Length));
                    var marker = Encoding.Latin1.GetString(probe);
                    if (!_privateBlock && PrivateBegin.IsMatch(marker)) { _privateBlock = true; store.Record(source, "[private key omitted]"); }
                    if (_privateBlock && PrivateEnd.IsMatch(marker)) _privateEnd = true;
                    CryptographicOperations.ZeroMemory(_markerTail);
                    _markerTail = probe.AsSpan(Math.Max(0, probe.Length - 80)).ToArray();
                }
                finally { CryptographicOperations.ZeroMemory(probe); }
                if (!_omitted)
                {
                    if (_pending.Length + length > 65536) { ClearPending(); _omitted = true; }
                    else _pending.Write(part);
                }
                if (end >= 0)
                {
                    if (_omitted) store.Record(source, "[oversize process line omitted]");
                    else if (!_privateBlock) store.Record(source, Encoding.UTF8.GetString(_pending.GetBuffer(), 0, (int)_pending.Length).TrimEnd('\r', '\n'));
                    if (_privateEnd) _privateBlock = false;
                    ClearPending(); _omitted = false; CryptographicOperations.ZeroMemory(_markerTail); _markerTail = []; _privateEnd = false;
                }
                start += length;
            }
        }
        public void Quarantine() { _quarantined = true; ClearPending(); CryptographicOperations.ZeroMemory(_markerTail); _markerTail = []; }
        public void Complete()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_quarantined && _pending.Length > 0 && !_privateBlock && !_omitted)
                store.Record(source, Encoding.UTF8.GetString(_pending.GetBuffer(), 0, (int)_pending.Length));
            ClearPending();
        }
        private void ClearPending()
        { CryptographicOperations.ZeroMemory(_pending.GetBuffer().AsSpan(0, (int)_pending.Length)); _pending.SetLength(0); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; ClearPending(); CryptographicOperations.ZeroMemory(_markerTail); _markerTail = []; _pending.Dispose();
        }
    }

    /// <summary>Bounded transient ingress; native IO only validates and copies a fragment.</summary>
    internal NativeSourceCapture CaptureNativeSourceOutput() => new(this);

    internal sealed class NativeSourceCapture : IAsyncDisposable
    {
        internal const int MaximumFragmentBytes = 448;
        internal const int MaximumQueuedFragments = 64;
        private sealed record Fragment(ulong Generation, byte[] Bytes);
        internal readonly record struct CaptureState(int Queued, int PeakQueued, ulong DroppedBytes, ulong QuarantinedThrough, bool Consuming, bool Closed);
        private readonly object _gate = new();
        private readonly SupportLogStore _store;
        private readonly IDisposable _lease;
        private readonly CancellationTokenSource _lifetime;
        private readonly SemaphoreSlim _wake = new(0, 1);
        private readonly Queue<Fragment> _pending = new();
        private readonly Task _worker;
        private Task? _dispose;
        private bool _closed, _consuming;
        private int _peak;
        private ulong _lastGeneration, _nextOffset, _quarantinedThrough, _finishedThrough, _nativeDiscarded, _dropped;
        private Exception? _failure;

        internal NativeSourceCapture(SupportLogStore store)
        {
            _store = store; _lease = store._owner.Enter();
            try
            {
                _lifetime = CancellationTokenSource.CreateLinkedTokenSource(store._owner.CancellationToken);
                try { _worker = Task.Run(ConsumeAsync); }
                catch { _lifetime.Dispose(); throw; }
            }
            catch { _wake.Dispose(); _lease.Dispose(); throw; }
        }
        internal CaptureState Snapshot
        { get { lock (_gate) return new(_pending.Count, _peak, _dropped, _quarantinedThrough, _consuming, _closed); } }
        internal void RequirePrivacy(SupportPrivacy privacy)
        {
            if (!ReferenceEquals(_store.Privacy, privacy)) throw new ArgumentException("Source output and source settings must share one privacy scope.");
        }

        internal bool Write(ulong generation, ulong offset, ReadOnlySpan<byte> bytes, bool gap)
        {
            if (generation == 0 || bytes.Length is <= 0 or > MaximumFragmentBytes || offset > ulong.MaxValue - (ulong)bytes.Length)
                throw new InvalidDataException("Invalid bounded source log fragment.");
            lock (_gate)
            {
                if (_closed) return false;
                if (generation < _lastGeneration || generation <= _finishedThrough)
                    throw new InvalidDataException("Source log generation is obsolete.");
                if (generation != _lastGeneration) { _lastGeneration = generation; _nextOffset = 0; }
                if (offset < _nextOffset || offset != _nextOffset && !gap)
                    throw new InvalidDataException("Source log offset is inconsistent.");
                _nextOffset = offset + (ulong)bytes.Length;
                if (gap) _quarantinedThrough = Math.Max(_quarantinedThrough, generation);
                if (_pending.Count == MaximumQueuedFragments)
                {
                    _quarantinedThrough = Math.Max(_quarantinedThrough, generation);
                    _dropped = AddSaturated(_dropped, (ulong)bytes.Length); Wake(); return false;
                }
                _pending.Enqueue(new(generation, bytes.ToArray())); _peak = Math.Max(_peak, _pending.Count); Wake(); return true;
            }
        }
        internal void ObserveNativeResponse(ulong generation, ulong discardedBytes, bool streamsDrained)
        {
            lock (_gate)
            {
                if (_closed) return;
                if (discardedBytes < _nativeDiscarded) throw new InvalidDataException("Source log discard counter moved backwards.");
                if (discardedBytes > _nativeDiscarded)
                {
                    // This also detects a final lost fragment with no later log frame.
                    _quarantinedThrough = Math.Max(_quarantinedThrough, generation);
                    _nativeDiscarded = discardedBytes;
                }
                if (streamsDrained && generation != 0) _finishedThrough = Math.Max(_finishedThrough, generation);
                Wake();
            }
        }
        private void Wake()
        { try { _wake.Release(); } catch (SemaphoreFullException) { } }
        private static ulong AddSaturated(ulong value, ulong amount) => value > ulong.MaxValue - amount ? ulong.MaxValue : value + amount;

        private async Task ConsumeAsync()
        {
            ProcessLines? lines = null; ulong generation = 0, announcedGap = 0;
            void QuarantineLines(ProcessLines? current, ulong version)
            {
                current?.Quarantine();
                if (version > announcedGap)
                { _store.Record("source-engine", "[source output omitted after capture gap]"); announcedGap = version; }
            }
            try
            {
                while (true)
                {
                    await _wake.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    while (true)
                    {
                        _lifetime.Token.ThrowIfCancellationRequested();
                        Fragment? fragment; ulong tainted, finished; bool closed;
                        lock (_gate)
                        { fragment = _pending.Count > 0 ? _pending.Dequeue() : null; _consuming = fragment is not null; tainted = _quarantinedThrough; finished = _finishedThrough; closed = _closed; }
                        if (fragment is null)
                        {
                            if (tainted > announcedGap) QuarantineLines(generation <= tainted ? lines : null, tainted);
                            if (lines is not null && (finished >= generation || closed))
                            {
                                if (tainted >= generation) QuarantineLines(lines, generation);
                                else if (finished < generation) lines.Quarantine();
                                lines.Complete(); lines.Dispose(); lines = null;
                            }
                            if (closed) return;
                            break;
                        }
                        try
                        {
                            if (fragment.Generation != generation)
                            {
                                if (lines is not null)
                                {
                                    if (tainted >= generation) QuarantineLines(lines, generation);
                                    else if (finished < generation) lines.Quarantine();
                                    lines.Complete(); lines.Dispose();
                                }
                                generation = fragment.Generation; lines = new(_store, "source-engine");
                            }
                            if (tainted >= generation)
                            {
                                QuarantineLines(lines, generation);
                            }
                            else lines!.Write(fragment.Bytes);
                        }
                        finally { CryptographicOperations.ZeroMemory(fragment.Bytes); lock (_gate) _consuming = false; }
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // A cancelled consumer cannot flush an incomplete secret/marker.
                lines?.Quarantine();
            }
            catch (Exception error)
            {
                _failure = error;
                try { _store.Record("source-engine", "[source output capture unavailable]"); } catch (Exception) { }
            }
            finally
            {
                lines?.Dispose();
                lock (_gate)
                {
                    _closed = true;
                    while (_pending.TryDequeue(out var fragment))
                    { _dropped = AddSaturated(_dropped, (ulong)fragment.Bytes.Length); CryptographicOperations.ZeroMemory(fragment.Bytes); }
                }
                _lease.Dispose();
            }
        }

        public ValueTask DisposeAsync()
        { lock (_gate) return new(_dispose ??= CloseAsync()); }
        private async Task CloseAsync()
        {
            // Native channel ownership must be joined before its caller closes
            // this ingress. Existing diagnostic-store cancellation also owns it.
            _closed = true; Wake();
            try
            {
                try { await _worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    lock (_gate) _quarantinedThrough = Math.Max(_quarantinedThrough, _lastGeneration);
                    await _lifetime.CancelAsync().ConfigureAwait(false);
                    await _worker.ConfigureAwait(false);
                }
            }
            finally { _lifetime.Dispose(); _wake.Dispose(); }
            if (_failure is not null) throw new IOException("Source output consumer failed after physical cleanup.", _failure);
        }
    }

    public ValueTask DisposeAsync() { lock (_stopGate) return new(_dispose ??= CloseAsync()); }
    private async Task CloseAsync()
    {
        try
        {
            await _owner.StopAsync(() =>
            {
                Stream[] streams; lock (_captureGate) streams = _captures.ToArray();
                List<Exception> errors = [];
                foreach (var stream in streams) try { stream.Dispose(); } catch (Exception error) { errors.Add(error); }
                if (errors.Count > 0) throw new AggregateException(errors);
            }).ConfigureAwait(false);
        }
        finally
        {
            try { await _io.RunAsync(() => { FlushCore(); return 0; }, CancellationToken.None).ConfigureAwait(false); }
            finally { await _owner.DisposeAsync().ConfigureAwait(false); }
        }
    }
    private void FlushCore()
    {
        if (_files is null) return;
        lock (_diskGate)
        {
            string[] lines; lock (_gate) { lines = _pending.ToArray(); _pending.Clear(); }
            if (lines.Length == 0) return;
            try
            {
                _files.Append(lines.Select(line =>
                {
                    var raw = Encoding.UTF8.GetBytes(line);
                    if (raw.Length <= _maximumBytes) return raw;
                    var row = JsonNode.Parse(line)!.AsObject(); row["message"] = "[oversize diagnostic omitted]";
                    return Encoding.UTF8.GetBytes(row.ToJsonString(JsonOptions) + "\n");
                }));
                _storageError = "";
            }
            catch (Exception error) when (StorageFailure(error)) { _storageError = error.GetType().Name; }
        }
    }
    private JsonObject SnapshotCore()
    {
        lock (_diskGate)
        {
            FlushCore(); var lines = new List<string>();
            if (_files is not null)
                try { foreach (var raw in _files.Read()) lines.AddRange(Encoding.UTF8.GetString(raw).Split('\n')); }
                catch (Exception error) when (StorageFailure(error)) { _storageError = error.GetType().Name; }
            long dropped; lock (_gate) { lines.AddRange(_memory); dropped = _dropped; }
            var rows = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                try
                {
                    if (JsonNode.Parse(line) is not JsonObject row || row["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id)
                        || !KnownPatterns.Hex12().IsMatch(id)) continue;
                    var selected = new JsonObject();
                    foreach (var key in new[] { "id", "time", "app_version", "instance_id", "source", "message" })
                        if (row[key] is JsonValue value && value.TryGetValue<string>(out var text)) selected[key] = text;
                    rows[id] = Privacy.Scrub(selected)!;
                }
                catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { }
            }
            var repair = Repair.Snapshot();
            return new() { ["entries"] = new JsonArray(rows.Values.ToArray()), ["storage_error"] = _storageError,
                ["source_repairs"] = Privacy.Scrub(repair.Repairs), ["repair_account"] = Privacy.Scrub(repair.Account),
                ["retention"] = new JsonObject { ["bounded"] = true, ["max_disk_bytes"] = (long)_maximumBytes * _fileCount, ["files"] = _fileCount,
                    ["dropped_records"] = dropped, ["memory_records"] = _pendingLimit, ["record_utf8_bytes"] = 4096, ["process_line_bytes"] = 65536,
                    ["persistent"] = _files is not null && _storageError.Length == 0 },
                ["scope"] = "Retained app errors/warnings and supervised process output. Older rotated logs and host/proxy/other-container logs are unavailable." };
        }
    }
    private static bool StorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    private CancellationTokenSource Lifetime(CancellationToken token) => CancellationTokenSource.CreateLinkedTokenSource(token, _owner.CancellationToken);
    private static string Utf8Prefix(string text, int maximum)
    {
        var result = new StringBuilder(); var bytes = 0;
        foreach (var rune in text.EnumerateRunes()) { if (bytes + rune.Utf8SequenceLength > maximum) break; bytes += rune.Utf8SequenceLength; result.Append(rune.ToString()); }
        return result.ToString();
    }
}
