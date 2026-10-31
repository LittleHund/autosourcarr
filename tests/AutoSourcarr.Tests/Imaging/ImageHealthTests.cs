using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Imaging;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources;
using ImageMagick;

namespace AutoSourcarr.Tests;

public static class ImageHealthTests
{
    public static void Register(TestSuite suite)
    {
        foreach (var format in new[] { "png", "jpg", "webp", "gif", "bmp", "avif" })
        {
            suite.Add("raster analysis: " + format + " decodes with pixel bounds", () =>
            {
                var bytes = File.ReadAllBytes(ConfigurationTests.Fixture("Images/sample." + format));
                using var decoded = RasterImages.Open(bytes);
                Check.Equal(8, decoded.Width); Check.Equal(6, decoded.Height);
                Check.Throws<ImageAnalysisLimitException>(() => { using var _ = RasterImages.Open(bytes, 47); });
            });
        }
        suite.Add("raster analysis: empty, HTML and truncated images cannot pass", () =>
        {
            Check.Throws<ImageAnalysisLimitException>(() => RasterImages.Open([]));
            Check.Throws<InvalidDataException>(() => RasterImages.Open(Encoding.UTF8.GetBytes("<html>login</html>")));
            var png = File.ReadAllBytes(ConfigurationTests.Fixture("Images/sample.png"));
            Check.Throws<InvalidDataException>(() => RasterImages.Open(png[..40]));
        });
        suite.Add("raster analysis: native policy denies vectors and paths outside private scratch", () =>
        {
            var png = File.ReadAllBytes(ConfigurationTests.Fixture("Images/sample.png"));
            using var decoded = RasterImages.Open(png);
            Check.Throws<MagickPolicyErrorException>(() =>
            {
                using var vector = new MagickImage(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"2\" height=\"2\"><rect width=\"2\" height=\"2\"/></svg>"), MagickFormat.Svg);
            });
            Check.Throws<MagickPolicyErrorException>(() =>
            {
                using var file = new MagickImage(ConfigurationTests.Fixture("Images/sample.png"), MagickFormat.Png);
            });
        });
        suite.Add("source health: installed-only observations preserve success and repair dates", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            database.Write(DatabaseFile.Catalog, session => session.Execute("""
                INSERT INTO source_config(source_id,name,lang,base_url,last_seen,available,enabled_language) VALUES
                ('-1','One','en','https://one.test','2026-10-19',1,1),('-2','Two','en','https://two.test','2026-10-19',1,0),
                ('-3','Removed','en','https://removed.test','2026-10-19',0,1);
                """));
            double now = 100000; var store = new SourceHealthStore(database, clock: () => now);
            var snapshot = store.Snapshot(); Check.Equal(2, snapshot["items"]!.AsArray().Count);
            Check.True(snapshot["daily"]!["enabled"]!.GetValue<bool>()); store.ConfigureDaily(false); Check.True(!store.DailyEnabled());
            store.Finish("-1", "working", "sample passed", [], "manual", startedAt: now - 1);
            now += 100; store.Finish("-1", "deferred", "capacity busy", [], "automatic");
            var row = store.Snapshot()["items"]![0]!;
            Check.Equal(100000.0, row["last_success_at"]!.GetValue<double>()); Check.Equal(now + 300, row["next_check_at"]!.GetValue<double>());
            var jobId = Guid.NewGuid().ToString("N");
            store.UpdateRepairObservations(new JsonArray(new JsonObject { ["id"] = jobId, ["state"] = "installed", ["pkg_name"] = "example", ["updated_at"] = now + 1, ["expected_ids"] = new JsonArray("-1") }), new());
            row = store.Snapshot()["items"]![0]!;
            Check.Equal(now + 1, row["last_repaired_at"]!.GetValue<double>()); Check.True(row["needs_recheck"]!.GetValue<bool>());
            Check.Equal(100000.0, row["last_success_at"]!.GetValue<double>());
            for (var i = 0; i < 10; i++) store.Finish("-1", "inconclusive", "empty sample", [], "manual");
            Check.Equal(8, store.Snapshot()["items"]![0]!["history"]!.AsArray().Count);
            Check.True(!store.Source("-2")!["eligible"]!.GetValue<bool>());
        });
        suite.Add("source health: functional sample validates catalogs, search, metadata and raster pages", () =>
        {
            var io = new BoundedWorkScheduler(1, 4);
            using var client = Client("working");
            try
            {
                var checks = new JsonArray();
                new SourceHealthProbe(client, io, TimeSpan.Zero).CheckAsync(new() { ["source_id"] = "-1", ["supports_latest"] = 1 }, checks).GetAwaiter().GetResult();
                Check.Equal(7, checks.Count); Check.True(checks.All(row => row!["state"]!.GetValue<string>() == "passed"));
            }
            finally { io.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("source health: empty catalogs and unsafe image responses never become working", () =>
        {
            foreach (var (mode, state) in new[] { ("empty", "inconclusive"), ("external-page", "failed"), ("login-image", "failed") })
            {
                var io = new BoundedWorkScheduler(1, 4); using var client = Client(mode);
                try
                {
                    var error = Check.Throws<Exception>(() => new SourceHealthProbe(client, io, TimeSpan.Zero)
                        .CheckAsync(new() { ["source_id"] = "-1" }, []).GetAwaiter().GetResult());
                    Check.Equal(state, SourceHealthFailures.Classify(error));
                }
                finally { io.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            Check.Equal("deferred", SourceHealthFailures.Classify(new SourceBudgetExceededException()));
            Check.Equal("inconclusive", SourceHealthFailures.Classify(new HttpRequestException("transport")));
            Check.Equal("verification", SourceHealthFailures.Classify(new SuwayomiException("CAPTCHA required")));
            Check.Equal("inconclusive", SourceHealthFailures.Classify(new ImageAnalysisLimitException("too large")));
        });
    }

    private static SuwayomiClient Client(string mode) => new TestSourceEngine(mode).Client();
}
