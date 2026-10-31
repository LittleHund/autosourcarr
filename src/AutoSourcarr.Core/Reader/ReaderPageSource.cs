using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Core.Reader;

public interface IReaderPageSource
{
    Task CheckAttentionAsync(long chapterId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> FetchPagesAsync(long chapterId, CancellationToken cancellationToken);
    Task<int> ChapterIndexAsync(long mangaId, long chapterId, CancellationToken cancellationToken);
    Task<ReaderImageStream> OpenPageAsync(long mangaId, int chapterIndex, int pageIndex, CancellationToken cancellationToken);
    Task<ReaderImageStream> OpenBinaryAsync(string privateUrl, CancellationToken cancellationToken);
}

/// <summary>Sources with a restartable upstream provide one actual operation spanning the reader's complete fallback.</summary>
public interface IReaderScopedPageSource : IReaderPageSource
{
    IReaderPageOperation OpenOperation(CancellationToken lifetime);
}
public interface IReaderPageOperation : IAsyncDisposable
{
    IReaderPageSource Source { get; }
    CancellationToken CancellationToken { get; }
    void ThrowIfRetired();
}

/// <summary>Owns the first close operation; repeated disposal joins the same physical close.</summary>
public sealed class ReaderImageStream(Func<CancellationToken, Task<Stream>> openBody, Func<ValueTask> close,
    string contentType = "application/octet-stream", long? contentLength = null, CancellationToken cancellationToken = default,
    Action? release = null, Func<CancellationToken, Task>? validatePublication = null) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly OperationOwner _transfers = new();
    private Task? _closed;
    private bool _copyStarted;
    public string ContentType { get; } = contentType;
    public long? ContentLength { get; } = contentLength;
    public CancellationToken CancellationToken { get; } = cancellationToken;
    internal Task<Stream> OpenBodyAsync(CancellationToken token) => openBody(token);

    /// <summary>Recheck membership before response headers, including HEAD; holds no source gate over streaming.</summary>
    public async Task ValidatePublicationAsync(CancellationToken token)
    {
        using var lease = _transfers.Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, CancellationToken, _transfers.CancellationToken);
        if (validatePublication is not null) await validatePublication(linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
    }

    public async Task CopyToAsync(Stream destination, CancellationToken token)
    {
        using var lease = _transfers.Enter();
        lock (_gate)
        {
            if (_copyStarted) throw new InvalidOperationException("Reader image body has already been consumed");
            _copyStarted = true;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, CancellationToken, _transfers.CancellationToken);
        if (validatePublication is not null) await validatePublication(linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        var body = await openBody(linked.Token).ConfigureAwait(false);
        await body.CopyToAsync(destination, 64 * 1024, linked.Token).ConfigureAwait(false);
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate) { _closed ??= CloseAsync(); return new(_closed); }
    }
    private async Task CloseAsync()
    {
        var errors = new List<Exception>();
        try
        {
            // Stop admission and cancel first, then always close the physical source.
            // Failed callbacks still own all active validation/body operations.
            _ = _transfers.StopAsync();
            try { await close().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            finally { try { await _transfers.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); } }
        }
        finally { release?.Invoke(); }
        if (errors.Count > 0) throw new AggregateException("Reader image cleanup failed", errors);
    }
}

public sealed class SuwayomiReaderPageSource(SuwayomiClient client) : IReaderScopedPageSource
{
    public IReaderPageOperation OpenOperation(CancellationToken lifetime) => new BoundSource(client.OpenAssetSession(lifetime));
    public Task CheckAttentionAsync(long chapterId, CancellationToken cancellationToken) => client.CheckChapterAttentionAsync(chapterId, cancellationToken);
    public async Task<IReadOnlyList<string>> FetchPagesAsync(long chapterId, CancellationToken cancellationToken) => await client.FetchChapterPagesAsync(chapterId, cancellationToken).ConfigureAwait(false);
    public Task<int> ChapterIndexAsync(long mangaId, long chapterId, CancellationToken cancellationToken) => client.ChapterIndexForIdAsync(mangaId, chapterId, cancellationToken);
    public async Task<ReaderImageStream> OpenPageAsync(long mangaId, int chapterIndex, int pageIndex, CancellationToken cancellationToken)
        => Wrap(await client.OpenPageImageStreamAsync(mangaId, chapterIndex, pageIndex, cancellationToken).ConfigureAwait(false));
    public async Task<ReaderImageStream> OpenBinaryAsync(string privateUrl, CancellationToken cancellationToken)
        => Wrap(await client.OpenBinaryStreamAsync(privateUrl, cancellationToken: cancellationToken).ConfigureAwait(false));
    private static ReaderImageStream Wrap(OwnedUpstreamResponse response)
    {
        var headers = response.ContentHeaders;
        var length = headers.ContentEncoding.Count == 0 || headers.ContentEncoding.All(value => value.Equals("identity", StringComparison.OrdinalIgnoreCase)) ? headers.ContentLength : null;
        return new(_ => response.OpenBodyAsync(), response.DisposeAsync,
            headers.ContentType?.ToString() ?? "application/octet-stream", length, response.CancellationToken);
    }
    private sealed class BoundSource(SourceTransportSession session) : IReaderPageOperation, IReaderPageSource
    {
        public IReaderPageSource Source => this;
        public CancellationToken CancellationToken => session.CancellationToken;
        public void ThrowIfRetired() => session.ThrowIfRetired();
        public ValueTask DisposeAsync() => session.DisposeAsync();
        public Task CheckAttentionAsync(long chapterId, CancellationToken cancellationToken) => session.CheckChapterAttentionAsync(chapterId, cancellationToken);
        public async Task<IReadOnlyList<string>> FetchPagesAsync(long chapterId, CancellationToken cancellationToken) => await session.FetchChapterPagesAsync(chapterId, cancellationToken).ConfigureAwait(false);
        public Task<int> ChapterIndexAsync(long mangaId, long chapterId, CancellationToken cancellationToken) => session.ChapterIndexForIdAsync(mangaId, chapterId, cancellationToken);
        public async Task<ReaderImageStream> OpenPageAsync(long mangaId, int chapterIndex, int pageIndex, CancellationToken cancellationToken)
            => Wrap(await session.OpenPageImageStreamAsync(mangaId, chapterIndex, pageIndex, cancellationToken).ConfigureAwait(false));
        public async Task<ReaderImageStream> OpenBinaryAsync(string privateUrl, CancellationToken cancellationToken)
            => Wrap(await session.OpenBinaryStreamAsync(privateUrl, cancellationToken: cancellationToken).ConfigureAwait(false));
    }
}
