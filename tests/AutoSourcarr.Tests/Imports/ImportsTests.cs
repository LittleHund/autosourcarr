using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Imports;
using AutoSourcarr.Core.Imports.Backup;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Matching.Manual;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Tests.Matching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests.Imports;

/// <summary>
/// Phase 9A backup imports: the generated reader on a sample backup of every supported version (IMP-11, PIPE-9), the
/// end-to-end import of each sample, Match/Search/Skip/Retry with the latest click winning (IMP-5), the fallback search
/// without labels (IMP-7), crash recovery (IMP-4), the merge-log recheck after a reset (IMP-9), remembered decisions
/// surviving history clears, the Suwayomi plan import and the admin-only routes.
/// </summary>
public static class ImportsTests
{
    private const string Harbor = "9101";
    private const string Quill = "-4611686018427387904";
    private static readonly DecisionActor Person = DecisionActor.User("owner");

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "backups", name);

    private static JsonObject Expected() => JsonNode.Parse(File.ReadAllText(Fixture("expected.json")))!.AsObject();

    private static JsonObject SourceManga(long id, string title, string url, string author, string description = "") => new()
    {
        ["id"] = id, ["title"] = title, ["url"] = url, ["author"] = author, ["artist"] = author, ["description"] = description,
        ["thumbnailUrl"] = "", ["initialized"] = true,
    };

    /// <summary>The main library: remembers what was added; an optional gate holds AddAsync to race decisions.</summary>
    private sealed class FakeLibrary : IImportLibrary
    {
        public HashSet<long> Members { get; } = [];
        public List<long> Added { get; } = [];
        public List<long> Removed { get; } = [];
        public TaskCompletionSource? Gate { get; set; }
        private int _addCalls;
        public int AddCalls => Volatile.Read(ref _addCalls);

        public Task<IReadOnlySet<long>> SubscribedAsync(CancellationToken cancellationToken)
        {
            lock (Members) return Task.FromResult<IReadOnlySet<long>>(Members.ToHashSet());
        }

        public async Task<ImportLibraryOutcome> AddAsync(long entryId, IReadOnlyList<string> titles, Func<bool> superseded, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _addCalls);
            if (Gate is { } gate) await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (superseded()) return new("superseded", "superseded");
            lock (Members)
            {
                if (!Members.Add(entryId)) return new("existing", "Already in main Suwayomi library");
                Added.Add(entryId);
            }
            return new("added", "Added to main Suwayomi library");
        }

        public Task<ImportLibraryOutcome> RemoveAsync(long entryId, IReadOnlyList<string> titles, Func<bool> superseded, CancellationToken cancellationToken)
        {
            lock (Members) { Members.Remove(entryId); Removed.Add(entryId); }
            return Task.FromResult(new ImportLibraryOutcome("removed", "Removed rejected import from main Suwayomi library"));
        }
    }

    /// <summary>Live sources: a fixed answer per source; a query is answered when its normalized form is one the source accepts.</summary>
    private sealed class FakeSources : IImportSourceSearch
    {
        public Dictionary<string, List<(string Query, JsonObject Manga)>> Answers { get; } = new(StringComparer.Ordinal);
        public List<(string Source, string Query)> Searched { get; } = [];
        public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

        public void Answer(string sourceId, string query, JsonObject manga)
        {
            if (!Answers.TryGetValue(sourceId, out var list)) Answers[sourceId] = list = [];
            list.Add((TextMatcher.NormalizeTitle(query), manga));
        }

        public Task<IReadOnlyList<JsonObject>> SearchAsync(string sourceId, string query, CancellationToken cancellationToken)
        {
            lock (Searched) Searched.Add((sourceId, query));
            if (Failing.Contains(sourceId)) throw new TimeoutException("Source request timed out after 8s of execution");
            var key = TextMatcher.NormalizeTitle(query);
            IReadOnlyList<JsonObject> found = Answers.TryGetValue(sourceId, out var list)
                ? list.Where(answer => answer.Query == key).Select(answer => (JsonObject)answer.Manga.DeepClone()).ToList() : [];
            return Task.FromResult(found);
        }

        public Task<JsonObject?> DetailsAsync(long mangaId, CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(null);
    }

    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _temp = new();
        public SqliteDatabase Database { get; }
        public InMemoryDecisionLog Log { get; } = new();
        public object Gate { get; } = new();
        public EntryIngest Ingest { get; }
        public ManualDecisions Manual { get; }
        public FakeLibrary Library { get; } = new();
        public FakeSources Sources { get; } = new();
        public ImportRuns Runs { get; private set; }
        public string DataDirectory => _temp.Path;

        public Setup()
        {
            Database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = _temp.Path });
            new SchemaMigrator(Database).Initialize();
            MatchingFixtures.Source(Database, Harbor, "Harborlight (EN)", "en");
            MatchingFixtures.Source(Database, Quill, "Quillfeed", "en");
            Ingest = new EntryIngest(Database, Gate, Log);
            Manual = new ManualDecisions(Database, Gate, Log);
            Sources.Answer(Harbor, "Lantern Keeper of the Ninth Harbor", SourceManga(501, "Lantern Keeper of the Ninth Harbor", "/series/lantern-keeper-ninth-harbor", "Mirela Tov"));
            Sources.Answer(Quill, "The Clockwork Orchard (Colored)", SourceManga(601, "The Clockwork Orchard (Colored)", "/title/clockwork-orchard-colored", "Ansel Brightwater"));
            Runs = NewRuns();
        }

        /// <summary>A second service over the same database: what a restarted process sees.</summary>
        public ImportRuns NewRuns() => Runs = new ImportRuns(Database, Gate, Log, Ingest)
        {
            Manual = Manual, Library = Library, Sources = Sources, History = Log, DataDirectory = _temp.Path,
        };

        public DatabaseRow Row(string importId, long itemId) =>
            Database.Read(DatabaseFile.Catalog, c => c.Query("SELECT * FROM import_items WHERE import_id=$run AND id=$id", ("$run", importId), ("$id", itemId)).Single());

        public IReadOnlyList<DatabaseRow> Rows(string importId) =>
            Database.Read(DatabaseFile.Catalog, c => c.Query("SELECT * FROM import_items WHERE import_id=$run ORDER BY position", ("$run", importId)));

        public void Sql(string sql, params (string, object?)[] parameters) => Database.Write(DatabaseFile.Catalog, c => c.Execute(sql, parameters));

        /// <summary>One stored unresolved row (a backup row of the Harborlight or Quillfeed source).</summary>
        public long Row(string importId, int position, string title, string source, string url, string sourceName, string author = "")
        {
            Runs.CreateRun(importId, "sample.tachibk");
            var item = new JsonObject
            {
                ["source"] = long.Parse(source, System.Globalization.CultureInfo.InvariantCulture), ["url"] = url, ["title"] = title, ["author"] = author,
                ["favorite"] = true, ["sourceName"] = sourceName,
            };
            return Runs.StoreItem(importId, position, item, "unresolved", "not resolved yet", null);
        }

        public void Dispose() { Runs.Idle().GetAwaiter().GetResult(); Database.Dispose(); _temp.Dispose(); }
    }

    private static string Id(int seed) => seed.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

    public static void Register(TestSuite suite)
    {
        suite.Add("imports: the reader decodes a sample backup of every supported version", () =>
        {
            var fixtures = Expected()["fixtures"]!.AsArray().OfType<JsonObject>().ToArray();
            var versions = fixtures.Select(fixture => fixture["version"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            foreach (var version in MihonBackupReader.Versions) Check.True(versions.Contains(version), "a sample backup for " + version);
            foreach (var fixture in fixtures)
            {
                var file = fixture["file"]!.GetValue<string>();
                var backup = MihonBackupReader.Read(File.ReadAllBytes(Fixture(file)));
                Check.Equal(MihonBackupReader.Format, backup.Format);
                Check.True(backup.Version == fixture["version"]!.GetValue<string>(), file + " detected " + backup.Version);
                Check.Equal(fixture["compression"]!.GetValue<string>(), backup.Compression);
                Check.Equal(fixture["manga"]!.GetValue<int>(), backup.Manga.Count);
                Check.Equal(fixture["favorites"]!.GetValue<int>(), backup.FavoriteCount);
                Check.Equal(string.Join('|', fixture["titles"]!.AsArray().Select(title => title!.GetValue<string>())), string.Join('|', backup.Manga.Select(manga => manga.Title)));
                foreach (var (sourceId, name) in fixture["source_names"]!.AsObject())
                    Check.True(backup.Manga.Where(manga => manga.Source.ToString(System.Globalization.CultureInfo.InvariantCulture) == sourceId).All(manga => manga.SourceName == name!.GetValue<string>()),
                        file + " names source " + sourceId);
                var first = backup.Manga[0];
                Check.Equal("/series/lantern-keeper-ninth-harbor", first.Url);
                Check.Equal(2, first.Chapters);
                Check.True(!backup.Manga[2].Favorite && backup.Manga[0].Favorite, file + " favorite flags");
                var json = first.ToJson();
                foreach (var key in new[] { "source", "url", "title", "artist", "author", "description", "genre", "status", "thumbnailUrl", "dateAdded", "favorite", "version", "notes", "initialized", "sourceName" })
                    Check.True(json.ContainsKey(key), "0.4.x raw_json key " + key);
            }
        });

        suite.Add("imports: the reader refuses empty, JSON, broken and invalid-identity backups", () =>
        {
            Check.Throws<BackupDecodeException>(() => MihonBackupReader.Read([]));
            Check.Throws<BackupDecodeException>(() => MihonBackupReader.Read(Encoding.UTF8.GetBytes("  {\"backupManga\":[]}")));
            Check.Throws<BackupDecodeException>(() => MihonBackupReader.Read([0x1f, 0x8b, 0x08, 0x00, 0x01, 0x02]));
            // Field 1 (a manga) whose url (field 2) is not UTF-8.
            Check.Throws<BackupDecodeException>(() => MihonBackupReader.Read([0x0a, 0x05, 0x08, 0x01, 0x12, 0x01, 0xff]));
            // A protobuf without any manga.
            Check.Throws<BackupDecodeException>(() => MihonBackupReader.Read([0x12, 0x02, 0x0a, 0x00]));
        });

        suite.Add("imports: every sample backup imports end to end", () =>
        {
            foreach (var fixture in Expected()["fixtures"]!.AsArray().OfType<JsonObject>())
            {
                var file = fixture["file"]!.GetValue<string>();
                using var setup = new Setup();
                var accepted = setup.Runs.AcceptUpload(File.ReadAllBytes(Fixture(file)), file, null, Person);
                var importId = accepted["import_id"]!.GetValue<string>();
                Check.Equal(fixture["version"]!.GetValue<string>(), accepted["version"]!.GetValue<string>());
                setup.Runs.Idle().GetAwaiter().GetResult();
                var run = setup.Runs.ListRuns()["runs"]!.AsArray()[0]!.AsObject();
                Check.True(run["state"]!.GetValue<string>() == "completed", file + ": " + run.ToJsonString());
                Check.Equal(2L, run["added"]!.GetValue<long>());
                Check.Equal(0L, run["unresolved"]!.GetValue<long>() + run["failed"]!.GetValue<long>());
                var rows = setup.Rows(importId);
                Check.Equal(2, rows.Count);
                Check.True(rows.All(row => row.Text("state") == "added" && row["entry_id"] is long), file + " rows added");
                Check.Equal(2, setup.Library.Added.Count);
                Check.True(setup.Ingest.FindExistingBySource(Harbor, 501) is not null && setup.Ingest.FindExistingBySource(Quill, 601) is not null, "source records catalogued");
                Check.True(setup.Log.Kinds.Contains("import.upload") && setup.Log.Kinds.Count(kind => kind == "import.match.automatic") == 2, string.Join(',', setup.Log.Kinds));
                Check.True(!Directory.Exists(Path.Combine(setup.DataDirectory, "imports", importId)), "the upload is removed after the import");
                // The same upload id answers its receipt instead of importing twice.
                Check.Equal(importId, setup.Runs.AcceptUpload(File.ReadAllBytes(Fixture(file)), file, importId, Person)["import_id"]!.GetValue<string>());
                Check.Throws<ImportConflictException>(() => setup.Runs.AcceptUpload(File.ReadAllBytes(Fixture(file)), "other.tachibk", importId, Person));
            }
        });

        suite.Add("imports: fallback search drops labels and a leading The (IMP-7)", () =>
        {
            var fallbacks = ImportQueries.FallbackTitleQueries("The Clockwork Orchard of Brass (Colored)");
            Check.True(fallbacks.Contains("The Clockwork Orchard of Brass") && fallbacks.Contains("Clockwork Orchard of Brass"), string.Join('|', fallbacks));
            Check.True(ImportQueries.FallbackTitleQueries("Salt Road Couriers (Webtoon)").Contains("Salt Road Couriers"), "webtoon label");
            Check.True(ImportQueries.FallbackTitleQueries("Salt Road Couriers (Manga)").Contains("Salt Road Couriers"), "manga label");
            Check.Equal(0, ImportQueries.FallbackTitleQueries("The Hero Returns").Count);
            var queries = ImportQueries.BackupSourceSearchQueries("The Clockwork Orchard (Colored)", "/title/clockwork-orchard-colored");
            Check.Equal("The Clockwork Orchard (Colored)", queries[0]);
            Check.True(queries.Contains("clockwork orchard colored") && queries.Contains("The Clockwork Orchard"), string.Join('|', queries));
            Check.Equal("/title/x", ImportQueries.BackupUrlKey("https://quill.invalid/title/x/"));
            Check.Equal(2, ImportQueries.SourceIdCandidates(Quill).Count);

            // The source only knows the label-free title: the second pass finds the exact URL and the row is added.
            using var setup = new Setup();
            setup.Sources.Answers.Clear();
            setup.Sources.Answer(Quill, "The Clockwork Orchard", SourceManga(601, "The Clockwork Orchard (Colored)", "/title/clockwork-orchard-colored", "Ansel Brightwater"));
            var importId = Id(7);
            var itemId = setup.Row(importId, 1, "The Clockwork Orchard (Colored)", Quill, "/title/clockwork-orchard-colored", "Quillfeed", "Ansel Brightwater");
            setup.Runs.ScheduleRetry(importId, itemId, Person);
            setup.Runs.Idle().GetAwaiter().GetResult();
            var row = setup.Row(importId, itemId);
            Check.True(row.Text("state") == "added", row.Text("state") + ": " + row.Text("reason"));
            Check.True(setup.Sources.Searched.Any(search => search.Query == "The Clockwork Orchard"), "the label-free query was searched");
        });

        suite.Add("imports: the latest click wins and a queued action never overrides Skip (IMP-5)", () =>
        {
            using var setup = new Setup();
            var first = MatchingFixtures.Entry(setup.Database, "Lantern Keeper of the Ninth Harbor", "Mirela Tov");
            var second = MatchingFixtures.Entry(setup.Database, "Lantern Keeper of the Ninth Harbor (Remastered)", "Mirela Tov");
            var importId = Id(9);
            var itemId = setup.Row(importId, 1, "Lantern Keeper of the Ninth Harbor", Harbor, "/series/lantern-keeper-ninth-harbor", "Harborlight (EN)", "Mirela Tov");

            // Match, then Skip while the library add is still pending: Skip stays.
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            setup.Library.Gate = gate;
            var match = setup.Runs.ScheduleMatch(importId, itemId, first, Person);
            Check.Equal("resolving", setup.Row(importId, itemId).Text("state"));
            var skip = setup.Runs.Skip(importId, itemId, Person);
            Check.True(skip["decision_revision"]!.GetValue<long>() > match["decision_revision"]!.GetValue<long>(), "Skip is a newer decision");
            gate.SetResult();
            setup.Runs.Idle().GetAwaiter().GetResult();
            Check.Equal("skipped", setup.Row(importId, itemId).Text("state"));
            Check.Equal(0, setup.Library.Added.Count);

            // Match A, then Match B before A finished: B is the answer and only B reaches the library.
            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            setup.Library.Gate = gate;
            setup.Runs.ScheduleMatch(importId, itemId, first, Person);
            setup.Runs.ScheduleMatch(importId, itemId, second, Person);
            gate.SetResult();
            setup.Runs.Idle().GetAwaiter().GetResult();
            var row = setup.Row(importId, itemId);
            Check.True(row.Text("state") == "added" && row.Integer("entry_id") == second, row.Text("state") + " " + row.Integer("entry_id"));
            Check.Equal(second, setup.Library.Added.Single());

            // Retry queued, then Skip: the retry wakes up to a newer decision and changes nothing.
            var retryRow = setup.Row(Id(10), 1, "Salt and Ember Cartography", Harbor, "/series/salt-and-ember-cartography", "Harborlight (EN)");
            setup.Runs.ScheduleRetry(Id(10), retryRow, Person);
            setup.Runs.Skip(Id(10), retryRow, Person);
            setup.Runs.Idle().GetAwaiter().GetResult();
            Check.Equal("skipped", setup.Row(Id(10), retryRow).Text("state"));

            // A Search replaces a queued Match: the row is reviewable again, never stuck in resolving.
            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            setup.Library.Gate = gate;
            var searchRow = setup.Row(Id(11), 1, "Lantern Keeper of the Ninth Harbor", Harbor, "/series/lantern-keeper-ninth-harbor", "Harborlight (EN)");
            setup.Runs.ScheduleMatch(Id(11), searchRow, first, Person);
            var search = setup.Runs.LiveSuggestionsAsync(Id(11), searchRow, "Lantern Keeper", automatic: false).GetAwaiter().GetResult();
            Check.True(search["suggestions"] is JsonArray, search.ToJsonString());
            gate.SetResult();
            setup.Runs.Idle().GetAwaiter().GetResult();
            Check.Equal("unresolved", setup.Row(Id(11), searchRow).Text("state"));
            Check.True(setup.Log.Kinds.Contains("import.match.manual") && setup.Log.Kinds.Contains("import.skip"), string.Join(',', setup.Log.Kinds));
            Check.True(setup.Log.Entries.Where(entry => entry.Kind == "import.skip").All(entry => entry.Actor == Person), "the actor of a decision");
        });

        suite.Add("imports: a crash leaves no stuck rows (restart recovery)", () =>
        {
            using var setup = new Setup();
            var entry = MatchingFixtures.Entry(setup.Database, "Lantern Keeper of the Ninth Harbor", "Mirela Tov");
            var importId = Id(21);
            var resolving = setup.Row(importId, 1, "Lantern Keeper of the Ninth Harbor", Harbor, "/series/lantern-keeper-ninth-harbor", "Harborlight (EN)", "Mirela Tov");
            var retrying = setup.Row(importId, 2, "Salt and Ember Cartography", Harbor, "/series/salt-and-ember-cartography", "Harborlight (EN)");
            var gone = setup.Row(importId, 3, "The Clockwork Orchard (Colored)", Quill, "/title/clockwork-orchard-colored", "Quillfeed");
            // What a killed process leaves behind: actions in flight and a run still "running" without its upload.
            setup.Sql("UPDATE import_items SET state='resolving',entry_id=$entry,decision_revision=3 WHERE id=$id", ("$entry", entry), ("$id", resolving));
            setup.Sql("UPDATE import_items SET state='retrying',decision_revision=2 WHERE id=$id", ("$id", retrying));
            setup.Sql("UPDATE import_items SET state='resolving',entry_id=987654,decision_revision=1 WHERE id=$id", ("$id", gone));

            var restarted = setup.NewRuns();
            var result = restarted.RecoverInterrupted();
            Check.Equal(3, result["recovered_actions"]!.GetValue<int>());
            restarted.Idle().GetAwaiter().GetResult();
            var rows = setup.Rows(importId);
            Check.True(rows.All(row => row.Text("state") is not ("resolving" or "retrying")), string.Join(',', rows.Select(row => row.Text("state"))));
            Check.Equal("added", setup.Row(importId, resolving).Text("state"));
            Check.Equal(entry, setup.Row(importId, resolving).Integer("entry_id"));
            var missing = setup.Row(importId, gone);
            Check.True(missing.Text("state") == "unresolved" && missing.Text("reason").Contains("no longer available", StringComparison.Ordinal), missing.Text("reason"));
            Check.Equal("failed", restarted.ListRuns()["runs"]!.AsArray()[0]!["state"]!.GetValue<string>());

            // An import interrupted mid-run resumes from its kept upload and never redecides a stored row.
            using var second = new Setup();
            var file = "mihon-0.21.tachibk";
            var bytes = File.ReadAllBytes(Fixture(file));
            var runId = Id(22);
            var folder = Path.Combine(second.DataDirectory, "imports", runId);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "upload.bin"), bytes);
            second.Runs.CreateRun(runId, file);
            var item = MihonBackupReader.Read(bytes).Manga[0].ToJson();
            second.Runs.StoreItem(runId, 1, item, "skipped", "Skipped by user", null);
            var resumed = second.NewRuns().RecoverInterrupted();
            Check.Equal(1, resumed["resumed_runs"]!.GetValue<int>());
            second.Runs.Idle().GetAwaiter().GetResult();
            var resumedRows = second.Rows(runId);
            Check.Equal(2, resumedRows.Count);
            Check.Equal("skipped", resumedRows[0].Text("state"));
            Check.Equal("added", resumedRows[1].Text("state"));
            Check.Equal("completed", second.Runs.ListRuns()["runs"]!.AsArray()[0]!["state"]!.GetValue<string>());
        });

        suite.Add("imports: rejections and remembered matches survive history clears and catalogue resets", () =>
        {
            using var setup = new Setup();
            var right = MatchingFixtures.Entry(setup.Database, "Lantern Keeper of the Ninth Harbor", "Mirela Tov");
            MatchingFixtures.Record(setup.Database, right, Harbor, 777, "Lantern Keeper of the Ninth Harbor", "Mirela Tov");
            var wrong = MatchingFixtures.Entry(setup.Database, "Lantern Keeper", "Someone Else");
            MatchingFixtures.Record(setup.Database, wrong, Quill, 888, "Lantern Keeper", "Someone Else");
            var importId = Id(31);
            var itemId = setup.Row(importId, 1, "Lantern Keeper of the Ninth Harbor", Harbor, "/series/elsewhere", "Harborlight (EN)", "Mirela Tov");

            setup.Runs.ScheduleMatch(importId, itemId, wrong, Person);
            setup.Runs.Idle().GetAwaiter().GetResult();
            Check.Equal("added", setup.Row(importId, itemId).Text("state"));
            var rejected = setup.Runs.ScheduleReject(importId, itemId, Person);
            Check.Equal("unresolved", rejected["state"]!.GetValue<string>());
            setup.Runs.Idle().GetAwaiter().GetResult();
            Check.Equal(wrong, setup.Library.Removed.Single());
            var item = ImportRuns.ItemPayload(setup.Row(importId, itemId));
            Check.True(setup.Runs.IsRejected(item, wrong), "the reject is remembered");
            Check.Throws<ImportConflictException>(() => setup.Runs.ScheduleReject(importId, itemId, Person));

            setup.Runs.ScheduleMatch(importId, itemId, right, Person);
            setup.Runs.Idle().GetAwaiter().GetResult();
            setup.Sql("UPDATE import_runs SET state='completed'");
            setup.Runs.ClearHistory(Person);
            Check.Equal(0, setup.Rows(importId).Count);
            Check.True(setup.Runs.IsRejected(item, wrong), "rejection survives clearing history");
            var (remembered, reason) = setup.Runs.ManualAutoresolveEntry(item, Person);
            Check.True(remembered == right && reason == "remembered manual import match", reason);

            // A catalogue reset rebuilds entries: the remembered manual match is restored from its anchored record.
            var again = setup.Row(Id(32), 1, "Lantern Keeper of the Ninth Harbor", Harbor, "/series/elsewhere", "Harborlight (EN)", "Mirela Tov");
            setup.Runs.ScheduleMatch(Id(32), again, right, Person);
            setup.Runs.Idle().GetAwaiter().GetResult();
            setup.Sql("DELETE FROM source_matches"); setup.Sql("DELETE FROM source_variants"); setup.Sql("DELETE FROM titles"); setup.Sql("DELETE FROM entries");
            var recheck = setup.Runs.RecheckAfterReset(DecisionActor.Automatic);
            Check.Equal(1, recheck["restored"]!.GetValue<int>());
            var restoredRow = setup.Row(Id(32), again);
            Check.True(restoredRow.Text("state") == "added" && setup.Ingest.EntryExists(restoredRow.Integer("entry_id")), restoredRow.Text("reason"));
            Check.True(setup.Ingest.FindExistingBySource(Harbor, 777) == restoredRow.Integer("entry_id"), "the anchored record is back on the restored entry");

            Check.Equal(1L, setup.Runs.ResetManualAutoresolve(Person)["autoresolve_mappings_removed"]!.GetValue<long>());
            Check.True(setup.Runs.ResetRejections(Person)["rejections_cleared"]!.GetValue<long>() >= 1, "explicit reset clears rejections");
            Check.True(!setup.Runs.IsRejected(item, wrong), "rejection gone after its explicit reset");
            Check.True(setup.Log.Kinds.Contains("reset.import.rejections") && setup.Log.Kinds.Contains("import.reject"), string.Join(',', setup.Log.Kinds));
        });

        suite.Add("imports: merge-log recheck after a reset keeps passing matches and reopens the rest (IMP-9)", () =>
        {
            using var setup = new Setup();
            var accepted = setup.Runs.AcceptUpload(File.ReadAllBytes(Fixture("mihon-0.21.tachibk")), "mihon-0.21.tachibk", null, Person);
            setup.Runs.Idle().GetAwaiter().GetResult();
            var importId = accepted["import_id"]!.GetValue<string>();
            var rows = setup.Rows(importId);
            Check.True(rows.All(row => row.Text("state") == "added"), "both added");
            // A catalogue reset clears entries and records but keeps import rows and the decision log.
            setup.Sql("DELETE FROM source_matches"); setup.Sql("DELETE FROM source_variants"); setup.Sql("DELETE FROM titles"); setup.Sql("DELETE FROM entries");
            // The rebuild re-catalogues one of the two source records; the other work is not back yet.
            var rebuilt = setup.Ingest.IngestManga(Harbor, SourceManga(501, "Lantern Keeper of the Ninth Harbor", "/series/lantern-keeper-ninth-harbor", "Mirela Tov")).EntryId!.Value;
            var result = setup.Runs.RecheckAfterReset(DecisionActor.Automatic);
            Check.True(result["kept"]!.GetValue<int>() == 1 && result["reopened"]!.GetValue<int>() == 1, result.ToJsonString());
            var after = setup.Rows(importId);
            Check.True(after[0].Text("state") == "added" && after[0].Integer("entry_id") == rebuilt, after[0].Text("reason"));
            Check.True(after[1].Text("state") == "unresolved" && after[1]["entry_id"] is null, after[1].Text("reason"));
            Check.True(setup.Log.Kinds.Contains("import.recheck.kept") && setup.Log.Kinds.Contains("import.recheck.reopened"), string.Join(',', setup.Log.Kinds));
            // Rechecking again changes nothing: decisions do not move without a new reset.
            var twice = setup.Runs.RecheckAfterReset(DecisionActor.Automatic);
            Check.Equal(0, twice["rechecked"]!.GetValue<int>());
        });

        suite.Add("imports: the stored Suwayomi library plan imports through the same pipeline", () =>
        {
            using var setup = new Setup();
            var plan = SuwayomiImport.Build(
            [
                JsonNode.Parse("""{"id":11,"sourceId":"9101","url":"/series/lantern-keeper-ninth-harbor","title":"Lantern Keeper of the Ninth Harbor"}""")!.AsObject(),
                JsonNode.Parse("""{"id":12,"sourceId":"-4611686018427387904","url":"/title/clockwork-orchard-colored","title":"The Clockwork Orchard (Colored)"}""")!.AsObject(),
            ], null, "http://suwayomi.invalid/", DateTimeOffset.Parse("2026-10-30T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            SuwayomiImport.Save(setup.DataDirectory, plan);
            Check.Throws<KeyNotFoundException>(() => new ImportRuns(setup.Database, setup.Gate, setup.Log, setup.Ingest) { DataDirectory = Path.Combine(setup.DataDirectory, "none") }
                .ImportSuwayomiPlan(Person));
            var queued = setup.Runs.ImportSuwayomiPlan(Person);
            setup.Runs.Idle().GetAwaiter().GetResult();
            var importId = queued["import_id"]!.GetValue<string>();
            Check.Equal(ImportRuns.PlanImportId(plan), importId);
            var rows = setup.Rows(importId);
            Check.True(rows.Count == 2 && rows.All(row => row.Text("state") == "existing" && row["entry_id"] is long), string.Join(',', rows.Select(row => row.Text("state") + ":" + row.Text("reason"))));
            Check.Equal(0, setup.Library.AddCalls);
            Check.True(setup.Ingest.FindExistingBySource(Harbor, 501) is not null, "catalogue entry created from the plan");
            Check.Equal(SuwayomiImport.Imported, SuwayomiImport.Load(setup.DataDirectory)!.Status);
            Check.Equal(ImportRuns.SuwayomiKind, setup.Runs.Item(importId, rows[0].Integer("id"))["item"]!["import_kind"]!.GetValue<string>());
        });

        suite.Add("imports: the /api/v1 import routes are admin-only and answer the pipeline", () =>
        {
            using var setup = new Setup();
            var settings = new AutoSourcarrSettings { AdminToken = "admin" };
            using var host = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
                .ConfigureServices(collection => collection.AddRouting())
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        var group = endpoints.MapGroup("/api/v1").AddEndpointFilter(new AdminAuthorizationFilter(() => settings));
                        ImportsV1Endpoints.Map(group, ApiJsonContext.Default, new ImportsV1Services { Runs = setup.Runs });
                    });
                })).Build();
            host.StartAsync().GetAwaiter().GetResult();
            using var anonymous = host.GetTestServer().CreateClient();
            foreach (var (method, path) in new[] { ("GET", "/api/v1/imports"), ("POST", "/api/v1/imports/clear-history"), ("POST", "/api/v1/imports/mihon-backup"),
                ("POST", "/api/v1/imports/suwayomi-plan"), ("POST", "/api/v1/imports/" + Id(1) + "/items/1/skip") })
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                using var response = anonymous.SendAsync(request).GetAwaiter().GetResult();
                Check.True(response.StatusCode == HttpStatusCode.Unauthorized, method + " " + path + " → " + response.StatusCode);
            }
            using var admin = host.GetTestServer().CreateClient();
            admin.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
            using (var upload = new ByteArrayContent(File.ReadAllBytes(Fixture("mihon-0.19.tachibk"))))
            {
                upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/imports/mihon-backup") { Content = upload };
                request.Headers.Add("X-Filename", "library.tachibk");
                using var response = admin.SendAsync(request).GetAwaiter().GetResult();
                var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check.True(response.StatusCode == HttpStatusCode.OK, text);
                Check.Equal("mihon-0.19", JsonNode.Parse(text)!["version"]!.GetValue<string>());
            }
            setup.Runs.Idle().GetAwaiter().GetResult();
            using (var bad = new ByteArrayContent(Encoding.UTF8.GetBytes("not a backup")))
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/imports/mihon-backup") { Content = bad };
                request.Headers.Add("X-Filename", "library.txt");
                using var response = admin.SendAsync(request).GetAwaiter().GetResult();
                Check.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
            var runs = JsonNode.Parse(admin.GetStringAsync("/api/v1/imports").GetAwaiter().GetResult())!["runs"]!.AsArray();
            Check.Equal(1, runs.Count);
            var importId = runs[0]!["id"]!.GetValue<string>();
            using (var items = admin.PostAsync("/api/v1/imports/" + importId + "/items", new StringContent("""{"state":"added","limit":10}""", Encoding.UTF8, "application/json")).GetAwaiter().GetResult())
            {
                var page = JsonNode.Parse(items.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
                Check.Equal(2, page["total"]!.GetValue<int>());
            }
            using (var missing = admin.PostAsync("/api/v1/imports/" + importId + "/items/999999/skip", new StringContent("{}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult())
                Check.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using (var receipt = admin.GetAsync("/api/v1/imports/" + importId + "/receipt").GetAwaiter().GetResult())
                Check.Equal(HttpStatusCode.OK, receipt.StatusCode);
            host.StopAsync().GetAwaiter().GetResult();
        });
    }
}
