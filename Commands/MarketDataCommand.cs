using System;
using System.Collections.Generic;
using System.Text;
using MTShared.Network;
using MTShared.Types;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// Market data commands — subscribe to real-time market data feeds.
///
/// Subcommands:
///   marketdata status                                    — show all active subscriptions
///   marketdata trades <symbol> [market]                  — show recent trades
///   marketdata trades-subscribe <symbol> [market]        — subscribe to trade feed
///   marketdata trades-unsubscribe <symbol> [market]      — unsubscribe from trade feed
///   marketdata depth <symbol> [market]                   — show order book
///   marketdata depth-subscribe <symbol> [market]         — subscribe to depth feed
///   marketdata depth-unsubscribe <symbol> [market]       — unsubscribe from depth feed
///   marketdata markprice <symbol> [market]               — show mark price + funding
///   marketdata markprice-subscribe <symbol> [market]     — subscribe to mark price
///   marketdata markprice-unsubscribe <symbol> [market]   — unsubscribe from mark price
///   marketdata klines <symbol> <interval> [market]       — show last kline
///   marketdata klines-subscribe <symbol> <interval> [m]  — subscribe to kline feed
///   marketdata klines-unsubscribe <symbol> <interval> [m]— unsubscribe from kline feed
///   marketdata ticker [market]                           — show all tickers
///   marketdata ticker-subscribe [market]                 — subscribe to ticker feed
///   marketdata ticker-unsubscribe [market]               — unsubscribe from ticker feed
///
/// Supports @profile targeting.
/// </summary>
public sealed class MarketDataCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "marketdata";
    public string Description => "Subscribe to and view real-time market data streams (trades, depth, mark price, klines, tickers)";
    public string Usage => "marketdata <status|trades|trades-subscribe|depth|depth-subscribe|markprice|klines|ticker|...> [symbol] [interval] [@profile]";

    public MarketDataCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public CommandResult Execute(string[] args)
    {
        string? targetProfile = null;
        var cleanArgs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('@'))
            {
                targetProfile = args[i][1..];
            }
            else
            {
                cleanArgs.Add(args[i]);
            }
        }

        string subcommand = cleanArgs.Count > 0 ? cleanArgs[0] : "status";
        string[] subArgs = cleanArgs.Count > 1 ? cleanArgs.GetRange(1, cleanArgs.Count - 1).ToArray() : Array.Empty<string>();

        switch (subcommand)
        {
            case "status":
                return HandleStatus(targetProfile);
            case "trades":
                return HandleTrades(subArgs, targetProfile);
            case "trades-subscribe":
                return HandleTradesSubscribe(subArgs, targetProfile);
            case "trades-unsubscribe":
                return HandleTradesUnsubscribe(subArgs, targetProfile);
            case "depth":
                return HandleDepth(subArgs, targetProfile);
            case "depth-subscribe":
                return HandleDepthSubscribe(subArgs, targetProfile);
            case "depth-unsubscribe":
                return HandleDepthUnsubscribe(subArgs, targetProfile);
            case "markprice":
                return HandleMarkPrice(subArgs, targetProfile);
            case "markprice-subscribe":
                return HandleMarkPriceSubscribe(subArgs, targetProfile);
            case "markprice-unsubscribe":
                return HandleMarkPriceUnsubscribe(subArgs, targetProfile);
            case "klines":
                return HandleKlines(subArgs, targetProfile);
            case "klines-subscribe":
                return HandleKlinesSubscribe(subArgs, targetProfile);
            case "klines-unsubscribe":
                return HandleKlinesUnsubscribe(subArgs, targetProfile);
            case "ticker":
                return HandleTicker(subArgs, targetProfile);
            case "ticker-subscribe":
                return HandleTickerSubscribe(subArgs, targetProfile);
            case "ticker-unsubscribe":
                return HandleTickerUnsubscribe(subArgs, targetProfile);
            default:
                return CommandResult.Fail($"Unknown subcommand '{subcommand}'. Use: status, trades, depth, markprice, klines, ticker (+ -subscribe/-unsubscribe)");
        }
    }

    // ---- Status ----

    private CommandResult HandleStatus(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"## Market Data Subscriptions — {conn.Name}");
        sb.AppendLine();

        IReadOnlyList<string> tradeSubs = conn.MarketDataStore.GetTradeSubscriptions();
        sb.AppendLine($"**Trades** ({tradeSubs.Count}): {(tradeSubs.Count > 0 ? string.Join(", ", tradeSubs) : "none")}");

        IReadOnlyList<string> depthSubs = conn.MarketDataStore.GetDepthSubscriptions();
        sb.AppendLine($"**Depth** ({depthSubs.Count}): {(depthSubs.Count > 0 ? string.Join(", ", depthSubs) : "none")}");

        IReadOnlyList<string> mpSubs = conn.MarketDataStore.GetMarkPriceSubscriptions();
        sb.AppendLine($"**Mark Price** ({mpSubs.Count}): {(mpSubs.Count > 0 ? string.Join(", ", mpSubs) : "none")}");

        IReadOnlyList<string> klineSubs = conn.MarketDataStore.GetKlineSubscriptions();
        sb.AppendLine($"**Klines** ({klineSubs.Count}): {(klineSubs.Count > 0 ? string.Join(", ", klineSubs) : "none")}");

        IReadOnlyList<string> tickerSubs = conn.MarketDataStore.GetTickerSubscriptions();
        sb.AppendLine($"**Tickers** ({tickerSubs.Count}): {(tickerSubs.Count > 0 ? string.Join(", ", tickerSubs) : "none")}");

        sb.AppendLine();
        sb.AppendLine($"Notifications subscribed: {conn.IsNotificationSubscribed}");
        sb.AppendLine($"Alerts subscribed: {conn.IsAlertsSubscribed}");

        return CommandResult.Ok(sb.ToString());
    }

    // ---- Trades ----

    private CommandResult HandleTrades(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (parts.Length < 1)
        {
            // Show all subscribed trades
            IReadOnlyList<string> subs = conn.MarketDataStore.GetTradeSubscriptions();
            if (subs.Count == 0)
            {
                return CommandResult.Ok($"No trade subscriptions on {conn.Name}.");
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"## Trades — {conn.Name}");
            sb.AppendLine();
            sb.AppendLine("| Symbol Key | Last Price | Qty | Side | Buffer |");
            sb.AppendLine("|------------|-----------|-----|------|--------|");

            foreach (string key in subs)
            {
                if (conn.MarketDataStore.TryGetLastTrade(key, out TradeUpdateData trade))
                {
                    string side = trade.isBuyerMaker ? "SELL" : "BUY";
                    List<TradeUpdateData> buffer = conn.MarketDataStore.GetTradeBuffer(key);
                    sb.AppendLine($"| {key} | {trade.price:F8} | {trade.quantity:F4} | {side} | {buffer.Count} |");
                }
            }
            return CommandResult.Ok(sb.ToString());
        }

        string symbol = parts[0].ToUpperInvariant();
        MarketType marketType = ParseMarketType(parts, 1);
        string tradeKey = $"{conn.Profile.Exchange}:{marketType}:{symbol}";

        List<TradeUpdateData> tradeBuffer = conn.MarketDataStore.GetTradeBuffer(tradeKey);
        if (tradeBuffer.Count == 0)
        {
            return CommandResult.Ok($"No trade data for {tradeKey}. Subscribe first.");
        }

        StringBuilder sb2 = new StringBuilder();
        sb2.AppendLine($"## Trades — {tradeKey} (last {tradeBuffer.Count})");
        sb2.AppendLine();
        sb2.AppendLine("| Time | Price | Qty | Side |");
        sb2.AppendLine("|------|-------|-----|------|");

        int start = Math.Max(0, tradeBuffer.Count - 20);
        for (int i = start; i < tradeBuffer.Count; i++)
        {
            TradeUpdateData t = tradeBuffer[i];
            string side = t.isBuyerMaker ? "SELL" : "BUY";
            string time = DateTimeOffset.FromUnixTimeMilliseconds(t.tradeTime).UtcDateTime.ToString("HH:mm:ss.fff");
            sb2.AppendLine($"| {time} | {t.price:F8} | {t.quantity:F4} | {side} |");
        }

        return CommandResult.Ok(sb2.ToString(), new { count = tradeBuffer.Count });
    }

    private CommandResult HandleTradesSubscribe(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!conn.IsConnected)
        {
            return CommandResult.Fail($"{conn.Name} is not connected.");
        }

        if (parts.Length < 1)
        {
            return CommandResult.Fail("Usage: marketdata trades-subscribe <symbol> [FUTURES|SPOT]");
        }

        string symbol = parts[0].ToUpperInvariant();
        MarketType marketType = ParseMarketType(parts, 1);
        conn.SubscribeTrades(conn.Profile.Exchange, marketType, symbol);
        return CommandResult.Ok($"Subscribed to trades for {symbol} ({marketType}) on {conn.Name}.");
    }

    private CommandResult HandleTradesUnsubscribe(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (parts.Length < 1)
        {
            return CommandResult.Fail("Usage: marketdata trades-unsubscribe <symbol> [FUTURES|SPOT]");
        }

        string symbol = parts[0].ToUpperInvariant();
        MarketType marketType = ParseMarketType(parts, 1);
        conn.UnsubscribeTrades(conn.Profile.Exchange, marketType, symbol);
        return CommandResult.Ok($"Unsubscribed from trades for {symbol} ({marketType}) on {conn.Name}.");
    }

    // ---- Depth ----

    private CommandResult HandleDepth(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (parts.Length < 1)
        {
            IReadOnlyList<string> subs = conn.MarketDataStore.GetDepthSubscriptions();
            if (subs.Count == 0)
            {
                return CommandResult.Ok($"No depth subscriptions on {conn.Name}.");
            }
            return CommandResult.Ok($"Depth subscriptions: {string.Join(", ", subs)}");
        }

        string symbol = parts[0].ToUpperInvariant();
        MarketType marketType = ParseMarketType(parts, 1);
        string depthKey = $"{conn.Profile.Exchange}:{marketType}:{symbol}";

        if (!conn.MarketDataStore.TryGetDepth(depthKey, out DepthUpdateData depth))
        {
            return CommandResult.Ok($"No depth data for {depthKey}. Subscribe first.");
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"## Order Book — {depthKey}");
        sb.AppendLine($"Initial: {depth.isInitial} | Event: {depth.eventTime}");
        sb.AppendLine();

        // Asks (top 10, reversed so lowest ask at bottom)
        sb.AppendLine("**Asks** (sell):");
        sb.AppendLine("| Price | Qty |");
        sb.AppendLine("|-------|-----|");
        if (depth.asks != null)
        {
            int askCount = Math.Min(10, depth.asks.Count);
            for (int i = askCount - 1; i >= 0; i--)
            {
                double[] level = depth.asks[i];
                sb.AppendLine($"| {level[0]:F8} | {level[1]:F4} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("**Bids** (buy):");
        sb.AppendLine("| Price | Qty |");
        sb.AppendLine("|-------|-----|");
        if (depth.bids != null)
        {
            int bidCount = Math.Min(10, depth.bids.Count);
            for (int i = 0; i < bidCount; i++)
            {
                double[] level = depth.bids[i];
                sb.AppendLine($"| {level[0]:F8} | {level[1]:F4} |");
            }
        }

        return CommandResult.Ok(sb.ToString());
    }

    private CommandResult HandleDepthSubscribe(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!conn.IsConnected)
        {
            return CommandResult.Fail($"{conn.Name} is not connected.");
        }

        if (parts.Length < 1)
        {
            return CommandResult.Fail("Usage: marketdata depth-subscribe <symbol> [FUTURES|SPOT]");
        }

        string symbol = parts[0].ToUpperInvariant();
        MarketType marketType = ParseMarketType(parts, 1);
        conn.SubscribeDepth(conn.Profile.Exchange, marketType, symbol);
        return CommandResult.Ok($"Subscribed to depth for {symbol} ({marketType}) on {conn.Name}.");
    }

    private CommandResult HandleDepthUnsubscribe(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (parts.Length < 1)
        {
            return CommandResult.Fail("Usage: marketdata depth-unsubscribe <symbol> [FUTURES|SPOT]");
        }

        string symbol = parts[0].ToUpperInvariant();
        MarketType marketType = ParseMarketType(parts, 1);
        conn.UnsubscribeDepth(conn.Profile.Exchange, marketType, symbol);
        return CommandResult.Ok($"Unsubscribed from depth for {symbol} ({marketType}) on {conn.Name}.");
    }

    // ---- Mark Price ----

    private CommandResult HandleMarkPrice(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (parts.Length < 1)
        {
            IReadOnlyList<string> subs = conn.MarketDataStore.GetMarkPriceSubscriptions();
            if (subs.Count == 0)
            {
                return CommandResult.Ok($"No mark price subscriptions on {conn.Name}.");
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"## Mark Prices — {conn.Name}");
            sb.AppendLine();
            sb.AppendLine("| Symbol Key | Price | Funding Rate | Next Funding |");
            sb.AppendLine("|------------|-------|--------------|--------------|");
            foreach (string key in subs)
            {
                if (conn.MarketDataStore.TryGetMarkPrice(key, out MarkPriceUpdateData mp))
                {
                    string nextFunding = DateTimeOffset.FromUnixTimeMilliseconds(mp.fundingNextTime).UtcDateTime.ToString("HH:mm:ss");
                    sb.AppendLine($"| {key} | {mp.price:F8} | {mp.fundingRate:F6} | {nextFunding} |");
                }
            }
            return CommandResult.Ok(sb.ToString());
        }

        string symbol = parts[0].ToUpperInvariant();
        MarketType marketType = ParseMarketType(parts, 1);
        string mpKey = $"{conn.Profile.Exchange}:{marketType}:{symbol}";

        if (!conn.MarketDataStore.TryGetMarkPrice(mpKey, out MarkPriceUpdateData markPrice))
        {
            return CommandResult.Ok($"No mark price data for {mpKey}. Subscribe first.");
        }

        string funding = DateTimeOffset.FromUnixTimeMilliseconds(markPrice.fundingNextTime).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        return CommandResult.Ok(
            $"**{mpKey}**\nPrice: {markPrice.price:F8}\nFunding Rate: {markPrice.fundingRate:F6}\nNext Funding: {funding}",
            new { price = markPrice.price, fundingRate = markPrice.fundingRate });
    }

    private CommandResult HandleMarkPriceSubscribe(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!conn.IsConnected)
        {
            return CommandResult.Fail($"{conn.Name} is not connected.");
        }

        if (parts.Length < 1)
        {
            return CommandResult.Fail("Usage: marketdata markprice-subscribe <symbol> [FUTURES|SPOT]");
        }

        string symbol = parts[0].ToUpperInvariant();
        MarketType marketType = ParseMarketType(parts, 1);
        conn.SubscribeMarkPrice(conn.Profile.Exchange, marketType, symbol);
        return CommandResult.Ok($"Subscribed to mark price for {symbol} ({marketType}) on {conn.Name}.");
    }

    private CommandResult HandleMarkPriceUnsubscribe(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (parts.Length < 1)
        {
            return CommandResult.Fail("Usage: marketdata markprice-unsubscribe <symbol> [FUTURES|SPOT]");
        }

        string symbol = parts[0].ToUpperInvariant();
        MarketType marketType = ParseMarketType(parts, 1);
        conn.UnsubscribeMarkPrice(conn.Profile.Exchange, marketType, symbol);
        return CommandResult.Ok($"Unsubscribed from mark price for {symbol} ({marketType}) on {conn.Name}.");
    }

    // ---- Klines ----

    private CommandResult HandleKlines(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (parts.Length < 2)
        {
            IReadOnlyList<string> subs = conn.MarketDataStore.GetKlineSubscriptions();
            if (subs.Count == 0)
            {
                return CommandResult.Ok($"No kline subscriptions on {conn.Name}.");
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"## Klines — {conn.Name}");
            sb.AppendLine();
            sb.AppendLine("| Key | Open | Close | High | Low | Vol |");
            sb.AppendLine("|-----|------|-------|------|-----|-----|");
            foreach (string key in subs)
            {
                if (conn.MarketDataStore.TryGetLastKline(key, out KlineUpdateData kline))
                {
                    sb.AppendLine($"| {key} | {kline.openPrice:F4} | {kline.closePrice:F4} | {kline.highPrice:F4} | {kline.lowPrice:F4} | {kline.baseVolume:F2} |");
                }
            }
            return CommandResult.Ok(sb.ToString());
        }

        string symbol = parts[0].ToUpperInvariant();
        KlineInterval interval = ParseKlineInterval(parts[1]);
        MarketType marketType = ParseMarketType(parts, 2);
        string klineKey = $"{conn.Profile.Exchange}:{marketType}:{symbol}:{interval}";

        if (!conn.MarketDataStore.TryGetLastKline(klineKey, out KlineUpdateData kl))
        {
            return CommandResult.Ok($"No kline data for {klineKey}. Subscribe first.");
        }

        string openTime = DateTimeOffset.FromUnixTimeMilliseconds(kl.openTime).UtcDateTime.ToString("yyyy-MM-dd HH:mm");
        string closeTime = DateTimeOffset.FromUnixTimeMilliseconds(kl.closeTime).UtcDateTime.ToString("yyyy-MM-dd HH:mm");

        return CommandResult.Ok(
            $"**{klineKey}**\nOpen: {kl.openPrice:F8} | Close: {kl.closePrice:F8}\nHigh: {kl.highPrice:F8} | Low: {kl.lowPrice:F8}\nVolume: {kl.baseVolume:F4} | Quote Vol: {kl.quoteVolume:F4}\nOpen Time: {openTime} | Close Time: {closeTime}",
            new { open = kl.openPrice, close = kl.closePrice, high = kl.highPrice, low = kl.lowPrice });
    }

    private CommandResult HandleKlinesSubscribe(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!conn.IsConnected)
        {
            return CommandResult.Fail($"{conn.Name} is not connected.");
        }

        if (parts.Length < 2)
        {
            return CommandResult.Fail("Usage: marketdata klines-subscribe <symbol> <1m|5m|15m|1h|4h|1d> [FUTURES|SPOT]");
        }

        string symbol = parts[0].ToUpperInvariant();
        KlineInterval interval = ParseKlineInterval(parts[1]);
        MarketType marketType = ParseMarketType(parts, 2);
        conn.SubscribeKlines(conn.Profile.Exchange, marketType, symbol, interval);
        return CommandResult.Ok($"Subscribed to {interval} klines for {symbol} ({marketType}) on {conn.Name}.");
    }

    private CommandResult HandleKlinesUnsubscribe(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (parts.Length < 2)
        {
            return CommandResult.Fail("Usage: marketdata klines-unsubscribe <symbol> <1m|5m|15m|1h|4h|1d> [FUTURES|SPOT]");
        }

        string symbol = parts[0].ToUpperInvariant();
        KlineInterval interval = ParseKlineInterval(parts[1]);
        MarketType marketType = ParseMarketType(parts, 2);
        conn.UnsubscribeKlines(conn.Profile.Exchange, marketType, symbol, interval);
        return CommandResult.Ok($"Unsubscribed from {interval} klines for {symbol} ({marketType}) on {conn.Name}.");
    }

    // ---- Ticker ----

    private CommandResult HandleTicker(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        IReadOnlyList<string> subs = conn.MarketDataStore.GetTickerSubscriptions();
        if (subs.Count == 0)
        {
            return CommandResult.Ok($"No ticker data on {conn.Name}. Subscribe first.");
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"## Tickers — {conn.Name} ({subs.Count} symbols)");
        sb.AppendLine();
        sb.AppendLine("| Symbol Key | Last Price | Base Vol | Quote Vol |");
        sb.AppendLine("|------------|-----------|----------|-----------|");

        int shown = 0;
        foreach (string key in subs)
        {
            if (conn.MarketDataStore.TryGetTicker(key, out TickerUpdateData ticker))
            {
                sb.AppendLine($"| {key} | {ticker.lastPrice:F8} | {ticker.tradedBaseAsset:F4} | {ticker.tradedQuoteAsset:F2} |");
                shown++;
                if (shown >= 50)
                {
                    sb.AppendLine($"... and {subs.Count - 50} more");
                    break;
                }
            }
        }

        return CommandResult.Ok(sb.ToString(), new { count = subs.Count });
    }

    private CommandResult HandleTickerSubscribe(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!conn.IsConnected)
        {
            return CommandResult.Fail($"{conn.Name} is not connected.");
        }

        MarketType marketType = ParseMarketType(parts, 0);
        conn.SubscribeTicker(conn.Profile.Exchange, marketType);
        return CommandResult.Ok($"Subscribed to tickers ({marketType}) on {conn.Name}.");
    }

    private CommandResult HandleTickerUnsubscribe(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        MarketType marketType = ParseMarketType(parts, 0);
        conn.UnsubscribeTicker(conn.Profile.Exchange, marketType);
        return CommandResult.Ok($"Unsubscribed from tickers ({marketType}) on {conn.Name}.");
    }

    // ---- Helpers ----

    private MarketType ParseMarketType(string[] parts, int index)
    {
        if (index < parts.Length)
        {
            string val = parts[index].ToUpperInvariant();
            if (val == "SPOT")
            {
                return MarketType.SPOT;
            }
            if (val == "MARGIN")
            {
                return MarketType.MARGIN;
            }
            if (val == "DELIVERY")
            {
                return MarketType.DELIVERY;
            }
        }
        return MarketType.FUTURES;
    }

    private KlineInterval ParseKlineInterval(string value)
    {
        switch (value.ToUpperInvariant())
        {
            case "1S": return KlineInterval.S_1;
            case "1M": case "1MIN": return KlineInterval.MIN_1;
            case "3M": case "3MIN": return KlineInterval.MIN_3;
            case "5M": case "5MIN": return KlineInterval.MIN_5;
            case "15M": case "15MIN": return KlineInterval.MIN_15;
            case "30M": case "30MIN": return KlineInterval.MIN_30;
            case "1H": return KlineInterval.H_1;
            case "2H": return KlineInterval.H_2;
            case "4H": return KlineInterval.H_4;
            case "6H": return KlineInterval.H_6;
            case "12H": return KlineInterval.H_12;
            case "1D": return KlineInterval.D_1;
            case "3D": return KlineInterval.D_3;
            case "1W": return KlineInterval.WEEK;
            case "1MO": return KlineInterval.MONTH;
            default: return KlineInterval.MIN_1;
        }
    }

    private CoreConnection? ResolveConnection(string? targetProfile, out CommandResult? error)
    {
        error = null;
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            error = targetProfile != null
                ? CommandResult.Fail($"No connection '{targetProfile}'. Use 'status' to see connections.")
                : CommandResult.Fail("Not connected. Use 'connect <profile>' first.");
            return null;
        }
        return conn;
    }
}
