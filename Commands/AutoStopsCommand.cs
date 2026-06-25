using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MTShared.Network;
using MTShared.Types;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// AutoStops commands — view and manage balance auto-stops on Core.
///
/// MTCore 0.7.24554 replaced the pre-24554 settings-blob model
/// (AutoStopAlgorithm.Balance.Filters / AutoStopAlgorithmData) with a live
/// AUTO_STOP request/event subsystem. These commands subscribe for the current
/// snapshot (AutoStopStore) and drive Add/Update/Run/Stop/Remove requests over
/// the wire; the index arguments below are positions in the snapshot the
/// preceding 'list' shows, resolved to the core-assigned auto-stop id.
///
/// Subcommands:
///   autostops list              — list balance + report auto-stops with status
///   autostops baseline          — request auto-stop baseline recalculation
///   autostops reports [ids]     — get report data for specific auto-stop IDs
///   autostops add ...           — create a new balance auto-stop (disabled)
///   autostops edit &lt;idx&gt; ...    — mutate a balance auto-stop by list index
///   autostops start [&lt;idx&gt;]     — enable one balance auto-stop (or all)
///   autostops stop  [&lt;idx&gt;]     — disable one balance auto-stop (or all)
///   autostops delete &lt;idx&gt;      — remove a balance auto-stop by list index
///
/// Supports @profile targeting.
/// </summary>
public sealed class AutoStopsCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "autostops";
    public string Description => "View and manage balance auto-stops (risk management)";
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
                "  list       — list balance + report auto-stops with status\n" +
                "  baseline   — request baseline recalculation\n" +
                "  reports    — get report data for auto-stop IDs\n" +
                "  add        — create a new balance auto-stop\n" +
                "  edit       — mutate a balance auto-stop by list index\n" +
                "  start      — enable a balance auto-stop (or all)\n" +
                "  stop       — disable a balance auto-stop (or all)\n" +
                "  delete     — remove a balance auto-stop by list index");
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

        conn.ForceRefreshAutoStops();
        IReadOnlyList<AutoStopOnBalanceData> balance = conn.AutoStopStore.Balance;
        IReadOnlyList<AutoStopOnReportsData> reports = conn.AutoStopStore.Reports;

        var sb = new StringBuilder();
        sb.AppendLine($"[{conn.Name}] Balance auto-stops ({balance.Count}):");
        for (int i = 0; i < balance.Count; i++)
        {
            AutoStopOnBalanceData a = balance[i];
            string en = a.isRunning ? "RUNNING" : "stopped";
            sb.AppendLine($"  [{i}] {en}  id={a.id} name=\"{a.name ?? ""}\" market={MarketName((int)a.marketType)} " +
                $"maxLoss={a.maxLoss} asset=\"{a.asset ?? ""}\" keywords=\"{a.keywords ?? ""}\"" +
                $"{(a.excludeKeywords ? " (excl)" : "")} panicSell={a.panicSellIfTriggered} " +
                $"algos={a.algorithmCount} baseLine={a.baseLine} balance={a.currentBalance}");
        }
        sb.AppendLine();
        sb.AppendLine($"Report auto-stops ({reports.Count}):");
        for (int i = 0; i < reports.Count; i++)
        {
            AutoStopOnReportsData r = reports[i];
            sb.AppendLine($"  [{i}] {(r.isRunning ? "RUNNING" : "stopped")} id={r.id} name=\"{r.name ?? ""}\" " +
                $"maxLoss={r.maxLoss} tf={r.timeFrame} symbols=\"{r.symbols ?? ""}\"");
        }

        return CommandResult.Ok(sb.ToString(),
            new
            {
                Server = conn.Name,
                BalanceCount = balance.Count,
                Balance = balance.Select((a, i) => new
                {
                    Index = i,
                    a.id,
                    a.name,
                    MarketType = MarketName((int)a.marketType),
                    a.maxLoss,
                    a.asset,
                    a.keywords,
                    a.excludeKeywords,
                    a.panicSellIfTriggered,
                    a.isRunning,
                    a.algorithmCount,
                    a.baseLine,
                    a.currentBalance,
                }).ToList(),
                ReportCount = reports.Count,
                Reports = reports.Select((r, i) => new
                {
                    Index = i,
                    r.id,
                    r.name,
                    r.maxLoss,
                    TimeFrame = r.timeFrame.ToString(),
                    r.symbols,
                    r.isRunning,
                }).ToList(),
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

        var autostop = new AutoStopOnBalanceData
        {
            id = 0, // core assigns the id on add
            name = flags.GetValueOrDefault("--name") ?? "",
            marketType = (MarketType)ResolveMarket(flags.GetValueOrDefault("--market") ?? "FUTURES"),
            asset = flags.GetValueOrDefault("--asset") ?? "usdt",
            keywords = flags.GetValueOrDefault("--keywords") ?? "",
            excludeKeywords = flags.ContainsKey("--exclude-keywords"),
            maxLoss = maxLoss,
            panicSellIfTriggered = flags.ContainsKey("--panic-sell"),
            isRunning = false,
        };

        conn.SendAutoStopBalanceRequest(new AutoStopOnBalanceAddRequestData { AutoStop = autostop });
        conn.ForceRefreshAutoStops();

        return CommandResult.Ok(
            $"[{conn.Name}] Balance auto-stop created (disabled — use 'autostops start' to activate). " +
            $"Now {conn.AutoStopStore.Balance.Count} balance auto-stop(s).",
            new { Server = conn.Name, BalanceCount = conn.AutoStopStore.Balance.Count });
    }

    private CommandResult HandleEdit(List<string> args, string? targetProfile, bool confirmed)
    {
        if (!confirmed) return CommandResult.Fail("autostops edit requires --confirm.");
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;
        if (args.Count == 0 || !int.TryParse(args[0], out int idx))
            return CommandResult.Fail("Usage: autostops edit <index> [flags] --confirm");

        conn.ForceRefreshAutoStops();
        IReadOnlyList<AutoStopOnBalanceData> balance = conn.AutoStopStore.Balance;
        if (idx < 0 || idx >= balance.Count)
            return CommandResult.Fail($"Index {idx} out of range (have {balance.Count} balance auto-stop(s)).");
        AutoStopOnBalanceData a = balance[idx];

        var flags = ParseFlags(args.GetRange(1, args.Count - 1));
        if (flags.TryGetValue("--max-loss", out string? mls) && double.TryParse(mls, NumberStyles.Any, CultureInfo.InvariantCulture, out double ml))
            a.maxLoss = ml;
        if (flags.TryGetValue("--name", out string? name)) a.name = name;
        if (flags.TryGetValue("--market", out string? mk)) a.marketType = (MarketType)ResolveMarket(mk);
        if (flags.TryGetValue("--asset", out string? asset)) a.asset = asset;
        if (flags.TryGetValue("--keywords", out string? kw)) a.keywords = kw;
        if (flags.ContainsKey("--exclude-keywords")) a.excludeKeywords = true;
        if (flags.ContainsKey("--include-keywords")) a.excludeKeywords = false;
        if (flags.ContainsKey("--panic-sell")) a.panicSellIfTriggered = true;
        if (flags.ContainsKey("--no-panic-sell")) a.panicSellIfTriggered = false;
        if (flags.TryGetValue("--enabled", out string? en))
            a.isRunning = en.Equals("true", StringComparison.OrdinalIgnoreCase) || en == "1";

        conn.SendAutoStopBalanceRequest(new AutoStopOnBalanceUpdateRequestData { AutoStop = a });
        conn.ForceRefreshAutoStops();
        return CommandResult.Ok(
            $"[{conn.Name}] Balance auto-stop id={a.id} updated.",
            new { Server = conn.Name, Index = idx, a.id });
    }

    private CommandResult HandleSetEnabled(List<string> args, string? targetProfile, bool confirmed, bool enabled, string verb)
    {
        if (!confirmed) return CommandResult.Fail($"autostops {verb} requires --confirm.");
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        conn.ForceRefreshAutoStops();
        IReadOnlyList<AutoStopOnBalanceData> balance = conn.AutoStopStore.Balance;

        int? idx = null;
        if (args.Count > 0 && int.TryParse(args[0], out int parsedIndex)) idx = parsedIndex;

        IEnumerable<AutoStopOnBalanceData> targets;
        if (idx == null)
        {
            targets = balance;
        }
        else
        {
            if (idx.Value < 0 || idx.Value >= balance.Count)
                return CommandResult.Fail($"Index {idx} out of range (have {balance.Count} balance auto-stop(s)).");
            targets = new[] { balance[idx.Value] };
        }

        int count = 0;
        foreach (AutoStopOnBalanceData a in targets)
        {
            AutoStopRequestData req = enabled
                ? new AutoStopOnBalanceRunRequestData { AutoStop = a }
                : new AutoStopOnBalanceStopRequestData { AutoStop = a };
            conn.SendAutoStopBalanceRequest(req);
            count++;
        }
        conn.ForceRefreshAutoStops();
        return CommandResult.Ok(
            $"[{conn.Name}] {count} balance auto-stop(s) {(enabled ? "STARTED" : "STOPPED")}.",
            new { Server = conn.Name, Enabled = enabled, Count = count });
    }

    private CommandResult HandleDelete(List<string> args, string? targetProfile, bool confirmed)
    {
        if (!confirmed) return CommandResult.Fail("autostops delete requires --confirm.");
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;
        if (args.Count == 0 || !int.TryParse(args[0], out int idx))
            return CommandResult.Fail("Usage: autostops delete <index> --confirm");

        conn.ForceRefreshAutoStops();
        IReadOnlyList<AutoStopOnBalanceData> balance = conn.AutoStopStore.Balance;
        if (idx < 0 || idx >= balance.Count)
            return CommandResult.Fail($"Index {idx} out of range (have {balance.Count} balance auto-stop(s)).");
        AutoStopOnBalanceData a = balance[idx];

        conn.SendAutoStopBalanceRequest(new AutoStopOnBalanceRemoveRequestData { AutoStop = a });
        conn.ForceRefreshAutoStops();
        return CommandResult.Ok(
            $"[{conn.Name}] Balance auto-stop id={a.id} deleted (remaining: {conn.AutoStopStore.Balance.Count}).",
            new { Server = conn.Name, RemovedId = a.id, BalanceCount = conn.AutoStopStore.Balance.Count });
    }

    // ── helpers ─────────────────────────────────────────────────────────────

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

    private static int ResolveMarket(string s)
    {
        if (Enum.TryParse<MarketType>(s, ignoreCase: true, out var mt) && Enum.IsDefined(typeof(MarketType), mt))
            return (int)mt;
        return (int)MarketType.FUTURES;
    }

    private static string MarketName(int v)
        => v switch
        {
            0 => "UNKNOWN", 1 => "SPOT", 2 => "MARGIN", 3 => "FUTURES", 4 => "DELIVERY",
            _ => $"({v})",
        };
}
