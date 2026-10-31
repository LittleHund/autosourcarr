using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

public static class UiPreferencesEndpoints
{
    public static RouteGroupBuilder MapUiPreferences(this IEndpointRouteBuilder endpoints, SettingsRepository preferences,
        BoundedWorkScheduler io, Func<AutoSourcarrSettings> settings)
    {
        var group = endpoints.MapGroup("/api/admin/ui-preferences").AddEndpointFilter(new AdminAuthorizationFilter(settings));
        group.MapGet("", (Func<HttpContext, Task<IResult>>)(async context =>
        {
            var identity = UiIdentity.Resolve(context.Request, settings());
            var values = await io.RunAsync(() => preferences.UserPreferences(identity.Key), context.RequestAborted).ConfigureAwait(false);
            return Results.Json(identity.PreferenceResponse(values));
        }));
        group.MapPut("", (Func<HttpContext, Task<IResult>>)(async context =>
        {
            var identity = UiIdentity.Resolve(context.Request, settings());
            var body = await ApiJson.ReadObjectAsync(context.Request, 512 * 1024).ConfigureAwait(false);
            var incoming = !body.ContainsKey("values") ? new JsonObject() : body["values"] as JsonObject ?? throw new ApiProblem(422, "values must be an object");
            JsonObject values;
            try { values = await io.RunAsync(() => preferences.SetUserPreferences(identity.Key, incoming), context.RequestAborted).ConfigureAwait(false); }
            catch (ArgumentException error) { throw new ApiProblem(400, error.Message); }
            return Results.Json(identity.PreferenceResponse(values));
        }));
        return group;
    }
}
