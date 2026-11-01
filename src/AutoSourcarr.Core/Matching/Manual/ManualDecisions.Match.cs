using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Matching.Manual.ManualSql;

namespace AutoSourcarr.Core.Matching.Manual;

public sealed partial class ManualDecisions
{
    /// <summary>
    /// manual_match: the user chose this exact source record for the entry. <paramref name="mode"/> "replace" makes it the
    /// primary record of its source (other records of that source are detached), "add" keeps it as an additional
    /// record. With <paramref name="deferHydration"/> and a matching <paramref name="candidate"/> card the choice commits
    /// locally at once and <see cref="NeedsHydration"/> in the answer tells the caller to queue
    /// <see cref="HydrateManualMatchAsync"/>; otherwise details and chapters are fetched first.
    /// </summary>
    public async Task<JsonObject> ManualMatchAsync(long entryId, string sourceId, long sourceMangaId, string mode, JsonObject? candidate,
        bool deferHydration, DecisionActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (mode is not ("replace" or "add")) throw new ArgumentException("mode must be replace or add", nameof(mode));
        var operations = RequireOperations();
        if (!Read(c => EntryExists(c, Canonical(c, entryId)))) throw new KeyNotFoundException("Entry not found");
        var manga = deferHydration ? ManualCandidateManga(sourceMangaId, candidate) : new JsonObject();
        var local = manga.Count > 0;
        var chapters = new JsonArray();
        if (!local)
        {
            var fetched = await operations.FetchMangaAsync(sourceMangaId, true, cancellationToken).ConfigureAwait(false);
            manga = fetched?["manga"] is JsonObject details && details.Count > 0 ? (JsonObject)details.DeepClone()
                : throw new ManualDecisionConflictException("Source manga not found");
            if (fetched?["chapters"] is JsonArray list) chapters = (JsonArray)list.DeepClone();
        }
        return CommitManualMatch(operations, entryId, sourceId, sourceMangaId, mode, manga, chapters, local, actor);
    }

