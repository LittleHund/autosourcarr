using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Core.Sources.Inventory;

/// <summary>
/// Installed-source inventory and exact installed-JAR observations read from the AutoSourcarr source host
/// (GET /v1/extensions and GET /v1/extensions/{pkg}/jar). The host replaces the native engine whose files
/// <see cref="EngineInstalledSources"/> reads: it names jars "&lt;pkg&gt;-v&lt;version&gt;.jar" (and
/// "&lt;pkg&gt;-v&lt;version&gt;-&lt;sha12&gt;.jar" after a repair) and does not expose that name, so the bytes are read
/// by package over the host API instead of from disk. Never installs, reloads or otherwise mutates the host.
/// <para>Guarantees match <see cref="EngineInstalledSources"/>: one transport session per observation; inventory, JAR,
/// inventory, JAR; both inventory proofs and both JAR reads must be equal, and the host generation (restart count)
/// must not change, otherwise <see cref="SourceRawConflictException"/>. A host that is not ready throws
/// <see cref="SourceEngineUnavailableException"/> (503), as does a host that does not answer.</para>
/// <para>Limits: the host jar route has no version parameter, so the version fence is the surrounding inventory
/// reads; <see cref="RequireCurrent"/> checks the transport epoch and host generation only and cannot see a jar
/// replaced inside the same host generation, so callers that act on a capture hold sharedSourceAdmin (as
/// <see cref="SourceBaselineStore"/> documents) to exclude installs and repairs. <see cref="InstalledSourceCapture.Filename"/>
/// is the logical "&lt;pkg&gt;-v&lt;version&gt;.jar" name, not necessarily the host's file name.</para>
/// </summary>
public sealed class HostRawInstalledSources : IInstalledSourceCaptures, IAsyncDisposable
{
    private static readonly string[] ExtensionFields = ["pkgName", "name", "lang", "apkName", "apkUrl", "isInstalled", "isObsolete", "hasUpdate", "versionName", "storeIndexUrl"];
    private readonly SuwayomiClient _client;
    private readonly Func<(bool Ready, long Generation)> _state;
    private readonly BoundedWorkScheduler _io;
    private readonly IInstalledSourceAnnotations _annotations;
    private readonly OperationOwner _owner = new();

