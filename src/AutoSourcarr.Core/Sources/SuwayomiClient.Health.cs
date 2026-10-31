using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Sources;

public sealed partial class SuwayomiClient
{
    public async Task<JsonObject> HealthAsync(CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await session.HealthAsync(cancellationToken).ConfigureAwait(false);
    }
    internal async Task<JsonObject> HealthCoreAsync(SourceTransportSession session, CancellationToken cancellationToken)
    {
        var probes = new JsonArray();
        async Task<bool> Probe(string operation, string method, string path, double seconds, Func<CancellationToken, Task> call)
        {
            var watch = Stopwatch.StartNew();
            var row = new JsonObject { ["operation"] = operation, ["method"] = method, ["path"] = path, ["timeout_seconds"] = seconds };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
            try { await call(deadline.Token).ConfigureAwait(false); row["ok"] = true; return true; }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && !session.CancellationToken.IsCancellationRequested)
            {
                session.ThrowIfRetired();
                var timedOut = error is OperationCanceledException && deadline.IsCancellationRequested;
                row["ok"] = false;
                row["error_type"] = timedOut ? nameof(TimeoutException) : error.GetType().Name;
                row["error"] = _privacy.Text(timedOut ? $"No response within {seconds:g}s" : error.Message);
                if (error is HttpRequestException { StatusCode: { } status }) row["http_status"] = (int)status;
                if (error is SourceWorkDeferredException) row["deferred"] = true;
                return false;
            }
            finally { row["elapsed_ms"] = watch.ElapsedMilliseconds; probes.Add(row); }
        }
        if (await Probe("graphql_health", "POST", "/api/graphql", 2.25, async token =>
            {
                var data = await GraphQlCoreAsync(session, "query AutoSourcarrHealth { __typename }", null, token).ConfigureAwait(false);
                if (SourceResponseContract.Text(data["__typename"]).Length == 0) throw new SuwayomiException("Health query returned no GraphQL type");
            }).ConfigureAwait(false)) return new() { ["ok"] = true, ["url"] = BaseUrl, ["probes"] = probes };
        if (probes[^1]?["deferred"]?.GetValue<bool>() == true)
            return new() { ["ok"] = false, ["deferred"] = true, ["url"] = BaseUrl, ["error"] = probes[^1]!["error"]!.DeepClone(), ["probes"] = probes };
        var available = await Probe("api_liveness", "GET", "/api/v1/settings/about", 2, async token =>
        {
            if (await RestJsonCoreAsync(session, "/api/v1/settings/about", token).ConfigureAwait(false) is not JsonObject)
                throw new SuwayomiException("About endpoint returned an unexpected response");
        }).ConfigureAwait(false);
        return available ? new JsonObject { ["ok"] = true, ["degraded"] = true, ["url"] = BaseUrl,
            ["warning"] = "Source API is reachable, but its GraphQL health check failed or was slow.", ["probes"] = probes }
            : new JsonObject { ["ok"] = false, ["url"] = BaseUrl, ["error"] = "Source API health checks failed; the engine may be busy or unavailable.", ["probes"] = probes };
    }
}
