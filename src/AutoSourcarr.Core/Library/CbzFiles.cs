using System.Globalization;
using System.IO.Compression;

namespace AutoSourcarr.Core.Library;

/// <summary>
/// File work of the CBZ library: atomic writes (a hidden ".name.partial" temp file next to the target, flushed to
/// disk, then renamed over it, so a reader never sees half a file and a crash leaves at most a temp file that the
/// next run replaces), resumable page staging (each fetched page is kept under the staging folder until its chapter
/// is packed, so a restart only fetches what is missing) and the archive itself: pages "001.jpg"... at the archive
/// root in reading order plus ComicInfo.xml, stored without recompression.
/// </summary>
public static class CbzFiles
{
    public const string ComicInfoName = "ComicInfo.xml";
    public const string CoverName = "cover.jpg";
    public const string DetailsName = "details.json";
    private const string PartialSuffix = ".partial";

    /// <summary>Writes bytes atomically (temp file in the same folder, fsync, rename).</summary>
    public static void WriteAtomic(string path, ReadOnlySpan<byte> content)
    {
        var temp = TempPath(path);
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>The image extension of the content (by signature, then by content type); "jpg" when unknown.</summary>
    public static string Extension(ReadOnlySpan<byte> content, string contentType = "")
    {
        if (content.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "jpg";
        if (content.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "png";
        if (content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8)) return "gif";
        if (content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content.Slice(8, 4).SequenceEqual("WEBP"u8)) return "webp";
        if (content.Length >= 12 && content.Slice(4, 4).SequenceEqual("ftyp"u8) && (content.Slice(8, 4).SequenceEqual("avif"u8) || content.Slice(8, 4).SequenceEqual("avis"u8))) return "avif";
        if (content.StartsWith("BM"u8)) return "bmp";
        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return type switch
        {
            "image/png" => "png", "image/gif" => "gif", "image/webp" => "webp", "image/avif" => "avif", "image/bmp" => "bmp",
            _ => "jpg",
        };
    }

    /// <summary>Whether the bytes look like an image a reader can show (signature check).</summary>
    public static bool IsImage(ReadOnlySpan<byte> content) => content.Length >= 4 && (content.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })
        || content.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) || content.StartsWith("GIF8"u8) || content.StartsWith("BM"u8)
        || content.Length >= 12 && (content[..4].SequenceEqual("RIFF"u8) && content.Slice(8, 4).SequenceEqual("WEBP"u8) || content.Slice(4, 4).SequenceEqual("ftyp"u8)));

    /// <summary>The staged page file of one page index, if already fetched.</summary>
    public static string? StagedPage(string stagingFolder, int pageIndex)
    {
        if (!Directory.Exists(stagingFolder)) return null;
        var stem = PageStem(pageIndex);
        return Directory.EnumerateFiles(stagingFolder, stem + ".*")
            .FirstOrDefault(path => !path.EndsWith(PartialSuffix, StringComparison.Ordinal) && new FileInfo(path).Length > 0);
    }

    /// <summary>Keeps one fetched page in the staging folder (atomic, so a crash never leaves a truncated page that counts as fetched).</summary>
    public static string StagePage(string stagingFolder, int pageIndex, LibraryImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Directory.CreateDirectory(stagingFolder);
        var path = Path.Combine(stagingFolder, PageStem(pageIndex) + "." + Extension(image.Content, image.ContentType));
        WriteAtomic(path, image.Content);
        return path;
    }

    /// <summary>
    /// Packs the staged pages (in index order) and ComicInfo.xml into <paramref name="target"/> atomically.
    /// Pages are named with at least three digits so natural and plain sorting agree.
    /// </summary>
    public static long Pack(string target, IReadOnlyList<string> pages, ReadOnlySpan<byte> comicInfo)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var width = Math.Max(3, pages.Count.ToString(CultureInfo.InvariantCulture).Length);
        var temp = TempPath(target);
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.None))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    for (var index = 0; index < pages.Count; index++)
                    {
                        var name = (index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') + Path.GetExtension(pages[index]).ToLowerInvariant();
                        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        using var output = entry.Open();
                        using var input = File.OpenRead(pages[index]);
                        input.CopyTo(output);
                    }
                    var info = archive.CreateEntry(ComicInfoName, CompressionLevel.Optimal);
                    info.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var infoStream = info.Open();
                    infoStream.Write(comicInfo);
                }
                stream.Flush(flushToDisk: true);
            }
            var size = new FileInfo(temp).Length;
            File.Move(temp, target, overwrite: true);
            return size;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Removes temp files a crash left in a series folder.</summary>
    public static int RemovePartials(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(folder, ".*" + PartialSuffix))
            if (TryDelete(path)) removed++;
        return removed;
    }

    public static bool TryDelete(string path)
    {
        try { if (!File.Exists(path)) return false; File.Delete(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { /* Staging is retried on the next download. */ }
        catch (UnauthorizedAccessException) { /* Staging is retried on the next download. */ }
    }

    private static string PageStem(int pageIndex) => pageIndex.ToString("D5", CultureInfo.InvariantCulture);

    // Hidden (leading dot) and not an archive extension: Mihon, Kavita and Komga all skip it while it is written.
    private static string TempPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? ".", "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + PartialSuffix);
}
