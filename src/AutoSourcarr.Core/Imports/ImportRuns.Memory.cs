using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Imports;

/// <summary>
/// The import decisions that outlive import history and catalogue resets: rejected candidates (import_match_rejections,
/// keyed by the backup's source family + source-relative URL and the rejected work's record signatures, never by a
/// transient entry id) and remembered manual matches (import_manual_autoresolve, anchored to one concrete source record).
/// </summary>
public sealed partial class ImportRuns
{
    /// <summary>_backup_rejection_key: "family|url:&lt;url&gt;" or "family|title:&lt;normalized title&gt;".</summary>
    internal string RejectionKey(JsonObject item)
    {
        var family = ImportQueries.RejectionSourceFamily(ExpectedSourceName(item));
        var url = ImportQueries.BackupUrlKey(Str(item, "url")).ToLowerInvariant();
        return url.Length > 0 ? family + "|url:" + url : family + "|title:" + TextMatcher.NormalizeTitle(Str(item, "title"));
    }

    /// <summary>_entry_import_rejection_signatures: source URL / manga / title signatures per source family of the entry's active records.</summary>
    internal HashSet<string> EntrySignatures(long entryId) => Read(c => EntrySignatures(c, CatalogueTitles.Canonical(c, entryId)));

    private static HashSet<string> EntrySignatures(DatabaseSession c, long entryId)
    {
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in c.Query("""
            SELECT x.source_id,x.source_manga_id,x.source_manga_url,x.source_title,COALESCE(NULLIF(sc.display_name,''),sc.name,x.source_id) source_name
            FROM (SELECT source_id,source_manga_id,source_manga_url,source_title FROM source_matches WHERE entry_id=$id AND active=1
                  UNION ALL SELECT source_id,source_manga_id,source_manga_url,source_title FROM source_variants WHERE entry_id=$id AND active=1) x
            LEFT JOIN source_config sc ON sc.source_id=x.source_id
            """, ("$id", entryId)))
        {
            var family = ImportQueries.RejectionSourceFamily(row.Text("source_name").Length > 0 ? row.Text("source_name") : row.Text("source_id"));
            if (family.Length == 0) continue;
            var url = ImportQueries.BackupUrlKey(row.Text("source_manga_url")).ToLowerInvariant();
            if (url.Length > 0) signatures.Add("source-url:" + family + ":" + url);
            signatures.Add("source-manga:" + family + ":" + Id(row.Integer("source_manga_id")));
            var title = TextMatcher.NormalizeTitle(row.Text("source_title"));
            if (title.Length > 0) signatures.Add("source-title:" + family + ":" + title);
        }
        if (signatures.Count == 0 && c.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", entryId)) is string preferred
            && TextMatcher.NormalizeTitle(preferred) is { Length: > 0 } key)
            signatures.Add("entry-title:" + key);
        return signatures;
    }

    private HashSet<string> RejectedSignatures(JsonObject item)
    {
        var key = RejectionKey(item);
        return Read(c => c.QueryAs<string>("SELECT signature FROM import_match_rejections WHERE backup_key=@key", new { key }).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>_is_import_entry_rejected.</summary>
    internal bool IsRejected(JsonObject item, long entryId)
    {
        var rejected = RejectedSignatures(item);
        return rejected.Count > 0 && rejected.Overlaps(EntrySignatures(entryId));
    }

    /// <summary>_filter_import_rejected_entries: canonical, distinct, rejected works left out.</summary>
    internal List<long> FilterRejected(JsonObject item, IEnumerable<long> entryIds)
    {
        var rejected = RejectedSignatures(item);
        return Read(c =>
        {
            var output = new List<long>();
            foreach (var id in entryIds)
            {
                var canonical = CatalogueTitles.Canonical(c, id);
                if (output.Contains(canonical)) continue;
                if (rejected.Count > 0 && rejected.Overlaps(EntrySignatures(c, canonical))) continue;
                output.Add(canonical);
            }
            return output;
        });
    }

    /// <summary>_remember_import_rejection.</summary>
    internal JsonObject RememberRejection(JsonObject item, long entryId)
    {
        var key = RejectionKey(item); var sourceName = ExpectedSourceName(item);
        return Write(c =>
        {
            var canonical = CatalogueTitles.Canonical(c, entryId);
            var signatures = EntrySignatures(c, canonical);
            var title = c.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", canonical)) as string ?? "";
            if (title.Length == 0) title = "Entry " + Id(canonical);
            if (signatures.Count == 0 && TextMatcher.NormalizeTitle(title) is { Length: > 0 } titleKey) signatures.Add("entry-title:" + titleKey);
            foreach (var signature in signatures)
                c.Execute("""
                    INSERT INTO import_match_rejections(backup_key,signature,backup_title,backup_source,rejected_title,rejected_at)
                    VALUES($key,$signature,$title,$source,$rejected,$now)
                    ON CONFLICT(backup_key,signature) DO UPDATE SET backup_title=excluded.backup_title,backup_source=excluded.backup_source,
                      rejected_title=excluded.rejected_title,rejected_at=excluded.rejected_at
                    """, ("$key", key), ("$signature", signature), ("$title", Str(item, "title")), ("$source", sourceName), ("$rejected", title), ("$now", Now()));
            return new JsonObject { ["backup_key"] = key, ["signatures"] = signatures.Count, ["rejected_title"] = title, ["entry_id"] = canonical };
        });
    }

    /// <summary>_clear_import_rejection_for_entry: an explicit Match of this work overrides an earlier Reject of it.</summary>
    internal int ClearRejectionForEntry(JsonObject item, long entryId)
    {
        var signatures = EntrySignatures(entryId);
        if (signatures.Count == 0) return 0;
        var key = RejectionKey(item);
        return Write(c => c.Execute("DELETE FROM import_match_rejections WHERE backup_key=$key AND signature IN (SELECT value FROM json_each($signatures))",
            ("$key", key), ("$signatures", JsonSerializer.Serialize(signatures.Order(StringComparer.Ordinal).ToArray()))));
    }

    /// <summary>reset_import_match_rejections: forgets every import Reject; unresolved rows search again.</summary>
    public JsonObject ResetRejections(DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var result = Write(c =>
        {
            var count = Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM import_match_rejections"), CultureInfo.InvariantCulture);
            c.Execute("DELETE FROM import_match_rejections");
            var runs = c.QueryAs<string>("SELECT DISTINCT import_id FROM import_items WHERE state IN ('unresolved','failed')").ToArray();
            c.Execute("""
                UPDATE import_items SET candidate_ids_json='[]',suggestion_checked=0,suggestion_state='queued',updated_at=$now
                WHERE state IN ('unresolved','failed')
                """, ("$now", Now()));
            Log("reset.import.rejections", actor, [], "Import rejections forgotten", after: new JsonObject { ["rejections"] = count });
            return (Count: count, Runs: runs);
        });
        var queued = result.Runs.Count(run => ScheduleDiscovery(run, resumeOnly: false, origin: "manual") is not null);
        return new JsonObject
        {
            ["rejections_cleared"] = result.Count, ["import_ids"] = new JsonArray(result.Runs.Select(run => (JsonNode?)JsonValue.Create(run)).ToArray()),
            ["enrichment_tasks_queued"] = queued,
        };
    }

    /// <summary>
    /// _import_autoresolve_anchor: the record that represents the user's choice: the backup's own source when attached,
    /// then manual, locked, primary, initialized, higher-priority and richer records.
    /// </summary>
    private JsonObject? AutoresolveAnchor(DatabaseSession c, long entryId, string wantedSource)
    {
        // One query per record table (their column lists differ), the same projection for both.
        const string Columns = "r.source_id,r.source_manga_id,r.source_manga_url,r.source_title,r.thumbnail_url,r.description,r.author,r.artist,r.match_type,"
            + "r.locked,r.initialized,COALESCE(NULLIF(sc.display_name,''),sc.name,r.source_id) source_name,COALESCE(sc.priority,999999) source_priority";
        var rows = new[] { ("source_matches", "primary"), ("source_variants", "additional") }
            .SelectMany(table => c.Query("SELECT '" + table.Item2 + "' kind," + Columns + " FROM " + table.Item1
                + " r LEFT JOIN source_config sc ON sc.source_id=r.source_id WHERE r.entry_id=$id AND r.active=1", ("$id", entryId)))
            .ToList();
        var row = rows.OrderBy(r => wantedSource.Length > 0 && r.Text("source_id") == wantedSource ? 0 : 1)
            .ThenBy(r => r.Text("match_type").Equals("manual", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(r => r.Integer("locked") != 0 ? 0 : 1).ThenBy(r => r.Text("kind") == "primary" ? 0 : 1)
            .ThenBy(r => r.Integer("initialized") != 0 ? 0 : 1).ThenBy(r => r.Integer("source_priority", 999999))
            .ThenByDescending(r => r.Text("description").Length).FirstOrDefault();
        if (row is null) return null;
        var record = new JsonObject
        {
            ["id"] = row.Integer("source_manga_id"), ["title"] = row.Text("source_title"), ["url"] = row.Text("source_manga_url"),
            ["thumbnailUrl"] = row.Text("thumbnail_url"), ["description"] = row.Text("description"), ["author"] = row.Text("author"),
            ["artist"] = row.Text("artist"),
        };
        return new JsonObject
        {
            ["source_id"] = row.Text("source_id"), ["source_manga_id"] = row.Integer("source_manga_id"), ["source_name"] = row.Text("source_name"),
            ["source_title"] = row.Text("source_title"), ["record"] = record, ["table"] = row.Text("kind") == "primary" ? "source_matches" : "source_variants",
        };
    }

    /// <summary>
    /// _remember_import_manual_autoresolve: keeps the user's Match for this backup identity outside import history, anchored
    /// to a concrete source record that is also locked as a manual binding.
    /// </summary>
    internal JsonObject RememberManualAutoresolve(JsonObject item, long entryId, DecisionActor actor)
    {
        var wanted = CurrentSourceId(item);
        var key = RejectionKey(item); var sourceName = ExpectedSourceName(item);
        var (canonical, anchor, title) = Write(c =>
        {
            var id = CatalogueTitles.Canonical(c, entryId);
            var found = AutoresolveAnchor(c, id, wanted);
            if (found is not null)
            {
                var table = Str(found, "table") == "source_matches" ? "source_matches" : "source_variants";
                c.Execute("UPDATE " + table + " SET match_type='manual',locked=1,last_checked=$now WHERE entry_id=$id AND source_id=$source AND source_manga_id=$manga",
                    ("$now", Now()), ("$id", id), ("$source", Str(found, "source_id")), ("$manga", found["source_manga_id"]!.GetValue<long>()));
            }
            var preferred = c.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", id)) as string ?? "";
            return (id, found, preferred.Length > 0 ? preferred : found is null ? "Entry " + Id(id) : Str(found, "source_title"));
        });
        var sourceId = anchor is null ? "" : Str(anchor, "source_id");
        var mangaId = anchor?["source_manga_id"]?.GetValue<long>() ?? 0;
        var record = anchor?["record"] as JsonObject;
        if (anchor is not null && record is not null && Manual is { } manual && sourceId.Length > 0 && mangaId > 0)
            manual.PersistManualBinding(canonical, sourceId, (JsonObject)record.DeepClone(), actor, Str(anchor, "source_name"));
        Write(c => c.Execute("""
            INSERT INTO import_manual_autoresolve(backup_key,backup_title,backup_source,backup_source_id,backup_source_url,target_source_id,
              target_source_manga_id,target_source_name,target_title,target_entry_id_hint,target_record_json,created_at,updated_at)
            VALUES($key,$title,$source,$sourceId,$url,$target,$manga,$targetName,$targetTitle,$hint,$record,$now,$now)
            ON CONFLICT(backup_key) DO UPDATE SET backup_title=excluded.backup_title,backup_source=excluded.backup_source,
              backup_source_id=excluded.backup_source_id,backup_source_url=excluded.backup_source_url,target_source_id=excluded.target_source_id,
              target_source_manga_id=excluded.target_source_manga_id,target_source_name=excluded.target_source_name,target_title=excluded.target_title,
              target_entry_id_hint=excluded.target_entry_id_hint,target_record_json=excluded.target_record_json,updated_at=excluded.updated_at
            """, ("$key", key), ("$title", Str(item, "title")), ("$source", sourceName), ("$sourceId", Str(item, "source")), ("$url", Str(item, "url")),
            ("$target", sourceId), ("$manga", mangaId), ("$targetName", anchor is null ? "" : Str(anchor, "source_name")), ("$targetTitle", title),
            ("$hint", canonical), ("$record", record?.ToJsonString() ?? "{}"), ("$now", Now())));
        return new JsonObject
        {
            ["backup_key"] = key, ["entry_id"] = canonical, ["target_title"] = title, ["target_source_id"] = sourceId, ["target_source_manga_id"] = mangaId,
        };
    }

    /// <summary>
    /// _restore_import_autoresolve_target (no network): the current owner of the anchored record, else the hinted entry
    /// (re-binding the anchor), else a new entry recreated from the stored record, else one unique exact title.
    /// </summary>
    private long? RestoreAutoresolveTarget(DatabaseRow row, DecisionActor actor)
    {
        var sourceId = row.Text("target_source_id"); var mangaId = row.Integer("target_source_manga_id");
        if (sourceId.Length > 0 && mangaId > 0 && _ingest.FindExistingBySource(sourceId, mangaId) is { } owner) return _ingest.CanonicalEntryId(owner);
        JsonObject? payload = null;
        try { payload = JsonNode.Parse(row.Text("target_record_json", "{}")) as JsonObject; } catch (JsonException) { }
        if (payload is { Count: > 0 } && sourceId.Length > 0 && mangaId > 0)
        {
            payload["id"] = mangaId;
            if (Str(payload, "title").Trim().Length == 0) payload["title"] = row.Text("target_title").Length > 0 ? row.Text("target_title") : "Manual import " + Id(mangaId);
        }
        else payload = null;
        if (row["target_entry_id_hint"] is long hint && _ingest.EntryExists(hint))
        {
            var entryId = _ingest.CanonicalEntryId(hint);
            if (payload is not null) Rebind(entryId, sourceId, payload, row.Text("target_source_name"), actor);
            return entryId;
        }
        if (payload is not null)
        {
            // AUTOINCREMENT state survives a reset, so the recreated record never takes an unrelated work's old id.
            var entryId = _ingest.CreateEntry(payload, sourceId);
            Rebind(entryId, sourceId, payload, row.Text("target_source_name"), actor);
            Write(c => c.Execute("UPDATE import_manual_autoresolve SET target_entry_id_hint=$entry,updated_at=$now WHERE backup_key=$key",
                ("$entry", entryId), ("$now", Now()), ("$key", row.Text("backup_key"))));
            return entryId;
        }
        var targetTitle = row.Text("target_title").Trim();
        if (targetTitle.Length > 0 && _ingest.ExactSearchEntryIds(targetTitle, 20) is { Count: 1 } single) return _ingest.CanonicalEntryId(single[0]);
        return null;
    }

    private void Rebind(long entryId, string sourceId, JsonObject payload, string sourceName, DecisionActor actor)
    {
        _ingest.UpsertMatch(entryId, sourceId, payload, 1, "manual", true);
        Manual?.PersistManualBinding(entryId, sourceId, (JsonObject)payload.DeepClone(), actor, sourceName.Length > 0 ? sourceName : sourceId);
    }

    /// <summary>
    /// _learn_import_autoresolve_from_history: a pre-0.2.46 accepted row whose chosen entry was one of its review candidates
    /// was a manual Match; remember it (unless the learning was switched off by a reset).
    /// </summary>
    private bool LearnAutoresolveFromHistory(JsonObject item, DecisionActor actor)
    {
        var disabled = Read(c => c.Scalar("SELECT value FROM import_manual_autoresolve_state WHERE key='legacy_history_learning_disabled'") as string) ?? "";
        if (disabled.Trim().ToLowerInvariant() is "1" or "true" or "yes") return false;
        var key = RejectionKey(item);
        var url = Str(item, "url");
        var rows = Read(c => url.Length > 0
            ? c.Query("SELECT * FROM import_items WHERE state IN ('added','existing') AND entry_id IS NOT NULL AND source_url=$value ORDER BY updated_at DESC LIMIT 50", ("$value", url))
            : c.Query("SELECT * FROM import_items WHERE state IN ('added','existing') AND entry_id IS NOT NULL AND title=$value ORDER BY updated_at DESC LIMIT 50", ("$value", Str(item, "title"))));
        foreach (var old in rows)
        {
            if (RejectionKey(ItemPayload(old)) != key) continue;
            var entryId = old.Integer("entry_id");
            if (!CandidateIds(old).Contains(entryId) || !_ingest.EntryExists(entryId) || IsRejected(item, entryId)) continue;
            RememberManualAutoresolve(item, entryId, actor);
            return true;
        }
        return false;
    }

    /// <summary>_import_manual_autoresolve_entry: the remembered manual target of this backup identity (entry, reason).</summary>
    internal (long? EntryId, string Reason) ManualAutoresolveEntry(JsonObject item, DecisionActor actor)
    {
        var key = RejectionKey(item);
        DatabaseRow? Find() => Read(c => c.Query("SELECT * FROM import_manual_autoresolve WHERE backup_key=$key", ("$key", key)).FirstOrDefault());
        var row = Find();
        if (row is null && LearnAutoresolveFromHistory(item, actor)) row = Find();
        if (row is null) return (null, "");
        var entryId = RestoreAutoresolveTarget(row, actor);
        if (entryId is null) return (null, "manual import autoresolve target is not currently restorable");
        if (IsRejected(item, entryId.Value)) return (null, "manual import autoresolve target is rejected");
        Write(c => c.Execute("UPDATE import_manual_autoresolve SET target_entry_id_hint=$entry,updated_at=$now WHERE backup_key=$key",
            ("$entry", entryId.Value), ("$now", Now()), ("$key", key)));
        return (entryId, "remembered manual import match");
    }

    /// <summary>reset_import_manual_autoresolve: forgets the learned import → entry mappings (and the legacy history learning).</summary>
    public JsonObject ResetManualAutoresolve(DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return Write(c =>
        {
            var count = Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM import_manual_autoresolve"), CultureInfo.InvariantCulture);
            c.Execute("DELETE FROM import_manual_autoresolve");
            c.Execute("""
                INSERT INTO import_manual_autoresolve_state(key,value) VALUES('legacy_history_learning_disabled','1')
                ON CONFLICT(key) DO UPDATE SET value='1'
                """);
            Log("reset.import.autoresolve", actor, [], "Remembered manual import matches forgotten", after: new JsonObject { ["mappings"] = count });
            return new JsonObject { ["reset"] = true, ["autoresolve_mappings_removed"] = count };
        });
    }

    /// <summary>_forget_import_manual_autoresolve.</summary>
    internal bool ForgetManualAutoresolve(JsonObject item)
    {
        var key = RejectionKey(item);
        return Write(c => c.Execute("DELETE FROM import_manual_autoresolve WHERE backup_key=$key", ("$key", key)) > 0);
    }
}
