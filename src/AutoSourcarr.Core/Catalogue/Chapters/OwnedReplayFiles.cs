using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Chapters;

/// <summary>Per-operation temporary handles below an already-owned private maintenance directory.</summary>
internal sealed partial class OwnedReplayFiles : IDisposable
{
    private readonly string _root, _name, _path;
    private readonly SafeFileHandle _rootHandle, _jobHandle;
    private readonly DirectoryIdentity _jobIdentity;
    private readonly List<FileStream> _files = [];
    private readonly HashSet<string> _namedLinuxFiles = [];
    private bool _disposed;
    public OwnedReplayFiles(string privateWorkDirectory)
    {
        _root = Path.GetFullPath(privateWorkDirectory);
        if (!Directory.Exists(_root)) throw new IOException("An existing private chapter maintenance directory is required");
        PrivateDocumentStore.ValidateDirectory(_root);
        if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(_root) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("Chapter maintenance directory must be private to the instance owner");
        _name = "chapter-replay-" + Guid.NewGuid().ToString("N"); _path = Path.Combine(_root, _name);
        _rootHandle = OpenDirectory(_root);
        var created = false; DirectoryIdentity? createdIdentity = null; SafeFileHandle? allocatedJob = null;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (MkdirAt(Fd(_rootHandle), _name, 0x1c0) != 0) throw new IOException("Cannot create private chapter job directory"); created = true;
                createdIdentity = NamedLinuxDirectory();
                var fd = OpenAt(Fd(_rootHandle), _name, 0x10000 | 0x20000 | 0x80000, 0);
                if (fd < 0) throw new IOException("Cannot own private chapter job directory");
                _jobHandle = new((IntPtr)fd, true); allocatedJob = _jobHandle;
                _jobIdentity = LinuxDirectory(_jobHandle); if (_jobIdentity != createdIdentity.Value) throw new IOException("Chapter job directory identity changed");
            }
            else
            {
                if (!CreateDirectory(_path, IntPtr.Zero)) throw new IOException("Cannot create private chapter job directory"); created = true;
                _jobHandle = OpenDirectory(_path, deleteAccess: true); allocatedJob = _jobHandle; _jobIdentity = WindowsDirectory(_jobHandle); createdIdentity = _jobIdentity;
            }
        }
        catch (Exception error)
        {
            try
            {
                if (created)
                {
                    if (OperatingSystem.IsLinux() && createdIdentity.HasValue)
                    {
                        if (allocatedJob is not null && LinuxDirectory(allocatedJob) != createdIdentity.Value) throw new IOException("Incomplete chapter job identity changed");
                        RemoveLinuxJob(createdIdentity.Value);
                    }
                    else throw new IOException("Incomplete chapter directory retained because its native identity could not be owned");
                }
            }
            catch (Exception cleanup) { throw new IOException("Chapter workspace creation and cleanup failed", new AggregateException(error, cleanup)); }
            finally { try { allocatedJob?.Dispose(); } finally { _rootHandle.Dispose(); } }
            throw;
        }
    }
    public FileStream Create()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var name = Guid.NewGuid().ToString("N") + ".tmp"; SafeFileHandle handle;
        if (OperatingSystem.IsLinux())
        {
            var fd = OpenAt(Fd(_jobHandle), name, 2 | 0x40 | 0x80 | 0x20000 | 0x80000 | 0x800, 0x180);
            if (fd < 0) throw new IOException("Cannot create owned chapter temporary file");
            _namedLinuxFiles.Add(name);
            handle = new((IntPtr)fd, true);
            try
            {
                if (Statx(fd, "", 0x1000, 0x20f, out var status) != 0 || (status.Mask & 0xf) != 0xf || (status.Mode & 0xf000) != 0x8000 || (status.Mode & 0xfff) != 0x180 || status.User != GetEuid() || status.Links != 1 || status.Size != 0)
                    throw new IOException("Unsafe chapter temporary file handle");
                // Anonymous after creation; no later lookup can substitute an attacker-written file.
                if (UnlinkAt(Fd(_jobHandle), name, 0) != 0) throw new IOException("Cannot unlink owned chapter temporary file"); _namedLinuxFiles.Remove(name);
            }
            catch { handle.Dispose(); throw; }
        }
        else
        {
            handle = CreateFile(Path.Combine(_path, name), 0xc0010000, 0, IntPtr.Zero, 1, 0x200000 | 0x4000000 | 0x100, IntPtr.Zero);
            try
            {
                if (handle.IsInvalid || !GetFileInformation(handle, out var status) || status.Links != 1 || (status.Attributes & (0x10u | 0x400u)) != 0 || status.SizeHigh != 0 || status.SizeLow != 0)
                    throw new IOException("Unsafe chapter temporary file handle");
            }
            catch { handle.Dispose(); throw; }
        }
        try { var stream = new FileStream(handle, FileAccess.ReadWrite, 8192, isAsync: false); _files.Add(stream); return stream; }
        catch { handle.Dispose(); throw; }
    }
    public void Release(FileStream file) { if (_files.Remove(file)) file.Dispose(); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        var errors = new List<Exception>();
        foreach (var file in _files) try { file.Dispose(); } catch (Exception error) { errors.Add(error); }
        if (OperatingSystem.IsLinux()) foreach (var name in _namedLinuxFiles) if (UnlinkAt(Fd(_jobHandle), name, 0) != 0) errors.Add(new IOException("Cannot unlink incomplete owned chapter file"));
        try
        {
            if (OperatingSystem.IsLinux())
            { if (LinuxDirectory(_jobHandle) != _jobIdentity) throw new IOException("Owned chapter directory identity changed"); RemoveLinuxJob(_jobIdentity); }
            else
            {
                if (WindowsDirectory(_jobHandle) != _jobIdentity) throw new IOException("Owned chapter directory identity changed");
                var disposition = new DeleteDisposition { Delete = 1 };
                if (!SetFileInformation(_jobHandle, 4, ref disposition, 1)) throw new IOException("Cannot remove chapter directory by its owned handle");
            }
        }
        catch (Exception error) { errors.Add(error); }
        finally { try { _jobHandle.Dispose(); } finally { _rootHandle.Dispose(); } }
        if (errors.Count != 0) throw new IOException("Owned chapter cleanup did not finish", new AggregateException(errors));
    }
    private DirectoryIdentity NamedLinuxDirectory()
    {
        if (Statx(Fd(_rootHandle), _name, 0x100, 0x10f, out var status) != 0) throw new IOException("Cannot inspect owned chapter directory name");
        return CheckLinuxDirectory(status);
    }
    private void RemoveLinuxJob(DirectoryIdentity expected)
    {
        _ = LinuxDirectory(_rootHandle);
        if (NamedLinuxDirectory() != expected) throw new IOException("Chapter directory name was replaced; its namesake was retained");
        if (UnlinkAt(Fd(_rootHandle), _name, 0x200) != 0) throw new IOException("Cannot remove owned chapter job directory");
    }
    private static DirectoryIdentity LinuxDirectory(SafeFileHandle handle)
    {
        if (Statx(Fd(handle), "", 0x1000, 0x10f, out var status) != 0) throw new IOException("Cannot inspect owned chapter directory handle");
        return CheckLinuxDirectory(status);
    }
    private static DirectoryIdentity CheckLinuxDirectory(LinuxStatus status)
    {
        if ((status.Mask & 0x10b) != 0x10b || (status.Mode & 0xf000) != 0x4000 || (status.Mode & 0xfff) != 0x1c0 || status.User != GetEuid())
            throw new IOException("Chapter directory handle must belong to the current owner with private 0700 permissions");
        return new(((ulong)status.DeviceMajor << 32) | status.DeviceMinor, status.Inode);
    }
    private static DirectoryIdentity WindowsDirectory(SafeFileHandle handle)
    {
        if (handle.IsInvalid || !GetFileInformation(handle, out var status) || (status.Attributes & 0x10) == 0 || (status.Attributes & 0x400) != 0)
            throw new IOException("Cannot own a regular chapter directory handle");
        return new(status.Volume, ((ulong)status.IndexHigh << 32) | status.IndexLow);
    }
    private static SafeFileHandle OpenDirectory(string path, bool deleteAccess = false)
    {
        if (OperatingSystem.IsLinux())
        {
            var fd = Open(path, 0x10000 | 0x20000 | 0x80000, 0); if (fd < 0) throw new IOException("Cannot own chapter maintenance directory");
            var owned = new SafeFileHandle((IntPtr)fd, true); try { _ = LinuxDirectory(owned); return owned; } catch { owned.Dispose(); throw; }
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Private chapter replay needs native file ownership");
        var result = CreateFile(path, deleteAccess ? 0x80010000u : 0x80000000u, 1 | 2, IntPtr.Zero, 3, 0x200000 | 0x2000000, IntPtr.Zero);
        try { _ = WindowsDirectory(result); } catch { result.Dispose(); throw; }
        return result;
    }
    private readonly record struct DirectoryIdentity(ulong Device, ulong Inode);
    [StructLayout(LayoutKind.Sequential)] private struct DeleteDisposition { public byte Delete; }
    private static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());
    [StructLayout(LayoutKind.Explicit, Size = 256)] private struct LinuxStatus
    { [FieldOffset(0)] public uint Mask; [FieldOffset(16)] public uint Links; [FieldOffset(20)] public uint User; [FieldOffset(28)] public ushort Mode; [FieldOffset(32)] public ulong Inode; [FieldOffset(40)] public ulong Size; [FieldOffset(136)] public uint DeviceMajor; [FieldOffset(140)] public uint DeviceMinor; }
    [StructLayout(LayoutKind.Explicit, Size = 52)] private struct WindowsStatus
    { [FieldOffset(0)] public uint Attributes; [FieldOffset(28)] public uint Volume; [FieldOffset(32)] public uint SizeHigh; [FieldOffset(36)] public uint SizeLow; [FieldOffset(40)] public uint Links; [FieldOffset(44)] public uint IndexHigh; [FieldOffset(48)] public uint IndexLow; }
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int Open(string path, int flags, uint mode);
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int OpenAt(int directory, string name, int flags, uint mode);
    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int MkdirAt(int directory, string name, uint mode);
    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int UnlinkAt(int directory, string name, int flags);
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int Statx(int directory, string name, int flags, uint mask, out LinuxStatus status);
    [LibraryImport("libc", EntryPoint = "geteuid")] private static partial uint GetEuid();
    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CreateDirectory(string path, IntPtr security);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] private static partial SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetFileInformation(SafeFileHandle file, out WindowsStatus status);
    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetFileInformation(SafeFileHandle file, int kind, ref DeleteDisposition disposition, uint size);
}
