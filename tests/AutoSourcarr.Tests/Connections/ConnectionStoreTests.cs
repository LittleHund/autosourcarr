using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Connections;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests.Connections;

/// <summary>
/// Phase 7 connection store (CON-1, CON-5): connections in config.json, secrets encrypted and never listed, kept on
/// update by marker, optimistic revisions, per-kind validation, single-connection types, the read-only view of
/// settings that are really connections, and tests with a fresh handler, a deadline and a recorded last result.
/// </summary>
public static class ConnectionStoreTests
{
    private const string Secret = "sentinel-api-key-7f3c";

    public static void Register(TestSuite suite)
    {
        suite.Add("connections: add, update and delete keep secrets encrypted on disk, never listed, kept by marker", () =>
        {
            using var temp = new Sandbox();
            var registered = new Dictionary<string, string?>(StringComparer.Ordinal);
            var store = new ConnectionStore(temp.Path, Registry(), registerSecrets: values => { foreach (var (key, value) in values) registered[key] = value; });
            var revision = store.Revision();
            var added = store.Add(revision, "fake", " Solver one ", true, Values($$"""{"url":"http://solver:8191/","api_key":"{{Secret}}"}"""));
            var id = added.Connection!.Id;
            Check.True(id.Length == 12 && id.All(Uri.IsHexDigit), "12 hex id: " + id);
            Check.Equal("Solver one", added.Connection!.Label);
            Check.Equal("http://solver:8191", added.Connection!.Values["url"]!.GetValue<string>());
            Check.Equal("8191", added.Connection!.Values["port"]!.GetValue<string>()); // field default applied
            Check.Equal(true, added.Connection!.Values["api_key"]!["set"]!.GetValue<bool>());
            Check.True(!added.Connection!.Values.ToJsonString().Contains(Secret, StringComparison.Ordinal), "secret not returned on add");
            Check.True(added.Revision != revision, "revision changes");
            Check.True(registered.ContainsValue(Secret), "secret registered with the redaction");

            var disk = File.ReadAllText(Path.Combine(temp.Path, ConfigStore.FileName));
            Check.True(!disk.Contains(Secret, StringComparison.Ordinal), "secret not on disk in plain text");
            var stored = JsonNode.Parse(disk)!["connections"]!.AsArray().Single()!;
            Check.Equal(id, stored["id"]!.GetValue<string>());
            Check.Equal("fake", stored["type"]!.GetValue<string>());
            Check.True(stored["secrets"]!["api_key"]!.GetValue<string>().StartsWith("enc:v1:", StringComparison.Ordinal), "secret encrypted");
            Check.True(stored["values"]!["api_key"] is null, "secret not among plain values");
            Check.True(File.Exists(Path.Combine(temp.Path, SettingsSecrets.KeyFileName)), "config.key created");

            var listed = store.List();
            Check.Equal(added.Revision, listed.Revision);
            var view = listed.Connections.Single();
            Check.Equal("config", view.Source);
            Check.True(view.Editable && view.TypeKnown, "editable known type");
            Check.True(!view.Values.ToJsonString().Contains(Secret, StringComparison.Ordinal), "secret never listed");
            Check.Equal(Secret, store.Find(id)!.Value("api_key"));

            // Marker and empty text keep the secret; other fields change; label and enabled switch.
            var updated = store.Update(id, listed.Revision, "Solver", false, Values("""{"api_key":{"set":true},"url":"https://other.example/v1"}"""));
            Check.Equal(Secret, store.Find(id)!.Value("api_key"));
            Check.Equal("https://other.example/v1", store.Find(id)!.Value("url"));
            Check.Equal(false, updated.Connection!.Enabled);
            Check.Equal("Solver", updated.Connection!.Label);
            updated = store.Update(id, updated.Revision, null, null, Values("""{"api_key":""}"""));
            Check.Equal(Secret, store.Find(id)!.Value("api_key"));
            Check.Equal("Solver", store.Find(id)!.Label);
            updated = store.Update(id, updated.Revision, null, true, Values("""{"api_key":"replacement-key"}"""));
            Check.Equal("replacement-key", store.Find(id)!.Value("api_key"));
            Check.Equal(1, store.Enabled("fake").Count);

            // The ciphertext is bound to the connection id and field: moving it elsewhere does not decrypt.
            var moved = JsonNode.Parse(File.ReadAllText(Path.Combine(temp.Path, ConfigStore.FileName)))!.AsObject();
            moved["connections"]![0]!["id"] = "0123456789ab";
            File.WriteAllText(Path.Combine(temp.Path, ConfigStore.FileName), moved.ToJsonString());
            Check.Throws<ConfigurationException>(() => store.Find("0123456789ab"));
            moved["connections"]![0]!["id"] = id;
            File.WriteAllText(Path.Combine(temp.Path, ConfigStore.FileName), moved.ToJsonString());

            var afterDelete = store.Delete(id, store.Revision());
            Check.Equal(0, store.List().Connections.Count);
            Check.Equal(afterDelete, store.Revision());
            Check.Throws<ConnectionNotFoundException>(() => store.Delete(id, afterDelete));
            Check.True(JsonNode.Parse(File.ReadAllText(Path.Combine(temp.Path, ConfigStore.FileName)))!["connections"] is null, "empty list removed");

            // A new store over the same files registers saved secrets from the start.
            var again = store.Add(store.Revision(), "fake", null, true, Values("""{"url":"http://a","api_key":"startup-secret"}"""));
            Check.Equal("Fake fake", again.Connection!.Label); // type label when no name is given
            registered.Clear();
            _ = new ConnectionStore(temp.Path, Registry(), registerSecrets: values => { foreach (var (key, value) in values) registered[key] = value; });
            Check.True(registered.ContainsValue("startup-secret"), "saved secrets redacted at start");
        });

        suite.Add("connections: stale revision 409, single-connection types, validation by field kind", () =>
        {
            using var temp = new Sandbox();
            var store = new ConnectionStore(temp.Path, Registry());
            var first = store.Revision();
            var added = store.Add(first, "fake", "one", true, Values("""{"url":"http://one","api_key":"k"}"""));
            Check.Throws<SettingsConflictException>(() => store.Add(first, "fake", "two", true, Values("""{"url":"http://two","api_key":"k"}""")));
            Check.Throws<SettingsConflictException>(() => store.Update(added.Connection!.Id, first, "x", null, null));
            Check.Throws<SettingsConflictException>(() => store.Delete(added.Connection!.Id, first));
            Check.Throws<ConnectionNotFoundException>(() => store.Update("ffffffffffff", added.Revision, "x", null, null));

            var single = store.Add(added.Revision, "single", null, true, Values("""{"url":"http://suwayomi:4567"}"""));
            var refused = Check.Throws<SettingsValidationException>(() => store.Add(single.Revision, "single", null, true, Values("""{"url":"http://other"}""")));
            Check.True(refused.Message.Contains("one connection", StringComparison.Ordinal), refused.Message);

            var revision = single.Revision;
            foreach (var bad in new[]
                     {
                         """{"url":"ftp://solver","api_key":"k"}""", """{"url":"http://user:pw@solver","api_key":"k"}""", """{"url":"solver:8191","api_key":"k"}""",
                         """{"url":"http://solver","api_key":"k","port":"80a"}""", """{"url":"http://solver","api_key":"k","port":1.5}""",
                         """{"url":"http://solver","api_key":"k","verify":"maybe"}""", """{"url":"http://solver"}""", """{"api_key":"k"}""",
                         """{"url":"http://solver","api_key":{"set":true}}""", """{"url":"http://solver","api_key":"k","other":"x"}""",
                         """{"url":["http://solver"],"api_key":"k"}""", """{"url":"http://solver","api_key":"k","port":{"set":true}}""",
                         """{"url":"http://solver","api_key":"line\nbreak"}"""
                     })
                Check.Throws<SettingsValidationException>(() => store.Add(revision, "fake", null, true, Values(bad)));
            Check.Throws<SettingsValidationException>(() => store.Add(revision, "unknown", null, true, Values("{}")));
            Check.Throws<SettingsValidationException>(() => store.Add(revision, "fake", new string('x', 101), true, Values("""{"url":"http://a","api_key":"k"}""")));
            Check.Equal(revision, store.Revision()); // nothing written by refused requests

            var normalized = store.Add(revision, "fake", null, true, Values("""{"url":"http://solver/","api_key":" spaced ","port":8192,"verify":"on"}"""));
            var saved = store.Find(normalized.Connection!.Id)!;
            Check.Equal("http://solver", saved.Value("url"));
            Check.Equal(" spaced ", saved.Value("api_key")); // secrets are kept exactly
            Check.Equal("8192", saved.Value("port"));
            Check.Equal("true", saved.Value("verify"));
            // Clearing a required field is refused; clearing an optional one removes it.
            Check.Throws<SettingsValidationException>(() => store.Update(saved.Id, normalized.Revision, null, null, Values("""{"url":null}""")));
            store.Update(saved.Id, normalized.Revision, null, null, Values("""{"verify":null}"""));
            Check.True(!store.Find(saved.Id)!.Values.ContainsKey("verify"), "optional value cleared");
        });

        suite.Add("connections: settings that are connections are listed read-only as settings-<type>", () =>
        {
            using var temp = new Sandbox();
            var editor = new SettingsEditor(temp.Path, () => new AutoSourcarrSettings(), new Dictionary<string, string?>());
            editor.Save(Changes("""{"MAIN_SUWAYOMI_URL":"http://suwayomi:4567","MAIN_SUWAYOMI_USERNAME":"reader","MAIN_SUWAYOMI_PASSWORD":"settings-password"}"""), editor.Revision());
            var registered = new Dictionary<string, string?>(StringComparer.Ordinal);
            var types = new ConnectionTypes([new FakeConnectionType("fake", true), new FakeConnectionType("suwayomi", false, withUserFields: true)]);
            var store = new ConnectionStore(temp.Path, types, registerSecrets: values => { foreach (var (key, value) in values) registered[key] = value; });
            var view = store.List().Connections.Single();
            Check.Equal("settings-suwayomi", view.Id);
            Check.Equal("settings", view.Source);
            Check.True(!view.Editable, "settings connections are read-only here");
            Check.Equal("http://suwayomi:4567", view.Values["url"]!.GetValue<string>());
            Check.Equal("reader", view.Values["username"]!.GetValue<string>());
            Check.Equal(false, view.Values["api_key"]!["set"]!.GetValue<bool>());
            Check.Equal(true, view.Values["password"]!["set"]!.GetValue<bool>());
            Check.True(!view.Values.ToJsonString().Contains("settings-password", StringComparison.Ordinal), "settings secret not listed");
            Check.Equal("settings-password", store.Find("settings-suwayomi")!.Value("password"));
            Check.Throws<SettingsConflictException>(() => store.Update("settings-suwayomi", store.Revision(), "x", null, null));
            Check.Throws<SettingsConflictException>(() => store.Delete("settings-suwayomi", store.Revision()));
            Check.Equal(1, store.Enabled("suwayomi").Count);

            // A settings save keeps the connections array untouched; a connection change keeps the settings.
            var added = store.Add(store.Revision(), "fake", null, true, Values("""{"url":"http://solver","api_key":"k"}"""));
            editor.Save(Changes("""{"MAIN_SUWAYOMI_USERNAME":"other"}"""), editor.Revision());
            Check.Equal(added.Revision, store.Revision());
            Check.Equal("k", store.Find(added.Connection!.Id)!.Value("api_key"));
            Check.Equal("other", editor.SavedSettings()["MAIN_SUWAYOMI_USERNAME"]);
            Check.Equal("settings-password", editor.SavedSettings()["MAIN_SUWAYOMI_PASSWORD"]);

            // Effective values from the running app win over config.json (Compose variables).
            var live = new ConnectionStore(temp.Path, types, settingValues: () => new Dictionary<string, string?> { ["MAIN_SUWAYOMI_URL"] = "http://compose:4567" });
            Check.Equal("http://compose:4567", live.Find("settings-suwayomi")!.Value("url"));
            // Types that are not registered show no settings view.
            Check.Equal(1, new ConnectionStore(temp.Path, new ConnectionTypes([new FakeConnectionType("fake", true)])).List().Connections.Count);
        });

        suite.Add("connections: tests use a fresh handler each, a deadline, and keep the last result in the data folder", () =>
        {
            using var temp = new Sandbox();
            var data = Path.Combine(temp.Path, "data");
            var handlers = new List<StubHandler>();
            HttpMessageHandler Factory()
            {
                var handler = new StubHandler(request => request.Headers.TryGetValues("X-Api-Key", out var keys) && keys.Single() == Secret
                    ? Json(HttpStatusCode.OK, """{"version":"3.4.0"}""") : Json(HttpStatusCode.Unauthorized, "{}"));
                handlers.Add(handler);
                return handler;
            }
            var hang = new FakeConnectionType("hang", true, test: async (_, _, _) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None); // ignores the token on purpose
                return ConnectionTestResult.Pass("late");
            });
            var types = new ConnectionTypes([new FakeConnectionType("fake", true), hang]);
            var store = new ConnectionStore(temp.Path, types, data, handlerFactory: Factory, testDeadline: TimeSpan.FromMilliseconds(300));
            var added = store.Add(store.Revision(), "fake", null, true, Values($$"""{"url":"http://solver:8191","api_key":"{{Secret}}"}"""));
            var id = added.Connection!.Id;

            var result = store.TestAsync(id, CancellationToken.None).GetAwaiter().GetResult();
            Check.True(result.Ok, result.Message);
            Check.Equal("3.4.0", result.Details!["version"]!.GetValue<string>());
            Check.Equal(1, handlers.Count);
            Check.True(handlers[0].Disposed, "handler disposed after the test");
            Check.Equal("http://solver:8191/health", handlers[0].Requests.Single());
            var last = store.LastTest(id)!;
            Check.True(last.Ok && last.Message == result.Message, "last test kept");
            Check.True(store.List().Connections.Single().LastTest is { Ok: true }, "last test listed");
            Check.True(File.Exists(Path.Combine(data, ConnectionStore.LastTestsFileName)), "last tests saved in the data folder");
            Check.True(!File.ReadAllText(Path.Combine(data, ConnectionStore.LastTestsFileName)).Contains(Secret, StringComparison.Ordinal), "no secret in the results file");
            // A new store reads the results back.
            Check.True(new ConnectionStore(temp.Path, types, data).LastTest(id) is { Ok: true }, "last test survives a restart");

            // Unsaved values: a wrong key fails; the marker with the saved connection's id uses the saved secret; nothing is recorded.
            var wrong = store.TestValuesAsync("fake", Values("""{"url":"http://solver:8191","api_key":"wrong"}"""), null, CancellationToken.None).GetAwaiter().GetResult();
            Check.True(!wrong.Ok, "wrong key fails");
            var kept = store.TestValuesAsync("fake", Values("""{"url":"http://solver:8191","api_key":{"set":true}}"""), id, CancellationToken.None).GetAwaiter().GetResult();
            Check.True(kept.Ok, kept.Message);
            Check.Equal(3, handlers.Count);
            Check.True(handlers.All(handler => handler.Disposed), "every handler disposed");
            Check.True(store.LastTest(id)!.Ok, "unsaved tests do not overwrite the last result");
            Check.Throws<SettingsValidationException>(() => store.TestValuesAsync("fake", Values("""{"url":"bad"}"""), null, CancellationToken.None).GetAwaiter().GetResult());

            // A type that ignores cancellation is still bounded by the deadline.
            var slow = store.Add(store.Revision(), "hang", null, true, Values("""{"url":"http://slow","api_key":"k"}"""));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var timedOut = store.TestAsync(slow.Connection!.Id, CancellationToken.None).GetAwaiter().GetResult();
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(3), "bounded by the deadline: " + watch.Elapsed);
            Check.True(!timedOut.Ok && timedOut.Message.Contains("No answer within", StringComparison.Ordinal), timedOut.Message);
            Check.True(store.LastTest(slow.Connection!.Id) is { Ok: false }, "failed result recorded");

