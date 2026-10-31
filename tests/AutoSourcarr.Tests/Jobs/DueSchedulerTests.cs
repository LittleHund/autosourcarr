using System.Text.Json.Nodes;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests.Jobs;

/// <summary>Port of due_scheduler.py: same delays, same text timestamps, same crawl-page rules.</summary>
public static class DueSchedulerTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("due work: complete waits the interval, deferred 60 s, incomplete backs off and is capped at one hour", () =>
        {
            using var temp = new Sandbox();
            using var database = Open(temp.Path);
            var time = new ManualTime(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
            var due = new DueScheduler(database, time);
            Check.True(due.Eligible("library", "7"), "Never scheduled work is due.");
            due.Attempted("library", "7", "rev1");
            Check.Equal("running", due.State("library", "7").Outcome);
            var complete = due.Finished("library", "7", complete: true, success: true, interval: 900);
            Check.Equal("2026-10-31T12:15:00+00:00", complete.NextDue);
            Check.Equal("rev1", complete.CompleteRevision);
            Check.True(!due.Eligible("library", "7", "rev1"));
            Check.True(due.Eligible("library", "7", "rev2"), "A changed configuration makes the work due at once.");
            Check.Equal("2026-10-31T12:01:00+00:00", due.Finished("library", "7", deferred: true).NextDue);
            var delays = new List<string>();
            for (var index = 0; index < 8; index++) delays.Add(due.Finished("library", "7", reason: "timeout").NextDue);
            Check.Equal("2026-10-31T12:02:00+00:00", delays[0]);
            Check.Equal("2026-10-31T12:04:00+00:00", delays[1]);
            Check.Equal("2026-10-31T13:00:00+00:00", delays[7]);
            Check.Equal(8L, due.State("library", "7").Failures);
            time.Now = time.Now.AddHours(2);
            Check.True(due.Eligible("library", "7"));
            Check.Equal(1, due.Snapshot().Count);
        });

        suite.Add("due work: timestamps match Python isoformat (microseconds only when set)", () =>
        {
            Check.Equal("2026-10-31T12:00:00+00:00", DueScheduler.Iso(new DateTimeOffset(2026, 10, 31, 13, 0, 0, TimeSpan.FromHours(1))));
            Check.Equal("2026-10-31T12:00:00.123456+00:00", DueScheduler.Iso(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero).AddTicks(1234560)));
        });

        suite.Add("due work: recent and backlog crawls take turns; the one due first goes, recent on ties", () =>
        {
            using var temp = new Sandbox();
            using var database = Open(temp.Path);
            var time = new ManualTime(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
            var due = new DueScheduler(database, time);
            Check.Equal("recent", due.CrawlLane("pair", "c1"));
            due.Attempted("catalog_recent", "pair", "c1"); due.Finished("catalog_recent", "pair", complete: true);
            Check.Equal("backlog", due.CrawlLane("pair", "c1"));
        });

        suite.Add("crawl pages: a page stays until every record succeeded; failed records retry alone; bad pages keep the cursor", () =>
        {
            using var temp = new Sandbox();
            using var database = Open(temp.Path);
            var time = new ManualTime(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
            var pages = new CrawlPages(database, time);
            Check.Throws<InvalidDataException>(() => pages.SavePage("k", 1, new JsonObject { ["mangas"] = new JsonArray(), ["hasNextPage"] = "yes" }));
            Check.Throws<InvalidDataException>(() => pages.SavePage("k", 1, new JsonObject { ["mangas"] = new JsonArray(new JsonObject { ["id"] = 0 }), ["hasNextPage"] = true }));
            var saved = pages.SavePage("k", 3, new JsonObject
            {
                ["hasNextPage"] = true,
                ["mangas"] = new JsonArray(new JsonObject { ["id"] = 1, ["title"] = "One", ["private"] = "dropped" }, new JsonObject { ["id"] = 2, ["title"] = "Two" }),
            });
            Check.Equal(3L, saved["page"]!.GetValue<long>());
            Check.True(saved["items"]![0]!["manga"]!["private"] is null, "Only known feed fields are kept.");
            Check.Equal(2, pages.Ready("k", 10).Count);
            pages.Acknowledge("k", 1, success: true);
            pages.Acknowledge("k", 2, success: false);
            Check.Equal(0, pages.Ready("k", 10).Count);
            Check.True(pages.Finish("k") is null, "A page with a failed record is kept.");
            time.Now = time.Now.AddMinutes(3);
            Check.Equal(2L, pages.Ready("k", 10).Single()["id"]!.GetValue<long>());
            pages.Acknowledge("k", 2, success: true);
            Check.Equal((long?)4, pages.Finish("k"));
            Check.True(pages.Load("k") is null);
        });
    }

    private static SqliteDatabase Open(string path)
    {
        var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = path });
        new SchemaMigrator(database).Initialize();
        return database;
    }

    internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
