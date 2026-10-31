using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AutoSourcarr.Api.Http;

public static class ApiPipeline
{
    public static IApplicationBuilder UseAutoSourcarrHttp(this IApplicationBuilder app, string version, Func<AutoSourcarrSettings> settings,
        Func<HttpContext, Exception, string> recordUnexpected, RequestAdmissionGate admission, WholeTaskCoordinator lanes,
        TaskRegistry tasks, SupportPrivacy privacy, Action<string> resolveErrorPrefix)
    {
        const string key = "AutoSourcarr.HttpPipeline.Registered";
        if (app.Properties.ContainsKey(key)) throw new InvalidOperationException("The instance HTTP pipeline is already registered");
        app.Properties[key] = true;
        app.UseMiddleware<HttpSafetyMiddleware>(version);
        app.UseMiddleware<ReaderAuthorizationMiddleware>(settings);
        // Catch against the rewritten internal route so reader failures and later
        // recovery share the same stable registry key.
        app.UseMiddleware<ApiExceptionMiddleware>(recordUnexpected);
        app.UseMiddleware<CatalogAdmissionMiddleware>(admission);
        app.UseMiddleware<InteractiveRequestMiddleware>(lanes, tasks, privacy, resolveErrorPrefix);
        return app.UseRouting();
    }
}
