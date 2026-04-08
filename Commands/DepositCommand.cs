namespace MTTextClient.Commands;

using System;
using System.Collections.Generic;
using System.Text;
using MTTextClient.Core;

public sealed class DepositCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "deposit";
    public string Description => "Query deposit information and addresses";
    public string Usage => "deposit <info|address> <coin> [network] [@profile]";

    public DepositCommand(ConnectionManager manager)
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
            "info" => GetInfo(cleanArgs, targetProfile),
            "address" => GetAddress(cleanArgs, targetProfile),
            _ => CommandResult.Fail($"Unknown subcommand: {sub}. Use: info, address")
        };
    }

    private CommandResult GetInfo(List<string> args, string? targetProfile)
    {
        if (args.Count < 2)
        {
            return CommandResult.Fail("Usage: deposit info <coin> [@profile]");
        }

        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        string coin = args[1].ToUpperInvariant();
        string result = conn.GetDepositInfo(coin);
        return CommandResult.Ok(result);
    }

    private CommandResult GetAddress(List<string> args, string? targetProfile)
    {
        if (args.Count < 3)
        {
            return CommandResult.Fail("Usage: deposit address <coin> <network> [@profile]");
        }

        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("No connection. Use 'connect' first.");
        }

        string coin = args[1].ToUpperInvariant();
        string network = args[2];
        string result = conn.GetDepositAddress(coin, network);
        return CommandResult.Ok(result);
    }
}
