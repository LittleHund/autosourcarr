using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Inventory;

namespace AutoSourcarr.Tests.Sources;

/// <summary>REP-1: installed-source observations (raws routes, original-APK evidence) read from the source host API.</summary>
public static class HostRawInstalledSourcesTests
{
    private const string Package = "eu.kanade.tachiyomi.extension.en.hostfixture";
    private const string Other = "eu.kanade.tachiyomi.extension.en.notinstalled";
    private const string Version = "1.4.23";

    public static void Register(TestSuite suite)
    {
        suite.Add("host raw sources: inventory lists installed rows only, in the engine inventory shape, annotated", () =>
        {
            using var setup = new Setup();
            var rows = setup.Installed.InstalledAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal(1, rows.Count);
            var row = rows[0];
            Check.Equal(Package, Text(row, "pkgName"));
            Check.Equal(Version, Text(row, "versionName"));
            Check.True(!row.ContainsKey("loadError") && !row.ContainsKey("versionCodeLong"), "Host-only fields are not part of the inventory row.");
            var sources = row["source"]!["nodes"]!.AsArray();
            Check.Equal(1, sources.Count);
            Check.Equal("-1234", Text(sources[0]!.AsObject(), "id"));
            Check.True(!sources[0]!.AsObject().ContainsKey("isConfigurable"), "Source nodes keep id, name, lang and baseUrl only.");
            // No repository URLs on an installed row: the local annotation marks it as a manual upload.
            Check.Equal(true, row["is_orphaned"]!.GetValue<bool>());
            Check.Equal("manual-upload", Text(row, "orphan_reason"));
            Check.True(setup.Installed.Ready, "A ready host is ready.");
        });

        suite.Add("host raw sources: observe reads the jar twice and answers its digest, identity and source ids", () =>
        {
            using var setup = new Setup();
            var observed = setup.Installed.ObserveAsync(Package, Version, CancellationToken.None).GetAwaiter().GetResult();
            Check.True(observed is not null, "The installed version is observed.");
            Check.Equal(Convert.ToHexStringLower(SHA256.HashData(setup.Jar)), observed!.BinarySha256);
            Check.Equal(InstalledSourceIdentity.Hash(observed.Extension), observed.IdentitySha256);
            Check.Equal("-1234", observed.SourceIds.Single());
            Check.Equal(2, setup.JarReads);
            Check.Equal(2, setup.ListReads);

            var capture = setup.Installed.CaptureAsync(Package, Version).GetAwaiter().GetResult()!;
            Check.True(capture.Contents().AsSpan().SequenceEqual(setup.Jar), "The capture keeps the exact jar bytes.");
            Check.Equal(Package + "-v" + Version + ".jar", capture.Filename);
            setup.Installed.RequireCurrent(capture);

            Check.True(setup.Installed.ObserveAsync(Package, "9.9.9", CancellationToken.None).GetAwaiter().GetResult() is null, "Another version is not observed.");
            Check.True(setup.Installed.ObserveAsync(Other, Version, CancellationToken.None).GetAwaiter().GetResult() is null, "A package that is not installed is not observed.");
        });

        suite.Add("host raw sources: a jar or identity change during observation is a conflict", () =>
        {
            using (var setup = new Setup())
            {
                setup.ChangeJarOnSecondRead = true;
                Check.Throws<SourceRawConflictException>(() => setup.Installed.ObserveAsync(Package, Version, CancellationToken.None).GetAwaiter().GetResult());
            }
            using (var setup = new Setup())
            {
                setup.ChangeSourcesOnSecondList = true;
                Check.Throws<SourceRawConflictException>(() => setup.Installed.ObserveAsync(Package, Version, CancellationToken.None).GetAwaiter().GetResult());
            }
            using (var setup = new Setup())
            {
                setup.JarStatus = HttpStatusCode.NotFound;
                Check.Throws<SourceRawConflictException>(() => setup.Installed.ObserveAsync(Package, Version, CancellationToken.None).GetAwaiter().GetResult());
            }
        });

        suite.Add("host raw sources: a restart invalidates a capture; an unready host is unavailable", () =>
        {
            using var setup = new Setup();
            var capture = setup.Installed.CaptureAsync(Package, Version).GetAwaiter().GetResult()!;
            setup.Generation = 2;
            Check.Throws<SourceRawConflictException>(() => setup.Installed.RequireCurrent(capture));
            setup.Ready = false;
            Check.True(!setup.Installed.Ready, "An unready host is not ready.");
            var reads = setup.ListReads;
            Check.Throws<SourceEngineUnavailableException>(() => setup.Installed.InstalledAsync(CancellationToken.None).GetAwaiter().GetResult());
            Check.Throws<SourceEngineUnavailableException>(() => setup.Installed.ObserveAsync(Package, Version, CancellationToken.None).GetAwaiter().GetResult());
            Check.Equal(reads, setup.ListReads);
        });

        suite.Add("host raw sources: only a source-host client is accepted", () =>
        {
            using var temp = new Sandbox();
            using var client = new SuwayomiClient(new AutoSourcarrSettings(), "http://127.0.0.1:4567", handler: new Handler((_, _) => throw new InvalidOperationException("No request expected")));
            var io = new BoundedWorkScheduler(1, 2);
            try { Check.Throws<ArgumentException>(() => _ = new HostRawInstalledSources(client, temp.Path, () => (true, 1), io)); }
            finally { io.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });

        suite.Add("host raw sources: the baseline store prepares raw-first repair input from the host jar", () =>
        {
            using var setup = new Setup();
            Check.Throws<InvalidDataException>(() => setup.Baselines.PrepareRepairInputAsync(Package, Version).GetAwaiter().GetResult());
            var raw = setup.Baselines.PrepareRepairInputAsync(Package, Version, rawFirst: true).GetAwaiter().GetResult();
            Check.True(raw["raw_first"]!.GetValue<bool>());
            Check.Equal(Convert.ToBase64String(setup.Jar), Text(raw, "baseline"));
            Check.Equal(Version, Text(raw, "installed_version"));
            var backup = Path.Combine(setup.Maintenance, "backups", Convert.ToHexStringLower(SHA256.HashData(setup.Jar)), "installed.jar");
            Check.True(File.ReadAllBytes(backup).AsSpan().SequenceEqual(setup.Jar), "The baseline jar is backed up under source-maintenance/backups.");
        });
    }

    private static string Text(JsonObject row, string key) => row[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static byte[] Archive(string label)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (name, bytes) in new[] { ("eu/example/Fixture.class", Encoding.UTF8.GetBytes("class " + label)), ("resource.txt", Encoding.UTF8.GetBytes("resource")) })
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal); entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open(); stream.Write(bytes);
            }
        return output.ToArray();
    }

    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _sandbox = new();
        private int _listReads, _jarReads;
        public bool Ready = true; public long Generation = 1;
        public bool ChangeJarOnSecondRead, ChangeSourcesOnSecondList;
        public HttpStatusCode JarStatus = HttpStatusCode.OK;
        public byte[] Jar { get; } = Archive("original");
        public string Maintenance { get; }
        public int ListReads => Volatile.Read(ref _listReads);
        public int JarReads => Volatile.Read(ref _jarReads);
        public BoundedWorkScheduler Io { get; } = new(2, 8);
        public SuwayomiClient Client { get; }
        public HostRawInstalledSources Installed { get; }
        public SourceBaselineStore Baselines { get; }

        public Setup()
        {
            Maintenance = Path.Combine(_sandbox.Path, "source-maintenance"); Directory.CreateDirectory(Maintenance);
            Client = SuwayomiClient.ForSourceHost(new AutoSourcarrSettings { SourceEngineMode = "integrated" }, "http://127.0.0.1:4567", "token", handler: new Handler(Answer));
            Installed = new HostRawInstalledSources(Client, Maintenance, () => (Volatile.Read(ref Ready), Volatile.Read(ref Generation)), Io);
            Baselines = new SourceBaselineStore(Installed, Maintenance, Io);
        }

        private Task<HttpResponseMessage> Answer(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/v1/extensions")
            {
                var count = Interlocked.Increment(ref _listReads);
                var body = new JsonObject { ["extensions"] = new JsonArray(Row(Package, true, ChangeSourcesOnSecondList && count % 2 == 0 ? "-5678" : "-1234"), Row(Other, false, "-999")), ["extensionStores"] = new JsonArray() };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
            }
            if (request.Method == HttpMethod.Get && path == "/v1/extensions/" + Package + "/jar")
            {
                var count = Interlocked.Increment(ref _jarReads);
                if (JarStatus != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(JarStatus) { Content = new StringContent("{\"detail\":\"" + Package + " is not installed\"}", Encoding.UTF8, "application/json") });
                var bytes = ChangeJarOnSecondRead && count % 2 == 0 ? Archive("changed") : Jar;
                var content = new ByteArrayContent(bytes); content.Headers.ContentType = new("application/java-archive");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            throw new InvalidOperationException("Unexpected host request " + request.Method + " " + path);
        }

        private static JsonObject Row(string package, bool installed, string sourceId) => new()
        {
            ["pkgName"] = package, ["name"] = "Host fixture", ["lang"] = "en", ["versionName"] = Version, ["versionCodeLong"] = "23",
            ["isInstalled"] = installed, ["hasUpdate"] = false, ["isObsolete"] = false, ["iconUrl"] = null, ["contentWarning"] = 0,
            ["storeIndexUrl"] = null, ["apkUrl"] = null, ["apkName"] = null, ["libVersion"] = "1.4", ["loadError"] = null,
            ["source"] = new JsonObject
            {
                ["nodes"] = new JsonArray(new JsonObject
                {
                    ["id"] = sourceId, ["name"] = "Host fixture", ["lang"] = "en", ["baseUrl"] = "https://fixture.example", ["isConfigurable"] = false,
                }),
            },
        };

        public void Dispose()
        {
            Baselines.DisposeAsync().AsTask().GetAwaiter().GetResult(); Installed.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Client.Dispose(); Io.DisposeAsync().AsTask().GetAwaiter().GetResult(); _sandbox.Dispose();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
