using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Reader;

/// <summary>Use the same sourceWriteGate as every catalogue membership/merge/split writer.</summary>
public sealed class ServingProfileRepository(SqliteDatabase database, object sourceWriteGate, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public JsonObject Read(long entryId, bool includeRetained = true) => database.Read(DatabaseFile.Catalog, session => Read(session, entryId, includeRetained));

    public JsonObject Save(long entryId, JsonNode? profile, long revision)
    {
        var validated = ServingProfile.Parse(profile);
        lock (sourceWriteGate)
            return database.Write(DatabaseFile.Catalog, session =>
            {
                var current = Read(session, entryId, true);
                if (ReaderValue.Integer(current, "revision") != revision) throw new ArgumentException("Profile changed; reload before saving");
                session.Execute("""
                    INSERT INTO serving_profiles(entry_id,profile_json,revision,updated_at) VALUES($id,$profile,$revision,$time)
                    ON CONFLICT(entry_id) DO UPDATE SET profile_json=excluded.profile_json,revision=excluded.revision,updated_at=excluded.updated_at
                    """, ("$id", entryId), ("$profile", validated.ToJson().ToJsonString()), ("$revision", checked(revision + 1)), ("$time", Timestamp()));
                return Read(session, entryId, true);
            });
    }

    public (ServingProfile Profile, IReadOnlyDictionary<string, string> Languages) Context(long entryId) => database.Read(DatabaseFile.Catalog, session =>
    {
        if (session.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", entryId)) is null)
            return (ServingProfile.Default, (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal));
        var row = session.Query("SELECT profile_json FROM serving_profiles WHERE entry_id=$id", ("$id", entryId)).FirstOrDefault();
        var profile = row is null ? ServingProfile.Default : ServingProfile.Parse(JsonNode.Parse(row.Text("profile_json")));
        IReadOnlyDictionary<string, string> languages = profile.IsDefault ? new Dictionary<string, string>(StringComparer.Ordinal)
            : session.Query("SELECT source_id,lang FROM source_config").ToDictionary(item => item.Text("source_id"), item => item.Text("lang"), StringComparer.Ordinal);
        return (profile, languages);
    });

    private static JsonObject Read(DatabaseSession session, long entryId, bool includeRetained)
    {
        if (session.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", entryId)) is null) throw new KeyNotFoundException("Entry not found");
        var row = session.Query("SELECT * FROM serving_profiles WHERE entry_id=$id", ("$id", entryId)).FirstOrDefault();
        var retained = new JsonArray();
        if (includeRetained)
            foreach (var old in session.Query("SELECT * FROM entry_preference_history WHERE canonical_entry_id=$id ORDER BY id DESC", ("$id", entryId)))
            {
                var tracking = old.Text("tracking_json");
                retained.Add(new JsonObject { ["id"] = old.Integer("id"), ["title"] = old.Text("title"),
                    ["profile"] = old.Text("profile_json") == "{}" ? null : JsonNode.Parse(old.Text("profile_json")),
                    ["monitored"] = tracking == "{}" ? null : JsonNode.Parse(tracking)?["monitored"]?.DeepClone() });
            }
        return new() { ["entry_id"] = entryId, ["profile"] = row is null ? ServingProfile.Default.ToJson() : JsonNode.Parse(row.Text("profile_json")),
            ["revision"] = row?.Integer("revision") ?? 0, ["retained"] = retained };
    }

    private string Timestamp() => _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
}
