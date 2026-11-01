using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Matching.Manual;

/// <summary>Small catalogue helpers shared by manual decisions and catalogue maintenance.</summary>
internal static class ManualSql
{
    private const int AliasHops = 64;

    public static (string Name, object? Value)[] P(params object?[] values) => values.Select((value, index) => ("$p" + index.ToString(CultureInfo.InvariantCulture), value)).ToArray();

    /// <summary>canonical_entry_id: follows entry_aliases (a cycle stops where it repeats, like 0.4.5).</summary>
    public static long Canonical(DatabaseSession c, long entryId)
    {
        var current = entryId; var seen = new HashSet<long>();
        for (var hop = 0; hop < AliasHops && seen.Add(current); hop++)
        {
            var next = c.Scalar("SELECT canonical_entry_id FROM entry_aliases WHERE old_entry_id=$p0", P(current));
            if (next is null) return current;
            current = Convert.ToInt64(next, CultureInfo.InvariantCulture);
        }
        return current;
    }

    public static bool EntryExists(DatabaseSession c, long entryId) => c.Scalar("SELECT 1 FROM entries WHERE id=$p0", P(entryId)) is not null;

    /// <summary>_find_existing_by_source: the canonical entry owning the active record (primary first), or null.</summary>
    public static long? Owner(DatabaseSession c, string sourceId, long sourceMangaId)
    {
        var owner = c.Scalar("SELECT entry_id FROM source_matches WHERE source_id=$p0 AND source_manga_id=$p1 AND active=1", P(sourceId, sourceMangaId))
            ?? c.Scalar("SELECT entry_id FROM source_variants WHERE source_id=$p0 AND source_manga_id=$p1 AND active=1", P(sourceId, sourceMangaId));
        return owner is null ? null : Canonical(c, Convert.ToInt64(owner, CultureInfo.InvariantCulture));
    }

    public static (long Left, long Right) Pair(long left, long right) => left < right ? (left, right) : (right, left);

    public static string RecordKey(string sourceId, long sourceMangaId) => sourceId + ":" + sourceMangaId.ToString(CultureInfo.InvariantCulture);

    /// <summary>_record_payload: the source manga shape of a stored record row.</summary>
    public static JsonObject Payload(DatabaseRow row)
    {
        JsonObject payload;
        try { payload = JsonNode.Parse(row.Text("metadata_json", "{}")) as JsonObject ?? new(); }
        catch (System.Text.Json.JsonException) { payload = new(); }
        string Pick(string column, string key) => row.Text(column) is { Length: > 0 } value ? value : payload[key] is JsonValue existing && existing.TryGetValue<string>(out var text) ? text : "";
        payload["id"] = row.Integer("source_manga_id");
        payload["url"] = Pick("source_manga_url", "url");
        payload["title"] = Pick("source_title", "title");
        payload["thumbnailUrl"] = Pick("thumbnail_url", "thumbnailUrl");
        payload["description"] = Pick("description", "description");
        payload["author"] = Pick("author", "author");
        payload["artist"] = Pick("artist", "artist");
        if (row.Text("genre").Length > 0) payload["genre"] = row.Text("genre");
        else if (payload["genre"] is null) payload["genre"] = new JsonArray();
        if (row.Integer("status") != 0) payload["status"] = row.Integer("status");
        else if (payload["status"] is null) payload["status"] = 0;
        payload["realUrl"] = Pick("real_url", "realUrl");
        payload["initialized"] = row.Flag("initialized");
        return payload;
    }

    /// <summary>A row as JSON (column names as they are).</summary>
    public static JsonObject Json(DatabaseRow row)
    {
        var result = new JsonObject();
        foreach (var (key, value) in row)
            result[key] = value switch
            {
                null => null,
                long number => number,
                int number => number,
                double number => number,
                string text => text,
                bool flag => flag,
                byte[] => null,
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            };
        return result;
    }

    public static string Text(JsonNode? node)
    {
        if (node is not JsonValue value) return "";
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<long>(out var number)) return number.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var real)) return real.ToString(CultureInfo.InvariantCulture);
        return value.TryGetValue<bool>(out var flag) ? (flag ? "true" : "false") : "";
    }

    public static long Integer(JsonNode? node)
    {
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue<long>(out var number)) return number;
        if (value.TryGetValue<int>(out var small)) return small;
        if (value.TryGetValue<double>(out var real) && Math.Abs(real % 1) < double.Epsilon && Math.Abs(real) < 9e18) return (long)real;
        return value.TryGetValue<string>(out var text) && long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    public static bool Truth(JsonNode? node) => node switch
    {
        null => false,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        JsonValue value when value.TryGetValue<string>(out var text) => text.Length > 0,
        JsonValue value when value.TryGetValue<double>(out var number) => number != 0,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        _ => true,
    };

    public static JsonArray JsonList(IEnumerable<JsonNode?> items) => new(items.ToArray());
}
