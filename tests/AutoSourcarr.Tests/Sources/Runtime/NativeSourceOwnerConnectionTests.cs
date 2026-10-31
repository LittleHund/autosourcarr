using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources.Runtime;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Diagnostics;
using System.Text;

namespace AutoSourcarr.Tests;

public static class NativeSourceOwnerConnectionTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("native connection: six actual C boot vectors preserve raw startup and resolved identity separately", BootVectors);
        suite.Add("native connection: four actual C reap vectors retain every signed wait status", ReapVectors);
        suite.Add("native connection: auxiliary messages reject malformed quotas, identities, profiles and trailing data", InvalidMessages);
        suite.Add("native connection: external handoff touches only fixed6 and joins its actor", () => External().GetAwaiter().GetResult());
        suite.Add("native connection: fragmented boot/proof and mismatched current request retain exact ownership", () => BootOwnership().GetAwaiter().GetResult());
        suite.Add("native connection: wrong held pipe, actual proof and boot trailing data never admit commands", () => InvalidHandoff().GetAwaiter().GetResult());
        suite.Add("native connection: startup cancellation before and after binding joins physical cleanup", () => StartupCancellation().GetAwaiter().GetResult());
        suite.Add("native connection: cancelled or failed actual proof retains staged response/reap retirement", () => ProofRetirement().GetAwaiter().GetResult());
        suite.Add("native connection: acceptance checks decidable requested and inherited identities without source-root reads", () => IdentitySelection().GetAwaiter().GetResult());
        suite.Add("native connection: real actor preserves unsigned generation and bounded ordinary admission", () => Commands().GetAwaiter().GetResult());
        suite.Add("native connection: cancellation before admission consumes no native request ID", () => CancelBefore().GetAwaiter().GetResult());
        suite.Add("native connection: committed partial START finishes its frame then joins reserved STOP", () => CancelPartial().GetAwaiter().GetResult());
        suite.Add("native connection: cancellation at reply publication cannot discard a live START", () => CancelPublication().GetAwaiter().GetResult());
        suite.Add("native connection: coalesced STOP precedes SHUTDOWN without a native busy shortcut", () => StopShutdown().GetAwaiter().GetResult());
        suite.Add("native connection: direct terminal START cleanup uses one SHUTDOWN and real reap", () => ShutdownStart().GetAwaiter().GetResult());
        suite.Add("native connection: actual-reap message alone cannot finish terminal response IO", () => ReapBeforeEof().GetAwaiter().GetResult());
        suite.Add("native connection: missing reap expires unresolved with retained response and joined local handles", () => MissingReap().GetAwaiter().GetResult());
        suite.Add("native connection: protocol and physical close failures survive repeated terminal disposal", () => ProtocolClose().GetAwaiter().GetResult());
        suite.Add("native connection: terminal completion waits for the actual IO thread cleanup", () => PhysicalJoin().GetAwaiter().GetResult());
        suite.Add("native connection: reserved stop supersedes an admitted but uncommitted start", () => StopBeforeStartCommit().GetAwaiter().GetResult());
        suite.Add("native connection: production factory is fixed/internal and controlled harness cannot mint a connection", FactoryBoundary);
        suite.Add("native connection: fragmented source logs retain real redaction and never become control replies", () => SourceLogs().GetAwaiter().GetResult());
        suite.Add("native connection: log flood never waits for redaction while reservedSTOP and physical shutdown progress", () => SourceLogPressure().GetAwaiter().GetResult());
        suite.Add("native connection: corrupt and truncated source log frames retain fatal cleanup", () => InvalidSourceLogs().GetAwaiter().GetResult());
        suite.Add("native snapshots actor: one issued lease permits status and blocks generation changes until release", () => SnapshotOwnership().GetAwaiter().GetResult());
        suite.Add("native snapshots actor: cancellation before admission consumes no request or ticket", () => SnapshotCancelBefore().GetAwaiter().GetResult());
        suite.Add("native snapshots actor: admitted cancellation still obtains and releases actual completed ticket", () => SnapshotCancelAfter().GetAwaiter().GetResult());
        suite.Add("native snapshots actor: long owned capture never retires healthy owner or blocks status lane", () => SnapshotBudgetAndStatus().GetAwaiter().GetResult());
        suite.Add("native snapshots actor: malformed or misordered completion retains fatal physical cleanup", () => SnapshotInvalidEvents().GetAwaiter().GetResult());
        suite.Add("native snapshots actor: cleanup owns a reserved request while ordinary status remains usable", () => SnapshotReleasePending().GetAwaiter().GetResult());
        suite.Add("native snapshots actor: failed cleanup retains lease and explicit reconciliation observes later success", () => SnapshotReconcile().GetAwaiter().GetResult());
        suite.Add("native snapshots actor: shutdown waits actual capture and cleanup replies before native retirement", () => SnapshotShutdown().GetAwaiter().GetResult());
        suite.Add("native snapshots actor: uncommitted observation is cancelled without native admission on shutdown", () => SnapshotUncommitted().GetAwaiter().GetResult());
        suite.Add("native snapshots actor: valid failed observations remain local and still release their issued ticket", () => SnapshotFailedResults().GetAwaiter().GetResult());
    }
    private static JsonObject Fixture()
    {
        var bytes = File.ReadAllBytes(ConfigurationTests.Fixture("native-bootstrap-binding.json"));
        Check.Equal("98a52a07dda79f7122e40812952f6575c1b13504cf7f760a6c7836253bb960e7", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return JsonNode.Parse(bytes)!.AsObject();
    }
    private static byte[] Boot(string name = "integrated") => Convert.FromHexString(Fixture()["boots"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == name)!["hex"]!.GetValue<string>());
    internal static byte[] SnapshotBoot() => Boot();
    private static void BootVectors()
    {
        var count = 0;
        foreach (var row in Fixture()["boots"]!.AsArray())
        {
            var frame = Convert.FromHexString(row!["hex"]!.GetValue<string>()); var message = NativeSourceOwnerConnection.ParseBootMessage(frame);
            Check.Equal(42U, message.MainPid); Check.Equal(5, message.Pipes.Count);
            if (message.StartupRequest.Mode == "external")
            {
                Check.True(message.ExpectedOwner is null);
                if (row["name"]!.GetValue<string>() == "external-unused-large-requested-ids")
                {
                    Check.Equal("-9007199254740993123456789", message.StartupRequest.Uid.ToString());
                    Check.Equal("9007199254740993123456789", message.StartupRequest.Gid.ToString());
                }
                Check.Equal(1, message.Pipes.Count(pipe => pipe.Inode != 0));
            }
            else
            {
                Check.Equal("integrated", message.StartupRequest.Mode); Check.Equal(37U, message.ExpectedOwner!.OwnerPid);
                var resolved = row["name"]!.GetValue<string>() == "legacy-main-caller" ? 568U : 1000U;
                Check.Equal(resolved, message.ExpectedOwner.Uid); Check.Equal(resolved, message.ExpectedOwner.Gid);
                Check.Equal(0x1122334455667788UL, message.ExpectedOwner.Root.Device); Check.Equal(0x8877665544332211UL, message.ExpectedOwner.Root.Inode);
            }
            if (row["name"]!.GetValue<string>() == "maximum-startup-frame") Check.Equal(16384, message.StartupRequest.Encode(message.Nonce).Length);
            if (row["name"]!.GetValue<string>() == "actual-hardware-group-zero") { Check.Equal(1U, message.GroupCount); Check.Equal(0U, message.KvmGid); }
            if (row["name"]!.GetValue<string>() == "no-hardware-group") Check.Equal(0U, message.GroupCount);
            if (row["name"]!.GetValue<string>() == "legacy-main-caller") Check.Equal(568U, message.OriginalUid);
            count++;
        }
        Check.Equal(6, count);
    }
    private static void ReapVectors()
    {
        var boot = NativeSourceOwnerConnection.ParseBootMessage(Boot()); var count = 0;
        foreach (var row in Fixture()["reaps"]!.AsArray())
        {
            var observed = NativeSourceOwnerConnection.ParseReapMessage(Convert.FromHexString(row!["hex"]!.GetValue<string>()), boot.Nonce, 37);
            Check.Equal(37U, observed.OwnerPid); Check.Equal(row["status"]!.GetValue<int>(), observed.WaitStatus); count++;
        }
        Check.Equal(4, count);
    }
    private static void InvalidMessages()
    {
        foreach (var (offset, value) in new (int, uint)[] { (8, 2), (12, 1), (16, 2), (20, uint.MaxValue), (24, uint.MaxValue), (28, 1), (96, 0), (96, 16385), (100, 75), (104, 2), (108, uint.MaxValue) })
        { var frame = Boot(); BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), value); Check.Throws<InvalidDataException>(() => NativeSourceOwnerConnection.ParseBootMessage(frame)); }
        var baseline = Boot(); var proof = 192 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(baseline.AsSpan(96)));
        foreach (var (offset, value) in new (int, uint)[] { (40, 1), (40, 42), (44, 2), (48, 0), (52, 0), (72, 1) })
        { var frame = Boot(); BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(proof + offset), value); Check.Throws<InvalidDataException>(() => NativeSourceOwnerConnection.ParseBootMessage(frame)); }
        foreach (var frame in new[] { baseline[..^1], baseline.Concat(new byte[] { 0 }).ToArray(), Boot("external-unused-large-requested-ids").Concat(new byte[76]).ToArray() })
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerConnection.ParseBootMessage(frame));
        var duplicate = Boot(); duplicate.AsSpan(112, 16).CopyTo(duplicate.AsSpan(128, 16)); Check.Throws<InvalidDataException>(() => NativeSourceOwnerConnection.ParseBootMessage(duplicate));
        var group = Boot("no-hardware-group"); BinaryPrimitives.WriteUInt32LittleEndian(group.AsSpan(108), 1); Check.Throws<InvalidDataException>(() => NativeSourceOwnerConnection.ParseBootMessage(group));
        var nonce = NativeSourceOwnerConnection.ParseBootMessage(baseline).Nonce;
        var reap = Convert.FromHexString(Fixture()["reaps"]![0]!["hex"]!.GetValue<string>()); reap[8] ^= 1;
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerConnection.ParseReapMessage(reap, nonce, 37));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerConnection.ParseReapMessage(new byte[47], nonce, 37));
    }
    private static async Task External()
    {
        using var peer = new Peer(Boot("external-unused-large-requested-ids")); var harness = peer.Harness();
        try
        {
            await harness.Acceptance.WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(peer.Inspected.SequenceEqual(new[] { 6 })); Check.True(peer.Closed.Keys.SequenceEqual(new[] { 6 })); Check.Equal(1, peer.Closed[6]); Check.Equal(0, peer.Commands.Count);
        }
        finally { await Cleanup(harness, peer); }
    }
    private static async Task BootOwnership()
    {
        using (var peer = new Peer(Boot()) { ReadChunk = 3 })
        {
            var harness = peer.Harness();
            try { await harness.Acceptance.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(peer.Inspected.Order().SequenceEqual(new[] { 3, 4, 5, 6, 7 })); }
            finally { await Cleanup(harness, peer); }
            Check.True(peer.Closed.Values.All(count => count == 1));
        }
        using (var peer = new Peer(Boot()))
        {
            var harness = new NativeSourceOwnerConnection.ControlledHarness(peer, new SourceStartupRequest("external", 0, 0));
            try { Check.True(await Failure(harness.Acceptance) is ConfigurationException); Check.True(peer.Inspected.Count == 0); }
            finally { await Cleanup(harness, peer); }
            Check.True(peer.Closed.Keys.SequenceEqual(new[] { 6 }));
        }
    }
    private static async Task Commands()
    {
        using var peer = new Peer(Boot()) { Generation = ulong.MaxValue - 1, HoldStart = true }; var harness = peer.Harness();
        try
        {
            await harness.Acceptance.WaitAsync(TimeSpan.FromSeconds(3)); var start = harness.StartAsync(new IndexedLaunch()); await peer.StartSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check.Equal("KCEF_ENABLED", peer.Commands[0].Environment.Single().Key);
            Check.Throws<SourceWorkDeferredException>(() => harness.StatusAsync()); peer.ReleaseStart(); var response = await start;
            Check.Equal(ulong.MaxValue, response.Generation); Check.Equal(1UL, response.RequestId);
            var status = await harness.StatusAsync(); Check.Equal(2UL, status.RequestId); Check.Equal(ulong.MaxValue, status.Generation);
        }
        finally { await Cleanup(harness, peer); }
    }
    private static async Task InvalidHandoff()
    {
        foreach (var mode in new[] { "pipe", "proof", "trailing" })
        {
            using var peer = new Peer(Boot(), trailingBoot: mode == "trailing", proofMutation: mode == "proof" ? bytes => bytes[8] ^= 1 : null);
            if (mode == "pipe") peer.WrongSlot = 3;
            var harness = peer.Harness();
            try { var failure = await Failure(harness.Acceptance); Check.True(failure is not null && Tree(failure).Any(error => error is InvalidDataException)); Check.Equal(0, peer.Commands.Count); }
            finally { await Cleanup(harness, peer); }
            Check.True(peer.Closed.Values.All(count => count == 1));
        }
    }
    private static async Task StartupCancellation()
    {
        foreach (var late in new[] { false, true })
        {
            using var caller = new CancellationTokenSource(); using var peer = new Peer(Boot()); peer.CloseGate.Reset();
            if (late) peer.AfterProofEof = caller.Cancel; else peer.HoldReadSlot = 6;
            var harness = new NativeSourceOwnerConnection.ControlledHarness(peer, NativeSourceOwnerConnection.ParseBootMessage(Boot()).StartupRequest, caller.Token);
            try
            {
                await peer.Opened.Task.WaitAsync(TimeSpan.FromSeconds(3)); if (!late) caller.Cancel();
                Check.True(peer.CloseEntered.Wait(TimeSpan.FromSeconds(3))); Check.True(!harness.Acceptance.IsCompleted);
                peer.CloseGate.Set(); Check.True(await Failure(harness.Acceptance) is OperationCanceledException);
                Check.Equal(late ? 1 : 0, peer.Commands.Count); if (late) Check.Equal(NativeSourceOwnerOpcode.Shutdown, peer.Commands[0].Opcode);
            }
            finally { peer.CloseGate.Set(); await Cleanup(harness, peer); }
            Check.True(peer.Closed.Values.All(count => count == 1));
        }
    }
    private static async Task ProofRetirement()
    {
        foreach (var mode in new[] { "cancel", "read-failure", "eof-failure", "cancel-timeout" })
        {
            using var caller = new CancellationTokenSource(); using var peer = new Peer(Boot(), proofTrailing: mode == "eof-failure") { ReapOnCommandClose = false };
            if (mode.StartsWith("cancel", StringComparison.Ordinal)) peer.HoldReadSlot = 5;
            var readFailure = new IOException("controlled actual-proof read failure");
            if (mode == "read-failure") peer.ProofReadFailure = readFailure;
            peer.CloseGate.Reset();
            var harness = new NativeSourceOwnerConnection.ControlledHarness(peer, NativeSourceOwnerConnection.ParseBootMessage(Boot()).StartupRequest, caller.Token);
            try
            {
                if (mode.StartsWith("cancel", StringComparison.Ordinal)) { await peer.ProofBlocked.Task.WaitAsync(TimeSpan.FromSeconds(3)); caller.Cancel(); }
                await peer.CommandClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await peer.TerminalPoll.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check.True(!harness.Acceptance.IsCompleted); Check.True(!harness.OwnerExited.IsCompleted); Check.Equal(0, peer.Commands.Count);
                Check.Throws<InvalidOperationException>(() => harness.StatusAsync());
                if (mode == "cancel-timeout") peer.Advance(25000);
                else
                {
                    peer.ReleaseReap(); var exit = await harness.OwnerExited.WaitAsync(TimeSpan.FromSeconds(3)); Check.Equal(0, exit.WaitStatus);
                }
                Check.True(peer.CloseEntered.Wait(TimeSpan.FromSeconds(3))); Check.True(!harness.Acceptance.IsCompleted);
                peer.CloseGate.Set(); var failure = await Failure(harness.Acceptance);
                Check.True(failure is NativeSourceOwnerConnection.LifetimeFailure); var partial = (NativeSourceOwnerConnection.LifetimeFailure)failure!;
                if (mode.StartsWith("cancel", StringComparison.Ordinal)) Check.True(Tree(failure!).Any(error => error is OperationCanceledException));
                if (mode == "read-failure") Check.True(Tree(failure!).Any(error => ReferenceEquals(error, readFailure)));
                if (mode == "eof-failure") Check.True(Tree(failure!).Any(error => error is InvalidDataException));
                if (mode == "cancel-timeout") { Check.True(partial.ObservedExitRecord is null); Check.True(Tree(failure!).Any(error => error is TimeoutException)); }
                else Check.Equal(37U, partial.ObservedExitRecord!.OwnerPid);
                Check.True(peer.CloseFinished); Check.True(peer.Closed.Values.All(count => count == 1));
            }
            finally { peer.CloseGate.Set(); await Cleanup(harness, peer); }
        }
    }
    private static async Task IdentitySelection()
    {
        foreach (var mode in new[] { "explicit-mismatch", "explicit-negative", "explicit-wide", "inherit-mismatch", "inherit-unused-wide" })
        {
            var frame = Boot(mode.StartsWith("inherit", StringComparison.Ordinal) ? "legacy-main-caller" : "integrated");
            if (mode == "inherit-mismatch")
            {
                var at = 192 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(96)));
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(at + 48), 1000);
            }
            else
            {
                var uid = mode switch
                {
                    "explicit-mismatch" => new System.Numerics.BigInteger(1001), "explicit-negative" => new System.Numerics.BigInteger(-7),
                    "explicit-wide" => System.Numerics.BigInteger.Parse("9007199254740993123456789"),
                    _ => System.Numerics.BigInteger.Parse("-9007199254740993123456789")
                };
                frame = ReplaceRequest(frame, new SourceStartupRequest("integrated", uid, 0));
            }
            using var peer = new Peer(frame); var harness = peer.Harness();
            try
            {
                if (mode == "inherit-unused-wide") await harness.Acceptance.WaitAsync(TimeSpan.FromSeconds(3));
                else { Check.True(await Failure(harness.Acceptance) is InvalidDataException); Check.Equal(0, peer.Commands.Count); }
            }
            finally { await Cleanup(harness, peer); }
        }
    }
    private static byte[] ReplaceRequest(byte[] original, SourceStartupRequest request)
    {
        var parsed = NativeSourceOwnerConnection.ParseBootMessage(original); var raw = request.Encode(parsed.Nonce);
        var proofLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(100)));
        var output = new byte[192 + raw.Length + proofLength]; original.AsSpan(0, 192).CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(96), (uint)raw.Length); raw.CopyTo(output, 192);
        original.AsSpan(original.Length - proofLength, proofLength).CopyTo(output.AsSpan(192 + raw.Length)); return output;
    }
    private static async Task CancelBefore()
    {
        using var peer = new Peer(Boot()); var harness = peer.Harness(); using var caller = new CancellationTokenSource();
        try
        {
            await harness.Acceptance.WaitAsync(TimeSpan.FromSeconds(3)); caller.Cancel();
            Check.Throws<OperationCanceledException>(() => harness.StartAsync([], caller.Token));
            var response = await harness.StatusAsync(); Check.Equal(1UL, response.RequestId); Check.Equal(1, peer.Commands.Count);
        }
        finally { await Cleanup(harness, peer); }
    }
    private static async Task CancelPartial()
    {
        using var peer = new Peer(Boot()) { WriteChunk = 13, PauseAfterFirstWrite = true, HoldStop = true }; var harness = peer.Harness(); using var caller = new CancellationTokenSource();
        Task? start = null;
        try
        {
            await harness.Acceptance.WaitAsync(TimeSpan.FromSeconds(3)); start = harness.StartAsync([], caller.Token);
            await peer.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(3)); caller.Cancel(); Check.True(!start.IsCompleted); Check.Equal(0, peer.Commands.Count);
            peer.PauseAfterFirstWrite = false; await peer.StopSeen.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(!start.IsCompleted);
            Check.True(peer.Commands.Select(command => command.Opcode).SequenceEqual(new[] { NativeSourceOwnerOpcode.Start, NativeSourceOwnerOpcode.Stop }));
            peer.ReleaseStop(); Check.True(await Failure(start) is OperationCanceledException);
            Check.True(peer.Commands.Select(command => command.RequestId).SequenceEqual(new ulong[] { 1, 2 }));
        }
        finally { await Cleanup(harness, peer); if (start is not null) await Failure(start); }
    }
    private static async Task CancelPublication()
    {
        using var peer = new Peer(Boot()) { HoldStop = true }; var harness = peer.Harness(); using var caller = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); Task? start = null;
        peer.Publication = response => { if (response.Opcode == NativeSourceOwnerOpcode.Start) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Publication gate was not released"); } };
        try
        {
            await harness.Acceptance.WaitAsync(TimeSpan.FromSeconds(3)); start = harness.StartAsync([], caller.Token); Check.True(entered.Wait(TimeSpan.FromSeconds(3)));
            caller.Cancel(); release.Set(); await peer.StopSeen.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(!start.IsCompleted);
            peer.ReleaseStop(); Check.True(await Failure(start) is OperationCanceledException);
        }
        finally { release.Set(); await Cleanup(harness, peer); if (start is not null) await Failure(start); }
    }
    private static async Task StopShutdown()
    {
        using var peer = new Peer(Boot()) { HoldStop = true }; var harness = peer.Harness();
        try
        {
            await harness.Acceptance.WaitAsync(TimeSpan.FromSeconds(3)); var first = harness.StopAsync(); var second = harness.StopAsync(); Check.True(ReferenceEquals(first, second));
            await peer.StopSeen.Task.WaitAsync(TimeSpan.FromSeconds(3)); var terminal = harness.ShutdownAsync();
            Check.True(!terminal.IsCompleted); Check.Equal(1, peer.Commands.Count); peer.ReleaseStop(); await first;
            var observation = await terminal; Check.Equal(0, observation.Exit.WaitStatus);
            Check.True(peer.Commands.Select(command => command.Opcode).SequenceEqual(new[] { NativeSourceOwnerOpcode.Stop, NativeSourceOwnerOpcode.Shutdown }));
            Check.True(ReferenceEquals(terminal, harness.ShutdownAsync()));
        }
        finally { await Cleanup(harness, peer); }
    }
    private static async Task ShutdownStart()
    {
        using var peer = new Peer(Boot()) { HoldStart = true }; var harness = peer.Harness(); Task? start = null;
        try
        {
            await harness.Acceptance.WaitAsync(TimeSpan.FromSeconds(3)); start = harness.StartAsync([]); await peer.StartSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var ended = await harness.ShutdownAsync(); Check.Equal(0, ended.Exit.WaitStatus); Check.True(await Failure(start) is OperationCanceledException);
            Check.True(peer.Commands.Select(command => command.Opcode).SequenceEqual(new[] { NativeSourceOwnerOpcode.Start, NativeSourceOwnerOpcode.Shutdown }));
        }
        finally { await Cleanup(harness, peer); if (start is not null) await Failure(start); }
    }
    private static async Task ReapBeforeEof()
    {
        using var peer = new Peer(Boot()) { HoldResponseEof = true }; var harness = peer.Harness();
        try
        {
            await harness.Acceptance.WaitAsync(TimeSpan.FromSeconds(3)); var terminal = harness.ShutdownAsync();
            var exited = await harness.OwnerExited.WaitAsync(TimeSpan.FromSeconds(3)); Check.Equal(0, exited.WaitStatus); Check.True(!terminal.IsCompleted);
            peer.End(4); var ended = await terminal; Check.Equal(NativeSourceOwnerOpcode.Shutdown, ended.Reply.Opcode); Check.True(peer.Closed.Values.All(count => count == 1));
        }
        finally { await Cleanup(harness, peer); }
    }
    private static async Task MissingReap()
    {
        using var peer = new Peer(Boot()) { OmitReap = true, ReapOnCommandClose = false }; var harness = peer.Harness();
        try
        {
            await harness.Acceptance.WaitAsync(TimeSpan.FromSeconds(3)); var terminal = harness.ShutdownAsync();
            await peer.TerminalPoll.Task.WaitAsync(TimeSpan.FromSeconds(3)); peer.Advance(25000);
            var failure = await Failure(terminal); Check.True(failure is NativeSourceOwnerConnection.LifetimeFailure);
            var details = (NativeSourceOwnerConnection.LifetimeFailure)failure!; Check.Equal(NativeSourceOwnerOpcode.Shutdown, details.LastResponse!.Opcode); Check.True(details.ObservedExitRecord is null);
            Check.True(await Failure(harness.OwnerExited) is not null); Check.True(peer.Closed.Values.All(count => count == 1));
        }
        finally { await Cleanup(harness, peer); }
    }
    private static async Task ProtocolClose()
    {
        using var peer = new Peer(Boot()) { CorruptNextResponse = true, CommandCloseFailure = new IOException("controlled physical close failure") }; var harness = peer.Harness();
        try
        {
            await harness.Acceptance.WaitAsync(TimeSpan.FromSeconds(3)); var request = harness.StatusAsync(); var failure = await Failure(request);
            Check.True(failure is NativeSourceOwnerConnection.LifetimeFailure); Check.True(Tree(failure!).Any(error => error is InvalidDataException));
            Check.True(Tree(failure!).Any(error => ReferenceEquals(error, peer.CommandCloseFailure)));
            Check.True(await harness.RetirementStarted is not null); var first = harness.ShutdownAsync(); var second = harness.ShutdownAsync(); Check.True(ReferenceEquals(first, second));
            Check.True(ReferenceEquals(await Failure(first), await Failure(second))); Check.True(peer.Closed.Values.All(count => count == 1));
        }
        finally { await Cleanup(harness, peer); }
    }
    private static async Task PhysicalJoin()
    {
        using var peer = new Peer(Boot()); var harness = peer.Harness();
        try
        {
            await harness.Acceptance.WaitAsync(TimeSpan.FromSeconds(3)); peer.CloseGate.Reset(); var terminal = harness.ShutdownAsync();
            Check.True(peer.CloseEntered.Wait(TimeSpan.FromSeconds(3))); Check.True(!terminal.IsCompleted);
            Check.Throws<OperationCanceledException>(() => harness.StatusAsync()); peer.CloseGate.Set(); await terminal;
            Check.True(peer.CloseFinished); Check.True(peer.Closed.Values.All(count => count == 1));
        }
        finally { peer.CloseGate.Set(); await Cleanup(harness, peer); }
    }
    private static void FactoryBoundary()
    {
        var type = typeof(NativeSourceOwnerConnection);
        Check.Equal(0, type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Length);
        var factory = type.GetMethod("AcceptInheritedAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Check.True(factory.IsAssembly); Check.True(factory.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(SourceStartupRequest), typeof(CancellationToken), typeof(SupportLogStore) }));
        Check.True(!typeof(NativeSourceOwnerConnection.ControlledHarness).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).Any(property => property.PropertyType == type));
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Check.Throws<PlatformNotSupportedException>(NativeSourceOwnerConnection.RequirePostExecPrivacyBeforeConfiguration);
            Check.True(Failure(NativeSourceOwnerConnection.AcceptInheritedAsync(new("external", 0, 0), CancellationToken.None)).GetAwaiter().GetResult() is PlatformNotSupportedException);
        }
    }
    private static async Task StopBeforeStartCommit()
    {
        using var peer = new Peer(Boot()); peer.PollGate.Reset(); var harness = peer.Harness();
        try
        {
            await harness.Acceptance.WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(peer.PollEntered.Wait(TimeSpan.FromSeconds(3)));
            var start = harness.StartAsync([]); var stop = harness.StopAsync();
            Check.Equal(0, peer.Commands.Count); peer.PollGate.Set();
            var stopped = await stop.WaitAsync(TimeSpan.FromSeconds(3));
            Check.Equal(NativeSourceOwnerState.Idle, stopped.State);
            Check.True(await Failure(start.WaitAsync(TimeSpan.FromSeconds(3))) is OperationCanceledException);
            var observed = await harness.StatusAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check.Equal(0UL, observed.Generation);
            Check.True(peer.Commands.Select(command => command.Opcode).SequenceEqual(new[] { NativeSourceOwnerOpcode.Stop, NativeSourceOwnerOpcode.Status }));
        }
        finally { peer.PollGate.Set(); await Cleanup(harness, peer); }
    }
    private static async Task SourceLogs()
    {
        using var logs = new SupportLogTests.Fixture(); logs.Privacy.Register(new Dictionary<string, string?> { ["password"] = "relay-sentinel" });
        using var peer = new Peer(Boot()) { ReadChunk = 7, WriteChunk = 3 };
        await using var harness = peer.Harness(logs.Store); await harness.Acceptance;
        await harness.StartAsync([]);
        var first = "password=relay-sentinel\n"; var second = "ASOREP1\nliteral response marker 界\nsafe tail";
        peer.Feed(4, peer.SourceLog(0, first)); peer.Feed(4, peer.SourceLog((ulong)Encoding.UTF8.GetByteCount(first), second));
        var stopped = await harness.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check.True((stopped.Facts & NativeSourceOwnerFacts.StreamsDrained) != 0);
        _ = await harness.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check.True(peer.CloseFinished); Check.True(harness.SourceOutputState is { Closed: true, Queued: 0, Consuming: false });
        var encoded = (await logs.Store.SnapshotAsync()).ToJsonString();
        Check.True(!encoded.Contains("relay-sentinel", StringComparison.Ordinal)); Check.True(encoded.Contains("literal response marker", StringComparison.Ordinal));
        Check.True(encoded.Contains("safe tail", StringComparison.Ordinal));
        Check.True(peer.Commands.Select(c => c.Opcode).SequenceEqual(new[] { NativeSourceOwnerOpcode.Start, NativeSourceOwnerOpcode.Stop, NativeSourceOwnerOpcode.Shutdown }));
    }
    private static async Task SourceLogPressure()
    {
        using var logs = new SupportLogTests.Fixture(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var register = Task.Run(() => logs.Privacy.Register(new SupportLogTests.HeldSecrets(entered, release)));
        using var peer = new Peer(Boot()); var harness = peer.Harness(logs.Store); Task<NativeSourceOwnerConnection.Termination>? shutdown = null;
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(3))); await harness.Acceptance; await harness.StartAsync([]);
            var first = "first relay line\n"; peer.Feed(4, peer.SourceLog(0, first));
            await WaitFor(() => harness.SourceOutputState is { Consuming: true });
            var text = "queue-held-secret\n"; var at = (ulong)Encoding.UTF8.GetByteCount(first); var size = (ulong)Encoding.UTF8.GetByteCount(text);
            for (var index = 0; index < 300; index++) { peer.Feed(4, peer.SourceLog(at, text)); at += size; }
            var stopped = await harness.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check.Equal(NativeSourceOwnerResult.Ok, stopped.Result);
            Check.True(harness.SourceOutputState is { PeakQueued: <= 64, DroppedBytes: > 0, QuarantinedThrough: 1 });
            shutdown = harness.ShutdownAsync();
            await WaitFor(() => Volatile.Read(ref peer.CloseFinished)); Check.True(!shutdown.IsCompleted);
            release.Set(); await register; _ = await shutdown.WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(harness.SourceOutputState is { Closed: true, Queued: 0, Consuming: false });
            var encoded = (await logs.Store.SnapshotAsync()).ToJsonString(); Check.True(!encoded.Contains("queue-held-secret", StringComparison.Ordinal));
            Check.True(encoded.Contains("capture gap", StringComparison.Ordinal));
        }
        finally
        {
            release.Set(); await register; peer.ReleaseEverything(); await harness.DisposeAsync();
            if (shutdown is not null) await shutdown;
        }
    }
    private static async Task InvalidSourceLogs()
    {
        foreach (var kind in new[] { "nonce", "flags", "offset", "length", "partial" })
        {
            using var logs = new SupportLogTests.Fixture(); using var peer = new Peer(Boot()) { ReadChunk = 5 };
            var harness = peer.Harness(logs.Store);
            try
            {
                await harness.Acceptance; await harness.StartAsync([]);
                var frame = peer.SourceLog(0, "untrusted partial payload");
                if (kind == "nonce") frame[8] ^= 1;
                if (kind == "flags") BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(60), 2);
                if (kind == "offset") BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(48), 1);
                if (kind == "length") BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(56), 449);
                peer.Feed(4, kind == "partial" ? frame[..70] : frame);
                if (kind == "partial") peer.End(4);
                Check.True(await harness.RetirementStarted.WaitAsync(TimeSpan.FromSeconds(3)) is not null);
                var failure = await Failure(harness.DisposeAsync().AsTask()); Check.True(failure is not null);
                Check.True(peer.CloseFinished && peer.Closed.Values.All(count => count == 1));
                Check.True(!peer.Commands.Any(command => command.Opcode == NativeSourceOwnerOpcode.PhysicalSelfTest));
                Check.True(!(await logs.Store.SnapshotAsync()).ToJsonString().Contains("untrusted partial payload", StringComparison.Ordinal));
            }
            finally { peer.ReleaseEverything(); await Failure(harness.DisposeAsync().AsTask()); }
        }
    }
    private static async Task WaitFor(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual native/log lifecycle state was not observed"); await Task.Delay(1); }
    }
    private static NativeSourceSnapshotRequest SnapshotQuery(Peer peer) => new(peer.Generation, NativeSourceSnapshotKind.InstalledJar, "test.jar");
    private static async Task SnapshotOwnership()
    {
        using var peer = new Peer(Boot()) { ReadChunk = 7, WriteChunk = 3 }; var h = peer.Harness();
        try
        {
            await h.Acceptance; await h.StartAsync([]); var lease = await h.ObserveAsync(SnapshotQuery(peer)).WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(h.OwnsSnapshot(lease)); Check.Equal(NativeSourceSnapshotOutcome.Complete, lease.Observation.Result.Outcome);
            Check.True(!lease.ToString().Contains(lease.Observation.Ticket, StringComparison.Ordinal));
            _ = await h.StatusAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check.Throws<SourceWorkDeferredException>(() => h.StartAsync([]));
            var first = lease.ReleaseAsync(); Check.True(ReferenceEquals(first, lease.ReleaseAsync()));
            Check.Equal(NativeSourceOwnerResult.Ok, (await first.WaitAsync(TimeSpan.FromSeconds(3))).Result);
            Check.True(!h.OwnsSnapshot(lease)); await h.StartAsync([]).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await Cleanup(h, peer); }
    }
    private static async Task SnapshotCancelBefore()
    {
        using var peer = new Peer(Boot()); var h = peer.Harness(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await h.Acceptance; Check.Throws<OperationCanceledException>(() => h.ObserveAsync(new(0, NativeSourceSnapshotKind.RetainedUserAgent, ""), cancel.Token)); Check.Equal(0, peer.Commands.Count); _ = await h.StatusAsync(); Check.Equal(1UL, peer.Commands[0].RequestId); }
        finally { await Cleanup(h, peer); }
    }
    private static async Task SnapshotCancelAfter()
    {
        using var peer = new Peer(Boot()) { HoldObservation = true }; var h = peer.Harness(); using var cancel = new CancellationTokenSource();
        try
        {
            await h.Acceptance; await h.StartAsync([]); var request = h.ObserveAsync(SnapshotQuery(peer), cancel.Token);
            await peer.ObservationSeen.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancel.Cancel(); Check.True(!request.IsCompleted);
            peer.ReleaseObservation(); var lease = await request.WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(h.OwnsSnapshot(lease)); Check.Equal(NativeSourceOwnerResult.Ok, (await lease.ReleaseAsync().WaitAsync(TimeSpan.FromSeconds(3))).Result);
        }
        finally { await Cleanup(h, peer); }
    }
    private static async Task SnapshotBudgetAndStatus()
    {
        using var peer = new Peer(Boot()) { HoldObservation = true }; var h = peer.Harness();
        try
        {
            await h.Acceptance; await h.StartAsync([]); var request = h.ObserveAsync(SnapshotQuery(peer)); await peer.ObservationSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
            peer.Advance(21000); _ = await h.StatusAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(!h.RetirementStarted.IsCompleted && !request.IsCompleted);
            Check.Throws<SourceWorkDeferredException>(() => h.ObserveAsync(SnapshotQuery(peer))); Check.Throws<SourceWorkDeferredException>(() => h.StartAsync([]));
            peer.ReleaseObservation(); var lease = await request.WaitAsync(TimeSpan.FromSeconds(3)); _ = await lease.ReleaseAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await Cleanup(h, peer); }
    }
    private static async Task SnapshotInvalidEvents()
    {
        foreach (var kind in new[] { "nonce", "reverse", "missing", "duplicate" })
        {
            using var peer = new Peer(Boot()) { SnapshotFault = kind }; var h = peer.Harness();
            try
            {
                await h.Acceptance; await h.StartAsync([]); var request = h.ObserveAsync(SnapshotQuery(peer));
                Check.True(await h.RetirementStarted.WaitAsync(TimeSpan.FromSeconds(3)) is not null);
                var error = await Failure(h.DisposeAsync().AsTask()); Check.True(error is not null && Tree(error).Any(e => e is InvalidDataException));
                Check.True(await Failure(request) is not null); Check.True(peer.CloseFinished);
            }
            finally { await Cleanup(h, peer); }
        }
    }
    private static async Task SnapshotReleasePending()
    {
        using var peer = new Peer(Boot()) { HoldRelease = true }; var h = peer.Harness();
        try
        {
            await h.Acceptance; await h.StartAsync([]); var lease = await h.ObserveAsync(SnapshotQuery(peer)); var release = lease.ReleaseAsync();
            await peer.ReleaseSeen.Task.WaitAsync(TimeSpan.FromSeconds(3)); _ = await h.StatusAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(!release.IsCompleted); Check.Throws<SourceWorkDeferredException>(() => h.StartAsync([]));
            peer.ReleaseSnapshot(); Check.Equal(NativeSourceOwnerResult.Ok, (await release.WaitAsync(TimeSpan.FromSeconds(3))).Result);
        }
        finally { await Cleanup(h, peer); }
    }
    private static async Task SnapshotReconcile()
    {
        using var peer = new Peer(Boot()) { ReleaseResult = NativeSourceOwnerResult.Failed }; var h = peer.Harness();
        try
        {
            await h.Acceptance; await h.StartAsync([]); var lease = await h.ObserveAsync(SnapshotQuery(peer));
            Check.Equal(NativeSourceOwnerResult.Failed, (await lease.ReleaseAsync()).Result); Check.True(!h.RetirementStarted.IsCompleted);
            Check.Throws<SourceWorkDeferredException>(() => h.StartAsync([])); peer.ReleaseResult = NativeSourceOwnerResult.Ok;
            Check.Equal(NativeSourceOwnerResult.Ok, (await lease.ReconcileAsync().WaitAsync(TimeSpan.FromSeconds(3))).Result);
            Check.True(!h.OwnsSnapshot(lease)); Check.Equal(2, peer.Commands.Count(c => c.Opcode == NativeSourceOwnerOpcode.ReleaseSnapshot));
        }
        finally { await Cleanup(h, peer); }
    }
    private static async Task SnapshotShutdown()
    {
        using var peer = new Peer(Boot()) { HoldObservation = true, HoldRelease = true }; var h = peer.Harness();
        try
        {
            await h.Acceptance; await h.StartAsync([]); var request = h.ObserveAsync(SnapshotQuery(peer)); await peer.ObservationSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var shutdown = h.ShutdownAsync(); Check.True(!shutdown.IsCompleted); peer.ReleaseObservation();
            await peer.ReleaseSeen.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(!shutdown.IsCompleted);
            Check.True(!peer.Commands.Any(c => c.Opcode == NativeSourceOwnerOpcode.Shutdown)); peer.ReleaseSnapshot();
            _ = await shutdown.WaitAsync(TimeSpan.FromSeconds(3)); var lease = await request.WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(!h.OwnsSnapshot(lease) && peer.CloseFinished);
            Check.True(peer.Commands.Select(c => c.Opcode).SequenceEqual(new[] { NativeSourceOwnerOpcode.Start, NativeSourceOwnerOpcode.Observe, NativeSourceOwnerOpcode.ReleaseSnapshot, NativeSourceOwnerOpcode.Shutdown }));
        }
        finally { await Cleanup(h, peer); }
    }
    private static async Task SnapshotUncommitted()
    {
        using var peer = new Peer(Boot()); peer.PollGate.Reset(); var h = peer.Harness();
        try
        {
            await h.Acceptance; Check.True(peer.PollEntered.Wait(TimeSpan.FromSeconds(3)));
            var request = h.ObserveAsync(new(0, NativeSourceSnapshotKind.RetainedUserAgent, "")); var shutdown = h.ShutdownAsync(); peer.PollGate.Set();
            _ = await shutdown.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(await Failure(request) is OperationCanceledException);
            Check.True(peer.Commands.Select(c => c.Opcode).SequenceEqual(new[] { NativeSourceOwnerOpcode.Shutdown }));
        }
        finally { peer.PollGate.Set(); await Cleanup(h, peer); }
    }
    private static async Task SnapshotFailedResults()
    {
        foreach (var template in new[] { "timeout-event", "truncated-event" })
        {
            using var peer = new Peer(Boot()) { SnapshotTemplate = template }; var h = peer.Harness();
            try
            {
                await h.Acceptance; await h.StartAsync([]); var lease = await h.ObserveAsync(SnapshotQuery(peer));
                Check.Equal(NativeSourceSnapshotOutcome.Failed, lease.Observation.Result.Outcome); Check.True(!h.RetirementStarted.IsCompleted);
                _ = await lease.ReleaseAsync(); _ = await h.StatusAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { await Cleanup(h, peer); }
        }
    }
    private static async Task Cleanup(NativeSourceOwnerConnection.ControlledHarness harness, Peer peer)
    { peer.ReleaseEverything(); await Failure(harness.DisposeAsync().AsTask()); await Failure(harness.Acceptance); }
    private static async Task<Exception?> Failure(Task task) { try { await task.ConfigureAwait(false); return null; } catch (Exception error) { return error; } }
    private static IEnumerable<Exception> Tree(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate) foreach (var child in aggregate.InnerExceptions) foreach (var item in Tree(child)) yield return item;
        else if (error.InnerException is { } inner) foreach (var item in Tree(inner)) yield return item;
    }
    private sealed class IndexedLaunch : IReadOnlyList<KeyValuePair<string, string>>
    {
        public int Count => 1;
        public KeyValuePair<string, string> this[int index] => index == 0 ? new("KCEF_ENABLED", "true") : throw new IndexOutOfRangeException();
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => throw new InvalidOperationException("Launch snapshot must not enumerate an arbitrary list");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    internal sealed class Peer : NativeSourceOwnerConnection.ControlledPeer, IDisposable
    {
        private readonly object _gate = new(); private readonly Dictionary<int, Queue<byte>> _inputs = []; private readonly HashSet<int> _eof = [], _owned = [];
        private readonly List<byte> _writing = []; private readonly List<NativeSourceOwnerCommandMessage> _commands = [];
        private readonly NativeSourceOwnerConnection.BootMessage _boot; private readonly byte[] _nonce; private long _offset;
        private NativeSourceOwnerCommandMessage? _start, _stop, _observation, _release; private bool _wroteOnce, _reapFed;
        internal int ReadChunk = int.MaxValue, WriteChunk = int.MaxValue, WrongSlot, HoldReadSlot;
        internal volatile bool PauseAfterFirstWrite, HoldStart, HoldStop, HoldResponseEof, OmitReap, ReapOnCommandClose = true, CorruptNextResponse;
        internal volatile bool HoldObservation, HoldRelease;
        internal string SnapshotTemplate = "complete-event", SnapshotFault = "";
        internal NativeSourceOwnerResult ObserveResult = NativeSourceOwnerResult.Ok, ReleaseResult = NativeSourceOwnerResult.Ok;
        internal ulong Generation; internal Action<NativeSourceOwnerResponseMessage>? Publication; internal Action? AfterProofEof; internal IOException? CommandCloseFailure, ProofReadFailure;
        internal readonly TaskCompletionSource StartSeen = Signal(), StopSeen = Signal(), FirstWrite = Signal(), TerminalPoll = Signal(), Opened = Signal(), ProofBlocked = Signal(), CommandClosed = Signal();
        internal readonly TaskCompletionSource ObservationSeen = Signal(), ReleaseSeen = Signal();
        internal readonly ManualResetEventSlim CloseGate = new(true), CloseEntered = new(); internal bool CloseFinished;
        internal readonly ManualResetEventSlim PollGate = new(true), PollEntered = new();
        internal readonly List<int> Inspected = []; internal readonly Dictionary<int, int> Closed = [];
        internal IReadOnlyList<NativeSourceOwnerCommandMessage> Commands { get { lock (_gate) return _commands.ToArray(); } }
        internal NativeSourceOwnerProofMessage SnapshotOwner => _boot.ExpectedOwner!;
        internal Peer(byte[] boot, bool trailingBoot = false, Action<byte[]>? proofMutation = null, bool proofTrailing = false)
        {
            _boot = NativeSourceOwnerConnection.ParseBootMessage(boot); _nonce = Convert.FromHexString(_boot.Nonce);
            Feed(6, trailingBoot ? boot.Concat(new byte[] { 0 }).ToArray() : boot); End(6);
            if (_boot.ExpectedOwner is not null)
            { var proof = boot.AsSpan(192 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(96)))).ToArray(); proofMutation?.Invoke(proof); Feed(5, proofTrailing ? proof.Concat(new byte[] { 0 }).ToArray() : proof); End(5); }
        }
        internal NativeSourceOwnerConnection.ControlledHarness Harness(SupportLogStore? diagnostics = null) => new(this, _boot.StartupRequest, diagnostics: diagnostics);
        private static readonly Lazy<byte[]> SourceLogTemplate = new(() => Convert.FromHexString(NativeSourceOwnerProtocolTests.SourceLogFixture()["vectors"]![0]!["hex"]!.GetValue<string>()));
        internal byte[] SourceLog(ulong offset, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text); Check.True(bytes.Length is > 0 and <= 448);
            var frame = new byte[64 + bytes.Length]; SourceLogTemplate.Value.AsSpan(0, 64).CopyTo(frame); _nonce.CopyTo(frame, 8);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(40), Generation); BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(48), offset);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(56), (uint)bytes.Length); BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(60), 0);
            bytes.CopyTo(frame, 64); return frame;
        }
        internal override long Milliseconds => Environment.TickCount64 + Interlocked.Read(ref _offset);
        internal void Advance(long milliseconds) => Interlocked.Add(ref _offset, milliseconds);
        internal override void OpenBoot() { lock (_gate) _owned.Add(6); Opened.TrySetResult(); }
        internal override void RequireMainIdentity(uint pid, uint groups, uint kvmGid)
        { Check.Equal(_boot.MainPid, pid); Check.Equal(_boot.GroupCount, groups); Check.Equal(_boot.KvmGid, kvmGid); }
        internal override NativeSourceOwnerConnection.PipeIdentity Inspect(int slot)
        { lock (_gate) { Inspected.Add(slot); var identity = _boot.Pipes[slot - 3]; return WrongSlot == slot ? identity with { Inode = identity.Inode + 1 } : identity; } }
        internal override void AdoptIntegrated() { lock (_gate) foreach (var slot in new[] { 3, 4, 5, 7 }) _owned.Add(slot); }
        internal override int Read(int slot, Span<byte> destination)
        {
            lock (_gate)
            {
                if (!_owned.Contains(slot)) throw new IOException("Controlled actor read an unowned pipe");
                if (slot == 5 && ProofReadFailure is { } failure) { ProofReadFailure = null; throw failure; }
                if (HoldReadSlot == slot) { if (slot == 5) ProofBlocked.TrySetResult(); return -1; }
                if (_inputs.TryGetValue(slot, out var input) && input.Count != 0)
                { var count = Math.Min(Math.Min(input.Count, destination.Length), ReadChunk); for (var index = 0; index < count; index++) destination[index] = input.Dequeue(); return count; }
                if (_eof.Contains(slot)) { if (slot == 5) Interlocked.Exchange(ref AfterProofEof, null)?.Invoke(); return 0; }
                return -1;
            }
        }
        internal override int Write(ReadOnlySpan<byte> bytes)
        {
            NativeSourceOwnerCommandMessage? command = null; int count;
            lock (_gate)
            {
                if (!_owned.Contains(3)) throw new IOException("Controlled command pipe is closed");
                if (PauseAfterFirstWrite && _wroteOnce) return -1;
                count = Math.Min(bytes.Length, WriteChunk); _writing.AddRange(bytes[..count].ToArray()); _wroteOnce = true; FirstWrite.TrySetResult();
                if (_writing.Count >= 56 && _writing.Count == 56 + BinaryPrimitives.ReadUInt32LittleEndian(_writing.ToArray().AsSpan(52)))
                { command = NativeSourceOwnerProtocol.DecodeCommand(_writing.ToArray(), _nonce, (ulong)_commands.Count + 1); _commands.Add(command); _writing.Clear(); }
            }
            if (command is not null) OnCommand(command); return count;
        }
        private void OnCommand(NativeSourceOwnerCommandMessage command)
        {
            switch (command.Opcode)
            {
                case NativeSourceOwnerOpcode.Start:
                    Generation++; _start = command; StartSeen.TrySetResult(); if (!HoldStart) ReleaseStart(); break;
                case NativeSourceOwnerOpcode.Status: Reply(command, NativeSourceOwnerState.Idle); break;
                case NativeSourceOwnerOpcode.Observe: _observation = command; ObservationSeen.TrySetResult(); if (!HoldObservation) ReleaseObservation(); break;
                case NativeSourceOwnerOpcode.ReleaseSnapshot: _release = command; ReleaseSeen.TrySetResult(); if (!HoldRelease) ReleaseSnapshot(); break;
                case NativeSourceOwnerOpcode.Stop: _stop = command; StopSeen.TrySetResult(); if (!HoldStop) ReleaseStop(); break;
                case NativeSourceOwnerOpcode.Shutdown:
                    ReleaseStart(unavailable: true); Reply(command, NativeSourceOwnerState.Idle);
                    if (!HoldResponseEof) End(4); if (!OmitReap) FeedReap(); break;
                default: throw new InvalidOperationException("Unexpected controlled opcode");
            }
        }
        internal void ReleaseStart(bool unavailable = false)
        { var start = Interlocked.Exchange(ref _start, null); if (start is not null) Reply(start, unavailable ? NativeSourceOwnerState.Idle : NativeSourceOwnerState.Running, unavailable ? NativeSourceOwnerResult.Unavailable : NativeSourceOwnerResult.Ok); }
        internal void ReleaseStop() { HoldStop = false; var stop = Interlocked.Exchange(ref _stop, null); if (stop is not null) Reply(stop, NativeSourceOwnerState.Idle); }
        internal void ReleaseObservation()
        {
            HoldObservation = false; var command = Interlocked.Exchange(ref _observation, null); if (command is null) return;
            if (ObserveResult != NativeSourceOwnerResult.Ok) { Reply(command, NativeSourceOwnerState.Running, ObserveResult); return; }
            var corpus = JsonNode.Parse(File.ReadAllBytes(ConfigurationTests.Fixture("native-source-observation-ownership.json")))!;
            var template = SnapshotTemplate == "missing-event" ? "complete-event" : SnapshotTemplate;
            var frame = Convert.FromHexString(corpus["parent_vectors"]!.AsArray().Single(row => row!["vector"]!.GetValue<string>() == template)!["hex"]!.GetValue<string>());
            _nonce.CopyTo(frame, 8); BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(40), command.RequestId);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(48), command.Observation!.Generation);
            if (command.Observation.Kind == NativeSourceSnapshotKind.RetainedUserAgent && SnapshotTemplate == "complete-event")
            {
                var original = JsonNode.Parse(File.ReadAllBytes(ConfigurationTests.Fixture("native-source-observation.json")))!;
                Convert.FromHexString(original["vectors"]!.AsArray().Single(row => row!["vector"]!.GetValue<string>() == "complete-ua-0-0")!["hex"]!.GetValue<string>()).CopyTo(frame, 104);
            }
            if (SnapshotTemplate == "missing-event")
            {
                var original = JsonNode.Parse(File.ReadAllBytes(ConfigurationTests.Fixture("native-source-observation.json")))!;
                Convert.FromHexString(original["vectors"]!.AsArray().Single(row => row!["vector"]!.GetValue<string>() == "missing-file")!["hex"]!.GetValue<string>()).CopyTo(frame, 104);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(108), (uint)command.Observation.Kind);
            var outcome = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(104));
            if (outcome is 1 or 2)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(160), _boot.ExpectedOwner!.Root.Device);
                BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(168), _boot.ExpectedOwner.Root.Inode);
                if (outcome == 1) { BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(240), _boot.ExpectedOwner.Uid); BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(244), _boot.ExpectedOwner.Gid); }
            }
            if (SnapshotFault == "nonce") frame[8] ^= 1;
            if (SnapshotFault == "reverse") Reply(command, NativeSourceOwnerState.Running);
            if (SnapshotFault != "missing") Feed(4, frame);
            if (SnapshotFault == "duplicate") Feed(4, frame);
            if (SnapshotFault != "reverse") Reply(command, NativeSourceOwnerState.Running);
        }
        internal void ReleaseSnapshot()
        { HoldRelease = false; var command = Interlocked.Exchange(ref _release, null); if (command is not null) Reply(command, NativeSourceOwnerState.Running, ReleaseResult); }
        private void Reply(NativeSourceOwnerCommandMessage command, NativeSourceOwnerState state, NativeSourceOwnerResult result = NativeSourceOwnerResult.Ok)
        {
            var frame = new byte[120]; "ASOREP1\n"u8.CopyTo(frame); _nonce.CopyTo(frame, 8);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(40), command.RequestId); BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(48), (ushort)command.Opcode);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(50), (ushort)result); BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(52), 56); BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(56), Generation);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(64), (uint)state); BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(68), Generation == 0 ? 0U : 73U); BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(72), Generation == 0 ? 0U : 73U);
            var facts = state == NativeSourceOwnerState.Idle ? NativeSourceOwnerFacts.NoChildren | NativeSourceOwnerFacts.StreamsDrained | (Generation == 0 ? NativeSourceOwnerFacts.None : NativeSourceOwnerFacts.Exit) : NativeSourceOwnerFacts.None;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(80), (uint)facts); BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(104), _boot.ExpectedOwner!.Root.Device); BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(112), _boot.ExpectedOwner.Root.Inode);
            if (CorruptNextResponse) { CorruptNextResponse = false; frame[0] ^= 1; } Feed(4, frame);
        }
        private void FeedReap()
        {
            lock (_gate) { if (_reapFed) return; _reapFed = true; }
            var frame = new byte[48]; "ASREAP1\n"u8.CopyTo(frame); _nonce.CopyTo(frame, 8); BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(40), _boot.ExpectedOwner!.OwnerPid);
            Feed(7, frame); End(7);
        }
        internal void ReleaseReap() => FeedReap();
        internal void Feed(int slot, byte[] bytes) { lock (_gate) { if (!_inputs.TryGetValue(slot, out var queue)) _inputs[slot] = queue = new(); foreach (var value in bytes) queue.Enqueue(value); } }
        internal void End(int slot) { lock (_gate) _eof.Add(slot); }
        internal override void Poll(bool writing, int milliseconds)
        {
            PollEntered.Set(); if (!PollGate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Controlled poll gate was not released");
            bool closed; lock (_gate) closed = Closed.ContainsKey(3);
            if (closed || Commands.Any(command => command.Opcode == NativeSourceOwnerOpcode.Shutdown)) TerminalPoll.TrySetResult();
            Thread.Sleep(Math.Min(5, milliseconds));
        }
        internal override void BeforePublication(NativeSourceOwnerResponseMessage response) => Publication?.Invoke(response);
        internal override void Close(int slot)
        {
            var retire = false;
            lock (_gate) if (_owned.Remove(slot)) { Closed[slot] = Closed.GetValueOrDefault(slot) + 1; retire = slot == 3; }
            if (retire)
            {
                CommandClosed.TrySetResult();
                End(4); if (ReapOnCommandClose && !OmitReap) FeedReap();
                if (CommandCloseFailure is not null) throw CommandCloseFailure;
            }
        }
        internal override void CloseAll()
        {
            CloseEntered.Set(); if (!CloseGate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Physical close gate was not released");
            int[] owned; lock (_gate) owned = _owned.ToArray(); var failures = new List<Exception>();
            foreach (var slot in owned) try { Close(slot); } catch (Exception error) { failures.Add(error); }
            CloseFinished = true; if (failures.Count != 0) throw new AggregateException(failures);
        }
        internal void ReleaseEverything()
        {
            CloseGate.Set(); PollGate.Set(); PauseAfterFirstWrite = false; HoldReadSlot = 0; HoldStart = false; HoldStop = false; HoldResponseEof = false; OmitReap = false; ReapOnCommandClose = true;
            ReleaseStart(); ReleaseStop(); ReleaseObservation(); ReleaseSnapshot();
            bool commandClosed; lock (_gate) commandClosed = Closed.ContainsKey(3);
            if (_boot.ExpectedOwner is not null && (Commands.Any(command => command.Opcode == NativeSourceOwnerOpcode.Shutdown) || commandClosed))
            { End(4); FeedReap(); }
        }
        public void Dispose() { CloseGate.Set(); PollGate.Set(); CloseGate.Dispose(); CloseEntered.Dispose(); PollGate.Dispose(); PollEntered.Dispose(); }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
