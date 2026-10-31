using AutoSourcarr.Api.Security;
using Microsoft.AspNetCore.Http;

namespace AutoSourcarr.Api.Http;

public sealed class HttpSafetyMiddleware(RequestDelegate next, string version)
{
    private static readonly string[] AssetPrefixes = ["/api/v1/pages/", "/api/v1/covers/", "/api/v1/source-covers/", "/api/v1/source-icon/", "/api/v1/source-extension-icon/"];
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var relative = ApiAuthorization.IsReaderPath(path) ? ApiAuthorization.InternalPrefix + path[ApiAuthorization.ReaderPrefix.Length..] : path;
        var asset = AssetPrefixes.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal));
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (path.StartsWith("/api/admin/", StringComparison.Ordinal) || path == "/api/v1/health") context.Response.Headers["X-AutoSourcarr-Version"] = version;
            if (asset)
            {
                context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
            }
            return Task.CompletedTask;
        });
        var hosts = context.Request.Headers.Host;
        if (hosts.Count > 1 || hosts.Any(host => host is not null && host.Any(character => character <= 32 || character >= 127 || "/\\?#@".Contains(character, StringComparison.Ordinal))))
        { await ApiJson.ProblemAsync(context, 400, "Invalid Host header").ConfigureAwait(false); return; }
        if (path == "/" || path.StartsWith("/static/", StringComparison.Ordinal))
        { context.Request.Headers.Remove("Range"); context.Request.Headers.Remove("If-Range"); }
        if (CrossSiteWritePolicy.IsCrossSiteWrite(context.Request.Method, path, context.Request.Headers))
        { await ApiJson.ProblemAsync(context, 403, CrossSiteWritePolicy.Detail).ConfigureAwait(false); return; }
        await next(context).ConfigureAwait(false);
    }
}
