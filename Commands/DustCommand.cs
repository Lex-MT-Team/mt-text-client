namespace MTTextClient.Commands;

using System;
using System.Collections.Generic;
using System.Text;
using MTTextClient.Core;

public sealed class DustCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "dust";
    public string Description => "Convert small balances (dust) to main asset";
    public string Usage => "dust <get|convert> [@profile]";

    public DustCommand(ConnectionManager manager)
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
            "get" => GetDust(targetProfile),
            "convert" => ConvertDust(targetProfile),
            _ => CommandResult.Fail($"Unknown subcommand: {sub}. Use: get, convert")
        };
    }

    private CommandResult GetDust(string? targetProfile)
    {
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        string result = conn.GetDust();
        return CommandResult.Ok(result);
    }

    private CommandResult ConvertDust(string? targetProfile)
    {
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        string result = conn.ConvertDust();
        return CommandResult.Ok(result);
    }
}
