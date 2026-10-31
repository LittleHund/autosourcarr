using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources.Repositories;

namespace AutoSourcarr.Tests;

public static class SourceRepositoryTests
{
    private const string Index = "https://github.com/publisher/extensions/raw/repo/index.pb";
    private static readonly byte[] Manifest = Encoding.UTF8.GetBytes("{\"name\":\"Fixture\",\"signingKey\":\"key\",\"extensionList\":{\"extensions\":[{},{}]}}");
    public static void Register(TestSuite suite)
    {
        suite.Add("source repositories: 78 released URL/JSON/protobuf/gzip metadata cases", Oracle);
        suite.Add("source repositories: 19 released env scenarios preserve exact bytes over 76 edits", EnvOracle);
        suite.Add("source repositories: exact-byte CAS includes unrelated settings and canonical revision", Cas);
        suite.Add("source repositories: canonical per-path ownership gives same-revision writers one winner", ConcurrentCas);
        suite.Add("source repositories: pending setup recovery precedes byte-CAS and refuses stale drafts", Recovery);
        suite.Add("source repositories: missing mount, bounded files and durable target restrictions fail closed", FileBoundaries);
        suite.Add("source repositories: source-host mode preservation keeps setup defaults unchanged", Permissions);
        suite.Add("source repositories: preview has released metadata/provenance shape and never edits config", () => Preview().GetAwaiter().GetResult());
        suite.Add("source repositories: declared publisher requires exact known index or unambiguous same-owner evidence", () => Publishers().GetAwaiter().GetResult());
        suite.Add("source repositories: preview composes guarded public fetcher and revalidates redirects", () => PublicTransport().GetAwaiter().GetResult());
        suite.Add("source repositories: timed-out previews retain two physical admissions and per-instance isolation", () => Deadline().GetAwaiter().GetResult());
        suite.Add("source repositories: cancelled caller retains physical admission until actual completion", () => Cancellation().GetAwaiter().GetResult());
        suite.Add("source repositories: shutdown closes admission and joins physical fetch before disposal", () => Shutdown().GetAwaiter().GetResult());
        suite.Add("source repositories: cancellation callback failure remains observable after physical drain and repeated disposal", () => FailedCancellation().GetAwaiter().GetResult());
        suite.Add("source repositories: shutdown during declaration is never returned as unknown provenance", () => DeclarationShutdown().GetAwaiter().GetResult());
        suite.Add("source repositories: malformed gzip checksum and bounded input are never accepted", GzipIntegrity);
    }

    private static void Oracle()
    {
        var count = 0;
        foreach (var row in Fixture()["cases"]!.AsArray().OfType<JsonObject>())
        {
            JsonNode? result;
            if (Text(row["kind"]) == "url")
            {
                string? input = row["value"] is JsonValue value && value.TryGetValue<string>(out var url) ? url : null;
                if (!row["valid"]!.GetValue<bool>()) { Check.Throws<ArgumentException>(() => SourceRepositoryStore.NormalizeUrl(input)); count++; continue; }
                result = JsonValue.Create(SourceRepositoryStore.NormalizeUrl(input));
            }
            else
            {
                var bytes = Convert.FromBase64String(Text(row["body"]));
                if (!row["valid"]!.GetValue<bool>()) { Check.Throws<InvalidDataException>(() => RepositoryManifest.Parse(bytes)); count++; continue; }
                result = RepositoryManifest.Parse(bytes);
            }
            Equal(row["result"], result); count++;
        }
        Check.Equal(78, count);
    }

