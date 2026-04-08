using System;
namespace MTTextClient.Core;

/// <summary>
/// Per-connection health record. Tracks connection quality, error rate, and reconnect history.
/// Attached to each profile in <see cref="ConnectionManager"/> and updated live via event callbacks.
/// Enables agents to make informed routing and escalation decisions without polling MTCore.
/// </summary>
public sealed class ConnectionHealthRecord
{
    /// <summary>Profile name (connection identifier).</summary>
    public string ProfileName { get; }

    /// <summary>Whether the UDP connection is currently established.</summary>
    public bool IsConnected { get; internal set; }

    /// <summary>
    /// UDP round-trip peer latency in milliseconds, sampled from MTCore status broadcasts.
    /// Updated every MTCore status push (~5-10 seconds). 0 if no data yet.
    /// </summary>
    public double LatencyMs { get; internal set; }

    /// <summary>
    /// Number of errors observed on this connection since last successful reconnect.
    /// Resets to 0 on successful reconnect. Used by <see cref="IsHealthy"/>.
    /// </summary>
    public int ErrorCount { get; internal set; }

    /// <summary>Total number of auto-reconnect attempts since this record was created.</summary>
    public int ReconnectCount { get; internal set; }

    /// <summary>UTC timestamp of the last successful connection or MTCore message received.</summary>
    public DateTime LastSeen { get; internal set; } = DateTime.UtcNow;

    /// <summary>
    /// Current backoff delay before the next auto-reconnect attempt.
    /// Grows exponentially with <see cref="ReconnectCount"/>, capped at 5 minutes.
    /// </summary>
    public TimeSpan ReconnectBackoff { get; internal set; } = TimeSpan.FromSeconds(1);

    /// <summary>Last error message received, or null if no errors.</summary>
    public string? LastError { get; internal set; }

    /// <summary>
    /// Connection is healthy when: connected, low latency (&lt;500ms), few errors (&lt;5),
    /// and a message was received within the last 60 seconds.
    /// </summary>
    public bool IsHealthy =>
        IsConnected &&
        LatencyMs < 500 &&
        ErrorCount < 5 &&
        (DateTime.UtcNow - LastSeen) < TimeSpan.FromSeconds(60);

    internal ConnectionHealthRecord(string profileName)
    {
        ProfileName = profileName ?? throw new ArgumentNullException(nameof(profileName));
    }

    /// <summary>
    /// Increase reconnect backoff exponentially (2^n seconds, max 300s).
    /// Called before each reconnect attempt.
    /// </summary>
    internal void IncreaseBackoff()
    {
        ReconnectCount++;
        double nextSeconds = Math.Min(300.0, Math.Pow(2.0, Math.Min(ReconnectCount, 8)));
        ReconnectBackoff = TimeSpan.FromSeconds(nextSeconds);
    }

    /// <summary>
    /// Reset backoff and error count after a successful reconnect.
    /// </summary>
    internal void ResetBackoff()
    {
        ReconnectBackoff = TimeSpan.FromSeconds(1);
        ErrorCount = 0;
    }
}
