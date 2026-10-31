using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace AutoSourcarr.Core.Jobs;

/// <summary>One change event: a stream-wide increasing id, a topic ("tasks", "jobs", "errors", ...) and a small JSON body.</summary>
public sealed record AppEvent(long Id, string Topic, string At, JsonObject Data);

/// <summary>
/// PERF-8 / UI-13: the single event stream. Every change is published once here and fans out to the live UI (SSE),
/// task history and diagnostics. Ids only increase, the last <see cref="Capacity"/> events are kept so a reconnecting
/// browser resumes from its Last-Event-ID; a reader that fell further behind gets a "resync" event and reloads.
/// Subscribers never block publishers: a slow reader's buffer is bounded and it is told to resync when it overflows.
/// </summary>
public sealed class EventStream
{
    public const int Capacity = 1024;
    private const int SubscriberBuffer = 256;
    private readonly object _gate = new();
    private readonly AppEvent[] _ring = new AppEvent[Capacity];
    private readonly List<Subscriber> _subscribers = [];
    private readonly Func<DateTimeOffset> _clock;
    private long _next = 1;
    private int _count;

    public EventStream(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Highest id published so far (0 before the first event).</summary>
    public long LastId { get { lock (_gate) return _next - 1; } }

    public int SubscriberCount { get { lock (_gate) return _subscribers.Count; } }

    public AppEvent Publish(string topic, JsonObject? data = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        AppEvent item;
        lock (_gate)
        {
            item = new AppEvent(_next++, topic, _clock().ToString("O", CultureInfo.InvariantCulture), data ?? []);
            _ring[(int)(item.Id % Capacity)] = item;
            _count = Math.Min(Capacity, _count + 1);
            // Delivered inside the lock so every reader sees ids in order; Offer never blocks.
            foreach (var subscriber in _subscribers)
                subscriber.Offer(item);
        }
        return item;
    }

    private readonly Dictionary<string, bool> _pulses = new(StringComparer.Ordinal);

    /// <summary>
    /// Coalesced "something changed" event: many changes inside <paramref name="window"/> become one event, so a busy
    /// task list does not flood the browser. The first change is published at once, the rest at the end of the window.
    /// </summary>
    public void Pulse(string topic, TimeSpan window, JsonObject? data = null)
    {
        lock (_gate)
        {
            if (!_pulses.TryAdd(topic, false)) { _pulses[topic] = true; return; }
        }
        Publish(topic, data?.DeepClone().AsObject() ?? new JsonObject { ["change"] = "updated" });
        _ = Task.Delay(window).ContinueWith(_ =>
        {
            bool again;
            lock (_gate) { again = _pulses.Remove(topic, out var flag) && flag; }
            if (again) Pulse(topic, window, data);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Events after <paramref name="lastId"/> still held, oldest first; null when some were already dropped.</summary>
    public IReadOnlyList<AppEvent>? Since(long lastId)
    {
        lock (_gate)
        {
            var newest = _next - 1;
            // An id from before a restart (ids start at 1 again) cannot be resumed: the reader must reload.
            if (lastId > newest) return null;
            if (lastId == newest) return [];
            var oldest = newest - _count + 1;
            if (lastId + 1 < oldest) return null;
            var result = new List<AppEvent>((int)(newest - lastId));
            for (var id = lastId + 1; id <= newest; id++)
                result.Add(_ring[(int)(id % Capacity)]);
            return result;
        }
    }

    /// <summary>
    /// A live subscription. With <paramref name="lastId"/> the held backlog after it is replayed first, then new events
    /// follow without gaps or duplicates. When the backlog is gone (or the reader overflowed) a "resync" event is sent.
    /// </summary>
    public EventSubscription Subscribe(long? lastId = null, IReadOnlySet<string>? topics = null)
    {
        lock (_gate)
        {
            IReadOnlyList<AppEvent>? backlog = lastId is { } after ? Since(after) : [];
            // Room for the whole replay plus the normal live buffer, so resuming never overflows by itself.
            var subscriber = new Subscriber(topics, SubscriberBuffer + (backlog?.Count ?? 1));
            if (backlog is null) subscriber.Offer(Resync(lastId!.Value));
            else foreach (var item in backlog) subscriber.Offer(item);
            _subscribers.Add(subscriber);
            return new EventSubscription(subscriber, () => { lock (_gate) _subscribers.Remove(subscriber); });
        }
    }

    private AppEvent Resync(long lastId) => new(_next - 1, "resync", _clock().ToString("O", CultureInfo.InvariantCulture),
        new JsonObject { ["reason"] = "Events after " + lastId.ToString(CultureInfo.InvariantCulture) + " are no longer held; reload the page data." });

    internal sealed class Subscriber(IReadOnlySet<string>? topics, int capacity)
    {
        private readonly Channel<AppEvent> _channel = Channel.CreateBounded<AppEvent>(new BoundedChannelOptions(capacity)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        private int _overflowed;
        internal ChannelReader<AppEvent> Reader => _channel.Reader;

        internal void Offer(AppEvent item)
        {
            if (topics is not null && item.Topic != "resync" && !topics.Contains(item.Topic)) return;
            if (Volatile.Read(ref _overflowed) != 0) return;
            if (_channel.Writer.TryWrite(item)) return;
            // The reader is too slow: stop queueing and tell it to reload instead of holding the publisher.
            Interlocked.Exchange(ref _overflowed, 1);
            _channel.Writer.TryComplete(new EventStreamOverflowException(item.Id));
        }
    }
}

/// <summary>A reader fell behind by more than its buffer; it must reload and reconnect with the id it last saw.</summary>
public sealed class EventStreamOverflowException(long id) : Exception("The event reader fell behind at event " + id.ToString(CultureInfo.InvariantCulture) + ".")
{
    public long Id { get; } = id;
}

public sealed class EventSubscription : IDisposable
{
    private readonly EventStream.Subscriber _subscriber;
    private Action? _dispose;
    internal EventSubscription(EventStream.Subscriber subscriber, Action dispose) { _subscriber = subscriber; _dispose = dispose; }

    /// <summary>Waits for the next event; null when <paramref name="wait"/> passes first (send a heartbeat then).</summary>
    public async Task<AppEvent?> NextAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        if (_subscriber.Reader.TryRead(out var ready)) return ready;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        try
        {
            if (!await _subscriber.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
                await _subscriber.Reader.Completion.ConfigureAwait(false); // rethrows the overflow
            return _subscriber.Reader.TryRead(out var item) ? item : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
