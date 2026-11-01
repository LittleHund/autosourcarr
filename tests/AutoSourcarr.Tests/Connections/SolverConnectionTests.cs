using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Connections;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources.WebView;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoSourcarr.Tests.Connections;

/// <summary>
/// Phase 7 CON-2 and SRC-5: the FlareSolverr/Byparr connection test against stub answers, the solver list the source
/// host gets (enabled solvers only), and the interactive WebView: admin-only session route with URL rules, the
/// one-time WebSocket ticket, and the relay between the page's WebSocket and a fake host stream (in-process).
/// </summary>
public static class SolverConnectionTests
{
    private const string AdminToken = "solver-test-admin";
    private const string HostToken = "solver-test-host-token";

    public static void Register(TestSuite suite)
    {
        suite.Add("solver connection: FlareSolverr and Byparr pass against stub /health and / answers; wrong body, 5xx, bad address and no answer fail", () =>
            TestConnection().GetAwaiter().GetResult());
        suite.Add("solver connection: the host's AUTOSOURCARR_SOLVERS lists enabled solver connections only (valid address, clamped timeout, no duplicates)",
            HostEnvironment);
        suite.Add("source webview: the session route is admin-only, checks addresses and the source's website; the socket needs Origin and a one-time ticket", () =>
            SessionRoute().GetAwaiter().GetResult());
        suite.Add("source webview: the WebSocket relays host frames and events, forwards checked input only, and closes the host session", () =>
            Relay().GetAwaiter().GetResult());
        suite.Add("source webview: an idle session is closed with a reason and the host session is closed", () =>
            IdleClose().GetAwaiter().GetResult());
    }

