using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests.Benchmarks;

/// <summary>
/// PIPE-8 / PERF-2 benchmark tool mode: <c>AutoSourcarr.Tests --benchmark [result.json] [--gate] [--entries N]</c>.
/// <list type="bullet">
/// <item>Large-library page load: a generated catalogue (50,000 entries, two source records each), the first page, deep
/// keyset pages and a title search through <see cref="CatalogueQueries"/> (the code behind /api/v1/catalogue/entries).</item>
/// <item>Job queue under load: 5,000 short jobs of five types (one with a minimum share) on the default three workers;
/// throughput, how long a High job and a share-holding Low job wait while the flood runs.</item>
/// </list>
/// Prints one JSON object (also written to the file when given). With <c>--gate</c> the exit code is 1 when a number is
/// over its budget (<see cref="Budgets"/>), so the pipeline can stop a release that got slower.
/// </summary>
public static class PerformanceBenchmark
{
    /// <summary>Budgets the gate compares against (milliseconds, jobs per second). Generous for shared CI runners.</summary>
    public static readonly IReadOnlyDictionary<string, double> Budgets = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["page_first_p95_ms"] = 250, ["page_deep_p95_ms"] = 250, ["search_p95_ms"] = 1500,
        ["queue_jobs_per_second_min"] = 500, ["queue_high_wait_ms"] = 2000, ["queue_share_wait_ms"] = 2000,
    };

    public static int Run(IReadOnlyList<string> args, TextWriter output)
    {
        string? file = null; var gate = false; var entries = 50_000;
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] == "--gate") gate = true;
            else if (args[index] == "--entries" && index + 1 < args.Count && int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0)
            { entries = count; index++; }
            else if (!args[index].StartsWith("--", StringComparison.Ordinal)) file = args[index];
            else { Console.Error.WriteLine("Usage: AutoSourcarr.Tests --benchmark [result.json] [--gate] [--entries N]"); return 2; }
        }
        var result = new JsonObject
        {
            ["benchmark"] = "pipe-8", ["entries"] = entries, ["machine"] = Environment.MachineName.Length > 0 ? Environment.ProcessorCount + " cpu" : "",
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        };
        Library(result, entries);
        Queue(result);
        var failed = new JsonArray();
        foreach (var (name, budget) in Budgets)
        {
            var key = name.EndsWith("_min", StringComparison.Ordinal) ? name[..^4] : name;
            if (result[key] is not JsonValue value || !value.TryGetValue<double>(out var measured)) continue;
            var over = name.EndsWith("_min", StringComparison.Ordinal) ? measured < budget : measured > budget;
            if (over) failed.Add(key + " " + measured.ToString("0.###", CultureInfo.InvariantCulture) + " vs budget " + budget.ToString(CultureInfo.InvariantCulture));
        }
        result["budgets"] = new JsonObject(Budgets.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value)));
        result["over_budget"] = failed;
        var text = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(text);
        if (file is not null) File.WriteAllText(file, text + "\n");
        return gate && failed.Count > 0 ? 1 : 0;
    }

    private static void Library(JsonObject result, int entries)
    {
        using var sandbox = new Sandbox();
        using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = sandbox.Path });
        new SchemaMigrator(database).Initialize();
        var watch = Stopwatch.StartNew();
        database.Write(DatabaseFile.Catalog, session =>
        {
            session.Execute("INSERT INTO source_config(source_id,name,lang,last_seen) VALUES('1','Alpha','en','2026-01-01'),('2','Beta','en','2026-01-01')");
            for (var id = 1; id <= entries; id++)
            {
                var title = "Generated Title " + id.ToString(CultureInfo.InvariantCulture) + (id % 7 == 0 ? " Colored" : "");
                session.Execute("INSERT INTO entries(id,preferred_title,author,created_at,updated_at) VALUES($id,$title,$author,'2026-01-01','2026-01-01')",
                    ("$id", id), ("$title", title), ("$author", "Writer " + (id % 977).ToString(CultureInfo.InvariantCulture)));
                foreach (var source in new[] { "1", "2" })
                    session.Execute("INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,match_type,last_checked) VALUES($e,$s,$m,$t,'auto','2026-01-01')",
                        ("$e", id), ("$s", source), ("$m", id * 10L + (source == "1" ? 1 : 2)), ("$t", title));
            }
            return 0;
        });
        result["generate_seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 2);
        var queries = new CatalogueQueries(database, new object(), _ => false);
        // Warm-up (first statements compile, pages fault in), then measured runs.
        queries.ListPage(null, 50);
        result["page_first_p95_ms"] = P95(30, () => queries.ListPage(null, 50));
        var cursors = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 200 && (cursor = queries.ListPage(cursor, 50).NextCursor) is not null; page++)
            if (page % 20 == 19) cursors.Add(cursor);
        var deep = 0;
        result["page_deep_p95_ms"] = P95(30, () => queries.ListPage(cursors[deep++ % cursors.Count], 50));
        var walk = Stopwatch.StartNew(); var pages = 0; cursor = null;
        do { cursor = queries.ListPage(cursor, 200).NextCursor; pages++; } while (cursor is not null && pages < 50);
        result["page_walk_ms_per_200"] = Math.Round(walk.Elapsed.TotalMilliseconds / pages, 2);
        var term = 0;
        result["search_p95_ms"] = P95(10, () => queries.List("Generated Title " + (1000 + 3_917 * term++ % Math.Max(1, entries - 1000)).ToString(CultureInfo.InvariantCulture), 50));
    }

    private static void Queue(JsonObject result)
    {
        var tasks = new TaskRegistry();
        var queue = new JobQueue(tasks);
        try
        {
            var types = Enumerable.Range(0, 4).Select(index => new JobType("bench-" + index.ToString(CultureInfo.InvariantCulture), "Benchmark", JobPriority.Normal, RunLimit: 3)).ToArray();
            var share = new JobType("bench-share", "Benchmark share", JobPriority.Low, RunLimit: 1) { MinimumShare = 1 };
            var high = new JobType("bench-high", "Benchmark high", JobPriority.High);
            const int Jobs = 5_000;
            var watch = Stopwatch.StartNew();
            var handles = new List<JobHandle>(Jobs);
            static async Task Work(JobContext job) { await Task.Yield(); var sum = 0; for (var i = 0; i < 2_000; i++) sum += i ^ job.Attempt; job.Result(sum.ToString(CultureInfo.InvariantCulture)); }
            for (var index = 0; index < Jobs; index++) handles.Add(queue.Enqueue(types[index % types.Length], "job-" + index.ToString(CultureInfo.InvariantCulture), Work));
            var highQueued = Stopwatch.StartNew(); var highStarted = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            var highHandle = queue.Enqueue(high, "high", _ => { highStarted.TrySetResult(highQueued.Elapsed.TotalMilliseconds); return Task.CompletedTask; });
            var shareQueued = Stopwatch.StartNew(); var shareStarted = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            var shareHandle = queue.Enqueue(share, "share", _ => { shareStarted.TrySetResult(shareQueued.Elapsed.TotalMilliseconds); return Task.CompletedTask; });
            Task.WhenAll(handles.Select(handle => handle.Completion).Append(highHandle.Completion).Append(shareHandle.Completion)).WaitAsync(TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
            var seconds = watch.Elapsed.TotalSeconds;
            result["queue_jobs"] = Jobs;
            result["queue_jobs_per_second"] = Math.Round(Jobs / seconds, 1);
            result["queue_high_wait_ms"] = Math.Round(highStarted.Task.GetAwaiter().GetResult(), 1);
            result["queue_share_wait_ms"] = Math.Round(shareStarted.Task.GetAwaiter().GetResult(), 1);
            result["queue_failed"] = handles.Count(handle => handle.Completion.Result != JobOutcome.Completed);
        }
        finally { queue.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static double P95(int runs, Action action)
    {
        var samples = new double[runs];
        for (var index = 0; index < runs; index++)
        {
            var watch = Stopwatch.StartNew(); action(); samples[index] = watch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return Math.Round(samples[Math.Min(runs - 1, (int)Math.Ceiling(runs * 0.95) - 1)], 2);
    }
}
