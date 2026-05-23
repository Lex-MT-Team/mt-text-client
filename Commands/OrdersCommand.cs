using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using MTShared;
using MTShared.Network;
using MTShared.Structs;
using MTShared.Types;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// Order & Position management commands.
///
/// orders list                                — list active orders (from AccountStore)
/// orders cancel <clientOrderId>              — cancel a specific order (--confirm)
/// orders cancel-all [symbol]                 — cancel all orders (--confirm)
/// orders close <symbol> [pct]                — close position, 100% by default (--confirm)
/// orders close-all                           — close all open positions (--confirm)
/// orders positions                           — list open positions with PnL
/// orders place <symbol> <side> <qty> [price] — place an order (--confirm)
/// orders move <clientOrderId> <newPrice>     — move/modify an existing order (--confirm)
/// orders set-leverage <symbol> <leverage>    — modify leverage (--confirm)
/// orders set-margin-type <symbol> <type>     — CROSS/ISOLATED (--confirm)
/// orders set-position-mode <symbol> <mode>   — HEDGE/ONE_WAY (--confirm)
/// orders get-position-mode <symbol>          — query position mode
/// orders panic-sell <symbol>                 — emergency close all (--confirm)
/// orders change-margin <symbol> <side> <amount> [add|reduce] — adjust isolated margin (--confirm)
/// orders transfer <asset> <amount> <from> <to> — transfer between SPOT/FUTURES (--confirm)
/// </summary>
public sealed class OrdersCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public OrdersCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public string Name => "orders";
    public string Description => "Order & position management (place, cancel, close, leverage, margin, transfer)";
    public string Usage => @"orders list|cancel|cancel-all|close|close-all|positions|place|move|set-leverage|set-margin-type|set-position-mode|get-position-mode|get-position-mode-account|panic-sell|change-margin|transfer";

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(Usage);
        }

        string? targetProfile = null;
        bool confirmFlag = false;
        var cleanArgs = new List<string>();
        foreach (string arg in args)
        {
            if (arg.StartsWith('@'))
            {
                targetProfile = arg[1..];
            }
            else if (arg.Equals("--confirm", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-y", StringComparison.OrdinalIgnoreCase))
            {
                confirmFlag = true;
            }
            else
            {
                cleanArgs.Add(arg);
            }
        }

        string? subCmd = cleanArgs[0].ToLowerInvariant();
        string[]? subArgs = cleanArgs.Count > 1 ? cleanArgs.GetRange(1, cleanArgs.Count - 1).ToArray() : Array.Empty<string>();

        return subCmd switch
        {
            "list" or "ls" => ListOrders(targetProfile),
            "positions" or "pos" => ListPositions(targetProfile),
            "cancel" => CancelOrder(subArgs, targetProfile, confirmFlag),
            "cancel-all" => CancelAllOrders(subArgs, targetProfile, confirmFlag),
            "close" => ClosePosition(subArgs, targetProfile, confirmFlag),
            "close-all" => CloseAllPositions(targetProfile, confirmFlag),
            "place" => PlaceOrder(subArgs, targetProfile, confirmFlag),
            "move" => MoveOrder(subArgs, targetProfile, confirmFlag),
            "set-leverage" => SetLeverage(subArgs, targetProfile, confirmFlag),
            "set-margin-type" => SetMarginType(subArgs, targetProfile, confirmFlag),
            "set-position-mode" => SetPositionMode(subArgs, targetProfile, confirmFlag),
            "get-position-mode" => GetPositionMode(subArgs, targetProfile),
            "get-position-mode-account" => GetPositionModeAccount(targetProfile),
            "panic-sell" => PanicSell(subArgs, targetProfile, confirmFlag),
            "change-margin" => ChangeMargin(subArgs, targetProfile, confirmFlag),
            "transfer" => TransferFunds(subArgs, targetProfile, confirmFlag),
            "set-leverage-buysell" => SetLeverageBuySell(subArgs, targetProfile, confirmFlag),
            "get-multiasset" => GetMultiAssetMode(subArgs, targetProfile),
            "set-multiasset" => SetMultiAssetMode(subArgs, targetProfile, confirmFlag),
            "move-batch" => MoveBatchOrders(subArgs, targetProfile, confirmFlag),
            "join" => JoinOrder(subArgs, targetProfile),
            "split" => SplitOrder(subArgs, targetProfile),
            "fund-transfer" => TransferAccountFunds(subArgs, targetProfile, confirmFlag),
            "close-by-tpsl" => ClosePositionByTPSL(subArgs, targetProfile, confirmFlag),
            "reset-tpsl" => ResetPositionTPSL(subArgs, targetProfile, confirmFlag),
            // Active-order TP/SL/TS update (wires SendOrderTPSLUpdateRequest).
            "update-tpsl" => UpdateOrderTpsl(subArgs, targetProfile, confirmFlag),
            _ => CommandResult.Fail($"Unknown subcommand: {subCmd}. {Usage}")
        };
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
        if (!conn.IsConnected)
        {
            error = CommandResult.Fail($"[{conn.Name}] Not connected.");
            return null;
        }
        return conn;
    }

    private CommandResult ListOrders(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        IReadOnlyList<OrderSnapshot>? orders = conn.AccountStore.GetOrders(activeOnly: true);
        if (orders.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] No active orders.");
        }

        var data = new List<object>(orders.Count);
        foreach (OrderSnapshot o in orders)
        {
            data.Add(new
            {
                o.ClientOrderId,
                o.Symbol,
                Side = o.Side.ToString(),
                Status = o.Status.ToString(),
                o.Price,
                Qty = o.Quantity,
                Filled = $"{o.FilledPercent}%",
                Type = o.OrderType.ToString(),
                TIF = o.TimeInForce.ToString(),
                TP = o.IsTakeProfit ? "TP" : "",
                SL = o.IsStopLoss ? "SL" : "",
                Algo = o.IsAlgoOrder ? o.AlgoSignature : "Manual",
                Emulated = o.IsEmulated
            });
        }

        return CommandResult.Ok($"[{conn.Name}] {orders.Count} active order(s).", data);
    }

    private CommandResult ListPositions(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        IReadOnlyList<PositionSnapshot>? positions = conn.AccountStore.GetPositions(openOnly: true);
        if (positions.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] No open positions.");
        }

        var data = new List<object>(positions.Count);
        foreach (PositionSnapshot p in positions)
        {
            data.Add(new
            {
                p.Symbol,
                Side = p.PositionSide.ToString(),
                Amount = p.Amount,
                Entry = p.EntryPrice,
                PnL = $"{p.UnrealizedPnl:F2}",
                PnlPct = $"{p.PnlPercent:F2}%",
                Leverage = $"{p.Leverage}x",
                Margin = p.Margin,
                MarginType = p.MarginType.ToString(),
                LiqPrice = $"{p.LiquidationPrice:F2}",
                Notional = p.NotionalValue
            });
        }

        return CommandResult.Ok($"[{conn.Name}] {positions.Count} open position(s).", data);
    }

    private CommandResult CancelOrder(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: orders cancel <clientOrderId> --confirm");
        }

        string? clientOrderId = args[0];

        // Find the order in AccountStore
        IReadOnlyList<OrderSnapshot>? orders = conn.AccountStore.GetOrders(activeOnly: true);
        OrderSnapshot? order = null;
        foreach (OrderSnapshot o in orders)
        {
            if (o.ClientOrderId == clientOrderId)
            {
                order = o;
                break;
            }
        }

        if (!confirmed)
        {
            string? info = order != null ? $" ({order.Symbol} {order.Side} @ {order.Price})" : "";
            return CommandResult.Fail(
                $"[{conn.Name}] ⚠ Cancel order {clientOrderId}{info}?\n" +
                $"  Re-run with --confirm flag:\n" +
                $"  orders cancel {clientOrderId} --confirm");
        }

        string? symbol = order?.Symbol ?? "";
        MarketType marketType = order?.MarketType ?? MarketType.FUTURES;

        NotificationMessageData? notification = conn.CancelOrder(
            conn.Profile.Exchange, marketType, symbol, clientOrderId);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Cancel order {clientOrderId}: sent (response timed out).");
        }

        return notification.IsOk
            ? CommandResult.Ok($"[{conn.Name}] Order {clientOrderId}: CANCELLED ✓",
                new { Server = conn.Name, ClientOrderId = clientOrderId, Action = "CANCEL" })
            : CommandResult.Fail($"[{conn.Name}] Cancel FAILED — {notification.notificationCode}: {notification}");
    }

    private CommandResult CancelAllOrders(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        string? symbol = args.Length > 0 ? args[0] : null;
        IReadOnlyList<OrderSnapshot>? activeOrders = conn.AccountStore.GetOrders(activeOnly: true);
        int orderCount = activeOrders.Count;

        if (!confirmed)
        {
            string? scope = symbol != null ? $"all orders for {symbol}" : "ALL orders";
            return CommandResult.Fail(
                $"[{conn.Name}] ⚠ Cancel {scope}? ({orderCount} active)\n" +
                $"  Re-run with --confirm flag:\n" +
                $"  orders cancel-all{(symbol != null ? $" {symbol}" : "")} --confirm");
        }

        // If there are no active orders, nothing to cancel
        if (orderCount == 0)
        {
            return CommandResult.Ok(
                $"[{conn.Name}] No active orders to cancel.",
                new { Server = conn.Name, Symbol = symbol ?? "ALL", Action = "CANCEL_ALL", OrderCount = 0 });
        }

        // Determine market types from active orders (don't hardcode FUTURES)
        var marketTypeSet = new HashSet<MarketType>();
        foreach (OrderSnapshot o in activeOrders)
        {
            marketTypeSet.Add(o.MarketType);
        }

        var marketTypes = new List<MarketType>(marketTypeSet);

        // Cancel for each market type that has active orders
        var results = new List<string>();
        foreach (MarketType mt in marketTypes)
        {
            NotificationMessageData? notification = conn.CancelAllOrders(
                conn.Profile.Exchange, mt, symbol);

            if (notification == null)
            {
                results.Add($"  {mt}: sent (response timed out)");
            }
            else if (notification.IsOk)
            {
                results.Add($"  {mt}: CANCELLED ✓");
            }
            else
            {
                results.Add($"  {mt}: FAILED — {notification}");
            }
        }

        return CommandResult.Ok(
            $"[{conn.Name}] Cancel-all results:\n{string.Join("\n", results)}",
            new { Server = conn.Name, Symbol = symbol ?? "ALL", Action = "CANCEL_ALL", MarketTypes = MarketTypesToStrings(marketTypes) });
    }

    private CommandResult ClosePosition(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: orders close <symbol> [percentage] --confirm\n  percentage: 0-100, default 100");
        }

        string? symbol = args[0].ToUpperInvariant();
        double percentage = 100.0;
        if (args.Length >= 2 && double.TryParse(args[1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double pct))
        {
            percentage = pct;
        }

        // Find position from AccountStore
        IReadOnlyList<PositionSnapshot>? positions = conn.AccountStore.GetPositions(openOnly: true);
        PositionSnapshot? position = null;
        foreach (PositionSnapshot p in positions)
        {
            if (p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            {
                position = p;
                break;
            }
        }

        if (position == null)
        {
            return CommandResult.Fail($"[{conn.Name}] No open position for {symbol}.");
        }

        if (!confirmed)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] ⚠ Close {percentage}% of {symbol} position?\n" +
                $"  Amount: {position.Amount}, Entry: {position.EntryPrice}, PnL: {position.UnrealizedPnl:F2}\n" +
                $"  Side: {position.PositionSide}, Leverage: {position.Leverage}x, Margin: {position.MarginType}\n" +
                $"  Re-run with --confirm flag:\n" +
                $"  orders close {symbol} {percentage} --confirm");
        }

        // Build PositionData from our snapshot — set ALL available fields
        var posData = new PositionData
        {
            marketType = position.MarketType,
            symbol = position.Symbol,
            positionAmount = position.Amount,
            entryPrice = position.EntryPrice,
            positionSide = position.PositionSide,
            leverage = position.Leverage,
            margin = position.Margin,
            marginType = position.MarginType,
            unrealizedPNL = position.UnrealizedPnl,
            liquidationPrice = position.LiquidationPrice,
            positionStatus = position.PositionStatus,
            creationTime = position.CreationTime
        };

        NotificationMessageData? notification = conn.ClosePosition(
            conn.Profile.Exchange, posData, OrderType.MARKET, percentage / 100.0);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Close {symbol}: sent (response timed out).");
        }

        return notification.IsOk
            ? CommandResult.Ok($"[{conn.Name}] {symbol} position: CLOSED {percentage}% ✓",
                new { Server = conn.Name, Symbol = symbol, Percentage = percentage, Action = "CLOSE" })
            : CommandResult.Fail($"[{conn.Name}] Close {symbol} FAILED — {notification.notificationCode}: {notification}");
    }

    private CommandResult CloseAllPositions(string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        IReadOnlyList<PositionSnapshot>? positions = conn.AccountStore.GetPositions(openOnly: true);
        if (positions.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] No open positions.");
        }

        if (!confirmed)
        {
            var symbolList = new List<string>(positions.Count);
            foreach (PositionSnapshot p in positions)
            {
                symbolList.Add(p.Symbol);
            }

            string? symbols = string.Join(", ", symbolList);
            return CommandResult.Fail(
                $"[{conn.Name}] ⚠ Close ALL {positions.Count} open position(s)?\n" +
                $"  Symbols: {symbols}\n" +
                $"  Re-run with --confirm flag:\n" +
                $"  orders close-all --confirm");
        }

        var results = new List<string>();
        foreach (PositionSnapshot position in positions)
        {
            var posData = new PositionData
            {
                marketType = position.MarketType,
                symbol = position.Symbol,
                positionAmount = position.Amount,
                entryPrice = position.EntryPrice,
                positionSide = position.PositionSide,
                leverage = position.Leverage,
                margin = position.Margin,
                marginType = position.MarginType,
                unrealizedPNL = position.UnrealizedPnl,
                liquidationPrice = position.LiquidationPrice,
                positionStatus = position.PositionStatus,
                creationTime = position.CreationTime
            };

            NotificationMessageData? notification = conn.ClosePosition(
                conn.Profile.Exchange, posData, OrderType.MARKET, 1.0);

            if (notification == null)
            {
                results.Add($"  {position.Symbol}: sent (timed out)");
            }
            else if (notification.IsOk)
            {
                results.Add($"  {position.Symbol}: CLOSED ✓");
            }
            else
            {
                results.Add($"  {position.Symbol}: FAILED — {notification}");
            }
        }

        return CommandResult.Ok(
            $"[{conn.Name}] Close-all results:\n{string.Join("\n", results)}",
            new { Server = conn.Name, Action = "CLOSE_ALL", Count = positions.Count });
    }

    #region New Order Operations

    private CommandResult PlaceOrder(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        // orders place <symbol> <side> <qty> [price] [--type LIMIT|MARKET] [--tif GTC|IOC|FOK]
        if (args.Length < 3)
        {
            return CommandResult.Fail(
                "Usage: orders place <symbol> <BUY|SELL> <qty> [price] [--type LIMIT|MARKET] [--tif GTC|IOC|FOK] [--reduce-only] [--position-side BOTH|LONG|SHORT] --confirm\n" +
                "Usage: orders place <symbol> <BUY|SELL> <qty> [price] [--type LIMIT|MARKET] [--tif GTC|IOC|FOK] [--reduce-only] [--emulated] [--iceberg] --confirm\n" +
                "  If price is omitted or 0, places a MARKET order.\n" +
                "  Examples:\n" +
                "    orders place BTCUSDT BUY 0.001 --confirm              (market buy)\n" +
                "    orders place BTCUSDT SELL 0.001 50000 --confirm       (limit sell at 50000)\n" +
                "    orders place ETHUSDT BUY 0.1 3000 --tif IOC --confirm (IOC limit buy)");
        }

        string? symbol = args[0].ToUpperInvariant();

        if (!Enum.TryParse<OrderSideType>(args[1].ToUpperInvariant(), out OrderSideType side))
        {
            return CommandResult.Fail($"Invalid side: {args[1]}. Use BUY or SELL.");
        }

        if (!decimal.TryParse(args[2], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out decimal qty) || qty <= 0)
        {
            return CommandResult.Fail($"Invalid quantity: {args[2]}");
        }

        decimal price = 0;
        int nextArg = 3;
        if (args.Length > 3 && decimal.TryParse(args[3], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out decimal parsedPrice))
        {
            price = parsedPrice;
            nextArg = 4;
        }

        // Parse optional flags from remaining args
        OrderType orderType = price > 0 ? OrderType.LIMIT : OrderType.MARKET;
        TimeInForce tif = TimeInForce.GTC;
        bool reduceOnly = false;
        PositionSide positionSideOverride = PositionSide.BOTH;
        bool hasPositionSideOverride = false;
        bool emulated = false;
        bool iceberg = false;
        MarketType marketOverride = MarketType.FUTURES;
        bool hasMarketOverride = false;
        string? clientOrderIdOverride = null;
        // TPSL inline-placement params. When tpPercent / slPercent are set,
        // OrderRequestData.{takeProfitSettings,stopLossSettings} are populated
        // so MTCore tracks the trade through TPSL bookkeeping (the only path
        // that reaches the reports Firebird DB on close).
        float tpPercent = 0f;
        OrderType tpType = OrderType.LIMIT;
        float slPercent = 0f;
        OrderType slType = OrderType.MARKET;
        bool trailingStop = false;
        float trailingSpread = 0f;

        for (int i = nextArg; i < args.Length; i++)
        {
            if (args[i].Equals("--type", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (Enum.TryParse<OrderType>(args[++i].ToUpperInvariant(), out OrderType ot))
                {
                    orderType = ot;
                }
            }
            else if (args[i].Equals("--tif", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (Enum.TryParse<TimeInForce>(args[++i].ToUpperInvariant(), out TimeInForce t))
                {
                    tif = t;
                }
            }
            else if (args[i].Equals("--reduce-only", StringComparison.OrdinalIgnoreCase))
            {
                reduceOnly = true;
            }
            else if (args[i].Equals("--position-side", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (Enum.TryParse<PositionSide>(args[++i].ToUpperInvariant(), out PositionSide ps))
                {
                    positionSideOverride = ps;
                    hasPositionSideOverride = true;
                }
            }
            else if (args[i].Equals("--emulated", StringComparison.OrdinalIgnoreCase))
            {
                emulated = true;
            }
            else if (args[i].Equals("--iceberg", StringComparison.OrdinalIgnoreCase))
            {
                iceberg = true;
            }
            else if (args[i].Equals("--market", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (Enum.TryParse<MarketType>(args[++i].ToUpperInvariant(), out MarketType mt))
                {
                    marketOverride = mt;
                    hasMarketOverride = true;
                }
            }
            else if (args[i].Equals("--client-order-id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                clientOrderIdOverride = args[++i];
            }
            else if (args[i].Equals("--tp-percent", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out tpPercent);
            }
            else if (args[i].Equals("--tp-type", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                Enum.TryParse(args[++i].ToUpperInvariant(), out tpType);
            }
            else if (args[i].Equals("--sl-percent", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out slPercent);
            }
            else if (args[i].Equals("--sl-type", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                Enum.TryParse(args[++i].ToUpperInvariant(), out slType);
            }
            else if (args[i].Equals("--trailing-stop", StringComparison.OrdinalIgnoreCase))
            {
                trailingStop = true;
            }
            else if (args[i].Equals("--trailing-spread", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out trailingSpread);
            }
        }

        // Determine market type.  Explicit --market wins outright; otherwise fall back
        // to the cached pair-info, with FUTURES as the default when the symbol isn't
        // in the exchange-info store yet.
        MarketType marketType;
        if (hasMarketOverride)
        {
            marketType = marketOverride;
        }
        else
        {
            marketType = MarketType.FUTURES;
            TradePairSnapshot? pairInfo = conn.ExchangeInfoStore.GetTradePair(symbol);
            if (pairInfo != null)
            {
                marketType = pairInfo.MarketType;
            }
        }

        // Determine position side: explicit override > auto-derive (FUTURES + HEDGE only).
        // SPOT / MARGIN orders always use BOTH on Bybit even if the account is
        // flagged HEDGE — the hedge flag only governs derivatives.
        //
        // The auto-derive path used to default to BOTH when AccountStore was cold
        // (no AccountInfoData yet on a fresh connect), which is silently WRONG on
        // a HEDGE-mode account: BYBIT rejects with "position idx not match position
        // mode" (retCode 10001). Prime the cache via ForceRefreshAccount on the
        // first auto-derive miss so the second read returns the real mode, and
        // surface a clear error if the prime still doesn't yield a snapshot.
        PositionSide positionSide;
        if (hasPositionSideOverride)
        {
            positionSide = positionSideOverride;
        }
        else if (marketType != MarketType.FUTURES)
        {
            // Spot / margin: position mode does not apply.
            positionSide = PositionSide.BOTH;
        }
        else
        {
            // Auto-derive priority:
            //   1. An open position for this symbol — match its positionSide
            //      (the venue's own state of truth; survives mismatches between
            //      MTCore's reported PositionMode and the per-symbol mode that
            //      BYBIT may have overridden).
            //   2. The per-market AccountInfo.PositionMode snapshot.
            //
            // BYBIT sends one AccountInfoData drop per market; the SPOT drop's
            // positionModeType is meaningless and defaults to HEDGE=0. Use the
            // per-market accessor so we read FUTURES positionMode, not whichever
            // market happened to land last in the long-lived store.
            PositionSnapshot? openForSymbol = null;
            foreach (var p in conn.AccountStore.GetPositions(openOnly: true))
            {
                if (p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) && p.MarketType == marketType)
                {
                    openForSymbol = p;
                    break;
                }
            }
            if (openForSymbol != null)
            {
                positionSide = openForSymbol.PositionSide;
            }
            else
            {
                AccountInfoSnapshot? accountInfo = conn.AccountStore.GetAccountInfo(marketType);
                if (accountInfo == null)
                {
                    conn.ForceRefreshAccount();
                    accountInfo = conn.AccountStore.GetAccountInfo(marketType);
                }

                if (accountInfo == null)
                {
                    return CommandResult.Fail(
                        $"[{conn.Name}] Cannot auto-derive position_side for {marketType}: " +
                        "AccountInfo cache is empty for that market and the force-refresh " +
                        "transient subscribe yielded no AccountInfoData. Pass --position-side " +
                        "BOTH|LONG|SHORT explicitly, or retry once the connection has settled.");
                }

                positionSide = accountInfo.PositionMode switch
                {
                    PositionModeType.HEDGE   => (side == OrderSideType.BUY ? PositionSide.LONG : PositionSide.SHORT),
                    PositionModeType.ONE_WAY => PositionSide.BOTH,
                    _                        => PositionSide.BOTH,
                };
            }
        }

        if (!confirmed)
        {
            string? typeStr = orderType == OrderType.MARKET ? "MARKET" : $"LIMIT @ {price}";
            return CommandResult.Fail(
                $"[{conn.Name}] ⚠ Place {side} {qty} {symbol} ({typeStr}, TIF={tif}, ReduceOnly={reduceOnly}, Emulated={emulated})?\n" +
                $"  Market: {marketType}, PositionSide: {positionSide}\n" +
                $"  Re-run with --confirm flag.");
        }

        var orderRequest = new OrderRequestData
        {
            exchangeType = conn.Profile.Exchange,
            marketType = marketType,
            symbol = symbol,
            orderSideType = side,
            qty = qty,
            price = price,
            positionSide = positionSide,
            useReduceOnly = reduceOnly,
            orderSettings = new OrderSettings
            {
                clientOrderType = orderType == OrderType.MARKET
                    ? ClientOrderType.MARKET
                    : ClientOrderType.LIMIT,
                isEmulationOn = emulated,
                isIceberg = iceberg,
            }
        };

        // Attach TPSL settings inline when the caller asked for them. Setting
        // takeProfitSettings.isOn / stopLossSettings.isOn = true is what
        // routes MTCore through its TPSL bookkeeping path on close — the
        // only path that writes a row into the reports Firebird DB.
        // See docs/TPSL_SAFETY_GUIDE.md for the full pattern.
        if (tpPercent > 0f)
        {
            orderRequest.takeProfitSettings = new TakeProfitSettings
            {
                isOn = true,
                percentage = tpPercent,
                orderType = tpType,
            };
        }
        if (slPercent > 0f)
        {
            var sl = new StopLossSettings
            {
                isOn = true,
                percentage = slPercent,
                orderType = slType,
            };
            if (trailingStop)
            {
                sl.tralingIsOn = true;
                if (trailingSpread > 0f) { sl.trailingSpread = trailingSpread; }
            }
            orderRequest.stopLossSettings = sl;
        }
        if (!string.IsNullOrEmpty(clientOrderIdOverride))
        {
            // MTShared OrderRequestData carries clientOrderId; the venue may still
            // overwrite it with its broker-prefix (Binance/OKX), but for the cases
            // where the venue passes it through (Bybit), our id survives end to end.
            orderRequest.clientOrderId = clientOrderIdOverride;
        }

        // BUG-2 fix: snapshot pre-place open-order ids so we can recover the
        // exchange-assigned ClientOrderId post-send (the wire response does not
        // echo it directly; we diff the AccountStore instead).
        var preIds = new HashSet<string>(
            (conn.AccountStore.GetOrders(activeOnly: true) ?? Array.Empty<OrderSnapshot>())
                .Select(o => o.ClientOrderId ?? string.Empty));

        NotificationMessageData? notification = conn.PlaceOrder(orderRequest);

        // BYBIT venue-drift recovery: the venue can reject with
        // retCode 10001 "position idx not match position mode" when the
        // account-wide PositionMode flag we derived from disagrees with the
        // per-symbol mode the venue actually enforces (some venues advertise
        // an account-wide PositionMode that disagrees with the per-symbol
        // mode actually enforced). When auto-derive picked the wrong side AND
        // no override was supplied, retry once with the alternate side.
        if (notification != null
            && !hasPositionSideOverride
            && marketType == MarketType.FUTURES
            && notification.msgString != null
            && notification.msgString.IndexOf("position idx not match position mode", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            PositionSide retrySide = positionSide == PositionSide.BOTH
                ? (side == OrderSideType.BUY ? PositionSide.LONG : PositionSide.SHORT)
                : PositionSide.BOTH;
            orderRequest.positionSide = retrySide;
            NotificationMessageData? retryNotif = conn.PlaceOrder(orderRequest);
            if (retryNotif != null && retryNotif.IsOk)
            {
                notification = retryNotif;
                positionSide = retrySide;
            }
            else if (retryNotif != null)
            {
                // Retry also failed — surface BOTH attempts in the message so
                // the operator can see the venue-drift signal clearly.
                notification = new NotificationMessageData
                {
                    notificationCode = retryNotif.notificationCode,
                    msgString = $"position_side retry FAILED on both sides. " +
                        $"first({positionSide})={notification.msgString}; " +
                        $"second({retrySide})={retryNotif.msgString}",
                };
            }
        }

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Place {side} {qty} {symbol}: sent (response timed out).");
        }

        // Best-effort coid recovery: poll up to ~1s for the new order to appear
        // in AccountStore. If unavailable (race / paper-only / market-fill),
        // we still return success without a coid rather than failing.
        string? newCoid = null;
        if (notification.IsOk)
        {
            for (int attempt = 0; attempt < 10 && newCoid == null; attempt++)
            {
                var post = conn.AccountStore.GetOrders(activeOnly: true) ?? Array.Empty<OrderSnapshot>();
                var match = post.FirstOrDefault(o =>
                    o.ClientOrderId is not null
                    && !preIds.Contains(o.ClientOrderId)
                    && string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                    && o.Side == side);
                if (match is not null) { newCoid = match.ClientOrderId; break; }
                System.Threading.Thread.Sleep(100);
            }
        }

        return notification.IsOk
            ? CommandResult.Ok($"[{conn.Name}] Order placed: {side} {qty} {symbol} ✓" + (emulated ? " (emulated)" : "") + (newCoid != null ? $" [{newCoid}]" : ""),
                new { Server = conn.Name, Symbol = symbol, Side = side.ToString(), Qty = qty, Price = price, Type = orderType.ToString(), Emulated = emulated, ClientOrderId = newCoid, Action = "PLACE" })
            : CommandResult.Fail($"[{conn.Name}] Place order FAILED — {notification.notificationCode}: {notification}");
    }

    private CommandResult MoveOrder(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 2)
        {
            return CommandResult.Fail("Usage: orders move <clientOrderId> <newPrice> --confirm");
        }

        string? clientOrderId = args[0];
        if (!double.TryParse(args[1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double newPrice) || newPrice <= 0)
        {
            return CommandResult.Fail($"Invalid price: {args[1]}");
        }

        // Find the order to get its market type
        IReadOnlyList<OrderSnapshot>? orders = conn.AccountStore.GetOrders(activeOnly: true);
        OrderSnapshot? order = null;
        foreach (OrderSnapshot o in orders)
        {
            if (o.ClientOrderId == clientOrderId)
            {
                order = o;
                break;
            }
        }

        MarketType marketType = order?.MarketType ?? MarketType.FUTURES;

        if (!confirmed)
        {
            string? info = order != null ? $" ({order.Symbol} {order.Side} currently @ {order.Price})" : "";
            return CommandResult.Fail(
                $"[{conn.Name}] ⚠ Move order {clientOrderId}{info} to {newPrice}?\n" +
                $"  Re-run with --confirm flag.");
        }

        // BUG-5 fix: snapshot the pre-move active-order id set so we can
        // detect cancel-and-replace exchanges (Bybit) where MoveOrder
        // produces a new ClientOrderId server-side.
        var preIds = new HashSet<string>(
            (conn.AccountStore.GetOrders(activeOnly: true) ?? Array.Empty<OrderSnapshot>())
                .Select(o => o.ClientOrderId ?? string.Empty));

        NotificationMessageData? notification = conn.MoveOrder(
            conn.Profile.Exchange, marketType, clientOrderId, newPrice);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Move order {clientOrderId} to {newPrice}: sent (response timed out).");
        }

        // Best-effort post-move id recovery. On Binance the id is preserved
        // (newCoid == clientOrderId); on Bybit it changes.
        string newCoid = clientOrderId;
        if (notification.IsOk)
        {
            decimal targetPrice = (decimal)newPrice;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                var post = conn.AccountStore.GetOrders(activeOnly: true) ?? Array.Empty<OrderSnapshot>();
                // Same id, repriced -> Binance-style; trust it.
                var same = post.FirstOrDefault(o => o.ClientOrderId == clientOrderId);
                if (same is not null && same.Price == targetPrice) { newCoid = clientOrderId; break; }
                // Brand-new id, same symbol/side/qty, repriced -> Bybit-style.
                var replaced = post.FirstOrDefault(o =>
                    o.ClientOrderId is not null
                    && !preIds.Contains(o.ClientOrderId)
                    && order is not null
                    && string.Equals(o.Symbol, order.Symbol, StringComparison.OrdinalIgnoreCase)
                    && o.Side == order.Side
                    && o.Quantity == order.Quantity
                    && o.Price == targetPrice);
                if (replaced is not null) { newCoid = replaced.ClientOrderId!; break; }
                System.Threading.Thread.Sleep(100);
            }
        }

        bool replacedId = newCoid != clientOrderId;
        return notification.IsOk
            ? CommandResult.Ok($"[{conn.Name}] Order {clientOrderId} moved to {newPrice} ✓" + (replacedId ? $" [new id: {newCoid}]" : ""),
                new { Server = conn.Name, ClientOrderId = newCoid, OriginalClientOrderId = clientOrderId, NewPrice = newPrice, Replaced = replacedId, Action = "MOVE" })
            : CommandResult.Fail($"[{conn.Name}] Move order FAILED — {notification.notificationCode}: {notification}");
    }

    private CommandResult SetLeverage(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 2)
        {
            return CommandResult.Fail("Usage: orders set-leverage <symbol> <leverage> --confirm\n  e.g. orders set-leverage BTCUSDT 10 --confirm");
        }

        string? symbol = args[0].ToUpperInvariant();
        if (!short.TryParse(args[1], out short leverage) || leverage < 1 || leverage > 125)
        {
            return CommandResult.Fail($"Invalid leverage: {args[1]}. Must be 1-125.");
        }

        if (!confirmed)
        {
            return CommandResult.Fail($"[{conn.Name}] ⚠ Set {symbol} leverage to {leverage}x? Re-run with --confirm.");
        }

        MarketType marketType = MarketType.FUTURES;
        TradePairSnapshot? pairInfo = conn.ExchangeInfoStore.GetTradePair(symbol);
        if (pairInfo != null)
        {
            marketType = pairInfo.MarketType;
        }

        NotificationMessageData? notification = conn.ModifyLeverage(marketType, symbol, leverage);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Set leverage {symbol} {leverage}x: sent (timed out).");
        }

        return notification.IsOk
            ? CommandResult.Ok($"[{conn.Name}] {symbol} leverage set to {leverage}x ✓",
                new { Server = conn.Name, Symbol = symbol, Leverage = leverage, Action = "SET_LEVERAGE" })
            : CommandResult.Fail($"[{conn.Name}] Set leverage FAILED — {notification.notificationCode}: {notification}");
    }

    private CommandResult SetMarginType(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 2)
        {
            return CommandResult.Fail("Usage: orders set-margin-type <symbol> <CROSS|ISOLATED> --confirm");
        }

        string? symbol = args[0].ToUpperInvariant();
        if (!Enum.TryParse<MarginType>(args[1].ToUpperInvariant(), out MarginType marginType) || marginType == MarginType.UNKNOWN)
        {
            return CommandResult.Fail($"Invalid margin type: {args[1]}. Use CROSS or ISOLATED.");
        }

        if (!confirmed)
        {
            return CommandResult.Fail($"[{conn.Name}] ⚠ Set {symbol} margin type to {marginType}? Re-run with --confirm.");
        }

        MarketType marketType = MarketType.FUTURES;
        TradePairSnapshot? pairInfo = conn.ExchangeInfoStore.GetTradePair(symbol);
        if (pairInfo != null)
        {
            marketType = pairInfo.MarketType;
        }

        string? notification = conn.ModifyMarginType(marketType, symbol, marginType);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Set margin type {symbol} {marginType}: sent (timed out).");
        }

        return CommandResult.Ok($"[{conn.Name}] Set margin type {symbol} {marginType}: {notification}",
                new { Server = conn.Name, Symbol = symbol, MarginType = marginType.ToString(), Action = "SET_MARGIN_TYPE" });
    }

    private CommandResult SetPositionMode(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 2)
        {
            return CommandResult.Fail("Usage: orders set-position-mode <symbol> <HEDGE|ONE_WAY> --confirm");
        }

        string? symbol = args[0].ToUpperInvariant();
        if (!Enum.TryParse<PositionModeType>(args[1].ToUpperInvariant(), out PositionModeType mode))
        {
            return CommandResult.Fail($"Invalid mode: {args[1]}. Use HEDGE or ONE_WAY.");
        }

        if (!confirmed)
        {
            return CommandResult.Fail($"[{conn.Name}] ⚠ Set {symbol} position mode to {mode}? Re-run with --confirm.");
        }

        MarketType marketType = MarketType.FUTURES;
        TradePairSnapshot? pairInfo = conn.ExchangeInfoStore.GetTradePair(symbol);
        if (pairInfo != null)
        {
            marketType = pairInfo.MarketType;
        }

        string? notification = conn.SetPositionMode(marketType, symbol, mode);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Set position mode {symbol} {mode}: sent (timed out).");
        }

        return CommandResult.Ok($"[{conn.Name}] Set position mode {symbol} {mode}: {notification}",
                new { Server = conn.Name, Symbol = symbol, Mode = mode.ToString(), Action = "SET_POSITION_MODE" });
    }

    private CommandResult GetPositionMode(string[] args, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: orders get-position-mode <symbol>");
        }

        // Bybit's vendor SDK does not implement GetPositionModeType per symbol —
        // a SendGetPositionModeType call times out (5+s). Redirect callers
        // to the account-wide tool instead of paying that timeout.
        if (conn.Profile.Exchange == ExchangeType.BYBIT)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] Position mode on Bybit is account-wide, not per-symbol. " +
                "Use 'orders get-position-mode-account' (MCP: mt_orders_get_position_mode_account) instead.");
        }

        string? symbol = args[0].ToUpperInvariant();
        MarketType marketType = MarketType.FUTURES;
        TradePairSnapshot? pairInfo = conn.ExchangeInfoStore.GetTradePair(symbol);
        if (pairInfo != null)
        {
            marketType = pairInfo.MarketType;
        }

        string? notification = conn.GetPositionMode(marketType, symbol);

        if (notification == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Get position mode: timed out.");
        }

        return CommandResult.Ok($"[{conn.Name}] Position mode for {symbol}: {notification}",
            new { Server = conn.Name, Symbol = symbol, Response = notification });
    }

    private CommandResult GetPositionModeAccount(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        // Per-symbol exchanges: redirect to the symbol-keyed tool. The account-wide
        // snapshot's PositionMode field is not meaningful on Binance/OKX (mode
        // varies per pair), so we don't return a value that would mislead.
        if (conn.Profile.Exchange != ExchangeType.BYBIT)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] Position mode on {conn.Profile.Exchange} is per-symbol, not account-wide. " +
                "Use 'orders get-position-mode <symbol>' (MCP: mt_orders_get_position_mode) instead.");
        }

        // Prime the AccountStore on first call so a cold-connect read still
        // returns the real mode rather than null. ForceRefreshAccount opens a
        // transient UDS subscribe and returns once AccountInfoData arrives.
        // Bybit's position mode is account-wide, but the FUTURES drop is the
        // one that carries the meaningful value (SPOT drop reports HEDGE=0
        // default for an unused field). Read the FUTURES snapshot specifically.
        AccountInfoSnapshot? snapshot = conn.AccountStore.GetAccountInfo(MarketType.FUTURES);
        if (snapshot == null)
        {
            conn.ForceRefreshAccount();
            snapshot = conn.AccountStore.GetAccountInfo(MarketType.FUTURES);
        }

        if (snapshot == null)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] Position mode unknown — AccountInfo cache is empty and the " +
                "force-refresh transient subscribe did not yield a snapshot. Try again after the " +
                "connection settles.");
        }

        string mode = snapshot.PositionMode.ToString();
        return CommandResult.Ok(
            $"[{conn.Name}] Account-wide position mode: {mode}",
            new { Server = conn.Name, Mode = mode, Exchange = conn.Profile.Exchange.ToString() });
    }

    private CommandResult PanicSell(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: orders panic-sell <symbol> [--deactivate] --confirm\n  Emergency market-close all positions for a symbol.");
        }

        string? symbol = args[0].ToUpperInvariant();
        bool activate = !ContainsIgnoreCase(args, "--deactivate");

        if (!confirmed)
        {
            return CommandResult.Fail($"[{conn.Name}] ⚠⚠ PANIC SELL {symbol}?? This will MARKET CLOSE all positions!\n  Re-run with --confirm flag.");
        }

        MarketType marketType = MarketType.FUTURES;
        TradePairSnapshot? pairInfo = conn.ExchangeInfoStore.GetTradePair(symbol);
        if (pairInfo != null)
        {
            marketType = pairInfo.MarketType;
        }

        string? notification = conn.PanicSell(marketType, symbol, activate);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Panic sell {symbol}: sent (timed out).");
        }

        return CommandResult.Ok($"[{conn.Name}] Panic sell {symbol} {(activate ? "ACTIVATED" : "DEACTIVATED")}: {notification}",
                new { Server = conn.Name, Symbol = symbol, Activated = activate, Action = "PANIC_SELL" });
    }

    private CommandResult ChangeMargin(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 3)
        {
            return CommandResult.Fail(
                "Usage: orders change-margin <symbol> <LONG|SHORT|BOTH> <amount> [add|reduce] --confirm\n" +
                "  Adjust isolated margin on a position. Default action: add.");
        }

        string? symbol = args[0].ToUpperInvariant();
        if (!Enum.TryParse<PositionSide>(args[1].ToUpperInvariant(), out PositionSide posSide))
        {
            return CommandResult.Fail($"Invalid position side: {args[1]}. Use LONG, SHORT, or BOTH.");
        }

        if (!decimal.TryParse(args[2], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
        {
            return CommandResult.Fail($"Invalid amount: {args[2]}");
        }

        bool isAdd = true;
        if (args.Length >= 4 && args[3].Equals("reduce", StringComparison.OrdinalIgnoreCase))
        {
            isAdd = false;
        }

        if (!confirmed)
        {
            string? action = isAdd ? "ADD" : "REDUCE";
            return CommandResult.Fail(
                $"[{conn.Name}] ⚠ {action} {amount} margin on {symbol} {posSide}? Re-run with --confirm.");
        }

        MarketType marketType = MarketType.FUTURES;
        TradePairSnapshot? pairInfo = conn.ExchangeInfoStore.GetTradePair(symbol);
        if (pairInfo != null)
        {
            marketType = pairInfo.MarketType;
        }

        NotificationMessageData? notification = conn.ChangePositionMargin(
            marketType, symbol, posSide, amount, isAdd);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Change margin {symbol}: sent (timed out).");
        }

        return notification.IsOk
            ? CommandResult.Ok($"[{conn.Name}] {symbol} margin {(isAdd ? "added" : "reduced")} by {amount} ✓",
                new { Server = conn.Name, Symbol = symbol, Amount = amount, IsAdd = isAdd, Action = "CHANGE_MARGIN" })
            : CommandResult.Fail($"[{conn.Name}] Change margin FAILED — {notification.notificationCode}: {notification}");
    }

    private CommandResult TransferFunds(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 4)
        {
            return CommandResult.Fail(
                "Usage: orders transfer <asset> <amount> <from> <to> --confirm\n" +
                "  from/to: SPOT or FUTURES\n" +
                "  e.g. orders transfer USDT 100 SPOT FUTURES --confirm");
        }

        string? asset = args[0].ToUpperInvariant();
        if (!double.TryParse(args[1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double amount) || amount <= 0)
        {
            return CommandResult.Fail($"Invalid amount: {args[1]}");
        }

        if (!Enum.TryParse<MarketType>(args[2].ToUpperInvariant(), out MarketType fromMarket))
        {
            return CommandResult.Fail($"Invalid source: {args[2]}. Use SPOT or FUTURES.");
        }

        if (!Enum.TryParse<MarketType>(args[3].ToUpperInvariant(), out MarketType toMarket))
        {
            return CommandResult.Fail($"Invalid destination: {args[3]}. Use SPOT or FUTURES.");
        }

        if (!confirmed)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] ⚠ Transfer {amount} {asset} from {fromMarket} to {toMarket}? Re-run with --confirm.");
        }

        NotificationMessageData? notification = conn.TransferFunds(fromMarket, toMarket, asset, amount);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Transfer {amount} {asset}: sent (timed out).");
        }

        return notification.IsOk
            ? CommandResult.Ok($"[{conn.Name}] Transferred {amount} {asset} from {fromMarket} to {toMarket} ✓",
                new { Server = conn.Name, Asset = asset, Amount = amount, From = fromMarket.ToString(), To = toMarket.ToString(), Action = "TRANSFER" })
            : CommandResult.Fail($"[{conn.Name}] Transfer FAILED — {notification.notificationCode}: {notification}");
    }

    #endregion

    private static List<string> MarketTypesToStrings(List<MarketType> types)
    {
        var result = new List<string>(types.Count);
        foreach (MarketType t in types)
        {
            result.Add(t.ToString());
        }

        return result;
    }

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


    private CommandResult SetLeverageBuySell(string[] args, string? targetProfile, bool confirm)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 3)
        {
            return CommandResult.Fail("Usage: orders set-leverage-buysell <asset> <buy_leverage> <sell_leverage> [market] --confirm");
        }

        if (!confirm)
        {
            return CommandResult.Fail("Add --confirm to proceed with leverage change.");
        }

        string asset = args[0].ToUpperInvariant();
        if (!short.TryParse(args[1], out short buyLev) || !short.TryParse(args[2], out short sellLev))
        {
            return CommandResult.Fail("Leverage values must be integers.");
        }

        MarketType marketType = MarketType.FUTURES;
        if (args.Length > 3)
        {
            Enum.TryParse(args[3], true, out marketType);
        }

        NotificationMessageData? result = conn.ModifyLeverageBuySell(marketType, asset, buyLev, sellLev);
        if (result == null)
        {
            return CommandResult.Fail("Request timed out or connection lost.");
        }

        return CommandResult.Ok($"Leverage set for {asset}: buy={buyLev}x sell={sellLev}x — {result.msgString}");
    }

    private CommandResult GetMultiAssetMode(string[] args, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        MarketType marketType = MarketType.FUTURES;
        if (args.Length > 0)
        {
            Enum.TryParse(args[0], true, out marketType);
        }

        MultiAssetModeResultData? result = conn.GetMultiAssetMode(marketType);
        if (result == null)
        {
            return CommandResult.Fail("Request timed out or connection lost.");
        }

        return CommandResult.Ok(
            $"Multi-asset mode ({marketType}): {(result.isMultiAssetModeEnabled ? "ENABLED" : "DISABLED")}",
            new { enabled = result.isMultiAssetModeEnabled });
    }

    private CommandResult SetMultiAssetMode(string[] args, string? targetProfile, bool confirm)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: orders set-multiasset <true|false> [market] --confirm");
        }

        if (!confirm)
        {
            return CommandResult.Fail("Add --confirm to proceed with multiasset mode change.");
        }

        bool enabled = args[0].Equals("true", StringComparison.OrdinalIgnoreCase) ||
                       args[0].Equals("on", StringComparison.OrdinalIgnoreCase) ||
                       args[0].Equals("1", StringComparison.OrdinalIgnoreCase);

        MarketType marketType = MarketType.FUTURES;
        if (args.Length > 1)
        {
            Enum.TryParse(args[1], true, out marketType);
        }

        MultiAssetModeResultData? result = conn.SetMultiAssetMode(marketType, enabled);
        if (result == null)
        {
            return CommandResult.Fail("Request timed out or connection lost.");
        }

        return CommandResult.Ok(
            $"Multi-asset mode ({marketType}): set to {(enabled ? "ENABLED" : "DISABLED")} — {result.msgString}",
            new { enabled = result.isMultiAssetModeEnabled, message = result.msgString });
    }

    private CommandResult MoveBatchOrders(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: orders move-batch <orders_json> [market] --confirm\n  JSON: {\"clientOrderId\": newPrice, ...}");
        }

        if (!confirmed)
        {
            return CommandResult.Fail($"[{conn.Name}] ⚠ Move batch orders? Re-run with --confirm.");
        }

        MarketType marketType = MarketType.FUTURES;
        string ordersJson = args[0];

        if (args.Length > 1)
        {
            if (Enum.TryParse<MarketType>(args[1].ToUpperInvariant(), out MarketType mt))
            {
                marketType = mt;
            }
        }

        var orders = new Dictionary<string, decimal>();
        try
        {
            var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, decimal>>(ordersJson);
            if (parsed != null)
            {
                orders = parsed;
            }
        }
        catch
        {
            return CommandResult.Fail("Invalid JSON. Expected: {\"clientOrderId\": newPrice, ...}");
        }

        string result = conn.MoveBatchOrders(marketType, orders);
        return CommandResult.Ok($"[{conn.Name}] Move batch ({orders.Count} orders): {result}");
    }

    private CommandResult JoinOrder(string[] args, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: orders join <client_order_id> [market]");
        }

        string clientOrderId = args[0];
        MarketType marketType = MarketType.FUTURES;
        if (args.Length > 1)
        {
            if (Enum.TryParse<MarketType>(args[1].ToUpperInvariant(), out MarketType mt))
            {
                marketType = mt;
            }
        }

        string result = conn.JoinOrder(marketType, clientOrderId);
        return CommandResult.Ok($"[{conn.Name}] Join order {clientOrderId}: {result}");
    }

    private CommandResult SplitOrder(string[] args, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: orders split <client_order_id> [count] [percentage] [market]");
        }

        string clientOrderId = args[0];
        byte count = 2;
        float percentage = 50f;
        MarketType marketType = MarketType.FUTURES;

        if (args.Length > 1) byte.TryParse(args[1], out count);
        if (args.Length > 2) float.TryParse(args[2], out percentage);
        if (args.Length > 3)
        {
            if (Enum.TryParse<MarketType>(args[3].ToUpperInvariant(), out MarketType mt))
            {
                marketType = mt;
            }
        }

        string result = conn.SplitOrder(marketType, clientOrderId, count, percentage);
        return CommandResult.Ok($"[{conn.Name}] Split order {clientOrderId} into {count} parts: {result}");
    }

    private CommandResult TransferAccountFunds(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 4)
        {
            return CommandResult.Fail("Usage: orders fund-transfer <FUNDING|TRADING> <asset> <amount> <FUNDING|TRADING> --confirm");
        }

        if (!confirmed)
        {
            return CommandResult.Fail($"[{conn.Name}] ⚠ Transfer funds? Re-run with --confirm.");
        }

        if (!Enum.TryParse<AccountType>(args[0].ToUpperInvariant(), out AccountType fromAccount) ||
            fromAccount == AccountType.UNKNOWN)
        {
            return CommandResult.Fail($"Invalid from account: {args[0]}. Use FUNDING or TRADING.");
        }

        string asset = args[1].ToUpperInvariant();
        if (!double.TryParse(args[2], out double amount))
        {
            return CommandResult.Fail($"Invalid amount: {args[2]}");
        }

        if (!Enum.TryParse<AccountType>(args[3].ToUpperInvariant(), out AccountType toAccount) ||
            toAccount == AccountType.UNKNOWN)
        {
            return CommandResult.Fail($"Invalid to account: {args[3]}. Use FUNDING or TRADING.");
        }

        string result = conn.TransferFunds(fromAccount, asset, amount, toAccount);
        return CommandResult.Ok($"[{conn.Name}] Transfer {amount} {asset} from {fromAccount} to {toAccount}: {result}");
    }

    private CommandResult ClosePositionByTPSL(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("Not connected. Use: connect <profile>");
        }

        if (!confirmed)
        {
            return CommandResult.Fail("close-by-tpsl requires --confirm. This will close the position via TPSL.");
        }

        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: orders close-by-tpsl <symbol> [--market <type>] [--side <LONG|SHORT>] --confirm");
        }

        string symbol = args[0];
        MarketType marketType = MarketType.FUTURES;
        PositionSide posSide = PositionSide.BOTH;
        // order_type defaults to MARKET (back-compat with earlier behaviour).
        // The MCP wrapper exposes the semantic via the order_type schema field;
        // callers that pass no override still get MARKET.
        OrderType orderType = OrderType.MARKET;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--market" && i + 1 < args.Length)
            {
                Enum.TryParse(args[++i], true, out marketType);
            }
            else if (args[i] == "--side" && i + 1 < args.Length)
            {
                Enum.TryParse(args[++i], true, out posSide);
            }
            else if (args[i] == "--order-type" && i + 1 < args.Length)
            {
                // Semantic-first per CD-6: v423's controller has the
                // "close-to-report single/list" name pair swapped; we use
                // the order_type value semantically (MARKET = immediate
                // close, LIMIT = post-only-style close at last price).
                Enum.TryParse(args[++i], true, out orderType);
            }
        }

        // Echo the full cached PositionData. The server identifies the
        // target by the full position state (amount, entry price, margin,
        // liquidation price, …), not by symbol+side alone — a minimal stub
        // is silently rejected with an empty error response.
        PositionData? posData = conn.AccountStore.GetPositionRaw(symbol, posSide);
        if (posData == null)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] No open position for {symbol} ({posSide}) in the local cache. " +
                "Run `account positions` first to populate the cache, then retry.");
        }

        NotificationMessageData? result = conn.ClosePositionByTPSL(
            conn.Profile.Exchange, posData, orderType);
        if (result == null)
        {
            return CommandResult.Fail("No response from close-by-tpsl.");
        }

        return result.IsOk
            ? CommandResult.Ok($"[{conn.Name}] Close-by-TPSL ({orderType}): {result.notificationCode}")
            : CommandResult.Fail($"[{conn.Name}] Close-by-TPSL ({orderType}) failed: {result.notificationCode} — {result.jsonData}");
    }

    private CommandResult ResetPositionTPSL(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("Not connected. Use: connect <profile>");
        }

        if (!confirmed)
        {
            return CommandResult.Fail("reset-tpsl requires --confirm. This will reset TP/SL on the position.");
        }

        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: orders reset-tpsl <symbol> [--market <type>] [--side <LONG|SHORT>] --confirm");
        }

        string symbol = args[0];
        MarketType marketType = MarketType.FUTURES;
        PositionSide posSide = PositionSide.BOTH;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--market" && i + 1 < args.Length)
            {
                Enum.TryParse(args[++i], true, out marketType);
            }
            else if (args[i] == "--side" && i + 1 < args.Length)
            {
                Enum.TryParse(args[++i], true, out posSide);
            }
        }

        // Echo the cached PositionData with its full identity tuple; an
        // amount/entry/margin stub is silently rejected by the server.
        PositionData? posData = conn.AccountStore.GetPositionRaw(symbol, posSide);
        if (posData == null)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] No open position for {symbol} ({posSide}) in the local cache. " +
                "Run `account positions` first to populate it, then retry.");
        }

        TakeProfitSettings tpSettings = new TakeProfitSettings();
        StopLossSettings slSettings = new StopLossSettings();

        NotificationMessageData? result = conn.ResetTPSL(
            conn.Profile.Exchange, posData, tpSettings, slSettings);
        if (result == null)
        {
            return CommandResult.Fail("No response from reset-tpsl.");
        }

        return result.IsOk
            ? CommandResult.Ok($"[{conn.Name}] Reset TPSL: {result.notificationCode}")
            : CommandResult.Fail($"[{conn.Name}] Reset TPSL failed: {result.notificationCode} — {result.jsonData}");
    }

    /// <summary>
    /// Update Take-Profit / Stop-Loss (and optional trailing spread) on
    /// an active order or open position.  Wires
    /// <see cref="CoreConnection.UpdateOrderTPSL"/> →
    /// <c>SendOrderTPSLUpdateRequest</c> on the MTShared wire.
    ///
    /// CLI form:
    ///   orders update-tpsl &lt;symbol&gt; &lt;BUY|SELL&gt;
    ///         [--market FUTURES|SPOT|MARGIN|DELIVERY]
    ///         [--position-side BOTH|LONG|SHORT]
    ///         [--tp &lt;percent&gt;] [--sl &lt;percent&gt;] [--trailing-spread &lt;percent&gt;]
    ///         [--client-order-id &lt;id&gt;] --confirm
    /// </summary>
    private CommandResult UpdateOrderTpsl(string[] args, string? targetProfile, bool confirmed)
    {
        if (!confirmed)
        {
            return CommandResult.Fail(
                "orders update-tpsl requires --confirm. This mutates open position TP/SL state.");
        }
        if (args.Length < 2)
        {
            return CommandResult.Fail(
                "Usage: orders update-tpsl <symbol> <BUY|SELL> " +
                "[--market FUTURES|SPOT|MARGIN|DELIVERY] " +
                "[--position-side BOTH|LONG|SHORT] " +
                "[--tp <percent>] [--sl <percent>] [--trailing-spread <percent>] " +
                "[--client-order-id <id>] --confirm");
        }

        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        string symbol = args[0].ToUpperInvariant();
        if (!Enum.TryParse<OrderSideType>(args[1].ToUpperInvariant(), out OrderSideType side))
        {
            return CommandResult.Fail($"Invalid side: {args[1]}. Use BUY or SELL.");
        }

        MarketType marketType = MarketType.FUTURES;
        PositionSide positionSide = side == OrderSideType.BUY ? PositionSide.LONG : PositionSide.SHORT;
        bool hasPositionSideOverride = false;
        decimal tpPercent = 0m, slPercent = 0m, trailSpread = 0m;
        bool hasTrail = false;
        string? clientOrderId = null;

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i].Equals("--market", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                Enum.TryParse(args[++i].ToUpperInvariant(), out marketType);
            }
            else if (args[i].Equals("--position-side", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (Enum.TryParse<PositionSide>(args[++i].ToUpperInvariant(), out PositionSide ps))
                {
                    positionSide = ps;
                    hasPositionSideOverride = true;
                }
            }
            else if (args[i].Equals("--tp", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                decimal.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out tpPercent);
            }
            else if (args[i].Equals("--sl", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                decimal.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out slPercent);
            }
            else if (args[i].Equals("--trailing-spread", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (decimal.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out trailSpread))
                    hasTrail = true;
            }
            else if (args[i].Equals("--client-order-id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                clientOrderId = args[++i];
            }
        }

        if (tpPercent <= 0m && slPercent <= 0m)
        {
            return CommandResult.Fail(
                "orders update-tpsl: at least one of --tp / --sl must be > 0 (otherwise there's nothing to update).");
        }

        // The server identifies the target order by the full identity
        // tuple in OrderRequestData (clientOrderId + parentClientOrderId +
        // orderSettings + qty + price + stopPrice). A sparse request
        // built only from MCP arguments is silently rejected — the cached
        // OrderData must be echoed back with only the TP/SL settings
        // mutated. Look up the order by clientOrderId, or fall back to a
        // unique symbol+side match in the active orders list.
        OrderData? cachedOrder = null;
        if (!string.IsNullOrEmpty(clientOrderId))
        {
            cachedOrder = conn.AccountStore.GetOrderRaw(clientOrderId!);
        }
        if (cachedOrder == null)
        {
            // No clientOrderId or stale id — try a symbol+side match in
            // the active orders. If exactly one matches, use it.
            var candidates = new List<OrderSnapshot>();
            foreach (OrderSnapshot snap in conn.AccountStore.GetOrders(activeOnly: true))
            {
                if (string.Equals(snap.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                    && snap.Side == side
                    && (!hasPositionSideOverride || snap.PositionSide == positionSide))
                {
                    candidates.Add(snap);
                }
            }
            if (candidates.Count == 1)
            {
                cachedOrder = conn.AccountStore.GetOrderRaw(candidates[0].ClientOrderId);
            }
            else if (candidates.Count > 1)
            {
                return CommandResult.Fail(
                    $"[{conn.Name}] {candidates.Count} active orders match {symbol} {side}. " +
                    "Pass --client-order-id <id> to disambiguate.");
            }
        }
        if (cachedOrder == null)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] No active order found for {symbol} {side} in the local cache. " +
                "Run `account orders` first to populate it, then retry with --client-order-id <id>.");
        }

        // TakeProfitSettings / StopLossSettings.isOn = true arms the leg;
        // isOn = false leaves it disabled. Percentage / trailingSpread are
        // `float` in MTShared.
        var tp = new TakeProfitSettings
        {
            isOn = tpPercent > 0m,
            percentage = (float)tpPercent,
        };
        var sl = new StopLossSettings
        {
            isOn = slPercent > 0m,
            percentage = (float)slPercent,
            tralingIsOn = hasTrail && slPercent > 0m,
            trailingSpread = (float)trailSpread,
        };

        // Echo the cached OrderData back with mutated TP/SL settings.
        var orderRequest = new OrderRequestData
        {
            exchangeType         = conn.Profile.Exchange,
            marketType           = cachedOrder.marketType,
            symbol               = cachedOrder.symbol,
            orderSideType        = cachedOrder.side,
            positionSide         = hasPositionSideOverride ? positionSide : cachedOrder.positionSide,
            qty                  = cachedOrder.qty,
            price                = cachedOrder.price,
            stopPrice            = cachedOrder.stopPrice,
            clientOrderId        = cachedOrder.clientOrderId,
            parentClientOrderId  = cachedOrder.parentClientOrderId,
            orderSettings        = cachedOrder.orderSettings,
            takeProfitSettings   = tp,
            stopLossSettings     = sl,
        };

        NotificationMessageData? notification = conn.UpdateOrderTPSL(orderRequest);
        if (notification == null)
        {
            return CommandResult.Ok(
                $"[{conn.Name}] update-tpsl {symbol} {side} (TP={tpPercent}%, SL={slPercent}%): sent (response timed out).");
        }

        string summary =
            $"TP={(tp.isOn ? tpPercent + "%" : "off")} " +
            $"SL={(sl.isOn ? slPercent + "%" : "off")}" +
            (sl.tralingIsOn ? $" trail={trailSpread}%" : "") +
            (hasPositionSideOverride ? $" positionSide={positionSide}" : "");

        return notification.IsOk
            ? CommandResult.Ok(
                $"[{conn.Name}] update-tpsl {symbol} {side} ✓ {summary} — {notification.notificationCode}",
                new
                {
                    Server = conn.Name,
                    Symbol = symbol,
                    Side = side.ToString(),
                    Market = marketType.ToString(),
                    PositionSide = positionSide.ToString(),
                    TakeProfitPercent = tp.isOn ? tpPercent : 0m,
                    StopLossPercent = sl.isOn ? slPercent : 0m,
                    TrailingSpread = sl.tralingIsOn ? trailSpread : 0m,
                    NotificationCode = notification.notificationCode.ToString(),
                    Action = "UPDATE_TPSL",
                })
            : CommandResult.Fail(
                $"[{conn.Name}] update-tpsl failed: {notification.notificationCode} — {notification.msgString}");
    }
}