    private static ConnectionConfig Solver(string type, string url, bool enabled = true, string? timeout = null, string id = "s1")
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { [SolverConnectionType.UrlField] = url };
        if (timeout is not null) values[SolverConnectionType.TimeoutField] = timeout;
        return new ConnectionConfig(id, type, type, enabled, values);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Paths) Paths.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task TestConnection()
    {
        var types = new ConnectionTypes(SolverConnectionType.All);
        Check.True(types.Find("flaresolverr") is SolverConnectionType { Multiple: true }, "flaresolverr registered, several allowed");
        Check.True(types.Find("byparr") is SolverConnectionType, "byparr registered");

        // FlareSolverr: /health ok and a root naming its version. "/v1" at the end of the address is tolerated.
        var flare = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health" => Json(HttpStatusCode.OK, "{\"status\":\"ok\"}"),
            "/" => Json(HttpStatusCode.OK, "{\"msg\":\"FlareSolverr is ready!\",\"version\":\"3.3.21\",\"userAgent\":\"Mozilla/5.0\"}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var passed = await SolverConnectionType.FlareSolverr.TestAsync(Solver("flaresolverr", "http://flaresolverr:8191/v1/"), flare, CancellationToken.None);
        Check.True(passed.Ok, passed.Message);
        Check.Equal("3.3.21", passed.Details!["version"]!.GetValue<string>());
        Check.True(passed.Message.Contains("3.3.21", StringComparison.Ordinal), passed.Message);
        Check.True(flare.Paths.All(path => path.StartsWith("GET ", StringComparison.Ordinal)), "the test only reads: " + string.Join(", ", flare.Paths));
        Check.True(flare.Paths.Contains("GET /health") && !flare.Paths.Any(path => path.Contains("/v1", StringComparison.Ordinal)), string.Join(", ", flare.Paths));

        // Byparr: /health ok, the root is the API docs page (HTML).
        var byparr = new StubHandler(request => request.RequestUri!.AbsolutePath == "/health"
            ? Json(HttpStatusCode.OK, "{\"status\":\"ok\"}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>docs</html>", Encoding.UTF8, "text/html") });
        Check.True((await SolverConnectionType.Byparr.TestAsync(Solver("byparr", "http://byparr:8191"), byparr, CancellationToken.None)).Ok, "byparr health");

        // An older FlareSolverr without /health: the root's "ready" message is enough.
        var oldFlare = new StubHandler(request => request.RequestUri!.AbsolutePath == "/"
            ? Json(HttpStatusCode.OK, "{\"msg\":\"FlareSolverr is ready!\",\"version\":\"2.2.10\"}")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        Check.True((await SolverConnectionType.FlareSolverr.TestAsync(Solver("flaresolverr", "http://old:8191"), oldFlare, CancellationToken.None)).Ok, "root ready");

        // Something else answers: wrong body fails.
        var wrong = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>router login</html>", Encoding.UTF8, "text/html") });
        var wrongResult = await SolverConnectionType.FlareSolverr.TestAsync(Solver("flaresolverr", "http://192.168.1.1"), wrong, CancellationToken.None);
        Check.True(!wrongResult.Ok && wrongResult.Message.Contains("FlareSolverr-compatible", StringComparison.Ordinal), wrongResult.Message);
        var notOk = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"status\":\"error\",\"msg\":\"starting\"}"));
        Check.True(!(await SolverConnectionType.FlareSolverr.TestAsync(Solver("flaresolverr", "http://x:8191"), notOk, CancellationToken.None)).Ok, "status error");

        // 5xx fails with the status.
        var broken = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var brokenResult = await SolverConnectionType.FlareSolverr.TestAsync(Solver("flaresolverr", "http://x:8191"), broken, CancellationToken.None);
        Check.True(!brokenResult.Ok && brokenResult.Message.Contains("500", StringComparison.Ordinal), brokenResult.Message);

        // No answer, and addresses that are not a solver base URL.
        var down = new StubHandler(_ => throw new HttpRequestException("Connection refused"));
        var downResult = await SolverConnectionType.FlareSolverr.TestAsync(Solver("flaresolverr", "http://x:8191"), down, CancellationToken.None);
        Check.True(!downResult.Ok && downResult.Message.Contains("No answer", StringComparison.Ordinal), downResult.Message);
        foreach (var bad in new[] { "", "ftp://x:21", "http://user:pass@x:8191", "http://x:8191/?q=1", "not a url" })
        {
            var result = await SolverConnectionType.FlareSolverr.TestAsync(Solver("flaresolverr", bad), down, CancellationToken.None);
            Check.True(!result.Ok, "refused address: " + bad);
        }
        Check.Equal("http://flaresolverr:8191/", SolverConnectionType.BaseUrl(" http://flaresolverr:8191/v1 ")!.AbsoluteUri);
    }

    private static void HostEnvironment()
    {
        var json = SolverConnectionType.HostEnvironment(
        [
            Solver("flaresolverr", "http://flaresolverr:8191/v1", id: "a"),
            Solver("byparr", "http://byparr:8191", timeout: "5", id: "b"),
            Solver("flaresolverr", "http://disabled:8191", enabled: false, id: "c"),
            Solver("flaresolverr", "http://flaresolverr:8191/", id: "d"),
            Solver("flaresolverr", "ftp://bad", id: "e"),
            Solver("anilist", "http://not-a-solver", id: "f"),
            Solver("byparr", "http://slow:8191", timeout: "9999", id: "g"),
        ]);
        var rows = JsonNode.Parse(json)!.AsArray().OfType<JsonObject>().ToArray();
        Check.Equal(3, rows.Length);
        Check.Equal("http://flaresolverr:8191", rows[0]["url"]!.GetValue<string>());
        Check.Equal(60_000, rows[0]["timeoutMs"]!.GetValue<int>());
        Check.Equal("flaresolverr", rows[0]["type"]!.GetValue<string>());
        Check.Equal("http://byparr:8191", rows[1]["url"]!.GetValue<string>());
        Check.Equal(10_000, rows[1]["timeoutMs"]!.GetValue<int>());
        Check.Equal(300_000, rows[2]["timeoutMs"]!.GetValue<int>());
        Check.True(!json.Contains("disabled", StringComparison.Ordinal) && !json.Contains("not-a-solver", StringComparison.Ordinal), json);
        Check.Equal("[]", SolverConnectionType.HostEnvironment([Solver("flaresolverr", "http://x:8191", enabled: false)]));
    }

    /// <summary>A stand-in for the source host's WebView routes on a loopback port, with a scripted stream.</summary>
    private sealed class FakeHost : IAsyncDisposable
    {
        private IHost? _host;
        public string Address { get; private set; } = "";
        public ConcurrentQueue<JsonObject> Inputs { get; } = new();
        public ConcurrentQueue<string> Deleted { get; } = new();
        public ConcurrentQueue<JsonObject> Opened { get; } = new();
        public TaskCompletionSource<bool> StreamOpened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _closed = new(StringComparer.Ordinal);
        private int _next;

        public static async Task<FakeHost> StartAsync()
        {
            var fake = new FakeHost();
            fake._host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).ConfigureWebHost(web => web
                .UseKestrel(options => options.Listen(IPAddress.Loopback, 0))
                .ConfigureServices(services => services.AddRouting())
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        if (context.Request.Headers["X-Source-Host-Token"].ToString() != HostToken) { context.Response.StatusCode = 401; return; }
                        await next(context);
                    });
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/v1/webview/sessions", (Func<HttpContext, Task<IResult>>)(async context =>
                        {
                            var body = (JsonObject)(await JsonNode.ParseAsync(context.Request.Body))!;
                            fake.Opened.Enqueue(body);
                            var id = "abc" + Interlocked.Increment(ref fake._next).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            fake._closed[id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            return Results.Json(new JsonObject { ["id"] = id, ["url"] = body["url"]?.GetValue<string>() ?? "about:blank" });
                        }));
                        endpoints.MapGet("/v1/webview/sessions/{id}/stream", (Func<HttpContext, Task<IResult>>)(async context =>
                        {
                            var id = context.Request.RouteValues["id"]!.ToString()!;
                            if (!fake._closed.TryGetValue(id, out var closed)) return Results.NotFound();
                            context.Response.ContentType = "application/octet-stream";
                            var output = context.Response.Body;
                            async Task Write(char type, byte[] payload)
                            {
                                var header = new byte[5];
                                header[0] = (byte)type;
                                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
                                await output.WriteAsync(header);
                                await output.WriteAsync(payload);
                                await output.FlushAsync();
                            }
                            await Write('E', Encoding.UTF8.GetBytes("{\"type\":\"ready\",\"id\":\"" + id + "\"}"));
                            await Write('H', []);
                            await Write('F', [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9]);
                            await Write('E', Encoding.UTF8.GetBytes("{\"type\":\"load\",\"url\":\"https://www.source.example/\",\"status\":200}"));
                            fake.StreamOpened.TrySetResult(true);
                            try { await closed.Task.WaitAsync(context.RequestAborted); } catch (OperationCanceledException) { }
                            return Results.Empty;
                        }));
                        endpoints.MapPost("/v1/webview/sessions/{id}/input", (Func<HttpContext, Task<IResult>>)(async context =>
                        {
                            fake.Inputs.Enqueue((JsonObject)(await JsonNode.ParseAsync(context.Request.Body))!);
                            return Results.Json(new JsonObject { ["ok"] = true });
                        }));
                        endpoints.MapDelete("/v1/webview/sessions/{id}", (Func<HttpContext, IResult>)(context =>
                        {
                            var id = context.Request.RouteValues["id"]!.ToString()!;
                            fake.Deleted.Enqueue(id);
                            if (fake._closed.TryGetValue(id, out var closed)) closed.TrySetResult(true);
                            return Results.Json(new JsonObject { ["ok"] = true });
                        }));
                    });
                })).Build();
            await fake._host.StartAsync();
            fake.Address = fake._host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return fake;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var closed in _closed.Values) closed.TrySetResult(true);
            if (_host is not null) { await _host.StopAsync(TimeSpan.FromSeconds(5)); _host.Dispose(); }
        }
    }

    /// <summary>The app side: the WebView routes alone, with the admin filter's token check and WebSockets on.</summary>
    private sealed class App : IAsyncDisposable
    {
        private IHost _host = null!;
        public TestServer Server { get; private set; } = null!;
        public SourceWebViewBroker Broker { get; private set; } = null!;

        public static async Task<App> StartAsync(FakeHost fake, SourceWebViewOptions? options = null)
        {
            var app = new App();
            var settings = new AutoSourcarrSettings { AdminToken = AdminToken };
            app.Broker = new SourceWebViewBroker(() => new SourceHostEndpoint(new Uri(fake.Address), HostToken),
                options: options ?? new SourceWebViewOptions { FrameInterval = TimeSpan.FromMilliseconds(10) });
            var services = new SourceWebViewServices
            {
                Broker = app.Broker,
                SourceBaseUrl = (id, _) => Task.FromResult<string?>(id == "42" ? "https://www.source.example/" : null),
            };
            app._host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(collection => collection.AddRouting()).Configure(builder =>
            {
                builder.UseWebSockets();
                builder.UseRouting();
                builder.UseEndpoints(endpoints => endpoints.MapSourceWebView(() => services, () => settings));
            })).Build();
            await app._host.StartAsync();
            app.Server = app._host.GetTestServer();
            return app;
        }

        public async Task<(HttpStatusCode Status, JsonObject Body)> OpenAsync(string body, string? token = AdminToken)
        {
            using var client = Server.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, SourceWebViewEndpoints.SessionPath) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (token is not null) request.Headers.Add("X-Admin-Token", token);
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, JsonNode.Parse(text.Length == 0 ? "{}" : text) as JsonObject ?? []);
        }

        public async Task<WebSocket> ConnectAsync(JsonObject opened, string? ticket = null, string? origin = "http://localhost")
        {
            var client = Server.CreateWebSocketClient();
            client.SubProtocols.Add(SourceWebViewBroker.SubProtocol);
            client.SubProtocols.Add(SourceWebViewBroker.TicketPrefix + (ticket ?? opened["ticket"]!.GetValue<string>()));
            client.ConfigureRequest = request => { if (origin is not null) request.Headers.Origin = origin; };
            return await client.ConnectAsync(new Uri(Server.BaseAddress, SourceWebViewEndpoints.SocketPath + "?session=" + opened["session_id"]!.GetValue<string>()),
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Broker.DisposeAsync();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private static async Task<bool> Refused(Func<Task<WebSocket>> connect)
    {
        try { using var socket = await connect(); return false; }
        catch (Exception error) when (error is InvalidOperationException or WebSocketException or HttpRequestException) { return true; }
    }

    private static async Task SessionRoute()
    {
        Check.Equal(AccessLevel.Public, AccessPolicy.Required("GET", SourceWebViewEndpoints.SocketPath));
        Check.Equal(AccessLevel.Admin, AccessPolicy.Required("POST", SourceWebViewEndpoints.SessionPath));
        await using var fake = await FakeHost.StartAsync();
        await using var app = await App.StartAsync(fake);

        Check.Equal(HttpStatusCode.Unauthorized, (await app.OpenAsync("{\"url\":\"https://www.source.example/\"}", token: null)).Status);
        Check.Equal(HttpStatusCode.Unauthorized, (await app.OpenAsync("{\"url\":\"https://www.source.example/\"}", token: "wrong")).Status);
        Check.True(fake.Opened.IsEmpty, "nothing opened for a refused caller");

        foreach (var bad in new[] { "{\"url\":\"file:///etc/passwd\"}", "{\"url\":\"javascript:alert(1)\"}", "{\"url\":\"http://user:pw@www.source.example/\"}",
            "{\"url\":42}", "{\"source_id\":\"42\",\"url\":\"https://elsewhere.example/\"}",
            // Item 10: link-local, cloud metadata and the source host's own loopback port are never opened.
            "{\"url\":\"http://169.254.169.254/latest/meta-data/\"}", "{\"url\":\"http://[fe80::1]/\"}", "{\"url\":\"http://metadata.google.internal/\"}",
            "{\"url\":\"http://[::ffff:169.254.169.254]/\"}", "{\"url\":\"" + fake.Address.TrimEnd('/') + "/v1/health\"}" })
            Check.Equal(HttpStatusCode.BadRequest, (await app.OpenAsync(bad)).Status);
        var reach = (await app.OpenAsync("{\"url\":\"http://169.254.169.254/\"}")).Body;
        Check.True(reach["detail"]!.GetValue<string>().Contains("link-local", StringComparison.Ordinal), reach.ToJsonString());
        // Navigate input gets the same checks; LAN and other loopback ports stay allowed for administrators.
        Check.True(SourceWebViewBroker.CheckInput(new JsonObject { ["type"] = "navigate", ["url"] = "http://169.254.169.254/" }) is null, "link-local navigate refused");
        Check.True(SourceWebViewBroker.CheckInput(new JsonObject { ["type"] = "navigate", ["url"] = "http://127.0.0.1:4567/v1/health" }) is null, "host port refused");
        Check.True(SourceWebViewBroker.CheckInput(new JsonObject { ["type"] = "navigate", ["url"] = "http://localhost:9000/" }, new Uri("http://127.0.0.1:9000")) is null, "host port of the running host refused");
        Check.True(SourceWebViewBroker.CheckInput(new JsonObject { ["type"] = "navigate", ["url"] = "http://192.168.1.10:8080/" }) is not null, "LAN allowed");
        Check.True(SourceWebViewBroker.CheckInput(new JsonObject { ["type"] = "navigate", ["url"] = "http://127.0.0.1:8080/" }) is not null, "other loopback ports allowed");
        Check.Equal(HttpStatusCode.NotFound, (await app.OpenAsync("{\"source_id\":\"7\"}")).Status);
        Check.True(fake.Opened.IsEmpty, "nothing opened for refused addresses");

        // The source's own website (its base URL, or a subdomain) opens; no url opens the source's base URL.
        var (status, first) = await app.OpenAsync("{\"source_id\":\"42\",\"url\":\"https://cdn.source.example/chapter/1\"}");
        Check.Equal(HttpStatusCode.OK, status);
        Check.Equal("https://cdn.source.example/chapter/1", first["url"]!.GetValue<string>());
        Check.Equal("/static/source-webview.html", first["page"]!.GetValue<string>());
        var (_, byId) = await app.OpenAsync("{\"source_id\":\"42\"}");
        Check.Equal("https://www.source.example/", byId["url"]!.GetValue<string>());
        // One session per administrator: the second replaced the first.
        Check.True(fake.Deleted.Contains(first["session_id"]!.GetValue<string>()), "the earlier session was closed");
        Check.Equal(1, app.Broker.Count);
        // Neither url nor source: an empty page where the administrator types an address.
        var (emptyStatus, empty) = await app.OpenAsync("{}");
        Check.Equal(HttpStatusCode.OK, emptyStatus);
        Check.True(fake.Opened.Last()["url"] is null, "the host opens an empty page");

        // The socket: Origin required, the ticket must match and works once.
        Check.True(await Refused(() => app.ConnectAsync(empty, origin: null)), "no Origin refused");
        Check.True(await Refused(() => app.ConnectAsync(empty, origin: "https://evil.example")), "foreign Origin refused");
        Check.True(await Refused(() => app.ConnectAsync(empty, ticket: "x" + empty["ticket"]!.GetValue<string>())), "wrong ticket refused");
        using (var socket = await app.ConnectAsync(empty))
        {
            Check.Equal(SourceWebViewBroker.SubProtocol, socket.SubProtocol);
            Check.True(await Refused(() => app.ConnectAsync(empty)), "a ticket works once");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        // A plain GET of the socket address is not a WebSocket.
        using var plain = app.Server.CreateClient();
        Check.Equal(HttpStatusCode.BadRequest, (await plain.GetAsync(SourceWebViewEndpoints.SocketPath)).StatusCode);
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(WebSocket socket, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var buffer = new byte[64 * 1024];
        var total = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), deadline.Token);
            total += result.Count;
        } while (!result.EndOfMessage);
        return (result.MessageType, buffer[..total]);
    }

    private static async Task<JsonObject> NextEventAsync(WebSocket socket, string type, List<byte[]>? frames = null)
    {
        for (var i = 0; i < 50; i++)
        {
            var (kind, data) = await ReceiveAsync(socket, TimeSpan.FromSeconds(10));
            if (kind == WebSocketMessageType.Close) throw new InvalidOperationException("closed before " + type);
            if (kind == WebSocketMessageType.Binary) { frames?.Add(data); continue; }
            var value = JsonNode.Parse(data) as JsonObject;
            if (value?["type"]?.GetValue<string>() == type) return value;
        }
        throw new InvalidOperationException("no " + type + " event");
    }

    private static async Task SendAsync(WebSocket socket, string json) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Check.True(condition(), what);
    }

    private static async Task Relay()
    {
        await using var fake = await FakeHost.StartAsync();
        await using var app = await App.StartAsync(fake);
        var (_, opened) = await app.OpenAsync("{\"source_id\":\"42\"}");
        var id = opened["session_id"]!.GetValue<string>();
        using var socket = await app.ConnectAsync(opened);

        // Host events arrive as text, frames as binary, heartbeats are dropped.
        var frames = new List<byte[]>();
        Check.Equal(id, (await NextEventAsync(socket, "ready", frames))["id"]!.GetValue<string>());
        var load = await NextEventAsync(socket, "load", frames);
        Check.Equal(200, load["status"]!.GetValue<int>());
        Check.Equal(1, frames.Count);
        Check.Equal(0xFF, frames[0][0]); Check.Equal(0xD9, frames[0][^1]); Check.Equal(9, frames[0].Length);

        // Checked input is forwarded rebuilt from its known fields; anything else is answered with an error event.
        await SendAsync(socket, "{\"type\":\"click\",\"x\":10.4,\"y\":20,\"button\":0,\"extra\":\"dropped\"}");
        await SendAsync(socket, "{\"type\":\"navigate\",\"url\":\"javascript:alert(1)\"}");
        Check.Equal("error", (await NextEventAsync(socket, "error"))["type"]!.GetValue<string>());
        await SendAsync(socket, "{\"type\":\"evaluate\",\"script\":\"document.cookie\"}");
        await NextEventAsync(socket, "error");
        await SendAsync(socket, "{\"type\":\"navigate\",\"url\":\"https://other.example/login\"}");
        await SendAsync(socket, "{\"type\":\"key\",\"key\":\"Enter\",\"ctrl\":false}");
        await SendAsync(socket, "{\"type\":\"ping\"}");
        await NextEventAsync(socket, "pong");
        await Until(() => fake.Inputs.Count >= 3, "three inputs forwarded");
        var inputs = fake.Inputs.ToArray();
        Check.Equal("{\"type\":\"click\",\"x\":10,\"y\":20,\"button\":0}", inputs[0].ToJsonString());
        Check.Equal("https://other.example/login", inputs[1]["url"]!.GetValue<string>());
        Check.Equal("{\"type\":\"key\",\"key\":\"Enter\"}", inputs[2].ToJsonString());
        Check.Equal(3, inputs.Length);

        // Done: the page closes, the host session is closed (it copies its cookies into the jar then).
        await SendAsync(socket, "{\"type\":\"close\"}");
        var closed = await NextEventAsync(socket, "closed");
        Check.True(closed["reason"]!.GetValue<string>().Length > 0, "close reason");
        await Until(() => fake.Deleted.Contains(id), "host session deleted");
        Check.Equal(0, app.Broker.Count);
    }

    private static async Task IdleClose()
    {
        await using var fake = await FakeHost.StartAsync();
        await using var app = await App.StartAsync(fake, new SourceWebViewOptions { FrameInterval = TimeSpan.FromMilliseconds(10), IdleTimeout = TimeSpan.FromMilliseconds(400) });
        var (_, opened) = await app.OpenAsync("{\"url\":\"https://www.source.example/\"}");
        using var socket = await app.ConnectAsync(opened);
        var closed = await NextEventAsync(socket, "closed");
        Check.True(closed["reason"]!.GetValue<string>().Contains("idle", StringComparison.Ordinal), closed.ToJsonString());
        await Until(() => fake.Deleted.Contains(opened["session_id"]!.GetValue<string>()), "host session deleted after idle");
    }
}
