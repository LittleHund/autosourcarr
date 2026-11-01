using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Repair.Migration;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Inventory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests.Api;

/// <summary>
/// Phase 6 RepairEndpoints under /api/v1/source-maintenance: retired worker setup (410), in-app control state, repair
/// jobs on the job queue (task id = job id), rollback delegation to the domain repair, bounded feedback, the repair
/// task projection and admin-only access.
/// </summary>
public static class RepairV1EndpointsTests
{
    private const string Package = "eu.kanade.tachiyomi.extension.en.example";
    private const string Root = "/api/v1/source-maintenance";

    public static void Register(TestSuite suite)
    {
        suite.Add("repair v1: retired worker setup answers 410; control and provider report the in-app state; unknown actions 404", () =>
        {
            using var setup = new Setup();
            Check.Equal(HttpStatusCode.Gone, setup.Send(HttpMethod.Put, Root + "/connection", new JsonObject { ["url"] = "http://worker", ["token"] = "x" }).StatusCode);
            Check.Equal(HttpStatusCode.Gone, setup.Send(HttpMethod.Post, Root + "/setup-preview", new JsonObject()).StatusCode);
            Check.Equal(HttpStatusCode.Gone, setup.Send(HttpMethod.Get, Root + "/host-setup").StatusCode);
            var gone = Body(setup.Send(HttpMethod.Get, Root + "/host-setup"));
            Check.True(gone["detail"]!.GetValue<string>().Contains("inside this AutoSourcarr instance", StringComparison.Ordinal), "410 explains why");
            var connection = Body(setup.Send(HttpMethod.Get, Root + "/connection"), HttpStatusCode.OK);
            Check.Equal("in-app", connection["mode"]!.GetValue<string>());
            Check.Equal(false, connection["external_worker"]!.GetValue<bool>());
            var control = Body(setup.Send(HttpMethod.Get, Root + "/control"), HttpStatusCode.OK);
            Check.Equal(true, control["domain_repair"]!.GetValue<bool>());
            Check.Equal("not-checked", control["isolation"]!["reason"]!.GetValue<string>());
            var provider = Body(setup.Send(HttpMethod.Get, Root + "/provider"), HttpStatusCode.OK);
            Check.Equal("not-configured", provider["state"]!.GetValue<string>());
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, Root + "/provider/test").StatusCode);
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, Root + "/control/login").StatusCode);
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, Root + "/control/check").StatusCode);
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Post, Root + "/control/format-disk").StatusCode);
            // Raw-code storage is not wired in this host: 503, not 500.
            Check.Equal(HttpStatusCode.ServiceUnavailable, setup.Send(HttpMethod.Get, Root + "/raws/" + Package).StatusCode);
        });

        suite.Add("repair v1: control/check runs the runner self-check when one is wired", () =>
        {
            using var setup = new Setup(runnerCheck: _ => Task.FromResult(new JsonObject { ["available"] = true, ["message"] = "runner ok" }));
            var check = Body(setup.Send(HttpMethod.Post, Root + "/control/check"), HttpStatusCode.Accepted);
            Check.Equal("runner ok", check["message"]!.GetValue<string>());
            Check.Equal("check", check["action"]!.GetValue<string>());
        });

        suite.Add("repair v1: jobs create answers 202 with the task id; the job, its report and the task projection are found by it; unknown jobs 404", () =>
        {
            using var setup = new Setup();
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Post, Root + "/jobs", new JsonObject { ["pkg_name"] = "../escape" }).StatusCode);
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Post, Root + "/jobs", new JsonObject { ["pkg_name"] = Package, ["new_host"] = 7 }).StatusCode);
            // The fake host does not have the package installed, so the repair ends refused (nothing is installed).
            var created = Body(setup.Send(HttpMethod.Post, Root + "/jobs", new JsonObject { ["pkg_name"] = Package, ["issue"] = "site moved" }), HttpStatusCode.Accepted);
            var id = created["id"]!.GetValue<string>();
            Check.Equal(id, created["task_id"]!.GetValue<string>());
            Check.Equal("domain", created["kind"]!.GetValue<string>());
            setup.WaitFinished(id);
            var job = Body(setup.Send(HttpMethod.Get, Root + "/jobs/" + id), HttpStatusCode.OK);
            Check.Equal("refused", job["state"]!.GetValue<string>());
            Check.Equal(Package, job["package"]!.GetValue<string>());
            Check.Equal("refused", job["report"]!["state"]!.GetValue<string>());
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Get, Root + "/jobs/ffffffffffff").StatusCode);
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Get, Root + "/jobs/not-a-job").StatusCode);
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Post, Root + "/jobs/ffffffffffff/cancel").StatusCode);
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Get, Root + "/jobs/" + id + "/inputs").StatusCode);
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Get, Root + "/jobs/" + id + "/code").StatusCode);
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, Root + "/jobs/" + id + "/install").StatusCode);
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, Root + "/jobs/" + id + "/cancel").StatusCode);
            var discarded = Body(setup.Send(HttpMethod.Post, Root + "/jobs/" + id + "/discard"), HttpStatusCode.OK);
            Check.Equal(false, discarded["queued"]!.GetValue<bool>());
            // A general task id is not a repair job.
            var other = setup.Tasks.Create("Something else", kind: "database-move");
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Get, Root + "/jobs/" + other).StatusCode);

            var tasks = Body(setup.Send(HttpMethod.Get, "/api/v1/tasks"), HttpStatusCode.OK);
            var row = tasks["items"]!.AsArray().OfType<JsonObject>().Single(item => item["id"]!.GetValue<string>() == id);
            Check.Equal("source-repair", row["kind"]!.GetValue<string>());
            Check.Equal(SourceRepairV1Endpoints.DomainRepairJob.Name, row["repair_job_type"]!.GetValue<string>());
            Check.Equal(Package, row["package"]!.GetValue<string>());
            Check.Equal("refused", row["repair_state"]!.GetValue<string>());
            Check.Equal(id, row["repair_id"]!.GetValue<string>());
            Check.Equal("Repair source domain " + Package, row["name"]!.GetValue<string>());
            Check.Equal(1, tasks["source_repairs"]!["count"]!.GetValue<int>());
            var general = tasks["items"]!.AsArray().OfType<JsonObject>().Single(item => item["id"]!.GetValue<string>() == other);
            Check.Equal("database-move", general["kind"]!.GetValue<string>());
        });

        suite.Add("repair v1: rollback delegates to the domain repair and installs the kept jar as a job", () =>
        {
            using var setup = new Setup();
            setup.Host.Installed = true;
            var kept = setup.Domain.KeepLastWorkingAsync(Package).GetAwaiter().GetResult();
            Check.True(kept is not null, "working jar kept");
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, Root + "/rollback").StatusCode); // no body and no installed repair record
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Post, Root + "/rollback", new JsonObject { ["pkg_name"] = "bad name" }).StatusCode);
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Post, Root + "/rollback", new JsonObject { ["pkg_name"] = Package + "other" }).StatusCode);
            var queued = Body(setup.Send(HttpMethod.Post, Root + "/rollback", new JsonObject { ["pkg_name"] = Package }), HttpStatusCode.Accepted);
            var id = queued["task_id"]!.GetValue<string>();
            setup.WaitFinished(id);
            Check.Equal(1, setup.Host.Installs.Count);
            Check.True(setup.Host.Installs[0].AsSpan().SequenceEqual(FakeHost.Jar), "the kept jar is installed again");
            Check.Equal("rolled_back", setup.Domain.LastReport(Package)!["state"]!.GetValue<string>());
            var job = Body(setup.Send(HttpMethod.Get, Root + "/jobs/" + id), HttpStatusCode.OK);
            Check.Equal("rollback", job["kind"]!.GetValue<string>());
            Check.Equal("rolled-back", job["state"]!.GetValue<string>());
            // The kept jar is the job's input download.
            var inputs = setup.Send(HttpMethod.Get, Root + "/jobs/" + id + "/inputs");
            Check.Equal(HttpStatusCode.OK, inputs.StatusCode);
            Check.Equal("application/zip", inputs.Content.Headers.ContentType!.MediaType);
            Check.True(inputs.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult().AsSpan().SequenceEqual(FakeHost.Jar), "kept jar downloaded");
        });

        suite.Add("repair v1: feedback is checked against the installed repair and its file stays bounded", () =>
        {
            using var setup = new Setup();
            var created = Body(setup.Send(HttpMethod.Post, Root + "/jobs", new JsonObject { ["pkg_name"] = Package }), HttpStatusCode.Accepted);
            var id = created["id"]!.GetValue<string>();
            setup.WaitFinished(id);
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Post, Root + "/jobs/ffffffffffff/feedback", new JsonObject { ["action"] = "fixed" }).StatusCode);
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Post, Root + "/jobs/" + id + "/feedback", new JsonObject { ["action"] = "delete" }).StatusCode);
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, Root + "/jobs/" + id + "/feedback",
                new JsonObject { ["action"] = "fixed", ["installed_sha256"] = new string('a', 64) }).StatusCode);
            Check.True(!File.Exists(Path.Combine(setup.Maintenance, "feedback.jsonl")), "refused feedback is not stored");

            var text = new string('x', RepairV1Endpoints.FeedbackTextLimit);
            for (var index = 0; index < 200; index++)
                RepairV1Endpoints.AppendFeedback(setup.Maintenance, new JsonObject { ["n"] = index, ["feedback"] = text });
            var path = Path.Combine(setup.Maintenance, "feedback.jsonl");
            Check.True(new FileInfo(path).Length <= RepairV1Endpoints.FeedbackFileLimit, "feedback file bounded: " + new FileInfo(path).Length);
            var lines = File.ReadAllLines(path);
            Check.True(lines.Length is > 10 and < 200, "oldest lines dropped: " + lines.Length);
            Check.Equal(199, JsonNode.Parse(lines[^1])!["n"]!.GetValue<int>());
            Check.Equal(200 - lines.Length, JsonNode.Parse(lines[0])!["n"]!.GetValue<int>());
        });

        suite.Add("repair v1: every source-maintenance route needs an admin; the access policy keeps them admin-only", () =>
        {
            using var setup = new Setup();
            var routes = setup.Server.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Where(endpoint => (endpoint.RoutePattern.RawText ?? "").StartsWith(Root, StringComparison.Ordinal))
                .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                    .Select(method => (Method: method, Path: endpoint.RoutePattern.RawText!
                        .Replace("{pkgName}", Package, StringComparison.Ordinal).Replace("{package}", Package, StringComparison.Ordinal)
                        .Replace("{jobId}", "ffffffffffff", StringComparison.Ordinal).Replace("{action}", "check", StringComparison.Ordinal))))
                .Distinct().ToArray();
            // 23 repair routes and 5 inventory routes (snapshot, watch, installed, rollback, original APK).
            Check.Equal(28, routes.Length);
            using var anonymous = setup.Server.CreateClient();
            foreach (var (method, path) in routes)
            {
                Check.True(AccessPolicy.Required(method, path) == AccessLevel.Admin, "admin-only: " + method + " " + path);
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (method is "POST" or "PUT") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var response = anonymous.SendAsync(request).GetAwaiter().GetResult();
                Check.True(response.StatusCode == HttpStatusCode.Unauthorized, method + " " + path + " without the admin token: " + (int)response.StatusCode);
            }
            Check.True(setup.Host.Calls == 0, "refused before any handler ran");
        });
    }

    private static JsonObject Body(HttpResponseMessage response, HttpStatusCode? expected = null)
    {
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (expected is { } status) Check.True(response.StatusCode == status, $"expected {status}, got {response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _temp = new();
        private readonly SuwayomiClient _client;
        private readonly IHost _host;
        public TaskRegistry Tasks { get; } = new();
        public JobQueue Jobs { get; }
        public FakeHost Host { get; } = new();
        public DomainRepair Domain { get; }
        public string Maintenance { get; }
        public TestServer Server { get; }
        public HttpClient Client { get; }

        public Setup(Func<CancellationToken, Task<JsonObject>>? runnerCheck = null)
        {
            var settings = new AutoSourcarrSettings { AdminToken = "admin", SourceEngineMode = "integrated" };
            Maintenance = Path.Combine(_temp.Path, "data", "source-maintenance");
            Jobs = new JobQueue(Tasks);
            var adminLock = new SemaphoreSlim(1, 1);
            Domain = new DomainRepair(Maintenance, Host, adminLock);
            _client = SuwayomiClient.ForSourceHost(new AutoSourcarrSettings { SourceEngineMode = "integrated" }, "http://127.0.0.1:4567", "token", handler: new NoNetwork());
            var services = new ApiV1Services(() => settings, _temp.Path, null)
            {
                Tasks = Tasks, Jobs = Jobs,
                SourceRepair = new SourceRepairServices { Domain = Domain },
                Repair = new RepairV1Services { MaintenanceRoot = Maintenance, RunnerCheck = runnerCheck },
                SourceInventory = new SourceInventoryServices
                {
                    Orphans = new SourceOrphans(Maintenance, _client, new NoFetcher()), Maintenance = new SourceMaintenanceState(Maintenance, _temp.Path),
                    Client = _client, AdminLock = adminLock,
                },
            };
            _host = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
                .ConfigureServices(collection => collection.AddRouting())
                .Configure(app => { app.UseRouting(); app.UseEndpoints(endpoints => endpoints.MapApiV1(services)); })).Build();
            _host.StartAsync().GetAwaiter().GetResult();
            Server = _host.GetTestServer();
            Client = Server.CreateClient();
            Client.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
        }

        public HttpResponseMessage Send(HttpMethod method, string path, JsonObject? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return Client.SendAsync(request).GetAwaiter().GetResult();
        }

        public void WaitFinished(string id) =>
            Check.True(SpinWait.SpinUntil(() => Tasks.Find(id) is { State: not "running" }, TimeSpan.FromSeconds(10)), "job " + id + " finished");

        public void Dispose()
        {
            Client.Dispose();
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
            Jobs.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _client.Dispose();
            _temp.Dispose();
        }
    }

    /// <summary>A source host with one package (installed on demand) that records installs; the content check is never reached.</summary>
    private sealed class FakeHost : IDomainRepairHost, IContentCheckSource
    {
        public static readonly byte[] Jar = Encoding.ASCII.GetBytes("PK\u0003\u0004 kept working jar for the rollback test");
        public bool Installed { get; set; }
        public int Calls;
        public List<byte[]> Installs { get; } = [];
        public IContentCheckSource Content => this;

        private JsonObject Row() => new()
        {
            ["pkgName"] = Package, ["versionName"] = "1.4.7", ["isInstalled"] = Installed, ["loadError"] = null,
            ["source"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["id"] = "123", ["name"] = "Example", ["lang"] = "en", ["baseUrl"] = "https://old.example" }) },
        };
        public Task<JsonObject?> ExtensionAsync(string packageName, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return Task.FromResult<JsonObject?>(packageName == Package ? Row() : null); }
        public Task<byte[]> ReadJarAsync(string packageName, CancellationToken cancellationToken) => Task.FromResult((byte[])Jar.Clone());
        public Task<JsonObject> InstallJarAsync(string packageName, byte[] jar, CancellationToken cancellationToken)
        { lock (Installs) Installs.Add(jar); return Task.FromResult(Row()); }
        public Task<JsonObject> BrowseAsync(string sourceId, string mode, string query, CancellationToken cancellationToken) => throw new InvalidOperationException("not reached");
        public Task<JsonObject> DetailsAsync(long mangaId, CancellationToken cancellationToken) => throw new InvalidOperationException("not reached");
        public Task<List<string>> PagesAsync(long chapterId, CancellationToken cancellationToken) => throw new InvalidOperationException("not reached");
        public Task<(byte[] Bytes, string ContentType)> FirstImageAsync(long mangaId, long chapterId, CancellationToken cancellationToken) => throw new InvalidOperationException("not reached");
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No source host in this test: " + request.RequestUri);
    }

    private sealed class NoFetcher : IPublicSourceFetcher
    {
        public Task<PublicSourceResponse> FetchAsync(PublicSourceRequest request, CancellationToken cancellationToken = default) =>
            throw new IOException("No public fetches in this test");
    }
}
