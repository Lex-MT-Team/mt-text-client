using System;
using System.Collections.Generic;
using System.Text;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// Notifications commands — subscribe to real-time notifications from Core.
///
/// Subcommands:
///   notifications list [--count N]      — show cached notifications
///   notifications subscribe             — start receiving notifications
///   notifications unsubscribe           — stop receiving notifications
///   notifications clear                 — clear cached notifications
///
/// Supports @profile targeting.
/// </summary>
public sealed class NotificationsCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "notifications";
    public string Description => "Subscribe to and view real-time notifications from Core";
    public string Usage => "notifications <list|subscribe|unsubscribe|clear> [--count N] [@profile]";

    public NotificationsCommand(ConnectionManager manager)
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
                return HandleList(cleanArgs.ToArray(), targetProfile);
            case "subscribe":
                return HandleSubscribe(targetProfile);
            case "unsubscribe":
                return HandleUnsubscribe(targetProfile);
            case "clear":
                return HandleClear(targetProfile);
            default:
                return CommandResult.Fail("Unknown subcommand. Use: list, subscribe, unsubscribe, clear");
        }
    }

    private CommandResult HandleList(string[] parts, string? targetProfile)
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

        List<NotificationEntry> entries = conn.NotificationStore.GetRecent(count);
        if (entries.Count == 0)
        {
            return CommandResult.Ok($"No notifications cached for {conn.Name}. Subscribe first with 'notifications subscribe'.");
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"## Notifications — {conn.Name} (last {entries.Count})");
        sb.AppendLine();
        sb.AppendLine("| Time (UTC) | Type | Message |");
        sb.AppendLine("|------------|------|---------|");

        for (int i = entries.Count - 1; i >= 0; i--)
        {
            NotificationEntry entry = entries[i];
            string time = entry.ReceivedAtUtc.ToString("HH:mm:ss.fff");
            string msg = entry.Message.Length > 80 ? entry.Message.Substring(0, 80) + "..." : entry.Message;
            msg = msg.Replace("|", "\\|").Replace("\n", " ");
            sb.AppendLine($"| {time} | {entry.NotificationType} | {msg} |");
        }

        sb.AppendLine();
        sb.AppendLine($"Subscribed: {conn.IsNotificationSubscribed}");

        return CommandResult.Ok(sb.ToString(), new { count = entries.Count });
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

        if (conn.IsNotificationSubscribed)
        {
            return CommandResult.Ok($"Already subscribed to notifications on {conn.Name}.");
        }

        conn.SubscribeNotifications();
        return CommandResult.Ok($"Subscribed to notifications on {conn.Name}.");
    }

    private CommandResult HandleUnsubscribe(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!conn.IsNotificationSubscribed)
        {
            return CommandResult.Ok($"Not subscribed to notifications on {conn.Name}.");
        }

        conn.UnsubscribeNotifications();
        return CommandResult.Ok($"Unsubscribed from notifications on {conn.Name}.");
    }

    private CommandResult HandleClear(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        int count = conn.NotificationStore.Count;
        conn.NotificationStore.Clear();
        return CommandResult.Ok($"Cleared {count} notifications from {conn.Name}.");
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
