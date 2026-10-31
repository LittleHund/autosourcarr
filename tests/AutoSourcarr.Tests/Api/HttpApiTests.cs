using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Text;
using AutoSourcarr.Core.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace AutoSourcarr.Tests;

public static class HttpApiTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("HTTP API: retained Unicode and proxy identity keep saved preference keys", () =>
        {
            Check.Equal("strasse i̇σσ𐐨", UnicodeCaseFold.Fold("Straße İΣς𐐀"));
            using var document = JsonDocument.Parse(File.ReadAllText(ConfigurationTests.Fixture("ui-identity.json")));
            foreach (var row in document.RootElement.EnumerateArray())
            {
                var context = new DefaultHttpContext();
                foreach (var header in row.GetProperty("headers").EnumerateObject()) context.Request.Headers[header.Name] = header.Value.GetString();
                var identity = UiIdentity.Resolve(context.Request, new AutoSourcarrSettings { AutheliaUserHeader = row.GetProperty("custom_header").GetString()! });
                Check.JsonEqual(row.GetProperty("expected"), JsonSerializer.SerializeToElement(identity.Context()));
            }
            Console.WriteLine("UI identity parity cases " + document.RootElement.GetArrayLength());
        });
        suite.Add("HTTP API: profile preference reads and merges use SQLite and preserve UTF8 limits", () =>
        {
            using var temp = new Sandbox(); using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize(); var io = new BoundedWorkScheduler(1, 8);
            try
            {
                var store = new SettingsRepository(database); using var pipeline = new Pipeline(preferences: store, io: io); using var client = pipeline.Server.CreateClient();
                client.DefaultRequestHeaders.Add("Remote-User", "Straße");
                Check.Equal(HttpStatusCode.Unauthorized, client.GetAsync("/api/admin/ui-preferences").GetAwaiter().GetResult().StatusCode);
                client.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
                var result = Body(client.PutAsync("/api/admin/ui-preferences", new StringContent("{\"values\":{\"theme\":\"dark\"}}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult());
                Check.Equal("Straße", result["user"]!.GetValue<string>());
                result = Body(client.PutAsync("/api/admin/ui-preferences", new StringContent("{\"values\":{\"view\":\"grid\"}}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult());
                Check.Equal(2, result["values"]!.AsObject().Count); Check.Equal("dark", store.UserPreferences("authelia:strasse")["theme"]!.GetValue<string>());
                var multibyte = new string('界', 21000);
                var json = JsonSerializer.Serialize(new { values = new { note = multibyte } });
                Check.Equal(HttpStatusCode.OK, client.PutAsync("/api/admin/ui-preferences", new StringContent(json, Encoding.UTF8, "application/json")).GetAwaiter().GetResult().StatusCode);
                Check.Equal(multibyte, store.UserPreferences("authelia:strasse")["note"]!.GetValue<string>());
                json = JsonSerializer.Serialize(new { values = new { note = new string('界', 24000) } });
                Check.Equal(HttpStatusCode.BadRequest, client.PutAsync("/api/admin/ui-preferences", new StringContent(json, Encoding.UTF8, "application/json")).GetAwaiter().GetResult().StatusCode);
                Check.Equal(multibyte, store.UserPreferences("authelia:strasse")["note"]!.GetValue<string>());
                Check.Equal(0, pipeline.Tasks.Snapshot()["running"]!.GetValue<int>());
                Check.Equal(0, pipeline.Tasks.Snapshot()["completed"]!.GetValue<int>());
            }
            finally { io.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("HTTP API: released request-admission and mutation policy corpus", () =>
        {
            var cases = JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("http-policy.json")))!.AsArray();
            foreach (var row in cases)
            {
                var method = row!["method"]!.GetValue<string>(); var path = row["path"]!.GetValue<string>();
                Check.Equal(row["task_name"]?.GetValue<string>(), RequestPolicy.MutationTaskName(method, path));
                Check.Equal(row["interactive"]!.GetValue<bool>(), RequestPolicy.Interactive(method, path));
                Check.Equal(row["catalog_admission"]!.GetValue<bool>(), RequestPolicy.CatalogAdmission(method, path));
            }
            Console.WriteLine("HTTP request policy parity cases " + cases.Count);
        });
        suite.Add("HTTP API: reset admission drains response ownership while monitoring remains available", () =>
        {
            using var pipeline = new Pipeline(); using var client = pipeline.Server.CreateClient();
            var pending = client.GetAsync("/api/v1/hold");
            pipeline.HoldEntered.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            var reset = pipeline.Admission.BeginExclusiveAsync(TimeSpan.FromSeconds(3));
            try
            {
                Check.True(!reset.IsCompleted); Check.Equal(1, pipeline.Admission.Snapshot.Active);
                var blocked = client.GetAsync("/api/v1/echo").GetAwaiter().GetResult();
                Check.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode); Check.Equal("5", Header(blocked, "Retry-After"));
                Check.Equal(HttpStatusCode.OK, client.GetAsync("/api/v1/live").GetAwaiter().GetResult().StatusCode);
                Check.Equal(1, pipeline.Lanes.Snapshot.Interactive);
                pipeline.HoldRelease.TrySetResult(); pending.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                using (reset.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult())
                    Check.Equal(HttpStatusCode.ServiceUnavailable, client.GetAsync("/api/v1/echo").GetAwaiter().GetResult().StatusCode);
                Check.Equal(0, pipeline.Admission.Snapshot.Active); Check.Equal(0, pipeline.Lanes.Snapshot.Interactive);
                Check.Equal(HttpStatusCode.OK, client.GetAsync("/api/v1/echo").GetAwaiter().GetResult().StatusCode);
            }
            finally { pipeline.HoldRelease.TrySetResult(); pending.GetAwaiter().GetResult(); if (reset.IsCompletedSuccessfully) reset.Result.Dispose(); }
        });
        suite.Add("HTTP API: failed reset drain reopens admission without resetting data", () =>
        {
            var admission = new RequestAdmissionGate(); using var active = admission.Enter();
            Check.Throws<TimeoutException>(() => admission.BeginExclusiveAsync(TimeSpan.FromMilliseconds(30)).GetAwaiter().GetResult());
            Check.True(!admission.Snapshot.Exclusive); using var next = admission.Enter(); Check.Equal(2, admission.Snapshot.Active);
        });
        suite.Add("HTTP API: reader token guards exact prefix, read-only verbs and rewrite", () =>
        {
            using var pipeline = new Pipeline(); using var client = pipeline.Server.CreateClient();
            Check.Equal(HttpStatusCode.Unauthorized, client.GetAsync("/api/reader/v1/echo").GetAwaiter().GetResult().StatusCode);
            using var key = Request(HttpMethod.Get, "/api/reader/v1/echo?value=1", "X-AutoSourcarr-Api-Key", "reader");
            var response = client.SendAsync(key).GetAwaiter().GetResult();
            Check.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = Body(response); Check.Equal("/api/v1/echo", body["path"]!.GetValue<string>()); Check.Equal("?value=1", body["query"]!.GetValue<string>());
            using var bearer = Request(HttpMethod.Head, "/api/reader/v1/echo", "Authorization", "Bearer reader");
            response = client.SendAsync(bearer).GetAwaiter().GetResult(); Check.Equal(HttpStatusCode.OK, response.StatusCode);
            Check.Equal(0, response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult().Length);
            using var mutation = Request(HttpMethod.Post, "/api/reader/v1/echo", "X-AutoSourcarr-Api-Key", "reader");
            response = client.SendAsync(mutation).GetAwaiter().GetResult(); Check.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Check.Equal("GET, HEAD", Header(response, "Allow"));
            Check.Equal(HttpStatusCode.Unauthorized, client.GetAsync("/api/reader/v1/echo?token=reader").GetAwaiter().GetResult().StatusCode);
            Check.Equal(HttpStatusCode.OK, client.GetAsync("/api/v1/echo").GetAwaiter().GetResult().StatusCode);
            Check.Equal(HttpStatusCode.NotFound, client.GetAsync("/api/reader/v1extra/echo").GetAwaiter().GetResult().StatusCode);
            pipeline.Settings = pipeline.Settings with { AutosourcarrReaderApiToken = "" };
            Check.Equal(HttpStatusCode.ServiceUnavailable, client.GetAsync("/api/reader/v1/echo").GetAwaiter().GetResult().StatusCode);
        });
        suite.Add("HTTP API: admin auth uses current settings and never accepts reader credentials", () =>
        {
            using var pipeline = new Pipeline(); using var client = pipeline.Server.CreateClient();
            Check.Equal(HttpStatusCode.Unauthorized, client.GetAsync("/api/admin/echo").GetAwaiter().GetResult().StatusCode);
            using var reader = Request(HttpMethod.Get, "/api/admin/echo", "X-AutoSourcarr-Api-Key", "reader");
            Check.Equal(HttpStatusCode.Unauthorized, client.SendAsync(reader).GetAwaiter().GetResult().StatusCode);
            using var valid = Request(HttpMethod.Get, "/api/admin/echo", "X-Admin-Token", "admin");
            Check.Equal(HttpStatusCode.OK, client.SendAsync(valid).GetAwaiter().GetResult().StatusCode);
            using var precedence = Request(HttpMethod.Get, "/api/admin/echo", "X-Admin-Token", "admin"); precedence.Headers.TryAddWithoutValidation("Authorization", "Bearer wrong");
            Check.Equal(HttpStatusCode.Unauthorized, client.SendAsync(precedence).GetAwaiter().GetResult().StatusCode);
            pipeline.Settings = pipeline.Settings with { AdminToken = "changed" };
            using var old = Request(HttpMethod.Get, "/api/admin/echo", "X-Admin-Token", "admin");
            Check.Equal(HttpStatusCode.Unauthorized, client.SendAsync(old).GetAwaiter().GetResult().StatusCode);
            using var changed = Request(HttpMethod.Get, "/api/admin/echo", "Authorization", "Bearer changed");
            Check.Equal(HttpStatusCode.OK, client.SendAsync(changed).GetAwaiter().GetResult().StatusCode);
            pipeline.Settings = pipeline.Settings with { AdminToken = "" };
            Check.Equal(HttpStatusCode.OK, client.GetAsync("/api/admin/echo").GetAwaiter().GetResult().StatusCode);
        });
        suite.Add("HTTP API: Host, static Range and upstream asset response protections", () =>
        {
            using var pipeline = new Pipeline(); using var client = pipeline.Server.CreateClient();
            foreach (var hosts in new[] { new[] { "one.test", "two.test" }, new[] { "unsafe@host" }, new[] { "bad/host" }, new[] { "bad host" } })
            {
                var context = pipeline.Server.SendAsync(context => { context.Request.Path = "/api/v1/echo"; context.Request.Headers.Host = new StringValues(hosts); }).GetAwaiter().GetResult();
                Check.Equal(400, context.Response.StatusCode);
            }
            using var asset = Request(HttpMethod.Get, "/api/reader/v1/covers/1", "X-AutoSourcarr-Api-Key", "reader");
            var response = client.SendAsync(asset).GetAwaiter().GetResult();
            Check.Equal("nosniff", Header(response, "X-Content-Type-Options"));
            Check.Equal("sandbox; default-src 'none'", Header(response, "Content-Security-Policy"));
            Check.Equal("no-referrer", Header(response, "Referrer-Policy"));
            using var range = Request(HttpMethod.Get, "/static/echo", "Range", "bytes=0-1,4-9"); range.Headers.TryAddWithoutValidation("If-Range", "old");
            var body = Body(client.SendAsync(range).GetAwaiter().GetResult());
            Check.True(!body["range"]!.GetValue<bool>()); Check.True(!body["if_range"]!.GetValue<bool>());
        });
        suite.Add("HTTP API: bounded JSON rejects bad UTF8, duplicate keys, arrays and oversize bodies", () =>
        {
            using var pipeline = new Pipeline(); using var client = pipeline.Server.CreateClient();
            foreach (var bytes in new[] { Encoding.UTF8.GetBytes("[]"), Encoding.UTF8.GetBytes("{\"value\":1,\"value\":2}"), new byte[] { 123, 34, 120, 34, 58, 34, 255, 34, 125 } })
            {
                using var request = Request(HttpMethod.Post, "/api/admin/json", "X-Admin-Token", "admin"); request.Content = new ByteArrayContent(bytes);
                Check.Equal(HttpStatusCode.BadRequest, client.SendAsync(request).GetAwaiter().GetResult().StatusCode);
            }
            var oversized = pipeline.Server.SendAsync(context =>
            {
                context.Request.Method = "POST"; context.Request.Path = "/api/admin/json"; context.Request.Headers["X-Admin-Token"] = "admin";
                context.Request.ContentLength = 16385; context.Request.Body = new FailIfReadStream();
            }).GetAwaiter().GetResult();
            Check.Equal(413, oversized.Response.StatusCode);
            using var valid = Request(HttpMethod.Post, "/api/admin/json", "X-Admin-Token", "admin");
            valid.Content = new StringContent("{\"value\":\"kept\"}", Encoding.UTF8, "application/json");
            Check.Equal("kept", Body(client.SendAsync(valid).GetAwaiter().GetResult())["value"]!.GetValue<string>());
            using var unauthorized = new HttpRequestMessage(HttpMethod.Post, "/api/admin/json") { Content = new ByteArrayContent(new byte[17000]) };
            Check.Equal(HttpStatusCode.Unauthorized, client.SendAsync(unauthorized).GetAwaiter().GetResult().StatusCode);
            Check.Equal(0, pipeline.Unexpected);
        });
        suite.Add("HTTP API: bounded upload counts entity max-plus-one and rejects known size before relaxing the server", () =>
        {
            foreach (var count in new[] { 7, 8 })
            {
                var context = new DefaultHttpContext(); var feature = new UploadLimit(); context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
                using var body = new SmallUploadBody(count, () => Check.True(feature.MaxRequestBodySize is null)); context.Request.Body = body;
                var work = ApiJson.ReadUploadAsync(context.Request, 7, "Safe upload byte limit", "Safe upload deadline");
                if (count == 7) Check.Equal(7, work.GetAwaiter().GetResult().Length);
                else Check.True(ObserveHttpFailure(work).GetAwaiter().GetResult() is ApiProblem { StatusCode: 413 });
                Check.Equal(count, body.BytesRead); Check.Equal(1, feature.Writes); Check.True(!body.Disposed);
            }
            var oversized = new DefaultHttpContext(); var unchanged = new UploadLimit(); oversized.Features.Set<IHttpMaxRequestBodySizeFeature>(unchanged);
            oversized.Request.ContentLength = 8; oversized.Request.Body = new FailIfReadStream();
            Check.True(ObserveHttpFailure(ApiJson.ReadUploadAsync(oversized.Request, 7, "Safe upload byte limit", "Safe upload deadline")).GetAwaiter().GetResult() is ApiProblem { StatusCode: 413 });
            Check.Equal(0, unchanged.Writes); Check.Equal(30_000_000L, unchanged.MaxRequestBodySize);
            var alreadyReading = new DefaultHttpContext(); var immutable = new UploadLimit { IsReadOnly = true }; alreadyReading.Features.Set<IHttpMaxRequestBodySizeFeature>(immutable); alreadyReading.Request.Body = new FailIfReadStream();
            Check.True(ObserveHttpFailure(ApiJson.ReadUploadAsync(alreadyReading.Request, 7, "Safe upload byte limit", "Safe upload deadline")).GetAwaiter().GetResult() is ApiProblem { StatusCode: 409 }); Check.Equal(0, immutable.Writes);
        });
        suite.Add("HTTP API: upload deadline is absolute45seconds and cancellation joins the actual request read", () => UploadDeadline().GetAwaiter().GetResult());
        suite.Add("HTTP API: typed Kestrel413/408 are safe quiet errors without generic aggregate unwrapping", () =>
        {
            foreach (var status in new[] { 413, 408, 500 })
            {
                Exception failure = new BadHttpRequestException("private transport path and request text", status == 500 ? 413 : status);
                if (status == 500) failure = new AggregateException("Unrelated domain aggregate", failure);
                var context = new DefaultHttpContext(); context.Request.Method = "POST"; using var body = new MemoryStream(); context.Response.Body = body;
                var unexpected = 0;
                new ApiExceptionMiddleware(_ => Task.FromException(failure), (_, _) => { unexpected++; return "recorded"; }).InvokeAsync(context).GetAwaiter().GetResult();
                Check.Equal(status, context.Response.StatusCode); Check.Equal(status == 500 ? 1 : 0, unexpected);
                var text = Encoding.UTF8.GetString(body.ToArray()); Check.True(!text.Contains("private transport", StringComparison.Ordinal));
                if (status != 500) Check.Equal("no-store", context.Response.Headers.CacheControl.ToString());
                Check.True(JsonNode.Parse(text)!["detail"]!.GetValue<string>().Length > 0);
            }
        });
        suite.Add("HTTP API: known source pauses stay quiet and unexpected details remain private", () =>
        {
            using var pipeline = new Pipeline(); using var client = pipeline.Server.CreateClient();
            var paused = client.GetAsync("/api/v1/paused").GetAwaiter().GetResult();
            Check.Equal(HttpStatusCode.ServiceUnavailable, paused.StatusCode); Check.Equal("3600", Header(paused, "Retry-After"));
            Check.True(Body(paused)["needs_attention"]!.GetValue<bool>()); Check.Equal(0, pipeline.Unexpected);
            var failed = client.GetAsync("/api/v1/fail").GetAwaiter().GetResult();
            Check.Equal(HttpStatusCode.InternalServerError, failed.StatusCode); Check.Equal(1, pipeline.Unexpected);
            var text = failed.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check.True(!text.Contains("private-sentinel", StringComparison.Ordinal)); Check.Equal(pipeline.LastErrorId, JsonNode.Parse(text)!["error_id"]!.GetValue<string>());
            Check.Equal(1, pipeline.Errors.Snapshot()["active_count"]!.GetValue<int>());
        });
        suite.Add("HTTP API: error views filter and clear without granting reader access or creating tasks", () =>
        {
            using var pipeline = new Pipeline(); using var client = pipeline.Server.CreateClient();
            Check.Equal(HttpStatusCode.Unauthorized, client.GetAsync("/api/admin/errors").GetAwaiter().GetResult().StatusCode);
            pipeline.Errors.Record("Source", "active", "active"); pipeline.Errors.Record("Source", "resolved", "resolved"); pipeline.Errors.Resolve("resolved");
            client.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
            var active = Body(client.GetAsync("/api/admin/errors?include_resolved=no&limit=1").GetAwaiter().GetResult());
            Check.Equal(1, active["items"]!.AsArray().Count); Check.Equal("active", active["items"]![0]!["key"]!.GetValue<string>());
            Check.Equal(HttpStatusCode.UnprocessableEntity, client.GetAsync("/api/admin/errors?limit=0").GetAwaiter().GetResult().StatusCode);
            Check.Equal(1, Body(client.PostAsync("/api/admin/errors/clear", null).GetAwaiter().GetResult())["cleared"]!.GetValue<int>());
            Check.Equal(1, Body(client.PostAsync("/api/admin/errors/clear?resolved_only=false", null).GetAwaiter().GetResult())["cleared"]!.GetValue<int>());
            Check.Equal(0, pipeline.Tasks.Snapshot()["completed"]!.GetValue<int>());
        });
        suite.Add("HTTP API: diagnostic download remains local, partial and redacted without exposing worker bodies", () =>
        {
            using var temp = new Sandbox(); var io = new BoundedWorkScheduler(2, 8); var privacy = new SupportPrivacy();
            var settings = new AutoSourcarrSettings { RepairAiApiKey = "exporter-private-value" };
            privacy.Register(new Dictionary<string, string?> { ["api_key"] = settings.RepairAiApiKey });
            var identity = RuntimeIdentity.Create("fixture"); var logs = new SupportLogStore(io, identity, privacy, temp.Path);
            try
            {
                var errors = new ErrorRegistry(identity, privacy); var sources = new DiagnosticSources();
                logs.Record("app", "password=exporter-private-value");
                logs.Repair.ObserveRepairs(new JsonObject { ["jobs"] = new JsonArray(new JsonObject { ["id"] = new string('a', 32), ["state"] = "blocked", ["message"] = "blocked", ["patch"] = "PRIVATE_PATCH" }) });
                var exporter = new DiagnosticExporter(io, identity, errors, logs, () => settings, sources);
                using var pipeline = new Pipeline(exporter: exporter); using var client = pipeline.Server.CreateClient();
                Check.Equal(HttpStatusCode.Unauthorized, client.GetAsync("/api/admin/diagnostics").GetAwaiter().GetResult().StatusCode);
                Check.Equal(0, sources.Captures);
                client.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
                var response = client.GetAsync("/api/admin/diagnostics").GetAwaiter().GetResult();
                Check.Equal(HttpStatusCode.OK, response.StatusCode); Check.Equal("no-store", Header(response, "Cache-Control"));
                var body = Body(response); var text = body.ToJsonString();
                Check.Equal(3, body["schema_version"]!.GetValue<int>()); Check.Equal("imports", body["partial_error"]!["section"]!.GetValue<string>());
                Check.True(body["logs"]!["entries"]!.AsArray().Count > 0); Check.Equal(1, sources.Captures); Check.Equal(1, sources.Imports);
                foreach (var forbidden in new[] { "exporter-private-value", "PRIVATE_PATCH", "PRIVATE_PROMPT", "PRIVATE_CONVERSATION" }) Check.True(!text.Contains(forbidden, StringComparison.Ordinal));
                sources.FailCached = true;
                body = Body(client.GetAsync("/api/admin/diagnostics").GetAwaiter().GetResult());
                Check.Equal("cached_context", body["partial_errors"]![0]!["section"]!.GetValue<string>());
                Check.True(body["logs"]!["entries"]!.AsArray().Count > 0); Check.Equal(0, pipeline.Unexpected);
            }
            finally { logs.DisposeAsync().AsTask().GetAwaiter().GetResult(); io.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("HTTP API: reader recovery resolves its original internal-route error", () =>
        {
            using var pipeline = new Pipeline(); using var client = pipeline.Server.CreateClient();
            client.DefaultRequestHeaders.Add("X-AutoSourcarr-Api-Key", "reader");
            Check.Equal(HttpStatusCode.InternalServerError, client.GetAsync("/api/reader/v1/fail").GetAwaiter().GetResult().StatusCode);
            Check.True(pipeline.Errors.Snapshot()["items"]![0]!["key"]!.GetValue<string>().Contains("|/api/v1/fail|", StringComparison.Ordinal));
            pipeline.FailRequests = false;
            Check.Equal(HttpStatusCode.OK, client.GetAsync("/api/reader/v1/fail").GetAwaiter().GetResult().StatusCode);
            Check.Equal(0, pipeline.Errors.Snapshot()["active_count"]!.GetValue<int>());
        });
        suite.Add("HTTP API: authenticated installed-source health routes retain WebUI response contracts", () =>
        {
            using var f = new HealthCoordinationTests.Fixture(); using var pipeline = new Pipeline(f.Coordinator, f.Lanes, f.Registry); using var client = pipeline.Server.CreateClient();
            Check.Equal(HttpStatusCode.Unauthorized, client.GetAsync("/api/admin/source-health").GetAwaiter().GetResult().StatusCode);
            client.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
            var snapshot = Body(client.GetAsync("/api/admin/source-health").GetAwaiter().GetResult());
            Check.Equal(1, snapshot["items"]!.AsArray().Count); Check.Equal(0, f.Engine.Calls);
            foreach (var (json, status) in new[] { ("{\"source_ids\":[-1]}", HttpStatusCode.BadRequest), ("{\"source_ids\":[\"-99\"]}", HttpStatusCode.NotFound) })
                Check.Equal(status, client.PostAsync("/api/admin/source-health/check", new StringContent(json, Encoding.UTF8, "application/json")).GetAwaiter().GetResult().StatusCode);
            Check.Equal(HttpStatusCode.BadRequest, client.PutAsync("/api/admin/source-health/daily", new StringContent("{\"enabled\":\"true\"}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult().StatusCode);
            var queued = client.PostAsync("/api/admin/source-health/check", new StringContent("{\"all_enabled\":true}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check.Equal(HttpStatusCode.Accepted, queued.StatusCode); Check.True(Body(queued)["queued"]!.GetValue<bool>());
            HealthCoordinationTests.WaitUntil(() => f.Registry.Snapshot()["running"]!.GetValue<int>() == 0);
            snapshot = Body(client.GetAsync("/api/admin/source-health").GetAwaiter().GetResult());
            Check.Equal("working", snapshot["items"]![0]!["state"]!.GetValue<string>());
            Check.Equal(1, f.Registry.Snapshot()["completed"]!.GetValue<int>());
            Check.Equal("0.4.3-migration", Header(queued, "X-AutoSourcarr-Version"));
            var disabled = Body(client.PutAsync("/api/admin/source-health/daily", new StringContent("{\"enabled\":false}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult());
            Check.True(!disabled["daily"]!["enabled"]!.GetValue<bool>()); Check.Equal(0, pipeline.Unexpected);
        });
        suite.Add("HTTP API: task reads remain cached and repair cancellation routes only the selected job", () =>
        {
            var projection = new RepairTaskProjection(new SupportPrivacy(), () => 100);
            var blockedId = new string('a', 32); var readyId = new string('b', 32); var runningId = new string('c', 32);
            projection.Observe(new JsonObject { ["jobs"] = new JsonArray(new JsonObject { ["id"] = blockedId, ["state"] = "blocked", ["message"] = "blocked" },
                new JsonObject { ["id"] = readyId, ["state"] = "ready", ["message"] = "ready" }, new JsonObject { ["id"] = runningId, ["state"] = "running", ["message"] = "running" }) });
            var commands = new RepairCommands(); using var pipeline = new Pipeline(repairTasks: projection, repairCommands: commands); using var client = pipeline.Server.CreateClient();
            Check.Equal(HttpStatusCode.Unauthorized, client.GetAsync("/api/admin/tasks").GetAwaiter().GetResult().StatusCode);
            client.DefaultRequestHeaders.Add("X-Admin-Token", "admin");
            var snapshot = Body(client.GetAsync("/api/admin/tasks").GetAwaiter().GetResult());
            Check.Equal(3, snapshot["items"]!.AsArray().Count); Check.Equal(1, snapshot["failed"]!.GetValue<int>()); Check.Equal(0, commands.Cancelled.Count);
            var cleared = Body(client.PostAsync("/api/admin/tasks/clear-history", null).GetAwaiter().GetResult());
            Check.Equal(1, cleared["cleared"]!.GetValue<int>()); Check.Equal(1, cleared["kept_for_review"]!.GetValue<int>());
            Check.Equal(HttpStatusCode.NotFound, client.PostAsync("/api/admin/tasks/source-repair:invalid/cancel", null).GetAwaiter().GetResult().StatusCode);
            var cancelled = Body(client.PostAsync("/api/admin/tasks/source-repair:" + runningId + "/cancel", null).GetAwaiter().GetResult());
            Check.True(cancelled["cancel_requested"]!.GetValue<bool>()); Check.True(commands.Cancelled.SequenceEqual(new[] { runningId }));
            Check.Equal(0, pipeline.Tasks.Snapshot()["completed"]!.GetValue<int>());
        });
    }

    private static async Task UploadDeadline()
    {
        foreach (var callerAbort in new[] { false, true })
        {
            var clock = new UploadClock(); var context = new DefaultHttpContext(); using var abort = new CancellationTokenSource(); context.RequestAborted = abort.Token;
            using var body = new HeldUploadBody(clock); context.Request.Body = body;
            // The internal overload only supplies a clock; production routes always use the fixed45s system-clock overload.
            var method = typeof(ApiJson).GetMethod("ReadUploadAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, null,
                [typeof(HttpRequest), typeof(int), typeof(string), typeof(string), typeof(TimeProvider)], null)!;
            var work = (Task<byte[]>)method.Invoke(null, [context.Request, 1024, "Safe upload byte limit", "Safe upload deadline", clock])!;
            try
            {
                await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check.Equal(TimeSpan.FromSeconds(30), clock.Elapsed);
                Check.Equal(1, clock.TimersCreated); Check.Equal(TimeSpan.FromSeconds(45), clock.InitialDue);
                if (callerAbort) abort.Cancel();
                else
                {
                    clock.Advance(TimeSpan.FromSeconds(14)); Check.True(!body.Cancelled.Task.IsCompleted);
                    clock.Advance(TimeSpan.FromSeconds(1));
                }
                await body.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(!work.IsCompleted); Check.Equal(0, body.PhysicalReadFinished);
                body.Release.TrySetResult(); var failure = await ObserveHttpFailure(work);
                if (callerAbort) Check.True(failure is OperationCanceledException);
                else Check.True(failure is ApiProblem { StatusCode: 408, Message: "Safe upload deadline" });
                Check.Equal(1, body.PhysicalReadFinished); Check.True(!body.Disposed);
            }
            finally { body.Release.TrySetResult(); await ObserveHttpFailure(work); }
        }
    }
    private static async Task<Exception?> ObserveHttpFailure(Task work) { try { await work.ConfigureAwait(false); return null; } catch (Exception error) { return error; } }
    private sealed class UploadLimit : IHttpMaxRequestBodySizeFeature
    {
        private long? _maximum = 30_000_000; public int Writes; public bool IsReadOnly { get; set; }
        public long? MaxRequestBodySize { get => _maximum; set { Writes++; _maximum = value; } }
    }
    private sealed class SmallUploadBody(int remaining, Action onRead) : Stream
    {
        public int BytesRead; public bool Disposed; public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { onRead(); cancellationToken.ThrowIfCancellationRequested(); var count = Math.Min(Math.Min(buffer.Length, 2), remaining); buffer.Span[..count].Fill(42); remaining -= count; BytesRead += count; return ValueTask.FromResult(count); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class HeldUploadBody(UploadClock clock) : Stream
    {
        private int _reads; public bool Disposed; public int PhysicalReadFinished;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_reads == 1) { buffer.Span[0] = 42; clock.Advance(TimeSpan.FromSeconds(30)); return 1; }
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult()); Entered.TrySetResult();
            try { await Release.Task.ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); return 0; }
            finally { Interlocked.Increment(ref PhysicalReadFinished); }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class UploadClock : TimeProvider
    {
        private UploadTimer? _timer; public TimeSpan Elapsed { get; private set; } public TimeSpan InitialDue; public int TimersCreated;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + Elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { TimersCreated++; InitialDue = dueTime; return _timer = new UploadTimer(this, callback, state, dueTime); }
        public void Advance(TimeSpan elapsed) { Elapsed += elapsed; _timer?.Fire(); }
        private sealed class UploadTimer(UploadClock clock, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private TimeSpan _due = clock.Elapsed + dueTime; private bool _disposed, _fired;
            public void Fire() { if (!_disposed && !_fired && clock.Elapsed >= _due) { _fired = true; callback(state); } }
            public bool Change(TimeSpan dueTime, TimeSpan period) { if (_disposed) return false; _due = clock.Elapsed + dueTime; _fired = false; return true; }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string header, string value)
    { var request = new HttpRequestMessage(method, path); request.Headers.TryAddWithoutValidation(header, value); return request; }
    private static JsonObject Body(HttpResponseMessage response) => JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
    private static string Header(HttpResponseMessage response, string name) => response.Headers.Concat(response.Content.Headers).Where(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(pair => string.Join(", ", pair.Value)).FirstOrDefault() ?? "";

    private sealed class Pipeline : IDisposable
    {
        private readonly SemaphoreSlim _adminLock = new(1, 1);
        private readonly BoundedWorkScheduler _diagnostics = new(1, 8);
        private readonly bool _ownsLanes;
        public AutoSourcarrSettings Settings = new() { AdminToken = "admin", AutosourcarrReaderApiToken = "reader" };
        public RequestAdmissionGate Admission { get; } = new();
        public TaskRegistry Tasks { get; }
        public WholeTaskCoordinator Lanes { get; }
        public TaskCompletionSource HoldEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HoldRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Unexpected;
        public bool FailRequests = true;
        public ErrorRegistry Errors { get; }
        public string LastErrorId { get; private set; } = "";
        public TestServer Server { get; }
        private readonly IHost _host;
        public Pipeline(SourceHealthCoordinator? health = null, WholeTaskCoordinator? lanes = null, TaskRegistry? tasks = null,
            SettingsRepository? preferences = null, BoundedWorkScheduler? io = null, DiagnosticExporter? exporter = null,
            RepairTaskProjection? repairTasks = null, IRepairTaskContext? repairCommands = null)
        {
            _ownsLanes = lanes is null; Tasks = tasks ?? new(); Lanes = lanes ?? new(Settings, Tasks);
            var privacy = new SupportPrivacy(); privacy.Register(new Dictionary<string, string?> { ["api_key"] = "private-sentinel" });
            Errors = new(RuntimeIdentity.Create("0.4.3-migration"), privacy);
            _host = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer().ConfigureServices(services => services.AddRouting()).Configure(app =>
            {
                app.UseAutoSourcarrHttp("0.4.3-migration", () => Settings, (context, error) =>
                {
                    Unexpected++;
                    return LastErrorId = Errors.Record("AutoSourcarr", error.Message, $"unhandled|{context.Request.Method}|{context.Request.Path}|{error.GetType().Name}|{error.Message}",
                        path: context.Request.Path.Value ?? "", method: context.Request.Method, errorType: error.GetType().Name, exception: error);
                }, Admission, Lanes, Tasks, privacy, prefix => Errors.ResolvePrefix(prefix));
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapMethods("/api/v1/echo", ["GET", "HEAD"], (HttpContext context) => ApiJson.WriteAsync(context, 200,
                        new() { ["path"] = context.Request.Path.Value, ["query"] = context.Request.QueryString.Value }));
                    endpoints.MapGet("/api/v1/live", () => Results.Json(new { ok = true }));
                    endpoints.MapGet("/api/v1/hold", async (HttpContext context) =>
                    { HoldEntered.TrySetResult(); await HoldRelease.Task.ConfigureAwait(false); await ApiJson.WriteAsync(context, 200, new() { ["ok"] = true }).ConfigureAwait(false); });
                    endpoints.MapGet("/api/v1/covers/1", () => Results.Content("<svg/>", "image/svg+xml"));
                    endpoints.MapGet("/static/echo", (HttpContext context) => Results.Json(new { range = context.Request.Headers.ContainsKey("Range"), if_range = context.Request.Headers.ContainsKey("If-Range") }));
                    endpoints.MapGet("/api/v1/paused", (HttpContext _) => Task.FromException(new HumanAssistanceRequiredException(new JsonObject { ["id"] = "attention", ["name"] = "Source", ["reason"] = "CAPTCHA needs help" }, 3600)));
                    endpoints.MapGet("/api/v1/fail", (HttpContext context) => FailRequests ? Task.FromException(new InvalidOperationException("private-sentinel-full-error")) : ApiJson.WriteAsync(context, 200, new() { ["ok"] = true }));
                    var admin = endpoints.MapGroup("/api/admin").AddEndpointFilter(new AdminAuthorizationFilter(() => Settings));
                    admin.MapGet("/echo", () => Results.Json(new { ok = true }));
                    admin.MapPost("/json", (Func<HttpContext, Task<IResult>>)(async context => Results.Json(await ApiJson.ReadObjectAsync(context.Request).ConfigureAwait(false))));
                    if (health is not null) endpoints.MapSourceHealth(health, () => Settings, _adminLock);
                    if (preferences is not null) endpoints.MapUiPreferences(preferences, io!, () => Settings);
                    endpoints.MapErrors(Errors, _diagnostics, () => Settings);
                    if (exporter is not null) endpoints.MapDiagnostics(exporter, () => Settings);
                    if (repairTasks is not null) endpoints.MapTasks(Tasks, repairTasks, repairCommands!, _diagnostics, () => Settings);
                });
            })).Build();
            _host.StartAsync().GetAwaiter().GetResult();
            Server = _host.GetTestServer();
        }
        public void Dispose() { _host.Dispose(); if (_ownsLanes) Lanes.DisposeAsync().AsTask().GetAwaiter().GetResult(); _diagnostics.DisposeAsync().AsTask().GetAwaiter().GetResult(); _adminLock.Dispose(); }
    }
    private sealed class FailIfReadStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Oversize request body must not be read");
    }
    private sealed class DiagnosticSources : IDiagnosticSources
    {
        public int Captures;
        public int Imports;
        public bool FailCached;
        public CachedDiagnosticSources CaptureCached()
        {
            Captures++;
            if (FailCached) throw new IOException("exporter-private-value cached status failed");
            return new(new() { ["app_version"] = "fixture" }, null, new() { ["state"] = "ready" }, new() { ["state"] = "ready" },
                new() { ["running"] = 1, ["items"] = new JsonArray(new JsonObject { ["id"] = "source-repair:" + new string('a', 32), ["kind"] = "source-repair",
                    ["state"] = "failed", ["message"] = "blocked", ["patch"] = "PRIVATE_PATCH", ["prompt"] = "PRIVATE_PROMPT", ["conversation"] = "PRIVATE_CONVERSATION" }) });
        }
        public JsonObject ImportContext(JsonArray errors) { Imports++; throw new IOException("exporter-private-value importer unavailable"); }
    }
    private sealed class RepairCommands : IRepairTaskContext
    {
        public List<string> Cancelled { get; } = [];
        public (JsonObject Operation, RepairTaskSupplement Supplement) CaptureCached() => (new(), new(new(), [], []));
        public Task<bool> RequestCancelAsync(string repairId, CancellationToken cancellationToken) { Cancelled.Add(repairId); return Task.FromResult(true); }
    }
}
