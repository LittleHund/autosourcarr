using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Matching.Manual.ManualSql;

namespace AutoSourcarr.Core.Catalogue.Maintenance;

/// <summary>
/// The manual decisions a normal database reset keeps although they name entries by id: "different works" reviews
/// (duplicate_reviews not_duplicate), separate-entry rules, main-title overrides, title scopes and manual aliases.
/// The reset clears the entries, so before clearing it saves the source records each named entry held
/// (<see cref="CarrySetting"/>); after subscriptions and manual matches are restored, <see cref="RekeyKeptDecisions"/>
/// points every kept decision at the entry that now holds those records (or that came back under its id / alias) and
/// drops — and counts — the ones whose work is not back.
/// </summary>
public sealed partial class CatalogueMaintenance
{
    /// <summary>Settings row holding what a normal reset must re-key (removed once re-keyed, and by a full reset).</summary>
    public const string CarrySetting = "reset_kept_decisions_v1";

    /// <summary>What a normal reset clears, stated exactly (the route summary and the reset log use it).</summary>
    public const string NormalResetSummary = "Database reset: entries, automatic matches and titles, merge/split history, automatic duplicate state and caches are cleared. "
        + "Kept: manual matches and groups (restored), deleted-record suppression, mixed-entry reviews, import history and configuration. "
        + "Different-works reviews, separate-entry rules, manual titles, title scopes and manual aliases are re-attached to the entry that holds their records after the restore; "
        + "those whose work is not back are dropped and counted in the summary.";

    private static void CaptureKeptDecisions(DatabaseSession c)
    {
        var ids = new SortedSet<long>();
        foreach (var row in c.Query("SELECT left_entry_id,right_entry_id FROM duplicate_reviews WHERE decision='not_duplicate'")) { ids.Add(row.Integer("left_entry_id")); ids.Add(row.Integer("right_entry_id")); }
        foreach (var row in c.Query("SELECT target_entry_id,COALESCE(separate_entry_id_hint,0) hint FROM manual_separate_records")) { ids.Add(row.Integer("target_entry_id")); ids.Add(row.Integer("hint")); }
        foreach (var sql in new[] { "SELECT entry_id FROM preferred_title_overrides", "SELECT entry_id FROM manual_title_scopes", "SELECT entry_id FROM titles WHERE title_type='manual'" })
            foreach (var row in c.Query(sql)) ids.Add(row.Integer("entry_id"));
        var records = new JsonObject();
        foreach (var id in ids.Where(id => id > 0))
        {
            var owner = Canonical(c, id);
            var held = c.Query("SELECT source_id,source_manga_id FROM source_matches WHERE entry_id=$p0 AND active=1 UNION SELECT source_id,source_manga_id FROM source_variants WHERE entry_id=$p0 AND active=1", P(owner));
            if (held.Count == 0) continue;
            records[id.ToString(CultureInfo.InvariantCulture)] = new JsonArray(held.Select(row => (JsonNode)new JsonArray(row.Text("source_id"), row.Integer("source_manga_id"))).ToArray());
        }
        var aliases = new JsonArray(c.Query("SELECT entry_id,title,normalized,language,source_id,source_manga_id FROM titles WHERE title_type='manual' ORDER BY id").Select(row => (JsonNode)new JsonObject
        {
            ["entry_id"] = row.Integer("entry_id"), ["title"] = row.Text("title"), ["normalized"] = row.Text("normalized"), ["language"] = row.Text("language"),
            ["source_id"] = row.Text("source_id"), ["source_manga_id"] = row.Integer("source_manga_id"),
        }).ToArray());
        var carry = new JsonObject { ["records"] = records, ["aliases"] = aliases };
        c.Execute("INSERT INTO settings(key,value) VALUES($p0,$p1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", P(CarrySetting, carry.ToJsonString()));
    }

