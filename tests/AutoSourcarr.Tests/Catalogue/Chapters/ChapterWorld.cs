using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Chapters;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests.Catalogue.Chapters;

/// <summary>A real catalogue/origins database with one work (entry 1) and two sources attached as primary records.</summary>
internal sealed class ChapterWorld : IDisposable
{
    public const long Entry = 1;
    public const string SourceA = "1001", SourceB = "1002";
    public const long MangaA = 501, MangaB = 502;
    private readonly Sandbox _sandbox = new();
    public object Gate { get; } = new();
    public SqliteDatabase Database { get; }
    public NativeChapterWriter Writer { get; }
    public ReaderRepository Reader { get; }
    public InMemoryDecisionLog Log { get; } = new();
    public string Work { get; }

    public ChapterWorld()
    {
        Work = Path.Combine(_sandbox.Path, "private-maintenance"); Directory.CreateDirectory(Work);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Database = new(new AutoSourcarrSettings { DataDir = _sandbox.Path }); new SchemaMigrator(Database).Initialize(); new GalleryPolicyRepository(Database, Gate).Initialize();
        Database.Write(DatabaseFile.Catalog, session => session.Execute("""
            INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(1,'Solo Story','now','now');
            INSERT INTO source_config(source_id,name,lang,priority,last_seen) VALUES('1001','Alpha Scans','en',10,'now'),('1002','Beta Comics','en',20,'now');
            INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,last_checked) VALUES(1,'1001',501,'Solo Story','now'),(1,'1002',502,'Solo Story','now');
            """));
        Writer = new(Database, Gate, new GalleryEvidenceService(Database, Gate), Work);
        Reader = new(Database, Gate);
    }

    public static JsonObject Chapter(long id, double number, string name, long pages = 0) =>
        new() { ["id"] = id, ["chapterNumber"] = number, ["name"] = name, ["url"] = "/c/" + id, ["sourceOrder"] = id, ["pageCount"] = pages };

    public ChapterWriteResult Write(string sourceId, long mangaId, params JsonObject[] chapters) =>
        Writer.Write(new ChapterBatch(Entry, sourceId, mangaId, chapters.Select(NativeChapter.FromJson).ToArray()));

    public long Scalar(DatabaseFile file, string sql, params (string, object?)[] parameters) =>
        Convert.ToInt64(Database.Read(file, session => session.Scalar(sql, parameters)), System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose() { Database.Dispose(); _sandbox.Dispose(); }
}
