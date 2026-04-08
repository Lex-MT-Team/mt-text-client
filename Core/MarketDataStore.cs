#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using MTShared.Network;
using MTShared.Types;

namespace MTTextClient.Core
{
    public class MarketDataStore
    {
        // Trade feeds: key = "exchange:market:symbol"
        private readonly ConcurrentDictionary<string, TradeUpdateData> _lastTrades = new ConcurrentDictionary<string, TradeUpdateData>();
        private readonly ConcurrentDictionary<string, List<TradeUpdateData>> _tradeBuffers = new ConcurrentDictionary<string, List<TradeUpdateData>>();

        // Depth snapshots: key = "exchange:market:symbol"
        private readonly ConcurrentDictionary<string, DepthUpdateData> _depthSnapshots = new ConcurrentDictionary<string, DepthUpdateData>();

        // Mark price: key = "exchange:market:symbol"
        private readonly ConcurrentDictionary<string, MarkPriceUpdateData> _markPrices = new ConcurrentDictionary<string, MarkPriceUpdateData>();

        // Kline updates: key = "exchange:market:symbol:interval"
        private readonly ConcurrentDictionary<string, KlineUpdateData> _lastKlines = new ConcurrentDictionary<string, KlineUpdateData>();

        // Ticker updates: key = "exchange:market:symbol"
        private readonly ConcurrentDictionary<string, TickerUpdateData> _tickers = new ConcurrentDictionary<string, TickerUpdateData>();

        private readonly int _maxTradeBuffer;

        public MarketDataStore(int maxTradeBuffer = 100)
        {
            _maxTradeBuffer = maxTradeBuffer;
        }

        #region Trades

        public void UpdateTrade(string key, TradeUpdateData trade)
        {
            _lastTrades[key] = trade;
            List<TradeUpdateData> buffer = _tradeBuffers.GetOrAdd(key, _ => new List<TradeUpdateData>());
            lock (buffer)
            {
                buffer.Add(trade);
                while (buffer.Count > _maxTradeBuffer)
                {
                    buffer.RemoveAt(0);
                }
            }
        }

        public bool TryGetLastTrade(string key, out TradeUpdateData trade)
        {
            return _lastTrades.TryGetValue(key, out trade);
        }

        public List<TradeUpdateData> GetTradeBuffer(string key)
        {
            if (_tradeBuffers.TryGetValue(key, out List<TradeUpdateData> buffer))
            {
                lock (buffer)
                {
                    return new List<TradeUpdateData>(buffer);
                }
            }
            return new List<TradeUpdateData>();
        }

        public IReadOnlyList<string> GetTradeSubscriptions()
        {
            return new List<string>(_lastTrades.Keys);
        }

        #endregion

        #region Depth

        public void UpdateDepth(string key, DepthUpdateData depth)
        {
            _depthSnapshots[key] = depth;
        }

        public bool TryGetDepth(string key, out DepthUpdateData depth)
        {
            return _depthSnapshots.TryGetValue(key, out depth);
        }

        public IReadOnlyList<string> GetDepthSubscriptions()
        {
            return new List<string>(_depthSnapshots.Keys);
        }

        #endregion

        #region Mark Price

        public void UpdateMarkPrice(string key, MarkPriceUpdateData data)
        {
            _markPrices[key] = data;
        }

        public bool TryGetMarkPrice(string key, out MarkPriceUpdateData data)
        {
            return _markPrices.TryGetValue(key, out data);
        }

        public IReadOnlyList<string> GetMarkPriceSubscriptions()
        {
            return new List<string>(_markPrices.Keys);
        }

        #endregion

        #region Klines

        public void UpdateKline(string key, KlineUpdateData data)
        {
            _lastKlines[key] = data;
        }

        public bool TryGetLastKline(string key, out KlineUpdateData data)
        {
            return _lastKlines.TryGetValue(key, out data);
        }

        public IReadOnlyList<string> GetKlineSubscriptions()
        {
            return new List<string>(_lastKlines.Keys);
        }

        #endregion

        #region Tickers

        public void UpdateTicker(string key, TickerUpdateData data)
        {
            _tickers[key] = data;
        }

        public bool TryGetTicker(string key, out TickerUpdateData data)
        {
            return _tickers.TryGetValue(key, out data);
        }

        public IReadOnlyList<string> GetTickerSubscriptions()
        {
            return new List<string>(_tickers.Keys);
        }

        #endregion

        public void ClearAll()
        {
            _lastTrades.Clear();
            _tradeBuffers.Clear();
            _depthSnapshots.Clear();
            _markPrices.Clear();
            _lastKlines.Clear();
            _tickers.Clear();
        }
    }
}
