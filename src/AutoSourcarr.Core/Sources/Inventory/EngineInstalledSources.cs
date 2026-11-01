using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Core.Sources.Inventory;

/// <summary>Immutable observed bytes, not proof of manifest identity, APK conversion or cold import.</summary>
public sealed class InstalledSourceCapture
{
    private readonly byte[] _bytes;
    private readonly RawInstalledSource _source;
    private readonly SourceTransportEpoch _transport;
    internal InstalledSourceCapture(RawInstalledSource source, byte[] bytes, string filename, long generation, SourceTransportEpoch transport)
    { _source = source; _bytes = (byte[])bytes.Clone(); Filename = filename; Generation = generation; _transport = transport; }
    public string Filename { get; }
    public long Generation { get; }
    public RawInstalledSource Source => new((JsonObject)_source.Extension.DeepClone(), _source.BinarySha256, _source.IdentitySha256, _source.SourceIds.ToArray());
    public byte[] Contents() => (byte[])_bytes.Clone();
    internal void RequireTransportCurrent() => _transport.ThrowIfRetired();
}

/// <summary>
/// Installed-source observations that also hand out the exact captured JAR bytes (original-APK evidence and repair
/// input). <see cref="CaptureAsync"/> answers null when the package/version is not installed; a capture is only valid
/// while <see cref="RequireCurrent"/> does not throw (same engine incarnation and generation). Neither member mutates
/// the engine; callers that need mutual exclusion with installs hold sharedSourceAdmin.
/// </summary>
public interface IInstalledSourceCaptures : IRawInstalledSources
{
    Task<InstalledSourceCapture?> CaptureAsync(string package, string version, CancellationToken cancellationToken = default);
    void RequireCurrent(InstalledSourceCapture capture);
}

/// <summary>Per-instance native inventory and exact installed JAR observations. Never starts or mutates an engine.</summary>
public sealed class EngineInstalledSources : IInstalledSourceCaptures, IAsyncDisposable
{
    public const string InventoryQuery = "query { extensions { nodes { pkgName name lang apkName apkUrl isInstalled isObsolete hasUpdate versionName storeIndexUrl source { nodes { id name lang baseUrl } } } } }";
    private readonly SuwayomiClient _client;
    private readonly IInstalledSourceBinaryReader _binaryReader;
    private readonly Func<(bool Ready, long Generation)> _state;
    private readonly BoundedWorkScheduler _io;
    private readonly IInstalledSourceAnnotations _annotations;
    private readonly OperationOwner _owner = new();
    public EngineInstalledSources(SuwayomiClient client, string engineDataRoot, string maintenanceRoot, Func<(bool Ready, long Generation)> currentState,
        BoundedWorkScheduler io, IInstalledSourceAnnotations? annotations = null)
        : this(client, new DiskInstalledSourceBinaryReader(engineDataRoot, io), maintenanceRoot, currentState, io, annotations) { }

