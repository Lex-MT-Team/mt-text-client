using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MTShared;
using MTShared.Network;
using MTShared.Types;
namespace MTTextClient.Core;

/// <summary>
/// In-memory store for account data received via UDS (User Data Stream) subscription.
/// Thread-safe. Receives push events for balances, orders, positions, and account info.
///
/// UDS Push Events Handled:
///   UDS_ACCOUNT_INFO_RESULT  (24) → AccountInfoData  — full account snapshot
///   UDS_BALANCE_RESULT       (25) → BalanceData       — single balance update
///   UDS_BALANCE_LIST_RESULT  (26) → BalanceListData   — batch balance update
///   UDS_EXECUTION_RESULT     (27) → OrderData         — trade execution (fill)
///   UDS_LIST_STATUS_RESULT   (28) → UDSListStatusData — OCO/list status
///   UDS_ORDER_LIST_RESULT    (29) → OrderListData     — full order snapshot
///   UDS_ORDER_UPDATE_RESULT  (30) → OrderData         — single order update
///   UDS_POSITIONS_RESULT     (31) → PositionListData  — full positions snapshot
/// </summary>
public sealed class AccountStore
{
    // ── Balances ──────────────────────────────────────────────
    // Key: "ASSET:MARKET" (e.g. "USDT:FUTURES")
    private readonly ConcurrentDictionary<string, BalanceSnapshot> _balances = new(StringComparer.OrdinalIgnoreCase);

    // ── Orders ───────────────────────────────────────────────
    // Key: clientOrderId
    private readonly ConcurrentDictionary<string, OrderSnapshot> _orders = new();

    // ── Positions ────────────────────────────────────────────
    // Key: "{symbol}:{positionSide}" (e.g. "BTCUSDT:BOTH")
    private readonly ConcurrentDictionary<string, PositionSnapshot> _positions = new();

    // ── Account Info ─────────────────────────────────────────
    private volatile AccountInfoSnapshot? _accountInfo;

    // ── Executions (recent fills) ────────────────────────────
    // Ring buffer of recent executions for display
    private readonly ConcurrentQueue<ExecutionSnapshot> _recentExecutions = new();
    private const int MAX_RECENT_EXECUTIONS = 100;

    // ── Timestamps ───────────────────────────────────────────
    public DateTime LastBalanceUpdate { get; private set; }
    public DateTime LastOrderUpdate { get; private set; }
    public DateTime LastPositionUpdate { get; private set; }
    public DateTime LastAccountInfoUpdate { get; private set; }

    // ── Events ───────────────────────────────────────────────
    public event Action<string>? OnBalanceChanged;
    public event Action<string>? OnOrderChanged;
    public event Action? OnPositionsChanged;
    public event Action? OnAccountInfoChanged;
    public event Action<ExecutionSnapshot>? OnExecution;

    /// <summary>
    /// Process incoming UDS data from subscription callback.
    /// Called by CoreConnection on the ProcessEventData timer thread.
    /// </summary>
    public void ProcessData(NetworkMessageType msgType, NetworkData data)
    {
        switch (msgType)
        {
            case NetworkMessageType.UDS_ACCOUNT_INFO_RESULT:
                ProcessAccountInfo(data);
                break;

            case NetworkMessageType.UDS_BALANCE_RESULT:
                ProcessBalanceUpdate(data);
                break;

            case NetworkMessageType.UDS_BALANCE_LIST_RESULT:
                ProcessBalanceList(data);
                break;

            case NetworkMessageType.UDS_ORDER_LIST_RESULT:
                ProcessOrderList(data);
                break;

            case NetworkMessageType.UDS_ORDER_UPDATE_RESULT:
                ProcessOrderUpdate(data);
                break;

            case NetworkMessageType.UDS_EXECUTION_RESULT:
                ProcessExecution(data);
                break;

            case NetworkMessageType.UDS_POSITIONS_RESULT:
                ProcessPositions(data);
                break;

            case NetworkMessageType.UDS_LIST_STATUS_RESULT:
                // OCO/contingent order status — logged but not stored yet
                break;
        }
    }

