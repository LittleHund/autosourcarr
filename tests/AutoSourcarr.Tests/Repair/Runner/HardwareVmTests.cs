using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AutoSourcarr.Core.Repair.Runner.Isolation;
using AutoSourcarr.Core.Repair.Runner.Protocol;
using AutoSourcarr.Core.Repair.Runner.Execution;

namespace AutoSourcarr.Tests;

public static class HardwareVmTests
{
    private static readonly VmBudget Machine = new(512, 1, 64L * 1024 * 1024);
    private static readonly HardwareSessionBudget Time = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));
    private static readonly string Job = new('a', 32);
    private static T Await<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    private static void Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class BytePipe : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
        private byte[]? _current; private int _position;
        public long ReadBytes, WrittenBytes;
        public Action<ReadOnlyMemory<byte>>? BeforeWrite;
        public Func<ReadOnlyMemory<byte>, Task>? PhysicalWrite;
        public void Complete() => _chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.IsEmpty) return 0;
            while (_current is null || _position == _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(token).ConfigureAwait(false)) return 0;
                if (!_chunks.Reader.TryRead(out _current)) continue; _position = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _position); _current.AsMemory(_position, count).CopyTo(buffer); _position += count; ReadBytes += count; return count;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            BeforeWrite?.Invoke(buffer); if (PhysicalWrite is not null) await PhysicalWrite(buffer).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); await _chunks.Writer.WriteAsync(buffer.ToArray(), token).ConfigureAwait(false); WrittenBytes += buffer.Length;
        }
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override void Flush() { }
        protected override void Dispose(bool disposing) { if (disposing) Complete(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class FakeEnvironment(string root) : IHardwareEnvironment
    {
        public TaskCompletionSource? PrepareEntered, PrepareRelease;
        public int Prepared, Closed;
        public async Task<PreparedHardwareJob> PrepareAsync(string id, VmBudget budget, CancellationToken token)
        {
            PrepareEntered?.TrySetResult(); if (PrepareRelease is not null) await PrepareRelease.Task.ConfigureAwait(false); // Deliberately physical/non-cancellable.
            var assetsRoot = Path.Combine(root, "guest"); Directory.CreateDirectory(assetsRoot);
            var kernel = "CONTROLLED_KERNEL_FIXTURE"u8.ToArray(); var disk = "CONTROLLED_ROOTFS_FIXTURE"u8.ToArray();
            File.WriteAllBytes(Path.Combine(assetsRoot, "kernel"), kernel); File.WriteAllBytes(Path.Combine(assetsRoot, "rootfs.raw"), disk);
            File.WriteAllText(Path.Combine(assetsRoot, "guest-assets.json"), JsonSerializer.Serialize(new { format = 1, kernelSha256 = Convert.ToHexStringLower(SHA256.HashData(kernel)), rootfsSha256 = Convert.ToHexStringLower(SHA256.HashData(disk)) }));
            var assets = GuestAssets.Load(assetsRoot); var job = Path.Combine(root, "source-maintenance", "vm-jobs", id); Directory.CreateDirectory(job);
            var scratch = new FileStream(Path.Combine(job, "scratch.raw"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite); scratch.SetLength(budget.ScratchBytes); Prepared++;
            return new(VmLaunchPlan.Create(job, assets, budget), assets, budget, 1000, new Closure(() => { scratch.Dispose(); Closed++; }));
        }
    }
    private sealed class Closure(Action action) : IDisposable { public void Dispose() => action(); }
    private sealed class BlockingChild : IBlockingHardwareChild
    {
        public readonly ManualResetEventSlim Exited = new(false), WaitEntered = new(false), KillEntered = new(false);
        public Thread? Creator, Waiter;
        public bool ThrowFirstWait;
        private int _waits;
        public int Kills, Disposed;
        public int Id => 9001;
        public bool HasExited => Exited.IsSet;
        public Stream Input => Stream.Null; public Stream Output => Stream.Null; public Stream Error => Stream.Null;
        public void WaitForExit()
        {
            Waiter = Thread.CurrentThread; WaitEntered.Set();
            if (ThrowFirstWait && Interlocked.Increment(ref _waits) == 1) throw new IOException("Controlled physical wait failure");
            Exited.Wait();
        }
        public void Kill() { Interlocked.Increment(ref Kills); KillEntered.Set(); }
        public void Dispose() { Disposed++; Exited.Dispose(); WaitEntered.Dispose(); KillEntered.Dispose(); }
    }
    private sealed class FakeFactory(string mode = "valid") : IHardwareProcessFactory
    {
        public FakeProcess? Process;
        public TaskCompletionSource? StartEntered, StartRelease;
        public int Starts;
        public IHardwareProcess Start(VmLaunchPlan plan)
        {
            Starts++; StartEntered?.TrySetResult(); StartRelease?.Task.GetAwaiter().GetResult();
            return Process = new FakeProcess(plan, mode);
        }
    }
    private sealed class FakeProcess : IHardwareProcess
    {
        public readonly BytePipe HostInput = new(), HostOutput = new(), HostError = new();
        public readonly TaskCompletionSource OperationSeen = Signal(), Killed = Signal(), ExitRelease = Signal();
        private readonly Task _peer;
        private readonly string _mode;
        public bool HoldExit;
        public int KillCalls, Disposed;
        public bool BeforeProofWrite;
        public int Id => 4321;
        public bool HasExited => _peer.IsCompleted;
        public Stream Input => HostInput; public Stream Output => HostOutput; public Stream Error => HostError;
        public FakeProcess(VmLaunchPlan plan, string mode)
        {
            _mode = mode; var nonce = mode == "native-nonce" ? new string('b', 64) : plan.ProofNonce;
            var proof = Encoding.ASCII.GetBytes("AUTOSOURCARR_SANDBOX_V2 " + nonce + " {\"pid\":4321,\"uid\":1000,\"ppid\":" + Environment.ProcessId + ",\"pdeathsig\":9,\"landlock\":6,\"nnp\":1,\"seccomp\":2}\n");
            if (mode != "no-proof") HostError.Write(proof);
            HostInput.BeforeWrite = _ => { if (HostError.ReadBytes < proof.Length) BeforeProofWrite = true; };
            _peer = Task.Run(PeerAsync);
        }
        private async Task PeerAsync()
        {
            try
            {
                using var channel = new GuestChannel(HostInput, HostOutput);
                using var hello = await channel.ReadAsync(default).ConfigureAwait(false); var frame = hello.RootElement;
                var nonce = frame.GetProperty("nonce").GetString()!; var id = frame.GetProperty("id").GetString()!;
                var image = frame.GetProperty("payload").GetProperty("image_sha256").GetString()!;
                var capabilities = _mode == "hello-capabilities" ? new[] { "self-test-v1", "self-test-v1" } : new[] { "self-test-v1", "package-inspect-v1", "project-build-v1", "package-validate-v1" };
                await channel.WriteAsync(JsonSerializer.SerializeToElement(new { type = "hello", id, nonce = _mode == "hello-nonce" ? "wrong" : nonce,
                    payload = new { protocol = 1, agent = "autosourcarr-guest-v1", image_sha256 = _mode == "hello-image" ? new string('c', 64) : image, capabilities } }), default).ConfigureAwait(false);
                using var selftest = await channel.ReadAsync(default).ConfigureAwait(false); var root = selftest.RootElement;
                var args = root.GetProperty("payload").GetProperty("arguments"); var challenge = args.GetProperty("challenge").GetString()!;
                var report = new JsonObject { ["protocol"] = 1, ["suite"] = "autosourcarr-guest-selftest-v1", ["challenge"] = challenge,
                    ["scratch_bytes"] = args.GetProperty("scratch_bytes").GetInt64(), ["scratch_roundtrip_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(challenge))),
                    ["root_write_errno"] = 30, ["non_loopback_interfaces"] = 0, ["host_share_mounts"] = 0, ["effective_uid"] = 1000,
                    ["working_directory"] = "/work", ["scratch_device"] = "/dev/vdb", ["memory_mib"] = args.GetProperty("memory_mib").GetInt32(), ["processors"] = args.GetProperty("processors").GetInt32() };
                if (_mode == "selftest-missing") report.Remove("root_write_errno");
                if (_mode == "selftest-network") report["non_loopback_interfaces"] = 1;
                if (_mode == "selftest-scratch") report["scratch_bytes"] = 1;
                if (_mode == "selftest-root") report["root_write_errno"] = 0;
                await channel.WriteAsync(JsonSerializer.SerializeToElement(new { type = "result", id = root.GetProperty("id").GetString(), nonce, payload = report }), default).ConfigureAwait(false);
                while (true)
                {
                    using var request = await channel.ReadAsync(default).ConfigureAwait(false); var item = request.RootElement; var requestId = item.GetProperty("id").GetString(); OperationSeen.TrySetResult();
                    if (_mode == "block-operation") { await Killed.Task.ConfigureAwait(false); break; }
                    if (_mode == "log-budget") for (var index = 0; index < 33; index++) await channel.WriteAsync(JsonSerializer.SerializeToElement(new { type = "log", id = requestId, nonce, payload = new { message = "PRIVATE_PROTOCOL_CANARY" } }), default).ConfigureAwait(false);
                    var type = _mode == "egress-frame" ? "http-open" : _mode == "guest-error" ? "error" : "result";
                    await channel.WriteAsync(JsonSerializer.SerializeToElement(new { type, id = _mode == "result-id" ? "stale" : requestId, nonce,
                        payload = new { accepted = true, opaque = item.GetProperty("payload").GetProperty("arguments").GetProperty("opaque").GetString() } }), default).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is EndOfStreamException or ChannelClosedException or OperationCanceledException or ObjectDisposedException) { }
            finally { if (HoldExit) await ExitRelease.Task.ConfigureAwait(false); HostOutput.Complete(); HostError.Complete(); }
        }
        public Task WaitForExitAsync() => _peer;
        public void Kill() { Interlocked.Increment(ref KillCalls); Killed.TrySetResult(); HostInput.Complete(); HostOutput.Complete(); HostError.Complete(); }
        public void Dispose() => Disposed++;
    }
    public static void Register(TestSuite suite)
    {
        suite.Add("hardware parent lifetime controlled: non-pool creator survives caller return and joins after physical exit", () =>
        {
            var child = new BlockingChild(); Thread? creator = null; IHardwareProcess? owned = null;
            try
            {
                owned = Await(Task.Run(() => DedicatedHardwareProcess.Start(() => { child.Creator = Thread.CurrentThread; return child; }, thread => creator = thread)));
                Check.True(child.WaitEntered.Wait(TimeSpan.FromSeconds(5))); var lifetimeThread = creator ?? throw new InvalidOperationException("Creator thread was not published");
                Check.True(lifetimeThread.IsAlive && !lifetimeThread.IsThreadPoolThread);
                Check.True(ReferenceEquals(child.Creator, lifetimeThread) && ReferenceEquals(child.Waiter, lifetimeThread)); var exit = owned.WaitForExitAsync();
                owned.Kill(); Check.True(!exit.IsCompleted && lifetimeThread.IsAlive); child.Exited.Set(); Await(exit); Check.True(!lifetimeThread.IsAlive);
                owned.Dispose(); owned.Dispose(); Check.Equal(1, child.Disposed);
            }
            finally { if (child.Disposed == 0) { child.Exited.Set(); owned?.Dispose(); } }
        });
        suite.Add("hardware parent lifetime controlled: failed startup leaves no creating thread", () =>
        {
            Thread? creator = null;
            Check.Throws<IOException>(() => DedicatedHardwareProcess.Start(() => throw new IOException("Controlled launch failure"), thread => creator = thread));
            Check.True(creator is not null && !creator.IsAlive);
        });
        suite.Add("hardware parent lifetime controlled: wait failure cannot retire the creator before the child is gone", () =>
        {
            var child = new BlockingChild { ThrowFirstWait = true }; Thread? creator = null; IHardwareProcess? owned = null;
            try
            {
                owned = DedicatedHardwareProcess.Start(() => { child.Creator = Thread.CurrentThread; return child; }, thread => creator = thread);
                Check.True(child.KillEntered.Wait(TimeSpan.FromSeconds(5))); var exit = owned.WaitForExitAsync(); Check.True(creator!.IsAlive && !exit.IsCompleted);
                child.Exited.Set(); owned.Kill(); Check.Throws<IOException>(() => Await(exit)); Check.True(!creator.IsAlive); owned.Dispose(); Check.Equal(1, child.Disposed);
            }
            finally { if (child.Disposed == 0) { child.Exited.Set(); owned?.Kill(); owned?.Dispose(); } }
        });
        suite.Add("hardware parent lifetime: launch arguments bind only the current parent and keep host-only values outside QEMU", () =>
        {
            using var sandbox = new Sandbox(); var environment = new FakeEnvironment(sandbox.Path); var prepared = Await(environment.PrepareAsync(Job, Machine, default));
            using (prepared)
            {
                var start = prepared.Plan.StartInfo(); Check.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), start.ArgumentList[2]); Check.Equal("--", start.ArgumentList[3]);
                var stale = prepared.Plan.Arguments.ToArray(); stale[2] = "0";
                Check.Throws<InvalidDataException>(() => (prepared.Plan with { Arguments = stale }).StartInfo());
            }
        });
        suite.Add("hardware VM: production platform and absent image cannot enable a guest", () =>
        {
            if (OperatingSystem.IsLinux()) return;
            using var sandbox = new Sandbox(); var runtime = new HardwareVmRuntime(sandbox.Path);
            Check.Throws<PlatformNotSupportedException>(() => Await(runtime.StartAsync(Job, Machine, Time))); Await(runtime.DisposeAsync().AsTask());
        });
        suite.Add("hardware VM controlled: native proof precedes serial bytes; structured guest report precedes private RPC", () =>
        {
            using var sandbox = new Sandbox(); var environment = new FakeEnvironment(sandbox.Path); var factory = new FakeFactory(); var runtime = new HardwareVmRuntime(environment, factory);
            var session = Await(runtime.StartAsync(Job, Machine, Time)); Check.Equal(HardwareSessionState.GuestQualified, session.State);
            Check.Equal(4321, session.NativeProof.ProcessId); Check.Equal(1000u, session.NativeProof.UserId); Check.Equal(Machine.ScratchBytes, session.GuestReport.ScratchBytes);
            var result = Await(session.RequestAsync(HardwareGuestOperation.InspectPackage, JsonSerializer.SerializeToElement(new { opaque = "PRIVATE_INPUT_CANARY" })));
            Check.Equal("PRIVATE_INPUT_CANARY", result.GetProperty("opaque").GetString()); Check.True(!factory.Process!.BeforeProofWrite);
            Await(runtime.StopAsync()); Check.True(factory.Process.HasExited); Check.Equal(1, factory.Process.Disposed); Check.Equal(1, environment.Closed);
            Check.True(File.Exists(Path.Combine(sandbox.Path, "source-maintenance", "vm-jobs", Job, "scratch.raw"))); Await(session.DisposeAsync().AsTask()); Await(runtime.DisposeAsync().AsTask());
        });
        suite.Add("hardware VM controlled: stale proof, guest identity, selftest and unsolicited egress fail closed", () =>
        {
            var fixture = JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("hardware-vm-protocol.json")))!;
            foreach (var node in fixture["invalid"]!.AsArray())
            {
                var mode = node!.GetValue<string>(); using var sandbox = new Sandbox(); var environment = new FakeEnvironment(sandbox.Path); var factory = new FakeFactory(mode); var runtime = new HardwareVmRuntime(environment, factory);
                try
                {
                    if (mode is "egress-frame" or "result-id" or "guest-error" or "log-budget")
                    { var session = Await(runtime.StartAsync(Job, Machine, Time)); Check.Throws<InvalidDataException>(() => Await(session.RequestAsync(HardwareGuestOperation.InspectPackage, JsonSerializer.SerializeToElement(new { opaque = "private" })))); Await(session.DisposeAsync().AsTask()); }
                    else Check.Throws<InvalidDataException>(() => Await(runtime.StartAsync(Job, Machine, Time)));
                    Check.True(factory.Process!.HasExited); Check.Equal(1, factory.Process.Disposed); Check.Equal(1, environment.Closed); Check.True(!factory.Process.BeforeProofWrite);
                    if (mode == "native-nonce") Check.Equal(0L, factory.Process.HostInput.WrittenBytes);
                }
                catch (Exception error) { throw new InvalidOperationException("Controlled hardware protocol case " + mode, error); }
                finally { factory.Process?.ExitRelease.TrySetResult(); Await(runtime.DisposeAsync().AsTask()); }
            }
        });
        suite.Add("hardware VM controlled: absent native proof reaches owned timeout without serial writes", () =>
        {
            using var sandbox = new Sandbox(); var environment = new FakeEnvironment(sandbox.Path); var factory = new FakeFactory("no-proof"); var runtime = new HardwareVmRuntime(environment, factory);
            Check.Throws<TimeoutException>(() => Await(runtime.StartAsync(Job, Machine, Time with { StartupTimeout = TimeSpan.FromMilliseconds(100) })));
            Check.True(factory.Process!.HasExited); Check.Equal(0L, factory.Process.HostInput.WrittenBytes); Check.Equal(1, environment.Closed); Await(runtime.DisposeAsync().AsTask());
        });
        suite.Add("hardware VM controlled: caller cancellation retains admission until actual process exit", () =>
        {
            using var sandbox = new Sandbox(); var environment = new FakeEnvironment(sandbox.Path); var factory = new FakeFactory("block-operation"); var runtime = new HardwareVmRuntime(environment, factory);
            var session = Await(runtime.StartAsync(Job, Machine, Time)); factory.Process!.HoldExit = true; using var cancel = new CancellationTokenSource();
            var request = session.RequestAsync(HardwareGuestOperation.InspectPackage, JsonSerializer.SerializeToElement(new { opaque = "private" }), cancel.Token);
            Await(factory.Process.OperationSeen.Task); cancel.Cancel(); Await(factory.Process.Killed.Task); var stop = runtime.StopAsync();
            Check.True(!stop.IsCompleted && !request.IsCompleted && environment.Closed == 0);
            factory.Process.ExitRelease.TrySetResult(); Check.Throws<OperationCanceledException>(() => Await(request)); Await(stop);
            Check.Equal(1, environment.Closed); Await(session.DisposeAsync().AsTask()); Await(runtime.DisposeAsync().AsTask());
        });
        suite.Add("hardware VM controlled: stop joins physical preparation and process startup races", () =>
        {
            using var sandbox = new Sandbox(); var environment = new FakeEnvironment(sandbox.Path) { PrepareEntered = Signal(), PrepareRelease = Signal() }; var factory = new FakeFactory(); var runtime = new HardwareVmRuntime(environment, factory);
            var starting = runtime.StartAsync(Job, Machine, Time); Await(environment.PrepareEntered.Task); var stopping = runtime.StopAsync(); Check.True(!stopping.IsCompleted);
            environment.PrepareRelease.TrySetResult(); Check.Throws<OperationCanceledException>(() => Await(starting)); Await(stopping); Check.Equal(0, factory.Starts); Check.Equal(1, environment.Closed); Await(runtime.DisposeAsync().AsTask());
            using var second = new Sandbox(); environment = new FakeEnvironment(second.Path); factory = new FakeFactory { StartEntered = Signal(), StartRelease = Signal() }; runtime = new HardwareVmRuntime(environment, factory);
            starting = runtime.StartAsync(Job, Machine, Time); Await(factory.StartEntered.Task); stopping = runtime.StopAsync(); Check.True(!stopping.IsCompleted);
            factory.StartRelease.TrySetResult(); Check.Throws<OperationCanceledException>(() => Await(starting)); Await(stopping); Check.True(factory.Process!.HasExited); Check.Equal(0L, factory.Process.HostInput.WrittenBytes); Check.Equal(1, environment.Closed); Await(runtime.DisposeAsync().AsTask());
        });
        suite.Add("hardware VM controlled: cancellation joins a blocked physical pipe write", () =>
        {
            using var sandbox = new Sandbox(); var environment = new FakeEnvironment(sandbox.Path); var factory = new FakeFactory(); var runtime = new HardwareVmRuntime(environment, factory);
            var session = Await(runtime.StartAsync(Job, Machine, Time)); var entered = Signal(); var release = Signal();
            factory.Process!.HostInput.PhysicalWrite = async bytes => { if (bytes.Length > 4) { entered.TrySetResult(); await release.Task.ConfigureAwait(false); } };
            using var cancel = new CancellationTokenSource(); var request = session.RequestAsync(HardwareGuestOperation.InspectPackage, JsonSerializer.SerializeToElement(new { opaque = "blocked-private" }), cancel.Token);
            Await(entered.Task); cancel.Cancel(); Await(factory.Process.Killed.Task); var stopping = runtime.StopAsync(); Check.True(!request.IsCompleted && !stopping.IsCompleted && environment.Closed == 0);
            release.TrySetResult(); Check.Throws<OperationCanceledException>(() => Await(request)); Await(stopping); Check.Equal(1, environment.Closed); Await(session.DisposeAsync().AsTask()); Await(runtime.DisposeAsync().AsTask());
        });
        suite.Add("hardware VM controlled: request deadline kills the peer; oversized private objects are never sent", () =>
        {
            using var sandbox = new Sandbox(); var environment = new FakeEnvironment(sandbox.Path); var factory = new FakeFactory("block-operation"); var runtime = new HardwareVmRuntime(environment, factory);
            var session = Await(runtime.StartAsync(Job, Machine, Time with { RequestTimeout = TimeSpan.FromMilliseconds(100) }));
            Check.Throws<TimeoutException>(() => Await(session.RequestAsync(HardwareGuestOperation.InspectPackage, JsonSerializer.SerializeToElement(new { opaque = "deadline" }))));
            Check.True(factory.Process!.HasExited); Check.Equal(1, environment.Closed); Await(session.DisposeAsync().AsTask()); Await(runtime.DisposeAsync().AsTask());
            using var second = new Sandbox(); environment = new FakeEnvironment(second.Path); factory = new FakeFactory(); runtime = new HardwareVmRuntime(environment, factory);
            session = Await(runtime.StartAsync(Job, Machine, Time)); var before = factory.Process!.HostInput.WrittenBytes;
            Check.Throws<InvalidDataException>(() => Await(session.RequestAsync(HardwareGuestOperation.InspectPackage, JsonSerializer.SerializeToElement(new { opaque = new string('x', 65536) }))));
            Check.Equal(before, factory.Process.HostInput.WrittenBytes); Check.True(factory.Process.HasExited); Await(session.DisposeAsync().AsTask()); Await(runtime.DisposeAsync().AsTask());
        });
        suite.Add("hardware VM controlled: failed startup closes timer and cancellation owners before stop returns", () =>
        {
            using var sandbox = new Sandbox(); var environment = new FakeEnvironment(sandbox.Path); var factory = new FakeFactory("hello-image"); HardwareVmSession? failed = null;
            var runtime = new HardwareVmRuntime(environment, factory, session => failed = session);
            Check.Throws<InvalidDataException>(() => Await(runtime.StartAsync(Job, Machine, Time)));
            Check.True(failed is not null && factory.Process!.HasExited && environment.Closed == 1);
            var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var lifetime = (CancellationTokenSource)typeof(HardwareVmSession).GetField("_lifetime", fields)!.GetValue(failed)!;
            var operations = typeof(HardwareVmSession).GetField("_operations", fields)!.GetValue(failed)!;
            var operationSource = (CancellationTokenSource)operations.GetType().GetField("_stop", fields)!.GetValue(operations)!;
            Check.Throws<ObjectDisposedException>(() => { _ = lifetime.Token; }); Check.Throws<ObjectDisposedException>(() => { _ = operationSource.Token; });
            Await(Task.WhenAll(failed!.StopAsync(), failed.DisposeAsync().AsTask(), failed.DisposeAsync().AsTask()));
            Check.Equal(1, factory.Process!.Disposed); Check.Equal(1, environment.Closed); Await(runtime.DisposeAsync().AsTask());
        });
    }
}
