using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair.Migration;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Runtime;

namespace AutoSourcarr.Tests.Sources;

/// <summary>
/// Phase 4: the C# client against the real AutoSourcarr source host (the jar built by scripts/build-source-host.sh).
/// Runs when AUTOSOURCARR_TEST_SOURCE_HOST_JAR names the jar (the CI probe sets it); otherwise it records a note.
/// The supervised process installs a Keiyoushi extension and reads search, details, chapters, pages and one image
/// through the same client calls the catalogue uses, then is killed and must come back by itself.
/// </summary>
public static class SourceHostIntegrationTests
{
    private const string Keiyoushi = AutoSourcarr.Core.Sources.Repositories.SourceRepositoryStore.KeiyoushiUrl;

    public static void Register(TestSuite suite)
    {
        suite.Add("source host: supervised host serves install, search, details, chapters, pages and image to the client; restarts after a crash", () =>
        {
            if (HostJar() is not { } jar) return;
            Run(jar).GetAwaiter().GetResult();
        });
        // Phase 6 done-when proof: a known orphan is repaired and passes content checks.
        suite.Add("source host: a moved-domain orphan is repaired by the domain repair, passes the content check and rolls back", () =>
        {
            if (HostJar() is not { } jar) return;
            RunDomainRepair(jar).GetAwaiter().GetResult();
        });
    }

    /// <summary>The host jar the CI probe builds, or null (with a note) when it is not set here.</summary>
    private static string? HostJar()
    {
        var jar = Environment.GetEnvironmentVariable("AUTOSOURCARR_TEST_SOURCE_HOST_JAR");
        if (!string.IsNullOrWhiteSpace(jar) && File.Exists(jar)) return jar;
        Console.WriteLine("NOTE source host jar not set; the source host integration check runs in the CI probe.");
        if (Environment.GetEnvironmentVariable("AUTOSOURCARR_REQUIRE_SOURCE_HOST") == "1") throw new InvalidOperationException("The source host jar is required here.");
        return null;
    }

