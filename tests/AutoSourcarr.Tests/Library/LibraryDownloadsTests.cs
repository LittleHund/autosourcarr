using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Library;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Tests.Matching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests.Library;

/// <summary>
/// Phase 10 CBZ library (CHAP-7): safe names (unicode, slashes, very long titles), ComicInfo.xml and details.json,
/// atomic writes and resume, per-source queue order, monitoring picking up a new chapter, the full download pipeline
/// into a temp library checked against Mihon's local source and Kavita's naming rules (the same checks as
/// scripts/check-cbz-library.py, which also runs when python3 is available) and the admin-only /api/v1 routes.
/// </summary>
public static class LibraryDownloadsTests
{
    private static readonly DecisionActor Person = DecisionActor.User("owner");

    public static void Register(TestSuite suite)
    {
        suite.Add("library: names are safe on Windows and Linux for unicode, slashes and very long titles", () =>
        {
            Check.Equal("Fate_Zero", LibraryNames.Component("Fate/Zero"));
            Check.Equal("Re_Zero_ Starting Life", LibraryNames.Component("Re\\Zero: Starting   Life"));
            Check.Equal("Café", LibraryNames.Component("Café"));
            Check.Equal("_CON", LibraryNames.Component("CON"));
            Check.Equal("_aux.txt", LibraryNames.Component("aux.txt"));
            Check.Equal("Untitled", LibraryNames.Component(" ... "));
            Check.Equal("Untitled", LibraryNames.Component(".."));
            Check.Equal("Title", LibraryNames.Component("Title...  "));
            Check.Equal("A_B", LibraryNames.Component("A\u0000B"));
            Check.True(!LibraryNames.Component("../../etc/passwd").Contains('/'), "No separators survive");
            Check.Equal("葬送のフリーレン", LibraryNames.Component("葬送のフリーレン"));

            var longLatin = string.Concat(Enumerable.Repeat("Overly Long Light Novel Title ", 20));
            var cut = LibraryNames.SeriesFolder(longLatin + "A");
            Check.True(Encoding.UTF8.GetByteCount(cut) <= LibraryNames.SeriesFolderBytes, "Capped in bytes");
            Check.True(Regex.IsMatch(cut, " ~[0-9a-f]{8}$"), "A capped name keeps a short hash: " + cut);
            Check.True(cut != LibraryNames.SeriesFolder(longLatin + "B"), "Two long titles stay distinct");
            var longJapanese = string.Concat(Enumerable.Repeat("転生したらスライムだった件👨‍👩‍👧", 20));
            var japanese = LibraryNames.SeriesFolder(longJapanese);
            Check.True(Encoding.UTF8.GetByteCount(japanese) <= LibraryNames.SeriesFolderBytes, "Capped in UTF-8 bytes");
            Check.True(!japanese.Any(char.IsSurrogate) || japanese.EnumerateRunes().All(rune => rune.Value != 0xFFFD), "No split surrogate pairs");

            Check.Equal("Frieren Vol.01 Ch.001.cbz", LibraryNames.ChapterFile("Frieren", 1, 1, "Chapter 1"));
            Check.Equal("Frieren Ch.012.5.cbz", LibraryNames.ChapterFile("Frieren", 12.5, -1, ""));
            Check.Equal("Frieren Ch.1000.cbz", LibraryNames.ChapterFile("Frieren", 1000, -1, ""));
            // A title with digits never leads the name: Kavita would read "8" as a chapter.
            Check.Equal("Ch.003.cbz", LibraryNames.ChapterFile("Kaiju No. 8", 3, -1, ""));
            Check.Equal("Vol.02 Ch.010.cbz", LibraryNames.ChapterFile("Kaiju No. 8", 10, 2, ""));
            Check.Equal("Frieren Ch.007 [55].cbz", LibraryNames.ChapterFile("Frieren", 7, -1, "", collision: 55));
            var special = LibraryNames.ChapterFile("Frieren", -1, -1, "Side Story 2: Extra");
            Check.True(!special.Any(char.IsDigit) && special.EndsWith(".cbz", StringComparison.Ordinal), special);
            var longFile = LibraryNames.ChapterFile(LibraryNames.SeriesFolder(longLatin), 1234.5, 12, "");
            Check.True(Encoding.UTF8.GetByteCount(longFile) <= LibraryNames.ChapterFileBytes, "Chapter file capped: " + longFile);
            Check.True(longFile.EndsWith("Vol.12 Ch.1234.5.cbz", StringComparison.Ordinal), longFile);

            using var temp = new Sandbox();
            Check.True(LibraryNames.Inside(temp.Path, "Series", "Ch.001.cbz").StartsWith(temp.Path, StringComparison.Ordinal), "Inside stays in the root");
            Check.Throws<ArgumentException>(() => LibraryNames.Inside(temp.Path, ".."));
            Check.Throws<ArgumentException>(() => LibraryNames.Inside(temp.Path, "a/../../b"));
            Check.Throws<ArgumentException>(() => LibraryNames.Inside(temp.Path, Path.GetTempPath()));
        });

        suite.Add("library: ComicInfo.xml and details.json carry the series and chapter fields", () =>
        {
            var series = new LibrarySeriesInfo(1, "Fate/Zero \u0001", "Gen Urobuchi", "Shinjirou", "Summary <with> & markup",
                ["Action", "Drama", "action", " "], 2);
            var chapter = new LibraryChapterInfo(10, 1, 12.5, 3, "Chapter 12.5: The Grail", "1", "Harborlight", "Group A",
                new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), "en");
            var xml = XDocument.Parse(Encoding.UTF8.GetString(ComicInfoDocument.Build(series, chapter, 7)));
            var root = xml.Root!;
            Check.Equal("ComicInfo", root.Name.LocalName);
            string Value(string name) => root.Element(name)?.Value ?? "";
            Check.Equal("Chapter 12.5: The Grail", Value("Title"));
            Check.Equal("Fate/Zero", Value("Series"));
            Check.Equal("12.5", Value("Number"));
            Check.Equal("3", Value("Volume"));
            Check.Equal("Summary <with> & markup", Value("Summary"));
            Check.Equal("Gen Urobuchi", Value("Writer"));
            Check.Equal("Shinjirou", Value("Penciller"));
            Check.Equal("Group A", Value("Translator"));
            Check.Equal("Action, Drama", Value("Genre"));
            Check.Equal("7", Value("PageCount"));
            Check.Equal("en", Value("LanguageISO"));
            Check.Equal("2026", Value("Year"));
            Check.Equal("3", Value("Month"));
            Check.Equal("4", Value("Day"));
            Check.Equal("Yes", Value("Manga"));
            Check.Equal("", Value("Format"));
            var order = root.Elements().Select(element => ComicInfoDocument.ElementOrder.ToList().IndexOf(element.Name.LocalName)).ToArray();
            Check.True(order.All(index => index >= 0) && order.SequenceEqual(order.Order()), "Schema sequence order");

            var unnumbered = XDocument.Parse(Encoding.UTF8.GetString(ComicInfoDocument.Build(series, chapter with { Number = -1, Volume = -1, Title = "" }, 1))).Root!;
            Check.Equal("Special", unnumbered.Element("Format")?.Value);
            Check.Equal("Special", unnumbered.Element("Title")?.Value);
            Check.True(unnumbered.Element("Number") is null && unnumbered.Element("Volume") is null, "No number for a special");

            var details = JsonNode.Parse(ComicInfoDocument.Details(series))!.AsObject();
            Check.Equal("Fate/Zero", details["title"]!.GetValue<string>());
            Check.Equal("Gen Urobuchi", details["author"]!.GetValue<string>());
            Check.Equal("2", details["status"]!.GetValue<string>());
            Check.Equal("Action|Drama", string.Join('|', details["genre"]!.AsArray().Select(node => node!.GetValue<string>())));
        });

