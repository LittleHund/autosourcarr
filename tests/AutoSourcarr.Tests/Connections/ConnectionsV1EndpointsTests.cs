using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Accounts;
using AutoSourcarr.Core.Connections;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests.Connections;

/// <summary>
/// Phase 7 /api/v1/connections: list with types and fields, add, change (enabled switch), delete, test saved and
/// unsaved values against a stub handler; secrets never in a response; stale revision 409; validation 400; one
/// connection for single types; admin-only (guest 403, anonymous 401).
/// </summary>
public static class ConnectionsV1EndpointsTests
{
    private const string Root = "/api/v1/connections";
    private const string Secret = "endpoint-sentinel-key";

    public static void Register(TestSuite suite)
    {
        suite.Add("connections v1: add, list, change, test and delete through the API; secrets never returned", () =>
        {
            using var setup = new Setup();
            var listing = Body(setup.Send(HttpMethod.Get, Root), HttpStatusCode.OK);
            var revision = listing["revision"]!.GetValue<string>();
            Check.Equal(0, listing["connections"]!.AsArray().Count);
            var fake = listing["types"]!.AsArray().OfType<JsonObject>().Single(type => type["type"]!.GetValue<string>() == "fake");
            Check.Equal(true, fake["multiple"]!.GetValue<bool>());
            var fields = fake["fields"]!.AsArray().OfType<JsonObject>().ToArray();
            Check.Equal("url,api_key,port,verify", string.Join(',', fields.Select(field => field["key"]!.GetValue<string>())));
            Check.Equal("secret", fields[1]["kind"]!.GetValue<string>());
            Check.Equal("8191", fields[2]["default"]!.GetValue<string>());

            var response = setup.Send(HttpMethod.Post, Root, new JsonObject
            {
                ["revision"] = revision, ["type"] = "fake", ["label"] = "Solver",
                ["values"] = new JsonObject { ["url"] = "http://solver:8191", ["api_key"] = Secret },
            });
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check.True(response.StatusCode == HttpStatusCode.Created, text);
            Check.True(!text.Contains(Secret, StringComparison.Ordinal), "secret not in the add response");
            var created = JsonNode.Parse(text)!.AsObject();
            var id = created["connection"]!["id"]!.GetValue<string>();
            revision = created["revision"]!.GetValue<string>();
            Check.Equal(true, created["connection"]!["values"]!["api_key"]!["set"]!.GetValue<bool>());
            // The first saved connection carries the rollback note (0.4.x refuses config.json with "connections").
            Check.Equal(ConnectionStore.RollbackNotice, created["notice"]!.GetValue<string>());
            Check.True(!File.ReadAllText(Path.Combine(setup.ConfigRoot, ConfigStore.FileName)).Contains(Secret, StringComparison.Ordinal), "encrypted on disk");

            var listText = setup.Send(HttpMethod.Get, Root).Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check.True(!listText.Contains(Secret, StringComparison.Ordinal), "secret not listed");

            // Stale revision, validation, single-connection types and unknown ids.
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, Root, new JsonObject
            {
                ["revision"] = listing["revision"]!.GetValue<string>(), ["type"] = "fake",
                ["values"] = new JsonObject { ["url"] = "http://other", ["api_key"] = "k" },
            }).StatusCode);
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Post, Root, new JsonObject
            {
                ["revision"] = revision, ["type"] = "fake", ["values"] = new JsonObject { ["url"] = "not a url", ["api_key"] = "k" },
            }).StatusCode);
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Post, Root, new JsonObject { ["revision"] = revision, ["type"] = "fake", ["values"] = "x" }).StatusCode);
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Post, Root, new JsonObject { ["type"] = "fake" }).StatusCode);
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Post, Root, new JsonObject { ["revision"] = revision, ["type"] = "nope" }).StatusCode);
            var single = Body(setup.Send(HttpMethod.Post, Root, new JsonObject
            {
                ["revision"] = revision, ["type"] = "single", ["values"] = new JsonObject { ["url"] = "http://suwayomi:4567" },
            }), HttpStatusCode.Created);
            revision = single["revision"]!.GetValue<string>();
            var refused = Body(setup.Send(HttpMethod.Post, Root, new JsonObject
            {
                ["revision"] = revision, ["type"] = "single", ["values"] = new JsonObject { ["url"] = "http://second" },
            }), HttpStatusCode.BadRequest);
            Check.True(refused["detail"]!.GetValue<string>().Contains("one connection", StringComparison.Ordinal), refused.ToJsonString());
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Put, Root + "/ffffffffffff", new JsonObject { ["revision"] = revision }).StatusCode);

            // Enable switch with the marker keeps the secret.
            var changed = Body(setup.Send(HttpMethod.Put, Root + "/" + id, new JsonObject
            {
                ["revision"] = revision, ["enabled"] = false, ["values"] = new JsonObject { ["api_key"] = new JsonObject { ["set"] = true } },
            }), HttpStatusCode.OK);
            Check.Equal(false, changed["connection"]!["enabled"]!.GetValue<bool>());
            Check.True(changed["notice"] is null, "no restart notice for this type");
            Check.Equal(Secret, setup.Store.Find(id)!.Value("api_key"));
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Put, Root + "/" + id, new JsonObject { ["revision"] = revision, ["enabled"] = true }).StatusCode);
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Put, Root + "/" + id, new JsonObject { ["revision"] = changed["revision"]!.DeepClone(), ["enabled"] = "yes" }).StatusCode);
            revision = changed["revision"]!.GetValue<string>();

            // Test the saved connection through the stub handler; the last result is listed.
            var tested = Body(setup.Send(HttpMethod.Post, Root + "/" + id + "/test"), HttpStatusCode.OK);
            Check.Equal(true, tested["ok"]!.GetValue<bool>());
            Check.Equal("3.4.0", tested["details"]!["version"]!.GetValue<string>());
            Check.Equal(true, tested["last_test"]!["ok"]!.GetValue<bool>());
            Check.Equal("http://solver:8191/health", setup.Requests.Single());
            var row = Body(setup.Send(HttpMethod.Get, Root), HttpStatusCode.OK)["connections"]!.AsArray().OfType<JsonObject>().Single(item => item["id"]!.GetValue<string>() == id);
            Check.Equal(true, row["last_test"]!["ok"]!.GetValue<bool>());
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Post, Root + "/ffffffffffff/test").StatusCode);

            // Unsaved values: a wrong key fails, the saved secret is used with id + marker, bad values are refused.
            var wrong = Body(setup.Send(HttpMethod.Post, Root + "/test", new JsonObject
            {
                ["type"] = "fake", ["values"] = new JsonObject { ["url"] = "http://solver:8191", ["api_key"] = "wrong" },
            }), HttpStatusCode.OK);
            Check.Equal(false, wrong["ok"]!.GetValue<bool>());
            var kept = Body(setup.Send(HttpMethod.Post, Root + "/test", new JsonObject
            {
                ["type"] = "fake", ["id"] = id, ["values"] = new JsonObject { ["url"] = "http://solver:8191", ["api_key"] = new JsonObject { ["set"] = true } },
            }), HttpStatusCode.OK);
            Check.Equal(true, kept["ok"]!.GetValue<bool>());
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Post, Root + "/test", new JsonObject { ["type"] = "fake", ["values"] = new JsonObject { ["url"] = "ftp://x", ["api_key"] = "k" } }).StatusCode);
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Post, Root + "/test", new JsonObject { ["type"] = "nope" }).StatusCode);
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Post, Root + "/test", new JsonObject { ["type"] = "fake", ["id"] = "ffffffffffff" }).StatusCode);
            Check.True(setup.Handlers.All(handler => handler.Disposed), "each test's handler disposed");

            // Delete needs the current revision.
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Delete, Root + "/" + id).StatusCode);
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Delete, Root + "/" + id + "?revision=" + listing["revision"]!.GetValue<string>()).StatusCode);
            var deleted = Body(setup.Send(HttpMethod.Delete, Root + "/" + id + "?revision=" + revision), HttpStatusCode.OK);
            Check.Equal(id, deleted["deleted"]!.GetValue<string>());
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Delete, Root + "/" + id + "?revision=" + deleted["revision"]!.GetValue<string>()).StatusCode);
            Check.Equal(1, Body(setup.Send(HttpMethod.Get, Root), HttpStatusCode.OK)["connections"]!.AsArray().Count);
        });

        suite.Add("connections v1: exact change notices, and the one-time Suwayomi import route (202, 404, 409, plan summary)", () =>
        {
            var types = new ConnectionTypes([new FakeConnectionType("fake", true), new FakeConnectionType("flaresolverr", true, urlOnly: true),
                new FakeConnectionType("suwayomi", false, urlOnly: true)]);
            using var setup = new Setup(types: types, withImport: true);
            var revision = Body(setup.Send(HttpMethod.Get, Root), HttpStatusCode.OK)["revision"]!.GetValue<string>();
            var solver = Body(setup.Send(HttpMethod.Post, Root, new JsonObject
            {
                ["revision"] = revision, ["type"] = "flaresolverr", ["values"] = new JsonObject { ["url"] = "http://flaresolverr:8191" },
            }), HttpStatusCode.Created);
            Check.Equal(ConnectionStore.SolverChangeNotice + " " + ConnectionStore.RollbackNotice, solver["notice"]!.GetValue<string>());
            var solverId = solver["connection"]!["id"]!.GetValue<string>();
            var switched = Body(setup.Send(HttpMethod.Put, Root + "/" + solverId, new JsonObject { ["revision"] = solver["revision"]!.DeepClone(), ["enabled"] = false }), HttpStatusCode.OK);
            Check.Equal("The source host restarts to use the new solver list.", switched["notice"]!.GetValue<string>());
            var renamed = Body(setup.Send(HttpMethod.Put, Root + "/" + solverId, new JsonObject { ["revision"] = switched["revision"]!.DeepClone(), ["label"] = "Renamed" }), HttpStatusCode.OK);
            Check.True(renamed["notice"] is null, "a label-only change restarts nothing");
            var suwayomi = Body(setup.Send(HttpMethod.Post, Root, new JsonObject
            {
                ["revision"] = renamed["revision"]!.DeepClone(), ["type"] = "suwayomi", ["values"] = new JsonObject { ["url"] = "http://suwayomi:4567" },
            }), HttpStatusCode.Created);
            Check.Equal("The Suwayomi change feed reconnects.", suwayomi["notice"]!.GetValue<string>());
            var suwayomiId = suwayomi["connection"]!["id"]!.GetValue<string>();
            revision = suwayomi["revision"]!.GetValue<string>();

            // Import: Suwayomi connections only.
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Post, Root + "/ffffffffffff/import").StatusCode);
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, Root + "/" + solverId + "/import").StatusCode);
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Get, Root + "/" + solverId + "/import").StatusCode);
            Check.Equal(HttpStatusCode.BadRequest, setup.Send(HttpMethod.Post, Root + "/" + suwayomiId + "/import", new JsonObject { ["force"] = "yes" }).StatusCode);
            var empty = Body(setup.Send(HttpMethod.Get, Root + "/" + suwayomiId + "/import"), HttpStatusCode.OK);
            Check.True(empty["plan"] is null, "no plan before the first import");
            var queued = Body(setup.Send(HttpMethod.Post, Root + "/" + suwayomiId + "/import"), HttpStatusCode.Accepted);
            Check.Equal("task-1", queued["task_id"]!.GetValue<string>());
            Check.Equal(SuwayomiImport.AwaitingMatchingNote, queued["note"]!.GetValue<string>());
            Body(setup.Send(HttpMethod.Post, Root + "/" + suwayomiId + "/import", new JsonObject { ["force"] = true }), HttpStatusCode.Accepted);
            Check.Equal(suwayomiId + ":False," + suwayomiId + ":True", string.Join(',', setup.Imports));
            var summary = Body(setup.Send(HttpMethod.Get, Root + "/" + suwayomiId + "/import"), HttpStatusCode.OK)["plan"]!.AsObject();
            Check.Equal(SuwayomiImport.AwaitingMatching, summary["status"]!.GetValue<string>());
            Check.Equal(2, summary["counts"]!["total"]!.GetValue<int>());
            Check.Equal(1, summary["counts"]!["sources"]!.GetValue<int>());
            Check.True(summary["saved_at"]!.GetValue<string>().StartsWith("2026-", StringComparison.Ordinal), summary.ToJsonString());
            Check.True(summary["items"] is null, "the summary leaves the items out");

            // A switched-off Suwayomi connection does not import; delete answers the feed notice.
            var off = Body(setup.Send(HttpMethod.Put, Root + "/" + suwayomiId, new JsonObject { ["revision"] = revision, ["enabled"] = false }), HttpStatusCode.OK);
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, Root + "/" + suwayomiId + "/import").StatusCode);
            var deleted = Body(setup.Send(HttpMethod.Delete, Root + "/" + suwayomiId + "?revision=" + off["revision"]!.GetValue<string>()), HttpStatusCode.OK);
            Check.Equal(ConnectionStore.SuwayomiChangeNotice, deleted["notice"]!.GetValue<string>());

            // Without the import delegates the routes answer 503.
            using var plain = new Setup(types: types);
            var row = Body(plain.Send(HttpMethod.Post, Root, new JsonObject
            {
                ["revision"] = Body(plain.Send(HttpMethod.Get, Root), HttpStatusCode.OK)["revision"]!.DeepClone(), ["type"] = "suwayomi",
                ["values"] = new JsonObject { ["url"] = "http://suwayomi:4567" },
            }), HttpStatusCode.Created)["connection"]!["id"]!.GetValue<string>();
            Check.Equal(HttpStatusCode.ServiceUnavailable, plain.Send(HttpMethod.Post, Root + "/" + row + "/import").StatusCode);
            Check.Equal(HttpStatusCode.ServiceUnavailable, plain.Send(HttpMethod.Get, Root + "/" + row + "/import").StatusCode);
        });

        suite.Add("connections v1: without the store the routes answer 503", () =>
        {
            using var setup = new Setup(withStore: false);
            Check.Equal(HttpStatusCode.ServiceUnavailable, setup.Send(HttpMethod.Get, Root).StatusCode);
            Check.Equal(HttpStatusCode.ServiceUnavailable, setup.Send(HttpMethod.Post, Root + "/test", new JsonObject { ["type"] = "fake" }).StatusCode);
        });

        suite.Add("connections v1: every route is admin-only (guest 403, anonymous 401) and skips catalogue admission", () =>
        {
            using var setup = new Setup();
            var routes = setup.Server.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Where(endpoint => (endpoint.RoutePattern.RawText ?? "").StartsWith(Root, StringComparison.Ordinal))
                .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                    .Select(method => (Method: method, Path: endpoint.RoutePattern.RawText!.Replace("{connectionId}", "ffffffffffff", StringComparison.Ordinal))))
                .Distinct().ToArray();
            Check.Equal(8, routes.Length);
            using var anonymous = setup.Server.CreateClient();
            using var guest = setup.Server.CreateClient();
            guest.DefaultRequestHeaders.Add("X-Test-Guest", "1");
            foreach (var (method, path) in routes)
            {
                Check.True(AccessPolicy.Required(method, path) == AccessLevel.Admin, "admin-only: " + method + " " + path);
                if (method != "GET")
                    Check.True(!RequestPolicy.CatalogAdmission(method, path), "no catalogue admission: " + method + " " + path);
                foreach (var (client, expected) in new[] { (anonymous, HttpStatusCode.Unauthorized), (guest, HttpStatusCode.Forbidden) })
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), path);
                    if (method is "POST" or "PUT") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using var response = client.SendAsync(request).GetAwaiter().GetResult();
                    Check.True(response.StatusCode == expected, $"{method} {path}: expected {expected}, got {(int)response.StatusCode}");
                }
            }
            Check.Equal(0, setup.Handlers.Count);
            Check.True(!File.Exists(Path.Combine(setup.ConfigRoot, ConfigStore.FileName)), "nothing written");
        });
    }

    private static JsonObject Body(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Check.True(response.StatusCode == expected, $"expected {expected}, got {response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _temp = new();
        private readonly IHost _host;
        public string ConfigRoot => _temp.Path;
        public ConnectionStore Store { get; }
        public List<StubHandler> Handlers { get; } = [];
        public List<string> Requests { get; } = [];
        public TestServer Server { get; }
        public HttpClient Client { get; }
        public List<string> Imports { get; } = [];

        public Setup(bool withStore = true, ConnectionTypes? types = null, bool withImport = false)
        {
            var settings = new AutoSourcarrSettings { AdminToken = "admin" };
            SuwayomiImportPlan? plan = null;
            Store = new ConnectionStore(_temp.Path, types ?? ConnectionStoreTests.Registry(), Path.Combine(_temp.Path, "data"), handlerFactory: () =>
            {
                var handler = new StubHandler(request =>
                {
                    lock (Requests) Requests.Add(request.RequestUri!.ToString());
                    return request.Headers.TryGetValues("X-Api-Key", out var keys) && keys.Single() == Secret
                        ? ConnectionStoreTests.Json(HttpStatusCode.OK, """{"version":"3.4.0"}""") : ConnectionStoreTests.Json(HttpStatusCode.Unauthorized, "{}");
                });
                lock (Handlers) Handlers.Add(handler);
                return handler;
            });
            var services = new ApiV1Services(() => settings, _temp.Path, null)
            {
                Connections = !withStore ? null : withImport ? new ConnectionsV1Services
                {
                    Store = Store,
                    StartImport = (connection, force) =>
                    {
                        lock (Imports) Imports.Add(connection.Id + ":" + force);
                        plan = SuwayomiImport.Build([JsonNode.Parse("""{"id":1,"sourceId":"42","url":"/a","title":"A"}""")!.AsObject(),
                            JsonNode.Parse("""{"id":2,"sourceId":"42","url":"/b","title":"B"}""")!.AsObject()], null, connection.Value("url"),
                            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
                        return "task-" + Imports.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    },
                    ImportPlan = () => plan,
                } : new ConnectionsV1Services { Store = Store },
            };
            _host = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
                .ConfigureServices(collection => collection.AddRouting())
                .Configure(app =>
                {
                    // Stands in for the account middleware: a signed-in guest.
                    app.Use((Func<HttpContext, RequestDelegate, Task>)((context, next) =>
                    {
                        if (context.Request.Headers.ContainsKey("X-Test-Guest"))
                            context.Items[AccessControlMiddleware.CallerItem] = new Caller(Role.Guest, "guest", "session");
                        return next(context);
                    }));
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapApiV1(services));
                })).Build();
            _host.StartAsync().GetAwaiter().GetResult();
            Server = _host.GetTestServer();
            Client = Server.CreateClient();
            Client.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
        }

        public HttpResponseMessage Send(HttpMethod method, string path, JsonObject? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return Client.SendAsync(request).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Client.Dispose();
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
            _temp.Dispose();
        }
    }
}
