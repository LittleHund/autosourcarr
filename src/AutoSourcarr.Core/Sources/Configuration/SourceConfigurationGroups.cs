using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Core.Sources.Configuration;

public sealed partial class SourceConfigurationStore
{
    public JsonObject CreateGroup(string name, long priority = 1000)
    {
        var clean = GroupName(name); var value = Math.Clamp(priority == 0 ? 1000 : priority, 1, 100000);
        lock (_sourceWriteGate)
        {
            try
            {
                return _database.Write(DatabaseFile.Catalog, session =>
                {
                    var stamp = SourceConfigurationJson.Timestamp(_clock);
                    session.Execute("INSERT INTO source_groups(name,priority,created_at,updated_at) VALUES($name,$priority,$time,$time)", ("$name", clean), ("$priority", value), ("$time", stamp));
                    var id = Convert.ToInt64(session.Scalar("SELECT last_insert_rowid()"), System.Globalization.CultureInfo.InvariantCulture);
                    return new JsonObject { ["id"] = id, ["name"] = clean, ["priority"] = value, ["member_variants"] = 0 };
                });
            }
            catch (SqliteException error) when (error.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
            { throw new ArgumentException("A source group with that name already exists", error); }
        }
    }
    public JsonObject UpdateGroup(long groupId, string? name = null, long? priority = null)
    {
        var clean = name is null ? null : GroupName(name); var groupPriority = priority.HasValue ? Math.Clamp(priority.Value, 1, 100000) : (long?)null;
        lock (_sourceWriteGate)
        {
            JsonObject result;
            try
            {
                result = _database.Write(DatabaseFile.Catalog, session =>
                {
                    if (session.Scalar("SELECT 1 FROM source_groups WHERE id=$id", ("$id", groupId)) is null) throw new KeyNotFoundException(groupId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (clean is not null) session.Execute("UPDATE source_groups SET name=$name,updated_at=$time WHERE id=$id", ("$name", clean), ("$time", SourceConfigurationJson.Timestamp(_clock)), ("$id", groupId));
                    if (groupPriority.HasValue)
                    {
                        session.Execute("UPDATE source_groups SET priority=$priority,updated_at=$time WHERE id=$id", ("$priority", groupPriority.Value), ("$time", SourceConfigurationJson.Timestamp(_clock)), ("$id", groupId));
                        session.Execute("UPDATE source_config SET priority=$priority WHERE group_id=$id", ("$priority", groupPriority.Value), ("$id", groupId));
                    }
                    return Groups(session).Single(value => SourceConfigurationJson.Integer(value["id"]) == groupId);
                });
            }
            catch (SqliteException error) when (error.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
            { throw new ArgumentException("A source group with that name already exists", error); }
            if (groupPriority.HasValue) Notify(SourceConfigurationEffectKind.SoftDiscoveryInvalidation); return result;
        }
    }
    public JsonObject DeleteGroup(long groupId)
    {
        lock (_sourceWriteGate)
        {
            var result = _database.Write(DatabaseFile.Catalog, session =>
            {
                if (session.Scalar("SELECT 1 FROM source_groups WHERE id=$id", ("$id", groupId)) is null) throw new KeyNotFoundException(groupId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                session.Execute("UPDATE source_config SET group_id=NULL WHERE group_id=$id", ("$id", groupId));
                session.Execute("DELETE FROM source_groups WHERE id=$id", ("$id", groupId));
                session.Execute("DELETE FROM matching_policies WHERE scope='group' AND subject=$id", ("$id", groupId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return new JsonObject { ["deleted"] = true, ["id"] = groupId };
            });
            Notify(SourceConfigurationEffectKind.DuplicateSnapshotInvalidation); return result;
        }
    }
    public JsonObject UpdateSource(string sourceId, JsonObject changes)
    {
        lock (_sourceWriteGate)
        {
            var hard = false; var soft = false;
            var result = _database.Write(DatabaseFile.Catalog, session =>
            {
                var current = session.Query("SELECT group_id,priority FROM source_config WHERE source_id=$source", ("$source", sourceId)).FirstOrDefault() ?? throw new KeyNotFoundException(sourceId);
                var requestedGroup = current["group_id"] is null ? null : (long?)current.Integer("group_id"); DatabaseRow? group;
                if (changes.ContainsKey("group_id")) requestedGroup = changes["group_id"] is null ? null : SourceConfigurationJson.Integer(changes["group_id"]);
                group = requestedGroup.HasValue ? session.Query("SELECT id,priority FROM source_groups WHERE id=$id", ("$id", requestedGroup.Value)).FirstOrDefault() : null;
                if (changes.ContainsKey("group_id") && requestedGroup.HasValue && group is null) throw new ArgumentException("Source group does not exist");
                var allowed = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var key in new[] { "enabled_language", "enabled_search", "enabled_chapters" })
                    if (changes[key] is not null) allowed[key] = SourceConfigurationJson.Truth(changes[key]) ? 1 : 0;
                if (changes.ContainsKey("group_id")) allowed["group_id"] = requestedGroup;
                var requestedPriority = changes["priority"] is not null ? (long?)SourceConfigurationJson.Integer(changes["priority"]) : null;
                if (requestedGroup.HasValue)
                {
                    if (requestedPriority.HasValue)
                    {
                        var priority = Math.Clamp(requestedPriority.Value, 1, 100000);
                        session.Execute("UPDATE source_groups SET priority=$priority,updated_at=$time WHERE id=$id", ("$priority", priority), ("$time", SourceConfigurationJson.Timestamp(_clock)), ("$id", requestedGroup.Value));
                        session.Execute("UPDATE source_config SET priority=$priority WHERE group_id=$id", ("$priority", priority), ("$id", requestedGroup.Value));
                    }
                    else allowed["priority"] = group?.Integer("priority") ?? throw new InvalidDataException("Source group reference is missing");
                }
                else if (requestedPriority.HasValue) allowed["priority"] = Math.Clamp(requestedPriority.Value, 1, 100000);
                if (allowed.Count > 0)
                {
                    var parameters = allowed.Select((pair, index) => (Name: "$value" + index, Value: pair.Value)).ToList(); parameters.Add(("$source", sourceId));
                    var setters = string.Join(",", allowed.Keys.Select((key, index) => key + "=$value" + index));
                    session.Execute("UPDATE source_config SET " + setters + " WHERE source_id=$source", parameters.ToArray());
                }
                hard = allowed.ContainsKey("enabled_language") || allowed.ContainsKey("enabled_search"); soft = requestedPriority.HasValue || changes.ContainsKey("group_id");
                return Sources(session).Single(value => SourceConfigurationJson.Text(value["source_id"]) == sourceId);
            });
            if (hard) Notify(SourceConfigurationEffectKind.HardDiscoveryInvalidation); else if (soft) Notify(SourceConfigurationEffectKind.SoftDiscoveryInvalidation);
            if (changes.ContainsKey("group_id")) Notify(SourceConfigurationEffectKind.DuplicateSnapshotInvalidation); return result;
        }
    }
    private static string GroupName(string? value)
    {
        var clean = Regex.Replace(value ?? "", @"[\s\x1c-\x1f]+", " ").Trim();
        return clean.Length == 0 ? throw new ArgumentException("Source group name is required") : clean;
    }
}
