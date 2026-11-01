using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Tags;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Tests.Matching;

namespace AutoSourcarr.Tests.Catalogue.Tags;

/// <summary>Phase 10 tags page: groups, suggestions, reviews (decision log), entry tags and the reindex job.</summary>
public static class TagCatalogueTests
{
    private static void Tags(SqliteDatabase database) => database.Write(DatabaseFile.Catalog, c => c.Execute("""
        INSERT INTO tags(id,namespace,name,normalized) VALUES
          (1,'tag','Spaceship','spaceship'),(2,'tag','Spaceships','spaceships'),(3,'tag','Sci-Fi','sci-fi'),(4,'tag','Science Fiction','science fiction'),
          (5,'artist','Spaceship','spaceship'),(6,'tag','Romance','romance');
        """));

    public static void Register(TestSuite suite)
    {
        suite.Add("tag catalogue: difflib ratio matches Python", () =>
        {
            Check.Equal(0.947, Math.Round(TagCatalogue.Ratio("spaceship", "spaceships"), 3));
            Check.Equal(0.75, TagCatalogue.Ratio("abcd", "bcde"));
            Check.Equal(1.0, TagCatalogue.Ratio("", ""));
            Check.Equal(0.0, TagCatalogue.Ratio("abc", "xyz"));
            Check.Equal(0.5, Math.Round(TagCatalogue.Ratio("romance", "drama"), 3)); // difflib: 2*3/12
        });

        suite.Add("tag catalogue: groups, counts, suggestions and reviews with the decision log", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            Tags(database);
            MatchingFixtures.Source(database, "1", "Alpha", "en");
            var entry = MatchingFixtures.Entry(database, "Planetes");
            MatchingFixtures.Record(database, entry, "1", 11, "Planetes");
            database.Write(DatabaseFile.Catalog, c => c.Execute("INSERT INTO source_record_tags VALUES('1',11,1),('1',11,3)"));
            var log = new MemoryDecisionLog(); var changed = 0;
            var tags = new TagCatalogue(database, new object(), log) { Changed = () => changed++ };

            var page = tags.List();
            Check.Equal(6, page["total"]!.GetValue<int>());
            var scifi = page["items"]!.AsArray().OfType<JsonObject>().Single(row => row["id"]!.GetValue<long>() == 3);
            var science = page["items"]!.AsArray().OfType<JsonObject>().Single(row => row["id"]!.GetValue<long>() == 4);
            Check.Equal(scifi["canonical_id"]!.GetValue<long>(), science["canonical_id"]!.GetValue<long>()); // built-in synonym
            Check.Equal(1, scifi["aliases"]!.AsArray().Count);
            Check.Equal(1L, scifi["entry_count"]!.GetValue<long>());
            Check.Equal(3, tags.List("space")["total"]!.GetValue<int>()); // two tags plus the artist name
            Check.Equal(1, tags.List("", 1, 5)["items"]!.AsArray().Count);

            var suggestions = tags.Suggestions()["items"]!.AsArray().OfType<JsonObject>().ToArray();
            Check.Equal(1, suggestions.Length); // never across namespaces, never already equivalent
            Check.Equal(1L, suggestions[0]["left"]!["id"]!.GetValue<long>());
            Check.Equal(0.947, suggestions[0]["score"]!.GetValue<double>());

            tags.Review(2, 1, "same", DecisionActor.User("owner"));
            Check.Equal(1, changed);
            Check.Equal("tags.review", log.Events.Single().Kind);
            Check.Equal("owner", log.Events.Single().Actor.Name);
            Check.Equal(0, tags.Suggestions()["items"]!.AsArray().Count);
            var reviews = tags.Reviews()["items"]!.AsArray();
            Check.Equal("same", reviews[0]!["decision"]!.GetValue<string>());
            Check.Equal(1L, reviews[0]!["left"]!["id"]!.GetValue<long>());

            Check.Throws<TagReviewException>(() => tags.Review(1, 5, "same", DecisionActor.User("owner"))); // different namespaces
            Check.Throws<TagReviewException>(() => tags.Review(1, 404, "same", DecisionActor.User("owner")));
            Check.Throws<TagReviewException>(() => tags.Review(1, 1, "same", DecisionActor.User("owner")));
            tags.Review(1, 6, "different", DecisionActor.User("owner"));
            Check.Throws<TagReviewException>(() => tags.Review(2, 6, "same", DecisionActor.User("owner"))); // contradicts Separate
            Check.Equal(2, tags.Reviews()["items"]!.AsArray().Count); // the refused one rolled back
            tags.Review(1, 2, "clear", DecisionActor.User("owner"));
            Check.Equal(1, tags.Reviews()["items"]!.AsArray().Count);

            var store = new NativeTagStore(database, new object());
            var own = tags.EntryTags(store, entry);
            Check.Equal(2, own["tags"]!.AsArray().Count);
            Check.Equal("catalogue", own["metadata_basis"]!.GetValue<string>());
            Check.Throws<KeyNotFoundException>(() => tags.EntryTags(store, 9999));
        });

        suite.Add("tag catalogue: the reindex runs as one job and marks the index current", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            MatchingFixtures.Source(database, "1", "Alpha", "en");
            var entry = MatchingFixtures.Entry(database, "Planetes");
            MatchingFixtures.Record(database, entry, "1", 11, "Planetes");
            database.Write(DatabaseFile.Catalog, c => c.Execute("UPDATE source_matches SET genre='Sci-Fi, Drama',initialized=1,metadata_json='{\"genre\":[\"Sci-Fi\",\"Drama\"],\"initialized\":true}'"));
            var gate = new object();
            var tags = new TagCatalogue(database, gate, new MemoryDecisionLog());
            var queue = new JobQueue(new TaskRegistry());
            try
            {
                Check.True(!tags.IndexCurrent());
                var answer = tags.QueueReindex(queue, new NativeTagStore(database, gate));
                Check.Equal("queued", answer["state"]!.GetValue<string>());
                for (var attempt = 0; attempt < 250 && !tags.IndexCurrent(); attempt++) Thread.Sleep(20);
                Check.True(tags.IndexCurrent());
                Check.True(tags.List()["total"]!.GetValue<int>() >= 2);
            }
            finally { queue.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }
}
