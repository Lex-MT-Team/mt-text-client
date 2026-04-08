using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using MTShared.Network;
using MTShared.Structs;
using MTShared.Types;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// TPSL commands — view and manage Take Profit / Stop Loss positions.
///
/// Subcommands:
///   tpsl list                  — list all TPSL positions (from subscription)
///   tpsl cancel <id>           — cancel a TPSL position
///   tpsl subscribe             — subscribe to TPSL updates
///   tpsl unsubscribe           — unsubscribe from TPSL updates
///
/// Supports @profile targeting.
/// </summary>
public sealed class TPSLCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "tpsl";
    public string Description => "View and manage Take Profit / Stop Loss positions";
    public string Usage => "tpsl <list|cancel <id>|subscribe|unsubscribe> [@profile]";

    public TPSLCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(
                "Usage: tpsl <subcommand>\n" +
                "  list         — list all TPSL positions\n" +
                "  cancel <id>  — cancel a TPSL position\n" +
                "  subscribe    — subscribe to TPSL updates\n" +
                "  unsubscribe  — unsubscribe from TPSL updates");
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
            "cancel" => HandleCancel(cleanArgs, targetProfile, confirmFlag),
            "subscribe" => HandleSubscribe(targetProfile),
            "unsubscribe" => HandleUnsubscribe(targetProfile),
            "join" => HandleJoin(cleanArgs, targetProfile, confirmFlag),
            "split" => HandleSplit(cleanArgs, targetProfile, confirmFlag),
            _ => CommandResult.Fail($"Unknown subcommand: {subcommand}. Use: list, cancel, subscribe, unsubscribe")
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

        TPSLStore? store = conn.TPSLStore;
        if (store == null || !store.HasData)
        {
            return CommandResult.Ok($"[{conn.Name}] No TPSL data. Use 'tpsl subscribe' first.");
        }

        IReadOnlyList<TPSLPositionSnapshot> positions = store.GetAll();
        var sb = new StringBuilder();
        sb.AppendLine($"[{conn.Name}] TPSL Positions ({positions.Count}):");
        sb.AppendLine();

        for (int i = 0; i < positions.Count; i++)
        {
            TPSLPositionSnapshot pos = positions[i];
            sb.AppendLine($"  [{i}] ID: {pos.Id}");
            sb.AppendLine($"      Symbol: {pos.Symbol} ({pos.MarketType}) {pos.Side}");
            sb.AppendLine($"      Qty: {pos.Qty:F6} @ Entry: {pos.EntryPrice:F4}");
            sb.AppendLine($"      TP: {(pos.TakeProfitEnabled ? $"{pos.TakeProfitPercent:F2}% ({pos.TakeProfitStatus})" : "OFF")}");
            sb.AppendLine($"      SL: {(pos.StopLossEnabled ? $"{pos.StopLossPercent:F2}% ({pos.StopLossStatus})" : "OFF")}");
            if (pos.TrailingEnabled)
            {
                sb.AppendLine($"      Trailing: {pos.TrailingSpread:F2}%");
            }
            sb.AppendLine($"      Running: {pos.IsRunning} | Split: {pos.SplitCount}x{pos.SplitPercentage:F1}%");
            sb.AppendLine();
        }

        return CommandResult.Ok(sb.ToString());
    }

    private CommandResult HandleCancel(List<string> cleanArgs, string? targetProfile, bool confirm)
    {
        if (cleanArgs.Count < 2)
        {
            return CommandResult.Fail("Usage: tpsl cancel <id> --confirm [@profile]");
        }
        if (!confirm)
        {
            return CommandResult.Fail($"Cancel TPSL ID {cleanArgs[1]}? Use --confirm to proceed.");
        }
        if (!long.TryParse(cleanArgs[1], out long tpslId))
        {
            return CommandResult.Fail($"Invalid TPSL ID: {cleanArgs[1]}");
        }

        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        NotificationMessageData? result = conn.CancelTPSL(tpslId);
        if (result == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Cancel TPSL {tpslId} failed or timed out.");
        }

        return CommandResult.Ok($"[{conn.Name}] Cancel TPSL {tpslId}: {result.notificationCode} — {result.msgString}");
    }

    private CommandResult HandleSubscribe(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        bool subscribed = conn.SubscribeTPSL();
        if (!subscribed)
        {
            return CommandResult.Fail($"[{conn.Name}] Failed to subscribe to TPSL updates.");
        }

        return CommandResult.Ok($"[{conn.Name}] Subscribed to TPSL updates. Use 'tpsl list' to view data.");
    }

    private CommandResult HandleUnsubscribe(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        conn.UnsubscribeTPSL();
        return CommandResult.Ok($"[{conn.Name}] Unsubscribed from TPSL updates.");
    }

    private CommandResult HandleJoin(List<string> args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!confirmed)
        {
            return CommandResult.Fail("tpsl join requires --confirm. Provide TPSL IDs to join.");
        }

        if (args.Count < 2)
        {
            return CommandResult.Fail("Usage: tpsl join <id1> <id2> [<id3>...] --confirm");
        }

        List<long> ids = new List<long>();
        for (int i = 1; i < args.Count; i++)
        {
            if (long.TryParse(args[i], out long id))
            {
                ids.Add(id);
            }
        }

        if (ids.Count < 2)
        {
            return CommandResult.Fail("Need at least 2 TPSL IDs to join.");
        }

        TPSLInfoListData tpslData = new TPSLInfoListData();
        tpslData.infoData = ids.Select(id => new TPSLInfoData { id = id }).ToList();

        NotificationMessageData? result = conn.JoinTPSL(tpslData);
        if (result == null)
        {
            return CommandResult.Fail("No response from TPSL join.");
        }

        return result.IsOk
            ? CommandResult.Ok($"[{conn.Name}] TPSL join: {result.notificationCode}")
            : CommandResult.Fail($"[{conn.Name}] TPSL join failed: {result.notificationCode} — {result.jsonData}");
    }

    private CommandResult HandleSplit(List<string> args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!confirmed)
        {
            return CommandResult.Fail("tpsl split requires --confirm. Provide TPSL ID to split.");
        }

        if (args.Count < 2)
        {
            return CommandResult.Fail("Usage: tpsl split <tpsl_id> --confirm");
        }

        if (!long.TryParse(args[1], out long tpslId))
        {
            return CommandResult.Fail($"Invalid TPSL ID: {args[1]}");
        }

        TPSLInfoData tpslData = new TPSLInfoData();
        tpslData.id = tpslId;

        NotificationMessageData? result = conn.SplitTPSL(tpslData);
        if (result == null)
        {
            return CommandResult.Fail("No response from TPSL split.");
        }

        return result.IsOk
            ? CommandResult.Ok($"[{conn.Name}] TPSL split: {result.notificationCode}")
            : CommandResult.Fail($"[{conn.Name}] TPSL split failed: {result.notificationCode} — {result.jsonData}");
    }

}
