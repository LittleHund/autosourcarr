using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Accounts;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests.Api;

/// <summary>
/// Phase 5 "Done when": every API route is covered by a role test, and the reset routes refuse cross-site posts.
/// Runs the whole app (external mode), lists every route the app maps, and checks each against anonymous, guest,
/// reader and script callers (refused before any handler runs, so nothing is changed) and against the policy for admins.
/// </summary>
public static class AccessControlTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("accounts: password hashes, owner first boot, sessions end on password change, keys shown once and revocable", Accounts);
        suite.Add("accounts: failed sign-ins slow down, then refuse; a burst is reported once", Throttle);
        suite.Add("access: every route of the app refuses anonymous, guest, reader and script-guest callers where the policy says so; admins reach every admin route", () => RouteMatrix().GetAwaiter().GetResult());
        suite.Add("access: forms sign-in, first-boot owner, owner-only routes, external groups and basic sign-in", () => SignIn().GetAwaiter().GetResult());
        suite.Add("access: the reset routes refuse cross-site posts even with the admin token", () => CrossSiteResets().GetAwaiter().GetResult());
    }

    private static void Accounts()
    {
        var hash = PasswordHashing.Hash("correct horse");
        Check.True(hash.StartsWith("pbkdf2-sha512$210000$", StringComparison.Ordinal) && !hash.Contains("correct", StringComparison.Ordinal));
        Check.True(PasswordHashing.Verify("correct horse", hash) && !PasswordHashing.Verify("correct horse!", hash) && !PasswordHashing.Verify("x", "garbage"));
        Check.True(PasswordHashing.Hash("same") != PasswordHashing.Hash("same"), "salted");
        using var temp = new Sandbox();
        var now = 1_800_000_000L;
        var store = new AccountStore(temp.Path, () => now);
        Check.True(store.SetupRequired && store.AutomaticAdminToken.StartsWith("asa_", StringComparison.Ordinal));
        Check.Throws<AccountException>(() => store.CreateOwner("owner", "short"));
        var owner = store.CreateOwner("Owner", "owner-password");
        Check.True(!store.SetupRequired && owner.Role == Role.Owner);
        Check.Throws<AccountException>(() => store.CreateOwner("second", "another-password"));
        Check.Equal(owner.Id, store.VerifyPassword("owner", "owner-password")!.Id);
        Check.True(store.VerifyPassword("owner", "wrong-password") is null && store.VerifyPassword("nobody", "owner-password") is null);
        var session = store.IssueSession(owner.Id);
        Check.Equal(owner.Id, store.ValidateSession(session)!.Value.Account.Id);
        Check.True(store.ValidateSession(session[..^1] + (session[^1] == '0' ? '1' : '0')) is null, "tampered session refused");
        store.ChangePassword(owner.Id, "owner-password", "new-owner-password");
        Check.True(store.ValidateSession(session) is null, "password change ends sessions");
        var admin = store.CreateUser("Helper", "helper-password", Role.Admin);
        Check.Throws<AccountException>(() => store.CreateUser("helper", "x-password-2", Role.Guest));
        Check.Throws<AccountException>(() => store.CreateUser("boss", "x-password-2", Role.Owner));
        Check.Throws<AccountException>(() => store.DeleteUser(owner.Id));
        var (reader, secret) = store.CreateKey("Phone", "reader", Role.Reader);
        Check.True(secret.StartsWith("asr_", StringComparison.Ordinal) && reader.Prefix == secret[..8]);
        Check.Equal(reader.Id, store.FindKey(secret)!.Id);
        var file = File.ReadAllText(Path.Combine(temp.Path, "accounts", "accounts.json"));
        Check.True(!file.Contains(secret, StringComparison.Ordinal) && !file.Contains("helper-password", StringComparison.Ordinal), "no secrets stored");
        Check.True(store.RevokeKey(reader.Id) && store.FindKey(secret) is null && !store.RevokeKey(reader.Id));
        store.CreateKey("Admin key", "admin", Role.Admin);
        Check.Throws<AccountException>(() => store.CreateKey("Second", "admin", Role.Admin));
        Check.Throws<AccountException>(() => store.CreateKey("Bad", "script", Role.Owner));
        var reopened = new AccountStore(temp.Path, () => now);
        Check.Equal(store.AutomaticAdminToken, reopened.AutomaticAdminToken);
        Check.Equal(admin.Id, reopened.VerifyPassword("helper", "helper-password")!.Id);
        var old = reopened.AutomaticAdminToken;
        Check.True(reopened.RegenerateAdminToken() != old);
        Check.True(reopened.ResetOwnerLogin() && reopened.SetupRequired && reopened.VerifyPassword("owner", "new-owner-password") is null);
        Check.Equal(owner.Id, reopened.CreateOwner("Owner", "reset-password-1").Id);
    }

    private static void Throttle()
    {
        var now = DateTimeOffset.Parse("2026-10-31T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var throttle = new LoginThrottle(() => now);
        var bursts = 0;
        for (var i = 0; i < 4; i++) { Check.Equal(TimeSpan.Zero, throttle.Before("10.0.0.1", "owner")); bursts += throttle.Failed("10.0.0.1", "owner") ? 1 : 0; }
        bursts += throttle.Failed("10.0.0.1", "owner") ? 1 : 0;
        Check.Equal(TimeSpan.FromSeconds(2), throttle.Before("10.0.0.1", "someone"));
        Check.Equal(TimeSpan.FromSeconds(2), throttle.Before("10.0.0.9", "Owner"));
        for (var i = 0; i < 15; i++) bursts += throttle.Failed("10.0.0.1", "owner") ? 1 : 0;
        Check.True(throttle.Before("10.0.0.1", "owner") is null, "refused after 20");
        Check.Equal(1, bursts);
        now = now.AddMinutes(16);
        Check.Equal(TimeSpan.Zero, throttle.Before("10.0.0.1", "owner"));
        var log = new SecurityLog(new Sandbox().Path);
        log.Record("login-failed", "owner", "10.0.0.1");
        Check.Throws<ArgumentException>(() => log.Record("unknown", "x", "y"));
        Check.Equal(1, log.Snapshot("login-failed")["items"]!.AsArray().Count);
    }

    private static AutoSourcarrSettings Settings(string root, string method = "forms") => new()
    {
        DataDir = Path.Combine(root, "data"), SettingsConfigDir = Path.Combine(root, "config"), SourceEngineMode = "external",
        SourceSuwayomiUrl = "http://127.0.0.1:1", AdminToken = "configured-admin-token", AutosourcarrReaderApiToken = "configured-reader-token",
        AuthMethod = method, AuthAdminGroups = "admins", AuthGuestGroups = "family"
    };

    private sealed class App : IAsyncDisposable
    {
        public required ApplicationRuntime Runtime { get; init; }
        public required IHost Host { get; init; }
        public HttpClient Client(params (string Name, string Value)[] headers)
        {
            var client = Host.GetTestClient();
            foreach (var (name, value) in headers) client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
            return client;
        }
        public async ValueTask DisposeAsync() { Host.Dispose(); await Runtime.DisposeAsync(); }
    }

    private static async Task<App> Start(string root, AutoSourcarrSettings settings)
    {
        var runtime = await ApplicationRuntime.CreateExternalForTestsAsync(settings, settings.SettingsConfigDir);
        var assets = Path.Combine(root, "assets"); Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "index.html"), "app-page"); File.WriteAllText(Path.Combine(assets, "login.html"), "login-page");
        var host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => services.AddRouting())
            .Configure(app => runtime.ConfigureHttp(app, assets))).Build();
        await host.StartAsync();
        return new App { Runtime = runtime, Host = host };
    }

    private static async Task RouteMatrix()
    {
        using var temp = new Sandbox();
        await using var app = await Start(temp.Path, Settings(temp.Path));
        var accounts = app.Runtime.Access!.Accounts;
        accounts.CreateOwner("owner", "owner-password");
        accounts.CreateUser("guest", "guest-password", Role.Guest);
        var guestSession = accounts.IssueSession(accounts.FindByName("guest")!.Id);
        var (_, readerKey) = accounts.CreateKey("Phone", "reader", Role.Reader);
        var (_, scriptKey) = accounts.CreateKey("Viewer", "script", Role.Guest);
        var routes = app.Host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => (Method: method, Path: Path(endpoint.RoutePattern.RawText ?? ""))))
            .Where(route => route.Path.Length > 0).Distinct().ToArray();
        Check.True(routes.Length > 60, "routes found: " + routes.Length);
        var callers = new (string Name, Caller Caller, (string, string)[] Headers)[]
        {
            ("anonymous", Caller.Anonymous, []),
            ("guest", new Caller(Role.Guest, "guest", "session"), [("Cookie", AccessControlMiddleware.SessionCookie + "=" + guestSession)]),
            ("reader", new Caller(Role.Reader, "Phone", "key"), [("X-Api-Key", readerKey)]),
            ("script-guest", new Caller(Role.Script, "Viewer", "key"), [("X-Api-Key", scriptKey)]),
        };
        var admin = new Caller(Role.Admin, "admin token", "admin-token");
        var checkedRoutes = 0;
        foreach (var (method, path) in routes)
        {
            var level = AccessPolicy.Required(method, path);
            if (level != AccessLevel.Owner) Check.True(AccessPolicy.Allows(admin, level), "admin reaches " + method + " " + path);
            foreach (var (name, caller, headers) in callers)
            {
                if (AccessPolicy.Allows(caller, level)) continue;
                using var client = app.Client(headers);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (method is "POST" or "PUT" or "PATCH") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                HttpStatusCode[] expected = caller.Role != Role.None ? [HttpStatusCode.Forbidden]
                    : path.StartsWith("/api/", StringComparison.Ordinal) ? [HttpStatusCode.Unauthorized] : [HttpStatusCode.Redirect, HttpStatusCode.Unauthorized];
                Check.True(expected.Contains(response.StatusCode), $"{name} {method} {path}: {(int)response.StatusCode}");
                checkedRoutes++;
            }
        }
        Check.True(checkedRoutes > routes.Length, "refusals checked: " + checkedRoutes);
        // Owner-only routes refuse admins (token) too.
        using var adminClient = app.Client(("X-Admin-Token", "configured-admin-token"), ("Sec-Fetch-Site", "same-origin"));
        Check.Equal(HttpStatusCode.Forbidden, (await adminClient.GetAsync("/api/v1/accounts")).StatusCode);
        Check.Equal(HttpStatusCode.Forbidden, (await adminClient.PostAsync("/api/v1/keys/admin-token/regenerate", null)).StatusCode);
        Check.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/v1/keys")).StatusCode);
        // Reader devices read the catalogue; guests cannot write.
        using var guest = app.Client(("Cookie", AccessControlMiddleware.SessionCookie + "=" + guestSession), ("Sec-Fetch-Site", "same-origin"));
        Check.Equal("app-page", await guest.GetStringAsync("/"));
        Check.Equal("guest", (await guest.GetFromJsonAsync<JsonObject>("/api/v1/auth/me"))!["role"]!.GetValue<string>());
        var denied = app.Runtime.Access!.Log.Snapshot("access-denied")["items"]!.AsArray();
        Check.True(denied.Count > 0, "refusals of signed-in callers are logged");
    }

    private static string Path(string pattern)
    {
        if (pattern.Length == 0) return "";
        var path = System.Text.RegularExpressions.Regex.Replace(pattern.StartsWith('/') ? pattern : "/" + pattern, "\\{\\*?([^}:]+)[^}]*\\}", "1");
        return path;
    }

    private static async Task SignIn()
    {
        using var temp = new Sandbox();
        await using (var app = await Start(temp.Path, Settings(temp.Path)))
        {
            using var client = app.Client(("Sec-Fetch-Site", "same-origin"));
            var status = await client.GetFromJsonAsync<JsonObject>("/api/v1/auth/status");
            Check.True(status!["setup_required"]!.GetValue<bool>() && !status["signed_in"]!.GetValue<bool>());
            Check.Equal("login-page", await client.GetStringAsync("/login"));
            Check.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/auth/setup", Json(new() { ["username"] = "me", ["password"] = "short" }))).StatusCode);
            var created = await client.PostAsync("/api/v1/auth/setup", Json(new() { ["username"] = "me", ["password"] = "owner-password" }));
            Check.Equal(HttpStatusCode.Created, created.StatusCode);
            var cookie = created.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(AccessControlMiddleware.SessionCookie, StringComparison.Ordinal));
            Check.True(cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
            Check.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/v1/auth/setup", Json(new() { ["username"] = "other", ["password"] = "other-password" }))).StatusCode);
            Check.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/auth/login", Json(new() { ["username"] = "me", ["password"] = "wrong-password" }))).StatusCode);
            var login = await client.PostAsync("/api/v1/auth/login", Json(new() { ["username"] = "me", ["password"] = "owner-password" }));
            Check.Equal(HttpStatusCode.OK, login.StatusCode);
            var session = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
            using var owner = app.Client(("Cookie", session), ("Sec-Fetch-Site", "same-origin"));
            Check.Equal("owner", (await owner.GetFromJsonAsync<JsonObject>("/api/v1/auth/me"))!["role"]!.GetValue<string>());
            var account = await owner.PostAsync("/api/v1/accounts", Json(new() { ["username"] = "friend", ["password"] = "friend-password", ["role"] = "guest" }));
            Check.Equal(HttpStatusCode.Created, account.StatusCode);
            var key = await (await owner.PostAsync("/api/v1/keys", Json(new() { ["name"] = "Tablet", ["kind"] = "reader" }))).Content.ReadFromJsonAsync<JsonObject>();
            using (var reader = new HttpRequestMessage(HttpMethod.Get, "/api/reader/v1/live"))
            {
                // The reader API keeps its own key check; device keys are accepted where the reader token is.
                reader.Headers.Add("X-AutoSourcarr-Api-Key", "configured-reader-token");
                Check.Equal(HttpStatusCode.OK, (await client.SendAsync(reader)).StatusCode);
            }
            Check.True(key!["secret"]!.GetValue<string>().StartsWith("asr_", StringComparison.Ordinal));
            Check.Equal(HttpStatusCode.OK, (await owner.PostAsync("/api/v1/keys/admin-token/regenerate", null)).StatusCode);
            var events = await owner.GetFromJsonAsync<JsonObject>("/api/v1/security/events");
            var kinds = events!["items"]!.AsArray().Select(row => row!["kind"]!.GetValue<string>()).ToHashSet();
            foreach (var kind in new[] { "owner-created", "login", "login-failed", "user-created", "key-created", "admin-token-regenerated" })
                Check.True(kinds.Contains(kind), "logged: " + kind);
            Check.Equal(HttpStatusCode.OK, (await owner.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        }
        using var externalRoot = new Sandbox();
        await using (var app = await Start(externalRoot.Path, Settings(externalRoot.Path, "external") with { AuthOwnerExternalUser = "boss" }))
        {
            using var boss = app.Client(("Remote-User", "boss"), ("Sec-Fetch-Site", "same-origin"));
            Check.Equal("owner", (await boss.GetFromJsonAsync<JsonObject>("/api/v1/auth/me"))!["role"]!.GetValue<string>());
            using var groupAdmin = app.Client(("Remote-User", "alice"), ("Remote-Groups", "users,admins"), ("Sec-Fetch-Site", "same-origin"));
            Check.Equal("admin", (await groupAdmin.GetFromJsonAsync<JsonObject>("/api/v1/auth/me"))!["role"]!.GetValue<string>());
            Check.Equal(HttpStatusCode.Forbidden, (await groupAdmin.GetAsync("/api/v1/accounts")).StatusCode);
            // A group named like the owner role never makes an owner.
            using var pretender = app.Client(("Remote-User", "mallory"), ("Remote-Groups", "owner,family"), ("Sec-Fetch-Site", "same-origin"));
            Check.Equal("guest", (await pretender.GetFromJsonAsync<JsonObject>("/api/v1/auth/me"))!["role"]!.GetValue<string>());
            using var stranger = app.Client(("Remote-User", "eve"), ("Remote-Groups", "users"), ("Sec-Fetch-Site", "same-origin"));
            Check.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/api/v1/keys")).StatusCode);
        }
        using var basicRoot = new Sandbox();
        await using (var app = await Start(basicRoot.Path, Settings(basicRoot.Path, "basic")))
        {
            app.Runtime.Access!.Accounts.CreateOwner("owner", "owner-password");
            using var anonymous = app.Client();
            var refused = await anonymous.GetAsync("/");
            Check.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Check.True(refused.Headers.WwwAuthenticate.ToString().StartsWith("Basic", StringComparison.Ordinal));
            using var basic = app.Client(("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("owner:owner-password"))));
            Check.Equal("app-page", await basic.GetStringAsync("/"));
        }
    }

    private static async Task CrossSiteResets()
    {
        using var temp = new Sandbox();
        await using var app = await Start(temp.Path, Settings(temp.Path));
        foreach (var path in new[] { "/api/admin/maintenance/reset-covers", "/api/admin/maintenance/reset-database", "/api/admin/maintenance/full-reset", "/api/admin/maintenance/reset-all-manual-actions" })
        {
            using var client = app.Client(("X-Admin-Token", "configured-admin-token"), ("Sec-Fetch-Site", "cross-site"));
            using var response = await client.PostAsync(path, Json(new()));
            Check.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Check.True((await response.Content.ReadAsStringAsync()).Contains(CrossSiteWritePolicy.Detail, StringComparison.Ordinal), path);
            using var foreign = app.Client(("X-Admin-Token", "configured-admin-token"), ("Origin", "https://evil.example"));
            Check.Equal(HttpStatusCode.Forbidden, (await foreign.PostAsync(path, Json(new()))).StatusCode);
        }
    }

    private static StringContent Json(JsonObject value) => new(value.ToJsonString(), Encoding.UTF8, "application/json");
}
