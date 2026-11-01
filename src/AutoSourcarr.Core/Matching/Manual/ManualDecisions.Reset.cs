using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Matching.Manual.ManualSql;

namespace AutoSourcarr.Core.Matching.Manual;

/// <summary>What an explicit manual reset forgets: every decision (manual_reset.py), only the groups, or the bindings and groups.</summary>
public enum ManualResetScope { All, Groups, Matches }

/// <summary>The preview changed since it was shown (0.4.5 StaleManualPreview; the routes answer 409).</summary>
public sealed class StaleManualPreviewException(string message) : InvalidOperationException(message);

public sealed partial class ManualDecisions
{
    private static readonly (string Key, string Label)[] AllTables =
    [
        ("manual_match_bindings", "Manual source matches"), ("manual_match_groups", "Manual match groups"),
        ("preferred_title_overrides", "Preferred titles"), ("manual_title_scopes", "Title scopes"),
        ("preferred_title_override_sources", "Legacy title overrides"), ("manual_separate_records", "Separate-entry rules"),
        ("duplicate_reviews", "Duplicate decisions"), ("mixed_entry_reviews", "Mixed-entry reviews"),
        ("tag_reviews", "Tag matches and separations"), ("import_match_rejections", "Rejected import matches"),
        ("import_manual_autoresolve", "Learned import matches"), ("deleted_source_records", "Deleted-source suppression"),
    ];

    private static readonly (string Key, string Sql, string Label)[] AllExtra =
    [
        ("manual_aliases", "SELECT * FROM titles WHERE title_type='manual'", "Manual aliases"),
        ("title_flags", "SELECT id,preferred_title FROM entries WHERE manual_preferred_title=1", "Manually named entries"),
        ("skipped_imports", "SELECT id,import_id,decision_revision FROM import_items WHERE state='skipped'", "Skipped imports returned to unresolved"),
        ("locked_records", LockedRecords, "Manual source locks and splits"),
    ];

    private const string LockedRecords = """
        SELECT 'match' AS kind,entry_id,source_id,source_manga_id,match_type,locked FROM source_matches WHERE locked=1 OR lower(match_type) IN ('manual','split-duplicate')
        UNION ALL SELECT 'variant',entry_id,source_id,source_manga_id,match_type,locked FROM source_variants WHERE locked=1 OR lower(match_type) IN ('manual','split-duplicate')
        """;

    private const string RejectedChapters = "SELECT * FROM chapter_candidate_rejections";

    private static IEnumerable<(string Key, string Sql, string Label)> CatalogQueries(ManualResetScope scope) => scope switch
    {
        ManualResetScope.Groups =>
        [
            ("manual_match_groups", "SELECT * FROM manual_match_groups", "Manual match groups"),
            ("grouped_bindings", "SELECT source_id,source_manga_id,group_id,grouping_suppressed FROM manual_match_bindings WHERE group_id IS NOT NULL OR grouping_suppressed=0", "Grouped manual matches"),
        ],
        ManualResetScope.Matches =>
        [
            ("manual_match_bindings", "SELECT * FROM manual_match_bindings", "Manual source matches"),
            ("manual_match_groups", "SELECT * FROM manual_match_groups", "Manual match groups"),
            ("manual_records", """
                SELECT 'match' AS kind,entry_id,source_id,source_manga_id,match_type FROM source_matches WHERE lower(match_type)='manual'
                UNION ALL SELECT 'variant',entry_id,source_id,source_manga_id,match_type FROM source_variants WHERE lower(match_type)='manual'
                """, "Manual source locks"),
        ],
        _ => AllTables.Select(table => (Key: table.Key, Sql: "SELECT * FROM " + table.Key, Label: table.Label)).Concat(AllExtra),
    };

    /// <summary>
    /// The preview of an explicit reset: what would be forgotten (counts per kind) and a <c>preview_token</c> (the
    /// content revision) the confirmation must send back, so a reset never forgets decisions the user has not seen.
    /// </summary>
    public JsonObject PreviewReset(ManualResetScope scope)
    {
        var rejected = scope == ManualResetScope.All ? _database.Read(DatabaseFile.Origins, o => Digest(o, RejectedChapters)) : default;
        return Read(c => Snapshot(c, scope, rejected));
    }

    private static (int Count, string[] Rows) Digest(DatabaseSession c, string sql)
    {
        // Each row is digested on its own and the digests are sorted, so row order and layout do not matter.
        var rows = c.Query(sql).Select(RowDigest).ToList();
        rows.Sort(StringComparer.Ordinal);
        return (rows.Count, rows.ToArray());
    }

