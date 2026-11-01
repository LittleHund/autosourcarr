using System.Text.Json.Nodes;
using AutoSourcarr.Core.Reader;

namespace AutoSourcarr.Core.Catalogue.Chapters;

/// <summary>
/// CHAP-3/CHAP-4: each source stays its own source (its chapters stay its own candidates), but a source group shares one
/// priority (kept equal by the source settings) and, for serving, the group member whose record has the most chapters
/// for this title wins over the other members of its group. Candidates of other sources keep their order. A group is a
/// serving rule only: it has no source, WebView or record of its own.
/// </summary>
public static class ServingGroups
{
    /// <summary>The bonus that lifts the winning member just above the group's best score (smaller than any real score step).</summary>
    public const double WinnerBonus = 0.001;

    /// <param name="ranked">Candidates already scored (<c>resolver_score</c>) and sorted best first; reordered in place.</param>
    /// <param name="groupOf">source_id → source group id, for grouped sources only.</param>
    /// <param name="chapterCounts">source_id → number of active chapters that source's record(s) give this title.</param>
    public static void Apply(List<JsonObject> ranked, IReadOnlyDictionary<string, long> groupOf, IReadOnlyDictionary<string, long> chapterCounts)
    {
        ArgumentNullException.ThrowIfNull(ranked); ArgumentNullException.ThrowIfNull(groupOf); ArgumentNullException.ThrowIfNull(chapterCounts);
        if (ranked.Count < 2 || groupOf.Count == 0) return;
        var changed = false;
        foreach (var group in ranked.Where(row => groupOf.ContainsKey(ReaderValue.Text(row, "source_id")))
                     .GroupBy(row => groupOf[ReaderValue.Text(row, "source_id")]))
        {
            var members = group.ToArray();
            if (members.Select(row => ReaderValue.Text(row, "source_id")).Distinct(StringComparer.Ordinal).Count() < 2) continue;
            var best = members.Max(row => ReaderValue.Number(row, "resolver_score"));
            // Most chapters wins; equal counts keep the resolver's own order (the first member is its best).
            var winner = members.Select((row, index) => (Row: row, Index: index))
                .OrderByDescending(item => chapterCounts.GetValueOrDefault(ReaderValue.Text(item.Row, "source_id")))
                .ThenBy(item => item.Index).First().Row;
            foreach (var row in members) row["source_group_id"] = group.Key;
            winner["source_group_winner"] = true;
            var lifted = best + WinnerBonus;
            if (ReaderValue.Number(winner, "resolver_score") < lifted) { winner["resolver_score"] = lifted; changed = true; }
        }
        if (!changed) return;
        var sorted = ranked.Select((row, index) => (Row: row, Index: index))
            .OrderByDescending(item => ReaderValue.Number(item.Row, "resolver_score")).ThenBy(item => item.Index).Select(item => item.Row).ToArray();
        ranked.Clear(); ranked.AddRange(sorted);
    }
}
