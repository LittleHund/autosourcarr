using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue;

/// <summary>Presentation ownership only. Matching continues to read original native source evidence.</summary>
public sealed class DisplayMetadata(SqliteDatabase database, object sourceWriteGate, TimeProvider? clock = null)
{
    private static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["description"] = "description", ["artwork"] = "thumbnail_url", ["author"] = "author", ["artist"] = "artist" };
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public JsonObject Read(long entryId)
    {
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, c => Read(c, entryId));
    }

    public JsonObject Save(long entryId, JsonNode? patch, long revision, long? restoreId = null)
    {
        if (patch is not JsonObject edits || edits.Any(pair => !Fields.ContainsKey(pair.Key))) throw new ArgumentException("Unknown display field");
        if (restoreId is not null && edits.Count != 0) throw new ArgumentException("Restore a snapshot separately from field edits");
        lock (sourceWriteGate)
        {
            database.Write(DatabaseFile.Catalog, c =>
            {
                var old = Read(c, entryId);
                if (old["revision"]!.GetValue<long>() != revision) throw new ArgumentException("Metadata ownership changed; reload before saving");
                var fields = old["fields"]!.DeepClone().AsObject();
                if (restoreId is not null)
                {
                    var retained = c.Scalar("SELECT fields_json FROM display_metadata_history WHERE entry_id=$entry AND id=$snapshot", ("$entry", entryId), ("$snapshot", restoreId));
                    if (retained is not string json) throw new ArgumentException("Metadata snapshot is unavailable");
                    fields = Parse(json);
                }
                else foreach (var (name, incoming) in edits)
                {
                    if (incoming is not JsonObject choice || choice.Any(pair => pair.Key is not ("mode" or "value" or "clear" or "source_id" or "source_manga_id"))
                        || Text(choice["mode"]) is not ("inherited" or "locked" or "source")) throw new ArgumentException("Invalid field ownership");
                    var mode = Text(choice["mode"]);
                    if (mode == "inherited") { fields.Remove(name); continue; }
                    if (mode == "source")
                    {
                        var sid = Text(choice["source_id"]);
                        if (sid is null || !Integer(choice["source_manga_id"], out var mid) || Record(c, entryId, sid, mid) is null)
                            throw new ArgumentException("Select an attached source record");
                        fields[name] = new JsonObject { ["mode"] = "source", ["source_id"] = sid, ["source_manga_id"] = mid }; continue;
                    }
                    var clear = false;
                    if (choice.TryGetPropertyValue("clear", out var explicitClear))
                    {
                        if (explicitClear is not JsonValue scalar || !scalar.TryGetValue<bool>(out clear)) throw new ArgumentException("Clear must be explicit");
                    }
                    var value = clear ? "" : Text(choice["value"]);
                    if (value is null || value.EnumerateRunes().LongCount() > 12000) throw new ArgumentException("A locked field requires a value or explicit clear");
                    if (value.Length == 0 && !clear) throw new ArgumentException("Choose a locked value or explicitly clear the field");
                    if (name == "artwork" && value.Length != 0)
                    {
                        var allowed = c.Scalar("""
                            SELECT 1 FROM source_matches WHERE entry_id=$entry AND active=1 AND thumbnail_url=$value
                            UNION ALL SELECT 1 FROM source_variants WHERE entry_id=$entry AND active=1 AND thumbnail_url=$value LIMIT 1
                            """, ("$entry", entryId), ("$value", value));
                        if (allowed is null) throw new ArgumentException("Choose artwork from an attached record");
                    }
                    fields[name] = new JsonObject { ["mode"] = "locked", ["value"] = value };
                }
                var stamp = Timestamp();
                c.Execute("INSERT INTO display_metadata_history(entry_id,old_entry_id,fields_json,reason,created_at) VALUES($id,$id,$fields,$reason,$stamp)",
                    ("$id", entryId), ("$fields", DefaultJson(old["fields"])), ("$reason", restoreId is null ? "before edit" : "rollback"), ("$stamp", stamp));
                c.Execute("INSERT OR REPLACE INTO display_metadata VALUES($id,$revision,$fields,$stamp)",
                    ("$id", entryId), ("$revision", checked(revision + 1)), ("$fields", DefaultJson(fields)), ("$stamp", stamp));
                return 0;
            });
            return database.Read(DatabaseFile.Catalog, c => Read(c, entryId));
        }
    }

    public JsonObject Overlay(JsonObject entry)
    {
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, c => Overlay(c, entry));
    }

    public IReadOnlyList<JsonObject> OverlayMany(IReadOnlyList<JsonObject> entries)
    {
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, c =>
        {
            // Keep the released batch selection contract: requested row IDs select overlays; Overlay resolves canonical_id.
            var ids = new JsonArray(entries.Select(row => (JsonNode?)row["id"]?.DeepClone()).ToArray()).ToJsonString();
            var owned = c.Query("SELECT entry_id FROM display_metadata WHERE entry_id IN(SELECT value FROM json_each($ids))", ("$ids", ids)).Select(row => row.Integer("entry_id")).ToHashSet();
            return entries.Select(row => owned.Contains(row["id"]!.GetValue<long>()) ? Overlay(c, row) : row.DeepClone().AsObject()).ToArray();
        });
    }

    /// <returns>Null for inherited artwork, zero for explicitly unavailable artwork, otherwise the exact attached native manga ID.</returns>
    public long? ArtworkSource(long entryId)
    {
        lock (sourceWriteGate) return database.Read<long?>(DatabaseFile.Catalog, c =>
        {
            var json = c.Scalar("SELECT fields_json FROM display_metadata WHERE entry_id=$entry", ("$entry", entryId)) as string;
            var choice = json is null ? null : Parse(json)["artwork"] as JsonObject;
            if (choice is null) return null;
            foreach (var table in new[] { "source_matches", "source_variants" })
                foreach (var row in c.Query($"SELECT source_id,source_manga_id,thumbnail_url FROM {table} WHERE entry_id=$entry AND active=1", ("$entry", entryId)))
                    if (Text(choice["mode"]) == "source" && row.Text("source_id") == Text(choice["source_id"]) && Integer(choice["source_manga_id"], out var mid) && row.Integer("source_manga_id") == mid
                        || Text(choice["mode"]) == "locked" && Text(choice["value"]) is { Length: > 0 } value && row.Text("thumbnail_url") == value)
                        return row.Integer("source_manga_id");
            return 0;
        });
    }

    /// <summary>Called inside the real catalogue merge transaction. Surviving fields win; every prior ownership record remains retained.</summary>
    public static void Reparent(DatabaseSession c, long target, long duplicate, string timestamp)
    {
        var row = c.Query("SELECT * FROM display_metadata WHERE entry_id=$entry", ("$entry", duplicate)).SingleOrDefault();
        c.Execute("UPDATE display_metadata_history SET entry_id=$target WHERE entry_id=$duplicate", ("$target", target), ("$duplicate", duplicate));
        if (row is null) return;
        c.Execute("INSERT INTO display_metadata_history(entry_id,old_entry_id,fields_json,reason,created_at) VALUES($target,$duplicate,$fields,$reason,$stamp)",
            ("$target", target), ("$duplicate", duplicate), ("$fields", row.Text("fields_json")), ("$reason", "absorbed ownership; surviving fields win conflicts"), ("$stamp", timestamp));
        var current = c.Query("SELECT * FROM display_metadata WHERE entry_id=$entry", ("$entry", target)).SingleOrDefault();
        var fields = Parse(row.Text("fields_json"));
        if (current is not null) foreach (var pair in Parse(current.Text("fields_json"))) fields[pair.Key] = pair.Value?.DeepClone();
        c.Execute("INSERT OR REPLACE INTO display_metadata VALUES($target,$revision,$fields,$stamp)",
            ("$target", target), ("$revision", checked(Math.Max(row.Integer("revision"), current?.Integer("revision") ?? 0) + 1)), ("$fields", DefaultJson(fields)), ("$stamp", timestamp));
        c.Execute("DELETE FROM display_metadata WHERE entry_id=$entry", ("$entry", duplicate));
    }

    /// <summary>
    /// Phase 9 per-field overrides (MetadataChooser) that only the locked mode supports and that 0.4.5 ignores on overlay:
    /// genres, status and year. They live in the same fields_json so a merge reparents and history retains them.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LockedOnlyFields = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["genres"] = "genre", ["status"] = "status", ["year"] = "year" };

    /// <summary>The saved ownership fields of an entry (empty when none), inside the caller's session.</summary>
    public static JsonObject OwnedFields(DatabaseSession c, long entryId)
    {
        ArgumentNullException.ThrowIfNull(c);
        var json = c.Scalar("SELECT fields_json FROM display_metadata WHERE entry_id=$entry", ("$entry", entryId)) as string;
        return json is null ? new JsonObject() : Parse(json);
    }

    /// <summary>
    /// Sets (or with a null <paramref name="choice"/> removes) one ownership field inside the caller's write, retaining the
    /// previous fields as a history snapshot and bumping the revision exactly like <see cref="Save"/>. Returns the previous choice.
    /// </summary>
    public static JsonObject? SetOwnedField(DatabaseSession c, long entryId, string name, JsonObject? choice, string reason, string timestamp)
    {
        ArgumentNullException.ThrowIfNull(c); ArgumentException.ThrowIfNullOrEmpty(name);
        var row = c.Query("SELECT revision,fields_json FROM display_metadata WHERE entry_id=$entry", ("$entry", entryId)).SingleOrDefault();
        var fields = row is null ? new JsonObject() : Parse(row.Text("fields_json"));
        var before = fields[name]?.DeepClone() as JsonObject;
        c.Execute("INSERT INTO display_metadata_history(entry_id,old_entry_id,fields_json,reason,created_at) VALUES($id,$id,$fields,$reason,$stamp)",
            ("$id", entryId), ("$fields", DefaultJson(fields)), ("$reason", reason), ("$stamp", timestamp));
        if (choice is null) fields.Remove(name); else fields[name] = choice.DeepClone();
        c.Execute("INSERT OR REPLACE INTO display_metadata VALUES($id,$revision,$fields,$stamp)",
            ("$id", entryId), ("$revision", checked((row?.Integer("revision") ?? 0) + 1)), ("$fields", DefaultJson(fields)), ("$stamp", timestamp));
        return before;
    }

    private static JsonObject Read(DatabaseSession c, long entryId)
    {
        if (c.Scalar("SELECT 1 FROM entries WHERE id=$entry", ("$entry", entryId)) is null) throw new KeyNotFoundException(entryId.ToString(CultureInfo.InvariantCulture));
        var row = c.Query("SELECT * FROM display_metadata WHERE entry_id=$entry", ("$entry", entryId)).SingleOrDefault();
        var history = c.Query("SELECT * FROM display_metadata_history WHERE entry_id=$entry ORDER BY id DESC LIMIT 50", ("$entry", entryId)).Select(r =>
        { var item = JsonSerializer.SerializeToNode(r)!.AsObject(); item["fields"] = Parse(r.Text("fields_json")); return (JsonNode)item; });
        return new JsonObject { ["entry_id"] = entryId, ["revision"] = row?.Integer("revision") ?? 0, ["fields"] = row is null ? new JsonObject() : Parse(row.Text("fields_json")), ["history"] = new JsonArray(history.ToArray()) };
    }

    private static JsonObject Overlay(DatabaseSession c, JsonObject entry)
    {
        var result = entry.DeepClone().AsObject(); var eid = entry["canonical_id"]?.GetValue<long>() is { } canonical && canonical != 0 ? canonical : entry["id"]!.GetValue<long>();
        var row = c.Query("SELECT revision,fields_json FROM display_metadata WHERE entry_id=$entry", ("$entry", eid)).SingleOrDefault();
        if (row is null) return result;
        var provenance = new JsonObject();
        foreach (var (name, node) in Parse(row.Text("fields_json")))
        {
            if (LockedOnlyFields.TryGetValue(name, out var lockedColumn))
            {
                if (node is JsonObject lockedChoice && Text(lockedChoice["mode"]) == "locked")
                { result[lockedColumn] = lockedChoice["value"]?.DeepClone(); provenance[name] = new JsonObject { ["mode"] = "locked" }; }
                continue;
            }
            if (!Fields.TryGetValue(name, out var column)) continue;
            if (node is not JsonObject choice) throw new InvalidDataException("Invalid saved display ownership");
            if (Text(choice["mode"]) == "locked")
            {
                result[column] = choice["value"]?.DeepClone(); provenance[name] = new JsonObject { ["mode"] = "locked" };
            }
            else
            {
                if (Text(choice["source_id"]) is not { } sid || !Integer(choice["source_manga_id"], out var mid)) throw new InvalidDataException("Invalid saved source ownership");
                var source = Record(c, eid, sid, mid); var available = source is not null && source.Text(column).Length != 0;
                if (available) result[column] = source!.Text(column);
                var detail = choice.DeepClone().AsObject(); detail["available"] = available; provenance[name] = detail;
            }
        }
        result["display_ownership"] = new JsonObject { ["revision"] = row.Integer("revision"), ["fields"] = provenance }; return result;
    }

    private static DatabaseRow? Record(DatabaseSession c, long entryId, string sourceId, long mangaId)
    {
        foreach (var table in new[] { "source_matches", "source_variants" })
        {
            var row = c.Query($"SELECT description,thumbnail_url,author,artist FROM {table} WHERE entry_id=$entry AND source_id=$source AND source_manga_id=$manga AND active=1",
                ("$entry", entryId), ("$source", sourceId), ("$manga", mangaId)).FirstOrDefault();
            if (row is not null) return row;
        }
        return null;
    }
    private static JsonObject Parse(string json) => JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Invalid saved display metadata");
    private static string? Text(JsonNode? node) => node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    private static bool Integer(JsonNode? node, out long result)
    {
        result = 0;
        return node is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.Number
            && scalar.ToJsonString().IndexOfAny(['.', 'e', 'E']) < 0 && scalar.TryGetValue(out result);
    }
    private static string DefaultJson(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(", ", obj.Select(pair => CanonicalJson.String(pair.Key) + ": " + DefaultJson(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(", ", array.Select(DefaultJson)) + "]",
        JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String => CanonicalJson.String(scalar.GetValue<string>()),
        _ => node.ToJsonString()
    };
    private string Timestamp()
    {
        var value = _clock.GetUtcNow();
        return value.ToString(value.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd'T'HH:mm:sszzz" : "yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
    }
}
