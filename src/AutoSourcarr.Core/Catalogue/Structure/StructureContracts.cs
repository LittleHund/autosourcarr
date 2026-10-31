using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Structure;

public readonly record struct NativeSourceRecord(string SourceId, long MangaId);

/// <summary>Real gallery chapter ingestion/replay for a previously committed gallery-policy split.</summary>
public interface ICatalogueGalleryReplay
{
    void Restore(long splitId, IReadOnlyList<NativeSourceRecord> records, IReadOnlyDictionary<NativeSourceRecord, long> currentOwners);
}

public sealed class CatalogueStructureRecoveryException(string message, JsonObject status) : InvalidOperationException(message)
{
    public JsonObject Status { get; } = (JsonObject)status.DeepClone();
}

internal static class StructureSql
{
    public static (string Name, object? Value)[] P(params object?[] values) => values.Select((value, index) => ("$p" + index, value)).ToArray();
    public static JsonObject Json(DatabaseRow row) => System.Text.Json.JsonSerializer.SerializeToNode(row.ToDictionary(pair => pair.Key, pair => pair.Value))!.AsObject();
    public static string Text(JsonNode? value, string key, string fallback = "") => value?[key]?.GetValue<string>() ?? fallback;
    public static int Length(string value) => value.EnumerateRunes().Count();
    public static JsonObject Payload(DatabaseRow row)
    {
        JsonObject payload;
        try { payload = JsonNode.Parse(row.Text("metadata_json", "{}")) as JsonObject ?? new(); } catch (System.Text.Json.JsonException) { payload = new(); }
        payload["id"] = row.Integer("source_manga_id");
        foreach (var (column, name) in new[] { ("source_manga_url", "url"), ("source_title", "title"), ("thumbnail_url", "thumbnailUrl"), ("description", "description"), ("author", "author"), ("artist", "artist"), ("genre", "genre"), ("real_url", "realUrl") })
            if (row.Text(column).Length != 0) payload[name] = row.Text(column); else if (!payload.ContainsKey(name)) payload[name] = name == "genre" ? new JsonArray() : JsonValue.Create("");
        if (row.Integer("status") != 0 || !payload.ContainsKey("status")) payload["status"] = row.Integer("status"); payload["initialized"] = row.Flag("initialized"); return payload;
    }
    public static JsonObject GalleryPayload(DatabaseRow row)
    {
        JsonObject payload;
        try { payload = JsonNode.Parse(row.Text("metadata_json", "{}")) as JsonObject ?? new(); } catch (System.Text.Json.JsonException) { payload = new(); }
        if (!payload.ContainsKey("title")) payload["title"] = row.Text("source_title"); if (!payload.ContainsKey("id")) payload["id"] = row.Integer("source_manga_id");
        foreach (var field in new[] { "description", "author", "artist", "genre" }) if (!payload.ContainsKey(field)) payload[field] = row.Text(field); return payload;
    }
}
