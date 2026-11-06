using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Translation;

namespace AutoSourcarr.Tests.Translation;

/// <summary>Phase 10 translation (MAT-20): a LibreTranslate-compatible client whose results only help search.</summary>
public static class TranslationTests
{
    internal sealed class FakeLibreTranslate(Func<JsonObject, string> translate) : HttpMessageHandler
    {
        public List<JsonObject> Requests { get; } = [];
        public List<string> Paths { get; } = [];
        public bool Fail { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            if (Fail) throw new HttpRequestException("connection refused");
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"code\":\"en\"},{\"code\":\"ja\"}]", Encoding.UTF8, "application/json") };
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!.AsObject();
            lock (Requests) Requests.Add(body);
            var answer = new JsonObject { ["translatedText"] = translate(body) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private static string Q(JsonObject body) => body["q"]!.GetValue<string>();
    private static string Target(JsonObject body) => body["target"]!.GetValue<string>();

    public static void Register(TestSuite suite)
    {
        suite.Add("translation: LibreTranslate requests are cached durably, keyed, and never fail a search", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            var settings = new AutoSourcarrSettings { DataDir = temp.Path, TranslationMode = "external", TranslationApiUrl = "http://translate.test/", TranslationApiKey = "secret" };
            var fake = new FakeLibreTranslate(body => Q(body) == "Same" ? "same" : "[" + Target(body) + "] " + Q(body));
            using var client = new TranslationClient(database, () => settings, fake);
            Check.Equal("http://translate.test", client.BaseUrl);
            Check.Equal("[ja] Witch Hat", client.TranslateAsync("Witch Hat", "JA").GetAwaiter().GetResult());
            Check.Equal("secret", fake.Requests[0]["api_key"]!.GetValue<string>());
            Check.Equal("auto", fake.Requests[0]["source"]!.GetValue<string>());
            Check.Equal("[ja] Witch Hat", client.TranslateAsync("Witch Hat", "ja").GetAwaiter().GetResult());
            Check.Equal(1, fake.Requests.Count); // the hot set answered
            using (var restarted = new TranslationClient(database, () => settings, fake))
                Check.Equal("[ja] Witch Hat", restarted.Cached("Witch Hat", "ja")); // translation_cache survives a restart
            Check.Equal("", client.TranslateAsync("Same", "en").GetAwaiter().GetResult()); // equal after normalization: not a translation
            Check.Equal(0L, database.Read(DatabaseFile.Catalog, s => (long)s.Scalar("SELECT COUNT(*) FROM titles")!)); // never a title
            fake.Fail = true;
            Check.Equal("", client.TranslateAsync("Other", "ko").GetAwaiter().GetResult());
            var health = client.HealthAsync().GetAwaiter().GetResult();
            Check.True(!health["ok"]!.GetValue<bool>() && health["configured"]!.GetValue<bool>());
            var off = settings with { TranslationMode = "off" };
            using var disabled = new TranslationClient(database, () => off, fake);
            Check.True(!disabled.Enabled);
            Check.Equal("", disabled.TranslateAsync("Unseen", "ja").GetAwaiter().GetResult());
            var builtin = settings with { TranslationMode = "builtin" };
            using var bundled = new TranslationClient(database, () => builtin, fake);
            Check.Equal("", bundled.BaseUrl); // 0.5.x bundles no translator
            Check.True(bundled.OffReason!.Contains("0.6.0", StringComparison.Ordinal), "the status says built-in translation returns in 0.6.0");
        });

        suite.Add("translation: a LibreTranslate connection wins over the setting; the 0.4.x loopback address counts as not configured (0.5.1)", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            var migrated = new AutoSourcarrSettings { DataDir = temp.Path, TranslationMode = "auto", TranslationApiUrl = "http://127.0.0.1:5000", TranslationApiKey = "old" };
            var fake = new FakeLibreTranslate(body => "[" + Target(body) + "] " + Q(body));
            using (var leftover = new TranslationClient(database, () => migrated, fake))
            {
                Check.True(!leftover.Enabled, "the 0.4.x built-in address is not a translator in 0.5");
                Check.True(leftover.OffReason!.Contains("127.0.0.1:5000", StringComparison.Ordinal), "the status names the old address");
            }
            var connection = new AutoSourcarr.Core.Connections.ConnectionConfig("t1", "libretranslate", "Translator", true,
                new Dictionary<string, string> { ["url"] = "http://libretranslate:5000/", ["api_key"] = "connection-key" });
            using var client = new TranslationClient(database, () => migrated with { TranslationApiUrl = "http://translate.test" }, fake) { Connections = () => [connection] };
            Check.Equal("http://libretranslate:5000", client.BaseUrl);
            Check.Equal("[ja] Witch Hat", client.TranslateAsync("Witch Hat", "ja").GetAwaiter().GetResult());
            Check.Equal("connection-key", fake.Requests[^1]["api_key"]!.GetValue<string>());
            Check.True(AutoSourcarr.Core.Connections.Types.TranslationConnectionType.BaseUrl("http://localhost:5000") is null, "loopback is refused for connections too");
            var broken = connection with { Label = "Old translator", Values = new Dictionary<string, string> { ["url"] = "http://127.0.0.1:5000" } };
            using var refused = new TranslationClient(database, () => migrated with { TranslationApiUrl = "http://translate.test" }, fake) { Connections = () => [broken] };
            Check.True(!refused.Enabled, "a broken connection never falls back to TRANSLATION_API_URL silently");
            Check.True(refused.OffReason!.Contains("Old translator", StringComparison.Ordinal), "the status names the broken connection");
        });

