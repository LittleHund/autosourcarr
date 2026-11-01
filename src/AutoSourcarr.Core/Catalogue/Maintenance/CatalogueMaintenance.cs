using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Manual;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources.Library;
using static AutoSourcarr.Core.Matching.Manual.ManualSql;

namespace AutoSourcarr.Core.Catalogue.Maintenance;

/// <summary>
/// Phase 8 "Catalogue/Maintenance": the database resets (reset_database keeps manual decisions and subscriptions and
/// restores manual matches; full_reset_database keeps only configuration and subscriptions), entry deletion, detaching
/// or promoting records, and the one-time startup repairs (run_startup_catalog_maintenance). Every change is logged.
/// </summary>
public sealed partial class CatalogueMaintenance
{
    /// <summary>Settings rows a reset keeps (source attention state, not catalogue data).</summary>
    public static readonly string[] KeptSettings = ["source_human_attention_v1", "source_solver_gate_v1", "source_solver_probe_v1"];

    /// <summary>reset_database: rebuildable catalogue state (see <see cref="NormalResetSummary"/>). duplicate_reviews keeps its
    /// "different works" rows (re-keyed by <see cref="RekeyKeptDecisions"/>); deleted-record suppression, configuration and import history stay.</summary>
    public static readonly string[] ResetCatalogTables =
    [
        "quality_attempts", "crawl_pending", "due_work", "library_complete_observations", "serving_choices",
        "duplicate_split_history", "duplicate_review_history",
        "browse_cache", "translation_cache", "metadata_query_cache", "match_history",
        "deleted_entries_history", "entry_aliases",
        "source_record_tags", "gallery_records",
        "source_variants", "source_matches", "identity_links",
        "source_color_profiles", "titles", "entries", "settings",
        "gallery_layout_candidates", "pending_entry_splits", "pending_entry_merges", "automatic_merge_lineage", "automatic_merge_events",
        "mixed_entry_findings",
    ];

    /// <summary>full_reset_database: everything learned or decided; source_config, source_groups and user_ui_preferences stay.</summary>
    public static readonly string[] FullResetCatalogTables =
    [
        "catalogue_exchange_stages", "catalogue_exchange_receipts", "catalogue_decisions", "catalogue_import_records",
        "catalogue_source_facts", "catalogue_public_refs", "catalogue_shared_entries", "catalogue_entry_ids", "catalogue_uid_records",
        "catalogue_record_bindings", "catalogue_record_resolutions",
        "portable_identity_reviews", "portable_identity_review_history",
        "quality_attempts", "crawl_pending", "due_work", "library_complete_observations", "serving_choices",
        "display_metadata", "display_metadata_history", "serving_upgrade_policies", "serving_upgrade_history",
        "policy_preview_history", "serving_policy_history",
        "serving_profiles", "library_tracking", "entry_preference_history", "matching_review_actions",
        "mixed_entry_reviews", "mixed_entry_findings", "duplicate_split_history", "duplicate_review_history", "duplicate_reviews",
        "gallery_layout_candidates", "pending_entry_splits", "pending_entry_merges", "automatic_merge_lineage", "automatic_merge_events",
        "browse_cache", "translation_cache", "metadata_query_cache", "match_history",
        "deleted_source_records", "deleted_entries_history", "entry_aliases",
        "source_record_tags", "gallery_records", "tag_reviews", "tags",
        "source_variants", "source_matches", "identity_links", "source_color_profiles",
        "thumbnail_fingerprints", "titles", "entries", "settings",
        "preferred_title_override_sources", "preferred_title_overrides", "manual_title_scopes",
        "manual_separate_records", "manual_match_bindings", "manual_match_groups",
        "import_match_rejections", "import_manual_autoresolve", "import_manual_autoresolve_state",
        "import_items", "import_runs", DecisionLog.Table,
    ];

    public static readonly string[] ResetOriginTables = ["candidate_pages", "served_origin", "origin_history", "quality_samples", "source_record_scans", "chapter_candidates", "virtual_chapters"];
    public static readonly string[] FullResetOriginTables = [.. ResetOriginTables, "chapter_candidate_rejections"];

