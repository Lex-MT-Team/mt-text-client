using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MTShared.Network;
using MTShared.Structs;
using MTShared.Types;
namespace MTTextClient.Core;

/// <summary>
/// In-memory store for Trading Performance data.
/// Thread-safe. Receives updates via TRADING_PERFORMANCE_SUBSCRIBE.
///
/// Push Events Handled:
///   TRADING_PERFORMANCE_RESULT (126) → TradingPerformanceListData
/// </summary>
public sealed class TradingPerformanceStore
{
    private readonly ConcurrentDictionary<string, TradingPerformanceSnapshot> _entries = new();
    private volatile bool _hasData;

    public bool HasData => _hasData;
    public int Count => _entries.Count;
    public DateTime LastUpdate { get; private set; }

    public event Action<TradingPerformanceSnapshot>? OnPerformanceUpdated;

    /// <summary>
    /// Process incoming trading performance data from subscription callback.
    /// </summary>
    public void ProcessData(TradingPerformanceListData data)
    {
        if (data == null)
        {
            return;
        }

        // 0.7.25267: a snapshot replaces everything; otherwise the payload is a
        // delta of metric changes plus a set of removed keys.
        if (data.isSnapshot)
        {
            _entries.Clear();
        }

        if (data.deletedKeys != null)
        {
            foreach (TradingPerformanceKey removed in data.deletedKeys)
            {
                _entries.TryRemove(BuildKey(removed), out _);
            }
        }

        if (data.metricChanges != null)
        {
            for (int i = 0; i < data.metricChanges.Count; i++)
            {
                TradingPerformanceMetricData perf = data.metricChanges[i];
                string key = BuildKey(perf.key);
                TradingPerformanceSnapshot snapshot = CreateSnapshot(perf);
                _entries[key] = snapshot;
                OnPerformanceUpdated?.Invoke(snapshot);
            }
        }

        _hasData = true;
        LastUpdate = DateTime.UtcNow;
    }

    public IReadOnlyList<TradingPerformanceSnapshot> GetAll()
    {
        var list = new List<TradingPerformanceSnapshot>(_entries.Values);
        list.Sort((a, b) => string.Compare(a.Symbol, b.Symbol, StringComparison.Ordinal));
        return list;
    }

    public void Clear()
    {
        _entries.Clear();
        _hasData = false;
    }

    private static string BuildKey(TradingPerformanceKey key)
    {
        return $"{key.marketType}:{key.symbol}:{key.algorithmId}";
    }

    private static TradingPerformanceSnapshot CreateSnapshot(TradingPerformanceMetricData perf)
    {
        // 0.7.25267 replaced the parallel per-timeframe lists (totals, priceDeltas,
        // …) with a single metrics[] array (one TradingPerformanceMetrics struct per
        // timeframe). The per-list counts now all reflect that array's length.
        int metricCount = perf.metrics != null ? perf.metrics.Length : 0;
        return new TradingPerformanceSnapshot
        {
            MarketType = (MarketType)perf.key.marketType,
            Symbol = perf.key.symbol ?? "",
            AlgorithmId = perf.key.algorithmId,
            StartTime = perf.startTime,
            Comment = perf.comment ?? "",
            TotalsCount = metricCount,
            PriceDeltasCount = metricCount,
            ProfitFactorsCount = metricCount,
            ProfitTotalsCount = metricCount,
            LossTotalsCount = metricCount,
            KeyGroup = TradingPerformanceKeyGroup.UNKNOWN,
            Timestamp = DateTime.UtcNow
        };
    }
}

/// <summary>Snapshot of a single trading performance entry for display.</summary>
public sealed class TradingPerformanceSnapshot
{
    public MarketType MarketType { get; init; }
    public string Symbol { get; init; } = "";
    public long AlgorithmId { get; init; }
    public long StartTime { get; init; }
    public string Comment { get; init; } = "";
    public int TotalsCount { get; init; }
    public int PriceDeltasCount { get; init; }
    public int ProfitFactorsCount { get; init; }
    public int ProfitTotalsCount { get; init; }
    public int LossTotalsCount { get; init; }
    public TradingPerformanceKeyGroup KeyGroup { get; init; }
    public DateTime Timestamp { get; init; }
}
