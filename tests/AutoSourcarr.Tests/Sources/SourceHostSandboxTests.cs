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
            Check.Equal("--uid 569 --gid 569 --data /data/source-host --rw /data/source-cache --rw /tmp/autosourcarr-source-host --ro /opt/source-host --ro /app/source-host --ro /opt/source-host/kcef --", arguments);
            Check.Equal("/run/ash", SourceHostSandboxOptions.FromEnvironment(new Dictionary<string, string?> { ["AUTOSOURCARR_SOURCE_HOST_TMP"] = "/run/ash/" }).TempDirectory);
            Check.Equal(SourceHostSandboxOptions.DefaultTemp, SourceHostSandboxOptions.FromEnvironment(new Dictionary<string, string?> { ["AUTOSOURCARR_SOURCE_HOST_TMP"] = "relative" }).TempDirectory);
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

        suite.Add("source host sandbox: signal scoping follows the kernel erratum, and the setting, never by itself after a crash (0.5.3)", () =>
        {
            const string Java = "/opt/source-host/bin/java", Jar = "/app/source-host/autosourcarr-source-host.jar";
            var auto = SourceHostSandboxOptions.FromEnvironment(new Dictionary<string, string?>());
            Check.Equal("auto", auto.Signals);
            Check.True(!auto.Arguments("/data/source-host", Java, Jar).Contains("--signals"), "auto passes nothing: the launcher decides from the kernel");
            var off = SourceHostSandboxOptions.FromEnvironment(new Dictionary<string, string?> { ["AUTOSOURCARR_SOURCE_HOST_SIGNAL_SCOPE"] = " OFF " });
            Check.Equal("--signals off", string.Join(' ', off.Arguments("/data/source-host", Java, Jar).Take(2)));
            Check.Equal("on", SourceHostSandboxOptions.FromEnvironment(new Dictionary<string, string?> { ["AUTOSOURCARR_SOURCE_HOST_SIGNAL_SCOPE"] = "on" }).Signals);
            Check.Equal("auto", SourceHostSandboxOptions.FromEnvironment(new Dictionary<string, string?> { ["AUTOSOURCARR_SOURCE_HOST_SIGNAL_SCOPE"] = "maybe" }).Signals);

            var erratum = SourceHostSandboxState.Parse("AUTOSOURCARR_SOURCE_SANDBOX_V1 {\"uid\":568,\"separated\":0,\"landlock\":6,\"scoped\":0,\"nnp\":1,\"reason\":\"\",\"signals\":\"kernel-erratum\"}")!;
            Check.True(erratum.Sandboxed && !erratum.SignalsScoped && erratum.Signals == "kernel-erratum", erratum.ToString());
            Check.True(erratum.Message().Contains("fixed in Linux 6.15", StringComparison.Ordinal), erratum.Message());
            Check.Equal("kernel-erratum", erratum.Json()["signals"]!.GetValue<string>());
            var old = SourceHostSandboxState.Parse("AUTOSOURCARR_SOURCE_SANDBOX_V1 {\"uid\":568,\"separated\":0,\"landlock\":6,\"scoped\":2,\"nnp\":1,\"reason\":\"\"}")!;
            Check.True(old.SignalsScoped && old.Signals == "", "an older launcher's proof still parses");

            // The app never weakens the sandbox by itself (sandboxed code can crash the host on purpose): it only adds a hint.
            Check.True(SourceHostProcess.WithSignalHint("Off.", signalsScoped: true)!.Contains("AUTOSOURCARR_SOURCE_HOST_SIGNAL_SCOPE=off", StringComparison.Ordinal), "hint when scoped");
            Check.Equal("Off.", SourceHostProcess.WithSignalHint("Off.", signalsScoped: false));
            Check.True(SourceHostProcess.WithSignalHint(null, signalsScoped: true) is null, "below the limit nothing changes");
        });

        suite.Add("source host: a stale Chromium profile lock from an earlier container is cleared before the host starts (0.5.3)", () =>
        {
            using var sandbox = new Sandbox();
            var profile = Path.Combine(sandbox.Path, "cache", "kcef");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "Cookies"), "keep");
            File.WriteAllText(Path.Combine(profile, "SingletonCookie"), "x");
            try { File.CreateSymbolicLink(Path.Combine(profile, "SingletonLock"), "b4ab980bf49c-104"); } // dangling, as Chromium writes it
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { File.WriteAllText(Path.Combine(profile, "SingletonLock"), "x"); }
            var lines = new List<string>();
            SourceHostProcess.ClearChromiumLock(sandbox.Path, lines.Add);
            Check.True(new FileInfo(Path.Combine(profile, "SingletonLock")).LinkTarget is null && !File.Exists(Path.Combine(profile, "SingletonLock")), "lock removed");
            Check.True(!File.Exists(Path.Combine(profile, "SingletonCookie")) && File.Exists(Path.Combine(profile, "Cookies")), "only the lock files go");
            Check.Equal(1, lines.Count(line => line.Contains("Cleared", StringComparison.Ordinal)));
            lines.Clear();
            SourceHostProcess.ClearChromiumLock(sandbox.Path, lines.Add);
            SourceHostProcess.ClearChromiumLock(Path.Combine(sandbox.Path, "missing"), lines.Add);
            Check.Equal(0, lines.Count);
        });

        suite.Add("source host: Chromium's temp folder is a private folder off the data folder, with a logged fallback (0.5.4)", () =>
        {
            using var sandbox = new Sandbox();
            var lines = new List<string>();
            var preferred = Path.Combine(sandbox.Path, "own-tmp");
            Check.Equal(preferred, SourceHostProcess.PrivateTemp(preferred, Path.Combine(sandbox.Path, "data", "tmp"), lines.Add));
            Check.True(Directory.Exists(preferred) && lines.Count == 0, string.Join('\n', lines));
            if (!OperatingSystem.IsWindows()) Check.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(preferred));
            var blocked = Path.Combine(sandbox.Path, "a-file");
            File.WriteAllText(blocked, "not a folder");
            var fallback = Path.Combine(sandbox.Path, "data", "tmp");
            Check.Equal(fallback, SourceHostProcess.PrivateTemp(blocked, fallback, lines.Add));
            Check.True(Directory.Exists(fallback) && lines.Single().Contains("AUTOSOURCARR_SOURCE_HOST_TMP", StringComparison.Ordinal), string.Join('\n', lines));
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

        suite.Add("source host sandbox: a proof line counts only as the launcher's first stderr line, never from host or extension output (audit 7)", () =>
        {
            var logged = new List<string>();
            var process = new SourceHostProcess("/x/java", "/x/host.jar", "/x/data", 1, new Dictionary<string, string>(), line => logged.Add(line));
            try
            {
                const string Fake = "AUTOSOURCARR_SOURCE_SANDBOX_V1 {\"uid\":569,\"separated\":1,\"landlock\":6,\"scoped\":2,\"nnp\":1,\"reason\":\"\"}";
                process.Output(Fake, proof: false);
                Check.Equal("unknown", process.SandboxState.State);
                Check.True(logged.Any(line => line.Contains("Ignored a sandbox proof", StringComparison.Ordinal)), string.Join("|", logged));
                process.Output(Fake, proof: true);
                Check.True(process.SandboxState.Sandboxed && process.SandboxState.SeparateUser, process.SandboxState.ToString());
            }
            finally { process.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });

        suite.Add("source host: a data folder that cannot be created is a reported start error, not a silent fault (audit 2)", () =>
        {
            using var sandbox = new Sandbox();
            var jar = Path.Combine(sandbox.Path, "host.jar"); File.WriteAllText(jar, "not a jar");
            var blocked = Path.Combine(sandbox.Path, "data-is-a-file"); File.WriteAllText(blocked, "x");
            var process = new SourceHostProcess(Path.Combine(sandbox.Path, "java"), jar, Path.Combine(blocked, "source-host"), 1,
                new Dictionary<string, string>(), _ => { });
            try
            {
                var error = Check.Throws<InvalidOperationException>(() => process.StartAsync(TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult());
                Check.True(error.Message.Contains("could not be created", StringComparison.Ordinal), error.Message);
                Check.True(process.LastError.Contains("could not be created", StringComparison.Ordinal), process.LastError);
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
