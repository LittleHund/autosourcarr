using System.Globalization;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Cutover;

/// <summary>Which comparison group an item counts in for the verdict.</summary>
public enum ParityGroup { Subscription, Manual, Import, Provenance }

/// <summary>
/// One kind of user-owned row compared between the 0.4.x file and the migrated file. <see cref="Key"/> identifies a row
/// (entry columns in it are mapped through the merge aliases to canonical ids), <see cref="Values"/> must stay equal.
/// <see cref="Entries"/> lists the columns holding entry ids; <see cref="Pair"/> makes the first two key columns an
/// unordered pair. <see cref="Expand"/> turns a row into several (the Subbed snapshot JSON).
/// </summary>
public sealed record ParityKind(string Name, ParityGroup Group, bool Origins, string Table, string Sql, string[] Key, string[] Values,
    string[] Entries, string Description, bool Pair = false, Func<Dictionary<string, string?>, IEnumerable<Dictionary<string, string?>>>? Expand = null);

/// <summary>A missing or changed row: its key, the work it belongs to (title), and the compared values (never secrets).</summary>
public sealed record ParityItem(string Key, string Title, string? Before, string? After)
{
    public JsonObject ToJson() => new() { ["key"] = Key, ["title"] = Title, ["before"] = Before, ["after"] = After };
}

public sealed class ParityKindResult
{
    public required ParityKind Kind { get; init; }
    /// <summary>False when the 0.4.x file has no such table (an older 0.4.x): nothing to compare.</summary>
    public bool Compared { get; init; }
    public int Original { get; init; }
    public int Migrated { get; init; }
    public List<ParityItem> Missing { get; } = [];
    public List<ParityItem> Changed { get; } = [];
    public int Lost => Missing.Count + Changed.Count;
}

/// <summary>
/// Phase 14 parity: compares every manual or user-owned decision of the original 0.4.x databases with the migrated ones.
/// Both files are opened read-only. A row is lost when its key is gone, or changed when a compared value differs (for
/// entry columns: when it no longer leads to the same existing canonical entry).
/// </summary>
public static class DecisionParity
{
    public const string SubbedSnapshotKey = "subbed_display_snapshot_v1";

    private static ParityKind K(string name, ParityGroup group, string table, string sql, string[] key, string[] values, string[] entries, string description,
        bool origins = false, bool pair = false) => new(name, group, origins, table, sql, key, values, entries, description, pair);

