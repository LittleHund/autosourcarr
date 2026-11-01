using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Imports;
using AutoSourcarr.Core.Imports.Backup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the import routes need from the running app (null answers 503).</summary>
public sealed class ImportsV1Services
{
    public required ImportRuns Runs { get; init; }
}

/// <summary>
/// Phase 9 imports under /api/v1 (0.4.5 /api/admin/import/mihon-backup and /api/admin/imports/...): the backup upload, runs,
/// items with Match / Search / Skip / Retry / Reject (latest click wins), the remembered-decision resets, the after-reset
/// recheck and the Suwayomi library plan import. Every change records its actor from the caller; all routes are admin-only
/// (the /api/v1 group filter). Filters travel in the body: the generated client has no query parameters.
/// </summary>
internal static class ImportsV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiJsonContext json, ImportsV1Services? imports)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Imports are not available in this host.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        async Task<IResult> Guard(HttpContext context, bool body, Func<ImportRuns, JsonObject, Task<IResult>> action)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (imports is null) return Unavailable();
            try
            {
                var request = body ? await ManualDecisionsV1Endpoints.Body(context).ConfigureAwait(false) : new JsonObject();
                return await action(imports.Runs, request).ConfigureAwait(false);
            }
            catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
            catch (BackupDecodeException error) { return Problem(400, error.Message); }
            catch (KeyNotFoundException error) { return Problem(404, error.Message); }
            catch (ImportUnavailableException error) { return Problem(503, error.Message); }
            catch (ImportConflictException error) { return Problem(409, error.Message); }
            catch (ImportPolicyBlockedException error) { return Problem(409, error.Message); }
            catch (ArgumentException error) { return Problem(400, error.Message); }
        }
        string RunId(HttpContext context)
        {
            var id = ManualDecisionsV1Endpoints.Route(context, "importId").Trim().ToLowerInvariant();
            return id.Length is > 0 and <= 64 ? id : throw new ApiProblem(400, "Invalid import identity");
        }
        long ItemId(HttpContext context, string name = "itemId") =>
            ManualDecisionsV1Endpoints.TryRouteLong(context, name, out var id) ? id : throw new ApiProblem(400, "Invalid " + (name == "itemId" ? "import item" : "entry") + " id");
        int BodyInt(JsonObject body, string name, int fallback) => ManualDecisionsV1Endpoints.BodyLong(body, name) is var value && value > 0
            ? (int)Math.Min(value, int.MaxValue) : fallback;

        // ---- upload and runs ----------------------------------------------------------------------------------------

        group.MapPost("/imports/mihon-backup", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, async (runs, _) =>
            {
                var request = context.Request;
                string? filename; string? importId = request.Headers["X-Import-Id"].ToString();
                byte[] data;
                if (request.HasFormContentType)
                {
                    if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                        limit.MaxRequestBodySize = MihonBackupReader.MaxBackupBytes + 1024 * 1024;
                    var form = await request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
                    var file = form.Files["file"] ?? form.Files.FirstOrDefault();
                    if (file is null) return Problem(400, "Choose a .tachibk or .proto.gz backup file.");
                    if (file.Length > MihonBackupReader.MaxBackupBytes) return Problem(413, "Backup file is larger than 128 MiB");
                    filename = file.FileName;
                    if (form["import_id"].ToString() is { Length: > 0 } formId) importId = formId;
                    using var buffer = new MemoryStream();
                    await using (var stream = file.OpenReadStream()) await stream.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
                    data = buffer.ToArray();
                }
                else
                {
                    var encoded = request.Headers["X-Filename-Utf8"].ToString();
                    if (encoded.Length > 0)
                    {
                        try { filename = Uri.UnescapeDataString(encoded); }
                        catch (UriFormatException) { return Problem(400, "Invalid UTF-8 backup filename"); }
                    }
                    else filename = request.Headers["X-Filename"].ToString();
                    data = await ApiJson.ReadUploadAsync(request, (int)MihonBackupReader.MaxBackupBytes, "Backup file is larger than 128 MiB",
                        "The backup upload did not finish in time; choose the file again").ConfigureAwait(false);
                }
                if (data.Length == 0) return Problem(400, "Backup file is empty");
                return Answer(runs.AcceptUpload(data, filename, importId, ManualDecisionsV1Endpoints.Actor(context)));
            })))
            .WithMetadata(ApiJson.BoundedUploadMetadata)
            .WithName("uploadMihonBackup")
            .WithSummary("Upload a Mihon .tachibk or Tachiyomi .proto.gz backup (raw body with X-Filename / X-Filename-Utf8 and optional X-Import-Id, or multipart field \"file\") and queue the add-only library import. Answers the run id, detected backup version and counts.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/imports/{importId}/receipt", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (runs, _) =>
            {
                var id = RunId(context);
                return Task.FromResult(runs.Receipt(id) is { } receipt ? Answer(receipt) : Problem(404, "No accepted upload is recorded for this identity"));
            })))
            .WithName("getImportReceipt")
            .WithSummary("The accepted upload of an import id (to resume a client that lost the upload answer).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/imports", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (runs, _) =>
                Task.FromResult(Answer(runs.ListRuns(50))))))
            .WithName("listImportRuns")
            .WithSummary("The 50 newest import runs with their added / existing / unresolved / failed / skipped counts.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/imports/clear-history", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (runs, _) =>
            {
                var result = runs.ClearHistory(ManualDecisionsV1Endpoints.Actor(context));
                result["ok"] = true;
                return Task.FromResult(Answer(result));
            })))
            .WithName("clearImportHistory")
            .WithSummary("Delete finished import runs and their rows; running imports, remembered matches and rejections stay.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/imports/reset-rejections", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (runs, _) =>
            {
                var result = runs.ResetRejections(ManualDecisionsV1Endpoints.Actor(context));
                result["ok"] = true;
                return Task.FromResult(Answer(result));
            })))
            .WithName("resetImportRejections")
            .WithSummary("Forget every remembered import Reject (explicit reset); unresolved rows search for candidates again.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/imports/reset-manual-autoresolve", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (runs, _) =>
            {
                var result = runs.ResetManualAutoresolve(ManualDecisionsV1Endpoints.Actor(context));
                result["ok"] = true;
                return Task.FromResult(Answer(result));
            })))
            .WithName("resetImportManualAutoresolve")
            .WithSummary("Forget the remembered manual import matches (explicit reset); ordinary manual matches and groups stay.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/imports/recheck-after-reset", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (runs, _) =>
                Task.FromResult(Answer(runs.RecheckAfterReset(ManualDecisionsV1Endpoints.Actor(context)))))))
            .WithName("recheckImportsAfterReset")
            .WithSummary("Recheck accepted imports whose entry is gone (after a catalogue reset) through the kept decision log: manual matches are restored, automatic ones kept only if they still pass the current rules, otherwise reopened.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/imports/suwayomi-plan", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (runs, _) =>
                Task.FromResult(Answer(runs.ImportSuwayomiPlan(ManualDecisionsV1Endpoints.Actor(context)))))))
            .WithName("importSuwayomiPlan")
            .WithSummary("Create catalogue entries for the stored Suwayomi library plan (Connections → Suwayomi import) through the import pipeline; the plan becomes \"imported\".")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        // ---- items --------------------------------------------------------------------------------------------------

        group.MapPost("/imports/{importId}/items", (Func<HttpContext, Task<IResult>>)(context => Guard(context, true, (runs, body) =>
                Task.FromResult(Answer(runs.Items(RunId(context), ManualDecisionsV1Endpoints.BodyText(body, "state"), Math.Clamp(BodyInt(body, "limit", 1000), 1, 5000),
                    (int)Math.Max(0, ManualDecisionsV1Endpoints.BodyLong(body, "offset")),
                    ManualDecisionsV1Endpoints.BodyText(body, "review") is { Length: > 0 } review ? review : "all"))))))
            .WithName("listImportItems")
            .WithSummary("One page of a run's rows ({state, review, limit, offset} in the body) with hydrated suggestions and review counts over the whole run.")
            .Accepts<JsonObject>("application/json")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/imports/{importId}/items/{itemId}", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (runs, _) =>
                Task.FromResult(Answer(runs.Item(RunId(context), ItemId(context)))))))
            .WithName("getImportItem")
            .WithSummary("One import row with its run.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/imports/{importId}/items/{itemId}/suggestions", (Func<HttpContext, Task<IResult>>)(context => Guard(context, true, (runs, body) =>
                Task.FromResult(Answer(runs.Suggestions(RunId(context), ItemId(context), ManualDecisionsV1Endpoints.BodyText(body, "q")))))))
            .WithName("getImportItemSuggestions")
            .WithSummary("Local catalogue suggestions for a row ({q} in the body; without q the stored candidates are refreshed).")
            .Accepts<JsonObject>("application/json")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/imports/{importId}/items/{itemId}/suggestions/live", (Func<HttpContext, Task<IResult>>)(context => Guard(context, true, async (runs, body) =>
                Answer(await runs.LiveSuggestionsAsync(RunId(context), ItemId(context), ManualDecisionsV1Endpoints.BodyText(body, "q"), automatic: false,
                    cancellationToken: context.RequestAborted).ConfigureAwait(false)))))
            .WithName("searchImportItemLive")
            .WithSummary("Search: the local catalogue, the backup's own source and the enabled sources for a row ({q} in the body). A Search is a decision: it replaces a queued Match or Retry of the row.")
            .Accepts<JsonObject>("application/json")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/imports/{importId}/items/{itemId}/retry", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (runs, _) =>
                Task.FromResult(Answer(runs.ScheduleRetry(RunId(context), ItemId(context), ManualDecisionsV1Endpoints.Actor(context)))))))
            .WithName("retryImportItem")
            .WithSummary("Retry a row in the background (remembered match, full resolution with metadata, live sources); the newest decision of the row wins.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/imports/{importId}/items/{itemId}/match/{entryId}", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (runs, _) =>
                Task.FromResult(Answer(runs.ScheduleMatch(RunId(context), ItemId(context), ItemId(context, "entryId"), ManualDecisionsV1Endpoints.Actor(context)))))))
            .WithName("matchImportItem")
            .WithSummary("Match a row to an entry: remembered for this backup identity at once, then bound and added to the main library in the background.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/imports/{importId}/items/{itemId}/reject", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (runs, _) =>
                Task.FromResult(Answer(runs.ScheduleReject(RunId(context), ItemId(context), ManualDecisionsV1Endpoints.Actor(context)))))))
            .WithName("rejectImportItem")
            .WithSummary("Reject an accepted Added/Existing match: remembered for this backup identity; a verified import addition is removed from the main library and alternatives are searched.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/imports/{importId}/items/{itemId}/skip", (Func<HttpContext, Task<IResult>>)(context => Guard(context, false, (runs, _) =>
                Task.FromResult(Answer(runs.Skip(RunId(context), ItemId(context), ManualDecisionsV1Endpoints.Actor(context)))))))
            .WithName("skipImportItem")
            .WithSummary("Skip a row; a queued Match or Retry of it then changes nothing.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);
    }
}
