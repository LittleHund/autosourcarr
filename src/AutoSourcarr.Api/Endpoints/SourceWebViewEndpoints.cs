using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources.WebView;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the interactive WebView routes need from the running app (null answers 503).</summary>
public sealed class SourceWebViewServices
{
    public required SourceWebViewBroker Broker { get; init; }
    /// <summary>The base URL of an installed source by id, or null when the source is unknown.</summary>
    public required Func<string, CancellationToken, Task<string?>> SourceBaseUrl { get; init; }
}

/// <summary>
/// Phase 7, SRC-5 "Open in WebView": <c>POST /api/v1/source-webview/session {url?, source_id?}</c> (admin only) opens a
/// session in the source host's WebView (with neither, an empty page where the administrator types an address; with a
/// source, the url must be on that source's website) and returns its id and a one-time ticket; the page
/// (/static/source-webview.html) then connects <c>WEBSOCKET /api/v1/source-webview/socket?session=&lt;id&gt;</c> offering
/// the subprotocols "autosourcarr.webview" and "ticket.&lt;ticket&gt;" (a browser cannot send the admin token on a
/// WebSocket, so the socket path is public in <see cref="AccessPolicy"/> and the ticket is its credential; it works
/// once, for one minute, and the Origin must be this app).
/// </summary>
public static class SourceWebViewEndpoints
{
    public const string SessionPath = "/api/v1/source-webview/session";
    public const string SocketPath = "/api/v1/source-webview/socket";
    public const string PagePath = "/static/source-webview.html";

    public static IEndpointRouteBuilder MapSourceWebView(this IEndpointRouteBuilder endpoints, Func<SourceWebViewServices?> services, Func<AutoSourcarrSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(endpoints); ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(settings);
        static IResult Problem(int status, string detail) => Results.Json(new JsonObject { ["detail"] = detail }, statusCode: status);

        endpoints.MapPost(SessionPath, (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services() is not { } webView) return Problem(503, "The WebView is not available in this host.");
                JsonObject body;
                try { body = await ApiJson.ReadObjectAsync(context.Request, 8192).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                var rawUrl = Text(body, "url");
                var sourceId = Text(body, "source_id");
                if (rawUrl is null && body["url"] is not null || sourceId is null && body["source_id"] is not null)
                    return Problem(400, "url and source_id must be text.");
                Uri? url = null;
                if (!string.IsNullOrWhiteSpace(rawUrl) && (url = SourceWebViewBroker.CheckUrl(rawUrl)) is null)
                    return Problem(400, "The WebView opens http and https addresses only (no user name or password).");
                if (!string.IsNullOrWhiteSpace(sourceId))
                {
                    string? baseText;
                    try { baseText = await webView.SourceBaseUrl(sourceId, context.RequestAborted).ConfigureAwait(false); }
                    catch (Exception error) when (error is HttpRequestException or AutoSourcarr.Core.Sources.SuwayomiException or TimeoutException)
                    { return Problem(503, "The source list is not available right now; try again shortly."); }
                    if (baseText is null) return Problem(404, "No installed source " + sourceId + ".");
                    if (SourceWebViewBroker.CheckUrl(baseText) is not { } sourceBase) return Problem(400, "This source has no website address.");
                    url ??= sourceBase;
                    // The address must be on the source's own website; another address can be typed in the WebView itself.
                    if (!SourceWebViewBroker.OnSourceSite(url, sourceBase))
                        return Problem(400, "The address is not on this source's website. Open the WebView and type the address there.");
                }
                var caller = AccessControlMiddleware.CallerOf(context);
                var owner = caller.Via + ":" + caller.Name + ":" + caller.KeyId;
                try
                {
                    var (session, ticket) = await webView.Broker.OpenAsync(owner, url, context.RequestAborted).ConfigureAwait(false);
                    return Results.Json(new JsonObject
                    {
                        ["session_id"] = session.Id,
                        ["ticket"] = ticket,
                        ["url"] = session.Start?.AbsoluteUri ?? "",
                        ["socket"] = SocketPath,
                        ["subprotocol"] = SourceWebViewBroker.SubProtocol,
                        ["page"] = PagePath,
                        ["ticket_seconds"] = (int)webView.Broker.Options.TicketLifetime.TotalSeconds,
                        ["idle_seconds"] = (int)webView.Broker.Options.IdleTimeout.TotalSeconds,
                        ["max_seconds"] = (int)webView.Broker.Options.MaxLifetime.TotalSeconds,
                    });
                }
                catch (SourceWebViewException error) { return Problem(error.Status, error.Message); }
                catch (HttpRequestException) { return Problem(503, "The source host is not reachable, so the WebView is not available."); }
                catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
                { return Problem(504, "The source host did not open the WebView in time."); }
            }))
            .AddEndpointFilter(new AdminAuthorizationFilter(settings))
            .ExcludeFromDescription();

        endpoints.MapGet(SocketPath, (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!context.WebSockets.IsWebSocketRequest) return Problem(400, "This address is the WebView's WebSocket; open WebView from AutoSourcarr.");
                if (services() is not { } webView) return Problem(503, "The WebView is not available in this host.");
                // A WebSocket handshake is a GET, so the cross-site write check does not see it: the Origin must be this app.
                var headers = context.Request.Headers;
                if (headers.Origin.ToString().Trim().Length == 0
                    || CrossSiteWritePolicy.IsCrossSiteWrite("POST", SocketPath, headers["Sec-Fetch-Site"].ToString(), headers.Origin.ToString(),
                        headers.Host.ToString(), headers["X-Forwarded-Host"].ToString()))
                    return Problem(403, "Open the WebView from AutoSourcarr itself.");
                var ticket = SourceWebViewBroker.TicketFrom(context.WebSockets.WebSocketRequestedProtocols);
                if (webView.Broker.Claim(context.Request.Query["session"].ToString(), ticket) is not { } session)
                    return Problem(401, "This WebView session has ended or was already opened. Open WebView again.");
                using var socket = await context.WebSockets.AcceptWebSocketAsync(SourceWebViewBroker.SubProtocol).ConfigureAwait(false);
                await webView.Broker.RelayAsync(session, socket, context.RequestAborted).ConfigureAwait(false);
                return Results.Empty;
            }))
            .ExcludeFromDescription();
        return endpoints;
    }

    private static string? Text(JsonObject body, string key) =>
        body[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
