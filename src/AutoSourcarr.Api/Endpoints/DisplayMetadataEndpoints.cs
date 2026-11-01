using System.Globalization;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Catalogue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>
/// The 0.4.5 read of an entry's display ownership and its retained history. Phase 9 made /api/v1/entries/{id}/metadata
/// (MetadataChooser) the only write path; the 0.4.5 PUT /api/admin/metadata-fields/{id} was removed.
/// </summary>
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
        return group;
    }

    private static long EntryId(HttpContext context) => long.TryParse(Convert.ToString(context.Request.RouteValues["entry_id"], CultureInfo.InvariantCulture),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : throw new ApiProblem(422, "Invalid entry_id integer");
}
