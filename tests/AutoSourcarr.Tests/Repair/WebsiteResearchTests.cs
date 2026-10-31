using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair;

namespace AutoSourcarr.Tests;

public static class WebsiteResearchTests
{
    private static JsonObject Fixtures() => JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("website-research.json")))!.AsObject();
    private static string Text(JsonObject row, string key) => WebsiteEvidence.Text(row, key);
    private static IEnumerable<JsonObject> Rows(JsonObject row, string key) => WebsiteEvidence.Rows(row, key);
    private static void Equal(JsonNode? expected, JsonNode? actual)
    {
        using var left = JsonDocument.Parse(WebsiteEvidence.Json(expected)); using var right = JsonDocument.Parse(WebsiteEvidence.Json(actual)); Check.JsonEqual(left.RootElement, right.RootElement);
    }
    private static T Await<T>(Task<T> task) => task.GetAwaiter().GetResult();
    private sealed class FixtureTransport(JsonObject fixture) : IWebsiteObservationTransport
    {
        public JsonArray Calls { get; } = [];
        public void RequireIsolation() { }
        public Task<WebsiteTransportResponse> ObserveAsync(WebsiteObservationRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Check.Equal(WebsiteObservationLimits.MaximumBody, request.MaximumBytes);
            Check.True(request.Timeout > TimeSpan.Zero && request.Timeout <= TimeSpan.FromSeconds(10)); Check.True(request.Accept is null or "application/vnd.github.sha");
            Calls.Add(new JsonObject { ["url"] = request.Url.AbsoluteUri, ["accept"] = request.Accept });
            var row = Rows(fixture, "routes").FirstOrDefault(row => Text(row, "url") == request.Url.AbsoluteUri)
                ?? new JsonObject { ["status"] = 404, ["body"] = Convert.ToBase64String("missing"u8) };
            if (WebsiteEvidence.Flag(row, "failure")) throw new IOException("Controlled connection failure");
            var headers = row["headers"] as JsonObject ?? new JsonObject();
            return Task.FromResult(new WebsiteTransportResponse(row["status"]!.GetValue<int>(), Text(headers, "Location"),
                Text(headers, "Content-Type") is { Length: > 0 } kind ? kind : "text/html", Text(headers, "Content-Encoding"), Convert.FromBase64String(Text(row, "body"))));
        }
    }
    private sealed class ControlledTransport(Func<WebsiteObservationRequest, CancellationToken, Task<WebsiteTransportResponse>> observe, Action? isolation = null) : IWebsiteObservationTransport
    {
        public void RequireIsolation() => isolation?.Invoke();
        public Task<WebsiteTransportResponse> ObserveAsync(WebsiteObservationRequest request, CancellationToken cancellationToken = default) => observe(request, cancellationToken);
    }
    private sealed class ResearchProbe(JsonObject fixture) : IWebsiteObservationClient
    {
        public JsonArray Calls { get; } = [];
        public Task<WebsiteObservationResponse> ResponseAsync(string url, string? accept = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls.Add(new JsonObject { ["url"] = url, ["accept"] = accept }); string body;
            if (url.Contains("/commits/HEAD", StringComparison.Ordinal)) body = new JsonObject { ["sha"] = Text(fixture, "head"), ["id"] = Text(fixture, "head") }.ToJsonString();
            else if (url.Contains("duckduckgo.com", StringComparison.Ordinal)) body = "<a class=\"result__a\" href=\"https://unverified.example/\">Search result</a>";
            else body = Text(fixture, "code");
            return Task.FromResult(new WebsiteObservationResponse(200, "", "text/plain", Encoding.UTF8.GetBytes(body), true));
        }
        public Task<JsonObject> OneAsync(string value, string? allowedOrigin = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(fixture["observed"] is JsonObject row ? (JsonObject)row.DeepClone() : new JsonObject { ["state"] = "working", ["final_url"] = value });
        public Task<JsonArray> CheckAsync(IEnumerable<JsonObject> sources, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    public static void Register(TestSuite suite)
    {
        suite.Add("website evidence: released public URLs, literal base addresses and HTML links", () =>
        {
            var fixtures = Fixtures();
            foreach (var row in Rows(fixtures, "urls"))
            {
                if (WebsiteEvidence.Flag(row, "error")) { Check.Throws<InvalidDataException>(() => WebsiteAddress.PublicUrl(Text(row, "input"))); continue; }
                Check.Equal(Text(row, "output"), WebsiteAddress.PublicUrl(Text(row, "input"))); Check.Equal(Text(row, "origin"), WebsiteAddress.Origin(Text(row, "input")));
            }
            foreach (var row in Rows(fixtures, "base_addresses")) Equal(row["expected"], WebsiteEvidence.Strings(AddressResearchEvidence.BaseAddresses(Text(row, "text"))));
            var markupIndex = 0;
            foreach (var row in Rows(fixtures, "markup"))
            {
                try { Equal(row["apis"], WebsiteEvidence.Strings(WebsiteMarkup.ApiLinks(Text(row, "text")))); }
                catch (Exception error) { throw new InvalidOperationException("Released API markup case " + markupIndex + ": " + Text(row, "text"), error); }
                try { Equal(row["search"], WebsiteEvidence.Strings(WebsiteMarkup.SearchLinks(Text(row, "text")))); }
                catch (Exception error) { throw new InvalidOperationException("Released search markup case " + markupIndex + ": " + Text(row, "text"), error); }
                markupIndex++;
            }
            Console.WriteLine("Released website URL/code/markup cases 51");
        });
        suite.Add("website probes: released redirect, API, content, challenge and uncertainty outcomes", () =>
        {
            var fixtures = Fixtures();
            foreach (var row in Rows(fixtures, "probes"))
            {
                try
                {
                    var transport = new FixtureTransport(row); using var probe = new WebsiteProbe(transport, pace: TimeSpan.Zero);
                    JsonNode actual = row["sources"] is JsonArray sources ? Await(probe.CheckAsync(sources.OfType<JsonObject>())) : Await(probe.OneAsync(Text(row, "url"), row["allowed_origin"]?.GetValue<string>()));
                    Equal(row["expected"], actual); Equal(row["calls"], transport.Calls);
                    Check.True(!WebsiteEvidence.Json(actual).Contains("COMPRESSED_CANARY", StringComparison.Ordinal));
                }
                catch (Exception error) { throw new InvalidOperationException("Released website probe case " + Text(row, "name"), error); }
            }
            Console.WriteLine("Released website probe cases 27");
        });
        suite.Add("address policy: corresponding origins, pinned official changes and automatic trust remain distinct", () =>
        {
            var fixtures = Fixtures();
            foreach (var row in Rows(fixtures, "policies"))
            {
                try
                {
                    var job = (JsonObject)row["job"]!.DeepClone(); var policy = new AddressPolicy(job, (_, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult((JsonArray)row["observations"]!.DeepClone()); });
                    JsonArray Run() => Await(policy.CheckAsync(Rows(row, "sources"), Rows(row, "previous"), WebsiteEvidence.Flag(row, "official")));
                    if (Text(row, "error") == "AddressBlocked") Check.Throws<AddressBlocked>(() => Run());
                    else if (Text(row, "error") == "AddressNeedsUpdate") Check.Throws<AddressNeedsUpdate>(() => Run());
                    else Equal(row["changes"], Run());
                    Equal(row["job_after"], job);
                }
                catch (Exception error) { throw new InvalidOperationException("Released address policy case " + Text(row, "name"), error); }
            }
            Console.WriteLine("Released address policy cases 15");
        });
        suite.Add("address research: pinned exact maintainer file and first-party evidence qualify candidates; search stays unverified", () =>
        {
            var fixtures = Fixtures();
            foreach (var row in Rows(fixtures, "research"))
            {
                try
                {
                    var inputs = (JsonObject)row["input"]!.DeepClone(); var before = inputs.DeepClone(); var probe = new ResearchProbe(row);
                    var result = Await(new AddressResearch(probe).ResearchAsync(inputs)); Equal(row["expected"], result); Equal(row["calls"], probe.Calls); Equal(before, inputs);
                    Check.True(!result.ContainsKey("verified") && !result.ContainsKey("working"));
                    foreach (var lead in Rows(result, "leads")) Check.Equal("unverified", Text(lead, "trust"));
                }
                catch (Exception error) { throw new InvalidOperationException("Released maintainer research case " + Text(row, "name"), error); }
            }
            Console.WriteLine("Released address research cases 15");
        });
        suite.Add("website probes: request, pacing and deadline budgets never retry or manufacture absence", () =>
        {
            var calls = 0; var now = TimeSpan.Zero;
            var transport = new ControlledTransport((_, _) => { calls++; return Task.FromResult(new WebsiteTransportResponse(200, "", "text/html", "identity", "<html>Catalog</html>"u8.ToArray())); });
            using (var probe = new WebsiteProbe(transport, pace: TimeSpan.Zero))
            {
                for (var index = 0; index < 16; index++) Check.Equal("working", Text(Await(probe.OneAsync("https://source.example/")), "state"));
                Check.Equal("inconclusive", Text(Await(probe.OneAsync("https://source.example/")), "state")); Check.Equal(16, calls); Check.Equal(16, probe.Requests);
            }
            calls = 0;
            using (var probe = new WebsiteProbe(transport, pace: TimeSpan.FromSeconds(2), deadline: TimeSpan.FromSeconds(3), clock: () => now,
                delay: (duration, token) => { token.ThrowIfCancellationRequested(); now += duration; return Task.CompletedTask; }))
            {
                Check.Equal("working", Text(Await(probe.OneAsync("https://source.example/")), "state"));
                Check.Equal("working", Text(Await(probe.OneAsync("https://source.example/")), "state"));
                Check.Equal("inconclusive", Text(Await(probe.OneAsync("https://source.example/")), "state")); Check.Equal(2, calls); Check.Equal(2, probe.Requests);
            }
        });
        suite.Add("website probes: isolation refusal, external cancellation and raw transport limits fail closed", () =>
        {
            var calls = 0;
            var denied = new ControlledTransport((_, _) => { calls++; throw new InvalidOperationException("Request must not run"); }, () => throw new InvalidOperationException("No qualified repair isolation"));
            using (var probe = new WebsiteProbe(denied, pace: TimeSpan.Zero)) Check.Throws<InvalidOperationException>(() => Await(probe.OneAsync("https://source.example/")));
            Check.Equal(0, calls);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            using (var probe = new WebsiteProbe(denied, pace: TimeSpan.Zero)) Check.Throws<OperationCanceledException>(() => Await(probe.OneAsync("https://source.example/", cancellationToken: cancelled.Token)));
            var large = new ControlledTransport((_, _) => Task.FromResult(new WebsiteTransportResponse(200, "", "text/html", "identity", new byte[WebsiteObservationLimits.MaximumBody + 1])));
            using (var probe = new WebsiteProbe(large, pace: TimeSpan.Zero)) Check.Equal("inconclusive", Text(Await(probe.OneAsync("https://source.example/")), "state"));
            var timeout = new ControlledTransport((_, _) => throw new TimeoutException("Controlled timeout"));
            using (var probe = new WebsiteProbe(timeout, pace: TimeSpan.Zero)) Check.Equal("inconclusive", Text(Await(probe.OneAsync("https://source.example/")), "state"));
            using (var probe = new WebsiteProbe(denied, pace: TimeSpan.Zero)) Check.Throws<ArgumentException>(() => Await(probe.ResponseAsync("https://source.example/", "text/private")));
        });
        suite.Add("address evidence: prompt bounds keep whole records and never silently truncate research", () =>
        {
            var job = new JsonObject { ["website_checks"] = new JsonArray(Enumerable.Range(0, 30).Select(index => (JsonNode)new JsonObject { ["state"] = "inconclusive", ["requested_url"] = "https://site" + index + ".example/", ["reason"] = new string('x', 100) }).ToArray()), ["address_targets"] = new JsonArray(), ["address_research"] = new JsonObject { ["note"] = AddressResearchEvidence.Note } };
            var before = job.DeepClone(); var text = AddressPolicy.PromptEvidence(job, 1000); var evidence = JsonNode.Parse(text)!.AsObject();
            Check.True(Encoding.UTF8.GetByteCount(text) <= 1000); Check.True(evidence["omitted"]!.GetValue<int>() > 0); Equal(before, job);
            Check.True(Rows(evidence, "website_checks").All(row => Text(row, "reason").Length == 100));
            job["address_research"] = new JsonObject { ["note"] = new string('x', 2000) };
            Check.Throws<InvalidDataException>(() => AddressPolicy.PromptEvidence(job, 1000));
            var inputs = (JsonObject)Rows(Fixtures(), "research").First()["input"]!.DeepClone(); inputs["extra"] = new string('x', 128 * 1024);
            Check.Throws<InvalidDataException>(() => Await(new AddressResearch(new ResearchProbe(new JsonObject())).ResearchAsync(inputs)));
        });
    }
}
