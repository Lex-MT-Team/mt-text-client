using System;
using System.Threading;

namespace MTTextClient.Core;

/// <summary>
/// MT-017: Per-connection token bucket rate limiter.
///
/// Enforces exchange API call rates to prevent IP bans.
/// Bybit conservative defaults: 120 calls/second bursting to 600.
///
/// Thread-safe. All operations are lock-free via Interlocked.
/// </summary>
public sealed class RateLimiter
{
    private readonly int _capacity;          // max burst tokens
    private readonly int _refillPerSecond;   // tokens added per second
    private readonly string _name;

    private double _tokens;                  // current token count (guarded by _lock)
    private long   _lastRefillTicks;         // Environment.TickCount64 at last refill
    private readonly object _lock = new();

    private long _totalAllowed;
    private long _totalThrottled;

    public string Name           => _name;
    public long   TotalAllowed   => Interlocked.Read(ref _totalAllowed);
    public long   TotalThrottled => Interlocked.Read(ref _totalThrottled);

    public RateLimiter(string name, int capacity = 600, int refillPerSecond = 120)
    {
        _name            = name;
        _capacity        = capacity;
        _refillPerSecond = refillPerSecond;
        _tokens          = capacity;           // start full
        _lastRefillTicks = Environment.TickCount64;
    }

    /// <summary>
    /// Attempt to consume one token.
    /// Returns true if the call is allowed; false if rate limit exceeded.
    /// When false, the caller should back off and retry after <see cref="RetryAfterMs"/>.
    /// </summary>
    public bool TryConsume()
    {
        lock (_lock)
        {
            Refill();

            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                Interlocked.Increment(ref _totalAllowed);
                return true;
            }
        }

        Interlocked.Increment(ref _totalThrottled);
        return false;
    }

    /// <summary>
    /// Blocking consume: waits until a token is available, up to <paramref name="maxWaitMs"/>.
    /// Returns false if the wait expired.
    /// </summary>
    public bool ConsumeBlocking(int maxWaitMs = 500)
    {
        long deadline = Environment.TickCount64 + maxWaitMs;
        while (Environment.TickCount64 < deadline)
        {
            if (TryConsume()) return true;
            Thread.Sleep(Math.Max(1, 1000 / _refillPerSecond));
        }
        return false;
    }

    /// <summary>Approximate ms until next token is available.</summary>
    public int RetryAfterMs
    {
        get
        {
            lock (_lock)
            {
                if (_tokens >= 1.0) return 0;
                double deficit = 1.0 - _tokens;
                return (int)Math.Ceiling(deficit / _refillPerSecond * 1000);
            }
        }
    }

    /// <summary>Current token count (informational).</summary>
    public double CurrentTokens
    {
        get { lock (_lock) { Refill(); return _tokens; } }
    }

    // Refill tokens based on elapsed time since last call. Must be called inside _lock.
    private void Refill()
    {
        long now     = Environment.TickCount64;
        long elapsed = now - _lastRefillTicks;
        if (elapsed <= 0) return;

        double added = elapsed * _refillPerSecond / 1000.0;
        _tokens = Math.Min(_capacity, _tokens + added);
        _lastRefillTicks = now;
    }
}
