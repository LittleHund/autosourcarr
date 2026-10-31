using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources.Runtime;
using AutoSourcarr.Core.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests;

public static class EngineCompatibilityApiTests
{
    private const string Path = "/api/admin/engine/compatibility";
    public static void Register(TestSuite suite)
    {
        suite.Add("engine compatibility API: admin authorization and mandatory reader separation", () => Authorization().GetAwaiter().GetResult());
        suite.Add("engine compatibility API: actual source/main/default scalar targets persist native API observations", () => Targets().GetAwaiter().GetResult());
        suite.Add("engine compatibility API: invalid targets return422 before native admission", () => InvalidTargets().GetAwaiter().GetResult());
        suite.Add("engine compatibility API: unconfigured targets return explicit safe incomplete409", () => Missing().GetAwaiter().GetResult());
        suite.Add("engine compatibility API: incomplete and typed local deferral errors expose only exception type", () => SafeErrors().GetAwaiter().GetResult());
        suite.Add("engine compatibility API: cancelled shared-admin waiter never releases another owner's lease", () => Waiting().GetAwaiter().GetResult());
        suite.Add("engine compatibility API: caller cancellation retains admin lane until physical about I/O joins", () => Cancellation().GetAwaiter().GetResult());
        suite.Add("engine compatibility API: owned service shutdown propagates cancellation after physical GraphQL joins", () => Shutdown().GetAwaiter().GetResult());
        suite.Add("engine compatibility API: configured targets serialize on the same actual admin lane", () => Serialized().GetAwaiter().GetResult());
    }

    private static async Task Authorization()
    {
        await using var f = new Fixture();
        foreach (var key in new string?[] { null, "reader", "invalid" })
        { using var response = await f.Send(key: key); Check.Equal(HttpStatusCode.Unauthorized, response.StatusCode); }
        using (var response = await f.Send(path: "/api/reader/v1/admin/engine/compatibility", key: "reader")) Check.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        using (var response = await f.Send(path: "/api/reader/v1/admin/engine/compatibility", key: "reader", method: HttpMethod.Get)) Check.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using (var response = await f.Send(method: HttpMethod.Get)) Check.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Check.Equal(0, f.Source.AboutCalls + f.Main.AboutCalls);
        using (var response = await f.Send()) Check.Equal(HttpStatusCode.OK, response.StatusCode);
        Check.Equal(1, f.Source.AboutCalls); Check.Equal(0, f.Unexpected);
    }

    private static async Task Targets()
    {
        await using var f = new Fixture(); var sources = f.SourceFlags();
        foreach (var (query, target, version) in new[] { ("", "source", "2.3.2243"), ("?target=main", "main", "2.4.2366"), ("?target=source", "source", "2.3.2243"), ("?target=source&target=main", "main", "2.4.2366") })
        {
            using var response = await f.Send(path: Path + query); Check.Equal(HttpStatusCode.OK, response.StatusCode); var body = await Body(response);
            Check.Equal(target, Text(body["engine"])); Check.Equal(version, Text(body["version"])); Check.True(body["contract_ok"]!.GetValue<bool>());
            Check.True(!body["native_qualified"]!.GetValue<bool>()); Check.Equal("no-store", response.Headers.CacheControl!.ToString());
            Equal(body, f.Settings.Get(SourceEngineCapabilityContract.SettingKey)); Check.True(!f.Source.Contract.Status()["pin_promoted"]!.GetValue<bool>());
        }
        f.Main.Schema = new JsonObject { ["__schema"] = new JsonObject { ["mutationType"] = new JsonObject { ["fields"] = new JsonArray() } } };
        using (var response = await f.Send(path: Path + "?target=main"))
        { Check.Equal(HttpStatusCode.OK, response.StatusCode); Check.True(!(await Body(response))["contract_ok"]!.GetValue<bool>()); }
        Check.Equal(2, f.Source.AboutCalls); Check.Equal(3, f.Main.AboutCalls); Check.Equal(sources, f.SourceFlags()); Check.Equal(0, f.Unexpected);
    }

    private static async Task InvalidTargets()
    {
        await using var f = new Fixture(); await f.Admin.WaitAsync();
        try
        {
            foreach (var query in new[] { "?target=", "?target=SOURCE", "?target=other", "?target=synthetic-private-target" })
            {
                using var response = await f.Send(path: Path + query).WaitAsync(TimeSpan.FromSeconds(2)); Check.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                Check.True(!(await response.Content.ReadAsStringAsync()).Contains("synthetic-private-target", StringComparison.Ordinal));
            }
            Check.Equal(0, f.Admin.CurrentCount); Check.Equal(0, f.Source.AboutCalls + f.Main.AboutCalls);
        }
        finally { f.Admin.Release(); }
    }