    private static void EnvOracle()
    {
        var scenarios = 0; var steps = 0;
        foreach (var row in Fixture()["edits"]!.AsArray().OfType<JsonObject>())
        {
            using var temp = new Sandbox(); var path = Path.Combine(temp.Path, "source-host.env");
            File.WriteAllBytes(path, Convert.FromBase64String(Text(row["before"])));
            var store = new SourceRepositoryStore(new CanonicalConfiguration(temp.Path)); var snapshot = row["snapshot"]!.AsObject();
            if (snapshot["valid"]!.GetValue<bool>()) Equal(snapshot["result"], store.Snapshot());
            else Check.Throws<ArgumentException>(() => store.Snapshot());
            foreach (var step in row["steps"]!.AsArray().OfType<JsonObject>())
            {
                if (step["valid"]!.GetValue<bool>()) Equal(step["result"], store.Change(Text(step["url"]), Text(step["revision"]), step["remove"]!.GetValue<bool>()));
                else Check.Throws<ArgumentException>(() => store.Change(Text(step["url"]), Text(step["revision"]), step["remove"]!.GetValue<bool>()));
                Check.True(File.ReadAllBytes(path).SequenceEqual(Convert.FromBase64String(Text(step["after"])))); steps++;
            }
            scenarios++;
        }
        Check.Equal(19, scenarios); Check.Equal(76, steps);
    }

    private static void Cas()
    {
        using var temp = new Sandbox(); var files = new DurableConfigurationFiles(temp.Path);
        files.Write("source-host.env", Encoding.UTF8.GetBytes("# unchanged comment\nEXTENSION_STORES=[]\nUNRELATED=one\n"));
        var canonical = new CanonicalConfiguration(temp.Path); var store = new SourceRepositoryStore(canonical);
        var before = store.Snapshot(); var allBefore = canonical.Revision();
        var other = Encoding.UTF8.GetBytes("# unchanged comment\nEXTENSION_STORES=[]\nUNRELATED=two\n"); files.Write("source-host.env", other);
        Check.Throws<SourceRepositoryConflictException>(() => store.Change("https://one.example/repo.json", Text(before["revision"])));
        Check.True(other.SequenceEqual(files.Read("source-host.env")!));
        var current = store.Snapshot(); var changed = store.Change("https://one.example/repo.json", Text(current["revision"]));
        Check.True(changed["restart_required"]!.GetValue<bool>()); Check.True(canonical.Revision() != allBefore);
        Check.True(Encoding.UTF8.GetString(files.Read("source-host.env")!).Contains("UNRELATED=two\n", StringComparison.Ordinal));
        Check.Equal(Hash(files.Read("source-host.env")!), Text(changed["revision"]));
        current["repositories"]!.AsArray().Add(new JsonObject { ["url"] = "https://tampered.example/" }); Check.Equal(1, store.Snapshot()["repositories"]!.AsArray().Count);
    }

    private static void ConcurrentCas()
    {
        using var temp = new Sandbox(); File.WriteAllText(Path.Combine(temp.Path, "source-host.env"), "EXTENSION_STORES=[]\n");
        var one = new SourceRepositoryStore(new CanonicalConfiguration(temp.Path)); var two = new SourceRepositoryStore(new CanonicalConfiguration(Path.Combine(temp.Path, ".")));
        var revision = Text(one.Snapshot()["revision"]); using var start = new ManualResetEventSlim();
        bool Save(SourceRepositoryStore store, string url)
        {
            start.Wait(); try { store.Change(url, revision); return true; } catch (SourceRepositoryConflictException) { return false; }
        }
        var first = Task.Run(() => Save(one, "https://one.example/repo.json")); var second = Task.Run(() => Save(two, "https://two.example/repo.json"));
        try { start.Set(); Check.True(Task.WaitAll([first, second], TimeSpan.FromSeconds(3))); Check.Equal(1, new[] { first.Result, second.Result }.Count(value => value)); Check.Equal(1, one.Snapshot()["repositories"]!.AsArray().Count); }
        finally { start.Set(); Task.WaitAll([first, second], TimeSpan.FromSeconds(3)); }
    }

