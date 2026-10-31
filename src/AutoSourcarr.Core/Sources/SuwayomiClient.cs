using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Privacy;

namespace AutoSourcarr.Core.Sources;

public sealed partial class SuwayomiClient : IDisposable, IAsyncDisposable, ISourceVerificationProbe
{
    private readonly SourceTransportManager _transports;
    private readonly AutoSourcarrSettings _settings;
    private readonly SupportPrivacy _privacy;
    private readonly IManagedSourceRuntime? _runtime;
    private readonly SemaphoreSlim? _total;
    private readonly SemaphoreSlim? _background;
    private readonly AuthenticationHeaderValue? _basic;
    private readonly Uri _origin;
    private readonly OperationOwner _owner = new();
    private readonly object _closeGate = new();
    private Task? _dispose;

    /// <param name="runtime">The real runtime owner. Lazy startup/restart must defer this call, never wait for a drain of this same admitted session.</param>
    /// <param name="transportFactory">Fresh exclusively owned pools; production defaults disable ambient proxies and credentials. A single handler is terminal-only.</param>
    public SuwayomiClient(AutoSourcarrSettings settings, string? baseUrl = null, int workLimit = 0,
        IManagedSourceRuntime? runtime = null, string username = "", string password = "",
        SupportPrivacy? privacy = null, HttpMessageHandler? handler = null, ISourceTransportFactory? transportFactory = null)
    {
        _settings = settings;
        _privacy = privacy ?? new();
        _runtime = runtime;
        BaseUrl = (baseUrl ?? settings.EffectiveSourceUrl).TrimEnd('/');
        _origin = new Uri(BaseUrl, UriKind.Absolute);
        if (_origin.Scheme is not ("http" or "https") || _origin.UserInfo.Length > 0)
            throw new ArgumentException("Source runtime must have an HTTP(S) address without embedded credentials.", nameof(baseUrl));
        if (username.Length > 0)
        {
            _basic = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
            _privacy.Register(new Dictionary<string, string?> { ["username"] = username, ["password"] = password, ["authorization"] = _basic.ToString() });
        }
        if (workLimit > 0)
        {
            _total = new(workLimit, workLimit);
            var background = Math.Max(1, Math.Min(settings.BackgroundSourceConcurrency, Math.Max(1, workLimit - 1)));
            _background = new(background, background);
        }
        if (handler is not null && transportFactory is not null) throw new ArgumentException("Choose a one-shot handler or a fresh transport factory.");
        _transports = new(this, _owner, transportFactory ?? new DefaultSourceTransportFactory(settings), handler);
    }