    private static string RowDigest(DatabaseRow row) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row.Values.Select(Cell).ToArray()))));

    private static string? Cell(object? value) => value switch
    {
        null => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static JsonObject Snapshot(DatabaseSession c, ManualResetScope scope, (int Count, string[] Rows) rejected)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(Encoding.UTF8.GetBytes(scope.ToString()));
        var counts = new JsonArray(); var total = 0;
        void Add(string key, string label, (int Count, string[] Rows) part)
        {
            digest.AppendData(Encoding.UTF8.GetBytes(key));
            foreach (var row in part.Rows) digest.AppendData(Encoding.UTF8.GetBytes(row));
            counts.Add(new JsonObject { ["key"] = key, ["label"] = label, ["count"] = part.Count });
            total += part.Count;
        }
        foreach (var (key, sql, label) in CatalogQueries(scope)) Add(key, label, Digest(c, sql));
        if (scope == ManualResetScope.All) Add("reset_origins.chapter_candidate_rejections", "Rejected chapters", rejected);
        var revision = Convert.ToHexStringLower(digest.GetHashAndReset());
        return new JsonObject
        {
            ["scope"] = scope.ToString().ToLowerInvariant(), ["revision"] = revision, ["preview_token"] = revision,
            ["counts"] = counts, ["total_records"] = total,
            ["retained"] = scope == ManualResetScope.All
                ? "Library subscriptions, current entries and source ownership, completed merges/splits, added/existing imports, source settings, downloads and history stay intact. This forgets decisions; it does not undo completed library operations."
                : "Current entries and their attached source records stay as they are.",
            ["effect"] = scope switch
            {
                ManualResetScope.Groups => "Manual groups are dissolved; their matches stay attached but are no longer kept together after a rebuild.",
                ManualResetScope.Matches => "Manual matches are forgotten: their records become automatic and a database reset will no longer restore them.",
                _ => "Automatic matching can reconsider these records. Skipped imports return to Unresolved; they are not automatically added. Deleted source records may be rediscovered.",
            },
        };
    }

    /// <summary>
    /// Applies an explicit reset after the user confirmed its preview. Refused with <see cref="StaleManualPreviewException"/>
    /// when the decisions changed since the preview (a new preview must be confirmed). Never called automatically.
    /// </summary>
    public JsonObject Reset(ManualResetScope scope, string previewToken, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(previewToken)) throw new StaleManualPreviewException("Review the reset preview and confirm it before resetting.");
        return scope switch
        {
            ManualResetScope.Groups => ResetGroups(previewToken.Trim(), actor),
            ManualResetScope.Matches => ResetMatches(previewToken.Trim(), actor),
            _ => ResetAll(previewToken.Trim(), actor),
        };
    }

    private static void RequireCurrent(JsonObject before, string token)
    {
        if (!string.Equals(before["revision"]!.GetValue<string>(), token, StringComparison.Ordinal))
            throw new StaleManualPreviewException("Manual decisions changed. Review a new reset preview before confirming.");
    }

    /// <summary>reset_manual_groups: dissolves every manual group; bindings stay and are not regrouped.</summary>
    private JsonObject ResetGroups(string token, DecisionActor actor)
    {
        lock (_gate) return ResetGroupsLocked(token, actor);
    }

    private JsonObject ResetGroupsLocked(string token, DecisionActor actor)
    {
        var result = Write(c =>
        {
            var before = Snapshot(c, ManualResetScope.Groups, default);
            RequireCurrent(before, token);
            var groups = Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM manual_match_groups"), CultureInfo.InvariantCulture);
            c.Execute("UPDATE manual_match_bindings SET group_id=NULL,grouping_suppressed=1,updated_at=$p0", P(Stamp()));
            c.Execute("DELETE FROM manual_match_groups");
            Log("reset.manual.groups", actor, [], [], "Manual groups reset", new JsonObject { ["counts"] = before["counts"]!.DeepClone() });
            return new JsonObject { ["reset"] = true, ["groups_removed"] = groups };
        });
        _operations?.Invalidate([]);
        return result;
    }

    /// <summary>reset_manual_matches: forgets bindings and groups; the records stay attached as automatic ones.</summary>
    private JsonObject ResetMatches(string token, DecisionActor actor)
    {
        lock (_gate) return ResetMatchesLocked(token, actor);
    }

    private JsonObject ResetMatchesLocked(string token, DecisionActor actor)
    {
        var result = Write(c =>
        {
            var before = Snapshot(c, ManualResetScope.Matches, default);
            RequireCurrent(before, token);
            var count = Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM manual_match_bindings"), CultureInfo.InvariantCulture);
            foreach (var table in new[] { "source_matches", "source_variants" })
                c.Execute($"""
                    UPDATE {table} SET match_type='auto',locked=0
                    WHERE lower(match_type)='manual' OR EXISTS(SELECT 1 FROM manual_match_bindings mb WHERE mb.source_id={table}.source_id AND mb.source_manga_id={table}.source_manga_id)
                    """);
            c.Execute("DELETE FROM manual_match_bindings");
            c.Execute("DELETE FROM manual_match_groups");
            Log("reset.manual.matches", actor, [], [], "Manual matches reset", new JsonObject { ["counts"] = before["counts"]!.DeepClone() });
            return new JsonObject { ["reset"] = true, ["manual_matches_removed"] = count };
        });
        _operations?.Invalidate([]);
        return result;
    }

    /// <summary>manual_reset.reset: forgets every user decision while keeping current entries, ownership and history.</summary>
    private JsonObject ResetAll(string token, DecisionActor actor)
    {
        lock (_gate)
        {
            var rejected = _database.Read(DatabaseFile.Origins, o => Digest(o, RejectedChapters));
            // Rejected chapter candidates still owned by an entry that has the record attached become active again.
            var candidates = _database.Read(DatabaseFile.Origins, o => o.Query("""
                SELECT DISTINCT cc.id,vc.entry_id,cc.source_id,cc.source_manga_id FROM chapter_candidates cc
                JOIN chapter_candidate_rejections r ON r.source_id=cc.source_id AND r.source_chapter_id=cc.source_chapter_id
                JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id
                """));
            var (result, titleEntries) = Write(c =>
            {
                var before = Snapshot(c, ManualResetScope.All, rejected);
                RequireCurrent(before, token);
                var titles = c.QueryAs<long>("""
                    SELECT id FROM entries WHERE manual_preferred_title=1 UNION SELECT entry_id FROM preferred_title_overrides
                    UNION SELECT entry_id FROM manual_title_scopes UNION SELECT entry_id FROM titles WHERE title_type='manual'
                    """).ToArray();
                var stamp = Stamp();
                foreach (var table in new[] { "source_matches", "source_variants" })
                    c.Execute($"""
                        UPDATE {table} SET match_type='auto',locked=0 WHERE locked=1 OR lower(match_type) IN ('manual','split-duplicate')
                        OR (match_type='separate-anchor' AND EXISTS(SELECT 1 FROM manual_separate_records r WHERE r.source_id={table}.source_id AND r.source_manga_id={table}.source_manga_id))
                        """);
                foreach (var (table, _) in AllTables) c.Execute("DELETE FROM " + table);
                c.Execute("DELETE FROM titles WHERE title_type='manual'");
                c.Execute("UPDATE entries SET manual_preferred_title=0 WHERE manual_preferred_title=1");
                c.Execute("INSERT INTO import_manual_autoresolve_state(key,value) VALUES('legacy_history_learning_disabled','1') ON CONFLICT(key) DO UPDATE SET value='1'");
                c.Execute("UPDATE import_items SET candidate_ids_json='[]',suggestion_checked=0,suggestion_state='queued',decision_revision=decision_revision+1,updated_at=$p0 WHERE state IN ('unresolved','failed')", P(stamp));
                c.Execute("""
                    UPDATE import_items SET state='unresolved',reason='Manual skip reset; review this entry',entry_id=NULL,candidate_ids_json='[]',matched_snapshot_json='{}',
                      suggestion_checked=0,suggestion_state='queued',decision_revision=decision_revision+1,updated_at=$p0 WHERE state='skipped'
                    """, P(stamp));
                Log("reset.manual", actor, [], [], "All manual decisions reset", new JsonObject { ["counts"] = before["counts"]!.DeepClone() });
                return (new JsonObject { ["reset"] = true, ["cleared"] = before["counts"]!.DeepClone(), ["retained"] = before["retained"]!.DeepClone() }, titles);
            });
            var active = Read(c => candidates.Where(row => c.Scalar("""
                SELECT 1 FROM source_matches WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2 AND active=1
                UNION ALL SELECT 1 FROM source_variants WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2 AND active=1 LIMIT 1
                """, P(row.Integer("entry_id"), row.Text("source_id"), row.Integer("source_manga_id"))) is not null).ToList());
            // The catalogue commit owns the decision; the origins file follows (a failure here keeps the rejections, never loses them).
            // The rejections are re-read and deleted in one origins write, and only the ones the confirmed preview showed:
            // a chapter rejected after the preview is kept.
            var previewed = rejected.Rows.GroupBy(row => row, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            _database.Write(DatabaseFile.Origins, o =>
            {
                o.Execute("UPDATE chapter_candidates SET active=1 WHERE id IN (SELECT value FROM json_each($p0))", P(JsonSerializer.Serialize(active.Select(row => row.Integer("id")).ToArray())));
                var removed = 0;
                foreach (var row in o.Query(RejectedChapters))
                {
                    var digest = RowDigest(row);
                    if (!previewed.TryGetValue(digest, out var left) || left == 0) continue;
                    previewed[digest] = left - 1;
                    removed += o.Execute("DELETE FROM chapter_candidate_rejections WHERE source_id=$p0 AND source_chapter_id=$p1", P(row.Text("source_id"), row.Integer("source_chapter_id")));
                }
                return removed;
            });
            if (_operations is { } operations)
            {
                foreach (var entry in titleEntries) { operations.SyncPreferredTitle(entry); operations.ChooseMetadata(entry); }
                foreach (var entry in candidates.Select(row => row.Integer("entry_id")).Distinct()) operations.ReconcileChapters(entry);
                operations.Invalidate([]);
            }
            return result;
        }
    }
}
