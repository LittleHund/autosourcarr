using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests.Matching;

/// <summary>Phase 8A titles: MAT-4 whole titles, manual main titles, aliases and bound-title consensus.</summary>
public static class CatalogueTitlesTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("matching titles: titles the released splitter cut stay whole (MAT-4)", () =>
        {
            const string steins = "Alternative titles: Fate/Zero; Steins;Gate";
            // The released splitter cuts "Steins;Gate" at the in-word semicolon.
            Check.True(TextMatcher.ExtractAltTitles(steins).Contains("Steins"));
            Check.Equal("Fate/Zero|Steins;Gate", string.Join('|', WholeTitleAliases.Extract(steins)));

            const string chunibyo = "Associated Names: Love, Chunibyo & Other Delusions, Chuunibyou demo Koi ga Shitai!";
            Check.True(TextMatcher.ExtractAltTitles(chunibyo).Contains("Love"));
            Check.Equal("Love, Chunibyo & Other Delusions|Chuunibyou demo Koi ga Shitai!",
                string.Join('|', WholeTitleAliases.Extract(chunibyo, ["Love, Chunibyo & Other Delusions"])));

            const string yotsuba = "Alt titles: Yotsuba&!, Hello, World";
            Check.True(!TextMatcher.ExtractAltTitles(yotsuba).Contains("Yotsuba&!"));
            var kept = WholeTitleAliases.Extract(yotsuba, ["Yotsuba&!", "Hello, World"]);
            Check.True(kept.Contains("Yotsuba&!") && kept.Contains("Hello, World") && !kept.Contains("Hello") && !kept.Contains("World"), string.Join('|', kept));

            // A known title in the summary body (outside the alias section) is not invented as an alias.
            Check.Equal(0, WholeTitleAliases.Extract("Yotsuba&! is a slice of life story.", ["Yotsuba&!"]).Count);
        });

        suite.Add("matching titles: manual main title and aliases are recorded and reset only on request", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            var log = new InMemoryDecisionLog();
            var titles = new CatalogueTitles(database, new object(), log);
            var entry = MatchingFixtures.Entry(database, "Sousou no Frieren");
            MatchingFixtures.Source(database, "1", "Alpha", "en");
            MatchingFixtures.Source(database, "2", "Beta", "en");
            MatchingFixtures.Record(database, entry, "1", 11, "Frieren: Beyond Journey's End");
            MatchingFixtures.Record(database, entry, "2", 22, "Frieren: Beyond Journey's End");

            Check.Equal("Frieren: Beyond Journey's End", titles.SyncPreferredTitleFromBoundRecords(entry));
            Check.Throws<CatalogueTitleException>(() => titles.SetPreferredTitle(entry, "  ", DecisionActor.User("owner")));
            Check.Equal(404, Check.Throws<CatalogueTitleException>(() => titles.SetPreferredTitle(entry + 99, "Frieren", DecisionActor.User("owner"))).Status);
            titles.SetPreferredTitle(entry, "Frieren", DecisionActor.User("owner"));
            Check.Equal("Frieren", titles.EntryPreferredTitle(entry));
            Check.Equal("Frieren", titles.PersistentTitleOverride(entry));
            // A manual main title survives the automatic consensus.
            Check.Equal("Frieren", titles.SyncPreferredTitleFromBoundRecords(entry));

            titles.AddManualAlias(entry, "Frieren at the Funeral", "en", DecisionActor.Key("script"));
            Check.True(titles.EntryTitleRows(entry).Any(row => row.TitleType == "manual" && row.Title == "Frieren at the Funeral"));

            titles.ClearManualTitles(entry, DecisionActor.User("owner"));
            Check.Equal("Frieren: Beyond Journey's End", titles.EntryPreferredTitle(entry));
            Check.Equal("", titles.PersistentTitleOverride(entry));
            Check.True(!titles.EntryTitleRows(entry).Any(row => row.TitleType == "manual"));
            Check.Equal("title.preferred,title.alias.add,title.manual.clear", string.Join(',', log.Kinds));
            Check.Equal("script", log.Entries[1].Actor.Name);
        });

        suite.Add("matching titles: alias cache follows merge chains and repairs dangling aliases", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            var titles = new CatalogueTitles(database, new object(), new InMemoryDecisionLog());
            var survivor = MatchingFixtures.Entry(database, "Vinland Saga");
            titles.RememberEntryAlias(900, 901, "merge");
            titles.RememberEntryAlias(901, survivor, "merge");
            Check.Equal(survivor, titles.CanonicalEntryId(900));
            var repaired = titles.RepairEntryAliasIntegrity();
            Check.Equal(0, repaired["removed"]!.GetValue<int>());
            database.Write(DatabaseFile.Catalog, session => session.Execute("INSERT INTO entry_aliases(old_entry_id,canonical_entry_id,reason,merged_at) VALUES(950,951,'merge','x')"));
            Check.Equal(1, titles.RepairEntryAliasIntegrity()["removed"]!.GetValue<int>());
        });
    }
}

/// <summary>Small catalogue rows for the phase 8A tests.</summary>
internal static class MatchingFixtures
{
    public static long Entry(SqliteDatabase database, string title, string author = "", string colorMode = "unknown", string mediaMode = "unknown") =>
        database.Write(DatabaseFile.Catalog, session =>
        {
            session.Execute("""
                INSERT INTO entries(preferred_title,author,color_mode,media_mode,created_at,updated_at) VALUES($title,$author,$color,$media,'2026-01-01','2026-01-01')
                """, ("$title", title), ("$author", author), ("$color", colorMode), ("$media", mediaMode));
            return Convert.ToInt64(session.Scalar("SELECT last_insert_rowid()"), System.Globalization.CultureInfo.InvariantCulture);
        });

    public static void Source(SqliteDatabase database, string id, string name, string language, bool official = false) =>
        database.Write(DatabaseFile.Catalog, session => session.Execute("""
            INSERT INTO source_config(source_id,name,lang,last_seen,official) VALUES($id,$name,$lang,'2026-01-01',$official)
            """, ("$id", id), ("$name", name), ("$lang", language), ("$official", official ? 1 : 0)));

    public static void Record(SqliteDatabase database, long entry, string source, long manga, string title, string author = "", bool variant = false) =>
        database.Write(DatabaseFile.Catalog, session => session.Execute("INSERT INTO " + (variant ? "source_variants" : "source_matches") + """
            (entry_id,source_id,source_manga_id,source_title,author,match_type,last_checked) VALUES($entry,$source,$manga,$title,$author,'auto','2026-01-01')
            """, ("$entry", entry), ("$source", source), ("$manga", manga), ("$title", title), ("$author", author)));

    public static JsonObject Manga(long id, string title, string author = "", string description = "") => new()
    {
        ["id"] = id, ["title"] = title, ["author"] = author, ["description"] = description, ["url"] = "/manga/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["thumbnailUrl"] = "", ["initialized"] = false,
    };
}
