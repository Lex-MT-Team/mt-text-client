using System;
using System.Collections.Generic;
using System.Linq;
using MTTextClient.Core;
namespace MTTextClient.Monitoring;

/// <summary>
/// Analyzes time-series CoreStatusSnapshots for trends, averages, and health assessment.
/// All data comes from UDP CoreStatusSubscription — no filesystem access.
/// </summary>
public static class MonitorAnalyzer
{
    /// <summary>Health status levels.</summary>
    public enum HealthStatus { HEALTHY, WARNING, CRITICAL }

    /// <summary>Compute aggregate statistics over a window of snapshots.</summary>
    public static MonitorStats ComputeStats(List<CoreStatusSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return new MonitorStats();
        }

        var latest = snapshots[^1];
        return new MonitorStats
        {
            SampleCount = snapshots.Count,
            WindowStart = snapshots[0].Timestamp,
            WindowEnd = latest.Timestamp,
            AvgCpuPercent = (int)snapshots.Average(s => s.CoreCpuPercent),
            MinCpuPercent = snapshots.Min(s => (int)s.CoreCpuPercent),
            MaxCpuPercent = snapshots.Max(s => (int)s.CoreCpuPercent),
            AvgMemoryMB = (int)snapshots.Average(s => s.CoreUsedMemoryMB),
            MinMemoryMB = snapshots.Min(s => s.CoreUsedMemoryMB),
            MaxMemoryMB = snapshots.Max(s => s.CoreUsedMemoryMB),
            AvgThreads = (int)snapshots.Average(s => s.CoreThreadCount),
            MinThreads = snapshots.Min(s => s.CoreThreadCount),
            MaxThreads = snapshots.Max(s => s.CoreThreadCount),
            AvgExchangeLatencyMs = (int)snapshots.Average(s => s.AvgExchangeLatencyMs),
            MaxExchangeLatencyMs = snapshots.Max(s => s.AvgExchangeLatencyMs),
            AvgPeerLatencyMs = (int)snapshots.Average(s => s.AvgPeerLatencyMs),
            LatestSnapshot = latest
        };
    }

    /// <summary>Assess overall health from recent snapshots.</summary>
    public static HealthReport AssessHealth(List<CoreStatusSnapshot> snapshots)
    {
        var report = new HealthReport();

        if (snapshots.Count == 0)
        {
            report.Status = HealthStatus.WARNING;
            report.Issues.Add(new HealthIssue("DATA", "WARNING", "No monitoring data collected yet"));
            return report;
        }

        var latest = snapshots[^1];
        var stats = ComputeStats(snapshots);

        // CPU check
        if (latest.CoreCpuPercent > 90)
        {
            report.Issues.Add(new HealthIssue("CPU", "CRITICAL", $"CPU at {latest.CoreCpuPercent}% (>90%)"));
        }
        else if (latest.CoreCpuPercent > 70)
        {
            report.Issues.Add(new HealthIssue("CPU", "WARNING", $"CPU at {latest.CoreCpuPercent}% (>70%)"));
        }

        // Memory check
        if (latest.MemoryUsagePercent > 90)
        {
            report.Issues.Add(new HealthIssue("MEMORY", "CRITICAL", $"System memory at {latest.MemoryUsagePercent}% (>90%)"));
        }
        else if (latest.MemoryUsagePercent > 80)
        {
            report.Issues.Add(new HealthIssue("MEMORY", "WARNING", $"System memory at {latest.MemoryUsagePercent}% (>80%)"));
        }

        // Exchange latency check
        if (latest.AvgExchangeLatencyMs > 2000)
        {
            report.Issues.Add(new HealthIssue("LATENCY", "CRITICAL", $"Exchange latency {latest.AvgExchangeLatencyMs}ms (>2000ms)"));
        }
        else if (latest.AvgExchangeLatencyMs > 1000)
        {
            report.Issues.Add(new HealthIssue("LATENCY", "WARNING", $"Exchange latency {latest.AvgExchangeLatencyMs}ms (>1000ms)"));
        }

        // UDS status check
        foreach (var (market, ok) in latest.UdsStatus)
        {
            if (!ok)
            {
                report.Issues.Add(new HealthIssue("UDS", "CRITICAL", $"{market} data stream is DOWN"));
            }
        }

        // Thread count trend (if enough samples)
        if (snapshots.Count >= 5)
        {
            int recentAvgThreads = (int)snapshots.Skip(snapshots.Count - 3).Average(s => s.CoreThreadCount);
            int earlyAvgThreads = (int)snapshots.Take(3).Average(s => s.CoreThreadCount);
            int threadGrowth = recentAvgThreads - earlyAvgThreads;
            if (threadGrowth > 50)
            {
                report.Issues.Add(new HealthIssue("THREADS", "WARNING", $"Thread count growing: {earlyAvgThreads} → {recentAvgThreads} (+{threadGrowth})"));
            }
        }

        // Memory trend (if enough samples)
        if (snapshots.Count >= 5)
        {
            int recentAvgMem = (int)snapshots.Skip(snapshots.Count - 3).Average(s => s.CoreUsedMemoryMB);
            int earlyAvgMem = (int)snapshots.Take(3).Average(s => s.CoreUsedMemoryMB);
            int memGrowth = recentAvgMem - earlyAvgMem;
            if (memGrowth > 500)
            {
                report.Issues.Add(new HealthIssue("MEMORY_TREND", "WARNING", $"Core memory growing: {earlyAvgMem}MB → {recentAvgMem}MB (+{memGrowth}MB)"));
            }
        }

        // Set overall status
        if (report.Issues.Any(i => i.Severity == "CRITICAL"))
        {
            report.Status = HealthStatus.CRITICAL;
        }
        else if (report.Issues.Any(i => i.Severity == "WARNING"))
        {
            report.Status = HealthStatus.WARNING;
        }
        else
        {
            report.Status = HealthStatus.HEALTHY;
        }

        report.Stats = stats;
        return report;
    }
}

