using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Accounts;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Matching.Scoring;
using AutoSourcarr.Core.Sources.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the entry-title, manual-search, matching-profile and matching-rule routes need (null answers 503).</summary>
public sealed class MatchingTitlesV1Services
{
    public required CatalogueTitles Titles { get; init; }
    public required EntryIngest Ingest { get; init; }
    public required MatchingPresetStore Presets { get; init; }
    public required MatchingPolicyStore Policies { get; init; }
    /// <summary>Live search of one source: (source id, query, page) → {mangas, hasNextPage} (SuwayomiClient.FetchSourceMangaAsync "SEARCH"). Null: local search only.</summary>
    public Func<string, string, int, CancellationToken, Task<JsonObject>>? SearchSource { get; init; }
    /// <summary>At most this many names are searched live per request (the rest only rank results).</summary>
    public int LiveQueryLimit { get; init; } = 4;
    public TimeSpan SearchTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Local search by the entry's own names: other entries that share an exact title, plus the names used.</summary>
    public JsonObject LocalSearch(long entryId, string query, bool allNames)
    {
        var canonical = Ingest.CanonicalEntryId(entryId);
        if (!Ingest.EntryExists(canonical)) throw new KeyNotFoundException("Entry not found");
        var names = Names(canonical, query, allNames);
        var ids = new List<long>();
        foreach (var name in names)
            ids.AddRange(query.Trim().Length > 0 && !allNames ? Ingest.SearchEntryIds(name.Query, 50) : Ingest.ExactSearchEntryIds(name.Query, 50));
        var entries = Ingest.EntrySummaries(ids.Distinct().Where(id => id != canonical).Take(100));
        return new JsonObject
        {
            ["entry_id"] = canonical,
            ["entries"] = new JsonArray(entries.Select(entry => (JsonNode)entry).ToArray()),
            ["queries"] = new JsonArray(names.Select(name => (JsonNode)name.Query).ToArray()),
            ["query_meta"] = Meta(names),
        };
    }

