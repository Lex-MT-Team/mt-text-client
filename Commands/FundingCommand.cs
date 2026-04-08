using System;
using MTTextClient.Core;

namespace MTTextClient.Commands;

/// <summary>
/// Funding balance commands — request funding account balances from Core.
/// 
/// funding request    — fire-and-forget request for funding balances
/// </summary>
public sealed class FundingCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public FundingCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public string Name => "funding";
    public string Description => "Request funding account balances";
    public string Usage => "funding request [@profile]";

    public CommandResult Execute(string[] args)
    {
        string? targetProfile = null;
        var cleanArgs = new System.Collections.Generic.List<string>();
        foreach (string arg in args)
        {
            if (arg.StartsWith('@'))
            {
                targetProfile = arg[1..];
            }
            else
            {
                cleanArgs.Add(arg);
            }
        }

        if (cleanArgs.Count == 0)
        {
            return CommandResult.Fail($"Usage: {Usage}");
        }

        string subCmd = cleanArgs[0].ToLowerInvariant();

        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            return CommandResult.Fail("Not connected. Use: connect <profile>");
        }

        return subCmd switch
        {
            "request" => HandleRequest(conn),
            _ => CommandResult.Fail($"Unknown subcommand: {subCmd}. Use: request")
        };
    }

    private CommandResult HandleRequest(CoreConnection conn)
    {
        conn.RequestFundingBalances();
        return CommandResult.Ok($"[{conn.Name}] Funding balances request sent (fire-and-forget).");
    }
}
