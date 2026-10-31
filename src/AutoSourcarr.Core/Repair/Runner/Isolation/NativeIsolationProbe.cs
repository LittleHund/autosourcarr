using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoSourcarr.Core.Repair.Runner.Isolation;

public sealed record IsolationCapabilities(
    bool Supported,
    string Reason,
    int LandlockAbi = 0,
    int KvmApi = 0,
    bool NoNewPrivileges = false,
    bool Unprivileged = false,
    bool EmptyCapabilities = false);

public interface IIsolationProbe
{
    IsolationCapabilities Inspect();
}

/// <summary>Capability checks are necessary; a full guest/launcher probe is still required.</summary>
public sealed partial class NativeIsolationProbe : IIsolationProbe
{
    public IsolationCapabilities Inspect()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return new(false, "hardware-repair-requires-linux-x64");
        var status = File.ReadAllLines("/proc/self/status")
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.Ordinal);
        var nnp = status.GetValueOrDefault("NoNewPrivs") == "1";
        var caps = new[] { "CapEff", "CapPrm", "CapInh", "CapAmb" }
            .All(key => status.TryGetValue(key, out var value) && value.Length > 0 && value.All(c => c == '0'));
        var unprivileged = GetEffectiveUid() != 0;
        if (!nnp || !caps || !unprivileged)
            return new(false, "application-must-be-unprivileged-before-repair", NoNewPrivileges: nnp,
                Unprivileged: unprivileged, EmptyCapabilities: caps);
        var abi = (int)LandlockVersion(444, IntPtr.Zero, 0, 1);
        if (abi < 6)
            return new(false, "landlock-abi6-unavailable", Math.Max(0, abi), NoNewPrivileges: nnp,
                Unprivileged: unprivileged, EmptyCapabilities: caps);
        try
        {
            using var kvm = File.OpenHandle("/dev/kvm", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var api = Ioctl(kvm, 0xAE00, 0);
            if (api != 12)
                return new(false, "kvm-api-unavailable", abi, api, nnp, unprivileged, caps);
            var machine = Ioctl(kvm, 0xAE01, 0);
            if (machine < 0)
                return new(false, "kvm-machine-creation-denied", abi, api, nnp, unprivileged, caps);
            using var created = new SafeFileHandle((IntPtr)machine, ownsHandle: true);
            return new(true, "capabilities-available-full-probe-required", abi, api, nnp, unprivileged, caps);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(false, "kvm-device-unavailable", abi, NoNewPrivileges: nnp,
                Unprivileged: unprivileged, EmptyCapabilities: caps);
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUid();

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long LandlockVersion(long number, IntPtr attributes, nuint size, uint flags);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(SafeFileHandle handle, nuint request, nuint argument);
}
