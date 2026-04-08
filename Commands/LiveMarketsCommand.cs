namespace MTTextClient.Commands;

using System;
using System.Collections.Generic;
using System.Text;
using MTTextClient.Core;

public sealed class LiveMarketsCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "livemarkets";
    public string Description => "Live market metrics streaming";
    public string Usage => "livemarkets <list|subscribe|unsubscribe> [symbol] [market] [quote_asset] [@profile]";

    public LiveMarketsCommand(ConnectionManager manager)
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

        if (cleanArgs.Count < 1)
        {
            return CommandResult.Fail("Usage: " + Usage);
        }

        string sub = cleanArgs[0].ToLowerInvariant();
        return sub switch
        {
            "list" => ListMarkets(targetProfile),
            "subscribe" => Subscribe(cleanArgs, targetProfile),
            "unsubscribe" => Unsubscribe(cleanArgs, targetProfile),
            _ => CommandResult.Fail($"Unknown subcommand: {sub}. Use: list, subscribe, unsubscribe")
        };
    }

    private CommandResult ListMarkets(string? targetProfile)
    {
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        var entries = conn.LiveMarketStore.GetAll();
        if (entries.Count == 0)
        {
            return CommandResult.Ok("No live market data. Subscribe first with 'livemarkets subscribe'.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Live Markets ({entries.Count} symbols):");
        sb.AppendLine("─────────────────────────────────────────────");
        foreach (var entry in entries)
        {
            sb.AppendLine($"  {entry.Symbol} ({entry.MarketType}) updated={entry.UpdatedAtUtc:HH:mm:ss}: {entry.MetricsJson}");
        }
        return CommandResult.Ok(sb.ToString());
    }

    private CommandResult Subscribe(List<string> args, string? targetProfile)
    {
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        var marketType = MTShared.Types.MarketType.FUTURES;
        string symbol = "";
        string quoteAsset = "";

        if (args.Count > 1)
        {
            symbol = args[1];
        }
        if (args.Count > 2)
        {
            marketType = ParseMarketType(args[2]);
        }
        if (args.Count > 3)
        {
            quoteAsset = args[3];
        }

        conn.SubscribeLiveMarkets(marketType, symbol, quoteAsset);
        return CommandResult.Ok($"Subscribed to live markets (market={marketType}, symbol={symbol}, quoteAsset={quoteAsset})");
    }

    private CommandResult Unsubscribe(List<string> args, string? targetProfile)
    {
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        var marketType = MTShared.Types.MarketType.FUTURES;
        string symbol = "";
        string quoteAsset = "";

        if (args.Count > 1)
        {
            symbol = args[1];
        }
        if (args.Count > 2)
        {
            marketType = ParseMarketType(args[2]);
        }
        if (args.Count > 3)
        {
            quoteAsset = args[3];
        }

        conn.UnsubscribeLiveMarkets(marketType, symbol, quoteAsset);
        return CommandResult.Ok("Unsubscribed from live markets");
    }

    private static MTShared.Types.MarketType ParseMarketType(string value)
    {
        return value.ToUpperInvariant() switch
        {
            "SPOT" => MTShared.Types.MarketType.SPOT,
            "MARGIN" => MTShared.Types.MarketType.MARGIN,
            "FUTURES" => MTShared.Types.MarketType.FUTURES,
            "DELIVERY" => MTShared.Types.MarketType.DELIVERY,
            _ => MTShared.Types.MarketType.FUTURES
        };
    }
}
