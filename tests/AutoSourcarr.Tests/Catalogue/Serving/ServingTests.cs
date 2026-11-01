using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Accounts;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Chapters;
using AutoSourcarr.Core.Catalogue.Serving;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Mixed;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Tests.Catalogue.Chapters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests.Catalogue.Serving;

/// <summary>CHAP-5 quality samples, CHAP-6 opt-in upgrades (minimum gain, cutoff), page images for colour checks, and route access.</summary>
public static class ServingTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("serving: quality score from sample pages; a failed sample keeps the old score", () => Quality().GetAwaiter().GetResult());
        suite.Add("serving: upgrades need the cutoff and the minimum gain, then apply with the caller", Upgrades);
        suite.Add("serving: page images sample interior pages of the newest chapter at most 256 px", () => PageImages().GetAwaiter().GetResult());
        suite.Add("serving: chapter and serving routes are admin-only; chapter details stay a guest read", () => Routes().GetAwaiter().GetResult());
    }

    private static async Task Quality()
    {
        Check.Equal("0|3|6|9", string.Join('|', CandidateQuality.SampleIndexes(10, 4)));
        Check.Equal(100.0, CandidateQuality.Score(2000, 3000, 3_000_000, 0));
        Check.Equal(5.0 + 1.5 * 15, Math.Round(CandidateQuality.Score(1000, 1500, 0, 1), 6));

        using var world = new ChapterWorld();
        world.Write(ChapterWorld.SourceA, ChapterWorld.MangaA, ChapterWorld.Chapter(1, 1, "Chapter 1", 8));
        var candidate = world.Scalar(DatabaseFile.Origins, "SELECT id FROM chapter_candidates");
        var pages = new FakePages(8) { Size = (1200, 1800) };
        var quality = new CandidateQuality(world.Database, world.Gate, pages, new FakeDecoder(), () => new QualityOptions(2, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), world.Log);
        var first = await quality.AnalyzeAsync(candidate, 5, DecisionActor.User("alice"));
        Check.True(first["complete"]!.GetValue<bool>());
        var score = first["quality_score"]!.GetValue<double>();
        Check.True(score > 30, "scored " + score.ToString(CultureInfo.InvariantCulture));
        Check.Equal(1200L, (long)first["median_width"]!.GetValue<int>());
        Check.Equal("complete", world.Database.Read(DatabaseFile.Catalog, s => s.Query("SELECT outcome FROM quality_attempts WHERE candidate_id=$id", ("$id", candidate)).Single().Text("outcome")));

        // One sampled page cannot be fetched: the run is incomplete and the old score and sample stay.
        pages.Size = (400, 600); pages.Failing.Add(7);
        var second = await quality.AnalyzeAsync(candidate, 5, DecisionActor.User("alice"));
        Check.True(!second["complete"]!.GetValue<bool>());
        Check.Equal(Math.Round(score, 2), second["quality_score"]!.GetValue<double>());
        Check.Equal(Math.Round(score, 6), Math.Round(world.Database.Read(DatabaseFile.Origins, s => Convert.ToDouble(s.Scalar("SELECT quality_score FROM chapter_candidates WHERE id=$id", ("$id", candidate)), CultureInfo.InvariantCulture)), 6));
        Check.Equal(1200L, world.Scalar(DatabaseFile.Origins, "SELECT median_width FROM quality_samples WHERE candidate_id=$id", ("$id", candidate)));
        Check.Equal("incomplete", world.Database.Read(DatabaseFile.Catalog, s => s.Query("SELECT outcome FROM quality_attempts WHERE candidate_id=$id", ("$id", candidate)).Single().Text("outcome")));

        // A broken image counts against integrity but is an observation, not a failure.
        pages.Failing.Clear(); pages.Broken.Add(0);
        var third = await quality.AnalyzeAsync(candidate, 5, DecisionActor.User("alice"));
        Check.True(third["complete"]!.GetValue<bool>()); Check.Equal(1, third["broken_pages"]!.GetValue<int>());
        Check.Equal("chapter.quality|chapter.quality", string.Join('|', world.Log.Kinds));
        await ThrowsAsync<KeyNotFoundException>(() => quality.AnalyzeAsync(424242, 5, DecisionActor.User("alice")));
    }

    private static void Upgrades()
    {
        var rules = ServingUpgrades.DefaultPolicy;
        Check.Equal(0, ServingUpgrades.Thresholds(rules, 70, 95).Count);
        Check.True(ServingUpgrades.Thresholds(rules, 92, 99).Contains("Current candidate already meets the cutoff"));
        Check.True(ServingUpgrades.Thresholds(rules, 70, 85).Contains("Target is below the cutoff"));
        Check.True(ServingUpgrades.Thresholds(rules, 85, 91).Contains("Improvement is below the configured minimum"));
        Check.Throws<ServingUpgradeException>(() => ServingUpgrades.Validate(new JsonObject { ["enabled"] = true }));
        Check.Throws<ServingUpgradeException>(() => ServingUpgrades.Validate(Policy(true, cutoff: 0)));

        using var world = new ChapterWorld();
        world.Write(ChapterWorld.SourceA, ChapterWorld.MangaA, ChapterWorld.Chapter(1, 1, "Chapter 1", 10));
        world.Write(ChapterWorld.SourceB, ChapterWorld.MangaB, ChapterWorld.Chapter(11, 1, "Chapter 1", 10));
        var chapter = world.Scalar(DatabaseFile.Origins, "SELECT id FROM virtual_chapters");
        var current = world.Scalar(DatabaseFile.Origins, "SELECT id FROM chapter_candidates WHERE source_id=$s", ("$s", ChapterWorld.SourceA));
        var target = world.Scalar(DatabaseFile.Origins, "SELECT id FROM chapter_candidates WHERE source_id=$s", ("$s", ChapterWorld.SourceB));
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
        void Sample(long id, double score) => world.Database.Write(DatabaseFile.Origins, s =>
        {
            s.Execute("UPDATE chapter_candidates SET quality_score=$q WHERE id=$id", ("$q", score), ("$id", id));
            return s.Execute("INSERT OR REPLACE INTO quality_samples(candidate_id,sampled_pages,broken_pages,median_width,median_height,avg_bytes,analyzed_at) VALUES($id,5,0,800,1200,100000,$t)", ("$id", id), ("$t", now));
        });
        Sample(current, 70); Sample(target, 85);
        // The reader already served the first source for this chapter: that is the baseline an upgrade replaces.
        world.Database.Write(DatabaseFile.Origins, s => s.Execute("INSERT INTO served_origin(virtual_chapter_id,candidate_id,selected_at,reason) VALUES($c,$id,$t,'initial')",
            ("$c", chapter), ("$id", current), ("$t", now)));
        var upgrades = new ServingUpgrades(world.Database, world.Gate, world.Reader, world.Log);
        var preview = upgrades.Preview(ChapterWorld.Entry, chapter, target);
        Check.True(!preview["eligible"]!.GetValue<bool>());
        var blockers = preview["blockers"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        Check.True(blockers.Contains("Reviewed upgrades are disabled for this work") && blockers.Contains("Target is below the cutoff"));

        var saved = upgrades.Save(ChapterWorld.Entry, Policy(true, cutoff: 80, minimum: 10), 0, DecisionActor.User("alice"));
        Check.Equal(1L, saved["revision"]!.GetValue<long>());
        Check.Throws<ServingUpgradeException>(() => upgrades.Save(ChapterWorld.Entry, Policy(true), 0, DecisionActor.User("alice")));
        preview = upgrades.Preview(ChapterWorld.Entry, chapter, target);
        Check.True(preview["eligible"]!.GetValue<bool>(), string.Join("; ", preview["blockers"]!.AsArray().Select(item => item!.GetValue<string>())));
        Check.Throws<ServingUpgradeException>(() => upgrades.Apply(ChapterWorld.Entry, chapter, target, "stale", DecisionActor.User("alice")));
        upgrades.Apply(ChapterWorld.Entry, chapter, target, preview["revision"]!.GetValue<string>(), DecisionActor.User("alice"));
        var detail = world.Reader.ChapterDetails(chapter);
        Check.Equal(target, detail["would_serve_candidate_id"]!.GetValue<long>());
        Check.Equal("serving.upgrade.policy|serving.upgrade", string.Join('|', world.Log.Kinds));
        // The cooldown now blocks a further change.
        Check.True(upgrades.Preview(ChapterWorld.Entry, chapter, current)["blockers"]!.AsArray().Any(item => item!.GetValue<string>() == "Serving choice cooldown is active"));
    }

    private static async Task ThrowsAsync<T>(Func<Task> run) where T : Exception
    {
        try { await run(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected exception " + typeof(T).Name);
    }

    private static JsonObject Policy(bool enabled, double cutoff = 90, double minimum = 10) => new()
    {
        ["enabled"] = enabled, ["cutoff"] = cutoff, ["minimum_improvement"] = minimum, ["cooldown_hours"] = 24, ["sample_max_age_hours"] = 168,
    };

    private static async Task PageImages()
    {
        using var world = new ChapterWorld();
        world.Write(ChapterWorld.SourceA, ChapterWorld.MangaA, ChapterWorld.Chapter(1, 1, "Chapter 1", 20), ChapterWorld.Chapter(2, 2, "Chapter 2", 30), ChapterWorld.Chapter(3, 3, "Chapter 3"));
        var newestWithPages = world.Scalar(DatabaseFile.Origins, "SELECT id FROM chapter_candidates WHERE source_chapter_id=2");
        var pages = new FakePages(30) { Size = (2000, 3000) };
        IPageImageSource source = new ReaderPageImages(world.Database, pages, new FakeDecoder());
        var rasters = await source.SamplePagesAsync(ChapterWorld.SourceA, ChapterWorld.MangaA, 4, default);
        Check.Equal(4, rasters.Count);
        Check.Equal(PageColorAnalysis.SampleIndexes(30, 4).Count, pages.Read.Count);
        Check.True(pages.Read.All(read => read.Candidate == newestWithPages), "the newest chapter with known pages");
        Check.True(pages.Read.All(read => read.Page is > 0 and < 29), "interior pages only");
        Check.True(rasters.All(raster => Math.Max(raster.SampleWidth, raster.SampleHeight) <= ReaderPageImages.MaximumSide && raster.Rgb.Length == raster.SampleWidth * raster.SampleHeight * 3));
        Check.Equal(2000, rasters[0].Width);
        // Unknown records have no pages; undecodable pages are skipped and all-broken raises the error.
        Check.Equal(0, (await source.SamplePagesAsync("nope", 1, 4, default)).Count);
        pages.Broken.UnionWith(Enumerable.Range(0, 30));
        await ThrowsAsync<InvalidDataException>(() => source.SamplePagesAsync(ChapterWorld.SourceA, ChapterWorld.MangaA, 4, default));
    }

    private static async Task Routes()
    {
        string[] adminRoutes =
        [
            "POST /api/v1/candidates/5/quality", "POST /api/v1/candidates/5/reject", "POST /api/v1/candidates/5/restore", "GET /api/v1/matrix",
            "POST /api/v1/matrix/chapter-stats", "POST /api/v1/entries/1/source-records/1001/501/check-chapters", "POST /api/v1/entries/1/refresh-all",
            "POST /api/v1/entries/1/refresh", "GET /api/v1/gallery-policies/1001", "PUT /api/v1/gallery-policies/1001", "POST /api/v1/gallery-policies/1001/apply",
            "GET /api/v1/serving-upgrades/1", "PUT /api/v1/serving-upgrades/1", "POST /api/v1/serving-upgrades/1/preview", "POST /api/v1/serving-upgrades/1/apply",
            "GET /api/v1/profiles/1", "PUT /api/v1/profiles/1",
        ];
        foreach (var route in adminRoutes)
        {
            var parts = route.Split(' ');
            Check.True(AccessPolicy.Required(parts[0], parts[1]) == AccessLevel.Admin, "admin-only: " + route);
        }
        Check.True(AccessPolicy.Required("GET", "/api/v1/chapters/5") == AccessLevel.Guest, "chapter details are a catalogue read");
        Check.True(AccessPolicy.Required("GET", "/api/v1/entries/1/chapters") == AccessLevel.Guest);

        using var world = new ChapterWorld();
        world.Write(ChapterWorld.SourceA, ChapterWorld.MangaA, ChapterWorld.Chapter(1, 1, "Chapter 1", 10));
        using var temp = new Sandbox();
        var settings = new AutoSourcarrSettings { AdminToken = "admin" };
        var services = new ApiV1Services(() => settings, temp.Path, null);
        var chapters = new ChaptersV1Services
        {
            Database = world.Database, Queries = new CatalogueQueries(world.Database, world.Gate, _ => false),
            Review = new ChapterCandidateReview(world.Database, world.Gate, world.Reader.ChapterDetails, world.Reader.ReconcileVisibility, world.Log),
        };
        var serving = new ServingV1Services { Upgrades = new ServingUpgrades(world.Database, world.Gate, world.Reader, world.Log), Profiles = new(world.Database, world.Gate), Decisions = world.Log };
        using var host = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer().ConfigureServices(collection => collection.AddRouting()).Configure(app =>
        {
            app.Use((Func<HttpContext, RequestDelegate, Task>)((context, next) =>
            {
                if (context.Request.Headers.ContainsKey("X-Test-Guest")) context.Items[AccessControlMiddleware.CallerItem] = new Caller(Role.Guest, "guest", "session");
                return next(context);
            }));
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                var group = endpoints.MapGroup("/api/v1").AddEndpointFilter(new AdminAuthorizationFilter(() => settings));
                ChaptersV1Endpoints.Map(group, services, ApiJsonContext.Default, chapters);
                ServingV1Endpoints.Map(group, services, ApiJsonContext.Default, serving);
            });
        })).Build();
        await host.StartAsync();
        try
        {
            using var anonymous = host.GetTestClient();
            using (var refused = await anonymous.GetAsync("/api/v1/matrix")) Check.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            using var guest = host.GetTestClient(); guest.DefaultRequestHeaders.Add("X-Test-Guest", "1");
            using (var refused = await guest.PostAsync("/api/v1/candidates/1/reject", new StringContent("{}", Encoding.UTF8, "application/json"))) Check.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            using (var refused = await guest.GetAsync("/api/v1/serving-upgrades/1")) Check.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            using var admin = host.GetTestClient(); admin.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
            using (var matrix = await admin.GetAsync("/api/v1/matrix?search=Solo&limit=10"))
            {
                var body = JsonNode.Parse(await matrix.Content.ReadAsStringAsync())!.AsObject();
                Check.True(matrix.StatusCode == HttpStatusCode.OK, body.ToJsonString());
                Check.Equal(1, body["entries"]!.AsArray().Count); Check.Equal(2, body["matches"]!.AsArray().Count);
                Check.Equal(1L, body["entries"]![0]!["served_chapter_count"]!.GetValue<long>());
            }
            using (var stats = await admin.PostAsync("/api/v1/matrix/chapter-stats", new StringContent("{\"ids\":[1]}", Encoding.UTF8, "application/json")))
            {
                var body = JsonNode.Parse(await stats.Content.ReadAsStringAsync())!.AsObject();
                Check.True(body["entry_stats"]!["1"] is JsonObject && body["record_stats"]!.AsArray().Count >= 1, body.ToJsonString());
            }
            var candidate = world.Scalar(DatabaseFile.Origins, "SELECT id FROM chapter_candidates");
            using (var rejected = await admin.PostAsync("/api/v1/candidates/" + candidate + "/reject", null)) Check.Equal(HttpStatusCode.OK, rejected.StatusCode);
            using (var missing = await admin.PostAsync("/api/v1/candidates/424242/restore", null)) Check.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using (var policy = await admin.GetAsync("/api/v1/serving-upgrades/1")) Check.Equal(HttpStatusCode.OK, policy.StatusCode);
            using (var unavailable = await admin.GetAsync("/api/v1/gallery-policies/1001")) Check.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            using (var profile = await admin.PutAsync("/api/v1/profiles/1", new StringContent("{\"revision\":0,\"profile\":{\"quality\":\"quality\"}}", Encoding.UTF8, "application/json")))
                Check.True(profile.StatusCode == HttpStatusCode.OK, await profile.Content.ReadAsStringAsync());
            Check.True(world.Log.Kinds.Contains("chapter.candidate.reject") && world.Log.Kinds.Contains("serving.profile"));
        }
        finally { await host.StopAsync(); }
    }

    private sealed class FakePages(int count) : ICandidatePageReader
    {
        public (int Width, int Height) Size { get; set; } = (800, 1200);
        public HashSet<int> Failing { get; } = [];
        public HashSet<int> Broken { get; } = [];
        public List<(long Candidate, int Page)> Read { get; } = [];
        public Task<int> PageCountAsync(long candidateId, CancellationToken cancellationToken) => Task.FromResult(count);
        public Task<byte[]> ReadPageAsync(long candidateId, int pageIndex, CancellationToken cancellationToken)
        {
            lock (Read) Read.Add((candidateId, pageIndex));
            if (Failing.Contains(pageIndex)) throw new HttpRequestException("upstream page failed");
            var header = Broken.Contains(pageIndex) ? "broken" : Size.Width.ToString(CultureInfo.InvariantCulture) + "x" + Size.Height.ToString(CultureInfo.InvariantCulture);
            var bytes = new byte[300_000]; Encoding.ASCII.GetBytes(header).CopyTo(bytes, 0);
            return Task.FromResult(bytes);
        }
    }

    /// <summary>Reads the "WxH" header written by <see cref="FakePages"/>; "broken" is an undecodable image.</summary>
    private sealed class FakeDecoder : IPageRasterDecoder
    {
        public (int Width, int Height) Measure(byte[] image)
        {
            var text = Encoding.ASCII.GetString(image, 0, 16).TrimEnd('\0');
            if (text.StartsWith("broken", StringComparison.Ordinal)) throw new InvalidDataException("broken image");
            var parts = text.Split('x');
            return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
        }

        public PageRaster Decode(byte[] image, int maximumSide)
        {
            var (width, height) = Measure(image);
            var scale = Math.Min(1.0, maximumSide / (double)Math.Max(width, height));
            int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));
            return new PageRaster(width, height, new byte[w * h * 3], w, h);
        }
    }
}