        suite.Add("library: writes are atomic and an interrupted chapter resumes from its staged pages", () =>
        {
            using var setup = new Setup(options => options with { PageAttempts = 1 });
            // Atomic replace leaves no temp file; a failed pack leaves neither a target nor a temp file.
            var folder = Path.Combine(setup.Options.Root, "Atomic");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "a.json");
            CbzFiles.WriteAtomic(file, "old"u8);
            CbzFiles.WriteAtomic(file, "new"u8);
            Check.Equal("new", File.ReadAllText(file));
            Check.Throws<FileNotFoundException>(() => CbzFiles.Pack(Path.Combine(folder, "Ch.001.cbz"), [Path.Combine(folder, "missing.jpg")], "<ComicInfo/>"u8));
            Check.True(!File.Exists(Path.Combine(folder, "Ch.001.cbz")), "No target after a failed pack");
            Check.Equal(0, Directory.EnumerateFiles(folder, ".*").Count());

            var entry = setup.Work("Lantern Keeper", [Chapter(101, 0, 1, "1")]);
            setup.Pages.PagesPerChapter = 3;
            setup.Pages.FailOnce.Add((101, 2));
            setup.Store.SetMembership(entry, true, "Lantern Keeper");
            setup.Store.Enqueue(entry, setup.Catalogue.Chapters(entry));
            var item = setup.Store.Queued(101)!;
            Check.Throws<IOException>(() => setup.Downloads.DownloadAsync(item, null, CancellationToken.None).GetAwaiter().GetResult());
            var series = Path.Combine(setup.Options.Root, "Lantern Keeper");
            Check.True(!Directory.Exists(series) || !Directory.EnumerateFiles(series, "*.cbz").Any(), "Nothing half-written reaches the library");
            var staging = Path.Combine(setup.Options.StagingRoot, "101");
            Check.True(CbzFiles.StagedPage(staging, 0) is not null && CbzFiles.StagedPage(staging, 1) is not null && CbzFiles.StagedPage(staging, 2) is null, "Fetched pages are kept");

            setup.Pages.Fetched.Clear();
            setup.Downloads.DownloadAsync(item, null, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal("101:2", string.Join(',', setup.Pages.Fetched.Select(page => page.Chapter + ":" + page.Page)));
            var cbz = Path.Combine(series, "Lantern Keeper Ch.001.cbz");
            using (var archive = ZipFile.OpenRead(cbz))
                Check.Equal("001.jpg,002.jpg,003.jpg,ComicInfo.xml", string.Join(',', archive.Entries.Select(zipEntry => zipEntry.FullName)));
            Check.True(!Directory.Exists(staging), "Staging is removed once the chapter is packed");
            Check.Equal(0, Directory.EnumerateFiles(series, ".*").Count());
            Check.Equal("Lantern Keeper/Lantern Keeper Ch.001.cbz", setup.Store.Downloaded(entry)[101]);

            // A process that stopped mid-download puts the chapter back in the queue on start.
            setup.Store.Enqueue(entry, [Chapter(102, 0, 2, "1")]);
            Check.True(setup.Store.Start(102), "Started");
            Check.Equal(1, setup.Store.ResetInterrupted());
            Check.Equal("queued", setup.Store.Queued(102)!.State);
        });

