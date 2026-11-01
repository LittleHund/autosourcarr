using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Jobs;
using Microsoft.AspNetCore.Http;
using AutoSourcarr.Core.Diagnostics;

namespace AutoSourcarr.Api.Http;

public sealed class CatalogAdmissionMiddleware(RequestDelegate next, RequestAdmissionGate admission)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!RequestPolicy.CatalogAdmission(context.Request.Method, context.Request.Path.Value ?? ""))
        { await next(context).ConfigureAwait(false); return; }
        IDisposable lease;
        try { lease = admission.Enter(); }
        catch (RequestAdmissionPausedException error)
        { context.Response.Headers.RetryAfter = "5"; await ApiJson.ProblemAsync(context, 503, error.Message).ConfigureAwait(false); return; }
        using (lease)
        using (RuntimeMetrics.Shared.Phase("admission"))
            await next(context).ConfigureAwait(false);
    }
}

public sealed class InteractiveRequestMiddleware(RequestDelegate next, WholeTaskCoordinator lanes, TaskRegistry tasks, SupportPrivacy privacy, Action<string> resolveErrorPrefix)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? ""; var method = context.Request.Method;
        using var interactive = RequestPolicy.Interactive(method, path) ? lanes.BeginInteractive() : null;
        var name = RequestPolicy.MutationTaskName(method, path);
        var id = name is null ? null : tasks.Create(name, 1, "Running", origin: "manual");
        try
        {
            await next(context).ConfigureAwait(false);
            if (id is not null)
            {
                if (context.Response.StatusCode < 400) tasks.Complete(id, "Completed");
                else tasks.Fail(id, "HTTP " + context.Response.StatusCode);
            }
            if (context.Response.StatusCode < 500) resolveErrorPrefix($"unhandled|{method}|{path}|");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        { if (id is not null) tasks.Cancel(id, "Request cancelled"); throw; }
        catch (Exception error) { if (id is not null) tasks.Fail(id, privacy.Text(error.Message)); throw; }
    }
}
