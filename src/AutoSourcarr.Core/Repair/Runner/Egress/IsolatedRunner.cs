using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoSourcarr.Core.Repair.Runner.Execution;

namespace AutoSourcarr.Core.Repair.Runner.Egress;

public sealed record IsolatedRunRequest(string Program, IReadOnlyList<string> Arguments, IReadOnlyCollection<string> AllowedHosts, string UserAgent)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Per stream; later output is drained and discarded.</summary>
    public int MaximumOutputBytes { get; init; } = 1024 * 1024;
    /// <summary>Runs with the empty private job directory before the program starts.</summary>
    public Action<string>? PrepareJob { get; init; }
}

/// <summary>Evidence printed by repair-runner before it releases the program.</summary>
public sealed record RunnerProof(int ProcessId, uint UserId, int ParentProcessId, int LandlockAbi, int ProxyPort, bool Scoped);

/// <param name="ExitCode">The program's exit code, 128+signal, or 78 when isolation was unavailable.</param>
/// <param name="Proof">Null when the runner refused to start (no isolation, no program run).</param>
public sealed record IsolatedRunResult(int ExitCode, string Output, string Error, IReadOnlyList<string> RefusedHosts,
    bool TimedOut, bool OutputTruncated, string JobDirectory, RunnerProof? Proof)
{
    public bool Isolated => Proof is not null;
}

/// <summary>
/// Runs one program under native/repair-runner (Landlock with network rules, seccomp connect gate)
/// with the domain-filtering <see cref="EgressProxy"/> as its only network path. The caller owns the
/// returned job directory (&lt;data&gt;/source-maintenance/runner-jobs/&lt;32 hex&gt;) and deletes it.
/// </summary>
public sealed partial class IsolatedRunner(string instanceDataRoot, Action<string>? log = null)
{
    public const string DefaultRunnerPath = "/opt/autosourcarr-native/repair-runner";
    public const string RunnerPathVariable = "AUTOSOURCARR_REPAIR_RUNNER";
    public const int IsolationUnavailableExitCode = 78;
    private const int MaximumProofBytes = 4096;
    private const string ProofPrefix = "AUTOSOURCARR_RUNNER_V1 ";
    private static readonly HashSet<string> ProofFields = ["pid", "uid", "ppid", "pdeathsig", "landlock", "nnp", "seccomp", "netgate", "proxy", "scoped"];

    public static string RunnerPath =>
        Environment.GetEnvironmentVariable(RunnerPathVariable) is { Length: > 0 } configured ? configured : DefaultRunnerPath;

