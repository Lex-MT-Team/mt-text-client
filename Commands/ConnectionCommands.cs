using System;
using System.Collections.Generic;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// Connect to one or more MT-Core servers.
/// Supports: connect <profile>, connect <p1> <p2> ..., connect all
/// </summary>
public sealed class ConnectCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public ConnectCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public string Name => "connect";
    public string Description => "Connect to MT-Core server(s)";
    public string Usage => "connect <profile> | connect <p1> <p2> ... | connect all";

    public CommandResult Execute(string[] args)
    {
        if (args.Length < 1)
        {
            return CommandResult.Fail(Usage);
        }

        List<ServerProfile>? profiles = ProfileManager.LoadProfiles();

        // "connect all" — connect to every profile
        if (args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (profiles.Count == 0)
            {
                return CommandResult.Fail("No profiles configured.");
            }

            return ConnectMultiple(profiles);
        }

        // Single or multiple profile names
        var targetProfiles = new List<ServerProfile>();
        var notFound = new List<string>();

        foreach (string name in args)
        {
            ServerProfile? p = ProfileManager.FindProfile(profiles, name);
            if (p != null)
            {
                targetProfiles.Add(p);
            }
            else
            {
                notFound.Add(name);
            }
        }

        if (targetProfiles.Count == 0)
        {
            return CommandResult.Fail(
                $"No profiles found for: {string.Join(", ", notFound)}. Use 'profile list'.");
        }

        CommandResult result = ConnectMultiple(targetProfiles);
        if (notFound.Count > 0)
        {
            result = CommandResult.Ok(
                result.Message + $"\n⚠ Not found: {string.Join(", ", notFound)}",
                result.Data);
        }

        return result;
    }

    private CommandResult ConnectMultiple(List<ServerProfile> profiles)
    {
        var results = new List<object>();
        int initiated = 0;

        foreach (ServerProfile profile in profiles)
        {
            CoreConnection? conn = _manager.Connect(profile);
            if (conn != null)
            {
                initiated++;
                results.Add(new
                {
                    profile.Name,
                    profile.Address,
                    profile.Port,
                    Exchange = profile.Exchange.ToString(),
                    Status = "connecting"
                });
            }
            else
            {
                results.Add(new
                {
                    profile.Name,
                    profile.Address,
                    profile.Port,
                    Exchange = profile.Exchange.ToString(),
                    Status = "FAILED"
                });
            }
        }

        string? msg = initiated == 1
            ? $"Connecting to {profiles[0]}..."
            : $"Initiated {initiated}/{profiles.Count} connection(s).";

        return CommandResult.Ok(msg, results.Count == 1 ? null : results);
    }
}

/// <summary>
/// Disconnect from server(s).
/// disconnect — disconnect active, disconnect <name>, disconnect all
/// </summary>
public sealed class DisconnectCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public DisconnectCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public string Name => "disconnect";
    public string Description => "Disconnect from server(s)";
    public string Usage => "disconnect [profile-name | all]";

    public CommandResult Execute(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            int count = _manager.ConnectedCount;
            _manager.DisconnectAll();
            return CommandResult.Ok($"Disconnected from {count} server(s).");
        }

        if (args.Length > 0)
        {
            string? name = args[0];
            if (_manager.Disconnect(name))
            {
                return CommandResult.Ok($"Disconnected from '{name}'.");
            }

            return CommandResult.Fail($"No connection named '{name}'.");
        }

        // Disconnect active
        string? active = _manager.ActiveConnectionName;
        if (string.IsNullOrEmpty(active))
        {
            return CommandResult.Fail("Not connected to any server.");
        }

        _manager.Disconnect(active);
        return CommandResult.Ok($"Disconnected from '{active}'.");
    }
}

