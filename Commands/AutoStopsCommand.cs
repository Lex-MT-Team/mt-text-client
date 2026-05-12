using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MTShared.Algorithms;
using MTShared.Network;
using MTShared.Types;
using MTTextClient.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace MTTextClient.Commands;

/// <summary>
/// AutoStops commands — view and manage auto-stop algorithms on Core.
///
/// Subcommands:
///   autostops list              — list all autostop algorithms with current status
///   autostops baseline          — request autostop baseline recalculation
///   autostops reports [ids]     — get report data for specific autostop algorithm IDs
///   autostops add ...           — append a new filter to AutoStopAlgorithm.Balance.Filters
///   autostops edit &lt;idx&gt; ...    — mutate a filter at the given index
///   autostops start [&lt;idx&gt;]     — enable a filter (or the master switch if idx omitted)
///   autostops stop  [&lt;idx&gt;]     — disable a filter (or the master switch if idx omitted)
///   autostops delete &lt;idx&gt;      — remove a filter at the given index
///
/// Supports @profile targeting.
/// </summary>
public sealed class AutoStopsCommand : ICommand
{
    private const string BalanceFiltersKey = "AutoStopAlgorithm.Balance.Filters";

    private readonly ConnectionManager _manager;

    public string Name => "autostops";
    public string Description => "View and manage auto-stop algorithms (risk management)";
    public string Usage => "autostops <list|baseline|reports|add|edit|start|stop|delete> [args] [@profile]";

