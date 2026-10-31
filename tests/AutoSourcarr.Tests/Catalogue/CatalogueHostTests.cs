using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Imaging;
using AutoSourcarr.Core.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoSourcarr.Tests;

public static class CatalogueHostTests
{
    internal const long Entry = 9007199254740993, Manga = 9007199254741001, SourceChapter = 9007199254741003;
    internal const string Source = "2718281828459045235";
    private static readonly byte[] Image = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];
    public static void Register(TestSuite suite)
    {
        suite.Add("catalogue host: real routes preserve aliases Int64 IDs auth and factual cache state", () => Reads().GetAwaiter().GetResult());
        suite.Add("catalogue host: metadata and profile CAS persist through the actual app owner", () => Decisions().GetAwaiter().GetResult());
        suite.Add("catalogue host: actual source HTTP pages GET HEAD and served provenance", () => Pages().GetAwaiter().GetResult());
        suite.Add("catalogue host: network image cancellation joins app shutdown", () => NetworkStop().GetAwaiter().GetResult());
        suite.Add("catalogue host: cancelled reader waits for the actual shared catalogue gate", () => GateStop().GetAwaiter().GetResult());
        suite.Add("catalogue host: background cache fill holds storage until physical completion", () => CacheStop().GetAwaiter().GetResult());
        suite.Add("catalogue host: failed cache construction releases app resources without replacing data", () => StartupFailure().GetAwaiter().GetResult());
    }
    private static async Task Reads()
    {
        await using var f = await Fixture.Create();
        Check.Equal(HttpStatusCode.Unauthorized, (await f.Request(HttpMethod.Get, "/api/reader/v1/sources", reader: false)).StatusCode);
        var sources = await Body(await f.Request(HttpMethod.Get, "/api/reader/v1/sources"));
        Check.True(sources["items"]!.AsArray().Any(row => row!["source_id"]!.GetValue<string>() == Source));
        var entry = await Body(await f.Request(HttpMethod.Get, "/api/reader/v1/entries/17"));
        Check.Equal(Entry, entry["id"]!.GetValue<long>()); Check.Equal(Entry, entry["canonical_id"]!.GetValue<long>());
        Check.Equal("17", entry["url"]!.GetValue<string>()); Check.True(!entry["cover_cached"]!.GetValue<bool>());
        await f.Runtime.Catalogue.Covers.WriteAsync(FormattableString.Invariant($"entry-{Entry}"), new(Image, "image/png"));
        Check.True((await Body(await f.Request(HttpMethod.Get, "/api/reader/v1/entries/17")))["cover_cached"]!.GetValue<bool>());
        var head = await f.Request(HttpMethod.Head, "/api/reader/v1/entries/17");
        Check.Equal(HttpStatusCode.OK, head.StatusCode); Check.Equal(0, (await head.Content.ReadAsByteArrayAsync()).Length);
        var chapters = await Body(await f.Request(HttpMethod.Get, "/api/reader/v1/entries/17/chapters"));
        Check.Equal(51L, chapters["items"]![0]!["id"]!.GetValue<long>());
        Check.Equal(HttpStatusCode.NotFound, (await f.Request(HttpMethod.Get, "/api/reader/v1/entries/99999")).StatusCode);
        Check.Equal(HttpStatusCode.UnprocessableEntity, (await f.Request(HttpMethod.Get, "/api/reader/v1/entries/9223372036854775808")).StatusCode);
        Check.Equal(HttpStatusCode.NotFound, (await f.Request(HttpMethod.Get, "/api/reader/v1/browse/popular")).StatusCode);
        Check.Equal(HttpStatusCode.NotFound, (await f.Request(HttpMethod.Get, "/api/reader/v1/entries/17/refresh")).StatusCode);
    }
    private static async Task Decisions()
    {
        await using var f = await Fixture.Create();
        var profilePath = FormattableString.Invariant($"/api/admin/profiles/{Entry}");
        var metadataPath = FormattableString.Invariant($"/api/admin/metadata-fields/{Entry}");
        // The reader key identifies a reader device (phase 5 roles): admin routes refuse it with 403; no key is 401.
        Check.Equal(HttpStatusCode.Forbidden, (await f.Request(HttpMethod.Get, profilePath)).StatusCode);
        Check.Equal(HttpStatusCode.Forbidden, (await f.Request(HttpMethod.Get, metadataPath)).StatusCode);
        Check.Equal(HttpStatusCode.Unauthorized, (await f.Request(HttpMethod.Get, metadataPath, reader: false)).StatusCode);
        Check.Equal(HttpStatusCode.NotFound, (await f.Request(HttpMethod.Get, "/api/admin/profiles/17", admin: true)).StatusCode);
        var profile = new JsonObject { ["revision"] = 0, ["profile"] = new JsonObject { ["sources"] = new JsonArray(Source), ["quality"] = "quality" } };
        var saved = await Body(await f.Request(HttpMethod.Put, profilePath, admin: true, body: profile));
        Check.Equal(1L, saved["revision"]!.GetValue<long>());
        Check.Equal(HttpStatusCode.Conflict, (await f.Request(HttpMethod.Put, profilePath, admin: true, body: profile)).StatusCode);
        var fields = new JsonObject { ["revision"] = 0, ["fields"] = new JsonObject
        { ["description"] = new JsonObject { ["mode"] = "locked", ["value"] = "Saved host description" } } };
        Check.Equal(HttpStatusCode.OK, (await f.Request(HttpMethod.Put, metadataPath, admin: true, body: fields)).StatusCode);
        Check.Equal(HttpStatusCode.Conflict, (await f.Request(HttpMethod.Put, metadataPath, admin: true, body: fields)).StatusCode);
        var shown = await Body(await f.Request(HttpMethod.Get, "/api/reader/v1/entries/17"));
        Check.True(shown["description_for_extension"]!.GetValue<string>().StartsWith("Saved host description", StringComparison.Ordinal));
        var sourceFacts = f.Runtime.Database.Read(DatabaseFile.Catalog, c => c.Query("SELECT source_title FROM source_matches WHERE entry_id=$id", ("$id", Entry))[0].Text("source_title"));
        Check.Equal("Source title", sourceFacts);
        await f.CloseApp();
        await using var reopened = await ApplicationRuntime.CreateExternalForTestsAsync(f.Settings, f.Settings.SettingsConfigDir);
        var profiles = new AutoSourcarr.Core.Reader.ServingProfileRepository(reopened.Database, reopened.CatalogueWriteGate);
        Check.Equal(1L, profiles.Read(Entry)["revision"]!.GetValue<long>());
        var display = new AutoSourcarr.Core.Catalogue.DisplayMetadata(reopened.Database, reopened.CatalogueWriteGate);
        Check.Equal("Saved host description", display.Overlay(new JsonObject { ["id"] = Entry, ["preferred_title"] = "Kept title" })["description"]!.GetValue<string>());
    }
    private static async Task Pages()
    {
        await using var f = await Fixture.Create(upstream: true);
        var pages = await Body(await f.Request(HttpMethod.Get, "/api/reader/v1/chapters/51/pages"));
        Check.Equal(1, pages["pages"]!.AsArray().Count); Check.True(!pages.ToJsonString().Contains("upstream-private", StringComparison.Ordinal));
        Check.Equal(1, f.Upstream!.PageQueries);
        var image = await f.Request(HttpMethod.Get, "/api/reader/v1/pages/61/0");
        Check.Equal(HttpStatusCode.OK, image.StatusCode); Check.True((await image.Content.ReadAsByteArrayAsync()).SequenceEqual(Image));
        Check.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        var head = await f.Request(HttpMethod.Head, "/api/reader/v1/pages/61/0");
        Check.Equal(HttpStatusCode.OK, head.StatusCode); Check.Equal(0, (await head.Content.ReadAsByteArrayAsync()).Length);
        Check.Equal(2, f.Upstream.ImageQueries);
        Check.Equal(61L, f.Runtime.Database.Read(DatabaseFile.Origins, c => Convert.ToInt64(c.Scalar("SELECT candidate_id FROM served_origin WHERE virtual_chapter_id=51"), CultureInfo.InvariantCulture)));
        Check.Equal(SourceChapter, f.Runtime.Database.Read(DatabaseFile.Origins, c => Convert.ToInt64(c.Scalar("SELECT source_chapter_id FROM chapter_candidates WHERE id=61"), CultureInfo.InvariantCulture)));
    }
    private static async Task NetworkStop()
    {
        await using var f = await Fixture.Create(upstream: true); f.Upstream!.BlockImages = true;
        f.Runtime.Database.Write(DatabaseFile.Origins, c => c.Execute("INSERT INTO candidate_pages(candidate_id,page_index,private_url,updated_at) VALUES(61,0,$url,$time)",
            ("$url", f.Upstream.Address + "/private.png"), ("$time", DateTimeOffset.UtcNow.ToString("O"))));
        using var request = f.ReaderRequest(HttpMethod.Get, "/api/reader/v1/pages/61/0");
        HttpResponseMessage? response = null; Task<byte[]>? body = null;
        try
        {
            response = await f.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            body = response.Content.ReadAsByteArrayAsync();
            await f.Upstream.ImageEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = f.Runtime.DisposeAsync().AsTask();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            try { await body.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Cancelled partial image became complete."); }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { }
            await f.Upstream.ImageAborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var held = ApplicationRuntime.InstanceRoots.Acquire([f.Settings.SettingsConfigDir]);
        }
        finally
        {
            f.Upstream.ReleaseImage.TrySetResult(); response?.Dispose();
            if (body is not null) try { await body; } catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { }
        }
    }
    private static async Task GateStop()
    {
        await using var f = await Fixture.Create(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var cancel = new CancellationTokenSource();
        var writer = Task.Run(() => { lock (f.Runtime.CatalogueWriteGate) f.Runtime.Database.Write(DatabaseFile.Catalog, _ => { entered.Set(); release.Wait(); return 0; }); });
        Task<HttpResponseMessage>? read = null; Task? stop = null;
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(5)));
            using var request = f.ReaderRequest(HttpMethod.Get, "/api/reader/v1/entries/17");
            read = f.Client.SendAsync(request, cancel.Token);
            Check.True(SpinWait.SpinUntil(() => f.Runtime.Io.Snapshot.Running > 0, TimeSpan.FromSeconds(5)));
            await cancel.CancelAsync(); stop = f.Runtime.DisposeAsync().AsTask();
            Check.True(SpinWait.SpinUntil(() => f.Runtime.Stopping, TimeSpan.FromSeconds(5)));
            Check.True(!stop.IsCompleted); Check.Throws<IOException>(() => ApplicationRuntime.InstanceRoots.Acquire([f.Settings.SettingsConfigDir]));
        }
        finally
        {
            release.Set(); await writer;
            if (read is not null) try { await read; } catch (OperationCanceledException) { }
            if (stop is not null) await stop;
        }
    }
    private static async Task CacheStop()
    {
        await using var f = await Fixture.Create(); var entered = Signal(); var release = Signal(); Task? stop = null;
        async Task<CoverData> Load(CancellationToken _) { entered.TrySetResult(); await release.Task; return new(Image, "image/png"); }
        var inline = f.Runtime.Catalogue.Covers.GetAsync("entry-pending", Load, TimeSpan.Zero);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check.True(await inline is null);
            stop = f.Runtime.DisposeAsync().AsTask(); Check.True(SpinWait.SpinUntil(() => f.Runtime.Stopping, TimeSpan.FromSeconds(5)));
            Check.True(!stop.IsCompleted); Check.Throws<IOException>(() => ApplicationRuntime.InstanceRoots.Acquire([f.Settings.SettingsConfigDir]));
        }
        finally { release.TrySetResult(); await inline; if (stop is not null) await stop; }
        Check.True(!File.Exists(Path.Combine(f.Settings.DataDir, "cover-cache", "entry-pending.img")));
    }
    private static async Task StartupFailure()
    {
        using var temp = new Sandbox(); var settings = Fixture.SettingsAt(temp.Path, "http://127.0.0.1:1");
        Directory.CreateDirectory(settings.DataDir); var path = Path.Combine(settings.DataDir, "cover-cache"); File.WriteAllText(path, "owned-fixture");
        Exception? failure = null;
        try { await using var unexpected = await ApplicationRuntime.CreateExternalForTestsAsync(settings, settings.SettingsConfigDir); }
        catch (Exception error) { failure = error; }
        Check.True(failure is IOException, failure?.ToString() ?? "Non-directory cache root was admitted.");
        using (ApplicationRuntime.InstanceRoots.Acquire([settings.DataDir, settings.SettingsConfigDir])) { }
        Check.Equal("owned-fixture", File.ReadAllText(path)); File.Delete(path);
        await using var retry = await ApplicationRuntime.CreateExternalForTestsAsync(settings, settings.SettingsConfigDir);
        Check.True(Directory.Exists(path));
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<JsonObject> Body(HttpResponseMessage response)
    { Check.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<JsonObject>())!; }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly Sandbox _temp = new(); private IHost? _host; private bool _closed;
        internal ApplicationRuntime Runtime { get; private set; } = null!;
        internal AutoSourcarrSettings Settings { get; private set; } = null!;
        internal HttpClient Client { get; private set; } = null!;
        internal SourceHttp? Upstream { get; private set; }
        internal static AutoSourcarrSettings SettingsAt(string root, string source) => new()
        { DataDir = Path.Combine(root, "data"), SettingsConfigDir = Path.Combine(root, "config"), SourceEngineMode = "external", SourceSuwayomiUrl = source, AdminToken = "catalogue-admin", AutosourcarrReaderApiToken = "catalogue-reader" };
        internal static async Task<Fixture> Create(bool upstream = false, Func<AutoSourcarrSettings, AutoSourcarrSettings>? configure = null)
        {
            var f = new Fixture();
            try
            {
                if (upstream) f.Upstream = await SourceHttp.Start();
                f.Settings = SettingsAt(f._temp.Path, f.Upstream?.Address ?? "http://127.0.0.1:1");
                if (configure is not null) f.Settings = configure(f.Settings);
                f.Runtime = await ApplicationRuntime.CreateExternalForTestsAsync(f.Settings, f.Settings.SettingsConfigDir);
                lock (f.Runtime.CatalogueWriteGate)
                {
                    f.Runtime.Database.Write(DatabaseFile.Catalog, c => c.Execute("""
                        INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES($entry,'Host title','now','now');
                        INSERT INTO entry_aliases VALUES(17,$entry,'merge','now');
                        INSERT INTO source_config(source_id,name,lang,priority,last_seen) VALUES($source,'Host source','en',10,'now');
                        INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,last_checked) VALUES($entry,$source,$manga,'Source title','now');
                        """, ("$entry", Entry), ("$source", Source), ("$manga", Manga)));
                    f.Runtime.Database.Write(DatabaseFile.Origins, c => c.Execute("""
                        INSERT INTO virtual_chapters(id,entry_id,chapter_key,chapter_number,display_name,created_at,updated_at) VALUES(51,$entry,'n:1',1,'Chapter 1','now','now');
                        INSERT INTO chapter_candidates(id,virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,source_chapter_index,name,chapter_number,page_count,first_seen,last_seen)
                        VALUES(61,51,$source,'Host source',$manga,$chapter,0,'Chapter 1',1,20,'now','now');
                        """, ("$entry", Entry), ("$source", Source), ("$manga", Manga), ("$chapter", SourceChapter)));
                }
                var assets = Path.Combine(f._temp.Path, "assets"); Directory.CreateDirectory(assets); File.WriteAllText(Path.Combine(assets, "index.html"), "host-test");
                f._host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => services.AddRouting())
                    .Configure(app => f.Runtime.ConfigureHttp(app, assets))).Build();
                await f._host.StartAsync(); f.Client = f._host.GetTestClient();
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal HttpRequestMessage ReaderRequest(HttpMethod method, string path)
        { var request = new HttpRequestMessage(method, path); request.Headers.Add("X-AutoSourcarr-Api-Key", Settings.AutosourcarrReaderApiToken); return request; }
        internal async Task<HttpResponseMessage> Request(HttpMethod method, string path, bool reader = true, bool admin = false, JsonObject? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (reader) request.Headers.Add("X-AutoSourcarr-Api-Key", Settings.AutosourcarrReaderApiToken);
            if (admin) request.Headers.Add("X-Admin-Token", Settings.AdminToken);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return await Client.SendAsync(request);
        }
        internal async Task CloseApp()
        {
            if (_closed) return; _closed = true;
            Client?.Dispose(); _host?.Dispose(); if (Runtime is not null) await Runtime.DisposeAsync();
        }
        public async ValueTask DisposeAsync()
        {
            try { await CloseApp(); }
            finally { try { if (Upstream is not null) await Upstream.DisposeAsync(); } finally { _temp.Dispose(); } }
        }
    }
    internal sealed class SourceHttp : IAsyncDisposable
    {
        private IHost? _host;
        internal string Address { get; private set; } = "";
        internal int PageQueries, ImageQueries;
        internal bool BlockImages;
        internal Func<JsonObject, HttpContext, Task<JsonObject>>? DetailsHandler { get; set; }
        internal TaskCompletionSource ImageEntered { get; } = Signal();
        internal TaskCompletionSource ImageAborted { get; } = Signal();
        internal TaskCompletionSource ReleaseImage { get; } = Signal();
        internal static async Task<SourceHttp> Start()
        {
            var source = new SourceHttp();
            try
            {
                source._host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).ConfigureWebHost(web =>
                    web.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)).Configure(app => app.Run(source.Handle))).Build();
                await source._host.StartAsync();
                source.Address = source._host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                return source;
            }
            catch (Exception primary)
            {
                try { await source.DisposeAsync(); }
                catch (Exception cleanup) { throw new AggregateException("Local HTTP fixture startup and cleanup failed.", primary, cleanup); }
                throw;
            }
        }
        private async Task Handle(HttpContext context)
        {
            if (context.Request.Path == "/api/graphql")
            {
                var request = await context.Request.ReadFromJsonAsync<JsonObject>();
                if (request!["query"]!.GetValue<string>().Contains("AutoSourcarrManga", StringComparison.Ordinal) && DetailsHandler is not null)
                {
                    var details = await DetailsHandler(request, context);
                    await context.Response.WriteAsJsonAsync(new JsonObject { ["data"] = new JsonObject { ["fetchMangaAndChapters"] = details } });
                    return;
                }
                Check.True(request!["query"]!.GetValue<string>().Contains("AutoSourcarrPages", StringComparison.Ordinal));
                Check.Equal(SourceChapter, request["variables"]!["input"]!["chapterId"]!.GetValue<long>());
                Interlocked.Increment(ref PageQueries);
                await context.Response.WriteAsJsonAsync(new { data = new { fetchChapterPages = new { pages = new[] { Address + "/private.png?access_token=upstream-private" } } } });
                return;
            }
            if (context.Request.Path == FormattableString.Invariant($"/api/v1/manga/{Manga}/chapter/0/page/0"))
            {
                Interlocked.Increment(ref ImageQueries); context.Response.ContentType = "image/png"; context.Response.ContentLength = Image.Length;
                if (BlockImages)
                {
                    using var cancellation = context.RequestAborted.Register(() => ImageAborted.TrySetResult());
                    await context.Response.Body.WriteAsync(Image.AsMemory(0, 1)); await context.Response.Body.FlushAsync(); ImageEntered.TrySetResult();
                    await ReleaseImage.Task;
                    if (!context.RequestAborted.IsCancellationRequested) await context.Response.Body.WriteAsync(Image.AsMemory(1));
                }
                else await context.Response.Body.WriteAsync(Image);
                return;
            }
            context.Response.StatusCode = 404;
        }
        public async ValueTask DisposeAsync()
        {
            ReleaseImage.TrySetResult();
            if (_host is not null) { await _host.StopAsync(); _host.Dispose(); }
        }
    }
}
