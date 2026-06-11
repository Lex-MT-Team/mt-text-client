using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
///   autostops start [&lt;idx&gt;]     — enable a filter (or all filters if idx omitted)
///   autostops stop  [&lt;idx&gt;]     — disable a filter (or all filters if idx omitted)
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
                "  start      — enable a filter (or all filters)\n" +
                "  stop       — disable a filter (or all filters)\n" +
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

        var (parseOk, values, parseErr) = TryParseBalanceFilters(balanceFilters);

        var sb = new StringBuilder();
        sb.AppendLine($"[{conn.Name}] AutoStop Algorithms:");
        if (!parseOk)
        {
            sb.AppendLine($"  Balance filters: INVALID ({parseErr})");
            sb.AppendLine("  Stored value must be a bare AutoStopAlgorithmData JSON array.");
        }
        sb.AppendLine($"  Balance filters: {values.Count}");
        for (int i = 0; i < values.Count; i++)
        {
            AutoStopAlgorithmData v = values[i];
            string en = v.isRunning ? "ENABLED" : "disabled";
            sb.AppendLine($"  [{i}] {en}  id={v.id} M={MarketName((int)v.marketType)} minMargin={v.minMargin} " +
                $"tf={v.timeFrame} symbol=\"{v.symbolFilter ?? ""}\" asset=\"{v.asset ?? ""}\" " +
                $"panicIfTriggered={v.panicIfTriggered} info=\"{v.info ?? ""}\"");
        }
        sb.AppendLine();
        sb.AppendLine("Report filters (raw):");
        sb.AppendLine($"  {(string.IsNullOrEmpty(reportFilters) ? "(none)" : reportFilters)}");
        sb.AppendLine($"  Balance Last Update: {(string.IsNullOrEmpty(balanceLastUpdate) ? "N/A" : balanceLastUpdate)}");
        sb.AppendLine($"  Report Last Update:  {(string.IsNullOrEmpty(reportLastUpdate) ? "N/A" : reportLastUpdate)}");

        return CommandResult.Ok(sb.ToString(),
            new
            {
                Server = conn.Name,
                Valid = parseOk,
                Error = parseErr,
                BalanceFilterCount = values.Count,
                Filters = values.Select((v, i) => new
                {
                    Index = i,
                    v.id,
                    v.info,
                    MarketType = MarketName((int)v.marketType),
                    v.minMargin,
                    v.algorithmComment,
                    v.notAlgorithmComment,
                    v.isRunning,
                    v.asset,
                    v.panicIfTriggered,
                    v.timeFrame,
                    v.symbolFilter,
                    v.notSymbolFilter,
                    v.reportComment,
                    v.notReportComment,
                    v.marketTypes,
                    v.excludeEmulatedTrades,
                    v.algorithmCount,
                    v.baseLine,
                    v.currentBalance,
                    v.reportTotal,
                    v.orderCount,
                }).ToList(),
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
        if (flags.ContainsKey("--value-max") || flags.ContainsKey("--is-range"))
        {
            return CommandResult.Fail("AutoStopAlgorithmData has no value_max/is_range fields; use --max-loss for minMargin.");
        }
        int filterType = ResolveFilterType(flags.GetValueOrDefault("--filter-type") ?? "GLOBAL_BY_SYMBOL");
        int sourceType = ResolveSourceType(flags.GetValueOrDefault("--source-type") ?? "VALUE");
        int marketType = ResolveMarket(flags.GetValueOrDefault("--market") ?? "FUTURES");
        long timeframeMs = (long)TryGetDouble(flags, "--timeframe-ms", 86_400_000.0);
        string symbolList = flags.GetValueOrDefault("--symbols") ?? "";
        string quoteList = flags.GetValueOrDefault("--quotes") ?? "";
        bool pauseAlgo = flags.ContainsKey("--pause-algo");

        var ensureErr = EnsureProfileSettings(conn);
        if (ensureErr != null) return ensureErr;

        var parsed = ParseOrInit(conn.ProfileSettingsStore.GetValue(BalanceFiltersKey));
        if (parsed.Error != null) return CommandResult.Fail(parsed.Error);
        List<AutoStopAlgorithmData> values = parsed.Filters;

        var newFilter = new AutoStopAlgorithmData
        {
            id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            info = flags.GetValueOrDefault("--info") ?? BuildAutoStopInfo(filterType, sourceType),
            marketType = (MarketType)marketType,
            minMargin = maxLoss,
            algorithmComment = flags.GetValueOrDefault("--algorithm-comment") ?? "",
            notAlgorithmComment = flags.ContainsKey("--not-algorithm-comment"),
            isRunning = false,
            asset = flags.GetValueOrDefault("--asset") ?? FirstCsv(quoteList) ?? "usdt",
            panicIfTriggered = pauseAlgo,
            timeFrame = ResolveTimeFrame(timeframeMs),
            symbolFilter = symbolList,
            notSymbolFilter = flags.ContainsKey("--not-symbol-filter"),
            reportComment = flags.GetValueOrDefault("--report-comment") ?? "",
            notReportComment = flags.ContainsKey("--not-report-comment"),
            marketTypes = new List<MarketType>(),
            excludeEmulatedTrades = flags.ContainsKey("--exclude-emulated-trades"),
            algorithmCount = 0,
            baseLine = 0,
            currentBalance = 0,
            reportTotal = 0,
            orderCount = 0,
        };
        values.Add(newFilter);
        int newIndex = values.Count - 1;

        var updateErr = WriteBalanceFilters(conn, values);
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

        var parsed = ParseOrInit(conn.ProfileSettingsStore.GetValue(BalanceFiltersKey));
        if (parsed.Error != null) return CommandResult.Fail(parsed.Error);
        List<AutoStopAlgorithmData> values = parsed.Filters;
        if (idx < 0 || idx >= values.Count)
            return CommandResult.Fail($"Index {idx} out of range (have {values.Count} filter(s)).");
        AutoStopAlgorithmData filter = values[idx];

        var flags = ParseFlags(args.GetRange(1, args.Count - 1));
        if (flags.ContainsKey("--value-max") || flags.ContainsKey("--is-range") || flags.ContainsKey("--no-range"))
        {
            return CommandResult.Fail("AutoStopAlgorithmData has no value_max/is_range fields; use --max-loss for minMargin.");
        }
        if (flags.TryGetValue("--max-loss", out string? mls) && double.TryParse(mls, NumberStyles.Any, CultureInfo.InvariantCulture, out double ml))
            filter.minMargin = ml;
        bool hasFilterType = flags.TryGetValue("--filter-type", out string? ft);
        bool hasSourceType = flags.TryGetValue("--source-type", out string? st);
        if (hasFilterType || hasSourceType)
            filter.info = BuildAutoStopInfo(ResolveFilterType(ft ?? "GLOBAL_BY_SYMBOL"),
                ResolveSourceType(st ?? "VALUE"));
        if (flags.TryGetValue("--market", out string? mk)) filter.marketType = (MarketType)ResolveMarket(mk);
        if (flags.TryGetValue("--timeframe-ms", out string? tfs) && long.TryParse(tfs, NumberStyles.Any, CultureInfo.InvariantCulture, out long tf))
            filter.timeFrame = ResolveTimeFrame(tf);
        if (flags.TryGetValue("--symbols", out string? sy)) filter.symbolFilter = sy;
        if (flags.TryGetValue("--quotes", out string? q)) filter.asset = FirstCsv(q) ?? filter.asset;
        if (flags.TryGetValue("--asset", out string? asset)) filter.asset = asset;
        if (flags.TryGetValue("--info", out string? info)) filter.info = info;
        if (flags.TryGetValue("--algorithm-comment", out string? ac)) filter.algorithmComment = ac;
        if (flags.TryGetValue("--report-comment", out string? rc)) filter.reportComment = rc;
        if (flags.ContainsKey("--pause-algo")) filter.panicIfTriggered = true;
        if (flags.ContainsKey("--no-pause-algo")) filter.panicIfTriggered = false;
        if (flags.ContainsKey("--not-symbol-filter")) filter.notSymbolFilter = true;
        if (flags.ContainsKey("--match-symbol-filter")) filter.notSymbolFilter = false;
        if (flags.ContainsKey("--not-algorithm-comment")) filter.notAlgorithmComment = true;
        if (flags.ContainsKey("--match-algorithm-comment")) filter.notAlgorithmComment = false;
        if (flags.ContainsKey("--not-report-comment")) filter.notReportComment = true;
        if (flags.ContainsKey("--match-report-comment")) filter.notReportComment = false;
        if (flags.ContainsKey("--exclude-emulated-trades")) filter.excludeEmulatedTrades = true;
        if (flags.ContainsKey("--include-emulated-trades")) filter.excludeEmulatedTrades = false;
        if (flags.TryGetValue("--enabled", out string? en))
            filter.isRunning = en.Equals("true", StringComparison.OrdinalIgnoreCase) || en == "1";

        var updateErr = WriteBalanceFilters(conn, values);
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

        var parsed = ParseOrInit(conn.ProfileSettingsStore.GetValue(BalanceFiltersKey));
        if (parsed.Error != null) return CommandResult.Fail(parsed.Error);
        List<AutoStopAlgorithmData> values = parsed.Filters;

        int? idx = null;
        if (args.Count > 0 && int.TryParse(args[0], out int parsedIndex)) idx = parsedIndex;

        if (idx == null)
        {
            foreach (var filter in values)
                filter.isRunning = enabled;
            var werr1 = WriteBalanceFilters(conn, values);
            if (werr1 != null) return werr1;
            return CommandResult.Ok(
                $"[{conn.Name}] AutoStop balance filters {(enabled ? "ENABLED" : "DISABLED")} ({values.Count}).",
                new { Server = conn.Name, Enabled = enabled, FilterCount = values.Count });
        }

        if (idx.Value < 0 || idx.Value >= values.Count)
            return CommandResult.Fail($"Index {idx} out of range (have {values.Count} filter(s)).");
        values[idx.Value].isRunning = enabled;
        var werr2 = WriteBalanceFilters(conn, values);
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

        var parsed = ParseOrInit(conn.ProfileSettingsStore.GetValue(BalanceFiltersKey));
        if (parsed.Error != null) return CommandResult.Fail(parsed.Error);
        List<AutoStopAlgorithmData> values = parsed.Filters;
        if (idx < 0 || idx >= values.Count)
            return CommandResult.Fail($"Index {idx} out of range (have {values.Count} filter(s)).");
        values.RemoveAt(idx);
        var updateErr = WriteBalanceFilters(conn, values);
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

    private static (bool Ok, List<AutoStopAlgorithmData> Filters, string? Error) TryParseBalanceFilters(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (true, new List<AutoStopAlgorithmData>(), null);
        try
        {
            var tok = JToken.Parse(raw);
            if (tok.Type != JTokenType.Array)
            {
                string hint = tok.Type == JTokenType.Object && tok["Values"] is JArray
                    ? "legacy mt-text-client wrapper object detected"
                    : $"got {tok.Type}";
                return (false, new List<AutoStopAlgorithmData>(),
                    $"Unexpected JSON shape ({hint}; expected bare AutoStopAlgorithmData array).");
            }

            var filters = tok.ToObject<List<AutoStopAlgorithmData>>() ?? new List<AutoStopAlgorithmData>();
            foreach (var filter in filters)
                NormalizeAutoStopFilter(filter);
            return (true, filters, null);
        }
        catch (Exception ex)
        {
            return (false, new List<AutoStopAlgorithmData>(), ex.Message);
        }
    }

    private static (List<AutoStopAlgorithmData> Filters, string? Error) ParseOrInit(string? raw)
    {
        var parsed = TryParseBalanceFilters(raw);
        if (parsed.Ok) return (parsed.Filters, null);
        return (new List<AutoStopAlgorithmData>(), parsed.Error);
    }

    private CommandResult? WriteBalanceFilters(CoreConnection conn, List<AutoStopAlgorithmData> filters)
    {
        foreach (var filter in filters)
            NormalizeAutoStopFilter(filter);
        string newValue = JsonConvert.SerializeObject(filters, Formatting.None);
        var updated = new Dictionary<string, string> { { BalanceFiltersKey, newValue } };
        var (success, _, error) = conn.UpdateProfileSettings(updated);
        if (!success) return CommandResult.Fail($"[{conn.Name}] Failed to update AutoStops: {error}");
        return null;
    }

    private static void NormalizeAutoStopFilter(AutoStopAlgorithmData filter)
    {
        filter.info ??= "";
        filter.algorithmComment ??= "";
        filter.asset = string.IsNullOrWhiteSpace(filter.asset) ? "usdt" : filter.asset;
        filter.symbolFilter ??= "";
        filter.reportComment ??= "";
        filter.marketTypes ??= new List<MarketType>();
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

    private static AutoStopsTimeFrame ResolveTimeFrame(long timeframeMs)
    {
        long hours = Math.Max(1, (long)Math.Round(timeframeMs / 3_600_000.0));
        return hours switch
        {
            <= 1 => AutoStopsTimeFrame.H1,
            <= 2 => AutoStopsTimeFrame.H2,
            <= 3 => AutoStopsTimeFrame.H3,
            <= 6 => AutoStopsTimeFrame.H6,
            <= 12 => AutoStopsTimeFrame.H12,
            <= 24 => AutoStopsTimeFrame.D1,
            <= 48 => AutoStopsTimeFrame.D2,
            <= 72 => AutoStopsTimeFrame.D3,
            <= 168 => AutoStopsTimeFrame.D7,
            _ => AutoStopsTimeFrame.D30,
        };
    }

    private static string BuildAutoStopInfo(int filterType, int sourceType)
        => $"mt-text-client {FilterTypeName(filterType)} {SourceName(sourceType)}";

    private static string? FirstCsv(string csv)
        => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    // AutoStop enums are byte-backed in MTShared, so Enum.IsDefined(typeof(...), int)
    // throws.  Render by name only if the int value falls in the known range; else
    // surface as "(N)" so the caller can still see the raw byte value.
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
