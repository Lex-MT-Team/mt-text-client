using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MTShared.Types;
using MTTextClient.Core;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Commands;

/// <summary>
/// Typed CRUD over the profile-level WhiteList.
///
/// Storage shape (mirrors BlackList — confirmed via live-bench probe):
///   WhiteList.Symbols : JArray of { MarketType:int, QuoteAsset:string, Symbol:string, TimeFilter:{} }
///   WhiteList.Quotes  : JArray of { MarketType:int, QuoteAsset:string, TimeFilter:{} }
///   WhiteList.Only    : boolean toggle ("true"/"false" string)
///
/// This is the <b>profile-level</b> whitelist (which pairs the profile is
/// allowed to trade) — distinct from per-algo <c>whiteList</c> in argsJson
/// which <c>mt_algos_bulk_edit</c> mutates.
///
/// Subcommands:
///   whitelist list
///   whitelist add-symbol    &lt;market&gt; &lt;quote&gt; &lt;symbol&gt; --confirm
///   whitelist add-quote     &lt;market&gt; &lt;quote&gt;            --confirm
///   whitelist remove-symbol &lt;market&gt; &lt;quote&gt; &lt;symbol&gt; --confirm
///   whitelist remove-quote  &lt;market&gt; &lt;quote&gt;            --confirm
///   whitelist bulk-add-symbol &lt;market&gt; &lt;quote&gt; &lt;sym,sym,...&gt; --confirm
///   whitelist bulk-add-quote  &lt;market&gt; &lt;quote,quote,...&gt;     --confirm
///   whitelist bulk-remove-symbol &lt;market&gt; &lt;quote&gt; &lt;sym,sym,...&gt; --confirm
///   whitelist bulk-remove-quote  &lt;market&gt; &lt;quote,quote,...&gt;     --confirm
/// </summary>
public sealed class WhitelistCommand : ICommand
{
    private const string KeySymbols = "WhiteList.Symbols";
    private const string KeyQuotes = "WhiteList.Quotes";
    private const string KeyOnly = "WhiteList.Only";

    private readonly ConnectionManager _manager;

    public string Name => "whitelist";
    public string Description => "Profile-level whitelist CRUD";
    public string Usage => "whitelist <list|add-symbol|add-quote|remove-symbol|remove-quote|bulk-*> [args] [@profile]";

