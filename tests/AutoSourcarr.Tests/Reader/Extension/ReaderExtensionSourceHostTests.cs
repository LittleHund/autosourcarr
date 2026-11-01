using System.Net;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Reader.Extension;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoSourcarr.Tests.ReaderExtension;

/// <summary>
/// Phase 10 done-when (no Mihon in CI): the prebuilt reader extension JAR (scripts/build-reader-extension.sh, the same
/// build as the release APK) runs in the AutoSourcarr source host; server slot 1 and slot 2 point at two AutoSourcarr
/// servers (real reader-key middleware and reader routes on Kestrel, each with its own key and tiny catalogue), and each
/// slot reads only its own server. A slot with a wrong key gets AutoSourcarr's 401 as an error and no entries.
/// Runs when AUTOSOURCARR_TEST_SOURCE_HOST_JAR and AUTOSOURCARR_TEST_READER_EXTENSION_JAR are set (the CI probe).
/// </summary>
public static class ReaderExtensionSourceHostTests
{
    public static void Register(TestSuite suite) =>
        suite.Add("source host: reader extension reads two servers with separate device keys", () =>
        {
            var host = Environment.GetEnvironmentVariable("AUTOSOURCARR_TEST_SOURCE_HOST_JAR");
            var extension = Environment.GetEnvironmentVariable("AUTOSOURCARR_TEST_READER_EXTENSION_JAR");
            if (string.IsNullOrWhiteSpace(host) || !File.Exists(host) || string.IsNullOrWhiteSpace(extension) || !File.Exists(extension))
            {
                Console.WriteLine("NOTE reader extension check skipped: set AUTOSOURCARR_TEST_SOURCE_HOST_JAR (scripts/build-source-host.sh) and "
                    + "AUTOSOURCARR_TEST_READER_EXTENSION_JAR (scripts/build-reader-extension.sh, jar/*.jar); the CI probe sets both.");
                if (Environment.GetEnvironmentVariable("AUTOSOURCARR_REQUIRE_SOURCE_HOST") == "1" && !string.IsNullOrWhiteSpace(extension))
                    throw new InvalidOperationException("The source host and reader extension jars are required here.");
                return;
            }
            Run(host, extension).GetAwaiter().GetResult();
        });

