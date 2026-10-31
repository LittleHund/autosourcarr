using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Sources;

public sealed partial class SuwayomiClient
{
    public const string MangaFields = "id sourceId url title thumbnailUrl initialized artist author description genre status realUrl inLibrary";
    public const string ChapterFields = "id url name uploadDate chapterNumber scanlator mangaId sourceOrder realUrl pageCount";
    public const string SourceFields = "id name lang iconUrl supportsLatest isConfigurable isNsfw displayName homeUrl baseUrl";
    public const string ExtensionFields = "pkgName name lang versionName versionCodeLong isInstalled hasUpdate isObsolete iconUrl contentWarning storeIndexUrl apkUrl apkName source { nodes { id name lang isConfigurable } }";
    public const string ExtensionStoreFields = "name indexUrl badgeLabel isLegacy extensionListUrl";

    public async Task<List<JsonObject>> SourcesAsync(CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await session.SourcesAsync(cancellationToken).ConfigureAwait(false);
    }
    internal async Task<List<JsonObject>> SourcesCoreAsync(SourceTransportSession session, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var query in new[] {
            $"query AutoSourcarrSources {{ sources {{ nodes {{ {SourceFields} }} }} }}",
            $"query AutoSourcarrSources {{ sources {{ {SourceFields} }} }}" })
        {
            try
            {
                var data = await GraphQlCoreAsync(session, query, null, cancellationToken).ConfigureAwait(false);
                var sources = SourceResponseContract.ConnectionNodes(data["sources"]);
                if (sources.Any(source => string.IsNullOrEmpty(SourceResponseContract.Identity(source["id"]))))
                    throw new SuwayomiException("Invalid source identity in GraphQL response");
                return sources;
            }
            catch (Exception error) when (error is not (OperationCanceledException or SourceWorkDeferredException)) { session.ThrowIfRetired(); lastError = error; }
        }
        throw new SuwayomiException("Unable to query sources: " + _privacy.Text(lastError?.Message ?? "Incomplete response"));
    }

    public async Task<JsonObject> ExtensionsAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await session.ExtensionsAsync(refresh, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<JsonObject> ExtensionsCoreAsync(SourceTransportSession session, bool refresh, CancellationToken cancellationToken)
    {
        if (refresh)
        {
            var data = await GraphQlCoreAsync(session, $"mutation AutoSourcarrFetchExtensions($input: FetchExtensionsInput!) {{ fetchExtensions(input: $input) {{ extensions {{ {ExtensionFields} }} extensionStores {{ {ExtensionStoreFields} }} }} }}",
                new() { ["input"] = new JsonObject() }, cancellationToken).ConfigureAwait(false);
            if (data["fetchExtensions"] is not JsonObject payload || payload["extensions"] is not JsonArray)
                throw new SuwayomiException("Source engine returned an incomplete extension catalog refresh");
            return new() { ["extensions"] = SourceResponseContract.Array(SourceResponseContract.ConnectionNodes(payload["extensions"])),
                ["stores"] = SourceResponseContract.Array(SourceResponseContract.ConnectionNodes(payload["extensionStores"] ?? new JsonArray())) };
        }
        var current = await GraphQlCoreAsync(session, $"query AutoSourcarrExtensions {{ extensions {{ nodes {{ {ExtensionFields} }} }} }}",
            null, cancellationToken).ConfigureAwait(false);
        return new() { ["extensions"] = SourceResponseContract.Array(SourceResponseContract.ConnectionNodes(current["extensions"])), ["stores"] = new JsonArray() };
    }

    public async Task<JsonObject?> UpdateExtensionAsync(string packageName, string action, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await session.UpdateExtensionAsync(packageName, action, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<JsonObject?> UpdateExtensionCoreAsync(SourceTransportSession session, string packageName, string action, CancellationToken cancellationToken)
    {
        action = action.Trim().ToLowerInvariant();
        if (action is not ("install" or "update" or "uninstall")) throw new ArgumentException("Unsupported extension action", nameof(action));
        if (string.IsNullOrWhiteSpace(packageName)) throw new ArgumentException("Package identity is required", nameof(packageName));
        var query = $"mutation AutoSourcarrUpdateExtension($input: UpdateExtensionInput!) {{ updateExtension(input: $input) {{ extension {{ {ExtensionFields} }} }} }}";
        var variables = new JsonObject { ["input"] = new JsonObject { ["id"] = packageName, ["patch"] = new JsonObject { [action] = true } } };
        async Task<JsonObject?> PerformAsync()
        {
            var data = await GraphQlCoreAsync(session, query, variables, cancellationToken).ConfigureAwait(false);
            var item = (data["updateExtension"] as JsonObject)?["extension"] as JsonObject;
            if (action == "install" && (item is null || SourceResponseContract.Text(item["pkgName"]) != packageName
                || !SourceResponseContract.Boolean(item["isInstalled"], out var installed) || !installed))
                throw new SuwayomiException($"Source engine did not confirm installation of {packageName}. Refresh the installed extension list before trying again.");
            return item is null ? null : (JsonObject)item.DeepClone();
        }
        try { return await PerformAsync().ConfigureAwait(false); }
        catch (SuwayomiException error) when (action == "install" && IsExtensionDownloadMissing(error)) { }

        // Only this confirmed package-download failure permits a single mutation replay.
        JsonObject inventory;
        try
        {
            session.ThrowIfRetired();
            using var refreshTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken);
            refreshTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            inventory = await ExtensionsCoreAsync(session, true, refreshTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not SourceWorkDeferredException && !cancellationToken.IsCancellationRequested && !session.CancellationToken.IsCancellationRequested)
        {
            session.ThrowIfRetired();
            throw new ExtensionInstallUnavailableException($"{packageName}: package download returned HTTP 404 and the extension catalog could not be refreshed. Use Refresh catalog before trying again. No install retry was sent. {_privacy.Text(error.Message)}");
        }
        var available = ((JsonArray)inventory["extensions"]!).OfType<JsonObject>().FirstOrDefault(row => SourceResponseContract.Text(row["pkgName"]) == packageName);
        if (available is not null && SourceResponseContract.Boolean(available["isInstalled"], out var alreadyInstalled) && alreadyInstalled)
            return (JsonObject)available.DeepClone();
        if (available is null || SourceResponseContract.Boolean(available["isObsolete"], out var obsolete) && obsolete)
            throw new ExtensionInstallUnavailableException($"{packageName}: the package is unavailable in the refreshed catalog. Check the repository or try Refresh catalog later. No install retry was sent.");
        try { return await PerformAsync().ConfigureAwait(false); }
        catch (SuwayomiException error) when (IsExtensionDownloadMissing(error))
        {
            throw new ExtensionInstallUnavailableException($"{packageName}: the repository still returns HTTP 404 after refreshing the catalog and retrying once. No further automatic retries.");
        }
    }

    public async Task InstallExtensionFileAsync(string filename, byte[] data, bool apk = true, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        await session.InstallExtensionFileAsync(filename, data, apk, cancellationToken).ConfigureAwait(false);
    }
    internal async Task InstallExtensionFileCoreAsync(SourceTransportSession session, string filename, byte[] data, bool apk, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.IndexOfAny(['/', '\\', '\r', '\n', '\0']) >= 0)
            throw new ArgumentException("A plain extension filename is required.", nameof(filename));
        if (data.Length == 0) throw new ArgumentException("The extension file is empty.", nameof(data));
        if (SourceHost)
        {
            if (!apk) throw new ArgumentException("The source host installs extension APK files only.", nameof(apk));
            await HostCoreAsync(session, HttpMethod.Post, "/v1/extensions/install-file?name=" + Uri.EscapeDataString(filename), null, cancellationToken,
                data, "application/vnd.android.package-archive", TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            return;
        }
        HttpContent Multipart()
        {
            var multipart = new MultipartFormDataContent();
            var content = new StreamContent(new MemoryStream(data, writable: false));
            content.Headers.ContentType = new(apk ? "application/vnd.android.package-archive" : "application/java-archive");
            multipart.Add(content, "file", filename);
            return multipart;
        }
        await using var response = await SendAsync(session, HttpMethod.Post, new Uri(BaseUrl + "/api/v1/extension/install"), null, null, null,
            TimeSpan.FromSeconds(90), false, cancellationToken, true, Multipart).ConfigureAwait(false);
        if (response.ContentHeaders.ContentType?.MediaType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
            throw new SuwayomiException("Source engine returned a login page instead of installing the extension");
    }

    public async Task<JsonObject> UpdateAllExtensionsAsync(CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await session.UpdateAllExtensionsAsync(cancellationToken).ConfigureAwait(false);
    }
    internal async Task<JsonObject> UpdateAllExtensionsCoreAsync(SourceTransportSession session, CancellationToken cancellationToken)
    {
        var inventory = await ExtensionsCoreAsync(session, true, cancellationToken).ConfigureAwait(false);
        var ids = ((JsonArray)inventory["extensions"]!).OfType<JsonObject>().Where(row =>
            SourceResponseContract.Boolean(row["isInstalled"], out var installed) && installed
            && SourceResponseContract.Boolean(row["hasUpdate"], out var update) && update
            && !(SourceResponseContract.Boolean(row["isObsolete"], out var obsolete) && obsolete)
            && SourceResponseContract.Text(row["pkgName"]).Length > 0).Select(row => SourceResponseContract.Text(row["pkgName"])).ToArray();
        if (ids.Length == 0) { inventory["updated"] = 0; return inventory; }
        var data = await GraphQlCoreAsync(session, $"mutation AutoSourcarrUpdateExtensions($input: UpdateExtensionsInput!) {{ updateExtensions(input: $input) {{ extensions {{ {ExtensionFields} }} }} }}",
            new() { ["input"] = new JsonObject { ["ids"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()), ["patch"] = new JsonObject { ["update"] = true } } }, cancellationToken).ConfigureAwait(false);
        var changed = (data["updateExtensions"] as JsonObject)?["extensions"] ?? new JsonArray();
        return new() { ["updated"] = ids.Length, ["extensions"] = SourceResponseContract.Array(SourceResponseContract.ConnectionNodes(changed)), ["stores"] = inventory["stores"]!.DeepClone() };
    }

    private static bool IsExtensionDownloadMissing(SuwayomiException error) => Regex.IsMatch(error.Message,
        @"^Exception while fetching data \(/updateExtension\)\s*:\s*HTTP error 404\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
