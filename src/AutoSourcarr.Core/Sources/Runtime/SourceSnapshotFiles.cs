using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace AutoSourcarr.Core.Sources.Runtime;

/// <summary>Reads only an issued immutable snapshot. Native Release owns removal,
/// including cases where this reader never gets filesystem admission.</summary>
internal abstract partial class SourceSnapshotFiles : IDisposable
{
    internal const string FixedRoot = "/run/autosourcarr-private/source-observations";
    internal const uint RequiredFileStats = 0x3df; // all used fields except atime
    internal abstract NativeSourceOwnerRoot Identity { get; }
    internal abstract void RequireCurrent();
    internal abstract Input OpenTicket(string ticket);
    public abstract void Dispose();

    internal readonly record struct FileFact(NativeSourceOwnerRoot Identity, ulong Size, uint Links, uint Mode,
        uint Uid, uint Gid, long ModifiedSeconds, uint ModifiedNanoseconds, long ChangedSeconds, uint ChangedNanoseconds);
    internal readonly record struct FileStatus(uint Mask, FileFact Fact);
    internal sealed class Input(Stream stream, Func<FileStatus> status) : IDisposable
    {
        internal Stream Stream { get; } = stream;
        internal FileStatus Status() => status();
        public void Dispose() => Stream.Dispose();
    }

    internal byte[] Read(NativeSourceObservationMessage message, NativeSourceOwnerProofMessage owner,
        Action requireLease, CancellationToken token)
    {
        var result = message.Result;
        NativeSourceObservationProtocol.RequireTicket(message.Ticket);
        if (result.Outcome != NativeSourceSnapshotOutcome.Complete || result.SourceRoot != owner.Root)
            throw new InvalidDataException("Only a completed native snapshot can be read.");
        byte[]? contents = null;
        try
        {
            token.ThrowIfCancellationRequested(); requireLease(); RequireCurrent(); RequireExchange(result);
            using (var input = OpenTicket(message.Ticket))
            {
                var before = RequireOutput(input.Status(), result, owner);
                contents = new byte[checked((int)result.Length)];
                var offset = 0;
                while (offset < contents.Length)
                {
                    token.ThrowIfCancellationRequested();
                    var got = input.Stream.Read(contents, offset, Math.Min(16384, contents.Length - offset));
                    if (got == 0) throw new IOException("The issued source snapshot changed length.");
                    offset += got;
                }
                token.ThrowIfCancellationRequested();
                if (input.Stream.ReadByte() != -1) throw new IOException("The issued source snapshot exceeds its bound.");
                if (RequireOutput(input.Status(), result, owner) != before) throw new IOException("The held source snapshot changed during reading.");
                RequireCurrent(); RequireExchange(result);
                using (var named = OpenTicket(message.Ticket))
                    if (RequireOutput(named.Status(), result, owner) != before) throw new IOException("The issued snapshot name was replaced.");
                var digest = SHA256.HashData(contents); var expected = Convert.FromHexString(result.Sha256);
                if (!CryptographicOperations.FixedTimeEquals(digest, expected)) throw new InvalidDataException("Snapshot bytes differ from the native receipt.");
                token.ThrowIfCancellationRequested(); requireLease(); RequireCurrent(); RequireExchange(result);
            }
            return contents;
        }
        catch
        {
            if (contents is not null) CryptographicOperations.ZeroMemory(contents);
            throw;
        }
    }
    private void RequireExchange(NativeSourceSnapshotResult result)
    {
        if (Identity.Inode == 0 || Identity != result.ExchangeRoot) throw new IOException("The actual snapshot directory differs from the native receipt.");
    }
    internal static FileFact RequireOutput(FileStatus status, NativeSourceSnapshotResult result, NativeSourceOwnerProofMessage owner)
    {
        var fact = status.Fact;
        var maximum = result.Kind switch
        {
            NativeSourceSnapshotKind.InstalledJar => NativeSourceSnapshotResult.JarMaximumBytes,
            NativeSourceSnapshotKind.RetainedUserAgent => NativeSourceSnapshotResult.UserAgentMaximumBytes,
            _ => throw new InvalidDataException("Invalid fixed source snapshot kind.")
        };
        if ((status.Mask & RequiredFileStats) != RequiredFileStats || fact.Mode != 0x8124 || fact.Links != 1
            || fact.Uid != owner.Uid || fact.Gid != owner.Gid || fact.Identity != result.Output || fact.Identity.Inode == 0
            || fact.Size != result.Length || fact.Size > maximum || result.Kind == NativeSourceSnapshotKind.InstalledJar && fact.Size == 0
            || fact.ModifiedNanoseconds >= 1000000000 || fact.ChangedNanoseconds >= 1000000000)
            throw new IOException("The issued snapshot is writable, linked, replaced or outside its exact native identity.");
        return fact;
    }
    internal static SourceSnapshotFiles OpenFixed(NativeSourceOwnerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The native snapshot receiver requires Linux x64.");
        connection.RequireSnapshotMainIdentity(); return LinuxFiles.Create(connection);
    }

