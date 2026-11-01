using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Core.Imaging;

/// <summary>An exact decoded-pixel fingerprint (thumbnail_fingerprints row).</summary>
public sealed record ThumbnailFingerprint(string Fingerprint, int Width, int Height, string SourceUrl = "");

/// <summary>Where thumbnails come from: a cover URL (with the source's referer) and the source host's own thumbnail of a manga.</summary>
public interface IThumbnailSource
{
    Task<(byte[] Bytes, string ContentType)> FetchUrlAsync(string url, string referer, TimeSpan timeout, CancellationToken cancellationToken);
    Task<(byte[] Bytes, string ContentType)> FetchMangaThumbnailAsync(long mangaId, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Thumbnails through the source runtime client (fetch_external_binary / fetch_manga_thumbnail).</summary>
public sealed class SuwayomiThumbnailSource(SuwayomiClient client) : IThumbnailSource
{
    public Task<(byte[] Bytes, string ContentType)> FetchUrlAsync(string url, string referer, TimeSpan timeout, CancellationToken cancellationToken) =>
        client.FetchBinaryAsync(url, timeout, RasterImages.MaximumBytes, referer, cancellationToken);
    public Task<(byte[] Bytes, string ContentType)> FetchMangaThumbnailAsync(long mangaId, TimeSpan timeout, CancellationToken cancellationToken) =>
        client.FetchMangaThumbnailAsync(mangaId, timeout, cancellationToken);
}

/// <summary>
/// exact_thumbnail_fingerprint: SHA-256 of "RGBA:{w}x{h}:" and the decoded RGBA pixels. Not perceptual: two thumbnails
/// match only when they decode to the same dimensions and pixels (container metadata and encoding details do not
/// matter; resizing or recompression that changes a pixel does). Covers under 96×96 are icons or placeholders and are refused.
/// </summary>
public static class ExactThumbnail
{
    public const int MinimumSide = 96;

    public static ThumbnailFingerprint Compute(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) throw new InvalidDataException("empty thumbnail");
        using var image = RasterImages.Open(bytes);
        if (image.Width < MinimumSide || image.Height < MinimumSide) throw new InvalidDataException("thumbnail is too small for identity matching");
        var pixels = image.RgbaPixels();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(FormattableString.Invariant($"RGBA:{image.Width}x{image.Height}:")));
        hash.AppendData(pixels);
        return new ThumbnailFingerprint(Convert.ToHexStringLower(hash.GetHashAndReset()), image.Width, image.Height);
    }
}

/// <summary>
/// Port-map "Imaging → ThumbnailFingerprint": fingerprints cached per subject in thumbnail_fingerprints ("url:{sha256}" for
/// a cover URL, "source:{source}:{manga}" for a record) for THUMBNAIL_FINGERPRINT_TTL_SECONDS; computed only when
/// EXACT_THUMBNAIL_MATCHING is on. Used as strong identity evidence by duplicate matching (the pass behind
/// MetadataIdentityResolver.Fingerprints), automatic record attachment (a weak candidate with the entry's exact cover) and
/// the import tie-break (only among candidates that already pass the non-image gate). Every failure answers "no fingerprint".
/// </summary>
public sealed class ThumbnailFingerprints
{
    private readonly SqliteDatabase _database;
    private readonly Func<AutoSourcarrSettings> _settings;
    private readonly IThumbnailSource _source;
    private readonly TimeProvider _clock;
    private readonly Func<byte[], ThumbnailFingerprint> _compute;

    public ThumbnailFingerprints(SqliteDatabase database, Func<AutoSourcarrSettings> settings, IThumbnailSource source, TimeProvider? clock = null,
        Func<byte[], ThumbnailFingerprint>? compute = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(source);
        _database = database; _settings = settings; _source = source; _clock = clock ?? TimeProvider.System; _compute = compute ?? ExactThumbnail.Compute;
    }

