using System.Text.Json.Nodes;
using AutoSourcarr.Api;
using AutoSourcarr.Core.Jobs;

namespace AutoSourcarr.Tests;

/// <summary>
/// UI-13/PERF-8 (phase 11 task 10): the live topics the UI subscribes to on /api/v1/events. Each burst of changes inside
/// the coalescing window reaches the browser as exactly one event, with the changed keys where the topic has them.
/// </summary>
public static class LiveTopicTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("live topics: a burst of keyed changes becomes one event with each key once; more than the cap sets truncated", () =>
        {
            var events = new EventStream();
            using var subscription = events.Subscribe(topics: new HashSet<string>(StringComparer.Ordinal) { "catalogue", "orphans" });
            for (var i = 0; i < 20; i++) events.PulseEntries("catalogue", TimeSpan.FromMilliseconds(150), [1, 2, i % 2 == 0 ? 3 : 2], "metadata");
            events.PulseEntries("catalogue", TimeSpan.FromMilliseconds(150), [2], "titles");
            for (var i = 0; i < 5; i++) events.Debounce("orphans", TimeSpan.FromMilliseconds(150), "inventory");
            var received = Drain(subscription, TimeSpan.FromMilliseconds(600));
            var catalogue = received.Where(item => item.Topic == "catalogue").ToList();
            Check.Equal(1, catalogue.Count);
            Check.Equal("updated", catalogue[0].Data["change"]!.GetValue<string>()); // metadata + titles
            Check.Equal("1,2,3", string.Join(",", catalogue[0].Data["entry_ids"]!.AsArray().Select(id => id!.GetValue<long>())));
            Check.Equal(false, catalogue[0].Data["truncated"]!.GetValue<bool>());
            var orphans = received.Where(item => item.Topic == "orphans").ToList();
            Check.Equal(1, orphans.Count);
            Check.Equal("inventory", orphans[0].Data["change"]!.GetValue<string>());
            Check.True(orphans[0].Data["entry_ids"] is null, "a key-less topic carries only its change");

            events.PulseEntries("catalogue", TimeSpan.FromMilliseconds(100), Enumerable.Range(1, EventStream.MaximumBatchKeys + 10).Select(id => (long)id), "metadata");
            var big = Drain(subscription, TimeSpan.FromMilliseconds(400)).Single(item => item.Topic == "catalogue");
            Check.Equal(EventStream.MaximumBatchKeys, big.Data["entry_ids"]!.AsArray().Count);
            Check.Equal(true, big.Data["truncated"]!.GetValue<bool>());
            // Audit A15b: a change whose entries are not all known (a tag reindex) says so instead of naming none.
            events.PulseEntries("catalogue", TimeSpan.FromMilliseconds(100), [], "tags", truncated: true);
            var unknown = Drain(subscription, TimeSpan.FromMilliseconds(400)).Single(item => item.Topic == "catalogue");
            Check.Equal(true, unknown.Data["truncated"]!.GetValue<bool>());
            Check.Equal("tags", unknown.Data["change"]!.GetValue<string>());
        });

        suite.Add("live topics: the app publishes catalogue, source-health, duplicates, imports, orphans and library once per change burst", () =>
            Phase11WiringTests.Run(async app =>
            {
                var runtime = app.Runtime;
                var topics = new HashSet<string>(StringComparer.Ordinal) { "catalogue", "source-health", "duplicates", "imports", "orphans", "library" };
                using var subscription = runtime.Events.Subscribe(runtime.Events.LastId, topics);
                // catalogue: the metadata chooser, titles and identity hooks name the entries.
                for (var i = 0; i < 5; i++) runtime.CatalogueChanged(41, "metadata");
                runtime.CatalogueChanged(42, "metadata");
                // source-health (audit A3): the external engine of this host is not ready (as during a source-host restart), so
                // even a CAPTCHA reported meanwhile never pauses the source and publishes nothing (hard rule 3).
                for (var i = 0; i < 3; i++) runtime.SourceResults.Record("9001", new InvalidOperationException("Cloudflare challenge: human verification"));
                // duplicates: the duplicate list went stale (several invalidations).
                for (var i = 0; i < 4; i++) runtime.ApiV1!.Duplicates!.Invalidate();
                // imports: a run was created and recounted.
                var runs = runtime.ApiV1!.Imports!.Runs;
                runs.CreateRun("phase11-live", "backup.tachibk"); runs.Recount("phase11-live");
                // orphans: an inventory refresh finished (the external engine is unreachable here: it still finishes).
                await runtime.ApiV1!.SourceInventory!.Orphans.RefreshInventoryAsync();
                // library: the Status page read starts the background status refresh; its end is published.
                using (var admin = app.Client(Phase11WiringTests.AdminToken)) using (await admin.GetAsync("/api/v1/health")) { }
                await Task.Delay(50);
                var received = Drain(subscription, TimeSpan.FromSeconds(2));
                foreach (var topic in new[] { "catalogue", "duplicates", "imports", "orphans", "library" })
                    Check.Equal(1, received.Count(item => item.Topic == topic));
                Check.True(!runtime.SourceResults.Paused("9001") && runtime.SourceResults.Snapshot().Count == 0, "nothing counts while the engine is not ready");
                Check.Equal(0, received.Count(item => item.Topic == "source-health"));
                var catalogue = received.Single(item => item.Topic == "catalogue");
                Check.Equal("41,42", string.Join(",", catalogue.Data["entry_ids"]!.AsArray().Select(id => id!.GetValue<long>())));
                Check.Equal("phase11-live", received.Single(item => item.Topic == "imports").Data["import_ids"]![0]!.GetValue<string>());
                Check.Equal("stale", received.Single(item => item.Topic == "duplicates").Data["change"]!.GetValue<string>());
            }).GetAwaiter().GetResult());
    }

    private static List<AppEvent> Drain(EventSubscription subscription, TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration; var items = new List<AppEvent>();
        while (DateTime.UtcNow < until)
        {
            var item = subscription.NextAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None).GetAwaiter().GetResult();
            if (item is not null) items.Add(item);
        }
        return items;
    }
}
