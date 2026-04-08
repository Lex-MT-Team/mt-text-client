using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using MTShared;
using MTShared.Network;
using MTShared.Types;
namespace MTTextClient.Core;

/// <summary>
/// In-memory store for Exchange Info received via EXCHANGE_INFO_SUBSCRIBE.
/// Thread-safe. Receives trade pair lists, price updates, and API limit data.
///
/// Push Events Handled:
///   TRADE_PAIR_LIST_RESULT       (9)  → TradePairListData    — full list of trade pairs with rules
///   TRADE_PAIR_PRICE_LIST_RESULT (10) → TradePairPriceListData — price updates for all pairs
///   MAX_API_LIMIT_RESULT         (11) → MaxApiLimitData       — API weight/rate limit loading %
///   TICKER_LIST_DATA_RESULT      (81) → (future) 24h ticker stats
///   TRADE_PAIR_LISTING_RESULT    (96) → (future) new pair listings
/// </summary>
public sealed class ExchangeInfoStore
{
    // Key: symbol (e.g. "BTCUSDT")
    private readonly ConcurrentDictionary<string, TradePairSnapshot> _tradePairs = new(StringComparer.OrdinalIgnoreCase);

    // API loading % per market type (from MaxApiLimitData)
    private readonly ConcurrentDictionary<MarketType, short> _apiLoading = new();

    public DateTime LastTradePairUpdate { get; private set; }
    public DateTime LastPriceUpdate { get; private set; }
    public DateTime LastApiLimitUpdate { get; private set; }

    // Events
    public event Action<int>? OnTradePairsLoaded;
    public event Action? OnPricesUpdated;

    /// <summary>
    /// Process incoming exchange info data from subscription callback.
    /// </summary>
    public void ProcessData(NetworkMessageType msgType, NetworkData data)
    {
        switch (msgType)
        {
            case NetworkMessageType.TRADE_PAIR_LIST_RESULT:
                ProcessTradePairList(data);
                break;

            case NetworkMessageType.TRADE_PAIR_PRICE_LIST_RESULT:
                ProcessPriceUpdate(data);
                break;

            case NetworkMessageType.MAX_API_LIMIT_RESULT:
                ProcessApiLimit(data);
                break;

                // TICKER_LIST_DATA_RESULT (81) — 24h ticker stats (volume, high/low, change%)
                // TODO: Handle when needed for market analysis
                // TRADE_PAIR_LISTING_RESULT (96) — new/delisted pairs
                // TODO: Handle for dynamic pair tracking
        }
    }

    /// <summary>
    /// Process full trade pair list (initial load + periodic full refresh).
    /// </summary>
    private void ProcessTradePairList(NetworkData data)
    {
        if (data is not TradePairListData listData)
        {
            return;
        }

        ConcurrentDictionary<string, TradePairData>? pairs = listData.TradePairs;
        if (pairs == null)
        {
            return;
        }

        foreach (KeyValuePair<string, TradePairData> kvp in pairs)
        {
            TradePairData tp = kvp.Value;
            if (tp == null)
            {
                continue;
            }

            string symbol = tp.symbol ?? "?";

            _tradePairs[symbol] = new TradePairSnapshot
            {
                Symbol = symbol,
                MarketType = tp.marketType,
                BaseAsset = tp.baseAsset ?? "",
                QuoteAsset = tp.quoteAsset ?? "",
                MarginAsset = tp.marginAsset ?? "",
                QuoteName = tp.quoteName ?? "",
                Status = tp.status,
                MinQty = tp.minQty,
                MaxQty = tp.maxQty,
                MarketMaxQty = tp.marketMaxQty,
                StepSize = tp.stepSize,
                StepSizePrecision = tp.stepSizePrecision,
                MinPrice = tp.minPrice,
                MaxPrice = tp.maxPrice,
                TickSize = tp.tickSize,
                TickSizePrecision = tp.tickSizePrecision,
                MinNotional = tp.minNotional,
                MaxNotional = tp.maxNotional,
                UseMinNotional = tp.useMinNotional,
                UseMaxNotional = tp.useMaxNotional,
                ContractSize = tp.contractSize,
                MultiplierUp = tp.multiplierUp,
                MultiplierDown = tp.multiplierDown,
                Qav24h = tp.Qav24h,
                IsTradable = tp.IsTradable,
                TickerPrice = tp.TickerPrice,
                TickerEventTime = tp.TickerEventTime,
                Timestamp = DateTime.UtcNow
            };
        }

        LastTradePairUpdate = DateTime.UtcNow;
        OnTradePairsLoaded?.Invoke(_tradePairs.Count);
    }

