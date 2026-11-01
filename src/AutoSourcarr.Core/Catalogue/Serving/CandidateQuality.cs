using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Imaging;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Core.Catalogue.Serving;

/// <summary>Sampling limits (QUALITY_ANALYSIS_CONCURRENCY, QUALITY_SAMPLE_TIMEOUT, QUALITY_OVERALL_TIMEOUT).</summary>
public sealed record QualityOptions(int Concurrency, TimeSpan SampleTimeout, TimeSpan OverallTimeout)
{
    public static QualityOptions Default { get; } = new(2, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
}

/// <summary>
/// CHAP-5 (0.4.5 quality.py): a candidate's quality score from a few sample pages — resolution (median size, up to 70),
/// file size (up to 20) and integrity (10, minus 5 per broken page). A run that could not observe every sampled page keeps
/// the old score and the old sample; only a complete run changes them. Every attempt is recorded (quality_attempts).
/// Perceptual hashes (imagehash in 0.4.5) are not computed; phash_first/phash_last stay empty.
/// </summary>
public sealed class CandidateQuality(SqliteDatabase database, object sourceWriteGate, ICandidatePageReader pages, IPageRasterDecoder decoder,
    Func<QualityOptions>? options = null, IDecisionLog? decisions = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<QualityOptions> _options = options ?? (() => QualityOptions.Default);

    /// <summary>The sampled page indexes: <paramref name="sampleCount"/> (2..12) spread evenly over the chapter, first and last included.</summary>
    public static IReadOnlyList<int> SampleIndexes(int count, int sampleCount)
    {
        if (count <= 0) return [];
        sampleCount = Math.Clamp(sampleCount, 2, 12);
        return Enumerable.Range(0, sampleCount)
            .Select(i => (int)Math.Round(i * (count - 1) / (double)Math.Max(1, sampleCount - 1), MidpointRounding.ToEven))
            .Distinct().Order().ToArray();
    }

    /// <summary>The released score formula.</summary>
    public static double Score(int medianWidth, int medianHeight, long averageBytes, int brokenPages)
    {
        var megapixels = medianWidth * (double)medianHeight / 1_000_000;
        var resolution = Math.Min(70.0, megapixels * 15.0);
        var file = Math.Min(20.0, averageBytes / 150_000.0);
        var integrity = Math.Max(0.0, 10.0 - brokenPages * 5.0);
        return Math.Min(100.0, resolution + file + integrity);
    }

    public async Task<JsonObject> AnalyzeAsync(long candidateId, int sampleCount, DecisionActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var candidate = database.Read(DatabaseFile.Origins, session => session.Query("""
            SELECT cc.id,cc.quality_score,cc.source_id,cc.source_manga_id,cc.source_chapter_id,vc.entry_id FROM chapter_candidates cc
            JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id WHERE cc.id=$id
            """, ("$id", candidateId)).FirstOrDefault()) ?? throw new KeyNotFoundException("Chapter candidate not found");
        JsonObject result;
        try { result = await SampleAsync(candidateId, candidate, sampleCount, cancellationToken).ConfigureAwait(false); }
        catch (Exception error)
        {
            Attempt(candidateId, error is SourceWorkDeferredException ? "deferred" : "incomplete", 0, 0, error.GetType().Name);
            throw;
        }
        var observed = ReaderInt(result, "sampled_pages") - ReaderInt(result, "unobserved_pages");
        Attempt(candidateId, result["outcome"]!.GetValue<string>(), observed, ReaderInt(result, "unobserved_pages"), result["reason"]!.GetValue<string>());
        if (result["complete"]!.GetValue<bool>())
            decisions?.Record(new DecisionEvent("chapter.quality", actor, [candidate.Integer("entry_id")],
                [candidate.Text("source_id") + ":" + candidate.Integer("source_manga_id").ToString(CultureInfo.InvariantCulture)],
                "Quality sampled for chapter candidate " + candidateId.ToString(CultureInfo.InvariantCulture),
                new JsonObject { ["candidate_id"] = candidateId, ["quality_score"] = candidate["quality_score"] is null ? null : candidate.Number("quality_score") },
                new JsonObject { ["candidate_id"] = candidateId, ["quality_score"] = result["quality_score"]?.DeepClone() }));
        return result;
    }

    private async Task<JsonObject> SampleAsync(long candidateId, DatabaseRow candidate, int sampleCount, CancellationToken cancellationToken)
    {
        var count = await pages.PageCountAsync(candidateId, cancellationToken).ConfigureAwait(false);
        if (count <= 0) throw new InvalidOperationException("This source returned no pages for the candidate");
        var indexes = SampleIndexes(count, sampleCount);
        var settings = _options();
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(settings.OverallTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : settings.OverallTimeout);
        using var slots = new SemaphoreSlim(Math.Max(1, settings.Concurrency));
        var outcomes = await Task.WhenAll(indexes.Select(index => InspectAsync(candidateId, index, slots, settings.SampleTimeout, overall.Token))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var measured = outcomes.Where(item => item.Size is not null).OrderBy(item => item.Index).ToArray();
        var broken = outcomes.Count(item => item.Broken);
        var unobserved = outcomes.Count(item => item.Size is null && !item.Broken);
        var reasons = string.Join(", ", outcomes.Where(item => item.Reason.Length > 0).Select(item => item.Reason).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        int width = 0, height = 0; long average = 0; var score = 0.0;
        if (measured.Length > 0)
        {
            width = Median(measured.Select(item => item.Size!.Value.Width)); height = Median(measured.Select(item => item.Size!.Value.Height));
            average = (long)measured.Average(item => (double)item.Bytes);
            score = Score(width, height, average, broken);
        }
        var stamp = _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
        double? kept = null; var analyzedAt = stamp;
        lock (sourceWriteGate)
            database.Write(DatabaseFile.Origins, session =>
            {
                if (session.Scalar("SELECT 1 FROM chapter_candidates WHERE id=$id", ("$id", candidateId)) is null) throw new KeyNotFoundException("Chapter candidate not found");
                if (unobserved > 0)
                {
                    // CHAP-5: a failed sample keeps the old score and the old sample.
                    var current = session.Scalar("SELECT quality_score FROM chapter_candidates WHERE id=$id", ("$id", candidateId));
                    kept = current is null ? null : Convert.ToDouble(current, CultureInfo.InvariantCulture);
                    analyzedAt = session.Scalar("SELECT analyzed_at FROM quality_samples WHERE candidate_id=$id", ("$id", candidateId)) as string ?? "";
                    return 0;
                }
                session.Execute("UPDATE chapter_candidates SET quality_score=$score WHERE id=$id", ("$score", score), ("$id", candidateId));
                session.Execute("""
                    INSERT INTO quality_samples(candidate_id,sampled_pages,broken_pages,median_width,median_height,avg_bytes,phash_first,phash_last,analyzed_at)
                    VALUES($id,$sampled,$broken,$width,$height,$bytes,'','',$time)
                    ON CONFLICT(candidate_id) DO UPDATE SET sampled_pages=excluded.sampled_pages,broken_pages=excluded.broken_pages,median_width=excluded.median_width,
                      median_height=excluded.median_height,avg_bytes=excluded.avg_bytes,phash_first=excluded.phash_first,phash_last=excluded.phash_last,analyzed_at=excluded.analyzed_at
                    """, ("$id", candidateId), ("$sampled", indexes.Count), ("$broken", broken), ("$width", width), ("$height", height), ("$bytes", average), ("$time", stamp));
                return 0;
            });
        double? reported = unobserved > 0 ? kept : score;
        return new JsonObject
        {
            ["candidate_id"] = candidateId, ["entry_id"] = candidate.Integer("entry_id"),
            ["quality_score"] = reported.HasValue ? Math.Round(reported.Value, 2) : null,
            ["complete"] = unobserved == 0, ["outcome"] = unobserved > 0 ? "incomplete" : "complete", ["unobserved_pages"] = unobserved,
            ["reason"] = reasons, ["analyzed_at"] = analyzedAt, ["sampled_pages"] = indexes.Count, ["broken_pages"] = broken,
            ["median_width"] = width, ["median_height"] = height, ["avg_bytes"] = average, ["phash_first"] = "", ["phash_last"] = "",
        };
    }

    private async Task<Outcome> InspectAsync(long candidateId, int index, SemaphoreSlim slots, TimeSpan timeout, CancellationToken overall)
    {
        try { await slots.WaitAsync(overall).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(index, null, 0, false, "TimeoutError"); }
        try
        {
            using var each = CancellationTokenSource.CreateLinkedTokenSource(overall);
            each.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : timeout);
            byte[] bytes;
            try { bytes = await pages.ReadPageAsync(candidateId, index, each.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new(index, null, 0, false, "TimeoutError"); }
            catch (SourceWorkDeferredException) { return new(index, null, 0, false, "deferred"); }
            catch (ImageAnalysisLimitException) { return new(index, null, 0, true, ""); }
            catch (Exception error) { return new(index, null, 0, false, error.GetType().Name); }
            try { return new(index, decoder.Measure(bytes), bytes.Length, false, ""); }
            catch (Exception error) when (error is InvalidDataException or ImageAnalysisLimitException) { return new(index, null, 0, true, ""); }
        }
        finally { slots.Release(); }
    }

    private void Attempt(long candidateId, string outcome, int observed, int unobserved, string reason) =>
        database.Write(DatabaseFile.Catalog, session => session.Execute("INSERT OR REPLACE INTO quality_attempts VALUES($id,$time,$outcome,$observed,$unobserved,$reason)",
            ("$id", candidateId), ("$time", _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture)), ("$outcome", outcome),
            ("$observed", observed), ("$unobserved", unobserved), ("$reason", reason)));

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.Order().ToArray();
        // statistics.median then int(): the mean of the two middle values for an even count, truncated.
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (int)((sorted[sorted.Length / 2 - 1] + (long)sorted[sorted.Length / 2]) / 2.0);
    }

    private static int ReaderInt(JsonObject value, string name) => value[name] is JsonValue item && item.TryGetValue<int>(out var number) ? number : 0;

    private sealed record Outcome(int Index, (int Width, int Height)? Size, long Bytes, bool Broken, string Reason);
}