        suite.Add("library: a merge moves library membership, queued and downloaded chapters to the surviving work", () =>
        {
            using var setup = new Setup();
            var target = setup.Work("Harbor Lights", [Chapter(701, 0, 1, "1")]);
            var absorbed = setup.Work("Harbor Lights (alt)", [Chapter(702, 0, 1, "1"), Chapter(703, 0, 2, "1")]);
            setup.Store.SetMembership(absorbed, true, "Harbor Lights (alt)");
            setup.Store.Enqueue(absorbed, [Chapter(703, 0, 2, "1")]);
            setup.Database.Write(DatabaseFile.Catalog, c => c.Execute(
                "INSERT INTO library_downloads(chapter_id,entry_id,file,pages,bytes,candidate_id,source_id,downloaded_at) VALUES(702,$e,'Harbor Lights (alt)/Ch.001.cbz',3,10,0,'1','now')",
                ("$e", absorbed)));
            setup.Database.Write(DatabaseFile.Catalog, c =>
            {
                AutoSourcarr.Core.Catalogue.Structure.CataloguePreferenceLineage.Reparent(c, target, absorbed, "now", (_, _, _, _) => { });
                return 0;
            });
            // The surviving work is in the library with the absorbed folder (it had none), its queue and its download.
            Check.True(setup.Store.Series(target) is { InLibrary: true, Folder: "Harbor Lights (alt)" }, "membership follows the merge");
            Check.True(setup.Store.Series(absorbed) is null, "no membership left on the absorbed work");
            Check.Equal(target, setup.Store.Queued(703)!.EntryId);
            Check.True(setup.Store.Downloaded(target).ContainsKey(702), "download row follows the merge");

            // Both in the library: the surviving work keeps its own folder.
            var third = setup.Work("Harbor Lights (third)", [Chapter(704, 0, 3, "1")]);
            setup.Store.SetMembership(third, true, "Harbor Lights (third)");
            setup.Database.Write(DatabaseFile.Catalog, c =>
            {
                AutoSourcarr.Core.Catalogue.Structure.CataloguePreferenceLineage.Reparent(c, target, third, "now", (_, _, _, _) => { });
                return 0;
            });
            Check.Equal("Harbor Lights (alt)", setup.Store.Series(target)!.Folder);
            Check.True(setup.Store.Series(third) is null, "the absorbed membership row is removed");
        });