/// <summary>
/// Show status of all connections or a specific one.
/// </summary>
public sealed class StatusCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public StatusCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public string Name => "status";
    public string Description => "Show connection status (all servers)";
    public string Usage => "status [profile-name]";

    public CommandResult Execute(string[] args)
    {
        if (args.Length > 0)
        {
            CoreConnection? conn = _manager.Get(args[0]);
            if (conn == null)
            {
                return CommandResult.Fail($"No connection '{args[0]}'.");
            }

            return FormatSingle(conn);
        }

        IReadOnlyList<CoreConnection>? all = _manager.GetAll();
        if (all.Count == 0)
        {
            return CommandResult.Ok("No connections. Use 'connect <profile>' to connect.");
        }

        var data = new List<object>();
        for (int i = 0; i < all.Count; i++)
        {
            CoreConnection? c = all[i];
            int running = 0;
            IReadOnlyList<MTShared.Network.AlgorithmData>? cAlgos = c.AlgoStore.GetAll();
            for (int j = 0; j < cAlgos.Count; j++)
            {
                if (cAlgos[j].isRunning)
                {
                    running++;
                }
            }

            // MCP-002: distinguish "connected but stale" from healthy CONNECTED.
            // The UDP socket can stay open silently after MTCore stops talking; we mark it
            // STALE if no message has been seen for >60s (matches ConnectionHealthRecord.IsHealthy window).
            ConnectionHealthRecord? hRow = _manager.GetHealthRecord(c.Name);
            TimeSpan idleRow = hRow != null ? DateTime.UtcNow - hRow.LastSeen : TimeSpan.Zero;
            bool staleRow = c.IsConnected && hRow != null && idleRow > TimeSpan.FromSeconds(60);
            string statusLabel = c.IsConnected
                ? (staleRow ? $"⚠ STALE ({(int)idleRow.TotalSeconds}s idle)" : "✓ CONNECTED")
                : "✗ DISCONNECTED";

            data.Add(new
            {
                c.Name,
                Status = statusLabel,
                Active = c.Name.Equals(_manager.ActiveConnectionName, StringComparison.OrdinalIgnoreCase) ? "◄" : "",
                Address = $"{c.Profile.Address}:{c.Profile.Port}",
                Exchange = c.Profile.Exchange.ToString(),
                Algos = c.AlgoStore.Count,
                Running = running,
                Uptime = c.IsConnected ? FormatUptime(c.Uptime) : "-",
                IdleSeconds = hRow != null ? (int)idleRow.TotalSeconds : -1,
                Stale = staleRow
            });
        }

        int connected = 0;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].IsConnected)
            {
                connected++;
            }
        }
        int totalAlgos = 0;
        int totalRunning = 0;
        for (int i = 0; i < all.Count; i++)
        {
            totalAlgos += all[i].AlgoStore.Count;
            IReadOnlyList<MTShared.Network.AlgorithmData>? aList = all[i].AlgoStore.GetAll();
            for (int j = 0; j < aList.Count; j++)
            {
                if (aList[j].isRunning)
                {
                    totalRunning++;
                }
            }
        }

        return CommandResult.Ok(
            $"{connected}/{all.Count} connected | {totalAlgos} algos ({totalRunning} running) | active: {_manager.ActiveConnectionName}",
            data);
    }

    private CommandResult FormatSingle(CoreConnection conn)
    {
        int algoCount = conn.AlgoStore.Count;
        int runningCount = 0;
        IReadOnlyList<MTShared.Network.AlgorithmData>? algosAll = conn.AlgoStore.GetAll();
        for (int i = 0; i < algosAll.Count; i++)
        {
            if (algosAll[i].isRunning)
            {
                runningCount++;
            }
        }

        // MCP-002: same staleness check as list view.
        ConnectionHealthRecord? h = _manager.GetHealthRecord(conn.Name);
        TimeSpan idle = h != null ? DateTime.UtcNow - h.LastSeen : TimeSpan.Zero;
        bool stale = conn.IsConnected && h != null && idle > TimeSpan.FromSeconds(60);

        var data = new
        {
            conn.Name,
            Connected = conn.IsConnected,
            Stale = stale,
            IdleSeconds = h != null ? (int)idle.TotalSeconds : -1,
            Address = $"{conn.Profile.Address}:{conn.Profile.Port}",
            Exchange = conn.Profile.Exchange.ToString(),
            Algos = algoCount,
            Running = runningCount,
            Uptime = conn.IsConnected ? FormatUptime(conn.Uptime) : "-"
        };

        string? status = conn.IsConnected
            ? (stale
                ? $"[{conn.Name}] STALE — connected to {conn.Profile.Address}:{conn.Profile.Port} but no MTCore messages for {(int)idle.TotalSeconds}s | {algoCount} algos ({runningCount} running)"
                : $"[{conn.Name}] Connected to {conn.Profile.Address}:{conn.Profile.Port} | {algoCount} algos ({runningCount} running)")
            : $"[{conn.Name}] Disconnected";

        return CommandResult.Ok(status, data);
    }

    private static string FormatUptime(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
        {
            return $"{ts.Hours}h {ts.Minutes}m";
        }

        if (ts.TotalMinutes >= 1)
        {
            return $"{ts.Minutes}m {ts.Seconds}s";
        }

        return $"{ts.Seconds}s";
    }
}

