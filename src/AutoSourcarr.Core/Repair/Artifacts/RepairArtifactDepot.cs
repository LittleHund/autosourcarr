using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Repair.Runner.Protocol;

[assembly: InternalsVisibleTo("AutoSourcarr.Tests")]

namespace AutoSourcarr.Core.Repair.Artifacts;

public sealed record RepairArtifactAdmission(string ObjectId, GuestArtifactDescriptor Descriptor);
public sealed record RepairArtifactDepotSnapshot(string JobId, string SessionFingerprint, string Status, IReadOnlyList<RepairArtifactAdmission> Inputs, IReadOnlyList<RepairArtifactAdmission> Outputs, int Quarantined);

/// <summary>Fresh per-job private storage. No arbitrary-path opener, resume, overwrite or source-valid/working receipt.</summary>
public sealed class RepairArtifactDepot : IGuestArtifactLeaseProvider, IAsyncDisposable
{
    private const int MaximumMetadata = 8 * 1024 * 1024;
    private const long MaximumBytes = RawCodeRules.MaximumExpanded + RawCodeRules.MaximumManifest + 2L * SourcePackage.MaximumBytes;
    private readonly GuestArtifactSession _context;
    private readonly string _root;
    private readonly Action<string>? _physical;
    private readonly BoundedWorkScheduler _io = new(1, 16);
    private readonly OperationOwner _owner = new();
    private readonly SemaphoreSlim _capacity = new(8, 8);
    private readonly ConcurrentDictionary<RepairArtifactStream, byte> _streams = new();
    private readonly ConcurrentQueue<Exception> _closeErrors = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _transfers = new(StringComparer.Ordinal);
    private PrivateDocumentStore? _documents;
    private long _revision;
    private int _unusable;
    private readonly object _disposeGate = new();
    private Task? _disposing;
    private sealed record Entry(string Id, GuestArtifactDescriptor Descriptor, bool Input, string State);
    private RepairArtifactDepot(string root, GuestArtifactSession context, Action<string>? physical) { _root = root; _context = context; _physical = physical; }
    public static Task<RepairArtifactDepot> CreateAsync(string maintenanceRoot, GuestArtifactSession context, CancellationToken cancellationToken = default) => CreateAsync(maintenanceRoot, context, null, cancellationToken);
    internal static async Task<RepairArtifactDepot> CreateAsync(string maintenanceRoot, GuestArtifactSession context, Action<string>? physical, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); var root = FixedRoot(maintenanceRoot, context.JobId);
        var depot = new RepairArtifactDepot(root, context, physical);
        try
        {
            await depot._io.RunAsync(() =>
            {
                CheckMaintenance(root);
                if (Directory.Exists(root) || File.Exists(root)) throw new InvalidDataException("Artifact depots are fresh job scopes and cannot be reopened for transfers");
                PrivateDocumentStore.ValidateDirectory(root);
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var claim = new FileStream(Path.Combine(root, "claim"), options)) { claim.Write(Encoding.ASCII.GetBytes(context.Fingerprint)); claim.Flush(true); }
                PrivateDocumentStore.ValidateDirectory(Path.Combine(root, "objects"));
                depot._documents = new PrivateDocumentStore(root); depot.Save("active", fresh: true); return 0;
            }, cancellationToken).ConfigureAwait(false);
            return depot;
        }
        catch { await depot.PoisonAsync().ConfigureAwait(false); try { await depot.DisposeAsync().ConfigureAwait(false); } catch { } throw; }
    }
    public Task<RepairArtifactAdmission> AdmitInputAsync(GuestArtifactDescriptor descriptor, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        descriptor.Validate(GuestArtifactDirection.IntoGuest);
        if (bytes.Length != descriptor.Length) throw new InvalidDataException("Artifact admission requires its exact bounded length");
        return AdmitInputAsync(descriptor, new MemoryStream(bytes.ToArray(), writable: false), cancellationToken);
    }
    public async Task<RepairArtifactAdmission> AdmitInputAsync(GuestArtifactDescriptor descriptor, Stream ownedInput, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownedInput); IDisposable? admission = null;
        try
        {
            admission = Enter(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken);
            descriptor.Validate(GuestArtifactDirection.IntoGuest);
            return await _io.RunAsync(() =>
            {
                EnsureUsable(); CheckQuota(descriptor, input: true); _physical?.Invoke("admit-read");
                if (!ownedInput.CanRead) throw new InvalidDataException("Artifact admission requires an owned readable stream");
                using var output = new MemoryStream(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[32768];
                while (true)
                {
                    var remaining = descriptor.Length + 1 - output.Length; var count = ownedInput.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (count == 0) break; output.Write(buffer, 0, count); hash.AppendData(buffer, 0, count);
                    if (output.Length > descriptor.Length) throw new InvalidDataException("Artifact admission exceeds its exact length");
                }
                if (output.Length != descriptor.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != descriptor.Sha256) throw new InvalidDataException("Artifact admission does not match its exact bytes");
                var entry = new Entry(NewId(), descriptor, true, "admitted");
                Objects();
                AtomicSourceFiles.Write(ObjectPath(entry.Id, partial: false), output.ToArray(), checked((int)descriptor.Length), immutable: true);
                using (var admitted = PrivateDocumentStore.OpenRegular(ObjectPath(entry.Id, false), false, false, checked((int)descriptor.Length))) Verify(admitted, descriptor);
                _entries.Add(entry.Id, entry); Save("active"); return new RepairArtifactAdmission(entry.Id, descriptor);
            }, linked.Token).ConfigureAwait(false);
        }
        catch { await PoisonAsync().ConfigureAwait(false); throw; }
        finally
        {
            try
            {
                if (admission is not null) await _io.RunAsync(() => { ownedInput.Dispose(); return 0; }).ConfigureAwait(false);
                else await ownedInput.DisposeAsync().ConfigureAwait(false);
            }
            catch { await PoisonAsync().ConfigureAwait(false); throw; }
            finally { admission?.Dispose(); }
        }
    }
    public async Task<GuestArtifactReadLease> OpenInputAsync(GuestArtifactBinding binding, CancellationToken cancellationToken)
    {
        CheckBinding(binding, GuestArtifactDirection.IntoGuest); var admission = Enter(); FileStream? file = null;
        try
        {
            await _io.RunAsync(() =>
            {
                EnsureUsable(); ReserveTransfer(binding); var entry = _entries.Values.SingleOrDefault(entry => entry.Input && entry.Descriptor == binding.Descriptor && entry.State == "admitted") ?? throw new InvalidDataException("The exact private input was not admitted or was already leased");
                Objects();
                file = PrivateDocumentStore.OpenRegular(ObjectPath(entry.Id, false), false, false, checked((int)entry.Descriptor.Length)); Verify(file, entry.Descriptor); file.Position = 0;
                _entries[entry.Id] = entry with { State = "leased" }; Save("active"); return 0;
            }, cancellationToken).ConfigureAwait(false);
            EnsureUsable(); var stream = Track(file ?? throw new IOException("Input handle was not created"), binding.Descriptor.Length, false, admission); file = null;
            if (_owner.IsStopping) { await stream.DisposeAsync().ConfigureAwait(false); throw new OperationCanceledException(_owner.CancellationToken); }
            return new(binding, stream);
        }
        catch
        {
            if (file is not null) await _io.RunAsync(() => { file.Dispose(); return 0; }).ConfigureAwait(false);
            try { await PoisonAsync().ConfigureAwait(false); } finally { admission.Dispose(); } throw;
        }
    }
    public async Task<GuestArtifactWriteLease> CreateOutputAsync(GuestArtifactBinding binding, CancellationToken cancellationToken)
    {
        CheckBinding(binding, GuestArtifactDirection.FromGuest); var admission = Enter(); FileStream? file = null; Entry? entry = null;
        try
        {
            await _io.RunAsync(() =>
            {
                EnsureUsable(); ReserveTransfer(binding); CheckQuota(binding.Descriptor, input: false); entry = new(NewId(), binding.Descriptor, false, "receiving");
                Objects();
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (new FileStream(ObjectPath(entry.Id, true), options)) { }
                file = PrivateDocumentStore.OpenRegular(ObjectPath(entry.Id, true), true, false, checked((int)binding.Descriptor.Length));
                _entries.Add(entry.Id, entry); Save("active"); return 0;
            }, cancellationToken).ConfigureAwait(false);
            EnsureUsable(); var stream = Track(file ?? throw new IOException("Output handle was not created"), binding.Descriptor.Length, true, admission); file = null;
            if (_owner.IsStopping) { await stream.DisposeAsync().ConfigureAwait(false); throw new OperationCanceledException(_owner.CancellationToken); }
            return new OutputLease(this, binding, stream, entry?.Id ?? throw new IOException("Output identity was not created"));
        }
        catch
        {
            if (file is not null) await _io.RunAsync(() => { file.Dispose(); return 0; }).ConfigureAwait(false);
            try { await PoisonAsync().ConfigureAwait(false); } finally { admission.Dispose(); } throw;
        }
    }
    private sealed class OutputLease(RepairArtifactDepot owner, GuestArtifactBinding binding, RepairArtifactStream stream, string id) : GuestArtifactWriteLease(binding, stream)
    {
        private bool _committed;
        public override async Task CommitAsync(GuestArtifactReceipt receipt, CancellationToken token)
        {
            if (_committed || receipt.Binding != Binding || receipt.Chunks != (Binding.Descriptor.Length + 32767) / 32768) throw new InvalidDataException("Output receipt does not match its private slot");
            try
            {
                await stream.WithFileAsync(file =>
                {
                    owner.EnsureUsable(); owner.Objects(); owner._physical?.Invoke("commit"); token.ThrowIfCancellationRequested(); Verify(file, Binding.Descriptor); file.Flush(true);
                    token.ThrowIfCancellationRequested(); owner.Objects(); File.Move(owner.ObjectPath(id, true), owner.ObjectPath(id, false), overwrite: false); DurableConfigurationFiles.SyncDirectory(Path.Combine(owner._root, "objects"));
                    using (var published = PrivateDocumentStore.OpenRegular(owner.ObjectPath(id, false), false, false, checked((int)Binding.Descriptor.Length))) Verify(published, Binding.Descriptor);
                    owner._entries[id] = owner._entries[id] with { State = "received" }; owner.Save("active"); return 0;
                }, token).ConfigureAwait(false);
                _committed = true;
            }
            catch { await owner.PoisonAsync().ConfigureAwait(false); throw; }
        }
        public override async ValueTask DisposeAsync()
        { try { if (!_committed) await owner.PoisonAsync().ConfigureAwait(false); } finally { await stream.DisposeAsync().ConfigureAwait(false); } }
    }
    private RepairArtifactStream Track(FileStream file, long length, bool writable, IDisposable admission)
    {
        var stream = new RepairArtifactStream(this, file, length, writable, admission); _streams.TryAdd(stream, 0);
        return stream;
    }
    private IDisposable Enter()
    {
        EnsureUsable(); if (!_capacity.Wait(0)) throw new WorkDeferredException();
        try { return new Admission(_owner.Enter(), _capacity); } catch { _capacity.Release(); throw; }
    }
    private sealed class Admission(IDisposable owned, SemaphoreSlim capacity) : IDisposable
    { private int _closed; public void Dispose() { if (Interlocked.Exchange(ref _closed, 1) == 0) { capacity.Release(); owned.Dispose(); } } }
    internal async Task<T> IOAsync<T>(Func<T> action, CancellationToken token) => await _io.RunAsync(action, token).ConfigureAwait(false);
    internal void Physical(string stage) => _physical?.Invoke(stage);
    internal void Untrack(RepairArtifactStream stream) => _streams.TryRemove(stream, out _);
    internal void CloseFailed(Exception error) => _closeErrors.Enqueue(error);
    internal void EnsureUsable()
    { if (Volatile.Read(ref _unusable) != 0 || _owner.IsStopping) throw new InvalidDataException("The private artifact session is closed or uncertain and cannot be reused"); }
    internal async Task PoisonAsync()
    {
        if (Interlocked.Exchange(ref _unusable, 1) != 0 || _documents is null) return;
        try { await _io.RunAsync(() => { Save("unusable"); return 0; }).ConfigureAwait(false); }
        catch { /* Keep uncertain files quarantined; never reconstruct or reopen a usable state. */ }
    }
    private void CheckBinding(GuestArtifactBinding binding, GuestArtifactDirection direction)
    {
        if (!_context.Matches(binding) || binding.Direction != direction || binding.ArtifactId.Length != 32 || binding.ArtifactId.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new InvalidDataException("Artifact lease belongs to another private job/session or role");
        binding.Descriptor.Validate(direction);
    }
    private void ReserveTransfer(GuestArtifactBinding binding)
    { if (!_transfers.Add(binding.ArtifactId)) throw new InvalidDataException("An artifact transfer identity cannot be resumed or replayed in another slot"); }
    private void CheckQuota(GuestArtifactDescriptor descriptor, bool input)
    {
        CheckQuota(_entries.Values.Where(entry => entry.Input == input).ToArray(), descriptor);
    }
    private static void CheckQuota(Entry[] rows, GuestArtifactDescriptor descriptor)
    {
        var raw = descriptor.Role is GuestArtifactRole.RawProjectFile or GuestArtifactRole.CandidateProjectFile;
        if (rows.Sum(entry => entry.Descriptor.Length) + descriptor.Length > MaximumBytes || rows.Length >= RawCodeRules.MaximumFiles + 3) throw new InvalidDataException("Artifact depot quota exceeded");
        if (rows.Any(entry => entry.Descriptor.Role == descriptor.Role && (!raw || entry.Descriptor.LogicalName == descriptor.LogicalName))) throw new InvalidDataException("Artifact input/output roles and raw filenames cannot be overwritten");
        var projectRows = rows.Where(entry => entry.Descriptor.ProjectId is not null).ToArray();
        if (descriptor.ProjectId is not null && projectRows.Any(entry => entry.Descriptor.ProjectId != descriptor.ProjectId) || raw && (rows.Count(entry => entry.Descriptor.LogicalName is not null) >= RawCodeRules.MaximumFiles || rows.Where(entry => entry.Descriptor.LogicalName is not null).Sum(entry => entry.Descriptor.Length) + descriptor.Length > RawCodeRules.MaximumExpanded)) throw new InvalidDataException("Artifact project quota or pinned identity changed");
    }
    private static void Verify(FileStream file, GuestArtifactDescriptor descriptor)
    {
        file.Position = 0;
        if (file.Length != descriptor.Length) throw new InvalidDataException("Private artifact bytes failed their exact length");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[32768]; var remaining = descriptor.Length;
        while (remaining != 0)
        {
            var count = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0) throw new InvalidDataException("Private artifact bytes ended before their exact length");
            remaining -= count; hash.AppendData(buffer, 0, count);
        }
        if (file.ReadByte() != -1 || file.Length != descriptor.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != descriptor.Sha256) throw new InvalidDataException("Private artifact bytes failed their exact identity");
    }
    private string ObjectPath(string id, bool partial) => Path.Combine(_root, "objects", id + (partial ? ".partial" : ".bin"));
    private void Objects()
    {
        var directory = Path.Combine(_root, "objects"); if (!Directory.Exists(_root) || !Directory.Exists(directory)) throw new FileNotFoundException("Private artifact storage disappeared");
        PrivateDocumentStore.ValidateDirectory(directory);
    }
    private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private void Save(string status, bool fresh = false)
    {
        var documents = _documents ?? throw new InvalidOperationException("Artifact metadata was not initialized");
        documents.Locked("depot.json", document =>
        {
            var previous = document.Read(MaximumMetadata);
            if (fresh ? previous is not null : previous is null) throw new InvalidDataException("Artifact depot metadata cannot be replaced or reset");
            if (!fresh)
            {
                var old = JsonNode.Parse(previous!)!.AsObject();
                if (old["revision"]?.GetValue<long>() != _revision || old["job"]?.GetValue<string>() != _context.JobId || old["session"]?.GetValue<string>() != _context.Fingerprint) throw new InvalidDataException("Artifact depot metadata changed");
            }
            var value = new JsonObject { ["schema"] = 1, ["job"] = _context.JobId, ["session"] = _context.Fingerprint, ["status"] = status, ["revision"] = ++_revision,
                ["entries"] = new JsonArray(_entries.Values.Select(entry => (JsonNode?)JsonSerializer.SerializeToNode(entry)).ToArray()) };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value); if (bytes.Length > MaximumMetadata) throw new InvalidDataException("Artifact receipts exceed their bound"); document.Write(bytes); return 0;
        });
    }
    private static string FixedRoot(string maintenanceRoot, string jobId)
    {
        var root = Path.GetFullPath(maintenanceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetFileName(root) != "source-maintenance" || jobId.Length != 32 || jobId.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new InvalidDataException("An existing configured maintenance workspace and exact job identity are required");
        return Path.Combine(root, "artifact-jobs", jobId);
    }
    private static void CheckMaintenance(string depotRoot)
    {
        var maintenance = Path.GetDirectoryName(Path.GetDirectoryName(depotRoot)!)!;
        if (!Directory.Exists(maintenance)) throw new InvalidDataException("The configured instance maintenance workspace must already exist");
        PrivateDocumentStore.ValidateDirectory(maintenance);
    }
    public static async Task<RepairArtifactDepotSnapshot> InspectAsync(string maintenanceRoot, GuestArtifactSession context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); var root = FixedRoot(maintenanceRoot, context.JobId);
        await using var io = new BoundedWorkScheduler(1, 0);
        return await io.RunAsync(() =>
        {
            CheckMaintenance(root); if (!Directory.Exists(root)) throw new FileNotFoundException("Artifact depot is absent");
            var bytes = AtomicSourceFiles.Read(Path.Combine(root, "depot.json"), MaximumMetadata) ?? throw new FileNotFoundException("Artifact receipts are absent");
            var value = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 16 })!.AsObject();
            if (value.Count != 6 || !new[] { "schema", "job", "session", "status", "revision", "entries" }.All(value.ContainsKey) || value["schema"]?.GetValue<int>() != 1 || value["revision"]?.GetValue<long>() is not > 0 || value["job"]?.GetValue<string>() != context.JobId || value["session"]?.GetValue<string>() != context.Fingerprint || value["entries"] is not JsonArray entries || entries.Count > 2 * (RawCodeRules.MaximumFiles + 3)) throw new InvalidDataException("Artifact receipt identity is invalid");
            var status = value["status"]!.GetValue<string>(); if (status is not ("active" or "closed" or "unusable")) throw new InvalidDataException("Unknown artifact depot state");
            var input = new List<RepairArtifactAdmission>(); var output = new List<RepairArtifactAdmission>(); var quarantined = 0; var seen = new HashSet<string>(StringComparer.Ordinal); var admitted = new List<Entry>();
            foreach (var node in entries)
            {
                if (node is not JsonObject record || record.Count != 4 || !new[] { "Id", "Descriptor", "Input", "State" }.All(record.ContainsKey) || record["Descriptor"] is not JsonObject definition || definition.Count != 5 || !new[] { "Role", "Length", "Sha256", "LogicalName", "ProjectId" }.All(definition.ContainsKey)) throw new InvalidDataException("Ambiguous artifact receipt");
                var entry = node!.Deserialize<Entry>() ?? throw new InvalidDataException("Invalid artifact receipt");
                if (entry.Id is null || entry.Id.Length != 32 || entry.Id.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || !seen.Add(entry.Id) ||
                    (entry.Input ? entry.State is not ("admitted" or "leased") : entry.State is not ("receiving" or "received"))) throw new InvalidDataException("Invalid artifact object identity/state");
                entry.Descriptor.Validate(entry.Input ? GuestArtifactDirection.IntoGuest : GuestArtifactDirection.FromGuest);
                CheckQuota(admitted.Where(row => row.Input == entry.Input).ToArray(), entry.Descriptor); admitted.Add(entry);
                if (!entry.Input && (entry.State != "received" || status == "unusable")) { quarantined++; continue; }
                var directory = Path.Combine(root, "objects"); if (!Directory.Exists(directory)) throw new FileNotFoundException("Artifact objects are absent"); PrivateDocumentStore.ValidateDirectory(directory);
                using var file = PrivateDocumentStore.OpenRegular(Path.Combine(directory, entry.Id + ".bin"), false, false, checked((int)entry.Descriptor.Length)); Verify(file, entry.Descriptor);
                (entry.Input ? input : output).Add(new(entry.Id, entry.Descriptor));
            }
            return new RepairArtifactDepotSnapshot(context.JobId, context.Fingerprint, status, input, output, quarantined);
        }, cancellationToken).ConfigureAwait(false);
    }
    public async Task StopAsync()
    {
        await _owner.StopAsync(() => { foreach (var stream in _streams.Keys) _ = ObserveCloseAsync(stream); }).ConfigureAwait(false);
        if (!_closeErrors.IsEmpty) throw new AggregateException("Artifact physical close failed after joining its worker", _closeErrors);
    }
    private static async Task ObserveCloseAsync(RepairArtifactStream stream)
    { try { await stream.DisposeAsync().ConfigureAwait(false); } catch { /* The stream records the fault before releasing physical ownership. */ } }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_disposeGate) { if (_disposing is not null) return new(_disposing); completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposing = completion.Task; }
        _ = DisposeCoreAsync(completion); return new(completion.Task);
    }
    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            Exception? stopped = null;
            try { await StopAsync().ConfigureAwait(false); } catch (Exception error) { stopped = error; }
            try
            {
                if (_documents is not null) await _io.RunAsync(() => { Save(Volatile.Read(ref _unusable) != 0 ? "unusable" : "closed"); return 0; }).ConfigureAwait(false);
                if (stopped is not null) throw stopped;
            }
            finally { await _io.DisposeAsync().ConfigureAwait(false); await _owner.DisposeAsync().ConfigureAwait(false); _capacity.Dispose(); }
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
