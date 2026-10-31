using System.Runtime.InteropServices;
using AutoSourcarr.Core.Repair.Runner.Isolation;

namespace AutoSourcarr.Core.Repair.Runner.Execution;

internal sealed class PreparedHardwareJob(VmLaunchPlan plan, GuestAssets assets, VmBudget budget, uint userId, IDisposable scratch) : IDisposable
{
    public VmLaunchPlan Plan { get; } = plan;
    public GuestAssets Assets { get; } = assets;
    public VmBudget Budget { get; } = budget;
    public uint UserId { get; } = userId;
    public void Dispose() => scratch.Dispose();
}
internal interface IHardwareEnvironment { Task<PreparedHardwareJob> PrepareAsync(string jobId, VmBudget budget, CancellationToken cancellationToken); }

/// <summary>Production preparation has no configurable executable, guest image or platform override.</summary>
internal sealed partial class LinuxHardwareEnvironment(string instanceDataRoot) : IHardwareEnvironment
{
    private const string ImageRoot = "/opt/autosourcarr-repair-guest";
    private const uint DirectoryMode = 0x4000, FileModeBits = 0x8000;
    public async Task<PreparedHardwareJob> PrepareAsync(string jobId, VmBudget budget, CancellationToken cancellationToken)
    {
        // Cancellation never abandons a hashing/filesystem thread. The runtime retains its
        // admission lease until this physical operation and any returned handle are joined.
        var prepared = await Task.Run(() => Prepare(jobId, budget, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) { prepared.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
        return prepared;
    }
    private PreparedHardwareJob Prepare(string jobId, VmBudget budget, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Hardware repair requires Linux x64");
        budget.Validate(); ValidateJob(jobId); token.ThrowIfCancellationRequested();
        var capabilities = new NativeIsolationProbe().Inspect();
        if (!capabilities.Supported || capabilities.LandlockAbi < 6 || capabilities.KvmApi != 12 || !capabilities.NoNewPrivileges || !capabilities.Unprivileged || !capabilities.EmptyCapabilities)
            throw new PlatformNotSupportedException("Hardware repair is unavailable: " + capabilities.Reason);
        var uid = EffectiveUid(); if (uid == 0) throw new InvalidDataException("Hardware repair cannot run as root");
        var root = Path.GetFullPath(instanceDataRoot);
        CheckAncestors(root); Check(root, DirectoryMode, owner: uid, privateAccess: false);
        CheckAncestors(ImageRoot); Check(ImageRoot, DirectoryMode, owner: 0, privateAccess: false);
        foreach (var (name, maximum) in new[] { ("guest-assets.json", 4096L), ("kernel", 256L * 1024 * 1024), ("rootfs.raw", 16L * 1024 * 1024 * 1024) })
        {
            var path = Path.Combine(ImageRoot, name); var info = Check(path, FileModeBits, owner: 0, privateAccess: false);
            if (info.Size <= 0 || info.Size > maximum) throw new InvalidDataException("Image-owned repair assets exceed their bounds");
        }
        VerifyExecutable("/opt/autosourcarr-native/repair-launcher"); VerifyExecutable("/usr/bin/qemu-system-x86_64");
        var assets = GuestAssets.Load(ImageRoot); token.ThrowIfCancellationRequested();
        var maintenance = Path.Combine(root, "source-maintenance"); EnsurePrivateDirectory(maintenance, uid);
        var jobs = Path.Combine(maintenance, "vm-jobs"); EnsurePrivateDirectory(jobs, uid);
        var job = Path.Combine(jobs, jobId);
        if (MakeDirectory(job, 0x1c0) != 0) throw new IOException("The private repair job already exists or cannot be created");
        Check(job, DirectoryMode, uid, privateAccess: true); token.ThrowIfCancellationRequested();
        FileStream? scratch = null;
        try
        {
            scratch = new FileStream(Path.Combine(job, "scratch.raw"), new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            scratch.SetLength(budget.ScratchBytes); scratch.Flush(flushToDisk: true);
            var info = Check(Path.Combine(job, "scratch.raw"), FileModeBits, uid, privateAccess: true);
            if (info.Size != budget.ScratchBytes) throw new InvalidDataException("The private repair scratch size changed");
            token.ThrowIfCancellationRequested(); var plan = VmLaunchPlan.Create(job, assets, budget);
            return new(plan, assets, budget, uid, scratch);
        }
        catch { scratch?.Dispose(); throw; }
    }
    private static void EnsurePrivateDirectory(string path, uint uid)
    {
        if (MakeDirectory(path, 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17) throw new IOException("Private repair storage could not be created");
        Check(path, DirectoryMode, uid, privateAccess: true);
    }
    private static void VerifyExecutable(string path)
    {
        CheckAncestors(path); var info = Check(path, FileModeBits, owner: 0, privateAccess: false);
        if ((info.Mode & 0x49) == 0 || info.Size < 64 || info.Size > 512L * 1024 * 1024) throw new InvalidDataException("The fixed repair executable is unavailable");
        using var file = File.OpenRead(path); var header = new byte[20]; file.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46 }) || header[4] != 2 || header[5] != 1 || header[18] != 62 || header[19] != 0)
            throw new InvalidDataException("The fixed repair executable must be Linux x64 ELF");
    }
    private static void CheckAncestors(string path)
    {
        var current = Path.GetDirectoryName(Path.GetFullPath(path));
        while (current is not null)
        {
            var info = Stat(current);
            if ((info.Mode & 0xf000) != DirectoryMode || (info.Mode & 0x12) != 0 || info.User != 0 && info.User != EffectiveUid()) throw new InvalidDataException("Repair paths cannot traverse links, foreign owners or writable shared ancestors");
            current = Path.GetDirectoryName(current);
        }
    }
    private static LinuxStat Check(string path, uint type, uint owner, bool privateAccess)
    {
        var info = Stat(path);
        if ((info.Mode & 0xf000) != type || info.User != owner || (info.Mode & (privateAccess ? 0x3fu : 0x12u)) != 0 || type == FileModeBits && info.Links != 1)
            throw new InvalidDataException("Repair paths require their exact owner, type and private permissions");
        return info;
    }
    private static LinuxStat Stat(string path) => LStat(path, out var info) == 0 ? info : throw new IOException("A required hardware repair path is unavailable");
    internal static void ValidateJob(string value)
    { if (value.Length != 32 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new ArgumentException("Invalid private repair job identity", nameof(value)); }
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong Device, Inode, Links;
        public uint Mode, User, Group;
        public int Padding;
        public ulong DeviceType;
        public long Size, BlockSize, Blocks, AccessSeconds, AccessNanos, ModifySeconds, ModifyNanos, ChangeSeconds, ChangeNanos;
        public long Reserved1, Reserved2, Reserved3;
    }
    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LStat(string path, out LinuxStat info);
    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MakeDirectory(string path, uint mode);
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint EffectiveUid();
}
