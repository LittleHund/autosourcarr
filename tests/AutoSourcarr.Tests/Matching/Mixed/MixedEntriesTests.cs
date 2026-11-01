using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Tags;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Mixed;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests.Matching;

/// <summary>Phase 8D: edition decisions from more than the title (MAT-3), split proposals, reviews and the scaled check.</summary>
public static class MixedEntriesTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("mixed entries: a plain-titled entry is colored from its sampled pages, a monochrome one conflicts with it", () =>
        {
            using var f = new Fixture();
            f.Entry(1, "Plain Title"); f.Record("source_matches", 1, "10", 101, "Plain Title");
            f.Entry(2, "Plain Title"); f.Record("source_matches", 2, "11", 102, "Plain Title");
            var pages = new FakePages();
            pages.Pages[("10", 101)] = Enumerable.Range(0, 4).Select(_ => Colored(64, 64, 800, 1200)).ToArray();
            pages.Pages[("11", 102)] = Enumerable.Range(0, 4).Select(_ => Gray(64, 64, 800, 1200)).ToArray();
            var editions = new EditionProfiles(f.Database, () => new EditionOptions(), pages);
            Check.Equal("unknown", editions.Profile(1).Color.Mode); // the title says nothing
            var colored = editions.AnalyzeSourceColorAsync("10", 101, "Plain Title", false, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal("colored", colored["color_mode"]!.GetValue<string>());
            Check.True(colored["evidence"]!.GetValue<string>().StartsWith("chapter-pages", StringComparison.Ordinal));
            editions.AnalyzeSourceColorAsync("11", 102, "Plain Title", false, CancellationToken.None).GetAwaiter().GetResult();
            var profile = editions.Recompute(1);
            Check.Equal("colored", profile.Color.Mode);
            Check.True(profile.Color.Evidence.Any(signal => signal.Evidence == "chapter-pages"), "page evidence is named");
            Check.Equal("monochrome", editions.Recompute(2).Color.Mode);
            Check.Equal("colored", f.Scalar("SELECT color_mode FROM entries WHERE id=1"));
            var verdict = editions.CompareEntries(1, 2);
            Check.True(verdict.Conflict, "colored and monochrome editions stay separate");
            Check.Equal("color-edition-conflict:colored!=monochrome", verdict.Reason);
            // The page shape is media evidence too: tall strips are a webtoon.
            pages.Pages[("12", 103)] = Enumerable.Range(0, 3).Select(_ => Colored(32, 64, 800, 12000)).ToArray();
            editions.AnalyzeSourceColorAsync("12", 103, "Strip", false, CancellationToken.None).GetAwaiter().GetResult();
            var strip = editions.CompareCandidates(new EditionCandidate("12", 103, "Strip"), new EditionCandidate("13", 104, "Strip", Genre: "Manga"));
            Check.True(strip.Conflict, "a long-strip record and a manga-genre record are different formats");
            // The automatic attachment gate uses the same evidence: a monochrome-labelled candidate cannot join the colored entry.
            var gate = f.Database.Read(DatabaseFile.Catalog, c => editions.GateCandidate(c, 1, new EditionCandidate("99", 1, "Plain Title (Black and White)")));
            Check.Equal("color-edition-conflict:colored!=monochrome", gate.Reason);
            Check.True(f.Database.Read(DatabaseFile.Catalog, c => editions.GateCandidate(c, 1, new EditionCandidate("98", 2, "Plain Title"))).Reason.StartsWith("color-edition-unresolved:", StringComparison.Ordinal), "an unknown candidate waits for page analysis");
        });

        suite.Add("mixed entries: tags, description and a webtoon-only source decide editions without a title marker", () =>
        {
            var facts = new EditionRecordFacts("1", 1, "Some Work", "Action, Full Color", "The official digital colored comics edition of the hit series.", [], "Src", "https://example.org", null);
            var color = EditionSignals.Decide(EditionSignals.Color(facts));
            Check.Equal("colored", color.Mode);
            Check.True(color.Confidence >= .78, "a tag and a description cue add up above the default floor");
            var webtoon = EditionSignals.Decide(EditionSignals.Media(facts with { SourceBaseUrl = "https://www.tapas.io/" }));
            Check.Equal("webtoon", webtoon.Mode);
            Check.Equal("unknown", EditionSignals.Decide(EditionSignals.Color(facts with { Genre = "Action", Description = "" })).Mode);
            // An explicit monochrome title outweighs a weaker colored tag.
            var title = EditionSignals.Decide(EditionSignals.Color(facts with { Title = "Some Work (Black and White)", Description = "" }));
            Check.Equal("monochrome", title.Mode);
            Check.Equal(PageColorAnalysis.SampleIndexes(30, 4).Count, 4);
        });

        suite.Add("mixed entries: the automatic split proposal moves the opposite automatic edition and leaves manual records for review", () =>
        {
            using var f = new Fixture();
            f.Entry(1, "Shared Title");
            f.Record("source_matches", 1, "20", 201, "Shared Title"); f.Record("source_variants", 1, "21", 202, "Shared Title");
            f.Record("source_variants", 1, "22", 203, "Shared Title");
            f.Profile("20", 201, "monochrome", .95); f.Profile("21", 202, "colored", .99); f.Profile("22", 203, "monochrome", .9);
            var mixed = f.Mixed();
            var proposal = f.Database.Read(DatabaseFile.Catalog, c => mixed.ProposeSplit(c, 1, "color"));
            Check.True(proposal is not null && !proposal.Blocked, "an unblocked proposal exists");
            Check.Equal("monochrome", proposal!.KeepMode);
            Check.Equal("21:202", string.Join(',', proposal.Move.Select(item => item.SourceId + ":" + item.MangaId.ToString(CultureInfo.InvariantCulture))));
            Check.True(f.Database.Read(DatabaseFile.Catalog, c => mixed.ProposeSplit(c, 1, "media")) is null, "no media conflict");
            var detail = mixed.Detail(1);
            Check.Equal("needs_review", detail["state"]!.GetValue<string>());
            Check.True(detail["evidence"]!.AsArray().Any(pair => pair!["reasons"]!.AsArray().Any(reason => reason!.GetValue<string>() == "Different color editions")), "pair evidence");
            Check.Equal(1, detail["split_proposals"]!.AsArray().Count);
            f.Execute("UPDATE source_variants SET locked=1 WHERE source_manga_id=202");
            var blocked = f.Database.Read(DatabaseFile.Catalog, c => mixed.ProposeSplit(c, 1, "color"));
            Check.True(blocked is not null && blocked.Blocked, "a locked record is never moved automatically");
            Check.True(mixed.AutoSplit(1, "color") is null, "a blocked proposal is not applied");
        });

        suite.Add("mixed entries: a kept-together review stops automatic splits, also when it arrives while the split waits for the gate", () =>
        {
            using var f = new Fixture();
            f.Entry(1, "Shared Title");
            f.Record("source_matches", 1, "20", 201, "Shared Title"); f.Record("source_variants", 1, "21", 202, "Shared Title");
            f.Profile("20", 201, "monochrome", .95); f.Profile("21", 202, "colored", .99);
            var splitter = new FakeSplitter(f.Database);
            var mixed = new MixedEntries(f.Database, new EditionProfiles(f.Database, () => new EditionOptions()), f.Log, splitter);
            var fingerprint = mixed.Detail(1)["fingerprint"]!.GetValue<string>();
            Check.Equal(0L, f.Count("SELECT COUNT(*) FROM mixed_entry_findings")); // viewing the detail writes nothing
            // A review that arrives after the proposal is seen under the write gate: nothing is split.
            splitter.BeforeCheck = () => mixed.Review(1, fingerprint, "keep", DecisionActor.User("owner"));
            Check.True(mixed.AutoSplit(1, "color") is null, "a review arriving before the split refuses it");
            Check.Equal(0, splitter.Splits);
            splitter.BeforeCheck = null;
            // The scaled check with automatic splits skips the reviewed entry, and so does a direct automatic split.
            mixed.StartCheck(restart: true, autoSplit: true);
            var batch = mixed.CheckBatch();
            Check.Equal(0, batch.Splits);
            Check.True(mixed.AutoSplit(1, "color") is null, "a kept-together entry is never split automatically");
            Check.Equal(0, splitter.Splits);
            // Once the review is cleared the same split goes ahead.
            mixed.Review(1, fingerprint, "clear", DecisionActor.User("owner"));
            Check.True(mixed.AutoSplit(1, "color") is not null, "without the review the split is applied");
            Check.Equal(1, splitter.Splits);
        });

        suite.Add("mixed entries: a review applies only to the exact evidence and is logged with its actor", () =>
        {
            using var f = new Fixture();
            f.Entry(1, "Work"); f.Record("source_matches", 1, "30", 301, "Work (Webtoon)"); f.Record("source_variants", 1, "31", 302, "Work", genre: "Manga");
            var mixed = f.Mixed();
            var detail = mixed.Detail(1); var fingerprint = detail["fingerprint"]!.GetValue<string>();
            Check.Equal(64, fingerprint.Length);
            Check.Throws<InvalidOperationException>(() => mixed.Review(1, new string('0', 64), "keep", DecisionActor.User("owner")));
            Check.Throws<ArgumentException>(() => mixed.Review(1, fingerprint, "split", DecisionActor.User("owner")));
            mixed.Review(1, fingerprint, "keep", DecisionActor.User("owner"));
            var list = mixed.Candidates("all");
            Check.Equal("reviewed", list["items"]![0]!["state"]!.GetValue<string>());
            Check.Equal(1L, list["counts"]!["reviewed"]!.GetValue<long>());
            Check.True(f.Log.Events.Any(item => item.Kind == "review.mixed" && item.Actor.Name == "owner"), "decision logged");
            f.Execute("UPDATE source_variants SET source_title='Work (Manga)' WHERE source_manga_id=302");
            Check.Equal("needs_review", mixed.Detail(1)["state"]!.GetValue<string>()); // new evidence needs a new review
        });

        suite.Add("mixed entries: the paged check over 3000 entries stays within its batch bound and resumes after a restart", () =>
        {
            using var f = new Fixture();
            f.Database.Write(DatabaseFile.Catalog, c =>
            {
                for (var id = 1; id <= 3000; id++)
                {
                    c.Execute("INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES($id,$title,'t','t')", ("$id", id), ("$title", "Work " + id.ToString(CultureInfo.InvariantCulture)));
                    c.Execute("INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,genre,last_checked) VALUES($id,'40',$id,$title,'','t')", ("$id", id), ("$title", "Work " + id.ToString(CultureInfo.InvariantCulture) + (id % 10 == 0 ? " (Webtoon)" : "")));
                    if (id % 10 == 0)
                        c.Execute("INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,genre,last_checked) VALUES($id,'41',$id,$title,'Manga','t')", ("$id", id), ("$title", "Work " + id.ToString(CultureInfo.InvariantCulture)));
                }
                return 0;
            });
            const int Batch = 250;
            var mixed = f.Mixed(); mixed.StartCheck(restart: true, autoSplit: false);
            var loads = new List<int>(); var batches = new List<MixedCheckBatch>();
            for (var i = 0; i < 3; i++) batches.Add(mixed.CheckBatch(Batch, CancellationToken.None, loads.Add));
            Check.Equal(750L, mixed.CheckStatus()["cursor"]!.GetValue<long>());
            // A new owner (an app restart) resumes from the saved cursor instead of starting over.
            var resumed = f.Mixed(); resumed.StartCheck(restart: false, autoSplit: false);
            Check.Equal(750L, resumed.CheckStatus()["cursor"]!.GetValue<long>());
            while (true) { var batch = resumed.CheckBatch(Batch, CancellationToken.None, loads.Add); batches.Add(batch); if (batch.Done) break; }
            Check.Equal(12, batches.Count);
            Check.True(batches.All(batch => batch.Entries <= Batch), "every batch reads at most the batch size of entries");
            Check.True(loads.All(count => count <= Batch * MixedEntries.MaximumRecordsPerEntry) && loads.Max() <= 50, "records loaded per batch stay bounded");
            Check.Equal(300, batches.Sum(batch => batch.MultiRecordEntries));
            var status = resumed.CheckStatus();
            Check.Equal(3000L, status["checked"]!.GetValue<long>());
            Check.True(status["completed_at"] is not null, "completed");
            var list = resumed.Candidates("needs_review", "", 0, 50);
            Check.Equal(300L, list["total"]!.GetValue<long>());
            Check.Equal(50, list["items"]!.AsArray().Count);
            Check.Equal(1L, resumed.Candidates("all", "work 2990", 0, 50)["total"]!.GetValue<long>());
            Check.Throws<InvalidOperationException>(() => resumed.CheckBatch(Batch)); // a finished run needs a new start
        });
    }

    private static PageRaster Colored(int width, int height, int pageWidth, int pageHeight)
    {
        byte[][] palette = [[220, 30, 30], [30, 200, 30], [30, 30, 220], [220, 200, 30], [200, 30, 200], [30, 200, 200]];
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++) { var color = palette[i % palette.Length]; rgb[i * 3] = color[0]; rgb[i * 3 + 1] = color[1]; rgb[i * 3 + 2] = color[2]; }
        return new PageRaster(pageWidth, pageHeight, rgb, width, height);
    }

    private static PageRaster Gray(int width, int height, int pageWidth, int pageHeight)
    {
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(100 + i / 3 % 60);
        return new PageRaster(pageWidth, pageHeight, rgb, width, height);
    }

    private sealed class FakePages : IPageImageSource
    {
        public Dictionary<(string, long), PageRaster[]> Pages { get; } = [];
        public Task<IReadOnlyList<PageRaster>> SamplePagesAsync(string sourceId, long mangaId, int pages, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PageRaster>>(Pages.TryGetValue((sourceId, mangaId), out var found) ? found.Take(pages).ToArray() : []);
    }

    /// <summary>A splitter that evaluates the precondition like CatalogueStructure (under its gate) and only counts splits.</summary>
    private sealed class FakeSplitter(SqliteDatabase database) : IEditionSplitter
    {
        public int Splits { get; private set; }
        public Action? BeforeCheck { get; set; }
        public (long OriginalEntryId, long NewEntryId) SplitAutomatic(long entryId, IReadOnlyList<(string SourceId, long MangaId)> records, string splitMatchType, CancellationToken cancellationToken) =>
            SplitAutomatic(entryId, records, splitMatchType, _ => true, cancellationToken);
        public (long OriginalEntryId, long NewEntryId) SplitAutomatic(long entryId, IReadOnlyList<(string SourceId, long MangaId)> records, string splitMatchType,
            Func<DatabaseSession, bool> stillAllowed, CancellationToken cancellationToken)
        {
            BeforeCheck?.Invoke();
            if (!database.Read(DatabaseFile.Catalog, stillAllowed)) throw new InvalidOperationException("The automatic split is no longer allowed");
            Splits++;
            return (entryId, 0);
        }
    }

    internal sealed class RecordingLog : IDecisionLog
    {
        public List<DecisionEvent> Events { get; } = [];
        public long Record(DecisionEvent decision) { Events.Add(decision); return Events.Count; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Sandbox _sandbox = new();
        public SqliteDatabase Database { get; }
        public RecordingLog Log { get; } = new();
        public Fixture()
        {
            Database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = _sandbox.Path });
            new SchemaMigrator(Database).Initialize();
        }
        public MixedEntries Mixed() => new(Database, new EditionProfiles(Database, () => new EditionOptions()), Log);
        public void Execute(string sql) => Database.Write(DatabaseFile.Catalog, c => c.Execute(sql));
        public string? Scalar(string sql) => Database.Read(DatabaseFile.Catalog, c => c.Scalar(sql) as string);
        public long Count(string sql) => Convert.ToInt64(Database.Read(DatabaseFile.Catalog, c => c.Scalar(sql)), CultureInfo.InvariantCulture);
        public void Entry(long id, string title) => Database.Write(DatabaseFile.Catalog, c =>
            c.Execute("INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES($id,$title,'t','t')", ("$id", id), ("$title", title)));
        public void Record(string table, long entry, string source, long manga, string title, string genre = "") => Database.Write(DatabaseFile.Catalog, c =>
            c.Execute("INSERT INTO " + table + "(entry_id,source_id,source_manga_id,source_title,genre,last_checked) VALUES($entry,$source,$manga,$title,$genre,'t')",
                ("$entry", entry), ("$source", source), ("$manga", manga), ("$title", title), ("$genre", genre)));
        public void Profile(string source, long manga, string mode, double confidence) => Database.Write(DatabaseFile.Catalog, c =>
            c.Execute("INSERT INTO source_color_profiles(source_id,source_manga_id,color_mode,confidence,sampled_pages,evidence,colored_fraction,analyzed_at) VALUES($s,$m,$mode,$c,4,'chapter-pages:sampled;paged',0,'t')",
                ("$s", source), ("$m", manga), ("$mode", mode), ("$c", confidence)));
        public void Dispose() { Database.Dispose(); _sandbox.Dispose(); }
    }
}