    // ── Account Info ─────────────────────────────────────────

    private void ProcessAccountInfo(NetworkData data)
    {
        if (data is not AccountInfoData info)
        {
            return;
        }

        var snapshot = new AccountInfoSnapshot
        {
            MarketType = info.marketType,
            CanTrade = info.canTrade,
            PositionMode = info.positionModeType,
            MultiAssetMode = info.multiAssetModeEnabled,
            EventTime = info.eventTime,
            Timestamp = DateTime.UtcNow
        };

        // Extract balances from account info
        if (info.balances != null)
        {
            foreach (KeyValuePair<string, BalanceData> kvp in info.balances)
            {
                BalanceData bal = kvp.Value;
                if (bal == null)
                {
                    continue;
                }

                UpdateBalance(bal);
            }
        }

        // Extract positions from account info
        if (info.positionList?.positions != null)
        {
            foreach (KeyValuePair<string, ConcurrentDictionary<PositionSide, PositionData>> symbolKvp in info.positionList.positions)
            {
                foreach (KeyValuePair<PositionSide, PositionData> sideKvp in symbolKvp.Value)
                {
                    PositionData pos = sideKvp.Value;
                    if (pos == null)
                    {
                        continue;
                    }

                    UpdatePosition(pos);
                }
            }
            LastPositionUpdate = DateTime.UtcNow;
        }

        _accountInfo = snapshot;
        LastAccountInfoUpdate = DateTime.UtcNow;
        OnAccountInfoChanged?.Invoke();
    }

    // ── Balances ──────────────────────────────────────────────

    private void ProcessBalanceUpdate(NetworkData data)
    {
        if (data is not BalanceData bal)
        {
            return;
        }

        UpdateBalance(bal);
        LastBalanceUpdate = DateTime.UtcNow;
        OnBalanceChanged?.Invoke(bal.asset ?? "?");
    }

    private void ProcessBalanceList(NetworkData data)
    {
        if (data is not BalanceListData listData)
        {
            return;
        }

        if (listData.balances == null)
        {
            return;
        }

        foreach (KeyValuePair<string, BalanceData> kvp in listData.balances)
        {
            BalanceData bal = kvp.Value;
            if (bal == null)
            {
                continue;
            }

            UpdateBalance(bal);
        }

        LastBalanceUpdate = DateTime.UtcNow;
    }