            // An exception in a type becomes a failed result; deleting forgets the last result.
            var throwing = new ConnectionStore(temp.Path, new ConnectionTypes([new FakeConnectionType("fake", true, test: (_, _, _) => throw new HttpRequestException("refused"))]), data);
            var failed = throwing.TestAsync(id, CancellationToken.None).GetAwaiter().GetResult();
            Check.True(!failed.Ok && failed.Message.Contains("refused", StringComparison.Ordinal), failed.Message);
            store = new ConnectionStore(temp.Path, types, data);
            store.Delete(id, store.Revision());
            Check.True(store.LastTest(id) is null, "deleted connection forgets its test");
            Check.Throws<ConnectionNotFoundException>(() => store.TestAsync(id, CancellationToken.None).GetAwaiter().GetResult());
        });

        suite.Add("connections: the registry refuses duplicate and malformed types", () =>
        {
            Check.Throws<ArgumentException>(() => new ConnectionTypes([new FakeConnectionType("fake", true), new FakeConnectionType("fake", false)]));
            Check.Throws<ArgumentException>(() => new ConnectionTypes([new FakeConnectionType("Bad Type", true)]));
            var types = new ConnectionTypes([new FakeConnectionType("fake", true), new FakeConnectionType("single", false)]);
            Check.Equal("single", types.Find("single")!.Type);
            Check.True(types.Find("other") is null && types.Find(null) is null, "unknown types are null");
            Check.Equal("fake,single", string.Join(',', types.All.Select(type => type.Type)));
        });
    }

    internal static ConnectionTypes Registry() =>
        new([new FakeConnectionType("fake", true), new FakeConnectionType("single", false, urlOnly: true)]);

    internal static JsonObject Values(string json) => JsonNode.Parse(json)!.AsObject();

    private static Dictionary<string, JsonElement> Changes(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }

    internal static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>A connection type for tests: GET {url}/health with the API key; details carry the reported version.</summary>