    private sealed partial class LinuxFiles : SourceSnapshotFiles
    {
        private const int DirectoryFlags = 0x200000 | 0x10000 | 0x20000 | 0x80000;
        private const int FileFlags = 0x20000 | 0x800 | 0x80000;
        private readonly NativeSourceOwnerConnection _connection;
        private readonly SafeFileHandle _root;
        private readonly NativeSourceOwnerRoot _identity;
        private int _disposed;
        private LinuxFiles(NativeSourceOwnerConnection connection, SafeFileHandle root, NativeSourceOwnerRoot identity)
        { _connection = connection; _root = root; _identity = identity; }
        internal override NativeSourceOwnerRoot Identity => _identity;
        internal static LinuxFiles Create(NativeSourceOwnerConnection connection)
        {
            var root = OpenFixedPath(connection.OwnerMessage.Uid);
            try
            {
                var status = Status(root); RequireDirectory(status, connection.OwnerMessage.Uid, 568, 0x1f8);
                return new(connection, root, Id(status));
            }
            catch { root.Dispose(); throw; }
        }
        internal override void RequireCurrent()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _connection.RequireSnapshotMainIdentity();
            var held = Status(_root); RequireDirectory(held, _connection.OwnerMessage.Uid, 568, 0x1f8);
            if (Id(held) != _identity) throw new IOException("The held snapshot directory changed.");
            using var named = OpenFixedPath(_connection.OwnerMessage.Uid); var current = Status(named);
            RequireDirectory(current, _connection.OwnerMessage.Uid, 568, 0x1f8);
            if (Id(current) != _identity) throw new IOException("The fixed snapshot directory was replaced.");
        }
        internal override Input OpenTicket(string ticket)
        {
            NativeSourceObservationProtocol.RequireTicket(ticket); RequireCurrent();
            SafeFileHandle? handle = Child(_root, ticket, FileFlags);
            try
            {
                var stream = new FileStream(handle, FileAccess.Read); handle = null;
                return new(stream, () => Convert(Status(stream.SafeFileHandle)));
            }
            finally { handle?.Dispose(); }
        }
        public override void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _root.Dispose(); }
        private static SafeFileHandle OpenFixedPath(uint uid)
        {
            var descriptor = OpenPath("/", DirectoryFlags, 0);
            if (descriptor < 0) throw new IOException("Cannot open the fixed snapshot directory chain.");
            var current = new SafeFileHandle((IntPtr)descriptor, true);
            try
            {
                foreach (var part in FixedRoot.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    var next = Child(current, part, DirectoryFlags);
                    try
                    {
                        var status = Status(next); RequireDirectory(status);
                        if (part == "autosourcarr-private") RequireDirectory(status, 568, 568, 0x1c0);
                        if (part == "source-observations") RequireDirectory(status, uid, 568, 0x1f8);
                    }
                    catch { next.Dispose(); throw; }
                    current.Dispose(); current = next;
                }
                return current;
            }
            catch { current.Dispose(); throw; }
        }
        private static void RequireDirectory(LinuxStatus status, uint? uid = null, uint? gid = null, uint? mode = null)
        {
            if ((status.Mask & 0x11f) != 0x11f || (status.Mode & 0xf000) != 0x4000 || status.Links == 0 || status.Inode == 0
                || uid is not null && status.Uid != uid || gid is not null && status.Gid != gid || mode is not null && (status.Mode & 0xfff) != mode)
                throw new IOException("The fixed snapshot directory ownership, mode or identity is unsafe.");
        }
        private static FileStatus Convert(LinuxStatus s) => new(s.Mask, new(Id(s), s.Size, s.Links, s.Mode, s.Uid, s.Gid,
            s.ModifiedSeconds, s.ModifiedNanoseconds, s.ChangedSeconds, s.ChangedNanoseconds));
        private static NativeSourceOwnerRoot Id(LinuxStatus s) => new((s.DeviceMinor & 0xffUL) | ((s.DeviceMajor & 0xfffUL) << 8)
            | ((s.DeviceMinor & ~0xffUL) << 12) | ((s.DeviceMajor & ~0xfffUL) << 32), s.Inode);
        private static LinuxStatus Status(SafeFileHandle handle)
        {
            if (Statx(checked((int)handle.DangerousGetHandle()), "", 0x1000, 0x7ff, out var status) != 0)
                throw new IOException("Cannot inspect the held snapshot descriptor.");
            return status;
        }
        private static SafeFileHandle Child(SafeFileHandle parent, string leaf, int flags)
        {
            var descriptor = OpenAt(checked((int)parent.DangerousGetHandle()), leaf, flags, 0);
            if (descriptor >= 0) return new((IntPtr)descriptor, true);
            if (Marshal.GetLastPInvokeError() == 2) throw new FileNotFoundException("The issued snapshot ticket is absent.");
            throw new IOException("Cannot open a no-follow snapshot descriptor.");
        }
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
        [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int OpenPath(string path, int flags, uint mode);
        [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int OpenAt(int directory, string name, int flags, uint mode);
        [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int Statx(int directory, string name, int flags, uint mask, out LinuxStatus status);
    }
}
