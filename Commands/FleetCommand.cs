using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using MTTextClient.Core;
using MTTextClient.Output;
using MTShared.Network;
using MTShared.Types;
using System.Diagnostics;
using System.Threading.Tasks;
namespace MTTextClient.Commands;

/// <summary>
/// Fleet-wide commands — operate across ALL connections simultaneously.
/// Designed for AI agents managing 16–1000+ servers.
///
/// Subcommands:
///   fleet connect              — connect to all profiles at once
///   fleet status               — status of all connections (compact)
///   fleet balances             — balances across all connected servers
///   fleet positions            — open positions across all servers
///   fleet algos                — algorithm summary across all servers
///   fleet health               — health check across all servers
///   fleet summary              — comprehensive fleet overview (one-shot)
///   fleet disconnect           — disconnect from all servers
///   fleet batchconnect          — connect to specific named profiles in parallel (MT-004)
///
/// Optimized for 900+ servers with parallel operations throughout.
/// </summary>
public sealed class FleetCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "fleet";
    public string Description => "Fleet-wide operations across ALL connected servers";
    public string Usage => "fleet <connect|status|balances|positions|algos|health|summary|disconnect>";

    public FleetCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(
                "Usage: fleet <subcommand>\n" +
                "  connect      — connect to all configured profiles\n" +
                "  status       — connection status overview\n" +
                "  balances     — balances across all servers\n" +
                "  positions    — open positions across all servers\n" +
                "  algos        — algorithm summary across all servers\n" +
                "  health       — health check all servers\n" +
                "  summary      — comprehensive fleet overview\n" +
                "  disconnect   — disconnect from all servers\n" +
                "  connhealth   — connection pool health: latency, errors, reconnect state\n" +
                "  batchconnect — connect to specific named profiles in parallel");
        }

        string? subcommand = args[0].ToLowerInvariant();

        return subcommand switch
        {
            "connect" or "conn" => HandleConnect(args.Length > 1 ? args[1..] : Array.Empty<string>()),
            "status" or "stat" => HandleStatus(),
            "balances" or "bal" => HandleBalances(),
            "positions" or "pos" => HandlePositions(),
            "algos" or "algorithms" => HandleAlgos(),
            "health" or "hp" => HandleHealth(),
            "summary" or "sum" => HandleSummary(),
            "disconnect" or "disc" => HandleDisconnect(),
            "batchconnect" or "bc" => HandleBatchConnect(args.Length > 1 ? args[1..] : Array.Empty<string>()),
            "connhealth" or "ch" => HandleConnectionHealth(),
            "batchstart" or "bsa" => HandleBatchAlgoOp(args.Length > 1 ? args[1..] : Array.Empty<string>(), AlgorithmData.ActionType.START),
            "batchstop"  or "bso" => HandleBatchAlgoOp(args.Length > 1 ? args[1..] : Array.Empty<string>(), AlgorithmData.ActionType.STOP),
            "batchconfig" or "bcc" => HandleBatchConfig(args.Length > 1 ? args[1..] : Array.Empty<string>()),
            "autostops" or "as" => HandleFleetAutoStops(),
            "blacklist" or "bl" => HandleFleetBlacklist(),
            "perf" or "performance" => HandleFleetPerformance(),
            "reports" or "rep" => HandleFleetReports(args.Length > 1 ? args[1..] : Array.Empty<string>()),
            _ => CommandResult.Fail($"Unknown fleet subcommand: '{subcommand}'. Use: connect, status, balances, positions, algos, health, summary, disconnect, autostops, blacklist, perf, reports")
        };
    }

    // ── Connect All ─────────────────────────────────────────
    // Parallel connect with semaphore throttling + smart settle

    private CommandResult HandleConnect(string[] args)
    {
        List<ServerProfile>? profiles = ProfileManager.LoadProfiles();
        if (profiles.Count == 0)
        {
            return CommandResult.Fail("No profiles configured in profiles.json.");
        }

        // If filter args provided, filter by exchange or name pattern
        if (args.Length > 0)
        {
            string? filter = args[0].ToLowerInvariant();
            var filtered = new List<ServerProfile>();
            foreach (ServerProfile p in profiles)
            {
                if (p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    p.Exchange.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    filtered.Add(p);
                }
            }
            profiles = filtered;

            if (profiles.Count == 0)
            {
                return CommandResult.Fail($"No profiles match filter '{args[0]}'.");
            }
        }

        int success = 0, failed = 0, alreadyConnected = 0;
        var results = new ConcurrentBag<(string name, string exchange, string status)>();

        // Parallel connect with semaphore — 100 concurrent connections max
        const int MAX_CONCURRENT = 100;
        var semaphore = new SemaphoreSlim(MAX_CONCURRENT);

        var tasks = new List<Task>(profiles.Count);
        foreach (ServerProfile profile in profiles)
        {
            tasks.Add(Task.Run(() =>
            {
                semaphore.Wait();
                try
                {
                    CoreConnection? existing = _manager.Get(profile.Name);
                    if (existing?.IsConnected == true)
                    {
                        Interlocked.Increment(ref alreadyConnected);
                        results.Add((profile.Name, profile.Exchange.ToString(), "ALREADY_CONNECTED"));
                        return;
                    }

                    CoreConnection? conn = _manager.Connect(profile);
                    if (conn != null)
                    {
                        Interlocked.Increment(ref success);
                        results.Add((profile.Name, profile.Exchange.ToString(), "CONNECTING"));
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                        results.Add((profile.Name, profile.Exchange.ToString(), "FAILED"));
                    }
                }
                finally { semaphore.Release(); }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Smart settle: poll every 200ms until connection count stabilizes or timeout
        int targetOnline = success;
        int settleMs = Math.Min(30000, 3000 + profiles.Count * 20); // Scale settle time with count
        int elapsed = 0;
        int lastOnline = 0;
        int stableCount = 0;

        while (elapsed < settleMs)
        {
            Thread.Sleep(200);
            elapsed += 200;
            int currentOnline = _manager.ConnectedCount;

            if (currentOnline >= targetOnline)
            {
                break; // All connected
            }

            if (currentOnline == lastOnline)
            {
                stableCount++;
                if (stableCount >= 5)
                {
                    break; // Stabilized for 1 second
                }
            }
            else
            {
                stableCount = 0;
            }
            lastOnline = currentOnline;
        }

        int connected = _manager.ConnectedCount;
        string? header = $"Fleet Connect — {connected}/{profiles.Count} online" +
                     $" (new: {success}, already: {alreadyConnected}, failed: {failed}, settle: {elapsed}ms)";

        // Convert results to anonymous objects for JSON
        var sortedResults = new List<(string name, string exchange, string status)>();
        foreach ((string name, string exchange, string status) r in results)
        {
            sortedResults.Add(r);
        }

        sortedResults.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));
        var rows = new List<object>(sortedResults.Count);
        foreach ((string name, string exchange, string status) r in sortedResults)
        {
            rows.Add(new
            {
                Server = r.name,
                Exchange = r.exchange,
                Status = _manager.Get(r.name)?.IsConnected == true ? "ONLINE" : r.status
            });
        }

        return CommandResult.Ok(header, rows);
    }

    // ── Status ──────────────────────────────────────────────
    // Parallel read — all stores are ConcurrentDictionary (thread-safe)

    private CommandResult HandleStatus()
    {
        CoreConnection[] connections = _manager.GetAllArray();
        if (connections.Length == 0)
        {
            return CommandResult.Ok("Fleet: No connections. Use 'fleet connect' to connect all servers.");
        }

        var rows = new ConcurrentBag<(string name, object row)>();

        Parallel.ForEach(connections, conn =>
        {
            rows.Add((conn.Name, new
            {
                Server = conn.Name,
                Exchange = conn.Profile.Exchange.ToString(),
                Status = conn.IsConnected ? "ONLINE" : "OFFLINE",
                Uptime = conn.IsConnected ? FormatUptime(conn.Uptime) : "-",
                Algos = FormatAlgoCount(conn),
                Active = conn.Name.Equals(_manager.ActiveConnectionName, StringComparison.OrdinalIgnoreCase) ? "◄" : ""
            }));
        });

        int online = 0;
        foreach (CoreConnection c in connections)
        {
            if (c.IsConnected)
            {
                online++;
            }
        }

        return CommandResult.Ok($"Fleet Status — {online}/{connections.Length} online",
            SortAndExtractRows(rows));
    }

    // ── Balances ─────────────────────────────────────────────

    private CommandResult HandleBalances()
    {
        CoreConnection[] allConns = _manager.GetAllArray();
        var connList = new List<CoreConnection>();
        foreach (CoreConnection c in allConns)
        {
            if (c.IsConnected)
            {
                connList.Add(c);
            }
        }

        CoreConnection[]? connections = connList.ToArray();
        if (connections.Length == 0)
        {
            return CommandResult.Ok("Fleet: No connected servers. Use 'fleet connect' first.");
        }

        var rows = new ConcurrentBag<(string name, double usdt, object row)>();

        Parallel.ForEach(connections, conn =>
        {
            double totalUsdt = conn.AccountStore.GetTotalBalanceUSDT();
            IReadOnlyList<BalanceSnapshot>? balances = conn.AccountStore.GetBalances(false);
            int posCount = conn.AccountStore.OpenPositionCount;
            int orderCount = conn.AccountStore.ActiveOrderCount;

            if (totalUsdt <= 0 && balances.Count > 0)
            { foreach (BalanceSnapshot b in balances)
                {
                    totalUsdt += b.EstimationUSDT > 0 ? b.EstimationUSDT : b.Total;
                }
            }

            var sortedBals = new List<BalanceSnapshot>(balances);
            sortedBals.Sort((a, b2) =>
            {
                double va = a.EstimationUSDT > 0 ? a.EstimationUSDT : a.Total;
                double vb = b2.EstimationUSDT > 0 ? b2.EstimationUSDT : b2.Total;
                return vb.CompareTo(va);
            });
            int topCount = Math.Min(sortedBals.Count, 3);
            var topAssets = new List<string>(topCount);
            for (int ti = 0; ti < topCount; ti++)
            {
                BalanceSnapshot? b2 = sortedBals[ti];
                topAssets.Add($"{b2.Asset}=${(b2.EstimationUSDT > 0 ? b2.EstimationUSDT : b2.Total):N0}");
            }

            bool hasBalanceData = conn.AccountStore.LastBalanceUpdate != default;
            rows.Add((conn.Name, totalUsdt, new
            {
                Server = conn.Name,
                Exchange = conn.Profile.Exchange.ToString(),
                TotalUSDT = totalUsdt > 0 ? $"${totalUsdt:N2}" : (hasBalanceData ? "$0.00" : "pending"),
                Assets = balances.Count,
                TopHoldings = topAssets.Count > 0 ? string.Join(", ", topAssets) : "-",
                Positions = posCount,
                Orders = orderCount
            }));
        });

        double grandTotal = 0;
        foreach ((string name, double usdt, object row) r in rows)
        {
            grandTotal += r.usdt;
        }

        return CommandResult.Ok(
            $"Fleet Balances — {connections.Length} servers | Grand Total: ${grandTotal:N2} USDT",
            SortAndExtractRows(rows));
    }

    // ── Positions ────────────────────────────────────────────

    private CommandResult HandlePositions()
    {
        CoreConnection[] allConns = _manager.GetAllArray();
        var connList = new List<CoreConnection>();
        foreach (CoreConnection c in allConns)
        {
            if (c.IsConnected)
            {
                connList.Add(c);
            }
        }

        CoreConnection[]? connections = connList.ToArray();
        if (connections.Length == 0)
        {
            return CommandResult.Ok("Fleet: No connected servers.");
        }

        var rows = new ConcurrentBag<(string name, object row)>();



        Parallel.ForEach(connections, conn =>
        {
            IReadOnlyList<PositionSnapshot>? positions = conn.AccountStore.GetPositions(openOnly: true);
            foreach (PositionSnapshot p in positions)
            {
                // Atomic double add not available, use bag and sum later
                rows.Add((conn.Name, new
                {
                    Server = conn.Name,
                    Exchange = conn.Profile.Exchange.ToString(),
                    Symbol = p.Symbol,
                    Side = p.PositionSide.ToString(),
                    Amount = FormatDecimal(p.Amount),
                    EntryPrice = FormatDecimal(p.EntryPrice),
                    Leverage = $"{p.Leverage}x",
                    UnrealizedPnl = FormatPnl(p.UnrealizedPnl),
                    PnlPct = $"{p.PnlPercent:+0.00;-0.00}%",
                    Margin = FormatDecimal(p.Margin)
                }));
            }
        });

        // Sum PnL from positions data
        int posCount = rows.Count;
        if (posCount == 0)
        {
            return CommandResult.Ok($"Fleet Positions — 0 open positions across {connections.Length} servers.");
        }

        return CommandResult.Ok(
            $"Fleet Positions — {posCount} open across {connections.Length} servers",
            SortAndExtractRows(rows));
    }

    // ── Algos ────────────────────────────────────────────────

    private CommandResult HandleAlgos()
    {
        CoreConnection[] allConns = _manager.GetAllArray();
        var connList = new List<CoreConnection>();
        foreach (CoreConnection c in allConns)
        {
            if (c.IsConnected)
            {
                connList.Add(c);
            }
        }

        CoreConnection[]? connections = connList.ToArray();
        if (connections.Length == 0)
        {
            return CommandResult.Ok("Fleet: No connected servers.");
        }

        var rows = new ConcurrentBag<(string name, int algos, int running, object row)>();

        Parallel.ForEach(connections, conn =>
        {
            IReadOnlyList<AlgorithmData>? algoArr = conn.AlgoStore.GetAll();
            var algos = new List<AlgorithmData>(algoArr.Count);
            foreach (AlgorithmData a in algoArr)
            {
                algos.Add(a);
            }

            int running = 0;
            foreach (AlgorithmData a in algos)
            {
                if (a.isRunning)
                {
                    running++;
                }
            }

            var sigGroups = new Dictionary<string, (int Count, int Running)>();
            foreach (AlgorithmData a in algos)
            {
                string key = a.signature ?? "unknown";
                if (!sigGroups.TryGetValue(key, out (int Count, int Running) grp))
                {
                    grp = (0, 0);
                }

                grp.Count++;
                if (a.isRunning)
                {
                    grp.Running++;
                }

                sigGroups[key] = grp;
            }
            var bySignature = new List<(string Type, int Count, int Running)>(sigGroups.Count);
            foreach (KeyValuePair<string, (int Count, int Running)> kvp in sigGroups)
            {
                bySignature.Add((kvp.Key, kvp.Value.Count, kvp.Value.Running));
            }

            bySignature.Sort((a2, b2) => b2.Count.CompareTo(a2.Count));

            int sigLimit = Math.Min(bySignature.Count, 5);
            var sigSummary = new List<string>(sigLimit);
            for (int si = 0; si < sigLimit; si++)
            {
                (string Type, int Count, int Running) g = bySignature[si];
                sigSummary.Add($"{g.Type}:{g.Running}/{g.Count}");
            }

            rows.Add((conn.Name, algos.Count, running, new
            {
                Server = conn.Name,
                Exchange = conn.Profile.Exchange.ToString(),
                Total = algos.Count,
                Running = running,
                Stopped = algos.Count - running,
                Types = sigSummary.Count > 0 ? string.Join(", ", sigSummary) : "-"
            }));
        });

        int totalAlgos = 0, totalRunning = 0;
        foreach ((string name, int algos, int running, object row) r in rows) { totalAlgos += r.algos; totalRunning += r.running; }
        return CommandResult.Ok(
            $"Fleet Algos — {totalAlgos} total, {totalRunning} running across {connections.Length} servers",
            SortAndExtractRows(rows));
    }

    // ── Health ───────────────────────────────────────────────

    private CommandResult HandleHealth()
    {
        CoreConnection[] allConns = _manager.GetAllArray();
        var connList = new List<CoreConnection>();
        foreach (CoreConnection c in allConns)
        {
            if (c.IsConnected)
            {
                connList.Add(c);
            }
        }

        CoreConnection[]? connections = connList.ToArray();
        if (connections.Length == 0)
        {
            return CommandResult.Ok("Fleet: No connected servers.");
        }

        int healthy = 0, warnings = 0, critical = 0;
        var rows = new ConcurrentBag<(string name, object row)>();

        Parallel.ForEach(connections, conn =>
        {
            CoreStatusSnapshot? status = conn.CoreStatusStore.GetStatus();
            CoreLicenseSnapshot? license = conn.CoreStatusStore.GetLicense();
            var issues = new List<string>();

            if (status == null)
            {
                rows.Add((conn.Name, new
                {
                    Server = conn.Name,
                    Health = "?",
                    CPU = "?",
                    RAM = "?",
                    FreeRAM = "?",
                    ExchLatency = "?",
                    UDS = "?",
                    License = "?",
                    Issues = "no data yet"
                }));
                return;
            }

            if (status.CoreCpuPercent > 90)
            {
                issues.Add("CPU>90%");
            }
            else if (status.CoreCpuPercent > 70)
            {
                issues.Add("CPU>70%");
            }

            if (status.MemoryUsagePercent > 90)
            {
                issues.Add("RAM>90%");
            }
            else if (status.MemoryUsagePercent > 80)
            {
                issues.Add("RAM>80%");
            }

            if (status.FreeMemoryMB < 100)
            {
                issues.Add("FreeRAM<100MB");
            }

            if (status.AvgExchangeLatencyMs > 1000)
            {
                issues.Add("Latency>1s");
            }
            else if (status.AvgExchangeLatencyMs > 500)
            {
                issues.Add("Latency>500ms");
            }

            foreach (KeyValuePair<MTShared.Types.MarketType, bool> kvp in status.UdsStatus)
            {
                if (!kvp.Value)
                {
                    issues.Add($"UDS:{kvp.Key}=OFF");
                }
            }

            if (license != null)
            {
                int licenseDays = license.LicenseDaysRemaining;
                if (licenseDays < 0 && licenseDays != int.MaxValue)
                {
                    issues.Add("LICENSE_EXPIRED");
                }
                else if (licenseDays < 7 && licenseDays != int.MaxValue)
                {
                    issues.Add($"LIC:{licenseDays}d");
                }
            }

            string? health = issues.Count == 0 ? "✅"
                : AnyIssueContains(issues, "EXPIRED", ">90", "<100") ? "❌" : "⚡";

            if (health == "✅")
            {
                Interlocked.Increment(ref healthy);
            }
            else if (health == "❌")
            {
                Interlocked.Increment(ref critical);
            }
            else
            {
                Interlocked.Increment(ref warnings);
            }

            rows.Add((conn.Name, new
            {
                Server = conn.Name,
                Health = health,
                CPU = $"{status.CoreCpuPercent}%",
                RAM = $"{status.AvgMemoryMB}MB ({status.MemoryUsagePercent}%)",
                FreeRAM = $"{status.FreeMemoryMB}MB",
                ExchLatency = $"{status.AvgExchangeLatencyMs}ms",
                UDS = AllUdsOk(status.UdsStatus) ? "OK" : "PARTIAL",
                License = license?.LicenseDaysRemaining == int.MaxValue ? "∞"
                    : license != null ? $"{license.LicenseDaysRemaining}d" : "?",
                Issues = issues.Count > 0 ? string.Join(", ", issues) : "none"
            }));
        });

        return CommandResult.Ok(
            $"Fleet Health — ✅ {healthy} healthy, ⚡ {warnings} warnings, ❌ {critical} critical ({connections.Length} servers)",
            SortAndExtractRows(rows));
    }

    // ── Summary (the mega-overview) ──────────────────────────

    private CommandResult HandleSummary()
    {
        List<ServerProfile>? profiles = ProfileManager.LoadProfiles();
        CoreConnection[] connections = _manager.GetAllArray();
        var connectedList = new List<CoreConnection>();
        foreach (CoreConnection c in connections)
        {
            if (c.IsConnected)
            {
                connectedList.Add(c);
            }
        }

        CoreConnection[]? connected = connectedList.ToArray();

        double grandTotalBalance = 0;
        double grandTotalPnl = 0;
        int grandTotalAlgos = 0;
        int grandTotalRunning = 0;
        int grandTotalPositions = 0;
        int grandTotalOrders = 0;

        // Per-exchange aggregation — use ConcurrentDictionary for thread safety
        var exchangeStats = new ConcurrentDictionary<string, (double balance, int algos, int running, int positions, int servers)>();

        Parallel.ForEach(connected, conn =>
        {
            double bal = conn.AccountStore.GetTotalBalanceUSDT();
            if (bal <= 0)
            {
                IReadOnlyList<BalanceSnapshot>? rawBalances = conn.AccountStore.GetBalances(false);
                if (rawBalances.Count > 0)
                { foreach (BalanceSnapshot b in rawBalances)
                    {
                        bal += b.EstimationUSDT > 0 ? b.EstimationUSDT : b.Total;
                    }
                }
            }
            int algos = conn.AlgoStore.Count;
            int running = 0;
            foreach (AlgorithmData a2 in conn.AlgoStore.GetAll())
            {
                if (a2.isRunning)
                {
                    running++;
                }
            }

            int positions = conn.AccountStore.OpenPositionCount;
            int orders = conn.AccountStore.ActiveOrderCount;
            double pnl = 0;
            foreach (PositionSnapshot p in conn.AccountStore.GetPositions(openOnly: true))
            {
                pnl += p.UnrealizedPnl;
            }

            // Thread-safe accumulation
            Interlocked.Add(ref grandTotalAlgos, algos);
            Interlocked.Add(ref grandTotalRunning, running);
            Interlocked.Add(ref grandTotalPositions, positions);
            Interlocked.Add(ref grandTotalOrders, orders);

            // For doubles, use lock-free pattern
            double oldBal, newBal;
            do { oldBal = grandTotalBalance; newBal = oldBal + bal; }
            while (Interlocked.CompareExchange(ref grandTotalBalance, newBal, oldBal) != oldBal);

            double oldPnl, newPnl;
            do { oldPnl = grandTotalPnl; newPnl = oldPnl + pnl; }
            while (Interlocked.CompareExchange(ref grandTotalPnl, newPnl, oldPnl) != oldPnl);

            string? exName = conn.Profile.Exchange.ToString();
            exchangeStats.AddOrUpdate(exName,
                _ => (bal, algos, running, positions, 1),
                (_, s) => (s.balance + bal, s.algos + algos, s.running + running, s.positions + positions, s.servers + 1));
        });

        var exchangeRows = new List<object>(exchangeStats.Count);
        foreach (KeyValuePair<string, (double balance, int algos, int running, int positions, int servers)> kvp in exchangeStats)
        {
            exchangeRows.Add(new
            {
                Exchange = kvp.Key,
                Servers = kvp.Value.servers,
                Balance = $"${kvp.Value.balance:N2}",
                Algos = $"{kvp.Value.running}/{kvp.Value.algos}",
                Positions = kvp.Value.positions
            });
        }

        var data = new
        {
            ConfiguredProfiles = profiles.Count,
            ConnectedServers = $"{connected.Length}/{connections.Length}",
            OfflineServers = connections.Length - connected.Length,
            GrandTotalBalance = $"${grandTotalBalance:N2}",
            TotalUnrealizedPnl = FormatPnlDouble(grandTotalPnl),
            TotalAlgorithms = $"{grandTotalRunning} running / {grandTotalAlgos} total",
            TotalOpenPositions = grandTotalPositions,
            TotalActiveOrders = grandTotalOrders,
            ExchangeBreakdown = exchangeRows,
            ActiveConnection = _manager.ActiveConnectionName
        };

        return CommandResult.Ok(
            $"═══ Fleet Summary ═══\n" +
            $"  Servers: {connected.Length}/{profiles.Count} online | " +
            $"Balance: ${grandTotalBalance:N2} | " +
            $"PnL: {FormatPnlDouble(grandTotalPnl)} | " +
            $"Algos: {grandTotalRunning}/{grandTotalAlgos} | " +
            $"Positions: {grandTotalPositions}",
            data);
    }

    // ── Disconnect All ──────────────────────────────────────

    private CommandResult HandleDisconnect()
    {
        int count = _manager.ConnectedCount;
        _manager.DisconnectAll();
        return CommandResult.Ok($"Fleet: Disconnected from {count} server(s).");
    }

    // ── Helpers ──────────────────────────────────────────────

    private static string FormatUptime(TimeSpan ts) =>
        ts.TotalDays >= 1 ? $"{(int)ts.TotalDays}d {ts.Hours}h"
        : ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
        : $"{ts.Minutes}m {ts.Seconds}s";

    private static string FormatDecimal(decimal value) =>
        value switch
        {
            >= 1_000m => $"{value:N2}",
            >= 1m => $"{value:N4}",
            >= 0.0001m => $"{value:N6}",
            _ => $"{value:N8}"
        };

    private static string FormatPnl(double value) =>
        value >= 0 ? $"+${value:N2}" : $"-${Math.Abs(value):N2}";

    private static string FormatPnlDouble(double value) =>
        value >= 0 ? $"+${value:N2}" : $"-${Math.Abs(value):N2}";

    private static string FormatAlgoCount(CoreConnection conn)
    {
        int running = 0;
        foreach (AlgorithmData a in conn.AlgoStore.GetAll())
        {
            if (a.isRunning)
            {
                running++;
            }
        }

        return $"{running}/{conn.AlgoStore.Count}";
    }

    private static List<object> SortAndExtractRows(ConcurrentBag<(string name, object row)> bag)
    {
        var list = new List<(string name, object row)>();
        foreach ((string name, object row) item in bag)
        {
            list.Add(item);
        }

        list.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));
        var result = new List<object>(list.Count);
        foreach ((string name, object row) item in list)
        {
            result.Add(item.row);
        }

        return result;
    }

    private static List<object> SortAndExtractRows(ConcurrentBag<(string name, double usdt, object row)> bag)
    {
        var list = new List<(string name, double usdt, object row)>();
        foreach ((string name, double usdt, object row) item in bag)
        {
            list.Add(item);
        }

        list.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));
        var result = new List<object>(list.Count);
        foreach ((string name, double usdt, object row) item in list)
        {
            result.Add(item.row);
        }

        return result;
    }

    private static List<object> SortAndExtractRows(ConcurrentBag<(string name, int algos, int running, object row)> bag)
    {
        var list = new List<(string name, int algos, int running, object row)>();
        foreach ((string name, int algos, int running, object row) item in bag)
        {
            list.Add(item);
        }

        list.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));
        var result = new List<object>(list.Count);
        foreach ((string name, int algos, int running, object row) item in list)
        {
            result.Add(item.row);
        }

        return result;
    }

    private static bool AnyIssueContains(List<string> issues, params string[] patterns)
    {
        foreach (string issue in issues)
        {
            foreach (string pat in patterns)
            {
                if (issue.Contains(pat))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool AllUdsOk(IDictionary<MTShared.Types.MarketType, bool> uds)
    {
        foreach (KeyValuePair<MTShared.Types.MarketType, bool> kvp in uds)
        {
            if (!kvp.Value)
            {
                return false;
            }
        }

        return true;
    }

    // ── Connection Pool Health (MT-003) ─────────────────────

    /// <summary>
    /// Returns connection pool health records for all tracked profiles.
    /// Shows latency, error count, reconnect history, and backoff state per profile.
    /// Profiles remain tracked even while disconnected (backoff state preserved).
    /// </summary>
    private CommandResult HandleConnectionHealth()
    {
        System.Collections.Generic.IReadOnlyList<ConnectionHealthRecord> records =
            _manager.GetHealthRecords();

        if (records.Count == 0)
        {
            return CommandResult.Ok("No connection health records. Use 'connect' or 'fleet connect' first.");
        }

        int healthyCount = 0;
        int unhealthyCount = 0;
        var rows = new System.Collections.Generic.List<object>(records.Count);

        foreach (ConnectionHealthRecord r in records)
        {
            if (r.IsHealthy) healthyCount++; else unhealthyCount++;
            rows.Add(new
            {
                profile = r.ProfileName,
                connected = r.IsConnected,
                healthy = r.IsHealthy,
                latencyMs = r.LatencyMs,
                errorCount = r.ErrorCount,
                reconnectCount = r.ReconnectCount,
                reconnectBackoffSec = r.ReconnectBackoff.TotalSeconds,
                lastSeen = r.LastSeen,
                lastError = r.LastError
            });
        }

        string summary = $"Connection health: {healthyCount} healthy, {unhealthyCount} unhealthy " +
                         $"({records.Count} total tracked).";
        return CommandResult.Ok(summary, new { summary, connections = rows });
    }

    // ── Batch Connect (MT-004) ───────────────────────────────

    /// <summary>
    /// Connect to a specific set of named profiles in parallel.
    /// Unlike <c>fleet connect</c> (which connects all configured profiles), this accepts
    /// an explicit list of profile names — suited for programmatic fleet orchestration.
    /// </summary>
    private CommandResult HandleBatchConnect(string[] profileNames)
    {
        if (profileNames.Length == 0)
        {
            return CommandResult.Fail("Usage: fleet batchconnect <profile1> [profile2] ...");
        }

        List<ServerProfile> allProfiles = ProfileManager.LoadProfiles();
        var targets = new List<ServerProfile>();
        var notFound = new List<string>();

        foreach (string name in profileNames)
        {
            ServerProfile? p = ProfileManager.FindProfile(allProfiles, name);
            if (p != null)
            {
                targets.Add(p);
            }
            else
            {
                notFound.Add(name);
            }
        }

        if (targets.Count == 0)
        {
            return CommandResult.Fail(
                $"No matching profiles found. Not found: {string.Join(", ", notFound)}");
        }

        var results = new ConcurrentBag<object>();
        var sw = Stopwatch.StartNew();

        // Connect in parallel, max 10 concurrent (LiteNetLib limit)
        Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = 10 }, profile =>
        {
            CoreConnection? conn = _manager.Connect(profile);
            results.Add(new
            {
                profile.Name,
                profile.Address,
                profile.Port,
                exchange = profile.Exchange.ToString(),
                connected = conn != null,
                status = conn != null ? "connecting" : "failed"
            });
        });

        sw.Stop();

        int connectedCount = 0;
        var failedList = new List<string>();
        foreach (object r in results)
        {
            // Use reflection to avoid dynamic — check connected field
            var type = r.GetType();
            var connProp = type.GetProperty("connected");
            var nameProp = type.GetProperty("Name");
            bool isConnected = connProp != null && (bool)(connProp.GetValue(r) ?? false);
            if (isConnected)
            {
                connectedCount++;
            }
            else if (nameProp != null)
            {
                failedList.Add(nameProp.GetValue(r)?.ToString() ?? "?");
            }
        }

        string msg = $"Batch connect: {connectedCount}/{targets.Count} initiated in {sw.ElapsedMilliseconds}ms.";
        if (notFound.Count > 0)
        {
            msg += $" (Not found: {string.Join(", ", notFound)})";
        }

        return CommandResult.Ok(msg, new
        {
            totalRequested = profileNames.Length,
            matched = targets.Count,
            connected = connectedCount,
            failed = failedList,
            notFound,
            durationMs = sw.ElapsedMilliseconds
        });
    }


// ── MT-008: Batch Algo Operations ─────────────────────────────────────────
// Start/stop/config an algorithm (by name pattern) across multiple servers.

/// <summary>
/// Resolve a list of target connections from profile name args.
/// If profileNames is empty, returns all currently connected servers.
/// </summary>
private List<CoreConnection> ResolveTargetConnections(string[] profileNames, out List<string> notFound)
{
    notFound = new List<string>();

    if (profileNames.Length == 0)
    {
        // All connected servers
        var all = new List<CoreConnection>();
        foreach (CoreConnection c in _manager.GetAll())
        {
            if (c.IsConnected) all.Add(c);
        }
        return all;
    }

    var targets = new List<CoreConnection>();
    foreach (string name in profileNames)
    {
        CoreConnection? conn = _manager.Get(name);
        if (conn != null && conn.IsConnected)
            targets.Add(conn);
        else
            notFound.Add(name);
    }
    return targets;
}

/// <summary>
/// Execute START or STOP on all algos matching <c>args[0]</c> (name/signature/symbol pattern)
/// across the specified profiles (default: all connected server).
/// args: [algo_pattern, profile1?, profile2?, ...]
/// </summary>
private CommandResult HandleBatchAlgoOp(string[] args, AlgorithmData.ActionType actionType)
{
    string opName = actionType == AlgorithmData.ActionType.START ? "batchstart" : "batchstop";
    if (args.Length < 1)
        return CommandResult.Fail($"Usage: fleet {opName} <algo_pattern> [profile1 ...]");

    string pattern   = args[0];
    string[] profArgs = args.Length > 1 ? args[1..] : Array.Empty<string>();
    List<CoreConnection> targets = ResolveTargetConnections(profArgs, out List<string> notFound);

    if (targets.Count == 0)
        return CommandResult.Fail(
            $"No connected targets found. Not found: {string.Join(", ", notFound.Count > 0 ? notFound : new List<string> { "(none connected)" })}");

    var results = new ConcurrentBag<object>();
    var sw = Stopwatch.StartNew();

    Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = 10 }, conn =>
    {
        IReadOnlyList<MTShared.Network.AlgorithmData> matches = conn.AlgoStore.Search(pattern);
        var algoResults = new List<object>(matches.Count);

        foreach (MTShared.Network.AlgorithmData algo in matches)
        {
            var request = new MTShared.Network.AlgorithmData(algo) { actionType = actionType };

            // Always resolve Core-internal type name for START to avoid silent init failure (BUG-13 mitigation)
            if (actionType == AlgorithmData.ActionType.START)
            {
                string? typeName = AlgoTypeNames.Resolve(algo.signature);
                if (typeName != null) request.name = typeName;
            }

            MTShared.Network.NotificationMessageData? notif = conn.SendAlgorithmRequest(request);
            bool ok = notif == null || notif.IsOk;

            algoResults.Add(new
            {
                id   = algo.id,
                name = algo.name,
                ok,
                msg  = notif?.msgString ?? "timeout (sent)"
            });
        }

        results.Add(new { profile = conn.Name, matched = matches.Count, algos = algoResults });
    });

    sw.Stop();

    int totalMatched = 0;
    foreach (object r in results)
    {
        var prop = r.GetType().GetProperty("matched");
        if (prop != null) totalMatched += (int)(prop.GetValue(r) ?? 0);
    }

    string msg = $"Batch {actionType}: searched '{pattern}', {totalMatched} algo(s) across {targets.Count} server(s) in {sw.ElapsedMilliseconds}ms.";
    if (notFound.Count > 0) msg += $" (Profiles not found: {string.Join(", ", notFound)})";

    return CommandResult.Ok(msg, new
    {
        action     = actionType.ToString(),
        pattern,
        servers    = targets.Count,
        totalMatched,
        notFound,
        durationMs = sw.ElapsedMilliseconds,
        results    = results.ToArray()
    });
}

