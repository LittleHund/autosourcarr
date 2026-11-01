using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Metadata;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Tests.Matching;

namespace AutoSourcarr.Tests.Catalogue.Metadata;

/// <summary>Phase 9: per-field official-first metadata with overrides over a real catalogue database.</summary>
public static class MetadataChooserTests
{
    internal sealed class Setup : IDisposable
    {
        private readonly Sandbox _temp = new();
        public SqliteDatabase Database { get; }
        public object Gate { get; } = new();
        public InMemoryDecisionLog Log { get; } = new();
        public CatalogueTitles Titles { get; }
        public MetadataChooser Chooser { get; }
        public long Entry { get; }

        public Setup()
        {
            Database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = _temp.Path });
            new SchemaMigrator(Database).Initialize();
            MatchingFixtures.Source(Database, "1", "Alpha", "en");
            MatchingFixtures.Source(Database, "10", "Publisher", "en", official: true);
            Titles = new CatalogueTitles(Database, Gate, Log);
            Chooser = new MetadataChooser(Database, Gate, Log, Titles);
            Entry = MatchingFixtures.Entry(Database, "Witch Hat Atelier");
            Database.Write(DatabaseFile.Catalog, session =>
            {
                session.Execute("""
                    INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,description,author,genre,thumbnail_url,status,match_type,last_checked)
                    VALUES($e,'1',11,'Witch Hat Atelier','A fan summary that is long enough to be a real summary of the work.','Fan Author','Action','https://a/a.jpg',1,'auto','2026-01-01'),
                          ($e,'10',12,'Witch Hat Atelier','The publisher summary of the story of Coco and the witches of the atelier.','Kamome Shirahama','','https://o/o.jpg',0,'auto','2026-01-01')
                    """, ("$e", Entry));
                session.Execute("""
                    INSERT INTO identity_links(entry_id,provider,provider_name,external_id,confidence,title,description,genre,status,year,last_checked)
                    VALUES($e,'anilist','AniList','98765',0.97,'Tongari Boushi no Atelier','Provider summary.','Drama, Fantasy',2,2016,'2026-01-01')
                    """, ("$e", Entry));
                return 0;
            });
        }

        public JsonObject Field(JsonObject view, string name) => (view["fields"] as JsonArray)!.OfType<JsonObject>().Single(item => item["field"]!.GetValue<string>() == name);
        public void Dispose() { Database.Dispose(); _temp.Dispose(); }
    }

    public static void Register(TestSuite suite)
    {
        suite.Add("catalogue metadata: official sources first, then providers, then other sources", () =>
        {
            using var setup = new Setup();
            var view = setup.Chooser.Read(setup.Entry, includePrivate: false);
            var description = setup.Field(view, "description");
            Check.True(description["value"]!.GetValue<string>().StartsWith("The publisher summary", StringComparison.Ordinal));
            Check.Equal("10", description["source"]!["source_id"]!.GetValue<string>());
            Check.True(description["source"]!["official"]!.GetValue<bool>());
            Check.Equal(3, (description["alternatives"] as JsonArray)!.Count);
            Check.Equal("provider", (description["alternatives"] as JsonArray)![1]!["source"]!["kind"]!.GetValue<string>());
            Check.Equal("Kamome Shirahama", setup.Field(view, "authors")["value"]!.GetValue<string>());
            // The official record has no genres or status: the provider comes before the other source.
            Check.Equal("Drama, Fantasy", setup.Field(view, "genres")["value"]!.GetValue<string>());
            Check.Equal("anilist", setup.Field(view, "genres")["source"]!["provider"]!.GetValue<string>());
            Check.Equal(2L, setup.Field(view, "status")["value"]!.GetValue<long>());
            Check.Equal(2016, setup.Field(view, "year")["value"]!.GetValue<int>());
            Check.Equal("https://o/o.jpg", setup.Field(view, "cover")["value"]!.GetValue<string>());
            Check.Equal("Witch Hat Atelier", setup.Field(view, "title")["value"]!.GetValue<string>());
            Check.True(!setup.Field(view, "title")["overridden"]!.GetValue<bool>());

            // The same choice is written into the entry row by the native metadata rebuild.
            var metadata = new CatalogueMetadata(() => 0.9) { FieldPolicy = setup.Chooser };
            setup.Database.Write(DatabaseFile.Catalog, session => { metadata.ChooseMetadata(session, setup.Entry, "2026-02-01T00:00:00+00:00"); return 0; });
            var row = setup.Database.Read(DatabaseFile.Catalog, session => session.Query("SELECT description,author,genre,status,thumbnail_url FROM entries WHERE id=$id", ("$id", setup.Entry)).Single());
            Check.True(row.Text("description").StartsWith("The publisher summary", StringComparison.Ordinal), row.Text("description"));
            Check.Equal("Kamome Shirahama", row.Text("author"));
            Check.Equal("Drama, Fantasy", row.Text("genre"));
            Check.Equal(2L, row.Integer("status"));
            Check.Equal("https://o/o.jpg", row.Text("thumbnail_url"));
        });

        suite.Add("catalogue metadata: overrides win, are logged with the actor and clear back to the ranked choice", () =>
        {
            using var setup = new Setup();
            var alice = DecisionActor.User("alice");
            var view = setup.Chooser.Override(setup.Entry, "description", new JsonObject { ["value"] = "My own summary." }, alice);
            var description = setup.Field(view, "description");
            Check.Equal("My own summary.", description["value"]!.GetValue<string>());
            Check.True(description["overridden"]!.GetValue<bool>());
            Check.Equal("user:alice", description["override"]!["set_by"]!.GetValue<string>());
            var entry = setup.Log.Entries.Single();
            Check.Equal("metadata.override", entry.Kind);
            Check.Equal(alice, entry.Actor);
            // A guest sees the value but not who set it.
            var guest = setup.Field(setup.Chooser.Read(setup.Entry, includePrivate: false), "description");
            Check.True(guest["override"]!["set_by"] is null && guest["value"]!.GetValue<string>() == "My own summary.");

            setup.Chooser.Override(setup.Entry, "status", new JsonObject { ["value"] = "hiatus" }, alice);
            setup.Chooser.Override(setup.Entry, "year", new JsonObject { ["value"] = 2014 }, alice);
            setup.Chooser.Override(setup.Entry, "genres", new JsonObject { ["value"] = new JsonArray("Fantasy", "Magic", "fantasy") }, alice);
            setup.Chooser.Override(setup.Entry, "authors", new JsonObject { ["source_id"] = "1", ["source_manga_id"] = 11 }, alice);
            view = setup.Chooser.Read(setup.Entry, includePrivate: true);
            Check.Equal(6L, setup.Field(view, "status")["value"]!.GetValue<long>());
            Check.Equal(2014, setup.Field(view, "year")["value"]!.GetValue<int>());
            Check.Equal("Fantasy, Magic", setup.Field(view, "genres")["value"]!.GetValue<string>());
            Check.Equal("Fan Author", setup.Field(view, "authors")["value"]!.GetValue<string>());
            Check.Equal("source", setup.Field(view, "authors")["override"]!["mode"]!.GetValue<string>());

            // Display overlay (the 0.4.5 contract) shows the locked fields; 0.4.5's own fields keep working.
            var display = new DisplayMetadata(setup.Database, setup.Gate);
            var overlay = display.Overlay(new JsonObject { ["id"] = setup.Entry, ["description"] = "x", ["genre"] = "", ["status"] = 0 });
            Check.Equal("My own summary.", overlay["description"]!.GetValue<string>());
            Check.Equal("Fantasy, Magic", overlay["genre"]!.GetValue<string>());
            Check.Equal(6L, overlay["status"]!.GetValue<long>());

            Check.Equal(400, Check.Throws<MetadataChoiceException>(() => setup.Chooser.Override(setup.Entry, "cover", new JsonObject { ["value"] = "https://elsewhere/x.jpg" }, alice)).Status);
            Check.Equal(400, Check.Throws<MetadataChoiceException>(() => setup.Chooser.Override(setup.Entry, "status", new JsonObject { ["value"] = "soon" }, alice)).Status);
            Check.Equal(400, Check.Throws<MetadataChoiceException>(() => setup.Chooser.Override(setup.Entry, "genres", new JsonObject { ["source_id"] = "1", ["source_manga_id"] = 11 }, alice)).Status);
            Check.Equal(404, Check.Throws<MetadataChoiceException>(() => setup.Chooser.Override(setup.Entry, "rating", new JsonObject { ["value"] = "5" }, alice)).Status);
            Check.Equal(404, Check.Throws<MetadataChoiceException>(() => setup.Chooser.Read(99999, includePrivate: false)).Status);
            Check.Equal("https://a/a.jpg", setup.Field(setup.Chooser.Override(setup.Entry, "cover", new JsonObject { ["value"] = "https://a/a.jpg" }, alice), "cover")["value"]!.GetValue<string>());

            view = setup.Chooser.ClearOverride(setup.Entry, "description", DecisionActor.User("bob"));
            Check.True(setup.Field(view, "description")["value"]!.GetValue<string>().StartsWith("The publisher summary", StringComparison.Ordinal));
            Check.True(!setup.Field(view, "description")["overridden"]!.GetValue<bool>());
            Check.Equal("metadata.override.clear", setup.Log.Entries[^1].Kind);
            Check.Equal("bob", setup.Log.Entries[^1].Actor.Name);
            // History keeps every previous ownership snapshot.
            Check.True(display.Read(setup.Entry)["history"]!.AsArray().Count >= 6);
        });

        suite.Add("catalogue metadata: the title override is the main title; clearing it keeps manual aliases", () =>
        {
            using var setup = new Setup();
            setup.Titles.AddManualAlias(setup.Entry, "Atelier of Witch Hat", "en", DecisionActor.User("alice"));
            var view = setup.Chooser.Override(setup.Entry, "title", new JsonObject { ["value"] = "Witch Hat Atelier (My Title)" }, DecisionActor.User("alice"));
            Check.Equal("Witch Hat Atelier (My Title)", setup.Field(view, "title")["value"]!.GetValue<string>());
            Check.True(setup.Field(view, "title")["overridden"]!.GetValue<bool>());
            Check.True(setup.Log.Kinds.Contains("title.preferred"));
            view = setup.Chooser.ClearOverride(setup.Entry, "title", DecisionActor.User("alice"));
            Check.True(!setup.Field(view, "title")["overridden"]!.GetValue<bool>());
            Check.Equal("Witch Hat Atelier", setup.Field(view, "title")["value"]!.GetValue<string>());
            Check.Equal("metadata.override.clear", setup.Log.Entries[^1].Kind);
            Check.True(setup.Titles.EntryTitleRows(setup.Entry).Any(row => row.Title == "Atelier of Witch Hat" && row.TitleType == "manual"));
        });
    }
}
