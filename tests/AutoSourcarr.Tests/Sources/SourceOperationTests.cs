using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Tests;

public static class SourceOperationTests
{
    private const string Source = "-9223372036854775808";
    private const long LargeId = 9007199254741003;

    public static void Register(TestSuite suite)
    {
        suite.Add("source operations: source connection variants retain exact source IDs", () =>
        {
            foreach (var shape in new[] { $"[{{\"id\":\"{Source}\"}}]", $"{{\"nodes\":[{{\"id\":{Source}}}]}}", $"{{\"edges\":[{{\"node\":{{\"id\":\"{Source}\"}}}}]}}" })
            {
                using var client = Client(new Handler((_, _) => Result("{\"data\":{\"sources\":" + shape + "}}")));
                var sources = client.SourcesAsync().GetAwaiter().GetResult();
                Check.Equal(1, sources.Count);
                Check.True(sources[0]["id"]!.ToJsonString().Contains(Source, StringComparison.Ordinal));
            }
            var calls = 0;
            using var fallback = Client(new Handler((_, _) => Result(++calls == 1 ? "{\"errors\":[{\"message\":\"nodes unsupported\"}]}" : "{\"data\":{\"sources\":[{\"id\":\"source\"}]}}")));
            Check.Equal(1, fallback.SourcesAsync().GetAwaiter().GetResult().Count);
            Check.Equal(2, calls);
            using var invalid = Client(new Handler((_, _) => Result("{\"data\":{\"sources\":[{\"id\":true}]}}")));
            Check.Throws<SuwayomiException>(() => invalid.SourcesAsync().GetAwaiter().GetResult());
        });
        suite.Add("source operations: library pagination preserves large IDs and rejects incomplete snapshots", () =>
        {
            var afters = new List<string?>();
            using var client = Client(new Handler(async (request, token) =>
            {
                var query = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false))!;
                var after = query["variables"]!["after"]?.GetValue<string>();
                afters.Add(after);
                return Json(Library(after is null ? LargeId : LargeId + 1, after is null, "next"));
            }));
            var rows = client.LibraryMangasAsync().GetAwaiter().GetResult();
            Check.Equal(2, rows.Count);
            Check.Equal(LargeId, rows[0]["id"]!.GetValue<long>());
            Check.True(afters.SequenceEqual(new string?[] { null, "next" }));
            foreach (var repeatedId in new[] { true, false })
            {
                var count = 0;
                using var invalid = Client(new Handler((_, _) => Result(Library(repeatedId ? LargeId : LargeId + ++count, true, "same"))));
                Check.Throws<SuwayomiException>(() => invalid.LibraryMangasAsync().GetAwaiter().GetResult());
            }
            foreach (var wrong in new[] { "true", "1.0", "\"1\"" })
            {
                using var invalid = Client(new Handler((_, _) => Result(Library(1, false, "").Replace("\"id\":1,", "\"id\":" + wrong + ",", StringComparison.Ordinal))));
                Check.Throws<SuwayomiException>(() => invalid.LibraryMangasAsync().GetAwaiter().GetResult());
            }
        });
        suite.Add("source operations: manga and chapter parent identities cannot be mixed", () =>
        {
            foreach (var invalidParent in new[] { false, true })
            {
                using var client = Client(new Handler((_, _) => Result($"{{\"data\":{{\"fetchMangaAndChapters\":{{\"manga\":{{\"id\":{LargeId}}},\"chapters\":[{{\"id\":12,\"mangaId\":{LargeId + (invalidParent ? 1 : 0)}}}]}}}}}}")));
                if (invalidParent) Check.Throws<SuwayomiException>(() => client.FetchMangaAndChaptersAsync(LargeId).GetAwaiter().GetResult());
                else Check.Equal(LargeId, client.FetchMangaAndChaptersAsync(LargeId).GetAwaiter().GetResult()["manga"]!["id"]!.GetValue<long>());
            }
            foreach (var response in new[] { "{\"fetchSourceManga\":{\"hasNextPage\":false,\"mangas\":[{\"id\":1,\"sourceId\":\"wrong\"}]}}",
                "{\"fetchSourceManga\":{\"hasNextPage\":0,\"mangas\":[]}}", "{\"fetchChapterPages\":{\"pages\":[null]}}" })
            {
                using var client = Client(new Handler((_, _) => Result("{\"data\":" + response + "}")));
                Check.Throws<SuwayomiException>(() =>
                {
                    if (response.Contains("fetchChapterPages", StringComparison.Ordinal)) client.FetchChapterPagesAsync(12).GetAwaiter().GetResult();
                    else client.FetchSourceMangaAsync(Source, "POPULAR").GetAwaiter().GetResult();
                });
            }
        });
        suite.Add("source operations: only source-level SEARCH 404 is an empty search result", () =>
        {
            foreach (var mode in new[] { "SEARCH", "POPULAR", "LATEST" })
            {
                using var client = Client(new Handler((_, _) => Result("{\"errors\":[{\"message\":\"Exception while fetching data: HTTP error 404\"}]}")));
                if (mode == "SEARCH") Check.True(client.FetchSourceMangaAsync(Source, mode).GetAwaiter().GetResult()["searchMiss"]!.GetValue<bool>());
                else Check.Throws<SuwayomiException>(() => client.FetchSourceMangaAsync(Source, mode).GetAwaiter().GetResult());
            }
            using var transport = Client(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
            Check.Throws<HttpRequestException>(() => transport.FetchSourceMangaAsync(Source, "SEARCH").GetAwaiter().GetResult());
        });
        suite.Add("source operations: install recovery replays only a confirmed package 404 once", () =>
        {
            var calls = 0;
            var package = "eu.kanade.tachiyomi.extension.en.example";
            using var client = Client(new Handler(async (request, token) =>
            {
                var query = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false))!;
                calls++;
                if (calls == 1) return Json("{\"errors\":[{\"message\":\"Exception while fetching data (/updateExtension) : HTTP error 404\"}]}");
                if (calls == 2)
                {
                    Check.True(query["query"]!.GetValue<string>().Contains("AutoSourcarrFetchExtensions", StringComparison.Ordinal));
                    return Json($"{{\"data\":{{\"fetchExtensions\":{{\"extensions\":[{{\"pkgName\":\"{package}\",\"isInstalled\":false}}],\"extensionStores\":[]}}}}}}");
                }
                return Json($"{{\"data\":{{\"updateExtension\":{{\"extension\":{{\"pkgName\":\"{package}\",\"isInstalled\":true}}}}}}}}");
            }));
            Check.True(client.UpdateExtensionAsync(package, "install").GetAwaiter().GetResult()!["isInstalled"]!.GetValue<bool>());
            Check.Equal(3, calls);
            foreach (var action in new[] { "update", "uninstall", "install" })
            {
                calls = 0;
                using var uncertain = Client(new Handler((_, _) =>
                {
                    calls++;
                    return Task.FromResult(action == "install" ? new HttpResponseMessage(HttpStatusCode.NotFound)
                        : Json("{\"errors\":[{\"message\":\"Exception while fetching data (/updateExtension) : HTTP error 404\"}]}"));
                }));
                Check.Throws<Exception>(() => uncertain.UpdateExtensionAsync(package, action).GetAwaiter().GetResult());
                Check.Equal(1, calls);
            }
            calls = 0;
            using var unconfirmed = Client(new Handler((_, _) => { calls++; return Result("{\"data\":{\"updateExtension\":{\"extension\":null}}}"); }));
            Check.Throws<SuwayomiException>(() => unconfirmed.UpdateExtensionAsync(package, "install").GetAwaiter().GetResult());
            Check.Equal(1, calls);
        });
        suite.Add("source operations: extension upload rejects login HTML and redirect success", () =>
        {
            foreach (var redirect in new[] { false, true })
            {
                var calls = 0;
                using var client = Client(new Handler(async (request, token) =>
                {
                    calls++;
                    Check.True(request.Content is MultipartFormDataContent);
                    var bytes = await request.Content!.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                    Check.True(bytes.Length > 3);
                    var response = new HttpResponseMessage(redirect ? HttpStatusCode.Found : HttpStatusCode.OK)
                    { Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html") };
                    if (redirect) response.Headers.Location = new Uri("https://login.test");
                    return response;
                }));
                Check.Throws<Exception>(() => client.InstallExtensionFileAsync("source.apk", [1, 2, 3]).GetAwaiter().GetResult());
                Check.Equal(1, calls);
            }
        });
        suite.Add("source operations: health requires API data and does not retry local deferrals", () =>
        {
            var calls = 0;
            using var html = Client(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") }); }));
            Check.True(!html.HealthAsync().GetAwaiter().GetResult()["ok"]!.GetValue<bool>());
            Check.Equal(2, calls);
            calls = 0;
            using var deferred = new SuwayomiClient(SettingsLoader.Load(), "http://source.test:4567", runtime: new UnavailableRuntime(),
                handler: new Handler((_, _) => { calls++; return Result("{}"); }));
            Check.True(deferred.HealthAsync().GetAwaiter().GetResult()["deferred"]!.GetValue<bool>());
            Check.Equal(0, calls);
            using var degraded = Client(new Handler((request, _) => Result(request.Method == HttpMethod.Post ? "{}" : "{\"version\":\"example\"}")));
            Check.True(degraded.HealthAsync().GetAwaiter().GetResult()["degraded"]!.GetValue<bool>());
        });
        suite.Add("source operations: foreground capacity survives background admission deferral", () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var attention = new Attention();
            using var client = new SuwayomiClient(SettingsLoader.Load(), "http://source.test:4567", workLimit: 2,
                handler: new Handler(async (_, token) =>
                {
                    if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token).ConfigureAwait(false); }
                    return Json("{\"data\":{\"fetchSourceManga\":{\"hasNextPage\":false,\"mangas\":[]}}}");
                })) { Attention = attention };
            Task<JsonObject> first;
            using (SourceWorkContext.Background()) first = client.FetchSourceMangaAsync(Source, "POPULAR");
            try
            {
                entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                using (SourceWorkContext.Background())
                using (SourceWorkContext.WithBudget(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(40)))
                    Check.Throws<SourceWorkDeferredException>(() => client.FetchSourceMangaAsync(Source, "POPULAR").GetAwaiter().GetResult());
                Check.Equal(1, calls);
                client.FetchSourceMangaAsync(Source, "POPULAR").WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Check.Equal(2, calls);
                Check.Equal(0, attention.Failures);
            }
            finally { release.TrySetResult(); first.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
        });
        suite.Add("source operations: local execution deadlines are deferrals rather than website failures", () =>
        {
            var attention = new Attention();
            using var client = Client(new Handler(async (_, token) =>
            { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return Json("{}"); }));
            client.Attention = attention;
            using (SourceWorkContext.WithBudget(TimeSpan.FromMilliseconds(40)))
                Check.Throws<SourceBudgetExceededException>(() => client.FetchSourceMangaAsync(Source, "POPULAR").GetAwaiter().GetResult());
            Check.Equal(0, attention.Failures);
        });
    }

    private static string Library(long id, bool next, string cursor) => $"{{\"data\":{{\"mangas\":{{\"nodes\":[{{\"id\":{id},\"sourceId\":\"{Source}\",\"url\":\"/item/{id}\",\"inLibrary\":true}}],\"pageInfo\":{{\"hasNextPage\":{(next ? "true" : "false")},\"endCursor\":\"{cursor}\"}}}}}}}}";
    private static SuwayomiClient Client(Handler handler) => new(SettingsLoader.Load(), "http://source.test:4567", handler: handler);
    private static Task<HttpResponseMessage> Result(string body) => Task.FromResult(Json(body));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class UnavailableRuntime : IManagedSourceRuntime
    {
        public bool BrowserMaintenance => false;
        public Task EnsureAvailableAsync(string requestPath, CancellationToken cancellationToken) => throw new SourceEngineUnavailableException("Starting");
    }
    private sealed class Attention : ISourceAttention
    {
        public int Failures { get; private set; }
        public Task<JsonObject> ContextAsync(string baseUrl, JsonObject operation, CancellationToken cancellationToken) => Task.FromResult(new JsonObject());
        public Task CheckAsync(JsonObject context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FailedAsync(JsonObject context, JsonObject operation, Exception error, CancellationToken cancellationToken) { Failures++; return Task.CompletedTask; }
        public Task CheckWebsiteAsync(string baseUrl, string host, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Learn(JsonObject value) { }
    }
}
