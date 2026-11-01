using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Inventory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests;

/// <summary>Phase 6d: orphan seed list, Installed / Not installed orphan groups (SRC-3), labels and history discovery.</summary>
public static class SourceOrphanTests
{
    private const string Prefix = "eu.kanade.tachiyomi.extension.en.";
    private const string Store = "https://repo.example/index.min.json";
    private const string Head = "1111111111111111111111111111111111111111", Older = "2222222222222222222222222222222222222222";

    public static void Register(TestSuite suite)
    {
        suite.Add("source orphans: the bundled seed list parses and seeds an instance without orphans.json", () =>
        {
            var bytes = SourceInventoryServices.BundledSeed();
            Check.True(bytes is not null, "The seed list is embedded in the Api assembly.");
            var seed = SourceOrphans.ParseSeed(bytes!);
            var records = seed["records"]!.AsObject();
            Check.Equal(47, records.Count);
            Check.True(seed["current"]!.AsArray().Count > 1000, "The seed keeps the current catalog.");
            var firescans = records[Prefix + "firescans"]!.AsObject();
            Check.Equal("Firescans", firescans["name"]!.GetValue<string>());
            Check.Equal("src/en/firescans", firescans["module"]!.GetValue<string>());
            Check.Equal(false, firescans["archive_available"]!.GetValue<bool>());

            Check.Throws<InvalidDataException>(() => SourceOrphans.ParseSeed(Encoding.UTF8.GetBytes("{\"records\":[]}")));
            Check.Throws<InvalidDataException>(() => SourceOrphans.ParseSeed(Encoding.UTF8.GetBytes("{\"records\":{\"" + Prefix + "a\":{\"pkgName\":\"" + Prefix + "b\"}}}")));
            Check.Throws<InvalidDataException>(() => SourceOrphans.ParseSeed(Encoding.UTF8.GetBytes("{\"current\":[1]}")));
            Check.Throws<InvalidDataException>(() => SourceOrphans.ParseSeed(Encoding.UTF8.GetBytes("{\"labels\":{\"" + Prefix + "a\":{\"orphaned\":\"yes\"}}}")));
            Check.Throws<InvalidDataException>(() => SourceOrphans.ParseSeed([0xff, 0xfe, 0x7b]));

            using var temp = new Sandbox();
            using var host = new FakeHost([]);
            var orphans = host.Orphans(temp.Path, () => bytes);
            Check.Equal(47, orphans.Records().Count);
            Check.True(!File.Exists(Path.Combine(temp.Path, "orphans.json")), "Reading the seed does not write state.");
        });

        suite.Add("source orphans: snapshot groups orphans under Installed and Not installed", () =>
        {
            using var temp = new Sandbox();
            using var host = new FakeHost(Inventory());
            var orphans = host.Orphans(temp.Path, () => Seed());
            Await(orphans.RefreshInventoryAsync());
            var snapshot = orphans.Snapshot(repairAvailable: false, configured: [Store]);
            Check.Equal(true, snapshot["inventory_known"]!.GetValue<bool>());
            var groups = snapshot["groups"]!.AsArray();
            Check.Equal(2, groups.Count);
            Check.Equal("installed", groups[0]!["key"]!.GetValue<string>());
            Check.Equal("Installed", groups[0]!["title"]!.GetValue<string>());
            Check.Equal("not-installed", groups[1]!["key"]!.GetValue<string>());
            Check.Equal("Not installed", groups[1]!["title"]!.GetValue<string>());
            var installed = Names(groups[0]!["items"]);
            var notInstalled = Names(groups[1]!["items"]);
            // Archived alpha and obsolete delta are installed orphans; normal epsilon and listed zeta are not orphans.
            Check.Equal("alpha,delta", string.Join(',', installed));
            Check.Equal("beta,omega", string.Join(',', notInstalled));
            Check.Equal(4, snapshot["items"]!.AsArray().Count);
            Check.Equal(2, groups[0]!["count"]!.GetValue<int>());

            var alpha = Item(snapshot, "alpha");
            Check.Equal("installed-unverified", alpha["state"]!.GetValue<string>());
            Check.Equal(true, alpha["installed"]!.GetValue<bool>());
            Check.True(alpha["sources"] is null, "The source list stays out of the page rows.");
            var delta = Item(snapshot, "delta");
            Check.Equal("Installed package; archive provenance not yet located", delta["origin"]!.GetValue<string>());
            var beta = Item(snapshot, "beta");
            Check.Equal("unavailable", beta["state"]!.GetValue<string>());
            Check.Equal(false, beta["can_install"]!.GetValue<bool>());
            var omega = Item(snapshot, "omega");
            Check.Equal("available", omega["state"]!.GetValue<string>());
            Check.Equal(true, omega["can_install"]!.GetValue<bool>());
            Check.Equal(false, omega["can_repair"]!.GetValue<bool>());
            Check.Equal(false, alpha["can_repair"]!.GetValue<bool>());
            Check.Equal(false, snapshot["repair_available"]!.GetValue<bool>());
            // With the in-app domain repair, installed orphans can be repaired; a not-installed one needs installing first.
            var available = orphans.Snapshot(repairAvailable: true, configured: [Store]);
            Check.Equal(true, available["worker_configured"]!.GetValue<bool>());
            Check.Equal(true, available["repair_available"]!.GetValue<bool>());
            Check.Equal(true, Item(available, "alpha")["can_repair"]!.GetValue<bool>());
            Check.Equal(true, Item(available, "delta")["can_repair"]!.GetValue<bool>());
            Check.Equal(false, Item(available, "omega")["can_repair"]!.GetValue<bool>());

            // The repair record of the last promotion marks its installed orphan as repaired.
            var repaired = orphans.Snapshot(false, [Store], new JsonObject { ["state"] = "installed", ["extension"] = new JsonObject { ["pkgName"] = Prefix + "alpha" } });
            Check.Equal("repaired", Item(repaired, "alpha")["state"]!.GetValue<string>());

            // Classification of the extension list (Manage sources) matches the groups.
            var annotated = orphans.Annotate(Inventory(), [Store]).ToDictionary(row => row["pkgName"]!.GetValue<string>()[Prefix.Length..]);
            Check.Equal(true, annotated["alpha"]["is_orphaned"]!.GetValue<bool>());
            Check.Equal("archived", annotated["alpha"]["orphan_reason"]!.GetValue<string>());
            Check.Equal(false, annotated["epsilon"]["is_orphaned"]!.GetValue<bool>());
            Check.Equal(false, annotated["epsilon"]["can_mark_orphan"]!.GetValue<bool>());
            Check.Equal(true, annotated["theta"]["can_mark_orphan"]!.GetValue<bool>());
            Check.Equal(true, annotated["theta"]["repository_missing"]!.GetValue<bool>());
            Check.Equal(false, annotated["zeta"]["is_orphaned"]!.GetValue<bool>());
        });

        suite.Add("source orphans: mark and unmark persist to orphans.json and survive a restart", () =>
        {
            using var temp = new Sandbox();
            using var host = new FakeHost(Inventory());
            var admin = new SemaphoreSlim(1, 1);
            var orphans = host.Orphans(temp.Path, () => Seed());
            Check.Equal(400, Check.Throws<SourceOrphanException>(() => Await(orphans.SetOrphanedAsync("not a package", true, admin, [Store]))).StatusCode);
            Check.Equal(404, Check.Throws<SourceOrphanException>(() => Await(orphans.SetOrphanedAsync(Prefix + "zeta", true, admin, [Store]))).StatusCode);
            Check.Equal(409, Check.Throws<SourceOrphanException>(() => Await(orphans.SetOrphanedAsync(Prefix + "epsilon", true, admin, [Store]))).StatusCode);
            Check.Equal(1, admin.CurrentCount);

            var marked = Await(orphans.SetOrphanedAsync(Prefix + "theta", true, admin, [Store]));
            Check.Equal("Source marked as orphaned.", marked["message"]!.GetValue<string>());
            Check.Equal(true, marked["extension"]!["is_orphaned"]!.GetValue<bool>());
            Check.Equal("manual", marked["extension"]!["orphan_reason"]!.GetValue<string>());
            Check.Equal(true, marked["extension"]!["can_unmark_orphan"]!.GetValue<bool>());
            var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(temp.Path, "orphans.json")))!;
            Check.Equal(true, saved["labels"]![Prefix + "theta"]!["orphaned"]!.GetValue<bool>());
            Check.Equal("Theta", saved["labels"]![Prefix + "theta"]!["name"]!.GetValue<string>());
            Check.Equal(4, saved["records"]!.AsObject().Count);

            // A new instance (restart) reads the saved labels, and the engine-side annotator agrees.
            var restarted = host.Orphans(temp.Path, () => Seed(extra: true));
            Check.Equal(4, restarted.Records().Count);
            Check.Equal(true, restarted.Labels()[Prefix + "theta"]!["orphaned"]!.GetValue<bool>());
            Await(restarted.RefreshInventoryAsync());
            var theta = Item(restarted.Snapshot(false, [Store]), "theta");
            Check.Equal(true, theta["installed"]!.GetValue<bool>());
            Check.Equal("Marked as orphaned. Its installed package and source settings are unchanged.", theta["message"]!.GetValue<string>());
            var engine = new InstalledSourceAnnotations(temp.Path).Annotate([Extension("theta", installed: true, store: "https://gone.example/index.min.json")]);
            Check.Equal(true, engine[0]["is_orphaned"]!.GetValue<bool>());

            var returned = Await(restarted.SetOrphanedAsync(Prefix + "theta", false, admin, [Store]));
            Check.Equal("Source returned to Installed.", returned["message"]!.GetValue<string>());
            Check.Equal(false, returned["extension"]!["is_orphaned"]!.GetValue<bool>());
            var again = host.Orphans(temp.Path, () => Seed());
            Check.Equal(false, again.Labels()[Prefix + "theta"]!["orphaned"]!.GetValue<bool>());
            Await(again.RefreshInventoryAsync());
            Check.True(!Names(again.Snapshot(false, [Store])["items"]).Contains("theta"), "An unmarked source leaves the orphan groups.");
            Check.Equal(true, again.Annotate([Extension("theta", installed: true, store: "https://gone.example/index.min.json")], [Store])[0]["can_mark_orphan"]!.GetValue<bool>());
        });

        suite.Add("source orphans: SRC-8 a newer version in a configured repository is an official replacement", () =>
        {
            JsonObject Row(string name, bool hasUpdate, string? store = Store, bool obsolete = false, bool listed = true, bool installed = true)
            {
                var row = Extension(name, installed, store, obsolete, listed);
                row["hasUpdate"] = hasUpdate;
                return row;
            }
            var rows = new List<JsonObject>
            {
                Row("updated", hasUpdate: true), Row("same", hasUpdate: false), Row("elsewhere", hasUpdate: true, store: "https://gone.example/index.min.json"),
                Row("obsolete", hasUpdate: true, obsolete: true), Row("local", hasUpdate: true, store: "", listed: false),
                Row("available", hasUpdate: true, installed: false),
            };
            var found = SourceOrphans.MaintainedVersion(rows, Prefix + "updated", [Store]);
            Check.True(found is not null && found.Contains(Store, StringComparison.Ordinal) && found.Contains("1.4.1", StringComparison.Ordinal), "newer listed version found: " + found);
            Check.True(SourceOrphans.MaintainedVersion(rows, Prefix + "same", [Store]) is null, "the same broken version is no replacement");
            Check.True(SourceOrphans.MaintainedVersion(rows, Prefix + "elsewhere", [Store]) is null, "a repository that is no longer configured does not count");
            Check.True(SourceOrphans.MaintainedVersion(rows, Prefix + "elsewhere", null) is not null, "unknown configuration counts as configured");
            Check.True(SourceOrphans.MaintainedVersion(rows, Prefix + "obsolete", [Store]) is null, "an obsolete listing is not maintained");
            Check.True(SourceOrphans.MaintainedVersion(rows, Prefix + "local", [Store]) is null, "a local-only package has no repository version");
            Check.True(SourceOrphans.MaintainedVersion(rows, Prefix + "available", [Store]) is null, "a package that is not installed has nothing to repair");
            Check.True(SourceOrphans.MaintainedVersion(rows, Prefix + "missing", [Store]) is null, "unknown package");
        });

        suite.Add("source orphans: discovery records packages that left the original catalog", () =>
        {
            using var temp = new Sandbox();
            using var host = new FakeHost(Inventory());
            var fetcher = new HistoryFetcher();
            var orphans = new SourceOrphans(temp.Path, host.Client, fetcher, () => null);
            var (start, answer) = orphans.BeginDiscovery();
            Check.True(start && answer["queued"]!.GetValue<bool>());
            Check.Equal(false, orphans.BeginDiscovery().Start);
            var result = Await(orphans.DiscoverAsync());
            Check.Equal("complete", result["state"]!.GetValue<string>());
            Check.Equal(2L, result["snapshots"]!.GetValue<long>());
            var records = orphans.Records();
            Check.Equal(1, records.Count);
            var omega = records[Prefix + "omega"]!;
            Check.Equal(SourceOrphans.Raw + Older + "/apk/tachiyomi-en.omega-v1.4.2.apk", omega["archive_url"]!.GetValue<string>());
            Check.Equal("available", omega["state"]!.GetValue<string>());
            Check.Equal("-5", omega["sources"]![0]!["id"]!.GetValue<string>());
            Check.True(File.Exists(Path.Combine(temp.Path, "orphans.json")));
            Check.Equal(429, Check.Throws<SourceOrphanException>(() => orphans.BeginDiscovery()).StatusCode);

            Check.Equal("", SourceOrphans.PackageUrl("https://mirror.example/keiyoushi/extensions/repo/apk/a.apk", Older));
            Check.Equal(SourceOrphans.Raw + Older + "/apk/a.apk", SourceOrphans.PackageUrl("https://github.com/keiyoushi/extensions/raw/repo/apk/a.apk", Older));
            Check.Throws<InvalidDataException>(() => SourceOrphans.PackageUrl("https://github.com/keiyoushi/extensions/raw/repo/apk/a.apk", "repo"));
        });

        suite.Add("source orphans: /api/v1 routes answer the snapshot, labels, the repair placeholder and the maintenance status", () =>
        {
            using var temp = new Sandbox();
            using var engine = new FakeHost(Inventory());
            var settings = new AutoSourcarrSettings { AdminToken = "admin", SourceEngineMode = "integrated" };
            var maintenance = Path.Combine(temp.Path, "data", "source-maintenance");
            var inventory = new SourceInventoryServices
            {
                Orphans = engine.Orphans(maintenance, () => Seed()), Maintenance = new SourceMaintenanceState(maintenance, temp.Path),
                Client = engine.Client, AdminLock = new SemaphoreSlim(1, 1)
            };
            var services = new ApiV1Services(() => settings, temp.Path, null) { SourceInventory = inventory };
            using var server = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
                .ConfigureServices(collection => collection.AddRouting())
                .Configure(app => { app.UseRouting(); app.UseEndpoints(endpoints => endpoints.MapApiV1(services)); })).Build();
            server.StartAsync().GetAwaiter().GetResult();
            var client = server.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Admin-Token", "admin");

            var snapshot = Json(client.GetAsync("/api/v1/source-orphans").GetAwaiter().GetResult(), HttpStatusCode.OK);
            Check.Equal(2, snapshot["groups"]!.AsArray().Count);
            Check.Equal(SourceOrphans.Notice, snapshot["notice"]!.GetValue<string>());
            Check.Equal(HttpStatusCode.NotFound, client.PostAsync("/api/v1/source-orphans/" + Prefix + "zeta/mark", null).GetAwaiter().GetResult().StatusCode);
            var marked = Json(client.PostAsync("/api/v1/source-orphans/" + Prefix + "theta/mark", null).GetAwaiter().GetResult(), HttpStatusCode.OK);
            Check.Equal(true, marked["extension"]!["is_orphaned"]!.GetValue<bool>());
            // The repair route forwards to /api/v1/source-repairs; without the domain repair service it is unavailable.
            Check.Equal(HttpStatusCode.ServiceUnavailable, client.PostAsync("/api/v1/source-orphans/" + Prefix + "alpha/repair", null).GetAwaiter().GetResult().StatusCode);
            Check.Equal(HttpStatusCode.NotFound, client.PostAsync("/api/v1/source-orphans/" + Prefix + "nothing/install", null).GetAwaiter().GetResult().StatusCode);
            var status = Json(client.GetAsync("/api/v1/source-maintenance").GetAwaiter().GetResult(), HttpStatusCode.OK);
            Check.Equal(false, status["configured"]!.GetValue<bool>());
            Check.Equal(false, status["watch"]!["enabled"]!.GetValue<bool>());
            Check.Equal("idle", status["operation"]!["state"]!.GetValue<string>());
            var watch = client.PutAsync("/api/v1/source-maintenance/watch", new StringContent("{\"enabled\":true}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check.Equal(HttpStatusCode.Conflict, watch.StatusCode);
            var installed = Json(client.GetAsync("/api/v1/source-maintenance/installed").GetAwaiter().GetResult(), HttpStatusCode.OK);
            Check.Equal(5, installed["items"]!.AsArray().Count);
            Check.Equal(HttpStatusCode.Conflict, client.PostAsync("/api/v1/source-maintenance/rollback", null).GetAwaiter().GetResult().StatusCode);
        });

        suite.Add("source orphans: hard rule 8 an archived APK with native code is refused and nothing is installed", () =>
        {
            using var temp = new Sandbox();
            using var host = new FakeHost(Inventory());
            var package = Prefix + "omega";
            var manifest = "<manifest xmlns:android=\"" + SourceManifest.AndroidNamespace + "\" package=\"" + package + "\" android:versionName=\"1.4.1\">"
                + "<uses-permission android:name=\"android.permission.INTERNET\"/><application><meta-data android:name=\"tachiyomi.extension.class\" android:value=\".Omega\"/></application></manifest>";
            var dex = Encoding.Latin1.GetBytes("dex\n035\0padding https://omega.example/api x" + new string('a', 200));
            var apk = Zip(("AndroidManifest.xml", Encoding.UTF8.GetBytes(manifest)), ("classes.dex", dex), ("lib/arm64-v8a/libevil.so", new byte[300]));
            var url = SourceOrphans.Raw + Older + "/apk/tachiyomi-en.omega-v1.4.1.apk";
            var orphans = new SourceOrphans(temp.Path, host.Client, new ArchiveFetcher(url, apk), () => Seed());
            orphans.BeginInstall(package);
            var admin = new SemaphoreSlim(1, 1);
            var result = Await(orphans.InstallAsync(package, admin));
            Check.Equal("failed", result["state"]!.GetValue<string>());
            var message = result["message"]!.GetValue<string>();
            Check.True(message.Contains("native code", StringComparison.Ordinal) && message.Contains("not installed", StringComparison.Ordinal), message);
            Check.True(result["inspection"] is null, "A refused archive carries no inspection result.");
            Check.True(host.Paths.Count > 0 && host.Paths.All(path => path == "/v1/extensions"), "Only the extension list was read: " + string.Join(',', host.Paths));
            Check.True(!Directory.Exists(Path.Combine(temp.Path, "archives")), "A refused archive is not kept.");
            var record = orphans.Records()[package]!;
            Check.Equal("available", record["state"]!.GetValue<string>());
            Check.True(record["download_sha256"] is null && record["installed_at"] is null, "The record keeps its original provenance.");
            Check.Equal(1, admin.CurrentCount);
        });
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>alpha/beta/omega are archived records; gamma is still in the current catalog.</summary>
    private static byte[] Seed(bool extra = false)
    {
        JsonObject Record(string name, bool available) => new()
        {
            ["pkgName"] = Prefix + name, ["name"] = char.ToUpperInvariant(name[0]) + name[1..], ["versionName"] = "1.4.1", ["lang"] = "en",
            ["origin"] = "Keiyoushi", ["commit"] = Older, ["commit_url"] = SourceOrphans.Repository + "/commit/" + Older,
            ["archive_url"] = SourceOrphans.Raw + Older + "/apk/tachiyomi-en." + name + "-v1.4.1.apk", ["sha256"] = "", ["state"] = "available",
            ["archive_available"] = available, ["sources"] = new JsonArray(new JsonObject { ["id"] = "7", ["name"] = name, ["lang"] = "en", ["baseUrl"] = "https://" + name + ".example" })
        };
        var records = new JsonObject { [Prefix + "alpha"] = Record("alpha", true), [Prefix + "beta"] = Record("beta", false), [Prefix + "omega"] = Record("omega", true), [Prefix + "gamma"] = Record("gamma", true) };
        if (extra) records[Prefix + "extra"] = Record("extra", true);
        return Encoding.UTF8.GetBytes(new JsonObject { ["records"] = records, ["current"] = new JsonArray(Prefix + "gamma"), ["refresh"] = new JsonObject { ["state"] = "complete" }, ["operation"] = new JsonObject { ["state"] = "idle" } }.ToJsonString());
    }

    private static JsonObject Extension(string name, bool installed, string? store = Store, bool obsolete = false, bool listed = true) => new()
    {
        ["pkgName"] = Prefix + name, ["name"] = char.ToUpperInvariant(name[0]) + name[1..], ["lang"] = "en", ["versionName"] = "1.4.1",
        ["apkName"] = "tachiyomi-en." + name + "-v1.4.1.apk", ["apkUrl"] = listed ? "https://repo.example/apk/" + name + ".apk" : null,
        ["isInstalled"] = installed, ["isObsolete"] = obsolete, ["hasUpdate"] = false, ["storeIndexUrl"] = store,
        ["source"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["id"] = name.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ["name"] = name, ["lang"] = "en", ["baseUrl"] = "https://" + name + ".example" }) }
    };

    private static List<JsonObject> Inventory() =>
    [
        Extension("alpha", installed: true),
        Extension("delta", installed: true, obsolete: true, listed: false),
        Extension("epsilon", installed: true),
        Extension("theta", installed: true, store: "https://gone.example/index.min.json"),
        Extension("gamma", installed: true),
        Extension("zeta", installed: false),
    ];

    private static List<string> Names(JsonNode? items) => (items as JsonArray ?? []).Select(item => item!["pkgName"]!.GetValue<string>()[Prefix.Length..]).ToList();

    private static JsonObject Item(JsonObject snapshot, string name) =>
        snapshot["items"]!.AsArray().OfType<JsonObject>().Single(item => item["pkgName"]!.GetValue<string>() == Prefix + name);

    private static T Await<T>(Task<T> task) => task.GetAwaiter().GetResult();
    private static void Await(Task task) => task.GetAwaiter().GetResult();

    private static JsonObject Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Check.True(response.StatusCode == expected, response.StatusCode + " " + text);
        return (JsonObject)JsonNode.Parse(text)!;
    }

    /// <summary>A stand-in source host answering the extension list.</summary>
    private sealed class FakeHost : IDisposable
    {
        public FakeHost(List<JsonObject> rows)
        {
            var handler = new Handler((request, _) =>
            {
                lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
                if (request.RequestUri!.AbsolutePath != "/v1/extensions") throw new InvalidOperationException(request.RequestUri.AbsolutePath);
                var body = new JsonObject { ["extensions"] = new JsonArray(rows.Select(row => (JsonNode?)row.DeepClone()).ToArray()) };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
            });
            Client = SuwayomiClient.ForSourceHost(new AutoSourcarrSettings { SourceEngineMode = "integrated" }, "http://127.0.0.1:4567", "token", handler: handler);
        }
        public SuwayomiClient Client { get; }
        /// <summary>Every source host path asked for, in order.</summary>
        public List<string> Paths { get; } = [];
        public SourceOrphans Orphans(string root, Func<byte[]?> seed) => new(root, Client, new HistoryFetcher(), seed);
        public void Dispose() => Client.Dispose();
    }

    /// <summary>Original history: today's catalog has gamma; the older sampled snapshot also had omega.</summary>
    private sealed class HistoryFetcher : IPublicSourceFetcher
    {
        public Task<PublicSourceResponse> FetchAsync(PublicSourceRequest request, CancellationToken cancellationToken = default)
        {
            var url = request.Url.OriginalString;
            string body;
            if (url.StartsWith(SourceOrphans.Api + "commits", StringComparison.Ordinal))
            {
                var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var sha = url.Contains("until=" + today, StringComparison.Ordinal) ? Head : Older;
                body = "[{\"sha\":\"" + sha + "\",\"commit\":{\"committer\":{\"date\":\"2026-10-01T00:00:00Z\"}}}]";
            }
            else if (url.EndsWith("/index.pb", StringComparison.Ordinal) || url.EndsWith("/release-assets.json", StringComparison.Ordinal)) throw new PublicSourceFetchException(404);
            else if (url == SourceOrphans.Raw + Head + "/index.min.json") body = "[" + Row("gamma") + "]";
            else if (url == SourceOrphans.Raw + Older + "/index.min.json") body = "[" + Row("gamma") + "," + Row("omega") + "]";
            else throw new PublicSourceFetchException(404);
            return Task.FromResult(new PublicSourceResponse(Encoding.UTF8.GetBytes(body), request.Url));
        }

        private static string Row(string name) => new JsonObject
        {
            ["name"] = name, ["pkg"] = Prefix + name, ["version"] = "1.4.2", ["lang"] = "en", ["nsfw"] = 0,
            ["apk"] = "tachiyomi-en." + name + "-v1.4.2.apk",
            ["sources"] = new JsonArray(new JsonObject { ["id"] = "-5", ["name"] = name, ["lang"] = "en", ["baseUrl"] = "https://" + name + ".example" })
        }.ToJsonString();
    }

    /// <summary>Today's head commit for the current-catalog check, and one archived APK; everything else as <see cref="HistoryFetcher"/>.</summary>
    private sealed class ArchiveFetcher(string url, byte[] archive) : IPublicSourceFetcher
    {
        private readonly HistoryFetcher _history = new();
        public Task<PublicSourceResponse> FetchAsync(PublicSourceRequest request, CancellationToken cancellationToken = default)
        {
            var requested = request.Url.OriginalString;
            if (requested.StartsWith(SourceOrphans.Api + "commits", StringComparison.Ordinal))
                return Task.FromResult(new PublicSourceResponse(Encoding.UTF8.GetBytes("[{\"sha\":\"" + Head + "\"}]"), request.Url));
            if (requested == url) return Task.FromResult(new PublicSourceResponse(archive, request.Url));
            return _history.FetchAsync(request, cancellationToken);
        }
    }

    private static byte[] Zip(params (string Name, byte[] Contents)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, contents) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open(); stream.Write(contents);
            }
        return output.ToArray();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
