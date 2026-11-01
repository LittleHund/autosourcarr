using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Mixed;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Unicode;
using static AutoSourcarr.Core.Catalogue.Structure.StructureSql;

namespace AutoSourcarr.Core.Catalogue.Structure;

/// <summary>An old source record remembered as a candidate hint for a rebuild after a destructive reset. A hint is
/// never restored directly: it is fetched live and must pass the current matcher before it can attach.</summary>
public sealed record ResetHint(string SourceId, long MangaId, string SourceTitle, string MatchType, bool Locked, bool IsVariant, long PreviousEntryId);

/// <summary>Live re-ingestion of reset hints (implemented by the entry-ingest owner, 8A).</summary>
public interface IResetHintIngest
{
    /// <summary>Fetches the record from its source and ingests it with the current matcher; returns the entry it joined, or null.</summary>
    Task<long?> FetchAndIngestAsync(ResetHint hint, CancellationToken cancellationToken);
    /// <summary>Fetches the record and attaches it to <paramref name="entryId"/> only when the strict automatic evidence
    /// against that entry's pre-attachment trusted evidence accepts it ("reset-revalidated").</summary>
    Task<bool> RevalidateForEntryAsync(long entryId, ResetHint hint, CancellationToken cancellationToken);
}

/// <summary>Catalogue/Structure → CatalogueOriginRecovery (phase 8): pending origin reconciliation and reset rebuild hints.</summary>
public sealed partial class CatalogueStructure
{
    public const string ResetHintFile = "reset-rebuild-hints.json";
    public static readonly JobType ResetHintReplayJob = new("reset-hint-replay", "Revalidate previous catalog", JobPriority.Low, JobFlags.LongRunning, StallTimeout: TimeSpan.FromMinutes(10));

