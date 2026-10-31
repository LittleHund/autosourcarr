using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests;

public static class ReaderServingTests
{
    private const long Entry = 41, Alias = 17, Chapter = 51, Candidate = 61, Manga = 9007199254740993, SourceChapter = 9007199254740995;
    private const string Source = "-9223372036854775808", VariantSource = "6084907896154116083";

    public static void Register(TestSuite suite)
    {
        suite.Add("reader serving: 288 released pure profile/ranking/official/display cases", Oracle);
        suite.Add("reader serving: profile CAS, retained decisions and reopen preserve exact IDs", Profiles);
        suite.Add("reader serving: aliases, gallery members and reviewed choices preserve admitted provenance", Chapters);
        suite.Add("reader serving: rejected scores retain the active candidate completeness baseline", RejectedScores);
        suite.Add("reader serving: official-source identity retains full Unicode case folding", () => Check.True(ChapterResolver.ProbablyOfficialSource("Manga Pluſ", "")));
        suite.Add("reader serving: page persistence reconciles split chapters through canonical aliases", AliasSplit);
        suite.Add("reader serving: exact ownership fences reject detached and cross-entry/source page URLs", Membership);
        suite.Add("reader serving: cached maps retain served history without duplicate repeat history", () => Cached().GetAwaiter().GetResult());
        suite.Add("reader serving: failed lookup falls back to stale admitted pages with distinct local deferral", () => Stale().GetAwaiter().GetResult());
        suite.Add("reader serving: human verification gates cached maps and fallback sources", () => HumanGate().GetAwaiter().GetResult());
        suite.Add("reader serving: shared page work survives one waiter cancellation and copies responses", () => Coalescing().GetAwaiter().GetResult());
        suite.Add("reader serving: last cancellation/shutdown joins owned source cleanup", () => LastCancellation().GetAwaiter().GetResult());
        suite.Add("reader serving: cancellation joins blocked physical SQLite admission", () => PhysicalSqlite().GetAwaiter().GetResult());
        suite.Add("reader serving: changed ownership during index lookup cannot write or open images", () => IndexOwnership().GetAwaiter().GetResult());
        suite.Add("reader serving: publication fence closes detached upstream before publishing bytes", () => Publication().GetAwaiter().GetResult());
        suite.Add("reader serving: shutdown closes and joins image transfer physical writes", () => StreamingDrain().GetAwaiter().GetResult());
        suite.Add("reader serving: callback failure cannot abandon physical image copy or closure", () => FailedStreamCancellation().GetAwaiter().GetResult());
        suite.Add("reader serving: callback failure cannot abandon final page waiter source cleanup", () => FailedWaiterCancellation().GetAwaiter().GetResult());
        suite.Add("reader serving: controlled Suwayomi HTTP stream preserves exact ID and encoding length", () => Adapter().GetAwaiter().GetResult());
        suite.Add("reader HTTP: exact routes, HEAD, reader token and profile admin separation", Http);
        suite.Add("reader HTTP: catalogue query/refresh wire and preparing responses use real middleware", CatalogueWire);
        suite.Add("reader serving: copy start rejects source detached after image acquisition", () => LateCopy().GetAwaiter().GetResult());
        suite.Add("reader HTTP: late detach fences GET/HEAD publication before any image bytes", LatePublication);
        suite.Add("reader HTTP: shutdown before GET/HEAD publication closes image and returns local deferral", ShutdownPublication);
        suite.Add("reader HTTP: copy and close faults join physical cleanup and retain recording failures", () => ImageCopyFailure().GetAwaiter().GetResult());
        suite.Add("reader HTTP: missing publication plus close failure preserves safe404 and both faults", () => ImageMissingFailure().GetAwaiter().GetResult());
        suite.Add("reader HTTP: abort before or during close propagates both faults without a response", () => ImageAbortFailure().GetAwaiter().GetResult());
        suite.Add("reader HTTP: local retirement plus close failure remains503 deferral", () => ImageRetirementFailure().GetAwaiter().GetResult());
        suite.Add("reader HTTP: started image failure joins close without replacing headers or body", () => ImageStartedFailure().GetAwaiter().GetResult());
        suite.Add("reader HTTP: HEAD close-only failure has safe error headers and never opens image bytes", () => ImageHeadFailure().GetAwaiter().GetResult());
    }

