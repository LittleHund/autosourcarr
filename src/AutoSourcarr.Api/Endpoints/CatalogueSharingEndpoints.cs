using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>Original Web UI sharing routes. All blocking work owns a bounded physical I/O lease.</summary>
public static class CatalogueSharingEndpoints
{
    public static RouteGroupBuilder MapCatalogueSharing(this IEndpointRouteBuilder endpoints, CatalogueExchange exchange,
        PortableIdentityResolver identities, CatalogueReviewTasks reviews, Func<AutoSourcarrSettings> settings, SemaphoreSlim adminLock, BoundedWorkScheduler io)
    {
        var api = new Handler(exchange, identities, reviews, adminLock, io);
        var group = endpoints.MapGroup("/api/admin").AddEndpointFilter(new AdminAuthorizationFilter(settings));
        group.MapGet("/portable-identities/resolve", (Func<HttpContext, Task<IResult>>)api.ResolveAsync);
        group.MapGet("/catalog-sharing/status", (Func<HttpContext, Task<IResult>>)api.StatusAsync);
        group.MapPost("/catalog-sharing/export", (Func<HttpContext, Task<IResult>>)api.ExportAsync);
        group.MapPost("/catalog-sharing/preview", (Func<HttpContext, Task<IResult>>)api.PreviewAsync).WithMetadata(ApiJson.BoundedUploadMetadata);
        group.MapGet("/catalog-sharing/stages/{stage_id}", (Func<HttpContext, Task<IResult>>)api.StageAsync);
        group.MapPost("/catalog-sharing/apply", (Func<HttpContext, Task<IResult>>)api.ApplyAsync);
        group.MapGet("/catalog-sharing/decisions", (Func<HttpContext, Task<IResult>>)api.DecisionsAsync);
        group.MapGet("/catalog-sharing/records", (Func<HttpContext, Task<IResult>>)api.RecordsAsync);
        group.MapPost("/catalog-sharing/decisions/review", (Func<HttpContext, Task<IResult>>)api.ReviewDecisionAsync);
        group.MapPost("/catalog-sharing/records/review", (Func<HttpContext, Task<IResult>>)api.ReviewRecordAsync);
        group.MapPost("/catalog-sharing/records/link", (Func<HttpContext, Task<IResult>>)api.LinkAsync);
        return group;
    }
    private sealed class Handler(CatalogueExchange exchange, PortableIdentityResolver identities, CatalogueReviewTasks reviews, SemaphoreSlim admin, BoundedWorkScheduler io)
    {
        public async Task<IResult> ResolveAsync(HttpContext context)
        {
            var reference = context.Request.Query["reference"].ToString(); if (reference.Length is < 1 or > 6000) throw new ApiProblem(422, "Enter a complete ASC Media ID");
            return await ReadAsync(context, () => identities.Resolve(reference)).ConfigureAwait(false);
        }
        public Task<IResult> StatusAsync(HttpContext context) => ReadAsync(context, exchange.Status);
        public async Task<IResult> ExportAsync(HttpContext context)
        {
            var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); Fields(body, "after", "limit", "include_decisions");
            var after = Integer(body, "after", 0, long.MaxValue, 0); var limit = (int)Integer(body, "limit", 1, 5000, 2000); var decisions = Boolean(body, "include_decisions", true);
            return await WriteAsync(context, () => exchange.Export(after, limit, decisions)).ConfigureAwait(false);
        }
        public async Task<IResult> PreviewAsync(HttpContext context)
        {
            var bytes = await ApiJson.ReadUploadAsync(context.Request, CatalogueFormat.MaximumBytes,
                "Catalogue file exceeds 64 MiB; export smaller batches", "Catalogue upload timed out; select the file again").ConfigureAwait(false);
            return await WriteAsync(context, () => exchange.Preview(bytes)).ConfigureAwait(false);
        }
        public Task<IResult> StageAsync(HttpContext context)
        {
            var id = context.Request.RouteValues["stage_id"]?.ToString() ?? "";
            var offset = QueryInteger(context, "offset", 0, int.MaxValue, 0); var limit = QueryInteger(context, "limit", 1, 200, 100);
            return ReadAsync(context, () => exchange.Stage(id, offset, limit));
        }
        public async Task<IResult> ApplyAsync(HttpContext context)
        {
            var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); Fields(body, "id", "revision", "include_decisions");
            var id = Hex(body, "id", 32); var revision = Hex(body, "revision", 64); var decisions = Boolean(body, "include_decisions", true);
            return await WriteAsync(context, () => exchange.Apply(id, revision, decisions)).ConfigureAwait(false);
        }
        public Task<IResult> DecisionsAsync(HttpContext context) => ReadAsync(context, () => exchange.Decisions(QueryInteger(context, "offset", 0, int.MaxValue, 0), QueryInteger(context, "limit", 1, 50, 30)));
        public Task<IResult> RecordsAsync(HttpContext context) => ReadAsync(context, () => exchange.Records(QueryInteger(context, "offset", 0, int.MaxValue, 0), QueryInteger(context, "limit", 1, 50, 30)));
        public async Task<IResult> ReviewDecisionAsync(HttpContext context)
        {
            var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); Fields(body, "uid", "digest", "revision", "action");
            var (uid, digest, revision) = ReviewIdentity(body); var action = ReviewAction(body);
            if (action is not ("apply" or "ignore")) throw new ApiProblem(409, "Choose Apply or Keep local");
            await admin.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                var current = await DomainAsync(context, () => exchange.Decision(uid, digest)).ConfigureAwait(false);
                if (current["state"]!.GetValue<string>() is "applied" or "ignored") return Results.Json(new JsonObject { ["already_reviewed"] = true, ["state"] = current["state"]!.DeepClone() });
                if (current["revision"]!.GetValue<string>() != revision) throw new ApiProblem(409, "Retained choice or local records changed; refresh before applying");
                if (action == "apply" && !current["can_apply"]!.GetValue<bool>()) throw new ApiProblem(409, current["message"]!.GetValue<string>());
                return Results.Json(reviews.Schedule(uid, digest, revision, action));
            }
            finally { admin.Release(); }
        }
        public async Task<IResult> ReviewRecordAsync(HttpContext context)
        {
            var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); Fields(body, "uid", "digest", "revision", "action");
            var (uid, digest, revision) = ReviewIdentity(body); var action = ReviewAction(body);
            return await WriteAsync(context, () => exchange.ReviewRecord(uid, digest, revision, action)).ConfigureAwait(false);
        }
        public async Task<IResult> LinkAsync(HttpContext context)
        {
            var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false); Fields(body, "uid", "digest", "revision", "target");
            var (uid, digest, revision) = ReviewIdentity(body); var target = Integer(body, "target", 1, long.MaxValue);
            await admin.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                var choice = await DomainAsync(context, () => exchange.PrepareLink(uid, digest, revision, target)).ConfigureAwait(false);
                return Results.Json(reviews.Schedule(choice["uid"]!.GetValue<string>(), choice["digest"]!.GetValue<string>(), choice["revision"]!.GetValue<string>()));
            }
            finally { admin.Release(); }
        }
        private async Task<IResult> ReadAsync(HttpContext context, Func<JsonObject> operation) => Results.Json(await DomainAsync(context, operation).ConfigureAwait(false));
        private async Task<IResult> WriteAsync(HttpContext context, Func<JsonObject> operation)
        {
            await admin.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try { return Results.Json(await DomainAsync(context, operation).ConfigureAwait(false)); }
            finally { admin.Release(); }
        }
        private async Task<JsonObject> DomainAsync(HttpContext context, Func<JsonObject> operation)
        {
            try { return await io.RunAsync(operation, context.RequestAborted).ConfigureAwait(false); }
            catch (KeyNotFoundException) { throw new ApiProblem(404, "Work or record no longer exists"); }
            catch (ArgumentException error) { throw new ApiProblem(409, error.Message); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("Native gallery metadata/evidence provider", StringComparison.Ordinal) || error.Message.StartsWith("A visible reviewed task", StringComparison.Ordinal)) { throw new ApiProblem(503, error.Message); }
        }
        private static void Fields(JsonObject body, params string[] allowed)
        { if (body.Any(pair => !allowed.Contains(pair.Key, StringComparer.Ordinal))) throw new ApiProblem(422, "Unsupported catalogue request field"); }
        private static string Text(JsonObject body, string name, int maximum)
        { if (body[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.String || value.GetValue<string>() is not { } text || text.EnumerateRunes().Count() > maximum) throw new ApiProblem(422, "Invalid " + name); return text; }
        private static string Hex(JsonObject body, string name, int length)
        { var text = Text(body, name, length); if (!Regex.IsMatch(text, "\\A[0-9a-f]{" + length + "}\\z", RegexOptions.CultureInvariant)) throw new ApiProblem(422, "Invalid " + name); return text; }
        private static long Integer(JsonObject body, string name, long minimum, long maximum, long? fallback = null)
        { if (!body.ContainsKey(name) && fallback is not null) return fallback.Value; if (body[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum) throw new ApiProblem(422, "Invalid " + name); return number; }
        private static bool Boolean(JsonObject body, string name, bool fallback)
        { if (!body.ContainsKey(name)) return fallback; if (body[name]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw new ApiProblem(422, "Invalid " + name); return body[name]!.GetValue<bool>(); }
        private static int QueryInteger(HttpContext context, string name, int minimum, int maximum, int fallback)
        { var value = context.Request.Query[name].ToString(); if (value.Length == 0) return fallback; if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < minimum || result > maximum) throw new ApiProblem(422, "Invalid " + name); return result; }
        private static (string Uid, string Digest, string Revision) ReviewIdentity(JsonObject body) => (Text(body, "uid", 100), Hex(body, "digest", 64), Hex(body, "revision", 64));
        private static string ReviewAction(JsonObject body)
        { var value = Text(body, "action", 16); if (value is not ("apply" or "ignore" or "separate")) throw new ApiProblem(422, "Invalid action"); return value; }
    }
}
