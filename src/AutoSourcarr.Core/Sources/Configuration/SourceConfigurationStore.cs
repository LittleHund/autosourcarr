using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Unicode;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Sources.Configuration;

public enum SourceConfigurationEffectKind { HardDiscoveryInvalidation, SoftDiscoveryInvalidation, DuplicateSnapshotInvalidation }

/// <summary>A committed invalidation intent, never a claim that a reconciliation/layout job was queued.</summary>
public sealed record SourceConfigurationEffect(SourceConfigurationEffectKind Kind)
{
    public JsonObject ToJson() => Kind == SourceConfigurationEffectKind.DuplicateSnapshotInvalidation ? new() { ["kind"] = "duplicates" }
        : new() { ["kind"] = "discovery", ["hard"] = Kind == SourceConfigurationEffectKind.HardDiscoveryInvalidation };
}

/// <summary>The caller must obtain the complete native source list successfully; partial inventories and failure fallbacks cannot be published.</summary>
public sealed class CompletedSourceSnapshot
{
    private readonly JsonObject[] _rows;
    private CompletedSourceSnapshot(JsonObject[] rows) => _rows = rows;
    public static CompletedSourceSnapshot FromCompleteNativeList(IReadOnlyList<JsonObject> rows)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal); var copy = new List<JsonObject>();
        foreach (var row in rows)
        {
            var id = SourceConfigurationJson.Identity(row["id"]);
            if (id.Length == 0 || !ids.Add(id)) throw new ArgumentException("Native source snapshot has missing or duplicate identities");
            foreach (var field in new[] { "name", "displayName", "lang", "homeUrl", "baseUrl", "iconUrl" })
                if (row[field] is { } value && value.GetValueKind() != JsonValueKind.String) throw new ArgumentException("Native source snapshot metadata is malformed");
            if (row["supportsLatest"] is { } latest && latest.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Native source snapshot flags are malformed");
            var valueCopy = (JsonObject)row.DeepClone(); valueCopy["id"] = id; copy.Add(valueCopy);
        }
        return new(copy.ToArray());
    }
    internal IReadOnlyList<JsonObject> Rows => _rows;
}

/// <summary>Existing source configuration, with no global source cache, engine command or structural writer.</summary>
public sealed partial class SourceConfigurationStore
{
    private readonly SqliteDatabase _database;
    private readonly object _sourceWriteGate;
    private readonly Func<IReadOnlySet<string>> _allowedLanguages;
    private readonly Action<SourceConfigurationEffect> _committed;
    private readonly TimeProvider _clock;

    /// <param name="sourceWriteGate">The same object used by every catalogue, reader, gallery, matching-policy and tag-review writer.</param>
    /// <param name="committed">Required real integration owner for invalidation intents. This store reports no scheduling success.</param>
    public SourceConfigurationStore(SqliteDatabase database, object sourceWriteGate, Func<IReadOnlySet<string>> allowedLanguages,
        Action<SourceConfigurationEffect> committed, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(committed); ArgumentNullException.ThrowIfNull(allowedLanguages);
        _database = database; _sourceWriteGate = sourceWriteGate; _allowedLanguages = allowedLanguages; _committed = committed; _clock = clock ?? TimeProvider.System;
    }
    public IReadOnlyList<JsonObject> ListSources()
    { lock (_sourceWriteGate) return _database.Read(DatabaseFile.Catalog, session => Sources(session).Where(value => SourceConfigurationJson.Flag(value["available"])).ToArray()); }
    public JsonObject GetSource(string sourceId)
    { lock (_sourceWriteGate) return _database.Read(DatabaseFile.Catalog, session => Sources(session).FirstOrDefault(value => SourceConfigurationJson.Text(value["source_id"]) == sourceId) ?? throw new KeyNotFoundException(sourceId)); }
    public IReadOnlyList<JsonObject> ListGroups()
    { lock (_sourceWriteGate) return _database.Read(DatabaseFile.Catalog, Groups); }

