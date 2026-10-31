using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair;

namespace AutoSourcarr.Core.Sources.Repositories;

public sealed class SourceRepositoryPreviewBusyException() : ArgumentException("Two repository previews are still running. Retry shortly");

/// <summary>
/// Per-instance, read-only repository previews. The injected public fetcher retains DNS pinning,
/// TLS verification and physical transport ownership; the caller owns and stops that shared fetcher.
/// This service owns at most two full previews, including late completion after a disconnected caller.
/// </summary>
public sealed class SourceRepositoryPreview : IAsyncDisposable
{
    public const string DeclaredStatus = "Declared by this repository; installed-code compatibility is checked separately.";
    public const string UnknownStatus = "No unambiguous source-code declaration found. Original project input may be required.";
    private readonly IPublicSourceFetcher _fetcher;
    private readonly OperationOwner _owner = new();
    private readonly SemaphoreSlim _slots = new(2, 2);
    private int _disposed;

    public SourceRepositoryPreview(IPublicSourceFetcher fetcher) => _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));

    public async Task<JsonObject> PreviewAsync(string url, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(10);
        if (wait <= TimeSpan.Zero || wait > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = SourceRepositoryStore.NormalizeUrl(url); var lease = _owner.Enter();
        if (!_slots.Wait(0)) { lease.Dispose(); throw new SourceRepositoryPreviewBusyException(); }
        var task = OwnedAsync(normalized, lease);
        // Consume a late failure without relinquishing the owner lease or admission before physical completion.
        _ = task.ContinueWith(static done => { _ = done.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await task.WaitAsync(wait, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { throw new TimeoutException("Repository preview timed out. No configuration was changed"); }
    }

    public Task StopAsync() => _owner.StopAsync();
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                try { await _owner.DisposeAsync().ConfigureAwait(false); }
                finally { _slots.Dispose(); }
        }
    }

    private async Task<JsonObject> OwnedAsync(string normalized, IDisposable lease)
    {
        using (lease)
        {
            try { return await PreviewCoreAsync(normalized, _owner.CancellationToken).ConfigureAwait(false); }
            finally { _slots.Release(); }
        }
    }

    private async Task<JsonObject> PreviewCoreAsync(string normalized, CancellationToken cancellationToken)
    {
        var uri = new Uri(normalized); PublicSourceFetcher.ValidateUrl(uri);
        var response = await _fetcher.FetchAsync(new(uri, RepositoryManifest.MaximumBytes, TimeSpan.FromSeconds(8)), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); PublicSourceFetcher.ValidateUrl(response.ResolvedUrl);
        var result = RepositoryManifest.Parse(response.Body); JsonObject? source = null;
        using (var declaration = new DeclarationFetcher(_fetcher, cancellationToken))
        {
            try { source = await new SourceDiscovery(declaration).DeclaredSourceAsync(normalized, declaration.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or TimeoutException or HttpRequestException or DecoderFallbackException or JsonException or ArgumentException) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
        result["url"] = normalized; result["resolved_url"] = response.ResolvedUrl.OriginalString;
        result["source_repository"] = source?["repository"]?.DeepClone();
        result["source_code_status"] = source is null ? UnknownStatus : DeclaredStatus;
        return result;
    }

    private sealed class DeclarationFetcher : IPublicSourceFetcher, IDisposable
    {
        private readonly IPublicSourceFetcher _fetcher;
        private readonly CancellationTokenSource _deadline;
        private readonly long _started = Stopwatch.GetTimestamp();
        public DeclarationFetcher(IPublicSourceFetcher fetcher, CancellationToken token)
        {
            _fetcher = fetcher; _deadline = CancellationTokenSource.CreateLinkedTokenSource(token); _deadline.CancelAfter(TimeSpan.FromSeconds(5));
        }
        public CancellationToken Token => _deadline.Token;
        public async Task<PublicSourceResponse> FetchAsync(PublicSourceRequest request, CancellationToken cancellationToken = default)
        {
            var remaining = TimeSpan.FromSeconds(5) - Stopwatch.GetElapsedTime(_started);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Repository source declaration reached its deadline");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
            PublicSourceFetcher.ValidateUrl(request.Url);
            var response = await _fetcher.FetchAsync(request with { Timeout = request.Timeout < remaining ? request.Timeout : remaining }, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return response;
        }
        public void Dispose() => _deadline.Dispose();
    }
}
