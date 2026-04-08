using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MTShared.Network;
using MTShared.Structs;
using MTShared.Types;
namespace MTTextClient.Core;

/// <summary>
/// In-memory store for TPSL (Take Profit / Stop Loss) position data.
/// Thread-safe. Receives updates via ALGORITHMS_TP_SL_SUBSCRIBE.
///
/// Push Events Handled:
///   ALGORITHMS_TP_SL_UPDATE (113) → TPSLInfoListData — list of TPSL positions
/// </summary>
public sealed class TPSLStore
{
    private readonly ConcurrentDictionary<long, TPSLPositionSnapshot> _positions = new();
    private volatile bool _hasData;

    public bool HasData => _hasData;
    public int Count => _positions.Count;
    public DateTime LastUpdate { get; private set; }

    public event Action<TPSLPositionSnapshot>? OnPositionUpdated;

    /// <summary>
    /// Process incoming TPSL data from subscription callback.
    /// </summary>
    public void ProcessData(TPSLInfoListData data)
    {
        if (data == null)
        {
            return;
        }

        // If this is the initial full list (not an update), clear existing data
        if (!data.isUpdate)
        {
            _positions.Clear();
        }

        if (data.infoData != null)
        {
            for (int i = 0; i < data.infoData.Count; i++)
            {
                TPSLInfoData info = data.infoData[i];
                TPSLPositionSnapshot snapshot = CreateSnapshot(info);
                _positions[info.id] = snapshot;
                OnPositionUpdated?.Invoke(snapshot);
            }
        }

        _hasData = true;
        LastUpdate = DateTime.UtcNow;
    }

    public IReadOnlyList<TPSLPositionSnapshot> GetAll()
    {
        var list = new List<TPSLPositionSnapshot>(_positions.Values);
        list.Sort((a, b) => a.Id.CompareTo(b.Id));
        return list;
    }

    public TPSLPositionSnapshot? GetById(long id)
    {
        _positions.TryGetValue(id, out TPSLPositionSnapshot? snapshot);
        return snapshot;
    }

    public void Clear()
    {
        _positions.Clear();
        _hasData = false;
    }

    private static TPSLPositionSnapshot CreateSnapshot(TPSLInfoData info)
    {
        return new TPSLPositionSnapshot
        {
            Id = info.id,
            Symbol = info.symbol ?? "",
            MarketType = info.marketType,
            Side = info.side,
            Qty = info.qty,
            EntryPrice = info.entryPrice,
            OpenTime = info.openTime,
            TakeProfitEnabled = info.takeProfitSettings.isOn,
            TakeProfitPercent = info.takeProfitSettings.percentage,
            TakeProfitStatus = info.takeProfitSettings.status,
            StopLossEnabled = info.stopLossSettings.isOn,
            StopLossPercent = info.stopLossSettings.percentage,
            StopLossStatus = info.stopLossSettings.status,
            TrailingEnabled = info.stopLossSettings.tralingIsOn,
            TrailingSpread = info.stopLossSettings.trailingSpread,
            SplitCount = info.tpslSplitCount,
            SplitPercentage = info.tpslSplitPercentage,
            IsRunning = info.IsRunning,
            IsEmulated = info.isEmulated,
            UseJoinKey = info.useJoinKey,
            JoinKey = info.joinKey ?? "",
            Timestamp = DateTime.UtcNow
        };
    }
}

/// <summary>Snapshot of a single TPSL position for display.</summary>
public sealed class TPSLPositionSnapshot
{
    public long Id { get; init; }
    public string Symbol { get; init; } = "";
    public MarketType MarketType { get; init; }
    public OrderSideType Side { get; init; }
    public double Qty { get; init; }
    public double EntryPrice { get; init; }
    public long OpenTime { get; init; }
    public bool TakeProfitEnabled { get; init; }
    public float TakeProfitPercent { get; init; }
    public TPSLStatus TakeProfitStatus { get; init; }
    public bool StopLossEnabled { get; init; }
    public float StopLossPercent { get; init; }
    public TPSLStatus StopLossStatus { get; init; }
    public bool TrailingEnabled { get; init; }
    public float TrailingSpread { get; init; }
    public byte SplitCount { get; init; }
    public float SplitPercentage { get; init; }
    public bool IsRunning { get; init; }
    public bool IsEmulated { get; init; }
    public bool UseJoinKey { get; init; }
    public string JoinKey { get; init; } = "";
    public DateTime Timestamp { get; init; }
}
