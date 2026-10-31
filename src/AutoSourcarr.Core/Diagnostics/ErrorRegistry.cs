using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Diagnostics;

public sealed class ErrorRegistry(RuntimeIdentity identity, SupportPrivacy privacy, int maximumEntries = 100,
    Action<JsonObject>? recordLog = null, Func<DateTimeOffset>? clock = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<JsonObject>> _items = new(StringComparer.Ordinal);
    private readonly LinkedList<JsonObject> _order = [];
    private readonly int _limit = Math.Max(20, maximumEntries);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public string Record(string component, string message, string? key = null, string severity = "error", string path = "", string method = "",
        int statusCode = 500, string errorType = "", string detail = "", JsonObject? sourceContext = null, bool pinned = false, JsonObject? context = null, Exception? exception = null)
    {
        component = Cut(component.Length > 0 ? component : "AutoSourcarr", 120);
        message = Cut(privacy.Text(message.Length > 0 ? message : "Unknown error"), 2000);
        severity = severity.ToLowerInvariant(); if (severity is not ("error" or "warning")) severity = "error";
        var signature = Cut(privacy.Text(key ?? $"{component}|{method}|{path}|{errorType}|{message}"), 1200);
        var source = new JsonObject();
        if (sourceContext is not null)
            foreach (var field in new[] { "source_id", "source_name", "source_chapter_id", "source_chapter_url" })
                if (sourceContext.ContainsKey(field)) source[field] = Cut(sourceContext[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : sourceContext[field]?.ToJsonString() ?? "None", 500);
        source = privacy.Scrub(source)!.AsObject();
        var fullDetail = privacy.Text(detail); var chain = DiagnosticContext.ExceptionChain(exception, privacy);
        var metadata = new JsonObject { ["app_version"] = identity.Version, ["instance_id"] = identity.InstanceId,
            ["context"] = privacy.Scrub(context ?? new()), ["exception_chain"] = chain, ["detail_truncated"] = fullDetail.EnumerateRunes().Count() > 12000 };
        recordLog?.Invoke(new() { ["component"] = component, ["message"] = message, ["error_type"] = errorType, ["status_code"] = statusCode,
            ["path"] = privacy.Text(path), ["method"] = method, ["detail"] = Cut(fullDetail, 12000), ["exception_chain"] = chain.DeepClone() });
        var now = Now();
        lock (_gate)
        {
            JsonObject item;
            if (_items.TryGetValue(signature, out var existing))
            {
                item = existing.Value; _order.Remove(existing); _order.AddLast(existing);
                item["count"] = D.Integer(item, "count") + 1; item["pinned"] = pinned || D.Flag(item, "pinned"); item.Remove("resolved_at");
            }
            else
            {
                item = new() { ["id"] = Guid.NewGuid().ToString("N")[..12], ["key"] = signature, ["component"] = component,
                    ["first_seen"] = now, ["count"] = 1L, ["pinned"] = pinned };
                _items.Add(signature, _order.AddLast(item));
            }
            item["last_seen"] = now; item["message"] = message; item["detail"] = Cut(fullDetail, 12000); item["source_context"] = source;
            item["status_code"] = statusCode; item["error_type"] = Cut(errorType, 160); item["path"] = Cut(privacy.Text(path), 500);
            item["method"] = Cut(method, 16); item["severity"] = severity; item["resolved"] = false;
            foreach (var (field, value) in metadata) item[field] = value?.DeepClone();
            var id = D.Text(item, "id");
            while (_items.Count > _limit)
            {
                var victim = _order.FirstOrDefault(row => D.Flag(row, "resolved")) ?? _order.FirstOrDefault(row => !D.Flag(row, "pinned")) ?? _order.First!.Value;
                Remove(D.Text(victim, "key"));
            }
            return id;
        }
    }

    public int Resolve(string? key = null, string? component = null) => ResolveWhere((signature, row) => (key is null || signature == key) && (component is null || D.Text(row, "component") == component));
    public int ResolvePrefix(string prefix) => prefix.Length == 0 ? 0 : ResolveWhere((signature, _) => signature.StartsWith(prefix, StringComparison.Ordinal));
    public int Clear(bool resolvedOnly = true)
    {
        lock (_gate)
        {
            var keys = _order.Where(row => !resolvedOnly || D.Flag(row, "resolved")).Select(row => D.Text(row, "key")).ToArray();
            foreach (var key in keys) Remove(key); return keys.Length;
        }
    }
    public JsonObject Snapshot(int limit = 50, bool includeResolved = true)
    {
        limit = Math.Clamp(limit == 0 ? 50 : limit, 1, 100);
        JsonObject[] rows;
        lock (_gate) rows = _order.Select(row => (JsonObject)row.DeepClone()).ToArray();
        var active = rows.Where(row => !D.Flag(row, "resolved")).ToArray();
        var included = includeResolved ? rows : active;
        var shown = included.Reverse().OrderByDescending(row => D.Flag(row, "pinned") && !D.Flag(row, "resolved")).Take(limit).ToArray();
        var groups = DiagnosticGroups.Group(included); var activeGroups = DiagnosticGroups.Group(active);
        return new() { ["app_version"] = identity.Version, ["instance_id"] = identity.InstanceId, ["started_at"] = identity.StartedAt, ["captured_at"] = Now(),
            ["retention_limit"] = _limit, ["omitted_records"] = included.Length - shown.Length, ["groups"] = Array(groups.Take(limit)),
            ["grouped_active_count"] = activeGroups.Count, ["grouped_active_error_count"] = activeGroups.Count(row => D.Text(row, "severity") == "error"),
            ["grouped_active_warning_count"] = activeGroups.Count(row => D.Text(row, "severity") == "warning"), ["active_count"] = active.Length,
            ["active_error_count"] = active.Count(row => D.Text(row, "severity") == "error"), ["active_warning_count"] = active.Count(row => D.Text(row, "severity") == "warning"),
            ["history_count"] = rows.Length, ["items"] = new JsonArray(shown.Select(row => privacy.Scrub(row)).ToArray()) };
    }
    private int ResolveWhere(Func<string, JsonObject, bool> predicate)
    {
        var changed = 0; var now = Now();
        lock (_gate)
            foreach (var (signature, node) in _items)
                if (predicate(signature, node.Value) && !D.Flag(node.Value, "resolved"))
                { node.Value["resolved"] = true; node.Value["resolved_at"] = now; changed++; }
        return changed;
    }
    private void Remove(string key) { if (_items.Remove(key, out var node)) _order.Remove(node); }
    private string Now() => _clock().ToString("O", CultureInfo.InvariantCulture);
    private static string Cut(string text, int length) => UnicodeCaseFold.Truncate(text, length);
    private static JsonArray Array(IEnumerable<JsonObject> rows) => new(rows.Select(row => (JsonNode?)row.DeepClone()).ToArray());
}
