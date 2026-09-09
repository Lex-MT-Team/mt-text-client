using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MTShared.Network;
using MTShared.Structs;
using MTShared.Types;
namespace MTTextClient.Core;

/// <summary>
/// In-memory store for Trading Performance data — the metrics MTCore's
/// performance-filter breakers act on.
/// Thread-safe. Receives updates via TRADING_PERFORMANCE_SUBSCRIBE.
///
/// Push Events Handled:
///   TRADING_PERFORMANCE_RESULT → TradingPerformanceListData
///
/// Since MTCore 0.7.25589 a drop is either a full snapshot (isSnapshot) or a
/// delta: <c>metricChanges</c> upserts keys, <c>deletedKeys</c> drops them.
/// Each entry is keyed by (marketType, symbol, algorithmId) and carries one
/// metrics tuple per <see cref="TradingPerformanceTimeFrame"/>.
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

        // A snapshot replaces everything; deltas merge.
        if (data.isSnapshot)
        {
            _entries.Clear();
        }

        if (data.metricChanges != null)
        {
            for (int i = 0; i < data.metricChanges.Count; i++)
            {
                TradingPerformanceMetricData metric = data.metricChanges[i];
                if (metric == null)
                {
                    continue;
                }
                TradingPerformanceSnapshot snapshot = CreateSnapshot(metric);
                _entries[BuildKey(metric.key)] = snapshot;
                OnPerformanceUpdated?.Invoke(snapshot);
            }
        }

        if (data.deletedKeys != null)
        {
            for (int i = 0; i < data.deletedKeys.Count; i++)
            {
                _entries.TryRemove(BuildKey(data.deletedKeys[i]), out _);
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

    private static TradingPerformanceSnapshot CreateSnapshot(TradingPerformanceMetricData metric)
    {
        var metrics = new Dictionary<TradingPerformanceTimeFrame, TradingPerformanceMetricsSnapshot>();
        TradingPerformanceMetrics[]? wire = metric.metrics;
        if (wire != null)
        {
            foreach (TradingPerformanceTimeFrame tf in TradingPerformanceTimeFrames.AllValues)
            {
                // The wire array is sized by the sender: a core built against a
                // different timeframe set may send fewer entries than we know.
                int idx = TradingPerformanceTimeFrames.GetIndex(tf);
                if (idx < 0 || idx >= wire.Length)
                {
                    continue;
                }
                TradingPerformanceMetrics m = wire[idx];
                metrics[tf] = new TradingPerformanceMetricsSnapshot(
                    m.total, m.priceDelta, m.profitFactor, m.profitTotal, m.lossTotal);
            }
        }

        return new TradingPerformanceSnapshot
        {
            MarketType = (MarketType)metric.key.marketType,
            Symbol = metric.key.symbol ?? "",
            AlgorithmId = metric.key.algorithmId,
            StartTime = metric.startTime,
            Comment = metric.comment ?? "",
            Metrics = metrics,
            Timestamp = DateTime.UtcNow
        };
    }
}

/// <summary>One timeframe's performance metrics (see TradingPerformanceMetrics).</summary>
public readonly record struct TradingPerformanceMetricsSnapshot(
    double Total,
    float PriceDelta,
    float ProfitFactor,
    double ProfitTotal,
    double LossTotal)
{
    /// <summary>True when no trade contributed to this timeframe.</summary>
    public bool IsEmpty => Total == 0d && ProfitTotal == 0d && LossTotal == 0d;
}

/// <summary>Snapshot of a single trading performance entry for display.</summary>
public sealed class TradingPerformanceSnapshot
{
    public MarketType MarketType { get; init; }
    public string Symbol { get; init; } = "";
    public long AlgorithmId { get; init; }
    public long StartTime { get; init; }
    public string Comment { get; init; } = "";

    /// <summary>Metrics per timeframe, as sent by the core.</summary>
    public IReadOnlyDictionary<TradingPerformanceTimeFrame, TradingPerformanceMetricsSnapshot> Metrics { get; init; }
        = new Dictionary<TradingPerformanceTimeFrame, TradingPerformanceMetricsSnapshot>();

    public DateTime Timestamp { get; init; }
}
