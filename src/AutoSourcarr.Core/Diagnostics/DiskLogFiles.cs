using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoSourcarr.Core.Diagnostics;

/// <summary>Fixed rotating diagnostic files; linked and non-regular leaves are refused.</summary>
internal sealed partial class DiskLogFiles(string root, int maximumBytes, int fileCount)
{
    private readonly string _root = Path.GetFullPath(root);
    private string Name(int index) => "support.jsonl" + (index > 0 ? "." + index : "");

    public void Append(IEnumerable<byte[]> lines)
    {
        CheckDirectory(create: true);
        using var directory = OpenDirectory();
        for (var i = 0; i < fileCount; i++) CheckExisting(directory, Name(i));
        FileStream? output = null;
        ulong size = 0;
        try
        {
            foreach (var line in lines)
            {
                if (line.Length > maximumBytes) throw new IOException("Diagnostic record exceeds its disk bound");
                if (output is null) size = CheckExisting(directory, Name(0))?.Size ?? 0;
                if (size + (ulong)line.Length > (ulong)maximumBytes)
                {
                    output?.Dispose(); output = null;
                    for (var i = fileCount - 1; i > 0; i--)
                        if (CheckExisting(directory, Name(i - 1)) is not null) Rename(directory, Name(i - 1), Name(i));
                    if (fileCount == 1) Delete(directory, Name(0));
                    size = 0;
                }
                if (output is null) { output = OpenRegular(directory, Name(0), append: true); output.Seek(0, SeekOrigin.End); }
                output.Write(line); size += (ulong)line.Length;
            }
        }
        finally { output?.Dispose(); }
    }
    public IReadOnlyList<byte[]> Read()
    {
        CheckDirectory(create: false);
        if (!Directory.Exists(_root)) return [];
        using var directory = OpenDirectory();
        var values = new List<byte[]>();
        for (var i = fileCount - 1; i >= 0; i--)
        {
            if (CheckExisting(directory, Name(i)) is null) continue;
            using var input = OpenRegular(directory, Name(i), append: false);
            using var output = new MemoryStream(); var buffer = new byte[8192];
            while (output.Length < maximumBytes)
            {
                var count = input.Read(buffer, 0, Math.Min(buffer.Length, maximumBytes - (int)output.Length));
                if (count == 0) break; output.Write(buffer, 0, count);
            }
            values.Add(output.ToArray());
        }
        return values;
    }

    private void CheckDirectory(bool create)
    {
        var info = new DirectoryInfo(_root);
        if (info.LinkTarget is not null || File.Exists(_root) || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Unsafe diagnostic directory");
        if (create)
        {
            Directory.CreateDirectory(_root); info.Refresh();
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe diagnostic directory");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
    private SafeFileHandle? OpenDirectory()
    {
        if (!OperatingSystem.IsLinux()) return null;
        var descriptor = Open(_root, 0x10000 | 0x20000 | 0x80000, 0);
        if (descriptor < 0) throw new IOException("Cannot open the private diagnostic directory");
        return new((IntPtr)descriptor, ownsHandle: true);
    }
    private FileIdentity? CheckExisting(SafeFileHandle? directory, string name)
    {
        if (OperatingSystem.IsLinux())
        {
            if (Statx(Fd(directory!), name, 0x100, 0x305, out var status) != 0)
            {
                if (Marshal.GetLastPInvokeError() == 2) return null;
                throw new IOException("Cannot inspect diagnostic log path");
            }
            return LinuxIdentity(status);
        }
        var info = new FileInfo(Path.Combine(_root, name));
        if (info.LinkTarget is not null || Directory.Exists(info.FullName) || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Unsafe diagnostic log path");
        if (!info.Exists) return null;
        using var input = OpenRegular(null, name, append: false);
        return Identity(input.SafeFileHandle);
    }
    private FileStream OpenRegular(SafeFileHandle? directory, string name, bool append)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsLinux())
        {
            var before = CheckExisting(directory, name);
            var flags = 0x20000 | 0x800 | 0x80000 | (append ? 1 | 0x400 | 0x40 : 0);
            if (append && before is null) flags |= 0x80;
            var descriptor = OpenAt(Fd(directory!), name, flags, 0x180);
            if (descriptor < 0) throw new IOException("Cannot open a regular diagnostic log");
            handle = new((IntPtr)descriptor, ownsHandle: true);
            try
            {
                var actual = Identity(handle);
                if (before is { } previous && (actual.Device != previous.Device || actual.Inode != previous.Inode)) throw new IOException("Diagnostic log identity changed");
            }
            catch { handle.Dispose(); throw; }
        }
        else if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(_root, name);
            handle = CreateFile(path, append ? 0x40000000u : 0x80000000u, 1, IntPtr.Zero, append ? 4u : 3u, 0x200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot open a regular diagnostic log"); }
            try { _ = Identity(handle); } catch { handle.Dispose(); throw; }
        }
        else throw new IOException("Persistent diagnostic files are unsupported on this platform");
        try { return new(handle, append ? FileAccess.Write : FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }
    private static FileIdentity Identity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
        {
            if (Statx(Fd(handle), "", 0x1000, 0x305, out var status) != 0) throw new IOException("Cannot inspect diagnostic file handle");
            return LinuxIdentity(status);
        }
        if (!GetFileInformation(handle, out var windows) || windows.Links != 1 || (windows.Attributes & (0x10u | 0x400u)) != 0)
            throw new IOException("Unsafe diagnostic file handle");
        return new(((ulong)windows.SizeHigh << 32) | windows.SizeLow, windows.Volume, ((ulong)windows.IndexHigh << 32) | windows.IndexLow);
    }
    private static FileIdentity LinuxIdentity(LinuxFileStatus status)
    {
        if ((status.Mask & 5) != 5 || (status.Mode & 0xF000) != 0x8000 || status.Links != 1) throw new IOException("Unsafe diagnostic file type or link count");
        return new(status.Size, ((ulong)status.DeviceMajor << 32) | status.DeviceMinor, status.Inode);
    }
    private void Rename(SafeFileHandle? directory, string source, string target)
    {
        if (OperatingSystem.IsLinux())
        { if (RenameAt(Fd(directory!), source, Fd(directory!), target) != 0) throw new IOException("Cannot rotate diagnostic log"); }
        else File.Move(Path.Combine(_root, source), Path.Combine(_root, target), overwrite: true);
    }
    private void Delete(SafeFileHandle? directory, string name)
    {
        if (OperatingSystem.IsLinux()) { if (UnlinkAt(Fd(directory!), name, 0) != 0) throw new IOException("Cannot rotate diagnostic log"); }
        else File.Delete(Path.Combine(_root, name));
    }
    private static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());
    private readonly record struct FileIdentity(ulong Size, ulong Device, ulong Inode);
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }
    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct WindowsFileStatus
    {
        [FieldOffset(0)] public uint Attributes;
        [FieldOffset(28)] public uint Volume;
        [FieldOffset(32)] public uint SizeHigh;
        [FieldOffset(36)] public uint SizeLow;
        [FieldOffset(40)] public uint Links;
        [FieldOffset(44)] public uint IndexHigh;
        [FieldOffset(48)] public uint IndexLow;
    }
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags, uint mode);
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(int directory, string name, int flags, uint mode);
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int directory, string name, int flags, uint mask, out LinuxFileStatus status);
    [LibraryImport("libc", EntryPoint = "renameat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt(int sourceDirectory, string source, int targetDirectory, string target);
    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnlinkAt(int directory, string name, int flags);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint attributes, IntPtr template);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformation(SafeFileHandle handle, out WindowsFileStatus status);
}