    private static async Task Missing()
    {
        await using var f = new Fixture(mapSource: false, mapMain: false); var sources = f.SourceFlags();
        foreach (var query in new[] { "", "?target=source", "?target=main" })
        {
            using var response = await f.Send(path: Path + query); Check.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Equal(new JsonObject { ["detail"] = "Engine compatibility check incomplete: InvalidOperationException" }, await Body(response));
        }
        Check.Equal(1, f.Admin.CurrentCount); Check.True(f.Settings.Get(SourceEngineCapabilityContract.SettingKey) is null); Check.Equal(sources, f.SourceFlags());
        Check.Equal(0, f.Source.AboutCalls + f.Main.AboutCalls); Check.Equal(0, f.Unexpected);
    }

    private static async Task SafeErrors()
    {
        await using (var f = new Fixture())
        {
            var sources = f.SourceFlags(); var saved = new JsonObject { ["retained"] = true }; f.Settings.Set(SourceEngineCapabilityContract.SettingKey, saved);
            f.Source.About = new JsonObject { ["version"] = "" };
            using (var response = await f.Send()) await Incomplete(response, nameof(SourceEngineCapabilityException));
            f.Source.About = new JsonObject { ["version"] = "2.3.2243" }; f.Source.GraphError = "synthetic-upstream-account-private-password";
            using (var response = await f.Send())
            {
                await Incomplete(response, nameof(SourceEngineCapabilityException)); Check.True(!(await response.Content.ReadAsStringAsync()).Contains("synthetic-upstream-account-private-password", StringComparison.Ordinal));
            }
            Equal(saved, f.Settings.Get(SourceEngineCapabilityContract.SettingKey)); Check.Equal(sources, f.SourceFlags()); Check.Equal(0, f.Unexpected);
        }
        await using (var deferred = new Fixture(deferredSource: true))
        {
            var sources = deferred.SourceFlags(); using var response = await deferred.Send();
            await Incomplete(response, nameof(SourceEngineUnavailableException)); Check.Equal(0, deferred.Source.GraphCalls);
            Check.True(deferred.Settings.Get(SourceEngineCapabilityContract.SettingKey) is null); Check.Equal(sources, deferred.SourceFlags()); Check.Equal(0, deferred.Unexpected);
        }
    }

