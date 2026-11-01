using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Metadata.Hydration;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Tests.Catalogue.Metadata;
using AutoSourcarr.Tests.Matching;

namespace AutoSourcarr.Tests.Catalogue.Hydration;

/// <summary>Phase 10 metadata hydration: provider status, enrichment planning, MangaDex seeding, orphan repair and refresh.</summary>
public static class HydrationTests
{
    private sealed class Recording : IEntryIngestFollowUps
    {
        public List<string> Calls { get; } = [];
        public void RecordWritten(long entryId, string sourceId, long sourceMangaId, JsonObject manga) => Calls.Add("record:" + sourceId);
        public void ChooseMetadata(long entryId) => Calls.Add("choose");
        public void ScheduleHydration(string sourceId, long sourceMangaId) => Calls.Add("hydrate");
        public void ScheduleIdentity(long entryId) => Calls.Add("identity");
        public void Invalidate(long entryId) => Calls.Add("invalidate");
    }

    private static (Sandbox Temp, SqliteDatabase Database) Open()
    {
        var temp = new Sandbox();
        var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
        new SchemaMigrator(database).Initialize();
        return (temp, database);
    }

    public static void Register(TestSuite suite)
    {
        suite.Add("metadata hydration: provider status lists every provider with what is in use", () =>
        {
            var settings = new AutoSourcarrSettings { MetadataIdentityEnabled = true, MetadataIdentityProviders = "anilist,comicvine,kitsu" };
            using var anilist = new MetadataIdentityTests.FakeProvider("anilist", _ => []);
            var items = MetadataProviderStatus.Status(settings, [anilist]).OfType<JsonObject>().ToDictionary(item => item["key"]!.GetValue<string>());
            Check.Equal(6, items.Count);
            Check.True(items["anilist"]["available"]!.GetValue<bool>());
            Check.Equal("API key required", items["comicvine"]["reason"]!.GetValue<string>());
            Check.Equal("not available in this version", items["kitsu"]["reason"]!.GetValue<string>());
            Check.Equal("disabled", items["mangadex"]["reason"]!.GetValue<string>());
            Check.Equal("edition", items["mangadex"]["identity_scope"]!.GetValue<string>());
            var off = MetadataProviderStatus.Status(settings with { MetadataIdentityEnabled = false }, [anilist]).OfType<JsonObject>().First(item => item["key"]!.GetValue<string>() == "anilist");
            Check.True(!off["available"]!.GetValue<bool>());
        });

        suite.Add("metadata hydration: sparse payloads, the feed plan and background enrichment", () =>
        {
            var (temp, database) = Open();
            using (temp) using (database)
            {
                Check.True(MetadataEnrichment.IsSparse(new JsonObject { ["initialized"] = true, ["thumbnailUrl"] = "x", ["description"] = "short" }));
                Check.True(!MetadataEnrichment.IsSparse(new JsonObject
                {
                    ["initialized"] = true, ["thumbnailUrl"] = "https://c.test/1.jpg", ["description"] = new string('a', 45), ["author"] = "A", ["artist"] = "B",
                    ["genre"] = new JsonArray("Drama"), ["status"] = 1,
                }));
                var sparse = MatchingFixtures.Entry(database, "Sparse");
                var full = MatchingFixtures.Entry(database, "Full");
                database.Write(DatabaseFile.Catalog, s => s.Execute("UPDATE entries SET thumbnail_url='t',description=$d,author='a',artist='b',genre='g',status=1 WHERE id=$id",
                    ("$d", new string('d', 50)), ("$id", full)));
                database.Write(DatabaseFile.Catalog, s => s.Execute("INSERT INTO identity_links(entry_id,provider,external_id,last_checked) VALUES($id,'anilist','1','t')", ("$id", full)));
                var identities = new List<long>(); var enriched = new List<long>(); var done = new TaskCompletionSource();
                var settings = new AutoSourcarrSettings { DataDir = temp.Path, MetadataIdentityEnabled = true };
                var enrichment = new MetadataEnrichment(database, () => settings, new MetadataEnrichmentHooks
                {
                    ScheduleIdentity = id => { identities.Add(id); return true; },
                    Enrich = (id, _) => { lock (enriched) enriched.Add(id); done.TrySetResult(); return Task.FromResult(true); },
                });
                var (needIdentity, needDetail) = enrichment.Plan([sparse, full, sparse, 9999]);
                Check.True(needIdentity.SequenceEqual([sparse]));
                Check.True(needDetail.SequenceEqual([sparse]));
                Check.Equal(1, enrichment.QueueFeedAsync([sparse, full]).GetAwaiter().GetResult());
                done.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                Check.True(identities.SequenceEqual([sparse]));
                lock (enriched) Check.True(enriched.SequenceEqual([sparse]));
                enrichment.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Check.Equal(0, enrichment.Schedule([sparse]));
            }
        });

        suite.Add("metadata hydration: a MangaDex record URL seeds the exact MangaDex identity", () =>
        {
            var (temp, database) = Open();
            using (temp) using (database)
            {
                MatchingFixtures.Source(database, "md", "MangaDex", "en");
                MatchingFixtures.Source(database, "other", "Other", "en");
                var entry = MatchingFixtures.Entry(database, "Frieren");
                var inner = new Recording(); var added = new List<long>();
                var seed = new SourceIdentitySeed(database, new object(), inner) { IdentityAdded = added.Add };
                var manga = new JsonObject
                {
                    ["id"] = 5, ["title"] = "Frieren", ["url"] = "/title/b0b721ff-c388-4486-aa0f-c2b0bb321512", ["author"] = "Kanehito Yamada",
                    ["genre"] = "Adventure, Fantasy", ["status"] = 1, ["description"] = "An elf mage.",
                };
                Check.Equal("b0b721ff-c388-4486-aa0f-c2b0bb321512", SourceIdentitySeed.MangaDexTitleId(manga));
                seed.RecordWritten(entry, "other", 5, manga);
                Check.Equal(0L, database.Read(DatabaseFile.Catalog, s => (long)s.Scalar("SELECT COUNT(*) FROM identity_links")!)); // not a MangaDex source
                seed.RecordWritten(entry, "md", 5, manga);
                var link = database.Read(DatabaseFile.Catalog, s => s.Query("SELECT * FROM identity_links WHERE entry_id=$id", ("$id", entry)).Single());
                Check.Equal("mangadex", link.Text("provider"));
                Check.Equal("b0b721ff-c388-4486-aa0f-c2b0bb321512", link.Text("external_id"));
                Check.Equal("source-exact", link.Text("match_type"));
                Check.Equal(1.0, link.Number("confidence"));
                Check.Equal(1L, link.Integer("status"));
                Check.True(added.SequenceEqual([entry]));
                Check.True(inner.Calls.SequenceEqual(["record:other", "record:md"]));
                Check.True(!seed.Seed(entry, "md", new JsonObject { ["url"] = "https://example.test/manga/5" }));
            }
        });

        suite.Add("metadata hydration: the orphan identity repair resumes failures and finishes once", () =>
        {
            var (temp, database) = Open();
            using (temp) using (database)
            {
                var a = MatchingFixtures.Entry(database, "Orphan A");
                var b = MatchingFixtures.Entry(database, "Orphan B");
                MatchingFixtures.Source(database, "1", "Alpha", "en");
                var bound = MatchingFixtures.Entry(database, "Bound");
                MatchingFixtures.Record(database, bound, "1", 1, "Bound");
                var failing = new HashSet<long> { b }; var calls = new List<long>();
                var settings = new AutoSourcarrSettings { DataDir = temp.Path, MetadataIdentityEnabled = true, MetadataIdentityConcurrency = 1 };
                var repair = new OrphanIdentityRepair(database, () => settings, (id, force, _) =>
                {
                    lock (calls) calls.Add(id);
                    return Task.FromResult(failing.Contains(id)
                        ? new JsonObject { ["matched"] = 0, ["provider_errors"] = new JsonObject { ["anilist"] = "429" } }
                        : new JsonObject { ["matched"] = 0 });
                }, () => true);
                var first = repair.RunAsync(false, 100, null, CancellationToken.None).GetAwaiter().GetResult();
                Check.True(first["pending"]!.GetValue<bool>());
                Check.Equal(2, first["attempted"]!.GetValue<int>());
                Check.True(!calls.Contains(bound));
                failing.Clear(); calls.Clear();
                var second = repair.RunAsync(false, 100, null, CancellationToken.None).GetAwaiter().GetResult();
                Check.True(calls.SequenceEqual([b])); // only the failed one is retried
                Check.True(!second["pending"]!.GetValue<bool>());
                Check.True(repair.RunAsync(false, 100, null, CancellationToken.None).GetAwaiter().GetResult()["skipped"]!.GetValue<bool>());
                Check.True(new OrphanIdentityRepair(database, () => settings, (_, _, _) => Task.FromResult(new JsonObject()), () => false)
                    .RunAsync(true, 100, null, CancellationToken.None).GetAwaiter().GetResult()["skipped"]!.GetValue<bool>());
                _ = a;
            }
        });

        suite.Add("metadata hydration: refresh metadata enriches only while sparse", () =>
        {
            var sparse = true; var enriched = 0;
            var refresh = new EntryMetadataRefresh
            {
                RefreshDetails = (id, _) => Task.FromResult(new JsonObject { ["id"] = id, ["from"] = "refresh" }),
                NeedsMetadata = _ => sparse,
                Enrich = (_, _) => { enriched++; sparse = false; return Task.FromResult(true); },
                Entry = id => new JsonObject { ["id"] = id, ["from"] = "entry" },
            };
            var result = refresh.RefreshAsync(7, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal("entry", result["entry"]!["from"]!.GetValue<string>());
            Check.True(!result["still_sparse"]!.GetValue<bool>());
            var again = refresh.RefreshAsync(7, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal("refresh", again["entry"]!["from"]!.GetValue<string>());
            Check.Equal(1, enriched);
        });
    }
}
