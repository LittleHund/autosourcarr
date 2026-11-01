using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Matching.Scoring;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Matching.Ingest;

/// <summary>
/// Additional-record discovery helpers (port-map "Sources/Discovery", 2 methods): the strict evidence gate for a
/// discovered record against an entry, and the owners of many records in a few grouped queries.
/// </summary>
public sealed class SourceDiscovery(EntryIngest ingest)
{
    /// <summary>
    /// _source_match_evidence: anchored to the entry's oldest record plus manual / main-library / locked evidence (a previous
    /// automatic match or provider identity never authorizes the next one). <paramref name="trusted"/> reuses a snapshot.
    /// </summary>
    public ScoredMatchDecision SourceMatchEvidence(long entryId, string sourceId, JsonObject manga, TrustedEntryEvidence? trusted = null)
    {
        ArgumentNullException.ThrowIfNull(manga);
        return ingest.WithRead(session => ingest.SourceMatchEvidence(session, CatalogueTitles.Canonical(session, entryId), manga, sourceId, trusted));
    }

    /// <summary>_source_record_owners: canonical owner entry per (source, manga) pair; unowned pairs are absent.</summary>
    public IReadOnlyDictionary<(string SourceId, long MangaId), long> SourceRecordOwners(IEnumerable<(string SourceId, long MangaId)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var grouped = pairs.GroupBy(pair => pair.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.MangaId).Distinct().Order().ToArray(), StringComparer.Ordinal);
        if (grouped.Count == 0) return new Dictionary<(string, long), long>();
        return ingest.WithRead(session =>
        {
            var owners = new Dictionary<(string SourceId, long MangaId), long>();
            foreach (var (sourceId, mangaIds) in grouped)
                foreach (var chunk in mangaIds.Chunk(400))
                {
                    var list = string.Join(',', chunk.Select(id => id.ToString(CultureInfo.InvariantCulture)));
                    foreach (var row in session.Query("SELECT source_manga_id,entry_id FROM source_matches WHERE source_id=$source AND active=1 AND source_manga_id IN (" + list + ")"
                        + " UNION ALL SELECT source_manga_id,entry_id FROM source_variants WHERE source_id=$source AND active=1 AND source_manga_id IN (" + list + ")", ("$source", sourceId)))
                        owners.TryAdd((sourceId, row.Integer("source_manga_id")), CatalogueTitles.Canonical(session, row.Integer("entry_id")));
                }
            return owners;
        });
    }
}
