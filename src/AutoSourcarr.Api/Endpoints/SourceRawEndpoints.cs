using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Repair;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

public interface IRawReconstructionCommands
{
    Task EnsureReadyAsync(CancellationToken cancellationToken);
    Task<JsonObject> ReconstructAsync(RawInstalledSource installed, long rawRevision, string? previousJobId, CancellationToken cancellationToken);
}

/// <summary>The retained Web UI wire routes. Reconstruction requires a real isolation-qualified command owner.</summary>
public static class SourceRawEndpoints
{
    /// <param name="prefix">Where the seven routes live; /api/v1 maps them inside its typed group instead (see <see cref="MapRoutes"/>).</param>
    public static RouteGroupBuilder MapSourceRaws(this IEndpointRouteBuilder endpoints, SourceRawService raws, IRawInstalledSources installed,
        IRawReconstructionCommands reconstruction, Func<AutoSourcarrSettings> settings, SemaphoreSlim adminLock, BoundedWorkScheduler io, RepairHistory? history = null,
        string prefix = "/api/admin/source-maintenance/raws")
    {
        var api = new Handler(raws, installed, reconstruction, adminLock, io, history);
        var group = endpoints.MapGroup(prefix).AddEndpointFilter(new AdminAuthorizationFilter(settings));
        MapRoutes(group, "", () => api, wrap: null);
        return group;
    }

    /// <summary>
    /// The seven raw-code routes under <paramref name="path"/> of <paramref name="group"/>. <paramref name="handler"/> answers null when
    /// source-code storage is not wired (the caller's <paramref name="wrap"/> then answers 503); <paramref name="wrap"/> also turns the
    /// handlers' ApiProblem into the caller's problem shape. Returns the route builders in order: status, project, request-input,
    /// code, origin, discover, unavailable.
    /// </summary>
    internal static RouteHandlerBuilder[] MapRoutes(RouteGroupBuilder group, string path, Func<Handler?> handler,
        Func<HttpContext, Func<Handler, Task<IResult>>, Task<IResult>>? wrap)
    {
        Func<HttpContext, Func<Handler, Task<IResult>>, Task<IResult>> run = wrap
            ?? ((context, action) => action(handler() ?? throw new ApiProblem(503, "Source-code storage is not available in this host")));
        Func<HttpContext, Task<IResult>> Route(Func<Handler, HttpContext, Task<IResult>> action) => context => run(context, api => action(api, context));
        // Handlers go through locals: the route-handler analyzer fails (AD0001) on a delegate produced by an invocation.
        Func<HttpContext, Task<IResult>> status = Route((api, context) => api.StatusAsync(context));
        Func<HttpContext, Task<IResult>> project = Route((api, context) => api.ProjectAsync(context));
        Func<HttpContext, Task<IResult>> requestInput = Route((api, context) => api.RequestInputAsync(context));
        Func<HttpContext, Task<IResult>> code = Route((api, context) => api.CodeAsync(context));
        Func<HttpContext, Task<IResult>> origin = Route((api, context) => api.OriginAsync(context));
        Func<HttpContext, Task<IResult>> discover = Route((api, context) => api.DiscoverAsync(context));
        Func<HttpContext, Task<IResult>> unavailable = Route((api, context) => api.UnavailableAsync(context));
        return
        [
            group.MapGet(path + "/{package}", status),
            group.MapPost(path + "/{package}/project", project).WithMetadata(ApiJson.BoundedUploadMetadata),
            group.MapPost(path + "/{package}/request-input", requestInput),
            group.MapGet(path + "/{package}/code", code),
            group.MapPost(path + "/{package}/origin", origin),
            group.MapPost(path + "/{package}/discover", discover),
            group.MapPost(path + "/{package}/unavailable", unavailable),
        ];
    }

