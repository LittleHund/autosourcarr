using System.Runtime.ExceptionServices;
using AutoSourcarr.Core.Concurrency;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Core.Persistence;

/// <summary>A synchronous, owned unit of work. Never retain it across an await.</summary>
public sealed class DatabaseSession
{
    private readonly SqliteConnection _connection;
    private readonly int _ownerThread;
    private readonly int _commandTimeout;
    private SqliteTransaction? _transaction;
    private int _savepoint;
    private bool _closed;
    internal SynchronousReturnBoundary Returns { get; }
    internal Exception? Violation => Returns.Failure;

    internal DatabaseSession(SqliteConnection connection, bool write, int commandTimeout, SynchronousReturnBoundary returns)
    {
        _connection = connection;
        _ownerThread = Environment.CurrentManagedThreadId;
        _commandTimeout = commandTimeout;
        Returns = returns;
        CanWrite = write;
        if (write)
            _transaction = connection.BeginTransaction(deferred: false);
    }

    /// <summary>False inside SqliteDatabase.Read: such a session only reads, so a snapshot can never write by accident.</summary>
    public bool CanWrite { get; private set; }

    /// <summary>
    /// A Write nested inside a Read of the same file may write for the length of that nested call only.
    /// When the read has no transaction yet, an IMMEDIATE one is started so the write lock is taken up front
    /// (a deferred read-to-write upgrade under WAL can fail at once with SQLITE_BUSY_SNAPSHOT).
    /// </summary>
    internal T WithWrites<T>(Func<DatabaseSession, T> operation)
    {
        CheckUsable();
        _transaction ??= _connection.BeginTransaction(deferred: false);
        CanWrite = true;
        try { return Savepoint(operation); }
        finally { CanWrite = false; }
    }

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        RequireWrite();
        _transaction ??= _connection.BeginTransaction(deferred: true);
        return _connection.Execute(sql, Parameters(parameters), _transaction, _commandTimeout);
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        CheckUsable();
        var value = _connection.ExecuteScalar(sql, Parameters(parameters), _transaction, _commandTimeout);
        return value is DBNull ? null : value;
    }

    /// <summary>Rows as name → value maps (Dapper does the reading; first column wins on duplicate names).</summary>
    public IReadOnlyList<DatabaseRow> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        CheckUsable();
        var rows = new List<DatabaseRow>();
        foreach (var row in _connection.Query(sql, Parameters(parameters), _transaction, buffered: true, _commandTimeout))
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in (IDictionary<string, object?>)row)
                values.TryAdd(name, value is DBNull ? null : value);
            rows.Add(new(values));
        }
        return rows;
    }

    /// <summary>Typed rows mapped by Dapper (column name = property or constructor parameter name). Use @name parameters.</summary>
    public IReadOnlyList<T> QueryAs<T>(string sql, object? parameters = null)
    {
        CheckUsable();
        return _connection.Query<T>(sql, parameters, _transaction, buffered: true, _commandTimeout).AsList();
    }

    public T? FirstOrDefaultAs<T>(string sql, object? parameters = null)
    {
        CheckUsable();
        return _connection.QueryFirstOrDefault<T>(sql, parameters, _transaction, _commandTimeout);
    }

    /// <summary>
    /// Page-first read (PERF-5): only the rows shown are read, plus one to know whether more exist. The SQL must
    /// have a deterministic ORDER BY on indexed columns and no LIMIT; use the last row's key for the next page.
    /// </summary>
    public DatabasePage<T> QueryPage<T>(string sql, object? parameters, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        if (sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("QueryPage adds the LIMIT itself.", nameof(sql));
        var rows = QueryAs<T>(sql + " LIMIT " + (limit + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), parameters);
        return new(rows.Take(limit).ToArray(), rows.Count > limit);
    }

    public int ExecuteWith(string sql, object parameters)
    {
        RequireWrite();
        _transaction ??= _connection.BeginTransaction(deferred: true);
        return _connection.Execute(sql, parameters, _transaction, _commandTimeout);
    }

    private void RequireWrite()
    {
        CheckUsable();
        if (!CanWrite)
            throw new InvalidOperationException("A read session cannot change data; use SqliteDatabase.Write for this work.");
    }

    private static DynamicParameters? Parameters((string Name, object? Value)[] parameters)
    {
        if (parameters.Length == 0)
            return null;
        var dynamic = new DynamicParameters();
        foreach (var (name, value) in parameters)
            dynamic.Add(name, value);
        return dynamic;
    }

    /// <summary>Reads one TEXT/BLOB cell without materializing it in Query's row objects.</summary>
    /// <remarks>
    /// The callback and stream are synchronous and confined to this session's owning thread.
    /// Updating any column of the selected row invalidates SQLite's blob handle; finish this
    /// callback before changing its journal row. The handle closes before this method returns.
    /// </remarks>
    public T WithReadBlob<T>(string table, string column, long rowid, Func<Stream, T> operation)
    {
        CheckUsable();
        CheckSynchronousResult<T>();
        ArgumentNullException.ThrowIfNull(operation);
        SqliteBlob? blob = null; ScopedBlobStream? view = null;
        T result = default!; Exception? failure = null;
        try
        {
            blob = new SqliteBlob(_connection, table, column, rowid, readOnly: true);
            view = new ScopedBlobStream(blob, CheckOwner);
            result = operation(view);
            CheckReturnedResult(result);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (view is not null) AttemptCleanup(ref failure, view.Dispose);
            if (blob is not null) AttemptCleanup(ref failure, blob.Dispose);
        }
        Returns.RememberFailure(failure);
        ThrowFailure(Returns.Failure ?? failure);
        return result;
    }

    public T Savepoint<T>(Func<DatabaseSession, T> operation)
    {
        CheckSynchronousResult<T>();
        CheckUsable();
        _transaction ??= _connection.BeginTransaction(deferred: true);
        var name = "autosourcarr_" + ++_savepoint;
        _transaction.Save(name);
        try
        {
            var result = operation(this);
            CheckReturnedResult(result);
            _transaction.Release(name);
            return result;
        }
        catch (Exception error)
        {
            Exception? failure = error;
            AttemptCleanup(ref failure, () => _transaction.Rollback(name));
            AttemptCleanup(ref failure, () => _transaction.Release(name));
            Returns.RememberFailure(failure);
            ThrowFailure(Returns.Failure ?? failure);
            throw; // ThrowFailure preserves the original stack and never clears failure.
        }
    }

    internal void Finish(bool success)
    {
        CheckOwner();
        Exception? failure = success ? Violation : null;
        try
        {
            if (_transaction is not null)
            {
                AttemptCleanup(ref failure, () =>
                {
                    if (success && Violation is null) _transaction.Commit();
                    else _transaction.Rollback();
                });
                AttemptCleanup(ref failure, _transaction.Dispose);
            }
        }
        finally
        {
            _closed = true;
        }
        ThrowFailure(failure);
    }

    internal void CheckReturnedResult<T>(T result)
    {
        CheckOwner();
        // A callback already running can return further physical work after
        // catching an inner violation. Capture that return before rejecting it.
        Returns.Observe(result);
    }

    internal static Exception Retain(Exception? primary, Exception next) => primary is null ? next
        : ReferenceEquals(primary, next) ? primary
        : new AggregateException("Database operation and cleanup failures were retained.", primary, next);

    internal static void AttemptCleanup(ref Exception? failure, Action cleanup)
    {
        try { cleanup(); } catch (Exception error) { failure = Retain(failure, error); }
    }

    internal static void ThrowFailure(Exception? failure)
    {
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static void CheckSynchronousResult<T>()
    {
        var type = typeof(T);
        if (typeof(Task).IsAssignableFrom(type) || type == typeof(ValueTask)
            || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            throw new InvalidOperationException("Database work must finish before its lease is released; asynchronous callbacks are not supported.");
    }

    private void CheckOwner()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("A database session cannot be shared with another worker thread.");
    }

    private void CheckUsable()
    {
        CheckOwner();
        ThrowFailure(Violation);
    }
}

/// <summary>Retains observed asynchronous returns across nested sessions of one synchronous database call.</summary>
internal sealed class SynchronousReturnBoundary
{
    private HashSet<object>? _captured;
    internal Exception? Failure { get; private set; }

    internal void RememberFailure(Exception? failure)
    {
        // Ordinary caught savepoint failures remain recoverable. Once poisoned,
        // retain the actual unwind failures even if an outer callback catches them.
        if (Failure is null || failure is null || Contains(Failure, failure)) return;
        Failure = Contains(failure, Failure) ? failure : DatabaseSession.Retain(Failure, failure);
    }

    internal void Observe<T>(T result)
    {
        object? actual = result;
        // An object-typed ValueTask may be returned through more than one
        // boundary. Consume the same box only once, including source-backed VTs.
        if (actual is not null && (_captured is null || !_captured.Contains(actual))
            && SynchronousCallbackViolationException.Capture(actual) is { } violation)
        {
            (_captured ??= new(ReferenceEqualityComparer.Instance)).Add(actual);
            Failure = DatabaseSession.Retain(Failure, violation);
        }
        DatabaseSession.ThrowFailure(Failure);
    }

    private static bool Contains(Exception error, Exception expected) => ReferenceEquals(error, expected)
        || error is AggregateException aggregate && aggregate.InnerExceptions.Any(inner => Contains(inner, expected));
}

/// <summary>One page of a page-first query and whether another page follows.</summary>
public sealed record DatabasePage<T>(IReadOnlyList<T> Items, bool HasMore);
