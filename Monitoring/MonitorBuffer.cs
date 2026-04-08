using System;
using System.Collections.Generic;
using System.Linq;
using MTTextClient.Core;
namespace MTTextClient.Monitoring;

/// <summary>
/// Time-series ring buffer of CoreStatusSnapshots received via UDP.
/// Thread-safe for concurrent write (from UDP callback) and read (from commands).
/// </summary>
public sealed class MonitorBuffer
{
    private readonly object _lock = new();
    private readonly CoreStatusSnapshot[] _buffer;
    private int _head;
    private int _count;

    public int Capacity { get; }

    public MonitorBuffer(int capacity = 1000)
    {
        Capacity = capacity;
        _buffer = new CoreStatusSnapshot[capacity];
    }

    public int Count
    {
        get { lock (_lock) { return _count; } }
    }

    public void Add(CoreStatusSnapshot snapshot)
    {
        lock (_lock)
        {
            _buffer[_head] = snapshot;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity)
            {
                _count++;
            }
        }
    }

    /// <summary>Returns the last N snapshots in chronological order (oldest first).</summary>
    public List<CoreStatusSnapshot> GetLast(int count)
    {
        lock (_lock)
        {
            int take = Math.Min(count, _count);
            var result = new List<CoreStatusSnapshot>(take);
            int start = (_head - take + Capacity) % Capacity;
            for (int i = 0; i < take; i++)
            {
                result.Add(_buffer[(start + i) % Capacity]);
            }
            return result;
        }
    }

    /// <summary>Returns the most recent snapshot, or null if empty.</summary>
    public CoreStatusSnapshot? GetLatest()
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                return null;
            }
            return _buffer[(_head - 1 + Capacity) % Capacity];
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _head = 0;
            _count = 0;
            Array.Clear(_buffer, 0, _buffer.Length);
        }
    }
}
