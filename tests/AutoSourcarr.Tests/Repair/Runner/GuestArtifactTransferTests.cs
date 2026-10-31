using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Repair.Runner.Isolation;
using AutoSourcarr.Core.Repair.Runner.Protocol;
using AutoSourcarr.Core.Repair.Runner.Execution;

namespace AutoSourcarr.Tests;

public static class GuestArtifactTransferTests
{
    private static readonly string Job = new('d', 32), Project = new('a', 64), Output = new('e', 32);
    private static readonly VmBudget Machine = new(512, 1, 64L * 1024 * 1024);
    private static readonly HardwareSessionBudget Budget = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Await<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    private static void Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    private static GuestArtifactDescriptor InputDescriptor(byte[] bytes) => new(GuestArtifactRole.RawProjectFile, bytes.Length, Hash(bytes), "src/Example.kt", Project);
    private static GuestArtifactDescriptor OutputDescriptor(byte[] bytes) => new(GuestArtifactRole.CandidateJar, bytes.Length, Hash(bytes));

    private sealed class Pipe : Stream
    {
        private readonly Channel<byte[]> _parts = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
        private byte[]? _part; private int _position;
        public void Complete() => _parts.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.Length == 0) return 0;
            while (_part is null || _position == _part.Length)
            { if (!await _parts.Reader.WaitToReadAsync(token).ConfigureAwait(false)) return 0; if (!_parts.Reader.TryRead(out _part)) continue; _position = 0; }
            var count = Math.Min(buffer.Length, _part.Length - _position); _part.AsMemory(_position, count).CopyTo(buffer); _position += count; return count;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => _parts.Writer.WriteAsync(buffer.ToArray(), token);
        public override void Write(byte[] bytes, int offset, int count) => WriteAsync(bytes.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override int Read(byte[] bytes, int offset, int count) => ReadAsync(bytes.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { } public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) Complete(); base.Dispose(disposing); }
    }
    private sealed class BlockingMemory(byte[]? bytes, bool writable) : MemoryStream(bytes ?? [], writable)
    {
        private readonly bool _blockReads = !writable;
        public TaskCompletionSource? Entered, Release;
        public bool Closed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { if (_blockReads && Release is not null) { Entered!.TrySetResult(); await Release.Task.ConfigureAwait(false); } return await base.ReadAsync(buffer, token).ConfigureAwait(false); }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
    private sealed class SinkStream : MemoryStream
    {
        public TaskCompletionSource? Entered, Release;
        public bool Closed;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { if (Release is not null) { Entered!.TrySetResult(); await Release.Task.ConfigureAwait(false); } await base.WriteAsync(buffer, token).ConfigureAwait(false); }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
    private sealed class Leases(byte[] input) : IGuestArtifactLeaseProvider
    {
        public readonly BlockingMemory Source = new(input, false);
        public readonly SinkStream Sink = new();
        public bool WrongBinding, Committed, Aborted;
        public TaskCompletionSource? CommitEntered, CommitRelease;
        public int Opened;
        public byte[]? Published;
        public Task<GuestArtifactReadLease> OpenInputAsync(GuestArtifactBinding binding, CancellationToken token)
        {
            Opened++; token.ThrowIfCancellationRequested(); return Task.FromResult(new GuestArtifactReadLease(WrongBinding ? binding with { JobId = new string('f', 32) } : binding, Source));
        }
        public Task<GuestArtifactWriteLease> CreateOutputAsync(GuestArtifactBinding binding, CancellationToken token)
        { Opened++; token.ThrowIfCancellationRequested(); return Task.FromResult<GuestArtifactWriteLease>(new SinkLease(this, WrongBinding ? binding with { ArtifactId = new string('f', 32) } : binding)); }
        private sealed class SinkLease(Leases owner, GuestArtifactBinding binding) : GuestArtifactWriteLease(binding, owner.Sink)
        {
            public override async Task CommitAsync(GuestArtifactReceipt receipt, CancellationToken token)
            {
                if (owner.CommitRelease is not null) { owner.CommitEntered!.TrySetResult(); await owner.CommitRelease.Task.ConfigureAwait(false); }
                token.ThrowIfCancellationRequested(); Check.Equal(Binding, receipt.Binding); owner.Published = owner.Sink.ToArray(); owner.Committed = true;
            }
            public override async ValueTask DisposeAsync() { if (!owner.Committed) owner.Aborted = true; await owner.Sink.DisposeAsync().ConfigureAwait(false); }
        }
    }
    private sealed class EnvironmentFixture(string root) : IHardwareEnvironment
    {
        public Task<PreparedHardwareJob> PrepareAsync(string id, VmBudget budget, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var assets = Path.Combine(root, "guest"); Directory.CreateDirectory(assets);
            var kernel = "CONTROLLED_ARTIFACT_KERNEL"u8.ToArray(); var disk = "CONTROLLED_ARTIFACT_ROOTFS"u8.ToArray();
            File.WriteAllBytes(Path.Combine(assets, "kernel"), kernel); File.WriteAllBytes(Path.Combine(assets, "rootfs.raw"), disk);
            File.WriteAllText(Path.Combine(assets, "guest-assets.json"), JsonSerializer.Serialize(new { format = 1, kernelSha256 = Hash(kernel), rootfsSha256 = Hash(disk) }));
            var guest = GuestAssets.Load(assets); var job = Path.Combine(root, "source-maintenance", "vm-jobs", id); Directory.CreateDirectory(job);
            return Task.FromResult(new PreparedHardwareJob(VmLaunchPlan.Create(job, guest, budget), guest, budget, 1000, Stream.Null));
        }
    }
    private sealed class Factory(byte[] bytes, string mode = "valid") : IHardwareProcessFactory
    {
        public PeerProcess? Peer;
        public IHardwareProcess Start(VmLaunchPlan plan) => Peer = new PeerProcess(plan, bytes, mode);
    }
    private sealed class PeerProcess : IHardwareProcess
    {
        private readonly Pipe _input = new(), _output = new();
        private readonly MemoryStream _error;
        private readonly Task _peer;
        private readonly byte[] _bytes;
        private readonly string _mode;
        public readonly TaskCompletionSource Killed = Signal();
        public bool ReceivedCommit;
        public int Disposed;
        public byte[] Received = [];
        public int Id => 5678;
        public bool HasExited => _peer.IsCompleted;
        public Stream Input => _input; public Stream Output => _output; public Stream Error => _error;
        public PeerProcess(VmLaunchPlan plan, byte[] bytes, string mode)
        {
            _bytes = bytes; _mode = mode;
            _error = new MemoryStream(Encoding.ASCII.GetBytes("AUTOSOURCARR_SANDBOX_V2 " + plan.ProofNonce + " {\"pid\":5678,\"uid\":1000,\"ppid\":" + System.Environment.ProcessId + ",\"pdeathsig\":9,\"landlock\":6,\"nnp\":1,\"seccomp\":2}\n"));
            _peer = Task.Run(RunAsync);
        }
        private async Task RunAsync()
        {
            try
            {
                using var channel = new GuestChannel(_input, _output); using var hello = await channel.ReadAsync(default).ConfigureAwait(false);
                var root = hello.RootElement; var nonce = root.GetProperty("nonce").GetString()!;
                await Send(channel, "hello", root.GetProperty("id").GetString()!, nonce, new { protocol = 1, agent = "autosourcarr-guest-v1", image_sha256 = root.GetProperty("payload").GetProperty("image_sha256").GetString(), capabilities = new[] { "self-test-v1", "artifact-transfer-v1" } }).ConfigureAwait(false);
                using var request = await channel.ReadAsync(default).ConfigureAwait(false); var test = request.RootElement; var args = test.GetProperty("payload").GetProperty("arguments"); var challenge = args.GetProperty("challenge").GetString()!;
                await Send(channel, "result", test.GetProperty("id").GetString()!, nonce, new { protocol = 1, suite = "autosourcarr-guest-selftest-v1", challenge,
                    scratch_bytes = args.GetProperty("scratch_bytes").GetInt64(), scratch_roundtrip_sha256 = Hash(Encoding.ASCII.GetBytes(challenge)), root_write_errno = 30,
                    non_loopback_interfaces = 0, host_share_mounts = 0, effective_uid = 1000, working_directory = "/work", scratch_device = "/dev/vdb", memory_mib = args.GetProperty("memory_mib").GetInt32(), processors = args.GetProperty("processors").GetInt32() }).ConfigureAwait(false);
                while (true)
                {
                    using var open = await channel.ReadAsync(default).ConfigureAwait(false); var frame = open.RootElement; var payload = frame.GetProperty("payload");
                    var id = frame.GetProperty("id").GetString()!; var artifact = payload.GetProperty("artifact_id").GetString()!; var length = payload.GetProperty("length").GetInt64(); var digest = payload.GetProperty("sha256").GetString()!;
                    var ack = new { phase = "open", artifact_id = artifact, index = 0, next_offset = 0 };
                    await Send(channel, "result", _mode == "wrong-id" ? "stale" : id, _mode == "wrong-nonce" ? new string('f', 64) : nonce, ack).ConfigureAwait(false);
                    if (payload.GetProperty("operation").GetString() == "artifact-upload-v1")
                    {
                        using var received = new MemoryStream(); var index = 0;
                        while (received.Length < length)
                        {
                            using var chunk = await channel.ReadAsync(default).ConfigureAwait(false); var data = chunk.RootElement.GetProperty("payload"); var value = Convert.FromBase64String(data.GetProperty("data").GetString()!); received.Write(value);
                            if (_mode == "drop-ack") { await Killed.Task.ConfigureAwait(false); return; }
                            await Send(channel, "result", id, nonce, new { phase = "data", artifact_id = artifact, index = _mode == "wrong-ack" ? index + 1 : index, next_offset = received.Length }).ConfigureAwait(false); index++;
                        }
                        Received = received.ToArray(); using var end = await channel.ReadAsync(default).ConfigureAwait(false); ReceivedCommit = true;
                        await Send(channel, "result", id, nonce, new { phase = "commit", artifact_id = artifact, length, sha256 = digest, chunks = index }).ConfigureAwait(false);
                    }
                    else
                    {
                        var offset = 0; var index = 0;
                        while (offset < _bytes.Length)
                        {
                            var count = Math.Min(GuestArtifactTransfer.ChunkBytes, _bytes.Length - offset); var encoded = Convert.ToBase64String(_bytes, offset, count);
                            if (_mode == "bad-base64") encoded = "!" + encoded[1..];
                            var chunk = JsonSerializer.SerializeToElement(new { phase = "data", artifact_id = artifact, index = _mode == "replayed" && index == 1 ? 0 : index, offset = _mode == "wrong-offset" ? offset + 1 : offset, data = encoded });
                            if (_mode == "extra-field") { var altered = JsonNode.Parse(chunk.GetRawText())!.AsObject(); altered["host_path"] = "/data/account"; chunk = JsonSerializer.SerializeToElement(altered); }
                            if (_mode == "duplicate-field") { using var duplicate = JsonDocument.Parse(chunk.GetRawText().Replace("\"offset\":0", "\"offset\":0,\"offset\":0", StringComparison.Ordinal)); chunk = duplicate.RootElement.Clone(); }
                            await Send(channel, "chunk", id, nonce, chunk).ConfigureAwait(false); using var accepted = await channel.ReadAsync(default).ConfigureAwait(false);
                            offset += count; index++;
                        }
                        await Send(channel, "result", id, nonce, new { phase = "commit", artifact_id = artifact, length, sha256 = digest, chunks = _mode == "wrong-count" ? index + 1 : index }).ConfigureAwait(false);
                        using var committed = await channel.ReadAsync(default).ConfigureAwait(false); ReceivedCommit = true;
                    }
                }
            }
            catch (Exception error) when (error is EndOfStreamException or ChannelClosedException or OperationCanceledException or ObjectDisposedException) { }
            finally { _input.Complete(); _output.Complete(); }
        }
        private static Task Send<T>(GuestChannel channel, string type, string id, string nonce, T payload) => channel.WriteAsync(JsonSerializer.SerializeToElement(new { type, id, nonce, payload }), default);
        public Task WaitForExitAsync() => _peer;
        public void Kill() { Killed.TrySetResult(); _input.Complete(); _output.Complete(); }
        public void Dispose() => Disposed++;
    }
    private static void Close(HardwareVmRuntime runtime, HardwareVmSession? session)
    { Await(runtime.StopAsync()); if (session is not null) Await(session.DisposeAsync().AsTask()); Await(runtime.DisposeAsync().AsTask()); }
    public static void Register(TestSuite suite)
    {
        suite.Add("guest artifact controlled: exact multi-chunk uploads/downloads return byte receipts and preserve originals", () =>
        {
            var data = Enumerable.Range(0, 70001).Select(index => (byte)(index % 251)).ToArray(); var original = (byte[])data.Clone(); using var sandbox = new Sandbox(); var factory = new Factory(data); var runtime = new HardwareVmRuntime(new EnvironmentFixture(sandbox.Path), factory); HardwareVmSession? session = null;
            try
            {
                session = Await(runtime.StartAsync(Job, Machine, Budget)); var leases = new Leases(data); var uploaded = Await(session.UploadArtifactAsync(InputDescriptor(data), leases));
                Check.Equal(3, uploaded.Chunks); Check.Equal(Job, uploaded.Binding.JobId); Check.True(factory.Peer!.Received.AsSpan().SequenceEqual(data)); Check.True(leases.Source.Closed);
                var downloaded = Await(session.DownloadArtifactAsync(Output, OutputDescriptor(data), leases)); Check.Equal(3, downloaded.Chunks); Check.True(leases.Committed && leases.Sink.Closed);
                var published = leases.Published ?? throw new InvalidOperationException("The controlled quarantine did not publish bytes"); Check.True(published.AsSpan().SequenceEqual(data));
                Check.True(data.AsSpan().SequenceEqual(original)); Check.Throws<InvalidDataException>(() => SourcePackage.Payloads(published)); // Receiving bytes does not validate a package.
                var empty = new Leases([]); var emptyReceipt = Await(session.UploadArtifactAsync(InputDescriptor([]) with { LogicalName = "src/Empty.kt" }, empty)); Check.Equal(0, emptyReceipt.Chunks); Check.True(empty.Source.Closed);
            }
            finally { Close(runtime, session); }
        });
        suite.Add("guest artifact: raw tree and manifest quotas preserve the full160MiB tree without a ZIP export ceiling", () =>
        {
            var hash = new string('a', 64); var quota = new GuestArtifactQuota();
            for (var index = 0; index < 20; index++) quota.Reserve(new(GuestArtifactRole.RawProjectFile, 8L * 1024 * 1024, hash, "src/" + index + ".kt", Project), GuestArtifactDirection.IntoGuest);
            quota.Reserve(new(GuestArtifactRole.RawProjectManifest, 2L * 1024 * 1024, hash, ProjectId: Project), GuestArtifactDirection.IntoGuest);
            quota.Reserve(new(GuestArtifactRole.OriginalApk, 32L * 1024 * 1024, hash), GuestArtifactDirection.IntoGuest); quota.Reserve(new(GuestArtifactRole.InstalledJar, 32L * 1024 * 1024, hash), GuestArtifactDirection.IntoGuest);
            Check.Throws<InvalidDataException>(() => quota.Reserve(new(GuestArtifactRole.RawProjectFile, 1, hash, "src/extra.kt", Project), GuestArtifactDirection.IntoGuest));
            quota = new GuestArtifactQuota();
            for (var index = 0; index < 8192; index++) quota.Reserve(new(GuestArtifactRole.RawProjectFile, 0, hash, "src/" + index + ".kt", Project), GuestArtifactDirection.IntoGuest);
            Check.Throws<InvalidDataException>(() => quota.Reserve(new(GuestArtifactRole.RawProjectFile, 0, hash, "src/extra.kt", Project), GuestArtifactDirection.IntoGuest));
            Check.Throws<InvalidDataException>(() => new GuestArtifactDescriptor(GuestArtifactRole.RawProjectFile, 1, hash, "../account", Project).Validate(GuestArtifactDirection.IntoGuest));
            Check.Throws<InvalidDataException>(() => new GuestArtifactDescriptor(GuestArtifactRole.RawProjectManifest, 2L * 1024 * 1024 + 1, hash, ProjectId: Project).Validate(GuestArtifactDirection.IntoGuest));
        });
        suite.Add("guest artifact controlled: malformed, replayed and cross-session replies kill and join without output publication", () =>
        {
            var data = Enumerable.Range(0, 40000).Select(index => (byte)(index % 241)).ToArray();
            foreach (var mode in new[] { "wrong-id", "wrong-nonce", "wrong-ack", "bad-base64", "wrong-offset", "replayed", "extra-field", "duplicate-field", "wrong-count" })
            {
                using var sandbox = new Sandbox(); var factory = new Factory(data, mode); var runtime = new HardwareVmRuntime(new EnvironmentFixture(sandbox.Path), factory); HardwareVmSession? session = null; var leases = new Leases(data);
                try
                {
                    session = Await(runtime.StartAsync(Job, Machine, Budget));
                    if (mode == "wrong-ack") Check.Throws<InvalidDataException>(() => Await(session.UploadArtifactAsync(InputDescriptor(data), leases)));
                    else Check.Throws<InvalidDataException>(() => Await(session.DownloadArtifactAsync(Output, OutputDescriptor(data), leases)));
                    Check.True(factory.Peer!.HasExited && factory.Peer.Disposed == 1 && !leases.Committed && leases.Published is null);
                }
                catch (Exception error) { throw new InvalidOperationException("Controlled artifact protocol case " + mode, error); }
                finally { Close(runtime, session); }
            }
        });
        suite.Add("guest artifact controlled: changed input hash, wrong private lease and arbitrary output paths never commit", () =>
        {
            var data = "SOURCE_INPUT_BYTES"u8.ToArray();
            foreach (var mode in new[] { "hash", "lease", "path", "output-hash" })
            {
                using var sandbox = new Sandbox(); var factory = new Factory(data); var runtime = new HardwareVmRuntime(new EnvironmentFixture(sandbox.Path), factory); HardwareVmSession? session = null; var leases = new Leases(data) { WrongBinding = mode == "lease" };
                try
                {
                    session = Await(runtime.StartAsync(Job, Machine, Budget));
                    if (mode == "path") Check.Throws<InvalidDataException>(() => Await(session.DownloadArtifactAsync("/data/account", OutputDescriptor(data), leases)));
                    else if (mode == "output-hash") Check.Throws<InvalidDataException>(() => Await(session.DownloadArtifactAsync(Output, OutputDescriptor(data) with { Sha256 = new string('f', 64) }, leases)));
                    else Check.Throws<InvalidDataException>(() => Await(session.UploadArtifactAsync(mode == "hash" ? InputDescriptor(data) with { Sha256 = new string('f', 64) } : InputDescriptor(data), leases)));
                    Check.True(!factory.Peer!.ReceivedCommit && !leases.Committed); if (mode == "path") Check.Equal(0, leases.Opened);
                }
                finally { Close(runtime, session); }
            }
        });
        suite.Add("guest artifact controlled: cancellation joins blocked physical input/output IO before releasing the session", () =>
        {
            var data = "PHYSICAL_ARTIFACT_BYTES"u8.ToArray();
            foreach (var phase in new[] { "read", "write", "commit" })
            {
                using var sandbox = new Sandbox(); var factory = new Factory(data); var runtime = new HardwareVmRuntime(new EnvironmentFixture(sandbox.Path), factory); HardwareVmSession? session = null; var leases = new Leases(data); var entered = Signal(); var release = Signal();
                try
                {
                    session = Await(runtime.StartAsync(Job, Machine, Budget));
                    if (phase == "read") { leases.Source.Entered = entered; leases.Source.Release = release; }
                    else if (phase == "write") { leases.Sink.Entered = entered; leases.Sink.Release = release; }
                    else { leases.CommitEntered = entered; leases.CommitRelease = release; }
                    using var cancel = new CancellationTokenSource(); var transfer = phase == "read" ? session.UploadArtifactAsync(InputDescriptor(data), leases, cancel.Token) : session.DownloadArtifactAsync(Output, OutputDescriptor(data), leases, cancel.Token);
                    Await(entered.Task); cancel.Cancel(); Await(factory.Peer!.Killed.Task); var stopped = runtime.StopAsync(); Check.True(!stopped.IsCompleted && !transfer.IsCompleted && !leases.Committed);
                    release.TrySetResult(); Check.Throws<OperationCanceledException>(() => Await(transfer)); Await(stopped); Check.True(phase == "read" ? leases.Source.Closed : leases.Sink.Closed); Check.True(!leases.Committed);
                }
                finally { release.TrySetResult(); Close(runtime, session); }
            }
        });
        suite.Add("guest artifact controlled: a dropped chunk acknowledgement times out without a commit or replay", () =>
        {
            var data = "NO_REPLAY_OR_COMMIT"u8.ToArray(); using var sandbox = new Sandbox(); var factory = new Factory(data, "drop-ack");
            var runtime = new HardwareVmRuntime(new EnvironmentFixture(sandbox.Path), factory); HardwareVmSession? session = null; var leases = new Leases(data);
            try
            {
                session = Await(runtime.StartAsync(Job, Machine, Budget with { RequestTimeout = TimeSpan.FromMilliseconds(100) }));
                Check.Throws<TimeoutException>(() => Await(session.UploadArtifactAsync(InputDescriptor(data), leases)));
                Check.True(factory.Peer!.HasExited && !factory.Peer.ReceivedCommit && leases.Source.Closed);
            }
            finally { Close(runtime, session); }
        });
    }
}
