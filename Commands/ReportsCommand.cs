using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using MTShared.Network;
using MTShared.Types;
using MTTextClient.Core;
using MTTextClient.Output;
using MTShared.LiveMarket;
namespace MTTextClient.Commands;

/// <summary>
/// Reports commands — query historical trade reports (closed positions) from MT-Core.
/// These are COMPLETED trades with full P&L, fees, entry/exit prices — the trading history.
///
/// Usage:
///   reports [@profile] [today|24h|7d|30d|90d]
///   reports [@profile] --from YYYY-MM-DD --to YYYY-MM-DD
///   reports [@profile] --symbol BTCUSDT
///   reports [@profile] --algo "Markets Saver"
///   reports [@profile] --sig MS
///   reports [@profile] --exclude-emulated
///   reports [@profile] --closed-by TP
///   reports [@profile] --market FUTURES
///   reports [@profile] --side BUY
///   reports [@profile] --mode REAL
///
/// Phase H: Added B6 (more filters), B7 (more fields), E1/E2 (null safety).
/// </summary>
public sealed class ReportsCommand : ICommand
{
    private readonly ConnectionManager _manager;
    private readonly ReportStore _reportStore;

    public string Name => "reports";
    public string Description => "Trade reports: closed positions, P&L history, trading statistics";
    public string Usage => "reports [@profile] [today|24h|7d|30d|90d] [--from DATE] [--to DATE] " +
        "[--symbol SYM] [--algo NAME] [--sig SIG] [--metrics] " +
        "[--exclude-emulated] [--closed-by TYPE] [--market TYPE] [--side BUY|SELL] [--mode REAL|EMULATED]\n" +
        "  reports export [@profile|--all] [filters...] [--path FILE] — Export to CSV\n" +
        "  reports store <name> [@profile] [filters...] — Save results locally\n" +
        "  reports stored — List saved report sets\n" +
        "  reports load <name> — Display a stored set\n" +
        "  reports delete <name> — Delete a stored set";

    public ReportsCommand(ConnectionManager manager, ReportStore reportStore)
    {
        _manager = manager;
        _reportStore = reportStore;
    }

