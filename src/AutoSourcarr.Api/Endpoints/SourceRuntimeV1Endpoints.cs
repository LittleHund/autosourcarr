using System.Text.Json.Nodes;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the source runtime routes need from the running app (null parts answer 503).</summary>
public sealed class SourceRuntimeServices
{
    /// <summary>Source engine status: mode, state, ready, restarts, error.</summary>
    public required Func<JsonObject> Engine { get; init; }
    public required SuwayomiClient Client { get; init; }
    public SourceAttention? Attention { get; init; }
    public Func<SourceEngineCapabilityContract?>? SourceCapabilities { get; init; }
    /// <summary>Read when called: the main-engine check is created after the routes.</summary>
    public Func<SourceEngineCapabilityContract?>? MainCapabilities { get; init; }
    public required SemaphoreSlim AdminLock { get; init; }
    /// <summary>Records a failure of background work started by a route (attention retries).</summary>
    public Action<Exception>? RecordError { get; init; }
}

/// <summary>
/// Phase 4: source engine, source browser, assistance (attention) and engine-compatibility routes under /api/v1.
/// The engine is the AutoSourcarr source host; browser controls talk to it directly and work without a restart.
/// </summary>
internal static class SourceRuntimeV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json)
    {
        group.MapGet("/source-engine", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services.SourceRuntime is not { } runtime) return Unavailable();
                return Results.Json(await EngineAsync(runtime, context.RequestAborted).ConfigureAwait(false), json.JsonObject);
            }))
            .WithName("getSourceEngine")
            .WithSummary("Source engine status: mode, state, readiness, restarts, last error and the Cloudflare handlers (WebView, FlareSolverr).")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/source-engine/challenges", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.SourceRuntime is not { } runtime) return Unavailable();
                var engine = await EngineAsync(runtime, context.RequestAborted).ConfigureAwait(false);
                // The source host solves challenges on demand (WebView, or FlareSolverr when configured): there is no
                // separate setup to queue. The answer reports the handlers as they are now.
                engine["queued"] = false;
                return Results.Json(engine, json.JsonObject);
            }))
            .WithName("checkSourceChallenges")
            .WithSummary("Report the Cloudflare handlers now. The source host solves challenges when a source meets one, so nothing is queued.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapGet("/source-browser", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services.SourceRuntime is not { } runtime) return Unavailable();
                return Results.Json(await BrowserAsync(runtime, services, null, context.RequestAborted).ConfigureAwait(false), json.JsonObject);
            }))
            .WithName("getSourceBrowser")
            .WithSummary("Source browser identity (user-agent preset and value), presets, the identity the engine uses now and the WebView state.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPut("/source-browser/user-agent", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.SourceRuntime is not { } runtime) return Unavailable();
                if (!Integrated(services)) return Problem(409, "Browser controls require integrated mode.");
                JsonObject? body;
                try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false) as JsonObject; }
                catch (System.Text.Json.JsonException) { body = null; }
                if (body is null) return Problem(400, "Expected a user-agent object");
                (string Preset, string UserAgent) chosen;
                try { chosen = SourceBrowserPreferences.Validate(Text(body["preset"]) ?? "custom", Text(body["user_agent"]) ?? ""); }
                catch (ArgumentException error) { return Problem(400, error.Message); }
                await runtime.AdminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                try
                {
                    SourceBrowserPreferences.Save(services.ConfigurationRoot, chosen.Preset, chosen.UserAgent);
                    // Empty = the engine default. Sources rebuild their headers when the identity changes.
                    var applied = await runtime.Client.HostAsync(HttpMethod.Put, "/v1/browser/user-agent",
                        new JsonObject { ["user_agent"] = chosen.UserAgent }, context.RequestAborted).ConfigureAwait(false) as JsonObject;
                    return Results.Json(await BrowserAsync(runtime, services, applied, context.RequestAborted).ConfigureAwait(false), json.JsonObject, statusCode: 202);
                }
                catch (Exception error) when (error is SuwayomiException or SourceWorkDeferredException or HttpRequestException)
                { return Problem(409, "Saved, but the source engine did not take the new identity: " + error.Message); }
                finally { runtime.AdminLock.Release(); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("setSourceUserAgent")
            .WithSummary("Save the source browser identity and apply it to the running engine (sources rebuild their headers).")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        MapHostAction(group, services, json, "/source-browser/clear-data", "/v1/browser/clear-data", "clearSourceBrowserData",
            "Remove the engine's cookies and browser data (sign-ins and Cloudflare clearances). Installed extensions stay.");
        MapHostAction(group, services, json, "/source-browser/restart", "/v1/sources/reload", "restartSources",
            "Load every installed extension again (new headers, changed settings). Installed extensions and sign-ins stay.");

        group.MapGet("/attention", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services.SourceRuntime?.Attention is not { } attention) return Unavailable();
                return Results.Json(await attention.SnapshotAsync(context.RequestAborted).ConfigureAwait(false), json.JsonObject);
            }))
            .WithName("getAttention")
            .WithSummary("Sources that need a person (verification, sign-in): open items with the next automatic retry.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/attention/{attentionId}/retry", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.SourceRuntime is not { Attention: { } attention } runtime) return Unavailable();
                var id = context.Request.RouteValues["attentionId"]?.ToString() ?? "";
                var snapshot = await attention.SnapshotAsync(context.RequestAborted).ConfigureAwait(false);
                if (!(snapshot["items"] as JsonArray ?? []).Any(row => Text(row?["id"]) == id))
                    return Problem(404, "This assistance item is already resolved");
                _ = Task.Run(async () =>
                {
                    try { await attention.RetryAsync(id, runtime.Client, manual: true).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException) { runtime.RecordError?.Invoke(error); }
                });
                return Results.Json(new JsonObject { ["queued"] = true, ["attention_id"] = id }, json.JsonObject, statusCode: 202);
            }))
            .WithName("retryAttention")
            .WithSummary("Check an assistance item again now (in the background).")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/engine/{target}/compatibility", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.SourceRuntime is not { } runtime) return Unavailable();
                var target = context.Request.RouteValues["target"]?.ToString() ?? "";
                context.Response.Headers.CacheControl = "no-store";
                if (target is not ("source" or "main")) return Problem(422, "Select source or main engine");
                var service = (target == "source" ? runtime.SourceCapabilities : runtime.MainCapabilities)?.Invoke();
                if (service is null) return Problem(409, "Configure this engine first");
                await runtime.AdminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                try { return Results.Json(await service.CheckAsync(context.RequestAborted).ConfigureAwait(false), json.JsonObject); }
                catch (OperationCanceledException) { context.Abort(); throw; }
                catch (Exception error) { return Problem(409, "Engine compatibility check incomplete: " + error.GetType().Name); }
                finally { runtime.AdminLock.Release(); }
            }))
            .WithName("checkEngineCompatibility")
            .WithSummary("Check which API features the source or main engine answers (target: source or main) and save the report.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);
    }

    private static void MapHostAction(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json, string route, string hostPath, string name, string summary)
    {
        group.MapPost(route, (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.SourceRuntime is not { } runtime) return Unavailable();
                if (!Integrated(services)) return Problem(409, "Browser controls require integrated mode.");
                await runtime.AdminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                try
                {
                    await runtime.Client.HostAsync(HttpMethod.Post, hostPath, null, context.RequestAborted).ConfigureAwait(false);
                    return Results.Json(new JsonObject { ["queued"] = true }, json.JsonObject, statusCode: 202);
                }
                catch (Exception error) when (error is SuwayomiException or SourceWorkDeferredException or HttpRequestException)
                { return Problem(409, "The source engine did not answer: " + error.Message); }
                finally { runtime.AdminLock.Release(); }
            }))
            .WithName(name)
            .WithSummary(summary)
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);
    }

    private static bool Integrated(ApiV1Services services) => services.Settings().SourceEngineMode == "integrated";

    private static async Task<JsonObject> HostJsonAsync(SourceRuntimeServices runtime, string path, CancellationToken cancellationToken)
    {
        if (!runtime.Client.SourceHost) return new JsonObject();
        try { return await runtime.Client.HostAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false) as JsonObject ?? new JsonObject(); }
        catch (Exception error) when (error is SuwayomiException or SourceWorkDeferredException or HttpRequestException) { return new JsonObject { ["error"] = error.Message }; }
    }

    private static async Task<JsonObject> EngineAsync(SourceRuntimeServices runtime, CancellationToken cancellationToken)
    {
        var engine = runtime.Engine();
        var health = await HostJsonAsync(runtime, "/v1/health", cancellationToken).ConfigureAwait(false);
        var webview = Text(health["webview"]) ?? "";
        var flare = runtime.Client.SourceHost ? "" : "configured in the external engine";
        engine["host"] = health.DeepClone();
        engine["automatic_challenges"] = new JsonObject
        {
            ["state"] = webview == "ready" ? "ready" : webview.StartsWith("failed", StringComparison.Ordinal) ? "failed" : webview == "disabled" ? "disabled" : "starting",
            ["message"] = runtime.Client.SourceHost
                ? webview == "ready" ? "Cloudflare checks are solved in the built-in WebView when a source meets one."
                    : webview.Length == 0 ? "WebView state unknown." : "WebView: " + webview + "."
                : "The external engine handles Cloudflare checks (" + flare + ").",
            ["sources"] = new JsonArray()
        };
        return engine;
    }

    private static async Task<JsonObject> BrowserAsync(SourceRuntimeServices runtime, ApiV1Services services, JsonObject? applied, CancellationToken cancellationToken)
    {
        var (preset, agent) = SourceBrowserPreferences.Read(services.ConfigurationRoot);
        var host = applied ?? await HostJsonAsync(runtime, "/v1/browser", cancellationToken).ConfigureAwait(false);
        var engine = runtime.Engine();
        var ready = engine["ready"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
        return new JsonObject
        {
            ["preferences"] = new JsonObject { ["preset"] = preset, ["user_agent"] = agent },
            ["presets"] = SourceBrowserPreferences.PresetsJson(),
            ["default_user_agent"] = Text(host["default_user_agent"]) ?? SourceEngineEnvironment.DefaultUserAgent,
            ["operation"] = applied is null ? "idle" : "completed",
            ["busy"] = false,
            ["error"] = Text(host["error"]) ?? "",
            ["result"] = null,
            ["runtime"] = new JsonObject { ["network"] = Text(host["user_agent"]), ["webview"] = Text(host["webview"]) },
            ["cookies"] = host["cookies"]?.DeepClone(),
            ["engine_state"] = ready ? "ready" : Text(engine["state"]) ?? "unknown",
            ["integrated"] = Integrated(services),
            ["automatic_challenges"] = (await EngineAsync(runtime, cancellationToken).ConfigureAwait(false))["automatic_challenges"]?.DeepClone()
        };
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static IResult Unavailable() => Problem(503, "The source engine is not available in this host.");
    private static IResult Problem(int status, string detail) =>
        Results.Json(new ProblemResponse(detail), ApiJsonContext.Default.ProblemResponse, statusCode: status);
}
