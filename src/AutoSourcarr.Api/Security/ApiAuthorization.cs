using System.Security.Cryptography;
using System.Text;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Http;

namespace AutoSourcarr.Api.Security;

public static class ApiAuthorization
{
    public const string ReaderPrefix = "/api/reader/v1";
    public const string InternalPrefix = "/api/v1";
    /// <summary>HttpContext.Items key set when a request arrived through the reader alias.</summary>
    public const string ReaderRequestItem = "AutoSourcarr.ReaderRequest";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static bool IsReaderPath(string path) => path == ReaderPrefix || path.StartsWith(ReaderPrefix + "/", StringComparison.Ordinal);
    public static bool AdminAllowed(HttpRequest request, AutoSourcarrSettings settings)
    {
        var expected = settings.AdminToken.Trim();
        if (expected.Length == 0) return true;
        var token = request.Headers["X-Admin-Token"].ToString();
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)) token = authorization[7..];
        return Matches(token, expected);
    }
    public static bool ReaderAllowed(HttpRequest request, AutoSourcarrSettings settings)
    {
        var supplied = request.Headers["X-AutoSourcarr-Api-Key"].ToString().Trim();
        if (supplied.Length == 0)
        {
            var authorization = request.Headers.Authorization.ToString().Trim();
            if (authorization.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)) supplied = authorization[7..].Trim();
        }
        return supplied.Length > 0 && Matches(supplied, settings.AutosourcarrReaderApiToken.Trim());
    }
    private static bool Matches(string actual, string expected)
    {
        try { return CryptographicOperations.FixedTimeEquals(StrictUtf8.GetBytes(actual), StrictUtf8.GetBytes(expected)); }
        catch (EncoderFallbackException) { return false; }
    }
}

public sealed class AdminAuthorizationFilter(Func<AutoSourcarrSettings> settings) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // The reader alias (/api/reader/v1, bypassed by Authelia) maps onto /api/v1. Admin endpoints
        // under /api/v1 must never answer through it, even when no admin token is configured.
        if (context.HttpContext.Items.ContainsKey(ApiAuthorization.ReaderRequestItem))
            return ValueTask.FromResult<object?>(Results.Json(new { detail = "Not found" }, statusCode: 404));
        // With accounts (the app), the access check already decided who is calling: admin reach is required here.
        if (context.HttpContext.Items.TryGetValue(AccessControlMiddleware.CallerItem, out var value) && value is AutoSourcarr.Core.Accounts.Caller caller)
            return AccessPolicy.Allows(caller, AccessPolicy.Required(context.HttpContext.Request.Method, context.HttpContext.Request.Path.Value ?? ""))
                && (caller.AdminReach || AccessPolicy.Required(context.HttpContext.Request.Method, context.HttpContext.Request.Path.Value ?? "") != AccessLevel.Admin) ? next(context)
                : ValueTask.FromResult<object?>(Results.Json(new { detail = "Administrator rights needed" }, statusCode: caller.Role == AutoSourcarr.Core.Accounts.Role.None ? 401 : 403));
        return ApiAuthorization.AdminAllowed(context.HttpContext.Request, settings()) ? next(context)
            : ValueTask.FromResult<object?>(Results.Json(new { detail = "Invalid admin token" }, statusCode: 401));
    }
}

public sealed class ReaderAuthorizationMiddleware(RequestDelegate next, Func<AutoSourcarrSettings> settings, Func<string, bool> deviceKey)
{
    /// <summary>Without accounts (tests, tools): only the configured reader token.</summary>
    public ReaderAuthorizationMiddleware(RequestDelegate next, Func<AutoSourcarrSettings> settings) : this(next, settings, _ => false) { }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (!ApiAuthorization.IsReaderPath(path)) { await next(context).ConfigureAwait(false); return; }
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.Headers.Allow = "GET, HEAD";
            await ApiJson.ProblemAsync(context, 405, "AutoSourcarr reader API is read-only").ConfigureAwait(false); return;
        }
        var current = settings();
        // AUTH-4: a reader key made for one device (Keys page) works like the reader token and can be revoked alone.
        if (deviceKey(Supplied(context.Request))) { await ContinueAsync(context, path).ConfigureAwait(false); return; }
        if (current.AutosourcarrReaderApiToken.Trim().Length == 0)
        { await ApiJson.ProblemAsync(context, 503, "AutoSourcarr reader API token is not configured").ConfigureAwait(false); return; }
        if (!ApiAuthorization.ReaderAllowed(context.Request, current))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await ApiJson.ProblemAsync(context, 401, "Invalid AutoSourcarr reader API token").ConfigureAwait(false); return;
        }
        await ContinueAsync(context, path).ConfigureAwait(false);
    }

    private static string Supplied(HttpRequest request)
    {
        var supplied = request.Headers["X-AutoSourcarr-Api-Key"].ToString().Trim();
        var authorization = request.Headers.Authorization.ToString().Trim();
        if (supplied.Length == 0 && authorization.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)) supplied = authorization[7..].Trim();
        return supplied;
    }

    private async Task ContinueAsync(HttpContext context, string path)
    {
        var original = context.Request.Path;
        context.Request.Path = ApiAuthorization.InternalPrefix + path[ApiAuthorization.ReaderPrefix.Length..];
        context.Items[ApiAuthorization.ReaderRequestItem] = true;
        try { await next(context).ConfigureAwait(false); }
        finally { context.Request.Path = original; context.Items.Remove(ApiAuthorization.ReaderRequestItem); }
    }
}
