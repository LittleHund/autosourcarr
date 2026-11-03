using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Core.Cutover;

/// <summary>Which comparison group an item counts in for the verdict.</summary>
public enum ParityGroup { Subscription, Manual, Import, Provenance, History }

/// <summary>Extra rule a kind applies to every 0.4.x row on top of key/value equality.</summary>
public enum ParityCheck
{
    None,
    /// <summary>The first two key columns are an unordered entry pair that must stay two different entries (a "different works" review).</summary>
    DistinctPair,
    /// <summary>manual_separate_records: the record must not be attached (primary or additional) to the target entry.</summary>
    SeparateNotAttached,
}

/// <summary>What a kind check may look at.</summary>
public sealed record ParityContext(EntryIds Before, EntryIds After, LegacyDatabase OriginalCatalog, LegacyDatabase MigratedCatalog);

/// <summary>
/// One kind of user-owned rows compared between the 0.4.x file and the migrated file. <see cref="Key"/> identifies a row
/// (entry columns are mapped through the merge aliases to canonical ids), <see cref="Values"/> must stay equal, and the
/// number of rows per key must stay the same (two rows that collapse into one after the alias mapping count as lost).
/// <see cref="Entries"/> lists the columns holding entry ids. <see cref="Expand"/> turns a row into several (the Subbed
/// snapshot JSON). <see cref="CountOnly"/> kinds (history tables) compare their row count: fewer rows = lost.
/// <see cref="Settled"/> says when a 0.4.x row that is gone is fine (an interrupted operation the start completed).
/// </summary>
public sealed record ParityKind(string Name, ParityGroup Group, bool Origins, string Table, string Sql, string[] Key, string[] Values,
    string[] Entries, string Description, Func<Dictionary<string, string?>, IEnumerable<Dictionary<string, string?>>>? Expand = null,
    ParityCheck Check = ParityCheck.None, bool CountOnly = false, Func<ParityContext, Dictionary<string, string?>, bool>? Settled = null);

/// <summary>A missing or changed row: its key, the work it belongs to (title), and the compared values (never secrets).</summary>
public sealed record ParityItem(string Key, string Title, string? Before, string? After)
{
    public JsonObject ToJson() => new() { ["key"] = Key, ["title"] = Title, ["before"] = Before, ["after"] = After };
}

public sealed class ParityKindResult
{
    public required ParityKind Kind { get; init; }
    /// <summary>False when the 0.4.x file has no such table or columns (an older 0.4.x): nothing to compare (<see cref="Note"/> says why).</summary>
    public bool Compared { get; init; }
    public string? Note { get; set; }
    public int Original { get; set; }
    public int Migrated { get; set; }
    public List<ParityItem> Missing { get; } = [];
    public List<ParityItem> Changed { get; } = [];
    /// <summary>Not losses: states that were already so in 0.4.x (for example a reviewed pair already merged there).</summary>
    public List<ParityItem> Preexisting { get; } = [];
    public int Lost => Missing.Count + Changed.Count;
}

/// <summary>
/// Phase 14 parity: compares every manual or user-owned decision of the original 0.4.x databases with the migrated ones.
/// Both files are opened read-only. A row is lost when its key is gone (or fewer rows share it), or changed when a
/// compared value differs (for entry columns: when it no longer leads to the same existing canonical entry), or when a
/// kind check says the decision is no longer honoured.
/// </summary>
public static class DecisionParity
{
    public const string SubbedSnapshotKey = "subbed_display_snapshot_v1";
    public const string MissingEntry = "missing entry";

    /// <summary>Settings rows that are caches or run markers, rebuilt by the app (not compared; see the coverage table).</summary>
    public static readonly string[] VolatileSettings =
    [
        "duplicate_review_snapshot_v1", "continuous_catalog_cursor", "automatic_duplicate_last_error", "automatic_duplicate_last_run",
        "automatic_duplicate_last_checked", "automatic_duplicate_last_merged", "reimagined_engine_contract", "source_solver_probe_v1", SubbedSnapshotKey,
    ];

    private static ParityKind K(string name, ParityGroup group, string table, string sql, string[] key, string[] values, string[] entries, string description,
        bool origins = false, ParityCheck check = ParityCheck.None, Func<ParityContext, Dictionary<string, string?>, bool>? settled = null)
        => new(name, group, origins, table, sql, key, values, entries, description, Check: check, Settled: settled);

