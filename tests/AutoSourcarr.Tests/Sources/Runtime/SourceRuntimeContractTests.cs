using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources.Runtime;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Tests;

public static class SourceRuntimeContractTests
{
    private const string Base = "http://fixture-engine.invalid";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T12:34:56.123456+00:00", CultureInfo.InvariantCulture);
    public static void Register(TestSuite suite)
    {
        suite.Add("source runtime contract: 130 released isolated dotenv/UA environment cases", EnvironmentOracle);
        suite.Add("source runtime contract: 108 released intended-vs-launch solver environment cases", LaunchOracle);
        suite.Add("source runtime contract: 108 released capability cases use controlled HTTP and persistent SQLite", () => CapabilityOracle().GetAwaiter().GetResult());
        suite.Add("source runtime contract: environment is read-only, immutable and registers actual source secrets", EnvironmentPrivacy);
        suite.Add("source runtime contract: missing config creates nothing and malformed/oversized browser preferences fail", EnvironmentBounds);
        suite.Add("source runtime contract: Python container repr preserves UA quotes/order/controls/hash and printable Unicode rejection", PythonRepresentations);
        suite.Add("source runtime contract: native projection retains explicit fixed-image intent, privacy and solver separation", NativeProjection);
        suite.Add("source runtime contract: native projection refuses conflicting image values and extra loader keys", NativeProjectionRefusals);
        suite.Add("source runtime contract: explicit configured credentials stay within one engine identity", () => Identity().GetAwaiter().GetResult());
        suite.Add("source runtime contract: incomplete about/schema observations retain the prior saved report", () => InvalidReports().GetAwaiter().GetResult());
        suite.Add("source runtime contract: about redirects, encodings and bytes remain bounded to configured identity", () => AboutBounds().GetAwaiter().GetResult());
        suite.Add("source runtime contract: cancelled about observation joins physical HTTP and never saves", () => Cancellation().GetAwaiter().GetResult());
        suite.Add("source runtime contract: shutdown joins delayed GraphQL and leaves caller-owned client intact", () => Shutdown().GetAwaiter().GetResult());
        suite.Add("source runtime contract: local engine deferral survives without a persisted missing-capability report", () => LocalDeferral().GetAwaiter().GetResult());
        suite.Add("source runtime contract: upstream error text and explicit credentials remain private", () => Privacy().GetAwaiter().GetResult());
    }

    private static void EnvironmentOracle()
    {
        var count = 0;
        foreach (var row in Fixture()["environment"]!.AsArray().OfType<JsonObject>())
        {
            using var temp = new Sandbox(); WriteEnvironment(temp.Path, row);
            var data = Path.Combine(temp.Path, "source-data"); var builder = new SourceEngineEnvironment(temp.Path, data, new SupportPrivacy());
            var parent = row["parent"]!.AsObject().ToDictionary(pair => pair.Key, pair => (string?)Text(pair.Value), StringComparer.Ordinal);
            if (!row["valid"]!.GetValue<bool>()) Check.Throws<ArgumentException>(() => builder.Build(parent));
            else
            {
                var result = builder.Build(parent).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                if (result.ContainsKey("AUTOSOURCARR_SOURCE_UA_STATUS"))
                {
                    Check.Equal(Path.Combine(data, "cache", "autosourcarr-user-agent.properties"), result["AUTOSOURCARR_SOURCE_UA_STATUS"]);
                    result["AUTOSOURCARR_SOURCE_UA_STATUS"] = "<source-data>/cache/autosourcarr-user-agent.properties";
                }
                Equal(row["result"], JsonSerializer.SerializeToNode(result));
            }
            Check.True(!Directory.Exists(data)); count++;
        }
        Check.Equal(130, count);
    }

