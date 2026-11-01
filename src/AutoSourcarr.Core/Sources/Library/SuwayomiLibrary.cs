using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Sources.Library;

/// <summary>Caps for the download-size walk: entries visited (files and folders) and wall time.</summary>
public sealed record DownloadWalkLimits(int MaxEntries, TimeSpan MaxDuration)
{
    public static DownloadWalkLimits Default { get; } = new(500_000, TimeSpan.FromSeconds(20));
}

/// <summary>
/// The main-library ("Subbed") view: manga in the main Suwayomi library that come from the AutoSourcarr reader source,
/// mapped back to catalogue entry ids (0.4.5 service.py subbed_entry_ids / cached_subbed_payload / cached_library_status /
/// main_download_size_status and the _main_* helpers). The reader source exposes each entry as SManga.url = entry id, so the
/// trailing number of the library manga's url is the (possibly merged) entry id; aliases resolve through entry_aliases.
/// </summary>
/// <remarks>
/// Cache-first: <see cref="CachedSubbedPayload"/> and <see cref="CachedLibraryStatus"/> never touch the network or the
/// filesystem; <see cref="ScheduleStatusRefresh"/> refreshes both in the background (one refresh at a time). The last
/// live projection (entry ids and per-entry counts) is kept in the catalogue settings table under
/// <see cref="DisplaySnapshotKey"/>, the same key 0.4.5 used, and shown as "refreshing" after a restart until the first
/// live read. Decisions that change membership must use a live read (<see cref="SubbedEntryIdsAsync"/> with force).
/// </remarks>
public sealed partial class SuwayomiLibrary : IAsyncDisposable
{
    public const string DisplaySnapshotKey = "subbed_display_snapshot_v1";
    /// <summary>Private search token the reader source resolves from the local catalogue without contacting any source.</summary>
    public const string EntryIdToken = "__autosourcarr_entry_id__:";
    private const string NotConfigured = "MAIN_SUWAYOMI_URL is not configured";
    private const string NotInstalled = "AutoSourcarr source is not installed in the main Suwayomi instance";
    private static readonly TimeSpan SourceIdTtl = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan DownloadSizeTtl = TimeSpan.FromSeconds(60);
    private const int AliasHops = 64;

    private readonly SqliteDatabase _database;
    private readonly SettingsRepository _preferences;
    private readonly Func<AutoSourcarrSettings> _settings;
    private readonly Func<SuwayomiClient?> _main;
    private readonly BoundedWorkScheduler _io;
    private readonly TimeProvider _clock;
    private readonly DownloadWalkLimits _limits;
    private readonly Action<Exception>? _recordError;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _walk = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private Snapshot? _live, _display;
    private string? _savedProjection;
    private (DateTimeOffset At, JsonObject Payload)? _size;
    private (DateTimeOffset At, string Id)? _sourceId;
    private Task? _refresh;
    private long _generation;
    private bool _disposed;

    /// <param name="main">Read when called: the main Suwayomi client (null when MAIN_SUWAYOMI_URL is empty).</param>
    /// <param name="io">The bounded IO scheduler; catalogue reads, the snapshot write and the download walk run on it.</param>
    /// <param name="recordError">Optional sink for failures of background refreshes.</param>
    public SuwayomiLibrary(SqliteDatabase database, SettingsRepository preferences, Func<AutoSourcarrSettings> settings,
        Func<SuwayomiClient?> main, BoundedWorkScheduler io, Action<Exception>? recordError = null,
        TimeProvider? clock = null, DownloadWalkLimits? limits = null)
    {
        _database = database; _preferences = preferences; _settings = settings; _main = main; _io = io;
        _recordError = recordError; _clock = clock ?? TimeProvider.System; _limits = limits ?? DownloadWalkLimits.Default;
    }

    private sealed record EntryStat(long EntryId, JsonNode? MainMangaId, long? ChapterCount, long? DownloadedChapterCount);
    private sealed record Snapshot(DateTimeOffset At, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, EntryStat> Stats,
        string ItemsJson, string ObservedAt);

    // ---- main Suwayomi helpers (_main_autosourcarr_source_id, _main_manga_for_autosourcarr_entry, _ensure_main_suwayomi_library)

