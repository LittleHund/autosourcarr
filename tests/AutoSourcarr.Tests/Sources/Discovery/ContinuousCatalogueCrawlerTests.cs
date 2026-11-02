using System.Text.Json.Nodes;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Discovery;
using AutoSourcarr.Tests.Matching;

namespace AutoSourcarr.Tests.Sources.Discovery;

/// <summary>Phase 13 item 6: the continuous catalogue crawler (0.4.5 schedule_continuous_catalog_discovery) as a job.</summary>
public static class ContinuousCatalogueCrawlerTests
{
    private sealed class Pages : ISourceCatalogueClient
    {
        public List<string> Calls { get; } = [];
        public Func<string, string, int, JsonObject> Page { get; init; } = (_, _, _) => new JsonObject { ["mangas"] = new JsonArray(), ["hasNextPage"] = false };
        public Task<JsonObject> BrowseAsync(string sourceId, string mode, int page, string query, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add(mode + ":" + sourceId + ":" + page.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(Page(sourceId, mode, page));
        }
        public Task<JsonObject> MangaAsync(long mangaId, bool details, bool chapters, CancellationToken cancellationToken) => throw new InvalidOperationException("not used");
    }

    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _temp = new();
        public SqliteDatabase Database { get; }
        public EntryIngest Ingest { get; }
        public AutoSourcarrSettings Settings { get; set; }
        public Setup()
        {
            Database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = _temp.Path });
            new SchemaMigrator(Database).Initialize();
            MatchingFixtures.Source(Database, "1", "Alpha", "en");
            MatchingFixtures.Source(Database, "2", "Beta", "en");
            Ingest = new EntryIngest(Database, new object(), new InMemoryDecisionLog());
            Settings = new AutoSourcarrSettings { DataDir = _temp.Path, ContinuousCatalogModes = "POPULAR", ContinuousCatalogIntervalSeconds = 0.5 };
        }
        public ContinuousCatalogueCrawler Crawler(Pages pages, Func<string, bool>? paused = null, Func<bool>? yields = null, List<long>? fresh = null,
            Action<string, Exception?>? result = null) =>
            new(Database, Ingest, pages, () => Settings)
            {
                SourcePaused = paused, Yield = yields, SourceResult = result, NewlyCatalogued = ids => fresh?.AddRange(ids),
            };
        public void Dispose() { Database.Dispose(); _temp.Dispose(); }
    }

    public static void Register(TestSuite suite)
    {
        suite.Add("crawler: one page per pass, pairs in priority order, the cursor persists and only new records fan out enrichment", () =>
        {
            using var setup = new Setup();
            var pages = new Pages
            {
                Page = (source, _, page) => new JsonObject
                {
                    ["mangas"] = new JsonArray(MatchingFixtures.Manga(source == "1" ? 100 + page : 200 + page, "Crawler Title " + source + page, "Writer " + source)),
                    ["hasNextPage"] = page < 2,
                },
            };
            var fresh = new List<long>();
            var crawler = setup.Crawler(pages, fresh: fresh);
            var first = crawler.StepAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal("fetched", first.Outcome);
            Check.Equal("1", first.SourceId);
            Check.Equal(1, first.NewItems);
            var second = crawler.StepAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal("2", second.SourceId);
            Check.Equal(2, fresh.Count);
            var cursor = crawler.Cursor();
            Check.Equal(0, cursor["pair_index"]!.GetValue<int>());
            Check.Equal("POPULAR", cursor["last_mode"]!.GetValue<string>());
            // A new crawler (a restart) continues from the saved cursor; page one ("recent") is now not due for 30 min,
            // so the backlog lane continues with page 2.
            var restarted = setup.Crawler(pages, fresh: fresh);
            var third = restarted.StepAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal("1", third.SourceId);
            Check.True(third.Outcome == "fetched" && third.Page == 1 || third.Outcome == "not-due", third.ToString());
            // Re-reading a known record never fans out enrichment again.
            var before = fresh.Count;
            restarted.StepAsync(CancellationToken.None).GetAwaiter().GetResult();
            restarted.StepAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check.True(fresh.Count <= before + 2, "fresh records only");
        });

        suite.Add("crawler: yields to higher-priority work, skips paused sources and stops when switched off", () =>
        {
            using var setup = new Setup();
            var pages = new Pages();
            var busy = true;
            var crawler = setup.Crawler(pages, paused: id => id == "1", yields: () => busy);
            Check.Equal("yielded", crawler.StepAsync(CancellationToken.None).GetAwaiter().GetResult().Outcome);
            Check.Equal(0, pages.Calls.Count);
            busy = false;
            var step = crawler.StepAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal("2", step.SourceId);
            Check.True(pages.Calls.All(call => !call.Contains(":1:", StringComparison.Ordinal)), "a paused source is never fetched");
            setup.Settings = setup.Settings with { ContinuousCatalogDiscovery = false };
            Check.Equal("off", crawler.StepAsync(CancellationToken.None).GetAwaiter().GetResult().Outcome);
            var tasks = new JobQueue(new TaskRegistry());
            try { Check.True(crawler.Schedule(tasks) is null, "switched off: no job"); }
            finally { tasks.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });

        suite.Add("crawler: hard rule 3: a local timeout is deferred (no failure count, no source pause), a real failure counts", () =>
        {
            using var setup = new Setup();
            var mode = "timeout";
            var pages = new Pages
            {
                Page = (_, _, _) => mode == "timeout" ? throw new TimeoutException("local deadline") : throw new HttpRequestException("site said 500", null, System.Net.HttpStatusCode.InternalServerError),
            };
            var tracker = new SourceResultTracker(() => setup.Settings with { SourceFailureThreshold = 1 });
            var crawler = setup.Crawler(pages, paused: tracker.Paused, result: tracker.Record);
            var step = crawler.StepAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal("failed", step.Outcome);
            var due = new DueScheduler(setup.Database).State("catalog_recent", "POPULAR|1");
            Check.Equal("deferred", due.Outcome);
            Check.Equal(0L, due.Failures);
            Check.True(!tracker.Paused("1"), "a local timeout never pauses a source");
            mode = "real";
            crawler.StepAsync(CancellationToken.None).GetAwaiter().GetResult(); // source 2
            Check.True(tracker.Paused("2"), "a real failure counts toward the source pause");
        });

        suite.Add("crawler: runs as one low-priority long-running job that merges and stops on cancel", () =>
        {
            using var setup = new Setup();
            setup.Settings = setup.Settings with { StartupRefreshDelaySeconds = 0 };
            var crawler = setup.Crawler(new Pages());
            var registry = new TaskRegistry();
            var jobs = new JobQueue(registry);
            try
            {
                var handle = crawler.Schedule(jobs)!;
                Check.True(crawler.Schedule(jobs)!.Merged, "a second crawler merges into the running one");
                Check.Equal(1, jobs.Active([ContinuousCatalogueCrawler.Job.Name]));
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (crawler.Cursor()["turn"] is null && DateTime.UtcNow < deadline) Thread.Sleep(50);
                Check.True(crawler.Cursor()["turn"] is not null, "the job ran a pass");
                registry.RequestCancelAsync(handle.Id).GetAwaiter().GetResult();
                Check.Equal(JobOutcome.Cancelled, handle.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
                Check.True(crawler.StoppedByUser, "a person's cancel keeps it off until the next start");
            }
            finally { jobs.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }
}
