using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoSourcarr.Core.Sources.Runtime;

/// <summary>Linux descriptor-relative, no-follow reads below one native-bound source root.</summary>
internal sealed partial class SourceObservationRoot : IDisposable
{
    internal const string FixedRoot = "/home/suwayomi/.local/share/Tachidesk";
    private const int DirectoryFlags = 0x200000 | 0x10000 | 0x20000 | 0x80000; // O_PATH|DIRECTORY|NOFOLLOW|CLOEXEC
    private const uint RequiredStats = 0x3dd; // type, nlink, uid/gid, ctime/mtime, inode, size
    private readonly SafeFileHandle _root;
    private readonly string _canonicalRoot;
    private int _disposed;
    public SourceObservationRootIdentity Identity { get; }

    private SourceObservationRoot(SafeFileHandle root, SourceObservationRootIdentity identity, string canonicalRoot)
    { _root = root; Identity = identity; _canonicalRoot = canonicalRoot; }

    internal static SourceObservationRoot AcceptNative(SafeFileHandle heldRoot, SourceObservationRootIdentity identity,
        uint uid, uint gid, int expectedParent)
    {
        RequireLinux();
        if (uid is 0 or uint.MaxValue || gid is 0 or uint.MaxValue || expectedParent <= 0
            || GetPpid() != expectedParent || GetResUid(out var ruid, out var euid, out var suid) != 0
            || GetResGid(out var rgid, out var egid, out var sgid) != 0
            || ruid != uid || euid != uid || suid != uid || rgid != gid || egid != gid || sgid != gid
            || unchecked((uint)SetFsUid(uint.MaxValue)) != uid || unchecked((uint)SetFsGid(uint.MaxValue)) != gid
            || GetGroups(0, IntPtr.Zero) != 0 || Prctl(39, 0, 0, 0, 0) != 1)
            throw new IOException("The source observer did not enter its native-bound dropped identity.");
        var header = new CapabilityHeader { Version = 0x20080522 };
        if (CapGet(ref header, out var caps) != 0 || caps.Effective0 != 0 || caps.Effective1 != 0
            || caps.Permitted0 != 0 || caps.Permitted1 != 0 || caps.Inheritable0 != 0 || caps.Inheritable1 != 0
            || Prctl(4, 0, 0, 0, 0) != 0 || Prctl(3, 0, 0, 0, 0) != 0)
            throw new IOException("The source observer capabilities or dumpability are unsafe.");
        return Duplicate(heldRoot, identity, FixedRoot);
    }

    internal static SourceObservationRoot Controlled(string syntheticRoot)
    {
        RequireLinux();
        var path = Path.GetFullPath(syntheticRoot);
        using var root = OpenAbsolute(path);
        var status = Status(root);
        return Duplicate(root, Id(status), path);
    }