    internal static SocketsHttpHandler CreateTransport(AutoSourcarrSettings settings) => new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            Credentials = null,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxConnectionsPerServer = Math.Max(4, settings.HttpMaxConnections),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(Math.Max(5, settings.HttpKeepaliveExpirySeconds)),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(5, settings.SourceRequestTimeout)),
            UseCookies = true
        };

    public string BaseUrl { get; }
    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(_settings.SourceRequestTimeout);
    public ISourceAttention? Attention { get; set; }
    // Instance-only return-boundary observation for deterministic ownership tests. No global hook or production bypass.
    internal Func<SourceTransportSession, Task>? BinaryScopeExitBoundary { get; set; }
    internal CancellationToken TerminalCancellationToken => _owner.CancellationToken;

    public SourceTransportSession OpenSession(CancellationToken lifetime = default) => _transports.Open(lifetime, asset: false);
    public SourceTransportSession OpenAssetSession(string url, CancellationToken lifetime = default)
        => _transports.Open(lifetime, asset: !SameOrigin(AssetUri(url)));
    /// <summary>Local cached-reader work may proceed without engine admission; a later native hop must bind the captured epoch.</summary>
    public SourceTransportSession OpenAssetSession(CancellationToken lifetime = default) => _transports.Open(lifetime, asset: true);
    /// <summary>Fence before waiting for shared source-admin. Never call drain while holding a session it must join.</summary>
    public SourceTransportPause BeginTransportPause() => _transports.BeginPause();
    public Task<SourceTransportDrain> DrainTransportAsync(SourceTransportPause pause, CancellationToken cancellationToken = default)
        => _transports.DrainAsync(pause, cancellationToken);
    /// <summary>Rotates HTTP resources only. The real native controller must first stop/join the old process and observe the replacement.</summary>
    public Task ResumeTransportAsync(SourceTransportDrain drain, CancellationToken cancellationToken = default)
        => _transports.ResumeAsync(drain, cancellationToken);

    public async Task<JsonObject> GraphQlAsync(string query, JsonObject? variables = null, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await session.GraphQlAsync(query, variables, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<JsonObject> GraphQlCoreAsync(SourceTransportSession session, string query, JsonObject? variables, CancellationToken cancellationToken)
    {
        if (SourceHost) return await HostOperationAsync(session, query, variables, cancellationToken).ConfigureAwait(false);
        session.ThrowIfRetired(); _ = session.EngineForRequest();
        var request = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["query"] = query, ["variables"] = variables?.DeepClone() ?? new JsonObject() });
        await using var response = await SendAsync(session, HttpMethod.Post, new Uri(BaseUrl + "/api/graphql"), request, "application/json", null, null, followRedirects: true, cancellationToken, requireRuntimeOrigin: true).ConfigureAwait(false);
        var body = await ReadJsonAsync(response).ConfigureAwait(false) as JsonObject
            ?? throw new SuwayomiException("Invalid GraphQL response: expected an object");
        session.ThrowIfRetired();
        if (body.TryGetPropertyValue("errors", out var errors))
        {
            if (errors is not JsonArray list || list.Any(error => error is not JsonObject || error["message"]?.GetValueKind() != JsonValueKind.String))
                throw new SuwayomiException("Invalid GraphQL error envelope");
            if (list.Count > 0)
                throw new SuwayomiException(string.Join("; ", list.Take(5).Select(error => Limit(_privacy.Text(error!["message"]!.GetValue<string>()), 500))));
        }
        return body["data"] as JsonObject ?? throw new SuwayomiException("Incomplete GraphQL response: no data object");
    }

    public async Task<JsonNode?> RestJsonAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var session = OpenAssetSession(path, cancellationToken);
        return await session.RestJsonAsync(path, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<JsonNode?> RestJsonCoreAsync(SourceTransportSession session, string path, CancellationToken cancellationToken)
    {
        var url = Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
            ? absolute : new Uri(BaseUrl + (path.StartsWith('/') ? path : "/" + path));
        await using var response = await SendAsync(session, HttpMethod.Get, url, null, null, null, null, true, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response).ConfigureAwait(false);
    }

    public string RuntimeAssetUrl(string? value)
    {
        var raw = (value ?? "").Trim();
        var previous = _settings.SourceSuwayomiUrl.TrimEnd('/');
        if (_settings.SourceEngineMode == "integrated" && BaseUrl == _settings.EffectiveSourceUrl
            && raw.StartsWith(previous + "/api/", StringComparison.Ordinal))
            return BaseUrl + raw[previous.Length..];
        return raw;
    }

    public async Task<OwnedUpstreamResponse> OpenBinaryStreamAsync(string url, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var session = OpenAssetSession(url, cancellationToken); OwnedUpstreamResponse? response = null;
        try
        {
            response = await session.OpenBinaryStreamAsync(url, timeout, cancellationToken).ConfigureAwait(false);
            if (BinaryScopeExitBoundary is { } boundary) await boundary(session).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            return response;
        }
        catch (Exception primary)
        {
            var failure = response is null ? primary : await response.DiscardAsync(primary).ConfigureAwait(false);
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                if (!ReferenceEquals(cleanup, primary)) failure = new AggregateException("Source response scope cleanup failed", failure, cleanup);
            }
            if (ReferenceEquals(failure, primary)) throw;
            throw failure;
        }
    }
    internal async Task<OwnedUpstreamResponse> OpenBinaryCoreAsync(SourceTransportSession session, string url, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var target = AssetUri(url);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken);
        await CheckAssetAttentionAsync(target, linked.Token).ConfigureAwait(false); session.ThrowIfRetired();
        return await SendAsync(session, HttpMethod.Get, target, null, null, null, timeout, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(byte[] Bytes, string ContentType)> FetchBinaryAsync(string url, TimeSpan? timeout = null,
        int maximumBytes = 32 * 1024 * 1024, string referer = "", CancellationToken cancellationToken = default)
    {
        await using var session = OpenAssetSession(url, cancellationToken);
        return await session.FetchBinaryAsync(url, timeout, maximumBytes, referer, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<(byte[] Bytes, string ContentType)> FetchBinaryCoreAsync(SourceTransportSession session, Uri target, TimeSpan? timeout,
        int maximumBytes, string referer, bool requireRuntimeOrigin, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken);
        await CheckAssetAttentionAsync(target, linked.Token).ConfigureAwait(false); session.ThrowIfRetired();
        var headers = new Dictionary<string, string> { ["Accept-Encoding"] = "identity" };
        if (referer.Length > 0) headers["Referer"] = referer;
        await using var response = await SendAsync(session, HttpMethod.Get, target, null, null, headers, timeout, true, cancellationToken, requireRuntimeOrigin).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response, maximumBytes).ConfigureAwait(false);
        return (bytes, response.ContentHeaders.ContentType?.ToString() ?? "application/octet-stream");
    }

    public async Task<(byte[] Bytes, string ContentType)> FetchAssetAsync(string value, CancellationToken cancellationToken = default)
    {
        var url = RuntimeAssetUrl(value);
        if (url.Length == 0) throw new SuwayomiException("Asset URL is empty");
        var target = url.StartsWith('/') ? new Uri(BaseUrl + url)
            : Uri.TryCreate(url, UriKind.Absolute, out var absolute) ? absolute : new Uri(BaseUrl + "/" + url);
        if (!SameOrigin(target)) throw new SuwayomiException("Refusing to proxy an external asset URL");
        await using var session = OpenSession(cancellationToken);
        var result = await FetchBinaryCoreAsync(session, target, TimeSpan.FromSeconds(Math.Min(Math.Max(_settings.SourceRequestTimeout, 5), 20)),
            4 * 1024 * 1024, "", true, cancellationToken).ConfigureAwait(false);
        var type = result.ContentType.Split(';', 2)[0].Trim();
        if (!type.StartsWith("image/", StringComparison.Ordinal) && type != "application/octet-stream")
            throw new SuwayomiException("Unexpected asset content type");
        return (result.Bytes, type);
    }

    public async Task<(byte[] Bytes, string ContentType)> FetchSourceBinaryAsync(string url, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await session.FetchSourceBinaryAsync(url, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_closeGate)
        {
            if (_dispose is null)
            {
                // Terminal fencing happens before DisposeAsync returns, not on a later continuation.
                var drain = _owner.StopAsync(); var transports = _transports.CloseAsync();
                _dispose = CloseAsync(drain, transports);
            }
            return new(_dispose);
        }
    }

    private async Task CloseAsync(Task drain, Task transports)
    {
        await Task.Yield();
        List<Exception> errors = [];
        try { await transports.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { await drain.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        finally
        {
            _total?.Dispose();
            _background?.Dispose();
            try { await _owner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { if (errors.Count == 0) errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    internal async Task<T> RunSourceWorkAsync<T>(SourceTransportSession session, Func<CancellationToken, Task<T>> operation, JsonObject? description,
        JsonObject? knownContext, CancellationToken cancellationToken)
    {
        session.ThrowIfRetired();
        var attention = description is null ? null : Attention;
        var context = knownContext;
        var budget = SourceWorkContext.CurrentBudget;
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken, session.ScopeCancellationToken);
        if (budget is not null) admission.CancelAfter(budget.Admission);
        var acquired = new List<SemaphoreSlim>();
        try
        {
            try
            {
                if (attention is not null)
                {
                    context ??= await attention.ContextAsync(BaseUrl, description!, admission.Token).ConfigureAwait(false);
                    await attention.CheckAsync(context, admission.Token).ConfigureAwait(false);
                }
                if (_runtime is not null) await _runtime.EnsureAvailableAsync("/api/graphql", admission.Token).ConfigureAwait(false);
                session.ThrowIfRetired();
                var semaphores = new List<SemaphoreSlim>();
                if (SourceWorkContext.IsBackground && _background is not null) semaphores.Add(_background);
                if (_total is not null) semaphores.Add(_total);
                foreach (var semaphore in semaphores)
                {
                    await semaphore.WaitAsync(admission.Token).ConfigureAwait(false);
                    acquired.Add(semaphore);
                }
                if (attention is not null) await attention.CheckAsync(context!, admission.Token).ConfigureAwait(false);
                session.ThrowIfRetired();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                session.ThrowIfRetired();
                throw new SourceWorkDeferredException("Source capacity or verification is busy; no upstream request started.");
            }
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken, session.ScopeCancellationToken);
            if (budget is not null) execution.CancelAfter(budget.Execution);
            try
            {
                var result = await operation(execution.Token).ConfigureAwait(false); session.ThrowIfRetired(); return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && execution.IsCancellationRequested)
            {
                session.ThrowIfRetired();
                throw new SourceBudgetExceededException();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                session.ThrowIfRetired();
                if (error is SourceWorkDeferredException) throw;
                if (_runtime?.BrowserMaintenance == true && error is HttpRequestException)
                    throw new SourceEngineUnavailableException("Source browser maintenance interrupted the request; retry shortly.");
                if (attention is not null) await attention.FailedAsync(context!, description!, error, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            for (var i = acquired.Count - 1; i >= 0; i--) acquired[i].Release();
        }
    }

    private async Task<OwnedUpstreamResponse> SendAsync(SourceTransportSession session, HttpMethod method, Uri target, byte[]? bytes, string? contentType,
        IReadOnlyDictionary<string, string>? headers, TimeSpan? timeout, bool followRedirects, CancellationToken cancellationToken,
        bool requireRuntimeOrigin = false, Func<HttpContent>? contentFactory = null, bool about = false,
        AuthenticationHeaderValue? aboutAuthorization = null, SourceAboutTransportRegistration? aboutTransport = null, int maximumRedirects = 20,
        bool allowErrorStatus = false)
    {
        using var call = session.EnterCall();
        var plane = session.Engine ?? (SameOrigin(target) ? session.EngineForRequest() : session.Plane);
        var pools = new List<SourceTransportEpoch> { plane }; var leases = new List<IDisposable> { plane.Enter() };
        var registrations = new List<CancellationTokenRegistration>();
        // Resource lifetime uses real caller/epoch/terminal sources, never the transient session-exit token.
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.RootCancellationToken, _owner.CancellationToken, plane.CancellationToken);
        var resourceToken = deadline.Token;
        var scopeCancellation = session.ScopeCancellationToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), deadline);
        var transferred = false;
        try
        {
            deadline.CancelAfter(timeout ?? RequestTimeout);
            for (var redirects = 0; redirects <= maximumRedirects; redirects++)
            {
                session.ThrowIfRetired(); deadline.Token.ThrowIfCancellationRequested();
                if (target.Scheme is not ("http" or "https")) throw new SuwayomiException("Unsupported upstream URL scheme");
                if (target.UserInfo.Length > 0 || requireRuntimeOrigin && !SameOrigin(target) || about && target.Fragment.Length > 0)
                    throw new SuwayomiException("Refusing an upstream redirect outside the configured source runtime");
                if (SameOrigin(target))
                {
                    var engine = session.EngineForRequest();
                    if (!pools.Contains(engine))
                    {
                        // A direct CDN operation may join only its captured still-open engine, never the latest replacement.
                        leases.Add(engine.Enter()); pools.Add(engine);
                        registrations.Add(engine.CancellationToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), deadline));
                    }
                    plane = engine;
                    if (!about && _runtime is not null) await _runtime.EnsureAvailableAsync(target.AbsolutePath, deadline.Token).ConfigureAwait(false);
                }
                else if (session.Engine is { } bound) plane = bound;
                session.ThrowIfRetired(); deadline.Token.ThrowIfCancellationRequested();
                var http = about ? plane.About(aboutTransport) : plane.Http;
                var request = new HttpRequestMessage(method, target);
                if (contentFactory is not null) request.Content = contentFactory();
                else if (bytes is not null)
                {
                    request.Content = new ByteArrayContent(bytes);
                    request.Content.Headers.ContentType = new(contentType ?? "application/octet-stream");
                }
                if (headers is not null) foreach (var (key, value) in headers) request.Headers.TryAddWithoutValidation(key, value);
                if (SameOrigin(target)) request.Headers.Authorization = about ? aboutAuthorization : _basic;
                if (SourceHost && _hostToken.Length > 0 && SameOrigin(target)) request.Headers.TryAddWithoutValidation("X-Source-Host-Token", _hostToken);
                HttpResponseMessage response;
                try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false); }
                catch { request.Dispose(); throw; }
                if (followRedirects && response.Headers.Location is { } location
                    && (int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var status = (int)response.StatusCode;
                    var next = location.IsAbsoluteUri ? location : new Uri(target, location);
                    CloseRaw(response, request, plane);
                    session.ThrowIfRetired(); resourceToken.ThrowIfCancellationRequested();
                    if (redirects == maximumRedirects) throw new SuwayomiException("Too many upstream redirects");
                    target = next;
                    if ((status == 303 && method != HttpMethod.Head) || (status is 301 or 302 && method == HttpMethod.Post))
                    { method = HttpMethod.Get; bytes = null; contentFactory = null; }
                    continue;
                }
                try { if (!allowErrorStatus) response.EnsureSuccessStatusCode(); }
                catch (Exception primary)
                {
                    try { CloseRaw(response, request, plane); } catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
                    session.ThrowIfRetired(); throw;
                }
                OwnedUpstreamResponse? owned = null;
                owned = new(response, request, deadline, cancellationToken, failure =>
                {
                    session.Released(owned!);
                    foreach (var pool in pools) pool.Released(owned!, failure);
                    try { foreach (var registration in registrations) registration.Dispose(); }
                    finally { foreach (var lease in leases) lease.Dispose(); }
                });
                transferred = true;
                var accepted = true;
                foreach (var pool in pools) accepted &= pool.Register(owned);
                session.Register(owned);
                owned.ArmCallerCancellation(cancellationToken, session.RootCancellationToken);
                // Remove transient scope cancellation only after the response has a physically tracked owner.
                scopeCancellation.Dispose();
                if (!accepted || deadline.IsCancellationRequested)
                {
                    await owned.DisposeAsync().ConfigureAwait(false);
                    session.ThrowIfRetired(); resourceToken.ThrowIfCancellationRequested();
                    throw new OperationCanceledException(session.CancellationToken);
                }
                return owned;
            }
            throw new SuwayomiException("Too many upstream redirects");
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            session.ThrowIfRetired();
            if (_owner.IsStopping) throw new OperationCanceledException(_owner.CancellationToken);
            throw new TimeoutException("Upstream response deadline exceeded.", error);
        }
        finally
        {
            scopeCancellation.Dispose();
            if (!transferred)
            {
                foreach (var registration in registrations) registration.Dispose();
                deadline.Dispose(); foreach (var lease in leases) lease.Dispose();
            }
        }
    }

    private static void CloseRaw(HttpResponseMessage response, HttpRequestMessage request, SourceTransportEpoch pool)
    {
        var errors = new List<Exception>();
        try { response.Dispose(); } catch (Exception error) { errors.Add(error); pool.Retain(error); }
        try { request.Dispose(); } catch (Exception error) { errors.Add(error); pool.Retain(error); }
        if (errors.Count > 0) throw new AggregateException("Upstream redirect/error cleanup failed", errors);
    }

    internal async Task<JsonNode?> ReadAboutCoreAsync(SourceTransportSession session, AuthenticationHeaderValue? authorization,
        SourceAboutTransportRegistration? transport, CancellationToken token)
    {
        const int maximum = 256 * 1024;
        _ = session.EngineForRequest();
        await using var response = await SendAsync(session, HttpMethod.Get, new Uri(BaseUrl + "/api/v1/settings/about"), null, null,
            new Dictionary<string, string> { ["Accept-Encoding"] = "identity" }, TimeSpan.FromMinutes(1), true, token,
            requireRuntimeOrigin: true, about: true, aboutAuthorization: authorization, aboutTransport: transport, maximumRedirects: 3).ConfigureAwait(false);
        if (response.ContentHeaders.ContentEncoding.Any(value => value.Length > 0 && !value.Equals("identity", StringComparison.OrdinalIgnoreCase))
            || response.ContentHeaders.ContentLength > maximum) throw new SuwayomiException("Engine about response is compressed or exceeds its byte bound");
        var bytes = await ReadBoundedAsync(response, maximum).ConfigureAwait(false); session.ThrowIfRetired();
        return JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 16 });
    }

    private async Task<JsonNode?> ReadJsonAsync(OwnedUpstreamResponse response)
    {
        try
        {
            var bytes = await ReadBoundedAsync(response, 32 * 1024 * 1024).ConfigureAwait(false);
            return JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 64 });
        }
        catch (JsonException) { throw new SuwayomiException("Invalid GraphQL JSON response"); }
    }

    private static async Task<byte[]> ReadBoundedAsync(OwnedUpstreamResponse response, int maximum)
    {
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        using var output = new MemoryStream();
        var buffer = new byte[32768];
        try
        {
            var input = await response.OpenBodyAsync().ConfigureAwait(false);
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, (long)maximum + 1 - output.Length)), response.CancellationToken).ConfigureAwait(false);
                if (read == 0) return output.ToArray();
                output.Write(buffer, 0, read);
                if (output.Length > maximum) throw new SuwayomiException("Asset/sample buffered-analysis limit exceeded.");
            }
        }
        catch (OperationCanceledException error) when (!response.IsClosing && !response.CallerCancellationToken.IsCancellationRequested && response.CancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Upstream body deadline exceeded.", error); }
    }

    private bool SameOrigin(Uri uri) => uri.Scheme.Equals(_origin.Scheme, StringComparison.OrdinalIgnoreCase)
        && uri.IdnHost.Equals(_origin.IdnHost, StringComparison.OrdinalIgnoreCase) && uri.Port == _origin.Port;
    internal Uri AssetUri(string url)
    {
        var raw = RuntimeAssetUrl(url);
        if (raw.Length == 0) throw new SuwayomiException("Asset URL is empty");
        return Uri.TryCreate(raw, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
            ? absolute : new Uri(BaseUrl + (raw.StartsWith('/') ? raw : "/" + raw));
    }
    private async Task CheckAssetAttentionAsync(Uri target, CancellationToken cancellationToken)
    {
        if (Attention is null) return;
        if (target.Authority.Equals(_origin.Authority, StringComparison.OrdinalIgnoreCase))
        {
            var match = System.Text.RegularExpressions.Regex.Match(target.AbsolutePath, @"^/api/v1/manga/(\d+)/");
            if (match.Success)
            {
                var context = await Attention.ContextAsync(BaseUrl, new JsonObject { ["kind"] = "manga", ["id"] = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
                await Attention.CheckAsync(context, cancellationToken).ConfigureAwait(false);
            }
        }
        else await Attention.CheckWebsiteAsync(BaseUrl, target.Host, cancellationToken).ConfigureAwait(false);
    }
    private static string Limit(string text, int count) => text.Length <= count ? text : text[..count];
}
