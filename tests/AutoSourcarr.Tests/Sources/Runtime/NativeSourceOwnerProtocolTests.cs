using System.Buffers.Binary;
using System.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Sources.Runtime;

namespace AutoSourcarr.Tests;

public static class NativeSourceOwnerProtocolTests
{
    // Literal wire vectors are independent of the managed codec. The private
    // source-linked C extraction verifies these against native helpers separately.
    private const string NonceHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private const string StatusHex = "41534f434d44310a" + NonceHex + "01000000000000000300000000000000";
    private const string StartHex = "41534f434d44310a" + NonceHex + "02000000000000000100000038000000"
        + "03000400070000004c414e47432e5554462d38"
        + "050004000000544f4b454e783dc3a9"
        + "07000900000042494e445f49503132372e302e302e31";
    private const string ProofHex = "41534f505246310a" + NonceHex
        + "040302014433221138020000e803000008070605040302011032547698badcfe00000000";
    private const string ResponseHex = "41534f524550310a" + NonceHex
        + "112233445566778802000000380000001032547698badcfe"
        + "000000007856341204030201f7ffffff27000000efcdab894030201000000000"
        + "efcdab896745230108070605040302011032547698badcfe";
    private static readonly byte[] Nonce = Convert.FromHexString(NonceHex);
    private static readonly NativeSourceOwnerRoot Root = new(0x0102030405060708UL, 0xfedcba9876543210UL);
    private static readonly NativeSourceOwnerProofMessage ExpectedProof = new(0x01020304, 0x11223344, 568, 1000, Root, NativeSourceOwnerProfile.Production);
    private const ulong ResponseRequest = 0x8877665544332211UL;

    public static void Register(TestSuite suite)
    {
        suite.Add("native owner protocol: literal command vectors retain exact native offsets and UTF8", GoldenCommands);
        suite.Add("native owner protocol: sequence never wraps and command headers bind the exact nonce and request", CommandHeaders);
        suite.Add("native owner protocol: fixed frames reject every truncation and trailing byte", FrameLengths);
        suite.Add("native owner protocol: START payload consumes its declared count and byte lengths exactly", PayloadLengths);
        suite.Add("native owner protocol: environment keys are bounded case sensitive native identifiers", EnvironmentKeys);
        suite.Add("native owner protocol: image forced values are exact and never silently rewritten", ForcedValues);
        suite.Add("native owner protocol: native injection exclusions retain exact case and prefix rules", InjectionExclusions);
        suite.Add("native owner protocol: UA map and fixed agent value preserve native textual constraints", UserAgentValues);
        suite.Add("native owner protocol: values use strict scalar UTF8 and reject NUL without normalizing", StrictUtf8);
        suite.Add("native owner protocol: entry value and total byte quotas accept the exact boundary", EnvironmentQuotas);
        suite.Add("native owner protocol: proof vector is a message with exact unsigned resolved IDs and root", GoldenProof);
        suite.Add("native owner protocol: proof fields profile and expected identity cannot drift", ProofIdentity);
        suite.Add("native owner protocol: response vector retains unsigned generation signed exit and cumulative counters", GoldenResponse);
        suite.Add("native owner protocol: only declared response enums and fact bits are accepted", ResponseEnums);
        suite.Add("native owner protocol: response correlation and observed root are exact", ResponseIdentity);
        suite.Add("native owner protocol: parsed observations remain immutable snapshots without readiness inference", ImmutableObservations);
        suite.Add("native owner protocol: validation errors never echo nonce environment or native identifiers", PrivateErrors);
        suite.Add("native owner protocol: empty START forms and physical opcode match the existing wire only", EmptyAndPhysical);
        suite.Add("native owner protocol:159 actual C source-log frames retain independent exact header values", SourceLogVectors);
        suite.Add("native owner protocol: source-log framing rejects wrong nonce lengths flags and overflow", SourceLogValidation);
    }