    public AutoStopsCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(
                "Usage: autostops <subcommand>\n" +
                "  list       — list all autostop algorithms with status\n" +
                "  baseline   — request baseline recalculation\n" +
                "  reports    — get report data for autostop algorithm IDs\n" +
                "  add        — append a new balance filter\n" +
                "  edit       — mutate a filter by index\n" +
                "  start      — enable a filter (or master switch)\n" +
                "  stop       — disable a filter (or master switch)\n" +
                "  delete     — remove a filter by index");
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
            else if (args[i] == "--confirm")
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
            return CommandResult.Fail("Missing subcommand. Use: list, baseline, reports, add, edit, start, stop, delete");
        }

        string subcommand = cleanArgs[0].ToLowerInvariant();
        var rest = cleanArgs.GetRange(1, cleanArgs.Count - 1);

        return subcommand switch
        {
            "list" => HandleList(targetProfile),
            "baseline" => HandleBaseline(targetProfile),
            "reports" => HandleReports(cleanArgs, targetProfile),
            "add" => HandleAdd(rest, targetProfile, confirmFlag),
            "edit" => HandleEdit(rest, targetProfile, confirmFlag),
            "start" => HandleSetEnabled(rest, targetProfile, confirmFlag, enabled: true, verb: "start"),
            "stop" => HandleSetEnabled(rest, targetProfile, confirmFlag, enabled: false, verb: "stop"),
            "delete" => HandleDelete(rest, targetProfile, confirmFlag),
            _ => CommandResult.Fail($"Unknown subcommand: {subcommand}. Use: list, baseline, reports, add, edit, start, stop, delete")
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
        if (conn == null) return error!;

        var ensureErr = EnsureProfileSettings(conn);
        if (ensureErr != null) return ensureErr;

        string? balanceFilters = conn.ProfileSettingsStore.GetValue(BalanceFiltersKey);
        string? reportFilters = conn.ProfileSettingsStore.GetValue("AutoStopAlgorithm.Report.Filters");
        string? balanceLastUpdate = conn.ProfileSettingsStore.GetValue("AutoStopAlgorithm.Balance.LastUpdate");
        string? reportLastUpdate = conn.ProfileSettingsStore.GetValue("AutoStopAlgorithm.Report.LastUpdate");

        var (parseOk, parsedList, parseErr) = TryParseBalanceList(balanceFilters);
        var values = parseOk ? (parsedList!["Values"] as JArray) ?? new JArray() : new JArray();
        bool masterEnabled = parseOk && parsedList!["isEnabled"]?.Value<bool>() == true;

        var sb = new StringBuilder();
        sb.AppendLine($"[{conn.Name}] AutoStop Algorithms:");
        sb.AppendLine($"  Master switch (Balance.isEnabled): {(masterEnabled ? "ON" : "off")}");
        sb.AppendLine($"  Balance filters: {values.Count}");
        for (int i = 0; i < values.Count; i++)
        {
            var v = values[i] as JObject;
            if (v == null) continue;
            string en = v["isEnabled"]?.Value<bool>() == true ? "ENABLED" : "disabled";
            int ft = v["filterType"]?.Value<int>() ?? 0;
            int mt = v["marketType"]?.Value<int>() ?? 0;
            int srcType = v["valueSourceType"]?.Value<int>() ?? 0;
            string sym = v["symbolList"]?.Value<string>() ?? "";
            string quote = v["quoteList"]?.Value<string>() ?? "";
            var range = v["valueRange"] as JObject;
            double min = range?["min"]?.Value<double>() ?? 0;
            double max = range?["max"]?.Value<double>() ?? 0;
            bool isRange = range?["isRange"]?.Value<bool>() ?? false;
            long tf = v["timeframe"]?.Value<long>() ?? 0;
            sb.AppendLine($"  [{i}] {en}  FT={FilterTypeName(ft)} M={MarketName(mt)} Src={SourceName(srcType)} " +
                $"min={min} max={max} isRange={isRange} tf={tf}ms sym=\"{sym}\" quote=\"{quote}\"");
        }
        sb.AppendLine();
        sb.AppendLine("Report filters (raw):");
        sb.AppendLine($"  {(string.IsNullOrEmpty(reportFilters) ? "(none)" : reportFilters)}");
        sb.AppendLine($"  Balance Last Update: {(string.IsNullOrEmpty(balanceLastUpdate) ? "N/A" : balanceLastUpdate)}");
        sb.AppendLine($"  Report Last Update:  {(string.IsNullOrEmpty(reportLastUpdate) ? "N/A" : reportLastUpdate)}");

        var listData = new List<object>();
        for (int i = 0; i < values.Count; i++)
        {
            var v = values[i] as JObject;
            if (v == null) continue;
            var range = v["valueRange"] as JObject;
            listData.Add(new
            {
                Index = i,
                Enabled = v["isEnabled"]?.Value<bool>() ?? false,
                FilterType = FilterTypeName(v["filterType"]?.Value<int>() ?? 0),
                MarketType = MarketName(v["marketType"]?.Value<int>() ?? 0),
                SourceType = SourceName(v["valueSourceType"]?.Value<int>() ?? 0),
                Min = range?["min"]?.Value<double>() ?? 0,
                Max = range?["max"]?.Value<double>() ?? 0,
                IsRange = range?["isRange"]?.Value<bool>() ?? false,
                TimeframeMs = v["timeframe"]?.Value<long>() ?? 0,
                SymbolList = v["symbolList"]?.Value<string>() ?? "",
                QuoteList = v["quoteList"]?.Value<string>() ?? "",
                AlgorithmId = v["algorithmId"]?.Value<long>() ?? 0,
                LastAlgoName = v["lastAlgoName"]?.Value<string>() ?? "",
                PauseAlgo = v["pauseAlgo"]?.Value<bool>() ?? false,
            });
        }

        return CommandResult.Ok(sb.ToString(),
            new
            {
                Server = conn.Name,
                MasterEnabled = masterEnabled,
                BalanceFilterCount = values.Count,
                Filters = listData,
                ReportFiltersRaw = reportFilters ?? "",
                BalanceLastUpdate = balanceLastUpdate ?? "",
                ReportLastUpdate = reportLastUpdate ?? "",
            });
    }

    private CommandResult HandleBaseline(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;
        conn.SendAutoStopsBaselineRequest();
        return CommandResult.Ok($"[{conn.Name}] AutoStops baseline recalculation requested.");
    }

    private CommandResult HandleReports(List<string> cleanArgs, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        var algoIds = new List<long>();
        if (cleanArgs.Count > 1)
        {
            string[] idParts = cleanArgs[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < idParts.Length; i++)
            {
                if (long.TryParse(idParts[i].Trim(), out long id)) algoIds.Add(id);
            }
        }

        ReportListData? result = conn.RequestAutoStopsReports(algoIds);
        if (result == null)
        {
            return CommandResult.Ok(
                $"[{conn.Name}] AutoStops Report Data: (empty)",
                new { Server = conn.Name, Total = 0.0, OrderCount = 0, Reports = new List<object>() });
        }
        var sb = new StringBuilder();
        sb.AppendLine($"[{conn.Name}] AutoStops Report Data:");
        sb.AppendLine($"  Total: {result.total:F4}");
        sb.AppendLine($"  Order Count: {result.orderCount}");
        sb.AppendLine($"  Reports: {(result.reports != null ? result.reports.Count : 0)} entries");
        if (result.reports != null)
        {
            for (int i = 0; i < result.reports.Count; i++)
                sb.AppendLine($"  [{i}] {result.reports[i]}");
        }
        return CommandResult.Ok(sb.ToString(),
            new
            {
                Server = conn.Name,
                Total = result.total,
                OrderCount = result.orderCount,
                Reports = result.reports ?? new List<ReportData>()
            });
    }

    private CommandResult HandleAdd(List<string> args, string? targetProfile, bool confirmed)
    {
        if (!confirmed) return CommandResult.Fail("autostops add requires --confirm.");
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        var flags = ParseFlags(args);
        if (!flags.TryGetValue("--max-loss", out string? maxLossStr) ||
            !double.TryParse(maxLossStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double maxLoss))
        {
            return CommandResult.Fail("autostops add --max-loss <value> is required (e.g. -0.1 or 5.0).");
        }
        double valueMax = TryGetDouble(flags, "--value-max", 1e15);
        bool isRange = flags.ContainsKey("--is-range");
        int filterType = ResolveFilterType(flags.GetValueOrDefault("--filter-type") ?? "GLOBAL_BY_SYMBOL");
        int sourceType = ResolveSourceType(flags.GetValueOrDefault("--source-type") ?? "VALUE");
        int marketType = ResolveMarket(flags.GetValueOrDefault("--market") ?? "FUTURES");
        long timeframeMs = (long)TryGetDouble(flags, "--timeframe-ms", 86_400_000.0);
        string symbolList = flags.GetValueOrDefault("--symbols") ?? "";
        string quoteList = flags.GetValueOrDefault("--quotes") ?? "";
        bool pauseAlgo = flags.ContainsKey("--pause-algo");

        var ensureErr = EnsureProfileSettings(conn);
        if (ensureErr != null) return ensureErr;

        string? raw = conn.ProfileSettingsStore.GetValue(BalanceFiltersKey);
        JObject list = ParseOrInit(raw);
        var values = (JArray)list["Values"]!;

        // Default new filters to *disabled*; the LiveTrade contract is
        // add then explicit start so each step is visible.
        var newFilter = new JObject
        {
            ["isEnabled"] = false,
            ["filterType"] = filterType,
            ["marketType"] = marketType,
            ["symbolList"] = symbolList,
            ["algorithmId"] = 0L,
            ["lastAlgoName"] = "",
            ["valueSourceType"] = sourceType,
            ["valueRange"] = new JObject
            {
                ["min"] = maxLoss,
                ["max"] = valueMax,
                ["isRange"] = isRange,
            },
            ["timeframe"] = timeframeMs,
            ["pauseAlgo"] = pauseAlgo,
            ["sleepTime"] = 0.0,
            ["quoteList"] = quoteList,
        };
        values.Add(newFilter);
        int newIndex = values.Count - 1;
        // Master switch defaults to true so individual filter toggling works.
        if (list["isEnabled"] == null || list["isEnabled"]!.Type == JTokenType.Null)
            list["isEnabled"] = true;

        var updateErr = WriteBalanceList(conn, list);
        if (updateErr != null) return updateErr;
        return CommandResult.Ok(
            $"[{conn.Name}] AutoStop balance filter added at index {newIndex} (disabled — use 'autostops start {newIndex}' to activate).",
            new { Server = conn.Name, Index = newIndex, FilterCount = values.Count });
    }

    private CommandResult HandleEdit(List<string> args, string? targetProfile, bool confirmed)
    {
        if (!confirmed) return CommandResult.Fail("autostops edit requires --confirm.");
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;
        if (args.Count == 0 || !int.TryParse(args[0], out int idx))
            return CommandResult.Fail("Usage: autostops edit <index> [flags] --confirm");

        var ensureErr = EnsureProfileSettings(conn);
        if (ensureErr != null) return ensureErr;

        string? raw = conn.ProfileSettingsStore.GetValue(BalanceFiltersKey);
        JObject list = ParseOrInit(raw);
        var values = (JArray)list["Values"]!;
        if (idx < 0 || idx >= values.Count)
            return CommandResult.Fail($"Index {idx} out of range (have {values.Count} filter(s)).");
        var filter = (JObject)values[idx];

        var flags = ParseFlags(args.GetRange(1, args.Count - 1));
        if (flags.TryGetValue("--max-loss", out string? mls) && double.TryParse(mls, NumberStyles.Any, CultureInfo.InvariantCulture, out double ml))
            ((JObject)filter["valueRange"]!)["min"] = ml;
        if (flags.TryGetValue("--value-max", out string? vms) && double.TryParse(vms, NumberStyles.Any, CultureInfo.InvariantCulture, out double vm))
            ((JObject)filter["valueRange"]!)["max"] = vm;
        if (flags.ContainsKey("--is-range")) ((JObject)filter["valueRange"]!)["isRange"] = true;
        if (flags.ContainsKey("--no-range")) ((JObject)filter["valueRange"]!)["isRange"] = false;
        if (flags.TryGetValue("--filter-type", out string? ft)) filter["filterType"] = ResolveFilterType(ft);
        if (flags.TryGetValue("--source-type", out string? st)) filter["valueSourceType"] = ResolveSourceType(st);
        if (flags.TryGetValue("--market", out string? mk)) filter["marketType"] = ResolveMarket(mk);
        if (flags.TryGetValue("--timeframe-ms", out string? tfs) && long.TryParse(tfs, NumberStyles.Any, CultureInfo.InvariantCulture, out long tf))
            filter["timeframe"] = tf;
        if (flags.TryGetValue("--symbols", out string? sy)) filter["symbolList"] = sy;
        if (flags.TryGetValue("--quotes", out string? q)) filter["quoteList"] = q;
        if (flags.ContainsKey("--pause-algo")) filter["pauseAlgo"] = true;
        if (flags.ContainsKey("--no-pause-algo")) filter["pauseAlgo"] = false;
        if (flags.TryGetValue("--enabled", out string? en))
            filter["isEnabled"] = en.Equals("true", StringComparison.OrdinalIgnoreCase) || en == "1";

        var updateErr = WriteBalanceList(conn, list);
        if (updateErr != null) return updateErr;
        return CommandResult.Ok(
            $"[{conn.Name}] AutoStop balance filter at index {idx} updated.",
            new { Server = conn.Name, Index = idx, FilterCount = values.Count });
    }

    private CommandResult HandleSetEnabled(List<string> args, string? targetProfile, bool confirmed, bool enabled, string verb)
    {
        if (!confirmed) return CommandResult.Fail($"autostops {verb} requires --confirm.");
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        var ensureErr = EnsureProfileSettings(conn);
        if (ensureErr != null) return ensureErr;

        string? raw = conn.ProfileSettingsStore.GetValue(BalanceFiltersKey);
        JObject list = ParseOrInit(raw);
        var values = (JArray)list["Values"]!;

        int? idx = null;
        if (args.Count > 0 && int.TryParse(args[0], out int parsed)) idx = parsed;

        if (idx == null)
        {
            list["isEnabled"] = enabled;
            var werr1 = WriteBalanceList(conn, list);
            if (werr1 != null) return werr1;
            return CommandResult.Ok(
                $"[{conn.Name}] AutoStop master switch {(enabled ? "ENABLED" : "DISABLED")}.",
                new { Server = conn.Name, MasterEnabled = enabled, FilterCount = values.Count });
        }

        if (idx.Value < 0 || idx.Value >= values.Count)
            return CommandResult.Fail($"Index {idx} out of range (have {values.Count} filter(s)).");
        ((JObject)values[idx.Value])["isEnabled"] = enabled;
        var werr2 = WriteBalanceList(conn, list);
        if (werr2 != null) return werr2;
        return CommandResult.Ok(
            $"[{conn.Name}] AutoStop balance filter at index {idx} {(enabled ? "ENABLED" : "DISABLED")}.",
            new { Server = conn.Name, Index = idx.Value, Enabled = enabled });
    }

    private CommandResult HandleDelete(List<string> args, string? targetProfile, bool confirmed)
    {
        if (!confirmed) return CommandResult.Fail("autostops delete requires --confirm.");
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;
        if (args.Count == 0 || !int.TryParse(args[0], out int idx))
            return CommandResult.Fail("Usage: autostops delete <index> --confirm");

        var ensureErr = EnsureProfileSettings(conn);
        if (ensureErr != null) return ensureErr;

        string? raw = conn.ProfileSettingsStore.GetValue(BalanceFiltersKey);
        JObject list = ParseOrInit(raw);
        var values = (JArray)list["Values"]!;
        if (idx < 0 || idx >= values.Count)
            return CommandResult.Fail($"Index {idx} out of range (have {values.Count} filter(s)).");
        values.RemoveAt(idx);
        var updateErr = WriteBalanceList(conn, list);
        if (updateErr != null) return updateErr;
        return CommandResult.Ok(
            $"[{conn.Name}] AutoStop balance filter at index {idx} deleted (remaining: {values.Count}).",
            new { Server = conn.Name, RemovedIndex = idx, FilterCount = values.Count });
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static CommandResult? EnsureProfileSettings(CoreConnection conn)
    {
        if (conn.ProfileSettingsStore.HasData) return null;
        var (success, reqError) = conn.RequestProfileSettings();
        if (!success) return CommandResult.Fail($"[{conn.Name}] Failed to load profile settings: {reqError}");
        return null;
    }

    private static (bool, JObject?, string?) TryParseBalanceList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (true, new JObject { ["isEnabled"] = false, ["Values"] = new JArray() }, null);
        try
        {
            var tok = JToken.Parse(raw);
            if (tok is JObject obj)
            {
                if (obj["Values"] == null || obj["Values"]!.Type != JTokenType.Array)
                    obj["Values"] = new JArray();
                return (true, obj, null);
            }
            return (false, null, $"Unexpected JSON shape (got {tok.Type}, expected object).");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static JObject ParseOrInit(string? raw)
    {
        var (ok, obj, _) = TryParseBalanceList(raw);
        if (ok && obj != null) return obj;
        return new JObject { ["isEnabled"] = false, ["Values"] = new JArray() };
    }

    private CommandResult? WriteBalanceList(CoreConnection conn, JObject list)
    {
        string newValue = list.ToString(Formatting.None);
        var updated = new Dictionary<string, string> { { BalanceFiltersKey, newValue } };
        var (success, _, error) = conn.UpdateProfileSettings(updated);
        if (!success) return CommandResult.Fail($"[{conn.Name}] Failed to update AutoStops: {error}");
        return null;
    }

    private static Dictionary<string, string> ParseFlags(List<string> args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Count; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            if (i + 1 < args.Count && !args[i + 1].StartsWith("--"))
            {
                d[args[i]] = args[i + 1];
                i++;
            }
            else
            {
                d[args[i]] = "true";
            }
        }
        return d;
    }

    private static double TryGetDouble(Dictionary<string, string> flags, string key, double dflt)
        => flags.TryGetValue(key, out string? s) && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : dflt;

    private static int ResolveFilterType(string s)
    {
        return s.ToUpperInvariant() switch
        {
            "GLOBAL_BY_SYMBOL" or "GBS" or "0" => (int)AutoStopFilterType.GLOBAL_BY_SYMBOL,
            "ALGO_SYMBOLS" or "AS" or "1" => (int)AutoStopFilterType.ALGO_SYMBOLS,
            "ALGO_TOTAL" or "AT" or "2" => (int)AutoStopFilterType.ALGO_TOTAL,
            "CUSTOM" or "C" or "3" => (int)AutoStopFilterType.CUSTOM,
            _ => (int)AutoStopFilterType.GLOBAL_BY_SYMBOL,
        };
    }

    private static int ResolveSourceType(string s)
    {
        return s.ToUpperInvariant() switch
        {
            "VALUE" or "0" => (int)AutoStopFilterValueSourceType.VALUE,
            "PRICE_DELTA_SUM" or "PDS" or "1" => (int)AutoStopFilterValueSourceType.PRICE_DELTA_SUM,
            "PROFIT_FACTOR" or "PF" or "2" => (int)AutoStopFilterValueSourceType.PROFIT_FACTOR,
            _ => (int)AutoStopFilterValueSourceType.VALUE,
        };
    }

    private static int ResolveMarket(string s)
    {
        if (Enum.TryParse<MarketType>(s, ignoreCase: true, out var mt) && Enum.IsDefined(typeof(MarketType), mt))
            return (int)mt;
        return (int)MarketType.FUTURES;
    }

    // AutoStop enums are byte-backed in MTShared, so Enum.IsDefined(typeof(...), int)
    // throws.  Render by name only if the int value falls in the known range; else
    // surface as "(N)" so the operator can still see the raw byte value.
    private static string FilterTypeName(int v)
        => v switch
        {
            0 => "GLOBAL_BY_SYMBOL", 1 => "ALGO_SYMBOLS", 2 => "ALGO_TOTAL", 3 => "CUSTOM",
            _ => $"({v})",
        };
    private static string SourceName(int v)
        => v switch
        {
            0 => "VALUE", 1 => "PRICE_DELTA_SUM", 2 => "PROFIT_FACTOR",
            _ => $"({v})",
        };
    private static string MarketName(int v)
        => v switch
        {
            0 => "UNKNOWN", 1 => "SPOT", 2 => "MARGIN", 3 => "FUTURES", 4 => "DELIVERY",
            _ => $"({v})",
        };
}
