using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Configuration;
using AutoSourcarr.Core.Sources.Controls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the source routes need from the running app.</summary>
public sealed class SourceServices
{
    public required SourceConfigurationStore Configuration { get; init; }
    public required SuwayomiClient Client { get; init; }
    public required SourcePreferenceService Preferences { get; init; }
    public required SemaphoreSlim AdminLock { get; init; }
    /// <summary>SRC-2: where an inspected APK waits (10 minutes) for its confirmation (&lt;data&gt;/source-upload-previews);
    /// null uses a folder in the system temp directory.</summary>
    public string? UploadStagingRoot { get; init; }
}

/// <summary>
/// SRC-2 two-step APK install: the upload is inspected and its bytes wait in a per-SHA-256 slot
/// (&lt;root&gt;/&lt;sha256&gt;.apk plus &lt;sha256&gt;.json with the file name and expiry) until the person confirms
/// within <see cref="Lifetime"/>. Expired slots are removed whenever a slot is written or read.
/// </summary>
internal static class ApkUploadSlots
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly System.Text.RegularExpressions.Regex Digest = new(@"\A[0-9a-f]{64}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly object Gate = new();

    public static string Root(SourceServices sources) => Path.GetFullPath(sources.UploadStagingRoot ?? Path.Combine(Path.GetTempPath(), "autosourcarr-upload-previews"));

    public static bool IsDigest(string? value) => value is not null && Digest.IsMatch(value);

    /// <summary>Keeps the inspected bytes; answers the expiry.</summary>
    public static DateTimeOffset Store(string root, string sha256, byte[] data, string filename)
    {
        var expires = DateTimeOffset.UtcNow + Lifetime;
        lock (Gate)
        {
            Prune(root);
            AutoSourcarr.Core.Settings.ConfigFiles.WriteAtomic(Path.Combine(root, sha256 + ".apk"), data);
            var record = new JsonObject { ["filename"] = filename, ["expires_at"] = expires.ToString("O", System.Globalization.CultureInfo.InvariantCulture) };
            AutoSourcarr.Core.Settings.ConfigFiles.WriteAtomic(Path.Combine(root, sha256 + ".json"), System.Text.Encoding.UTF8.GetBytes(record.ToJsonString()));
        }
        return expires;
    }

    /// <summary>Takes a waiting upload out of its slot (the slot is removed); null when unknown, expired or changed.</summary>
    public static (byte[] Data, string Filename)? Take(string root, string sha256)
    {
        lock (Gate)
        {
            Prune(root);
            var apk = new FileInfo(Path.Combine(root, sha256 + ".apk"));
            var meta = new FileInfo(Path.Combine(root, sha256 + ".json"));
            try
            {
                if (!apk.Exists || !meta.Exists || apk.LinkTarget is not null || meta.LinkTarget is not null
                    || apk.Length > AutoSourcarr.Core.Repair.ApkInspection.MaximumBytes || meta.Length > 64 * 1024) return null;
                var record = JsonNode.Parse(File.ReadAllBytes(meta.FullName)) as JsonObject;
                var data = File.ReadAllBytes(apk.FullName);
                if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)) != sha256) return null;
                var filename = record?["filename"] is JsonValue name && name.TryGetValue<string>(out var text) ? text : sha256 + ".apk";
                return (data, filename);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
            finally { Remove(root, sha256); }
        }
    }

    private static void Prune(string root)
    {
        if (!Directory.Exists(root)) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            var sha = Path.GetFileNameWithoutExtension(path);
            if (!IsDigest(sha)) continue;
            DateTimeOffset? expires = null;
            try
            {
                if (new FileInfo(path).Length <= 64 * 1024 && JsonNode.Parse(File.ReadAllBytes(path)) is JsonObject record
                    && record["expires_at"] is JsonValue value && value.TryGetValue<string>(out var text)
                    && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                    expires = parsed;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            if (expires is null || expires.Value <= now) Remove(root, sha);
        }
        // An .apk without its record is a leftover of an interrupted write.
        foreach (var path in Directory.EnumerateFiles(root, "*.apk"))
            if (IsDigest(Path.GetFileNameWithoutExtension(path)) && !File.Exists(Path.ChangeExtension(path, ".json"))) TryDelete(path);
    }

    private static void Remove(string root, string sha256)
    {
        TryDelete(Path.Combine(root, sha256 + ".apk"));
        TryDelete(Path.Combine(root, sha256 + ".json"));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Phase 6 (port of main.py SourceEndpoints): source list and sync, groups, per-source settings, source
/// (the admin list is /api/v1/source-settings: /api/v1/sources is the reader's public source list),
/// preferences and source manga details under /api/v1. Sources come from the engine (the source host);
/// enabled/priority/group choices are AutoSourcarr's own (SourceConfigurationStore).
/// </summary>
internal static class SourceV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json)
    {
        IResult Unavailable() => Results.Json(new ProblemResponse("Sources are not available in this host."), json.ProblemResponse, statusCode: 503);
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);

        group.MapPost("/source-settings/sync", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Sources is not { } sources) return Unavailable();
                var rows = await sources.Client.SourcesAsync(context.RequestAborted).ConfigureAwait(false);
                var stored = sources.Configuration.PersistSnapshot(CompletedSourceSnapshot.FromCompleteNativeList(rows));
                return Results.Json(new JsonObject { ["items"] = SourceConfigurationJson.Array(stored) }, json.JsonObject);
            }))
            .WithName("syncSources")
            .WithSummary("Read the installed sources from the engine and store them (new sources get the default choices; empty engine lists keep existing ones).")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapGet("/source-settings", (Func<HttpContext, IResult>)(_ =>
            {
                if (services.Sources is not { } sources) return Unavailable();
                return Results.Json(new JsonObject
                {
                    ["items"] = SourceConfigurationJson.Array(sources.Configuration.ListSources()),
                    ["groups"] = SourceConfigurationJson.Array(sources.Configuration.ListGroups())
                }, json.JsonObject);
            }))
            .WithName("getSources")
            .WithSummary("Available sources with enabled, priority, group and language choices, and the source groups.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/source-groups", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Sources is not { } sources) return Unavailable();
                var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
                try { return Results.Json(sources.Configuration.CreateGroup(SourceConfigurationJson.Text(body["name"]), body["priority"] is null ? 1000 : SourceConfigurationJson.Integer(body["priority"])), json.JsonObject); }
                catch (ArgumentException error) { return Problem(400, error.Message); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("createSourceGroup")
            .WithSummary("Create a source group (members share one priority; the member with the most chapters serves).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest);

        group.MapPut("/source-groups/{groupId}", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Sources is not { } sources) return Unavailable();
                if (!long.TryParse(context.Request.RouteValues["groupId"]?.ToString(), out var id)) return Problem(404, "Source group not found");
                var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
                try
                {
                    return Results.Json(sources.Configuration.UpdateGroup(id, body["name"] is null ? null : SourceConfigurationJson.Text(body["name"]),
                        body["priority"] is null ? null : SourceConfigurationJson.Integer(body["priority"])), json.JsonObject);
                }
                catch (KeyNotFoundException) { return Problem(404, "Source group not found"); }
                catch (ArgumentException error) { return Problem(400, error.Message); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("updateSourceGroup")
            .WithSummary("Rename a source group or change its priority.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapDelete("/source-groups/{groupId}", (Func<HttpContext, IResult>)(context =>
            {
                if (services.Sources is not { } sources) return Unavailable();
                if (!long.TryParse(context.Request.RouteValues["groupId"]?.ToString(), out var id)) return Problem(404, "Source group not found");
                try { return Results.Json(sources.Configuration.DeleteGroup(id), json.JsonObject); }
                catch (KeyNotFoundException) { return Problem(404, "Source group not found"); }
            }))
            .WithName("deleteSourceGroup")
            .WithSummary("Delete a source group; its members become ungrouped.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPut("/source-settings/{sourceId}", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Sources is not { } sources) return Unavailable();
                var id = context.Request.RouteValues["sourceId"]?.ToString() ?? "";
                var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
                try { return Results.Json(sources.Configuration.UpdateSource(id, body), json.JsonObject); }
                catch (KeyNotFoundException) { return Problem(404, "Source not found"); }
                catch (ArgumentException error) { return Problem(400, error.Message); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("updateSource")
            .WithSummary("Change a source's choices: enabled, priority, group, official, matching rules.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/sources/{sourceId}/preferences", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Sources is not { } sources) return Unavailable();
                context.Response.Headers.CacheControl = "no-store";
                try { return Results.Json(await sources.Preferences.SnapshotAsync(context.Request.RouteValues["sourceId"]?.ToString() ?? "", context.RequestAborted).ConfigureAwait(false), json.JsonObject); }
                catch (SourceControlFailedException error) { return Problem(error.RefreshRequired ? 409 : 502, error.Message); }
                catch (SourceWorkDeferredException error) { return Problem(503, error.Message); }
            }))
            .WithName("getSourcePreferences")
            .WithSummary("The extension's own settings for this source (as in Mihon), with current values.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPut("/sources/{sourceId}/preferences", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Sources is not { } sources) return Unavailable();
                var body = await ApiJson.ReadObjectAsync(context.Request, 64 * 1024).ConfigureAwait(false);
                await sources.AdminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                try { return Results.Json(await sources.Preferences.UpdateAsync(context.Request.RouteValues["sourceId"]?.ToString() ?? "", body, context.RequestAborted).ConfigureAwait(false), json.JsonObject); }
                catch (SourceControlFailedException error) { return Problem(error.RefreshRequired ? 409 : 400, error.Message); }
                catch (SourceWorkDeferredException error) { return Problem(503, error.Message); }
                finally { sources.AdminLock.Release(); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("setSourcePreference")
            .WithSummary("Change one extension setting of this source.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest);

        // Extensions (installed and available), answered by the engine; install/update/uninstall refresh the source list.
        async Task SyncAsync(SourceServices sources)
        {
            try
            {
                var rows = await sources.Client.SourcesAsync(CancellationToken.None).ConfigureAwait(false);
                sources.Configuration.PersistSnapshot(CompletedSourceSnapshot.FromCompleteNativeList(rows));
            }
            catch (Exception error) when (error is SuwayomiException or SourceWorkDeferredException or HttpRequestException) { }
        }

        group.MapGet("/source-extensions", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Sources is not { } sources) return Unavailable();
                var refresh = string.Equals(context.Request.Query["refresh"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
                try { return Results.Json(Annotated(services, await sources.Client.ExtensionsAsync(refresh, context.RequestAborted).ConfigureAwait(false)), json.JsonObject); }
                catch (SourceWorkDeferredException error) { return Problem(503, error.Message); }
                catch (SuwayomiException error) { return Problem(503, "Unable to read the extension catalog: " + error.Message); }
            }))
            .WithName("getSourceExtensions")
            .WithSummary("Installed and available extensions from the configured repositories (?refresh=true reloads the repositories).")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/source-extensions/{pkgName}/action", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Sources is not { } sources) return Unavailable();
                var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
                var action = SourceConfigurationJson.Text(body["action"]);
                if (action is not ("install" or "update" or "uninstall")) return Problem(400, "Action is install, update or uninstall.");
                await sources.AdminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                try
                {
                    var package = context.Request.RouteValues["pkgName"]?.ToString() ?? "";
                    var extension = await sources.Client.UpdateExtensionAsync(package, action, context.RequestAborted).ConfigureAwait(false);
                    _ = SyncAsync(sources);
                    return Results.Json(new JsonObject { ["extension"] = AnnotatedExtension(services, package, extension), ["source_sync_pending"] = true }, json.JsonObject);
                }
                catch (ArgumentException error) { return Problem(400, error.Message); }
                catch (SourceWorkDeferredException error) { return Problem(503, error.Message); }
                catch (SuwayomiException error) { return Problem(502, error.Message); }
                finally { sources.AdminLock.Release(); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("changeSourceExtension")
            .WithSummary("Install, update or uninstall an extension; the source list refreshes in the background.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest);

        static JsonArray Strings(IEnumerable<string> items) => new(items.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray());

        // SRC-2 step 1: inspect and keep for confirmation; nothing is installed.
        group.MapPost("/source-extensions/upload", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Sources is not { } sources) return Unavailable();
                var media = context.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
                if (media is not ("application/vnd.android.package-archive" or "application/octet-stream")) return Problem(415, "Choose an Android source extension APK file.");
                if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = AutoSourcarr.Core.Repair.ApkInspection.MaximumBytes + 1;
                var filename = Uri.UnescapeDataString(context.Request.Headers["X-Filename"].ToString());
                using var buffer = new MemoryStream();
                var chunk = new byte[81920]; int read;
                while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > AutoSourcarr.Core.Repair.ApkInspection.MaximumBytes) return Problem(413, "A source APK must be at most 32 MiB.");
                }
                var data = buffer.ToArray();
                AutoSourcarr.Core.Repair.ApkReport report;
                try { report = AutoSourcarr.Core.Repair.ApkInspection.Inspect(data, filename); }
                catch (InvalidDataException error) { return Problem(400, error.Message); }
                DateTimeOffset expires;
                try { expires = ApkUploadSlots.Store(ApkUploadSlots.Root(sources), report.Sha256, data, filename); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { return Problem(503, "The APK could not be kept for confirmation: " + error.Message); }
                return Results.Json(new JsonObject
                {
                    ["preview"] = true, ["confirm_required"] = true, ["installed"] = false,
                    ["package"] = report.Package, ["class_name"] = report.ClassName, ["library_version"] = report.LibraryVersion,
                    ["sha256"] = report.Sha256, ["bytes"] = report.Bytes,
                    ["permissions"] = Strings(report.Permissions), ["domains"] = Strings(report.Domains), ["warnings"] = Strings(report.Warnings),
                    ["expires_at"] = expires.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    ["message"] = "Checked, not installed. Review the permissions, web addresses and warnings, then confirm within 10 minutes."
                }, json.JsonObject);
            }))
            .WithName("uploadSourceExtension")
            .WithSummary("Check a source APK (archive structure, manifest and entry point, no native code) and list its permissions, web addresses and warnings; nothing is installed. The file waits 10 minutes for POST /source-extensions/upload/confirm with its sha256.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ProblemResponse>(StatusCodes.Status415UnsupportedMediaType);

        // SRC-2 step 2: install the inspected file the person confirmed.
        group.MapPost("/source-extensions/upload/confirm", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Sources is not { } sources) return Unavailable();
                var body = await ApiJson.ReadObjectAsync(context.Request, 4096).ConfigureAwait(false);
                var sha = body["sha256"] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim().ToLowerInvariant() : "";
                if (!ApkUploadSlots.IsDigest(sha)) return Problem(400, "sha256 of a checked upload is required.");
                if (ApkUploadSlots.Take(ApkUploadSlots.Root(sources), sha) is not { } waiting)
                    return Problem(404, "This upload is unknown or older than 10 minutes. Drop the APK again.");
                AutoSourcarr.Core.Repair.ApkReport report;
                try { report = AutoSourcarr.Core.Repair.ApkInspection.Inspect(waiting.Data, waiting.Filename); }
                catch (InvalidDataException error) { return Problem(400, error.Message); }
                await sources.AdminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                try
                {
                    // Never the person's filename: the package name decides which installed jar it replaces.
                    await sources.Client.InstallExtensionFileAsync(report.Package + ".apk", waiting.Data, true, context.RequestAborted).ConfigureAwait(false);
                    var installed = await sources.Client.ExtensionsAsync(false, context.RequestAborted).ConfigureAwait(false);
                    var extension = (installed["extensions"] as JsonArray ?? []).OfType<JsonObject>()
                        .FirstOrDefault(row => SourceConfigurationJson.Text(row["pkgName"]) == report.Package && SourceConfigurationJson.Flag(row["isInstalled"]));
                    if (extension is null) return Problem(502, "The source engine did not confirm the installation.");
                    _ = SyncAsync(sources);
                    return Results.Json(new JsonObject
                    {
                        ["extension"] = extension.DeepClone(), ["sha256"] = report.Sha256, ["source_sync_pending"] = true, ["installed"] = true,
                        ["library_version"] = report.LibraryVersion,
                        ["permissions"] = Strings(report.Permissions), ["domains"] = Strings(report.Domains), ["warnings"] = Strings(report.Warnings),
                        ["message"] = "Source APK installed. Website access and chapter retrieval have not been checked."
                    }, json.JsonObject);
                }
                catch (SourceWorkDeferredException error) { return Problem(503, error.Message); }
                catch (SuwayomiException error) { return Problem(502, error.Message); }
                finally { sources.AdminLock.Release(); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("confirmSourceExtensionUpload")
            .WithSummary("Install a source APK checked by POST /source-extensions/upload (body: sha256) within 10 minutes of its check.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status502BadGateway);

        group.MapPost("/source-extensions/update-all", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Sources is not { } sources) return Unavailable();
                await sources.AdminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                try
                {
                    var result = await sources.Client.UpdateAllExtensionsAsync(context.RequestAborted).ConfigureAwait(false);
                    _ = SyncAsync(sources);
                    result["source_sync_pending"] = true;
                    return Results.Json(result, json.JsonObject);
                }
                catch (SourceWorkDeferredException error) { return Problem(503, error.Message); }
                catch (SuwayomiException error) { return Problem(503, "Unable to update source extensions: " + error.Message); }
                finally { sources.AdminLock.Release(); }
            }))
            .WithName("updateAllSourceExtensions")
            .WithSummary("Update every installed extension that has a newer version.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapGet("/source-manga/{mangaId}", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Sources is not { } sources) return Unavailable();
                if (!long.TryParse(context.Request.RouteValues["mangaId"]?.ToString(), out var id) || id <= 0) return Problem(404, "Unknown source manga");
                try { return Results.Json(await sources.Client.SourceMangaDetailsAsync(id, context.RequestAborted).ConfigureAwait(false), json.JsonObject); }
                catch (SuwayomiException error) { return Problem(502, "Unable to load source manga details: " + error.Message); }
            }))
            .WithName("getSourceManga")
            .WithSummary("Details of one title as its source reports them (refreshed from the source).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status502BadGateway);
    }

    /// <summary>
    /// 0.4.x classified the extension list with the orphan labels (is_orphaned, orphan_reason, can_mark_orphan,
    /// can_unmark_orphan, repository_missing) so orphans leave the Installed list. Without the orphan service, or
    /// with unreadable orphan state, the list is returned as the engine sent it.
    /// </summary>
    private static JsonObject Annotated(ApiV1Services services, JsonObject result)
    {
        if (services.SourceInventory is not { } inventory || result["extensions"] is not JsonArray rows) return result;
        try
        {
            var extensions = rows.OfType<JsonObject>().ToList();
            inventory.Orphans.AcceptInventory(extensions);
            var annotated = inventory.Orphans.Annotate(extensions, SourceInventoryV1Endpoints.ConfiguredRepositories(services));
            result["extensions"] = new JsonArray(annotated.Select(row => (JsonNode?)row).ToArray());
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { }
        return result;
    }

    private static JsonNode? AnnotatedExtension(ApiV1Services services, string package, JsonObject? extension)
    {
        if (services.SourceInventory is not { } inventory) return extension?.DeepClone();
        inventory.Orphans.UpdateInventoryPackage(package, extension);
        if (extension is null) return null;
        try { return inventory.Orphans.Annotate([extension], SourceInventoryV1Endpoints.ConfiguredRepositories(services))[0]; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return extension.DeepClone(); }
    }
}