    /// <summary>
    /// After a normal reset's restore: re-keys the kept id-based manual decisions to the entries that now hold their
    /// records (aliasing the old id to it, like the manual-match restore) and drops the ones whose work is not back.
    /// Answers kept/dropped counts per kind; a no-op without a pending reset.
    /// </summary>
    public JsonObject RekeyKeptDecisions()
    {
        var result = Write(c =>
        {
            if (c.Scalar("SELECT value FROM settings WHERE key=$p0", P(CarrySetting)) is not string raw) return new JsonObject { ["pending"] = false };
            JsonObject carry;
            try { carry = JsonNode.Parse(raw) as JsonObject ?? new JsonObject(); }
            catch (JsonException) { carry = new JsonObject(); }
            var records = carry["records"] as JsonObject ?? new JsonObject();
            var stamp = Stamp();
            var resolved = new Dictionary<long, long?>();
            long? Resolve(long id)
            {
                if (id <= 0) return null;
                if (resolved.TryGetValue(id, out var known)) return known;
                long? answer = null;
                var canonical = Canonical(c, id);
                if (EntryExists(c, canonical)) answer = canonical;
                else if (records[id.ToString(CultureInfo.InvariantCulture)] is JsonArray held)
                {
                    var owners = held.OfType<JsonArray>().Where(pair => pair.Count == 2)
                        .Select(pair => Owner(c, pair[0]?.GetValue<string>() ?? "", pair[1]?.GetValue<long>() ?? 0)).OfType<long>().Distinct().ToArray();
                    if (owners.Length == 1)
                    {
                        answer = owners[0];
                        if (canonical == id && c.Scalar("SELECT 1 FROM entry_aliases WHERE old_entry_id=$p0", P(id)) is null)
                            c.Execute("INSERT INTO entry_aliases(old_entry_id,canonical_entry_id,reason,merged_at) VALUES($p0,$p1,'reset-rekey',$p2)", P(id, answer.Value, stamp));
                    }
                }
                resolved[id] = answer;
                return answer;
            }
            int reviewsKept = 0, reviewsDropped = 0, separateKept = 0, separateDropped = 0, overridesKept = 0, overridesDropped = 0, scopesKept = 0, scopesDropped = 0, aliasesKept = 0, aliasesDropped = 0;
            foreach (var row in c.Query("SELECT left_entry_id,right_entry_id,reviewed_at FROM duplicate_reviews WHERE decision='not_duplicate'"))
            {
                var (oldLeft, oldRight) = (row.Integer("left_entry_id"), row.Integer("right_entry_id"));
                var (left, right) = (Resolve(oldLeft), Resolve(oldRight));
                if (left is null || right is null || left == right) { c.Execute("DELETE FROM duplicate_reviews WHERE left_entry_id=$p0 AND right_entry_id=$p1", P(oldLeft, oldRight)); reviewsDropped++; continue; }
                var (low, high) = Pair(left.Value, right.Value);
                if (low != oldLeft || high != oldRight)
                {
                    c.Execute("DELETE FROM duplicate_reviews WHERE left_entry_id=$p0 AND right_entry_id=$p1", P(oldLeft, oldRight));
                    c.Execute("INSERT INTO duplicate_reviews(left_entry_id,right_entry_id,decision,reviewed_at) VALUES($p0,$p1,'not_duplicate',$p2) ON CONFLICT(left_entry_id,right_entry_id) DO UPDATE SET decision='not_duplicate'", P(low, high, row.Text("reviewed_at")));
                }
                reviewsKept++;
            }
            foreach (var row in c.Query("SELECT * FROM manual_separate_records"))
            {
                var oldTarget = row.Integer("target_entry_id"); var source = row.Text("source_id"); var manga = row.Integer("source_manga_id");
                var target = Resolve(oldTarget);
                if (target is null) { c.Execute("DELETE FROM manual_separate_records WHERE target_entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2", P(oldTarget, source, manga)); separateDropped++; continue; }
                var hint = row["separate_entry_id_hint"] is null ? null : Resolve(row.Integer("separate_entry_id_hint"));
                c.Execute("DELETE FROM manual_separate_records WHERE target_entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2", P(oldTarget, source, manga));
                c.Execute("""
                    INSERT INTO manual_separate_records(target_entry_id,source_id,source_manga_id,separate_entry_id_hint,source_title,created_at,updated_at) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)
                    ON CONFLICT(target_entry_id,source_id,source_manga_id) DO NOTHING
                    """, P(target.Value, source, manga, hint, row.Text("source_title"), row.Text("created_at"), stamp));
                separateKept++;
            }
            foreach (var row in c.Query("SELECT entry_id,title,updated_at FROM preferred_title_overrides"))
            {
                var old = row.Integer("entry_id"); var entry = Resolve(old);
                if (entry is null) { c.Execute("DELETE FROM preferred_title_overrides WHERE entry_id=$p0", P(old)); overridesDropped++; continue; }
                if (entry.Value != old)
                {
                    c.Execute("DELETE FROM preferred_title_overrides WHERE entry_id=$p0", P(old));
                    if (c.Scalar("SELECT 1 FROM preferred_title_overrides WHERE entry_id=$p0", P(entry.Value)) is not null) { overridesDropped++; continue; }
                    c.Execute("INSERT INTO preferred_title_overrides(entry_id,title,updated_at) VALUES($p0,$p1,$p2)", P(entry.Value, row.Text("title"), row.Text("updated_at")));
                }
                c.Execute("UPDATE entries SET preferred_title=$p0,manual_preferred_title=1,updated_at=$p1 WHERE id=$p2 AND manual_preferred_title=0", P(row.Text("title"), stamp, entry.Value));
                overridesKept++;
            }
            foreach (var row in c.Query("SELECT * FROM manual_title_scopes"))
            {
                var old = row.Integer("entry_id"); var source = row.Text("source_id"); var manga = row.Integer("source_manga_id");
                var entry = Owner(c, source, manga) ?? Resolve(old);
                c.Execute("DELETE FROM manual_title_scopes WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2", P(old, source, manga));
                if (entry is null) { scopesDropped++; continue; }
                c.Execute("INSERT OR IGNORE INTO manual_title_scopes(entry_id,source_id,source_manga_id,title,captured_at) VALUES($p0,$p1,$p2,$p3,$p4)", P(entry.Value, source, manga, row.Text("title"), row.Text("captured_at")));
                scopesKept++;
            }
            foreach (var alias in (carry["aliases"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var entry = Resolve(Integer(alias["entry_id"]));
                if (entry is null) { aliasesDropped++; continue; }
                c.Execute("INSERT OR IGNORE INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES($p0,$p1,$p2,$p3,'manual',$p4,$p5)",
                    P(entry.Value, Text(alias["title"]), Text(alias["normalized"]), Text(alias["language"]), Text(alias["source_id"]), Integer(alias["source_manga_id"])));
                aliasesKept++;
            }
            c.Execute("DELETE FROM settings WHERE key=$p0", P(CarrySetting));
            var summary = new JsonObject
            {
                ["pending"] = false,
                ["different_works_reviews"] = new JsonObject { ["kept"] = reviewsKept, ["dropped"] = reviewsDropped },
                ["separate_rules"] = new JsonObject { ["kept"] = separateKept, ["dropped"] = separateDropped },
                ["manual_titles"] = new JsonObject { ["kept"] = overridesKept, ["dropped"] = overridesDropped },
                ["title_scopes"] = new JsonObject { ["kept"] = scopesKept, ["dropped"] = scopesDropped },
                ["manual_aliases"] = new JsonObject { ["kept"] = aliasesKept, ["dropped"] = aliasesDropped },
                ["note"] = "Dropped decisions named works that were not restored (no subscribed entry or manually bound record holds their records).",
            };
            Log("reset.database.rekey", DecisionActor.Automatic, resolved.Values.OfType<long>(), [], "Kept manual decisions re-attached after a database reset", after: (JsonObject)summary.DeepClone());
            return summary;
        });
        _hooks.ResetCaches?.Invoke();
        return result;
    }
}
