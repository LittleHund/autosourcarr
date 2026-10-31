using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Repair;

public interface IRawRepositorySource
{
    Task<JsonObject> ObtainAsync(JsonObject origin, CancellationToken cancellationToken = default);
}

/// <summary>Private disposable repository downloads; selected immutable code alone enters the shared store.</summary>
public sealed class RawRepositoryCache : IRawRepositorySource, IAsyncDisposable
{
    private readonly string _root;
    private readonly RawStore _store;
    private readonly IPublicSourceFetcher _fetcher;
    private readonly Func<double> _clock;
    private readonly OperationOwner _owner = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public RawRepositoryCache(string root, RawStore store, IPublicSourceFetcher fetcher, Func<double>? clock = null)
    { _root = Path.GetFullPath(root); _store = store; _fetcher = fetcher; _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0); PrivateDocumentStore.ValidateDirectory(_root); }
    public async Task<JsonObject> ObtainAsync(JsonObject origin, CancellationToken cancellationToken = default)
    {
        using var owned = _owner.Enter(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken); var token = stop.Token;
        if (SourceOrigin.Optional(origin, "state") != "known") throw new InvalidDataException("Original raw code is required; provide it or choose Unavailable");
        var plan = SourceOrigin.RepositoryPlan(RawCodeRules.Text(origin, "repository")); var basis = SourceOrigin.Optional(origin, "basis"); if (basis.Length == 0) basis = "user-provided";
        var sourceOrigin = new JsonObject { ["repository"] = plan.Repository, ["basis"] = basis }; if (RawCodeRules.Present(origin["index_url"])) sourceOrigin["index_url"] = origin["index_url"]!.DeepClone();
        var meta = RawCodeRules.Metadata(new JsonObject { ["package"] = origin["package"]?.DeepClone(), ["version"] = origin["version"]?.DeepClone(), ["module"] = origin["module"]?.DeepClone(),
            ["ecosystem"] = origin["ecosystem"]?.DeepClone(), ["recipe"] = "gradle", ["kind"] = basis == "known-repository" ? "upstream" : "provided", ["origin"] = sourceOrigin });
        var root = Path.Combine(_root, RawCodeRules.Digest(Encoding.UTF8.GetBytes(plan.Repository))); PrivateDocumentStore.ValidateDirectory(root);
        await using var locked = await SourceFileLease.AcquireAsync(Path.Combine(root, "fetch.lock"), TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
        var revision = await ResolveAsync(root, plan, SourceOrigin.Optional(origin, "revision"), token).ConfigureAwait(false);
        var path = await DownloadAsync(root, plan, revision, token).ConfigureAwait(false); meta["origin"]!["revision"] = revision;
        JsonObject result;
        try { token.ThrowIfCancellationRequested(); result = _store.PutRepositoryFile(path, meta); }
        catch (InvalidDataException error) when (SourceOrigin.Optional(origin, "revision").Length == 0 && error.Message == "No Kotlin/Java source files exist in the selected project module")
        {
            revision = await HistoricalModuleAsync(plan, RawCodeRules.Text(meta, "module"), RawCodeRules.Text(meta, "version"), token).ConfigureAwait(false);
            path = await DownloadAsync(root, plan, revision, token).ConfigureAwait(false); meta["kind"] = "provided"; meta["origin"]!["revision"] = revision; meta["origin"]!["basis"] = "repository-candidate";
            token.ThrowIfCancellationRequested(); result = _store.PutRepositoryFile(path, meta);
        }
        Prune(root, revision); token.ThrowIfCancellationRequested(); return result;
    }
    private async Task<string> ResolveAsync(string root, SourceRepositoryPlan plan, string revision, CancellationToken token)
    {
        if (revision.Length != 0) return RawCodeRules.Commit(revision);
        var saved = ReadJson(Path.Combine(root, "head.json"));
        if (saved is not null && SourceOrigin.Optional(saved, "repository") == plan.Repository && Number(saved["fetched_at"], out var fetched) && _clock() - fetched is >= 0 and <= 300)
        { var sha = SourceOrigin.Optional(saved, "revision"); try { return RawCodeRules.Commit(sha); } catch (InvalidDataException) { } }
        var raw = await FetchAsync(plan.ResolveUrl, 512 * 1024, TimeSpan.FromSeconds(30), token, plan.Host == "github.com" ? "application/vnd.github.sha" : null).ConfigureAwait(false);
        var text = StrictUtf8.GetString(raw).Trim();
        if (!Regex.IsMatch(text, "\\A[0-9a-f]{40}\\z", RegexOptions.CultureInvariant))
        {
            var body = Parse(raw); text = SourceOrigin.Optional(body, plan.Host == "github.com" ? "sha" : "id");
        }
        revision = RawCodeRules.Commit(text);
        WriteJson(Path.Combine(root, "head.json"), new() { ["repository"] = plan.Repository, ["revision"] = revision, ["fetched_at"] = _clock() }); return revision;
    }
    private async Task<string> DownloadAsync(string root, SourceRepositoryPlan plan, string revision, CancellationToken token)
    {
        var reference = Path.Combine(root, "commits", revision + ".json"); var saved = ReadJson(reference);
        if (saved is not null)
        {
            var key = RawCodeRules.Hash(SourceOrigin.Optional(saved, "archive_sha256"));
            if (SourceOrigin.Optional(saved, "repository") != plan.Repository || SourceOrigin.Optional(saved, "revision") != revision || !Integer(saved["bytes"], out var size) || size is < 1 or > RawCodeRules.MaximumRepositoryZip)
                throw new InvalidDataException("Repository download cache has an invalid identity");
            var previous = Path.Combine(root, "archives", key + ".zip"); var bytes = AtomicSourceFiles.Read(previous, RawCodeRules.MaximumRepositoryZip);
            if (bytes is not null)
            { if (bytes.Length != size || RawCodeRules.Digest(bytes) != key) throw new InvalidDataException("Repository download cache changed; it was not trusted"); return previous; }
        }
        var payload = await FetchAsync(SourceOrigin.RepositoryArchive(plan, revision), RawCodeRules.MaximumRepositoryZip, TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
        RawArchive.ValidateContainer(payload, repository: true); token.ThrowIfCancellationRequested();
        var hash = RawCodeRules.Digest(payload); var path = Path.Combine(root, "archives", hash + ".zip");
        AtomicSourceFiles.Write(path, payload, RawCodeRules.MaximumRepositoryZip, immutable: true);
        WriteJson(reference, new() { ["repository"] = plan.Repository, ["revision"] = revision, ["archive_sha256"] = hash, ["bytes"] = payload.Length, ["fetched_at"] = _clock() }); return path;
    }
    private async Task<string> HistoricalModuleAsync(SourceRepositoryPlan plan, string module, string version, CancellationToken token)
    {
        if (plan.Host != "github.com" || !module.StartsWith("src/", StringComparison.Ordinal)) throw new InvalidDataException("The selected module is absent; provide its original source revision/project");
        var watch = Stopwatch.StartNew(); var bytes = await FetchAsync("https://api.github.com/repos/" + plan.Path + "/commits?path=" + Uri.EscapeDataString(module) + "&per_page=8", 512 * 1024, TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
        var rows = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonArray;
        if (rows is null || rows.Count > 8) throw new InvalidDataException("Original repository module history is unavailable");
        var candidates = new List<string>();
        void Add(JsonNode? value) { if (value is JsonValue scalar && scalar.TryGetValue<string>(out var sha) && Regex.IsMatch(sha, "\\A[0-9a-f]{40}\\z", RegexOptions.CultureInvariant) && !candidates.Contains(sha, StringComparer.Ordinal)) candidates.Add(sha); }
        foreach (var row in rows.OfType<JsonObject>())
        {
            Add(row["sha"]);
            if (row["parents"] is not null && row["parents"] is not JsonArray) throw new InvalidDataException("Original repository module parents are malformed");
            if (row["parents"] is JsonArray parents) foreach (var parent in parents.Take(2).OfType<JsonObject>()) Add(parent["sha"]);
        }
        string? fallback = null;
        foreach (var revision in candidates.Take(12))
        {
            var remaining = TimeSpan.FromSeconds(30) - watch.Elapsed; if (remaining <= TimeSpan.Zero) break;
            string text;
            try { text = StrictUtf8.GetString(await FetchAsync("https://raw.githubusercontent.com/" + plan.Path + "/" + revision + "/" + module + "/build.gradle.kts", 64 * 1024, remaining < TimeSpan.FromSeconds(6) ? remaining : TimeSpan.FromSeconds(6), token).ConfigureAwait(false)); }
            catch (PublicSourceFetchException error) when (error.StatusCode == 404) { continue; }
            fallback ??= revision;
            var codes = Regex.Matches(text, @"\b(?:extVersionCode|versionCode)\s*=\s*([0-9]+)\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var libraries = Regex.Matches(text, "\\blibVersion\\s*=\\s*[\"']([0-9.]+)[\"']", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (codes.Count != 0 && version.EndsWith("." + codes[0].Groups[1].Value, StringComparison.Ordinal) && (libraries.Count == 0 || version == libraries[0].Groups[1].Value + "." + codes[0].Groups[1].Value)) return revision;
        }
        return fallback ?? throw new InvalidDataException("No retained module was found in the bounded original repository history; provide its earlier commit/project or choose Unavailable");
    }
    private void Prune(string root, string keep)
    {
        var commits = Path.Combine(root, "commits"); PrivateDocumentStore.ValidateDirectory(commits);
        var references = Directory.EnumerateFiles(commits, "*.json").Where(path => Regex.IsMatch(Path.GetFileNameWithoutExtension(path), "\\A[0-9a-f]{40}\\z", RegexOptions.CultureInvariant)).Take(4097).ToArray();
        if (references.Length > 4096) throw new InvalidDataException("Repository cache metadata exceeds its listing limit");
        var ordered = references.OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.Ordinal).ToArray();
        var retained = new HashSet<string>(StringComparer.Ordinal) { keep }; foreach (var path in ordered) { if (retained.Count >= 3) break; retained.Add(Path.GetFileNameWithoutExtension(path)); }
        var protectedHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in ordered)
        { var value = ReadJson(path); if (retained.Contains(Path.GetFileNameWithoutExtension(path))) { if (value is not null) protectedHashes.Add(RawCodeRules.Hash(SourceOrigin.Optional(value, "archive_sha256"))); } else Remove(path, commits); }
        var archives = Path.Combine(root, "archives"); PrivateDocumentStore.ValidateDirectory(archives);
        foreach (var path in Directory.EnumerateFiles(archives, "*.zip"))
            if (Regex.IsMatch(Path.GetFileNameWithoutExtension(path), "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant) && !protectedHashes.Contains(Path.GetFileNameWithoutExtension(path))) Remove(path, archives);
    }
    private static void Remove(string path, string parent)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(parent)) throw new InvalidDataException("Unsafe repository cache cleanup path");
        using (PrivateDocumentStore.OpenRegular(path, writable: false, create: false, maximum: RawCodeRules.MaximumRepositoryZip)) { }
        File.Delete(path);
    }
    private async Task<byte[]> FetchAsync(string url, int maximum, TimeSpan timeout, CancellationToken token, string? accept = null)
    {
        var response = await _fetcher.FetchAsync(new(new Uri(url), maximum, timeout, accept), token).ConfigureAwait(false);
        if (response.Body.Length == 0 || response.Body.Length > maximum) throw new InvalidDataException("Original repository returned an invalid or oversized response"); return response.Body;
    }
    private static JsonObject Parse(byte[] bytes)
    {
        try { return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject ?? throw new InvalidDataException("Repository metadata is invalid"); }
        catch (JsonException error) { throw new InvalidDataException("Repository metadata is damaged", error); }
    }
    private static JsonObject? ReadJson(string path) { var bytes = AtomicSourceFiles.Read(path, 65536); return bytes is null ? null : Parse(bytes); }
    private static void WriteJson(string path, JsonObject value) => AtomicSourceFiles.Write(path, CanonicalJson.Bytes(value), 65536);
    private static bool Number(JsonNode? node, out double value) { value = 0; return node is JsonValue number && number.GetValueKind() == JsonValueKind.Number && double.TryParse(number.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value); }
    private static bool Integer(JsonNode? node, out long value) { value = 0; return node is JsonValue number && number.GetValueKind() == JsonValueKind.Number && long.TryParse(number.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value); }
    public Task StopAsync() => _owner.StopAsync();
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); await _owner.DisposeAsync().ConfigureAwait(false); }
}
