using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Library;

namespace AutoSourcarr.Tests.Sources;

/// <summary>Port checks for the main-library (Subbed) view: 0.4.5 subbed_entry_ids, cached_library_status, main_download_size_status.</summary>
public static class SuwayomiLibraryTests
{
    private const string MainUrl = "http://main.test:4567";
    private const string LibraryNodes = """
        [{"id":1,"sourceId":"777","url":"/manga/17","title":"Old alias","inLibrary":true,"downloadCount":3,"chapters":{"totalCount":10}},
         {"id":2,"sourceId":"777","url":"41/","title":"Work","inLibrary":true,"downloadCount":5,"chapters":{"totalCount":8}},
         {"id":3,"sourceId":"5","url":"42","title":"Other source","inLibrary":true},
         {"id":4,"sourceId":"777","url":"99","title":"Deleted entry","inLibrary":true},
         {"id":5,"sourceId":"777","url":"no-id","title":"Not an entry","inLibrary":true}]
        """;

    public static void Register(TestSuite suite)
    {
        suite.Add("main library: subbed set maps reader-source urls to canonical live entries and keeps the highest counts", () =>
        {
            using var setup = new Setup();
            var payload = setup.Library.SubbedEntryIdsAsync(force: true).GetAwaiter().GetResult();
            Check.True(payload["ok"]!.GetValue<bool>(), "live read succeeds");
            var ids = payload["entry_ids"]!.AsArray();
            Check.Equal(1, ids.Count);
            Check.Equal(41L, ids[0]!.GetValue<long>()); // 17 is an alias of 41; 42 is on another source; 99 is not in the catalogue
            var stat = payload["stats_by_entry"]!["41"]!;
            Check.Equal(10L, stat["chapter_count"]!.GetValue<long>());
            Check.Equal(5L, stat["downloaded_chapter_count"]!.GetValue<long>());
            var items = payload["items"]!.AsArray();
            Check.Equal(2, items.Count);
            Check.True(items.All(item => item!["autosourcarr_entry_id"]!.GetValue<long>() == 41), "items carry the canonical entry id");
            var saved = setup.Preferences.Get(SuwayomiLibrary.DisplaySnapshotKey)!.AsObject();
            Check.Equal(MainUrl, saved["origin"]!.GetValue<string>());
            Check.Equal(41L, saved["payload"]!["entry_ids"]![0]!.GetValue<long>());
        });

        suite.Add("main library: url entry ids and second-pass title queries follow 0.4.5", () =>
        {
            Check.Equal(17L, SuwayomiLibrary.UrlEntryId(" /manga/17// "));
            Check.Equal(41L, SuwayomiLibrary.UrlEntryId("41"));
            Check.True(SuwayomiLibrary.UrlEntryId("/manga/abc") is null);
            Check.True(SuwayomiLibrary.UrlEntryId("") is null);
            Check.Equal(0, SuwayomiLibrary.FallbackTitleQueries("The Hero Returns").Count); // short titles keep "The"
            Check.True(SuwayomiLibrary.FallbackTitleQueries("The  Hero Returns To Town").Contains("Hero Returns To Town"));
            Check.Equal(0, SuwayomiLibrary.FallbackTitleQueries("   ").Count);
        });

        suite.Add("main library: status is cache-first and never waits for main Suwayomi", () =>
        {
            using var setup = new Setup();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            setup.Main.LibraryGate = release.Task;
            var empty = setup.Library.CachedLibraryStatus();
            Check.True(empty["ok"] is null, "no snapshot yet: ok is unknown");
            Check.True(empty["refreshing"]!.GetValue<bool>());
            Check.Equal(0, empty["subbed_entries"]!.GetValue<int>());
            Check.Equal("Download size is refreshing", empty["download_size"]!["error"]!.GetValue<string>());

            Check.True(setup.Library.ScheduleStatusRefresh(), "first poll starts a refresh");
            Check.True(setup.Main.LibraryEntered.Task.Wait(TimeSpan.FromSeconds(10)), "refresh reached main Suwayomi");
            Check.True(!setup.Library.ScheduleStatusRefresh(), "one refresh at a time");
            var during = setup.Library.CachedLibraryStatus(); // main Suwayomi is blocked: this must still answer
            Check.True(during["ok"] is null);

            release.SetResult();
            Check.True(setup.Library.RefreshCompletion.Wait(TimeSpan.FromSeconds(10)), "refresh finishes");
            var done = setup.Library.CachedLibraryStatus();
            Check.True(done["ok"]!.GetValue<bool>());
            Check.True(!done["refreshing"]!.GetValue<bool>());
            Check.Equal(1, done["subbed_entries"]!.GetValue<int>());
            Check.Equal(5L, done["downloaded_chapters"]!.GetValue<long>());
            Check.True(!done["download_size"]!["configured"]!.GetValue<bool>(), "no downloads path configured");
            Check.True(!setup.Library.ScheduleStatusRefresh(), "fresh caches start no work");

            Check.Equal(1, setup.Main.Library);
            setup.Library.SubbedEntryIdsAsync().GetAwaiter().GetResult();
            Check.Equal(1, setup.Main.Library); // within SUBBED_CACHE_TTL_SECONDS
            setup.Clock.Now += TimeSpan.FromSeconds(11);
            setup.Library.SubbedEntryIdsAsync().GetAwaiter().GetResult();
            Check.Equal(2, setup.Main.Library);

            setup.Library.Invalidate();
            var invalidated = setup.Library.CachedSubbedPayload();
            Check.True(invalidated["refreshing"]!.GetValue<bool>(), "a membership change marks the view refreshing");
            Check.Equal(41L, invalidated["entry_ids"]![0]!.GetValue<long>());
        });

        suite.Add("main library: failures answer ok=false and leave the cache unknown", () =>
        {
            using var setup = new Setup(configured: false);
            var payload = setup.Library.SubbedEntryIdsAsync(force: true).GetAwaiter().GetResult();
            Check.True(!payload["ok"]!.GetValue<bool>());
            Check.Equal("MAIN_SUWAYOMI_URL is not configured", payload["error"]!.GetValue<string>());
            Check.True(setup.Library.CachedLibraryStatus()["ok"] is null);
            Check.True(setup.Library.MainMangaForEntryAsync(41, "777", ["Work"]).GetAwaiter().GetResult() is null);

            using var missing = new Setup(sources: """[{"id":"5","name":"Other"}]""");
            var notInstalled = missing.Library.SubbedEntryIdsAsync(force: true).GetAwaiter().GetResult();
            Check.Equal("AutoSourcarr source is not installed in the main Suwayomi instance", notInstalled["error"]!.GetValue<string>());
            Check.Equal(0, missing.Main.Library);
        });

        suite.Add("main library: saved display snapshot is restored per main address and canonicalised", () =>
        {
            using var setup = new Setup();
            setup.Preferences.Set(SuwayomiLibrary.DisplaySnapshotKey, JsonNode.Parse($$"""
                {"origin":"{{MainUrl}}","observed_at":"2026-10-31T00:00:00Z",
                 "payload":{"entry_ids":[17,99],"stats_by_entry":{"17":{"entry_id":17,"chapter_count":4,"downloaded_chapter_count":2},"99":{"downloaded_chapter_count":7}}}}
                """));
            setup.Library.RestoreDisplaySnapshotAsync().GetAwaiter().GetResult();
            var payload = setup.Library.CachedSubbedPayload();
            Check.True(payload["saved_snapshot"]!.GetValue<bool>());
            Check.True(payload["refreshing"]!.GetValue<bool>());
            Check.Equal(1, payload["entry_ids"]!.AsArray().Count);
            Check.Equal(41L, payload["entry_ids"]![0]!.GetValue<long>());
            Check.Equal(2L, payload["stats_by_entry"]!["41"]!["downloaded_chapter_count"]!.GetValue<long>());
            Check.Equal(2L, setup.Library.CachedLibraryStatus()["downloaded_chapters"]!.GetValue<long>());

            using var other = new Setup(mainUrl: "http://other.test:4567", database: setup);
            other.Library.RestoreDisplaySnapshotAsync().GetAwaiter().GetResult();
            Check.True(other.Library.CachedSubbedPayload()["ok"] is null, "a snapshot of another main Suwayomi is ignored");
        });

        suite.Add("main library: entry lookup tries the private entry-id token first and checks the canonical id", () =>
        {
            using var setup = new Setup();
            setup.Main.BrowseMangas = """[{"id":9,"sourceId":"777","url":"17","title":"Old alias","inLibrary":false}]""";
            Check.Equal("777", setup.Library.MainAutoSourcarrSourceIdAsync().GetAwaiter().GetResult());
            var found = setup.Library.MainMangaForEntryAsync(41, "777", ["Work"]).GetAwaiter().GetResult();
            Check.Equal(9L, found!["id"]!.GetValue<long>());
            Check.Equal(1, setup.Main.Browse);
            Check.True(setup.Main.Bodies.Any(body => body.Contains(SuwayomiLibrary.EntryIdToken + "41", StringComparison.Ordinal)));
            setup.Main.BrowseMangas = """[{"id":10,"sourceId":"777","url":"42","title":"Other edition","inLibrary":false}]""";
            Check.True(setup.Library.MainMangaForEntryAsync(41, "777", ["Work"]).GetAwaiter().GetResult() is null, "another entry's manga is never returned");
        });

        suite.Add("main library: ensure-in-library returns the confirmed manga and marks the view refreshing", () =>
        {
            using var setup = new Setup();
            setup.Library.SubbedEntryIdsAsync(force: true).GetAwaiter().GetResult();
            var confirmed = setup.Library.EnsureMainLibraryAsync(2).GetAwaiter().GetResult();
            Check.True(confirmed["inLibrary"]!.GetValue<bool>());
            Check.Equal(1, setup.Main.SetLibrary);
            Check.True(setup.Library.CachedSubbedPayload()["refreshing"]!.GetValue<bool>());
            var superseded = setup.Library.EnsureMainLibraryAsync(2, superseded: () => true).GetAwaiter().GetResult();
            Check.True(superseded["superseded"]!.GetValue<bool>());
            Check.Equal(1, setup.Main.SetLibrary); // a newer choice before the first attempt sends nothing
        });

        suite.Add("main library: download size walks the AutoSourcarr folder, caches 60 s and stops at its caps", () =>
        {
            using var temp = new Sandbox();
            var root = Path.Combine(temp.Path, "downloads");
            var own = Path.Combine(root, "AutoSourcarr (EN)");
            Directory.CreateDirectory(Path.Combine(own, "Work", "Chapter 1"));
            Directory.CreateDirectory(Path.Combine(root, "Other source"));
            File.WriteAllBytes(Path.Combine(own, "Work", "Chapter 1", "001.webp"), new byte[100]);
            File.WriteAllBytes(Path.Combine(own, "Work", "Chapter 1", "002.webp"), new byte[50]);
            File.WriteAllBytes(Path.Combine(own, "Work", "cover.jpg"), new byte[25]);
            File.WriteAllBytes(Path.Combine(root, "Other source", "big.bin"), new byte[1000]);

            using var setup = new Setup(downloads: root);
            var size = setup.Library.DownloadSizeStatusAsync().GetAwaiter().GetResult();
            Check.True(size["available"]!.GetValue<bool>());
            Check.Equal(175L, size["bytes"]!.GetValue<long>());
            Check.Equal(3L, size["files"]!.GetValue<long>());
            Check.Equal(own, size["path"]!.GetValue<string>());
            Check.True(!size["truncated"]!.GetValue<bool>());

            File.WriteAllBytes(Path.Combine(own, "Work", "cover2.jpg"), new byte[5]);
            Check.Equal(3L, setup.Library.DownloadSizeStatusAsync().GetAwaiter().GetResult()["files"]!.GetValue<long>()); // cached
            var status = setup.Library.CachedLibraryStatus()["download_size"]!;
            Check.True(!status["refreshing"]!.GetValue<bool>());
            Check.Equal(175L, status["bytes"]!.GetValue<long>());
            Check.Equal(4L, setup.Library.DownloadSizeStatusAsync(force: true).GetAwaiter().GetResult()["files"]!.GetValue<long>());

            using var capped = new Setup(downloads: root, limits: new DownloadWalkLimits(2, TimeSpan.FromMinutes(1)));
            var partial = capped.Library.DownloadSizeStatusAsync().GetAwaiter().GetResult();
            Check.True(partial["truncated"]!.GetValue<bool>(), "the walk stops at the entry cap");
            Check.True(partial["files"]!.GetValue<long>() <= 2);

            using var unmounted = new Setup(downloads: Path.Combine(temp.Path, "missing"));
            var missing = unmounted.Library.DownloadSizeStatusAsync().GetAwaiter().GetResult();
            Check.True(missing["configured"]!.GetValue<bool>() && !missing["available"]!.GetValue<bool>());
            Check.Equal("Configured download path is not mounted/readable", missing["error"]!.GetValue<string>());
        });
    }

