using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests.Api;

/// <summary>Phase 4: /api/v1 source engine, source browser and attention routes against a stand-in source host.</summary>
public static class SourceRuntimeApiTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("api v1 source runtime: engine status, browser identity saved and applied to the host, clear data, reload, 409 in external mode, 503 without a runtime", () =>
        {
            using var temp = new Sandbox();
            var calls = new List<string>();
            var agent = SourceEngineEnvironment.DefaultUserAgent;
            var handler = new Handler(async (request, _) =>
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                lock (calls) calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath + " " + body);
                Check.Equal("run-token", request.Headers.GetValues("X-Source-Host-Token").Single());
                JsonObject answer = request.RequestUri!.AbsolutePath switch
                {
                    "/v1/health" => new() { ["ok"] = true, ["webview"] = "ready", ["sources"] = 3 },
                    "/v1/browser/user-agent" => Browser(agent = JsonNode.Parse(body)!["user_agent"]!.GetValue<string>() is { Length: > 0 } set ? set : SourceEngineEnvironment.DefaultUserAgent),
                    "/v1/browser" or "/v1/browser/clear-data" => Browser(agent),
                    "/v1/sources/reload" or "/v1/extensions/fetch" => new() { ["ok"] = true },
                    "/v1/extension-stores" => new() { ["urls"] = JsonNode.Parse(body)!["urls"]!.DeepClone() },
                    _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath)
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
            });
            var settings = new AutoSourcarrSettings { AdminToken = "admin", SourceEngineMode = "integrated" };
            using var source = SuwayomiClient.ForSourceHost(settings, "http://127.0.0.1:4567", "run-token", handler: handler);
            var runtime = new SourceRuntimeServices
            {
                Engine = () => new JsonObject { ["mode"] = "integrated", ["state"] = "source-host", ["ready"] = true, ["restarts"] = 0, ["error"] = "" },
                Client = source, AdminLock = new SemaphoreSlim(1, 1)
            };
            using var host = Start(temp.Path, settings, runtime);
            var client = host.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Admin-Token", "admin");

            var engine = Json(client.GetAsync("/api/v1/source-engine").GetAwaiter().GetResult(), HttpStatusCode.OK);
            Check.Equal("ready", engine["automatic_challenges"]!["state"]!.GetValue<string>());
            Check.Equal(3, engine["host"]!["sources"]!.GetValue<int>());
            Check.Equal(true, Json(client.PostAsync("/api/v1/source-engine/challenges", null).GetAwaiter().GetResult(), HttpStatusCode.OK)["queued"]!.GetValue<bool>());

            var browser = Json(client.GetAsync("/api/v1/source-browser").GetAwaiter().GetResult(), HttpStatusCode.OK);
            Check.Equal("default", browser["preferences"]!["preset"]!.GetValue<string>());
            Check.Equal(6, browser["presets"]!.AsArray().Count);
            Check.Equal("ready", browser["engine_state"]!.GetValue<string>());

            var bad = client.PutAsync("/api/v1/source-browser/user-agent", Body(new JsonObject { ["preset"] = "custom", ["user_agent"] = "x\r\nInjected: 1" })).GetAwaiter().GetResult();
            Check.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            var saved = Json(client.PutAsync("/api/v1/source-browser/user-agent", Body(new JsonObject { ["preset"] = "custom", ["user_agent"] = "Test Agent/2" })).GetAwaiter().GetResult(), HttpStatusCode.Accepted);
            Check.Equal("Test Agent/2", saved["runtime"]!["network"]!.GetValue<string>());
            Check.Equal(("custom", "Test Agent/2"), SourceBrowserPreferences.Read(temp.Path));
            var reset = Json(client.PutAsync("/api/v1/source-browser/user-agent", Body(new JsonObject { ["preset"] = "default" })).GetAwaiter().GetResult(), HttpStatusCode.Accepted);
            Check.Equal(SourceEngineEnvironment.DefaultUserAgent, reset["runtime"]!["network"]!.GetValue<string>());

            Json(client.PostAsync("/api/v1/source-browser/clear-data", null).GetAwaiter().GetResult(), HttpStatusCode.Accepted);
            Json(client.PostAsync("/api/v1/source-browser/restart", null).GetAwaiter().GetResult(), HttpStatusCode.Accepted);
            lock (calls)
            {
                Check.True(calls.Any(call => call.StartsWith("POST /v1/browser/clear-data", StringComparison.Ordinal)), string.Join('\n', calls));
                Check.True(calls.Any(call => call.StartsWith("POST /v1/sources/reload", StringComparison.Ordinal)), string.Join('\n', calls));
                // Apply saved repositories: the saved list (Keiyoushi by default) goes to the host before the reload.
                var stores = calls.FindIndex(call => call.StartsWith("PUT /v1/extension-stores", StringComparison.Ordinal));
                Check.True(stores >= 0 && calls[stores].Contains("keiyoushi", StringComparison.OrdinalIgnoreCase), string.Join('\n', calls));
                Check.True(stores < calls.FindIndex(call => call.StartsWith("POST /v1/sources/reload", StringComparison.Ordinal)));
            }
            Check.Equal(HttpStatusCode.ServiceUnavailable, client.GetAsync("/api/v1/attention").GetAwaiter().GetResult().StatusCode);
            Check.Equal(HttpStatusCode.Conflict, client.PostAsync("/api/v1/engine/source/compatibility", null).GetAwaiter().GetResult().StatusCode);
            Check.Equal(HttpStatusCode.UnprocessableEntity, client.PostAsync("/api/v1/engine/other/compatibility", null).GetAwaiter().GetResult().StatusCode);

            var external = new AutoSourcarrSettings { AdminToken = "admin", SourceEngineMode = "external" };
            using var externalHost = Start(temp.Path, external, runtime);
            var externalClient = externalHost.GetTestClient();
            externalClient.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
            Check.Equal(HttpStatusCode.Conflict, externalClient.PostAsync("/api/v1/source-browser/clear-data", null).GetAwaiter().GetResult().StatusCode);

            using var bare = Start(temp.Path, settings, null);
            var bareClient = bare.GetTestClient();
            bareClient.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
            Check.Equal(HttpStatusCode.ServiceUnavailable, bareClient.GetAsync("/api/v1/source-engine").GetAwaiter().GetResult().StatusCode);
            using var anonymous = bare.GetTestClient();
            Check.Equal(HttpStatusCode.Unauthorized, anonymous.GetAsync("/api/v1/source-browser").GetAwaiter().GetResult().StatusCode);
        });
    }

    private static JsonObject Browser(string agent) => new()
    {
        ["user_agent"] = agent, ["default_user_agent"] = SourceEngineEnvironment.DefaultUserAgent, ["webview"] = "ready", ["cookies"] = 0
    };

    private static StringContent Body(JsonObject value) => new(value.ToJsonString(), Encoding.UTF8, "application/json");

    private static JsonObject Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Check.True(response.StatusCode == expected, response.StatusCode + " " + text);
        return (JsonObject)JsonNode.Parse(text)!;
    }

    private static IHost Start(string root, AutoSourcarrSettings settings, SourceRuntimeServices? runtime)
    {
        var services = new ApiV1Services(() => settings, root, null) { SourceRuntime = runtime };
        var host = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
            .ConfigureServices(collection => collection.AddRouting())
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapApiV1(services));
            })).Build();
        host.StartAsync().GetAwaiter().GetResult();
        return host;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
