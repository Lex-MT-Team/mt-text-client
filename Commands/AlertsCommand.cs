using System;
using System.Collections.Generic;
using System.Text;
using MTShared.Network;
using MTShared.Types;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// Alerts commands — subscribe to price alerts from Core.
///
/// Subcommands:
///   alerts list                         — show active alerts
///   alerts subscribe                    — subscribe to alert updates
///   alerts unsubscribe                  — unsubscribe from alert updates
///   alerts history [--count N]          — show alert history
///   alerts history-subscribe            — subscribe to alert history
///   alerts history-unsubscribe          — unsubscribe from alert history
///
/// Supports @profile targeting.
/// </summary>
public sealed class AlertsCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "alerts";
    public string Description => "Manage price alerts and alert history subscriptions";
    public string Usage => "alerts <list|subscribe|unsubscribe|history|history-subscribe|history-unsubscribe> [--count N] [@profile]";

    public AlertsCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public CommandResult Execute(string[] args)
    {
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

        string subcommand = cleanArgs.Count > 0 ? cleanArgs[0] : "list";

        switch (subcommand)
        {
            case "list":
                return HandleList(targetProfile);
            case "subscribe":
                return HandleSubscribe(targetProfile);
            case "unsubscribe":
                return HandleUnsubscribe(targetProfile);
            case "history":
                return HandleHistory(cleanArgs.ToArray(), targetProfile);
            case "history-subscribe":
                return HandleHistorySubscribe(targetProfile);
            case "history-unsubscribe":
                return HandleHistoryUnsubscribe(targetProfile);
            default:
                return CommandResult.Fail("Unknown subcommand. Use: list, subscribe, unsubscribe, history, history-subscribe, history-unsubscribe");
        }
    }

    private CommandResult HandleList(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        IReadOnlyList<AlertInfoData> alerts = conn.AlertStore.GetAll();
        if (alerts.Count == 0)
        {
            return CommandResult.Ok($"No alerts on {conn.Name}. Subscribe first with 'alerts subscribe'.");
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"## Alerts — {conn.Name} ({alerts.Count})");
        sb.AppendLine();
        sb.AppendLine("| ID | Name | Symbol | Market | Running | Condition |");
        sb.AppendLine("|----|------|--------|--------|---------|-----------|");

        foreach (AlertInfoData alert in alerts)
        {
            string condition = alert.condition.type.ToString();
            sb.AppendLine($"| {alert.id} | {alert.name} | {alert.symbol} | {alert.marketType} | {alert.isRunning} | {condition} |");
        }

        sb.AppendLine();
        sb.AppendLine($"Subscribed: {conn.IsAlertsSubscribed}");
        return CommandResult.Ok(sb.ToString(), new { count = alerts.Count });
    }

    private CommandResult HandleSubscribe(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!conn.IsConnected)
        {
            return CommandResult.Fail($"{conn.Name} is not connected.");
        }

        if (conn.IsAlertsSubscribed)
        {
            return CommandResult.Ok($"Already subscribed to alerts on {conn.Name}.");
        }

        conn.SubscribeAlerts();
        return CommandResult.Ok($"Subscribed to alerts on {conn.Name}.");
    }

    private CommandResult HandleUnsubscribe(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!conn.IsAlertsSubscribed)
        {
            return CommandResult.Ok($"Not subscribed to alerts on {conn.Name}.");
        }

        conn.UnsubscribeAlerts();
        return CommandResult.Ok($"Unsubscribed from alerts on {conn.Name}.");
    }

    private CommandResult HandleHistory(string[] parts, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        int count = 50;
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i] == "--count" && i + 1 < parts.Length)
            {
                Int32.TryParse(parts[i + 1], out count);
                i++;
            }
        }

        List<AlertHistoryEntry> history = conn.AlertStore.GetHistory(count);
        if (history.Count == 0)
        {
            return CommandResult.Ok($"No alert history on {conn.Name}. Subscribe with 'alerts history-subscribe'.");
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"## Alert History — {conn.Name} (last {history.Count})");
        sb.AppendLine();
        sb.AppendLine("| Time (UTC) | Exchange | Action | Detail |");
        sb.AppendLine("|------------|----------|--------|--------|");

        for (int i = history.Count - 1; i >= 0; i--)
        {
            AlertHistoryEntry entry = history[i];
            string time = entry.ReceivedAtUtc.ToString("HH:mm:ss.fff");
            sb.AppendLine($"| {time} | {entry.ExchangeType} | {entry.ActionType} | {entry.RawJson} |");
        }

        sb.AppendLine();
        sb.AppendLine($"History subscribed: {conn.IsAlertHistorySubscribed}");
        return CommandResult.Ok(sb.ToString(), new { count = history.Count });
    }

    private CommandResult HandleHistorySubscribe(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!conn.IsConnected)
        {
            return CommandResult.Fail($"{conn.Name} is not connected.");
        }

        if (conn.IsAlertHistorySubscribed)
        {
            return CommandResult.Ok($"Already subscribed to alert history on {conn.Name}.");
        }

        conn.SubscribeAlertHistory();
        return CommandResult.Ok($"Subscribed to alert history on {conn.Name}.");
    }

    private CommandResult HandleHistoryUnsubscribe(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!conn.IsAlertHistorySubscribed)
        {
            return CommandResult.Ok($"Not subscribed to alert history on {conn.Name}.");
        }

        conn.UnsubscribeAlertHistory();
        return CommandResult.Ok($"Unsubscribed from alert history on {conn.Name}.");
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
        return conn;
    }
}
