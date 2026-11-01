using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Catalogue.Tags;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Matching.Mixed;

/// <summary>Moves selected automatic records of one entry to one new entry (implemented by CatalogueStructure: the
/// journaled, non-manual split). Throws when the records are no longer attached or would empty the entry.</summary>
public interface IEditionSplitter
{
    (long OriginalEntryId, long NewEntryId) SplitAutomatic(long entryId, IReadOnlyList<(string SourceId, long MangaId)> records, string splitMatchType, CancellationToken cancellationToken);
}

/// <summary>What an automatic edition split would do. <see cref="Blocked"/> proposals are left for review (manual,
/// locked or manually bound records are never moved automatically).</summary>
public sealed record MixedSplitProposal(long EntryId, string Axis, string KeepMode, IReadOnlyList<(string SourceId, long MangaId)> Move, bool Blocked, string Reason)
{
    public JsonObject Json() => new()
    {
        ["entry_id"] = EntryId, ["axis"] = Axis, ["keep_mode"] = KeepMode, ["blocked"] = Blocked, ["reason"] = Reason,
        ["move"] = new JsonArray(Move.Select(item => (JsonNode)new JsonObject { ["source_id"] = item.SourceId, ["source_manga_id"] = item.MangaId }).ToArray()),
    };
}

/// <summary>One bounded step of the scaled check: <see cref="Entries"/> entries read after the old cursor, at most
/// <see cref="RecordsLoaded"/> records loaded, <see cref="Done"/> once the last entry was checked.</summary>
public sealed record MixedCheckBatch(long Cursor, int Entries, int RecordsLoaded, int MultiRecordEntries, int NeedsReview, int Splits, bool Done);

/// <summary>A per-entry view of the mixed-entry assessment.</summary>
internal sealed record MixedSnapshot(long EntryId, string PreferredTitle, string Fingerprint, IReadOnlyList<MixedRecord> Records,
    IReadOnlyList<MixedPairEvidence> Evidence, bool Partial, int RecordCount, IReadOnlyList<EditionRecordDecision> Decisions)
{
    public string State(string? reviewedAt) => reviewedAt is not null ? "reviewed" : Evidence.Count != 0 ? "needs_review" : "no_conflict";
}

/// <summary>
/// Matching → MixedEntries (phase 8): the scaled mixed-entry check, the review list/detail/review, automatic edition
/// split proposals and splits, legacy false-attachment repair and the per-source color analysis job.
/// The check walks the catalogue in keyset pages (entries.id), loads at most <see cref="MaximumRecordsPerEntry"/>
/// records per entry, saves its cursor after every batch (resumable after a restart) and stores one finding per
/// multi-record entry, so the list is a page read. A review never changes bindings or matching safeguards.
/// </summary>
public sealed class MixedEntries
{
    public const string CheckStateKey = "mixed_entry_check";
    public const int DefaultBatchSize = 200;
    public const int MaximumBatchSize = 500;
    public const int MaximumRecordsPerEntry = 64;
    public static readonly JobType CheckJob = new("mixed-entry-check", "Check mixed entries", JobPriority.Low, JobFlags.LongRunning, StallTimeout: TimeSpan.FromMinutes(5));
    public static readonly JobType ColorAnalysisJob = new("source-color-analysis", "Analyse source color edition", JobPriority.Low, RunLimit: 2, StallTimeout: TimeSpan.FromMinutes(2));
    private static readonly string[] States = ["needs_review", "reviewed", "no_conflict", "all"];

    private readonly SqliteDatabase _database;
    private readonly EditionProfiles _editions;
    private readonly IDecisionLog _decisions;
    private readonly IEditionSplitter? _splitter;
    private readonly CatalogueMetadata? _metadata;
    private readonly TimeProvider _clock;
    private readonly object _checkGate = new();

    public MixedEntries(SqliteDatabase database, EditionProfiles editions, IDecisionLog decisions, IEditionSplitter? splitter = null,
        CatalogueMetadata? metadata = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(editions); ArgumentNullException.ThrowIfNull(decisions);
        _database = database; _editions = editions; _decisions = decisions; _splitter = splitter; _metadata = metadata; _clock = clock ?? TimeProvider.System;
    }

