using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Duplicates;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Core.Cutover;

/// <summary>Which comparison group an item counts in for the verdict.</summary>
public enum ParityGroup { Subscription, Manual, Import, Provenance, History }

/// <summary>Extra rule a kind applies to every 0.4.x row on top of whole-row equality.</summary>
public enum ParityCheck
{
    None,
    /// <summary>The first two key columns are an unordered entry pair that must stay two different entries (a "different works" review).</summary>
    DistinctPair,
    /// <summary>manual_separate_records: the record must not be attached (primary or additional) to the target entry.</summary>
    SeparateNotAttached,
}

/// <summary>What a kind check may look at. <see cref="Before"/> resolves through the 0.4.x aliases only, <see cref="After"/> through the migrated ones.</summary>
public sealed record ParityContext(EntryIds Before, EntryIds After, LegacyDatabase OriginalCatalog, LegacyDatabase MigratedCatalog);

/// <summary>
/// One kind of user-owned rows compared between the 0.4.x file and the migrated file, as WHOLE rows: every column the
/// 0.4.x row has except the <see cref="Key"/>, pure timestamps (columns ending in <c>_at</c>, <c>last_checked</c>,
/// <c>last_seen</c>, <c>first_seen</c>; unless listed in <see cref="KeepTimestamps"/> or the kind is <see cref="Raw"/>)
/// and the columns in <see cref="Exclude"/> (with <see cref="ExcludeReason"/>, shown in the report). Entry columns
/// (<see cref="Entries"/>) compare as the canonical entry each side resolves to through its OWN merge aliases, so a merge
/// the migration made shows as a change. The number of rows per key must stay the same. <see cref="KeepOut"/> kinds (rules
/// that keep records out) also fail on rows the migration added. <see cref="Raw"/> kinds (append-only history) compare
/// every column as stored, keyed by their id.
/// </summary>
public sealed record ParityKind(string Name, ParityGroup Group, bool Origins, string Table, string Sql, string[] Key, string[] Entries, string Description)
{
    public string[] Exclude { get; init; } = [];
    public string ExcludeReason { get; init; } = "";
    public string[] KeepTimestamps { get; init; } = [];
    public bool Raw { get; init; }
    public bool KeepOut { get; init; }
    public ParityCheck Check { get; init; }
    /// <summary>Catalogue kinds that join chapter-origins.sqlite (attached as <c>o</c>).</summary>
    public bool NeedsOrigins { get; init; }
    public Func<Dictionary<string, string?>, IEnumerable<Dictionary<string, string?>>>? Expand { get; init; }
    /// <summary>Adds computed columns to a row (for example the manual/automatic class of a merge reason).</summary>
    public Action<Dictionary<string, string?>>? Derive { get; init; }
    /// <summary>When a 0.4.x row that is gone is fine (an interrupted operation the start completed).</summary>
    public Func<ParityContext, Dictionary<string, string?>, bool>? Settled { get; init; }
    /// <summary>When two different values of one column still count as equal (key, column, 0.4.x value, migrated value).</summary>
    public Func<string, string, string?, string?, bool>? Equivalent { get; init; }

    public static bool IsTimestamp(string column) => column.EndsWith("_at", StringComparison.Ordinal) || column is "last_checked" or "last_seen" or "first_seen";
}

/// <summary>A missing, changed or added row: its key, the work it belongs to (title), and the compared values (never secrets).</summary>
public sealed record ParityItem(string Key, string Title, string? Before, string? After)
{
    public JsonObject ToJson() => new() { ["key"] = Key, ["title"] = Title, ["before"] = Before, ["after"] = After };
}

public sealed class ParityKindResult
{
    public required ParityKind Kind { get; init; }
    /// <summary>False when the 0.4.x file has no such table, or the table is empty and cannot be queried (<see cref="Note"/> says why).</summary>
    public bool Compared { get; init; }
    public string? Note { get; set; }
    public int Original { get; set; }
    public int Migrated { get; set; }
    /// <summary>The compared columns (whole row minus key, timestamps and excluded columns).</summary>
    public IReadOnlyList<string> Columns { get; set; } = [];
    public List<ParityItem> Missing { get; } = [];
    public List<ParityItem> Changed { get; } = [];
    /// <summary>Rows the migration added to a kind that keeps records out (deleted records, separate rules, rejections, reviews).</summary>
    public List<ParityItem> Added { get; } = [];
    /// <summary>Not losses: states that were already so in 0.4.x (for example a reviewed pair already merged there).</summary>
    public List<ParityItem> Preexisting { get; } = [];
    public int Lost => Missing.Count + Changed.Count + Added.Count;
}

