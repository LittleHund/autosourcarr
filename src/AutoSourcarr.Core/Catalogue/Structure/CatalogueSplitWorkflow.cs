using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Mixed;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Catalogue.Structure.StructureSql;

namespace AutoSourcarr.Core.Catalogue.Structure;

/// <summary>
/// Strict automatic evidence used by Redo (implemented by the entry-ingest/duplicate owners; <see cref="TitleRedoEvidence"/>
/// is the conservative built-in). <see cref="PairSupport"/> ranks the anchor (eligible pair → 1 + score, near-identical
/// long summary with a related title → 0.5, otherwise 0); <see cref="ClearlyForeign"/> decides which clean entries are
/// remembered as "not duplicate" after the redo split.
/// </summary>
public interface IAutomationRedoEvidence
{
    double PairSupport(string leftSourceId, JsonObject left, string rightSourceId, JsonObject right);
    bool ClearlyForeign(DatabaseSession catalog, long leftEntryId, long rightEntryId);
}

/// <summary>The follow-up of a Redo (8A discovery/refresh, 8C duplicate pause).</summary>
public interface IEntryAutomation
{
    Task<IAsyncDisposable> PauseDuplicateMatchingAsync(string reason, CancellationToken cancellationToken);
    /// <summary>Searches every enabled source again for the entry; returns its canonical id afterwards.</summary>
    Task<long> DiscoverAllSourceRecordsAsync(long entryId, CancellationToken cancellationToken);
    Task RefreshEntryAsync(long entryId, CancellationToken cancellationToken);
}

/// <summary>Conservative default redo evidence from titles, summaries and the edition gate only.</summary>
public sealed class TitleRedoEvidence(IEditionGate? editions = null, double threshold = .94) : IAutomationRedoEvidence
{
    public double PairSupport(string leftSourceId, JsonObject left, string rightSourceId, JsonObject right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        var (title, _, _) = TextMatcher.BestAutomaticTitleScore(Titles(left), Titles(right));
        var summary = TextMatcher.DescriptionSimilarity(Text(left, "description"), Text(right, "description"));
        if (title >= threshold) return 1.0 + title;
        return summary >= .92 && title >= .70 ? .5 : 0;
    }

    public bool ClearlyForeign(DatabaseSession catalog, long leftEntryId, long rightEntryId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (editions is not null && editions.CompareEntries(catalog, leftEntryId, rightEntryId).Conflict) return true;
        IEnumerable<string> EntryTitles(long id) => CatalogueMetadata.NativeTitleRows(catalog, id).Select(row => row["title"]!.GetValue<string>());
        string Summary(long id) => catalog.Scalar("SELECT description FROM entries WHERE id=$p0", P(id)) as string ?? "";
        var left = EntryTitles(leftEntryId).ToArray(); var right = EntryTitles(rightEntryId).ToArray();
        var (title, _, _) = TextMatcher.BestAutomaticTitleScore(left, right);
        var exact = left.Select(TextMatcher.NormalizeTitle).Intersect(right.Select(TextMatcher.NormalizeTitle), StringComparer.Ordinal).Any(value => value.Length != 0);
        var summary = TextMatcher.DescriptionSimilarity(Summary(leftEntryId), Summary(rightEntryId));
        return title < .72 && summary < .85 && !exact;
    }

    private static IEnumerable<string> Titles(JsonObject payload) =>
        TextMatcher.ExtractAltTitles(Text(payload, "description")).Prepend(Text(payload, "title")).Where(value => TextMatcher.IsMatchEvidenceTitle(value, primary: true));
}

/// <summary>Catalogue/Structure → CatalogueSplit (phase 8): automatic edition splits, unbind and redo of automatic
/// attachments, pending split completion and the shared record payloads.</summary>
public sealed partial class CatalogueStructure : IEditionSplitter
{
    public static readonly JobType RedoJob = new("entry-automation-redo", "Redo entry automation", JobPriority.High, StallTimeout: TimeSpan.FromMinutes(15));

