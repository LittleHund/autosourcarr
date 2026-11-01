using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Connections.Types;

/// <summary>A change seen in the main Suwayomi (CON-3).</summary>
public abstract record SuwayomiChange;

/// <summary>Manga were added to or removed from the main library (library ids are Suwayomi manga ids).</summary>
public sealed record SuwayomiLibraryChanged(IReadOnlyList<long> Added, IReadOnlyList<long> Removed) : SuwayomiChange;

/// <summary>Chapter lists of these manga changed (library update finished for them). <paramref name="Incomplete"/>: Suwayomi
/// omitted updates or the ids are unknown; refresh everything.</summary>
public sealed record SuwayomiChaptersChanged(IReadOnlyList<long> MangaIds, bool Incomplete) : SuwayomiChange;

/// <summary>Downloads of these manga/chapters were queued, finished, failed or removed. <paramref name="Incomplete"/> as above.</summary>
public sealed record SuwayomiDownloadChanged(IReadOnlyList<long> MangaIds, IReadOnlyList<long> ChapterIds, bool Incomplete) : SuwayomiChange;

public enum SuwayomiFeedMode { Starting, Live, Polling, Stopped }

/// <summary>The feed switched between live subscriptions and the polling fallback.</summary>
public sealed record SuwayomiFeedModeChanged(SuwayomiFeedMode Mode, string Reason) : SuwayomiChange;

public sealed record SuwayomiChangeFeedOptions
{
    /// <summary>Library diff interval while subscriptions are unavailable (the polling fallback).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// Library-membership diff interval while subscriptions are live. Suwayomi v2.x has no subscription for library
    /// add/remove (only downloads, library updates, sync and WebUI updates), so membership is always an ids diff.
    /// </summary>
    public TimeSpan LivePollInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ReconnectMin { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMax { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>The reconnect back-off starts over only after a socket stayed up this long (a socket that opens and drops at once keeps backing off).</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>graphql-transport-ws ping interval; no message for two intervals means the socket is dead.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>maxUpdates of the download and library-update subscriptions (Suwayomi omits the rest and says so).</summary>
    public int MaxUpdates { get; init; } = 50;
    /// <summary>Emit the first library read as "added" (an initial sync); off by default.</summary>
    public bool EmitInitialLibrary { get; init; }
}

/// <summary>
/// CON-3 change feed of the main Suwayomi. Live: GraphQL subscriptions over WebSocket (graphql-transport-ws at
/// /api/graphql, Suwayomi v2.x: downloadStatusChanged and libraryUpdateStatusChanged) give download and chapter changes
/// within a second; the socket is pinged, and reconnects with exponential back-off. Library membership has no
/// subscription in Suwayomi, so a light ids diff runs every <see cref="SuwayomiChangeFeedOptions.LivePollInterval"/>.
/// Fallback: while the socket is down or subscriptions are refused, the same diff also compares chapter and download
/// counts every <see cref="SuwayomiChangeFeedOptions.PollInterval"/>. Changes reach <c>onChange</c> on the feed's own
/// tasks; it must be quick and must not throw (a throw is reported to <c>onError</c>).
/// </summary>
public sealed class SuwayomiChangeFeed : IAsyncDisposable
{
    public const string Protocol = "graphql-transport-ws";
    private const string DownloadsId = "autosourcarr-downloads";
    private const string UpdatesId = "autosourcarr-updates";
    private const int MaxMessage = 4 * 1024 * 1024;
    private static readonly string[] PollFields = ["id downloadCount chapters { totalCount }", "id"];

    private readonly SuwayomiEndpoint _endpoint;
    private readonly SuwayomiGraphQlHttp _graph;
    private readonly Action<SuwayomiChange> _onChange;
    private readonly Action<Exception>? _onError;
    private readonly SuwayomiChangeFeedOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _nudge = new(0, 1);
    private readonly object _gate = new();
    private Task? _socketLoop, _pollLoop;
    private int _mode = (int)SuwayomiFeedMode.Starting;
    private string _lastSocketError = "", _lastPollError = "";
    private int _pollFields;

    public SuwayomiChangeFeed(SuwayomiEndpoint endpoint, HttpMessageHandler http, Action<SuwayomiChange> onChange,
        Action<Exception>? onError = null, SuwayomiChangeFeedOptions? options = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _onChange = onChange ?? throw new ArgumentNullException(nameof(onChange));
        _onError = onError;
        _options = options ?? new SuwayomiChangeFeedOptions();
        _graph = new SuwayomiGraphQlHttp(endpoint, http, TimeSpan.FromSeconds(30));
    }

    public SuwayomiFeedMode Mode => (SuwayomiFeedMode)Volatile.Read(ref _mode);

    /// <summary>Starts the socket and the library diff; a second call does nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
            if (_socketLoop is not null) return;
            _socketLoop = Task.Run(() => SocketLoopAsync(_stop.Token));
            _pollLoop = Task.Run(() => PollLoopAsync(_stop.Token));
        }
    }

