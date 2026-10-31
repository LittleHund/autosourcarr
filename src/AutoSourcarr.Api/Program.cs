using System.Collections;
using System.Runtime.InteropServices;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoSourcarr.Api;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        StopSignals? signals = null;
        try
        {
            // Ordinary exec resets dumpability. Do this before environment,
            // canonical configuration, host defaults or diagnostic construction.
            NativeSourceOwnerConnection.RequirePostExecPrivacyBeforeConfiguration();
            await using var processSignals = signals = new StopSignals();
            if (args.Length != 0) throw new InvalidOperationException("Unexpected application arguments.");
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (DictionaryEntry pair in Environment.GetEnvironmentVariables())
                environment[(string)pair.Key] = pair.Value?.ToString();
            // Cancellation still joins the finite configuration worker before
            // leaving startup. It never abandons filesystem recovery work.
            AutoSourcarr.Core.Settings.StartupConfigurationSnapshot startup;
            await using (var startupIo = new AutoSourcarr.Core.Concurrency.BoundedWorkScheduler(1, 0))
                startup = await startupIo.RunAsync(() => StartupConfigurationLoader.Load(Environment.CurrentDirectory, environment,
                    bundledImage: true), processSignals.Token).ConfigureAwait(false);
            ApplyTimeZone(startup.Settings, environment);
            await using var runtime = await ApplicationRuntime.CreateNativeAsync(startup, environment, processSignals.Token).ConfigureAwait(false);
            var host = new HostBuilder().UseConsoleLifetime()
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices(services => services.AddRouting())
                .ConfigureWebHost(web => web.UseKestrel(options => { options.AddServerHeader = false; options.ListenAnyIP(8282); })
                    .Configure(app => runtime.ConfigureHttp(app, "/app/static"))).Build();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            // The cached task remains owned and is joined by runtime disposal even
            // if the framework's server shutdown deadline expires first.
            using var stopping = lifetime.ApplicationStopping.Register(() => _ = runtime.BeginHttpStopAsync());
            using var watchStop = new CancellationTokenSource();
            var fatal = StopOnFatalAsync(runtime, lifetime, watchStop.Token);
            try { await host.RunAsync(processSignals.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (processSignals.Token.IsCancellationRequested) { }
            finally
            {
                await watchStop.CancelAsync().ConfigureAwait(false);
                await fatal.ConfigureAwait(false);
            }
            return runtime.FatalFailure.IsCompleted ? 78 : 0;
        }
        catch (OperationCanceledException) when (signals?.Token.IsCancellationRequested == true) { return 0; }
        catch (Exception error)
        {
            // Before the shared privacy registry exists, raw messages/stacks and
            // configuration paths must not enter console logs.
            Console.Error.WriteLine("AutoSourcarr startup or shutdown failed (" + error.GetType().Name + ").");
            return 78;
        }
    }
    internal static void ApplyTimeZone(AutoSourcarrSettings settings, IDictionary<string, string?> environment)
    {
        // The released initializer overwrites inherited TZ with the effective
        // setting. Keep both CLR local-time caching and the source launch copy current.
        Environment.SetEnvironmentVariable("TZ", settings.Tz);
        TimeZoneInfo.ClearCachedData();
        environment["TZ"] = settings.Tz;
    }
    /// <summary>Own early startup signals too, before Generic Host is constructed.</summary>
    private sealed class StopSignals : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly List<PosixSignalRegistration> _signals = [];
        private Task? _cancel;
        private bool _closed;
        internal CancellationToken Token { get; }
        internal StopSignals()
        {
            Token = _stop.Token;
            try
            {
                foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT })
                    _signals.Add(PosixSignalRegistration.Create(signal, context =>
                    { context.Cancel = true; lock (_gate) if (!_closed) _cancel ??= _stop.CancelAsync(); }));
            }
            catch
            {
                lock (_gate) _closed = true;
                foreach (var registration in _signals) registration.Dispose();
                _stop.Dispose(); throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            Task? cancel; lock (_gate) { _closed = true; cancel = _cancel; }
            foreach (var registration in _signals) registration.Dispose();
            try { if (cancel is not null) await cancel.ConfigureAwait(false); }
            finally { _stop.Dispose(); }
        }
    }
    private static async Task StopOnFatalAsync(ApplicationRuntime runtime, IHostApplicationLifetime lifetime, CancellationToken token)
    {
        try
        {
            var failure = await runtime.FatalFailure.WaitAsync(token).ConfigureAwait(false);
            runtime.Logs.Record("Application", "Owned runtime lifetime failed (" + failure.GetType().Name + ").");
            lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
