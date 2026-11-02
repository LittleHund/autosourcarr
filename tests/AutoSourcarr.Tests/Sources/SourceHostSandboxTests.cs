using System.Diagnostics;
using AutoSourcarr.Core.Sources.Runtime;

namespace AutoSourcarr.Tests.Sources;

/// <summary>Phase 13 items 2–3 (SRC-5): the source host sandbox contract, its health state and (Linux CI) the real launcher.</summary>
public static class SourceHostSandboxTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("source host sandbox: environment contract, launcher arguments and identity", () =>
        {
            var options = SourceHostSandboxOptions.FromEnvironment(new Dictionary<string, string?>
            {
                ["AUTOSOURCARR_SOURCE_HOST_UID"] = "569", ["AUTOSOURCARR_SOURCE_HOST_GID"] = "569",
                ["AUTOSOURCARR_SOURCE_HOST_RW"] = "/data/source-cache:relative/ignored", ["AUTOSOURCARR_SOURCE_HOST_RO"] = "/opt/source-host/kcef",
            });
            Check.Equal(SourceHostSandboxOptions.DefaultLauncher, options.LauncherPath);
            Check.Equal(569u, options.UserId!.Value);
            Check.Equal(1, options.ReadWrite!.Count);
            var arguments = string.Join(' ', options.Arguments("/data/source-host", "/opt/source-host/bin/java", "/app/source-host/autosourcarr-source-host.jar"));
            Check.Equal("--uid 569 --gid 569 --data /data/source-host --rw /data/source-cache --ro /opt/source-host --ro /app/source-host --ro /opt/source-host/kcef --", arguments);
            Check.True(SourceHostSandboxOptions.FromEnvironment(new Dictionary<string, string?> { ["AUTOSOURCARR_SOURCE_HOST_UID"] = "0" }).UserId is null, "uid 0 is never a source host user");
        });

        suite.Add("source host sandbox: the launcher's proof line becomes the health state; unsandboxed is degraded, never a refusal", () =>
        {
            var proof = SourceHostSandboxState.Parse("AUTOSOURCARR_SOURCE_SANDBOX_V1 {\"uid\":568,\"separated\":0,\"landlock\":6,\"scoped\":2,\"nnp\":1,\"reason\":\"identity-unavailable\"}")!;
            Check.True(proof.Sandboxed && proof.SignalsScoped && !proof.SeparateUser, proof.ToString());
            Check.True(proof.Message().Contains("CAP_SETUID", StringComparison.Ordinal), proof.Message());
            var separated = SourceHostSandboxState.Parse("AUTOSOURCARR_SOURCE_SANDBOX_V1 {\"uid\":569,\"separated\":1,\"landlock\":6,\"scoped\":2,\"nnp\":1,\"reason\":\"\"}")!;
            Check.True(separated.SeparateUser && separated.UserId == 569, separated.ToString());
            var none = SourceHostSandboxState.Parse("AUTOSOURCARR_SOURCE_SANDBOX_V1 {\"uid\":568,\"separated\":0,\"landlock\":0,\"scoped\":0,\"nnp\":1,\"reason\":\"\"}")!;
            Check.True(!none.Sandboxed && none.Reason == "landlock-unavailable" && none.Message().Contains("WITHOUT a sandbox", StringComparison.Ordinal), none.Message());
            Check.True(SourceHostSandboxState.Parse("AUTOSOURCARR_RUNNER_V1 {}") is null && SourceHostSandboxState.Parse("AUTOSOURCARR_SOURCE_SANDBOX_V1 [") is null, "foreign or broken lines are ignored");
        });

        suite.Add("source host sandbox: a missing launcher (or another platform) starts the host directly and reports it unsandboxed", () =>
        {
            using var sandbox = new Sandbox();
            var process = new SourceHostProcess(Path.Combine(sandbox.Path, "java"), Path.Combine(sandbox.Path, "host.jar"), Path.Combine(sandbox.Path, "data"), 1,
                new Dictionary<string, string>(), _ => { }) { Sandbox = new SourceHostSandboxOptions(Path.Combine(sandbox.Path, "no-such-launcher")) };
            try
            {
                Check.Equal("unknown", process.SandboxState.State); // not launched yet
                var (launcher, unsandboxed) = process.SandboxLauncher();
                Check.True(launcher is null && unsandboxed is { State: "unsandboxed" }, "no launcher: the host starts directly");
                Check.Equal(OperatingSystem.IsLinux() ? "sandbox-missing" : "platform", unsandboxed!.Reason);
                var unconfigured = new SourceHostProcess("/x/java", "/x/host.jar", "/x/data", 1, new Dictionary<string, string>(), _ => { });
                try { Check.Equal("sandbox-not-configured", unconfigured.SandboxLauncher().Unsandboxed!.Reason); }
                finally { unconfigured.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            finally { process.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });

        // Linux CI (probe): the real launcher confines writes to the data folder and reports its proof.
        var launcher = Environment.GetEnvironmentVariable("AUTOSOURCARR_SOURCE_HOST_SANDBOX") is { Length: > 0 } configured ? configured : SourceHostSandboxOptions.DefaultLauncher;
        if (OperatingSystem.IsLinux() && File.Exists(launcher) && File.Exists("/bin/sh"))
            suite.Add("source host sandbox (linux): the launcher allows writes only in the data folder and reports Landlock", () =>
            {
                using var sandbox = new Sandbox();
                var data = Path.Combine(sandbox.Path, "data"); Directory.CreateDirectory(data);
                var outside = Path.Combine(sandbox.Path, "outside.txt"); var inside = Path.Combine(data, "inside.txt");
                var start = new ProcessStartInfo(launcher) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
                foreach (var argument in new[] { "--data", data, "--", "/bin/sh", "-c", "echo x > '" + outside + "' 2>/dev/null; echo y > '" + inside + "'" })
                    start.ArgumentList.Add(argument);
                using var child = Process.Start(start)!;
                var error = child.StandardError.ReadToEnd();
                child.WaitForExit(30_000);
                var proof = SourceHostSandboxState.Parse(error.Split('\n')[0]) ?? throw new InvalidOperationException("no proof: " + error);
                Check.True(File.Exists(inside), "the data folder is writable");
                if (proof.Sandboxed) Check.True(!File.Exists(outside), "a write outside the data folder must be refused under Landlock");
                else Console.WriteLine("NOTE source host sandbox (linux): kernel without Landlock; reported " + proof.Reason);
            });
        else Console.WriteLine("SKIP source host sandbox (linux): needs Linux and the launcher at " + launcher + " (build native/source-host-sandbox.c)");
    }
}
