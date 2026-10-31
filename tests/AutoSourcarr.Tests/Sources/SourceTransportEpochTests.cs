using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Sources.Controls;
using AutoSourcarr.Core.Sources.Inventory;
using AutoSourcarr.Core.Sources.Runtime;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Tests;

public static class SourceTransportEpochTests
{
    private const string Origin = "http://engine.test:4567";
    public static void Register(TestSuite suite)
    {
        suite.Add("transport epochs: pause fences admission and fresh pools cannot revive old sessions", () => Fence().GetAwaiter().GetResult());
        suite.Add("transport epochs: late headers remain physically owned until close", () => LateHeaders().GetAwaiter().GetResult());
        suite.Add("transport epochs: direct external streams survive engine pause", () => External().GetAwaiter().GetResult());
        suite.Add("transport epochs: external engine redirect uses its captured pool or defers", () => ExternalHop().GetAwaiter().GetResult());
        suite.Add("transport epochs: schema fallback cannot continue into a new pool", () => Composite().GetAwaiter().GetResult());
        suite.Add("transport epochs: cancellation callback faults forbid resume after physical drain", () => CallbackFailure().GetAwaiter().GetResult());
        suite.Add("transport epochs: terminal disposal joins a staged factory and remains terminal", () => StagedFactory().GetAwaiter().GetResult());
        suite.Add("transport epochs: legacy injected handler explicitly refuses restart control", () => OneShot().GetAwaiter().GetResult());
        suite.Add("transport epochs: concurrent resume joins one actual factory", () => ConcurrentResume().GetAwaiter().GetResult());
        suite.Add("transport epochs: pause joins physical stream creation read and close", () => BodyOwnership().GetAwaiter().GetResult());
        suite.Add("transport epochs: late body close failure is retained and forbids a clean drain", () => LateBodyFailure().GetAwaiter().GetResult());
        suite.Add("transport epochs: about and introspection share one epoch and only complete reports persist", () => Capability().GetAwaiter().GetResult());
        suite.Add("transport epochs: inventory physical work and retained captures cannot cross retirement", () => Inventory().GetAwaiter().GetResult());
        suite.Add("transport epochs: controls cancel old lane waiters and never mutate a replacement", () => Controls().GetAwaiter().GetResult());
        suite.Add("transport epochs: cached reader pages and external images remain available while paused", () => ReaderCache().GetAwaiter().GetResult());
        suite.Add("transport epochs: native reader mapping cannot fall through into a replacement", () => ReaderMapping().GetAwaiter().GetResult());
        suite.Add("transport epochs: a whole source health sample is one logical operation", () => HealthSample().GetAwaiter().GetResult());
        suite.Add("transport epochs: library pagination and install recovery stop with their original epoch", () => CompositeBoundaries().GetAwaiter().GetResult());
        suite.Add("transport epochs: clean factory failure permits explicit retry without reusing pools", () => FactoryFailure().GetAwaiter().GetResult());
        suite.Add("transport epochs: caller cancellation closes an escaped unopened response", () => EscapedCancellation().GetAwaiter().GetResult());
        suite.Add("transport epochs: an engine hop keeps subsequent CDN requests in the retiring epoch", () => RedirectChain().GetAwaiter().GetResult());
        suite.Add("transport epochs: an asset operation captured during pause cannot join a later engine", () => AbsentEngineHop().GetAwaiter().GetResult());
        suite.Add("transport epochs: real default handlers are fresh and have no ambient proxy or credentials", FactoryDefaults);
        suite.Add("transport epochs: explicit parallel children remain joined when their scope exits", () => Children().GetAwaiter().GetResult());
        suite.Add("transport epochs: engine retirement cancels capacity waiters without website failures", () => Capacity().GetAwaiter().GetResult());
        suite.Add("transport epochs: a reused factory handler is rejected and cannot be resumed", () => ReusedFactory().GetAwaiter().GetResult());
        suite.Add("transport publication: scope exit joins a discarded response before cancelling its child", () => DiscardedResponse().GetAwaiter().GetResult());
        suite.Add("transport publication: discarded completion retains its primary and physical close failure", () => DiscardedResponseFailure().GetAwaiter().GetResult());
        suite.Add("transport publication: a successful escaped response survives normal scope disposal", () => SuccessfulPublication().GetAwaiter().GetResult());
        suite.Add("transport publication: facade scope failure closes its held result and retains both errors", () => FacadeDiscard().GetAwaiter().GetResult());
        suite.Add("transport publication: reader scope failure closes its held image and retains both errors", () => ReaderDiscard().GetAwaiter().GetResult());
        suite.Add("reader shutdown: synchronous image release cannot invert service and image locks", () => ReaderStopLockOrder().GetAwaiter().GetResult());
    }

