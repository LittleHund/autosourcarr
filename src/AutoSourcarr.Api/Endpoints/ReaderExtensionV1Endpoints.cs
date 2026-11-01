using System.Text.Json.Nodes;
using AutoSourcarr.Core.Reader.Extension;
using AutoSourcarr.Core.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the reader extension routes need from the running app (null answers 503).</summary>
public sealed class ReaderExtensionServices
{
    /// <summary>The prebuilt reader extension and its repository files (READ-1).</summary>
    public required ReaderExtensionRepository Repository { get; init; }
    /// <summary>APK/JAR downloads of installed sources (READ-2); null without the source host.</summary>
    public InstalledExtensionDownloads? Downloads { get; init; }
}

/// <summary>
/// Phase 10 reader extension and its distribution:
/// <list type="bullet">
/// <item>GET/HEAD /api/reader/repo/{index.min.json | repo.json | apk/* | icon/* | jar/*}: AutoSourcarr's own extension
/// repository. Public (readers add a repository by URL, without headers); it holds no address, key or catalogue data.
/// Needs AccessPolicy to treat <see cref="ReaderExtensionRepository.RoutePrefix"/>/ as public.</item>
/// <item>GET /api/v1/reader-extension/setup (admin): repository link, Mihon/Tachiyomi add-repository links and the
/// address per server slot for the System page QR codes (READ-5).</item>
/// <item>GET /api/v1/source-extensions/{pkgName}/jar and /apk (admin): downloads for the Installed tab (READ-2).</item>
/// </list>
/// </summary>
public static class ReaderExtensionV1Endpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, ReaderExtensionServices? reader)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "The reader extension is not available in this host.");
        string Package(HttpContext context) => context.Request.RouteValues["pkgName"]?.ToString() ?? "";

        endpoints.MapMethods(ReaderExtensionRepository.RoutePrefix + "/{**path}", ["GET", "HEAD"], (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-cache";
                if (reader is null) return Unavailable();
                var file = reader.Repository.Read(context.Request.RouteValues["path"]?.ToString() ?? "");
                return file is null ? Problem(404, "Not part of the reader extension repository.")
                    : Results.File(file.Body, file.ContentType, file.ContentType == "application/json" || file.ContentType == "image/png" ? null : file.FileName, enableRangeProcessing: false);
            }))
            .ExcludeFromDescription();

        group.MapGet("/reader-extension/setup", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (reader is null) return Unavailable();
                return Results.Json(ReaderSetup.Build(services.Settings().AutosourcarrPublicUrl, reader.Repository.Status()), json.JsonObject);
            }))
            .WithName("getReaderExtensionSetup")
            .WithSummary("Reader setup for the System page: the reader extension repository URL, the Mihon and Tachiyomi add-repository links, the reader address and the source of each server slot, and the served build (version, slots, signing). Keys are never included.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        async Task<IResult> Download(HttpContext context, Func<InstalledExtensionDownloads, string, CancellationToken, Task<ReaderRepositoryFile>> read)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (reader?.Downloads is not { } downloads) return Unavailable();
            try
            {
                var file = await read(downloads, Package(context), context.RequestAborted).ConfigureAwait(false);
                return Results.File(file.Body, file.ContentType, file.FileName, enableRangeProcessing: false);
            }
            catch (ExtensionDownloadException error) { return Problem(error.StatusCode, error.Message); }
            catch (SourceEngineUnavailableException error) { return Problem(503, error.Message); }
            catch (SuwayomiException error) { return Problem(502, error.Message); }
        }

        group.MapGet("/source-extensions/{pkgName}/jar", (Func<HttpContext, Task<IResult>>)(context =>
                Download(context, (downloads, package, token) => downloads.JarAsync(package, token))))
            .WithName("downloadSourceExtensionJar")
            .WithSummary("Download the JAR the source host runs for an installed source right now (never a cached copy).")
            .Produces(StatusCodes.Status200OK, contentType: "application/java-archive")
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/source-extensions/{pkgName}/apk", (Func<HttpContext, Task<IResult>>)(context =>
                Download(context, (downloads, package, token) => downloads.ApkAsync(package, token))))
            .WithName("downloadSourceExtensionApk")
            .WithSummary("Download an installed source's APK: its extension store index is read again and the package's current APK is downloaded and checked at request time (AutoSourcarr's own reader extension comes from its local repository).")
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.android.package-archive")
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status410Gone)
            .Produces<ProblemResponse>(StatusCodes.Status502BadGateway)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);
    }
}
