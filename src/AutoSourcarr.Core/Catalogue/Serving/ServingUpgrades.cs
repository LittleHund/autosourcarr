using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Core.Catalogue.Serving;

/// <summary>A serving-upgrade request the current state does not allow (stale revision, invalid policy, blockers).</summary>
public sealed class ServingUpgradeException(string message) : Exception(message);

/// <summary>
/// CHAP-6 (0.4.5 profile_upgrade.py): opt-in quality upgrades per work, like a Sonarr quality profile. A reviewed
/// candidate may replace the serving one only when upgrades are enabled, no manually locked record controls the work,
/// both have a recent complete quality sample, the current one is below the cutoff, the target reaches the cutoff,
/// the gain is at least the minimum improvement and the per-chapter cooldown has passed. Applying records the choice
/// (serving_choices), its history and a decision-log event; downloads, reading progress and origin history are kept.
/// </summary>
public sealed class ServingUpgrades(SqliteDatabase database, object sourceWriteGate, Func<long, JsonObject> chapterDetails, IDecisionLog decisions,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public static JsonObject DefaultPolicy => new() { ["enabled"] = false, ["cutoff"] = 90, ["minimum_improvement"] = 10, ["cooldown_hours"] = 24, ["sample_max_age_hours"] = 168 };
    private static readonly (string Name, double Minimum, double Maximum)[] Limits =
        [("cutoff", 1, 100), ("minimum_improvement", 1, 100), ("cooldown_hours", 1, 8760), ("sample_max_age_hours", 1, 8760)];

    /// <summary>Over the reader's chapter details (<see cref="ReaderRepository.ChapterDetails"/>).</summary>
    public ServingUpgrades(SqliteDatabase database, object sourceWriteGate, ReaderRepository reader, IDecisionLog decisions, TimeProvider? clock = null)
        : this(database, sourceWriteGate, (reader ?? throw new ArgumentNullException(nameof(reader))).ChapterDetails, decisions, clock) { }

    public JsonObject Read(long entryId) => database.Read(DatabaseFile.Catalog, session => Read(session, entryId));

    public static JsonObject Validate(JsonNode? policy)
    {
        if (policy is not JsonObject value || !value.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(DefaultPolicy.Select(pair => pair.Key))
            || value["enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out _)) throw new ServingUpgradeException("Invalid upgrade policy");
        foreach (var (name, minimum, maximum) in Limits)
        {
            var number = Number(value, name);
            if (value[name] is not JsonValue item || item.GetValueKind() != JsonValueKind.Number || !double.IsFinite(number) || number < minimum || number > maximum)
                throw new ServingUpgradeException("Invalid upgrade threshold");
        }
        return (JsonObject)value.DeepClone();
    }

    public JsonObject Save(long entryId, JsonNode? policy, long revision, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var validated = Validate(policy);
        lock (sourceWriteGate)
            return database.Write(DatabaseFile.Catalog, session =>
            {
                var current = Read(session, entryId);
                if (ReaderValue.Integer(current, "revision") != revision) throw new ServingUpgradeException("Upgrade policy changed; reload");
                var stamp = Stamp();
                session.Execute("INSERT INTO serving_policy_history(entry_id,old_entry_id,policy_json,reason,created_at) VALUES($id,$id,$policy,'before policy edit',$time)",
                    ("$id", entryId), ("$policy", current["policy"]!.ToJsonString()), ("$time", stamp));
                session.Execute("INSERT OR REPLACE INTO serving_upgrade_policies VALUES($id,$revision,$policy,$time)",
                    ("$id", entryId), ("$revision", checked(revision + 1)), ("$policy", validated.ToJsonString()), ("$time", stamp));
                decisions.Record(new DecisionEvent("serving.upgrade.policy", actor, [entryId], [], "Quality upgrade policy saved",
                    (JsonObject)current["policy"]!.DeepClone(), (JsonObject)validated.DeepClone()));
                return Read(session, entryId);
            });
    }

    public JsonObject Preview(long entryId, long chapterId, long candidateId)
    {
        var policy = Read(entryId); var rules = policy["policy"]!.AsObject();
        var detail = chapterDetails(chapterId);
        if (ReaderValue.Integer(detail["chapter"]!.AsObject(), "entry_id") != entryId) throw new ServingUpgradeException("Chapter is not owned by this work");
        var candidates = detail["candidates"]!.AsArray().OfType<JsonObject>().ToDictionary(row => ReaderValue.Integer(row, "id"));
        if (!candidates.TryGetValue(candidateId, out var target)) throw new ServingUpgradeException("Candidate is no longer admitted by the resolver");
        var (choice, manual, attempt) = database.Read(DatabaseFile.Catalog, session => (
            session.Query("SELECT * FROM serving_choices WHERE virtual_chapter_id=$id", ("$id", chapterId)).FirstOrDefault(),
            Locked(session, entryId),
            session.Query("SELECT * FROM quality_attempts WHERE candidate_id=$id", ("$id", candidateId)).FirstOrDefault()));
        var served = detail["served"] as JsonObject;
        long? currentId = choice is not null ? choice.Integer("candidate_id")
            : served is not null && ReaderValue.Integer(served, "candidate_id") != 0 ? ReaderValue.Integer(served, "candidate_id")
            : detail["would_serve_candidate_id"] is JsonValue would && would.TryGetValue<long>(out var wouldId) ? wouldId : null;
        var current = currentId.HasValue ? candidates.GetValueOrDefault(currentId.Value) : null;
        var (sample, baseline) = database.Read(DatabaseFile.Origins, session => (
            session.Query("SELECT * FROM quality_samples WHERE candidate_id=$id", ("$id", candidateId)).FirstOrDefault(),
            currentId.HasValue ? session.Query("SELECT * FROM quality_samples WHERE candidate_id=$id", ("$id", currentId.Value)).FirstOrDefault() : null));
        var maximumAge = Number(rules, "sample_max_age_hours") * 3600;
        var blockers = new List<string>();
        if (!(rules["enabled"]?.GetValue<bool>() ?? false)) blockers.Add("Reviewed upgrades are disabled for this work");
        if (manual) blockers.Add("A manually locked source record controls this work");
        if (current is null) blockers.Add("Previous serving candidate is no longer an admitted baseline");
        if (currentId == candidateId) blockers.Add("Candidate is already selected");
        if (sample is null || Age(sample.Text("analyzed_at")) > maximumAge || attempt is not null && attempt.Text("outcome") != "complete") blockers.Add("Target needs a recent complete quality sample");
        if (baseline is null || Age(baseline.Text("analyzed_at")) > maximumAge) blockers.Add("Current candidate needs a recent complete quality sample");
        var before = current is null ? 0 : ReaderValue.Number(current, "quality_score");
        var after = ReaderValue.Number(target, "quality_score");
        blockers.AddRange(Thresholds(rules, before, after));
        if (choice is not null && Age(choice.Text("changed_at")) < Number(rules, "cooldown_hours") * 3600) blockers.Add("Serving choice cooldown is active");
        var output = new JsonObject
        {
            ["entry_id"] = entryId, ["chapter_id"] = chapterId, ["candidate_id"] = candidateId,
            ["current_candidate_id"] = currentId.HasValue ? JsonValue.Create(currentId.Value) : null,
            ["quality_before"] = before, ["quality_after"] = after,
            ["blockers"] = new JsonArray(blockers.Select(item => (JsonNode)JsonValue.Create(item)).ToArray()), ["eligible"] = blockers.Count == 0,
            ["policy"] = policy, ["choice"] = choice is null ? null : Json(choice),
            ["candidate_revision"] = Hash(detail),
            ["sample_revision"] = Hash(new JsonArray(sample is null ? null : Json(sample), baseline is null ? null : Json(baseline), attempt is null ? null : Json(attempt))),
            ["effect"] = "Select this candidate for future serving; retain downloaded files, reading progress and origin history.",
        };
        output["revision"] = Hash(output);
        return output;
    }

    /// <summary>The minimum-gain and cutoff rules alone (Sonarr-style: upgrade until the cutoff, only by at least the minimum).</summary>
    public static IReadOnlyList<string> Thresholds(JsonObject rules, double before, double after)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var blockers = new List<string>();
        var cutoff = Number(rules, "cutoff");
        if (before >= cutoff) blockers.Add("Current candidate already meets the cutoff");
        if (after < cutoff) blockers.Add("Target is below the cutoff");
        if (after - before < Number(rules, "minimum_improvement")) blockers.Add("Improvement is below the configured minimum");
        return blockers;
    }

    public JsonObject Apply(long entryId, long chapterId, long candidateId, string revision, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (sourceWriteGate)
        {
            var result = Preview(entryId, chapterId, candidateId);
            if (result["revision"]!.GetValue<string>() != revision) throw new ServingUpgradeException("Candidate, sample or policy changed; preview again");
            if (!result["eligible"]!.GetValue<bool>())
                throw new ServingUpgradeException(string.Join("; ", result["blockers"]!.AsArray().Select(item => item!.GetValue<string>())));
            var old = result["choice"] as JsonObject; var stamp = Stamp();
            var previous = result["current_candidate_id"] is JsonValue value && value.TryGetValue<long>(out var id) ? id : 0;
            database.Write(DatabaseFile.Catalog, session =>
            {
                session.Execute("INSERT OR REPLACE INTO serving_choices VALUES($chapter,$entry,$candidate,$revision,$time)",
                    ("$chapter", chapterId), ("$entry", entryId), ("$candidate", candidateId), ("$revision", (old is null ? 0 : ReaderValue.Integer(old, "revision")) + 1), ("$time", stamp));
                session.Execute("INSERT INTO serving_upgrade_history(entry_id,virtual_chapter_id,old_candidate_id,candidate_id,changed_at) VALUES($entry,$chapter,$old,$candidate,$time)",
                    ("$entry", entryId), ("$chapter", chapterId), ("$old", previous), ("$candidate", candidateId), ("$time", stamp));
                decisions.Record(new DecisionEvent("serving.upgrade", actor, [entryId], [], "Quality upgrade applied to chapter " + chapterId.ToString(CultureInfo.InvariantCulture),
                    new JsonObject { ["chapter_id"] = chapterId, ["candidate_id"] = previous, ["quality"] = result["quality_before"]!.DeepClone() },
                    new JsonObject { ["chapter_id"] = chapterId, ["candidate_id"] = candidateId, ["quality"] = result["quality_after"]!.DeepClone() }));
                return 0;
            });
            return new JsonObject { ["applied"] = true, ["effect"] = result["effect"]!.DeepClone() };
        }
    }

    private static JsonObject Read(DatabaseSession session, long entryId)
    {
        if (session.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", entryId)) is null) throw new KeyNotFoundException("Entry not found");
        var row = session.Query("SELECT * FROM serving_upgrade_policies WHERE entry_id=$id", ("$id", entryId)).FirstOrDefault();
        var retained = new JsonArray(session.Query("SELECT * FROM serving_policy_history WHERE entry_id=$id ORDER BY id DESC LIMIT 50", ("$id", entryId))
            .Select(item => { var value = Json(item); value["policy"] = JsonNode.Parse(item.Text("policy_json")); return (JsonNode)value; }).ToArray());
        return new JsonObject
        {
            ["entry_id"] = entryId, ["revision"] = row?.Integer("revision") ?? 0,
            ["policy"] = row is null ? DefaultPolicy : JsonNode.Parse(row.Text("policy_json")), ["retained"] = retained,
        };
    }

    private static bool Locked(DatabaseSession session, long entryId) => session.Scalar("""
        SELECT 1 FROM source_matches WHERE entry_id=$id AND active=1 AND locked=1
        UNION ALL SELECT 1 FROM source_variants WHERE entry_id=$id AND active=1 AND locked=1 LIMIT 1
        """, ("$id", entryId)) is not null;

    private double Age(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp)
        ? Math.Max(0, (_clock.GetUtcNow() - stamp).TotalSeconds) : double.PositiveInfinity;

    private static double Number(JsonObject rules, string name) => rules[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
        && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
    private string Stamp() => _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
    private static string Hash(JsonNode value) => Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Bytes(value)));
    private static JsonObject Json(DatabaseRow row) => JsonSerializer.SerializeToNode(row.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))!.AsObject();
}
