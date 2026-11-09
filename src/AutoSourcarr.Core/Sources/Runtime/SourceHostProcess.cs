using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AutoSourcarr.Core.Sources.Runtime;

/// <summary>
/// Phase 4 integrated source engine: the app starts the AutoSourcarr source host (one JVM, <c>java -jar</c>) itself
/// instead of Suwayomi through the native launcher. The host listens on 127.0.0.1 only and every request carries a
/// token made for this run. A host that exits is started again after 2 s, 5 s, 15 s, 30 s and then every 60 s; the
/// ladder resets after ten minutes of uptime. The host keeps its own state (extensions, ids) in its data folder.
/// </summary>
public sealed partial class SourceHostProcess : IAsyncDisposable
{
    private static readonly TimeSpan[] RestartDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    private readonly object _gate = new();
    private readonly string _java, _jar, _dataDirectory;
    private readonly Func<IReadOnlyDictionary<string, string>> _environment;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _health;
    private Process? _process;
    private Process? _display;
    private bool _group;
    private string _displayName = "";
    private Task? _watch;
    private int _restarts;
    private string _lastError = "", _startupError = "";
    private bool _healthy;
    private bool _recycling;
    private SourceHostSandboxState _sandbox = SourceHostSandboxState.Unknown;
    private bool _launchedWithWebView;
    private int _webViewCrashes;
    private string? _webViewOff;

    /// <summary>
    /// 0.5.1: called once with the reason when the app turns the WebView off by itself after it crashed the host (for a
    /// pinned notice). The host then keeps running without it until the app restarts.
    /// </summary>
    public Action<string>? WebViewTurnedOff { get; init; }

    /// <summary>
    /// Why the host's WebView is off: "setting" (SOURCE_WEBVIEW_ENABLED=false or WEBVIEW_ENABLED=false in source-host.env),
    /// the crash reason after the automatic fallback, or null while it is on.
    /// </summary>
    public string? WebViewOff
    {
        get
        {
            lock (_gate)
            {
                if (_webViewOff is { } reason) return reason;
                // A running host keeps what it was started with; a changed setting applies at its next start.
                if (_process is { HasExited: false }) return _launchedWithWebView ? null : "setting";
            }
            return WebViewRequested(_environment()) ? null : "setting";
        }
    }

    /// <summary>Crashes by a signal within this time after a start count as WebView crashes.</summary>
    internal static readonly TimeSpan WebViewCrashWindow = TimeSpan.FromMinutes(5);
    internal const int WebViewCrashLimit = 2;

