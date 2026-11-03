using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Cutover;

/// <summary>How the parity report treats one 0.4.x table.</summary>
public sealed record TableCoverageItem(string File, string Table, string Treatment, string Detail)
{
    public JsonObject ToJson() => new() { ["file"] = File, ["table"] = Table, ["treatment"] = Treatment, ["detail"] = Detail };
}

/// <summary>
/// Every table the 0.4.5 code creates (db.py, reimagined_state.py, gallery_policy.py, catalog_identity.py,
/// identity_review.py: 86 tables) plus any other table found in the 0.4.x files, with how the report covers it:
/// compared as a kind, counted (fewer rows = lost), or counted only because it is rebuildable (with the reason).
/// A table that is neither in this list nor a kind is counted with loss = FAILED.
/// </summary>
public static class TableCoverage
{
    /// <summary>Rebuildable or derived 0.4.x tables: their counts are shown, a change does not fail the verdict.</summary>
    public static readonly IReadOnlyDictionary<string, string> Rebuildable = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["browse_cache"] = "browse page cache, refetched", ["translation_cache"] = "translation cache, refetched",
        ["metadata_query_cache"] = "provider query cache, refetched", ["crawl_pending"] = "crawler work queue, rebuilt by the crawler",
        ["due_work"] = "monitoring clocks, rebuilt from library_tracking", ["quality_attempts"] = "quality probe attempts, re-measured",
        ["quality_samples"] = "page quality samples, re-measured", ["source_record_scans"] = "chapter scan cursors, rescanned",
        ["candidate_pages"] = "page lists of chapter versions, refetched", ["chapter_candidates"] = "chapter versions found on sources, rescanned (choices are compared via served_origin/serving_choices)",
        ["catalogue_exchange_stages"] = "staging of an unfinished shared-catalogue import", ["gallery_records"] = "gallery metadata of source records, refetched",
        ["gallery_layout_candidates"] = "gallery split candidates, recomputed", ["source_color_profiles"] = "detected colour profiles, re-detected",
        ["source_discovery_outcomes"] = "last discovery result per entry and source, rediscovered", ["source_health"] = "source health checks, rechecked",
        ["source_record_tags"] = "tags read from source records, re-read", ["thumbnail_fingerprints"] = "cover fingerprints, recomputed",
        ["library_complete_observations"] = "library completeness observations, re-observed", ["general_task_history"] = "0.4.x task list; 0.5.0 keeps its own task journal",
        ["entry_preferred_titles"] = "derived title index of entries", ["tags"] = "tag index (tag reviews are compared by tag name)",
        ["source_matches"] = "automatic records are rebuildable; manual and locked ones are compared as a kind", ["source_variants"] = "as source_matches",
        ["titles"] = "source titles are rebuilt; manual and main-library titles are compared as a kind",
        ["identity_links"] = "automatic identities are re-resolved; manual and locked ones are compared as a kind",
        ["entries"] = "every 0.4.x entry id must still resolve to an entry (Entries section); manual preferred titles are a kind",
        ["virtual_chapters"] = "chapter list, rebuilt from sources; 'hidden' is computed (split-chapter parts covered by a whole chapter, service.py _reconcile_split_chapters), not a user choice",
        ["sqlite_sequence"] = "SQLite internal",
    };

    /// <summary>0.4.x tables of the shared-catalogue identity: counted, fewer rows = lost.</summary>
    public static readonly string[] CountedIdentity =
    [
        "catalogue_origin", "catalogue_entry_ids", "catalogue_uid_records", "catalogue_shared_entries", "catalogue_public_refs",
        "catalogue_import_records", "catalogue_record_resolutions", "catalogue_exchange_receipts", "catalogue_source_facts",
    ];

    /// <summary>The 86 tables the 0.4.5 code creates, by file (origins = chapter-origins.sqlite).</summary>
    public static readonly IReadOnlyList<(string File, string Table)> Known =
    [
        .. new[] { "candidate_pages", "chapter_candidate_rejections", "chapter_candidates", "origin_history", "quality_samples", "served_origin", "source_record_scans", "virtual_chapters" }
            .Select(table => ("origins", table)),
        .. new[]
        {
            "automatic_merge_events", "automatic_merge_lineage", "browse_cache", "catalogue_decisions", "catalogue_entry_ids", "catalogue_exchange_receipts",
            "catalogue_exchange_stages", "catalogue_import_records", "catalogue_origin", "catalogue_public_refs", "catalogue_record_bindings", "catalogue_record_resolutions",
            "catalogue_shared_entries", "catalogue_source_facts", "catalogue_uid_records", "crawl_pending", "deleted_entries_history", "deleted_source_records",
            "display_metadata", "display_metadata_history", "due_work", "duplicate_review_history", "duplicate_reviews", "duplicate_split_history", "entries",
            "entry_aliases", "entry_preference_history", "entry_preferred_titles", "gallery_layout_candidates", "gallery_policies", "gallery_records",
            "general_task_history", "identity_links", "import_items", "import_manual_autoresolve", "import_manual_autoresolve_state", "import_match_rejections",
            "import_runs", "library_complete_observations", "library_tracking", "manual_match_bindings", "manual_match_groups", "manual_separate_records",
            "manual_title_scopes", "match_history", "matching_policies", "matching_presets", "matching_review_actions", "metadata_query_cache",
            "mixed_entry_reviews", "pending_entry_merges", "pending_entry_splits", "policy_preview_history", "portable_identity_review_history",
            "portable_identity_reviews", "preferred_title_override_sources", "preferred_title_overrides", "quality_attempts", "serving_choices",
            "serving_policy_history", "serving_profiles", "serving_upgrade_history", "serving_upgrade_policies", "settings", "source_color_profiles",
            "source_config", "source_discovery_outcomes", "source_groups", "source_health", "source_matches", "source_record_tags", "source_variants",
            "tag_reviews", "tags", "thumbnail_fingerprints", "titles", "translation_cache", "user_ui_preferences",
        }.Select(table => ("catalog", table)),
    ];

    /// <summary>Count-only kinds for the counted identity tables and for any 0.4.x table nobody classified (fewer rows = lost).</summary>
    public static IEnumerable<ParityKind> CountedKinds(LegacyDatabase catalog, LegacyDatabase origins, IEnumerable<ParityKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(origins);
        var compared = kinds.Select(kind => (kind.Origins ? "origins" : "catalog") + "." + kind.Table).ToHashSet(StringComparer.Ordinal);
        foreach (var table in CountedIdentity.Where(catalog.HasTable).Where(table => !compared.Contains("catalog." + table)))
            yield return Count(table, false, "Shared-catalogue identity rows.");
        foreach (var (file, database) in new[] { ("catalog", catalog), ("origins", origins) })
            foreach (var table in database.Tables.Order(StringComparer.Ordinal))
                if (!compared.Contains(file + "." + table) && !Rebuildable.ContainsKey(table) && !CountedIdentity.Contains(table, StringComparer.Ordinal))
                    yield return Count(table, file == "origins", "Not classified by the tool: counted so nothing can disappear unseen.");
    }

    private static ParityKind Count(string table, bool origins, string description) =>
        new("Counted: " + table, ParityGroup.History, origins, table, "SELECT COUNT(*) AS n FROM " + table, [], [], [], description, CountOnly: true);

    /// <summary>The coverage table of the report: every known 0.4.5 table and every table found in the 0.4.x files.</summary>
    public static List<TableCoverageItem> Describe(LegacyDatabase catalog, LegacyDatabase origins, IEnumerable<ParityKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(origins);
        var all = kinds.ToList();
        var tables = Known.Concat(catalog.Tables.Select(table => ("catalog", table))).Concat(origins.Tables.Select(table => ("origins", table)))
            .Distinct().OrderBy(item => item.Item1, StringComparer.Ordinal).ThenBy(item => item.Item2, StringComparer.Ordinal);
        var items = new List<TableCoverageItem>();
        foreach (var (file, table) in tables)
        {
            var present = (file == "origins" ? origins : catalog).HasTable(table);
            var mine = all.Where(kind => (kind.Origins ? "origins" : "catalog") == file && (kind.Table == table || kind.Sql.Contains(" " + table + " ", StringComparison.Ordinal))).ToList();
            var compared = mine.Where(kind => !kind.CountOnly).Select(kind => kind.Name).ToList();
            var counted = mine.Where(kind => kind.CountOnly).Select(kind => kind.Name).ToList();
            string treatment, detail;
            if (compared.Count > 0) { treatment = "compared"; detail = string.Join("; ", compared) + (Rebuildable.TryGetValue(table, out var rest) ? " (" + rest + ")" : ""); }
            else if (counted.Count > 0) { treatment = "counted (fewer rows = lost)"; detail = string.Join("; ", counted); }
            else if (Rebuildable.TryGetValue(table, out var reason)) { treatment = "counted only (rebuildable)"; detail = reason; }
            else { treatment = "counted (fewer rows = lost)"; detail = "not classified"; }
            items.Add(new(file, table, present ? treatment : "not in this 0.4.x", detail));
        }
        return items;
    }
}
