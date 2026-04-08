using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MTShared.Types;
using MTTextClient.Core;
using MTTextClient.Output;
namespace MTTextClient.Commands;

/// <summary>
/// Account data commands — balances, orders, positions, executions, info.
/// Provides real-time account snapshot from UDS (User Data Stream).
///
/// Subcommands:
///   account balance [-all]           — show balances (non-dust by default)
///   account orders [-all]            — show active orders (or all with -all)
///   account positions [-all]         — show open positions (or all with -all)
///   account executions [count]       — show recent trade fills
///   account info                     — show account info (can-trade, position mode, etc.)
///   account summary                  — compact overview across all data
///
/// Supports @profile prefix for multi-server targeting:
///   account @bnc_001 balance
///   account @okx_001 positions
///
/// Phase H: Updated to display all expanded snapshot fields from AccountStore.
/// </summary>
public sealed class AccountCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "account";
    public string Description => "Account data: balance, orders, positions, executions, info";
    public string Usage => "account [<@profile>] <balance|orders|positions|executions|info|summary> [options]";

    public AccountCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(
                "Usage: account <subcommand>\n" +
                "  balance [-all]        — balances (non-dust default)\n" +
                "  orders [-all]         — active orders\n" +
                "  positions [-all]      — open positions\n" +
                "  executions [count]    — recent trade fills\n" +
                "  info                  — account metadata\n" +
                "  summary               — compact overview");
        }

        // Parse @profile from any position in args
        string? profileName = null;
        var argsList = new List<string>();
        foreach (string arg in args)
        {
            if (arg.StartsWith('@'))
            {
                profileName = arg[1..];
            }
            else
            {
                argsList.Add(arg);
            }
        }

        if (argsList.Count == 0)
        {
            return CommandResult.Fail("Missing subcommand. Use: balance, orders, positions, executions, info, summary");
        }

        CoreConnection? conn = ResolveConnection(profileName);
        if (conn == null)
        {
            return CommandResult.Fail(
                profileName != null
                    ? $"Connection '{profileName}' not found. Use 'status' to see connections."
                    : "No active connection. Use 'connect <profile>' first.");
        }

        if (!conn.IsConnected)
        {
            return CommandResult.Fail($"Connection '{conn.Name}' is not connected.");
        }

        string? subcommand = argsList[0].ToLowerInvariant();
        string[]? subArgs = argsList.Count > 1 ? argsList.GetRange(1, argsList.Count - 1).ToArray() : Array.Empty<string>();

        return subcommand switch
        {
            "balance" or "bal" or "balances" => HandleBalance(conn, subArgs),
            "orders" or "order" => HandleOrders(conn, subArgs),
            "positions" or "pos" or "position" => HandlePositions(conn, subArgs),
            "executions" or "exec" or "fills" => HandleExecutions(conn, subArgs),
            "info" => HandleInfo(conn),
            "summary" or "sum" => HandleSummary(conn),
            _ => CommandResult.Fail($"Unknown subcommand: '{subcommand}'. Use: balance, orders, positions, executions, info, summary")
        };
    }

    // ── Balance ──────────────────────────────────────────────

    private CommandResult HandleBalance(CoreConnection conn, string[] args)
    {
        bool includeDust = ContainsIgnoreCase(args, "-all");
        IReadOnlyList<BalanceSnapshot>? balances = conn.AccountStore.GetBalances(includeDust);

        if (balances.Count == 0)
        {
            string? msg = conn.AccountStore.LastBalanceUpdate == default
                ? $"[{conn.Name}] No balance data received yet. UDS subscription may still be initializing."
                : $"[{conn.Name}] No balances found" + (includeDust ? "." : " (use -all to include dust/zero).");
            return CommandResult.Ok(msg);
        }

        double totalUsdt = conn.AccountStore.GetTotalBalanceUSDT();
        var rows = new List<object>(balances.Count);
        foreach (BalanceSnapshot b in balances)
        {
            rows.Add(new
            {
                Asset = b.Asset,
                Total = FormatNumber(b.Total),
                Available = FormatNumber(b.Available),
                Locked = FormatNumber(b.Locked),
                EstUSDT = FormatMoney(b.EstimationUSDT),
                Market = b.MarketType.ToString(),
                Transferable = b.IsTransferable ? "Y" : "",
                Dust = b.IsDust ? "yes" : ""
            });
        }

        string? header = $"[{conn.Name}] Balances — Total: ${totalUsdt:N2} USDT" +
                     $" ({balances.Count} asset{(balances.Count != 1 ? "s" : "")})";
        return CommandResult.Ok(header, rows);
    }

    // ── Orders ───────────────────────────────────────────────

    private CommandResult HandleOrders(CoreConnection conn, string[] args)
    {
        bool showAll = ContainsIgnoreCase(args, "-all");
        IReadOnlyList<OrderSnapshot>? orders = conn.AccountStore.GetOrders(activeOnly: !showAll);

        if (orders.Count == 0)
        {
            string? msg = conn.AccountStore.LastOrderUpdate == default
                ? $"[{conn.Name}] No order data received yet."
                : $"[{conn.Name}] No {(showAll ? "" : "active ")}orders found.";
            return CommandResult.Ok(msg);
        }

        // Display table with key expanded fields
        TableBuilder rows = new TableBuilder("Symbol", "Side", "Type", "Status", "Price", "Qty", "Filled", "StopPrice", "PosSide", "TIF", "SL", "TP", "Emu", "Algo", "OrderId");
        foreach (OrderSnapshot o in orders)
        {
            rows.AddRow(
                o.Symbol,
                o.Side.ToString(),
                o.OrderType.ToString(),
                o.Status.ToString(),
                o.Price != 0 ? FormatDecimal(o.Price) : "MARKET",
                FormatDecimal(o.Quantity),
                $"{o.FilledPercent}%",
                o.StopPrice != 0 ? FormatDecimal(o.StopPrice) : "",
                o.PositionSide.ToString(),
                o.TimeInForce.ToString(),
                o.IsStopLoss ? "SL" : "",
                o.IsTakeProfit ? "TP" : "",
                o.IsEmulated ? "E" : "",
                !string.IsNullOrEmpty(o.AlgoSignature) ? o.AlgoSignature : "",
                TruncateId(o.ClientOrderId)
            );
        }

        // MCP structured data with ALL fields
        var mcpOrders = new List<object>(orders.Count);
        foreach (OrderSnapshot o in orders)
        {
            mcpOrders.Add(new
            {
                o.Symbol,
                Side = o.Side.ToString(),
                OrderType = o.OrderType.ToString(),
                Status = o.Status.ToString(),
                o.Price,
                o.Quantity,
                o.FilledPercent,
                o.StopPrice,
                PositionSide = o.PositionSide.ToString(),
                MarketType = o.MarketType.ToString(),
                TimeInForce = o.TimeInForce.ToString(),
                o.ClientOrderId,
                // OriginalClientOrderId not in snapshot
                o.IsStopLoss,
                o.IsTakeProfit,
                o.IsEmulated,
                o.IsArchived,
                o.IsAlgoOrder,
                o.IsManualOrder,
                o.AlgoId,
                o.AlgoSignature,
                o.AlgoName,
                AlgoGroupType = o.AlgoGroupType.ToString(),
                TpslStatus = o.TpslStatus.ToString(),
                o.OrderComment,
                o.CommissionUSDT,
                o.TotalCommission,
                o.LastExecutedQty,
                o.ExecutedQtyUSDT,
                o.EstimatedValueUSDT,
            });
        }

        var mcpData = new
        {
            Server = conn.Name,
            TotalOrders = orders.Count,
            ShowAll = showAll,
            Orders = mcpOrders
        };

        string? header = $"[{conn.Name}] Orders — {orders.Count} {(showAll ? "total" : "active")}";
        return CommandResult.Ok(header + "\n" + rows.ToString(), mcpData);
    }

    // ── Positions ────────────────────────────────────────────

    private CommandResult HandlePositions(CoreConnection conn, string[] args)
    {
        bool showAll = ContainsIgnoreCase(args, "-all");
        IReadOnlyList<PositionSnapshot>? positions = conn.AccountStore.GetPositions(openOnly: !showAll);

        if (positions.Count == 0)
        {
            string? msg = conn.AccountStore.LastPositionUpdate == default
                ? $"[{conn.Name}] No position data received yet."
                : $"[{conn.Name}] No {(showAll ? "" : "open ")}positions found.";
            return CommandResult.Ok(msg);
        }

        TableBuilder rows = new TableBuilder("Symbol", "Side", "Amount", "EntryPrice", "Leverage", "Margin", "MarginType", "UnrealizedPnl", "PnlPct", "LiqPrice", "Status", "Notional");
        foreach (PositionSnapshot p in positions)
        {
            rows.AddRow(
                p.Symbol,
                p.PositionSide.ToString(),
                FormatDecimal(p.Amount),
                FormatDecimal(p.EntryPrice),
                $"{p.Leverage}x",
                FormatDecimal(p.Margin),
                p.MarginType.ToString(),
                FormatPnl(p.UnrealizedPnl),
                $"{p.PnlPercent:+0.00;-0.00}%",
                p.LiquidationPrice > 0 ? FormatNumber(p.LiquidationPrice) : "N/A",
                p.PositionStatus.ToString(),
                FormatDecimal(p.NotionalValue)
            );
        }

        double totalPnl = 0;
        foreach (PositionSnapshot p in positions)
        {
            totalPnl += p.UnrealizedPnl;
        }

        string? header = $"[{conn.Name}] Positions — {positions.Count} {(showAll ? "total" : "open")}" +
                     $" | Unrealized PnL: {FormatPnl(totalPnl)}";
        return CommandResult.Ok(header + "\n" + rows.ToString(), positions);
    }

    // ── Executions ───────────────────────────────────────────

    private CommandResult HandleExecutions(CoreConnection conn, string[] args)
    {
        int count = 20;
        if (args.Length > 0 && int.TryParse(args[0], out int parsed) && parsed > 0)
        {
            count = Math.Min(parsed, 100);
        }

        IReadOnlyList<ExecutionSnapshot>? executions = conn.AccountStore.GetRecentExecutions(count);

        if (executions.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] No recent executions.");
        }

        TableBuilder rows = new TableBuilder("Time", "Symbol", "Side", "Price", "Qty", "Commission", "Market", "Type", "Status", "Emu", "Algo", "OrderId");
        foreach (ExecutionSnapshot e in executions)
        {
            rows.AddRow(
                e.ExecutionTime.ToString("HH:mm:ss"),
                e.Symbol,
                e.Side.ToString(),
                FormatDecimal(e.Price),
                FormatDecimal(e.LastFillQty),
                $"{FormatDecimal(e.Commission)} {e.CommissionAsset}",
                e.MarketType.ToString(),
                e.OrderType.ToString(),
                e.Status.ToString(),
                e.IsEmulated ? "E" : "",
                !string.IsNullOrEmpty(e.AlgoSignature) ? e.AlgoSignature : "",
                TruncateId(e.ClientOrderId)
            );
        }

        // MCP data with all expanded fields
        var mcpExecs = new List<object>(executions.Count);
        foreach (ExecutionSnapshot e in executions)
        {
            mcpExecs.Add(new
            {
                e.ExecutionTime,
                e.Symbol,
                Side = e.Side.ToString(),
                e.Price,
                e.LastFillQty,
                e.Commission,
                e.CommissionAsset,
                MarketType = e.MarketType.ToString(),
                PositionSide = e.PositionSide.ToString(),
                OrderType = e.OrderType.ToString(),
                Status = e.Status.ToString(),
                e.OrderId,
                e.ClientOrderId,
                e.CumulativeQty,
                e.ExecutedQtyUSDT,
                e.CommissionUSDT,
                e.IsEmulated,
                e.IsAlgoOrder,
                e.AlgoSignature,
                e.AlgoId,
                e.TransactTime,
            });
        }

        var mcpData = new
        {
            Server = conn.Name,
            TotalExecutions = executions.Count,
            Executions = mcpExecs
        };

        string? header = $"[{conn.Name}] Recent Executions — {executions.Count} fills";
        return CommandResult.Ok(header + "\n" + rows.ToString(), mcpData);
    }

    // ── Account Info ─────────────────────────────────────────

    private CommandResult HandleInfo(CoreConnection conn)
    {
        AccountInfoSnapshot? info = conn.AccountStore.GetAccountInfo();

        if (info == null)
        {
            return CommandResult.Ok($"[{conn.Name}] No account info received yet.");
        }

        var data = new
        {
            Server = conn.Name,
            Exchange = conn.Profile.Exchange.ToString(),
            MarketType = info.MarketType.ToString(),
            CanTrade = info.CanTrade ? "Yes" : "No",
            PositionMode = info.PositionMode.ToString(),
            MultiAssetMode = info.MultiAssetMode ? "Enabled" : "Disabled",
            EventTime = info.EventTime > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(info.EventTime).ToString("yyyy-MM-dd HH:mm:ss UTC")
                : "N/A",
            LastUpdate = info.Timestamp.ToString("yyyy-MM-dd HH:mm:ss UTC")
        };

        return CommandResult.Ok($"[{conn.Name}] Account Info", data);
    }

    // ── Summary ──────────────────────────────────────────────

    private CommandResult HandleSummary(CoreConnection conn)
    {
        AccountStore acct = conn.AccountStore;
        double totalUsdt = acct.GetTotalBalanceUSDT();
        IReadOnlyList<PositionSnapshot>? openPositions = acct.GetPositions(openOnly: true);
        IReadOnlyList<OrderSnapshot>? activeOrders = acct.GetOrders(activeOnly: true);
        double totalPnl = 0;
        foreach (PositionSnapshot p in openPositions)
        {
            totalPnl += p.UnrealizedPnl;
        }

        AccountInfoSnapshot? info = acct.GetAccountInfo();

        int emulatedCount = 0;
        int algoOrderCount = 0;
        foreach (OrderSnapshot o in activeOrders)
        {
            if (o.IsEmulated)
            {
                emulatedCount++;
            }

            if (o.IsAlgoOrder)
            {
                algoOrderCount++;
            }
        }
        int runningAlgoCount = 0;
        foreach (MTShared.Network.AlgorithmData a in conn.AlgoStore.GetAll())
        {
            if (a.isRunning)
            {
                runningAlgoCount++;
            }
        }

        var data = new
        {
            Server = conn.Name,
            Exchange = conn.Profile.Exchange.ToString(),
            Status = conn.IsConnected ? "CONNECTED" : "DISCONNECTED",
            Uptime = FormatTimeSpan(conn.Uptime),
            CanTrade = info?.CanTrade == true ? "Yes" : (info == null ? "?" : "No"),
            TotalBalanceUSDT = $"${totalUsdt:N2}",
            ActiveBalances = acct.BalanceCount,
            OpenPositions = openPositions.Count,
            UnrealizedPnl = FormatPnl(totalPnl),
            ActiveOrders = activeOrders.Count,
            EmulatedOrders = emulatedCount,
            AlgoOrders = algoOrderCount,
            Algorithms = conn.AlgoStore.Count,
            RunningAlgos = runningAlgoCount,
            TradePairs = conn.ExchangeInfoStore.TradePairCount,
            LastBalanceUpdate = acct.LastBalanceUpdate != default
                ? acct.LastBalanceUpdate.ToString("HH:mm:ss")
                : "none",
            LastOrderUpdate = acct.LastOrderUpdate != default
                ? acct.LastOrderUpdate.ToString("HH:mm:ss")
                : "none",
            LastPositionUpdate = acct.LastPositionUpdate != default
                ? acct.LastPositionUpdate.ToString("HH:mm:ss")
                : "none"
        };

        return CommandResult.Ok($"[{conn.Name}] Account Summary", data);
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

    private static string FormatNumber(double value) =>
        value switch
        {
            >= 1_000_000 => $"{value:N2}",
            >= 1 => $"{value:N4}",
            >= 0.0001 => $"{value:N6}",
            _ => $"{value:N8}"
        };

    private static string FormatMoney(double value) =>
        value >= 0.01 ? $"${value:N2}" : value > 0 ? $"${value:N6}" : "$0.00";

    private static string FormatDecimal(decimal value) =>
        value switch
        {
            >= 1_000m => $"{value:N2}",
            >= 1m => $"{value:N4}",
            >= 0.0001m => $"{value:N6}",
            _ => $"{value:N8}"
        };

    private static string FormatPnl(double value) =>
        value >= 0 ? $"+${value:N2}" : $"-${Math.Abs(value):N2}";

    private static string FormatTimeSpan(TimeSpan ts) =>
        ts.TotalDays >= 1 ? $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m"
        : ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
        : $"{ts.Minutes}m {ts.Seconds}s";

    private static string TruncateId(string id) =>
        id.Length > 16 ? id[..16] + "…" : id;



    private static bool ContainsIgnoreCase(string[] args, string value)
    {
        foreach (string a in args)
        {
            if (a.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
