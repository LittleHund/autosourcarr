using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Imports;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
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
/// Phase 9 audit fixes: provider-id conflicts refuse the automatic gate (H2), a shutdown keeps a run resumable (H3),
/// previewed import resets (M2), the Match transaction (M3), unrestorable remembered matches return to review (M6) and
/// one run per import id (L3).
/// </summary>
public static partial class ImportsTests
{
    private static string RunState(Setup setup, string importId) =>
        setup.Database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT state FROM import_runs WHERE id=$id", ("$id", importId)) as string ?? "");

    private static ImportRuns QueuedRuns(Setup setup, JobQueue jobs) => new(setup.Database, setup.Gate, setup.Log, setup.Ingest)
    {
        Manual = setup.Manual, Library = setup.Library, Sources = setup.Sources, History = setup.Log, DataDirectory = setup.DataDirectory, Jobs = jobs,
    };

    private static void RegisterAudit(TestSuite suite)
    {
        suite.Add("imports: a provider id on both sides that differs refuses the automatic gate (H2)", () =>
        {
            using var setup = new Setup();
            var entry = MatchingFixtures.Entry(setup.Database, "Lantern Keeper of the Ninth Harbor", "Mirela Tov");
            MatchingFixtures.Record(setup.Database, entry, Harbor, 777, "Lantern Keeper of the Ninth Harbor", "Mirela Tov");
            setup.Sql("INSERT INTO identity_links(entry_id,provider,external_id,confidence,title,last_checked) VALUES($e,'anilist','111',0.97,'Lantern Keeper of the Ninth Harbor','2026-01-01')",
                ("$e", entry));
            var item = new JsonObject
            {
                ["source"] = long.Parse(Harbor, System.Globalization.CultureInfo.InvariantCulture), ["url"] = "/series/elsewhere",
                ["title"] = "Lantern Keeper of the Ninth Harbor", ["author"] = "Mirela Tov", ["sourceName"] = "Harborlight (EN)",
                [ImportRuns.MetadataKey] = new JsonObject
                {
                    ["identities"] = new JsonArray(new JsonObject { ["provider"] = "AniList", ["external_id"] = "222", ["confidence"] = 0.99 }),
                },
            };
            // The exact full title alone would pass; the different AniList id refuses it.
            var refused = setup.Runs.CandidateEvidence(item, entry);
            Check.True(!refused.Eligible && refused.Reason.StartsWith("different anilist identities", StringComparison.Ordinal), refused.Reason);
            Check.Equal("different anilist identities (222 / 111)", setup.Runs.IdentityConflict(item, entry));
            item[ImportRuns.MetadataKey]!["identities"]![0]!["external_id"] = "111";
            var shared = setup.Runs.CandidateEvidence(item, entry);
            Check.True(shared.Eligible && shared.SharedIdentities.Contains("anilist:111"), shared.Reason);
            // Different providers, or one common id among several, are no conflict.
            Check.True(ImportRuns.IdentityConflict(["mangadex:abc"], ["anilist:1"]) is null, "different providers");
            Check.True(ImportRuns.IdentityConflict(["anilist:1", "anilist:2"], ["anilist:2"]) is null, "one common id");
        });

        suite.Add("imports: a shutdown keeps the run resumable; only a person's Cancel ends it (H3)", () =>
        {
            const string file = "mihon-0.21.tachibk";
            var bytes = File.ReadAllBytes(Fixture(file));
            using (var setup = new Setup())
            {
                var tasks = new TaskRegistry(); var jobs = new JobQueue(tasks);
                setup.Library.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var runs = QueuedRuns(setup, jobs);
                var importId = runs.AcceptUpload(bytes, file, Id(41), Person)["import_id"]!.GetValue<string>();
                Check.True(SpinWait.SpinUntil(() => setup.Library.AddCalls > 0, TimeSpan.FromSeconds(10)), "the import reached the library step");
                jobs.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(45));
                Check.Equal("running", RunState(setup, importId));
                var upload = Path.Combine(setup.DataDirectory, "imports", importId, "upload.bin");
                Check.True(File.Exists(upload), "the upload stays for the next start");
                setup.Library.Gate = null;
                var restarted = setup.NewRuns();
                Check.Equal(1, restarted.RecoverInterrupted()["resumed_runs"]!.GetValue<int>());
                restarted.Idle().GetAwaiter().GetResult();
                Check.Equal("completed", RunState(setup, importId));
                Check.True(!File.Exists(upload), "the upload is removed once the resumed import finished");
            }
            using (var setup = new Setup())
            {
                var tasks = new TaskRegistry(); var jobs = new JobQueue(tasks);
                try
                {
                    setup.Library.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var runs = QueuedRuns(setup, jobs);
                    var accepted = runs.AcceptUpload(bytes, file, Id(42), Person);
                    var importId = accepted["import_id"]!.GetValue<string>();
                    Check.True(SpinWait.SpinUntil(() => setup.Library.AddCalls > 0, TimeSpan.FromSeconds(10)), "the import reached the library step");
                    Check.True(tasks.RequestCancelAsync(accepted["task_id"]!.GetValue<string>()).GetAwaiter().GetResult(), "the import task is cancellable");
                    runs.Idle().GetAwaiter().GetResult();
                    Check.Equal("cancelled", RunState(setup, importId));
                    Check.True(!Directory.Exists(Path.Combine(setup.DataDirectory, "imports", importId)), "a cancelled import drops its upload");
                    Check.Equal(0, setup.NewRuns().RecoverInterrupted()["resumed_runs"]!.GetValue<int>());
                }
                finally { jobs.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(45)); }

                // Clearing history removes the kept upload of a finished run; a running run keeps its upload.
                var failedFolder = Path.Combine(setup.DataDirectory, "imports", Id(43));
                var runningFolder = Path.Combine(setup.DataDirectory, "imports", Id(44));
                foreach (var (id, folder) in new[] { (Id(43), failedFolder), (Id(44), runningFolder) })
                {
                    Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "upload.bin"), bytes);
                    setup.Runs.CreateRun(id, file);
                }
                setup.Sql("UPDATE import_runs SET state='failed' WHERE id=$id", ("$id", Id(43)));
                setup.Runs.ClearHistory(Person);
                Check.True(!Directory.Exists(failedFolder), "cleared history drops the kept upload");
                Check.True(File.Exists(Path.Combine(runningFolder, "upload.bin")), "a running import keeps its upload");
            }
        });

        suite.Add("imports: rejection and remembered-match resets need the preview they confirm (M2)", () =>
        {
            using var setup = new Setup();
            var wrong = MatchingFixtures.Entry(setup.Database, "Lantern Keeper", "Someone Else");
            MatchingFixtures.Record(setup.Database, wrong, Quill, 888, "Lantern Keeper", "Someone Else");
            var item = new JsonObject
            {
                ["source"] = long.Parse(Harbor, System.Globalization.CultureInfo.InvariantCulture), ["url"] = "/series/elsewhere",
                ["title"] = "Lantern Keeper of the Ninth Harbor", ["sourceName"] = "Harborlight (EN)",
            };
            var preview = setup.Runs.PreviewResetRejections();
            Check.Equal(0L, preview["rejections"]!.GetValue<long>());
            setup.Runs.RememberRejection(item, wrong);
            Check.Throws<ImportConflictException>(() => setup.Runs.ResetRejections(Person, preview["preview_token"]!.GetValue<string>()));
            Check.True(setup.Runs.IsRejected(item, wrong), "a stale preview resets nothing");
            var fresh = setup.Runs.PreviewResetRejections();
            Check.True(fresh["rejections"]!.GetValue<long>() >= 1, fresh.ToJsonString());
            Check.True(setup.Runs.ResetRejections(Person, fresh["preview_token"]!.GetValue<string>())["rejections_cleared"]!.GetValue<long>() >= 1, "confirmed reset");
            Check.True(!setup.Runs.IsRejected(item, wrong), "rejection forgotten");

            var mapping = setup.Runs.PreviewResetManualAutoresolve();
            setup.Runs.RememberManualAutoresolve(item, wrong, Person);
            Check.Throws<ImportConflictException>(() => setup.Runs.ResetManualAutoresolve(Person, mapping["preview_token"]!.GetValue<string>()));
            var current = setup.Runs.PreviewResetManualAutoresolve();
            Check.Equal(1L, current["autoresolve_mappings"]!.GetValue<long>());
            Check.Equal(1L, setup.Runs.ResetManualAutoresolve(Person, current["preview_token"]!.GetValue<string>())["autoresolve_mappings_removed"]!.GetValue<long>());

            // The routes: GET preview, POST {confirm, preview_token}; the recheck after a reset is a task (202).
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
            using var admin = host.GetTestServer().CreateClient();
            admin.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
            HttpResponseMessage Post(string path, string body) => admin.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            setup.Runs.RememberRejection(item, wrong);
            using (var unconfirmed = Post("/api/v1/imports/reset-rejections", "{}")) Check.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
            using (var stale = Post("/api/v1/imports/reset-rejections", """{"confirm":true,"preview_token":"stale"}""")) Check.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var token = JsonNode.Parse(admin.GetStringAsync("/api/v1/imports/reset-rejections").GetAwaiter().GetResult())!["preview_token"]!.GetValue<string>();
            using (var confirmed = Post("/api/v1/imports/reset-rejections", new JsonObject { ["confirm"] = true, ["preview_token"] = token }.ToJsonString()))
                Check.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            Check.True(!setup.Runs.IsRejected(item, wrong), "reset through the route");
            var mappingToken = JsonNode.Parse(admin.GetStringAsync("/api/v1/imports/reset-manual-autoresolve").GetAwaiter().GetResult())!["preview_token"]!.GetValue<string>();
            using (var confirmed = Post("/api/v1/imports/reset-manual-autoresolve", new JsonObject { ["confirm"] = true, ["preview_token"] = mappingToken }.ToJsonString()))
                Check.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            using (var recheck = Post("/api/v1/imports/recheck-after-reset", "{}")) Check.Equal(HttpStatusCode.Accepted, recheck.StatusCode);
            setup.Runs.Idle().GetAwaiter().GetResult();
            Check.True(setup.Log.Kinds.Count(kind => kind == "reset.import.rejections") == 2, string.Join(',', setup.Log.Kinds));
            host.StopAsync().GetAwaiter().GetResult();
        });

        suite.Add("imports: racing Match clicks leave the remembered match on the row's entry (M3)", () =>
        {
            using var setup = new Setup();
            var first = MatchingFixtures.Entry(setup.Database, "Lantern Keeper of the Ninth Harbor", "Mirela Tov");
            MatchingFixtures.Record(setup.Database, first, Harbor, 777, "Lantern Keeper of the Ninth Harbor", "Mirela Tov");
            var second = MatchingFixtures.Entry(setup.Database, "Lantern Keeper of the Ninth Harbor (Remastered)", "Mirela Tov");
            MatchingFixtures.Record(setup.Database, second, Harbor, 778, "Lantern Keeper of the Ninth Harbor (Remastered)", "Mirela Tov");
            for (var round = 0; round < 6; round++)
            {
                var importId = Id(50 + round);
                var itemId = setup.Row(importId, 1, "Lantern Keeper of the Ninth Harbor", Harbor, "/series/race-" + round, "Harborlight (EN)", "Mirela Tov");
                var clicks = new[] { first, second }.Select(entry => Task.Run(() => setup.Runs.ScheduleMatch(importId, itemId, entry, Person))).ToArray();
                Task.WaitAll(clicks);
                setup.Runs.Idle().GetAwaiter().GetResult();
                var row = setup.Row(importId, itemId);
                var key = setup.Runs.RejectionKey(ImportRuns.ItemPayload(row));
                var remembered = setup.Database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT target_entry_id_hint FROM import_manual_autoresolve WHERE backup_key=$key", ("$key", key)));
                Check.True(row.Text("state") is "added" or "existing", row.Text("state") + ": " + row.Text("reason"));
                Check.Equal(row.Integer("entry_id"), Convert.ToInt64(remembered, System.Globalization.CultureInfo.InvariantCulture));
            }
        });

        suite.Add("imports: a remembered manual match whose work is gone returns to review after a reset (M6)", () =>
        {
            using var setup = new Setup();
            var entry = MatchingFixtures.Entry(setup.Database, "Salt and Ember Cartography", "Ines Varo");
            var importId = Id(61);
            var itemId = setup.Row(importId, 1, "Salt and Ember Cartography", Harbor, "/series/salt-and-ember-cartography", "Harborlight (EN)", "Ines Varo");
            setup.Runs.ScheduleMatch(importId, itemId, entry, Person);
            setup.Runs.Idle().GetAwaiter().GetResult();
            Check.True(setup.Row(importId, itemId).Text("state") is "added" or "existing", setup.Row(importId, itemId).Text("reason"));
            setup.Sql("DELETE FROM source_matches"); setup.Sql("DELETE FROM source_variants"); setup.Sql("DELETE FROM titles"); setup.Sql("DELETE FROM entries");
            var result = setup.Runs.RecheckAfterReset(DecisionActor.Automatic);
            Check.True(result["restored"]!.GetValue<int>() == 0 && result["reopened"]!.GetValue<int>() == 1 && result["manual_reopened"]!.GetValue<int>() == 1, result.ToJsonString());
            var row = setup.Row(importId, itemId);
            Check.True(row.Text("state") == "unresolved" && row["entry_id"] is null, row.Text("state"));
            Check.True(row.Text("reason").Contains("could not be restored", StringComparison.Ordinal), row.Text("reason"));
            Check.True(setup.Log.Kinds.Contains("import.recheck.reopened"), string.Join(',', setup.Log.Kinds));
            // The remembered match itself is a manual decision: it stays until its explicit reset.
            Check.Equal(1L, setup.Database.Read(DatabaseFile.Catalog, c => Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM import_manual_autoresolve"), System.Globalization.CultureInfo.InvariantCulture)));
        });

        suite.Add("imports: the same X-Import-Id twice creates one run and one import (L3)", () =>
        {
            const string file = "mihon-0.21.tachibk";
            var bytes = File.ReadAllBytes(Fixture(file));
            using var setup = new Setup();
            var answers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => setup.Runs.AcceptUpload(bytes, file, Id(70), Person))).ToArray();
            Task.WaitAll(answers);
            setup.Runs.Idle().GetAwaiter().GetResult();
            Check.True(answers.All(answer => answer.Result["import_id"]!.GetValue<string>() == Id(70)), "one identity");
            Check.Equal(1, answers.Count(answer => answer.Result["version"] is not null));
            Check.Equal(1L, setup.Database.Read(DatabaseFile.Catalog, c => Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM import_runs"), System.Globalization.CultureInfo.InvariantCulture)));
            Check.Equal(2, setup.Library.AddCalls);
            Check.Equal(1, setup.Log.Kinds.Count(kind => kind == "import.upload"));
        });
    }
}
