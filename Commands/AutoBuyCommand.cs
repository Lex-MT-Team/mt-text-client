namespace MTTextClient.Commands;

using System;
using System.Collections.Generic;
using System.Text;
using MTTextClient.Core;
using Newtonsoft.Json;

public sealed class AutoBuyCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "autobuy";
    public string Description => "AutoBuy (DCA/recurring buy) management";
    public string Usage => "autobuy <list|subscribe|unsubscribe|save|delete|start|stop|refresh-pairs> [json_data] [@profile]";

    public AutoBuyCommand(ConnectionManager manager)
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
            "list" => ListEntries(cleanArgs, targetProfile),
            "subscribe" => Subscribe(targetProfile),
            "unsubscribe" => Unsubscribe(targetProfile),
            "save" => AutoBuyAction("SAVE", cleanArgs, targetProfile),
            "delete" => AutoBuyAction("DELETE", cleanArgs, targetProfile),
            "start" => AutoBuyAction("START", cleanArgs, targetProfile),
            "stop" => AutoBuyAction("STOP", cleanArgs, targetProfile),
            "refresh-pairs" => AutoBuyAction("REFRESH_ASSET_PAIRS", cleanArgs, targetProfile),
            _ => CommandResult.Fail($"Unknown subcommand: {sub}")
        };
    }

    private CommandResult ListEntries(List<string> args, string? targetProfile)
    {
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        int count = 20;
        if (args.Count > 1 && args[1].StartsWith("--count="))
        {
            int.TryParse(args[1][8..], out count);
        }

        var entries = conn.AutoBuyStore.GetRecent(count);
        if (entries.Count == 0)
        {
            return CommandResult.Ok("No AutoBuy events. Subscribe first with 'autobuy subscribe'.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"AutoBuy events ({entries.Count} of {conn.AutoBuyStore.Count} total):");
        sb.AppendLine("─────────────────────────────────────────────");
        foreach (var entry in entries)
        {
            sb.AppendLine($"  [{entry.ReceivedAtUtc:HH:mm:ss}] {entry.ActionType}: {entry.RawJson}");
        }
        return CommandResult.Ok(sb.ToString());
    }

    private CommandResult Subscribe(string? targetProfile)
    {
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        conn.SubscribeAutoBuy();
        return CommandResult.Ok("Subscribed to AutoBuy events");
    }

    private CommandResult Unsubscribe(string? targetProfile)
    {
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        conn.UnsubscribeAutoBuy();
        return CommandResult.Ok("Unsubscribed from AutoBuy");
    }

    private CommandResult AutoBuyAction(string actionType, List<string> args, string? targetProfile)
    {
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        string dataJson = "";
        if (args.Count > 1)
        {
            dataJson = string.Join(" ", args.GetRange(1, args.Count - 1));
        }

        string result = conn.SendAutoBuyRequest(actionType, dataJson);
        return CommandResult.Ok(result);
    }
}
