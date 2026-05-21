namespace MTTextClient.Core;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

/// <summary>
/// Request-id observability for the synchronous reports wire.
///
/// MTShared's SendReportListRequest is a blocking ~30 s RPC with no native
/// cancel/status surface. This registry sits above it, recording one entry
/// per <c>mt_reports_query</c> / <c>mt_reports_csv_inline</c> dispatch so the
/// companion <c>mt_reports_cancel</c> and <c>mt_reports_status</c> tools
/// can answer about completed requests. Cancel cannot actually interrupt
/// the wire on this build; it records intent so callers can observe that
/// the gesture was accepted and noted.
/// </summary>
public sealed class ReportsRequestEntry
{
    public string RequestId { get; init; } = "";
    public string Profile { get; init; } = "";
    public string FilterSummary { get; init; } = "";
    public DateTime StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; set; }
    public bool CancellationRequested { get; set; }
    public int RowCount { get; set; }
    public string Status { get; set; } = "in_progress"; // in_progress | completed | cancel_requested | error
    public string? ErrorMessage { get; set; }
}

public static class ReportsRequestRegistry
{
    private const int MaxEntries = 256;
    private static readonly ConcurrentDictionary<string, ReportsRequestEntry> _entries = new();
    private static readonly ConcurrentQueue<string> _order = new();

    public static ReportsRequestEntry Begin(string profile, string filterSummary)
    {
        var entry = new ReportsRequestEntry
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Profile = profile,
            FilterSummary = filterSummary,
            StartedAtUtc = DateTime.UtcNow,
        };
        _entries[entry.RequestId] = entry;
        _order.Enqueue(entry.RequestId);
        while (_order.Count > MaxEntries && _order.TryDequeue(out var oldId))
            _entries.TryRemove(oldId, out _);
        return entry;
    }

    public static ReportsRequestEntry Complete(string requestId, int rowCount)
    {
        if (_entries.TryGetValue(requestId, out var entry))
        {
            entry.CompletedAtUtc = DateTime.UtcNow;
            entry.RowCount = rowCount;
            entry.Status = entry.CancellationRequested ? "completed_after_cancel_requested" : "completed";
        }
        return entry!;
    }

    public static ReportsRequestEntry Error(string requestId, string message)
    {
        if (_entries.TryGetValue(requestId, out var entry))
        {
            entry.CompletedAtUtc = DateTime.UtcNow;
            entry.Status = "error";
            entry.ErrorMessage = message;
        }
        return entry!;
    }

    public static ReportsRequestEntry? Get(string requestId)
        => _entries.TryGetValue(requestId, out var e) ? e : null;

    public static ReportsRequestEntry RequestCancel(string requestId)
    {
        if (_entries.TryGetValue(requestId, out var entry))
        {
            entry.CancellationRequested = true;
            if (entry.Status == "in_progress") entry.Status = "cancel_requested";
        }
        return entry!;
    }
}
