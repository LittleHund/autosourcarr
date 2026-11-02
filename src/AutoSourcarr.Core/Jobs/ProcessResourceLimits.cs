using System.Globalization;
using System.Runtime.InteropServices;

namespace AutoSourcarr.Core.Jobs;

/// <summary>How a limit was applied: "cgroup" (cgroup v2 cpu.weight/memory.max), "nice" (nice + RLIMIT_AS) or "none".</summary>
public sealed record AppliedLimits(string Mode, string Detail);

/// <summary>What <see cref="ProcessResourceLimits"/> needs from the system (replaced in tests).</summary>
public interface IProcessLimitHost
{
    bool Linux { get; }
    /// <summary>The app's own cgroup v2 directory (from /proc/self/cgroup "0::/path" under /sys/fs/cgroup), or null.</summary>
    string? OwnCgroup();
    bool TryWrite(string path, string value);
    bool TryCreateDirectory(string path);
    bool Exists(string path);
    string? Read(string path);
    bool SetNice(int processId, int nice);
    bool LimitAddressSpace(int processId, long bytes);
}

/// <summary>
/// PERF-2 CPU/memory limits per job type for the processes jobs start (the repair runner, the source host). In-process
/// work (C# jobs) shares the app's process and is bounded by the queue's workers, run limits and minimum shares.
/// <list type="bullet">
/// <item>cgroup v2, when the container's cgroup is delegated and writable (needs no host permission, only a writable
/// cgroup mount): a sibling group <c>autosourcarr-&lt;type&gt;</c> next to the app's own leaf with <c>cpu.weight</c> and
/// <c>memory.max</c>; the process is moved there.</item>
/// <item>otherwise: <c>setpriority</c> (a higher nice value for lower-priority work; raising nice needs no capability) and
/// <c>prlimit(RLIMIT_AS)</c> for memory (lowering a limit of an own child needs no capability).</item>
/// </list>
/// Nothing fails when neither works: the result says "none" and the job still runs with the queue's limits.
/// </summary>
public sealed class ProcessResourceLimits(IProcessLimitHost? host = null)
{
    private readonly IProcessLimitHost _host = host ?? LinuxProcessLimitHost.Instance;

    public AppliedLimits Apply(int processId, string typeName, ProcessLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (!_host.Linux || processId <= 1) return new("none", "process limits need Linux");
        var name = new string(typeName.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
        if (Cgroup(processId, name, limits) is { } applied) return applied;
        var parts = new List<string>();
        if (limits.Nice > 0 && _host.SetNice(processId, Math.Clamp(limits.Nice, 1, 19))) parts.Add("nice " + limits.Nice.ToString(CultureInfo.InvariantCulture));
        if (limits.AddressSpaceBytes is { } bytes && bytes > 0 && _host.LimitAddressSpace(processId, bytes))
            parts.Add("address space " + (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB");
        return parts.Count == 0 ? new("none", "the cgroup is not writable and no fallback limit applied")
            : new("nice", string.Join(", ", parts) + " (the cgroup is not writable)");
    }

    private AppliedLimits? Cgroup(int processId, string name, ProcessLimits limits)
    {
        if (_host.OwnCgroup() is not { } own) return null;
        var parent = Path.GetDirectoryName(own.TrimEnd('/'));
        if (parent is null || !_host.Exists(Path.Combine(parent, "cgroup.subtree_control"))) return null;
        var controllers = _host.Read(Path.Combine(parent, "cgroup.controllers")) ?? "";
        if (!controllers.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("cpu") || !controllers.Contains("memory", StringComparison.Ordinal)) return null;
        // The parent's subtree must hand cpu and memory to its children (the app's own leaf is one of them already).
        if (!_host.TryWrite(Path.Combine(parent, "cgroup.subtree_control"), "+cpu +memory")) return null;
        var group = Path.Combine(parent, "autosourcarr-" + name);
        if (!_host.Exists(group) && !_host.TryCreateDirectory(group)) return null;
        if (!_host.TryWrite(Path.Combine(group, "cpu.weight"), Math.Clamp(limits.CpuWeight, 1, 10000).ToString(CultureInfo.InvariantCulture))) return null;
        var memory = limits.MemoryBytes is { } bytes && bytes > 0 ? bytes.ToString(CultureInfo.InvariantCulture) : "max";
        if (!_host.TryWrite(Path.Combine(group, "memory.max"), memory)) return null;
        if (!_host.TryWrite(Path.Combine(group, "cgroup.procs"), processId.ToString(CultureInfo.InvariantCulture))) return null;
        return new("cgroup", group + " cpu.weight " + Math.Clamp(limits.CpuWeight, 1, 10000).ToString(CultureInfo.InvariantCulture) + ", memory.max " + memory);
    }
}

public sealed partial class LinuxProcessLimitHost : IProcessLimitHost
{
    public static LinuxProcessLimitHost Instance { get; } = new();
    public bool Linux => OperatingSystem.IsLinux();

    public string? OwnCgroup()
    {
        try
        {
            foreach (var line in File.ReadAllLines("/proc/self/cgroup"))
                if (line.StartsWith("0::", StringComparison.Ordinal))
                {
                    var relative = line[3..].Trim().TrimStart('/');
                    // In the container's own cgroup namespace the app usually sits at "/": no sibling can be made safely.
                    if (relative.Length == 0) return null;
                    return Path.Combine("/sys/fs/cgroup", relative);
                }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _ = error; }
        return null;
    }

    public bool TryWrite(string path, string value)
    {
        try { File.WriteAllText(path, value); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public bool TryCreateDirectory(string path)
    {
        try { Directory.CreateDirectory(path); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public string? Read(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public bool SetNice(int processId, int nice) => Linux && SetPriority(0 /* PRIO_PROCESS */, (uint)processId, nice) == 0;

    public bool LimitAddressSpace(int processId, long bytes)
    {
        if (!Linux) return false;
        var limit = new RLimit { Current = (ulong)bytes, Maximum = (ulong)bytes };
        return PrLimit(processId, 9 /* RLIMIT_AS */, ref limit, IntPtr.Zero) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit { public ulong Current; public ulong Maximum; }

    [LibraryImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static partial int SetPriority(int which, uint who, int priority);
    [LibraryImport("libc", EntryPoint = "prlimit", SetLastError = true)]
    private static partial int PrLimit(int processId, int resource, ref RLimit limit, IntPtr old);
}
