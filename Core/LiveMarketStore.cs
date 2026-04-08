#nullable disable
namespace MTTextClient.Core;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

public sealed class LiveMarketEntry
{
    public string Symbol { get; }
    public string MarketType { get; }
    public string MetricsJson { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public LiveMarketEntry(string symbol, string marketType, string metricsJson)
    {
        Symbol = symbol;
        MarketType = marketType;
        MetricsJson = metricsJson;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}

public sealed class LiveMarketStore
{
    private readonly ConcurrentDictionary<string, LiveMarketEntry> _entries =
        new ConcurrentDictionary<string, LiveMarketEntry>(StringComparer.OrdinalIgnoreCase);

    public void Update(string key, LiveMarketEntry entry)
    {
        _entries.AddOrUpdate(key, entry, (_, existing) =>
        {
            existing.MetricsJson = entry.MetricsJson;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            return existing;
        });
    }

    public bool TryGet(string key, out LiveMarketEntry entry)
    {
        return _entries.TryGetValue(key, out entry);
    }

    public IReadOnlyList<LiveMarketEntry> GetAll()
    {
        return _entries.Values.ToArray();
    }

    public void Clear()
    {
        _entries.Clear();
    }

    public int Count => _entries.Count;
}
