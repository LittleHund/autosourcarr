using System.Runtime.InteropServices;
using System.Collections;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Privacy;

namespace AutoSourcarr.Tests;

public static class SupportLogTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("support logs: sanitize before disk, deduplicate memory and retain across restart", () =>
        {
            using var f = new Fixture(); f.Privacy.Register(new Dictionary<string, string?> { ["password"] = "sentinel-secret" });
            f.Store.Record("app", "password=sentinel-secret"); f.Store.Record("source", "checked"); f.Store.FlushAsync().GetAwaiter().GetResult();
            Check.True(!File.ReadAllText(Path.Combine(f.Directory, "support.jsonl")).Contains("sentinel-secret", StringComparison.Ordinal));
            Check.Equal(2, f.Store.SnapshotAsync().GetAwaiter().GetResult()["entries"]!.AsArray().Count);
            f.Store.DisposeAsync().AsTask().GetAwaiter().GetResult();
            var restarted = new SupportLogStore(f.Io, RuntimeIdentity.Create("fixture"), f.Privacy, f.Temp.Path);
            try { Check.Equal(2, restarted.SnapshotAsync().GetAwaiter().GetResult()["entries"]!.AsArray().Count); }
            finally { restarted.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("support logs: rotation and overflow remain bounded with an accurate dropped count", () =>
        {
            using var f = new Fixture(maximum: 8192, count: 2, pending: 20);
            for (var i = 0; i < 30; i++) f.Store.Record("app", "item " + i + " " + new string('x', 1000));
            var snapshot = f.Store.SnapshotAsync().GetAwaiter().GetResult();
            Check.Equal(10L, snapshot["retention"]!["dropped_records"]!.GetValue<long>());
            var files = new DirectoryInfo(f.Directory).GetFiles("support.jsonl*");
            Check.True(files.Length <= 2); Check.True(files.Sum(file => file.Length) <= 16384); Check.True(files.All(file => file.Length <= 8192));
            Check.Equal(20, snapshot["entries"]!.AsArray().Count);
        });
        suite.Add("support logs: disk failure retains a downloadable in-memory tail", () =>
        {
            using var f = new Fixture(); Directory.CreateDirectory(Path.Combine(f.Directory, "support.jsonl"));
            f.Store.Record("app", "retained in memory");
            var snapshot = f.Store.SnapshotAsync().GetAwaiter().GetResult();
            Check.True(snapshot["storage_error"]!.GetValue<string>().Length > 0);
            Check.True(!snapshot["retention"]!["persistent"]!.GetValue<bool>());
            Check.Equal("retained in memory", snapshot["entries"]![0]!["message"]!.GetValue<string>());
        });
        suite.Add("support logs: hard-linked diagnostic paths cannot read or overwrite another file", () =>
        {
            using var f = new Fixture(); Directory.CreateDirectory(f.Directory);
            var target = Path.Combine(f.Temp.Path, "private.txt"); File.WriteAllText(target, "PRIVATE_FILE_SENTINEL");
            var link = Path.Combine(f.Directory, "support.jsonl");
            if (OperatingSystem.IsWindows()) Check.True(CreateHardLink(link, target, IntPtr.Zero));
            else if (OperatingSystem.IsLinux()) Check.Equal(0, Link(target, link));
            else throw new PlatformNotSupportedException("Native diagnostic hard-link qualification needs Windows or Linux");
            f.Store.Record("app", "safe tail"); var snapshot = f.Store.SnapshotAsync().GetAwaiter().GetResult();
            Check.True(snapshot["storage_error"]!.GetValue<string>().Length > 0);
            Check.True(!snapshot.ToJsonString().Contains("PRIVATE_FILE_SENTINEL", StringComparison.Ordinal));
            Check.Equal("PRIVATE_FILE_SENTINEL", File.ReadAllText(target));
        });
        suite.Add("support logs: process chunks cannot leak private key blocks or unbounded lines", () =>
        {
            using var f = new Fixture(); f.Privacy.Register(new Dictionary<string, string?> { ["password"] = "sentinel-secret" });
            var text = "okay\n-----BEGIN PRIVATE KEY-----\nPRIVATE_KEY_PAYLOAD\n-----END PRIVATE KEY-----\npassword=sentinel-secret\n" + new string('x', 70000) + "\nsafe end";
            using var input = new ChunkStream(Encoding.UTF8.GetBytes(text), 7);
            f.Store.CaptureAsync(input, "tool", CapturedOutputKind.OperationalText).GetAwaiter().GetResult();
            var snapshot = f.Store.SnapshotAsync().GetAwaiter().GetResult(); var encoded = snapshot.ToJsonString();
            Check.True(!encoded.Contains("PRIVATE_KEY_PAYLOAD", StringComparison.Ordinal)); Check.True(!encoded.Contains("sentinel-secret", StringComparison.Ordinal));
            Check.True(encoded.Contains("private key omitted", StringComparison.Ordinal)); Check.True(encoded.Contains("oversize process line omitted", StringComparison.Ordinal));
            Check.True(encoded.Contains("safe end", StringComparison.Ordinal));
            Check.True(snapshot["entries"]!.AsArray().All(row => Encoding.UTF8.GetByteCount(row!["message"]!.GetValue<string>()) <= 4096));
            using var privateProtocol = new MemoryStream(Encoding.UTF8.GetBytes("PRIVATE_PROMPT_PATCH_CONVERSATION"));
            f.Store.CaptureAsync(privateProtocol, "editor", CapturedOutputKind.PrivateProtocol).GetAwaiter().GetResult();
            Check.True(!f.Store.SnapshotAsync().GetAwaiter().GetResult().ToJsonString().Contains("PRIVATE_PROMPT_PATCH_CONVERSATION", StringComparison.Ordinal));
        });
        suite.Add("support logs: one writer and owned input streams drain before shutdown", () =>
        {
            using var f = new Fixture(); var input = new BlockingStream();
            var capture = f.Store.CaptureAsync(input, "tool", CapturedOutputKind.OperationalText);
            input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            var writer = f.Store.RunWriterAsync(CancellationToken.None);
            Check.Throws<InvalidOperationException>(() => f.Store.RunWriterAsync(CancellationToken.None).GetAwaiter().GetResult());
            f.Store.Record("app", "last record"); f.Store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check.Throws<OperationCanceledException>(() => capture.GetAwaiter().GetResult()); Check.Throws<OperationCanceledException>(() => writer.GetAwaiter().GetResult());
            Check.True(input.Closed); Check.True(File.ReadAllText(Path.Combine(f.Directory, "support.jsonl")).Contains("last record", StringComparison.Ordinal));
        });
        suite.Add("support logs: repair cache excludes code/accounts and redacts before truncation", () =>
        {
            using var f = new Fixture(); var longSecret = new string('Q', 6000); f.Privacy.Register(new Dictionary<string, string?> { ["api_key"] = longSecret });
            var job = new JsonObject { ["id"] = new string('a', 32), ["pkg_name"] = "eu.kanade.example", ["state"] = "blocked", ["message"] = longSecret,
                ["patch"] = "PRIVATE_PATCH", ["conversation"] = "PRIVATE_CONVERSATION", ["prompt"] = "PRIVATE_PROMPT", ["baseline_sha256"] = new string('b', 64),
                ["raw_sha256"] = new string('c', 64), ["raw_code"] = "PRIVATE_SOURCE_CODE", ["expected_ids"] = new JsonArray("-1") };
            f.Store.Repair.ObserveRepairs(new JsonObject { ["jobs"] = new JsonArray(job), ["active"] = null });
            f.Store.Repair.ObserveAccount(new JsonObject { ["state"] = "failed", ["action"] = "login", ["device_code"] = "PRIVATE_LOGIN_CODE", ["account"] = "PRIVATE_ACCOUNT",
                ["diagnostics"] = new JsonObject { ["reason"] = "resource-limit", ["phase"] = "starting", ["os_error"] = 11,
                    ["resources"] = new JsonObject { ["nproc_soft"] = "100", ["cgroup_memory_current_bytes"] = "unlimited", ["private_field"] = "PRIVATE_RESOURCE", ["cgroup_scope"] = "visible-v2-only" } } });
            var snapshot = f.Store.SnapshotAsync().GetAwaiter().GetResult(); var encoded = snapshot.ToJsonString();
            foreach (var forbidden in new[] { "PRIVATE_PATCH", "PRIVATE_CONVERSATION", "PRIVATE_PROMPT", "PRIVATE_SOURCE_CODE", "PRIVATE_LOGIN_CODE", "PRIVATE_ACCOUNT", "PRIVATE_RESOURCE", new string('Q', 100) })
                Check.True(!encoded.Contains(forbidden, StringComparison.Ordinal));
            Check.True(snapshot["source_repairs"]!["jobs"]![0]!["raw_code_available"]!.GetValue<bool>());
            Check.Equal("100", snapshot["repair_account"]!["diagnostics"]!["resources"]!["nproc_soft"]!.GetValue<string>());
            Check.True(snapshot["repair_account"]!["diagnostics"]!["resources"]!["cgroup_memory_current_bytes"] is null);
        });
        suite.Add("support logs: native fragments use actual redaction and trusted stream completion", () => NativeLines().GetAwaiter().GetResult());
        suite.Add("support logs: a gap hiding a private marker quarantines only through that generation", () => NativeGap().GetAwaiter().GetResult());
        suite.Add("support logs: final native discard facts cannot flush a sensitive partial line", () => NativeFinalGap().GetAwaiter().GetResult());
        suite.Add("support logs: native ingress stays bounded while real redaction is physically blocked", () => NativeBounded().GetAwaiter().GetResult());
        suite.Add("support logs: store shutdown owns native consumer completion and malformed fragments refuse", () => NativeStoreStop().GetAwaiter().GetResult());
    }

    private static async Task NativeLines()
    {
        using var f = new Fixture(); f.Privacy.Register(new Dictionary<string, string?> { ["password"] = "native-sentinel-secret" });
        var capture = f.Store.CaptureNativeSourceOutput();
        try
        {
            var begin = "-----" + "BEGIN PRIVATE KEY-----"; var end = "-----" + "END PRIVATE KEY-----";
            var text = "okay 界\n" + begin + "\nNATIVE_PRIVATE_BODY\n" + end + "\npassword=native-sentinel-secret\n" + new string('x', 70000) + "\nsafe native tail";
            await Feed(capture, 1, Encoding.UTF8.GetBytes(text), 127);
            capture.ObserveNativeResponse(1, 0, true); await capture.DisposeAsync();
            var snapshot = await f.Store.SnapshotAsync(); var encoded = snapshot.ToJsonString();
            Check.True(!encoded.Contains("NATIVE_PRIVATE_BODY", StringComparison.Ordinal)); Check.True(!encoded.Contains("native-sentinel-secret", StringComparison.Ordinal));
            Check.True(encoded.Contains("private key omitted", StringComparison.Ordinal)); Check.True(encoded.Contains("oversize process line omitted", StringComparison.Ordinal));
            Check.True(encoded.Contains("safe native tail", StringComparison.Ordinal)); Check.True(encoded.Contains("okay", StringComparison.Ordinal));
            Check.True(snapshot["entries"]!.AsArray().All(row => Encoding.UTF8.GetByteCount(row!["message"]!.GetValue<string>()) <= 4096));
            var retained = File.ReadAllText(Path.Combine(f.Directory, "support.jsonl"));
            Check.True(!retained.Contains("native-sentinel-secret", StringComparison.Ordinal) && !retained.Contains("NATIVE_PRIVATE_BODY", StringComparison.Ordinal));
        }
        finally { await capture.DisposeAsync(); }
    }
    private static async Task NativeGap()
    {
        using var f = new Fixture(); var capture = f.Store.CaptureNativeSourceOutput();
        try
        {
            var first = Encoding.UTF8.GetBytes("valid before gap\npartial-private-");
            Check.True(capture.Write(1, 0, first, false));
            await Until(() => capture.Snapshot.Queued == 0 && !capture.Snapshot.Consuming);
            // The omitted bytes could have contained a complete private-key BEGIN.
            var raw = Encoding.UTF8.GetBytes("UNMARKED_PRIVATE_BODY\nstill private\n");
            Check.True(capture.Write(1, (ulong)first.Length + 80, raw, true));
            capture.ObserveNativeResponse(1, 80, true);
            var fresh = Encoding.UTF8.GetBytes("new generation is visible\n");
            Check.True(capture.Write(2, 0, fresh, false)); capture.ObserveNativeResponse(2, 80, true);
            await capture.DisposeAsync(); var encoded = (await f.Store.SnapshotAsync()).ToJsonString();
            Check.True(encoded.Contains("valid before gap", StringComparison.Ordinal)); Check.True(encoded.Contains("capture gap", StringComparison.Ordinal));
            Check.True(encoded.Contains("new generation is visible", StringComparison.Ordinal));
            foreach (var hidden in new[] { "partial-private-", "UNMARKED_PRIVATE_BODY", "still private" }) Check.True(!encoded.Contains(hidden, StringComparison.Ordinal));
        }
        finally { await capture.DisposeAsync(); }
    }
    private static async Task NativeFinalGap()
    {
        using var f = new Fixture(); var capture = f.Store.CaptureNativeSourceOutput();
        try
        {
            await Feed(capture, 1, Encoding.UTF8.GetBytes("unlabelled-sensitive-fragment"), 7);
            capture.ObserveNativeResponse(1, 20, true); await capture.DisposeAsync();
            var encoded = (await f.Store.SnapshotAsync()).ToJsonString();
            Check.True(!encoded.Contains("unlabelled-sensitive-fragment", StringComparison.Ordinal)); Check.True(encoded.Contains("capture gap", StringComparison.Ordinal));
        }
        finally { await capture.DisposeAsync(); }
    }
    private static async Task NativeBounded()
    {
        using var f = new Fixture(); var capture = f.Store.CaptureNativeSourceOutput();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var register = Task.Run(() => f.Privacy.Register(new HeldSecrets(entered, release))); Task? stopped = null;
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var first = Encoding.UTF8.GetBytes("before flood\n"); Check.True(capture.Write(1, 0, first, false));
            await Until(() => capture.Snapshot.Consuming);
            var bytes = Encoding.UTF8.GetBytes("queue-held-secret\n");
            var producer = Task.Run(() => { var at = (ulong)first.Length; for (var index = 0; index < 1000; index++) { capture.Write(1, at, bytes, false); at += (ulong)bytes.Length; } });
            await producer.WaitAsync(TimeSpan.FromSeconds(3)); var state = capture.Snapshot;
            Check.True(state.Queued <= 64 && state.PeakQueued <= 64 && state.DroppedBytes > 0 && state.QuarantinedThrough == 1);
            stopped = capture.DisposeAsync().AsTask(); await Task.Delay(20); Check.True(!stopped.IsCompleted);
            release.Set(); await register; await stopped.WaitAsync(TimeSpan.FromSeconds(3));
            Check.Equal(0, capture.Snapshot.Queued); Check.True(!capture.Snapshot.Consuming && capture.Snapshot.Closed);
            var encoded = (await f.Store.SnapshotAsync()).ToJsonString(); Check.True(!encoded.Contains("queue-held-secret", StringComparison.Ordinal));
            Check.True(encoded.Contains("capture gap", StringComparison.Ordinal));
        }
        finally { release.Set(); await register; await capture.DisposeAsync(); if (stopped is not null) await stopped; }
    }
    private static async Task NativeStoreStop()
    {
        using var f = new Fixture(); var capture = f.Store.CaptureNativeSourceOutput();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var register = Task.Run(() => f.Privacy.Register(new HeldSecrets(entered, release))); Task? stopped = null;
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Check.Throws<InvalidDataException>(() => capture.Write(0, 0, "x"u8, false));
            Check.Throws<InvalidDataException>(() => capture.Write(1, ulong.MaxValue, "x"u8, false));
            Check.Throws<InvalidDataException>(() => capture.Write(1, 0, new byte[449], false));
            Check.True(capture.Write(1, 0, "queue-held-secret\n"u8, false));
            Check.Throws<InvalidDataException>(() => capture.Write(1, 0, "duplicate"u8, false));
            await Until(() => capture.Snapshot.Consuming);
            stopped = f.Store.DisposeAsync().AsTask(); await Task.Delay(20); Check.True(!stopped.IsCompleted);
            release.Set(); await register; await stopped.WaitAsync(TimeSpan.FromSeconds(3)); await capture.DisposeAsync();
            Check.True(capture.Snapshot.Closed && !capture.Snapshot.Consuming); Check.Equal(0, capture.Snapshot.Queued);
            Check.True(!File.ReadAllText(Path.Combine(f.Directory, "support.jsonl")).Contains("queue-held-secret", StringComparison.Ordinal));
        }
        finally { release.Set(); await register; await capture.DisposeAsync(); if (stopped is not null) await stopped; }
    }
    private static async Task Feed(SupportLogStore.NativeSourceCapture capture, ulong generation, byte[] bytes, int chunk)
    {
        for (var at = 0; at < bytes.Length; at += chunk)
        {
            await Until(() => capture.Snapshot.Queued < 32);
            Check.True(capture.Write(generation, (ulong)at, bytes.AsSpan(at, Math.Min(chunk, bytes.Length - at)), false));
        }
    }
    private static async Task Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual source-log capture did not reach its observed state"); await Task.Delay(1); }
    }
    internal sealed class HeldSecrets(ManualResetEventSlim entered, ManualResetEventSlim release) : IReadOnlyDictionary<string, string?>
    {
        public string? this[string key] => key == "password" ? "queue-held-secret" : throw new KeyNotFoundException();
        public IEnumerable<string> Keys => ["password"];
        public IEnumerable<string?> Values => ["queue-held-secret"];
        public int Count => 1;
        public bool ContainsKey(string key) => key == "password";
        public bool TryGetValue(string key, out string? value) { value = key == "password" ? "queue-held-secret" : null; return key == "password"; }
        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator()
        { entered.Set(); release.Wait(); yield return new("password", "queue-held-secret"); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class Fixture : IDisposable
    {
        public Sandbox Temp { get; } = new();
        public BoundedWorkScheduler Io { get; } = new(1, 8);
        public SupportPrivacy Privacy { get; } = new();
        public SupportLogStore Store { get; }
        public string Directory => Path.Combine(Temp.Path, "diagnostics");
        public Fixture(int maximum = 512 * 1024, int count = 3, int pending = 512) => Store = new(Io, RuntimeIdentity.Create("fixture"), Privacy, Temp.Path, maximum, count, pending);
        public void Dispose() { Store.DisposeAsync().AsTask().GetAwaiter().GetResult(); Io.DisposeAsync().AsTask().GetAwaiter().GetResult(); Temp.Dispose(); }
    }
    private sealed class ChunkStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken); }
    private sealed class BlockingStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); return 0; }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string filename, string existing, IntPtr reserved);
    [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int Link(string existing, string filename);
}
