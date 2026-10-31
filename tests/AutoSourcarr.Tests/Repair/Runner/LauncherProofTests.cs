using System.Text;
using AutoSourcarr.Core.Repair.Runner.Isolation;

namespace AutoSourcarr.Tests;

public static class LauncherProofTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static string Body => "{\"pid\":42,\"uid\":568,\"ppid\":" + Environment.ProcessId + ",\"pdeathsig\":9,\"landlock\":6,\"nnp\":1,\"seccomp\":2}";

    public static void Register(TestSuite suite)
    {
        suite.Add("repair launcher proof: exact host identity consumes only its bounded first line", () =>
        {
            using var stream = Bytes(Line(Body) + "QEMU diagnostics retained\n");
            var proof = Read(stream);
            Check.Equal(42, proof.ProcessId); Check.Equal(568U, proof.UserId); Check.Equal(6, proof.LandlockAbi);
            Check.Equal(Environment.ProcessId, proof.ParentProcessId); Check.Equal(9, proof.ParentDeathSignal);
            using var remainder = new StreamReader(stream);
            Check.Equal("QEMU diagnostics retained", remainder.ReadLine());
            Check.True(stream.CanRead);
        });
        suite.Add("repair launcher proof: guest claims cannot replace required host restrictions", () =>
        {
            foreach (var value in new[]
            {
                Body.Replace("\"pid\":42", "\"pid\":43", StringComparison.Ordinal), Body.Replace("568", "0", StringComparison.Ordinal),
                Body.Replace("568", "569", StringComparison.Ordinal), Body.Replace("\"landlock\":6", "\"landlock\":5", StringComparison.Ordinal),
                Body.Replace("\"nnp\":1", "\"nnp\":0", StringComparison.Ordinal), Body.Replace("\"seccomp\":2", "\"seccomp\":0", StringComparison.Ordinal),
                Body.Replace("\"pid\":42", "\"pid\":42.0", StringComparison.Ordinal),
                Body.Replace("\"uid\":568", "\"uid\":\"568\"", StringComparison.Ordinal),
                Body.Replace("\"landlock\":6", "\"landlock\":2147483648", StringComparison.Ordinal),
                Body.Replace("\"pdeathsig\":9", "\"pdeathsig\":15", StringComparison.Ordinal),
                Body.Replace("\"ppid\":" + Environment.ProcessId, "\"ppid\":0", StringComparison.Ordinal),
                Body.Replace("\"ppid\":" + Environment.ProcessId, "\"ppid\":42", StringComparison.Ordinal)
            })
            { using var stream = Bytes(Line(value)); Check.Throws<InvalidDataException>(() => Read(stream)); }
        });
        suite.Add("repair launcher proof: stale nonce, wrong version and ambiguous fields fail closed", () =>
        {
            foreach (var text in new[]
            {
                Line(Body).Replace(Nonce, new string('f', 64), StringComparison.Ordinal),
                Line(Body).Replace("SANDBOX_V2", "SANDBOX_V1", StringComparison.Ordinal),
                Line(Body[..^1] + ",\"pid\":42}"), Line(Body[..^1] + ",\"extra\":true}"),
                Line("[]"), Line(Body.Replace(",\"nnp\":1", "", StringComparison.Ordinal)), Line(Body + " garbage"),
                Line(Body.Replace(",\"pdeathsig\":9", "", StringComparison.Ordinal)),
                Line(Body.Replace(",\"ppid\":" + Environment.ProcessId, "", StringComparison.Ordinal)),
                Line(Body[..^1] + ",\"ppid\":" + Environment.ProcessId + "}")
            })
            { using var stream = Bytes(text); Check.Throws<InvalidDataException>(() => Read(stream)); }
        });
        suite.Add("repair launcher proof: incomplete, control, non-ASCII and oversized lines are refused", () =>
        {
            using (var stream = Bytes(Line(Body).TrimEnd('\n'))) Check.Throws<EndOfStreamException>(() => Read(stream));
            foreach (var text in new[] { Line(Body).Replace("\n", "\r\n", StringComparison.Ordinal), "\uFEFF" + Line(Body), new string('a', NativeLauncherProof.MaximumLineBytes + 1) + "\n" })
            { using var stream = Bytes(text); Check.Throws<InvalidDataException>(() => Read(stream)); }
        });
        suite.Add("repair launcher proof: invalid expected identities reject before consuming evidence", () =>
        {
            foreach (var (pid, uid, nonce) in new (int, uint, string)[] { (1, 568, Nonce), (42, 0, Nonce), (42, 568, "short"), (42, 568, Nonce.ToUpperInvariant()) })
            {
                using var stream = Bytes(Line(Body));
                Check.Throws<ArgumentException>(() => NativeLauncherProof.ReadAsync(stream, pid, uid, nonce, default).GetAwaiter().GetResult());
                Check.Equal(0L, stream.Position);
            }
        });
        suite.Add("repair launcher proof: cancellation does not close the process owner's stream", () =>
        {
            using var stream = Bytes(Line(Body)); using var stop = new CancellationTokenSource(); stop.Cancel();
            Check.Throws<OperationCanceledException>(() => NativeLauncherProof.ReadAsync(stream, 42, 568, Nonce, stop.Token).GetAwaiter().GetResult());
            Check.True(stream.CanRead); Check.Equal(0L, stream.Position);
        });
        suite.Add("repair launcher proof: PID1 is a valid expected parent; invalid or mismatched parents cannot pass", () =>
        {
            using var stream = Bytes(Line(Body.Replace("\"ppid\":" + Environment.ProcessId, "\"ppid\":1", StringComparison.Ordinal)));
            var proof = NativeLauncherProof.ReadAsync(stream, 42, 568, 1, Nonce, default).GetAwaiter().GetResult(); Check.Equal(1, proof.ParentProcessId);
            foreach (var parent in new[] { -1, 0, 42 })
            {
                using var invalid = Bytes(Line(Body));
                Check.Throws<ArgumentException>(() => NativeLauncherProof.ReadAsync(invalid, 42, 568, parent, Nonce, default).GetAwaiter().GetResult()); Check.Equal(0L, invalid.Position);
            }
        });
    }

    private static string Line(string body) => "AUTOSOURCARR_SANDBOX_V2 " + Nonce + " " + body + "\n";
    private static MemoryStream Bytes(string value) => new(Encoding.UTF8.GetBytes(value));
    private static NativeLauncherProof Read(Stream stream) => NativeLauncherProof.ReadAsync(stream, 42, 568, Nonce, default).GetAwaiter().GetResult();
}
