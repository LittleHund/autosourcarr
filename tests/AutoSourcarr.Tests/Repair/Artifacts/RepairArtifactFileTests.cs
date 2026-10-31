using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair.Artifacts;
using AutoSourcarr.Core.Repair.Runner.Protocol;

namespace AutoSourcarr.Tests;

public static class RepairArtifactFileTests
{
    private static readonly string Job = new('a', 32), Nonce = new('b', 64), Project = new('c', 64);
    private static GuestArtifactSession Context() => new(Job, Nonce); // Controlled identity only; no real VM qualification.
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static T Await<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    private static void Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static GuestArtifactDescriptor Input(byte[] bytes) => new(GuestArtifactRole.RawProjectFile, bytes.Length, Hash(bytes), "src/Example.kt", Project);
    private static GuestArtifactDescriptor Output(byte[] bytes) => new(GuestArtifactRole.CandidateJar, bytes.Length, Hash(bytes));
    private static GuestArtifactBinding Binding(GuestArtifactDescriptor descriptor, bool input = true) => new(Job, Nonce, new string(input ? 'd' : 'e', 32), input ? GuestArtifactDirection.IntoGuest : GuestArtifactDirection.FromGuest, descriptor);
    private static string Maintenance(Sandbox sandbox) { var path = Path.Combine(sandbox.Path, "source-maintenance"); Directory.CreateDirectory(path); return path; }
    private static string Root(string maintenance) => Path.Combine(maintenance, "artifact-jobs", Job);
    private sealed class OwnedInput(byte[] bytes) : MemoryStream(bytes, writable: false)
    { public bool Closed; protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); } }
    private static void HardLink(string existing, string target)
    {
        if (OperatingSystem.IsWindows()) Check.True(CreateHardLink(target, existing, IntPtr.Zero), "Native artifact hard-link fixture failed: " + Marshal.GetLastWin32Error());
        else if (OperatingSystem.IsLinux()) Check.Equal(0, Link(existing, target));
        else throw new PlatformNotSupportedException("Artifact native fixture requires Windows/Linux");
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string target, string existing, IntPtr reserved);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string existing, string target);
    public static void Register(TestSuite suite)
    {
        suite.Add("artifact files controlled: exact immutable inputs and independently verified quarantine receipts survive reopen inspection", () =>
        {
            using var sandbox = new Sandbox(); var maintenance = Maintenance(sandbox); var context = Context(); var depot = Await(RepairArtifactDepot.CreateAsync(maintenance, context));
            var original = "EXACT_ORIGINAL_SOURCE_BYTES"u8.ToArray(); var descriptor = Input(original); var admission = Await(depot.AdmitInputAsync(descriptor, original));
            var lease = Await(depot.OpenInputAsync(Binding(descriptor), default)); var read = new byte[original.Length]; Await(lease.Content.ReadExactlyAsync(read).AsTask()); Await(lease.DisposeAsync().AsTask()); Check.True(read.AsSpan().SequenceEqual(original));
            var candidate = "UNTRUSTED_CANDIDATE_BYTES"u8.ToArray(); var output = Binding(Output(candidate), input: false); var sink = Await(depot.CreateOutputAsync(output, default));
            Await(sink.Content.WriteAsync(candidate).AsTask()); Await(sink.CommitAsync(new(output, 1), default)); Await(sink.DisposeAsync().AsTask()); Await(depot.DisposeAsync().AsTask());
            var observed = Await(RepairArtifactDepot.InspectAsync(maintenance, context)); Check.Equal("closed", observed.Status); Check.Equal(1, observed.Inputs.Count); Check.Equal(1, observed.Outputs.Count); Check.Equal(0, observed.Quarantined);
            Check.Equal(admission, observed.Inputs[0]); Check.True(File.ReadAllBytes(Path.Combine(Root(maintenance), "objects", admission.ObjectId + ".bin")).AsSpan().SequenceEqual(original));
            Check.True(!File.ReadAllText(Path.Combine(Root(maintenance), "depot.json")).Contains(Nonce, StringComparison.Ordinal));
            Check.Throws<InvalidDataException>(() => Await(RepairArtifactDepot.CreateAsync(maintenance, context))); // Inspection never resumes a session.
        });
        suite.Add("artifact files controlled: wrong job/session/role and missing maintenance cannot open arbitrary data", () =>
        {
            using var sandbox = new Sandbox(); var maintenance = Maintenance(sandbox); var depot = Await(RepairArtifactDepot.CreateAsync(maintenance, Context())); var bytes = "SOURCE"u8.ToArray(); var descriptor = Input(bytes);
            try
            {
                foreach (var changed in new[] { Binding(descriptor) with { JobId = new string('f', 32) }, Binding(descriptor) with { Session = new string('f', 64) }, Binding(descriptor) with { ArtifactId = "../account" } })
                    Check.Throws<InvalidDataException>(() => Await(depot.OpenInputAsync(changed, default)));
                Check.Throws<InvalidDataException>(() => Await(depot.CreateOutputAsync(Binding(descriptor) with { Direction = GuestArtifactDirection.FromGuest }, default)));
                Check.Throws<InvalidDataException>(() => Await(RepairArtifactDepot.InspectAsync(maintenance, new GuestArtifactSession(Job, new string('f', 64)))));
                var missing = Path.Combine(sandbox.Path, "missing", "source-maintenance"); Check.Throws<InvalidDataException>(() => Await(RepairArtifactDepot.CreateAsync(missing, Context()))); Check.True(!Directory.Exists(missing));
            }
            finally { Await(depot.DisposeAsync().AsTask()); }
        });
        suite.Add("artifact files controlled: changed admission and hard-linked input fail closed without replacing originals", () =>
        {
            foreach (var linked in new[] { false, true })
            {
                using var sandbox = new Sandbox(); var maintenance = Maintenance(sandbox); var depot = Await(RepairArtifactDepot.CreateAsync(maintenance, Context())); var bytes = "ORIGINAL_IMMUTABLE_BYTES"u8.ToArray(); var descriptor = Input(bytes);
                try
                {
                    if (!linked)
                    {
                        var stream = new OwnedInput(bytes); Check.Throws<InvalidDataException>(() => Await(depot.AdmitInputAsync(descriptor with { Sha256 = new string('f', 64) }, stream))); Check.True(stream.Closed);
                    }
                    else
                    {
                        var receipt = Await(depot.AdmitInputAsync(descriptor, bytes)); var path = Path.Combine(Root(maintenance), "objects", receipt.ObjectId + ".bin");
                        HardLink(path, Path.Combine(sandbox.Path, "linked-input")); Check.Throws<InvalidDataException>(() => Await(depot.OpenInputAsync(Binding(descriptor), default))); Check.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes));
                    }
                    Check.Throws<InvalidDataException>(() => Await(depot.AdmitInputAsync(descriptor, bytes)));
                }
                finally { Await(depot.DisposeAsync().AsTask()); }
            }
        });
        suite.Add("artifact files controlled: wrong output bytes and hard links remain quarantined and cannot be committed", () =>
        {
            foreach (var linked in new[] { false, true })
            {
                using var sandbox = new Sandbox(); var maintenance = Maintenance(sandbox); var context = Context(); var depot = Await(RepairArtifactDepot.CreateAsync(maintenance, context));
                var expected = "EXPECTED_BINARY_PAYLOAD"u8.ToArray(); var binding = Binding(Output(expected), false); var lease = Await(depot.CreateOutputAsync(binding, default));
                try
                {
                    Await(lease.Content.WriteAsync(linked ? expected : new byte[expected.Length]).AsTask());
                    if (linked) HardLink(Directory.GetFiles(Path.Combine(Root(maintenance), "objects"), "*.partial").Single(), Path.Combine(sandbox.Path, "linked-output"));
                    Check.Throws<InvalidDataException>(() => Await(lease.CommitAsync(new(binding, 1), default)));
                }
                finally { Await(lease.DisposeAsync().AsTask()); Await(depot.DisposeAsync().AsTask()); }
                var observed = Await(RepairArtifactDepot.InspectAsync(maintenance, context)); Check.Equal("unusable", observed.Status); Check.Equal(0, observed.Outputs.Count); Check.Equal(1, observed.Quarantined);
            }
        });
        suite.Add("artifact files controlled: cancelled physical admission stays owned and is never resumed", () =>
        {
            using var sandbox = new Sandbox(); var maintenance = Maintenance(sandbox); var entered = Signal(); using var release = new ManualResetEventSlim(); var context = Context();
            var depot = Await(RepairArtifactDepot.CreateAsync(maintenance, context, stage => { if (stage == "admit-read") { entered.TrySetResult(); release.Wait(); } }, default));
            var bytes = "CANCELLED_OWNED_INPUT"u8.ToArray(); var source = new OwnedInput(bytes); using var cancel = new CancellationTokenSource();
            var admitting = depot.AdmitInputAsync(Input(bytes), source, cancel.Token); Await(entered.Task); cancel.Cancel(); var closing = depot.DisposeAsync().AsTask();
            try { Check.True(!admitting.IsCompleted && !closing.IsCompleted); }
            finally { release.Set(); }
            Check.Throws<OperationCanceledException>(() => Await(admitting)); Await(closing); Check.True(source.Closed);
            Check.Equal("unusable", Await(RepairArtifactDepot.InspectAsync(maintenance, context)).Status);
        });
        suite.Add("artifact files controlled: read/write/commit/close cancellation joins physical IO before depot disposal", () =>
        {
            foreach (var phase in new[] { "read", "write", "commit", "close" })
            {
                using var sandbox = new Sandbox(); var maintenance = Maintenance(sandbox); var context = Context(); var entered = Signal(); using var release = new ManualResetEventSlim(); var enabled = false;
                var depot = Await(RepairArtifactDepot.CreateAsync(maintenance, context, stage => { if (enabled && stage == phase) { entered.TrySetResult(); release.Wait(); } }, default));
                var bytes = "PHYSICAL_ARTIFACT_IO"u8.ToArray(); using var cancel = new CancellationTokenSource(); GuestArtifactReadLease? read = null; GuestArtifactWriteLease? write = null; Task active;
                if (phase is "read" or "close")
                {
                    Await(depot.AdmitInputAsync(Input(bytes), bytes)); read = Await(depot.OpenInputAsync(Binding(Input(bytes)), default)); enabled = true;
                    active = phase == "read" ? read.Content.ReadAsync(new byte[bytes.Length], cancel.Token).AsTask() : read.DisposeAsync().AsTask();
                }
                else
                {
                    var binding = Binding(Output(bytes), false); write = Await(depot.CreateOutputAsync(binding, default));
                    if (phase == "commit") Await(write.Content.WriteAsync(bytes).AsTask()); enabled = true;
                    active = phase == "write" ? write.Content.WriteAsync(bytes, cancel.Token).AsTask() : write.CommitAsync(new(binding, 1), cancel.Token);
                }
                Await(entered.Task); cancel.Cancel(); var closed = depot.DisposeAsync().AsTask();
                try { Check.True(!active.IsCompleted && !closed.IsCompleted); }
                finally { release.Set(); }
                if (phase == "close") Await(active); else Check.Throws<OperationCanceledException>(() => Await(active));
                Await(closed); if (read is not null) Await(read.DisposeAsync().AsTask()); if (write is not null) Await(write.DisposeAsync().AsTask());
                var view = Await(RepairArtifactDepot.InspectAsync(maintenance, context)); Check.True(phase == "close" || view.Status == "unusable"); Check.Equal(0, view.Outputs.Count);
            }
        });
        suite.Add("artifact files controlled: a physical close fault is joined, retained and makes concurrent disposal fail closed", () =>
        {
            using var sandbox = new Sandbox(); var maintenance = Maintenance(sandbox); var context = Context();
            var depot = Await(RepairArtifactDepot.CreateAsync(maintenance, context, stage => { if (stage == "close") throw new IOException("Controlled physical close fault"); }, default));
            var bytes = "CLOSE_FAILURE_ORIGINAL"u8.ToArray(); var admission = Await(depot.AdmitInputAsync(Input(bytes), bytes)); var lease = Await(depot.OpenInputAsync(Binding(Input(bytes)), default));
            Check.Throws<IOException>(() => Await(lease.DisposeAsync().AsTask()));
            var first = depot.DisposeAsync().AsTask(); var second = depot.DisposeAsync().AsTask();
            Check.Throws<AggregateException>(() => Await(first)); Check.Throws<AggregateException>(() => Await(second));
            var observed = Await(RepairArtifactDepot.InspectAsync(maintenance, context)); Check.Equal("unusable", observed.Status);
            using var exclusive = new FileStream(Path.Combine(Root(maintenance), "objects", admission.ObjectId + ".bin"), FileMode.Open, FileAccess.Read, FileShare.None);
            Check.Equal(bytes.Length, checked((int)exclusive.Length)); // The actual file handle was closed despite the retained fault.
        });
        suite.Add("artifact files controlled: forensic inspection rejects tampered identity and quota receipts", () =>
        {
            foreach (var change in new[] { "duplicate-name", "project-switch", "revision-key" })
            {
                using var sandbox = new Sandbox(); var maintenance = Maintenance(sandbox); var context = Context(); var depot = Await(RepairArtifactDepot.CreateAsync(maintenance, context));
                var bytes = "RECEIPT_BYTES"u8.ToArray(); Await(depot.AdmitInputAsync(Input(bytes), bytes)); Await(depot.AdmitInputAsync(Input(bytes) with { LogicalName = "src/Second.kt" }, bytes)); Await(depot.DisposeAsync().AsTask());
                var path = Path.Combine(Root(maintenance), "depot.json"); var receipt = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
                if (change == "revision-key") { receipt.Remove("revision"); receipt["unexpected"] = 1; }
                else
                {
                    var descriptor = receipt["entries"]![1]!["Descriptor"]!.AsObject();
                    if (change == "duplicate-name") descriptor["LogicalName"] = "src/Example.kt"; else descriptor["ProjectId"] = new string('f', 64);
                }
                File.WriteAllText(path, receipt.ToJsonString()); Check.Throws<InvalidDataException>(() => Await(RepairArtifactDepot.InspectAsync(maintenance, context)));
            }
        });
        suite.Add("artifact files controlled: a guest artifact identity cannot be replayed under another valid raw filename", () =>
        {
            using var sandbox = new Sandbox(); var maintenance = Maintenance(sandbox); var context = Context(); var depot = Await(RepairArtifactDepot.CreateAsync(maintenance, context));
            var bytes = "SAME_BYTES_DISTINCT_RAW_NAME"u8.ToArray(); var descriptor = new GuestArtifactDescriptor(GuestArtifactRole.CandidateProjectFile, bytes.Length, Hash(bytes), "src/First.kt", Project); var binding = Binding(descriptor, false);
            var lease = Await(depot.CreateOutputAsync(binding, default)); Await(lease.Content.WriteAsync(bytes).AsTask()); Await(lease.CommitAsync(new(binding, 1), default)); Await(lease.DisposeAsync().AsTask());
            Check.Throws<InvalidDataException>(() => Await(depot.CreateOutputAsync(binding with { Descriptor = descriptor with { LogicalName = "src/Second.kt" } }, default))); Await(depot.DisposeAsync().AsTask());
            var observed = Await(RepairArtifactDepot.InspectAsync(maintenance, context)); Check.Equal("unusable", observed.Status); Check.Equal(1, observed.Quarantined);
            Check.Equal(1, Directory.GetFiles(Path.Combine(Root(maintenance), "objects")).Length);
        });
    }
}
