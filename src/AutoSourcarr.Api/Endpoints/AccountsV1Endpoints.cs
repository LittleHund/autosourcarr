using System.Text.Json.Nodes;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>
/// Phase 5 accounts: sign-in (status, first-boot owner, login, logout, me, password), Keys page (automatic admin
/// token, permanent admin key, reader and script keys), accounts (owner only) and the security log.
/// Who may call what is decided by AccessPolicy; handlers check owner-only actions again.
/// </summary>
internal static class AccountsV1Endpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, RouteGroupBuilder admin, ApiV1Services services, ApiJsonContext json)
    {
        // Sign-in routes are public (or any signed-in role): not behind the admin filter.
        var auth = endpoints.MapGroup("/api/v1/auth").WithMetadata(ApiContractMetadata.Instance);

        auth.MapGet("/status", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services.Access is not { } access) return Unavailable();
                var caller = AccessControlMiddleware.CallerOf(context);
                var method = services.Settings().AuthMethod;
                return Results.Json(new JsonObject
                {
                    ["method"] = method,
                    ["setup_required"] = access.Accounts.SetupRequired && method != "external",
                    ["setup_code_required"] = access.Accounts.SetupCodeRequired,
                    ["signed_in"] = caller.Role != Role.None,
                    ["user"] = caller.Name, ["role"] = Roles.Name(caller.Role)
                }, json.JsonObject);
            }))
            .WithName("getAuthStatus")
            .WithSummary("Sign-in method, whether the owner account still has to be created, and who is signed in.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        auth.MapPost("/setup", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Access is not { } access) return Unavailable();
                var body = await Body(context).ConfigureAwait(false);
                if (services.Settings().AuthMethod == "external")
                    return Problem(409, "With external sign-in the owner is the account named in AUTH_OWNER_EXTERNAL_USER.");
                Account owner;
                try { owner = access.Accounts.CreateOwner(Text(body["username"]), Text(body["password"]), Text(body["setup_code"])); }
                catch (AccountException error) { return Problem(error.Message.Contains("already", StringComparison.Ordinal) ? 409 : 400, error.Message); }
                access.Log.Record("owner-created", owner.Name, AccessControlMiddleware.Address(context));
                AccessControlMiddleware.IssueCookie(context, access.Accounts.IssueSession(owner.Id));
                return Results.Json(new JsonObject { ["user"] = owner.Name, ["role"] = "owner" }, json.JsonObject, statusCode: 201);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("createOwner")
            .WithSummary("First boot: create the owner account (the first admin) and sign in. Refused once an owner exists.")
            .Produces<JsonObject>(StatusCodes.Status201Created)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        auth.MapPost("/login", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Access is not { } access) return Unavailable();
                if (services.Settings().AuthMethod != "forms") return Problem(409, "This instance does not use the login page.");
                var body = await Body(context).ConfigureAwait(false);
                var name = Text(body["username"]);
                var account = await AccessControlMiddleware.SignInAsync(access, context, name, Text(body["password"])).ConfigureAwait(false);
                if (account is null) return Problem(401, "Wrong name or password, or too many attempts; wait and try again.");
                access.Log.Record("login", account.Name, AccessControlMiddleware.Address(context), "forms");
                AccessControlMiddleware.IssueCookie(context, access.Accounts.IssueSession(account.Id));
                return Results.Json(new JsonObject { ["user"] = account.Name, ["role"] = Roles.Name(account.Role) }, json.JsonObject);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("login")
            .WithSummary("Sign in with name and password (login page). Failed attempts slow down and are logged.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status401Unauthorized);

        auth.MapPost("/logout", (Func<HttpContext, IResult>)(context =>
            {
                var caller = AccessControlMiddleware.CallerOf(context);
                if (services.Access is { } access && caller.Role != Role.None)
                {
                    // Sessions are signed tokens: signing out ends every session of the account (a copied cookie stops working).
                    if (caller.Via == "session" && caller.KeyId is not null) access.Accounts.EndSessions(caller.KeyId);
                    access.Log.Record("logout", caller.Name, AccessControlMiddleware.Address(context));
                }
                AccessControlMiddleware.ClearCookie(context);
                return Results.Json(new JsonObject { ["signed_in"] = false }, json.JsonObject);
            }))
            .WithName("logout")
            .WithSummary("Sign out. Ends every session of the account, on all devices.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        auth.MapGet("/me", (Func<HttpContext, IResult>)(context =>
            {
                var caller = AccessControlMiddleware.CallerOf(context);
                return Results.Json(new JsonObject { ["user"] = caller.Name, ["role"] = Roles.Name(caller.Role), ["via"] = caller.Via }, json.JsonObject);
            }))
            .WithName("getMe")
            .WithSummary("Who is calling: name, role and how they signed in.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        auth.MapPost("/password", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Access is not { } access) return Unavailable();
                var caller = AccessControlMiddleware.CallerOf(context);
                if (caller.Via is not ("session" or "basic") || caller.KeyId is null) return Problem(409, "Only accounts with a password can change it here.");
                var body = await Body(context).ConfigureAwait(false);
                var address = AccessControlMiddleware.Address(context);
                var wait = access.Throttle.Before(address, caller.Name);
                if (wait is null) return Problem(429, "Too many attempts; wait 15 minutes.");
                if (wait > TimeSpan.Zero) await Task.Delay(wait.Value, context.RequestAborted).ConfigureAwait(false);
                try { access.Accounts.ChangePassword(caller.KeyId, Text(body["current"]), Text(body["password"])); }
                catch (AccountException error)
                {
                    access.Log.Record("login-failed", caller.Name, address, "password change");
                    access.Throttle.Failed();
                    return Problem(400, error.Message);
                }
                access.Throttle.Succeeded(address, caller.Name);
                access.Log.Record("password-changed", caller.Name, AccessControlMiddleware.Address(context));
                AccessControlMiddleware.IssueCookie(context, access.Accounts.IssueSession(caller.KeyId));
                return Results.Json(new JsonObject { ["changed"] = true }, json.JsonObject);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("changePassword")
            .WithSummary("Change your own password; other sessions of the account end.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest);

        // Keys page (admin; regenerating the automatic token is owner-only).
        admin.MapGet("/keys", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services.Access is not { } access) return Unavailable();
                return Results.Json(new JsonObject
                {
                    ["admin_token"] = access.Accounts.AutomaticAdminToken,
                    ["configured_admin_token"] = services.Settings().AdminToken.Trim().Length > 0,
                    ["reader_token_configured"] = services.Settings().AutosourcarrReaderApiToken.Trim().Length > 0,
                    ["keys"] = new JsonArray(access.Accounts.Keys().Select(KeyJson).ToArray())
                }, json.JsonObject);
            }))
            .WithName("getKeys")
            .WithSummary("Keys page: the automatic admin token, the permanent admin key, reader keys (one per device) and script keys.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        admin.MapPost("/keys", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Access is not { } access) return Unavailable();
                var body = await Body(context).ConfigureAwait(false);
                try
                {
                    var (key, secret) = access.Accounts.CreateKey(Text(body["name"]), Text(body["kind"]), Roles.Parse(Text(body["role"])));
                    access.Log.Record("key-created", AccessControlMiddleware.CallerOf(context).Name, AccessControlMiddleware.Address(context), key.Kind + " key " + key.Name);
                    var result = KeyJson(key).AsObject();
                    result["secret"] = secret;
                    return Results.Json(result, json.JsonObject, statusCode: 201);
                }
                catch (AccountException error) { return Problem(error.Message.Contains("already", StringComparison.Ordinal) ? 409 : 400, error.Message); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("createKey")
            .WithSummary("Create a key (kind admin, reader or script; scripts are admin or guest). The secret is shown only in this answer.")
            .Produces<JsonObject>(StatusCodes.Status201Created)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest);

        admin.MapDelete("/keys/{keyId}", (Func<HttpContext, IResult>)(context =>
            {
                if (services.Access is not { } access) return Unavailable();
                var id = context.Request.RouteValues["keyId"]?.ToString() ?? "";
                if (!access.Accounts.RevokeKey(id)) return Problem(404, "No such live key.");
                access.Log.Record("key-revoked", AccessControlMiddleware.CallerOf(context).Name, AccessControlMiddleware.Address(context), id);
                return Results.Json(new JsonObject { ["revoked"] = id }, json.JsonObject);
            }))
            .WithName("revokeKey")
            .WithSummary("Revoke a key at once (a lost phone is cut off without touching other devices).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        admin.MapPost("/keys/admin-token/regenerate", (Func<HttpContext, IResult>)(context =>
            {
                if (services.Access is not { } access) return Unavailable();
                if (AccessControlMiddleware.CallerOf(context).Role != Role.Owner) return Problem(403, "Only the owner can regenerate the admin token.");
                var token = access.Accounts.RegenerateAdminToken();
                access.RegisterSecret?.Invoke(token);
                access.Log.Record("admin-token-regenerated", AccessControlMiddleware.CallerOf(context).Name, AccessControlMiddleware.Address(context));
                return Results.Json(new JsonObject { ["admin_token"] = token }, json.JsonObject);
            }))
            .WithName("regenerateAdminToken")
            .WithSummary("Owner only: replace the automatic admin token; the old one stops working at once.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status403Forbidden);

        // Accounts (owner only).
        admin.MapGet("/accounts", (Func<HttpContext, IResult>)(context =>
            {
                if (services.Access is not { } access) return Unavailable();
                if (AccessControlMiddleware.CallerOf(context).Role != Role.Owner) return Problem(403, "Only the owner manages accounts.");
                return Results.Json(new JsonObject { ["items"] = new JsonArray(access.Accounts.Accounts().Select(AccountJson).ToArray()) }, json.JsonObject);
            }))
            .WithName("getAccounts")
            .WithSummary("Owner only: accounts with role and sign-in source (no password data).")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        admin.MapPost("/accounts", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Access is not { } access) return Unavailable();
                var caller = AccessControlMiddleware.CallerOf(context);
                if (caller.Role != Role.Owner) return Problem(403, "Only the owner manages accounts.");
                var body = await Body(context).ConfigureAwait(false);
                try
                {
                    var account = access.Accounts.CreateUser(Text(body["username"]), Text(body["password"]), Roles.Parse(Text(body["role"])));
                    access.Log.Record("user-created", caller.Name, AccessControlMiddleware.Address(context), account.Name + " (" + Roles.Name(account.Role) + ")");
                    return Results.Json(AccountJson(account).AsObject(), json.JsonObject, statusCode: 201);
                }
                catch (AccountException error) { return Problem(400, error.Message); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("createAccount")
            .WithSummary("Owner only: add an admin or guest account with a password.")
            .Produces<JsonObject>(StatusCodes.Status201Created)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest);

        admin.MapDelete("/accounts/{accountId}", (Func<HttpContext, IResult>)(context =>
            {
                if (services.Access is not { } access) return Unavailable();
                var caller = AccessControlMiddleware.CallerOf(context);
                if (caller.Role != Role.Owner) return Problem(403, "Only the owner manages accounts.");
                var id = context.Request.RouteValues["accountId"]?.ToString() ?? "";
                try
                {
                    if (!access.Accounts.DeleteUser(id)) return Problem(404, "No such account.");
                    access.Accounts.EndSessions(id);
                }
                catch (AccountException error) { return Problem(409, error.Message); }
                access.Log.Record("user-deleted", caller.Name, AccessControlMiddleware.Address(context), id);
                return Results.Json(new JsonObject { ["deleted"] = id }, json.JsonObject);
            }))
            .WithName("deleteAccount")
            .WithSummary("Owner only: remove an account (not the owner); its sessions end.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        admin.MapGet("/security/events", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services.Access is not { } access) return Unavailable();
                var kind = context.Request.Query["kind"].ToString();
                var limit = int.TryParse(context.Request.Query["limit"].ToString(), out var parsed) ? parsed : 200;
                return Results.Json(access.Log.Snapshot(kind.Length == 0 ? null : kind, limit), json.JsonObject);
            }))
            .WithName("getSecurityEvents")
            .WithSummary("Security log: sign-ins, failed sign-ins, slow-downs, key, token and account events, newest first; filter with ?kind=.")
            .Produces<JsonObject>(StatusCodes.Status200OK);
    }

    private static JsonNode KeyJson(ApiKey key) => new JsonObject
    {
        ["id"] = key.Id, ["name"] = key.Name, ["kind"] = key.Kind, ["role"] = Roles.Name(key.Role), ["prefix"] = key.Prefix,
        ["created"] = key.Created, ["last_used"] = key.LastUsed, ["revoked"] = key.Revoked
    };

    private static JsonNode AccountJson(Account account) => new JsonObject
    {
        ["id"] = account.Id, ["name"] = account.Name, ["role"] = Roles.Name(account.Role), ["source"] = account.Source,
        ["created"] = account.Created, ["has_password"] = account.HasPassword
    };

    private static async Task<JsonObject> Body(HttpContext context)
    {
        try { return await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { return new JsonObject(); }
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static IResult Unavailable() => Problem(503, "Accounts are not available in this host.");
    private static IResult Problem(int status, string detail) =>
        Results.Json(new ProblemResponse(detail), ApiJsonContext.Default.ProblemResponse, statusCode: status);
}
