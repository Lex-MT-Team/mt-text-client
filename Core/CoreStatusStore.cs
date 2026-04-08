using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using MTShared;
using MTShared.Network;
using MTShared.Types;
namespace MTTextClient.Core;

/// <summary>
/// In-memory store for Core Status data received via CORE_STATUS_SUBSCRIBE.
/// Thread-safe. Receives periodic status updates from the MT-Core instance.
///
/// Push Events Handled:
///   CORE_STATUS_RESULT (56) → CoreStatusData — system metrics + license info
///
/// Initial update (isInitialUpdate=true) includes license info, build version, OS, etc.
/// Subsequent updates contain only metrics: CPU, memory, latency, API loading.
///
/// Field semantics (from MTController UI):
///   avgCPU = system average CPU usage (percent, 0-100)
///   avgMemory = system average RAM usage (MB, not percent!)
///   coreCPU = core process CPU usage (value from ushort)
///   coreUsedMB = core process RAM (MB)
///   freeMB = system free RAM (MB)
/// </summary>
public sealed class CoreStatusStore
{
    private volatile CoreStatusSnapshot? _current;
    private volatile CoreLicenseSnapshot? _license;

    public DateTime LastUpdate { get; private set; }
    public bool HasData => _current != null;

    // Events
    public event Action<CoreStatusSnapshot>? OnStatusUpdated;
    public event Action<CoreLicenseSnapshot>? OnLicenseReceived;

    /// <summary>
    /// Process incoming core status data from subscription callback.
    /// </summary>
    public void ProcessData(NetworkMessageType msgType, NetworkData data)
    {
        if (msgType != NetworkMessageType.CORE_STATUS_RESULT)
        {
            return;
        }

        if (data is not CoreStatusData status)
        {
            return;
        }

        var snapshot = new CoreStatusSnapshot
        {
            AvgExchangeLatencyMs = status.avgCoreExchageLatency,
            AvgPeerLatencyMs = status.avgCorePeerLatency,
            AvgCpuPercent = status.avgCPU,
            AvgMemoryMB = status.avgMemory,
            FreeMemoryMB = status.freeMB,
            CoreUsedMemoryMB = status.coreUsedMB,
            CoreThreadCount = status.coreThreads,
            CoreCpuPercent = status.coreCPU,
            EndPoint = status.endPoint ?? "",
            ApiLoading = status.apiLoading != null
                ? new Dictionary<MarketType, short>(status.apiLoading)
                : new Dictionary<MarketType, short>(),
            UdsStatus = status.udsStatus != null
                ? new Dictionary<MarketType, bool>(status.udsStatus)
                : new Dictionary<MarketType, bool>(),
            IsInitialUpdate = status.isInitialUpdate,
            Timestamp = DateTime.UtcNow
        };

        _current = snapshot;
        LastUpdate = DateTime.UtcNow;
        OnStatusUpdated?.Invoke(snapshot);

        // Initial update includes license/version info
        if (status.isInitialUpdate)
        {
            var license = new CoreLicenseSnapshot
            {
                LicenseId = status.licenseID,
                LicenseName = status.licenseName ?? "",
                BuildVersion = status.buildVersion ?? "",
                CoreOS = status.coreNameOS ?? "",
                StartingTime = status.startingTime,
                LicenseValidTill = status.licenseValidTill,
                ApiKeysExpiration = status.exchangeAPIKeysExpirationTime,
                UserComment = status.licenseUserComment ?? "",
                Timestamp = DateTime.UtcNow
            };

            _license = license;
            OnLicenseReceived?.Invoke(license);
        }
    }

    // ── Queries ──────────────────────────────────────────────

    public CoreStatusSnapshot? GetStatus() => _current;
    public CoreLicenseSnapshot? GetLicense() => _license;

    /// <summary>Clear all stored data (on disconnect).</summary>
    public void Clear()
    {
        _current = null;
        _license = null;
    }
}

// ── Snapshot DTOs ────────────────────────────────────────────

public sealed class CoreStatusSnapshot
{
    public int AvgExchangeLatencyMs { get; init; }
    public int AvgPeerLatencyMs { get; init; }
    public int AvgCpuPercent { get; init; }
    /// <summary>System average RAM in MB (not percent — field name avgMemory is misleading in MTShared).</summary>
    public int AvgMemoryMB { get; init; }
    public int FreeMemoryMB { get; init; }
    public int CoreUsedMemoryMB { get; init; }
    public int CoreThreadCount { get; init; }
    public ushort CoreCpuPercent { get; init; }
    public string EndPoint { get; init; } = "";
    public Dictionary<MarketType, short> ApiLoading { get; init; } = new();
    public Dictionary<MarketType, bool> UdsStatus { get; init; } = new();
    public bool IsInitialUpdate { get; init; }
    public DateTime Timestamp { get; init; }