    /// <summary>_record_payload: the stored source record as a source manga payload (row columns win over metadata_json).</summary>
    public static JsonObject RecordPayload(DatabaseRow row) => Payload(row);

    /// <summary>_gallery_record_payload: the retained native gallery payload (metadata_json first, columns fill gaps).</summary>
    public static JsonObject GalleryRecordPayload(DatabaseRow row) => GalleryPayload(row);

    /// <summary>pending_entry_origin_count: merges plus splits whose chapter-origin work is still journaled.</summary>
    public long PendingEntryOriginCount() => _database.Read(DatabaseFile.Catalog, c =>
        Convert.ToInt64(c.Scalar("SELECT (SELECT COUNT(*) FROM pending_entry_merges)+(SELECT COUNT(*) FROM pending_entry_splits)"), CultureInfo.InvariantCulture));

    /// <summary>_finish_pending_entry_split (with _restore_gallery_split_chapters and _complete_pending_entry_split): finishes one journaled split.</summary>
    public void FinishPendingSplit(long splitId)
    {
        lock (_gate) FinishSplit(splitId);
    }

    /// <summary>_reconcile_split_chapters: hides N.1/N.2 parts whose page counts add up to a whole chapter N (the reader owner's rule).</summary>
    public void ReconcileSplitChapters(long entryId) => _visibility(_database.Read(DatabaseFile.Catalog, c => Canonical(c, entryId)));

    /// <summary>IEditionSplitter: a journaled, non-manual split of automatic records (edition and legacy repairs).</summary>
    public (long OriginalEntryId, long NewEntryId) SplitAutomatic(long entryId, IReadOnlyList<(string SourceId, long MangaId)> records, string splitMatchType, CancellationToken cancellationToken)
        => SplitAutomatic(entryId, records, splitMatchType, _ => true, cancellationToken);