    public static readonly IReadOnlyList<ParityKind> Kinds =
    [
        new("Subbed (main-library subscriptions)", ParityGroup.Subscription, false, "settings",
            "SELECT value FROM settings WHERE key='" + SubbedSnapshotKey + "'", ["subscribed_id"], [], ["entry_id"],
            "AutoSourcarr's saved view of your main-Suwayomi library (the subscriptions live in Suwayomi itself; each subscribed id must still open an entry).",
            Expand: ExpandSubbed),
        K("Library tracking (monitored / main-library id)", ParityGroup.Subscription, "library_tracking",
            "SELECT entry_id,monitored,main_manga_id FROM library_tracking WHERE monitored=1 OR main_manga_id IS NOT NULL", ["entry_id"], ["monitored", "main_manga_id"], ["entry_id"],
            "Monitored works and their main-library manga id."),

        K("Manual titles and aliases", ParityGroup.Manual, "titles",
            "SELECT entry_id,title_type,normalized,source_id,title,language FROM titles WHERE title_type IN ('manual','main-library')",
            ["entry_id", "title_type", "normalized", "source_id"], ["title", "language"], ["entry_id"], "Titles you typed (manual) and main-library titles."),
        K("Manual preferred titles", ParityGroup.Manual, "entries",
            "SELECT id AS entry_id,preferred_title FROM entries WHERE manual_preferred_title=1", ["entry_id"], ["preferred_title"], ["entry_id"], "Entries whose display title you chose."),
        K("Preferred-title overrides", ParityGroup.Manual, "preferred_title_overrides",
            "SELECT entry_id,title FROM preferred_title_overrides", ["entry_id"], ["title"], ["entry_id"], "Per-entry title overrides."),
        // 0.4.x itself moves these into manual_title_scopes and empties the table at every start (db.py v0.2.82), so a row
        // counts as kept while the same record and title is still a scoped manual title.
        K("Legacy source-keyed title overrides", ParityGroup.Manual, "preferred_title_override_sources",
            "SELECT source_id,source_manga_id,title FROM preferred_title_override_sources UNION SELECT source_id,source_manga_id,title FROM manual_title_scopes",
            ["source_id", "source_manga_id", "title"], [], [], "Old title overrides keyed by source record; kept as manual title scopes."),
        K("Manual title scopes", ParityGroup.Manual, "manual_title_scopes",
            "SELECT entry_id,source_id,source_manga_id,title FROM manual_title_scopes", ["entry_id", "source_id", "source_manga_id"], ["title"], ["entry_id"], "Which source record a manual title belongs to."),
        K("Manual and locked source records (matches)", ParityGroup.Manual, "source_matches",
            "SELECT 'primary' AS kind,entry_id,source_id,source_manga_id,match_type,locked,active FROM source_matches WHERE locked=1 OR lower(match_type) NOT LIKE 'auto%' "
            + "UNION ALL SELECT 'additional',entry_id,source_id,source_manga_id,match_type,locked,active FROM source_variants WHERE locked=1 OR lower(match_type) NOT LIKE 'auto%'",
            ["kind", "source_id", "source_manga_id"], ["entry_id", "match_type", "locked", "active"], ["entry_id"], "Records you matched, locked, imported or split by hand, and the entry they belong to."),
        K("Manual match groups", ParityGroup.Manual, "manual_match_groups",
            "SELECT id,title FROM manual_match_groups", ["id"], ["title"], [], "Groups of records you put together."),
        K("Manual match bindings", ParityGroup.Manual, "manual_match_bindings",
            "SELECT source_id,source_manga_id,group_id,grouping_suppressed FROM manual_match_bindings", ["source_id", "source_manga_id"], ["group_id", "grouping_suppressed"], [],
            "Records bound to a manual group."),
        K("Separate entries (records kept apart)", ParityGroup.Manual, "manual_separate_records",
            "SELECT target_entry_id,source_id,source_manga_id FROM manual_separate_records", ["target_entry_id", "source_id", "source_manga_id"], [], ["target_entry_id"],
            "Records you said are not this entry."),
        K("Duplicate reviews (different works / same work)", ParityGroup.Manual, "duplicate_reviews",
            "SELECT left_entry_id,right_entry_id,decision FROM duplicate_reviews", ["left_entry_id", "right_entry_id"], ["decision"], ["left_entry_id", "right_entry_id"],
            "Rejected matches: pairs you reviewed as different works (or the same).", pair: true),
        K("Mixed-entry reviews", ParityGroup.Manual, "mixed_entry_reviews",
            "SELECT fingerprint FROM mixed_entry_reviews", ["fingerprint"], [], [], "Mixed entries you reviewed as fine."),
        K("Tag reviews", ParityGroup.Manual, "tag_reviews",
            "SELECT l.namespace||':'||l.normalized AS left_tag,r.namespace||':'||r.normalized AS right_tag,t.decision FROM tag_reviews t JOIN tags l ON l.id=t.left_id JOIN tags r ON r.id=t.right_id",
            ["left_tag", "right_tag"], ["decision"], [], "Tag pairs you reviewed as same or different."),
        K("Manual or locked identity links", ParityGroup.Manual, "identity_links",
            "SELECT entry_id,provider,external_id,match_type,locked FROM identity_links WHERE locked=1 OR match_type<>'auto'",
            ["entry_id", "provider", "external_id"], ["match_type", "locked"], ["entry_id"], "Provider identities (MangaDex, AniList, ...) you set or locked."),
        K("Portable identity reviews", ParityGroup.Manual, "portable_identity_reviews",
            "SELECT entry_id,reference,decision FROM portable_identity_reviews", ["entry_id", "reference"], ["decision"], ["entry_id"], "Shared-catalogue identity reviews."),
        K("Deleted source records (kept out)", ParityGroup.Manual, "deleted_source_records",
            "SELECT source_id,source_manga_id FROM deleted_source_records", ["source_id", "source_manga_id"], [], [], "Records you deleted; they are not added again."),
        K("Shared-catalogue record bindings", ParityGroup.Manual, "catalogue_record_bindings",
            "SELECT source_id,source_manga_id,reference,entry_id FROM catalogue_record_bindings", ["source_id", "source_manga_id", "reference"], ["entry_id"], ["entry_id"],
            "Records bound by an imported shared catalogue."),
        K("Shared-catalogue decisions", ParityGroup.Manual, "catalogue_decisions",
            "SELECT uid,digest,state FROM catalogue_decisions", ["uid", "digest"], ["state"], [], "Decisions taken while combining shared catalogues."),
        K("Source choices", ParityGroup.Manual, "source_config",
            "SELECT source_id,enabled_language,enabled_search,enabled_chapters,priority,group_id FROM source_config", ["source_id"],
            ["enabled_language", "enabled_search", "enabled_chapters", "priority", "group_id"], [], "Which sources are enabled, their priority and group."),
        K("Source groups", ParityGroup.Manual, "source_groups",
            "SELECT id,name,priority FROM source_groups", ["id"], ["name", "priority"], [], "Your source groups."),
        K("Matching policies", ParityGroup.Manual, "matching_policies",
            "SELECT scope,subject,direction,rule_json FROM matching_policies", ["scope", "subject", "direction"], ["rule_json"], [], "Per-source and per-group matching rules."),
        K("Matching presets", ParityGroup.Manual, "matching_presets",
            "SELECT id,name,rule_json FROM matching_presets", ["id"], ["name", "rule_json"], [], "Saved matching presets."),
        K("Serving profiles", ParityGroup.Manual, "serving_profiles",
            "SELECT entry_id,profile_json FROM serving_profiles", ["entry_id"], ["profile_json"], ["entry_id"], "Per-entry chapter serving preferences."),
        K("Serving upgrade policies", ParityGroup.Manual, "serving_upgrade_policies",
            "SELECT entry_id,policy_json FROM serving_upgrade_policies", ["entry_id"], ["policy_json"], ["entry_id"], "Per-entry quality upgrade policies."),
        K("UI preferences", ParityGroup.Manual, "user_ui_preferences",
            "SELECT user_key,values_json FROM user_ui_preferences", ["user_key"], ["values_json"], [], "Saved Web UI preferences per user."),

        K("Import runs", ParityGroup.Import, "import_runs",
            "SELECT id,state,total,added,existing,unresolved,failed,skipped FROM import_runs", ["id"], ["state", "total", "added", "existing", "unresolved", "failed", "skipped"], [],
            "Your backup imports."),
        K("Import decisions (items)", ParityGroup.Import, "import_items",
            "SELECT id,import_id,state,entry_id FROM import_items", ["id"], ["import_id", "state", "entry_id"], ["entry_id"], "Each imported title and what was decided for it."),
        K("Import rejections", ParityGroup.Import, "import_match_rejections",
            "SELECT backup_key,signature FROM import_match_rejections", ["backup_key", "signature"], [], [], "Import matches you rejected."),
        K("Manual import auto-resolve memory", ParityGroup.Import, "import_manual_autoresolve",
            "SELECT backup_key,target_source_id,target_source_manga_id FROM import_manual_autoresolve", ["backup_key"], ["target_source_id", "target_source_manga_id"], [],
            "Manual import choices remembered for the next import."),
        K("Manual import auto-resolve state", ParityGroup.Import, "import_manual_autoresolve_state",
            "SELECT key,value FROM import_manual_autoresolve_state", ["key"], ["value"], [], "State of the remembered import choices."),

        K("Served chapter origins", ParityGroup.Provenance, "served_origin",
            "SELECT virtual_chapter_id,candidate_id,reason FROM served_origin", ["virtual_chapter_id"], ["candidate_id", "reason"], [], "Which source serves each chapter.", origins: true),
        K("Chapter candidate rejections", ParityGroup.Provenance, "chapter_candidate_rejections",
            "SELECT source_id,source_chapter_id,source_manga_id FROM chapter_candidate_rejections", ["source_id", "source_chapter_id"], ["source_manga_id"], [],
            "Chapter versions you rejected.", origins: true),
        K("Origin history", ParityGroup.Provenance, "origin_history",
            "SELECT id,virtual_chapter_id,candidate_id,source_id,source_chapter_id FROM origin_history", ["id"], ["virtual_chapter_id", "candidate_id", "source_id", "source_chapter_id"], [],
            "Every earlier origin change.", origins: true),
        K("Hidden chapters", ParityGroup.Provenance, "virtual_chapters",
            "SELECT id,entry_id,chapter_key FROM virtual_chapters WHERE hidden=1", ["id"], ["chapter_key"], [], "Chapters you hid.", origins: true),
        K("Serving choices", ParityGroup.Provenance, "serving_choices",
            "SELECT virtual_chapter_id,candidate_id FROM serving_choices", ["virtual_chapter_id"], ["candidate_id"], [], "Chapter versions you chose to serve."),
    ];

