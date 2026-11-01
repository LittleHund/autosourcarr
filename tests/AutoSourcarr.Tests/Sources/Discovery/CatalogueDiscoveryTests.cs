using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources.Discovery;
using AutoSourcarr.Tests.Matching;

namespace AutoSourcarr.Tests.Sources.Discovery;

/// <summary>Phase 9: the catalogue-wide discovery jobs over a real catalogue database with a fake source host.</summary>
public static class CatalogueDiscoveryTests
{
    private sealed class FakeSource : ISourceCatalogueClient
    {
        private readonly Lock _gate = new();
        private readonly List<string> _calls = [];
        public Func<string, string, string, JsonArray> Browse { get; init; } = (_, _, _) => new JsonArray();
        public Dictionary<long, JsonObject> Details { get; } = [];
        public IReadOnlyList<string> Calls { get { lock (_gate) return _calls.ToArray(); } }
        public void Reset() { lock (_gate) _calls.Clear(); }

        public Task<JsonObject> BrowseAsync(string sourceId, string mode, int page, string query, CancellationToken cancellationToken)
        {
            lock (_gate) _calls.Add(mode + ":" + sourceId);
            return Task.FromResult(new JsonObject { ["mangas"] = Browse(sourceId, mode, query), ["hasNextPage"] = false });
        }

        public Task<JsonObject> MangaAsync(long mangaId, bool details, bool chapters, CancellationToken cancellationToken)
        {
            lock (_gate) _calls.Add("detail:" + mangaId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Details.TryGetValue(mangaId, out var manga)
                ? Task.FromResult(new JsonObject { ["manga"] = manga.DeepClone(), ["chapters"] = new JsonArray() })
                : throw new InvalidOperationException("unknown manga");
        }
    }

    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _temp = new();
        public SqliteDatabase Database { get; }
        public object Gate { get; } = new();
        public EntryIngest Ingest { get; }
        public FakeSource Source { get; }
        public CatalogueDiscovery Discovery { get; }
        public List<(long, string, long)> Attached { get; } = [];