        suite.Add("translation: search names gain origin and literal translations, transient and deduplicated", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            var settings = new AutoSourcarrSettings { DataDir = temp.Path, TranslationMode = "external", TranslationApiUrl = "http://translate.test", ManualTranslationQueryLimit = 6 };
            var fake = new FakeLibreTranslate(body => Target(body) == "en" ? "Witch Hat Atelier" : "とんがり帽子のアトリエ");
            using var client = new TranslationClient(database, () => settings, fake);
            var names = new TranslatedSearchNames(client, () => settings);
            IReadOnlyList<SearchName> own = [new("Witch Hat Atelier", "known", "en"), new("とんがり帽子のアトリエ", "known", "ja")];
            var expanded = names.ExpandAsync(own, "Manga, Fantasy").GetAwaiter().GetResult();
            // Both translations already exist as titles: nothing is added twice.
            Check.Equal(2, expanded.Count);
            IReadOnlyList<SearchName> latin = [new("Witch Hat Atelier", "known", "en")];
            var origin = names.ExpandAsync(latin, "manga").GetAwaiter().GetResult();
            Check.Equal(2, origin.Count);
            Check.Equal("translated-origin", origin[1].Kind); Check.Equal("ja", origin[1].Language);
            IReadOnlyList<SearchName> korean = [new("나 혼자만 레벨업", "known", "ko")];
            var literal = new TranslatedSearchNames(new TranslationClient(database, () => settings, new FakeLibreTranslate(_ => "Solo Leveling")), () => settings)
                .ExpandAsync(korean).GetAwaiter().GetResult();
            Check.Equal("translated-literal", literal[1].Kind); Check.Equal("Solo Leveling", literal[1].Query);
            Check.Equal("ko", TitleScripts.Language("나 혼자만")); Check.Equal("ja", TitleScripts.Language("とんがり")); Check.Equal("zh", TitleScripts.Language("全职高手"));
            Check.True(TitleScripts.OriginLanguages(["Solo"], "Manhwa", ["ja"]).SequenceEqual(["ko"]));
            Check.True(TitleScripts.OriginLanguages(["Solo"], "", ["ja", "fr"]).SequenceEqual(["ja"]));
        });

        suite.Add("translation: the health check answers at once and checks in the background", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            var settings = new AutoSourcarrSettings { DataDir = temp.Path, TranslationMode = "external", TranslationApiUrl = "http://translate.test" };
            var fake = new FakeLibreTranslate(_ => "");
            using var client = new TranslationClient(database, () => settings, fake);
            var check = new TranslationHealthCheck(client);
            var first = check.Snapshot();
            Check.True(!first["checked"]!.GetValue<bool>());
            JsonObject later = first;
            for (var attempt = 0; attempt < 100 && !later["checked"]!.GetValue<bool>(); attempt++) { Thread.Sleep(20); later = check.Snapshot(); }
            Check.True(later["checked"]!.GetValue<bool>());
            Check.True(later["ok"]!.GetValue<bool>());
            Check.Equal(2, later["languages"]!.GetValue<int>());
        });
    }
}
