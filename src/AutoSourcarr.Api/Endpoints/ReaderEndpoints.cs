using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

public static class ReaderEndpoints
{
    private static readonly string[] Reads = ["GET", "HEAD"];

    /// <summary>Concrete SQLite/page routes. Requires the existing reader authorization middleware before routing.</summary>
    public static RouteGroupBuilder MapReaderServing(this IEndpointRouteBuilder endpoints, ReaderRepository repository, ReaderPageService pages, BoundedWorkScheduler io)
    {
        var group = endpoints.MapGroup("/api/v1");
        group.MapMethods("/entries/{entry_id}/chapters", Reads, (Func<HttpContext, Task<IResult>>)(async context =>
        {
            var id = Identity(context, "entry_id");
            var chapters = await io.RunAsync(() => repository.ListChapters(id), context.RequestAborted).ConfigureAwait(false);
            return Json(new() { ["items"] = new JsonArray(chapters.Select(row => (JsonNode)row).ToArray()) });
        }));
        group.MapMethods("/chapters/{chapter_id}", Reads, (Func<HttpContext, Task<IResult>>)(async context =>
        {
            try { return Json(await io.RunAsync(() => repository.ChapterDetails(Identity(context, "chapter_id")), context.RequestAborted).ConfigureAwait(false)); }
            catch (KeyNotFoundException) { throw new ApiProblem(404, "Chapter not found"); }
        }));
        group.MapMethods("/chapters/{chapter_id}/pages", Reads, (Func<HttpContext, Task<IResult>>)(async context =>
        {
            try
            {
                var result = await pages.ResolvePagesAsync(Identity(context, "chapter_id"), context.RequestAborted).ConfigureAwait(false);
                var requestBase = context.Request.Scheme + "://" + context.Request.Host + context.Request.PathBase;
                result["pages"] = new JsonArray(result["pages"]!.AsArray().Select(node =>
                {
                    var value = node!.GetValue<string>();
                    return JsonValue.Create(value.StartsWith("http://", StringComparison.Ordinal) || value.StartsWith("https://", StringComparison.Ordinal) ? value : requestBase.TrimEnd('/') + "/" + value.TrimStart('/'));
                }).ToArray());
                return Json(result);
            }
            catch (KeyNotFoundException) { throw new ApiProblem(404, "Chapter not found"); }
            catch (ChapterPagesBlockedException blocked)
            { context.Response.Headers.RetryAfter = blocked.RetryAfter.ToString(CultureInfo.InvariantCulture); throw new ApiProblem(503, blocked.Message); }
            catch (OperationCanceledException) { throw; }
            catch (SourceWorkDeferredException) { throw; }
            catch (WorkDeferredException) { throw; }
            catch (ApiProblem) { throw; }
            catch (Exception) { throw new ApiProblem(503, "Private source host unavailable while resolving chapter pages"); }
        }));
        group.MapMethods("/pages/{candidate_id}/{page_index}", Reads, (Func<HttpContext, Task<IResult>>)(async context =>
        {
            try
            {
                var candidate = Identity(context, "candidate_id"); var index = Identity(context, "page_index");
                if (index < int.MinValue || index > int.MaxValue) throw new ApiProblem(422, "Invalid page_index integer range");
                return new ImageResult(await pages.OpenImageAsync(candidate, (int)index, context.RequestAborted).ConfigureAwait(false));
            }
            catch (KeyNotFoundException) { throw new ApiProblem(404, "Page not found; reopen the chapter to refresh page URLs"); }
            catch (OperationCanceledException) { throw; }
            catch (SourceWorkDeferredException) { throw; }
            catch (WorkDeferredException) { throw; }
            catch (ApiProblem) { throw; }
            catch (Exception error) { throw new ApiProblem(502, "Source page failed: " + error.Message); }
        }));
        return group;
    }

    /// <summary>Only local Sources/Entry routes. Existing reader authorization must precede routing.</summary>
    public static RouteGroupBuilder MapReaderCatalogueRead(this IEndpointRouteBuilder endpoints, IReaderCatalogueRead catalogue)
    {
        var group = endpoints.MapGroup("/api/v1");
        group.MapMethods("/sources", Reads, (Func<HttpContext, Task<IResult>>)(async context => Json(await catalogue.SourcesAsync(context.RequestAborted).ConfigureAwait(false))));
        group.MapMethods("/entries/{entry_id}", Reads, (Func<HttpContext, Task<IResult>>)(async context =>
        {
            try { return Json(await catalogue.EntryAsync(Identity(context, "entry_id"), context.RequestAborted).ConfigureAwait(false)); }
            catch (KeyNotFoundException) { throw new ApiProblem(404, "Entry not found"); }
        }));
        return group;
    }

    /// <summary>Register once, instead of the read-only helper, and only with real feed/refresh owners.</summary>
    public static RouteGroupBuilder MapReaderCatalogue(this IEndpointRouteBuilder endpoints, IReaderCatalogue catalogue)
    {
        var group = endpoints.MapReaderCatalogueRead(catalogue);
        group.MapMethods("/browse/popular", Reads, (Func<HttpContext, Task<IResult>>)(context => Browse(context, catalogue, "POPULAR")));
        group.MapMethods("/browse/latest", Reads, (Func<HttpContext, Task<IResult>>)(context => Browse(context, catalogue, "LATEST")));
        group.MapMethods("/search", Reads, (Func<HttpContext, Task<IResult>>)(context => Browse(context, catalogue, "SEARCH")));
        group.MapMethods("/entries/{entry_id}/refresh", ["GET", "HEAD", "POST"], (Func<HttpContext, Task<IResult>>)(async context =>
        {
            try { return Json(await catalogue.RefreshAsync(Identity(context, "entry_id"), ApiQuery.Boolean(context.Request, "fetch_details", true), ApiQuery.Boolean(context.Request, "fetch_chapters", true), context.RequestAborted).ConfigureAwait(false)); }
            catch (KeyNotFoundException) { throw new ApiProblem(404, "Entry not found"); }
        }));
        return group;
    }