    private static async Task Fence()
    {
        var factory = new Factory((_, _) => Task.FromResult(Json("{\"data\":{\"__typename\":\"Query\"}}")));
        await using var client = Client(factory);
        await using var old = client.OpenSession();
        var pause = client.BeginTransportPause();
        Check.True(ReferenceEquals(pause, client.BeginTransportPause()));
        await Throws<SourceWorkDeferredException>(() => client.GraphQlAsync("query { __typename }"));
        var drainTask = client.DrainTransportAsync(pause);
        Check.True(!drainTask.IsCompleted); // A logical scope also owns non-HTTP work.
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Throws<OperationCanceledException>(() => client.DrainTransportAsync(pause, cancelled.Token));
            Check.True(!drainTask.IsCompleted);
        }
        await old.DisposeAsync();
        var drain = await drainTask.WaitAsync(TimeSpan.FromSeconds(3));
        await client.ResumeTransportAsync(drain).WaitAsync(TimeSpan.FromSeconds(3));
        await Throws<OperationCanceledException>(() => old.GraphQlAsync("query { __typename }"));
        await client.GraphQlAsync("query { __typename }");
        Check.Equal(2, factory.Created.Count(handler => handler.Plane == SourceTransportPlane.Engine));
        Check.True(factory.Created.First(handler => handler.Plane == SourceTransportPlane.Engine).Closed);
        await Throws<InvalidOperationException>(() => client.ResumeTransportAsync(drain));
    }

    private static async Task LateHeaders()
    {
        var entered = Signal(); var release = Signal(); var closed = Signal();
        var factory = new Factory(async (_, _) => { entered.TrySetResult(); await release.Task; return new(HttpStatusCode.OK) { Content = new StreamContent(new CloseStream(closed)) }; });
        await using var client = Client(factory);
        var open = client.OpenBinaryStreamAsync("/image"); Task<SourceTransportDrain>? drain = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            drain = client.DrainTransportAsync(client.BeginTransportPause());
            Check.True(!drain.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Cleanup(() => CloseReturned(open), () => Throws<OperationCanceledException>(() => open),
                () => closed.Task.WaitAsync(TimeSpan.FromSeconds(3)),
                () => (drain ?? client.DrainTransportAsync(client.BeginTransportPause())).WaitAsync(TimeSpan.FromSeconds(3)));
        }
    }

    private static async Task External()
    {
        var factory = new Factory((request, _) => Task.FromResult(request.RequestUri!.Host == "cdn.test"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) } : Json("{}")));
        await using var client = Client(factory);
        await using var image = await client.OpenBinaryStreamAsync("https://cdn.test/image");
        var drain = await client.DrainTransportAsync(client.BeginTransportPause()).WaitAsync(TimeSpan.FromSeconds(3));
        Check.True(!image.CancellationToken.IsCancellationRequested);
        var body = await image.OpenBodyAsync(); var bytes = new byte[3]; Check.Equal(3, await body.ReadAsync(bytes));
        Check.Equal(3, (await client.FetchBinaryAsync("https://cdn.test/next")).Bytes.Length);
        Check.True(factory.Created.Single(handler => handler.Plane == SourceTransportPlane.Assets).Targets.All(uri => uri.Host == "cdn.test"));
        await client.ResumeTransportAsync(drain);
    }

    private static async Task ExternalHop()
    {
        var entered = Signal(); var release = Signal();
        var factory = new Factory(async (request, _) =>
        {
            if (request.RequestUri!.Host != "cdn.test") return new(HttpStatusCode.OK) { Content = new ByteArrayContent([7]) };
            entered.TrySetResult(); await release.Task;
            var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new(Origin + "/private"); return redirect;
        });
        await using var client = Client(factory);
        var pending = client.FetchBinaryAsync("https://cdn.test/image");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var drain = await client.DrainTransportAsync(client.BeginTransportPause()).WaitAsync(TimeSpan.FromSeconds(3));
            await client.ResumeTransportAsync(drain);
        }
        finally { release.TrySetResult(); await Throws<SourceWorkDeferredException>(() => pending); }
        Check.True(factory.Created.All(handler => handler.Targets.All(uri => uri.Host != "engine.test")));
        Check.Equal(1, (await client.FetchBinaryAsync("https://cdn.test/new")).Bytes.Length);
        Check.True(factory.Created.Where(handler => handler.Plane == SourceTransportPlane.Assets).All(handler => handler.Targets.All(uri => uri.Host == "cdn.test")));
        Check.Equal(1, factory.Created.Last(handler => handler.Plane == SourceTransportPlane.Engine).Targets.Count);
    }

    private static async Task Composite()
    {
        var entered = Signal(); var release = Signal(); var calls = 0;
        var factory = new Factory(async (_, _) => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; return Json("{\"errors\":[{\"message\":\"nodes unsupported\"}]}"); });
        await using var client = Client(factory); var pending = client.SourcesAsync(); Task<SourceTransportDrain>? drain = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); drain = client.DrainTransportAsync(client.BeginTransportPause()); Check.True(!drain.IsCompleted); }
        finally { release.TrySetResult(); await Cleanup(() => Throws<OperationCanceledException>(() => pending), () => (drain ?? client.DrainTransportAsync(client.BeginTransportPause())).WaitAsync(TimeSpan.FromSeconds(3))); }
        await client.ResumeTransportAsync(await drain!); Check.Equal(1, calls);
    }

    private static async Task CallbackFailure()
    {
        var entered = Signal(); var release = Signal(); var cancelled = Signal();
        var factory = new Factory(async (_, token) =>
        {
            using var registration = token.Register(() => { cancelled.TrySetResult(); throw new InvalidOperationException("controlled callback"); });
            entered.TrySetResult(); await release.Task; return Json("{}");
        });
        var client = Client(factory); var pending = client.GraphQlAsync("query { __typename }"); Task<SourceTransportDrain>? drain = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); drain = client.DrainTransportAsync(client.BeginTransportPause()); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(!drain.IsCompleted); }
        finally
        {
            release.TrySetResult();
            await Cleanup(() => Ignore(pending), () => Throws<Exception>(() => drain ?? client.DrainTransportAsync(client.BeginTransportPause())),
                () => Throws<Exception>(() => client.DisposeAsync().AsTask()), () => Throws<Exception>(() => client.DisposeAsync().AsTask()));
        }
    }

    private static async Task StagedFactory()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var factory = new Factory((_, _) => Task.FromResult(Json("{}"))) { BeforeCreate = (plane, count) => { if (plane == SourceTransportPlane.Engine && count == 2) { entered.Set(); release.Wait(); } } };
        var client = Client(factory); var drain = await client.DrainTransportAsync(client.BeginTransportPause());
        var resume = client.ResumeTransportAsync(drain); Task? stop = null;
        try { Check.True(entered.Wait(TimeSpan.FromSeconds(3))); stop = client.DisposeAsync().AsTask(); Check.True(!stop.IsCompleted); }
        finally { release.Set(); await Ignore(resume); await (stop ?? client.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3)); }
        await Throws<OperationCanceledException>(() => client.GraphQlAsync("query { __typename }"));
        await Throws<OperationCanceledException>(() => client.ResumeTransportAsync(drain));
        Check.True(factory.Created.All(handler => handler.Closed));
    }

    private static async Task OneShot()
    {
        await using var client = new SuwayomiClient(new(), Origin, handler: new Handler(SourceTransportPlane.Engine, (_, _) => Task.FromResult(Json("{}"))));
        Check.Throws<NotSupportedException>(() => client.BeginTransportPause());
    }

    private static async Task ConcurrentResume()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var factory = new Factory((_, _) => Task.FromResult(Json("{}"))) { BeforeCreate = (plane, count) => { if (plane == SourceTransportPlane.Engine && count == 2) { entered.Set(); release.Wait(); } } };
        await using var client = Client(factory); var drain = await client.DrainTransportAsync(client.BeginTransportPause());
        var first = client.ResumeTransportAsync(drain); Task? second = null;
        try { Check.True(entered.Wait(TimeSpan.FromSeconds(3))); second = client.ResumeTransportAsync(drain); Check.True(ReferenceEquals(first, second)); }
        finally { release.Set(); await first.WaitAsync(TimeSpan.FromSeconds(3)); if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(3)); }
        Check.Equal(2, factory.Created.Count(handler => handler.Plane == SourceTransportPlane.Engine));
    }

    private static async Task BodyOwnership()
    {
        foreach (var phase in new[] { "open", "read", "close" })
        {
            using var blocked = new BlockingBody(phase == "close");
            var content = phase == "open" ? (HttpContent)new DelayedContent(blocked) : new StreamContent(blocked);
            var factory = new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
            await using var client = Client(factory); var opening = client.OpenBinaryStreamAsync("/image");
            OwnedUpstreamResponse? response = null;
            Task? work = null; Task<SourceTransportDrain>? drain = null;
            try
            {
                response = await opening.WaitAsync(TimeSpan.FromSeconds(3));
                if (phase == "open") work = response.OpenBodyAsync();
                else if (phase == "read") work = (await response.OpenBodyAsync()).ReadAsync(new byte[1]).AsTask();
                if (phase != "close") await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                drain = client.DrainTransportAsync(client.BeginTransportPause());
                if (phase == "close") await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (phase == "open") await ((DelayedContent)content).Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (phase == "read") await blocked.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check.True(!drain.IsCompleted);
            }
            finally
            {
                blocked.Release.TrySetResult(); blocked.ReleaseClose.Set();
                await Cleanup(() => CloseReturned(opening),
                    () => work is null ? Task.CompletedTask : Throws<OperationCanceledException>(() => work),
                    () => (drain ?? client.DrainTransportAsync(client.BeginTransportPause())).WaitAsync(TimeSpan.FromSeconds(3)));
                blocked.ReleaseClose.Dispose();
            }
            Check.True(blocked.Closed.Task.IsCompletedSuccessfully);
        }
    }

    private static async Task LateBodyFailure()
    {
        using var blocked = new BlockingBody(false, throwOnClose: true);
        var content = new DelayedContent(blocked); var client = Client(new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        var response = await client.OpenBinaryStreamAsync("/image"); var open = response.OpenBodyAsync(); Task<SourceTransportDrain>? drain = null;
        try { await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); drain = client.DrainTransportAsync(client.BeginTransportPause()); await content.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(!drain.IsCompleted); }
        finally
        {
            blocked.Release.TrySetResult();
            await Cleanup(() => Ignore(open), () => Throws<Exception>(() => drain ?? client.DrainTransportAsync(client.BeginTransportPause())),
                () => Throws<Exception>(() => client.DisposeAsync().AsTask()), () => Throws<Exception>(() => response.DisposeAsync().AsTask()));
        }
    }

    private static async Task Capability()
    {
        using var setup = new DatabaseSetup(); var entered = Signal(); var release = Signal(); var abouts = 0; var queries = 0;
        var factory = new Factory(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                if (Interlocked.Increment(ref abouts) == 1) { entered.TrySetResult(); await release.Task; }
                return Json("{\"version\":\"2.3.2243\"}");
            }
            Interlocked.Increment(ref queries); return Json("{\"data\":{\"__schema\":{\"mutationType\":{\"fields\":[{\"name\":\"updateManga\"},{\"name\":\"enqueueChapterDownloads\"},{\"name\":\"fetchMangaAndChapters\"},{\"name\":\"fetchChapterPages\"}]}}}}");
        });
        await using var client = Client(factory);
        await using var service = new SourceEngineCapabilityContract(setup.Settings, client, SourceEngineCapabilityIdentity.ConfiguredExternal(Origin), new SupportPrivacy(), "source");
        var pending = service.CheckAsync(); Task<SourceTransportDrain>? drain = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); drain = client.DrainTransportAsync(client.BeginTransportPause()); Check.True(!drain.IsCompleted); }
        finally { release.TrySetResult(); await Cleanup(() => Throws<OperationCanceledException>(() => pending), () => (drain ?? client.DrainTransportAsync(client.BeginTransportPause())).WaitAsync(TimeSpan.FromSeconds(3))); }
        Check.Equal(0, queries);
        Check.True(setup.Settings.Get(SourceEngineCapabilityContract.SettingKey, new JsonObject()) is JsonObject { Count: 0 });
        await client.ResumeTransportAsync(await drain!); Check.True((await service.CheckAsync())["contract_ok"]!.GetValue<bool>());
        Check.Equal(1, queries); Check.Equal(2, factory.Created.Count(handler => handler.Plane == SourceTransportPlane.CapabilityAbout));
        Check.True(factory.Created.First(handler => handler.Plane == SourceTransportPlane.CapabilityAbout).Closed);
    }

    private static async Task Inventory()
    {
        using var temp = new Sandbox(); using var annotation = new BlockingAnnotations(); await using var io = new BoundedWorkScheduler(2, 8);
        var folder = Path.Combine(temp.Path, "extensions"); Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "fixture.jar"), [1, 2, 3]);
        var calls = 0; var factory = new Factory((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(Json("{\"data\":{\"extensions\":{\"nodes\":[{\"pkgName\":\"eu.example.fixture\",\"versionName\":\"1.0\",\"apkName\":\"fixture.apk\",\"isInstalled\":true,\"source\":{\"nodes\":[{\"id\":\"-9223372036854775808\",\"name\":\"Fixture\",\"lang\":\"en\",\"baseUrl\":\"https://fixture.test\"}]}}]}}}")); });
        await using var client = Client(factory); await using var inventory = new EngineInstalledSources(client, temp.Path, Path.Combine(temp.Path, "maintenance"), () => (true, 7), io, annotation);
        var capture = (await inventory.CaptureAsync("eu.example.fixture", "1.0"))!; Check.Equal(3, capture.Contents().Length);
        annotation.Block = true; var pending = inventory.CaptureAsync("eu.example.fixture", "1.0"); Task<SourceTransportDrain>? drain = null;
        try
        {
            Check.True(annotation.Entered.Wait(TimeSpan.FromSeconds(3))); drain = client.DrainTransportAsync(client.BeginTransportPause());
            Check.Throws<OperationCanceledException>(() => inventory.RequireCurrent(capture)); Check.True(!drain.IsCompleted);
        }
        finally { annotation.Release.Set(); await Cleanup(() => Throws<OperationCanceledException>(() => pending), () => (drain ?? client.DrainTransportAsync(client.BeginTransportPause())).WaitAsync(TimeSpan.FromSeconds(3))); }
        Check.Equal(3, calls); Check.Equal(0, io.Snapshot.Running); await client.ResumeTransportAsync(await drain!);
        Check.Throws<OperationCanceledException>(() => inventory.RequireCurrent(capture));
    }

    private static async Task Controls()
    {
        var entered = Signal(); var release = Signal(); var queries = 0; var mutations = 0;
        var preferences = JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("source-controls.json")))!["cases"]!.AsArray().OfType<JsonObject>().First(row => row["kind"]?.GetValue<string>() == "normalize-preferences")["preferences"]!.DeepClone();
        var factory = new Factory(async (request, token) =>
        {
            var query = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!["query"]!.GetValue<string>();
            if (query.StartsWith("mutation", StringComparison.Ordinal)) Interlocked.Increment(ref mutations);
            else if (Interlocked.Increment(ref queries) == 2) { entered.TrySetResult(); await release.Task; }
            return Json(new JsonObject { ["data"] = new JsonObject { ["source"] = new JsonObject { ["id"] = "17", ["name"] = "Fixture", ["isConfigurable"] = true, ["preferences"] = preferences.DeepClone() } } }.ToJsonString());
        });
        await using var client = Client(factory); using var admin = new SemaphoreSlim(1, 1);
        await using var controls = new SourcePreferenceService(client, admin, () => (true, 1), new SupportPrivacy());
        var screen = await controls.SnapshotAsync("17");
        var change = new JsonObject { ["revision"] = screen["revision"]!.DeepClone(), ["index"] = 0, ["key"] = screen["preferences"]![0]!["key"]?.DeepClone(), ["type"] = screen["preferences"]![0]!["type"]!.DeepClone(), ["value"] = true };
        var update = controls.UpdateAsync("17", change); Task<JsonObject>? waiter = null; Task<SourceTransportDrain>? drain = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); waiter = controls.SnapshotAsync("17");
            drain = client.DrainTransportAsync(client.BeginTransportPause()); Check.True(!drain.IsCompleted); Check.Equal(0, admin.CurrentCount);
        }
        finally
        {
            release.TrySetResult();
            await Cleanup(() => Throws<OperationCanceledException>(() => update),
                () => waiter is null ? Task.CompletedTask : Throws<OperationCanceledException>(() => waiter),
                () => (drain ?? client.DrainTransportAsync(client.BeginTransportPause())).WaitAsync(TimeSpan.FromSeconds(3)));
        }
        Check.Equal(0, mutations); Check.Equal(1, admin.CurrentCount); await client.ResumeTransportAsync(await drain!);
        await Throws<SourcePreferencesConflictException>(() => controls.UpdateAsync("17", change)); Check.Equal(2, queries);
    }

    private static async Task ReaderCache()
    {
        var factory = new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([4, 5]) }));
        await using var client = Client(factory);
        var drain = await client.DrainTransportAsync(client.BeginTransportPause());
        foreach (var index in new[] { -2, -1 })
        {
            using var setup = new ReaderSetup(client, index);
            Check.True((await setup.Pages.ResolvePagesAsync(51))["page_cache_hit"]!.GetValue<bool>());
            await using var image = await setup.Pages.OpenImageAsync(61, 0); using var output = new MemoryStream(); await image.CopyToAsync(output, default);
            Check.Equal(2L, output.Length);
        }
        Check.True(factory.Created.Single(handler => handler.Plane == SourceTransportPlane.Assets).Targets.All(uri => uri.Host == "cdn.test"));
        await client.ResumeTransportAsync(drain);
    }

    private static async Task ReaderMapping()
    {
        var entered = Signal(); var release = Signal(); var calls = 0;
        var factory = new Factory(async (_, _) => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; return Json("{\"chapters\":[{\"id\":300,\"index\":0}]}"); });
        await using var client = Client(factory); using var setup = new ReaderSetup(client, -1);
        var pending = setup.Pages.OpenImageAsync(61, 0); Task<SourceTransportDrain>? drain = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); drain = client.DrainTransportAsync(client.BeginTransportPause()); Check.True(!drain.IsCompleted); }
        finally { release.TrySetResult(); await Cleanup(() => CloseReturned(pending), () => Throws<OperationCanceledException>(() => pending), () => (drain ?? client.DrainTransportAsync(client.BeginTransportPause())).WaitAsync(TimeSpan.FromSeconds(3))); }
        await client.ResumeTransportAsync(await drain!); Check.Equal(1, calls);
    }

    private static async Task HealthSample()
    {
        var entered = Signal(); var release = Signal(); var calls = 0;
        var factory = new Factory(async (_, _) => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; return Json("{\"data\":{\"fetchSourceManga\":{\"hasNextPage\":false,\"mangas\":[{\"id\":1,\"sourceId\":\"17\",\"title\":\"Fixture\"}]}}}"); });
        await using var client = Client(factory); await using var io = new BoundedWorkScheduler(1, 4); var checks = new JsonArray();
        var probe = new SourceHealthProbe(client, io, TimeSpan.Zero); var pending = probe.CheckAsync(new JsonObject { ["source_id"] = "17", ["supports_latest"] = false }, checks); Task<SourceTransportDrain>? drain = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); drain = client.DrainTransportAsync(client.BeginTransportPause()); Check.True(!drain.IsCompleted); }
        finally { release.TrySetResult(); await Cleanup(() => Throws<OperationCanceledException>(() => pending), () => (drain ?? client.DrainTransportAsync(client.BeginTransportPause())).WaitAsync(TimeSpan.FromSeconds(3))); }
        await client.ResumeTransportAsync(await drain!); Check.Equal(1, calls);
    }

    private static async Task CompositeBoundaries()
    {
        foreach (var operation in new[] { "library", "install", "health" })
        {
            var entered = Signal(); var release = Signal(); var calls = 0;
            var factory = new Factory(async (_, _) =>
            {
                Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task;
                return Json(operation == "library" ? "{\"data\":{\"mangas\":{\"nodes\":[{\"id\":1,\"sourceId\":\"17\",\"url\":\"/1\",\"inLibrary\":true}],\"pageInfo\":{\"hasNextPage\":true,\"endCursor\":\"next\"}}}}"
                    : operation == "install" ? "{\"errors\":[{\"message\":\"Exception while fetching data (/updateExtension) : HTTP error 404\"}]}" : "{}");
            });
            await using var client = Client(factory);
            Task pending;
            if (operation == "library") pending = client.LibraryMangasAsync();
            else if (operation == "install") pending = client.UpdateExtensionAsync("eu.example.fixture", "install");
            else pending = client.HealthAsync();
            Task<SourceTransportDrain>? drain = null;
            try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); drain = client.DrainTransportAsync(client.BeginTransportPause()); Check.True(!drain.IsCompleted); }
            finally { release.TrySetResult(); await Cleanup(() => Throws<OperationCanceledException>(() => pending), () => (drain ?? client.DrainTransportAsync(client.BeginTransportPause())).WaitAsync(TimeSpan.FromSeconds(3))); }
            await client.ResumeTransportAsync(await drain!); Check.Equal(1, calls);
        }
    }

    private static async Task FactoryFailure()
    {
        var failed = false;
        var factory = new Factory((_, _) => Task.FromResult(Json("{\"data\":{}}"))) { BeforeCreate = (plane, count) => { if (plane == SourceTransportPlane.Engine && count == 2 && !failed) { failed = true; throw new IOException("Controlled clean factory failure"); } } };
        await using var client = Client(factory); var drain = await client.DrainTransportAsync(client.BeginTransportPause());
        await Throws<IOException>(() => client.ResumeTransportAsync(drain)); await Throws<SourceWorkDeferredException>(() => client.GraphQlAsync("query { __typename }"));
        await client.ResumeTransportAsync(drain); await client.GraphQlAsync("query { __typename }"); Check.Equal(2, factory.Created.Count(handler => handler.Plane == SourceTransportPlane.Engine));
    }

    private static async Task EscapedCancellation()
    {
        using var caller = new CancellationTokenSource(); var closed = Signal();
        await using var client = Client(new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CloseStream(closed)) })));
        var response = await client.OpenBinaryStreamAsync("/image", cancellationToken: caller.Token);
        caller.Cancel(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(3)); await response.DisposeAsync();
        await client.DrainTransportAsync(client.BeginTransportPause()).WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task RedirectChain()
    {
        var entered = Signal(); var release = Signal();
        var factory = new Factory(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/finish") { entered.TrySetResult(); await release.Task; return new(HttpStatusCode.OK) { Content = new ByteArrayContent([8]) }; }
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new(request.RequestUri.Host == "cdn.test" ? Origin + "/private" : "https://cdn.test/finish"); return redirect;
        });
        await using var client = Client(factory); var pending = client.FetchBinaryAsync("https://cdn.test/start"); Task<SourceTransportDrain>? drain = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); drain = client.DrainTransportAsync(client.BeginTransportPause()); Check.True(!drain.IsCompleted); }
        finally { release.TrySetResult(); await Cleanup(() => Throws<OperationCanceledException>(() => pending), () => (drain ?? client.DrainTransportAsync(client.BeginTransportPause())).WaitAsync(TimeSpan.FromSeconds(3))); }
        var assets = factory.Created.Single(handler => handler.Plane == SourceTransportPlane.Assets); var engine = factory.Created.Single(handler => handler.Plane == SourceTransportPlane.Engine);
        Check.Equal(1, assets.Targets.Count); Check.Equal("/start", assets.Targets[0].AbsolutePath);
        Check.True(engine.Targets.Select(uri => uri.AbsolutePath).SequenceEqual(new[] { "/private", "/finish" })); Check.True(engine.Closed && !assets.Closed);
    }

    private static async Task AbsentEngineHop()
    {
        var entered = Signal(); var release = Signal();
        var factory = new Factory(async (_, _) => { entered.TrySetResult(); await release.Task; var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new(Origin + "/private"); return response; });
        await using var client = Client(factory); var drain = await client.DrainTransportAsync(client.BeginTransportPause());
        var pending = client.FetchBinaryAsync("https://cdn.test/start");
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await client.ResumeTransportAsync(drain); }
        finally { release.TrySetResult(); await Throws<SourceWorkDeferredException>(() => pending); }
        Check.True(factory.Created.Where(handler => handler.Plane == SourceTransportPlane.Engine).All(handler => handler.Targets.Count == 0));
    }

    private static void FactoryDefaults()
    {
        var factory = new DefaultSourceTransportFactory(new AutoSourcarrSettings());
        using var first = (SocketsHttpHandler)factory.Create(SourceTransportPlane.Engine);
        using var second = (SocketsHttpHandler)factory.Create(SourceTransportPlane.Engine);
        using var assets = (SocketsHttpHandler)factory.Create(SourceTransportPlane.Assets);
        using var about = (SocketsHttpHandler)factory.Create(SourceTransportPlane.CapabilityAbout);
        Check.True(!ReferenceEquals(first, second) && !ReferenceEquals(first, assets));
        foreach (var handler in new[] { first, second, assets, about }) Check.True(!handler.UseProxy && handler.Credentials is null && !handler.AllowAutoRedirect);
        Check.True(!ReferenceEquals(first.CookieContainer, second.CookieContainer)); Check.True(!about.UseCookies && about.AutomaticDecompression == DecompressionMethods.None);
    }

    private static async Task Children()
    {
        var entered = Signal(); var release = Signal(); var calls = 0;
        var factory = new Factory(async (_, _) => { if (Interlocked.Increment(ref calls) == 2) entered.TrySetResult(); await release.Task; return Json("{\"data\":{}}"); });
        await using var client = Client(factory); await using var session = client.OpenSession();
        var first = session.GraphQlAsync("query { __typename }"); var second = session.GraphQlAsync("query { __typename }"); Task? closed = null; Task<SourceTransportDrain>? drain = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); closed = session.DisposeAsync().AsTask();
            drain = client.DrainTransportAsync(client.BeginTransportPause()); Check.True(!closed.IsCompleted && !drain.IsCompleted);
        }
        finally
        {
            closed ??= session.DisposeAsync().AsTask(); release.TrySetResult();
            await Cleanup(() => Throws<OperationCanceledException>(() => first), () => Throws<OperationCanceledException>(() => second),
                () => closed.WaitAsync(TimeSpan.FromSeconds(3)),
                () => (drain ?? client.DrainTransportAsync(client.BeginTransportPause())).WaitAsync(TimeSpan.FromSeconds(3)));
        }
        Check.True(first.IsCompleted && second.IsCompleted); Check.Equal(2, calls);
    }

    private static async Task Capacity()
    {
        var entered = Signal(); var release = Signal(); var calls = 0; var attention = new Attention();
        var factory = new Factory(async (_, _) => { if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task; } return Json("{\"data\":{\"fetchSourceManga\":{\"hasNextPage\":false,\"mangas\":[]}}}"); });
        await using var client = new SuwayomiClient(new AutoSourcarrSettings(), Origin, workLimit: 1, transportFactory: factory) { Attention = attention };
        var first = client.FetchSourceMangaAsync("17", "POPULAR"); Task<JsonObject>? waiting = null; Task<SourceTransportDrain>? drain = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); waiting = client.FetchSourceMangaAsync("17", "POPULAR");
            Check.Equal(1, calls); drain = client.DrainTransportAsync(client.BeginTransportPause()); Check.True(!drain.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Cleanup(() => Throws<OperationCanceledException>(() => first),
                () => waiting is null ? Task.CompletedTask : Throws<OperationCanceledException>(() => waiting),
                () => (drain ?? client.DrainTransportAsync(client.BeginTransportPause())).WaitAsync(TimeSpan.FromSeconds(3)));
        }
        Check.Equal(1, calls); Check.Equal(0, attention.Failures); await client.ResumeTransportAsync(await drain!);
        await client.FetchSourceMangaAsync("17", "POPULAR"); Check.Equal(2, calls);
    }

    private static async Task ReusedFactory()
    {
        var factory = new ReusingFactory(); var client = new SuwayomiClient(new AutoSourcarrSettings(), Origin, transportFactory: factory);
        try
        {
            await client.GraphQlAsync("query { __typename }"); var drain = await client.DrainTransportAsync(client.BeginTransportPause());
            await Throws<InvalidOperationException>(() => client.ResumeTransportAsync(drain));
            await Throws<InvalidOperationException>(() => client.ResumeTransportAsync(drain));
            await Throws<SourceWorkDeferredException>(() => client.GraphQlAsync("query { __typename }"));
            Check.Equal(1, factory.Engine.Targets.Count);
        }
        finally { await Throws<Exception>(() => client.DisposeAsync().AsTask()); }
        Check.True(factory.Engine.Closed && factory.Assets.Closed);
    }

    private static async Task DiscardedResponse()
    {
        using var body = new BlockingBody(blockClose: true);
        var boundary = Signal(); var release = Signal();
        var client = Client(new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));
        await using var session = client.OpenSession();
        session.ResponseCompletionBoundary = async () => { boundary.TrySetResult(); await release.Task; };
        var pending = session.OpenBinaryStreamAsync("/image"); Task? close = null;
        try
        {
            await boundary.Task.WaitAsync(TimeSpan.FromSeconds(3));
            close = session.DisposeAsync().AsTask();
            Check.True(!pending.IsCompleted && !close.IsCompleted);
            release.TrySetResult();
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(!pending.IsCompleted && !close.IsCompleted, "The discarded result was acknowledged before its physical close finished");
        }
        finally
        {
            release.TrySetResult(); body.ReleaseClose.Set();
            await Cleanup(() => Throws<OperationCanceledException>(() => pending),
                () => (close ?? session.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3)),
                () => client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            body.ReleaseClose.Dispose();
        }
        Check.True(body.Closed.Task.IsCompletedSuccessfully);
    }

    private static async Task DiscardedResponseFailure()
    {
        foreach (var cancelled in new[] { false, true })
        {
            using var body = new BlockingBody(false, throwOnClose: true); var boundary = Signal(); var release = Signal();
            var client = Client(new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));
            await using var session = client.OpenSession();
            session.ResponseCompletionBoundary = async () => { boundary.TrySetResult(); await release.Task; if (!cancelled) throw new PublicationBoundaryException(); };
            var pending = session.OpenBinaryStreamAsync("/image"); Task? close = null;
            try
            {
                await boundary.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (cancelled) close = session.DisposeAsync().AsTask();
                release.TrySetResult(); var error = await Error(pending);
                Check.True(error is AggregateException && Contains<IOException>(error));
                Check.True(cancelled ? Contains<OperationCanceledException>(error) : Contains<PublicationBoundaryException>(error));
                Check.True(body.Closed.Task.IsCompletedSuccessfully && pending.IsFaulted);
            }
            finally
            {
                release.TrySetResult();
                await Cleanup(() => Ignore(pending), () => (close ?? session.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3)),
                    () => Throws<Exception>(() => client.DisposeAsync().AsTask()));
            }
        }
    }

    private static async Task SuccessfulPublication()
    {
        var closed = Signal(); await using var client = Client(new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CloseStream(closed)) })));
        await using var session = client.OpenSession(); var response = await session.OpenBinaryStreamAsync("/image");
        try
        {
            await session.DisposeAsync(); Check.True(!closed.Task.IsCompleted && !response.CancellationToken.IsCancellationRequested);
            Check.Equal(1, (await response.OpenBodyAsync()).ReadByte());
        }
        finally { await response.DisposeAsync(); }
        Check.True(closed.Task.IsCompletedSuccessfully);
    }

    private static async Task FacadeDiscard()
    {
        foreach (var closeFails in new[] { false, true })
        {
            using var body = new BlockingBody(false, throwOnClose: closeFails);
            var client = Client(new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));
            client.BinaryScopeExitBoundary = scope =>
            {
                scope.ScopeCancellationToken.Register(() => throw new PublicationBoundaryException());
                return Task.CompletedTask;
            };
            var pending = client.OpenBinaryStreamAsync("/image");
            try
            {
                var error = await Error(pending);
                Check.True(Contains<PublicationBoundaryException>(error));
                if (closeFails) Check.True(Contains<IOException>(error));
                Check.True(body.Closed.Task.IsCompletedSuccessfully, "Facade lost its returned response when scope disposal failed");
            }
            finally { await Cleanup(() => CloseReturned(pending), () => Throws<Exception>(() => client.DisposeAsync().AsTask())); }
        }
    }

    private static async Task ReaderDiscard()
    {
        foreach (var closeFails in new[] { false, true })
        {
            using var body = new BlockingBody(false, throwOnClose: closeFails);
            var client = Client(new Factory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));
            using var setup = new ReaderSetup(client, -2, new FailingReaderScope(new SuwayomiReaderPageSource(client)));
            var pending = setup.Pages.OpenImageAsync(61, 0);
            try
            {
                var error = await Error(pending); Check.True(Contains<PublicationBoundaryException>(error));
                if (closeFails) Check.True(Contains<IOException>(error));
                Check.True(body.Closed.Task.IsCompletedSuccessfully);
            }
            finally
            {
                await Cleanup(() => CloseReturned(pending),
                    () => closeFails ? Throws<Exception>(() => client.DisposeAsync().AsTask()) : client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            }
        }
    }

    private static async Task ReaderStopLockOrder()
    {
        var closeEntered = Signal(); using var releaseSynchronousClose = new ManualResetEventSlim();
        var physicalClose = Signal(); var stopReturned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var synchronous = 0; var closes = 0;
        ValueTask Close()
        {
            closeEntered.TrySetResult();
            if (!releaseSynchronousClose.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controlled synchronous close was not released");
            Interlocked.Increment(ref closes);
            // On a failed assertion, leave one real incomplete close so old code can release both
            // monitor gates before finally completes the close. The green path is synchronous.
            return Volatile.Read(ref synchronous) == 1 ? ValueTask.CompletedTask : new ValueTask(physicalClose.Task);
        }
        await using var client = Client(new Factory((_, _) => Task.FromResult(Json("{}"))));
        using var setup = new ReaderSetup(client, -2, new SynchronousCloseSource(Close));
        var image = await setup.Pages.OpenImageAsync(61, 0);
        var closing = Task.Run(async () => await image.DisposeAsync()); Task? invokingStop = null;
        Task InvokeStop() => Task.Run(() =>
        {
            try { stopReturned.TrySetResult(setup.Pages.StopAsync()); }
            catch (Exception error) { stopReturned.TrySetException(error); throw; }
        });
        try
        {
            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            invokingStop = InvokeStop();
            var stop = await stopReturned.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(!stop.IsCompleted && !closing.IsCompleted, "Stop must return its pending join before attempting the held image gate");
            await Throws<SourceWorkDeferredException>(() => setup.Pages.ResolvePagesAsync(51));
            Volatile.Write(ref synchronous, 1); releaseSynchronousClose.Set();
            await closing.WaitAsync(TimeSpan.FromSeconds(3)); await stop.WaitAsync(TimeSpan.FromSeconds(3));
            Check.Equal(1, closes);
        }
        finally
        {
            releaseSynchronousClose.Set(); var stopInvoker = invokingStop ??= InvokeStop();
            Task? stop = null;
            try { stop = await stopReturned.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
            finally
            {
                physicalClose.TrySetResult();
                await Cleanup(() => stopInvoker.WaitAsync(TimeSpan.FromSeconds(3)),
                    () => closing.WaitAsync(TimeSpan.FromSeconds(3)),
                    () => (stop ?? setup.Pages.StopAsync()).WaitAsync(TimeSpan.FromSeconds(3)));
            }
        }
    }

    private static SuwayomiClient Client(Factory factory) => new(new AutoSourcarrSettings(), Origin, transportFactory: factory);
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Throws<T>(Func<Task> work) where T : Exception
    {
        Task? task = null;
        try { task = work(); await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (T) when (task is null || task.IsCompleted) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task Ignore(Task work) { try { await work.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { } }
    private static async Task<Exception> Error(Task work)
    {
        try { await work.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception error) when (work.IsCompleted) { return error; }
        throw new InvalidOperationException("Expected a failed or cancelled publication");
    }
    private static bool Contains<T>(Exception error) where T : Exception => error is T
        || error is AggregateException aggregate && aggregate.InnerExceptions.Any(Contains<T>)
        || error.InnerException is { } inner && Contains<T>(inner);
    private static async Task Cleanup(params Func<Task>[] steps)
    {
        var errors = new List<Exception>();
        foreach (var step in steps) try { await step(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Controlled fixture cleanup/expectation failed", errors);
    }
    private static async Task CloseReturned<T>(Task<T> work)
    {
        T result;
        try { result = await work.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception) when (work.IsCompleted && !work.IsCompletedSuccessfully) { return; }
        if (result is IAsyncDisposable owned) await owned.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }
    private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : ISourceTransportFactory
    {
        public List<Handler> Created { get; } = [];
        public Action<SourceTransportPlane, int>? BeforeCreate { get; init; }
        public HttpMessageHandler Create(SourceTransportPlane plane)
        {
            int count; lock (Created) count = Created.Count(handler => handler.Plane == plane) + 1;
            BeforeCreate?.Invoke(plane, count); var result = new Handler(plane, send); lock (Created) Created.Add(result); return result;
        }
    }
    private sealed class Handler(SourceTransportPlane plane, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public SourceTransportPlane Plane { get; } = plane;
        public List<Uri> Targets { get; } = [];
        public bool Closed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { lock (Targets) Targets.Add(request.RequestUri!); return send(request, cancellationToken); }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
    private sealed class ReusingFactory : ISourceTransportFactory
    {
        public Handler Engine { get; } = new(SourceTransportPlane.Engine, (_, _) => Task.FromResult(Json("{\"data\":{}}")));
        public Handler Assets { get; } = new(SourceTransportPlane.Assets, (_, _) => Task.FromResult(Json("{}")));
        public HttpMessageHandler Create(SourceTransportPlane plane) => plane == SourceTransportPlane.Engine ? Engine : Assets;
    }
    private sealed class CloseStream(TaskCompletionSource closed) : MemoryStream([1, 2, 3])
    { protected override void Dispose(bool disposing) { closed.TrySetResult(); base.Dispose(disposing); } }
    private sealed class DatabaseSetup : IDisposable
    {
        private readonly Sandbox _temp = new(); public SqliteDatabase Database { get; } public SettingsRepository Settings { get; }
        public DatabaseSetup() { Database = new(new AutoSourcarrSettings { DataDir = _temp.Path }); new SchemaMigrator(Database).Initialize(); Settings = new(Database); }
        public void Dispose() { Database.Dispose(); _temp.Dispose(); }
    }
    private sealed class ReaderSetup : IDisposable
    {
        private readonly DatabaseSetup _database = new(); private readonly BoundedWorkScheduler _io = new(2, 8);
        public ReaderPageService Pages { get; }
        public ReaderSetup(SuwayomiClient client, int chapterIndex, IReaderPageSource? source = null)
        {
            _database.Database.Write(DatabaseFile.Catalog, session => session.Execute("""
                INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(41,'Fixture','now','now');
                INSERT INTO source_config(source_id,name,lang,priority,last_seen) VALUES('17','Fixture','en',10,'now');
                INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,last_checked) VALUES(41,'17',200,'Fixture','now');
                """));
            _database.Database.Write(DatabaseFile.Origins, session => session.Execute("""
                INSERT INTO virtual_chapters(id,entry_id,chapter_key,chapter_number,display_name,created_at,updated_at) VALUES(51,41,'1',1,'Chapter 1','now','now');
                INSERT INTO chapter_candidates(id,virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,source_chapter_index,name,chapter_number,page_count,first_seen,last_seen)
                VALUES(61,51,'17','Fixture',200,300,$index,'Chapter 1',1,1,'now','now');
                INSERT INTO candidate_pages(candidate_id,page_index,private_url,updated_at) VALUES(61,0,'https://cdn.test/image',$time);
                """, ("$index", chapterIndex), ("$time", DateTimeOffset.UtcNow.ToString("O"))));
            Pages = new(new ReaderRepository(_database.Database, new object()), source ?? new SuwayomiReaderPageSource(client), _io, TimeSpan.FromHours(1));
        }
        public void Dispose() { Pages.DisposeAsync().AsTask().GetAwaiter().GetResult(); _io.DisposeAsync().AsTask().GetAwaiter().GetResult(); _database.Dispose(); }
    }
    private sealed class BlockingAnnotations : IInstalledSourceAnnotations, IDisposable
    {
        public bool Block; public ManualResetEventSlim Entered { get; } = new(); public ManualResetEventSlim Release { get; } = new();
        public IReadOnlyList<JsonObject> Annotate(IReadOnlyList<JsonObject> rows)
        {
            if (Block) { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controlled annotation was not released"); }
            return rows;
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
    private sealed class DelayedContent(BlockingBody body) : HttpContent
    {
        public TaskCompletionSource Disposed { get; } = Signal();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        { body.Entered.TrySetResult(); await body.Release.Task; return body; }
        protected override void Dispose(bool disposing) { try { base.Dispose(disposing); } finally { if (disposing) Disposed.TrySetResult(); } }
    }
    private sealed class BlockingBody(bool blockClose, bool throwOnClose = false) : Stream
    {
        public TaskCompletionSource Entered { get; } = Signal(); public TaskCompletionSource Release { get; } = Signal(); public TaskCompletionSource Closed { get; } = Signal();
        public ManualResetEventSlim ReleaseClose { get; } = new(); private int _closes;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task; buffer.Span[0] = 7; return 1; }
        public override void Flush() => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Increment(ref _closes) == 1)
            {
                if (blockClose) { Entered.TrySetResult(); if (!ReleaseClose.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controlled close was not released"); }
                Closed.TrySetResult(); base.Dispose(disposing);
                if (throwOnClose) throw new IOException("Controlled late body close failure");
            }
        }
    }
    private sealed class Attention : ISourceAttention
    {
        public int Failures;
        public Task<JsonObject> ContextAsync(string baseUrl, JsonObject operation, CancellationToken cancellationToken) => Task.FromResult(new JsonObject());
        public Task CheckAsync(JsonObject context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FailedAsync(JsonObject context, JsonObject operation, Exception error, CancellationToken cancellationToken) { Interlocked.Increment(ref Failures); return Task.CompletedTask; }
        public Task CheckWebsiteAsync(string baseUrl, string host, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Learn(JsonObject value) { }
    }
    private sealed class PublicationBoundaryException() : Exception("Controlled publication scope failure");
    private sealed class SynchronousCloseSource(Func<ValueTask> close) : IReaderPageSource
    {
        public Task CheckAttentionAsync(long chapterId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> FetchPagesAsync(long chapterId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["https://cdn.test/image"]);
        public Task<int> ChapterIndexAsync(long mangaId, long chapterId, CancellationToken cancellationToken) => Task.FromResult(-2);
        public Task<ReaderImageStream> OpenPageAsync(long mangaId, int chapterIndex, int pageIndex, CancellationToken cancellationToken) => OpenBinaryAsync("", cancellationToken);
        public Task<ReaderImageStream> OpenBinaryAsync(string privateUrl, CancellationToken cancellationToken)
            => Task.FromResult(new ReaderImageStream(_ => Task.FromResult<Stream>(new MemoryStream([1])), close));
    }
    private sealed class FailingReaderScope(IReaderScopedPageSource source) : IReaderScopedPageSource
    {
        public IReaderPageOperation OpenOperation(CancellationToken lifetime) => new FailingOperation(source.OpenOperation(lifetime));
        public Task CheckAttentionAsync(long chapterId, CancellationToken cancellationToken) => source.CheckAttentionAsync(chapterId, cancellationToken);
        public Task<IReadOnlyList<string>> FetchPagesAsync(long chapterId, CancellationToken cancellationToken) => source.FetchPagesAsync(chapterId, cancellationToken);
        public Task<int> ChapterIndexAsync(long mangaId, long chapterId, CancellationToken cancellationToken) => source.ChapterIndexAsync(mangaId, chapterId, cancellationToken);
        public Task<ReaderImageStream> OpenPageAsync(long mangaId, int chapterIndex, int pageIndex, CancellationToken cancellationToken) => source.OpenPageAsync(mangaId, chapterIndex, pageIndex, cancellationToken);
        public Task<ReaderImageStream> OpenBinaryAsync(string privateUrl, CancellationToken cancellationToken) => source.OpenBinaryAsync(privateUrl, cancellationToken);
        private sealed class FailingOperation(IReaderPageOperation inner) : IReaderPageOperation
        {
            public IReaderPageSource Source => inner.Source;
            public CancellationToken CancellationToken => inner.CancellationToken;
            public void ThrowIfRetired() => inner.ThrowIfRetired();
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); throw new PublicationBoundaryException(); }
        }
    }
}
