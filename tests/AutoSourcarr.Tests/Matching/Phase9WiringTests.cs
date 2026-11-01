using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AutoSourcarr.Api;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests.Matching;

/// <summary>
/// Phase 9 integration: the whole app (external mode, empty catalogue) maps every imports, sharing, metadata and discovery
/// route under /api/v1 with the right access level, and the list reads answer on an empty catalogue.
/// </summary>
public static class Phase9WiringTests
{
    private static readonly (string Method, string Path, AccessLevel Level)[] Routes =
    [
        // imports (9A)
        ("POST", "/api/v1/imports/mihon-backup", AccessLevel.Admin), ("GET", "/api/v1/imports/{importId}/receipt", AccessLevel.Admin),
        ("GET", "/api/v1/imports", AccessLevel.Admin), ("POST", "/api/v1/imports/clear-history", AccessLevel.Admin),
        ("POST", "/api/v1/imports/reset-rejections", AccessLevel.Admin), ("POST", "/api/v1/imports/reset-manual-autoresolve", AccessLevel.Admin),
        ("POST", "/api/v1/imports/recheck-after-reset", AccessLevel.Admin), ("POST", "/api/v1/imports/suwayomi-plan", AccessLevel.Admin),
        ("POST", "/api/v1/imports/{importId}/items", AccessLevel.Admin), ("GET", "/api/v1/imports/{importId}/items/{itemId}", AccessLevel.Admin),
        ("POST", "/api/v1/imports/{importId}/items/{itemId}/suggestions", AccessLevel.Admin),
        ("POST", "/api/v1/imports/{importId}/items/{itemId}/suggestions/live", AccessLevel.Admin),
        ("POST", "/api/v1/imports/{importId}/items/{itemId}/retry", AccessLevel.Admin),
        ("POST", "/api/v1/imports/{importId}/items/{itemId}/match/{entryId}", AccessLevel.Admin),
        ("POST", "/api/v1/imports/{importId}/items/{itemId}/reject", AccessLevel.Admin), ("POST", "/api/v1/imports/{importId}/items/{itemId}/skip", AccessLevel.Admin),
        // catalogue sharing, portable identities and the compatibility list (9B)
        ("GET", "/api/v1/compatibility", AccessLevel.Admin), ("GET", "/api/v1/catalog-sharing/status", AccessLevel.Admin),
        ("POST", "/api/v1/catalog-sharing/export", AccessLevel.Admin), ("POST", "/api/v1/catalog-sharing/preview", AccessLevel.Admin),
        ("GET", "/api/v1/catalog-sharing/stages/{stageId}", AccessLevel.Admin), ("POST", "/api/v1/catalog-sharing/apply", AccessLevel.Admin),
        ("GET", "/api/v1/catalog-sharing/decisions", AccessLevel.Admin), ("GET", "/api/v1/catalog-sharing/records", AccessLevel.Admin),
        ("POST", "/api/v1/catalog-sharing/decisions/review", AccessLevel.Admin), ("POST", "/api/v1/catalog-sharing/records/review", AccessLevel.Admin),
        ("POST", "/api/v1/catalog-sharing/records/link", AccessLevel.Admin), ("POST", "/api/v1/portable-identities/resolve", AccessLevel.Admin),
        ("GET", "/api/v1/identity-review", AccessLevel.Admin), ("GET", "/api/v1/identity-review/{entryId}", AccessLevel.Admin),
        ("POST", "/api/v1/identity-review/{entryId}", AccessLevel.Admin),
        // per-field metadata (9C): reading is a catalogue read, overrides are admin writes
        ("GET", "/api/v1/entries/{entryId}/metadata", AccessLevel.Guest), ("PUT", "/api/v1/entries/{entryId}/metadata/{field}", AccessLevel.Admin),
        ("DELETE", "/api/v1/entries/{entryId}/metadata/{field}", AccessLevel.Admin),
        // catalogue-wide discovery jobs (9C)
        ("POST", "/api/v1/maintenance/refresh-feeds", AccessLevel.Admin), ("POST", "/api/v1/maintenance/refresh-metadata", AccessLevel.Admin),
        ("POST", "/api/v1/maintenance/discover-additional", AccessLevel.Admin), ("POST", "/api/v1/subbed/search-all", AccessLevel.Admin),
        ("POST", "/api/v1/subbed/repair-unmatched", AccessLevel.Admin),
    ];

    public static void Register(TestSuite suite) =>
        suite.Add("phase 9 wiring: imports, sharing, metadata and discovery routes are mapped with their access level; lists answer on an empty catalogue",
            () => Run().GetAwaiter().GetResult());

    private static string Shape(string pattern) => Regex.Replace(pattern.StartsWith('/') ? pattern : "/" + pattern, "\\{[^}]+\\}", "{}").TrimEnd('/');
    private static string Concrete(string pattern) => Regex.Replace(pattern, "\\{[^}]+\\}", "1");

    private static async Task Run()
    {
        using var temp = new Sandbox();
        var settings = new AutoSourcarrSettings
        {
            DataDir = Path.Combine(temp.Path, "data"), SettingsConfigDir = Path.Combine(temp.Path, "config"), SourceEngineMode = "external",
            SourceSuwayomiUrl = "http://127.0.0.1:1", AdminToken = "configured-admin-token", AutosourcarrReaderApiToken = "configured-reader-token",
            AuthMethod = "forms",
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
            runtime.Access!.Accounts.CreateOwner("owner", "owner-password");

            var mapped = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                    .Select(method => method + " " + Shape(endpoint.RoutePattern.RawText ?? "")))
                .ToHashSet(StringComparer.Ordinal);
            using var anonymous = host.GetTestClient();
            anonymous.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            foreach (var (method, pattern, level) in Routes)
            {
                Check.True(mapped.Contains(method + " " + Shape(pattern)), "mapped: " + method + " " + pattern);
                var path = Concrete(pattern);
                Check.True(AccessPolicy.Required(method, path) == level, level + ": " + method + " " + path);
                // Refused before any handler runs, so nothing is changed.
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (method is "POST" or "PUT") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var response = await anonymous.SendAsync(request);
                Check.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            using var admin = host.GetTestClient();
            admin.DefaultRequestHeaders.TryAddWithoutValidation("X-Admin-Token", "configured-admin-token");
            admin.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            foreach (var path in new[] { "/api/v1/imports", "/api/v1/compatibility", "/api/v1/catalog-sharing/status", "/api/v1/catalog-sharing/decisions",
                "/api/v1/catalog-sharing/records", "/api/v1/identity-review" })
            {
                using var response = await admin.GetAsync(path);
                var body = await response.Content.ReadAsStringAsync();
                Check.True(response.StatusCode == HttpStatusCode.OK, path + ": " + (int)response.StatusCode + " " + body);
                Check.True(body.StartsWith('{'), path + " answers a JSON object");
            }
            // An unknown entry has no metadata; without a main Suwayomi the import upload is unavailable, not a crash.
            using (var missing = await admin.GetAsync("/api/v1/entries/424242/metadata"))
                Check.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using (var plan = await admin.PostAsync("/api/v1/imports/suwayomi-plan", new StringContent("{}", Encoding.UTF8, "application/json")))
                Check.Equal(HttpStatusCode.NotFound, plan.StatusCode);
        }
        finally
        {
            host?.Dispose();
            await runtime.DisposeAsync();
        }
    }
}