    // Stablecoins where 1 unit ≈ $1 USDT — estimation should never be 0 if total > 0
    private static readonly HashSet<string> StablecoinAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        "USDT", "USDC", "BUSD", "FDUSD", "TUSD", "DAI", "USDP", "USDD", "PYUSD"
    };

    private void UpdateBalance(BalanceData bal)
    {
        string? assetName = bal.asset ?? "?";
        // Composite key: "ASSET:MARKET" to keep SPOT and FUTURES separate.
        string? key = $"{assetName}:{bal.marketType}";

        // MT-Core may send incorrect estimation for stablecoins (0, partial, or stale values)
        // before exchange price data fully loads. Since stablecoins ≈ $1 USDT each,
        // always use totalAmount as the authoritative estimation for these assets.
        double estimation = bal.estimation;
        if (bal.totalAmount > 0 && StablecoinAssets.Contains(assetName))
        {
            estimation = bal.totalAmount;
        }

        _balances[key] = new BalanceSnapshot
        {
            Asset = assetName,
            MarketType = bal.marketType,
            Total = bal.totalAmount,
            Locked = bal.lockedAmount,
            Available = bal.AvailableAmount,
            EstimationUSDT = estimation,
            IsDust = bal.isDust,
            IsTransferable = bal.isTransferable,
            Timestamp = DateTime.UtcNow
        };
    }

    // ── Orders ───────────────────────────────────────────────

    private void ProcessOrderList(NetworkData data)
    {
        if (data is not OrderListData listData)
        {
            return;
        }

        if (listData.orders == null)
        {
            return;
        }

        // Full snapshot — replace all orders for this market type
        var keysToRemove = new List<string>();
        foreach (KeyValuePair<string, OrderSnapshot> kvp in _orders)
        {
            if (kvp.Value.MarketType == listData.marketType)
            {
                keysToRemove.Add(kvp.Key);
            }
        }
        foreach (string key in keysToRemove)
        {
            _orders.TryRemove(key, out _);
        }

        foreach (KeyValuePair<string, OrderData> kvp in listData.orders)
        {
            OrderData order = kvp.Value;
            if (order == null)
            {
                continue;
            }

            UpdateOrder(order);
        }

        LastOrderUpdate = DateTime.UtcNow;
    }

    private void ProcessOrderUpdate(NetworkData data)
    {
        if (data is not OrderData order)
        {
            return;
        }

        UpdateOrder(order);
        LastOrderUpdate = DateTime.UtcNow;
        OnOrderChanged?.Invoke(order.clientOrderId ?? "?");
    }

    private void UpdateOrder(OrderData order)
    {
        string? key = order.clientOrderId ?? order.orderId ?? Guid.NewGuid().ToString();
        _orders[key] = new OrderSnapshot
        {
            ClientOrderId = order.clientOrderId ?? "",
            OrderId = order.orderId ?? "",
            Symbol = order.symbol ?? "",
            MarketType = order.marketType,
            Side = order.side,
            PositionSide = order.positionSide,
            OrderType = order.orderType,
            Status = order.status,
            Price = order.price,
            AvgPrice = order.avgPrice,
            StopPrice = order.stopPrice,
            Quantity = order.qty,
            ExecutedQty = order.executedQty,
            LastExecutedQty = order.lastExecutedQty,
            Commission = order.commission,
            CommissionAsset = order.commissionAsset ?? "",
            CommissionUSDT = order.commissionUSDT,
            TotalCommission = order.totalCommission,
            TotalCommissionAsset = order.totalCommissionAsset ?? "",
            ExecutedQtyUSDT = order.executedQtyUSDT,
            EstimatedValueUSDT = order.estimatedValueUSDT,
            TimeInForce = order.timeInForce,
            IsStopLoss = order.isStopLoss,
            IsTakeProfit = order.isTakeProfit,
            IsEntryPoint = order.isEntryPoint,
            IsEmulated = order.isEmulated,
            IsArchived = order.isArchived,
            TpslStatus = order.tpslStatus,
            IsAlgoOrder = order.IsAlgoOrder,
            IsManualOrder = order.IsManualOrder,
            AlgoId = order.info.algorithmId,
            AlgoSignature = order.info.signature ?? "",
            AlgoName = order.AlgorithmInfo.name ?? "",
            AlgoGroupType = order.info.algorithmGroupType,
            OrderComment = order.info.orderComment ?? "",
            CreationTime = order.creationTime,
            TransactTime = order.transactTime,
            GroupId = order.groupID,
            Timestamp = DateTime.UtcNow
        };
    }

    // ── Executions ───────────────────────────────────────────

    private void ProcessExecution(NetworkData data)
    {
        if (data is not OrderData order)
        {
            return;
        }

        // Execution is an order fill event — also update the order store
        UpdateOrder(order);

        var exec = new ExecutionSnapshot
        {
            ClientOrderId = order.clientOrderId ?? "",
            OrderId = order.orderId ?? "",
            Symbol = order.symbol ?? "",
            MarketType = order.marketType,
            Side = order.side,
            PositionSide = order.positionSide,
            OrderType = order.orderType,
            Status = order.status,
            Price = order.avgPrice != 0 ? order.avgPrice : order.price,
            LastFillQty = order.lastExecutedQty,
            CumulativeQty = order.executedQty,
            ExecutedQtyUSDT = order.executedQtyUSDT,
            Commission = order.commission,
            CommissionAsset = order.commissionAsset ?? "",
            CommissionUSDT = order.commissionUSDT,
            IsEmulated = order.isEmulated,
            IsAlgoOrder = order.IsAlgoOrder,
            AlgoSignature = order.info.signature ?? "",
            AlgoId = order.info.algorithmId,
            TransactTime = order.transactTime,
            ExecutionTime = DateTime.UtcNow
        };

        _recentExecutions.Enqueue(exec);
        while (_recentExecutions.Count > MAX_RECENT_EXECUTIONS)
        {
            _recentExecutions.TryDequeue(out _);
        }

        LastOrderUpdate = DateTime.UtcNow;
        OnExecution?.Invoke(exec);
    }

    // ── Positions ────────────────────────────────────────────

    private void ProcessPositions(NetworkData data)
    {
        if (data is not PositionListData listData)
        {
            return;
        }

        if (listData.positions == null)
        {
            return;
        }

        // Full snapshot — rebuild
        _positions.Clear();

        foreach (KeyValuePair<string, ConcurrentDictionary<PositionSide, PositionData>> symbolKvp in listData.positions)
        {
            foreach (KeyValuePair<PositionSide, PositionData> sideKvp in symbolKvp.Value)
            {
                PositionData pos = sideKvp.Value;
                if (pos == null)
                {
                    continue;
                }

                UpdatePosition(pos);
            }
        }

        LastPositionUpdate = DateTime.UtcNow;
        OnPositionsChanged?.Invoke();
    }

    private void UpdatePosition(PositionData pos)
    {
        string? key = $"{pos.symbol ?? "?"}:{pos.positionSide}";
        _positions[key] = new PositionSnapshot
        {
            Symbol = pos.symbol ?? "",
            MarketType = pos.marketType,
            PositionSide = pos.positionSide,
            PositionStatus = pos.positionStatus,
            Leverage = pos.leverage,
            Amount = pos.positionAmount,
            EntryPrice = pos.entryPrice,
            UnrealizedPnl = pos.unrealizedPNL,
            LiquidationPrice = pos.liquidationPrice,
            MarginType = pos.marginType,
            Margin = pos.margin,
            IsOpen = pos.IsOpen,
            CreationTime = pos.creationTime,
            Timestamp = DateTime.UtcNow
        };
    }

    // ── Queries ──────────────────────────────────────────────

    public AccountInfoSnapshot? GetAccountInfo() => _accountInfo;

    /// <summary>Get all balances, optionally excluding dust.</summary>
    public IReadOnlyList<BalanceSnapshot> GetBalances(bool includeDust = false)
    {
        var list = new List<BalanceSnapshot>();
        foreach (BalanceSnapshot b in _balances.Values)
        {
            if (includeDust || (!b.IsDust && b.Total > 0))
            {
                list.Add(b);
            }
        }
        list.Sort((a, b) => b.EstimationUSDT.CompareTo(a.EstimationUSDT));
        return list;
    }

    /// <summary>Get a specific balance by asset name (returns highest-value match across market types).</summary>
    public BalanceSnapshot? GetBalance(string asset)
    {
        BalanceSnapshot? best = null;
        foreach (BalanceSnapshot b in _balances.Values)
        {
            if (b.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase))
            {
                if (best == null || b.EstimationUSDT > best.EstimationUSDT)
                {
                    best = b;
                }
            }
        }
        return best;
    }

    /// <summary>Get all balances for a specific asset across market types (SPOT, FUTURES, etc.).</summary>
    public IReadOnlyList<BalanceSnapshot> GetBalanceAllMarkets(string asset)
    {
        var list = new List<BalanceSnapshot>();
        foreach (BalanceSnapshot b in _balances.Values)
        {
            if (b.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(b);
            }
        }
        list.Sort((a, b) => b.EstimationUSDT.CompareTo(a.EstimationUSDT));
        return list;
    }

    /// <summary>Get total estimated balance in USDT.</summary>
    public double GetTotalBalanceUSDT()
    {
        double total = 0;
        foreach (BalanceSnapshot b in _balances.Values)
        {
            if (!b.IsDust)
            {
                total += b.EstimationUSDT;
            }
        }
        return total;
    }

    /// <summary>Get all active orders.</summary>
    public IReadOnlyList<OrderSnapshot> GetOrders(bool activeOnly = true)
    {
        var list = new List<OrderSnapshot>();
        foreach (OrderSnapshot o in _orders.Values)
        {
            if (!activeOnly || o.Status == OrderStatus.NEW || o.Status == OrderStatus.PARTIALLY_FILLED)
            {
                list.Add(o);
            }
        }
        list.Sort((a, b) => b.CreationTime.CompareTo(a.CreationTime));
        return list;
    }

    /// <summary>Get orders for a specific symbol.</summary>
    public IReadOnlyList<OrderSnapshot> GetOrdersBySymbol(string symbol)
    {
        var list = new List<OrderSnapshot>();
        foreach (OrderSnapshot o in _orders.Values)
        {
            if (o.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(o);
            }
        }
        list.Sort((a, b) => b.CreationTime.CompareTo(a.CreationTime));
        return list;
    }

    /// <summary>Get all positions, optionally only open ones.</summary>
    public IReadOnlyList<PositionSnapshot> GetPositions(bool openOnly = true)
    {
        var list = new List<PositionSnapshot>();
        foreach (PositionSnapshot p in _positions.Values)
        {
            if (!openOnly || p.IsOpen)
            {
                list.Add(p);
            }
        }
        list.Sort((a, b) => string.Compare(a.Symbol, b.Symbol, StringComparison.Ordinal));
        return list;
    }

    /// <summary>Get recent trade executions.</summary>
    public IReadOnlyList<ExecutionSnapshot> GetRecentExecutions(int count = 20)
    {
        ExecutionSnapshot[]? arr = _recentExecutions.ToArray();
        var list = new List<ExecutionSnapshot>();
        for (int i = arr.Length - 1; i >= 0 && list.Count < count; i--)
        {
            list.Add(arr[i]);
        }
        return list;
    }

    /// <summary>Count of active (non-dust, positive) balances.</summary>
    public int BalanceCount
    {
        get
        {
            int count = 0;
            foreach (BalanceSnapshot b in _balances.Values)
            {
                if (!b.IsDust && b.Total > 0)
                {
                    count++;
                }
            }
            return count;
        }
    }

    /// <summary>Count of active orders.</summary>
    public int ActiveOrderCount
    {
        get
        {
            int count = 0;
            foreach (OrderSnapshot o in _orders.Values)
            {
                if (o.Status == OrderStatus.NEW || o.Status == OrderStatus.PARTIALLY_FILLED)
                {
                    count++;
                }
            }
            return count;
        }
    }

    /// <summary>Count of open positions.</summary>
    public int OpenPositionCount
    {
        get
        {
            int count = 0;
            foreach (PositionSnapshot p in _positions.Values)
            {
                if (p.IsOpen)
                {
                    count++;
                }
            }
            return count;
        }
    }

    /// <summary>Clear all stored data (on disconnect).</summary>
    public void Clear()
    {
        _balances.Clear();
        _orders.Clear();
        _positions.Clear();
        _recentExecutions.Clear();
        _accountInfo = null;
    }
}