    /// <summary>The answer of <see cref="ManualMatchAsync"/> asks for a background hydration of the chosen record.</summary>
    public static bool NeedsHydration(JsonObject result) => result?["background_hydration"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private JsonObject CommitManualMatch(IManualCatalogueOperations operations, long entryId, string sourceId, long sourceMangaId, string mode,
        JsonObject manga, JsonArray chapters, bool local, DecisionActor actor)
    {
        lock (_gate)
        {
            var key = RecordKey(sourceId, sourceMangaId);
            var state = Write(c =>
            {
                var target = Canonical(c, entryId);
                if (!EntryExists(c, target)) throw new KeyNotFoundException("Entry not found");
                // An explicit manual match supersedes an earlier "Separate entry" decision for this exact record.
                ForgetSeparate(c, target, sourceId, sourceMangaId);
                var owner = Owner(c, sourceId, sourceMangaId);
                var primary = c.Scalar("SELECT source_manga_id FROM source_matches WHERE entry_id=$p0 AND source_id=$p1 AND active=1", P(target, sourceId));
                var variants = c.QueryAs<long>("SELECT source_manga_id FROM source_variants WHERE entry_id=@target AND source_id=@sourceId AND active=1", new { target, sourceId }).ToArray();
                var before = new JsonObject
                {
                    ["primary"] = primary is null ? null : (long?)Convert.ToInt64(primary, CultureInfo.InvariantCulture),
                    ["variants"] = JsonList(variants.Select(id => (JsonNode?)id)), ["owner"] = owner,
                };
                return (Target: target, Forced: owner is long previous && previous != target ? previous : 0L,
                    Primary: primary is null ? (long?)null : Convert.ToInt64(primary, CultureInfo.InvariantCulture), Variants: variants, Before: before);
            });
            var target = state.Target;
            var sourceName = Read(c => SourceName(c, sourceId, ""));
            if (mode == "add" && state.Primary is not null)
            {
                operations.UpsertVariant(target, sourceId, manga, 1.0, "manual", true);
                if (!local) { operations.IngestChapters(target, sourceId, sourceMangaId, chapters); operations.ReconcileChapters(target); }
                operations.ChooseMetadata(target);
            }
            else
            {
                var detached = state.Variants.Append(state.Primary ?? 0).Where(id => id > 0 && id != sourceMangaId).ToHashSet();
                if (detached.Count > 0)
                {
                    Write(c => { foreach (var id in detached) ForgetBinding(c, sourceId, id, dissolveSmallGroup: true); return 0; });
                    operations.PurgeSourceRecordTitles(target, sourceId, detached);
                }
                if (state.Primary is long old && old != sourceMangaId) DeactivateCandidates(target, sourceId, old);
                // Replacing the primary is deliberate: other records of this source on the entry are detached.
                Write(c => c.Execute("UPDATE source_variants SET active=0,last_checked=$p0 WHERE entry_id=$p1 AND source_id=$p2", P(Stamp(), target, sourceId)));
                operations.UpsertPrimary(target, sourceId, manga, 1.0, "manual", true);
                if (!local) { operations.IngestChapters(target, sourceId, sourceMangaId, chapters); operations.ReconcileChapters(target); }
                operations.ChooseMetadata(target);
                if (detached.Count > 0)
                    operations.Invalidate(detached.Select(id => "source-" + id.ToString(CultureInfo.InvariantCulture)).Append("entry-" + target.ToString(CultureInfo.InvariantCulture)).ToArray());
            }
            Write(c =>
            {
                // A forced move also leaves the previous durable group, so a later reset cannot merge it back.
                if (state.Forced > 0) ForgetBinding(c, sourceId, sourceMangaId, dissolveSmallGroup: true);
                PersistBinding(c, target, sourceId, manga, sourceName);
                Log(mode == "add" ? "manual.match.add" : "manual.match", actor, [target, state.Forced], [key],
                    state.Forced > 0 ? "Manual match moved the record from another entry" : "Manual match", state.Before,
                    new JsonObject { ["entry_id"] = target, ["source_manga_id"] = sourceMangaId, ["mode"] = mode });
                return 0;
            });
            return new JsonObject
            {
                ["id"] = target, ["entry_id"] = target, ["source_id"] = sourceId, ["source_manga_id"] = sourceMangaId, ["mode"] = mode,
                ["background_hydration"] = local,
                ["forced_from_entry_ids"] = state.Forced > 0 ? new JsonArray(state.Forced) : new JsonArray(),
            };
        }
    }

    /// <summary>
    /// schedule_manual_match_hydration's work: fetches details and chapters of a record committed from a search card and
    /// stores them, unless the user changed or cleared the match meanwhile. Run it as a job (the caller queues it).
    /// </summary>
    public async Task<bool> HydrateManualMatchAsync(long entryId, string sourceId, long sourceMangaId, CancellationToken cancellationToken = default)
    {
        var operations = RequireOperations();
        var fetched = await operations.FetchMangaAsync(sourceMangaId, true, cancellationToken).ConfigureAwait(false);
        if (fetched?["manga"] is not JsonObject details || details.Count == 0) return false;
        var manga = (JsonObject)details.DeepClone();
        var chapters = fetched?["chapters"] is JsonArray list ? (JsonArray)list.DeepClone() : new JsonArray();
        lock (_gate)
        {
            var (target, primary, variant, sourceName) = Read(c =>
            {
                var id = Canonical(c, entryId);
                return (id,
                    c.Scalar("SELECT 1 FROM source_matches WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2 AND active=1 AND locked=1", P(id, sourceId, sourceMangaId)) is not null,
                    c.Scalar("SELECT 1 FROM source_variants WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2 AND active=1 AND locked=1", P(id, sourceId, sourceMangaId)) is not null,
                    SourceName(c, sourceId, ""));
            });
            // The user may have changed or cleared the match while the details were loading.
            if (!primary && !variant) return false;
            if (primary) operations.UpsertPrimary(target, sourceId, manga, 1.0, "manual", true);
            else operations.UpsertVariant(target, sourceId, manga, 1.0, "manual", true);
            operations.IngestChapters(target, sourceId, sourceMangaId, chapters);
            operations.ReconcileChapters(target);
            operations.ChooseMetadata(target);
            Write(c => PersistBinding(c, target, sourceId, manga, sourceName));
            operations.Invalidate(["entry-" + target.ToString(CultureInfo.InvariantCulture), "source-" + sourceMangaId.ToString(CultureInfo.InvariantCulture)]);
            return true;
        }
    }
}
