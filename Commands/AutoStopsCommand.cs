using System;
using System.Collections.Generic;
using System.Text;
using MTShared.Algorithms;
using MTShared.Network;
using MTShared.Types;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// AutoStops commands — view and manage auto-stop algorithms on Core.
///
/// Subcommands:
///   autostops list              — list all autostop algorithms with current status
///   autostops baseline          — request autostop baseline recalculation
///   autostops reports [ids]     — get report data for specific autostop algorithm IDs
///
/// Supports @profile targeting.
/// </summary>
public sealed class AutoStopsCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "autostops";
    public string Description => "View and manage auto-stop algorithms (risk management)";
    public string Usage => "autostops <list|baseline|reports [id1,id2,...]> [@profile]";

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
                "  reports    — get report data for autostop algorithm IDs");
        }

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

        if (cleanArgs.Count == 0)
        {
            return CommandResult.Fail("Missing subcommand. Use: list, baseline, reports");
        }

        string subcommand = cleanArgs[0].ToLowerInvariant();

        return subcommand switch
        {
            "list" => HandleList(targetProfile),
            "baseline" => HandleBaseline(targetProfile),
            "reports" => HandleReports(cleanArgs, targetProfile),
            _ => CommandResult.Fail($"Unknown subcommand: {subcommand}. Use: list, baseline, reports")
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

        // AutoStop data comes from the algo store — filter for autostop-type algorithms
        var algos = conn.AlgoStore.GetAll();
        var sb = new StringBuilder();
        sb.AppendLine($"[{conn.Name}] AutoStop Algorithms:");
        sb.AppendLine();

        // Also read autostop settings from profile settings
        string? balanceFilters = conn.ProfileSettingsStore.GetValue("AutoStopAlgorithm.Balance.Filters");
        string? reportFilters = conn.ProfileSettingsStore.GetValue("AutoStopAlgorithm.Report.Filters");
        string? balanceLastUpdate = conn.ProfileSettingsStore.GetValue("AutoStopAlgorithm.Balance.LastUpdate");
        string? reportLastUpdate = conn.ProfileSettingsStore.GetValue("AutoStopAlgorithm.Report.LastUpdate");

        sb.AppendLine("AutoStop Settings:");
        sb.AppendLine($"  Balance Filters: {(string.IsNullOrEmpty(balanceFilters) ? "(none)" : balanceFilters)}");
        sb.AppendLine($"  Report Filters:  {(string.IsNullOrEmpty(reportFilters) ? "(none)" : reportFilters)}");
        sb.AppendLine($"  Balance Last Update: {(string.IsNullOrEmpty(balanceLastUpdate) ? "N/A" : balanceLastUpdate)}");
        sb.AppendLine($"  Report Last Update:  {(string.IsNullOrEmpty(reportLastUpdate) ? "N/A" : reportLastUpdate)}");

        return CommandResult.Ok(sb.ToString());
    }

    private CommandResult HandleBaseline(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        conn.SendAutoStopsBaselineRequest();
        return CommandResult.Ok($"[{conn.Name}] AutoStops baseline recalculation requested.");
    }

    private CommandResult HandleReports(List<string> cleanArgs, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        // Parse algorithm IDs
        var algoIds = new List<long>();
        if (cleanArgs.Count > 1)
        {
            string[] idParts = cleanArgs[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < idParts.Length; i++)
            {
                if (long.TryParse(idParts[i].Trim(), out long id))
                {
                    algoIds.Add(id);
                }
            }
        }

        ReportListData? result = conn.RequestAutoStopsReports(algoIds);
        if (result == null)
        {
            return CommandResult.Fail($"[{conn.Name}] AutoStops reports request failed or timed out.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[{conn.Name}] AutoStops Report Data:");
        sb.AppendLine($"  Total: {result.total:F4}");
        sb.AppendLine($"  Order Count: {result.orderCount}");
        sb.AppendLine($"  Reports: {(result.reports != null ? result.reports.Count : 0)} entries");

        if (result.reports != null)
        {
            for (int i = 0; i < result.reports.Count; i++)
            {
                var report = result.reports[i];
                sb.AppendLine($"  [{i}] {report}");
            }
        }

        return CommandResult.Ok(sb.ToString());
    }
}