        suite.Add("library: a fake two-chapter series downloads through the full pipeline into a library Mihon and Kavita read (CHAP-7)", () =>
        {
            using var setup = new Setup();
            const string title = "Sōsō no Frieren: Beyond Journey's End";
            var entry = setup.Work(title, [Chapter(501, 0, 1, "1", volume: 1, name: "Chapter 1: The Journey's End"), Chapter(502, 0, 2, "1", name: "Chapter 2: It Didn't Have to Be Magic...")]);
            var controls = setup.Manager.Controls(entry);
            Check.True(controls["in_library"]!.GetValue<bool>() == false, "Not in the library yet");
            var added = setup.Manager.SetMembershipAsync(entry, true, false, controls["revision"]!.GetValue<long>(), Person).GetAwaiter().GetResult();
            Check.True(added["in_library"]!.GetValue<bool>(), "Added");
            var folderName = added["series_folder"]!.GetValue<string>();
            Check.Equal("Sōsō no Frieren_ Beyond Journey's End", folderName);
            var queued = setup.Manager.EnqueueMissingAsync(entry, Person).GetAwaiter().GetResult();
            Check.Equal(2, queued["queued"]!.GetValue<int>());
            setup.WaitIdle();

            var series = Path.Combine(setup.Options.Root, folderName);
            Check.Equal($"{folderName} Ch.002.cbz|{folderName} Vol.01 Ch.001.cbz",
                string.Join('|', Directory.EnumerateFiles(series, "*.cbz").Select(Path.GetFileName).Order(StringComparer.Ordinal)));
            Check.True(File.Exists(Path.Combine(series, "cover.jpg")) && File.Exists(Path.Combine(series, "details.json")), "Series files");
            var errors = CbzLibraryCheck.Errors(setup.Options.Root);
            Check.True(errors.Count == 0, string.Join("\n", errors));
            Check.Equal(title, JsonNode.Parse(File.ReadAllText(Path.Combine(series, "details.json")))!["title"]!.GetValue<string>());
            CbzLibraryCheck.RunPythonChecker(setup.Options.Root, 1, 2);
            // The CI probe opens this library in Kavita (scripts/kavita-check.sh): LIBRARY_EXPORT receives a copy.
            if (Environment.GetEnvironmentVariable("LIBRARY_EXPORT") is { Length: > 0 } export)
            {
                foreach (var file in Directory.EnumerateFiles(setup.Options.Root, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(export, Path.GetRelativePath(setup.Options.Root, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
            }

            var observed = setup.Manager.ObserveAsync(entry).GetAwaiter().GetResult();
            var observation = observed["observation"]!.AsObject();
            Check.Equal(2, observation["downloaded"]!.GetValue<int>());
            Check.Equal(0, observation["missing_downloads"]!.GetValue<int>());
            Check.Equal("checked", observation["coverage_state"]!.GetValue<string>());
            var page = setup.Manager.Page(new LibraryQuery());
            Check.Equal(1L, page["total"]!.GetValue<long>());
            Check.Equal(2L, page["entries"]![0]!["library_stats"]!["downloaded_chapter_count"]!.GetValue<long>());
            Check.Equal(0L, setup.Manager.Page(new LibraryQuery { Scope = "missing_downloads" })["total"]!.GetValue<long>());
            Check.Equal(1L, setup.Manager.Page(new LibraryQuery { Scope = "catalog", Search = "Frieren", Sort = "title" })["total"]!.GetValue<long>());
            Check.Throws<LibraryConflictException>(() => setup.Manager.Page(new LibraryQuery { Cursor = "bm90LWEtY3Vyc29y" }));
            Check.Equal("library.add,library.download", string.Join(',', setup.Log.Kinds));
            Check.Equal(2, setup.Store.DownloadHistory(0, 10)["total"]!.GetValue<long>());

            // Removing keeps the files; a stale revision is refused.
            var stale = added["revision"]!.GetValue<long>() - 1;
            Check.Throws<LibraryConflictException>(() => setup.Manager.SetMembershipAsync(entry, false, false, stale, Person).GetAwaiter().GetResult());
            var current = setup.Manager.Controls(entry)["revision"]!.GetValue<long>();
            setup.Manager.SetMembershipAsync(entry, false, false, current, Person).GetAwaiter().GetResult();
            Check.Equal(2, Directory.EnumerateFiles(series, "*.cbz").Count());
            Check.Throws<LibraryConflictException>(() => setup.Manager.EnqueueMissingAsync(entry, Person).GetAwaiter().GetResult());
        });

        suite.Add("library: each source downloads one chapter at a time, in queue order", () =>
        {
            using var setup = new Setup();
            setup.Pages.Delay = TimeSpan.FromMilliseconds(15);
            var entry = setup.Work("Clockwork Orchard", [
                Chapter(201, 0, 1, "1"), Chapter(202, 0, 2, "2"), Chapter(203, 0, 3, "1"),
                Chapter(204, 0, 4, "2"), Chapter(205, 0, 5, "1"), Chapter(206, 0, 6, "2")]);
            AddToLibrary(setup, entry);
            var stopped = setup.Manager.QueueActionAsync("stop", setup.Manager.Queue()["revision"]!.GetValue<string>(), 0, null, Person).GetAwaiter().GetResult();
            Check.Equal("STOPPED", stopped["state"]!.GetValue<string>());
            setup.Manager.EnqueueMissingAsync(entry, Person).GetAwaiter().GetResult();
            var queue = setup.Manager.Queue();
            Check.Equal("201,202,203,204,205,206", string.Join(',', queue["queue"]!.AsArray().Select(row => row!["chapter"]!["id"]!.GetValue<long>())));
            Check.Throws<LibraryConflictException>(() => setup.Manager.QueueActionAsync("move", "stale", 205, 0, Person).GetAwaiter().GetResult());
            var moved = setup.Manager.QueueActionAsync("move", queue["revision"]!.GetValue<string>(), 205, 0, Person).GetAwaiter().GetResult();
            Check.Equal("205,201,202,203,204,206", string.Join(',', moved["queue"]!.AsArray().Select(row => row!["chapter"]!["id"]!.GetValue<long>())));
            Thread.Sleep(100);
            Check.Equal(0, setup.Pages.Fetched.Count);
            setup.Manager.QueueActionAsync("start", moved["revision"]!.GetValue<string>(), 0, null, Person).GetAwaiter().GetResult();
            setup.WaitIdle();
            Check.Equal("205,201,203", string.Join(',', setup.Pages.Order("1")));
            Check.Equal("202,204,206", string.Join(',', setup.Pages.Order("2")));
            Check.Equal(1, setup.Pages.MaxActive("1"));
            Check.Equal(1, setup.Pages.MaxActive("2"));
            Check.True(setup.Store.Downloaded(entry).Count == 6, "Six chapters downloaded; queue: " + setup.Manager.Queue().ToJsonString());
            Check.True(setup.Tasks.History(0, 50)["items"]!.AsArray().Any(row => row!["name"]!.GetValue<string>().StartsWith("Download chapters", StringComparison.Ordinal)),
                "Per-source download lanes appear in Tasks");
        });

        suite.Add("library: a failed chapter stays in the queue with its error and can be retried", () =>
        {
            using var setup = new Setup(options => options with { PageAttempts = 2 });
            var entry = setup.Work("Quill Garden", [Chapter(401, 0, 1, "1")]);
            setup.Pages.AlwaysFail.Add(401);
            AddToLibrary(setup, entry);
            setup.Manager.EnqueueMissingAsync(entry, Person).GetAwaiter().GetResult();
            setup.WaitIdle();
            var row = setup.Manager.Queue()["queue"]!.AsArray().Single()!;
            Check.Equal("ERROR", row["state"]!.GetValue<string>());
            Check.True(row["error"]!.GetValue<string>().Contains("unavailable", StringComparison.Ordinal), row.ToJsonString());
            Check.Equal(2, setup.Pages.Fetched.Count(page => page.Chapter == 401));
            setup.Pages.AlwaysFail.Clear();
            setup.Manager.QueueActionAsync("retry", setup.Manager.Queue()["revision"]!.GetValue<string>(), 401, null, Person).GetAwaiter().GetResult();
            setup.WaitIdle();
            Check.Equal(0, setup.Store.Queue().Count);
            Check.True(setup.Store.Downloaded(entry).ContainsKey(401), "Downloaded after retry");
            Check.True(setup.Store.DownloadHistory(0, 10)["items"]!.AsArray().Select(item => item!["event"]!.GetValue<string>()).SequenceEqual(new[] { "downloaded", "failed" }), "History");
        });

        suite.Add("library: monitoring picks up a new chapter and the interrupted check replays read-only", () =>
        {
            using var setup = new Setup();
            var entry = setup.Work("Harbor Lights", [Chapter(301, 0, 1, "1")]);
            AddToLibrary(setup, entry);
            Check.Throws<LibraryConflictException>(() => setup.Manager.SetMonitoringAsync(entry, true, 99, Person).GetAwaiter().GetResult());
            var monitored = setup.Manager.SetMonitoringAsync(entry, true, setup.Manager.Controls(entry)["revision"]!.GetValue<long>(), Person).GetAwaiter().GetResult();
            Check.True(monitored["monitored"]!.GetValue<bool>(), "Monitored");
            setup.Manager.EnqueueMissingAsync(entry, Person).GetAwaiter().GetResult();
            setup.WaitIdle();
            Check.Equal(1, setup.Store.Downloaded(entry).Count);

            setup.Catalogue.OnRefresh[entry] = [Chapter(302, entry, 2, "1")];
            var checks = setup.Manager.MonitorDue();
            Check.Equal(1, checks.Count);
            foreach (var check in checks) Check.Equal(JobOutcome.Completed, check.Completion.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult());
            setup.WaitIdle();
            Check.Equal(1, setup.Catalogue.Refreshes);
            Check.True(setup.Store.Downloaded(entry).ContainsKey(302), "The new chapter was downloaded");
            Check.True(File.Exists(Path.Combine(setup.Options.Root, "Harbor Lights", "Harbor Lights Ch.002.cbz")), "New CBZ on disk");
            Check.Equal(0, setup.Manager.MonitorDue().Count);
            Check.True(setup.Log.Entries.Any(item => item.Kind == "library.download" && item.Actor == DecisionActor.Automatic), "Automatic queueing is recorded");

            var tracking = setup.Manager.Controls(entry);
            Check.Equal("complete", tracking["due"]!["outcome"]!.GetValue<string>());
            var replay = setup.Manager.SafeReplayAsync(new TaskRecord { Kind = LibraryManager.MonitorKind, Context = entry.ToString(CultureInfo.InvariantCulture), Interrupted = true },
                CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal(2, replay["observed"]!["observation"]!["downloaded"]!.GetValue<int>());
            Check.Equal(1, setup.Catalogue.Refreshes);
            Check.Throws<LibraryConflictException>(() => setup.Manager.SafeReplayAsync(new TaskRecord { Context = "x" }, CancellationToken.None).GetAwaiter().GetResult());
        });

        suite.Add("library: the catalogue view reads series metadata from entries", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            var entry = MatchingFixtures.Entry(database, "Vinland Saga", "Makoto Yukimura");
            database.Write(DatabaseFile.Catalog, c => c.Execute("UPDATE entries SET genre='Action, History',status=1,description='Thors' WHERE id=$id", ("$id", entry)));
            var catalogue = new CatalogueLibrary(database, new ReaderRepository(database, new object()));
            var series = catalogue.Series(entry)!;
            Check.Equal("Vinland Saga", series.Title);
            Check.Equal("Makoto Yukimura", series.Author);
            Check.Equal("Action|History", string.Join('|', series.Genres));
            Check.Equal(1, series.Status);
            Check.True(catalogue.Series(entry + 100) is null, "Missing work");
            Check.Equal(0, catalogue.Chapters(entry).Count);
            Check.True(!catalogue.Coverage(entry).Checked, "No coverage source");
            Check.Equal(entry, catalogue.CanonicalEntryId(entry));
        });

        suite.Add("library: the /api/v1 library and download routes are admin-only and answer", () =>
        {
            using var setup = new Setup();
            var entry = setup.Work("Lantern Keeper", [Chapter(601, 0, 1, "1")]);
            var settings = new AutoSourcarrSettings { AdminToken = "admin" };
            var services = new ApiV1Services(() => settings, setup.DataDirectory, null) { Tasks = setup.Tasks };
            using var host = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
                .ConfigureServices(collection => collection.AddRouting())
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        var group = endpoints.MapGroup("/api/v1").AddEndpointFilter(new AdminAuthorizationFilter(() => settings));
                        LibraryDownloadsV1Endpoints.Map(group, services, ApiJsonContext.Default, new LibraryDownloadsV1Services { Manager = setup.Manager });
                    });
                })).Build();
            host.StartAsync().GetAwaiter().GetResult();
            using var anonymous = host.GetTestServer().CreateClient();
            var id = entry.ToString(CultureInfo.InvariantCulture);
            foreach (var (method, path) in new[] { ("GET", "/api/v1/library"), ("GET", "/api/v1/library/" + id + "/controls"), ("PUT", "/api/v1/library/" + id + "/membership"),
                ("PUT", "/api/v1/library/" + id + "/monitoring"), ("POST", "/api/v1/library/" + id + "/observe"), ("POST", "/api/v1/library/" + id + "/download"),
                ("GET", "/api/v1/downloads"), ("POST", "/api/v1/downloads"), ("GET", "/api/v1/activity/history") })
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                using var response = anonymous.SendAsync(request).GetAwaiter().GetResult();
                Check.True(response.StatusCode == HttpStatusCode.Unauthorized, method + " " + path + " → " + response.StatusCode);
            }
            using var admin = host.GetTestServer().CreateClient();
            admin.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
            JsonObject Send(string method, string path, string? body, HttpStatusCode expected)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = admin.SendAsync(request).GetAwaiter().GetResult();
                var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check.True(response.StatusCode == expected, method + " " + path + " → " + response.StatusCode + " " + text);
                return JsonNode.Parse(text)!.AsObject();
            }
            Check.Equal(0L, Send("GET", "/api/v1/library?limit=10", null, HttpStatusCode.OK)["total"]!.GetValue<long>());
            Check.Equal(1L, Send("GET", "/api/v1/library?scope=catalog&search=Lantern", null, HttpStatusCode.OK)["total"]!.GetValue<long>());
            Send("GET", "/api/v1/library?sort=nope", null, HttpStatusCode.Conflict);
            Send("GET", "/api/v1/library?limit=500", null, HttpStatusCode.BadRequest);
            var controls = Send("GET", "/api/v1/library/" + id + "/controls", null, HttpStatusCode.OK);
            Send("GET", "/api/v1/library/999999/controls", null, HttpStatusCode.NotFound);
            Send("PUT", "/api/v1/library/" + id + "/membership", """{"in_library":true}""", HttpStatusCode.BadRequest);
            Send("PUT", "/api/v1/library/" + id + "/monitoring", $$"""{"revision":{{controls["revision"]}},"monitored":true}""", HttpStatusCode.Conflict);
            var added = Send("PUT", "/api/v1/library/" + id + "/membership", $$"""{"revision":{{controls["revision"]}},"in_library":true,"monitored":false}""", HttpStatusCode.OK);
            Check.True(added["in_library"]!.GetValue<bool>(), "Added over HTTP");
            Send("PUT", "/api/v1/library/" + id + "/monitoring", $$"""{"revision":{{added["revision"]}},"monitored":true}""", HttpStatusCode.OK);
            Check.Equal(1, Send("POST", "/api/v1/library/" + id + "/download", null, HttpStatusCode.OK)["queued"]!.GetValue<int>());
            setup.WaitIdle();
            var observed = Send("POST", "/api/v1/library/" + id + "/observe", """{"refresh":false}""", HttpStatusCode.OK);
            Check.Equal(1, observed["observation"]!["downloaded"]!.GetValue<int>());
            var queue = Send("GET", "/api/v1/downloads", null, HttpStatusCode.OK);
            Check.Equal("STARTED", queue["state"]!.GetValue<string>());
            Send("POST", "/api/v1/downloads", """{"action":"stop","revision":"stale"}""", HttpStatusCode.Conflict);
            Send("POST", "/api/v1/downloads", """{"action":"stop"}""", HttpStatusCode.BadRequest);
            Check.Equal("STOPPED", Send("POST", "/api/v1/downloads", "{\"action\":\"stop\",\"revision\":\"" + queue["revision"]!.GetValue<string>() + "\"}", HttpStatusCode.OK)["state"]!.GetValue<string>());
            var history = Send("GET", "/api/v1/activity/history?offset=0&limit=50", null, HttpStatusCode.OK);
            Check.True(history["downloads"]!["items"]!.AsArray().Count == 1, history.ToJsonString());
            Send("GET", "/api/v1/activity/history?limit=500", null, HttpStatusCode.BadRequest);
            Check.Equal("library.add,library.monitoring,library.download,download.queue.stop", string.Join(',', setup.Log.Kinds));
            Check.True(setup.Log.Entries.All(item => item.Actor.Name.Length > 0 && item.Actor != DecisionActor.Automatic), "Route changes carry the caller");
            host.StopAsync().GetAwaiter().GetResult();
        });
    }

    private static void AddToLibrary(Setup setup, long entry) =>
        setup.Manager.SetMembershipAsync(entry, true, false, setup.Manager.Controls(entry)["revision"]!.GetValue<long>(), Person).GetAwaiter().GetResult();

    private static LibraryChapterInfo Chapter(long id, long entry, double number, string source, double volume = -1, string name = "") =>
        new(id, entry, number, volume, name.Length > 0 ? name : "Chapter " + number.ToString(CultureInfo.InvariantCulture), source,
            source == "1" ? "Harborlight" : "Quillfeed", "", 0, "en");

    /// <summary>A temp catalogue, library folder, job queue and the library services over fake pages and chapters.</summary>
    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _temp = new();
        public SqliteDatabase Database { get; }
        public InMemoryDecisionLog Log { get; } = new();
        public TaskRegistry Tasks { get; } = new();
        public JobQueue Jobs { get; }
        public FakeCatalogue Catalogue { get; } = new();
        public FakePages Pages { get; } = new();
        public LibraryOptions Options { get; }
        public LibraryStore Store { get; }
        public LibraryDownloads Downloads { get; }
        public LibraryManager Manager { get; }
        public string DataDirectory => _temp.Path;

        public Setup(Func<LibraryOptions, LibraryOptions>? configure = null)
        {
            Database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = _temp.Path });
            new SchemaMigrator(Database).Initialize();
            Jobs = new JobQueue(Tasks);
            var options = new LibraryOptions
            {
                Root = Path.Combine(_temp.Path, "library"), StagingRoot = Path.Combine(_temp.Path, "staging"),
                PageDelay = TimeSpan.Zero, ChapterDelay = TimeSpan.Zero, RetryDelay = TimeSpan.FromMilliseconds(1),
            };
            Options = configure?.Invoke(options) ?? options;
            Store = new LibraryStore(Database);
            Downloads = new LibraryDownloads(Store, Catalogue, Pages, Jobs, Options);
            Manager = new LibraryManager(Database, Store, Catalogue, Downloads, Jobs, Log);
        }

        /// <summary>A catalogue entry with series metadata and its chapter list.</summary>
        public long Work(string title, IReadOnlyList<LibraryChapterInfo> chapters)
        {
            var entry = MatchingFixtures.Entry(Database, title, "Mirela Tov");
            Catalogue.Add(new LibrarySeriesInfo(entry, title, "Mirela Tov", "", "A lighthouse keeper's story.", ["Adventure", "Fantasy"], 1),
                chapters.Select(chapter => chapter with { EntryId = entry }).ToArray());
            foreach (var chapter in chapters) Pages.SourceOf[chapter.ChapterId] = chapter.SourceId;
            return entry;
        }

        /// <summary>Waits until nothing is queued or downloading (failed chapters stay listed).</summary>
        public void WaitIdle()
        {
            var watch = Stopwatch.StartNew();
            while (Store.Queue().Any(item => item.State is "queued" or "downloading"))
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Downloads did not finish: " + Manager.Queue().ToJsonString());
                Thread.Sleep(20);
            }
            // Let the lane's job finish its bookkeeping.
            Thread.Sleep(50);
        }

        public void Dispose()
        {
            Manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Jobs.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Database.Dispose();
            _temp.Dispose();
        }
    }

    private sealed class FakeCatalogue : ILibraryCatalogue
    {
        private readonly object _gate = new();
        private readonly Dictionary<long, LibrarySeriesInfo> _series = [];
        private readonly Dictionary<long, List<LibraryChapterInfo>> _chapters = [];
        public ConcurrentDictionary<long, LibraryChapterInfo[]> OnRefresh { get; } = new();
        public int Refreshes;

        public void Add(LibrarySeriesInfo series, IReadOnlyList<LibraryChapterInfo> chapters)
        {
            lock (_gate) { _series[series.EntryId] = series; _chapters[series.EntryId] = [.. chapters]; }
        }

        public long CanonicalEntryId(long entryId) => entryId;
        public LibrarySeriesInfo? Series(long entryId) { lock (_gate) return _series.GetValueOrDefault(entryId); }
        public IReadOnlyList<LibraryChapterInfo> Chapters(long entryId) { lock (_gate) return _chapters.TryGetValue(entryId, out var list) ? list.ToArray() : []; }
        public LibraryCoverage Coverage(long entryId) => new(true, 0, DateTimeOffset.UtcNow.AddSeconds(1).ToString("o", CultureInfo.InvariantCulture));

        public Task RefreshAsync(long entryId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Refreshes);
            if (OnRefresh.TryRemove(entryId, out var added)) lock (_gate) _chapters[entryId].AddRange(added);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePages : IChapterPageSource
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _active = [];
        private readonly Dictionary<string, int> _max = [];
        private readonly Dictionary<string, List<long>> _order = [];
        public int PagesPerChapter { get; set; } = 2;
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public ConcurrentDictionary<long, string> SourceOf { get; } = new();
        public ConcurrentQueue<(long Chapter, int Page)> Fetched { get; } = new();
        public HashSet<(long Chapter, int Page)> FailOnce { get; } = [];
        public HashSet<long> AlwaysFail { get; } = [];

        public IReadOnlyList<long> Order(string source) { lock (_gate) return _order.TryGetValue(source, out var list) ? list.ToArray() : []; }
        public int MaxActive(string source) { lock (_gate) return _max.GetValueOrDefault(source); }

        public Task<LibraryChapterPages> ResolveAsync(long chapterId, CancellationToken cancellationToken) =>
            Task.FromResult(new LibraryChapterPages(chapterId * 10, PagesPerChapter, SourceOf.GetValueOrDefault(chapterId, "1")));

        public async Task<LibraryImage> PageAsync(LibraryChapterPages chapter, int pageIndex, CancellationToken cancellationToken)
        {
            var chapterId = chapter.CandidateId / 10;
            var source = chapter.SourceId;
            Fetched.Enqueue((chapterId, pageIndex));
            lock (_gate)
            {
                if (pageIndex == 0)
                {
                    if (!_order.TryGetValue(source, out var list)) _order[source] = list = [];
                    if (!list.Contains(chapterId)) list.Add(chapterId);
                }
                _active[source] = _active.GetValueOrDefault(source) + 1;
                _max[source] = Math.Max(_max.GetValueOrDefault(source), _active[source]);
            }
            try
            {
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    if (AlwaysFail.Contains(chapterId)) throw new IOException("Source is unavailable");
                    if (FailOnce.Remove((chapterId, pageIndex))) throw new IOException("Connection reset while reading the page");
                }
                var content = new byte[64];
                content[0] = 0xFF; content[1] = 0xD8; content[2] = 0xFF; content[3] = 0xE0;
                content[4] = (byte)chapterId; content[5] = (byte)pageIndex;
                return new LibraryImage(content, "image/jpeg");
            }
            finally
            {
                lock (_gate) _active[source]--;
            }
        }

        public Task<LibraryImage?> CoverAsync(long entryId, CancellationToken cancellationToken) => Task.FromResult<LibraryImage?>(null);
    }
}