    private static async Task Run(string hostJar, string extensionJar)
    {
        using var temp = new Sandbox();
        await using var alpha = await TestServer.StartAsync("alpha-device-key-0123456789", ["Alpha Saga", "Alpha Chronicle"]);
        await using var beta = await TestServer.StartAsync("beta-device-key-9876543210", ["Beta Story"]);

        // The prebuilt JAR, recorded the way the host records an installed extension (class from the Keiyoushi manifest).
        var data = Path.Combine(temp.Path, "source-host");
        var extensions = Path.Combine(data, "extensions");
        Directory.CreateDirectory(extensions);
        var jarName = ReaderExtensionRepository.Package + "-v1.4.test.jar";
        File.Copy(extensionJar, Path.Combine(extensions, jarName));
        File.WriteAllText(Path.Combine(extensions, "installed.json"), new JsonArray(new JsonObject
        {
            ["pkg"] = ReaderExtensionRepository.Package, ["name"] = "AutoSourcarr", ["versionName"] = "1.4.test", ["versionCode"] = 1,
            ["libVersion"] = "1.4", ["className"] = "keiyoushi.source.Generated", ["jar"] = jarName, ["lang"] = "all", ["contentWarning"] = 1
        }).ToJsonString());

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = temp.Path,
            ["EXTENSION_STORES"] = "[]",
            ["WEBVIEW_ENABLED"] = "false"
        };
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome) environment["JAVA_HOME"] = javaHome;
        var java = Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home ? Path.Combine(home, "bin", "java") : "java";
        var log = new List<string>();
        await using var process = new SourceHostProcess(java, hostJar, data, FreePort(), environment, line => { lock (log) log.Add(line); });
        try
        {
            await process.StartAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
            await using var client = SuwayomiClient.ForSourceHost(new AutoSourcarrSettings(), process.BaseUrl, process.Token);
            var installed = await client.HostExtensionAsync(ReaderExtensionRepository.Package);
            Check.Equal("", SourceResponseContract.Text(installed?["loadError"]));
            var sources = await client.SourcesAsync();
            string Slot(string name) => sources.First(row => SourceResponseContract.Text(row["name"]) == name)["id"]!.ToString();
            var slot1 = Slot("AutoSourcarr"); var slot2 = Slot("AutoSourcarr (2)"); var slot3 = Slot("AutoSourcarr (3)");
            Check.Equal(ReaderExtensionRepository.DefaultSlots, sources.Count(row => SourceResponseContract.Text(row["name"]).StartsWith("AutoSourcarr", StringComparison.Ordinal)));
            Check.True(new[] { slot1, slot2, slot3 }.Distinct().Count() == 3, "every slot has its own source id");

            // Unconfigured: the slot says what to set instead of reading anything.
            var unset = await Failure(client, slot1);
            Check.True(unset.Contains("address", StringComparison.OrdinalIgnoreCase), "unconfigured slot: " + unset);

            await Configure(client, slot1, alpha.Address, alpha.Key);
            await Configure(client, slot2, beta.Address, beta.Key);
            Check.Equal("Alpha Chronicle|Alpha Saga", string.Join('|', (await Titles(client, slot1)).Order(StringComparer.Ordinal)));
            Check.Equal("Beta Story", string.Join('|', await Titles(client, slot2)));
            Check.Equal(0, alpha.KeyedReads(beta.Key));   // beta's key never reached alpha
            Check.Equal(0, beta.KeyedReads(alpha.Key));
            Check.True(alpha.KeyedReads(alpha.Key) > 0 && beta.KeyedReads(beta.Key) > 0, "each server was read with its own key");

            // A wrong key: AutoSourcarr's 401 surfaces as an error; no entries.
            await Configure(client, slot3, alpha.Address, beta.Key);
            var refused = await Failure(client, slot3);
            Check.True(refused.Contains("401", StringComparison.Ordinal), "wrong key: " + refused);
            Check.True(alpha.Rejected > 0, "alpha rejected the wrong key");

            // Changing a slot's server applies without restarting: slot 2 now reads alpha.
            await Configure(client, slot2, alpha.Address, alpha.Key);
            Check.Equal("Alpha Chronicle|Alpha Saga", string.Join('|', (await Titles(client, slot2)).Order(StringComparer.Ordinal)));
        }
        catch
        {
            lock (log) Console.WriteLine(string.Join('\n', log.TakeLast(80)));
            throw;
        }
    }

    /// <summary>Slot preferences by position (0: address, 1: reader key) through the host's preference API.</summary>
    private static async Task Configure(SuwayomiClient client, string sourceId, string address, string key)
    {
        var path = "/v1/sources/" + Uri.EscapeDataString(sourceId) + "/preferences";
        var listed = (await client.HostAsync(HttpMethod.Get, path))!.AsArray();
        Check.True(listed.Count >= 2, "address and key preferences");
        await client.HostAsync(HttpMethod.Put, path, new JsonObject { ["position"] = 0, ["value"] = address });
        await client.HostAsync(HttpMethod.Put, path, new JsonObject { ["position"] = 1, ["value"] = key });
        // A failed browse earlier must not leave the source paused.
        await client.HostAsync(HttpMethod.Delete, "/v1/sources/" + Uri.EscapeDataString(sourceId) + "/backoff");
    }

    private static async Task<List<string>> Titles(SuwayomiClient client, string sourceId)
    {
        var page = await client.HostAsync(HttpMethod.Get, "/v1/sources/" + Uri.EscapeDataString(sourceId) + "/mangas?type=POPULAR&page=1") as JsonObject;
        return ((page?["mangas"] as JsonArray) ?? []).OfType<JsonObject>().Select(row => SourceResponseContract.Text(row["title"])).ToList();
    }

    private static async Task<string> Failure(SuwayomiClient client, string sourceId)
    {
        try
        {
            var titles = await Titles(client, sourceId);
            throw new InvalidOperationException("Expected an error, got " + titles.Count + " entries");
        }
        catch (SuwayomiException error) { return error.Message; }
        finally { await client.HostAsync(HttpMethod.Delete, "/v1/sources/" + Uri.EscapeDataString(sourceId) + "/backoff"); }
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>An AutoSourcarr reader server on 127.0.0.1: the real HTTP pipeline (reader key check, /api/reader/v1 alias)
    /// and the real reader routes over a tiny catalogue.</summary>
    private sealed class TestServer : IAsyncDisposable
    {
        private readonly IHost _host; private readonly WholeTaskCoordinator _lanes; private readonly List<string> _keys = [];
        public string Address { get; private set; } = "";
        public string Key { get; }
        public int Rejected;

        private TestServer(string key, IReadOnlyList<string> titles)
        {
            Key = key;
            var settings = new AutoSourcarrSettings { AutosourcarrReaderApiToken = key };
            var tasks = new TaskRegistry(); _lanes = new(settings, tasks);
            var catalogue = new TinyCatalogue(titles);
            _host = new HostBuilder().ConfigureLogging(logging => logging.ClearProviders()).ConfigureWebHost(web => web
                .UseKestrel(options => options.Listen(IPAddress.Loopback, 0))
                .ConfigureServices(services => services.AddRouting())
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        lock (_keys) _keys.Add(context.Request.Headers["X-AutoSourcarr-API-Key"].ToString());
                        await next(context);
                        if (context.Response.StatusCode == 401) Interlocked.Increment(ref Rejected);
                    });
                    app.UseAutoSourcarrHttp("reader-extension-fixture", () => settings, (_, _) => "unexpected-reader-extension", new RequestAdmissionGate(), _lanes, tasks, new SupportPrivacy(), _ => { });
                    app.UseEndpoints(endpoints => endpoints.MapReaderCatalogue(catalogue));
                })).Build();
        }

        public static async Task<TestServer> StartAsync(string key, IReadOnlyList<string> titles)
        {
            var server = new TestServer(key, titles);
            await server._host.StartAsync();
            server.Address = server._host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
            return server;
        }

        public int KeyedReads(string key) { lock (_keys) return _keys.Count(item => item == key); }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
            await _lanes.DisposeAsync();
        }
    }

    private sealed class TinyCatalogue(IReadOnlyList<string> titles) : IReaderCatalogue
    {
        public Task<JsonObject> SourcesAsync(CancellationToken cancellationToken) => Task.FromResult(new JsonObject { ["items"] = new JsonArray() });
        public Task<JsonObject> EntryAsync(long entryId, CancellationToken cancellationToken) => throw new KeyNotFoundException();
        public Task<JsonObject> RefreshAsync(long entryId, bool fetchDetails, bool fetchChapters, CancellationToken cancellationToken) => throw new KeyNotFoundException();
        public Task<JsonObject> BrowseAsync(ReaderBrowseRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new JsonObject
            {
                ["items"] = new JsonArray(titles.Select((title, index) => (JsonNode)new JsonObject
                {
                    ["id"] = index + 1, ["preferred_title"] = title, ["metadata_complete"] = true
                }).ToArray()),
                ["has_next_page"] = false
            });
    }
}