    public static RouteGroupBuilder MapServingProfiles(this IEndpointRouteBuilder endpoints, ServingProfileRepository profiles, BoundedWorkScheduler io,
        Func<AutoSourcarrSettings> settings, SemaphoreSlim adminLock)
    {
        var group = endpoints.MapGroup("/api/admin/profiles").AddEndpointFilter(new AdminAuthorizationFilter(settings));
        group.MapGet("/{entry_id}", (Func<HttpContext, Task<IResult>>)(async context =>
        {
            try { return Json(await io.RunAsync(() => profiles.Read(Identity(context, "entry_id")), context.RequestAborted).ConfigureAwait(false)); }
            catch (KeyNotFoundException) { throw new ApiProblem(404, "Work no longer exists"); }
        }));
        group.MapPut("/{entry_id}", (Func<HttpContext, Task<IResult>>)(async context =>
        {
            var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
            if (body["profile"] is not JsonObject profile || body["revision"] is not JsonValue revisionNode || !revisionNode.TryGetValue<long>(out var revision)) throw new ApiProblem(422, "Expected profile object and revision integer");
            await adminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try { return Json(await io.RunAsync(() => profiles.Save(Identity(context, "entry_id"), profile, revision), context.RequestAborted).ConfigureAwait(false)); }
            catch (ArgumentException error) { throw new ApiProblem(409, error.Message); }
            catch (KeyNotFoundException) { throw new ApiProblem(404, "Work no longer exists"); }
            finally { adminLock.Release(); }
        }));
        return group;
    }

    private static async Task<IResult> Browse(HttpContext context, IReaderCatalogue catalogue, string mode)
    {
        var request = context.Request; var sort = request.Query.TryGetValue("sort", out var sortValues) ? sortValues.LastOrDefault() : "relevance";
        if (sort is not ("relevance" or "sources" or "title")) throw new ApiProblem(422, "Invalid sort");
        var specification = new ReaderBrowseRequest(mode, ApiQuery.Integer(request, "page", 1, 1, int.MaxValue), request.Query["q"].LastOrDefault() ?? "",
            request.Query.TryGetValue("sources", out var sources) ? sources.Select(item => item ?? "").ToArray() : null,
            ApiQuery.Integer(request, "min_matches", 1, 0, int.MaxValue), request.Query.ContainsKey("match_count") ? ApiQuery.Integer(request, "match_count", 0, 0, int.MaxValue) : null, sort);
        try { return Json(await catalogue.BrowseAsync(specification, context.RequestAborted).ConfigureAwait(false)); }
        catch (ReaderBrowsePendingException)
        {
            context.Response.Headers.RetryAfter = "3"; context.Response.Headers.CacheControl = "no-store"; context.Response.Headers["X-AutoSourcarr-Reader-State"] = "preparing";
            return Json(new() { ["detail"] = "The AutoSourcarr catalog is preparing this page. Retry shortly.", ["preparing"] = true }, 503);
        }
    }
    private static long Identity(HttpContext context, string name) => long.TryParse(context.Request.RouteValues[name]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new ApiProblem(422, "Invalid " + name + " integer");
    private static IResult Json(JsonObject value, int status = 200) => new ReaderJsonResult(value, status);
    private sealed class ReaderJsonResult(JsonObject value, int status) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => ApiJson.WriteAsync(httpContext, status, value);
    }
    private sealed class ImageResult(ReaderImageStream image) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            Exception? operationFailure = null, cleanupFailure = null;
            try
            {
                await image.ValidatePublicationAsync(context.RequestAborted).ConfigureAwait(false);
                context.Response.ContentType = image.ContentType; context.Response.ContentLength = image.ContentLength;
                context.Response.Headers.CacheControl = "private, max-age=3600"; context.Response.Headers.XContentTypeOptions = "nosniff";
                if (!HttpMethods.IsHead(context.Request.Method)) await image.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception error) { operationFailure = error; }
            try { await image.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanupFailure = error; }

            // Select against the final state, after the actual close joins. A caller
            // abort arriving during close must not become a new local503 response.
            var responseFailure = operationFailure;
            if (!context.Response.HasStarted)
            {
                if (operationFailure is KeyNotFoundException) responseFailure = new ApiProblem(404, "Page not found; reopen the chapter to refresh page URLs");
                else if (operationFailure is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
                    responseFailure = new SourceEngineUnavailableException("Chapter page streaming is unavailable during shutdown or reset; retry shortly");
                if (operationFailure is not null || cleanupFailure is not null)
                {
                    context.Response.ContentType = null; context.Response.ContentLength = null;
                    context.Response.Headers.CacheControl = "no-store";
                }
            }
            if (cleanupFailure is not null) throw new ApiCleanupFailure(operationFailure, cleanupFailure, responseFailure);
            if (responseFailure is not null) ExceptionDispatchInfo.Capture(responseFailure).Throw();
        }
    }
}
