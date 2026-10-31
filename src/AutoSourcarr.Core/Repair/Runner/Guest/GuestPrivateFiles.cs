using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using AutoSourcarr.Core.Repair;

namespace AutoSourcarr.Core.Repair.Runner.Guest;

/// <summary>Guest-private files only. Native no-follow/type/link checks precede every open.</summary>
internal static partial class GuestPrivateFiles
{
    internal static void RequireAgent()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || User() != 1000)
            throw new PlatformNotSupportedException("The real guest artifact endpoint requires Linux x64 agent UID 1000; boot/self-test remains separate");
    }
    internal static void DirectoryPath(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (!current.Exists || current.LinkTarget is not null || (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Guest storage requires existing non-link directories");
            if (OperatingSystem.IsLinux())
            {
                var status = PathStatus(current.FullName);
                if ((status.Mode & 0xf000) != 0x4000 || status.User != 0 && status.User != User() || (status.Mode & 0x12) != 0)
                    throw new InvalidDataException("Guest storage ancestors have an unsafe owner or shared write access");
            }
            current = current.Parent;
        }
    }
    internal static void PrivateRoot(string path)
    {
        DirectoryPath(path);
        if (OperatingSystem.IsLinux())
        {
            var status = PathStatus(path);
            if (status.User != User() || (status.Mode & 0x3f) != 0) throw new InvalidDataException("Guest agent storage requires its current owner and private permissions");
        }
    }
    internal static void NewDirectory(string path)
    {
        PrivateRoot(Path.GetDirectoryName(path)!);
        var created = OperatingSystem.IsLinux() ? MakeDirectory(path, 0x1c0) == 0 : OperatingSystem.IsWindows() ? CreateDirectory(path, IntPtr.Zero) : throw new PlatformNotSupportedException("Native guest filesystem checks require Linux or controlled Windows tests");
        if (!created) throw new IOException("A guest private slot already exists or cannot be created");
        PrivateRoot(path); Sync(Path.GetDirectoryName(path)!);
    }
    internal sealed class DirectoryLease(SafeFileHandle handle, ulong identity, uint deviceHigh, uint deviceLow) : IDisposable
    {
        internal void AssertAt(string path)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this); PrivateRoot(path);
            if (OperatingSystem.IsLinux())
            {
                var status = PathStatus(path);
                if (status.Inode != identity || status.DeviceMajor != deviceHigh || status.DeviceMinor != deviceLow) throw new InvalidDataException("Guest directory identity was replaced");
            }
            else
            {
                using var current = DirectoryHandle(path);
                if (!Information(current, out var status) || (((ulong)status.IndexHigh << 32) | status.IndexLow) != identity || status.Volume != deviceHigh) throw new InvalidDataException("Controlled guest directory identity was replaced");
            }
        }
        public void Dispose() => handle.Dispose();
    }
    internal static DirectoryLease HoldDirectory(string path, bool allowRename = false)
    {
        PrivateRoot(path); var handle = DirectoryHandle(path, allowRename);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (Statx(checked((int)handle.DangerousGetHandle()), "", 0x1000, 0x10f, out var status) != 0 || (status.Mask & 0x10f) != 0x10f || (status.Mode & 0xf000) != 0x4000 || status.User != User() || (status.Mode & 0x3f) != 0)
                    throw new InvalidDataException("Cannot hold the private guest directory identity");
                return new(handle, status.Inode, status.DeviceMajor, status.DeviceMinor);
            }
            if (!Information(handle, out var windows) || (windows.Attributes & (0x10u | 0x400u)) != 0x10u) throw new InvalidDataException("Cannot hold the controlled guest directory identity");
            return new(handle, ((ulong)windows.IndexHigh << 32) | windows.IndexLow, windows.Volume, 0);
        }
        catch { handle.Dispose(); throw; }
    }
    private static SafeFileHandle DirectoryHandle(string path, bool allowRename = false)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsLinux())
        {
            var descriptor = OpenNative(path, 0x10000 | 0x80000 | 0x20000, 0); if (descriptor < 0) throw new IOException("Cannot hold the guest private directory"); handle = new((IntPtr)descriptor, true);
        }
        else if (OperatingSystem.IsWindows())
        {
            // A held slot also denies deletion/renaming on Windows; Linux identity checks
            // refuse stale paths while retaining the original directory descriptor.
            handle = CreateFile(path, 0x80, 1u | 2u | (allowRename ? 4u : 0u), IntPtr.Zero, 3, 0x200000 | 0x2000000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot hold the controlled guest directory"); }
        }
        else throw new PlatformNotSupportedException("Native guest directory ownership is unavailable"); return handle;
    }
    internal static FileStream Open(string path, bool write, bool create, long maximum)
    {
        if (maximum is < 0 or > RawCodeRules.MaximumExpanded) throw new ArgumentOutOfRangeException(nameof(maximum));
        DirectoryPath(Path.GetDirectoryName(path)!); SafeFileHandle handle;
        if (OperatingSystem.IsLinux())
        {
            var fd = OpenNative(path, (write ? 2 : 0) | (create ? 0x40 | 0x80 : 0) | 0x20000 | 0x800 | 0x80000, 0x180);
            if (fd < 0) throw new IOException("Cannot open a new or existing native guest regular file");
            handle = new((IntPtr)fd, true);
            try
            {
                if (Statx(fd, "", 0x1000, 0x20f, out var status) != 0 || (status.Mask & 0x20f) != 0x20f ||
                    (status.Mode & 0xf000) != 0x8000 || status.User != User() || status.Links != 1 || status.Size > (ulong)maximum || (status.Mode & 0x3f) != 0)
                    throw new InvalidDataException("Guest object is linked, special, oversized, foreign or public");
            }
            catch { handle.Dispose(); throw; }
        }
        else if (OperatingSystem.IsWindows())
        {
            handle = CreateFile(path, write ? 0xc0000000u : 0x80000000u, 1 | 2 | 4, IntPtr.Zero, create ? 1u : 3u, 0x200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot open a controlled guest regular-file fixture"); }
            try
            {
                if (!Information(handle, out var status) || status.Links != 1 || (status.Attributes & (0x10u | 0x400u)) != 0 || ((ulong)status.SizeHigh << 32 | status.SizeLow) > (ulong)maximum)
                    throw new InvalidDataException("Guest object is linked, special or oversized");
            }
            catch { handle.Dispose(); throw; }
        }
        else throw new PlatformNotSupportedException("Native guest filesystem checks are unavailable");
        try { return new(handle, write ? FileAccess.ReadWrite : FileAccess.Read); } catch { handle.Dispose(); throw; }
    }
    internal static void Verify(FileStream file, long length, string hash)
    {
        file.Position = 0; if (file.Length != length) throw new InvalidDataException("Guest object length changed");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[32768]; var remaining = length;
        while (remaining != 0)
        {
            var count = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0) throw new InvalidDataException("Guest object ended early"); digest.AppendData(buffer, 0, count); remaining -= count;
        }
        if (file.ReadByte() != -1 || file.Length != length || Convert.ToHexStringLower(digest.GetHashAndReset()) != hash) throw new InvalidDataException("Guest object hash or full length changed");
    }
    internal static byte[] Read(string path, long length, string hash)
    {
        using var input = Open(path, false, false, length); Verify(input, length, hash); input.Position = 0;
        var bytes = new byte[checked((int)length)]; input.ReadExactly(bytes); return bytes;
    }
    internal static void WriteNew(string path, ReadOnlySpan<byte> bytes)
    { using (var file = Open(path, true, true, bytes.Length)) { file.Write(bytes); file.Flush(true); } Sync(Path.GetDirectoryName(path)!); }
    internal static void Freeze(string path, bool executable)
    {
        if (OperatingSystem.IsLinux())
        {
            var expected = UnixFileMode.UserRead | (executable ? UnixFileMode.UserExecute : 0); File.SetUnixFileMode(path, expected);
            if (File.GetUnixFileMode(path) != expected) throw new InvalidDataException("Committed guest executable mode changed");
            using var file = Open(path, false, false, RawCodeRules.MaximumFile);
            if (Fsync(checked((int)file.SafeFileHandle.DangerousGetHandle())) != 0) throw new IOException("Cannot durably retain guest executable mode");
        }
        // Controlled Windows tests preserve metadata but cannot establish Linux executable-mode behavior.
    }
    internal static void CheckMode(string path, bool executable)
    {
        if (OperatingSystem.IsLinux() && File.GetUnixFileMode(path) != (UnixFileMode.UserRead | (executable ? UnixFileMode.UserExecute : 0))) throw new InvalidDataException("Committed guest executable mode changed");
    }
    internal static void Sync(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        var descriptor = OpenNative(path, 0x10000 | 0x80000 | 0x20000, 0);
        if (descriptor < 0) throw new IOException("Cannot open guest directory for durable publication");
        using var handle = new SafeFileHandle((IntPtr)descriptor, true);
        if (Fsync(descriptor) != 0) throw new IOException("Cannot durably publish guest storage");
    }
    private static LinuxStatus PathStatus(string path) => Statx(-100, path, 0x100, 0x30f, out var status) == 0 && (status.Mask & 0x10f) == 0x10f ? status : throw new IOException("Guest path status is unavailable");
    [StructLayout(LayoutKind.Explicit, Size = 256)] private struct LinuxStatus
    { [FieldOffset(0)] public uint Mask; [FieldOffset(16)] public uint Links; [FieldOffset(20)] public uint User; [FieldOffset(28)] public ushort Mode; [FieldOffset(32)] public ulong Inode; [FieldOffset(40)] public ulong Size; [FieldOffset(136)] public uint DeviceMajor; [FieldOffset(140)] public uint DeviceMinor; }
    [StructLayout(LayoutKind.Explicit, Size = 52)] private struct WindowsStatus
    { [FieldOffset(0)] public uint Attributes; [FieldOffset(28)] public uint Volume; [FieldOffset(32)] public uint SizeHigh; [FieldOffset(36)] public uint SizeLow; [FieldOffset(40)] public uint Links; [FieldOffset(44)] public uint IndexHigh; [FieldOffset(48)] public uint IndexLow; }
    [LibraryImport("libc", EntryPoint = "geteuid")] private static partial uint User();
    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int MakeDirectory(string path, uint mode);
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int OpenNative(string path, int flags, uint mode);
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int Statx(int directory, string path, int flags, uint mask, out LinuxStatus status);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)] private static partial int Fsync(int fd);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CreateDirectory(string path, IntPtr security);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] private static partial SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool Information(SafeFileHandle file, out WindowsStatus status);
}