    private static void LaunchOracle()
    {
        var count = 0;
        foreach (var row in Fixture()["launch"]!.AsArray().OfType<JsonObject>())
        {
            var environment = row["environment"]!.AsObject().ToDictionary(pair => pair.Key, pair => Text(pair.Value), StringComparer.Ordinal);
            var settings = new AutoSourcarrSettings { AppServicesMode = row["integrated"]!.GetValue<bool>() ? "integrated" : "external", SourceAutoChallenges = row["automatic"]!.GetValue<bool>(), SourceRequestTimeout = row["request_timeout"]!.GetValue<double>() };
            var result = SourceEngineEnvironment.PrepareLaunch(environment, settings);
            Equal(row["result"], JsonSerializer.SerializeToNode(new { launch = result.Launch, intended = result.IntendedChallenges }));
            Equal(row["environment"], JsonSerializer.SerializeToNode(environment)); count++;
        }
        Check.Equal(108, count);
    }

    private static async Task CapabilityOracle()
    {
        using var setup = new DatabaseSetup(); JsonObject current = new();
        await using var client = new SuwayomiClient(new AutoSourcarrSettings(), Base, handler: new Handler(async (request, token) =>
        {
            Check.Equal(HttpMethod.Post, request.Method); Check.Equal("/api/graphql", request.RequestUri!.AbsolutePath);
            var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!; Check.Equal(SourceEngineCapabilityContract.Query, Text(payload["query"]));
            return Response(new JsonObject { ["data"] = current["schema"]!.DeepClone() });
        }));
        await using var service = Contract(setup, client, new Handler((request, _) =>
        {
            Check.Equal("/api/v1/settings/about", request.RequestUri!.AbsolutePath); return Task.FromResult(Response(current["about"]));
        }));
        var count = 0;
        foreach (var row in Fixture()["capabilities"]!.AsArray().OfType<JsonObject>())
        {
            current = row; setup.Settings.Set(SourceEngineCapabilityContract.SettingKey, new JsonObject());
            if (row["valid"]!.GetValue<bool>()) Equal(row["result"], await service.CheckAsync());
            else await Throws<SourceEngineCapabilityException>(() => service.CheckAsync());
            Equal(row["status"], service.Status()); Check.True(!service.Status()["pin_promoted"]!.GetValue<bool>()); count++;
        }
        Check.Equal(108, count);
    }

    private static void EnvironmentPrivacy()
    {
        using var temp = new Sandbox(); var privacy = new SupportPrivacy();
        const string content = "# keep literal\nSOURCE_PASSWORD=synthetic-source-private\nJAVA_HOME=/build/jdk21\nJAVA_TOOL_OPTIONS=-javaagent:/untrusted.jar\nLD_PRELOAD=bad\nAUTOSOURCARR_SOURCE_USER_AGENT=bad\nPYTHON_EVIL=bad\n";
        File.WriteAllText(Path.Combine(temp.Path, "source-host.env"), content);
        var parent = new Dictionary<string, string?> { ["HOME"] = "/home/suwayomi", ["JAVA_HOME"] = "/image/jvm25", ["ADMIN_TOKEN"] = "synthetic-parent-private", ["AUTOSOURCARR_BUILD_JAVA_HOME"] = "/build/jdk21" };
        var builder = new SourceEngineEnvironment(temp.Path, Path.Combine(temp.Path, "profile"), privacy); var result = builder.BuildLaunch(parent, new AutoSourcarrSettings { AppServicesMode = "integrated" });
        Check.Equal("/image/jvm25", result.Launch["JAVA_HOME"]); Check.Equal("none", result.Launch["AUTH_MODE"]); Check.Equal("127.0.0.1", result.Launch["BIND_IP"]); Check.Equal("4567", result.Launch["BIND_PORT"]);
        Check.True(!result.Launch.ContainsKey("JAVA_TOOL_OPTIONS") && !result.Launch.ContainsKey("ADMIN_TOKEN") && !result.Launch.ContainsKey("LD_PRELOAD"));
        Check.Equal("[redacted]", privacy.Text("synthetic-source-private")); Check.Equal(content, File.ReadAllText(Path.Combine(temp.Path, "source-host.env")));
        Check.Throws<NotSupportedException>(() => ((IDictionary<string, string>)result.Launch).Add("MUTATED", "bad"));
        Check.Equal("false", result.Launch["FLARESOLVERR_ENABLED"]); Check.Equal("true", result.IntendedChallenges["FLARESOLVERR_ENABLED"]);
        Check.Equal("2.3.2243", SourceEngineEnvironment.EngineVersion); Check.Equal(1, SourceEngineEnvironment.SolverTimeout("0_1", 45));
    }