    /// <summary>
    /// Manual search of one source (0.4.5 search_source_for_manual): live search with the typed query or the entry's names,
    /// results merged by record id, ranked by the scorer against the entry, with each record's current owner. Partial results
    /// are returned when a query fails or the time budget runs out.
    /// </summary>
    public async Task<JsonObject> SourceSearchAsync(long entryId, string sourceId, string query, int page, bool allNames, CancellationToken cancellationToken)
    {
        var canonical = Ingest.CanonicalEntryId(entryId);
        if (!Ingest.EntryExists(canonical)) throw new KeyNotFoundException("Entry not found");
        if (SearchSource is null) throw new MatchingProfileException(503, "Live source search is not available in this host.");
        var names = Names(canonical, query, allNames);
        if (names.Count == 0) return new JsonObject { ["mangas"] = new JsonArray(), ["hasNextPage"] = false, ["error"] = "No searchable titles", ["queries"] = new JsonArray() };
        var entry = Ingest.GetEntry(canonical);
        var target = new MatchCandidate
        {
            Titles = Titles.EntryTitles(canonical),
            Authors = new[] { Text(entry["author"]), Text(entry["artist"]) }.Where(name => name.Length > 0).ToArray(),
        };
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(SearchTimeout);
        var live = names.Where(name => name.Kind != "transliteration" || !allNames).Take(Math.Max(1, LiveQueryLimit)).ToArray();
        var found = new Dictionary<long, JsonObject>(); var warnings = new JsonArray(); var liveQueries = new JsonArray(); var deferred = 0; var next = false;
        foreach (var name in live)
        {
            if (budget.IsCancellationRequested) { deferred++; continue; }
            liveQueries.Add((JsonNode)name.Query);
            JsonObject result;
            try { result = await SearchSource(sourceId, name.Query, page, budget.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { deferred++; continue; }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or TimeoutException)
            { warnings.Add((JsonNode)(name.Query + ": " + error.Message)); continue; }
            next |= result["hasNextPage"] is JsonValue more && more.TryGetValue<bool>(out var flag) && flag;
            foreach (var manga in (result["mangas"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var id = CatalogueTitles.MangaId(manga["id"]);
                if (id <= 0 || found.ContainsKey(id)) continue;
                var copy = (JsonObject)manga.DeepClone(); copy["matched_query"] = name.Query; found[id] = copy;
            }
        }
        var owners = new SourceDiscovery(Ingest).SourceRecordOwners(found.Keys.Select(id => (sourceId, id)));
        foreach (var (id, manga) in found)
        {
            var candidate = new MatchCandidate
            {
                Titles = new[] { Text(manga["title"]) }.Concat(WholeTitleAliases.Extract(Text(manga["description"]), [Text(manga["title"])])).ToArray(),
                Authors = new[] { Text(manga["author"]), Text(manga["artist"]) }.Where(author => author.Length > 0).ToArray(),
                Description = Text(manga["description"]),
            };
            var decision = MatchScorer.Score(candidate, target);
            manga["match_score"] = Math.Round(decision.Score, 4);
            manga["match_decision"] = decision.ToJson();
            manga["owner_entry_id"] = owners.TryGetValue((sourceId, id), out var owner) ? (JsonNode?)owner : null;
            manga["already_attached"] = owners.TryGetValue((sourceId, id), out var current) && current == canonical;
        }
        var ranked = found.Values.OrderByDescending(manga => manga["match_score"]!.GetValue<double>()).ToArray();
        return new JsonObject
        {
            ["mangas"] = new JsonArray(ranked.Select(manga => (JsonNode)manga).ToArray()),
            ["hasNextPage"] = next,
            ["queries"] = new JsonArray(names.Select(name => (JsonNode)name.Query).ToArray()),
            ["query_meta"] = Meta(names),
            ["live_queries"] = liveQueries,
            ["warnings"] = warnings,
            ["deferred_count"] = deferred,
            ["cached_results"] = 0,
        };
    }

    private List<SearchName> Names(long entryId, string query, bool allNames)
    {
        if (allNames) return Ingest.ExpandedSearchNames(entryId, query).ToList();
        var names = new List<SearchName>();
        if (query.Trim().Length > 0) names.Add(new SearchName(query.Trim(), "manual", ""));
        return names;
    }

    private static JsonArray Meta(IEnumerable<SearchName> names) => new(names.Select(name => (JsonNode)new JsonObject
    {
        ["query"] = name.Query, ["kind"] = name.Kind, ["language"] = name.Language,
    }).ToArray());

    private static string Text(JsonNode? node) => CatalogueTitles.Text(node).Trim();
}

/// <summary>
/// Phase 8A routes under /api/v1: the entry main title and alternative titles (0.4.5 /api/admin/entries/{id}/preferred-title,
/// /aliases), manual search (GET: by the entry's own names in the catalogue; POST {query, source_id?, page?, all_names?}:
/// one source live, or the catalogue), matching profiles (/matching-presets: ready-made, simple, advanced) and matching
/// rules (/matching-policies). DELETE /entries/{entryId}/manual-titles is mapped by ManualDecisionsV1Endpoints.
/// Every change records its actor in the decision log.
/// </summary>
internal static class MatchingTitlesV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, MatchingTitlesV1Services? services, ApiJsonContext json)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Unavailable() => Problem(503, "Matching titles and profiles are not available in this host.");
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        IResult Guard(Func<IResult> action)
        {
            try { return action(); }
            catch (Exception error) when (Status(error) is { } status) { return Problem(status, error.Message); }
        }
        async Task<IResult> GuardAsync(Func<Task<IResult>> action)
        {
            try { return await action().ConfigureAwait(false); }
            catch (Exception error) when (Status(error) is { } status) { return Problem(status, error.Message); }
        }

        group.MapPost("/entries/{entryId}/preferred-title", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services is not { } matching) return Unavailable();
                if (!EntryId(context, out var entryId)) return Problem(400, "Invalid entry id.");
                return await GuardAsync(async () =>
                {
                    var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
                    var id = matching.Titles.SetPreferredTitle(entryId, Text(body["title"]), Actor(context));
                    return Answer(matching.Ingest.GetEntry(id));
                }).ConfigureAwait(false);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("setEntryPreferredTitle")
            .WithSummary("Set the entry's main title: {title}. A manual main title is kept for this exact entry until it is removed or an explicit reset; it is recorded in the decision log. Returns the entry.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/entries/{entryId}/aliases", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services is not { } matching) return Unavailable();
                if (!EntryId(context, out var entryId)) return Problem(400, "Invalid entry id.");
                return await GuardAsync(async () =>
                {
                    var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
                    var id = matching.Titles.AddManualAlias(entryId, Text(body["title"]), Text(body["language"]), Actor(context));
                    return Answer(matching.Ingest.GetEntry(id));
                }).ConfigureAwait(false);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("addEntryAlias")
            .WithSummary("Add a manual alternative title: {title, language?}. The whole title is kept (never split) and becomes matching evidence for this entry. Returns the entry.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/entries/{entryId}/manual-search", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services is not { } matching) return Unavailable();
                if (!EntryId(context, out var entryId)) return Problem(400, "Invalid entry id.");
                return Guard(() => Answer(matching.LocalSearch(entryId, "", allNames: true)));
            }))
            .WithName("getEntryManualSearch")
            .WithSummary("Search the catalogue by the entry's own names (main title, alternative and source titles, romanised forms): other entries with an exact shared title, plus the names used.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/entries/{entryId}/manual-search", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services is not { } matching) return Unavailable();
                if (!EntryId(context, out var entryId)) return Problem(400, "Invalid entry id.");
                return await GuardAsync(async () =>
                {
                    var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
                    var query = Text(body["query"]); var sourceId = Text(body["source_id"]);
                    var page = body["page"] is JsonValue pageValue && pageValue.TryGetValue<int>(out var number) ? number : 1;
                    if (page < 1 || page > 1000) return Problem(400, "page must be between 1 and 1000.");
                    var allNames = body["all_names"] is not JsonValue all || !all.TryGetValue<bool>(out var flag) || flag;
                    if (query.Length > 300) return Problem(400, "The search text is limited to 300 characters.");
                    return sourceId.Length == 0 ? Answer(matching.LocalSearch(entryId, query, allNames))
                        : Answer(await matching.SourceSearchAsync(entryId, sourceId, query, page, allNames, context.RequestAborted).ConfigureAwait(false));
                }).ConfigureAwait(false);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("searchEntryManual")
            .WithSummary("Manual search: {query, source_id?, page?, all_names?}. With source_id, search that source live with the typed query and (all_names, default true) the entry's names; results are ranked by the matching scorer against the entry and show their current owner. Without source_id, search the catalogue.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/matching-presets", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services is not { } matching) return Unavailable();
                return Guard(() => Answer(matching.Presets.Configuration()));
            }))
            .WithName("getMatchingPresets")
            .WithSummary("Matching profiles: ready-made presets (fixed), saved simple and advanced profiles with their effective scorer options, the preset revision and the default source ranking (official publisher sources, then health, then quality).")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPost("/matching-presets", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services is not { } matching) return Unavailable();
                return await GuardAsync(async () =>
                {
                    var body = await ApiJson.ReadObjectAsync(context.Request, 65536).ConfigureAwait(false);
                    return Answer(matching.Presets.Save(body, null, Actor(context)), 201);
                }).ConfigureAwait(false);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("createMatchingPreset")
            .WithSummary("Save a new matching profile: {revision, name, description?, rule} (0.4.5 rule {enabled, checks}) or {revision, name, description?, kind: simple|advanced, settings}. The title floor never goes below 94%.")
            .Produces<JsonObject>(StatusCodes.Status201Created)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPut("/matching-presets/{presetId}", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services is not { } matching) return Unavailable();
                var presetId = Route(context, "presetId");
                return await GuardAsync(async () =>
                {
                    var body = await ApiJson.ReadObjectAsync(context.Request, 65536).ConfigureAwait(false);
                    return Answer(matching.Presets.Save(body, presetId, Actor(context)));
                }).ConfigureAwait(false);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("updateMatchingPreset")
            .WithSummary("Change a saved matching profile (same body as create). Ready-made profiles are copied with Save as new instead.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapDelete("/matching-presets/{presetId}", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services is not { } matching) return Unavailable();
                var presetId = Route(context, "presetId");
                return await GuardAsync(async () =>
                {
                    var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
                    return Answer(matching.Presets.Delete(presetId, Text(body["revision"]), Actor(context)));
                }).ConfigureAwait(false);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("deleteMatchingPreset")
            .WithSummary("Delete a saved matching profile: body {revision}. Ready-made profiles cannot be deleted.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapGet("/matching-policies", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services is not { } matching) return Unavailable();
                return Guard(() => Answer(matching.Policies.Configuration(matching.Presets)));
            }))
            .WithName("getMatchingPolicies")
            .WithSummary("Matching rules per source and source group (internal / external matching), with their revision, the sources and groups they can name, and the matching profiles.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapPut("/matching-policies/{scope}/{subject}/{direction}", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services is not { } matching) return Unavailable();
                var key = new MatchingRuleKey(Route(context, "scope"), Route(context, "subject"), Route(context, "direction"));
                return await GuardAsync(async () =>
                {
                    var body = await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false);
                    if (key.Scope is not ("source" or "group") || key.Direction is not ("internal" or "external"))
                        return Problem(400, "Unknown source/group or matching direction.");
                    if (!matching.Policies.SubjectExists(key)) return Problem(404, "Source or group no longer exists.");
                    var rule = body["rule"] is JsonObject value ? (JsonObject)value.DeepClone() : null;
                    matching.Policies.Write(key, rule, Text(body["revision"]), Actor(context), Text(body["reason"]));
                    return Answer(matching.Policies.Configuration(matching.Presets));
                }).ConfigureAwait(false);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("saveMatchingPolicy")
            .WithSummary("Save the matching rule of one source or group and direction: {revision, rule: {enabled, checks} | null (inherit), reason?}. Title checks stay at 94% or more; identity and edition safeguards always apply.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);
    }

    /// <summary>The decision actor of a request: the signed-in account, the API key or the admin token.</summary>
    private static DecisionActor Actor(HttpContext context)
    {
        var caller = AccessControlMiddleware.CallerOf(context);
        return caller.Via switch
        {
            "key" => DecisionActor.Key(caller.Name.Length > 0 ? caller.Name : caller.KeyId ?? "key"),
            "admin-token" => DecisionActor.Key("admin-token"),
            "none" => DecisionActor.User("admin"),
            _ => DecisionActor.User(caller.Name.Length > 0 ? caller.Name : Roles.Name(caller.Role)),
        };
    }

    private static int? Status(Exception error) => error switch
    {
        ApiProblem problem => problem.StatusCode,
        MatchingProfileException profile => profile.Status,
        CatalogueTitleException title => title.Status,
        MatchingRuleConflictException => 409,
        KeyNotFoundException => 404,
        ArgumentException => 400,
        _ => null,
    };

    private static string Route(HttpContext context, string name) => context.Request.RouteValues[name]?.ToString() ?? "";

    private static bool EntryId(HttpContext context, out long entryId) =>
        long.TryParse(Route(context, "entryId"), NumberStyles.None, CultureInfo.InvariantCulture, out entryId) && entryId > 0;

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