    /// <summary>Human-readable API loading summary.</summary>
    public string ApiLoadingSummary
    {
        get
        {
            if (ApiLoading.Count == 0)
            {
                return "N/A";
            }

            string[] parts = new string[ApiLoading.Count];
            int idx = 0;
            foreach (KeyValuePair<MarketType, short> kvp in ApiLoading)
            {
                parts[idx++] = $"{kvp.Key}: {kvp.Value}%";
            }
            return string.Join(", ", parts);
        }
    }

    /// <summary>Human-readable UDS status summary.</summary>
    public string UdsStatusSummary
    {
        get
        {
            if (UdsStatus.Count == 0)
            {
                return "N/A";
            }

            string[] parts = new string[UdsStatus.Count];
            int idx = 0;
            foreach (KeyValuePair<MarketType, bool> kvp in UdsStatus)
            {
                parts[idx++] = $"{kvp.Key}: {(kvp.Value ? "OK" : "DOWN")}";
            }
            return string.Join(", ", parts);
        }
    }

    /// <summary>Total system RAM = used + free (approximate).</summary>
    public int TotalSystemMemoryMB => AvgMemoryMB + FreeMemoryMB;

    /// <summary>System memory usage percentage (computed from MB values).</summary>
    public int MemoryUsagePercent =>
        TotalSystemMemoryMB > 0 ? (int)((long)AvgMemoryMB * 100 / TotalSystemMemoryMB) : 0;
}

public sealed class CoreLicenseSnapshot
{
    public long LicenseId { get; init; }
    public string LicenseName { get; init; } = "";
    public string BuildVersion { get; init; } = "";
    public string CoreOS { get; init; } = "";
    public long StartingTime { get; init; }
    public long LicenseValidTill { get; init; }
    public long ApiKeysExpiration { get; init; }
    public string UserComment { get; init; } = "";
    public DateTime Timestamp { get; init; }

    // Valid unix timestamp range for DateTimeOffset.FromUnixTimeSeconds
    private const long MIN_UNIX_SECONDS = -62135596800L;
    private const long MAX_UNIX_SECONDS = 253402300799L;

    private static bool IsValidUnixSeconds(long seconds) =>
        seconds > 0 && seconds >= MIN_UNIX_SECONDS && seconds <= MAX_UNIX_SECONDS;

    /// <summary>Core uptime since start.</summary>
    public TimeSpan CoreUptime
    {
        get
        {
            if (!IsValidUnixSeconds(StartingTime))
            {
                return TimeSpan.Zero;
            }

            try
            {
                DateTime start = DateTimeOffset.FromUnixTimeSeconds(StartingTime).UtcDateTime;
                TimeSpan uptime = DateTime.UtcNow - start;
                return uptime > TimeSpan.Zero ? uptime : TimeSpan.Zero;
            }
            catch { return TimeSpan.Zero; }
        }
    }

    /// <summary>Days until license expires (negative = expired, int.MaxValue = unlimited).</summary>
    public int LicenseDaysRemaining
    {
        get
        {
            if (LicenseValidTill <= 0 || LicenseValidTill >= 999999999999L)
            {
                return int.MaxValue; // effectively infinite
            }

            if (!IsValidUnixSeconds(LicenseValidTill))
            {
                return int.MaxValue;
            }

            try
            {
                DateTime expires = DateTimeOffset.FromUnixTimeSeconds(LicenseValidTill).UtcDateTime;
                return (int)(expires - DateTime.UtcNow).TotalDays;
            }
            catch { return int.MaxValue; }
        }
    }

    /// <summary>Days until API keys expire (negative = expired, int.MaxValue = no expiry).</summary>
    public int ApiKeysDaysRemaining
    {
        get
        {
            if (ApiKeysExpiration <= 0)
            {
                return int.MaxValue;
            }

            if (!IsValidUnixSeconds(ApiKeysExpiration))
            {
                return int.MaxValue;
            }

            try
            {
                DateTime expires = DateTimeOffset.FromUnixTimeSeconds(ApiKeysExpiration).UtcDateTime;
                return (int)(expires - DateTime.UtcNow).TotalDays;
            }
            catch { return int.MaxValue; }
        }
    }
}
