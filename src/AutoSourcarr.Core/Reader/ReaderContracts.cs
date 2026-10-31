using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Reader;

/// <summary>Concrete local Sources/Entry reads. No feed preparation or refresh capability is implied.</summary>
public interface IReaderCatalogueRead
{
    Task<JsonObject> SourcesAsync(CancellationToken cancellationToken);
    Task<JsonObject> EntryAsync(long entryId, CancellationToken cancellationToken);
}

/// <summary>Full catalogue operations require real feed and refresh owners; no successful fallback is provided.</summary>
public interface IReaderCatalogue : IReaderCatalogueRead
{
    Task<JsonObject> BrowseAsync(ReaderBrowseRequest request, CancellationToken cancellationToken);
    Task<JsonObject> RefreshAsync(long entryId, bool fetchDetails, bool fetchChapters, CancellationToken cancellationToken);
}

public sealed record ReaderBrowseRequest(string Mode, int Page = 1, string Query = "", IReadOnlyList<string>? SourceIds = null,
    int MinimumMatches = 1, int? ExactMatchCount = null, string Sort = "relevance");

public sealed class ReaderBrowsePendingException() : Exception("AutoSourcarr catalogue is busy or preparing this page. Retry shortly; requests already in progress are reused.");