    private static ParityKind History(string table, string description, bool origins = false)
        => new("History: " + table, ParityGroup.History, origins, table, "SELECT COUNT(*) AS n FROM " + table, [], [], [], description, CountOnly: true);

    public static readonly IReadOnlyList<ParityKind> Kinds =
    [
        new("Subbed (main-library subscriptions)", ParityGroup.Subscription, false, "settings",
            "SELECT value FROM settings WHERE key='" + SubbedSnapshotKey + "'", ["subscribed_id"], ["entry_id"], ["entry_id"],
            "AutoSourcarr's saved view of your main-Suwayomi library (the subscriptions live in Suwayomi itself; each subscribed id must still open the same entry).",
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
        K("Display metadata you chose or locked", ParityGroup.Manual, "display_metadata",
            "SELECT entry_id,fields_json FROM display_metadata", ["entry_id"], ["fields_json"], ["entry_id"], "Per-entry display fields (chosen values and locks)."),
        K("Manual and locked source records (matches)", ParityGroup.Manual, "source_matches",
            "SELECT 'primary' AS kind,entry_id,source_id,source_manga_id,match_type,locked,active FROM source_matches WHERE locked=1 OR lower(match_type) NOT LIKE 'auto%' "
            + "UNION ALL SELECT 'additional',entry_id,source_id,source_manga_id,match_type,locked,active FROM source_variants WHERE locked=1 OR lower(match_type) NOT LIKE 'auto%'",
            ["kind", "source_id", "source_manga_id"], ["entry_id", "match_type", "locked", "active"], ["entry_id"], "Records you matched, locked, imported or split by hand, and the entry they belong to."),
        K("Merges (entry aliases)", ParityGroup.Manual, "entry_aliases",
            "SELECT old_entry_id,canonical_entry_id,reason FROM entry_aliases", ["old_entry_id"], ["canonical_entry_id"], ["canonical_entry_id"],
            "Every earlier merge (manual and automatic): the merged id must still lead to the same entry."),
        K("Pending merges (interrupted)", ParityGroup.Manual, "pending_entry_merges",
            "SELECT duplicate_entry_id,target_entry_id FROM pending_entry_merges", ["duplicate_entry_id"], ["target_entry_id"], ["target_entry_id"],
            "Merges a 0.4.x stop interrupted; gone is fine only when the merge was completed.",
            settled: (context, row) => EntryIds.Id(row["duplicate_entry_id"]) is { } duplicate && EntryIds.Id(row["target_entry_id"]) is { } target
                && context.After.Exists(target) && context.After.Resolve(duplicate) == context.After.Resolve(context.Before.Resolve(target))),
        K("Pending splits (interrupted)", ParityGroup.Manual, "pending_entry_splits",
            "SELECT id,original_entry_id,new_entry_id,records_json FROM pending_entry_splits", ["id"], ["original_entry_id", "new_entry_id", "records_json"], ["original_entry_id", "new_entry_id"],
            "Splits a 0.4.x stop interrupted; gone is fine only when both entries exist afterwards.",
            settled: (context, row) => EntryIds.Id(row["original_entry_id"]) is { } original && EntryIds.Id(row["new_entry_id"]) is { } created
                && context.After.Exists(original) && context.After.Exists(created) && context.After.Resolve(original) != context.After.Resolve(created)),
        K("Manual match groups", ParityGroup.Manual, "manual_match_groups",
            "SELECT id,title FROM manual_match_groups", ["id"], ["title"], [], "Groups of records you put together."),
        K("Manual match bindings", ParityGroup.Manual, "manual_match_bindings",
            "SELECT source_id,source_manga_id,group_id,grouping_suppressed,entry_id_hint FROM manual_match_bindings", ["source_id", "source_manga_id"],
            ["group_id", "grouping_suppressed", "entry_id_hint"], ["entry_id_hint"], "Records bound to a manual group, and the entry they point at."),
        K("Separate entries (records kept apart)", ParityGroup.Manual, "manual_separate_records",
            "SELECT target_entry_id,source_id,source_manga_id FROM manual_separate_records", ["target_entry_id", "source_id", "source_manga_id"], [], ["target_entry_id"],
            "Records you said are not this entry; the record must not be attached to it.", check: ParityCheck.SeparateNotAttached),
        K("Duplicate reviews (different works / same work)", ParityGroup.Manual, "duplicate_reviews",
            "SELECT left_entry_id,right_entry_id,decision FROM duplicate_reviews", ["left_entry_id", "right_entry_id"], ["decision"], ["left_entry_id", "right_entry_id"],
            "Rejected matches: pairs you reviewed as different works (they must stay two entries) or as the same.", check: ParityCheck.DistinctPair),
        K("Mixed-entry reviews", ParityGroup.Manual, "mixed_entry_reviews",
            "SELECT fingerprint,entry_id_hint FROM mixed_entry_reviews", ["fingerprint"], ["entry_id_hint"], ["entry_id_hint"], "Mixed entries you reviewed as fine."),
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
            "SELECT source_id,enabled_language,enabled_search,enabled_chapters,priority,group_id,official_manual FROM source_config", ["source_id"],
            ["enabled_language", "enabled_search", "enabled_chapters", "priority", "group_id", "official_manual"], [],
            "Which sources are enabled, their priority, group and your official/unofficial choice."),
        K("Source groups", ParityGroup.Manual, "source_groups",
            "SELECT id,name,priority FROM source_groups", ["id"], ["name", "priority"], [], "Your source groups."),
        K("Gallery policies", ParityGroup.Manual, "gallery_policies",
            "SELECT source_id,policy_json FROM gallery_policies", ["source_id"], ["policy_json"], [], "Per-source gallery family rules you set."),
        K("Matching policies", ParityGroup.Manual, "matching_policies",
            "SELECT scope,subject,direction,rule_json FROM matching_policies", ["scope", "subject", "direction"], ["rule_json"], [], "Per-source and per-group matching rules."),
        K("Matching presets", ParityGroup.Manual, "matching_presets",
            "SELECT id,name,rule_json FROM matching_presets", ["id"], ["name", "rule_json"], [], "Saved matching presets."),
        K("Review workbench actions", ParityGroup.Manual, "matching_review_actions",
            "SELECT id,action_json,undone_at FROM matching_review_actions", ["id"], ["action_json", "undone_at"], [], "Your review workbench actions and their undo state."),
        K("Serving profiles", ParityGroup.Manual, "serving_profiles",
            "SELECT entry_id,profile_json FROM serving_profiles", ["entry_id"], ["profile_json"], ["entry_id"], "Per-entry chapter serving preferences."),
        K("Serving upgrade policies", ParityGroup.Manual, "serving_upgrade_policies",
            "SELECT entry_id,policy_json FROM serving_upgrade_policies", ["entry_id"], ["policy_json"], ["entry_id"], "Per-entry quality upgrade policies."),
        K("UI preferences", ParityGroup.Manual, "user_ui_preferences",
            "SELECT user_key,values_json FROM user_ui_preferences", ["user_key"], ["values_json"], [], "Saved Web UI preferences per user."),
        K("App state in the settings table", ParityGroup.Manual, "settings",
            "SELECT key,CASE WHEN key LIKE '%\\_version' ESCAPE '\\' THEN '(version)' ELSE value END AS value FROM settings WHERE key NOT IN ("
            + string.Join(",", VolatileSettings.Select(key => "'" + key + "'")) + ")", ["key"], ["value"], [],
            "Every settings-table row (source health days, human-attention and solver gates, one-time upgrade markers by presence); caches excluded."),

        K("Import runs", ParityGroup.Import, "import_runs",
            "SELECT id,state,total,added,existing,unresolved,failed,skipped FROM import_runs", ["id"], ["state", "total", "added", "existing", "unresolved", "failed", "skipped"], [],
            "Your backup imports."),
        K("Import decisions (items)", ParityGroup.Import, "import_items",
            "SELECT id,import_id,state,entry_id,reference_entry_id,suggestion_state FROM import_items", ["id"], ["import_id", "state", "entry_id", "reference_entry_id", "suggestion_state"],
            ["entry_id", "reference_entry_id"], "Each imported title, what was decided for it and its suggestion."),
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
        K("Serving choices", ParityGroup.Provenance, "serving_choices",
            "SELECT virtual_chapter_id,entry_id,candidate_id FROM serving_choices", ["virtual_chapter_id"], ["entry_id", "candidate_id"], ["entry_id"], "Chapter versions you chose to serve."),

        History("display_metadata_history", "Earlier display-metadata versions."),
        History("duplicate_review_history", "Every earlier duplicate review."),
        History("duplicate_split_history", "Every earlier split."),
        History("match_history", "Every earlier match change and merge."),
        History("entry_preference_history", "Preferences of merged entries."),
        History("portable_identity_review_history", "Earlier identity reviews."),
        History("serving_policy_history", "Earlier serving policies."),
        History("serving_upgrade_history", "Earlier serving upgrades."),
        History("deleted_entries_history", "Entries you deleted."),
        History("automatic_merge_events", "Automatic merges with their undo snapshots."),
        History("automatic_merge_lineage", "Members of automatic merges."),
        History("policy_preview_history", "Matching policy previews."),
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

    private sealed record Row(string Key, string Values, string Title, Dictionary<string, string?> Raw);

    /// <summary>Compares every kind. <paramref name="original"/> and <paramref name="migrated"/> are (catalog, origins) pairs.</summary>
    public static List<ParityKindResult> Compare((LegacyDatabase Catalog, LegacyDatabase Origins) original, (LegacyDatabase Catalog, LegacyDatabase Origins) migrated,
        IEnumerable<ParityKind>? kinds = null)
    {
        var context = new ParityContext(new EntryIds(original.Catalog), new EntryIds(migrated.Catalog), original.Catalog, migrated.Catalog);
        var results = new List<ParityKindResult>();
        foreach (var kind in kinds ?? Kinds)
        {
            var o = kind.Origins ? original.Origins : original.Catalog;
            var m = kind.Origins ? migrated.Origins : migrated.Catalog;
            if (!o.HasTable(kind.Table))
            {
                results.Add(new ParityKindResult { Kind = kind, Compared = false, Note = "no " + kind.Table + " table in this 0.4.x" });
                continue;
            }
            if (kind.CountOnly) { results.Add(CompareCount(kind, o, m)); continue; }
            List<Row> before;
            try { before = Rows(o, kind, id => context.Before.Exists(id) ? context.After.Resolve(context.Before.Resolve(id)).ToString(CultureInfo.InvariantCulture) : MissingEntry, context.Before.Title); }
            catch (SqliteException error)
            {
                // An older 0.4.x without a column this kind reads: compare what exists, say so, never crash.
                results.Add(new ParityKindResult { Kind = kind, Compared = false, Note = "not comparable with this 0.4.x schema (" + error.Message + ")" });
                continue;
            }
            var result = new ParityKindResult { Kind = kind, Compared = true, Original = before.Count };
            List<Row> after;
            try { after = m.HasTable(kind.Table) ? Rows(m, kind, id => context.After.Exists(id) ? context.After.Resolve(id).ToString(CultureInfo.InvariantCulture) : MissingEntry, _ => "") : []; }
            catch (SqliteException error) { after = []; result.Note = "the migrated file cannot be read for this kind (" + error.Message + ")"; }
            result.Migrated = after.Count;
            var now = after.GroupBy(row => row.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(row => row.Values).ToList(), StringComparer.Ordinal);
            foreach (var group in before.GroupBy(row => row.Key, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                if (!now.TryGetValue(group.Key, out var remaining))
                {
                    foreach (var row in group)
                        if (kind.Settled?.Invoke(context, row.Raw) != true) result.Missing.Add(new ParityItem(row.Key, row.Title, row.Values, null));
                    continue;
                }
                remaining = [.. remaining];
                foreach (var row in group)
                {
                    var index = remaining.FindIndex(value => string.Equals(value, row.Values, StringComparison.Ordinal));
                    if (index >= 0) { remaining.RemoveAt(index); continue; }
                    result.Changed.Add(new ParityItem(row.Key, row.Title, row.Values,
                        remaining.Count > 0 ? string.Join(" ; ", remaining) : "(fewer rows: " + group.Count().ToString(CultureInfo.InvariantCulture) + " in 0.4.x share this key after the merge mapping)"));
                }
            }
            foreach (var row in before) Verify(kind, context, row, result);
            results.Add(result);
        }
        return results;
    }

    private static ParityKindResult CompareCount(ParityKind kind, LegacyDatabase o, LegacyDatabase m)
    {
        var before = (int)o.Count(kind.Table);
        var after = m.HasTable(kind.Table) ? (int)m.Count(kind.Table) : 0;
        var result = new ParityKindResult { Kind = kind, Compared = true, Original = before, Migrated = after };
        if (after < before)
            result.Missing.Add(new ParityItem(kind.Table, "", before.ToString(CultureInfo.InvariantCulture) + " rows", after.ToString(CultureInfo.InvariantCulture) + " rows"));
        return result;
    }

    /// <summary>The kind checks: a reviewed "different works" pair that became one entry, a separated record attached again.</summary>
    private static void Verify(ParityKind kind, ParityContext context, Row row, ParityKindResult result)
    {
        switch (kind.Check)
        {
            case ParityCheck.DistinctPair:
            {
                if (row.Raw.GetValueOrDefault("decision") is "same" or "duplicate") return; // a "same work" review has nothing to keep apart
                if (EntryIds.Id(row.Raw.GetValueOrDefault(kind.Key[0])) is not { } left || EntryIds.Id(row.Raw.GetValueOrDefault(kind.Key[1])) is not { } right) return;
                var before = context.Before.Resolve(left) == context.Before.Resolve(right);
                var after = context.After.Resolve(context.Before.Resolve(left)) == context.After.Resolve(context.Before.Resolve(right));
                if (before) result.Preexisting.Add(new ParityItem(row.Key, row.Title, "both entries were already one entry in 0.4.x", null));
                else if (after) result.Changed.Add(new ParityItem(row.Key, row.Title, row.Values, "VIOLATED: both entries are one entry in 0.5.0"));
                return;
            }
            case ParityCheck.SeparateNotAttached:
            {
                if (EntryIds.Id(row.Raw.GetValueOrDefault("target_entry_id")) is not { } target) return;
                var source = row.Raw.GetValueOrDefault("source_id"); var manga = row.Raw.GetValueOrDefault("source_manga_id");
                var before = Owners(context.OriginalCatalog, source, manga).Select(context.Before.Resolve).Contains(context.Before.Resolve(target));
                var after = Owners(context.MigratedCatalog, source, manga).Select(context.After.Resolve).Contains(context.After.Resolve(context.Before.Resolve(target)));
                if (before) result.Preexisting.Add(new ParityItem(row.Key, row.Title, "the record was already attached to this entry in 0.4.x", null));
                else if (after) result.Changed.Add(new ParityItem(row.Key, row.Title, "kept apart", "VIOLATED: the record is attached to this entry in 0.5.0"));
                return;
            }
        }
    }

    private static IEnumerable<long> Owners(LegacyDatabase catalog, string? source, string? manga)
    {
        if (!catalog.HasTable("source_matches")) return [];
        var sql = "SELECT entry_id FROM source_matches WHERE source_id=$s AND source_manga_id=$m AND active=1"
            + (catalog.HasTable("source_variants") ? " UNION SELECT entry_id FROM source_variants WHERE source_id=$s AND source_manga_id=$m AND active=1" : "");
        return catalog.Query(sql, ("$s", source), ("$m", long.TryParse(manga, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : manga))
            .Select(row => EntryIds.Id(row["entry_id"])).OfType<long>().ToList();
    }

    private static List<Row> Rows(LegacyDatabase database, ParityKind kind, Func<long, string> entry, Func<long, string> title)
    {
        var rows = new List<Row>();
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
            if (kind.Check == ParityCheck.DistinctPair && keyParts.Length >= 2 && string.CompareOrdinal(keyParts[0], keyParts[1]) > 0) (keyParts[0], keyParts[1]) = (keyParts[1], keyParts[0]);
            var label = "";
            foreach (var column in kind.Entries)
                if (row.TryGetValue(column, out var cell) && EntryIds.Id(cell) is { } id && title(id) is { Length: > 0 } found) { label = found; break; }
            rows.Add(new Row(string.Join(" / ", keyParts), string.Join(" | ", kind.Values.Select(Cell)), label, row));
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

    /// <summary>Tables of the 0.4.x file that the migrated file no longer has (none is retired on purpose in 0.5.0).</summary>
    public static List<string> DroppedTables(LegacyDatabase original, LegacyDatabase migrated, string file)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(migrated);
        return original.Tables.Where(table => !migrated.HasTable(table) && !RetiredTables.ContainsKey(table)).Order(StringComparer.Ordinal).Select(table => file + "." + table).ToList();
    }

    /// <summary>0.4.x tables 0.5.0 removes on purpose, with the reason (none so far).</summary>
    public static readonly IReadOnlyDictionary<string, string> RetiredTables = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Every 0.4.x entry id (entries and merge aliases) that no longer leads to an existing entry after the migration.</summary>
    public static List<long> UnresolvedEntries(LegacyDatabase original, LegacyDatabase migrated)
    {
        var before = new EntryIds(original); var after = new EntryIds(migrated);
        return before.AllIds.Where(before.Exists).Where(id => !after.Exists(before.Resolve(id))).Distinct().Order().ToList();
    }
}