    private static Dictionary<string, string?> NativeImageParent() => new(StringComparer.Ordinal)
    {
        ["HOME"] = "/home/suwayomi", ["PATH"] = "/usr/bin:/bin", ["JAVA_HOME"] = "/opt/java/openjdk", ["LANG"] = "C.UTF-8",
        ["TZ"] = "UTC", ["SSL_CERT_FILE"] = "/etc/ssl/certs/ca-certificates.crt", ["ADMIN_TOKEN"] = "synthetic-app-only-token"
    };
    private static void NativeProjection()
    {
        using var temp = new Sandbox(); var privacy = new SupportPrivacy(); var data = Path.Combine(temp.Path, "profile");
        const string content = "SOURCE_PASSWORD=synthetic-native-source-secret\nFLARESOLVERR_ENABLED=true\n";
        var configuration = Path.Combine(temp.Path, "source-host.env"); File.WriteAllText(configuration, content);
        var builder = new SourceEngineEnvironment(temp.Path, data, privacy); var parent = NativeImageParent();
        var before = JsonSerializer.Serialize(parent); var settings = new AutoSourcarrSettings { AppServicesMode = "integrated" };
        var original = builder.BuildLaunch(parent, settings);
        // The legacy builder's fixed system fields cannot be sent as native overrides.
        Check.Throws<InvalidDataException>(() => NativeSourceOwnerProtocol.EncodeCommand(new byte[32], 1, NativeSourceOwnerOpcode.Start, original.Launch.ToArray()));
        var projected = builder.BuildNativeLaunch(parent, settings);
        foreach (var key in new[] { "HOME", "PATH", "JAVA_HOME", "LANG", "ADMIN_TOKEN" }) Check.True(!projected.Launch.ContainsKey(key));
        var frame = NativeSourceOwnerProtocol.EncodeCommand(new byte[32], 1, NativeSourceOwnerOpcode.Start, projected.Launch.ToArray());
        try
        {
            var command = NativeSourceOwnerProtocol.DecodeCommand(frame, new byte[32], 1);
            Check.True(command.Environment.SequenceEqual(projected.Launch));
            Check.Equal("false", projected.Launch["FLARESOLVERR_ENABLED"]); Check.Equal("true", projected.IntendedChallenges["FLARESOLVERR_ENABLED"]);
            Check.Equal("UTC", projected.Launch["TZ"]); Check.Equal("127.0.0.1", projected.Launch["BIND_IP"]);
            Check.Equal("/etc/ssl/certs/ca-certificates.crt", projected.Launch["SSL_CERT_FILE"]);
            Equal(JsonSerializer.SerializeToNode(original.IntendedChallenges), JsonSerializer.SerializeToNode(projected.IntendedChallenges));
            Check.Equal("[redacted]", privacy.Text("synthetic-native-source-secret"));
            Check.Equal(before, JsonSerializer.Serialize(parent)); Check.Equal(content, File.ReadAllText(configuration)); Check.True(!Directory.Exists(data));
            Check.Throws<NotSupportedException>(() => ((IDictionary<string, string>)projected.Launch).Add("MUTATED", "bad"));
        }
        finally { CryptographicOperations.ZeroMemory(frame); }
        // Missing inherited system fields use the same explicit fixed native defaults.
        _ = builder.BuildNativeLaunch(new Dictionary<string, string?>(), settings);
    }
    private static void NativeProjectionRefusals()
    {
        using var temp = new Sandbox(); var builder = new SourceEngineEnvironment(temp.Path, Path.Combine(temp.Path, "profile"), new SupportPrivacy());
        var settings = new AutoSourcarrSettings { AppServicesMode = "integrated" };
        foreach (var key in new[] { "HOME", "PATH", "JAVA_HOME", "LANG" })
        {
            var parent = NativeImageParent(); parent[key] = "synthetic-conflicting-private-value";
            var error = Check.Throws<InvalidDataException>(() => builder.BuildNativeLaunch(parent, settings));
            Check.True(error.Message.Contains(key, StringComparison.Ordinal)); Check.True(!error.Message.Contains("synthetic-conflicting-private-value", StringComparison.Ordinal));
            Check.Equal("synthetic-conflicting-private-value", parent[key]);
        }
        foreach (var key in new[] { "LD_AUDIT", "DOTNET_STARTUP_HOOKS", "CORECLR_ENABLE_PROFILING", "COMPlus_EnableDiagnostics", new string('X', 129) })
        {
            var content = key + "=synthetic-loader-private-value\n"; var path = Path.Combine(temp.Path, "source-host.env"); File.WriteAllText(path, content);
            var error = Check.Throws<InvalidDataException>(() => builder.BuildNativeLaunch(NativeImageParent(), settings));
            Check.True(!error.Message.Contains("synthetic-loader-private-value", StringComparison.Ordinal)); Check.Equal(content, File.ReadAllText(path));
        }
        Check.Throws<InvalidOperationException>(() => builder.BuildNativeLaunch(NativeImageParent(), settings with { SourceEngineMode = "external" }));
    }

