using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
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
        Directory.CreateDirectory(_dataDirectory);
        await StartDisplayAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

    private void Launch()
    {
        var setsid = OperatingSystem.IsLinux() ? new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists) : null;
        var start = new ProcessStartInfo(setsid ?? _java) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        if (setsid is not null) start.ArgumentList.Add(_java);
        start.ArgumentList.Add("-jar");
        start.ArgumentList.Add(_jar);
        start.Environment.Clear();
        foreach (var (key, value) in _environment()) start.Environment[key] = value;
        start.Environment["SOURCE_HOST_DATA"] = _dataDirectory;
        start.Environment["SOURCE_HOST_PORT"] = Port.ToString(CultureInfo.InvariantCulture);
        start.Environment["SOURCE_HOST_BIND"] = "127.0.0.1";
        start.Environment["SOURCE_HOST_TOKEN"] = Token;
        lock (_gate) if (_displayName.Length > 0) start.Environment["DISPLAY"] = _displayName;
        var process = Process.Start(start) ?? throw new InvalidOperationException("The source host could not be started.");
        process.OutputDataReceived += (_, line) => Output(line.Data);
        process.ErrorDataReceived += (_, line) => Output(line.Data);
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
            _process = process; _group = setsid is not null; _healthy = false;
        }
        _log("Source host started (process " + process.Id.ToString(CultureInfo.InvariantCulture) + ").");
    }

    private void Output(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
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
                lock (_gate) { _restarts++; _lastError = "The source host exited with code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + "."; }
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
    /// (same user) cannot read the app's memory or /proc/&lt;pid&gt;/environ (secrets in variables). A separate low-rights user for
    /// the host is planned for conversion phase 13 (the new image).
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
