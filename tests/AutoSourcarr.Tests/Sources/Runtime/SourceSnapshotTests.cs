using System.Reflection;
using System.Security.Cryptography;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Sources.Runtime;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Tests;

public static class SourceSnapshotTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("snapshot receiver: physical file bytes match native hash and cleanup belongs to native lease", () => Bytes().GetAwaiter().GetResult());
        suite.Add("snapshot receiver: empty retained UA at generation0 and missing target remain distinct", () => UserAgentAndMissing().GetAwaiter().GetResult());
        suite.Add("snapshot receiver: output masks identity account mode link size and time bounds fail closed", () => Metadata().GetAwaiter().GetResult());
        suite.Add("snapshot receiver: changed exchange root refuses reading but still releases ticket", () => RootChanged().GetAwaiter().GetResult());
        suite.Add("snapshot receiver: full hash and named replacement are checked independently", () => ChangedBytesAndName().GetAwaiter().GetResult());
        suite.Add("snapshot receiver: read and stream-close failures retain native cleanup", () => ReadAndCloseFailures().GetAwaiter().GetResult());
        suite.Add("snapshot receiver: full I/O scheduler refuses locally without abandoning unadmitted ticket", () => SchedulerRefusal().GetAwaiter().GetResult());
        suite.Add("snapshot receiver: cancellation after native admission waits completion and releases before file read", () => CancelBeforeFile().GetAwaiter().GetResult());
        suite.Add("snapshot receiver: pause drains physical read and native cleanup before source Stop", () => PauseDrain().GetAwaiter().GetResult());
        suite.Add("snapshot receiver: cancelled late physical failure is retained while native Stop still executes", () => LateFailure().GetAwaiter().GetResult());
        suite.Add("snapshot receiver: failed release zeroes unreturned bytes and next explicit request reconciles one lease", () => CleanupFailure().GetAwaiter().GetResult());
        suite.Add("snapshot receiver: shutdown drains and closes files before retiring native channel", () => ShutdownDrain().GetAwaiter().GetResult());
        suite.Add("snapshot receiver: stopped admission and simultaneous close failures retain all errors", () => StopFailures().GetAwaiter().GetResult());
        suite.Add("snapshot receiver: one active transaction defers another without restarting healthy source", () => Admission().GetAwaiter().GetResult());
        suite.Add("snapshot receiver: production root factory requires actual connection and accepts no caller path", FactoryBoundary);
        suite.Add("snapshot inventory: actual client metadata and two native read-release transactions retain exact bytes", () => Inventory().GetAwaiter().GetResult());
        suite.Add("snapshot inventory: changed generation cannot publish stale installed evidence", () => InventoryGeneration().GetAwaiter().GetResult());
        suite.Add("snapshot inventory: retired HTTP epoch waits physical read and native release then rejects result", () => InventoryEpoch().GetAwaiter().GetResult());
    }
    private static async Task Bytes()
    {
        await using var f = await Fixture.Create();
        var bytes = await f.Reader.ReadInstalledJarAsync("test.jar", 1, default);
        Check.True(bytes.SequenceEqual(Files.Pattern())); Check.Equal(2, f.Files.Opens); Check.Equal(1, f.Releases);
        Check.True(File.Exists(f.Files.Path)); // Main never unlinks the synthetic ticket.
        _ = await f.Harness.StartAsync([]);
    }
    private static async Task UserAgentAndMissing()
    {
        await using (var f = await Fixture.Create(start: false))
        {
            File.WriteAllBytes(f.Files.Path, []);
            Check.Equal(0, (await f.Reader.ReadRetainedUserAgentAsync(0))!.Length); Check.Equal(1, f.Releases);
        }
        await using (var f = await Fixture.Create())
        {
            f.Peer.SnapshotTemplate = "missing-event";
            Check.True(await Failure(f.Reader.ReadInstalledJarAsync("test.jar", 1, default)) is FileNotFoundException);
            Check.Equal(0, f.Files.Opens); Check.Equal(1, f.Releases);
        }
    }
    private static async Task Metadata()
    {
        for (var kind = 0; kind < 12; kind++)
        {
            await using var f = await Fixture.Create(); var selected = kind;
            f.Files.Alter = status => selected switch
            {
                0 => status with { Mask = status.Mask & ~2U },
                1 => status with { Fact = status.Fact with { Identity = new(5, 106) } },
                2 => status with { Fact = status.Fact with { Uid = 999 } },
                3 => status with { Fact = status.Fact with { Gid = 568 } },
                4 => status with { Fact = status.Fact with { Mode = 0x8180 } },
                5 => status with { Fact = status.Fact with { Mode = 0x4124 } },
                6 => status with { Fact = status.Fact with { Links = 2 } },
                7 => status with { Fact = status.Fact with { Links = 0 } },
                8 => status with { Fact = status.Fact with { Size = status.Fact.Size + 1 } },
                9 => status with { Fact = status.Fact with { Size = 33554433 } },
                10 => status with { Fact = status.Fact with { ModifiedNanoseconds = 1000000000 } },
                _ => status with { Fact = status.Fact with { ChangedNanoseconds = 1000000000 } }
            };
            Check.True(await Failure(f.Reader.ReadInstalledJarAsync("test.jar", 1, default)) is IOException); Check.Equal(1, f.Releases);
        }
    }
    private static async Task RootChanged()
    {
        await using var f = await Fixture.Create(); f.Files.Exchange = new(6, 102);
        Check.True(await Failure(f.Reader.ReadInstalledJarAsync("test.jar", 1, default)) is IOException);
        Check.Equal(0, f.Files.Opens); Check.Equal(1, f.Releases);
    }
    private static async Task ChangedBytesAndName()
    {
        foreach (var name in new[] { "hash", "name", "metadata" })
        {
            await using var f = await Fixture.Create();
            if (name == "hash") { var bytes = Files.Pattern(); bytes[0] ^= 1; File.WriteAllBytes(f.Files.Path, bytes); }
            else f.Files.AfterRead = () => { if (name == "name") f.Files.NamedIdentity = new(5, 106); else f.Files.Modified++; };
            var error = await Failure(f.Reader.ReadInstalledJarAsync("test.jar", 1, default));
            Check.True(error is IOException or InvalidDataException); Check.Equal(1, f.Releases);
        }
    }
    private static async Task ReadAndCloseFailures()
    {
        for (var kind = 0; kind < 2; kind++)
        {
            await using var f = await Fixture.Create(); f.Files.ThrowRead = kind == 0; f.Files.ThrowClose = kind == 1;
            Check.True(await Failure(f.Reader.ReadInstalledJarAsync("test.jar", 1, default)) is IOException); Check.Equal(1, f.Releases);
        }
    }
    private static async Task SchedulerRefusal()
    {
        await using var f = await Fixture.Create(); using var entered = new ManualResetEventSlim(); using var gate = new ManualResetEventSlim();
        var busy = f.Io.RunAsync(() => { entered.Set(); if (!gate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return 1; });
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Check.True(await Failure(f.Reader.ReadInstalledJarAsync("test.jar", 1, default)) is SourceWorkDeferredException);
            Check.Equal(0, f.Files.Opens); Check.Equal(1, f.Releases); Check.True(!f.Harness.RetirementStarted.IsCompleted);
        }
        finally { gate.Set(); _ = await busy; }
    }
    private static async Task CancelBeforeFile()
    {
        await using var f = await Fixture.Create(); using var cancel = new CancellationTokenSource(); f.Peer.HoldObservation = true;
        var read = f.Reader.ReadInstalledJarAsync("test.jar", 1, cancel.Token); await f.Peer.ObservationSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel(); Check.True(!read.IsCompleted); f.Peer.ReleaseObservation();
        Check.True(await Failure(read) is OperationCanceledException); Check.Equal(0, f.Files.Opens); Check.Equal(1, f.Releases);
    }
    private static async Task PauseDrain()
    {
        await using var f = await Fixture.Create(); f.Files.Block = true;
        var read = f.Reader.ReadInstalledJarAsync("test.jar", 1, default); Check.True(f.Files.Entered.Wait(TimeSpan.FromSeconds(3)));
        var stop = f.Reader.WithPausedAsync(f.Harness.StopAsync);
        Check.True(!read.IsCompleted && !stop.IsCompleted); Check.True(!f.Peer.Commands.Any(c => c.Opcode == NativeSourceOwnerOpcode.Stop));
        f.Files.Gate.Set(); _ = await stop.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(await Failure(read) is OperationCanceledException);
        Check.Equal(1, f.Releases); var commands = f.Peer.Commands.Select(c => c.Opcode).ToArray();
        Check.True(Array.IndexOf(commands, NativeSourceOwnerOpcode.ReleaseSnapshot) < Array.IndexOf(commands, NativeSourceOwnerOpcode.Stop));
    }
    private static async Task LateFailure()
    {
        await using var f = await Fixture.Create(); f.Files.Block = true; f.Files.ThrowRead = true;
        var read = f.Reader.ReadInstalledJarAsync("test.jar", 1, default); Check.True(f.Files.Entered.Wait(TimeSpan.FromSeconds(3)));
        var stop = f.Reader.WithPausedAsync(f.Harness.StopAsync); f.Files.Gate.Set();
        var readError = await Failure(read); var stopError = await Failure(stop);
        Check.True(readError is not null && Tree(readError).Any(e => e is IOException));
        Check.True(stopError is not null && Tree(stopError).Any(e => e is IOException));
        Check.True(f.Peer.Commands.Any(c => c.Opcode == NativeSourceOwnerOpcode.Stop)); Check.Equal(1, f.Releases);
    }
    private static async Task CleanupFailure()
    {
        await using var f = await Fixture.Create(); f.Peer.ReleaseResult = NativeSourceOwnerResult.Failed;
        Check.True(await Failure(f.Reader.ReadInstalledJarAsync("test.jar", 1, default)) is IOException);
        Check.True(f.Files.LastBuffer is { } first && first.All(value => value == 0)); Check.Equal(1, f.Releases);
        f.Peer.ReleaseResult = NativeSourceOwnerResult.Ok;
        Check.True((await f.Reader.ReadInstalledJarAsync("test.jar", 1, default)).SequenceEqual(Files.Pattern()));
        Check.Equal(3, f.Releases); Check.Equal(2, f.Peer.Commands.Count(c => c.Opcode == NativeSourceOwnerOpcode.Observe));
    }
    private static async Task ShutdownDrain()
    {
        await using var f = await Fixture.Create(); f.Files.Block = true;
        var read = f.Reader.ReadInstalledJarAsync("test.jar", 1, default); Check.True(f.Files.Entered.Wait(TimeSpan.FromSeconds(3)));
        var shutdown = f.Reader.StopBeforeAsync(f.Harness.ShutdownAsync);
        Check.True(!shutdown.IsCompleted && f.Files.Disposed == 0); Check.True(!f.Peer.Commands.Any(c => c.Opcode == NativeSourceOwnerOpcode.Shutdown));
        f.Files.Gate.Set(); _ = await shutdown.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(await Failure(read) is OperationCanceledException);
        Check.Equal(1, f.Files.Disposed); Check.True(f.Harness.OwnerExited.IsCompletedSuccessfully);
    }
    private static async Task StopFailures()
    {
        await using var f = await Fixture.Create(); f.Files.ThrowDispose = true;
        var error = await Failure(f.Reader.StopBeforeAsync<int>(() => Task.FromException<int>(new InvalidOperationException("native-stop-sentinel"))));
        Check.True(error is AggregateException && Tree(error).Any(e => e.Message == "file-close-sentinel") && Tree(error).Any(e => e.Message == "native-stop-sentinel"));
        Check.True(await Failure(f.Reader.ReadInstalledJarAsync("test.jar", 1, default)) is OperationCanceledException);
    }
    private static async Task Admission()
    {
        await using var f = await Fixture.Create(); f.Peer.HoldObservation = true;
        var first = f.Reader.ReadInstalledJarAsync("test.jar", 1, default); await f.Peer.ObservationSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check.True(await Failure(f.Reader.ReadInstalledJarAsync("test.jar", 1, default)) is SourceWorkDeferredException);
        Check.True(!f.Harness.RetirementStarted.IsCompleted); f.Peer.ReleaseObservation(); _ = await first;
    }
    private static void FactoryBoundary()
    {
        var factory = typeof(NativeSourceSnapshots).GetMethod("CreateAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check.Equal(typeof(NativeSourceOwnerConnection), factory.GetParameters()[0].ParameterType);
        Check.True(factory.GetParameters().All(p => p.ParameterType != typeof(string)));
        Check.True(typeof(NativeSourceSnapshots).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).All(c => c.IsPrivate));
        Check.Equal(typeof(NativeSourceOwnerConnection), typeof(SourceSnapshotFiles).GetMethod("OpenFixed", BindingFlags.Static | BindingFlags.NonPublic)!.GetParameters()[0].ParameterType);
    }
    private static async Task Inventory()
    {
        await using var f = await Fixture.Create(); using var setup = new SourceInventoryTests.Setup(binaryReader: f.Reader);
        var capture = await setup.Installed.CaptureAsync(SourceInventoryTests.Package, SourceInventoryTests.Version);
        Check.True(capture is not null); Check.True(capture!.Contents().SequenceEqual(Files.Pattern()));
        Check.Equal(Convert.ToHexStringLower(SHA256.HashData(Files.Pattern())), capture.Source.BinarySha256);
        Check.Equal(2, f.Releases); Check.Equal(2, f.Peer.Commands.Count(c => c.Opcode == NativeSourceOwnerOpcode.Observe));
        Check.Equal(2, setup.Http.Calls); Check.Equal("fixture.jar", capture.Filename); setup.Installed.RequireCurrent(capture);
    }
    private static async Task InventoryGeneration()
    {
        await using var f = await Fixture.Create(); using var setup = new SourceInventoryTests.Setup(binaryReader: f.Reader);
        setup.Http.Before = call => { if (call == 2) setup.Generation = 2; };
        Check.True(await Failure(setup.Installed.CaptureAsync(SourceInventoryTests.Package, SourceInventoryTests.Version)) is SourceRawConflictException);
        Check.Equal(1, f.Releases); Check.True(!f.Harness.RetirementStarted.IsCompleted);
    }
    private static async Task InventoryEpoch()
    {
        await using var f = await Fixture.Create(); using var setup = new SourceInventoryTests.Setup(binaryReader: f.Reader, restartable: true); f.Files.Block = true;
        var capture = setup.Installed.CaptureAsync(SourceInventoryTests.Package, SourceInventoryTests.Version);
        Check.True(f.Files.Entered.Wait(TimeSpan.FromSeconds(3))); _ = setup.Client.BeginTransportPause(); Check.True(!capture.IsCompleted);
        f.Files.Gate.Set(); Check.True(await Failure(capture) is OperationCanceledException); Check.Equal(1, f.Releases);
    }
    private static async Task<Exception?> Failure(Task task) { try { await task; return null; } catch (Exception error) { return error; } }
    private static IEnumerable<Exception> Tree(Exception error)
    { yield return error; if (error is AggregateException aggregate) foreach (var child in aggregate.InnerExceptions) foreach (var item in Tree(child)) yield return item; else if (error.InnerException is { } inner) foreach (var item in Tree(inner)) yield return item; }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly NativeSourceOwnerConnectionTests.Peer Peer = new(NativeSourceOwnerConnectionTests.SnapshotBoot());
        internal readonly BoundedWorkScheduler Io = new(1, 0);
        internal NativeSourceOwnerConnection.ControlledHarness Harness = null!;
        internal NativeSourceSnapshots Reader = null!;
        internal Files Files = null!;
        internal int Releases => Peer.Commands.Count(c => c.Opcode == NativeSourceOwnerOpcode.ReleaseSnapshot);
        internal static async Task<Fixture> Create(bool start = true)
        {
            var f = new Fixture(); f.Harness = f.Peer.Harness(); await f.Harness.Acceptance;
            if (start) _ = await f.Harness.StartAsync([]);
            f.Files = new(f.Peer.SnapshotOwner); f.Reader = NativeSourceSnapshots.Controlled(f.Harness, f.Peer.SnapshotOwner, f.Files, f.Io); return f;
        }
        public async ValueTask DisposeAsync()
        {
            Files.Gate.Set(); Peer.ReleaseResult = NativeSourceOwnerResult.Ok; Peer.ReleaseEverything();
            _ = await Failure(Reader.StopBeforeAsync(Harness.ShutdownAsync)); _ = await Failure(Harness.DisposeAsync().AsTask());
            await Io.DisposeAsync(); Files.Cleanup(); Peer.Dispose();
        }
    }
    private sealed class Files : SourceSnapshotFiles
    {
        private readonly Sandbox _sandbox = new(); private readonly NativeSourceOwnerProofMessage _owner;
        internal string Path { get; }
        internal NativeSourceOwnerRoot Exchange = new(5, 102), NamedIdentity = new(5, 105);
        internal int Opens, Disposed; internal long Modified = 100;
        internal bool Block, ThrowRead, ThrowClose, ThrowDispose;
        internal Action? AfterRead; internal Func<FileStatus, FileStatus>? Alter;
        internal byte[]? LastBuffer;
        internal readonly ManualResetEventSlim Entered = new(), Gate = new();
        internal Files(NativeSourceOwnerProofMessage owner) { _owner = owner; Path = System.IO.Path.Combine(_sandbox.Path, "synthetic-ticket"); File.WriteAllBytes(Path, Pattern()); }
        internal static byte[] Pattern() => Enumerable.Range(0, 16390).Select(i => unchecked((byte)(i * 17 + 3))).ToArray();
        internal override NativeSourceOwnerRoot Identity => Exchange;
        internal override void RequireCurrent() { if (Disposed != 0) throw new ObjectDisposedException(nameof(Files)); }
        internal override Input OpenTicket(string ticket)
        {
            NativeSourceObservationProtocol.RequireTicket(ticket); Opens++; var identity = NamedIdentity;
            var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var wrapped = new FileRead(stream, this);
            return new(wrapped, () =>
            {
                var status = new FileStatus(RequiredFileStats, new(identity, (ulong)stream.Length, 1, 0x8124, _owner.Uid, _owner.Gid, Modified, 101, 200, 201));
                return Alter?.Invoke(status) ?? status;
            });
        }
        public override void Dispose() { Disposed++; if (ThrowDispose) throw new IOException("file-close-sentinel"); }
        internal void Cleanup() { Gate.Set(); Entered.Dispose(); Gate.Dispose(); _sandbox.Dispose(); }
        private sealed class FileRead(FileStream inner, Files files) : Stream
        {
            private bool _first;
            public override int Read(byte[] buffer, int offset, int count)
            {
                files.LastBuffer = buffer;
                if (!_first) { _first = true; files.Entered.Set(); if (files.Block && !files.Gate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Physical file gate not released"); }
                if (files.ThrowRead) throw new IOException("physical-read-sentinel");
                var result = inner.Read(buffer, offset, count); Interlocked.Exchange(ref files.AfterRead, null)?.Invoke(); return result;
            }
            public override int ReadByte() => inner.ReadByte();
            protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); if (disposing && files.ThrowClose) throw new IOException("physical-close-sentinel"); base.Dispose(disposing); }
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
