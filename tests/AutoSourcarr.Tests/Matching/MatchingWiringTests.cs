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
/// Phase 8 integration: the whole app (external mode, empty catalogue) maps every matching and maintenance route of the
/// port map under /api/v1, each one admin-only, and the review lists answer on an empty catalogue.
/// </summary>
public static class MatchingWiringTests
{
    /// <summary>Every /api/v1 route of MatchingEndpoints + MaintenanceEndpoints (port-map) as the endpoint files map them.</summary>
    private static readonly (string Method, string Path)[] Routes =
    [
        // duplicates (8C)
        ("GET", "/api/v1/duplicates"), ("POST", "/api/v1/duplicates/automatic"), ("POST", "/api/v1/duplicates/review"),
        ("GET", "/api/v1/duplicate-candidates"), ("POST", "/api/v1/duplicate-candidates"),
        ("GET", "/api/v1/duplicates/history"), ("GET", "/api/v1/duplicates/history/{eventId}"),
        ("GET", "/api/v1/maintenance/consolidate-preview"), ("POST", "/api/v1/maintenance/consolidate"),
        ("GET", "/api/v1/maintenance/unmatch-duplicates"), ("POST", "/api/v1/entries/{entryId}/unmatch-duplicates"),
        ("POST", "/api/v1/entries/{entryId}/unbind-auto-matches"), ("POST", "/api/v1/entries/{entryId}/redo-automation"),
        // maintenance (8B)
        ("GET", "/api/v1/maintenance/reset-all-manual-actions"), ("POST", "/api/v1/maintenance/reset-all-manual-actions"),
        ("GET", "/api/v1/maintenance/reset-manual-groups"), ("POST", "/api/v1/maintenance/reset-manual-groups"),
        ("GET", "/api/v1/maintenance/reset-manual-matches"), ("POST", "/api/v1/maintenance/reset-manual-matches"),
        ("POST", "/api/v1/maintenance/reset-database"), ("POST", "/api/v1/maintenance/full-reset"), ("POST", "/api/v1/maintenance/reset-covers"),
        ("DELETE", "/api/v1/entries/{entryId}"), ("DELETE", "/api/v1/entries/{entryId}/variants/{sourceId}/{sourceMangaId}"),
        ("POST", "/api/v1/entries/{entryId}/variants/{sourceId}/{sourceMangaId}/promote"), ("DELETE", "/api/v1/entries/{entryId}/matches/{sourceId}"),
        // manual decisions and the decision log (8B)
        ("GET", "/api/v1/manual-actions"), ("GET", "/api/v1/manual-groups"), ("POST", "/api/v1/manual-groups/{groupId}/candidates"),
        ("POST", "/api/v1/manual-groups/{groupId}/members"), ("DELETE", "/api/v1/manual-groups/{groupId}/members/{sourceId}/{sourceMangaId}"),
        ("POST", "/api/v1/entries/{entryId}/separate-source"), ("DELETE", "/api/v1/entries/{entryId}/separate-source/{sourceId}/{sourceMangaId}"),
        ("POST", "/api/v1/entries/{entryId}/manual-match"), ("DELETE", "/api/v1/entries/{entryId}/manual-titles"),
        ("GET", "/api/v1/decisions"), ("POST", "/api/v1/decisions"), ("GET", "/api/v1/decisions/{eventId}"),
        // titles, manual search, presets and policies (8A)
        ("POST", "/api/v1/entries/{entryId}/preferred-title"), ("POST", "/api/v1/entries/{entryId}/aliases"),
        ("GET", "/api/v1/entries/{entryId}/manual-search"), ("POST", "/api/v1/entries/{entryId}/manual-search"),
        ("GET", "/api/v1/matching-presets"), ("POST", "/api/v1/matching-presets"), ("PUT", "/api/v1/matching-presets/{presetId}"),
        ("DELETE", "/api/v1/matching-presets/{presetId}"), ("GET", "/api/v1/matching-policies"), ("PUT", "/api/v1/matching-policies/{scope}/{subject}/{direction}"),
        // mixed entries and tag synonyms (8D)
        ("GET", "/api/v1/mixed-entries"), ("GET", "/api/v1/mixed-entries/{entryId}"), ("POST", "/api/v1/mixed-entries/{entryId}/review"),
        ("POST", "/api/v1/mixed-entries/check"), ("GET", "/api/v1/tag-synonyms"), ("PUT", "/api/v1/tag-synonyms"),
        // review workbench and policy simulator (8E)
        ("GET", "/api/v1/review-workbench"), ("GET", "/api/v1/review-workbench/entries/{entryId}"), ("POST", "/api/v1/review-workbench/preview"),
        ("POST", "/api/v1/review-workbench/apply"), ("POST", "/api/v1/review-workbench/{identifier}/undo"),
        ("GET", "/api/v1/policy-simulator/options"), ("POST", "/api/v1/policy-simulator/preview"), ("POST", "/api/v1/policy-simulator/apply"),
    ];

    public static void Register(TestSuite suite) =>
        suite.Add("matching wiring: every phase-8 route is mapped and admin-only; review lists answer on an empty catalogue", () => Run().GetAwaiter().GetResult());

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
            foreach (var (method, pattern) in Routes)
            {
                Check.True(mapped.Contains(method + " " + Shape(pattern)), "mapped: " + method + " " + pattern);
                var path = Concrete(pattern);
                Check.True(AccessPolicy.Required(method, path) == AccessLevel.Admin, "admin-only: " + method + " " + path);
                // Refused before any handler runs, so nothing is changed.
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (method is "POST" or "PUT") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var response = await anonymous.SendAsync(request);
                Check.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            using var admin = host.GetTestClient();
            admin.DefaultRequestHeaders.TryAddWithoutValidation("X-Admin-Token", "configured-admin-token");
            admin.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            foreach (var path in new[] { "/api/v1/duplicates", "/api/v1/manual-groups", "/api/v1/mixed-entries", "/api/v1/review-workbench", "/api/v1/decisions", "/api/v1/matching-presets" })
            {
                using var response = await admin.GetAsync(path);
                var body = await response.Content.ReadAsStringAsync();
                Check.True(response.StatusCode == HttpStatusCode.OK, path + ": " + (int)response.StatusCode + " " + body);
                Check.True(body.StartsWith('{'), path + " answers a JSON object");
            }
        }
        finally
        {
            host?.Dispose();
            await runtime.DisposeAsync();
        }
    }
}
