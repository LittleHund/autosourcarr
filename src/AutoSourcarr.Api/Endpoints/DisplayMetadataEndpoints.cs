using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Catalogue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

public static class DisplayMetadataEndpoints
{
    public static RouteGroupBuilder MapDisplayMetadata(this IEndpointRouteBuilder endpoints, DisplayMetadata metadata,
        BoundedWorkScheduler io, Func<AutoSourcarrSettings> settings)
    {
        var group = endpoints.MapGroup("/api/admin/metadata-fields").AddEndpointFilter(new AdminAuthorizationFilter(settings));
        group.MapGet("/{entry_id}", (Func<HttpContext, Task<IResult>>)(async context =>
        {
            var entryId = EntryId(context);
            try { return Results.Json(await io.RunAsync(() => metadata.Read(entryId), context.RequestAborted).ConfigureAwait(false)); }
            catch (KeyNotFoundException) { throw new ApiProblem(404, "Entry not found"); }
        }));
        group.MapPut("/{entry_id}", (Func<HttpContext, Task<IResult>>)(async context =>
        {
            var entryId = EntryId(context);
            // Four fields may each contain twelve thousand Unicode scalars, including JSON escapes.
            var body = await ApiJson.ReadObjectAsync(context.Request, 1024 * 1024).ConfigureAwait(false);
            if (body.Any(pair => pair.Key is not ("revision" or "fields" or "restore_id"))) throw new ApiProblem(422, "Unknown metadata request field");
            var revision = Integer(body["revision"], "revision", 0);
            var fields = body.TryGetPropertyValue("fields", out var supplied) ? supplied as JsonObject ?? throw new ApiProblem(422, "fields must be an object") : new JsonObject();
            long? restore = body["restore_id"] is null ? null : Integer(body["restore_id"], "restore_id", 1);
            try { return Results.Json(await io.RunAsync(() => metadata.Save(entryId, fields, revision, restore), context.RequestAborted).ConfigureAwait(false)); }
            catch (KeyNotFoundException) { throw new ApiProblem(404, "Entry not found"); }
            catch (ArgumentException error) { throw new ApiProblem(409, error.Message); }
        }));
        return group;
    }

    private static long Integer(JsonNode? node, string field, long minimum)
    {
        if (node is not JsonValue scalar || scalar.GetValueKind() != JsonValueKind.Number || !scalar.TryGetValue<long>(out var value) || value < minimum)
            throw new ApiProblem(422, "Invalid " + field + " integer");
        return value;
    }
    private static long EntryId(HttpContext context) => long.TryParse(Convert.ToString(context.Request.RouteValues["entry_id"], CultureInfo.InvariantCulture),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : throw new ApiProblem(422, "Invalid entry_id integer");
}
