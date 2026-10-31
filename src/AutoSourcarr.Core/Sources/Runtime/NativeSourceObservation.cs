using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AutoSourcarr.Core.Sources.Runtime;

internal enum NativeSourceSnapshotKind : uint { InstalledJar = 1, RetainedUserAgent = 2 }
internal enum NativeSourceSnapshotOutcome : uint { Complete = 1, Missing = 2, Changed = 3, Failed = 4 }
[Flags]
internal enum NativeSourceSnapshotFacts : uint { None = 0, Stable = 1, RootCurrent = 2, ExchangeHeld = 4, Published = 8 }
internal enum NativeSourceSnapshotCleanup : uint { None = 0, Removed = 1, Uncertain = 2 }
[Flags]
internal enum NativeSourceObserverFacts : uint { None = 0, Reaped = 1, EndOfFile = 2, ExitZero = 4, Timeout = 8, InvalidResult = 16 }

internal sealed record NativeSourceSnapshotRequest(ulong Generation, NativeSourceSnapshotKind Kind, string Leaf)
{
    internal void RequireValid()
    {
        if (Kind == NativeSourceSnapshotKind.InstalledJar)
        {
            try { SourceOwnedObservation.RequireInstalledJarLeaf(Leaf); }
            catch (ArgumentException) { throw new InvalidDataException("Invalid fixed source snapshot target."); }
        }
        else if (Kind != NativeSourceSnapshotKind.RetainedUserAgent || Leaf != string.Empty)
            throw new InvalidDataException("Invalid fixed source snapshot target.");
    }
    public override string ToString() => "Native source snapshot request.";
}

/// <summary>Decoded values only. Actual pipe acceptance and pending-request
/// ownership belong to the native connection actor, not this record or codec.</summary>
internal sealed record NativeSourceObservationMessage(ulong RequestId, ulong Generation, uint HelperPid,
    NativeSourceObserverFacts Facts, int WaitStatus, string Ticket, NativeSourceSnapshotResult Result)
{
    public override string ToString() => "Native source observation message.";
}

