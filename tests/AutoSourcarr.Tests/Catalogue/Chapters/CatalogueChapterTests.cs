using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Chapters;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Tests;

public static class CatalogueChapterTests
{
    private const string Stamp = "2026-10-21T00:00:00+00:00";
    public static void Register(TestSuite suite)
    {
        suite.Add("catalogue chapters:28 released numeric/title keys retain exact Unicode and conversion behavior", () =>
        {
            var count = 0;
            foreach (var value in Fixture()["keys"]!.AsArray())
            {
                try { var result = ChapterNumbers.Key(value!["input"]!.AsObject()); Check.Equal(value["key"]!.GetValue<string>(), result.Key); Check.Equal(value["number"]!.GetValue<double>(), result.Number); count++; }
                catch (Exception error) { throw new InvalidOperationException(value!["name"]!.GetValue<string>(), error); }
            }
            Check.Equal(28, count);
        });
        suite.Add("catalogue chapters:27 released complete native ingestion cases use real metadata and SQLite", () =>
        {
            var count = 0;
            foreach (var value in Fixture()["ingestions"]!.AsArray())
            {
                try
                {
                    using var f = new Setup(value!.AsObject()); f.AssertMetadata(); var catalogue = f.Snapshot(DatabaseFile.Catalog);
                    foreach (var step in value["steps"]!.AsArray())
                    {
                        var input = step!["input"]!; var result = f.Writer.Write(new(L(input, "entry_id"), S(input, "source_id"), L(input, "source_manga_id"), input["chapters"]!.AsArray().Select(ch => NativeChapter.FromJson(ch!.AsObject())).ToArray()), input["update_scan"]!.GetValue<bool>());
                        Equal(step["expected"]!["origins_after"]!, f.Snapshot(DatabaseFile.Origins)); Equal(step["expected"]!["touched"]!, JsonSerializer.SerializeToNode(result.TouchedChapters)!); Equal(step["expected"]!["invalidated_candidates"]!, JsonSerializer.SerializeToNode(result.InvalidatedCandidates)!);
                        Equal(catalogue, f.Snapshot(DatabaseFile.Catalog)); f.Cleaned();
                    }
                    count++;
                }
                catch (Exception error) { throw new InvalidOperationException(S(value!, "name"), error); }
            }
            Check.Equal(27, count);
        });
        suite.Add("catalogue chapters:12 released saved gallery replay cases retain complete scan and journal proof", () =>
        {
            var count = 0;
            foreach (var value in Fixture()["replays"]!.AsArray())
            {
                try
                {
                    using var f = new Setup(value!.AsObject()); f.AssertMetadata(); var catalogue = f.Snapshot(DatabaseFile.Catalog);
                    foreach (var step in value["steps"]!.AsArray())
                    { f.Restore(step!["input"]!.AsObject()); Equal(step["expected"]!["origins_after"]!, f.Snapshot(DatabaseFile.Origins)); Equal(catalogue, f.Snapshot(DatabaseFile.Catalog)); f.Cleaned(); }
                    count++;
                }
                catch (Exception error) { throw new InvalidOperationException(S(value!, "name"), error); }
            }
            Check.Equal(12, count);
        });
        suite.Add("catalogue chapters: strict full streamed intents reject corruption before origins mutations", () =>
        {
            var invalid = new[] { "[100] trailing", "[100,]", "[0100]", "[100.0]", "[1e2]", "[true]", "[\"100\"]", "{\"id\":100}", "[9223372036854775808]", "[-9223372036854775809]", "[100", "\ufeff[100]" }.Select(value => Encoding.UTF8.GetBytes(value)).Append(new byte[] { 91, 0xc3, 0x28, 93 });
            foreach (var bytes in invalid)
            {
                using var f = new Setup(ReplayCase()); f.C(c => c.Execute("UPDATE gallery_layout_candidates SET candidate_ids_json=$value", ("$value", bytes))); var before = f.Snapshot(DatabaseFile.Origins);
                Check.Throws<InvalidDataException>(() => f.Restore(ReplayInput())); Equal(before, f.Snapshot(DatabaseFile.Origins)); Check.Equal(1L, f.Count("pending_entry_splits")); Check.Equal(1L, f.Count("gallery_layout_candidates"));
                Check.True(((byte[])f.Database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT candidate_ids_json FROM gallery_layout_candidates"))!).SequenceEqual(bytes)); f.Cleaned();
            }
            using var valid = new Setup(ReplayCase()); valid.C(c => c.Execute("UPDATE gallery_layout_candidates SET candidate_ids_json='[-9223372036854775808,100,100,9223372036854775807]'")); valid.Restore(ReplayInput()); Check.Equal(1L, valid.Scalar("SELECT active FROM chapter_candidates", DatabaseFile.Origins)!); valid.Cleaned();
        });
        suite.Add("catalogue chapters: streamed sort and SQL pages preserve one complete record across every batch", () =>
        {
            using var f = new Setup(ReplayCase()); const int amount = 769;
            f.O(o =>
            {
                o.Execute("DELETE FROM served_origin; DELETE FROM origin_history; DELETE FROM chapter_candidates; DELETE FROM virtual_chapters");
                for (var i = 1; i <= amount; i++)
                {
                    o.Execute("INSERT INTO virtual_chapters(id,entry_id,chapter_key,chapter_number,display_name,created_at,updated_at) VALUES($id,1,$key,$n,'Native chapter','old','old')", ("$id", 10000 + i), ("$key", "n:" + i), ("$n", i));
                    o.Execute("INSERT INTO chapter_candidates(id,virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,name,chapter_number,page_count,active,first_seen,last_seen) VALUES($id,$vid,'17','E-Hentai',111,$chapter,$name,$n,1,0,'original-first','old')", ("$id", 20000 + i), ("$vid", 10000 + i), ("$chapter", 30000 + i), ("$name", "Chapter " + i), ("$n", i));
                    if (i is 1 or amount) o.Execute("INSERT INTO candidate_pages VALUES($id,0,'https://images.invalid/retained','old')", ("$id", 20000 + i));
                }
                return 0;
            });
            var ids = Enumerable.Range(1, amount).Reverse().Select(i => (long)(20000 + i)).Concat(Enumerable.Range(0, 10000).Select(i => 90000000L + i)).Concat([20001L, 20769L]);
            var text = "[" + string.Join(",", ids) + "]"; f.C(c => c.Execute("UPDATE gallery_layout_candidates SET candidate_ids_json=$text", ("$text", text))); var scan = f.Database.Read(DatabaseFile.Origins, o => Rows(o, "source_record_scans"));
            f.Restore(ReplayInput()); Check.Equal((long)amount, f.Scalar("SELECT COUNT(*) FROM chapter_candidates WHERE active=1", DatabaseFile.Origins)!); Check.Equal((long)amount, f.Scalar("SELECT COUNT(*) FROM virtual_chapters WHERE entry_id=2", DatabaseFile.Origins)!); Check.Equal((long)amount, f.Scalar("SELECT COUNT(*) FROM chapter_candidates WHERE first_seen='original-first'", DatabaseFile.Origins)!);
            Equal(scan, f.Database.Read(DatabaseFile.Origins, o => Rows(o, "source_record_scans"))); Check.Equal("https://images.invalid/retained", f.Reader.PageFetchInfo(20001, 0)["private_url"]!.GetValue<string>()); Check.Equal("https://images.invalid/retained", f.Reader.PageFetchInfo(20769, 0)["private_url"]!.GetValue<string>());
            f.Restore(ReplayInput()); Check.Equal((long)amount, f.Count("chapter_candidates", DatabaseFile.Origins)); Check.Equal(1L, f.Count("pending_entry_splits")); f.Cleaned();
        });
        suite.Add("catalogue chapters: actual native ownership rejects detached and changed mappings", () =>
        {
            using (var f = new Setup(ReplayCase()))
            {
                var before = f.Snapshot(DatabaseFile.Origins); var input = ReplayInput(); input["current_owners"] = new JsonArray(new JsonArray("17", 111, 1));
                Check.Throws<InvalidOperationException>(() => f.Restore(input)); Equal(before, f.Snapshot(DatabaseFile.Origins)); f.Cleaned();
            }
            using (var f = new Setup(ReplayCase()))
            {
                f.C(c => c.Execute("UPDATE source_matches SET active=0")); var before = f.Snapshot(DatabaseFile.Origins);
                Check.Throws<InvalidOperationException>(() => f.Writer.Write(new(2, "17", 111, [new(1001, 0, "Chapter 0")]), false)); Equal(before, f.Snapshot(DatabaseFile.Origins)); f.Cleaned();
            }
            using (var f = new Setup(ReplayCase()))
            {
                var before = f.Snapshot(DatabaseFile.Origins); var input = ReplayInput(); input["records"] = new JsonArray(new JsonArray("18", 111));
                Check.Throws<InvalidOperationException>(() => f.Restore(input)); Equal(before, f.Snapshot(DatabaseFile.Origins)); f.Cleaned();
            }
        });
        suite.Add("catalogue chapters: real structural recovery replays before metadata, reader and journal acknowledgement", () =>
        {
            using var f = new Setup(ReplayCase()); var structure = f.Structure(f.Database); structure.ReconcilePending(default);
            Check.Equal(0L, f.Count("pending_entry_splits")); Check.Equal(0L, f.Count("gallery_layout_candidates")); Check.Equal(2L, f.Scalar("SELECT entry_id FROM source_record_scans", DatabaseFile.Origins)!); Check.Equal("2001-source-proof", f.Scalar("SELECT checked_at FROM source_record_scans", DatabaseFile.Origins) as string); Check.Equal("Retained verification error", f.Scalar("SELECT error FROM source_record_scans", DatabaseFile.Origins) as string);
            Check.Equal("https://images.invalid/100/page", f.Reader.PageFetchInfo(100, 0)["private_url"]!.GetValue<string>()); Check.True(f.Invalidated.SequenceEqual([1L, 2L])); f.Cleaned();
        });
        suite.Add("catalogue chapters: physical replay failure retains the real journal and recovers after reopen", () =>
        {
            using var f = new Setup(ReplayCase()); f.O(o => o.Execute("CREATE TRIGGER chapter_replay_abort BEFORE UPDATE OF chapter_key ON virtual_chapters BEGIN SELECT RAISE(ABORT,'fixture replay interruption'); END"));
            Check.Throws<CatalogueStructureRecoveryException>(() => f.Structure(f.Database).ReconcilePending(default)); Check.Equal(1L, f.Count("pending_entry_splits")); Check.Equal(1L, f.Count("gallery_layout_candidates")); Check.True(((string)f.Scalar("SELECT last_error FROM pending_entry_splits")!).Contains("fixture replay interruption", StringComparison.Ordinal)); Check.Equal(1L, f.Count("candidate_pages", DatabaseFile.Origins)); Check.Equal(1L, f.Count("origin_history", DatabaseFile.Origins));
            // Released replay first commits exclusive chapter ownership while freezing,
            // then ingests the record. That retained partial state must recover honestly.
            Check.Equal(2L, f.Scalar("SELECT entry_id FROM virtual_chapters WHERE id=10", DatabaseFile.Origins)!); Check.Equal("n:0", f.Scalar("SELECT chapter_key FROM virtual_chapters WHERE id=10", DatabaseFile.Origins) as string); Check.Equal(0L, f.Scalar("SELECT active FROM chapter_candidates WHERE id=100", DatabaseFile.Origins)!); f.Cleaned();
            f.O(o => o.Execute("DROP TRIGGER chapter_replay_abort")); using var reopened = new SqliteDatabase(new AutoSourcarrSettings { DataDir = f.Path }); f.Structure(reopened).ReconcilePending(default);
            Check.Equal(0L, f.Count("pending_entry_splits")); Check.Equal(0L, f.Count("gallery_layout_candidates")); Check.Equal(1L, f.Count("chapter_candidates", DatabaseFile.Origins)); Check.Equal(1L, f.Count("candidate_pages", DatabaseFile.Origins)); Check.Equal(1L, f.Count("origin_history", DatabaseFile.Origins)); Check.Equal(10L, f.Scalar("SELECT virtual_chapter_id FROM chapter_candidates WHERE id=100", DatabaseFile.Origins)!); Check.Equal(10L, f.Scalar("SELECT virtual_chapter_id FROM served_origin", DatabaseFile.Origins)!); Check.Equal(10L, f.Scalar("SELECT virtual_chapter_id FROM origin_history", DatabaseFile.Origins)!); Check.Equal(1L, f.Scalar("SELECT active FROM chapter_candidates WHERE id=100", DatabaseFile.Origins)!); f.Cleaned();
        });
        suite.Add("catalogue chapters: cancellation and invalid work roots publish no replay or fake acknowledgement", () =>
        {
            using var f = new Setup(ReplayCase()); var before = f.Snapshot(DatabaseFile.Origins); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check.Throws<OperationCanceledException>(() => f.Replay.Restore(1, [new("17", 111)], new Dictionary<NativeSourceRecord, long> { [new("17", 111)] = 2 }, cancelled.Token)); Equal(before, f.Snapshot(DatabaseFile.Origins)); f.Cleaned();
            var absent = new SavedGalleryReplay(f.Database, f.Gate, f.Writer, System.IO.Path.Combine(f.Work, "absent")); Check.Throws<IOException>(() => absent.Restore(1, [new("17", 111)], new Dictionary<NativeSourceRecord, long> { [new("17", 111)] = 2 })); Equal(before, f.Snapshot(DatabaseFile.Origins)); Check.Equal(1L, f.Count("pending_entry_splits")); Check.Equal(1L, f.Count("gallery_layout_candidates")); f.Cleaned();
        });
        suite.Add("catalogue chapters: owned spool cleanup is idempotent and retains a replaced directory namesake", () =>
        {
            using var f = new Setup(ReplayCase()); var type = typeof(SavedGalleryReplay).Assembly.GetType("AutoSourcarr.Core.Catalogue.Chapters.OwnedReplayFiles", throwOnError: true)!;
            using var owner = (IDisposable)Activator.CreateInstance(type, [f.Work])!; var create = type.GetMethod("Create")!;
            var file = (FileStream)create.Invoke(owner, null)!; var handle = file.SafeFileHandle; file.Write([1, 2, 3]); var job = Directory.EnumerateDirectories(f.Work).Single();
            if (OperatingSystem.IsLinux())
            {
                var moved = job + "-retained"; Directory.Move(job, moved); Directory.CreateDirectory(job); File.SetUnixFileMode(job, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); var marker = System.IO.Path.Combine(job, "namesake.txt"); File.WriteAllText(marker, "retain this namesake");
                Check.Throws<IOException>(owner.Dispose); owner.Dispose(); Check.Equal("retain this namesake", File.ReadAllText(marker)); Check.True(Directory.Exists(moved)); File.Delete(marker); Directory.Delete(job, recursive: false); Directory.Delete(moved, recursive: false);
            }
            else
            {
                var blocked = false; try { Directory.Move(job, job + "-replaced"); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { blocked = true; }
                Check.True(blocked, "Held Windows chapter directory handle must deny substitution"); owner.Dispose(); owner.Dispose();
            }
            Check.True(handle.IsClosed); Check.True(Check.Throws<TargetInvocationException>(() => create.Invoke(owner, null)).InnerException is ObjectDisposedException); f.Cleaned();
        });
    }
    private static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("catalogue-chapters.json")))!.AsObject();
    private static JsonObject ReplayCase() => Fixture()["replays"]!.AsArray().First(value => S(value!, "name") == "restore-exclusive-current-owner")!.AsObject();
    private static JsonObject ReplayInput() => ReplayCase()["steps"]![0]!["input"]!.AsObject();
    private static string S(JsonNode value, string name) => value[name]!.GetValue<string>();
    private static long L(JsonNode value, string name) => value[name]!.GetValue<long>();
    private static void Equal(JsonNode expected, JsonNode actual)
    { using var left = JsonDocument.Parse(expected.ToJsonString()); using var right = JsonDocument.Parse(actual.ToJsonString()); Check.JsonEqual(left.RootElement, right.RootElement); }
    private static JsonArray Rows(DatabaseSession c, string table) => new(c.Query("SELECT * FROM " + table + " ORDER BY rowid").Select(row => JsonSerializer.SerializeToNode(row.ToDictionary(pair => pair.Key, pair => pair.Value))).ToArray());
    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _sandbox = new(); public string Path => _sandbox.Path; public string Work { get; } public object Gate { get; } = new(); public SqliteDatabase Database { get; } public GalleryEvidenceService Gallery { get; } public NativeChapterWriter Writer { get; } public SavedGalleryReplay Replay { get; } public ReaderRepository Reader { get; } public List<long> Invalidated { get; } = []; private readonly JsonObject _fixture;
        public Setup(JsonObject fixture)
        {
            _fixture = fixture; Work = System.IO.Path.Combine(Path, "private-maintenance"); Directory.CreateDirectory(Work); if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Database = new(new AutoSourcarrSettings { DataDir = Path }); new SchemaMigrator(Database).Initialize(); new GalleryPolicyRepository(Database, Gate).Initialize();
            foreach (var seed in fixture["seed"]!.AsArray()) Database.Write(S(seed!, "file") == "catalog" ? DatabaseFile.Catalog : DatabaseFile.Origins, c => c.Execute(S(seed!, "sql")));
            Gallery = new(Database, Gate); Writer = new(Database, Gate, Gallery, Work, new FixedClock()); Replay = new(Database, Gate, Writer, Work); Reader = new(Database, Gate);
        }
        public void AssertMetadata()
        {
            foreach (var record in _fixture["controlled_native_records"]!.AsArray())
            {
                var row = Database.Read(DatabaseFile.Catalog, c => c.Query("SELECT metadata_json,active FROM source_matches WHERE source_id=$sid AND source_manga_id=$mid UNION ALL SELECT metadata_json,active FROM source_variants WHERE source_id=$sid AND source_manga_id=$mid", ("$sid", S(record!, "source_id")), ("$mid", L(record!, "source_manga_id"))).First()); if (!row.Flag("active")) continue;
                var meta = Gallery.CleanNative(S(record!, "source_id"), JsonNode.Parse(row.Text("metadata_json"))!.AsObject())["_gallery_metadata"] as JsonObject; var expected = record!["controlled_gallery_metadata"]!.AsObject();
                if (expected.Count == 0) Check.True(meta is null); else { Check.True(meta is not null); foreach (var pair in expected) { if (pair.Value is null) Check.True(meta![pair.Key] is null); else Equal(pair.Value, meta![pair.Key]!); } }
            }
        }
        public void Restore(JsonObject input) => Replay.Restore(L(input, "split_id"), input["records"]!.AsArray().Select(value => new NativeSourceRecord(value![0]!.GetValue<string>(), value[1]!.GetValue<long>())).ToArray(), input["current_owners"]!.AsArray().ToDictionary(value => new NativeSourceRecord(value![0]!.GetValue<string>(), value[1]!.GetValue<long>()), value => value![2]!.GetValue<long>()));
        public CatalogueStructure Structure(SqliteDatabase database)
        {
            var gallery = new GalleryEvidenceService(database, Gate); var metadata = new CatalogueMetadata(() => .9); var evidence = new NativeCatalogueEvidence(metadata, gallery, () => 24, () => true); var reader = new ReaderRepository(database, Gate); var writer = new NativeChapterWriter(database, Gate, gallery, Work, new FixedClock()); var replay = new SavedGalleryReplay(database, Gate, writer, Work);
            return new(database, Gate, new TaskRegistry(), gallery, metadata.Recompute, reader.ReconcileVisibility, DisplayMetadata.Reparent, evidence.Snapshot, ids => Invalidated.AddRange(ids), galleryReplay: replay, clock: new FixedClock());
        }
        public void C(Func<DatabaseSession, int> action) => Database.Write(DatabaseFile.Catalog, action); public void O(Func<DatabaseSession, int> action) => Database.Write(DatabaseFile.Origins, action);
        public object? Scalar(string sql, DatabaseFile file = DatabaseFile.Catalog) => Database.Read(file, c => c.Scalar(sql)); public long Count(string table, DatabaseFile file = DatabaseFile.Catalog) => Convert.ToInt64(Scalar("SELECT COUNT(*) FROM " + table, file), CultureInfo.InvariantCulture);
        public JsonObject Snapshot(DatabaseFile file) => Database.Read(file, c => { var result = new JsonObject(); foreach (var table in c.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name<>'VersionInfo' ORDER BY name")) result[table.Text("name")] = Rows(c, table.Text("name")); return result; });
        public void Cleaned() => Check.Equal(0, Directory.EnumerateFileSystemEntries(Work).Count());
        public void Dispose() { Database.Dispose(); _sandbox.Dispose(); }
    }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse(Stamp, CultureInfo.InvariantCulture); }
}
