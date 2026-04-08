using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using MTShared.Network;

namespace MTTextClient.Core;

/// <summary>
/// Local in-memory report store with optional file persistence.
/// Stores named report sets for later retrieval, comparison, and export.
/// Thread-safe via ConcurrentDictionary.
/// </summary>
public sealed class ReportStore
{
    private static readonly string STORE_DIR = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "mt-reports", "store");

    private readonly ConcurrentDictionary<string, StoredReportSet> _sets =
        new ConcurrentDictionary<string, StoredReportSet>(StringComparer.OrdinalIgnoreCase);

    public void Save(string name, string serverName, List<ReportData> reports,
        long fromUnix, long toUnix, string filterDescription)
    {
        StoredReportSet set = new StoredReportSet
        {
            Name = name,
            ServerName = serverName,
            CapturedAtUtc = DateTime.UtcNow,
            FromUnix = fromUnix,
            ToUnix = toUnix,
            FilterDescription = filterDescription,
            TradeCount = reports.Count,
            Reports = reports,
        };

        ComputeStats(set);
        _sets[name] = set;
    }

    public void SaveMerged(string name, Dictionary<string, List<ReportData>> reportsByServer,
        long fromUnix, long toUnix, string filterDescription)
    {
        List<ReportData> allReports = new List<ReportData>();
        List<string> serverNames = new List<string>();

        foreach (KeyValuePair<string, List<ReportData>> kvp in reportsByServer)
        {
            allReports.AddRange(kvp.Value);
            serverNames.Add($"{kvp.Key}({kvp.Value.Count})");
        }

        allReports.Sort((a, b) => b.reportTime.CompareTo(a.reportTime));

        StoredReportSet set = new StoredReportSet
        {
            Name = name,
            ServerName = string.Join(", ", serverNames),
            CapturedAtUtc = DateTime.UtcNow,
            FromUnix = fromUnix,
            ToUnix = toUnix,
            FilterDescription = filterDescription,
            TradeCount = allReports.Count,
            Reports = allReports,
        };

        ComputeStats(set);
        _sets[name] = set;
    }

    public StoredReportSet? Get(string name)
    {
        _sets.TryGetValue(name, out StoredReportSet? set);
        return set;
    }

    public bool Delete(string name)
    {
        return _sets.TryRemove(name, out _);
    }

    public List<StoredReportSet> ListAll()
    {
        List<StoredReportSet> result = new List<StoredReportSet>(_sets.Count);
        foreach (KeyValuePair<string, StoredReportSet> kvp in _sets)
        {
            result.Add(kvp.Value);
        }

        result.Sort((a, b) => b.CapturedAtUtc.CompareTo(a.CapturedAtUtc));
        return result;
    }

    public int Count => _sets.Count;

    private static void ComputeStats(StoredReportSet set)
    {
        double totalPnl = 0;
        double totalFees = 0;
        double totalVolume = 0;
        int wins = 0;
        int losses = 0;

        foreach (ReportData r in set.Reports)
        {
            totalPnl += r.totalUSDT;
            totalFees += r.commissionUSDT;
            totalVolume += r.executedQtyUSDT;
            if (r.totalUSDT > 0) { wins++; }
            else if (r.totalUSDT < 0) { losses++; }
        }

        set.TotalPnlUSDT = Math.Round(totalPnl, 2);
        set.TotalFeesUSDT = Math.Round(totalFees, 2);
        set.TotalVolumeUSDT = Math.Round(totalVolume, 2);
        set.Wins = wins;
        set.Losses = losses;
        set.WinRate = set.TradeCount > 0
            ? Math.Round((double)wins / set.TradeCount * 100, 1)
            : 0;
    }
}

public sealed class StoredReportSet
{
    public string Name { get; set; } = "";
    public string ServerName { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; }
    public long FromUnix { get; set; }
    public long ToUnix { get; set; }
    public string FilterDescription { get; set; } = "";
    public int TradeCount { get; set; }
    public double TotalPnlUSDT { get; set; }
    public double TotalFeesUSDT { get; set; }
    public double TotalVolumeUSDT { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public double WinRate { get; set; }
    public List<ReportData> Reports { get; set; } = new List<ReportData>();
}
