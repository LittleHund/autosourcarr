using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Compatibility;
using AutoSourcarr.Core.Concurrency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the catalogue sharing routes need from the running app (null answers 503).</summary>
public sealed class CatalogueSharingV1Services
{
    public required CatalogueExchange Exchange { get; init; }
    public required PortableIdentityResolver Identities { get; init; }
    public required CatalogueReviewTasks Reviews { get; init; }
    public required IdentityReview IdentityReview { get; init; }
    /// <summary>The Web UI write lock: one sharing/identity write at a time.</summary>
    public required SemaphoreSlim AdminLock { get; init; }
    /// <summary>Bounded physical I/O: every SQLite read/write of these routes owns a lease.</summary>
    public required BoundedWorkScheduler Io { get; init; }
}

/// <summary>
/// Phase 9B catalogue sharing and portable identity under /api/v1 (0.4.5 /api/admin/catalog-sharing/*,
/// /api/admin/portable-identities/resolve and /api/admin/identity-review*), plus the PIPE-4 compatibility list.
/// Every route is admin-only (AccessPolicy). Catalogue files carry a versioned header; a file needing a newer
/// reader is refused with exactly "Version mismatch: version X required" before anything is staged.
/// State changes record a decision-log event with the caller as actor.
/// </summary>
internal static class CatalogueSharingV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiJsonContext json, CatalogueSharingV1Services? sharing)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Answer(JsonObject value) => Results.Json(value, json.JsonObject);
        async Task<IResult> Guard(HttpContext context, Func<CatalogueSharingV1Services, Task<JsonObject>> work)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (sharing is null) return Problem(503, "Catalogue sharing is not available in this host.");
            try { return Answer(await work(sharing).ConfigureAwait(false)); }
            catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
        }

        group.MapGet("/compatibility", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                try { return Answer(CompatibilityList.Current.ToJson()); }
                catch (InvalidOperationException error) { return Problem(503, error.Message); }
            }))
            .WithName("getCompatibility")
            .WithSummary("PIPE-4 compatibility list of this build: app version, database schema (latest migration of each database), share format with its reader table, extension API (libVersion) and profile format.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/catalog-sharing/status", (Func<HttpContext, Task<IResult>>)(context =>
                Guard(context, services => Read(services, context, services.Exchange.Status))))
            .WithName("getCatalogueSharingStatus")
            .WithSummary("Database sharing overview: origin ID, local entries, catalogue files added, retained incoming versions and pending shared choices.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/catalog-sharing/export", (Func<HttpContext, Task<IResult>>)(context => Guard(context, async services =>
            {
                var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); Fields(body, "after", "limit", "include_decisions");
                var after = Integer(body, "after", 0, long.MaxValue, 0); var limit = (int)Integer(body, "limit", 1, 5000, 2000); var decisions = Boolean(body, "include_decisions", true);
                return await Write(services, context, () => services.Exchange.Export(after, limit, decisions)).ConfigureAwait(false);
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("exportCatalogue")
            .WithSummary("One catalogue file (versioned header, engine-neutral JSON, portable IDs) of up to limit entries after the cursor, with saved manual decisions. Body: after, limit (1-5000), include_decisions. next_cursor names the next part.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/catalog-sharing/preview", (Func<HttpContext, Task<IResult>>)(context => Guard(context, async services =>
            {
                var bytes = await ApiJson.ReadUploadAsync(context.Request, CatalogueFormat.MaximumBytes,
                    "Catalogue file exceeds 64 MiB; export smaller batches", "Catalogue upload timed out; select the file again").ConfigureAwait(false);
                return await Write(services, context, () => services.Exchange.Preview(bytes)).ConfigureAwait(false);
            })))
            .WithMetadata(ApiJson.BoundedUploadMetadata)
            .Accepts<JsonObject>("application/json")
            .WithName("previewCatalogue")
            .WithSummary("Stage a catalogue file (the body is the file, up to 64 MiB) and show what adding it would do. A file needing a newer reader answers 409 \"Version mismatch: version X required\"; nothing is staged.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status413PayloadTooLarge);

        group.MapGet("/catalog-sharing/stages/{stageId}", (Func<HttpContext, Task<IResult>>)(context => Guard(context, services =>
            {
                var id = context.Request.RouteValues["stageId"]?.ToString() ?? "";
                if (!Regex.IsMatch(id, "\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant)) throw new ApiProblem(422, "Invalid stage id");
                var offset = QueryInteger(context, "offset", 0, int.MaxValue, 0); var limit = QueryInteger(context, "limit", 1, 200, 100);
                return Read(services, context, () => services.Exchange.Stage(id, offset, limit));
            })))
            .WithName("getCatalogueStage")
            .WithSummary("A staged preview page (state preview, stale or applied with its result). Optional query for the UI: offset, limit (1-200).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/catalog-sharing/apply", (Func<HttpContext, Task<IResult>>)(context => Guard(context, async services =>
            {
                var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); Fields(body, "id", "revision", "include_decisions");
                var id = Hex(body, "id", 32); var revision = Hex(body, "revision", 64); var decisions = Boolean(body, "include_decisions", true);
                var actor = MixedEntriesV1Endpoints.Actor(context);
                return await Write(services, context, () => services.Exchange.Apply(id, revision, decisions, actor)).ConfigureAwait(false);
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("applyCatalogue")
            .WithSummary("Add a staged catalogue file in one transaction (all or nothing). Never merges or splits; existing manual choices win. Body: id, revision (from the preview), include_decisions.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/catalog-sharing/decisions", (Func<HttpContext, Task<IResult>>)(context => Guard(context, services =>
            {
                var offset = QueryInteger(context, "offset", 0, int.MaxValue, 0); var limit = QueryInteger(context, "limit", 1, 50, 30);
                return Read(services, context, () => services.Exchange.Decisions(offset, limit));
            })))
            .WithName("listCatalogueDecisions")
            .WithSummary("Retained shared choices with their local state, mapped entries and revision. Optional query for the UI: offset, limit (1-50).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/catalog-sharing/records", (Func<HttpContext, Task<IResult>>)(context => Guard(context, services =>
            {
                var offset = QueryInteger(context, "offset", 0, int.MaxValue, 0); var limit = QueryInteger(context, "limit", 1, 50, 30);
                return Read(services, context, () => services.Exchange.Records(offset, limit));
            })))
            .WithName("listCatalogueRecords")
            .WithSummary("Incoming entry versions retained for review, with local candidates. Optional query for the UI: offset, limit (1-50).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/catalog-sharing/decisions/review", (Func<HttpContext, Task<IResult>>)(context => Guard(context, async services =>
            {
                var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); Fields(body, "uid", "digest", "revision", "action");
                var (uid, digest, revision) = ReviewIdentity(body); var action = ReviewAction(body);
                if (action is not ("apply" or "ignore")) throw new ApiProblem(409, "Choose Apply or Keep local");
                var actor = MixedEntriesV1Endpoints.Actor(context);
                await services.AdminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                try
                {
                    var current = await Domain(services, context, () => services.Exchange.Decision(uid, digest)).ConfigureAwait(false);
                    if (current["state"]!.GetValue<string>() is "applied" or "ignored") return new JsonObject { ["already_reviewed"] = true, ["state"] = current["state"]!.DeepClone() };
                    if (current["revision"]!.GetValue<string>() != revision) throw new ApiProblem(409, "Retained choice or local records changed; refresh before applying");
                    if (action == "apply" && !current["can_apply"]!.GetValue<bool>()) throw new ApiProblem(409, current["message"]!.GetValue<string>());
                    return services.Reviews.Schedule(uid, digest, revision, action, actor);
                }
                finally { services.AdminLock.Release(); }
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("reviewCatalogueDecision")
            .WithSummary("Apply or keep local one retained shared choice as a task (structural choices use the journalled merge/split owner and never override a local manual decision). Body: uid, digest, revision, action (apply|ignore).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/catalog-sharing/records/review", (Func<HttpContext, Task<IResult>>)(context => Guard(context, async services =>
            {
                var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); Fields(body, "uid", "digest", "revision", "action");
                var (uid, digest, revision) = ReviewIdentity(body); var action = ReviewAction(body); var actor = MixedEntriesV1Endpoints.Actor(context);
                return await Write(services, context, () => services.Exchange.ReviewRecord(uid, digest, revision, action, actor)).ConfigureAwait(false);
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("reviewCatalogueRecord")
            .WithSummary("Add a retained incoming version as a separate entry (action separate) or keep the local record (ignore). Body: uid, digest, revision, action.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/catalog-sharing/records/link", (Func<HttpContext, Task<IResult>>)(context => Guard(context, async services =>
            {
                var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); Fields(body, "uid", "digest", "revision", "target");
                var (uid, digest, revision) = ReviewIdentity(body); var target = Integer(body, "target", 1, long.MaxValue); var actor = MixedEntriesV1Endpoints.Actor(context);
                await services.AdminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                try
                {
                    var choice = await Domain(services, context, () => services.Exchange.PrepareLink(uid, digest, revision, target, actor)).ConfigureAwait(false);
                    return services.Reviews.Schedule(choice["uid"]!.GetValue<string>(), choice["digest"]!.GetValue<string>(), choice["revision"]!.GetValue<string>(), actor: actor);
                }
                finally { services.AdminLock.Release(); }
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("linkCatalogueRecord")
            .WithSummary("Link an unverified imported entry to its verified local counterpart as a reviewed task. Body: uid, digest, revision, target (local entry id).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/portable-identities/resolve", (Func<HttpContext, Task<IResult>>)(context => Guard(context, async services =>
            {
                var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); Fields(body, "reference");
                if (body["reference"] is not JsonValue value || value.GetValueKind() != JsonValueKind.String || value.GetValue<string>().Length is < 1 or > PortableIdentity.MaximumReferenceLength)
                    throw new ApiProblem(422, "Enter a complete ASC Media ID");
                var reference = value.GetValue<string>();
                return await Read(services, context, () => services.Identities.Resolve(reference)).ConfigureAwait(false);
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("resolvePortableIdentity")
            .WithSummary("Local entries associated with an ASC Media ID (local associations only; never merges or asks a source). Body: reference.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/identity-review", (Func<HttpContext, Task<IResult>>)(context => Guard(context, services =>
            {
                var search = context.Request.Query["search"].LastOrDefault() ?? "";
                if (search.Length > 200) throw new ApiProblem(422, "The search text is too long");
                var offset = QueryInteger(context, "offset", 0, int.MaxValue, 0); var limit = QueryInteger(context, "limit", 1, 50, 30);
                return Read(services, context, () => services.IdentityReview.Listing(search, offset, limit));
            })))
            .WithName("listIdentityReviews")
            .WithSummary("Saved entries with their provider link and saved identity review counts. Optional query for the UI: search, offset, limit (1-50).")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/identity-review/{entryId}", (Func<HttpContext, Task<IResult>>)(context => Guard(context, services =>
            {
                var entryId = EntryId(context);
                return Read(services, context, () => services.IdentityReview.Read(entryId));
            })))
            .WithName("getIdentityReview")
            .WithSummary("One entry's portable references with local review decisions, the entries sharing each reference, recent review history and the revision to save against.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/identity-review/{entryId}", (Func<HttpContext, Task<IResult>>)(context => Guard(context, async services =>
            {
                var entryId = EntryId(context);
                var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); Fields(body, "reference", "decision", "revision", "note", "restore_id");
                var reference = Text(body, "reference", PortableIdentity.MaximumReferenceLength); var decision = Text(body, "decision", 16);
                if (decision is not ("confirmed" or "excluded" or "needs-review" or "reset" or "restore")) throw new ApiProblem(422, "Invalid decision");
                var revision = Hex(body, "revision", 64);
                var note = body.ContainsKey("note") ? Text(body, "note", IdentityReview.MaximumNote) : "";
                long? restore = body["restore_id"] is null ? null : Integer(body, "restore_id", 1, long.MaxValue);
                var actor = MixedEntriesV1Endpoints.Actor(context);
                return await Write(services, context, () => services.IdentityReview.Save(entryId, reference, decision, revision, note, restore, actor)).ConfigureAwait(false);
            })))
            .Accepts<JsonObject>("application/json")
            .WithName("saveIdentityReview")
            .WithSummary("Save a local identity review: confirmed, excluded, needs-review, reset (inherited) or restore (restore_id). Source bindings, merges and chapters are unchanged. Body: reference, decision, revision, note, restore_id.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);
    }

    private static Task<JsonObject> Read(CatalogueSharingV1Services services, HttpContext context, Func<JsonObject> operation) => Domain(services, context, operation);

    private static async Task<JsonObject> Write(CatalogueSharingV1Services services, HttpContext context, Func<JsonObject> operation)
    {
        await services.AdminLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try { return await Domain(services, context, operation).ConfigureAwait(false); }
        finally { services.AdminLock.Release(); }
    }

    /// <summary>Domain errors as HTTP answers; a version mismatch keeps its exact message.</summary>
    private static async Task<JsonObject> Domain(CatalogueSharingV1Services services, HttpContext context, Func<JsonObject> operation)
    {
        try { return await services.Io.RunAsync(operation, context.RequestAborted).ConfigureAwait(false); }
        catch (KeyNotFoundException) { throw new ApiProblem(404, "Work or record no longer exists"); }
        catch (ArgumentException error) { throw new ApiProblem(409, error.Message); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Native gallery metadata/evidence provider", StringComparison.Ordinal) || error.Message.StartsWith("A visible reviewed task", StringComparison.Ordinal)) { throw new ApiProblem(503, error.Message); }
    }

    private static long EntryId(HttpContext context) =>
        long.TryParse(context.Request.RouteValues["entryId"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id
            : throw new ApiProblem(400, "A positive entry id is required");

    private static void Fields(JsonObject body, params string[] allowed)
    { if (body.Any(pair => !allowed.Contains(pair.Key, StringComparer.Ordinal))) throw new ApiProblem(422, "Unsupported catalogue request field"); }

    private static string Text(JsonObject body, string name, int maximum)
    { if (body[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.String || value.GetValue<string>() is not { } text || text.EnumerateRunes().Count() > maximum) throw new ApiProblem(422, "Invalid " + name); return text; }

    private static string Hex(JsonObject body, string name, int length)
    { var text = Text(body, name, length); if (!Regex.IsMatch(text, "\\A[0-9a-f]{" + length.ToString(CultureInfo.InvariantCulture) + "}\\z", RegexOptions.CultureInvariant)) throw new ApiProblem(422, "Invalid " + name); return text; }

    private static long Integer(JsonObject body, string name, long minimum, long maximum, long? fallback = null)
    {
        if (!body.ContainsKey(name) && fallback is not null) return fallback.Value;
        if (body[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum) throw new ApiProblem(422, "Invalid " + name);
        return number;
    }

    private static bool Boolean(JsonObject body, string name, bool fallback)
    { if (!body.ContainsKey(name)) return fallback; if (body[name]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw new ApiProblem(422, "Invalid " + name); return body[name]!.GetValue<bool>(); }

    private static int QueryInteger(HttpContext context, string name, int minimum, int maximum, int fallback)
    { var value = context.Request.Query[name].ToString(); if (value.Length == 0) return fallback; if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < minimum || result > maximum) throw new ApiProblem(422, "Invalid " + name); return result; }

    private static (string Uid, string Digest, string Revision) ReviewIdentity(JsonObject body) => (Text(body, "uid", 100), Hex(body, "digest", 64), Hex(body, "revision", 64));

    private static string ReviewAction(JsonObject body)
    { var value = Text(body, "action", 16); if (value is not ("apply" or "ignore" or "separate")) throw new ApiProblem(422, "Invalid action"); return value; }
}
