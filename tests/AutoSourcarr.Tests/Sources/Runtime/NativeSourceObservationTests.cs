using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Sources.Runtime;

namespace AutoSourcarr.Tests;

public static class NativeSourceObservationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("native snapshots: actual C results retain exact hash metadata and distinct failures", ActualResults);
        suite.Add("native snapshots: unknown enums reserved bytes and truncated records fail closed", Framing);
        suite.Add("native snapshots: JAR and user-agent size boundaries remain distinct", Bounds);
        suite.Add("native snapshots: readable source ownership is independent from immutable output ownership", Ownership);
        suite.Add("native snapshots: missing and failed results cannot claim published source evidence", FailureFacts);
        suite.Add("native snapshots: uncertain close retains reconciliation identity without a readable snapshot", Reconciliation);
        suite.Add("snapshot protocol: commands match actual C accepted request bytes", Commands);
        suite.Add("snapshot protocol: bounded event framing remains separate from logs and environment commands", EventFraming);
        suite.Add("snapshot protocol: actual C parent events retain process outcome and cleanup ownership", ParentEvents);
        suite.Add("snapshot protocol: event context nonce generation root account and ticket are checked", EventContext);
        suite.Add("snapshot protocol: successful bytes cannot bypass actual parent EOF reap and exit facts", PhysicalFacts);
        suite.Add("snapshot protocol: only fixed bounded JAR or empty retained-UA requests are encoded", Targets);
        suite.Add("snapshot protocol: release carries one exact native-issued ticket", ReleaseTargets);
        suite.Add("snapshot protocol: diagnostic values omit private ticket and source evidence", PrivateStrings);
    }

    private static JsonObject Corpus() => JsonNode.Parse(File.ReadAllBytes(ConfigurationTests.Fixture("native-source-observation.json")))!.AsObject();
    private static IEnumerable<(string Name, byte[] Bytes)> Vectors() => Corpus()["vectors"]!.AsArray()
        .Select(row => (row!["vector"]!.GetValue<string>(), Convert.FromHexString(row["hex"]!.GetValue<string>())));
    private static byte[] Complete() => Vectors().First(row => row.Name == "complete-jar-16390-0").Bytes;
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Put64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    private static void Reject(byte[] original, int offset, uint value)
    { var changed = (byte[])original.Clone(); Put(changed, offset, value); Check.Throws<InvalidDataException>(() => NativeSourceSnapshotResult.Decode(changed)); }

    private static void ActualResults()
    {
        var corpus = Corpus(); Check.Equal(176, corpus["result_bytes"]!.GetValue<int>());
        Check.True(corpus["provenance"]!["actual_c_body"]!.GetValue<bool>());
        Check.True(corpus["provenance"]!["modeled_syscalls"]!.GetValue<bool>());
        Check.True(!corpus["provenance"]!["linux_execution"]!.GetValue<bool>());
        foreach (var (name, bytes) in Vectors())
        {
            var result = NativeSourceSnapshotResult.Decode(bytes);
            if (name.StartsWith("complete-", StringComparison.Ordinal))
            {
                Check.Equal(NativeSourceSnapshotOutcome.Complete, result.Outcome);
                var data = new byte[checked((int)result.Length)];
                for (var index = 0; index < data.Length; index++) data[index] = unchecked((byte)(index * 17 + 3));
                Check.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), result.Sha256);
                Check.Equal(new NativeSourceOwnerRoot(5, 105), result.Output);
                Check.Equal(new NativeSourceOwnerRoot(5, 104), result.SourceFile);
                Check.Equal(new NativeSourceOwnerRoot(5, 103), result.SourceDirectory);
            }
            else if (name.StartsWith("missing-", StringComparison.Ordinal)) Check.Equal(NativeSourceSnapshotOutcome.Missing, result.Outcome);
            else Check.True(result.Outcome is NativeSourceSnapshotOutcome.Changed or NativeSourceSnapshotOutcome.Failed);
        }
        Check.Equal(95, Vectors().Count());
        var current = OwnershipCorpus();
        Check.Equal(current["original_capture_fixture_sha256"]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ConfigurationTests.Fixture("native-source-observation.json")))));
        Check.JsonEqual(System.Text.Json.JsonSerializer.SerializeToElement(corpus["vectors"]), System.Text.Json.JsonSerializer.SerializeToElement(current["capture_vectors"]));
        var startup = StartupCorpus();
        Check.Equal("d26fe8a49e06ce985d03a122e832af7b37d652b713a83aab1fac249ce710dad6", startup["previous_ownership_fixture_sha256"]!.GetValue<string>());
        Check.True(!startup["verification"]!["linux_execution"]!.GetValue<bool>());
        foreach (var file in new[] { "source-observation.c", "source-observation.h", "source-owner.c", "source-owner.h", "container-entry.c" })
            Check.Equal(startup["source_hashes"]![file]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "NativeInputs", file)))));
    }

    private static void Framing()
    {
        var bytes = Complete();
        foreach (var length in new[] { 0, 1, 55, 64, 175, 177, 512 })
            Check.Throws<InvalidDataException>(() => NativeSourceSnapshotResult.Decode(bytes.Take(length).Concat(new byte[Math.Max(0, length - bytes.Length)]).ToArray()));
        foreach (var offset in new[] { 0, 4, 8, 168, 172 }) foreach (var value in new[] { 0U, 5U, 16U, uint.MaxValue })
        {
            if (offset == 168 && value == 0 || offset == 172 && value == 0) continue;
            Reject(bytes, offset, value);
        }
    }

    private static void Bounds()
    {
        var bytes = Complete(); Put64(bytes, 16, 0); Check.Throws<InvalidDataException>(() => NativeSourceSnapshotResult.Decode(bytes));
        Put64(bytes, 16, NativeSourceSnapshotResult.JarMaximumBytes); Check.Equal(NativeSourceSnapshotResult.JarMaximumBytes, NativeSourceSnapshotResult.Decode(bytes).Length);
        Put64(bytes, 16, NativeSourceSnapshotResult.JarMaximumBytes + 1); Check.Throws<InvalidDataException>(() => NativeSourceSnapshotResult.Decode(bytes));
        Put(bytes, 4, 2); Put64(bytes, 16, 0); Check.Equal(0UL, NativeSourceSnapshotResult.Decode(bytes).Length);
        Put64(bytes, 16, NativeSourceSnapshotResult.UserAgentMaximumBytes); Check.Equal(NativeSourceSnapshotResult.UserAgentMaximumBytes, NativeSourceSnapshotResult.Decode(bytes).Length);
        Put64(bytes, 16, NativeSourceSnapshotResult.UserAgentMaximumBytes + 1); Check.Throws<InvalidDataException>(() => NativeSourceSnapshotResult.Decode(bytes));
        Put64(bytes, 16, ulong.MaxValue); Check.Throws<InvalidDataException>(() => NativeSourceSnapshotResult.Decode(bytes));
    }

    private static void Ownership()
    {
        var bytes = Complete(); Check.Equal(0U, NativeSourceSnapshotResult.Decode(bytes).SourceUid);
        foreach (var value in new[] { 0U, 568U, 1000U, uint.MaxValue })
        { Put(bytes, 152, value); Put(bytes, 156, value); Check.Equal(value, NativeSourceSnapshotResult.Decode(bytes).SourceUid); }
        foreach (var offset in new[] { 136, 140 }) foreach (var value in new[] { 0U, uint.MaxValue }) Reject(bytes, offset, value);
        foreach (var mode in new[] { 0x8180U, 0x8126U, 0xa124U, 0x4124U, 0x18924U }) Reject(bytes, 144, mode);
        Reject(bytes, 148, 0); Reject(bytes, 148, 2); Reject(bytes, 164, 2);
    }

    private static void FailureFacts()
    {
        foreach (var (_, bytes) in Vectors().Where(row => NativeSourceSnapshotResult.Decode(row.Bytes).Outcome != NativeSourceSnapshotOutcome.Complete))
        {
            Reject(bytes, 8, 15); Reject(bytes, 16, 1); Reject(bytes, 24, 1); Reject(bytes, 144, 0x8124);
            Reject(bytes, 104, 1); Reject(bytes, 160, 0x81a4);
        }
        var missing = Vectors().First(row => row.Name == "missing-file").Bytes;
        Reject(missing, 12, 1); Reject(missing, 168, 2); Reject(missing, 96, 1);
    }

    private static void Reconciliation()
    {
        var values = Vectors().Select(row => NativeSourceSnapshotResult.Decode(row.Bytes))
            .Where(row => row.Cleanup == NativeSourceSnapshotCleanup.Uncertain).ToArray();
        Check.True(values.Any(row => row.Output.Inode == 105));
        Check.True(values.Any(row => row.Output.Inode == 0));
        foreach (var result in values)
        { Check.True(result.Outcome != NativeSourceSnapshotOutcome.Complete); Check.Equal(0UL, result.Length); Check.Equal(NativeSourceSnapshotFacts.None, result.Facts); }
        var complete = NativeSourceSnapshotResult.Decode(Complete());
        Check.True(!complete.ToString().Contains(complete.Sha256, StringComparison.Ordinal));
    }

    private static JsonObject OwnershipCorpus()
    {
        var bytes = File.ReadAllBytes(ConfigurationTests.Fixture("native-source-observation-ownership.json"));
        Check.Equal("d26fe8a49e06ce985d03a122e832af7b37d652b713a83aab1fac249ce710dad6", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return JsonNode.Parse(bytes)!.AsObject();
    }
    private static JsonObject StartupCorpus()
    {
        var bytes = File.ReadAllBytes(ConfigurationTests.Fixture("native-source-observation-startup.json"));
        Check.Equal("288527493ba36f4dbea78628b4a133261dabb8708520afb00bce27a0bfd9a119", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return JsonNode.Parse(bytes)!.AsObject();
    }
    private static byte[] ParentVector(string name) => Convert.FromHexString(OwnershipCorpus()["parent_vectors"]!.AsArray().Single(row => row!["vector"]!.GetValue<string>() == name)!["hex"]!.GetValue<string>());
    private static byte[] Nonce() => Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    private static NativeSourceSnapshotRequest Request() => new(1, NativeSourceSnapshotKind.InstalledJar, "test.jar");
    private static NativeSourceOwnerProofMessage Owner() => new(500, 1, 1000, 1000, new(5, 101), NativeSourceOwnerProfile.Production);
    private static NativeSourceObservationMessage Event(byte[] bytes) => NativeSourceObservationProtocol.DecodeEvent(bytes, Nonce(), 73, Request(), Owner());
    private static void RejectEvent(byte[] original, int offset, uint value)
    { var changed = (byte[])original.Clone(); Put(changed, offset, value); Check.Throws<InvalidDataException>(() => Event(changed)); }

    private static void Commands()
    {
        var observe = NativeSourceObservationProtocol.EncodeObserve(Nonce(), 1, Request());
        Check.True(observe.SequenceEqual(ParentVector("observe-command")));
        var decoded = NativeSourceOwnerProtocol.DecodeCommand(observe, Nonce(), 1);
        Check.Equal(NativeSourceOwnerOpcode.Observe, decoded.Opcode); Check.Equal(Request(), decoded.Observation!); Check.Equal(0, decoded.Environment.Count);
        var ua = new NativeSourceSnapshotRequest(0, NativeSourceSnapshotKind.RetainedUserAgent, "");
        var command = NativeSourceObservationProtocol.EncodeObserve(Nonce(), 1, ua);
        Check.True(command.SequenceEqual(ParentVector("ua-command")));
        Check.Equal(ua, NativeSourceOwnerProtocol.DecodeCommand(command, Nonce(), 1).Observation!);
        var release = NativeSourceObservationProtocol.EncodeRelease(Nonce(), 1, "0123456789abcdef0123456789abcdef");
        Check.True(release.SequenceEqual(ParentVector("release-command")));
        Check.Equal("0123456789abcdef0123456789abcdef", NativeSourceOwnerProtocol.DecodeCommand(release, Nonce(), 1).SnapshotTicket!);
    }
    private static void EventFraming()
    {
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce(), 1, NativeSourceOwnerOpcode.Observe));
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(Nonce(), 1, NativeSourceOwnerOpcode.ReleaseSnapshot));
        foreach (var name in new[] { "complete-event", "timeout-event", "truncated-event" })
        {
            var frame = ParentVector(name); Check.True(NativeSourceObservationProtocol.IsEvent(frame)); Check.True(!NativeSourceOwnerProtocol.IsLogFrame(frame));
            Check.Equal(280, NativeSourceOwnerProtocol.IncomingFrameBytes(frame.AsSpan(0, 64), Nonce()));
            foreach (var length in new[] { 0, 63, 64, 279, 281, 512 })
            { var partial = frame.Take(length).Concat(new byte[Math.Max(0, length - frame.Length)]).ToArray(); Check.Throws<InvalidDataException>(() => Event(partial)); }
        }
    }
    private static void ParentEvents()
    {
        var complete = Event(ParentVector("complete-event")); Check.Equal(73UL, complete.RequestId); Check.Equal(1UL, complete.Generation); Check.Equal(400U, complete.HelperPid);
        Check.Equal(0, complete.WaitStatus); Check.Equal((NativeSourceObserverFacts)7, complete.Facts); Check.Equal(NativeSourceSnapshotOutcome.Complete, complete.Result.Outcome);
        var timeout = Event(ParentVector("timeout-event")); Check.Equal(9, timeout.WaitStatus); Check.Equal((NativeSourceObserverFacts)11, timeout.Facts);
        Check.Equal(NativeSourceSnapshotOutcome.Failed, timeout.Result.Outcome); Check.Equal(new NativeSourceOwnerRoot(5, 105), timeout.Result.Output); Check.Equal(NativeSourceSnapshotCleanup.Uncertain, timeout.Result.Cleanup);
        var truncated = Event(ParentVector("truncated-event")); Check.Equal((NativeSourceObserverFacts)23, truncated.Facts); Check.Equal(NativeSourceSnapshotOutcome.Failed, truncated.Result.Outcome);
        Check.Equal(default(NativeSourceOwnerRoot), truncated.Result.Output); Check.Equal(0UL, truncated.Result.Length);
    }
    private static void EventContext()
    {
        var bytes = ParentVector("complete-event");
        foreach (var offset in new[] { 0, 8, 40, 48, 68, 72, 104 + 4, 104 + 56, 104 + 136, 104 + 140 })
        { var changed = (byte[])bytes.Clone(); changed[offset] ^= 0xff; Check.Throws<InvalidDataException>(() => Event(changed)); }
        foreach (var value in new[] { 0U, 1U, 500U, uint.MaxValue }) RejectEvent(bytes, 56, value);
        RejectEvent(bytes, 60, 39); RejectEvent(bytes, 68, 1);
        Check.Throws<InvalidDataException>(() => NativeSourceObservationProtocol.DecodeEvent(bytes, Nonce(), 74, Request(), Owner()));
        Check.Throws<InvalidDataException>(() => NativeSourceObservationProtocol.DecodeEvent(bytes, Nonce(), 73, Request() with { Generation = 2 }, Owner()));
        Check.Throws<InvalidDataException>(() => NativeSourceObservationProtocol.DecodeEvent(bytes, Nonce(), 73, Request(), Owner() with { Profile = NativeSourceOwnerProfile.PhysicalTestOnly }));
        Check.Throws<InvalidDataException>(() => NativeSourceObservationProtocol.DecodeEvent(bytes, Nonce(), 73, Request(), Owner() with { Uid = 0 }));
    }
    private static void PhysicalFacts()
    {
        var bytes = ParentVector("complete-event");
        foreach (var facts in new[] { 0U, 1U, 2U, 3U, 5U, 6U, 15U, 23U, 31U }) RejectEvent(bytes, 60, facts);
        foreach (var status in new[] { 1U, 9U, 125U << 8, uint.MaxValue, 65536U }) RejectEvent(bytes, 64, status);
        var failed = ParentVector("timeout-event");
        RejectEvent(failed, 60, 15); // ExitZero cannot accompany the observed SIGKILL status.
        RejectEvent(failed, 64, 0); // A zero status requires the independent ExitZero fact.
    }
    private static void Targets()
    {
        foreach (var leaf in new[] { "", ".jar", "test.JAR", "../test.jar", "nested/test.jar", "nested\\test.jar", "test .jar", "téšt.jar", new string('a', 1021) + ".jar" })
            Check.Throws<InvalidDataException>(() => NativeSourceObservationProtocol.EncodeObserve(Nonce(), 1, Request() with { Leaf = leaf }));
        var maximum = Request() with { Leaf = new string('a', 1020) + ".jar" };
        var frame = NativeSourceObservationProtocol.EncodeObserve(Nonce(), 1, maximum); Check.Equal(56 + 16 + 1024, frame.Length);
        Check.Equal(maximum, NativeSourceOwnerProtocol.DecodeCommand(frame, Nonce(), 1).Observation!);
        Check.Throws<InvalidDataException>(() => NativeSourceObservationProtocol.EncodeObserve(Nonce(), 1, new(0, NativeSourceSnapshotKind.RetainedUserAgent, "anything")));
        Check.Throws<InvalidDataException>(() => NativeSourceObservationProtocol.EncodeObserve(Nonce(), 1, new(0, (NativeSourceSnapshotKind)3, "")));
    }
    private static void ReleaseTargets()
    {
        foreach (var ticket in new[] { "", "0123", new string('a', 33), new string('A', 32), new string('g', 32), new string('/', 32), new string('\0', 32) })
            Check.Throws<InvalidDataException>(() => NativeSourceObservationProtocol.EncodeRelease(Nonce(), 1, ticket));
        var invalid = Enumerable.Repeat((byte)'a', 32).ToArray(); invalid[3] = 128;
        Check.Throws<InvalidDataException>(() => NativeSourceObservationProtocol.DecodeReleasePayload(invalid));
    }
    private static void PrivateStrings()
    {
        var message = Event(ParentVector("complete-event"));
        Check.True(!message.ToString().Contains(message.Ticket, StringComparison.Ordinal));
        Check.True(!message.ToString().Contains(message.Result.Sha256, StringComparison.Ordinal));
        Check.True(!Request().ToString().Contains("test.jar", StringComparison.Ordinal));
    }
}
