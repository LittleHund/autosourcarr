using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Repair.Runner.Protocol;

namespace AutoSourcarr.Core.Repair.Runner.Guest;

public sealed record GuestProducedArtifact(string ArtifactId, GuestArtifactDescriptor Descriptor);
internal sealed record GuestPeerIdentity(string JobId, string Nonce)
{
    internal string Fingerprint => RawCodeRules.Digest(Encoding.ASCII.GetBytes(JobId + "\n" + Nonce));
    internal bool Matches(GuestArtifactBinding binding) => binding.JobId == JobId && binding.Session is { Length: 64 } && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(binding.Session), Encoding.ASCII.GetBytes(Nonce));
}

/// <summary>Fresh guest-private objects and one complete raw-tree transaction. There is no restore or arbitrary-path opener.</summary>
public sealed class GuestArtifactStore : IAsyncDisposable
{
    private sealed record Entry(string Id, GuestArtifactDescriptor Descriptor, bool Input, bool Complete, bool Used);
    private readonly GuestPeerIdentity _peer;
    private readonly string _root;
    private readonly Action<string>? _physical;
    private readonly BoundedWorkScheduler _io = new(1, 16);
    private readonly OperationOwner _owner = new();
    private readonly SemaphoreSlim _capacity = new(8, 8);
    private readonly GuestArtifactQuota _quota = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<GuestStorageLease, byte> _leases = new();
    private readonly ConcurrentQueue<Exception> _closeErrors = new();
    private readonly object _disposeGate = new();
    private Task? _disposing;
    private GuestPrivateFiles.DirectoryLease? _rootLease, _objectsLease, _treesLease;
    private int _unusable;
    private bool _created, _commitAttempted;
    private GuestProjectCommitReceipt? _tree;
    private GuestArtifactStore(string root, GuestPeerIdentity peer, Action<string>? physical) { _root = root; _peer = peer; _physical = physical; }
    public string JobId => _peer.JobId;
    public string SessionFingerprint => _peer.Fingerprint;
    internal string PrivatePath => _root; // Controlled filesystem tests only; no public path-opening API.
    internal static async Task<GuestArtifactStore> CreateAsync(string agentRoot, GuestPeerIdentity peer, Action<string>? physical, CancellationToken token)
    {
        GuestWire.Identity(peer.JobId, 32); GuestWire.Identity(peer.Nonce, 64);
        var parent = Path.GetFullPath(agentRoot);
        if (Path.GetFileName(parent) != "agent-jobs") throw new InvalidDataException("A fixed guest agent workspace is required");
        var store = new GuestArtifactStore(Path.Combine(parent, peer.JobId), peer, physical);
        try
        {
            await store._io.RunAsync(() =>
            {
                GuestPrivateFiles.PrivateRoot(parent); GuestPrivateFiles.NewDirectory(store._root); store._created = true; store._rootLease = GuestPrivateFiles.HoldDirectory(store._root);
                GuestPrivateFiles.NewDirectory(Path.Combine(store._root, "objects")); store._objectsLease = GuestPrivateFiles.HoldDirectory(Path.Combine(store._root, "objects"));
                GuestPrivateFiles.NewDirectory(Path.Combine(store._root, "trees")); store._treesLease = GuestPrivateFiles.HoldDirectory(Path.Combine(store._root, "trees"));
                GuestPrivateFiles.WriteNew(Path.Combine(store._root, "session.json"), CanonicalJson.Bytes(new JsonObject { ["schema"] = 1, ["job"] = peer.JobId, ["session"] = peer.Fingerprint, ["purpose"] = "artifact-identity-only" })); return 0;
            }, token).ConfigureAwait(false);
            return store;
        }
        catch (Exception original)
        {
            await store.PoisonAsync().ConfigureAwait(false);
            try { await store.DisposeAsync().ConfigureAwait(false); } catch (Exception cleanup) { throw new AggregateException("Guest private store creation and joined cleanup both failed", original, cleanup); }
            throw;
        }
    }
    internal async Task<GuestStorageLease> BeginReceiveAsync(GuestArtifactBinding binding, CancellationToken token)
    {
        Binding(binding, GuestArtifactDirection.IntoGuest); var admission = Enter(); FileStream? file = null;
        try
        {
            await _io.RunAsync(() =>
            {
                EnsureUsable(); if (_entries.ContainsKey(binding.ArtifactId)) throw new InvalidDataException("Guest artifact identity cannot be replayed");
                _quota.Reserve(binding.Descriptor, binding.Direction); Objects();
                file = GuestPrivateFiles.Open(ObjectPath(binding.ArtifactId, true), true, true, binding.Descriptor.Length);
                _entries.Add(binding.ArtifactId, new(binding.ArtifactId, binding.Descriptor, true, false, true)); return 0;
            }, token).ConfigureAwait(false);
            EnsureUsable(); var lease = Track(binding.ArtifactId, binding.Descriptor, file ?? throw new IOException("Guest input handle was not created"), true, admission); file = null;
            if (_owner.IsStopping) { await lease.DisposeAsync().ConfigureAwait(false); throw new OperationCanceledException(_owner.CancellationToken); }
            return lease;
        }
        catch
        {
            if (file is not null) await _io.RunAsync(() => { file.Dispose(); return 0; }).ConfigureAwait(false);
            try { await PoisonAsync().ConfigureAwait(false); } finally { admission.Dispose(); } throw;
        }
    }
    internal async Task<GuestStorageLease> OpenProducedAsync(GuestArtifactBinding binding, CancellationToken token)
    {
        Binding(binding, GuestArtifactDirection.FromGuest); var admission = Enter(); FileStream? file = null;
        try
        {
            await _io.RunAsync(() =>
            {
                EnsureUsable();
                if (!_entries.TryGetValue(binding.ArtifactId, out var entry) || entry.Input || !entry.Complete || entry.Used || entry.Descriptor != binding.Descriptor) throw new InvalidDataException("Guest output is absent, changed or already used");
                _entries[entry.Id] = entry with { Used = true }; Objects(); file = GuestPrivateFiles.Open(ObjectPath(entry.Id, false), false, false, entry.Descriptor.Length);
                GuestPrivateFiles.Verify(file, entry.Descriptor.Length, entry.Descriptor.Sha256); file.Position = 0; return 0;
            }, token).ConfigureAwait(false);
            EnsureUsable(); var lease = Track(binding.ArtifactId, binding.Descriptor, file ?? throw new IOException("Guest output handle was not created"), false, admission); file = null;
            if (_owner.IsStopping) { await lease.DisposeAsync().ConfigureAwait(false); throw new OperationCanceledException(_owner.CancellationToken); } return lease;
        }
        catch
        {
            if (file is not null) await _io.RunAsync(() => { file.Dispose(); return 0; }).ConfigureAwait(false);
            try { await PoisonAsync().ConfigureAwait(false); } finally { admission.Dispose(); } throw;
        }
    }
    public Task<GuestProducedArtifact> PublishOutputAsync(GuestArtifactDescriptor descriptor, ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        descriptor.Validate(GuestArtifactDirection.FromGuest);
        if (descriptor.Length != bytes.Length) throw new InvalidDataException("Guest output requires its exact bounded bytes");
        return PublishOutputAsync(descriptor, new MemoryStream(bytes.ToArray(), writable: false), token);
    }
    /// <summary>Owns the producer stream. Publication establishes byte transport only, never a built/valid/working artifact.</summary>
    public async Task<GuestProducedArtifact> PublishOutputAsync(GuestArtifactDescriptor descriptor, Stream ownedSource, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(ownedSource); IDisposable? admission = null;
        try
        {
            admission = Enter(); descriptor.Validate(GuestArtifactDirection.FromGuest);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _owner.CancellationToken);
            return await _io.RunAsync(() =>
            {
                EnsureUsable(); _quota.Reserve(descriptor, GuestArtifactDirection.FromGuest); Objects(); var id = NewId(); _physical?.Invoke("publish-read"); Objects();
                using (var output = GuestPrivateFiles.Open(ObjectPath(id, true), true, true, descriptor.Length))
                {
                    var buffer = new byte[32768]; long count = 0;
                    while (true)
                    {
                        var read = ownedSource.Read(buffer, 0, (int)Math.Min(buffer.Length, descriptor.Length + 1 - count));
                        if (read == 0) break; count += read; if (count > descriptor.Length) throw new InvalidDataException("Guest producer exceeded its exact length"); output.Write(buffer, 0, read);
                    }
                    GuestPrivateFiles.Verify(output, descriptor.Length, descriptor.Sha256); output.Flush(true);
                }
                linked.Token.ThrowIfCancellationRequested(); Publish(id, descriptor, input: false);
                _entries.Add(id, new(id, descriptor, false, true, false)); return new GuestProducedArtifact(id, descriptor);
            }, linked.Token).ConfigureAwait(false);
        }
        catch { await PoisonAsync().ConfigureAwait(false); throw; }
        finally
        {
            try
            {
                if (admission is not null) await _io.RunAsync(() => { ownedSource.Dispose(); return 0; }).ConfigureAwait(false); else await ownedSource.DisposeAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (_owner.IsStopping) throw new OperationCanceledException(_owner.CancellationToken);
            }
            catch { await PoisonAsync().ConfigureAwait(false); throw; }
            finally { admission?.Dispose(); }
        }
    }
    public async Task<GuestProjectCommitReceipt> CommitProjectAsync(GuestProjectCommitRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var admission = Enter();
            RawCodeRules.Hash(request.ProjectId); GuestWire.Identity(request.ManifestArtifactId, 32);
            if (request.Files is null || request.Files.Count is < 1 or > RawCodeRules.MaximumFiles) throw new InvalidDataException("Complete bounded project references are required");
            var count = request.Files.Count; var references = request.Files.Take(RawCodeRules.MaximumFiles + 1).ToArray();
            if (references.Length != count || references.Length > RawCodeRules.MaximumFiles) throw new InvalidDataException("Raw project references changed while being admitted");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _owner.CancellationToken);
            return await _io.RunAsync(() =>
            {
                EnsureUsable(); if (_commitAttempted) throw new InvalidDataException("A guest project transaction cannot be reused or resumed"); _commitAttempted = true;
                if (!_entries.TryGetValue(request.ManifestArtifactId, out var manifest) || !manifest.Input || !manifest.Complete || manifest.Descriptor.Role != GuestArtifactRole.RawProjectManifest || manifest.Descriptor.ProjectId != request.ProjectId)
                    throw new InvalidDataException("The exact raw manifest was not admitted");
                Objects(); var bytes = GuestPrivateFiles.Read(ObjectPath(manifest.Id, false), manifest.Descriptor.Length, manifest.Descriptor.Sha256); var project = GuestRawProject.Parse(bytes, request.ProjectId);
                var actual = _entries.Values.Where(entry => entry.Input && entry.Descriptor.Role == GuestArtifactRole.RawProjectFile).ToArray();
                if (actual.Length != project.Files.Count || references.Length != project.Files.Count || actual.Any(entry => !entry.Complete || entry.Descriptor.ProjectId != project.Id)) throw new InvalidDataException("Raw tree has extra, missing or incomplete objects");
                var ids = new HashSet<string>(StringComparer.Ordinal); var names = new HashSet<string>(StringComparer.Ordinal); var selected = new List<(Entry Entry, GuestRawFile File)>();
                foreach (var reference in references)
                {
                    if (reference is null) throw new InvalidDataException("Missing raw file reference"); GuestWire.Identity(reference.ArtifactId, 32);
                    if (reference.LogicalName is null || RawCodeRules.RelativeName(reference.LogicalName) != reference.LogicalName) throw new InvalidDataException("Noncanonical raw file reference");
                    if (!ids.Add(reference.ArtifactId) || !names.Add(reference.LogicalName) || !project.Files.TryGetValue(reference.LogicalName, out var expected) ||
                        !_entries.TryGetValue(reference.ArtifactId, out var entry) || !entry.Input || !entry.Complete || entry.Descriptor.Role != GuestArtifactRole.RawProjectFile || entry.Descriptor.ProjectId != project.Id ||
                        entry.Descriptor.LogicalName != reference.LogicalName || entry.Descriptor.Length != expected.Length || entry.Descriptor.Sha256 != expected.Sha256 || reference.Executable != expected.Executable)
                        throw new InvalidDataException("Raw file name, hash, length, executable bit or identity changed");
                    selected.Add((entry, expected));
                }
                if (actual.Any(entry => !ids.Contains(entry.Id))) throw new InvalidDataException("Raw tree contains an undeclared object");
                var id = NewId(); var parent = Path.Combine(_root, "trees"); Trees(); var staging = Path.Combine(parent, id + ".partial"); GuestPrivateFiles.NewDirectory(staging); using var slot = GuestPrivateFiles.HoldDirectory(staging, allowRename: true);
                var tree = Path.Combine(staging, "files"); GuestPrivateFiles.NewDirectory(tree);
                foreach (var (entry, expected) in selected)
                {
                    linked.Token.ThrowIfCancellationRequested(); var name = entry.Descriptor.LogicalName!; var destination = TreeFile(tree, name, createParents: true);
                    Objects(); var data = GuestPrivateFiles.Read(ObjectPath(entry.Id, false), expected.Length, expected.Sha256); GuestPrivateFiles.WriteNew(destination, data); GuestPrivateFiles.Freeze(destination, expected.Executable);
                }
                _physical?.Invoke("commit-tree"); linked.Token.ThrowIfCancellationRequested(); Trees(); slot.AssertAt(staging); VerifyTree(tree, project); GuestPrivateFiles.WriteNew(Path.Combine(staging, "manifest.json"), bytes);
                var receipt = new GuestProjectCommitReceipt(id, project.Id, manifest.Descriptor.Sha256, project.Files.Count, project.Files.Values.Sum(file => file.Length));
                GuestPrivateFiles.WriteNew(Path.Combine(staging, "receipt.json"), CanonicalJson.Bytes(new JsonObject { ["schema"] = 1, ["tree"] = id, ["project"] = project.Id, ["manifest_sha256"] = manifest.Descriptor.Sha256, ["files"] = receipt.Files, ["bytes"] = receipt.Bytes, ["purpose"] = "complete-raw-tree-only" }));
                linked.Token.ThrowIfCancellationRequested(); EnsureUsable(); Trees(); slot.AssertAt(staging); Directory.Move(staging, Path.Combine(parent, id)); GuestPrivateFiles.Sync(parent); slot.AssertAt(Path.Combine(parent, id)); VerifyTree(Path.Combine(parent, id, "files"), project); _tree = receipt; return receipt;
            }, linked.Token).ConfigureAwait(false);
        }
        catch { await PoisonAsync().ConfigureAwait(false); throw; }
    }
    public GuestProjectCommitReceipt CommittedProject
    { get { EnsureUsable(); return _tree ?? throw new InvalidOperationException("No complete raw project transaction exists"); } }
    private static string TreeFile(string tree, string name, bool createParents)
    {
        if (RawCodeRules.RelativeName(name) != name) throw new InvalidDataException("Noncanonical raw tree name"); var parts = name.Split('/'); var current = tree;
        foreach (var part in parts[..^1])
        {
            current = Path.Combine(current, part);
            if (createParents && !Directory.Exists(current)) GuestPrivateFiles.NewDirectory(current); else GuestPrivateFiles.PrivateRoot(current);
        }
        var path = Path.GetFullPath(Path.Combine(current, parts[^1]));
        if (!path.StartsWith(Path.GetFullPath(tree) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException("Raw tree escaped its opaque slot"); return path;
    }
    private static void VerifyTree(string tree, GuestRawProject project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal); var expectedDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in project.Files.Keys)
        {
            var prefix = ""; foreach (var part in name.Split('/')[..^1]) { prefix = prefix.Length == 0 ? part : prefix + "/" + part; expectedDirectories.Add(prefix); }
        }
        var directories = new Stack<string>(); directories.Push(tree);
        while (directories.TryPop(out var directory))
        {
            GuestPrivateFiles.PrivateRoot(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path); if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Committed tree contains a link");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!expectedDirectories.Contains(Path.GetRelativePath(tree, path).Replace(Path.DirectorySeparatorChar, '/'))) throw new InvalidDataException("Committed raw tree has an extra directory");
                    directories.Push(path); continue;
                }
                var name = Path.GetRelativePath(tree, path).Replace(Path.DirectorySeparatorChar, '/');
                if (!seen.Add(name) || !project.Files.TryGetValue(name, out var expected)) throw new InvalidDataException("Committed raw tree has extra files");
                using var input = GuestPrivateFiles.Open(path, false, false, expected.Length); GuestPrivateFiles.Verify(input, expected.Length, expected.Sha256); GuestPrivateFiles.CheckMode(path, expected.Executable);
            }
        }
        if (seen.Count != project.Files.Count) throw new InvalidDataException("Committed raw tree has missing files");
    }
    internal async Task CompleteReceiveAsync(string id, GuestArtifactDescriptor descriptor, FileStream file, CancellationToken token)
    {
        await _io.RunAsync(() =>
        {
            EnsureUsable(); Objects(); _physical?.Invoke("receive-commit"); token.ThrowIfCancellationRequested(); GuestPrivateFiles.Verify(file, descriptor.Length, descriptor.Sha256); file.Flush(true); file.Dispose(); token.ThrowIfCancellationRequested();
            Publish(id, descriptor, input: true); _entries[id] = _entries[id] with { Complete = true }; return 0;
        }, token).ConfigureAwait(false);
    }
    private void Publish(string id, GuestArtifactDescriptor descriptor, bool input)
    {
        Objects(); File.Move(ObjectPath(id, true), ObjectPath(id, false), overwrite: false); GuestPrivateFiles.Sync(Path.Combine(_root, "objects"));
        using (var file = GuestPrivateFiles.Open(ObjectPath(id, false), false, false, descriptor.Length)) GuestPrivateFiles.Verify(file, descriptor.Length, descriptor.Sha256);
        GuestPrivateFiles.WriteNew(Path.Combine(_root, "objects", id + ".json"), CanonicalJson.Bytes(new JsonObject { ["schema"] = 1, ["artifact"] = id, ["role"] = descriptor.Role.ToString(), ["length"] = descriptor.Length, ["sha256"] = descriptor.Sha256, ["logical_name"] = descriptor.LogicalName, ["project"] = descriptor.ProjectId, ["direction"] = input ? "into-guest" : "from-guest", ["purpose"] = "byte-receipt-only" }));
    }
    private GuestStorageLease Track(string id, GuestArtifactDescriptor descriptor, FileStream file, bool receiving, IDisposable admission)
    { var lease = new GuestStorageLease(this, id, descriptor, file, receiving, admission); _leases.TryAdd(lease, 0); return lease; }
    private void Binding(GuestArtifactBinding binding, GuestArtifactDirection direction)
    {
        if (!_peer.Matches(binding) || binding.Direction != direction) throw new InvalidDataException("Guest artifact job/session/direction changed"); GuestWire.Identity(binding.ArtifactId, 32); binding.Descriptor.Validate(direction);
    }
    private IDisposable Enter()
    {
        EnsureUsable(); if (!_capacity.Wait(0)) throw new WorkDeferredException();
        try { return new Admission(_owner.Enter(), _capacity); } catch { _capacity.Release(); throw; }
    }
    private sealed class Admission(IDisposable owned, SemaphoreSlim capacity) : IDisposable
    { private int _closed; public void Dispose() { if (Interlocked.Exchange(ref _closed, 1) == 0) { capacity.Release(); owned.Dispose(); } } }
    private string ObjectPath(string id, bool partial) => Path.Combine(_root, "objects", id + (partial ? ".partial" : ".bin"));
    private void Root() => (_rootLease ?? throw new InvalidDataException("Guest private root was not leased")).AssertAt(_root);
    private void Objects() { Root(); (_objectsLease ?? throw new InvalidDataException("Guest object slot was not leased")).AssertAt(Path.Combine(_root, "objects")); }
    private void Trees() { Root(); (_treesLease ?? throw new InvalidDataException("Guest tree slot was not leased")).AssertAt(Path.Combine(_root, "trees")); }
    private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    internal void EnsureUsable() { if (Volatile.Read(ref _unusable) != 0 || _owner.IsStopping) throw new InvalidDataException("Guest artifacts are closed or uncertain and cannot be reused"); }
    internal Task<T> IOAsync<T>(Func<T> action, CancellationToken token) => _io.RunAsync(action, token);
    internal void Physical(string stage) => _physical?.Invoke(stage);
    internal void Untrack(GuestStorageLease lease) => _leases.TryRemove(lease, out _);
    internal void CloseFailed(Exception error) => _closeErrors.Enqueue(error);
    internal async Task PoisonAsync()
    {
        if (Interlocked.Exchange(ref _unusable, 1) != 0 || !_created) return;
        try { await _io.RunAsync(() => { Root(); GuestPrivateFiles.WriteNew(Path.Combine(_root, "unusable"), "unusable; never resume"u8); return 0; }).ConfigureAwait(false); } catch { /* Physical uncertainty never restores admission. */ }
    }
    public async Task StopAsync()
    {
        await _owner.StopAsync(() => { foreach (var lease in _leases.Keys) _ = ObserveCloseAsync(lease); }).ConfigureAwait(false);
        if (!_closeErrors.IsEmpty) throw new AggregateException("Guest file close failed after its physical worker joined", _closeErrors);
    }
    private static async Task ObserveCloseAsync(GuestStorageLease lease) { try { await lease.DisposeAsync().ConfigureAwait(false); } catch { } }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_disposeGate) { if (_disposing is not null) return new(_disposing); completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposing = completion.Task; }
        _ = DisposeCoreAsync(completion); return new(completion.Task);
    }
    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        var errors = new List<Exception>();
        try { await StopAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try
        {
            if (_created && Volatile.Read(ref _unusable) == 0) await _io.RunAsync(() => { Root(); GuestPrivateFiles.WriteNew(Path.Combine(_root, "closed"), "closed; never resume"u8); return 0; }).ConfigureAwait(false);
        }
        catch (Exception error) { errors.Add(error); }
        try
        {
            await _io.RunAsync(() =>
            {
                var closeErrors = new List<Exception>();
                foreach (var lease in new[] { _treesLease, _objectsLease, _rootLease }) try { lease?.Dispose(); } catch (Exception error) { closeErrors.Add(error); }
                if (closeErrors.Count != 0) throw new AggregateException("Guest directory handles failed to close after file work joined", closeErrors); return 0;
            }).ConfigureAwait(false);
        }
        catch (Exception error) { errors.Add(error); }
        try { await _io.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { await _owner.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { _capacity.Dispose(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count == 0) completion.TrySetResult(); else completion.TrySetException(new AggregateException("Guest store cleanup retained failures after physical ownership joined", errors));
    }
}