    public CommandResult Execute(string[] args)
    {
        // Parse @profile from any position in args
        string? profileName = null;
        var cleanArgs = new List<string>();
        foreach (string arg in args)
        {
            if (arg.StartsWith('@'))
            {
                profileName = arg[1..];
            }
            else
            {
                cleanArgs.Add(arg);
            }
        }

        // Subcommands that don't require a connection
        if (cleanArgs.Count > 0)
        {
            string? firstArg = cleanArgs[0].ToLowerInvariant();

            if (firstArg == "stored")
            {
                return HandleStored();
            }

            if (firstArg == "load")
            {
                if (cleanArgs.Count < 2)
                {
                    return CommandResult.Fail("Usage: reports load <name>");
                }

                return HandleLoad(cleanArgs[1]);
            }

            if (firstArg == "delete")
            {
                if (cleanArgs.Count < 2)
                {
                    return CommandResult.Fail("Usage: reports delete <name>");
                }

                return HandleDelete(cleanArgs[1]);
            }

            if (firstArg == "export" && HasFlag(cleanArgs, "--all"))
            {
                return HandleFleetExport(cleanArgs.GetRange(1, cleanArgs.Count - 1).ToArray());
            }

            if (firstArg == "store" && HasFlag(cleanArgs, "--all"))
            {
                return HandleFleetStore(cleanArgs.GetRange(1, cleanArgs.Count - 1).ToArray());
            }
        }

        CoreConnection? conn = ResolveConnection(profileName);
        if (conn == null)
        {
            return CommandResult.Fail(profileName != null
                ? $"Connection '{profileName}' not found. Use 'status' to see connections."
                : "No active connection. Use 'connect <profile>' first.");
        }

        if (!conn.IsConnected)
        {
            return CommandResult.Fail($"Connection '{conn.Name}' is not connected.");
        }

        // Phase K: Check for subcommands (comments, dates)
        if (cleanArgs.Count > 0)
        {
            string? firstArg = cleanArgs[0].ToLowerInvariant();
            if (firstArg == "comments")
            {
                return GetReportComments(conn);
            }

            if (firstArg == "dates")
            {
                return GetReportDates(conn);
            }

            if (firstArg == "export")
            {
                return HandleExport(conn, cleanArgs.GetRange(1, cleanArgs.Count - 1).ToArray());
            }

            if (firstArg == "store")
            {
                return HandleStore(conn, cleanArgs.GetRange(1, cleanArgs.Count - 1).ToArray());
            }
        }
        // Parse time range and filters
        long unixTo = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long unixFrom = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();

        string symbolFilter = "";
        string algoFilter = "";
        string sigFilter = "";
        string rangeLabel = "last 24h";
        bool includeMetrics = false;

        // B6: Additional filters
        bool excludeEmulated = false;
        string? closedByFilter = null;
        string? marketTypeFilter = null;
        string? sideFilter = null;
        string? tradeModeFilter = null;

        for (int i = 0; i < cleanArgs.Count; i++)
        {
            string? a = cleanArgs[i].ToLowerInvariant();
            switch (a)
            {
                case "today":
                    unixFrom = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
                    rangeLabel = "today";
                    break;
                case "24h":
                    unixFrom = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();
                    rangeLabel = "last 24h";
                    break;
                case "7d":
                    unixFrom = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds();
                    rangeLabel = "last 7 days";
                    break;
                case "30d":
                    unixFrom = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
                    rangeLabel = "last 30 days";
                    break;
                case "90d":
                    unixFrom = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds();
                    rangeLabel = "last 90 days";
                    break;
                case "--from":
                    if (i + 1 < cleanArgs.Count && DateTimeOffset.TryParse(cleanArgs[i + 1], out DateTimeOffset fromDt))
                    {
                        unixFrom = fromDt.ToUnixTimeMilliseconds();
                        rangeLabel = $"from {cleanArgs[i + 1]}";
                        i++;
                    }
                    break;
                case "--to":
                    if (i + 1 < cleanArgs.Count && DateTimeOffset.TryParse(cleanArgs[i + 1], out DateTimeOffset toDt))
                    {
                        unixTo = toDt.ToUnixTimeMilliseconds();
                        rangeLabel += $" to {cleanArgs[i + 1]}";
                        i++;
                    }
                    break;
                case "--symbol":
                    if (i + 1 < cleanArgs.Count) { symbolFilter = cleanArgs[++i]; }
                    break;
                case "--algo":
                    if (i + 1 < cleanArgs.Count) { algoFilter = cleanArgs[++i]; }
                    break;
                case "--sig":
                    if (i + 1 < cleanArgs.Count) { sigFilter = cleanArgs[++i]; }
                    break;
                case "--metrics":
                    includeMetrics = true;
                    break;
                case "--exclude-emulated":
                    excludeEmulated = true;
                    break;
                case "--closed-by":
                    if (i + 1 < cleanArgs.Count) { closedByFilter = cleanArgs[++i]; }
                    break;
                case "--market":
                    if (i + 1 < cleanArgs.Count) { marketTypeFilter = cleanArgs[++i]; }
                    break;
                case "--side":
                    if (i + 1 < cleanArgs.Count) { sideFilter = cleanArgs[++i]; }
                    break;
                case "--mode":
                    if (i + 1 < cleanArgs.Count) { tradeModeFilter = cleanArgs[++i]; }
                    break;
            }
        }

        // B6: Parse advanced filter enums for the request
        List<ReportClosedByType>? closedByList = ParseClosedByFilter(closedByFilter);
        List<MarketType>? marketTypes = ParseMarketTypeFilter(marketTypeFilter);
        List<OrderSideType>? orderSideTypes = ParseOrderSideFilter(sideFilter);
        TradeModeType tradeModeType = ParseTradeModeType(tradeModeFilter);

        // Request reports from MT-Core (with extended filters)
        ReportListData? reportList = conn.RequestReports(
            unixFrom, unixTo, symbolFilter, algoFilter, sigFilter,
            includeMetrics, excludeEmulated, closedByList, marketTypes, orderSideTypes, tradeModeType);

        if (reportList == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Report request timed out or failed.");
        }

        List<ReportData>? reports = reportList.reports;
        if (reports == null || reports.Count == 0)
        {
            string? msg = $"[{conn.Name}] No trades found for {rangeLabel}.";
            if (!string.IsNullOrEmpty(symbolFilter))
            {
                msg += $" Symbol: {symbolFilter}";
            }

            if (!string.IsNullOrEmpty(algoFilter))
            {
                msg += $" Algo: {algoFilter}";
            }

            if (!string.IsNullOrEmpty(sigFilter))
            {
                msg += $" Sig: {sigFilter}";
            }

            if (excludeEmulated)
            {
                msg += " (emulated excluded)";
            }

            return CommandResult.Ok(msg);
        }

        // Sort by close time descending
        var sorted = new List<ReportData>(reports);
        sorted.Sort((a, b) => b.reportTime.CompareTo(a.reportTime));

        // Summary statistics
        double totalPnlUsdt = 0;
        foreach (ReportData r in sorted)
        {
            totalPnlUsdt += r.totalUSDT;
        }

        double grossPnlUsdt = 0;
        foreach (ReportData r in sorted)
        {
            grossPnlUsdt += r.profitUSDT;
        }

        double totalFees = 0;
        foreach (ReportData r in sorted)
        {
            totalFees += r.commissionUSDT;
        }

        int wins = 0;
        foreach (ReportData r in sorted)
        {
            if (r.totalUSDT > 0)
            {
                wins++;
            }
        }

        int losses = 0;
        foreach (ReportData r in sorted)
        {
            if (r.totalUSDT < 0)
            {
                losses++;
            }
        }

        int breakeven = 0;
        foreach (ReportData r in sorted)
        {
            if (r.totalUSDT == 0)
            {
                breakeven++;
            }
        }

        double winRate = sorted.Count > 0 ? (double)wins / sorted.Count * 100 : 0;
        double avgPnl = sorted.Count > 0 ? totalPnlUsdt / sorted.Count : 0;
        double bestTrade = 0;
        if (sorted.Count > 0) { bestTrade = sorted[0].totalUSDT; foreach (ReportData r in sorted)
            {
                if (r.totalUSDT > bestTrade)
                {
                    bestTrade = r.totalUSDT;
                }
            }
        }
        double worstTrade = 0;
        if (sorted.Count > 0) { worstTrade = sorted[0].totalUSDT; foreach (ReportData r in sorted)
            {
                if (r.totalUSDT < worstTrade)
                {
                    worstTrade = r.totalUSDT;
                }
            }
        }
        double totalVolume = 0;
        foreach (ReportData r in sorted)
        {
            totalVolume += r.executedQtyUSDT;
        }

        double avgOrderSize = sorted.Count > 0 ? totalVolume / sorted.Count : 0;

        // Group by algo signature
        var algoGroups = new Dictionary<string, (int Trades, double PnL, int Wins)>();
        foreach (ReportData r in sorted)
        {
            string key = string.IsNullOrEmpty(r.orderInfo.signature) || r.orderInfo.signature == "00"
                ? "Manual"
                : r.orderInfo.signature;
            if (!algoGroups.TryGetValue(key, out (int Trades, double PnL, int Wins) grp))
            {
                grp = (0, 0, 0);
            }

            grp.Trades++;
            grp.PnL += r.totalUSDT;
            if (r.totalUSDT > 0)
            {
                grp.Wins++;
            }

            algoGroups[key] = grp;
        }
        var byAlgo = new List<(string Algo, int Trades, double PnL, double WinRate)>(algoGroups.Count);
        foreach (KeyValuePair<string, (int Trades, double PnL, int Wins)> kvp in algoGroups)
        {
            double wr = kvp.Value.Trades > 0 ? (double)kvp.Value.Wins / kvp.Value.Trades * 100 : 0;
            byAlgo.Add((kvp.Key, kvp.Value.Trades, kvp.Value.PnL, wr));
        }
        byAlgo.Sort((a, b) => Math.Abs(b.PnL).CompareTo(Math.Abs(a.PnL)));

        // B7: Build trade table (top 50) with expanded fields
        int rowLimit = Math.Min(sorted.Count, 50);
        TableBuilder rows = new TableBuilder("Close", "Symbol", "Side", "Entry", "Exit", "PnL", "Gross", "Fee", "ROE", "Market", "ClosedBy", "Emu", "Algo");
        for (int idx = 0; idx < rowLimit; idx++)
        {
            ReportData? r = sorted[idx];
            rows.AddRow(
                DateTimeOffset.FromUnixTimeMilliseconds(r.reportTime).ToString("MM/dd HH:mm"),
                Trunc(r.symbol, 12),
                r.orderSideType == OrderSideType.BUY ? "BUY" : "SELL",
                FormatPrice(r.priceOpen),
                FormatPrice(r.priceClose),
                FormatPnl(r.totalUSDT),
                FormatPnl(r.profitUSDT),
                $"{r.commissionUSDT:F2}",
                $"{r.profitPercentage:+0.00;-0.00}%",
                r.marketType.ToString(),
                r.closedBy.ToString(),
                r.isEmulated ? "E" : "",
                Trunc(r.orderInfo.signature ?? "Manual", 8)
            );
        }

        // B7: Build structured per-trade array for MCP data (ALL trades, not just top 50)
        // E1/E2: Null-safe access to orderInfo fields
        var tradeList = new List<object>(sorted.Count);
        foreach (ReportData r in sorted)
        {
            tradeList.Add(new
            {
                ReportId = r.id,
                OpenTime = r.reportOpenTime,
                CloseUnix = r.reportTime,
                Symbol = r.symbol,
                Side = r.orderSideType == OrderSideType.BUY ? "BUY" : "SELL",
                MarketType = r.marketType.ToString(),
                EntryPrice = r.priceOpen,
                ExitPrice = r.priceClose,
                PriceDelta = Math.Round((double)r.priceDelta, 6),
                PnL = Math.Round(r.totalUSDT, 4),
                GrossPnL = Math.Round(r.profitUSDT, 4),
                Fees = Math.Round(r.commissionUSDT, 4),
                ROE = Math.Round((double)r.profitPercentage, 2),
                OrderSize = Math.Round(r.executedQtyUSDT, 2),
                ClosedBy = r.closedBy.ToString(),
                IsEmulated = r.isEmulated,
                AlgoSignature = r.orderInfo.signature ?? "Manual",
                AlgoId = r.orderInfo.algorithmId,
                AlgoName = r.orderInfo.AlgorithmInfo.name ?? "",
                AlgoGroupType = r.orderInfo.algorithmGroupType.ToString(),
                IsManualOrder = r.orderInfo.IsManualOrder,
                IsAlgoOrder = r.orderInfo.IsAlgoOrder,
            });
        }

        string? header = $"[{conn.Name}] Trade Reports — {rangeLabel} | {sorted.Count} trades";
        string? table = rows.ToString();

        // Per-algo breakdown
        TableBuilder algoRows = new TableBuilder("Algo", "Trades", "PnL", "WinRate");
        int algoCount = 0;
        foreach ((string Algo, int Trades, double PnL, double WinRate) a in byAlgo)
        {
            algoRows.AddRow(
                Trunc(a.Algo, 10),
                a.Trades.ToString(),
                FormatPnl(a.PnL),
                $"{a.WinRate:F0}%"
            );
            algoCount++;
        }

        string? algoTable = algoCount > 1 ? "\n--- By Algorithm ---\n" + algoRows.ToString() : "";

        // Extract condensed market context if metrics were requested
        object? metricsContext = null;
        if (includeMetrics && sorted.Count > 0)
        {
            var metricsList = new List<Dictionary<string, object>>(Math.Min(sorted.Count, 50));
            int metricsLimit = Math.Min(sorted.Count, 50);
            for (int mIdx = 0; mIdx < metricsLimit; mIdx++)
            {
                ReportData? r = sorted[mIdx];
                var ctx = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["symbol"] = r.symbol,
                    ["closeTime"] = r.reportTime,
                    ["depthVolume"] = r.depthVolume,
                    ["distanceAtOrder"] = r.distanceAtOrder,
                };

                // Extract trigger/fill metrics from deltas dictionary
                if (r.deltas != null)
                {
                    foreach (KeyValuePair<MarkerEventType, LiveMarketMetricsData> kvp in r.deltas)
                    {
                        string prefix = kvp.Key.ToString();
                        LiveMarketMetricsData md = kvp.Value;
                        if (md?.metrics != null)
                        {
                            LiveMarketMetrics m = md.metrics;
                            ctx[$"{prefix}_coreTime"] = md.CoreTime;
                            ctx[$"{prefix}_tickerPrice"] = m.TickerPrice;
                            ctx[$"{prefix}_markPrice"] = m.MarkPrice;
                            ctx[$"{prefix}_markPricePct"] = m.MarkPricePercent;
                            ctx[$"{prefix}_fundingRate"] = m.NextFundingRate;
                            ctx[$"{prefix}_lastPrice"] = m.LastPrice;
                            ctx[$"{prefix}_price2sAgo"] = m.Price2SecAgo;
                            ctx[$"{prefix}_qav24h"] = m.Qav24H;

                            var keyTfNames = new HashSet<string> { "SEC5", "SEC30", "MIN1", "MIN5", "MIN15", "HOUR1", "HOUR24" };
                            foreach (TimeFrame val in Enum.GetValues(typeof(TimeFrame)))
                            {
                                string tfName = val.ToString();
                                if (!keyTfNames.Contains(tfName))
                                {
                                    continue;
                                }

                                try
                                {
                                    LiveMarketMetricsRecord rec = m.GetMetrics(val);
                                    ctx[$"{prefix}_{tfName}_avgPrice"] = rec.avgPrice;
                                    ctx[$"{prefix}_{tfName}_delta"] = Math.Round(rec.delta * 100, 4);
                                    ctx[$"{prefix}_{tfName}_rDelta"] = Math.Round(rec.rDelta * 100, 4);
                                    ctx[$"{prefix}_{tfName}_avgBaseVol"] = rec.avgBaseVolume;
                                    ctx[$"{prefix}_{tfName}_avgQuoteVol"] = rec.avgQuoteVolume;
                                }
                                catch { /* skip if GetMetrics doesn't accept this value */ }
                            }
                        }
                    }
                }
                metricsList.Add(ctx);
            }
            metricsContext = metricsList;
        }

