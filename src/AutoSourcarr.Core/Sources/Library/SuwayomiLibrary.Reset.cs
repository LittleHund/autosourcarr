using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Sources.Library;

/// <summary>
/// Phase 8 (moved from phase 6): keeping main-library subscriptions through a database reset (0.4.5
/// snapshot_subbed_reset_seeds / restore_subbed_reset_seeds / rebrowse_subbed_reset_seeds) and the v0.2.80 repair of
/// source-less reset clones (_repair_subbed_orphan_aliases / repair_subbed_orphan_aliases_upgrade). The reader source keeps
/// the entry id in SManga.url, so recreating the same ids keeps every subscription resolving while the catalogue rebuilds.
/// </summary>
public sealed partial class SuwayomiLibrary
{
    public const string OrphanAliasRepairKey = "subbed_orphan_alias_repair_version";
    private const string OrphanAliasRepairVersion = "0.2.80";

    /// <summary>
    /// snapshot_subbed_reset_seeds: before a reset, one seed per main-library manga of the AutoSourcarr source — its raw
    /// and canonical entry id, a trusted title (manual title, else a real English title, else the library title) and
    /// the entry's display fields. Answers {"ok", "error", "seeds"}; ok=false when main Suwayomi is unavailable.
    /// </summary>
    public async Task<JsonObject> SnapshotResetSeedsAsync(CancellationToken cancellationToken = default)
    {
        if (_main() is not { } main) return SeedFailure(NotConfigured);
        List<JsonObject> library;
        HashSet<string> ids;
        try
        {
            ids = AutoSourcarrSourceIds(await main.SourcesAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            if (ids.Count == 0) return SeedFailure(NotInstalled);
            library = await main.LibraryMangasAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return SeedFailure(error.Message); }
        var items = library.Where(manga => SourceResponseContract.Identity(manga["sourceId"]) is { } source && ids.Contains(source)).ToList();
        var seeds = await _io.RunAsync(() => _database.Read(DatabaseFile.Catalog, session => Seeds(session, items)), cancellationToken).ConfigureAwait(false);
        return new JsonObject { ["ok"] = true, ["error"] = "", ["seeds"] = new JsonArray(seeds.Select(seed => (JsonNode?)seed).ToArray()) };
    }

    private static JsonObject SeedFailure(string error) => new() { ["ok"] = false, ["error"] = error, ["seeds"] = new JsonArray() };

    private static List<JsonObject> Seeds(DatabaseSession session, IReadOnlyList<JsonObject> items)
    {
        var unique = new Dictionary<long, JsonObject>();
        foreach (var manga in items)
        {
            if (UrlEntryId(SourceResponseContract.Text(manga["url"])) is not long raw) continue;
            var canonical = Canonical(session, raw);
            var rows = session.Query("SELECT preferred_title,manual_preferred_title,description,thumbnail_url,status,author,artist,genre FROM entries WHERE id=$id", ("$id", canonical));
            var current = rows.Count > 0 ? rows[0] : null;
            var libraryTitle = SourceResponseContract.Text(manga["title"]).Trim();
            var overrideTitle = (session.Scalar("SELECT title FROM preferred_title_overrides WHERE entry_id=$id", ("$id", canonical)) as string ?? "").Trim();
            var manual = current?.Flag("manual_preferred_title") == true;
            var title = overrideTitle.Length > 0 ? overrideTitle : manual ? current!.Text("preferred_title").Trim() : "";
            if (title.Length == 0) title = libraryTitle;
            // Without a user title and with a native main-library title, reuse a trustworthy English title (never a generated translation).
            if (overrideTitle.Length == 0 && !manual && (title.Length == 0 || !TextMatcher.LooksPreferredEnglish(title)))
            {
                var english = session.Query("""
                    SELECT t.title,t.language,t.title_type,COALESCE(sc.lang,'') source_lang FROM titles t LEFT JOIN source_config sc ON sc.source_id=t.source_id WHERE t.entry_id=$id
                    UNION ALL SELECT sm.source_title,COALESCE(sc.lang,''),'source',COALESCE(sc.lang,'') FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id WHERE sm.entry_id=$id AND sm.active=1
                    UNION ALL SELECT sv.source_title,COALESCE(sc.lang,''),'source-variant',COALESCE(sc.lang,'') FROM source_variants sv LEFT JOIN source_config sc ON sc.source_id=sv.source_id WHERE sv.entry_id=$id AND sv.active=1
                    """, ("$id", canonical))
                    .Select(row => (Title: row.Text("title").Trim(), Row: row))
                    .FirstOrDefault(item => item.Title.Length > 0 && !item.Row.Text("title_type").StartsWith("translated-", StringComparison.Ordinal)
                        && (item.Row.Text("language").Equals("en", StringComparison.OrdinalIgnoreCase) || item.Row.Text("source_lang").Equals("en", StringComparison.OrdinalIgnoreCase))
                        && TextMatcher.LooksPreferredEnglish(item.Title)).Title;
                if (!string.IsNullOrEmpty(english)) title = english;
            }
            if (title.Length == 0 && current is not null) title = current.Text("preferred_title").Trim();
            if (title.Length == 0) continue;
            string Field(string column, string key) => current?.Text(column) is { Length: > 0 } value ? value : SourceResponseContract.Text(manga[key]);
            unique[raw] = new JsonObject
            {
                ["entry_id"] = raw, ["canonical_entry_id"] = canonical, ["title"] = title, ["manual_title"] = overrideTitle.Length > 0 || manual,
                ["main_manga_id"] = manga["id"]?.DeepClone(), ["library_title"] = libraryTitle,
                ["description"] = Field("description", "description"), ["thumbnail_url"] = Field("thumbnail_url", "thumbnailUrl"),
                ["status"] = current?.Integer("status") ?? 0, ["author"] = Field("author", "author"), ["artist"] = Field("artist", "artist"),
                ["genre"] = current?.Text("genre") ?? "", ["rebuild_hints"] = new JsonArray(),
            };
        }
        return unique.Values.ToList();
    }

    private static long SeedLong(JsonObject seed, string key) => SourceResponseContract.TryLong(seed[key], out var value) ? value : 0;
    private static string SeedText(JsonObject seed, string key) => SourceResponseContract.Text(seed[key]);

    /// <summary>
    /// restore_subbed_reset_seeds (after the reset): recreates one entry per canonical id with its trusted title (a kept
    /// manual title override wins) and a main-library title row, and aliases every other raw library id to it.
    /// </summary>
    public int RestoreResetSeeds(IReadOnlyList<JsonObject> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        var grouped = new Dictionary<long, List<JsonObject>>();
        foreach (var seed in seeds)
        {
            var raw = SeedLong(seed, "entry_id"); var canonical = SeedLong(seed, "canonical_entry_id");
            if (canonical <= 0) canonical = raw;
            if (raw > 0 && canonical > 0) (grouped.TryGetValue(canonical, out var list) ? list : grouped[canonical] = []).Add(seed);
        }
        var stamp = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);
        var restored = 0;
        foreach (var (canonical, rows) in grouped)
        {
            var seed = rows.OrderByDescending(row => row["manual_title"] is JsonValue flag && flag.TryGetValue<bool>(out var manual) && manual)
                .ThenByDescending(row => SeedText(row, "description").Length).ThenByDescending(row => SeedText(row, "thumbnail_url").Trim().Length > 0).First();
            var title = SeedText(seed, "title").Trim();
            if (title.Length == 0) continue;
            _database.Write(DatabaseFile.Catalog, session =>
            {
                var overrideTitle = (session.Scalar("SELECT title FROM preferred_title_overrides WHERE entry_id=$id", ("$id", canonical)) as string ?? "").Trim();
                var chosen = overrideTitle.Length > 0 ? overrideTitle : title;
                var manual = overrideTitle.Length > 0 || seed["manual_title"] is JsonValue flag && flag.TryGetValue<bool>(out var isManual) && isManual;
                session.Execute("""
                    INSERT OR IGNORE INTO entries(id,preferred_title,description,thumbnail_url,status,author,artist,genre,manual_preferred_title,created_at,updated_at)
                    VALUES($id,$title,$description,$thumbnail,$status,$author,$artist,$genre,$manual,$stamp,$stamp)
                    """, ("$id", canonical), ("$title", chosen), ("$description", TextMatcher.CleanSummary(SeedText(seed, "description"))),
                    ("$thumbnail", SeedText(seed, "thumbnail_url")), ("$status", SeedLong(seed, "status")), ("$author", SeedText(seed, "author")),
                    ("$artist", SeedText(seed, "artist")), ("$genre", SeedText(seed, "genre")), ("$manual", manual ? 1 : 0), ("$stamp", stamp));
                // A source-less anchor needs one trusted title, or the strict matcher rejects every rebuild hint.
                var normalized = TextMatcher.NormalizeTitle(chosen);
                if (normalized.Length > 0)
                    session.Execute("INSERT OR IGNORE INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES($id,$title,$normalized,'','main-library','',0)",
                        ("$id", canonical), ("$title", chosen), ("$normalized", normalized));
                foreach (var raw in rows.Select(row => SeedLong(row, "entry_id")).Where(raw => raw > 0 && raw != canonical).Distinct())
                    session.Execute("""
                        INSERT INTO entry_aliases(old_entry_id,canonical_entry_id,reason,merged_at) VALUES($old,$id,'reset-preserved-library-alias',$stamp)
                        ON CONFLICT(old_entry_id) DO UPDATE SET canonical_entry_id=excluded.canonical_entry_id,reason=excluded.reason,merged_at=excluded.merged_at
                        """, ("$old", raw), ("$id", canonical), ("$stamp", stamp));
                return 0;
            });
            restored++;
        }
        Invalidate();
        return restored;
    }

