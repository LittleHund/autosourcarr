using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests;

public static class RawApiTests
{
    private const string Package = "eu.kanade.tachiyomi.extension.en.example", Root = "/api/admin/source-maintenance/raws/" + Package;
    public static void Register(TestSuite suite)
    {
        suite.Add("raw HTTP: admin authorization and upload bound precede body/filesystem work", () =>
        {
            using var setup = new Setup();
            Check.Equal(HttpStatusCode.Unauthorized, setup.Client.GetAsync(Root).GetAwaiter().GetResult().StatusCode); Check.Equal(0, setup.Installed.Calls);
            var context = setup.Server.SendAsync(context =>
            {
                context.Request.Method = "POST"; context.Request.Path = Root + "/project"; context.Request.Headers["X-Admin-Token"] = "admin";
                context.Request.Headers["X-Raw-Revision"] = "1"; context.Request.Headers["X-Source-Version"] = "1.4.23"; context.Request.ContentLength = RawCodeRules.MaximumZip + 1; context.Request.Body = new NoRead();
            }).GetAwaiter().GetResult();
            Check.Equal(413, context.Response.StatusCode); Check.Equal(0, setup.Installed.Calls); Check.Equal(0, setup.Unexpected);
            using var reader = new HttpRequestMessage(HttpMethod.Get, Root); reader.Headers.Add("X-AutoSourcarr-Api-Key", "reader");
            Check.Equal(HttpStatusCode.Unauthorized, setup.Client.SendAsync(reader).GetAwaiter().GetResult().StatusCode);
        });
        suite.Add("raw HTTP: private upload, portable download and stale request conflict preserve selected code", () =>
        {
            using var setup = new Setup(); var status = Body(setup.Send(HttpMethod.Get, Root)); Check.Equal("needs-input", status["state"]!.GetValue<string>());
            var revision = status["revision"]!.GetValue<long>(); var upload = setup.Upload(revision, "1.4.23"); Check.Equal(HttpStatusCode.OK, upload.StatusCode); var saved = Body(upload);
            Check.Equal("private", saved["visibility"]!.GetValue<string>()); Check.Equal(0, setup.Shared.Versions(Package).Count);
            var code = setup.Send(HttpMethod.Get, Root + "/code"); Check.Equal(HttpStatusCode.OK, code.StatusCode); Check.Equal("no-store", code.Headers.CacheControl!.ToString());
            Check.Equal("application/zip", code.Content.Headers.ContentType!.MediaType); Check.True(code.Content.Headers.ContentDisposition!.FileName!.Contains("AutoSourcarr-source-code.zip", StringComparison.Ordinal));
            var meta = RawArchive.PortableMetadata(code.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult())!; Check.Equal(Package, meta["package"]!.GetValue<string>());
            Check.Equal(HttpStatusCode.Conflict, setup.Upload(revision, "1.4.23").StatusCode); Check.Equal(saved["raw_id"]!.GetValue<string>(), setup.Catalog.Status(Package)!["raw_id"]!.GetValue<string>());
            Check.Equal(HttpStatusCode.Conflict, setup.Upload(saved["revision"]!.GetValue<long>(), "1.4.22").StatusCode); Check.Equal(0, setup.Unexpected);
        });
        suite.Add("raw HTTP: retained repair code is adopted by matching binary after restart", () =>
        {
            using var setup = new Setup(); var row = Fixture(); var value = setup.Private.PutZip(Convert.FromBase64String(row["payload"]!.GetValue<string>()), row["metadata"]!.AsObject()); var id = value["id"]!.GetValue<string>();
            setup.History.Observe(Package, new JsonObject { ["binary_sha256"] = setup.Installed.Binary, ["raw_id"] = id, ["version"] = "1.4.23", ["entrypoint"] = null, ["extension_lib"] = null, ["source_ids"] = new JsonArray("123"), ["identity_sha256"] = null, ["job_id"] = null });
            var status = Body(setup.Send(HttpMethod.Get, Root)); Check.Equal("available", status["state"]!.GetValue<string>()); Check.Equal(id, status["raw_id"]!.GetValue<string>()); Check.Equal(0, setup.Repository.Calls);
        });
        suite.Add("raw HTTP: explicit input reset, discovery and supplied-origin validation keep revision guards", () =>
        {
            using var setup = new Setup(); var first = Body(setup.Send(HttpMethod.Get, Root)); var revision = first["revision"]!.GetValue<long>();
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, Root + "/request-input", new JsonObject { ["revision"] = revision + 1 }).StatusCode);
            var reset = setup.Send(HttpMethod.Post, Root + "/request-input", new JsonObject { ["revision"] = revision }); Check.Equal(HttpStatusCode.OK, reset.StatusCode); revision = Body(reset)["revision"]!.GetValue<long>();
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Post, Root + "/discover", new JsonObject { ["revision"] = revision, ["extra"] = true }).StatusCode);
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Post, Root + "/origin", new JsonObject { ["revision"] = revision, ["origin"] = new JsonObject { ["repository"] = "https://github.com/a/b/tree/main" } }).StatusCode);
            var discovered = setup.Send(HttpMethod.Post, Root + "/discover", new JsonObject { ["revision"] = revision }); Check.Equal(HttpStatusCode.Accepted, discovered.StatusCode);
            Check.True(Body(discovered)["message"]!.GetValue<string>().Contains("queued", StringComparison.Ordinal));
            Check.True(SpinWait.SpinUntil(() => setup.Catalog.Status(Package)!["revision"]!.GetValue<long>() > revision, TimeSpan.FromSeconds(3)));
            revision = setup.Catalog.Status(Package)!["revision"]!.GetValue<long>();
            var origin = setup.Send(HttpMethod.Post, Root + "/origin", new JsonObject { ["revision"] = revision, ["origin"] = new JsonObject { ["repository"] = SourceOrigin.KeiyoushiSource, ["module"] = "src/en/example" } });
            Check.Equal(HttpStatusCode.Accepted, origin.StatusCode); Check.Equal("acquiring", Body(origin)["state"]!.GetValue<string>()); Check.Equal(0, setup.Commands.Calls); Check.Equal(0, setup.Unexpected);
        });
        suite.Add("raw HTTP: unavailable choice requires isolation readiness and current installed bytes", () =>
        {
            using var setup = new Setup(); var first = Body(setup.Send(HttpMethod.Get, Root)); var revision = first["revision"]!.GetValue<long>();
            setup.Commands.Ready = false; Check.Equal(HttpStatusCode.ServiceUnavailable, setup.Send(HttpMethod.Post, Root + "/unavailable", new JsonObject { ["revision"] = revision }).StatusCode); Check.Equal(0, setup.Commands.Calls);
            setup.Commands.Ready = true; Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Post, Root + "/unavailable", new JsonObject { ["revision"] = revision, ["job_id"] = "bad" }).StatusCode);
            setup.Installed.Binary = new string('2', 64); Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, Root + "/unavailable", new JsonObject { ["revision"] = revision }).StatusCode); Check.Equal(0, setup.Commands.Calls);
            setup.Installed.Binary = new string('1', 64); var accepted = setup.Send(HttpMethod.Post, Root + "/unavailable", new JsonObject { ["revision"] = revision });
            Check.Equal(HttpStatusCode.Accepted, accepted.StatusCode); Check.Equal(1, setup.Commands.Calls); Check.True(setup.Commands.LastPrevious is null); Check.Equal(setup.Installed.Binary, setup.Commands.LastBinary); Check.Equal(0, setup.Unexpected);
        });
        suite.Add("raw HTTP: unavailable storage or missing installation cannot start acquisitions", () =>
        {
            using (var setup = new Setup(storageFailure: true)) { Check.Equal(HttpStatusCode.ServiceUnavailable, setup.Send(HttpMethod.Get, Root).StatusCode); Check.Equal(0, setup.Installed.Calls); Check.Equal(0, setup.Repository.Calls); }
            using (var setup = new Setup()) { setup.Installed.IsInstalled = false; Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Get, Root).StatusCode); Check.Equal(0, setup.Repository.Calls); }
        });
        suite.Add("raw HTTP: upload metadata is route-only and authorization/header guards leave the body untouched", () =>
        {
            using var setup = new Setup();
            foreach (var mode in new[] { "unauthorized", "reader", "missing-headers", "oversized", "unrelated" })
            {
                var feature = new UploadLimit(); using var body = new NoRead();
                var context = setup.Server.SendAsync(context =>
                {
                    context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature); context.Request.Method = "POST";
                    context.Request.Path = Root + (mode == "unrelated" ? "/request-input" : "/project"); context.Request.Body = body;
                    if (mode == "reader") context.Request.Headers["X-AutoSourcarr-Api-Key"] = "reader";
                    if (mode is "missing-headers" or "oversized") context.Request.Headers["X-Admin-Token"] = "admin";
                    if (mode == "oversized")
                    {
                        context.Request.Headers["X-Raw-Revision"] = "1"; context.Request.Headers["X-Source-Version"] = "1.4.23";
                        context.Request.ContentLength = RawCodeRules.MaximumZip + 1L;
                    }
                }).GetAwaiter().GetResult();
                Check.Equal(mode == "missing-headers" ? 400 : mode == "oversized" ? 413 : 401, context.Response.StatusCode);
                var metadata = context.GetEndpoint()!.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
                if (mode == "unrelated") Check.True(metadata is null); else Check.True(metadata is not null && metadata.MaxRequestBodySize is null);
                // ASP.NET applies route metadata before endpoint filters. This is
                // a feature assignment, not authorization or an entity-body read.
                Check.Equal(mode == "unrelated" ? 0 : 1, feature.Writes);
                if (mode == "unrelated") Check.Equal(30_000_000L, feature.MaxRequestBodySize); else Check.True(feature.MaxRequestBodySize is null);
                Check.Equal(0, body.Reads); Check.Equal(0, setup.Installed.Calls);
            }
            Check.Equal(0, setup.Unexpected);
        });
        suite.Add("raw HTTP: unknown-length uploads consume at most32MiB plus one before real catalogue work", () =>
        {
            using var setup = new Setup();
            foreach (var extra in new[] { 0, 1 })
            {
                var feature = new UploadLimit(); var calls = setup.Installed.Calls;
                using var body = new UploadSizeStream(RawCodeRules.MaximumZip + (long)extra, () =>
                { Check.True(feature.MaxRequestBodySize is null); Check.Equal(calls, setup.Installed.Calls); });
                var context = setup.Server.SendAsync(context =>
                {
                    context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature); context.Request.Method = "POST"; context.Request.Path = Root + "/project";
                    context.Request.Headers["X-Admin-Token"] = "admin"; context.Request.Headers["X-Raw-Revision"] = "1"; context.Request.Headers["X-Source-Version"] = "1.4.23";
                    context.Request.Body = body;
                }).GetAwaiter().GetResult();
                // Exact-size synthetic bytes reach the actual catalogue's conflict/ZIP validation, not a false successful upload.
                Check.Equal(extra == 0 ? 409 : 413, context.Response.StatusCode); Check.Equal(RawCodeRules.MaximumZip + (long)extra, body.BytesRead);
                Check.Equal(2, feature.Writes); Check.True(feature.MaxRequestBodySize is null); // framework metadata, then authorized bounded reader
                Check.Equal(extra == 0 ? calls + 2 : calls, setup.Installed.Calls); Check.Equal(0, setup.Private.Versions(Package).Count);
            }
            Check.Equal(0, setup.Unexpected);
        });
    }
    private static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("raw-code.json")))!.AsArray().OfType<JsonObject>().Single(row => row["name"]!.GetValue<string>() == "compression-8");
    private static JsonObject Body(HttpResponseMessage response) => JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _sandbox = new(); private readonly SemaphoreSlim _admin = new(1, 1); private readonly BoundedWorkScheduler _io = new(2, 16); private readonly WholeTaskCoordinator _lanes;
        private readonly SourceRawService _service; private readonly IHost _host;
        public Installed Installed { get; } = new(); public Repository Repository { get; } = new(); public Commands Commands { get; } = new();
        public RawStore Shared { get; } public RawStore Private { get; } public SourceRawCatalog Catalog { get; } public RepairHistory History { get; }
        public TestServer Server { get; } public HttpClient Client { get; } public int Unexpected;
        public Setup(bool storageFailure = false)
        {
            var tasks = new TaskRegistry(); var settings = new AutoSourcarrSettings { AdminToken = "admin", AutosourcarrReaderApiToken = "reader", BackgroundIdleSeconds = .01 }; var privacy = new SupportPrivacy();
            _lanes = new(settings, tasks); Shared = new(Path.Combine(_sandbox.Path, "shared"), true); Private = new(Path.Combine(_sandbox.Path, "raw-private"));
            Catalog = new(Shared, Private, new PrivateDocumentStore(Path.Combine(_sandbox.Path, "raw-requests")), Repository, new Fetch()); History = new(new PrivateDocumentStore(Path.Combine(_sandbox.Path, "history")));
            _service = new(() => storageFailure ? throw new InvalidDataException("Damaged private source storage") : Catalog, Installed, _admin, () => [], () => [], _io, _lanes, tasks, privacy, _ => { }, History);
            _service.StartAsync().GetAwaiter().GetResult();
            _host = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer().ConfigureServices(services => services.AddRouting()).Configure(app =>
            {
                app.UseAutoSourcarrHttp("0.4.3-migration", () => settings, (_, _) => { Unexpected++; return "unexpected"; }, new RequestAdmissionGate(), _lanes, tasks, privacy, _ => { });
                app.UseEndpoints(endpoints => endpoints.MapSourceRaws(_service, Installed, Commands, () => settings, _admin, _io, History));
            })).Build();
            _host.StartAsync().GetAwaiter().GetResult(); Server = _host.GetTestServer(); Client = Server.CreateClient();
        }
        public HttpResponseMessage Send(HttpMethod method, string path, JsonObject? body = null)
        {
            using var request = new HttpRequestMessage(method, path); request.Headers.Add("X-Admin-Token", "admin"); if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return Client.SendAsync(request).GetAwaiter().GetResult();
        }
        public HttpResponseMessage Upload(long revision, string version)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Root + "/project"); request.Headers.Add("X-Admin-Token", "admin"); request.Headers.Add("X-Raw-Revision", revision.ToString(System.Globalization.CultureInfo.InvariantCulture)); request.Headers.Add("X-Source-Version", version);
            request.Content = new ByteArrayContent(Convert.FromBase64String(Fixture()["payload"]!.GetValue<string>())); return Client.SendAsync(request).GetAwaiter().GetResult();
        }
        public void Dispose() { Client.Dispose(); _host.Dispose(); _service.DisposeAsync().AsTask().GetAwaiter().GetResult(); _lanes.DisposeAsync().AsTask().GetAwaiter().GetResult(); _io.DisposeAsync().AsTask().GetAwaiter().GetResult(); _admin.Dispose(); _sandbox.Dispose(); }
    }
    private sealed class Installed : IRawInstalledSources
    {
        public bool Ready => false; public bool IsInstalled = true; public string Binary = new('1', 64); public int Calls;
        private JsonObject Row => new() { ["pkgName"] = Package, ["versionName"] = "1.4.23", ["isInstalled"] = IsInstalled, ["orphan_reason"] = "manual-upload" };
        public Task<IReadOnlyList<JsonObject>> InstalledAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult<IReadOnlyList<JsonObject>>([Row]); }
        public Task<RawInstalledSource?> ObserveAsync(string package, string version, CancellationToken cancellationToken) { Calls++; return Task.FromResult<RawInstalledSource?>(new(Row, Binary, new string('3', 64), ["123"])); }
    }
    private sealed class Repository : IRawRepositorySource
    { public int Calls; public Task<JsonObject> ObtainAsync(JsonObject origin, CancellationToken cancellationToken = default) { Calls++; throw new IOException("The original public repository is unavailable"); } }
    private sealed class Fetch : IPublicSourceFetcher
    { public Task<PublicSourceResponse> FetchAsync(PublicSourceRequest request, CancellationToken cancellationToken = default) => throw new IOException("No source declaration fixture"); }
    private sealed class Commands : IRawReconstructionCommands
    {
        public bool Ready = true; public int Calls; public string LastBinary = ""; public string? LastPrevious;
        public Task EnsureReadyAsync(CancellationToken cancellationToken) { if (!Ready) throw new ApiProblem(503, "Repair isolation is unavailable"); return Task.CompletedTask; }
        public Task<JsonObject> ReconstructAsync(RawInstalledSource installed, long rawRevision, string? previousJobId, CancellationToken cancellationToken)
        { Calls++; LastBinary = installed.BinarySha256; LastPrevious = previousJobId; return Task.FromResult(new JsonObject { ["id"] = new string('a', 32), ["state"] = "awaiting-raws" }); }
    }
    private sealed class NoRead : Stream
    {
        public int Reads; public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private Exception RejectedRead() { Reads++; return new InvalidOperationException("Bounded upload must not read the rejected body"); }
        public override int Read(byte[] buffer, int offset, int count) => throw RejectedRead();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(RejectedRead());
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(RejectedRead());
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class UploadLimit : IHttpMaxRequestBodySizeFeature
    {
        private long? _maximum = 30_000_000; public int Writes; public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get => _maximum; set { Writes++; _maximum = value; } }
    }
    private sealed class UploadSizeStream(long remaining, Action? beforeRead = null) : Stream
    {
        public long BytesRead; public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { beforeRead?.Invoke(); cancellationToken.ThrowIfCancellationRequested(); var count = (int)Math.Min(buffer.Length, remaining); buffer.Span[..count].Clear(); remaining -= count; BytesRead += count; return ValueTask.FromResult(count); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
