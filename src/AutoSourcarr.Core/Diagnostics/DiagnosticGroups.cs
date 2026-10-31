using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Unicode;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Core.Diagnostics;

public static class DiagnosticGroups
{
    public static IReadOnlyList<JsonObject> Group(IEnumerable<JsonObject> items)
    {
        var groups = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var identity = Identity(item);
            if (!groups.TryGetValue(identity, out var group))
            {
                group = (JsonObject)item.DeepClone(); group["group_key"] = "diagnostic-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
                group["count"] = 0L; group["record_count"] = 0L; group["member_ids"] = new JsonArray(); group["paths"] = new JsonArray();
                groups.Add(identity, group);
            }
            else if (string.CompareOrdinal(LastSeen(item), LastSeen(group)) > 0)
            {
                foreach (var field in new[] { "id", "key", "message", "detail", "source_context", "path", "last_seen", "resolved_at", "app_version", "instance_id", "context", "exception_chain", "detail_truncated" })
                    if (item.ContainsKey(field)) group[field] = item[field]?.DeepClone(); else group.Remove(field);
            }
            group["count"] = D.Integer(group, "count") + Math.Max(1, D.Integer(item, "count", 1));
            group["record_count"] = D.Integer(group, "record_count") + 1;
            var members = group["member_ids"]!.AsArray();
            if (!members.Any(id => JsonNode.DeepEquals(id, item["id"]))) members.Add(item["id"]?.DeepClone());
            if (D.Text(item, "path") is { Length: > 0 } path && !group["paths"]!.AsArray().Any(value => D.Text(value) == path)) group["paths"]!.AsArray().Add(path);
            var first = D.Text(item, "first_seen");
            if (first.Length > 0 && (D.Text(group, "first_seen").Length == 0 || string.CompareOrdinal(first, D.Text(group, "first_seen")) < 0)) group["first_seen"] = first;
        }
        return groups.Values.OrderByDescending(group => D.Flag(group, "pinned") && !D.Flag(group, "resolved"))
            .ThenByDescending(LastSeen, StringComparer.Ordinal).ToArray();
    }

    private static string Identity(JsonObject item)
    {
        var context = item["source_context"] as JsonObject ?? new();
        var source = D.Text(context, "source_id");
        if (source.Length == 0) source = UnicodeCaseFold.Fold(D.Text(context, "source_name").Trim());
        var key = D.Text(item, "key");
        if (source.Length == 0 && key.StartsWith("import-source:", StringComparison.Ordinal)) source = key["import-source:".Length..];
        var message = KnownPatterns.Whitespace().Replace(D.Text(item, "message", "Unknown error"), " ").Trim();
        message = Regex.Replace(message, @"\s*\(error [a-f0-9]{12}\)", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (source.Length > 0) message = Regex.Split(message, @"\s+at\s+(?:[\w$]+\.)+[\w$<>]+\(", RegexOptions.CultureInvariant).First();
        message = Regex.Replace(message, @"then retry in \d+ seconds", "then retry after cooldown", RegexOptions.CultureInvariant);
        object[] identity = [source.Length == 0 && key.StartsWith("source-host-pages:", StringComparison.Ordinal) ? key : "",
            D.Flag(item, "client_side"), D.Text(item, "component", "AutoSourcarr"), D.Text(item, "severity", "error"), D.Integer(item, "status_code"),
            D.Text(item, "error_type"), D.Flag(item, "resolved"), D.Flag(item, "pinned"), source, D.Text(item, "method").ToUpperInvariant(), Route(D.Text(item, "path")), message];
        return "[" + string.Join(", ", identity.Select(PythonScalar)) + "]";
    }
    private static string Route(string text)
    {
        var origin = Regex.Match(text, @"^(?<scheme>[A-Za-z][A-Za-z0-9+.-]*):\/\/(?<authority>[^/?#]*)", RegexOptions.CultureInvariant);
        var head = origin.Success ? origin.Groups["scheme"].Value.ToLowerInvariant() + "://" + origin.Groups["authority"].Value : "";
        var route = origin.Success ? text[origin.Length..] : text;
        var separator = route.IndexOfAny(['?', '#']); if (separator >= 0) route = route[..separator];
        route = Regex.Replace(route, @"(/api/(?:admin|v1)/chapters/)\d+(?=/|$)", "$1{id}", RegexOptions.CultureInvariant);
        route = Regex.Replace(route, @"(/api/(?:admin|v1)/entries/)\d+(?=/|$)", "$1{id}", RegexOptions.CultureInvariant);
        route = Regex.Replace(route, @"(/api/admin/imports/)[A-Za-z0-9_-]+(/items/)\d+(?=/|$)", "$1{run}$2{id}", RegexOptions.CultureInvariant);
        return head + route;
    }
    private static string PythonScalar(object value)
    {
        if (value is bool flag) return flag ? "true" : "false";
        if (value is long number) return number.ToString(CultureInfo.InvariantCulture);
        return CanonicalJson.String((string)value);
    }
    private static string LastSeen(JsonObject row) => D.Text(row, "last_seen") is { Length: > 0 } last ? last : D.Text(row, "first_seen");
}

internal static class D
{
    public static string Text(JsonObject row, string field, string fallback = "") => row[field] is { } value && Text(value).Length > 0 ? Text(value) : fallback;
    public static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
    public static bool Flag(JsonObject row, string field) => row[field] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    public static bool Truth(JsonNode? value) => value switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text.Length > 0,
        JsonValue scalar when double.TryParse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number != 0,
        _ => value.ToJsonString() is not ("0" or "0.0")
    };
    public static long Integer(JsonObject row, string field, long fallback = 0) => row[field] is JsonValue value && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : fallback;
}
