using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Repair;

public sealed class RepairHistoryConflictException(string message) : InvalidOperationException(message);

/// <summary>Private code/binary history; install plans never imply user-confirmed working code.</summary>
public sealed class RepairHistory(IPrivateDocumentStore files, Func<double>? clock = null)
{
    public const int MaximumHistoryBytes = 512 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ReferenceKeys = ["binary_sha256", "raw_id", "version", "entrypoint", "extension_lib", "source_ids", "identity_sha256", "job_id"];
    private static readonly HashSet<string> Statuses = ["unknown", "unconfirmed", "confirmed", "reported-not-working"];
    private static readonly HashSet<string> JobStates = ["preparing", "installed", "confirmed", "reported-not-working", "reverted", "failed", "blocked", "cancelled", "interrupted", "discarded"];
    private static readonly HashSet<string> JobFields = ["state", "epoch", "baseline_sha256", "created_at", "finished_at", "feedback"];
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, JsonObject> _cache = new(StringComparer.Ordinal);
    private readonly Func<double> _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);

    public JsonObject Snapshot(string package) => files.Locked(Name(package), document => { var state = Read(document, package); Cache(state); return Clone(state); });
    public JsonObject Cached() { lock (_cacheGate) return new(_cache.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value.DeepClone()))); }
    public void Load()
    {
        foreach (var name in files.JsonDocuments(4096))
            files.Locked(name, document =>
            {
                var value = Decode(document.Read(MaximumHistoryBytes)) ?? throw Conflict("Repair-history file vanished during load");
                var package = Text(value, "package");
                if (Name(package) != name) throw Conflict("Repair-history file/package changed");
                Validate(value, package); Cache(value); return 0;
            });
    }
    public JsonObject Summaries(bool includeSourceIds = false)
    {
        lock (_cacheGate)
        {
            var result = new JsonObject();
            foreach (var (package, state) in _cache)
            {
                JsonObject? current = null;
                if (state["current"] is JsonObject installed)
                {
                    current = new();
                    foreach (var key in new[] { "job_id", "binary_sha256", "raw_id", "version", "status", "installed_at", "confirmed_at" }.Concat(includeSourceIds ? ["source_ids"] : Array.Empty<string>())) current[key] = installed[key]?.DeepClone();
                }
                var jobs = new JsonObject(); foreach (var (id, row) in state["jobs"]!.AsObject()) jobs[id] = row!["state"]!.DeepClone();
                result[package] = new JsonObject { ["revision"] = state["revision"]!.DeepClone(), ["pending"] = state["pending"]?.DeepClone(), ["current"] = current,
                    ["working_at"] = (state["working"] as JsonObject)?["confirmed_at"]?.DeepClone(), ["can_revert"] = state["revert"] is not null && state["pending"] is null, ["jobs"] = jobs };
            }
            return result;
        }
    }

    public JsonObject Admit(string package, string job, JsonObject? baseline)
    {
        JobId(job); var reference = Reference(baseline);
        return files.Locked(Name(package), document =>
        {
            var state = Read(document, package); var jobs = state["jobs"]!.AsObject();
            if (jobs[job] is JsonObject existing)
            {
                if (TextOrNull(existing["baseline_sha256"]) != Binary(reference)) throw Conflict("Repair request identity was reused for different input");
                return Clone(state);
            }
            if (state["pending"] is not null) throw Conflict("This source already has an owned repair; wait or cancel it first");
            if (Binary(state["current"]) != Binary(reference)) { state["current"] = WithStatus(reference, "unknown"); state["revert"] = null; }
            if (Text(state["current"] as JsonObject, "status") == "confirmed") state["revert"] = null;
            state["epoch"] = Integer(state, "epoch") + 1; state["revision"] = Integer(state, "revision") + 1; state["pending"] = job;
            jobs[job] = new JsonObject { ["state"] = "preparing", ["epoch"] = state["epoch"]!.DeepClone(), ["baseline_sha256"] = JsonValue.Create(Binary(reference)), ["created_at"] = _clock(), ["feedback"] = "" };
            var keep = new HashSet<string?>([job, Text(state["current"] as JsonObject, "job_id"), Text(state["working"] as JsonObject, "job_id"), Text(state["revert"] as JsonObject, "job_id")], StringComparer.Ordinal);
            foreach (var key in jobs.Select(pair => pair.Key).ToArray()) { if (jobs.Count <= 64) break; if (!keep.Contains(key)) jobs.Remove(key); }
            return Save(document, state);
        });
    }
    public JsonObject Observe(string package, JsonObject? reference)
    {
        var observed = Reference(reference);
        return files.Locked(Name(package), document =>
        {
            var state = Read(document, package);
            if (Binary(state["current"]) == Binary(observed)) return state;
            state["current"] = WithStatus(observed, "unknown"); state["revert"] = null; state["revision"] = Integer(state, "revision") + 1;
            return Save(document, state);
        });
    }
    public JsonObject PlanInstall(string package, string job, JsonObject candidate, long expectedRevision)
    {
        JobId(job); var reference = Reference(candidate, candidate: true) ?? throw Conflict("Candidate does not belong to this repair");
        if (reference["job_id"] is not null && Text(reference, "job_id") != job) throw Conflict("Candidate does not belong to this repair");
        return files.Locked(Name(package), document =>
        {
            var state = Read(document, package); Revision(state, expectedRevision);
            if (Text(state, "pending") != job) throw Conflict("Repair admission is no longer current");
            var current = state["current"] as JsonObject; var anchor = current?.DeepClone();
            foreach (var key in new[] { "entrypoint", "extension_lib", "source_ids", "identity_sha256" })
                if (current is not null && Truth(current[key]) && !JsonNode.DeepEquals(current[key], reference[key])) throw Conflict("Source loading or source identities changed; this is not a compatible repair");
            if (current is not null && state["revert"] is JsonObject revert && Text(revert, "job_id") == Text(current, "job_id")) anchor = revert["target"]?.DeepClone();
            else if (current is not null && state["working"] is JsonObject working && (Binary(current) == Binary(working) || Compatible(working, reference))) anchor = working.DeepClone();
            var after = Clone(state); var installed = Clone(reference); installed["job_id"] = job; installed["status"] = "unconfirmed"; installed["installed_at"] = _clock();
            after["current"] = installed; after["pending"] = null; after["last_recovery"] = null;
            after["revert"] = new JsonObject { ["job_id"] = job, ["target"] = anchor, ["working_before"] = state["working"]?.DeepClone(), ["epoch"] = state["epoch"]!.DeepClone() };
            after["jobs"]![job]!["state"] = "installed"; after["jobs"]![job]!["finished_at"] = _clock();
            return Plan(state, after, "install", job);
        });
    }
    public JsonObject BindBaselineCode(string package, string job, string rawId)
    {
        JobId(job); Hash(rawId);
        return files.Locked(Name(package), document =>
        {
            var state = Read(document, package);
            if (Text(state, "pending") != job) throw Conflict("Repair admission is no longer current");
            if (state["current"] is JsonObject current && current["raw_id"] is null)
            { current["raw_id"] = rawId; state["revision"] = Integer(state, "revision") + 1; return Save(document, state); }
            return state;
        });
    }
    public JsonObject Commit(JsonObject plan, string? observedSha256)
    {
        CheckedPlan(plan); Hash(observedSha256, optional: true);
        if (observedSha256 != Binary(plan["target"])) throw Conflict("Installed binary does not match the source promotion plan");
        var package = Text(plan, "package");
        return files.Locked(Name(package), document =>
        {
            var state = Read(document, package);
            if (Text(state, "last_plan") == Text(plan, "id") && Binary(state["current"]) == observedSha256) return Clone(state);
            if (!JsonNode.DeepEquals(state, plan["before"])) throw Conflict("Source history changed before promotion completed");
            var after = Clone(plan["after"]!.AsObject()); after["last_plan"] = plan["id"]!.DeepClone(); return Save(document, after);
        });
    }
    public JsonObject Confirm(string package, string job, long expectedRevision, string? observedSha256) => files.Locked(Name(package), document =>
    {
        var state = Read(document, package); Revision(state, expectedRevision); Active(state, job, observedSha256);
        var current = state["current"]!.AsObject();
        if (current["raw_id"] is null) throw Conflict("Working source code must be retained before marking Fixed");
        current["status"] = "confirmed"; current["confirmed_at"] = _clock(); state["working"] = current.DeepClone(); state["jobs"]![job]!["state"] = "confirmed";
        state["revision"] = Integer(state, "revision") + 1; return Save(document, state);
    });
    public JsonObject ReportFailed(string package, string job, long expectedRevision, string? observedSha256, string feedback = "")
    {
        if (feedback.EnumerateRunes().Count() > 6000) throw Conflict("Describe the remaining problem in at most6000 characters");
        return files.Locked(Name(package), document =>
        {
            var state = Read(document, package); Revision(state, expectedRevision); Active(state, job, observedSha256);
            state["current"]!["status"] = "reported-not-working"; state["jobs"]![job]!["state"] = "reported-not-working"; state["jobs"]![job]!["feedback"] = feedback;
            if (Binary(state["working"]) == observedSha256) state["working"] = (state["revert"] as JsonObject)?["working_before"]?.DeepClone();
            state["revision"] = Integer(state, "revision") + 1; return Save(document, state);
        });
    }
    public JsonObject PlanRevert(string package, string job, long expectedRevision, string? observedSha256) => files.Locked(Name(package), document =>
    {
        var state = Read(document, package); Revision(state, expectedRevision); Active(state, job, observedSha256);
        if (state["revert"] is not JsonObject receipt || Text(receipt, "job_id") != job) throw Conflict("A later fix closed this version’s revert window");
        var after = Clone(state); after["current"] = receipt["target"]?.DeepClone(); after["working"] = receipt["working_before"]?.DeepClone(); after["revert"] = null;
        after["jobs"]![job]!["state"] = "reverted"; after["jobs"]![job]!["finished_at"] = _clock(); return Plan(state, after, "revert", job);
    });
    public JsonObject Finish(string package, string job, string outcome)
    {
        JobId(job); if (outcome is not ("failed" or "blocked" or "cancelled" or "interrupted" or "discarded")) throw Conflict("Invalid terminal repair outcome");
        return files.Locked(Name(package), document =>
        {
            var state = Read(document, package);
            if (!state["jobs"]!.AsObject().ContainsKey(job)) throw Conflict("Repair history was not admitted here");
            if (Text(state, "pending") != job) return Clone(state);
            state["pending"] = null; state["jobs"]![job]!["state"] = outcome; state["jobs"]![job]!["finished_at"] = _clock(); state["revision"] = Integer(state, "revision") + 1;
            return Save(document, state);
        });
    }
    public JsonObject RestoreBefore(JsonObject plan, string? observedSha256)
    {
        CheckedPlan(plan);
        if (Text(plan, "action") != "install" || observedSha256 != Binary(plan["before"]!["current"])) throw Conflict("Interrupted install recovery does not match the restored binary");
        var package = Text(plan, "package");
        return files.Locked(Name(package), document =>
        {
            var state = Read(document, package);
            if (Text(state, "last_recovery") == Text(plan, "id")) return Clone(state);
            if (!JsonNode.DeepEquals(state, plan["before"]) && Text(state, "last_plan") != Text(plan, "id")) throw Conflict("A newer source history cannot be replaced by old recovery");
            var restored = Clone(plan["before"]!.AsObject()); restored["revision"] = Integer(state, "revision") + 1; restored["pending"] = null; restored["last_plan"] = null; restored["last_recovery"] = plan["id"]!.DeepClone();
            var job = Text(plan, "job_id"); restored["jobs"]![job]!["state"] = "interrupted"; restored["jobs"]![job]!["finished_at"] = _clock();
            return Save(document, restored);
        });
    }

    private static JsonObject Plan(JsonObject before, JsonObject after, string action, string job)
    {
        after["revision"] = Integer(before, "revision") + 1;
        var plan = new JsonObject { ["schema"] = 1, ["package"] = before["package"]!.DeepClone(), ["action"] = action, ["job_id"] = job,
            ["before"] = before.DeepClone(), ["after"] = after, ["target"] = after["current"]?.DeepClone() };
        plan["id"] = Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Bytes(plan))); return plan;
    }
    private static void CheckedPlan(JsonObject plan)
    {
        ExactKeys(plan, ["schema", "package", "action", "job_id", "before", "after", "target", "id"], "Invalid source promotion plan");
        var raw = Clone(plan); raw.Remove("id");
        if (Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Bytes(raw))) != Text(plan, "id")) throw Conflict("Source promotion plan changed");
        var package = Text(plan, "package"); _ = Name(package); JobId(Text(plan, "job_id"));
        if (Integer(plan, "schema") != 1 || Text(plan, "action") is not ("install" or "revert")) throw Conflict("Unsupported source promotion plan");
        if (plan["before"] is not JsonObject before || plan["after"] is not JsonObject after) throw Conflict("Invalid source promotion state");
        Validate(before, package); Validate(after, package);
        if (Integer(after, "revision") != Integer(before, "revision") + 1 || !JsonNode.DeepEquals(plan["target"], after["current"])) throw Conflict("Source promotion plan does not match its version target");
    }
    private JsonObject Read(IPrivateDocument document, string package)
    {
        JsonObject state;
        try { state = Decode(document.Read(MaximumHistoryBytes)) ?? Initial(package); }
        catch (InvalidDataException) { throw Conflict("Source repair history is damaged; it was not reset"); }
        Validate(state, package); return state;
    }
    private static JsonObject? Decode(byte[]? bytes)
    {
        if (bytes is null) return null;
        try { return JsonNode.Parse(StrictUtf8.GetString(bytes), documentOptions: new() { MaxDepth = 32 }) as JsonObject ?? throw Conflict("Source repair history has an unsupported schema"); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException) { throw Conflict("Source repair history is damaged; it was not reset"); }
    }
    private JsonObject Save(IPrivateDocument document, JsonObject state)
    {
        Validate(state, Text(state, "package")); var bytes = CanonicalJson.Bytes(state);
        if (bytes.Length > MaximumHistoryBytes) throw Conflict("Source history exceeds its persistence limit");
        document.Write(bytes); Cache(state); return Clone(state);
    }
    private void Cache(JsonObject state) { lock (_cacheGate) _cache[Text(state, "package")] = Clone(state); }
    private static JsonObject Initial(string package) => new() { ["schema"] = 1, ["package"] = package, ["revision"] = 0L, ["epoch"] = 0L, ["current"] = null,
        ["working"] = null, ["revert"] = null, ["pending"] = null, ["jobs"] = new JsonObject(), ["last_plan"] = null, ["last_recovery"] = null };
    private static void Validate(JsonObject state, string package)
    {
        ExactKeys(state, Initial(package).Select(pair => pair.Key), "Source repair history has an unsupported schema or package");
        if (Integer(state, "schema") != 1 || Text(state, "package") != package) throw Conflict("Source repair history has an unsupported schema or package");
        foreach (var key in new[] { "revision", "epoch" }) if (Integer(state, key) is < 0 or >= 9007199254740991L) throw Conflict("Invalid source-history revision");
        foreach (var key in new[] { "current", "working" }) ReferenceNode(state[key], internalValue: true);
        if (state["working"] is JsonObject working && Text(working, "status") != "confirmed") throw Conflict("Working history lacks a user confirmation");
        if (state["pending"] is not null) JobId(Text(state, "pending"));
        foreach (var key in new[] { "last_plan", "last_recovery" }) HashNode(state[key], optional: true);
        var epoch = Integer(state, "epoch");
        if (state["revert"] is not null)
        {
            if (state["revert"] is not JsonObject receipt) throw Conflict("Invalid source revert receipt");
            ExactKeys(receipt, ["job_id", "target", "working_before", "epoch"], "Invalid source revert receipt"); JobId(Text(receipt, "job_id"));
            ReferenceNode(receipt["target"], internalValue: true); ReferenceNode(receipt["working_before"], internalValue: true);
            if (Integer(receipt, "epoch") is var revertEpoch && (revertEpoch < 0 || revertEpoch > epoch)) throw Conflict("Invalid revert epoch");
        }
        if (state["jobs"] is not JsonObject jobs || jobs.Count > 64) throw Conflict("Repair history exceeds its bounded job index");
        foreach (var (id, value) in jobs)
        {
            JobId(id);
            if (value is not JsonObject row || row.Any(pair => !JobFields.Contains(pair.Key))) throw Conflict("Invalid repair history job");
            if (!JobStates.Contains(Text(row, "state"))) throw Conflict("Invalid repair history job state");
            if (Integer(row, "epoch") is var jobEpoch && (jobEpoch < 0 || jobEpoch > epoch)) throw Conflict("Invalid repair history job epoch");
            HashNode(row["baseline_sha256"], optional: true);
            if (row.ContainsKey("feedback") && (TextOrNull(row["feedback"]) is not { } feedback || feedback.EnumerateRunes().Count() > 6000)) throw Conflict("Repair feedback exceeds its limit");
            foreach (var key in new[] { "created_at", "finished_at" }) if (row.ContainsKey(key)) Stamp(row[key]);
        }
        if (state["pending"] is not null && !jobs.ContainsKey(Text(state, "pending"))) throw Conflict("Missing pending repair receipt");
    }
    private static JsonObject? ReferenceNode(JsonNode? value, bool internalValue = false, bool candidate = false)
    {
        if (value is null) return null;
        if (value is not JsonObject obj) throw Conflict("Unsupported source-version reference");
        return Reference(obj, internalValue, candidate);
    }
    private static JsonObject? Reference(JsonObject? value, bool internalValue = false, bool candidate = false)
    {
        if (value is null) return null;
        var allowed = new HashSet<string>(ReferenceKeys, StringComparer.Ordinal); if (internalValue) allowed.UnionWith(["status", "installed_at", "confirmed_at"]);
        if (value.Any(pair => !allowed.Contains(pair.Key))) throw Conflict("Unsupported source-version reference");
        var result = new JsonObject(); foreach (var key in ReferenceKeys) result[key] = value[key]?.DeepClone();
        HashNode(result["binary_sha256"]); HashNode(result["raw_id"], optional: !candidate);
        if (!Matches(result["version"], "[0-9][A-Za-z0-9.+_-]{0,99}")) throw Conflict("Source version metadata is missing");
        if (result["job_id"] is not null) JobId(Text(result, "job_id"));
        if (result["entrypoint"] is not null && !Matches(result["entrypoint"], "[A-Za-z_$][A-Za-z0-9_.$]{0,239}")) throw Conflict("Invalid source loading-class reference");
        if (result["extension_lib"] is not null && !Matches(result["extension_lib"], @"[0-9]{1,3}\.[0-9]{1,3}")) throw Conflict("Invalid source library reference");
        if (result["source_ids"] is not JsonArray ids || ids.Count > 200 || ids.Any(id => !Matches(id, "-?[0-9]{1,20}"))) throw Conflict("Invalid source identities in repair history");
        result["source_ids"] = new JsonArray(ids.Select(id => id!.GetValue<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        HashNode(result["identity_sha256"], optional: !candidate);
        if (candidate && (Text(result, "entrypoint").Length == 0 || Text(result, "extension_lib").Length == 0 || ids.Count == 0)) throw Conflict("Validated source loading identity is required before installation");
        if (internalValue)
        {
            if (!Statuses.Contains(Text(value, "status"))) throw Conflict("Invalid source confirmation state"); result["status"] = value["status"]!.DeepClone();
            foreach (var key in new[] { "installed_at", "confirmed_at" }) if (value.ContainsKey(key)) { Stamp(value[key]); result[key] = value[key]!.DeepClone(); }
        }
        return result;
    }
    private static void Revision(JsonObject state, long expected) { if (expected != Integer(state, "revision")) throw Conflict("Repair history changed; refresh before acting"); }
    private static void Active(JsonObject state, string job, string? observed)
    {
        JobId(job); Hash(observed, optional: true);
        if (state["pending"] is not null) throw Conflict("A new repair is active; wait or cancel it before changing the installed version");
        if (state["current"] is not JsonObject current || Text(current, "job_id") != job || Binary(current) != observed) throw Conflict("This is not the currently installed repair; refresh its version");
    }
    private static bool Compatible(JsonObject? left, JsonObject? right) => left is not null && right is not null && Text(left, "entrypoint").Length > 0 && Text(left, "extension_lib").Length > 0
        && Text(left, "identity_sha256").Length > 0 && new[] { "entrypoint", "extension_lib", "source_ids", "identity_sha256" }.All(key => JsonNode.DeepEquals(left[key], right[key]));
    private static JsonObject? WithStatus(JsonObject? value, string status) { if (value is null) return null; var result = Clone(value); result["status"] = status; return result; }
    private static string? Binary(JsonNode? value) => value is JsonObject obj ? TextOrNull(obj["binary_sha256"]) : null;
    private static void ExactKeys(JsonObject value, IEnumerable<string> keys, string message) { if (!value.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(keys)) throw Conflict(message); }
    private static void Stamp(JsonNode? value)
    {
        if (value?.GetValueKind() != JsonValueKind.Number || !double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < 0) throw Conflict("Invalid repair-history timestamp");
    }
    private static long Integer(JsonObject state, string key) => state[key]?.GetValueKind() == JsonValueKind.Number && long.TryParse(state[key]!.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : throw Conflict("Invalid source-history integer");
    private static string Name(string package)
    {
        if (package.Length > 200 || !Regex.IsMatch(package, "\\A[A-Za-z_$][A-Za-z0-9_$]*(?:\\.[A-Za-z_$][A-Za-z0-9_$]*){2,}\\z", RegexOptions.CultureInvariant)) throw Conflict("Invalid source package identity");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(package))) + ".json";
    }
    private static void JobId(string job) { if (!Regex.IsMatch(job, "\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant)) throw Conflict("Invalid repair job identity"); }
    private static void Hash(string? value, bool optional = false) { if (optional && value is null) return; if (value is null || !Regex.IsMatch(value, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)) throw Conflict("Invalid repair fingerprint"); }
    private static void HashNode(JsonNode? value, bool optional = false) { if (optional && value is null) return; Hash(TextOrNull(value)); }
    private static bool Matches(JsonNode? value, string pattern) => TextOrNull(value) is { } text && Regex.IsMatch(text, "\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant);
    private static bool Truth(JsonNode? value) => value is JsonArray array ? array.Count > 0 : TextOrNull(value) is { Length: > 0 };
    private static string? TextOrNull(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    private static string Text(JsonObject? value, string key) => value is null ? "" : TextOrNull(value[key]) ?? "";
    private static JsonObject Clone(JsonObject value) => (JsonObject)value.DeepClone();
    private static RepairHistoryConflictException Conflict(string message) => new(message);
}
