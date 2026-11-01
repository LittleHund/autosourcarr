using System.Text.Json.Nodes;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Catalogue.Structure.StructureSql;

namespace AutoSourcarr.Core.Catalogue.Structure;

internal static class CataloguePreferenceLineage
{
    public static void Reparent(DatabaseSession c, long target, long duplicate, string stamp, Action<DatabaseSession, long, long, string> display)
    {
        display(c, target, duplicate, stamp); ReparentIdentityReviews(c, target, duplicate, stamp); CatalogueIdentity.Reparent(c, target, duplicate);
        var upgrade = c.Query("SELECT * FROM serving_upgrade_policies WHERE entry_id=$p0", P(duplicate)).FirstOrDefault();
        c.Execute("UPDATE serving_policy_history SET entry_id=$p0 WHERE entry_id=$p1", P(target, duplicate));
        if (upgrade is not null)
        {
            c.Execute("INSERT INTO serving_policy_history(entry_id,old_entry_id,policy_json,reason,created_at) VALUES($p0,$p1,$p2,'absorbed policy; surviving choice remains authoritative',$p3)", P(target, duplicate, upgrade.Text("policy_json"), stamp));
            c.Execute("INSERT OR IGNORE INTO serving_upgrade_policies VALUES($p0,$p1,$p2,$p3)", P(target, upgrade.Integer("revision") + 1, upgrade.Text("policy_json"), stamp));
            c.Execute("DELETE FROM serving_upgrade_policies WHERE entry_id=$p0", P(duplicate));
        }
        c.Execute("UPDATE serving_choices SET entry_id=$p0 WHERE entry_id=$p1", P(target, duplicate));
        c.Execute("UPDATE serving_upgrade_history SET entry_id=$p0 WHERE entry_id=$p1", P(target, duplicate));
        c.Execute("DELETE FROM due_work WHERE kind='library' AND work_key IN ($p0,$p1)", P(target.ToString(System.Globalization.CultureInfo.InvariantCulture), duplicate.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        c.Execute("DELETE FROM library_complete_observations WHERE entry_id IN ($p0,$p1)", P(target, duplicate));
        ReparentLibrary(c, target, duplicate, stamp);
        var profile = c.Query("SELECT * FROM serving_profiles WHERE entry_id=$p0", P(duplicate)).FirstOrDefault(); var tracking = c.Query("SELECT * FROM library_tracking WHERE entry_id=$p0", P(duplicate)).FirstOrDefault();
        c.Execute("UPDATE entry_preference_history SET canonical_entry_id=$p0 WHERE canonical_entry_id=$p1", P(target, duplicate));
        if (profile is null && tracking is null) return;
        var title = c.Scalar("SELECT preferred_title FROM entries WHERE id=$p0", P(duplicate)) as string ?? "";
        c.Execute("INSERT INTO entry_preference_history(canonical_entry_id,old_entry_id,title,profile_json,tracking_json,saved_at) VALUES($p0,$p1,$p2,$p3,$p4,$p5)", P(target, duplicate, title, profile?.Text("profile_json") ?? "{}", tracking is null ? "{}" : CanonicalJson.Text(Json(tracking)), stamp));
        if (profile is not null)
        {
            if (c.Scalar("SELECT 1 FROM serving_profiles WHERE entry_id=$p0", P(target)) is null) c.Execute("UPDATE serving_profiles SET entry_id=$p0,revision=revision+1 WHERE entry_id=$p1", P(target, duplicate));
            else { c.Execute("UPDATE serving_profiles SET revision=revision+1 WHERE entry_id=$p0", P(target)); c.Execute("DELETE FROM serving_profiles WHERE entry_id=$p0", P(duplicate)); }
        }
        if (tracking is not null)
        {
            var current = c.Query("SELECT * FROM library_tracking WHERE entry_id=$p0", P(target)).FirstOrDefault() ?? tracking;
            var observation = JsonNode.Parse(current.Text("observation_json")) as JsonObject ?? throw new InvalidDataException("Retained library observation is invalid"); observation["download_state"] = "unknown"; observation["coverage_state"] = "verification";
            c.Execute("INSERT OR REPLACE INTO library_tracking VALUES($p0,$p1,$p2,$p3,$p4,'',$p5)", P(target, current.Flag("monitored") ? 1 : 0, Math.Max(current.Integer("revision"), tracking.Integer("revision")) + 1, current["main_manga_id"], CanonicalJson.Text(observation), stamp));
            c.Execute("DELETE FROM library_tracking WHERE entry_id=$p0", P(duplicate));
        }
    }
    /// <summary>
    /// Phase 10 CBZ library: membership, queued chapters, downloaded chapters and their history follow the surviving
    /// entry. When both were in the library the surviving entry keeps its own series folder; the absorbed folder's
    /// files stay where they are and are still known by their download rows.
    /// </summary>
    private static void ReparentLibrary(DatabaseSession c, long target, long duplicate, string stamp)
    {
        var absorbed = c.Query("SELECT in_library FROM library_series WHERE entry_id=$p0", P(duplicate)).FirstOrDefault();
        if (absorbed is not null)
        {
            if (c.Scalar("SELECT 1 FROM library_series WHERE entry_id=$p0", P(target)) is null)
                c.Execute("UPDATE library_series SET entry_id=$p0,updated_at=$p1 WHERE entry_id=$p2", P(target, stamp, duplicate));
            else
            {
                if (absorbed.Flag("in_library")) c.Execute("UPDATE library_series SET in_library=1,updated_at=$p0 WHERE entry_id=$p1", P(stamp, target));
                c.Execute("DELETE FROM library_series WHERE entry_id=$p0", P(duplicate));
            }
        }
        foreach (var table in new[] { "download_queue", "library_downloads", "download_history" })
            c.Execute("UPDATE " + table + " SET entry_id=$p0 WHERE entry_id=$p1", P(target, duplicate));
    }

    private static void ReparentIdentityReviews(DatabaseSession c, long target, long duplicate, string stamp)
    {
        var rows = c.Query("SELECT * FROM portable_identity_reviews WHERE entry_id=$p0", P(duplicate));
        c.Execute("UPDATE portable_identity_review_history SET entry_id=$p0 WHERE entry_id=$p1", P(target, duplicate));
        foreach (var row in rows)
        {
            c.Execute("INSERT INTO portable_identity_review_history(entry_id,old_entry_id,reference,decision,evidence_sha256,note,reason,created_at) VALUES($p0,$p1,$p2,$p3,$p4,$p5,'retained after merge; surviving decision wins; restore requires review',$p6)", P(target, duplicate, row.Text("reference"), row.Text("decision"), row.Text("evidence_sha256"), row.Text("note"), stamp));
            c.Execute("INSERT OR IGNORE INTO portable_identity_reviews VALUES($p0,$p1,$p2,$p3,$p4,$p5)", P(target, row.Text("reference"), row.Text("decision") == "excluded" ? "excluded" : "needs-review", row.Text("evidence_sha256"), row.Text("note"), stamp));
        }
        c.Execute("DELETE FROM portable_identity_reviews WHERE entry_id=$p0", P(duplicate));
    }
}
