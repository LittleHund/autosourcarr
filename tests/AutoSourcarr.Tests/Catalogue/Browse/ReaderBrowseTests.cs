using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Browse;
using AutoSourcarr.Core.Catalogue.Chapters;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Tests.Catalogue.Chapters;

namespace AutoSourcarr.Tests.Catalogue.Browse;

/// <summary>READ-3: the reader gets stored data at once; old feeds and chapter lists only queue a background refresh.</summary>
public static class ReaderBrowseTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("reader browse: saved feeds serve at once, old ones queue a crawl, missing ones answer preparing", () => Feeds().GetAwaiter().GetResult());
        suite.Add("reader browse: opening a work serves stored chapters and refreshes old records in the background", () => Chapters().GetAwaiter().GetResult());
    }

    private static async Task Feeds()
    {
        using var world = new ChapterWorld();
        world.Write(ChapterWorld.SourceA, ChapterWorld.MangaA, ChapterWorld.Chapter(1, 1, "Chapter 1"));
        world.Database.Write(DatabaseFile.Catalog, s => s.Execute("""
            INSERT INTO browse_cache(cache_key,mode,page,entry_ids_json,has_next_page,updated_at,last_accessed)
            VALUES('popular-1','POPULAR',1,'[1]',1,'2020-01-01T00:00:00+00:00','2020-01-01T00:00:00+00:00')
            """));
        var crawls = 0;
        await using var io = new BoundedWorkScheduler(2, 16);
        var browse = Browse(world, io, null, (_, _, _, _) => Task.FromResult(new JsonObject()), () => { crawls++; return true; });

        var popular = await browse.BrowseAsync(new ReaderBrowseRequest("POPULAR"), default);
        Check.Equal(1, popular["items"]!.AsArray().Count);
        Check.True(popular["refreshing"]!.GetValue<bool>() && popular["has_next_page"]!.GetValue<bool>());
        Check.Equal(1, crawls);
        // Filters apply to the saved page: no record of another source, and not enough sources.
        Check.Equal(0, (await browse.BrowseAsync(new ReaderBrowseRequest("POPULAR", SourceIds: ["9999"]), default))["items"]!.AsArray().Count);
        Check.Equal(0, (await browse.BrowseAsync(new ReaderBrowseRequest("POPULAR", MinimumMatches: 3), default))["items"]!.AsArray().Count);
        // No saved Latest page yet: "preparing", and the crawl is queued.
        await ThrowsAsync<ReaderBrowsePendingException>(() => browse.BrowseAsync(new ReaderBrowseRequest("LATEST"), default));
        Check.True(crawls >= 2);
        var search = await browse.BrowseAsync(new ReaderBrowseRequest("SEARCH", Query: "solo"), default);
        Check.Equal(1, search["items"]!.AsArray().Count);
        Check.True(search["items"]![0]!["match_confidence"]!.GetValue<double>() > 0);
        var admin = await browse.BrowseAsync(new JsonObject { ["mode"] = "SEARCH", ["q"] = "story", ["page_size"] = 5 }, default);
        Check.Equal(5, admin["page_size"]!.GetValue<int>());
        // The reader source's private entry-id token resolves one known work locally (no source search, no crawl).
        var before = crawls;
        var token = await browse.BrowseAsync(new ReaderBrowseRequest("SEARCH", Query: "__autosourcarr_entry_id__:1"), default);
        Check.True(token["internal_entry_lookup"]!.GetValue<bool>() && token["exact_local_match"]!.GetValue<bool>(), "token lookup");
        Check.Equal(1, token["items"]!.AsArray().Count);
        Check.Equal(0, (await browse.BrowseAsync(new ReaderBrowseRequest("SEARCH", Query: "__autosourcarr_entry_id__:424242"), default))["items"]!.AsArray().Count);
        Check.Equal(before, crawls);
    }

    private static async Task Chapters()
    {
        using var world = new ChapterWorld();
        world.Write(ChapterWorld.SourceA, ChapterWorld.MangaA, ChapterWorld.Chapter(1, 1, "Chapter 1"));
        var fetched = new List<long>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JsonObject> Fetch(long manga, bool details, bool chapters, CancellationToken token)
        {
            lock (fetched) { fetched.Add(manga); if (fetched.Count == 2) done.TrySetResult(); }
            var id = manga == ChapterWorld.MangaA ? 1 : 11;
            return Task.FromResult(new JsonObject { ["chapters"] = new JsonArray(ChapterWorld.Chapter(id, 1, "Chapter 1"), ChapterWorld.Chapter(id + 1, 2, "Chapter 2")) });
        }
        await using var io = new BoundedWorkScheduler(2, 16);
        var tasks = new TaskRegistry();
        await using var jobs = new JobQueue(tasks);
        var browse = Browse(world, io, jobs, Fetch);

        // Source B was never checked: the stored entry answers at once and a refresh job fetches both records.
        var entry = await browse.EntryAsync(ChapterWorld.Entry, default);
        Check.Equal(ChapterWorld.Entry, entry["id"]!.GetValue<long>());
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        for (var wait = 0; wait < 200 && world.Scalar(DatabaseFile.Origins, "SELECT COUNT(*) FROM source_record_scans WHERE entry_id=1") < 2; wait++) await Task.Delay(50);
        Check.Equal(2L, world.Scalar(DatabaseFile.Origins, "SELECT COUNT(*) FROM source_record_scans WHERE entry_id=1"));
        for (var wait = 0; wait < 200 && world.Reader.ListChapters(ChapterWorld.Entry).Count < 2; wait++) await Task.Delay(50);
        Check.Equal(2, world.Reader.ListChapters(ChapterWorld.Entry).Count);

        // Fresh records do not queue another refresh.
        var refresh = new ChapterRefresh(world.Database, world.Gate, world.Writer, Fetch);
        Check.True(!refresh.ScheduleIfStale(jobs, ChapterWorld.Entry, TimeSpan.FromHours(6)));

        // Refresh fetches now and answers the entry with its chapters; an unknown work is 404.
        var refreshed = await browse.RefreshAsync(ChapterWorld.Entry, fetchDetails: false, fetchChapters: true, default);
        Check.Equal(2, refreshed["chapters"]!.AsArray().Count);
        await ThrowsAsync<KeyNotFoundException>(() => browse.RefreshAsync(424242, true, true, default));

        // A failed check is remembered on the record and leaves its chapters alone.
        var failing = new ChapterRefresh(world.Database, world.Gate, world.Writer, (_, _, _, _) => Task.FromException<JsonObject>(new HttpRequestException("source down")));
        await ThrowsAsync<HttpRequestException>(() => failing.RefreshRecordAsync(ChapterWorld.Entry, ChapterWorld.SourceB, ChapterWorld.MangaB));
        Check.Equal(1L, world.Scalar(DatabaseFile.Origins, "SELECT COUNT(*) FROM source_record_scans WHERE source_id=$s AND error='source down'", ("$s", ChapterWorld.SourceB)));
        Check.Equal(2, world.Reader.ListChapters(ChapterWorld.Entry).Count);
        await ThrowsAsync<KeyNotFoundException>(() => failing.RefreshRecordAsync(ChapterWorld.Entry, "unknown", 1));
    }

    private static ReaderBrowse Browse(ChapterWorld world, BoundedWorkScheduler io, JobQueue? jobs, Func<long, bool, bool, CancellationToken, Task<JsonObject>> fetch, Func<bool>? crawl = null) =>
        new(world.Database, world.Gate, new Read(), world.Reader, new ChapterRefresh(world.Database, world.Gate, world.Writer, fetch), io,
            () => new ReaderFreshness(TimeSpan.FromMinutes(10), TimeSpan.FromHours(6), 50)) { Jobs = jobs, ScheduleFeedRefresh = crawl };

    private static async Task ThrowsAsync<T>(Func<Task> run) where T : Exception
    {
        try { await run(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected exception " + typeof(T).Name);
    }

    private sealed class Read : IReaderCatalogueRead
    {
        public Task<JsonObject> SourcesAsync(CancellationToken cancellationToken) => Task.FromResult(new JsonObject { ["items"] = new JsonArray() });
        public Task<JsonObject> EntryAsync(long entryId, CancellationToken cancellationToken) =>
            entryId == ChapterWorld.Entry ? Task.FromResult(new JsonObject { ["id"] = entryId }) : Task.FromException<JsonObject>(new KeyNotFoundException("Entry not found"));
    }
}