    private static Dictionary<string, string> HostEnvironment(string home)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = home,
            ["EXTENSION_STORES"] = "[\"" + Keiyoushi + "\"]",
            ["WEBVIEW_ENABLED"] = "false"
        };
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome) environment["JAVA_HOME"] = javaHome;
        return environment;
    }

    /// <summary>
    /// Mangatown (passes the CI source check) made into an orphan by moving its compiled address to
    /// www.mangatown.invalid: the content check fails; the domain repair from the .invalid host back to
    /// www.mangatown.com ends repaired with a passing content check; rollback installs the kept jar and it still loads.
    /// </summary>
    private static async Task RunDomainRepair(string jar)
    {
        const string package = "eu.kanade.tachiyomi.extension.en.mangatown";
        const string liveHost = "www.mangatown.com", deadHost = "www.mangatown.invalid";
        using var temp = new Sandbox();
        var java = Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home ? Path.Combine(home, "bin", "java") : "java";
        var log = new List<string>();
        await using var host = new SourceHostProcess(java, jar, Path.Combine(temp.Path, "source-host"), FreePort(), HostEnvironment(temp.Path),
            line => { lock (log) log.Add(line); });
        try
        {
            await host.StartAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
            await using var client = SuwayomiClient.ForSourceHost(new AutoSourcarrSettings(), host.BaseUrl, host.Token);
            var catalog = await client.ExtensionsAsync(refresh: true);
            Check.True(catalog["extensions"]!.AsArray().Any(row => row?["pkgName"]?.GetValue<string>() == package), "store lists " + package);
            var installed = await client.UpdateExtensionAsync(package, "install");
            Check.True(installed?["isInstalled"]?.GetValue<bool>() == true, "extension installed");

            // 1. the working jar
            var working = await client.ReadExtensionJarAsync(package);
            var contents = new SuwayomiContentCheckSource(client);

            // 2. the simulated orphan: its site "moved" to an address that does not exist
            var orphan = DomainMigration.Migrate(working, liveHost, deadHost);
            Check.True(orphan.Replacements > 0, "the jar names " + liveHost);
            var broken = await client.InstallExtensionJarAsync(package, orphan.Jar);
            Check.Equal("", SourceResponseContract.Text(broken["loadError"]));
            var sources = (broken["source"] as JsonObject)?["nodes"] as JsonArray ?? [];
            var source = sources.OfType<JsonObject>().First(row => SourceResponseContract.Text(row["lang"]) == "en");
            var sourceId = SourceResponseContract.Identity(source["id"])!;
            Check.True(SourceResponseContract.Text(source["baseUrl"]).Contains(deadHost, StringComparison.Ordinal), "orphan base URL " + source["baseUrl"]);
            var failing = await ContentCheck.RunAsync(contents, sourceId, SourceResponseContract.Text(source["name"]));
            Check.True(!failing.Ok, "the orphan's content check fails");
            // The failed check must not leave the source paused by the host's back-off.
            await client.HostAsync(HttpMethod.Delete, "/v1/sources/" + Uri.EscapeDataString(sourceId) + "/backoff");

            // 3. the domain repair back to the live site
            var root = Path.Combine(temp.Path, "source-maintenance");
            var repair = new DomainRepair(root, new SuwayomiDomainRepairHost(client), new SemaphoreSlim(1, 1));
            var report = await repair.RunAsync(package, new DomainRepairOptions(NewHost: liveHost, OldHost: deadHost));
            Check.True(SourceResponseContract.Text(report["state"]) == "repaired", "domain repair: " + report.ToJsonString());
            Check.True(report["content_check"]?["ok"]?.GetValue<bool>() == true, "repaired content check passed");
            Check.True(((report["sources_after"] as JsonArray) ?? []).OfType<JsonObject>()
                .Any(row => SourceResponseContract.Text(row["base_url"]).Contains(liveHost, StringComparison.Ordinal)), "repaired base URL");
            var passing = await ContentCheck.RunAsync(contents, sourceId, SourceResponseContract.Text(source["name"]));
            Check.True(passing.Ok, "content check after repair: " + passing.ToJson().ToJsonString());
            Check.True(Directory.EnumerateFiles(repair.LastWorkingDirectory(package), "*.jar").Any(), "the jar before the repair is kept");

            // 4. rollback installs the kept jar (the orphan's) and the source still loads
            var rolledBack = await repair.RollbackAsync(package);
            Check.True(SourceResponseContract.Text(rolledBack["state"]) == "rolled_back", "rollback: " + rolledBack.ToJsonString());
            Check.True((await client.SourcesAsync()).Any(row => row["id"]!.ToString() == sourceId), "source loads after rollback");
            var after = await client.HostExtensionAsync(package);
            Check.Equal("", SourceResponseContract.Text(after?["loadError"]));

            // Leave the working jar installed.
            await client.InstallExtensionJarAsync(package, working);
        }
        catch
        {
            lock (log) Console.WriteLine(string.Join('\n', log.TakeLast(80)));
            throw;
        }
    }

    private static async Task Run(string jar)
    {
        using var temp = new Sandbox();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = temp.Path,
            ["EXTENSION_STORES"] = "[\"" + Keiyoushi + "\"]",
            // The WebView (a 150 MB Chromium download) is checked by the probe's Cloudflare run, not here.
            ["WEBVIEW_ENABLED"] = "false"
        };
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome) environment["JAVA_HOME"] = javaHome;
        var java = Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home ? Path.Combine(home, "bin", "java") : "java";
        var log = new List<string>();
        await using var host = new SourceHostProcess(java, jar, Path.Combine(temp.Path, "source-host"), FreePort(), environment, line => { lock (log) log.Add(line); });
        try
        {
            await host.StartAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
            await using var client = SuwayomiClient.ForSourceHost(new AutoSourcarrSettings(), host.BaseUrl, host.Token);
            using (var anonymous = new HttpClient())
                Check.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.GetAsync(host.BaseUrl + "/v1/sources")).StatusCode);

            var catalog = await client.ExtensionsAsync(refresh: true);
            const string package = "eu.kanade.tachiyomi.extension.en.mangapill";
            Check.True(catalog["extensions"]!.AsArray().Any(row => row?["pkgName"]?.GetValue<string>() == package), "store lists " + package);
            var installed = await client.UpdateExtensionAsync(package, "install");
            Check.True(installed?["isInstalled"]?.GetValue<bool>() == true, "extension installed");

            var source = (await client.SourcesAsync()).First(row => row["name"]?.GetValue<string>()?.Contains("MangaPill", StringComparison.OrdinalIgnoreCase) == true);
            var sourceId = source["id"]!.ToString();
            var feed = await client.FetchSourceMangaAsync(sourceId, "search", 1, "one piece");
            var manga = feed["mangas"]!.AsArray().OfType<JsonObject>().First();
            var mangaId = manga["id"]!.GetValue<long>();
            var details = await client.FetchMangaAndChaptersAsync(mangaId);
            var chapters = details["chapters"]!.AsArray().OfType<JsonObject>().ToList();
            Check.True(chapters.Count > 0, "chapters returned");
            var pages = await client.FetchChapterPagesAsync(chapters[^1]["id"]!.GetValue<long>());
            Check.True(pages.Count > 0, "pages returned");
            var index = await client.ChapterIndexForIdAsync(mangaId, chapters[^1]["id"]!.GetValue<long>());
            var (bytes, contentType) = await client.FetchPageImageAsync(mangaId, index, 0);
            Check.True(bytes.Length > 1000 && contentType.StartsWith("image/", StringComparison.Ordinal), "page image " + contentType + " " + bytes.Length);

            // A crashed host comes back on the same port and token and keeps its installed extensions.
            KillHost(host);
            // During the restart delay the status must stay readable (no disposed process behind it).
            for (var i = 0; i < 20; i++) { _ = host.Running; _ = host.LastError; Thread.Sleep(100); }
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline && !(host.Restarts == 1 && await host.HealthyAsync(CancellationToken.None))) await Task.Delay(500);
            Check.Equal(1, host.Restarts);
            Check.True(await host.HealthyAsync(CancellationToken.None), "host healthy after restart");
            Check.True((await client.SourcesAsync()).Any(row => row["id"]!.ToString() == sourceId), "extension kept across restart");
            for (var i = 0; i < 50 && host.LastError.Length > 0; i++) await Task.Delay(100);
            Check.Equal("", host.LastError);
            // Disposal during a restart delay stops the host for good.
            KillHost(host);
            await Task.Delay(300);
            await host.DisposeAsync();
            Check.True(!host.Running, "no host after dispose");
            await Task.Delay(6000);
            using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
                Check.True(!await Reachable(probe, host.BaseUrl), "no host was started after dispose");
        }
        catch
        {
            lock (log) Console.WriteLine(string.Join('\n', log.TakeLast(80)));
            throw;
        }
    }

    private static async Task<bool> Reachable(HttpClient client, string baseUrl)
    {
        try { using var _ = await client.GetAsync(baseUrl + "/v1/health"); return true; }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { return false; }
    }

    private static void KillHost(SourceHostProcess host)
    {
        var field = typeof(SourceHostProcess).GetField("_process", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((System.Diagnostics.Process)field.GetValue(host)!).Kill(entireProcessTree: true);
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
