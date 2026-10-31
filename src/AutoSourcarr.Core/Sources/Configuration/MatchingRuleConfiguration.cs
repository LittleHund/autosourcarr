using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Unicode;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Sources.Configuration;

public readonly record struct MatchingRuleKey(string Scope, string Subject, string Direction);
public sealed class MatchingRuleConflictException() : ArgumentException("Matching rules changed in another window. Reload before saving.");

/// <summary>Configuration only. These requirements constrain a real native matcher; this module supplies no base eligible result.</summary>
public sealed class MatchingRuleConfiguration(SqliteDatabase database, object sourceWriteGate, Action<SourceConfigurationEffect> committed, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Action<SourceConfigurationEffect> _committed = committed ?? throw new ArgumentNullException(nameof(committed));
    public static JsonObject? Validate(JsonNode? rule)
    {
        if (rule is null) return null;
        if (rule is not JsonObject obj || !obj.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["enabled", "checks"])
            || obj["enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out var active)) throw new ArgumentException("Use enabled and a set of required checks.");
        if (obj["checks"] is not JsonObject checks || checks.Any(pair => pair.Key is not ("title" or "summary" or "author" or "tags"))) throw new ArgumentException("Unknown matching check.");
        if (active && checks.Count == 0) throw new ArgumentException("Choose at least one required check, or restore inherited rules.");
        foreach (var pair in checks)
        {
            var minimum = pair.Key == "title" ? .94 : .01;
            if (pair.Value is not JsonValue value || value.GetValueKind() != System.Text.Json.JsonValueKind.Number) throw new ArgumentException("Invalid matching threshold");
            var number = SourceConfigurationJson.Number(value); if (!double.IsFinite(number) || number < minimum || number > 1) throw new ArgumentException("Invalid matching threshold");
        }
        return new() { ["enabled"] = active, ["checks"] = checks.DeepClone() };
    }
    public IReadOnlyDictionary<MatchingRuleKey, JsonObject?> Rules()
    { lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, ReadRules); }
    public JsonObject Read()
    { lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, Configuration); }
    public JsonObject Save(string scope, string subject, string direction, JsonNode? rule, string expectedRevision)
    {
        if (scope is not ("source" or "group") || direction is not ("internal" or "external")) throw new ArgumentException("Unknown source/group or matching direction.");
        var value = Validate(rule);
        lock (sourceWriteGate)
        {
            var result = database.Write(DatabaseFile.Catalog, session =>
            {
                if (Revision(ReadRules(session)) != expectedRevision) throw new MatchingRuleConflictException();
                var exists = scope == "source" ? session.Scalar("SELECT 1 FROM source_config WHERE source_id=$subject", ("$subject", subject))
                    : session.Scalar("SELECT 1 FROM source_groups WHERE CAST(id AS TEXT)=$subject", ("$subject", subject));
                if (exists is null) throw new KeyNotFoundException(subject);
                if (value is null) session.Execute("DELETE FROM matching_policies WHERE scope=$scope AND subject=$subject AND direction=$direction", ("$scope", scope), ("$subject", subject), ("$direction", direction));
                else session.Execute("""
                    INSERT INTO matching_policies(scope,subject,direction,rule_json,updated_at) VALUES($scope,$subject,$direction,$rule,$time)
                    ON CONFLICT(scope,subject,direction) DO UPDATE SET rule_json=excluded.rule_json,updated_at=excluded.updated_at
                    """, ("$scope", scope), ("$subject", subject), ("$direction", direction), ("$rule", SourceConfigurationJson.PythonJson(value)), ("$time", SourceConfigurationJson.Timestamp(_clock)));
                return Configuration(session);
            });
            _committed(new(SourceConfigurationEffectKind.DuplicateSnapshotInvalidation)); return result;
        }
    }
    public static string Revision(IReadOnlyDictionary<MatchingRuleKey, JsonObject?> values)
    {
        var ordered = values.OrderBy(pair => pair.Key.Scope, UnicodeCodePointComparer.Instance).ThenBy(pair => pair.Key.Subject, UnicodeCodePointComparer.Instance).ThenBy(pair => pair.Key.Direction, UnicodeCodePointComparer.Instance);
        var rows = new JsonArray(ordered.Select(pair => (JsonNode)new JsonArray(new JsonArray(pair.Key.Scope, pair.Key.Subject, pair.Key.Direction), pair.Value?.DeepClone())).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceConfigurationJson.PythonJson(rows, sortKeys: true)))).ToLowerInvariant();
    }
    public static (JsonObject? Rule, string Origin) Effective(IReadOnlyDictionary<MatchingRuleKey, JsonObject?> values, IReadOnlyDictionary<string, JsonObject> sources, string sourceId, string direction)
    {
        if (values.TryGetValue(new("source", sourceId, direction), out var own) && own is not null) return ((JsonObject)own.DeepClone(), "source " + sourceId);
        var group = sources.TryGetValue(sourceId, out var row) && row["group_id"] is { } id ? SourceConfigurationJson.Integer(id).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        var inherited = group is not null ? values.GetValueOrDefault(new("group", group, direction)) : null;
        return (inherited is null ? null : (JsonObject)inherited.DeepClone(), "group " + (group ?? "None"));
    }
    private static IReadOnlyDictionary<MatchingRuleKey, JsonObject?> ReadRules(DatabaseSession session) => session.Query("SELECT scope,subject,direction,rule_json FROM matching_policies")
        .ToDictionary(row => new MatchingRuleKey(row.Text("scope"), row.Text("subject"), row.Text("direction")), row => Validate(JsonNode.Parse(row.Text("rule_json"))));
    private static JsonObject Configuration(DatabaseSession session)
    {
        var values = ReadRules(session); var sources = SourceConfigurationStore.Sources(session);
        return new() { ["revision"] = Revision(values), ["rules"] = new JsonArray(values.Select(pair => (JsonNode)new JsonObject { ["scope"] = pair.Key.Scope, ["subject"] = pair.Key.Subject, ["direction"] = pair.Key.Direction, ["rule"] = pair.Value?.DeepClone() }).ToArray()),
            ["sources"] = new JsonArray(sources.Select(source => (JsonNode)new JsonObject { ["id"] = source["source_id"]!.DeepClone(), ["name"] = SourceConfigurationJson.First(source["name"], source["source_id"]), ["lang"] = source["lang"]!.DeepClone(), ["group_id"] = source["group_id"]?.DeepClone() }).ToArray()),
            ["groups"] = SourceConfigurationJson.Array(SourceConfigurationStore.Groups(session)) };
    }
}
