using System.Globalization;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Sources;

/// <summary>Phase 6e source repair calls of the AutoSourcarr source host: the installed jar of a package (the last
/// working code) and installing a repaired or restored jar in its place. Source host only.</summary>
public sealed partial class SuwayomiClient
{
    /// <summary>Largest extension jar accepted in either direction (the host accepts uploads up to 64 MiB).</summary>
    public const int MaximumExtensionJar = 64 * 1024 * 1024;

    /// <summary>GET /v1/extensions/{pkg}/jar: the jar the host runs for an installed package.</summary>
    public async Task<byte[]> ReadExtensionJarAsync(string packageName, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await ReadExtensionJarCoreAsync(session, packageName, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<byte[]> ReadExtensionJarCoreAsync(SourceTransportSession session, string packageName, CancellationToken cancellationToken)
    {
        RequireRepairHost(packageName);
        session.ThrowIfRetired(); _ = session.EngineForRequest();
        await using var response = await SendAsync(session, HttpMethod.Get, new Uri(BaseUrl + "/v1/extensions/" + Uri.EscapeDataString(packageName) + "/jar"), null, null,
            new Dictionary<string, string> { ["Accept-Encoding"] = "identity" }, TimeSpan.FromSeconds(90), followRedirects: false, cancellationToken,
            requireRuntimeOrigin: true, allowErrorStatus: true).ConfigureAwait(false);
        if (response.StatusCode is < 200 or > 299)
        {
            var json = await ReadJsonAsync(response).ConfigureAwait(false);
            var detail = (json as JsonObject)?["detail"] is JsonValue value && value.TryGetValue<string>(out var text) ? text
                : "HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture);
            throw new SuwayomiException(Limit(_privacy.Text(detail), 1000));
        }
        var bytes = await ReadBoundedAsync(response, MaximumExtensionJar).ConfigureAwait(false);
        session.ThrowIfRetired();
        if (bytes.Length < 22) throw new SuwayomiException("The source host returned an empty extension jar");
        return bytes;
    }

    /// <summary>POST /v1/extensions/install-jar?pkg=: replaces the jar of an installed package; answers the host's extension row.</summary>
    public async Task<JsonObject> InstallExtensionJarAsync(string packageName, byte[] jar, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await InstallExtensionJarCoreAsync(session, packageName, jar, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<JsonObject> InstallExtensionJarCoreAsync(SourceTransportSession session, string packageName, byte[] jar, CancellationToken cancellationToken)
    {
        RequireRepairHost(packageName);
        ArgumentNullException.ThrowIfNull(jar);
        if (jar.Length == 0 || jar.Length > MaximumExtensionJar) throw new ArgumentException("The extension jar is empty or larger than 64 MiB.", nameof(jar));
        var answer = await HostCoreAsync(session, HttpMethod.Post, "/v1/extensions/install-jar?pkg=" + Uri.EscapeDataString(packageName), null, cancellationToken,
            jar, "application/java-archive", TimeSpan.FromSeconds(120)).ConfigureAwait(false);
        return answer as JsonObject ?? throw new SuwayomiException("The source host returned no extension after installing the jar");
    }

    /// <summary>GET /v1/extensions: the host's row for one package (installed or not), or null when the host does not know it.</summary>
    public async Task<JsonObject?> HostExtensionAsync(string packageName, CancellationToken cancellationToken = default)
    {
        RequireRepairHost(packageName);
        var list = await HostAsync(HttpMethod.Get, "/v1/extensions", cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject;
        return (list?["extensions"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(row => SourceResponseContract.Text(row["pkgName"]) == packageName)?.DeepClone() as JsonObject;
    }

    private void RequireRepairHost(string packageName)
    {
        if (!SourceHost) throw new SourceEngineUnavailableException("Source repairs need the AutoSourcarr source host.");
        if (string.IsNullOrWhiteSpace(packageName) || packageName.Length > 200 || packageName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_')))
            throw new ArgumentException("A plain extension package name is required.", nameof(packageName));
    }
}
