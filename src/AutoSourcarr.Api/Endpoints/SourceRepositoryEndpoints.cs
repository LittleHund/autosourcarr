using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>Existing Web UI repository wires, backed by the instance's actual canonical writer and public preview owner.</summary>
public static class SourceRepositoryEndpoints
{
    public static RouteGroupBuilder MapSourceRepositories(this IEndpointRouteBuilder endpoints,
        SourceRepositoryStore repositories, SourceRepositoryPreview previews, BoundedWorkScheduler io, Func<AutoSourcarrSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(repositories); ArgumentNullException.ThrowIfNull(previews);
        ArgumentNullException.ThrowIfNull(io); ArgumentNullException.ThrowIfNull(settings);
        var group = endpoints.MapGroup("/api/admin/source-repositories").AddEndpointFilter(new AdminAuthorizationFilter(settings));
        group.MapGet("", (Func<HttpContext, Task<IResult>>)(context => ExecuteAsync(context, async () =>
        {
            var snapshot = await io.RunAsync(repositories.Snapshot, context.RequestAborted).ConfigureAwait(false);
            snapshot["can_restart"] = settings().SourceEngineMode == "integrated";
            return snapshot;
        })));
        group.MapPost("/preview", (Func<HttpContext, Task<IResult>>)(context => ExecuteAsync(context, async () =>
        {
            var request = await ApiJson.ReadObjectAsync(context.Request, 64 * 1024).ConfigureAwait(false);
            // Useful validation messages originate only in our own URL rules;
            // an arbitrary exception from the transport is never echoed.
            var url = SourceRepositoryStore.NormalizeUrl(Field(request, "url", 1, 2048));
            try { return await previews.PreviewAsync(url, cancellationToken: context.RequestAborted).ConfigureAwait(false); }
            catch (SourceRepositoryPreviewBusyException error) { throw new ApiProblem(400, error.Message); }
            catch (OperationCanceledException) { throw; }
            // Arbitrary upstream/decoder messages can contain request/response data.
            // They are not configuration errors and must not enter global logs.
            catch (Exception) { throw new ApiProblem(400, "Repository preview could not be completed. Check the HTTPS manifest URL and retry."); }
        })));
        group.MapPost("", (Func<HttpContext, Task<IResult>>)(context => ChangeAsync(context, repositories, io, remove: false)));
        group.MapDelete("", (Func<HttpContext, Task<IResult>>)(context => ChangeAsync(context, repositories, io, remove: true)));
        return group;
    }

    private static Task<IResult> ChangeAsync(HttpContext context, SourceRepositoryStore repositories, BoundedWorkScheduler io, bool remove) =>
        ExecuteAsync(context, async () =>
        {
            var body = await ApiJson.ReadObjectAsync(context.Request, 64 * 1024).ConfigureAwait(false);
            var url = Field(body, "url", 1, 2048); var revision = Field(body, "revision", 64, 64);
            // The canonical writer owns CAS and physical publication. A disconnected
            // request cannot cancel only its await and abandon an admitted file write.
            return await io.RunAsync(() => repositories.Change(url, revision, remove), context.RequestAborted).ConfigureAwait(false);
        });

    private static async Task<IResult> ExecuteAsync(HttpContext context, Func<Task<JsonObject>> operation)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { return Results.Json(await operation().ConfigureAwait(false)); }
        catch (SourceRepositoryConflictException error) { throw new ApiProblem(409, error.Message); }
        catch (ArgumentException error) { throw new ApiProblem(400, error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new ApiProblem(409, "Repository configuration is unavailable. Check its retained source-host settings file and refresh."); }
        catch (OperationCanceledException) { context.Abort(); throw; }
    }

    private static string Field(JsonObject body, string key, int minimum, int maximum)
    {
        if (body[key] is not JsonValue scalar || !scalar.TryGetValue<string>(out var value))
            throw new ApiProblem(422, "Invalid " + key + " string length");
        var count = value.EnumerateRunes().Count();
        if (count < minimum || count > maximum)
            throw new ApiProblem(422, "Invalid " + key + " string length");
        return value;
    }
}
