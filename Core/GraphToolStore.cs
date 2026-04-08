#nullable disable
namespace MTTextClient.Core;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

public sealed class GraphToolEntry
{
    public string ProfileName { get; }
    public string EventType { get; }
    public string RawJson { get; }
    public DateTime ReceivedAtUtc { get; }

    public GraphToolEntry(string profileName, string eventType, string rawJson)
    {
        ProfileName = profileName;
        EventType = eventType;
        RawJson = rawJson;
        ReceivedAtUtc = DateTime.UtcNow;
    }
}

public sealed class GraphToolStore
{
    private readonly ConcurrentQueue<GraphToolEntry> _entries = new ConcurrentQueue<GraphToolEntry>();
    private const int MaxEntries = 200;

    public void Add(GraphToolEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries)
        {
            _entries.TryDequeue(out _);
        }
    }

    public IReadOnlyList<GraphToolEntry> GetAll()
    {
        return _entries.ToArray();
    }

    public IReadOnlyList<GraphToolEntry> GetRecent(int count)
    {
        GraphToolEntry[] all = _entries.ToArray();
        return all.Skip(Math.Max(0, all.Length - count)).ToArray();
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }

    public int Count => _entries.Count;
}