    /// <param name="client">A source-host client (<see cref="SuwayomiClient.ForSourceHost"/>).</param>
    /// <param name="maintenanceRoot">&lt;data&gt;/source-maintenance, for the local orphan/manual-upload labels.</param>
    /// <param name="currentState">Host readiness and a generation that changes whenever the host restarts.</param>
    public HostRawInstalledSources(SuwayomiClient client, string maintenanceRoot, Func<(bool Ready, long Generation)> currentState,
        BoundedWorkScheduler io, IInstalledSourceAnnotations? annotations = null)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(currentState); ArgumentNullException.ThrowIfNull(io);
        if (!client.SourceHost) throw new ArgumentException("Host installed-source observations need the AutoSourcarr source host client.", nameof(client));
        _client = client; _state = currentState; _io = io;
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
        var before = Select(await ReadInventoryAsync(session, token).ConfigureAwait(false), package, version); session.ThrowIfRetired(); CheckGeneration(generation);
        if (before is null) return null;
        var first = await ReadJarAsync(session, package, token).ConfigureAwait(false); session.ThrowIfRetired(); CheckGeneration(generation);
        var after = Select(await ReadInventoryAsync(session, token).ConfigureAwait(false), package, version); session.ThrowIfRetired(); CheckGeneration(generation);
        if (after is null || Proof(before) != Proof(after)) throw Conflict("Installed source identity changed during observation; refresh before acting");
        var second = await ReadJarAsync(session, package, token).ConfigureAwait(false); session.ThrowIfRetired(); CheckGeneration(generation);
        if (!first.AsSpan().SequenceEqual(second)) throw Conflict("Installed source binary changed during observation; refresh before acting");
        var sources = after["source"]!["nodes"]!.AsArray().OfType<JsonObject>().Select(row => row["id"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
        var observed = new RawInstalledSource((JsonObject)after.DeepClone(), SourceInventoryFiles.Digest(first), InstalledSourceIdentity.Hash(after), sources);
        var epoch = session.Engine ?? throw Conflict("Source host transport was not bound during observation; refresh before acting");
        session.ThrowIfRetired(); token.ThrowIfCancellationRequested(); CheckGeneration(generation);
        return new(observed, first, InstalledFilename(package, version), generation, epoch);
    }

    /// <summary>Throws when the capture's host transport retired or the host restarted since the capture.</summary>
    public void RequireCurrent(InstalledSourceCapture capture)
    { ArgumentNullException.ThrowIfNull(capture); capture.RequireTransportCurrent(); CheckGeneration(capture.Generation); }

    /// <summary>The logical name of an installed jar ("&lt;pkg&gt;-v&lt;version&gt;.jar", the host's unrepaired name).</summary>
    public static string InstalledFilename(string package, string version)
    {
        var filename = RawCodeRules.Package(package) + "-v" + RawCodeRules.Version(version) + ".jar";
        if (!SourceInventoryFiles.IsInstalledFilename(filename)) throw Conflict("Installed extension filename is unavailable or invalid; no files were changed");
        return filename;
    }

    private async Task<List<JsonObject>> ReadInventoryAsync(SourceTransportSession session, CancellationToken token)
    {
        JsonObject data;
        try { data = await session.GraphQlAsync(EngineInstalledSources.InventoryQuery, cancellationToken: token).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or TimeoutException) { throw Unreachable(session, token); }
        var all = SourceResponseContract.ConnectionNodes(data["extensions"]); if (all.Count > 50000) throw Conflict("Installed inventory exceeds its bounded catalog limit");
        var result = new List<JsonObject>(); var packages = new HashSet<string>(StringComparer.Ordinal); var sourceOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var host in all)
        {
            if (!SourceResponseContract.Boolean(host["isInstalled"], out var installed)) throw Conflict("Installed source flags are incomplete");
            if (!installed) continue;
            var package = RawCodeRules.Package(SourceResponseContract.Text(host["pkgName"])); RawCodeRules.Version(SourceResponseContract.Text(host["versionName"]));
            if (!packages.Add(package) || result.Count >= 4096) throw Conflict("Installed package ownership is ambiguous or exceeds its bound");
            // The fields of the engine inventory query only: the host row also carries load errors and preference flags.
            var row = new JsonObject(); foreach (var key in ExtensionFields) if (host.ContainsKey(key)) row[key] = host[key]?.DeepClone();
            var sources = SourceResponseContract.ConnectionNodes(host["source"]); if (sources.Count > 4096) throw Conflict("Installed source identity exceeds its bound");
            var nodes = new JsonArray();
            foreach (var source in sources)
            {
                var raw = SourceResponseContract.Identity(source["id"]);
                if (raw is null || !long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) || id.ToString(CultureInfo.InvariantCulture) != raw) throw Conflict("Installed source ID is not an exact signed identity");
                if (!sourceOwners.TryAdd(raw, package)) throw Conflict("Installed source ownership is ambiguous");
                var node = new JsonObject { ["id"] = raw };
                foreach (var name in new[] { "name", "lang", "baseUrl" })
                {
                    if (!source.ContainsKey(name) || source[name] is not null && source[name]!.GetValueKind() != JsonValueKind.String) throw Conflict("Installed source metadata is malformed or incomplete");
                    node[name] = source[name]?.DeepClone();
                }
                nodes.Add(node);
            }
            row["source"] = new JsonObject { ["nodes"] = nodes }; result.Add(row);
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

    private async Task<byte[]> ReadJarAsync(SourceTransportSession session, string package, CancellationToken token)
    {
        byte[] bytes;
        try { bytes = await _client.ReadExtensionJarCoreAsync(session, package, token).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or TimeoutException) { throw Unreachable(session, token); }
        catch (Exception error) when (error is SuwayomiException or IOException or InvalidDataException)
        {
            session.ThrowIfRetired(); token.ThrowIfCancellationRequested();
            throw Conflict("Installed JAR evidence is unavailable or changed; refresh before acting");
        }
        if (bytes.Length is 0 or > SourcePackage.MaximumBytes) throw Conflict("Installed JAR evidence is unavailable or changed; refresh before acting");
        // Keep each observation independent so a reused buffer cannot make two reads compare equal.
        return (byte[])bytes.Clone();
    }

    private static SourceEngineUnavailableException Unreachable(SourceTransportSession session, CancellationToken token)
    {
        session.ThrowIfRetired(); token.ThrowIfCancellationRequested();
        return new SourceEngineUnavailableException("The source host did not answer; installed source evidence remains unknown");
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
    { var state = _state(); if (!state.Ready || _owner.IsStopping) throw new SourceEngineUnavailableException("The source host is unavailable; installed source evidence remains unknown"); return state.Generation; }
    private void CheckGeneration(long generation) { if (Generation() != generation) throw Conflict("The source host restarted during observation; refresh before acting"); }
    private CancellationTokenSource Lifetime(CancellationToken token) => CancellationTokenSource.CreateLinkedTokenSource(token, _owner.CancellationToken);
    private static SourceRawConflictException Conflict(string message) => new(message);
    public Task StopAsync() => _owner.StopAsync();
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); await _owner.DisposeAsync().ConfigureAwait(false); }
}
