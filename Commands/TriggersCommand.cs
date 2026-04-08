namespace MTTextClient.Commands;

using System;
using System.Collections.Generic;
using System.Text;
using MTTextClient.Core;
using Newtonsoft.Json;

public sealed class TriggersCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "triggers";
    public string Description => "Manage conditional triggers (subscribe, CRUD operations)";
    public string Usage => "triggers <list|subscribe|unsubscribe|save|delete|start|stop|start-all|stop-all> [json_data] [@profile]";

    public TriggersCommand(ConnectionManager manager)
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
            "list" => ListTriggers(cleanArgs, targetProfile),
            "subscribe" => Subscribe(targetProfile),
            "unsubscribe" => Unsubscribe(targetProfile),
            "save" => TriggerAction("SAVE_ACTION", cleanArgs, targetProfile),
            "delete" => TriggerAction("DELETE_ACTION", cleanArgs, targetProfile),
            "start" => TriggerAction("START_ACTION", cleanArgs, targetProfile),
            "stop" => TriggerAction("STOP_ACTION", cleanArgs, targetProfile),
            "start-all" => TriggerAction("START_ALL_ACTIONS", cleanArgs, targetProfile),
            "stop-all" => TriggerAction("STOP_ALL_ACTIONS", cleanArgs, targetProfile),
            _ => CommandResult.Fail($"Unknown subcommand: {sub}")
        };
    }

    private CommandResult ListTriggers(List<string> args, string? targetProfile)
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

        var entries = conn.TriggerStore.GetRecent(count);
        if (entries.Count == 0)
        {
            return CommandResult.Ok("No trigger events received. Subscribe first with 'triggers subscribe'.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Trigger events ({entries.Count} of {conn.TriggerStore.Count} total):");
        sb.AppendLine("─────────────────────────────────────────────");
        foreach (var entry in entries)
        {
            sb.AppendLine($"  [{entry.ReceivedAtUtc:HH:mm:ss}] {entry.EventType}: {entry.RawJson}");
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

        conn.SubscribeTriggers();
        return CommandResult.Ok("Subscribed to triggers");
    }

    private CommandResult Unsubscribe(string? targetProfile)
    {
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        conn.UnsubscribeTriggers();
        return CommandResult.Ok("Unsubscribed from triggers");
    }

    private CommandResult TriggerAction(string actionType, List<string> args, string? targetProfile)
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

        string result = conn.SendTriggerRequest(actionType, dataJson);
        return CommandResult.Ok(result);
    }
}