    public WhitelistCommand(ConnectionManager manager) { _manager = manager; }

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0) return CommandResult.Fail(Usage);

        string? targetProfile = null;
        bool confirmFlag = false;
        var cleanArgs = new List<string>();
        foreach (string arg in args)
        {
            if (arg.StartsWith('@')) targetProfile = arg[1..];
            else if (arg.Equals("--confirm", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-y", StringComparison.OrdinalIgnoreCase)) confirmFlag = true;
            else cleanArgs.Add(arg);
        }
        if (cleanArgs.Count == 0) return CommandResult.Fail(Usage);

        string sub = cleanArgs[0].ToLowerInvariant();
        var rest = cleanArgs.GetRange(1, cleanArgs.Count - 1);

        return sub switch
        {
            "list" or "ls" => HandleList(targetProfile),
            "add-symbol" => HandleAddSymbol(rest, targetProfile, confirmFlag, bulk: false),
            "add-quote" => HandleAddQuote(rest, targetProfile, confirmFlag, bulk: false),
            "remove-symbol" => HandleRemoveSymbol(rest, targetProfile, confirmFlag, bulk: false),
            "remove-quote" => HandleRemoveQuote(rest, targetProfile, confirmFlag, bulk: false),
            "bulk-add-symbol" => HandleAddSymbol(rest, targetProfile, confirmFlag, bulk: true),
            "bulk-add-quote" => HandleAddQuote(rest, targetProfile, confirmFlag, bulk: true),
            "bulk-remove-symbol" => HandleRemoveSymbol(rest, targetProfile, confirmFlag, bulk: true),
            "bulk-remove-quote" => HandleRemoveQuote(rest, targetProfile, confirmFlag, bulk: true),
            _ => CommandResult.Fail($"Unknown subcommand: '{sub}'.")
        };
    }

    private CoreConnection? ResolveConnection(string? targetProfile, out CommandResult? error)
    {
        error = null;
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            error = targetProfile != null
                ? CommandResult.Fail($"No connection '{targetProfile}'.")
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
        if (conn == null) return error!;

        var ensureErr = EnsureSettings(conn);
        if (ensureErr != null) return ensureErr;

        string rawSyms = conn.ProfileSettingsStore.GetValue(KeySymbols) ?? "[]";
        string rawQuotes = conn.ProfileSettingsStore.GetValue(KeyQuotes) ?? "[]";
        string only = conn.ProfileSettingsStore.GetValue(KeyOnly) ?? "false";

        var (sok, symArr, symErr) = TryParseArray(rawSyms);
        var (qok, quoteArr, qErr) = TryParseArray(rawQuotes);

        var sb = new StringBuilder();
        sb.AppendLine($"[{conn.Name}] WhiteList:");
        sb.AppendLine($"  WhiteList.Only:    {only}");
        sb.AppendLine($"  WhiteList.Symbols ({(sok ? symArr!.Count : 0)}):");
        if (sok)
        {
            foreach (var e in symArr!.OfType<JObject>())
                sb.AppendLine($"    {MarketName(e["MarketType"]?.Value<int>() ?? 0)} {e["QuoteAsset"]?.Value<string>()} {e["Symbol"]?.Value<string>()}");
        }
        else sb.AppendLine($"    (unparseable: {symErr})");

        sb.AppendLine($"  WhiteList.Quotes ({(qok ? quoteArr!.Count : 0)}):");
        if (qok)
        {
            foreach (var e in quoteArr!.OfType<JObject>())
                sb.AppendLine($"    {MarketName(e["MarketType"]?.Value<int>() ?? 0)} {e["QuoteAsset"]?.Value<string>()}");
        }
        else sb.AppendLine($"    (unparseable: {qErr})");

        var symData = sok
            ? symArr!.OfType<JObject>().Select(e => (object)new
            {
                MarketType = MarketName(e["MarketType"]?.Value<int>() ?? 0),
                QuoteAsset = e["QuoteAsset"]?.Value<string>() ?? "",
                Symbol = e["Symbol"]?.Value<string>() ?? "",
            }).ToList()
            : new List<object>();
        var quoteData = qok
            ? quoteArr!.OfType<JObject>().Select(e => (object)new
            {
                MarketType = MarketName(e["MarketType"]?.Value<int>() ?? 0),
                QuoteAsset = e["QuoteAsset"]?.Value<string>() ?? "",
            }).ToList()
            : new List<object>();

        return CommandResult.Ok(sb.ToString(), new
        {
            Server = conn.Name,
            Only = string.Equals(only, "true", StringComparison.OrdinalIgnoreCase),
            Symbols = symData,
            SymbolCount = symData.Count,
            Quotes = quoteData,
            QuoteCount = quoteData.Count,
            SymbolsParsed = sok,
            QuotesParsed = qok,
        });
    }

    // ── add-symbol / bulk-add-symbol ─────────────────────────────────────────

    private CommandResult HandleAddSymbol(List<string> rest, string? targetProfile, bool confirmed, bool bulk)
    {
        if (!confirmed) return CommandResult.Fail($"whitelist {(bulk ? "bulk-add-symbol" : "add-symbol")} requires --confirm.");
        if (rest.Count < 3)
            return CommandResult.Fail($"Usage: whitelist {(bulk ? "bulk-add-symbol" : "add-symbol")} <market> <quote> <{(bulk ? "csv-of-symbols" : "symbol")}> --confirm");

        var (ok, market, mErr) = ParseMarket(rest[0]);
        if (!ok) return CommandResult.Fail(mErr!);
        string quote = rest[1].ToLowerInvariant();
        var symbols = bulk
            ? rest[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList()
            : new List<string> { rest[2].ToLowerInvariant() };
        if (symbols.Count == 0) return CommandResult.Fail("No symbols supplied.");

        return MutateSymbols(targetProfile, market, quote, symbols, add: true);
    }

    private CommandResult HandleRemoveSymbol(List<string> rest, string? targetProfile, bool confirmed, bool bulk)
    {
        if (!confirmed) return CommandResult.Fail($"whitelist {(bulk ? "bulk-remove-symbol" : "remove-symbol")} requires --confirm.");
        if (rest.Count < 3)
            return CommandResult.Fail($"Usage: whitelist {(bulk ? "bulk-remove-symbol" : "remove-symbol")} <market> <quote> <{(bulk ? "csv-of-symbols" : "symbol")}> --confirm");

        var (ok, market, mErr) = ParseMarket(rest[0]);
        if (!ok) return CommandResult.Fail(mErr!);
        string quote = rest[1].ToLowerInvariant();
        var symbols = bulk
            ? rest[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList()
            : new List<string> { rest[2].ToLowerInvariant() };
        if (symbols.Count == 0) return CommandResult.Fail("No symbols supplied.");

        return MutateSymbols(targetProfile, market, quote, symbols, add: false);
    }

    private CommandResult MutateSymbols(string? targetProfile, MarketType market, string quote, List<string> symbols, bool add)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        var ensureErr = EnsureSettings(conn);
        if (ensureErr != null) return ensureErr;

        string raw = conn.ProfileSettingsStore.GetValue(KeySymbols) ?? "[]";
        var (parseOk, arr, _) = TryParseArray(raw);
        if (!parseOk) arr = new JArray();  // recover from corrupt state — overwrite with a clean array

        var alreadyPresent = new List<string>();
        var notFound = new List<string>();
        var notTradable = new List<string>();

        foreach (string s in symbols)
        {
            bool exists = arr!.OfType<JObject>().Any(e =>
                (e["MarketType"]?.Value<int>() ?? 0) == (int)market &&
                string.Equals(e["QuoteAsset"]?.Value<string>(), quote, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e["Symbol"]?.Value<string>(), s, StringComparison.OrdinalIgnoreCase));
            if (add)
            {
                if (exists) { alreadyPresent.Add(s); continue; }
                arr!.Add(new JObject
                {
                    ["MarketType"] = (int)market,
                    ["QuoteAsset"] = quote,
                    ["Symbol"] = s,
                    ["TimeFilter"] = new JObject(),
                });
                // Pre-flight: warn if not in pair cache.
                var pair = conn.ExchangeInfoStore.GetTradePair(s);
                if (pair == null) notTradable.Add(s);
            }
            else
            {
                if (!exists) { notFound.Add(s); continue; }
                int removed = 0;
                for (int i = arr!.Count - 1; i >= 0; i--)
                {
                    if (arr[i] is JObject e2 &&
                        (e2["MarketType"]?.Value<int>() ?? 0) == (int)market &&
                        string.Equals(e2["QuoteAsset"]?.Value<string>(), quote, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(e2["Symbol"]?.Value<string>(), s, StringComparison.OrdinalIgnoreCase))
                    { arr.RemoveAt(i); removed++; }
                }
                if (removed == 0) notFound.Add(s);
            }
        }

        if (!add && notFound.Count == symbols.Count && notFound.Count > 0)
            return CommandResult.Fail(
                $"not_found: none of [{string.Join(",", notFound)}] are in {KeySymbols} on {conn.Name}.");

        string newValue = arr!.ToString(Newtonsoft.Json.Formatting.None);
        if (newValue == raw)
        {
            return CommandResult.Ok(
                $"[{conn.Name}] whitelist {(add ? "add" : "remove")}-symbol: no_change (idempotent).",
                BuildMutateData(conn.Name, add, "symbol", false, alreadyPresent, notFound, notTradable, arr.Count));
        }
        var (success, _, error2) = conn.UpdateProfileSettings(new Dictionary<string, string> { [KeySymbols] = newValue });
        if (!success) return CommandResult.Fail($"[{conn.Name}] Failed to update {KeySymbols}: {error2}");
        return CommandResult.Ok(
            $"[{conn.Name}] whitelist {(add ? "add" : "remove")}-symbol: " +
            $"{(add ? symbols.Count - alreadyPresent.Count : symbols.Count - notFound.Count)} applied; " +
            $"WhiteList.Symbols now {arr.Count} entries." +
            BuildWarningSuffix(alreadyPresent, notFound, notTradable),
            BuildMutateData(conn.Name, add, "symbol", true, alreadyPresent, notFound, notTradable, arr.Count));
    }

    // ── add-quote / bulk-add-quote ───────────────────────────────────────────

    private CommandResult HandleAddQuote(List<string> rest, string? targetProfile, bool confirmed, bool bulk)
    {
        if (!confirmed) return CommandResult.Fail($"whitelist {(bulk ? "bulk-add-quote" : "add-quote")} requires --confirm.");
        if (rest.Count < 2)
            return CommandResult.Fail($"Usage: whitelist {(bulk ? "bulk-add-quote" : "add-quote")} <market> <{(bulk ? "csv-of-quotes" : "quote")}> --confirm");

        var (ok, market, mErr) = ParseMarket(rest[0]);
        if (!ok) return CommandResult.Fail(mErr!);
        var quotes = bulk
            ? rest[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList()
            : new List<string> { rest[1].ToLowerInvariant() };
        if (quotes.Count == 0) return CommandResult.Fail("No quotes supplied.");
        return MutateQuotes(targetProfile, market, quotes, add: true);
    }

    private CommandResult HandleRemoveQuote(List<string> rest, string? targetProfile, bool confirmed, bool bulk)
    {
        if (!confirmed) return CommandResult.Fail($"whitelist {(bulk ? "bulk-remove-quote" : "remove-quote")} requires --confirm.");
        if (rest.Count < 2)
            return CommandResult.Fail($"Usage: whitelist {(bulk ? "bulk-remove-quote" : "remove-quote")} <market> <{(bulk ? "csv-of-quotes" : "quote")}> --confirm");

        var (ok, market, mErr) = ParseMarket(rest[0]);
        if (!ok) return CommandResult.Fail(mErr!);
        var quotes = bulk
            ? rest[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList()
            : new List<string> { rest[1].ToLowerInvariant() };
        if (quotes.Count == 0) return CommandResult.Fail("No quotes supplied.");
        return MutateQuotes(targetProfile, market, quotes, add: false);
    }

    private CommandResult MutateQuotes(string? targetProfile, MarketType market, List<string> quotes, bool add)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        var ensureErr = EnsureSettings(conn);
        if (ensureErr != null) return ensureErr;

        string raw = conn.ProfileSettingsStore.GetValue(KeyQuotes) ?? "[]";
        var (parseOk, arr, _) = TryParseArray(raw);
        if (!parseOk) arr = new JArray();

        var alreadyPresent = new List<string>();
        var notFound = new List<string>();

        foreach (string q in quotes)
        {
            bool exists = arr!.OfType<JObject>().Any(e =>
                (e["MarketType"]?.Value<int>() ?? 0) == (int)market &&
                string.Equals(e["QuoteAsset"]?.Value<string>(), q, StringComparison.OrdinalIgnoreCase));
            if (add)
            {
                if (exists) { alreadyPresent.Add(q); continue; }
                arr!.Add(new JObject
                {
                    ["MarketType"] = (int)market,
                    ["QuoteAsset"] = q,
                    ["TimeFilter"] = new JObject(),
                });
            }
            else
            {
                if (!exists) { notFound.Add(q); continue; }
                for (int i = arr!.Count - 1; i >= 0; i--)
                {
                    if (arr[i] is JObject e2 &&
                        (e2["MarketType"]?.Value<int>() ?? 0) == (int)market &&
                        string.Equals(e2["QuoteAsset"]?.Value<string>(), q, StringComparison.OrdinalIgnoreCase))
                        arr.RemoveAt(i);
                }
            }
        }

        if (!add && notFound.Count == quotes.Count && notFound.Count > 0)
            return CommandResult.Fail($"not_found: none of [{string.Join(",", notFound)}] are in {KeyQuotes} on {conn.Name}.");

        string newValue = arr!.ToString(Newtonsoft.Json.Formatting.None);
        if (newValue == raw)
            return CommandResult.Ok(
                $"[{conn.Name}] whitelist {(add ? "add" : "remove")}-quote: no_change (idempotent).",
                BuildMutateData(conn.Name, add, "quote", false, alreadyPresent, notFound, new List<string>(), arr.Count));

        var (success, _, error2) = conn.UpdateProfileSettings(new Dictionary<string, string> { [KeyQuotes] = newValue });
        if (!success) return CommandResult.Fail($"[{conn.Name}] Failed to update {KeyQuotes}: {error2}");
        return CommandResult.Ok(
            $"[{conn.Name}] whitelist {(add ? "add" : "remove")}-quote: " +
            $"{(add ? quotes.Count - alreadyPresent.Count : quotes.Count - notFound.Count)} applied; " +
            $"WhiteList.Quotes now {arr.Count} entries." +
            BuildWarningSuffix(alreadyPresent, notFound, new List<string>()),
            BuildMutateData(conn.Name, add, "quote", true, alreadyPresent, notFound, new List<string>(), arr.Count));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static CommandResult? EnsureSettings(CoreConnection conn)
    {
        if (conn.ProfileSettingsStore.HasData) return null;
        var (ok, err) = conn.RequestProfileSettings();
        if (!ok) return CommandResult.Fail($"[{conn.Name}] Failed to load profile settings: {err}");
        return null;
    }

    private static (bool ok, JArray? arr, string? error) TryParseArray(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (true, new JArray(), null);
        try
        {
            var tok = JToken.Parse(raw);
            if (tok is JArray ja) return (true, ja, null);
            return (false, null, $"expected JArray, got {tok.Type}");
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    private static (bool ok, MarketType mt, string? error) ParseMarket(string raw)
    {
        if (Enum.TryParse<MarketType>(raw, ignoreCase: true, out var mt) &&
            Enum.IsDefined(typeof(MarketType), mt) && mt != MarketType.UNKNOWN)
            return (true, mt, null);
        return (false, default, $"Invalid market '{raw}'. Allowed: SPOT, MARGIN, FUTURES, DELIVERY.");
    }

    private static string MarketName(int v) => v switch
    {
        1 => "SPOT", 2 => "MARGIN", 3 => "FUTURES", 4 => "DELIVERY",
        _ => $"({v})",
    };

    private static string BuildWarningSuffix(List<string> alreadyPresent, List<string> notFound, List<string> notTradable)
    {
        var parts = new List<string>();
        if (alreadyPresent.Count > 0) parts.Add($"already_present: {string.Join(",", alreadyPresent)}");
        if (notFound.Count > 0) parts.Add($"not_found: {string.Join(",", notFound)}");
        if (notTradable.Count > 0) parts.Add($"not_tradable: {string.Join(",", notTradable)} (MTCore may refuse at order-place)");
        return parts.Count == 0 ? "" : " | warnings: " + string.Join("; ", parts);
    }

    private static object BuildMutateData(string server, bool add, string type, bool mutated,
        List<string> alreadyPresent, List<string> notFound, List<string> notTradable, int afterCount) =>
        new
        {
            Server = server,
            Action = add ? "add" : "remove",
            Type = type,
            Mutated = mutated,
            AfterCount = afterCount,
            AlreadyPresent = alreadyPresent,
            NotFound = notFound,
            NotTradable = notTradable,
        };
}