    /// <summary>The AutoSourcarr source id in the main Suwayomi instance; cached for five minutes.</summary>
    /// <exception cref="InvalidOperationException">Main Suwayomi is not configured or the source is not installed there.</exception>
    public async Task<string> MainAutoSourcarrSourceIdAsync(CancellationToken cancellationToken = default)
    {
        var main = _main() ?? throw new InvalidOperationException(NotConfigured);
        lock (_gate)
            if (_sourceId is { } cached && _clock.GetUtcNow() - cached.At <= SourceIdTtl) return cached.Id;
        var sources = await main.SourcesAsync(cancellationToken).ConfigureAwait(false);
        var id = AutoSourcarrSourceIds(sources).FirstOrDefault() ?? throw new InvalidOperationException(NotInstalled);
        lock (_gate) _sourceId = (_clock.GetUtcNow(), id);
        return id;
    }

    /// <summary>
    /// Finds the main-Suwayomi manga for one entry through the AutoSourcarr source: first the private entry-id token, then
    /// the given titles, then conservative article/colour fallbacks. Only a manga whose url resolves to the same canonical
    /// entry is returned; null when none does or main Suwayomi is not configured.
    /// </summary>
    public async Task<JsonObject?> MainMangaForEntryAsync(long entryId, string sourceId, IReadOnlyList<string> titles,
        CancellationToken cancellationToken = default)
    {
        if (_main() is not { } main) return null;
        var wanted = await _io.RunAsync(() => _database.Read(DatabaseFile.Catalog, session => Canonical(session, entryId)), cancellationToken)
            .ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        async Task<JsonObject?> TryQueriesAsync(IEnumerable<string> queries)
        {
            foreach (var title in queries)
            {
                var query = (title ?? "").Trim();
                var normalized = TextMatcher.NormalizeTitle(query);
                if (query.Length == 0 || !seen.Add(normalized)) continue;
                JsonObject result;
                try { result = await main.FetchSourceMangaAsync(sourceId, "SEARCH", 1, query, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { continue; }
                if (result["mangas"] is not JsonArray mangas) continue;
                var candidates = new List<(JsonObject Manga, long RawId)>();
                foreach (var node in mangas)
                    if (node is JsonObject manga && UrlEntryId(SourceResponseContract.Text(manga["url"])) is long rawId) candidates.Add((manga, rawId));
                if (candidates.Count == 0) continue;
                var canonical = await _io.RunAsync(() => _database.Read(DatabaseFile.Catalog,
                    session => candidates.Select(candidate => Canonical(session, candidate.RawId)).ToArray()), cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < candidates.Count; index++)
                    if (canonical[index] == wanted) return (JsonObject)candidates[index].Manga.DeepClone();
            }
            return null;
        }

        // Fast path: the reader source forwards this token back to AutoSourcarr, which resolves it from the local
        // catalogue without touching any upstream source. One cheap round trip replaces several title searches.
        return await TryQueriesAsync([EntryIdToken + wanted.ToString(CultureInfo.InvariantCulture)]).ConfigureAwait(false)
            ?? await TryQueriesAsync(titles).ConfigureAwait(false)
            ?? await TryQueriesAsync(titles.SelectMany(title => FallbackTitleQueries(title)).ToList()).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds one manga to the main Suwayomi library and verifies it (three attempts, then a library read for servers whose
    /// mutation answer omits inLibrary). <paramref name="superseded"/> reports a newer choice for the same import item: the
    /// add is then only observed, never undone. The Subbed view has no flag of its own; it is only main-library state.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not configured, or main Suwayomi did not confirm the manga in its library.</exception>
    public async Task<JsonObject> EnsureMainLibraryAsync(long mangaId, Func<bool>? superseded = null, CancellationToken cancellationToken = default)
    {
        var main = _main() ?? throw new InvalidOperationException(NotConfigured);
        var last = new JsonObject();
        Exception? lastError = null;
        var attempted = false;
        bool Superseded() => superseded?.Invoke() == true;

        async Task<JsonObject> SupersededAsync(IReadOnlyList<JsonObject>? observed = null)
        {
            // An issued request may have succeeded despite a lost answer: observe it without another mutation.
            var result = new JsonObject { ["id"] = mangaId, ["superseded"] = true, ["library_state_verified"] = false };
            if (!attempted) return result;
            if (observed is null && SourceResponseContract.Boolean(last["inLibrary"], out _))
            {
                var copy = (JsonObject)last.DeepClone(); copy["superseded"] = true; copy["library_state_verified"] = true; return copy;
            }
            try
            {
                var library = observed ?? await main.LibraryMangasAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                result["inLibrary"] = library.Any(item => SameManga(item, mangaId));
                result["library_state_verified"] = true;
            }
            catch (Exception error) when (error is not OperationCanceledException) { _recordError?.Invoke(error); }
            return result;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (Superseded()) return await SupersededAsync().ConfigureAwait(false);
            last = new JsonObject();
            try
            {
                attempted = true;
                last = await main.SetMangaInLibraryAsync(mangaId, true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException) { lastError = error; }
            if (Superseded()) return await SupersededAsync().ConfigureAwait(false);
            if (SourceResponseContract.Boolean(last["inLibrary"], out var inLibrary) && inLibrary) { Invalidate(); return last; }
            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), _clock, cancellationToken).ConfigureAwait(false);
                if (Superseded()) return await SupersededAsync().ConfigureAwait(false);
            }
        }
        if (Superseded()) return await SupersededAsync().ConfigureAwait(false);
        try
        {
            var library = await main.LibraryMangasAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (Superseded()) return await SupersededAsync(library).ConfigureAwait(false);
            if (library.FirstOrDefault(item => SameManga(item, mangaId)) is { } confirmed) { Invalidate(); return (JsonObject)confirmed.DeepClone(); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            lastError ??= error;
            if (Superseded()) return new JsonObject { ["id"] = mangaId, ["superseded"] = true, ["library_state_verified"] = false };
        }
        throw new InvalidOperationException("Main Suwayomi did not confirm manga " + mangaId.ToString(CultureInfo.InvariantCulture)
            + " in its library" + (lastError is null ? "" : ": " + lastError.Message), lastError);
    }

    // ---- the Subbed set (subbed_entry_ids, _map_subbed_library_items, cached_subbed_payload)

    /// <summary>
    /// Main-library manga from the AutoSourcarr source mapped to entry ids: entry_ids, items (each with
    /// autosourcarr_entry_id), stats_by_entry, ok, error. Served from the cache for SUBBED_CACHE_TTL_SECONDS unless
    /// <paramref name="force"/>. Failures answer ok=false and leave the cache as it was.
    /// </summary>
    public async Task<JsonObject> SubbedEntryIdsAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(0, _settings().SubbedCacheTtlSeconds));
        if (!force)
        {
            Snapshot? cached;
            lock (_gate) cached = _live;
            if (cached is not null && _clock.GetUtcNow() - cached.At <= ttl) return Payload(cached, includeItems: true);
        }
        if (_main() is not { } main) return Failure(NotConfigured);
        try
        {
            long generation;
            lock (_gate) generation = _generation;
            var ids = AutoSourcarrSourceIds(await main.SourcesAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            if (ids.Count == 0) return Failure(NotInstalled);
            var library = await main.LibraryMangasAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var items = library.Where(manga => SourceResponseContract.Identity(manga["sourceId"]) is { } source && ids.Contains(source)).ToList();
            var mapped = await _io.RunAsync(() => Map(items), cancellationToken).ConfigureAwait(false);
            lock (_gate)
                if (generation == _generation && !_disposed) _live = mapped;
            try { await _io.RunAsync(() => { PersistDisplaySnapshot(mapped); return true; }, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { _recordError?.Invoke(error); }
            return Payload(mapped, includeItems: true);
        }
        // A transport timeout is a failure answer; only the caller's own cancellation propagates.
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return Failure(error.Message); }
    }

    /// <summary>Maps main-library manga (already filtered to the AutoSourcarr source) to live catalogue entries. Synchronous catalogue read.</summary>
    public JsonObject MapLibraryItems(IReadOnlyList<JsonObject> items) => Payload(Map(items), includeItems: true);

    /// <summary>The last completed main-library snapshot without network I/O (refreshing=true while none is available yet).</summary>
    public JsonObject CachedSubbedPayload()
    {
        Snapshot? live, display;
        lock (_gate) { live = _live; display = _display; }
        if (live is not null)
        {
            var result = Payload(live, includeItems: true);
            result["refreshing"] = false;
            result["cache_age_seconds"] = Age(live.At);
            return result;
        }
        if (display is not null)
        {
            var result = Payload(display, includeItems: false);
            result["refreshing"] = true; result["saved_snapshot"] = true; result["observed_at"] = display.ObservedAt;
            return result;
        }
        return new JsonObject
        {
            ["ok"] = null, ["entry_ids"] = new JsonArray(), ["items"] = new JsonArray(), ["stats_by_entry"] = new JsonObject(),
            ["error"] = "Library status is refreshing", ["refreshing"] = true
        };
    }

    /// <summary>Drops the live snapshot after a membership change; the last projection stays visible as refreshing.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            if (_live is not null) _display = _live with { ObservedAt = _live.At.ToString("O", CultureInfo.InvariantCulture) };
            _live = null; _generation++;
        }
    }

    // ---- display snapshot (_restore_subbed_display_snapshot, _persist_subbed_display_snapshot)

    /// <summary>Startup: loads the saved projection for the configured main Suwayomi (display only). Runs on the IO scheduler.</summary>
    public Task RestoreDisplaySnapshotAsync(CancellationToken cancellationToken = default)
        => _io.RunAsync(() => { RestoreDisplaySnapshot(); return true; }, cancellationToken);

    /// <summary>Synchronous catalogue read; ignores a snapshot saved for another main Suwayomi address or with invalid content.</summary>
    public void RestoreDisplaySnapshot()
    {
        var origin = Origin();
        if (origin.Length == 0) return;
        if (_preferences.Get(DisplaySnapshotKey) is not JsonObject saved || SourceResponseContract.Text(saved["origin"]) != origin) return;
        var payload = saved["payload"] as JsonObject ?? new JsonObject();
        var rawIds = new List<long>();
        if (payload["entry_ids"] is JsonArray idArray)
            foreach (var node in idArray)
            {
                if (!SourceResponseContract.TryLong(node, out var id)) return; // invalid snapshot: ignore it whole, like 0.4.5
                rawIds.Add(id);
            }
        var snapshot = _database.Read(DatabaseFile.Catalog, session =>
        {
            var ids = new List<long>(); var unique = new HashSet<long>();
            foreach (var raw in rawIds) { var canonical = Canonical(session, raw); if (unique.Add(canonical)) ids.Add(canonical); }
            var live = ids.Where(id => Exists(session, id)).ToHashSet();
            var stats = new Dictionary<long, EntryStat>();
            if (payload["stats_by_entry"] is JsonObject savedStats)
                foreach (var (key, value) in savedStats)
                {
                    if (!long.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var raw) || value is not JsonObject stat) continue;
                    var canonical = Canonical(session, raw);
                    if (!live.Contains(canonical)) continue;
                    var incoming = new EntryStat(canonical, stat["main_manga_id"]?.DeepClone(), Count(stat["chapter_count"]), Count(stat["downloaded_chapter_count"]));
                    stats[canonical] = stats.TryGetValue(canonical, out var previous) ? Combine(previous, incoming) : incoming;
                }
            return new Snapshot(_clock.GetUtcNow(), ids.Where(live.Contains).ToArray(), stats, "[]", SourceResponseContract.Text(saved["observed_at"]));
        });
        lock (_gate) _display = snapshot;
    }

