using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Sources.Discovery;

/// <summary>What one crawler pass did ("yielded", "backlog", "no-sources", "not-due", "fetched", "failed", "off") and how long to wait.</summary>
public sealed record CrawlStep(string Outcome, TimeSpan Delay, string SourceId = "", string Mode = "", long Page = 0, int Items = 0, int NewItems = 0, string Error = "");

/// <summary>
/// Port of 0.4.5 <c>schedule_continuous_catalog_discovery</c> (phase 13): a low-priority, long-running job that walks the
/// enabled sources' feeds one source page per pass. Pairs (source × CONTINUOUS_CATALOG_MODES; LATEST only where supported)
/// are visited in (priority, name) order; the cursor (pair index, backlog page per pair) is kept in the
/// <c>continuous_catalog_cursor</c> setting, so a restart continues the walk. Each pair has two due clocks
/// (<see cref="DueScheduler"/> kinds catalog_recent — page one every 30 min — and catalog_backlog — the next page after
/// CONTINUOUS_CATALOG_INTERVAL_SECONDS); <see cref="CrawlPages"/> keeps a fetched page until every record on it is ingested.
/// The crawler yields to higher-priority work (<see cref="Yield"/>: reset rebuilds, hint replay, imports) and to a large
/// enrichment backlog, skips sources paused by <see cref="SourceResultTracker"/> and, by hard rule 3, records a local
/// timeout or capacity deferral as "deferred" (no failure count, no source pause). Only genuinely new records fan out
/// enrichment (<see cref="NewlyCatalogued"/>).
/// </summary>
public sealed class ContinuousCatalogueCrawler
{
    public const string CursorKey = "continuous_catalog_cursor";
    public static readonly JobType Job = new("continuous-catalogue-discovery", "Continuous catalogue discovery", JobPriority.Low, JobFlags.LongRunning,
        StallTimeout: TimeSpan.FromMinutes(5), Cancellable: true);

    private readonly SqliteDatabase _database;
    private readonly EntryIngest _ingest;
    private readonly ISourceCatalogueClient _client;
    private readonly Func<AutoSourcarrSettings> _settings;
    private readonly DueScheduler _due;
    private readonly CrawlPages _pending;
    private readonly TimeProvider _clock;

    public ContinuousCatalogueCrawler(SqliteDatabase database, EntryIngest ingest, ISourceCatalogueClient client, Func<AutoSourcarrSettings> settings,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(ingest); ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(settings);
        _database = database; _ingest = ingest; _client = client; _settings = settings; _clock = clock ?? TimeProvider.System;
        _due = new DueScheduler(database, _clock); _pending = new CrawlPages(database, _clock);
    }

    /// <summary>_source_in_cooldown.</summary>
    public Func<string, bool>? SourcePaused { get; init; }
    /// <summary>_record_source_result (local errors are dropped by the tracker, hard rule 3).</summary>
    public Action<string, Exception?>? SourceResult { get; init; }
    /// <summary>True while higher-priority work runs (Subbed reset rebuild, reset-hint replay, imports).</summary>
    public Func<bool>? Yield { get; init; }
    /// <summary>The enrichment backlog (metadata, identity, translation jobs waiting).</summary>
    public Func<int>? Backlog { get; init; }
    /// <summary>Genuinely new entries of one page (not "known-source-id"): enrichment fan-out.</summary>
    public Action<IReadOnlyList<long>>? NewlyCatalogued { get; init; }
    /// <summary>remember_cover_hint for each crawled manga.</summary>
    public Action<string, JsonObject>? RememberCover { get; init; }

