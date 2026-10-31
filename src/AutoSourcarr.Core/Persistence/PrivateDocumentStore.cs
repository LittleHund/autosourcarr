using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Settings;
using Microsoft.Win32.SafeHandles;

namespace AutoSourcarr.Core.Persistence;

/// <summary>Atomic private documents with local and cross-process lock ownership.</summary>
public sealed partial class PrivateDocumentStore : IPrivateDocumentStore
{
    private static readonly object[] Stripes = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    private readonly string _root;
    public PrivateDocumentStore(string root) { _root = Path.GetFullPath(root); EnsureDirectory(); }
    public IReadOnlyList<string> JsonDocuments(int maximum)
    {
        if (maximum is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximum));
        EnsureDirectory();
        return Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Order(StringComparer.Ordinal).Take(maximum).Select(value => value!).ToArray();
    }
    public T Locked<T>(string name, Func<IPrivateDocument, T> operation, TimeSpan? timeout = null)
    {
        DatabaseSession.CheckSynchronousResult<T>();
        var path = FixedPath(name); EnsureDirectory(); var budget = timeout ?? TimeSpan.FromSeconds(5);
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var key = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        var stripe = Stripes[(int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)Stripes.Length)];
        var elapsed = Stopwatch.StartNew();
        if (!Monitor.TryEnter(stripe, budget)) throw new IOException("Source history is busy; refresh before retrying");
        try
        {
            var lockPath = Path.ChangeExtension(path, ".lock");
            using var locked = OpenRegular(lockPath, writable: true, create: true, maximum: 64);
            if (locked.Length == 0) { locked.WriteByte((byte)'0'); locked.Flush(flushToDisk: true); }
            var acquired = false;
            try
            {
                while (!acquired)
                {
                    if (OperatingSystem.IsLinux()) acquired = Flock(checked((int)locked.SafeFileHandle.DangerousGetHandle()), 2 | 4) == 0;
                    else if (OperatingSystem.IsWindows())
                    {
                        try { locked.Lock(0, 1); acquired = true; } catch (IOException) { }
                    }
                    else throw new PlatformNotSupportedException("Private source documents need Linux or Windows file locks");
                    if (!acquired)
                    {
                        if (elapsed.Elapsed >= budget) throw new IOException("Source history is busy; refresh before retrying");
                        Thread.Sleep(20);
                    }
                }
                var document = new Document(this, path);
                try { return operation(document); }
                finally { document.Close(); }
            }
            finally
            {
                if (acquired)
                {
                    if (OperatingSystem.IsLinux()) { if (Flock(checked((int)locked.SafeFileHandle.DangerousGetHandle()), 8) != 0) throw new IOException("Cannot release source history lock"); }
                    else if (OperatingSystem.IsWindows()) locked.Unlock(0, 1);
                }
            }
        }
        finally { Monitor.Exit(stripe); }
    }

    private byte[]? Read(string path, int maximum)
    {
        if (maximum is < 1 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximum));
        return AtomicSourceFiles.Read(path, maximum);
    }
    private void Write(string path, byte[] bytes)
    {
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Private source document exceeds its byte bound");
        AtomicSourceFiles.Write(path, bytes, maximum: 16 * 1024 * 1024);
    }
    private string FixedPath(string name)
    {
        if (name is "." or ".." || !Regex.IsMatch(name, "\\A[A-Za-z0-9_.-]{1,128}\\z", RegexOptions.CultureInvariant)) throw new ArgumentException("A plain private document name is required", nameof(name));
        return Path.Combine(_root, name);
    }
    private void EnsureDirectory() => ValidateDirectory(_root);
    internal static void ValidateDirectory(string root, bool publicCode = false)
    {
        var path = new DirectoryInfo(Path.GetFullPath(root)); var parents = new Stack<DirectoryInfo>();
        while (path is not null) { parents.Push(path); path = path.Parent; }
        while (parents.TryPop(out var directory))
        {
            if (directory.LinkTarget is not null || File.Exists(directory.FullName) || directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Source storage contains a linked or non-directory path");
            if (!directory.Exists)
            {
                Directory.CreateDirectory(directory.FullName);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory.FullName, publicCode ? (UnixFileMode)0x1ed : (UnixFileMode)0x1c0);
                directory.Refresh();
                if (directory.LinkTarget is not null || (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Source storage directory changed");
            }
        }
    }
    internal static FileStream OpenRegular(string path, bool writable, bool create, int maximum)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsLinux())
        {
            var descriptor = Open(path, (writable ? 2 : 0) | (create ? 0x40 : 0) | 0x20000 | 0x800 | 0x80000, 0x180);
            if (descriptor < 0)
            {
                if (Marshal.GetLastPInvokeError() == 2) throw new FileNotFoundException("Private source document is absent");
                throw new IOException("Cannot open a regular private source document");
            }
            handle = new((IntPtr)descriptor, ownsHandle: true);
            try
            {
                if (Statx(descriptor, "", 0x1000, 0x205, out var status) != 0 || (status.Mask & 5) != 5 || (status.Mode & 0xF000) != 0x8000 || status.Links != 1 || status.Size > (ulong)maximum)
                    throw new InvalidDataException("Private source document is linked, special or oversized");
            }
            catch { handle.Dispose(); throw; }
        }
        else if (OperatingSystem.IsWindows())
        {
            handle = CreateFile(path, writable ? 0xC0000000u : 0x80000000u, 1 | 2 | 4, IntPtr.Zero, create ? 4u : 3u, 0x200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError(); handle.Dispose();
                if (error is 2 or 3) throw new FileNotFoundException("Private source document is absent");
                throw new IOException("Cannot open a regular private source document");
            }
            try
            {
                if (!GetFileInformation(handle, out var status) || status.Links != 1 || (status.Attributes & (0x10u | 0x400u)) != 0 || ((ulong)status.SizeHigh << 32 | status.SizeLow) > (ulong)maximum)
                    throw new InvalidDataException("Private source document is linked, special or oversized");
            }
            catch { handle.Dispose(); throw; }
        }
        else throw new PlatformNotSupportedException("Private source documents need native regular-file protection");
        try { return new(handle, writable ? FileAccess.ReadWrite : FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }
    internal static bool TryExclusiveLock(FileStream stream)
    {
        if (OperatingSystem.IsLinux())
        {
            if (Flock(checked((int)stream.SafeFileHandle.DangerousGetHandle()), 2 | 4) == 0) return true;
            if (Marshal.GetLastPInvokeError() == 11) return false;
            throw new IOException("Cannot acquire source storage lock");
        }
        if (OperatingSystem.IsWindows()) { try { stream.Lock(0, 1); return true; } catch (IOException) { return false; } }
        throw new PlatformNotSupportedException("Source storage locks require Linux or Windows");
    }
    internal static void ReleaseExclusiveLock(FileStream stream)
    {
        if (OperatingSystem.IsLinux()) { if (Flock(checked((int)stream.SafeFileHandle.DangerousGetHandle()), 8) != 0) throw new IOException("Cannot release source storage lock"); }
        else if (OperatingSystem.IsWindows()) stream.Unlock(0, 1);
        else throw new PlatformNotSupportedException("Source storage locks require Linux or Windows");
    }
    private sealed class Document(PrivateDocumentStore owner, string path) : IPrivateDocument
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private bool _closed;
        public byte[]? Read(int maximumBytes) { Check(); return owner.Read(path, maximumBytes); }
        public void Write(byte[] bytes) { Check(); owner.Write(path, bytes); }
        public void Close() => _closed = true;
        private void Check()
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("A private document cannot escape its synchronous lock owner");
        }
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(40)] public ulong Size;
    }
    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct WindowsStatus
    {
        [FieldOffset(0)] public uint Attributes;
        [FieldOffset(32)] public uint SizeHigh;
        [FieldOffset(36)] public uint SizeLow;
        [FieldOffset(40)] public uint Links;
    }
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags, uint mode);
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int directory, string name, int flags, uint mask, out LinuxStatus status);
    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(int descriptor, int operation);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformation(SafeFileHandle handle, out WindowsStatus status);
}