internal static class NativeSourceObservationProtocol
{
    internal const int EventBytes = 280;
    internal static bool IsEvent(ReadOnlySpan<byte> bytes) => bytes.Length >= 8 && bytes[..8].SequenceEqual("ASOOBS1\n"u8);
    internal static byte[] EncodeObserve(ReadOnlySpan<byte> nonce, ulong requestId, NativeSourceSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); request.RequireValid();
        var payload = new byte[16 + request.Leaf.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, request.Generation);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)request.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), (uint)request.Leaf.Length);
        Encoding.ASCII.GetBytes(request.Leaf, payload.AsSpan(16));
        try { return NativeSourceOwnerProtocol.EncodeSnapshotCommand(nonce, requestId, NativeSourceOwnerOpcode.Observe, payload); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }
    internal static NativeSourceSnapshotRequest DecodeObservePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 16) throw Invalid();
        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]);
        if (count > 1024 || payload.Length != 16L + count) throw Invalid();
        foreach (var value in payload[16..]) if (value > 127) throw Invalid();
        var request = new NativeSourceSnapshotRequest(BinaryPrimitives.ReadUInt64LittleEndian(payload),
            (NativeSourceSnapshotKind)BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]), Encoding.ASCII.GetString(payload[16..]));
        request.RequireValid(); return request;
    }
    internal static byte[] EncodeRelease(ReadOnlySpan<byte> nonce, ulong requestId, string ticket)
    {
        RequireTicket(ticket);
        var payload = Encoding.ASCII.GetBytes(ticket);
        try { return NativeSourceOwnerProtocol.EncodeSnapshotCommand(nonce, requestId, NativeSourceOwnerOpcode.ReleaseSnapshot, payload); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }
    internal static string DecodeReleasePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 32) throw Invalid();
        var ticket = Encoding.ASCII.GetString(payload); RequireTicket(ticket); return ticket;
    }
    internal static void RequireTicket(string ticket)
    {
        if (ticket is null || ticket.Length != 32 || ticket.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw Invalid();
    }
    internal static void RequireHeader(ReadOnlySpan<byte> header, ReadOnlySpan<byte> nonce)
    {
        if (header.Length != 64 || !IsEvent(header) || nonce.Length != 32
            || !CryptographicOperations.FixedTimeEquals(header.Slice(8, 32), nonce)) throw Invalid();
    }
    internal static NativeSourceObservationMessage DecodeEvent(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> nonce,
        ulong requestId, NativeSourceSnapshotRequest request, NativeSourceOwnerProofMessage owner)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(owner); request.RequireValid();
        if (frame.Length != EventBytes || requestId == 0 || owner.Profile != NativeSourceOwnerProfile.Production
            || owner.OwnerPid is <= 1 or > int.MaxValue || owner.Uid is 0 or uint.MaxValue || owner.Gid is 0 or uint.MaxValue || owner.Root.Inode == 0) throw Invalid();
        RequireHeader(frame[..64], nonce);
        var id = BinaryPrimitives.ReadUInt64LittleEndian(frame[40..]);
        var generation = BinaryPrimitives.ReadUInt64LittleEndian(frame[48..]);
        var pid = BinaryPrimitives.ReadUInt32LittleEndian(frame[56..]);
        var facts = (NativeSourceObserverFacts)BinaryPrimitives.ReadUInt32LittleEndian(frame[60..]);
        var status = BinaryPrimitives.ReadInt32LittleEndian(frame[64..]);
        if (id != requestId || generation != request.Generation || pid is <= 1 or > int.MaxValue || pid == owner.OwnerPid
            || ((uint)facts & ~31U) != 0 || (facts & (NativeSourceObserverFacts.Reaped | NativeSourceObserverFacts.EndOfFile)) != (NativeSourceObserverFacts.Reaped | NativeSourceObserverFacts.EndOfFile)
            || status is < 0 or > 65535 || ((facts & NativeSourceObserverFacts.ExitZero) != 0) != (status == 0)
            || BinaryPrimitives.ReadUInt32LittleEndian(frame[68..]) != 0) throw Invalid();
        var ticket = DecodeReleasePayload(frame.Slice(72, 32));
        var result = NativeSourceSnapshotResult.Decode(frame[104..]);
        if (result.Kind != request.Kind) throw Invalid();
        if (result.Outcome is NativeSourceSnapshotOutcome.Complete or NativeSourceSnapshotOutcome.Missing)
        {
            if (facts != (NativeSourceObserverFacts.Reaped | NativeSourceObserverFacts.EndOfFile | NativeSourceObserverFacts.ExitZero)
                || result.SourceRoot != owner.Root) throw Invalid();
            if (result.Outcome == NativeSourceSnapshotOutcome.Complete && (result.OutputUid != owner.Uid || result.OutputGid != owner.Gid)) throw Invalid();
        }
        return new(id, generation, pid, facts, status, ticket, result);
    }
    private static InvalidDataException Invalid() => new("Invalid native source observation message.");
}