internal sealed class FakeConnectionType : IConnectionType
{
    private readonly Func<ConnectionConfig, HttpMessageHandler, CancellationToken, Task<ConnectionTestResult>>? _test;

    public FakeConnectionType(string type, bool multiple, bool urlOnly = false, bool withUserFields = false,
        Func<ConnectionConfig, HttpMessageHandler, CancellationToken, Task<ConnectionTestResult>>? test = null)
    {
        Type = type;
        Multiple = multiple;
        _test = test;
        var fields = new List<ConnectionField> { new("url", "Address", ConnectionFieldKind.Url, true, Help: "Where the service answers.") };
        if (!urlOnly)
        {
            fields.Add(new("api_key", "API key", ConnectionFieldKind.Secret, !withUserFields));
            fields.Add(new("port", "Port", ConnectionFieldKind.Integer, false, "8191"));
            fields.Add(new("verify", "Verify", ConnectionFieldKind.Boolean, false));
        }
        if (withUserFields)
        {
            fields.Add(new("username", "User name", ConnectionFieldKind.Text, false));
            fields.Add(new("password", "Password", ConnectionFieldKind.Secret, false));
        }
        Fields = fields;
    }

    public string Type { get; }
    public string Label => "Fake " + Type;
    public string Purpose => "A stand-in service for tests.";
    public bool Multiple { get; }
    public IReadOnlyList<ConnectionField> Fields { get; }

    public async Task<ConnectionTestResult> TestAsync(ConnectionConfig connection, HttpMessageHandler http, CancellationToken cancellationToken)
    {
        if (_test is not null)
            return await _test(connection, http, cancellationToken).ConfigureAwait(false);
        using var client = new HttpClient(http, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, connection.Value("url") + "/health");
        request.Headers.Add("X-Api-Key", connection.Value("api_key"));
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return ConnectionTestResult.Fail("The service refused the key (HTTP " + (int)response.StatusCode + ").");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!.AsObject();
        return ConnectionTestResult.Pass("Connected.", new JsonObject { ["version"] = body["version"]!.GetValue<string>() });
    }
}

/// <summary>Answers every request from a function and records the request URLs; remembers being disposed.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    public bool Disposed { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
            Requests.Add(request.RequestUri!.ToString());
        return Task.FromResult(respond(request));
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