    private static bool WebViewRequested(IReadOnlyDictionary<string, string> environment) =>
        !string.Equals(environment.GetValueOrDefault("WEBVIEW_ENABLED")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 0.5.1 crash fallback. On some machines Chromium (JCEF, loaded into the host JVM) kills the whole host with a signal
    /// right after the start (seen: exit 133 = SIGTRAP). A JVM fault in extension code is a Java exception, not a signal,
    /// so a host that had the WebView on and died by a signal shortly after starting counts as a WebView crash. Returns
    /// the new crash count and, once the limit is reached, the reason to turn the WebView off.
    /// </summary>
    internal static (int Crashes, string? TurnOff) WebViewCrash(int exitCode, TimeSpan uptime, bool webViewOn, int crashes)
    {
        if (!webViewOn) return (crashes, null);
        var signal = exitCode is > 128 and < 128 + 65 ? exitCode - 128 : 0;
        // SIGKILL (9) is the kernel's out-of-memory kill or a stop from outside, never Chromium's own crash.
        if (signal is 0 or 9 || uptime > WebViewCrashWindow) return (0, null);
        crashes++;
        return crashes < WebViewCrashLimit ? (crashes, null)
            : (crashes, "The WebView (Chromium) stopped the source host " + crashes.ToString(CultureInfo.InvariantCulture) + " times (signal "
                + signal.ToString(CultureInfo.InvariantCulture) + ", exit code " + exitCode.ToString(CultureInfo.InvariantCulture)
                + "), so it runs without the WebView until AutoSourcarr restarts. Sources work; Cloudflare checks and login pages need an "
                + "external solver (Connections) meanwhile. To keep it off set SOURCE_WEBVIEW_ENABLED=false.");
    }

    /// <summary>
    /// 0.5.3: the WebView turn-off reason, with a hint when the host ran with Landlock signal scoping: on a kernel without
    /// the signal fix that does not report it either, scoping is what crashes Chromium. The app never turns scoping off by
    /// itself (code inside the sandbox can crash the host on purpose); the operator decides with the setting.
    /// </summary>
    internal static string? WithSignalHint(string? turnOff, bool signalsScoped) =>
        turnOff is null || !signalsScoped ? turnOff
            : turnOff + " The host ran with Landlock signal scoping; on a Linux kernel older than 6.15 that can cause this crash: "
                + "set AUTOSOURCARR_SOURCE_HOST_SIGNAL_SCOPE=off and restart AutoSourcarr to keep the WebView (the file sandbox stays on).";

    /// <summary>
    /// Phase 13 (SRC-5): start the host through <c>native/source-host-sandbox</c> (Landlock, optional low-rights user).
    /// Null or a missing launcher starts it directly; that is reported as "unsandboxed" (never a refusal: sources must work).
    /// </summary>
    public SourceHostSandboxOptions? Sandbox { get; init; }

    /// <summary>
    /// PERF-2 limits for the host process (cgroup v2 cpu.weight/memory.max when writable, else nothing: a JVM must not get
    /// an address-space limit). Default: normal CPU weight and 4 GiB memory, so a runaway extension cannot take the
    /// container's memory; the app itself stays outside that group.
    /// </summary>
    public AutoSourcarr.Core.Jobs.ProcessLimits? Limits { get; init; } = DefaultLimits;
    public static readonly AutoSourcarr.Core.Jobs.ProcessLimits DefaultLimits = new(CpuWeight: 100, MemoryBytes: 4L << 30);

    /// <summary>What the running host runs under (from the launcher's proof line), for health.</summary>
    public SourceHostSandboxState SandboxState { get { lock (_gate) return _sandbox; } }

    public SourceHostProcess(string java, string jar, string dataDirectory, int port, IReadOnlyDictionary<string, string> environment, Action<string> log)
        : this(java, jar, dataDirectory, port, () => environment, log) { }

    /// <param name="environment">Built again for every launch, so a restarted host gets the saved identity and repositories.</param>
    public SourceHostProcess(string java, string jar, string dataDirectory, int port, Func<IReadOnlyDictionary<string, string>> environment, Action<string> log)
    {
        _java = java; _jar = jar; _dataDirectory = dataDirectory; Port = port; _environment = environment; _log = log;
        Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _health = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        _health.DefaultRequestHeaders.Add("X-Source-Host-Token", Token);
        _health.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public int Port { get; }
    public string BaseUrl => "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);
    /// <summary>Per-run secret the client sends as X-Source-Host-Token.</summary>
    public string Token { get; }
    public int Restarts { get { lock (_gate) return _restarts; } }
    public string LastError { get { lock (_gate) return _lastError; } }
    public bool Running { get { lock (_gate) return _process is { HasExited: false }; } }  // _process is never a disposed instance
    /// <summary>The X display the host's WebView uses: the app's own Xvfb, else the app's DISPLAY, or null (WebView unavailable).</summary>
    public string? Display
    {
        get
        {
            lock (_gate) if (_displayName is { Length: > 0 } own && _display is { HasExited: false }) return own;
            return System.Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } inherited ? inherited : null;
        }
    }

    /// <summary>Default deployment paths; AUTOSOURCARR_JAVA and AUTOSOURCARR_SOURCE_HOST_JAR override them.</summary>
    public static (string Java, string Jar) Locate(IReadOnlyDictionary<string, string?> environment)
    {
        var java = environment.GetValueOrDefault("AUTOSOURCARR_JAVA") is { Length: > 0 } configured ? configured
            : File.Exists("/opt/java/openjdk/bin/java") ? "/opt/java/openjdk/bin/java" : "java";
        var jar = environment.GetValueOrDefault("AUTOSOURCARR_SOURCE_HOST_JAR") is { Length: > 0 } path ? path : "/app/source-host/autosourcarr-source-host.jar";
        return (java, jar);
    }

    /// <summary>Starts the host and waits until it answers its health check (a cold JVM start takes a few seconds).</summary>
    public async Task StartAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!File.Exists(_jar)) throw new FileNotFoundException("The source host jar is missing.", _jar);
        try
        {
            CreateFolder(_dataDirectory);
            await StartDisplayAsync(cancellationToken).ConfigureAwait(false);
            Launch();
            await WaitHealthyAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lock (_gate) _lastError = error.Message;
            throw;
        }
        finally { StartWatching(); }
    }

    /// <summary>Keeps the host running (restarts with back-off), also after a failed first start.</summary>
    public void StartWatching()
    {
        lock (_gate) if (!_stop.IsCancellationRequested) _watch ??= Task.Run(WatchAsync);
    }

    /// <summary>
    /// Stops the running host so the watch loop starts it again at once with a freshly built environment (for example
    /// after the solver connections changed). Not counted as a crash: no back-off and no restart count. False when no
    /// host is running (the next start reads the new environment anyway).
    /// </summary>
    public bool Recycle(string reason)
    {
        Process? process;
        int id;
        lock (_gate)
        {
            if (_stop.IsCancellationRequested || _process is not { HasExited: false } running) return false;
            _recycling = true;
            process = running;
            id = running.Id;
        }
        _log("Restarting the source host: " + reason);
        try { process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { _ = error; } // exited meanwhile
        KillGroup(id);
        return true;
    }

    public async Task<bool> HealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _health.GetAsync(new Uri(BaseUrl + "/v1/health"), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private async Task WaitHealthyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(timeout);
        while (true)
        {
            try
            {
                if (await HealthyAsync(deadline.Token).ConfigureAwait(false)) { lock (_gate) _healthy = true; return; }
                lock (_gate)
                    if (_process is { HasExited: true } exited)
                        throw new InvalidOperationException("The source host stopped while starting (exit code " + exited.ExitCode.ToString(CultureInfo.InvariantCulture) + "). " + _startupError);
                await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested)
            { throw new TimeoutException("The source host did not answer its health check within " + timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s. " + _startupError); }
        }
    }

    /// <summary>
    /// The host's WebView (Chromium) needs an X display even when it renders off screen. Without DISPLAY the app starts
    /// its own Xvfb (when installed) and keeps it for the host's lifetime; -displayfd picks a free display number.
    /// </summary>
    private async Task StartDisplayAsync(CancellationToken cancellationToken)
    {
        if (_environment().ContainsKey("DISPLAY")) return;
        var xvfb = new[] { "/usr/bin/Xvfb", "/usr/local/bin/Xvfb" }.FirstOrDefault(File.Exists);
        if (xvfb is null) { _log("Xvfb is not installed; the source host WebView runs only if it finds a display."); return; }
        var start = new ProcessStartInfo(xvfb) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-displayfd", "1", "-screen", "0", "1280x1024x24", "-nolisten", "tcp" })
            start.ArgumentList.Add(argument);
        var display = Process.Start(start) ?? throw new InvalidOperationException("Xvfb could not be started.");
        display.ErrorDataReceived += (_, _) => { };
        display.BeginErrorReadLine();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(15));
        string? number;
        try { number = await display.StandardOutput.ReadLineAsync(wait.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { number = null; }
        if (string.IsNullOrWhiteSpace(number) || !int.TryParse(number.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            try { display.Kill(); } catch (InvalidOperationException) { }
            display.Dispose();
            _log("Xvfb did not report a display; the source host WebView will be unavailable.");
            return;
        }
        lock (_gate) { _display = display; _displayName = ":" + number.Trim(); }
        _log("Started Xvfb on display " + _displayName + " for the source host WebView.");
    }

    /// <summary>
    /// 0.5.3: Chromium's profile lock (<c>cache/kcef/Singleton*</c>) names the host and process that held it. A recreated
    /// container has a new hostname, so a lock left by the old one makes Chromium refuse the profile ("in use by another
    /// computer", exit 21) and the WebView never starts. Only this app runs a host on this data folder and no host runs
    /// while one is launched, so a lock found here is always stale.
    /// </summary>
    internal static void ClearChromiumLock(string dataDirectory, Action<string> log)
    {
        var profile = Path.Combine(dataDirectory, "cache", "kcef");
        var cleared = 0;
        foreach (var name in new[] { "SingletonLock", "SingletonSocket", "SingletonCookie" })
        {
            var path = Path.Combine(profile, name);
            try
            {
                // They are symbolic links (often dangling), so File.Exists cannot see them.
                if (new FileInfo(path).LinkTarget is null && !File.Exists(path)) continue;
                File.Delete(path);
                cleared++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log("Could not clear the WebView profile lock " + name + ": " + error.GetType().Name + ": " + error.Message);
            }
        }
        if (cleared > 0) log("Cleared a stale WebView (Chromium) profile lock left by an earlier start.");
    }

    private void Launch()
    {
        ClearChromiumLock(_dataDirectory, _log);
        var setsid = OperatingSystem.IsLinux() ? new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists) : null;
        var (sandbox, unsandboxed) = SandboxLauncher();
        var command = new List<string>();
        if (sandbox is not null) { command.Add(sandbox); command.AddRange(Sandbox!.Arguments(_dataDirectory, _java, _jar)); }
        command.AddRange([_java, "-jar", _jar]);
        var start = new ProcessStartInfo(setsid ?? command[0]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in setsid is null ? command.Skip(1) : command) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        var environment = _environment();
        foreach (var (key, value) in environment) start.Environment[key] = value;
        bool webView;
        lock (_gate) webView = _webViewOff is null && WebViewRequested(environment);
        start.Environment["WEBVIEW_ENABLED"] = webView ? "true" : "false";
        if (sandbox is not null)
        {
            // Inside the sandbox only the data folder and the temp folder are writable. Home stays in the data folder; temp
            // files stay on the container's own file system (0.5.4: Chromium crashed with them on a ZFS dataset).
            var home = Path.Combine(_dataDirectory, "home");
            CreateFolder(home);
            var temp = PrivateTemp(Sandbox!.TempDirectory, Path.Combine(_dataDirectory, "tmp"), _log);
            start.Environment["HOME"] = home; start.Environment["TMPDIR"] = temp;
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(home, ".cache"); start.Environment["XDG_CONFIG_HOME"] = Path.Combine(home, ".config");
        }
        lock (_gate) _sandbox = unsandboxed ?? new SourceHostSandboxState("unknown", 0, false, false, null, "no-proof");
        start.Environment["SOURCE_HOST_DATA"] = _dataDirectory;
        start.Environment["SOURCE_HOST_PORT"] = Port.ToString(CultureInfo.InvariantCulture);
        start.Environment["SOURCE_HOST_BIND"] = "127.0.0.1";
        start.Environment["SOURCE_HOST_TOKEN"] = Token;
        lock (_gate) if (_displayName.Length > 0) start.Environment["DISPLAY"] = _displayName;
        var process = Process.Start(start) ?? throw new InvalidOperationException("The source host could not be started.");
        // The sandbox proof counts only as the launcher's first stderr line, written before the JVM exists; the same
        // text later (or on stdout) comes from host or extension code and is ignored.
        var proofLine = new StrongBox<bool>(sandbox is not null);
        process.OutputDataReceived += (_, line) => Output(line.Data, proof: false);
        process.ErrorDataReceived += (_, line) =>
        {
            var first = proofLine.Value && line.Data is not null;
            if (line.Data is not null) proofLine.Value = false;
            Output(line.Data, first);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_gate)
        {
            if (_stop.IsCancellationRequested)
            {
                // Disposal began while this host was starting: it must not outlive the owner.
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose();
                throw new OperationCanceledException(_stop.Token);
            }
            _process = process; _group = setsid is not null; _healthy = false; _launchedWithWebView = webView;
        }
        _log("Source host started (process " + process.Id.ToString(CultureInfo.InvariantCulture) + ").");
        if (Limits is { } limits && OperatingSystem.IsLinux())
        {
            // The launcher execs the JVM in this process (or forks it right away); limits apply to it and its children.
            var applied = new AutoSourcarr.Core.Jobs.ProcessResourceLimits().Apply(process.Id, "source-host", limits);
            _log("Source host limits: " + applied.Mode + " (" + applied.Detail + ").");
        }
    }

    /// <summary>The launcher to use, or null with the reason the host runs unsandboxed.</summary>
    internal (string? Launcher, SourceHostSandboxState? Unsandboxed) SandboxLauncher()
    {
        static SourceHostSandboxState Without(string reason) => new("unsandboxed", 0, false, false, null, reason);
        if (Sandbox is not { } options) return (null, Without("sandbox-not-configured"));
        if (!OperatingSystem.IsLinux()) return (null, Without("platform"));
        if (!Path.IsPathFullyQualified(_java) || !Path.IsPathFullyQualified(_jar) || !Path.IsPathFullyQualified(_dataDirectory))
            return (null, Without("java-path-not-absolute"));
        try { if (!File.Exists(options.LauncherPath)) return (null, Without("sandbox-missing")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return (null, Without("sandbox-missing")); }
        return (options.LauncherPath, null);
    }

    /// <summary>
    /// 0.5.4: the host's temp folder: <paramref name="preferred"/> (on the container's own file system) as a private folder
    /// of this user, else <paramref name="fallback"/> in the data folder (logged). A link or a folder of another user is
    /// never used.
    /// </summary>
    internal static string PrivateTemp(string preferred, string fallback, Action<string> log)
    {
        try
        {
            var info = new DirectoryInfo(preferred);
            if (!info.Exists)
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(preferred);
                else Directory.CreateDirectory(preferred, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                info.Refresh();
            }
            if (info.LinkTarget is not null) throw new IOException("it is a link");
            if (!OperatingSystem.IsWindows())
            {
                // Only this user may use it (a folder made by someone else could hold planted files).
                var probe = Path.Combine(preferred, ".owner-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                File.SetUnixFileMode(preferred, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return preferred;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log("The source host temp folder " + preferred + " cannot be used (" + error.Message + "); using " + fallback + " instead. If the WebView then crashes, "
                + "set AUTOSOURCARR_SOURCE_HOST_TMP to a writable folder on the container's own file system.");
            CreateFolder(fallback);
            return fallback;
        }
    }

    /// <summary>Creates a host folder; a failure becomes the start error (reported, restart ladder) instead of an unhandled fault.</summary>
    private static void CreateFolder(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new InvalidOperationException("The source host folder " + Path.GetFileName(path) + " could not be created: " + error.Message, error); }
    }

    internal void Output(string? line, bool proof)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (line.StartsWith(SourceHostSandboxState.ProofPrefix, StringComparison.Ordinal))
        {
            if (proof && SourceHostSandboxState.Parse(line) is { } state)
            {
                lock (_gate) _sandbox = state;
                _log(state.Message());
            }
            else _log("Ignored a sandbox proof line that did not come from the launcher.");
            return;
        }
        // Only errors before the host answers count as its error: a failing source call logs exceptions too.
        if (line.Contains(" ERROR ", StringComparison.Ordinal) || line.Contains("Exception", StringComparison.Ordinal))
            lock (_gate) if (!_healthy) _startupError = line.Length > 500 ? line[..500] : line;
        if (!line.Contains(" DEBUG ", StringComparison.Ordinal) && !line.Contains(" TRACE ", StringComparison.Ordinal)) _log(line);
    }

    private async Task WatchAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            Process? current;
            lock (_gate) current = _process;
            if (current is null)
            {
                // The previous start failed (no process): try again on the back-off ladder.
                var wait = RestartDelays[Math.Min(failures++, RestartDelays.Length - 1)];
                try { await Task.Delay(wait, _stop.Token).ConfigureAwait(false); Launch(); _ = ClearErrorWhenHealthyAsync(); }
                catch (OperationCanceledException) { return; }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                { lock (_gate) _lastError = "The source host could not be started: " + error.Message; _log(LastError); }
                continue;
            }
            var process = current;
            var started = DateTimeOffset.UtcNow;
            try { await process.WaitForExitAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (_stop.IsCancellationRequested) return;
            KillGroup(process.Id);
            bool recycled;
            lock (_gate) { recycled = _recycling; _recycling = false; }
            if (DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(10)) failures = 0;
            TimeSpan delay;
            if (recycled)
            {
                // Asked for (Recycle): start again at once, without counting it as a crash.
                delay = TimeSpan.FromMilliseconds(500);
                _log("The source host stopped for its restart; starting it again.");
            }
            else
            {
                delay = RestartDelays[Math.Min(failures, RestartDelays.Length - 1)];
                failures++;
                string? turnOff; bool hadWebView;
                lock (_gate)
                {
                    hadWebView = _launchedWithWebView;
                    _restarts++; _lastError = "The source host exited with code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ".";
                    (_webViewCrashes, turnOff) = WebViewCrash(process.ExitCode, DateTimeOffset.UtcNow - started, _launchedWithWebView, _webViewCrashes);
                    turnOff = WithSignalHint(turnOff, _sandbox.SignalsScoped);
                    if (turnOff is not null) _webViewOff = turnOff;
                }
                if (process.ExitCode > 128 && hadWebView) LogChromiumTail();
                if (turnOff is not null)
                {
                    // The next start runs without the WebView: no reason to wait on the back-off ladder for it.
                    delay = RestartDelays[0]; failures = 0;
                    _log(turnOff);
                    try { WebViewTurnedOff?.Invoke(turnOff); }
                    // An outside callback must never stop the watch loop (the host would not be restarted).
                    catch (Exception error) when (error is not OperationCanceledException) { _log("Could not record the WebView notice: " + error.GetType().Name + ": " + error.Message); }
                }
                _log(LastError + " Starting it again in " + delay.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s.");
            }
            lock (_gate) if (ReferenceEquals(_process, process)) _process = null;
            process.Dispose();
            try
            {
                await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
                Launch();
                _ = ClearErrorWhenHealthyAsync();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Keep trying on the ladder: the next loop pass sees no process and starts one.
                lock (_gate) _lastError = "The source host could not be started again: " + error.Message;
                _log(LastError);
            }
        }
    }

    /// <summary>0.5.1: after a signal death with the WebView on, the end of Chromium's own log (written by the host).</summary>
    private void LogChromiumTail()
    {
        var file = Path.Combine(_dataDirectory, "logs", "chromium.log");
        try
        {
            if (!File.Exists(file)) { _log("Chromium wrote no log before the source host stopped."); return; }
            var lines = File.ReadLines(file).TakeLast(15).Where(line => line.Length > 0).ToList();
            foreach (var line in lines) _log("Chromium log: " + (line.Length > 500 ? line[..500] : line));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _log("Chromium's log could not be read: " + error.Message); }
    }

    /// <summary>A restarted host that answers again clears the exit message from the status.</summary>
    private async Task ClearErrorWhenHealthyAsync()
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            while (!await HealthyAsync(deadline.Token).ConfigureAwait(false)) await Task.Delay(1000, deadline.Token).ConfigureAwait(false);
            lock (_gate) { _healthy = true; _lastError = ""; }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>setsid made the host a process-group leader (group id = its pid): stop what is left of the group.</summary>
    private void KillGroup(int processId)
    {
        bool group;
        lock (_gate) group = _group;
        if (group && OperatingSystem.IsLinux() && processId > 1) _ = KillSignal(-processId, 9);
    }

    /// <summary>
    /// Hosted mode has no native launcher to do this: mark the app not dumpable, so code running in the source host
    /// (same user) cannot read the app's memory or /proc/&lt;pid&gt;/environ (secrets in variables). Phase 13: the host also runs
    /// in the Landlock sandbox (<see cref="Sandbox"/>: own data folder only, no signals to the app) and, where the image
    /// grants the launcher CAP_SETUID/CAP_SETGID, as its own low-rights user.
    /// </summary>
    public static void ProtectParentProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (Prctl(4 /* PR_SET_DUMPABLE */, 0, 0, 0, 0) != 0)
            throw new InvalidOperationException("Could not mark the process not dumpable (prctl error " + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture) + ").");
    }

    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static partial int Prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int KillSignal(int processId, int signal);

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        Task? watch;
        lock (_gate) watch = _watch;
        // The watch loop stops at its next await and never launches again once _stop is cancelled (Launch checks it).
        if (watch is not null) await watch.ConfigureAwait(false);
        Process? process;
        lock (_gate) { process = _process; _process = null; }
        if (process is { HasExited: false })
        {
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            KillGroup(process.Id);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(wait.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        process?.Dispose();
        Process? display;
        lock (_gate) display = _display;
        if (display is not null)
        {
            try { if (!display.HasExited) display.Kill(); } catch (InvalidOperationException) { }
            display.Dispose();
        }
        _health.Dispose();
        _stop.Dispose();
    }
}
