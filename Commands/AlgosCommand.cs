using System.Threading;
using System;
using System.Collections.Generic;
using MTShared;
using MTShared.Network;
using MTTextClient.Core;
using Newtonsoft.Json.Linq;
namespace MTTextClient.Commands;

/// <summary>
/// Algorithm management commands with multi-server support.
/// Phase B: Full algorithm lifecycle — list, get, start, stop, save, delete, clone,
/// config view/edit, group management, toggle debug, rename.
/// 
/// All commands operate on the active connection unless @profile is specified.
/// 
/// algos list             — list algos on active connection
/// algos list @bnc_001    — list algos on bnc_001
/// algos list-all         — list algos across ALL connections
/// algos search <query>   — search algos by name/signature/symbol
/// algos get <id>         — get algo detail
/// algos start <id>       — start algo
/// algos stop <id>        — stop algo
/// algos stop-all         — stop all algos
/// algos start-all        — start all algos
/// algos save <id>        — save algo config changes to Core
/// algos save-start <id>  — save and immediately start
/// algos delete <id>      — delete algo (requires confirmation flag)
/// algos toggle-debug <id>— toggle debug mode
/// algos rename <id> <name> — rename algorithm
/// algos config <id>      — view algo configuration (parsed argsJson)
/// algos config <id> set <key> <value> — set a config parameter
/// algos groups           — list algorithm groups/folders
/// algos group <groupId>  — list algos in a group
/// algos clone-group <groupId> — clone an entire group
/// algos delete-group <groupId> — delete an entire group (requires --confirm)
/// algos copy <id> @destination  — copy algo from active to destination server (--confirm)
/// algos export <id>             — export algo as portable JSON
///
/// FIX HISTORY:
///   - BUG-8: Rename uses description field, preserves type name in name field.
///   - BUG-9: START_ALL/STOP_ALL uses AlgorithmData, not AlgorithmListData.
///   - BUG-16: DELETE_GROUP uses AlgorithmData (single request), not AlgorithmListData.
///             Core handles DELETE_GROUP in AlgorithmRequest handler, not AlgorithmListRequest.
/// </summary>
/// <summary>
/// Maps algorithm signature codes to their Core-internal type names.
/// Core uses AlgorithmData.name as the type identifier in a switch statement
/// to instantiate the correct algorithm class. If name doesn't match exactly,
/// the algo silently fails to start (null algorithm, no error).
/// </summary>
internal static class AlgoTypeNames
{
    private static readonly Dictionary<string, string> SignatureToName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SA"] = "Shot",
        ["SG"] = "Shots Group",
        ["DS"] = "Depth Shot",
        ["DSG"] = "Depth Shots Group",
        ["MW"] = "Markets Watcher",
        ["AV"] = "Averages",
        ["AG"] = "Averages Group",
        ["VE"] = "Vector",
        ["VG"] = "Vector Group",
        ["SI"] = "Signal",
        ["MS"] = "Markets Saver",
    };

    /// <summary>
    /// Resolves the Core-internal algorithm type name from a signature code.
    /// Returns null if the signature is unknown.
    /// </summary>
    public static string? Resolve(string? signature)
        => signature != null && SignatureToName.TryGetValue(signature, out string? name) ? name : null;
}

