using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests;

public static class ApiV1Tests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("api v1: settings need the admin token and never return secret values", () =>
        {
            using var temp = new Sandbox();
            using var host = Start(temp.Path, new AutoSourcarrSettings
            {
                AdminToken = "admin-value-1", AutosourcarrReaderApiToken = "reader-value-2", RepairAiApiKey = "repair-value-3"
            });
            var client = host.GetTestClient();

            Check.Equal(HttpStatusCode.Unauthorized, client.GetAsync("/api/v1/settings").GetAwaiter().GetResult().StatusCode);

            var body = Send(client, HttpMethod.Get, "/api/v1/settings", "admin-value-1", null, HttpStatusCode.OK);
            foreach (var secret in new[] { "admin-value-1", "reader-value-2", "repair-value-3" })
                Check.True(!body.Contains(secret, StringComparison.Ordinal), "A secret value was returned.");
            using var document = JsonDocument.Parse(body);
            var items = document.RootElement.GetProperty("groups").EnumerateArray().SelectMany(group => group.GetProperty("items").EnumerateArray()).ToArray();
            Check.Equal(AutoSourcarrSettings.Definitions.Count, items.Length);
            var admin = items.Single(item => item.GetProperty("key").GetString() == "ADMIN_TOKEN");
            Check.True(admin.GetProperty("secret").GetBoolean() && admin.GetProperty("is_set").GetBoolean());
            Check.Equal(JsonValueKind.Null, admin.GetProperty("value").ValueKind);
            Check.True(items.All(item => item.GetProperty("description").GetString()!.Length > 0), "Every setting has a description.");
            Check.True(!document.RootElement.GetProperty("config_file_exists").GetBoolean());
        });

        suite.Add("api v1: saving settings writes config.json with encrypted secrets and a key-only change list", () =>
        {
            using var temp = new Sandbox();
            using var host = Start(temp.Path, new AutoSourcarrSettings { AdminToken = "admin" });
            var client = host.GetTestClient();
            var revision = Revision(client);
            var saved = Send(client, HttpMethod.Put, "/api/v1/settings", "admin",
                $$"""{"revision":"{{revision}}","values":{"BROWSE_PAGE_SIZE":75,"TRANSLATION_API_KEY":"translation-secret-77","TZ":"UTC"} }""", HttpStatusCode.OK);
            Check.True(!saved.Contains("translation-secret-77", StringComparison.Ordinal), "The response must not echo a secret.");
            using (var result = JsonDocument.Parse(saved))
            {
                var keys = result.RootElement.GetProperty("changed_keys").EnumerateArray().Select(key => key.GetString()).ToArray();
                Check.Equal("BROWSE_PAGE_SIZE,TRANSLATION_API_KEY,TZ", string.Join(',', keys));
                Check.True(result.RootElement.GetProperty("restart_required").GetBoolean());
            }
            var file = File.ReadAllText(Path.Combine(temp.Path, ConfigStore.FileName));
            Check.True(!file.Contains("translation-secret-77", StringComparison.Ordinal) && file.Contains("\"BROWSE_PAGE_SIZE\": \"75\"", StringComparison.Ordinal), "config.json content");
            Check.Equal("translation-secret-77", new ConfigStore(temp.Path).Load().Settings["TRANSLATION_API_KEY"]);

            // The old revision is stale now; nothing is written.
            Send(client, HttpMethod.Put, "/api/v1/settings", "admin", $$"""{"revision":"{{revision}}","values":{"TZ":"Europe/Paris"} }""", HttpStatusCode.Conflict);
            var current = Revision(client);
            var bad = Send(client, HttpMethod.Put, "/api/v1/settings", "admin", $$"""{"revision":"{{current}}","values":{"AUTO_MATCH_THRESHOLD":0.5} }""", HttpStatusCode.BadRequest);
            Check.True(bad.Contains("0.94", StringComparison.Ordinal), "The 0.94 floor is enforced: " + bad);
            Send(client, HttpMethod.Put, "/api/v1/settings", "admin", $$"""{"revision":"{{current}}","values":{"NOT_A_SETTING":"x"} }""", HttpStatusCode.BadRequest);
            Send(client, HttpMethod.Put, "/api/v1/settings", "admin", $$"""{"revision":"{{current}}","values":{"TZ":"Not/AZone"} }""", HttpStatusCode.BadRequest);
            // Null resets to the default; an empty secret removes it.
            Send(client, HttpMethod.Put, "/api/v1/settings", "admin", $$"""{"revision":"{{current}}","values":{"BROWSE_PAGE_SIZE":null,"TRANSLATION_API_KEY":""} }""", HttpStatusCode.OK);
            var after = new ConfigStore(temp.Path).Load().Settings;
            Check.True(!after.ContainsKey("BROWSE_PAGE_SIZE") && !after.ContainsKey("TRANSLATION_API_KEY") && after["TZ"] == "UTC", "reset and removal");
        });

        suite.Add("api v1: setup wizard reads and finishes through config.json", () =>
        {
            using var temp = new Sandbox();
            using var host = Start(temp.Path, new AutoSourcarrSettings { AdminToken = "admin", MainSuwayomiPassword = "library-pass" });
            var client = host.GetTestClient();
            string revision;
            using (var setup = JsonDocument.Parse(Send(client, HttpMethod.Get, "/api/v1/setup", "admin", null, HttpStatusCode.OK)))
            {
                var root = setup.RootElement;
                Check.True(!root.GetProperty("completed").GetBoolean());
                revision = root.GetProperty("revision").GetString()!;
                var fields = root.GetProperty("fields").EnumerateArray().ToDictionary(field => field.GetProperty("key").GetString()!);
                Check.Equal(SetupFields.ByKey.Count, fields.Count);
                Check.Equal(JsonValueKind.Array, fields["EXTENSION_STORES"].GetProperty("value").ValueKind);
                Check.True(fields["MAIN_SUWAYOMI_PASSWORD"].GetProperty("configured").GetBoolean() && fields["MAIN_SUWAYOMI_PASSWORD"].GetProperty("value").GetString() == "");
                Check.Equal(JsonValueKind.False, root.GetProperty("reader_build").GetProperty("ready").ValueKind);
            }
            var transaction = Guid.NewGuid().ToString();
            var finish = $$"""{"revision":"{{revision}}","transaction":"{{transaction}}","changes":{"TZ":"UTC","AUTOMATIC_DUPLICATE_MATCHING":false,"EXTENSION_STORES":["https://example.org/index.min.json"]} }""";
            using (var first = JsonDocument.Parse(Send(client, HttpMethod.Post, "/api/v1/setup/finish", "admin", finish, HttpStatusCode.OK)))
                Check.Equal("AUTOMATIC_DUPLICATE_MATCHING,EXTENSION_STORES,TZ", string.Join(',', first.RootElement.GetProperty("changed").EnumerateArray().Select(key => key.GetString())));
            // The same request id again (a retried click) returns the first result and writes nothing new.
            var bytes = File.ReadAllBytes(Path.Combine(temp.Path, ConfigStore.FileName));
            Send(client, HttpMethod.Post, "/api/v1/setup/finish", "admin", finish, HttpStatusCode.OK);
            Check.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(temp.Path, ConfigStore.FileName))), "A repeated finish must not write.");
            using (var again = JsonDocument.Parse(Send(client, HttpMethod.Get, "/api/v1/setup", "admin", null, HttpStatusCode.OK)))
            {
                Check.True(again.RootElement.GetProperty("completed").GetBoolean());
                var fields = again.RootElement.GetProperty("fields").EnumerateArray().ToDictionary(field => field.GetProperty("key").GetString()!);
                Check.Equal("https://example.org/index.min.json", fields["EXTENSION_STORES"].GetProperty("value")[0].GetString());
                Check.Equal(JsonValueKind.False, fields["AUTOMATIC_DUPLICATE_MATCHING"].GetProperty("value").ValueKind);
            }
            // Changing the library address without touching its saved password is refused (0.4.x rule).
            var current = Revision(client);
            var moved = Send(client, HttpMethod.Post, "/api/v1/setup/finish", "admin",
                $$"""{"revision":"{{current}}","transaction":"{{Guid.NewGuid()}}","changes":{"MAIN_SUWAYOMI_URL":"http://other:4567"} }""", HttpStatusCode.BadRequest);
            Check.True(moved.Contains("saved credential", StringComparison.Ordinal), moved);
            Send(client, HttpMethod.Post, "/api/v1/setup/restart", "admin", "{}", HttpStatusCode.Conflict);
            var reader = Send(client, HttpMethod.Post, "/api/v1/setup/reader-preflight", "admin",
                $$"""{"revision":"{{current}}","changes":{"AUTOSOURCARR_PUBLIC_URL":"https://manga.example.org","AUTOSOURCARR_READER_API_TOKEN":"{{new string('k', 40)}}"} }""", HttpStatusCode.OK);
            using (var check = JsonDocument.Parse(reader))
                Check.True(check.RootElement.GetProperty("reader_build").GetProperty("ready").GetBoolean(), reader);
            Check.True(!File.ReadAllText(Path.Combine(temp.Path, ConfigStore.FileName)).Contains("kkkkkkkk", StringComparison.Ordinal), "Preflight never saves.");
        });

        suite.Add("api v1: admin endpoints are not reachable through the reader alias", () =>
        {
            using var temp = new Sandbox();
            using var host = Start(temp.Path, new AutoSourcarrSettings { AdminToken = "", AutosourcarrReaderApiToken = "reader" });
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/reader/v1/settings");
            request.Headers.Add("X-AutoSourcarr-Api-Key", "reader");
            using var response = host.GetTestClient().SendAsync(request).GetAwaiter().GetResult();
            Check.Equal(HttpStatusCode.NotFound, response.StatusCode);
            // With no admin token configured the direct path is open, so the 404 above comes from the alias rule.
            Check.Equal(HttpStatusCode.OK, host.GetTestClient().GetAsync("/api/v1/settings").GetAwaiter().GetResult().StatusCode);
        });

        suite.Add("api v1: openapi.json describes the typed contract from the same JSON types", () =>
        {
            using var temp = new Sandbox();
            using var host = Start(temp.Path, new AutoSourcarrSettings());
            var text = host.GetTestClient().GetStringAsync("/api/v1/openapi.json").GetAwaiter().GetResult();
            var document = JsonNode.Parse(text)!.AsObject();
            Check.Equal("3.1.0", document["openapi"]!.GetValue<string>());
            var paths = document["paths"]!.AsObject();
            Check.Equal("getSettings", paths["/api/v1/settings"]!["get"]!["operationId"]!.GetValue<string>());
            Check.Equal("getSettingsConversion", paths["/api/v1/settings/conversion"]!["get"]!["operationId"]!.GetValue<string>());
            Check.True(!paths.ContainsKey("/api/v1/openapi.json"), "openapi.json must not describe itself.");
            Check.Equal("#/components/schemas/SettingsOverview",
                paths["/api/v1/settings"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>());
            var overview = document["components"]!["schemas"]!["SettingsOverview"]!["properties"]!.AsObject();
            Check.True(overview.ContainsKey("groups") && overview.ContainsKey("config_file_exists"), "Schema uses the snake_case wire names.");
            Check.Equal("#/components/schemas/SettingsSaveRequest",
                paths["/api/v1/settings"]!["put"]!["requestBody"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>());
            foreach (var path in new[] { "/api/v1/setup", "/api/v1/setup/finish", "/api/v1/setup/reader-preflight", "/api/v1/setup/restart",
                         "/api/v1/settings/schema", "/api/v1/settings/update-notice", "/api/v1/config", "/api/v1/storage/database",
                         "/api/v1/status/stats", "/api/v1/diagnostics/metrics", "/api/v1/health" })
                Check.True(paths.ContainsKey(path), "OpenAPI misses " + path);
            // Stable output: building twice gives identical text, so CI can diff the generated client.
            Check.Equal(text, host.GetTestClient().GetStringAsync("/api/v1/openapi.json").GetAwaiter().GetResult());
        });
    }

    /// <summary>Test-runner mode used by CI: writes the OpenAPI document the UI client is generated from.</summary>
    public static int WriteOpenApi(string path)
    {
        using var temp = new Sandbox();
        var settings = new AutoSourcarrSettings();
        // The documented groups ApplicationRuntime.ConfigureHttp maps beside MapApiV1, with the same prefixes. No request
        // is sent: the dependencies only have to exist for the routes and their metadata to be mapped.
        using var health = new HealthCoordinationTests.Fixture();
        using var adminLock = new SemaphoreSlim(1, 1);
        var io = new BoundedWorkScheduler(1, 1);
        var fetcher = new PublicSourceFetcher();
        var previews = new SourceRepositoryPreview(fetcher);
        try
        {
            var repositories = new SourceRepositoryStore(new CanonicalConfiguration(temp.Path));
            using var host = Start(temp.Path, settings, endpoints =>
            {
                endpoints.MapSourceRepositories(repositories, previews, io, () => settings, "/api/v1/source-repositories");
                endpoints.MapSourceHealth(health.Coordinator, () => settings, adminLock, "/api/v1/source-health");
            });
            var document = OpenApiDocumentBuilder.Build(host.Services.GetRequiredService<EndpointDataSource>());
            var text = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, text);
            Console.WriteLine("Wrote " + path);
            return 0;
        }
        finally
        {
            previews.DisposeAsync().AsTask().GetAwaiter().GetResult();
            fetcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
            io.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Test-runner mode used by CI: writes docs/settings.md from the settings schema.</summary>
    public static int WriteSettingsDocs(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, SettingsSchema.Markdown());
        Console.WriteLine("Wrote " + path);
        return 0;
    }

    private static string Revision(HttpClient client)
    {
        using var document = JsonDocument.Parse(Send(client, HttpMethod.Get, "/api/v1/settings", "admin", null, HttpStatusCode.OK));
        return document.RootElement.GetProperty("revision").GetString()!;
    }

    private static string Send(HttpClient client, HttpMethod method, string path, string token, string? json, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Admin-Token", token);
        if (json is not null)
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = client.SendAsync(request).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Check.True(response.StatusCode == expected, $"{method} {path}: expected {expected}, got {response.StatusCode}: {body}");
        return body;
    }

    private static IHost Start(string root, AutoSourcarrSettings settings, Action<IEndpointRouteBuilder>? extra = null)
    {
        var services = new ApiV1Services(() => settings, root, null);
        var host = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
            .ConfigureServices(collection => collection.AddRouting())
            .Configure(app =>
            {
                app.UseMiddleware<ReaderAuthorizationMiddleware>((Func<AutoSourcarrSettings>)(() => settings));
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapApiV1(services);
                    extra?.Invoke(endpoints);
                });
            })).Build();
        host.StartAsync().GetAwaiter().GetResult();
        return host;
    }
}
