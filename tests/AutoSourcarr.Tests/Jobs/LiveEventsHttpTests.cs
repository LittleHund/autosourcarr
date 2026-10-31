using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoSourcarr.Tests.Jobs;

/// <summary>Phase 3 HTTP: /api/v1 tasks, jobs, errors and the live event stream, directly and through nginx.</summary>
public static class LiveEventsHttpTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("api v1 jobs: task list, cancel, clear history, job queue, errors and clear are admin-only /api/v1 routes", () =>
        {
            using var temp = new Sandbox();
            using var app = App.Start(temp.Path).GetAwaiter().GetResult();
            using var client = app.Client();
            using (var anonymous = new HttpClient { BaseAddress = new Uri(app.Address) })
                Check.Equal(HttpStatusCode.Unauthorized, anonymous.GetAsync("/api/v1/tasks").GetAwaiter().GetResult().StatusCode);
            var gate = new TaskCompletionSource();
            var type = new JobType("demo", "Demo work");
            var running = app.Jobs.Enqueue(type, "a", job => gate.Task.WaitAsync(job.CancellationToken));
            var queued = app.Jobs.Enqueue(type, "b", _ => Task.CompletedTask);
            JobQueueTests.Wait(() => app.Tasks.Find(running.Id)!.Activity == "running");
            var tasks = Json(client.GetStringAsync("/api/v1/tasks").GetAwaiter().GetResult());
            var row = tasks["items"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == queued.Id)!;
            Check.Equal("queued", row["activity"]!.GetValue<string>());
            Check.Equal("demo", row["job_type"]!.GetValue<string>());
            Check.Equal(3, tasks["jobs"]!["workers"]!.GetValue<int>());
            var jobs = Json(client.GetStringAsync("/api/v1/jobs").GetAwaiter().GetResult());
            Check.Equal(1, jobs["running"]!.AsArray().Count);
            var cancel = client.PostAsync($"/api/v1/tasks/{running.Id}/cancel", null).GetAwaiter().GetResult();
            Check.Equal(HttpStatusCode.OK, cancel.StatusCode);
            Check.Equal(JobOutcome.Cancelled, running.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Check.Equal(HttpStatusCode.Conflict, client.PostAsync($"/api/v1/tasks/{running.Id}/cancel", null).GetAwaiter().GetResult().StatusCode);
            Check.Equal(HttpStatusCode.Conflict, client.PostAsync($"/api/v1/tasks/{running.Id}/retry-safe", null).GetAwaiter().GetResult().StatusCode);
            queued.Completion.Wait(TimeSpan.FromSeconds(5));
            var cleared = Json(client.PostAsync("/api/v1/tasks/clear-history", null).GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult());
            Check.Equal(2, cleared["cleared"]!.GetValue<int>());
            app.Errors.Record("Jobs", "Demo work failed: broken", key: "job-failed:demo");
            app.Errors.Resolve("job-failed:demo");
            var errors = Json(client.GetStringAsync("/api/v1/errors").GetAwaiter().GetResult());
            Check.Equal(1, errors["items"]!.AsArray().Count);
            var clearedErrors = client.PostAsync("/api/v1/errors/clear", new StringContent("""{"resolved_only":true}""", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check.Equal(1, Json(clearedErrors.Content.ReadAsStringAsync().GetAwaiter().GetResult())["cleared"]!.GetValue<int>());
            gate.SetResult();
        });

        suite.Add("api v1 jobs: an interrupted task is replayed only by a registered safe replay of its kind", () =>
        {
            using var temp = new Sandbox();
            var replays = 0;
            using var app = App.Start(temp.Path, safe: new Dictionary<string, Func<TaskRecord, CancellationToken, Task<JsonObject>>>(StringComparer.Ordinal)
            {
                ["library-monitor"] = (row, _) => { replays++; return Task.FromResult(new JsonObject { ["observed"] = row.Context }); },
            }).GetAwaiter().GetResult();
            using var client = app.Client();
            app.Tasks.Restore([
                new TaskRecord { Id = "aaaaaaaaaaaa", Name = "Observe", Kind = "library-monitor", Context = "17", CreatedAt = "2026-10-31T00:00:00Z" },
                new TaskRecord { Id = "bbbbbbbbbbbb", Name = "Merge", Kind = "merge", Context = "18", CreatedAt = "2026-10-31T00:00:00Z" }]);
            var ok = client.PostAsync("/api/v1/tasks/aaaaaaaaaaaa/retry-safe", null).GetAwaiter().GetResult();
            Check.Equal(HttpStatusCode.OK, ok.StatusCode);
            Check.Equal("17", Json(ok.Content.ReadAsStringAsync().GetAwaiter().GetResult())["observed"]!.GetValue<string>());
            Check.Equal(HttpStatusCode.Conflict, client.PostAsync("/api/v1/tasks/bbbbbbbbbbbb/retry-safe", null).GetAwaiter().GetResult().StatusCode);
            Check.Equal(1, replays);
        });

        suite.Add("live events: text/event-stream with ids, no proxy buffering, heartbeat and Last-Event-ID resume", () =>
        {
            using var temp = new Sandbox();
            using var app = App.Start(temp.Path, heartbeat: TimeSpan.FromMilliseconds(200)).GetAwaiter().GetResult();
            using var client = app.Client();
            using (var reader = Sse.Open(client, "/api/v1/events"))
            {
                Check.Equal("text/event-stream; charset=utf-8", reader.Response.Content.Headers.ContentType!.ToString());
                Check.Equal("no", string.Join(",", reader.Response.Headers.GetValues("X-Accel-Buffering")));
                Check.True(reader.Response.Headers.CacheControl!.NoCache);
                Check.True(reader.ReadUntil(line => line.StartsWith(": heartbeat", StringComparison.Ordinal), TimeSpan.FromSeconds(3)) is not null, "A heartbeat is sent while idle.");
                app.Events.Publish("tasks", new JsonObject { ["change"] = "updated" });
                var item = reader.NextEvent(TimeSpan.FromSeconds(3));
                Check.Equal("tasks", item.Event);
                Check.Equal("updated", item.Data["data"]!["change"]!.GetValue<string>());
            }
            var last = app.Events.LastId;
            app.Events.Publish("jobs", new JsonObject { ["change"] = "queued" });
            app.Events.Publish("errors", new JsonObject { ["change"] = "updated" });
            using (var resumed = Sse.Open(client, "/api/v1/events", last))
            {
                Check.Equal("jobs", resumed.NextEvent(TimeSpan.FromSeconds(3)).Event);
                Check.Equal("errors", resumed.NextEvent(TimeSpan.FromSeconds(3)).Event);
            }
            using (var filtered = Sse.Open(client, "/api/v1/events?topics=errors", last))
                Check.Equal("errors", filtered.NextEvent(TimeSpan.FromSeconds(3)).Event);
            // The reader alias (no admin auth behind Authelia) never serves the admin event stream.
            using var reader404 = new HttpClient { BaseAddress = new Uri(app.Address) };
            reader404.DefaultRequestHeaders.Add("X-AutoSourcarr-Api-Key", "reader-key");
            Check.Equal(HttpStatusCode.NotFound, reader404.GetAsync("/api/reader/v1/events").GetAwaiter().GetResult().StatusCode);
        });

        suite.Add("live events: job changes arrive through nginx at once (X-Accel-Buffering: no)", () =>
        {
            var nginx = Nginx.Find();
            if (nginx is null)
            {
                // Recorded in the test log; CI installs nginx so this runs there.
                Console.WriteLine("NOTE nginx is not installed here; the reverse-proxy check ran without nginx.");
                if (Environment.GetEnvironmentVariable("AUTOSOURCARR_REQUIRE_NGINX") == "1") throw new InvalidOperationException("nginx is required in CI.");
                return;
            }
            using var temp = new Sandbox();
            using var app = App.Start(temp.Path, heartbeat: TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            using var proxy = Nginx.Start(nginx, temp.Path, new Uri(app.Address).Port);
            using var client = new HttpClient { BaseAddress = new Uri(proxy.Address), Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
            using var reader = Sse.Open(client, "/api/v1/events");
            Check.True(reader.ReadUntil(line => line.StartsWith(": connected", StringComparison.Ordinal), TimeSpan.FromSeconds(5)) is not null, "The opening comment passed nginx.");
            var watch = Stopwatch.StartNew();
            var handle = app.Jobs.Enqueue(new JobType("proxy", "Proxy check"), "k", _ => Task.CompletedTask);
            var item = reader.NextEvent(TimeSpan.FromSeconds(5));
            Check.Equal("jobs", item.Event);
            Check.Equal(handle.Id, item.Data["data"]!["id"]!.GetValue<string>());
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(2), "The event arrived through nginx in " + watch.ElapsedMilliseconds + " ms.");
        });
    }

    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    private sealed class App : IDisposable
    {
        private IHost? _host;
        public string Address { get; private set; } = "";
        public TaskRegistry Tasks { get; } = new();
        public EventStream Events { get; } = new();
        public ErrorRegistry Errors { get; } = new(RuntimeIdentity.Create("fixture"), new SupportPrivacy());
        public JobQueue Jobs { get; private set; } = null!;

        public static async Task<App> Start(string root, TimeSpan? heartbeat = null,
            IReadOnlyDictionary<string, Func<TaskRecord, CancellationToken, Task<JsonObject>>>? safe = null)
        {
            var app = new App();
            app.Jobs = new JobQueue(app.Tasks, app.Events, app.Errors);
            var settings = new AutoSourcarrSettings { AdminToken = "admin", AutosourcarrReaderApiToken = "reader-key" };
            var services = new ApiV1Services(() => settings, root, null)
            {
                Tasks = app.Tasks, Jobs = app.Jobs, Events = app.Events, Errors = app.Errors,
                EventHeartbeat = heartbeat ?? TimeSpan.FromSeconds(15),
                SafeRetries = safe ?? new Dictionary<string, Func<TaskRecord, CancellationToken, Task<JsonObject>>>(StringComparer.Ordinal),
            };
            app._host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).ConfigureWebHost(web => web
                .UseKestrel(options => options.Listen(IPAddress.Loopback, 0))
                .ConfigureServices(collection => collection.AddRouting())
                .Configure(builder =>
                {
                    builder.UseMiddleware<ReaderAuthorizationMiddleware>((Func<AutoSourcarrSettings>)(() => settings));
                    builder.UseRouting();
                    builder.UseEndpoints(endpoints => endpoints.MapApiV1(services));
                })).Build();
            await app._host.StartAsync();
            app.Address = app._host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return app;
        }

        public HttpClient Client()
        {
            var client = new HttpClient { BaseAddress = new Uri(Address), Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
            return client;
        }

        public void Dispose()
        {
            Jobs.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
            _host?.StopAsync().Wait(TimeSpan.FromSeconds(10));
            _host?.Dispose();
        }
    }

    private sealed class Sse : IDisposable
    {
        private readonly StreamReader _reader;
        private readonly CancellationTokenSource _cancel = new();
        private Sse(HttpResponseMessage response, StreamReader reader) { Response = response; _reader = reader; }
        public HttpResponseMessage Response { get; }

        public static Sse Open(HttpClient client, string path, long? lastId = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (lastId is { } id) request.Headers.Add("Last-Event-ID", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            Check.Equal(HttpStatusCode.OK, response.StatusCode);
            return new Sse(response, new StreamReader(response.Content.ReadAsStreamAsync().GetAwaiter().GetResult(), Encoding.UTF8));
        }

        public string? ReadUntil(Func<string, bool> match, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var line = ReadLine(deadline - DateTime.UtcNow);
                if (line is null) return null;
                if (match(line)) return line;
            }
            return null;
        }

        public (string Event, JsonObject Data) NextEvent(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            string? name = null; string? data = null;
            while (DateTime.UtcNow < deadline)
            {
                var line = ReadLine(deadline - DateTime.UtcNow) ?? throw new TimeoutException("No event arrived in time.");
                if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
                else if (line.Length == 0 && name is not null && data is not null) return (name, JsonNode.Parse(data)!.AsObject());
            }
            throw new TimeoutException("No event arrived in time.");
        }

        private string? ReadLine(TimeSpan timeout)
        {
            var read = _reader.ReadLineAsync(_cancel.Token).AsTask();
            return read.Wait(timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : timeout) ? read.Result : throw new TimeoutException("No data arrived in time.");
        }

        public void Dispose() { _cancel.Cancel(); _reader.Dispose(); Response.Dispose(); _cancel.Dispose(); }
    }

    private sealed class Nginx : IDisposable
    {
        private readonly Process _process;
        private Nginx(Process process, string address) { _process = process; Address = address; }
        public string Address { get; }

        public static string? Find()
        {
            foreach (var candidate in new[] { "/usr/sbin/nginx", "/usr/bin/nginx", "/usr/local/sbin/nginx", "/usr/local/bin/nginx" })
                if (File.Exists(candidate)) return candidate;
            return null;
        }

        public static Nginx Start(string binary, string root, int upstreamPort)
        {
            var prefix = Path.Combine(root, "nginx");
            Directory.CreateDirectory(Path.Combine(prefix, "logs"));
            var port = FreePort();
            // Default proxy settings on purpose (proxy_buffering on): only the app's X-Accel-Buffering header
            // makes nginx pass each event through at once, which is exactly what the app relies on.
            File.WriteAllText(Path.Combine(prefix, "nginx.conf"), $$"""
                worker_processes 1;
                pid {{prefix}}/nginx.pid;
                error_log {{prefix}}/logs/error.log warn;
                events { worker_connections 64; }
                http {
                    access_log off;
                    client_body_temp_path {{prefix}}/body;
                    proxy_temp_path {{prefix}}/proxy;
                    fastcgi_temp_path {{prefix}}/fastcgi;
                    uwsgi_temp_path {{prefix}}/uwsgi;
                    scgi_temp_path {{prefix}}/scgi;
                    server {
                        listen 127.0.0.1:{{port}};
                        location / {
                            proxy_pass http://127.0.0.1:{{upstreamPort}};
                            proxy_http_version 1.1;
                            proxy_set_header Connection "";
                            proxy_read_timeout 120s;
                        }
                    }
                }
                """);
            var start = new ProcessStartInfo(binary) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in new[] { "-p", prefix, "-c", Path.Combine(prefix, "nginx.conf"), "-g", "daemon off;" }) start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new InvalidOperationException("nginx did not start.");
            var proxy = new Nginx(process, $"http://127.0.0.1:{port}");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try { using var probe = new TcpClient(); probe.Connect(IPAddress.Loopback, port); return proxy; }
                catch (SocketException) when (DateTime.UtcNow < deadline && !process.HasExited) { Thread.Sleep(50); }
                catch (SocketException)
                {
                    var log = Path.Combine(prefix, "logs", "error.log");
                    throw new InvalidOperationException("nginx did not listen: " + process.StandardError.ReadToEnd() + (File.Exists(log) ? File.ReadAllText(log) : ""));
                }
            }
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public void Dispose()
        {
            try { if (!_process.HasExited) { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); } }
            finally { _process.Dispose(); }
        }
    }
}
