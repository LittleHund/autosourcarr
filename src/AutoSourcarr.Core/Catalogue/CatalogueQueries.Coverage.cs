using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue;

public sealed partial class CatalogueQueries
{
    public IReadOnlyDictionary<(long EntryId, string SourceId, long MangaId), JsonObject> RecordChapterStats(IReadOnlyList<long> requested)
    {
        lock (sourceWriteGate)
        {
            var ids = database.Read(DatabaseFile.Catalog, session => CanonicalMap(session, requested.Where(id => id > 0)).Values.Distinct().ToArray());
            if (ids.Length == 0) return new Dictionary<(long, string, long), JsonObject>();
            return database.Read(DatabaseFile.Origins, session =>
            {
                var marks = Marks(ids.Length); var parameters = Parameters(ids.Cast<object?>());
                var rows = session.Query($"""
                    SELECT vc.entry_id,cc.source_id,cc.source_manga_id,
                           COUNT(DISTINCT cc.source_chapter_id) chapter_count,
                           MAX(CASE WHEN cc.chapter_number>=0 THEN cc.chapter_number END) latest_chapter,
                           MAX(CASE WHEN cc.chapter_number>=1 THEN CAST(cc.chapter_number AS INTEGER) END) latest_whole,
                           COUNT(DISTINCT CASE WHEN cc.chapter_number>=1
                              AND ABS(cc.chapter_number-CAST(cc.chapter_number AS INTEGER))<0.000001
                              THEN CAST(cc.chapter_number AS INTEGER) END) whole_count
                    FROM chapter_candidates cc JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id
                    WHERE cc.active=1 AND vc.entry_id IN ({marks})
                    GROUP BY vc.entry_id,cc.source_id,cc.source_manga_id
                    """, parameters);
                var result = new Dictionary<(long, string, long), JsonObject>();
                foreach (var scan in session.Query($"SELECT entry_id,source_id,source_manga_id,checked_at,chapter_count,error FROM source_record_scans WHERE entry_id IN ({marks})", parameters))
                    result[(scan.Integer("entry_id"), scan.Text("source_id"), scan.Integer("source_manga_id"))] = new()
                    {
                        ["chapter_count"] = scan.Integer("chapter_count"), ["latest_chapter"] = null, ["missing_chapter_count"] = 0,
                        ["chapters_checked"] = true, ["chapter_checked_at"] = Node(scan["checked_at"]), ["chapter_check_error"] = scan.Text("error")
                    };
                foreach (var row in rows)
                {
                    var key = (row.Integer("entry_id"), row.Text("source_id"), row.Integer("source_manga_id"));
                    var previous = result.GetValueOrDefault(key); var latestWhole = row.Integer("latest_whole");
                    result[key] = new()
                    {
                        ["chapter_count"] = row.Integer("chapter_count"), ["latest_chapter"] = row["latest_chapter"] is null ? null : JsonValue.Create(row.Number("latest_chapter")),
                        ["missing_chapter_count"] = latestWhole > 0 ? Math.Max(0, latestWhole - row.Integer("whole_count")) : 0,
                        ["chapters_checked"] = previous?["chapters_checked"]?.GetValue<bool>() ?? false,
                        ["chapter_checked_at"] = previous?["chapter_checked_at"]?.DeepClone(), ["chapter_check_error"] = previous?["chapter_check_error"]?.DeepClone() ?? JsonValue.Create("")
                    };
                }
                return result;
            });
        }
    }

    public IReadOnlyDictionary<long, JsonObject> EntryChapterStats(IReadOnlyList<long> requested)
    {
        lock (sourceWriteGate)
        {
            var (ids, enabled) = database.Read(DatabaseFile.Catalog, session =>
                (CanonicalMap(session, requested.Where(id => id > 0)).Values.Distinct().ToArray(),
                session.Query("SELECT source_id FROM source_config WHERE available=1 AND enabled_language=1 AND enabled_chapters=1").Select(row => row.Text("source_id")).Order(StringComparer.Ordinal).ToArray()));
            if (ids.Length == 0) return new Dictionary<long, JsonObject>();
            return database.Read(DatabaseFile.Origins, session =>
            {
                var result = ids.ToDictionary(id => id, _ => new JsonObject { ["served_chapter_count"] = 0, ["served_latest_chapter"] = null, ["chapter_records_checked"] = false });
                var marks = Marks(ids.Length); var parameters = Parameters(ids.Cast<object?>());
                foreach (var row in session.Query($"SELECT entry_id,COUNT(*) scan_count FROM source_record_scans WHERE entry_id IN ({marks}) GROUP BY entry_id", parameters))
                    result[row.Integer("entry_id")]["chapter_records_checked"] = row.Integer("scan_count") > 0;
                if (enabled.Length != 0)
                {
                    var sources = Marks(enabled.Length, ids.Length);
                    foreach (var row in session.Query($"""
                        SELECT vc.entry_id,COUNT(*) served_chapter_count,
                               MAX(CASE WHEN vc.chapter_number>=0 THEN vc.chapter_number END) served_latest_chapter
                        FROM virtual_chapters vc WHERE vc.entry_id IN ({marks}) AND vc.hidden=0
                          AND EXISTS(SELECT 1 FROM chapter_candidates cc WHERE cc.virtual_chapter_id=vc.id
                                     AND cc.active=1 AND cc.source_id IN ({sources})) GROUP BY vc.entry_id
                        """, Parameters(ids.Cast<object?>().Concat(enabled))))
                    {
                        var value = result[row.Integer("entry_id")]; value["served_chapter_count"] = row.Integer("served_chapter_count");
                        value["served_latest_chapter"] = row["served_latest_chapter"] is null ? null : JsonValue.Create(row.Number("served_latest_chapter"));
                    }
                }
                return result;
            });
        }
    }

    private static JsonObject EmptyRecordStats() => new()
    {
        ["chapter_count"] = 0, ["latest_chapter"] = null, ["missing_chapter_count"] = 0,
        ["chapters_checked"] = false, ["chapter_checked_at"] = null, ["chapter_check_error"] = ""
    };
    private static JsonNode? Node(object? value) => value is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(value);
}
