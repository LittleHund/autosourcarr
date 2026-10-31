using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Sources.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests;

public static class ApplicationRuntimeTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("application host: real held locks exclude another process and deduplicate roots", Locks);
        suite.Add("application host: failed multi-root admission releases its earlier locks", LockRollback);
        suite.Add("application host: external composition uses SQLite middleware auth and existing endpoints", () => HttpComposition().GetAwaiter().GetResult());
        suite.Add("application host: reserved liveness and physical shutdown retain instance ownership", () => PhysicalDrain().GetAwaiter().GetResult());
        suite.Add("application host: partial construction closes storage and permits an explicit retry", () => StartupRollback().GetAwaiter().GetResult());
        suite.Add("application host: missing configured database mount is never created", () => MissingMount().GetAwaiter().GetResult());
        suite.Add("application host: pre-admission cancellation and controlled integrated mode create no storage", () => Admission().GetAwaiter().GetResult());
        suite.Add("application host: saved timezone replaces stale inherited process and source launch values", TimeZone);
        if (OperatingSystem.IsWindows())
            suite.Add("application host: actual executable refuses Windows before private configuration", ExecutableGuard);
    }
    private static AutoSourcarrSettings Settings(string root) => new()
    {
        DataDir = Path.Combine(root, "data"), SettingsConfigDir = Path.Combine(root, "config"), SourceEngineMode = "external",
        SourceSuwayomiUrl = "http://127.0.0.1:1", AdminToken = "host-admin-private", AutosourcarrReaderApiToken = "host-reader-private"
    };
    private static void TimeZone()
    {
        using var temp = new Sandbox(); var settings = Settings(temp.Path) with { Tz = "Europe/Amsterdam" };
        var inherited = new Dictionary<string, string?> { ["TZ"] = "Etc/UTC" };
        var previous = Environment.GetEnvironmentVariable("TZ");
        try
        {
            AutoSourcarr.Api.Program.ApplyTimeZone(settings, inherited);
            Check.Equal(settings.Tz, Environment.GetEnvironmentVariable("TZ"));
            var source = new SourceEngineEnvironment(settings.SettingsConfigDir, Path.Combine(temp.Path, "source"), new SupportPrivacy());
            Check.Equal(settings.Tz, source.Build(inherited)["TZ"]);
        }
        finally { Environment.SetEnvironmentVariable("TZ", previous); TimeZoneInfo.ClearCachedData(); }
    }
    public static int LockProbe(string root)
    {
        try { using var held = ApplicationRuntime.InstanceRoots.Acquire([root]); return 0; }
        catch (IOException) { return 17; }
    }
    private static (int Exit, string Output) Child(string assembly, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("exec"); start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AutoSourcarr.Tests.runtimeconfig.json"));
        start.ArgumentList.Add(assembly);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DATA_DIR"] = Path.Combine(workingDirectory, "must-not-create-data");
        start.Environment["SETTINGS_CONFIG_DIR"] = Path.Combine(workingDirectory, "must-not-create-config");
        start.Environment["ADMIN_TOKEN"] = "host-executable-private-sentinel";
        start.Environment["DOTNET_STARTUP_HOOKS"] = "";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new TimeoutException("The confined host probe did not exit."); }
        return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }
    private static void Locks()
    {
        using var temp = new Sandbox(); var root = Path.Combine(temp.Path, "instance");
        using (ApplicationRuntime.InstanceRoots.Acquire([root, root + Path.DirectorySeparatorChar]))
        {
            var child = Child(typeof(ApplicationRuntimeTests).Assembly.Location, temp.Path, "--instance-lock-probe", root);
            Check.Equal(17, child.Exit);
            Check.Throws<IOException>(() => ApplicationRuntime.InstanceRoots.Acquire([root]));
        }
        Check.Equal(0, Child(typeof(ApplicationRuntimeTests).Assembly.Location, temp.Path, "--instance-lock-probe", root).Exit);
        Check.Equal("0", File.ReadAllText(Path.Combine(root, ".autosourcarr-instance.lock")));
        using var again = ApplicationRuntime.InstanceRoots.Acquire([root]);
    }
    private static void LockRollback()
    {
        using var temp = new Sandbox(); var earlier = Path.Combine(temp.Path, "a"); var busy = Path.Combine(temp.Path, "z");
        using var first = ApplicationRuntime.InstanceRoots.Acquire([busy]);
        Check.Throws<IOException>(() => ApplicationRuntime.InstanceRoots.Acquire([earlier, busy]));
        using var recovered = ApplicationRuntime.InstanceRoots.Acquire([earlier]);
        Check.Equal(17, Child(typeof(ApplicationRuntimeTests).Assembly.Location, temp.Path, "--instance-lock-probe", busy).Exit);
    }
    private static async Task HttpComposition()
    {
        using var temp = new Sandbox(); var settings = Settings(temp.Path);
        await using (var runtime = await ApplicationRuntime.CreateExternalForTestsAsync(settings, settings.SettingsConfigDir))
        {
            Check.True(runtime.SourceSnapshots is null, "External mode must not fabricate local installed-byte authority.");
            using var http = new RunningHttp(runtime, temp.Path); using var client = http.Host.GetTestClient();
            var live = await client.GetFromJsonAsync<JsonObject>("/api/v1/live");
            Check.True(live!["ok"]!.GetValue<bool>()); Check.Equal("0.4.3-migration", live["version"]!.GetValue<string>());
            Check.Equal("host-index", await client.GetStringAsync("/"));
            Check.Equal("host-static", await client.GetStringAsync("/static/probe.txt"));
            Check.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/ui-preferences")).StatusCode);
            Check.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/reader/v1/live")).StatusCode);
            using var reader = new HttpRequestMessage(HttpMethod.Get, "/api/reader/v1/live"); reader.Headers.Add("X-AutoSourcarr-Api-Key", settings.AutosourcarrReaderApiToken);
            Check.Equal(HttpStatusCode.OK, (await client.SendAsync(reader)).StatusCode);
            client.DefaultRequestHeaders.Add("X-Admin-Token", settings.AdminToken); client.DefaultRequestHeaders.Add("Remote-User", "HostUser");
            var saved = await client.PutAsync("/api/admin/ui-preferences", new StringContent("{\"values\":{\"theme\":\"dark\"}}", Encoding.UTF8, "application/json"));
            Check.Equal(HttpStatusCode.OK, saved.StatusCode);
            Check.Equal("dark", (await (await client.GetAsync("/api/admin/ui-preferences")).Content.ReadFromJsonAsync<JsonObject>())!["values"]!["theme"]!.GetValue<string>());
            Check.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/unconverted-route")).StatusCode);
            runtime.Preferences.Set("host-preserved", new JsonObject { ["decision"] = "kept" });
            await runtime.BeginHttpStopAsync();
            Check.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/v1/live")).StatusCode);
        }
        await using var reopened = await ApplicationRuntime.CreateExternalForTestsAsync(settings, settings.SettingsConfigDir);
        Check.Equal("kept", reopened.Preferences.Get("host-preserved", new JsonObject())!["decision"]!.GetValue<string>());
    }
    private static async Task PhysicalDrain()
    {
        using var temp = new Sandbox(); var settings = Settings(temp.Path);
        var runtime = await ApplicationRuntime.CreateExternalForTestsAsync(settings, settings.SettingsConfigDir);
        using var entered = new CountdownEvent(4); using var release = new ManualResetEventSlim();
        var workers = Enumerable.Range(0, 4).Select(_ => runtime.Io.RunAsync(() => { entered.Signal(); release.Wait(); return 0; })).ToArray();
        Task? closing = null;
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(10)));
            using var http = new RunningHttp(runtime, temp.Path); using var client = http.Host.GetTestClient();
            Check.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/live")).StatusCode);
            closing = runtime.DisposeAsync().AsTask();
            Check.True(SpinWait.SpinUntil(() => runtime.Stopping, TimeSpan.FromSeconds(3)));
            Check.True(!closing.IsCompleted);
            Check.Throws<IOException>(() => ApplicationRuntime.InstanceRoots.Acquire([settings.SettingsConfigDir]));
        }
        finally
        {
            release.Set();
            try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }
            await (closing ?? runtime.DisposeAsync().AsTask());
        }
        using var recovered = ApplicationRuntime.InstanceRoots.Acquire([settings.SettingsConfigDir]);
        Check.True(workers.All(task => task.IsCompleted));
    }
    private static async Task StartupRollback()
    {
        using var temp = new Sandbox(); var settings = Settings(temp.Path); Exception? failure = null;
        try { await using var unexpected = await ApplicationRuntime.CreateExternalForTestsAsync(settings with { SourceSuwayomiUrl = "file:///invalid-engine" }, settings.SettingsConfigDir); }
        catch (Exception error) { failure = error; }
        Check.True(failure is ArgumentException or UriFormatException, failure?.ToString() ?? "Invalid engine was admitted.");
        using (ApplicationRuntime.InstanceRoots.Acquire([settings.SettingsConfigDir, settings.DataDir])) { }
        await using var retry = await ApplicationRuntime.CreateExternalForTestsAsync(settings, settings.SettingsConfigDir);
        retry.Preferences.Set("startup-retry", new JsonObject { ["ok"] = true });
        Check.True(retry.Preferences.Get("startup-retry", new JsonObject())!["ok"]!.GetValue<bool>());
    }
    private static async Task MissingMount()
    {
        using var temp = new Sandbox(); var settings = Settings(temp.Path) with { DatabaseDir = Path.Combine(temp.Path, "missing-mount") };
        Exception? failure = null;
        try { await using var unexpected = await ApplicationRuntime.CreateExternalForTestsAsync(settings, settings.SettingsConfigDir); }
        catch (Exception error) { failure = error; }
        Check.True(failure is ConfigurationException, failure?.ToString() ?? "A replacement mount was admitted.");
        Check.True(!Directory.Exists(settings.DatabaseDir));
        Check.True(!File.Exists(Path.Combine(settings.DataDir, "catalog.sqlite")));
        Check.True(!Directory.Exists(settings.SettingsConfigDir));
    }
    private static async Task Admission()
    {
        using var temp = new Sandbox(); var settings = Settings(temp.Path); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await using var unexpected = await ApplicationRuntime.CreateExternalForTestsAsync(settings, settings.SettingsConfigDir, cancel.Token); throw new InvalidOperationException("Cancelled startup was admitted."); }
        catch (OperationCanceledException) { }
        Check.Throws<ArgumentException>(() => ApplicationRuntime.CreateExternalForTestsAsync(settings with { SourceEngineMode = "integrated" }, settings.SettingsConfigDir));
        Check.True(!Directory.Exists(settings.DataDir)); Check.True(!Directory.Exists(settings.SettingsConfigDir));
    }
    private static void ExecutableGuard()
    {
        using var temp = new Sandbox();
        using var locked = new FileStream(Path.Combine(temp.Path, ".env"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        locked.Write(Encoding.UTF8.GetBytes("ADMIN_TOKEN=host-executable-private-sentinel")); locked.Flush(true);
        var child = Child(typeof(ApplicationRuntime).Assembly.Location, temp.Path);
        Check.Equal(78, child.Exit); Check.True(child.Output.Contains("PlatformNotSupportedException", StringComparison.Ordinal));
        Check.True(!child.Output.Contains("private-sentinel", StringComparison.Ordinal));
        Check.True(!Directory.Exists(Path.Combine(temp.Path, "must-not-create-data")));
        Check.True(!Directory.Exists(Path.Combine(temp.Path, "must-not-create-config")));
    }
    private sealed class RunningHttp : IDisposable
    {
        internal IHost Host { get; }
        internal RunningHttp(ApplicationRuntime runtime, string root)
        {
            var assets = Path.Combine(root, "http-assets"); Directory.CreateDirectory(assets);
            File.WriteAllText(Path.Combine(assets, "index.html"), "host-index"); File.WriteAllText(Path.Combine(assets, "probe.txt"), "host-static");
            Host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => services.AddRouting())
                .Configure(app => runtime.ConfigureHttp(app, assets))).Build();
            Host.StartAsync().GetAwaiter().GetResult();
        }
        public void Dispose() => Host.Dispose();
    }
}
