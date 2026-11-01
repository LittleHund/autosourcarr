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
/// Phase 10 integration: the whole app (external mode, empty catalogue) maps every chapter, serving, library, catalogue,
/// tag and reader-extension route under /api/v1 with the right access level, the list reads answer on an empty
/// catalogue, the reader repository is public and the reader API keeps its key check.
/// </summary>
public static class Phase10WiringTests
{
    private static readonly (string Method, string Path, AccessLevel Level)[] Routes =
    [
        // chapters and candidates (10A)
        ("POST", "/api/v1/candidates/{candidateId}/reject", AccessLevel.Admin), ("POST", "/api/v1/candidates/{candidateId}/restore", AccessLevel.Admin),
        ("POST", "/api/v1/candidates/{candidateId}/quality", AccessLevel.Admin), ("GET", "/api/v1/matrix", AccessLevel.Admin),
        ("POST", "/api/v1/matrix/chapter-stats", AccessLevel.Admin),
        ("POST", "/api/v1/entries/{entryId}/source-records/{sourceId}/{sourceMangaId}/check-chapters", AccessLevel.Admin),
        // serving (10A)
        ("GET", "/api/v1/gallery-policies/{sourceId}", AccessLevel.Admin), ("PUT", "/api/v1/gallery-policies/{sourceId}", AccessLevel.Admin),
        ("POST", "/api/v1/gallery-policies/{sourceId}/apply", AccessLevel.Admin), ("GET", "/api/v1/serving-upgrades/{entryId}", AccessLevel.Admin),
        ("PUT", "/api/v1/serving-upgrades/{entryId}", AccessLevel.Admin), ("POST", "/api/v1/serving-upgrades/{entryId}/preview", AccessLevel.Admin),
        ("POST", "/api/v1/serving-upgrades/{entryId}/apply", AccessLevel.Admin), ("GET", "/api/v1/profiles/{entryId}", AccessLevel.Admin),
        ("PUT", "/api/v1/profiles/{entryId}", AccessLevel.Admin),
        // CBZ library and downloads (10B)
        ("GET", "/api/v1/library", AccessLevel.Admin), ("GET", "/api/v1/library/{entryId}/controls", AccessLevel.Admin),
        ("PUT", "/api/v1/library/{entryId}/membership", AccessLevel.Admin), ("PUT", "/api/v1/library/{entryId}/monitoring", AccessLevel.Admin),
        ("POST", "/api/v1/library/{entryId}/observe", AccessLevel.Admin), ("POST", "/api/v1/library/{entryId}/download", AccessLevel.Admin),
        ("GET", "/api/v1/downloads", AccessLevel.Admin), ("POST", "/api/v1/downloads", AccessLevel.Admin),
        ("GET", "/api/v1/activity/history", AccessLevel.Admin),
        // catalogue views, metadata, identity and tags (10C)
        ("GET", "/api/v1/catalogue/entries", AccessLevel.Admin), ("GET", "/api/v1/catalogue/entries/{entryId}", AccessLevel.Admin),
        ("POST", "/api/v1/catalogue/entries/summaries", AccessLevel.Admin), ("GET", "/api/v1/catalogue/browse", AccessLevel.Admin),
        ("GET", "/api/v1/entries/{entryId}/history", AccessLevel.Admin), ("POST", "/api/v1/entries/{entryId}/refresh-metadata", AccessLevel.Admin),
        ("POST", "/api/v1/entries/{entryId}/refresh-covers", AccessLevel.Admin), ("POST", "/api/v1/entries/{entryId}/refresh-all", AccessLevel.Admin),
        ("POST", "/api/v1/entries/{entryId}/identity/refresh", AccessLevel.Admin), ("POST", "/api/v1/entries/{entryId}/discover-additional", AccessLevel.Admin),
        ("GET", "/api/v1/metadata-providers", AccessLevel.Admin), ("GET", "/api/v1/research-improvements/status", AccessLevel.Admin),
        ("GET", "/api/v1/tags", AccessLevel.Admin), ("GET", "/api/v1/tags/suggestions", AccessLevel.Admin), ("GET", "/api/v1/tags/reviews", AccessLevel.Admin),
        ("POST", "/api/v1/tags/review", AccessLevel.Admin), ("POST", "/api/v1/tags/reindex", AccessLevel.Admin),
        // entry tags are catalogue reads like the other entry reads
        ("GET", "/api/v1/entries/{entryId}/tags", AccessLevel.Guest),
        // reader extension (10D)
        ("GET", "/api/v1/reader-extension/setup", AccessLevel.Admin), ("GET", "/api/v1/source-extensions/{pkgName}/apk", AccessLevel.Admin),
        ("GET", "/api/v1/source-extensions/{pkgName}/jar", AccessLevel.Admin),
    ];

    public static void Register(TestSuite suite) =>
        suite.Add("phase 10 wiring: chapter, serving, library, catalogue, tag and reader-extension routes are mapped with their access level; lists answer on an empty catalogue; the reader repository is public",
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
            AuthMethod = "forms", LibraryDir = Path.Combine(temp.Path, "library"),
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
            foreach (var path in new[] { "/api/v1/library", "/api/v1/downloads", "/api/v1/activity/history", "/api/v1/catalogue/entries", "/api/v1/tags",
                "/api/v1/tags/reviews", "/api/v1/metadata-providers", "/api/v1/matrix", "/api/v1/reader-extension/setup" })
            {
                using var response = await admin.GetAsync(path);
                var body = await response.Content.ReadAsStringAsync();
                Check.True(response.StatusCode == HttpStatusCode.OK, path + ": " + (int)response.StatusCode + " " + body);
                Check.True(body.StartsWith('{') || body.StartsWith('['), path + " answers JSON");
            }

            // READ-1: Mihon adds the repository by URL without credentials. No build is present here, so 404, never 401.
            using (var repo = await anonymous.GetAsync("/api/reader/repo/index.min.json"))
                Check.True(repo.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound, "public reader repository: " + (int)repo.StatusCode);
            using (var write = await anonymous.PostAsync("/api/reader/repo/index.min.json", new StringContent("{}", Encoding.UTF8, "application/json")))
                Check.True(write.StatusCode != HttpStatusCode.OK, "the reader repository is read-only");
            // READ-4: the reader API still needs its key, and admin routes never answer through the reader alias.
            using (var reader = await anonymous.GetAsync("/api/reader/v1/sources"))
                Check.Equal(HttpStatusCode.Unauthorized, reader.StatusCode);
            using var keyed = host.GetTestClient();
            keyed.DefaultRequestHeaders.TryAddWithoutValidation("X-AutoSourcarr-Api-Key", "configured-reader-token");
            using (var sources = await keyed.GetAsync("/api/reader/v1/sources"))
                Check.Equal(HttpStatusCode.OK, sources.StatusCode);
            using (var aliased = await keyed.GetAsync("/api/reader/v1/library"))
                Check.Equal(HttpStatusCode.NotFound, aliased.StatusCode);
        }
        finally
        {
            host?.Dispose();
            await runtime.DisposeAsync();
        }
    }
}
