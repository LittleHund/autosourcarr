using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Core.Repair;

public sealed class SourceRawConflictException(string message) : InvalidOperationException(message);

/// <summary>Instance-private revision-fenced raw requests. Cached views never perform I/O or start tools.</summary>
public sealed class SourceRawCatalog
{
    private readonly object _gate = new();
    private readonly IPrivateDocumentStore _requests;
    private readonly IRawRepositorySource _repositories;
    private readonly SourceDiscovery _discovery;
    private readonly Func<double> _clock;
    private readonly Dictionary<string, JsonObject> _cache = new(StringComparer.Ordinal);
    public RawStore Shared { get; }
    public RawStore Private { get; }
    public SourceRawCatalog(RawStore shared, RawStore privateStore, IPrivateDocumentStore requests, IRawRepositorySource repositories, IPublicSourceFetcher fetcher, Func<double>? clock = null)
    {
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!shared.Public || privateStore.Public || shared.Root.Equals(privateStore.Root, pathComparison) || privateStore.Root.StartsWith(shared.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, pathComparison) ||
            shared.Root.StartsWith(privateStore.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, pathComparison)) throw new ArgumentException("Public and private raw-code stores must remain separate");
        Shared = shared; Private = privateStore; _requests = requests; _repositories = repositories; _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        _discovery = new(new DiscoveryCache(fetcher, _clock));
        foreach (var name in requests.JsonDocuments(4096))
        {
            if (name.Length != 69 || !name.EndsWith(".json", StringComparison.Ordinal)) continue;
            try { RawCodeRules.Hash(name[..64]); } catch (InvalidDataException) { continue; }
            requests.Locked(name, document => { var value = Read(document, name); if (value is not null) _cache[RawCodeRules.Text(value, "package")] = value; return 0; });
        }
    }
    public JsonObject? Status(string package) { RawCodeRules.Package(package); lock (_gate) return _cache.TryGetValue(package, out var value) ? Clone(value) : null; }
    public JsonObject Cached() { lock (_gate) return new(_cache.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value.DeepClone()))); }
    public JsonObject Begin(JsonObject extension, bool? manual = null, string? binarySha256 = null, JsonObject? supplied = null, bool force = false)
    {
        var origin = SourceOrigin.Resolve(extension, manual, supplied); var package = RawCodeRules.Text(origin, "package"); var version = RawCodeRules.Text(origin, "version"); var binary = Binary(binarySha256);
        return Mutate(package, document =>
        {
            var old = Read(document, Name(package));
            if (old is not null && !force && supplied is null && (State(old) == "available" || State(old) == "needs-input" && RawCodeRules.Present(old["discovery_version"])) && binary.Length != 0 && RawCodeRules.Text(old, "version") == version && SourceOrigin.Optional(old, "binary_sha256") == binary)
            { Cache(old); return Clone(old); }
            var same = old is not null && State(old) != "removed" && RawCodeRules.Text(old, "version") == version && JsonNode.DeepEquals(old["origin"], origin) &&
                (binary.Length == 0 || SourceOrigin.Optional(old, "binary_sha256").Length == 0 || SourceOrigin.Optional(old, "binary_sha256") == binary);
            if (same && !force)
            {
                if (binary.Length != 0 && SourceOrigin.Optional(old!, "binary_sha256").Length == 0) { old!["binary_sha256"] = binary; Advance(old); return Save(document, old); }
                Cache(old!); return Clone(old!);
            }
            var value = new JsonObject { ["schema"] = 1, ["package"] = package, ["version"] = version, ["revision"] = old is null ? 1 : Revision(old) + 1,
                ["binary_sha256"] = binary, ["origin"] = origin, ["updated_at"] = _clock(), ["state"] = State(origin) == "known" ? "acquiring" : "needs-input",
                ["message"] = State(origin) == "known" ? "Archiving the original source project." : RawCodeRules.Text(origin, "reason") };
            return Save(document, value);
        });
    }
    public async Task<JsonObject?> AcquireAsync(JsonObject intent, CancellationToken cancellationToken = default)
    {
        var package = RawCodeRules.Package(RawCodeRules.Text(intent, "package")); var expected = Revision(intent);
        var version = RawCodeRules.Version(RawCodeRules.Text(intent, "version")); var binary = Binary(SourceOrigin.Optional(intent, "binary_sha256"));
        var source = (JsonObject)intent["origin"]!.DeepClone(); JsonObject changes;
        try
        {
            var snapshot = await _repositories.ObtainAsync(source, cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
            if (RawCodeRules.Text(snapshot, "package") != package || RawCodeRules.Text(snapshot, "version") != version) throw new InvalidDataException("Acquired source code belongs to another installed package/version");
            var id = RawCodeRules.Hash(RawCodeRules.Text(snapshot, "id"));
            changes = new() { ["state"] = "available", ["raw_id"] = id, ["visibility"] = "shared", ["message"] = SourceOrigin.Optional(snapshot["origin"]!.AsObject(), "basis") == "repository-candidate" ?
                "Upstream candidate code archived. Its revision is not proven to reproduce this installation; compatible build and installed-source identity must pass before repair." :
                "Original repository code archived; matching build and source identity are checked during repair." };
        }
        catch (Exception error) when (error is InvalidDataException or IOException or JsonException or TimeoutException)
        { changes = new() { ["state"] = "needs-input", ["message"] = "Original raw code could not be archived: " + Clip(error.Message, 1600) + ". Provide the project or choose Unavailable." }; }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Mutate(package, document =>
            {
                var value = Current(document, package, expected, version: version);
                if (SourceOrigin.Optional(value, "binary_sha256") != binary) throw Conflict("Installed binary changed during source acquisition");
                foreach (var (key, node) in changes) value[key] = node?.DeepClone(); Advance(value); return Save(document, value);
            });
        }
        catch (SourceRawConflictException) { return Status(package); }
    }
    public async Task<JsonObject> DiscoverAsync(JsonObject intent, JsonObject extension, IEnumerable<string> indexes, CancellationToken cancellationToken = default)
    {
        var package = RawCodeRules.Package(RawCodeRules.Text(intent, "package")); var expected = Revision(intent);
        var version = RawCodeRules.Version(RawCodeRules.Text(intent, "version")); var binary = Binary(SourceOrigin.Optional(intent, "binary_sha256"));
        var installed = (JsonObject)extension.DeepClone();
        if (RawCodeRules.Package(RawCodeRules.Text(installed, "pkgName")) != package || RawCodeRules.Version(RawCodeRules.Text(installed, "versionName")) != version)
            throw new InvalidDataException("Discovery belongs to another installed package/version");
        var origin = await _discovery.DiscoverAsync(installed, indexes, cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
        return Mutate(package, document =>
        {
            var value = Current(document, package, expected, version: version);
            if (SourceOrigin.Optional(value, "binary_sha256") != binary) throw Conflict("Installed binary changed during source discovery");
            if (State(value) != "needs-input") return value;
            value["discovery_version"] = SourceDiscovery.CapabilityVersion; Advance(value);
            if (State(origin) == "known") { value["origin"] = origin; value["state"] = "acquiring"; value["message"] = "Declared upstream repository found. Archiving candidate code; original installed bytes remain unchanged."; }
            else value["message"] = origin["reason"]!.DeepClone();
            return Save(document, value);
        });
    }
    internal static bool NeedsDiscovery(JsonObject intent, JsonObject extension, bool? manual = null)
    {
        if (State(intent) != "needs-input" || manual == true || SourceOrigin.Optional(extension, "orphan_reason") == "manual-upload"
            || RawCodeRules.Text(intent, "package") != RawCodeRules.Text(extension, "pkgName") || RawCodeRules.Text(intent, "version") != RawCodeRules.Text(extension, "versionName")) return false;
        // Only an absent marker or a known older capability is automatically due.
        // Unknown/future markers, manual choices and other origins stay authoritative.
        var marker = intent["discovery_version"];
        if (marker is not null && (marker is not JsonValue scalar || !scalar.TryGetValue<int>(out var capability)
            || capability < 1 || capability >= SourceDiscovery.CapabilityVersion)) return false;
        var automatic = SourceOrigin.Resolve(extension, manual: false);
        return (marker is null || State(automatic) == "needs-input") && JsonNode.DeepEquals(intent["origin"], automatic);
    }
    public JsonObject Provide(JsonObject extension, byte[] payload, long expectedRevision, string? binarySha256 = null, string? module = null, bool share = false)
    {
        var origin = SourceOrigin.Resolve(extension, manual: true); var package = RawCodeRules.Text(origin, "package"); var binary = Binary(binarySha256);
        return Mutate(package, document =>
        {
            var value = Current(document, package, expectedRevision, version: RawCodeRules.Text(origin, "version"));
            if (SourceOrigin.Optional(value, "binary_sha256").Length != 0 && SourceOrigin.Optional(value, "binary_sha256") != binary) throw Conflict("Installed binary changed after this raw-code request");
            if (State(value) is not ("needs-input" or "acquiring")) throw Conflict("This source already has raw code; start a fresh input request first");
            var previous = value["origin"]!.AsObject(); var suppliedOrigin = new JsonObject { ["basis"] = "user-provided" };
            foreach (var key in new[] { "repository", "index_url" }) if (RawCodeRules.Present(previous[key])) suppliedOrigin[key] = previous[key]!.DeepClone();
            var info = new JsonObject { ["package"] = package, ["version"] = origin["version"]!.DeepClone(), ["module"] = RawCodeRules.Module(module ?? RawCodeRules.Text(origin, "module")),
                ["ecosystem"] = previous["ecosystem"]?.DeepClone() ?? JsonValue.Create("tachiyomi"), ["recipe"] = "gradle", ["kind"] = "provided", ["origin"] = suppliedOrigin };
            var exported = RawArchive.PortableMetadata(payload);
            if (exported is not null)
            {
                if (RawCodeRules.Text(exported, "package") != package) throw new InvalidDataException("Exported source code belongs to another installed package");
                foreach (var key in new[] { "module", "ecosystem", "recipe", "upstream_module", "overlay_sources" }) if (exported.ContainsKey(key)) info[key] = exported[key]?.DeepClone();
                if (RawCodeRules.Present(exported["origin"]?["repository"])) info["origin"]!["repository"] = exported["origin"]!["repository"]!.DeepClone();
            }
            var snapshot = (share ? Shared : Private).PutZip(payload, info);
            value["state"] = "available"; value["raw_id"] = snapshot["id"]!.DeepClone(); value["visibility"] = share ? "shared" : "private"; value["binary_sha256"] = binary;
            value["message"] = "Provided raw code retained. Build and identity still require validation."; Advance(value); return Save(document, value);
        });
    }
    public JsonObject Retain(string package, byte[] payload, JsonObject info, long? expectedRevision = null, bool share = false, bool activate = false, string? expectedBinary = null)
    {
        if (RawCodeRules.Text(info, "package") != RawCodeRules.Package(package)) throw new InvalidDataException("Retained code has a different package");
        return Mutate(package, document =>
        {
            var value = expectedRevision is null ? Read(document, Name(package)) : Current(document, package, expectedRevision.Value);
            var snapshot = (share ? Shared : Private).PutZip(payload, info);
            var matching = value is not null && RawCodeRules.Text(value, "version") == RawCodeRules.Text(info, "version") && (expectedBinary is null || SourceOrigin.Optional(value, "binary_sha256") == expectedBinary);
            if (activate && matching)
            {
                value!["state"] = "available"; value["raw_id"] = snapshot["id"]!.DeepClone(); value["visibility"] = share ? "shared" : "private";
                value["message"] = "Retained " + RawCodeRules.Text(info, "kind") + " code; working confirmation is separate."; Advance(value); Save(document, value);
            }
            return snapshot;
        });
    }
    public (RawStore Store, JsonObject Snapshot) Resolve(string package, string rawId)
    {
        RawCodeRules.Package(package);
        try { return (Private, Private.Get(package, rawId)); }
        catch (FileNotFoundException) { return (Shared, Shared.Get(package, rawId)); }
    }
    public JsonObject RequestInput(string package, long expectedRevision, string binarySha256) => Mutate(package, document =>
    {
        var value = Current(document, package, expectedRevision); value["state"] = "needs-input"; value["binary_sha256"] = Binary(binarySha256); value.Remove("raw_id"); value.Remove("visibility");
        value["message"] = "Provide matching original code, or choose Unavailable to reconstruct the installed source."; Advance(value); return Save(document, value);
    });
    public void Removed(string package) => Mutate(package, document =>
    {
        var value = Read(document, Name(package)); if (value is null) return 0;
        value["state"] = "removed"; value["message"] = "Source uninstalled; archived code versions retained."; Advance(value); Save(document, value); return 0;
    });
    public JsonObject Adopt(JsonObject extension, string binarySha256, string rawId)
    {
        var package = RawCodeRules.Package(RawCodeRules.Text(extension, "pkgName")); var resolved = Resolve(package, rawId);
        return Mutate(package, document =>
        {
            var old = Read(document, Name(package));
            return Save(document, new() { ["schema"] = 1, ["package"] = package, ["version"] = RawCodeRules.Version(RawCodeRules.Text(extension, "versionName")), ["revision"] = old is null ? 1 : Revision(old) + 1,
                ["state"] = "available", ["binary_sha256"] = Binary(binarySha256), ["raw_id"] = rawId, ["visibility"] = resolved.Store.Public ? "shared" : "private", ["updated_at"] = _clock(),
                ["origin"] = SourceOrigin.Resolve(extension), ["message"] = "Code reference matches this installed repair; Fixed confirmation remains separate." });
        });
    }
    public JsonArray PendingItems()
    {
        lock (_gate)
        {
            var rows = new JsonArray();
            foreach (var value in _cache.Values.Where(row => State(row) == "needs-input").Take(500))
            {
                var package = RawCodeRules.Text(value, "package");
                rows.Add(new JsonObject { ["id"] = "source-raw:" + RawCodeRules.Digest(Encoding.UTF8.GetBytes(package))[..24], ["kind"] = "source-raw-input", ["name"] = "Source code required", ["context"] = package,
                    ["state"] = "running", ["activity"] = "waiting", ["raw_package"] = package, ["raw_revision"] = value["revision"]!.DeepClone(), ["raw_module"] = value["origin"]!["module"]!.DeepClone(),
                    ["source_version"] = value["version"]!.DeepClone(), ["message"] = value["message"]!.DeepClone(), ["origin"] = "manual", ["current"] = 0, ["total"] = 0, ["cancellable"] = false, ["created_at"] = "", ["updated_at"] = "" });
            }
            return rows;
        }
    }
    private T Mutate<T>(string package, Func<IPrivateDocument, T> operation) => _requests.Locked(Name(package), operation);
    private JsonObject Current(IPrivateDocument document, string package, long expected, string? version = null)
    {
        var value = Read(document, Name(package));
        if (value is null || Revision(value) != expected || version is not null && RawCodeRules.Text(value, "version") != version) throw Conflict("The installed source or raw-code request changed. Refresh before supplying code."); return value;
    }
    private JsonObject? Read(IPrivateDocument document, string name)
    {
        var bytes = document.Read(32768); if (bytes is null) return null;
        try
        {
            var value = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject ?? throw new InvalidDataException("Invalid raw-code request");
            if (value["schema"]?.ToJsonString() != "1" || Revision(value) < 1 || State(value) is not ("acquiring" or "available" or "needs-input" or "reconstructing" or "removed") || Name(RawCodeRules.Text(value, "package")) != name)
                throw new InvalidDataException("Raw-code request journal is damaged; it was not reset");
            RawCodeRules.Version(RawCodeRules.Text(value, "version")); Binary(SourceOrigin.Optional(value, "binary_sha256"));
            if (value["origin"] is not JsonObject origin || RawCodeRules.Text(origin, "package") != RawCodeRules.Text(value, "package") || RawCodeRules.Text(origin, "version") != RawCodeRules.Text(value, "version")) throw new InvalidDataException("Raw-code request origin identity changed");
            RawCodeRules.Module(RawCodeRules.Text(origin, "module")); _ = RawCodeRules.Text(value, "message"); return value;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { throw new InvalidDataException("Raw-code request journal is damaged; it was not reset", error); }
    }
    private JsonObject Save(IPrivateDocument document, JsonObject value)
    {
        var bytes = CanonicalJson.Bytes(value); if (bytes.Length > 32768) throw new InvalidDataException("Raw-code request metadata exceeds its byte limit");
        document.Write(bytes); Cache(value); return Clone(value);
    }
    private void Cache(JsonObject value) { lock (_gate) _cache[RawCodeRules.Text(value, "package")] = Clone(value); }
    private void Advance(JsonObject value) { value["revision"] = Revision(value) + 1; value["updated_at"] = _clock(); }
    private static long Revision(JsonObject value) => value["revision"] is JsonValue scalar && long.TryParse(scalar.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and < 9007199254740991 ? number : throw new InvalidDataException("Invalid raw-code request revision");
    private static string State(JsonObject value) => RawCodeRules.Text(value, "state");
    private static string Binary(string? value) => string.IsNullOrEmpty(value) ? "" : RawCodeRules.Hash(value);
    private static string Name(string package) => RawCodeRules.Digest(Encoding.UTF8.GetBytes(RawCodeRules.Package(package))) + ".json";
    private static JsonObject Clone(JsonObject value) => (JsonObject)value.DeepClone();
    private static string Clip(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    private static SourceRawConflictException Conflict(string message) => new(message);
    private sealed class DiscoveryCache(IPublicSourceFetcher fetcher, Func<double> clock) : IPublicSourceFetcher
    {
        private readonly object _gate = new();
        private readonly Dictionary<PublicSourceRequest, (double At, PublicSourceResponse Value)> _cache = [];
        public async Task<PublicSourceResponse> FetchAsync(PublicSourceRequest request, CancellationToken cancellationToken = default)
        {
            lock (_gate) if (_cache.TryGetValue(request, out var row) && clock() - row.At is >= 0 and < 300) return new((byte[])row.Value.Body.Clone(), row.Value.ResolvedUrl);
            var value = await fetcher.FetchAsync(request, cancellationToken).ConfigureAwait(false);
            if (value.Body.Length > request.MaximumBytes) throw new InvalidDataException("Source declaration exceeds its byte limit");
            lock (_gate) { if (_cache.Count >= 8) _cache.Remove(_cache.Keys.First()); _cache[request] = (clock(), new((byte[])value.Body.Clone(), value.ResolvedUrl)); }
            return value;
        }
    }
}
