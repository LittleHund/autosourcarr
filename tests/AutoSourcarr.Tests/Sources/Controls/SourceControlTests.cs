using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Sources.Controls;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Tests;

public static class SourceControlTests
{
    private const string Source = "-9223372036854775808";
    public static void Register(TestSuite suite)
    {
        suite.Add("source controls: 324 released ID/preference/screen/value differential cases", Oracle);
        suite.Add("source controls: controlled native HTTP carries all five exact GraphQL preference changes", () => Wire().GetAwaiter().GetResult());
        suite.Add("source controls: stale position/key/type/current screen is refused before native mutation", () => Stale().GetAwaiter().GetResult());
        suite.Add("source controls: real ready/generation input and per-instance observations gate admission", () => Generation().GetAwaiter().GetResult());
        suite.Add("source controls: generation change during transport never publishes confirmed success", () => ChangedGeneration().GetAwaiter().GetResult());
        suite.Add("source controls: shared source-admin ownership is cancellable before admission", () => WaitingLane().GetAwaiter().GetResult());
        suite.Add("source controls: disconnected mutation joins physical HTTP and retains source-admin ownership", () => Cancellation().GetAwaiter().GetResult());
        suite.Add("source controls: stop joins an admitted physical transport that ignores cancellation", () => Shutdown().GetAwaiter().GetResult());
        suite.Add("source controls: uncertain mutation requires a fresh snapshot without an automatic retry", () => Uncertain().GetAwaiter().GetResult());
        suite.Add("source controls: source/current/new secrets remain private in safe failures and shared diagnostics", () => Privacy().GetAwaiter().GetResult());
        suite.Add("source controls: malformed native confirmation is refused and screen observation invalidated", () => Malformed().GetAwaiter().GetResult());
        suite.Add("source controls: Unicode text bounds, NUL rejection and native multiselect set semantics", Bounds);
    }
    private static void Oracle()
    {
        var count = 0;
        foreach (var row in Fixture()["cases"]!.AsArray().OfType<JsonObject>())
        {
            JsonNode? result;
            switch (Text(row["kind"]))
            {
                case "source-id":
                    if (!row["valid"]!.GetValue<bool>()) { Check.Throws<ArgumentException>(() => SourcePreferenceContract.SourceId(row["value"])); count++; continue; }
                    result = JsonValue.Create(SourcePreferenceContract.SourceId(row["value"])); break;
                case "normalize-preferences": result = SourcePreferenceContract.Normalize(row["source"]!.AsObject(), row["preferences"]!.AsArray()); break;
                case "preference-change":
                    if (!row["valid"]!.GetValue<bool>())
                    {
                        var error = Check.Throws<ArgumentException>(() => SourcePreferenceContract.Change(row["snapshot"]!.AsObject(), row["change"]));
                        Check.Equal(Text(row["error"]) == "SourcePreferencesConflict", error is SourcePreferencesConflictException); count++; continue;
                    }
                    result = SourcePreferenceContract.Change(row["snapshot"]!.AsObject(), row["change"]); break;
                default: throw new InvalidDataException("Unknown source-control oracle kind");
            }
            Equal(row["result"], result); count++;
        }
        Check.Equal(324, count);
    }
    private static async Task Wire()
    {
        await using var setup = new Setup(); var snapshot = await setup.Service.SnapshotAsync(Source);
        Check.Equal(Source, Text(snapshot["source_id"])); Check.True(snapshot["preferences"]![2]!["secret"]!.GetValue<bool>());
        var values = new JsonNode?[] { JsonValue.Create(true), JsonValue.Create(false), JsonValue.Create("new-private-setting"), JsonValue.Create("ja"), new JsonArray("a", "b") };
        for (var index = 0; index < values.Length; index++) snapshot = await setup.Service.UpdateAsync(Source, Change(snapshot, index, values[index]));
        Check.Equal(5, setup.Engine.Mutations); Check.Equal(6, setup.Engine.Queries);
        var expected = new[] { "switchState", "checkBoxState", "editTextState", "listState", "multiSelectState" };
        for (var index = 0; index < expected.Length; index++) { Check.True(setup.Engine.Changes[index].ContainsKey(expected[index])); Check.Equal(index, setup.Engine.Changes[index]["position"]!.GetValue<int>()); }
        Check.True(setup.Engine.SeenSourceValues.All(value => value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() == Source));
        Check.Equal(NormalizeWhitespace(Text(Fixture()["preference_fields"])), NormalizeWhitespace(SourcePreferenceContract.PreferenceFields));
        Check.True(setup.Admin.Wait(0)); setup.Admin.Release();
    }
    private static async Task Stale()
    {
        await using var setup = new Setup(); var snapshot = await setup.Service.SnapshotAsync(Source); var change = Change(snapshot, 0, JsonValue.Create(true));
        var first = setup.Engine.Preferences[0]!.DeepClone(); setup.Engine.Preferences.RemoveAt(0); setup.Engine.Preferences.Add(first);
        await Throws<SourcePreferencesConflictException>(() => setup.Service.UpdateAsync(Source, change)); Check.Equal(0, setup.Engine.Mutations);
        snapshot = await setup.Service.SnapshotAsync(Source);
        foreach (var key in new[] { "index", "key", "type" })
        {
            var wrong = Change(snapshot, 0, JsonValue.Create(true)); wrong[key] = key == "index" ? JsonValue.Create(true) : JsonValue.Create("wrong");
            await Throws<SourcePreferencesConflictException>(() => setup.Service.UpdateAsync(Source, wrong));
        }
        Check.Equal(0, setup.Engine.Mutations);
    }
    private static async Task Generation()
    {
        await using var setup = new Setup(); setup.Engine.Ready = false;
        await Throws<SourceEngineUnavailableException>(() => setup.Service.SnapshotAsync(Source)); Check.Equal(0, setup.Engine.Queries);
        setup.Engine.Ready = true; var snapshot = await setup.Service.SnapshotAsync(Source); setup.Engine.Generation++;
        var queries = setup.Engine.Queries; await Throws<SourcePreferencesConflictException>(() => setup.Service.UpdateAsync(Source, Change(snapshot, 0, JsonValue.Create(true)))); Check.Equal(queries, setup.Engine.Queries);
        snapshot = await setup.Service.SnapshotAsync(Source); await setup.Service.UpdateAsync(Source, Change(snapshot, 0, JsonValue.Create(true))); Check.Equal(1, setup.Engine.Mutations);
        await using var other = new Setup(); await Throws<SourcePreferencesConflictException>(() => other.Service.UpdateAsync(Source, Change(snapshot, 0, JsonValue.Create(true)))); Check.Equal(0, other.Engine.Queries);
        Check.Throws<ArgumentNullException>(() => new SourcePreferenceService(setup.Client, setup.Admin, null!, setup.Privacy));
    }
    private static async Task ChangedGeneration()
    {
        await using var setup = new Setup(); var snapshot = await setup.Service.SnapshotAsync(Source);
        setup.Engine.BeforeReply = (mutation, _) => { if (!mutation) setup.Engine.Generation++; return Task.CompletedTask; };
        await Throws<SourcePreferencesConflictException>(() => setup.Service.UpdateAsync(Source, Change(snapshot, 0, JsonValue.Create(true)))); Check.Equal(0, setup.Engine.Mutations);
        setup.Engine.BeforeReply = null; snapshot = await setup.Service.SnapshotAsync(Source);
        setup.Engine.BeforeReply = (mutation, _) => { if (mutation) setup.Engine.Generation++; return Task.CompletedTask; };
        var failed = await Throws<SourceControlFailedException>(() => setup.Service.UpdateAsync(Source, Change(snapshot, 0, JsonValue.Create(true)))); Check.True(failed.RefreshRequired); Check.Equal(1, setup.Engine.Mutations);
        await Throws<SourcePreferencesConflictException>(() => setup.Service.UpdateAsync(Source, Change(snapshot, 0, JsonValue.Create(true)))); Check.Equal(1, setup.Engine.Mutations);
    }
    private static async Task WaitingLane()
    {
        await using var setup = new Setup(); await setup.Admin.WaitAsync(); using var cancellation = new CancellationTokenSource();
        var request = setup.Service.SnapshotAsync(Source, cancellation.Token);
        try { Check.True(!request.IsCompleted); Check.Equal(0, setup.Engine.Queries); cancellation.Cancel(); await Throws<OperationCanceledException>(async () => await request); Check.Equal(0, setup.Admin.CurrentCount); }
        finally { cancellation.Cancel(); setup.Admin.Release(); try { await request; } catch (OperationCanceledException) { } }
    }
    private static async Task Cancellation()
    {
        await using var setup = new Setup(); var snapshot = await setup.Service.SnapshotAsync(Source); var entered = Signal(); var release = Signal(); var finished = Signal(); var transportCancelled = false;
        setup.Engine.BeforeReply = async (mutation, token) =>
        {
            if (!mutation) return; using var registration = token.Register(() => transportCancelled = true); entered.TrySetResult();
            try { await release.Task; } finally { finished.TrySetResult(); }
        };
        using var cancellation = new CancellationTokenSource(); var request = setup.Service.UpdateAsync(Source, Change(snapshot, 0, JsonValue.Create(true)), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
            Check.True(!request.IsCompleted); Check.Equal(0, setup.Admin.CurrentCount); Check.True(!transportCancelled);
            release.TrySetResult(); await Throws<OperationCanceledException>(async () => await request); Check.True(finished.Task.IsCompletedSuccessfully); Check.Equal(1, setup.Admin.CurrentCount); Check.Equal(1, setup.Engine.Mutations);
        }
        finally { release.TrySetResult(); try { await request; } catch (OperationCanceledException) { } }
    }
    private static async Task Shutdown()
    {
        await using var setup = new Setup(); var snapshot = await setup.Service.SnapshotAsync(Source); var entered = Signal(); var release = Signal();
        setup.Engine.BeforeReply = async (mutation, _) => { if (mutation) { entered.TrySetResult(); await release.Task; } };
        var request = setup.Service.UpdateAsync(Source, Change(snapshot, 0, JsonValue.Create(true))); Task? stop = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop = setup.Service.StopAsync(); Check.True(!stop.IsCompleted && !request.IsCompleted); Check.Equal(0, setup.Admin.CurrentCount);
            release.TrySetResult(); var failed = await Throws<SourceControlFailedException>(async () => await request); Check.True(failed.RefreshRequired); await stop; Check.Equal(1, setup.Admin.CurrentCount);
        }
        finally { release.TrySetResult(); try { await request; } catch (SourceControlFailedException) { } if (stop is not null) await stop; }
    }
    private static async Task Uncertain()
    {
        await using var setup = new Setup(); var snapshot = await setup.Service.SnapshotAsync(Source); var change = Change(snapshot, 0, JsonValue.Create(true));
        setup.Engine.FailAfterMutation = true; var failed = await Throws<SourceControlFailedException>(() => setup.Service.UpdateAsync(Source, change)); Check.True(failed.RefreshRequired); Check.True(failed.InnerException is null);
        Check.Equal(1, setup.Engine.Mutations); var queries = setup.Engine.Queries;
        await Throws<SourcePreferencesConflictException>(() => setup.Service.UpdateAsync(Source, change)); Check.Equal(1, setup.Engine.Mutations); Check.Equal(queries, setup.Engine.Queries);
        setup.Engine.FailAfterMutation = false; var fresh = await setup.Service.SnapshotAsync(Source); Check.True(fresh["preferences"]![0]!["value"]!.GetValue<bool>());
    }
    private static async Task Privacy()
    {
        await using var setup = new Setup(); var snapshot = await setup.Service.SnapshotAsync(Source); var current = Text(snapshot["preferences"]![2]!["value"]);
        Check.True(current.Length > 0 && !setup.Privacy.Text("failed " + current).Contains(current, StringComparison.Ordinal));
        const string incoming = "opaque-incoming-private-value"; setup.Engine.GraphQlError = "opaque unregistered extension password data";
        var error = await Throws<SourceControlFailedException>(() => setup.Service.SnapshotAsync(Source)); Check.True(!error.ToString().Contains(setup.Engine.GraphQlError, StringComparison.Ordinal)); Check.True(error.InnerException is null);
        setup.Engine.GraphQlError = null; snapshot = await setup.Service.SnapshotAsync(Source); setup.Engine.FailAfterMutation = true;
        error = await Throws<SourceControlFailedException>(() => setup.Service.UpdateAsync(Source, Change(snapshot, 2, JsonValue.Create(incoming))));
        Check.True(!error.ToString().Contains(incoming, StringComparison.Ordinal)); Check.True(!setup.Privacy.Text("request failed " + incoming).Contains(incoming, StringComparison.Ordinal));
    }
    private static async Task Malformed()
    {
        foreach (var kind in new[] { "source", "preferences", "value" })
        {
            await using var setup = new Setup(); var snapshot = await setup.Service.SnapshotAsync(Source); setup.Engine.MalformedMutation = kind;
            var error = await Throws<SourceControlFailedException>(() => setup.Service.UpdateAsync(Source, Change(snapshot, 0, JsonValue.Create(true)))); Check.True(error.RefreshRequired);
            await Throws<SourcePreferencesConflictException>(() => setup.Service.UpdateAsync(Source, Change(snapshot, 0, JsonValue.Create(true)))); Check.Equal(1, setup.Engine.Mutations);
        }
    }
    private static void Bounds()
    {
        var row = Fixture()["cases"]!.AsArray().OfType<JsonObject>().First(value => Text(value["kind"]) == "normalize-preferences");
        var snapshot = SourcePreferenceContract.Normalize(row["source"]!.AsObject(), row["preferences"]!.AsArray()); var text = string.Concat(Enumerable.Repeat("😀", 8192));
        Check.Equal(text, SourcePreferenceContract.Change(snapshot, Change(snapshot, 2, JsonValue.Create(text)))["editTextState"]!.GetValue<string>());
        Check.Throws<ArgumentException>(() => SourcePreferenceContract.Change(snapshot, Change(snapshot, 2, JsonValue.Create(text + "😀"))));
        Check.Throws<ArgumentException>(() => SourcePreferenceContract.Change(snapshot, Change(snapshot, 2, JsonValue.Create("a\0b"))));
        var selected = SourcePreferenceContract.Change(snapshot, Change(snapshot, 4, new JsonArray("a", "a"))); Check.Equal(1, selected["multiSelectState"]!.AsArray().Count);
        Check.Throws<ArgumentException>(() => SourcePreferenceContract.Change(snapshot, Change(snapshot, 4, new JsonArray("a", "a", "b"))));
    }

    private sealed class Setup : IAsyncDisposable
    {
        public Engine Engine { get; } = new(); public SemaphoreSlim Admin { get; } = new(1, 1); public SupportPrivacy Privacy { get; } = new(); public SuwayomiClient Client { get; } public SourcePreferenceService Service { get; }
        public Setup() { Client = new(new AutoSourcarrSettings { SourceRequestTimeout = 30 }, "http://source.invalid:4567", privacy: Privacy, handler: Engine); Service = new(Client, Admin, () => (Engine.Ready, Engine.Generation), Privacy); }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); await Client.DisposeAsync(); Admin.Dispose(); }
    }
    private sealed class Engine : HttpMessageHandler
    {
        public bool Ready = true; public long Generation = 1; public int Queries, Mutations; public bool FailAfterMutation; public string? GraphQlError, MalformedMutation;
        public Func<bool, CancellationToken, Task>? BeforeReply; public JsonArray Preferences { get; }
        public List<JsonObject> Changes { get; } = []; public List<JsonNode> SeenSourceValues { get; } = [];
        public Engine() { Preferences = (JsonArray)Fixture()["cases"]!.AsArray().OfType<JsonObject>().First(row => Text(row["kind"]) == "normalize-preferences")["preferences"]!.DeepClone(); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check.Equal(HttpMethod.Post, request.Method); Check.Equal("/api/graphql", request.RequestUri!.AbsolutePath);
            var envelope = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject(); var query = Text(envelope["query"]); var variables = envelope["variables"]!.AsObject();
            var mutation = query.StartsWith("mutation AutoSourcarrSourcePreference", StringComparison.Ordinal);
            var source = mutation ? variables["input"]!["source"]! : variables["id"]!; SeenSourceValues.Add(source.DeepClone()); Check.Equal(Source, source.GetValue<string>());
            Check.True(query.Contains("switch_value: currentValue", StringComparison.Ordinal) && query.Contains("checkbox_default: default", StringComparison.Ordinal) && query.Contains("multiselect_value: currentValue", StringComparison.Ordinal));
            if (mutation)
            {
                Mutations++; var change = variables["input"]!["change"]!.AsObject(); Changes.Add((JsonObject)change.DeepClone()); var position = change["position"]!.GetValue<int>(); var pref = Preferences[position]!.AsObject();
                var pair = change.Single(pair => pair.Key != "position"); var field = pair.Key switch { "switchState" => "switch_value", "checkBoxState" => "checkbox_value", "editTextState" => "text_value", "listState" => "list_value", "multiSelectState" => "multiselect_value", _ => throw new InvalidDataException("Wrong native preference mutation field") };
                pref[field] = pair.Value?.DeepClone();
            }
            else Queries++;
            if (BeforeReply is { } hook) await hook(mutation, cancellationToken);
            if (mutation && FailAfterMutation) throw new HttpRequestException("opaque native transport text " + Preferences[2]!["text_value"]!.GetValue<string>());
            if (GraphQlError is { } raw) return Reply(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = raw }) });
            var item = new JsonObject { ["id"] = Source, ["name"] = "Example 設定", ["isConfigurable"] = true };
            if (!mutation) { item["preferences"] = Preferences.DeepClone(); return Reply(new JsonObject { ["data"] = new JsonObject { ["source"] = item } }); }
            var payload = new JsonObject { ["source"] = item, ["preferences"] = Preferences.DeepClone() };
            if (MalformedMutation == "source") item["id"] = "17";
            if (MalformedMutation == "preferences") payload["preferences"] = null;
            if (MalformedMutation == "value") payload["preferences"]![0]!["switch_value"] = "not-a-boolean";
            return Reply(new JsonObject { ["data"] = new JsonObject { ["updateSourcePreference"] = payload } });
        }
        private static HttpResponseMessage Reply(JsonObject value) => new(HttpStatusCode.OK) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
    }
    private static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("source-controls.json")))!.AsObject();
    private static JsonObject Change(JsonObject snapshot, int index, JsonNode? value) => new() { ["revision"] = snapshot["revision"]!.DeepClone(), ["index"] = index, ["key"] = snapshot["preferences"]![index]!["key"]?.DeepClone(), ["type"] = snapshot["preferences"]![index]!["type"]!.DeepClone(), ["value"] = value?.DeepClone() };
    private static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
    private static string NormalizeWhitespace(string value) => System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Equal(JsonNode? expected, JsonNode? actual) => Check.JsonEqual(JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(actual));
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
