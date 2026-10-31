using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Text;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue;

public sealed partial class CatalogueQueries
{
    public IReadOnlyList<long> SearchEntryIds(string search, int limit = 200, IReadOnlyList<long>? restrictIds = null, IReadOnlyList<string>? sourceIds = null)
    {
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, session => Search(session, search, limit, restrictIds, sourceIds));
    }

    private static IReadOnlyList<long> Search(DatabaseSession session, string search, int limit, IReadOnlyList<long>? restrictIds, IReadOnlyList<string>? sourceIds)
    {
        var query = UnicodeText.Trim(search); var normalized = TextMatcher.NormalizeTitle(query); if (normalized.Length == 0) return [];
        var wanted = (restrictIds ?? []).Distinct().ToArray(); var rawLike = "%" + UnicodeCaseFold.Fold(query) + "%"; var normLike = "%" + normalized + "%";
        var ids = new List<long>();
        if (sourceIds is { Count: > 0 })
        {
            var sources = sourceIds.Distinct(StringComparer.Ordinal).ToArray(); var marks = Marks(sources.Length);
            var restriction = wanted.Length == 0 ? "" : $" AND e.id IN ({Marks(wanted.Length, sources.Length)})";
            var values = sources.Cast<object?>().Concat(wanted.Cast<object?>()).ToList(); var raw = "$p" + values.Count; values.Add(rawLike); var norm = "$p" + values.Count; values.Add(normLike); var cap = "$p" + values.Count; values.Add(limit);
            var rows = session.Query($"""
                WITH linked AS (SELECT entry_id FROM source_matches WHERE active=1 AND source_id IN ({marks})
                    UNION SELECT entry_id FROM source_variants WHERE active=1 AND source_id IN ({marks}))
                SELECT e.id FROM entries e WHERE e.id IN (SELECT entry_id FROM linked) {restriction}
                    AND (lower(e.preferred_title) LIKE {raw} OR EXISTS(SELECT 1 FROM titles t WHERE t.entry_id=e.id AND t.normalized LIKE {norm})
                    OR EXISTS(SELECT 1 FROM source_matches sm WHERE sm.entry_id=e.id AND sm.active=1 AND lower(sm.source_title) LIKE {raw})
                    OR EXISTS(SELECT 1 FROM source_variants sv WHERE sv.entry_id=e.id AND sv.active=1 AND lower(sv.source_title) LIKE {raw}))
                ORDER BY e.updated_at DESC,e.id DESC LIMIT {cap}
                """, Parameters(values));
            return rows.Select(row => PortableIdentityResolver.Canonical(session, row.Integer("id"))).Distinct().ToArray();
        }
        var restrict = wanted.Length == 0 ? "" : $" AND entry_id IN ({Marks(wanted.Length, 1)})";
        var aliasParameters = new List<object?> { normLike }; aliasParameters.AddRange(wanted.Cast<object?>()); aliasParameters.Add(limit);
        ids.AddRange(session.Query($"SELECT DISTINCT entry_id FROM titles WHERE normalized LIKE $p0{restrict} LIMIT $p{aliasParameters.Count - 1}", Parameters(aliasParameters)).Select(row => row.Integer("entry_id")));
        foreach (var table in new[] { "entries", "source_matches", "source_variants" })
        {
            var column = table == "entries" ? "id" : "entry_id"; var title = table == "entries" ? "preferred_title" : "source_title";
            var predicate = table == "entries" ? "" : "active=1 AND "; var values = wanted.Cast<object?>().ToList();
            if (wanted.Length != 0) predicate += $"{column} IN ({Marks(wanted.Length)}) AND ";
            var raw = "$p" + values.Count; values.Add(rawLike); var cap = "$p" + values.Count; values.Add(limit);
            ids.AddRange(session.Query($"SELECT {(table == "entries" ? "" : "DISTINCT ")}{column} FROM {table} WHERE {predicate}lower({title}) LIKE {raw} LIMIT {cap}", Parameters(values)).Select(row => row.Integer(column)));
        }
        var found = ids.Select(id => PortableIdentityResolver.Canonical(session, id)).Distinct().ToArray();
        // SQLite LIMIT<0 means unlimited. Public callers retain their original validated positive caps.
        return wanted.Length == 0 ? Slice(found, limit) : Slice(wanted.Where(found.ToHashSet().Contains), limit);
    }

    public IReadOnlyList<long> ExactSearchEntryIds(string search, int limit = 200, IReadOnlyList<string>? sourceIds = null)
    {
        var normalized = TextMatcher.NormalizeTitle(UnicodeText.Trim(search)); if (normalized.Length == 0) return [];
        lock (sourceWriteGate) return database.Write(DatabaseFile.Catalog, session =>
        {
            var ids = session.Query("SELECT DISTINCT entry_id FROM titles WHERE normalized=$p0 LIMIT $p1", ("$p0", normalized), ("$p1", limit)).Select(row => row.Integer("entry_id")).ToList();
            foreach (var row in session.Query("SELECT entry_id,raw_title FROM entry_preferred_titles WHERE normalized IS NULL"))
                session.Execute("UPDATE entry_preferred_titles SET normalized=$normalized WHERE entry_id=$entry AND raw_title=$raw AND normalized IS NULL",
                    ("$normalized", TextMatcher.NormalizeTitle(row.Text("raw_title"))), ("$entry", row.Integer("entry_id")), ("$raw", row.Text("raw_title")));
            foreach (var row in session.Query("""
                SELECT e.id,p.raw_title,p.normalized FROM entry_preferred_titles p JOIN entries e ON e.id=p.entry_id
                WHERE p.normalized=$p0 OR p.normalized IS NULL ORDER BY e.updated_at DESC,e.id DESC
                """, ("$p0", normalized)))
            {
                var value = row["normalized"] is null ? TextMatcher.NormalizeTitle(row.Text("raw_title")) : row.Text("normalized");
                if (value != normalized) continue; ids.Add(row.Integer("id")); if (ids.Count >= limit) break;
            }
            var canonical = ids.Select(id => PortableIdentityResolver.Canonical(session, id)).Distinct().ToArray();
            return Slice(Scope(session, canonical, sourceIds), limit);
        });
    }

    public IReadOnlyList<long> SourceScopedEntryIds(IReadOnlyList<long> entryIds, IReadOnlyList<string>? sourceIds)
    {
        if (entryIds.Count == 0 || sourceIds is null || sourceIds.Count == 0) return entryIds.ToArray();
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, session => Scope(session, entryIds, sourceIds));
    }
    private static IReadOnlyList<long> Scope(DatabaseSession session, IReadOnlyList<long> entryIds, IReadOnlyList<string>? sourceIds)
    {
        if (entryIds.Count == 0 || sourceIds is null || sourceIds.Count == 0) return entryIds.ToArray();
        var sources = sourceIds.Distinct(StringComparer.Ordinal).ToArray(); var ids = entryIds.Distinct().ToArray();
        var marks = Marks(sources.Length); var entries = Marks(ids.Length, sources.Length);
        var linked = session.Query($"""
            SELECT entry_id FROM source_matches WHERE active=1 AND source_id IN ({marks}) AND entry_id IN ({entries})
            UNION SELECT entry_id FROM source_variants WHERE active=1 AND source_id IN ({marks}) AND entry_id IN ({entries})
            """, Parameters(sources.Cast<object?>().Concat(ids.Cast<object?>()))).Select(row => row.Integer("entry_id")).ToHashSet();
        return entryIds.Where(linked.Contains).ToArray();
    }
    public IReadOnlyList<JsonObject> List(string search = "", int limit = 200)
    {
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, session =>
        {
            var ids = UnicodeText.Trim(search).Length == 0 ? session.Query("SELECT id FROM entries ORDER BY updated_at DESC LIMIT $p0", ("$p0", limit)).Select(row => row.Integer("id")).ToArray() : Search(session, search, limit, null, null);
            return ids.Count == 0 ? [] : Summaries(session, ids);
        });
    }
    public IReadOnlyList<JsonObject> ByIds(IReadOnlyList<long> entryIds, string search = "", int limit = 500)
    {
        if (entryIds.Count == 0) return [];
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, session =>
        {
            IReadOnlyList<long> ids = CanonicalMap(session, entryIds).Values.Distinct().ToArray();
            if (UnicodeText.Trim(search).Length != 0) ids = Search(session, search, Math.Max(limit, ids.Count), ids, null);
            var selected = Slice(ids, limit); return selected.Count == 0 ? [] : Summaries(session, selected);
        });
    }
    private static IReadOnlyList<T> Slice<T>(IEnumerable<T> source, int end)
    {
        var values = source.ToArray(); var length = end < 0 ? Math.Max(0, values.Length + end) : Math.Min(end, values.Length);
        return values.Take(length).ToArray();
    }
}
