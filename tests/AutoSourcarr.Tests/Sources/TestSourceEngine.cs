using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Tests;

internal sealed class TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}

internal sealed class TestSourceEngine(string mode = "working")
{
    public int Calls;
    public Func<HttpRequestMessage, CancellationToken, Task>? BeforeSend { get; set; }
    public SuwayomiClient Client() => new(SettingsLoader.Load(), "http://source.test", handler: new TestHttpHandler(async (request, token) =>
    {
        Interlocked.Increment(ref Calls);
        if (BeforeSend is not null) await BeforeSend(request, token).ConfigureAwait(false);
        if (request.Method == HttpMethod.Get)
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(mode == "login-image" ? Encoding.UTF8.GetBytes("<html>login</html>") : File.ReadAllBytes(ConfigurationTests.Fixture("Images/sample.png"))) };
        var input = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false))!;
        var query = input["query"]!.GetValue<string>();
        var body = query.Contains("AutoSourcarrBrowse", StringComparison.Ordinal)
            ? "{\"fetchSourceManga\":{\"hasNextPage\":false,\"mangas\":" + (mode == "empty" ? "[]" : "[{\"id\":42,\"sourceId\":\"-1\",\"title\":\"Sample\"}]") + "}}"
            : query.Contains("AutoSourcarrManga", StringComparison.Ordinal)
                ? "{\"fetchMangaAndChapters\":{\"manga\":{\"id\":42,\"sourceId\":\"-1\",\"title\":\"Sample\",\"url\":\"/manga/42\",\"author\":\"Author\"},\"chapters\":[{\"id\":43,\"mangaId\":42,\"name\":\"One\",\"url\":\"/chapter/43\"}]}}"
                : query.Contains("AutoSourcarrExtensions", StringComparison.Ordinal)
                    ? "{\"extensions\":{\"nodes\":[{\"pkgName\":\"example\",\"versionName\":\"1.0\",\"isInstalled\":true,\"source\":{\"nodes\":[{\"id\":\"-1\"}]}}]}}"
                    : "{\"fetchChapterPages\":{\"pages\":[\"" + (mode == "external-page" ? "https://untrusted.test/page.jpg" : "/api/v1/manga/42/chapter/0/page/0") + "\"]}}";
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":" + body + "}", Encoding.UTF8, "application/json") };
    }));
}