    /// <summary>
    /// The reader owns its physical byte capture and cleanup; this owner keeps
    /// the GraphQL epoch, generation, identity and two-independent-read fences.
    /// The interface itself provides no native credentials or process proof.
    /// </summary>
    public EngineInstalledSources(SuwayomiClient client, IInstalledSourceBinaryReader binaryReader, string maintenanceRoot,
        Func<(bool Ready, long Generation)> currentState, BoundedWorkScheduler io, IInstalledSourceAnnotations? annotations = null)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(binaryReader);
        ArgumentNullException.ThrowIfNull(currentState); ArgumentNullException.ThrowIfNull(io);
        _client = client; _binaryReader = binaryReader; _state = currentState; _io = io;
        _annotations = annotations ?? new InstalledSourceAnnotations(maintenanceRoot);
    }
    public bool Ready => !_owner.IsStopping && _state().Ready;
    public async Task<IReadOnlyList<JsonObject>> InstalledAsync(CancellationToken cancellationToken)
    {
        using var lease = _owner.Enter(); using var linked = Lifetime(cancellationToken); var generation = Generation();
        await using var session = _client.OpenSession(linked.Token);
        var rows = await ReadInventoryAsync(session, session.CancellationToken).ConfigureAwait(false); session.ThrowIfRetired(); CheckGeneration(generation); return rows;
    }
    /// <summary>Existing callers hold sharedSourceAdmin. This read-only adapter never reacquires it.</summary>
    public async Task<RawInstalledSource?> ObserveAsync(string package, string version, CancellationToken cancellationToken)
        => (await CaptureAsync(package, version, cancellationToken).ConfigureAwait(false))?.Source;
    public async Task<InstalledSourceCapture?> CaptureAsync(string package, string version, CancellationToken cancellationToken = default)
    {
        RawCodeRules.Package(package); RawCodeRules.Version(version);
        using var lease = _owner.Enter(); using var linked = Lifetime(cancellationToken); var generation = Generation();
        await using var session = _client.OpenSession(linked.Token); var token = session.CancellationToken;
        var firstRows = await ReadInventoryAsync(session, token).ConfigureAwait(false); session.ThrowIfRetired(); CheckGeneration(generation);
        var before = Select(firstRows, package, version); if (before is null) return null;
        var first = await ReadInstalledAsync(session, before, generation, token).ConfigureAwait(false); session.ThrowIfRetired(); CheckGeneration(generation);
        var after = Select(await ReadInventoryAsync(session, token).ConfigureAwait(false), package, version); session.ThrowIfRetired(); CheckGeneration(generation);
        if (after is null || Proof(before) != Proof(after)) throw Conflict("Installed source identity changed during observation; refresh before acting");
        var second = await ReadInstalledAsync(session, after, generation, token).ConfigureAwait(false); session.ThrowIfRetired(); CheckGeneration(generation);
        if (!first.AsSpan().SequenceEqual(second)) throw Conflict("Installed source binary changed during observation; refresh before acting");
        var sources = after["source"]!["nodes"]!.AsArray().OfType<JsonObject>().Select(row => row["id"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
        var observed = new RawInstalledSource((JsonObject)after.DeepClone(), SourceInventoryFiles.Digest(first), InstalledSourceIdentity.Hash(after), sources);
        session.ThrowIfRetired(); token.ThrowIfCancellationRequested(); CheckGeneration(generation); return new(observed, first, InstalledFilename(after), generation, session.Engine!);
    }
    public void RequireCurrent(InstalledSourceCapture capture)
    { capture.RequireTransportCurrent(); CheckGeneration(capture.Generation); }
    private async Task<List<JsonObject>> ReadInventoryAsync(SourceTransportSession session, CancellationToken token)
    {
        var data = await session.GraphQlAsync(InventoryQuery, cancellationToken: token).ConfigureAwait(false);
        var all = SourceResponseContract.ConnectionNodes(data["extensions"]); if (all.Count > 50000) throw Conflict("Installed inventory exceeds its bounded catalog limit");
        var result = new List<JsonObject>(); var packages = new HashSet<string>(StringComparer.Ordinal); var sourceOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in all)
        {
            if (!SourceResponseContract.Boolean(row["isInstalled"], out var installed)) throw Conflict("Installed source flags are incomplete");
            if (!installed) continue;
            var package = RawCodeRules.Package(SourceResponseContract.Text(row["pkgName"])); RawCodeRules.Version(SourceResponseContract.Text(row["versionName"]));
            if (!packages.Add(package) || result.Count >= 4096) throw Conflict("Installed package ownership is ambiguous or exceeds its bound");
            InstalledFilename(row); var sources = SourceResponseContract.ConnectionNodes(row["source"]); if (sources.Count > 4096) throw Conflict("Installed source identity exceeds its bound");
            foreach (var source in sources)
            {
                var raw = SourceResponseContract.Identity(source["id"]);
                if (raw is null || !long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) || id.ToString(CultureInfo.InvariantCulture) != raw) throw Conflict("Installed source ID is not an exact signed identity");
                if (!sourceOwners.TryAdd(raw, package)) throw Conflict("Installed source ownership is ambiguous"); source["id"] = raw;
                foreach (var name in new[] { "lang", "name", "baseUrl" }) if (!source.ContainsKey(name) || source[name] is not null && source[name]!.GetValueKind() != JsonValueKind.String) throw Conflict("Installed source metadata is malformed or incomplete");
            }
            row["source"] = new JsonObject { ["nodes"] = new JsonArray(sources.Select(source => (JsonNode?)source).ToArray()) }; result.Add(row);
        }
        IReadOnlyList<JsonObject> annotated;
        try { annotated = await _io.RunAsync(() => _annotations.Annotate(result), token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidDataException) { session.ThrowIfRetired(); token.ThrowIfCancellationRequested(); throw Conflict("Local source provenance is unavailable; no official or manual origin was inferred"); }
        session.ThrowIfRetired();
        if (annotated.Count != result.Count) throw Conflict("Local source annotation changed inventory ownership");
        for (var i = 0; i < result.Count; i++)
            if (NativeProof(result[i]) != NativeProof(annotated[i])) throw Conflict("Local source annotation changed native inventory evidence");
        return annotated.Select(row => (JsonObject)row.DeepClone()).ToList();
    }
    private async Task<byte[]> ReadInstalledAsync(SourceTransportSession session, JsonObject extension, long generation, CancellationToken token)
    {
        try
        {
            var bytes = await _binaryReader.ReadInstalledJarAsync(InstalledFilename(extension), generation, token).ConfigureAwait(false);
            if (bytes is null || bytes.Length is 0 or > SourcePackage.MaximumBytes)
                throw new InvalidDataException("Installed JAR is empty or oversized.");
            // A backend may reuse its buffer. Keep each observation independent
            // so a later mutation cannot make two different reads compare equal.
            return (byte[])bytes.Clone();
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            session.ThrowIfRetired(); token.ThrowIfCancellationRequested();
            throw Conflict("Installed JAR evidence is unavailable or changed; refresh before acting");
        }
        catch { session.ThrowIfRetired(); token.ThrowIfCancellationRequested(); throw; }
    }
    public static string InstalledFilename(JsonObject extension)
    {
        var filename = SourceResponseContract.Text(extension["apkName"]);
        if (!SourceInventoryFiles.IsInstalledFilename(filename)) throw Conflict("Installed extension filename is unavailable or invalid; no files were changed");
        return filename[..^4] + ".jar";
    }
    private static JsonObject? Select(IReadOnlyList<JsonObject> rows, string package, string version) => rows.SingleOrDefault(row => SourceInventoryFiles.Text(row, "pkgName") == package && SourceInventoryFiles.Text(row, "versionName") == version);
    private static string NativeProof(JsonObject row)
    {
        var proof = new JsonObject(); foreach (var key in new[] { "pkgName", "versionName", "apkName", "isInstalled", "storeIndexUrl", "apkUrl" }) proof[key] = row[key]?.DeepClone();
        var sources = row["source"]!["nodes"]!.AsArray().OfType<JsonObject>().OrderBy(source => source["id"]!.GetValue<string>(), StringComparer.Ordinal);
        proof["source"] = new JsonArray(sources.Select(source => (JsonNode?)new JsonObject { ["id"] = source["id"]!.DeepClone(), ["name"] = source["name"]?.DeepClone(), ["lang"] = source["lang"]?.DeepClone(), ["baseUrl"] = source["baseUrl"]?.DeepClone() }).ToArray());
        return RawCodeRules.Digest(CanonicalJson.Bytes(proof));
    }
    private static string Proof(JsonObject row) => NativeProof(row) + ":" + SourceInventoryFiles.Text(row, "orphan_reason") + ":" + row["is_orphaned"]?.ToJsonString();
    private long Generation()
    { var state = _state(); if (!state.Ready || _owner.IsStopping) throw new SourceEngineUnavailableException("Integrated source engine is unavailable; installed source evidence remains unknown"); return state.Generation; }
    private void CheckGeneration(long generation) { if (Generation() != generation) throw Conflict("Source engine changed during observation; refresh before acting"); }
    private CancellationTokenSource Lifetime(CancellationToken token) => CancellationTokenSource.CreateLinkedTokenSource(token, _owner.CancellationToken);
    private static SourceRawConflictException Conflict(string message) => new(message);
    public Task StopAsync() => _owner.StopAsync();
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); await _owner.DisposeAsync().ConfigureAwait(false); }
}