    private static void Recovery()
    {
        using var temp = new Sandbox(); var files = new DurableConfigurationFiles(temp.Path);
        files.Write("autosourcarr.env", Encoding.UTF8.GetBytes("BROWSE_PAGE_SIZE=20\n")); files.Write("source-host.env", Encoding.UTF8.GetBytes("EXTENSION_STORES=[]\n"));
        var original = files.Read("source-host.env")!; var before = new JsonObject();
        foreach (var name in DurableConfigurationFiles.ManagedFiles) before[name] = files.Read(name) is { } bytes ? JsonValue.Create(Convert.ToBase64String(bytes)) : null;
        files.Write(DurableConfigurationFiles.Journal, JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["transaction"] = Guid.NewGuid().ToString(), ["before"] = before }));
        var partial = Encoding.UTF8.GetBytes("EXTENSION_STORES=[\"https://partial.example/\"]\n"); files.Write("source-host.env", partial);
        var store = new SourceRepositoryStore(new CanonicalConfiguration(temp.Path));
        Check.Throws<SourceRepositoryConflictException>(() => store.Change("https://new.example/repo.json", Hash(partial)));
        Check.True(original.SequenceEqual(files.Read("source-host.env")!)); Check.True(files.Read(DurableConfigurationFiles.Journal) is null);
        var saved = store.Change("https://new.example/repo.json", Hash(original)); Check.Equal(1, saved["repositories"]!.AsArray().Count);
    }

    private static void FileBoundaries()
    {
        using var temp = new Sandbox(); var store = new SourceRepositoryStore(new CanonicalConfiguration(temp.Path)); var path = Path.Combine(temp.Path, "source-host.env");
        Check.Throws<FileNotFoundException>(() => store.Snapshot()); Check.True(!File.Exists(path));
        var oversized = Enumerable.Repeat((byte)'x', DurableConfigurationFiles.FileLimit + 1).ToArray(); File.WriteAllBytes(path, oversized);
        Check.Throws<InvalidDataException>(() => store.Snapshot()); Check.True(oversized.SequenceEqual(File.ReadAllBytes(path)));
        var files = new DurableConfigurationFiles(temp.Path);
        Check.Throws<ArgumentException>(() => files.Write("autosourcarr.env", [], preserveSourceHostMode: true));
        Check.Throws<ArgumentException>(() => files.Write("../outside", [], preserveSourceHostMode: true));
        File.Delete(path); Directory.CreateDirectory(path); Check.Throws<InvalidDataException>(() => store.Snapshot());
    }

    private static void Permissions()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = new Sandbox(); var files = new DurableConfigurationFiles(temp.Path); files.Write("source-host.env", Encoding.UTF8.GetBytes("EXTENSION_STORES=[]\n"));
        var path = Path.Combine(temp.Path, "source-host.env"); var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead; File.SetUnixFileMode(path, mode);
        var store = new SourceRepositoryStore(new CanonicalConfiguration(temp.Path)); store.Change("https://one.example/repo.json", Text(store.Snapshot()["revision"]));
        Check.Equal(mode, File.GetUnixFileMode(path)); files.Write("autosourcarr.env", Encoding.UTF8.GetBytes("BROWSE_PAGE_SIZE=20\n"));
        Check.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(temp.Path, "autosourcarr.env")));
    }

    private static async Task Preview()
    {
        using var temp = new Sandbox(); var files = new DurableConfigurationFiles(temp.Path); files.Write("source-host.env", Encoding.UTF8.GetBytes("EXTENSION_STORES=[]\n# not touched\n")); var original = files.Read("source-host.env")!;
        var requests = new List<PublicSourceRequest>(); var fetcher = new Fetcher((request, _) =>
        {
            requests.Add(request); return Task.FromResult(new PublicSourceResponse(Manifest, new Uri("https://cdn.example/repo.json")));
        });
        await using var service = new SourceRepositoryPreview(fetcher); var result = await service.PreviewAsync("https://PUBLIC.EXAMPLE/repo.json");
        Equal(new JsonObject { ["name"] = "Fixture", ["format"] = "Suwayomi repository JSON", ["extension_count"] = 2, ["url"] = "https://public.example/repo.json",
            ["resolved_url"] = "https://cdn.example/repo.json", ["source_repository"] = null, ["source_code_status"] = SourceRepositoryPreview.UnknownStatus }, result);
        Check.Equal(1, requests.Count); Check.Equal(RepositoryManifest.MaximumBytes, requests[0].MaximumBytes); Check.Equal(TimeSpan.FromSeconds(8), requests[0].Timeout);
        Check.True(original.SequenceEqual(files.Read("source-host.env")!));
    }

    private static async Task Publishers()
    {
        async Task<JsonObject> Inspect(string index, string readme, string? resolved = null)
        {
            var fetcher = new Fetcher((request, _) => Task.FromResult(request.Url.AbsolutePath.EndsWith("README.md", StringComparison.Ordinal)
                ? new PublicSourceResponse(Encoding.UTF8.GetBytes(readme), new Uri(resolved ?? request.Url.OriginalString)) : new PublicSourceResponse(Manifest, request.Url)));
            await using var service = new SourceRepositoryPreview(fetcher); return await service.PreviewAsync(index);
        }
        var declared = await Inspect(Index, "# Source Code\nhttps://github.com/publisher/original-source\n");
        Check.Equal("https://github.com/publisher/original-source", Text(declared["source_repository"])); Check.Equal(SourceRepositoryPreview.DeclaredStatus, Text(declared["source_code_status"]));
        foreach (var readme in new[] { "# Source Code\nhttps://github.com/other/replacement\n", "# Source Code\nhttps://github.com/publisher/one\nhttps://github.com/publisher/two\n", "# Friends\nhttps://github.com/publisher/not-a-declaration\n" })
            Check.True((await Inspect(Index, readme))["source_repository"] is null);
        Check.True((await Inspect(Index, "# Source\nhttps://github.com/publisher/original\n", "https://raw.githubusercontent.com/other/extensions/repo/README.md"))["source_repository"] is null);
        Check.True((await Inspect("https://github.com/keiyoushi/unrelated/raw/repo/index.pb", "No declaration"))["source_repository"] is null);
        var known = await Inspect(SourceRepositoryStore.KeiyoushiUrl, "unused"); Check.Equal(SourceOrigin.KeiyoushiSource, Text(known["source_repository"]));
    }

    private static async Task PublicTransport()
    {
        var calls = 0;
        await using var fetcher = new PublicSourceFetcher(() => new Handler((request, _) =>
        {
            calls++; Check.True(request.Headers.Authorization is null && !request.Headers.Contains("Cookie")); Check.Equal("identity", request.Headers.AcceptEncoding.Single().Value);
            var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri("https://127.0.0.1/private"); return Task.FromResult(response);
        }));
        await using var service = new SourceRepositoryPreview(fetcher);
        await Throws<InvalidDataException>(() => service.PreviewAsync("https://public.example/repo.json")); Check.Equal(1, calls);
        await Throws<InvalidDataException>(() => service.PreviewAsync("https://127.0.0.1/repo.json")); Check.Equal(1, calls);
        var bytes = new byte[RepositoryManifest.MaximumBytes + 1];
        await using var large = new PublicSourceFetcher(() => new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
        await using var largeService = new SourceRepositoryPreview(large); await Throws<InvalidDataException>(() => largeService.PreviewAsync("https://public.example/repo.json"));
        await using var compressed = new PublicSourceFetcher(() => new Handler((_, _) =>
        {
            var content = new ByteArrayContent(Manifest); content.Headers.ContentEncoding.Add("gzip"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        await using var compressedService = new SourceRepositoryPreview(compressed); await Throws<InvalidDataException>(() => compressedService.PreviewAsync("https://public.example/repo.json"));
    }

    private static async Task Deadline()
    {
        var release = Signal(); var started = Signal(); var finished = 0; var calls = 0;
        var fetcher = new Fetcher(async (request, _) => { if (Interlocked.Increment(ref calls) == 2) started.TrySetResult(); await release.Task; Interlocked.Increment(ref finished); return new(Manifest, request.Url); });
        await using var service = new SourceRepositoryPreview(fetcher); var first = service.PreviewAsync("https://one.example/repo.json", TimeSpan.FromMilliseconds(50)); var second = service.PreviewAsync("https://two.example/repo.json", TimeSpan.FromMilliseconds(50));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); await Throws<TimeoutException>(() => first); await Throws<TimeoutException>(() => second);
            await Throws<SourceRepositoryPreviewBusyException>(() => service.PreviewAsync("https://three.example/repo.json")); Check.Equal(2, calls); Check.Equal(0, finished);
            await using var other = new SourceRepositoryPreview(new Fetcher((request, _) => Task.FromResult(new PublicSourceResponse(Manifest, request.Url))));
            Check.Equal("Fixture", Text((await other.PreviewAsync("https://other.example/repo.json"))["name"]));
        }
        finally { release.TrySetResult(); await Ignore(first); await Ignore(second); await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        Check.Equal(2, finished);
    }

    private static async Task Cancellation()
    {
        var release = Signal(); var started = Signal(); var calls = 0; using var cancel = new CancellationTokenSource();
        var fetcher = new Fetcher(async (request, _) => { if (Interlocked.Increment(ref calls) == 2) started.TrySetResult(); await release.Task; return new(Manifest, request.Url); });
        await using var service = new SourceRepositoryPreview(fetcher); var first = service.PreviewAsync("https://one.example/repo.json", cancellationToken: cancel.Token); var second = service.PreviewAsync("https://two.example/repo.json");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel(); await Throws<OperationCanceledException>(() => first);
            await Throws<SourceRepositoryPreviewBusyException>(() => service.PreviewAsync("https://three.example/repo.json")); Check.Equal(2, calls);
        }
        finally { release.TrySetResult(); await Ignore(first); await Ignore(second); await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    private static async Task Shutdown()
    {
        var release = Signal(); var started = Signal();
        var fetcher = new Fetcher(async (request, _) => { started.TrySetResult(); await release.Task; return new(Manifest, request.Url); });
        await using var service = new SourceRepositoryPreview(fetcher); var task = service.PreviewAsync("https://one.example/repo.json"); Task? stop = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); stop = service.StopAsync(); Check.True(!stop.IsCompleted);
            await Throws<OperationCanceledException>(() => service.PreviewAsync("https://two.example/repo.json")); release.TrySetResult();
            await Throws<OperationCanceledException>(() => task); await stop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.TrySetResult(); await Ignore(task); await (stop ?? service.StopAsync()).WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    private static async Task FailedCancellation()
    {
        var release = Signal(); var started = Signal(); var callback = Signal(); var finished = 0; CancellationToken captured = default;
        var fetcher = new Fetcher(async (request, token) =>
        {
            captured = token;
            using var registration = token.Register(() => { callback.TrySetResult(); throw new InvalidOperationException("controlled preview cancellation callback failure"); });
            started.TrySetResult();
            try { await release.Task; return new(Manifest, request.Url); }
            finally { Interlocked.Increment(ref finished); }
        });
        var service = new SourceRepositoryPreview(fetcher);
        // Observe actual resource disposal without adding a production-only testing API.
        var slots = (SemaphoreSlim)typeof(SourceRepositoryPreview).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var task = service.PreviewAsync("https://one.example/repo.json"); Task? stop = null, dispose = null, repeat = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); stop = service.StopAsync();
            dispose = service.DisposeAsync().AsTask(); repeat = service.DisposeAsync().AsTask();
            await callback.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check.True(!stop.IsCompleted && !dispose.IsCompleted && !repeat.IsCompleted); Check.Equal(0, finished);
            Check.True(ReferenceEquals(stop, service.StopAsync()));
            await Throws<OperationCanceledException>(() => service.PreviewAsync("https://two.example/repo.json"));
            release.TrySetResult(); await Throws<OperationCanceledException>(() => task);
            var failure = await Throws<AggregateException>(() => stop); Check.True(failure.Flatten().InnerExceptions.Any(error => error.Message == "controlled preview cancellation callback failure"));
            await Throws<AggregateException>(() => dispose); await Throws<AggregateException>(() => repeat); Check.Equal(1, finished);
            Check.Throws<ObjectDisposedException>(() => slots.Wait(0)); Check.Throws<ObjectDisposedException>(() => { _ = captured.WaitHandle; });
            await Throws<AggregateException>(() => service.DisposeAsync().AsTask());
        }
        finally
        {
            release.TrySetResult(); await Ignore(task);
            if (stop is not null) await Ignore(stop); if (dispose is not null) await Ignore(dispose); if (repeat is not null) await Ignore(repeat);
            try { await service.DisposeAsync(); } catch (AggregateException) { }
        }
    }

    private static async Task DeclarationShutdown()
    {
        var release = Signal(); var started = Signal();
        var fetcher = new Fetcher(async (request, _) =>
        {
            if (!request.Url.AbsolutePath.EndsWith("README.md", StringComparison.Ordinal)) return new(Manifest, request.Url);
            Check.Equal(128 * 1024, request.MaximumBytes); Check.True(request.Timeout > TimeSpan.Zero && request.Timeout <= TimeSpan.FromSeconds(5));
            started.TrySetResult(); await release.Task; return new(Encoding.UTF8.GetBytes("# Source\nhttps://github.com/publisher/source\n"), request.Url);
        });
        await using var service = new SourceRepositoryPreview(fetcher); var task = service.PreviewAsync(Index); Task? stop = null;
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); stop = service.StopAsync(); Check.True(!stop.IsCompleted); release.TrySetResult(); await Throws<OperationCanceledException>(() => task); await stop.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { release.TrySetResult(); await Ignore(task); await (stop ?? service.StopAsync()).WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    private static void GzipIntegrity()
    {
        var row = Fixture()["cases"]!.AsArray().OfType<JsonObject>().First(item => Text(item["kind"]) == "manifest" && item["valid"]!.GetValue<bool>() && Convert.FromBase64String(Text(item["body"])).AsSpan().StartsWith(new byte[] { 0x1f, 0x8b }));
        var gzip = Convert.FromBase64String(Text(row["body"])); var crc = gzip.ToArray(); crc[^8] ^= 1; Check.Throws<InvalidDataException>(() => RepositoryManifest.Parse(crc));
        var size = gzip.ToArray(); size[^4] ^= 1; Check.Throws<InvalidDataException>(() => RepositoryManifest.Parse(size));
        Check.Throws<InvalidDataException>(() => RepositoryManifest.Parse(new byte[RepositoryManifest.MaximumBytes + 1]));
        Check.Throws<InvalidDataException>(() => RepositoryManifest.Parse(Encoding.UTF8.GetBytes(new string('[', 34) + "0" + new string(']', 34))));
    }

    private static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("source-repositories.json")))!.AsObject();
    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? "";
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static void Equal(JsonNode? expected, JsonNode? actual) => Check.JsonEqual(JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(actual));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Ignore(Task task) { try { await task.WaitAsync(TimeSpan.FromSeconds(3)); } catch { } }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(3)); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class Fetcher(Func<PublicSourceRequest, CancellationToken, Task<PublicSourceResponse>> fetch) : IPublicSourceFetcher
    {
        public Task<PublicSourceResponse> FetchAsync(PublicSourceRequest request, CancellationToken cancellationToken = default) => fetch(request, cancellationToken);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
