using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Repair.Runner;
using AutoSourcarr.Core.Repair.Runner.Egress;

namespace AutoSourcarr.Tests.Repair;

/// <summary>Phase 13 item 1: repair code runs only through the runner; without isolation it is refused (hard rule 13).</summary>
public static class RepairIsolationRuntimeTests
{
    private sealed class FakeHost : IRepairIsolationHost
    {
        public bool LinuxX64 { get; init; } = true;
        public bool Root { get; init; }
        public bool RunnerInstalled { get; init; } = true;
        public int Abi { get; init; } = 6;
        public bool Executable(string path) => RunnerInstalled || !path.Contains("runner", StringComparison.Ordinal);
        public int LandlockAbi() => Abi;
    }

    private static readonly IsolatedRunRequest Request = new("/usr/bin/true", [], [], "test agent");

    public static void Register(TestSuite suite)
    {
        suite.Add("repair isolation: missing runner, Landlock below ABI 6, root or another platform are 'isolation unavailable' and nothing runs", () =>
        {
            foreach (var (host, reason) in new[]
            {
                (new FakeHost { RunnerInstalled = false }, "runner-missing"), (new FakeHost { Abi = 5 }, "landlock-abi"),
                (new FakeHost { Root = true }, "root"), (new FakeHost { LinuxX64 = false }, "platform"),
            })
            {
                var ran = 0;
                using var sandbox = new Sandbox();
                var isolation = new RepairIsolation(sandbox.Path, host: host, runnerPath: () => "/opt/autosourcarr-native/repair-runner",
                    run: (_, _) => { ran++; throw new InvalidOperationException("must not run"); });
                var state = isolation.Probe();
                Check.True(!state.Available && state.State == "unavailable", reason);
                Check.Equal(reason, state.Reason);
                var error = Check.Throws<RepairIsolationUnavailableException>(() => isolation.RunAsync(Request, CancellationToken.None).GetAwaiter().GetResult());
                Check.True(error.Message.StartsWith("Isolation unavailable: ", StringComparison.Ordinal), error.Message);
                Check.Equal(0, ran);
                var check = isolation.SelfCheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                Check.True(check["ok"]!.GetValue<bool>() == false && check["state"]!.GetValue<string>() == "unavailable", check.ToJsonString());
            }
        });

        suite.Add("repair isolation: a runner that refuses (no proof) fails closed; a proven run is returned", () =>
        {
            using var sandbox = new Sandbox();
            var refusing = new RepairIsolation(sandbox.Path, host: new FakeHost(), run: (_, _) =>
                Task.FromResult(new IsolatedRunResult(78, "", "repair-runner: Landlock ABI 6 ... is required\n", [], false, false, "", null)));
            var error = Check.Throws<RepairIsolationUnavailableException>(() => refusing.RunAsync(Request, CancellationToken.None).GetAwaiter().GetResult());
            Check.Equal("runner-refused", error.Isolation.Reason);
            Check.True(error.Message.Contains("Landlock ABI 6", StringComparison.Ordinal), error.Message);
            var proven = new RepairIsolation(sandbox.Path, host: new FakeHost(), run: (_, _) =>
                Task.FromResult(new IsolatedRunResult(0, "", "", [], false, false, "", new RunnerProof(42, 568, 7, 6, 40000, true))));
            Check.Equal(0, proven.RunAsync(Request, CancellationToken.None).GetAwaiter().GetResult().ExitCode);
            var check = proven.SelfCheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check.True(check["ok"]!.GetValue<bool>() && check["proof"]!["landlock_abi"]!.GetValue<int>() == 6, check.ToJsonString());
        });

        suite.Add("repair isolation: rebuilding a source is refused with 'Isolation unavailable' before anything else when the runner is missing", () =>
        {
            using var sandbox = new Sandbox();
            var missing = new RepairIsolation(sandbox.Path, host: new FakeHost { RunnerInstalled = false });
            var guarded = new IsolatedRawReconstruction(missing, NoRawReconstruction.Instance);
            var refused = Check.Throws<ApiProblem>(() => guarded.EnsureReadyAsync(CancellationToken.None).GetAwaiter().GetResult());
            Check.Equal(409, refused.StatusCode);
            Check.True(refused.Message.StartsWith("Isolation unavailable: ", StringComparison.Ordinal), refused.Message);
            // With isolation the next refusal (no AI provider in this version) is what the user sees.
            var ready = new IsolatedRawReconstruction(new RepairIsolation(sandbox.Path, host: new FakeHost()), NoRawReconstruction.Instance);
            var next = Check.Throws<ApiProblem>(() => ready.EnsureReadyAsync(CancellationToken.None).GetAwaiter().GetResult());
            Check.True(next.Message.Contains("AI provider", StringComparison.Ordinal), next.Message);
        });

        // Linux CI (probe): the real runner binary, when built and installed, passes its self-check.
        var runner = IsolatedRunner.RunnerPath;
        if (OperatingSystem.IsLinux() && File.Exists(runner) && LinuxRepairIsolationHost.Instance.LandlockAbi() >= 6 && !LinuxRepairIsolationHost.Instance.Root)
            suite.Add("repair isolation (linux): the installed repair runner passes the self-check with Landlock proof", () =>
            {
                using var sandbox = new Sandbox();
                var check = new RepairIsolation(sandbox.Path).SelfCheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                Check.True(check["ok"]!.GetValue<bool>(), check.ToJsonString());
            });
        else Console.WriteLine("SKIP repair isolation (linux): needs Linux x64, the runner at " + runner + " (AUTOSOURCARR_REPAIR_RUNNER), Landlock ABI 6 and a non-root user");
    }
}