    /// <summary>Enqueues the crawler unless CONTINUOUS_CATALOG_DISCOVERY is off; one crawler per queue (same key merges).</summary>
    public JobHandle? Schedule(JobQueue jobs, string origin = "automatic")
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (!_settings().ContinuousCatalogDiscovery) return null;
        return jobs.Enqueue(Job, "catalogue", RunAsync, origin: origin);
    }

    /// <summary>The job body: waits STARTUP_REFRESH_DELAY_SECONDS (at least 2 s), then one pass at a time until cancelled or switched off.</summary>
    public async Task RunAsync(JobContext job)
    {
        ArgumentNullException.ThrowIfNull(job);
        try { await RunPassesAsync(job).ConfigureAwait(false); }
        catch (OperationCanceledException) when (job.CancelledByUser) { StoppedByUser = true; throw; }
    }

    /// <summary>A person cancelled the crawler in Tasks: it is not started again until the next app start.</summary>
    public bool StoppedByUser { get; private set; }

    private async Task RunPassesAsync(JobContext job)
    {
        var token = job.CancellationToken;
        job.Report(message: "Waiting for the source engine to settle");
        await WaitAsync(job, TimeSpan.FromSeconds(Math.Max(2.0, _settings().StartupRefreshDelaySeconds)), token).ConfigureAwait(false);
        long passes = 0, items = 0, added = 0;
        while (!token.IsCancellationRequested)
        {
            if (!_settings().ContinuousCatalogDiscovery) { job.Result("Switched off (CONTINUOUS_CATALOG_DISCOVERY)"); return; }
            var step = await StepAsync(token).ConfigureAwait(false);
            passes++; items += step.Items; added += step.NewItems;
            job.Report(message: Describe(step) + " · " + added.ToString(CultureInfo.InvariantCulture) + " new records so far");
            job.Result(passes.ToString(CultureInfo.InvariantCulture) + " passes, " + items.ToString(CultureInfo.InvariantCulture) + " records read, "
                + added.ToString(CultureInfo.InvariantCulture) + " new");
            await WaitAsync(job, step.Delay, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
    }

    /// <summary>One crawler pass (the body of the 0.4.5 loop): at most one source page fetched.</summary>
    public async Task<CrawlStep> StepAsync(CancellationToken cancellationToken)
    {
        var settings = _settings();
        var interval = TimeSpan.FromSeconds(Math.Max(0.5, settings.ContinuousCatalogIntervalSeconds));
        if (!settings.ContinuousCatalogDiscovery) return new("off", TimeSpan.FromSeconds(5));
        // Subscriptions and explicit recovery work are always ahead of speculative catalogue growth.
        if (Yield?.Invoke() == true) return new("yielded", TimeSpan.FromSeconds(0.75));
        if ((Backlog?.Invoke() ?? 0) >= Math.Max(8, settings.ContinuousCatalogBacklogLimit)) return new("backlog", TimeSpan.FromSeconds(1));
        var sources = Sources().Where(source => SourcePaused?.Invoke(source.Id) != true).ToArray();
        var modes = settings.ContinuousCatalogModesList;
        var pairs = sources.SelectMany(source => modes.Where(mode => mode != "LATEST" || source.Latest).Select(mode => (Source: source, Mode: mode))).ToArray();
        if (pairs.Length == 0) return new("no-sources", TimeSpan.FromSeconds(5));
        var state = _database.Read(DatabaseFile.Catalog, c => ReadCursor(c));
        var pairIndex = (int)(Long(state["pair_index"]) % pairs.Length);
        var pages = state["pages"] as JsonObject ?? new JsonObject();
        var (src, mode) = pairs[pairIndex];
        var pairKey = mode + "|" + src.Id;
        var configuration = Configuration(mode, src, settings);
        var channel = _due.CrawlLane(pairKey, configuration);
        var workKey = channel + "|" + pairKey;
        var held = _pending.Load(workKey);
        var turn = Long(state["turn"]) + 1;
        if (held is null && !_due.Eligible("catalog_" + channel, pairKey, configuration))
        {
            state["pair_index"] = (pairIndex + 1) % pairs.Length; state["turn"] = turn;
            WriteCursor(state);
            return new("not-due", interval, src.Id, mode);
        }
        var page = held is not null ? held["page"]!.GetValue<long>() : channel == "recent" ? 1 : Math.Max(1, Long(pages[pairKey]));
        Exception? error = null;
        if (held is null)
        {
            _due.Attempted("catalog_" + channel, pairKey, configuration);
            try
            {
                using var priority = SourceWorkContext.Background(true);
                using var metric = Diagnostics.RuntimeMetrics.Shared.Phase("crawl");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(6.0, settings.FederatedSourceTimeout + 2.0)));
                JsonObject payload;
                try { payload = await _client.BrowseAsync(src.Id, mode, (int)Math.Min(page, int.MaxValue), "", deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new TimeoutException("The source page did not answer within its local deadline"); }
                SourceResult?.Invoke(src.Id, null);
                try { held = _pending.SavePage(workKey, page, payload); }
                catch (InvalidDataException invalid) { error = invalid; }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                error = failure;
                SourceResult?.Invoke(src.Id, failure);
            }
        }
        var touched = new List<long>(); var fresh = new List<long>();
        if (held is not null)
        {
            foreach (var manga in _pending.Ready(workKey, Math.Max(1, settings.FederatedItemsPerSource)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mangaId = manga["id"]!.GetValue<long>();
                try
                {
                    RememberCover?.Invoke(src.Id, manga);
                    var result = _ingest.IngestManga(src.Id, (JsonObject)manga.DeepClone());
                    if (result.EntryId is { } entryId)
                    {
                        var canonical = _ingest.CanonicalEntryId(entryId);
                        touched.Add(canonical);
                        if (result.Reason != "known-source-id") fresh.Add(canonical);
                    }
                    _pending.Acknowledge(workKey, mangaId, true);
                }
                catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or InvalidDataException or KeyNotFoundException)
                {
                    _pending.Acknowledge(workKey, mangaId, false);
                }
            }
            var next = _pending.Finish(workKey);
            if (next is { } nextPage)
            {
                if (channel == "backlog") pages[pairKey] = nextPage;
                _due.Finished("catalog_" + channel, pairKey, complete: true, success: true,
                    interval: channel == "recent" ? 1800 : (int)Math.Max(1, Math.Round(interval.TotalSeconds)));
            }
            else _due.Finished("catalog_" + channel, pairKey, deferred: true, reason: "Durable page records remain; retries retain their own deadlines");
            if (fresh.Count > 0) NewlyCatalogued?.Invoke(fresh.Distinct().ToArray());
        }
        if (error is not null)
            // Hard rule 3: a local timeout or capacity deferral is "deferred" (retried in 60 s), never a failure of the source.
            _due.Finished("catalog_" + channel, pairKey, deferred: SourceResultTracker.IsLocal(error) || error is InvalidDataException, reason: error.GetType().Name);
        var saved = new JsonObject
        {
            ["turn"] = turn, ["channel"] = channel, ["pair_index"] = (pairIndex + 1) % pairs.Length, ["pages"] = pages.DeepClone(),
            ["last_source_id"] = src.Id, ["last_source_name"] = src.Name.Length > 0 ? src.Name : src.Id, ["last_mode"] = mode, ["last_page"] = page,
            ["last_items"] = touched.Count, ["last_new_items"] = fresh.Distinct().Count(), ["last_error"] = error?.Message ?? "",
            ["updated_at"] = DueScheduler.Iso(_clock.GetUtcNow()),
        };
        WriteCursor(saved);
        return new(error is null ? "fetched" : "failed", interval, src.Id, mode, page, touched.Count, fresh.Distinct().Count(), error?.Message ?? "");
    }

    /// <summary>The saved cursor (for status and tests).</summary>
    public JsonObject Cursor() => _database.Read(DatabaseFile.Catalog, c => ReadCursor(c));

    private sealed record Source(string Id, string Name, long Priority, bool Latest, string BaseUrl, long? GroupId);

    private Source[] Sources() => _database.Read(DatabaseFile.Catalog, c => c.Query("""
        SELECT source_id,COALESCE(NULLIF(display_name,''),name) name,priority,supports_latest,base_url,group_id FROM source_config
         WHERE available=1 AND enabled_language=1 AND enabled_search=1 ORDER BY priority,name,source_id
        """).Select(row => new Source(row.Text("source_id"), row.Text("name"), row.Integer("priority", 999999), row.Flag("supports_latest"), row.Text("base_url"),
            row["group_id"] is null ? null : row.Integer("group_id"))).ToArray());

    /// <summary>The due clocks restart when what this pair crawls changed (crawl-due-v1 configuration digest).</summary>
    private static string Configuration(string mode, Source source, AutoSourcarrSettings settings)
    {
        var text = "[\"crawl-due-v1\", " + JsonSerializer.Serialize(mode) + ", " + JsonSerializer.Serialize(source.Id) + ", "
            + settings.FederatedItemsPerSource.ToString(CultureInfo.InvariantCulture) + ", "
            + settings.ContinuousCatalogIntervalSeconds.ToString("0.0###############", CultureInfo.InvariantCulture)
            + ", {\"base_url\": " + JsonSerializer.Serialize(source.BaseUrl) + ", \"enabled_language\": true, \"enabled_search\": true, \"group_id\": "
            + (source.GroupId is { } group ? group.ToString(CultureInfo.InvariantCulture) : "null") + "}]";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static JsonObject ReadCursor(DatabaseSession c)
    {
        if (c.Scalar("SELECT value FROM settings WHERE key=$key", ("$key", CursorKey)) is not string text) return new JsonObject();
        try { return JsonNode.Parse(text) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private void WriteCursor(JsonObject state) => _database.Write(DatabaseFile.Catalog, c => c.Execute(
        "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
        ("$key", CursorKey), ("$value", state.ToJsonString())));

    private static long Long(JsonNode? value) => value is JsonValue number && number.TryGetValue<long>(out var result) ? result
        : value is JsonValue real && real.TryGetValue<double>(out var fraction) ? (long)fraction : 0;

    private static string Describe(CrawlStep step) => step.Outcome switch
    {
        "fetched" => step.Mode + " page " + step.Page.ToString(CultureInfo.InvariantCulture) + " of " + step.SourceId + ": " + step.Items.ToString(CultureInfo.InvariantCulture) + " records",
        "failed" => step.Mode + " page " + step.Page.ToString(CultureInfo.InvariantCulture) + " of " + step.SourceId + " failed: " + step.Error,
        "yielded" => "Waiting for higher-priority work",
        "backlog" => "Waiting for the enrichment backlog",
        "no-sources" => "No enabled source to crawl",
        "not-due" => "Nothing due",
        _ => step.Outcome,
    };

    /// <summary>Waits in short slices with heartbeats, so long intervals never look frozen to the watchdog.</summary>
    private static async Task WaitAsync(JobContext job, TimeSpan delay, CancellationToken token)
    {
        var remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            var slice = remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30);
            await Task.Delay(slice, token).ConfigureAwait(false);
            job.Heartbeat();
            remaining -= slice;
        }
    }
}