    /// <summary>Runs the library diff now instead of at the next interval.</summary>
    public void PollNow()
    {
        try { if (_nudge.CurrentCount == 0) _nudge.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    // ---- live subscriptions

    private async Task SocketLoopAsync(CancellationToken token)
    {
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            var refused = false;
            DateTimeOffset? connectedAt = null;
            try
            {
                await RunSocketAsync(() => connectedAt = DateTimeOffset.UtcNow, token).ConfigureAwait(false);
                SetMode(SuwayomiFeedMode.Polling, "The Suwayomi subscription socket closed; polling until it reconnects.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                refused = error is SubscriptionRefusedException;
                SetMode(SuwayomiFeedMode.Polling, (refused ? "Suwayomi refused the subscriptions" : "Suwayomi subscriptions are unavailable")
                    + "; polling every " + _options.PollInterval.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s.");
                bool fresh;
                lock (_gate) { fresh = error.Message != _lastSocketError; _lastSocketError = error.Message; }
                if (fresh) Report(error);
            }
            if (connectedAt is { } since && DateTimeOffset.UtcNow - since >= _options.StableAfter) attempt = 0;
            var delay = refused ? _options.ReconnectMax : Backoff(attempt++);
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var seconds = _options.ReconnectMin.TotalSeconds * Math.Pow(2, Math.Min(attempt, 16));
        return TimeSpan.FromSeconds(Math.Min(_options.ReconnectMax.TotalSeconds, seconds));
    }

    private async Task RunSocketAsync(Action connected, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(Protocol);
        socket.Options.Proxy = null;
        if (_endpoint.Authorization is { } authorization) socket.Options.SetRequestHeader("Authorization", authorization.ToString());
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connect.CancelAfter(_options.ConnectTimeout);
            try { await socket.ConnectAsync(_endpoint.WebSocket, connect.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Suwayomi subscription socket did not open in time."); }
        }
        // Suwayomi (Javalin) may not echo the sub-protocol; only a different one is a refusal.
        if (socket.SubProtocol is { Length: > 0 } chosen && chosen != Protocol) throw new SubscriptionRefusedException("Suwayomi did not accept the " + Protocol + " protocol.");
        using var send = new SemaphoreSlim(1, 1);
        using var life = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task SendAsync(JsonObject message)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            await send.WaitAsync(life.Token).ConfigureAwait(false);
            try { await socket.SendAsync(new ReadOnlyMemory<byte>(bytes), WebSocketMessageType.Text, true, life.Token).ConfigureAwait(false); }
            finally { send.Release(); }
        }

        await SendAsync(new JsonObject { ["type"] = "connection_init", ["payload"] = new JsonObject() }).ConfigureAwait(false);
        var ackBy = DateTimeOffset.UtcNow + _options.ConnectTimeout;
        while (true)
        {
            var wait = ackBy - DateTimeOffset.UtcNow;
            if (wait <= TimeSpan.Zero) throw new TimeoutException("Suwayomi did not acknowledge the subscription connection.");
            var message = await ReceiveAsync(socket, wait, token).ConfigureAwait(false)
                ?? throw new WebSocketException("Suwayomi closed the subscription socket during the handshake.");
            var type = JsonRead.Text(message["type"]);
            if (type == "connection_ack") break;
            if (type == "ping") await SendAsync(new JsonObject { ["type"] = "pong" }).ConfigureAwait(false);
            else if (type is "error" or "connection_error") throw new SubscriptionRefusedException("Suwayomi rejected the subscription connection.");
        }
        var max = Math.Max(1, _options.MaxUpdates).ToString(CultureInfo.InvariantCulture);
        await SendAsync(Subscribe(DownloadsId, "subscription AutoSourcarrDownloads { downloadStatusChanged(input: {maxUpdates: " + max
            + "}) { omittedUpdates updates { type download { state chapter { id mangaId } } } } }")).ConfigureAwait(false);
        await SendAsync(Subscribe(UpdatesId, "subscription AutoSourcarrLibraryUpdates { libraryUpdateStatusChanged(input: {maxUpdates: " + max
            + "}) { omittedUpdates jobsInfo { isRunning } mangaUpdates { status manga { id } } } }")).ConfigureAwait(false);
        connected();
        lock (_gate) _lastSocketError = "";
        SetMode(SuwayomiFeedMode.Live, "Suwayomi subscriptions are live.");
        PollNow(); // catch anything that changed while the socket was down

        var pinger = Task.Run(async () =>
        {
            try
            {
                while (!life.Token.IsCancellationRequested)
                {
                    await Task.Delay(_options.KeepAlive, life.Token).ConfigureAwait(false);
                    await SendAsync(new JsonObject { ["type"] = "ping" }).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
        }, CancellationToken.None);
        try
        {
            var silence = _options.KeepAlive * 2 + TimeSpan.FromSeconds(1);
            while (true)
            {
                var message = await ReceiveAsync(socket, silence, token).ConfigureAwait(false);
                if (message is null) return; // closed by Suwayomi
                switch (JsonRead.Text(message["type"]))
                {
                    case "ping": await SendAsync(new JsonObject { ["type"] = "pong" }).ConfigureAwait(false); break;
                    case "next": Next(JsonRead.Text(message["id"]), message["payload"] as JsonObject); break;
                    case "error": throw new SubscriptionRefusedException("Suwayomi refused subscription " + JsonRead.Text(message["id"]) + ".");
                    case "complete": return; // a subscription ended: reconnect and subscribe again
                }
            }
        }
        finally
        {
            await life.CancelAsync().ConfigureAwait(false);
            await pinger.ConfigureAwait(false);
            if (socket.State == WebSocketState.Open)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", close.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }

    private static JsonObject Subscribe(string id, string query) => new()
    {
        ["id"] = id, ["type"] = "subscribe", ["payload"] = new JsonObject { ["query"] = query, ["variables"] = new JsonObject() },
    };

    /// <summary>Reads one whole text message; null on close. No message within <paramref name="wait"/> is a dead socket.</summary>
    private static async Task<JsonObject?> ReceiveAsync(ClientWebSocket socket, TimeSpan wait, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(wait);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(chunk.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                buffer.Write(chunk, 0, result.Count);
                if (buffer.Length > MaxMessage) throw new WebSocketException("A Suwayomi subscription message is larger than 4 MiB.");
                if (!result.EndOfMessage) continue;
                if (result.MessageType != WebSocketMessageType.Text) { buffer.SetLength(0); continue; }
                try { return JsonNode.Parse(buffer.ToArray()) as JsonObject ?? throw new WebSocketException("A Suwayomi subscription message is not a JSON object."); }
                catch (JsonException error) { throw new WebSocketException("A Suwayomi subscription message is not JSON.", error); }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("The Suwayomi subscription socket went silent."); }
    }

    private void Next(string id, JsonObject? payload)
    {
        if (payload?["data"] is not JsonObject data) return;
        if (id == DownloadsId && data["downloadStatusChanged"] is JsonObject downloads)
        {
            var mangas = new SortedSet<long>();
            var chapters = new SortedSet<long>();
            foreach (var update in JsonRead.Objects(downloads["updates"]))
            {
                // Progress and queue-position updates are frequent and change nothing a reader sees.
                if (JsonRead.Text(update["type"]) is "PROGRESS" or "POSITION") continue;
                var chapter = (update["download"] as JsonObject)?["chapter"] as JsonObject;
                if (JsonRead.Long(chapter?["mangaId"]) is { } manga) mangas.Add(manga);
                if (JsonRead.Long(chapter?["id"]) is { } chapterId) chapters.Add(chapterId);
            }
            var omitted = JsonRead.Bool(downloads["omittedUpdates"]);
            if (mangas.Count > 0 || omitted) Emit(new SuwayomiDownloadChanged([.. mangas], [.. chapters], omitted));
        }
        else if (id == UpdatesId && data["libraryUpdateStatusChanged"] is JsonObject updates)
        {
            var mangas = new SortedSet<long>();
            foreach (var update in JsonRead.Objects(updates["mangaUpdates"]))
                if (JsonRead.Text(update["status"]) == "COMPLETE" && JsonRead.Long((update["manga"] as JsonObject)?["id"]) is { } manga) mangas.Add(manga);
            var omitted = JsonRead.Bool(updates["omittedUpdates"]);
            if (mangas.Count > 0 || omitted) Emit(new SuwayomiChaptersChanged([.. mangas], omitted));
            // A finished library update may also have added manga (auto-add categories): diff the library now.
            if ((updates["jobsInfo"] as JsonObject)?["isRunning"] is JsonValue running && running.TryGetValue<bool>(out var busy) && !busy) PollNow();
        }
    }

    // ---- library diff (membership always; chapter/download counts only while not live, and only read then)

    private async Task PollLoopAsync(CancellationToken token)
    {
        Dictionary<long, (int Chapters, int Downloads)>? baseline = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                // While live, the subscriptions report chapters and downloads: read ids only (a much lighter query).
                var current = await ReadLibraryAsync(Mode == SuwayomiFeedMode.Live, token).ConfigureAwait(false);
                if (baseline is null)
                {
                    if (_options.EmitInitialLibrary && current.Count > 0) Emit(new SuwayomiLibraryChanged([.. current.Keys.Order()], []));
                }
                else Diff(baseline, current, Mode != SuwayomiFeedMode.Live);
                baseline = current;
                lock (_gate) _lastPollError = "";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                bool fresh;
                lock (_gate) { fresh = error.Message != _lastPollError; _lastPollError = error.Message; }
                if (fresh) Report(error);
            }
            var interval = Mode == SuwayomiFeedMode.Live ? _options.LivePollInterval : _options.PollInterval;
            try { await _nudge.WaitAsync(interval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<Dictionary<long, (int Chapters, int Downloads)>> ReadLibraryAsync(bool idsOnly, CancellationToken token)
    {
        while (true)
        {
            var fields = idsOnly ? PollFields[^1] : PollFields[Math.Min(Volatile.Read(ref _pollFields), PollFields.Length - 1)];
            List<JsonObject> rows;
            try { rows = await _graph.LibraryAsync(fields, token).ConfigureAwait(false); }
            catch (ConnectionHttpException error) when (!idsOnly && error.Message.Contains("GraphQL error", StringComparison.Ordinal) && Volatile.Read(ref _pollFields) < PollFields.Length - 1)
            {
                Interlocked.Increment(ref _pollFields); // an older server without downloadCount: ids only
                continue;
            }
            var result = new Dictionary<long, (int, int)>();
            foreach (var row in rows)
                if (JsonRead.Long(row["id"]) is long id && id > 0)
                    result[id] = (JsonRead.Int((row["chapters"] as JsonObject)?["totalCount"]) ?? -1, JsonRead.Int(row["downloadCount"]) ?? -1);
            return result;
        }
    }

    private void Diff(Dictionary<long, (int Chapters, int Downloads)> before, Dictionary<long, (int Chapters, int Downloads)> after, bool counts)
    {
        var added = after.Keys.Where(id => !before.ContainsKey(id)).Order().ToList();
        var removed = before.Keys.Where(id => !after.ContainsKey(id)).Order().ToList();
        if (added.Count > 0 || removed.Count > 0) Emit(new SuwayomiLibraryChanged(added, removed));
        if (!counts) return;
        var chapters = new List<long>();
        var downloads = new List<long>();
        foreach (var (id, now) in after.OrderBy(pair => pair.Key))
        {
            // -1: not read (ids-only while live, or an older server); unknown is not a change.
            if (!before.TryGetValue(id, out var then)) continue;
            if (then.Chapters >= 0 && now.Chapters >= 0 && now.Chapters != then.Chapters) chapters.Add(id);
            if (then.Downloads >= 0 && now.Downloads >= 0 && now.Downloads != then.Downloads) downloads.Add(id);
        }
        if (chapters.Count > 0) Emit(new SuwayomiChaptersChanged(chapters, false));
        if (downloads.Count > 0) Emit(new SuwayomiDownloadChanged(downloads, [], false));
    }

    // ---- plumbing

    private void SetMode(SuwayomiFeedMode mode, string reason)
    {
        if (Interlocked.Exchange(ref _mode, (int)mode) == (int)mode) return;
        Emit(new SuwayomiFeedModeChanged(mode, reason));
    }

    private void Emit(SuwayomiChange change)
    {
        try { _onChange(change); }
        catch (Exception error) when (error is not OutOfMemoryException) { Report(error); }
    }

    private void Report(Exception error)
    {
        try { _onError?.Invoke(error); }
        catch (Exception secondary) when (secondary is not OutOfMemoryException) { _ = secondary; }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] loops;
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            _stop.Cancel();
            loops = new[] { _socketLoop, _pollLoop }.OfType<Task>().ToArray();
        }
        try { await Task.WhenAll(loops).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        SetMode(SuwayomiFeedMode.Stopped, "Stopped.");
        _graph.Dispose();
        _nudge.Dispose();
        _stop.Dispose();
    }

    /// <summary>The server refused the protocol or a subscription: retry only after the longest back-off.</summary>
    private sealed class SubscriptionRefusedException(string message) : Exception(message);
}
