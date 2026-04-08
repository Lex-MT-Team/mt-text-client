using System;
using System.Collections.Generic;
using System.Linq;
using MTTextClient.Core;
using MTTextClient.Monitoring;
namespace MTTextClient.Commands;

/// <summary>
/// Real-time core monitoring via UDP CoreStatusSubscription data.
/// No filesystem access — works with remote MTCore instances.
///
/// Subcommands:
///   monitor start               — begin collecting status snapshots
///   monitor stop                — stop collecting
///   monitor status              — collection state and buffer info
///   monitor health              — health assessment with trend analysis
///   monitor performance [count] — time-series performance snapshots
///   monitor stats               — aggregate statistics over collection window
/// </summary>
public sealed class MonitorCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "monitor";
    public string Description => "Real-time core monitoring: health, performance, stats (UDP-based, works remotely)";
    public string Usage => "monitor [<@profile>] <start|stop|status|health|performance|stats>";

    public MonitorCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(
                "Usage: monitor <subcommand>\n" +
                "  start               — begin collecting core status snapshots\n" +
                "  stop                — stop collecting\n" +
                "  status              — collection state and buffer info\n" +
                "  health              — health assessment with trend analysis\n" +
                "  performance [count] — recent performance snapshots (default: 10)\n" +
                "  stats               — aggregate statistics over collection window");
        }

        string sub = args[0].ToLowerInvariant();
        var conn = _manager.ActiveConnection;

        if (conn == null)
        {
            return CommandResult.Fail("Not connected to any server. Use 'connect <profile>' first.");
        }

        return sub switch
        {
            "start" => ExecuteStart(conn),
            "stop" => ExecuteStop(conn),
            "status" => ExecuteStatus(conn),
            "health" => ExecuteHealth(conn),
            "performance" => ExecutePerformance(conn, args),
            "stats" => ExecuteStats(conn),
            _ => CommandResult.Fail($"Unknown subcommand: {sub}. Use: start, stop, status, health, performance, stats")
        };
    }

    private CommandResult ExecuteStart(CoreConnection conn)
    {
        if (conn.MonitorBuffer != null)
        {
            int count = conn.MonitorBuffer.Count;
            return CommandResult.Ok($"Monitor already running ({count} snapshots collected). Use 'monitor stop' first to reset.");
        }

        conn.StartMonitor();
        return CommandResult.Ok("Monitor started. Collecting core status snapshots via UDP.",
            new Dictionary<string, object>
            {
                ["status"] = "started",
                ["server"] = conn.Name,
                ["has_initial_data"] = conn.CoreStatusStore.HasData
            });
    }

    private CommandResult ExecuteStop(CoreConnection conn)
    {
        if (conn.MonitorBuffer == null)
        {
            return CommandResult.Fail("Monitor not running.");
        }

        int collected = conn.MonitorBuffer.Count;
        conn.StopMonitor();
        return CommandResult.Ok($"Monitor stopped. {collected} snapshots were collected.");
    }

    private CommandResult ExecuteStatus(CoreConnection conn)
    {
        var buffer = conn.MonitorBuffer;
        bool running = buffer != null;
        var status = conn.CoreStatusStore.GetStatus();

        var data = new Dictionary<string, object>
        {
            ["server"] = conn.Name,
            ["monitoring"] = running,
            ["snapshots_collected"] = running ? buffer!.Count : 0,
            ["buffer_capacity"] = running ? buffer!.Capacity : 0,
            ["has_core_status"] = status != null,
            ["last_update"] = conn.CoreStatusStore.LastUpdate.ToString("HH:mm:ss UTC")
        };

        if (status != null)
        {
            data["core_cpu"] = $"{status.CoreCpuPercent}%";
            data["core_memory"] = $"{status.CoreUsedMemoryMB} MB";
            data["threads"] = status.CoreThreadCount;
            data["exchange_latency"] = $"{status.AvgExchangeLatencyMs} ms";
        }

        string msg = running
            ? $"[{conn.Name}] Monitor: RUNNING ({buffer!.Count} snapshots)"
            : $"[{conn.Name}] Monitor: STOPPED";

        return CommandResult.Ok(msg, data);
    }

    private CommandResult ExecuteHealth(CoreConnection conn)
    {
        var buffer = conn.MonitorBuffer;
        List<CoreStatusSnapshot> snapshots;

        if (buffer != null && buffer.Count > 0)
        {
            snapshots = buffer.GetLast(100);
        }
        else
        {
            // Even without monitor running, use current status if available
            var current = conn.CoreStatusStore.GetStatus();
            if (current == null)
            {
                return CommandResult.Fail("No monitoring data. Use 'monitor start' or wait for core status update.");
            }
            snapshots = new List<CoreStatusSnapshot> { current };
        }

        var report = MonitorAnalyzer.AssessHealth(snapshots);
        string statusEmoji = report.Status switch
        {
            MonitorAnalyzer.HealthStatus.HEALTHY => "✅",
            MonitorAnalyzer.HealthStatus.WARNING => "⚠️",
            MonitorAnalyzer.HealthStatus.CRITICAL => "🔴",
            _ => "❓"
        };

        var data = new Dictionary<string, object>
        {
            ["server"] = conn.Name,
            ["status"] = $"{statusEmoji} {report.Status}",
            ["healthy"] = report.Status == MonitorAnalyzer.HealthStatus.HEALTHY,
            ["issues"] = report.Issues.Select(i => new Dictionary<string, object>
            {
                ["category"] = i.Category,
                ["severity"] = i.Severity,
                ["description"] = i.Description
            }).ToList<object>()
        };

        if (report.Stats?.LatestSnapshot != null)
        {
            var latest = report.Stats.LatestSnapshot;
            data["metrics"] = new Dictionary<string, object>
            {
                ["coreCpuPercent"] = (int)latest.CoreCpuPercent,
                ["coreMemoryMB"] = latest.CoreUsedMemoryMB,
                ["memoryPercent"] = latest.MemoryUsagePercent,
                ["freeMemoryMB"] = latest.FreeMemoryMB,
                ["threads"] = latest.CoreThreadCount,
                ["exchangeLatencyMs"] = latest.AvgExchangeLatencyMs,
                ["peerLatencyMs"] = latest.AvgPeerLatencyMs,
                ["udsStatus"] = latest.UdsStatus.ToDictionary(kv => kv.Key.ToString(), kv => (object)kv.Value)
            };
        }

        if (report.Stats != null && report.Stats.SampleCount > 1)
        {
            data["trends"] = new Dictionary<string, object>
            {
                ["window"] = report.Stats.WindowDuration,
                ["samples"] = report.Stats.SampleCount,
                ["cpu_range"] = $"{report.Stats.MinCpuPercent}%-{report.Stats.MaxCpuPercent}%",
                ["memory_range"] = $"{report.Stats.MinMemoryMB}-{report.Stats.MaxMemoryMB} MB",
                ["thread_range"] = $"{report.Stats.MinThreads}-{report.Stats.MaxThreads}",
                ["max_latency"] = $"{report.Stats.MaxExchangeLatencyMs} ms"
            };
        }

        var license = conn.CoreStatusStore.GetLicense();
        if (license != null)
        {
            data["license"] = new Dictionary<string, object>
            {
                ["daysRemaining"] = license.LicenseDaysRemaining,
                ["apiKeysDaysRemaining"] = license.ApiKeysDaysRemaining,
                ["version"] = license.BuildVersion
            };
        }

        string msg = $"[{conn.Name}] Health: {statusEmoji} {report.Status}";
        if (report.Stats?.LatestSnapshot != null)
        {
            var l = report.Stats.LatestSnapshot;
            msg += $"\n  CPU: {l.CoreCpuPercent}% | RAM: {l.CoreUsedMemoryMB}MB | Threads: {l.CoreThreadCount} | Exchange: {l.AvgExchangeLatencyMs}ms";
        }

        return CommandResult.Ok(msg, data);
    }

    private CommandResult ExecutePerformance(CoreConnection conn, string[] args)
    {
        int count = 10;
        if (args.Length > 1 && int.TryParse(args[1], out int parsed))
        {
            count = Math.Clamp(parsed, 1, 100);
        }

        var buffer = conn.MonitorBuffer;
        if (buffer == null || buffer.Count == 0)
        {
            // Fall back to single current snapshot
            var current = conn.CoreStatusStore.GetStatus();
            if (current == null)
            {
                return CommandResult.Fail("No monitoring data. Use 'monitor start' first.");
            }

            return CommandResult.Ok($"[{conn.Name}] Performance (current snapshot only — start monitor for history)",
                new List<object>
                {
                    SnapshotToDict(current)
                });
        }

        var snapshots = buffer.GetLast(count);
        var data = snapshots.Select(s => (object)SnapshotToDict(s)).ToList();

        return CommandResult.Ok(
            $"[{conn.Name}] Performance — {snapshots.Count} snapshots",
            data);
    }

    private CommandResult ExecuteStats(CoreConnection conn)
    {
        var buffer = conn.MonitorBuffer;
        if (buffer == null || buffer.Count == 0)
        {
            return CommandResult.Fail("No monitoring data. Use 'monitor start' first.");
        }

        var snapshots = buffer.GetLast(buffer.Count);
        var stats = MonitorAnalyzer.ComputeStats(snapshots);

        var data = new Dictionary<string, object>
        {
            ["server"] = conn.Name,
            ["samples"] = stats.SampleCount,
            ["window"] = stats.WindowDuration,
            ["window_start"] = stats.WindowStart.ToString("HH:mm:ss UTC"),
            ["window_end"] = stats.WindowEnd.ToString("HH:mm:ss UTC"),
            ["cpu"] = new Dictionary<string, object>
            {
                ["avg"] = $"{stats.AvgCpuPercent}%",
                ["min"] = $"{stats.MinCpuPercent}%",
                ["max"] = $"{stats.MaxCpuPercent}%"
            },
            ["memory_mb"] = new Dictionary<string, object>
            {
                ["avg"] = stats.AvgMemoryMB,
                ["min"] = stats.MinMemoryMB,
                ["max"] = stats.MaxMemoryMB
            },
            ["threads"] = new Dictionary<string, object>
            {
                ["avg"] = stats.AvgThreads,
                ["min"] = stats.MinThreads,
                ["max"] = stats.MaxThreads
            },
            ["exchange_latency_ms"] = new Dictionary<string, object>
            {
                ["avg"] = stats.AvgExchangeLatencyMs,
                ["max"] = stats.MaxExchangeLatencyMs
            },
            ["peer_latency_ms"] = stats.AvgPeerLatencyMs
        };

        return CommandResult.Ok(
            $"[{conn.Name}] Monitor Stats — {stats.SampleCount} samples over {stats.WindowDuration}\n" +
            $"  CPU: avg {stats.AvgCpuPercent}% (min {stats.MinCpuPercent}%, max {stats.MaxCpuPercent}%)\n" +
            $"  Memory: avg {stats.AvgMemoryMB}MB (min {stats.MinMemoryMB}MB, max {stats.MaxMemoryMB}MB)\n" +
            $"  Threads: avg {stats.AvgThreads} (min {stats.MinThreads}, max {stats.MaxThreads})\n" +
            $"  Exchange Latency: avg {stats.AvgExchangeLatencyMs}ms (max {stats.MaxExchangeLatencyMs}ms)",
            data);
    }

    private static Dictionary<string, object> SnapshotToDict(CoreStatusSnapshot s)
    {
        return new Dictionary<string, object>
        {
            ["time"] = s.Timestamp.ToString("HH:mm:ss"),
            ["cpu"] = $"{s.CoreCpuPercent}%",
            ["system_cpu"] = $"{s.AvgCpuPercent}%",
            ["core_memory_mb"] = s.CoreUsedMemoryMB,
            ["system_memory_mb"] = s.AvgMemoryMB,
            ["free_memory_mb"] = s.FreeMemoryMB,
            ["memory_percent"] = $"{s.MemoryUsagePercent}%",
            ["threads"] = s.CoreThreadCount,
            ["exchange_latency_ms"] = s.AvgExchangeLatencyMs,
            ["peer_latency_ms"] = s.AvgPeerLatencyMs,
            ["uds"] = s.UdsStatusSummary,
            ["api_loading"] = s.ApiLoadingSummary
        };
    }
}
