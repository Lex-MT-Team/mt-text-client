using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MTShared.Types;
namespace MTTextClient.Core;

/// <summary>
/// Manages multiple simultaneous connections to MT-Core instances.
/// Each connection is independent with its own UDPClient, AlgoStore, AccountStore,
/// CoreStatusStore, and ExchangeInfoStore.
/// One connection can be "active" (targeted by commands without explicit prefix).
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, CoreConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private string _activeConnectionName = string.Empty;
    private bool _disposed;

    // Consolidated polling pump — one thread for all connections
    private readonly ConnectionPump _pump;

    // Cached array for pump iteration (avoids LINQ allocation per tick)
    private CoreConnection[] _cachedArray = Array.Empty<CoreConnection>();
    private int _cachedVersion;
    private int _currentVersion;

    // MT-002: Persistent profiles — auto-reconnect when connection drops
    private readonly ConcurrentDictionary<string, ServerProfile> _persistentProfiles =
        new(StringComparer.OrdinalIgnoreCase);

    // MT-003: Per-connection health records — latency, error rate, reconnect history
    private readonly ConcurrentDictionary<string, ConnectionHealthRecord> _healthRecords =
        new(StringComparer.OrdinalIgnoreCase);

    // Cancellation for all pending reconnect tasks
    private readonly CancellationTokenSource _reconnectCts = new();

    // MT-016: cap concurrent reconnect attempts during fleet storms (e.g. 30 conns drop at once)
    private static readonly SemaphoreSlim _reconnectSemaphore = new(10, 10);

    // MT-015: fired when Core restarts (connectionId or serverStartTime changes on same connection)
    public event Action<CoreConnection>? OnCoreRestarted;

    public ConnectionManager()
    {
        _pump = new ConnectionPump(this);
        _pump.Start();
    }

    /// <summary>Name of the currently active connection.</summary>
    public string ActiveConnectionName
    {
        get => _activeConnectionName;
        set
        {
            if (!string.IsNullOrEmpty(value) && !_connections.ContainsKey(value))
            {
                throw new ArgumentException($"Connection '{value}' not found.");
            }

            _activeConnectionName = value;
        }
    }

    /// <summary>The active CoreConnection (or null if none active).</summary>
    public CoreConnection? ActiveConnection =>
        !string.IsNullOrEmpty(_activeConnectionName) && _connections.TryGetValue(_activeConnectionName, out CoreConnection? conn)
            ? conn
            : null;

    /// <summary>Number of active connections.</summary>
    public int Count => _connections.Count;

    /// <summary>Number of connected (online) connections.</summary>
    public int ConnectedCount
    {
        get
        {
            int count = 0;
            foreach (CoreConnection c in _connections.Values)
            {
                if (c.IsConnected)
                {
                    count++;
                }
            }
            return count;
        }
    }

    /// <summary>Whether any connection is active.</summary>
    public bool HasActiveConnection => ActiveConnection?.IsConnected == true;

    // Events — connection lifecycle
    public event Action<CoreConnection>? OnConnectionEstablished;
    public event Action<CoreConnection>? OnConnectionLost;
    public event Action<CoreConnection, string>? OnConnectionError;
    public event Action<CoreConnection, int>? OnAlgorithmsLoaded;

    // Events — Phase A data streams
    public event Action<CoreConnection>? OnCoreStatusReceived;
    public event Action<CoreConnection, int>? OnTradePairsLoaded;
    public event Action<CoreConnection>? OnAccountDataReceived;

    /// <summary>
    /// Connect to a server profile. Creates and starts a new CoreConnection.
    /// If this is the first connection, it becomes the active one automatically.
    /// </summary>
    public CoreConnection? Connect(ServerProfile profile)
    {
        if (_connections.ContainsKey(profile.Name))
        {
            CoreConnection? existing = _connections[profile.Name];
            if (existing.IsConnected)
            {
                OnConnectionError?.Invoke(existing, $"Already connected to '{profile.Name}'.");
                return existing;
            }
            // Remove stale connection
            Disconnect(profile.Name);
        }

        // MT-002/MT-003: register profile for auto-reconnect and init health record
        _persistentProfiles[profile.Name] = profile;
        ConnectionHealthRecord health = _healthRecords.GetOrAdd(profile.Name,
            n => new ConnectionHealthRecord(n));

        var conn = new CoreConnection(profile);

        // Wire connection lifecycle events
        conn.OnConnected += c =>
        {
            OnConnectionEstablished?.Invoke(c);
        };
        conn.OnDisconnected += c =>
        {
            OnConnectionLost?.Invoke(c);
            // If active connection was lost, try to pick another
            if (_activeConnectionName.Equals(c.Name, StringComparison.OrdinalIgnoreCase))
            {
                CoreConnection? next = null;
                foreach (CoreConnection x in _connections.Values)
                {
                    if (x.IsConnected && !x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        next = x;
                        break;
                    }
                }
                if (next != null)
                {
                    _activeConnectionName = next.Name;
                }
            }
        };
        conn.OnError += (c, msg) => OnConnectionError?.Invoke(c, msg);
        conn.OnAlgorithmsLoaded += (c, count) => OnAlgorithmsLoaded?.Invoke(c, count);

        // Wire Phase A data events
        conn.OnCoreStatusReceived += c => OnCoreStatusReceived?.Invoke(c);
        conn.OnTradePairsLoaded += (c, count) => OnTradePairsLoaded?.Invoke(c, count);
        conn.OnAccountDataReceived += c => OnAccountDataReceived?.Invoke(c);

        // MT-003: wire health metric updates
        conn.OnConnected += c =>
        {
            health.IsConnected = true;
            health.LastSeen = DateTime.UtcNow;
            health.ResetBackoff();
        };
        conn.OnDisconnected += c =>
        {
            health.IsConnected = false;
            // MT-002: schedule auto-reconnect with exponential backoff
            if (_persistentProfiles.TryGetValue(c.Name, out ServerProfile? savedProfile))
            {
                _ = ScheduleReconnectAsync(savedProfile, _reconnectCts.Token);
            }
        };
        conn.OnError += (c, msg) =>
        {
            health.ErrorCount++;
            health.LastError = msg;
        };
        conn.OnCoreStatusReceived += c =>
        {
            CoreStatusSnapshot? st = c.CoreStatusStore.GetStatus();
            if (st != null)
            {
                health.LatencyMs = st.AvgPeerLatencyMs;
                health.LastSeen = DateTime.UtcNow;
            }
        };
        // ISS-1 fix: update LastSeen on any data event, not just CoreStatus push.
        // Connections that respond to commands are clearly alive even if CoreStatus stops.
        conn.OnAlgorithmsLoaded += (c, _) => health.LastSeen = DateTime.UtcNow;
        conn.OnTradePairsLoaded += (c, _) => health.LastSeen = DateTime.UtcNow;
        conn.OnAccountDataReceived += c => health.LastSeen = DateTime.UtcNow;
        // MT-015: propagate Core restart notification from CoreConnection to manager
        conn.OnCoreRestarted += c => OnCoreRestarted?.Invoke(c);

        _connections[profile.Name] = conn;
        Interlocked.Increment(ref _currentVersion);

        bool success = conn.Connect();
        if (!success)
        {
            _connections.TryRemove(profile.Name, out _);
            return null;
        }

        // First connection becomes active automatically
        if (string.IsNullOrEmpty(_activeConnectionName))
        {
            _activeConnectionName = profile.Name;
        }

        return conn;
    }

    /// <summary>Disconnect a specific connection by profile name.</summary>
    public bool Disconnect(string profileName)
    {
        // MT-002: stop auto-reconnect for explicitly disconnected profiles
        _persistentProfiles.TryRemove(profileName, out _);

        if (_connections.TryRemove(profileName, out CoreConnection? conn))
        {
            Interlocked.Increment(ref _currentVersion);
            conn.Dispose();
            if (_activeConnectionName.Equals(profileName, StringComparison.OrdinalIgnoreCase))
            {
                CoreConnection? next2 = null;
                foreach (CoreConnection x in _connections.Values)
                {
                    if (x.IsConnected)
                    {
                        next2 = x;
                        break;
                    }
                }
                _activeConnectionName = next2?.Name ?? string.Empty;
            }
            return true;
        }
        return false;
    }

    /// <summary>Disconnect all connections.</summary>
    public void DisconnectAll()
    {
        foreach (string name in new List<string>(_connections.Keys))
        {
            Disconnect(name);
        }

        _activeConnectionName = string.Empty;
    }

    // ── Health Records (MT-003) ───────────────────────────────

    /// <summary>
    /// Returns health records for all tracked profiles (connected or reconnecting).
    /// Includes profiles that were connected and are currently in backoff.
    /// </summary>
    public IReadOnlyList<ConnectionHealthRecord> GetHealthRecords()
    {
        return new List<ConnectionHealthRecord>(_healthRecords.Values);
    }

    /// <summary>Returns the health record for a specific profile, or null if not tracked.</summary>
    public ConnectionHealthRecord? GetHealthRecord(string profileName)
    {
        _healthRecords.TryGetValue(profileName, out ConnectionHealthRecord? record);
        return record;
    }

    // ── Auto-Reconnect (MT-002) ───────────────────────────────

    /// <summary>
    /// Schedules a reconnect attempt for a profile after its current backoff delay.
    /// Called automatically when a persistent connection drops.
    /// </summary>
    private async Task ScheduleReconnectAsync(ServerProfile profile, CancellationToken ct)
    {
        if (!_healthRecords.TryGetValue(profile.Name, out ConnectionHealthRecord? health))
        {
            return;
        }

        try
        {
            health.IncreaseBackoff();
            await Task.Delay(health.ReconnectBackoff, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return; // Dispose() was called — stop reconnecting
        }

        // If explicitly disconnected during backoff, don't reconnect
        if (!_persistentProfiles.ContainsKey(profile.Name) || ct.IsCancellationRequested)
        {
            return;
        }

        // MT-016: throttle concurrent reconnects to avoid LiteNetLib socket storm
        // when many connections drop simultaneously (e.g. network hiccup hitting 30 conns)
        try
        {
            await _reconnectSemaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            // Re-check — may have been explicitly disconnected while waiting for semaphore
            if (!_persistentProfiles.ContainsKey(profile.Name) || ct.IsCancellationRequested)
            {
                return;
            }
            Connect(profile);
        }
        finally
        {
            _reconnectSemaphore.Release();
        }
    }

    /// <summary>Get a specific connection by profile name.</summary>
    public CoreConnection? Get(string profileName)
    {
        _connections.TryGetValue(profileName, out CoreConnection? conn);
        return conn;
    }

    /// <summary>Get all connections as a cached array (for pump hot path — no allocation).</summary>
    public CoreConnection[] GetAllArray()
    {
        int ver = _currentVersion;
        if (ver != _cachedVersion)
        {
            ICollection<CoreConnection>? vals = _connections.Values;
            CoreConnection[]? arr = new CoreConnection[vals.Count];
            vals.CopyTo(arr, 0);
            _cachedArray = arr;
            _cachedVersion = ver;
        }
        return _cachedArray;
    }

    /// <summary>Get all connections.</summary>
    public IReadOnlyList<CoreConnection> GetAll()
    {
        var list = new List<CoreConnection>(_connections.Values);
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return list;
    }

    /// <summary>
    /// Switch the active connection to the given profile name.
    /// Returns the new active connection, or null if not found.
    /// </summary>
    public CoreConnection? SwitchTo(string profileName)
    {
        if (_connections.TryGetValue(profileName, out CoreConnection? conn))
        {
            _activeConnectionName = profileName;
            return conn;
        }
        return null;
    }

    /// <summary>
    /// Resolve which connection to use for a command.
    /// If an explicit name is provided, use that. Otherwise use the active connection.
    /// </summary>
    public CoreConnection? Resolve(string? explicitName = null)
    {
        if (!string.IsNullOrEmpty(explicitName))
        {
            _connections.TryGetValue(explicitName, out CoreConnection? conn);
            return conn;
        }
        return ActiveConnection;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reconnectCts.Cancel();
        _reconnectCts.Dispose();
        _pump.Dispose();
        DisconnectAll();
    }
}