        // B7: Enhanced summary data for MCP with all new fields
        var summaryData = new
        {
            Server = conn.Name,
            Exchange = conn.Profile.Exchange.ToString(),
            Period = rangeLabel,
            FromUnix = unixFrom,
            ToUnix = unixTo,
            TotalTrades = sorted.Count,
            Wins = wins,
            Losses = losses,
            Breakeven = breakeven,
            WinRate = Math.Round(winRate, 1),
            TotalPnlUSDT = Math.Round(totalPnlUsdt, 2),
            GrossPnlUSDT = Math.Round(grossPnlUsdt, 2),
            TotalFeesUSDT = Math.Round(totalFees, 2),
            AvgPnlPerTrade = Math.Round(avgPnl, 2),
            AvgOrderSizeUSDT = Math.Round(avgOrderSize, 2),
            BestTradeUSDT = Math.Round(bestTrade, 2),
            WorstTradeUSDT = Math.Round(worstTrade, 2),
            TotalVolumeUSDT = Math.Round(totalVolume, 2),
            UniqueSymbols = CountUniqueSymbols(sorted),
            TopSymbols = GetTopSymbols(sorted, 10),
            EmulatedCount = CountEmulated(sorted),
            RealCount = sorted.Count - CountEmulated(sorted),
            UniqueMarketTypes = GetUniqueMarketTypes(sorted),
            MarketMetrics = metricsContext,
            AlgoBreakdown = FormatAlgoBreakdown(byAlgo),
            TradeList = tradeList,
        };

