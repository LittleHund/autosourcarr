using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Repair.Runner.Guest;
using AutoSourcarr.Core.Repair.Runner.Protocol;

namespace AutoSourcarr.Tests;

public static class GuestServerTests
{
    private static readonly string Job = new('a', 32), Nonce = new('b', 64), Image = new('c', 64);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Id() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private static T Await<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
    private static void Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string AgentRoot(Sandbox sandbox)
    {
        var root = Path.Combine(sandbox.Path, "agent-jobs"); Directory.CreateDirectory(root);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return root;
    }
    private sealed record FileFixture(string Name, byte[] Bytes, bool Executable = false);
    private sealed record ProjectFixture(string Id, byte[] Manifest, FileFixture[] Files);
    private static ProjectFixture Project(params FileFixture[] files)
    {
        var metadata = RawCodeRules.Metadata(new JsonObject { ["package"] = "eu.kanade.tachiyomi.extension.en.fixture", ["version"] = "1.0", ["module"] = "src/en/fixture", ["ecosystem"] = "keiyoushi", ["recipe"] = "gradle", ["kind"] = "provided", ["origin"] = new JsonObject { ["basis"] = "user-provided" } });
        var rows = new JsonObject(); foreach (var file in files) rows[file.Name] = new JsonObject { ["sha256"] = Hash(file.Bytes), ["bytes"] = file.Bytes.Length, ["executable"] = file.Executable };
        metadata["schema"] = 1; metadata["files"] = rows; var id = RawCodeRules.Digest(CanonicalJson.Bytes(metadata)); metadata["id"] = id; return new(id, CanonicalJson.Bytes(metadata), files);
    }
    private static GuestArtifactDescriptor FileDescriptor(ProjectFixture project, FileFixture file) => new(GuestArtifactRole.RawProjectFile, file.Bytes.Length, Hash(file.Bytes), file.Name, project.Id);
    private static GuestArtifactDescriptor ManifestDescriptor(ProjectFixture project) => new(GuestArtifactRole.RawProjectManifest, project.Manifest.Length, Hash(project.Manifest), ProjectId: project.Id);
    private static GuestArtifactBinding Binding(string id, GuestArtifactDescriptor descriptor, GuestArtifactDirection direction = GuestArtifactDirection.IntoGuest) => new(Job, Nonce, id, direction, descriptor);
    private static GuestArtifactStore Store(string root, Action<string>? physical = null) => Await(GuestArtifactStore.CreateAsync(root, new GuestPeerIdentity(Job, Nonce), physical, default)); // Controlled identity; no Linux/boot/self-test proof.
    private static string Admit(GuestArtifactStore store, GuestArtifactDescriptor descriptor, byte[] bytes)
    {
        var id = Id(); var lease = Await(store.BeginReceiveAsync(Binding(id, descriptor), default));
        try { Await(lease.AppendAsync(bytes, default)); Await(lease.CompleteAsync(default)); } finally { Await(lease.DisposeAsync().AsTask()); } return id;
    }
    private static GuestProjectCommitRequest AdmitProject(GuestArtifactStore store, ProjectFixture project)
    {
        var references = project.Files.Select(file => new GuestProjectFileReference(Admit(store, FileDescriptor(project, file), file.Bytes), file.Name, file.Executable)).ToArray();
        return new(project.Id, Admit(store, ManifestDescriptor(project), project.Manifest), references);
    }
    private static void Write(GuestChannel channel, string type, string id, object payload, string? nonce = null) => Await(channel.WriteAsync(JsonSerializer.SerializeToElement(new { type, id, nonce = nonce ?? Nonce, payload }), default));
    private static object OpenPayload(string operation, string artifact, GuestArtifactDescriptor descriptor) => new { operation, artifact_id = artifact, role = descriptor.Role.ToString(), length = descriptor.Length, sha256 = descriptor.Sha256, logical_name = descriptor.LogicalName, project_id = descriptor.ProjectId };
    private sealed class PipeEnd(Channel<byte[]> incoming, Channel<byte[]> outgoing) : Stream
    {
        private byte[]? _current; private int _position, _closed;
        public static (PipeEnd Server, PipeEnd Client) Pair() { var a = Channel.CreateUnbounded<byte[]>(); var b = Channel.CreateUnbounded<byte[]>(); return (new(a, b), new(b, a)); }
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.Length == 0) return 0;
            while (_current is null || _position == _current.Length)
            {
                if (!await incoming.Reader.WaitToReadAsync(token).ConfigureAwait(false)) return 0;
                if (!incoming.Reader.TryRead(out _current)) continue; _position = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _position); _current.AsMemory(_position, count).CopyTo(buffer); _position += count; return count;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this); return outgoing.Writer.WriteAsync(buffer.ToArray(), token); }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        protected override void Dispose(bool disposing) { if (Interlocked.Exchange(ref _closed, 1) == 0) { incoming.Writer.TryComplete(); outgoing.Writer.TryComplete(); } base.Dispose(disposing); }
        public override int Read(byte[] bytes, int offset, int count) => ReadAsync(bytes.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] bytes, int offset, int count) => WriteAsync(bytes.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class Session : IDisposable
    {
        private readonly Sandbox _sandbox = new(); private readonly PipeEnd _client;
        private readonly CancellationTokenSource _stop = new(); private int _sequence = 1;
        internal GuestArtifactServer Server { get; }
        internal GuestChannel Client { get; }
        internal GuestArtifactTransfer Transfer { get; }
        internal JsonElement Hello { get; }
        internal Task Running { get; }
        internal bool ExpectedFailure;
        internal string Next() => Interlocked.Increment(ref _sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
        internal Session(Action<string>? physical = null)
        {
            var pair = PipeEnd.Pair(); _client = pair.Client; Client = new(_client, _client);
            var accepting = GuestArtifactServer.AcceptControlledAsync(pair.Server, pair.Server, AgentRoot(_sandbox), physical);
            Write(Client, "hello", "1", new { protocol = 1, agent = "autosourcarr-controller-v1", image_sha256 = Image, job_id = Job });
            using (var hello = Await(Client.ReadAsync(default))) Hello = hello.RootElement.Clone(); Server = Await(accepting); Running = Server.RunAsync(_stop.Token); Transfer = new(Client, Nonce, Job, new GuestArtifactQuota());
        }
        public void Dispose()
        {
            try
            {
                _stop.Cancel(); Await(Server.DisposeAsync().AsTask());
                if (ExpectedFailure) { try { Await(Running); } catch (Exception) { } }
                else Check.Throws<OperationCanceledException>(() => Await(Running));
            }
            finally { Client.Dispose(); _client.Dispose(); _stop.Dispose(); _sandbox.Dispose(); }
        }
    }
    private sealed class MemoryLeases : IGuestArtifactLeaseProvider
    {
        internal byte[] Input = []; internal byte[]? Output; internal bool Committed;
        public Task<GuestArtifactReadLease> OpenInputAsync(GuestArtifactBinding binding, CancellationToken token) => Task.FromResult(new GuestArtifactReadLease(binding, new MemoryStream(Input, writable: false)));
        public Task<GuestArtifactWriteLease> CreateOutputAsync(GuestArtifactBinding binding, CancellationToken token) => Task.FromResult<GuestArtifactWriteLease>(new Sink(this, binding));
        private sealed class Sink(MemoryLeases owner, GuestArtifactBinding binding) : GuestArtifactWriteLease(binding, new MemoryStream())
        {
            public override Task CommitAsync(GuestArtifactReceipt receipt, CancellationToken token)
            { token.ThrowIfCancellationRequested(); Check.Equal(Binding, receipt.Binding); owner.Output = ((MemoryStream)Content).ToArray(); owner.Committed = true; return Task.CompletedTask; }
            public override ValueTask DisposeAsync() => Content.DisposeAsync();
        }
    }
    private sealed class OwnedSource(byte[] bytes) : MemoryStream(bytes, writable: false)
    { internal bool Closed; protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); } }
    private sealed class GateStream(Stream inner) : Stream
    {
        internal volatile bool GateReads; internal readonly TaskCompletionSource ReadEntered = Signal(), ReadReleased = Signal(), CloseEntered = Signal(), CloseReleased = Signal();
        public override async ValueTask<int> ReadAsync(Memory<byte> bytes, CancellationToken token = default)
        { if (GateReads) { ReadEntered.TrySetResult(); await ReadReleased.Task.ConfigureAwait(false); return await inner.ReadAsync(bytes, CancellationToken.None).ConfigureAwait(false); } return await inner.ReadAsync(bytes, token).ConfigureAwait(false); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => inner.WriteAsync(bytes, token);
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override async ValueTask DisposeAsync() { CloseEntered.TrySetResult(); await CloseReleased.Task.ConfigureAwait(false); await inner.DisposeAsync().ConfigureAwait(false); }
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] bytes, int offset, int count) => ReadAsync(bytes.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult(); public override void Write(byte[] bytes, int offset, int count) => WriteAsync(bytes.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class CloseFaultStream(Stream inner, string label) : Stream
    {
        internal bool Closed;
        public override ValueTask<int> ReadAsync(Memory<byte> bytes, CancellationToken token = default) => inner.ReadAsync(bytes, token);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => inner.WriteAsync(bytes, token);
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); Closed = true; throw new IOException(label); }
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] bytes, int offset, int count) => ReadAsync(bytes.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult(); public override void Write(byte[] bytes, int offset, int count) => WriteAsync(bytes.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private static void HardLink(string existing, string target)
    {
        if (OperatingSystem.IsWindows()) Check.True(CreateHardLink(target, existing, IntPtr.Zero), "Controlled guest hard-link fixture failed: " + Marshal.GetLastWin32Error());
        else if (OperatingSystem.IsLinux()) Check.Equal(0, Link(existing, target)); else throw new PlatformNotSupportedException();
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string target, string existing, IntPtr reserved);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string existing, string target);
    public static void Register(TestSuite suite)
    {
        suite.Add("guest server controlled: hello has transport identity only and unsupported self-test remains unavailable", () =>
        {
            using var session = new Session(); var payload = session.Hello.GetProperty("payload"); Check.Equal("autosourcarr-guest-v1", payload.GetProperty("agent").GetString()); Check.Equal(Image, payload.GetProperty("image_sha256").GetString());
            Check.Equal(1, payload.GetProperty("capabilities").GetArrayLength()); Check.Equal("artifact-transfer-v1", payload.GetProperty("capabilities")[0].GetString());
            session.ExpectedFailure = true; var id = session.Next(); Write(session.Client, "request", id, new { operation = "self-test-v1", arguments = new { } }); using var response = Await(session.Client.ReadAsync(default)); Check.Equal("error", response.RootElement.GetProperty("type").GetString()); Check.Equal("unsupported-operation", response.RootElement.GetProperty("payload").GetProperty("code").GetString()); Check.Throws<InvalidDataException>(() => Await(session.Running));
        });
        suite.Add("guest server controlled: real host transfer code uploads exact multichunk/empty files and commits one full canonical tree", () =>
        {
            using var session = new Session(); var project = Project(new("src/en/fixture/src/𝄞.kt", Enumerable.Range(0, 70000).Select(value => (byte)value).ToArray(), true), new("README.md", [])); var leases = new MemoryLeases(); var references = new List<GuestProjectFileReference>();
            foreach (var file in project.Files) { leases.Input = file.Bytes; var received = Await(session.Transfer.UploadAsync(session.Next(), FileDescriptor(project, file), leases, default)); references.Add(new(received.Binding.ArtifactId, file.Name, file.Executable)); }
            leases.Input = project.Manifest; var manifest = Await(session.Transfer.UploadAsync(session.Next(), ManifestDescriptor(project), leases, default));
            var receipt = Await(session.Server.Artifacts.CommitProjectAsync(new(project.Id, manifest.Binding.ArtifactId, references))); Check.Equal(project.Id, receipt.ProjectId); Check.Equal(2, receipt.Files); Check.Equal(70000L, receipt.Bytes); Check.Equal(receipt, session.Server.Artifacts.CommittedProject);
            foreach (var file in project.Files)
            {
                var path = Path.Combine(session.Server.Artifacts.PrivatePath, "trees", receipt.TreeId, "files", file.Name.Replace('/', Path.DirectorySeparatorChar)); Check.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(file.Bytes));
                if (OperatingSystem.IsLinux()) Check.Equal(UnixFileMode.UserRead | (file.Executable ? UnixFileMode.UserExecute : 0), File.GetUnixFileMode(path));
            }
            Check.True(!File.ReadAllText(Path.Combine(session.Server.Artifacts.PrivatePath, "session.json")).Contains(Nonce, StringComparison.Ordinal));
        });
        suite.Add("guest server controlled: produced bytes download through the real host protocol and never claim a built package", () =>
        {
            using var session = new Session(); var bytes = Enumerable.Range(0, 100001).Select(value => (byte)(value * 7)).ToArray(); var descriptor = new GuestArtifactDescriptor(GuestArtifactRole.CandidateJar, bytes.Length, Hash(bytes)); var source = new OwnedSource(bytes);
            var produced = Await(session.Server.Artifacts.PublishOutputAsync(descriptor, source)); Check.True(source.Closed); var leases = new MemoryLeases(); var receipt = Await(session.Transfer.DownloadAsync(session.Next(), produced.ArtifactId, descriptor, leases, default));
            Check.Equal(4, receipt.Chunks); Check.True(leases.Committed && leases.Output is not null && leases.Output.AsSpan().SequenceEqual(bytes));
            session.ExpectedFailure = true; var replay = new GuestArtifactTransfer(session.Client, Nonce, Job, new GuestArtifactQuota()).DownloadAsync(session.Next(), produced.ArtifactId, descriptor, leases, default); Check.Throws<EndOfStreamException>(() => Await(replay)); Check.Throws<InvalidDataException>(() => Await(session.Running));
        });
        suite.Add("guest server controlled: malformed chunk binding/order/Base64/hash/commit and HTTP frames poison rather than acknowledge completion", () =>
        {
            foreach (var mode in new[] { "nonce", "id", "offset", "index", "base64", "hash", "commit", "http" })
            {
                using var session = new Session(); session.ExpectedFailure = true; var bytes = "EXACT_PRIVATE_INPUT"u8.ToArray(); var project = Project(new FileFixture("src/One.kt", bytes)); var descriptor = FileDescriptor(project, project.Files[0]); if (mode == "hash") descriptor = descriptor with { Sha256 = new string('f', 64) };
                var id = session.Next(); var artifact = Id(); Write(session.Client, "request", id, OpenPayload("artifact-upload-v1", artifact, descriptor)); using (var opened = Await(session.Client.ReadAsync(default))) Check.Equal("open", opened.RootElement.GetProperty("payload").GetProperty("phase").GetString());
                Write(session.Client, mode == "http" ? "http-open" : "chunk", mode == "id" ? "999" : id, new { phase = "data", artifact_id = artifact, index = mode == "index" ? 1 : 0, offset = mode == "offset" ? 1 : 0, data = mode == "base64" ? new string('!', Convert.ToBase64String(bytes).Length) : Convert.ToBase64String(bytes) }, mode == "nonce" ? new string('f', 64) : null);
                if (mode is "hash" or "commit")
                {
                    using (var ack = Await(session.Client.ReadAsync(default))) Check.Equal("data", ack.RootElement.GetProperty("payload").GetProperty("phase").GetString());
                    Write(session.Client, "request", id, new { phase = "commit", artifact_id = artifact, length = descriptor.Length, sha256 = descriptor.Sha256, chunks = mode == "commit" ? 2 : 1 });
                }
                Check.Throws<InvalidDataException>(() => Await(session.Running)); Check.True(File.Exists(Path.Combine(session.Server.Artifacts.PrivatePath, "unusable")), mode);
            }
        });
        suite.Add("guest server controlled: full tree rejects extra/missing/reference/exec/hash/project/name collisions and never resumes", () =>
        {
            foreach (var mode in new[] { "extra", "missing", "duplicate", "exec", "hash", "name", "project", "collision" })
            {
                using var sandbox = new Sandbox(); var store = Store(AgentRoot(sandbox));
                try
                {
                    var project = mode == "collision" ? Project(new("src", "ONE"u8.ToArray()), new("src/Two.kt", "TWO"u8.ToArray())) : Project(new("src/One.kt", "ONE"u8.ToArray()), new("src/Two.kt", "TWO"u8.ToArray()));
                    if (mode == "hash")
                    {
                        var json = JsonNode.Parse(project.Manifest)!.AsObject(); json["files"]![project.Files[0].Name]!["sha256"] = new string('f', 64); json.Remove("id"); var changed = RawCodeRules.Digest(CanonicalJson.Bytes(json)); json["id"] = changed; project = project with { Id = changed, Manifest = CanonicalJson.Bytes(json) };
                    }
                    var request = AdmitProject(store, project);
                    if (mode == "extra") Admit(store, new GuestArtifactDescriptor(GuestArtifactRole.RawProjectFile, 0, Hash([]), "extra.kt", project.Id), []);
                    if (mode == "missing") request = request with { Files = request.Files.Take(1).ToArray() };
                    if (mode == "duplicate") request = request with { Files = new[] { request.Files[0], request.Files[0] } };
                    if (mode == "exec") request = request with { Files = new[] { request.Files[0] with { Executable = true }, request.Files[1] } };
                    if (mode == "name") request = request with { Files = new[] { request.Files[0] with { LogicalName = "../foreign.kt" }, request.Files[1] } };
                    if (mode == "project") request = request with { ProjectId = new string('f', 64) };
                    Check.Throws<InvalidDataException>(() => Await(store.CommitProjectAsync(request))); Check.Throws<InvalidDataException>(() => Await(store.CommitProjectAsync(request))); Check.True(!Directory.EnumerateDirectories(Path.Combine(store.PrivatePath, "trees")).Any());
                }
                finally { Await(store.DisposeAsync().AsTask()); }
            }
        });
        suite.Add("guest server controlled: independent final tree scan refuses added files and empty directories", () =>
        {
            foreach (var directory in new[] { false, true })
            {
                using var sandbox = new Sandbox(); var root = AgentRoot(sandbox); GuestArtifactStore? store = null;
                store = Store(root, stage =>
                {
                    if (stage != "commit-tree") return; var tree = Path.Combine(Directory.GetDirectories(Path.Combine(root, Job, "trees")).Single(), "files");
                    if (directory) Directory.CreateDirectory(Path.Combine(tree, "undeclared")); else File.WriteAllBytes(Path.Combine(tree, "undeclared.kt"), [1]);
                });
                try { var request = AdmitProject(store, Project(new FileFixture("src/One.kt", "ORIGINAL"u8.ToArray()))); Check.Throws<InvalidDataException>(() => Await(store.CommitProjectAsync(request))); Check.Throws<InvalidDataException>(() => _ = store.CommittedProject); }
                finally { Await(store.DisposeAsync().AsTask()); }
            }
        });
        suite.Add("guest server controlled: native hard-linked original is refused and its exact original bytes survive", () =>
        {
            using var sandbox = new Sandbox(); var store = Store(AgentRoot(sandbox)); var bytes = "IMMUTABLE_ORIGINAL"u8.ToArray();
            try
            {
                var request = AdmitProject(store, Project(new FileFixture("src/One.kt", bytes))); var path = Path.Combine(store.PrivatePath, "objects", request.Files[0].ArtifactId + ".bin"); HardLink(path, Path.Combine(sandbox.Path, "foreign-link"));
                Check.Throws<InvalidDataException>(() => Await(store.CommitProjectAsync(request))); Check.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes));
            }
            finally { Await(store.DisposeAsync().AsTask()); }
        });
        suite.Add("guest server controlled: project commit is one use and closed filesystem jobs cannot be reopened", () =>
        {
            using var sandbox = new Sandbox(); var root = AgentRoot(sandbox); var store = Store(root);
            try { var request = AdmitProject(store, Project(new FileFixture("src/One.kt", "SOURCE"u8.ToArray()))); Await(store.CommitProjectAsync(request)); Check.Throws<InvalidDataException>(() => Await(store.CommitProjectAsync(request))); }
            finally { Await(store.DisposeAsync().AsTask()); }
            Check.Throws<IOException>(() => Store(root));
        });
        suite.Add("guest server controlled: cancellation joins physical file write/tree publication/close before returning ownership", () =>
        {
            foreach (var phase in new[] { "receive-write", "commit-tree", "file-close" })
            {
                using var sandbox = new Sandbox(); var entered = Signal(); using var release = new ManualResetEventSlim(); var enabled = false;
                var store = Store(AgentRoot(sandbox), stage => { if (enabled && stage == phase) { entered.TrySetResult(); release.Wait(); } }); var project = Project(new FileFixture("src/One.kt", "BLOCKED_PHYSICAL_SOURCE"u8.ToArray())); using var cancel = new CancellationTokenSource(); GuestStorageLease? lease = null; Task active;
                if (phase == "commit-tree") { var request = AdmitProject(store, project); enabled = true; active = store.CommitProjectAsync(request, cancel.Token); }
                else
                {
                    lease = Await(store.BeginReceiveAsync(Binding(Id(), FileDescriptor(project, project.Files[0])), default));
                    if (phase == "file-close") Await(lease.AppendAsync(project.Files[0].Bytes, default)); // Uncommitted native handle is genuinely still open at the controlled close boundary.
                    enabled = true; active = phase == "file-close" ? lease.DisposeAsync().AsTask() : lease.AppendAsync(project.Files[0].Bytes, cancel.Token);
                }
                Await(entered.Task); cancel.Cancel(); var closed = store.DisposeAsync().AsTask();
                try { Check.True(!active.IsCompleted && !closed.IsCompleted, phase); } finally { release.Set(); }
                if (phase == "file-close") Await(active); else Check.Throws<OperationCanceledException>(() => Await(active)); Await(closed); if (lease is not null) Await(lease.DisposeAsync().AsTask());
                if (phase != "file-close") Check.True(File.Exists(Path.Combine(store.PrivatePath, "unusable")), phase);
            }
        });
        suite.Add("guest server controlled: noncooperative physical read and asynchronous close both remain owned after cancellation", () =>
        {
            using var sandbox = new Sandbox(); var pair = PipeEnd.Pair(); var gated = new GateStream(pair.Server); using var client = new GuestChannel(pair.Client, pair.Client);
            var accepting = GuestArtifactServer.AcceptControlledAsync(gated, gated, AgentRoot(sandbox)); Write(client, "hello", "1", new { protocol = 1, agent = "autosourcarr-controller-v1", image_sha256 = Image, job_id = Job }); using (Await(client.ReadAsync(default))) { } var server = Await(accepting); gated.GateReads = true;
            using var cancel = new CancellationTokenSource(); var running = server.RunAsync(cancel.Token); Await(gated.ReadEntered.Task); cancel.Cancel(); var closed = server.DisposeAsync().AsTask(); Await(gated.CloseEntered.Task);
            try
            {
                Check.True(!running.IsCompleted && !closed.IsCompleted); gated.CloseReleased.TrySetResult(); Check.True(!closed.IsCompleted); // Close alone does not finish the physical reader.
            }
            finally { gated.CloseReleased.TrySetResult(); gated.ReadReleased.TrySetResult(); }
            Check.Throws<OperationCanceledException>(() => Await(running)); Await(closed); pair.Client.Dispose();
        });
        suite.Add("guest server controlled: exact producer identity is checked and failed owned sources are physically closed", () =>
        {
            using var sandbox = new Sandbox(); var store = Store(AgentRoot(sandbox)); var source = new OwnedSource("NOT_THE_DECLARED_BYTES"u8.ToArray());
            try { Check.Throws<InvalidDataException>(() => Await(store.PublishOutputAsync(new(GuestArtifactRole.CandidateJar, source.Length, new string('f', 64)), source))); Check.True(source.Closed); Check.Throws<InvalidDataException>(() => _ = store.CommittedProject); }
            finally { Await(store.DisposeAsync().AsTask()); }
        });
        suite.Add("guest server controlled: caller-mutated references after admission cannot change a queued project commit", () =>
        {
            using var sandbox = new Sandbox(); var entered = Signal(); using var release = new ManualResetEventSlim();
            var store = Store(AgentRoot(sandbox), stage => { if (stage == "publish-read") { entered.TrySetResult(); release.Wait(); } });
            try
            {
                var request = AdmitProject(store, Project(new FileFixture("src/One.kt", "PINNED_SOURCE"u8.ToArray()))); var references = request.Files.ToList(); var output = "OPAQUE_OUTPUT"u8.ToArray();
                var publishing = store.PublishOutputAsync(new(GuestArtifactRole.CandidateJar, output.Length, Hash(output)), output); Await(entered.Task);
                Task<GuestProjectCommitReceipt> committing;
                try { committing = store.CommitProjectAsync(request with { Files = references }); references.Clear(); Check.True(!committing.IsCompleted); }
                finally { release.Set(); }
                Await(publishing); var receipt = Await(committing); Check.Equal(1, receipt.Files); Check.Equal(request.ProjectId, receipt.ProjectId);
            }
            finally { release.Set(); Await(store.DisposeAsync().AsTask()); }
        });
        suite.Add("guest server controlled: held directory slots refuse replacement or stale cleanup without touching a peer", () =>
        {
            using var sandbox = new Sandbox(); var store = Store(AgentRoot(sandbox)); var original = store.PrivatePath; var retained = original + ".retained";
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var error = Check.Throws<Exception>(() => Directory.Move(original, retained)); Check.True(error is IOException or UnauthorizedAccessException); Check.True(Directory.Exists(original) && !Directory.Exists(retained));
                }
                finally { Await(store.DisposeAsync().AsTask()); }
            }
            else
            {
                Directory.Move(original, retained); Directory.CreateDirectory(original); File.SetUnixFileMode(original, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); var sentinel = Path.Combine(original, "peer"); File.WriteAllText(sentinel, "UNRELATED_PEER");
                var failure = Check.Throws<AggregateException>(() => Await(store.DisposeAsync().AsTask())); Check.True(failure.Flatten().InnerExceptions.Any(error => error is InvalidDataException)); Check.Equal("UNRELATED_PEER", File.ReadAllText(sentinel)); Check.True(!File.Exists(Path.Combine(original, "closed")) && !File.Exists(Path.Combine(original, "unusable")) && Directory.Exists(retained));
            }
        });
        suite.Add("guest server controlled: original protocol fault and both physically joined close failures are retained", () =>
        {
            using var sandbox = new Sandbox(); var pair = PipeEnd.Pair(); var input = new CloseFaultStream(pair.Server, "CONTROLLED_CLOSE_INPUT"); var output = new CloseFaultStream(pair.Server, "CONTROLLED_CLOSE_OUTPUT"); using var client = new GuestChannel(pair.Client, pair.Client);
            var accepting = GuestArtifactServer.AcceptControlledAsync(input, output, AgentRoot(sandbox)); Write(client, "hello", "1", new { protocol = 1, agent = "autosourcarr-controller-v1", image_sha256 = Image, job_id = Job }); using (Await(client.ReadAsync(default))) { } var server = Await(accepting); var running = server.RunAsync();
            Write(client, "request", "2", new { operation = "self-test-v1", arguments = new { } }); using (Await(client.ReadAsync(default))) { }
            var failure = Check.Throws<AggregateException>(() => Await(running)); var failures = failure.Flatten().InnerExceptions; Check.True(failures.Any(error => error is InvalidDataException)); Check.True(failures.Any(error => error.Message == "CONTROLLED_CLOSE_INPUT") && failures.Any(error => error.Message == "CONTROLLED_CLOSE_OUTPUT")); Check.True(input.Closed && output.Closed);
            Check.Throws<AggregateException>(() => Await(server.DisposeAsync().AsTask())); pair.Client.Dispose();
        });
    }
}