    private static void Oracle()
    {
        var root = JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("reader-serving.json")))!.AsObject();
        var count = 0;
        foreach (var row in root["cases"]!.AsArray().OfType<JsonObject>())
        {
            JsonNode? actual;
            switch (row["kind"]!.GetValue<string>())
            {
                case "season": actual = JsonValue.Create(ChapterResolver.SeasonNumber(row["name"]!.GetValue<string>())); break;
                case "display": actual = JsonValue.Create(ChapterResolver.DisplayName(row["number"]!.GetValue<double>(), row["name"]!.GetValue<string>())); break;
                case "source": actual = JsonValue.Create(ChapterResolver.ProbablyOfficialSource(row["name"]!.GetValue<string>(), row["url"]!.GetValue<string>())); break;
                case "official": actual = JsonValue.Create(ChapterResolver.OfficialRelease(row["candidate"]!.AsObject(), Set(row["official"]!))); break;
                case "score": actual = JsonValue.Create(ChapterResolver.Score(row["candidate"]!.AsObject(), row["priorities"]!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<long>(), StringComparer.Ordinal), row["maximum"]!.GetValue<long>(), Set(row["official"]!))); break;
                case "profile":
                    if (!row["valid"]!.GetValue<bool>()) { Check.Throws<ArgumentException>(() => ServingProfile.Parse(row["value"])); count++; continue; }
                    actual = ServingProfile.Parse(row["value"]).ToJson(); break;
                case "rank":
                    var candidates = row["candidates"]!.AsArray().OfType<JsonObject>().Select(item => (JsonObject)item.DeepClone()).ToArray();
                    ServingProfile.Parse(row["profile"]).Rank(candidates, row["languages"]!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>(), StringComparer.Ordinal));
                    actual = new JsonArray(candidates.Select(item => (JsonNode)item).ToArray()); break;
                default: throw new InvalidDataException("Unknown reader oracle kind");
            }
            EqualJson(row["result"], actual); count++;
        }
        Check.Equal(288, count);
    }

    private static void Profiles()
    {
        using var setup = new Setup(); var profiles = new ServingProfileRepository(setup.Database, setup.Gate);
        Check.Equal(0L, ReaderValue.Integer(profiles.Read(Entry), "revision"));
        setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("INSERT INTO entry_preference_history(canonical_entry_id,old_entry_id,title,profile_json,tracking_json,saved_at) VALUES($id,18,'Retained work','{\"sources\":[\"other\"]}','{\"monitored\":true}','then')", ("$id", Entry)));
        var saved = profiles.Save(Entry, new JsonObject { ["sources"] = new JsonArray(VariantSource, VariantSource), ["quality"] = "quality" }, 0);
        Check.Equal(1L, ReaderValue.Integer(saved, "revision")); Check.Equal(1, saved["profile"]!["sources"]!.AsArray().Count); Check.Equal(1, saved["retained"]!.AsArray().Count);
        Check.True(saved["retained"]![0]!["monitored"]!.GetValue<bool>()); Check.Throws<ArgumentException>(() => profiles.Save(Entry, new JsonObject(), 0));
        Check.Throws<ArgumentException>(() => profiles.Save(Entry, new JsonObject { ["sources"] = new JsonArray(9) }, 1));
        using var reopened = new SqliteDatabase(new AutoSourcarrSettings { DataDir = setup.Directory });
        EqualJson(saved, new ServingProfileRepository(reopened, setup.Gate).Read(Entry));
        Check.Equal(Source, new ServingProfileRepository(reopened, setup.Gate).Context(Entry).Languages.Keys.First());
        Check.Throws<KeyNotFoundException>(() => profiles.Read(999));
    }

    private static void Chapters()
    {
        using var setup = new Setup(); var chapters = setup.Repository.ListChapters(Alias); Check.Equal(1, chapters.Count);
        Check.Equal(Chapter.ToString(System.Globalization.CultureInfo.InvariantCulture), ReaderValue.Text(chapters[0], "url"));
        Check.Equal(Candidate, ReaderValue.Integer(chapters[0], "serving_candidate_id")); Check.Equal("Chapter 1: Gallery A", ReaderValue.Text(chapters[0], "display_name"));
        Check.Equal("Gallery A", ReaderValue.Text(chapters[0], "serving_source_entry_title"));
        setup.AddVariant(); var profile = new ServingProfileRepository(setup.Database, setup.Gate); profile.Save(Entry, new JsonObject { ["sources"] = new JsonArray(VariantSource) }, 0);
        var detail = setup.Repository.ChapterDetails(Chapter); Check.Equal(2, detail["candidates"]!.AsArray().Count);
        setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("INSERT INTO serving_upgrade_policies VALUES($id,1,'{\"enabled\":true}','now'); INSERT INTO serving_choices VALUES($chapter,$id,62,1,'now')", ("$id", Entry), ("$chapter", Chapter)));
        Check.Equal(62L, ReaderValue.Integer(setup.Repository.ListChapters(Entry)[0], "serving_candidate_id"));
        setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("UPDATE source_matches SET locked=1 WHERE entry_id=$id", ("$id", Entry)));
        Check.Equal(Candidate, ReaderValue.Integer(setup.Repository.ListChapters(Entry)[0], "serving_candidate_id"));
        setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("UPDATE source_config SET enabled_chapters=0 WHERE source_id=$source", ("$source", Source)));
        Check.Equal(62L, ReaderValue.Integer(setup.Repository.ListChapters(Entry)[0], "serving_candidate_id"));
    }

    private static void Membership()
    {
        using var setup = new Setup(); Check.Equal(Manga, ReaderValue.Integer(setup.Repository.PageFetchInfo(Candidate, 0), "source_manga_id"));
        setup.Database.Write(DatabaseFile.Origins, session => session.Execute("UPDATE chapter_candidates SET active=0 WHERE id=$id", ("$id", Candidate)));
        Check.Equal(Manga, ReaderValue.Integer(setup.Repository.PageFetchInfo(Candidate, 0), "source_manga_id"));
        Check.Throws<KeyNotFoundException>(() => setup.Repository.PageFetchInfo(Candidate, -1));
        Check.Throws<KeyNotFoundException>(() => setup.Repository.RecordServed(Chapter, CandidateRow(setup)));
        setup.Detach(); Check.Throws<KeyNotFoundException>(() => setup.Repository.PageFetchInfo(Candidate, 0));
        setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("UPDATE source_matches SET active=1,entry_id=42 WHERE source_id=$source", ("$source", Source)));
        Check.Throws<KeyNotFoundException>(() => setup.Repository.PageFetchInfo(Candidate, 0));
        setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("UPDATE source_matches SET entry_id=$id,source_id='different'", ("$id", Entry)));
        Check.Throws<KeyNotFoundException>(() => setup.Repository.PageFetchInfo(Candidate, 0));
    }

    private static void RejectedScores()
    {
        using var setup = new Setup();
        setup.Database.Write(DatabaseFile.Origins, session => session.Execute("""
            INSERT INTO chapter_candidates(id,virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,source_chapter_index,name,chapter_number,page_count,active,first_seen,last_seen)
            VALUES(62,51,$source,'Gallery A',$manga,200,0,'Chapter 1',1,40,0,'now','now');
            INSERT INTO chapter_candidate_rejections(source_id,source_chapter_id,rejected_at) VALUES($source,200,'now');
            """, ("$source", Source), ("$manga", Manga)));
        var rejected = setup.Repository.ChapterDetails(Chapter)["rejected_candidates"]![0]!.AsObject();
        Check.Equal(124900.0, ReaderValue.Number(rejected, "resolver_score"));
    }

    private static void AliasSplit()
    {
        using var setup = new Setup();
        setup.Database.Write(DatabaseFile.Origins, session => session.Execute("""
            UPDATE virtual_chapters SET entry_id=17 WHERE id=51;
            INSERT INTO virtual_chapters(id,entry_id,chapter_key,chapter_number,display_name,created_at,updated_at)
            VALUES(71,41,'n:1',1,'Whole','now','now'),(72,41,'n:1.1',1.1,'Part A','now','now'),(73,41,'n:1.2',1.2,'Part B','now','now');
            INSERT INTO chapter_candidates(id,virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,name,page_count,first_seen,last_seen)
            VALUES(81,71,$source,'Gallery A',$manga,1001,'Whole',20,'now','now'),(82,72,$source,'Gallery A',$manga,1002,'Part A',10,'now','now'),(83,73,$source,'Gallery A',$manga,1003,'Part B',10,'now','now');
            """, ("$source", Source), ("$manga", Manga)));
        setup.Repository.PersistPages(Chapter, CandidateRow(setup), ["https://images.invalid/resolved.png"], 0);
        Check.Equal(2L, setup.Database.Read(DatabaseFile.Origins, session => (long)session.Scalar("SELECT COUNT(*) FROM virtual_chapters WHERE id IN (72,73) AND hidden=1")!));
        Check.Equal(0L, setup.Database.Read(DatabaseFile.Origins, session => session.Query("SELECT hidden FROM virtual_chapters WHERE id=51")[0].Integer("hidden")));
    }

    private static async Task Cached()
    {
        using var setup = new Setup(); var first = await setup.Pages.ResolvePagesAsync(Chapter); Check.True(first["page_cache_hit"]!.GetValue<bool>());
        Check.Equal(0, setup.Source.Fetches); Check.Equal("/api/v1/pages/61/0", first["pages"]![0]!.GetValue<string>());
        await setup.Pages.ResolvePagesAsync(Chapter);
        Check.Equal(1L, setup.Database.Read(DatabaseFile.Origins, session => (long)session.Scalar("SELECT COUNT(*) FROM origin_history")!));
        Check.Equal("repeat", setup.Database.Read(DatabaseFile.Origins, session => session.Query("SELECT reason FROM served_origin")[0].Text("reason")));
        Check.Equal(SourceChapter, setup.Database.Read(DatabaseFile.Origins, session => session.Query("SELECT source_chapter_id FROM origin_history")[0].Integer("source_chapter_id")));
    }

    private static async Task Stale()
    {
        using var setup = new Setup(); setup.Expire(); setup.Source.Fetch = _ => throw new SourceBudgetExceededException();
        var deferred = await setup.Pages.ResolvePagesAsync(Chapter); Check.True(deferred["page_cache_stale"]!.GetValue<bool>()); Check.True(deferred["source_warning_deferred"]!.GetValue<bool>());
        Check.Equal(1, setup.Source.Fetches); setup.Source.Fetch = _ => throw new HttpRequestException("HTTP 403 blocked", null, HttpStatusCode.Forbidden);
        var failed = await setup.Pages.ResolvePagesAsync(Chapter); Check.True(!failed["source_warning_deferred"]!.GetValue<bool>());
        var calls = setup.Source.Fetches; await setup.Pages.ResolvePagesAsync(Chapter); Check.Equal(calls, setup.Source.Fetches);
    }

    private static async Task HumanGate()
    {
        using var setup = new Setup(); setup.Source.Attention = id => id == SourceChapter ? new HumanAssistanceRequiredException(new JsonObject { ["id"] = "attention", ["name"] = "Gallery", ["reason"] = "CAPTCHA" }, 3600) : null;
        setup.Source.Fetch = _ => throw setup.Source.Attention(SourceChapter)!;
        await ThrowsAsync<HumanAssistanceRequiredException>(() => setup.Pages.ResolvePagesAsync(Chapter));
        setup.AddVariant(); setup.SeedCached(62, "https://images.invalid/other.png");
        var result = await setup.Pages.ResolvePagesAsync(Chapter); Check.Equal(62L, ReaderValue.Integer(result["candidate"]!.AsObject(), "id"));
    }

    private static async Task Coalescing()
    {
        using var setup = new Setup(); setup.ClearPages(); var entered = Signal(); var release = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Source.Fetch = async token => { entered.TrySetResult(); return await release.Task.WaitAsync(token); };
        using var cancellation = new CancellationTokenSource(); var first = setup.Pages.ResolvePagesAsync(Chapter, cancellation.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = setup.Pages.ResolvePagesAsync(Chapter);
        try
        {
            cancellation.Cancel(); await ThrowsAsync<OperationCanceledException>(async () => await first);
            Check.True(!second.IsCompleted); release.TrySetResult(["https://images.invalid/new.png"]); var result = await second;
            Check.Equal(1, setup.Source.Fetches); Check.True(!result["page_cache_hit"]!.GetValue<bool>()); Check.Equal(0L, ReaderValue.Integer(setup.Repository.PageFetchInfo(Candidate, 0), "source_chapter_index"));
            var copies = await Task.WhenAll(setup.Pages.ResolvePagesAsync(Chapter), setup.Pages.ResolvePagesAsync(Chapter));
            copies[0]["pages"]!.AsArray()[0] = "changed"; Check.Equal("/api/v1/pages/61/0", copies[1]["pages"]![0]!.GetValue<string>());
        }
        finally { release.TrySetResult(["https://images.invalid/new.png"]); try { await first; } catch (OperationCanceledException) { } await second; }
    }

    private static async Task LastCancellation()
    {
        using var setup = new Setup(); setup.ClearPages(); var entered = Signal(); var cleanup = Signal(); var release = Signal();
        setup.Source.Fetch = async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return []; }
            finally { cleanup.TrySetResult(); await release.Task; }
        };
        using var cancellation = new CancellationTokenSource(); var request = setup.Pages.ResolvePagesAsync(Chapter, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
            await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(3)); var stopping = setup.Pages.StopAsync(); Check.True(!request.IsCompleted && !stopping.IsCompleted);
            release.TrySetResult(); await ThrowsAsync<OperationCanceledException>(async () => await request); await stopping;
        }
        finally { release.TrySetResult(); cancellation.Cancel(); try { await request; } catch (OperationCanceledException) { } await setup.Pages.StopAsync(); }
    }

    private static async Task PhysicalSqlite()
    {
        using var setup = new Setup(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var blocking = Task.Run(() => setup.Database.Quiescent(_ => { entered.Set(); Check.True(release.Wait(TimeSpan.FromSeconds(3))); return 0; }));
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(3))); using var cancellation = new CancellationTokenSource();
            var request = setup.Pages.OpenImageAsync(Candidate, 0, cancellation.Token);
            Check.True(SpinWait.SpinUntil(() => setup.Io.Snapshot.Running > 0, TimeSpan.FromSeconds(3))); cancellation.Cancel();
            Check.True(!request.IsCompleted); release.Set(); await blocking; await ThrowsAsync<OperationCanceledException>(async () => await request); Check.Equal(0, setup.Source.Opens);
        }
        finally { release.Set(); await blocking; }
    }

    private static async Task IndexOwnership()
    {
        foreach (var deferred in new[] { false, true })
        {
            using var setup = new Setup(); setup.Repository.SetChapterIndex(Candidate, -1); var entered = Signal(); var release = Signal();
            setup.Source.Index = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); if (deferred) throw new SourceBudgetExceededException(); return 0; };
            var request = setup.Pages.OpenImageAsync(Candidate, 0);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); setup.Detach(); release.TrySetResult();
                await ThrowsAsync<KeyNotFoundException>(async () => await request); Check.Equal(0, setup.Source.Opens);
                Check.Equal(-1L, setup.Database.Read(DatabaseFile.Origins, session => session.Query("SELECT source_chapter_index FROM chapter_candidates")[0].Integer("source_chapter_index")));
            }
            finally { release.TrySetResult(); try { await using var image = await request; } catch (KeyNotFoundException) { } }
        }
    }

    private static async Task Publication()
    {
        using var setup = new Setup(); var headers = Signal(); var release = Signal(); var closing = Signal(); var closeRelease = Signal(); var reads = 0; var closes = 0;
        setup.Source.Open = async token =>
        {
            headers.TrySetResult(); await release.Task.WaitAsync(token);
            return new(_ => { reads++; return Task.FromResult<Stream>(new MemoryStream([1, 2])); }, async () => { closes++; closing.TrySetResult(); await closeRelease.Task; });
        };
        var request = setup.Pages.OpenImageAsync(Candidate, 0);
        try
        {
            await headers.Task.WaitAsync(TimeSpan.FromSeconds(3)); setup.Detach(); release.TrySetResult();
            await closing.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(!request.IsCompleted); closeRelease.TrySetResult();
            await ThrowsAsync<KeyNotFoundException>(async () => await request); Check.Equal(0, reads); Check.Equal(1, closes);
        }
        finally { release.TrySetResult(); closeRelease.TrySetResult(); try { await using var image = await request; } catch (KeyNotFoundException) { } }
    }

    private static async Task StreamingDrain()
    {
        using var setup = new Setup(); var closed = 0; var closeEntered = Signal();
        setup.Source.Open = _ => Task.FromResult(new ReaderImageStream(_ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])), () => { closed++; closeEntered.TrySetResult(); return ValueTask.CompletedTask; }, "image/png"));
        var image = await setup.Pages.OpenImageAsync(Candidate, 0); using var destination = new SlowWrite(); var copying = image.CopyToAsync(destination, CancellationToken.None);
        try
        {
            await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var stop = setup.Pages.StopAsync();
            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check.Equal(1, closed); Check.True(!stop.IsCompleted && !copying.IsCompleted);
            destination.Release.TrySetResult(); try { await copying; } catch (OperationCanceledException) { }
            await stop; await image.DisposeAsync(); Check.Equal(1, closed);
        }
        finally { destination.Release.TrySetResult(); try { await copying; } catch (OperationCanceledException) { } await image.DisposeAsync(); await setup.Pages.StopAsync(); }
    }

    private static async Task Adapter()
    {
        var paths = new List<string>(); using var client = new SuwayomiClient(new AutoSourcarrSettings(), "http://source.invalid:4567", handler: new Handler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath); var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 8, 7]) };
            response.Content.Headers.ContentType = new("image/png"); response.Content.Headers.ContentEncoding.Add("gzip"); return response;
        }));
        var adapter = new SuwayomiReaderPageSource(client); await using var stream = await adapter.OpenPageAsync(Manga, 2, 7, CancellationToken.None);
        Check.Equal("/api/v1/manga/9007199254740993/chapter/2/page/7", paths.Single()); Check.True(stream.ContentLength is null);
        using var output = new MemoryStream(); await stream.CopyToAsync(output, CancellationToken.None); Check.True(output.ToArray().SequenceEqual(new byte[] { 9, 8, 7 }));
    }

    private static async Task FailedStreamCancellation()
    {
        var closeEntered = Signal(); var released = false; var closed = 0;
        var image = new ReaderImageStream(token =>
        {
            token.Register(() => throw new InvalidOperationException("controlled cancellation callback failure"));
            return Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
        }, () => { closed++; closeEntered.TrySetResult(); return ValueTask.CompletedTask; }, release: () => released = true);
        using var destination = new SlowWrite(); var copying = image.CopyToAsync(destination, CancellationToken.None);
        try
        {
            await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var closing = image.DisposeAsync().AsTask();
            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check.Equal(1, closed); Check.True(!closing.IsCompleted && !released);
            destination.Release.TrySetResult(); await copying; await ThrowsAsync<AggregateException>(async () => await closing); Check.True(released);
        }
        finally { destination.Release.TrySetResult(); try { await copying; } catch (Exception) { } try { await image.DisposeAsync(); } catch (AggregateException) { } }
    }

    private static async Task FailedWaiterCancellation()
    {
        using var setup = new Setup(); setup.ClearPages(); var entered = Signal(); var cleanup = Signal(); var release = Signal();
        setup.Source.Fetch = async token =>
        {
            using var registration = token.Register(() => throw new InvalidOperationException("controlled source cancellation callback failure"));
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return []; }
            finally { cleanup.TrySetResult(); await release.Task; }
        };
        using var cancellation = new CancellationTokenSource(); var request = setup.Pages.ResolvePagesAsync(Chapter, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel(); await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(!request.IsCompleted); release.TrySetResult(); await ThrowsAsync<AggregateException>(async () => await request);
        }
        finally { release.TrySetResult(); try { await request; } catch (Exception) { } }
    }

    private static void Http()
    {
        using var setup = new Setup(http: true); var client = setup.Client!;
        Check.Equal(HttpStatusCode.Unauthorized, client.GetAsync("/api/reader/v1/chapters/51/pages").GetAwaiter().GetResult().StatusCode);
        var response = setup.Request(HttpMethod.Get, "/api/reader/v1/chapters/51/pages", reader: true); Check.Equal(HttpStatusCode.OK, response.StatusCode);
        Check.Equal("http://localhost/api/v1/pages/61/0", Body(response)["pages"]![0]!.GetValue<string>());
        Check.Equal(HttpStatusCode.OK, setup.Request(HttpMethod.Get, "/api/reader/v1/entries/17/chapters", reader: true).StatusCode);
        var image = setup.Request(HttpMethod.Get, "/api/reader/v1/pages/61/0", reader: true); Check.Equal(HttpStatusCode.OK, image.StatusCode); Check.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Check.True(image.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult().SequenceEqual(new byte[] { 1, 2, 3 }));
        Check.True(image.Headers.CacheControl!.Private); Check.Equal(TimeSpan.FromSeconds(3600), image.Headers.CacheControl.MaxAge);
        var head = setup.Request(HttpMethod.Head, "/api/reader/v1/pages/61/0", reader: true); Check.Equal(HttpStatusCode.OK, head.StatusCode); Check.Equal(0, head.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult().Length);
        Check.Equal(HttpStatusCode.MethodNotAllowed, setup.Request(HttpMethod.Post, "/api/reader/v1/entries/41/refresh", reader: true).StatusCode);
        Check.Equal(HttpStatusCode.Unauthorized, setup.Request(HttpMethod.Get, "/api/admin/profiles/41", reader: true).StatusCode);
        Check.Equal(HttpStatusCode.NotFound, setup.Request(HttpMethod.Get, "/api/reader/v1/admin/profiles/41", reader: true).StatusCode);
        Check.Equal(HttpStatusCode.OK, setup.Request(HttpMethod.Put, "/api/admin/profiles/41", admin: true, body: new JsonObject { ["profile"] = new JsonObject { ["sources"] = new JsonArray(Source) }, ["revision"] = 0 }).StatusCode);
        Check.Equal(HttpStatusCode.Conflict, setup.Request(HttpMethod.Put, "/api/admin/profiles/41", admin: true, body: new JsonObject { ["profile"] = new JsonObject(), ["revision"] = 0 }).StatusCode);
        Check.Equal(HttpStatusCode.NotFound, setup.Request(HttpMethod.Get, "/api/v1/chapters/999").StatusCode); Check.Equal(0, setup.Unexpected);
    }

    private static void CatalogueWire()
    {
        using var setup = new Setup(http: true, catalogue: true);
        Check.Equal(HttpStatusCode.OK, setup.Request(HttpMethod.Get, "/api/reader/v1/search?q=Alpha&page=2&sources=" + Source + "&sources=" + VariantSource + "&min_matches=0&match_count=1&sort=title", reader: true).StatusCode);
        var seen = setup.Catalogue.Last!; Check.Equal("SEARCH", seen.Mode); Check.Equal(2, seen.Page); Check.Equal("Alpha", seen.Query); Check.Equal(1, seen.ExactMatchCount); Check.True(seen.SourceIds!.SequenceEqual(new[] { Source, VariantSource }));
        setup.Catalogue.Pending = true; var pending = setup.Request(HttpMethod.Get, "/api/reader/v1/browse/latest", reader: true);
        Check.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode); Check.Equal("preparing", pending.Headers.GetValues("X-AutoSourcarr-Reader-State").Single()); Check.Equal("3", pending.Headers.RetryAfter!.ToString());
        Check.Equal(HttpStatusCode.UnprocessableEntity, setup.Request(HttpMethod.Get, "/api/v1/search?page=0").StatusCode);
        Check.Equal(HttpStatusCode.OK, setup.Request(HttpMethod.Get, "/api/reader/v1/entries/17/refresh?fetch_details=false&fetch_chapters=true", reader: true).StatusCode); Check.Equal((Alias, false, true), setup.Catalogue.Refresh);
        Check.Equal(0, setup.Unexpected);
    }

    private static async Task LateCopy()
    {
        using var setup = new Setup(); var reads = 0; var closes = 0;
        setup.Source.Open = _ => Task.FromResult(new ReaderImageStream(_ => { reads++; return Task.FromResult<Stream>(new MemoryStream([1, 2, 3])); }, () => { closes++; return ValueTask.CompletedTask; }));
        await using (var image = await setup.Pages.OpenImageAsync(Candidate, 0))
        {
            setup.Detach(); using var output = new MemoryStream();
            await ThrowsAsync<KeyNotFoundException>(() => image.CopyToAsync(output, CancellationToken.None)); Check.Equal(0L, output.Length); Check.Equal(0, reads);
        }
        Check.Equal(1, closes);
    }

    private static void LatePublication()
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using var setup = new Setup(http: true); var reads = 0; var closes = 0;
            setup.Source.Open = _ => Task.FromResult(new ReaderImageStream(_ => { reads++; return Task.FromResult<Stream>(new MemoryStream([1, 2, 3])); }, () => { closes++; return ValueTask.CompletedTask; }, "image/png", 3));
            setup.BeforeResult = _ => { setup.Detach(); return Task.CompletedTask; };
            var response = setup.Request(method, "/api/reader/v1/pages/61/0", reader: true);
            Check.Equal(HttpStatusCode.NotFound, response.StatusCode); Check.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
            Check.Equal(0, reads); Check.Equal(1, closes); Check.Equal(0, setup.Unexpected);
            if (method == HttpMethod.Get) Check.True(Body(response)["detail"]!.GetValue<string>().Contains("Page not found", StringComparison.Ordinal));
            else Check.Equal(0, response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult().Length);
        }
    }

    private static void ShutdownPublication()
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using var setup = new Setup(http: true); var reads = 0; var closes = 0;
            setup.Source.Open = _ => Task.FromResult(new ReaderImageStream(_ => { reads++; return Task.FromResult<Stream>(new MemoryStream([1, 2, 3])); }, () => { closes++; return ValueTask.CompletedTask; }, "image/png", 3));
            setup.BeforeResult = _ => setup.Pages.StopAsync();
            var response = setup.Request(method, "/api/reader/v1/pages/61/0", reader: true);
            Check.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Check.Equal(0, reads); Check.Equal(1, closes); Check.Equal(0, setup.Unexpected);
            if (method == HttpMethod.Get) Check.True(Body(response)["deferred"]!.GetValue<bool>());
            else Check.Equal(0, response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult().Length);
        }
    }

    private static async Task ImageCopyFailure()
    {
        foreach (var recordingFails in new[] { false, true })
        {
            var primary = new IOException("private synthetic copy failure");
            await using var fixture = new ImageFailureFixture(writeFailure: primary);
            var recording = new IOException("private synthetic recording failure");
            if (recordingFails) fixture.RecordingFailure = recording;
            fixture.Start(); await fixture.WaitForCloseAsync(); fixture.ReleaseClose();
            var propagated = await ObserveFailure(fixture.Execution!);
            fixture.AssertJoined(primary); Check.Equal(1, fixture.Records);
            if (recordingFails)
            {
                Check.True(propagated is not null && ExceptionTree(propagated).Any(error => ReferenceEquals(error, recording)));
                Check.True(ExceptionTree(propagated!).Any(error => ReferenceEquals(error, primary)));
                Check.True(ExceptionTree(propagated!).Any(error => ReferenceEquals(error, fixture.CleanupFailure)));
            }
            else
            {
                Check.True(propagated is null); Check.Equal(500, fixture.Context.Response.StatusCode);
                fixture.AssertSafeErrorHeaders(); Check.Equal("unexpected", fixture.ErrorBody()["error_id"]!.GetValue<string>());
                Check.True(!fixture.Output.Text.Contains("private synthetic", StringComparison.Ordinal));
            }
            await fixture.AssertRepeatedDisposeAsync();
        }
    }
    private static async Task ImageMissingFailure()
    {
        var primary = new KeyNotFoundException("private detached source identity");
        await using var fixture = new ImageFailureFixture(validationFailure: primary);
        fixture.Start(); await fixture.WaitForCloseAsync(); fixture.ReleaseClose(); await fixture.Execution!;
        fixture.AssertJoined(primary); Check.Equal(0, fixture.Opens); Check.Equal(1, fixture.Records);
        Check.Equal(404, fixture.Context.Response.StatusCode); fixture.AssertSafeErrorHeaders();
        Check.Equal("Page not found; reopen the chapter to refresh page URLs", fixture.ErrorBody()["detail"]!.GetValue<string>());
        Check.True(ExceptionTree(fixture.Recorded!).Any(error => error is ApiProblem { StatusCode: 404 }));
        await fixture.AssertRepeatedDisposeAsync();
    }
    private static async Task ImageAbortFailure()
    {
        foreach (var abortDuringClose in new[] { false, true })
        {
            var primary = new OperationCanceledException("private request cancellation");
            await using var fixture = new ImageFailureFixture(validationFailure: primary);
            if (!abortDuringClose) fixture.Abort.Cancel();
            fixture.Start(); await fixture.WaitForCloseAsync();
            if (abortDuringClose) fixture.Abort.Cancel();
            fixture.ReleaseClose(); var propagated = await ObserveFailure(fixture.Execution!);
            Check.True(propagated is not null); fixture.AssertJoined(primary); Check.Equal(1, fixture.Records);
            Check.True(ExceptionTree(propagated!).Any(error => ReferenceEquals(error, primary)));
            Check.True(!ExceptionTree(propagated!).Any(error => error is SourceEngineUnavailableException));
            Check.Equal(200, fixture.Context.Response.StatusCode); Check.Equal(0, fixture.Output.Writes); Check.Equal(0, fixture.Opens);
            await fixture.AssertRepeatedDisposeAsync();
        }
        var ordinaryAbort = new OperationCanceledException("ordinary request cancellation");
        await using var ordinary = new ImageFailureFixture(validationFailure: ordinaryAbort) { FailClose = false };
        ordinary.Abort.Cancel(); ordinary.Start(); await ordinary.WaitForCloseAsync(); ordinary.ReleaseClose();
        Check.True(ReferenceEquals(ordinaryAbort, await ObserveFailure(ordinary.Execution!)));
        Check.Equal(0, ordinary.Records); Check.Equal(1, ordinary.Closes); Check.Equal(1, ordinary.Releases); Check.Equal(0, ordinary.Output.Writes);
    }
    private static async Task ImageRetirementFailure()
    {
        var primary = new OperationCanceledException("private local transport retirement");
        await using var fixture = new ImageFailureFixture(validationFailure: primary);
        fixture.Start(); await fixture.WaitForCloseAsync(); fixture.ReleaseClose(); await fixture.Execution!;
        fixture.AssertJoined(primary); Check.Equal(503, fixture.Context.Response.StatusCode); Check.Equal(1, fixture.Records);
        Check.Equal("5", fixture.Context.Response.Headers.RetryAfter.ToString()); fixture.AssertSafeErrorHeaders();
        Check.True(fixture.ErrorBody()["deferred"]!.GetValue<bool>()); Check.Equal(0, fixture.Opens);
        await fixture.AssertRepeatedDisposeAsync();
    }
    private static async Task ImageStartedFailure()
    {
        var primary = new IOException("private started response copy failure");
        await using var fixture = new ImageFailureFixture(writeFailure: primary, startOnWrite: true);
        fixture.Start(); await fixture.WaitForCloseAsync(); fixture.ReleaseClose();
        var propagated = await ObserveFailure(fixture.Execution!);
        Check.True(propagated is not null); fixture.AssertJoined(primary); Check.Equal(1, fixture.Records);
        Check.True(ExceptionTree(propagated!).Any(error => ReferenceEquals(error, primary)));
        Check.True(ExceptionTree(propagated!).Any(error => ReferenceEquals(error, fixture.CleanupFailure)));
        Check.True(fixture.Context.Response.HasStarted); Check.Equal(200, fixture.Context.Response.StatusCode);
        Check.Equal("image/png", fixture.Context.Response.ContentType); Check.Equal(3L, fixture.Context.Response.ContentLength);
        Check.Equal("private, max-age=3600", fixture.Context.Response.Headers.CacheControl.ToString()); Check.Equal(1, fixture.Output.Writes);
        await fixture.AssertRepeatedDisposeAsync();
    }
    private static async Task ImageHeadFailure()
    {
        await using var fixture = new ImageFailureFixture(method: "HEAD");
        fixture.Start(); await fixture.WaitForCloseAsync(); fixture.ReleaseClose(); await fixture.Execution!;
        fixture.AssertJoined(); Check.Equal(500, fixture.Context.Response.StatusCode); fixture.AssertSafeErrorHeaders();
        Check.Equal(0, fixture.Opens); Check.Equal(0, fixture.Output.Writes); Check.Equal(0, fixture.Output.Text.Length);
        Check.True(fixture.Context.Response.ContentLength > 3); Check.Equal(1, fixture.Records);
        Check.Equal("nosniff", fixture.Context.Response.Headers.XContentTypeOptions.ToString());
        await fixture.AssertRepeatedDisposeAsync();
    }
    private sealed class ImageFailureFixture : IAsyncDisposable
    {
        private readonly TaskCompletionSource _entered = Signal(), _release = Signal();
        private readonly MemoryStream _body = new([1, 2, 3], writable: false);
        private readonly ReaderImageStream _image;
        public DefaultHttpContext Context { get; } = new();
        public CancellationTokenSource Abort { get; } = new();
        public IOException CleanupFailure { get; } = new("private synthetic physical close failure");
        public Exception? RecordingFailure, Recorded;
        public bool FailClose = true;
        public int Closes, Releases, Opens, Records;
        public Task? Execution;
        public ImageResponseBody Output { get; }
        public ImageFailureFixture(Exception? validationFailure = null, Exception? writeFailure = null, string method = "GET", bool startOnWrite = false)
        {
            var feature = new ImageResponseFeature(); Context.Features.Set<IHttpResponseFeature>(feature);
            Output = new(writeFailure, startOnWrite ? feature.MarkStarted : null); Context.Response.Body = Output;
            Context.Request.Method = method; Context.Request.Path = "/api/reader/v1/pages/61/0"; Context.RequestAborted = Abort.Token;
            Context.Response.Headers["X-Retained-Security"] = "retained";
            _image = new(_ => { Opens++; return Task.FromResult<Stream>(_body); }, async () =>
            {
                _entered.TrySetResult(); await _release.Task.ConfigureAwait(false); await _body.DisposeAsync().ConfigureAwait(false);
                Interlocked.Increment(ref Closes); if (FailClose) throw CleanupFailure;
            }, "image/png", 3, release: () => Interlocked.Increment(ref Releases),
                validatePublication: validationFailure is null ? null : _ => Task.FromException(validationFailure));
        }
        public void Start()
        {
            var type = typeof(ReaderEndpoints).GetNestedType("ImageResult", System.Reflection.BindingFlags.NonPublic)!;
            var constructor = type.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Single();
            var result = (IResult)constructor.Invoke([_image]);
            Execution = new ApiExceptionMiddleware(result.ExecuteAsync, (_, error) =>
            { Records++; Recorded = error; if (RecordingFailure is { } failure) throw failure; return "unexpected"; }).InvokeAsync(Context);
        }
        public async Task WaitForCloseAsync()
        {
            await _entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(!Execution!.IsCompleted);
            Check.Equal(0, Closes); Check.Equal(0, Releases);
        }
        public void ReleaseClose() => _release.TrySetResult();
        public void AssertJoined(Exception? primary = null)
        {
            Check.Equal(1, Closes); Check.Equal(1, Releases); Check.True(Recorded is not null);
            Check.True(ExceptionTree(Recorded!).Any(error => ReferenceEquals(error, CleanupFailure)));
            if (primary is not null) Check.True(ExceptionTree(Recorded!).Any(error => ReferenceEquals(error, primary)));
        }
        public void AssertSafeErrorHeaders()
        {
            Check.Equal("application/json; charset=utf-8", Context.Response.ContentType); Check.Equal("no-store", Context.Response.Headers.CacheControl.ToString());
            Check.Equal("retained", Context.Response.Headers["X-Retained-Security"].ToString()); Check.True(Context.Response.ContentLength != 3);
        }
        public JsonObject ErrorBody() => JsonNode.Parse(Output.Text)!.AsObject();
        public async Task AssertRepeatedDisposeAsync()
        { var error = await ObserveFailure(_image.DisposeAsync().AsTask()); Check.True(error is not null); Check.Equal(1, Closes); Check.Equal(1, Releases); }
        public async ValueTask DisposeAsync()
        {
            _release.TrySetResult(); if (Execution is not null) await ObserveFailure(Execution);
            await ObserveFailure(_image.DisposeAsync().AsTask()); await _body.DisposeAsync(); Abort.Dispose(); Output.Dispose();
        }
    }
    private sealed class ImageResponseFeature : IHttpResponseFeature
    {
        private int _status = 200; private readonly HeaderDictionary _headers = new();
        public int StatusCode { get => _status; set { if (HasStarted) throw new InvalidOperationException("Response already started"); _status = value; } }
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get => _headers; set => throw new NotSupportedException(); }
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; private set; }
        public void MarkStarted() { HasStarted = true; _headers.IsReadOnly = true; }
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
    private sealed class ImageResponseBody(Exception? firstFailure, Action? onFirstWrite) : Stream
    {
        private readonly MemoryStream _output = new(); public int Writes; public string Text => Encoding.UTF8.GetString(_output.ToArray());
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => _output.Length; public override long Position { get => _output.Position; set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++Writes == 1) { onFirstWrite?.Invoke(); if (firstFailure is not null) return ValueTask.FromException(firstFailure); }
            _output.Write(buffer.Span); return ValueTask.CompletedTask;
        }
        protected override void Dispose(bool disposing) { if (disposing) _output.Dispose(); base.Dispose(disposing); }
    }
    private static async Task<Exception?> ObserveFailure(Task work) { try { await work.ConfigureAwait(false); return null; } catch (Exception error) { return error; } }
    private static IEnumerable<Exception> ExceptionTree(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate) foreach (var child in aggregate.InnerExceptions) foreach (var item in ExceptionTree(child)) yield return item;
        else if (error.InnerException is { } inner) foreach (var item in ExceptionTree(inner)) yield return item;
    }

    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _temp = new(); private readonly SemaphoreSlim _admin = new(1, 1); private readonly WholeTaskCoordinator _lanes;
        private readonly IHost? _host;
        public string Directory => _temp.Path;
        public object Gate { get; } = new(); public SqliteDatabase Database { get; } public ReaderRepository Repository { get; } public ReaderPageService Pages { get; }
        public BoundedWorkScheduler Io { get; } = new(2, 16); public PageSource Source { get; } = new(); public Catalogue Catalogue { get; } = new(); public HttpClient? Client { get; } public int Unexpected;
        public Func<HttpContext, Task>? BeforeResult;
        public Setup(bool http = false, bool catalogue = false)
        {
            var settings = new AutoSourcarrSettings { DataDir = _temp.Path, AdminToken = "admin", AutosourcarrReaderApiToken = "reader" };
            Database = new(settings); new SchemaMigrator(Database).Initialize(); Repository = new(Database, Gate);
            Database.Write(DatabaseFile.Catalog, session => session.Execute("""
                INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(41,'Gallery work','now','now'),(42,'Other edition','now','now');
                INSERT INTO entry_aliases VALUES(17,41,'merge','now');
                INSERT INTO source_config(source_id,name,lang,priority,last_seen) VALUES($source,'Gallery A','en',10,'now'),($variant,'Gallery B','ja',1000,'now');
                INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,last_checked) VALUES(41,$source,$manga,'Gallery A','now');
                """, ("$source", ReaderServingTests.Source), ("$variant", VariantSource), ("$manga", Manga)));
            Database.Write(DatabaseFile.Origins, session => session.Execute("""
                INSERT INTO virtual_chapters(id,entry_id,chapter_key,chapter_number,display_name,created_at,updated_at) VALUES(51,41,'g:member:1',1,'Stored gallery','now','now');
                INSERT INTO chapter_candidates(id,virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,source_chapter_index,name,chapter_number,page_count,first_seen,last_seen)
                VALUES(61,51,$source,'Gallery A',$manga,$chapter,0,'Chapter 1: Gallery A',1,20,'now','now');
                """, ("$source", ReaderServingTests.Source), ("$manga", Manga), ("$chapter", SourceChapter)));
            SeedCached(Candidate, "https://images.invalid/page.png"); Pages = new(Repository, Source, Io, TimeSpan.FromHours(1));
            var tasks = new TaskRegistry(); _lanes = new(settings, tasks);
            if (!http) return;
            _host = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer().ConfigureServices(services => services.AddRouting()).Configure(app =>
            {
                app.UseAutoSourcarrHttp("0.4.3-migration", () => settings, (_, _) => { Unexpected++; return "unexpected"; }, new RequestAdmissionGate(), _lanes, tasks, new SupportPrivacy(), _ => { });
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapReaderServing(Repository, Pages, Io).AddEndpointFilter(async (context, next) =>
                    {
                        var result = await next(context);
                        if (BeforeResult is { } callback) await callback(context.HttpContext);
                        return result;
                    });
                    endpoints.MapServingProfiles(new(Database, Gate), Io, () => settings, _admin);
                    if (catalogue) endpoints.MapReaderCatalogue(Catalogue);
                });
            })).Build();
            _host.StartAsync().GetAwaiter().GetResult(); Client = _host.GetTestClient();
        }
        public void AddVariant()
        {
            Database.Write(DatabaseFile.Catalog, session => session.Execute("INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,last_checked) VALUES(41,$source,200,'Gallery B','now')", ("$source", VariantSource)));
            Database.Write(DatabaseFile.Origins, session => session.Execute("""
                INSERT INTO chapter_candidates(id,virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,source_chapter_index,name,chapter_number,page_count,first_seen,last_seen)
                VALUES(62,51,$source,'Gallery B',200,300,0,'Chapter 1: Gallery B',1,20,'now','now')
                """, ("$source", VariantSource)));
        }
        public void SeedCached(long candidate, string url) => Database.Write(DatabaseFile.Origins, session => session.Execute("INSERT INTO candidate_pages(candidate_id,page_index,private_url,updated_at) VALUES($id,0,$url,$time)", ("$id", candidate), ("$url", url), ("$time", DateTimeOffset.UtcNow.ToString("O"))));
        public void ClearPages() => Database.Write(DatabaseFile.Origins, session => session.Execute("DELETE FROM candidate_pages"));
        public void Expire() => Database.Write(DatabaseFile.Origins, session => session.Execute("UPDATE candidate_pages SET updated_at='2000-01-01T00:00:00Z'"));
        public void Detach() { lock (Gate) Database.Write(DatabaseFile.Catalog, session => session.Execute("UPDATE source_matches SET active=0 WHERE source_id=$source", ("$source", ReaderServingTests.Source))); }
        public HttpResponseMessage Request(HttpMethod method, string path, bool reader = false, bool admin = false, JsonObject? body = null)
        {
            using var request = new HttpRequestMessage(method, path); if (reader) request.Headers.Add("X-AutoSourcarr-API-Key", "reader"); if (admin) request.Headers.Add("X-Admin-Token", "admin");
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"); return Client!.SendAsync(request).GetAwaiter().GetResult();
        }
        public void Dispose()
        {
            Client?.Dispose(); _host?.Dispose(); Pages.DisposeAsync().AsTask().GetAwaiter().GetResult(); _lanes.DisposeAsync().AsTask().GetAwaiter().GetResult(); Io.DisposeAsync().AsTask().GetAwaiter().GetResult(); Database.Dispose(); _admin.Dispose(); _temp.Dispose();
        }
    }

    private sealed class PageSource : IReaderPageSource
    {
        public int Fetches, Opens; public Func<CancellationToken, Task<IReadOnlyList<string>>> Fetch = _ => Task.FromResult<IReadOnlyList<string>>(["https://images.invalid/page.png"]);
        public Func<long, Exception?> Attention = _ => null; public Func<CancellationToken, Task<int>> Index = _ => Task.FromResult(0);
        public Func<CancellationToken, Task<ReaderImageStream>> Open = _ => Task.FromResult(new ReaderImageStream(_ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])), () => ValueTask.CompletedTask, "image/png", 3));
        public Task CheckAttentionAsync(long chapterId, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); if (Attention(chapterId) is { } error) throw error; return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> FetchPagesAsync(long chapterId, CancellationToken cancellationToken) { Interlocked.Increment(ref Fetches); return Fetch(cancellationToken); }
        public Task<int> ChapterIndexAsync(long mangaId, long chapterId, CancellationToken cancellationToken) => Index(cancellationToken);
        public Task<ReaderImageStream> OpenPageAsync(long mangaId, int chapterIndex, int pageIndex, CancellationToken cancellationToken) { Interlocked.Increment(ref Opens); return Open(cancellationToken); }
        public Task<ReaderImageStream> OpenBinaryAsync(string privateUrl, CancellationToken cancellationToken) { Interlocked.Increment(ref Opens); return Open(cancellationToken); }
    }
    private sealed class Catalogue : IReaderCatalogue
    {
        public ReaderBrowseRequest? Last; public bool Pending; public (long, bool, bool) Refresh;
        public Task<JsonObject> SourcesAsync(CancellationToken cancellationToken) => Task.FromResult(new JsonObject { ["items"] = new JsonArray() });
        public Task<JsonObject> BrowseAsync(ReaderBrowseRequest request, CancellationToken cancellationToken) { Last = request; if (Pending) throw new ReaderBrowsePendingException(); return Task.FromResult(new JsonObject { ["items"] = new JsonArray(new JsonObject { ["id"] = Entry, ["preferred_title"] = "Gallery" }), ["has_next_page"] = true }); }
        public Task<JsonObject> EntryAsync(long entryId, CancellationToken cancellationToken) => Task.FromResult(new JsonObject { ["id"] = entryId, ["preferred_title"] = "Gallery" });
        public Task<JsonObject> RefreshAsync(long entryId, bool fetchDetails, bool fetchChapters, CancellationToken cancellationToken) { Refresh = (entryId, fetchDetails, fetchChapters); return Task.FromResult(new JsonObject { ["entry"] = new JsonObject { ["id"] = entryId, ["preferred_title"] = "Gallery" }, ["chapters"] = new JsonArray() }); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private sealed class SlowWrite : Stream
    {
        private readonly MemoryStream _content = new();
        public TaskCompletionSource Entered { get; } = Signal(); public TaskCompletionSource Release { get; } = Signal();
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => _content.Length; public override long Position { get => _content.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _content.Flush(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _content.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Entered.TrySetResult(); await Release.Task; _content.Write(buffer.Span); }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { Entered.TrySetResult(); await Release.Task; _content.Write(buffer, offset, count); }
        protected override void Dispose(bool disposing) { if (disposing) _content.Dispose(); base.Dispose(disposing); }
    }
    private static HashSet<string> Set(JsonNode value) => value.AsArray().Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
    private static JsonObject CandidateRow(Setup setup) => setup.Database.Read(DatabaseFile.Origins, session => JsonSerializer.SerializeToNode(session.Query("SELECT * FROM chapter_candidates WHERE id=61")[0].ToDictionary(pair => pair.Key, pair => pair.Value))!.AsObject());
    private static JsonObject Body(HttpResponseMessage response) => JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
    private static void EqualJson(JsonNode? expected, JsonNode? actual) => Check.JsonEqual(JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(actual));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected exception " + typeof(T).Name); }
}
