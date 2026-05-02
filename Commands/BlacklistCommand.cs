using System;
using System.Collections.Generic;
using System.Text;
using MTShared.Types;
using MTTextClient.Core;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Commands;

/// <summary>
/// Blacklist commands — view and manage symbol/market/quote blacklist.
///
/// Storage shape (matches MTCore profile-settings format):
///   BlackList.MarketTypes : JSON array of { MarketType:int, TimeFilter:{} }
///   BlackList.Quotes      : JSON array of { MarketType:int, QuoteAsset:string, TimeFilter:{} }
///   BlackList.Symbols     : JSON array of { MarketType:int, QuoteAsset:string, Symbol:string, TimeFilter:{} }
///
/// MarketType ints: 0=UNKNOWN, 1=SPOT, 2=MARGIN, 3=FUTURES, 4=DELIVERY.
///
/// Subcommands:
///   blacklist list
///   blacklist add-market    &lt;market&gt;                            --confirm
///   blacklist add-quote     &lt;market&gt; &lt;quote&gt;                    --confirm
///   blacklist add-symbol    &lt;market&gt; &lt;quote&gt; &lt;symbol&gt;           --confirm
///   blacklist remove-market &lt;market&gt;                            --confirm
///   blacklist remove-quote  &lt;market&gt; &lt;quote&gt;                    --confirm
///   blacklist remove-symbol &lt;market&gt; &lt;quote&gt; &lt;symbol&gt;           --confirm
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
    public string Usage => "blacklist <list|add-market|add-quote|add-symbol|remove-market|remove-quote|remove-symbol> [args] --confirm [@profile]";

    public BlacklistCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(UsageHelp());
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
            "list"          => HandleList(targetProfile),
            "add-market"    => HandleAddMarket(cleanArgs, targetProfile, confirmFlag),
            "add-quote"     => HandleAddQuote(cleanArgs, targetProfile, confirmFlag),
            "add-symbol"    => HandleAddSymbol(cleanArgs, targetProfile, confirmFlag),
            "remove-market" => HandleRemoveMarket(cleanArgs, targetProfile, confirmFlag),
            "remove-quote"  => HandleRemoveQuote(cleanArgs, targetProfile, confirmFlag),
            "remove-symbol" => HandleRemoveSymbol(cleanArgs, targetProfile, confirmFlag),
            _ => CommandResult.Fail($"Unknown subcommand: {subcommand}.\n{UsageHelp()}")
        };
    }

    private static string UsageHelp() =>
        "Usage:\n" +
        "  blacklist list\n" +
        "  blacklist add-market    <market>                  --confirm\n" +
        "  blacklist add-quote     <market> <quote>          --confirm\n" +
        "  blacklist add-symbol    <market> <quote> <symbol> --confirm\n" +
        "  blacklist remove-market <market>                  --confirm\n" +
        "  blacklist remove-quote  <market> <quote>          --confirm\n" +
        "  blacklist remove-symbol <market> <quote> <symbol> --confirm\n" +
        "  market = SPOT | MARGIN | FUTURES | DELIVERY";

    // ─── helpers ────────────────────────────────────────────────────────────

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

    private static (bool ok, MarketType mt, string? error) ParseMarket(string raw)
    {
        if (Enum.TryParse<MarketType>(raw, ignoreCase: true, out var mt) &&
            Enum.IsDefined(typeof(MarketType), mt) &&
            mt != MarketType.UNKNOWN)
        {
            return (true, mt, null);
        }
        return (false, default, $"Invalid market '{raw}'. Allowed: SPOT, MARGIN, FUTURES, DELIVERY.");
    }

    private static (bool ok, JArray arr, string? error) ParseStoredArray(string? raw, string key)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (true, new JArray(), null);
        }
        try
        {
            var token = JToken.Parse(raw);
            if (token is JArray arr)
            {
                return (true, arr, null);
            }
            return (false, new JArray(),
                $"Setting '{key}' has unexpected shape (expected JSON array, got {token.Type}). " +
                "This typically means the value was written by an older client using comma-separated text. " +
                "Edit the value via the GUI or 'settings set' to a valid JSON array before using blacklist commands.");
        }
        catch (Exception ex)
        {
            return (false, new JArray(),
                $"Setting '{key}' is not valid JSON ({ex.Message}). " +
                "Expected a JSON array of typed objects.");
        }
    }

    private static bool MarketEntryEquals(JToken entry, MarketType mt) =>
        entry is JObject o && (int?)o["MarketType"] == (int)mt;

    private static bool QuoteEntryEquals(JToken entry, MarketType mt, string quote) =>
        entry is JObject o &&
        (int?)o["MarketType"] == (int)mt &&
        string.Equals((string?)o["QuoteAsset"], quote, StringComparison.OrdinalIgnoreCase);

    private static bool SymbolEntryEquals(JToken entry, MarketType mt, string quote, string symbol) =>
        entry is JObject o &&
        (int?)o["MarketType"] == (int)mt &&
        string.Equals((string?)o["QuoteAsset"], quote, StringComparison.OrdinalIgnoreCase) &&
        string.Equals((string?)o["Symbol"], symbol, StringComparison.OrdinalIgnoreCase);

    private CommandResult LoadAndUpdate(
        CoreConnection conn,
        string key,
        Func<JArray, (bool ok, string? error)> mutate,
        string successMessage)
    {
        if (!conn.ProfileSettingsStore.HasData)
        {
            var (success, reqError) = conn.RequestProfileSettings();
            if (!success)
            {
                return CommandResult.Fail($"[{conn.Name}] Failed to load settings: {reqError}");
            }
        }

        string? raw = conn.ProfileSettingsStore.GetValue(key);
        var (parseOk, arr, parseErr) = ParseStoredArray(raw, key);
        if (!parseOk)
        {
            return CommandResult.Fail($"[{conn.Name}] {parseErr}");
        }

        var (mutOk, mutErr) = mutate(arr);
        if (!mutOk)
        {
            return CommandResult.Fail($"[{conn.Name}] {mutErr}");
        }

        string newValue = arr.ToString(Newtonsoft.Json.Formatting.None);
        var updated = new Dictionary<string, string> { { key, newValue } };

        var (updateSuccess, coreRestartNeeded, updateError) = conn.UpdateProfileSettings(updated);
        if (!updateSuccess)
        {
            return CommandResult.Fail($"[{conn.Name}] Failed to update blacklist: {updateError}");
        }

        string msg = $"[{conn.Name}] {successMessage}";
        if (coreRestartNeeded)
        {
            msg += " (Core restart needed for full effect)";
        }
        return CommandResult.Ok(msg);
    }

    // ─── list ───────────────────────────────────────────────────────────────

    private CommandResult HandleList(string? targetProfile)
    {
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

        string? marketsRaw  = conn.ProfileSettingsStore.GetValue(BLACKLIST_MARKETS_KEY);
        string? quotesRaw   = conn.ProfileSettingsStore.GetValue(BLACKLIST_QUOTES_KEY);
        string? symbolsRaw  = conn.ProfileSettingsStore.GetValue(BLACKLIST_SYMBOLS_KEY);
        string? firstInit   = conn.ProfileSettingsStore.GetValue(BLACKLIST_FIRST_INIT_KEY);
        string? newEnabled  = conn.ProfileSettingsStore.GetValue(NEW_LISTED_ENABLED_KEY);
        string? newTime     = conn.ProfileSettingsStore.GetValue(NEW_LISTED_TIME_KEY);

        var (mOk, markets, mErr) = ParseStoredArray(marketsRaw, BLACKLIST_MARKETS_KEY);
        var (qOk, quotes,  qErr) = ParseStoredArray(quotesRaw,  BLACKLIST_QUOTES_KEY);
        var (sOk, symbols, sErr) = ParseStoredArray(symbolsRaw, BLACKLIST_SYMBOLS_KEY);

        var sb = new StringBuilder();
        sb.AppendLine($"[{conn.Name}] Blacklist Configuration:");
        sb.AppendLine();

        sb.AppendLine($"  Market Types ({(mOk ? markets.Count.ToString() : "?")}):");
        if (!mOk) sb.AppendLine($"    ⚠ {mErr}");
        else if (markets.Count == 0) sb.AppendLine("    (none)");
        else foreach (var e in markets)
            sb.AppendLine($"    • {MarketName((int?)e["MarketType"])}");

        sb.AppendLine($"  Quote Assets ({(qOk ? quotes.Count.ToString() : "?")}):");
        if (!qOk) sb.AppendLine($"    ⚠ {qErr}");
        else if (quotes.Count == 0) sb.AppendLine("    (none)");
        else foreach (var e in quotes)
            sb.AppendLine($"    • {MarketName((int?)e["MarketType"])} / {(string?)e["QuoteAsset"] ?? "?"}");

        sb.AppendLine($"  Symbols ({(sOk ? symbols.Count.ToString() : "?")}):");
        if (!sOk) sb.AppendLine($"    ⚠ {sErr}");
        else if (symbols.Count == 0) sb.AppendLine("    (none)");
        else foreach (var e in symbols)
            sb.AppendLine($"    • {MarketName((int?)e["MarketType"])} / {(string?)e["QuoteAsset"] ?? "?"} / {(string?)e["Symbol"] ?? "?"}");

        sb.AppendLine();
        sb.AppendLine($"  First Init:             {firstInit ?? "N/A"}");
        sb.AppendLine($"  New Listed → Blacklist: {newEnabled ?? "N/A"}");
        sb.AppendLine($"  New Listed Duration:    {newTime ?? "N/A"}");

        return CommandResult.Ok(sb.ToString());
    }

    private static string MarketName(int? code) => code switch
    {
        0 => "UNKNOWN",
        1 => "SPOT",
        2 => "MARGIN",
        3 => "FUTURES",
        4 => "DELIVERY",
        null => "?",
        _ => $"UNKNOWN({code})"
    };

    // ─── market ─────────────────────────────────────────────────────────────

    private CommandResult HandleAddMarket(List<string> args, string? profile, bool confirm)
    {
        if (args.Count < 2) return CommandResult.Fail("Usage: blacklist add-market <market> --confirm");
        var (ok, mt, err) = ParseMarket(args[1]);
        if (!ok) return CommandResult.Fail(err!);
        if (!confirm) return CommandResult.Fail($"Add market '{mt}' to blacklist? Use --confirm to proceed.");

        CoreConnection? conn = ResolveConnection(profile, out var connErr);
        if (conn == null) return connErr!;

        return LoadAndUpdate(conn, BLACKLIST_MARKETS_KEY, arr =>
        {
            foreach (var e in arr)
                if (MarketEntryEquals(e, mt))
                    return (false, $"Market '{mt}' already in blacklist.");
            arr.Add(new JObject {
                ["MarketType"] = (int)mt,
                ["TimeFilter"] = new JObject()
            });
            return (true, null);
        }, $"Added market '{mt}' to blacklist.");
    }

    private CommandResult HandleRemoveMarket(List<string> args, string? profile, bool confirm)
    {
        if (args.Count < 2) return CommandResult.Fail("Usage: blacklist remove-market <market> --confirm");
        var (ok, mt, err) = ParseMarket(args[1]);
        if (!ok) return CommandResult.Fail(err!);
        if (!confirm) return CommandResult.Fail($"Remove market '{mt}' from blacklist? Use --confirm to proceed.");

        CoreConnection? conn = ResolveConnection(profile, out var connErr);
        if (conn == null) return connErr!;

        return LoadAndUpdate(conn, BLACKLIST_MARKETS_KEY, arr =>
        {
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (MarketEntryEquals(arr[i], mt))
                {
                    arr.RemoveAt(i);
                    return (true, null);
                }
            }
            return (false, $"Market '{mt}' not found in blacklist.");
        }, $"Removed market '{mt}' from blacklist.");
    }

    // ─── quote ──────────────────────────────────────────────────────────────

    private CommandResult HandleAddQuote(List<string> args, string? profile, bool confirm)
    {
        if (args.Count < 3) return CommandResult.Fail("Usage: blacklist add-quote <market> <quote> --confirm");
        var (ok, mt, err) = ParseMarket(args[1]);
        if (!ok) return CommandResult.Fail(err!);
        string quote = args[2].Trim().ToLowerInvariant();
        if (quote.Length == 0) return CommandResult.Fail("Quote asset is empty.");
        if (!confirm) return CommandResult.Fail($"Add quote '{mt}/{quote}' to blacklist? Use --confirm to proceed.");

        CoreConnection? conn = ResolveConnection(profile, out var connErr);
        if (conn == null) return connErr!;

        return LoadAndUpdate(conn, BLACKLIST_QUOTES_KEY, arr =>
        {
            foreach (var e in arr)
                if (QuoteEntryEquals(e, mt, quote))
                    return (false, $"Quote '{mt}/{quote}' already in blacklist.");
            arr.Add(new JObject {
                ["MarketType"] = (int)mt,
                ["QuoteAsset"] = quote,
                ["TimeFilter"] = new JObject()
            });
            return (true, null);
        }, $"Added quote '{mt}/{quote}' to blacklist.");
    }

    private CommandResult HandleRemoveQuote(List<string> args, string? profile, bool confirm)
    {
        if (args.Count < 3) return CommandResult.Fail("Usage: blacklist remove-quote <market> <quote> --confirm");
        var (ok, mt, err) = ParseMarket(args[1]);
        if (!ok) return CommandResult.Fail(err!);
        string quote = args[2].Trim().ToLowerInvariant();
        if (quote.Length == 0) return CommandResult.Fail("Quote asset is empty.");
        if (!confirm) return CommandResult.Fail($"Remove quote '{mt}/{quote}' from blacklist? Use --confirm to proceed.");

        CoreConnection? conn = ResolveConnection(profile, out var connErr);
        if (conn == null) return connErr!;

        return LoadAndUpdate(conn, BLACKLIST_QUOTES_KEY, arr =>
        {
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (QuoteEntryEquals(arr[i], mt, quote))
                {
                    arr.RemoveAt(i);
                    return (true, null);
                }
            }
            return (false, $"Quote '{mt}/{quote}' not found in blacklist.");
        }, $"Removed quote '{mt}/{quote}' from blacklist.");
    }

    // ─── symbol ─────────────────────────────────────────────────────────────

    private CommandResult HandleAddSymbol(List<string> args, string? profile, bool confirm)
    {
        if (args.Count < 4) return CommandResult.Fail("Usage: blacklist add-symbol <market> <quote> <symbol> --confirm");
        var (ok, mt, err) = ParseMarket(args[1]);
        if (!ok) return CommandResult.Fail(err!);
        string quote = args[2].Trim().ToLowerInvariant();
        string symbol = args[3].Trim().ToLowerInvariant();
        if (quote.Length == 0 || symbol.Length == 0) return CommandResult.Fail("Quote and symbol must be non-empty.");
        if (!confirm) return CommandResult.Fail($"Add symbol '{mt}/{quote}/{symbol}' to blacklist? Use --confirm to proceed.");

        CoreConnection? conn = ResolveConnection(profile, out var connErr);
        if (conn == null) return connErr!;

        return LoadAndUpdate(conn, BLACKLIST_SYMBOLS_KEY, arr =>
        {
            foreach (var e in arr)
                if (SymbolEntryEquals(e, mt, quote, symbol))
                    return (false, $"Symbol '{mt}/{quote}/{symbol}' already in blacklist.");
            arr.Add(new JObject {
                ["MarketType"] = (int)mt,
                ["QuoteAsset"] = quote,
                ["Symbol"]     = symbol,
                ["TimeFilter"] = new JObject()
            });
            return (true, null);
        }, $"Added symbol '{mt}/{quote}/{symbol}' to blacklist.");
    }

    private CommandResult HandleRemoveSymbol(List<string> args, string? profile, bool confirm)
    {
        if (args.Count < 4) return CommandResult.Fail("Usage: blacklist remove-symbol <market> <quote> <symbol> --confirm");
        var (ok, mt, err) = ParseMarket(args[1]);
        if (!ok) return CommandResult.Fail(err!);
        string quote = args[2].Trim().ToLowerInvariant();
        string symbol = args[3].Trim().ToLowerInvariant();
        if (quote.Length == 0 || symbol.Length == 0) return CommandResult.Fail("Quote and symbol must be non-empty.");
        if (!confirm) return CommandResult.Fail($"Remove symbol '{mt}/{quote}/{symbol}' from blacklist? Use --confirm to proceed.");

        CoreConnection? conn = ResolveConnection(profile, out var connErr);
        if (conn == null) return connErr!;

        return LoadAndUpdate(conn, BLACKLIST_SYMBOLS_KEY, arr =>
        {
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (SymbolEntryEquals(arr[i], mt, quote, symbol))
                {
                    arr.RemoveAt(i);
                    return (true, null);
                }
            }
            return (false, $"Symbol '{mt}/{quote}/{symbol}' not found in blacklist.");
        }, $"Removed symbol '{mt}/{quote}/{symbol}' from blacklist.");
    }
}
