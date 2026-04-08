using System;
using System.Collections.Generic;
using System.Text;
using MTShared.Network;
using MTShared.Types;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// Trading performance commands — view trading performance data from Core.
///
/// Subcommands:
///   perf list                   — list current trading performance data
///   perf subscribe              — subscribe to trading performance updates
///   perf unsubscribe            — unsubscribe from trading performance updates
///   perf request [refresh|reset] — request performance data or reset
///
/// Supports @profile targeting.
/// </summary>
public sealed class PerformanceCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "perf";
    public string Description => "View trading performance data and metrics";
    public string Usage => "perf <list|subscribe|unsubscribe|request [refresh|reset]> [@profile]";

    public PerformanceCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(
                "Usage: perf <subcommand>\n" +
                "  list         — list current performance data\n" +
                "  subscribe    — subscribe to performance updates\n" +
                "  unsubscribe  — unsubscribe from performance\n" +
                "  request      — request performance data refresh");
        }

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

        if (cleanArgs.Count == 0)
        {
            return CommandResult.Fail("Missing subcommand.");
        }

        string subcommand = cleanArgs[0].ToLowerInvariant();

        return subcommand switch
        {
            "list" => HandleList(targetProfile),
            "subscribe" => HandleSubscribe(cleanArgs, targetProfile),
            "unsubscribe" => HandleUnsubscribe(targetProfile),
            "request" => HandleRequest(cleanArgs, targetProfile),
            _ => CommandResult.Fail($"Unknown subcommand: {subcommand}. Use: list, subscribe, unsubscribe, request")
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

    private CommandResult HandleList(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        TradingPerformanceStore? store = conn.TradingPerfStore;
        if (store == null || !store.HasData)
        {
            return CommandResult.Ok($"[{conn.Name}] No performance data. Use 'perf subscribe' first.");
        }

        IReadOnlyList<TradingPerformanceSnapshot> entries = store.GetAll();
        var sb = new StringBuilder();
        sb.AppendLine($"[{conn.Name}] Trading Performance ({entries.Count} entries):");
        sb.AppendLine();

        for (int i = 0; i < entries.Count; i++)
        {
            TradingPerformanceSnapshot entry = entries[i];
            sb.AppendLine($"  [{i}] {entry.KeyGroup}: {entry.Symbol} (Market: {entry.MarketType})");
            sb.AppendLine($"      Algo ID: {entry.AlgorithmId} | Comment: {entry.Comment}");
            sb.AppendLine($"      Start: {entry.StartTime}");
            sb.AppendLine($"      Totals: {entry.TotalsCount} | PriceDeltas: {entry.PriceDeltasCount}");
            sb.AppendLine($"      ProfitFactors: {entry.ProfitFactorsCount} | ProfitTotals: {entry.ProfitTotalsCount} | LossTotals: {entry.LossTotalsCount}");
            sb.AppendLine();
        }

        return CommandResult.Ok(sb.ToString());
    }

    private CommandResult HandleSubscribe(List<string> cleanArgs, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        // Optional market type arg
        MarketType marketType = MarketType.FUTURES;
        if (cleanArgs.Count > 1)
        {
            string marketStr = cleanArgs[1].ToUpperInvariant();
            if (marketStr == "SPOT")
            {
                marketType = MarketType.SPOT;
            }
            else if (marketStr == "DELIVERY")
            {
                marketType = MarketType.DELIVERY;
            }
        }

        bool subscribed = conn.SubscribeTradingPerformance(marketType);
        if (!subscribed)
        {
            return CommandResult.Fail($"[{conn.Name}] Failed to subscribe to trading performance.");
        }

        return CommandResult.Ok($"[{conn.Name}] Subscribed to trading performance ({marketType}). Use 'perf list' to view data.");
    }

    private CommandResult HandleUnsubscribe(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        conn.UnsubscribeTradingPerformance();
        return CommandResult.Ok($"[{conn.Name}] Unsubscribed from trading performance.");
    }

    private CommandResult HandleRequest(List<string> cleanArgs, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        TradingPerformanceRequestData.ActionType actionType = TradingPerformanceRequestData.ActionType.REFRESH;
        if (cleanArgs.Count > 1 && cleanArgs[1].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            actionType = TradingPerformanceRequestData.ActionType.RESET;
        }

        conn.SendTradingPerformanceRequest(actionType);
        return CommandResult.Ok($"[{conn.Name}] Trading performance {actionType} requested.");
    }
}