    public EditionProfiles Editions => _editions;
    private string Stamp() => _clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    // ---- scaled check -----------------------------------------------------------------------------------------------

    /// <summary>The saved check state: run_id, cursor, checked, needs_review, splits, started_at, completed_at, auto_split.</summary>
    public JsonObject CheckStatus() => _database.Read(DatabaseFile.Catalog, ReadState);

    /// <summary>Starts a new check, or keeps an unfinished one so it resumes from its cursor (unless <paramref name="restart"/>).</summary>
    public JsonObject StartCheck(bool restart, bool autoSplit) => _database.Write(DatabaseFile.Catalog, c =>
    {
        var state = ReadState(c);
        var unfinished = state["run_id"] is not null && state["completed_at"] is null;
        if (!unfinished || restart)
            state = new JsonObject
            {
                ["run_id"] = Guid.NewGuid().ToString("N"), ["cursor"] = 0L, ["checked"] = 0L, ["needs_review"] = 0L, ["splits"] = 0L,
                ["started_at"] = Stamp(), ["completed_at"] = null, ["auto_split"] = autoSplit,
            };
        else if (autoSplit) state["auto_split"] = true;
        WriteState(c, state);
        return state;
    });

    /// <summary>Enqueues the check as a visible, cancellable, resumable job (same run merges).</summary>
    public JobHandle EnqueueCheck(JobQueue jobs, bool restart, bool autoSplit, int batchSize = DefaultBatchSize, string origin = "manual")
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var state = StartCheck(restart, autoSplit);
        return jobs.Enqueue(CheckJob, "catalogue", job => Task.Run(() =>
        {
            var total = Math.Max(1, _database.Read(DatabaseFile.Catalog, c => Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM entries"), CultureInfo.InvariantCulture)));
            while (true)
            {
                var batch = CheckBatch(batchSize, job.CancellationToken);
                var status = CheckStatus();
                var done = status["checked"]?.GetValue<long>() ?? 0;
                job.Report((int)Math.Min(int.MaxValue, done), (int)Math.Min(int.MaxValue, Math.Max(done, total)),
                    "Checked " + done.ToString(CultureInfo.InvariantCulture) + " entries · " + (status["needs_review"]?.GetValue<long>() ?? 0).ToString(CultureInfo.InvariantCulture) + " need review");
                if (batch.Done) { job.Result("Mixed-entry check complete · " + (status["needs_review"]?.GetValue<long>() ?? 0).ToString(CultureInfo.InvariantCulture) + " entries need review"); return; }
            }
        }, job.CancellationToken), origin: origin, title: "Check mixed entries", context: state["run_id"]?.GetValue<string>() ?? "");
    }

    /// <summary>Checks the next keyset page of at most <paramref name="batchSize"/> entries and saves the cursor.
    /// <paramref name="observeLoaded"/> receives the number of source records loaded for the batch.</summary>
    public MixedCheckBatch CheckBatch(int batchSize = DefaultBatchSize, CancellationToken cancellationToken = default, Action<int>? observeLoaded = null)
    {
        batchSize = Math.Clamp(batchSize, 1, MaximumBatchSize);
        lock (_checkGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = _database.Read(DatabaseFile.Catalog, c =>
            {
                var state = ReadState(c);
                if (state["run_id"] is null || state["completed_at"] is not null) throw new InvalidOperationException("Start a mixed-entry check first.");
                var after = state["cursor"]?.GetValue<long>() ?? 0;
                var page = c.QueryPage<long>("SELECT id FROM entries WHERE id>@after ORDER BY id", new { after }, batchSize);
                var snapshots = Snapshots(c, page.Items, out var loaded);
                var reviewed = Reviewed(c, snapshots.Select(snapshot => snapshot.Fingerprint));
                return (State: state, After: after, Page: page, Snapshots: snapshots, Loaded: loaded, Reviewed: reviewed);
            });
            observeLoaded?.Invoke(read.Loaded);
            cancellationToken.ThrowIfCancellationRequested();
            var last = read.Page.Items.Count == 0 ? read.After : read.Page.Items[^1];
            var done = !read.Page.HasMore;
            var needs = read.Snapshots.Count(snapshot => snapshot.State(read.Reviewed.GetValueOrDefault(snapshot.Fingerprint)) == "needs_review");
            _database.Write(DatabaseFile.Catalog, c =>
            {
                var current = ReadState(c);
                if (current["run_id"]?.GetValue<string>() != read.State["run_id"]?.GetValue<string>() || (current["cursor"]?.GetValue<long>() ?? 0) != read.After)
                    throw new InvalidOperationException("The mixed-entry check was restarted; this batch is discarded.");
                foreach (var snapshot in read.Snapshots) SaveFinding(c, snapshot);
                var keep = read.Snapshots.Select(snapshot => snapshot.EntryId).ToArray();
                var (names, parameters) = EditionProfiles.InList(keep);
                var range = done ? "entry_id>$after" : "entry_id>$after AND entry_id<=$last";
                c.Execute("DELETE FROM mixed_entry_findings WHERE " + range + (keep.Length == 0 ? "" : " AND entry_id NOT IN (" + names + ")"),
                    [("$after", read.After), ("$last", last), .. parameters]);
                current["cursor"] = last;
                current["checked"] = (current["checked"]?.GetValue<long>() ?? 0) + read.Page.Items.Count;
                current["needs_review"] = (current["needs_review"]?.GetValue<long>() ?? 0) + needs;
                if (done) current["completed_at"] = Stamp();
                WriteState(c, current);
                return 0;
            });
            var splits = 0;
            if (read.State["auto_split"]?.GetValue<bool>() == true && _splitter is not null)
                foreach (var snapshot in read.Snapshots.Where(item => item.Evidence.Any(pair => pair.Reasons.Any(reason => reason.StartsWith("Different", StringComparison.Ordinal)))))
                    foreach (var axis in new[] { "color", "media" })
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (AutoSplit(snapshot.EntryId, axis, cancellationToken) is not null) { splits++; break; }
                    }
            if (splits != 0)
                _database.Write(DatabaseFile.Catalog, c => { var state = ReadState(c); state["splits"] = (state["splits"]?.GetValue<long>() ?? 0) + splits; WriteState(c, state); return 0; });
            return new(last, read.Page.Items.Count, read.Loaded, read.Snapshots.Count, needs, splits, done);
        }
    }

