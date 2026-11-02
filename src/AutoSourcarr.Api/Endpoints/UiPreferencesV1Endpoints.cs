using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Accounts;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Unicode;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the UI preference routes need from the running app (null answers 503).</summary>
public sealed class UiPreferencesV1Services
{
    public required SettingsRepository Preferences { get; init; }
    public required BoundedWorkScheduler Io { get; init; }
}

/// <summary>
/// Phase 11 (port-map UiPreferencesEndpoints): the per-user UI preferences of 0.4.x <c>/api/admin/ui-preferences</c>
/// under /api/v1. 0.4.x keyed them by the proxy identity (Remote-User, else "local:default"); 0.5.0 keys them by the
/// signed-in account, so every account (guests too) keeps its own layout. Guests may read and change only their own
/// preferences; reader devices and scripts have no UI and are refused. Proxy headers never grant access here: they only
/// name the profile when no account is signed in (admin token, admin key), exactly as in 0.4.x.
/// </summary>
internal static class UiPreferencesV1Endpoints
{
    /// <summary>The 0.4.x profile everyone shared without a proxy identity; the owner starts from it once.</summary>
    internal const string LegacyLocalKey = "local:default";

    internal static void Map(RouteGroupBuilder group, ApiV1Services api, ApiJsonContext json, UiPreferencesV1Services? services)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "UI preferences are not available in this host.");

        group.MapGet("/ui-preferences", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services is null) return Unavailable();
                if (Resolve(context, api.Settings()) is not { } profile) return Problem(403, "Reader and script keys have no UI preferences.");
                var values = await services.Io.RunAsync(() =>
                {
                    var own = services.Preferences.UserPreferences(profile.Key);
                    // The owner inherits the single 0.4.x profile until they save their own.
                    return own.Count == 0 && profile.InheritsLegacy ? services.Preferences.UserPreferences(LegacyLocalKey) : own;
                }, context.RequestAborted).ConfigureAwait(false);
                return Results.Json(profile.Response(values), json.JsonObject);
            }))
            .WithName("getUiPreferences")
            .WithSummary("Your UI preferences (layout, sorting, columns, theme) and whose profile they are: the signed-in account, else the proxy identity.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status403Forbidden)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapPut("/ui-preferences", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services is null) return Unavailable();
                if (Resolve(context, api.Settings()) is not { } profile) return Problem(403, "Reader and script keys have no UI preferences.");
                JsonObject body;
                try { body = await ApiJson.ReadObjectAsync(context.Request, 512 * 1024).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (body.ContainsKey("values") && body["values"] is not JsonObject) return Problem(422, "values must be an object");
                var incoming = body["values"] as JsonObject ?? new JsonObject();
                JsonObject values;
                try
                {
                    values = await services.Io.RunAsync(() =>
                    {
                        // First save of an inheriting owner: keep the 0.4.x values that this save does not change.
                        if (profile.InheritsLegacy && services.Preferences.UserPreferences(profile.Key).Count == 0)
                        {
                            var legacy = services.Preferences.UserPreferences(LegacyLocalKey);
                            if (legacy.Count > 0) services.Preferences.SetUserPreferences(profile.Key, legacy, merge: false);
                        }
                        return services.Preferences.SetUserPreferences(profile.Key, incoming);
                    }, context.RequestAborted).ConfigureAwait(false);
                }
                catch (ArgumentException error) { return Problem(400, error.Message); }
                return Results.Json(profile.Response(values), json.JsonObject);
            }))
            .WithName("saveUiPreferences")
            .WithSummary("Merge changed UI preferences into your profile (body: values {name: value}); at most 64 KiB per profile.")
            .Accepts<JsonObject>("application/json")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status403Forbidden)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>The preference profile of this request; null for callers without a UI (reader devices, scripts).</summary>
    internal static Profile? Resolve(HttpContext context, Core.Settings.AutoSourcarrSettings settings)
    {
        var caller = AccessControlMiddleware.CallerOf(context);
        if (caller.Role is Role.Reader or Role.Script) return null;
        var identity = UiIdentity.Resolve(context.Request, settings);
        return caller switch
        {
            // External sign-in: the same key 0.4.x used for this Remote-User, so their saved layout carries over.
            { Via: "external", Role: not Role.None } => new Profile("authelia:" + UnicodeCaseFold.Fold(caller.Name), caller.Name, true, false, identity),
            // A signed-in account (forms or Basic): its own profile; the owner starts from the shared 0.4.x one.
            { KeyId: { Length: > 0 } id, Via: "session" or "basic" } =>
                new Profile("account:" + id, caller.Name, false, caller.Role == Role.Owner, identity),
            // Admin token or admin key (no account): the 0.4.x rule, proxy identity else the shared local profile.
            _ => new Profile(identity.Key, identity.User, identity.Authelia, false, identity),
        };
    }

    internal sealed record Profile(string Key, string User, bool Authelia, bool InheritsLegacy, UiIdentity Identity)
    {
        public JsonObject Response(JsonObject values)
        {
            var response = Identity.PreferenceResponse(values);
            response["user"] = User;
            response["authelia"] = Authelia;
            return response;
        }
    }
}
