using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Tests;

public static class TaskRegistryTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("task registry: queued wait time does not distort active work estimates", () =>
        {
            var now = DateTimeOffset.Parse("2026-10-19T00:00:00+00:00");
            var registry = new TaskRegistry(() => now);
            var id = registry.Create("health", 6, activity: "queued", origin: "manual");
            now += TimeSpan.FromSeconds(30); registry.Update(id, current: 1);
            var row = registry.Snapshot()["items"]![0]!;
            Check.Equal(30L, row["waiting_seconds"]!.GetValue<long>());
            Check.True(row["eta_seconds"] is null);
            registry.MarkActive(id); now += TimeSpan.FromSeconds(2);
            row = registry.Snapshot()["items"]![0]!;
            Check.Equal(2L, row["elapsed_seconds"]!.GetValue<long>());
            Check.Equal(10L, row["eta_seconds"]!.GetValue<long>());
            registry.Complete(id);
            Check.Equal(6, registry.Snapshot()["items"]![0]!["current"]!.GetValue<int>());
        });
        suite.Add("task registry: cancel visibility is immediate while runtime ownership drains", () =>
        {
            var registry = new TaskRegistry();
            var id = registry.Create("owned request");
            using var cancel = new CancellationTokenSource();
            var physical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = registry.BindRuntimeAsync(id, physical.Task, cancel);
            Check.True(registry.RequestCancelAsync(id).GetAwaiter().GetResult());
            Check.True(cancel.IsCancellationRequested);
            Check.True(!observer.IsCompleted);
            var snapshot = registry.Snapshot();
            Check.Equal(0, snapshot["running"]!.GetValue<int>()); Check.Equal(1, snapshot["failed"]!.GetValue<int>());
            Check.True(snapshot["items"]![0]!["cancel_requested"]!.GetValue<bool>());
            physical.TrySetResult(); observer.GetAwaiter().GetResult();
            Check.True(!registry.RequestCancelAsync(id).GetAwaiter().GetResult());
        });
        suite.Add("task registry: retention and clear preserve running work", () =>
        {
            var now = DateTimeOffset.Parse("2026-10-19T00:00:00+00:00");
            var registry = new TaskRegistry(() => now);
            var active = registry.Create("preserve");
            for (var i = 0; i < 305; i++) { now += TimeSpan.FromSeconds(1); registry.Complete(registry.Create("done")); }
            Check.Equal(300, registry.History()["total"]!.GetValue<int>());
            now += TimeSpan.FromDays(8);
            Check.Equal(0, registry.History()["total"]!.GetValue<int>());
            registry.Complete(registry.Create("new done"));
            Check.Equal(1, registry.ClearHistory()["cleared"]!.GetValue<int>());
            Check.True(registry.IsRunning(active));
        });
        suite.Add("task journal: restart retains finished history and marks unfinished work interrupted", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            var io = new BoundedWorkScheduler(2, 8);
            var privacy = new SupportPrivacy(); privacy.Register(new Dictionary<string, string?> { ["password"] = "sentinel-journal-secret" });
            try
            {
                var registry = new TaskRegistry();
                var journal = new TaskJournal(database, registry, io, privacy);
                try
                {
                    journal.StartAsync().GetAwaiter().GetResult();
                    registry.Create("unfinished", message: "sentinel-journal-secret");
                    registry.Complete(registry.Create("finished")); journal.FlushAsync().GetAwaiter().GetResult();
                    Check.True(journal.Available);
                }
                finally { journal.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                var raw = database.Read(DatabaseFile.Catalog, session => string.Concat(session.Query("SELECT record_json FROM general_task_history").Select(row => row.Text("record_json"))));
                Check.True(!raw.Contains("sentinel-journal-secret", StringComparison.Ordinal));
                var restored = new TaskRegistry(); var restarted = new TaskJournal(database, restored, io, privacy);
                try
                {
                    restarted.StartAsync().GetAwaiter().GetResult();
                    var snapshot = restored.Snapshot();
                    Check.True(snapshot["durable"]!.GetValue<bool>());
                    Check.Equal(0, snapshot["running"]!.GetValue<int>());
                    Check.Equal(1, snapshot["failed"]!.GetValue<int>()); Check.Equal(1, snapshot["completed"]!.GetValue<int>());
                    Check.True(snapshot["items"]!.AsArray().Any(row => row!["interrupted"]!.GetValue<bool>()));
                    restored.ClearHistory(); restarted.FlushAsync().GetAwaiter().GetResult();
                    Check.Equal(0L, database.Read(DatabaseFile.Catalog, session => (long)session.Scalar("SELECT count(*) FROM general_task_history")!));
                }
                finally { restarted.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            finally { io.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("task journal: unreadable historical records are retained without overwrite", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            database.Write(DatabaseFile.Catalog, session => session.Execute("INSERT INTO general_task_history VALUES('retained','not json','2026-10-19')"));
            var io = new BoundedWorkScheduler(1, 4);
            var registry = new TaskRegistry(); var journal = new TaskJournal(database, registry, io, new SupportPrivacy());
            try
            {
                journal.StartAsync().GetAwaiter().GetResult(); Check.True(!journal.Available); Check.True(journal.Error.Length > 0);
                registry.Create("new"); Check.Throws<InvalidOperationException>(() => journal.FlushAsync().GetAwaiter().GetResult());
            }
            finally { journal.DisposeAsync().AsTask().GetAwaiter().GetResult(); io.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            Check.Equal("not json", database.Read(DatabaseFile.Catalog, session => (string)session.Scalar("SELECT record_json FROM general_task_history WHERE id='retained'")!));
        });
    }
}