    // ---- list, detail, review ---------------------------------------------------------------------------------------

    /// <summary>mixed_entry_candidates from the saved findings (run the check to refresh them).</summary>
    public JsonObject Candidates(string state = "needs_review", string query = "", int offset = 0, int limit = 50)
    {
        if (!States.Contains(state)) throw new ArgumentException("Unknown mixed-entry state", nameof(state));
        limit = Math.Clamp(limit, 1, 100);
        var folded = UnicodeCaseFold.Fold(UnicodeText.Trim(query ?? ""));
        return _database.Read(DatabaseFile.Catalog, c =>
        {
            const string Effective = "CASE WHEN r.fingerprint IS NOT NULL THEN 'reviewed' ELSE f.state END";
            var counts = c.Query("SELECT " + Effective + " effective,COUNT(*) n FROM mixed_entry_findings f LEFT JOIN mixed_entry_reviews r ON r.fingerprint=f.fingerprint GROUP BY effective");
            long CountOf(string name) => counts.Where(row => row.Text("effective") == name).Sum(row => row.Integer("n"));
            var where = (state == "all" ? "1=1" : Effective + "=$state") + (folded.Length == 0 ? "" : " AND instr(f.search_text,$query)>0");
            var total = Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM mixed_entry_findings f LEFT JOIN mixed_entry_reviews r ON r.fingerprint=f.fingerprint WHERE " + where,
                ("$state", state), ("$query", folded)), CultureInfo.InvariantCulture);
            var start = total == 0 ? 0 : Math.Min(Math.Max(0, offset), (int)((total - 1) / limit * limit));
            var items = c.Query("SELECT f.*,r.reviewed_at," + Effective + " effective FROM mixed_entry_findings f LEFT JOIN mixed_entry_reviews r ON r.fingerprint=f.fingerprint WHERE " + where
                + " ORDER BY CASE WHEN " + Effective + "='needs_review' THEN 0 ELSE 1 END,f.evidence_count DESC,f.sort_title,f.entry_id LIMIT $limit OFFSET $offset",
                ("$state", state), ("$query", folded), ("$limit", limit), ("$offset", start));
            return new JsonObject
            {
                ["items"] = new JsonArray(items.Select(row => (JsonNode)new JsonObject
                {
                    ["entry_id"] = row.Integer("entry_id"), ["preferred_title"] = row.Text("preferred_title"),
                    ["cover_url"] = "/api/v1/covers/" + row.Integer("entry_id").ToString(CultureInfo.InvariantCulture),
                    ["fingerprint"] = row.Text("fingerprint"), ["record_count"] = row.Integer("record_count"), ["evidence"] = ParseArray(row.Text("evidence_json")),
                    ["partial"] = row.Flag("partial"), ["reviewed_at"] = row["reviewed_at"] is null ? null : JsonValue.Create(row.Text("reviewed_at")),
                    ["state"] = row.Text("effective"), ["checked_at"] = row.Text("checked_at"),
                }).ToArray()),
                ["total"] = total, ["offset"] = start, ["limit"] = limit,
                ["counts"] = new JsonObject { ["needs_review"] = CountOf("needs_review"), ["reviewed"] = CountOf("reviewed"), ["all"] = counts.Sum(row => row.Integer("n")) },
                ["check"] = ReadState(c),
            };
        });
    }

    /// <summary>mixed_entry_detail: live evidence for one entry (also refreshes its saved finding).</summary>
    public JsonObject Detail(long entryId)
    {
        var (snapshot, reviewedAt, profile) = _database.Read(DatabaseFile.Catalog, c =>
        {
            var current = Current(c, entryId);
            return (current, current is null ? null : Reviewed(c, [current.Fingerprint]).GetValueOrDefault(current.Fingerprint),
                current is null ? null : _editions.Build(current.EntryId, current.PreferredTitle, current.Decisions));
        });
        if (snapshot is null || profile is null)
        {
            _database.Write(DatabaseFile.Catalog, c => c.Execute("DELETE FROM mixed_entry_findings WHERE entry_id=$id", ("$id", entryId)));
            throw new KeyNotFoundException("This entry no longer has multiple source records");
        }
        _database.Write(DatabaseFile.Catalog, c => { SaveFinding(c, snapshot); return 0; });
        var proposals = _database.Read(DatabaseFile.Catalog, c => new[] { ProposeSplit(c, snapshot.EntryId, "color"), ProposeSplit(c, snapshot.EntryId, "media") });
        return new JsonObject
        {
            ["entry_id"] = snapshot.EntryId, ["preferred_title"] = snapshot.PreferredTitle,
            ["cover_url"] = "/api/v1/covers/" + snapshot.EntryId.ToString(CultureInfo.InvariantCulture), ["fingerprint"] = snapshot.Fingerprint,
            ["records"] = new JsonArray(snapshot.Records.Select(record => (JsonNode)record.Json()).ToArray()), ["record_count"] = snapshot.RecordCount,
            ["evidence"] = new JsonArray(snapshot.Evidence.Select(pair => (JsonNode)pair.Json()).ToArray()), ["partial"] = snapshot.Partial,
            ["reviewed_at"] = reviewedAt, ["state"] = snapshot.State(reviewedAt), ["editions"] = profile.Json(),
            ["split_proposals"] = new JsonArray(proposals.Where(proposal => proposal is not null).Select(proposal => (JsonNode)proposal!.Json()).ToArray()),
        };
    }

    /// <summary>review_mixed_entry: "keep" remembers this exact evidence as correct, "clear" forgets it. Nothing else changes.</summary>
    public JsonObject Review(long entryId, string fingerprint, string decision, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (decision is not ("keep" or "clear")) throw new ArgumentException("Unknown review decision", nameof(decision));
        return _database.Write(DatabaseFile.Catalog, c =>
        {
            var current = Current(c, entryId) ?? throw new KeyNotFoundException("This entry no longer has multiple source records");
            if (current.Fingerprint != fingerprint) throw new InvalidOperationException("Source evidence changed. Refresh this entry before reviewing it.");
            var before = c.Scalar("SELECT reviewed_at FROM mixed_entry_reviews WHERE fingerprint=$f", ("$f", fingerprint)) as string;
            var stamp = Stamp();
            if (decision == "keep")
                c.Execute("INSERT OR REPLACE INTO mixed_entry_reviews(fingerprint,entry_id_hint,reviewed_at) VALUES($f,$id,$stamp)", ("$f", fingerprint), ("$id", current.EntryId), ("$stamp", stamp));
            else c.Execute("DELETE FROM mixed_entry_reviews WHERE fingerprint=$f", ("$f", fingerprint));
            SaveFinding(c, current);
            _decisions.Record(new DecisionEvent("review.mixed", actor, [current.EntryId], current.Records.Select(record => record.Key).ToArray(),
                decision == "keep" ? "Mixed entry kept together" : "Mixed entry review cleared",
                new JsonObject { ["fingerprint"] = fingerprint, ["reviewed_at"] = before },
                new JsonObject { ["fingerprint"] = fingerprint, ["reviewed_at"] = decision == "keep" ? stamp : null }));
            return new JsonObject { ["ok"] = true, ["entry_id"] = current.EntryId, ["decision"] = decision };
        });
    }

    // ---- automatic edition splits -----------------------------------------------------------------------------------

    /// <summary>The proposal behind _auto_split_mixed_color_entry / _auto_split_mixed_media_entry; null when the entry
    /// has no two strong editions on that axis.</summary>
    public MixedSplitProposal? ProposeSplit(DatabaseSession catalog, long entryId, string axis)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (axis is not ("color" or "media")) throw new ArgumentException("Axis is color or media", nameof(axis));
        entryId = PortableIdentityResolver.Canonical(catalog, entryId);
        var records = _editions.RecordDecisions(catalog, [entryId], TagSynonyms.Load(catalog)).GetValueOrDefault(entryId) ?? [];
        var minimum = _editions.MinimumConfidence;
        var classified = records.Select(record => (Record: record, Decision: axis == "color" ? record.Color : record.Media))
            .Where(item => item.Decision.Strong(minimum)).ToArray();
        if (classified.Select(item => item.Decision.Mode).Distinct(StringComparer.Ordinal).Count() < 2) return null;
        var primary = classified.Where(item => item.Record.Kind == "primary").Select(item => item.Decision.Mode).FirstOrDefault();
        var keep = primary ?? classified.GroupBy(item => item.Decision.Mode, StringComparer.Ordinal).OrderByDescending(group => group.Count()).First().Key;
        var selected = classified.Where(item => item.Decision.Mode != keep).ToArray();
        var move = selected.Select(item => (item.Record.Facts.SourceId, item.Record.Facts.MangaId)).ToArray();
        var manual = selected.Any(item => item.Record.Locked || item.Record.MatchType.ToLowerInvariant() is "manual" or "split-duplicate"
            || catalog.Scalar("SELECT 1 FROM manual_match_bindings WHERE source_id=$s AND source_manga_id=$m", ("$s", item.Record.Facts.SourceId), ("$m", item.Record.Facts.MangaId)) is not null);
        if (manual) return new(entryId, axis, keep, move, true, "A manual or locked record keeps this " + axis + " conflict visible for review");
        if (move.Length >= records.Count) return new(entryId, axis, keep, move, true, "Leave at least one source record on the original entry");
        return new(entryId, axis, keep, move, false, "Move " + move.Length.ToString(CultureInfo.InvariantCulture) + " automatic " + (axis == "color" ? (keep == "colored" ? "monochrome" : "colored") : (keep == "manga" ? "webtoon" : "manga")) + " record(s) to their own entry");
    }

    /// <summary>Applies an unblocked proposal as an automatic split, recomputes both entries and records the decision.</summary>
    public JsonObject? AutoSplit(long entryId, string axis, CancellationToken cancellationToken = default)
    {
        var proposal = _database.Read(DatabaseFile.Catalog, c => ProposeSplit(c, entryId, axis));
        if (proposal is null || proposal.Blocked) return null;
        if (_splitter is null) throw new InvalidOperationException("Automatic edition splits need the catalogue structure owner.");
        (long OriginalEntryId, long NewEntryId) result;
        try { result = _splitter.SplitAutomatic(proposal.EntryId, proposal.Move, "split-duplicate", cancellationToken); }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException) { return null; }
        _database.Write(DatabaseFile.Catalog, c =>
        {
            var stamp = Stamp();
            foreach (var id in new[] { result.OriginalEntryId, result.NewEntryId })
            {
                if (c.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", id)) is null) continue;
                _editions.Recompute(c, id);
                if (axis == "media") _metadata?.SyncRelatedMediaTitles(c, id, stamp);
                EditionProfiles.SyncColoredTitleLabel(c, id, stamp);
            }
            _decisions.Record(new DecisionEvent("split.edition", DecisionActor.Automatic, [result.OriginalEntryId, result.NewEntryId],
                proposal.Move.Select(item => item.SourceId + ":" + item.MangaId.ToString(CultureInfo.InvariantCulture)).ToArray(),
                "Automatic " + axis + " edition split: kept " + proposal.KeepMode, proposal.Json(),
                new JsonObject { ["original_entry_id"] = result.OriginalEntryId, ["new_entry_id"] = result.NewEntryId }));
            return 0;
        });
        return new JsonObject { ["original_entry_id"] = result.OriginalEntryId, ["new_entry_id"] = result.NewEntryId, ["axis"] = axis, ["kept"] = proposal.KeepMode };
    }

    /// <summary>_repair_legacy_color_false_attachments: splits only records attached by the old bare-color-word bug
    /// (one coherent group per entry; manual/locked records are never moved).</summary>
    public JsonObject RepairLegacyColorFalseAttachments(IEnumerable<long> entryIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        if (_splitter is null) throw new InvalidOperationException("Legacy repairs need the catalogue structure owner.");
        var entries = 0; var records = 0;
        foreach (var raw in entryIds.Where(id => id > 0).Distinct().Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = _database.Read(DatabaseFile.Catalog, c =>
            {
                var id = PortableIdentityResolver.Canonical(c, raw);
                var rows = c.Query("SELECT 'primary' kind,source_id,source_manga_id,source_title,match_type,locked FROM source_matches WHERE entry_id=$id AND active=1 UNION ALL SELECT 'additional' kind,source_id,source_manga_id,source_title,match_type,locked FROM source_variants WHERE entry_id=$id AND active=1", ("$id", id));
                if (rows.Count < 2) return (Entry: id, Move: Array.Empty<(string, long)>());
                var norms = rows.Select(row => TextMatcher.NormalizeTitle(row.Text("source_title"))).Where(value => value.Length != 0).ToHashSet(StringComparer.Ordinal);
                var group = rows.Where(row =>
                {
                    var title = row.Text("source_title").Trim(); var norm = TextMatcher.NormalizeTitle(title); var legacy = EditionProfiles.LegacyAmbiguousColorBase(title);
                    return title.Length != 0 && norm.Length != 0 && legacy.Length != 0 && legacy != norm && TextMatcher.TitleColorVariant(title) == "unknown" && norms.Contains(legacy)
                        && !row.Flag("locked") && row.Text("match_type").ToLowerInvariant() is not ("manual" or "split-duplicate");
                }).GroupBy(row => TextMatcher.NormalizeTitle(row.Text("source_title")), StringComparer.Ordinal).OrderBy(item => item.Key, StringComparer.Ordinal).FirstOrDefault();
                var move = group?.Select(row => (row.Text("source_id"), row.Integer("source_manga_id"))).Distinct().ToArray() ?? Array.Empty<(string, long)>();
                return (Entry: id, Move: move.Length >= rows.Count ? Array.Empty<(string, long)>() : move);
            });
            if (plan.Move.Length == 0) continue;
            try { _splitter.SplitAutomatic(plan.Entry, plan.Move, "split-duplicate", cancellationToken); }
            catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException) { continue; }
            entries++; records += plan.Move.Length;
            _database.Write(DatabaseFile.Catalog, c => _decisions.Record(new DecisionEvent("split.legacy-color", DecisionActor.Upgrade("legacy-color-false-attachments"), [plan.Entry],
                plan.Move.Select(item => item.Item1 + ":" + item.Item2.ToString(CultureInfo.InvariantCulture)).ToArray(), "Record attached by the removed bare color-word matcher")));
        }
        return new JsonObject { ["split_entries"] = entries, ["split_records"] = records };
    }

    /// <summary>schedule_source_color_analysis as a job: analyse one source record's pages, recompute the entry,
    /// split a now high-confidence colored/monochrome mixture automatically and keep the display title clean.
    /// Null when the record already has a decided analysis (the entry is recomputed instead).</summary>
    public JobHandle? ScheduleSourceColorAnalysis(JobQueue jobs, long entryId, string sourceId, long mangaId, string title = "")
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (!_editions.Options.Enabled) return null;
        var existing = _database.Read(DatabaseFile.Catalog, c => EditionProfiles.SourceProfile(c, sourceId, mangaId));
        if (existing is not null && existing.Confidence >= _editions.MinimumConfidence && !existing.Evidence.StartsWith("inferred:", StringComparison.Ordinal))
        {
            _database.Write(DatabaseFile.Catalog, c => { _editions.Recompute(c, entryId); EditionProfiles.SyncColoredTitleLabel(c, PortableIdentityResolver.Canonical(c, entryId), Stamp()); return 0; });
            return null;
        }
        return jobs.Enqueue(ColorAnalysisJob, sourceId + ":" + mangaId.ToString(CultureInfo.InvariantCulture), async job =>
        {
            job.Report(message: "Sampling chapter pages");
            await _editions.AnalyzeSourceColorAsync(sourceId, mangaId, title, false, job.CancellationToken).ConfigureAwait(false);
            var profile = _editions.Recompute(entryId);
            if (profile.Color.Mode == "mixed" && _splitter is not null && AutoSplit(profile.EntryId, "color", job.CancellationToken) is { } split)
                job.Result("Separated colored and monochrome editions into entry " + split["new_entry_id"]!.ToJsonString());
            else job.Result("Color edition: " + profile.Color.Mode);
            _database.Write(DatabaseFile.Catalog, c => EditionProfiles.SyncColoredTitleLabel(c, profile.EntryId, Stamp()));
        }, origin: "automatic", title: "Analyse color edition", context: sourceId + ":" + mangaId.ToString(CultureInfo.InvariantCulture));
    }

    // ---- internals --------------------------------------------------------------------------------------------------

    private MixedSnapshot? Current(DatabaseSession c, long entryId)
    {
        var id = PortableIdentityResolver.Canonical(c, entryId);
        return Snapshots(c, [id], out _).FirstOrDefault();
    }

    /// <summary>Snapshots of the given entries that have two or more active records (records bounded per entry).</summary>
    private List<MixedSnapshot> Snapshots(DatabaseSession c, IReadOnlyList<long> ids, out int loaded)
    {
        loaded = 0;
        var result = new List<MixedSnapshot>();
        if (ids.Count == 0) return result;
        var (names, parameters) = EditionProfiles.InList(ids);
        var counts = c.Query("""
            SELECT entry_id,COUNT(*) n FROM (SELECT entry_id FROM source_matches WHERE active=1 AND entry_id IN (NAMES)
            UNION ALL SELECT entry_id FROM source_variants WHERE active=1 AND entry_id IN (NAMES)) GROUP BY entry_id
            """.Replace("NAMES", names, StringComparison.Ordinal), parameters).ToDictionary(row => row.Integer("entry_id"), row => (int)row.Integer("n"));
        var multi = ids.Where(id => counts.GetValueOrDefault(id) >= 2).ToArray();
        if (multi.Length == 0) return result;
        var (titleNames, titleParameters) = EditionProfiles.InList(multi);
        var titles = c.Query("SELECT id,preferred_title FROM entries WHERE id IN (" + titleNames + ")", titleParameters).ToDictionary(row => row.Integer("id"), row => row.Text("preferred_title"));
        var decisions = _editions.RecordDecisions(c, multi, TagSynonyms.Load(c), MaximumRecordsPerEntry);
        var minimum = _editions.MinimumConfidence;
        foreach (var id in multi)
        {
            if (!titles.TryGetValue(id, out var title) || !decisions.TryGetValue(id, out var records)) continue;
            loaded += records.Count;
            var mixed = records.Select(record => new MixedRecord(record.Facts.Key, record.Facts.SourceId, record.Facts.MangaId,
                record.Facts.SourceName.Length == 0 ? record.Facts.SourceId : record.Facts.SourceName, record.Lang, record.Facts.Title, record.Facts.Description, record.Author,
                record.Media.Strong(minimum) ? record.Media.Mode : "unknown", record.Color.Strong(minimum) ? record.Color.Mode : "unknown", record.Locked, record.MatchType)).ToArray();
            var (evidence, partial) = MixedEntryAssessment.Assess(mixed);
            result.Add(new(id, title, MixedEntryAssessment.Fingerprint(mixed), mixed, evidence, partial || counts[id] > records.Count, counts[id], records));
        }
        return result;
    }

    private static Dictionary<string, string> Reviewed(DatabaseSession c, IEnumerable<string> fingerprints)
    {
        var values = fingerprints.Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length == 0) return new(StringComparer.Ordinal);
        var parameters = values.Select((value, index) => ("$f" + index.ToString(CultureInfo.InvariantCulture), (object?)value)).ToArray();
        return c.Query("SELECT fingerprint,reviewed_at FROM mixed_entry_reviews WHERE fingerprint IN (" + string.Join(',', parameters.Select(pair => pair.Item1)) + ")", parameters)
            .ToDictionary(row => row.Text("fingerprint"), row => row.Text("reviewed_at"), StringComparer.Ordinal);
    }

    private void SaveFinding(DatabaseSession c, MixedSnapshot snapshot)
    {
        var search = UnicodeCaseFold.Fold(string.Join('\n', snapshot.Records.Select(record => record.Title).Prepend(snapshot.PreferredTitle)));
        if (search.Length > 4000) search = search[..4000];
        c.Execute("""
            INSERT INTO mixed_entry_findings(entry_id,fingerprint,state,evidence_count,record_count,partial,preferred_title,sort_title,search_text,evidence_json,checked_at)
            VALUES($id,$fingerprint,$state,$evidence,$records,$partial,$title,$sort,$search,$json,$stamp)
            ON CONFLICT(entry_id) DO UPDATE SET fingerprint=excluded.fingerprint,state=excluded.state,evidence_count=excluded.evidence_count,record_count=excluded.record_count,
              partial=excluded.partial,preferred_title=excluded.preferred_title,sort_title=excluded.sort_title,search_text=excluded.search_text,evidence_json=excluded.evidence_json,checked_at=excluded.checked_at
            """, ("$id", snapshot.EntryId), ("$fingerprint", snapshot.Fingerprint), ("$state", snapshot.Evidence.Count != 0 ? "needs_review" : "no_conflict"),
            ("$evidence", snapshot.Evidence.Count), ("$records", snapshot.RecordCount), ("$partial", snapshot.Partial ? 1 : 0), ("$title", snapshot.PreferredTitle),
            ("$sort", UnicodeCaseFold.Fold(snapshot.PreferredTitle)), ("$search", search),
            ("$json", new JsonArray(snapshot.Evidence.Select(pair => (JsonNode)pair.Json()).ToArray()).ToJsonString()), ("$stamp", Stamp()));
    }

    private static JsonObject ReadState(DatabaseSession c)
    {
        if (c.Scalar("SELECT value FROM settings WHERE key=$key", ("$key", CheckStateKey)) is not string raw) return new JsonObject();
        try { return JsonNode.Parse(raw) as JsonObject ?? new JsonObject(); } catch (JsonException) { return new JsonObject(); }
    }

    private static void WriteState(DatabaseSession c, JsonObject state) =>
        c.Execute("INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", CheckStateKey), ("$value", state.ToJsonString()));

    private static JsonArray ParseArray(string text)
    {
        try { return JsonNode.Parse(text) as JsonArray ?? new JsonArray(); } catch (JsonException) { return new JsonArray(); }
    }
}
