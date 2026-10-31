using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoSourcarr.Core.Settings;

/// <summary>Fixed private configuration files with bounded reads and atomic publication.</summary>
public sealed partial class DurableConfigurationFiles
{
    public const int FileLimit = 2 * 1024 * 1024;
    public const string State = "setup-state.json";
    public const string Journal = ".setup-transaction.json";
    public static IReadOnlyList<string> ManagedFiles { get; } = ["autosourcarr.env", "source-host.env", State];
    private readonly string _root;

    public DurableConfigurationFiles(string root) => _root = Path.GetFullPath(root);
    public string Root => _root;

    public byte[]? Read(string name)
    {
        var path = FixedPath(name);
        var limit = name == Journal ? 8 * FileLimit : FileLimit;
        if (OperatingSystem.IsLinux())
        {
            const int noFollowNonBlockRead = 0x20000 | 0x800 | 0x80000;
            var descriptor = Open(path, noFollowNonBlockRead, 0);
            if (descriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 2)
                    return null;
                throw new IOException("Cannot open a regular configuration file; links and unsupported files are refused (errno " + error + ").");
            }
            using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            if (Statx(descriptor, "", 0x1000, 0x201, out var status) != 0
                || (status.Mode & 0xF000) != 0x8000 || status.Size > (ulong)limit)
                throw new InvalidDataException("Configuration is not a bounded regular file.");
            using var stream = new FileStream(handle, FileAccess.Read);
            return ReadBounded(stream, limit);
        }
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || Directory.Exists(path))
            throw new InvalidDataException("Configuration links and directories are refused.");
        if (!info.Exists)
            return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return ReadBounded(input, limit);
    }

    public void Write(string name, byte[]? data, bool preserveSourceHostMode = false)
    {
        if (preserveSourceHostMode && name != "source-host.env")
            throw new ArgumentException("Mode preservation is only supported for an existing source-host.env.", nameof(name));
        var path = FixedPath(name);
        var current = Read(name);
        if (preserveSourceHostMode && current is null)
            throw new FileNotFoundException("Private source host settings file is not mounted");
        var mode = !OperatingSystem.IsWindows() && preserveSourceHostMode
            ? File.GetUnixFileMode(path) & (UnixFileMode)0x1ff
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (data is not null && data.Length > (name == Journal ? 8 * FileLimit : FileLimit))
            throw new InvalidDataException("Configuration exceeds its supported size.");
        Directory.CreateDirectory(_root);
        if (data is null)
        {
            File.Delete(path);
            SyncDirectory(_root);
            return;
        }
        var temporary = Path.Combine(_root, ".setup-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, mode);
                output.Write(data);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            SyncDirectory(_root);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static void SyncDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var descriptor = Open(directory, 0x10000 | 0x80000, 0);
        if (descriptor < 0)
            throw new IOException("Cannot open the configuration directory for durable publication.");
        using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        if (Fsync(descriptor) != 0)
            throw new IOException("Cannot durably synchronize the configuration directory.");
    }

    private string FixedPath(string name)
    {
        if (name != Journal && !ManagedFiles.Contains(name, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported configuration target.", nameof(name));
        return Path.Combine(_root, name);
    }

    private static byte[] ReadBounded(Stream stream, int limit)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)output.Length));
            if (read == 0)
                return output.ToArray();
            output.Write(buffer, 0, read);
            if (output.Length > limit)
                throw new InvalidDataException("Configuration exceeds its supported size.");
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct FileStatus
    {
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(40)] public ulong Size;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags, uint mode);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int descriptor);
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int directory, string path, int flags, uint mask, out FileStatus status);
}
