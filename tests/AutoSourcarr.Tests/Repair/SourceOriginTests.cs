using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Tests;

public static class SourceOriginTests
{
    private const string Package = "eu.kanade.tachiyomi.extension.en.example";
    private static JsonObject Extension(string index = "") => new() { ["pkgName"] = Package, ["versionName"] = "1.4.23", ["storeIndexUrl"] = index };
    public static void Register(TestSuite suite)
    {
        suite.Add("source origins: released corpus with five explicit current publisher endpoint updates", () =>
        {
            var rows = JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("source-origins.json")))!.AsArray();
            // Retain the original fixture bytes. These five old min-JSON assumptions
            // are intentionally superseded by the reviewed official JSON publisher.
            var endpointUpdates = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SourceOrigin.KeiyoushiIndex] = "https://raw.githubusercontent.com/keiyoushi/extensions/repo/index.json",
                ["https://raw.githubusercontent.com/keiyoushi/extensions/repo/index.json"] = "https://raw.githubusercontent.com/keiyoushi/extensions/repo/index.json",
                ["https://github.com/publisher/extensions/raw/repo/index.pb"] = "https://raw.githubusercontent.com/publisher/extensions/repo/index.json",
                ["https://github.com/other/extensions/raw/repo/index.pb"] = "https://raw.githubusercontent.com/other/extensions/repo/index.json",
                ["https://github.com/keiyoushi/extensions/raw/notrepo/index.pb"] = "https://raw.githubusercontent.com/keiyoushi/extensions/notrepo/index.json"
            };
            var changedEndpoints = 0;
            foreach (var row in rows)
            {
                JsonNode? Run()
                {
                    var argument = row!["argument"]!;
                    switch (row["kind"]!.GetValue<string>())
                    {
                        case "repository":
                            var plan = SourceOrigin.RepositoryPlan(argument.GetValue<string>());
                            return new JsonObject { ["repository"] = plan.Repository, ["host"] = plan.Host, ["path"] = plan.Path, ["resolve_url"] = plan.ResolveUrl };
                        case "known-index": return JsonValue.Create(SourceOrigin.KnownIndex(argument.GetValue<string>()));
                        case "index-project":
                            var project = SourceDiscovery.IndexProject(argument.GetValue<string>());
                            return project is null ? null : new JsonObject { ["owner"] = project.Owner, ["repository"] = project.Repository, ["readme"] = project.Readme, ["index"] = project.Index };
                        default: return SourceOrigin.Resolve(argument["extension"]!.AsObject(), argument["manual"]?.GetValue<bool>(), argument["supplied"] as JsonObject);
                    }
                }
                if (!row!["valid"]!.GetValue<bool>()) { Check.Throws<InvalidDataException>(() => Run()); continue; }
                var expected = row["result"]?.DeepClone();
                if (row["kind"]!.GetValue<string>() == "index-project" && endpointUpdates.TryGetValue(row["argument"]!.GetValue<string>(), out var endpoint))
                {
                    var expectedProject = expected as JsonObject ?? throw new InvalidDataException("Missing historical index project expectation");
                    Check.True(expectedProject["index"]!.GetValue<string>().EndsWith("/index.min.json", StringComparison.Ordinal));
                    expectedProject["index"] = endpoint; changedEndpoints++;
                }
                Check.Equal(CanonicalJson.Text(expected), CanonicalJson.Text(Run()));
            }
            Check.Equal(5, changedEndpoints);
            Console.WriteLine("Source origin corpus cases " + rows.Count + "; explicit current index endpoint changes " + changedEndpoints);
        });
        suite.Add("source discovery: declared Keiyoushi candidate preserves installed version without network guess", () =>
        {
            var fetch = new FakeFetch(new()); var discovery = new SourceDiscovery(fetch);
            foreach (var declared in new[] { SourceOrigin.KeiyoushiIndex, "https://raw.githubusercontent.com/keiyoushi/extensions/repo/index.json",
                "https://raw.githubusercontent.com/keiyoushi/extensions/refs/heads/repo/index.json" })
            {
                Check.True(SourceOrigin.KnownIndex(declared));
                var value = discovery.DiscoverAsync(Extension(declared), []).GetAwaiter().GetResult();
                Check.Equal("known", value["state"]!.GetValue<string>()); Check.Equal("repository-candidate", value["basis"]!.GetValue<string>());
                Check.Equal("1.4.23", value["version"]!.GetValue<string>()); Check.Equal(SourceOrigin.KeiyoushiSource, value["repository"]!.GetValue<string>()); Check.Equal(0, fetch.Calls);
                Check.Equal(SourceOrigin.KeiyoushiSource, SourceOrigin.Resolve(Extension(declared))["repository"]!.GetValue<string>());
                Check.Equal("needs-input", SourceOrigin.Resolve(Extension(declared), manual: true)["state"]!.GetValue<string>());
            }
            foreach (var foreign in new[] { "https://raw.githubusercontent.com/unrelated/extensions/refs/heads/repo/index.json",
                "https://raw.githubusercontent.com/keiyoushi/other/refs/heads/repo/index.json", "https://raw.githubusercontent.com/keiyoushi/extensions/refs/heads/other/index.json" })
                Check.True(!SourceOrigin.KnownIndex(foreign));
            Check.Equal("needs-input", SourceOrigin.Resolve(Extension(SourceOrigin.KeiyoushiIndex), manual: true)["state"]!.GetValue<string>());
        });
        suite.Add("source discovery: configured indices need exact package and same-publisher code section", () =>
        {
            const string index = "https://raw.githubusercontent.com/publisher/extensions/repo/index.min.json", readme = "https://raw.githubusercontent.com/publisher/extensions/repo/README.md";
            var responses = new Dictionary<string, (string Body, string? Resolved)> { [index] = ("[{\"pkg\":\"" + Package + "\"}]", null), [readme] = ("# Source Code\nhttps://github.com/publisher/original-source\n", null) };
            var fetch = new FakeFetch(responses); var discovery = new SourceDiscovery(fetch);
            var value = discovery.DiscoverAsync(Extension(), [index]).GetAwaiter().GetResult();
            Check.Equal("https://github.com/publisher/original-source", value["repository"]!.GetValue<string>()); Check.Equal(2, fetch.Calls);
            responses[index] = ("[{\"pkg\":\"different.package.name\"}]", null); fetch.Calls = 0;
            value = discovery.DiscoverAsync(Extension(), [index]).GetAwaiter().GetResult(); Check.Equal("needs-input", value["state"]!.GetValue<string>()); Check.Equal(1, fetch.Calls);
            responses[index] = ("[{\"pkg\":\"" + Package + "\"}]", null);
            foreach (var body in new[] { "# About\nhttps://github.com/publisher/guess\n", "# Source Code\nhttps://github.com/unrelated/source\n", "# Source Code\nhttps://github.com/publisher/one\nhttps://github.com/publisher/two\n" })
            { responses[readme] = (body, null); Check.Equal("needs-input", discovery.DiscoverAsync(Extension(), [index]).GetAwaiter().GetResult()["state"]!.GetValue<string>()); }
            responses[readme] = ("# Source Code\nhttps://github.com/publisher/original-source\n", "https://raw.githubusercontent.com/attacker/extensions/repo/README.md");
            Check.Equal("needs-input", discovery.DiscoverAsync(Extension(), [index]).GetAwaiter().GetResult()["state"]!.GetValue<string>());
        });
        suite.Add("source discovery: explicit JSON, legacy min and protobuf sibling endpoints retain installed identity", ModernEndpoints);
        suite.Add("source discovery: bounded inline shapes require the exact installed package", InlineMembership);
        suite.Add("source discovery: index and README redirects retain the same semantic publisher", PublisherScope);
        suite.Add("source discovery: modern candidates remain ambiguous and declarations remain authoritative", ModernDeclarations);
        suite.Add("source discovery: bounded errors continue independently without fallback or cancellation success", DiscoveryBounds);
        suite.Add("source downloads: private, mixed DNS, mapped addresses and credential targets are refused", () =>
        {
            foreach (var url in new[] { "http://github.com/a/b", "https://localhost/a", "https://127.0.0.1/a", "https://[::1]/a", "https://user:secret@github.com/a/b", "https://github.com:444/a/b" })
                Check.Throws<InvalidDataException>(() => PublicSourceFetcher.ValidateUrl(new Uri(url)));
            foreach (var address in new[] { "10.0.0.1", "192.168.1.1", "169.254.169.254", "::ffff:127.0.0.1", "fc00::1", "ff02::1" })
                Check.Throws<InvalidDataException>(() => PublicSourceFetcher.ValidateAddresses([IPAddress.Parse("1.1.1.1"), IPAddress.Parse(address)]));
            PublicSourceFetcher.ValidateAddresses([IPAddress.Parse("1.1.1.1"), IPAddress.Parse("2606:4700:4700::1111")]);
            Check.Throws<InvalidDataException>(() => PublicSourceFetcher.ValidateAddresses([]));
        });
        suite.Add("source downloads: bounded bodies, checked redirects and exact HTTP failure status", () =>
        {
            var calls = 0; var fetch = new PublicSourceFetcher(() => new Handler((message, _) =>
            {
                calls++; Check.True(message.Headers.Authorization is null); Check.True(!message.Headers.Contains("Cookie"));
                return Task.FromResult(calls == 1 ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://codeload.github.com/a/b/zip/pinned") } } : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
            }));
            try
            {
                var value = fetch.FetchAsync(new(new Uri("https://github.com/a/b"), 3, TimeSpan.FromSeconds(1))).GetAwaiter().GetResult(); Check.Equal(2, calls); Check.Equal("codeload.github.com", value.ResolvedUrl.Host);
            }
            finally { fetch.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            fetch = new(() => new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) })));
            try { Check.Throws<InvalidDataException>(() => fetch.FetchAsync(new(new Uri("https://github.com/a/b"), 3, TimeSpan.FromSeconds(1))).GetAwaiter().GetResult()); }
            finally { fetch.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            fetch = new(() => new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
            try { Check.Equal(404, Check.Throws<PublicSourceFetchException>(() => fetch.FetchAsync(new(new Uri("https://github.com/a/b"), 3, TimeSpan.FromSeconds(1))).GetAwaiter().GetResult()).StatusCode); }
            finally { fetch.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            fetch = new(() => new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://127.0.0.1/private") } })));
            try { Check.Throws<InvalidDataException>(() => fetch.FetchAsync(new(new Uri("https://github.com/a/b"), 3, TimeSpan.FromSeconds(1))).GetAwaiter().GetResult()); }
            finally { fetch.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            fetch = new(() => new Handler((message, _) =>
            {
                Check.Equal("identity", message.Headers.AcceptEncoding.Single().Value);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }; response.Content.Headers.ContentEncoding.Add("gzip"); return Task.FromResult(response);
            }));
            try { Check.Throws<InvalidDataException>(() => fetch.FetchAsync(new(new Uri("https://github.com/a/b"), 3, TimeSpan.FromSeconds(1))).GetAwaiter().GetResult()); }
            finally { fetch.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            fetch = new(() => new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1]) })));
            try { Check.Equal(206, Check.Throws<PublicSourceFetchException>(() => fetch.FetchAsync(new(new Uri("https://github.com/a/b"), 3, TimeSpan.FromSeconds(1))).GetAwaiter().GetResult()).StatusCode); }
            finally { fetch.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("source downloads: deadline, caller cancellation and shutdown join owned HTTP", () =>
        {
            var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var left = false;
            var fetch = new PublicSourceFetcher(() => new Handler(async (_, token) => { admitted.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } finally { left = true; } return new(HttpStatusCode.OK); }));
            var pending = fetch.FetchAsync(new(new Uri("https://github.com/a/b"), 10, TimeSpan.FromSeconds(30))); admitted.Task.GetAwaiter().GetResult();
            fetch.StopAsync().GetAwaiter().GetResult(); Check.True(left); Check.Throws<OperationCanceledException>(() => pending.GetAwaiter().GetResult()); fetch.DisposeAsync().AsTask().GetAwaiter().GetResult();
            fetch = new(() => new Handler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(HttpStatusCode.OK); }));
            try { Check.Throws<TimeoutException>(() => fetch.FetchAsync(new(new Uri("https://github.com/a/b"), 10, TimeSpan.FromMilliseconds(30))).GetAwaiter().GetResult()); }
            finally { fetch.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }
    private const string Index = "https://raw.githubusercontent.com/publisher/extensions/repo/index.json";
    private const string Readme = "https://raw.githubusercontent.com/publisher/extensions/repo/README.md";
    private const string Repository = "https://github.com/publisher/original-source";
    private static string Modern(string package = Package) => "{\"name\":\"Synthetic Publisher\",\"extensionList\":{\"extensions\":[{\"packageName\":\"" + package
        + "\",\"versionName\":\"9.9.999\",\"resources\":{\"jarUrl\":\"https://unrelated.invalid/artifact.jar\"}}]}}";
    private static Dictionary<string, (string Body, string? Resolved)> DiscoveryResponses(string body) => new(StringComparer.Ordinal)
    { [Index] = (body, null), [Readme] = ("# Source Code\n" + Repository + "\n", null) };
    private static JsonObject Discover(FakeFetch fetch, params string[] indexes) => new SourceDiscovery(fetch).DiscoverAsync(Extension(), indexes).GetAwaiter().GetResult();
    private static void ModernEndpoints()
    {
        foreach (var (configured, requested) in new[]
        {
            (Index, Index), ("https://github.com/publisher/extensions/raw/repo/index.pb", Index),
            ("https://github.com/publisher/extensions/raw/refs/heads/repo/index.json", Index),
            ("https://raw.githubusercontent.com/publisher/extensions/refs/heads/repo/index.json", Index),
            ("https://raw.githubusercontent.com/publisher/extensions/refs/heads/repo/index.pb", Index),
            ("https://raw.githubusercontent.com/publisher/extensions/repo/index.min.json", "https://raw.githubusercontent.com/publisher/extensions/repo/index.min.json"),
            ("https://raw.githubusercontent.com/publisher/extensions/repo/nested/index.json", "https://raw.githubusercontent.com/publisher/extensions/repo/nested/index.json")
        })
        {
            var responses = DiscoveryResponses(Modern()); responses[requested] = requested.EndsWith("min.json", StringComparison.Ordinal) ? ("[{\"pkg\":\"" + Package + "\"}]", null) : (Modern(), null);
            var fetch = new FakeFetch(responses); var input = Extension(); var before = input.ToJsonString();
            var result = new SourceDiscovery(fetch).DiscoverAsync(input, [configured]).GetAwaiter().GetResult();
            Check.Equal("known", result["state"]!.GetValue<string>()); Check.Equal(Repository, result["repository"]!.GetValue<string>());
            Check.Equal(configured, result["index_url"]!.GetValue<string>()); Check.Equal(Package, result["package"]!.GetValue<string>());
            Check.Equal("1.4.23", result["version"]!.GetValue<string>()); Check.Equal("", result["revision"]!.GetValue<string>());
            Check.Equal("repository-candidate", result["basis"]!.GetValue<string>()); Check.Equal(before, input.ToJsonString());
            Check.Equal(requested + "|" + Readme, string.Join('|', fetch.Requests.Select(r => r.Url.AbsoluteUri)));
        }
    }
    private static void InlineMembership()
    {
        foreach (var body in new[]
        {
            Modern(Package + ".other"), Modern(Package.ToUpperInvariant()), "{}", "null", "42", "[]",
            "{\"extensionListUrl\":\"https://unrelated.invalid/list.json\"}",
            "{\"extensionList\":{\"extensions\":{\"packageName\":\"" + Package + "\"}}}",
            "{\"extensionList\":{\"extensions\":[null,true,{}, {\"packageName\":123},{\"pkg\":\"" + Package + "\"}]}}",
            "[null,true,\"pkg\",{\"pkg\":123},{\"packageName\":\"" + Package + "\"}]",
            "{\"extensionList\":{\"extensions\":[{\"packageName\":\"" + Package + "\"}]},\"extensionListUrl\":\"https://unrelated.invalid/list.json\"}"
        })
        {
            var fetch = new FakeFetch(DiscoveryResponses(body)); var result = Discover(fetch, Index);
            Check.True(result["state"]!.GetValue<string>() == "needs-input", body); Check.Equal(1, fetch.Calls); Check.True(result["repository"] is null);
        }
        var rows = new JsonArray(Enumerable.Range(0, 12000).Select(_ => (JsonNode?)new JsonObject { ["packageName"] = "different.package.name" }).ToArray());
        rows.Add(new JsonObject { ["packageName"] = Package });
        var lastMatch = new JsonObject { ["extensionList"] = new JsonObject { ["extensions"] = rows } };
        Check.Equal("known", Discover(new FakeFetch(DiscoveryResponses(lastMatch.ToJsonString())), Index)["state"]!.GetValue<string>());
    }
    private static void PublisherScope()
    {
        foreach (var target in new[] { Index, Readme }) foreach (var resolved in new[]
        {
            "https://raw.githubusercontent.com/unrelated/extensions/repo/" + (target == Index ? "index.json" : "README.md"),
            "https://raw.githubusercontent.com/publisher/other/repo/" + (target == Index ? "index.json" : "README.md"),
            "https://raw.githubusercontent.com/unrelated/extensions/refs/heads/repo/" + (target == Index ? "index.json" : "README.md"),
            "https://raw.githubusercontent.com/publisher/other/refs/heads/repo/" + (target == Index ? "index.json" : "README.md"),
            "https://github.com/publisher/extensions/blob/repo/README.md", "https://user:secret@raw.githubusercontent.com/publisher/extensions/repo/README.md",
            "http://raw.githubusercontent.com/publisher/extensions/repo/README.md", "https://raw.githubusercontent.com/publisher/extensions/repo/README.md?private=1"
        })
        {
            var responses = DiscoveryResponses(Modern()); responses[target] = (responses[target].Body, resolved);
            var fetch = new FakeFetch(responses); Check.Equal("needs-input", Discover(fetch, Index)["state"]!.GetValue<string>());
            Check.Equal(target == Index ? 1 : 2, fetch.Calls);
        }
        foreach (var (resolvedIndex, resolvedReadme) in new[]
        {
            ("https://raw.githubusercontent.com/PUBLISHER/EXTENSIONS/new-branch/index.json", "https://raw.githubusercontent.com/PUBLISHER/EXTENSIONS/new-branch/README.md"),
            ("https://github.com/publisher/extensions/raw/refs/heads/new-branch/index.json", "https://github.com/publisher/extensions/raw/new-branch/README.md"),
            ("https://raw.githubusercontent.com/publisher/extensions/refs/heads/new-branch/index.json", "https://raw.githubusercontent.com/publisher/extensions/refs/heads/new-branch/README.md")
        })
        {
            var responses = DiscoveryResponses(Modern()); responses[Index] = (Modern(), resolvedIndex); responses[Readme] = (responses[Readme].Body, resolvedReadme);
            Check.Equal("known", Discover(new FakeFetch(responses), Index)["state"]!.GetValue<string>());
        }
    }
    private static void ModernDeclarations()
    {
        const string otherIndex = "https://raw.githubusercontent.com/other/extensions/repo/index.json", otherReadme = "https://raw.githubusercontent.com/other/extensions/repo/README.md";
        var responses = DiscoveryResponses(Modern()); responses[otherIndex] = (Modern(), null); responses[otherReadme] = ("# Source Repository\nhttps://github.com/other/original-source\n", null);
        var ambiguous = Discover(new FakeFetch(responses), Index, otherIndex); Check.Equal("needs-input", ambiguous["state"]!.GetValue<string>());
        Check.True(ambiguous["reason"]!.GetValue<string>().Contains("Multiple", StringComparison.Ordinal));
        foreach (var declaration in new[] { "# About\n" + Repository, "# Source Code\nhttps://github.com/unrelated/source", "# Source Code\n" + Repository + "\nhttps://github.com/publisher/other-source" })
        { responses[Readme] = (declaration, null); Check.Equal("needs-input", Discover(new FakeFetch(responses), Index)["state"]!.GetValue<string>()); }
        responses[Readme] = ("# Source Code\n" + Repository + "\n" + Repository + ".git\nhttps://github.com/PUBLISHER/ORIGINAL-SOURCE\n", null);
        Check.Equal("known", Discover(new FakeFetch(responses), Index)["state"]!.GetValue<string>());
        var declared = new FakeFetch(DiscoveryResponses("{}"));
        Check.Equal("known", new SourceDiscovery(declared).DiscoverAsync(Extension(Index), []).GetAwaiter().GetResult()["state"]!.GetValue<string>());
        Check.Equal(Readme, declared.Requests.Single().Url.AbsoluteUri);
    }
    private static void DiscoveryBounds()
    {
        foreach (var body in new[] { "", new string(' ', 3 * 1024 * 1024 + 1), "{", "{\"extensionList\":" + new string('[', 33) + "0" + new string(']', 33) + "}" })
        {
            var fetch = new FakeFetch(DiscoveryResponses(body)); var result = Discover(fetch, Index);
            Check.Equal("needs-input", result["state"]!.GetValue<string>()); Check.Equal(1, result["errors"]!.AsArray().Count); Check.Equal(1, fetch.Calls);
            if (body.Length == 0 || body.Length > 3 * 1024 * 1024) Check.Equal("InvalidDataException", result["errors"]![0]!.GetValue<string>());
        }
        var exact = Modern(); exact += new string(' ', 3 * 1024 * 1024 - Encoding.UTF8.GetByteCount(exact));
        var bounded = new FakeFetch(DiscoveryResponses(exact)); Check.Equal("known", Discover(bounded, Index)["state"]!.GetValue<string>());
        foreach (var request in bounded.Requests) { Check.Equal(TimeSpan.FromSeconds(8), request.Timeout); Check.Equal(request.Url.AbsoluteUri == Readme ? 128 * 1024 : 3 * 1024 * 1024, request.MaximumBytes); Check.True(request.Accept is null); }
        var overReadme = DiscoveryResponses(Modern()); overReadme[Readme] = (new string('x', 128 * 1024 + 1), null);
        var readmeFailure = Discover(new FakeFetch(overReadme), Index); Check.Equal("InvalidDataException", readmeFailure["errors"]![0]!.GetValue<string>());
        var urls = Enumerable.Range(0, 6).Select(i => "https://raw.githubusercontent.com/publisher" + i + "/extensions/repo/index.json").ToArray();
        var capped = new FakeFetch(urls.ToDictionary(url => url, _ => ("{}", (string?)null), StringComparer.Ordinal));
        Check.Equal(5, Discover(capped, urls)["checked"]!.GetValue<int>()); Check.Equal(5, capped.Calls);
        var noFallback = new FakeFetch(new()) { Operation = (_, _) => throw new PublicSourceFetchException(404) };
        Check.Equal("needs-input", Discover(noFallback, Index)["state"]!.GetValue<string>()); Check.Equal(1, noFallback.Calls);
        const string followingIndex = "https://raw.githubusercontent.com/following/extensions/repo/index.json", followingReadme = "https://raw.githubusercontent.com/following/extensions/repo/README.md";
        var following = new FakeFetch(new()
        {
            [Index] = ("", null), [followingIndex] = (Modern(), null), [followingReadme] = ("# Source Repository\nhttps://github.com/following/original-source\n", null)
        });
        Check.Equal("https://github.com/following/original-source", Discover(following, Index, followingIndex)["repository"]!.GetValue<string>());
        Check.Equal(Index + "|" + followingIndex + "|" + followingReadme, string.Join('|', following.Requests.Select(r => r.Url.AbsoluteUri)));
        var badEncoding = new FakeFetch(new()) { Operation = (request, _) => Task.FromResult(new PublicSourceResponse([0xc3, 0x28], request.Url)) };
        Check.Equal("needs-input", Discover(badEncoding, Index)["state"]!.GetValue<string>()); Check.Equal(1, badEncoding.Calls);
        var invalid = Extension(); invalid["pkgName"] = "not a package"; var unused = new FakeFetch(new());
        Check.Throws<InvalidDataException>(() => new SourceDiscovery(unused).DiscoverAsync(invalid, [Index]).GetAwaiter().GetResult()); Check.Equal(0, unused.Calls);
        using var cancel = new CancellationTokenSource(); var cancelled = new FakeFetch(DiscoveryResponses(Modern()))
        { Operation = (request, _) => { cancel.Cancel(); return Task.FromResult(new PublicSourceResponse(Encoding.UTF8.GetBytes(Modern()), request.Url)); } };
        Check.Throws<OperationCanceledException>(() => new SourceDiscovery(cancelled).DiscoverAsync(Extension(), [Index], cancel.Token).GetAwaiter().GetResult());
        Check.Equal(1, cancelled.Calls);
    }
    private sealed class FakeFetch(Dictionary<string, (string Body, string? Resolved)> responses) : IPublicSourceFetcher
    {
        public int Calls;
        public List<PublicSourceRequest> Requests { get; } = [];
        public Func<PublicSourceRequest, CancellationToken, Task<PublicSourceResponse>>? Operation;
        public Task<PublicSourceResponse> FetchAsync(PublicSourceRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Requests.Add(request); cancellationToken.ThrowIfCancellationRequested();
            if (Operation is not null) return Operation(request, cancellationToken);
            if (!responses.TryGetValue(request.Url.AbsoluteUri, out var value)) throw new IOException("No declared fixture response");
            return Task.FromResult(new PublicSourceResponse(Encoding.UTF8.GetBytes(value.Body), new(value.Resolved ?? request.Url.AbsoluteUri)));
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
