using System.Globalization;
using System.Runtime.ExceptionServices;
using AutoSourcarr.Core.Imaging;
using AutoSourcarr.Core.Matching.Mixed;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Sources;
using ImageMagick;

namespace AutoSourcarr.Core.Catalogue.Serving;

/// <summary>Reads the pages of one chapter candidate for sampling (quality and colour), without choosing it for serving.</summary>
public interface ICandidatePageReader
{
    /// <summary>The candidate's page count: its saved page list, or the list fetched from its source and saved.</summary>
    Task<int> PageCountAsync(long candidateId, CancellationToken cancellationToken);

    /// <summary>One page image (at most <see cref="RasterImages.MaximumBytes"/>).</summary>
    Task<byte[]> ReadPageAsync(long candidateId, int pageIndex, CancellationToken cancellationToken);
}

/// <summary>Decodes sampled page images. Broken images throw <see cref="InvalidDataException"/> or <see cref="ImageAnalysisLimitException"/>.</summary>
public interface IPageRasterDecoder
{
    (int Width, int Height) Measure(byte[] image);

    /// <summary>RGB pixels reduced to at most <paramref name="maximumSide"/> on the longer side, plus the original size.</summary>
    PageRaster Decode(byte[] image, int maximumSide);
}

/// <summary>The app's decoder: the bounded raster policy of <see cref="RasterImages"/> (formats, size and pixel limits) first, then pixels.</summary>
public sealed class MagickPageRasterDecoder : IPageRasterDecoder
{
    public (int Width, int Height) Measure(byte[] image)
    {
        using var raster = RasterImages.Open(image);
        return (raster.Width, raster.Height);
    }

    public PageRaster Decode(byte[] image, int maximumSide)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var metric = Diagnostics.RuntimeMetrics.Shared.Phase("quality_decode", image.LongLength);
        int width, height;
        // Validates the format and limits and activates the restricted native policy before pixels are read.
        using (var raster = RasterImages.Open(image)) { width = raster.Width; height = raster.Height; }
        using var decoded = Diagnostics.RuntimeMetrics.Shared.DecodedPixels((long)width * height);
        try
        {
            using var pixels = new MagickImage(image, new MagickReadSettings { FrameIndex = 0, FrameCount = 1, SyncImageWithExifProfile = false });
            var scale = Math.Min(1.0, Math.Max(1, maximumSide) / (double)Math.Max(width, height));
            if (scale < 1) pixels.Resize((uint)Math.Max(1, (int)Math.Round(width * scale)), (uint)Math.Max(1, (int)Math.Round(height * scale)));
            using var collection = pixels.GetPixels();
            var rgb = collection.ToByteArray(PixelMapping.RGB) ?? throw new InvalidDataException("Page pixels could not be read");
            return new(width, height, rgb, checked((int)pixels.Width), checked((int)pixels.Height));
        }
        catch (MagickException error) { throw new InvalidDataException("Sample page could not be decoded", error); }
    }
}

