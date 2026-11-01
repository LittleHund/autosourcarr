using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair.Migration;

namespace AutoSourcarr.Tests.Repair;

/// <summary>Phase 6f: REP-6 automatic orphan repair passes and REP-1 last-working keeps.</summary>
public static class AutoRepairWatcherTests
{
    private const string Prefix = "eu.kanade.tachiyomi.extension.en.";

    public static void Register(TestSuite suite)
    {
        suite.Add("auto repair: with the switch off a pass reads and changes nothing", () =>
        {
            using var temp = new Sandbox();
            var world = new World();
            world.Moved.Add("a.example");
            world.Candidates.Add(new(Prefix + "a", "https://a.example", "health-failed"));
            var watcher = world.Watcher(temp.Path, enabled: () => false);
            var summary = watcher.RunOnceAsync().GetAwaiter().GetResult();
            Check.Equal("off", summary["state"]!.GetValue<string>());
            Check.Equal(0, world.CandidateReads);
            Check.Equal(0, world.Observed.Count);
            Check.Equal(0, world.Repairs.Count);
            Check.True(!File.Exists(watcher.StatePath), "no state written");
        });

        suite.Add("auto repair: only failing packages whose old address redirects to another site are repaired", () =>
        {
            using var temp = new Sandbox();
            var world = new World();
            world.Moved.Add("moved.example");
            world.Moved.Add("www.second.example");
            world.SameSite.Add("same.example");
            world.Throwing.Add("broken.example");
            world.Candidates.AddRange([
                new(Prefix + "moved", "https://moved.example/", "health-failed"),
                new(Prefix + "moved", "https://moved.example/other", "health-failed"),
                new(Prefix + "same", "https://same.example", "health-inconclusive"),
                new(Prefix + "broken", "https://broken.example", "health-failed"),
                new(Prefix + "multi", "https://quiet.example", "orphan"),
                new(Prefix + "multi", "https://www.second.example", "orphan"),
                new("not a package", "https://moved.example", "orphan"),
            ]);
            var watcher = world.Watcher(temp.Path);
            var summary = watcher.RunOnceAsync().GetAwaiter().GetResult();
            Check.Equal("done", summary["state"]!.GetValue<string>());
            Check.Equal(2, world.Repairs.Count);
            Check.Equal(Prefix + "moved", world.Repairs[0].Package);
            Check.Equal("moved.example", world.Repairs[0].Options.OldHost);
            Check.True(world.Repairs[0].Options.NewHost is null, "discovery path: no explicit new host");
            Check.Equal(Prefix + "multi", world.Repairs[1].Package);
            Check.Equal("www.second.example", world.Repairs[1].Options.OldHost);
            Check.Equal(2, summary["repaired"]!.GetValue<int>());
            var notMoved = summary["not_moved"]!.AsArray().Select(node => node!.GetValue<string>()).ToHashSet();
            Check.True(notMoved.SetEquals([Prefix + "same", Prefix + "broken"]), "same-site and unreachable packages are not repaired");
            // One observation per distinct host: moved (once), same, broken, quiet and second.
            Check.Equal(5, world.Observed.Count);
            var state = watcher.State();
            Check.Equal("repaired", state["packages"]![Prefix + "moved"]!["state"]!.GetValue<string>());
            Check.Equal("done", state["last_pass"]!["state"]!.GetValue<string>());
        });

        suite.Add("auto repair: at most five packages a pass, attempted packages wait seven days", () =>
        {
            using var temp = new Sandbox();
            var world = new World();
            for (var i = 0; i < 7; i++)
            {
                world.Moved.Add("site" + i + ".example");
                world.Candidates.Add(new(Prefix + "s" + i, "https://site" + i + ".example", "health-failed"));
            }
            world.Outcome = package => package.EndsWith(".s1", StringComparison.Ordinal) ? "refused" : "repaired";
            var watcher = world.Watcher(temp.Path);

            var first = watcher.RunOnceAsync().GetAwaiter().GetResult();
            Check.Equal(5, world.Repairs.Count);
            Check.True(first["capped"]?.GetValue<bool>() == true, "first pass capped");
            Check.Equal(4, first["repaired"]!.GetValue<int>());

            world.Time.Advance(TimeSpan.FromDays(1));
            var second = watcher.RunOnceAsync().GetAwaiter().GetResult();
            Check.Equal(7, world.Repairs.Count);
            Check.Equal(5, second["skipped_recent"]!.AsArray().Count);
            Check.True(world.Repairs.Skip(5).All(item => item.Package is Prefix + "s5" or Prefix + "s6"), "second pass takes the rest");

            world.Time.Advance(TimeSpan.FromDays(1));
            var third = watcher.RunOnceAsync().GetAwaiter().GetResult();
            Check.Equal(7, world.Repairs.Count);
            Check.Equal(7, third["skipped_recent"]!.AsArray().Count);
            Check.Equal(0, third["attempted"]!.GetValue<int>());

            // A refused package is skipped like a repaired one; after seven days both are tried again.
            world.Time.Advance(TimeSpan.FromDays(6));
            watcher.RunOnceAsync().GetAwaiter().GetResult();
            Check.Equal(12, world.Repairs.Count);
            Check.True(world.Repairs.Skip(7).Select(item => item.Package).Contains(Prefix + "s1"), "refused package retried after seven days");
        });

        suite.Add("auto repair: passes never overlap; the loop survives errors and stops on cancellation", () =>
        {
            using var temp = new Sandbox();
            var world = new World();
            world.Moved.Add("slow.example");
            world.Candidates.Add(new(Prefix + "slow", "https://slow.example", "health-failed"));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            world.Gate = async () => { entered.TrySetResult(); await release.Task; };
            var watcher = world.Watcher(temp.Path);

            var running = Task.Run(() => watcher.RunOnceAsync());
            Check.True(entered.Task.Wait(TimeSpan.FromSeconds(10)), "first pass reached the repair");
            Check.True(watcher.Running, "a pass is running");
            var busy = watcher.RunOnceAsync().GetAwaiter().GetResult();
            Check.Equal("busy", busy["state"]!.GetValue<string>());
            release.SetResult();
            Check.Equal("done", running.GetAwaiter().GetResult()["state"]!.GetValue<string>());
            Check.Equal(1, world.Repairs.Count);
            Check.True(!watcher.Running, "pass finished");

            // Cancelling a running pass: RunOnceAsync throws, the next pass works.
            world.Time.Advance(TimeSpan.FromDays(8));
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            world.Gate = async () => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, world.RepairToken); };
            using (var cancel = new CancellationTokenSource())
            {
                var cancelled = Task.Run(() => watcher.RunOnceAsync(cancel.Token));
                Check.True(entered.Task.Wait(TimeSpan.FromSeconds(10)), "pass reached the repair");
                cancel.Cancel();
                Check.Throws<OperationCanceledException>(() => cancelled.GetAwaiter().GetResult());
            }
            Check.True(!watcher.Running, "cancelled pass released");

            // The background loop: a failing candidate read becomes a failed summary, a trigger runs a pass at once,
            // and cancellation ends the loop without an exception.
            var loopWorld = new World { CandidateError = new InvalidOperationException("database locked") };
            var summaries = new List<JsonObject>();
            var passDone = new SemaphoreSlim(0);
            var loopWatcher = loopWorld.Watcher(Path.Combine(temp.Path, "loop"), record: summary => { lock (summaries) summaries.Add(summary); passDone.Release(); },
                options: new AutoRepairOptions { FirstDelay = TimeSpan.FromHours(1), Interval = TimeSpan.FromHours(1) });
            var errors = new List<Exception>();
            using var stop = new CancellationTokenSource();
            var loop = loopWatcher.RunAsync(error => { lock (errors) errors.Add(error); }, stop.Token);
            loopWatcher.Trigger();
            Check.True(passDone.Wait(TimeSpan.FromSeconds(10)), "triggered pass ran");
            lock (summaries) Check.Equal("failed", summaries[0]["state"]!.GetValue<string>());
            stop.Cancel();
            Check.True(loop.Wait(TimeSpan.FromSeconds(10)), "loop ended");
            Check.True(loop.IsCompletedSuccessfully, "loop ended without an exception");
            lock (errors) Check.Equal(0, errors.Count);
        });

        suite.Add("last working: a passed package's jar is kept once per digest", () =>
        {
            using var temp = new Sandbox();
            var host = new KeepHost();
            var repair = new DomainRepair(Path.Combine(temp.Path, "source-maintenance"), host, new SemaphoreSlim(1, 1));
            var kept = repair.KeepLastWorkingAsync(KeepHost.Package).GetAwaiter().GetResult();
            Check.True(kept is not null, "first keep writes a record");
            Check.Equal("passed-health-check", kept!["reason"]!.GetValue<string>());
            var directory = repair.LastWorkingDirectory(KeepHost.Package);
            Check.True(File.Exists(Path.Combine(directory, DomainMigration.Sha(host.Jar) + ".jar")), "jar kept");

            // Same version: the jar is not read again.
            Check.True(repair.KeepLastWorkingAsync(KeepHost.Package).GetAwaiter().GetResult() is null, "same version not kept again");
            Check.Equal(1, host.Reads);
            // New version, same bytes: read once, but the digest is already kept.
            host.Version = "1.4.8";
            Check.True(repair.KeepLastWorkingAsync(KeepHost.Package).GetAwaiter().GetResult() is null, "same digest not kept again");
            Check.Equal(2, host.Reads);
            Check.Equal(2, Directory.GetFiles(directory).Length);
            // New bytes: a second kept jar.
            host.Version = "1.4.9"; host.Jar = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23];
            Check.True(repair.KeepLastWorkingAsync(KeepHost.Package).GetAwaiter().GetResult() is not null, "new digest kept");
            Check.Equal(4, Directory.GetFiles(directory).Length);
            // Not installed or not loading: nothing kept.
            host.LoadError = "ClassNotFoundException";
            host.Version = "2.0.0";
            Check.True(repair.KeepLastWorkingAsync(KeepHost.Package).GetAwaiter().GetResult() is null, "broken extension not kept");
            Check.Equal(3, host.Reads);
        });
    }

    /// <summary>Fake candidates, observations and repairs with a controllable clock.</summary>
    private sealed class World
    {
        public List<AutoRepairCandidate> Candidates { get; } = [];
        public HashSet<string> Moved { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SameSite { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Throwing { get; } = new(StringComparer.Ordinal);
        public List<string> Observed { get; } = [];
        public List<(string Package, DomainRepairOptions Options)> Repairs { get; } = [];
        public Func<string, string> Outcome { get; set; } = _ => "repaired";
        public Func<Task>? Gate { get; set; }
        public Exception? CandidateError { get; set; }
        public CancellationToken RepairToken { get; private set; }
        public int CandidateReads { get; private set; }
        public FakeTime Time { get; } = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

        public AutoRepairWatcher Watcher(string root, Func<bool>? enabled = null, Action<JsonObject>? record = null, AutoRepairOptions? options = null) =>
            new(Path.Combine(root, "source-maintenance"), enabled ?? (() => true),
                _ =>
                {
                    CandidateReads++;
                    return CandidateError is { } error ? Task.FromException<IReadOnlyList<AutoRepairCandidate>>(error)
                        : Task.FromResult<IReadOnlyList<AutoRepairCandidate>>(Candidates.ToList());
                },
                (url, _) =>
                {
                    lock (Observed) Observed.Add(url);
                    var host = new Uri(url).Host;
                    if (Throwing.Contains(host)) throw new HttpRequestException("no route");
                    var final = Moved.Contains(host) ? "https://new-" + host.Replace("www.", "", StringComparison.Ordinal) + "/"
                        : SameSite.Contains(host) ? "https://www." + host + "/" : url;
                    return Task.FromResult(new JsonObject { ["state"] = "working", ["final_url"] = final });
                },
                async (package, repairOptions, token) =>
                {
                    lock (Repairs) Repairs.Add((package, repairOptions));
                    RepairToken = token;
                    if (Gate is { } gate) await gate();
                    return new JsonObject { ["state"] = Outcome(package), ["message"] = "test", ["new_host"] = "new.example" };
                },
                options, Time, record);
    }

    public sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A host with one installed package for last-working keeps.</summary>
    private sealed class KeepHost : IDomainRepairHost
    {
        public const string Package = Prefix + "keep";
        public byte[] Jar { get; set; } = [80, 75, 5, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        public string Version { get; set; } = "1.4.7";
        public string? LoadError { get; set; }
        public int Reads { get; private set; }
        public IContentCheckSource Content { get; } = null!;

        public Task<JsonObject?> ExtensionAsync(string packageName, CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(new JsonObject
        {
            ["pkgName"] = Package, ["versionName"] = Version, ["versionCodeLong"] = "1", ["isInstalled"] = true, ["loadError"] = LoadError,
            ["source"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["id"] = "9", ["name"] = "Keep", ["lang"] = "en", ["baseUrl"] = "https://keep.example" }) },
        });
        public Task<byte[]> ReadJarAsync(string packageName, CancellationToken cancellationToken) { Reads++; return Task.FromResult(Jar); }
        public Task<JsonObject> InstallJarAsync(string packageName, byte[] jar, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
