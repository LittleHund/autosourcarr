using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Reader.Extension;

namespace AutoSourcarr.Tests.ReaderExtension;

/// <summary>Phase 10 READ-1/READ-5: the served reader extension repository and the per-slot setup links.</summary>
public static class ReaderExtensionTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("reader extension: repository serves only its index, repo, apk, icon and jar files", Repository);
        suite.Add("reader extension: setup gives the Mihon and Tachiyomi repository links and one row per server slot", Setup);
        suite.Add("reader extension: setup without a public address or build warns and gives no links", SetupWarnings);
    }

    private const string Index = """
        [{"name":"Tachiyomi: AutoSourcarr","pkg":"eu.kanade.tachiyomi.extension.all.autosourcarr","apk":"tachiyomi-all.autosourcarr-v1.4.4005.apk","lang":"all","code":108005,"version":"1.4.4005","nsfw":1,
          "sources":[{"name":"AutoSourcarr","lang":"all","id":1594614510450266156,"baseUrl":""},{"name":"AutoSourcarr (2)","lang":"all","id":2791373593127977187,"baseUrl":""}],
          "sha256":"","jar":"tachiyomi-all.autosourcarr-v1.4.4005.jar","jar_sha256":""}]
        """;

    private static string Build(Sandbox sandbox)
    {
        var root = Path.Combine(sandbox.Path, "reader-extension");
        Directory.CreateDirectory(Path.Combine(root, "apk")); Directory.CreateDirectory(Path.Combine(root, "icon")); Directory.CreateDirectory(Path.Combine(root, "jar"));
        File.WriteAllText(Path.Combine(root, "index.min.json"), Index);
        File.WriteAllText(Path.Combine(root, "repo.json"), """{"index_v2":null,"meta":{"name":"AutoSourcarr","shortName":"AutoSourcarr","website":"","signingKeyFingerprint":"ab"}}""");
        File.WriteAllText(Path.Combine(root, "build.json"), """{"autosourcarr_version":"0.4.5","signed_for_release":true,"signing_fingerprint":"ab","slots":2}""");
        File.WriteAllBytes(Path.Combine(root, "apk", "tachiyomi-all.autosourcarr-v1.4.4005.apk"), Encoding.ASCII.GetBytes("PK-apk"));
        File.WriteAllBytes(Path.Combine(root, "jar", "tachiyomi-all.autosourcarr-v1.4.4005.jar"), Encoding.ASCII.GetBytes("PK-jar"));
        File.WriteAllBytes(Path.Combine(root, "icon", ReaderExtensionRepository.Package + ".png"), [0x89, 0x50]);
        File.WriteAllText(Path.Combine(sandbox.Path, "secret.txt"), "not served");
        return root;
    }

    private static void Repository()
    {
        using var sandbox = new Sandbox();
        var repository = new ReaderExtensionRepository(Build(sandbox));
        Check.Equal("application/json", repository.Read("index.min.json")!.ContentType);
        Check.Equal("application/json", repository.Read("repo.json")!.ContentType);
        Check.Equal("application/vnd.android.package-archive", repository.Read("apk/tachiyomi-all.autosourcarr-v1.4.4005.apk")!.ContentType);
        Check.Equal("application/java-archive", repository.Read("jar/tachiyomi-all.autosourcarr-v1.4.4005.jar")!.ContentType);
        Check.Equal("image/png", repository.Read("icon/" + ReaderExtensionRepository.Package + ".png")!.ContentType);
        foreach (var refused in new[] { "build.json", "../secret.txt", "apk/../../secret.txt", "apk/..", "apk/x.jar", "icon/a/b.png", "", "/index.min.json", "apk/missing.apk", "APK/x.apk" })
            Check.True(repository.Read(refused) is null, "refused " + refused);
        var status = repository.Status();
        Check.True(status["available"]!.GetValue<bool>(), "build available");
        Check.Equal(2, status["slots"]!.GetValue<int>());
        Check.Equal("1.4.4005", status["version_name"]!.GetValue<string>());
        Check.Equal("2791373593127977187", status["sources"]![1]!["source_id"]!.GetValue<string>());
        Check.Equal("/api/reader/repo/apk/tachiyomi-all.autosourcarr-v1.4.4005.apk", status["apk"]!.GetValue<string>());
        Check.True(status["signed_for_release"]!.GetValue<bool>(), "signed build");
    }

    private static void Setup()
    {
        using var sandbox = new Sandbox();
        var setup = ReaderSetup.Build("https://books.example.com/", new ReaderExtensionRepository(Build(sandbox)).Status());
        Check.Equal("https://books.example.com", setup["address"]!.GetValue<string>());
        Check.Equal("https://books.example.com/api/reader/repo/index.min.json", setup["repository_url"]!.GetValue<string>());
        Check.Equal("mihon://extension-store?url=https%3A%2F%2Fbooks.example.com%2Fapi%2Freader%2Frepo%2Findex.min.json", setup["mihon_link"]!.GetValue<string>());
        Check.Equal("tachiyomi://add-repo?url=https%3A%2F%2Fbooks.example.com%2Fapi%2Freader%2Frepo%2Findex.min.json", setup["tachiyomi_link"]!.GetValue<string>());
        var slots = setup["slots"]!.AsArray().OfType<JsonObject>().ToList();
        Check.Equal(2, slots.Count);
        Check.Equal("AutoSourcarr (2)", slots[1]["source_name"]!.GetValue<string>());
        Check.Equal("1594614510450266156", slots[0]["source_id"]!.GetValue<string>());
        Check.True(slots[1]["settings_text"]!.GetValue<string>().Contains("Server 2 address: https://books.example.com\n", StringComparison.Ordinal), "slot 2 text");
        Check.Equal(0, setup["warnings"]!.AsArray().Count);
        Check.True(!setup.ToJsonString().Contains("reader-key", StringComparison.Ordinal), "no key in setup");
    }

    private static void SetupWarnings()
    {
        using var sandbox = new Sandbox();
        var missing = new ReaderExtensionRepository(Path.Combine(sandbox.Path, "none")).Status();
        Check.True(!missing["available"]!.GetValue<bool>(), "no build");
        var setup = ReaderSetup.Build("autosourcarr:8282", missing);
        Check.Equal("", setup["repository_url"]!.GetValue<string>());
        Check.Equal("", setup["mihon_link"]!.GetValue<string>());
        Check.Equal(ReaderExtensionRepository.DefaultSlots, setup["slots"]!.AsArray().Count);
        Check.Equal(2, setup["warnings"]!.AsArray().Count);
        Check.Equal("", ReaderSetup.Address("https://user:pw@example.com"));
        Check.Equal("http://192.168.1.5:8282/sub", ReaderSetup.Address("http://192.168.1.5:8282/sub/"));
        // A plain-HTTP address with a build: only the HTTPS warning.
        Check.Equal(1, ReaderSetup.Build("http://192.168.1.5:8282", new ReaderExtensionRepository(Build(sandbox)).Status())["warnings"]!.AsArray().Count);
    }
}