    private void PersistDisplaySnapshot(Snapshot snapshot)
    {
        var projection = Projection(snapshot).ToJsonString();
        lock (_gate)
            if (projection == _savedProjection) return; // counts rarely change between refreshes: skip the writer lock
        _preferences.Set(DisplaySnapshotKey, new JsonObject
        {
            ["origin"] = Origin(),
            ["observed_at"] = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            ["payload"] = JsonNode.Parse(projection)
        });
        lock (_gate) _savedProjection = projection;
    }

    // ---- Status page (cached_library_status, schedule_status_aux_refresh, main_download_size_status)

    /// <summary>The health route's library block, from caches only: never waits on main Suwayomi or the filesystem.</summary>
    public JsonObject CachedLibraryStatus()
    {
        var subbed = CachedSubbedPayload();
        long downloaded = 0;
        if (subbed["stats_by_entry"] is JsonObject stats)
            foreach (var (_, value) in stats)
                if (value is JsonObject row && SourceResponseContract.TryLong(row["downloaded_chapter_count"], out var count)) downloaded += count;
        JsonObject size;
        (DateTimeOffset At, JsonObject Payload)? cached;
        lock (_gate) cached = _size;
        if (cached is { } stamped)
        {
            size = (JsonObject)stamped.Payload.DeepClone();
            size["refreshing"] = false;
            size["cache_age_seconds"] = Age(stamped.At);
        }
        else
        {
            var path = _settings().MainSuwayomiDownloadsPath ?? "";
            size = new JsonObject
            {
                ["configured"] = path.Trim().Length > 0, ["available"] = false, ["bytes"] = null, ["files"] = 0, ["path"] = path,
                ["error"] = "Download size is refreshing", ["refreshing"] = true
            };
        }
        return new JsonObject
        {
            ["ok"] = subbed["ok"]?.DeepClone(),
            ["error"] = SourceResponseContract.Text(subbed["error"]),
            ["refreshing"] = SourceResponseContract.Boolean(subbed["refreshing"], out var refreshing) && refreshing,
            ["subbed_entries"] = (subbed["entry_ids"] as JsonArray)?.Count ?? 0,
            ["downloaded_chapters"] = downloaded,
            ["download_size"] = size
        };
    }

