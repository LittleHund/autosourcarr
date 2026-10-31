using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace AutoSourcarr.Core.Sources.Runtime;

/// <summary>
/// Phase 4 integrated source engine: the app starts the AutoSourcarr source host (one JVM, <c>java -jar</c>) itself
/// instead of Suwayomi through the native launcher. The host listens on 127.0.0.1 only and every request carries a
/// token made for this run. A host that exits is started again after 2 s, 5 s, 15 s, 30 s and then every 60 s; the
/// ladder resets after ten minutes of uptime. The host keeps its own state (extensions, ids) in its data folder.
/// </summary>
public sealed class SourceHostProcess : IAsyncDisposable
{
    private static readonly TimeSpan[] RestartDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    private readonly object _gate = new();
    private readonly string _java, _jar, _dataDirectory;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _health;
    private Process? _process;
    private Task? _watch;
    private int _restarts;
    private string _lastError = "";

    public SourceHostProcess(string java, string jar, string dataDirectory, int port, IReadOnlyDictionary<string, string> environment, Action<string> log)
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
    public bool Running { get { lock (_gate) return _process is { HasExited: false }; } }

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
        Launch();
        await WaitHealthyAsync(timeout, cancellationToken).ConfigureAwait(false);
        lock (_gate) _watch ??= Task.Run(WatchAsync);
    }

    public async Task<bool> HealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _health.GetAsync(new Uri(BaseUrl + "/v1/health"), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested) { return false; }
    }

    private async Task WaitHealthyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(timeout);
        while (true)
        {
            if (await HealthyAsync(deadline.Token).ConfigureAwait(false)) return;
            lock (_gate)
                if (_process is { HasExited: true } exited)
                    throw new InvalidOperationException("The source host stopped while starting (exit code " + exited.ExitCode.ToString(CultureInfo.InvariantCulture) + "). " + _lastError);
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested)
            { throw new TimeoutException("The source host did not answer its health check within " + timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s."); }
        }
    }

    private void Launch()
    {
        var start = new ProcessStartInfo(_java) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add("-jar");
        start.ArgumentList.Add(_jar);
        start.Environment.Clear();
        foreach (var (key, value) in _environment) start.Environment[key] = value;
        start.Environment["SOURCE_HOST_DATA"] = _dataDirectory;
        start.Environment["SOURCE_HOST_PORT"] = Port.ToString(CultureInfo.InvariantCulture);
        start.Environment["SOURCE_HOST_BIND"] = "127.0.0.1";
        start.Environment["SOURCE_HOST_TOKEN"] = Token;
        var process = Process.Start(start) ?? throw new InvalidOperationException("The source host could not be started.");
        process.OutputDataReceived += (_, line) => Output(line.Data);
        process.ErrorDataReceived += (_, line) => Output(line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_gate) _process = process;
        _log("Source host started (process " + process.Id.ToString(CultureInfo.InvariantCulture) + ").");
    }

    private void Output(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (line.Contains(" ERROR ", StringComparison.Ordinal) || line.Contains("Exception", StringComparison.Ordinal))
            lock (_gate) _lastError = line.Length > 500 ? line[..500] : line;
        if (!line.Contains(" DEBUG ", StringComparison.Ordinal) && !line.Contains(" TRACE ", StringComparison.Ordinal)) _log(line);
    }

    private async Task WatchAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            Process process;
            lock (_gate) process = _process!;
            var started = DateTimeOffset.UtcNow;
            try { await process.WaitForExitAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (_stop.IsCancellationRequested) return;
            if (DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(10)) failures = 0;
            var delay = RestartDelays[Math.Min(failures, RestartDelays.Length - 1)];
            failures++;
            lock (_gate) { _restarts++; _lastError = "The source host exited with code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + "."; }
            _log(LastError + " Starting it again in " + delay.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s.");
            process.Dispose();
            try
            {
                await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
                Launch();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                lock (_gate) _lastError = "The source host could not be started again: " + error.Message;
                _log(LastError);
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        Process? process;
        Task? watch;
        lock (_gate) { process = _process; watch = _watch; }
        if (process is { HasExited: false })
        {
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(wait.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (watch is not null) await watch.ConfigureAwait(false);
        process?.Dispose();
        _health.Dispose();
        _stop.Dispose();
    }
}
