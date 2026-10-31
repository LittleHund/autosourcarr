using System.Diagnostics;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Tests;

public static class HealthCoordinationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("source health coordination: complete manual job retains distinct observed and confirmed dates", () =>
        {
            using var f = new Fixture();
            var selected = f.Selection(); var queued = f.Coordinator.Queue([selected], "manual");
            Check.True(queued["queued"]!.GetValue<bool>());
            WaitUntil(() => f.Registry.Snapshot()["running"]!.GetValue<int>() == 0);
            var row = f.Coordinator.SnapshotAsync().GetAwaiter().GetResult()["items"]![0]!;
            Check.Equal("working", row["state"]!.GetValue<string>());
            Check.Equal(100000.0, row["last_success_at"]!.GetValue<double>());
            Check.Equal(99990.0, row["last_repaired_at"]!.GetValue<double>());
            Check.Equal(90001.0, row["last_confirmed_working_at"]!.GetValue<double>());
            Check.Equal("working", row["repair_confirmation"]!.GetValue<string>());
            Check.Equal(1, f.Registry.Snapshot()["completed"]!.GetValue<int>());
        });
        suite.Add("source health coordination: active manual checks coalesce and cancellation drains", () =>
        {
            using var f = new Fixture();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Engine.BeforeSend = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); };
            var selected = f.Selection(); var queued = f.Coordinator.Queue([selected], "manual");
            entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check.True(f.Coordinator.Queue([selected], "manual")["coalesced"]!.GetValue<bool>());
            Check.Throws<SourceCheckConflictException>(() => f.Coordinator.Queue([new JsonObject { ["source_id"] = "-2" }], "manual"));
            Check.True(f.Registry.RequestCancelAsync(queued["task_id"]!.GetValue<string>()).GetAwaiter().GetResult());
            WaitUntil(() => f.Coordinator.SnapshotAsync().GetAwaiter().GetResult()["active"]!.AsObject().Count == 0);
            Check.Equal("cancelled", f.Coordinator.SnapshotAsync().GetAwaiter().GetResult()["items"]![0]!["state"]!.GetValue<string>());
            Check.Equal(0, f.Lanes.Snapshot.ManualActive);
        });
        suite.Add("source health coordination: waiting daily work yields to explicit checks and rechecks due state", () =>
        {
            using var f = new Fixture(); using var interactive = f.Lanes.BeginInteractive();
            f.Coordinator.TickAsync().GetAwaiter().GetResult();
            WaitUntil(() => f.Registry.Snapshot()["waiting"]!.GetValue<int>() > 0);
            Check.Equal(0, f.Lanes.Snapshot.BackgroundActive); Check.Equal(0, f.Engine.Calls);
            var manual = f.Coordinator.Queue([f.Selection()], "manual");
            WaitUntil(() => f.Registry.Snapshot()["completed"]!.GetValue<int>() == 1);
            var calls = f.Engine.Calls;
            interactive.Dispose();
            WaitUntil(() => f.Registry.Snapshot()["running"]!.GetValue<int>() == 0);
            Check.Equal(calls, f.Engine.Calls); Check.Equal(2, f.Registry.Snapshot()["completed"]!.GetValue<int>());
        });
        suite.Add("source health coordination: disabling daily checks cancels queued work without contacting sites", () =>
        {
            using var f = new Fixture(); using var interactive = f.Lanes.BeginInteractive();
            f.Coordinator.TickAsync().GetAwaiter().GetResult();
            var result = f.Coordinator.ConfigureDailyAsync(false).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check.True(!result["daily"]!["enabled"]!.GetValue<bool>()); Check.Equal(0, f.Engine.Calls);
            Check.Equal(0, result["active"]!.AsObject().Count);
        });
    }

    internal static void WaitUntil(Func<bool> check)
    {
        var timer = Stopwatch.StartNew();
        while (!check()) { if (timer.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Controlled health job did not finish"); Thread.Sleep(5); }
    }
    internal sealed class Fixture : IDisposable
    {
        private readonly Sandbox _temp = new();
        public SqliteDatabase Database { get; }
        public BoundedWorkScheduler Io { get; } = new(2, 24);
        public TaskRegistry Registry { get; } = new();
        public WholeTaskCoordinator Lanes { get; }
        public TestSourceEngine Engine { get; } = new();
        public SuwayomiClient Client { get; }
        public SourceHealthCoordinator Coordinator { get; }
        public Fixture()
        {
            Database = new(new AutoSourcarrSettings { DataDir = _temp.Path }); new SchemaMigrator(Database).Initialize();
            Database.Write(DatabaseFile.Catalog, session => session.Execute("""
                INSERT INTO source_config(source_id,name,lang,base_url,last_seen) VALUES('-1','Sample','en','https://sample.test','2026-10-19')
                """));
            Lanes = new(new AutoSourcarrSettings { BackgroundIdleSeconds = .01 }, Registry); Client = Engine.Client();
            Coordinator = new(new SourceHealthStore(Database, clock: () => 100000), Client, Io, Lanes, Registry,
                new Repairs(), () => true, new SupportPrivacy(), () => 100000, TimeSpan.Zero);
        }
        public JsonObject Selection() => Coordinator.SnapshotAsync().GetAwaiter().GetResult()["items"]![0]!.AsObject();
        public void Dispose()
        {
            Coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult(); Client.Dispose(); Lanes.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Io.DisposeAsync().AsTask().GetAwaiter().GetResult(); Database.Dispose(); _temp.Dispose();
        }
    }
    private sealed class Repairs : IRepairHealthObservations
    {
        public Task<(JsonArray Jobs, JsonObject Operation, JsonObject Histories)> ReadAsync(CancellationToken cancellationToken) => Task.FromResult((
            new JsonArray(new JsonObject { ["id"] = new string('a', 32), ["state"] = "installed", ["pkg_name"] = "example", ["expected_ids"] = new JsonArray("-1"), ["updated_at"] = 99990.0 }),
            new JsonObject(), new JsonObject { ["example"] = new JsonObject { ["working_at"] = 90001.0,
                ["current"] = new JsonObject { ["status"] = "working", ["source_ids"] = new JsonArray("-1") } } }));
    }
}