    private static async Task Waiting()
    {
        await using var f = new Fixture(); using var cancel = new CancellationTokenSource(); await f.Admin.WaitAsync();
        var arrived = f.CaptureNext(); var request = f.Send(cancellationToken: cancel.Token); RequestTrace? trace = null;
        try
        {
            trace = await arrived.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel(); await Aborted(request); await trace.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check.True(trace.Error is OperationCanceledException); Check.Equal(0, f.Admin.CurrentCount); Check.Equal(0, f.Source.AboutCalls); Check.Equal(0, f.Unexpected);
        }
        finally
        {
            cancel.Cancel();
            try { await Ignore(request); if (trace is not null) await trace.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { f.Admin.Release(); }
        }
    }

    private static async Task Cancellation()
    {
        await using var f = new Fixture(); var entered = Signal(); var release = Signal(); var cancelled = Signal(); var finished = 0; using var cancel = new CancellationTokenSource();
        f.Source.BeforeAbout = async token =>
        { using var registration = token.Register(() => cancelled.TrySetResult()); entered.TrySetResult(); await release.Task; Interlocked.Increment(ref finished); };
        var arrived = f.CaptureNext(); var request = f.Send(cancellationToken: cancel.Token); RequestTrace? trace = null;
        try
        {
            trace = await arrived.WaitAsync(TimeSpan.FromSeconds(2)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel(); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check.Equal(0, finished); Check.Equal(0, f.Admin.CurrentCount); Check.True(!trace.Finished.Task.IsCompleted);
            release.TrySetResult(); await Aborted(request); await trace.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check.Equal(1, finished); Check.True(trace.Error is OperationCanceledException); Check.Equal(1, f.Admin.CurrentCount);
            Check.True(f.Settings.Get(SourceEngineCapabilityContract.SettingKey) is null); Check.Equal(0, f.Unexpected);
        }
        finally { release.TrySetResult(); cancel.Cancel(); await Ignore(request); if (trace is not null) await trace.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    private static async Task Shutdown()
    {
        await using var f = new Fixture(); var entered = Signal(); var release = Signal(); var cancelled = Signal(); var finished = 0;
        f.Source.BeforeGraph = async token =>
        { using var registration = token.Register(() => cancelled.TrySetResult()); entered.TrySetResult(); await release.Task; Interlocked.Increment(ref finished); };
        var arrived = f.CaptureNext(); var request = f.Send(); RequestTrace? trace = null; Task? stop = null;
        try
        {
            trace = await arrived.WaitAsync(TimeSpan.FromSeconds(2)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); stop = f.Source.Contract.StopAsync(); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check.True(!stop.IsCompleted && !trace.Finished.Task.IsCompleted); Check.Equal(0, f.Admin.CurrentCount); Check.Equal(0, finished);
            release.TrySetResult(); await Aborted(request); await trace.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2)); await stop.WaitAsync(TimeSpan.FromSeconds(2));
            Check.True(trace.Error is OperationCanceledException && trace.Aborted); Check.Equal(1, finished); Check.Equal(1, f.Admin.CurrentCount);
            Check.True(f.Settings.Get(SourceEngineCapabilityContract.SettingKey) is null); Check.Equal(0, f.Unexpected);
        }
        finally { release.TrySetResult(); await Ignore(request); if (trace is not null) await trace.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2)); if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    private static async Task Serialized()
    {
        await using var f = new Fixture(); var entered = Signal(); var release = Signal();
        f.Main.BeforeAbout = async _ => { entered.TrySetResult(); await release.Task; };
        var first = f.Send(path: Path + "?target=main"); Task<HttpResponseMessage>? second = null; RequestTrace? trace = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); var arrived = f.CaptureNext(); second = f.Send(); trace = await arrived.WaitAsync(TimeSpan.FromSeconds(2));
            Check.True(!trace.Finished.Task.IsCompleted); Check.Equal(0, f.Source.AboutCalls); Check.Equal(0, f.Admin.CurrentCount);
            release.TrySetResult(); using var main = await first; using var source = await second; Check.Equal(HttpStatusCode.OK, main.StatusCode); Check.Equal(HttpStatusCode.OK, source.StatusCode);
            Check.Equal("source", Text(f.Settings.Get(SourceEngineCapabilityContract.SettingKey)?["engine"])); Check.Equal(1, f.Admin.CurrentCount);
        }
        finally { release.TrySetResult(); await Ignore(first); if (second is not null) await Ignore(second); if (trace is not null) await trace.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    private sealed class RequestTrace
    {
        public TaskCompletionSource Finished { get; } = Signal(); public Exception? Error { get; set; } public bool Aborted { get; set; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Sandbox _temp = new(); private readonly SqliteDatabase _database; private readonly WholeTaskCoordinator _lanes;
        private readonly IHost _host; private readonly HttpClient _client; private TaskCompletionSource<RequestTrace>? _next;
        public SemaphoreSlim Admin { get; } = new(1, 1); public SettingsRepository Settings { get; }
        public Engine Source { get; } public Engine Main { get; } public int Unexpected;
        public Fixture(bool mapSource = true, bool mapMain = true, bool deferredSource = false)
        {
            var settings = new AutoSourcarrSettings { DataDir = _temp.Path, AdminToken = "admin", AutosourcarrReaderApiToken = "reader" };
            var privacy = new SupportPrivacy(); var tasks = new TaskRegistry(); _lanes = new(settings, tasks); _database = new(settings); new SchemaMigrator(_database).Initialize(); Settings = new(_database);
            _database.Write(DatabaseFile.Catalog, c => c.Execute("INSERT INTO source_config(source_id,name,last_seen,available,enabled_search,enabled_chapters) VALUES('synthetic-source','Fixture','2026-10-21T00:00:00+00:00',1,0,1)"));
            Source = new(Settings, privacy, "source", "http://127.0.0.1:4567", "2.3.2243", deferredSource ? new DeferredRuntime() : null);
            Main = new(Settings, privacy, "main", "http://main-engine.invalid", "2.4.2366");
            _host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => services.AddRouting()).Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    var trace = new RequestTrace(); Interlocked.Exchange(ref _next, null)?.TrySetResult(trace);
                    try { await next(context); } catch (Exception error) { trace.Error = error; throw; }
                    finally { trace.Aborted = context.RequestAborted.IsCancellationRequested; trace.Finished.TrySetResult(); }
                });
                app.UseAutoSourcarrHttp("0.4.3-migration", () => settings, (_, _) => { Interlocked.Increment(ref Unexpected); return "unexpected"; }, new RequestAdmissionGate(), _lanes, tasks, privacy, _ => { });
                app.UseEndpoints(routes => routes.MapEngineCompatibility(mapSource ? Source.Contract : null, mapMain ? Main.Contract : null, () => settings, Admin));
            })).Build();
            _host.Start(); _client = _host.GetTestClient();
        }
        public Task<RequestTrace> CaptureNext()
        { var signal = new TaskCompletionSource<RequestTrace>(TaskCreationOptions.RunContinuationsAsynchronously); Check.True(Interlocked.CompareExchange(ref _next, signal, null) is null); return signal.Task; }
        public async Task<HttpResponseMessage> Send(string path = Path, string? key = "admin", HttpMethod? method = null, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path);
            if (key is not null) request.Headers.Add(key == "reader" ? "X-AutoSourcarr-Api-Key" : "X-Admin-Token", key);
            return await _client.SendAsync(request, cancellationToken);
        }
        public string SourceFlags() => _database.Read(DatabaseFile.Catalog, c => JsonSerializer.Serialize(c.Query("SELECT source_id,available,enabled_search,enabled_chapters FROM source_config ORDER BY source_id")));
        public async ValueTask DisposeAsync()
        {
            try { await _host.StopAsync(); }
            finally
            {
                _client.Dispose(); _host.Dispose();
                try { await Source.DisposeAsync(); }
                finally
                {
                    try { await Main.DisposeAsync(); }
                    finally
                    {
                        try { await _lanes.DisposeAsync(); }
                        finally { Admin.Dispose(); _database.Dispose(); _temp.Dispose(); }
                    }
                }
            }
        }
    }
    private sealed class Engine : IAsyncDisposable
    {
        private readonly SuwayomiClient _client; public SourceEngineCapabilityContract Contract { get; }
        public int AboutCalls, GraphCalls; public JsonNode? About { get; set; } public JsonObject Schema { get; set; } = CapabilitySchema(); public string? GraphError;
        public Func<CancellationToken, Task>? BeforeAbout, BeforeGraph;
        public Engine(SettingsRepository settings, SupportPrivacy privacy, string target, string address, string version, IManagedSourceRuntime? runtime = null)
        {
            About = new JsonObject { ["version"] = version };
            _client = new(new AutoSourcarrSettings(), address, runtime: runtime, privacy: privacy, handler: new Handler(async (_, token) =>
            {
                Interlocked.Increment(ref GraphCalls); if (BeforeGraph is { } before) await before(token);
                return Response(GraphError is { } error ? new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = error }) } : new JsonObject { ["data"] = Schema.DeepClone() });
            }));
            var identity = target == "source" ? SourceEngineCapabilityIdentity.IntegratedSource() : SourceEngineCapabilityIdentity.ConfiguredExternal(address);
            Contract = new(settings, _client, identity, privacy, target, trustedTestAboutTransport: new Handler(async (_, token) =>
            { Interlocked.Increment(ref AboutCalls); if (BeforeAbout is { } before) await before(token); return Response(About); }));
        }
        public async ValueTask DisposeAsync() { try { await Contract.DisposeAsync(); } finally { await _client.DisposeAsync(); } }
    }
    private sealed class DeferredRuntime : IManagedSourceRuntime
    { public bool BrowserMaintenance => false; public Task EnsureAvailableAsync(string requestPath, CancellationToken cancellationToken) => Task.FromException(new SourceEngineUnavailableException("synthetic-private-local-runtime-detail")); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private static JsonObject CapabilitySchema() => new() { ["__schema"] = new JsonObject { ["mutationType"] = new JsonObject { ["fields"] = new JsonArray(new[] { "updateManga", "enqueueChapterDownloads", "fetchMangaAndChapters", "fetchChapterPages" }.Select(name => (JsonNode)new JsonObject { ["name"] = name }).ToArray()) } } };
    private static HttpResponseMessage Response(JsonNode? node) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(node)) };
    private static async Task<JsonObject> Body(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    private static async Task Incomplete(HttpResponseMessage response, string type)
    { Check.Equal(HttpStatusCode.Conflict, response.StatusCode); Equal(new JsonObject { ["detail"] = "Engine compatibility check incomplete: " + type }, await Body(response)); }
    private static async Task Aborted(Task<HttpResponseMessage> task)
    {
        try { using var response = await task.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("Cancelled observation returned HTTP " + response.StatusCode); }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }
        catch (IOException) { }
    }
    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? "";
    private static void Equal(JsonNode? expected, JsonNode? actual) => Check.JsonEqual(JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(actual));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Ignore(Task task) { try { await task.WaitAsync(TimeSpan.FromSeconds(3)); } catch { } }
}