    public async Task<IsolatedRunResult> RunAsync(IsolatedRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The isolated repair runner requires Linux x64");
        if (!Path.IsPathFullyQualified(request.Program) || request.Arguments.Any(argument => argument.Contains('\0', StringComparison.Ordinal)))
            throw new ArgumentException("The isolated program needs an absolute path and NUL-free arguments.", nameof(request));
        if (request.Timeout <= TimeSpan.Zero || request.MaximumOutputBytes is < 1024 or > 64 * 1024 * 1024)
            throw new ArgumentException("Invalid isolated run bounds.", nameof(request));
        var job = CreateJobDirectory();
        request.PrepareJob?.Invoke(job);
        await using var proxy = await EgressProxy.StartAsync(request.AllowedHosts, request.UserAgent, log).ConfigureAwait(false);

        var start = new ProcessStartInfo(RunnerPath)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = job
        };
        // The runner clears its environment anyway; nothing of ours is handed over even briefly.
        start.Environment.Clear();
        foreach (var argument in new[] { job, proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--", request.Program }.Concat(request.Arguments))
            start.ArgumentList.Add(argument);
        RunnerChild? owned = null;
        // PDEATHSIG follows the creating thread, which DedicatedHardwareProcess keeps alive.
        var process = DedicatedHardwareProcess.Start(() =>
        {
            var child = new Process { StartInfo = start };
            try
            {
                if (!child.Start()) throw new IOException("The isolated repair runner did not start");
                return owned = new RunnerChild(child);
            }
            catch { child.Dispose(); throw; }
        });
        var timedOut = false;
        RunnerProof? proof = null;
        try
        {
            process.Input.Dispose();
            var output = DrainAsync(process.Output, request.MaximumOutputBytes);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(request.Timeout);
            var exit = process.WaitForExitAsync();
            string? firstLine;
            try { firstLine = await ReadFirstLineAsync(process.Error, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { firstLine = null; timedOut = true; }
            if (firstLine is not null && firstLine.StartsWith(ProofPrefix, StringComparison.Ordinal))
            {
                proof = ParseProof(firstLine, process.Id, EffectiveUserId(), Environment.ProcessId, Path.GetFileName(job), proxy.Port)
                    ?? throw new InvalidDataException("The isolated repair runner did not establish the required restrictions.");
            }
            // Without evidence the runner is refusing (exit 78) and never released the program.
            if (proof is null && await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false) != exit)
                KillGroup(process);
            var error = DrainAsync(process.Error, request.MaximumOutputBytes);
            if (!timedOut && proof is not null && await Task.WhenAny(exit, Task.Delay(Timeout.Infinite, deadline.Token)).ConfigureAwait(false) != exit)
                timedOut = true;
            if (timedOut) KillGroup(process);
            await exit.ConfigureAwait(false);
            // Descendants share the runner's process group (setsid/setpgid are denied inside).
            KillGroup(process);
            var outputs = await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var host in proxy.RefusedHosts) log?.Invoke("isolated run refused egress to " + host);
            var errorText = proof is null && firstLine is not null ? firstLine + "\n" + outputs[1].Text : outputs[1].Text;
            return new IsolatedRunResult(owned?.ExitCode ?? -1, outputs[0].Text, errorText, proxy.RefusedHosts, timedOut,
                outputs[0].Truncated || outputs[1].Truncated, job, proof);
        }
        catch
        {
            KillGroup(process);
            try { await process.WaitForExitAsync().ConfigureAwait(false); }
            finally { TryDelete(job); }
            throw;
        }
        finally
        {
            try { await process.WaitForExitAsync().ConfigureAwait(false); } finally { process.Dispose(); }
        }
    }

    private static void TryDelete(string job)
    {
        try { Directory.Delete(job, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private string CreateJobDirectory()
    {
        // The runner insists on a canonical path; resolve the data root's links once, here.
        var root = Canonical(Path.GetFullPath(instanceDataRoot));
        var maintenance = Path.Combine(root, "source-maintenance");
        var jobs = Path.Combine(maintenance, "runner-jobs");
        foreach (var directory in new[] { maintenance, jobs })
            if (MakeDirectory(directory, 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17)
                throw new IOException("Private repair storage could not be created");
        var job = Path.Combine(jobs, RandomNumberGenerator.GetHexString(32, lowercase: true));
        if (MakeDirectory(job, 0x1c0) != 0) throw new IOException("The private repair job already exists or cannot be created");
        return job;
    }

    private static string Canonical(string path)
    {
        var resolved = RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero) throw new IOException("The instance data root is unavailable");
        try { return Marshal.PtrToStringUTF8(resolved) ?? throw new IOException("The instance data root is unavailable"); }
        finally { Free(resolved); }
    }

    private static void KillGroup(IHardwareProcess process)
    {
        int id;
        try { id = process.Id; } catch (InvalidOperationException) { return; }
        // repair-runner makes itself a group leader first; its PGID equals its PID.
        if (id > 1) _ = Kill(-id, 9);
        try { process.Kill(); } catch (InvalidOperationException) { }
    }

    private static async Task<string?> ReadFirstLineAsync(Stream error, CancellationToken token)
    {
        // Byte-wise: later diagnostics stay in the stream for the bounded drain.
        var line = new byte[MaximumProofBytes];
        var one = new byte[1];
        var length = 0;
        while (true)
        {
            if (await error.ReadAsync(one, token).ConfigureAwait(false) == 0) return length == 0 ? null : Encoding.ASCII.GetString(line, 0, length);
            if (one[0] == (byte)'\n') break;
            if (length == line.Length) throw new InvalidDataException("The isolated repair runner's first line is oversized.");
            line[length++] = one[0];
        }
        // Evidence, or the reason a refusing runner (exit 78) printed instead.
        return Encoding.ASCII.GetString(line, 0, length);
    }

    internal static RunnerProof? ParseProof(string line, int expectedProcessId, uint expectedUserId, int expectedParentProcessId, string expectedJob, int expectedPort)
    {
        var prefix = ProofPrefix + expectedJob + " ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || line.Any(c => c is < ' ' or > '~')) return null;
        try
        {
            using var document = JsonDocument.Parse(line[prefix.Length..], new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!ProofFields.Contains(property.Name) || !seen.Add(property.Name)) return null;
            if (seen.Count != ProofFields.Count
                || !Integer(root, "pid", out var pid) || pid != expectedProcessId || pid <= 1
                || !Integer(root, "uid", out var uid) || uid != expectedUserId || uid == 0
                || !Integer(root, "ppid", out var parent) || parent != expectedParentProcessId
                || !Integer(root, "pdeathsig", out var signal) || signal != 9
                || !Integer(root, "landlock", out var abi) || abi is < 6 or > 1024
                || !Integer(root, "nnp", out var nnp) || nnp != 1
                || !Integer(root, "seccomp", out var seccomp) || seccomp != 2
                || !Integer(root, "netgate", out var gate) || gate != 1
                || !Integer(root, "proxy", out var port) || port != expectedPort
                || !Integer(root, "scoped", out var scoped) || scoped != 3)
                return null;
            return new RunnerProof((int)pid, (uint)uid, (int)parent, (int)abi, (int)port, scoped == 3);
        }
        catch (JsonException) { return null; }
    }

    private static bool Integer(JsonElement value, string name, out long result)
    {
        result = 0;
        return value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out result);
    }

    private static async Task<(string Text, bool Truncated)> DrainAsync(Stream stream, int maximum)
    {
        using var kept = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var truncated = false;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) break;
                var room = maximum - (int)kept.Length;
                if (room > 0) kept.Write(buffer, 0, Math.Min(room, read));
                if (read > room) truncated = true;
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { truncated = true; }
        return (Encoding.UTF8.GetString(kept.GetBuffer(), 0, (int)kept.Length), truncated);
    }

    private sealed class RunnerChild(Process process) : IBlockingHardwareChild
    {
        public int Id => process.Id;
        public bool HasExited => process.HasExited;
        public int? ExitCode => process.HasExited ? process.ExitCode : null;
        public Stream Input => process.StandardInput.BaseStream;
        public Stream Output => process.StandardOutput.BaseStream;
        public Stream Error => process.StandardError.BaseStream;
        public void WaitForExit() => process.WaitForExit();
        public void Kill()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
        }
        public void Dispose() => process.Dispose();
    }

    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MakeDirectory(string path, uint mode);
    [LibraryImport("libc", EntryPoint = "realpath", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RealPath(string path, IntPtr resolved);
    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(IntPtr pointer);
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint EffectiveUserId();
}
