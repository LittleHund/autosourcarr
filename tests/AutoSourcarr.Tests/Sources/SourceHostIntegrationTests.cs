using System.Text.Json.Nodes;
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
            var jar = Environment.GetEnvironmentVariable("AUTOSOURCARR_TEST_SOURCE_HOST_JAR");
            if (string.IsNullOrWhiteSpace(jar) || !File.Exists(jar))
            {
                Console.WriteLine("NOTE source host jar not set; the source host integration check runs in the CI probe.");
                if (Environment.GetEnvironmentVariable("AUTOSOURCARR_REQUIRE_SOURCE_HOST") == "1") throw new InvalidOperationException("The source host jar is required here.");
                return;
            }
            Run(jar).GetAwaiter().GetResult();
        });
    }

    private static async Task Run(string jar)
    {
        using var temp = new Sandbox();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = temp.Path,
            ["EXTENSION_STORES"] = "[\"" + Keiyoushi + "\"]"
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
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline && !(host.Restarts == 1 && await host.HealthyAsync(CancellationToken.None))) await Task.Delay(500);
            Check.Equal(1, host.Restarts);
            Check.True(await host.HealthyAsync(CancellationToken.None), "host healthy after restart");
            Check.True((await client.SourcesAsync()).Any(row => row["id"]!.ToString() == sourceId), "extension kept across restart");
        }
        catch
        {
            lock (log) Console.WriteLine(string.Join('\n', log.TakeLast(80)));
            throw;
        }
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