public sealed class AlgosCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public AlgosCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public string Name => "algos";
    public string Description => "Full algorithm lifecycle management (supports @profile targeting)";
    public string Usage => @"algos list|list-all|search|get|start|stop|start-all|stop-all|
  save|save-start|delete|toggle-debug|rename|config|groups|group|clone-group|delete-group|
  copy|export";

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(Usage);
        }

        // Parse out @profile suffix and --confirm flag from any position
        string? targetProfile = null;
        bool confirmFlag = false;
        var cleanArgs = new List<string>();
        foreach (string arg in args)
        {
            if (arg.StartsWith('@'))
            {
                targetProfile = arg[1..];
            }
            else if (arg.Equals("--confirm", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-y", StringComparison.OrdinalIgnoreCase))
            {
                confirmFlag = true;
            }
            else
            {
                cleanArgs.Add(arg);
            }
        }

        string? subCmd = cleanArgs[0].ToLowerInvariant();
        string[]? subArgs = cleanArgs.Count > 1 ? cleanArgs.GetRange(1, cleanArgs.Count - 1).ToArray() : Array.Empty<string>();

        return subCmd switch
        {
            "list" or "ls" => ListAlgos(targetProfile),
            "list-all" or "ls-all" => ListAllAlgos(),
            "search" => SearchAlgos(subArgs, targetProfile),
            "get" => GetAlgo(subArgs, targetProfile),
            "start" => AlgoAction(subArgs, AlgorithmData.ActionType.START, targetProfile),
            "stop" => AlgoAction(subArgs, AlgorithmData.ActionType.STOP, targetProfile),
            "start-all" => AlgoAction(Array.Empty<string>(), AlgorithmData.ActionType.START_ALL, targetProfile),
            "stop-all" => AlgoAction(Array.Empty<string>(), AlgorithmData.ActionType.STOP_ALL, targetProfile),
            "save" => SaveAlgo(subArgs, AlgorithmData.ActionType.SAVE, targetProfile),
            "save-start" => SaveAlgo(subArgs, AlgorithmData.ActionType.SAVE_START, targetProfile),
            "delete" => DeleteAlgo(subArgs, targetProfile, confirmFlag),
            "toggle-debug" => ToggleDebug(subArgs, targetProfile),
            "rename" => RenameAlgo(subArgs, targetProfile),
            "config" => HandleConfig(subArgs, targetProfile),
            "groups" => ListGroups(targetProfile),
            "group" => ListGroupAlgos(subArgs, targetProfile),
            "clone-group" => CloneGroup(subArgs, targetProfile),
            "delete-group" => DeleteGroup(subArgs, targetProfile, confirmFlag),
            "copy" or "cp" => CopyAlgo(subArgs, targetProfile, confirmFlag),
            "export" => ExportAlgo(subArgs, targetProfile),
            "start-verify" or "sv" => StartAndVerify(subArgs, targetProfile),
            "verify" => VerifyAlgo(subArgs, targetProfile),
            _ => CommandResult.Fail($"Unknown subcommand: {subCmd}. {Usage}")
        };
    }

    #region Connection Resolution

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

    #endregion

    #region List / Search / Get

    private CommandResult ListAlgos(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        IReadOnlyList<AlgorithmData>? algos = conn.AlgoStore.GetAll();
        if (algos.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] No algorithms loaded yet.");
        }

        (int running, int stopped, int processing) = conn.AlgoStore.GetCounts();
        var data = new List<object>(algos.Count);
        foreach (AlgorithmData a in algos)
        {
            data.Add(new
            {
                a.id,
                name = string.IsNullOrEmpty(a.description) ? a.name : a.description,
                a.signature,
                Running = a.isRunning ? "YES" : "no",
                isRunning = a.isRunning,
                Processing = a.isProcessing ? "YES" : "no",
                Market = a.marketType.ToString(),
                a.symbol,
                GroupType = a.groupType.ToString(),
                Group = a.groupID
            });
        }

        return CommandResult.Ok(
            $"[{conn.Name}] {algos.Count} algorithm(s) — {running} running, {stopped} stopped, {processing} processing.",
            data);
    }

    /// <summary>List algos across ALL connected servers.</summary>
    private CommandResult ListAllAlgos()
    {
        IReadOnlyList<CoreConnection>? allConns = _manager.GetAll();
        var connections = new List<CoreConnection>();
        foreach (CoreConnection c in allConns)
        {
            if (c.IsConnected)
            {
                connections.Add(c);
            }
        }

        if (connections.Count == 0)
        {
            return CommandResult.Fail("No active connections.");
        }

        var allData = new List<object>();
        int totalAlgos = 0, totalRunning = 0;

        foreach (CoreConnection conn in connections)
        {
            IReadOnlyList<AlgorithmData> algos = conn.AlgoStore.GetAll();
            totalAlgos += algos.Count;
            foreach (AlgorithmData a2 in algos)
            {
                if (a2.isRunning)
                {
                    totalRunning++;
                }
            }

            foreach (AlgorithmData a in algos)
            {
                allData.Add(new
                {
                    Server = conn.Name,
                    a.id,
                    a.name,
                    Running = a.isRunning ? "YES" : "no",
                    isRunning = a.isRunning,
                    Market = a.marketType.ToString(),
                    a.symbol,
                    GroupType = a.groupType.ToString()
                });
            }
        }

        return CommandResult.Ok(
            $"All servers: {totalAlgos} algorithm(s), {totalRunning} running across {connections.Count} connection(s).",
            allData);
    }

    private CommandResult SearchAlgos(string[] args, string? targetProfile)
    {
        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: algos search <query>");
        }

        string? query = string.Join(" ", args);
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        IReadOnlyList<AlgorithmData>? results = conn.AlgoStore.Search(query);
        if (results.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] No algorithms matching '{query}'.");
        }

        var data = new List<object>(results.Count);
        foreach (AlgorithmData a in results)
        {
            data.Add(new
            {
                a.id,
                a.name,
                a.signature,
                Running = a.isRunning ? "YES" : "no",
                a.symbol,
                isRunning = a.isRunning,
                GroupType = a.groupType.ToString()
            });
        }

        return CommandResult.Ok($"[{conn.Name}] {results.Count} algorithm(s) matching '{query}'.", data);
    }

    private CommandResult GetAlgo(string[] args, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1 || !long.TryParse(args[0], out long id))
        {
            return CommandResult.Fail("Usage: algos get <id>");
        }

        AlgorithmData? algo = conn.AlgoStore.FindById(id);
        if (algo == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Algorithm {id} not found.");
        }

        // Find group name
        AlgorithmGroupData? group = conn.AlgoStore.FindGroupById(algo.groupID);
        string? groupName = group?.name ?? $"(group {algo.groupID})";

        var data = new
        {
            Server = conn.Name,
            algo.id,
            algo.name,
            algo.signature,
            algo.description,
            Running = algo.isRunning,
            Processing = algo.isProcessing,
            Profiling = algo.isProfilingOn,
            Market = algo.marketType.ToString(),
            algo.symbol,
            GroupId = algo.groupID,
            GroupName = groupName,
            GroupType = algo.groupType.ToString(),
            IsClone = algo.isClone,
            IsTradingAlgo = algo.isTradingAlgo,
            Version = algo.version,
            Created = SafeFromUnixMs(algo.created),
            Updated = SafeFromUnixMs(algo.updated),
            ArgsJsonLength = algo.argsJson?.Length ?? 0
        };

        return CommandResult.Ok($"[{conn.Name}] Algorithm {id}: {algo.name}", data);
    }

    #endregion

    #region Start / Stop

    private CommandResult AlgoAction(string[] args, AlgorithmData.ActionType actionType, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (conn.Client == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Client not available.");
        }

        // Individual start/stop
        if (actionType == AlgorithmData.ActionType.START || actionType == AlgorithmData.ActionType.STOP)
        {
            if (args.Length < 1 || !long.TryParse(args[0], out long id))
            {
                return CommandResult.Fail($"Usage: algos {actionType.ToString().ToLower()} <id>");
            }

            AlgorithmData? algo = conn.AlgoStore.FindById(id);
            if (algo == null)
            {
                return CommandResult.Fail($"[{conn.Name}] Algorithm {id} not found.");
            }

            var request = new AlgorithmData(algo) { actionType = actionType };

            // BUG FIX: Core uses AlgorithmData.name in a switch to instantiate the correct
            // algorithm class (e.g. "Shot", "Shots Group"). After rename, name may be a
            // user display name which doesn't match any case → algo silently fails to start.
            // Always resolve the proper type name from the signature for START operations.
            if (actionType == AlgorithmData.ActionType.START)
            {
                string? typeName = AlgoTypeNames.Resolve(algo.signature);
                if (typeName != null)
                {
                    request.name = typeName;
                }
            }

            NotificationMessageData? notification = conn.SendAlgorithmRequest(request);

            if (notification == null)
            {
                return CommandResult.Ok($"[{conn.Name}] Algorithm {id}: {actionType} sent (response timed out).");
            }

            return notification.IsOk
                ? CommandResult.Ok($"[{conn.Name}] Algorithm {id} ({algo.name}): {actionType} ✓",
                    new { Server = conn.Name, id, algo.name, Action = actionType.ToString(), notification.msgString })
                : CommandResult.Fail($"[{conn.Name}] Algorithm {id} ({algo.name}): {actionType} FAILED — {notification.notificationCode}: {notification.msgString}");
        }
        else
        {
            // START_ALL / STOP_ALL — send via AlgorithmData (type 111), not AlgorithmListData (type 112).
            // Core handles START_ALL/STOP_ALL in the AlgorithmRequest handler, not AlgorithmListRequest.
            var request = new AlgorithmData { actionType = actionType };
            NotificationMessageData? notification = conn.SendAlgorithmRequest(request);

            if (notification == null)
            {
                return CommandResult.Ok($"[{conn.Name}] {actionType} sent (response timed out).");
            }

            return notification.IsOk
                ? CommandResult.Ok($"[{conn.Name}] {actionType} ✓",
                    new { Server = conn.Name, Action = actionType.ToString(), notification.msgString })
                : CommandResult.Fail($"[{conn.Name}] {actionType} FAILED — {notification.notificationCode}: {notification.msgString}");
        }
    }

    #endregion

    #region Save / Save-Start

    private CommandResult SaveAlgo(string[] args, AlgorithmData.ActionType saveType, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1 || !long.TryParse(args[0], out long id))
        {
            return CommandResult.Fail($"Usage: algos {(saveType == AlgorithmData.ActionType.SAVE_START ? "save-start" : "save")} <id>");
        }

        AlgorithmData? algo = conn.AlgoStore.FindById(id);
        if (algo == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Algorithm {id} not found.");
        }

        var request = new AlgorithmData(algo) { actionType = saveType };

        // BUG FIX: Ensure proper algorithm type name for SAVE_START.
        // Core's SaveAlgorithm uses config.name to start the algo after saving.
        if (saveType == AlgorithmData.ActionType.SAVE_START)
        {
            string? typeName = AlgoTypeNames.Resolve(algo.signature);
            if (typeName != null)
            {
                request.name = typeName;
            }
        }

        NotificationMessageData? notification = conn.SendAlgorithmRequest(request);

        string? actionName = saveType == AlgorithmData.ActionType.SAVE_START ? "SAVE+START" : "SAVE";

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Algorithm {id}: {actionName} sent (response timed out).");
        }

        return notification.IsOk
            ? CommandResult.Ok($"[{conn.Name}] Algorithm {id} ({algo.name}): {actionName} ✓",
                new { Server = conn.Name, id, algo.name, Action = actionName, notification.msgString })
            : CommandResult.Fail($"[{conn.Name}] Algorithm {id} ({algo.name}): {actionName} FAILED — {notification.notificationCode}: {notification.msgString}");
    }

    #endregion

    #region Delete (with confirmation gate)

    private CommandResult DeleteAlgo(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1 || !long.TryParse(args[0], out long id))
        {
            return CommandResult.Fail("Usage: algos delete <id> --confirm");
        }

        AlgorithmData? algo = conn.AlgoStore.FindById(id);
        if (algo == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Algorithm {id} not found.");
        }

        // Safety gate: require explicit confirmation for destructive operations
        if (!confirmed)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] ⚠ DELETE algorithm {id} ({algo.name}) on {conn.Name}?\n" +
                $"  This is IRREVERSIBLE. Re-run with --confirm flag:\n" +
                $"  algos delete {id} --confirm");
        }

        if (algo.isRunning)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] Algorithm {id} ({algo.name}) is RUNNING. Stop it first:\n" +
                $"  algos stop {id}");
        }

        var request = new AlgorithmData(algo) { actionType = AlgorithmData.ActionType.DELETE };
        NotificationMessageData? notification = conn.SendAlgorithmRequest(request);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Algorithm {id}: DELETE sent (response timed out).");
        }

        return notification.IsOk
            ? CommandResult.Ok($"[{conn.Name}] Algorithm {id} ({algo.name}): DELETED ✓",
                new { Server = conn.Name, id, algo.name, Action = "DELETE" })
            : CommandResult.Fail($"[{conn.Name}] Algorithm {id} ({algo.name}): DELETE FAILED — {notification.notificationCode}: {notification.msgString}");
    }

    #endregion

    #region Toggle Debug

    private CommandResult ToggleDebug(string[] args, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1 || !long.TryParse(args[0], out long id))
        {
            return CommandResult.Fail("Usage: algos toggle-debug <id>");
        }

        AlgorithmData? algo = conn.AlgoStore.FindById(id);
        if (algo == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Algorithm {id} not found.");
        }

        var request = new AlgorithmData(algo) { actionType = AlgorithmData.ActionType.TOGGLE_DEBUG };
        NotificationMessageData? notification = conn.SendAlgorithmRequest(request);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Algorithm {id}: TOGGLE_DEBUG sent (response timed out).");
        }

        return notification.IsOk
            ? CommandResult.Ok($"[{conn.Name}] Algorithm {id} ({algo.name}): debug toggled ✓",
                new { Server = conn.Name, id, algo.name, WasProfiling = algo.isProfilingOn })
            : CommandResult.Fail($"[{conn.Name}] Algorithm {id}: TOGGLE_DEBUG FAILED — {notification.notificationCode}: {notification.msgString}");
    }

    #endregion

    #region Rename

    private CommandResult RenameAlgo(string[] args, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 2 || !long.TryParse(args[0], out long id))
        {
            return CommandResult.Fail("Usage: algos rename <id> <new-name>");
        }

        AlgorithmData? algo = conn.AlgoStore.FindById(id);
        if (algo == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Algorithm {id} not found.");
        }

        string? newName = string.Join(" ", args, 1, args.Length - 1);
        string? oldName = algo.description ?? algo.name;

        // BUG FIX: Core uses AlgorithmData.name as the algorithm type identifier
        // (e.g. "Shot", "Shots Group") in a switch statement. Overwriting it with a
        // user display name causes the algo to silently fail to start.
        // Store the display name in the description field instead.
        var request = new AlgorithmData(algo)
        {
            description = newName,
            actionType = AlgorithmData.ActionType.SAVE
        };

        // Ensure the type name is correct (in case it was previously corrupted by rename)
        string? typeName = AlgoTypeNames.Resolve(algo.signature);
        if (typeName != null)
        {
            request.name = typeName;
        }

        NotificationMessageData? notification = conn.SendAlgorithmRequest(request);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Algorithm {id}: rename sent (response timed out).");
        }

        return notification.IsOk
            ? CommandResult.Ok($"[{conn.Name}] Algorithm {id}: renamed '{oldName}' → '{newName}' ✓",
                new { Server = conn.Name, id, OldName = oldName, NewName = newName })
            : CommandResult.Fail($"[{conn.Name}] Algorithm {id}: rename FAILED — {notification.notificationCode}: {notification.msgString}");
    }

    #endregion

    #region Config View / Edit

    private CommandResult HandleConfig(string[] args, string? targetProfile)
    {
        if (args.Length < 1 || !long.TryParse(args[0], out long id))
        {
            return CommandResult.Fail("Usage: algos config <id> [set <key> <value>]");
        }

        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        AlgorithmData? algo = conn.AlgoStore.FindById(id);
        if (algo == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Algorithm {id} not found.");
        }

        // algos config <id> set <key> <value>
        if (args.Length >= 4 && args[1].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            string? paramKey = args[2];
            string? paramValue = string.Join(" ", args, 3, args.Length - 3);
            return SetConfigParam(conn, algo, paramKey, paramValue);
        }

        // algos config <id> — view config
        return ViewConfig(conn, algo);
    }

    private CommandResult ViewConfig(CoreConnection conn, AlgorithmData algo)
    {
        AlgorithmConfig? config = AlgorithmStore.ParseConfig(algo);
        if (config == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Algorithm {algo.id}: no configuration data (argsJson empty or unparseable).");
        }

        var data = new List<object>(config.Parameters.Count);
        foreach (AlgorithmParameter p in config.Parameters)
        {
            data.Add(new
            {
                p.Key,
                p.Label,
                Value = p.DisplayValue,
                Type = p.ValueType,
                p.Unit,
                p.Group,
                Positive = p.UseOnlyPositiveValue ? "yes" : ""
            });
        }

        return CommandResult.Ok(
            $"[{conn.Name}] Algorithm {algo.id} ({algo.name}) — {config.Signature}: {config.Parameters.Count} parameter(s).",
            data);
    }

    private CommandResult SetConfigParam(CoreConnection conn, AlgorithmData algo, string paramKey, string paramValue)
    {
        (bool success, string? error) = AlgorithmStore.UpdateParameter(algo, paramKey, paramValue);
        if (!success)
        {
            return CommandResult.Fail($"[{conn.Name}] {error}");
        }

        return CommandResult.Ok(
            $"[{conn.Name}] Algorithm {algo.id} ({algo.name}): parameter '{paramKey}' set to '{paramValue}'.\n" +
            $"  Changes are LOCAL only. Use 'algos save {algo.id}' to persist to Core.",
            new { Server = conn.Name, algo.id, algo.name, ParameterKey = paramKey, NewValue = paramValue, Persisted = false });
    }

    #endregion

    #region Group Management

    private CommandResult ListGroups(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        IReadOnlyList<AlgorithmGroupData>? groups = conn.AlgoStore.GetAllGroups();
        if (groups.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] No algorithm groups loaded.");
        }

        var data = new List<object>(groups.Count);
        foreach (AlgorithmGroupData g in groups)
        {
            IReadOnlyList<AlgorithmData> algosInGroup = conn.AlgoStore.GetByGroup(g.id);
            int running = 0;
            foreach (AlgorithmData a in algosInGroup)
            {
                if (a.isRunning)
                {
                    running++;
                }
            }

            data.Add(new
            {
                g.id,
                g.name,
                GroupType = g.groupType.ToString(),
                AlgoCount = algosInGroup.Count,
                Running = running,
                Stopped = algosInGroup.Count - running
            });
        }

        return CommandResult.Ok($"[{conn.Name}] {groups.Count} group(s).", data);
    }

    private CommandResult ListGroupAlgos(string[] args, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1 || !long.TryParse(args[0], out long groupId))
        {
            return CommandResult.Fail("Usage: algos group <groupId>");
        }

        AlgorithmGroupData? group = conn.AlgoStore.FindGroupById(groupId);
        string? groupName = group?.name ?? $"(group {groupId})";

        IReadOnlyList<AlgorithmData>? algos = conn.AlgoStore.GetByGroup(groupId);
        if (algos.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] Group '{groupName}' ({groupId}): no algorithms.");
        }

        int running = 0;
        foreach (AlgorithmData a in algos)
        {
            if (a.isRunning)
            {
                running++;
            }
        }

        var data = new List<object>(algos.Count);
        foreach (AlgorithmData a in algos)
        {
            data.Add(new
            {
                a.id,
                a.name,
                a.signature,
                Running = a.isRunning ? "YES" : "no",
                Market = a.marketType.ToString(),
                isRunning = a.isRunning,
                a.symbol
            });
        }

        return CommandResult.Ok(
            $"[{conn.Name}] Group '{groupName}' ({groupId}): {algos.Count} algo(s), {running} running.",
            data);
    }

    private CommandResult CloneGroup(string[] args, string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1 || !long.TryParse(args[0], out long groupId))
        {
            return CommandResult.Fail("Usage: algos clone-group <groupId>");
        }

        AlgorithmGroupData? group = conn.AlgoStore.FindGroupById(groupId);
        if (group == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Group {groupId} not found.");
        }

        var cloneGroup = new AlgorithmGroupData(group)
        {
            actionType = AlgorithmData.ActionType.CLONE_GROUP
        };

        var listData = new AlgorithmListData
        {
            actionType = AlgorithmData.ActionType.CLONE_GROUP
        };
        listData.groups.Add(cloneGroup);

        NotificationMessageData? notification = conn.SendAlgorithmListRequest(listData);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Group '{group.name}' ({groupId}): CLONE sent (response timed out).");
        }

        return notification.IsOk
            ? CommandResult.Ok($"[{conn.Name}] Group '{group.name}' ({groupId}): CLONED ✓",
                new { Server = conn.Name, GroupId = groupId, GroupName = group.name, Action = "CLONE_GROUP" })
            : CommandResult.Fail($"[{conn.Name}] Group '{group.name}' ({groupId}): CLONE FAILED — {notification.notificationCode}: {notification.msgString}");
    }

    /// <summary>
    /// FIX BUG-16: DELETE_GROUP must use AlgorithmData (single request), NOT AlgorithmListData.
    /// Core's CoreMessagesProcessor routes:
    ///   - AlgorithmRequest (type 111) → handles DELETE_GROUP at line 1023
    ///   - AlgorithmListRequest (type 112) → does NOT handle DELETE_GROUP (no case for it)
    /// Previously we sent via SendAlgorithmListRequestAsync which hit AlgorithmListRequest,
    /// where DELETE_GROUP fell through to default (no-op) returning success without deleting.
    /// </summary>
    private CommandResult DeleteGroup(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1 || !long.TryParse(args[0], out long groupId))
        {
            return CommandResult.Fail("Usage: algos delete-group <groupId> --confirm");
        }

        AlgorithmGroupData? group = conn.AlgoStore.FindGroupById(groupId);
        if (group == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Group {groupId} not found.");
        }

        IReadOnlyList<AlgorithmData>? algosInGroup = conn.AlgoStore.GetByGroup(groupId);
        int runningCount = 0;
        foreach (AlgorithmData a in algosInGroup)
        {
            if (a.isRunning)
            {
                runningCount++;
            }
        }

        // Safety gate
        if (!confirmed)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] ⚠ DELETE group '{group.name}' ({groupId}) containing {algosInGroup.Count} algorithm(s)?\n" +
                (runningCount > 0 ? $"  WARNING: {runningCount} algorithm(s) are currently RUNNING!\n" : "") +
                $"  This is IRREVERSIBLE. Re-run with --confirm flag:\n" +
                $"  algos delete-group {groupId} --confirm");
        }

        // BUG-16 FIX: Send as AlgorithmData via AlgorithmRequest handler.
        // Core's AlgorithmRequest handler routes DELETE_GROUP to AlgorithmManager.DeleteGroup(AlgorithmData),
        // which accesses requestData.groupID to find and remove the folder.
        var request = new AlgorithmData
        {
            groupID = groupId,
            actionType = AlgorithmData.ActionType.DELETE_GROUP
        };

        NotificationMessageData? notification = conn.SendAlgorithmRequest(request);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Group '{group.name}' ({groupId}): DELETE sent (response timed out).");
        }

        return notification.IsOk
            ? CommandResult.Ok($"[{conn.Name}] Group '{group.name}' ({groupId}): DELETED ✓ ({algosInGroup.Count} algos removed)",
                new { Server = conn.Name, GroupId = groupId, GroupName = group.name, AlgosRemoved = algosInGroup.Count })
            : CommandResult.Fail($"[{conn.Name}] Group '{group.name}' ({groupId}): DELETE FAILED — {notification.notificationCode}: {notification.msgString}");
    }

    #endregion

    #region Utilities

    private static string SafeFromUnixMs(long unixMs)
    {
        try
        {
            if (unixMs <= 0 || unixMs > 253402300799000L)
            {
                return "(not set)";
            }

            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch
        {
            return "(invalid)";
        }
    }

    #endregion

    #region Cross-Server Copy / Export

    /// <summary>
    /// Export an algorithm as portable JSON that can be imported on another server.
    /// Usage: algos export &lt;id&gt; [@source]
    /// </summary>
    private CommandResult ExportAlgo(string[] args, string? sourceProfile)
    {
        CoreConnection? conn = ResolveConnection(sourceProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (args.Length < 1 || !long.TryParse(args[0], out long id))
        {
            return CommandResult.Fail("Usage: algos export <id>");
        }

        AlgorithmData? algo = conn.AlgoStore.FindById(id);
        if (algo == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Algorithm {id} not found.");
        }

        // Build portable export object
        AlgorithmConfig? config = AlgorithmStore.ParseConfig(algo);
        var exportData = new
        {
            algo.name,
            algo.signature,
            algo.description,
            symbol = algo.symbol,
            market = algo.marketType.ToString(),
            marketTypeInt = (int)algo.marketType,
            groupType = algo.groupType.ToString(),
            groupTypeInt = (int)algo.groupType,
            algo.isTradingAlgo,
            algo.version,
            algo.argsJson,
            parameters = FormatExportParams(config),
            exportedFrom = conn.Name,
            exportedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC")
        };

        return CommandResult.Ok(
            $"[{conn.Name}] Algorithm {id} ({algo.name}) exported. Use the data field for cross-server import.",
            exportData);
    }

    /// <summary>
    /// Copy an algorithm from one server to another.
    /// The source is determined by @profile (or active connection).
    /// The destination is the LAST @-prefixed arg found in subArgs (after the id).
    /// 
    /// Usage patterns:
    ///   algos copy &lt;id&gt; @destination --confirm           (source = active)
    ///   algos copy &lt;id&gt; @source @destination --confirm   (explicit source)
    ///   
    /// In MCP mode, source_profile and destination_profile are explicit parameters.
    /// </summary>
    private CommandResult CopyAlgo(string[] args, string? sourceProfile, bool confirmed)
    {
        // Parse: we need an ID and a to:destination from args.
        // @profile (sourceProfile) was already extracted by ExecuteAsync as the source server.
        // Destination uses "to:profileName" syntax to avoid conflict with @ parsing.
        // In MCP mode: "algos copy 123 to:profile_B @profile_A --confirm"
        // In REPL mode: "algos copy 123 @destination --confirm" (source = active)
        string? destinationProfile = null;
        var cleanedArgs = new List<string>();

        foreach (string arg in args)
        {
            if (arg.StartsWith("to:", StringComparison.OrdinalIgnoreCase))
            {
                destinationProfile = arg[3..];
            }
            else if (arg.StartsWith('@'))
            {
                destinationProfile = arg[1..];  // REPL-mode: @dest also works
            }
            else
            {
                cleanedArgs.Add(arg);
            }
        }

        if (cleanedArgs.Count < 1 || !long.TryParse(cleanedArgs[0], out long algoId))
        {
            return CommandResult.Fail(
                "Usage: algos copy <id> @destination [--confirm]\n" +
                "  Copies from active server (or @source if specified before subcommand) to @destination.\n" +
                "  Example: algos copy 123456 @profile_A --confirm");
        }

        if (destinationProfile == null)
        {
            return CommandResult.Fail("Missing destination. Usage: algos copy <id> @destination --confirm");
        }

        // Source connection
        CoreConnection? sourceConn = ResolveConnection(sourceProfile, out CommandResult? srcError);
        if (sourceConn == null)
        {
            return srcError!;
        }

        // Destination connection
        CoreConnection? destConn = _manager.Get(destinationProfile);
        if (destConn == null)
        {
            return CommandResult.Fail($"Destination '{destinationProfile}' not found. Use 'status' to see connections.");
        }

        if (!destConn.IsConnected)
        {
            return CommandResult.Fail($"Destination '{destinationProfile}' is not connected.");
        }

        if (sourceConn.Name == destConn.Name)
        {
            return CommandResult.Fail("Source and destination are the same server. Use 'algos clone-group' for same-server cloning.");
        }

        // Find algo on source
        AlgorithmData? algo = sourceConn.AlgoStore.FindById(algoId);
        if (algo == null)
        {
            return CommandResult.Fail($"[{sourceConn.Name}] Algorithm {algoId} not found.");
        }

        if (algo.isRunning)
        {
            return CommandResult.Fail($"[{sourceConn.Name}] Algorithm {algoId} ({algo.name}) is running. Stop it first.");
        }

        // Preview
        if (!confirmed)
        {
            AlgorithmConfig? config = AlgorithmStore.ParseConfig(algo);
            int paramCount = config?.Parameters?.Count ?? 0;

            return CommandResult.Ok(
                $"COPY PREVIEW — DRY RUN\n" +
                $"  Source:      {sourceConn.Name} ({sourceConn.Profile.Exchange})\n" +
                $"  Destination: {destConn.Name} ({destConn.Profile.Exchange})\n" +
                $"  Algorithm:   {algo.name} ({algo.signature})\n" +
                $"  Symbol:      {algo.symbol} ({algo.marketType})\n" +
                $"  Parameters:  {paramCount}\n" +
                $"  ArgsJson:    {algo.argsJson?.Length ?? 0} chars\n\n" +
                $"Add --confirm to execute: algos copy {algoId} @{destinationProfile} --confirm",
                new
                {
                    Source = sourceConn.Name,
                    Destination = destConn.Name,
                    algo.name,
                    algo.signature,
                    algo.symbol,
                    Market = algo.marketType.ToString(),
                    ParameterCount = paramCount,
                    DryRun = true
                });
        }

        // Create copy for destination
        var newAlgo = new AlgorithmData(algo)
        {
            id = -1,  // Signal new algorithm
            actionType = AlgorithmData.ActionType.SAVE,
            isRunning = false,
            isProcessing = false,
            groupID = 0  // No group on destination (different server)
        };

        NotificationMessageData? notification = destConn.SendAlgorithmRequest(newAlgo);

        if (notification == null)
        {
            return CommandResult.Ok(
                $"[{sourceConn.Name} → {destConn.Name}] Algorithm '{algo.name}' copy sent (response timed out).",
                new { Source = sourceConn.Name, Destination = destConn.Name, algo.name, algo.signature, TimedOut = true });
        }

        return notification.IsOk
            ? CommandResult.Ok(
                $"[{sourceConn.Name} → {destConn.Name}] Algorithm '{algo.name}' ({algo.signature}) COPIED ✓",
                new { Source = sourceConn.Name, Destination = destConn.Name, algo.name, algo.signature, Success = true })
            : CommandResult.Fail(
                $"[{sourceConn.Name} → {destConn.Name}] Copy FAILED — {notification.notificationCode}: {notification.msgString}");
    }

    #endregion

    private static List<object>? FormatExportParams(AlgorithmConfig? config)
    {
        if (config?.Parameters == null)
        {
            return null;
        }

        var result = new List<object>(config.Parameters.Count);
        foreach (AlgorithmParameter p in config.Parameters)
        {
            result.Add(new
            {
                p.Key,
                p.Label,
                p.ValueType,
                Value = p.DisplayValue,
                p.Unit,
                p.Group
            });
        }
        return result;
    }


    #region MT-012: Algo Verification (BUG-13 Detection)

    /// <summary>
    /// MT-012: Start an algorithm then verify it initialized successfully.
    ///
    /// BUG-13 (Silent Init Failure): MTCore may report isRunning=true even when the
    /// algo's Init() failed. Running algos should have a non-empty symbol and a known
    /// marketType. This method detects that pattern after a configurable wait.
    ///
    /// Usage: algos start-verify <id> [wait_seconds=4] [@profile]
    /// </summary>
    private CommandResult StartAndVerify(string[] args, string? targetProfile)
    {
        if (args.Length < 1 || !long.TryParse(args[0], out long id))
            return CommandResult.Fail("Usage: algos start-verify <id> [wait_seconds=4]");

        int waitSeconds = 4;
        if (args.Length >= 2 && int.TryParse(args[1], out int customWait))
            waitSeconds = Math.Clamp(customWait, 1, 30);

        // Start the algo (reuse existing AlgoAction which resolves type name)
        CommandResult startResult = AlgoAction(new[] { id.ToString() }, AlgorithmData.ActionType.START, targetProfile);

        // Wait for initialization window
        Thread.Sleep(waitSeconds * 1_000);

        // Re-read algo state
        CoreConnection? conn = ResolveConnection(targetProfile, out _);
        AlgorithmData? postStart = conn?.AlgoStore.FindById(id);

        return EvaluateVerification(conn?.Name ?? "?", id, postStart, waitSeconds, startResult.Message);
    }

    /// <summary>
    /// MT-012: Verify current state of an already-started algo (BUG-13 spot check).
    /// Does not start the algo — just evaluates the current AlgoStore state.
    ///
    /// Usage: algos verify <id> [@profile]
    /// </summary>
    private CommandResult VerifyAlgo(string[] args, string? targetProfile)
    {
        if (args.Length < 1 || !long.TryParse(args[0], out long id))
            return CommandResult.Fail("Usage: algos verify <id>");

        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        return EvaluateVerification(conn.Name, id, conn.AlgoStore.FindById(id), 0, "verify check");
    }

    /// <summary>
    /// Evaluate algo state data for BUG-13 patterns and produce a structured report.
    ///
    /// BUG-13 heuristic: trading algo shows isRunning=true but symbol is empty and
    /// marketType is still UNKNOWN (0). This indicates Init() failed silently.
    /// </summary>
    private static CommandResult EvaluateVerification(string server, long id, AlgorithmData? algo, int waitedSecs, string startMsg)
    {
        if (algo == null)
            return CommandResult.Fail($"[{server}] Algorithm {id} not found after operation.");

        string marketStr   = algo.marketType.ToString();
        bool   symEmpty    = string.IsNullOrWhiteSpace(algo.symbol);
        bool   mktUnknown  = string.Equals(marketStr, "UNKNOWN", StringComparison.OrdinalIgnoreCase)
                          || (int)algo.marketType == 0;
        bool   isTradingAlgo = algo.isTradingAlgo;

        // BUG-13 pattern: reports running, but no symbol/market resolved (Init failed)
        bool bug13Detected = algo.isRunning && isTradingAlgo && symEmpty && mktUnknown;

        // Verified: running AND (not a trading algo, OR has resolved symbol+market)
        bool verified = algo.isRunning && (!isTradingAlgo || (!symEmpty && !mktUnknown));

        string status = bug13Detected      ? "BUG13_SUSPECTED"
                      : verified           ? "VERIFIED"
                      : algo.isRunning     ? "RUNNING_UNCONFIRMED"
                                           : "NOT_RUNNING";

        var evidence = new List<string>();
        if (algo.isRunning)                      evidence.Add("isRunning=true");
        if (algo.isProcessing)                   evidence.Add("isProcessing=true");
        if (!symEmpty)                           evidence.Add($"symbol={algo.symbol}");
        if (!mktUnknown && !symEmpty)            evidence.Add($"market={marketStr}");
        if (bug13Detected)                       evidence.Add("WARN: isRunning=true but symbol/market unresolved — BUG-13 pattern");
        if (!algo.isRunning)                     evidence.Add("isRunning=false — algo is not running");

        string msg = $"[{server}] Algo {id} ({algo.name}) — {status} (waited {waitedSecs}s). {startMsg}";

        return (bug13Detected || !algo.isRunning)
            ? new CommandResult { Success = false, Message = msg, Data = new { server, id, name = algo.name, verified, status, isRunning = algo.isRunning, bug13Detected, symbol = algo.symbol ?? "", market = marketStr, waitedSecs, evidence } }
            : CommandResult.Ok(msg, new { server, id, name = algo.name, verified, status, isRunning = algo.isRunning, bug13Detected, symbol = algo.symbol ?? "", market = marketStr, waitedSecs, evidence });
    }

    #endregion

}