/// <summary>
/// Switch the active connection context.
/// </summary>
public sealed class UseCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public UseCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public string Name => "use";
    public string Description => "Switch active connection (context for commands)";
    public string Usage => "use <profile-name>";

    public CommandResult Execute(string[] args)
    {
        if (args.Length < 1)
        {
            string? current = _manager.ActiveConnectionName;
            return string.IsNullOrEmpty(current)
                ? CommandResult.Fail("No active connection. Use 'connect <profile>' first.")
                : CommandResult.Ok($"Active connection: {current}. Use 'use <name>' to switch.");
        }

        string? name = args[0];
        CoreConnection? conn = _manager.SwitchTo(name);

        if (conn == null)
        {
            return CommandResult.Fail(
                $"No connection '{name}'. Use 'status' to see connections.");
        }

        return CommandResult.Ok(
            $"Switched to '{name}' ({conn.Profile.Address}:{conn.Profile.Port})",
            new { conn.Name, conn.Profile.Address, conn.Profile.Port, Connected = conn.IsConnected });
    }
}

/// <summary>
/// Set or list runtime tags on connection profiles for fleet orchestration.
/// tag                            — list tags for all connections
/// tag <profile>                  — list tags for one connection
/// tag <profile> <key> <value>   — set a tag (key/value pair) on a connection
///
/// Tags persist for the lifetime of the connection — useful for AI agent
/// routing: role=coordinator, strategy=scalper, group=us-east, region=prod.
/// </summary>
public sealed class TagCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public TagCommand(ConnectionManager manager) => _manager = manager;

    public string Name => "tag";
    public string Description => "Set or list fleet orchestration tags on connection profiles";
    public string Usage => "tag [<profile> [<key> <value>]]";

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
            return ListAll();

        string profileName = args[0];
        CoreConnection? conn = _manager.Get(profileName);
        if (conn == null)
            return CommandResult.Fail($"No connection '{profileName}'. Use 'status' to list connections.");

        if (args.Length == 1)
        {
            return CommandResult.Ok(
                $"Tags for '{profileName}' ({conn.Profile.Tags.Count} tag(s)):",
                new { profile = profileName, tags = conn.Profile.Tags });
        }

        if (args.Length == 3)
        {
            string key = args[1];
            string value = args[2];
            conn.Profile.Tags[key] = value;
            return CommandResult.Ok(
                $"Tag set: {profileName} [{key}] = \"{value}\"",
                new { profile = profileName, key, value, action = "set" });
        }

        return CommandResult.Fail("Usage: tag [<profile> [<key> <value>]]");
    }

    private CommandResult ListAll()
    {
        IReadOnlyList<CoreConnection> all = _manager.GetAll();
        var items = new List<object>(all.Count);
        foreach (CoreConnection c in all)
            items.Add(new { profile = c.Name, tags = c.Profile.Tags });
        return CommandResult.Ok(
            $"Fleet tags: {all.Count} connection(s) listed",
            items);
    }
}
