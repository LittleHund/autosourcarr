using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AutoSourcarr.Core.Sources.Runtime;

/// <summary>
/// Pure directional codec for native/source-owner.h. Expected fields are caller
/// supplied message values, not an authenticated boot binding. No process, pipe,
/// source-root access, environment projection or readiness authority exists here.
/// </summary>
internal static class NativeSourceOwnerProtocol
{
    public const int NonceBytes = 32;
    public const int CommandBytes = 56;
    public const int ProofBytes = 76;
    public const int ResponseBytes = 120;
    public const int IncomingHeaderBytes = 64;
    public const int MaximumLogPayloadBytes = 448;
    public const int MaximumIncomingBytes = IncomingHeaderBytes + MaximumLogPayloadBytes;
    public const int MaximumPayloadBytes = 65536;
    public const int MaximumEnvironmentCount = 256;
    public const int MaximumKeyBytes = 128;
    public const int MaximumValueBytes = 8192;
    private const string SourceRoot = "/home/suwayomi/.local/share/Tachidesk";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static ulong NextRequestId(ulong previous)
    {
        if (previous == ulong.MaxValue) throw Invalid();
        return previous + 1;
    }

    /// <summary>
    /// Serializes one complete command. A future connection must own sequence
    /// allocation, private payload lifetime and the physical full-frame write.
    /// </summary>
    public static byte[] EncodeCommand(ReadOnlySpan<byte> nonce, ulong requestId, NativeSourceOwnerOpcode opcode,
        IReadOnlyList<KeyValuePair<string, string>>? environment = null)
    {
        ValidateNonce(nonce); ValidateRequest(requestId); ValidateOpcode(opcode);
        if (opcode is NativeSourceOwnerOpcode.Observe or NativeSourceOwnerOpcode.ReleaseSnapshot) throw Invalid();
        var entries = Snapshot(environment);
        if (opcode != NativeSourceOwnerOpcode.Start && entries.Length != 0) throw Invalid();
        var payloadBytes = entries.Length == 0 ? 0 : 2;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            var valueBytes = ValidateEntry(key, value);
            if (!keys.Add(key)) throw Invalid();
            payloadBytes += 6 + key.Length + valueBytes;
            if (payloadBytes > MaximumPayloadBytes) throw Invalid();
        }
        var frame = new byte[CommandBytes + payloadBytes];
        "ASOCMD1\n"u8.CopyTo(frame); nonce.CopyTo(frame.AsSpan(8, NonceBytes));
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(40), requestId);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(48), (ushort)opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(52), (uint)payloadBytes);
        if (payloadBytes == 0) return frame;
        var at = CommandBytes;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(at), (ushort)entries.Length); at += 2;
        foreach (var (key, value) in entries)
        {
            var count = Utf8.GetByteCount(value);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(at), (ushort)key.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(at + 2), (uint)count); at += 6;
            at += Encoding.ASCII.GetBytes(key, frame.AsSpan(at));
            at += Utf8.GetBytes(value, frame.AsSpan(at));
        }
        return frame;
    }

    /// <summary>Validates one exact frame; expectedRequestId is the caller-owned consecutive identity.</summary>
    public static NativeSourceOwnerCommandMessage DecodeCommand(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> expectedNonce, ulong expectedRequestId)
    {
        ValidateRequest(expectedRequestId);
        if (frame.Length < CommandBytes || frame.Length > CommandBytes + MaximumPayloadBytes) throw Invalid();
        ValidateMarkerAndNonce(frame, "ASOCMD1\n"u8, expectedNonce);
        var request = BinaryPrimitives.ReadUInt64LittleEndian(frame[40..]);
        var opcode = (NativeSourceOwnerOpcode)BinaryPrimitives.ReadUInt16LittleEndian(frame[48..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(frame[52..]);
        ValidateOpcode(opcode);
        if (request != expectedRequestId || BinaryPrimitives.ReadUInt16LittleEndian(frame[50..]) != 0
            || length > MaximumPayloadBytes || length != frame.Length - CommandBytes
            || (opcode is not (NativeSourceOwnerOpcode.Start or NativeSourceOwnerOpcode.Observe or NativeSourceOwnerOpcode.ReleaseSnapshot) && length != 0)) throw Invalid();
        if (opcode == NativeSourceOwnerOpcode.Observe) return new(request, opcode, [], NativeSourceObservationProtocol.DecodeObservePayload(frame[CommandBytes..]));
        if (opcode == NativeSourceOwnerOpcode.ReleaseSnapshot) return new(request, opcode, [], snapshotTicket: NativeSourceObservationProtocol.DecodeReleasePayload(frame[CommandBytes..]));
        KeyValuePair<string, string>[] entries = opcode == NativeSourceOwnerOpcode.Start ? DecodeEnvironment(frame[CommandBytes..]) : [];
        return new(request, opcode, entries);
    }

    internal static byte[] EncodeSnapshotCommand(ReadOnlySpan<byte> nonce, ulong requestId, NativeSourceOwnerOpcode opcode, ReadOnlySpan<byte> payload)
    {
        ValidateNonce(nonce); ValidateRequest(requestId);
        if (opcode == NativeSourceOwnerOpcode.Observe) _ = NativeSourceObservationProtocol.DecodeObservePayload(payload);
        else if (opcode == NativeSourceOwnerOpcode.ReleaseSnapshot) _ = NativeSourceObservationProtocol.DecodeReleasePayload(payload);
        else throw Invalid();
        var frame = new byte[CommandBytes + payload.Length];
        "ASOCMD1\n"u8.CopyTo(frame); nonce.CopyTo(frame.AsSpan(8, NonceBytes));
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(40), requestId);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(48), (ushort)opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(52), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(CommandBytes)); return frame;
    }

    /// <summary>
    /// Parses and compares every expected message field. It cannot establish who
    /// wrote a pipe, prove native credential changes, or authenticate this snapshot.
    /// </summary>
    public static NativeSourceOwnerProofMessage DecodeProof(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> expectedNonce,
        NativeSourceOwnerProofMessage expectedFields)
    {
        if (frame.Length != ProofBytes || expectedFields is null) throw Invalid();
        ValidateMarkerAndNonce(frame, "ASOPRF1\n"u8, expectedNonce);
        ValidateProofFields(expectedFields);
        var observed = new NativeSourceOwnerProofMessage(
            BinaryPrimitives.ReadUInt32LittleEndian(frame[40..]), BinaryPrimitives.ReadUInt32LittleEndian(frame[44..]),
            BinaryPrimitives.ReadUInt32LittleEndian(frame[48..]), BinaryPrimitives.ReadUInt32LittleEndian(frame[52..]),
            ReadRoot(frame[56..]), (NativeSourceOwnerProfile)BinaryPrimitives.ReadUInt32LittleEndian(frame[72..]));
        ValidateProofFields(observed);
        if (observed != expectedFields) throw Invalid();
        return observed;
    }

    public static NativeSourceOwnerResponseMessage DecodeResponse(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> expectedNonce,
        ulong expectedRequestId, NativeSourceOwnerOpcode expectedOpcode, NativeSourceOwnerRoot expectedRoot)
    {
        ValidateRequest(expectedRequestId); ValidateOpcode(expectedOpcode);
        if (frame.Length != ResponseBytes) throw Invalid();
        ValidateMarkerAndNonce(frame, "ASOREP1\n"u8, expectedNonce);
        var request = BinaryPrimitives.ReadUInt64LittleEndian(frame[40..]);
        var opcode = (NativeSourceOwnerOpcode)BinaryPrimitives.ReadUInt16LittleEndian(frame[48..]);
        var result = (NativeSourceOwnerResult)BinaryPrimitives.ReadUInt16LittleEndian(frame[50..]);
        var state = (NativeSourceOwnerState)BinaryPrimitives.ReadUInt32LittleEndian(frame[64..]);
        var leader = BinaryPrimitives.ReadUInt32LittleEndian(frame[68..]);
        var group = BinaryPrimitives.ReadUInt32LittleEndian(frame[72..]);
        var facts = (NativeSourceOwnerFacts)BinaryPrimitives.ReadUInt32LittleEndian(frame[80..]);
        var diagnostic = (NativeSourceOwnerDiagnostic)BinaryPrimitives.ReadUInt32LittleEndian(frame[92..]);
        var root = ReadRoot(frame[104..]);
        if (request != expectedRequestId || opcode != expectedOpcode || root != expectedRoot
            || BinaryPrimitives.ReadUInt32LittleEndian(frame[52..]) != 56
            || result is not (NativeSourceOwnerResult.Ok or NativeSourceOwnerResult.Unavailable or NativeSourceOwnerResult.Busy or NativeSourceOwnerResult.ProtocolError or NativeSourceOwnerResult.Failed)
            || state is not (NativeSourceOwnerState.Idle or NativeSourceOwnerState.Starting or NativeSourceOwnerState.Running or NativeSourceOwnerState.Stopping or NativeSourceOwnerState.Faulted)
            || diagnostic is not (NativeSourceOwnerDiagnostic.None or NativeSourceOwnerDiagnostic.Asset or NativeSourceOwnerDiagnostic.Root or NativeSourceOwnerDiagnostic.Protocol or NativeSourceOwnerDiagnostic.Exec or NativeSourceOwnerDiagnostic.Exit or NativeSourceOwnerDiagnostic.Signal or NativeSourceOwnerDiagnostic.Io or NativeSourceOwnerDiagnostic.Wait)
            || ((uint)facts & ~63U) != 0 || leader > int.MaxValue || group > int.MaxValue) throw Invalid();
        return new(request, opcode, result, BinaryPrimitives.ReadUInt64LittleEndian(frame[56..]), state,
            leader, group, BinaryPrimitives.ReadInt32LittleEndian(frame[76..]), facts,
            BinaryPrimitives.ReadUInt32LittleEndian(frame[84..]), BinaryPrimitives.ReadUInt32LittleEndian(frame[88..]),
            diagnostic, BinaryPrimitives.ReadUInt64LittleEndian(frame[96..]), root);
    }

    public static bool IsLogFrame(ReadOnlySpan<byte> frame) => frame.Length >= 8 && frame[..8].SequenceEqual("ASOLOG1\n"u8);
    /// <summary>Only chooses bounded framing; it does not certify a control response or process.</summary>
    public static int IncomingFrameBytes(ReadOnlySpan<byte> header, ReadOnlySpan<byte> expectedNonce)
    {
        if (header.Length != IncomingHeaderBytes) throw Invalid();
        if (IsLogFrame(header)) return IncomingHeaderBytes + DecodeLogHeader(header, expectedNonce).PayloadBytes;
        if (NativeSourceObservationProtocol.IsEvent(header)) { NativeSourceObservationProtocol.RequireHeader(header, expectedNonce); return NativeSourceObservationProtocol.EventBytes; }
        ValidateMarkerAndNonce(header, "ASOREP1\n"u8, expectedNonce);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[52..]) != 56) throw Invalid();
        return ResponseBytes;
    }
    /// <summary>Diagnostic values only; generation/offset never establish readiness or source identity.</summary>
    public static NativeSourceLogHeader DecodeLogHeader(ReadOnlySpan<byte> header, ReadOnlySpan<byte> expectedNonce)
    {
        if (header.Length != IncomingHeaderBytes) throw Invalid();
        ValidateMarkerAndNonce(header, "ASOLOG1\n"u8, expectedNonce);
        var generation = BinaryPrimitives.ReadUInt64LittleEndian(header[40..]);
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(header[48..]);
        var bytes = BinaryPrimitives.ReadUInt32LittleEndian(header[56..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(header[60..]);
        if (generation == 0 || bytes is 0 or > MaximumLogPayloadBytes || flags > 1 || offset > ulong.MaxValue - bytes) throw Invalid();
        return new(generation, offset, (int)bytes, flags == 1);
    }

    private static KeyValuePair<string, string>[] Snapshot(IReadOnlyList<KeyValuePair<string, string>>? environment)
    {
        if (environment is null) return [];
        var count = environment.Count;
        if (count is < 0 or > MaximumEnvironmentCount) throw Invalid();
        var result = new KeyValuePair<string, string>[count];
        for (var index = 0; index < count; index++) result[index] = environment[index];
        return result;
    }

    private static KeyValuePair<string, string>[] DecodeEnvironment(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) return [];
        if (payload.Length < 2) throw Invalid();
        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (count > MaximumEnvironmentCount) throw Invalid();
        var entries = new KeyValuePair<string, string>[count];
        var keys = new HashSet<string>(StringComparer.Ordinal); var at = 2;
        for (var index = 0; index < count; index++)
        {
            if (payload.Length - at < 6) throw Invalid();
            var keyBytes = BinaryPrimitives.ReadUInt16LittleEndian(payload[at..]);
            var valueBytes = BinaryPrimitives.ReadUInt32LittleEndian(payload[(at + 2)..]); at += 6;
            if (keyBytes is 0 or > MaximumKeyBytes || valueBytes > MaximumValueBytes
                || keyBytes > payload.Length - at || valueBytes > payload.Length - at - keyBytes) throw Invalid();
            var key = Encoding.ASCII.GetString(payload.Slice(at, keyBytes)); at += keyBytes;
            string value;
            try { value = Utf8.GetString(payload.Slice(at, (int)valueBytes)); }
            catch (DecoderFallbackException) { throw Invalid(); }
            at += (int)valueBytes;
            ValidateEntry(key, value);
            if (!keys.Add(key)) throw Invalid();
            entries[index] = new(key, value);
        }
        if (at != payload.Length) throw Invalid();
        return entries;
    }

    private static int ValidateEntry(string key, string value)
    {
        if (key is null || value is null || key.Length is 0 or > MaximumKeyBytes
            || value.Length > MaximumValueBytes || value.Contains('\0')) throw Invalid();
        for (var index = 0; index < key.Length; index++)
            if (key[index] is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not '_'
                && (index == 0 || key[index] is not (>= '0' and <= '9'))) throw Invalid();
        int count;
        try { count = Utf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw Invalid(); }
        if (count > MaximumValueBytes) throw Invalid();
        if (key is "HOME" or "PATH" or "JAVA_HOME" or "_JAVA_OPTIONS" or "JDK_JAVA_OPTIONS" or "CLASSPATH" or "BASH_ENV" or "ENV" or "SHELLOPTS"
            || key.StartsWith("LD_", StringComparison.Ordinal) || key.StartsWith("DOTNET_", StringComparison.Ordinal)
            || key.StartsWith("CORECLR_", StringComparison.Ordinal) || key.StartsWith("COMPlus_", StringComparison.Ordinal)
            || key.StartsWith("PYTHON", StringComparison.Ordinal)) throw Invalid();
        // This checks only the wire value. Native environment_parse must still
        // open and attest its fixed immutable agent; this codec cannot do so.
        if (key == "JAVA_TOOL_OPTIONS" && value != "-javaagent:/app/source-browser-agent.jar") throw Invalid();
        if (key.StartsWith("AUTOSOURCARR_", StringComparison.Ordinal))
        {
            switch (key)
            {
                case "AUTOSOURCARR_SOURCE_USER_AGENT":
                    if (count is 0 or > 1024 || value.Any(c => c is < (char)32 or > (char)126)) throw Invalid();
                    break;
                case "AUTOSOURCARR_SOURCE_UA_REVISION":
                    if (count != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw Invalid();
                    break;
                case "AUTOSOURCARR_SOURCE_UA_STATUS":
                    if (value != SourceRoot + "/cache/autosourcarr-user-agent.properties") throw Invalid();
                    break;
                default: throw Invalid();
            }
        }
        var forced = key switch
        {
            "BIND_IP" => "127.0.0.1", "BIND_PORT" => "4567", "AUTH_MODE" => "none",
            "BASIC_AUTH_ENABLED" or "WEB_UI_ENABLED" or "INITIAL_OPEN_IN_BROWSER_ENABLED" => "false",
            "KCEF_ENABLED" => "true", "LANG" => "C.UTF-8", _ => null
        };
        if (forced is not null && value != forced) throw Invalid();
        return count;
    }

    private static void ValidateProofFields(NativeSourceOwnerProofMessage fields)
    {
        if (fields.OwnerPid is 0 or > int.MaxValue || fields.ParentPid is 0 or > int.MaxValue
            || fields.Uid is 0 or uint.MaxValue || fields.Gid is 0 or uint.MaxValue
            || fields.Profile is not (NativeSourceOwnerProfile.Production or NativeSourceOwnerProfile.PhysicalTestOnly)) throw Invalid();
    }
    private static NativeSourceOwnerRoot ReadRoot(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]));
    private static void ValidateMarkerAndNonce(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> marker, ReadOnlySpan<byte> expectedNonce)
    {
        ValidateNonce(expectedNonce);
        if (!frame[..8].SequenceEqual(marker) || !CryptographicOperations.FixedTimeEquals(frame.Slice(8, NonceBytes), expectedNonce)) throw Invalid();
    }
    private static void ValidateNonce(ReadOnlySpan<byte> nonce) { if (nonce.Length != NonceBytes) throw Invalid(); }
    private static void ValidateRequest(ulong request) { if (request == 0) throw Invalid(); }
    private static void ValidateOpcode(NativeSourceOwnerOpcode opcode)
    {
        if (opcode is not (NativeSourceOwnerOpcode.Start or NativeSourceOwnerOpcode.Stop or NativeSourceOwnerOpcode.Status or NativeSourceOwnerOpcode.Shutdown or NativeSourceOwnerOpcode.PhysicalSelfTest or NativeSourceOwnerOpcode.Observe or NativeSourceOwnerOpcode.ReleaseSnapshot)) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("Invalid native source-owner message.");
}

internal readonly record struct NativeSourceLogHeader(ulong Generation, ulong RawOffset, int PayloadBytes, bool Gap);
