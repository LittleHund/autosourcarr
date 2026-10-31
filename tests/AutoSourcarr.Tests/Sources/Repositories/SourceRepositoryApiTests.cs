using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Sources.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoSourcarr.Tests;

public static class SourceRepositoryApiTests
{
    private const string Path = "/api/admin/source-repositories";
    private const string Url = "https://publisher.example/repo.json";

    public static void Register(TestSuite suite)
    {
        suite.Add("repository API: reader and missing credentials cannot read, preview or mutate configuration", () => Authorization().GetAwaiter().GetResult());
        suite.Add("repository API: actual per-instance snapshot and restart affordance retain no-store", () => Snapshot().GetAwaiter().GetResult());
        suite.Add("repository API: actual add/remove/CAS retain unrelated bytes and another instance", () => Changes().GetAwaiter().GetResult());
        suite.Add("repository API: removals retain access to legacy URLs rejected by current add rules", () => LegacyRemoval().GetAwaiter().GetResult());
        suite.Add("repository API: bounded typed request validation does not touch configuration", () => Validation().GetAwaiter().GetResult());
        suite.Add("repository API: guarded preview metadata and unknown declarations never imply code trust", () => Preview().GetAwaiter().GetResult());
        suite.Add("repository API: arbitrary upstream errors remain private and leave configuration unchanged", () => PrivateErrors().GetAwaiter().GetResult());
        suite.Add("repository API: cancelled preview retains physical admission until its shared owner drains", () => CancelledPreview().GetAwaiter().GetResult());
        if (OperatingSystem.IsWindows())
            suite.Add("repository API: Windows read-only retained configuration returns safe409", () => PermissionFailure().GetAwaiter().GetResult());
    }