// ── Snapshot DTOs ────────────────────────────────────────────
// Immutable snapshots for thread-safe reads. Decoupled from MTShared types.

public sealed class AccountInfoSnapshot
{
    public MarketType MarketType { get; init; }
    public bool CanTrade { get; init; }
    public PositionModeType PositionMode { get; init; }
    public bool MultiAssetMode { get; init; }
    public long EventTime { get; init; }
    public DateTime Timestamp { get; init; }
}

public sealed class BalanceSnapshot
{
    public string Asset { get; init; } = "";
    public MarketType MarketType { get; init; }
    public double Total { get; init; }
    public double Locked { get; init; }
    public double Available { get; init; }
    public double EstimationUSDT { get; init; }
    public bool IsDust { get; init; }
    public bool IsTransferable { get; init; }
    public DateTime Timestamp { get; init; }
}

public sealed class OrderSnapshot
{
    public string ClientOrderId { get; init; } = "";
    public string OrderId { get; init; } = "";
    public string Symbol { get; init; } = "";
    public MarketType MarketType { get; init; }
    public OrderSideType Side { get; init; }
    public PositionSide PositionSide { get; init; }
    public OrderType OrderType { get; init; }
    public OrderStatus Status { get; init; }
    public decimal Price { get; init; }
    public decimal AvgPrice { get; init; }
    public decimal StopPrice { get; init; }
    public decimal Quantity { get; init; }
    public decimal ExecutedQty { get; init; }
    public decimal LastExecutedQty { get; init; }
    public decimal Commission { get; init; }
    public string CommissionAsset { get; init; } = "";
    public double CommissionUSDT { get; init; }
    public decimal TotalCommission { get; init; }
    public string TotalCommissionAsset { get; init; } = "";
    public double ExecutedQtyUSDT { get; init; }
    public uint EstimatedValueUSDT { get; init; }
    public TimeInForce TimeInForce { get; init; }
    public bool IsStopLoss { get; init; }
    public bool IsTakeProfit { get; init; }
    public bool IsEntryPoint { get; init; }
    public bool IsEmulated { get; init; }
    public bool IsArchived { get; init; }
    public TPSLStatus TpslStatus { get; init; }
    public bool IsAlgoOrder { get; init; }
    public bool IsManualOrder { get; init; }
    public long AlgoId { get; init; }
    public string AlgoSignature { get; init; } = "";
    public string AlgoName { get; init; } = "";
    public AlgorithmGroupType AlgoGroupType { get; init; }
    public string OrderComment { get; init; } = "";
    public long CreationTime { get; init; }
    public long TransactTime { get; init; }
    public int GroupId { get; init; }
    public DateTime Timestamp { get; init; }

