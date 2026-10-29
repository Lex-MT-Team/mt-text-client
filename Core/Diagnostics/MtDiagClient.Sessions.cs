using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MTTextClient.Core;
using Newtonsoft.Json;

namespace MTTextClient.Core.Diagnostics;

public sealed partial class MtDiagClient
{
    private const int MaximumSessions = 128;
    private static readonly TimeSpan SessionIdleLease = TimeSpan.FromSeconds(20);
    private readonly object _sessionsGate = new();
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private Timer? _expiry;
    private bool _disposed;

    private SessionEntry? Acquire(ServerProfile profile, DiagnosticRoute route, DiagnosticIdentityEvidence evidence, string seed, bool pooled)
    {
        // Include every identity/validation input. Keys never leave memory or appear in errors.
        string key = pooled ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(
            new { profile.Name, route, profile.ClientToken, seed, evidence })))) : string.Empty;
        lock (_sessionsGate)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            ExpireLocked();
            if (!pooled) return new SessionEntry(key) { Users = 1 };
            if (!_sessions.TryGetValue(key, out SessionEntry? entry))
            {
                if (_sessions.Count >= MaximumSessions) return null;
                entry = new SessionEntry(key);
                _sessions.Add(key, entry);
            }
            entry.Users++;
            _expiry ??= new Timer(_ => Expire(), null, SessionIdleLease, SessionIdleLease);
            return entry;
        }
    }

    private void Release(SessionEntry entry, bool pooled)
    {
        lock (_sessionsGate)
        {
            entry.Users--;
            entry.LastUsed = Environment.TickCount64;
            if (entry.Users == 0 && (!pooled || _disposed || entry.Session is null))
            {
                if (pooled) _sessions.Remove(entry.Key);
                entry.Reset();
                entry.Gate.Dispose();
            }
            StopEmptyTimer();
        }
    }

    private void Expire()
    {
        lock (_sessionsGate) ExpireLocked();
    }

    private void ExpireLocked()
    {
        foreach (SessionEntry entry in _sessions.Values.Where(e => e.Users == 0 && Environment.TickCount64 - e.LastUsed >= SessionIdleLease.TotalMilliseconds).ToArray())
        {
            _sessions.Remove(entry.Key);
            entry.Reset();
            entry.Gate.Dispose();
        }
        StopEmptyTimer();
    }

    private void StopEmptyTimer()
    {
        if (_sessions.Count != 0) return;
        _expiry?.Dispose();
        _expiry = null;
    }

    /// <summary>Cancels outstanding calls and closes idle diagnostic connections. In-flight calls close on unwind.</summary>
    public void Dispose()
    {
        // Cancel outside the map lock: cancellation continuations may release entries.
        lock (_sessionsGate) { if (_disposed) return; _disposed = true; }
        _lifetime.Cancel();
        lock (_sessionsGate)
        {
            _expiry?.Dispose();
            _expiry = null;
            foreach (SessionEntry entry in _sessions.Values.Where(e => e.Users == 0).ToArray())
            {
                _sessions.Remove(entry.Key);
                entry.Reset();
                entry.Gate.Dispose();
            }
        }
        // Retain the cancelled token source so a racing/subsequent caller receives Cancelled, not ObjectDisposedException.
    }

    private sealed class SessionEntry
    {
        public SessionEntry(string key) => Key = key;
        public string Key { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users;
        public long LastUsed = Environment.TickCount64;
        public NativeDiagnosticSession? Session;
        public DiagnosticResult? Capabilities;
        public void Reset() { Session?.Dispose(); Session = null; Capabilities = null; }
    }
}