    private sealed class Setup : IDisposable
    {
        private readonly Sandbox? _temp;
        private readonly SqliteDatabase? _ownedDatabase;
        private readonly SuwayomiClient? _client;
        public ManualClock Clock { get; } = new(DateTimeOffset.Parse("2026-11-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        public SqliteDatabase Database { get; }
        public SettingsRepository Preferences { get; }
        public BoundedWorkScheduler Io { get; } = new(2, 16);
        public FakeMain Main { get; }
        public SuwayomiLibrary Library { get; }

        public Setup(bool configured = true, string? sources = null, string mainUrl = MainUrl, Setup? database = null,
            string downloads = "", DownloadWalkLimits? limits = null)
        {
            if (database is null)
            {
                _temp = new Sandbox();
                Database = _ownedDatabase = new SqliteDatabase(new AutoSourcarrSettings { DataDir = _temp.Path });
                new SchemaMigrator(Database).Initialize();
                Database.Write(DatabaseFile.Catalog, session => session.Execute("""
                    INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(41,'Work','now','now'),(42,'Other edition','now','now');
                    INSERT INTO entry_aliases VALUES(17,41,'merge','now');
                    """));
            }
            else Database = database.Database;
            Preferences = new SettingsRepository(Database);
            var settings = new AutoSourcarrSettings
            {
                MainSuwayomiUrl = configured ? mainUrl : "", MainSuwayomiDownloadsPath = downloads, SubbedCacheTtlSeconds = 10
            };
            Main = new FakeMain(sources ?? """[{"id":"5","name":"Other"},{"id":"777","name":"AutoSourcarr","displayName":"AutoSourcarr (EN)"}]""");
            if (configured) _client = new SuwayomiClient(SettingsLoader.Load(), mainUrl, handler: Main);
            Library = new SuwayomiLibrary(Database, Preferences, () => settings, () => _client, Io, clock: Clock, limits: limits);
        }

        public void Dispose()
        {
            Library.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _client?.Dispose();
            Io.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _ownedDatabase?.Dispose();
            _temp?.Dispose();
        }
    }

    internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Main Suwayomi GraphQL stand-in, chosen by operation name.</summary>
    private sealed class FakeMain(string sources) : HttpMessageHandler
    {
        private int _sources, _library, _browse, _setLibrary;
        public int Sources => Volatile.Read(ref _sources);
        public int Library => Volatile.Read(ref _library);
        public int Browse => Volatile.Read(ref _browse);
        public int SetLibrary => Volatile.Read(ref _setLibrary);
        public List<string> Bodies { get; } = [];
        public Task LibraryGate { get; set; } = Task.CompletedTask;
        public TaskCompletionSource LibraryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string BrowseMangas { get; set; } = "[]";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (Bodies) Bodies.Add(body);
            string json;
            if (body.Contains("AutoSourcarrSources", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _sources);
                json = "{\"data\":{\"sources\":{\"nodes\":" + sources + "}}}";
            }
            else if (body.Contains("AutoSourcarrLibrary", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _library);
                LibraryEntered.TrySetResult();
                await LibraryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                json = "{\"data\":{\"mangas\":{\"nodes\":" + LibraryNodes + ",\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null}}}}";
            }
            else if (body.Contains("AutoSourcarrBrowse", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _browse);
                json = "{\"data\":{\"fetchSourceManga\":{\"hasNextPage\":false,\"mangas\":" + BrowseMangas + "}}}";
            }
            else if (body.Contains("AutoSourcarrSetLibrary", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _setLibrary);
                json = "{\"data\":{\"updateManga\":{\"manga\":{\"id\":2,\"sourceId\":\"777\",\"url\":\"41\",\"title\":\"Work\",\"inLibrary\":true}}}}";
            }
            else json = "{\"errors\":[{\"message\":\"unexpected operation\"}]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
