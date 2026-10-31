using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Catalogue;

/// <summary>
/// Status page counts (port of service.status_stats / status_stats_snapshot): one owned scan at a time,
/// the last result served for 15 seconds so polling never stacks database and folder scans.
/// Duplicate-snapshot and live task counters belong to phases 3 and 8 and are reported as not available yet.
/// </summary>
public sealed class CatalogueStatusStats(SqliteDatabase database, Func<AutoSourcarrSettings> settings, TimeProvider? clock = null)
{
    private static readonly string[] StatusLabels = ["Unknown", "Ongoing", "Completed", "Licensed", "Publishing finished", "Cancelled", "Hiatus"];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private (long At, JsonObject Value)? _cached;

    public async Task<JsonObject> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetTimestamp();
        if (_cached is { } cached && _clock.GetElapsedTime(cached.At, now) < TimeSpan.FromSeconds(15))
            return With(cached.Value, _clock.GetElapsedTime(cached.At, now).TotalSeconds);
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } fresh && _clock.GetElapsedTime(fresh.At, _clock.GetTimestamp()) < TimeSpan.FromSeconds(15))
                return With(fresh.Value, _clock.GetElapsedTime(fresh.At, _clock.GetTimestamp()).TotalSeconds);
            var value = await Task.Run(Build, cancellationToken).ConfigureAwait(false);
            _cached = (_clock.GetTimestamp(), value);
            return With(value, 0);
        }
        finally { _refresh.Release(); }
    }

    private static JsonObject With(JsonObject value, double age)
    {
        var copy = (JsonObject)value.DeepClone();
        copy["cache_age_seconds"] = Math.Round(age, 3);
        return copy;
    }

    public JsonObject Build()
    {
        var catalog = database.Read(DatabaseFile.Catalog, session =>
        {
            long Count(string sql) => Convert.ToInt64(session.Scalar(sql) ?? 0L, CultureInfo.InvariantCulture);
            var entries = Count("SELECT COUNT(*) FROM entries");
            var matched = Count("SELECT COUNT(DISTINCT entry_id) FROM source_matches WHERE active=1");
            var statuses = new JsonObject();
            foreach (var row in session.Query("SELECT status, COUNT(*) n FROM entries GROUP BY status ORDER BY status"))
            {
                var status = row.Integer("status");
                statuses[status is >= 0 and < 7 ? StatusLabels[(int)status] : "Status " + status.ToString(CultureInfo.InvariantCulture)] = row.Integer("n");
            }
            var latest = session.Scalar("SELECT MAX(updated_at) FROM browse_cache");
            return new JsonObject
            {
                ["catalog"] = new JsonObject
                {
                    ["entries"] = entries,
                    ["matched_entries"] = matched,
                    ["unmatched_entries"] = Math.Max(0, entries - matched),
                    ["source_matches"] = Count("SELECT COUNT(*) FROM source_matches WHERE active=1"),
                    ["manual_matches"] = Count("SELECT (SELECT COUNT(*) FROM source_matches WHERE active=1 AND locked=1) + (SELECT COUNT(*) FROM source_variants WHERE active=1 AND locked=1)"),
                    ["alternate_source_records"] = Count("SELECT COUNT(*) FROM source_variants WHERE active=1"),
                    ["merged_aliases"] = Count("SELECT COUNT(*) FROM entry_aliases"),
                    ["possible_duplicates"] = null,
                    ["duplicate_groups"] = null,
                    ["duplicate_snapshot_ready"] = false,
                    ["reviewed_nonduplicates"] = Count("SELECT COUNT(*) FROM duplicate_reviews WHERE decision='not_duplicate'"),
                    ["duplicate_review_history"] = Count("SELECT COUNT(*) FROM duplicate_review_history"),
                    ["entries_with_additional_matches"] = Count("SELECT COUNT(DISTINCT entry_id) FROM source_variants WHERE active=1"),
                    ["translated_titles"] = Count("SELECT COUNT(*) FROM titles WHERE title_type IN ('translated-origin','translated-literal')"),
                    ["deleted_entries"] = Count("SELECT COUNT(*) FROM deleted_entries_history"),
                    ["deleted_source_records"] = Count("SELECT COUNT(*) FROM deleted_source_records"),
                    ["publication_statuses"] = statuses
                },
                ["sources"] = new JsonObject
                {
                    ["families"] = Count("SELECT COUNT(*) FROM (SELECT 1 FROM source_config WHERE available=1 GROUP BY name, base_url)"),
                    ["enabled_families"] = Count("SELECT COUNT(*) FROM (SELECT 1 FROM source_config WHERE available=1 AND enabled_language=1 GROUP BY name, base_url)"),
                    ["variants"] = Count("SELECT COUNT(*) FROM source_config WHERE available=1"),
                    ["enabled_variants"] = Count("SELECT COUNT(*) FROM source_config WHERE available=1 AND enabled_language=1"),
                    ["search_variants"] = Count("SELECT COUNT(*) FROM source_config WHERE available=1 AND enabled_language=1 AND enabled_search=1"),
                    ["chapter_variants"] = Count("SELECT COUNT(*) FROM source_config WHERE available=1 AND enabled_language=1 AND enabled_chapters=1"),
                    ["unavailable_variants"] = Count("SELECT COUNT(*) FROM source_config WHERE available=0")
                },
                ["cache"] = new JsonObject
                {
                    ["persistent_browse_feeds"] = Count("SELECT COUNT(*) FROM browse_cache"),
                    ["latest_browse_refresh"] = latest is null or DBNull ? "" : Convert.ToString(latest, CultureInfo.InvariantCulture),
                    ["translation_cache_rows"] = Count("SELECT COUNT(*) FROM translation_cache")
                }
            };
        });
        var chapters = database.Read(DatabaseFile.Origins, session =>
        {
            long Count(string sql) => Convert.ToInt64(session.Scalar(sql) ?? 0L, CultureInfo.InvariantCulture);
            return new JsonObject
            {
                ["virtual_chapters"] = Count("SELECT COUNT(*) FROM virtual_chapters WHERE hidden=0"),
                ["chapter_candidates"] = Count("SELECT COUNT(*) FROM chapter_candidates WHERE active=1"),
                ["served_history"] = Count("SELECT COUNT(*) FROM origin_history")
            };
        });
        var current = settings();
        var (covers, coverBytes) = Folder(Path.Combine(current.DataDir, "cover-cache"), "*.img", recursive: false);
        var cache = catalog["cache"]!.AsObject();
        cache["cover_files"] = covers;
        cache["cover_bytes"] = coverBytes;
        var paths = database.Paths; // live paths: a database move switches them without a restart
        long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        var databaseBytes = new[] { paths.Catalog, paths.Origins }
            .Sum(path => Size(path) + Size(path + "-wal") + Size(path + "-shm"));
        var (_, dataBytes) = Folder(current.DataDir, "*", recursive: true);
        catalog["chapters"] = chapters;
        catalog["storage"] = new JsonObject
        {
            ["database_dir"] = paths.Directory,
            ["database_bytes"] = databaseBytes,
            ["data_dir"] = current.DataDir,
            ["data_bytes"] = dataBytes
        };
        catalog["captured_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return catalog;
    }

    private static (long Files, long Bytes) Folder(string path, string pattern, bool recursive)
    {
        if (!Directory.Exists(path))
            return (0, 0);
        long files = 0, bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(path).EnumerateFiles(pattern, options))
        {
            files++;
            try { bytes += file.Length; }
            catch (IOException) { }
        }
        return (files, bytes);
    }
}