    private bool Enabled => _settings().ExactThumbnailMatching;
    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Max(1.0, _settings().ThumbnailFingerprintTimeout));

    /// <summary>_thumbnail_url_key.</summary>
    public static string UrlKey(string url) => "url:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((url ?? "").Trim())));
    public static string SourceKey(string sourceId, long mangaId) => "source:" + sourceId + ":" + mangaId.ToString(CultureInfo.InvariantCulture);

    /// <summary>_thumbnail_fingerprint_cache_get: a fresh cached fingerprint, or null (also when matching is off).</summary>
    public ThumbnailFingerprint? CacheGet(string subject)
    {
        if (!Enabled) return null;
        var row = _database.Read(DatabaseFile.Catalog, s => s.Query(
            "SELECT fingerprint,width,height,source_url,updated_at FROM thumbnail_fingerprints WHERE subject_key=$key", ("$key", subject)).FirstOrDefault());
        if (row is null || row.Text("fingerprint").Length == 0) return null;
        if (DateTimeOffset.TryParse(row.Text("updated_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updated)
            && (_clock.GetUtcNow() - updated).TotalSeconds > Math.Max(60, _settings().ThumbnailFingerprintTtlSeconds)) return null;
        return new ThumbnailFingerprint(row.Text("fingerprint"), (int)row.Integer("width"), (int)row.Integer("height"), row.Text("source_url"));
    }

    /// <summary>_thumbnail_fingerprint_cache_put.</summary>
    public ThumbnailFingerprint CachePut(string subject, ThumbnailFingerprint value, string sourceUrl = "")
    {
        ArgumentNullException.ThrowIfNull(value);
        var stamp = _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
        _database.Write(DatabaseFile.Catalog, s => s.Execute("""
            INSERT INTO thumbnail_fingerprints(subject_key,fingerprint,width,height,source_url,updated_at) VALUES($key,$fp,$w,$h,$url,$at)
            ON CONFLICT(subject_key) DO UPDATE SET fingerprint=excluded.fingerprint,width=excluded.width,height=excluded.height,
              source_url=excluded.source_url,updated_at=excluded.updated_at
            """, ("$key", subject), ("$fp", value.Fingerprint), ("$w", value.Width), ("$h", value.Height), ("$url", sourceUrl ?? ""), ("$at", stamp)));
        return value with { SourceUrl = sourceUrl ?? "" };
    }

    /// <summary>_thumbnail_fingerprint_for_url: fetches and fingerprints one cover URL (cached by URL); null on any failure.</summary>
    public async Task<ThumbnailFingerprint?> ForUrlAsync(string url, string referer = "", CancellationToken cancellationToken = default)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0 || !Enabled) return null;
        var key = UrlKey(url);
        if (CacheGet(key) is { } cached) return cached;
        try
        {
            var (bytes, type) = await _source.FetchUrlAsync(url, referer ?? "", Timeout, cancellationToken).ConfigureAwait(false);
            if (type.Length > 0 && !type.Contains("image", StringComparison.OrdinalIgnoreCase)) return null;
            return CachePut(key, _compute(bytes), url);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return null; }
    }

    /// <summary>
    /// _thumbnail_fingerprint_for_source: the record's cover URL (with the source's base URL as referer), else the source
    /// host's thumbnail of the manga; cached per record. Without a manga id only the URL is used.
    /// </summary>
    public async Task<ThumbnailFingerprint?> ForSourceAsync(string sourceId, long mangaId, string thumbnailUrl, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return null;
        thumbnailUrl = (thumbnailUrl ?? "").Trim();
        if (mangaId <= 0) return await ForUrlAsync(thumbnailUrl, "", cancellationToken).ConfigureAwait(false);
        var subject = SourceKey(sourceId, mangaId);
        if (CacheGet(subject) is { } cached) return cached;
        var baseUrl = _database.Read(DatabaseFile.Catalog, s => s.Scalar("SELECT base_url FROM source_config WHERE source_id=$id", ("$id", sourceId)) as string) ?? "";
        if (thumbnailUrl.Length > 0 && await ForUrlAsync(thumbnailUrl, baseUrl, cancellationToken).ConfigureAwait(false) is { } byUrl)
            return CachePut(subject, byUrl, thumbnailUrl);
        try
        {
            var (bytes, type) = await _source.FetchMangaThumbnailAsync(mangaId, Timeout, cancellationToken).ConfigureAwait(false);
            if (type.Length > 0 && !type.Contains("image", StringComparison.OrdinalIgnoreCase)) return null;
            return CachePut(subject, _compute(bytes), thumbnailUrl);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return null; }
    }

    /// <summary>_entry_thumbnail_fingerprints: fingerprints of the entry's best cover candidates (at most 8).</summary>
    public async Task<IReadOnlySet<string>> EntryFingerprintsAsync(long entryId, int limit = 4, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CoverHint> hints;
        try { hints = _database.Read(DatabaseFile.Catalog, s => CoverHints.EntryHints(s, entryId, Math.Clamp(limit, 1, 8))); }
        catch (KeyNotFoundException) { return new HashSet<string>(StringComparer.Ordinal); }
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hint in hints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = hint.SourceId.Length > 0 && hint.SourceMangaId > 0
                ? await ForSourceAsync(hint.SourceId, hint.SourceMangaId, hint.ThumbnailUrl, cancellationToken).ConfigureAwait(false)
                : await ForUrlAsync(hint.ThumbnailUrl, hint.BaseUrl, cancellationToken).ConfigureAwait(false);
            if (found is { Fingerprint.Length: > 0 }) values.Add(found.Fingerprint);
        }
        return values;
    }

    /// <summary>_entry_cached_thumbnail_fingerprints: the stored fingerprints of the entry's records and covers (no network).</summary>
    public IReadOnlySet<string> EntryCachedFingerprints(long entryId) => _database.Read(DatabaseFile.Catalog, s =>
    {
        var id = CatalogueTitles.Canonical(s, entryId);
        var subjects = new List<string>();
        foreach (var row in s.Query("""
            SELECT source_id,source_manga_id,thumbnail_url FROM source_matches WHERE entry_id=$id AND active=1
            UNION ALL SELECT source_id,source_manga_id,thumbnail_url FROM source_variants WHERE entry_id=$id AND active=1
            """, ("$id", id)))
        {
            subjects.Add(SourceKey(row.Text("source_id"), row.Integer("source_manga_id")));
            if (row.Text("thumbnail_url").Length > 0) subjects.Add(UrlKey(row.Text("thumbnail_url")));
        }
        if (s.Scalar("SELECT thumbnail_url FROM entries WHERE id=$id", ("$id", id)) is string own && own.Length > 0) subjects.Add(UrlKey(own));
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in subjects.Distinct(StringComparer.Ordinal))
            if (s.Scalar("SELECT fingerprint FROM thumbnail_fingerprints WHERE subject_key=$key", ("$key", subject)) is string fingerprint && fingerprint.Length > 0)
                result.Add(fingerprint);
        return (IReadOnlySet<string>)result;
    });

    /// <summary>_entry_candidate_exact_thumbnail_match: the candidate record decodes to one of the entry's exact covers.</summary>
    public async Task<bool> CandidateExactMatchAsync(long entryId, string sourceId, JsonObject manga, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manga);
        var left = await EntryFingerprintsAsync(entryId, 3, cancellationToken).ConfigureAwait(false);
        if (left.Count == 0) return false;
        var right = await ForSourceAsync(sourceId, Long(manga["id"] ?? manga["source_manga_id"]), Text(manga["thumbnailUrl"] ?? manga["thumbnail_url"]), cancellationToken).ConfigureAwait(false);
        return right is not null && left.Contains(right.Fingerprint);
    }

    /// <summary>
    /// _import_thumbnail_fingerprints: the backup row's cover plus the covers of its accepted metadata records
    /// (bundle records[].candidate, then the older matches[] rows), at most four URLs.
    /// </summary>
    public async Task<IReadOnlySet<string>> ImportFingerprintsAsync(JsonObject item, JsonObject? bundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var urls = new List<string>();
        void Add(JsonNode? node) { var value = Text(node).Trim(); if (value.Length > 0) urls.Add(value); }
        Add(item["thumbnailUrl"]); Add(item["thumbnail_url"]);
        if (bundle is not null)
        {
            foreach (var row in (bundle["records"] as JsonArray ?? new JsonArray()).Take(8))
                if (row is JsonObject record && record["candidate"] is JsonObject candidate) { Add(candidate["thumbnail_url"]); Add(candidate["thumbnailUrl"]); }
            foreach (var row in (bundle["matches"] as JsonArray ?? new JsonArray()).Take(8))
                if (row is JsonObject match && (match["candidate"] as JsonObject ?? match) is { } candidate) { Add(candidate["thumbnail_url"]); Add(candidate["thumbnailUrl"]); }
        }
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var url in urls.Distinct(StringComparer.Ordinal).Take(4))
            if (await ForUrlAsync(url, "", cancellationToken).ConfigureAwait(false) is { Fingerprint.Length: > 0 } found) values.Add(found.Fingerprint);
        return values;
    }

    /// <summary>
    /// _import_exact_thumbnail_entry_ids: the (canonical, sorted) candidates among the first twelve whose covers decode to
    /// the backup row's exact cover. A tie-break only: the caller passes candidates that already pass the non-image gate.
    /// </summary>
    public async Task<IReadOnlyList<long>> ImportExactEntryIdsAsync(JsonObject item, JsonObject? bundle, IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var wanted = await ImportFingerprintsAsync(item, bundle, cancellationToken).ConfigureAwait(false);
        if (wanted.Count == 0) return [];
        var matched = new SortedSet<long>();
        foreach (var entryId in entryIds.Take(12))
        {
            var values = await EntryFingerprintsAsync(entryId, 3, cancellationToken).ConfigureAwait(false);
            if (values.Overlaps(wanted)) matched.Add(_database.Read(DatabaseFile.Catalog, s => CatalogueTitles.Canonical(s, entryId)));
        }
        return matched.ToArray();
    }

    /// <summary>
    /// _hydrate_duplicate_thumbnail_fingerprints: fingerprints the covers of the most recently updated entries (at most
    /// <paramref name="limit"/>, ≤ 5000), two at a time, each bounded by three fetch timeouts. Answers how many entries
    /// have at least one fingerprint. The duplicate sweep's MetadataIdentityResolver.Fingerprints hook.
    /// </summary>
    public async Task<int> HydrateAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return 0;
        var settings = _settings();
        var count = Math.Clamp(limit <= 0 ? settings.ThumbnailFingerprintEntriesPerPass : limit, 1, 5000);
        var ids = _database.Read(DatabaseFile.Catalog, s => s.Query("SELECT id FROM entries ORDER BY updated_at DESC,id DESC LIMIT $limit", ("$limit", count))
            .Select(row => row.Integer("id")).ToArray());
        var perItem = TimeSpan.FromSeconds(Math.Max(8.0, settings.ThumbnailFingerprintTimeout * 3.0));
        var fingerprinted = 0;
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(settings.MaxSourceConcurrency, 1, 2), CancellationToken = cancellationToken },
            async (id, token) =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(perItem);
                try { if ((await EntryFingerprintsAsync(id, 2, deadline.Token).ConfigureAwait(false)).Count > 0) Interlocked.Increment(ref fingerprinted); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }).ConfigureAwait(false);
        return fingerprinted;
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static long Long(JsonNode? node) => JsonNumber(node);

    /// <summary>A JSON integer whether it was parsed or built in memory (int, long, whole double or digits).</summary>
    internal static long JsonNumber(JsonNode? node) => node is not JsonValue value ? 0
        : value.TryGetValue<long>(out var whole) ? whole
        : value.TryGetValue<int>(out var small) ? small
        : value.TryGetValue<double>(out var real) && real == Math.Floor(real) && Math.Abs(real) < 9e15 ? (long)real
        : value.TryGetValue<string>(out var text) && long.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed
        : 0;
}
