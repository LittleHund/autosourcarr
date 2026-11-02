using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Sources;

/// <summary>
/// 0.4.5 <c>_record_source_result</c> (service.py): what discovery, feed crawls and metadata enrichment learned about a
/// source. An answer clears the source's failures; SOURCE_FAILURE_THRESHOLD real failures in a row pause it for
/// SOURCE_COOLDOWN_SECONDS (a challenge, rate limit or access refusal pauses at once; a CAPTCHA for at least 5 minutes).
/// Hard rule 3: a local timeout, a cancelled or expired local deadline and a capacity deferral are never evidence of a
/// source outage and are ignored here. Neither is a failure of the app's own link to the source engine (the source
/// host on 127.0.0.1 or the configured Suwayomi: connection refused or reset, the engine unavailable or restarting), nor
/// anything that fails while the engine is not ready (<paramref name="engineReady"/>). In memory only, like 0.4.5.
/// </summary>
public sealed class SourceResultTracker(Func<AutoSourcarrSettings> settings, TimeProvider? clock = null, Func<bool>? engineReady = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Until, string Error, string Kind)> _failures = new(StringComparer.Ordinal);

    /// <summary>Raised when a source becomes paused or recovers (live UI topic <c>source-health</c>).</summary>
    public event Action<string>? Changed;

    /// <summary>Text the source host and the engine link use while the engine itself is not answering.</summary>
    private static readonly string[] EngineLinkMarkers =
    [
        "connection refused", "connection reset", "actively refused", "no connection could be made", "an existing connection was forcibly closed",
        "source host is restarting", "source host is not running", "source host did not start", "source engine is not ready",
        "source engine is restarting", "source engine unavailable", "source host unavailable", "127.0.0.1:", "localhost:", "[::1]:",
    ];

    /// <summary>Whether this error says nothing about the source itself (hard rule 3).</summary>
    public static bool IsLocal(Exception error) => error switch
    {
        // SourceEngineUnavailableException is a SourceWorkDeferredException.
        SourceWorkDeferredException or TimeoutException or OperationCanceledException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsLocal),
        // A transport failure has no HTTP status: the app could not reach its source engine (the engine reports a site's
        // own failure as an answer with its message), so it is never the source's fault.
        HttpRequestException { StatusCode: null } => true,
        System.Net.Sockets.SocketException => true,
        IOException { InnerException: System.Net.Sockets.SocketException } => true,
        _ => SourceFailures.Kind(error) is "deferred" or "timeout"
            || EngineLinkMarkers.Any(marker => error.Message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            || error.InnerException is { } inner && IsLocal(inner),
    };

    public void Record(string sourceId, Exception? error)
    {
        if (string.IsNullOrEmpty(sourceId)) return;
        if (error is not null && (IsLocal(error) || engineReady?.Invoke() == false)) return;
        if (error is null)
        {
            if (_failures.TryRemove(sourceId, out var previous) && previous.Until > _clock.GetUtcNow()) Changed?.Invoke(sourceId);
            return;
        }
        var current = settings();
        var threshold = Math.Max(1, current.SourceFailureThreshold);
        var kind = SourceFailures.Kind(error);
        var wasPaused = Paused(sourceId);
        var failures = (_failures.TryGetValue(sourceId, out var known) ? known.Failures : 0) + 1;
        if (kind is "captcha" or "rate_limit" or "access_denied") failures = Math.Max(failures, threshold);
        var until = DateTimeOffset.MinValue;
        if (failures >= threshold)
        {
            var seconds = Math.Max(kind == "captcha" ? 300.0 : 1.0, Math.Max(current.SourceCooldownSeconds, SourceFailures.ChapterRetryDelay(error)));
            until = _clock.GetUtcNow().AddSeconds(seconds);
        }
        var text = (kind == "captcha" ? "CAPTCHA: " : "") + error.Message;
        _failures[sourceId] = (failures, until, text.Length > 1000 ? text[..1000] : text, kind);
        if (!wasPaused && until > _clock.GetUtcNow()) Changed?.Invoke(sourceId);
    }

    /// <summary>A source paused after repeated real failures (callers skip it until the pause ends).</summary>
    public bool Paused(string sourceId)
    {
        if (!_failures.TryGetValue(sourceId, out var state)) return false;
        if (state.Until != DateTimeOffset.MinValue && state.Until <= _clock.GetUtcNow()) { _failures.TryRemove(sourceId, out _); return false; }
        return state.Until != DateTimeOffset.MinValue && state.Failures >= Math.Max(1, settings().SourceFailureThreshold);
    }

    public int PausedCount() => _failures.Keys.Count(Paused);

    /// <summary>The 0.4.5 <c>source_cooldowns</c> list: at most 100 sources with failures, retry time and last error.</summary>
    public JsonArray Snapshot()
    {
        var now = _clock.GetUtcNow();
        return new JsonArray(_failures.OrderBy(pair => pair.Key, StringComparer.Ordinal).Take(100).Select(pair => (JsonNode?)new JsonObject
        {
            ["source_id"] = pair.Key, ["failures"] = pair.Value.Failures, ["kind"] = pair.Value.Kind,
            ["paused"] = pair.Value.Until > now,
            ["retry_after_seconds"] = pair.Value.Until > now ? Math.Round((pair.Value.Until - now).TotalSeconds) : 0,
            ["last_error"] = pair.Value.Error,
        }).ToArray());
    }

    public override string ToString() => "Source results (" + _failures.Count.ToString(CultureInfo.InvariantCulture) + " failing)";
}
