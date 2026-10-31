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
                await sources.AdminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                try
                {
                    // Never the person's filename: the package name decides which installed jar it replaces.
                    await sources.Client.InstallExtensionFileAsync(report.Package + ".apk", data, true, context.RequestAborted).ConfigureAwait(false);
                    var installed = await sources.Client.ExtensionsAsync(false, context.RequestAborted).ConfigureAwait(false);
                    var extension = (installed["extensions"] as JsonArray ?? []).OfType<JsonObject>()
                        .FirstOrDefault(row => SourceConfigurationJson.Text(row["pkgName"]) == report.Package && SourceConfigurationJson.Flag(row["isInstalled"]));
                    if (extension is null) return Problem(502, "The source engine did not confirm the installation.");
                    _ = SyncAsync(sources);
                    return Results.Json(new JsonObject
                    {
                        ["extension"] = extension.DeepClone(), ["sha256"] = report.Sha256, ["source_sync_pending"] = true,
                        ["library_version"] = report.LibraryVersion,
                        ["permissions"] = new JsonArray(report.Permissions.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray()),
                        ["domains"] = new JsonArray(report.Domains.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray()),
                        ["warnings"] = new JsonArray(report.Warnings.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray()),
                        ["message"] = "Source APK installed. Website access and chapter retrieval have not been checked."
                    }, json.JsonObject);
                }
                catch (SourceWorkDeferredException error) { return Problem(503, error.Message); }
                catch (SuwayomiException error) { return Problem(502, error.Message); }
                finally { sources.AdminLock.Release(); }
            }))
            .WithName("uploadSourceExtension")
            .WithSummary("Install a source APK by drag and drop after the checks: archive structure, manifest and entry point, no native code; lists permissions and web addresses.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest);

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