    /// <summary>The subscribed-entry rebuild after a reset: high priority, one at a time, long.</summary>
    public static readonly JobType SubscribedRebuildJob = new("subbed-reset-rebuild", "Rebuild subscribed entries", JobPriority.High,
        JobFlags.LongRunning, StallTimeout: TimeSpan.FromMinutes(30));

    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly IDecisionLog _log;
    private readonly ManualDecisions _manual;
    private readonly IManualCatalogueOperations? _operations;
    private readonly SuwayomiLibrary? _library;
    private readonly JobQueue? _jobs;
    private readonly CatalogueMaintenanceHooks _hooks;
    private readonly SettingsRepository _settings;
    private readonly Func<long, JsonObject>? _entry;
    private readonly string? _dataDirectory;
    private readonly TimeProvider _clock;

    /// <param name="entry">The entry payload answered after a change (CatalogueQueries.Entry); without it the routes answer the entry id.</param>
    /// <param name="dataDirectory">Where 0.4.5 kept reset-rebuild-hints.json (removed by a full reset).</param>
    public CatalogueMaintenance(SqliteDatabase database, object sourceWriteGate, IDecisionLog log, ManualDecisions manual,
        CatalogueMaintenanceHooks? hooks = null, SuwayomiLibrary? library = null, JobQueue? jobs = null,
        Func<long, JsonObject>? entry = null, string? dataDirectory = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate);
        ArgumentNullException.ThrowIfNull(log); ArgumentNullException.ThrowIfNull(manual);
        _database = database; _gate = sourceWriteGate; _log = log; _manual = manual; _operations = manual.Operations;
        _hooks = hooks ?? new CatalogueMaintenanceHooks(); _library = library; _jobs = jobs; _entry = entry;
        _dataDirectory = dataDirectory; _clock = clock ?? TimeProvider.System; _settings = new SettingsRepository(database);
    }

    private string Stamp() => DecisionStamp.Now(_clock);
    private T Read<T>(Func<DatabaseSession, T> operation) => _database.Read(DatabaseFile.Catalog, operation);
    private T Write<T>(Func<DatabaseSession, T> operation) { lock (_gate) return _database.Write(DatabaseFile.Catalog, operation); }
    private void Log(string kind, DecisionActor actor, IEnumerable<long> entries, IEnumerable<string> records, string reason, JsonObject? before = null, JsonObject? after = null)
        => _log.Record(new DecisionEvent(kind, actor, entries.Where(id => id > 0).Distinct().ToArray(), records.Distinct(StringComparer.Ordinal).ToArray(), reason, before, after));
    private JsonObject EntryAnswer(long entryId) => _entry is { } view ? view(entryId) : new JsonObject { ["entry_id"] = entryId };
    private static string Cover(string kind, long id) => kind + "-" + id.ToString(CultureInfo.InvariantCulture);

    // ---- database resets

    /// <summary>reset_database / full_reset_database tables only (no seeds, hooks or restore); the routes use <see cref="ResetAsync"/>.</summary>
    public JsonObject ClearTables(bool full, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var catalog = full ? FullResetCatalogTables : ResetCatalogTables;
        var origins = full ? FullResetOriginTables : ResetOriginTables;
        var existing = Read(c => c.QueryAs<string>("SELECT name FROM sqlite_master WHERE type='table'").ToHashSet(StringComparer.Ordinal));
        Write(c =>
        {
            // A normal reset keeps the id-based manual decisions: remember which records their entries held, for the re-key.
            if (!full) CaptureKeptDecisions(c);
            if (!full && existing.Contains("duplicate_reviews")) c.Execute("DELETE FROM duplicate_reviews WHERE decision<>'not_duplicate'");
            foreach (var table in catalog.Where(existing.Contains))
            {
                if (table == "settings")
                    c.Execute("DELETE FROM settings WHERE key NOT IN (SELECT value FROM json_each($p0))", P(JsonSerializer.Serialize(full ? KeptSettings : KeptSettings.Append(CarrySetting).ToArray())));
                else
                    c.Execute("DELETE FROM \"" + table + "\"");
            }
            // AUTOINCREMENT state stays: reader and main-library URLs carry entry and chapter ids, which must never be reused.
            Log(full ? "reset.full" : "reset.database", actor, [], [], full ? "Full reset: catalogue and decisions cleared, configuration kept"
                : NormalResetSummary);
            return 0;
        });
        _database.Write(DatabaseFile.Origins, o => { foreach (var table in origins) o.Execute("DELETE FROM \"" + table + "\""); return 0; });
        if (full && _dataDirectory is { Length: > 0 } directory)
        {
            try { File.Delete(Path.Combine(directory, "reset-rebuild-hints.json")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        _hooks.ResetCaches?.Invoke();
        var result = new JsonObject { ["reset"] = true, ["catalog_tables"] = catalog.Length, ["origin_tables"] = origins.Length };
        if (full) { result["full_reset"] = true; result["configuration_kept"] = true; }
        else result["summary"] = NormalResetSummary;
        return result;
    }

    /// <summary>
    /// admin_reset_database / admin_full_reset: snapshots main-library subscriptions (aborts when main Suwayomi is
    /// configured but cannot be read), captures rebuild hints and source settings (normal reset), clears covers and
    /// tables, re-syncs sources, recreates subscribed entries, restores manual matches (normal reset) and queues the
    /// subscribed rebuild. Run it as an exclusive job: nothing else may write while it runs.
    /// </summary>
    public async Task<JsonObject> ResetAsync(bool full, DecisionActor actor, Action<string>? report = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        report?.Invoke("Saving main-library subscriptions");
        var seeds = new List<JsonObject>();
        if (_library is { } library)
        {
            var snapshot = await library.SnapshotResetSeedsAsync(cancellationToken).ConfigureAwait(false);
            var configured = snapshot["error"]?.GetValue<string>() is not ("" or null) && !string.Equals(snapshot["error"]!.GetValue<string>(), "MAIN_SUWAYOMI_URL is not configured", StringComparison.Ordinal);
            if (snapshot["ok"]?.GetValue<bool>() != true && configured)
                throw new InvalidOperationException((full ? "Full reset" : "Database reset") + " aborted: unable to snapshot subscribed entries: " + snapshot["error"]!.GetValue<string>());
            seeds = (snapshot["seeds"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(seed => (JsonObject)seed.DeepClone()).ToList();
        }
        JsonObject? hints = null, preferences = null;
        if (full)
        {
            // Full reset discards old source hints and manual state; only the main-library identity and title stay.
            foreach (var seed in seeds)
            {
                seed["rebuild_hints"] = new JsonArray();
                seed["title"] = (Text(seed["library_title"]) is { Length: > 0 } libraryTitle ? libraryTitle : Text(seed["title"])).Trim();
                seed["manual_title"] = false; seed["description"] = ""; seed["thumbnail_url"] = ""; seed["status"] = 0;
                seed["author"] = ""; seed["artist"] = ""; seed["genre"] = "";
            }
        }
        else
        {
            if (_hooks.RebuildHintsForEntries is { } hintsFor)
            {
                var map = hintsFor(seeds.Select(seed => Integer(seed["canonical_entry_id"]) is > 0 and var id ? id : Integer(seed["entry_id"])).Where(id => id > 0).Distinct().ToArray());
                foreach (var seed in seeds)
                {
                    var id = Integer(seed["canonical_entry_id"]) is > 0 and var canonical ? canonical : Integer(seed["entry_id"]);
                    seed["rebuild_hints"] = map.TryGetValue(id, out var list) ? list.DeepClone() : new JsonArray();
                }
            }
            hints = _hooks.CaptureRebuildHints?.Invoke();
            preferences = _hooks.CaptureSourcePreferences?.Invoke();
        }
        report?.Invoke("Clearing covers and the catalogue");
        var covers = _hooks.ClearCovers is { } clear ? await clear(cancellationToken).ConfigureAwait(false) : 0;
        var result = ClearTables(full, actor);
        result["cover_cache_files_removed"] = covers;
        if (_hooks.SyncSources is { } sync)
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(TimeSpan.FromSeconds(8));
                await sync(limit.Token).ConfigureAwait(false);
                result["sources_resynced"] = true;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            { result["sources_resynced"] = false; result["source_sync_error"] = error.Message; }
        }
        else result["sources_resynced"] = false;
        if (!full) result["source_preferences_restored"] = preferences is not null && _hooks.RestoreSourcePreferences is { } restore ? restore(preferences) : 0;
        report?.Invoke("Recreating subscribed entries");
        result["subbed_snapshot"] = seeds.Count;
        result["subbed_restored"] = _library?.RestoreResetSeeds(seeds) ?? 0;
        if (!full)
        {
            report?.Invoke("Restoring manual matches");
            result["manual_matches"] = _manual.RestorePersistentManualMatches(actor);
            report?.Invoke("Re-attaching kept manual decisions");
            result["kept_decisions"] = RekeyKeptDecisions();
            result["rebuild_hint_records"] = Integer(hints?["records"]);
        }
        result["subbed_rebrowse_queued"] = seeds.Count > 0 && ScheduleSubscribedRebuild(seeds);
        if (!full) result["catalog_hint_replay_queued"] = hints is not null && _hooks.ScheduleHintReplay is { } replay && replay(Text(hints["path"]));
        result["continuous_catalog_queued"] = _hooks.ScheduleContinuousDiscovery?.Invoke() ?? false;
        return result;
    }

    /// <summary>schedule_subbed_reset_rebrowse: queues the subscribed rebuild as a job (false when it cannot run here).</summary>
    public bool ScheduleSubscribedRebuild(IReadOnlyList<JsonObject> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        if (_library is not { } library || _hooks.RebuildSubscribedEntry is not { } rebuild || _jobs is not { } jobs) return false;
        var copy = seeds.Select(seed => (JsonObject)seed.DeepClone()).ToArray();
        try
        {
            jobs.Enqueue(SubscribedRebuildJob, "", async job =>
            {
                var result = await library.RebrowseResetSeedsAsync(copy, rebuild, (done, total, message) => job.Report(done, total, message), job.CancellationToken).ConfigureAwait(false);
                job.Result("Recovered " + Integer(result["recovered"]).ToString(CultureInfo.InvariantCulture) + " of " + Integer(result["searched"]).ToString(CultureInfo.InvariantCulture) + " subscribed entries");
            }, origin: "automatic", context: "reset");
            return true;
        }
        catch (Exception error) when (error is ObjectDisposedException or ArgumentException) { return false; }
    }

    // ---- entry maintenance (delete_entry, clear_source_variant, promote_variant_to_primary, clear_match)

    /// <summary>
    /// delete_entry: removes the entry, its chapters, aliases and titles and tombstones its records (deleted_source_records)
    /// so a background refresh does not bring it back. Manual bindings of its records are forgotten.
    /// </summary>
    public JsonObject DeleteEntry(long entryId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            var (target, records) = Write(c =>
            {
                var id = Canonical(c, entryId);
                var entry = c.Query("SELECT * FROM entries WHERE id=$p0", P(id));
                if (entry.Count == 0) throw new KeyNotFoundException("Entry not found");
                var rows = c.Query("SELECT source_id,source_manga_id,source_title FROM source_matches WHERE entry_id=$p0 UNION ALL SELECT source_id,source_manga_id,source_title FROM source_variants WHERE entry_id=$p0", P(id));
                var stamp = Stamp(); var title = entry[0].Text("preferred_title");
                foreach (var record in rows)
                    c.Execute("""
                        INSERT INTO deleted_source_records(source_id,source_manga_id,source_title,deleted_entry_title,deleted_at) VALUES($p0,$p1,$p2,$p3,$p4)
                        ON CONFLICT(source_id,source_manga_id) DO UPDATE SET source_title=excluded.source_title,deleted_entry_title=excluded.deleted_entry_title,deleted_at=excluded.deleted_at
                        """, P(record.Text("source_id"), record.Integer("source_manga_id"), record.Text("source_title"), title, stamp));
                c.Execute("INSERT INTO deleted_entries_history(old_entry_id,preferred_title,source_records,deleted_at) VALUES($p0,$p1,$p2,$p3)", P(id, title, rows.Count, stamp));
                Log("entry.delete", actor, [id], rows.Select(row => RecordKey(row.Text("source_id"), row.Integer("source_manga_id"))), "Entry deleted; its records are suppressed",
                    new JsonObject { ["preferred_title"] = title, ["records"] = rows.Count });
                return (id, rows);
            });
            // Origins live in their own file: remove the chapters first.
            _database.Write(DatabaseFile.Origins, o =>
            {
                o.Execute("DELETE FROM source_record_scans WHERE entry_id=$p0", P(target));
                var chapters = JsonSerializer.Serialize(o.QueryAs<long>("SELECT id FROM virtual_chapters WHERE entry_id=@target", new { target }).ToArray());
                o.Execute("DELETE FROM served_origin WHERE virtual_chapter_id IN (SELECT value FROM json_each($p0))", P(chapters));
                o.Execute("DELETE FROM origin_history WHERE virtual_chapter_id IN (SELECT value FROM json_each($p0))", P(chapters));
                return o.Execute("DELETE FROM virtual_chapters WHERE id IN (SELECT value FROM json_each($p0))", P(chapters));
            });
            Write(c =>
            {
                foreach (var record in records) _manual.ForgetBinding(c, record.Text("source_id"), record.Integer("source_manga_id"), dissolveSmallGroup: true);
                c.Execute("DELETE FROM entry_aliases WHERE old_entry_id=$p0 OR canonical_entry_id=$p0", P(target));
                c.Execute("DELETE FROM preferred_title_overrides WHERE entry_id=$p0", P(target));
                c.Execute("DELETE FROM manual_title_scopes WHERE entry_id=$p0", P(target));
                foreach (var record in records)
                    c.Execute("DELETE FROM preferred_title_override_sources WHERE source_id=$p0 AND source_manga_id=$p1", P(record.Text("source_id"), record.Integer("source_manga_id")));
                return c.Execute("DELETE FROM entries WHERE id=$p0", P(target));
            });
            RepairCachedEntryIds();
            _operations?.Invalidate([Cover("entry", target)]);
            return new JsonObject { ["deleted"] = true, ["entry_id"] = entryId, ["canonical_entry_id"] = target, ["source_records_suppressed"] = records.Count };
        }
    }

    /// <summary>clear_source_variant: detaches one additional record (its chapters stop contributing; its binding is forgotten).</summary>
    public JsonObject ClearSourceVariant(long entryId, string sourceId, long sourceMangaId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            var target = Write(c =>
            {
                var id = Canonical(c, entryId);
                if (c.Scalar("SELECT 1 FROM source_variants WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2 AND active=1", P(id, sourceId, sourceMangaId)) is null)
                    throw new KeyNotFoundException("Additional match not found");
                c.Execute("UPDATE source_variants SET active=0,last_checked=$p0 WHERE entry_id=$p1 AND source_id=$p2 AND source_manga_id=$p3", P(Stamp(), id, sourceId, sourceMangaId));
                _manual.ForgetBinding(c, sourceId, sourceMangaId, dissolveSmallGroup: true);
                Log("variant.clear", actor, [id], [RecordKey(sourceId, sourceMangaId)], "Additional record removed");
                return id;
            });
            _operations?.PurgeSourceRecordTitles(target, sourceId, [sourceMangaId]);
            _manual.DeactivateCandidates(target, sourceId, sourceMangaId);
            _operations?.ReconcileChapters(target);
            _operations?.ChooseMetadata(target);
            _operations?.Invalidate([Cover("entry", target), Cover("source", sourceMangaId)]);
            return EntryAnswer(target);
        }
    }

    /// <summary>promote_variant_to_primary: the additional record becomes the primary; the old primary stays as an additional record.</summary>
    public JsonObject PromoteVariantToPrimary(long entryId, string sourceId, long sourceMangaId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var operations = _operations ?? throw new ManualDecisionUnavailableException("Catalogue operations are not available in this host; nothing was changed.");
        lock (_gate)
        {
            var (target, promoted, previous) = Write(c =>
            {
                var id = Canonical(c, entryId);
                var variant = c.Query("SELECT * FROM source_variants WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2 AND active=1", P(id, sourceId, sourceMangaId));
                if (variant.Count == 0) throw new KeyNotFoundException("Entry or additional record not found");
                var primary = c.Query("SELECT * FROM source_matches WHERE entry_id=$p0 AND source_id=$p1 AND active=1", P(id, sourceId));
                // The additional row goes first so the same record can become the unique primary.
                c.Execute("DELETE FROM source_variants WHERE id=$p0", P(variant[0].Integer("id")));
                Log("variant.promote", actor, [id], [RecordKey(sourceId, sourceMangaId)], "Additional record used as primary",
                    primary.Count > 0 ? new JsonObject { ["primary_source_manga_id"] = primary[0].Integer("source_manga_id") } : null);
                return (id, variant[0], primary.Count > 0 ? primary[0] : null);
            });
            operations.UpsertPrimary(target, sourceId, ManualSql.Payload(promoted), 1.0, "manual", true);
            if (previous is not null && previous.Integer("source_manga_id") != sourceMangaId)
                operations.UpsertVariant(target, sourceId, ManualSql.Payload(previous), previous.Number("confidence", 1.0) is var confidence && confidence > 0 ? confidence : 1.0,
                    previous.Text("match_type", "auto") is { Length: > 0 } type ? type : "auto", previous.Flag("locked"));
            operations.ChooseMetadata(target);
            operations.Invalidate([Cover("entry", target)]);
            return EntryAnswer(target);
        }
    }

    /// <summary>clear_match: detaches every record of the source from the entry (marked 'cleared'); their bindings are forgotten.</summary>
    public JsonObject ClearMatch(long entryId, string sourceId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            var (target, removed) = Write(c =>
            {
                var id = Canonical(c, entryId);
                if (!EntryExists(c, id)) throw new KeyNotFoundException("Entry not found");
                var primary = c.Scalar("SELECT source_manga_id FROM source_matches WHERE entry_id=$p0 AND source_id=$p1 AND active=1", P(id, sourceId));
                if (primary is null) return (id, new HashSet<long>());
                var ids = c.QueryAs<long>("SELECT source_manga_id FROM source_variants WHERE entry_id=@id AND source_id=@sourceId AND active=1", new { id, sourceId }).ToHashSet();
                ids.Add(Convert.ToInt64(primary, CultureInfo.InvariantCulture));
                var stamp = Stamp();
                c.Execute("UPDATE source_matches SET active=0,locked=0,match_type='cleared',last_checked=$p0 WHERE entry_id=$p1 AND source_id=$p2", P(stamp, id, sourceId));
                c.Execute("UPDATE source_variants SET active=0,last_checked=$p0 WHERE entry_id=$p1 AND source_id=$p2", P(stamp, id, sourceId));
                foreach (var mangaId in ids.Order()) _manual.ForgetBinding(c, sourceId, mangaId, dissolveSmallGroup: true);
                Log("match.clear", actor, [id], ids.Select(mangaId => RecordKey(sourceId, mangaId)), "Source match cleared");
                return (id, ids);
            });
            if (removed.Count == 0) return EntryAnswer(target);
            _operations?.PurgeSourceRecordTitles(target, sourceId, removed);
            _manual.DeactivateCandidates(target, sourceId, null);
            _operations?.ReconcileChapters(target);
            _operations?.ChooseMetadata(target);
            _operations?.Invalidate(removed.Select(id => Cover("source", id)).Append(Cover("entry", target)).ToArray());
            return EntryAnswer(target);
        }
    }

    /// <summary>_repair_cached_entry_ids: rewrites persisted browse feeds through entry_aliases and drops ids that no longer exist.</summary>
    public int RepairCachedEntryIds() => Write(c =>
    {
        var changed = 0;
        foreach (var row in c.Query("SELECT cache_key,entry_ids_json FROM browse_cache"))
        {
            long[] old;
            try { old = JsonSerializer.Deserialize<long[]>(row.Text("entry_ids_json", "[]")) ?? []; }
            catch (JsonException) { old = []; }
            var fresh = new List<long>(); var seen = new HashSet<long>();
            foreach (var id in old)
            {
                var canonical = Canonical(c, id);
                if (EntryExists(c, canonical) && seen.Add(canonical)) fresh.Add(canonical);
            }
            if (fresh.SequenceEqual(old)) continue;
            c.Execute("UPDATE browse_cache SET entry_ids_json=$p0 WHERE cache_key=$p1", P(JsonSerializer.Serialize(fresh), row.Text("cache_key")));
            changed++;
        }
        return changed;
    });
}