    /// <summary>reconcile_pending_entry_origins: finishes every journaled merge and split, counting instead of throwing.</summary>
    public JsonObject ReconcilePendingOrigins(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var merges = _database.Read(DatabaseFile.Catalog, c => c.Query("SELECT duplicate_entry_id FROM pending_entry_merges ORDER BY created_at,duplicate_entry_id").Select(row => row.Integer("duplicate_entry_id")).ToArray());
            var splits = _database.Read(DatabaseFile.Catalog, c => c.Query("SELECT id FROM pending_entry_splits ORDER BY created_at,id").Select(row => row.Integer("id")).ToArray());
            var completed = 0; var failed = 0;
            foreach (var id in merges) { cancellationToken.ThrowIfCancellationRequested(); try { FinishMerge(id); completed++; } catch (Exception error) when (error is not OperationCanceledException) { failed++; } }
            foreach (var id in splits) { cancellationToken.ThrowIfCancellationRequested(); try { FinishSplit(id); completed++; } catch (Exception error) when (error is not OperationCanceledException) { failed++; } }
            return new JsonObject { ["checked"] = merges.Length + splits.Length, ["completed"] = completed, ["failed"] = failed, ["remaining"] = PendingEntryOriginCount() };
        }
    }

    /// <summary>reset_rebuild_hints_for_entries: old records of the given entries, locked and manual/import ones first.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<ResetHint>> ResetRebuildHintsForEntries(IEnumerable<long> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        return _database.Read(DatabaseFile.Catalog, c =>
        {
            var ids = entryIds.Where(id => id > 0).Select(id => Canonical(c, id)).Distinct().ToArray();
            var result = new Dictionary<long, IReadOnlyList<ResetHint>>();
            if (ids.Length == 0) return result;
            var (names, parameters) = EditionProfiles.InList(ids);
            var rows = c.Query("""
                SELECT entry_id,source_id,source_manga_id,source_title,match_type,locked,0 is_variant FROM source_matches WHERE active=1 AND entry_id IN (NAMES)
                UNION ALL SELECT entry_id,source_id,source_manga_id,source_title,match_type,locked,1 is_variant FROM source_variants WHERE active=1 AND entry_id IN (NAMES)
                """.Replace("NAMES", names, StringComparison.Ordinal), parameters);
            foreach (var group in rows.GroupBy(row => row.Integer("entry_id")))
                result[group.Key] = group.Select(Hint).OrderBy(hint => hint.Locked ? 0 : 1).ThenBy(hint => hint.MatchType.ToLowerInvariant() is "manual" or "import" ? 0 : 1)
                    .ThenBy(hint => hint.IsVariant ? 1 : 0).ThenBy(hint => UnicodeCaseFold.Fold(hint.SourceTitle), UnicodeCodePointComparer.Instance).ToArray();
            return result;
        });
    }

    /// <summary>capture_reset_rebuild_hints: writes the compact hint file (no trusted merge state) in keyset pages, atomically.</summary>
    public JsonObject CaptureResetRebuildHints(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var path = Path.Combine(dataDirectory, ResetHintFile); var temporary = path + ".tmp"; var count = 0;
        var seen = new HashSet<NativeSourceRecord>();
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteString("created_at", Stamp()); writer.WriteStartArray("records");
            foreach (var (table, variant) in new[] { ("source_matches", false), ("source_variants", true) })
            {
                var after = 0L;
                while (true)
                {
                    var page = _database.Read(DatabaseFile.Catalog, c => c.Query("SELECT id,entry_id,source_id,source_manga_id,source_title,match_type,locked," + (variant ? "1" : "0") + " is_variant FROM " + table + " WHERE active=1 AND id>$p0 ORDER BY id LIMIT 1000", P(after)));
                    if (page.Count == 0) break;
                    foreach (var row in page)
                    {
                        var hint = Hint(row);
                        if (hint.SourceId.Length == 0 || hint.MangaId <= 0 || !seen.Add(new(hint.SourceId, hint.MangaId))) continue;
                        writer.WriteStartObject(); writer.WriteString("source_id", hint.SourceId); writer.WriteNumber("source_manga_id", hint.MangaId);
                        writer.WriteNumber("previous_entry_id", hint.PreviousEntryId); writer.WriteString("source_title", hint.SourceTitle);
                        writer.WriteString("match_type", hint.MatchType); writer.WriteBoolean("locked", hint.Locked); writer.WriteEndObject(); count++;
                    }
                    after = page[^1].Integer("id");
                }
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        File.Move(temporary, path, overwrite: true);
        return new JsonObject { ["path"] = path, ["records"] = count, ["bytes"] = new FileInfo(path).Length };
    }

    /// <summary>Reads a hint file written by <see cref="CaptureResetRebuildHints"/> (or by 0.4.x).</summary>
    public static IReadOnlyList<ResetHint> ReadResetHints(string path)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); } catch (Exception error) when (error is JsonException or IOException) { return []; }
        if (root?["records"] is not JsonArray records) return [];
        var result = new List<ResetHint>();
        foreach (var item in records.OfType<JsonObject>())
        {
            string Field(string name) => item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
            long Number(string name) => item[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
            var hint = new ResetHint(Field("source_id"), Number("source_manga_id"), Field("source_title"), Field("match_type"),
                item["locked"] is JsonValue locked && locked.TryGetValue<bool>(out var flag) && flag, false, Number("previous_entry_id"));
            if (hint.SourceId.Length != 0 && hint.MangaId > 0) result.Add(hint);
        }
        return result;
    }

    /// <summary>_revalidate_reset_hints_for_entry: checks every old record independently against the same anchor
    /// (an accepted hint never feeds the next one); records the user separated from this entry are skipped.</summary>
    public async Task<JsonObject> RevalidateResetHintsForEntryAsync(long entryId, IReadOnlyList<ResetHint> hints, IResetHintIngest ingest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hints); ArgumentNullException.ThrowIfNull(ingest);
        var current = _database.Read(DatabaseFile.Catalog, c => { var id = Canonical(c, entryId); return Exists(c, id) ? id : 0; });
        var checkedCount = 0; var accepted = 0; var errors = 0; var acceptedSources = new SortedSet<string>(StringComparer.Ordinal); var rejected = new SortedSet<string>(StringComparer.Ordinal);
        if (current != 0)
            foreach (var hint in hints.Where(item => item.SourceId.Length != 0 && item.MangaId > 0))
            {
                cancellationToken.ThrowIfCancellationRequested(); checkedCount++;
                var separate = _database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT 1 FROM manual_separate_records WHERE target_entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2", P(current, hint.SourceId, hint.MangaId)) is not null);
                if (separate) { rejected.Add(hint.SourceId); continue; }
                bool ok;
                try { ok = await ingest.RevalidateForEntryAsync(current, hint, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException) { errors++; rejected.Add(hint.SourceId); continue; }
                if (ok) { accepted++; acceptedSources.Add(hint.SourceId); } else rejected.Add(hint.SourceId);
            }
        if (accepted != 0) ReconcileEntry(current);
        rejected.ExceptWith(acceptedSources);
        return new JsonObject
        {
            ["checked"] = checkedCount, ["accepted"] = accepted, ["errors"] = errors,
            ["accepted_sources"] = new JsonArray(acceptedSources.Select(value => (JsonNode)value).ToArray()), ["rejected_sources"] = new JsonArray(rejected.Select(value => (JsonNode)value).ToArray()),
        };
    }

    /// <summary>schedule_reset_hint_replay as a job: re-ingests the pre-reset catalogue from exact old source ids in
    /// small batches with progress, then deletes the hint file. Null when there is no file or nothing in it.</summary>
    public JobHandle? ScheduleResetHintReplay(JobQueue jobs, string path, IResetHintIngest ingest)
    {
        ArgumentNullException.ThrowIfNull(jobs); ArgumentNullException.ThrowIfNull(ingest);
        if (!File.Exists(path)) return null;
        var records = ReadResetHints(path);
        if (records.Count == 0) { File.Delete(path); return null; }
        return jobs.Enqueue(ResetHintReplayJob, "catalogue", async job =>
        {
            var done = 0; var restored = 0; var errors = 0;
            foreach (var batch in records.Chunk(16))
            {
                foreach (var hint in batch)
                {
                    job.CancellationToken.ThrowIfCancellationRequested();
                    try { if (await ingest.FetchAndIngestAsync(hint, job.CancellationToken).ConfigureAwait(false) is not null) restored++; }
                    catch (Exception error) when (error is not OperationCanceledException) { errors++; }
                    done++;
                }
                job.Report(done, records.Count, "Revalidated " + done.ToString(CultureInfo.InvariantCulture) + "/" + records.Count.ToString(CultureInfo.InvariantCulture)
                    + " old source ids · " + restored.ToString(CultureInfo.InvariantCulture) + " accepted · " + errors.ToString(CultureInfo.InvariantCulture) + " unavailable/changed");
            }
            File.Delete(path);
            job.Result("Previous catalog revalidated · " + restored.ToString(CultureInfo.InvariantCulture) + "/" + records.Count.ToString(CultureInfo.InvariantCulture) + " source records accepted under current rules");
        }, origin: "automatic", title: "Revalidate previous catalog");
    }

    private static ResetHint Hint(DatabaseRow row) => new(row.Text("source_id"), row.Integer("source_manga_id"), row.Text("source_title"), row.Text("match_type"),
        row.Flag("locked"), row.Flag("is_variant"), row.Integer("entry_id"));
}