    /// <summary>Publish only an authoritative completed native result. Empty reload observations keep existing availability and choices.</summary>
    public IReadOnlyList<JsonObject> PersistSnapshot(CompletedSourceSnapshot snapshot)
    {
        lock (_sourceWriteGate)
        {
            var changed = false;
            var result = _database.Write(DatabaseFile.Catalog, session =>
            {
                var sources = snapshot.Rows;
                if (sources.Count == 0 && Convert.ToInt64(session.Scalar("SELECT COUNT(*) FROM source_config"), CultureInfo.InvariantCulture) > 0)
                    return Sources(session).Where(value => SourceConfigurationJson.Flag(value["available"])).ToArray();
                var allowed = _allowedLanguages(); var stamp = SourceConfigurationJson.Timestamp(_clock);
                var existingRows = session.Query("SELECT source_id,name,base_url,priority,available,group_id,official,official_manual FROM source_config");
                var available = existingRows.Where(row => row.Flag("available")).Select(row => row.Text("source_id")).ToHashSet(StringComparer.Ordinal);
                var priorities = existingRows.ToDictionary(row => row.Text("source_id"), row => row.Integer("priority"), StringComparer.Ordinal);
                var families = new Dictionary<(string, string), long>();
                foreach (var row in existingRows)
                {
                    var key = (UnicodeCaseFold.Fold(row.Text("name")), UnicodeCaseFold.Fold(row.Text("base_url"))); var priority = row.Integer("priority");
                    families[key] = Math.Min(families.GetValueOrDefault(key, priority), priority);
                }
                var next = checked(priorities.Values.DefaultIfEmpty(0).Max() + 10);
                session.Execute("UPDATE source_config SET available=0");
                foreach (var source in sources)
                {
                    var id = SourceConfigurationJson.Text(source["id"]); var language = SourceConfigurationJson.Text(source["lang"]).ToLowerInvariant();
                    var name = SourceConfigurationJson.First(source["name"], source["displayName"], JsonValue.Create(id));
                    var url = SourceConfigurationJson.First(source["homeUrl"], source["baseUrl"]); var family = (UnicodeCaseFold.Fold(name), UnicodeCaseFold.Fold(url));
                    var enabled = allowed.Count == 0 || allowed.Contains(language) ? 1 : 0;
                    long priority;
                    if (priorities.TryGetValue(id, out var retained)) priority = retained;
                    else if (families.TryGetValue(family, out retained)) priority = retained;
                    else { priority = next; families[family] = priority; next = checked(next + 10); }
                    session.Execute("""
                        INSERT INTO source_config(source_id,name,display_name,lang,base_url,icon_url,supports_latest,enabled_language,enabled_search,enabled_chapters,priority,last_seen,available,official,official_manual)
                        VALUES($id,$name,$display,$lang,$url,$icon,$latest,$enabled,$enabled,$enabled,$priority,$time,1,$official,0)
                        ON CONFLICT(source_id) DO UPDATE SET name=excluded.name,display_name=excluded.display_name,lang=excluded.lang,base_url=excluded.base_url,
                          icon_url=excluded.icon_url,supports_latest=excluded.supports_latest,last_seen=excluded.last_seen,available=1,official=excluded.official,official_manual=0
                        """, ("$id", id), ("$name", name), ("$display", SourceConfigurationJson.Text(source["displayName"])), ("$lang", language), ("$url", url),
                        ("$icon", SourceConfigurationJson.Text(source["iconUrl"])), ("$latest", !source.ContainsKey("supportsLatest") || SourceConfigurationJson.Truth(source["supportsLatest"]) ? 1 : 0),
                        ("$enabled", enabled), ("$priority", priority), ("$time", stamp), ("$official", ChapterResolver.ProbablyOfficialSource(name, url) ? 1 : 0));
                }
                foreach (var family in session.Query("SELECT name,base_url,MIN(priority) priority FROM source_config WHERE available=1 GROUP BY name,base_url"))
                    session.Execute("UPDATE source_config SET priority=$priority WHERE name=$name AND base_url=$url", ("$priority", family.Integer("priority")), ("$name", family.Text("name")), ("$url", family.Text("base_url")));
                ApplyGroupPriorities(session);
                changed = !available.SetEquals(sources.Select(source => SourceConfigurationJson.Text(source["id"])));
                return Sources(session).Where(value => SourceConfigurationJson.Flag(value["available"])).ToArray();
            });
            if (changed) Notify(SourceConfigurationEffectKind.HardDiscoveryInvalidation); return result;
        }
    }
    internal static IReadOnlyList<JsonObject> Sources(DatabaseSession session) => session.Query("""
        SELECT sc.*,sg.name group_name,sg.priority group_priority FROM source_config sc LEFT JOIN source_groups sg ON sg.id=sc.group_id
        ORDER BY sc.priority,sc.name COLLATE NOCASE,sc.lang COLLATE NOCASE
        """).Select(row =>
        {
            var value = SourceConfigurationJson.Json(row);
            foreach (var field in new[] { "supports_latest", "enabled_language", "enabled_search", "enabled_chapters", "available", "official", "official_manual" }) value[field] = row.Flag(field);
            return value;
        }).ToArray();
    internal static IReadOnlyList<JsonObject> Groups(DatabaseSession session) => session.Query("""
        SELECT sg.id,sg.name,sg.priority,sg.created_at,sg.updated_at,COUNT(sc.source_id) member_variants
        FROM source_groups sg LEFT JOIN source_config sc ON sc.group_id=sg.id GROUP BY sg.id ORDER BY sg.priority,sg.name COLLATE NOCASE
        """).Select(SourceConfigurationJson.Json).ToArray();
    private static void ApplyGroupPriorities(DatabaseSession session) => session.Execute("UPDATE source_config SET priority=(SELECT sg.priority FROM source_groups sg WHERE sg.id=source_config.group_id) WHERE group_id IS NOT NULL");
    private void Notify(SourceConfigurationEffectKind kind) => _committed(new(kind));
}
