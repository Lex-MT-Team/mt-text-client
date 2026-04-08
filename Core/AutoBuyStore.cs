#nullable disable
namespace MTTextClient.Core;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

public sealed class AutoBuyEntry
{
    public string ProfileName { get; }
    public string ActionType { get; }
    public string RawJson { get; }
    public DateTime ReceivedAtUtc { get; }

    public AutoBuyEntry(string profileName, string actionType, string rawJson)
    {
        ProfileName = profileName;
        ActionType = actionType;
        RawJson = rawJson;
        ReceivedAtUtc = DateTime.UtcNow;
    }
}

public sealed class AutoBuyStore
{
    private readonly ConcurrentQueue<AutoBuyEntry> _entries = new ConcurrentQueue<AutoBuyEntry>();
    private const int MaxEntries = 200;

    public void Add(AutoBuyEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries)
        {
            _entries.TryDequeue(out _);
        }
    }

    public IReadOnlyList<AutoBuyEntry> GetAll()
    {
        return _entries.ToArray();
    }

    public IReadOnlyList<AutoBuyEntry> GetRecent(int count)
    {
        AutoBuyEntry[] all = _entries.ToArray();
        return all.Skip(Math.Max(0, all.Length - count)).ToArray();
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }

    public int Count => _entries.Count;
}
