using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Sources.Configuration;

public sealed partial class SourceConfigurationStore
{
    public JsonObject CapturePreferences()
    {
        lock (_sourceWriteGate) return _database.Read(DatabaseFile.Catalog, session => session.Savepoint(snapshot => new JsonObject
        {
            ["groups"] = SourceConfigurationJson.Array(snapshot.Query("SELECT id,name,priority FROM source_groups ORDER BY id").Select(SourceConfigurationJson.Json)),
            ["sources"] = SourceConfigurationJson.Array(snapshot.Query("SELECT source_id,name,base_url,lang,priority,enabled_language,enabled_search,enabled_chapters,group_id FROM source_config").Select(SourceConfigurationJson.Json)),
            ["matching_policies"] = SourceConfigurationJson.Array(snapshot.Query("SELECT * FROM matching_policies").Select(SourceConfigurationJson.Json))
        }));
    }
    public JsonObject RestorePreferences(JsonObject snapshot)
    {
        var oldGroups = PreferenceRows(snapshot, "groups"); var oldSources = PreferenceRows(snapshot, "sources"); var oldPolicies = PreferenceRows(snapshot, "matching_policies");
        var namesByOld = oldGroups.Where(row => row["id"] is not null).ToDictionary(row => SourceConfigurationJson.Integer(row["id"]), row => SourceConfigurationJson.String(row["name"]));
        lock (_sourceWriteGate)
        {
            var result = _database.Write(DatabaseFile.Catalog, session =>
            {
                foreach (var name in namesByOld.Values)
                {
                    var priority = oldGroups.Where(row => SourceConfigurationJson.First(row["name"]) == name).Select(row => SourceConfigurationJson.Truth(row["priority"]) ? SourceConfigurationJson.Integer(row["priority"]) : 1000).FirstOrDefault(1000);
                    session.Execute("""
                        INSERT INTO source_groups(name,priority,created_at,updated_at) VALUES($name,$priority,$time,$time)
                        ON CONFLICT(name) DO UPDATE SET priority=excluded.priority,updated_at=excluded.updated_at
                        """, ("$name", name), ("$priority", priority), ("$time", SourceConfigurationJson.Timestamp(_clock)));
                }
                var groups = session.Query("SELECT id,name FROM source_groups").ToDictionary(row => row.Text("name"), row => row.Integer("id"), StringComparer.Ordinal);
                var current = session.Query("SELECT source_id,name,base_url,lang FROM source_config");
                var currentIds = current.Select(row => row.Text("source_id")).ToHashSet(StringComparer.Ordinal);
                var sourceMap = oldSources.Where(row => row["source_id"] is not null && currentIds.Contains(SourceConfigurationJson.String(row["source_id"])))
                    .ToDictionary(row => SourceConfigurationJson.String(row["source_id"]), row => SourceConfigurationJson.String(row["source_id"]), StringComparer.Ordinal);
                var claimed = sourceMap.Values.ToHashSet(StringComparer.Ordinal);
                var missing = oldSources.Where(row => row["source_id"] is not null && !sourceMap.ContainsKey(SourceConfigurationJson.String(row["source_id"]))).ToArray();
                static (string, string, string) Identity(JsonObject row) => (SourceConfigurationJson.First(row["name"]), SourceConfigurationJson.First(row["base_url"]), SourceConfigurationJson.First(row["lang"]));
                foreach (var old in missing)
                {
                    var identity = Identity(old); var matches = current.Where(row => !claimed.Contains(row.Text("source_id")) && (row.Text("name"), row.Text("base_url"), row.Text("lang")) == identity).ToArray();
                    if (matches.Length == 1 && missing.Count(row => Identity(row) == identity) == 1)
                    { var id = matches[0].Text("source_id"); sourceMap[SourceConfigurationJson.String(old["source_id"])] = id; claimed.Add(id); }
                }
                var restored = 0;
                foreach (var row in oldSources)
                {
                    if (!sourceMap.TryGetValue(SourceConfigurationJson.String(row["source_id"]), out var id)) continue;
                    var name = row["group_id"] is null ? null : namesByOld.GetValueOrDefault(SourceConfigurationJson.Integer(row["group_id"]));
                    long? group = name is not null && name.Length > 0 && groups.TryGetValue(name, out var found) ? found : null;
                    restored += session.Execute("UPDATE source_config SET priority=$priority,enabled_language=$language,enabled_search=$search,enabled_chapters=$chapters,group_id=$group WHERE source_id=$source",
                        ("$priority", SourceConfigurationJson.Truth(row["priority"]) ? SourceConfigurationJson.Integer(row["priority"]) : 1000),
                        ("$language", SourceConfigurationJson.Truth(row["enabled_language"]) ? 1 : 0), ("$search", SourceConfigurationJson.Truth(row["enabled_search"]) ? 1 : 0), ("$chapters", SourceConfigurationJson.Truth(row["enabled_chapters"]) ? 1 : 0),
                        ("$group", group), ("$source", id));
                }
                ApplyGroupPriorities(session);
                foreach (var policy in oldPolicies)
                {
                    var scope = SourceConfigurationJson.Text(policy["scope"]); string? subject;
                    if (scope == "group")
                    {
                        var name = namesByOld.GetValueOrDefault(SourceConfigurationJson.Integer(policy["subject"]));
                        subject = name is not null && groups.TryGetValue(name, out var group) ? group.ToString(CultureInfo.InvariantCulture) : null;
                    }
                    else subject = sourceMap.GetValueOrDefault(SourceConfigurationJson.String(policy["subject"]));
                    var direction = SourceConfigurationJson.Text(policy["direction"]);
                    if (subject is null || direction is not ("internal" or "external")) continue;
                    var rule = MatchingRuleConfiguration.Validate(JsonNode.Parse(SourceConfigurationJson.Text(policy["rule_json"])));
                    session.Execute("""
                        INSERT INTO matching_policies(scope,subject,direction,rule_json,updated_at) VALUES($scope,$subject,$direction,$rule,$time)
                        ON CONFLICT(scope,subject,direction) DO UPDATE SET rule_json=excluded.rule_json,updated_at=excluded.updated_at
                        """, ("$scope", scope), ("$subject", subject), ("$direction", direction), ("$rule", SourceConfigurationJson.PythonJson(rule)), ("$time", SourceConfigurationJson.Timestamp(_clock)));
                }
                return new JsonObject { ["groups"] = groups.Count, ["sources"] = restored };
            });
            Notify(SourceConfigurationEffectKind.DuplicateSnapshotInvalidation); return result;
        }
    }
    private static JsonObject[] PreferenceRows(JsonObject snapshot, string name)
    {
        if (snapshot[name] is null) return [];
        if (snapshot[name] is not JsonArray rows || rows.Any(row => row is not JsonObject)) throw new ArgumentException("Invalid source preference snapshot");
        return rows.OfType<JsonObject>().ToArray();
    }
}
