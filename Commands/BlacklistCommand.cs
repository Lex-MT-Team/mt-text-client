using System;
using System.Collections.Generic;
using System.Text;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// Blacklist commands — view and manage symbol/market/quote blacklist on Core.
/// Blacklist data is managed through profile settings.
///
/// Subcommands:
///   blacklist list                — show current blacklist (markets, quotes, symbols)
///   blacklist add-market <value>  — add a market type to blacklist
///   blacklist add-quote <value>   — add a quote asset to blacklist
///   blacklist add-symbol <value>  — add a symbol to blacklist
///   blacklist remove-market <v>   — remove a market type from blacklist
///   blacklist remove-quote <v>    — remove a quote asset from blacklist
///   blacklist remove-symbol <v>   — remove a symbol from blacklist
///
/// Supports @profile targeting.
/// </summary>
public sealed class BlacklistCommand : ICommand
{
    private readonly ConnectionManager _manager;

    private const string BLACKLIST_MARKETS_KEY = "BlackList.MarketTypes";
    private const string BLACKLIST_QUOTES_KEY = "BlackList.Quotes";
    private const string BLACKLIST_SYMBOLS_KEY = "BlackList.Symbols";
    private const string BLACKLIST_FIRST_INIT_KEY = "BlackList.FirstInitialization";
    private const string NEW_LISTED_ENABLED_KEY = "NewListedMarket.AddToBlacklistEnabled";
    private const string NEW_LISTED_TIME_KEY = "NewListedMarket.AddToBlacklistForTime";

    public string Name => "blacklist";
    public string Description => "View and manage symbol/market/quote blacklist (risk management)";
    public string Usage => "blacklist <list|add-market|add-quote|add-symbol|remove-market|remove-quote|remove-symbol> [value] [@profile]";

