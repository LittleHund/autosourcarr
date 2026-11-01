using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Collections;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Imaging;

/// <summary>One cover candidate of an entry: an active record's thumbnail (or the entry's own one when SourceId is empty).</summary>
public sealed record CoverHint(string SourceId, long SourceMangaId, string ThumbnailUrl, string BaseUrl, long Priority = 1000, bool IsVariant = false,
    bool Initialized = false, double Confidence = 0)
{
    public JsonObject ToJson() => new()
    {
        ["source_id"] = SourceId, ["source_manga_id"] = SourceMangaId, ["thumbnail_url"] = ThumbnailUrl, ["base_url"] = BaseUrl,
        ["priority"] = Priority, ["is_variant"] = IsVariant ? 1 : 0, ["initialized"] = Initialized ? 1 : 0, ["confidence"] = Confidence,
    };
}

/// <summary>The cache keys and records one "Refresh covers" clears (the unified cover plus every active record's cover).</summary>
public sealed record CoverRefreshPlan(long EntryId, IReadOnlyList<string> Keys, IReadOnlyList<long> SourceMangaIds);

/// <summary>
/// Port-map "Imaging → CoverCache / CoverRecovery": cover candidates across every active record of an entry (a missing or
/// blocked thumbnail on one source fails over to the next), the per-record cover hint (remembered from browse results,
/// else the stored record), and cover recovery: after a unified cover miss, one source-detail hydration of the best record
/// without a thumbnail is scheduled, at most once a minute per entry. Local reads only; nothing here contacts a source.
/// </summary>
public sealed class CoverHints
{
    private const int RecoveryCooldownSeconds = 60;
    private readonly SqliteDatabase _database;
    private readonly Func<string, long, bool>? _hydrate;
    private readonly Func<double> _monotonic;
    private readonly BoundedLruCache<long, CoverHint> _hints;
    private readonly BoundedLruCache<long, double> _recoveries = new(2000);

    /// <param name="hydrate">schedule_source_metadata_hydration(source, manga, eager: true) (NativeMetadataHydration.Schedule); null schedules nothing.</param>
    public CoverHints(SqliteDatabase database, Func<AutoSourcarrSettings> settings, Func<string, long, bool>? hydrate = null, Func<double>? monotonic = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(settings);
        _database = database; _hydrate = hydrate;
        _monotonic = monotonic ?? (() => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency);
        _hints = new(Math.Max(128, settings().CoverHintCacheMaxEntries));
    }

    /// <summary>remember_cover_hint: keeps the thumbnail a browse/search result carried for its source manga id.</summary>
    public void Remember(string sourceId, JsonObject manga)
    {
        ArgumentNullException.ThrowIfNull(manga);
        var id = ThumbnailFingerprints.JsonNumber(manga["id"]);
        if (id == 0) return;
        var baseUrl = _database.Read(DatabaseFile.Catalog, s => s.Scalar("SELECT base_url FROM source_config WHERE source_id=$id", ("$id", sourceId)) as string) ?? "";
        var thumbnail = manga["thumbnailUrl"] is JsonValue thumb && thumb.TryGetValue<string>(out var url) ? url : "";
        _hints.Set(id, new CoverHint(sourceId, id, thumbnail, baseUrl));
    }

    /// <summary>source_cover_hint: the remembered hint, else the highest-priority active record with this manga id; null when unknown.</summary>
    public CoverHint? SourceHint(long mangaId)
    {
        if (_hints.TryGet(mangaId, out var known)) return known;
        var row = _database.Read(DatabaseFile.Catalog, s => s.Query("""
            SELECT sm.source_id,sm.thumbnail_url,COALESCE(sc.base_url,'') base_url FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id
             WHERE sm.source_manga_id=$id AND sm.active=1 ORDER BY COALESCE(sc.priority,1000) LIMIT 1
            """, ("$id", mangaId)).FirstOrDefault() ?? s.Query("""
            SELECT sv.source_id,sv.thumbnail_url,COALESCE(sc.base_url,'') base_url FROM source_variants sv LEFT JOIN source_config sc ON sc.source_id=sv.source_id
             WHERE sv.source_manga_id=$id AND sv.active=1 ORDER BY COALESCE(sc.priority,1000) LIMIT 1
            """, ("$id", mangaId)).FirstOrDefault());
        if (row is null) return null;
        var hint = new CoverHint(row.Text("source_id"), mangaId, row.Text("thumbnail_url"), row.Text("base_url"));
        _hints.Set(mangaId, hint);
        return hint;
    }

    /// <summary>
    /// entry_cover_hints: candidates of every active record on an available source (records with a thumbnail first, then
    /// initialized, source priority, primary before additional, confidence), at most <paramref name="limit"/> (1-32), plus
    /// the entry's own thumbnail when no record carries it. KeyNotFoundException when the entry does not exist.
    /// </summary>
    public IReadOnlyList<CoverHint> EntryHints(long entryId, int limit = 8) => _database.Read(DatabaseFile.Catalog, s => EntryHints(s, entryId, limit));