/// <summary>
/// <see cref="ICandidatePageReader"/> over the reader: a missing page list is fetched from the candidate's source and saved
/// (as 0.4.5 quality.py did, without marking the candidate as served); page bytes come through
/// <see cref="ReaderPageService.OpenImageAsync"/>, which re-checks that the candidate still belongs to its work.
/// </summary>
public sealed class ReaderCandidatePages(SqliteDatabase database, object sourceWriteGate, IReaderPageSource source, ReaderPageService pages,
    TimeProvider? clock = null) : ICandidatePageReader
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<int> PageCountAsync(long candidateId, CancellationToken cancellationToken)
    {
        var (row, cached) = database.Read(DatabaseFile.Origins, session => (
            session.Query("SELECT source_manga_id,source_chapter_id,source_chapter_index FROM chapter_candidates WHERE id=$id", ("$id", candidateId)).FirstOrDefault(),
            Convert.ToInt64(session.Scalar("SELECT COUNT(*) FROM candidate_pages WHERE candidate_id=$id", ("$id", candidateId)), CultureInfo.InvariantCulture)));
        if (row is null) throw new KeyNotFoundException("Chapter candidate not found");
        if (cached > 0) return checked((int)cached);
        var chapterId = row.Integer("source_chapter_id");
        var urls = await source.FetchPagesAsync(chapterId, cancellationToken).ConfigureAwait(false);
        var index = checked((int)row.Integer("source_chapter_index", -1));
        if (index < 0)
        {
            try { index = await source.ChapterIndexAsync(row.Integer("source_manga_id"), chapterId, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (SourceWorkDeferredException) { index = -1; }
            catch (Exception) { index = -2; }
        }
        var stamp = _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
        lock (sourceWriteGate)
            database.Write(DatabaseFile.Origins, session =>
            {
                // The candidate may have been re-pointed while the source answered: then the fetched list is not its list.
                if (session.Scalar("SELECT 1 FROM chapter_candidates WHERE id=$id AND source_chapter_id=$chapter", ("$id", candidateId), ("$chapter", chapterId)) is null)
                    throw new KeyNotFoundException("Candidate changed while its pages were read");
                session.Execute("DELETE FROM candidate_pages WHERE candidate_id=$id", ("$id", candidateId));
                for (var page = 0; page < urls.Count; page++)
                    session.Execute("INSERT INTO candidate_pages(candidate_id,page_index,private_url,updated_at) VALUES($id,$index,$url,$time)",
                        ("$id", candidateId), ("$index", page), ("$url", urls[page]), ("$time", stamp));
                session.Execute("UPDATE chapter_candidates SET page_count=$count,source_chapter_index=$index,last_seen=$time WHERE id=$id",
                    ("$count", urls.Count), ("$index", index), ("$time", stamp), ("$id", candidateId));
                return 0;
            });
        return urls.Count;
    }

    public async Task<byte[]> ReadPageAsync(long candidateId, int pageIndex, CancellationToken cancellationToken)
    {
        var image = await pages.OpenImageAsync(candidateId, pageIndex, cancellationToken).ConfigureAwait(false);
        await using (image.ConfigureAwait(false))
        {
            if (image.ContentLength > RasterImages.MaximumBytes) throw new ImageAnalysisLimitException("Page image exceeds the 32 MiB analysis limit");
            using var buffer = new LimitedBuffer(RasterImages.MaximumBytes);
            await image.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    private sealed class LimitedBuffer(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { Check(count); return base.WriteAsync(buffer, offset, count, cancellationToken); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Check(buffer.Length); return base.WriteAsync(buffer, cancellationToken); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        private void Check(int count)
        {
            if (Length + count > limit) throw new ImageAnalysisLimitException("Page image exceeds the 32 MiB analysis limit");
        }
    }
}

/// <summary>
/// <see cref="IPageImageSource"/> for edition (colour) checks: the newest chapter with pages of one source record →
/// its page list → <see cref="PageColorAnalysis.SampleIndexes"/> → fetch and decode each page, reduced to at most 256 px.
/// Pages that cannot be decoded are skipped; when none could be read the last error is raised.
/// </summary>
public sealed class ReaderPageImages(SqliteDatabase database, ICandidatePageReader reader, IPageRasterDecoder decoder) : IPageImageSource
{
    public const int MaximumSide = 256;

    public async Task<IReadOnlyList<PageRaster>> SamplePagesAsync(string sourceId, long mangaId, int pages, CancellationToken cancellationToken)
    {
        var chapters = database.Read(DatabaseFile.Origins, session => session.Query("""
            SELECT id,page_count,chapter_number FROM chapter_candidates WHERE source_id=$source AND source_manga_id=$manga AND active=1
            ORDER BY CASE WHEN chapter_number>=0 THEN 0 ELSE 1 END,chapter_number DESC,source_order DESC LIMIT 12
            """, ("$source", sourceId), ("$manga", mangaId)));
        if (chapters.Count == 0) return [];
        // A chapter with known pages first, newest first (color_profile.py).
        var chapter = chapters.OrderBy(row => row.Integer("page_count") <= 0).ThenByDescending(row => row.Number("chapter_number", -1)).First();
        var candidateId = chapter.Integer("id");
        var count = await reader.PageCountAsync(candidateId, cancellationToken).ConfigureAwait(false);
        var result = new List<PageRaster>();
        Exception? last = null;
        foreach (var index in PageColorAnalysis.SampleIndexes(count, pages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { result.Add(decoder.Decode(await reader.ReadPageAsync(candidateId, index, cancellationToken).ConfigureAwait(false), MaximumSide)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { last = error; }
        }
        if (result.Count == 0 && last is not null) ExceptionDispatchInfo.Throw(last);
        return result;
    }
}
