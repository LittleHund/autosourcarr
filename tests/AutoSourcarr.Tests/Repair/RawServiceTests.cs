using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Tests;

public static class RawServiceTests
{
    private const string Package = "eu.kanade.tachiyomi.extension.en.example";
    private static JsonObject Extension() => new() { ["pkgName"] = Package, ["versionName"] = "1.4.23", ["storeIndexUrl"] = SourceOrigin.KeiyoushiIndex };
    public static void Register(TestSuite suite)
    {
        suite.Add("raw service: manual capture progresses while automatic inventory waits for browser idle", () =>
        {
            using var setup = new Setup(); using var interactive = setup.Lanes.BeginInteractive(); setup.Service.StartAsync().GetAwaiter().GetResult();
            var receipt = setup.Service.Schedule(Extension())!; var id = receipt["task_id"]!.GetValue<string>();
            Wait(() => !setup.Tasks.IsRunning(id)); Check.Equal("available", setup.Catalog.Status(Package)!["state"]!.GetValue<string>());
            Check.Equal(0, setup.Lanes.Snapshot.BackgroundActive); Check.Equal(1, setup.Installed.ObserveCalls); Check.Equal(0, setup.Installed.InventoryCalls);
            Check.True(setup.History.Snapshot(Package)["current"]!["confirmed_at"] is null); Check.Equal(0, setup.Service.PendingItems().Count);
        });
        suite.Add("raw service: engine readiness waits own no lane or serial capacity", () =>
        {
            using var setup = new Setup(); setup.Installed.Ready = false; setup.Service.StartAsync().GetAwaiter().GetResult();
            var receipt = setup.Service.Schedule(Extension())!; Wait(() => !setup.Tasks.IsRunning(receipt["task_id"]!.GetValue<string>()));
            Check.Equal("available", setup.Catalog.Status(Package)!["state"]!.GetValue<string>()); Check.Equal(0, setup.Lanes.Snapshot.BackgroundActive);
            Check.Equal(0, setup.Installed.InventoryCalls); setup.Service.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        });
        suite.Add("raw service: repeated capture coalesces and cancellation joins registered work", () =>
        {
            using var setup = new Setup(); using var interactive = setup.Lanes.BeginInteractive(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var left = false;
            setup.Repository.Operation = async (_, token) => { entered.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } finally { left = true; } return new JsonObject(); };
            setup.Service.StartAsync().GetAwaiter().GetResult(); var first = setup.Service.Schedule(Extension())!; entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            var second = setup.Service.Schedule(Extension())!; Check.Equal(first["task_id"]!.GetValue<string>(), second["task_id"]!.GetValue<string>()); Check.True(second["coalesced"]!.GetValue<bool>());
            Check.True(setup.Tasks.RequestCancelAsync(first["task_id"]!.GetValue<string>()).GetAwaiter().GetResult());
            setup.Service.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); Check.True(left); Check.True(setup.Service.Schedule(Extension()) is null);
            Check.True(setup.Catalog.Status(Package)!["raw_id"] is null); Check.Equal(0, setup.Lanes.Snapshot.ManualActive);
        });
        suite.Add("raw service: storage failure remains actionable without reading or starting sources", () =>
        {
            using var setup = new Setup(factoryFailure: true); setup.Service.StartAsync().GetAwaiter().GetResult();
            Check.True(setup.Service.Catalog is null); var rows = setup.Service.PendingItems(); Check.Equal(1, rows.Count); Check.Equal("source-raw-storage", rows[0]!["id"]!.GetValue<string>());
            Check.Equal(0, setup.Installed.ObserveCalls); Check.Equal(0, setup.Installed.InventoryCalls); Check.True(!rows.ToJsonString().Contains("SYNTHETIC_SECRET", StringComparison.Ordinal));
            Check.True(setup.Service.Schedule(Extension()) is null);
        });
        suite.Add("raw service: shutdown waits for a physically running startup filesystem operation", () =>
        {
            using var setup = new Setup(); using var began = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var service = setup.CreateService(() => { began.Set(); release.Wait(); return setup.Catalog; });
            try
            {
                var start = service.StartAsync(); Check.True(began.Wait(TimeSpan.FromSeconds(3))); var stop = service.StopAsync(); Check.True(!stop.IsCompleted);
                release.Set(); Check.Throws<OperationCanceledException>(() => start.GetAwaiter().GetResult()); stop.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Check.True(service.Catalog is null); Check.Equal(0, setup.Io.Snapshot.Running);
            }
            finally { release.Set(); service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("raw service: raw-only repair admissions suppress duplicate cached input prompts", () =>
        {
            using var setup = new Setup(); setup.Catalog.Begin(Extension(), manual: true, binarySha256: new string('1', 64));
            using var interactive = setup.Lanes.BeginInteractive(); setup.Service.StartAsync().GetAwaiter().GetResult(); Check.Equal(1, setup.Service.PendingItems().Count);
            setup.Admissions.Add(new JsonObject { ["package"] = Package, ["state"] = "accepted", ["raw_only"] = true }); Check.Equal(0, setup.Service.PendingItems().Count);
            setup.Admissions[0]!["state"] = "failed"; Check.Equal(1, setup.Service.PendingItems().Count);
        });
        suite.Add("raw service: older automatic negative discovery is revisited once under the current capability", () =>
        {
            foreach (var advertised in new[] { false, true })
            {
                using var setup = new Setup(); var extension = Extension(); extension["storeIndexUrl"] = "";
                setup.Legacy(extension, JsonValue.Create(1)!); setup.Installed.Current = extension; setup.Installed.Rows = [extension];
                setup.Indexes = ["https://raw.githubusercontent.com/publisher/extensions/repo/index.json"];
                setup.Fetch.Operation = (request, _) => Task.FromResult(new PublicSourceResponse(System.Text.Encoding.UTF8.GetBytes(
                    request.Url.AbsolutePath.EndsWith("README.md", StringComparison.Ordinal) ? "# Source Code\nhttps://github.com/publisher/original-source\n"
                    : advertised ? "{\"extensionList\":{\"extensions\":[{\"packageName\":\"" + Package + "\"}]}}" : "{}"), request.Url));
                setup.Service.StartAsync().GetAwaiter().GetResult(); setup.Service.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                var result = setup.Catalog.Status(Package)!;
                Check.Equal(SourceDiscovery.CapabilityVersion, result["discovery_version"]!.GetValue<int>());
                Check.Equal(advertised ? "available" : "needs-input", result["state"]!.GetValue<string>());
                Check.Equal(advertised ? 2 : 1, setup.Fetch.Requests.Count); Check.Equal(1, setup.Installed.ObserveCalls);
                var receipt = setup.Service.Schedule(extension, origin: "automatic")!;
                Wait(() => !setup.Tasks.IsRunning(receipt["task_id"]!.GetValue<string>()));
                Check.Equal(advertised ? 2 : 1, setup.Fetch.Requests.Count); Check.Equal(result["revision"]!.GetValue<long>(), setup.Catalog.Status(Package)!["revision"]!.GetValue<long>());
                setup.Service.StopAsync().GetAwaiter().GetResult();
                var restart = setup.CreateService(() => setup.Catalog);
                try { restart.StartAsync().GetAwaiter().GetResult(); restart.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); Check.Equal(2, setup.Installed.ObserveCalls); }
                finally { restart.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
        });
        suite.Add("raw service: discovery upgrades preserve manual, orphan, current and admitted reconstruction choices", () =>
        {
            foreach (var protection in new[] { "manual", "orphan", "current", "future", "unknown-marker", "admitting", "accepted" })
            {
                using var setup = new Setup(); var extension = Extension(); extension["storeIndexUrl"] = "";
                if (protection == "orphan") extension["orphan_reason"] = "manual-upload";
                var marker = protection == "current" ? JsonValue.Create(SourceDiscovery.CapabilityVersion)! : protection == "future" ? JsonValue.Create(99)! : protection == "unknown-marker" ? JsonValue.Create("1")! : JsonValue.Create(1)!;
                setup.Legacy(extension, marker, protection == "manual" ? true : null); setup.Installed.Current = extension; setup.Installed.Rows = [extension];
                if (protection is "admitting" or "accepted") setup.Admissions.Add(new JsonObject { ["package"] = Package, ["state"] = protection, ["raw_only"] = true });
                var before = setup.Catalog.Status(Package)!.ToJsonString();
                setup.Service.StartAsync().GetAwaiter().GetResult(); setup.Service.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Check.True(before == setup.Catalog.Status(Package)!.ToJsonString(), protection); Check.Equal(0, setup.Installed.ObserveCalls); Check.Equal(0, setup.Fetch.Requests.Count);
            }
        });
    }
    private static void Wait(Func<bool> ready) => Check.True(SpinWait.SpinUntil(ready, TimeSpan.FromSeconds(3)), "Owned source operation did not finish within the fixture deadline");
    private sealed class Installed : IRawInstalledSources
    {
        public bool Ready { get; set; } = true;
        public int ObserveCalls, InventoryCalls;
        public JsonObject Current = Extension();
        public IReadOnlyList<JsonObject> Rows = [];
        public Task<IReadOnlyList<JsonObject>> InstalledAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref InventoryCalls); return Task.FromResult<IReadOnlyList<JsonObject>>(Rows.Select(row => (JsonObject)row.DeepClone()).ToArray()); }
        public Task<RawInstalledSource?> ObserveAsync(string package, string version, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref ObserveCalls); return Task.FromResult<RawInstalledSource?>(new((JsonObject)Current.DeepClone(), new string('1', 64), new string('2', 64), ["123"])); }
    }
    private sealed class Repository : IRawRepositorySource
    {
        public Func<JsonObject, CancellationToken, Task<JsonObject>> Operation = (_, _) => Task.FromResult(new JsonObject { ["package"] = Package, ["version"] = "1.4.23", ["id"] = new string('a', 64), ["origin"] = new JsonObject { ["basis"] = "known-repository" } });
        public Task<JsonObject> ObtainAsync(JsonObject origin, CancellationToken cancellationToken = default) => Operation(origin, cancellationToken);
    }
    private sealed class Fetch : IPublicSourceFetcher
    {
        public List<PublicSourceRequest> Requests { get; } = [];
        public Func<PublicSourceRequest, CancellationToken, Task<PublicSourceResponse>> Operation = (_, _) => throw new IOException("No source was fetched");
        public Task<PublicSourceResponse> FetchAsync(PublicSourceRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); return Operation(request, cancellationToken); }
    }
    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _sandbox = new(); private readonly SemaphoreSlim _admin = new(1, 1); private readonly SupportPrivacy _privacy = new();
        public Installed Installed { get; } = new();
        public Repository Repository { get; } = new();
        public Fetch Fetch { get; } = new();
        public IReadOnlyList<string> Indexes { get; set; } = [];
        public JsonArray Admissions { get; } = [];
        public BoundedWorkScheduler Io { get; } = new(2, 16);
        public TaskRegistry Tasks { get; } = new();
        public WholeTaskCoordinator Lanes { get; }
        public RepairHistory History { get; }
        public SourceRawCatalog Catalog { get; private set; }
        private readonly IPrivateDocumentStore _requests;
        public SourceRawService Service { get; }
        public Setup(bool factoryFailure = false)
        {
            _privacy.Register(new Dictionary<string, string?> { ["apiKey"] = "SYNTHETIC_SECRET" });
            Lanes = new(new AutoSourcarrSettings { BackgroundIdleSeconds = .01 }, Tasks);
            History = new(new PrivateDocumentStore(Path.Combine(_sandbox.Path, "history")));
            _requests = new PrivateDocumentStore(Path.Combine(_sandbox.Path, "raw-requests"));
            Catalog = new(new RawStore(Path.Combine(_sandbox.Path, "sources", "raw"), true), new RawStore(Path.Combine(_sandbox.Path, "raw-private")), _requests, Repository, Fetch);
            Service = CreateService(() => factoryFailure ? throw new InvalidDataException("SYNTHETIC_SECRET damaged state") : Catalog);
        }
        public void Legacy(JsonObject extension, JsonNode marker, bool? manual = null)
        {
            var value = Catalog.Begin(extension, manual, new string('1', 64)); value["discovery_version"] = marker.DeepClone();
            var name = RawCodeRules.Digest(System.Text.Encoding.UTF8.GetBytes(Package)) + ".json";
            _requests.Locked(name, document => { document.Write(CanonicalJson.Bytes(value)); return 0; });
            Catalog = new(Catalog.Shared, Catalog.Private, _requests, Repository, Fetch);
        }
        public SourceRawService CreateService(Func<SourceRawCatalog> factory) => new(factory, Installed, _admin, () => Indexes, () => (JsonArray)Admissions.DeepClone(), Io, Lanes, Tasks, _privacy, _ => { }, History);
        public void Dispose()
        {
            Service.DisposeAsync().AsTask().GetAwaiter().GetResult(); Lanes.DisposeAsync().AsTask().GetAwaiter().GetResult(); Io.DisposeAsync().AsTask().GetAwaiter().GetResult(); _admin.Dispose(); _sandbox.Dispose();
        }
    }
}
