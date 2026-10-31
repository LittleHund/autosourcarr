using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Privacy;

namespace AutoSourcarr.Core.Diagnostics;

/// <summary>Export allowlists deliberately exclude source bodies, prompts, patches and accounts.</summary>
public static class RepairEvidence
{
    private static readonly HashSet<string> States = ["idle", "running", "complete", "queued", "checking-history", "checking-website", "checking-backup", "checking-apk-backup", "checking-baseline", "backup-verified", "backup-saved", "checking-upstream", "checkout", "building-repair", "repairing", "validating", "ready", "installed", "blocked", "failed", "cancelled", "interrupted", "discarded", "awaiting-raws", "raws-acquired", "raws-retained", "reconstructing", "checking-bytecode", "confirmed", "reported-not-working", "reverted"];
    private const string Package = @"[A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*){2,}";
    private static readonly string[] ProofFields = ["source_apk_sha256", "installed_jar_sha256", "converted_jar_sha256", "payload_sha256", "validation_envelope_sha256"];

    public static JsonObject? Operation(JsonNode? value, SupportPrivacy privacy)
    {
        if (value is not JsonObject source) return null;
        var result = new JsonObject { ["state"] = Choice(source["state"], States) };
        if (source["message"] is JsonValue message && message.TryGetValue<string>(out var text)) result["message"] = Cut(privacy.Text(text), 1200);
        foreach (var key in new[] { "id", "repair_job_id" }) CopyString(source, result, key, "[0-9a-f]{32}");
        foreach (var key in new[] { "pkg_name", "pkgName" }) CopyString(source, result, key, Package);
        return result;
    }
    public static JsonObject? Job(JsonNode? value, SupportPrivacy privacy)
    {
        if (value is not JsonObject source || Operation(source, privacy) is not { } result || !result.ContainsKey("id")) return null;
        foreach (var key in new[] { "created_at", "updated_at", "validation_time" }) if (Time(source[key]) is { } stamp) result[key] = stamp;
        foreach (var (key, pattern) in new[] { ("module", @"(?:\.|[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+){0,7})"), ("version", @"[0-9]+\.[0-9]+\.[0-9]+"),
            ("baseline_sha256", "[0-9a-f]{64}"), ("candidate_sha256", "[0-9a-f]{64}"), ("source_apk_sha256", "[0-9a-f]{64}") }) CopyString(source, result, key, pattern);
        if (D.Text(source, "origin") is "manual" or "automatic") result["origin"] = source["origin"]!.DeepClone();
        foreach (var key in new[] { "raw_first", "raw_only", "auto_install" }) if (source[key] is JsonValue scalar && scalar.TryGetValue<bool>(out var flag)) result[key] = flag;
        result["raw_code_available"] = Valid(source["raw_sha256"], "[0-9a-f]{64}") is not null;
        if (source["expected_ids"] is JsonArray ids && ids.Count <= 200 && ids.All(id => Valid(id, "-?[0-9]{1,20}") is not null)) result["expected_ids"] = ids.DeepClone();
        foreach (var (key, allowed) in new (string, string[])[] {
            ("baseline_format", ["native-jar", "converted-apk-jar"]), ("baseline_issue", ["converted-jar-without-manifest", "invalid-native-manifest"]),
            ("baseline_contract_basis", ["native-manifest", "recorded-installed-upload", "user-confirmed-original", "cold-import-installed-jar"]),
            ("baseline_metadata_basis", ["recorded-installed-upload", "user-confirmed-original", "installed-bytecode"]) })
            if (allowed.Contains(D.Text(source, key), StringComparer.Ordinal)) result[key] = source[key]!.DeepClone();
        if (source["conversion_proof"] is JsonObject proof) result["conversion_proof"] = HashFields(proof, ProofFields);
        if (source["baseline_import_proof"] is JsonObject imported)
        {
            var fields = HashFields(imported, ProofFields.Append("source_identity_sha256"));
            if (imported["verified"] is JsonValue verified && verified.TryGetValue<bool>(out var flag)) fields["verified"] = flag;
            result["baseline_import_proof"] = fields;
        }
        if (source["conversion_comparison"] is JsonObject comparison)
        {
            var fields = HashFields(comparison, ["source_apk_sha256", "installed_jar_sha256", "converted_jar_sha256"]);
            foreach (var key in new[] { "installed_entries", "converted_entries", "identical_entries", "changed_entries", "missing_entries", "added_entries", "changed_class_entries", "changed_resource_entries", "missing_class_entries", "added_class_entries" })
                if (Integer(comparison, key, 0, 4096) is { } number) fields[key] = number;
            result["conversion_comparison"] = fields;
        }
        if (source.ContainsKey("website_checks")) result["website_checks"] = Websites(source["website_checks"], privacy);
        if (source["address_migrations"] is JsonArray migrations) result["address_migration_count"] = Math.Min(200, migrations.Count);
        else if (Integer(source, "address_migration_count", 0, 200) is { } count) result["address_migration_count"] = count;
        if (source["baseline_contract"] is JsonObject contract)
        {
            var fields = new JsonObject();
            foreach (var (key, pattern) in new[] { ("package", @"[A-Za-z_$][A-Za-z0-9_.$]{1,240}"), ("entrypoint", @"[A-Za-z_$][A-Za-z0-9_.$]{1,240}"), ("library", @"[0-9]{1,3}\.[0-9]{1,3}") }) CopyString(contract, fields, key, pattern);
            result["baseline_contract"] = fields;
        }
        if (source["history"] is JsonArray history)
        {
            var rows = new JsonArray();
            foreach (var item in history.TakeLast(8))
                if (item is JsonObject row && Operation(row, privacy) is { } safe)
                {
                    var fields = new JsonObject { ["state"] = safe["state"]!.DeepClone() };
                    if (safe.ContainsKey("message")) fields["message"] = Cut(D.Text(safe, "message"), 600);
                    if (Time(row["at"]) is { } stamp) fields["at"] = stamp;
                    rows.Add(fields);
                }
            result["history"] = rows;
        }
        return result;
    }
    public static JsonObject? Isolation(JsonNode? value, SupportPrivacy privacy)
    {
        if (value is not JsonObject source) return null;
        var result = new JsonObject { ["available"] = source["available"] is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) ? JsonValue.Create(flag) : JsonValue.Create("unknown") };
        CopyString(source, result, "reason", "[a-z][a-z0-9-]{0,63}");
        foreach (var (key, maximum) in new[] { ("message", 1200), ("details", 1800) })
            if (source[key] is JsonValue text && text.TryGetValue<string>(out var detail)) result[key] = Cut(privacy.Text(detail), maximum);
        if (Integer(source, "exit_code", -255, 255) is { } code) result["exit_code"] = code;
        if (source["checks"] is JsonObject checks)
        {
            var fields = new JsonObject();
            foreach (var key in new[] { "NoNewPrivs", "Seccomp", "max_user_namespaces", "unprivileged_userns_clone", "apparmor_restrict_unprivileged_userns", "apparmor_restrict_unprivileged_userns_force", "proc_masked_paths", "proc_readonly_paths" })
                CopyString(checks, fields, key, "(?:[0-9]{1,20}|not reported)");
            if (checks["apparmor"] is JsonValue label && label.TryGetValue<string>(out var name)) fields["apparmor"] = Cut(privacy.Text(name), 250);
            result["checks"] = fields;
        }
        return result;
    }
    public static JsonArray Websites(JsonNode? value, SupportPrivacy privacy)
    {
        var result = new JsonArray(); if (value is not JsonArray rows) return result;
        foreach (var row in rows.Take(16).OfType<JsonObject>())
        {
            var item = new JsonObject { ["state"] = Choice(row["state"], ["working", "inconclusive", "verification", "rate_limited", "not_found"]) };
            foreach (var key in new[] { "requested_url", "final_url" })
                if (PublicAddress(row[key]) is { } address) item[key] = privacy.Text(address);
            if (Integer(row, "http_status", 100, 599) is { } status) item["http_status"] = status;
            foreach (var (key, allowed) in new (string, string[])[] { ("content_kind", ["html", "json", "other"]),
                ("reason", ["invalid-public-address", "connection-redirect-or-budget", "api-origin-changed", "invalid-or-truncated-json", "encoded-response-not-decoded"]) })
                if (allowed.Contains(D.Text(row, key), StringComparer.Ordinal)) item[key] = row[key]!.DeepClone();
            if (row["source_ids"] is JsonArray ids) item["source_ids"] = new JsonArray(ids.Take(200).Where(id => Valid(id, "-?[0-9]{1,20}") is not null).Select(id => id!.DeepClone()).ToArray());
            if (row["apis"] is JsonArray apis) item["apis"] = Websites(new JsonArray(apis.Take(2).OfType<JsonObject>().Where(api => !api.ContainsKey("apis")).Select(api => (JsonNode?)api.DeepClone()).ToArray()), privacy);
            result.Add(item);
        }
        return result;
    }
    internal static string Choice(JsonNode? value, IEnumerable<string> allowed, string fallback = "unknown") => allowed.Contains(D.Text(value), StringComparer.Ordinal) ? D.Text(value) : fallback;
    internal static long? Integer(JsonObject source, string key, long minimum, long maximum)
    {
        if (source[key]?.GetValueKind() != JsonValueKind.Number || !long.TryParse(source[key]!.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) return null;
        return value >= minimum && value <= maximum ? value : null;
    }
    private static JsonObject HashFields(JsonObject source, IEnumerable<string> names)
    { var result = new JsonObject(); foreach (var key in names) CopyString(source, result, key, "[0-9a-f]{64}"); return result; }
    private static void CopyString(JsonObject source, JsonObject target, string key, string pattern) { if (Valid(source[key], pattern) is { } value) target[key] = value; }
    private static string? Valid(JsonNode? value, string pattern) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && Regex.IsMatch(text, "\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant) ? text : null;
    private static double? Time(JsonNode? value) => value?.GetValueKind() == JsonValueKind.Number && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number) && number is >= 0 and <= 10_000_000_000_000 ? number : null;
    private static string? PublicAddress(JsonNode? value)
    {
        var text = D.Text(value);
        return text.Length is > 0 and <= 2048 && !text.Any(char.IsWhiteSpace) && Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? text : null;
    }
    private static string Cut(string value, int count) => Unicode.UnicodeCaseFold.Truncate(value, count);
}
