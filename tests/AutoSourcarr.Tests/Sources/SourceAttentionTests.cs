using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Tests;

public static class SourceAttentionTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("website verification: only confirmed challenges require assistance", () =>
        {
            foreach (var (message, expected) in new[] { ("Shape-selecting captcha detected", "WAF"), ("403 {captcha_required}", "WAF"),
                ("challenge at /@waf/challenge", "WAF"), ("CFClearance$CloudflareBypassException", "Cloudflare"), ("human verification required", "CAPTCHA"),
                ("HTTP error 403", ""), ("HTTP error 521", ""), ("HTTP 429 Too many requests", ""), ("Unknown type 'Long'", ""), ("ReadTimeout at /health", "") })
            {
                var actual = SourceFailures.AssistanceReason(new SuwayomiException(message));
                Check.True(expected.Length == 0 ? actual.Length == 0 : actual.StartsWith(expected, StringComparison.Ordinal));
            }
            Check.Equal("", SourceFailures.AssistanceReason(new SourceWorkDeferredException("CAPTCHA capacity busy")));
        });
        suite.Add("website verification: website-wide pause survives restart and gates image routes", () =>
        {
            using var f = new Fixture();
            var error = Check.Throws<HumanAssistanceRequiredException>(() => f.Client.FetchSourceMangaAsync("mf-en", "POPULAR").GetAwaiter().GetResult());
            Check.Equal(3600, error.RetryAfter);
            f.Attention.Learn(JsonNode.Parse("{\"manga\":{\"id\":55,\"sourceId\":\"mf-fr\"},\"chapters\":[{\"id\":77,\"mangaId\":55}]}")!.AsObject());
            Check.Throws<HumanAssistanceRequiredException>(() => f.Client.FetchSourceMangaAsync("mf-fr", "POPULAR").GetAwaiter().GetResult());
            Check.Throws<HumanAssistanceRequiredException>(() => f.Client.FetchMangaAndChaptersAsync(55).GetAwaiter().GetResult());
            Check.Throws<HumanAssistanceRequiredException>(() => f.Client.FetchChapterPagesAsync(77).GetAwaiter().GetResult());
            Check.Throws<HumanAssistanceRequiredException>(() => f.Client.FetchMangaThumbnailAsync(55).GetAwaiter().GetResult());
            Check.Throws<HumanAssistanceRequiredException>(() => f.Client.FetchBinaryAsync("https://www.mangafire.to/page.jpg").GetAwaiter().GetResult());
            Check.Equal(1, f.Calls);
            var restarted = new SourceAttention(f.Database, f.Io, () => f.Now);
            try
            {
                f.Client.Attention = restarted;
                Check.Throws<HumanAssistanceRequiredException>(() => f.Client.FetchSourceMangaAsync("mf-fr", "POPULAR").GetAwaiter().GetResult());
                Check.Equal(1, f.Calls);
                Check.Throws<HumanAssistanceRequiredException>(() => f.Client.FetchSourceMangaAsync("other", "POPULAR").GetAwaiter().GetResult());
                Check.Equal(2, f.Calls);
                Check.Equal(2, restarted.SnapshotAsync().GetAwaiter().GetResult()["count"]!.GetValue<int>());
            }
            finally { f.Client.Attention = f.Attention; restarted.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("website verification: queued requests recheck a newly raised website pause", () =>
        {
            using var f = new Fixture();
            f.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = Enumerable.Range(0, 8).Select(_ => f.Client.FetchSourceMangaAsync("mf-en", "POPULAR")).ToArray();
            try
            {
                f.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                f.Hold.TrySetResult();
                foreach (var request in requests) Check.Throws<HumanAssistanceRequiredException>(() => request.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
                Check.Equal(1, f.Calls);
                Check.Equal(1L, f.Row()["occurrences"]!.GetValue<long>());
            }
            finally { f.Hold.TrySetResult(); foreach (var request in requests) try { request.GetAwaiter().GetResult(); } catch (HumanAssistanceRequiredException) { } }
        });
        suite.Add("website verification: one reserved hourly probe and explicit retry recover a site", () =>
        {
            using var f = new Fixture();
            f.Pause(); var key = f.Row()["id"]!.GetValue<string>();
            f.Now += 3599; f.Attention.TickAsync(f.Client).GetAwaiter().GetResult(); Check.Equal(1, f.Calls);
            f.Now++; f.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously); f.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var tick = f.Attention.TickAsync(f.Client);
            try
            {
                f.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Check.True(!f.Attention.RetryAsync(key, f.Client, true).GetAwaiter().GetResult());
                Check.True(f.Row()["retrying"]!.GetValue<bool>());
                f.Hold.TrySetResult(); tick.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Check.Equal(2, f.Calls);
                Check.Equal(f.Now + 3600, f.Row()["next_retry_at"]!.GetValue<double>());
                f.Attention.TickAsync(f.Client).GetAwaiter().GetResult(); Check.Equal(2, f.Calls);
                f.Fail = false; f.Hold = null;
                Check.True(f.Attention.RetryAsync(key, f.Client, true).GetAwaiter().GetResult());
                Check.Equal(0, f.Attention.SnapshotAsync().GetAwaiter().GetResult()["count"]!.GetValue<int>());
            }
            finally { f.Hold?.TrySetResult(); tick.GetAwaiter().GetResult(); }
        });
        suite.Add("website verification: cancelled probe retains deadline and releases its ownership", () =>
        {
            using var f = new Fixture();
            f.Pause(); var key = f.Row()["id"]!.GetValue<string>(); f.Now += 100;
            f.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously); f.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancel = new CancellationTokenSource();
            var retry = f.Attention.RetryAsync(key, f.Client, true, cancel.Token);
            f.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); cancel.Cancel();
            Check.Throws<OperationCanceledException>(() => retry.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
            Check.True(!f.Row()["retrying"]!.GetValue<bool>());
            Check.Equal(f.Now + 3600, f.Row()["next_retry_at"]!.GetValue<double>());
            var restarted = new SourceAttention(f.Database, f.Io, () => f.Now);
            try { Check.Equal(f.Now + 3600, restarted.SnapshotAsync().GetAwaiter().GetResult()["items"]![0]!["next_retry_at"]!.GetValue<double>()); }
            finally { restarted.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("website verification: stale probe success cannot clear a newer challenge", () =>
        {
            using var f = new Fixture();
            f.Pause(); var key = f.Row()["id"]!.GetValue<string>(); f.Fail = false;
            f.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously); f.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var retry = f.Attention.RetryAsync(key, f.Client, true);
            try
            {
                f.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                var op = new JsonObject { ["kind"] = "browse", ["id"] = "mf-en", ["source_id"] = "mf-en" };
                var context = f.Attention.ContextAsync(f.Client.BaseUrl, op, default).GetAwaiter().GetResult();
                Check.Throws<HumanAssistanceRequiredException>(() => f.Attention.FailedAsync(context, op, new SuwayomiException("CAPTCHA required"), default).GetAwaiter().GetResult());
                f.Hold.TrySetResult(); Check.True(retry.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
                Check.Equal(1, f.Attention.SnapshotAsync().GetAwaiter().GetResult()["count"]!.GetValue<int>());
            }
            finally { f.Hold.TrySetResult(); retry.GetAwaiter().GetResult(); }
        });
        suite.Add("website verification: provisional ID pause follows later source provenance", () =>
        {
            using var f = new Fixture();
            Check.Throws<HumanAssistanceRequiredException>(() => f.Client.FetchMangaAndChaptersAsync(98765).GetAwaiter().GetResult());
            f.Attention.Learn(JsonNode.Parse("{\"manga\":{\"id\":98765,\"sourceId\":\"mf-en\"}}")!.AsObject());
            Check.Throws<HumanAssistanceRequiredException>(() => f.Client.FetchSourceMangaAsync("mf-en", "POPULAR").GetAwaiter().GetResult());
            Check.Throws<HumanAssistanceRequiredException>(() => f.Client.FetchSourceMangaAsync("mf-fr", "POPULAR").GetAwaiter().GetResult());
            Check.Equal(1, f.Calls); Check.Equal("mangafire.to", f.Row()["website"]!.GetValue<string>());
            Check.Equal(f.Now + 3600, f.Row()["next_retry_at"]!.GetValue<double>());
        });
        suite.Add("website verification: malformed saved state cannot silently unpause sources", () =>
        {
            using var f = new Fixture();
            new SettingsRepository(f.Database).Set("source_human_attention_v1", new JsonObject { ["invalid"] = "not a record" });
            Check.Throws<SourceWorkDeferredException>(() => f.Client.FetchSourceMangaAsync("mf-en", "POPULAR").GetAwaiter().GetResult());
            Check.Equal(0, f.Calls);
        });
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Sandbox _temp = new();
        public SqliteDatabase Database { get; }
        public BoundedWorkScheduler Io { get; } = new(2, 24);
        public SourceAttention Attention { get; }
        public SuwayomiClient Client { get; }
        public double Now { get; set; } = 100000;
        public int Calls;
        public bool Fail = true;
        public TaskCompletionSource? Hold;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture()
        {
            Database = new(new AutoSourcarrSettings { DataDir = _temp.Path });
            new SchemaMigrator(Database).Initialize();
            Database.Write(DatabaseFile.Catalog, session => session.Execute("""
                INSERT INTO source_config(source_id,name,lang,base_url,last_seen) VALUES
                ('mf-en','MangaFire','en','https://mangafire.to','2026-10-19T00:00:00+00:00'),
                ('mf-fr','MangaFire','fr','https://www.mangafire.to','2026-10-19T00:00:00+00:00'),
                ('other','Other','en','https://other.test','2026-10-19T00:00:00+00:00');
                """));
            Attention = new(Database, Io, () => Now);
            Client = new(SettingsLoader.Load(), "http://engine.test", workLimit: 1, handler: new Handler(async (_, token) =>
            {
                Interlocked.Increment(ref Calls); Started.TrySetResult();
                if (Hold is not null) await Hold.Task.WaitAsync(token).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fail
                    ? "{\"errors\":[{\"message\":\"Shape-selecting captcha detected. Open in WebView\"}]}"
                    : "{\"data\":{\"fetchSourceManga\":{\"hasNextPage\":false,\"mangas\":[]}}}", Encoding.UTF8, "application/json") };
            })) { Attention = Attention };
        }
        public void Pause() => Check.Throws<HumanAssistanceRequiredException>(() => Client.FetchSourceMangaAsync("mf-en", "POPULAR").GetAwaiter().GetResult());
        public JsonObject Row() => (JsonObject)Attention.SnapshotAsync().GetAwaiter().GetResult()["items"]![0]!;
        public void Dispose()
        {
            Hold?.TrySetResult(); Client.Dispose(); Attention.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Io.DisposeAsync().AsTask().GetAwaiter().GetResult(); Database.Dispose(); _temp.Dispose();
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