    /// <summary>
    /// rebrowse_subbed_reset_seeds: rebuilds each subscribed entry once (several raw ids can share one canonical entry,
    /// their hints are merged) through <paramref name="rebuild"/>, which answers the entry's match count afterwards.
    /// Answers {"searched", "recovered", "errors"}.
    /// </summary>
    public async Task<JsonObject> RebrowseResetSeedsAsync(IReadOnlyList<JsonObject> seeds, Func<long, JsonArray, CancellationToken, Task<long>> rebuild,
        Action<int, int, string>? report = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seeds); ArgumentNullException.ThrowIfNull(rebuild);
        var canonical = new Dictionary<long, (JsonObject Seed, JsonArray Hints, HashSet<string> Seen)>();
        foreach (var seed in seeds)
        {
            var id = SeedLong(seed, "canonical_entry_id");
            if (id <= 0) id = SeedLong(seed, "entry_id");
            if (id <= 0) continue;
            if (!canonical.TryGetValue(id, out var item)) canonical[id] = item = (seed, new JsonArray(), new HashSet<string>(StringComparer.Ordinal));
            foreach (var hint in (seed["rebuild_hints"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (item.Seen.Add(SeedText(hint, "source_id") + ":" + SeedLong(hint, "source_manga_id").ToString(CultureInfo.InvariantCulture)))
                    item.Hints.Add(hint.DeepClone());
        }
        int searched = 0, recovered = 0, done = 0;
        var errors = new List<string>();
        foreach (var (id, item) in canonical)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var title = SeedText(item.Seed, "title").Trim();
            if (title.Length > 0)
            {
                searched++;
                try
                {
                    var entry = await _io.RunAsync(() => _database.Read(DatabaseFile.Catalog, session => Canonical(session, id)), cancellationToken).ConfigureAwait(false);
                    if (await rebuild(entry, item.Hints, cancellationToken).ConfigureAwait(false) > 0) recovered++;
                }
                catch (Exception error) when (error is not OperationCanceledException) { errors.Add(title + ": " + error.Message); }
            }
            report?.Invoke(++done, canonical.Count, title.Length > 0 ? title : "Skipped untitled subscribed entry");
        }
        Invalidate();
        return new JsonObject
        {
            ["searched"] = searched, ["recovered"] = recovered,
            ["errors"] = new JsonArray(errors.Take(20).Select(error => (JsonNode?)error).ToArray()),
        };
    }

    /// <summary>
    /// _repair_subbed_orphan_aliases: a source-less entry from an old reset is aliased to the one source-backed entry
    /// with the same normalised title and the same thumbnail or a substantial identical description; ambiguous ones stay.
    /// </summary>
    public JsonObject RepairOrphanAliases(IReadOnlyList<long> rawEntryIds)
    {
        ArgumentNullException.ThrowIfNull(rawEntryIds);
        var raw = rawEntryIds.Where(id => id > 0).Distinct().ToList();
        if (raw.Count == 0) return new JsonObject { ["checked"] = 0, ["aliased"] = 0, ["ambiguous"] = 0 };
        var stamp = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);
        var (checkedCount, aliased, ambiguous) = _database.Write(DatabaseFile.Catalog, session =>
        {
            var rows = session.Query("""
                SELECT e.id,e.preferred_title,e.description,e.thumbnail_url,COALESCE(rc.n,0) record_count FROM entries e LEFT JOIN (
                  SELECT entry_id,COUNT(*) n FROM (SELECT entry_id FROM source_matches WHERE active=1 UNION ALL SELECT entry_id FROM source_variants WHERE active=1) GROUP BY entry_id
                ) rc ON rc.entry_id=e.id
                """);
            var byId = rows.ToDictionary(row => row.Integer("id"));
            var backed = rows.Where(row => row.Integer("record_count") > 0).GroupBy(row => TextMatcher.NormalizeTitle(row.Text("preferred_title")))
                .Where(group => group.Key.Length > 0).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            int count = 0, linked = 0, unclear = 0;
            foreach (var id in raw)
            {
                if (Canonical(session, id) != id || !byId.TryGetValue(id, out var orphan) || orphan.Integer("record_count") > 0) continue;
                count++;
                var normalized = TextMatcher.NormalizeTitle(orphan.Text("preferred_title"));
                if (normalized.Length == 0 || !backed.TryGetValue(normalized, out var candidates)) continue;
                var description = TextMatcher.NormalizeDescription(orphan.Text("description"));
                var thumbnail = orphan.Text("thumbnail_url").Trim();
                var matches = candidates.Where(candidate => candidate.Integer("id") != id
                        && (description.Length >= 80 && description == TextMatcher.NormalizeDescription(candidate.Text("description"))
                            || thumbnail.Length > 0 && thumbnail == candidate.Text("thumbnail_url").Trim()))
                    .Select(candidate => Canonical(session, candidate.Integer("id"))).Distinct().ToList();
                if (matches.Count == 1)
                {
                    session.Execute("""
                        INSERT INTO entry_aliases(old_entry_id,canonical_entry_id,reason,merged_at) VALUES($old,$id,'repair:source-less-subbed-clone-v0.2.80',$stamp)
                        ON CONFLICT(old_entry_id) DO UPDATE SET canonical_entry_id=excluded.canonical_entry_id,reason=excluded.reason,merged_at=excluded.merged_at
                        """, ("$old", id), ("$id", matches[0]), ("$stamp", stamp));
                    linked++;
                }
                else if (matches.Count > 1) unclear++;
            }
            return (count, linked, unclear);
        });
        if (aliased > 0)
        {
            // The persisted duplicate snapshot still lists the aliased clones; a fresh scan rebuilds it.
            _preferences.Set("duplicate_review_snapshot_v1", new JsonObject());
            Invalidate();
        }
        return new JsonObject { ["checked"] = checkedCount, ["aliased"] = aliased, ["ambiguous"] = ambiguous };
    }

