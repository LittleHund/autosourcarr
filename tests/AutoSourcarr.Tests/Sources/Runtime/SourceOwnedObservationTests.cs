using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Sources.Runtime;
using Microsoft.Win32.SafeHandles;

namespace AutoSourcarr.Tests;

public static partial class SourceOwnedObservationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("source-owned observations: byte results are immutable evidence, with full binary hash", ImmutableBytes);
        suite.Add("source-owned observations: installed JAR leaf policy admits copied packages and refuses paths", LeafPolicy);
        if (!SourceObservationRoot.IsSupported)
        {
            suite.Add("source-owned observations: unsupported OS has no descriptor-authority fallback", Unsupported);
            return;
        }
        // These groups perform real synthetic Linux filesystem I/O. They do not
        // claim the native bootstrap/UID/group/subreaper/JVM has been qualified.
        suite.Add("source-owned observations Linux disk: exact JAR and retained UA bytes, no file-owner restriction", () => ExactDisk().GetAwaiter().GetResult());
        suite.Add("source-owned observations Linux disk: absent, empty and oversized files remain distinct", () => Bounds().GetAwaiter().GetResult());
        suite.Add("source-owned observations Linux disk: symlinks and hardlinks are refused", () => Links().GetAwaiter().GetResult());
        suite.Add("source-owned observations Linux disk: swapped canonical root never yields stale bytes", () => ReplacedRoot().GetAwaiter().GetResult());
        suite.Add("source-owned observations Linux disk: replaced extension directory is refused during read", () => ReplacedDirectory().GetAwaiter().GetResult());
        suite.Add("source-owned observations Linux disk: cancellation and repeated disposal physically join read", () => Cancellation().GetAwaiter().GetResult());
        suite.Add("source-owned observations Linux disk: cancelled late physical fault is retained", () => LateFault().GetAwaiter().GetResult());
    }

    private static void ImmutableBytes()
    {
        byte[] bytes = [0, 255, 128, 1, 0];
        var original = (byte[])bytes.Clone();
        var observation = new SourceOwnedBytes("installed-jar", "copy.jar", new(1, 2), bytes);
        bytes[0] = 9; var exported = observation.Contents(); exported[1] = 0;
        Check.True(observation.Contents().AsSpan().SequenceEqual(original));
        Check.Equal(original.Length, observation.Length);
        Check.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), observation.Sha256);
        Check.True(!observation.ToString().Contains("loaded", StringComparison.OrdinalIgnoreCase));
    }

    private static void LeafPolicy()
    {
        foreach (var leaf in new[] { "extension.jar", "manual-copy_1.2+3.jar", "...jar" }) SourceOwnedObservation.RequireInstalledJarLeaf(leaf);
        foreach (var leaf in new[] { "", ".", "..", "copy.apk", "copy.JAR", "/copy.jar", "../copy.jar", "a/copy.jar", "a\\copy.jar", "C:copy.jar", "copy.jar\0", "copy jar.jar", new string('a', 1025) + ".jar" })
        {
            var error = Check.Throws<ArgumentException>(() => SourceOwnedObservation.RequireInstalledJarLeaf(leaf));
            Check.True(!error.Message.Contains(leaf, StringComparison.Ordinal) || leaf.Length < 3);
        }
    }

    private static void Unsupported()
    {
        using var invalid = new SafeFileHandle((IntPtr)(-1), false);
        var io = new BoundedWorkScheduler(1, 0);
        try { Check.Throws<PlatformNotSupportedException>(() => SourceOwnedObservation.AcceptInheritedRoot(invalid, new(1, 2), 1000, 2000, 1, io)); }
        finally { io.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static async Task ExactDisk()
    {
        using var temp = new Sandbox(); Prepare(temp.Path);
        var jar = new byte[] { 0x50, 0x4b, 0, 255, 1, 128 };
        File.WriteAllBytes(Path.Combine(temp.Path, "extensions", "manual-copy.jar"), jar);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.Combine(temp.Path, "extensions", "manual-copy.jar"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var status = Encoding.UTF8.GetBytes("# retained, not current process proof\nrevision=abcdef\nagent=loaded\n");
        File.WriteAllBytes(Path.Combine(temp.Path, "cache", SourceOwnedObservation.UserAgentStatusLeaf), status);
        await using var io = new BoundedWorkScheduler(1, 1);
        await using var observer = SourceOwnedObservation.Controlled(temp.Path, io);
        var first = await observer.ReadInstalledJarAsync("manual-copy.jar") ?? throw new InvalidOperationException("Missing synthetic JAR");
        var ua = await observer.ReadUserAgentStatusAsync() ?? throw new InvalidOperationException("Missing synthetic UA");
        Check.True(first.Contents().AsSpan().SequenceEqual(jar)); Check.True(ua.Contents().AsSpan().SequenceEqual(status));
        Check.Equal("retained-ua-status", ua.Kind);
        Check.Equal(Convert.ToHexStringLower(SHA256.HashData(jar)), first.Sha256);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.Combine(temp.Path, "extensions", "manual-copy.jar"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.WriteAllBytes(Path.Combine(temp.Path, "extensions", "manual-copy.jar"), [1, 2, 3]);
        Check.True(first.Contents().AsSpan().SequenceEqual(jar));
    }

    private static async Task Bounds()
    {
        using var temp = new Sandbox(); Prepare(temp.Path);
        await using var io = new BoundedWorkScheduler(1, 0);
        await using var observer = SourceOwnedObservation.Controlled(temp.Path, io);
        Check.True(await observer.ReadInstalledJarAsync("absent.jar") is null);
        Check.True(await observer.ReadUserAgentStatusAsync() is null);
        File.WriteAllBytes(Path.Combine(temp.Path, "extensions", "empty.jar"), []);
        await Throws<InvalidDataException>(() => observer.ReadInstalledJarAsync("empty.jar"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "extensions", "directory.jar"));
        await Throws<InvalidDataException>(() => observer.ReadInstalledJarAsync("directory.jar"));
        if (MkFifo(Path.Combine(temp.Path, "extensions", "fifo.jar"), 0x180) != 0) throw new IOException("Cannot create controlled FIFO fixture");
        await Throws<InvalidDataException>(() => observer.ReadInstalledJarAsync("fifo.jar"));
        var large = Path.Combine(temp.Path, "extensions", "large.jar");
        using (var output = File.Create(large)) output.SetLength(SourcePackage.MaximumBytes + 1L);
        await Throws<InvalidDataException>(() => observer.ReadInstalledJarAsync("large.jar"));
        File.WriteAllBytes(Path.Combine(temp.Path, "cache", SourceOwnedObservation.UserAgentStatusLeaf), new byte[SourceOwnedObservation.UserAgentStatusMaximumBytes + 1]);
        await Throws<InvalidDataException>(() => observer.ReadUserAgentStatusAsync());
    }

    private static async Task Links()
    {
        using var temp = new Sandbox(); Prepare(temp.Path);
        var real = Path.Combine(temp.Path, "real.jar"); File.WriteAllBytes(real, [1, 2, 3]);
        File.CreateSymbolicLink(Path.Combine(temp.Path, "extensions", "linked.jar"), real);
        if (Link(real, Path.Combine(temp.Path, "extensions", "hard.jar")) != 0) throw new IOException("Cannot create controlled hardlink fixture");
        await using var io = new BoundedWorkScheduler(1, 0);
        await using var observer = SourceOwnedObservation.Controlled(temp.Path, io);
        await Throws<IOException>(() => observer.ReadInstalledJarAsync("linked.jar"));
        await Throws<InvalidDataException>(() => observer.ReadInstalledJarAsync("hard.jar"));
    }

    private static async Task ReplacedRoot()
    {
        using var temp = new Sandbox(); var root = Path.Combine(temp.Path, "source"); Prepare(root);
        File.WriteAllBytes(Path.Combine(root, "extensions", "x.jar"), [1]);
        await using var io = new BoundedWorkScheduler(1, 0);
        await using var observer = SourceOwnedObservation.Controlled(root, io);
        Directory.Move(root, root + "-retained"); Prepare(root);
        File.WriteAllBytes(Path.Combine(root, "extensions", "x.jar"), [2]);
        await Throws<IOException>(() => observer.ReadInstalledJarAsync("x.jar"));
        Check.True(File.ReadAllBytes(Path.Combine(root + "-retained", "extensions", "x.jar")).AsSpan().SequenceEqual(new byte[] { 1 }));
        Check.True(File.ReadAllBytes(Path.Combine(root, "extensions", "x.jar")).AsSpan().SequenceEqual(new byte[] { 2 }));
    }

    private static async Task ReplacedDirectory()
    {
        using var temp = new Sandbox(); Prepare(temp.Path);
        File.WriteAllBytes(Path.Combine(temp.Path, "extensions", "x.jar"), [1, 2, 3]);
        var replaced = false;
        await using var io = new BoundedWorkScheduler(1, 0);
        await using var observer = SourceOwnedObservation.Controlled(temp.Path, io, () =>
        {
            if (replaced) return; replaced = true;
            Directory.Move(Path.Combine(temp.Path, "extensions"), Path.Combine(temp.Path, "old-extensions"));
            Directory.CreateDirectory(Path.Combine(temp.Path, "extensions"));
            File.WriteAllBytes(Path.Combine(temp.Path, "extensions", "x.jar"), [9, 9, 9]);
        });
        await Throws<IOException>(() => observer.ReadInstalledJarAsync("x.jar"));
    }

    private static async Task Cancellation()
    {
        using var temp = new Sandbox(); Prepare(temp.Path); File.WriteAllBytes(Path.Combine(temp.Path, "extensions", "x.jar"), [1]);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
        await using var io = new BoundedWorkScheduler(1, 0);
        var observer = SourceOwnedObservation.Controlled(temp.Path, io, () => { entered.Set(); release.Wait(); });
        Task? stop = null; Task? again = null;
        var read = observer.ReadInstalledJarAsync("x.jar", cancellation.Token);
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(5))); cancellation.Cancel();
            stop = observer.StopAsync(); again = observer.DisposeAsync().AsTask();
            Check.True(!read.IsCompleted && !stop.IsCompleted && !again.IsCompleted);
            await Throws<OperationCanceledException>(() => observer.ReadUserAgentStatusAsync());
        }
        finally { release.Set(); await Throws<OperationCanceledException>(() => read); if (stop is not null) await stop; if (again is not null) await again; await observer.DisposeAsync(); }
        Check.Equal((0, 0), io.Snapshot);
    }

    private static async Task LateFault()
    {
        using var temp = new Sandbox(); Prepare(temp.Path); File.WriteAllBytes(Path.Combine(temp.Path, "extensions", "x.jar"), [1]);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
        await using var io = new BoundedWorkScheduler(1, 0);
        await using var observer = SourceOwnedObservation.Controlled(temp.Path, io, () =>
        { entered.Set(); release.Wait(); throw new IOException("Controlled late physical read failure"); });
        var read = observer.ReadInstalledJarAsync("x.jar", cancellation.Token);
        try { Check.True(entered.Wait(TimeSpan.FromSeconds(5))); cancellation.Cancel(); Check.True(!read.IsCompleted); }
        finally { release.Set(); }
        var failure = await Throws<AggregateException>(() => read);
        Check.True(failure.Flatten().InnerExceptions.Any(error => error is IOException));
        Check.True(failure.Flatten().InnerExceptions.Any(error => error is OperationCanceledException));
        Check.Equal((0, 0), io.Snapshot);
    }

    private static void Prepare(string root) { Directory.CreateDirectory(Path.Combine(root, "extensions")); Directory.CreateDirectory(Path.Combine(root, "cache")); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int Link(string from, string to);
    [LibraryImport("libc", EntryPoint = "mkfifo", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int MkFifo(string path, uint mode);
}
