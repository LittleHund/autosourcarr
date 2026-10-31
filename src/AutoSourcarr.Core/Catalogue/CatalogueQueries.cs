using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue;

/// <summary>Local catalogue reads. No source request, automatic match or invented binding occurs here.</summary>
public sealed partial class CatalogueQueries(SqliteDatabase database, object sourceWriteGate, Func<long, bool> cachedCover)
{
    public IReadOnlyList<JsonObject> Sources()
    {
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, session => session.Query("""
            SELECT sc.*,sg.name group_name,sg.priority group_priority
            FROM source_config sc LEFT JOIN source_groups sg ON sg.id=sc.group_id
            ORDER BY sc.priority,sc.name COLLATE NOCASE,sc.lang COLLATE NOCASE
            """).Where(row => row.Flag("available")).Select(row =>
            {
                var value = Json(row);
                foreach (var key in new[] { "supports_latest", "enabled_language", "enabled_search", "enabled_chapters", "available", "official", "official_manual" }) value[key] = row.Flag(key);
                return value;
            }).ToArray());
    }

    public IReadOnlyList<JsonObject> SourceGroups()
    {
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, session => session.Query("""
            SELECT sg.id,sg.name,sg.priority,sg.created_at,sg.updated_at,COUNT(sc.source_id) member_variants
            FROM source_groups sg LEFT JOIN source_config sc ON sc.group_id=sg.id
            GROUP BY sg.id ORDER BY sg.priority,sg.name COLLATE NOCASE
            """).Select(Json).ToArray());
    }

    public IReadOnlyList<JsonObject> Summaries(IReadOnlyList<long> entryIds)
    {
        if (entryIds.Count == 0) return [];
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, session => Summaries(session, entryIds));
    }

    private IReadOnlyList<JsonObject> Summaries(DatabaseSession session, IReadOnlyList<long> requested)
    {
        var canonical = CanonicalMap(session, requested); var ids = canonical.Values.Distinct().ToArray();
        var marks = Marks(ids.Length); var parameters = Parameters(ids.Cast<object?>());
        var entries = session.Query($"SELECT * FROM entries WHERE id IN ({marks})", parameters).ToDictionary(row => row.Integer("id"));
        var matches = session.Query($"""
            SELECT sm.entry_id,COALESCE(sc.name,sm.source_id) name,COALESCE(sc.base_url,'') base_url,
                   COALESCE(sc.lang,'') lang,COALESCE(sc.priority,1000) priority
            FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id
            WHERE sm.active=1 AND sm.entry_id IN ({marks}) ORDER BY sm.entry_id,priority,name
            """, parameters).GroupBy(row => row.Integer("entry_id")).ToDictionary(group => group.Key, group => group.ToArray());
        var counts = session.Query($"""
            SELECT entry_id,COUNT(*) record_count FROM (
                SELECT entry_id FROM source_matches WHERE active=1 AND entry_id IN ({marks})
                UNION ALL SELECT entry_id FROM source_variants WHERE active=1 AND entry_id IN ({marks})
            ) records GROUP BY entry_id
            """, parameters).ToDictionary(row => row.Integer("entry_id"), row => row.Integer("record_count"));
        var result = new List<JsonObject>();
        foreach (var requestedId in requested)
        {
            var id = canonical[requestedId]; if (!entries.TryGetValue(id, out var entry)) continue;
            var families = CataloguePresentation.SourceFamilies(matches.GetValueOrDefault(id, []).Select(row => new CatalogueSourceDisplay(row.Text("name"), row.Text("base_url"), row.Text("lang"), row.Integer("priority"))));
            var value = Json(entry); var missing = CataloguePresentation.MissingMetadata(value);
            value["match_count"] = families.Count; value["entry_match_count"] = counts.GetValueOrDefault(id);
            value["sources"] = Strings(families); value["status_label"] = CataloguePresentation.StatusLabel(entry.Integer("status"));
            value["url"] = requestedId.ToString(System.Globalization.CultureInfo.InvariantCulture); value["canonical_id"] = id;
            value["cover_url"] = FormattableString.Invariant($"/api/v1/covers/{requestedId}"); value["cover_cached"] = cachedCover(id);
            value["metadata_complete"] = missing.Count == 0; value["missing_metadata"] = Strings(missing); result.Add(value);
        }
        return result;
    }

    public JsonObject Entry(long entryId)
    {
        lock (sourceWriteGate)
        {
            var value = database.Read(DatabaseFile.Catalog, session =>
            {
                var id = PortableIdentityResolver.Canonical(session, entryId);
                var value = Summaries(session, [entryId]).SingleOrDefault() ?? throw new KeyNotFoundException("Entry not found");
                value["titles"] = Rows(session.Query("SELECT * FROM titles WHERE entry_id=$p0 ORDER BY title_type,title COLLATE NOCASE", ("$p0", id)));
                value["matches"] = Rows(session.Query("""
                    SELECT sm.*,sc.name source_name,sc.lang source_lang,sc.priority source_priority,sc.base_url base_url
                    FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id
                    WHERE sm.entry_id=$p0 AND sm.active=1 ORDER BY sc.priority,sm.source_title
                    """, ("$p0", id)));
                value["source_variants"] = Rows(session.Query("""
                    SELECT sv.*,sc.name source_name,sc.lang source_lang,sc.priority source_priority,sc.base_url base_url
                    FROM source_variants sv LEFT JOIN source_config sc ON sc.source_id=sv.source_id
                    WHERE sv.entry_id=$p0 AND sv.active=1 ORDER BY sc.priority,sv.source_title
                    """, ("$p0", id)));
                value["identities"] = Rows(session.Query("""
                    SELECT provider,provider_name,external_id,external_url,identity_scope,confidence,match_type,locked,
                           title,description,thumbnail_url,status,author,artist,genre,media_type,country,year,adult,last_checked
                    FROM identity_links WHERE entry_id=$p0 ORDER BY CASE provider
                    WHEN 'mangadex' THEN 10 WHEN 'anilist' THEN 20 WHEN 'mangaupdates' THEN 30
                    WHEN 'kitsu' THEN 40 WHEN 'myanimelist' THEN 50 WHEN 'comicvine' THEN 60 ELSE 999 END,provider
                    """, ("$p0", id)));
                return value;
            });
            var canonical = value["canonical_id"]!.GetValue<long>(); var stats = RecordChapterStats([canonical]);
            foreach (var row in value["matches"]!.AsArray().Concat(value["source_variants"]!.AsArray()).OfType<JsonObject>())
            {
                var key = (canonical, row["source_id"]!.GetValue<string>(), row["source_manga_id"]!.GetValue<long>());
                var stat = stats.GetValueOrDefault(key) ?? EmptyRecordStats();
                foreach (var field in stat) row[field.Key] = field.Value?.DeepClone();
            }
            value["description_for_extension"] = CataloguePresentation.DescriptionForExtension(value); return value;
        }
    }

    private static Dictionary<long, long> CanonicalMap(DatabaseSession session, IEnumerable<long> requested)
    {
        // A local per-read map avoids N+1 queries and cannot publish a stale global alias cache.
        var aliases = session.Query("SELECT old_entry_id,canonical_entry_id FROM entry_aliases").ToDictionary(row => row.Integer("old_entry_id"), row => row.Integer("canonical_entry_id"));
        var result = new Dictionary<long, long>();
        foreach (var id in requested)
        {
            var current = id; var path = new HashSet<long>();
            while (aliases.TryGetValue(current, out var next))
            { if (!path.Add(current)) throw new InvalidDataException("Entry alias cycle; catalogue identity requires repair"); current = next; }
            result[id] = current;
        }
        return result;
    }
    private static JsonObject Json(DatabaseRow row) => JsonSerializer.SerializeToNode(row)!.AsObject();
    private static JsonArray Rows(IEnumerable<DatabaseRow> rows) => new(rows.Select(row => (JsonNode)Json(row)).ToArray());
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    private static string Marks(int count, int start = 0) => string.Join(',', Enumerable.Range(start, count).Select(index => "$p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    private static (string, object?)[] Parameters(IEnumerable<object?> values) => values.Select((value, index) => ("$p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), value)).ToArray();
}
