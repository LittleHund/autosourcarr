using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Catalogue.Tags;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Mixed;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests;

/// <summary>Phase 8D tag synonyms: built-in defaults, user edits in the database, reviewed tag pairs, and their use as edition evidence.</summary>
public static class TagSynonymsTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("tag synonyms: built-in, user and reviewed synonyms map tags; loops and creator names are refused", () =>
        {
            using var sandbox = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = sandbox.Path });
            new SchemaMigrator(database).Initialize();
            var synonyms = new TagSynonyms(database); var log = new AutoSourcarr.Tests.Matching.MixedEntriesTests.RecordingLog();
            var map = synonyms.Load();
            Check.Equal("tag:science fiction", map.Canonical("tag", "Sci-Fi"));
            Check.Equal("tag:full colour", map.Canonical("genre", "Full  Color"));
            Check.Equal("tag:webtoon", map.Canonical("tag", "Long Strip"));
            Check.Equal("artist:sci-fi", map.Canonical("artist", "sci-fi")); // creator names are never aliased
            // The released gallery comparison form keeps working with the map.
            Check.True(GalleryTagEquivalence.CanonicalKeys([System.Text.Json.Nodes.JsonValue.Create("genre:Sci Fi")], map.Mapping).Contains("tag:science fiction"), "mapping form");

            var saved = synonyms.Save([new TagSynonym("tag", "Boys Love", "Yaoi", "user"), new TagSynonym("", "long strip", "long strip", "user"),
                new TagSynonym("tag", "Vertical Comic", "Webtoon", "user")], map.Revision, log, DecisionActor.User("owner"));
            Check.Equal("tag:yaoi", saved.Canonical("tag", "boys love"));
            Check.Equal("tag:long strip", saved.Canonical("tag", "Long Strip")); // a self-mapping switched the built-in off
            Check.True(saved.Revision != map.Revision, "revision changes with the user synonyms");
            Check.Equal(saved.Revision, synonyms.Load().Revision);
            Check.True(log.Events.Single().Kind == "tags.synonyms" && log.Events.Single().Actor.Name == "owner", "logged with the actor");
            Check.Throws<InvalidOperationException>(() => synonyms.Save([], map.Revision, log, DecisionActor.User("owner")));
            Check.Throws<TagSynonymException>(() => synonyms.Save([new TagSynonym("tag", "a", "b", "user"), new TagSynonym("tag", "b", "a", "user")], null, log, DecisionActor.User("owner")));
            Check.Throws<TagSynonymException>(() => synonyms.Save([new TagSynonym("artist", "x", "y", "user")], null, log, DecisionActor.User("owner")));
            Check.Throws<TagSynonymException>(() => synonyms.Save([new TagSynonym("tag", "same", "Same", "user")], null, log, DecisionActor.User("owner")));

            // Reviewed "same" tag pairs join the map.
            database.Write(DatabaseFile.Catalog, c =>
            {
                c.Execute("INSERT INTO tags(id,namespace,name,normalized) VALUES(1,'tag','Isekai','isekai'),(2,'tag','Another World','another world')");
                return c.Execute("INSERT INTO tag_reviews(left_id,right_id,decision,updated_at) VALUES(1,2,'same','t')");
            });
            var reviewed = synonyms.Load();
            Check.Equal(reviewed.Canonical("tag", "Isekai"), reviewed.Canonical("tag", "Another World"));

            // User synonyms are edition evidence: an unlabeled record tagged "Vertical Comic" is a webtoon.
            var facts = new EditionRecordFacts("1", 1, "Plain", "Vertical Comic", "", [], "", "", null);
            Check.Equal("webtoon", EditionSignals.Decide(EditionSignals.Media(facts, reviewed)).Mode);
            Check.Equal("unknown", EditionSignals.Decide(EditionSignals.Media(facts)).Mode);
        });
    }
}