    internal sealed class Handler(SourceRawService raws, IRawInstalledSources installed, IRawReconstructionCommands reconstruction, SemaphoreSlim admin, BoundedWorkScheduler io, RepairHistory? history)
    {
        public async Task<IResult> StatusAsync(HttpContext context)
        {
            var package = Package(context); var catalog = Catalog(); JsonObject row;
            await admin.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                var source = await SelectAsync(package, context.RequestAborted).ConfigureAwait(false); var cached = catalog.Status(package);
                var saved = history is null ? new JsonObject() : await io.RunAsync(() => history.Snapshot(package), context.RequestAborted).ConfigureAwait(false);
                var current = saved["current"] as JsonObject;
                if (current?["raw_id"] is JsonValue rawId && rawId.TryGetValue<string>(out var id) && SourceOrigin.Optional(current, "binary_sha256") == source.BinarySha256 && (cached is null || SourceOrigin.Optional(cached, "binary_sha256") != source.BinarySha256))
                    row = await io.RunAsync(() => catalog.Adopt(source.Extension, source.BinarySha256, id), context.RequestAborted).ConfigureAwait(false);
                else row = await io.RunAsync(() => catalog.Begin(source.Extension, binarySha256: source.BinarySha256), context.RequestAborted).ConfigureAwait(false);
            }
            finally { admin.Release(); }
            if (Text(row, "state") == "acquiring") raws.ScheduleIntent(row);
            return Results.Json(row);
        }
        public async Task<IResult> ProjectAsync(HttpContext context)
        {
            var package = Package(context); var catalog = Catalog();
            if (!long.TryParse(context.Request.Headers["X-Raw-Revision"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision) || revision < 1 || string.IsNullOrEmpty(context.Request.Headers["X-Source-Version"]))
                throw new ApiProblem(400, "Refresh the raw-code request before uploading");
            var version = context.Request.Headers["X-Source-Version"].ToString();
            var bytes = await ApiJson.ReadUploadAsync(context.Request, RawCodeRules.MaximumZip,
                "Source project ZIP exceeds32MiB", "Source project upload timed out; select the file again").ConfigureAwait(false);
            await admin.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                var source = await SelectAsync(package, context.RequestAborted).ConfigureAwait(false);
                if (Text(source.Extension, "versionName") != version) throw new ApiProblem(409, "Source version changed; refresh before supplying code");
                var module = context.Request.Headers["X-Source-Module"].ToString();
                try { return Results.Json(await io.RunAsync(() => catalog.Provide(source.Extension, bytes, revision, source.BinarySha256, module.Length == 0 ? null : module), context.RequestAborted).ConfigureAwait(false)); }
                catch (InvalidDataException error) { throw new ApiProblem(409, error.Message); }
            }
            finally { admin.Release(); }
        }
        public async Task<IResult> RequestInputAsync(HttpContext context)
        {
            var package = Package(context); var catalog = Catalog(); var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); var revision = Revision(body);
            await admin.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                var source = await SelectAsync(package, context.RequestAborted).ConfigureAwait(false); Current(catalog, package, source, revision);
                return Results.Json(await io.RunAsync(() => catalog.RequestInput(package, revision, source.BinarySha256), context.RequestAborted).ConfigureAwait(false));
            }
            finally { admin.Release(); }
        }
        public async Task<IResult> CodeAsync(HttpContext context)
        {
            var package = Package(context); var catalog = Catalog(); var row = catalog.Status(package);
            if (row is null || Text(row, "state") != "available") throw new ApiProblem(409, "No retained raw code is selected for this source");
            var bytes = await io.RunAsync(() => { var resolved = catalog.Resolve(package, Text(row, "raw_id")); return resolved.Store.ExportZip(package, Text(resolved.Snapshot, "id"), portable: true); }, context.RequestAborted).ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(bytes, "application/zip", "AutoSourcarr-source-code.zip", enableRangeProcessing: false);
        }
        public async Task<IResult> OriginAsync(HttpContext context)
        {
            var package = Package(context); var catalog = Catalog(); var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); var revision = Revision(body); JsonObject row;
            if (body["origin"] is not JsonObject origin) throw new ApiProblem(400, "Provide the original source repository");
            await admin.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                var source = await SelectAsync(package, context.RequestAborted).ConfigureAwait(false); Current(catalog, package, source, revision);
                try { row = await io.RunAsync(() => catalog.Begin(source.Extension, binarySha256: source.BinarySha256, supplied: origin, force: true), context.RequestAborted).ConfigureAwait(false); }
                catch (InvalidDataException error) { throw new ApiProblem(400, error.Message); }
            }
            finally { admin.Release(); }
            raws.ScheduleIntent(row); return Results.Json(row, statusCode: 202);
        }
        public async Task<IResult> DiscoverAsync(HttpContext context)
        {
            var package = Package(context); var catalog = Catalog(); var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
            if (body.Count != 1) throw new ApiProblem(400, "Refresh the installed code request before discovery"); var revision = Revision(body); JsonObject row; RawInstalledSource source;
            await admin.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                source = await SelectAsync(package, context.RequestAborted).ConfigureAwait(false); row = Current(catalog, package, source, revision);
                if (Text(row, "state") != "needs-input") throw new ApiProblem(409, "This source already has code or an active acquisition");
            }
            finally { admin.Release(); }
            raws.DiscoverIntent(row, source.Extension); row["message"] = "Repository lookup queued. Refresh this source after the archive task finishes; original installed bytes remain unchanged.";
            return Results.Json(row, statusCode: 202);
        }
        public async Task<IResult> UnavailableAsync(HttpContext context)
        {
            var package = Package(context); var catalog = Catalog(); var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); var revision = Revision(body);
            string? previous = null;
            if (RawCodeRules.Present(body["job_id"]))
            { previous = Text(body, "job_id"); if (!Regex.IsMatch(previous, "\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant)) throw new ApiProblem(400, "Invalid repair identifier"); }
            await reconstruction.EnsureReadyAsync(context.RequestAborted).ConfigureAwait(false);
            await admin.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                var source = await SelectAsync(package, context.RequestAborted).ConfigureAwait(false); Current(catalog, package, source, revision);
                return Results.Json(await reconstruction.ReconstructAsync(source, revision, previous, context.RequestAborted).ConfigureAwait(false), statusCode: 202);
            }
            finally { admin.Release(); }
        }
        private SourceRawCatalog Catalog() => raws.Catalog ?? throw new ApiProblem(503, raws.StorageError.Length == 0 ? "Source-code storage is not ready" : raws.StorageError);
        private async Task<RawInstalledSource> SelectAsync(string package, CancellationToken token)
        {
            var row = (await installed.InstalledAsync(token).ConfigureAwait(false)).FirstOrDefault(item => SourceOrigin.Optional(item, "pkgName") == package && item["isInstalled"]?.GetValue<bool>() == true);
            if (row is null) throw new ApiProblem(404, "Select an installed source");
            var source = await installed.ObserveAsync(package, Text(row, "versionName"), token).ConfigureAwait(false);
            if (source is null || Text(source.Extension, "pkgName") != package || Text(source.Extension, "versionName") != Text(row, "versionName")) throw new ApiProblem(409, "Installed source changed; refresh before acting");
            RawCodeRules.Hash(source.BinarySha256); return source;
        }
        private static JsonObject Current(SourceRawCatalog catalog, string package, RawInstalledSource source, long revision)
        {
            var row = catalog.Status(package);
            if (row is null || row["revision"]!.GetValue<long>() != revision || Text(row, "version") != Text(source.Extension, "versionName") || SourceOrigin.Optional(row, "binary_sha256").Length != 0 && SourceOrigin.Optional(row, "binary_sha256") != source.BinarySha256)
                throw new ApiProblem(409, "Installed source or raw-code request changed; refresh before acting"); return row;
        }
        private static long Revision(JsonObject body) => body["revision"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && long.TryParse(value.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) && revision > 0 ? revision : throw new ApiProblem(400, "Refresh the raw-code request before acting");
        private static string Package(HttpContext context)
        {
            try { return RawCodeRules.Package(context.Request.RouteValues["package"]?.ToString() ?? ""); }
            catch (InvalidDataException) { throw new ApiProblem(400, "Invalid source package identity"); }
        }
        private static string Text(JsonObject value, string key) => RawCodeRules.Text(value, key);
    }
}
