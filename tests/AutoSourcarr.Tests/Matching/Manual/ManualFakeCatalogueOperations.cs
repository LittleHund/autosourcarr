using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Matching.Manual;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests.Matching;

/// <summary>
/// Plain-SQL stand-in for the catalogue operations other phase-8 areas provide (create, attach, merge, split); the
/// metadata, title and chapter steps only record that they were asked for.
/// </summary>
internal sealed class ManualFakeCatalogueOperations(SqliteDatabase database) : IManualCatalogueOperations
{
    public List<string> Calls { get; } = [];

    private static long Id(JsonObject manga) => manga["id"] is JsonValue value && value.TryGetValue<long>(out var id) ? id
        : long.Parse(manga["id"]!.ToString(), CultureInfo.InvariantCulture);
    private static string Title(JsonObject manga) => manga["title"] is JsonValue value && value.TryGetValue<string>(out var title) ? title : "";

    public long CreateEntry(string sourceId, JsonObject manga)
    {
        Calls.Add("create " + sourceId + ":" + Id(manga).ToString(CultureInfo.InvariantCulture));
        return database.Write(DatabaseFile.Catalog, c =>
        {
            c.Execute("INSERT INTO entries(preferred_title,created_at,updated_at) VALUES($title,'2026-01-01','2026-01-01')", ("$title", Title(manga)));
            return Convert.ToInt64(c.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
        });
    }

    public void UpsertPrimary(long entryId, string sourceId, JsonObject manga, double confidence, string matchType, bool locked)
    {
        Calls.Add("primary " + entryId.ToString(CultureInfo.InvariantCulture) + " " + sourceId + ":" + Id(manga).ToString(CultureInfo.InvariantCulture));
        database.Write(DatabaseFile.Catalog, c =>
        {
            var id = Id(manga);
            c.Execute("UPDATE source_matches SET active=0 WHERE source_id=$s AND source_manga_id=$m AND entry_id<>$e", ("$s", sourceId), ("$m", id), ("$e", entryId));
            c.Execute("UPDATE source_variants SET active=0 WHERE source_id=$s AND source_manga_id=$m", ("$s", sourceId), ("$m", id));
            return c.Execute("""
                INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_title,confidence,match_type,locked,active,metadata_json,last_checked)
                VALUES($e,$s,$m,$t,$c,$type,$locked,1,$json,'2026-01-01')
                ON CONFLICT(entry_id,source_id) DO UPDATE SET source_manga_id=excluded.source_manga_id,source_title=excluded.source_title,
                  confidence=excluded.confidence,match_type=excluded.match_type,locked=excluded.locked,active=1,metadata_json=excluded.metadata_json
                """, ("$e", entryId), ("$s", sourceId), ("$m", id), ("$t", Title(manga)), ("$c", confidence), ("$type", matchType), ("$locked", locked ? 1 : 0), ("$json", manga.ToJsonString()));
        });
    }

    public void UpsertVariant(long entryId, string sourceId, JsonObject manga, double confidence, string matchType, bool locked)
    {
        Calls.Add("variant " + entryId.ToString(CultureInfo.InvariantCulture) + " " + sourceId + ":" + Id(manga).ToString(CultureInfo.InvariantCulture));
        database.Write(DatabaseFile.Catalog, c =>
        {
            var id = Id(manga);
            c.Execute("UPDATE source_matches SET active=0 WHERE source_id=$s AND source_manga_id=$m", ("$s", sourceId), ("$m", id));
            return c.Execute("""
                INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,confidence,match_type,locked,active,metadata_json,last_checked)
                VALUES($e,$s,$m,$t,$c,$type,$locked,1,$json,'2026-01-01')
                ON CONFLICT(source_id,source_manga_id) DO UPDATE SET entry_id=excluded.entry_id,source_title=excluded.source_title,
                  confidence=excluded.confidence,match_type=excluded.match_type,locked=excluded.locked,active=1
                """, ("$e", entryId), ("$s", sourceId), ("$m", id), ("$t", Title(manga)), ("$c", confidence), ("$type", matchType), ("$locked", locked ? 1 : 0), ("$json", manga.ToJsonString()));
        });
    }

    public long MergeEntries(long target, long duplicate, string reason)
    {
        Calls.Add("merge " + target.ToString(CultureInfo.InvariantCulture) + "<-" + duplicate.ToString(CultureInfo.InvariantCulture));
        return database.Write(DatabaseFile.Catalog, c =>
        {
            c.Execute("UPDATE source_variants SET entry_id=$t WHERE entry_id=$d", ("$t", target), ("$d", duplicate));
            // A second primary of one source becomes an additional record of the target.
            foreach (var row in c.Query("SELECT * FROM source_matches WHERE entry_id=$d", ("$d", duplicate)))
            {
                c.Execute("DELETE FROM source_matches WHERE id=$id", ("$id", row.Integer("id")));
                c.Execute("""
                    INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_title,match_type,locked,active,last_checked)
                    VALUES($t,$s,$m,$title,$type,$locked,$active,'2026-01-01')
                    """, ("$t", target), ("$s", row.Text("source_id")), ("$m", row.Integer("source_manga_id")), ("$title", row.Text("source_title")),
                    ("$type", row.Text("match_type")), ("$locked", row.Integer("locked")), ("$active", row.Integer("active")));
            }
            c.Execute("INSERT INTO entry_aliases(old_entry_id,canonical_entry_id,reason,merged_at) VALUES($d,$t,$r,'2026-01-01')", ("$d", duplicate), ("$t", target), ("$r", reason));
            c.Execute("DELETE FROM entries WHERE id=$d", ("$d", duplicate));
            return target;
        });
    }

    public long SplitRecords(long entryId, IReadOnlyList<NativeSourceRecord> records)
    {
        Calls.Add("split " + entryId.ToString(CultureInfo.InvariantCulture));
        return database.Write(DatabaseFile.Catalog, c =>
        {
            c.Execute("INSERT INTO entries(preferred_title,created_at,updated_at) VALUES('Split','2026-01-01','2026-01-01')");
            var created = Convert.ToInt64(c.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
            foreach (var record in records)
            {
                c.Execute("UPDATE source_matches SET entry_id=$n WHERE entry_id=$e AND source_id=$s AND source_manga_id=$m", ("$n", created), ("$e", entryId), ("$s", record.SourceId), ("$m", record.MangaId));
                c.Execute("UPDATE source_variants SET entry_id=$n WHERE entry_id=$e AND source_id=$s AND source_manga_id=$m", ("$n", created), ("$e", entryId), ("$s", record.SourceId), ("$m", record.MangaId));
            }
            return created;
        });
    }

    public void PurgeSourceRecordTitles(long entryId, string sourceId, IReadOnlyCollection<long> sourceMangaIds) => Calls.Add("purge " + entryId.ToString(CultureInfo.InvariantCulture));
    public void SyncPreferredTitle(long entryId) => Calls.Add("sync-title " + entryId.ToString(CultureInfo.InvariantCulture));
    public void ApplySourceTitleOverride(long entryId, string sourceId, long sourceMangaId) => Calls.Add("title-override " + entryId.ToString(CultureInfo.InvariantCulture));
    public void ChooseMetadata(long entryId) => Calls.Add("metadata " + entryId.ToString(CultureInfo.InvariantCulture));
    public void ReconcileChapters(long entryId) => Calls.Add("chapters " + entryId.ToString(CultureInfo.InvariantCulture));
    public void IngestChapters(long entryId, string sourceId, long sourceMangaId, JsonArray chapters) => Calls.Add("ingest " + entryId.ToString(CultureInfo.InvariantCulture));
    public Task<JsonObject?> FetchMangaAsync(long sourceMangaId, bool withChapters, CancellationToken cancellationToken)
    {
        Calls.Add("fetch " + sourceMangaId.ToString(CultureInfo.InvariantCulture));
        return Task.FromResult<JsonObject?>(new JsonObject
        {
            ["manga"] = new JsonObject { ["id"] = sourceMangaId, ["title"] = "Fetched " + sourceMangaId.ToString(CultureInfo.InvariantCulture), ["description"] = "Loaded" },
            ["chapters"] = new JsonArray(),
        });
    }
    public void Invalidate(IReadOnlyCollection<string> coverKeys) => Calls.Add("invalidate");
}

/// <summary>Small catalogue fixtures for the manual-decision and maintenance tests.</summary>
internal static class ManualFixture
{
    public static SqliteDatabase Open(Sandbox temp)
    {
        var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
        new SchemaMigrator(database).Initialize();
        return database;
    }

    public static long Entry(SqliteDatabase database, string title) => database.Write(DatabaseFile.Catalog, c =>
    {
        c.Execute("INSERT INTO entries(preferred_title,created_at,updated_at) VALUES($title,'2026-01-01','2026-01-01')", ("$title", title));
        return Convert.ToInt64(c.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
    });

    public static void Source(SqliteDatabase database, string sourceId, string name) => database.Write(DatabaseFile.Catalog, c =>
        c.Execute("INSERT OR IGNORE INTO source_config(source_id,name,lang,last_seen) VALUES($id,$name,'en','2026-01-01')", ("$id", sourceId), ("$name", name)));

    public static void Record(SqliteDatabase database, long entryId, string sourceId, long mangaId, string title, bool variant = false, string matchType = "auto", bool locked = false)
        => database.Write(DatabaseFile.Catalog, c => c.Execute(
            "INSERT INTO " + (variant ? "source_variants" : "source_matches") + "(entry_id,source_id,source_manga_id,source_title,match_type,locked,active,last_checked) VALUES($e,$s,$m,$t,$type,$locked,1,'2026-01-01')",
            ("$e", entryId), ("$s", sourceId), ("$m", mangaId), ("$t", title), ("$type", matchType), ("$locked", locked ? 1 : 0)));

    public static long Count(SqliteDatabase database, string sql) => Convert.ToInt64(database.Read(DatabaseFile.Catalog, c => c.Scalar(sql)), CultureInfo.InvariantCulture);
}