/// <summary>An entry the migration merged into another (its alias is new or points elsewhere than in 0.4.x).</summary>
public sealed record NewMerge(long Entry, long Into, string Title, string Reason, bool Fails, string Why)
{
    public JsonObject ToJson() => new() { ["entry_id"] = Entry, ["into"] = Into, ["title"] = Title, ["reason"] = Reason, ["fails"] = Fails, ["why"] = Why };
}

/// <summary>
/// Phase 14 parity: compares every manual, user-owned or provenance row of the original 0.4.x databases with the migrated
/// ones (both opened read-only). A row is lost when its key is gone (or fewer rows share it), changed when a compared
/// column differs, added when a keep-out kind gained it, or violated when a kind check says the decision is no longer
/// honoured.
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

    /// <summary>One-time upgrade markers and the value 0.5.0 stores when it ran the step (a marker may move to this value only).</summary>
    public static readonly IReadOnlyDictionary<string, string> ExpectedVersions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["startup_catalog_maintenance_version"] = "\"0.2.69\"", ["automatic_merge_safety_repair_version"] = "\"0.2.69\"",
        ["bound_title_consensus_version"] = "\"0.2.78\"", ["subbed_orphan_alias_repair_version"] = "\"0.2.80\"",
    };

    private const string SourceMetadata = "source metadata fetched from the source (thumbnail, description, author, genre, metadata_json, initialized, real_url) is refreshed from it; not a decision";

    private static string Candidate(string alias) =>
        alias + ".source_id AS " + alias + "_source_id," + alias + ".source_manga_id AS " + alias + "_manga_id," + alias + ".source_chapter_id AS " + alias + "_chapter_id";

    private static ParityKind History(string table, string description, bool origins = false) =>
        new("History: " + table, ParityGroup.History, origins, table, "SELECT * FROM " + table, ["id"], [], description) { Raw = true };

    private static ParityKind Whole(string name, ParityGroup group, string table, string[] key, string[] entries, string description, string? where = null, bool origins = false)
        => new(name, group, origins, table, "SELECT * FROM " + table + (where is null ? "" : " WHERE " + where), key, entries, description);

    public static readonly IReadOnlyList<ParityKind> Kinds =
    [
        new("Subbed (main-library subscriptions)", ParityGroup.Subscription, false, "settings",
            "SELECT value FROM settings WHERE key='" + SubbedSnapshotKey + "'", ["subscribed_id"], ["entry_id"],
            "AutoSourcarr's saved view of your main-Suwayomi library (the subscriptions live in Suwayomi itself; each subscribed id must still open the same entry).")
            { Expand = ExpandSubbed },
        Whole("Library tracking (monitored / main-library id)", ParityGroup.Subscription, "library_tracking", ["entry_id"], ["entry_id"],
            "Monitoring and the main-library manga id of each work.") with
            { Exclude = ["revision", "observation_json"], ExcludeReason = "the last library observation and its counter are re-observed from the library; not a decision" },

        Whole("Manual titles and aliases", ParityGroup.Manual, "titles", ["entry_id", "title_type", "normalized", "source_id", "source_manga_id"], ["entry_id"],
            "Titles you typed (manual) and main-library titles.", "title_type IN ('manual','main-library')") with { Exclude = ["id"], ExcludeReason = "row id (the key identifies the title)" },
        new("Manual preferred titles", ParityGroup.Manual, false, "entries",
            "SELECT id AS entry_id,preferred_title,manual_preferred_title FROM entries WHERE manual_preferred_title=1", ["entry_id"], ["entry_id"],
            "Entries whose display title you chose (the other entry columns are source metadata, refreshed from the sources)."),
        Whole("Preferred-title overrides", ParityGroup.Manual, "preferred_title_overrides", ["entry_id"], ["entry_id"], "Per-entry title overrides."),
        // 0.4.x itself moves these into manual_title_scopes and empties the table at every start (db.py v0.2.82), so a row
        // counts as kept while the same record and title is still a scoped manual title.
        new("Legacy source-keyed title overrides", ParityGroup.Manual, false, "preferred_title_override_sources",
            "SELECT source_id,source_manga_id,title FROM preferred_title_override_sources UNION SELECT source_id,source_manga_id,title FROM manual_title_scopes",
            ["source_id", "source_manga_id", "title"], [], "Old title overrides keyed by source record; kept as manual title scopes."),
        Whole("Manual title scopes", ParityGroup.Manual, "manual_title_scopes", ["entry_id", "source_id", "source_manga_id"], ["entry_id"], "Which source record a manual title belongs to."),
        Whole("Display metadata you chose or locked", ParityGroup.Manual, "display_metadata", ["entry_id"], ["entry_id"], "Per-entry display fields (chosen values and locks)."),
        new("Manual and locked source records (matches)", ParityGroup.Manual, false, "source_matches",
            "SELECT 'primary' AS kind,entry_id,source_id,source_manga_id,source_manga_url,source_title,confidence,match_type,locked,active FROM source_matches WHERE locked=1 OR lower(match_type) NOT LIKE 'auto%' "
            + "UNION ALL SELECT 'additional',entry_id,source_id,source_manga_id,source_manga_url,source_title,confidence,match_type,locked,active FROM source_variants WHERE locked=1 OR lower(match_type) NOT LIKE 'auto%'",
            ["kind", "source_id", "source_manga_id"], ["entry_id"], "Records you matched, locked, imported or split by hand, and the entry they belong to.")
            { ExcludeReason = SourceMetadata },
        Whole("Merges (entry aliases)", ParityGroup.Manual, "entry_aliases", ["old_entry_id"], ["canonical_entry_id"],
            "Every earlier merge: the merged id must still lead to the same entry, with the same reason and manual/automatic class.") with
            { Derive = row => row["merge_class"] = DuplicateMatching.AutomaticMergeReason(row.GetValueOrDefault("reason") ?? "") ? "automatic" : "manual" },
        Whole("Pending merges (interrupted)", ParityGroup.Manual, "pending_entry_merges", ["duplicate_entry_id"], ["target_entry_id"],
            "Chapter moves of merges a 0.4.x stop interrupted; gone is fine only when the merged id leads to the target.") with
            {
                Exclude = ["last_error"], ExcludeReason = "the retry status of the interrupted step",
                Settled = (context, row) => EntryIds.Id(row["duplicate_entry_id"]) is { } duplicate && EntryIds.Id(row["target_entry_id"]) is { } target
                    && context.After.Exists(target) && context.After.Resolve(duplicate) == context.After.Resolve(target),
            },
        Whole("Pending splits (interrupted)", ParityGroup.Manual, "pending_entry_splits", ["id"], ["original_entry_id", "new_entry_id"],
            "Chapter moves of splits a 0.4.x stop interrupted; gone is fine only when both entries exist afterwards.") with
            {
                Exclude = ["last_error"], ExcludeReason = "the retry status of the interrupted step",
                Settled = (context, row) => EntryIds.Id(row["original_entry_id"]) is { } original && EntryIds.Id(row["new_entry_id"]) is { } created
                    && context.After.Exists(original) && context.After.Exists(created) && context.After.Resolve(original) != context.After.Resolve(created),
            },
        Whole("Manual match groups", ParityGroup.Manual, "manual_match_groups", ["id"], [], "Groups of records you put together."),
        Whole("Manual match bindings", ParityGroup.Manual, "manual_match_bindings", ["source_id", "source_manga_id"], ["entry_id_hint"], "Records bound to a manual group, the record and the entry they point at."),
        Whole("Separate entries (records kept apart)", ParityGroup.Manual, "manual_separate_records", ["target_entry_id", "source_id", "source_manga_id"],
            ["target_entry_id", "separate_entry_id_hint"], "Records you said are not this entry; the record must not be attached to it.") with
            { KeepOut = true, Check = ParityCheck.SeparateNotAttached },
        Whole("Duplicate reviews (different works / same work)", ParityGroup.Manual, "duplicate_reviews", ["left_entry_id", "right_entry_id"], ["left_entry_id", "right_entry_id"],
            "Rejected matches: pairs you reviewed as different works (they must stay two entries) or as the same.") with { KeepOut = true, Check = ParityCheck.DistinctPair },
        Whole("Mixed-entry reviews", ParityGroup.Manual, "mixed_entry_reviews", ["fingerprint"], ["entry_id_hint"], "Mixed entries you reviewed as fine."),
        new("Tag reviews", ParityGroup.Manual, false, "tag_reviews",
            "SELECT l.namespace||':'||l.normalized AS left_tag,r.namespace||':'||r.normalized AS right_tag,t.decision FROM tag_reviews t JOIN tags l ON l.id=t.left_id JOIN tags r ON r.id=t.right_id",
            ["left_tag", "right_tag"], [], "Tag pairs you reviewed as same or different.") { ExcludeReason = "tag ids are compared through the tag names they stand for" },
        new("Manual or locked identity links", ParityGroup.Manual, false, "identity_links",
            "SELECT entry_id,provider,provider_name,external_id,external_url,identity_scope,confidence,match_type,locked FROM identity_links WHERE locked=1 OR match_type<>'auto'",
            ["entry_id", "provider", "external_id"], ["entry_id"], "Provider identities (MangaDex, AniList, ...) you set or locked.")
            { ExcludeReason = "provider metadata (title, description, cover, status, creators) is refreshed from the provider; not a decision" },
        Whole("Portable identity reviews", ParityGroup.Manual, "portable_identity_reviews", ["entry_id", "reference"], ["entry_id"], "Shared-catalogue identity reviews."),
        Whole("Deleted source records (kept out)", ParityGroup.Manual, "deleted_source_records", ["source_id", "source_manga_id"], [], "Records you deleted; they are not added again.") with { KeepOut = true },
        Whole("Shared-catalogue record bindings", ParityGroup.Manual, "catalogue_record_bindings", ["source_id", "source_manga_id", "reference"], ["entry_id"], "Records bound by an imported shared catalogue."),
        Whole("Shared-catalogue decisions", ParityGroup.Manual, "catalogue_decisions", ["uid", "digest"], [], "Decisions taken while combining shared catalogues."),
        Whole("Unfinished shared-catalogue import", ParityGroup.Manual, "catalogue_exchange_stages", ["id"], [], "A shared-catalogue import you staged but did not finish."),
        Whole("Source choices", ParityGroup.Manual, "source_config", ["source_id"], [], "Which sources are enabled, their priority, group and your official/unofficial choice.") with
            { Exclude = ["name", "display_name", "lang", "base_url", "icon_url", "supports_latest", "available", "official"], ExcludeReason = "facts read from the installed extension, refreshed from it" },
        Whole("Source groups", ParityGroup.Manual, "source_groups", ["id"], [], "Your source groups."),
        Whole("Source health and repair state", ParityGroup.Manual, "source_health", ["source_id"], [], "Health state, repair job and repair history per source.") with
            { KeepTimestamps = ["last_repaired_at", "last_success_at"] },
        Whole("Gallery policies", ParityGroup.Manual, "gallery_policies", ["source_id"], [], "Per-source gallery family rules you set.") with
            { Exclude = ["layout_revision", "layout_result_json"], ExcludeReason = "the computed layout result of the rules, recomputed" },
        Whole("Matching policies", ParityGroup.Manual, "matching_policies", ["scope", "subject", "direction"], [], "Per-source and per-group matching rules."),
        Whole("Matching presets", ParityGroup.Manual, "matching_presets", ["id"], [], "Saved matching presets."),
        Whole("Review workbench actions", ParityGroup.Manual, "matching_review_actions", ["id"], [], "Your review workbench actions and their undo state.") with { KeepTimestamps = ["undone_at"] },
        Whole("Serving profiles", ParityGroup.Manual, "serving_profiles", ["entry_id"], ["entry_id"], "Per-entry chapter serving preferences."),
        Whole("Serving upgrade policies", ParityGroup.Manual, "serving_upgrade_policies", ["entry_id"], ["entry_id"], "Per-entry quality upgrade policies."),
        Whole("UI preferences", ParityGroup.Manual, "user_ui_preferences", ["user_key"], [], "Saved Web UI preferences per user."),
        new("App state in the settings table", ParityGroup.Manual, false, "settings",
            "SELECT key,value FROM settings WHERE key NOT IN (" + string.Join(",", VolatileSettings.Select(key => "'" + key + "'")) + ")", ["key"], [],
            "Every settings-table row (source health days, human-attention and solver gates, one-time upgrade markers); caches excluded.")
            {
                ExcludeReason = "caches excluded: " + string.Join(", ", VolatileSettings),
                // A one-time marker may only move to the value 0.5.0 stores when it ran that step.
                Equivalent = (key, column, before, after) => column == "value" && key.EndsWith("_version", StringComparison.Ordinal)
                    && ExpectedVersions.TryGetValue(key, out var expected) && after == expected,
            },

        Whole("Import runs", ParityGroup.Import, "import_runs", ["id"], [], "Your backup imports."),
        Whole("Import decisions (items)", ParityGroup.Import, "import_items", ["id"], ["entry_id", "reference_entry_id"], "Each imported title, what was decided for it and its suggestion."),
        Whole("Import rejections", ParityGroup.Import, "import_match_rejections", ["backup_key", "signature"], [], "Import matches you rejected.") with { KeepOut = true },
        Whole("Manual import auto-resolve memory", ParityGroup.Import, "import_manual_autoresolve", ["backup_key"], ["target_entry_id_hint"], "Manual import choices remembered for the next import."),
        Whole("Manual import auto-resolve state", ParityGroup.Import, "import_manual_autoresolve_state", ["key"], [], "State of the remembered import choices."),

        new("Served chapter origins", ParityGroup.Provenance, true, "served_origin",
            "SELECT vc.entry_id AS entry_id,vc.chapter_key AS chapter_key,s.reason AS reason," + Candidate("cc")
            + " FROM served_origin s LEFT JOIN virtual_chapters vc ON vc.id=s.virtual_chapter_id LEFT JOIN chapter_candidates cc ON cc.id=s.candidate_id",
            ["entry_id", "chapter_key"], ["entry_id"], "Which source chapter serves each chapter (entry, chapter key and source chapter, resolved through ids).")
            { ExcludeReason = "virtual_chapter_id and candidate_id are compared through the chapter and source chapter they point at" },
        Whole("Chapter candidate rejections", ParityGroup.Provenance, "chapter_candidate_rejections", ["source_id", "source_chapter_id"], [], "Chapter versions you rejected.", origins: true) with { KeepOut = true },
        new("Origin history", ParityGroup.Provenance, true, "origin_history",
            "SELECT h.id AS id,vc.entry_id AS chapter_entry_id,vc.chapter_key AS chapter_key," + Candidate("cc")
            + ",h.source_id AS source_id,h.source_name AS source_name,h.source_manga_id AS source_manga_id,h.source_chapter_id AS source_chapter_id,h.served_at AS served_at,h.reason AS reason"
            + " FROM origin_history h LEFT JOIN virtual_chapters vc ON vc.id=h.virtual_chapter_id LEFT JOIN chapter_candidates cc ON cc.id=h.candidate_id",
            ["id"], ["chapter_entry_id"], "Every earlier origin change, resolved to its chapter and source chapter.")
            { KeepTimestamps = ["served_at"], ExcludeReason = "virtual_chapter_id and candidate_id are compared through what they point at" },
        new("Serving choices", ParityGroup.Provenance, false, "serving_choices",
            "SELECT vc.entry_id AS chapter_entry_id,vc.chapter_key AS chapter_key,s.entry_id AS entry_id,s.revision AS revision," + Candidate("cc")
            + " FROM serving_choices s LEFT JOIN o.virtual_chapters vc ON vc.id=s.virtual_chapter_id LEFT JOIN o.chapter_candidates cc ON cc.id=s.candidate_id",
            ["chapter_entry_id", "chapter_key"], ["chapter_entry_id", "entry_id"], "Chapter versions you chose to serve, resolved to chapter and source chapter.")
            { NeedsOrigins = true, ExcludeReason = "virtual_chapter_id and candidate_id are compared through what they point at" },
        new("Serving upgrade history", ParityGroup.History, false, "serving_upgrade_history",
            "SELECT u.id AS id,u.entry_id AS entry_id,vc.entry_id AS chapter_entry_id,vc.chapter_key AS chapter_key,"
            + "oc.source_id||':'||oc.source_manga_id||':'||oc.source_chapter_id AS old_candidate,nc.source_id||':'||nc.source_manga_id||':'||nc.source_chapter_id AS candidate,u.changed_at AS changed_at"
            + " FROM serving_upgrade_history u LEFT JOIN o.virtual_chapters vc ON vc.id=u.virtual_chapter_id LEFT JOIN o.chapter_candidates oc ON oc.id=u.old_candidate_id LEFT JOIN o.chapter_candidates nc ON nc.id=u.candidate_id",
            ["id"], [], "Earlier serving upgrades, resolved to chapter and source chapters.") { Raw = true, NeedsOrigins = true },

        History("display_metadata_history", "Earlier display-metadata versions."),
        History("duplicate_review_history", "Every earlier duplicate review."),
        History("duplicate_split_history", "Every earlier split."),
        History("match_history", "Every earlier match change and merge."),
        History("entry_preference_history", "Preferences of merged entries."),
        History("portable_identity_review_history", "Earlier identity reviews."),
        History("serving_policy_history", "Earlier serving policies."),
        History("deleted_entries_history", "Entries you deleted."),
        History("automatic_merge_events", "Automatic merges with their undo snapshots."),
        new("History: automatic_merge_lineage", ParityGroup.History, false, "automatic_merge_lineage", "SELECT * FROM automatic_merge_lineage",
            ["canonical_entry_id", "member_entry_id"], [], "Members of automatic merges.") { Raw = true },
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

    private sealed record Row(string Key, Dictionary<string, string?> Values, string Title, Dictionary<string, string?> Raw);

    private static string Show(IReadOnlyList<string> columns, Dictionary<string, string?> values) =>
        string.Join("; ", columns.Select(column => column + "=" + (values.TryGetValue(column, out var value) ? value ?? "NULL" : "(absent)")));

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
            if (kind.NeedsOrigins) { EnsureAttached(original); EnsureAttached(migrated); }
            List<Row> before;
            try { before = Rows(o, kind, id => context.Before.Exists(id) ? context.Before.Resolve(id).ToString(CultureInfo.InvariantCulture) : MissingEntry, context.Before.Title); }
            catch (SqliteException error)
            {
                // An older 0.4.x without a column this kind reads: fine only while the table is empty.
                var rows = o.Count(kind.Table);
                var result = new ParityKindResult { Kind = kind, Compared = false, Note = "not comparable with this 0.4.x schema (" + error.Message + ")", Original = (int)rows };
                if (rows > 0) result.Missing.Add(new ParityItem(kind.Table, "", rows.ToString(CultureInfo.InvariantCulture) + " rows that cannot be compared", null));
                results.Add(result);
                continue;
            }
            var compared = new ParityKindResult { Kind = kind, Compared = true, Original = before.Count };
            var columns = before.FirstOrDefault()?.Values.Keys.ToList() ?? [];
            compared.Columns = columns;
            List<Row> after;
            try { after = m.HasTable(kind.Table) ? Rows(m, kind, id => context.After.Exists(id) ? context.After.Resolve(id).ToString(CultureInfo.InvariantCulture) : MissingEntry, _ => "") : []; }
            catch (SqliteException error) { after = []; compared.Note = "the migrated file cannot be read for this kind (" + error.Message + ")"; }
            compared.Migrated = after.Count;
            var now = after.GroupBy(row => row.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            bool Same(Row left, Row right) => columns.All(column =>
            {
                var a = left.Values.GetValueOrDefault(column);
                if (!right.Values.TryGetValue(column, out var b)) return false;
                return string.Equals(a, b, StringComparison.Ordinal) || kind.Equivalent?.Invoke(left.Key, column, a, b) == true;
            });
            var beforeKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in before.GroupBy(row => row.Key, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                beforeKeys.Add(group.Key);
                if (!now.TryGetValue(group.Key, out var remaining))
                {
                    foreach (var row in group)
                        if (kind.Settled?.Invoke(context, row.Raw) != true) compared.Missing.Add(new ParityItem(row.Key, row.Title, Show(columns, row.Values), null));
                    continue;
                }
                remaining = [.. remaining];
                var unmatched = new List<Row>();
                foreach (var row in group)
                {
                    var index = remaining.FindIndex(candidate => Same(row, candidate));
                    if (index >= 0) remaining.RemoveAt(index); else unmatched.Add(row);
                }
                foreach (var row in unmatched)
                {
                    // A changed row consumes one leftover migrated row of the same key (it is the same row, altered).
                    var counterpart = remaining.FirstOrDefault();
                    if (counterpart is not null) remaining.RemoveAt(0);
                    compared.Changed.Add(new ParityItem(row.Key, row.Title, Show(columns, row.Values), counterpart is not null ? Show(columns, counterpart.Values)
                        : "(fewer rows: " + group.Count().ToString(CultureInfo.InvariantCulture) + " 0.4.x rows share this key after the migration)"));
                }
                if (kind.KeepOut)
                    foreach (var extra in remaining) compared.Added.Add(new ParityItem(extra.Key, extra.Title, null, Show(columns, extra.Values)));
            }
            if (kind.KeepOut)
                foreach (var extra in after.Where(row => !beforeKeys.Contains(row.Key)))
                    compared.Added.Add(new ParityItem(extra.Key, extra.Title, null, Show(columns.Count > 0 ? columns : extra.Values.Keys.ToList(), extra.Values)));
            foreach (var row in before) Verify(kind, context, row, compared);
            results.Add(compared);
        }
        return results;
    }

    private static void EnsureAttached((LegacyDatabase Catalog, LegacyDatabase Origins) pair)
    {
        try { pair.Catalog.Query("SELECT 1 FROM o.sqlite_master LIMIT 1"); }
        catch (SqliteException) { pair.Catalog.Attach(pair.Origins.Path, "o"); }
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
                if (context.Before.Resolve(left) == context.Before.Resolve(right))
                    result.Preexisting.Add(new ParityItem(row.Key, row.Title, "both entries were already one entry in 0.4.x", null));
                else if (context.After.Resolve(left) == context.After.Resolve(right))
                    result.Changed.Add(new ParityItem(row.Key, row.Title, "two entries", "VIOLATED: both entries are one entry in 0.5.0"));
                return;
            }
            case ParityCheck.SeparateNotAttached:
            {
                if (EntryIds.Id(row.Raw.GetValueOrDefault("target_entry_id")) is not { } target) return;
                var source = row.Raw.GetValueOrDefault("source_id"); var manga = row.Raw.GetValueOrDefault("source_manga_id");
                var before = Owners(context.OriginalCatalog, source, manga).Select(context.Before.Resolve).Contains(context.Before.Resolve(target));
                var after = Owners(context.MigratedCatalog, source, manga).Select(context.After.Resolve).Contains(context.After.Resolve(target));
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
        foreach (var source in raw)
        {
            var row = new Dictionary<string, string?>(source, StringComparer.Ordinal);
            kind.Derive?.Invoke(row);
            string? Cell(string column)
            {
                var value = row.GetValueOrDefault(column);
                return !kind.Raw && kind.Entries.Contains(column, StringComparer.Ordinal) && EntryIds.Id(value) is { } id ? entry(id) : value;
            }
            var keyParts = kind.Key.Select(column => Cell(column) ?? "NULL").ToArray();
            if (kind.Check == ParityCheck.DistinctPair && keyParts.Length >= 2 && string.CompareOrdinal(keyParts[0], keyParts[1]) > 0) (keyParts[0], keyParts[1]) = (keyParts[1], keyParts[0]);
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var column in row.Keys)
            {
                if (kind.Key.Contains(column, StringComparer.Ordinal) || kind.Exclude.Contains(column, StringComparer.Ordinal)) continue;
                if (!kind.Raw && ParityKind.IsTimestamp(column) && !kind.KeepTimestamps.Contains(column, StringComparer.Ordinal)) continue;
                values[column] = Cell(column);
            }
            var label = "";
            foreach (var column in kind.Entries)
                if (row.TryGetValue(column, out var cell) && EntryIds.Id(cell) is { } id && title(id) is { Length: > 0 } found) { label = found; break; }
            rows.Add(new Row(string.Join(" / ", keyParts), values, label, row));
        }
        return rows;
    }

    /// <summary>Every 0.4.x entry id that appears in a subscription or manual kind (resolved through the 0.4.x aliases).</summary>
    public static HashSet<long> DecisionEntries(LegacyDatabase catalog, LegacyDatabase origins, IEnumerable<ParityKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(origins);
        var ids = new EntryIds(catalog); var result = new HashSet<long>();
        foreach (var kind in kinds.Where(kind => kind.Group != ParityGroup.History && kind.Entries.Length > 0 && !kind.Raw))
        {
            var database = kind.Origins ? origins : catalog;
            if (!database.HasTable(kind.Table)) continue;
            if (kind.NeedsOrigins) EnsureAttached((catalog, origins));
            try
            {
                foreach (var row in Rows(database, kind, id => id.ToString(CultureInfo.InvariantCulture), _ => ""))
                    foreach (var column in kind.Entries)
                        if (EntryIds.Id(row.Raw.GetValueOrDefault(column)) is { } id && ids.Exists(id)) result.Add(ids.Resolve(id));
            }
            catch (SqliteException) { }
        }
        return result;
    }

    /// <summary>
    /// Merges the migration made: every 0.4.x entry that resolves to another entry after the migration than before. Such
    /// a merge fails the verdict when it involves an entry with a subscription or manual decision, or when its stored
    /// merge snapshot does not hold under the current rules (<paramref name="failingRecheck"/>); otherwise it is listed.
    /// </summary>
    public static List<NewMerge> NewMerges(LegacyDatabase original, LegacyDatabase migrated, IReadOnlySet<long> decisionEntries, IReadOnlySet<long> failingRecheck)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(migrated);
        var before = new EntryIds(original); var after = new EntryIds(migrated);
        var reasons = migrated.HasTable("entry_aliases")
            ? migrated.Query("SELECT old_entry_id,reason FROM entry_aliases").Where(row => EntryIds.Id(row["old_entry_id"]) is not null)
                .ToDictionary(row => EntryIds.Id(row["old_entry_id"])!.Value, row => row["reason"] ?? "")
            : [];
        var merges = new List<NewMerge>();
        foreach (var id in before.AllIds.Where(before.Exists).Select(before.Resolve).Distinct().Order())
        {
            var into = after.Resolve(id);
            if (into == id) continue;
            var involved = decisionEntries.Contains(id) || decisionEntries.Contains(into);
            var failing = failingRecheck.Contains(id);
            var why = involved ? "it involves an entry with a subscription or manual decision" : failing ? "its merge does not hold under the current rules" : "informational";
            merges.Add(new NewMerge(id, into, before.Title(id), reasons.GetValueOrDefault(id, ""), involved || failing, why));
        }
        return merges;
    }

    /// <summary>0.4.x entries that had at least one active source record and have none after the migration (rebuilding them needs the network).</summary>
    public static List<long> EmptiedEntries(LegacyDatabase original, LegacyDatabase migrated)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(migrated);
        static Dictionary<long, int> Active(LegacyDatabase database, EntryIds ids)
        {
            var counts = new Dictionary<long, int>();
            foreach (var table in new[] { "source_matches", "source_variants" }.Where(database.HasTable))
                foreach (var row in database.Query("SELECT entry_id FROM " + table + " WHERE active=1"))
                    if (EntryIds.Id(row["entry_id"]) is { } id) counts[ids.Resolve(id)] = counts.GetValueOrDefault(ids.Resolve(id)) + 1;
            return counts;
        }
        var before = new EntryIds(original); var after = new EntryIds(migrated);
        var had = Active(original, before); var has = Active(migrated, after);
        return had.Keys.Where(id => !has.ContainsKey(after.Resolve(id))).Order().ToList();
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

    /// <summary>
    /// Every 0.4.x entry (resolved through the 0.4.x aliases) that does not exist after the migration, neither itself nor
    /// as an alias. Entries the migration merged are listed by <see cref="NewMerges"/>, not here.
    /// </summary>
    public static List<long> UnresolvedEntries(LegacyDatabase original, LegacyDatabase migrated)
    {
        var before = new EntryIds(original); var after = new EntryIds(migrated);
        return before.AllIds.Where(before.Exists).Select(before.Resolve).Distinct().Where(id => !after.Exists(id)).Order().ToList();
    }
}