/// <summary>
/// Returns exact bounded installed-JAR bytes. Implementations must join admitted
/// physical work and cleanup before completing, including cancellation. A native
/// implementation must also bind the requested generation to its actual owner.
/// This seam provides no readiness, manifest, APK equality or health evidence.
/// </summary>
public interface IInstalledSourceBinaryReader
{
    Task<byte[]> ReadInstalledJarAsync(string leaf, long expectedGeneration, CancellationToken cancellationToken);
}

// Retains existing disk compatibility for controlled component consumers. The
// final integrated main must select the actual source-account observation reader.
internal sealed class DiskInstalledSourceBinaryReader(string engineDataRoot, BoundedWorkScheduler io) : IInstalledSourceBinaryReader
{
    private readonly string _extensions = Path.Combine(Path.GetFullPath(engineDataRoot), "extensions");

    public Task<byte[]> ReadInstalledJarAsync(string leaf, long expectedGeneration, CancellationToken cancellationToken)
    {
        if (!leaf.EndsWith(".jar", StringComparison.Ordinal) || !SourceInventoryFiles.IsInstalledFilename(leaf))
            throw new InvalidDataException("Installed JAR filename is unavailable or invalid.");
        return io.RunAsync(() => SourceInventoryFiles.ReadRequired(Path.Combine(_extensions, leaf), SourceInventoryFiles.PackageLimit), cancellationToken);
    }
}
