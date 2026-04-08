using System;
using MTTextClient.Core;

namespace MTTextClient.Commands;

/// <summary>
/// Buy API limit commands — check and request API rate limits from Core.
/// 
/// buylimit request <amount>   — request buy API limit check
/// </summary>
public sealed class BuyApiLimitCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public BuyApiLimitCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public string Name => "buylimit";
    public string Description => "Check buy API rate limits";
    public string Usage => "buylimit request <amount> [@profile]";

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

        if (cleanArgs.Count < 2)
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
            "request" => HandleRequest(cleanArgs.ToArray(), conn),
            _ => CommandResult.Fail($"Unknown subcommand: {subCmd}. Use: request")
        };
    }

    private CommandResult HandleRequest(string[] args, CoreConnection conn)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int amount))
        {
            return CommandResult.Fail("Usage: buylimit request <amount>");
        }

        string result = conn.RequestBuyApiLimit(amount);
        return CommandResult.Ok($"[{conn.Name}] BuyApiLimit result:\n{result}");
    }
}
