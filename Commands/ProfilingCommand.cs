namespace MTTextClient.Commands;

using System;
using System.Collections.Generic;
using System.Text;
using MTTextClient.Core;

public sealed class ProfilingCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "profiling";
    public string Description => "Algorithm profiling data streaming";
    public string Usage => "profiling <subscribe|unsubscribe|view> <symbol> <algo_id> [market] [@profile]";

    public ProfilingCommand(ConnectionManager manager)
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
            "subscribe" => Subscribe(cleanArgs, targetProfile),
            "unsubscribe" => Unsubscribe(cleanArgs, targetProfile),
            _ => CommandResult.Fail($"Unknown subcommand: {sub}. Use: subscribe, unsubscribe")
        };
    }

    private CommandResult Subscribe(List<string> args, string? targetProfile)
    {
        if (args.Count < 3)
        {
            return CommandResult.Fail("Usage: profiling subscribe <symbol> <algo_id> [market] [@profile]");
        }

        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        string symbol = args[1];
        if (!long.TryParse(args[2], out long algorithmId))
        {
            return CommandResult.Fail($"Invalid algorithm ID: {args[2]}");
        }

        var marketType = MTShared.Types.MarketType.FUTURES;
        if (args.Count > 3)
        {
            marketType = ParseMarketType(args[3]);
        }

        conn.SubscribeProfiling(marketType, symbol, algorithmId);
        return CommandResult.Ok($"Subscribed to profiling for {symbol} algo {algorithmId} ({marketType})");
    }

    private CommandResult Unsubscribe(List<string> args, string? targetProfile)
    {
        if (args.Count < 3)
        {
            return CommandResult.Fail("Usage: profiling unsubscribe <symbol> <algo_id> [market] [@profile]");
        }

        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        string symbol = args[1];
        if (!long.TryParse(args[2], out long algorithmId))
        {
            return CommandResult.Fail($"Invalid algorithm ID: {args[2]}");
        }

        var marketType = MTShared.Types.MarketType.FUTURES;
        if (args.Count > 3)
        {
            marketType = ParseMarketType(args[3]);
        }

        conn.UnsubscribeProfiling(marketType, symbol, algorithmId);
        return CommandResult.Ok($"Unsubscribed from profiling for {symbol} algo {algorithmId}");
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