/// <summary>
/// Plain child-result values. Decoding these bytes does not authenticate a native
/// owner, prove the child's actual exit/reap/EOF or grant filesystem authority.
/// Parent framing, lifetime and receiver integration remain required separately.
/// </summary>
internal sealed record NativeSourceSnapshotResult(
    NativeSourceSnapshotOutcome Outcome, NativeSourceSnapshotKind Kind, NativeSourceSnapshotFacts Facts,
    uint Error, ulong Length, string Sha256, NativeSourceOwnerRoot SourceRoot, NativeSourceOwnerRoot ExchangeRoot,
    NativeSourceOwnerRoot Output, NativeSourceOwnerRoot SourceFile, NativeSourceOwnerRoot SourceDirectory,
    uint OutputUid, uint OutputGid, uint OutputMode, uint OutputLinks,
    uint SourceUid, uint SourceGid, uint SourceMode, uint SourceLinks, NativeSourceSnapshotCleanup Cleanup)
{
    internal const int ByteCount = 176;
    internal const ulong JarMaximumBytes = 32UL * 1024 * 1024, UserAgentMaximumBytes = 64UL * 1024;
    private const NativeSourceSnapshotFacts CompleteFacts = NativeSourceSnapshotFacts.Stable | NativeSourceSnapshotFacts.RootCurrent
        | NativeSourceSnapshotFacts.ExchangeHeld | NativeSourceSnapshotFacts.Published;
    private const NativeSourceSnapshotFacts MissingFacts = NativeSourceSnapshotFacts.RootCurrent | NativeSourceSnapshotFacts.ExchangeHeld;

    internal static NativeSourceSnapshotResult Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteCount) throw Invalid();
        var outcome = (NativeSourceSnapshotOutcome)U32(bytes, 0); var kind = (NativeSourceSnapshotKind)U32(bytes, 4);
        if (outcome is not (NativeSourceSnapshotOutcome.Complete or NativeSourceSnapshotOutcome.Missing
            or NativeSourceSnapshotOutcome.Changed or NativeSourceSnapshotOutcome.Failed)
            || kind is not (NativeSourceSnapshotKind.InstalledJar or NativeSourceSnapshotKind.RetainedUserAgent)
            || U32(bytes, 172) != 0) throw Invalid();
        var result = new NativeSourceSnapshotResult(outcome, kind, (NativeSourceSnapshotFacts)U32(bytes, 8), U32(bytes, 12), U64(bytes, 16),
            Convert.ToHexStringLower(bytes.Slice(24, 32)), Root(bytes, 56), Root(bytes, 72), Root(bytes, 88), Root(bytes, 104), Root(bytes, 120),
            U32(bytes, 136), U32(bytes, 140), U32(bytes, 144), U32(bytes, 148),
            U32(bytes, 152), U32(bytes, 156), U32(bytes, 160), U32(bytes, 164), (NativeSourceSnapshotCleanup)U32(bytes, 168));
        result.RequireShape(); return result;
    }

    private void RequireShape()
    {
        var maximum = Kind == NativeSourceSnapshotKind.InstalledJar ? JarMaximumBytes : UserAgentMaximumBytes;
        if (Cleanup is not (NativeSourceSnapshotCleanup.None or NativeSourceSnapshotCleanup.Removed or NativeSourceSnapshotCleanup.Uncertain)) throw Invalid();
        if (Outcome == NativeSourceSnapshotOutcome.Complete)
        {
            if (Facts != CompleteFacts || Error != 0 || Cleanup != NativeSourceSnapshotCleanup.None || Length > maximum
                || Kind == NativeSourceSnapshotKind.InstalledJar && Length == 0
                || SourceRoot.Inode == 0 || ExchangeRoot.Inode == 0 || Output.Inode == 0 || SourceFile.Inode == 0 || SourceDirectory.Inode == 0
                || OutputUid is 0 or uint.MaxValue || OutputGid is 0 or uint.MaxValue || OutputMode != 0x8124 || OutputLinks != 1
                || (SourceMode & 0xf000) != 0x8000 || (SourceMode & ~0xffffU) != 0 || SourceLinks != 1) throw Invalid();
            // Original source bytes may be readable through group/ACL/root0644.
            // Source owner equality with the execution account is not required.
            return;
        }
        if (Length != 0 || Sha256.Any(character => character != '0') || SourceFile != default || SourceDirectory != default
            || OutputUid != 0 || OutputGid != 0 || OutputMode != 0 || OutputLinks != 0
            || SourceUid != 0 || SourceGid != 0 || SourceMode != 0 || SourceLinks != 0) throw Invalid();
        if (Outcome == NativeSourceSnapshotOutcome.Missing)
        {
            if (Facts != MissingFacts || Error != 0 || Cleanup != NativeSourceSnapshotCleanup.None
                || SourceRoot.Inode == 0 || ExchangeRoot.Inode == 0 || Output != default) throw Invalid();
            return;
        }
        // A known created inode may survive an uncertain final close. It is only
        // reconciliation evidence: no length, digest or publication facts survive.
        if (Facts != NativeSourceSnapshotFacts.None || Error is 0 or > 4095 || SourceRoot != default || ExchangeRoot != default
            || Output.Inode == 0 && Output.Device != 0 || Cleanup == NativeSourceSnapshotCleanup.None && Output != default) throw Invalid();
    }

    public override string ToString() => "Native source snapshot result values.";
    private static NativeSourceOwnerRoot Root(ReadOnlySpan<byte> bytes, int at) => new(U64(bytes, at), U64(bytes, at + 8));
    private static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);
    private static InvalidDataException Invalid() => new("Invalid bounded native source snapshot result.");
}