/// <summary>
/// Set a config parameter locally on all algos matching <c>args[0]</c> across the specified profiles.
/// args: [algo_pattern, key, value, profile1?, profile2?, ...]
/// Changes are LOCAL — use 'algos save &lt;id&gt; @&lt;profile&gt;' to persist to Core.
/// </summary>
private CommandResult HandleBatchConfig(string[] args)
{
    if (args.Length < 3)
        return CommandResult.Fail("Usage: fleet batchconfig <algo_pattern> <key> <value> [profile1 ...]");

    string pattern   = args[0];
    string key       = args[1];
    string value     = args[2];
    string[] profArgs = args.Length > 3 ? args[3..] : Array.Empty<string>();
    List<CoreConnection> targets = ResolveTargetConnections(profArgs, out List<string> notFound);

    if (targets.Count == 0)
        return CommandResult.Fail(
            $"No connected targets found. Not found: {string.Join(", ", notFound.Count > 0 ? notFound : new List<string> { "(none connected)" })}");

    var results = new ConcurrentBag<object>();
    var sw = Stopwatch.StartNew();

    Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = 10 }, conn =>
    {
        IReadOnlyList<MTShared.Network.AlgorithmData> matches = conn.AlgoStore.Search(pattern);
        var algoResults = new List<object>(matches.Count);

        foreach (MTShared.Network.AlgorithmData algo in matches)
        {
            (bool ok, string? err) = MTTextClient.Core.AlgorithmStore.UpdateParameter(algo, key, value);
            algoResults.Add(new { id = algo.id, name = algo.name, ok, error = err ?? string.Empty });
        }

        results.Add(new { profile = conn.Name, matched = matches.Count, algos = algoResults });
    });

    sw.Stop();

    int totalMatched = 0;
    foreach (object r in results)
    {
        var prop = r.GetType().GetProperty("matched");
        if (prop != null) totalMatched += (int)(prop.GetValue(r) ?? 0);
    }

    string msg = $"Batch config '{key}={value}' on '{pattern}': {totalMatched} algo(s) updated locally across {targets.Count} server(s) in {sw.ElapsedMilliseconds}ms. Use 'algos save <id> @<profile>' to persist.";
    if (notFound.Count > 0) msg += $" (Profiles not found: {string.Join(", ", notFound)})";

    return CommandResult.Ok(msg, new
    {
        pattern,
        key,
        value,
        servers    = targets.Count,
        totalMatched,
        notFound,
        durationMs = sw.ElapsedMilliseconds,
        results    = results.ToArray()
    });
}


    #region Fleet P4 Extensions

    private CommandResult HandleFleetAutoStops()
    {
        IReadOnlyList<CoreConnection> connections = _manager.GetAll();
        if (connections.Count == 0)
        {
            return CommandResult.Fail("No connections. Use 'fleet connect' first.");
        }

        TableBuilder table = new TableBuilder("Server", "AutoStops", "Status");
        List<object> data = new List<object>();

        foreach (CoreConnection c in connections)
        {
            if (!c.IsConnected)
            {
                table.AddRow(c.Name, "—", "disconnected");
                continue;
            }

            // Read autostop settings from profile settings store
            IReadOnlyList<KeyValuePair<string, string>> settings = c.ProfileSettingsStore.GetAll();
            string balanceFilters = "";
            string reportFilters = "";
            foreach (KeyValuePair<string, string> kvp in settings)
            {
                if (kvp.Key == "AutoStopAlgorithm.Balance.Filters") { balanceFilters = kvp.Value; }
                if (kvp.Key == "AutoStopAlgorithm.Report.Filters") { reportFilters = kvp.Value; }
            }

            bool hasBalance = !string.IsNullOrEmpty(balanceFilters);
            bool hasReport = !string.IsNullOrEmpty(reportFilters);
            string status = hasBalance || hasReport ? "configured" : "none";

            table.AddRow(c.Name,
                $"Bal:{(hasBalance ? "Y" : "N")} Rep:{(hasReport ? "Y" : "N")}",
                status);

            data.Add(new
            {
                Server = c.Name,
                HasBalanceFilters = hasBalance,
                BalanceFilters = balanceFilters ?? "",
                HasReportFilters = hasReport,
                ReportFilters = reportFilters ?? "",
            });
        }

        return CommandResult.Ok(
            $"Fleet AutoStops — {connections.Count} servers\n{table}",
            new { Servers = connections.Count, AutoStops = data });
    }

    private CommandResult HandleFleetBlacklist()
    {
        IReadOnlyList<CoreConnection> connections = _manager.GetAll();
        if (connections.Count == 0)
        {
            return CommandResult.Fail("No connections. Use 'fleet connect' first.");
        }

        TableBuilder table = new TableBuilder("Server", "Markets", "Quotes", "Symbols");
        List<object> data = new List<object>();

        foreach (CoreConnection c in connections)
        {
            if (!c.IsConnected)
            {
                table.AddRow(c.Name, "—", "—", "—");
                continue;
            }

            IReadOnlyList<KeyValuePair<string, string>> settings = c.ProfileSettingsStore.GetAll();
            string markets = "";
            string quotes = "";
            string symbols = "";
            foreach (KeyValuePair<string, string> kvp in settings)
            {
                if (kvp.Key == "BlackList.MarketTypes") { markets = kvp.Value; }
                if (kvp.Key == "BlackList.Quotes") { quotes = kvp.Value; }
                if (kvp.Key == "BlackList.Symbols") { symbols = kvp.Value; }
            }

            int marketCount = string.IsNullOrEmpty(markets) ? 0 : markets.Split(',').Length;
            int quoteCount = string.IsNullOrEmpty(quotes) ? 0 : quotes.Split(',').Length;
            int symbolCount = string.IsNullOrEmpty(symbols) ? 0 : symbols.Split(',').Length;

            table.AddRow(c.Name,
                marketCount > 0 ? marketCount.ToString() : "—",
                quoteCount > 0 ? quoteCount.ToString() : "—",
                symbolCount > 0 ? symbolCount.ToString() : "—");

            data.Add(new
            {
                Server = c.Name,
                MarketCount = marketCount,
                Markets = markets ?? "",
                QuoteCount = quoteCount,
                Quotes = quotes ?? "",
                SymbolCount = symbolCount,
                Symbols = symbols ?? "",
            });
        }

        return CommandResult.Ok(
            $"Fleet Blacklists — {connections.Count} servers\n{table}",
            new { Servers = connections.Count, Blacklists = data });
    }

    private CommandResult HandleFleetPerformance()
    {
        IReadOnlyList<CoreConnection> connections = _manager.GetAll();
        if (connections.Count == 0)
        {
            return CommandResult.Fail("No connections. Use 'fleet connect' first.");
        }

        TableBuilder table = new TableBuilder("Server", "Entries", "Subscribed");
        List<object> data = new List<object>();

        foreach (CoreConnection c in connections)
        {
            if (!c.IsConnected)
            {
                table.AddRow(c.Name, "—", "—");
                continue;
            }

            int entryCount = 0;
            bool subscribed = false;
            if (c.TradingPerfStore != null)
            {
                entryCount = c.TradingPerfStore.Count;
                subscribed = entryCount > 0;
            }

            table.AddRow(c.Name, entryCount.ToString(), subscribed ? "Y" : "N");
            data.Add(new { Server = c.Name, Entries = entryCount, Subscribed = subscribed });
        }

        return CommandResult.Ok(
            $"Fleet Performance — {connections.Count} servers\n{table}",
            new { Servers = connections.Count, Performance = data });
    }

    private CommandResult HandleFleetReports(string[] args)
    {
        IReadOnlyList<CoreConnection> connections = _manager.GetAll();
        if (connections.Count == 0)
        {
            return CommandResult.Fail("No connections. Use 'fleet connect' first.");
        }

        // Default: 24h, but parse time range if provided
        long unixTo = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long unixFrom = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();
        string rangeLabel = "last 24h";

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            switch (a)
            {
                case "today":
                    unixFrom = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
                    rangeLabel = "today";
                    break;
                case "7d":
                    unixFrom = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds();
                    rangeLabel = "last 7 days";
                    break;
                case "30d":
                    unixFrom = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
                    rangeLabel = "last 30 days";
                    break;
            }
        }

        TableBuilder table = new TableBuilder("Server", "Trades", "PnL", "Fees", "WinRate", "Volume");
        List<object> data = new List<object>();
        int totalTrades = 0;
        double totalPnl = 0;
        double totalFees = 0;

        foreach (CoreConnection c in connections)
        {
            if (!c.IsConnected)
            {
                table.AddRow(c.Name, "—", "—", "—", "—", "—");
                continue;
            }

            ReportListData? reportList = c.RequestReports(unixFrom, unixTo);
            if (reportList?.reports == null || reportList.reports.Count == 0)
            {
                table.AddRow(c.Name, "0", "$0.00", "$0.00", "—", "$0");
                data.Add(new { Server = c.Name, Trades = 0, PnL = 0.0, Fees = 0.0, WinRate = 0.0, Volume = 0.0 });
                continue;
            }

            List<ReportData> reports = reportList.reports;
            double pnl = 0;
            double fees = 0;
            double volume = 0;
            int wins = 0;

            foreach (ReportData r in reports)
            {
                pnl += r.totalUSDT;
                fees += r.commissionUSDT;
                volume += r.executedQtyUSDT;
                if (r.totalUSDT > 0) { wins++; }
            }

            double winRate = reports.Count > 0 ? (double)wins / reports.Count * 100 : 0;

            table.AddRow(c.Name,
                reports.Count.ToString(),
                pnl >= 0 ? $"+{pnl:F2}" : $"{pnl:F2}",
                $"{fees:F2}",
                $"{winRate:F0}%",
                $"${volume:N0}");

            data.Add(new
            {
                Server = c.Name,
                Trades = reports.Count,
                PnL = Math.Round(pnl, 2),
                Fees = Math.Round(fees, 2),
                WinRate = Math.Round(winRate, 1),
                Volume = Math.Round(volume, 2),
            });

            totalTrades += reports.Count;
            totalPnl += pnl;
            totalFees += fees;
        }

        string summary = $"Fleet Reports ({rangeLabel}) — {connections.Count} servers, " +
            $"{totalTrades} trades, PnL: {(totalPnl >= 0 ? "+" : "")}{totalPnl:F2}, Fees: {totalFees:F2}\n{table}";

        return CommandResult.Ok(summary,
            new { Period = rangeLabel, Servers = connections.Count,
                  TotalTrades = totalTrades, TotalPnL = Math.Round(totalPnl, 2),
                  TotalFees = Math.Round(totalFees, 2), PerServer = data });
    }

    #endregion

}