/// <summary>
/// The structural checks of scripts/check-cbz-library.py in C# (Mihon local source and Kavita naming rules), so the
/// pipeline test does not depend on python; <see cref="RunPythonChecker"/> also runs the script when python3 exists.
/// </summary>
internal static class CbzLibraryCheck
{
    private static readonly Regex KavitaVolume = new(@"(vol\.? ?)(?<v>\d+(\.\d)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex KavitaChapter = new(@"(\b|_)(c|ch)(\.?\s?)(?<c>(\d+(\.\d)?)(-c?\d+(\.\d)?)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MihonVolume = new(@"\b(?:(?:vol(?:ume)?|season)[^a-z0-9]? *|[vs]\.?)([0-9]+(?:\.[0-9]+)?)\b", RegexOptions.CultureInvariant);
    private static readonly Regex MihonNumber = new(@"([0-9]+)(\.[0-9]+)?(\.?[a-z]+)?", RegexOptions.CultureInvariant);
    private static readonly Regex MihonBasic = new(@"(?<=ch\.) *([0-9]+)(\.[0-9]+)?(\.?[a-z]+)?", RegexOptions.CultureInvariant);
    private static readonly string[] Required = ["Title", "Series", "Writer", "Summary", "Genre", "PageCount"];

    public static IReadOnlyList<string> Errors(string root)
    {
        var errors = new List<string>();
        if (Directory.EnumerateFiles(root).Any(path => !Path.GetFileName(path).StartsWith('.')))
            errors.Add("Files at the library root (Kavita needs a folder per series)");
        foreach (var folder in Directory.EnumerateDirectories(root).Where(path => !Path.GetFileName(path).StartsWith('.')))
        {
            var name = Path.GetFileName(folder);
            if (name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 || name.EndsWith('.') || name.EndsWith(' ')) errors.Add(name + ": not a Windows-safe name");
            var cover = Path.Combine(folder, "cover.jpg");
            if (!File.Exists(cover) || !File.ReadAllBytes(cover).AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) errors.Add(name + ": cover.jpg missing or not a JPEG");
            string? title = null;
            var detailsPath = Path.Combine(folder, "details.json");
            if (File.Exists(detailsPath))
            {
                var details = JsonNode.Parse(File.ReadAllText(detailsPath))!.AsObject();
                title = details["title"]?.GetValue<string>();
                if (details["genre"] is not JsonArray) errors.Add(name + ": details.json genre is not a list");
                if (details["status"]?.GetValue<string>() is not { Length: 1 } status || status[0] is < '0' or > '6') errors.Add(name + ": details.json status is not \"0\"..\"6\"");
            }
            else errors.Add(name + ": details.json missing");
            var seriesValues = new HashSet<string>(StringComparer.Ordinal);
            var archives = Directory.EnumerateFiles(folder, "*.cbz").ToArray();
            if (archives.Length == 0) errors.Add(name + ": no chapters");
            foreach (var path in archives)
            {
                var where = name + "/" + Path.GetFileName(path);
                using var archive = ZipFile.OpenRead(path);
                var info = archive.GetEntry("ComicInfo.xml");
                if (info is null) { errors.Add(where + ": no ComicInfo.xml"); continue; }
                var pages = archive.Entries.Where(entry => entry.FullName != "ComicInfo.xml").ToArray();
                if (pages.Length == 0 || pages.Any(entry => entry.FullName.Contains('/', StringComparison.Ordinal))) errors.Add(where + ": pages missing or in folders");
                if (!pages.Select(entry => entry.FullName).SequenceEqual(pages.Select(entry => entry.FullName).Order(StringComparer.Ordinal))) errors.Add(where + ": page order");
                foreach (var page in pages)
                {
                    using var stream = page.Open();
                    var head = new byte[3];
                    stream.ReadExactly(head);
                    if (!head.AsSpan().SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF })) errors.Add(where + ": " + page.FullName + " is not an image");
                }
                XElement root2;
                using (var stream = info.Open()) root2 = XDocument.Load(stream).Root!;
                var names = root2.Elements().Select(element => element.Name.LocalName).ToArray();
                var order = names.Select(element => ComicInfoDocument.ElementOrder.ToList().IndexOf(element)).ToArray();
                if (order.Any(index => index < 0) || !order.SequenceEqual(order.Order()) || names.Distinct().Count() != names.Length) errors.Add(where + ": ComicInfo order " + string.Join(',', names));
                string Value(string element) => root2.Element(element)?.Value.Trim() ?? "";
                var special = Value("Format") == "Special";
                foreach (var element in special ? Required : [.. Required, "Number"])
                    if (Value(element).Length == 0) errors.Add(where + ": ComicInfo " + element + " missing");
                if (Value("PageCount") != pages.Length.ToString(CultureInfo.InvariantCulture)) errors.Add(where + ": PageCount");
                seriesValues.Add(Value("Series"));
                if (special) continue;
                var number = double.Parse(Value("Number"), CultureInfo.InvariantCulture);
                var stem = Path.GetFileNameWithoutExtension(path);
                var chapter = KavitaChapter.Match(stem);
                if (!chapter.Success || Math.Abs(Kavita(chapter.Groups["c"].Value) - Math.Floor(number * 10) / 10) > 1e-9)
                    errors.Add(where + ": Kavita reads chapter " + chapter.Groups["c"].Value + ", ComicInfo says " + Value("Number"));
                var volume = KavitaVolume.Match(stem);
                if (volume.Success && (Value("Volume").Length == 0 || Math.Abs(Kavita(volume.Groups["v"].Value) - double.Parse(Value("Volume"), CultureInfo.InvariantCulture)) > 1e-9))
                    errors.Add(where + ": Kavita volume " + volume.Groups["v"].Value + " vs ComicInfo " + Value("Volume"));
                if (Math.Abs(Mihon(name, stem) - number) > 1e-9) errors.Add(where + ": Mihon reads " + Mihon(name, stem).ToString(CultureInfo.InvariantCulture));
                var marker = Regex.Match(stem, @"(?:^|\s)(?:Vol\.\d|Ch\.\d)", RegexOptions.CultureInvariant);
                if (marker.Success && stem[..marker.Index].Any(char.IsDigit)) errors.Add(where + ": digits before the markers");
            }
            if (seriesValues.Count != 1) errors.Add(name + ": ComicInfo Series differs between chapters");
            if (title is not null && !seriesValues.Contains(title)) errors.Add(name + ": details.json title differs from ComicInfo Series");
        }
        return errors;
    }