    private static SourceObservationRoot Duplicate(SafeFileHandle supplied, SourceObservationRootIdentity expected, string canonical)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        var retained = false;
        int descriptor;
        try
        {
            supplied.DangerousAddRef(ref retained);
            descriptor = Fcntl(checked((int)supplied.DangerousGetHandle()), 1030, 3); // F_DUPFD_CLOEXEC
        }
        finally { if (retained) supplied.DangerousRelease(); }
        if (descriptor < 0) throw new IOException("Cannot retain the native source root descriptor.");
        var root = new SourceObservationRoot(new((IntPtr)descriptor, true), expected, canonical);
        try { root.RequireCurrent(); return root; }
        catch { root.Dispose(); throw; }
    }

    internal void RequireCurrent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var held = Status(_root);
        RequireDirectory(held);
        if (Id(held) != Identity) throw new IOException("The held source root identity changed.");
        using var current = OpenAbsolute(_canonicalRoot);
        var path = Status(current);
        RequireDirectory(path);
        if (Id(path) != Identity) throw new IOException("The fixed source data root was renamed or replaced.");
    }

    internal ReadLease Open(string directory, string leaf, int maximum)
    {
        RequireCurrent();
        if (directory is not ("extensions" or "cache") || leaf.Length == 0 || leaf.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new InvalidDataException("Invalid fixed source observation target.");
        var parent = Child(_root, directory, DirectoryFlags);
        SafeFileHandle? file = null;
        try
        {
            var parentStatus = Status(parent); RequireDirectory(parentStatus);
            file = Child(parent, leaf, 0x20000 | 0x800 | 0x80000); // RDONLY|NOFOLLOW|NONBLOCK|CLOEXEC
            var status = Status(file);
            if ((status.Mask & RequiredStats) != RequiredStats || (status.Mode & 0xf000) != 0x8000
                || status.Links != 1 || status.Size > (ulong)maximum)
                throw new InvalidDataException("Source observation must be a bounded, single-link regular file.");
            // Do not require source UID==file/root owner: group/ACL access and root0644 JARs are legitimate.
            var stream = new FileStream(file, FileAccess.Read);
            file = null;
            return new(parent, stream, Id(parentStatus), Fact(status));
        }
        catch { file?.Dispose(); parent.Dispose(); throw; }
    }

    internal void RequireUnchanged(ReadLease read, string directory, string leaf, int maximum)
    {
        var after = Fact(Status(read.File.SafeFileHandle));
        if (after != read.Before || Id(Status(read.Directory)) != read.DirectoryBefore)
            throw new IOException("Source observation changed during its physical read.");
        RequireCurrent();
        using var reopened = Open(directory, leaf, maximum);
        if (reopened.Before != read.Before || reopened.DirectoryBefore != read.DirectoryBefore)
            throw new IOException("Source observation path changed during its physical read.");
    }

    internal sealed class ReadLease(SafeFileHandle directory, FileStream file, SourceObservationRootIdentity directoryBefore, FileFact before) : IDisposable
    {
        private int _disposed;
        internal SafeFileHandle Directory { get; } = directory;
        internal FileStream File { get; } = file;
        internal SourceObservationRootIdentity DirectoryBefore { get; } = directoryBefore;
        internal FileFact Before { get; } = before;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { File.Dispose(); } finally { Directory.Dispose(); }
        }
    }

    internal readonly record struct FileFact(SourceObservationRootIdentity Identity, ulong Size, uint Links,
        ushort Mode, uint Uid, uint Gid, long ModifiedSeconds, uint ModifiedNanoseconds, long ChangedSeconds, uint ChangedNanoseconds);
    private static FileFact Fact(LinuxStatus status) => new(Id(status), status.Size, status.Links, status.Mode,
        status.Uid, status.Gid, status.ModifiedSeconds, status.ModifiedNanoseconds, status.ChangedSeconds, status.ChangedNanoseconds);
    private static SourceObservationRootIdentity Id(LinuxStatus status) => new(
        (status.DeviceMinor & 0xffUL) | ((status.DeviceMajor & 0xfffUL) << 8)
        | ((status.DeviceMinor & ~0xffUL) << 12) | ((status.DeviceMajor & ~0xfffUL) << 32), status.Inode);
    private static void RequireDirectory(LinuxStatus status)
    {
        if ((status.Mask & 0x105) != 0x105 || (status.Mode & 0xf000) != 0x4000 || status.Links == 0)
            throw new IOException("Source observation directory is missing, linked or changed.");
    }
    private static LinuxStatus Status(SafeFileHandle handle)
    {
        if (Statx(checked((int)handle.DangerousGetHandle()), "", 0x1000, 0x7ff, out var status) != 0)
            throw new IOException("Cannot inspect a held source observation descriptor.");
        return status;
    }
    private static SafeFileHandle Child(SafeFileHandle parent, string leaf, int flags)
    {
        var descriptor = OpenAt(checked((int)parent.DangerousGetHandle()), leaf, flags, 0);
        if (descriptor >= 0) return new((IntPtr)descriptor, true);
        if (Marshal.GetLastPInvokeError() == 2) throw new FileNotFoundException("The retained source observation is absent.");
        throw new IOException("Cannot open a no-follow source observation descriptor.");
    }
    private static SafeFileHandle OpenAbsolute(string path)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal)) throw new IOException("The fixed source root is not absolute.");
        var first = OpenPath("/", DirectoryFlags, 0);
        if (first < 0) throw new IOException("Cannot inspect the fixed source root chain.");
        var current = new SafeFileHandle((IntPtr)first, true);
        try
        {
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part is "." or "..") throw new IOException("The fixed source root contains an invalid component.");
                var next = Child(current, part, DirectoryFlags);
                try { RequireDirectory(Status(next)); } catch { next.Dispose(); throw; }
                current.Dispose(); current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }
    internal static bool IsSupported => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    private static void RequireLinux()
    { if (!IsSupported) throw new PlatformNotSupportedException("Native held-root source observations require Linux x64 descriptor authority."); }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _root.Dispose(); }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(24)] public uint Gid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(96)] public long ChangedSeconds;
        [FieldOffset(104)] public uint ChangedNanoseconds;
        [FieldOffset(112)] public long ModifiedSeconds;
        [FieldOffset(120)] public uint ModifiedNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CapabilityHeader { public uint Version; public int Pid; }
    [StructLayout(LayoutKind.Sequential)] private struct CapabilityPair
    { public uint Effective0, Permitted0, Inheritable0, Effective1, Permitted1, Inheritable1; }
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int OpenPath(string path, int flags, uint mode);
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int OpenAt(int directory, string name, int flags, uint mode);
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static partial int Fcntl(int descriptor, int command, int argument);
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int Statx(int directory, string name, int flags, uint mask, out LinuxStatus status);
    [LibraryImport("libc", EntryPoint = "getresuid")] private static partial int GetResUid(out uint real, out uint effective, out uint saved);
    [LibraryImport("libc", EntryPoint = "getresgid")] private static partial int GetResGid(out uint real, out uint effective, out uint saved);
    [LibraryImport("libc", EntryPoint = "setfsuid")] private static partial int SetFsUid(uint uid);
    [LibraryImport("libc", EntryPoint = "setfsgid")] private static partial int SetFsGid(uint gid);
    [LibraryImport("libc", EntryPoint = "getppid")] private static partial int GetPpid();
    [LibraryImport("libc", EntryPoint = "getgroups")] private static partial int GetGroups(int size, IntPtr groups);
    [LibraryImport("libc", EntryPoint = "capget")] private static partial int CapGet(ref CapabilityHeader header, out CapabilityPair capabilities);
    [LibraryImport("libc", EntryPoint = "prctl")] private static partial int Prctl(int option, nuint a, nuint b, nuint c, nuint d);
}
