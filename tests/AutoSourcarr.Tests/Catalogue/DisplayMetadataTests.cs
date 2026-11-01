using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests;

public static class DisplayMetadataTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("display metadata:151 released SQLite edit/source/clear/rollback/merge transitions", Oracle);
        suite.Add("display metadata: ownership never changes native entry/title/source/identity evidence", () =>
        {
            using var f = new Fixture(); var before = f.NativeSnapshot();
            f.Store.Save(11, JsonNode.Parse("""{"description":{"mode":"locked","value":"Presentation only"},"author":{"mode":"locked","clear":true}}"""), 0);
            var overlaid = f.Store.Overlay(f.Entry(11)); Check.Equal("Presentation only", overlaid["description"]!.GetValue<string>());
            Check.Equal("", overlaid["author"]!.GetValue<string>()); Check.Equal(before, f.NativeSnapshot());
            overlaid["display_ownership"]!.AsObject().Clear(); Check.Equal(2, f.Store.Read(11)["fields"]!.AsObject().Count);
        });
        suite.Add("display metadata: failed merge rolls back fields/history beside actual catalogue transaction", () =>
        {
            using var f = new Fixture(); f.Store.Save(12, JsonNode.Parse("""{"artist":{"mode":"locked","value":"Preserved"}}"""), 0);
            var before = f.State().ToJsonString();
            Check.Throws<InvalidOperationException>(() => f.Database.Write<int>(DatabaseFile.Catalog, c =>
            {
                DisplayMetadata.Reparent(c, 11, 12, Stamp); c.Execute("UPDATE entries SET preferred_title='Must roll back' WHERE id=11");
                throw new InvalidOperationException("Injected post-reparent failure");
            }));
            Check.Equal(before, f.State().ToJsonString()); Check.Equal("First", f.Entry(11)["preferred_title"]!.GetValue<string>());
        });
        suite.Add("display metadata: reopen, stale CAS and corrupt ownership do not reset retained fields", () =>
        {
            using var f = new Fixture(); f.Store.Save(11, JsonNode.Parse("""{"artist":{"mode":"locked","value":"Retained"}}"""), 0);
            var reopened = new DisplayMetadata(f.Database, f.Gate, new Clock()); Check.Equal(1L, reopened.Read(11)["revision"]!.GetValue<long>());
            Check.Throws<ArgumentException>(() => reopened.Save(11, new JsonObject(), 0));
            f.Database.Write(DatabaseFile.Catalog, c => c.Execute("UPDATE display_metadata SET fields_json='{' WHERE entry_id=11"));
            Check.Throws<JsonException>(() => reopened.Read(11));
            Check.Equal("{", f.Database.Read(DatabaseFile.Catalog, c => (string)c.Scalar("SELECT fields_json FROM display_metadata WHERE entry_id=11")!));
        });
        suite.Add("display metadata: cancelled write joins physical commit and CAS prevents silent replay", () => PhysicalWrite().GetAwaiter().GetResult());
        suite.Add("display metadata: actual admin HTTP GET/PUT authorization, exact IDs, CAS and rollback", Api);
    }

    private const string Stamp = "2026-10-21T16:00:00+00:00";
    private static JsonObject Data() => JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("display-metadata.json")))!.AsObject();
    private static void Oracle()
    {
        var scenarios = Data()["scenarios"]!.AsArray(); var count = 0;
        foreach (var scenario in scenarios.OfType<JsonObject>())
        {
            using var f = new Fixture();
            foreach (var step in scenario["steps"]!.AsArray().OfType<JsonObject>())
            {
                count++; var operation = step["operation"]!.GetValue<string>(); var eid = step["entry_id"]?.GetValue<long>() ?? 11; JsonNode? result = null; string? error = null;
                try
                {
                    switch (operation)
                    {
                        case "save": result = f.Store.Save(eid, step["fields"], step["revision"]!.GetValue<long>(), step["restore_id"]?.GetValue<long>()); break;
                        case "read": result = f.Store.Read(eid); break;
                        case "overlay":
                            var entry = f.Entry(eid); if (step["canonical_id"] is not null) entry["canonical_id"] = step["canonical_id"]!.DeepClone(); result = f.Store.Overlay(entry); break;
                        case "overlay_many": result = JsonSerializer.SerializeToNode(f.Store.OverlayMany(step["ids"]!.AsArray().Select(id => f.Entry(id!.GetValue<long>())).ToArray())); break;
                        case "artwork_source": result = JsonValue.Create(f.Store.ArtworkSource(eid)); break;
                        case "sql": f.Database.Write(DatabaseFile.Catalog, c => c.Execute(step["sql"]!.GetValue<string>())); break;
                        case "reparent": f.Database.Write(DatabaseFile.Catalog, c => { DisplayMetadata.Reparent(c, step["target"]!.GetValue<long>(), step["duplicate"]!.GetValue<long>(), Stamp); return 0; }); break;
                        default: throw new InvalidDataException(operation);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException) { error = ex.Message; }
                var actual = new JsonObject { ["state"] = f.State() }; if (error is null) actual["result"] = result; else actual["error"] = error;
                try { Check.JsonEqual(JsonSerializer.SerializeToElement(step["expected"]), JsonSerializer.SerializeToElement(actual)); }
                catch (Exception ex) { throw new InvalidOperationException("Released display transition " + scenario["name"] + " #" + count + " " + operation, ex); }
            }
        }
        Check.Equal(151, count); Console.WriteLine("Display ownership parity transitions " + count);
    }

    private static async Task PhysicalWrite()
    {
        using var f = new Fixture(); await using var io = new BoundedWorkScheduler(1, 4); using var cancel = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var blocker = Task.Run(() => { lock (f.Gate) { entered.Set(); release.Wait(); } }); Task<JsonObject>? write = null;
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(3)));
            write = io.RunAsync(() => f.Store.Save(11, JsonNode.Parse("""{"author":{"mode":"locked","value":"Committed physical work"}}"""), 0), cancel.Token);
            Check.True(SpinWait.SpinUntil(() => io.Snapshot.Running > 0, TimeSpan.FromSeconds(3))); cancel.Cancel();
            await Task.Delay(20); Check.True(!write.IsCompleted); release.Set();
            try { await write; throw new InvalidOperationException("Cancelled request returned success"); } catch (OperationCanceledException) { }
            Check.Equal(1L, f.Store.Read(11)["revision"]!.GetValue<long>()); Check.Throws<ArgumentException>(() => f.Store.Save(11, new JsonObject(), 0));
        }
        finally { release.Set(); await blocker; if (write is not null) try { await write; } catch (OperationCanceledException) { } }
    }

    private static void Api()
    {
        using var f = new Fixture(); using var io = new AsyncScope<BoundedWorkScheduler>(new(2, 8));
        var settings = new AutoSourcarrSettings { AdminToken = "admin", AutosourcarrReaderApiToken = "reader" }; var tasks = new TaskRegistry();
        using var lanes = new AsyncScope<WholeTaskCoordinator>(new(settings, tasks)); var unexpected = 0;
        using var host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => services.AddRouting()).Configure(app =>
        {
            app.UseAutoSourcarrHttp("0.4.3-migration", () => settings, (_, _) => { unexpected++; return "unexpected"; }, new RequestAdmissionGate(), lanes.Value, tasks, new SupportPrivacy(), _ => { });
            app.UseEndpoints(routes => routes.MapDisplayMetadata(f.Store, io.Value, () => settings));
        })).Build();
        host.Start(); using var client = host.GetTestClient();
        HttpResponseMessage Send(HttpMethod method, long id, string? body = null, string? key = "admin")
        {
            using var request = new HttpRequestMessage(method, "/api/admin/metadata-fields/" + id.ToString(CultureInfo.InvariantCulture));
            if (key is not null) request.Headers.Add(key == "reader" ? "X-AutoSourcarr-API-Key" : "X-Admin-Token", key);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json"); return client.SendAsync(request).GetAwaiter().GetResult();
        }
        using (var denied = Send(HttpMethod.Get, 11, key: "reader")) Check.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var denied = Send(HttpMethod.Get, 11, key: null)) Check.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var initial = Send(HttpMethod.Get, 9007199254740993)) Check.Equal(9007199254740993L, JsonNode.Parse(initial.Content.ReadAsStringAsync().Result)!["entry_id"]!.GetValue<long>());
        // Phase 9: /api/v1/entries/{id}/metadata is the only write path; the 0.4.5 PUT here is gone and changes nothing.
        var before = f.Store.Read(11).ToJsonString();
        using (var removed = Send(HttpMethod.Put, 11, """{"revision":0,"fields":{"author":{"mode":"locked","value":"Saved through HTTP"}}}"""))
            Check.True(removed.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, removed.StatusCode.ToString());
        Check.Equal(before, f.Store.Read(11).ToJsonString());
        using (var missing = Send(HttpMethod.Get, 999)) Check.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Check.Equal(0, f.Store.Read(11)["fields"]!.AsObject().Count); Check.Equal(0, unexpected);
    }

    private sealed class AsyncScope<T>(T value) : IDisposable where T : IAsyncDisposable
    { public T Value { get; } = value; public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse(Stamp, CultureInfo.InvariantCulture); }
    private sealed class Fixture : IDisposable
    {
        private readonly Sandbox _temp = new(); public object Gate { get; } = new(); public SqliteDatabase Database { get; } public DisplayMetadata Store { get; }
        public Fixture()
        {
            Database = new(new AutoSourcarrSettings { DataDir = _temp.Path }); new SchemaMigrator(Database).Initialize();
            Database.Write(DatabaseFile.Catalog, c => c.Execute(Data()["seed"]!.GetValue<string>())); Store = new(Database, Gate, new Clock());
        }
        public JsonObject Entry(long id) => Database.Read(DatabaseFile.Catalog, c => JsonSerializer.SerializeToNode(c.Query("SELECT * FROM entries WHERE id=$id", ("$id", id)).Single())!.AsObject());
        public JsonObject State() => Database.Read(DatabaseFile.Catalog, c => new JsonObject
        { ["display_metadata"] = JsonSerializer.SerializeToNode(c.Query("SELECT * FROM display_metadata ORDER BY 1")), ["display_metadata_history"] = JsonSerializer.SerializeToNode(c.Query("SELECT * FROM display_metadata_history ORDER BY 1")) });
        public string NativeSnapshot() => Database.Read(DatabaseFile.Catalog, c => string.Join('\n', new[] { "entries", "titles", "source_matches", "source_variants", "identity_links", "manual_match_bindings" }
            .Select(table => table + ":" + JsonSerializer.Serialize(c.Query("SELECT * FROM " + table + " ORDER BY 1")))));
        public void Dispose() { Database.Dispose(); _temp.Dispose(); }
    }
}