    /// <summary>The Subbed snapshot JSON ({"payload":{"entry_ids":[...]}}) as one row per subscribed entry id.</summary>
    private static IEnumerable<Dictionary<string, string?>> ExpandSubbed(Dictionary<string, string?> row)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(row["value"] ?? ""); }
        catch (System.Text.Json.JsonException) { yield break; }
        var ids = node?["payload"]?["entry_ids"] as JsonArray ?? node?["entry_ids"] as JsonArray;
        if (ids is null) yield break;
        foreach (var id in ids)
        {
            var text = id?.ToJsonString().Trim('"') ?? "";
            if (text.Length == 0) continue;
            yield return new Dictionary<string, string?>(StringComparer.Ordinal) { ["subscribed_id"] = text, ["entry_id"] = text };
        }
    }

    /// <summary>Compares every kind. <paramref name="original"/> and <paramref name="migrated"/> are (catalog, origins) pairs.</summary>
    public static List<ParityKindResult> Compare((LegacyDatabase Catalog, LegacyDatabase Origins) original, (LegacyDatabase Catalog, LegacyDatabase Origins) migrated,
        IEnumerable<ParityKind>? kinds = null)
    {
        var before = new EntryIds(original.Catalog);
        var after = new EntryIds(migrated.Catalog);
        var results = new List<ParityKindResult>();
        foreach (var kind in kinds ?? Kinds)
        {
            var o = kind.Origins ? original.Origins : original.Catalog;
            var m = kind.Origins ? migrated.Origins : migrated.Catalog;
            if (!o.HasTable(kind.Table) || (kind.Table == "settings" && !o.HasColumns("settings", ["key", "value"])))
            {
                results.Add(new ParityKindResult { Kind = kind, Compared = false });
                continue;
            }
            var oldRows = Rows(o, kind, id => (before.Exists(id) ? after.Resolve(before.Resolve(id)).ToString(CultureInfo.InvariantCulture) : "missing entry"), id => before.Title(id));
            var newRows = m.HasTable(kind.Table) ? Rows(m, kind, id => after.Exists(id) ? after.Resolve(id).ToString(CultureInfo.InvariantCulture) : "missing entry", _ => "")
                : new Dictionary<string, (string Values, string Title)>(StringComparer.Ordinal);
            var result = new ParityKindResult { Kind = kind, Compared = true, Original = oldRows.Count, Migrated = newRows.Count };
            foreach (var (key, (values, title)) in oldRows.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!newRows.TryGetValue(key, out var now)) result.Missing.Add(new ParityItem(key, title, values, null));
                else if (!string.Equals(now.Values, values, StringComparison.Ordinal)) result.Changed.Add(new ParityItem(key, title, values, now.Values));
            }
            results.Add(result);
        }
        return results;
    }

    private static Dictionary<string, (string Values, string Title)> Rows(LegacyDatabase database, ParityKind kind, Func<long, string> entry, Func<long, string> title)
    {
        var rows = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        IEnumerable<Dictionary<string, string?>> raw = database.Query(kind.Sql);
        if (kind.Expand is { } expand) raw = raw.SelectMany(expand);
        foreach (var row in raw)
        {
            string Cell(string column)
            {
                var value = row.TryGetValue(column, out var text) ? text ?? "" : "";
                return kind.Entries.Contains(column, StringComparer.Ordinal) && EntryIds.Id(value) is { } id ? entry(id) : value;
            }
            var keyParts = kind.Key.Select(Cell).ToArray();
            if (kind.Pair && keyParts.Length >= 2 && string.CompareOrdinal(keyParts[0], keyParts[1]) > 0) (keyParts[0], keyParts[1]) = (keyParts[1], keyParts[0]);
            var key = string.Join(" / ", keyParts);
            var values = string.Join(" | ", kind.Values.Select(Cell));
            var label = "";
            foreach (var column in kind.Entries)
                if (row.TryGetValue(column, out var raw2) && EntryIds.Id(raw2) is { } id && title(id) is { Length: > 0 } found) { label = found; break; }
            rows.TryAdd(key, (values, label));
        }
        return rows;
    }

    /// <summary>Row counts of every table that exists in both files.</summary>
    public static List<(string File, string Table, long Original, long Migrated)> Counts(LegacyDatabase original, LegacyDatabase migrated, string file)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(migrated);
        return original.Tables.Where(migrated.HasTable).Order(StringComparer.Ordinal)
            .Select(table => (file, table, original.Count(table), migrated.Count(table))).ToList();
    }

    /// <summary>Every 0.4.x entry id (entries and merge aliases) that no longer leads to an existing entry after the migration.</summary>
    public static List<long> UnresolvedEntries(LegacyDatabase original, LegacyDatabase migrated)
    {
        var before = new EntryIds(original); var after = new EntryIds(migrated);
        return before.AllIds.Where(before.Exists).Where(id => !after.Exists(before.Resolve(id))).Distinct().Order().ToList();
    }
}