public sealed class MonitorStats
{
    public int SampleCount { get; init; }
    public DateTime WindowStart { get; init; }
    public DateTime WindowEnd { get; init; }
    public int AvgCpuPercent { get; init; }
    public int MinCpuPercent { get; init; }
    public int MaxCpuPercent { get; init; }
    public int AvgMemoryMB { get; init; }
    public int MinMemoryMB { get; init; }
    public int MaxMemoryMB { get; init; }
    public int AvgThreads { get; init; }
    public int MinThreads { get; init; }
    public int MaxThreads { get; init; }
    public int AvgExchangeLatencyMs { get; init; }
    public int MaxExchangeLatencyMs { get; init; }
    public int AvgPeerLatencyMs { get; init; }
    public CoreStatusSnapshot? LatestSnapshot { get; init; }

    public string WindowDuration
    {
        get
        {
            if (SampleCount == 0)
            {
                return "—";
            }
            var span = WindowEnd - WindowStart;
            if (span.TotalHours >= 1)
            {
                return $"{span.Hours}h {span.Minutes}m";
            }
            if (span.TotalMinutes >= 1)
            {
                return $"{span.Minutes}m {span.Seconds}s";
            }
            return $"{span.Seconds}s";
        }
    }
}

public sealed class HealthReport
{
    public MonitorAnalyzer.HealthStatus Status { get; set; } = MonitorAnalyzer.HealthStatus.HEALTHY;
    public List<HealthIssue> Issues { get; } = new();
    public MonitorStats? Stats { get; set; }
}

public sealed class HealthIssue
{
    public string Category { get; }
    public string Severity { get; }
    public string Description { get; }

    public HealthIssue(string category, string severity, string description)
    {
        Category = category;
        Severity = severity;
        Description = description;
    }
}
