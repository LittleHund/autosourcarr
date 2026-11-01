using System.Collections.Concurrent;

namespace AutoSourcarr.Core.Connections.Types;

/// <summary>
/// A token bucket: <see cref="Capacity"/> requests per <see cref="Period"/>, refilled continuously. The server's own view
/// wins when it is stricter: <see cref="Observe"/> lowers the free tokens to X-RateLimit-Remaining and blocks until the
/// reported reset when nothing is left; <see cref="BlockFor"/> applies a Retry-After to every caller of the bucket.
/// Waiting is bounded: a wait longer than the caller's limit fails with <see cref="MetadataRateLimitedException"/>.
/// </summary>
public sealed class TokenBucketLimiter
{
    private static readonly ConcurrentDictionary<string, TokenBucketLimiter> Shared = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly double _perTick;
    private double _tokens;
    private long _stamp;
    private long _blockedUntil;

    public TokenBucketLimiter(int capacity, TimeSpan period, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
        _time = time ?? TimeProvider.System;
        Capacity = capacity;
        Period = period;
        _perTick = capacity / (period.TotalSeconds * _time.TimestampFrequency);
        _tokens = capacity;
        _stamp = _time.GetTimestamp();
    }

    /// <summary>One process-wide bucket per key (provider + host), so every client of a provider shares its limit.</summary>
    public static TokenBucketLimiter ForKey(string key, int capacity, TimeSpan period)
        => Shared.GetOrAdd(key, _ => new TokenBucketLimiter(capacity, period));

    public int Capacity { get; }
    public TimeSpan Period { get; }
    /// <summary>The clock of this bucket (also used for the servers' reset times).</summary>
    public TimeProvider Time => _time;

    /// <summary>Whole tokens free now.</summary>
    public int Available
    {
        get { lock (_gate) { Refill(_time.GetTimestamp()); return Blocked(_time.GetTimestamp()) > TimeSpan.Zero ? 0 : (int)Math.Floor(_tokens); } }
    }

    /// <summary>Takes a token when one is free; otherwise returns false and how long until one will be.</summary>
    public bool TryAcquire(out TimeSpan wait)
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            Refill(now);
            var blocked = Blocked(now);
            if (blocked > TimeSpan.Zero) { wait = blocked; return false; }
            if (_tokens >= 1) { _tokens -= 1; wait = TimeSpan.Zero; return true; }
            wait = TimeSpan.FromSeconds((1 - _tokens) / (_perTick * _time.TimestampFrequency));
            if (wait < TimeSpan.FromMilliseconds(1)) wait = TimeSpan.FromMilliseconds(1);
            return false;
        }
    }

    /// <summary>Waits for a token. A single wait longer than <paramref name="maxWait"/> fails at once with a clear message.</summary>
    public async Task WaitAsync(string provider, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        while (!TryAcquire(out var wait))
        {
            if (waited + wait > maxWait) throw new MetadataRateLimitedException(provider, waited + wait);
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            waited += wait;
        }
    }

    /// <summary>
    /// The server's X-RateLimit-Remaining (and optional time until its reset): free tokens never exceed what the server
    /// still allows, and with nothing left every caller waits for the reset (or one refill period when none is given).
    /// </summary>
    public void Observe(int? remaining, TimeSpan? resetIn)
    {
        if (remaining is not { } left) return;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            Refill(now);
            if (left < _tokens) _tokens = Math.Max(0, left);
            if (left <= 0) BlockCore(now, resetIn is { } reset && reset > TimeSpan.Zero ? reset : Period / Capacity);
        }
    }

    /// <summary>A Retry-After: nobody using this bucket sends before it passes.</summary>
    public void BlockFor(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        lock (_gate) { var now = _time.GetTimestamp(); Refill(now); _tokens = 0; BlockCore(now, delay); }
    }

    private void BlockCore(long now, TimeSpan delay)
    {
        var until = now + (long)(delay.TotalSeconds * _time.TimestampFrequency);
        if (until > _blockedUntil) _blockedUntil = until;
    }

    private TimeSpan Blocked(long now) => _blockedUntil > now ? TimeSpan.FromSeconds((_blockedUntil - now) / (double)_time.TimestampFrequency) : TimeSpan.Zero;

    private void Refill(long now)
    {
        // Tokens do not accumulate while blocked by the server.
        var from = Math.Max(_stamp, Math.Min(now, _blockedUntil));
        if (now > from) _tokens = Math.Min(Capacity, _tokens + (now - from) * _perTick);
        _stamp = now;
    }
}
