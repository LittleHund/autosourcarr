using System.Text.Json;
using System.Threading.Tasks.Sources;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Tests;

public static class StorageTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("storage: released populated schema, values, IDs and relations survive reopen", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            var migrator = new SchemaMigrator(database);
            foreach (var schema in new[] { "catalogue-identity.sql", "gallery-policy.sql", "identity-review.sql" })
                migrator.EnsureFeatureSchema(schema);
            Seed(database, DatabaseFile.Catalog, "catalog-seed.sql");
            Seed(database, DatabaseFile.Origins, "origins-seed.sql");
            migrator.Initialize();
            Console.WriteLine("SQLite runtime " + database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT sqlite_version()")));
            using var expected = JsonDocument.Parse(File.ReadAllText(ConfigurationTests.Fixture("storage-contract.json")));
            AssertSnapshot(database, expected.RootElement);
            migrator.Initialize();
            AssertSnapshot(database, expected.RootElement);
            Check.Equal(9007199254740993L, database.Read(DatabaseFile.Catalog, c => c.Query("SELECT id FROM entries")[0].Integer("id")));
            Check.Equal("-9223372036854775808", database.Read(DatabaseFile.Catalog, c => c.Query("SELECT source_id FROM source_config")[0].Text("source_id")));
        });
        suite.Add("storage: nested helper writes join outer rollback", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            Check.Throws<FixtureFailure>(() => database.Write<int>(DatabaseFile.Catalog, outer =>
            {
                Put(outer, "outer");
                database.Write(DatabaseFile.Catalog, inner => Put(inner, "inner"));
                throw new FixtureFailure();
            }));
            Check.Equal(0L, database.Read(DatabaseFile.Catalog, c => (long)c.Scalar("SELECT COUNT(*) FROM settings")!));
        });
        suite.Add("storage: failed inner savepoint does not discard outer work", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            database.Write(DatabaseFile.Catalog, outer =>
            {
                Put(outer, "outer");
                Check.Throws<FixtureFailure>(() => database.Write<int>(DatabaseFile.Catalog, inner =>
                {
                    Put(inner, "discard-inner");
                    throw new FixtureFailure();
                }));
                return Put(outer, "after");
            });
            Check.Equal("after,outer", database.Read(DatabaseFile.Catalog,
                c => string.Join(',', c.Query("SELECT key FROM settings ORDER BY key").Select(r => r.Text("key")))));
        });
        suite.Add("storage: nested write from plain context cannot commit before its owner", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            Check.Throws<FixtureFailure>(() => database.Read<int>(DatabaseFile.Catalog, _ =>
            {
                database.Write(DatabaseFile.Catalog, inner => Put(inner, "not-committed"));
                throw new FixtureFailure();
            }));
            Check.Equal(0L, database.Read(DatabaseFile.Catalog, c => (long)c.Scalar("SELECT COUNT(*) FROM settings")!));
        });
        suite.Add("storage: plain reads can observe another committed writer", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            database.Read(DatabaseFile.Catalog, reader =>
            {
                Check.Equal(0L, (long)reader.Scalar("SELECT COUNT(*) FROM settings")!);
                Task.Run(() => database.Write(DatabaseFile.Catalog, writer => Put(writer, "external"))).GetAwaiter().GetResult();
                Check.Equal(1L, (long)reader.Scalar("SELECT COUNT(*) FROM settings")!);
                return 0;
            });
        });
        suite.Add("storage: quiescence waits for physical work and times out safely", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var writer = Task.Run(() => database.Write(DatabaseFile.Catalog, session =>
            {
                entered.Set();
                Check.True(release.Wait(TimeSpan.FromSeconds(10)), "Fixture worker release timed out");
                return Put(session, "drained");
            }));
            try
            {
                Check.True(entered.Wait(TimeSpan.FromSeconds(5)), "Fixture worker did not start");
                Check.Throws<TimeoutException>(() => database.Quiescent(_ => 0, TimeSpan.FromMilliseconds(50)));
            }
            finally
            {
                release.Set();
                writer.GetAwaiter().GetResult();
            }
            Check.Equal(1L, database.Quiescent(_ =>
            {
                using var native = new SqliteConnection("Data Source=" + database.Paths.Catalog + ";Pooling=False");
                native.Open();
                using var command = native.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM settings";
                return (long)command.ExecuteScalar()!;
            }));
        });
        suite.Add("storage: sessions cannot escape or silently outlive their owner", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            var leaked = database.Read(DatabaseFile.Catalog, c => c);
            Check.Throws<ObjectDisposedException>(() => leaked.Scalar("SELECT 1"));
            var invoked = false;
            Check.Throws<InvalidOperationException>(() => database.Read(DatabaseFile.Catalog, _ =>
            {
                invoked = true;
                return Task.FromResult(1);
            }));
            Check.True(!invoked);
        });
        suite.Add("storage: SQLite snapshot includes committed WAL rows", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            using var anchor = new SqliteConnection("Data Source=" + database.Paths.Catalog + ";Pooling=False");
            anchor.Open();
            database.Write(DatabaseFile.Catalog, c => Put(c, "committed-wal"));
            Check.True(File.Exists(database.Paths.Catalog + "-wal"));
            var target = Path.Combine(temp.Path, "snapshots", "catalog.sqlite");
            database.Quiescent(paths => { SqliteSnapshot.CopyToNew(paths.Catalog, target); return 0; });
            using var snapshot = new SqliteConnection("Data Source=" + target + ";Mode=ReadOnly;Pooling=False");
            snapshot.Open();
            using var check = snapshot.CreateCommand();
            check.CommandText = "SELECT value FROM settings WHERE key='committed-wal'";
            Check.Equal("saved", check.ExecuteScalar() as string);
            Check.Throws<IOException>(() => SqliteSnapshot.CopyToNew(database.Paths.Catalog, target));
        });
        suite.Add("storage: hidden awaitables roll back before outside-lock handoff", HiddenReturnsRollback);
        suite.Add("storage: swallowed nested awaitable poisons its outer unit", SwallowedViolationRollsBack);
        suite.Add("storage: poisoned callback still hands off its second directly returned task", PoisonedReturnRetainsBoth);
        suite.Add("storage: swallowed cross-file awaitables reject both active sessions", CrossFileViolationRollsBack);
        suite.Add("storage: repeated boxed source-backed ValueTask is consumed once", RepeatedValueTaskReturn);
        suite.Add("storage: callback handoff survives actual rollback failure", RollbackFailureRetainsCompletion);
        suite.Add("storage: rejected DB and blob work remains owned until physical completion", RejectedWorkRemainsOwned);
        suite.Add("storage: quiescent callback rejects hidden awaitables after lock unwind", QuiescentRejectsHiddenReturn);
        suite.Add("storage: quiescent nesting retains swallowed asynchronous returns", NestedQuiescentRetainsReturn);
        suite.Add("storage: swallowed poisoned savepoint retains actual cleanup errors", SwallowedCleanupRetainsErrors);
    }

    private static readonly string[] AwaitableForms = ["Task", "ValueTask", "ValueTask<int>"];
    private static object Hidden(string form, Task<int> work) => form switch
    {
        "Task" => work, "ValueTask" => new ValueTask(work), "ValueTask<int>" => new ValueTask<int>(work),
        _ => throw new ArgumentOutOfRangeException(nameof(form))
    };
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<int> Pending(Task gate) { await gate.ConfigureAwait(false); return 1; }
    private static Exception? Observe(Task task)
    {
        try { task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); return null; }
        catch (Exception error) { return error; }
    }
    private static IEnumerable<Exception> Failures(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions)
                foreach (var nested in Failures(inner)) yield return nested;
        else if (error.InnerException is not null)
            foreach (var nested in Failures(error.InnerException)) yield return nested;
    }
    private static long Count(SqliteDatabase database) => database.Read(DatabaseFile.Catalog,
        session => (long)session.Scalar("SELECT COUNT(*) FROM settings")!);

    private static void HiddenReturnsRollback()
    {
        foreach (var form in AwaitableForms)
        {
            using var temp = new Sandbox(); using var database = NewDatabase(temp.Path);
            var release = Gate(); Task<int>? physical = null; DatabaseSession? leaked = null;
            try
            {
                var error = Check.Throws<SynchronousCallbackViolationException>(() => database.Write<object>(DatabaseFile.Catalog, session =>
                {
                    leaked = session; Put(session, "must-not-commit"); physical = Pending(release.Task);
                    return Hidden(form, physical);
                }));
                Check.True(ReferenceEquals(error.Completion, physical));
                Check.True(!error.Completion.IsCompleted);
                Check.Equal(0L, Count(database));
                Check.True(!database.HasCurrentThreadSession(DatabaseFile.Catalog));
                Check.Equal(7, database.Quiescent(_ => 7));
                Check.Throws<ObjectDisposedException>(() => leaked!.Scalar("SELECT 1"));
            }
            finally { release.TrySetResult(); if (physical is not null) Check.True(Observe(physical) is null); }
        }
    }

    private static void SwallowedViolationRollsBack()
    {
        using var temp = new Sandbox(); using var database = NewDatabase(temp.Path);
        var release = Gate(); Task<int>? physical = null; SynchronousCallbackViolationException? swallowed = null;
        var invokedAgain = false;
        try
        {
            var outerError = Check.Throws<SynchronousCallbackViolationException>(() => database.Write(DatabaseFile.Catalog, outer =>
            {
                Put(outer, "outer-must-roll-back");
                swallowed = Check.Throws<SynchronousCallbackViolationException>(() => database.Write<object>(DatabaseFile.Catalog, inner =>
                {
                    Put(inner, "inner-must-roll-back"); physical = Pending(release.Task); return new ValueTask<int>(physical);
                }));
                Check.Throws<SynchronousCallbackViolationException>(() => outer.Savepoint(_ => { invokedAgain = true; return 0; }));
                return 1; // Catching the nested guard cannot turn this unit into a commit.
            }));
            Check.True(ReferenceEquals(swallowed, outerError));
            Check.True(!invokedAgain && !outerError.Completion.IsCompleted);
            Check.Equal(0L, Count(database));
            Check.Equal(8, database.Quiescent(_ => 8));
        }
        finally { release.TrySetResult(); if (physical is not null) Check.True(Observe(physical) is null); }
    }

    private static void RollbackFailureRetainsCompletion()
    {
        using var temp = new Sandbox(); using var database = NewDatabase(temp.Path);
        var release = Gate(); Task<int>? physical = null;
        try
        {
            var error = Check.Throws<AggregateException>(() => database.Write<object>(DatabaseFile.Catalog, outer => outer.Savepoint<object>(session =>
            {
                Put(session, "already-rolled-back");
                // External ROLLBACK removes the real savepoint. The provider
                // tolerates a later whole rollback, but rollback/release of this
                // now-missing savepoint must retain their real SQLite failures.
                session.Execute("ROLLBACK");
                physical = Pending(release.Task); return physical;
            })));
            var graph = Failures(error).ToArray();
            Check.True(graph.OfType<SynchronousCallbackViolationException>().Any(item => ReferenceEquals(item.Completion, physical)));
            var providerFailures = graph.OfType<InvalidOperationException>()
                .Where(item => item is not SynchronousCallbackViolationException).ToArray();
            Check.True(providerFailures.Length >= 2, error.ToString());
            Check.Equal(0L, Count(database));
            Check.Equal(9, database.Quiescent(_ => 9));
        }
        finally { release.TrySetResult(); if (physical is not null) Check.True(Observe(physical) is null); }
    }

    private static void PoisonedReturnRetainsBoth()
    {
        foreach (var form in AwaitableForms)
        {
            using var temp = new Sandbox(); using var database = NewDatabase(temp.Path);
            var scheduler = new BoundedWorkScheduler(1, 0);
            var firstRelease = Gate(); var secondRelease = Gate(); var unwound = Gate();
            Task<int>? first = null, second = null;
            var operation = scheduler.RunAsync<object>(() =>
            {
                try
                {
                    return database.Write<object>(DatabaseFile.Catalog, outer =>
                    {
                        Put(outer, "must-roll-back");
                        Check.Throws<SynchronousCallbackViolationException>(() => outer.Savepoint<object>(_ =>
                        { first = Pending(firstRelease.Task); return first; }));
                        // No further session API is called. The callback already
                        // running can directly return another physical operation.
                        second = Pending(secondRelease.Task); return Hidden(form, second);
                    });
                }
                finally { unwound.TrySetResult(); }
            });
            Exception? observed;
            try
            {
                unwound.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Check.Equal(0L, Count(database));
                firstRelease.TrySetResult();
                first!.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            finally
            {
                firstRelease.TrySetResult(); secondRelease.TrySetResult();
                observed = Observe(operation);
                if (first is not null) Check.True(Observe(first) is null);
                if (second is not null) Check.True(Observe(second) is null);
                scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            Check.True(observed is not null);
            var completions = Failures(observed!).OfType<SynchronousCallbackViolationException>().Select(item => item.Completion).ToArray();
            Check.True(completions.Any(item => ReferenceEquals(item, first)));
            Check.True(completions.Any(item => ReferenceEquals(item, second)), "Second directly returned physical task was abandoned");
        }
    }

    private static void RejectedWorkRemainsOwned()
    {
        foreach (var form in AwaitableForms) foreach (var scope in new[] { "outer", "savepoint", "blob" })
        {
            using var temp = new Sandbox(); using var database = NewDatabase(temp.Path);
            database.Write(DatabaseFile.Catalog, session => session.Execute("CREATE TABLE hidden_blob(id INTEGER PRIMARY KEY,data BLOB); INSERT INTO hidden_blob VALUES(1,x'010203')"));
            var scheduler = new BoundedWorkScheduler(1, 0); using var cancel = new CancellationTokenSource();
            var release = Gate(); var unwound = Gate(); Task<int>? physical = null; Exception? late = null;
            async Task<int> Work(Stream? stream)
            {
                await release.Task.ConfigureAwait(false);
                try
                {
                    if (stream is not null) stream.ReadByte();
                    throw new IOException("synthetic late physical work failure");
                }
                catch (Exception error) { late = error; throw; }
            }
            var operation = scheduler.RunAsync<object>(() =>
            {
                object Return(DatabaseSession session)
                {
                    Put(session, "must-not-commit"); physical = Work(null); return Hidden(form, physical);
                }
                try
                {
                    return database.Write<object>(DatabaseFile.Catalog, session => scope switch
                    {
                        "outer" => Return(session),
                        "savepoint" => session.Savepoint<object>(Return),
                        _ => session.WithReadBlob<object>("hidden_blob", "data", 1, stream =>
                        { physical = Work(stream); return Hidden(form, physical); })
                    });
                }
                finally { unwound.TrySetResult(); }
            }, cancel.Token);
            Task? stop = null; Exception? observed;
            try
            {
                unwound.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Check.Equal(0L, Count(database));
                Check.Equal(10, database.Quiescent(_ => 10)); // All DB locks must be gone before joining.
                Check.Equal((1, 1), scheduler.Snapshot);
                Check.True(!operation.IsCompleted && physical is { IsCompleted: false });
                Check.True(Observe(scheduler.RunAsync(() => 0)) is WorkDeferredException);
                cancel.Cancel(); stop = scheduler.StopAsync();
                Check.True(!stop.IsCompleted);
            }
            finally
            {
                release.TrySetResult(); observed = Observe(operation);
                if (physical is not null) _ = Observe(physical);
                if (stop is not null) Check.True(Observe(stop) is null);
                scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            Check.True(observed is not null && late is not null);
            var graph = Failures(observed!).ToArray();
            Check.True(graph.OfType<SynchronousCallbackViolationException>().Any());
            Check.True(graph.Any(item => ReferenceEquals(item, late)), "The actual late failure was lost");
            Check.True(scope != "blob" || late is ObjectDisposedException, "Borrowed blob must close before late use");
            Check.Equal(0L, Count(database));
        }
    }

    private static void CrossFileViolationRollsBack()
    {
        foreach (var outerFile in new[] { DatabaseFile.Catalog, DatabaseFile.Origins })
        {
            var innerFile = outerFile == DatabaseFile.Catalog ? DatabaseFile.Origins : DatabaseFile.Catalog;
            using var temp = new Sandbox(); using var database = NewDatabase(temp.Path);
            foreach (var file in new[] { outerFile, innerFile })
                database.Write(file, session => session.Execute("CREATE TABLE hidden_boundary(value INTEGER)"));
            var release = Gate(); Task<int>? physical = null; SynchronousCallbackViolationException? innerError = null;
            var calledAgain = false;
            try
            {
                var error = Check.Throws<SynchronousCallbackViolationException>(() => database.Write(outerFile, outer =>
                {
                    outer.Execute("INSERT INTO hidden_boundary VALUES(1)");
                    innerError = Check.Throws<SynchronousCallbackViolationException>(() => database.Write<object>(innerFile, inner =>
                    {
                        inner.Execute("INSERT INTO hidden_boundary VALUES(2)");
                        physical = Pending(release.Task); return physical;
                    }));
                    Check.Throws<SynchronousCallbackViolationException>(() => outer.Savepoint(_ => { calledAgain = true; return 0; }));
                    Check.Throws<SynchronousCallbackViolationException>(() => database.Read(innerFile, _ => { calledAgain = true; return 0; }));
                    return 3;
                }));
                Check.True(ReferenceEquals(error, innerError) && ReferenceEquals(error.Completion, physical));
                Check.True(!calledAgain && !error.Completion.IsCompleted);
                foreach (var file in new[] { outerFile, innerFile })
                    Check.Equal(0L, database.Read(file, session => (long)session.Scalar("SELECT COUNT(*) FROM hidden_boundary")!));
                Check.Equal(12, database.Quiescent(_ => 12));
            }
            finally { release.TrySetResult(); if (physical is not null) Check.True(Observe(physical) is null); }
        }
    }

    private static void RepeatedValueTaskReturn()
    {
        using var temp = new Sandbox(); using var database = NewDatabase(temp.Path);
        var source = new SingleResultSource(); object boxed = source.Value;
        SynchronousCallbackViolationException? innerError = null, outerError = null;
        try
        {
            outerError = Check.Throws<SynchronousCallbackViolationException>(() => database.Write<object>(DatabaseFile.Catalog, outer =>
            {
                Put(outer, "must-roll-back");
                innerError = Check.Throws<SynchronousCallbackViolationException>(() => outer.Savepoint<object>(_ => boxed));
                return boxed;
            }));
            Check.True(ReferenceEquals(innerError, outerError));
            Check.True(!outerError.Completion.IsCompleted);
            Check.Equal(0L, Count(database));
        }
        finally
        {
            source.Complete();
            if (outerError is not null) Check.True(Observe(outerError.Completion) is null);
            else if (innerError is not null) Check.True(Observe(innerError.Completion) is null);
        }
        Check.Equal(1, source.ResultCalls);
    }

    private sealed class SingleResultSource : IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> _source = new() { RunContinuationsAsynchronously = true };
        private int _resultCalls;
        public int ResultCalls => Volatile.Read(ref _resultCalls);
        public ValueTask<int> Value => new(this, _source.Version);
        public void Complete() => _source.SetResult(4);
        public int GetResult(short token) { Interlocked.Increment(ref _resultCalls); return _source.GetResult(token); }
        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _source.OnCompleted(continuation, state, token, flags);
    }

    private static void QuiescentRejectsHiddenReturn()
    {
        using var temp = new Sandbox(); using var database = NewDatabase(temp.Path);
        var release = Gate(); Task<int>? physical = null;
        try
        {
            var error = Check.Throws<SynchronousCallbackViolationException>(() => database.Quiescent<object>(_ =>
            { physical = Pending(release.Task); return new ValueTask<int>(physical); }));
            Check.True(ReferenceEquals(error.Completion, physical) && !error.Completion.IsCompleted);
            Check.Equal(0L, Count(database));
            Check.Equal(11, database.Quiescent(_ => 11));
        }
        finally { release.TrySetResult(); if (physical is not null) Check.True(Observe(physical) is null); }
    }

    private static void NestedQuiescentRetainsReturn()
    {
        foreach (var scope in new[] { "quiescent", "session" }) foreach (var form in AwaitableForms)
        {
            using var temp = new Sandbox(); using var database = NewDatabase(temp.Path);
            var release = Gate(); Task<int>? physical = null; SynchronousCallbackViolationException? innerError = null;
            var calledAgain = false;
            try
            {
                var error = Check.Throws<SynchronousCallbackViolationException>(() => database.Quiescent(_ =>
                {
                    object Begin() { physical = Pending(release.Task); return Hidden(form, physical); }
                    innerError = Check.Throws<SynchronousCallbackViolationException>(() =>
                    {
                        if (scope == "quiescent") database.Quiescent<object>(_ => Begin());
                        else database.Write<object>(DatabaseFile.Catalog, session => { Put(session, "must-roll-back"); return Begin(); });
                    });
                    Check.Throws<SynchronousCallbackViolationException>(() => database.Quiescent(_ => { calledAgain = true; return 0; }));
                    return 0;
                }));
                Check.True(ReferenceEquals(error, innerError) && ReferenceEquals(error.Completion, physical));
                Check.True(!calledAgain && !error.Completion.IsCompleted);
                Check.Equal(0L, Count(database));
                Check.Equal(13, database.Quiescent(_ => 13));
            }
            finally { release.TrySetResult(); if (physical is not null) Check.True(Observe(physical) is null); }
        }
    }

    private static void SwallowedCleanupRetainsErrors()
    {
        using var temp = new Sandbox(); using var database = NewDatabase(temp.Path);
        var release = Gate(); Task<int>? physical = null; AggregateException? caught = null;
        try
        {
            var error = Check.Throws<AggregateException>(() => database.Write(DatabaseFile.Catalog, outer =>
            {
                caught = Check.Throws<AggregateException>(() => outer.Savepoint<object>(session =>
                {
                    Put(session, "already-rolled-back"); session.Execute("ROLLBACK");
                    physical = Pending(release.Task); return physical;
                }));
                return 0;
            }));
            Check.True(Failures(error).Any(item => ReferenceEquals(item, caught)));
            var graph = Failures(error).ToArray();
            Check.True(graph.OfType<SynchronousCallbackViolationException>().Any(item => ReferenceEquals(item.Completion, physical)));
            Check.True(graph.OfType<InvalidOperationException>().Count(item => item is not SynchronousCallbackViolationException) >= 2);
            Check.Equal(0L, Count(database));
            Check.Equal(14, database.Quiescent(_ => 14));
        }
        finally { release.TrySetResult(); if (physical is not null) Check.True(Observe(physical) is null); }
    }

    private static SqliteDatabase NewDatabase(string root)
    {
        var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = root });
        new SchemaMigrator(database).Initialize();
        return database;
    }

    private static int Put(DatabaseSession session, string key) => session.Execute(
        "INSERT INTO settings(key,value) VALUES($key,'saved')", ("$key", key));
    private static void Seed(SqliteDatabase database, DatabaseFile file, string fixture) =>
        database.Write(file, session => session.Execute(File.ReadAllText(ConfigurationTests.Fixture(fixture))));

    private static void AssertSnapshot(SqliteDatabase database, JsonElement expected)
    {
        foreach (var (name, file) in new[] { ("catalog", DatabaseFile.Catalog), ("origins", DatabaseFile.Origins) })
        {
            database.Read(file, session =>
            {
                var contract = expected.GetProperty(name);
                var tables = session.Query("SELECT name FROM sqlite_master WHERE type='table' AND name<>'VersionInfo' ORDER BY name").Select(r => r.Text("name")).ToArray();
                Check.Equal(contract.GetProperty("columns").EnumerateObject().Count(), tables.Length);
                foreach (var table in tables)
                {
                    Check.JsonEqual(contract.GetProperty("columns").GetProperty(table), JsonSerializer.SerializeToElement(session.Query($"PRAGMA table_info(\"{table}\")")), name + ".columns." + table);
                    // PRAGMA optimize owns these derived estimates; row order and
                    // values can vary by SQLite build without changing app data.
                    if (table is not ("sqlite_stat1" or "sqlite_stat4"))
                        Check.JsonEqual(contract.GetProperty("rows").GetProperty(table), JsonSerializer.SerializeToElement(session.Query($"SELECT * FROM \"{table}\" ORDER BY rowid")), name + ".rows." + table);
                    Check.JsonEqual(contract.GetProperty("foreignKeys").GetProperty(table), JsonSerializer.SerializeToElement(session.Query($"PRAGMA foreign_key_list(\"{table}\")")), name + ".foreignKeys." + table);
                }
                Check.JsonEqual(contract.GetProperty("indexes"), JsonSerializer.SerializeToElement(session.Query("SELECT name,tbl_name FROM sqlite_master WHERE type='index' AND tbl_name<>'VersionInfo' ORDER BY name")));
                Check.Equal("ok", session.Scalar("PRAGMA integrity_check") as string);
                Check.Equal(0, session.Query("PRAGMA foreign_key_check").Count);
                return 0;
            });
        }
    }

    private sealed class FixtureFailure : Exception;
}
