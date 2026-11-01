using System.Text.Json.Nodes;
using AutoSourcarr.Core.Imaging;
using AutoSourcarr.Core.Reader;

namespace AutoSourcarr.Core.Library;

/// <summary>
/// <see cref="IChapterPageSource"/> over the reader page service: the same serving candidate, fallback order, page
/// cache and blocked-source back-off the reader uses (ReaderPageService.ResolvePagesAsync / OpenImageAsync). Covers come
/// from the catalogue cover cache (key "entry-&lt;id&gt;") when it holds one; otherwise the downloader uses the first page.
/// </summary>
public sealed class ReaderLibraryPageSource(ReaderPageService pages, CoverCache? covers = null, int maximumBytes = 64 * 1024 * 1024) : IChapterPageSource
{
    public async Task<LibraryChapterPages> ResolveAsync(long chapterId, CancellationToken cancellationToken)
    {
        var result = await pages.ResolvePagesAsync(chapterId, cancellationToken).ConfigureAwait(false);
        var candidate = result["candidate"] as JsonObject ?? throw new InvalidDataException("Chapter has no serving candidate");
        var count = result["pages"] is JsonArray list ? list.Count : 0;
        return new LibraryChapterPages(ReaderValue.Integer(candidate, "id"), count, ReaderValue.Text(candidate, "source_id"));
    }

    public async Task<LibraryImage> PageAsync(LibraryChapterPages chapter, int pageIndex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        await using var image = await pages.OpenImageAsync(chapter.CandidateId, pageIndex, cancellationToken).ConfigureAwait(false);
        if (image.ContentLength > maximumBytes) throw new InvalidDataException("Page is larger than the page limit");
        using var buffer = new LimitedBuffer(maximumBytes);
        await image.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new LibraryImage(buffer.ToArray(), image.ContentType);
    }

    public async Task<LibraryImage?> CoverAsync(long entryId, CancellationToken cancellationToken)
    {
        if (covers is null) return null;
        var cover = await covers.ReadAsync(FormattableString.Invariant($"entry-{entryId}"), cancellationToken).ConfigureAwait(false);
        return cover is null || cover.Content.Length == 0 ? null : new LibraryImage(cover.Content.ToArray(), cover.ContentType);
    }

    /// <summary>A memory stream that refuses to grow past the page limit (a source cannot fill memory with one page).</summary>
    private sealed class LimitedBuffer(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Check(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }

        private void Check(int count)
        {
            if (Length + count > limit) throw new InvalidDataException("Page is larger than the page limit");
        }
    }
}
