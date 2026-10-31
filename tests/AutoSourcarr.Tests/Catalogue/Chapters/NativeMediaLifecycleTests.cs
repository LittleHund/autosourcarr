using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AutoSourcarr.Api;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Tests;

public static class NativeMediaLifecycleTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("native media lifecycle:34 original selections use the actual app structural owner", () => Oracle().GetAwaiter().GetResult());
        suite.Add("native media lifecycle:split retains pages history and scan provenance", () => Provenance().GetAwaiter().GetResult());
        suite.Add("native media lifecycle:current manual change and cancellation are rechecked at writer admission", () => Admission().GetAwaiter().GetResult());
        suite.Add("native media lifecycle:post-commit cache error retains exact recoverable journal", () => Journal().GetAwaiter().GetResult());
        suite.Add("native media lifecycle:cancelled admitted split finishes its physical cross-file work", () => Physical().GetAwaiter().GetResult());
        suite.Add("native media lifecycle:ambiguous native rows and nested sessions cannot grant mutation", () => InvalidAuthority().GetAwaiter().GetResult());
        suite.Add("native media lifecycle:owner construction never runs automatic changes", () => Construction().GetAwaiter().GetResult());
        suite.Add("native gallery lifecycle:animation separates from still with real cached chapter replay", () => GalleryProvenance().GetAwaiter().GetResult());
        suite.Add("native gallery lifecycle:all manual authority fences retain current owners", () => GalleryManual().GetAwaiter().GetResult());
        suite.Add("native gallery lifecycle:explicit profiles and disabled combining keep animation identity", () => GalleryProfiles().GetAwaiter().GetResult());
        suite.Add("native gallery lifecycle:optimistic metadata conflict defers instead of splitting", () => GalleryConflict().GetAwaiter().GetResult());
        suite.Add("native gallery lifecycle:committed split cancellation drains physical replay", () => GalleryPhysical().GetAwaiter().GetResult());
        suite.Add("native gallery lifecycle:post-commit error retains exact journal recovery", () => GalleryJournal().GetAwaiter().GetResult());
        suite.Add("native gallery lifecycle:paged native replay retains every candidate and rejection", () => GalleryPaged().GetAwaiter().GetResult());
    }
    private static JsonArray Corpus()
    {
        var bytes = File.ReadAllBytes(ConfigurationTests.Fixture("native-media-auto.json"));
        Check.Equal("5ccd7830877228e03965c60d65c4b89ec4527f68f098aa3362c82288e7a5b3a6", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var root = JsonNode.Parse(bytes)!;
        Check.True(root["provenance"]!["split_effects_stubbed"]!.GetValue<bool>());
        Check.True(root["provenance"]!["actual_python_ast"]!.GetValue<bool>());
        return root["cases"]!.AsArray();
    }
    private static async Task Oracle()
    {
        var count = 0;
        foreach (var item in Corpus())
        {
            try
            {
                await using var f = await Fixture.Create(item!["rows"]!.AsArray());
                var split = await f.Run(); var expected = item["selected"]!.AsArray();
                Check.Equal(expected.Count != 0, split is not null);
                if (split is not null)
                {
                    Check.Equal(1L, split.OriginalEntryId); Check.True(split.NewEntryId != 1);
                    var wanted = expected.Select(row => (row!["source_id"]!.GetValue<string>(), row["source_manga_id"]!.GetValue<long>())).ToHashSet();
                    Check.True(wanted.SetEquals(split.Records.Select(row => (row.SourceId, row.MangaId))));
                    foreach (var row in f.Owners()) Check.Equal(wanted.Contains((row.Text("source_id"), row.Integer("source_manga_id"))) ? split.NewEntryId : 1L, row.Integer("entry_id"));
                    Check.Equal(2L, f.Count("entries")); Check.Equal(0L, f.Count("pending_entry_splits"));
                }
                else { Check.Equal(1L, f.Count("entries")); Check.True(f.Owners().All(row => row.Integer("entry_id") == 1)); }
                Check.True(item["original_sqlite_unchanged"]!.GetValue<bool>()); count++;
            }
            catch (Exception error) { throw new InvalidOperationException(item!["name"]!.GetValue<string>(), error); }
        }
        Check.Equal(34, count);
    }
    private static JsonArray Pair() => new(Row("primary", "a", 101, "manga"), Row("additional", "b", 102, "webtoon"));
    private static JsonObject Row(string kind, string source, long manga, string genre) => new()
    { ["kind"] = kind, ["source_id"] = source, ["source_manga_id"] = manga, ["title"] = "Work", ["genre"] = genre, ["locked"] = 0, ["match_type"] = "auto" };
    private static async Task Provenance()
    {
        await using var f = await Fixture.Create(Pair());
        f.Runtime.Database.Write(DatabaseFile.Origins, c => c.Execute("""
            INSERT INTO virtual_chapters(id,entry_id,chapter_key,chapter_number,display_name,created_at,updated_at) VALUES(51,1,'n:1',1,'Chapter','now','now');
            INSERT INTO chapter_candidates(id,virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,name,first_seen,last_seen)
              VALUES(61,51,'a','A',101,1001,'Chapter','now','now'),(62,51,'b','B',102,1002,'Chapter','now','now');
            INSERT INTO candidate_pages VALUES(61,0,'https://images.invalid/a','now'),(62,0,'https://images.invalid/b','now');
            INSERT INTO served_origin VALUES(51,61,'now','fixture');
            INSERT INTO origin_history(virtual_chapter_id,candidate_id,source_id,source_name,source_manga_id,source_chapter_id,served_at,reason)
              VALUES(51,62,'b','B',102,1002,'now','fixture');
            INSERT INTO source_record_scans VALUES(1,'a',101,'now',1,''),(1,'b',102,'now',1,'');
            """));
        var split = (await f.Run())!;
        Check.Equal(2L, f.Count("chapter_candidates", DatabaseFile.Origins)); Check.Equal(2L, f.Count("candidate_pages", DatabaseFile.Origins));
        Check.Equal(1L, f.Count("origin_history", DatabaseFile.Origins)); Check.Equal(1L, f.Count("served_origin", DatabaseFile.Origins));
        Check.Equal(0L, f.Scalar("SELECT active FROM chapter_candidates WHERE id=62", DatabaseFile.Origins));
        Check.Equal(split.NewEntryId, f.Scalar("SELECT entry_id FROM source_record_scans WHERE source_id='b'", DatabaseFile.Origins));
        Check.Equal(0L, f.Count("pending_entry_splits"));
        Check.Equal(0L, f.Count("manual_match_bindings"));
    }
    private static async Task Admission()
    {
        await using var f = await Fixture.Create(Pair()); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            lock (f.Runtime.CatalogueWriteGate)
            {
                entered.Set(); release.Wait();
                f.Runtime.Database.Write(DatabaseFile.Catalog, c => c.Execute("UPDATE source_variants SET locked=1 WHERE source_id='b'"));
            }
        });
        Task<NativeMediaSplit?>? work = null;
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(5))); work = f.Run();
            Check.True(SpinWait.SpinUntil(() => f.Runtime.Io.Snapshot.Running > 0, TimeSpan.FromSeconds(5))); Check.True(!work.IsCompleted);
            release.Set(); await writer; Check.True(await work is null); Check.Equal(1L, f.Count("entries"));
        }
        finally { release.Set(); await writer; if (work is not null) await work; }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await f.Run(cancelled.Token); throw new InvalidOperationException("Cancelled separation admitted."); } catch (OperationCanceledException) { }
        Check.Equal(1L, f.Count("entries"));
    }
    private static async Task Journal()
    {
        await using var f = await Fixture.Create(Pair()); var cache = Path.Combine(f.Settings.DataDir, "cover-cache");
        Directory.Delete(cache); File.WriteAllText(cache, "owned-fault-fixture");
        try { await f.Run(); throw new InvalidOperationException("Failed invalidation became a clean split."); } catch (IOException) { }
        Check.Equal(2L, f.Count("entries")); Check.Equal(1L, f.Count("pending_entry_splits"));
        var created = f.Scalar("SELECT new_entry_id FROM pending_entry_splits");
        Check.Equal(created, f.Scalar("SELECT entry_id FROM source_matches WHERE source_id='b'"));
        File.Delete(cache);
        await f.Runtime.Io.RunAsync(() => { f.Runtime.Catalogue.Structure.ReconcilePending(CancellationToken.None); return 0; });
        Check.Equal(2L, f.Count("entries")); Check.Equal(0L, f.Count("pending_entry_splits"));
        Check.Equal(created, f.Scalar("SELECT entry_id FROM source_matches WHERE source_id='b'"));
    }
    private static async Task Physical()
    {
        await using var f = await Fixture.Create(Pair()); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var cancel = new CancellationTokenSource();
        var blocker = Task.Run(() => f.Runtime.Database.Write(DatabaseFile.Origins, c => { c.Execute("UPDATE virtual_chapters SET hidden=hidden"); entered.Set(); release.Wait(); return 0; }));
        Task<NativeMediaSplit?>? work = null;
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(5))); work = f.Run(cancel.Token);
            Check.True(SpinWait.SpinUntil(() => f.Count("entries") == 2, TimeSpan.FromSeconds(5)));
            await cancel.CancelAsync(); Check.True(!work.IsCompleted); Check.Equal(1L, f.Count("pending_entry_splits"));
            release.Set(); await blocker; var split = await work.WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(split is not null); Check.Equal(0L, f.Count("pending_entry_splits"));
        }
        finally { release.Set(); await blocker; if (work is not null) await work; }
    }
    private static async Task InvalidAuthority()
    {
        await using var f = await Fixture.Create(Pair());
        Check.Throws<InvalidOperationException>(() => f.Runtime.Database.Write(DatabaseFile.Catalog,
            _ => f.Runtime.Catalogue.Structure.SeparateMixedMedia(1, f.Runtime.Catalogue.NativeMetadata)));
        f.Runtime.Database.Write(DatabaseFile.Catalog, c => c.Execute("INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,genre,last_checked) VALUES(1,'a',101,'Work','webtoon','now')"));
        try { await f.Run(); throw new InvalidOperationException("Ambiguous native authority was inferred."); } catch (InvalidOperationException error) { Check.True(error.Message.Contains("unambiguous", StringComparison.Ordinal)); }
        Check.Equal(1L, f.Count("entries")); Check.Equal(0L, f.Count("pending_entry_splits"));
    }
    private static async Task Construction()
    {
        await using var f = await Fixture.Create(Pair()); await f.Runtime.DisposeAsync();
        f.Runtime = await ApplicationRuntime.CreateExternalForTestsAsync(f.Settings, f.Settings.SettingsConfigDir);
        Check.Equal(1L, f.Count("entries")); Check.Equal(0L, f.Count("pending_entry_splits"));
        Check.Equal(2, f.Owners().Count);
    }
    private static async Task GalleryProvenance()
    {
        await using var f = await Fixture.Create(Pair());
        f.ConfigureGalleries();
        f.Runtime.Database.Write(DatabaseFile.Origins, c => c.Execute("""
            INSERT INTO virtual_chapters(id,entry_id,chapter_key,chapter_number,display_name,created_at,updated_at) VALUES(51,1,'n:1',1,'Old','then','then');
            INSERT INTO chapter_candidates(id,virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,source_order,name,chapter_number,upload_date,scanlator,page_count,active,first_seen,last_seen)
              VALUES(61,51,'a','A',101,1001,4,'Old',1,1234,'Team',23,1,'then','then'),(62,51,'b','B',102,1002,5,'Old',1,1235,'Team B',24,1,'then','then'),(63,51,'b','B',102,1003,6,'Rejected',1,1236,'Team C',25,0,'then','then');
            INSERT INTO candidate_pages VALUES(61,0,'https://images.invalid/a','then'),(62,0,'https://images.invalid/b','then'),(63,0,'https://images.invalid/c','then');
            INSERT INTO chapter_candidate_rejections(source_id,source_chapter_id,rejected_at) VALUES('b',1003,'then');
            INSERT INTO served_origin VALUES(51,62,'then','fixture');
            INSERT INTO origin_history(virtual_chapter_id,candidate_id,source_id,source_name,source_manga_id,source_chapter_id,served_at,reason)
              VALUES(51,63,'b','B',102,1003,'then','fixture');
            INSERT INTO source_record_scans VALUES(1,'a',101,'scan-a',1,'old-a'),(1,'b',102,'scan-b',2,'old-b');
            """));
        var result = await f.Runtime.Io.RunAsync(() => f.Runtime.Catalogue.ReconcileGallery(17));
        Check.Equal(new GalleryReconciliationResult(2, 1, 0), result);
        Check.Equal(2L, f.Count("entries"));
        var created = f.Scalar("SELECT entry_id FROM source_matches WHERE source_id='b'"); Check.True(created != 1);
        Check.Equal(0L, f.Count("pending_entry_splits")); Check.Equal(0L, f.Count("manual_match_bindings"));
        Check.Equal(3L, f.Count("chapter_candidates", DatabaseFile.Origins)); Check.Equal(3L, f.Count("candidate_pages", DatabaseFile.Origins));
        Check.Equal(0L, f.Scalar("SELECT active FROM chapter_candidates WHERE id=63", DatabaseFile.Origins));
        Check.Equal(1L, f.Count("chapter_candidate_rejections", DatabaseFile.Origins));
        Check.Equal(created, f.Scalar("SELECT vc.entry_id FROM virtual_chapters vc JOIN chapter_candidates cc ON cc.virtual_chapter_id=vc.id WHERE cc.id=62", DatabaseFile.Origins));
        Check.Equal(created, f.Scalar("SELECT vc.entry_id FROM virtual_chapters vc JOIN chapter_candidates cc ON cc.virtual_chapter_id=vc.id WHERE cc.id=63", DatabaseFile.Origins));
        Check.Equal(24L, f.Scalar("SELECT page_count FROM chapter_candidates WHERE id=62", DatabaseFile.Origins));
        Check.Equal(1235L, f.Scalar("SELECT upload_date FROM chapter_candidates WHERE id=62", DatabaseFile.Origins));
        Check.Equal(5L, f.Scalar("SELECT source_order FROM chapter_candidates WHERE id=62", DatabaseFile.Origins));
        Check.Equal(created, f.Scalar("SELECT entry_id FROM source_record_scans WHERE source_id='b'", DatabaseFile.Origins));
        Check.Equal(1L, f.Count("origin_history", DatabaseFile.Origins)); Check.Equal(1L, f.Count("served_origin", DatabaseFile.Origins));
        Check.Equal(1L, f.Scalar("SELECT COUNT(*) FROM source_matches WHERE source_id='b' AND match_type='auto-animation-split' AND locked=0"));
        var repeated = await f.RunGallery(); Check.Equal(new GalleryReconciliationResult(1, 0, 0), repeated); Check.Equal(2L, f.Count("entries"));
    }
    private static async Task GalleryManual()
    {
        foreach (var type in new[] { "manual", "MaNuAl", "split-duplicate", "separate-anchor", "locked", "binding" })
        {
            await using var f = await Fixture.Create(Pair()); f.ConfigureGalleries();
            f.Runtime.Database.Write(DatabaseFile.Catalog, c =>
            {
                c.Execute("UPDATE entries SET preferred_title='My title',manual_preferred_title=1 WHERE id=1");
                if (type == "locked") c.Execute("UPDATE source_matches SET locked=1 WHERE source_id='a'");
                else if (type == "binding") c.Execute("INSERT INTO manual_match_bindings(source_id,source_manga_id,entry_id_hint,created_at,updated_at) VALUES('b',102,1,'then','then')");
                else c.Execute("UPDATE source_matches SET match_type=$type WHERE source_id='a'", ("$type", type));
                return 0;
            });
            Check.Equal(new GalleryReconciliationResult(2, 0, 0), await f.RunGallery()); Check.Equal(1L, f.Count("entries"));
            Check.True(f.Owners().All(row => row.Integer("entry_id") == 1));
            Check.Equal(1L, f.Scalar("SELECT COUNT(*) FROM entries WHERE preferred_title='My title' AND manual_preferred_title=1"));
        }
    }
    private static async Task GalleryProfiles()
    {
        foreach (var enabled in new[] { true, false })
        {
            await using var f = await Fixture.Create(Pair()); f.ConfigureGalleries();
            f.Runtime.Database.Write(DatabaseFile.Catalog, c => c.Execute("UPDATE source_config SET name='Unrecognized source',base_url=''"));
            Check.Equal(new GalleryReconciliationResult(0, 0, 0), await f.RunGallery());
            var repository = new GalleryPolicyRepository(f.Runtime.Database, f.Runtime.CatalogueWriteGate);
            foreach (var sid in new[] { "a", "b" }) { var policy = GalleryPolicy.Default; policy["profile"] = "gallery"; policy["enabled"] = enabled; repository.Save(sid, policy, 0); }
            Check.Equal(new GalleryReconciliationResult(2, 1, 0), await f.RunGallery()); Check.Equal(2L, f.Count("entries"));
        }
        // A conventional manga source is also a still-work anchor.
        await using var anchor = await Fixture.Create(Pair()); anchor.ConfigureGalleries();
        anchor.Runtime.Database.Write(DatabaseFile.Catalog, c => c.Execute("UPDATE source_config SET name='Conventional source',base_url='' WHERE source_id='a'"));
        Check.Equal(new GalleryReconciliationResult(1, 1, 0), await anchor.RunGallery());
    }
    private static async Task GalleryConflict()
    {
        foreach (var mutation in new[] { "ignore", "manual", "profile" })
        {
            await using var f = await Fixture.Create(Pair()); f.ConfigureGalleries();
            var sql = mutation switch
            {
                "ignore" => "CREATE TRIGGER gallery_race BEFORE UPDATE OF source_title,metadata_json ON source_variants BEGIN SELECT RAISE(IGNORE); END;",
                "manual" => "CREATE TRIGGER gallery_race AFTER UPDATE OF source_title,metadata_json ON source_variants BEGIN UPDATE source_variants SET locked=1 WHERE id=NEW.id; END;",
                _ => "CREATE TRIGGER gallery_race AFTER UPDATE OF source_title,metadata_json ON source_variants BEGIN UPDATE source_config SET name='Changed source' WHERE source_id=NEW.source_id; END;"
            };
            f.Runtime.Database.Write(DatabaseFile.Catalog, c => c.Execute(sql));
            var result = await f.RunGallery(); Check.Equal(1, result.Deferred); Check.Equal(0, result.Splits);
            Check.Equal(mutation == "ignore" ? 1 : 2, result.Records); Check.Equal(1L, f.Count("entries")); Check.Equal(0L, f.Count("pending_entry_splits"));
        }
    }
    private static async Task GalleryPhysical()
    {
        await using var f = await Fixture.Create(Pair()); f.ConfigureGalleries();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var cancel = new CancellationTokenSource();
        var blocker = Task.Run(() => f.Runtime.Database.Write(DatabaseFile.Origins, c => { c.Execute("UPDATE virtual_chapters SET hidden=hidden"); entered.Set(); release.Wait(); return 0; }));
        Task<GalleryReconciliationResult>? work = null;
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(5))); work = f.RunGallery(cancel.Token);
            Check.True(SpinWait.SpinUntil(() => f.Count("entries") == 2, TimeSpan.FromSeconds(5)));
            await cancel.CancelAsync(); Check.True(!work.IsCompleted); release.Set(); await blocker;
            Check.Equal(new GalleryReconciliationResult(2, 1, 0), await work.WaitAsync(TimeSpan.FromSeconds(5))); Check.Equal(0L, f.Count("pending_entry_splits"));
        }
        finally { release.Set(); await blocker; if (work is not null) await work; }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await f.RunGallery(cancelled.Token); throw new InvalidOperationException("Cancelled gallery admission succeeded."); } catch (OperationCanceledException) { }
        Check.Equal(2L, f.Count("entries"));
    }
    private static async Task GalleryJournal()
    {
        await using var f = await Fixture.Create(Pair()); f.ConfigureGalleries();
        var cache = Path.Combine(f.Settings.DataDir, "cover-cache"); Directory.Delete(cache); File.WriteAllText(cache, "owned-fault-fixture");
        try { await f.RunGallery(); throw new InvalidOperationException("Gallery split ignored failed cleanup."); } catch (IOException) { }
        Check.Equal(2L, f.Count("entries")); Check.Equal(1L, f.Count("pending_entry_splits")); File.Delete(cache);
        await f.Runtime.Io.RunAsync(() => { f.Runtime.Catalogue.Structure.ReconcilePending(CancellationToken.None); return 0; });
        Check.Equal(2L, f.Count("entries")); Check.Equal(0L, f.Count("pending_entry_splits"));
        Check.Equal(1L, f.Scalar("SELECT COUNT(*) FROM source_matches WHERE source_id='b' AND match_type='auto-animation-split' AND locked=0"));
    }
    private static async Task GalleryPaged()
    {
        await using var f = await Fixture.Create(Pair()); f.ConfigureGalleries();
        f.Runtime.Database.Write(DatabaseFile.Origins, c =>
        {
            c.Execute("INSERT INTO virtual_chapters(id,entry_id,chapter_key,chapter_number,display_name,created_at,updated_at) VALUES(51,1,'n:1',1,'Old','then','then')");
            for (var i = 0; i < 600; i++)
            {
                c.Execute("INSERT INTO chapter_candidates(id,virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,source_order,name,chapter_number,active,first_seen,last_seen) VALUES($i,51,'b','B',102,$ch,$order,'Old',1,$active,'then','then')", ("$i", 1000 + i), ("$ch", 2000 + i), ("$order", i), ("$active", i % 3 == 0 ? 0 : 1));
                c.Execute("INSERT INTO candidate_pages VALUES($i,0,'https://images.invalid/page','then')", ("$i", 1000 + i));
                if (i % 3 == 0) c.Execute("INSERT INTO chapter_candidate_rejections(source_id,source_chapter_id,rejected_at) VALUES('b',$ch,'then')", ("$ch", 2000 + i));
            }
            return 0;
        });
        Check.Equal(new GalleryReconciliationResult(2, 1, 0), await f.RunGallery());
        Check.Equal(600L, f.Count("chapter_candidates", DatabaseFile.Origins)); Check.Equal(600L, f.Count("candidate_pages", DatabaseFile.Origins));
        Check.Equal(400L, f.Scalar("SELECT COUNT(*) FROM chapter_candidates WHERE active=1", DatabaseFile.Origins));
        Check.Equal(200L, f.Count("chapter_candidate_rejections", DatabaseFile.Origins));
        var owner = f.Scalar("SELECT entry_id FROM source_matches WHERE source_id='b'");
        Check.Equal(600L, f.Scalar("SELECT COUNT(*) FROM chapter_candidates cc JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id WHERE vc.entry_id=" + owner.ToString(CultureInfo.InvariantCulture), DatabaseFile.Origins));
        Check.Equal(0L, f.Count("source_record_scans", DatabaseFile.Origins));
        Check.Equal(0, Directory.EnumerateFileSystemEntries(Path.Combine(f.Settings.DataDir, "catalogue-work")).Count());
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Sandbox _temp = new();
        internal ApplicationRuntime Runtime { get; set; } = null!;
        internal AutoSourcarrSettings Settings { get; private set; } = null!;
        internal static async Task<Fixture> Create(JsonArray rows)
        {
            var f = new Fixture();
            try
            {
                f.Settings = new() { DataDir = Path.Combine(f._temp.Path, "data"), SettingsConfigDir = Path.Combine(f._temp.Path, "config"), SourceEngineMode = "external", SourceSuwayomiUrl = "http://127.0.0.1:1", SqliteBusyTimeoutMs = 10000 };
                f.Runtime = await ApplicationRuntime.CreateExternalForTestsAsync(f.Settings, f.Settings.SettingsConfigDir);
                f.Runtime.Database.Write(DatabaseFile.Catalog, c =>
                {
                    c.Execute("INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(1,'Work','now','now'); INSERT INTO entry_aliases VALUES(17,1,'merge','now')");
                    foreach (var row in rows)
                    {
                        var source = row!["source_id"]!.GetValue<string>(); var manga = row["source_manga_id"]!.GetValue<long>();
                        c.Execute("INSERT OR IGNORE INTO source_config(source_id,name,lang,last_seen) VALUES($s,'Fixture source','en','now')", ("$s", source));
                        var table = row["kind"]!.GetValue<string>() == "primary" ? "source_matches" : "source_variants";
                        c.Execute($"INSERT INTO {table}(entry_id,source_id,source_manga_id,source_manga_url,source_title,genre,match_type,locked,last_checked) VALUES(1,$s,$m,$url,$t,$g,$type,$locked,'now')",
                            ("$s", source), ("$m", manga), ("$url", "/manga/" + manga.ToString(CultureInfo.InvariantCulture)), ("$t", row["title"]!.GetValue<string>()),
                            ("$g", row["genre"]!.GetValue<string>()), ("$type", row["match_type"]!.GetValue<string>()), ("$locked", row["locked"]!.GetValue<int>()));
                    }
                    return 0;
                });
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal Task<NativeMediaSplit?> Run(CancellationToken token = default) => Runtime.Io.RunAsync(() => Runtime.Catalogue.Structure.SeparateMixedMedia(17, Runtime.Catalogue.NativeMetadata, token));
        internal Task<GalleryReconciliationResult> RunGallery(CancellationToken token = default) => Runtime.Io.RunAsync(() => Runtime.Catalogue.ReconcileGallery(17, token));
        internal void ConfigureGalleries() => Runtime.Database.Write(DatabaseFile.Catalog, c =>
        {
            c.Execute("UPDATE source_config SET name='E-Hentai',base_url='https://e-hentai.org'");
            c.Execute("UPDATE source_matches SET source_title='[Artist] Work',genre='',metadata_json=$j WHERE source_id='a'", ("$j", "{\"id\":101,\"title\":\"[Artist] Work\",\"initialized\":true}"));
            c.Execute("UPDATE source_variants SET source_title='[Artist] Work [Animation]',genre='',metadata_json=$j WHERE source_id='b'", ("$j", "{\"id\":102,\"title\":\"[Artist] Work [Animation]\",\"initialized\":true}"));
            return 0;
        });
        internal IReadOnlyList<DatabaseRow> Owners() => Runtime.Database.Read(DatabaseFile.Catalog, c => c.Query("SELECT entry_id,source_id,source_manga_id FROM source_matches WHERE active=1 UNION ALL SELECT entry_id,source_id,source_manga_id FROM source_variants WHERE active=1"));
        internal long Scalar(string sql, DatabaseFile file = DatabaseFile.Catalog) => Runtime.Database.Read(file, c => Convert.ToInt64(c.Scalar(sql), CultureInfo.InvariantCulture));
        internal long Count(string table, DatabaseFile file = DatabaseFile.Catalog) => Scalar("SELECT COUNT(*) FROM " + table, file);
        public async ValueTask DisposeAsync() { try { if (Runtime is not null) await Runtime.DisposeAsync(); } finally { _temp.Dispose(); } }
    }
}
