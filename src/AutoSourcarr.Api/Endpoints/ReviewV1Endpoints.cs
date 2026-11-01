using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Review;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the review workbench and policy simulator routes need from the running app (null answers 503).</summary>
public sealed class ReviewV1Services
{
    public required ReviewWorkbench Workbench { get; init; }
    public required PolicySimulator Simulator { get; init; }
}

/// <summary>
/// Phase 8E: the review workbench (audit list with actors, reviewed batches, undo) and the matching policy simulator under
/// /api/v1 (0.4.x /api/admin/review-workbench and /api/admin/policy-simulator).
/// </summary>
internal static class ReviewV1Endpoints
{
    private const int MaximumBody = 262144;

    internal static void Map(RouteGroupBuilder group, ApiJsonContext json, ReviewV1Services? review)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Matching review is not available in this host.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        async Task<IResult> Guard(HttpContext context, bool body, Func<ReviewV1Services, JsonObject, IResult> action)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (review is null) return Unavailable();
            try
            {
                var request = body ? await ApiJson.ReadObjectAsync(context.Request, MaximumBody).ConfigureAwait(false) : new JsonObject();
                return action(review, request);
            }
            catch (ReviewException error) { return Problem(error.StatusCode, error.Message); }
            catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
            catch (KeyNotFoundException) { return Problem(404, "Work not found"); }
            catch (ArgumentException error) { return Problem(400, error.Message); }
            catch (InvalidOperationException error) { return Problem(409, error.Message); }
        }
        long? RouteId(HttpContext context, string name) =>
            long.TryParse(context.Request.RouteValues[name]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

        // ------------------------------------------------------- review workbench

        group.MapGet("/review-workbench", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (services, _) =>
            {
                var query = context.Request.Query["entry_id"].ToString();
                long? entry = null;
                if (query.Length > 0)
                {
                    if (!long.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1) return Problem(400, "Use an exact positive work ID");
                    entry = id;
                }
                return Answer(services.Workbench.Audit(entry));
            })))
            .WithName("getReviewWorkbench")
            .WithSummary("Recent matching decisions with who made them (merges, splits, separate works, manual matches, title choices), reviewed batches and their undo state.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/review-workbench/entries/{entryId}", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (services, _) =>
                RouteId(context, "entryId") is { } entry ? Answer(services.Workbench.Audit(entry)) : Problem(400, "Use an exact positive work ID"))))
            .WithName("getReviewWorkbenchEntry")
            .WithSummary("The audit trail of one work: its decisions with actors, its current source records and title.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/review-workbench/preview", (Func<HttpContext, Task<IResult>>)(context => Guard(context, true, (services, body) =>
                Answer(services.Workbench.Preview(body)))))
            .Accepts<JsonObject>("application/json")
            .WithName("previewReviewWorkbench")
            .WithSummary("Check 1 to 100 review items (pairs to keep separate, or actions: separate, merge, split, manual.match, title) and return their effects and a revision.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/review-workbench/apply", (Func<HttpContext, Task<IResult>>)(context => Guard(context, true, (services, body) =>
                Answer(services.Workbench.Apply(body, Actor(context))))))
            .Accepts<JsonObject>("application/json")
            .WithName("applyReviewWorkbench")
            .WithSummary("Apply a previewed review batch when its revision still matches; logged as one decision with the signed-in actor.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/review-workbench/{identifier}/undo", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (services, _) =>
                RouteId(context, "identifier") is { } id ? Answer(services.Workbench.Undo(id, Actor(context))) : Problem(404, "Review action is unavailable"))))
            .WithName("undoReviewWorkbench")
            .WithSummary("Undo a reviewed batch while every work and decision it changed is still as the batch left it.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        // ------------------------------------------------------- policy simulator

        group.MapGet("/policy-simulator/options", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (services, _) =>
                Answer(services.Simulator.Options()))))
            .WithName("getPolicySimulatorOptions")
            .WithSummary("Sources, custom groups, saved matching rules (with revision) and presets for the policy simulator.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/policy-simulator/preview", (Func<HttpContext, Task<IResult>>)(context => Guard(context, true, (services, body) =>
            {
                var result = services.Simulator.Preview(body);
                return Answer(result, result["state"]?.GetValue<string>() is "queued" or "running" ? 202 : 200);
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("previewPolicySimulator")
            .WithSummary("Run the real matcher with the current and a draft policy: chosen pairs or a catalogue page at once, or the whole catalogue as a task (scan); lists works that would merge or split.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/policy-simulator/apply", (Func<HttpContext, Task<IResult>>)(context => Guard(context, true, (services, body) =>
                Answer(services.Simulator.Apply(body, Actor(context))))))
            .Accepts<JsonObject>("application/json")
            .WithName("applyPolicySimulator")
            .WithSummary("Save the previewed policy when rules and evidence are unchanged; nothing is merged or split now. Logged with the signed-in actor.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);
    }

    /// <summary>The decision actor of a request: the signed-in account, or the key that called.</summary>
    internal static DecisionActor Actor(HttpContext context)
    {
        var caller = AccessControlMiddleware.CallerOf(context);
        var name = caller.Name.Length > 0 ? caller.Name : "admin";
        return caller.Via == "key" ? DecisionActor.Key(name) : DecisionActor.User(name);
    }
}
