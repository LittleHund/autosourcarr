namespace AutoSourcarr.Core.Library;

/// <summary>One downloaded image (a page or a cover) with the content type the source reported.</summary>
public sealed record LibraryImage(byte[] Content, string ContentType);

/// <summary>The resolved pages of one catalogue chapter: the serving candidate and how many pages it has.</summary>
public sealed record LibraryChapterPages(long CandidateId, int Count, string SourceId);

/// <summary>
/// Pages of one catalogue chapter (virtual chapter id) for CBZ downloads (CHAP-7). Implemented by
/// <see cref="ReaderLibraryPageSource"/> over the reader page service (the same serving choice and page cache the
/// reader uses); tests use an in-memory source.
/// </summary>
public interface IChapterPageSource
{
    /// <summary>Resolves the serving candidate and its page count (the reader's fallback order applies).</summary>
    Task<LibraryChapterPages> ResolveAsync(long chapterId, CancellationToken cancellationToken);

    /// <summary>Fetches one page image of the resolved candidate.</summary>
    Task<LibraryImage> PageAsync(LibraryChapterPages chapter, int pageIndex, CancellationToken cancellationToken);

    /// <summary>The work's cover, or null when none is available (the first page is then used).</summary>
    Task<LibraryImage?> CoverAsync(long entryId, CancellationToken cancellationToken);
}

/// <summary>Series metadata written to ComicInfo.xml and details.json.</summary>
public sealed record LibrarySeriesInfo(long EntryId, string Title, string Author, string Artist, string Summary,
    IReadOnlyList<string> Genres, int Status, string Web = "");

/// <summary>One catalogue chapter as the library sees it (number and volume are -1 when unknown).</summary>
public sealed record LibraryChapterInfo(long ChapterId, long EntryId, double Number, double Volume, string Title,
    string SourceId, string SourceName, string Scanlator = "", long UploadDate = 0, string Language = "");

/// <summary>Source chapter coverage of a work: whether every attached record was checked and how many gaps it has.</summary>
public sealed record LibraryCoverage(bool Checked, long SourceGaps, string CheckedAt);

/// <summary>
/// What the library needs from the catalogue: series metadata, the chapter list (one serving source per chapter,
/// CHAP-4) and a refresh of a work's source chapters for monitoring. Implemented by <see cref="CatalogueLibrary"/>.
/// </summary>
public interface ILibraryCatalogue
{
    long CanonicalEntryId(long entryId);
    LibrarySeriesInfo? Series(long entryId);
    IReadOnlyList<LibraryChapterInfo> Chapters(long entryId);
    LibraryCoverage Coverage(long entryId);
    /// <summary>Fetches the work's source chapter lists again (monitoring picks up new chapters through this).</summary>
    Task RefreshAsync(long entryId, CancellationToken cancellationToken);
}

/// <summary>Where the library lives and how downloads are paced.</summary>
public sealed record LibraryOptions
{
    /// <summary>The library folder (LIBRARY_DIR, default &lt;DATA_DIR&gt;/library): &lt;Library&gt;/&lt;Series&gt;/&lt;Chapter&gt;.cbz.</summary>
    public required string Root { get; init; }
    /// <summary>Resumable page staging, outside the library so readers never scan half-downloaded chapters.</summary>
    public required string StagingRoot { get; init; }
    /// <summary>Pause between two page requests to the same source.</summary>
    public TimeSpan PageDelay { get; init; } = TimeSpan.FromMilliseconds(400);
    /// <summary>Pause between two chapters of the same source.</summary>
    public TimeSpan ChapterDelay { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Attempts per page before the chapter is marked failed.</summary>
    public int PageAttempts { get; init; } = 3;
    /// <summary>Back-off base between page attempts (doubles each time).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>How often monitored works are checked for due monitoring.</summary>
    public TimeSpan MonitorInterval { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>Interval after a complete observation before the next automatic check.</summary>
    public int MonitorRecheckSeconds { get; init; } = 6 * 3600;
    /// <summary>Largest accepted page or cover.</summary>
    public int MaximumImageBytes { get; init; } = 64 * 1024 * 1024;

    public static LibraryOptions For(global::AutoSourcarr.Core.Settings.AutoSourcarrSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = string.IsNullOrWhiteSpace(settings.LibraryDir) ? Path.Combine(settings.DataDir, "library") : settings.LibraryDir.Trim();
        return new LibraryOptions { Root = Path.GetFullPath(root), StagingRoot = Path.GetFullPath(Path.Combine(settings.DataDir, "library-staging")) };
    }
}

/// <summary>A library change that conflicts with the current state (stale revision, not in the library, ...): HTTP 409.</summary>
public sealed class LibraryConflictException(string message) : Exception(message);
