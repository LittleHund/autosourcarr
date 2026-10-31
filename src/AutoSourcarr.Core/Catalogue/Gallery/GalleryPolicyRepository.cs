using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Gallery;

/// <summary>Existing source policy persistence; layout execution stays with the real journal/task owner.</summary>
public sealed class GalleryPolicyRepository(SqliteDatabase database, object sourceWriteGate, Action? invalidate = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public void Initialize() => new SchemaMigrator(database).EnsureFeatureSchema("gallery-policy.sql");
    public JsonObject Read(string sourceId) => database.Read(DatabaseFile.Catalog, session => Read(session, sourceId));
    public JsonObject Effective(string sourceId) => database.Read(DatabaseFile.Catalog, session => Effective(session, sourceId));

    /// <summary>All source membership, policy and tag-review writers must use this same sourceWriteGate.</summary>
    public JsonObject Save(string sourceId, JsonNode? value, long revision)
    {
        var validated = GalleryPolicy.Validate(value);
        lock (sourceWriteGate)
        {
            var result = database.Write(DatabaseFile.Catalog, session =>
            {
                var current = Read(session, sourceId);
                if (GalleryStorage.Integer(current["revision"]) != revision) throw new ArgumentException("Gallery policy changed; reload before saving");
                session.Execute("""
                    INSERT INTO gallery_policies(source_id,revision,policy_json,updated_at) VALUES($source,$revision,$policy,$time)
                    ON CONFLICT(source_id) DO UPDATE SET revision=excluded.revision,policy_json=excluded.policy_json,updated_at=excluded.updated_at
                    """, ("$source", sourceId), ("$revision", checked(revision + 1)), ("$policy", validated.ToJsonString()), ("$time", _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture)));
                return Read(session, sourceId);
            });
            invalidate?.Invoke(); return result;
        }
    }
    public IReadOnlyList<string> Pending() => database.Read(DatabaseFile.Catalog, session => session.Query("SELECT source_id FROM gallery_policies WHERE layout_revision<>revision").Select(row => row.Text("source_id")).ToArray());

    /// <summary>Only the actual layout owner may acknowledge its completed revision; stale receipts cannot clear a newer policy.</summary>
    public bool RecordLayout(string sourceId, long revision, JsonObject result)
    {
        lock (sourceWriteGate) return database.Write(DatabaseFile.Catalog, session => session.Execute("UPDATE gallery_policies SET layout_revision=$revision,layout_result_json=$result WHERE source_id=$source AND revision=$revision",
            ("$revision", revision), ("$result", result.ToJsonString()), ("$source", sourceId))) != 0;
    }
    internal static JsonObject Effective(DatabaseSession session, string sourceId)
    {
        var raw = session.Scalar("SELECT policy_json FROM gallery_policies WHERE source_id=$source", ("$source", sourceId)) as string;
        return raw is null ? GalleryPolicy.Default : JsonNode.Parse(raw) as JsonObject ?? throw new InvalidDataException("Invalid saved gallery policy");
    }
    private static JsonObject Read(DatabaseSession session, string sourceId)
    {
        var source = session.Query("SELECT source_id,name,lang,base_url FROM source_config WHERE source_id=$source", ("$source", sourceId)).FirstOrDefault();
        var row = session.Query("SELECT * FROM gallery_policies WHERE source_id=$source", ("$source", sourceId)).FirstOrDefault();
        var sharedSchema = session.Scalar("SELECT 1 FROM sqlite_master WHERE type='table' AND name='catalogue_source_facts'") is not null;
        if (source is null && row is null && (!sharedSchema || session.Scalar("SELECT 1 FROM catalogue_source_facts WHERE source_id=$source LIMIT 1", ("$source", sourceId)) is null)) throw new KeyNotFoundException(sourceId);
        var sourceValue = source is null ? new JsonObject { ["source_id"] = sourceId, ["name"] = "Shared source " + sourceId, ["lang"] = "not installed", ["base_url"] = "" } : GalleryStorage.Json(source);
        var combined = Convert.ToInt64(session.Scalar("""
            SELECT COUNT(*) FROM (SELECT entry_id FROM (
              SELECT entry_id,source_id,source_manga_id FROM source_matches WHERE active=1
              UNION SELECT entry_id,source_id,source_manga_id FROM source_variants WHERE active=1)
              GROUP BY entry_id HAVING COUNT(*)>1 AND SUM(source_id=$source)>0)
            """, ("$source", sourceId)), CultureInfo.InvariantCulture);
        if (sharedSchema) combined += Convert.ToInt64(session.Scalar("""
            SELECT COUNT(*) FROM (SELECT f.entry_id FROM catalogue_source_facts f JOIN entries e ON e.id=f.entry_id
              WHERE NOT EXISTS(SELECT 1 FROM source_matches m WHERE m.entry_id=f.entry_id AND m.active=1)
                AND NOT EXISTS(SELECT 1 FROM source_variants v WHERE v.entry_id=f.entry_id AND v.active=1)
              GROUP BY f.entry_id HAVING COUNT(*)>1 AND SUM(f.source_id=$source)>0)
            """, ("$source", sourceId)), CultureInfo.InvariantCulture);
        return new() { ["source"] = sourceValue, ["revision"] = row?.Integer("revision") ?? 0,
            ["policy"] = row is null ? GalleryPolicy.Default : JsonNode.Parse(row.Text("policy_json")), ["detected"] = GalleryMetadata.IsGallerySource(sourceValue), ["combined_entries"] = combined,
            ["layout_pending"] = row is not null && row.Integer("layout_revision") != row.Integer("revision"), ["layout_result"] = row is null ? new JsonObject() : JsonNode.Parse(row.Text("layout_result_json")),
            ["note"] = "Selected criteria add requirements to native title, creator, edition and animation safeguards. All selected checks must pass. Turning combining off separates gallery records; chapter pages and served IDs are retained." };
    }
}

internal static class GalleryStorage
{
    public static JsonObject Json(DatabaseRow row) => JsonSerializer.SerializeToNode(row.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))!.AsObject();
    public static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
    public static long Integer(JsonNode? value) => value is JsonValue scalar ? scalar.TryGetValue<long>(out var number) ? number : scalar.TryGetValue<int>(out var small) ? small : 0 : 0;
    public static bool Flag(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) && flag;
}