    /// <summary>repair_subbed_orphan_aliases_upgrade: runs <see cref="RepairOrphanAliases"/> once (v0.2.80) over the live main-library ids.</summary>
    public async Task<JsonObject> RepairOrphanAliasesUpgradeAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && SourceResponseContract.Text(_preferences.Get(OrphanAliasRepairKey)) == OrphanAliasRepairVersion)
            return new JsonObject { ["skipped"] = true, ["version"] = OrphanAliasRepairVersion };
        if (_main() is not { } main)
            return new JsonObject { ["skipped"] = true, ["version"] = OrphanAliasRepairVersion, ["error"] = NotConfigured };
        List<long> raw;
        try
        {
            var ids = AutoSourcarrSourceIds(await main.SourcesAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            if (ids.Count == 0) return new JsonObject { ["skipped"] = true, ["version"] = OrphanAliasRepairVersion, ["error"] = NotInstalled };
            var library = await main.LibraryMangasAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            raw = library.Where(manga => SourceResponseContract.Identity(manga["sourceId"]) is { } source && ids.Contains(source))
                .Select(manga => UrlEntryId(SourceResponseContract.Text(manga["url"]))).OfType<long>().ToList();
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        { return new JsonObject { ["skipped"] = true, ["version"] = OrphanAliasRepairVersion, ["error"] = error.Message }; }
        var result = await _io.RunAsync(() => RepairOrphanAliases(raw), cancellationToken).ConfigureAwait(false);
        _preferences.Set(OrphanAliasRepairKey, OrphanAliasRepairVersion);
        result["skipped"] = false; result["version"] = OrphanAliasRepairVersion;
        return result;
    }
}
