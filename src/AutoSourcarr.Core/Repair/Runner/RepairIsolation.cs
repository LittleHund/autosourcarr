using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair.Runner.Egress;

namespace AutoSourcarr.Core.Repair.Runner;

/// <summary>
/// Whether repair code can run isolated here. <see cref="State"/> is "available" (runner present, Linux x64, not root,
/// Landlock ABI ≥ 6) or "unavailable" with a <see cref="Reason"/> code and a readable <see cref="Message"/>.
/// </summary>
public sealed record RepairIsolationState(bool Available, string Reason, string Message, string RunnerPath, int LandlockAbi)
{
    public string State => Available ? "available" : "unavailable";

    public JsonObject Json() => new()
    {
        ["available"] = Available, ["state"] = State, ["reason"] = Reason, ["message"] = Message,
        ["runner"] = RunnerPath, ["landlock_abi"] = LandlockAbi,
    };
}

/// <summary>A repair step that would run code was refused because no host isolation is available (hard rule 13: fail closed).</summary>
public sealed class RepairIsolationUnavailableException(RepairIsolationState state)
    : InvalidOperationException("Isolation unavailable: " + state.Message)
{
    public RepairIsolationState Isolation { get; } = state;
}

/// <summary>What the isolation check reads from the system (replaced in tests).</summary>
public interface IRepairIsolationHost
{
    bool LinuxX64 { get; }
    bool Root { get; }
    bool Executable(string path);
    /// <summary>The kernel's Landlock ABI (0 when Landlock is missing or disabled).</summary>
    int LandlockAbi();
}

public sealed partial class LinuxRepairIsolationHost : IRepairIsolationHost
{
    public static LinuxRepairIsolationHost Instance { get; } = new();
    public bool LinuxX64 => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    public bool Root => LinuxX64 && GetEffectiveUid() == 0;
    public bool Executable(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            return !OperatingSystem.IsLinux() || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    public int LandlockAbi()
    {
        if (!LinuxX64) return 0;
        var abi = Syscall(444 /* landlock_create_ruleset */, IntPtr.Zero, 0, 1 /* LANDLOCK_CREATE_RULESET_VERSION */);
        return abi < 0 ? 0 : (int)Math.Min(abi, int.MaxValue);
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUid();
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long Syscall(long number, IntPtr attributes, nuint size, uint flags);
}

/// <summary>
/// Phase 13 (REP-5, hard rule 13): the one way repair code runs. Every code-running repair step goes through
/// <see cref="RunAsync"/>, which runs the program under <c>native/repair-runner</c> (Landlock ABI ≥ 6, connect gate to the
/// per-run <see cref="EgressProxy"/>, signal gate, time/memory/task limits) and refuses — "isolation unavailable" — when
/// the runner binary is missing, the kernel lacks Landlock ABI 6, the app runs as root, or the runner did not prove its
/// restrictions. There is no unisolated fallback. Domain-move repairs rewrite class-file constants and run no code here
/// (their content check runs in the source host), so they do not need it.
/// </summary>
public sealed class RepairIsolation
{
    private readonly string _dataRoot;
    private readonly IRepairIsolationHost _host;
    private readonly Func<string> _runnerPath;
    private readonly Func<IsolatedRunRequest, CancellationToken, Task<IsolatedRunResult>> _run;
    private readonly Action<string>? _log;

    public RepairIsolation(string instanceDataRoot, Action<string>? log = null, IRepairIsolationHost? host = null, Func<string>? runnerPath = null,
        Func<IsolatedRunRequest, CancellationToken, Task<IsolatedRunResult>>? run = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceDataRoot);
        _dataRoot = instanceDataRoot; _log = log; _host = host ?? LinuxRepairIsolationHost.Instance;
        _runnerPath = runnerPath ?? (() => IsolatedRunner.RunnerPath);
        _run = run ?? ((request, token) => new IsolatedRunner(_dataRoot, _log).RunAsync(request, token));
    }

    /// <summary>A cheap check (no process): what the status page and the repair control show.</summary>
    public RepairIsolationState Probe()
    {
        var path = _runnerPath();
        if (!_host.LinuxX64) return new(false, "platform", "Isolated repairs need Linux x86-64; this host cannot run repair code.", path, 0);
        if (!_host.Executable(path)) return new(false, "runner-missing", "The isolated repair runner is not installed (" + path + "); repairs that run code are unavailable.", path, 0);
        if (_host.Root) return new(false, "root", "AutoSourcarr runs as root; the repair runner refuses root, so repairs that run code are unavailable.", path, 0);
        var abi = _host.LandlockAbi();
        if (abi < 6)
            return new(false, "landlock-abi", "The kernel offers Landlock ABI " + abi.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "; isolated repairs need ABI 6 (Linux 6.12 or newer with Landlock enabled). Repairs that run code are unavailable.", path, abi);
        return new(true, "ready", "Repair code runs in the Landlock repair runner (ABI " + abi.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ") with the egress proxy as its only network path.", path, abi);
    }

    /// <summary>Runs one program isolated, or throws <see cref="RepairIsolationUnavailableException"/> (nothing ran unisolated).</summary>
    public async Task<IsolatedRunResult> RunAsync(IsolatedRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = Probe();
        if (!state.Available) throw new RepairIsolationUnavailableException(state);
        var result = await _run(request, cancellationToken).ConfigureAwait(false);
        if (!result.Isolated)
        {
            var reason = result.Error.Split('\n', 2)[0].Trim();
            throw new RepairIsolationUnavailableException(state with
            {
                Available = false, Reason = "runner-refused",
                Message = "The repair runner refused to start the program" + (reason.Length > 0 ? ": " + (reason.Length > 300 ? reason[..300] : reason) : "") + ".",
            });
        }
        return result;
    }

    /// <summary>The runner self-check behind Tasks → Repairs "Check": runs /bin/true through the runner and reports its proof.</summary>
    public async Task<JsonObject> SelfCheckAsync(CancellationToken cancellationToken)
    {
        var state = Probe();
        var answer = state.Json();
        answer["ok"] = false;
        if (!state.Available) return answer;
        var program = new[] { "/usr/bin/true", "/bin/true" }.FirstOrDefault(_host.Executable) ?? "/usr/bin/true";
        try
        {
            var result = await RunAsync(new IsolatedRunRequest(program, [], [], "AutoSourcarr repair runner self-check") { Timeout = TimeSpan.FromSeconds(30) },
                cancellationToken).ConfigureAwait(false);
            TryDelete(result.JobDirectory);
            answer["ok"] = result.ExitCode == 0 && !result.TimedOut;
            answer["exit_code"] = result.ExitCode;
            answer["proof"] = new JsonObject
            {
                ["landlock_abi"] = result.Proof!.LandlockAbi, ["uid"] = result.Proof.UserId, ["scoped"] = result.Proof.Scoped,
            };
            answer["message"] = answer["ok"]!.GetValue<bool>() ? "The isolated repair runner works: " + state.Message : "The runner started but the check program failed.";
        }
        catch (RepairIsolationUnavailableException error) { answer = error.Isolation.Json(); answer["ok"] = false; }
        catch (Exception error) when (error is IOException or InvalidDataException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            answer["available"] = false; answer["state"] = "unavailable"; answer["reason"] = "runner-failed";
            answer["message"] = "The isolated repair runner could not be started: " + error.Message;
        }
        return answer;
    }

    private static void TryDelete(string directory)
    {
        try { if (directory.Length > 0) Directory.Delete(directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _ = error; }
    }
}