    internal static IReadOnlyList<CoverHint> EntryHints(DatabaseSession s, long entryId, int limit)
    {
        var id = CatalogueTitles.Canonical(s, entryId);
        var entry = s.Query("SELECT thumbnail_url FROM entries WHERE id=$id", ("$id", id)).FirstOrDefault()
            ?? throw new KeyNotFoundException("Entry not found");
        var hints = s.Query("""
            SELECT source_id,source_manga_id,thumbnail_url,base_url,priority,is_variant,initialized,confidence FROM (
              SELECT sm.source_id,sm.source_manga_id,sm.thumbnail_url,COALESCE(sc.base_url,'') base_url,COALESCE(sc.priority,1000) priority,0 is_variant,sm.initialized,sm.confidence
                FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id WHERE sm.entry_id=$id AND sm.active=1 AND COALESCE(sc.available,1)=1
              UNION ALL
              SELECT sv.source_id,sv.source_manga_id,sv.thumbnail_url,COALESCE(sc.base_url,'') base_url,COALESCE(sc.priority,1000) priority,1 is_variant,sv.initialized,sv.confidence
                FROM source_variants sv LEFT JOIN source_config sc ON sc.source_id=sv.source_id WHERE sv.entry_id=$id AND sv.active=1 AND COALESCE(sc.available,1)=1)
            ORDER BY CASE WHEN thumbnail_url<>'' THEN 0 ELSE 1 END,initialized DESC,priority,is_variant,confidence DESC LIMIT $limit
            """, ("$id", id), ("$limit", Math.Clamp(limit, 1, 32)))
            .Select(row => new CoverHint(row.Text("source_id"), row.Integer("source_manga_id"), row.Text("thumbnail_url"), row.Text("base_url"),
                row.Integer("priority", 1000), row.Flag("is_variant"), row.Flag("initialized"), row.Number("confidence"))).ToList();
        var own = entry.Text("thumbnail_url");
        if (own.Length > 0 && hints.All(hint => hint.ThumbnailUrl != own)) hints.Add(new CoverHint("", 0, own, ""));
        return hints;
    }

    /// <summary>entry_cover_hint: the best single candidate, or null.</summary>
    public CoverHint? EntryHint(long entryId) => EntryHints(entryId, 1).FirstOrDefault();

    /// <summary>
    /// The keys "Refresh covers" clears: entry-{requested id}, entry-{canonical id} and source-{manga id} of every active
    /// record. KeyNotFoundException when the entry does not exist.
    /// </summary>
    public CoverRefreshPlan RefreshPlan(long entryId) => _database.Read(DatabaseFile.Catalog, s =>
    {
        var canonical = CatalogueTitles.Canonical(s, entryId);
        var hints = EntryHints(s, canonical, 32);
        var keys = new List<string> { "entry-" + entryId.ToString(CultureInfo.InvariantCulture) };
        if (canonical != entryId) keys.Add("entry-" + canonical.ToString(CultureInfo.InvariantCulture));
        var records = hints.Select(hint => hint.SourceMangaId).Where(id => id != 0).Distinct().Order().ToArray();
        keys.AddRange(records.Select(id => "source-" + id.ToString(CultureInfo.InvariantCulture)));
        return new CoverRefreshPlan(canonical, keys, records);
    });

    /// <summary>entry_cover_recovery_candidate: the best record still missing a thumbnail (or the least initialized one).</summary>
    public (string SourceId, long SourceMangaId)? RecoveryCandidate(long entryId) => _database.Read(DatabaseFile.Catalog, s =>
    {
        var id = CatalogueTitles.Canonical(s, entryId);
        var row = s.Query("""
            SELECT source_id,source_manga_id FROM (
              SELECT sm.source_id,sm.source_manga_id,sm.thumbnail_url,sm.initialized,COALESCE(sc.priority,1000) priority,0 is_variant
                FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id WHERE sm.entry_id=$id AND sm.active=1 AND COALESCE(sc.available,1)=1
              UNION ALL
              SELECT sv.source_id,sv.source_manga_id,sv.thumbnail_url,sv.initialized,COALESCE(sc.priority,1000) priority,1 is_variant
                FROM source_variants sv LEFT JOIN source_config sc ON sc.source_id=sv.source_id WHERE sv.entry_id=$id AND sv.active=1 AND COALESCE(sc.available,1)=1)
            ORDER BY CASE WHEN thumbnail_url='' THEN 0 ELSE 1 END,initialized,priority,is_variant LIMIT 1
            """, ("$id", id)).FirstOrDefault();
        return row is null ? ((string, long)?)null : (row.Text("source_id"), row.Integer("source_manga_id"));
    });

    /// <summary>
    /// schedule_entry_cover_recovery / recover_entry_cover: after a unified cover miss, schedules one eager source-detail
    /// hydration of <see cref="RecoveryCandidate"/>, at most once per minute per entry. Answers whether one was scheduled.
    /// </summary>
    public bool ScheduleRecovery(long entryId)
    {
        if (_hydrate is null) return false;
        var id = _database.Read(DatabaseFile.Catalog, s => CatalogueTitles.Canonical(s, entryId));
        var now = _monotonic();
        lock (_recoveries)
        {
            if (_recoveries.TryGet(id, out var last) && now - last < RecoveryCooldownSeconds) return false;
            _recoveries.Set(id, now);
        }
        return RecoveryCandidate(id) is { } candidate && _hydrate(candidate.SourceId, candidate.SourceMangaId);
    }

    /// <summary>
    /// _invalidate_discovery_caches (database part): after a source configuration change the saved browse feeds are
    /// marked stale (served once more, then refreshed); <paramref name="hard"/> deletes them. Answers the rows touched.
    /// </summary>
    public static int InvalidateDiscoveryCaches(SqliteDatabase database, bool hard = false)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.Write(DatabaseFile.Catalog, s => hard
            ? s.Execute("DELETE FROM browse_cache")
            : s.Execute("UPDATE browse_cache SET updated_at='1970-01-01T00:00:00+00:00'"));
    }
}