        string? statsBlock = "\n--- Summary ---\n" +
            $"  Trades: {sorted.Count} (W:{wins} / L:{losses} / BE:{breakeven}) | Win Rate: {winRate:F1}%\n" +
            $"  Total PnL: {FormatPnl(totalPnlUsdt)} | Gross: {FormatPnl(grossPnlUsdt)} | Fees: ${totalFees:F2}\n" +
            $"  Volume: ${totalVolume:N0} | Avg Size: ${avgOrderSize:F2} | Avg PnL: {FormatPnl(avgPnl)}\n" +
            $"  Best: {FormatPnl(bestTrade)} | Worst: {FormatPnl(worstTrade)}\n" +
            $"  Symbols: {summaryData.UniqueSymbols} ({summaryData.TopSymbols})\n" +
            $"  Real: {summaryData.RealCount} | Emulated: {summaryData.EmulatedCount}";

        return CommandResult.Ok(
            header + "\n" + table + statsBlock + algoTable,
            summaryData);
    }

    // ── B6: Filter parsing helpers ──────────────────────────

    private static List<ReportClosedByType>? ParseClosedByFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var result = new List<ReportClosedByType>();
        foreach (string part in filter.Split(','))
        {
            string trimmed = part.Trim().ToUpperInvariant();
            ReportClosedByType? matched = trimmed switch
            {
                "TP" or "TAKE_PROFIT" => ReportClosedByType.TAKE_PROFIT,
                "SL" or "STOP_LOSS" => ReportClosedByType.STOP_LOSS,
                "TS" or "TRAILING_STOP" => ReportClosedByType.TRAILING_STOP,
                "LIQ" or "LIQUIDATION" => ReportClosedByType.LIQUIDATION,
                "PANIC" or "PANIC_SELL" => ReportClosedByType.PANIC_SELL,
                "AUTO" or "AUTO_STOP" => ReportClosedByType.AUTO_STOP,
                "MARKET" => ReportClosedByType.MARKET,
                "LIMIT" => ReportClosedByType.LIMIT,
                "FUNDING" or "FUNDING_FEE" => ReportClosedByType.FUNDING_FEE,
                "LICENSE" or "LICENSE_AUTO_STOP" => ReportClosedByType.LICENSE_AUTO_STOP,
                _ => (ReportClosedByType?)null
            };
            if (matched.HasValue)
            {
                result.Add(matched.Value);
            }
        }
        return result.Count > 0 ? result : null;
    }

    private static List<MarketType>? ParseMarketTypeFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var result = new List<MarketType>();
        foreach (string part in filter.Split(','))
        {
            if (Enum.TryParse<MarketType>(part.Trim(), true, out MarketType mt))
            {
                result.Add(mt);
            }
        }
        return result.Count > 0 ? result : null;
    }

    private static List<OrderSideType>? ParseOrderSideFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var result = new List<OrderSideType>();
        foreach (string part in filter.Split(','))
        {
            if (Enum.TryParse<OrderSideType>(part.Trim(), true, out OrderSideType st))
            {
                result.Add(st);
            }
        }
        return result.Count > 0 ? result : null;
    }

    private static TradeModeType ParseTradeModeType(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return TradeModeType.UNKNOWN;
        }

        return filter.Trim().ToUpperInvariant() switch
        {
            "REAL" => TradeModeType.REAL,
            "EMULATED" or "EMU" => TradeModeType.EMULATED,
            _ => TradeModeType.UNKNOWN
        };
    }

    // ── Helpers ──────────────────────────────────────────────

    private CoreConnection? ResolveConnection(string? profileName)
    {
        if (profileName != null)
        {
            return _manager.Get(profileName);
        }

        return _manager.ActiveConnection;
    }

    private static string FormatPnl(double value) =>
        value >= 0 ? $"+{value:F2}" : $"{value:F2}";

    private static string FormatPrice(double value) =>
        value switch
        {
            >= 1000 => $"{value:F2}",
            >= 1 => $"{value:F4}",
            >= 0.001 => $"{value:F6}",
            _ => $"{value:G6}"
        };

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s[..max];



    #region Phase K: Report Metadata

    private CommandResult GetReportComments(CoreConnection conn)
    {
        ReportsFieldData? data = conn.RequestReportComments();
        if (data == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Report comments request timed out.");
        }

        List<string>? comments = data.reportComments ?? new List<string>();
        if (comments.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] No report comments.");
        }

        return CommandResult.Ok(
            $"[{conn.Name}] {comments.Count} report comment(s):",
            new { Server = conn.Name, Comments = comments });
    }

    private CommandResult GetReportDates(CoreConnection conn)
    {
        ReportsFieldData? data = conn.RequestReportDates();
        if (data == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Report dates request timed out.");
        }

        List<long>? dates = data.reportsDate ?? new List<long>();
        if (dates.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] No report dates.");
        }

        var formatted = new List<string>(dates.Count);
        foreach (long d in dates)
        {
            formatted.Add(DateTimeOffset.FromUnixTimeMilliseconds(d).UtcDateTime.ToString("yyyy-MM-dd"));
        }

        return CommandResult.Ok(
            $"[{conn.Name}] {dates.Count} report date(s): {string.Join(", ", formatted.GetRange(0, Math.Min(formatted.Count, 30)))}",
            new { Server = conn.Name, Dates = formatted, RawTimestamps = dates });
    }

    #endregion

    private static int CountUniqueSymbols(List<ReportData> trades)
    {
        var symbols = new HashSet<string>();
        foreach (ReportData r in trades)
        {
            symbols.Add(r.symbol);
        }

        return symbols.Count;
    }

    private static string GetTopSymbols(List<ReportData> trades, int limit)
    {
        var seen = new List<string>();
        var set = new HashSet<string>();
        foreach (ReportData r in trades)
        {
            if (set.Add(r.symbol))
            {
                seen.Add(r.symbol);
                if (seen.Count >= limit)
                {
                    break;
                }
            }
        }
        return string.Join(", ", seen);
    }

    private static int CountEmulated(List<ReportData> trades)
    {
        int count = 0;
        foreach (ReportData r in trades)
        {
            if (r.isEmulated)
            {
                count++;
            }
        }

        return count;
    }

    private static string GetUniqueMarketTypes(List<ReportData> trades)
    {
        var seen = new List<string>();
        var set = new HashSet<string>();
        foreach (ReportData r in trades)
        {
            string mt = r.marketType.ToString();
            if (set.Add(mt))
            {
                seen.Add(mt);
            }
        }
        return string.Join(", ", seen);
    }

    private static List<object> FormatAlgoBreakdown(List<(string Algo, int Trades, double PnL, double WinRate)> byAlgo)
    {
        var result = new List<object>(byAlgo.Count);
        foreach ((string Algo, int Trades, double PnL, double WinRate) a in byAlgo)
        {
            result.Add(new { a.Algo, a.Trades, PnL = Math.Round(a.PnL, 2), WinRate = Math.Round(a.WinRate, 1) });
        }

        return result;
    }


    #region Export & Store

    private static bool HasFlag(List<string> args, string flag)
    {
        foreach (string a in args)
        {
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private CommandResult HandleExport(CoreConnection conn, string[] args)
    {
        // Parse filters from args (reuse same filter parsing as main Execute)
        ParseFilters(args, out long unixFrom, out long unixTo, out string symbolFilter,
            out string algoFilter, out string sigFilter, out bool excludeEmulated,
            out string? closedByFilter, out string? marketTypeFilter,
            out string? sideFilter, out string? tradeModeFilter,
            out string rangeLabel, out string? filePath);

        List<ReportClosedByType>? closedByList = ParseClosedByFilter(closedByFilter);
        List<MarketType>? marketTypes = ParseMarketTypeFilter(marketTypeFilter);
        List<OrderSideType>? orderSideTypes = ParseOrderSideFilter(sideFilter);
        TradeModeType tradeModeType = ParseTradeModeType(tradeModeFilter);

        ReportListData? reportList = conn.RequestReports(
            unixFrom, unixTo, symbolFilter, algoFilter, sigFilter,
            false, excludeEmulated, closedByList, marketTypes, orderSideTypes, tradeModeType);

        if (reportList == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Report request timed out or failed.");
        }

        List<ReportData>? reports = reportList.reports;
        if (reports == null || reports.Count == 0)
        {
            return CommandResult.Fail($"[{conn.Name}] No trades found for {rangeLabel}.");
        }

        string csv = ReportCsvExporter.GenerateCsv(reports, conn.Name);
        string outputPath = ReportCsvExporter.WriteToFile(csv, filePath);

        return CommandResult.Ok(
            $"[{conn.Name}] Exported {reports.Count} trades to: {outputPath}",
            new { Server = conn.Name, TradeCount = reports.Count, FilePath = outputPath, Period = rangeLabel });
    }

    private CommandResult HandleFleetExport(string[] args)
    {
        ParseFilters(args, out long unixFrom, out long unixTo, out string symbolFilter,
            out string algoFilter, out string sigFilter, out bool excludeEmulated,
            out string? closedByFilter, out string? marketTypeFilter,
            out string? sideFilter, out string? tradeModeFilter,
            out string rangeLabel, out string? filePath);

        List<ReportClosedByType>? closedByList = ParseClosedByFilter(closedByFilter);
        List<MarketType>? marketTypes = ParseMarketTypeFilter(marketTypeFilter);
        List<OrderSideType>? orderSideTypes = ParseOrderSideFilter(sideFilter);
        TradeModeType tradeModeType = ParseTradeModeType(tradeModeFilter);

        IReadOnlyList<CoreConnection> connections = _manager.GetAll();
        Dictionary<string, List<ReportData>> reportsByServer =
            new Dictionary<string, List<ReportData>>();
        List<string> errors = new List<string>();

        foreach (CoreConnection c in connections)
        {
            if (!c.IsConnected)
            {
                continue;
            }

            ReportListData? reportList = c.RequestReports(
                unixFrom, unixTo, symbolFilter, algoFilter, sigFilter,
                false, excludeEmulated, closedByList, marketTypes, orderSideTypes, tradeModeType);

            if (reportList?.reports != null && reportList.reports.Count > 0)
            {
                reportsByServer[c.Name] = reportList.reports;
            }
            else
            {
                errors.Add(c.Name);
            }
        }

        if (reportsByServer.Count == 0)
        {
            return CommandResult.Fail("No trades found from any connected server.");
        }

        string csv = ReportCsvExporter.GenerateMergedCsv(reportsByServer);
        string outputPath = ReportCsvExporter.WriteToFile(csv, filePath);

        int totalTrades = 0;
        foreach (KeyValuePair<string, List<ReportData>> kvp in reportsByServer)
        {
            totalTrades += kvp.Value.Count;
        }

        StringBuilder summary = new StringBuilder();
        summary.AppendLine($"Fleet CSV Export — {reportsByServer.Count} servers, {totalTrades} trades → {outputPath}");
        foreach (KeyValuePair<string, List<ReportData>> kvp in reportsByServer)
        {
            summary.AppendLine($"  {kvp.Key}: {kvp.Value.Count} trades");
        }

        if (errors.Count > 0)
        {
            summary.AppendLine($"  Skipped ({errors.Count}): {string.Join(", ", errors)}");
        }

        return CommandResult.Ok(summary.ToString(),
            new { Servers = reportsByServer.Count, TotalTrades = totalTrades,
                  FilePath = outputPath, Period = rangeLabel });
    }

    private CommandResult HandleStore(CoreConnection conn, string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail("Usage: reports store <name> [filters...]");
        }

        string storeName = args[0];
        string[] filterArgs = args.Length > 1 ? args[1..] : Array.Empty<string>();

        ParseFilters(filterArgs, out long unixFrom, out long unixTo, out string symbolFilter,
            out string algoFilter, out string sigFilter, out bool excludeEmulated,
            out string? closedByFilter, out string? marketTypeFilter,
            out string? sideFilter, out string? tradeModeFilter,
            out string rangeLabel, out string? _);

        List<ReportClosedByType>? closedByList = ParseClosedByFilter(closedByFilter);
        List<MarketType>? marketTypes = ParseMarketTypeFilter(marketTypeFilter);
        List<OrderSideType>? orderSideTypes = ParseOrderSideFilter(sideFilter);
        TradeModeType tradeModeType = ParseTradeModeType(tradeModeFilter);

        ReportListData? reportList = conn.RequestReports(
            unixFrom, unixTo, symbolFilter, algoFilter, sigFilter,
            false, excludeEmulated, closedByList, marketTypes, orderSideTypes, tradeModeType);

        if (reportList?.reports == null || reportList.reports.Count == 0)
        {
            return CommandResult.Fail($"[{conn.Name}] No trades found for {rangeLabel}.");
        }

        string filterDesc = BuildFilterDescription(rangeLabel, symbolFilter, algoFilter, sigFilter);
        _reportStore.Save(storeName, conn.Name, reportList.reports, unixFrom, unixTo, filterDesc);

        return CommandResult.Ok(
            $"Stored '{storeName}': {reportList.reports.Count} trades from [{conn.Name}] ({rangeLabel})",
            new { Name = storeName, Server = conn.Name, TradeCount = reportList.reports.Count, Period = rangeLabel });
    }

    private CommandResult HandleFleetStore(string[] args)
    {
        // Remove --all flag from args
        List<string> cleanedArgs = new List<string>();
        foreach (string a in args)
        {
            if (!string.Equals(a, "--all", StringComparison.OrdinalIgnoreCase))
            {
                cleanedArgs.Add(a);
            }
        }

        if (cleanedArgs.Count == 0)
        {
            return CommandResult.Fail("Usage: reports store <name> --all [filters...]");
        }

        string storeName = cleanedArgs[0];
        string[] filterArgs = cleanedArgs.Count > 1
            ? cleanedArgs.GetRange(1, cleanedArgs.Count - 1).ToArray()
            : Array.Empty<string>();

        ParseFilters(filterArgs, out long unixFrom, out long unixTo, out string symbolFilter,
            out string algoFilter, out string sigFilter, out bool excludeEmulated,
            out string? closedByFilter, out string? marketTypeFilter,
            out string? sideFilter, out string? tradeModeFilter,
            out string rangeLabel, out string? _);

        List<ReportClosedByType>? closedByList = ParseClosedByFilter(closedByFilter);
        List<MarketType>? marketTypes = ParseMarketTypeFilter(marketTypeFilter);
        List<OrderSideType>? orderSideTypes = ParseOrderSideFilter(sideFilter);
        TradeModeType tradeModeType = ParseTradeModeType(tradeModeFilter);

        IReadOnlyList<CoreConnection> connections = _manager.GetAll();
        Dictionary<string, List<ReportData>> reportsByServer =
            new Dictionary<string, List<ReportData>>();

        foreach (CoreConnection c in connections)
        {
            if (!c.IsConnected)
            {
                continue;
            }

            ReportListData? reportList = c.RequestReports(
                unixFrom, unixTo, symbolFilter, algoFilter, sigFilter,
                false, excludeEmulated, closedByList, marketTypes, orderSideTypes, tradeModeType);

            if (reportList?.reports != null && reportList.reports.Count > 0)
            {
                reportsByServer[c.Name] = reportList.reports;
            }
        }

        if (reportsByServer.Count == 0)
        {
            return CommandResult.Fail("No trades found from any connected server.");
        }

        string filterDesc = BuildFilterDescription(rangeLabel, symbolFilter, algoFilter, sigFilter);
        _reportStore.SaveMerged(storeName, reportsByServer, unixFrom, unixTo, filterDesc);

        int totalTrades = 0;
        foreach (KeyValuePair<string, List<ReportData>> kvp in reportsByServer)
        {
            totalTrades += kvp.Value.Count;
        }

        return CommandResult.Ok(
            $"Stored '{storeName}': {totalTrades} trades from {reportsByServer.Count} servers ({rangeLabel})",
            new { Name = storeName, Servers = reportsByServer.Count, TotalTrades = totalTrades, Period = rangeLabel });
    }

    private CommandResult HandleStored()
    {
        List<StoredReportSet> sets = _reportStore.ListAll();
        if (sets.Count == 0)
        {
            return CommandResult.Ok("No stored report sets. Use 'reports store <name>' to save one.");
        }

        TableBuilder table = new TableBuilder("Name", "Server", "Trades", "PnL", "WinRate", "Volume", "Filters", "Captured");
        foreach (StoredReportSet s in sets)
        {
            table.AddRow(
                s.Name,
                Trunc(s.ServerName, 20),
                s.TradeCount.ToString(),
                FormatPnl(s.TotalPnlUSDT),
                $"{s.WinRate:F1}%",
                $"${s.TotalVolumeUSDT:N0}",
                Trunc(s.FilterDescription, 20),
                s.CapturedAtUtc.ToString("MM/dd HH:mm"));
        }

        var data = new List<object>(sets.Count);
        foreach (StoredReportSet s in sets)
        {
            data.Add(new
            {
                s.Name, s.ServerName, s.TradeCount, s.TotalPnlUSDT, s.TotalFeesUSDT,
                s.TotalVolumeUSDT, s.Wins, s.Losses, s.WinRate, s.FilterDescription,
                CapturedUtc = s.CapturedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"),
            });
        }

        return CommandResult.Ok(
            $"Stored Report Sets ({sets.Count}):\n{table}",
            new { Count = sets.Count, Sets = data });
    }

    private CommandResult HandleLoad(string name)
    {
        StoredReportSet? set = _reportStore.Get(name);
        if (set == null)
        {
            return CommandResult.Fail($"No stored report set named '{name}'. Use 'reports stored' to list.");
        }

        // Sort by close time descending
        List<ReportData> sorted = new List<ReportData>(set.Reports);
        sorted.Sort((a, b) => b.reportTime.CompareTo(a.reportTime));

        int rowLimit = Math.Min(sorted.Count, 50);
        TableBuilder rows = new TableBuilder("Close", "Symbol", "Side", "Entry", "Exit", "PnL", "Fee", "ROE", "Market", "ClosedBy", "Algo");
        for (int idx = 0; idx < rowLimit; idx++)
        {
            ReportData r = sorted[idx];
            rows.AddRow(
                DateTimeOffset.FromUnixTimeMilliseconds(r.reportTime).ToString("MM/dd HH:mm"),
                Trunc(r.symbol, 12),
                r.orderSideType == OrderSideType.BUY ? "BUY" : "SELL",
                FormatPrice(r.priceOpen),
                FormatPrice(r.priceClose),
                FormatPnl(r.totalUSDT),
                $"{r.commissionUSDT:F2}",
                $"{r.profitPercentage:+0.00;-0.00}%",
                r.marketType.ToString(),
                r.closedBy.ToString(),
                Trunc(r.orderInfo.signature ?? "Manual", 8));
        }

        string display = $"Stored Set '{name}' [{set.ServerName}] — {set.TradeCount} trades, " +
            $"PnL: {FormatPnl(set.TotalPnlUSDT)}, WinRate: {set.WinRate:F1}%, " +
            $"Captured: {set.CapturedAtUtc:yyyy-MM-dd HH:mm}\n{rows}";

        if (sorted.Count > rowLimit)
        {
            display += $"\n... and {sorted.Count - rowLimit} more trades";
        }

        return CommandResult.Ok(display,
            new { set.Name, set.ServerName, set.TradeCount, set.TotalPnlUSDT,
                  set.TotalFeesUSDT, set.TotalVolumeUSDT, set.Wins, set.Losses, set.WinRate });
    }

    private CommandResult HandleDelete(string name)
    {
        if (_reportStore.Delete(name))
        {
            return CommandResult.Ok($"Deleted stored report set '{name}'.");
        }

        return CommandResult.Fail($"No stored report set named '{name}'.");
    }

    private static void ParseFilters(string[] args,
        out long unixFrom, out long unixTo,
        out string symbolFilter, out string algoFilter, out string sigFilter,
        out bool excludeEmulated,
        out string? closedByFilter, out string? marketTypeFilter,
        out string? sideFilter, out string? tradeModeFilter,
        out string rangeLabel, out string? filePath)
    {
        unixTo = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        unixFrom = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();
        symbolFilter = "";
        algoFilter = "";
        sigFilter = "";
        rangeLabel = "last 24h";
        excludeEmulated = false;
        closedByFilter = null;
        marketTypeFilter = null;
        sideFilter = null;
        tradeModeFilter = null;
        filePath = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            switch (a)
            {
                case "today":
                    unixFrom = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
                    rangeLabel = "today";
                    break;
                case "24h":
                    unixFrom = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();
                    rangeLabel = "last 24h";
                    break;
                case "7d":
                    unixFrom = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds();
                    rangeLabel = "last 7 days";
                    break;
                case "30d":
                    unixFrom = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
                    rangeLabel = "last 30 days";
                    break;
                case "90d":
                    unixFrom = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds();
                    rangeLabel = "last 90 days";
                    break;
                case "--from":
                    if (i + 1 < args.Length && DateTimeOffset.TryParse(args[i + 1], out DateTimeOffset fromDt))
                    {
                        unixFrom = fromDt.ToUnixTimeMilliseconds();
                        rangeLabel = $"from {args[i + 1]}";
                        i++;
                    }
                    break;
                case "--to":
                    if (i + 1 < args.Length && DateTimeOffset.TryParse(args[i + 1], out DateTimeOffset toDt))
                    {
                        unixTo = toDt.ToUnixTimeMilliseconds();
                        rangeLabel += $" to {args[i + 1]}";
                        i++;
                    }
                    break;
                case "--symbol":
                    if (i + 1 < args.Length) { symbolFilter = args[++i]; }
                    break;
                case "--algo":
                    if (i + 1 < args.Length) { algoFilter = args[++i]; }
                    break;
                case "--sig":
                    if (i + 1 < args.Length) { sigFilter = args[++i]; }
                    break;
                case "--exclude-emulated":
                    excludeEmulated = true;
                    break;
                case "--closed-by":
                    if (i + 1 < args.Length) { closedByFilter = args[++i]; }
                    break;
                case "--market":
                    if (i + 1 < args.Length) { marketTypeFilter = args[++i]; }
                    break;
                case "--side":
                    if (i + 1 < args.Length) { sideFilter = args[++i]; }
                    break;
                case "--mode":
                    if (i + 1 < args.Length) { tradeModeFilter = args[++i]; }
                    break;
                case "--path":
                    if (i + 1 < args.Length) { filePath = args[++i]; }
                    break;
                case "--all":
                    // Handled at dispatch level
                    break;
            }
        }
    }

    private static string BuildFilterDescription(string rangeLabel, string symbolFilter,
        string algoFilter, string sigFilter)
    {
        StringBuilder desc = new StringBuilder(rangeLabel);
        if (!string.IsNullOrEmpty(symbolFilter))
        {
            desc.Append($" sym={symbolFilter}");
        }

        if (!string.IsNullOrEmpty(algoFilter))
        {
            desc.Append($" algo={algoFilter}");
        }

        if (!string.IsNullOrEmpty(sigFilter))
        {
            desc.Append($" sig={sigFilter}");
        }

        return desc.ToString();
    }

    #endregion

}