    /// <summary>
    /// Starts one background refresh of the Subbed snapshot and the download size, unless both are fresh (Subbed within
    /// max(5 s, TTL), size within 60 s) or a refresh already runs. Returns whether a refresh started.
    /// </summary>
    public bool ScheduleStatusRefresh(bool force = false)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_refresh is { IsCompleted: false }) return false;
            if (!force)
            {
                var now = _clock.GetUtcNow();
                var ttl = TimeSpan.FromSeconds(Math.Max(5, _settings().SubbedCacheTtlSeconds));
                if (_live is { } live && now - live.At <= ttl && _size is { } size && now - size.At <= DownloadSizeTtl) return false;
            }
            var token = _stopping.Token;
            _refresh = Task.Run(() => RefreshAsync(force, token), CancellationToken.None);
            return true;
        }
    }

    /// <summary>Waits for the background refresh started last (tests and orderly shutdown).</summary>
    public Task RefreshCompletion { get { lock (_gate) return _refresh ?? Task.CompletedTask; } }

    private async Task RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        try
        {
            await SubbedEntryIdsAsync(force, cancellationToken).ConfigureAwait(false);
            await DownloadSizeStatusAsync(force, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WorkDeferredException) { } // IO queue is full: the next health poll tries again
        catch (Exception error) { _recordError?.Invoke(error); }
    }

    /// <summary>The download size walk on the IO scheduler (bounded by <see cref="DownloadWalkLimits"/>).</summary>
    public Task<JsonObject> DownloadSizeStatusAsync(bool force = false, CancellationToken cancellationToken = default)
        => _io.RunAsync(() => MainDownloadSizeStatus(force, cancellationToken), cancellationToken);

    /// <summary>
    /// Downloaded bytes under MAIN_SUWAYOMI_DOWNLOADS_PATH (or its single sub-folder whose name contains "autosourcarr"),
    /// cached for 60 seconds. Synchronous filesystem walk: call through <see cref="DownloadSizeStatusAsync"/>. The walk
    /// does not follow linked folders and stops at the entry/time caps (truncated=true, bytes is then a lower bound).
    /// A second caller while a walk runs gets the cached answer instead of a second walk.
    /// </summary>
    public JsonObject MainDownloadSizeStatus(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && FreshSize() is { } fresh) return fresh;
        if (!_walk.Wait(0))
        {
            lock (_gate)
                if (_size is { } running) return (JsonObject)running.Payload.DeepClone();
            return new JsonObject
            {
                ["configured"] = true, ["available"] = false, ["bytes"] = null, ["files"] = 0,
                ["path"] = _settings().MainSuwayomiDownloadsPath ?? "", ["error"] = "Download size is refreshing", ["truncated"] = false
            };
        }
        try
        {
            var payload = Walk(cancellationToken);
            lock (_gate) _size = (_clock.GetUtcNow(), (JsonObject)payload.DeepClone());
            return payload;
        }
        finally { _walk.Release(); }
    }

    private JsonObject? FreshSize()
    {
        lock (_gate)
            return _size is { } size && _clock.GetUtcNow() - size.At <= DownloadSizeTtl ? (JsonObject)size.Payload.DeepClone() : null;
    }

    private JsonObject Walk(CancellationToken cancellationToken)
    {
        static JsonObject Result(bool configured, bool available, long? bytes, long files, string path, string error, bool truncated = false) => new()
        {
            ["configured"] = configured, ["available"] = available, ["bytes"] = bytes, ["files"] = files,
            ["path"] = path, ["error"] = error, ["truncated"] = truncated
        };
        var raw = (_settings().MainSuwayomiDownloadsPath ?? "").Trim();
        if (raw.Length == 0) return Result(false, false, null, 0, "", "MAIN_SUWAYOMI_DOWNLOADS_PATH is not configured");
        if (!Directory.Exists(raw)) return Result(true, false, null, 0, raw, "Configured download path is not mounted/readable");
        var scanRoot = raw;
        try
        {
            var direct = Directory.EnumerateDirectories(raw)
                .Where(path => Path.GetFileName(path).Contains("autosourcarr", StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (direct.Count == 1) scanRoot = direct[0];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = true, ReturnSpecialDirectories = false, AttributesToSkip = 0
        };
        var started = _clock.GetTimestamp();
        long bytes = 0, files = 0, visited = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(scanRoot));
        while (pending.TryPop(out var directory))
        {
            try
            {
                foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++visited > _limits.MaxEntries || (visited % 256 == 0 && _clock.GetElapsedTime(started) > _limits.MaxDuration))
                        return Result(true, true, bytes, files, scanRoot, string.Create(CultureInfo.InvariantCulture,
                            $"The download walk stopped at its safety limit ({_limits.MaxEntries} entries or {_limits.MaxDuration.TotalSeconds:0} s); the size is a lower bound."), true);
                    if (entry is DirectoryInfo child)
                    {
                        if ((child.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                    }
                    else if (entry is FileInfo file)
                    {
                        try { bytes += file.Length; files++; }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // os.walk ignores unreadable folders too
        }
        return Result(true, true, bytes, files, scanRoot, "");
    }

    // ---- shared helpers

    /// <summary>Second-pass search hints (0.4.5 _fallback_title_queries): no leading "The" for long titles, no colour edition qualifier.</summary>
    internal static IReadOnlyList<string> FallbackTitleQueries(string? title)
    {
        static string Collapse(string? value) => string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        var raw = Collapse(title);
        if (raw.Length == 0) return [];
        var output = new List<string>();
        void Add(string value)
        {
            value = Collapse(value);
            if (value.Length == 0 || TextMatcher.NormalizeTitle(value) == TextMatcher.NormalizeTitle(raw)) return;
            if (!output.Any(existing => TextMatcher.NormalizeTitle(existing) == TextMatcher.NormalizeTitle(value))) output.Add(value);
        }
        var articleBase = TextMatcher.ColorBaseTitle(raw);
        var allowArticle = Words().Matches(articleBase).Count >= 4;
        var withoutThe = LeadingThe().Replace(raw, "").Trim();
        if (allowArticle && withoutThe != raw) Add(withoutThe);
        var withoutColor = TextMatcher.ColorBaseTitle(raw);
        if (withoutColor != raw)
        {
            Add(withoutColor);
            if (allowArticle) Add(LeadingThe().Replace(withoutColor, "").Trim());
        }
        return output.Take(3).ToArray();
    }

    /// <summary>The entry id at the end of a reader-source manga url (trailing slashes ignored); null when there is none.</summary>
    internal static long? UrlEntryId(string? url)
    {
        var match = TrailingDigits().Match((url ?? "").Trim().TrimEnd('/'));
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private static IEnumerable<string> AutoSourcarrSourceIds(IEnumerable<JsonObject> sources) => sources
        .Where(source => (SourceResponseContract.Text(source["name"]) + " " + SourceResponseContract.Text(source["displayName"]))
            .Contains("autosourcarr", StringComparison.OrdinalIgnoreCase))
        .Select(source => SourceResponseContract.Identity(source["id"]))
        .OfType<string>()
        .Where(id => id.Length > 0);

    private Snapshot Map(IReadOnlyList<JsonObject> items) => _database.Read(DatabaseFile.Catalog, session =>
    {
        var ids = new List<long>(); var unique = new HashSet<long>();
        var stats = new Dictionary<long, EntryStat>();
        var mapped = new JsonArray();
        foreach (var manga in items)
        {
            if (UrlEntryId(SourceResponseContract.Text(manga["url"])) is not long raw) continue;
            var canonical = Canonical(session, raw);
            if (!Exists(session, canonical)) continue;
            if (unique.Add(canonical)) ids.Add(canonical);
            var stat = new EntryStat(canonical, manga["id"]?.DeepClone(), Count((manga["chapters"] as JsonObject)?["totalCount"]), Count(manga["downloadCount"]));
            // One canonical entry normally has one library manga; an old alias can leave more. Keep the highest counts.
            stats[canonical] = stats.TryGetValue(canonical, out var previous) ? Combine(previous, stat) : stat;
            var copy = (JsonObject)manga.DeepClone();
            copy["autosourcarr_entry_id"] = canonical;
            mapped.Add(copy);
        }
        return new Snapshot(_clock.GetUtcNow(), ids.ToArray(), stats, mapped.ToJsonString(), "");
    });

    private static EntryStat Combine(EntryStat previous, EntryStat next) => next with
    {
        ChapterCount = Max(previous.ChapterCount, next.ChapterCount),
        DownloadedChapterCount = Max(previous.DownloadedChapterCount, next.DownloadedChapterCount)
    };
    private static long? Max(long? left, long? right) => left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);
    private static long? Count(JsonNode? value) => SourceResponseContract.TryLong(value, out var number) ? number : null;

    private static long Canonical(DatabaseSession session, long entryId)
    {
        var current = entryId; var seen = new HashSet<long>();
        for (var hop = 0; hop < AliasHops && seen.Add(current); hop++)
        {
            var next = session.Scalar("SELECT canonical_entry_id FROM entry_aliases WHERE old_entry_id=$id", ("$id", current));
            if (next is null) return current;
            current = Convert.ToInt64(next, CultureInfo.InvariantCulture);
        }
        return current; // a cycle stops where it repeats, like 0.4.5's canonical_entry_id
    }

    private static bool Exists(DatabaseSession session, long entryId) => session.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", entryId)) is not null;

    private static bool SameManga(JsonObject row, long mangaId)
        => SourceResponseContract.TryLong(row["id"], out var id) && id == mangaId
            && (!SourceResponseContract.Boolean(row["inLibrary"], out var inLibrary) || inLibrary);

    private static JsonObject Projection(Snapshot snapshot) => new()
    {
        ["entry_ids"] = new JsonArray(snapshot.EntryIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        ["stats_by_entry"] = Stats(snapshot)
    };

    private static JsonObject Stats(Snapshot snapshot)
    {
        var stats = new JsonObject();
        foreach (var (id, stat) in snapshot.Stats.OrderBy(pair => pair.Key))
            stats[id.ToString(CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["entry_id"] = stat.EntryId, ["main_manga_id"] = stat.MainMangaId?.DeepClone(),
                ["chapter_count"] = stat.ChapterCount, ["downloaded_chapter_count"] = stat.DownloadedChapterCount
            };
        return stats;
    }

    private static JsonObject Payload(Snapshot snapshot, bool includeItems)
    {
        var projection = Projection(snapshot);
        return new JsonObject
        {
            ["entry_ids"] = projection["entry_ids"]!.DeepClone(),
            ["items"] = includeItems ? JsonNode.Parse(snapshot.ItemsJson) : new JsonArray(),
            ["stats_by_entry"] = projection["stats_by_entry"]!.DeepClone(),
            ["ok"] = true,
            ["error"] = ""
        };
    }

    private static JsonObject Failure(string error) => new()
    {
        ["entry_ids"] = new JsonArray(), ["items"] = new JsonArray(), ["stats_by_entry"] = new JsonObject(), ["ok"] = false, ["error"] = error
    };

    private double Age(DateTimeOffset at) => Math.Max(0, (_clock.GetUtcNow() - at).TotalSeconds);
    private string Origin() => (_settings().MainSuwayomiUrl ?? "").Trim().TrimEnd('/');

    public async ValueTask DisposeAsync()
    {
        Task? refresh;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; refresh = _refresh;
        }
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (refresh is not null)
        {
            try { await refresh.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        // A walk started outside the refresh holds the walk gate; wait for it before releasing the gate's handle.
        await _walk.WaitAsync().ConfigureAwait(false);
        _walk.Dispose();
        _stopping.Dispose();
    }

    [GeneratedRegex(@"([0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingDigits();
    [GeneratedRegex(@"[\w’'-]+")]
    private static partial Regex Words();
    [GeneratedRegex(@"^the\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingThe();
}