    /// <summary>IEditionSplitter with a precondition re-checked under the source-write gate right before the split.</summary>
    public (long OriginalEntryId, long NewEntryId) SplitAutomatic(long entryId, IReadOnlyList<(string SourceId, long MangaId)> records, string splitMatchType,
        Func<DatabaseSession, bool> stillAllowed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records); ArgumentNullException.ThrowIfNull(stillAllowed);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireStandalone();
            var keys = records.Select(record => new NativeSourceRecord(record.SourceId, record.MangaId)).Distinct().ToArray();
            var state = _database.Read(DatabaseFile.Catalog, c =>
            {
                var id = Canonical(c, entryId); var rows = Records(c, id); var wanted = keys.ToHashSet();
                var manual = rows.Any(row => wanted.Contains(row.Key) && (row.Row.Flag("locked") || row.Row.Text("match_type").ToLowerInvariant() == "manual"
                    || c.Scalar("SELECT 1 FROM manual_match_bindings WHERE source_id=$p0 AND source_manga_id=$p1", P(row.Key.SourceId, row.Key.MangaId)) is not null));
                return (Entry: id, Manual: manual);
            });
            if (state.Manual) throw new InvalidOperationException("Manual or locked records are never moved automatically");
            // The precondition is re-checked inside the split's own catalogue transaction (a review can not slip in between).
            var created = Split(state.Entry, keys, manual: false, splitMatchType: splitMatchType, precondition: stillAllowed);
            return (state.Entry, created);
        }
    }

    /// <summary>unbind_entry_automatic_records: every unlocked "auto*" record (except the oldest primary anchor) moves to
    /// its own entry and each pair is remembered as not duplicate. Manual, imported, split and locked records stay.</summary>
    public JsonObject UnbindAutomaticRecords(long entryId, IDecisionLog decisions, DecisionActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decisions); ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            RequireStandalone();
            var plan = _database.Read(DatabaseFile.Catalog, c =>
            {
                var id = Canonical(c, entryId); if (!Exists(c, id)) throw new KeyNotFoundException("Entry not found");
                var rows = Records(c, id);
                if (rows.Count < 2) return (Entry: id, Move: new List<NativeSourceRecord>());
                var primaries = rows.Where(row => row.Storage == "source_matches").ToList();
                var anchor = (primaries.Count != 0 ? primaries : rows).OrderBy(row => row.Row.Integer("id")).First();
                bool Automatic(Owned row)
                {
                    var type = row.Row.Text("match_type").ToLowerInvariant();
                    return type is not ("manual" or "import" or "split-duplicate") && type.StartsWith("auto", StringComparison.Ordinal);
                }
                var move = rows.Where(row => row.Key != anchor.Key && !row.Row.Flag("locked") && Automatic(row)).Select(row => row.Key).Distinct().ToList();
                return (Entry: id, Move: move);
            });
            if (plan.Move.Count == 0)
                return new JsonObject { ["entry_id"] = plan.Entry, ["split_records"] = 0, ["new_entry_ids"] = new JsonArray(), ["message"] = "No automatic records to unbind" };
            var created = new List<long>(); var current = plan.Entry;
            foreach (var record in plan.Move)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = _database.Read(DatabaseFile.Catalog, c => Canonical(c, current));
                long id;
                try { id = Split(current, [record], manual: false); }
                catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException) { continue; }
                created.Add(id);
                var anchorEntry = current;
                _database.Write(DatabaseFile.Catalog, c =>
                {
                    SeparateReview(c, anchorEntry, id, Stamp());
                    decisions.Record(new DecisionEvent("unbind.automatic", actor, [anchorEntry, id], [record.SourceId + ":" + record.MangaId.ToString(CultureInfo.InvariantCulture)],
                        "Automatic record unbound and kept separate"));
                    return 0;
                });
            }
            var final = _database.Read(DatabaseFile.Catalog, c => Canonical(c, current));
            _invalidate([final, .. created]);
            return new JsonObject
            {
                ["entry_id"] = final, ["split_records"] = created.Count, ["new_entry_ids"] = new JsonArray(created.Select(value => (JsonNode)value).ToArray()),
                ["message"] = "Unbound " + created.Count.ToString(CultureInfo.InvariantCulture) + " automatic source record" + (created.Count == 1 ? "" : "s"),
            };
        }
    }

    /// <summary>prepare_entry_automation_redo: keeps the best-supported anchor and every manual/locked record, splits all
    /// other records into their own entries ("redo-anchor"), discards unlocked provider identities, generated aliases and
    /// automatic merge lineage of the affected entries, rebuilds provenance aliases, and remembers only clearly foreign
    /// pairs as not duplicate. Unlike unbind, nothing else is blocked: Redo lets current matching decide again.</summary>
    public JsonObject PrepareAutomationRedo(long entryId, IAutomationRedoEvidence evidence, IDecisionLog decisions, DecisionActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence); ArgumentNullException.ThrowIfNull(decisions); ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            RequireStandalone();
            var state = _database.Read(DatabaseFile.Catalog, c =>
            {
                var id = Canonical(c, entryId);
                var entry = c.Query("SELECT preferred_title,manual_preferred_title FROM entries WHERE id=$p0", P(id)).FirstOrDefault() ?? throw new KeyNotFoundException("Entry not found");
                var intent = c.Query("SELECT title FROM titles WHERE entry_id=$p0 AND title_type IN ('manual','main-library') ORDER BY id", P(id)).Select(row => row.Text("title")).Where(title => title.Trim().Length != 0).ToArray();
                return (Entry: id, Title: entry.Text("preferred_title").Trim(), ManualTitle: entry.Flag("manual_preferred_title"), Intent: intent, Rows: Records(c, id).OrderBy(row => row.Row.Integer("id")).ToList());
            });
            if (state.Rows.Count == 0)
                return new JsonObject { ["entry_id"] = state.Entry, ["split_records"] = 0, ["new_entry_ids"] = new JsonArray(), ["entry_ids"] = new JsonArray(state.Entry), ["preserved_records"] = 0 };
            static bool Curated(Owned row) { var type = row.Row.Text("match_type").ToLowerInvariant(); return row.Row.Flag("locked") || type == "manual" || type.StartsWith("manual-", StringComparison.Ordinal); }
            var payloads = state.Rows.ToDictionary(row => row.Key, row => Payload(row.Row));
            IEnumerable<string> NativeTitles(Owned row) => TextMatcher.ExtractAltTitles(Text(payloads[row.Key], "description")).Prepend(Text(payloads[row.Key], "title"))
                .Where(value => TextMatcher.IsMatchEvidenceTitle(value, primary: true));
            (double Score, double Curated, double Consensus, double Strong, double Weak, long Id) Rank(Owned row)
            {
                var titles = NativeTitles(row).ToArray();
                var consensus = state.Rows.Where(other => other.Key != row.Key).Sum(other => evidence.PairSupport(row.Key.SourceId, payloads[row.Key], other.Key.SourceId, payloads[other.Key]));
                var strong = titles.Length == 0 || state.Intent.Length == 0 ? 0 : titles.Max(a => state.Intent.Max(b => TextMatcher.TitleSimilarity(a, b)));
                var weak = titles.Length == 0 || state.Title.Length == 0 ? 0 : titles.Max(a => TextMatcher.TitleSimilarity(a, state.Title));
                var type = row.Row.Text("match_type").ToLowerInvariant();
                var curated = Curated(row) ? 1.0 : type == "import" ? .5 : 0;
                var primary = row.Storage == "source_matches" ? .25 : 0;
                var richness = Math.Min(.25, Length(Text(payloads[row.Key], "description")) / 4000.0);
                var score = consensus * 5 + strong * 8 + weak * (state.ManualTitle ? 8 : 3) + curated * 1.5 + primary + richness;
                return (score, curated, consensus, strong, weak, -row.Row.Integer("id"));
            }
            var anchor = state.Rows.MaxBy(Rank)!;
            var preserved = state.Rows.Count(row => row.Key == anchor.Key || Curated(row));
            var move = state.Rows.Where(row => row.Key != anchor.Key && !Curated(row)).Select(row => row.Key).Distinct().ToList();
            var created = new List<long>(); var current = state.Entry;
            foreach (var record in move)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = _database.Read(DatabaseFile.Catalog, c => Canonical(c, current));
                try { created.Add(Split(current, [record], manual: false, splitMatchType: "redo-anchor")); }
                catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException) { }
            }
            var affected = _database.Read(DatabaseFile.Catalog, c => created.Prepend(current).Select(id => Canonical(c, id)).Distinct().Where(id => Exists(c, id)).ToArray());
            var blocked = 0;
            if (affected.Length != 0)
            {
                _database.Write(DatabaseFile.Catalog, c =>
                {
                    var (names, parameters) = EditionProfiles.InList(affected); var stamp = Stamp();
                    // Unlocked provider identities, generated aliases and frozen merge lineage must not teach the redo.
                    c.Execute("DELETE FROM identity_links WHERE entry_id IN (" + names + ") AND locked=0", parameters);
                    c.Execute("DELETE FROM titles WHERE entry_id IN (" + names + ") AND title_type NOT IN ('manual','main-library')", parameters);
                    c.Execute("DELETE FROM automatic_merge_lineage WHERE canonical_entry_id IN (" + names + ")", parameters);
                    c.Execute("UPDATE automatic_merge_events SET repaired_at=$stamp WHERE repaired_at='' AND target_entry_id=$target", ("$stamp", stamp), ("$target", state.Entry));
                    foreach (var id in affected)
                        foreach (var row in Records(c, id)) RefreshTitles(c, id, row.Key.SourceId, Payload(row.Row));
                    for (var i = 0; i < affected.Length; i++)
                        for (var j = i + 1; j < affected.Length; j++)
                            if (evidence.ClearlyForeign(c, affected[i], affected[j])) { SeparateReview(c, affected[i], affected[j], stamp); blocked++; }
                    foreach (var id in affected) _metadata(c, id, stamp);
                    decisions.Record(new DecisionEvent("redo.automation", actor, affected, move.Select(key => key.SourceId + ":" + key.MangaId.ToString(CultureInfo.InvariantCulture)).ToArray(),
                        "Automatic relationships reset for a clean re-evaluation",
                        new JsonObject { ["entry_id"] = state.Entry, ["records"] = state.Rows.Count },
                        new JsonObject { ["entry_ids"] = new JsonArray(affected.Select(value => (JsonNode)value).ToArray()), ["blocked_pairs"] = blocked, ["anchor"] = anchor.Key.SourceId + ":" + anchor.Key.MangaId.ToString(CultureInfo.InvariantCulture) }));
                    return 0;
                });
                foreach (var id in affected) _visibility(id);
                _invalidate(affected);
            }
            return new JsonObject
            {
                ["entry_id"] = affected.FirstOrDefault(state.Entry), ["split_records"] = created.Count,
                ["new_entry_ids"] = new JsonArray(created.Select(value => (JsonNode)value).ToArray()),
                ["entry_ids"] = new JsonArray(affected.Select(value => (JsonNode)value).ToArray()), ["preserved_records"] = preserved, ["blocked_pairs"] = blocked,
                ["anchor_source_id"] = anchor.Key.SourceId, ["anchor_source_manga_id"] = anchor.Key.MangaId, ["anchor_title"] = anchor.Row.Text("source_title"),
            };
        }
    }

    /// <summary>schedule_entry_automation_redo as a visible job: pause automatic duplicate matching, prepare the redo,
    /// then search and refresh every resulting entry. One redo per entry at a time (merged).</summary>
    public JobHandle ScheduleEntryAutomationRedo(JobQueue jobs, long entryId, IEntryAutomation automation, IAutomationRedoEvidence evidence,
        IDecisionLog decisions, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(jobs); ArgumentNullException.ThrowIfNull(automation);
        var (canonical, title) = _database.Read(DatabaseFile.Catalog, c =>
        {
            var id = Canonical(c, entryId);
            return (id, c.Scalar("SELECT preferred_title FROM entries WHERE id=$p0", P(id)) as string ?? throw new KeyNotFoundException("Entry not found"));
        });
        return jobs.Enqueue(RedoJob, canonical.ToString(CultureInfo.InvariantCulture), async job =>
        {
            var token = job.CancellationToken;
            await using var pause = (await automation.PauseDuplicateMatchingAsync("redo-entry:" + canonical.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false)).ConfigureAwait(false);
            job.Report(0, 4, "Resetting automatic relationships");
            var prep = await Task.Run(() => PrepareAutomationRedo(canonical, evidence, decisions, actor, token), token).ConfigureAwait(false);
            var ids = prep["entry_ids"]!.AsArray().Select(value => value!.GetValue<long>()).Distinct().ToArray();
            var total = Math.Max(2, 2 + ids.Length * 2); var progress = 1; var splits = prep["split_records"]!.GetValue<int>();
            job.Report(progress, total, "Clean anchor: " + prep["anchor_title"]?.GetValue<string>() + " · split " + splits.ToString(CultureInfo.InvariantCulture) + " attached record" + (splits == 1 ? "" : "s"));
            var touched = new HashSet<long>();
            foreach (var raw in ids)
            {
                var id = _database.Read(DatabaseFile.Catalog, c => { var value = Canonical(c, raw); return Exists(c, value) ? value : 0; });
                if (id == 0 || !touched.Add(id)) { progress += 2; continue; }
                job.Report(progress, total, "Re-searching sources");
                id = await automation.DiscoverAllSourceRecordsAsync(id, token).ConfigureAwait(false); progress++;
                job.Report(progress, total, "Refreshing entry");
                await automation.RefreshEntryAsync(id, token).ConfigureAwait(false); progress++;
            }
            job.Result("Automation redone · " + splits.ToString(CultureInfo.InvariantCulture) + " automatic attachment" + (splits == 1 ? "" : "s") + " re-evaluated");
        }, origin: "manual", title: "Redo entry automation", context: title);
    }

    private void RequireStandalone()
    {
        if (_database.HasCurrentThreadSession(DatabaseFile.Catalog) || _database.HasCurrentThreadSession(DatabaseFile.Origins))
            throw new InvalidOperationException("Catalogue structure work requires standalone catalogue work.");
    }
}
