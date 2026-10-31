using AutoSourcarr.Api;
using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Records;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Tests.CatalogueHostTests;

namespace AutoSourcarr.Tests;

public static class NativeMetadataHydrationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("native hydration:actual HTTP detail and current manual title native tags and hints persist", () => Detail().GetAwaiter().GetResult());
        suite.Add("native hydration:idle request promotes in place without duplicate source work", () => Promotion().GetAwaiter().GetResult());
        suite.Add("native hydration:source capacity is shared while whole-task capacity stays independent", () => Capacity().GetAwaiter().GetResult());
        suite.Add("native hydration:delayed detail follows current owner and never recreates detached records", () => Ownership().GetAwaiter().GetResult());
        suite.Add("native hydration:wrong identities and detail deadline cannot publish native facts", () => InvalidAndTimeout().GetAwaiter().GetResult());
        suite.Add("native hydration:retired transport waiting on real writer gate cannot commit", () => Retirement().GetAwaiter().GetResult());
        suite.Add("native hydration:admitted split survives cancellation and hydrates its actual new role", () => Physical(false).GetAwaiter().GetResult());
        suite.Add("native hydration:late physical error survives cancellation and exact retry recovers", () => Physical(true).GetAwaiter().GetResult());
        suite.Add("native hydration:authoritative failure preserves prior durable native phases", () => Partial().GetAwaiter().GetResult());
        suite.Add("native hydration:bounded pending jobs stop without consuming source or whole-task slots", () => Bounded().GetAwaiter().GetResult());
        suite.Add("native hydration:bounded cover hint LRU retains native fallback and exact IDs", () => Hints().GetAwaiter().GetResult());
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<NativeHydrationResult> Queue(Fixture f, bool eager = true, long manga = Manga)
    {
        Check.True(f.Runtime.Catalogue.Hydration.TrySchedule(Source, manga, out var completion, eager)); return completion!;
    }
    private static long Number(Fixture f, string sql, DatabaseFile file = DatabaseFile.Catalog) => f.Runtime.Database.Read(file, c => Convert.ToInt64(c.Scalar(sql), CultureInfo.InvariantCulture));
    private static JsonObject Details(long id = Manga, string source = Source, string genre = "Manga") => new()
    {
        ["manga"] = new JsonObject { ["id"] = id, ["sourceId"] = source, ["title"] = "Hydrated work", ["url"] = "/work", ["realUrl"] = "https://source.invalid/work",
            ["initialized"] = true, ["description"] = "brief", ["author"] = "Writer", ["artist"] = "Painter", ["genre"] = new JsonArray(genre, "artist:Painter"), ["status"] = 2, ["thumbnailUrl"] = "https://source.invalid/cover.png" },
        ["chapters"] = new JsonArray()
    };
    private static void InstallHandler(Fixture f, string genre = "Manga") => f.Upstream!.DetailsHandler = (request, _) =>
    {
        Check.Equal(Manga, request["variables"]!["input"]!["id"]!.GetValue<long>());
        Check.True(request["variables"]!["input"]!["fetchManga"]!.GetValue<bool>());
        Check.True(!request["variables"]!["input"]!["fetchChapters"]!.GetValue<bool>());
        return Task.FromResult(Details(genre: genre));
    };
    private static async Task Detail()
    {
        await using var f = await Fixture.Create(upstream: true, configure: s => s with { BackgroundIdleSeconds = 0 }); InstallHandler(f);
        var rowId = Number(f, "SELECT id FROM source_matches");
        f.Runtime.Database.Write(DatabaseFile.Catalog, c => c.Execute("""
            UPDATE entries SET preferred_title='My kept title',manual_preferred_title=1;
            UPDATE source_matches SET source_title='Old',description='Old description longer than the new detail',locked=1,match_type='manual';
            UPDATE source_config SET base_url='https://source.invalid';
            INSERT INTO titles(entry_id,title,normalized,language,title_type,source_id) VALUES($id,'Manual alias','manual alias','en','manual','');
            """, ("$id", Entry)));
        var result = await Queue(f).WaitAsync(TimeSpan.FromSeconds(10)); Check.Equal("applied", result.State); Check.Equal(Entry, result.EntryId!.Value);
        var native = f.Runtime.Database.Read(DatabaseFile.Catalog, c => c.Query("SELECT * FROM source_matches").Single());
        Check.Equal(rowId, native.Integer("id")); Check.Equal(Manga, native.Integer("source_manga_id"));
        Check.Equal("brief", native.Text("description")); Check.Equal("Hydrated work", native.Text("source_title")); Check.True(native.Flag("locked")); Check.Equal("manual", native.Text("match_type"));
        Check.Equal(1L, Number(f, "SELECT COUNT(*) FROM entries WHERE preferred_title='My kept title' AND manual_preferred_title=1"));
        Check.Equal(1L, Number(f, "SELECT COUNT(*) FROM titles WHERE title_type='manual' AND title='Manual alias'"));
        Check.True(Number(f, "SELECT COUNT(*) FROM source_record_tags") > 0); Check.Equal(0L, Number(f, "SELECT COUNT(*) FROM source_record_scans", DatabaseFile.Origins));
        Check.Equal(new NativeCoverHint(Source, "https://source.invalid/cover.png", "https://source.invalid"), f.Runtime.Catalogue.Hydration.SourceCoverHint(Manga));
        Check.Equal(0, f.Runtime.Catalogue.Hydration.Pending);
    }
    private static async Task Promotion()
    {
        await using var f = await Fixture.Create(upstream: true, configure: s => s with { BackgroundIdleSeconds = 60 });
        var entered = Signal(); var release = Signal(); var calls = 0;
        f.Upstream!.DetailsHandler = async (_, _) => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; return Details(); };
        using var interactive = f.Runtime.Lanes.BeginInteractive(); var first = Queue(f, eager: false);
        try
        {
            Check.True(!f.Runtime.Catalogue.Hydration.TrySchedule(Source, Manga, out var repeated)); Check.True(ReferenceEquals(first, repeated));
            await Task.Delay(100); Check.True(!entered.Task.IsCompleted); Check.Equal(1, f.Runtime.Catalogue.Hydration.Pending);
            Check.True(f.Runtime.Catalogue.Hydration.TrySchedule(Source, Manga, out var promoted, eager: true)); Check.True(ReferenceEquals(first, promoted));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check.Equal(1, calls);
        }
        finally { release.TrySetResult(); }
        Check.Equal("applied", (await first.WaitAsync(TimeSpan.FromSeconds(10))).State); Check.Equal(0, f.Runtime.Catalogue.Hydration.Pending);
        Check.Equal("applied", (await Queue(f).WaitAsync(TimeSpan.FromSeconds(10))).State); Check.Equal(2, calls);
    }
    private static async Task Capacity()
    {
        await using var f = await Fixture.Create(upstream: true, configure: s => s with { BackgroundIdleSeconds = 0, BackgroundTaskConcurrency = 1, BackgroundSourceConcurrency = 1, SourceHostWorkConcurrency = 2 });
        var calls = 0; f.Upstream!.DetailsHandler = (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(Details()); };
        foreach (var sourceLane in new[] { false, true })
        {
            var entered = Signal(); var release = Signal(); var held = f.Runtime.Lanes.RunBackgroundAsync(async _ => { entered.TrySetResult(); await release.Task; return 0; }, sourceLane: sourceLane);
            Task<NativeHydrationResult>? hydration = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var before = calls; hydration = Queue(f);
                if (sourceLane) { await Task.Delay(100); Check.Equal(before, calls); Check.True(!hydration.IsCompleted); }
                else { Check.Equal("applied", (await hydration.WaitAsync(TimeSpan.FromSeconds(10))).State); Check.Equal(before + 1, calls); Check.Equal(1, f.Runtime.Lanes.Snapshot.BackgroundActive); }
            }
            finally { release.TrySetResult(); await held; if (hydration is not null) Check.Equal("applied", (await hydration.WaitAsync(TimeSpan.FromSeconds(10))).State); }
        }
    }
    private static async Task Ownership()
    {
        foreach (var detach in new[] { false, true })
        {
            await using var f = await Fixture.Create(upstream: true, configure: s => s with { BackgroundIdleSeconds = 0 }); var entered = Signal(); var release = Signal();
            f.Upstream!.DetailsHandler = async (_, _) => { entered.TrySetResult(); await release.Task; return Details(); };
            var hydration = Queue(f); var target = Entry + 100;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                lock (f.Runtime.CatalogueWriteGate) f.Runtime.Database.Write(DatabaseFile.Catalog, c =>
                {
                    c.Execute("INSERT INTO entries(id,preferred_title,manual_preferred_title,created_at,updated_at) VALUES($id,'New manual owner',1,'now','now')", ("$id", target));
                    if (detach) c.Execute("DELETE FROM source_matches");
                    else c.Execute("UPDATE source_matches SET entry_id=$id,locked=1,match_type='manual'", ("$id", target));
                    return 0;
                });
            }
            finally { release.TrySetResult(); }
            var result = await hydration.WaitAsync(TimeSpan.FromSeconds(10)); Check.Equal(detach ? "detached" : "applied", result.State);
            Check.Equal(detach ? 0L : 1L, Number(f, "SELECT COUNT(*) FROM source_matches"));
            Check.Equal(1L, Number(f, "SELECT COUNT(*) FROM entries WHERE id=" + Entry.ToString(CultureInfo.InvariantCulture) + " AND description=''"));
            if (!detach) { Check.Equal(target, result.EntryId!.Value); Check.Equal(1L, Number(f, "SELECT COUNT(*) FROM entries WHERE preferred_title='New manual owner' AND description='brief' AND manual_preferred_title=1")); }
        }
    }
    private static async Task InvalidAndTimeout()
    {
        foreach (var failure in new[] { "source", "manga", "timeout" })
        {
            await using var f = await Fixture.Create(upstream: true, configure: s => s with { BackgroundIdleSeconds = 0, MetadataEnrichmentTimeout = .01 });
            f.Upstream!.DetailsHandler = async (_, context) =>
            {
                if (failure == "timeout") await Task.Delay(Timeout.Infinite, context.RequestAborted);
                return Details(id: failure == "manga" ? Manga + 1 : Manga, source: failure == "source" ? "wrong-source" : Source);
            };
            var result = await Queue(f).WaitAsync(TimeSpan.FromSeconds(10)); Check.Equal(failure == "timeout" ? "timeout" : "failed", result.State);
            Check.Equal(1L, Number(f, "SELECT COUNT(*) FROM source_matches WHERE source_title='Source title' AND initialized=0 AND description=''"));
            Check.Equal(1L, Number(f, "SELECT COUNT(*) FROM entries"));
        }
    }
    private static async Task Retirement()
    {
        await using var f = await Fixture.Create(upstream: true, configure: s => s with { BackgroundIdleSeconds = 0 }); InstallHandler(f);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var writer = Task.Run(() => { lock (f.Runtime.CatalogueWriteGate) { entered.Set(); release.Wait(); } });
        Task<NativeHydrationResult>? hydration = null; Task? drain = null;
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(5))); hydration = Queue(f);
            Check.True(SpinWait.SpinUntil(() => f.Runtime.Catalogue.Hydration.StageOf(Source, Manga) == "persisting" && f.Runtime.Io.Snapshot.Running > 0, TimeSpan.FromSeconds(5)));
            var pause = f.Runtime.SourceRuntime.Client.BeginTransportPause(); drain = f.Runtime.SourceRuntime.Client.DrainTransportAsync(pause);
            Check.True(!drain.IsCompleted, "Transport drain released the real session while persistence still owned the gate wait.");
            Check.True(!hydration.IsCompleted, "Hydration completed before its admitted physical gate wait finished.");
        }
        finally { release.Set(); await writer; }
        Check.Equal("cancelled", (await hydration!.WaitAsync(TimeSpan.FromSeconds(10))).State); await drain!.WaitAsync(TimeSpan.FromSeconds(10));
        Check.Equal(1L, Number(f, "SELECT COUNT(*) FROM source_matches WHERE source_title='Source title' AND initialized=0"));
    }
    private static async Task Physical(bool fault)
    {
        await using var f = await Fixture.Create(upstream: true, configure: s => s with { BackgroundIdleSeconds = 0, SqliteBusyTimeoutMs = 10000 }); InstallHandler(f, "Webtoon");
        f.Runtime.Database.Write(DatabaseFile.Catalog, c => c.Execute("""
            UPDATE source_matches SET source_manga_id=$anchor,source_title='Work',genre='Manga';
            INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,description,genre,last_checked)
              VALUES($entry,$source,$manga,'Work','Old description longer than new detail','Manga','then');
            """, ("$anchor", Manga + 10), ("$entry", Entry), ("$source", Source), ("$manga", Manga)));
        var cache = Path.Combine(f.Settings.DataDir, "cover-cache"); if (fault) { Directory.Delete(cache); File.WriteAllText(cache, "owned-fault"); }
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var blocker = Task.Run(() => f.Runtime.Database.Write(DatabaseFile.Origins, c => { c.Execute("UPDATE virtual_chapters SET hidden=hidden"); entered.Set(); release.Wait(); return 0; }));
        Task<NativeHydrationResult>? hydration = null; Task? stop = null;
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(5))); hydration = Queue(f);
            Check.True(SpinWait.SpinUntil(() => Number(f, "SELECT COUNT(*) FROM entries") == 2, TimeSpan.FromSeconds(5)));
            stop = f.Runtime.DisposeAsync().AsTask(); Check.True(SpinWait.SpinUntil(() => f.Runtime.Stopping, TimeSpan.FromSeconds(5)));
            Check.True(!stop.IsCompleted); Check.True(!hydration.IsCompleted);
            Check.Throws<IOException>(() => ApplicationRuntime.InstanceRoots.Acquire([f.Settings.SettingsConfigDir]));
        }
        finally { release.Set(); await blocker; if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(10)); }
        var result = await hydration!.WaitAsync(TimeSpan.FromSeconds(10)); Check.Equal(fault ? "failed" : "applied", result.State);
        if (fault) { Check.True(result.FailureTypes.Contains("IOException", StringComparison.Ordinal)); File.Delete(cache); }
        await using var reopened = await ApplicationRuntime.CreateExternalForTestsAsync(f.Settings, f.Settings.SettingsConfigDir);
        var pending = reopened.Database.Read(DatabaseFile.Catalog, c => Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM pending_entry_splits"), CultureInfo.InvariantCulture)); Check.Equal(fault ? 1L : 0L, pending);
        if (fault)
        {
            await reopened.Io.RunAsync(() => { reopened.Catalogue.Structure.ReconcilePending(CancellationToken.None); return 0; });
            Check.True(reopened.Catalogue.Hydration.TrySchedule(Source, Manga, out var retry, eager: true));
            Check.Equal("applied", (await retry!.WaitAsync(TimeSpan.FromSeconds(10))).State);
        }
        var current = reopened.Database.Read(DatabaseFile.Catalog, c => c.Query("SELECT * FROM source_matches WHERE source_id=$s AND source_manga_id=$m", ("$s", Source), ("$m", Manga)).Single());
        Check.True(current.Integer("entry_id") != Entry); Check.Equal("brief", current.Text("description")); Check.True(current.Flag("initialized"));
        Check.Equal(2L, reopened.Database.Read(DatabaseFile.Catalog, c => Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM entries"), CultureInfo.InvariantCulture)));
    }
    private static async Task Partial()
    {
        await using var f = await Fixture.Create(upstream: true, configure: s => s with { BackgroundIdleSeconds = 0 }); InstallHandler(f);
        f.Runtime.Database.Write(DatabaseFile.Catalog, c => c.Execute("""
            UPDATE source_matches SET description='Longer retained cached description';
            CREATE TRIGGER fail_detail BEFORE UPDATE OF metadata_json ON source_matches WHEN NEW.description='brief' BEGIN SELECT RAISE(FAIL,'controlled authoritative failure'); END;
            """));
        var result = await Queue(f).WaitAsync(TimeSpan.FromSeconds(10)); Check.Equal("failed", result.State); Check.True(result.FailureTypes.Contains("SqliteException", StringComparison.Ordinal));
        Check.Equal(1L, Number(f, "SELECT COUNT(*) FROM source_matches WHERE source_title='Hydrated work' AND description='Longer retained cached description'"));
        Check.True(Number(f, "SELECT COUNT(*) FROM titles WHERE title='Hydrated work'") > 0); Check.Equal(1L, Number(f, "SELECT COUNT(*) FROM entries"));
    }
    private static async Task Bounded()
    {
        await using var f = await Fixture.Create(upstream: true, configure: s => s with { BackgroundIdleSeconds = 60 }); var calls = 0;
        f.Upstream!.DetailsHandler = (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(Details()); };
        using var interactive = f.Runtime.Lanes.BeginInteractive(); var completions = new List<Task<NativeHydrationResult>>();
        for (var i = 0; i < NativeMetadataHydration.MaximumPendingJobs; i++) completions.Add(Queue(f, eager: false, manga: Manga + i));
        Check.True(!f.Runtime.Catalogue.Hydration.TrySchedule(Source, Manga + NativeMetadataHydration.MaximumPendingJobs, out var rejected)); Check.True(rejected is null);
        Check.Equal(NativeMetadataHydration.MaximumPendingJobs, f.Runtime.Catalogue.Hydration.Pending); Check.Equal(0, f.Runtime.Lanes.Snapshot.BackgroundActive);
        await f.Runtime.Catalogue.Hydration.DisposeAsync();
        foreach (var completion in completions) Check.Equal("cancelled", (await completion).State);
        Check.Equal(0, calls); Check.Equal(0, f.Runtime.Catalogue.Hydration.Pending); Check.True(!f.Runtime.Catalogue.Hydration.Schedule(Source, Manga, eager: true));
    }
    private static async Task Hints()
    {
        await using var f = await Fixture.Create(configure: s => s with { CoverHintCacheMaxEntries = 1 });
        f.Runtime.Database.Write(DatabaseFile.Catalog, c =>
        {
            for (var i = 1; i <= 129; i++) c.Execute("INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,thumbnail_url,last_checked) VALUES($e,$s,$m,'Variant',$url,'then')",
                ("$e", Entry), ("$s", Source), ("$m", Manga + i), ("$url", "https://source.invalid/" + i.ToString(CultureInfo.InvariantCulture)));
            return 0;
        });
        for (var i = 1; i <= 129; i++) Check.Equal("https://source.invalid/" + i.ToString(CultureInfo.InvariantCulture), f.Runtime.Catalogue.Hydration.SourceCoverHint(Manga + i)!.ThumbnailUrl);
        f.Runtime.Database.Write(DatabaseFile.Catalog, c => c.Execute("UPDATE source_variants SET thumbnail_url='https://source.invalid/changed' WHERE source_manga_id=$m", ("$m", Manga + 1)));
        Check.Equal("https://source.invalid/changed", f.Runtime.Catalogue.Hydration.SourceCoverHint(Manga + 1)!.ThumbnailUrl);
        Check.Equal(Source, f.Runtime.Catalogue.Hydration.SourceCoverHint(Manga + 1)!.SourceId); Check.True(f.Runtime.Catalogue.Hydration.SourceCoverHint(1) is null);
    }
}
