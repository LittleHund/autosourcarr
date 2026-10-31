using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;

namespace AutoSourcarr.Core.Sources;

/// <summary>One explicit logical operation. Nested requests, validation and physical local work must use this scope and its token.</summary>
public sealed class SourceTransportSession : IAsyncDisposable
{
    private readonly SuwayomiClient _client;
    private readonly SourceTransportManager _manager;
    private readonly SourceTransportEpoch? _stamp;
    private readonly IDisposable _terminalLease, _planeLease;
    private readonly OperationOwner _calls = new();
    private readonly object _gate = new();
    private readonly HashSet<OwnedUpstreamResponse> _unpublished = [];
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private CancellationTokenSource? _boundLifetime;
    private CancellationToken _boundToken;
    private IDisposable? _engineLease;
    private SourceTransportEpoch? _engine;
    private bool _closed;
    private Task? _dispose;

    // Instance-only deterministic observation boundary; unset in production and invoked outside all ownership locks.
    internal Func<Task>? ResponseCompletionBoundary { get; set; }

    internal SourceTransportSession(SuwayomiClient client, SourceTransportManager manager, SourceTransportEpoch plane,
        SourceTransportEpoch? engine, SourceTransportEpoch? stamp, IDisposable terminalLease, IDisposable planeLease, CancellationToken lifetime)
    {
        _client = client; _manager = manager; Plane = plane; _engine = engine; _stamp = stamp;
        _terminalLease = terminalLease; _planeLease = planeLease; RootCancellationToken = lifetime;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime, client.TerminalCancellationToken, plane.CancellationToken);
        _token = _lifetime.Token;
    }
    public CancellationToken CancellationToken => Engine is not null && _boundLifetime is not null ? _boundToken : _token;
    internal CancellationToken RootCancellationToken { get; }
    internal CancellationToken ScopeCancellationToken => _calls.CancellationToken;
    internal SourceTransportEpoch Plane { get; }
    internal SourceTransportEpoch? Engine => Volatile.Read(ref _engine);
    internal SourceTransportEpoch EngineForRequest() => Engine ?? _manager.BindEngine(this, _stamp);

    public void ThrowIfRetired()
    {
        if (Volatile.Read(ref _closed)) throw new OperationCanceledException(_token);
        CancellationToken.ThrowIfCancellationRequested(); Plane.ThrowIfRetired(); Engine?.ThrowIfRetired();
    }
    internal IDisposable EnterCall() { lock (_gate) { ThrowIfRetired(); return _calls.Enter(); } }
    internal SourceTransportEpoch BindInsideManager(SourceTransportEpoch epoch)
    {
        lock (_gate)
        {
            ThrowIfRetired();
            if (_engine is not null) return _engine;
            _engineLease = epoch.Enter();
            _boundLifetime = CancellationTokenSource.CreateLinkedTokenSource(_token, epoch.CancellationToken);
            _boundToken = _boundLifetime.Token; Volatile.Write(ref _engine, epoch); return epoch;
        }
    }
    internal void Register(OwnedUpstreamResponse response)
    {
        bool close;
        lock (_gate) { close = _closed; _unpublished.Add(response); }
        if (close) response.Dispose();
    }
    internal void Released(OwnedUpstreamResponse response) { lock (_gate) _unpublished.Remove(response); }
    internal void PublishInsideManager(OwnedUpstreamResponse response)
    {
        lock (_gate)
        {
            ThrowIfRetired();
            if (!_unpublished.Remove(response)) throw new InvalidOperationException("This session does not own the unpublished response.");
        }
    }
    internal void PublishResponseToCaller(OwnedUpstreamResponse response) => _manager.Publish(this, response);

    public Task<JsonObject> GraphQlAsync(string query, JsonObject? variables = null, CancellationToken cancellationToken = default)
        => Call(() => _client.GraphQlCoreAsync(this, query, variables, cancellationToken));
    public Task<JsonNode?> RestJsonAsync(string path, CancellationToken cancellationToken = default)
        => Call(() => _client.RestJsonCoreAsync(this, path, cancellationToken));
    public Task<List<JsonObject>> SourcesAsync(CancellationToken cancellationToken = default) => Call(() => _client.SourcesCoreAsync(this, cancellationToken));
    public Task<JsonObject> ExtensionsAsync(bool refresh = false, CancellationToken cancellationToken = default) => Call(() => _client.ExtensionsCoreAsync(this, refresh, cancellationToken));
    public Task<JsonObject?> UpdateExtensionAsync(string packageName, string action, CancellationToken cancellationToken = default)
        => Call(() => _client.UpdateExtensionCoreAsync(this, packageName, action, cancellationToken));
    public Task<JsonObject> UpdateAllExtensionsAsync(CancellationToken cancellationToken = default) => Call(() => _client.UpdateAllExtensionsCoreAsync(this, cancellationToken));
    public Task InstallExtensionFileAsync(string filename, byte[] data, bool apk = true, CancellationToken cancellationToken = default)
        => Call(() => _client.InstallExtensionFileCoreAsync(this, filename, data, apk, cancellationToken));
    public Task<List<JsonObject>> LibraryMangasAsync(int first = 500, CancellationToken cancellationToken = default) => Call(() => _client.LibraryMangasCoreAsync(this, first, cancellationToken));
    public Task<JsonObject> FetchSourceMangaAsync(string sourceId, string mode, int page = 1, string queryText = "", CancellationToken cancellationToken = default)
        => Call(() => _client.FetchSourceMangaCoreAsync(this, sourceId, mode, page, queryText, cancellationToken));
    public Task<JsonObject> FetchMangaAndChaptersAsync(long mangaId, bool fetchManga = true, bool fetchChapters = true, CancellationToken cancellationToken = default)
        => Call(() => _client.FetchMangaAndChaptersCoreAsync(this, mangaId, fetchManga, fetchChapters, cancellationToken));
    public Task<List<string>> FetchChapterPagesAsync(long chapterId, CancellationToken cancellationToken = default) => Call(() => _client.FetchChapterPagesCoreAsync(this, chapterId, cancellationToken));
    public Task<JsonObject> RetryVerificationAsync(JsonObject operation, JsonObject context, CancellationToken cancellationToken = default)
        => Call(() => _client.RetryVerificationCoreAsync(this, operation, context, cancellationToken));
    public Task CheckChapterAttentionAsync(long chapterId, CancellationToken cancellationToken = default) => Call(() => _client.CheckChapterAttentionCoreAsync(this, chapterId, cancellationToken));
    public Task<int> ChapterIndexForIdAsync(long mangaId, long chapterId, CancellationToken cancellationToken = default)
        => Call(() => _client.ChapterIndexForIdCoreAsync(this, mangaId, chapterId, cancellationToken));
    public Task<JsonObject> HealthAsync(CancellationToken cancellationToken = default) => Call(() => _client.HealthCoreAsync(this, cancellationToken));
    public Task<(byte[] Bytes, string ContentType)> FetchBinaryAsync(string url, TimeSpan? timeout = null, int maximumBytes = 32 * 1024 * 1024,
        string referer = "", CancellationToken cancellationToken = default)
        => Call(() => _client.FetchBinaryCoreAsync(this, _client.AssetUri(url), timeout, maximumBytes, referer, false, cancellationToken));
    public Task<(byte[] Bytes, string ContentType)> FetchSourceBinaryAsync(string url, CancellationToken cancellationToken = default)
        => Call(() => _client.RunSourceWorkAsync(this, token => _client.FetchBinaryCoreAsync(this, _client.AssetUri(url), null, 32 * 1024 * 1024, "", false, token), null, null, cancellationToken));
    public Task<OwnedUpstreamResponse> OpenBinaryStreamAsync(string url, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => ResponseCall(() => _client.OpenBinaryCoreAsync(this, url, timeout, cancellationToken));
    public Task<OwnedUpstreamResponse> OpenPageImageStreamAsync(long mangaId, int chapterIndex, int pageIndex, CancellationToken cancellationToken = default)
        => OpenBinaryStreamAsync(FormattableString.Invariant($"/api/v1/manga/{mangaId}/chapter/{chapterIndex}/page/{pageIndex}"), cancellationToken: cancellationToken);
    internal Task<JsonNode?> ReadAboutAsync(AuthenticationHeaderValue? authorization, SourceAboutTransportRegistration? transport, CancellationToken token)
        => Call(() => _client.ReadAboutCoreAsync(this, authorization, transport, token));

    private Task<T> Call<T>(Func<Task<T>> work)
    {
        var lease = EnterCall(); var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = CompleteCallAsync(work, lease, completion); return completion.Task;
    }
    private Task Call(Func<Task> work) => Call(async () => { await work().ConfigureAwait(false); return true; });
    private Task<OwnedUpstreamResponse> ResponseCall(Func<Task<OwnedUpstreamResponse>> work)
    {
        var lease = EnterCall(); var completion = new TaskCompletionSource<OwnedUpstreamResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = CompleteResponseCallAsync(work, lease, completion); return completion.Task;
    }
    private async Task CompleteResponseCallAsync(Func<Task<OwnedUpstreamResponse>> work, IDisposable lease, TaskCompletionSource<OwnedUpstreamResponse> completion)
    {
        OwnedUpstreamResponse? response = null; Exception? failure = null;
        try
        {
            response = await work().ConfigureAwait(false);
            PublishResponseToCaller(response);
            if (ResponseCompletionBoundary is { } boundary) await boundary().ConfigureAwait(false);
            ThrowIfRetired();
        }
        catch (Exception primary)
        {
            failure = primary;
            if (response is not null)
            {
                // Publication removed this resource from the scope's unpublished set, but its task
                // has not yet transferred a handle. Scope exit must join this discarded resource.
                failure = await response.DiscardAsync(primary).ConfigureAwait(false);
            }
            else try { ThrowIfRetired(); } catch (Exception interrupted) { failure = interrupted; }
        }
        try
        {
            if (failure is OperationCanceledException cancelled) completion.TrySetCanceled(cancelled.CancellationToken);
            else if (failure is not null) { completion.TrySetException(failure); _ = completion.Task.Exception; }
            else completion.TrySetResult(response!);
        }
        finally { lease.Dispose(); }
    }
    private async Task CompleteCallAsync<T>(Func<Task<T>> work, IDisposable lease, TaskCompletionSource<T> completion)
    {
        T? result = default; Exception? failure = null;
        try { result = await work().ConfigureAwait(false); ThrowIfRetired(); }
        catch (Exception error)
        {
            failure = error;
            try { ThrowIfRetired(); } catch (Exception interrupted) { failure = interrupted; }
        }
        // Set the returned task's complete outcome before releasing the owner. Scope drain therefore joins
        // the actual child result/error, including a child the callback forgot to await.
        try
        {
            if (failure is OperationCanceledException cancelled) completion.TrySetCanceled(cancelled.CancellationToken);
            else if (failure is not null) { completion.TrySetException(failure); _ = completion.Task.Exception; }
            else completion.TrySetResult(result!);
        }
        finally { lease.Dispose(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { _closed = true; return new(_dispose ??= CloseCoreAsync()); }
    }
    private async Task CloseCoreAsync()
    {
        await Task.Yield(); var errors = new List<Exception>(); var drain = _calls.StopAsync();
        OwnedUpstreamResponse[] pending; lock (_gate) pending = _unpublished.ToArray();
        foreach (var response in pending) response.Dispose();
        try { await drain.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        foreach (var response in pending) try { await response.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { await _calls.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        if (errors.Count > 0) foreach (var error in errors) (Engine ?? Plane).Retain(error);
        _boundLifetime?.Dispose(); _lifetime.Dispose(); _engineLease?.Dispose(); _planeLease.Dispose(); _terminalLease.Dispose();
        if (errors.Count > 0) throw new AggregateException("Source logical scope cleanup failed", errors);
    }
}
