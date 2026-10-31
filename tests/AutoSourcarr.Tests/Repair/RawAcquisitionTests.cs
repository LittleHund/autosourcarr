using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Tests;

public static class RawAcquisitionTests
{
    private const string Package = "eu.kanade.tachiyomi.extension.en.example", Module = "src/en/example";
    private static JsonObject Extension(string version = "1.4.23") => new() { ["pkgName"] = Package, ["versionName"] = version, ["storeIndexUrl"] = SourceOrigin.KeiyoushiIndex, ["isInstalled"] = true };
    private static byte[] Zip(bool module = true)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (name, text) in new Dictionary<string, string> { ["LICENSE"] = "PUBLIC_LICENSE", ["gradlew"] = "#!/bin/sh\n", [(module ? Module : "src/en/other") + "/src/Example.kt"] = "class Example {}" })
            { var info = zip.CreateEntry(name); info.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero); using var entry = info.Open(); entry.Write(Encoding.UTF8.GetBytes(text)); }
        return output.ToArray();
    }
    private static JsonObject Meta(string version = "1.4.23") => new() { ["package"] = Package, ["version"] = version, ["module"] = Module, ["ecosystem"] = "keiyoushi", ["recipe"] = "gradle", ["kind"] = "provided", ["origin"] = new JsonObject { ["basis"] = "user-provided" } };
    private static string Text(JsonObject value, string key) => value[key]!.GetValue<string>();
    private static long Revision(JsonObject value) => value["revision"]!.GetValue<long>();
    public static void Register(TestSuite suite)
    {
        suite.Add("raw acquisition: normal install, restart and status retain code without new network", () =>
        {
            using var sandbox = new Sandbox(); var calls = 0;
            var fetch = new Fetch((request, _) => { calls++; return Task.FromResult(Response(request, request.Url.AbsolutePath.EndsWith("/commits/HEAD", StringComparison.Ordinal) ? Encoding.ASCII.GetBytes(new string('a', 40)) : Zip())); });
            var shared = new RawStore(Path.Combine(sandbox.Path, "sources", "raw"), true); var privateStore = new RawStore(Path.Combine(sandbox.Path, "instance", "raw-private"));
            var documents = new PrivateDocumentStore(Path.Combine(sandbox.Path, "instance", "raw-requests")); var repo = new RawRepositoryCache(Path.Combine(sandbox.Path, "downloads"), shared, fetch, () => 1000);
            try
            {
                var catalog = new SourceRawCatalog(shared, privateStore, documents, repo, fetch, () => 1000);
                var intent = catalog.Begin(Extension(), binarySha256: new string('1', 64)); Check.Equal("acquiring", Text(intent, "state"));
                var result = catalog.AcquireAsync(intent).GetAwaiter().GetResult()!; Check.Equal("available", Text(result, "state")); Check.Equal("shared", Text(result, "visibility")); Check.Equal(2, calls);
                var resolved = catalog.Resolve(Package, Text(result, "raw_id")); Check.True(resolved.Store.Public); Check.Equal("upstream", Text(resolved.Snapshot, "kind"));
                var reopened = new SourceRawCatalog(shared, privateStore, documents, repo, fetch); Check.Equal(Text(result, "raw_id"), Text(reopened.Status(Package)!, "raw_id")); Check.Equal(2, calls);
                Check.Equal(Revision(result), Revision(reopened.Begin(Extension(), binarySha256: new string('1', 64)))); Check.Equal(2, calls);
            }
            finally { repo.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("raw acquisition: manual uploads stay private and stale dialogs cannot replace newer code", () =>
        {
            using var setup = new CatalogSetup(); var catalog = setup.Catalog;
            var first = catalog.Begin(Extension(), manual: true, binarySha256: new string('1', 64));
            Check.Equal("needs-input", Text(first, "state")); Check.Equal(1, catalog.PendingItems().Count); Check.Equal(0, setup.FetchCalls);
            var result = catalog.Provide(Extension(), Zip(), Revision(first), new string('1', 64)); Check.Equal("private", Text(result, "visibility"));
            Check.Throws<SourceRawConflictException>(() => catalog.Provide(Extension(), Zip(), Revision(first), new string('1', 64)));
            Check.True(!catalog.Resolve(Package, Text(result, "raw_id")).Store.Public); Check.Equal(0, setup.Shared.Versions(Package).Count);
            var newer = catalog.Begin(Extension("1.4.24"), manual: true, binarySha256: new string('2', 64));
            Check.Throws<SourceRawConflictException>(() => catalog.Provide(Extension(), Zip(), Revision(newer), new string('1', 64)));
            Check.Equal("1.4.24", Text(catalog.Status(Package)!, "version")); Check.Equal(1, setup.Private.Versions(Package).Count);
        });
        suite.Add("raw acquisition: late completed download cannot replace a newly installed version", () =>
        {
            using var setup = new CatalogSetup(); var release = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            setup.Repository.Obtain = (_, token) => release.Task.WaitAsync(token);
            var old = setup.Catalog.Begin(Extension(), binarySha256: new string('1', 64)); var pending = setup.Catalog.AcquireAsync(old);
            var current = setup.Catalog.Begin(Extension("1.4.24"), manual: true, binarySha256: new string('2', 64));
            var retained = setup.Shared.PutZip(Zip(), Meta()); release.SetResult(retained);
            var result = pending.GetAwaiter().GetResult()!; Check.Equal(Revision(current), Revision(result)); Check.Equal("1.4.24", Text(result, "version")); Check.True(result["raw_id"] is null);
            Check.Equal(Text(retained, "id"), Text(setup.Catalog.Resolve(Package, Text(retained, "id")).Snapshot, "id"));
        });
        suite.Add("raw acquisition: invalid bounded repository results leave an actionable fenced journal", () =>
        {
            foreach (var failure in new[] { "archive", "package", "version", "hash" })
            {
                using var setup = new CatalogSetup(); var first = setup.Catalog.Begin(Extension(), binarySha256: new string('1', 64));
                setup.Repository.Obtain = (_, _) =>
                {
                    if (failure == "archive") throw new InvalidDataException("Synthetic invalid archive");
                    return Task.FromResult(new JsonObject { ["package"] = failure == "package" ? "different.package.name" : Package,
                        ["version"] = failure == "version" ? "9.9.9" : "1.4.23", ["id"] = failure == "hash" ? "not-a-hash" : new string('a', 64), ["origin"] = new JsonObject() });
                };
                var result = setup.Catalog.AcquireAsync(first).GetAwaiter().GetResult()!;
                Check.Equal("needs-input", Text(result, "state")); Check.Equal(Revision(first) + 1, Revision(result)); Check.True(result["raw_id"] is null);
                Check.Equal("needs-input", Text(setup.Reopen().Status(Package)!, "state")); Check.Equal(0, setup.Shared.Versions(Package).Count);
            }
            using var cancelledSetup = new CatalogSetup(); using var cancel = new CancellationTokenSource();
            var acquiring = cancelledSetup.Catalog.Begin(Extension(), binarySha256: new string('1', 64)); cancel.Cancel();
            cancelledSetup.Repository.Obtain = (_, token) => Task.FromCanceled<JsonObject>(token);
            Check.Throws<OperationCanceledException>(() => cancelledSetup.Catalog.AcquireAsync(acquiring, cancel.Token).GetAwaiter().GetResult());
            Check.Equal("acquiring", Text(cancelledSetup.Catalog.Status(Package)!, "state")); Check.Equal(Revision(acquiring), Revision(cancelledSetup.Catalog.Status(Package)!));
        });
        suite.Add("raw acquisition: current discovery receipts are candidates and explicit retries remain available", () =>
        {
            using var setup = new CatalogSetup(); var extension = Extension(); extension["storeIndexUrl"] = "";
            setup.FetchOperation = (request, _) => Task.FromResult(Response(request, Encoding.UTF8.GetBytes(request.Url.AbsolutePath.EndsWith("README.md", StringComparison.Ordinal)
                ? "# Source Code\nhttps://github.com/publisher/original-source\n" : "{\"extensionList\":{\"extensions\":[{\"packageName\":\"" + Package + "\",\"versionName\":\"9.9.9\"}]}}")));
            var first = setup.Catalog.Begin(extension, binarySha256: new string('1', 64));
            var found = setup.Catalog.DiscoverAsync(first, extension, ["https://raw.githubusercontent.com/publisher/extensions/repo/index.json"]).GetAwaiter().GetResult();
            Check.Equal("acquiring", Text(found, "state")); Check.Equal(SourceDiscovery.CapabilityVersion, found["discovery_version"]!.GetValue<int>());
            Check.Equal("repository-candidate", Text(found["origin"]!.AsObject(), "basis")); Check.Equal("1.4.23", Text(found, "version")); Check.True(found["raw_id"] is null);
            var unknown = setup.Catalog.Begin(extension, manual: true, binarySha256: new string('1', 64), force: true);
            var negative = setup.Catalog.DiscoverAsync(unknown, extension, []).GetAwaiter().GetResult();
            Check.Equal("needs-input", Text(negative, "state"));
            var retried = setup.Catalog.DiscoverAsync(negative, extension, ["https://raw.githubusercontent.com/publisher/extensions/repo/index.json"]).GetAwaiter().GetResult();
            Check.Equal("acquiring", Text(retried, "state")); Check.True(Revision(retried) > Revision(negative));
        });
        suite.Add("raw acquisition: discovery rechecks package, revision, cancellation and installed version before publication", () =>
        {
            using var setup = new CatalogSetup(); var extension = Extension(); extension["storeIndexUrl"] = "";
            var first = setup.Catalog.Begin(extension, binarySha256: new string('1', 64)); var different = (JsonObject)extension.DeepClone(); different["pkgName"] = "different.package.name";
            Check.Throws<InvalidDataException>(() => setup.Catalog.DiscoverAsync(first, different, []).GetAwaiter().GetResult()); Check.Equal(0, setup.FetchCalls);
            var release = new TaskCompletionSource<PublicSourceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            setup.FetchOperation = (request, _) => request.Url.AbsolutePath.EndsWith("README.md", StringComparison.Ordinal)
                ? Task.FromResult(Response(request, Encoding.UTF8.GetBytes("# Source Code\nhttps://github.com/publisher/original-source\n"))) : release.Task;
            var delayed = setup.Catalog.DiscoverAsync(first, extension, ["https://raw.githubusercontent.com/publisher/extensions/repo/index.json"]);
            var response = new PublicSourceResponse(Encoding.UTF8.GetBytes("[{\"pkg\":\"" + Package + "\"}]"), new("https://raw.githubusercontent.com/publisher/extensions/repo/index.json"));
            try
            {
                var newer = setup.Catalog.Begin(Extension("1.4.24"), manual: true, binarySha256: new string('2', 64)); release.TrySetResult(response);
                Check.Throws<SourceRawConflictException>(() => delayed.GetAwaiter().GetResult()); Check.Equal(Revision(newer), Revision(setup.Catalog.Status(Package)!));
                Check.Equal("1.4.24", Text(setup.Catalog.Status(Package)!, "version"));
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                Check.Throws<OperationCanceledException>(() => setup.Catalog.DiscoverAsync(newer, Extension("1.4.24"), [], cancel.Token).GetAwaiter().GetResult());
                Check.Equal(Revision(newer), Revision(setup.Catalog.Status(Package)!));
            }
            finally
            {
                release.TrySetResult(response);
                try { delayed.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); } catch (SourceRawConflictException) { }
            }
        });
        suite.Add("raw acquisition: task rendering is cached and corrupt request journals are preserved", () =>
        {
            using var setup = new CatalogSetup(); setup.Catalog.Begin(Extension(), manual: true, binarySha256: new string('1', 64)); setup.Documents.Block = true;
            Check.Equal(1, setup.Catalog.PendingItems().Count); Check.Equal("needs-input", Text(setup.Catalog.Status(Package)!, "state")); Check.Equal(1, setup.Catalog.Cached().Count);
            setup.Documents.Block = false; var path = Directory.EnumerateFiles(setup.RequestRoot, "*.json").Single(); File.WriteAllText(path, "{damaged");
            Check.Throws<InvalidDataException>(() => setup.Reopen()); Check.Equal("{damaged", File.ReadAllText(path));
        });
        suite.Add("raw acquisition: cached status remains responsive while a journal write is physically blocked", () =>
        {
            using var setup = new CatalogSetup(); var first = setup.Catalog.Begin(Extension(), manual: true, binarySha256: new string('1', 64));
            var available = setup.Catalog.Provide(Extension(), Zip(), Revision(first), new string('1', 64));
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            setup.Documents.BeforeWrite = () => { entered.Set(); release.Wait(); };
            var writer = Task.Run(() => setup.Catalog.RequestInput(Package, Revision(available), new string('1', 64))); Task<JsonObject?>? read = null;
            try
            {
                Check.True(entered.Wait(TimeSpan.FromSeconds(3))); read = Task.Run(() => setup.Catalog.Status(Package));
                Check.True(read.Wait(TimeSpan.FromSeconds(3)), "Observed status waited for filesystem work"); Check.Equal("available", Text(read.Result!, "state"));
            }
            finally { release.Set(); writer.GetAwaiter().GetResult(); read?.GetAwaiter().GetResult(); }
            Check.Equal("needs-input", Text(setup.Catalog.Status(Package)!, "state"));
        });
        suite.Add("raw acquisition: portable code recipes and retained candidate activation keep feedback separate", () =>
        {
            using var setup = new CatalogSetup(); var meta = Meta(); meta["recipe"] = "jvm"; meta["module"] = "."; meta["kind"] = "repaired"; meta["overlay_sources"] = new JsonArray("src/Actual.java");
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) { using var entry = zip.CreateEntry("src/Actual.java").Open(); entry.Write("class Actual {}"u8); }
            var source = setup.Private.PutZip(memory.ToArray(), meta); var portable = setup.Private.ExportZip(Package, Text(source, "id"), true);
            var first = setup.Catalog.Begin(Extension(), manual: true, binarySha256: new string('1', 64));
            var supplied = setup.Catalog.Provide(Extension(), portable, Revision(first), new string('1', 64)); var saved = setup.Catalog.Resolve(Package, Text(supplied, "raw_id")).Snapshot;
            Check.Equal("jvm", Text(saved, "recipe")); Check.Equal(".", Text(saved, "module")); Check.Equal("provided", Text(saved, "kind"));
            var request = setup.Catalog.RequestInput(Package, Revision(supplied), new string('1', 64));
            setup.Catalog.Retain(Package, portable, meta, Revision(request), activate: true, expectedBinary: new string('2', 64)); Check.Equal("needs-input", Text(setup.Catalog.Status(Package)!, "state"));
            setup.Catalog.Retain(Package, portable, meta, Revision(request), activate: true, expectedBinary: new string('1', 64)); Check.Equal("available", Text(setup.Catalog.Status(Package)!, "state"));
            var adopted = setup.Catalog.Adopt(Extension(), new string('2', 64), Text(source, "id")); Check.True(adopted["confirmed_at"] is null); Check.True(Text(adopted, "message").Contains("Fixed confirmation remains separate", StringComparison.Ordinal));
            setup.Catalog.Removed(Package); Check.Equal("removed", Text(setup.Catalog.Status(Package)!, "state")); Check.True(setup.Private.Versions(Package).Count > 0);
        });
        suite.Add("raw acquisition: corrupt private snapshot never falls through to a matching shared version", () =>
        {
            using var setup = new CatalogSetup(); var snapshot = setup.Private.PutZip(Zip(), Meta()); setup.Shared.PutZip(Zip(), Meta());
            var path = Directory.EnumerateFiles(Path.Combine(setup.Private.Root, "versions"), "*.json", SearchOption.AllDirectories).Single(); File.WriteAllText(path, "{damaged");
            Check.Throws<InvalidDataException>(() => setup.Catalog.Resolve(Package, Text(snapshot, "id"))); Check.Equal("{damaged", File.ReadAllText(path));
        });
        suite.Add("repository cache: pinned downloads reuse bytes and corruption never triggers silent replacement", () =>
        {
            using var sandbox = new Sandbox(); var calls = 0; var store = new RawStore(Path.Combine(sandbox.Path, "shared"), true);
            var fetch = new Fetch((request, _) => { calls++; return Task.FromResult(Response(request, request.Url.AbsolutePath.EndsWith("/commits/HEAD", StringComparison.Ordinal) ? Encoding.ASCII.GetBytes(new string('a', 40)) : Zip())); });
            var root = Path.Combine(sandbox.Path, "cache"); var repo = new RawRepositoryCache(root, store, fetch, () => 1000); var origin = SourceOrigin.Resolve(Extension());
            try
            {
                var first = repo.ObtainAsync(origin).GetAwaiter().GetResult(); Check.Equal(Text(first, "id"), Text(repo.ObtainAsync(origin).GetAwaiter().GetResult(), "id")); Check.Equal(2, calls);
                var archive = Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories).Single(); File.WriteAllText(archive, "TAMPERED");
                Check.Throws<InvalidDataException>(() => repo.ObtainAsync(origin).GetAwaiter().GetResult()); Check.Equal(2, calls); Check.Equal("TAMPERED", File.ReadAllText(archive));
                Check.True(store.ExportZip(Package, Text(first, "id")).Length > 0);
            }
            finally { repo.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("repository cache: removed module uses bounded original history as candidate only", () =>
        {
            using var sandbox = new Sandbox(); var old = new string('a', 40); var previous = new string('b', 40); var matching = new string('c', 40); var calls = new List<string>();
            var fetch = new Fetch((request, _) =>
            {
                var url = request.Url.AbsoluteUri; calls.Add(url); byte[] bytes;
                if (url.EndsWith("/commits/HEAD", StringComparison.Ordinal)) bytes = Encoding.ASCII.GetBytes(old);
                else if (request.Url.Query.Contains("per_page=8", StringComparison.Ordinal)) bytes = Encoding.UTF8.GetBytes("[{\"sha\":\"" + previous + "\",\"parents\":[{\"sha\":\"" + matching + "\"}]}]");
                else if (url.EndsWith("build.gradle.kts", StringComparison.Ordinal)) bytes = Encoding.UTF8.GetBytes("libVersion = \"1.4\"\nextVersionCode = " + (url.Contains(matching, StringComparison.Ordinal) ? "23" : "22"));
                else bytes = Zip(module: !url.EndsWith(old, StringComparison.Ordinal));
                return Task.FromResult(Response(request, bytes));
            });
            var store = new RawStore(Path.Combine(sandbox.Path, "shared"), true); var repo = new RawRepositoryCache(Path.Combine(sandbox.Path, "cache"), store, fetch, () => 1000);
            try
            {
                var result = repo.ObtainAsync(SourceOrigin.Resolve(Extension())).GetAwaiter().GetResult(); Check.Equal("provided", Text(result, "kind"));
                Check.Equal("repository-candidate", result["origin"]!["basis"]!.GetValue<string>()); Check.Equal(matching, result["origin"]!["revision"]!.GetValue<string>());
                Check.Equal(6, calls.Count); Check.True(calls.All(url => !url.Contains("unrelated", StringComparison.Ordinal)));
            }
            finally { repo.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("repository cache: pruning disposable downloads retains every immutable raw version", () =>
        {
            using var sandbox = new Sandbox(); var now = 1000.0; var heads = 0;
            var fetch = new Fetch((request, _) => Task.FromResult(Response(request, request.Url.AbsolutePath.EndsWith("/commits/HEAD", StringComparison.Ordinal) ? Encoding.ASCII.GetBytes(new string((char)('a' + heads++), 40)) : Zip())));
            var store = new RawStore(Path.Combine(sandbox.Path, "shared"), true); var root = Path.Combine(sandbox.Path, "cache"); var repo = new RawRepositoryCache(root, store, fetch, () => now);
            try
            {
                for (var i = 0; i < 4; i++) { _ = repo.ObtainAsync(SourceOrigin.Resolve(Extension())).GetAwaiter().GetResult(); now += 301; }
                Check.Equal(4, store.Versions(Package).Count); Check.Equal(3, Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Count(path => Path.GetFileName(path) != "head.json"));
                Check.Equal(1, Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories).Count());
            }
            finally { repo.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("repository cache: cancellation releases a queued cross-owner lease and joins active work", () =>
        {
            using var sandbox = new Sandbox(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fetch = new Fetch(async (request, token) =>
            {
                if (request.Url.AbsolutePath.EndsWith("/commits/HEAD", StringComparison.Ordinal)) { entered.TrySetResult(); await release.Task.WaitAsync(token); return Response(request, Encoding.ASCII.GetBytes(new string('a', 40))); }
                return Response(request, Zip());
            });
            var store = new RawStore(Path.Combine(sandbox.Path, "shared"), true); var root = Path.Combine(sandbox.Path, "cache"); var first = new RawRepositoryCache(root, store, fetch); var second = new RawRepositoryCache(root, store, fetch);
            try
            {
                var active = first.ObtainAsync(SourceOrigin.Resolve(Extension())); entered.Task.GetAwaiter().GetResult();
                var queued = second.ObtainAsync(SourceOrigin.Resolve(Extension())); Check.True(!queued.IsCompleted); second.StopAsync().GetAwaiter().GetResult();
                Check.Throws<OperationCanceledException>(() => queued.GetAwaiter().GetResult()); release.SetResult(); Check.True(active.GetAwaiter().GetResult()["id"] is not null);
            }
            finally { first.DisposeAsync().AsTask().GetAwaiter().GetResult(); second.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }
    private static PublicSourceResponse Response(PublicSourceRequest request, byte[] bytes) => new(bytes, request.Url);
    private sealed class Fetch(Func<PublicSourceRequest, CancellationToken, Task<PublicSourceResponse>> operation) : IPublicSourceFetcher
    { public Task<PublicSourceResponse> FetchAsync(PublicSourceRequest request, CancellationToken cancellationToken = default) => operation(request, cancellationToken); }
    private sealed class Repository : IRawRepositorySource
    {
        public Func<JsonObject, CancellationToken, Task<JsonObject>> Obtain = (_, _) => throw new IOException("Original source is unavailable");
        public Task<JsonObject> ObtainAsync(JsonObject origin, CancellationToken cancellationToken = default) => Obtain(origin, cancellationToken);
    }
    private sealed class DocumentGuard(IPrivateDocumentStore inner) : IPrivateDocumentStore
    {
        public bool Block;
        public Action? BeforeWrite;
        public IReadOnlyList<string> JsonDocuments(int maximum) { Check.True(!Block, "Cached rendering performed filesystem I/O"); return inner.JsonDocuments(maximum); }
        public T Locked<T>(string name, Func<IPrivateDocument, T> operation, TimeSpan? timeout = null) { Check.True(!Block, "Cached rendering performed filesystem I/O"); return inner.Locked(name, document => operation(new GuardedDocument(document, () => BeforeWrite?.Invoke())), timeout); }
        private sealed class GuardedDocument(IPrivateDocument inner, Action beforeWrite) : IPrivateDocument
        { public byte[]? Read(int maximumBytes) => inner.Read(maximumBytes); public void Write(byte[] bytes) { beforeWrite(); inner.Write(bytes); } }
    }
    private sealed class CatalogSetup : IDisposable
    {
        private readonly Sandbox _sandbox = new();
        public string RequestRoot { get; }
        public RawStore Shared { get; }
        public RawStore Private { get; }
        public Repository Repository { get; } = new();
        public DocumentGuard Documents { get; }
        public SourceRawCatalog Catalog { get; }
        public int FetchCalls;
        public Func<PublicSourceRequest, CancellationToken, Task<PublicSourceResponse>> FetchOperation = (_, _) => throw new IOException("No source was fetched");
        private readonly Fetch _fetch;
        public CatalogSetup()
        {
            Shared = new(Path.Combine(_sandbox.Path, "sources", "raw"), true); Private = new(Path.Combine(_sandbox.Path, "instance", "raw-private")); RequestRoot = Path.Combine(_sandbox.Path, "instance", "raw-requests");
            Documents = new(new PrivateDocumentStore(RequestRoot)); _fetch = new((request, token) => { FetchCalls++; return FetchOperation(request, token); }); Catalog = Reopen();
        }
        public SourceRawCatalog Reopen() => new(Shared, Private, Documents, Repository, _fetch, () => 1000);
        public void Dispose() => _sandbox.Dispose();
    }
}