    private static void EnvironmentBounds()
    {
        using var temp = new Sandbox(); var missing = Path.Combine(temp.Path, "missing");
        var builder = new SourceEngineEnvironment(missing, Path.Combine(temp.Path, "profile"), new SupportPrivacy());
        Check.Equal("true", builder.Build(new Dictionary<string, string?>())["KCEF_ENABLED"]); Check.True(!Directory.Exists(missing));
        builder = new(temp.Path, Path.Combine(temp.Path, "profile"), new SupportPrivacy()); var preferences = Path.Combine(temp.Path, "source-browser.json");
        File.WriteAllText(preferences, "{"); Check.Throws<ArgumentException>(() => builder.Build(new Dictionary<string, string?>())); Check.Equal("{", File.ReadAllText(preferences));
        File.WriteAllText(preferences, new string('x', 65537)); Check.Throws<InvalidDataException>(() => builder.Build(new Dictionary<string, string?>()));
        File.Delete(preferences); Directory.CreateDirectory(preferences); Check.Throws<InvalidDataException>(() => builder.Build(new Dictionary<string, string?>()));
    }

    private static void PythonRepresentations()
    {
        using var temp = new Sandbox(); var builder = new SourceEngineEnvironment(temp.Path, System.IO.Path.Combine(temp.Path, "profile"), new SupportPrivacy());
        var preferences = System.IO.Path.Combine(temp.Path, "source-browser.json"); var parent = new Dictionary<string, string?>();
        foreach (var (value, expected) in new (JsonNode Value, string Expected)[]
        {
            (new JsonArray("Example"), "['Example']"),
            (new JsonObject { ["z"] = 2, ["a"] = 1 }, "{'z': 2, 'a': 1}"),
            (new JsonArray("can't", "say \"yes\"", "a\\b", "\0\n\t"), "[\"can't\", 'say \"yes\"', 'a\\\\b', '\\x00\\n\\t']"),
            (new JsonArray(true, false, null, 42), "[True, False, None, 42]"),
            (new JsonArray("\u00a0\u200b\ue000"), "['\\xa0\\u200b\\ue000']")
        })
        {
            File.WriteAllText(preferences, new JsonObject { ["preset"] = "custom", ["user_agent"] = value }.ToJsonString());
            var actual = builder.Build(parent); Check.Equal(expected, actual["AUTOSOURCARR_SOURCE_USER_AGENT"]);
            Check.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expected))), actual["AUTOSOURCARR_SOURCE_UA_REVISION"]);
        }
        foreach (var value in new JsonNode[] { new JsonArray("é"), new JsonObject { ["界"] = "value" }, new JsonArray("😀") })
        {
            File.WriteAllText(preferences, new JsonObject { ["preset"] = "custom", ["user_agent"] = value }.ToJsonString());
            Check.Throws<ArgumentException>(() => builder.Build(parent));
        }
    }

    private static async Task Identity()
    {
        using var setup = new DatabaseSetup(); var privacy = new SupportPrivacy(); const string address = "http://configured-engine.invalid/service";
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("configured-user:configured-private-password")); var calls = new List<string>();
        await using var client = new SuwayomiClient(new AutoSourcarrSettings(), address, username: "configured-user", password: "configured-private-password", privacy: privacy, handler: new Handler((request, _) =>
        { Check.Equal(expected, request.Headers.Authorization!.ToString()); calls.Add(request.RequestUri!.AbsolutePath); return Task.FromResult(Response(new JsonObject { ["data"] = Schema() })); }));
        await using var service = new SourceEngineCapabilityContract(setup.Settings, client, SourceEngineCapabilityIdentity.ConfiguredExternal(address, "configured-user", "configured-private-password"), privacy, "main", new FixedClock(), trustedTestAboutTransport: new Handler((request, _) =>
        {
            Check.Equal(expected, request.Headers.Authorization!.ToString()); Check.True(!request.Headers.Contains("Cookie") && request.Headers.ProxyAuthorization is null); calls.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Response(new JsonObject { ["serverVersion"] = "2.4.2366" }));
        }));
        var report = await service.CheckAsync(); Check.Equal("main", Text(report["engine"])); Check.True(report["batch_queue_reorder"]!.GetValue<bool>()); Check.True(!report["native_qualified"]!.GetValue<bool>());
        Check.True(calls.SequenceEqual(new[] { "/service/api/v1/settings/about", "/service/api/graphql" })); Check.Equal("[redacted]", privacy.Text("configured-private-password"));
        Check.Throws<ArgumentException>(() => new SourceEngineCapabilityContract(setup.Settings, client, SourceEngineCapabilityIdentity.IntegratedSource(), privacy, "source"));
        Check.Throws<ArgumentException>(() => SourceEngineCapabilityIdentity.ConfiguredExternal("http://u:p@configured-engine.invalid/"));
        Check.Throws<ArgumentException>(() => SourceEngineCapabilityIdentity.ConfiguredExternal("http://configured-engine.invalid/?token=bad"));
    }

    private static async Task InvalidReports()
    {
        using var setup = new DatabaseSetup(); var previous = new JsonObject { ["retained"] = "earlier valid observation" }; setup.Settings.Set(SourceEngineCapabilityContract.SettingKey, previous);
        JsonNode? about = new JsonObject { ["version"] = "" }; JsonObject graph = Schema(); var queries = 0;
        await using var client = new SuwayomiClient(new AutoSourcarrSettings(), Base, handler: new Handler((_, _) => { queries++; return Task.FromResult(Response(new JsonObject { ["data"] = graph.DeepClone() })); }));
        await using var service = Contract(setup, client, new Handler((_, _) => Task.FromResult(Response(about))));
        await Throws<SourceEngineCapabilityException>(() => service.CheckAsync()); Check.Equal(0, queries); Equal(previous, setup.Settings.Get(SourceEngineCapabilityContract.SettingKey));
        about = new JsonObject { ["version"] = "2.3.2243" }; graph = new JsonObject { ["__schema"] = new JsonObject { ["mutationType"] = new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["name"] = 42 }) } } };
        await Throws<SourceEngineCapabilityException>(() => service.CheckAsync()); Equal(previous, setup.Settings.Get(SourceEngineCapabilityContract.SettingKey));
        graph = Schema(); graph["__schema"]!["mutationType"]!["fields"]!.AsArray().RemoveAt(0); var observed = await service.CheckAsync();
        Check.True(!observed["contract_ok"]!.GetValue<bool>()); Equal(observed, setup.Settings.Get(SourceEngineCapabilityContract.SettingKey));
    }

    private static async Task AboutBounds()
    {
        using var setup = new DatabaseSetup(); var previous = new JsonObject { ["retained"] = true }; setup.Settings.Set(SourceEngineCapabilityContract.SettingKey, previous); var queries = 0;
        await using var client = new SuwayomiClient(new AutoSourcarrSettings(), Base, handler: new Handler((_, _) => { queries++; return Task.FromResult(Response(new JsonObject { ["data"] = Schema() })); }));
        var replies = new Func<HttpResponseMessage>[]
        {
            () => { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri("http://other-engine.invalid/api/v1/settings/about"); return response; },
            () => { var response = Response(new JsonObject { ["version"] = "2.3.2243" }); response.Content.Headers.ContentEncoding.Add("gzip"); return response; },
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[256 * 1024 + 1]) },
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not-json") },
            () => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("private upstream detail") }
        };
        foreach (var reply in replies)
        {
            var calls = 0; await using var service = Contract(setup, client, new Handler((_, _) => { calls++; return Task.FromResult(reply()); }));
            await Throws<SourceEngineCapabilityException>(() => service.CheckAsync()); Check.Equal(1, calls); Equal(previous, setup.Settings.Get(SourceEngineCapabilityContract.SettingKey));
        }
        Check.Equal(0, queries);
    }

    private static async Task Cancellation()
    {
        using var setup = new DatabaseSetup(); var started = Signal(); var release = Signal(); var finished = 0; using var cancel = new CancellationTokenSource();
        await using var client = new SuwayomiClient(new AutoSourcarrSettings(), Base, handler: new Handler((_, _) => Task.FromResult(Response(new JsonObject { ["data"] = Schema() }))));
        await using var service = Contract(setup, client, new Handler(async (_, _) => { started.TrySetResult(); await release.Task; Interlocked.Increment(ref finished); return Response(new JsonObject { ["version"] = "2.3.2243" }); }));
        var task = service.CheckAsync(cancel.Token);
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel(); Check.True(!task.IsCompleted); release.TrySetResult(); await Throws<OperationCanceledException>(() => task); Check.Equal(1, finished); Check.True(setup.Settings.Get(SourceEngineCapabilityContract.SettingKey) is null); }
        finally { release.TrySetResult(); await Ignore(task); }
    }

    private static async Task Shutdown()
    {
        using var setup = new DatabaseSetup(); var started = Signal(); var release = Signal(); var finished = 0;
        await using var client = new SuwayomiClient(new AutoSourcarrSettings(), Base, handler: new Handler(async (_, _) => { started.TrySetResult(); await release.Task; Interlocked.Increment(ref finished); return Response(new JsonObject { ["data"] = Schema() }); }));
        await using var service = Contract(setup, client, new Handler((_, _) => Task.FromResult(Response(new JsonObject { ["version"] = "2.3.2243" }))));
        var task = service.CheckAsync(); Task? stop = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); stop = service.StopAsync(); Check.True(!stop.IsCompleted); await Throws<OperationCanceledException>(() => service.CheckAsync());
            release.TrySetResult(); await Throws<OperationCanceledException>(() => task); await stop.WaitAsync(TimeSpan.FromSeconds(2)); Check.Equal(1, finished);
            Check.True(setup.Settings.Get(SourceEngineCapabilityContract.SettingKey) is null); Check.True((await client.GraphQlAsync("query CallerStillOwnsClient { __typename }"))["__schema"] is JsonObject);
        }
        finally { release.TrySetResult(); await Ignore(task); await (stop ?? service.StopAsync()).WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    private static async Task LocalDeferral()
    {
        using var setup = new DatabaseSetup(); var calls = 0;
        await using var client = new SuwayomiClient(new AutoSourcarrSettings(), Base, runtime: new DeferredRuntime(), handler: new Handler((_, _) => { calls++; throw new InvalidOperationException("Native work must be locally deferred"); }));
        await using var service = Contract(setup, client, new Handler((_, _) => Task.FromResult(Response(new JsonObject { ["version"] = "2.3.2243" }))));
        var error = await Throws<SourceEngineUnavailableException>(() => service.CheckAsync()); Check.True(error.LocalSourceDeferral); Check.Equal(0, calls); Check.True(setup.Settings.Get(SourceEngineCapabilityContract.SettingKey) is null);
    }

    private static async Task Privacy()
    {
        using var setup = new DatabaseSetup(); var privacy = new SupportPrivacy();
        await using var client = new SuwayomiClient(new AutoSourcarrSettings(), Base, privacy: privacy, handler: new Handler((_, _) => Task.FromResult(Response(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "unregistered-upstream-private-value" }) }))));
        await using var service = new SourceEngineCapabilityContract(setup.Settings, client, SourceEngineCapabilityIdentity.ConfiguredExternal(Base, "private-user", "private-password"), privacy, "source", new FixedClock(), trustedTestAboutTransport: new Handler((_, _) => Task.FromResult(Response(new JsonObject { ["version"] = "2.3.2243" }))));
        var error = await Throws<SourceEngineCapabilityException>(() => service.CheckAsync()); Check.True(!error.ToString().Contains("unregistered-upstream-private-value", StringComparison.Ordinal)); Check.True(error.InnerException is null);
        Check.Equal("[redacted]", privacy.Text("private-password")); Check.True(setup.Settings.Get(SourceEngineCapabilityContract.SettingKey) is null);
    }

    private sealed class DatabaseSetup : IDisposable
    {
        private readonly Sandbox _temp = new(); private readonly SqliteDatabase _database;
        public SettingsRepository Settings { get; }
        public DatabaseSetup() { _database = new(new AutoSourcarrSettings { DataDir = _temp.Path }); new SchemaMigrator(_database).Initialize(); Settings = new(_database); }
        public void Dispose() { _database.Dispose(); _temp.Dispose(); }
    }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class DeferredRuntime : IManagedSourceRuntime
    { public bool BrowserMaintenance => false; public Task EnsureAvailableAsync(string requestPath, CancellationToken cancellationToken) => Task.FromException(new SourceEngineUnavailableException("Controlled local deferral")); }
    private static SourceEngineCapabilityContract Contract(DatabaseSetup setup, SuwayomiClient client, HttpMessageHandler about) => new(setup.Settings, client,
        SourceEngineCapabilityIdentity.ConfiguredExternal(Base), new SupportPrivacy(), "source", new FixedClock(), trustedTestAboutTransport: about);
    private static JsonObject Schema() => new() { ["__schema"] = new JsonObject { ["mutationType"] = new JsonObject { ["fields"] = new JsonArray(new[] { "updateManga", "enqueueChapterDownloads", "fetchMangaAndChapters", "fetchChapterPages", "reorderChapterDownload", "reorderChapterDownloads" }.Select(name => (JsonNode)new JsonObject { ["name"] = name }).ToArray()) } } };
    private static HttpResponseMessage Response(JsonNode? body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body)) };
    private static void WriteEnvironment(string directory, JsonObject row)
    {
        if (row["text"] is not null) File.WriteAllText(Path.Combine(directory, "source-host.env"), Text(row["text"]));
        if (row["preferences_text"] is not null) File.WriteAllText(Path.Combine(directory, "source-browser.json"), Text(row["preferences_text"]));
        else if (row["preferences"] is not null) File.WriteAllText(Path.Combine(directory, "source-browser.json"), row["preferences"]!.ToJsonString());
    }
    private static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("source-runtime-contract.json")))!.AsObject();
    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? "";
    private static void Equal(JsonNode? expected, JsonNode? actual) => Check.JsonEqual(JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(actual));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Ignore(Task task) { try { await task.WaitAsync(TimeSpan.FromSeconds(3)); } catch { } }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(TimeSpan.FromSeconds(3)); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
