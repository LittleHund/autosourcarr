using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Tests;

/// <summary>Phase 11: discovery results feed source health (0.4.5 _record_source_result); hard rule 3 holds.</summary>
public static class SourceResultTrackerTests
{
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20000); public override DateTimeOffset GetUtcNow() => Now; }

    public static void Register(TestSuite suite)
    {
        suite.Add("source results: local timeouts, cancelled deadlines and capacity deferrals never pause a source (hard rule 3)", () =>
        {
            var clock = new Clock(); var tracker = new SourceResultTracker(() => new AutoSourcarrSettings { SourceFailureThreshold = 1, SourceCooldownSeconds = 120 }, clock);
            var changes = 0; tracker.Changed += _ => changes++;
            foreach (var local in new Exception[] { new TimeoutException("deadline"), new OperationCanceledException(), new TaskCanceledException(),
                new SourceWorkDeferredException("Source capacity busy; request deferred before upstream"), new InvalidOperationException("Request timed out after 5 s"),
                new AggregateException(new TimeoutException("a"), new SourceWorkDeferredException("b")) })
            {
                Check.True(SourceResultTracker.IsLocal(local), "local: " + local.GetType().Name);
                tracker.Record("1", local);
            }
            Check.True(!tracker.Paused("1"), "never paused by local conditions");
            Check.Equal(0, tracker.Snapshot().Count);
            Check.Equal(0, changes);
        });

        suite.Add("source results: the app's own engine link (refused/reset connections, an unavailable or restarting host) and any failure while the engine is not ready never count (audit A3)", () =>
        {
            var clock = new Clock(); var ready = true;
            var tracker = new SourceResultTracker(() => new AutoSourcarrSettings { SourceFailureThreshold = 1, SourceCooldownSeconds = 120 }, clock, () => ready);
            foreach (var link in new Exception[]
            {
                new SourceEngineUnavailableException("Source settings are unavailable during shutdown or reset; retry shortly"),
                new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (127.0.0.1:4567)"),
                new HttpRequestException("An error occurred while sending the request."),
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset),
                new IOException("Unable to read data from the transport connection", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset)),
                new SuwayomiException("The source host is restarting; retry shortly"),
                new InvalidOperationException("wrapped", new HttpRequestException(HttpRequestError.ConnectionError, "refused")),
            })
            {
                Check.True(SourceResultTracker.IsLocal(link), "engine link: " + link.Message);
                tracker.Record("1", link);
            }
            Check.True(!tracker.Paused("1"), "the engine link never pauses a source");
            // A host restart during discovery: while the engine is not ready even a site-looking failure does not count.
            ready = false;
            tracker.Record("2", new InvalidOperationException("HTTP 500 Internal Server Error"));
            tracker.Record("2", new InvalidOperationException("Cloudflare challenge: human verification"));
            Check.True(!tracker.Paused("2"), "nothing counts during a restart");
            Check.Equal(0, tracker.Snapshot().Count);
            ready = true;
            // A site's own answer with an HTTP status still counts once the engine is back.
            tracker.Record("2", new HttpRequestException("Response status code does not indicate success: 503", null, System.Net.HttpStatusCode.ServiceUnavailable));
            Check.True(tracker.Paused("2"), "a real failure after the restart counts");
        });

        suite.Add("source results: repeated real failures pause a source, a challenge pauses at once, an answer or the cooldown ends it", () =>
        {
            var clock = new Clock(); var tracker = new SourceResultTracker(() => new AutoSourcarrSettings { SourceFailureThreshold = 3, SourceCooldownSeconds = 120 }, clock);
            var changes = new List<string>(); tracker.Changed += id => changes.Add(id);
            tracker.Record("1", new InvalidOperationException("HTTP 500 Internal Server Error"));
            tracker.Record("1", new InvalidOperationException("HTTP 500 Internal Server Error"));
            Check.True(!tracker.Paused("1"), "below the threshold");
            tracker.Record("1", new InvalidOperationException("HTTP 500 Internal Server Error"));
            Check.True(tracker.Paused("1"), "paused at the threshold");
            tracker.Record("1", new InvalidOperationException("HTTP 500 Internal Server Error"));
            Check.Equal(1, changes.Count); // once per pause, not per failure
            Check.Equal(1, tracker.PausedCount());
            clock.Now = clock.Now.AddSeconds(121);
            Check.True(!tracker.Paused("1"), "the cooldown ends the pause");

            tracker.Record("2", new InvalidOperationException("Cloudflare challenge: human verification"));
            Check.True(tracker.Paused("2"), "a challenge pauses at once");
            var row = tracker.Snapshot().Single(item => item!["source_id"]!.GetValue<string>() == "2")!;
            Check.Equal("captcha", row["kind"]!.GetValue<string>());
            Check.True(row["retry_after_seconds"]!.GetValue<double>() >= 300, "a CAPTCHA pauses at least 5 minutes");
            tracker.Record("2", null);
            Check.True(!tracker.Paused("2"), "an answer clears the source");
            Check.Equal("1,2,2", string.Join(",", changes));
        });
    }
}
