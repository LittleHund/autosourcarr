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
        suite.Add("application host: the source host environment keeps the solver split and drops loader/injection keys (replaces the native launch guard)", SourceHostEnvironmentGuard);
        suite.Add("application host: SOURCE_WEBVIEW_ENABLED=false turns the source host WebView off; it never turns a source-host.env off back on", SourceHostWebViewSetting);
        suite.Add("source host: two signal deaths soon after a start with the WebView on turn the WebView off (0.5.1 crash fallback)", SourceHostWebViewCrashFallback);
        suite.Add("application host: a restart the app asks for stops normally and exits with the restart code (no native launcher)", () => RestartExit().GetAwaiter().GetResult());
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
    private static void SourceHostEnvironmentGuard()
    {
        using var temp = new Sandbox(); var privacy = new SupportPrivacy();
        var content = "SOURCE_PASSWORD=synthetic-host-source-secret\nFLARESOLVERR_ENABLED=true\nLD_AUDIT=/tmp/x.so\nDOTNET_STARTUP_HOOKS=/tmp/h.dll\n"
            + "CORECLR_ENABLE_PROFILING=1\nCOMPlus_EnableDiagnostics=1\nJDK_JAVA_OPTIONS=-Dx=1\nKEEP_ME=1\n";
        File.WriteAllText(Path.Combine(temp.Path, "source-host.env"), content);
        var launch = new AutoSourcarr.Core.Sources.Runtime.SourceEngineEnvironment(temp.Path, Path.Combine(temp.Path, "profile"), privacy);
        var parent = new Dictionary<string, string?> { ["HOME"] = "/home/autosourcarr", ["PATH"] = "/usr/bin:/bin", ["TZ"] = "UTC" };
        var environment = ApplicationRuntime.SourceHostEnvironment(new AutoSourcarrSettings { AppServicesMode = "integrated" }, launch, parent);
        foreach (var key in new[] { "LD_AUDIT", "DOTNET_STARTUP_HOOKS", "CORECLR_ENABLE_PROFILING", "COMPlus_EnableDiagnostics", "JDK_JAVA_OPTIONS",
                     "JAVA_TOOL_OPTIONS", "BIND_IP", "AUTH_MODE" })
            Check.True(!environment.ContainsKey(key), key + " must not reach the source host");
        Check.Equal("1", environment["KEEP_ME"]); Check.Equal("UTC", environment["TZ"]); Check.Equal("/usr/bin:/bin", environment["PATH"]);
        Check.Equal("false", environment["FLARESOLVERR_ENABLED"]); // solver intent stays separate from the launch value
        Check.Equal("[redacted]", privacy.Text("synthetic-host-source-secret"));
        foreach (var key in new[] { "GTK_MODULES", "GTK_PATH", "GIO_EXTRA_MODULES", "GDK_PIXBUF_MODULE_FILE", "GCONV_PATH", "LOCPATH", "MALLOC_CHECK_", "MALLOC_ARENA_MAX" })
            Check.True(ApplicationRuntime.LoaderKey(key), key + " loads code or changes the allocator (audit 9)");
        Check.True(ApplicationRuntime.LoaderKey("1BAD") && ApplicationRuntime.LoaderKey("BAD-KEY") && ApplicationRuntime.LoaderKey(new string('X', 129)) && !ApplicationRuntime.LoaderKey("SOURCE_MIN_INTERVAL_MS"));
    }
    private static void SourceHostWebViewSetting()
    {
        using var temp = new Sandbox(); var privacy = new SupportPrivacy();
        var launch = new AutoSourcarr.Core.Sources.Runtime.SourceEngineEnvironment(temp.Path, Path.Combine(temp.Path, "profile"), privacy);
        var parent = new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin" };
        var on = ApplicationRuntime.SourceHostEnvironment(new AutoSourcarrSettings(), launch, parent);
        Check.True(!on.ContainsKey("WEBVIEW_ENABLED"), "the default leaves the WebView to the host (on)");
        var off = ApplicationRuntime.SourceHostEnvironment(new AutoSourcarrSettings { SourceWebViewEnabled = false }, launch, parent);
        Check.Equal("false", off["WEBVIEW_ENABLED"]);
        File.WriteAllText(Path.Combine(temp.Path, "source-host.env"), "WEBVIEW_ENABLED=false\n");
        var file = ApplicationRuntime.SourceHostEnvironment(new AutoSourcarrSettings { SourceWebViewEnabled = true }, launch, parent);
        Check.Equal("false", file["WEBVIEW_ENABLED"]);
    }
    private static void SourceHostWebViewCrashFallback()
    {
        var soon = TimeSpan.FromSeconds(8);
        var first = SourceHostProcess.WebViewCrash(133, soon, webViewOn: true, crashes: 0);
        Check.Equal(1, first.Crashes); Check.True(first.TurnOff is null, "one crash is not enough");
        var second = SourceHostProcess.WebViewCrash(133, soon, webViewOn: true, crashes: first.Crashes);
        Check.Equal(2, second.Crashes);
        Check.True(second.TurnOff is { } reason && reason.Contains("signal 5", StringComparison.Ordinal) && reason.Contains("SOURCE_WEBVIEW_ENABLED", StringComparison.Ordinal), "the second crash turns it off and says why");
        Check.Equal(0, SourceHostProcess.WebViewCrash(1, soon, webViewOn: true, crashes: 1).Crashes); // a normal error exit resets the count
        Check.Equal(0, SourceHostProcess.WebViewCrash(134, TimeSpan.FromMinutes(30), webViewOn: true, crashes: 1).Crashes); // long uptime is not a start crash
        Check.Equal(0, SourceHostProcess.WebViewCrash(137, soon, webViewOn: true, crashes: 1).Crashes); // SIGKILL (out of memory) is not Chromium
        var noToken = new Dictionary<string, string?> { ["AUTOSOURCARR_PUBLIC_URL"] = "https://reader.example" };
        Check.True(AutoSourcarr.Core.Settings.ReaderConfigCheck.Evaluate(noToken).Errors.Any(e => e.Code == 12), "no token and no reader key: error 12");
        Check.True(AutoSourcarr.Core.Settings.ReaderConfigCheck.Evaluate(noToken, readerKeys: true).Errors.All(e => e.Code is not (12 or 13)), "a reader key replaces the token (no format error for the empty token)");
        var withoutWebView = SourceHostProcess.WebViewCrash(139, soon, webViewOn: false, crashes: 1);
        Check.Equal(1, withoutWebView.Crashes); Check.True(withoutWebView.TurnOff is null, "a host without the WebView never blames it");
    }
    private static async Task RestartExit()
    {
        using var temp = new Sandbox(); var settings = Settings(temp.Path);
        await using var runtime = await ApplicationRuntime.CreateExternalForTestsAsync(settings, settings.SettingsConfigDir);
        var stops = 0; runtime.RequestStop = () => stops++;
        Check.True(!runtime.RestartRequested, "no restart before it is asked for");
        runtime.RequestRestart();
        Check.Equal(1, stops);
        Check.True(runtime.RestartRequested, "the restart is remembered for the exit code");
        Check.Equal(75, ApplicationRuntime.RestartExitCode); // documented image contract: not 0 (clean stop) and not 78 (fatal)
    }
    private static async Task HttpComposition()
    {
        using var temp = new Sandbox(); var settings = Settings(temp.Path);
        await using (var runtime = await ApplicationRuntime.CreateExternalForTestsAsync(settings, settings.SettingsConfigDir))
        {
            using var http = new RunningHttp(runtime, temp.Path); using var client = http.Host.GetTestClient();
            var live = await client.GetFromJsonAsync<JsonObject>("/api/v1/live");
            Check.True(live!["ok"]!.GetValue<bool>()); Check.Equal("0.5.1", live["version"]!.GetValue<string>());
            // Accounts (phase 5): the page needs a sign-in; the login page and static files are public.
            using (var page = await client.GetAsync("/"))
            {
                Check.Equal(HttpStatusCode.Redirect, page.StatusCode);
                Check.Equal("/login?next=%2F", page.Headers.Location!.OriginalString);
            }
            using (var adminPage = new HttpRequestMessage(HttpMethod.Get, "/"))
            {
                adminPage.Headers.Add("X-Admin-Token", settings.AdminToken);
                Check.Equal("host-index", await (await client.SendAsync(adminPage)).Content.ReadAsStringAsync());
            }
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
