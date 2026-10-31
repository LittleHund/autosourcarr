using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;

namespace AutoSourcarr.Core.Persistence;

/// <summary>Retained app settings and profile preferences; independent of startup env files.</summary>
public sealed class SettingsRepository(SqliteDatabase database)
{
    private static readonly JsonSerializerOptions PreferenceJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public JsonNode? Get(string key, JsonNode? fallback = null) => database.Read(DatabaseFile.Catalog, session =>
    {
        var raw = session.Scalar("SELECT value FROM settings WHERE key=$key", ("$key", key)) as string;
        if (raw is null) return fallback?.DeepClone();
        try { return JsonNode.Parse(raw); }
        catch (JsonException) { return JsonValue.Create(raw); }
    });

    public void Set(string key, JsonNode? value)
    {
        var raw = value?.ToJsonString() ?? "null";
        database.Write(DatabaseFile.Catalog, session => session.Execute("""
            INSERT INTO settings(key,value) VALUES($key,$value)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """, ("$key", key), ("$value", raw)));
    }

    public JsonObject UserPreferences(string? userKey) => database.Read(DatabaseFile.Catalog, session =>
    {
        var raw = session.Scalar("SELECT values_json FROM user_ui_preferences WHERE user_key=$key", ("$key", UserKey(userKey))) as string;
        return ObjectOrEmpty(raw);
    });

    public JsonObject SetUserPreferences(string? userKey, JsonObject values, bool merge = true) => database.Write(DatabaseFile.Catalog, session =>
    {
        var key = UserKey(userKey);
        var current = merge ? ObjectOrEmpty(session.Scalar("SELECT values_json FROM user_ui_preferences WHERE user_key=$key", ("$key", key)) as string) : new JsonObject();
        foreach (var (name, value) in values) current[name] = value?.DeepClone();
        var raw = current.ToJsonString(PreferenceJson);
        if (Encoding.UTF8.GetByteCount(raw) > 65536) throw new ArgumentException("UI preferences exceed 64 KiB", nameof(values));
        session.Execute("""
            INSERT INTO user_ui_preferences(user_key,values_json,updated_at) VALUES($key,$value,$at)
            ON CONFLICT(user_key) DO UPDATE SET values_json=excluded.values_json,updated_at=excluded.updated_at
            """, ("$key", key), ("$value", raw), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        return current;
    });

    private static string UserKey(string? userKey)
    {
        var key = string.IsNullOrEmpty(userKey) ? "local:default" : userKey;
        return string.Concat(key.EnumerateRunes().Take(512).Select(rune => rune.ToString()));
    }
    private static JsonObject ObjectOrEmpty(string? raw)
    {
        try { return JsonNode.Parse(raw ?? "{}") as JsonObject ?? new(); }
        catch (JsonException) { return new(); }
    }
}
