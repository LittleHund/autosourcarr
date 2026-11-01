using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Repositories;

namespace AutoSourcarr.Core.Reader.Extension;

/// <summary>What the downloads need from the source engine (the AutoSourcarr source host).</summary>
public interface IReaderExtensionHost
{
    /// <summary>The host's row for an installed package (pkgName, versionName, apkUrl, storeIndexUrl, isInstalled), or null.</summary>
    Task<JsonObject?> InstalledExtensionAsync(string package, CancellationToken cancellationToken);
    /// <summary>The JAR the host runs for the package.</summary>
    Task<byte[]> ReadJarAsync(string package, CancellationToken cancellationToken);
}

/// <summary><see cref="IReaderExtensionHost"/> over the source host client (GET /v1/extensions, /v1/extensions/{pkg}/jar).</summary>
public sealed class SourceHostReaderExtensionHost(Func<SuwayomiClient?> client) : IReaderExtensionHost
{
    public Task<JsonObject?> InstalledExtensionAsync(string package, CancellationToken cancellationToken) =>
        (client() ?? throw new SourceEngineUnavailableException("Source downloads need the AutoSourcarr source host.")).HostExtensionAsync(package, cancellationToken);

    public Task<byte[]> ReadJarAsync(string package, CancellationToken cancellationToken) =>
        (client() ?? throw new SourceEngineUnavailableException("Source downloads need the AutoSourcarr source host.")).ReadExtensionJarAsync(package, cancellationToken);
}

/// <summary>The download could not be produced; <see cref="StatusCode"/> is the HTTP answer.</summary>
public sealed class ExtensionDownloadException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// READ-2: APK and JAR downloads for every installed source, never stale. The JAR is the one the source host runs right
/// now. The host keeps no APKs, so the APK is resolved at request time: the store index the package came from is read
/// again, the package's current APK is downloaded through <see cref="IPublicSourceFetcher"/> (public HTTPS only), and it is
/// checked (APK structure, and the index's sha256 when the store publishes one). AutoSourcarr's own reader extension
/// comes from its local repository instead.
/// </summary>
public sealed class InstalledExtensionDownloads(IReaderExtensionHost host, IPublicSourceFetcher fetcher, ReaderExtensionRepository repository)
{
    private const int MaximumApk = 64 * 1024 * 1024;

    public static bool IsPackageName(string package) =>
        package.Length is > 0 and <= 200 && package.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_');

    public async Task<ReaderRepositoryFile> JarAsync(string package, CancellationToken cancellationToken)
    {
        var row = await InstalledAsync(package, cancellationToken).ConfigureAwait(false);
        var bytes = await host.ReadJarAsync(package, cancellationToken).ConfigureAwait(false);
        return new(bytes, "application/java-archive", FileName(package, row, ".jar"));
    }

    public async Task<ReaderRepositoryFile> ApkAsync(string package, CancellationToken cancellationToken)
    {
        if (package == ReaderExtensionRepository.Package && repository.Extension() is { } own
            && repository.Read("apk/" + ReaderExtensionRepository.Text(own["apk"])) is { } local)
        {
            Verify(local.Body, ReaderExtensionRepository.Text(own["sha256"]));
            return local;
        }
        var row = await InstalledAsync(package, cancellationToken).ConfigureAwait(false);
        var store = ReaderExtensionRepository.Text(row["storeIndexUrl"]);
        if (!Uri.TryCreate(store, UriKind.Absolute, out var storeUrl))
            throw new ExtensionDownloadException(409, "This source was not installed from an extension store (no store index), so there is no APK to download. Use the JAR.");
        PublicSourceResponse index;
        try { index = await fetcher.FetchAsync(new(storeUrl, RepositoryManifest.MaximumBytes, TimeSpan.FromSeconds(30)), cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw new ExtensionDownloadException(502, "The extension store could not be read: " + error.Message); }
        IndexedExtension? listed;
        try { listed = ExtensionIndex.Parse(index.Body, index.ResolvedUrl).Extensions.FirstOrDefault(item => item.Package == package); }
        catch (InvalidDataException error) { throw new ExtensionDownloadException(502, "The extension store index is not readable: " + error.Message); }
        if (listed is null) throw new ExtensionDownloadException(410, "The extension store no longer lists this source; only the installed JAR is available.");
        if (!Uri.TryCreate(listed.ApkUrl, UriKind.Absolute, out var apkUrl)) throw new ExtensionDownloadException(502, "The extension store lists no APK address for this source.");
        PublicSourceResponse apk;
        try { apk = await fetcher.FetchAsync(new(apkUrl, MaximumApk, TimeSpan.FromSeconds(60)), cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw new ExtensionDownloadException(502, "The APK could not be downloaded from the extension store: " + error.Message); }
        Verify(apk.Body, StoreDigest(index.Body, package));
        var name = Path.GetFileName(apk.ResolvedUrl.AbsolutePath);
        return new(apk.Body, "application/vnd.android.package-archive", name.EndsWith(".apk", StringComparison.Ordinal) ? name : FileName(package, row, ".apk"));
    }

    private async Task<JsonObject> InstalledAsync(string package, CancellationToken cancellationToken)
    {
        if (!IsPackageName(package)) throw new ExtensionDownloadException(400, "A plain extension package name is required.");
        var row = await host.InstalledExtensionAsync(package, cancellationToken).ConfigureAwait(false);
        if (row is null || row["isInstalled"] is not JsonValue installed || !installed.TryGetValue<bool>(out var yes) || !yes)
            throw new ExtensionDownloadException(404, package + " is not installed.");
        return row;
    }

    private static string FileName(string package, JsonObject row, string extension)
    {
        var version = ReaderExtensionRepository.Text(row["versionName"]);
        var safe = new string(version.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return package + (safe.Length == 0 ? "" : "-v" + safe) + extension;
    }

    /// <summary>A legacy index.min.json row may carry "sha256" (AutoSourcarr's own index does); index.pb rows do not.</summary>
    private static string StoreDigest(byte[] index, string package)
    {
        if (index.Length == 0 || index[0] != (byte)'[') return "";
        try
        {
            return (JsonNode.Parse(index) as JsonArray)?.OfType<JsonObject>()
                .Where(row => ReaderExtensionRepository.Text(row["pkg"]) == package)
                .Select(row => ReaderExtensionRepository.Text(row["sha256"])).FirstOrDefault() ?? "";
        }
        catch (System.Text.Json.JsonException) { return ""; }
    }

    private static void Verify(byte[] apk, string expectedSha256)
    {
        if (expectedSha256.Length > 0 && !string.Equals(Convert.ToHexStringLower(SHA256.HashData(apk)), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new ExtensionDownloadException(502, "The APK does not match the sha256 its extension store lists.");
        try
        {
            using var zip = new ZipArchive(new MemoryStream(apk, writable: false), ZipArchiveMode.Read);
            if (zip.GetEntry("AndroidManifest.xml") is null || !zip.Entries.Any(entry => entry.FullName.StartsWith("classes", StringComparison.Ordinal) && entry.FullName.EndsWith(".dex", StringComparison.Ordinal)))
                throw new ExtensionDownloadException(502, "The downloaded file is not an extension APK.");
        }
        catch (InvalidDataException) { throw new ExtensionDownloadException(502, "The downloaded file is not an APK archive."); }
    }
}
