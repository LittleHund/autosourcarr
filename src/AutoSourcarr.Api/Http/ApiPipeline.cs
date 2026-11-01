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
        TaskRegistry tasks, SupportPrivacy privacy, Action<string> resolveErrorPrefix, AutoSourcarr.Api.Security.AccessControlServices? access = null)
    {
        const string key = "AutoSourcarr.HttpPipeline.Registered";
        if (app.Properties.ContainsKey(key)) throw new InvalidOperationException("The instance HTTP pipeline is already registered");
        app.Properties[key] = true;
        app.UseMiddleware<HttpSafetyMiddleware>(version);
        Func<string, bool> deviceKey = access is null ? _ => false : secret => secret.Length > 0 && access.Accounts.FindKey(secret) is { Kind: "reader" };
        app.UseMiddleware<ReaderAuthorizationMiddleware>(settings, deviceKey);
        // Catch against the rewritten internal route so reader failures and later
        // recovery share the same stable registry key.
        app.UseMiddleware<ApiExceptionMiddleware>(recordUnexpected);
        // AUTH-6: who is calling and what they may use, decided once for every request (the app always passes it).
        if (access is not null) app.UseMiddleware<AutoSourcarr.Api.Security.AccessControlMiddleware>(access);
        app.UseMiddleware<CatalogAdmissionMiddleware>(admission);
        app.UseMiddleware<InteractiveRequestMiddleware>(lanes, tasks, privacy, resolveErrorPrefix);
        // Phase 7: the interactive WebView (SourceWebViewEndpoints) is the only WebSocket; its route checks Origin and ticket.
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        return app.UseRouting();
    }
}
