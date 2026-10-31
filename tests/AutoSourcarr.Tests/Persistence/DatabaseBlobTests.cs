using System.Security.Cryptography;
using System.Text;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Tests;

public static class DatabaseBlobTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("storage blobs: TEXT and BLOB stream exact UTF8 bytes with Int64 row IDs", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            var text = string.Concat(Enumerable.Repeat("日本語 😀 e\u0301\n", 180000));
            var bytes = Encoding.UTF8.GetBytes(text);
            Seed(database, text, bytes);
            database.Read(DatabaseFile.Catalog, session =>
            {
                foreach (var column in new[] { "document", "binary" })
                    session.WithReadBlob("stream_documents", column, 9007199254740993L, stream =>
                    {
                        Check.Equal((long)bytes.Length, stream.Length);
                        Check.True(stream.CanRead && stream.CanSeek && !stream.CanWrite);
                        Check.True(SHA256.HashData(stream).SequenceEqual(SHA256.HashData(bytes)));
                        Check.Equal(stream.Length, stream.Position);
                        stream.Seek(-37, SeekOrigin.End);
                        Span<byte> tail = stackalloc byte[37];
                        stream.ReadExactly(tail);
                        Check.True(tail.SequenceEqual(bytes.AsSpan(bytes.Length - 37)));
                        stream.Position = 0;
                        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 97, leaveOpen: true);
                        Check.Equal(text, reader.ReadToEnd());
                        return 0;
                    });
                return 0;
            });
        });
        suite.Add("storage blobs: read-only identifiers are not SQL and writes never change bytes", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            database.Write(DatabaseFile.Catalog, session => session.Execute(
                "CREATE TABLE \"not; DROP TABLE settings\"(payload BLOB); INSERT INTO \"not; DROP TABLE settings\" VALUES(x'010203')"));
            database.Read(DatabaseFile.Catalog, session => session.WithReadBlob("not; DROP TABLE settings", "payload", 1, stream =>
            {
                Check.Throws<NotSupportedException>(() => stream.WriteByte(9));
                Check.Throws<NotSupportedException>(() => stream.SetLength(1));
                Check.Throws<NotSupportedException>(() => stream.Write(new byte[] { 9 }));
                Check.Equal(1, stream.ReadByte());
                Check.Equal(3L, stream.Length);
                return 0;
            }));
            Check.Equal(1L, database.Read(DatabaseFile.Catalog, session => (long)session.Scalar(
                "SELECT count(*) FROM sqlite_master WHERE name='settings'")!));
        });
        suite.Add("storage blobs: escaped stream and callback failure close the actual handle", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            Seed(database, "original", [1, 2, 3]);
            Stream? leaked = null;
            database.Read(DatabaseFile.Catalog, session => session.WithReadBlob("stream_documents", "document", 9007199254740993L,
                stream => { leaked = stream; return 0; }));
            Check.Throws<ObjectDisposedException>(() => leaked!.ReadByte());
            Check.Throws<FixtureFailure>(() => database.Write<int>(DatabaseFile.Catalog, session =>
                session.WithReadBlob<int>("stream_documents", "document", 9007199254740993L, stream =>
                {
                    leaked = stream;
                    session.Execute("INSERT INTO settings(key,value) VALUES('blob-rollback','discard')");
                    throw new FixtureFailure();
                })));
            Check.Throws<ObjectDisposedException>(() => leaked!.ReadByte());
            Check.Equal(0L, database.Read(DatabaseFile.Catalog, session => (long)session.Scalar(
                "SELECT count(*) FROM settings WHERE key='blob-rollback'")!));
            database.Write(DatabaseFile.Catalog, session => session.Execute("UPDATE stream_documents SET document='reopened'"));
            Check.Equal("reopened", database.Read(DatabaseFile.Catalog, session => session.WithReadBlob(
                "stream_documents", "document", 9007199254740993L, ReadText)));
        });
        suite.Add("storage blobs: async callbacks and boxed awaitables cannot escape a lease", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            Seed(database, "value", [4]);
            database.Read(DatabaseFile.Catalog, session =>
            {
                var invoked = false;
                Check.Throws<InvalidOperationException>(() => session.WithReadBlob("stream_documents", "document", 9007199254740993L,
                    _ => { invoked = true; return Task.FromResult(1); }));
                Check.True(!invoked);
                Check.Throws<InvalidOperationException>(() => session.WithReadBlob("stream_documents", "document", 9007199254740993L,
                    _ => { invoked = true; return ValueTask.FromResult(1); }));
                Check.True(!invoked);
                return 0;
            });
            foreach (var boxed in new object[] { Task.FromResult(1), new ValueTask(), ValueTask.FromResult(1) })
            {
                Stream? leaked = null; var invoked = false;
                var violation = Check.Throws<SynchronousCallbackViolationException>(() => database.Read(DatabaseFile.Catalog, session =>
                {
                    Check.Throws<SynchronousCallbackViolationException>(() => session.WithReadBlob<object>("stream_documents", "document", 9007199254740993L,
                        stream => { invoked = true; leaked = stream; return boxed; }));
                    Check.Throws<ObjectDisposedException>(() => leaked!.ReadByte());
                    return 0; // A swallowed runtime violation must still reject the outer unit.
                }));
                violation.Completion.GetAwaiter().GetResult(); // Outside the fully unwound DB/blob lease.
                Check.True(invoked);
                Check.Throws<ObjectDisposedException>(() => leaked!.ReadByte());
            }
        });
        suite.Add("storage blobs: cross-thread and asynchronous stream IO are refused", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            Seed(database, "value", [4]);
            database.Read(DatabaseFile.Catalog, session => session.WithReadBlob("stream_documents", "document", 9007199254740993L, stream =>
            {
                Exception? threadFailure = null;
                var thread = new Thread(() =>
                {
                    try { stream.ReadByte(); }
                    catch (Exception error) { threadFailure = error; }
                });
                thread.Start();
                Check.True(thread.Join(TimeSpan.FromSeconds(3)));
                Check.True(threadFailure is InvalidOperationException);
                Check.Throws<NotSupportedException>(() => stream.ReadAsync(new byte[1]).AsTask().GetAwaiter().GetResult());
                Check.Throws<NotSupportedException>(() => stream.ReadAsync(new byte[1], 0, 1).GetAwaiter().GetResult());
                Check.Throws<NotSupportedException>(() => stream.BeginRead(new byte[1], 0, 1, null, null));
                Check.Throws<NotSupportedException>(() => stream.CopyToAsync(Stream.Null).GetAwaiter().GetResult());
                Check.Throws<NotSupportedException>(() => stream.DisposeAsync().AsTask().GetAwaiter().GetResult());
                Check.Equal('v', (char)stream.ReadByte());
                return 0;
            }));
        });
        suite.Add("storage blobs: same-row mutation expires the handle and outer write rolls back", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            Seed(database, "before", [4]);
            var error = Check.Throws<SqliteException>(() => database.Write(DatabaseFile.Catalog, session =>
                session.WithReadBlob("stream_documents", "document", 9007199254740993L, stream =>
                {
                    session.Execute("UPDATE stream_documents SET binary=x'09'");
                    return stream.ReadByte();
                })));
            Check.Equal(4, error.SqliteErrorCode); // SQLITE_ABORT, including an update to another column.
            Check.Equal("04", database.Read(DatabaseFile.Catalog, session => session.Scalar("SELECT hex(binary) FROM stream_documents") as string));
            Check.Equal("before", database.Read(DatabaseFile.Catalog, session => session.WithReadBlob(
                "stream_documents", "document", 9007199254740993L, ReadText)));
        });
        suite.Add("storage blobs: missing/non-streamable cells fail without blocking later work", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            database.Write(DatabaseFile.Catalog, session => session.Execute("CREATE TABLE blob_invalid(data); INSERT INTO blob_invalid VALUES(NULL),(42),('')"));
            foreach (var id in new[] { 1, 2, 4 })
                Check.Throws<SqliteException>(() => database.Read(DatabaseFile.Catalog, session => session.WithReadBlob("blob_invalid", "data", id, ReadText)));
            Check.Equal("", database.Read(DatabaseFile.Catalog, session => session.WithReadBlob("blob_invalid", "data", 3, ReadText)));
            database.Write(DatabaseFile.Catalog, session => session.Execute("DELETE FROM blob_invalid"));
            Check.Equal(0L, database.Read(DatabaseFile.Catalog, session => (long)session.Scalar("SELECT count(*) FROM blob_invalid")!));
        });
        suite.Add("storage blobs: cancellation and reset retain ownership until physical read ends", () =>
        {
            using var temp = new Sandbox();
            using var database = NewDatabase(temp.Path);
            Seed(database, "still physically owned", [4]);
            var scheduler = new BoundedWorkScheduler(1, 1);
            using var cancel = new CancellationTokenSource();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var work = scheduler.RunAsync(() => database.Read(DatabaseFile.Catalog, session => session.WithReadBlob(
                "stream_documents", "document", 9007199254740993L, stream =>
                {
                    entered.Set();
                    Check.True(release.Wait(TimeSpan.FromSeconds(5)));
                    return ReadText(stream);
                })), cancel.Token);
            try
            {
                Check.True(entered.Wait(TimeSpan.FromSeconds(5)));
                cancel.Cancel();
                Check.True(!work.IsCompleted);
                Check.Throws<TimeoutException>(() => database.Quiescent(_ => 0, TimeSpan.FromMilliseconds(30)));
            }
            finally
            {
                release.Set();
                Check.Throws<OperationCanceledException>(() => work.GetAwaiter().GetResult());
                scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            Check.Equal(1, database.Quiescent(_ => 1));
        });
    }

    private static string ReadText(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 128, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static SqliteDatabase NewDatabase(string root)
    {
        var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = root });
        new SchemaMigrator(database).Initialize();
        return database;
    }

    private static void Seed(SqliteDatabase database, string text, byte[] binary) => database.Write(DatabaseFile.Catalog, session =>
        session.Execute("CREATE TABLE stream_documents(id INTEGER PRIMARY KEY,document TEXT,binary BLOB); INSERT INTO stream_documents VALUES($id,$text,$binary)",
            ("$id", 9007199254740993L), ("$text", text), ("$binary", binary)));

    private sealed class FixtureFailure : Exception;
}