    private static async Task Authorization()
    {
        await using var f = new Fixture(); var before = f.Bytes();
        foreach (var key in new string?[] { null, "reader", "bad" })
            foreach (var (method, suffix) in new[] { (HttpMethod.Get, ""), (HttpMethod.Post, ""), (HttpMethod.Delete, ""), (HttpMethod.Post, "/preview") })
            {
                using var response = await f.Send(method, suffix, new() { ["url"] = Url, ["revision"] = new string('0', 64) }, key);
                Check.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        Check.True(before.SequenceEqual(f.Bytes())); Check.Equal(0, f.Fetcher.Calls); Check.Equal(0, f.Unexpected);
    }

    private static async Task Snapshot()
    {
        foreach (var external in new[] { false, true })
        {
            await using var f = new Fixture(external); using var response = await f.Send(HttpMethod.Get);
            Check.Equal(HttpStatusCode.OK, response.StatusCode); Check.Equal("no-store", response.Headers.CacheControl!.ToString());
            var body = await Body(response); Check.Equal(!external, body["can_restart"]!.GetValue<bool>());
            Check.Equal(0, body["repositories"]!.AsArray().Count); Check.Equal(64, Text(body["revision"]).Length);
            Check.Equal(0, f.Fetcher.Calls); Check.Equal(0, f.Unexpected);
        }
    }

    private static async Task Changes()
    {
        await using var one = new Fixture(); await using var two = new Fixture(); var other = two.Bytes();
        var revision = await one.Revision();
        using (var response = await one.Send(HttpMethod.Post, body: new() { ["url"] = Url, ["revision"] = revision }))
        {
            Check.Equal(HttpStatusCode.OK, response.StatusCode); var body = await Body(response);
            Check.True(body["restart_required"]!.GetValue<bool>()); Check.Equal(1, body["repositories"]!.AsArray().Count);
            Check.Equal("no-store", response.Headers.CacheControl!.ToString());
        }
        var changed = one.Bytes();
        Check.True(Encoding.UTF8.GetString(changed).Contains("# retained comment\nUNRELATED=retained\n", StringComparison.Ordinal));
        using (var response = await one.Send(HttpMethod.Post, body: new() { ["url"] = "https://other.example/repo.json", ["revision"] = revision }))
            Check.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Check.True(changed.SequenceEqual(one.Bytes())); Check.True(other.SequenceEqual(two.Bytes()));
        using (var response = await one.Send(HttpMethod.Delete, body: new() { ["url"] = Url, ["revision"] = await one.Revision() }))
        { Check.Equal(HttpStatusCode.OK, response.StatusCode); Check.Equal(0, (await Body(response))["repositories"]!.AsArray().Count); }
        Check.Equal(0, one.Fetcher.Calls); Check.Equal(0, one.Unexpected);
    }

    private static async Task LegacyRemoval()
    {
        await using var f = new Fixture(); const string legacy = "http://legacy.example/index.json";
        File.WriteAllText(f.SourcePath, "EXTENSION_STORES=[\"" + legacy + "\"]\n"); var revision = await f.Revision();
        using (var rejected = await f.Send(HttpMethod.Post, body: new() { ["url"] = legacy, ["revision"] = revision })) Check.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var response = await f.Send(HttpMethod.Delete, body: new() { ["url"] = legacy, ["revision"] = revision });
        Check.Equal(HttpStatusCode.OK, response.StatusCode); Check.Equal(0, (await Body(response))["repositories"]!.AsArray().Count); Check.Equal(0, f.Unexpected);
    }

    private static async Task Validation()
    {
        await using var f = new Fixture(); var before = f.Bytes();
        foreach (var request in new[] { new JsonObject(), new JsonObject { ["url"] = 1 }, new JsonObject { ["url"] = "" }, new JsonObject { ["url"] = Url, ["revision"] = "wrong" } })
        {
            using var response = await f.Send(HttpMethod.Post, body: request); Check.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }
        foreach (var raw in new[] { "[]", "{broken", "{\"url\":\"one\",\"url\":\"two\"}", new string('X', 65537) })
        {
            using var response = await f.Raw(HttpMethod.Post, raw);
            Check.Equal(raw.Length > 65536 ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var response = await f.Send(HttpMethod.Post, "/preview", new() { ["url"] = "https://account:synthetic-secret@publisher.example/repo.json" }))
        { Check.Equal(HttpStatusCode.BadRequest, response.StatusCode); Check.True(!(await response.Content.ReadAsStringAsync()).Contains("synthetic-secret", StringComparison.Ordinal)); }
        Check.True(before.SequenceEqual(f.Bytes())); Check.Equal(0, f.Fetcher.Calls); Check.Equal(0, f.Unexpected);
    }

    private static async Task Preview()
    {
        await using var f = new Fixture(); var before = f.Bytes(); using var response = await f.Send(HttpMethod.Post, "/preview", new() { ["url"] = Url });
        Check.Equal(HttpStatusCode.OK, response.StatusCode); var body = await Body(response);
        Check.Equal("Fixture", Text(body["name"])); Check.Equal(2, body["extension_count"]!.GetValue<int>());
        Check.Equal(SourceRepositoryPreview.UnknownStatus, Text(body["source_code_status"])); Check.True(body["source_repository"] is null);
        Check.Equal("no-store", response.Headers.CacheControl!.ToString()); Check.True(before.SequenceEqual(f.Bytes())); Check.True(f.Fetcher.Calls >= 1); Check.Equal(0, f.Unexpected);
    }

    private static async Task PrivateErrors()
    {
        await using var f = new Fixture(); var before = f.Bytes();
        foreach (var error in new Exception[] { new IOException("synthetic-upstream-private-account-password"),
                     new ArgumentException("synthetic-upstream-private-account-password"), new HttpRequestException("synthetic-upstream-private-account-password") })
        {
            f.Fetcher.Before = _ => throw error;
            using var response = await f.Send(HttpMethod.Post, "/preview", new() { ["url"] = Url });
            Check.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Check.True(!(await response.Content.ReadAsStringAsync()).Contains("synthetic-upstream-private", StringComparison.Ordinal));
        }
        Check.True(before.SequenceEqual(f.Bytes())); Check.Equal(0, f.Unexpected);
    }

    private static async Task CancelledPreview()
    {
        await using var f = new Fixture(); var entered = Signal(); var release = Signal(); var before = f.Bytes();
        f.Fetcher.Before = async _ => { entered.TrySetResult(); await release.Task; };
        using var cancel = new CancellationTokenSource(); var request = f.Send(HttpMethod.Post, "/preview", new() { ["url"] = Url }, cancellationToken: cancel.Token);
        Task? stopped = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancel.Cancel();
            try { using var ignored = await request.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("Cancelled preview produced a response"); } catch (OperationCanceledException) { }
            stopped = f.Previews.StopAsync(); await Task.Delay(30); Check.True(!stopped.IsCompleted);
        }
        finally { release.TrySetResult(); if (stopped is not null) await stopped.WaitAsync(TimeSpan.FromSeconds(3)); try { using var ignored = await request; } catch (OperationCanceledException) { } }
        Check.True(before.SequenceEqual(f.Bytes())); Check.Equal(0, f.Unexpected);
    }

    private static async Task PermissionFailure()
    {
        await using var f = new Fixture(); var before = f.Bytes(); var revision = await f.Revision();
        var attributes = File.GetAttributes(f.SourcePath);
        try
        {
            File.SetAttributes(f.SourcePath, attributes | FileAttributes.ReadOnly);
            using var response = await f.Send(HttpMethod.Post, body: new() { ["url"] = Url, ["revision"] = revision });
            Check.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Check.True(!(await response.Content.ReadAsStringAsync()).Contains(f.SourcePath, StringComparison.Ordinal));
            Check.True(before.SequenceEqual(f.Bytes())); Check.Equal(0, f.Unexpected);
        }
        finally { File.SetAttributes(f.SourcePath, attributes); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Sandbox _temp = new(); private readonly IHost _host; private readonly HttpClient _client; private readonly WholeTaskCoordinator _lanes;
        private readonly BoundedWorkScheduler _io = new(1, 1);
        public readonly Fetcher Fetcher = new(); public SourceRepositoryPreview Previews { get; } public int Unexpected;
        public string SourcePath => System.IO.Path.Combine(_temp.Path, "source-host.env");
        public Fixture(bool external = false)
        {
            File.WriteAllText(SourcePath, "# retained comment\nUNRELATED=retained\nEXTENSION_STORES=[]\n", new UTF8Encoding(false));
            var settings = new AutoSourcarrSettings { DataDir = _temp.Path, AdminToken = "admin", AutosourcarrReaderApiToken = "reader", SourceEngineMode = external ? "external" : "integrated" };
            var privacy = new SupportPrivacy(); var tasks = new TaskRegistry(); _lanes = new(settings, tasks); Previews = new(Fetcher);
            var repositories = new SourceRepositoryStore(new CanonicalConfiguration(_temp.Path));
            _host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => services.AddRouting()).Configure(app =>
            {
                app.UseAutoSourcarrHttp("0.4.3-migration", () => settings, (_, _) => { Interlocked.Increment(ref Unexpected); return "unexpected"; }, new RequestAdmissionGate(), _lanes, tasks, privacy, _ => { });
                app.UseEndpoints(routes => routes.MapSourceRepositories(repositories, Previews, _io, () => settings));
            })).Build(); _host.Start(); _client = _host.GetTestClient();
        }
        public byte[] Bytes() => File.ReadAllBytes(SourcePath);
        public async Task<string> Revision() { using var response = await Send(HttpMethod.Get); Check.Equal(HttpStatusCode.OK, response.StatusCode); return Text((await Body(response))["revision"]); }
        public Task<HttpResponseMessage> Send(HttpMethod method, string suffix = "", JsonObject? body = null, string? key = "admin", CancellationToken cancellationToken = default) =>
            Raw(method, body?.ToJsonString(), suffix, key, cancellationToken);
        public async Task<HttpResponseMessage> Raw(HttpMethod method, string? raw, string suffix = "", string? key = "admin", CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(method, Path + suffix);
            if (key is not null) request.Headers.Add(key == "reader" ? "X-AutoSourcarr-Api-Key" : "X-Admin-Token", key);
            if (raw is not null) request.Content = new StringContent(raw, Encoding.UTF8, "application/json");
            return await _client.SendAsync(request, cancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            try { await _host.StopAsync(); }
            finally
            {
                _client.Dispose(); _host.Dispose();
                try { await Previews.DisposeAsync(); }
                finally { try { await _io.DisposeAsync(); } finally { await _lanes.DisposeAsync(); _temp.Dispose(); } }
            }
        }
    }
    private sealed class Fetcher : IPublicSourceFetcher
    {
        public int Calls; public Func<CancellationToken, Task>? Before;
        public async Task<PublicSourceResponse> FetchAsync(PublicSourceRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls); if (Before is { } before) await before(cancellationToken);
            var body = request.Url.AbsolutePath.EndsWith("repo.json", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes("{\"name\":\"Fixture\",\"signingKey\":\"key\",\"extensionList\":{\"extensions\":[{},{}]}}")
                : throw new IOException("No declared fixture project");
            return new(body, request.Url);
        }
    }
    private static async Task<JsonObject> Body(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    private static string Text(JsonNode? value) => value?.GetValue<string>() ?? "";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
