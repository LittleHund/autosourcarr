using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Collections;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Imaging;

namespace AutoSourcarr.Tests;

public static class CoverCacheTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("cover cache:23 released Unicode filename cases and84 disk read/presence cases", Oracle);
        suite.Add("cover cache: cold requests coalesce and a disconnected waiter does not cancel the fill", () => Coalescing().GetAwaiter().GetResult());
        suite.Add("cover cache: finite physical pending admission is separate from source failure", () => Admission().GetAwaiter().GetResult());
        suite.Add("cover cache: total deadline retains noncooperative work until physical completion", () => Deadline().GetAwaiter().GetResult());
        suite.Add("cover cache: synchronous invalidation fences a late loader from restoring deleted artwork", () => Invalidation().GetAwaiter().GetResult());
        suite.Add("cover cache: async clear and close join admitted real filesystem deletion after cancellation", () => PhysicalClear().GetAwaiter().GetResult());
        suite.Add("cover cache: failure cooldown expires and owned write/clear changes presence immediately", () => Cooldown().GetAwaiter().GetResult());
        suite.Add("cover cache: oversized/corrupt/special cache files never become image responses", UnsafeFiles);
        suite.Add("cover cache: presence/LRU bounds and reset preserve unrelated files", Bounds);
    }
    private static readonly CoverData Image = new(Encoding.ASCII.GetBytes("synthetic-cover"), "image/png");
    private static JsonObject Data() => JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("cover-cache.json")))!.AsObject();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Await(Task value) => value.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    private static void Oracle()
    {
        var data = Data();
        foreach (var item in data["filenames"]!.AsArray().OfType<JsonObject>())
        {
            using var f = new Fixture(); f.Cache.Write(item["key"]!.GetValue<string>(), Image);
            Check.True(File.Exists(Path.Combine(f.Root, item["image"]!.GetValue<string>()))); Check.True(File.Exists(Path.Combine(f.Root, item["metadata"]!.GetValue<string>())));
        }
        foreach (var item in data["reads"]!.AsArray().OfType<JsonObject>())
        {
            using var f = new Fixture(); var image = Path.Combine(f.Root, "entry-read.img"); var meta = Path.Combine(f.Root, "entry-read.json");
            File.WriteAllBytes(image, Image.Content.ToArray()); File.SetLastWriteTimeUtc(image, f.Clock.GetUtcNow().AddSeconds(-item["age"]!.GetValue<double>()).UtcDateTime);
            if (item["metadata"] is { } value) File.WriteAllText(meta, value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text == "invalid-json" ? "{" : value.ToJsonString(), new UTF8Encoding(false));
            var read = f.Cache.Read("entry-read"); var expected = item["result"];
            Check.Equal(expected is not null, read is not null);
            if (expected is not null) { Check.Equal(expected["hex"]!.GetValue<string>(), Convert.ToHexStringLower(read!.Content.Span)); Check.Equal(expected["content_type"]!.GetValue<string>(), read.ContentType); }
            Check.Equal(item["present"]!.GetValue<bool>(), f.Cache.IsCached("entry-read"));
        }
    }
    private static async Task Coalescing()
    {
        using var f = new Fixture(); var entered = Signal(); var release = Signal(); using var cancelled = new CancellationTokenSource(); var loads = 0;
        async Task<CoverData> Load(CancellationToken token) { Interlocked.Increment(ref loads); entered.TrySetResult(); await release.Task; return Image; }
        var first = f.Cache.GetAsync("entry-1", Load, TimeSpan.FromSeconds(5)); Task<CoverData?>? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); second = f.Cache.GetAsync("entry-1", Load, TimeSpan.FromSeconds(5), cancelled.Token); cancelled.Cancel();
            try { await second; throw new InvalidOperationException("Disconnected waiter returned success"); } catch (OperationCanceledException) { }
            Check.Equal(1, loads); Check.Equal(1, f.Cache.Snapshot.Pending); release.TrySetResult(); var value = await first;
            Check.True(value is not null && value.Content.Span.SequenceEqual(Image.Content.Span)); Check.Equal(1, loads); Check.True(f.Cache.IsCached("entry-1"));
        }
        finally { release.TrySetResult(); await first; if (second is not null) try { await second; } catch (OperationCanceledException) { } }
    }
    private static async Task Admission()
    {
        using var f = new Fixture(maximum: 2); var entered = Signal(); var release = Signal(); var starts = 0;
        async Task<CoverData> Load(CancellationToken token) { Interlocked.Increment(ref starts); entered.TrySetResult(); await release.Task; return Image; }
        try
        {
            Check.True(await f.Cache.GetAsync("one", Load, TimeSpan.Zero) is null); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(await f.Cache.GetAsync("two", Load, TimeSpan.Zero) is null); Check.True(await f.Cache.GetAsync("three", Load, TimeSpan.Zero) is null);
            Check.Equal(2, f.Cache.Snapshot.Pending); Check.Equal(1, f.Cache.Snapshot.Active); Check.Equal(0, f.Cache.Snapshot.Failed); Check.Equal(1, starts);
        }
        finally { release.TrySetResult(); await f.Cache.CloseAsync(); }
        Check.Equal(0, f.Cache.Snapshot.Pending);
    }
    private static async Task Deadline()
    {
        using var f = new Fixture(deadline: TimeSpan.FromMilliseconds(40)); var entered = Signal(); var deadline = Signal(); var release = Signal();
        async Task<CoverData> Load(CancellationToken token)
        { using var notice = token.Register(() => deadline.TrySetResult()); entered.TrySetResult(); await release.Task; return Image; }
        try
        {
            await f.Cache.GetAsync("slow", Load, TimeSpan.Zero); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await deadline.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Equal(1, f.Cache.Snapshot.Pending); Check.Equal(0, f.Cache.Snapshot.Failed);
            var close = f.Cache.CloseAsync(); Check.True(!close.IsCompleted); release.TrySetResult(); await close.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Equal(0, f.Cache.Snapshot.Pending); Check.True(!File.Exists(Path.Combine(f.Root, "slow.img")));
        }
        finally { release.TrySetResult(); await f.Cache.CloseAsync(); }
    }
    private static async Task Invalidation()
    {
        using var f = new Fixture(); var entered = Signal(); var release = Signal();
        async Task<CoverData> Load(CancellationToken token) { entered.TrySetResult(); await release.Task; return Image; }
        var pending = f.Cache.GetAsync("late", Load, TimeSpan.FromSeconds(5));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check.Equal(0, f.Cache.ClearKeys(["late", "late"]));
            release.TrySetResult(); Check.True(await pending is null); Check.True(f.Cache.Read("late") is null); Check.Equal(0, f.Cache.Snapshot.Failed);
        }
        finally { release.TrySetResult(); await pending; }
    }
    private static async Task PhysicalClear()
    {
        using var f = new Fixture(ioWorkers: 1); f.Cache.Write("entry-1", Image); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var cancel = new CancellationTokenSource();
        var blocked = f.Io.RunAsync(() => { entered.Set(); release.Wait(); return 0; }); Task<int>? clearing = null; Task? closing = null;
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(5))); clearing = f.Cache.ClearAllAsync(cancel.Token); cancel.Cancel(); closing = f.Cache.CloseAsync();
            await Task.Delay(20); Check.True(!clearing.IsCompleted && !closing.IsCompleted); Check.True(File.Exists(Path.Combine(f.Root, "entry-1.img")));
            release.Set(); await blocked;
            try { await clearing; throw new InvalidOperationException("Cancelled clear returned success"); } catch (OperationCanceledException) { }
            await closing; Check.True(!File.Exists(Path.Combine(f.Root, "entry-1.img"))); Check.True(!File.Exists(Path.Combine(f.Root, "entry-1.json")));
        }
        finally
        {
            release.Set(); await blocked; if (clearing is not null) try { await clearing; } catch (OperationCanceledException) { }
            if (closing is not null) await closing;
        }
    }
    private static async Task Cooldown()
    {
        using var f = new Fixture(); var loads = 0;
        Task<CoverData> Fail(CancellationToken token) { loads++; throw new IOException("Synthetic image failure"); }
        Check.True(await f.Cache.GetAsync("failed", Fail, TimeSpan.FromSeconds(5)) is null); Check.Equal(1, loads); Check.Equal(1, f.Cache.Snapshot.Failed);
        Check.True(await f.Cache.GetAsync("failed", Fail, TimeSpan.FromSeconds(5)) is null); Check.Equal(1, loads);
        f.Tick += 31; Check.True(await f.Cache.GetAsync("failed", Fail, TimeSpan.FromSeconds(5)) is null); Check.Equal(2, loads);
        Check.True(!f.Cache.IsCached("entry-1")); f.Cache.Write("entry-1", Image); Check.True(f.Cache.IsCached("entry-1")); Check.Equal(2, f.Cache.ClearKeys(["entry-1"])); Check.True(!f.Cache.IsCached("entry-1"));
    }
    private static void UnsafeFiles()
    {
        using var f = new Fixture();
        Check.Throws<ArgumentException>(() => f.Cache.Write("too-big", new(new byte[CoverCache.MaximumImageBytes + 1], "image/png")));
        using (var file = new FileStream(Path.Combine(f.Root, "oversize.img"), FileMode.CreateNew, FileAccess.Write)) file.SetLength(CoverCache.MaximumImageBytes + 1);
        Check.True(f.Cache.Read("oversize") is null); Check.True(!f.Cache.IsCached("oversize"));
        var directory = Path.Combine(f.Root, "directory.img"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "keep"), "Retain nested data");
        Check.True(f.Cache.Read("directory") is null); Check.True(!f.Cache.IsCached("directory")); Check.Equal(0, f.Cache.ClearKeys(["directory"])); Check.True(File.Exists(Path.Combine(directory, "keep")));
        f.Cache.Write("corrupt", Image); File.WriteAllText(Path.Combine(f.Root, "corrupt.json"), "{"); Check.True(f.Cache.Read("corrupt") is null);
    }
    private static void Bounds()
    {
        using var f = new Fixture();
        for (var i = 0; i < 4100; i++) Check.True(!f.Cache.IsCached("missing-" + i.ToString(CultureInfo.InvariantCulture)));
        Check.Equal(4096, f.Cache.Snapshot.Presence); File.WriteAllText(Path.Combine(f.Root, "unrelated.keep"), "Preserve"); f.Cache.Write("owned", Image);
        Check.Equal(2, f.Cache.ClearAllAsync().GetAwaiter().GetResult()); Check.Equal(0, f.Cache.Snapshot.Presence); Check.True(File.Exists(Path.Combine(f.Root, "unrelated.keep")));
        var lru = new BoundedLruCache<string, int>(2); lru.Set("a", 1); lru.Set("b", 2); Check.True(lru.TryGet("a", out _)); lru.Set("c", 3);
        Check.True(!lru.TryGet("b", out _)); Check.Equal(2, lru.Count); Check.True(lru.Remove("a")); Check.True(!lru.Remove("absent")); lru.Clear(); Check.Equal(0, lru.Count);
    }
    private sealed class FixedClock(double unix) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(unix * 1000))); }
    private sealed class Fixture : IDisposable
    {
        private readonly Sandbox _temp = new(); public BoundedWorkScheduler Io { get; } public CoverCache Cache { get; } public TimeProvider Clock { get; } public double Tick = 100;
        public string Root => Path.Combine(_temp.Path, "cover-cache");
        public Fixture(int maximum = 64, TimeSpan? deadline = null, int ioWorkers = 2)
        {
            Io = new(ioWorkers, 32); Clock = new FixedClock(Data()["now"]!.GetValue<double>());
            Cache = new(_temp.Path, () => new AutoSourcarrSettings { CoverFetchConcurrency = 1, CoverCacheTtlSeconds = 60 }, Io, maximum, deadline, Clock, () => Tick);
        }
        public void Dispose() { try { Cache.DisposeAsync().AsTask().GetAwaiter().GetResult(); } finally { Io.DisposeAsync().AsTask().GetAwaiter().GetResult(); _temp.Dispose(); } }
    }
}