        public Setup(FakeSource source)
        {
            Database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = _temp.Path });
            new SchemaMigrator(Database).Initialize();
            MatchingFixtures.Source(Database, "1", "Alpha", "en");
            MatchingFixtures.Source(Database, "2", "Beta", "en");
            MatchingFixtures.Source(Database, "3", "Gamma", "en");
            Ingest = new EntryIngest(Database, Gate, new InMemoryDecisionLog());
            Source = source;
            var settings = new AutoSourcarrSettings { DataDir = _temp.Path, BrowsePrefetchPages = 1 };
            Discovery = new CatalogueDiscovery(Database, Gate, Ingest, source, () => settings)
            { RecordAttached = (entry, sourceId, manga) => Attached.Add((entry, sourceId, manga)) };
        }

        public string Outcome(long entry, string source) => Database.Read(DatabaseFile.Catalog, session =>
            session.Scalar("SELECT state FROM source_discovery_outcomes WHERE entry_id=$e AND source_id=$s", ("$e", entry), ("$s", source)) as string ?? "");

        public void Dispose() { Database.Dispose(); _temp.Dispose(); }
    }

    private const string Title = "The Apothecary Diaries";
    private const string Author = "Natsu Hyuuga";

    public static void Register(TestSuite suite)
    {
        suite.Add("discovery: the feed crawl ingests source feeds and saves the default browse feeds", () =>
        {
            var source = new FakeSource
            {
                Browse = (sourceId, mode, _) => (sourceId, mode) switch
                {
                    ("1", "POPULAR") => new JsonArray(MatchingFixtures.Manga(11, Title, Author)),
                    ("2", "POPULAR") => new JsonArray(MatchingFixtures.Manga(21, Title, Author)),
                    ("2", "LATEST") => throw new InvalidOperationException("source 2 is down"),
                    _ => new JsonArray(),
                },
            };
            using var setup = new Setup(source);
            var progress = new List<string>(); var failures = new List<string>();
            var discovery = new CatalogueDiscovery(setup.Database, setup.Gate, setup.Ingest, source,
                () => new AutoSourcarrSettings { BrowsePrefetchPages = 1 }) { SourceResult = (id, error) => { if (error is not null) failures.Add(id); } };
            var result = discovery.RefreshFeedsAsync((_, _, message) => progress.Add(message ?? ""), CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal(2, result["feeds"]!.GetValue<int>());
            Check.Equal(1, result["cataloged_entries"]!.GetValue<int>());
            Check.Equal("2", failures.Single());
            var entry = setup.Ingest.FindExistingBySource("1", 11)!.Value;
            Check.Equal(entry, setup.Ingest.FindExistingBySource("2", 21) ?? 0);
            var rows = setup.Database.Read(DatabaseFile.Catalog, session => session.Query("SELECT cache_key,mode,entry_ids_json,source_errors_json FROM browse_cache ORDER BY mode"));
            Check.Equal(2, rows.Count);
            Check.Equal(CatalogueDiscovery.FeedCacheKey("LATEST", 1, 50), rows[0].Text("cache_key"));
            Check.Equal("[]", rows[0].Text("entry_ids_json"));
            Check.True(rows[0].Text("source_errors_json").Contains("source 2 is down", StringComparison.Ordinal));
            Check.Equal("[" + entry.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]", rows[1].Text("entry_ids_json"));
            Check.Equal("{\"exact_match_count\":null,\"min_matches\":1,\"mode\":\"POPULAR\",\"page\":1,\"page_size\":50,\"query\":\"\",\"sort\":\"relevance\",\"source_ids\":[]}",
                rows[1].Text("cache_key"));
            Check.True(progress.Count >= 4);

            // A cancelled crawl stops between source requests.
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check.Throws<OperationCanceledException>(() => discovery.RefreshFeedsAsync(null, cancelled.Token).GetAwaiter().GetResult());
        });

        suite.Add("discovery: missing and additional records attach only on live, strict evidence; outcomes resume", () =>
        {
            var source = new FakeSource
            {
                Browse = (sourceId, mode, _) => (sourceId, mode) switch
                {
                    ("1", "SEARCH") => new JsonArray(MatchingFixtures.Manga(11, Title, Author), MatchingFixtures.Manga(13, Title, Author), MatchingFixtures.Manga(14, "Completely Different Story")),
                    ("2", "SEARCH") => new JsonArray(MatchingFixtures.Manga(22, Title, Author)),
                    ("3", "SEARCH") => throw new InvalidOperationException("gamma failed"),
                    _ => new JsonArray(),
                },
            };
            source.Details[22] = MatchingFixtures.Manga(22, Title, Author, "Maomao works in the palace.");
            source.Details[13] = MatchingFixtures.Manga(13, Title, Author, "Maomao works in the palace.");
            using var setup = new Setup(source);
            var entry = MatchingFixtures.Entry(setup.Database, Title, Author);
            MatchingFixtures.Record(setup.Database, entry, "1", 11, Title, Author);

            var missing = setup.Discovery.DiscoverMissingAsync(entry, exhaustive: false, priorityRebuild: false, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal(1, missing["added"]!.GetValue<int>());
            Check.Equal(1, (missing["errors"] as JsonArray)!.Count);
            Check.Equal(entry, setup.Ingest.FindExistingBySource("2", 22) ?? 0);
            Check.Equal("matched", setup.Outcome(entry, "2"));
            Check.Equal("unavailable", setup.Outcome(entry, "3"));
            Check.Equal((entry, "2", 22L), setup.Attached.Single());

            var additional = setup.Discovery.DiscoverAdditionalAsync(entry, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal(1, additional["added"]!.GetValue<int>());
            Check.True(setup.Database.Read(DatabaseFile.Catalog, session =>
                session.Scalar("SELECT 1 FROM source_variants WHERE entry_id=$e AND source_id='1' AND source_manga_id=13 AND active=1", ("$e", entry))) is not null);
            // The unrelated title was never fetched; the entry's own record was not re-attached.
            Check.True(!source.Calls.Contains("detail:14") && !source.Calls.Contains("detail:11"));

            // An exhaustive retry visits the sources the last run did not reach first.
            MatchingFixtures.Source(setup.Database, "4", "Delta", "en");
            source.Reset();
            setup.Discovery.DiscoverMissingAsync(entry, exhaustive: true, priorityRebuild: false, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal("SEARCH:4", source.Calls.First(call => call.StartsWith("SEARCH:", StringComparison.Ordinal)));
            Check.Equal("no_results", setup.Outcome(entry, "4"));
        });

        suite.Add("discovery: Subbed order, repair-unmatched and the catalogue scan", () =>
        {
            var source = new FakeSource
            {
                Browse = (sourceId, mode, _) => (sourceId, mode) switch
                {
                    ("2", "SEARCH") => new JsonArray(MatchingFixtures.Manga(22, Title, Author)),
                    _ => new JsonArray(),
                },
            };
            source.Details[22] = MatchingFixtures.Manga(22, Title, Author, "Maomao works in the palace.");
            using var setup = new Setup(source);
            var covered = MatchingFixtures.Entry(setup.Database, Title, Author);
            MatchingFixtures.Record(setup.Database, covered, "1", 11, Title, Author);
            var orphan = MatchingFixtures.Entry(setup.Database, "Frieren Beyond Journeys End");

            Check.Equal(string.Join(',', orphan, covered), string.Join(',', setup.Discovery.SubbedOrder([covered, orphan, covered, 0, 99999], unmatchedOnly: false)));
            Check.Equal(orphan, setup.Discovery.SubbedOrder([covered, orphan], unmatchedOnly: true).Single());

            var progress = new List<(int?, int?)>();
            var repair = setup.Discovery.SearchSubbedAsync([orphan], unmatchedOnly: true, (current, total, _) => progress.Add((current, total)), CancellationToken.None).GetAwaiter().GetResult();
            Check.True(repair["message"]!.GetValue<string>().StartsWith("Repair unmatched complete", StringComparison.Ordinal));
            Check.Equal(((int?)1, (int?)1), progress[^1]);
            Check.Equal("no_results", setup.Outcome(orphan, "1"));

            var all = setup.Discovery.SearchSubbedAsync([covered], unmatchedOnly: false, null, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal(1, all["added"]!.GetValue<int>());
            Check.Equal(covered, setup.Ingest.FindExistingBySource("2", 22) ?? 0);

            Check.Equal(covered, setup.Discovery.AdditionalScanEntries(10).Single());
            var scan = setup.Discovery.ScanAdditionalAsync([covered], null, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal(0, scan["added"]!.GetValue<int>());
            Check.Equal(0, scan["failed"]!.GetValue<int>());

            // Sparse-metadata selection, and repair through the best record's live detail.
            Check.True(setup.Discovery.SparseEntries(10).Contains(covered));
            source.Details[11] = MatchingFixtures.Manga(11, Title, Author, "A long enough description of the apothecary and the palace intrigue.");
            var chosen = new List<long>();
            var enrich = new CatalogueDiscovery(setup.Database, setup.Gate, setup.Ingest, source, () => new AutoSourcarrSettings { MetadataIdentityEnabled = false }) { ChooseMetadata = chosen.Add }
                .EnrichMetadataAsync([covered], null, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal(1, enrich["completed"]!.GetValue<int>());
            Check.Equal(0, enrich["failed"]!.GetValue<int>());
            Check.True(chosen.Contains(covered));
            Check.True(setup.Database.Read(DatabaseFile.Catalog, session => session.Scalar("SELECT description FROM source_matches WHERE source_id='1' AND source_manga_id=11") as string ?? "")
                .StartsWith("A long enough description", StringComparison.Ordinal));
        });
    }
}
