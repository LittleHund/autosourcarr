using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Catalogue.Metadata.Hydration;

/// <summary>
/// repair_orphan_identity_metadata_upgrade: entries with a title but no source record and no provider identity are looked
/// up at the metadata providers once (title-only bootstrap). Progress (a cursor and the ids whose providers failed) is saved
/// after every wave in the catalogue settings, so a restart resumes; an empty search is a result, a provider failure is
/// retried next time; a wave in which every lookup failed stops the run instead of hammering a blocked provider. Runs as
/// one low-priority job.
/// </summary>
public sealed class OrphanIdentityRepair(SqliteDatabase database, Func<AutoSourcarrSettings> settings,
    Func<long, bool, CancellationToken, Task<JsonObject>> resolve, Func<bool> providersAvailable)
{
    public const string Version = "0.2.85";
    public const string VersionKey = "orphan_identity_bootstrap_repair_version";
    public const string ProgressKey = "orphan_identity_bootstrap_repair_progress";
    public static readonly JobType Job = new("orphan-identity-repair", "Repair orphan metadata identities", JobPriority.Low, JobFlags.LongRunning,
        StallTimeout: TimeSpan.FromMinutes(10));
    private const string Orphan = """
        length(trim(COALESCE(e.preferred_title,'')))>0
        AND NOT EXISTS (SELECT 1 FROM source_matches sm WHERE sm.entry_id=e.id AND sm.active=1)
        AND NOT EXISTS (SELECT 1 FROM source_variants sv WHERE sv.entry_id=e.id AND sv.active=1)
        AND NOT EXISTS (SELECT 1 FROM identity_links il WHERE il.entry_id=e.id)
        """;

    /// <summary>Queues the repair unless it already finished for this version (or <paramref name="force"/>); answers the task id or null.</summary>
    public string? Schedule(JobQueue jobs, bool force = false, string origin = "automatic")
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (!force && Setting(VersionKey) == Version) return null;
        if (!settings().MetadataIdentityEnabled || !providersAvailable()) return null;
        return jobs.Enqueue(Job, "", async job =>
        {
            var result = await RunAsync(force, 5000, (current, total, message) => job.Report(current, total, message), job.CancellationToken).ConfigureAwait(false);
            job.Result(result["message"]?.GetValue<string>() ?? "Done");
        }, origin: origin).Id;
    }

    public async Task<JsonObject> RunAsync(bool force, int limit, Action<int, int, string>? progress, CancellationToken cancellationToken)
    {
        if (!settings().MetadataIdentityEnabled || !providersAvailable())
            return new JsonObject { ["skipped"] = true, ["version"] = Version, ["reason"] = "metadata identity disabled or no providers enabled", ["message"] = "Skipped: metadata identity is off" };
        if (!force && Setting(VersionKey) == Version) return new JsonObject { ["skipped"] = true, ["version"] = Version, ["message"] = "Already complete" };
        var (cursor, failures) = force ? (0L, new SortedSet<long>()) : Progress();
        limit = Math.Clamp(limit, 1, 20000);
        var ids = database.Read(DatabaseFile.Catalog, s =>
        {
            failures.RemoveWhere(id => s.Scalar("SELECT 1 FROM entries e WHERE e.id=$id AND " + Orphan, ("$id", id)) is null);
            var fresh = s.Query("SELECT e.id FROM entries e WHERE e.id>$cursor AND " + Orphan + " ORDER BY e.id LIMIT $limit", ("$cursor", cursor), ("$limit", limit + 1))
                .Select(row => row.Integer("id"));
            return failures.Take(limit).Concat(fresh.Where(id => !failures.Contains(id))).Take(limit).ToList();
        });
        var resolved = 0; var attempted = 0; var providerErrors = 0;
        var waveSize = Math.Clamp(settings().MetadataIdentityConcurrency * 2, 1, 6);
        for (var offset = 0; offset < ids.Count; offset += waveSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wave = ids.Skip(offset).Take(waveSize).ToArray();
            var results = await Task.WhenAll(wave.Select(async id =>
            {
                try { return await resolve(id, true, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                { return new JsonObject { ["matched"] = 0, ["provider_errors"] = new JsonObject { ["repair"] = error.Message } }; }
            })).ConfigureAwait(false);
            var allFailed = true;
            for (var index = 0; index < wave.Length; index++)
            {
                var result = results[index];
                attempted++;
                var matched = result["matched"] is JsonValue value && value.TryGetValue<int>(out var count) && count > 0;
                var errors = result["provider_errors"] as JsonObject;
                resolved += matched ? 1 : 0;
                providerErrors += errors?.Count ?? 0;
                if (errors is { Count: > 0 } && !matched) failures.Add(wave[index]);
                else { failures.Remove(wave[index]); allFailed = false; }
                cursor = Math.Max(cursor, wave[index]);
            }
            SaveProgress(cursor, failures);
            progress?.Invoke(attempted, ids.Count, "Resolved " + Text(resolved) + "/" + Text(attempted) + "; retryable failures " + Text(failures.Count));
            if (allFailed) break;
        }
        var more = database.Read(DatabaseFile.Catalog, s => s.Scalar("SELECT 1 FROM entries e WHERE e.id>$cursor AND " + Orphan + " LIMIT 1", ("$cursor", cursor)) is not null);
        var pending = failures.Count > 0 || more || attempted < ids.Count;
        if (!pending) SetSetting(VersionKey, Version);
        return new JsonObject
        {
            ["skipped"] = false, ["version"] = Version, ["candidates"] = ids.Count, ["attempted"] = attempted, ["resolved"] = resolved,
            ["unresolved"] = Math.Max(0, attempted - resolved), ["provider_errors"] = providerErrors, ["pending"] = pending,
            ["failed_ids"] = new JsonArray(failures.Select(id => (JsonNode)id).ToArray()),
            ["message"] = "Orphan metadata repair " + (pending ? "paused; retries remain for next startup" : "complete") + " · resolved " + Text(resolved) + "/" + Text(attempted),
        };
    }

    private (long Cursor, SortedSet<long> Failures) Progress()
    {
        try
        {
            if (JsonNode.Parse(Setting(ProgressKey) is { Length: > 0 } text ? text : "{}") is JsonObject state && state["version"]?.GetValue<string>() == Version)
            {
                var cursor = state["cursor"] is JsonValue value && value.TryGetValue<long>(out var number) ? Math.Max(0, number) : 0;
                var failed = new SortedSet<long>((state["failed_ids"] as JsonArray ?? new JsonArray()).OfType<JsonValue>()
                    .Select(item => item.TryGetValue<long>(out var id) ? id : 0).Where(id => id > 0));
                return (cursor, failed);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { }
        return (0, new SortedSet<long>());
    }

    private void SaveProgress(long cursor, IEnumerable<long> failures) => SetSetting(ProgressKey, new JsonObject
    {
        ["version"] = Version, ["cursor"] = cursor, ["failed_ids"] = new JsonArray(failures.Select(id => (JsonNode)id).ToArray()),
    }.ToJsonString());

    private string Setting(string key) => database.Read(DatabaseFile.Catalog, s => s.Scalar("SELECT value FROM settings WHERE key=$key", ("$key", key)) as string) ?? "";
    private void SetSetting(string key, string value) => database.Write(DatabaseFile.Catalog, s =>
        s.Execute("INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", value)));
    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