    public BlacklistCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(
                "Usage: blacklist <subcommand>\n" +
                "  list            — show current blacklist\n" +
                "  add-market <v>  — add market type to blacklist\n" +
                "  add-quote <v>   — add quote asset to blacklist\n" +
                "  add-symbol <v>  — add symbol to blacklist\n" +
                "  remove-market   — remove market from blacklist\n" +
                "  remove-quote    — remove quote from blacklist\n" +
                "  remove-symbol   — remove symbol from blacklist");
        }

        string? targetProfile = null;
        bool confirmFlag = false;
        var cleanArgs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('@'))
            {
                targetProfile = args[i][1..];
            }
            else if (args[i].Equals("--confirm", StringComparison.OrdinalIgnoreCase) ||
                     args[i].Equals("-y", StringComparison.OrdinalIgnoreCase))
            {
                confirmFlag = true;
            }
            else
            {
                cleanArgs.Add(args[i]);
            }
        }

        if (cleanArgs.Count == 0)
        {
            return CommandResult.Fail("Missing subcommand.");
        }

        string subcommand = cleanArgs[0].ToLowerInvariant();

        return subcommand switch
        {
            "list" => HandleList(targetProfile),
            "add-market" => HandleAdd(BLACKLIST_MARKETS_KEY, cleanArgs, targetProfile, confirmFlag),
            "add-quote" => HandleAdd(BLACKLIST_QUOTES_KEY, cleanArgs, targetProfile, confirmFlag),
            "add-symbol" => HandleAdd(BLACKLIST_SYMBOLS_KEY, cleanArgs, targetProfile, confirmFlag),
            "remove-market" => HandleRemove(BLACKLIST_MARKETS_KEY, cleanArgs, targetProfile, confirmFlag),
            "remove-quote" => HandleRemove(BLACKLIST_QUOTES_KEY, cleanArgs, targetProfile, confirmFlag),
            "remove-symbol" => HandleRemove(BLACKLIST_SYMBOLS_KEY, cleanArgs, targetProfile, confirmFlag),
            _ => CommandResult.Fail($"Unknown subcommand: {subcommand}. Use: list, add-market, add-quote, add-symbol, remove-market, remove-quote, remove-symbol")
        };
    }

    private CoreConnection? ResolveConnection(string? targetProfile, out CommandResult? error)
    {
        error = null;
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            error = targetProfile != null
                ? CommandResult.Fail($"No connection '{targetProfile}'. Use 'status' to see connections.")
                : CommandResult.Fail("Not connected. Use 'connect <profile>' first.");
            return null;
        }
        if (!conn.IsConnected)
        {
            error = CommandResult.Fail($"[{conn.Name}] Not connected.");
            return null;
        }
        return conn;
    }

    private CommandResult HandleList(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        // Ensure settings are loaded
        if (!conn.ProfileSettingsStore.HasData)
        {
            var (success, reqError) = conn.RequestProfileSettings();
            if (!success)
            {
                return CommandResult.Fail($"[{conn.Name}] Failed to load settings: {reqError}");
            }
        }

        string? markets = conn.ProfileSettingsStore.GetValue(BLACKLIST_MARKETS_KEY);
        string? quotes = conn.ProfileSettingsStore.GetValue(BLACKLIST_QUOTES_KEY);
        string? symbols = conn.ProfileSettingsStore.GetValue(BLACKLIST_SYMBOLS_KEY);
        string? firstInit = conn.ProfileSettingsStore.GetValue(BLACKLIST_FIRST_INIT_KEY);
        string? newListedEnabled = conn.ProfileSettingsStore.GetValue(NEW_LISTED_ENABLED_KEY);
        string? newListedTime = conn.ProfileSettingsStore.GetValue(NEW_LISTED_TIME_KEY);

        var sb = new StringBuilder();
        sb.AppendLine($"[{conn.Name}] Blacklist Configuration:");
        sb.AppendLine();
        sb.AppendLine($"  Market Types:   {(string.IsNullOrEmpty(markets) ? "(none)" : markets)}");
        sb.AppendLine($"  Quote Assets:   {(string.IsNullOrEmpty(quotes) ? "(none)" : quotes)}");
        sb.AppendLine($"  Symbols:        {(string.IsNullOrEmpty(symbols) ? "(none)" : symbols)}");
        sb.AppendLine();
        sb.AppendLine($"  First Init:            {firstInit ?? "N/A"}");
        sb.AppendLine($"  New Listed → Blacklist: {newListedEnabled ?? "N/A"}");
        sb.AppendLine($"  New Listed Duration:    {newListedTime ?? "N/A"}");

        // Count items
        int marketCount = CountItems(markets);
        int quoteCount = CountItems(quotes);
        int symbolCount = CountItems(symbols);
        sb.AppendLine();
        sb.AppendLine($"  Total: {marketCount} markets, {quoteCount} quotes, {symbolCount} symbols");

        return CommandResult.Ok(sb.ToString());
    }

    private CommandResult HandleAdd(string settingsKey, List<string> cleanArgs, string? targetProfile, bool confirm)
    {
        if (cleanArgs.Count < 2)
        {
            return CommandResult.Fail($"Usage: blacklist {cleanArgs[0]} <value> --confirm [@profile]");
        }
        if (!confirm)
        {
            return CommandResult.Fail($"Add '{cleanArgs[1]}' to blacklist? Use --confirm to proceed.");
        }

        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        // Load current settings
        if (!conn.ProfileSettingsStore.HasData)
        {
            var (success, reqError) = conn.RequestProfileSettings();
            if (!success)
            {
                return CommandResult.Fail($"[{conn.Name}] Failed to load settings: {reqError}");
            }
        }

        string currentValue = conn.ProfileSettingsStore.GetValue(settingsKey) ?? "";
        string newItem = cleanArgs[1].Trim();

        // Check if already present
        string[] existing = currentValue.Split(',', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < existing.Length; i++)
        {
            if (existing[i].Trim().Equals(newItem, StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Fail($"[{conn.Name}] '{newItem}' already in blacklist ({settingsKey}).");
            }
        }

        // Append
        string newValue = string.IsNullOrEmpty(currentValue) ? newItem : $"{currentValue},{newItem}";
        var updated = new Dictionary<string, string> { { settingsKey, newValue } };

        var (updateSuccess, coreRestartNeeded, updateError) = conn.UpdateProfileSettings(updated);
        if (!updateSuccess)
        {
            return CommandResult.Fail($"[{conn.Name}] Failed to update blacklist: {updateError}");
        }

        string msg = $"[{conn.Name}] Added '{newItem}' to {settingsKey}.";
        if (coreRestartNeeded)
        {
            msg += " (Core restart needed for full effect)";
        }
        return CommandResult.Ok(msg);
    }

    private CommandResult HandleRemove(string settingsKey, List<string> cleanArgs, string? targetProfile, bool confirm)
    {
        if (cleanArgs.Count < 2)
        {
            return CommandResult.Fail($"Usage: blacklist {cleanArgs[0]} <value> --confirm [@profile]");
        }
        if (!confirm)
        {
            return CommandResult.Fail($"Remove '{cleanArgs[1]}' from blacklist? Use --confirm to proceed.");
        }

        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!conn.ProfileSettingsStore.HasData)
        {
            var (success, reqError) = conn.RequestProfileSettings();
            if (!success)
            {
                return CommandResult.Fail($"[{conn.Name}] Failed to load settings: {reqError}");
            }
        }

        string currentValue = conn.ProfileSettingsStore.GetValue(settingsKey) ?? "";
        string removeItem = cleanArgs[1].Trim();

        // Filter out the item
        string[] existing = currentValue.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var remaining = new List<string>();
        bool found = false;
        for (int i = 0; i < existing.Length; i++)
        {
            if (existing[i].Trim().Equals(removeItem, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
            }
            else
            {
                remaining.Add(existing[i].Trim());
            }
        }

        if (!found)
        {
            return CommandResult.Fail($"[{conn.Name}] '{removeItem}' not found in {settingsKey}.");
        }

        string newValue = string.Join(",", remaining);
        var updated = new Dictionary<string, string> { { settingsKey, newValue } };

        var (updateSuccess, coreRestartNeeded, updateError) = conn.UpdateProfileSettings(updated);
        if (!updateSuccess)
        {
            return CommandResult.Fail($"[{conn.Name}] Failed to update blacklist: {updateError}");
        }

        string msg = $"[{conn.Name}] Removed '{removeItem}' from {settingsKey}.";
        if (coreRestartNeeded)
        {
            msg += " (Core restart needed for full effect)";
        }
        return CommandResult.Ok(msg);
    }

    private static int CountItems(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
