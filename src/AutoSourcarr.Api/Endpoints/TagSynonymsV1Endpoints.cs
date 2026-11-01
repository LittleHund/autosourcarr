using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Catalogue.Tags;
using AutoSourcarr.Core.Matching.Decisions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the tag synonym routes need from the running app (null answers 503).</summary>
public sealed class TagSynonymsV1Services
{
    public required TagSynonyms Synonyms { get; init; }
    public required IDecisionLog Decisions { get; init; }
}

/// <summary>
/// Phase 8D tag synonyms under /api/v1: the built-in vocabulary, the user's synonyms and the effective map. The map is
/// applied when tags are compared, merged and used as edition evidence; stored source tags are never rewritten.
/// (Pairwise same/different tag reviews stay with the tag review routes.)
/// </summary>
internal static class TagSynonymsV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiJsonContext json, TagSynonymsV1Services? synonyms)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Tag synonyms are not available in this host.");
        IResult Answer(JsonObject value) => Results.Json(value, json.JsonObject);
        static JsonObject View(TagSynonymMap map) => new()
        {
            ["revision"] = map.Revision,
            ["built_in"] = Json(TagSynonyms.BuiltIn, includeOrigin: false),
            ["user"] = Json(map.User, includeOrigin: false),
            ["effective"] = Json(map.Effective, includeOrigin: true),
        };

        group.MapGet("/tag-synonyms", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (synonyms is null) return Unavailable();
                var owner = synonyms.Synonyms;
                return Answer(View(await Task.Run(() => owner.Load(), context.RequestAborted).ConfigureAwait(false)));
            }))
            .WithName("getTagSynonyms")
            .WithSummary("The tag synonym map: built-in synonyms, your synonyms and the effective list (namespace, from, to, origin), with its revision.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapPut("/tag-synonyms", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (synonyms is null) return Unavailable();
                JsonObject body;
                try { body = await ApiJson.ReadObjectAsync(context.Request, 262144).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (body["synonyms"] is not JsonArray items) return Problem(400, "synonyms must be a list of {namespace, from, to}");
                var values = new List<TagSynonym>();
                foreach (var item in items)
                {
                    if (item is not JsonObject entry) return Problem(400, "Each synonym is an object {namespace, from, to}");
                    string Field(string name) => entry[name] is JsonValue value && value.TryGetValue<string>(out var fieldText) ? fieldText : "";
                    values.Add(new TagSynonym(Field("namespace"), Field("from"), Field("to"), "user"));
                }
                var revision = body["revision"] is JsonValue given && given.TryGetValue<string>(out var revisionText) ? revisionText : null;
                var owner = synonyms.Synonyms; var decisions = synonyms.Decisions; var actor = MixedEntriesV1Endpoints.Actor(context);
                try { return Answer(View(await Task.Run(() => owner.Save(values, revision, decisions, actor), CancellationToken.None).ConfigureAwait(false))); }
                catch (TagSynonymException error) { return Problem(422, error.Message); }
                catch (InvalidOperationException error) { return Problem(409, error.Message); }
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("saveTagSynonyms")
            .WithSummary("Replace your tag synonyms (body: synonyms [{namespace, from, to}], optional revision from the last read). Mapping a built-in synonym to itself switches it off. Creator, character, parody and language names cannot be synonyms.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status422UnprocessableEntity);
    }

    private static JsonArray Json(IEnumerable<TagSynonym> values, bool includeOrigin) => new(values.Select(item =>
    {
        var value = new JsonObject { ["namespace"] = item.Namespace, ["from"] = item.From, ["to"] = item.To };
        if (includeOrigin) value["origin"] = item.Origin;
        return (JsonNode)value;
    }).ToArray());
}
