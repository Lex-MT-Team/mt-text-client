namespace MTTextClient.Commands;

using System;
using System.Collections.Generic;
using System.Text;
using MTTextClient.Core;

public sealed class GraphToolCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "graphtool";
    public string Description => "Chart drawing/graph tool management";
    public string Usage => "graphtool <list|subscribe|unsubscribe|save|delete> [json_data] [@profile]";

    public GraphToolCommand(ConnectionManager manager)
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
            "save" => GraphToolAction("SAVE", cleanArgs, targetProfile),
            "delete" => GraphToolAction("DELETE", cleanArgs, targetProfile),
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

        var entries = conn.GraphToolStore.GetRecent(count);
        if (entries.Count == 0)
        {
            return CommandResult.Ok("No graph tool events. Subscribe first with 'graphtool subscribe'.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Graph Tool events ({entries.Count} of {conn.GraphToolStore.Count} total):");
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

        conn.SubscribeGraphTool();
        return CommandResult.Ok("Subscribed to graph tool events");
    }

    private CommandResult Unsubscribe(string? targetProfile)
    {
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        conn.UnsubscribeGraphTool();
        return CommandResult.Ok("Unsubscribed from graph tool");
    }

    private CommandResult GraphToolAction(string requestType, List<string> args, string? targetProfile)
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

        string result = conn.SendGraphToolRequest(requestType, dataJson);
        return CommandResult.Ok(result);
    }
}
