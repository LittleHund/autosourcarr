using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Matching.Scoring;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources.Configuration;

namespace AutoSourcarr.Tests.Matching;

/// <summary>Phase 8A match-on-ingest over a real catalogue database.</summary>
public static class EntryIngestTests
{
    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _temp = new();
        public SqliteDatabase Database { get; }
        public InMemoryDecisionLog Log { get; } = new();
        public object Gate { get; } = new();
        public EntryIngest Ingest { get; }

        public Setup(Func<Setup, EntryIngest>? ingest = null)
        {
            Database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = _temp.Path });
            new SchemaMigrator(Database).Initialize();
            MatchingFixtures.Source(Database, "1", "Alpha", "en");
            MatchingFixtures.Source(Database, "2", "Beta", "en");
            MatchingFixtures.Source(Database, "3", "Gamma", "en");
            Ingest = ingest?.Invoke(this) ?? new EntryIngest(Database, Gate, Log);
        }

        public void Dispose() { Database.Dispose(); _temp.Dispose(); }
    }

    public static void Register(TestSuite suite)
    {
        suite.Add("matching ingest: new, known, attached, edition and sequel records", () =>
        {
            using var setup = new Setup();
            var ingest = setup.Ingest;
            var first = ingest.IngestManga("1", MatchingFixtures.Manga(11, "The Apothecary Diaries", "Natsu Hyuuga"));
            Check.Equal("new-entry", first.Reason);
            var entry = first.EntryId!.Value;
            Check.Equal("known-source-id", ingest.IngestManga("1", MatchingFixtures.Manga(11, "The Apothecary Diaries", "Natsu Hyuuga")).Reason);

            var attached = ingest.IngestManga("2", MatchingFixtures.Manga(22, "The Apothecary Diaries", "Hyuuga Natsu"));
            Check.Equal(entry, attached.EntryId ?? 0);
            Check.True(attached.Score >= MatchScorer.MinimumFloor && attached.Reason.StartsWith("trusted-anchor-title", StringComparison.Ordinal), attached.Reason);
            Check.Equal("match.automatic", string.Join(',', setup.Log.Kinds));
            Check.True(setup.Log.Entries[0].Actor == DecisionActor.Automatic && setup.Log.Entries[0].Records.Contains("2:22"));

            // Editions and sequels become their own entries (MAT-1 / MAT-3).
            var colored = ingest.IngestManga("3", MatchingFixtures.Manga(33, "The Apothecary Diaries (Colored)", "Natsu Hyuuga"));
            Check.True(colored.EntryId != entry && colored.Reason == "new-entry");
            var sequel = ingest.IngestManga("3", MatchingFixtures.Manga(34, "The Apothecary Diaries 2", "Natsu Hyuuga"));
            Check.True(sequel.EntryId != entry && sequel.EntryId != colored.EntryId);
            // A different creator never attaches, even with the same title (MAT-2).
            var other = ingest.IngestManga("3", MatchingFixtures.Manga(35, "The Apothecary Diaries", "Someone Else"));
            Check.True(other.EntryId != entry);

            Check.Equal(entry, ingest.FindExistingBySource("2", 22) ?? 0);
            Check.True(ingest.SourceRecordAttached(entry, "2", 22));
            var detail = ingest.GetEntry(entry);
            Check.Equal(2, (detail["matches"] as JsonArray)!.Count);
            Check.Equal(2L, detail["entry_match_count"]!.GetValue<long>());
            Check.True(detail["description_for_extension"]!.GetValue<string>().Contains("Sources", StringComparison.Ordinal));
            Check.True(ingest.ExactSearchEntryIds("the apothecary diaries").Contains(entry));
            Check.True(ingest.SearchEntryIds("apothecary").Contains(entry));
            Check.Equal(entry, ingest.EntriesByIds([entry], "apothecary")[0]["canonical_id"]!.GetValue<long>());
            var discovery = new SourceDiscovery(ingest);
            Check.Equal(entry, discovery.SourceRecordOwners([("2", 22L), ("2", 999L)])[("2", 22L)]);
            Check.True(discovery.SourceMatchEvidence(entry, "3", MatchingFixtures.Manga(36, "The Apothecary Diaries", "Natsu Hyuuga")).AutomaticMatch);
        });

        suite.Add("matching ingest: near-tied candidates, separate records and deleted records stay apart", () =>
        {
            using var setup = new Setup();
            var left = MatchingFixtures.Entry(setup.Database, "Witch Hat Atelier");
            var right = MatchingFixtures.Entry(setup.Database, "Witch Hat Atelier");
            MatchingFixtures.Record(setup.Database, left, "1", 11, "Witch Hat Atelier", "Kamome Shirahama");
            MatchingFixtures.Record(setup.Database, right, "2", 22, "Witch Hat Atelier", "Kamome Shirahama");
            var tied = setup.Ingest.IngestManga("3", MatchingFixtures.Manga(33, "Witch Hat Atelier", "Kamome Shirahama"));
            Check.Equal("ambiguous-local-candidates:new-entry", tied.Reason);
            Check.True(tied.EntryId != left && tied.EntryId != right);

            using var second = new Setup();
            var target = MatchingFixtures.Entry(second.Database, "Witch Hat Atelier");
            MatchingFixtures.Record(second.Database, target, "1", 11, "Witch Hat Atelier", "Kamome Shirahama");
            second.Database.Write(DatabaseFile.Catalog, session => session.Execute("""
                INSERT INTO manual_separate_records(target_entry_id,source_id,source_manga_id,created_at,updated_at) VALUES($entry,'2',22,'x','x')
                """, ("$entry", target)));
            var separate = second.Ingest.IngestManga("2", MatchingFixtures.Manga(22, "Witch Hat Atelier", "Kamome Shirahama"));
            Check.True(separate.Reason == "manual-separate:new-entry" && separate.EntryId != target, separate.Reason);
            second.Database.Write(DatabaseFile.Catalog, session => session.Execute("INSERT INTO deleted_source_records(source_id,source_manga_id,deleted_at) VALUES('3',44,'x')"));
            var deleted = second.Ingest.IngestManga("3", MatchingFixtures.Manga(44, "Witch Hat Atelier"));
            Check.True(deleted.EntryId is null && deleted.Reason == "manually-deleted");
        });

        suite.Add("matching ingest: manual bindings and rejections win over automatic matching", () =>
        {
            var view = new FixedManualView();
            using var setup = new Setup(s => new EntryIngest(s.Database, s.Gate, s.Log, view));
            var bound = MatchingFixtures.Entry(setup.Database, "Completely Different Title");
            var anchor = MatchingFixtures.Entry(setup.Database, "Blue Period");
            MatchingFixtures.Record(setup.Database, anchor, "1", 11, "Blue Period", "Tsubasa Yamaguchi");
            view.Owner = bound;
            var restored = setup.Ingest.IngestManga("2", MatchingFixtures.Manga(22, "Blue Period", "Tsubasa Yamaguchi"));
            Check.True(restored.EntryId == bound && restored.Reason == "manual-binding", restored.Reason);
            view.Owner = null; view.Rejected = anchor;
            var rejected = setup.Ingest.IngestManga("3", MatchingFixtures.Manga(33, "Blue Period", "Tsubasa Yamaguchi"));
            // The rejected entry is never chosen; the record may still go to another eligible entry.
            Check.True(rejected.EntryId != anchor, rejected.Reason);
        });

        suite.Add("matching ingest: matching rules raise thresholds and disabled rules stop automation", () =>
        {
            using var setup = new Setup(s => new EntryIngest(s.Database, s.Gate, s.Log)
            {
                Policies = new MatchingPolicyStore(s.Database, s.Gate, s.Log),
            });
            var anchor = MatchingFixtures.Entry(setup.Database, "Kaguya-sama: Love Is War");
            MatchingFixtures.Record(setup.Database, anchor, "1", 11, "Kaguya-sama: Love Is War", "Aka Akasaka");
            var policies = new MatchingPolicyStore(setup.Database, setup.Gate, setup.Log);
            var revision = MatchingRuleConfiguration.Revision(policies.Rules());
            policies.Write(new MatchingRuleKey("source", "2", "external"), new JsonObject { ["enabled"] = false, ["checks"] = new JsonObject() }, revision,
                DecisionActor.User("owner"), "no automatic matching from Beta");
            Check.Equal("policy.write", setup.Log.Kinds[^1]);
            var blocked = setup.Ingest.IngestManga("2", MatchingFixtures.Manga(22, "Kaguya-sama: Love Is War", "Aka Akasaka"));
            Check.True(blocked.EntryId != anchor, blocked.Reason);
            var allowed = setup.Ingest.IngestManga("3", MatchingFixtures.Manga(33, "Kaguya-sama - Love is War", "Akasaka Aka"));
            Check.Equal(anchor, allowed.EntryId ?? 0);
        });

        suite.Add("matching profiles: presets keep the 0.4.5 revision rules and record changes", () =>
        {
            using var setup = new Setup();
            var presets = new MatchingPresetStore(setup.Database, setup.Gate, setup.Log);
            var configuration = presets.Configuration();
            Check.Equal(5, (configuration["presets"] as JsonArray)!.Count);
            var revision = configuration["preset_revision"]!.GetValue<string>();
            Check.Equal(409, Check.Throws<MatchingProfileException>(() => presets.Save(new JsonObject
            {
                ["revision"] = "stale", ["name"] = "Mine", ["kind"] = "simple", ["settings"] = new JsonObject { ["strict_titles"] = true },
            }, null, DecisionActor.User("owner"))).Status);
            var saved = presets.Save(new JsonObject
            {
                ["revision"] = revision, ["name"] = "Mine", ["kind"] = "simple", ["settings"] = new JsonObject { ["strict_titles"] = true, ["require_creators"] = true },
            }, null, DecisionActor.User("owner"));
            var mine = (saved["presets"] as JsonArray)!.OfType<JsonObject>().Single(preset => preset["name"]!.GetValue<string>() == "Mine");
            Check.Equal("simple", mine["kind"]!.GetValue<string>());
            Check.Equal(.97, mine["options"]!["floor"]!.GetValue<double>());
            Check.Equal(400, Check.Throws<MatchingProfileException>(() => presets.Save(new JsonObject
            {
                ["revision"] = saved["preset_revision"]!.GetValue<string>(), ["name"] = "Strict", ["rule"] = new JsonObject { ["enabled"] = true, ["checks"] = new JsonObject { ["title"] = .99 } },
            }, "strict", DecisionActor.User("owner"))).Status);
            var after = presets.Delete(mine["id"]!.GetValue<string>(), saved["preset_revision"]!.GetValue<string>(), DecisionActor.User("owner"));
            Check.Equal(5, (after["presets"] as JsonArray)!.Count);
            Check.Equal("profile.write,profile.delete", string.Join(',', setup.Log.Kinds));
            Check.True(after["source_ranking"] is JsonArray { Count: 3 });
        });

        suite.Add("matching titles: description aliases of attached records keep whole titles", () =>
        {
            using var setup = new Setup();
            var result = setup.Ingest.IngestManga("1", MatchingFixtures.Manga(11, "Shutainzu Geto", "Chiyomaru Shikura",
                "A story.\n\nAssociated Names:\nSteins;Gate\nShutainzu Geto"));
            var titles = new CatalogueTitles(setup.Database, setup.Gate, setup.Log);
            var rows = titles.EntryTitleRows(result.EntryId!.Value);
            Check.True(rows.Any(row => row.TitleType == "description-alias" && row.Title == "Steins;Gate"), string.Join('|', rows.Select(row => row.TitleType + ":" + row.Title)));
            Check.True(!rows.Any(row => row.Title is "Steins" or "Gate"));
        });
    }

    private sealed class FixedManualView : IManualDecisionsView
    {
        public long? Owner { get; set; }
        public long? Rejected { get; set; }
        public long? ManualOwner(string sourceId, long sourceMangaId) => Owner;
        public bool IsSeparated(long entryId, string sourceId, long sourceMangaId) => false;
        public bool IsRejected(long entryId, string sourceId, long sourceMangaId) => Rejected == entryId;
    }
}
