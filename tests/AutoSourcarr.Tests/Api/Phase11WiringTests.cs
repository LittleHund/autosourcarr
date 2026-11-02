using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Api;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Accounts;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests;

/// <summary>
/// Phase 11 integration: the whole app (external mode, forms sign-in) maps the Web UI finish routes under /api/v1 with
/// their access level; UI preferences are kept per account (guests too, reader keys refused); the internal services
/// read answers the 0.5.0 shape; the diagnostics download removes planted secrets, tokens, cookies, keys, passwords,
/// e-mail addresses and absolute data paths; the old /api/admin/instance backup routes answer 404 (phase 12 serves them on /api/v1).
/// </summary>
public static class Phase11WiringTests
{
    internal static readonly (string Method, string Path, AccessLevel Level)[] Routes =
    [
        ("GET", "/api/v1/ui-preferences", AccessLevel.Guest), ("PUT", "/api/v1/ui-preferences", AccessLevel.Guest),
        ("GET", "/api/v1/internal-services", AccessLevel.Admin), ("GET", "/api/v1/diagnostics", AccessLevel.Admin),
    ];

    public static void Register(TestSuite suite)
    {
        suite.Add("phase 11 wiring: UI preference, internal services and diagnostics routes are mapped with their access level; the old /api/admin/instance backup routes answer 404 (moved to /api/v1 in phase 12)",
            () => Run(Wiring).GetAwaiter().GetResult());
        suite.Add("phase 11 ui preferences: one profile per account, guests keep their own, the owner starts from the 0.4.x profile, reader keys are refused",
            () => Run(Preferences).GetAwaiter().GetResult());
        suite.Add("phase 11 internal services: the Status panel lists the app's own services from cached state and offers no start actions",
            () => Run(InternalServices).GetAwaiter().GetResult());
        suite.Add("phase 11 matrix: discovery_outcomes is an array of the stored outcome rows in the 0.4.5 shape (audit A2)",
            () => Run(MatrixOutcomes).GetAwaiter().GetResult());
        suite.Add("phase 11 diagnostics (PERF-9): one download with logs and errors; planted secrets, tokens, cookies, keys, passwords, e-mails and data paths never leak",
            () => Run(Diagnostics).GetAwaiter().GetResult());
    }

    internal const string AdminToken = "phase11-admin-token-7f3a9c2e5b";
    internal const string ReaderToken = "phase11-reader-token-41d8e6b0aa";
    internal const string SuwayomiPassword = "phase11-suwayomi-pass-9c1e";

    internal sealed class App(ApplicationRuntime runtime, IHost host, Sandbox temp, AutoSourcarrSettings settings)
    {
        public ApplicationRuntime Runtime { get; } = runtime;
        public IHost Host { get; } = host;
        public Sandbox Temp { get; } = temp;
        public AutoSourcarrSettings Settings { get; } = settings;
        public HttpClient Client(string? token = null, string? session = null, string? readerKey = null)
        {
            var client = Host.GetTestClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            if (token is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("X-Admin-Token", token);
            if (session is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", AccessControlMiddleware.SessionCookie + "=" + session);
            if (readerKey is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("X-AutoSourcarr-Api-Key", readerKey);
            return client;
        }
    }

    internal static async Task Run(Func<App, Task> body)
    {
        using var temp = new Sandbox();
        var settings = new AutoSourcarrSettings
        {
            DataDir = Path.Combine(temp.Path, "data"), SettingsConfigDir = Path.Combine(temp.Path, "config"), SourceEngineMode = "external",
            SourceSuwayomiUrl = "http://127.0.0.1:1", AdminToken = AdminToken, AutosourcarrReaderApiToken = ReaderToken,
            AuthMethod = "forms", LibraryDir = Path.Combine(temp.Path, "library"), MainSuwayomiPassword = SuwayomiPassword,
        };
        var runtime = await ApplicationRuntime.CreateExternalForTestsAsync(settings, settings.SettingsConfigDir);
        IHost? host = null;
        try
        {
            var assets = Path.Combine(temp.Path, "assets"); Directory.CreateDirectory(assets);
            File.WriteAllText(Path.Combine(assets, "index.html"), "app-page"); File.WriteAllText(Path.Combine(assets, "login.html"), "login-page");
            host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => services.AddRouting())
                .Configure(app => runtime.ConfigureHttp(app, assets))).Build();
            await host.StartAsync();
            await body(new App(runtime, host, temp, settings));
        }
        finally
        {
            host?.Dispose();
            await runtime.DisposeAsync();
        }
    }

