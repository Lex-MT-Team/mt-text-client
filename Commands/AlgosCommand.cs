using System.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using MTShared;
using MTShared.Network;
using MTShared.Types;
using MTTextClient.Core;
using Newtonsoft.Json.Linq;
namespace MTTextClient.Commands;

/// <summary>
/// Algorithm management commands with multi-server support.
/// Full algorithm lifecycle — list, get, start, stop, save, delete, clone,
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
///   - Rename uses description field, preserves type name in name field.
///   - START_ALL/STOP_ALL uses AlgorithmData, not AlgorithmListData.
///   - DELETE_GROUP uses AlgorithmData (single request), not AlgorithmListData.
///     Core handles DELETE_GROUP in AlgorithmRequest handler, not AlgorithmListRequest.
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
            // File-backed clipboard for cross-profile transfer.
            "copy-to-clipboard" => CopyToClipboard(subArgs, targetProfile),
            "paste-from-clipboard" => PasteFromClipboard(subArgs, targetProfile, confirmFlag),
            "import-json" => ImportFromJson(subArgs, targetProfile, confirmFlag),
            // Bulk field-level edit across many algos.
            "bulk-edit" => BulkEdit(subArgs, targetProfile, confirmFlag),
            // Create a new algorithm via clone-from-source.
            "create" => CreateAlgo(subArgs, targetProfile, confirmFlag),
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
                name = ResolveDisplayName(a),
                CoreName = a.name,
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


    /// <summary>
    /// Resolve display name in priority info → description → name.
    /// Filters out the raw mt-algo-XXXX synthetic names so user-set
    /// labels surface in list/info outputs. Raw on-wire name still emitted
    /// alongside as CoreName for joins / id-based tooling.
    /// </summary>
    private static string ResolveDisplayName(AlgorithmData algo)
    {
        AlgorithmConfig? config = AlgorithmStore.ParseConfig(algo);
        if (config?.Parameters != null)
        {
            foreach (AlgorithmParameter parameter in config.Parameters)
            {
                if (!string.Equals(parameter.Key, "info", System.StringComparison.OrdinalIgnoreCase))
                    continue;
                string? value = parameter.DisplayValue?.Trim();
                if (!string.IsNullOrWhiteSpace(value)
                    && !value.StartsWith("mt-algo-", System.StringComparison.OrdinalIgnoreCase))
                    return value;
            }
        }
        string? description = algo.description?.Trim();
        if (!string.IsNullOrWhiteSpace(description)
            && !description.StartsWith("mt-algo-", System.StringComparison.OrdinalIgnoreCase))
            return description;
        return algo.name;
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
                    name = ResolveDisplayName(a),
                    CoreName = a.name,
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
            name = ResolveDisplayName(algo),
            CoreName = algo.name,
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
            return CommandResult.Ok($"[{conn.Name}] Algorithm {id}: already deleted (not found).",
                new { Server = conn.Name, Id = id, Status = "already_deleted" });
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
                name = ResolveDisplayName(a),
                CoreName = a.name,
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
    /// DELETE_GROUP must use AlgorithmData (single request), NOT AlgorithmListData.
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
            return CommandResult.Ok($"[{conn.Name}] Group {groupId}: already deleted (not found).",
                new { Server = conn.Name, GroupId = groupId, Status = "already_deleted" });
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

        // Send as AlgorithmData via AlgorithmRequest handler.
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

    #region File-backed Clipboard (copy-to-clipboard / paste / import-json)

    /// <summary>
    /// Serialise an algorithm to the schema-versioned clipboard.
    /// Read-only: never mutates the source profile.
    /// </summary>
    private CommandResult CopyToClipboard(string[] args, string? sourceProfile)
    {
        CoreConnection? conn = ResolveConnection(sourceProfile, out CommandResult? error);
        if (conn == null) return error!;
        if (args.Length < 1 || !long.TryParse(args[0], out long id))
            return CommandResult.Fail("Usage: algos copy-to-clipboard <id> [@profile]");
        AlgorithmData? algo = conn.AlgoStore.FindById(id);
        if (algo == null) return CommandResult.Fail($"[{conn.Name}] Algorithm {id} not found.");

        var payload = BuildExportPayload(conn, algo);
        string json = payload.ToString(Newtonsoft.Json.Formatting.None);
        AlgorithmClipboard.Save(json);
        return CommandResult.Ok(
            $"[{conn.Name}] Algorithm {id} ({algo.name}) → clipboard. " +
            $"Schema={AlgorithmClipboard.CurrentSchemaVersion}, exchange={conn.Profile.Exchange}, " +
            $"symbol={algo.symbol}, market={algo.marketType}.",
            new
            {
                Source = conn.Name,
                Exchange = conn.Profile.Exchange.ToString(),
                AlgorithmId = id,
                algo.name,
                algo.signature,
                algo.symbol,
                Market = algo.marketType.ToString(),
                SchemaVersion = AlgorithmClipboard.CurrentSchemaVersion,
                ClipboardFile = AlgorithmClipboard.ClipboardFile,
                JsonBytes = json.Length,
            });
    }

    /// <summary>
    /// Read the clipboard JSON, validate schema, run edge-case
    /// pre-flight (symbol/market/duplicate/blacklist), and dispatch to the
    /// target connection.  Optional <c>--override-symbol</c> / <c>--override-market</c>
    /// flags let the caller force the paste through a known mismatch.
    /// </summary>
    private CommandResult PasteFromClipboard(string[] args, string? targetProfile, bool confirmed)
    {
        string? json = AlgorithmClipboard.Load();
        if (json == null) return CommandResult.Fail("Clipboard is empty. Run 'algos copy-to-clipboard <id>' first.");
        return ApplyPayload(json, args, targetProfile, confirmed, sourceLabel: "clipboard");
    }

    /// <summary>
    /// Direct inline JSON paste (the automation-friendly form;
    /// avoids the clipboard-file hop).  Same edge-case pre-flight as paste.
    /// </summary>
    private CommandResult ImportFromJson(string[] args, string? targetProfile, bool confirmed)
    {
        var flags = ParseFlagsList(args);
        if (!flags.TryGetValue("--payload", out string? payload) || string.IsNullOrWhiteSpace(payload))
            return CommandResult.Fail(
                "Usage: algos import-json --payload <json|b64> [@destination] [--override-symbol X] [--override-market FUTURES] --confirm");
        // The MCP dispatcher base64-encodes the payload to avoid the REPL splitter
        // mangling raw JSON spaces/quotes; the REPL form accepts either base64 or
        // raw JSON.  Detect by trying base64 first.
        string decoded = TryDecodeBase64Utf8(payload) ?? payload;
        return ApplyPayload(decoded, args, targetProfile, confirmed, sourceLabel: "import-json");
    }

    private static string? TryDecodeBase64Utf8(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.TrimStart().StartsWith("{")) return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(s.Trim());
            string str = System.Text.Encoding.UTF8.GetString(bytes);
            if (str.TrimStart().StartsWith("{")) return str;
            return null;
        }
        catch { return null; }
    }

    private CommandResult ApplyPayload(string json, string[] args, string? targetProfile, bool confirmed, string sourceLabel)
    {
        // Parse override flags + force flag.
        var flags = ParseFlagsList(args);
        string? overrideSymbol = flags.GetValueOrDefault("--override-symbol");
        string? overrideMarketRaw = flags.GetValueOrDefault("--override-market");
        bool force = flags.ContainsKey("--force");

        // Resolve destination connection BEFORE accepting the JSON — fail fast
        // on disconnected destination rather than after a wasted parse.
        CoreConnection? destConn = ResolveConnection(targetProfile, out CommandResult? error);
        if (destConn == null) return error!;

        JObject payload;
        try { payload = JObject.Parse(json); }
        catch (Exception ex) { return CommandResult.Fail($"Clipboard ({sourceLabel}) JSON unparseable: {ex.Message}"); }

        // Schema-version gate — refuse to silently apply a payload from a
        // different MTCore generation.  The caller can re-export with the
        // current schema or update the importing client.
        string? schemaErr = AlgorithmClipboard.ValidateSchema(payload);
        if (schemaErr != null) return CommandResult.Fail(schemaErr);

        string sourceExchangeName = payload[AlgorithmClipboard.ExportedFromExchangeField]?.Value<string>() ?? "UNKNOWN";
        string sourceProfileName = payload[AlgorithmClipboard.ExportedFromProfileField]?.Value<string>() ?? "?";
        JObject? algoObj = payload[AlgorithmClipboard.AlgorithmField] as JObject;
        if (algoObj == null) return CommandResult.Fail(
            $"Payload ({sourceLabel}) missing '{AlgorithmClipboard.AlgorithmField}' block. Re-export with current schema.");

        string sourceSymbol = algoObj["symbol"]?.Value<string>() ?? "";
        string sourceMarketName = algoObj["market"]?.Value<string>() ?? "FUTURES";
        string sourceName = algoObj["name"]?.Value<string>() ?? "?";
        string sourceSignature = algoObj["signature"]?.Value<string>() ?? "?";

        // Pre-flight 1: symbol-mismatch detection across exchanges.
        Enum.TryParse<ExchangeType>(sourceExchangeName, ignoreCase: true, out var srcExEnum);
        ExchangeType dstExEnum = destConn.Profile.Exchange;
        string destSymbol = overrideSymbol ?? sourceSymbol;
        string? symbolWarning = null;
        if (overrideSymbol == null && srcExEnum != dstExEnum && !ExchangeSymbolMap.SameFormat(srcExEnum, dstExEnum))
        {
            string? suggested = ExchangeSymbolMap.Suggest(sourceSymbol, srcExEnum, dstExEnum,
                ParseMarket(sourceMarketName));
            return CommandResult.Fail(
                "symbol_mismatch: cross-exchange paste requires override_symbol.\n" +
                $"  source_symbol:        {sourceSymbol}\n" +
                $"  source_exchange:      {srcExEnum}\n" +
                $"  destination_exchange: {dstExEnum}\n" +
                $"  suggested_symbol:     {suggested ?? "(no automatic mapping; specify --override-symbol)"}\n" +
                $"  retry:                algos paste-from-clipboard @{destConn.Name} " +
                $"--override-symbol {suggested ?? "<symbol>"} --confirm");
        }

        // Pre-flight 2: market-type override sanity check.
        MarketType destMarket = ParseMarket(sourceMarketName);
        if (overrideMarketRaw != null)
        {
            if (!Enum.TryParse<MarketType>(overrideMarketRaw, ignoreCase: true, out var parsedMt) ||
                !Enum.IsDefined(typeof(MarketType), parsedMt))
                return CommandResult.Fail($"Invalid --override-market '{overrideMarketRaw}'. " +
                    "Allowed: SPOT, MARGIN, FUTURES, DELIVERY.");
            destMarket = parsedMt;
        }
        bool marketMismatch = destMarket != ParseMarket(sourceMarketName);

        // Pre-flight 3: duplicate detection on destination (same name + symbol + market).
        AlgorithmData? duplicate = null;
        foreach (var existing in destConn.AlgoStore.GetAll())
        {
            if (string.Equals(existing.name, sourceName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.symbol, destSymbol, StringComparison.OrdinalIgnoreCase) &&
                existing.marketType == destMarket)
            {
                duplicate = existing;
                break;
            }
        }

        // Pre-flight 4: dry-run preview if not --confirm.  Returns warnings + suggested
        // command WITHOUT touching the destination.
        if (!confirmed)
        {
            var warnings = new List<string>();
            if (marketMismatch) warnings.Add($"market_type_changed: {sourceMarketName} → {destMarket} " +
                "(MTCore will validate compatibility server-side)");
            if (duplicate != null) warnings.Add($"duplicate_detected: existing algorithm id={duplicate.id} on " +
                $"{destConn.Name} matches name + symbol + market (paste will create a separate copy)");
            // Blacklist check is best-effort: only fires if profile-settings store has loaded.
            string? blacklistWarn = TryDetectBlacklistConflict(destConn, destSymbol, destMarket);
            if (blacklistWarn != null) warnings.Add(blacklistWarn);

            return CommandResult.Ok(
                $"PASTE PREVIEW — DRY RUN ({sourceLabel})\n" +
                $"  Source:       {sourceProfileName} ({sourceExchangeName})\n" +
                $"  Destination:  {destConn.Name} ({destConn.Profile.Exchange})\n" +
                $"  Algorithm:    {sourceName} ({sourceSignature})\n" +
                $"  Symbol:       {sourceSymbol} → {destSymbol}\n" +
                $"  Market:       {sourceMarketName} → {destMarket}\n" +
                $"  Warnings:     {(warnings.Count == 0 ? "(none)" : string.Join("; ", warnings))}\n" +
                $"\nAdd --confirm to execute.",
                new
                {
                    DryRun = true,
                    SourceLabel = sourceLabel,
                    Source = sourceProfileName,
                    SourceExchange = sourceExchangeName,
                    Destination = destConn.Name,
                    DestinationExchange = destConn.Profile.Exchange.ToString(),
                    AlgorithmName = sourceName,
                    AlgorithmSignature = sourceSignature,
                    SourceSymbol = sourceSymbol,
                    DestinationSymbol = destSymbol,
                    SourceMarket = sourceMarketName,
                    DestinationMarket = destMarket.ToString(),
                    Warnings = warnings,
                    DuplicateAlgorithmId = duplicate?.id,
                });
        }

        // Build the destination AlgorithmData and dispatch.  We rebuild it
        // from the parsed JSON rather than from a source connection so the
        // paste works even when the source bench is offline.
        var newAlgo = BuildAlgorithmDataFromPayload(algoObj, destSymbol, destMarket, destConn);
        NotificationMessageData? notification = destConn.SendAlgorithmRequest(newAlgo);
        var resultWarnings = new List<string>();
        if (marketMismatch) resultWarnings.Add($"market_type_changed: {sourceMarketName} → {destMarket}");
        if (duplicate != null) resultWarnings.Add($"duplicate_detected: existing id={duplicate.id}");
        string? blacklistApplied = TryDetectBlacklistConflict(destConn, destSymbol, destMarket);
        if (blacklistApplied != null) resultWarnings.Add(blacklistApplied);

        if (notification == null)
        {
            return CommandResult.Ok(
                $"[{sourceProfileName} → {destConn.Name}] Paste sent (response timed out).",
                new { Destination = destConn.Name, AlgorithmName = sourceName, TimedOut = true, Warnings = resultWarnings });
        }

        if (!notification.IsOk)
            return CommandResult.Fail(
                $"[{sourceProfileName} → {destConn.Name}] Paste FAILED — {notification.notificationCode}: {notification.msgString}");

        return CommandResult.Ok(
            $"[{sourceProfileName} → {destConn.Name}] Algorithm '{sourceName}' ({sourceSignature}) PASTED ✓ " +
            $"symbol={destSymbol} market={destMarket}" +
            (resultWarnings.Count == 0 ? "" : " | warnings: " + string.Join("; ", resultWarnings)),
            new
            {
                Source = sourceProfileName,
                SourceExchange = sourceExchangeName,
                Destination = destConn.Name,
                DestinationExchange = destConn.Profile.Exchange.ToString(),
                AlgorithmName = sourceName,
                AlgorithmSignature = sourceSignature,
                DestinationSymbol = destSymbol,
                DestinationMarket = destMarket.ToString(),
                Success = true,
                Warnings = resultWarnings,
            });
    }

    private static JObject BuildExportPayload(CoreConnection conn, AlgorithmData algo)
    {
        var algoJson = new JObject
        {
            ["name"] = algo.name,
            ["signature"] = algo.signature,
            ["description"] = algo.description,
            ["symbol"] = algo.symbol,
            ["market"] = algo.marketType.ToString(),
            ["marketTypeInt"] = (int)algo.marketType,
            ["groupType"] = algo.groupType.ToString(),
            ["groupTypeInt"] = (int)algo.groupType,
            ["isTradingAlgo"] = algo.isTradingAlgo,
            ["version"] = algo.version,
            ["argsJson"] = algo.argsJson ?? "",
        };
        return new JObject
        {
            [AlgorithmClipboard.SchemaVersionField] = AlgorithmClipboard.CurrentSchemaVersion,
            [AlgorithmClipboard.ExportedFromExchangeField] = conn.Profile.Exchange.ToString(),
            [AlgorithmClipboard.ExportedFromProfileField] = conn.Name,
            ["exported_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            [AlgorithmClipboard.AlgorithmField] = algoJson,
        };
    }

    private static AlgorithmData BuildAlgorithmDataFromPayload(JObject algoObj, string destSymbol, MarketType destMarket, CoreConnection destConn)
    {
        var algo = new AlgorithmData
        {
            id = -1,
            name = algoObj["name"]?.Value<string>() ?? "",
            signature = algoObj["signature"]?.Value<string>() ?? "",
            description = algoObj["description"]?.Value<string>() ?? "",
            symbol = destSymbol,
            marketType = destMarket,
            isTradingAlgo = algoObj["isTradingAlgo"]?.Value<bool>() ?? true,
            argsJson = algoObj["argsJson"]?.Value<string>() ?? "",
            actionType = AlgorithmData.ActionType.SAVE,
            isRunning = false,
            isProcessing = false,
            groupID = 0,
            version = algoObj["version"]?.Value<int>() ?? 0,
        };
        if (algoObj["groupTypeInt"] is JValue gt && gt.Type == JTokenType.Integer)
        {
            try { algo.groupType = (AlgorithmGroupType)gt.Value<int>(); }
            catch { /* leave default */ }
        }
        return algo;
    }

    private static MarketType ParseMarket(string s)
    {
        if (Enum.TryParse<MarketType>(s, ignoreCase: true, out var mt) && Enum.IsDefined(typeof(MarketType), mt))
            return mt;
        return MarketType.FUTURES;
    }

    private static string? TryDetectBlacklistConflict(CoreConnection conn, string symbol, MarketType market)
    {
        if (!conn.ProfileSettingsStore.HasData) return null;
        string? raw = conn.ProfileSettingsStore.GetValue("BlackList");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var arr = JArray.Parse(raw);
            foreach (var entry in arr)
            {
                if (entry is not JObject o) continue;
                int? entryMarket = (int?)o["MarketType"];
                string? entrySymbol = (string?)o["Symbol"];
                if (entryMarket == (int)market && string.Equals(entrySymbol, symbol, StringComparison.OrdinalIgnoreCase))
                    return $"blacklist_conflict: {symbol} ({market}) is blacklisted on {conn.Name}; paste will land but MTCore may refuse to run it";
            }
        }
        catch { return null; }
        return null;
    }

    private static Dictionary<string, string> ParseFlagsList(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
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

    // ── bulk-edit ──────────────────────────────────────────────

    /// <summary>
    /// Fan out a single field-level mutation across many algos
    /// in one call.  Dry-run by default (no <c>--confirm</c>) returns a
    /// per-algo diff preview.  With <c>--confirm</c> each affected algo is
    /// saved via <see cref="CoreConnection.SendAlgorithmRequest"/>; failures
    /// are tolerated and surfaced in a per-row <c>partial_result</c>
    /// structure (we never abort the batch on first failure).
    ///
    /// CLI:
    ///   algos bulk-edit [@profile]
    ///       --filter <base64 JSON: {"ids":[...]} | {"group_id":N} | {"all":true}>
    ///       --mutation <base64 JSON: {"whitelist_add":[...]} | {"whitelist_remove":[...]} | {"set":{k:v}}>
    ///       --confirm
    /// </summary>
    private CommandResult BulkEdit(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        var flags = ParseFlagsList(args);
        if (!flags.TryGetValue("--filter", out string? filterRaw))
            return CommandResult.Fail("Usage: algos bulk-edit --filter <JSON|b64> --mutation <JSON|b64> [--confirm]");
        if (!flags.TryGetValue("--mutation", out string? mutationRaw))
            return CommandResult.Fail("Missing --mutation. " +
                "Examples: {\"whitelist_add\":[\"ETHUSDT\"]}, {\"whitelist_remove\":[\"XRPUSDT\"]}, {\"set\":{\"key\":\"value\"}}.");

        string filterJson = TryDecodeBase64Utf8(filterRaw) ?? filterRaw;
        string mutationJson = TryDecodeBase64Utf8(mutationRaw) ?? mutationRaw;

        JObject filter, mutation;
        try { filter = JObject.Parse(filterJson); }
        catch (Exception ex) { return CommandResult.Fail($"--filter JSON unparseable: {ex.Message}"); }
        try { mutation = JObject.Parse(mutationJson); }
        catch (Exception ex) { return CommandResult.Fail($"--mutation JSON unparseable: {ex.Message}"); }

        var (mtype, err) = ValidateMutation(mutation);
        if (err != null) return CommandResult.Fail($"mutation_schema_error: {err}");

        var matched = ResolveFilter(conn, filter, out string? filterErr);
        if (filterErr != null) return CommandResult.Fail($"filter_error: {filterErr}");
        if (matched.Count == 0)
        {
            return CommandResult.Ok(
                $"[{conn.Name}] bulk-edit matched 0 algorithms (filter={filterJson}).",
                new { Server = conn.Name, Matched = 0, DryRun = !confirmed, Algos = new List<object>() });
        }

        // Per-algo plan: compute current → proposed, record warnings.
        var plan = new List<BulkEditPlanEntry>(matched.Count);
        foreach (var algo in matched)
            plan.Add(BuildPlanEntry(algo, mtype, mutation));

        if (!confirmed)
        {
            // Dry-run preview — no wire calls.
            var preview = plan.Select(e => new
            {
                e.AlgorithmId,
                e.Name,
                e.Signature,
                CurrentValue = e.CurrentValue,
                ProposedValue = e.ProposedValue,
                Warnings = e.Warnings,
                WillMutate = e.WillMutate,
                SchemaMismatch = e.SchemaMismatch,
            }).ToList<object>();
            int wouldMutate = plan.Count(e => e.WillMutate);
            int schemaIssues = plan.Count(e => e.SchemaMismatch);
            return CommandResult.Ok(
                $"BULK-EDIT PREVIEW (DRY RUN) — {plan.Count} algorithm(s) matched; " +
                $"{wouldMutate} would mutate, {schemaIssues} schema-mismatch.\n" +
                $"Add --confirm to apply.",
                new
                {
                    Server = conn.Name,
                    DryRun = true,
                    Matched = plan.Count,
                    WouldMutate = wouldMutate,
                    SchemaMismatches = schemaIssues,
                    Algos = preview,
                });
        }

        // Commit path — for each algo with WillMutate=true, mutate the in-memory
        // AlgorithmData (UpdateParameter / direct whiteList rewrite) then
        // SendAlgorithmRequest(SAVE).  Failures recorded per row.
        int successCount = 0;
        int failCount = 0;
        var rows = new List<object>(plan.Count);
        foreach (var entry in plan)
        {
            if (entry.SchemaMismatch || !entry.WillMutate)
            {
                rows.Add(new
                {
                    entry.AlgorithmId,
                    entry.Name,
                    entry.Signature,
                    Success = false,
                    Reason = entry.SchemaMismatch ? "schema_mismatch" : "no_change_needed",
                    entry.Warnings,
                });
                if (entry.SchemaMismatch) failCount++;
                continue;
            }

            var (mutOk, mutErr) = ApplyMutation(entry.AlgoRef, mtype, mutation);
            if (!mutOk)
            {
                rows.Add(new { entry.AlgorithmId, entry.Name, entry.Signature,
                    Success = false, Error = mutErr, entry.Warnings });
                failCount++;
                continue;
            }

            // Save to bench.  Cannot mutate a running algo.
            if (entry.AlgoRef.isRunning)
            {
                rows.Add(new { entry.AlgorithmId, entry.Name, entry.Signature,
                    Success = false, Error = "algorithm_running: stop it first", entry.Warnings });
                failCount++;
                continue;
            }
            var request = new AlgorithmData(entry.AlgoRef) { actionType = AlgorithmData.ActionType.SAVE };
            NotificationMessageData? notif = conn.SendAlgorithmRequest(request, timeoutMs: 15_000);
            if (notif == null)
            {
                rows.Add(new { entry.AlgorithmId, entry.Name, entry.Signature,
                    Success = false, Error = "timeout", entry.Warnings });
                failCount++;
                continue;
            }
            if (!notif.IsOk)
            {
                rows.Add(new { entry.AlgorithmId, entry.Name, entry.Signature,
                    Success = false,
                    Error = $"mtcore_rejected: {notif.notificationCode} {notif.msgString}",
                    entry.Warnings });
                failCount++;
                continue;
            }
            rows.Add(new
            {
                entry.AlgorithmId,
                entry.Name,
                entry.Signature,
                Success = true,
                CurrentValue = entry.CurrentValue,
                NewValue = entry.ProposedValue,
                entry.Warnings,
            });
            successCount++;
        }

        return CommandResult.Ok(
            $"[{conn.Name}] bulk-edit applied: {successCount} mutated, {failCount} failed " +
            $"(of {plan.Count} matched).  Some rows may carry warnings.",
            new
            {
                Server = conn.Name,
                Matched = plan.Count,
                Mutated = successCount,
                Failed = failCount,
                PartialResult = rows,
            });
    }

    private List<AlgorithmData> ResolveFilter(CoreConnection conn, JObject filter, out string? error)
    {
        error = null;
        var all = conn.AlgoStore.GetAll();

        if (filter["all"]?.Value<bool>() == true)
            return all.ToList();

        if (filter["group_id"] is JValue gv && gv.Type == JTokenType.Integer)
        {
            long gid = gv.Value<long>();
            return all.Where(a => a.groupID == gid).ToList();
        }

        if (filter["ids"] is JArray ids)
        {
            var idSet = new HashSet<long>();
            foreach (var t in ids)
            {
                if (t.Type == JTokenType.Integer) idSet.Add(t.Value<long>());
                else if (t.Type == JTokenType.String && long.TryParse(t.Value<string>(), out long parsed)) idSet.Add(parsed);
            }
            return all.Where(a => idSet.Contains(a.id)).ToList();
        }

        error = "filter must specify one of: {\"all\":true}, {\"group_id\":N}, {\"ids\":[...]}";
        return new List<AlgorithmData>();
    }

    private enum BulkMutationKind { Unknown, WhitelistAdd, WhitelistRemove, Set }

    private static (BulkMutationKind, string? err) ValidateMutation(JObject mutation)
    {
        if (mutation["whitelist_add"] is JArray)   return (BulkMutationKind.WhitelistAdd, null);
        if (mutation["whitelist_remove"] is JArray)return (BulkMutationKind.WhitelistRemove, null);
        if (mutation["set"] is JObject)            return (BulkMutationKind.Set, null);
        return (BulkMutationKind.Unknown,
            "mutation must declare exactly one of: whitelist_add (array), whitelist_remove (array), set (object).");
    }

    private sealed class BulkEditPlanEntry
    {
        public required AlgorithmData AlgoRef;
        public required long AlgorithmId;
        public required string Name;
        public required string Signature;
        public string? CurrentValue;
        public string? ProposedValue;
        public bool WillMutate;
        public bool SchemaMismatch;
        public List<string> Warnings { get; } = new();
    }

    private BulkEditPlanEntry BuildPlanEntry(AlgorithmData algo, BulkMutationKind mtype, JObject mutation)
    {
        var entry = new BulkEditPlanEntry
        {
            AlgoRef = algo,
            AlgorithmId = algo.id,
            Name = algo.name ?? "",
            Signature = algo.signature ?? "",
        };

        switch (mtype)
        {
            case BulkMutationKind.WhitelistAdd:
            case BulkMutationKind.WhitelistRemove:
            {
                string? current = ReadArgValue(algo, "whiteList");
                if (current == null)
                {
                    entry.SchemaMismatch = true;
                    entry.Warnings.Add("schema_mismatch: argsJson has no 'whiteList' parameter (signature " + entry.Signature + ")");
                    return entry;
                }
                entry.CurrentValue = current;
                var values = (mutation[mtype == BulkMutationKind.WhitelistAdd ? "whitelist_add" : "whitelist_remove"] as JArray)!
                    .Where(t => t.Type == JTokenType.String)
                    .Select(t => t.Value<string>()!.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                var currentSet = SplitCsv(current).ToList();
                List<string> nextList;
                if (mtype == BulkMutationKind.WhitelistAdd)
                {
                    nextList = currentSet.ToList();
                    foreach (string v in values)
                        if (!nextList.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)))
                            nextList.Add(v);
                }
                else
                {
                    nextList = currentSet
                        .Where(x => !values.Any(v => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                }
                string proposed = string.Join(",", nextList);
                entry.ProposedValue = proposed;
                entry.WillMutate = proposed != current;
                if (!entry.WillMutate) entry.Warnings.Add("no_change: whiteList already in desired state");
                if (entry.WillMutate)
                {
                    string? blacklistConflict = DetectBlacklistConflict(algo, values);
                    if (blacklistConflict != null) entry.Warnings.Add(blacklistConflict);
                }
                return entry;
            }

            case BulkMutationKind.Set:
            {
                var kvBlock = (JObject)mutation["set"]!;
                if (kvBlock.Count == 0)
                {
                    entry.SchemaMismatch = true;
                    entry.Warnings.Add("schema_mismatch: 'set' block is empty");
                    return entry;
                }
                // Pre-check every key in the set block exists on the algo's argsJson.
                var diffs = new List<string>();
                foreach (var prop in kvBlock.Properties())
                {
                    string? cur = ReadArgValue(algo, prop.Name);
                    if (cur == null)
                    {
                        entry.SchemaMismatch = true;
                        entry.Warnings.Add($"schema_mismatch: argsJson has no '{prop.Name}' parameter on {entry.Signature}");
                        return entry;
                    }
                    string newVal = prop.Value.Type switch
                    {
                        JTokenType.String => prop.Value.Value<string>() ?? "",
                        JTokenType.Boolean => prop.Value.Value<bool>() ? "ON" : "OFF",
                        _ => prop.Value.ToString(),
                    };
                    diffs.Add($"{prop.Name}: {cur} → {newVal}");
                    if (newVal != cur) entry.WillMutate = true;
                }
                entry.CurrentValue = "—";
                entry.ProposedValue = string.Join(" | ", diffs);
                if (!entry.WillMutate) entry.Warnings.Add("no_change: all set keys already in desired state");
                return entry;
            }
        }
        entry.SchemaMismatch = true;
        return entry;
    }

    private static (bool, string?) ApplyMutation(AlgorithmData algo, BulkMutationKind mtype, JObject mutation)
    {
        switch (mtype)
        {
            case BulkMutationKind.WhitelistAdd:
            case BulkMutationKind.WhitelistRemove:
            {
                string? current = ReadArgValue(algo, "whiteList");
                if (current == null) return (false, "schema_mismatch_at_apply: whiteList vanished");
                var values = (mutation[mtype == BulkMutationKind.WhitelistAdd ? "whitelist_add" : "whitelist_remove"] as JArray)!
                    .Where(t => t.Type == JTokenType.String)
                    .Select(t => t.Value<string>()!.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                var currentSet = SplitCsv(current).ToList();
                List<string> nextList;
                if (mtype == BulkMutationKind.WhitelistAdd)
                {
                    nextList = currentSet.ToList();
                    foreach (string v in values)
                        if (!nextList.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)))
                            nextList.Add(v);
                }
                else
                {
                    nextList = currentSet
                        .Where(x => !values.Any(v => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                }
                string proposed = string.Join(",", nextList);
                var (ok, e) = AlgorithmStore.UpdateParameter(algo, "whiteList", proposed);
                return (ok, e);
            }
            case BulkMutationKind.Set:
            {
                var kv = (JObject)mutation["set"]!;
                foreach (var prop in kv.Properties())
                {
                    string newVal = prop.Value.Type switch
                    {
                        JTokenType.String => prop.Value.Value<string>() ?? "",
                        JTokenType.Boolean => prop.Value.Value<bool>() ? "ON" : "OFF",
                        _ => prop.Value.ToString(),
                    };
                    var (ok, e) = AlgorithmStore.UpdateParameter(algo, prop.Name, newVal);
                    if (!ok) return (false, $"set[{prop.Name}]: {e}");
                }
                return (true, null);
            }
        }
        return (false, "unknown_mutation_kind");
    }

    private static string? ReadArgValue(AlgorithmData algo, string key)
    {
        if (string.IsNullOrWhiteSpace(algo.argsJson)) return null;
        try
        {
            JObject root = JObject.Parse(algo.argsJson);
            JObject? args = root["Arguments"] as JObject;
            if (args == null) return null;
            if (args[key] is not JObject argObj) return null;
            JToken? val = argObj["value"];
            return val?.ToString() ?? "";
        }
        catch { return null; }
    }

    private static IEnumerable<string> SplitCsv(string s) =>
        (s ?? "").Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(x => x.Trim())
                  .Where(x => x.Length > 0);

    /// <summary>
    /// Detect whitelist values that collide with the algo's blackList parameter.
    /// Returns a single warning string listing the conflicts, or null if none.
    /// </summary>
    private static string? DetectBlacklistConflict(AlgorithmData algo, List<string> values)
    {
        string? bl = ReadArgValue(algo, "blackList");
        if (string.IsNullOrWhiteSpace(bl)) return null;
        var blSet = SplitCsv(bl)
            .Select(x => x.ToUpperInvariant())
            .ToHashSet();
        var collisions = values
            .Where(v => blSet.Contains(v.ToUpperInvariant()))
            .ToList();
        if (collisions.Count == 0) return null;
        return $"blacklist_conflict: {string.Join(",", collisions)} also in this algo's blackList";
    }

    #endregion

    #region Algorithm creation (clone-from-source w/ 3-layer resilience)

    /// <summary>
    /// Schema-version stamp embedded in created algorithms' argsJson.
    /// Bump on any change to the metadata shape or the clone-merge contract.
    /// </summary>
    public const string AlgoCreateSchemaVersion = "algo-create-v1";

    /// <summary>
    /// Create a new algorithm on a target profile by cloning a source
    /// algorithm's argsJson and applying user overrides on top.
    ///
    /// <para>Strategy: MTCore exposes no preset / template RPC.  The only viable
    /// creation path is clone-from-source.  A user-pinned <c>--source-id</c>
    /// takes precedence; otherwise the handler auto-discovers a source on the
    /// target profile matching the <c>--algo-type</c> (group_type) and optional
    /// <c>--signature</c>.</para>
    ///
    /// <para><b>Three-layer resilience against MoonTrader-update drift:</b></para>
    /// <list type="number">
    ///   <item><b>Layer 1 — preset from a live algorithm, not from MCP-side
    ///   hardcoded constants.</b>  The argsJson template is whatever MTCore
    ///   carries on the source algorithm at this moment.  New fields shipped
    ///   in a future MTCore binary flow through to the new algorithm because
    ///   we copy the whole argsJson verbatim.</item>
    ///   <item><b>Layer 2 — schema-version stamp.</b>  A synthetic
    ///   <c>_mcp_metadata</c> JObject is injected into <c>argsJson.Arguments</c>
    ///   carrying <c>created_by_mcp_version</c>, <c>algo_type</c>,
    ///   <c>source_algo_id</c>, <c>source_profile</c>, <c>source_exchange</c>,
    ///   <c>created_at_utc</c>.  This creates a history of what schema was
    ///   in use at creation time, observable via <c>mt_algos_config</c>.</item>
    ///   <item><b>Layer 3 — unknown-field passthrough.</b>  Overrides flow
    ///   through <see cref="AlgorithmStore.UpdateParameter"/> which touches
    ///   only <c>Arguments.&lt;key&gt;.value</c>; every other field
    ///   (argumentType, useOnlyPositiveValue, label, group, order, AND any
    ///   future field the MCP layer doesn't recognise) is preserved
    ///   verbatim.  Unknown override keys are warned but accepted — a
    ///   caller may know about a new MT field before MCP does.</item>
    /// </list>
    ///
    /// CLI:
    ///   algos create [@profile]
    ///       --algo-type SHOTS|AVERAGES|WATCHERS|SIGNALS|SAVER|DEPTHSHOTS|VECTOR
    ///       [--signature SG]
    ///       [--source-id N]                        (pin a specific source)
    ///       [--new-name "..."]                     (default: <source>_copy_<ts>)
    ///       [--overrides &lt;b64-json&gt;]               (flat {key:newValue} map)
    ///       [--force]                              (accept duplicate_name)
    ///       [--no-dry-run]                         (commit; default is dry-run)
    ///       [--confirm]
    /// </summary>
    private CommandResult CreateAlgo(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        var flags = ParseFlagsList(args);
        string? algoTypeRaw = flags.GetValueOrDefault("--algo-type");
        string? sigFilter = flags.GetValueOrDefault("--signature");
        string? sourceIdRaw = flags.GetValueOrDefault("--source-id");
        string? newName = flags.GetValueOrDefault("--new-name");
        string? overridesRaw = flags.GetValueOrDefault("--overrides");
        bool force = flags.ContainsKey("--force");
        bool dryRun = !flags.ContainsKey("--no-dry-run");  // dry-run is the default
        bool confirmFlag = confirmed || flags.ContainsKey("--confirm");

        if (!dryRun && !confirmFlag)
            return CommandResult.Fail("algos create --no-dry-run requires --confirm.");

        // Resolve overrides JSON (base64 or raw).
        JObject overrides = new();
        if (!string.IsNullOrWhiteSpace(overridesRaw))
        {
            string ovJson = TryDecodeBase64Utf8(overridesRaw) ?? overridesRaw;
            try { overrides = JObject.Parse(ovJson); }
            catch (Exception ex) { return CommandResult.Fail($"--overrides JSON unparseable: {ex.Message}"); }
        }

        // Locate the source algorithm.
        AlgorithmData? source = null;
        string presetSource = "(unset)";
        if (long.TryParse(sourceIdRaw, out long sourceId))
        {
            source = conn.AlgoStore.FindById(sourceId);
            if (source == null)
                return CommandResult.Fail($"template_not_available: source_algo_id={sourceId} not on {conn.Name}.");
            presetSource = $"clone_from_id:{sourceId}";
        }
        else
        {
            // Auto-discover by --algo-type (group_type) and optional --signature.
            if (string.IsNullOrWhiteSpace(algoTypeRaw))
                return CommandResult.Fail(
                    "algo_type_required: specify --algo-type SHOTS|AVERAGES|WATCHERS|SIGNALS|SAVER|DEPTHSHOTS|VECTOR " +
                    "OR specify --source-id N to clone a specific algorithm.");
            if (!Enum.TryParse<AlgorithmGroupType>(algoTypeRaw.ToUpperInvariant(), out var wantGroupType) ||
                !Enum.IsDefined(typeof(AlgorithmGroupType), wantGroupType) ||
                wantGroupType == AlgorithmGroupType.UNKNOWN)
            {
                var known = string.Join("|", Enum.GetNames(typeof(AlgorithmGroupType))
                    .Where(n => n != "UNKNOWN"));
                return CommandResult.Fail($"algo_type_unknown: '{algoTypeRaw}'. Known types: {known}.");
            }
            // Scan the bench for a match.
            foreach (var a in conn.AlgoStore.GetAll())
            {
                if (a.groupType != wantGroupType) continue;
                if (sigFilter != null && !string.Equals(a.signature, sigFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                source = a;
                break;
            }
            if (source == null)
                return CommandResult.Fail(
                    $"template_not_available: no algorithm of group_type={wantGroupType}" +
                    (sigFilter != null ? $" + signature={sigFilter}" : "") +
                    $" found on {conn.Name}.  " +
                    "Either (a) specify --source-id N to clone a specific algorithm, " +
                    "or (b) create at least one algorithm of this type manually via the GUI / paste-from-clipboard first.");
            presetSource = $"auto_discover_on_target_profile (group_type={wantGroupType}, signature={sigFilter ?? source.signature ?? "?"}, sample_id={source.id})";
        }

        // Clone the source — preserves whole argsJson verbatim (Layer 1 + 3).
        var fresh = new AlgorithmData(source)
        {
            id = -1,
            actionType = AlgorithmData.ActionType.SAVE,
            isRunning = false,
            isProcessing = false,
            groupID = 0,
        };
        string finalName = newName ?? $"{source.name}_copy_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        fresh.name = finalName;

        // Duplicate-name check on destination.
        AlgorithmData? duplicate = null;
        foreach (var existing in conn.AlgoStore.GetAll())
        {
            if (string.Equals(existing.name, finalName, StringComparison.OrdinalIgnoreCase))
            { duplicate = existing; break; }
        }
        if (duplicate != null && !force)
        {
            return CommandResult.Fail(
                $"duplicate_name: '{finalName}' already exists on {conn.Name} (id={duplicate.id}). " +
                "Specify a unique --new-name, or pass --force to create a separate row anyway.");
        }

        // Apply overrides via UpdateParameter (Layer 3: touches only
        // Arguments.<key>.value; everything else preserved verbatim).
        var appliedOverrides = new List<string>();
        var unknownOverrides = new List<string>();
        if (overrides.Count > 0)
        {
            foreach (var prop in overrides.Properties())
            {
                string key = prop.Name;
                string newVal = prop.Value.Type switch
                {
                    JTokenType.String => prop.Value.Value<string>() ?? "",
                    JTokenType.Boolean => prop.Value.Value<bool>() ? "ON" : "OFF",
                    _ => prop.Value.ToString(),
                };
                var (ok, err) = AlgorithmStore.UpdateParameter(fresh, key, newVal);
                if (ok) appliedOverrides.Add(key);
                else
                {
                    // Treat "Parameter not found" as unknown-override (warn but accept).
                    if ((err ?? "").Contains("not found", StringComparison.OrdinalIgnoreCase))
                        unknownOverrides.Add(key);
                    else
                        return CommandResult.Fail($"override_apply_failed[{key}]: {err}");
                }
            }
        }

        // Inject Layer 2 metadata into argsJson.
        int unknownFieldsPreserved = InjectMcpMetadata(fresh, presetSource, source, conn,
            algoTypeRaw, sigFilter, finalName);

        // Dry-run preview (default).
        if (dryRun)
        {
            return CommandResult.Ok(
                $"ALGO CREATE PREVIEW (DRY RUN) on {conn.Name}\n" +
                $"  preset_source:           {presetSource}\n" +
                $"  source_algo_id:          {source.id}\n" +
                $"  source_name:             {source.name}\n" +
                $"  source_signature:        {source.signature}\n" +
                $"  source_group_type:       {source.groupType}\n" +
                $"  new_name:                {finalName}\n" +
                $"  overridden_fields ({appliedOverrides.Count}): {string.Join(", ", appliedOverrides)}\n" +
                $"  unknown_override_fields ({unknownOverrides.Count}): {string.Join(", ", unknownOverrides)}\n" +
                $"  unknown_fields_preserved: {unknownFieldsPreserved}\n" +
                $"  schema_version:          {AlgoCreateSchemaVersion}\n" +
                $"  duplicate_name:          {(duplicate != null ? $"WARN id={duplicate.id} (forced)" : "no")}\n" +
                "Add --no-dry-run --confirm to commit.",
                new
                {
                    Server = conn.Name,
                    DryRun = true,
                    PresetSource = presetSource,
                    SourceAlgoId = source.id,
                    SourceName = source.name,
                    SourceSignature = source.signature,
                    SourceGroupType = source.groupType.ToString(),
                    NewName = finalName,
                    OverriddenFields = appliedOverrides,
                    UnknownOverrideFields = unknownOverrides,
                    UnknownFieldsPreserved = unknownFieldsPreserved,
                    SchemaVersion = AlgoCreateSchemaVersion,
                    DuplicateName = duplicate?.id,
                    ResolvedArgsJsonBytes = fresh.argsJson?.Length ?? 0,
                });
        }

        // Commit path — dispatch via SendAlgorithmRequest (same wire as paste).
        NotificationMessageData? notif = conn.SendAlgorithmRequest(fresh, timeoutMs: 15_000);
        if (notif == null)
            return CommandResult.Ok(
                $"[{conn.Name}] algos create '{finalName}' sent (response timed out).",
                new { Server = conn.Name, NewName = finalName, TimedOut = true });
        if (!notif.IsOk)
            return CommandResult.Fail(
                $"[{conn.Name}] algos create '{finalName}' FAILED — {notif.notificationCode}: {notif.msgString}");
        return CommandResult.Ok(
            $"[{conn.Name}] algos create '{finalName}' ✓ (preset_source={presetSource}, " +
            $"overrides={appliedOverrides.Count}, unknown_overrides={unknownOverrides.Count}, schema={AlgoCreateSchemaVersion})",
            new
            {
                Server = conn.Name,
                Success = true,
                NewName = finalName,
                PresetSource = presetSource,
                SourceAlgoId = source.id,
                OverriddenFields = appliedOverrides,
                UnknownOverrideFields = unknownOverrides,
                UnknownFieldsPreserved = unknownFieldsPreserved,
                SchemaVersion = AlgoCreateSchemaVersion,
                Notification = notif.msgString,
            });
    }

    /// <summary>
    /// Inject the Stage-resilience metadata into <paramref name="fresh"/>'s
    /// argsJson under a synthetic <c>_mcp_metadata</c> key.  Returns the
    /// count of unknown-to-MCP fields preserved verbatim from the source
    /// template (Layer 3 evidence — observable in the dry-run preview).
    /// </summary>
    private static int InjectMcpMetadata(AlgorithmData fresh, string presetSource,
        AlgorithmData source, CoreConnection conn, string? algoTypeHint, string? sigFilter, string newName)
    {
        if (string.IsNullOrWhiteSpace(fresh.argsJson)) return 0;
        int unknownPreserved = 0;
        try
        {
            JObject root = JObject.Parse(fresh.argsJson);
            if (root["Arguments"] is JObject args)
            {
                // Inject metadata as a synthetic Arguments entry.  We use a key
                // prefixed with `_` to signal "not a user-facing parameter".
                args["_mcp_metadata"] = new JObject
                {
                    ["created_by_mcp_version"] = AlgoCreateSchemaVersion,
                    ["algo_type_hint"] = algoTypeHint ?? "",
                    ["signature_hint"] = sigFilter ?? "",
                    ["source_algo_id"] = source.id,
                    ["source_name"] = source.name,
                    ["source_signature"] = source.signature,
                    ["source_group_type"] = source.groupType.ToString(),
                    ["source_profile"] = conn.Name,
                    ["source_exchange"] = conn.Profile.Exchange.ToString(),
                    ["new_name"] = newName,
                    ["preset_source"] = presetSource,
                    ["created_at_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                };
                // Layer 3 evidence: count Arguments entries whose names we don't
                // know.  This is a heuristic — "known" = matches a small static
                // set of parameter-name patterns documented in the codebase.
                // The count is purely informational; ALL entries are preserved.
                unknownPreserved = CountUnknownArgs(args);
            }
            fresh.argsJson = root.ToString(Newtonsoft.Json.Formatting.None);
        }
        catch { /* best-effort metadata; never block creation on JSON quirk */ }
        return unknownPreserved;
    }

    /// <summary>
    /// Conservative heuristic: how many parameters in Arguments are NOT in our
    /// small static list of names the MCP layer knows about?  This number is
    /// purely informational (every entry is preserved verbatim in any case).
    /// </summary>
    private static readonly HashSet<string> KnownArgKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "isEmulated", "isPreset", "namingRule", "info", "autoStart",
        "stopIfTradeLatencyGreaterThan", "stopIfOrderLatencyGreaterThan",
        "autoRestart", "shotRestartDelay", "openGraphOnOrderFill",
        "openGraphOnDealComplete", "filterCheckFrequency",
        "criticalErrorRestartDelay", "whiteList", "blackList",
        "quoteAssets", "useListingOnly",
        "coinDeltaFilterList", "deltaFilterList", "autoStopFilterList",
        "triggerList", "_mcp_metadata",
    };

    private static int CountUnknownArgs(JObject args)
    {
        int count = 0;
        foreach (var p in args.Properties())
            if (!KnownArgKeys.Contains(p.Name)) count++;
        return count;
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


    #region Algo Verification (Silent Init Failure Detection)

    /// <summary>
    /// Start an algorithm then verify it initialized successfully.
    ///
    /// Silent Init Failure: MTCore may report isRunning=true even when the
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
    /// Verify current state of an already-started algo (silent init failure spot check).
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
    /// Evaluate algo state data for silent-init-failure patterns and produce a structured report.
    ///
    /// Heuristic: trading algo shows isRunning=true but symbol is empty and
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

        // Silent init failure pattern: reports running, but no symbol/market resolved (Init failed)
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
        if (bug13Detected)                       evidence.Add("WARN: isRunning=true but symbol/market unresolved — silent init failure pattern");
        if (!algo.isRunning)                     evidence.Add("isRunning=false — algo is not running");

        string msg = $"[{server}] Algo {id} ({algo.name}) — {status} (waited {waitedSecs}s). {startMsg}";

        return (bug13Detected || !algo.isRunning)
            ? new CommandResult { Success = false, Message = msg, Data = new { server, id, name = algo.name, verified, status, isRunning = algo.isRunning, bug13Detected, symbol = algo.symbol ?? "", market = marketStr, waitedSecs, evidence } }
            : CommandResult.Ok(msg, new { server, id, name = algo.name, verified, status, isRunning = algo.isRunning, bug13Detected, symbol = algo.symbol ?? "", market = marketStr, waitedSecs, evidence });
    }

    #endregion

}
