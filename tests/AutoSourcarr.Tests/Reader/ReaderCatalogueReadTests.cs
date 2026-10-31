using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Imaging;
using AutoSourcarr.Core.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests;

public static class ReaderCatalogueReadTests
{
    private const long Big = 9007199254740993;
    private static readonly Lazy<JsonObject> Corpus = new(() =>
    {
        var bytes = File.ReadAllBytes(ConfigurationTests.Fixture("reader-catalogue-read.json")); Check.True(bytes.Length < 4 * 1024 * 1024);
        Check.Equal("eefc5aa068e1e2802dd5bf5a012ec9d8c9f306632c0ede219f37fc10348f2e67", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var value = JsonNode.Parse(bytes)!.AsObject(); Check.Equal(1, value["format"]!.GetValue<int>()); Check.Equal(20, value["cases"]!.AsArray().Count);
        Check.Equal(82, value["reused_query_cases"]!.GetValue<int>()); Check.Equal(151, value["reused_display_transitions"]!.GetValue<int>());
        return value;
    });
    public static void Register(TestSuite suite)
    {
        suite.Add("reader catalogue read:20 original SQL-overlay-synopsis outputs leave actual SQLite unchanged", () => Oracle().GetAwaiter().GetResult());
        suite.Add("reader catalogue read:actual raster cache and controlled fill supply factual cover presence", () => Covers().GetAwaiter().GetResult());
        suite.Add("reader catalogue read:real HTTP owner preserves auth HEAD aliases exact IDs and404", () => Http().GetAwaiter().GetResult());
        suite.Add("reader catalogue read:current source display and alias changes never become native evidence", () => Current().GetAwaiter().GetResult());
        suite.Add("reader catalogue read:cancellation joins actual source gate and SQLite writer before releasing I/O", () => Physical().GetAwaiter().GetResult());
    }
    private static async Task Oracle()
    {
        var count = 0; var missing = 0;
        foreach (var node in Corpus.Value["cases"]!.AsArray())
        {
            var value = node!.AsObject(); var input = value["input"]!.AsObject();
            try
            {
                // Retain every original state as evidence, while comparing only
                // this C# instance's own initialization/state around its read.
                Equal(value["before"]!, value["after"]!);
                Check.Equal(0L, value["attempted_total_changes"]!["catalog"]!.GetValue<long>()); Check.Equal(0L, value["attempted_total_changes"]!["origins"]!.GetValue<long>());
                using var f = new Fixture(input); var before = f.State();
                if (value["error"] is not null)
                {
                    Check.Equal("KeyError", Text(value["error"]!, "type"));
                    try { await f.Reader.EntryAsync(input["entry_id"]!.GetValue<long>(), CancellationToken.None); throw new InvalidOperationException("Missing original entry became successful"); }
                    catch (KeyNotFoundException) { missing++; }
                }
                else if (input["operation"]?.GetValue<string>() == "sources") Equal(value["result"]!, await f.Reader.SourcesAsync(CancellationToken.None));
                else
                {
                    var id = input["entry_id"]!.GetValue<long>(); var raw = f.Queries.Entry(id); Equal(value["raw_entry"]!, raw);
                    var original = raw.DeepClone(); Equal(value["display_entry"]!, f.Display.Overlay(raw)); Equal(original, raw);
                    Equal(value["result"]!, await f.Reader.EntryAsync(id, CancellationToken.None));
                }
                Equal(before, f.State()); count++;
            }
            catch (Exception error) { throw new InvalidOperationException(Text(input, "name"), error); }
        }
        Check.Equal(20, count); Check.Equal(2, missing);
    }
    private static async Task Covers()
    {
        using var f = new Fixture(); Check.True((await f.Reader.EntryAsync(11, CancellationToken.None))["cover_cached"]!.GetValue<bool>());
        Check.True(!(await f.Reader.EntryAsync(13, CancellationToken.None))["cover_cached"]!.GetValue<bool>());
        var before = f.State(); var entered = Signal(); var release = Signal(); var loads = 0;
        async Task<CoverData> Load(CancellationToken cancellationToken)
        { Interlocked.Increment(ref loads); entered.TrySetResult(); await release.Task.ConfigureAwait(false); return f.Image; }
        var fill = f.Cache.GetAsync("entry-13", Load, TimeSpan.FromSeconds(5));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check.True(!(await f.Reader.EntryAsync(13, CancellationToken.None))["cover_cached"]!.GetValue<bool>());
            release.TrySetResult(); var image = await fill; Check.True(image is not null && image.Content.Span.SequenceEqual(f.Image.Content.Span)); Check.Equal(1, loads);
            Check.True((await f.Reader.EntryAsync(13, CancellationToken.None))["cover_cached"]!.GetValue<bool>());
            Check.Equal(2, f.Cache.ClearKeys(["entry-11"])); Check.True(!(await f.Reader.EntryAsync(11, CancellationToken.None))["cover_cached"]!.GetValue<bool>());
            Equal(before, f.State());
        }
        finally { release.TrySetResult(); await fill; }
    }
    private static async Task Http()
    {
        using var f = new Fixture(); using var pipeline = new Pipeline(f); using var client = pipeline.Server.CreateClient(); var before = f.State();
        // A missing/wrong token must be rejected before a real read can wait for the source gate.
        lock (f.Gate)
        {
            Check.Equal(HttpStatusCode.Unauthorized, client.GetAsync("/api/reader/v1/entries/11").WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult().StatusCode);
            Check.Equal(0, f.Io.Snapshot.Running);
        }
        using (var wrong = Request(HttpMethod.Get, "/api/reader/v1/sources", "wrong")) Check.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);
        using (var queryToken = await client.GetAsync("/api/reader/v1/sources?token=reader")) Check.Equal(HttpStatusCode.Unauthorized, queryToken.StatusCode);
        using (var response = await Send(client, HttpMethod.Get, "/api/reader/v1/sources"))
        { Check.Equal(HttpStatusCode.OK, response.StatusCode); Equal(await f.Reader.SourcesAsync(CancellationToken.None), await Body(response)); }
        using (var response = await Send(client, HttpMethod.Get, "/api/reader/v1/entries/90"))
        {
            Check.Equal(HttpStatusCode.OK, response.StatusCode); var body = await Body(response);
            Check.Equal(Big, body["id"]!.GetValue<long>()); Check.Equal(Big, body["canonical_id"]!.GetValue<long>()); Check.Equal("90", Text(body, "url")); Check.Equal("/api/v1/covers/90", Text(body, "cover_url"));
        }
        using (var response = await Send(client, HttpMethod.Get, "/api/reader/v1/entries/9007199254740993"))
        { Check.Equal(HttpStatusCode.OK, response.StatusCode); Check.Equal(Big, (await Body(response))["id"]!.GetValue<long>()); }
        using (var response = await Send(client, HttpMethod.Head, "/api/reader/v1/entries/11"))
        { Check.Equal(HttpStatusCode.OK, response.StatusCode); Check.Equal(0, (await response.Content.ReadAsByteArrayAsync()).Length); }
        foreach (var id in new[] { "999999", "92" })
            using (var response = await Send(client, HttpMethod.Get, "/api/reader/v1/entries/" + id))
            { Check.Equal(HttpStatusCode.NotFound, response.StatusCode); Check.Equal("Entry not found", Text(await Body(response), "detail")); }
        using (var response = await Send(client, HttpMethod.Get, "/api/reader/v1/entries/9223372036854775808")) Check.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using (var response = await Send(client, HttpMethod.Post, "/api/reader/v1/entries/11")) Check.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        using (var response = await client.GetAsync("/api/v1/entries/11")) Check.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var response = await client.GetAsync("/api/reader/v1extra/entries/11")) Check.Equal(HttpStatusCode.NotFound, response.StatusCode);
        foreach (var path in new[] { "/browse/popular", "/browse/latest", "/search?q=Alpha", "/entries/11/refresh" })
            using (var response = await Send(client, HttpMethod.Get, "/api/reader/v1" + path)) Check.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Check.True(!typeof(IReaderCatalogue).IsAssignableFrom(typeof(ReaderCatalogue)));
        var mapped = pipeline.Server.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText).ToArray();
        Check.Equal(1, mapped.Count(path => path == "/api/v1/sources")); Check.Equal(1, mapped.Count(path => path == "/api/v1/entries/{entry_id}"));
        pipeline.Settings = pipeline.Settings with { AutosourcarrReaderApiToken = "" };
        using (var response = await Send(client, HttpMethod.Get, "/api/reader/v1/sources")) Check.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Check.Equal(0, pipeline.Unexpected); Check.Equal(0, pipeline.Admission.Snapshot.Active);
        Equal(before, f.State());
    }
    private static async Task Current()
    {
        using var f = new Fixture(); var first = await f.Reader.EntryAsync(11, CancellationToken.None); first["matches"]!.AsArray().Clear(); first["preferred_title"] = "Caller edit";
        Check.Equal(3, (await f.Reader.EntryAsync(11, CancellationToken.None))["matches"]!.AsArray().Count);
        f.Display.Save(11, new JsonObject { ["description"] = new JsonObject { ["mode"] = "locked", ["value"] = "Display-only current synopsis remains separate from native completeness." } }, 0);
        var before = f.State(); var shown = await f.Reader.EntryAsync(11, CancellationToken.None);
        Check.True(Text(shown, "description_for_extension").StartsWith("Display-only current synopsis", StringComparison.Ordinal)); Check.True(!shown["metadata_complete"]!.GetValue<bool>());
        Equal(before, f.State());
        f.Write(c => c.Execute("UPDATE entry_aliases SET canonical_entry_id=12 WHERE old_entry_id=91; UPDATE source_config SET available=0 WHERE source_id='-1';"));
        before = f.State(); var alias = await f.Reader.EntryAsync(90, CancellationToken.None); Check.Equal(12L, alias["canonical_id"]!.GetValue<long>()); Check.Equal("90", Text(alias, "url"));
        var sources = await f.Reader.SourcesAsync(CancellationToken.None); Check.True(!sources["items"]!.AsArray().Any(row => Text(row!, "source_id") == "-1")); Equal(before, f.State());
        f.Write(c => c.Execute("UPDATE entry_aliases SET canonical_entry_id=90 WHERE old_entry_id=91")); before = f.State();
        Exception? rejection = null;
        try { await f.Reader.EntryAsync(90, CancellationToken.None); } catch (Exception error) { rejection = error; }
        Check.True(rejection is InvalidOperationException, "Cyclic corrupt aliases must be refused by the canonical identity resolver");
        Equal(before, f.State());
    }
    private static async Task Physical()
    {
        using var f = new Fixture(workers: 1); var before = f.State(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var cancel = new CancellationTokenSource();
        var writer = Task.Run(() => { lock (f.Gate) f.Database.Write(DatabaseFile.Catalog, c => { entered.Set(); release.Wait(); return 0; }); });
        Task<JsonObject>? read = null; Task? stopped = null;
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(5))); read = f.Reader.EntryAsync(Big, cancel.Token);
            Check.True(SpinWait.SpinUntil(() => f.Io.Snapshot.Running == 1, TimeSpan.FromSeconds(5))); cancel.Cancel(); stopped = f.Io.StopAsync();
            await Task.Delay(20); Check.True(!read.IsCompleted && !stopped.IsCompleted);
            Check.Throws<TimeoutException>(() => f.Database.Quiescent(_ => 0, TimeSpan.FromMilliseconds(30)));
            release.Set(); await writer.WaitAsync(TimeSpan.FromSeconds(5));
            try { await read.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Cancelled reader published a completed frame"); } catch (OperationCanceledException) { }
            await stopped.WaitAsync(TimeSpan.FromSeconds(5)); Check.Equal(0, f.Io.Snapshot.Running); Check.Equal(0, f.Io.Snapshot.Admitted); Equal(before, f.State());
        }
        finally
        {
            release.Set(); await writer;
            if (read is not null) try { await read; } catch (OperationCanceledException) { }
            if (stopped is not null) await stopped;
        }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Text(JsonNode value, string key) => value[key]!.GetValue<string>();
    private static void Equal(JsonNode expected, JsonNode actual) => Check.JsonEqual(JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(actual));
    private static HttpRequestMessage Request(HttpMethod method, string path, string token = "reader")
    { var request = new HttpRequestMessage(method, path); request.Headers.Add("X-AutoSourcarr-Api-Key", token); return request; }
    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path)
    { using var request = Request(method, path); return await client.SendAsync(request); }
    private static async Task<JsonObject> Body(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    private static JsonObject Snapshot(DatabaseSession c)
    {
        var value = new JsonObject(); foreach (var row in c.Query("SELECT name FROM sqlite_master WHERE type='table' AND (name NOT LIKE 'sqlite_%' OR name='sqlite_sequence') ORDER BY name"))
        {
            var table = row.Text("name"); value[table] = new JsonArray(c.Query("SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" ORDER BY rowid").Select(record => JsonSerializer.SerializeToNode(record.ToDictionary(pair => pair.Key, pair => pair.Value))).ToArray());
        }
        return value;
    }
    private sealed class Fixture : IDisposable
    {
        private readonly Sandbox _sandbox = new();
        public object Gate { get; } = new(); public AutoSourcarrSettings Settings { get; }
        public SqliteDatabase Database { get; } public BoundedWorkScheduler Io { get; } public CoverCache Cache { get; } public CoverData Image { get; }
        public CatalogueQueries Queries { get; } public DisplayMetadata Display { get; } public ReaderCatalogue Reader { get; }
        public Fixture(JsonObject? input = null, int workers = 2)
        {
            Settings = new() { DataDir = _sandbox.Path, AutosourcarrReaderApiToken = "reader", AdminToken = "admin" };
            Database = new(Settings); new SchemaMigrator(Database).Initialize();
            Database.Write(DatabaseFile.Catalog, c => c.Execute(Text(Corpus.Value, "catalog_seed"))); Database.Write(DatabaseFile.Origins, c => c.Execute(Text(Corpus.Value, "origins_seed")));
            Database.Write(DatabaseFile.Origins, c => c.Execute("INSERT INTO candidate_pages VALUES(1,0,'https://reader-fixture.invalid/retained-page','reader-proof'); INSERT INTO served_origin VALUES(1,1,'reader-proof','retained-reader-choice'); INSERT INTO origin_history(virtual_chapter_id,candidate_id,source_id,source_name,source_manga_id,source_chapter_id,served_at,reason) VALUES(1,1,'-1','Retained Reader Source',101,1001,'reader-proof','retained-reader-history'); INSERT INTO chapter_candidate_rejections VALUES('-2',1008,102,'Retained Reader Source','Rejected chapter','reader-proof');"));
            if (input?["sql"] is JsonValue sql) Database.Write(DatabaseFile.Catalog, c => c.Execute(sql.GetValue<string>()));
            if (input?["origins_sql"] is JsonValue origins) Database.Write(DatabaseFile.Origins, c => c.Execute(origins.GetValue<string>()));
            if (input?["fields"] is JsonObject fields)
            {
                var id = input["entry_id"]!.GetValue<long>(); if (id == 90) id = Big;
                Database.Write(DatabaseFile.Catalog, c => c.Execute("INSERT INTO display_metadata VALUES($id,7,$fields,'reader-proof')", ("$id", id), ("$fields", fields.ToJsonString())));
            }
            Io = new(workers, 8); Cache = new(_sandbox.Path, () => Settings, Io);
            var pixels = File.ReadAllBytes(ConfigurationTests.Fixture("Images/sample.png")); using (var raster = RasterImages.Open(pixels)) Check.True(raster.Width > 0 && raster.Height > 0);
            Image = new(pixels, "image/png"); foreach (var id in Corpus.Value["cached_covers"]!.AsArray()) Cache.Write("entry-" + id!.GetValue<long>().ToString(CultureInfo.InvariantCulture), Image);
            Queries = new(Database, Gate, id => Cache.IsCached("entry-" + id.ToString(CultureInfo.InvariantCulture))); Display = new(Database, Gate); Reader = new(Database, Gate, Cache, Io);
        }
        public JsonObject State() => new() { ["catalog"] = Database.Read(DatabaseFile.Catalog, Snapshot), ["origins"] = Database.Read(DatabaseFile.Origins, Snapshot) };
        public void Write(Func<DatabaseSession, int> action) { lock (Gate) Database.Write(DatabaseFile.Catalog, action); }
        public void Dispose() { Cache.DisposeAsync().AsTask().GetAwaiter().GetResult(); Io.DisposeAsync().AsTask().GetAwaiter().GetResult(); Database.Dispose(); _sandbox.Dispose(); }
    }
    private sealed class Pipeline : IDisposable
    {
        private readonly IHost _host; private readonly WholeTaskCoordinator _lanes; private readonly TaskRegistry _tasks = new();
        public AutoSourcarrSettings Settings; public RequestAdmissionGate Admission { get; } = new(); public int Unexpected; public TestServer Server { get; }
        public Pipeline(Fixture f)
        {
            Settings = f.Settings; _lanes = new(Settings, _tasks);
            _host = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer().ConfigureServices(services => services.AddRouting()).Configure(app =>
            {
                app.UseAutoSourcarrHttp("reader-read-fixture", () => Settings, (_, _) => { Unexpected++; return "unexpected-reader-read"; }, Admission, _lanes, _tasks, new SupportPrivacy(), _ => { });
                app.UseEndpoints(endpoints => endpoints.MapReaderCatalogueRead(f.Reader));
            })).Build();
            _host.StartAsync().GetAwaiter().GetResult(); Server = _host.GetTestServer();
        }
        public void Dispose() { _host.Dispose(); _lanes.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