    private static string Shape(string pattern) => Regex.Replace(pattern.StartsWith('/') ? pattern : "/" + pattern, "\\{[^}]+\\}", "{}").TrimEnd('/');
    private static string Concrete(string pattern) => Regex.Replace(pattern, "\\{[^}]+\\}", "1");

    private static async Task Wiring(App app)
    {
        app.Runtime.Access!.Accounts.CreateOwner("owner", "owner-password");
        var mapped = app.Host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => method + " " + Shape(endpoint.RoutePattern.RawText ?? "")))
            .ToHashSet(StringComparer.Ordinal);
        using var anonymous = app.Client();
        foreach (var (method, pattern, level) in Routes)
        {
            Check.True(mapped.Contains(method + " " + Shape(pattern)), "mapped: " + method + " " + pattern);
            var path = Concrete(pattern);
            Check.True(AccessPolicy.Required(method, path) == level, level + ": " + method + " " + path);
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method is "POST" or "PUT") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await anonymous.SendAsync(request);
            Check.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        // Admin-only routes stay admin-only for guests.
        var guest = app.Runtime.Access.Accounts.CreateUser("guest", "guest-password", Role.Guest);
        using var guestClient = app.Client(session: app.Runtime.Access.Accounts.IssueSession(guest.Id));
        foreach (var (method, pattern, level) in Routes.Where(route => route.Level == AccessLevel.Admin))
        {
            using var response = await guestClient.SendAsync(new HttpRequestMessage(new HttpMethod(method), Concrete(pattern)));
            Check.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        // The reader alias never reaches these routes.
        using var reader = app.Client(readerKey: ReaderToken);
        using (var aliased = await reader.GetAsync("/api/reader/v1/ui-preferences")) Check.True(aliased.StatusCode != HttpStatusCode.OK, "no UI preferences through the reader alias");
        using (var aliased = await reader.GetAsync("/api/reader/v1/internal-services")) Check.True(aliased.StatusCode != HttpStatusCode.OK, "no internal services through the reader alias");
        // Task 3: the 0.4.5 instance backup routes are not served; phase 12 moved them to /api/v1/backups (BackupTests).
        using var admin = app.Client(AdminToken);
        foreach (var (method, path) in new[] { ("GET", "/api/admin/instance/backups"), ("POST", "/api/admin/instance/backups"), ("POST", "/api/admin/instance/restore/preview") })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "POST") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await admin.SendAsync(request);
            Check.True(response.StatusCode == HttpStatusCode.NotFound, method + " " + path + ": " + (int)response.StatusCode);
        }
    }

    private static async Task<JsonObject> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Check.True(response.IsSuccessStatusCode, (int)response.StatusCode + " " + text);
        return JsonNode.Parse(text)!.AsObject();
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task Preferences(App app)
    {
        var accounts = app.Runtime.Access!.Accounts;
        // The single 0.4.x profile (no proxy identity) is "local:default".
        app.Runtime.Preferences.SetUserPreferences("local:default", new JsonObject { ["theme"] = "amber", ["browsePageSize"] = 60 });
        var owner = accounts.CreateOwner("owner", "owner-password");
        var guest = accounts.CreateUser("guest", "guest-password", Role.Guest);
        var other = accounts.CreateUser("other-guest", "guest-password", Role.Guest);
        using var ownerClient = app.Client(session: accounts.IssueSession(owner.Id));
        using var guestClient = app.Client(session: accounts.IssueSession(guest.Id));
        using var otherClient = app.Client(session: accounts.IssueSession(other.Id));

        var ownerView = await Json(await ownerClient.GetAsync("/api/v1/ui-preferences"));
        Check.Equal("owner", ownerView["user"]!.GetValue<string>());
        Check.Equal("amber", ownerView["values"]!["theme"]!.GetValue<string>());
        // The owner's first save keeps the inherited values it does not change.
        var saved = await Json(await ownerClient.PutAsync("/api/v1/ui-preferences", Body("{\"values\":{\"theme\":\"violet\"}}")));
        Check.Equal("violet", saved["values"]!["theme"]!.GetValue<string>());
        Check.Equal(60, saved["values"]!["browsePageSize"]!.GetValue<int>());
        Check.Equal("amber", app.Runtime.Preferences.UserPreferences("local:default")["theme"]!.GetValue<string>());

        // Guests have their own profile and start empty (never the owner's).
        var guestView = await Json(await guestClient.GetAsync("/api/v1/ui-preferences"));
        Check.Equal("guest", guestView["user"]!.GetValue<string>());
        Check.Equal(0, guestView["values"]!.AsObject().Count);
        await Json(await guestClient.PutAsync("/api/v1/ui-preferences", Body("{\"values\":{\"theme\":\"rose\"}}")));
        Check.Equal("rose", (await Json(await guestClient.GetAsync("/api/v1/ui-preferences")))["values"]!["theme"]!.GetValue<string>());
        Check.Equal(0, (await Json(await otherClient.GetAsync("/api/v1/ui-preferences")))["values"]!.AsObject().Count);
        Check.Equal("violet", (await Json(await ownerClient.GetAsync("/api/v1/ui-preferences")))["values"]!["theme"]!.GetValue<string>());

        // Body rules as in 0.4.x: values must be an object; a profile is at most 64 KiB.
        using (var bad = await guestClient.PutAsync("/api/v1/ui-preferences", Body("{\"values\":[1]}"))) Check.Equal((HttpStatusCode)422, bad.StatusCode);
        using (var large = await guestClient.PutAsync("/api/v1/ui-preferences", Body("{\"values\":{\"x\":\"" + new string('a', 70000) + "\"}}")))
            Check.Equal(HttpStatusCode.BadRequest, large.StatusCode);

        // The admin token (no account) keeps the 0.4.x rule: the shared local profile.
        using var admin = app.Client(AdminToken);
        Check.Equal("amber", (await Json(await admin.GetAsync("/api/v1/ui-preferences")))["values"]!["theme"]!.GetValue<string>());

        // Reader devices and scripts have no UI.
        var (_, readerSecret) = accounts.CreateKey("phone", "reader", Role.Reader);
        using var reader = app.Client();
        reader.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", readerSecret);
        using (var refused = await reader.GetAsync("/api/v1/ui-preferences")) Check.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using (var refused = await reader.PutAsync("/api/v1/ui-preferences", Body("{\"values\":{\"theme\":\"x\"}}"))) Check.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    private static async Task InternalServices(App app)
    {
        using var admin = app.Client(AdminToken);
        var view = await Json(await admin.GetAsync("/api/v1/internal-services"));
        Check.Equal("external", view["mode"]!.GetValue<string>());
        Check.Equal(0, view["actions"]!.AsArray().Count);
        var services = view["services"]!.AsObject();
        foreach (var name in new[] { "source_host", "webview", "solver", "repair", "egress_proxy", "translation", "metadata", "suwayomi", "builder" })
        {
            Check.True(services[name] is JsonObject, "service " + name);
            var service = services[name]!.AsObject();
            Check.True(service["label"]!.GetValue<string>().Length > 0 && service["message"]!.GetValue<string>().Length > 0, name + " has a label and message");
            Check.True(new[] { "running", "ready", "idle", "off", "degraded", "unavailable", "starting" }.Contains(service["state"]!.GetValue<string>()), name + " state");
        }
        // External mode: no built-in source host, WebView or repair runner here.
        Check.Equal("unavailable", services["webview"]!["state"]!.GetValue<string>());
        Check.Equal("unavailable", services["repair"]!["state"]!.GetValue<string>());
        Check.True(view["reader_build"] is JsonObject, "reader build check included");
        Check.True(!view.ToJsonString().Contains(app.Temp.Path, StringComparison.OrdinalIgnoreCase), "no absolute data path");
    }

    private static async Task MatrixOutcomes(App app)
    {
        using var admin = app.Client(AdminToken);
        var empty = await Json(await admin.GetAsync("/api/v1/matrix?include_chapter_stats=false"));
        Check.True(empty["discovery_outcomes"] is JsonArray { Count: 0 }, "an empty catalogue answers []: " + empty["discovery_outcomes"]?.ToJsonString());
        app.Runtime.Database.Write(Core.Persistence.DatabaseFile.Catalog, c =>
        {
            c.Execute("INSERT INTO entries(id,preferred_title,status,created_at,updated_at) VALUES(7,'Outcome work',0,'2026-01-01','2026-01-01')");
            c.Execute("INSERT INTO entries(id,preferred_title,status,created_at,updated_at) VALUES(8,'Other work',0,'2026-01-01','2026-01-01')");
            c.Execute("INSERT INTO source_discovery_outcomes(entry_id,source_id,state,message,candidate_entry_id,checked_at) VALUES(7,'100','owned','belongs elsewhere',8,'2026-01-02T00:00:00Z')");
            c.Execute("INSERT INTO source_discovery_outcomes(entry_id,source_id,state,message,candidate_entry_id,checked_at) VALUES(7,'200','no_results','none',NULL,'2026-01-02T00:00:00Z')");
            return 0;
        });
        var matrix = await Json(await admin.GetAsync("/api/v1/matrix?include_chapter_stats=false"));
        var outcomes = matrix["discovery_outcomes"] as JsonArray ?? [];
        Check.True(outcomes.Count == 2, "two outcome rows: " + matrix["discovery_outcomes"]?.ToJsonString());
        foreach (var row in outcomes.OfType<JsonObject>())
            foreach (var field in new[] { "entry_id", "source_id", "state", "message", "candidate_entry_id", "checked_at" })
                Check.True(row.ContainsKey(field), "outcome field " + field);
        var owned = outcomes.OfType<JsonObject>().Single(row => row["source_id"]!.GetValue<string>() == "100");
        Check.Equal(7L, owned["entry_id"]!.GetValue<long>());
        Check.Equal("owned", owned["state"]!.GetValue<string>());
        Check.Equal(8L, owned["candidate_entry_id"]!.GetValue<long>());
        Check.True(outcomes.OfType<JsonObject>().Single(row => row["source_id"]!.GetValue<string>() == "200")["candidate_entry_id"] is null, "no candidate is null");
    }

    private static async Task Diagnostics(App app)
    {
        var data = app.Settings.DataDir;
        // Planted values: none of these may appear in the download.
        var planted = new[]
        {
            AdminToken, ReaderToken, SuwayomiPassword, "hunter2-plain-password", "cf-clearance-cookie-6d1f", "session-cookie-value-88ab",
            "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJwaGFzZTExIn0.c2lnbmF0dXJlLXBoYXNlMTE", "private-api-key-31c7", "MIIEvQIBADANprivatekeybody",
            "someone.private@example.com", data, Path.Combine(data, "catalog.sqlite"), "/srv/nas-share/library/Private Title", "bearer-token-55e0",
        };
        var logs = app.Runtime.Logs;
        logs.Record("Planted", "Login with admin token " + AdminToken + " and reader token " + ReaderToken);
        logs.Record("Planted", "Suwayomi password=" + SuwayomiPassword + " password: hunter2-plain-password");
        logs.Record("Planted", "Cookie: cf_clearance=cf-clearance-cookie-6d1f; session=session-cookie-value-88ab");
        logs.Record("Planted", "cf_clearance=cf-clearance-cookie-6d1f returned by the solver");
        logs.Record("Planted", "Authorization: Bearer bearer-token-55e0");
        logs.Record("Planted", "token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJwaGFzZTExIn0.c2lnbmF0dXJlLXBoYXNlMTE expired");
        logs.Record("Planted", "X-Api-Key: private-api-key-31c7 api_key=private-api-key-31c7");
        logs.Record("Planted", "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANprivatekeybody\n-----END PRIVATE KEY-----");
        logs.Record("Planted", "Report sent by someone.private@example.com");
        logs.Record("Planted", "Could not open " + Path.Combine(data, "catalog.sqlite") + " in " + data);
        logs.Record("Planted", "Library scan failed in /srv/nas-share/library/Private Title");
        app.Runtime.Errors.Record("Planted", "Fetch https://user:hunter2-plain-password@source.example/manga?token=bearer-token-55e0 failed for someone.private@example.com in " + data,
            "planted-error");
        await logs.FlushAsync();

        using var admin = app.Client(AdminToken);
        using var response = await admin.GetAsync("/api/v1/diagnostics");
        var text = await response.Content.ReadAsStringAsync();
        Check.Equal(HttpStatusCode.OK, response.StatusCode);
        Check.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = JsonNode.Parse(text)!.AsObject();
        // One download: logs, errors, tasks and the runtime context.
        Check.True(body["logs"]!["entries"]!.AsArray().Count >= 11, "logs included");
        Check.True(body["errors"]!["items"]!.AsArray().Count >= 1, "errors included");
        Check.True(body["runtime"] is JsonObject && body["tasks"] is JsonArray && body["internal_services"] is JsonObject, "runtime, tasks and services included");
        var decoded = Regex.Unescape(text);
        foreach (var value in planted)
            Check.True(!text.Contains(value, StringComparison.OrdinalIgnoreCase) && !decoded.Contains(value, StringComparison.OrdinalIgnoreCase)
                && !decoded.Contains(value.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase), "diagnostics leak a planted value: " + value);
        Check.True(!decoded.Contains(app.Temp.Path, StringComparison.OrdinalIgnoreCase) && !decoded.Contains(app.Temp.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase),
            "no absolute path of the instance folders");
        Check.True(decoded.Contains("[data]", StringComparison.Ordinal), "the data folder is named, not shown");
    }
}
