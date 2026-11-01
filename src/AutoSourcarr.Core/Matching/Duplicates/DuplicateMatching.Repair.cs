using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Matching.Duplicates.DuplicateJson;

namespace AutoSourcarr.Core.Matching.Duplicates;

public sealed partial class DuplicateMatching
{
    // ---- consolidation (consolidate_entries / repair_catalog_duplicates / automatic sweep) ----

    /// <summary>
    /// Merges shortlisted pairs only when every original member has direct proof (_consolidate_entries_locked).
    /// Each pair is re-resolved and re-checked under the write gate so a manual decision made meanwhile wins.
    /// Returns old id → current canonical id.
    /// </summary>
    public Dictionary<long, long> Consolidate(IReadOnlyCollection<long> entryIds, DecisionActor actor, DuplicateProgress? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIds); ArgumentNullException.ThrowIfNull(actor);
        var current = Read(c => entryIds.Select(id => DuplicateEvidence.Canonical(c, id)).Distinct().ToArray());
        var remap = current.ToDictionary(id => id, id => id);
        if (current.Length < 2) return remap;
        var pairs = Shortlist(current).Pairs.Keys.Order().ToArray();
        progress?.Invoke(0, pairs.Length, pairs.Length == 0 ? "No duplicate candidate pairs found" : "Evaluating " + pairs.Length.ToString(CultureInfo.InvariantCulture) + " duplicate candidate pair" + (pairs.Length == 1 ? "" : "s"));
        var merged = 0; var evaluated = 0; var members = new Dictionary<long, List<JsonObject>>();
        foreach (var (originalLeft, originalRight) in pairs)
        {
            if (Paused)
            {
                RescanRequested = true;
                progress?.Invoke(evaluated, pairs.Length, "Duplicate sweep paused for entry/catalogue rebuild");
                break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            evaluated++;
            lock (_gate)
            {
                var (left, right) = Read(c => (DuplicateEvidence.Canonical(c, originalLeft), DuplicateEvidence.Canonical(c, originalRight)));
                if (left != right && Read(c => Exists(c, left) && Exists(c, right)))
                {
                    members.Remove(left); members.Remove(right);
                    var evidence = GroupEvidence(left, right, members);
                    if (Bool(evidence["eligible"]))
                    {
                        try
                        {
                            var target = MergeAutomatic(left, right, evidence, actor);
                            members.Remove(left); members.Remove(right); members.Remove(target);
                            merged++;
                        }
                        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or InvalidDataException)
                        {
                            // A gallery grouping rule or a concurrent change refused this pair; the next sweep re-evaluates it.
                        }
                    }
                }
            }
            if (evaluated == 1 || evaluated % 10 == 0 || evaluated == pairs.Length)
                progress?.Invoke(evaluated, pairs.Length, "Evaluated " + evaluated.ToString(CultureInfo.InvariantCulture) + "/" + pairs.Length.ToString(CultureInfo.InvariantCulture) + " direct duplicate pairs · " + merged.ToString(CultureInfo.InvariantCulture) + " safe merge" + (merged == 1 ? "" : "s"));
        }
        Read(c => { foreach (var id in current) remap[id] = DuplicateEvidence.Canonical(c, id); return 0; });
        return remap;
    }

    /// <summary>Recovers pending chapter work, then conservatively consolidates the whole catalogue (repair_catalog_duplicates).</summary>
    public JsonObject RepairCatalogDuplicates(DecisionActor actor, int limit = 10000, DuplicateProgress? progress = null, CancellationToken cancellationToken = default)
    {
        _structure.ReconcilePending(cancellationToken);
        var ids = Read(c => c.Query("SELECT id FROM entries ORDER BY id LIMIT $p0", P(Math.Max(1, limit))).Select(row => row.Integer("id")).ToArray());
        progress?.Invoke(0, 0, "Building duplicate shortlist across " + ids.Length.ToString(CultureInfo.InvariantCulture) + " catalogue entries");
        if (ids.Length < 2) return new JsonObject { ["checked"] = ids.Length, ["merged"] = 0 };
        var remap = Consolidate(ids, actor, progress, cancellationToken);
        var merged = remap.Count(pair => pair.Key != pair.Value);
        progress?.Invoke(ids.Length, ids.Length, "Merged " + merged.ToString(CultureInfo.InvariantCulture) + " duplicate entr" + (merged == 1 ? "y" : "ies"));
        return new JsonObject { ["checked"] = ids.Length, ["merged"] = merged };
    }

    /// <summary>
    /// One automatic duplicate sweep (the body of schedule_automatic_duplicate_matching): waits for the safety repair,
    /// hydrates evidence when available, consolidates, records the run and rebuilds the review snapshot.
    /// </summary>
    public async Task<JsonObject> RunAutomaticAsync(DuplicateProgress? progress = null, CancellationToken cancellationToken = default)
    {
        if (Paused) { RescanRequested = true; return new JsonObject { ["deferred"] = true, ["message"] = "Deferred while entry/catalogue automation is being rebuilt" }; }
        if (Str(_stored.Get("automatic_merge_safety_repair_version")) != SafetyRepairVersion) RunSafetyRepair();
        RescanRequested = false;
        var stamp = Stamp();
        try
        {
            if (Hydration is { } hydration)
            {
                progress?.Invoke(null, null, "Resolving metadata identities for duplicate candidates");
                await hydration.ResolveIdentitiesAsync(IdentityCandidates(), cancellationToken).ConfigureAwait(false);
                await hydration.FingerprintThumbnailsAsync(Math.Max(1, _settings().ThumbnailFingerprintEntriesPerPass), cancellationToken).ConfigureAwait(false);
            }
            var result = RepairCatalogDuplicates(DecisionActor.Automatic, 10000, progress, cancellationToken);
            _stored.Set("automatic_duplicate_last_run", stamp);
            _stored.Set("automatic_duplicate_last_checked", Long(result["checked"]));
            _stored.Set("automatic_duplicate_last_merged", Long(result["merged"]));
            _stored.Set("automatic_duplicate_last_error", "");
            progress?.Invoke(null, null, "Refreshing duplicate review");
            Groups(true, cancellationToken);
            if (RescanRequested && !Paused) ScheduleAutomatic(TimeSpan.FromSeconds(Math.Max(1, _settings().AutomaticDuplicateMatchingDelaySeconds)));
            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _stored.Set("automatic_duplicate_last_run", stamp);
            _stored.Set("automatic_duplicate_last_error", error.Message.Length > 1000 ? error.Message[..1000] : error.Message);
            throw;
        }
    }

    /// <summary>Queues one automatic sweep as a job (coalesced by the queue); false when disabled, paused or no queue runs.</summary>
    public bool ScheduleAutomatic(TimeSpan? delay = null, string origin = "automatic") => EnqueueAutomatic(delay, origin) is { Merged: false };

    /// <summary>Queues one automatic sweep and returns its job (Merged when one was already queued or running); null when not queued.</summary>
    public JobHandle? EnqueueAutomatic(TimeSpan? delay = null, string origin = "automatic")
    {
        if (!_settings().AutomaticDuplicateMatching && origin == "automatic") return null;
        if (Paused) { RescanRequested = true; return null; }
        if (Jobs is not { } jobs) return null;
        var wait = delay ?? TimeSpan.FromSeconds(Math.Max(0, _settings().AutomaticDuplicateMatchingDelaySeconds));
        try
        {
            var handle = jobs.Enqueue(AutomaticJob, "sweep", async job =>
            {
                if (wait > TimeSpan.Zero) { job.Report(message: "Queued · waiting briefly for foreground activity"); await Task.Delay(wait, job.CancellationToken).ConfigureAwait(false); }
                var result = await RunAutomaticAsync((current, total, message) => job.Report(current, total, message), job.CancellationToken).ConfigureAwait(false);
                job.Result(Bool(result["deferred"]) ? Str(result["message"]) : "Checked " + Long(result["checked"]).ToString(CultureInfo.InvariantCulture) + " entries · merged " + Long(result["merged"]).ToString(CultureInfo.InvariantCulture));
            }, origin: origin, title: "Automatic duplicate matching");
            if (handle.Merged) RescanRequested = true;
            return handle;
        }
        catch (Exception error) when (error is ObjectDisposedException or ArgumentException) { return null; }
    }

    /// <summary>Shortlisted entries without provider identities, capped (_hydrate_duplicate_identity_candidates selection).</summary>
    public IReadOnlyList<long> IdentityCandidates(int cap = 48)
    {
        cap = Math.Clamp(cap, 1, 200);
        var pairs = Shortlist().Pairs.Keys.Order().ToArray();
        return Read(c =>
        {
            var seen = new HashSet<long>(); var missing = new List<long>();
            foreach (var (left, right) in pairs)
                foreach (var raw in new[] { left, right })
                {
                    var id = DuplicateEvidence.Canonical(c, raw);
                    if (!seen.Add(id)) continue;
                    if (c.Scalar("SELECT 1 FROM identity_links WHERE entry_id=$p0", P(id)) is null) { missing.Add(id); if (missing.Count >= cap) return missing; }
                }
            return missing;
        });
    }

    // ---- unmatching (duplicate_unmatch_candidates / unmatch_duplicate_records / _split_duplicate_records) ----

    /// <summary>Merged entries whose records a person may split apart again (record-centric; nothing is guessed).</summary>
    public JsonArray UnmatchCandidates() => Read(c =>
    {
        var history = new Dictionary<long, List<DatabaseRow>>();
        foreach (var row in c.Query("SELECT old_entry_id,canonical_entry_id,reason,merged_at FROM entry_aliases ORDER BY merged_at DESC"))
        {
            var canonical = DuplicateEvidence.Canonical(c, row.Integer("canonical_entry_id"));
            if (!history.TryGetValue(canonical, out var list)) history[canonical] = list = [];
            list.Add(row);
        }
        var result = new List<JsonObject>();
        foreach (var (canonical, rows) in history)
        {
            var entry = c.Query("SELECT preferred_title FROM entries WHERE id=$p0", P(canonical)).SingleOrDefault();
            if (entry is null) continue;
            var records = ActiveRecords(c, canonical);
            if (records.Count < 2) continue;
            result.Add(new JsonObject
            {
                ["entry_id"] = canonical, ["preferred_title"] = entry.Text("preferred_title"), ["cover_url"] = "/api/v1/covers/" + canonical.ToString(CultureInfo.InvariantCulture),
                ["merge_count"] = rows.Count, ["last_merge_at"] = rows[0].Text("merged_at"),
                ["records"] = new JsonArray(records.Select(row => (JsonNode?)new JsonObject
                {
                    ["kind"] = row.Text("kind"), ["source_id"] = row.Text("source_id"), ["source_name"] = row.Text("source_name", "Source"), ["source_manga_id"] = row.Integer("source_manga_id"),
                    ["title"] = row.Text("source_title"), ["match_type"] = row.Text("match_type"), ["locked"] = row.Flag("locked"),
                }).ToArray()),
            });
        }
        return new JsonArray(result.OrderByDescending(item => Long(item["merge_count"])).ThenBy(item => Str(item["preferred_title"]), StringComparer.OrdinalIgnoreCase).Select(item => (JsonNode?)item).ToArray());
    });

    /// <summary>The explicit split: the chosen records become one new entry, remembered as a manual "different work" decision.</summary>
    public JsonObject UnmatchRecords(long entryId, IReadOnlyList<NativeSourceRecord> records, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(records); ArgumentNullException.ThrowIfNull(actor);
        if (records.Count == 0) throw new ArgumentException("Choose at least one source record", nameof(records));
        lock (_gate)
        {
            var original = Read(c => DuplicateEvidence.Canonical(c, entryId));
            if (!Read(c => Exists(c, original))) throw new KeyNotFoundException("Entry not found");
            var created = _structure.SplitRecords(original, records.Distinct().ToArray(), manual: true, "split-duplicate");
            _decisions.Record(new DecisionEvent("split", actor, [original, created], records.Select(Key).Distinct(StringComparer.Ordinal).ToArray(), "Unmatch duplicate records", null, new JsonObject { ["new_entry_id"] = created }));
            Invalidate();
            return new JsonObject { ["entry_id"] = original, ["new_entry_id"] = created, ["split_records"] = records.Count };
        }
    }

    /// <summary>
    /// Splits every automatic attachment off one entry, each to its own entry, and keeps them apart (unbind_entry_automatic_records).
    /// The oldest primary stays as anchor; manual, import, split and locked records are never touched.
    /// </summary>
    public JsonObject UnbindAutomaticRecords(long entryId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            var canonical = Read(c => DuplicateEvidence.Canonical(c, entryId));
            if (!Read(c => Exists(c, canonical))) throw new KeyNotFoundException("Entry not found");
            var rows = Read(c => ActiveRecords(c, canonical));
            if (rows.Count < 2) return new JsonObject { ["entry_id"] = canonical, ["split_records"] = 0, ["new_entry_ids"] = new JsonArray(), ["message"] = "No automatic records to unbind" };
            var primary = rows.Where(row => row.Text("kind") == "primary").ToList();
            var anchor = (primary.Count != 0 ? primary : rows).MinBy(row => row.Integer("id"))!;
            var anchorKey = new NativeSourceRecord(anchor.Text("source_id"), anchor.Integer("source_manga_id"));
            var candidates = rows.Select(row => (Row: row, Key: new NativeSourceRecord(row.Text("source_id"), row.Integer("source_manga_id")), Type: row.Text("match_type").ToLowerInvariant()))
                .Where(item => item.Key != anchorKey && !item.Row.Flag("locked") && item.Type is not ("manual" or "import" or "split-duplicate") && item.Type.StartsWith("auto", StringComparison.Ordinal))
                .Select(item => item.Key).Distinct().ToArray();
            var created = new List<long>();
            foreach (var record in candidates)
            {
                canonical = Read(c => DuplicateEvidence.Canonical(c, canonical));
                long id;
                try { id = _structure.SplitRecords(canonical, [record], manual: false, "split-duplicate"); }
                catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException) { continue; }
                created.Add(id);
                ReviewLocked(canonical, id, "not_duplicate", actor, "Unbind automatic matches");
                _decisions.Record(new DecisionEvent("split.unbind", actor, [canonical, id], [Key(record)], "Unbind automatic match", null, new JsonObject { ["new_entry_id"] = id }));
            }
            Invalidate();
            return new JsonObject
            {
                ["entry_id"] = Read(c => DuplicateEvidence.Canonical(c, canonical)), ["split_records"] = created.Count, ["new_entry_ids"] = JArray(created),
                ["message"] = "Unbound " + created.Count.ToString(CultureInfo.InvariantCulture) + " automatic source record" + (created.Count == 1 ? "" : "s"),
            };
        }
    }

    /// <summary>
    /// Resets only automation-owned relationships of one entry (prepare_entry_automation_redo): the best-supported
    /// native record stays as anchor, every unlocked non-manual record is split back out, unlocked provider identities,
    /// generated titles and stale lineage are dropped, titles are rebuilt from attached records, and only clearly
    /// foreign pairs are blocked. Manual and locked records are preserved.
    /// </summary>
    public JsonObject PrepareRedo(long entryId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            var canonical = Read(c => DuplicateEvidence.Canonical(c, entryId));
            var state = Read(c =>
            {
                var entry = c.Query("SELECT preferred_title,manual_preferred_title FROM entries WHERE id=$p0", P(canonical)).SingleOrDefault() ?? throw new KeyNotFoundException("Entry not found");
                var intent = c.Query("SELECT title FROM titles WHERE entry_id=$p0 AND title_type IN ('manual','main-library') ORDER BY id", P(canonical)).Select(row => row.Text("title")).Where(title => title.Trim().Length != 0).ToArray();
                return (Preferred: entry.Text("preferred_title").Trim(), Manual: entry.Flag("manual_preferred_title"), Intent: intent, Rows: ActiveRecords(c, canonical));
            });
            if (state.Rows.Count == 0)
                return new JsonObject { ["entry_id"] = canonical, ["split_records"] = 0, ["new_entry_ids"] = new JsonArray(), ["entry_ids"] = JArray(new[] { canonical }), ["preserved_records"] = 0 };
            static bool Curated(DatabaseRow row) { var type = row.Text("match_type").ToLowerInvariant(); return row.Flag("locked") || type == "manual" || type.StartsWith("manual-", StringComparison.Ordinal); }
            static string[] Native(DatabaseRow row) => TextMatcher.ExtractAltTitles(row.Text("description")).Prepend(row.Text("source_title")).Where(title => TextMatcher.IsMatchEvidenceTitle(title, primary: true)).ToArray();
            double Support(DatabaseRow a, DatabaseRow b)
            {
                var decision = TextMatcher.ScoreMatch(Native(a), Native(b), a.Text("author"), b.Text("author"), a.Text("description"), [b.Text("description")]);
                if (decision.Score >= TextAutomaticMatchGate.TitleFloor) return 1 + decision.Score;
                return decision.DescriptionScore >= 0.92 && decision.TitleScore >= 0.70 ? 0.5 : 0;
            }
            (double, double, double, double, double, long) Rank(DatabaseRow row)
            {
                var titles = Native(row);
                var consensus = state.Rows.Where(other => !ReferenceEquals(other, row)).Sum(other => Support(row, other));
                var strong = titles.SelectMany(a => state.Intent.Select(b => TextMatcher.TitleSimilarity(a, b))).DefaultIfEmpty(0).Max();
                var weak = state.Preferred.Length == 0 ? 0 : titles.Select(a => TextMatcher.TitleSimilarity(a, state.Preferred)).DefaultIfEmpty(0).Max();
                var type = row.Text("match_type").ToLowerInvariant();
                var curated = Curated(row) ? 1.0 : type == "import" ? 0.5 : 0;
                var primary = row.Text("kind") == "primary" ? 0.25 : 0;
                var richness = Math.Min(0.25, row.Text("description").Length / 4000.0);
                var score = consensus * 5 + strong * 8 + weak * (state.Manual ? 8 : 3) + curated * 1.5 + primary + richness;
                return (score, curated, consensus, strong, weak, -row.Integer("id"));
            }
            var anchor = state.Rows.MaxBy(Rank)!;
            var anchorKey = new NativeSourceRecord(anchor.Text("source_id"), anchor.Integer("source_manga_id"));
            var preserved = 0; var candidates = new List<NativeSourceRecord>();
            foreach (var row in state.Rows)
            {
                var key = new NativeSourceRecord(row.Text("source_id"), row.Integer("source_manga_id"));
                if (key == anchorKey || Curated(row)) { preserved++; continue; }
                candidates.Add(key);
            }
            var created = new List<long>(); var current = canonical;
            foreach (var record in candidates.Distinct())
            {
                current = Read(c => DuplicateEvidence.Canonical(c, current));
                try { created.Add(_structure.SplitRecords(current, [record], manual: false, "redo-anchor")); }
                catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException) { }
            }
            var affected = Read(c => created.Prepend(current).Select(id => DuplicateEvidence.Canonical(c, id)).Distinct().Where(id => Exists(c, id)).ToArray());
            var stamp = Stamp();
            Write(c =>
            {
                foreach (var id in affected)
                {
                    c.Execute("DELETE FROM identity_links WHERE entry_id=$p0 AND locked=0", P(id));
                    c.Execute("DELETE FROM titles WHERE entry_id=$p0 AND title_type NOT IN ('manual','main-library')", P(id));
                    c.Execute("DELETE FROM automatic_merge_lineage WHERE canonical_entry_id=$p0", P(id));
                    foreach (var record in ActiveRecords(c, id)) RefreshTitles(c, id, record);
                }
                c.Execute("UPDATE automatic_merge_events SET repaired_at=$p0 WHERE repaired_at='' AND target_entry_id=$p1", P(stamp, canonical));
                return 0;
            });
            var blocked = 0;
            var snapshots = Read(c => affected.ToDictionary(id => id, id => _evidence.Snapshot(c, id)));
            for (var i = 0; i < affected.Length; i++)
                for (var j = i + 1; j < affected.Length; j++)
                {
                    var decision = _matchGate.Decide(snapshots[affected[i]], snapshots[affected[j]]);
                    if (decision.Eligible) continue;
                    var title = Num(decision.Evidence["best_title_similarity"]); var summary = Num(decision.Evidence["summary_similarity"]);
                    var exact = Strings(decision.Evidence["exact_titles"]); var distinctive = Strings(decision.Evidence["distinctive_exact_titles"]);
                    var hard = decision.Reason.Contains("separate works", StringComparison.Ordinal);
                    var foreign = title < 0.72 && summary < 0.85 && exact.Count == 0;
                    var generic = exact.Count != 0 && distinctive.Count == 0 && summary < 0.72;
                    if (!(hard || foreign || generic)) continue;
                    ReviewLocked(affected[i], affected[j], "not_duplicate", actor, "Redo automation: clearly different works");
                    blocked++;
                }
            Metadata(affected);
            _decisions.Record(new DecisionEvent("split.redo", actor, affected, candidates.Select(Key).ToArray(), "Redo entry automation", null,
                new JsonObject { ["anchor"] = Key(anchorKey), ["new_entry_ids"] = JArray(created), ["blocked_pairs"] = blocked }));
            Invalidate();
            return new JsonObject
            {
                ["entry_id"] = Read(c => DuplicateEvidence.Canonical(c, current)), ["split_records"] = created.Count, ["new_entry_ids"] = JArray(created), ["entry_ids"] = JArray(affected),
                ["preserved_records"] = preserved, ["blocked_pairs"] = blocked, ["anchor_source_id"] = anchorKey.SourceId, ["anchor_source_manga_id"] = anchorKey.MangaId,
                ["anchor_title"] = anchor.Text("source_title"),
            };
        }
    }

    /// <summary>Queues Redo automation as a visible job: reset, then re-search and refresh each resulting entry (schedule_entry_automation_redo).</summary>
    public JobHandle ScheduleRedo(long entryId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var jobs = Jobs ?? throw new InvalidOperationException("The job queue is not running");
        var canonical = Read(c => DuplicateEvidence.Canonical(c, entryId));
        var title = Read(c => Exists(c, canonical) ? c.Scalar("SELECT preferred_title FROM entries WHERE id=$p0", P(canonical)) as string ?? "" : throw new KeyNotFoundException("Entry not found"));
        return jobs.Enqueue(RedoJob, canonical.ToString(CultureInfo.InvariantCulture), async job =>
        {
            using var pause = PauseAutomatic("redo-entry:" + canonical.ToString(CultureInfo.InvariantCulture));
            job.Report(0, 4, "Resetting automatic relationships");
            var prepared = PrepareRedo(canonical, actor);
            var ids = prepared["entry_ids"] is JsonArray array ? array.Select(node => Long(node)).Where(id => id > 0).Distinct().ToArray() : [canonical];
            var total = Math.Max(2, 2 + ids.Length * 2); var step = 1;
            job.Report(step, total, "Clean anchor: " + Str(prepared["anchor_title"], title) + " · split " + Long(prepared["split_records"]).ToString(CultureInfo.InvariantCulture) + " attached record(s)");
            if (Redo is { } redo)
            {
                var touched = new HashSet<long>();
                foreach (var raw in ids)
                {
                    var id = Read(c => DuplicateEvidence.Canonical(c, raw));
                    if (!touched.Add(id) || !Read(c => Exists(c, id))) { step += 2; continue; }
                    job.Report(step, total, "Re-searching sources for entry " + id.ToString(CultureInfo.InvariantCulture));
                    id = await redo.RediscoverAsync(id, job.CancellationToken).ConfigureAwait(false); step++;
                    job.Report(step, total, "Refreshing entry " + id.ToString(CultureInfo.InvariantCulture));
                    await redo.RefreshAsync(id, job.CancellationToken).ConfigureAwait(false); step++;
                }
            }
            lock (_snapshotLock) _snapshotStale = true;
            job.Result("Automation redone · " + Long(prepared["split_records"]).ToString(CultureInfo.InvariantCulture) + " automatic attachment(s) re-evaluated" + (Redo is null ? " · source re-search is not available in this build" : ""));
        }, origin: "manual", title: "Redo entry automation", context: canonical.ToString(CultureInfo.InvariantCulture));
    }

    private static void RefreshTitles(DatabaseSession c, long entryId, DatabaseRow record)
    {
        var sourceId = record.Text("source_id"); var manga = record.Integer("source_manga_id"); var description = record.Text("description");
        var language = c.Scalar("SELECT lang FROM source_config WHERE source_id=$p0", P(sourceId)) as string ?? "";
        var names = new List<(string Title, string Kind)> { (record.Text("source_title"), "matched-alias") };
        if (description.Trim().Length != 0) names.AddRange(TextMatcher.ExtractAltTitles(description).Select(name => (name, "description-alias")));
        foreach (var (name, kind) in names)
        {
            var title = name.Trim(); if (!TextMatcher.IsMeaningfulTitle(title)) continue;
            var normalized = TextMatcher.NormalizeTitle(title); if (normalized.Length == 0) continue;
            c.Execute("""
                INSERT INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)
                ON CONFLICT(entry_id,normalized,source_id,title_type) DO UPDATE SET title=excluded.title,
                  language=CASE WHEN excluded.language<>'' THEN excluded.language ELSE titles.language END,
                  source_manga_id=CASE WHEN excluded.source_manga_id<>0 THEN excluded.source_manga_id ELSE titles.source_manga_id END
                """, P(entryId, title, normalized, language, kind, sourceId, manga));
        }
    }

    // ---- automatic-merge safety repair (_automatic_merge_reason / _repair_unsafe_automatic_merges / run_automatic_merge_safety_repair) ----

    /// <summary>True for a recorded automatic merge reason; manual, import and split reasons never count.</summary>
    public static bool AutomaticMergeReason(string reason)
    {
        var value = (reason ?? "").ToLowerInvariant();
        if (value.Length == 0) return false;
        if (new[] { "manual-", "manual_", "manual ", "import-source-bind", "split-duplicate" }.Any(marker => value.Contains(marker, StringComparison.Ordinal))) return false;
        return value.Contains("merge:", StringComparison.Ordinal) || value.Contains("automatic", StringComparison.Ordinal);
    }

    /// <summary>The one-time v0.2.69 upgrade repair: source attachments (CatalogueMaintenance, when wired), then old automatic snowballs.</summary>
    public JsonObject RunSafetyRepair(bool force = false)
    {
        if (!force && Str(_stored.Get("automatic_merge_safety_repair_version")) == SafetyRepairVersion) return new JsonObject { ["skipped"] = true, ["version"] = SafetyRepairVersion };
        lock (_gate)
        {
            if (!force && Str(_stored.Get("automatic_merge_safety_repair_version")) == SafetyRepairVersion) return new JsonObject { ["skipped"] = true, ["version"] = SafetyRepairVersion };
            var sources = SourceAttachmentRepair?.Invoke() ?? new JsonObject { ["available"] = false };
            var merges = RepairUnsafeAutomaticMerges();
            _stored.Set("automatic_merge_safety_repair_version", SafetyRepairVersion);
            return new JsonObject { ["skipped"] = false, ["version"] = SafetyRepairVersion, ["source_attachments"] = sources, ["merges"] = merges };
        }
    }

    /// <summary>
    /// Splits clearly foreign record groups out of snowball-shaped automatic merges (many records, several distinct
    /// source titles, or many generated aliases). Clique grouping prevents a transitive repair; locked groups and close
    /// calls stay for review.
    /// </summary>
    public JsonObject RepairUnsafeAutomaticMerges()
    {
        var actor = DecisionActor.Upgrade("automatic-merge-safety-" + SafetyRepairVersion);
        var plan = Read(c =>
        {
            var aliases = new Dictionary<long, int>(); var candidates = new HashSet<long>();
            foreach (var row in c.Query("SELECT canonical_entry_id,reason FROM entry_aliases ORDER BY merged_at"))
                if (AutomaticMergeReason(row.Text("reason"))) { var id = DuplicateEvidence.Canonical(c, row.Integer("canonical_entry_id")); aliases[id] = aliases.GetValueOrDefault(id) + 1; candidates.Add(id); }
            var generated = c.Query("SELECT entry_id,COUNT(*) n FROM titles WHERE title_type IN ('description-alias','matched-alias') GROUP BY entry_id").GroupBy(row => DuplicateEvidence.Canonical(c, row.Integer("entry_id"))).ToDictionary(group => group.Key, group => group.Sum(row => row.Integer("n")));
            var records = new Dictionary<long, List<string>>();
            foreach (var row in c.Query("SELECT entry_id,source_title FROM source_matches WHERE active=1 UNION ALL SELECT entry_id,source_title FROM source_variants WHERE active=1"))
            {
                var id = DuplicateEvidence.Canonical(c, row.Integer("entry_id"));
                if (!records.TryGetValue(id, out var list)) records[id] = list = [];
                list.Add(row.Text("source_title"));
            }
            foreach (var (id, titles) in records)
            {
                var distinct = titles.Select(TextMatcher.NormalizeTitle).Where(value => value.Length != 0).Distinct(StringComparer.Ordinal).Count();
                if (titles.Count >= 8 || titles.Count >= 5 && distinct >= 3 || generated.GetValueOrDefault(id) >= 16) candidates.Add(id);
            }
            foreach (var (id, count) in generated) if (count >= 16) candidates.Add(id);
            return (Candidates: candidates.Order().ToArray(), Aliases: aliases, Generated: generated);
        });
        int scanned = 0, repaired = 0, groupsSplit = 0, recordsSplit = 0;
        foreach (var canonical in plan.Candidates)
        {
            scanned++;
            lock (_gate)
            {
                var (preferred, rows) = Read(c => (c.Scalar("SELECT preferred_title FROM entries WHERE id=$p0", P(canonical)) as string ?? "", ActiveRecords(c, canonical)));
                var distinctTitles = rows.Select(row => TextMatcher.NormalizeTitle(row.Text("source_title"))).Where(value => value.Length != 0).Distinct(StringComparer.Ordinal).Count();
                var suspicious = rows.Count >= 8 || plan.Aliases.GetValueOrDefault(canonical) >= 5 || rows.Count >= 5 && distinctTitles >= 3 || plan.Generated.GetValueOrDefault(canonical) >= 16;
                if (!suspicious || rows.Count < 2) continue;
                var snaps = Read(c => rows.Select(row => RepairSnapshot(c, row)).ToList())
                    .OrderByDescending(snap => snap.Locked).ThenByDescending(snap => TextMatcher.TitleSimilarity(preferred, snap.Title)).ToList();
                var groups = new List<List<RepairRecord>>();
                foreach (var snap in snaps)
                {
                    var home = groups.FirstOrDefault(group => group.All(other => DirectlyCompatible(snap, other)));
                    if (home is null) groups.Add([snap]); else home.Add(snap);
                }
                if (groups.Count <= 1) continue;
                var anchorIndex = Enumerable.Range(0, groups.Count).MaxBy(i => (groups[i].Any(x => x.Locked), groups[i].Max(x => TextMatcher.TitleSimilarity(preferred, x.Title)), groups[i].Count));
                var changed = false;
                foreach (var (_, group) in groups.Select((group, index) => (index, group)).Where(item => item.index != anchorIndex).OrderBy(item => -item.group.Count).ThenBy(item => item.index))
                {
                    if (group.Any(x => x.Locked)) continue;
                    var anchorGroup = groups[anchorIndex];
                    var bestTitle = anchorGroup.SelectMany(a => group.Select(b => TextMatcher.TitleSimilarity(a.Title, b.Title))).DefaultIfEmpty(0).Max();
                    var bestDescription = anchorGroup.SelectMany(a => group.Select(b => TextMatcher.DescriptionSimilarity(a.Description, b.Description))).DefaultIfEmpty(0).Max();
                    if (bestTitle >= 0.70 || bestTitle >= 0.35 && bestDescription >= 0.93) continue;
                    var current = Read(c => DuplicateEvidence.Canonical(c, canonical));
                    long created;
                    try { created = _structure.SplitRecords(current, group.Select(x => x.Key).ToArray(), manual: false, "split-duplicate"); }
                    catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException) { continue; }
                    groupsSplit++; recordsSplit += group.Count; changed = true;
                    Write(c => c.Execute("DELETE FROM identity_links WHERE entry_id IN ($p0,$p1) AND locked=0", P(current, created)));
                    Metadata([current, created]);
                    _decisions.Record(new DecisionEvent("split.repair", actor, [current, created], group.Select(x => Key(x.Key)).ToArray(), "Unsafe automatic merge repair: records are clearly a different work"));
                }
                if (changed) repaired++;
            }
        }
        if (repaired != 0) Invalidate();
        return new JsonObject { ["scanned_candidates"] = scanned, ["repaired_entries"] = repaired, ["split_groups"] = groupsSplit, ["split_records"] = recordsSplit };
    }

    private sealed record RepairRecord(NativeSourceRecord Key, string Title, string Normalized, string Description, string Author, string Fingerprint, string Color, string Media, bool Locked);

    private static RepairRecord RepairSnapshot(DatabaseSession c, DatabaseRow row)
    {
        var title = row.Text("source_title").Trim();
        var subject = "source:" + row.Text("source_id") + ":" + row.Integer("source_manga_id").ToString(CultureInfo.InvariantCulture);
        var fingerprint = c.Scalar("SELECT fingerprint FROM thumbnail_fingerprints WHERE subject_key=$p0", P(subject)) as string ?? "";
        return new(new(row.Text("source_id"), row.Integer("source_manga_id")), title, TextMatcher.NormalizeTitle(title), row.Text("description"), row.Text("author"), fingerprint,
            TextMatcher.TitleColorVariant(title), Catalogue.CatalogueMetadata.CandidateMediaMode(title, row.Text("genre")), row.Flag("locked"));
    }

    /// <summary>Released _repair_records_directly_compatible.</summary>
    private static bool DirectlyCompatible(RepairRecord left, RepairRecord right)
    {
        if (left.Color is "colored" or "monochrome" && right.Color is "colored" or "monochrome" && left.Color != right.Color) return false;
        if (left.Media is "manga" or "webtoon" && right.Media is "manga" or "webtoon" && left.Media != right.Media) return false;
        var title = left.Title.Length != 0 && right.Title.Length != 0 ? TextMatcher.TitleSimilarity(left.Title, right.Title) : 0;
        var description = TextMatcher.DescriptionSimilarity(left.Description, right.Description);
        var author = left.Author.Length != 0 && right.Author.Length != 0 ? TextMatcher.NameSimilarity(left.Author, right.Author) : 0;
        var thumb = left.Fingerprint.Length != 0 && left.Fingerprint == right.Fingerprint;
        if (left.Normalized.Length != 0 && left.Normalized == right.Normalized) return true;
        return thumb && title >= 0.90 && description >= 0.55 || title >= 0.94 && description >= 0.65 || title >= 0.90 && author >= 0.80 && description >= 0.45 || description >= 0.92 && title >= 0.70;
    }
}
