using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Core.Connections;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources.Runtime;
using AutoSourcarr.Core.Sources.WebView;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests.Sources;

/// <summary>
/// Phase 7 against the real source host jar (AUTOSOURCARR_TEST_SOURCE_HOST_JAR, set by the CI probe) and the local
/// challenge server scripts/fake-cloudflare.py (AUTOSOURCARR_TEST_CHALLENGE_URL, default http://127.0.0.1:8899/; the
/// test starts the script itself on a free port when nothing answers there):
/// 1. the interactive WebView: a session through the app's WebSocket route shows frames, takes a click and a
///    navigation, and the page's cf_clearance lands in the host cookie jar;
/// 2. the external solver fallback: with the built-in solver off, a stub FlareSolverr answers with cookies (and a
///    decoy page body), the host retries with the solver's user agent and gets the real page; the clearance is cached.
/// AUTOSOURCARR_TEST_KCEF_DIR may name an already downloaded Chromium (a host data folder's bin/kcef) to skip the
/// 150 MB download in test 1.
/// </summary>
public static class SourceHostWebViewIntegrationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("source host: interactive webview streams frames, takes a click and a navigation, and the page's cookie lands in the host cookie jar", () =>
        {
            if (HostJar() is not { } jar) return;
            InteractiveWebView(jar).GetAwaiter().GetResult();
        });
        suite.Add("source host: external solver fallback (built-in WebView off) uses only the stub solver's cookies and user agent, then its per-domain cache", () =>
        {
            if (HostJar() is not { } jar) return;
            ExternalSolverFallback(jar).GetAwaiter().GetResult();
        });
    }

    private static string? HostJar()
    {
        var jar = Environment.GetEnvironmentVariable("AUTOSOURCARR_TEST_SOURCE_HOST_JAR");
        if (!string.IsNullOrWhiteSpace(jar) && File.Exists(jar)) return jar;
        Console.WriteLine("NOTE source host jar not set; the source host WebView checks run in the CI probe.");
        if (Environment.GetEnvironmentVariable("AUTOSOURCARR_REQUIRE_SOURCE_HOST") == "1") throw new InvalidOperationException("The source host jar is required here.");
        return null;
    }

    private static string Java() => Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home ? Path.Combine(home, "bin", "java") : "java";

    private static Dictionary<string, string> Environment0(string home)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = home,
        };
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome) environment["JAVA_HOME"] = javaHome;
        if (Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } display) environment["DISPLAY"] = display;
        return environment;
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The challenge server: the probe's, or scripts/fake-cloudflare.py started here.</summary>
    private sealed class Challenge : IDisposable
    {
        private Process? _process;
        public string Url { get; private set; } = "";

        public static async Task<Challenge> StartAsync()
        {
            var challenge = new Challenge { Url = Environment.GetEnvironmentVariable("AUTOSOURCARR_TEST_CHALLENGE_URL") is { Length: > 0 } configured ? configured : "http://127.0.0.1:8899/" };
            if (await challenge.StatsAsync() is not null) return challenge;
            var script = FindScript();
            var port = FreePort();
            challenge._process = Process.Start(new ProcessStartInfo("python3") { ArgumentList = { script, port.ToString(System.Globalization.CultureInfo.InvariantCulture) }, UseShellExecute = false })
                ?? throw new InvalidOperationException("python3 did not start");
            challenge.Url = "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";
            for (var i = 0; i < 50 && await challenge.StatsAsync() is null; i++) await Task.Delay(100);
            Check.True(await challenge.StatsAsync() is not null, "fake challenge server started");
            return challenge;
        }

        private static string FindScript()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "scripts", "fake-cloudflare.py");
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("scripts/fake-cloudflare.py not found above " + AppContext.BaseDirectory);
        }

        public async Task<JsonObject?> StatsAsync()
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            try { return JsonNode.Parse(await client.GetStringAsync(new Uri(new Uri(Url), "/stats"))) as JsonObject; }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return null; }
        }

        public void Dispose()
        {
            if (_process is null) return;
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process.Dispose();
        }
    }

    private static HttpClient HostClient(SourceHostProcess host)
    {
        var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Add("X-Source-Host-Token", host.Token);
        return client;
    }

    private static async Task<JsonObject> HostJson(HttpClient client, HttpMethod method, string path, JsonObject? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Check.True(response.IsSuccessStatusCode, method + " " + path + ": " + (int)response.StatusCode + " " + text);
        return (JsonObject)JsonNode.Parse(text)!;
    }

    private static async Task<List<string>> CookieNames(HttpClient client, string url) =>
        (await HostJson(client, HttpMethod.Get, "/v1/browser/cookies?url=" + Uri.EscapeDataString(url)))["cookies"]!.AsArray()
            .OfType<JsonObject>().Select(row => row["name"]!.GetValue<string>()).ToList();

    private static async Task InteractiveWebView(string jar)
    {
        using var temp = new Sandbox();
        using var challenge = await Challenge.StartAsync();
        var data = Path.Combine(temp.Path, "source-host");
        if (Environment.GetEnvironmentVariable("AUTOSOURCARR_TEST_KCEF_DIR") is { Length: > 0 } kcef && Directory.Exists(kcef))
        {
            Directory.CreateDirectory(Path.Combine(data, "bin"));
            Directory.CreateSymbolicLink(Path.Combine(data, "bin", "kcef"), kcef);
        }
        var environment = Environment0(temp.Path);
        environment["WEBVIEW_ENABLED"] = "true";
        var log = new List<string>();
        await using var host = new SourceHostProcess(Java(), jar, data, FreePort(), environment, line => { lock (log) log.Add(line); });
        try
        {
            await host.StartAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
            using var client = HostClient(host);
            // The WebView downloads its Chromium build on first start: wait for it (up to 6 minutes).
            var state = "";
            for (var deadline = DateTime.UtcNow.AddMinutes(6); DateTime.UtcNow < deadline; await Task.Delay(3000))
            {
                state = (await HostJson(client, HttpMethod.Get, "/v1/health"))["webview"]?.GetValue<string>() ?? "";
                if (state == "ready" || state.StartsWith("failed", StringComparison.Ordinal) || state == "disabled") break;
            }
            Check.Equal("ready", state);
            Check.True(!(await CookieNames(client, challenge.Url)).Contains("cf_clearance"), "no clearance before the session");
            var solvers = await HostJson(client, HttpMethod.Get, "/v1/challenges/solvers");
            Check.True(solvers["builtin"]!["ready"]!.GetValue<bool>(), solvers.ToJsonString());

            // The app side: the real routes and broker in-process, talking to the real host.
            await using var broker = new SourceWebViewBroker(() => new SourceHostEndpoint(new Uri(host.BaseUrl), host.Token));
            var settings = new AutoSourcarrSettings { AdminToken = "integration-admin" };
            var services = new SourceWebViewServices { Broker = broker, SourceBaseUrl = (_, _) => Task.FromResult<string?>(null) };
            using var app = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(collection => collection.AddRouting()).Configure(builder =>
            {
                builder.UseWebSockets();
                builder.UseRouting();
                builder.UseEndpoints(endpoints => endpoints.MapSourceWebView(() => services, () => settings));
            })).Build();
            await app.StartAsync();
            var server = app.GetTestServer();
            using var http = server.CreateClient();
            using var open = new HttpRequestMessage(HttpMethod.Post, SourceWebViewEndpoints.SessionPath)
            { Content = new StringContent(new JsonObject { ["url"] = challenge.Url }.ToJsonString(), Encoding.UTF8, "application/json") };
            open.Headers.Add("X-Admin-Token", "integration-admin");
            using var opened = await http.SendAsync(open);
            var session = (JsonObject)JsonNode.Parse(await opened.Content.ReadAsStringAsync())!;
            Check.True(opened.IsSuccessStatusCode, "open: " + session.ToJsonString());
            var socketClient = server.CreateWebSocketClient();
            socketClient.SubProtocols.Add(SourceWebViewBroker.SubProtocol);
            socketClient.SubProtocols.Add(SourceWebViewBroker.TicketPrefix + session["ticket"]!.GetValue<string>());
            socketClient.ConfigureRequest = request => request.Headers.Origin = "http://localhost";
            using var socket = await socketClient.ConnectAsync(new Uri(server.BaseAddress,
                SourceWebViewEndpoints.SocketPath + "?session=" + session["session_id"]!.GetValue<string>()), CancellationToken.None);

            // One reader collects everything (a cancelled WebSocket receive would abort the socket).
            var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var frames = 0;
            var badFrames = new System.Collections.Concurrent.ConcurrentQueue<int>();
            var reader = Task.Run(async () =>
            {
                var buffer = new byte[8 * 1024 * 1024];
                while (true)
                {
                    var total = 0;
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), CancellationToken.None);
                        total += result.Count;
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        if (!(total > 100 && buffer[0] == 0xFF && buffer[1] == 0xD8)) badFrames.Enqueue(total);
                        Interlocked.Increment(ref frames);
                    }
                    else events.Enqueue(Encoding.UTF8.GetString(buffer, 0, total));
                }
            });
            string Recent() => string.Join(" | ", events.TakeLast(20));
            async Task Send(JsonObject message) =>
                await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

            for (var i = 0; i < 900 && Volatile.Read(ref frames) == 0 && !reader.IsCompleted; i++) await Task.Delay(100);
            Check.True(Volatile.Read(ref frames) > 0, "at least one frame; events: " + Recent());
            Check.True(badFrames.IsEmpty, "frames are JPEG images");
            await Send(new JsonObject { ["type"] = "click", ["x"] = 200, ["y"] = 150, ["button"] = 0 });
            await Send(new JsonObject { ["type"] = "navigate", ["url"] = challenge.Url + "?after=navigation" });

            // The page's challenge script sets cf_clearance and reloads; the host copies it into its cookie jar.
            var cookies = new List<string>();
            for (var deadline = DateTime.UtcNow.AddSeconds(90); DateTime.UtcNow < deadline && !cookies.Contains("cf_clearance"); await Task.Delay(1000))
                cookies = await CookieNames(client, challenge.Url);
            Check.True(cookies.Contains("cf_clearance"), "cf_clearance in the host jar; events: " + Recent());
            Check.True(events.Any(text => text.Contains("\"type\":\"load\"", StringComparison.Ordinal)), "load events relayed: " + Recent());
            for (var i = 0; i < 100 && !events.Any(text => text.Contains("after=navigation", StringComparison.Ordinal)); i++) await Task.Delay(100);
            Check.True(events.Any(text => text.Contains("after=navigation", StringComparison.Ordinal)), "the navigation reached the page: " + Recent());

            await Send(new JsonObject { ["type"] = "close" });
            Check.True(await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(20))) == reader, "the app closed the WebSocket");
            Check.True(events.Any(text => text.Contains("\"type\":\"closed\"", StringComparison.Ordinal)), "closed event: " + Recent());
            for (var i = 0; i < 50 && (await HostJson(client, HttpMethod.Get, "/v1/challenges/solvers"))["interactive_sessions"]!.GetValue<int>() > 0; i++) await Task.Delay(100);
            Check.Equal(0, (await HostJson(client, HttpMethod.Get, "/v1/challenges/solvers"))["interactive_sessions"]!.GetValue<int>());
            Check.True((await CookieNames(client, challenge.Url)).Contains("cf_clearance"), "the cookie stays after the session");
            await app.StopAsync();
        }
        catch
        {
            lock (log) Console.WriteLine(string.Join('\n', log.TakeLast(80)));
            throw;
        }
    }

    /// <summary>Same token as fake-cloudflare.py: the first 32 hex digits of SHA-256("autosourcarr-test|" + agent).</summary>
    private static string ClearanceFor(string agent) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("autosourcarr-test|" + agent)))[..32];

    private static async Task ExternalSolverFallback(string jar)
    {
        const string agent = "AutoSourcarrStubSolver/1.0 (integration test)";
        using var temp = new Sandbox();
        using var challenge = await Challenge.StartAsync();
        var solverPort = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:" + solverPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/");
        listener.Start();
        var requests = new List<JsonObject>();
        using var stopSolver = new CancellationTokenSource();
        var solver = Task.Run(async () =>
        {
            while (!stopSolver.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException) { return; }
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject ?? [];
                lock (requests) requests.Add(body);
                var url = body["url"]?.GetValue<string>() ?? "";
                var answer = context.Request.Url!.AbsolutePath == "/v1" && body["cmd"]?.GetValue<string>() == "request.get"
                    ? new JsonObject
                    {
                        ["status"] = "ok", ["message"] = "Challenge solved!", ["version"] = "3.3.21", ["startTimestamp"] = 0, ["endTimestamp"] = 1,
                        ["solution"] = new JsonObject
                        {
                            ["url"] = url, ["status"] = 200, ["userAgent"] = agent, ["headers"] = new JsonObject(),
                            // A decoy: the host must never use the solver's page as content.
                            ["response"] = "<html><head><title>Just a moment...</title></head><body>decoy from the solver</body></html>",
                            ["cookies"] = new JsonArray(new JsonObject
                            {
                                ["name"] = "cf_clearance", ["value"] = ClearanceFor(agent), ["domain"] = new Uri(url).Host, ["path"] = "/",
                                ["expires"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), ["httpOnly"] = true, ["secure"] = false,
                            }),
                        },
                    }
                    : new JsonObject { ["status"] = "error", ["message"] = "unexpected request" };
                var bytes = Encoding.UTF8.GetBytes(answer.ToJsonString());
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        });

        var environment = Environment0(temp.Path);
        environment["WEBVIEW_ENABLED"] = "false";
        environment["AUTOSOURCARR_DISABLE_BUILTIN_SOLVER"] = "1";
        // What the app passes: the enabled solver connections (a disabled one is left out).
        environment["AUTOSOURCARR_SOLVERS"] = SolverConnectionType.HostEnvironment(
        [
            new ConnectionConfig("stub", "flaresolverr", "Stub", true, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SolverConnectionType.UrlField] = "http://127.0.0.1:" + solverPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [SolverConnectionType.TimeoutField] = "20",
            }),
            new ConnectionConfig("off", "byparr", "Off", false, new Dictionary<string, string>(StringComparer.Ordinal) { [SolverConnectionType.UrlField] = "http://127.0.0.1:9" }),
        ]);
        var log = new List<string>();
        await using var host = new SourceHostProcess(Java(), jar, Path.Combine(temp.Path, "source-host"), FreePort(), environment, line => { lock (log) log.Add(line); });
        try
        {
            await host.StartAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
            using var client = HostClient(host);
            var solvers = await HostJson(client, HttpMethod.Get, "/v1/challenges/solvers");
            Check.True(!solvers["builtin"]!["ready"]!.GetValue<bool>(), "built-in solver off: " + solvers.ToJsonString());
            var external = solvers["external"]!.AsArray();
            Check.Equal(1, external.Count);
            Check.Equal(20_000, external[0]!["timeout_ms"]!.GetValue<int>());

            var before = (await challenge.StatsAsync())!;
            var first = await HostJson(client, HttpMethod.Post, "/v1/webview/fetch", new JsonObject { ["url"] = challenge.Url });
            Check.True(first["status"]!.GetValue<int>() == 200 && !first["challenge_page"]!.GetValue<bool>() && first["clearance"]!.GetValue<bool>(),
                "first fetch through the solver: " + first.ToJsonString());
            lock (requests)
            {
                Check.Equal(1, requests.Count);
                Check.Equal("request.get", requests[0]["cmd"]!.GetValue<string>());
                Check.Equal(20_000, requests[0]["maxTimeout"]!.GetValue<int>());
            }
            // Cached per domain: the next request uses the solver's user agent and cookie without asking it again.
            var second = await HostJson(client, HttpMethod.Post, "/v1/webview/fetch", new JsonObject { ["url"] = challenge.Url + "?again=1" });
            Check.Equal(200, second["status"]!.GetValue<int>());
            lock (requests) Check.Equal(1, requests.Count);
            var after = (await challenge.StatsAsync())!;
            // Both pages came from the website itself (cookies only; the solver's decoy page was never used).
            Check.True(after["passed"]!.GetValue<int>() - before["passed"]!.GetValue<int>() >= 2, "website served both pages: " + after.ToJsonString());
            var cached = (await HostJson(client, HttpMethod.Get, "/v1/challenges/solvers"))["cached"]!.AsArray();
            Check.True(cached.OfType<JsonObject>().Any(row => row["host"]!.GetValue<string>() == new Uri(challenge.Url).Host), "cached clearance: " + cached.ToJsonString());
        }
        catch
        {
            lock (log) Console.WriteLine(string.Join('\n', log.TakeLast(80)));
            throw;
        }
        finally
        {
            await stopSolver.CancelAsync();
            listener.Stop();
            await solver.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }
}
