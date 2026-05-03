using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using MTShared.Types;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// Core status commands — system metrics, license info, health dashboard.
/// Provides real-time monitoring of MT-Core instances.
///
/// Subcommands:
///   core status                    — current core metrics (CPU, memory, latency)
///   core license                   — license and version info
///   core health                    — health check (latency, UDS status, API loading)
///   core dashboard                 — all servers status at a glance
///
/// Supports @profile prefix:
///   core @bnc_001 status
///   core dashboard                 — always shows all servers
/// </summary>
public sealed class CoreStatusCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "core";
    public string Description => "Core status: metrics, license, health dashboard";
    public string Usage => "core [<@profile>] <status|license|health|dashboard>";

    public CoreStatusCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(
                "Usage: core <subcommand>\n" +
                "  status      — CPU, memory, latency metrics\n" +
                "  license     — license, version, OS info\n" +
                "  health      — latency, UDS, API loading health check\n" +
                "  dashboard   — all servers overview");
        }

        // Parse @profile from any position in args
        string? profileName = null;
        var argsList = new List<string>();
        foreach (string arg in args)
        {
            if (arg.StartsWith('@'))
            {
                profileName = arg[1..];
            }
            else
            {
                argsList.Add(arg);
            }
        }

        if (argsList.Count == 0)
        {
            return CommandResult.Fail("Missing subcommand. Use: status, license, health, dashboard");
        }

        bool confirmFlag = args.Any(a => a.Equals("--confirm", StringComparison.OrdinalIgnoreCase) || a.Equals("-y", StringComparison.OrdinalIgnoreCase));
        string? subcommand = argsList[0].ToLowerInvariant();

        // Dashboard always shows all servers
        if (subcommand is "dashboard" or "dash")
        {
            return HandleDashboard();
        }

        CoreConnection? conn = ResolveConnection(profileName);
        if (conn == null)
        {
            return CommandResult.Fail(
                profileName != null
                    ? $"Connection '{profileName}' not found."
                    : "No active connection. Use 'connect <profile>' first.");
        }

        if (!conn.IsConnected)
        {
            return CommandResult.Fail($"Connection '{conn.Name}' is not connected.");
        }

        return subcommand switch
        {
            "status" or "stat" => HandleStatus(conn),
            "license" or "lic" => HandleLicense(conn),
            "health" or "hp" => HandleHealth(conn),
            "restart" => HandleRestart(conn, confirmFlag),
            "restart-update" => HandleRestartUpdate(conn, confirmFlag),
            "clear-orders" => HandleClearOrders(conn, confirmFlag),
            "clear-archive" => HandleClearArchive(conn, confirmFlag),
            _ => CommandResult.Fail($"Unknown subcommand: '{subcommand}'. Use: status, license, health, restart, restart-update, clear-orders, clear-archive")
        };
    }

    // ── Status ───────────────────────────────────────────────

    private CommandResult HandleStatus(CoreConnection conn)
    {
        CoreStatusSnapshot? status = conn.CoreStatusStore.GetStatus();

        if (status == null)
        {
            return CommandResult.Ok($"[{conn.Name}] No core status data received yet.");
        }

        var data = new
        {
            Server = conn.Name,
            Exchange = conn.Profile.Exchange.ToString(),
            EndPoint = status.EndPoint,
            CoreCPU = $"{status.CoreCpuPercent}%",
            SystemCPU = $"{status.AvgCpuPercent}%",
            SystemRAM = $"{status.AvgMemoryMB} MB / {status.TotalSystemMemoryMB} MB ({status.MemoryUsagePercent}%)",
            CoreRAM = $"{status.CoreUsedMemoryMB} MB",
            FreeRAM = $"{status.FreeMemoryMB} MB",
            Threads = status.CoreThreadCount,
            ExchangeLatency = $"{status.AvgExchangeLatencyMs} ms",
            PeerLatency = $"{status.AvgPeerLatencyMs} ms",
            ApiLoading = status.ApiLoadingSummary,
            UdsStatus = status.UdsStatusSummary,
            LastUpdate = status.Timestamp.ToString("HH:mm:ss UTC")
        };

        return CommandResult.Ok($"[{conn.Name}] Core Status", data);
    }

    // ── License ──────────────────────────────────────────────

    private CommandResult HandleLicense(CoreConnection conn)
    {
        CoreLicenseSnapshot? license = conn.CoreStatusStore.GetLicense();

        if (license == null)
        {
            return CommandResult.Ok($"[{conn.Name}] No license data received yet (sent on initial status update).");
        }

        string? uptimeStr = FormatTimeSpan(license.CoreUptime);
        int licenseDays = license.LicenseDaysRemaining;
        int apiDays = license.ApiKeysDaysRemaining;

        var data = new
        {
            Server = conn.Name,
            LicenseId = license.LicenseId,
            LicenseName = license.LicenseName,
            BuildVersion = license.BuildVersion,
            CoreOS = license.CoreOS,
            CoreUptime = license.CoreUptime > TimeSpan.Zero ? uptimeStr : "N/A",
            LicenseValid = licenseDays == int.MaxValue
                ? "Unlimited"
                : licenseDays >= 0
                    ? $"{licenseDays} days remaining"
                    : $"EXPIRED ({Math.Abs(licenseDays)} days ago)",
            ApiKeysValid = apiDays == int.MaxValue
                ? "No expiry set"
                : apiDays >= 0
                    ? $"{apiDays} days remaining"
                    : $"EXPIRED ({Math.Abs(apiDays)} days ago)",
            UserComment = string.IsNullOrEmpty(license.UserComment) ? "(none)" : license.UserComment,
            ReceivedAt = license.Timestamp.ToString("yyyy-MM-dd HH:mm:ss UTC")
        };

        return CommandResult.Ok($"[{conn.Name}] License Info", data);
    }

    // ── Health ───────────────────────────────────────────────

    private CommandResult HandleHealth(CoreConnection conn)
    {
        CoreStatusSnapshot? status = conn.CoreStatusStore.GetStatus();
        CoreLicenseSnapshot? license = conn.CoreStatusStore.GetLicense();

        if (status == null)
        {
            return CommandResult.Ok($"[{conn.Name}] No status data yet — cannot assess health.");
        }

        var issues = new List<string>();

        // Check critical metrics
        if (status.CoreCpuPercent > 90)
        {
            issues.Add($"⚠ Core CPU critical: {status.CoreCpuPercent}%");
        }
        else if (status.CoreCpuPercent > 70)
        {
            issues.Add($"⚡ Core CPU high: {status.CoreCpuPercent}%");
        }

        if (status.MemoryUsagePercent > 90)
        {
            issues.Add($"⚠ System RAM critical: {status.MemoryUsagePercent}% ({status.AvgMemoryMB} MB used)");
        }
        else if (status.MemoryUsagePercent > 80)
        {
            issues.Add($"⚡ System RAM high: {status.MemoryUsagePercent}% ({status.AvgMemoryMB} MB used)");
        }

        if (status.FreeMemoryMB < 100)
        {
            issues.Add($"⚠ Free RAM very low: {status.FreeMemoryMB} MB");
        }
        else if (status.FreeMemoryMB < 500)
        {
            issues.Add($"⚡ Free RAM low: {status.FreeMemoryMB} MB");
        }

        if (status.AvgExchangeLatencyMs > 1000)
        {
            issues.Add($"⚠ Exchange latency high: {status.AvgExchangeLatencyMs} ms");
        }
        else if (status.AvgExchangeLatencyMs > 500)
        {
            issues.Add($"⚡ Exchange latency elevated: {status.AvgExchangeLatencyMs} ms");
        }

        if (status.AvgPeerLatencyMs > 500)
        {
            issues.Add($"⚠ Peer latency high: {status.AvgPeerLatencyMs} ms");
        }

        // Check UDS status
        foreach (KeyValuePair<MarketType, bool> kvp in status.UdsStatus)
        {
            if (!kvp.Value)
            {
                issues.Add($"⚠ UDS disconnected for {kvp.Key}");
            }
        }

        // Check API loading
        foreach (KeyValuePair<MarketType, short> kvp in status.ApiLoading)
        {
            if (kvp.Value > 90)
            {
                issues.Add($"⚠ API rate limit near capacity for {kvp.Key}: {kvp.Value}%");
            }
            else if (kvp.Value > 70)
            {
                issues.Add($"⚡ API usage high for {kvp.Key}: {kvp.Value}%");
            }
        }

        // Check license
        if (license != null)
        {
            int licenseDays = license.LicenseDaysRemaining;
            if (licenseDays < 0 && licenseDays != int.MaxValue)
            {
                issues.Add("⚠ LICENSE EXPIRED!");
            }
            else if (licenseDays < 7 && licenseDays != int.MaxValue)
            {
                issues.Add($"⚡ License expires in {licenseDays} days");
            }

            int apiDays = license.ApiKeysDaysRemaining;
            if (apiDays < 0 && apiDays != int.MaxValue)
            {
                issues.Add("⚠ API keys expired!");
            }
            else if (apiDays < 7 && apiDays != int.MaxValue)
            {
                issues.Add($"⚡ API keys expire in {apiDays} days");
            }
        }

        string healthStatus;
        if (issues.Count == 0)
        {
            healthStatus = "✅ HEALTHY";
        }
        else
        {
            bool hasWarning = false;
            for (int i = 0; i < issues.Count; i++)
            {
                if (issues[i].StartsWith("⚠"))
                {
                    hasWarning = true;
                    break;
                }
            }
            healthStatus = hasWarning ? "❌ ISSUES DETECTED" : "⚡ WARNINGS";
        }

        string? report = $"[{conn.Name}] Health: {healthStatus}\n";
        if (issues.Count > 0)
        {
            string[]? issueParts = new string[issues.Count];
            for (int i = 0; i < issues.Count; i++)
            {
                issueParts[i] = $"  {issues[i]}";
            }
            report += string.Join("\n", issueParts);
        }
        else
        {
            report += $"  CPU: {status.CoreCpuPercent}% | " +
                      $"RAM: {status.AvgMemoryMB} MB ({status.MemoryUsagePercent}%, {status.FreeMemoryMB} MB free) | " +
                      $"Exchange: {status.AvgExchangeLatencyMs}ms | " +
                      $"Peer: {status.AvgPeerLatencyMs}ms";
        }

        var healthData = new
        {
            server = conn.Name,
            status = healthStatus,
            healthy = issues.Count == 0,
            metrics = new
            {
                coreCpuPercent = status.CoreCpuPercent,
                memoryMB = status.AvgMemoryMB,
                memoryPercent = status.MemoryUsagePercent,
                freeMemoryMB = status.FreeMemoryMB,
                exchangeLatencyMs = status.AvgExchangeLatencyMs,
                peerLatencyMs = status.AvgPeerLatencyMs,
                coreThreadCount = status.CoreThreadCount,
                udsStatus = status.UdsStatus,
                apiLoading = status.ApiLoading
            },
            license = license != null ? new
            {
                daysRemaining = license.LicenseDaysRemaining,
                apiKeysDaysRemaining = license.ApiKeysDaysRemaining,
                version = license.BuildVersion
            } : null,
            issues = issues
        };

        return CommandResult.Ok(report, healthData);
    }

    // ── Dashboard ────────────────────────────────────────────

    private CommandResult HandleDashboard()
    {
        IReadOnlyList<CoreConnection>? connections = _manager.GetAll();

        if (connections.Count == 0)
        {
            return CommandResult.Ok("No connections. Use 'connect <profile>' to connect.");
        }

        var rows = new List<object>();
        for (int ci = 0; ci < connections.Count; ci++)
        {
            CoreConnection? conn = connections[ci];
            CoreStatusSnapshot? status = conn.CoreStatusStore.GetStatus();
            CoreLicenseSnapshot? license = conn.CoreStatusStore.GetLicense();
            AccountStore? acct = conn.AccountStore;

            int runningAlgos = 0;
            IReadOnlyList<MTShared.Network.AlgorithmData>? allAlgos = conn.AlgoStore.GetAll();
            for (int ai = 0; ai < allAlgos.Count; ai++)
            {
                if (allAlgos[ai].isRunning)
                {
                    runningAlgos++;
                }
            }

            rows.Add(new
            {
                Server = conn.Name + (_manager.ActiveConnectionName == conn.Name ? " *" : ""),
                Exchange = conn.Profile.Exchange.ToString(),
                Status = conn.IsConnected ? "ONLINE" : "OFFLINE",
                Uptime = FormatTimeSpan(conn.Uptime),
                CPU = status != null ? $"{status.CoreCpuPercent}%" : "?",
                RAM = status != null ? $"{status.AvgMemoryMB}MB" : "?",
                FreeRAM = status != null ? $"{status.FreeMemoryMB}MB" : "?",
                ExchLat = status != null ? $"{status.AvgExchangeLatencyMs}ms" : "?",
                Algos = $"{runningAlgos}/{conn.AlgoStore.Count}",
                Positions = $"{acct.OpenPositionCount}",
                Orders = $"{acct.ActiveOrderCount}",
                Balance = acct.GetTotalBalanceUSDT() > 0
                    ? $"${acct.GetTotalBalanceUSDT():N0}"
                    : "?",
                Version = license?.BuildVersion ?? "?",
                Pairs = conn.ExchangeInfoStore.TradePairCount > 0
                    ? $"{conn.ExchangeInfoStore.TradePairCount}"
                    : "?"
            });
        }

        int online = 0;
        for (int ci = 0; ci < connections.Count; ci++)
        {
            if (connections[ci].IsConnected)
            {
                online++;
            }
        }
        string? header = $"Core Dashboard — {online}/{connections.Count} servers online";
        return CommandResult.Ok(header, rows);
    }

    // ── Helpers ──────────────────────────────────────────────

    private CoreConnection? ResolveConnection(string? profileName)
    {
        if (profileName != null)
        {
            return _manager.Get(profileName);
        }

        return _manager.ActiveConnection;
    }

    private static string FormatTimeSpan(TimeSpan ts) =>
        ts.TotalDays >= 1 ? $"{(int)ts.TotalDays}d {ts.Hours}h"
        : ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
        : $"{ts.Minutes}m {ts.Seconds}s";

    private CommandResult HandleRestart(CoreConnection conn, bool confirmed)
    {
        if (!confirmed)
        {
            return CommandResult.Fail("Core restart requires --confirm flag. This will restart the trading core.");
        }
        return ExecuteRestartWithProbe(conn, "restart", () => conn.SendCoreRestart());
    }

    private CommandResult HandleRestartUpdate(CoreConnection conn, bool confirmed)
    {
        if (!confirmed)
        {
            return CommandResult.Fail("Core restart-update requires --confirm flag. This will restart with update.");
        }
        return ExecuteRestartWithProbe(conn, "restart-update", () => conn.SendCoreRestartWithUpdate());
    }

    private CommandResult HandleClearOrders(CoreConnection conn, bool confirmed)
    {
        if (!confirmed)
        {
            return CommandResult.Fail("Core clear-orders requires --confirm flag. This will clear orders cache and restart.");
        }
        return ExecuteRestartWithProbe(conn, "clear-orders", () => conn.SendCoreClearOrdersCache());
    }

    /// <summary>
    /// MCP-009 mitigation: send a restart-class command and synchronously verify the Core comes
    /// back within a reasonable window. Hooks <see cref="CoreConnection.OnCoreRestarted"/>
    /// (fired when MTCore reconnects with a new connectionId / serverStartTime) and falls back
    /// to LastSeen-based liveness if the event signal is missed.
    ///
    /// Outcomes:
    ///   ✅ Core came back              → CommandResult.Ok
    ///   ⚠ Core was reachable but no restart event observed → CommandResult.Ok with warning text
    ///   ❌ Core never came back within timeout → CommandResult.Fail (this is the MCP-009 case:
    ///        on macOS Rosetta the spawned core can crash; agents now see success:false instead
    ///        of a misleading "command sent" success).
    /// </summary>
    private CommandResult ExecuteRestartWithProbe(CoreConnection conn, string label, Action sendCommand)
    {
        // Probe window: long enough for a real restart on a slow box but not so long it blocks
        // the MCP caller indefinitely. 12s matches the empirical p95 cold-start of MTCore.
        TimeSpan timeout = TimeSpan.FromSeconds(12);

        TaskCompletionSource<bool> restartedTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<CoreConnection> handler = _ => restartedTcs.TrySetResult(true);

        DateTime preSendUtc = DateTime.UtcNow;
        conn.OnCoreRestarted += handler;
        try
        {
            sendCommand();

            // Wait for the OnCoreRestarted event (preferred signal).
            bool gotEvent = restartedTcs.Task.Wait(timeout);
            if (gotEvent)
            {
                return CommandResult.Ok(
                    $"[{conn.Name}] Core {label} command sent and Core reconnected (new connectionId observed).");
            }

            // Event missed — fall back to LastSeen-based liveness check via the health record.
            ConnectionHealthRecord? health = _manager.GetHealthRecord(conn.Name);
            if (health is not null && conn.IsConnected &&
                (DateTime.UtcNow - health.LastSeen) < TimeSpan.FromSeconds(15) &&
                health.LastSeen > preSendUtc)
            {
                return CommandResult.Ok(
                    $"[{conn.Name}] Core {label} command sent. Core appears reachable " +
                    $"(LastSeen={health.LastSeen:HH:mm:ss}Z) but no restart event observed within {timeout.TotalSeconds:F0}s — " +
                    $"verify with 'core status'.");
            }

            // Hard failure: this is the MCP-009 symptom (Mac Rosetta crash, etc.).
            return CommandResult.Fail(
                $"[{conn.Name}] Core {label} command sent but Core did NOT come back within {timeout.TotalSeconds:F0}s. " +
                $"IsConnected={conn.IsConnected}, LastSeen={(health?.LastSeen.ToString("HH:mm:ss") ?? "n/a")}Z. " +
                $"Likely the Core process crashed on restart (see MCP-009: Mac Rosetta x64 crash). " +
                $"Inspect ~/.config/moontrader/logs/ for stack traces and try re-launching the Core manually.");
        }
        finally
        {
            conn.OnCoreRestarted -= handler;
        }
    }

    private CommandResult HandleClearArchive(CoreConnection conn, bool confirmed)
    {
        if (!confirmed)
        {
            return CommandResult.Fail("Core clear-archive requires --confirm flag. This will clear archive data and restart.");
        }

        conn.SendCoreClearArchiveData();
        return CommandResult.Ok($"[{conn.Name}] Core clear-archive-data and restart command sent.");
    }

}
