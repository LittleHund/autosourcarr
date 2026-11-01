using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Connections;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>What the Connections routes need from the running app (null answers 503).</summary>
public sealed class ConnectionsV1Services
{
    /// <summary>Saved connections in config.json, the registered types and the last test results.</summary>
    public required ConnectionStore Store { get; init; }

    /// <summary>
    /// Queues the one-time import (CON-3) of this main Suwayomi connection on the job queue (true: replace a stored plan)
    /// and returns the task id. Null: the import is not available in this host (503).
    /// </summary>
    public Func<ConnectionConfig, bool, string>? StartImport { get; init; }

    /// <summary>The stored import plan, or null when none was made yet. Null delegate: not available (503).</summary>
    public Func<SuwayomiImportPlan?>? ImportPlan { get; init; }
}

/// <summary>
/// Phase 7: the one Connections menu (CON-1) under /api/v1: list types and connections, add, change (including the
/// enabled switch), delete and test (saved or unsaved values). Every route is admin-only. Secret values never leave
/// the server; they are listed as <c>{"set": true}</c> markers (CON-5). The old /api/admin/services routes
/// (compose-internal services) are not ported.
/// </summary>
internal static class ConnectionsV1Endpoints
{
    internal static void Map(RouteGroupBuilder group, ApiV1Services services, ApiJsonContext json)
    {
        IResult Problem(int status, string detail) => Results.Json(new ProblemResponse(detail), json.ProblemResponse, statusCode: status);
        IResult Answer(JsonObject value, int status = 200) => Results.Json(value, json.JsonObject, statusCode: status);
        IResult Unavailable() => Problem(503, "Connections are not available in this host.");
        string Id(HttpContext context) => context.Request.RouteValues["connectionId"]?.ToString() ?? "";

        group.MapGet("/connections", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services.Connections is not { } connections) return Unavailable();
                return Guarded(Problem, () => Answer(Listing(connections.Store)));
            }))
            .WithName("getConnections")
            .WithSummary("The Connections menu: every connection type with its fields, the saved connections (secret values as {set} markers), the read-only ones derived from Settings, the last test of each, and the revision to send with a change.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/connections", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Connections is not { } connections) return Unavailable();
                JsonObject body;
                try { body = await ApiJson.ReadObjectAsync(context.Request, 65536).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (Text(body, "revision") is not { } revision || Text(body, "type") is not { } type)
                    return Problem(400, "Send the revision and the connection type.");
                if (!OptionalText(body, "label", out var label) || !OptionalBool(body, "enabled", out var enabled) || !OptionalObject(body, "values", out var values))
                    return Problem(400, "label must be text, enabled true or false and values an object.");
                return Guarded(Problem, () =>
                {
                    var change = connections.Store.Add(revision, type, label, enabled ?? true, values);
                    return Answer(Change(change, Notice(change.Connection?.Type, true, change.FirstSaved)), 201);
                });
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("addConnection")
            .WithSummary("Add a connection: {revision, type, label?, enabled?, values}. Values are checked by field kind (URL, whole number, on/off, required); a type that allows one connection refuses a second, and a Suwayomi connection is refused (409) while MAIN_SUWAYOMI_URL is set in Settings. The answer's notice says what happens next (solver: the source host restarts; Suwayomi: the change feed reconnects; the first saved connection: how to roll back to 0.4.x).")
            .Produces<JsonObject>(StatusCodes.Status201Created)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPut("/connections/{connectionId}", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Connections is not { } connections) return Unavailable();
                JsonObject body;
                try { body = await ApiJson.ReadObjectAsync(context.Request, 65536).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (Text(body, "revision") is not { } revision) return Problem(400, "Send the revision the change is based on.");
                if (!OptionalText(body, "label", out var label) || !OptionalBool(body, "enabled", out var enabled) || !OptionalObject(body, "values", out var values))
                    return Problem(400, "label must be text, enabled true or false and values an object.");
                var id = Id(context);
                return Guarded(Problem, () =>
                {
                    var change = connections.Store.Update(id, revision, label, enabled, values);
                    return Answer(Change(change, Notice(change.Connection?.Type, change.Effective, false)));
                });
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("updateConnection")
            .WithSummary("Change a saved connection: {revision, label?, enabled?, values?}. Fields left out keep their value; a secret sent as {set:true} or empty text is kept unless an address now points at another server (then it must be entered again); null clears a field. The answer's notice says what happens next.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapDelete("/connections/{connectionId}", (Func<HttpContext, IResult>)(context =>
            {
                if (services.Connections is not { } connections) return Unavailable();
                var revision = context.Request.Query["revision"].ToString();
                if (revision.Length == 0) return Problem(400, "Send the revision the change is based on (?revision=).");
                var id = Id(context);
                return Guarded(Problem, () =>
                {
                    var type = connections.Store.TypeOf(id);
                    var next = connections.Store.Delete(id, revision);
                    return Answer(new JsonObject { ["revision"] = next, ["deleted"] = id, ["notice"] = Notice(type, true, false) });
                });
            }))
            .WithName("deleteConnection")
            .WithSummary("Delete a saved connection (?revision= names the revision it is based on). Connections from Settings are cleared on the Settings page.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/connections/test", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Connections is not { } connections) return Unavailable();
                JsonObject body;
                try { body = await ApiJson.ReadObjectAsync(context.Request, 65536).ConfigureAwait(false); }
                catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                if (Text(body, "type") is not { } type) return Problem(400, "Send the connection type and its values.");
                if (!OptionalObject(body, "values", out var values) || !OptionalText(body, "id", out var basedOn))
                    return Problem(400, "values must be an object and id text.");
                return await GuardedAsync(Problem, async () =>
                    Answer(Result(await connections.Store.TestValuesAsync(type, values, basedOn, context.RequestAborted).ConfigureAwait(false), null))).ConfigureAwait(false);
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("testConnectionValues")
            .WithSummary("Test values that are not saved yet: {type, values, id?}. With id, the values are applied onto that connection first, so a kept secret is tested with its saved value, unless an address now points at another server (then the secret must be sent again). Nothing is saved.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/connections/{connectionId}/test", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Connections is not { } connections) return Unavailable();
                var id = Id(context);
                return await GuardedAsync(Problem, async () =>
                {
                    var result = await connections.Store.TestAsync(id, context.RequestAborted).ConfigureAwait(false);
                    return Answer(Result(result, connections.Store.LastTest(id)));
                }).ConfigureAwait(false);
            }))
            .WithName("testConnection")
            .WithSummary("Test a saved connection (or one from Settings) without changing anything on the remote side, within 15 seconds: ok, a short message and details such as the version. The result is kept as the connection's last test.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/connections/{connectionId}/import", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Connections is not { } connections) return Unavailable();
                if (connections.StartImport is not { } start) return Problem(503, "The Suwayomi import is not available in this host.");
                var body = new JsonObject();
                if (context.Request.ContentLength > 0 || context.Request.ContentType is { Length: > 0 })
                {
                    try { body = await ApiJson.ReadObjectAsync(context.Request, 1024).ConfigureAwait(false); }
                    catch (ApiProblem error) { return Problem(error.StatusCode, error.Message); }
                }
                if (!OptionalBool(body, "force", out var force)) return Problem(400, "force must be true or false.");
                var id = Id(context);
                return Guarded(Problem, () =>
                {
                    if (MainSuwayomiFor(connections.Store, id, out var refusal) is not { } main) return refusal!;
                    var taskId = start(main, force ?? false);
                    return Answer(new JsonObject
                    {
                        ["task_id"] = taskId, ["status"] = "queued", ["note"] = SuwayomiImport.AwaitingMatchingNote,
                    }, 202);
                });
            }))
            .Accepts<JsonObject>("application/json")
            .WithName("importSuwayomiLibrary")
            .WithSummary("One-time import of the main Suwayomi library (Suwayomi connections only): {force?}. Queues a task that reads the library (read-only) and stores the plan; POST /api/v1/imports/suwayomi-plan then creates the catalogue entries through the import pipeline. With a plan stored, the task does nothing unless force is true. Answers 202 with the task id.")
            .Produces<JsonObject>(StatusCodes.Status202Accepted)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/connections/{connectionId}/import", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services.Connections is not { } connections) return Unavailable();
                if (connections.ImportPlan is not { } read) return Problem(503, "The Suwayomi import is not available in this host.");
                var id = Id(context);
                return Guarded(Problem, () =>
                {
                    if (MainSuwayomiFor(connections.Store, id, out var refusal) is null) return refusal!;
                    SuwayomiImportPlan? plan;
                    try { plan = read(); }
                    catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
                    { return Problem(409, "The saved import plan could not be read; import again with force."); }
                    return Answer(new JsonObject
                    {
                        ["plan"] = plan is null ? null : new JsonObject
                        {
                            ["status"] = plan.Status, ["saved_at"] = plan.CreatedAt, ["server_url"] = plan.ServerUrl, ["counts"] = plan.Counts(),
                        },
                        ["note"] = SuwayomiImport.AwaitingMatchingNote,
                    });
                });
            }))
            .WithName("getSuwayomiImport")
            .WithSummary("The stored one-time import plan of the main Suwayomi (Suwayomi connections only): status (awaiting-matching), saved_at, server_url and counts (total, sources, per source); plan is null before the first import.")
            .Produces<JsonObject>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status404NotFound)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict)
            .Produces<ProblemResponse>(StatusCodes.Status503ServiceUnavailable);

        // The import reads the same server as the library: the main Suwayomi (Settings first, else the saved one).
        ConnectionConfig? MainSuwayomiFor(ConnectionStore store, string id, out IResult? refusal)
        {
            refusal = null;
            var connection = store.Find(id);
            if (connection is null) { refusal = Problem(404, "No connection " + id + "."); return null; }
            if (connection.Type != ConnectionStore.MainSuwayomiType)
            { refusal = Problem(409, "Only a Suwayomi connection imports a library."); return null; }
            if (store.MainSuwayomi() is not { } main || main.Id != connection.Id)
            {
                refusal = Problem(409, connection.Enabled
                    ? "The library uses the Suwayomi from Settings (MAIN_SUWAYOMI_URL); import from that connection."
                    : "This connection is switched off; switch it on to import from it.");
                return null;
            }
            return main;
        }
    }

    /// <summary>The CON-5 notice of a change: what restarts, and (first saved connection) how to roll back to 0.4.x.</summary>
    internal static string? Notice(string? type, bool effective, bool firstSaved)
    {
        var parts = new List<string>();
        if (effective && ConnectionStore.ChangeNotice(type) is { } notice) parts.Add(notice);
        if (firstSaved) parts.Add(ConnectionStore.RollbackNotice);
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    internal static JsonObject Listing(ConnectionStore store)
    {
        var listing = store.List();
        var types = new JsonArray();
        foreach (var type in store.Types.All)
        {
            var fields = new JsonArray();
            foreach (var field in type.Fields)
                fields.Add(new JsonObject
                {
                    ["key"] = field.Key, ["label"] = field.Label, ["kind"] = Kind(field.Kind), ["required"] = field.Required,
                    ["default"] = field.Kind == ConnectionFieldKind.Secret ? null : field.Default, ["help"] = field.Help,
                });
            types.Add(new JsonObject
            {
                ["type"] = type.Type, ["label"] = type.Label, ["purpose"] = type.Purpose, ["multiple"] = type.Multiple, ["fields"] = fields,
            });
        }
        var items = new JsonArray();
        foreach (var connection in listing.Connections)
            items.Add(View(connection));
        return new JsonObject
        {
            ["revision"] = listing.Revision,
            ["test_deadline_seconds"] = store.TestDeadline.TotalSeconds,
            ["types"] = types,
            ["connections"] = items,
        };
    }

    private static string Kind(ConnectionFieldKind kind) => kind switch
    {
        ConnectionFieldKind.Url => "url",
        ConnectionFieldKind.Secret => "secret",
        ConnectionFieldKind.Integer => "integer",
        ConnectionFieldKind.Boolean => "boolean",
        _ => "text",
    };

    private static JsonObject View(ConnectionView connection) => new()
    {
        ["id"] = connection.Id, ["type"] = connection.Type, ["label"] = connection.Label, ["enabled"] = connection.Enabled,
        ["source"] = connection.Source, ["editable"] = connection.Editable, ["type_known"] = connection.TypeKnown,
        ["values"] = connection.Values.DeepClone(), ["last_test"] = Test(connection.LastTest),
    };

    private static JsonObject? Test(ConnectionTestRecord? test) => test is null ? null : new JsonObject
    {
        ["at"] = test.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), ["ok"] = test.Ok, ["message"] = test.Message,
    };

    private static JsonObject Change(ConnectionChange change, string? notice) => new()
    {
        ["revision"] = change.Revision, ["connection"] = change.Connection is null ? null : View(change.Connection), ["notice"] = notice,
    };

    private static JsonObject Result(ConnectionTestResult result, ConnectionTestRecord? last) => new()
    {
        ["ok"] = result.Ok, ["message"] = result.Message, ["details"] = result.Details?.DeepClone(), ["last_test"] = Test(last),
    };

    private static string? Text(JsonObject body, string name) =>
        body[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static bool OptionalText(JsonObject body, string name, out string? text)
    {
        text = null;
        if (body[name] is null) return true;
        if (body[name] is JsonValue value && value.TryGetValue<string>(out var found)) { text = found; return true; }
        return false;
    }

    private static bool OptionalBool(JsonObject body, string name, out bool? flag)
    {
        flag = null;
        if (body[name] is null) return true;
        if (body[name] is JsonValue value && value.TryGetValue<bool>(out var found)) { flag = found; return true; }
        return false;
    }

    private static bool OptionalObject(JsonObject body, string name, out JsonObject? item)
    {
        item = body[name] as JsonObject;
        return body[name] is null || item is not null;
    }

    private static IResult Guarded(Func<int, string, IResult> problem, Func<IResult> action)
    {
        try { return action(); }
        catch (Exception error) when (Status(error) is { } status) { return problem(status, Message(error)); }
    }

    private static async Task<IResult> GuardedAsync(Func<int, string, IResult> problem, Func<Task<IResult>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception error) when (Status(error) is { } status) { return problem(status, Message(error)); }
    }

    private static int? Status(Exception error) => error switch
    {
        SettingsValidationException => 400,
        ConnectionNotFoundException => 404,
        SettingsConflictException or ConfigurationException or IOException or UnauthorizedAccessException => 409,
        _ => null,
    };

    private static string Message(Exception error) => error switch
    {
        ConfigurationException => error.Message + " No connections were changed.",
        IOException => "Could not read or write config.json. Check that the config folder is mounted and writable; no connections were changed.",
        UnauthorizedAccessException => "config.json is not writable. Check the config folder permissions; no connections were changed.",
        _ => error.Message,
    };
}
