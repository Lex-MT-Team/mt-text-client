using System;
using System.Collections.Generic;
using System.Text;
using MTShared.Types;
using MTTextClient.Core;
using MTTextClient.Output;
namespace MTTextClient.Commands;

/// <summary>
/// Exchange info commands — trade pairs, prices, market data.
///
/// exchange pairs            — list all trade pairs (first 100)
/// exchange search <query>   — search trade pairs
/// exchange detail <symbol>  — detail view of a specific pair
/// exchange summary          — exchange info summary
/// exchange limits           — API loading percentages
/// exchange ticker24 <symbol> — 24h ticker statistics
/// exchange klines <symbol> [interval] [limit] — candlestick data
/// exchange trades <symbol> [limit] — recent trades
/// </summary>
public sealed class ExchangeCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "exchange";
    public string Description => "Exchange info: trade pairs, prices, ticker, klines, trades";
    public string Usage => "exchange pairs|search|detail|summary|limits|ticker24|klines|trades";

    public ExchangeCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(Usage);
        }

        string? targetProfile = null;
        var cleanArgs = new List<string>();
        foreach (string arg in args)
        {
            if (arg.StartsWith('@'))
            {
                targetProfile = arg[1..];
            }
            else
            {
                cleanArgs.Add(arg);
            }
        }

        string? subCmd = cleanArgs[0].ToLowerInvariant();
        string[]? subArgs = cleanArgs.Count > 1 ? cleanArgs.GetRange(1, cleanArgs.Count - 1).ToArray() : Array.Empty<string>();

        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("Not connected. Use 'connect <profile>' first.");
        }

        if (!conn.IsConnected)
        {
            return CommandResult.Fail($"[{conn.Name}] Not connected.");
        }

        ExchangeInfoStore store = conn.ExchangeInfoStore;

        return subCmd switch
        {
            "pairs" or "list" => ListPairs(conn.Name, store),
            "search" or "find" => SearchPairs(conn.Name, store, subArgs),
            "pair" or "detail" => PairDetail(conn.Name, store, subArgs),
            "summary" or "info" => Summary(conn.Name, store),
            "limits" or "api" => ApiLimits(conn.Name, store),
            "ticker24" or "ticker" => Ticker24(conn, subArgs),
            "klines" or "candles" => Klines(conn, subArgs),
            "trades" or "recent-trades" => Trades(conn, subArgs),
            _ => CommandResult.Fail($"Unknown subcommand: {subCmd}. {Usage}")
        };
    }

    #region Existing Subcommands

    private CommandResult ListPairs(string connName, ExchangeInfoStore store)
    {
        IReadOnlyList<TradePairSnapshot>? pairs = store.GetTradePairs();
        if (pairs.Count == 0)
        {
            return CommandResult.Ok($"[{connName}] No trade pairs loaded yet.");
        }

        Dictionary<MarketType, int>? countsByMarket = store.GetCountsByMarketType();
        string? header = $"[{connName}] {pairs.Count} trade pairs";
        if (countsByMarket.Count > 0)
        {
            header += " (" + FormatMarketCounts(countsByMarket) + ")";
        }

        int displayLimit = Math.Min(pairs.Count, 100);
        TableBuilder display = new TableBuilder("Symbol", "Market", "Status", "Price", "BaseAsset", "QuoteAsset", "MinQty", "StepSize", "TickSize", "MinNotional", "Tradable");
        for (int idx = 0; idx < displayLimit; idx++)
        {
            TradePairSnapshot? p = pairs[idx];
            display.AddRow(
                p.Symbol,
                p.MarketType.ToString(),
                p.Status == SymbolStatusType.TRADING ? "TRADING" : p.Status.ToString(),
                p.TickerPrice > 0 ? FormatPrice(p.TickerPrice) : "—",
                p.BaseAsset,
                p.QuoteAsset,
                $"{p.MinQty}",
                $"{p.StepSize}",
                $"{p.TickSize}",
                p.UseMinNotional ? $"{p.MinNotional}" : "—",
                p.IsTradable ? "Yes" : "No"
            );
        }

        var fullData = new List<object>(pairs.Count);
        foreach (TradePairSnapshot p in pairs)
        {
            fullData.Add(new
            {
                p.Symbol,
                MarketType = p.MarketType.ToString(),
                Status = p.Status.ToString(),
                p.TickerPrice,
                p.BaseAsset,
                p.QuoteAsset,
                p.MarginAsset,
                p.MinQty,
                p.MaxQty,
                p.MarketMaxQty,
                p.StepSize,
                p.StepSizePrecision,
                p.TickSize,
                p.TickSizePrecision,
                p.MinNotional,
                p.MaxNotional,
                p.UseMinNotional,
                p.UseMaxNotional,
                p.ContractSize,
                p.Qav24h,
                p.IsTradable,
            });
        }

        return CommandResult.Ok(
            header + "\n" + display.ToString() +
            (pairs.Count > 100 ? $"\n... and {pairs.Count - 100} more (use 'exchange search' to filter)" : ""),
            new { Server = connName, TotalPairs = pairs.Count, CountsByMarket = ToDictStringInt(countsByMarket), Pairs = fullData });
    }

    private CommandResult SearchPairs(string connName, ExchangeInfoStore store, string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail("Usage: exchange search <query>");
        }

        string? query = args[0];
        IReadOnlyList<TradePairSnapshot>? matches = store.SearchTradePairs(query);

        if (matches.Count == 0)
        {
            return CommandResult.Ok($"[{connName}] No pairs matching '{query}'.");
        }

        int searchLimit = Math.Min(matches.Count, 50);
        TableBuilder display = new TableBuilder("Symbol", "Market", "Status", "Price", "BaseAsset", "QuoteAsset", "MinQty", "TickSize", "Qav24h", "Tradable");
        for (int idx = 0; idx < searchLimit; idx++)
        {
            TradePairSnapshot? p = matches[idx];
            display.AddRow(
                p.Symbol,
                p.MarketType.ToString(),
                p.Status.ToString(),
                p.TickerPrice > 0 ? FormatPrice(p.TickerPrice) : "—",
                p.BaseAsset,
                p.QuoteAsset,
                $"{p.MinQty}",
                $"{p.TickSize}",
                p.Qav24h > 0 ? $"{p.Qav24h:N0}" : "—",
                p.IsTradable ? "Yes" : "No"
            );
        }

        var fullData = new List<object>(matches.Count);
        foreach (TradePairSnapshot p in matches)
        {
            fullData.Add(new
            {
                p.Symbol,
                MarketType = p.MarketType.ToString(),
                Status = p.Status.ToString(),
                p.TickerPrice,
                p.BaseAsset,
                p.QuoteAsset,
                p.MinQty,
                p.MaxQty,
                p.StepSize,
                p.StepSizePrecision,
                p.TickSize,
                p.TickSizePrecision,
                p.MinNotional,
                p.Qav24h,
                p.IsTradable,
            });
        }

        return CommandResult.Ok(
            $"[{connName}] {matches.Count} pairs matching '{query}':\n" + display.ToString(),
            new { Server = connName, Query = query, Count = matches.Count, Pairs = fullData });
    }

    private CommandResult PairDetail(string connName, ExchangeInfoStore store, string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail("Usage: exchange detail <symbol>");
        }

        string? symbol = args[0].ToUpperInvariant();
        TradePairSnapshot? pair = store.GetTradePair(symbol);

        if (pair == null)
        {
            return CommandResult.Fail($"[{connName}] Trade pair '{symbol}' not found.");
        }

        var details = new
        {
            pair.Symbol,
            MarketType = pair.MarketType.ToString(),
            Status = pair.Status.ToString(),
            pair.BaseAsset,
            pair.QuoteAsset,
            pair.MarginAsset,
            TickerPrice = pair.TickerPrice > 0 ? FormatPrice(pair.TickerPrice) : "N/A",
            Qav24h = pair.Qav24h > 0 ? $"{pair.Qav24h:N0}" : "N/A",
            IsTradable = pair.IsTradable,
            QuantityRules = new
            {
                pair.MinQty,
                pair.MaxQty,
                pair.MarketMaxQty,
                pair.StepSize,
                pair.StepSizePrecision,
                pair.ContractSize,
            },
            PriceRules = new
            {
                pair.MinPrice,
                pair.MaxPrice,
                pair.TickSize,
                pair.TickSizePrecision,
                pair.MultiplierUp,
                pair.MultiplierDown,
            },
            NotionalRules = new
            {
                pair.MinNotional,
                pair.MaxNotional,
                pair.UseMinNotional,
                pair.UseMaxNotional,
            }
        };

        string? text = $"[{connName}] {symbol} Detail:\n" +
            $"  Market: {pair.MarketType} | Status: {pair.Status} | Tradable: {pair.IsTradable}\n" +
            $"  Base: {pair.BaseAsset} | Quote: {pair.QuoteAsset} | Margin: {pair.MarginAsset}\n" +
            $"  Price: {(pair.TickerPrice > 0 ? FormatPrice(pair.TickerPrice) : "N/A")} | 24h Volume: {(pair.Qav24h > 0 ? $"{pair.Qav24h:N0}" : "N/A")}\n" +
            $"  Qty: {pair.MinQty} — {pair.MaxQty} (step: {pair.StepSize}, precision: {pair.StepSizePrecision})\n" +
            $"  Price: {pair.MinPrice} — {pair.MaxPrice} (tick: {pair.TickSize}, precision: {pair.TickSizePrecision})\n" +
            $"  Notional: {(pair.UseMinNotional ? $"min {pair.MinNotional}" : "no min")} | {(pair.UseMaxNotional ? $"max {pair.MaxNotional}" : "no max")}\n" +
            $"  Contract: {pair.ContractSize} | Market Max: {pair.MarketMaxQty}";

        return CommandResult.Ok(text, details);
    }

    private CommandResult Summary(string connName, ExchangeInfoStore store)
    {
        IReadOnlyList<TradePairSnapshot>? pairs = store.GetTradePairs();
        Dictionary<MarketType, int>? countsByMarket = store.GetCountsByMarketType();
        IReadOnlyDictionary<MarketType, short>? apiLoading = store.GetApiLoading();

        int tradable = 0;
        foreach (TradePairSnapshot p in pairs)
        {
            if (p.IsTradable)
            {
                tradable++;
            }
        }

        int withPrice = 0;
        foreach (TradePairSnapshot p in pairs)
        {
            if (p.TickerPrice > 0)
            {
                withPrice++;
            }
        }

        var summary = new
        {
            Server = connName,
            TotalPairs = pairs.Count,
            TradablePairs = tradable,
            PairsWithPrice = withPrice,
            CountsByMarket = ToDictStringInt(countsByMarket),
            ApiLoading = ToDictStringIntFromLoading(apiLoading),
            LastTradePairUpdate = store.LastTradePairUpdate,
            LastPriceUpdate = store.LastPriceUpdate,
            LastApiLimitUpdate = store.LastApiLimitUpdate,
        };

        string? text = $"[{connName}] Exchange Summary:\n" +
            $"  Total Pairs: {pairs.Count} (tradable: {tradable}, with price: {withPrice})\n" +
            $"  Markets: {FormatMarketCounts(countsByMarket)}\n" +
            $"  API Loading: {(apiLoading.Count > 0 ? FormatApiLoading(apiLoading) : "N/A")}\n" +
            $"  Last pair update: {store.LastTradePairUpdate:HH:mm:ss}\n" +
            $"  Last price update: {store.LastPriceUpdate:HH:mm:ss}";

        return CommandResult.Ok(text, summary);
    }

    private CommandResult ApiLimits(string connName, ExchangeInfoStore store)
    {
        IReadOnlyDictionary<MarketType, short>? apiLoading = store.GetApiLoading();
        if (apiLoading.Count == 0)
        {
            return CommandResult.Ok($"[{connName}] No API loading data available.");
        }

        TableBuilder data = new TableBuilder("Market", "Loading", "Status");
        foreach (KeyValuePair<MarketType, short> kvp in apiLoading)
        {
            data.AddRow(
                kvp.Key.ToString(),
                $"{kvp.Value}%",
                kvp.Value > 90 ? "⚠ HIGH" : kvp.Value > 70 ? "⚠ WARN" : "OK"
            );
        }

        return CommandResult.Ok(
            $"[{connName}] API Loading:\n" + data.ToString(),
            new { Server = connName, Loading = ToDictStringIntFromLoading(apiLoading) });
    }

    #endregion

    #region Phase K: New Market Data Commands

    private CommandResult Ticker24(CoreConnection conn, string[] args)
    {
        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: exchange ticker24 <symbol> [market_type]\n  market_type: FUTURES (default) or SPOT");
        }

        string? symbol = args[0].ToUpperInvariant();
        MarketType marketType = MarketType.FUTURES;

        if (args.Length >= 2 && Enum.TryParse<MarketType>(args[1].ToUpperInvariant(), out MarketType mt))
        {
            marketType = mt;
        }

        // Auto-detect market type from exchange info
        TradePairSnapshot? pairInfo = conn.ExchangeInfoStore.GetTradePair(symbol);
        if (pairInfo != null)
        {
            marketType = pairInfo.MarketType;
        }

        MTShared.Network.TickerPrice24ListData? result = conn.RequestTicker24(marketType, symbol);

        if (result == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Ticker24 for {symbol}: request timed out.");
        }

        if (result.tickerPriceList == null || result.tickerPriceList.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] No ticker data for {symbol}.");
        }

        var tickers = new List<object>(result.tickerPriceList.Count);
        foreach (MTShared.Network.TickerPrice24UpdateData t in result.tickerPriceList)
        {
            tickers.Add(new
            {
                Symbol = t.symbol ?? symbol,
                LastPrice = t.lastPrice,
                PriceChange = t.priceChange,
                PriceChangePct = $"{t.priceChangePercent:F2}%",
                OpenPrice = t.openPrice,
                HighPrice = t.highPrice,
                LowPrice = t.lowPrice,
                Volume = t.volume,
                QuoteVolume = t.quoteVolume,
                WeightedAvg = t.weightedAvgPrice,
                TradeCount = t.count,
            });
        }

        MTShared.Network.TickerPrice24UpdateData? first = result.tickerPriceList[0];
        string? text = $"[{conn.Name}] 24h Ticker for {symbol}:\n" +
            $"  Last Price: {first.lastPrice} (change: {first.priceChange:+0.##;-0.##} / {first.priceChangePercent:+0.##;-0.##}%)\n" +
            $"  Open: {first.openPrice} | High: {first.highPrice} | Low: {first.lowPrice}\n" +
            $"  Volume: {first.volume:N2} | Quote Volume: {first.quoteVolume:N2}\n" +
            $"  Trades: {first.count:N0}";

        return CommandResult.Ok(text, new { Server = conn.Name, Symbol = symbol, MarketType = marketType.ToString(), Tickers = tickers });
    }

    private CommandResult Klines(CoreConnection conn, string[] args)
    {
        if (args.Length < 1)
        {
            return CommandResult.Fail(
                "Usage: exchange klines <symbol> [interval] [limit]\n" +
                "  interval: 1m,3m,5m,15m,30m,1h,2h,4h,6h,12h,1d,3d,1w,1M (default: 1h)\n" +
                "  limit: 1-1000 (default: 100)");
        }

        string? symbol = args[0].ToUpperInvariant();
        KlineInterval interval = KlineInterval.H_1; // default 1 hour
        short limit = 100;

        if (args.Length >= 2)
        {
            interval = ParseKlineInterval(args[1]) ?? KlineInterval.H_1;
        }
        if (args.Length >= 3 && short.TryParse(args[2], out short lim))
        {
            limit = (short)Math.Clamp((int)lim, 1, 1000);
        }

        MarketType marketType = MarketType.FUTURES;
        TradePairSnapshot? pairInfo = conn.ExchangeInfoStore.GetTradePair(symbol);
        if (pairInfo != null)
        {
            marketType = pairInfo.MarketType;
        }

        MTShared.Network.KlineListData? result = conn.RequestKlines(marketType, symbol, interval, limit);

        if (result == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Klines for {symbol}: request timed out.");
        }

        if (result.klines == null || result.klines.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] No kline data for {symbol}.");
        }

        var klines = new List<object>(result.klines.Count);
        foreach (MTShared.Network.KlineUpdateData k in result.klines)
        {
            klines.Add(new
            {
                Time = DateTimeOffset.FromUnixTimeMilliseconds(k.openTime).UtcDateTime.ToString("yyyy-MM-dd HH:mm"),
                Open = k.openPrice,
                High = k.highPrice,
                Low = k.lowPrice,
                Close = k.closePrice,
                Volume = k.baseVolume,
                QuoteVol = k.quoteVolume,
            });
        }

        return CommandResult.Ok(
            $"[{conn.Name}] {klines.Count} klines for {symbol} ({interval}):",
            new { Server = conn.Name, Symbol = symbol, Interval = interval.ToString(), Count = klines.Count, Klines = klines });
    }

    private CommandResult Trades(CoreConnection conn, string[] args)
    {
        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: exchange trades <symbol>");
        }

        string? symbol = args[0].ToUpperInvariant();
        MarketType marketType = MarketType.FUTURES;
        TradePairSnapshot? pairInfo = conn.ExchangeInfoStore.GetTradePair(symbol);
        if (pairInfo != null)
        {
            marketType = pairInfo.MarketType;
        }

        (MTShared.Network.TradeListData? data, MTShared.Network.NotificationCode code) = conn.RequestTrades(marketType, symbol);

        if (data == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Trades for {symbol}: request timed out or failed (code: {code}).");
        }

        if (data.trades == null || data.trades.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] No recent trades for {symbol}.");
        }

        var trades = new List<object>(data.trades.Count);
        foreach (MTShared.Network.TradeUpdateData t in data.trades)
        {
            trades.Add(new
            {
                Time = DateTimeOffset.FromUnixTimeMilliseconds(t.tradeTime).UtcDateTime.ToString("HH:mm:ss.fff"),
                Price = t.price,
                Qty = t.quantity,
                Side = t.isBuyerMaker ? "SELL" : "BUY"
            });
        }

        return CommandResult.Ok(
            $"[{conn.Name}] {trades.Count} recent trades for {symbol}:",
            new { Server = conn.Name, Symbol = symbol, Count = trades.Count, Trades = trades });
    }

    private static KlineInterval? ParseKlineInterval(string input)
    {
        return input.ToLowerInvariant() switch
        {
            "1s" => KlineInterval.S_1,
            "1m" or "1min" => KlineInterval.MIN_1,
            "3m" or "3min" => KlineInterval.MIN_3,
            "5m" or "5min" => KlineInterval.MIN_5,
            "15m" or "15min" => KlineInterval.MIN_15,
            "30m" or "30min" => KlineInterval.MIN_30,
            "1h" => KlineInterval.H_1,
            "2h" => KlineInterval.H_2,
            "4h" => KlineInterval.H_4,
            "6h" => KlineInterval.H_6,
            "12h" => KlineInterval.H_12,
            "1d" => KlineInterval.D_1,
            "3d" => KlineInterval.D_3,
            "1w" or "week" => KlineInterval.WEEK,
            "1M" or "month" => KlineInterval.MONTH,
            _ => null
        };
    }

    #endregion

    #region Formatting Helpers

    private static string FormatPrice(double value) =>
        value switch
        {
            >= 1000 => $"{value:F2}",
            >= 1 => $"{value:F4}",
            >= 0.001 => $"{value:F6}",
            _ => $"{value:G6}"
        };



    #endregion

    private static string FormatMarketCounts(IReadOnlyDictionary<MarketType, int> counts)
    {
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (KeyValuePair<MarketType, int> kvp in counts)
        {
            if (!first)
            {
                sb.Append(", ");
            }

            sb.Append($"{kvp.Key}: {kvp.Value}");
            first = false;
        }
        return sb.ToString();
    }

    private static string FormatApiLoading(IReadOnlyDictionary<MarketType, short> loading)
    {
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (KeyValuePair<MarketType, short> kvp in loading)
        {
            if (!first)
            {
                sb.Append(", ");
            }

            sb.Append($"{kvp.Key}: {kvp.Value}%");
            first = false;
        }
        return sb.ToString();
    }

    private static Dictionary<string, int> ToDictStringInt(IReadOnlyDictionary<MarketType, int> source)
    {
        var result = new Dictionary<string, int>(source.Count);
        foreach (KeyValuePair<MarketType, int> kvp in source)
        {
            result[kvp.Key.ToString()] = kvp.Value;
        }

        return result;
    }

    private static Dictionary<string, int> ToDictStringIntFromLoading(IReadOnlyDictionary<MarketType, short> source)
    {
        var result = new Dictionary<string, int>(source.Count);
        foreach (KeyValuePair<MarketType, short> kvp in source)
        {
            result[kvp.Key.ToString()] = (int)kvp.Value;
        }

        return result;
    }

}
