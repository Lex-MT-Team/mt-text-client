#nullable disable
namespace MTTextClient.Core;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

public sealed class TriggerEntry
{
    public string ProfileName { get; }
    public string EventType { get; }
    public string RawJson { get; }
    public DateTime ReceivedAtUtc { get; }

    public TriggerEntry(string profileName, string eventType, string rawJson)
    {
        ProfileName = profileName;
        EventType = eventType;
        RawJson = rawJson;
        ReceivedAtUtc = DateTime.UtcNow;
    }
}

public sealed class TriggerStore
{
    private readonly ConcurrentQueue<TriggerEntry> _entries = new ConcurrentQueue<TriggerEntry>();
    private const int MaxEntries = 200;

    public void Add(TriggerEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries)
        {
            _entries.TryDequeue(out _);
        }
    }

    public IReadOnlyList<TriggerEntry> GetAll()
    {
        return _entries.ToArray();
    }

    public IReadOnlyList<TriggerEntry> GetRecent(int count)
    {
        TriggerEntry[] all = _entries.ToArray();
        return all.Skip(Math.Max(0, all.Length - count)).ToArray();
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }

    public int Count => _entries.Count;
}
