using System;
using System.Collections.Generic;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// Manages server profiles: list, add, remove.
/// </summary>
public sealed class ProfileCommand : ICommand
{
    public string Name => "profile";
    public string Description => "Manage server profiles";
    public string Usage => "profile list | profile add <name> <ip> <port> <token> [exchange] | profile remove <name>";

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(Usage);
        }

        return args[0].ToLowerInvariant() switch
        {
            "list" or "ls" => ListProfiles(),
            "add" => AddProfile(args[1..]),
            "remove" or "rm" => RemoveProfile(args[1..]),
            _ => CommandResult.Fail($"Unknown subcommand: {args[0]}. {Usage}")
        };
    }

    private static CommandResult ListProfiles()
    {
        List<ServerProfile>? profiles = ProfileManager.LoadProfiles();
        if (profiles.Count == 0)
        {
            return CommandResult.Ok("No profiles configured. Use 'profile add' to create one.");
        }

        var data = new List<object>();
        for (int i = 0; i < profiles.Count; i++)
        {
            ServerProfile? p = profiles[i];
            data.Add(new
            {
                p.Name,
                p.Address,
                p.Port,
                Exchange = p.Exchange.ToString(),
                Token = p.ClientToken.Length > 8
                    ? p.ClientToken[..8] + "..."
                    : p.ClientToken
            });
        }

        return CommandResult.Ok($"{profiles.Count} profile(s) found.", data);
    }

    private static CommandResult AddProfile(string[] args)
    {
        if (args.Length < 4)
        {
            return CommandResult.Fail("Usage: profile add <name> <ip> <port> <token> [exchange]");
        }

        string? name = args[0];
        string? ip = args[1];
        if (!int.TryParse(args[2], out int port))
        {
            return CommandResult.Fail($"Invalid port: {args[2]}");
        }

        string? token = args[3];

        string? exchangeStr = args.Length > 4 ? args[4] : "BINANCE";
        if (!Enum.TryParse<MTShared.Types.ExchangeType>(exchangeStr, true, out MTShared.Types.ExchangeType exchange))
        {
            return CommandResult.Fail($"Invalid exchange: {exchangeStr}. Valid: BINANCE, BYBIT, OKX, etc.");
        }

        List<ServerProfile>? profiles = ProfileManager.LoadProfiles();

        if (ProfileManager.FindProfile(profiles, name) != null)
        {
            return CommandResult.Fail($"Profile '{name}' already exists. Remove it first.");
        }

        var profile = new ServerProfile
        {
            Name = name,
            Address = ip,
            Port = port,
            ClientToken = token,
            Exchange = exchange
        };

        profiles.Add(profile);
        ProfileManager.SaveProfiles(profiles);

        return CommandResult.Ok($"Profile '{name}' added: {profile}");
    }

    private static CommandResult RemoveProfile(string[] args)
    {
        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: profile remove <name>");
        }

        string? name = args[0];
        List<ServerProfile>? profiles = ProfileManager.LoadProfiles();
        ServerProfile? profile = ProfileManager.FindProfile(profiles, name);

        if (profile == null)
        {
            return CommandResult.Fail($"Profile '{name}' not found.");
        }

        profiles.Remove(profile);
        ProfileManager.SaveProfiles(profiles);

        return CommandResult.Ok($"Profile '{name}' removed.");
    }
}