    /// <summary>
    /// Process price-only updates (periodic, high-frequency).
    /// Updates TickerPrice for existing pairs.
    /// </summary>
    private void ProcessPriceUpdate(NetworkData data)
    {
        // TRADE_PAIR_PRICE_LIST_RESULT sends TradePairPriceListData
        // which has List<TradePairPriceUpdateData> with {symbol, price, marketType}
        if (data is TradePairPriceListData priceListData && priceListData.prices != null)
        {
            foreach (TradePairPriceUpdateData priceUpdate in priceListData.prices)
            {
                if (priceUpdate.symbol == null)
                {
                    continue;
                }

                if (_tradePairs.TryGetValue(priceUpdate.symbol, out TradePairSnapshot? existing))
                {
                    _tradePairs[priceUpdate.symbol] = existing with
                    {
                        TickerPrice = priceUpdate.price,
                        Timestamp = DateTime.UtcNow
                    };
                }
            }
            LastPriceUpdate = DateTime.UtcNow;
            OnPricesUpdated?.Invoke();
            return;
        }

        // Fallback: some builds may send TradePairListData for price updates too
        if (data is TradePairListData listData && listData.TradePairs != null)
        {
            foreach (KeyValuePair<string, TradePairData> kvp in listData.TradePairs)
            {
                TradePairData tp = kvp.Value;
                if (tp?.symbol == null)
                {
                    continue;
                }

                if (_tradePairs.TryGetValue(tp.symbol, out TradePairSnapshot? existing))
                {
                    _tradePairs[tp.symbol] = existing with
                    {
                        TickerPrice = tp.TickerPrice,
                        TickerEventTime = tp.TickerEventTime,
                        Timestamp = DateTime.UtcNow
                    };
                }
            }
            LastPriceUpdate = DateTime.UtcNow;
            OnPricesUpdated?.Invoke();
        }
    }

    /// <summary>
    /// Process API loading data. MaxApiLimitData contains Dict{MarketType, short}
    /// representing API usage percentage per market.
    /// </summary>
    private void ProcessApiLimit(NetworkData data)
    {
        if (data is not MaxApiLimitData limitData)
        {
            return;
        }

        if (limitData.maxApiLimit == null)
        {
            return;
        }

        foreach (KeyValuePair<MarketType, short> kvp in limitData.maxApiLimit)
        {
            _apiLoading[kvp.Key] = kvp.Value;
        }
        LastApiLimitUpdate = DateTime.UtcNow;
    }

    // ── Queries ──────────────────────────────────────────────

    /// <summary>Get all trade pairs.</summary>
    public IReadOnlyList<TradePairSnapshot> GetTradePairs()
    {
        var list = new List<TradePairSnapshot>(_tradePairs.Values);
        list.Sort((a, b) => string.Compare(a.Symbol, b.Symbol, StringComparison.Ordinal));
        return list;
    }

    /// <summary>Get trade pairs filtered by market type.</summary>
    public IReadOnlyList<TradePairSnapshot> GetTradePairs(MarketType marketType)
    {
        var list = new List<TradePairSnapshot>();
        foreach (TradePairSnapshot t in _tradePairs.Values)
        {
            if (t.MarketType == marketType)
            {
                list.Add(t);
            }
        }
        list.Sort((a, b) => string.Compare(a.Symbol, b.Symbol, StringComparison.Ordinal));
        return list;
    }

    /// <summary>Search trade pairs by symbol substring.</summary>
    public IReadOnlyList<TradePairSnapshot> SearchTradePairs(string query)
    {
        var list = new List<TradePairSnapshot>();
        foreach (TradePairSnapshot t in _tradePairs.Values)
        {
            if (t.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.BaseAsset.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(t);
            }
        }
        list.Sort((a, b) => string.Compare(a.Symbol, b.Symbol, StringComparison.Ordinal));
        return list;
    }

    /// <summary>Get a specific trade pair.</summary>
    public TradePairSnapshot? GetTradePair(string symbol)
    {
        _tradePairs.TryGetValue(symbol, out TradePairSnapshot? tp);
        return tp;
    }

    /// <summary>Get API loading percentages per market type.</summary>
    public IReadOnlyDictionary<MarketType, short> GetApiLoading()
    {
        return new Dictionary<MarketType, short>(_apiLoading);
    }

    /// <summary>Total number of known trade pairs.</summary>
    public int TradePairCount => _tradePairs.Count;

    /// <summary>Count by market type.</summary>
    public Dictionary<MarketType, int> GetCountsByMarketType()
    {
        var dict = new Dictionary<MarketType, int>();
        foreach (TradePairSnapshot t in _tradePairs.Values)
        {
            if (dict.ContainsKey(t.MarketType))
            {
                dict[t.MarketType]++;
            }
            else
            {
                dict[t.MarketType] = 1;
            }
        }
        return dict;
    }

    /// <summary>Clear all stored data (on disconnect).</summary>
    public void Clear()
    {
        _tradePairs.Clear();
        _apiLoading.Clear();
    }
}

// ── Snapshot DTOs ────────────────────────────────────────────

public sealed record TradePairSnapshot
{
    public string Symbol { get; init; } = "";
    public MarketType MarketType { get; init; }
    public string BaseAsset { get; init; } = "";
    public string QuoteAsset { get; init; } = "";
    public string MarginAsset { get; init; } = "";
    public string QuoteName { get; init; } = "";
    public SymbolStatusType Status { get; init; }
    public double MinQty { get; init; }
    public double MaxQty { get; init; }
    public double MarketMaxQty { get; init; }
    public double StepSize { get; init; }
    public byte StepSizePrecision { get; init; }
    public double MinPrice { get; init; }
    public double MaxPrice { get; init; }
    public double TickSize { get; init; }
    public byte TickSizePrecision { get; init; }
    public double MinNotional { get; init; }
    public double MaxNotional { get; init; }
    public bool UseMinNotional { get; init; }
    public bool UseMaxNotional { get; init; }
    public float ContractSize { get; init; }
    public double MultiplierUp { get; init; }
    public double MultiplierDown { get; init; }
    public double Qav24h { get; init; }
    public bool IsTradable { get; init; }
    public double TickerPrice { get; init; }
    public long TickerEventTime { get; init; }
    public DateTime Timestamp { get; init; }
}