    public decimal FilledPercent =>
        Quantity == 0 ? 0 : Math.Round(ExecutedQty / Quantity * 100, 2);
}

public sealed class PositionSnapshot
{
    public string Symbol { get; init; } = "";
    public MarketType MarketType { get; init; }
    public PositionSide PositionSide { get; init; }
    public PositionStatus PositionStatus { get; init; }
    public short Leverage { get; init; }
    public decimal Amount { get; init; }
    public decimal EntryPrice { get; init; }
    public double UnrealizedPnl { get; init; }
    public double LiquidationPrice { get; init; }
    public MarginType MarginType { get; init; }
    public decimal Margin { get; init; }
    public bool IsOpen { get; init; }
    public long CreationTime { get; init; }
    public DateTime Timestamp { get; init; }

    /// <summary>Position notional value = |amount| * entryPrice.</summary>
    public decimal NotionalValue =>
        Math.Abs(Amount) * EntryPrice;

    /// <summary>PnL percentage relative to margin.</summary>
    public double PnlPercent =>
        Margin != 0 ? UnrealizedPnl / (double)Margin * 100.0 : 0;
}

public sealed class ExecutionSnapshot
{
    public string ClientOrderId { get; init; } = "";
    public string OrderId { get; init; } = "";
    public string Symbol { get; init; } = "";
    public MarketType MarketType { get; init; }
    public OrderSideType Side { get; init; }
    public PositionSide PositionSide { get; init; }
    public OrderType OrderType { get; init; }
    public OrderStatus Status { get; init; }
    public decimal Price { get; init; }
    public decimal LastFillQty { get; init; }
    public decimal CumulativeQty { get; init; }
    public double ExecutedQtyUSDT { get; init; }
    public decimal Commission { get; init; }
    public string CommissionAsset { get; init; } = "";
    public double CommissionUSDT { get; init; }
    public bool IsEmulated { get; init; }
    public bool IsAlgoOrder { get; init; }
    public string AlgoSignature { get; init; } = "";
    public long AlgoId { get; init; }
    public long TransactTime { get; init; }
    public DateTime ExecutionTime { get; init; }
}
