using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MTTextClient.Core;

/// <summary>
/// Single source of truth for per-profile connection lifecycle state.
///
/// Centralises connection state into one publish/subscribe channel that:
///   • carries the full lifecycle including the in-between
///     <see cref="ConnectionState.Connecting"/> and
///     <see cref="ConnectionState.Reconnecting"/> states,
///   • can be subscribed to without holding a CoreConnection reference,
///   • snapshots the current state at any time so tests don't need to
///     race the events.
///
/// The existing <c>OnConnected</c> / <c>OnDisconnected</c> events on
/// <see cref="CoreConnection"/> push state changes into this observable.
/// Tools that currently query <c>CoreConnection.IsConnected</c> directly
/// (mt_status, mt_connection_health, per-tool readiness checks) can be
/// migrated to subscribe here in a follow-up — that's a behavioural
/// refactor with its own risk envelope.
/// </summary>
public sealed class ConnectionStateObservable
{
    /// <summary>
    /// Lifecycle phases a single connection passes through. Consumers that
    /// only see "connected vs. not" lose the initial-handshake and
    /// reconnect-backoff windows; tools that diagnose flap behaviour need
    /// to distinguish those.
    /// </summary>
    public enum ConnectionState
    {
        /// <summary>No connection attempt active. Initial state.</summary>
        Disconnected,
        /// <summary>UDP handshake in flight; auth not yet confirmed.</summary>
        Connecting,
        /// <summary>Auth complete; bidirectional traffic flowing.</summary>
        Connected,
        /// <summary>Was Connected; transport broke; reconnect backoff active.</summary>
        Reconnecting
    }

    /// <summary>
    /// Snapshot record describing one observed transition. The
    /// <see cref="At"/> timestamp lets subscribers correlate against
    /// MTCore logs without their own clock.
    /// </summary>
    public sealed record StateEvent(string Profile, ConnectionState State, DateTime At);

    private readonly ConcurrentDictionary<string, ConnectionState> _current =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fires whenever any profile's state changes. Subscribers should
    /// finish their work quickly — the event is invoked on whichever
    /// thread published the change, typically the LiteNetLib pump.
    /// </summary>
    public event Action<StateEvent>? OnStateChanged;

    /// <summary>
    /// Record a transition. Idempotent: emitting the same state twice
    /// in a row is a no-op (no event fires), so callers can publish
    /// "Connected" on every heartbeat without spamming subscribers.
    /// </summary>
    public void Publish(string profile, ConnectionState state)
    {
        if (string.IsNullOrEmpty(profile)) return;
        bool changed = !_current.TryGetValue(profile, out var prev) || prev != state;
        _current[profile] = state;
        if (changed)
        {
            OnStateChanged?.Invoke(new StateEvent(profile, state, DateTime.UtcNow));
        }
    }

    /// <summary>
    /// Point-in-time state for one profile. Returns
    /// <see cref="ConnectionState.Disconnected"/> if the profile has
    /// never been observed (no event ever published for it).
    /// </summary>
    public ConnectionState Snapshot(string profile)
    {
        if (string.IsNullOrEmpty(profile)) return ConnectionState.Disconnected;
        return _current.TryGetValue(profile, out var s) ? s : ConnectionState.Disconnected;
    }

    /// <summary>
    /// Read-only copy of every profile's current state. Subscribers use
    /// this on first connect to seed their view rather than waiting for
    /// the next transition.
    /// </summary>
    public IReadOnlyDictionary<string, ConnectionState> SnapshotAll() =>
        _current.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
}
