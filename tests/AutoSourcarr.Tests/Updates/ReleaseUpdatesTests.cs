using System.Net;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Updates;
using AutoSourcarr.Tests.Connections;

namespace AutoSourcarr.Tests.Updates;

/// <summary>0.5.4: the update notice (registry tags, CHANGELOG sections, instructions by image tag, dismiss).</summary>
public static class ReleaseUpdatesTests
{
    private const string Changelog = """
        # Changelog

        ## 0.5.5 - 2026-11-20

        - **Five:** fifth.

        ## 0.5.4 - 2026-11-12

        - **Four:** fourth.

        ## 0.5.3 - 2026-11-08

        - Three.

        ## 0.5.2 - 2026-11-07

        - Two.
        """;

    public static void Register(TestSuite suite)
    {
        suite.Add("update notice: release tags, version order and CHANGELOG sections between this and the newest version", () =>
        {
            var tags = ReleaseUpdates.ParseTags("""{"name":"littlehund/autosourcarr","tags":["latest","0.5.2","0.5.10","0.5.3","sha-abc","0.5.9-rc1"]}""");
            Check.Equal("0.5.2,0.5.10,0.5.3", string.Join(',', tags));
            Check.Equal("0.5.10", ReleaseUpdates.Newest(tags));
            Check.True(ReleaseUpdates.Newer("0.5.10", "0.5.9") && !ReleaseUpdates.Newer("0.5.3", "0.5.3") && !ReleaseUpdates.Newer("", "0.5.3"), "numeric order");
            var notes = ReleaseUpdates.ParseChangelog(Changelog, "0.5.3", "0.5.5");
            Check.Equal("0.5.5,0.5.4", string.Join(',', notes.Select(note => note.Version)));
            Check.Equal("2026-11-12", notes[1].Date);
            Check.Equal("- **Four:** fourth.", notes[1].Text);
            Check.Equal(1, ReleaseUpdates.ParseChangelog(Changelog, "0.5.3", "0.5.4").Count); // a version on GitHub but not yet published is left out
        });

        suite.Add("update notice: instructions follow UPDATE_IMAGE_TAG (latest restarts, pinned names the new tag, auto shows both)", () =>
        {
            Check.True(ReleaseUpdates.Instructions("0.5.4", "latest").Single().Contains("restart", StringComparison.Ordinal), "latest");
            Check.True(ReleaseUpdates.Instructions("0.5.4", "pinned").Single().Contains("autosourcarr:0.5.4", StringComparison.Ordinal), "pinned");
            Check.Equal(2, ReleaseUpdates.Instructions("0.5.4", "auto").Count);
        });

        suite.Add("update notice: a check reads the registry anonymously, shows newer versions with their changes, and a dismissal lasts until the next version", () =>
        {
            using var sandbox = new Sandbox();
            var published = new List<string> { "latest", "0.5.3", "0.5.4" };
            var handler = new StubHandler(request => request.RequestUri!.AbsoluteUri switch
            {
                var url when url.StartsWith("https://ghcr.io/token", StringComparison.Ordinal) => Json("""{"token":"anon"}"""),
                var url when url.StartsWith("https://ghcr.io/v2/littlehund/autosourcarr/tags/list", StringComparison.Ordinal)
                    => request.Headers.Authorization?.Parameter == "anon" ? Json(new JsonObject { ["tags"] = new JsonArray(published.Select(tag => (JsonNode?)tag).ToArray()) }.ToJsonString()) : new HttpResponseMessage(HttpStatusCode.Unauthorized),
                ReleaseUpdates.ChangelogUrl => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Changelog) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
            using var http = new HttpClient(handler);
            var state = Path.Combine(sandbox.Path, "update-check.json");
            using var updates = new ReleaseUpdates("0.5.3", state, http);
            var announced = new List<string>();
            updates.NewVersion = announced.Add;

            updates.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            var json = updates.Json(enabled: true, "pinned");
            Check.True(json["available"]!.GetValue<bool>() && json["latest"]!.GetValue<string>() == "0.5.4", json.ToJsonString());
            Check.Equal(1, json["notes"]!.AsArray().Count);
            Check.True(json["instructions"]![0]!.GetValue<string>().Contains(":0.5.4", StringComparison.Ordinal), "pinned instruction");
            Check.Equal("0.5.4", string.Join(',', announced));
            Check.True(!updates.Json(enabled: false, "auto")["available"]!.GetValue<bool>(), "turned off shows nothing");

            updates.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal(1, handler.Requests.Count(url => url == ReleaseUpdates.ChangelogUrl)); // the CHANGELOG is read once per new version
            Check.Equal(1, announced.Count);

            updates.Dismiss("0.5.4");
            Check.True(updates.Json(true, "auto")["dismissed"]!.GetValue<bool>(), "dismissed");
            using (var reopened = new ReleaseUpdates("0.5.3", state, http))
            {
                reopened.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                Check.True(reopened.Json(true, "auto")["dismissed"]!.GetValue<bool>(), "the dismissal survives a restart");
            }
            published.Add("0.5.5");
            updates.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            json = updates.Json(true, "latest");
            Check.True(json["available"]!.GetValue<bool>() && !json["dismissed"]!.GetValue<bool>() && json["latest"]!.GetValue<string>() == "0.5.5", json.ToJsonString());
            Check.Equal("0.5.5,0.5.4", string.Join(',', json["notes"]!.AsArray().Select(note => note!["version"]!.GetValue<string>())));
            Check.True(handler.Requests.All(url => !url.Contains('?') || url.Contains("scope=repository", StringComparison.Ordinal) || url.Contains("n=1000", StringComparison.Ordinal)),
                "no instance data in any request");
        });

        suite.Add("update notice: an unreachable registry is reported, never an update", () =>
        {
            using var sandbox = new Sandbox();
            using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            using var updates = new ReleaseUpdates("0.5.3", Path.Combine(sandbox.Path, "u.json"), http);
            updates.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            var json = updates.Json(true, "auto");
            Check.True(!json["available"]!.GetValue<bool>() && json["error"]!.GetValue<string>().Contains("ghcr.io", StringComparison.Ordinal), json.ToJsonString());
        });
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