    /// <summary>Runs scripts/check-cbz-library.py when the repository and python3 are available (CI has both).</summary>
    public static void RunPythonChecker(string root, int series, int chapters)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? script = null;
        for (; directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "check-cbz-library.py");
            if (File.Exists(candidate)) { script = candidate; break; }
        }
        if (script is null) return;
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-I", script, root, "--expect-series", series.ToString(CultureInfo.InvariantCulture), "--expect-chapters", chapters.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception) { return; }
        if (process is null) return;
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(60_000)) { process.Kill(); throw new TimeoutException("check-cbz-library.py did not finish"); }
            Check.True(process.ExitCode == 0, "check-cbz-library.py failed:\n" + output.GetAwaiter().GetResult() + error);
        }
    }

    private static double Kavita(string value)
    {
        var match = Regex.Match(value, @"\d+(\.\d)?", RegexOptions.CultureInvariant);
        return double.Parse(match.Value, CultureInfo.InvariantCulture);
    }

    private static double Mihon(string series, string name)
    {
        var clean = name.ToLowerInvariant().Replace(series.ToLowerInvariant(), "", StringComparison.Ordinal);
        clean = MihonVolume.Replace(clean, " ").Trim().Replace(',', '.').Replace('-', '.');
        var numbers = MihonNumber.Matches(clean);
        var match = numbers.Count > 1 ? MihonBasic.Match(clean) : Match.Empty;
        if (!match.Success) match = numbers.Count > 0 ? numbers[0] : Match.Empty;
        if (!match.Success) return -1;
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            + (match.Groups[2].Success && match.Groups[2].Value.Length > 0 ? double.Parse("0" + match.Groups[2].Value, CultureInfo.InvariantCulture) : 0);
    }
}
