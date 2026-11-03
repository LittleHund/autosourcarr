using System.Globalization;
using System.Runtime.InteropServices;

namespace AutoSourcarr.Core.Jobs;

/// <summary>How a limit was applied: "cgroup" (cgroup v2 cpu.weight/memory.max), "nice" (nice + RLIMIT_AS) or "none".</summary>
public sealed record AppliedLimits(string Mode, string Detail);

/// <summary>What <see cref="ProcessResourceLimits"/> needs from the system (replaced in tests).</summary>
public interface IProcessLimitHost
{
    bool Linux { get; }
    /// <summary>The app's own cgroup v2 directory (from /proc/self/cgroup "0::/path" under /sys/fs/cgroup, the mount root for "/"), or null.</summary>
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
/// cgroup mount): groups inside the app's OWN cgroup subtree, per instance: <c>autosourcarr-&lt;instance&gt;-app</c> holds the
/// app's own processes (cgroup v2 allows controllers for children only when the parent itself holds no processes),
/// <c>autosourcarr-&lt;instance&gt;-&lt;type&gt;</c> gets <c>cpu.weight</c> and <c>memory.max</c> and the process.</item>
/// <item>otherwise: <c>setpriority</c> (a higher nice value for lower-priority work; raising nice needs no capability) and,
/// when the type asks for it, <c>prlimit(RLIMIT_AS)</c> (lowering a limit of an own child needs no capability).</item>
/// </list>
/// Nothing fails when neither works: the result says "none" and the job still runs with the queue's limits.
/// </summary>
public sealed class ProcessResourceLimits(IProcessLimitHost? host = null, string? instance = null)
{
    /// <summary>Set once at startup (a short form of the instance id), so two instances never share a group.</summary>
    public static string InstanceName { get; set; } = "default";
    private readonly IProcessLimitHost _host = host ?? LinuxProcessLimitHost.Instance;
    private readonly string _instance = Clean(instance ?? InstanceName);

    public AppliedLimits Apply(int processId, string typeName, ProcessLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (!_host.Linux || processId <= 1) return new("none", "process limits need Linux");
        if (Cgroup(processId, Clean(typeName), limits) is { } applied) return applied;
        var parts = new List<string>();
        if (limits.Nice > 0 && _host.SetNice(processId, Math.Clamp(limits.Nice, 1, 19))) parts.Add("nice " + limits.Nice.ToString(CultureInfo.InvariantCulture));
        if (limits.AddressSpaceBytes is { } bytes && bytes > 0 && _host.LimitAddressSpace(processId, bytes))
            parts.Add("address space " + (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB");
        return parts.Count == 0 ? new("none", "the cgroup is not writable and no fallback limit applied")
            : new("nice", string.Join(", ", parts) + " (the cgroup is not writable)");
    }

    private static string Clean(string value)
    {
        var text = new string(value.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? char.ToLowerInvariant(c) : '-').ToArray());
        return text.Length == 0 ? "default" : text.Length > 40 ? text[..40] : text;
    }

    private AppliedLimits? Cgroup(int processId, string type, ProcessLimits limits)
    {
        if (_host.OwnCgroup() is not { } own) return null;
        own = own.TrimEnd('/');
        var leafName = "autosourcarr-" + _instance + "-app";
        // Once the app moved itself into its leaf, the subtree root is the leaf's parent.
        if (Path.GetFileName(own) == leafName && Path.GetDirectoryName(own) is { } parent) own = parent.Replace('\\', '/');
        var controllers = (_host.Read(own + "/cgroup.controllers") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!controllers.Contains("cpu") || !controllers.Contains("memory")) return null;
        if (!_host.TryWrite(own + "/cgroup.subtree_control", "+cpu +memory"))
        {
            // The app's cgroup still holds processes (cgroup v2 refuses controllers then): move them into the app leaf.
            var leaf = own + "/" + leafName;
            if (!_host.Exists(leaf) && !_host.TryCreateDirectory(leaf)) return null;
            foreach (var line in (_host.Read(own + "/cgroup.procs") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _ = _host.TryWrite(leaf + "/cgroup.procs", line);
            if (!_host.TryWrite(own + "/cgroup.subtree_control", "+cpu +memory")) return null;
        }
        var group = own + "/autosourcarr-" + _instance + "-" + type;
        if (!_host.Exists(group) && !_host.TryCreateDirectory(group)) return null;
        var weight = Math.Clamp(limits.CpuWeight, 1, 10000).ToString(CultureInfo.InvariantCulture);
        if (!_host.TryWrite(group + "/cpu.weight", weight)) return null;
        var memory = limits.MemoryBytes is { } bytes && bytes > 0 ? bytes.ToString(CultureInfo.InvariantCulture) : "max";
        if (!_host.TryWrite(group + "/memory.max", memory)) return null;
        if (!_host.TryWrite(group + "/cgroup.procs", processId.ToString(CultureInfo.InvariantCulture))) return null;
        return new("cgroup", group + " cpu.weight " + weight + ", memory.max " + memory);
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
                    // In the container's own cgroup namespace the app usually sits at "/" (the mount root).
                    return relative.Length == 0 ? "/sys/fs/cgroup" : "/sys/fs/cgroup/" + relative;
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