    internal static JsonObject SourceLogFixture()
    {
        var bytes = File.ReadAllBytes(ConfigurationTests.Fixture("native-source-logs.json"));
        Check.Equal("857b5a20e5f7aafbe1b2a7453da812fc5855f236085c9f75cec7d9eab0ce3df5", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return JsonNode.Parse(bytes)!.AsObject();
    }
    private static void SourceLogVectors()
    {
        var value = SourceLogFixture(); var nonce = Convert.FromHexString(value["nonce"]!.GetValue<string>()); var count = 0;
        Check.True(value["modeled_only"]!.GetValue<bool>() && !value["actual_linux_or_native_qualification"]!.GetValue<bool>());
        foreach (var vector in value["vectors"]!.AsArray())
        {
            var bytes = Convert.FromHexString(vector!["hex"]!.GetValue<string>()); var header = bytes.AsSpan(0, 64);
            Check.True(NativeSourceOwnerProtocol.IsLogFrame(bytes));
            Check.Equal(bytes.Length, NativeSourceOwnerProtocol.IncomingFrameBytes(header, nonce));
            var decoded = NativeSourceOwnerProtocol.DecodeLogHeader(header, nonce);
            Check.Equal(ulong.Parse(vector["generation"]!.GetValue<string>(), CultureInfo.InvariantCulture), decoded.Generation);
            Check.Equal(ulong.Parse(vector["raw_offset"]!.GetValue<string>(), CultureInfo.InvariantCulture), decoded.RawOffset);
            Check.Equal(vector["payload_bytes"]!.GetValue<int>(), decoded.PayloadBytes); Check.Equal(vector["gap"]!.GetValue<bool>(), decoded.Gap);
            Check.True(bytes.AsSpan(64).SequenceEqual(Convert.FromHexString(vector["payload_hex"]!.GetValue<string>())));
            Check.True(bytes.Length <= 512); count++;
        }
        Check.Equal(159, count);
        Check.Equal(120, NativeSourceOwnerProtocol.IncomingFrameBytes(Convert.FromHexString(ResponseHex).AsSpan(0, 64), Nonce));
    }
    private static void SourceLogValidation()
    {
        var value = SourceLogFixture(); var nonce = Convert.FromHexString(value["nonce"]!.GetValue<string>());
        var frame = Convert.FromHexString(value["vectors"]![0]!["hex"]!.GetValue<string>()); var header = frame[..64];
        for (var length = 0; length < 64; length++)
        {
            var shortened = header[..length];
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeLogHeader(shortened, nonce));
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.IncomingFrameBytes(shortened, nonce));
        }
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeLogHeader(new byte[65], nonce));
        foreach (var index in new[] { 0, 7, 8, 39 })
        {
            var changed = (byte[])header.Clone(); changed[index] ^= 0x80;
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeLogHeader(changed, nonce));
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.IncomingFrameBytes(changed, nonce));
        }
        foreach (var size in new uint[] { 0, 449, uint.MaxValue })
        {
            var changed = (byte[])header.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(56), size);
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeLogHeader(changed, nonce));
        }
        foreach (var flag in new uint[] { 2, uint.MaxValue })
        {
            var changed = (byte[])header.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(60), flag);
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeLogHeader(changed, nonce));
        }
        var zero = (byte[])header.Clone(); BinaryPrimitives.WriteUInt64LittleEndian(zero.AsSpan(40), 0);
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeLogHeader(zero, nonce));
        var overflow = (byte[])header.Clone(); BinaryPrimitives.WriteUInt64LittleEndian(overflow.AsSpan(48), ulong.MaxValue);
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeLogHeader(overflow, nonce));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeLogHeader(header, new byte[31]));
    }

    private static void GoldenCommands()
    {
        Check.True(Convert.FromHexString(StatusHex).SequenceEqual(NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Status)));
        var entries = Map(("LANG", "C.UTF-8"), ("TOKEN", "x=é"), ("BIND_IP", "127.0.0.1"));
        Check.True(Convert.FromHexString(StartHex).SequenceEqual(NativeSourceOwnerProtocol.EncodeCommand(Nonce, 2, NativeSourceOwnerOpcode.Start, entries)));
        var command = NativeSourceOwnerProtocol.DecodeCommand(Convert.FromHexString(StartHex), Nonce, 2);
        Check.Equal(2UL, command.RequestId); Check.Equal(NativeSourceOwnerOpcode.Start, command.Opcode);
        Check.True(entries.SequenceEqual(command.Environment));
    }

    private static void CommandHeaders()
    {
        Check.Equal(1UL, NativeSourceOwnerProtocol.NextRequestId(0));
        Check.Equal(ulong.MaxValue, NativeSourceOwnerProtocol.NextRequestId(ulong.MaxValue - 1));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.NextRequestId(ulong.MaxValue));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce, 0, NativeSourceOwnerOpcode.Status));
        var final = NativeSourceOwnerProtocol.EncodeCommand(Nonce, ulong.MaxValue, NativeSourceOwnerOpcode.Status);
        Check.Equal(ulong.MaxValue, NativeSourceOwnerProtocol.DecodeCommand(final, Nonce, ulong.MaxValue).RequestId);
        foreach (var size in new[] { 0, 31, 33 })
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(new byte[size], 1, NativeSourceOwnerOpcode.Status));
        var frame = Convert.FromHexString(StatusHex);
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(frame, Nonce, 0));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(frame, Nonce, 2));
        for (var index = 0; index < 40; index++)
        {
            var altered = (byte[])frame.Clone(); altered[index] ^= 0x80;
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(altered, Nonce, 1));
        }
        foreach (var opcode in new ushort[] { 0, 6, ushort.MaxValue })
        {
            var altered = Alter(frame, b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(48), opcode));
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(altered, Nonce, 1));
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, (NativeSourceOwnerOpcode)opcode));
        }
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(Alter(frame, b => b[50] = 1), Nonce, 1));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(Alter(frame, b => b[51] = 1), Nonce, 1));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(Alter(frame, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(52), 65537)), Nonce, 1));
    }

    private static void FrameLengths()
    {
        var status = Convert.FromHexString(StatusHex); var proof = Convert.FromHexString(ProofHex); var response = Convert.FromHexString(ResponseHex);
        for (var length = 0; length < status.Length; length++)
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(status[..length], Nonce, 1));
        for (var length = 0; length < proof.Length; length++)
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeProof(proof[..length], Nonce, ExpectedProof));
        for (var length = 0; length < response.Length; length++)
            Check.Throws<InvalidDataException>(() => Response(response[..length]));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand([.. status, 0], Nonce, 1));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeProof([.. proof, 0], Nonce, ExpectedProof));
        Check.Throws<InvalidDataException>(() => Response([.. response, 0]));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(new byte[NativeSourceOwnerProtocol.CommandBytes + NativeSourceOwnerProtocol.MaximumPayloadBytes + 1], Nonce, 1));
    }

    private static void PayloadLengths()
    {
        var valid = Convert.FromHexString(StartHex);
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(Alter(valid, b => b[52]--), Nonce, 2));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(Alter(valid, b => b[52]++), Nonce, 2));
        foreach (var payload in new byte[][] { [0], [1, 0], [0, 0, 1], [1, 0, 1, 0, 0, 0, 0], [1, 0, 1, 0, 0xff, 0xff, 0xff, 0xff, (byte)'K'] })
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(RawStart(payload), Nonce, 2));
        var duplicate = RawEnvironment(("X", Encoding.UTF8.GetBytes("one")), ("X", Encoding.UTF8.GetBytes("two")));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(duplicate, Nonce, 2));
        var surplus = RawStart([0, 0, 0]);
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(surplus, Nonce, 2));
        foreach (var opcode in new[] { NativeSourceOwnerOpcode.Stop, NativeSourceOwnerOpcode.Status, NativeSourceOwnerOpcode.Shutdown, NativeSourceOwnerOpcode.PhysicalSelfTest })
        {
            var other = Alter(valid, b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(48), (ushort)opcode));
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(other, Nonce, 2));
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce, 2, opcode, Map(("X", "value"))));
        }
    }

    private static void EnvironmentKeys()
    {
        foreach (var key in new[] { "", "1KEY", "KEY-NAME", "KEY=VALUE", "K\0Y", "KéY", new string('A', 129) })
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, Map((key, "value"))));
        var valid = Map(("_", ""), ("key", "lower"), ("KEY", "upper"), (new string('A', 128), "long"), ("A_09z", "ok"));
        var parsed = NativeSourceOwnerProtocol.DecodeCommand(NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, valid), Nonce, 1);
        Check.True(valid.SequenceEqual(parsed.Environment));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, Map(("X", "a"), ("X", "b"))));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, [new("X", null!)]));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, [new(null!, "value")]));
    }

    private static void ForcedValues()
    {
        foreach (var (key, value) in new[] { ("BIND_IP", "127.0.0.1"), ("BIND_PORT", "4567"), ("AUTH_MODE", "none"), ("BASIC_AUTH_ENABLED", "false"), ("WEB_UI_ENABLED", "false"), ("INITIAL_OPEN_IN_BROWSER_ENABLED", "false"), ("KCEF_ENABLED", "true"), ("LANG", "C.UTF-8") })
        {
            var frame = NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, Map((key, value)));
            Check.Equal(value, NativeSourceOwnerProtocol.DecodeCommand(frame, Nonce, 1).Environment.Single().Value);
            foreach (var wrong in new[] { "", value + " ", value.ToUpperInvariant(), "other" }.Where(v => v != value))
                Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, Map((key, wrong))));
        }
    }

    private static void InjectionExclusions()
    {
        foreach (var key in new[] { "HOME", "PATH", "JAVA_HOME", "LD_PRELOAD", "LD_", "DOTNET_ROOT", "CORECLR_ENABLE_PROFILING", "COMPlus_gcServer", "PYTHON", "PYTHONPATH", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "CLASSPATH", "BASH_ENV", "ENV", "SHELLOPTS", "AUTOSOURCARR_SOURCE_DATA_FD", "AUTOSOURCARR_OTHER" })
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, Map((key, "value"))));
        var allowed = Map(("home", "relative"), ("ld_preload", "ordinary"), ("COMPLUS_gcServer", "ordinary"), ("PythonPath", "ordinary"), ("ENV1", "ordinary"), ("TOKEN", "private-value"));
        Check.Equal(allowed.Length, NativeSourceOwnerProtocol.DecodeCommand(NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, allowed), Nonce, 1).Environment.Count);
    }

    private static void UserAgentValues()
    {
        foreach (var value in new[] { "", "line\nvalue", "tab\tvalue", "café", new string('a', 1025), "\u007f" })
            Refuse("AUTOSOURCARR_SOURCE_USER_AGENT", value);
        foreach (var value in new[] { " ", new string('~', 1024) }) Accept("AUTOSOURCARR_SOURCE_USER_AGENT", value);
        foreach (var value in new[] { "", new string('a', 63), new string('a', 65), new string('A', 64), new string('g', 64) })
            Refuse("AUTOSOURCARR_SOURCE_UA_REVISION", value);
        Accept("AUTOSOURCARR_SOURCE_UA_REVISION", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        const string status = "/home/suwayomi/.local/share/Tachidesk/cache/autosourcarr-user-agent.properties";
        Accept("AUTOSOURCARR_SOURCE_UA_STATUS", status); Refuse("AUTOSOURCARR_SOURCE_UA_STATUS", status + "/../other");
        Accept("JAVA_TOOL_OPTIONS", "-javaagent:/app/source-browser-agent.jar");
        foreach (var value in new[] { "", "-javaagent:/other.jar", "-javaagent:/app/source-browser-agent.jar -Xmx1g", "-javaagent:/app/source-browser-agent.jar\n" }) Refuse("JAVA_TOOL_OPTIONS", value);
        // Textual acceptance does not assert that the immutable agent exists/trusts.
    }

    private static void StrictUtf8()
    {
        foreach (var value in new[] { "", "a=b", "\r\n\t\u0001", "é🙂\uffff", "\U0010ffff" }) Accept("VALUE", value);
        foreach (var value in new[] { "\0", "before\0after", "\ud800", "\udc00" }) Refuse("VALUE", value);
        foreach (var value in new byte[][] { [0], [0x80], [0xc0, 0xaf], [0xc2], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xf5, 0x80, 0x80, 0x80] })
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(RawEnvironment(("VALUE", value)), Nonce, 2));
    }

    private static void EnvironmentQuotas()
    {
        var maximumCount = Enumerable.Range(0, 256).Select(i => new KeyValuePair<string, string>("K" + i, "")).ToArray();
        Check.Equal(256, NativeSourceOwnerProtocol.DecodeCommand(NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, maximumCount), Nonce, 1).Environment.Count);
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, [.. maximumCount, new("EXTRA", "")]));
        var count257 = RawStart([1, 1]);
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeCommand(count257, Nonce, 2));
        Accept("VALUE", new string('a', 8192)); Refuse("VALUE", new string('a', 8193));
        Accept("VALUE", new string('é', 4096)); Refuse("VALUE", new string('é', 4097));
        var exact = Enumerable.Range(0, 8).Select(i => new KeyValuePair<string, string>("K" + i, new string('x', i == 7 ? 8126 : 8192))).ToArray();
        var frame = NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, exact);
        Check.Equal(65536 + 56, frame.Length);
        Check.Equal(8, NativeSourceOwnerProtocol.DecodeCommand(frame, Nonce, 1).Environment.Count);
        exact[7] = new("K7", new string('x', 8127));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, exact));
    }

    private static void GoldenProof()
    {
        var frame = Convert.FromHexString(ProofHex);
        Check.Equal(76, frame.Length);
        Check.Equal(ExpectedProof, NativeSourceOwnerProtocol.DecodeProof(frame, Nonce, ExpectedProof));
        var testOnly = ExpectedProof with { Profile = NativeSourceOwnerProfile.PhysicalTestOnly };
        var testFrame = Alter(frame, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(72), 1));
        Check.Equal(testOnly, NativeSourceOwnerProtocol.DecodeProof(testFrame, Nonce, testOnly));
        var wideIds = ExpectedProof with { Uid = uint.MaxValue - 1, Gid = uint.MaxValue - 1 };
        var wideFrame = Alter(frame, b => { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(48), uint.MaxValue - 1); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(52), uint.MaxValue - 1); });
        Check.Equal(wideIds, NativeSourceOwnerProtocol.DecodeProof(wideFrame, Nonce, wideIds));
        // Resolved wire uint32s never replace SourceStartupRequest's raw signed/wide request.
    }

    private static void ProofIdentity()
    {
        var frame = Convert.FromHexString(ProofHex);
        for (var index = 0; index < 76; index++)
        {
            var altered = Alter(frame, b => b[index] ^= 0x80);
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeProof(altered, Nonce, ExpectedProof));
        }
        foreach (var offset in new[] { 40, 44, 48, 52 })
            foreach (var value in new[] { 0U, uint.MaxValue })
            {
                var altered = Alter(frame, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), value));
                var expected = offset switch
                {
                    40 => ExpectedProof with { OwnerPid = value }, 44 => ExpectedProof with { ParentPid = value },
                    48 => ExpectedProof with { Uid = value }, _ => ExpectedProof with { Gid = value }
                };
                Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeProof(altered, Nonce, expected));
            }
        foreach (var profile in new[] { 2U, uint.MaxValue })
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeProof(Alter(frame, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(72), profile)), Nonce, ExpectedProof with { Profile = (NativeSourceOwnerProfile)profile }));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeProof(frame, Nonce, ExpectedProof with { OwnerPid = 0 }));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeProof(frame, Nonce, ExpectedProof with { Profile = (NativeSourceOwnerProfile)2 }));
    }

    private static void GoldenResponse()
    {
        var response = Response(Convert.FromHexString(ResponseHex));
        Check.Equal(ResponseRequest, response.RequestId); Check.Equal(NativeSourceOwnerOpcode.Stop, response.Opcode);
        Check.Equal(NativeSourceOwnerResult.Ok, response.Result); Check.Equal(0xfedcba9876543210UL, response.Generation);
        Check.Throws<OverflowException>(() => { _ = checked((long)response.Generation); });
        Check.Equal(NativeSourceOwnerState.Idle, response.State); Check.Equal(0x12345678U, response.LeaderPid); Check.Equal(0x01020304U, response.ProcessGroupId);
        Check.Equal(-9, response.ExitStatus); Check.Equal((NativeSourceOwnerFacts)39, response.Facts);
        Check.Equal(0x89abcdefU, response.Reaped); Check.Equal(0x10203040U, response.AdoptedReaped);
        Check.Equal(0x0123456789abcdefUL, response.DiscardedLogBytes); Check.Equal(Root, response.Root);
        var maximum = Response(Alter(Convert.FromHexString(ResponseHex), b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(56), ulong.MaxValue)));
        Check.Equal(ulong.MaxValue, maximum.Generation);
    }

    private static void ResponseEnums()
    {
        var frame = Convert.FromHexString(ResponseHex);
        for (uint state = 0; state <= 4; state++)
            Check.Equal((NativeSourceOwnerState)state, Response(Alter(frame, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(64), state))).State);
        for (ushort result = 0; result <= 4; result++)
            Check.Equal((NativeSourceOwnerResult)result, Response(Alter(frame, b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(50), result))).Result);
        for (uint diagnostic = 0; diagnostic <= 8; diagnostic++)
            Check.Equal((NativeSourceOwnerDiagnostic)diagnostic, Response(Alter(frame, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(92), diagnostic))).Diagnostic);
        for (uint facts = 0; facts <= 63; facts++)
            Check.Equal((NativeSourceOwnerFacts)facts, Response(Alter(frame, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(80), facts))).Facts);
        foreach (var (offset, value) in new[] { (64, 5U), (64, uint.MaxValue), (80, 64U), (80, uint.MaxValue), (92, 9U), (92, uint.MaxValue), (68, 0x80000000U), (72, uint.MaxValue) })
            Check.Throws<InvalidDataException>(() => Response(Alter(frame, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), value))));
        foreach (var result in new ushort[] { 5, ushort.MaxValue })
            Check.Throws<InvalidDataException>(() => Response(Alter(frame, b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(50), result))));
    }

    private static void ResponseIdentity()
    {
        var frame = Convert.FromHexString(ResponseHex);
        foreach (var index in Enumerable.Range(0, 50).Concat(Enumerable.Range(104, 16)))
            Check.Throws<InvalidDataException>(() => Response(Alter(frame, b => b[index] ^= 0x80)));
        foreach (var size in new[] { 0U, 55U, 57U, uint.MaxValue })
            Check.Throws<InvalidDataException>(() => Response(Alter(frame, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(52), size))));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeResponse(frame, Nonce, 0, NativeSourceOwnerOpcode.Stop, Root));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeResponse(frame, Nonce, ResponseRequest, (NativeSourceOwnerOpcode)6, Root));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeResponse(frame, Nonce, ResponseRequest, NativeSourceOwnerOpcode.Start, Root));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeResponse(frame, new byte[31], ResponseRequest, NativeSourceOwnerOpcode.Stop, Root));
    }

    private static void ImmutableObservations()
    {
        var frame = Convert.FromHexString(StartHex); var parsed = NativeSourceOwnerProtocol.DecodeCommand(frame, Nonce, 2);
        Array.Clear(frame); Check.Equal("x=é", parsed.Environment[1].Value);
        Check.True(parsed.Environment is not KeyValuePair<string, string>[]);
        if (parsed.Environment is IList<KeyValuePair<string, string>> mutable)
            Check.Throws<NotSupportedException>(() => mutable[0] = new("CHANGED", "changed"));
        var responseFrame = Alter(Convert.FromHexString(ResponseHex), b => { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(64), 2); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(80), 0); });
        var response = Response(responseFrame); Array.Clear(responseFrame);
        Check.Equal(NativeSourceOwnerState.Running, response.State); Check.Equal(NativeSourceOwnerFacts.None, response.Facts);
        // RUNNING/OK with no drain facts is retained as observed, never promoted to ready or safe-to-resume.
        var proofFrame = Convert.FromHexString(ProofHex); var proof = NativeSourceOwnerProtocol.DecodeProof(proofFrame, Nonce, ExpectedProof); Array.Clear(proofFrame);
        Check.Equal(ExpectedProof, proof);
    }

    private static void PrivateErrors()
    {
        const string secret = "synthetic-private-value-never-echo";
        var failures = new List<Exception>
        {
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, Map(("HOME", secret)))),
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, Map((secret, "\ud800")))),
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeProof(Convert.FromHexString(ProofHex), new byte[32], ExpectedProof)),
            Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.DecodeResponse(Convert.FromHexString(ResponseHex), Nonce, 1, NativeSourceOwnerOpcode.Stop, Root))
        };
        foreach (var failure in failures)
        {
            Check.True(!failure.ToString().Contains(secret, StringComparison.Ordinal));
            Check.True(!failure.ToString().Contains(NonceHex, StringComparison.OrdinalIgnoreCase));
            Check.True(!failure.ToString().Contains(ResponseRequest.ToString(), StringComparison.Ordinal));
        }
    }

    private static void EmptyAndPhysical()
    {
        Check.Equal(56, NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start).Length);
        Check.Equal(0, NativeSourceOwnerProtocol.DecodeCommand(RawStart([]), Nonce, 2).Environment.Count);
        Check.Equal(0, NativeSourceOwnerProtocol.DecodeCommand(RawStart([0, 0]), Nonce, 2).Environment.Count);
        foreach (var opcode in new[] { NativeSourceOwnerOpcode.Start, NativeSourceOwnerOpcode.Stop, NativeSourceOwnerOpcode.Status, NativeSourceOwnerOpcode.Shutdown, NativeSourceOwnerOpcode.PhysicalSelfTest })
        {
            var frame = NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, opcode);
            Check.Equal(opcode, NativeSourceOwnerProtocol.DecodeCommand(frame, Nonce, 1).Opcode);
        }
        // Opcode5 remains only a typed wire value, not a production launch API or invented observer operation.
    }

    private static NativeSourceOwnerResponseMessage Response(byte[] frame) => NativeSourceOwnerProtocol.DecodeResponse(frame, Nonce, ResponseRequest, NativeSourceOwnerOpcode.Stop, Root);
    private static KeyValuePair<string, string>[] Map(params (string Key, string Value)[] entries) => entries.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)).ToArray();
    private static void Accept(string key, string value)
    {
        var frame = NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, Map((key, value)));
        Check.Equal(value, NativeSourceOwnerProtocol.DecodeCommand(frame, Nonce, 1).Environment.Single().Value);
    }
    private static void Refuse(string key, string value) => Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce, 1, NativeSourceOwnerOpcode.Start, Map((key, value))));
    private static byte[] Alter(byte[] input, Action<byte[]> change) { var copy = (byte[])input.Clone(); change(copy); return copy; }
    private static byte[] RawStart(byte[] payload)
    {
        var header = Convert.FromHexString(StatusHex); header[40] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(48), 1); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), (uint)payload.Length);
        return [.. header, .. payload];
    }
    private static byte[] RawEnvironment(params (string Key, byte[] Value)[] entries)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)entries.Length);
        foreach (var (key, value) in entries)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key); writer.Write((ushort)keyBytes.Length); writer.Write((uint)value.Length); writer.Write(keyBytes); writer.Write(value);
        }
        return RawStart(output.ToArray());
    }
}
