using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Sources;

public sealed class SourceHealthStore
{
    public const int DaySeconds = 86400;
    private const string DailyKey = "source_health_daily_v1";
    private static readonly HashSet<string> States = ["working", "failed", "inconclusive", "deferred", "verification", "rate_limited", "cancelled"];
    private readonly SqliteDatabase _database;
    private readonly SettingsRepository _settings;
    private readonly SupportPrivacy _privacy;
    private readonly Func<double> _clock;
    private readonly Action<JsonObject>? _record;

    public SourceHealthStore(SqliteDatabase database, SupportPrivacy? privacy = null, Func<double>? clock = null, Action<JsonObject>? record = null)
    {
        _database = database; _settings = new(database); _privacy = privacy ?? new();
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0); _record = record;
    }
    public bool DailyEnabled() => (_settings.Get(DailyKey, new JsonObject { ["enabled"] = true }) as JsonObject)?["enabled"] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled;
    public void ConfigureDaily(bool enabled) => _settings.Set(DailyKey, new JsonObject { ["enabled"] = enabled });

    public JsonObject Snapshot()
    {
        var rows = _database.Read(DatabaseFile.Catalog, session => session.Query("""
            SELECT sc.source_id,sc.name,sc.lang,sc.base_url,sc.available,
            sc.enabled_language,sc.enabled_search,sc.enabled_chapters,sc.supports_latest,
            h.state,h.message,h.checked_at,h.started_at,h.last_success_at,h.last_repaired_at,h.last_changed_at,h.next_check_at,
            h.package_name,h.checked_version,h.repair_job_id,h.checks_json,h.history_json
            FROM source_config sc LEFT JOIN source_health h ON h.source_id=sc.source_id
            WHERE sc.available=1 ORDER BY sc.name COLLATE NOCASE,sc.lang COLLATE NOCASE
            """));
        var items = new List<JsonObject>(); var due = new List<double>();
        foreach (var row in rows)
        {
            var item = JsonSerializer.SerializeToNode(row)!.AsObject();
            foreach (var field in new[] { "checked_at", "started_at", "last_success_at", "last_repaired_at", "last_changed_at", "next_check_at" }) item[field] = row.Number(field);
            item["state"] = row.Text("state") is { Length: > 0 } state ? state : "unchecked"; item["message"] = row.Text("message");
            var eligible = row.Flag("available") && row.Flag("enabled_language") && (row.Flag("enabled_search") || row.Flag("enabled_chapters"));
            item["eligible"] = eligible;
            item["needs_recheck"] = Math.Max(row.Number("last_repaired_at"), row.Number("last_changed_at")) > row.Number("started_at");
            item["stale"] = row.Number("checked_at") > 0 && _clock() - row.Number("checked_at") > DaySeconds;
            foreach (var field in new[] { "checks", "history" })
            { item[field] = ArrayOrEmpty(row.Text(field + "_json")); item.Remove(field + "_json"); }
            items.Add(item); if (eligible) due.Add(row.Number("next_check_at"));
        }
        return new() { ["items"] = SourceResponseContract.Array(items), ["daily"] = new JsonObject { ["enabled"] = DailyEnabled(),
            ["next_due_at"] = due.Count > 0 ? JsonValue.Create(due.Min()) : null } };
    }

    public JsonObject? Source(string sourceId) => _database.Read(DatabaseFile.Catalog, session =>
    {
        var row = session.Query("""
            SELECT sc.available,sc.enabled_language,sc.enabled_search,sc.enabled_chapters,coalesce(h.next_check_at,0) next_check_at
            FROM source_config sc LEFT JOIN source_health h ON h.source_id=sc.source_id WHERE sc.source_id=$id
            """, ("$id", sourceId)).FirstOrDefault();
        if (row is null) return null;
        var result = JsonSerializer.SerializeToNode(row)!.AsObject();
        result["eligible"] = row.Flag("available") && row.Flag("enabled_language") && (row.Flag("enabled_search") || row.Flag("enabled_chapters"));
        return result;
    });

    public void Reserve(string sourceId) => _database.Write(DatabaseFile.Catalog, session => session.Execute("""
        INSERT INTO source_health(source_id,next_check_at) VALUES($id,$next)
        ON CONFLICT(source_id) DO UPDATE SET next_check_at=excluded.next_check_at
        """, ("$id", sourceId), ("$next", _clock() + DaySeconds)));

    public void Finish(string sourceId, string state, string message, JsonArray checks, string origin, double? stamp = null, double? startedAt = null, string package = "", string version = "")
    {
        if (!States.Contains(state)) throw new ArgumentException("Invalid source health state", nameof(state));
        var when = stamp ?? _clock(); var began = startedAt ?? when;
        if (!double.IsFinite(when) || !double.IsFinite(began)) throw new ArgumentOutOfRangeException(nameof(stamp));
        message = _privacy.Text(message); message = message[..Math.Min(600, message.Length)];
        var safeMessage = message;
        _database.Write(DatabaseFile.Catalog, session =>
        {
            var history = ArrayOrEmpty(session.Scalar("SELECT history_json FROM source_health WHERE source_id=$id", ("$id", sourceId)) as string);
            var retained = new JsonArray(history.TakeLast(7).Select(item => item?.DeepClone()).ToArray());
            retained.Add(new JsonObject { ["state"] = state, ["at"] = when, ["origin"] = origin, ["message"] = safeMessage });
            return session.Execute("""
                INSERT INTO source_health(source_id,state,message,checked_at,started_at,last_success_at,next_check_at,package_name,checked_version,checks_json,history_json)
                VALUES($id,$state,$message,$at,$began,$success,$next,$package,$version,$checks,$history)
                ON CONFLICT(source_id) DO UPDATE SET state=excluded.state,message=excluded.message,
                checked_at=excluded.checked_at,started_at=excluded.started_at,last_success_at=CASE WHEN excluded.state='working'
                THEN excluded.checked_at ELSE source_health.last_success_at END,next_check_at=excluded.next_check_at,
                package_name=CASE WHEN excluded.package_name!='' THEN excluded.package_name ELSE source_health.package_name END,
                checked_version=excluded.checked_version,checks_json=excluded.checks_json,history_json=excluded.history_json
                """, ("$id", sourceId), ("$state", state), ("$message", safeMessage), ("$at", when), ("$began", began),
                ("$success", state == "working" ? when : 0), ("$next", when + (state == "deferred" ? 300 : DaySeconds)),
                ("$package", package), ("$version", version), ("$checks", checks.ToJsonString()), ("$history", retained.ToJsonString()));
        });
        _record?.Invoke(new() { ["source_id"] = sourceId, ["state"] = state, ["checked_at"] = when, ["origin"] = origin,
            ["checks"] = checks.DeepClone(), ["message"] = message });
    }

    public void UpdateRepairObservations(JsonArray jobs, JsonObject operation)
    {
        var events = new List<(JsonNode? Ids, double At, string Id, string Package)>();
        foreach (var job in jobs.OfType<JsonObject>())
            if (SourceResponseContract.Text(job["state"]) == "installed")
                events.Add((job["expected_ids"], Stamp(job["updated_at"]), SourceResponseContract.Text(job["id"]), SourceResponseContract.Text(job["pkg_name"])));
        var state = SourceResponseContract.Text(operation["state"]);
        var extension = operation["extension"] as JsonObject ?? new();
        var sources = (extension["source"] as JsonObject)?["nodes"] as JsonArray ?? [];
        var ids = new JsonArray(sources.OfType<JsonObject>().Select(row => row["id"]?.DeepClone()).ToArray());
        if (state is "installed" or "recovered") events.Add((ids, Stamp(operation["installed_at"]), SourceResponseContract.Text(operation["id"]), SourceResponseContract.Text(extension["pkgName"])));
        var changedAt = state == "rolled-back" ? Stamp(operation["changed_at"]) : 0;
        _database.Write(DatabaseFile.Catalog, session =>
        {
            if (changedAt > 0)
                foreach (var id in ids)
                    if (ValidSource(SourceResponseContract.Identity(id)) is { } sid)
                        session.Execute("""
                            INSERT INTO source_health(source_id,last_changed_at) VALUES($id,$at)
                            ON CONFLICT(source_id) DO UPDATE SET last_changed_at=max(source_health.last_changed_at,excluded.last_changed_at)
                            """, ("$id", sid), ("$at", changedAt));
            foreach (var item in events)
            {
                if (item.At == 0 || item.Ids is not JsonArray values || values.Count > 200 || !Regex.IsMatch(item.Id, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant)) continue;
                foreach (var value in values)
                    if (ValidSource(SourceResponseContract.Identity(value)) is { } sid)
                        session.Execute("""
                            INSERT INTO source_health(source_id,last_repaired_at,repair_job_id,package_name) VALUES($id,$at,$job,$package)
                            ON CONFLICT(source_id) DO UPDATE SET last_repaired_at=excluded.last_repaired_at,repair_job_id=excluded.repair_job_id,package_name=excluded.package_name
                            WHERE excluded.last_repaired_at>source_health.last_repaired_at
                            """, ("$id", sid), ("$at", item.At), ("$job", item.Id), ("$package", item.Package[..Math.Min(240, item.Package.Length)]));
            }
            return 0;
        });
    }

    public static double Stamp(JsonNode? value)
    {
        if (value?.GetValueKind() != JsonValueKind.Number || !double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number)) return 0;
        return double.IsFinite(number) && number is > 0 and < 100_000_000_000 ? number : 0;
    }
    private static string? ValidSource(string? id) => id is not null && Regex.IsMatch(id, "^-?[0-9]{1,20}$", RegexOptions.CultureInvariant) ? id : null;
    private static JsonArray ArrayOrEmpty(string? text)
    {
        try { return JsonNode.Parse(text ?? "[]") as JsonArray ?? new(); }
        catch (JsonException) { return new(); }
    }
}
