using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Settings;
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

            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings");
            request.Headers.Add("X-Admin-Token", "admin-value-1");
            using var response = client.SendAsync(request).GetAwaiter().GetResult();
            Check.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            foreach (var secret in new[] { "admin-value-1", "reader-value-2", "repair-value-3" })
                Check.True(!body.Contains(secret, StringComparison.Ordinal), "A secret value was returned.");
            using var document = JsonDocument.Parse(body);
            var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Check.Equal(AutoSourcarrSettings.Definitions.Count, items.Length);
            var admin = items.Single(item => item.GetProperty("key").GetString() == "ADMIN_TOKEN");
            Check.True(admin.GetProperty("secret").GetBoolean() && admin.GetProperty("is_set").GetBoolean());
            Check.Equal(JsonValueKind.Null, admin.GetProperty("value").ValueKind);
            Check.True(!document.RootElement.GetProperty("config_file_exists").GetBoolean());
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
            Check.True(overview.ContainsKey("items") && overview.ContainsKey("config_file_exists"), "Schema uses the snake_case wire names.");
            // Stable output: building twice gives identical text, so CI can diff the generated client.
            Check.Equal(text, host.GetTestClient().GetStringAsync("/api/v1/openapi.json").GetAwaiter().GetResult());
        });
    }

    /// <summary>Test-runner mode used by CI: writes the OpenAPI document the UI client is generated from.</summary>
    public static int WriteOpenApi(string path)
    {
        using var temp = new Sandbox();
        using var host = Start(temp.Path, new AutoSourcarrSettings());
        var document = OpenApiDocumentBuilder.Build(host.Services.GetRequiredService<EndpointDataSource>());
        var text = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text);
        Console.WriteLine("Wrote " + path);
        return 0;
    }

    private static IHost Start(string root, AutoSourcarrSettings settings)
    {
        var services = new ApiV1Services(() => settings, root, null);
        var host = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
            .ConfigureServices(collection => collection.AddRouting())
            .Configure(app =>
            {
                app.UseMiddleware<ReaderAuthorizationMiddleware>((Func<AutoSourcarrSettings>)(() => settings));
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapApiV1(services));
            })).Build();
        host.StartAsync().GetAwaiter().GetResult();
        return host;
    }
}
