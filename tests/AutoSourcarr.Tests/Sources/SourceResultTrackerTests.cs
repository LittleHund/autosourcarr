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
