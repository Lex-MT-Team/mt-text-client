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
            // TPSL bulk operations (loop wrappers around the existing single-item wire methods).
            "cancel-many" => HandleCancelMany(cleanArgs, targetProfile, confirmFlag),
            "split-many"  => HandleSplitMany(cleanArgs, targetProfile, confirmFlag),
            // Panic operations (immediate MARKET close via TPSL mechanism).
            "panic"       => HandlePanic(cleanArgs, targetProfile, confirmFlag),
            "panic-many"  => HandlePanicMany(cleanArgs, targetProfile, confirmFlag),
            _ => CommandResult.Fail($"Unknown subcommand: {subcommand}. Use: list, cancel, subscribe, unsubscribe, join, split, cancel-many, split-many, panic, panic-many")
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

    // ── TPSL bulk operations ─────────────────────────────────────
    //
    // The MTShared wire protocol exposes only single-item Cancel / Split
    // requests. These "many" tools are loop wrappers that emit one wire call
    // per ID and aggregate the results. A single bad ID does not abort the
    // remaining ones — every ID is attempted and reported individually.

    private CommandResult HandleCancelMany(List<string> args, string? targetProfile, bool confirmed)
    {
        if (!confirmed)
        {
            return CommandResult.Fail("tpsl cancel-many requires --confirm. Provide TPSL IDs to cancel.");
        }
        if (args.Count < 2)
        {
            return CommandResult.Fail("Usage: tpsl cancel-many <id1> <id2> [...] --confirm");
        }
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        var rows = new List<object>();
        int ok = 0, fail = 0;
        for (int i = 1; i < args.Count; i++)
        {
            if (!long.TryParse(args[i], out long id))
            {
                rows.Add(new { id = args[i], success = false, message = "invalid id" });
                fail++;
                continue;
            }
            NotificationMessageData? result = conn.CancelTPSL(id);
            bool success = result != null && result.IsOk;
            if (success) ok++; else fail++;
            rows.Add(new
            {
                id,
                success,
                notificationCode = result?.notificationCode.ToString() ?? "TIMEOUT",
                message = result?.msgString ?? "no response"
            });
        }
        return CommandResult.Ok(
            $"[{conn.Name}] TPSL cancel-many: {ok} succeeded, {fail} failed (of {args.Count - 1}).",
            new { Server = conn.Name, Ok = ok, Failed = fail, Results = rows });
    }

    private CommandResult HandleSplitMany(List<string> args, string? targetProfile, bool confirmed)
    {
        if (!confirmed)
        {
            return CommandResult.Fail("tpsl split-many requires --confirm. Provide TPSL IDs to split.");
        }
        if (args.Count < 2)
        {
            return CommandResult.Fail("Usage: tpsl split-many <id1> <id2> [...] --confirm");
        }
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        var rows = new List<object>();
        int ok = 0, fail = 0;
        for (int i = 1; i < args.Count; i++)
        {
            if (!long.TryParse(args[i], out long id))
            {
                rows.Add(new { id = args[i], success = false, message = "invalid id" });
                fail++;
                continue;
            }
            NotificationMessageData? result = conn.SplitTPSL(new TPSLInfoData { id = id });
            bool success = result != null && result.IsOk;
            if (success) ok++; else fail++;
            rows.Add(new
            {
                id,
                success,
                notificationCode = result?.notificationCode.ToString() ?? "TIMEOUT",
                message = result?.msgString ?? "no response"
            });
        }
        return CommandResult.Ok(
            $"[{conn.Name}] TPSL split-many: {ok} succeeded, {fail} failed (of {args.Count - 1}).",
            new { Server = conn.Name, Ok = ok, Failed = fail, Results = rows });
    }

    // ── TPSL panic close ─────────────────────────────────────────
    //
    // "Panic" close = immediate MARKET-order exit of the position underlying
    // the named TPSL. Implementation: look up the TPSL in the store (must be
    // subscribed first), extract its symbol/market/side, build PositionData,
    // and call ClosePositionByTPSL with OrderType.MARKET.

    private CommandResult HandlePanic(List<string> args, string? targetProfile, bool confirmed)
    {
        if (!confirmed)
        {
            return CommandResult.Fail("tpsl panic requires --confirm. This will MARKET-close the position via TPSL.");
        }
        if (args.Count < 2)
        {
            return CommandResult.Fail("Usage: tpsl panic <tpsl_id> --confirm");
        }
        if (!long.TryParse(args[1], out long tpslId))
        {
            return CommandResult.Fail($"Invalid TPSL ID: {args[1]}");
        }
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        var panicResult = PanicSingle(conn, tpslId);
        return panicResult.Success
            ? CommandResult.Ok(
                $"[{conn.Name}] TPSL panic {tpslId}: {panicResult.NotificationCode}",
                new { Server = conn.Name, Id = tpslId, panicResult.NotificationCode, panicResult.Message })
            : CommandResult.Fail(
                $"[{conn.Name}] TPSL panic {tpslId} failed: {panicResult.NotificationCode} — {panicResult.Message}");
    }

    private CommandResult HandlePanicMany(List<string> args, string? targetProfile, bool confirmed)
    {
        if (!confirmed)
        {
            return CommandResult.Fail("tpsl panic-many requires --confirm. Provide TPSL IDs to MARKET-close.");
        }
        if (args.Count < 2)
        {
            return CommandResult.Fail("Usage: tpsl panic-many <id1> <id2> [...] --confirm");
        }
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        var rows = new List<object>();
        int ok = 0, fail = 0;
        for (int i = 1; i < args.Count; i++)
        {
            if (!long.TryParse(args[i], out long id))
            {
                rows.Add(new { id = args[i], success = false, message = "invalid id" });
                fail++;
                continue;
            }
            var r = PanicSingle(conn, id);
            if (r.Success) ok++; else fail++;
            rows.Add(new { id, success = r.Success, r.NotificationCode, r.Message });
        }
        return CommandResult.Ok(
            $"[{conn.Name}] TPSL panic-many: {ok} succeeded, {fail} failed (of {args.Count - 1}).",
            new { Server = conn.Name, Ok = ok, Failed = fail, Results = rows });
    }

    /// <summary>
    /// Single-ID panic helper used by HandlePanic and HandlePanicMany.
    /// Looks up the TPSL snapshot for the position metadata; without an
    /// active TPSL subscription (`tpsl subscribe`) the store is empty and
    /// the lookup fails with a "subscribe first" diagnostic.
    /// </summary>
    private static (bool Success, string NotificationCode, string Message) PanicSingle(CoreConnection conn, long tpslId)
    {
        TPSLPositionSnapshot? snap = conn.TPSLStore?.GetById(tpslId);
        if (snap == null)
        {
            return (false, "NOT_FOUND",
                "TPSL not found in store. Run 'tpsl subscribe' first, then 'tpsl list' to confirm IDs.");
        }
        // TPSLPositionSnapshot.Side is OrderSideType (BUY / SELL); PositionData
        // wants PositionSide (LONG / SHORT / BOTH). Map: BUY-side TPSL belongs
        // to a LONG position; SELL-side to SHORT. BOTH covers the one-way /
        // SPOT case where no separate hedge side is tracked.
        PositionSide mapped = snap.Side switch
        {
            OrderSideType.BUY  => PositionSide.LONG,
            OrderSideType.SELL => PositionSide.SHORT,
            _                  => PositionSide.BOTH,
        };
        var posData = new PositionData
        {
            symbol = snap.Symbol,
            marketType = snap.MarketType,
            positionSide = mapped,
        };
        NotificationMessageData? result = conn.ClosePositionByTPSL(
            conn.Profile.Exchange, posData, OrderType.MARKET);
        if (result == null)
        {
            return (false, "TIMEOUT", "no response from close-position-by-tpsl");
        }
        return (result.IsOk, result.notificationCode.ToString(), result.msgString ?? "");
    }

}
