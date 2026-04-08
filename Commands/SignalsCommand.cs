namespace MTTextClient.Commands;

using System;
using System.Collections.Generic;
using MTTextClient.Core;

public sealed class SignalsCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "signals";
    public string Description => "Send external trading signals to MTCore";
    public string Usage => "signals send <symbol> <side> <price> [--market=FUTURES] [--tp=5.0] [--sl=3.0] [@profile]";

    public SignalsCommand(ConnectionManager manager)
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
            "send" => SendSignal(cleanArgs, targetProfile),
            _ => CommandResult.Fail($"Unknown subcommand: {sub}. Use: send")
        };
    }

    private CommandResult SendSignal(List<string> args, string? targetProfile)
    {
        if (args.Count < 4)
        {
            return CommandResult.Fail("Usage: signals send <symbol> <BUY|SELL> <price> [--market=FUTURES] [--tp=5.0] [--sl=3.0] [--channel=default] [@profile]");
        }

        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        string symbol = args[1];
        string sideStr = args[2].ToUpperInvariant();
        if (!decimal.TryParse(args[3], out decimal price))
        {
            return CommandResult.Fail($"Invalid price: {args[3]}");
        }

        var side = sideStr == "BUY"
            ? MTShared.Types.OrderSideType.BUY
            : sideStr == "SELL"
                ? MTShared.Types.OrderSideType.SELL
                : MTShared.Types.OrderSideType.UNKNOWN;

        if (side == MTShared.Types.OrderSideType.UNKNOWN)
        {
            return CommandResult.Fail($"Invalid side: {sideStr}. Use BUY or SELL.");
        }

        // Parse optional flags
        var marketType = MTShared.Types.MarketType.FUTURES;
        float tpPct = 0f;
        float slPct = 0f;
        string channelId = "default";

        for (int i = 4; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--market="))
            {
                marketType = ParseMarketType(arg[9..]);
            }
            else if (arg.StartsWith("--tp="))
            {
                float.TryParse(arg[5..], out tpPct);
            }
            else if (arg.StartsWith("--sl="))
            {
                float.TryParse(arg[5..], out slPct);
            }
            else if (arg.StartsWith("--channel="))
            {
                channelId = arg[10..];
            }
        }

        conn.SendSignal(channelId, marketType, side, symbol, price, tpPct, slPct);
        return CommandResult.Ok($"Signal sent: {side} {symbol} @ {price} (tp={tpPct}%, sl={slPct}%)");
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
