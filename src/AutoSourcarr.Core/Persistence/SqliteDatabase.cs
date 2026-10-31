using System.Globalization;
using AutoSourcarr.Core.Settings;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Core.Persistence;

/// <summary>Existing two-file SQLite storage with explicit transaction ownership.</summary>
public sealed class SqliteDatabase : IDisposable
{
    private readonly ReaderWriterLockSlim _admission = new(LockRecursionPolicy.SupportsRecursion);
    private readonly ThreadLocal<Dictionary<DatabaseFile, DatabaseSession>> _sessions = new(() => []);
    private readonly ThreadLocal<SynchronousReturnBoundary?> _returns = new();
    private readonly AutoSourcarrSettings _settings;
    private DatabasePaths _paths;
    private bool _disposed;

    public SqliteDatabase(AutoSourcarrSettings settings)
    {
        _settings = settings;
        _paths = DatabasePaths.Resolve(settings);
    }

    public DatabasePaths Paths => _paths;

    /// <summary>Tests only this physical thread's session ownership. A nested
    /// savepoint is not an outer commit and cannot acknowledge a durable cursor.</summary>
    public bool HasCurrentThreadSession(DatabaseFile file)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sessions.IsValueCreated && _sessions.Value!.ContainsKey(file);
    }

    /// <summary>Verifies an actual current session of this database and file;
    /// a gate or another instance's session alone cannot supply that authority.</summary>
    public bool IsCurrentThreadSession(DatabaseFile file, DatabaseSession session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sessions.IsValueCreated && _sessions.Value!.TryGetValue(file, out var current)
            && ReferenceEquals(current, session);
    }

    public T Read<T>(DatabaseFile file, Func<DatabaseSession, T> operation) => Run(file, operation, write: false);
    public T Write<T>(DatabaseFile file, Func<DatabaseSession, T> operation) => Run(file, operation, write: true);

    public T Quiescent<T>(Func<DatabasePaths, T> operation, TimeSpan? timeout = null)
    {
        DatabaseSession.CheckSynchronousResult<T>();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_admission.TryEnterWriteLock(timeout ?? TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Database work has not drained; storage remains unchanged.");
        var previous = _returns.Value;
        var boundary = previous ?? new SynchronousReturnBoundary();
        _returns.Value = boundary;
        T result = default!; Exception? failure = null;
        try
        {
            DatabaseSession.ThrowFailure(boundary.Failure);
            ClearPools();
            result = operation(_paths);
            boundary.Observe(result);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            DatabaseSession.AttemptCleanup(ref failure, _admission.ExitWriteLock);
            boundary.RememberFailure(failure);
            failure = boundary.Failure ?? failure;
            DatabaseSession.AttemptCleanup(ref failure, () => _returns.Value = previous);
        }
        DatabaseSession.ThrowFailure(failure);
        return result;
    }

    internal SqliteConnection Open(DatabaseFile file, bool initialize = false)
    {
        var path = file == DatabaseFile.Catalog ? _paths.Catalog : _paths.Origins;
        var timeout = Math.Max(1, (int)Math.Ceiling(Math.Max(250, _settings.SqliteBusyTimeoutMs) / 1000.0));
        var connection = new SqliteConnection(ConnectionString(path, initialize));
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandTimeout = timeout;
            command.CommandText = string.Create(CultureInfo.InvariantCulture, $"""
                PRAGMA foreign_keys=ON;
                PRAGMA busy_timeout={Math.Max(250, _settings.SqliteBusyTimeoutMs)};
                PRAGMA synchronous=NORMAL;
                PRAGMA temp_store=MEMORY;
                PRAGMA cache_size={-Math.Max(4, _settings.SqliteCacheMb) * 1024L};
                PRAGMA mmap_size=268435456;
                """);
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal void SwitchPaths(DatabasePaths paths)
    {
        if (!_admission.IsWriteLockHeld)
            throw new InvalidOperationException("Database relocation requires a drained storage lease.");
        _paths = paths;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Quiescent(_ => { _disposed = true; return 0; });
        _sessions.Dispose();
        _returns.Dispose();
        _admission.Dispose();
    }

    private T Run<T>(DatabaseFile file, Func<DatabaseSession, T> operation, bool write)
    {
        DatabaseSession.CheckSynchronousResult<T>();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _admission.EnterReadLock();
        var previous = _returns.Value;
        var boundary = previous ?? new SynchronousReturnBoundary();
        _returns.Value = boundary;
        T result = default!; Exception? failure = null;
        SqliteConnection? connection = null; DatabaseSession? session = null;
        var registered = false;
        try
        {
            DatabaseSession.ThrowFailure(boundary.Failure);
            if (_sessions.Value!.TryGetValue(file, out var nested))
            {
                if (write && !nested.CanWrite)
                    nested.AllowWrites();
                result = nested.Savepoint(operation);
            }
            else
            {
                connection = Open(file);
                session = new DatabaseSession(connection, write, Math.Max(1,
                    (int)Math.Ceiling(Math.Max(250, _settings.SqliteBusyTimeoutMs) / 1000.0)), boundary);
                _sessions.Value.Add(file, session); registered = true;
                result = operation(session);
                session.CheckReturnedResult(result);
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (session is not null)
            {
                DatabaseSession.AttemptCleanup(ref failure, () => session.Finish(success: failure is null));
                if (registered) DatabaseSession.AttemptCleanup(ref failure, () => _sessions.Value!.Remove(file));
            }
            if (connection is not null) DatabaseSession.AttemptCleanup(ref failure, connection.Dispose);
            DatabaseSession.AttemptCleanup(ref failure, _admission.ExitReadLock);
            boundary.RememberFailure(failure);
            failure = boundary.Failure ?? failure;
            DatabaseSession.AttemptCleanup(ref failure, () => _returns.Value = previous);
        }
        // This is a synchronous resource boundary, not an awaitable work owner.
        // The scheduler/caller joins a captured completion only after this unwind.
        DatabaseSession.ThrowFailure(failure);
        return result;
    }

    private void ClearPools()
    {
        foreach (var path in new[] { _paths.Catalog, _paths.Origins })
        {
            foreach (var initialize in new[] { false, true })
            {
                using var connection = new SqliteConnection(ConnectionString(path, initialize));
                SqliteConnection.ClearPool(connection);
            }
        }
    }

    private string ConnectionString(string path, bool initialize) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = initialize ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
        ForeignKeys = true,
        DefaultTimeout = Math.Max(1, (int)Math.Ceiling(Math.Max(250, _settings.SqliteBusyTimeoutMs) / 1000.0)),
        Pooling = true
    }.ToString();
}
