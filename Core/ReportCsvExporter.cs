using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using MTShared.Network;
using MTShared.Types;

namespace MTTextClient.Core;

/// <summary>
/// Generates CSV content from ReportData collections.
/// Supports single-core and multi-core merged exports.
/// </summary>
public static class ReportCsvExporter
{
    private const string CSV_HEADER =
        "Id,ServerName,CloseTime,OpenTime,Exchange,MarketType,Symbol,Side," +
        "EntryPrice,ExitPrice,PriceDelta,Qty,ExecutedQty,SizeUSDT," +
        "PnLCoin,PnLUSDT,GrossUSDT,FeeUSDT,ROE%," +
        "ClosedBy,IsEmulated,AlgoSignature,AlgoId,AlgoName,AlgoGroup," +
        "DepthVolume,DistanceAtOrder,ShotDepth";

    public static string GenerateCsv(List<ReportData> reports, string serverName)
    {
        StringBuilder sb = new StringBuilder(reports.Count * 256);
        sb.AppendLine(CSV_HEADER);

        foreach (ReportData r in reports)
        {
            AppendRow(sb, r, serverName);
        }

        return sb.ToString();
    }

    public static string GenerateMergedCsv(
        Dictionary<string, List<ReportData>> reportsByServer)
    {
        int totalCount = 0;
        foreach (KeyValuePair<string, List<ReportData>> kvp in reportsByServer)
        {
            totalCount += kvp.Value.Count;
        }

        // Collect all reports with server name, then sort by close time descending
        List<(string Server, ReportData Report)> merged =
            new List<(string, ReportData)>(totalCount);

        foreach (KeyValuePair<string, List<ReportData>> kvp in reportsByServer)
        {
            foreach (ReportData r in kvp.Value)
            {
                merged.Add((kvp.Key, r));
            }
        }

        merged.Sort((a, b) => b.Report.reportTime.CompareTo(a.Report.reportTime));

        StringBuilder sb = new StringBuilder(merged.Count * 256);
        sb.AppendLine(CSV_HEADER);

        foreach ((string server, ReportData r) in merged)
        {
            AppendRow(sb, r, server);
        }

        return sb.ToString();
    }

    public static string WriteToFile(string csvContent, string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            filePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "mt-reports",
                $"reports_{timestamp}.csv");
        }

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, csvContent, Encoding.UTF8);
        return filePath;
    }

    private static void AppendRow(StringBuilder sb, ReportData r, string serverName)
    {
        string closeTime = DateTimeOffset.FromUnixTimeMilliseconds(r.reportTime)
            .ToString("yyyy-MM-dd HH:mm:ss");
        string openTime = DateTimeOffset.FromUnixTimeMilliseconds(r.reportOpenTime)
            .ToString("yyyy-MM-dd HH:mm:ss");
        string side = r.orderSideType == OrderSideType.BUY ? "BUY" : "SELL";
        string signature = r.orderInfo.signature ?? "Manual";
        if (string.IsNullOrEmpty(signature) || signature == "00")
        {
            signature = "Manual";
        }

        string algoName = r.orderInfo.AlgorithmInfo.name ?? "";
        // Escape fields that might contain commas
        algoName = EscapeCsvField(algoName);

        sb.Append(r.id); sb.Append(',');
        sb.Append(EscapeCsvField(serverName)); sb.Append(',');
        sb.Append(closeTime); sb.Append(',');
        sb.Append(openTime); sb.Append(',');
        sb.Append(r.exchangeType); sb.Append(',');
        sb.Append(r.marketType); sb.Append(',');
        sb.Append(r.symbol); sb.Append(',');
        sb.Append(side); sb.Append(',');
        sb.Append(r.priceOpen); sb.Append(',');
        sb.Append(r.priceClose); sb.Append(',');
        sb.Append(Math.Round((double)r.priceDelta, 6)); sb.Append(',');
        sb.Append(r.qty); sb.Append(',');
        sb.Append(r.executedQty); sb.Append(',');
        sb.Append(Math.Round(r.executedQtyUSDT, 2)); sb.Append(',');
        sb.Append(Math.Round(r.profit, 6)); sb.Append(',');
        sb.Append(Math.Round(r.profitUSDT, 4)); sb.Append(',');
        sb.Append(Math.Round(r.totalUSDT, 4)); sb.Append(',');
        sb.Append(Math.Round(r.commissionUSDT, 4)); sb.Append(',');
        sb.Append(Math.Round((double)r.profitPercentage, 2)); sb.Append(',');
        sb.Append(r.closedBy); sb.Append(',');
        sb.Append(r.isEmulated ? "true" : "false"); sb.Append(',');
        sb.Append(EscapeCsvField(signature)); sb.Append(',');
        sb.Append(r.orderInfo.algorithmId); sb.Append(',');
        sb.Append(algoName); sb.Append(',');
        sb.Append(r.orderInfo.algorithmGroupType); sb.Append(',');
        sb.Append(Math.Round(r.depthVolume, 4)); sb.Append(',');
        sb.Append(Math.Round(r.distanceAtOrder, 6)); sb.Append(',');
        sb.Append(r.metrics.shotDepth);
        sb.AppendLine();
    }

    private static string EscapeCsvField(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        return field;
    }
}
