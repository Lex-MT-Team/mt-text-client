using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using MTTextClient.Core.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core.Diagnostics.Companion;

/// <summary>
/// Provides three independent Guardian observations: a configured snapshot,
/// configured event history, and an optional allowlisted process-name lookup.
/// None of these observations implies another or changes local process state.
/// </summary>
public sealed class GuardianEvidenceReader
{
    private const int MaximumSnapshotBytes = 256 * 1024;
    private const int MaximumHistoryBytes = 256 * 1024;
    private const int DefaultHistoryEntries = 100;
    private const int ResponseContentBudgetBytes = 48 * 1024;
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(2);
    private static readonly Regex Timestamp = new(@"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:?\d{2})\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private readonly CompanionProfileConfiguration _profile;

    /// <summary>Creates a reader permanently bound to one configured profile.</summary>
    public GuardianEvidenceReader(CompanionProfileConfiguration profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <summary>Reads the configured Guardian evidence without contacting or controlling Guardian.</summary>
    public JObject Read(CancellationToken cancellationToken, int maximumEventEntries = DefaultHistoryEntries, TimeSpan? deadline = null)
    {
        if (maximumEventEntries is < 1 or > DiagnosticRequest.MaximumLines)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEventEntries));
        }

        TimeSpan limit = deadline.GetValueOrDefault(DefaultDeadline);
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromSeconds(10))
        {
            limit = DefaultDeadline;
        }
        var stopwatch = Stopwatch.StartNew();
        var result = new JObject
        {
            ["source"] = "guardian",
            ["snapshot"] = ReadSnapshot(cancellationToken, stopwatch, limit),
            ["eventHistory"] = ReadEventHistory(cancellationToken, stopwatch, limit, maximumEventEntries),
            ["process"] = ReadProcessObservation(cancellationToken, stopwatch, limit),
            ["responseTruncated"] = false
        };
        EnforceResponseBudget(result);
        return result;
    }

    private JObject ReadSnapshot(CancellationToken cancellationToken, Stopwatch stopwatch, TimeSpan deadline)
    {
        if (_profile.GuardianSnapshotPath is null)
        {
            return new JObject { ["status"] = "unsupported" };
        }
        CompanionFileRead file = CompanionFileRead.Read(_profile.GuardianSnapshotPath, MaximumSnapshotBytes, cancellationToken, stopwatch, deadline);
        var result = new JObject
        {
            ["status"] = file.Status,
            ["truncated"] = file.Truncated
        };
        if (file.ModifiedUtc.HasValue)
        {
            result["fileModifiedAtUtc"] = file.ModifiedUtc.Value;
        }
        if (file.Status == "available")
        {
            result["observedAtUtc"] = DateTimeOffset.UtcNow;
            result["content"] = DiagnosticReplyValidator.SanitizeEvidence(file.Text);
        }
        return result;
    }

    private JObject ReadEventHistory(CancellationToken cancellationToken, Stopwatch stopwatch, TimeSpan deadline, int maximumEventEntries)
    {
        if (_profile.GuardianEventPath is null)
        {
            return new JObject { ["status"] = "unsupported" };
        }
        CompanionFileRead file = CompanionFileRead.Read(_profile.GuardianEventPath, MaximumHistoryBytes, cancellationToken, stopwatch, deadline);
        var result = new JObject
        {
            ["status"] = file.Status,
            ["truncated"] = file.Truncated,
            ["entries"] = new JArray()
        };
        if (file.Status != "available")
        {
            return result;
        }

        string[] values = file.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        string[] nonEmptyValues = values.Where(value => value.Length > 0).ToArray();
        result["truncated"] = file.Truncated || nonEmptyValues.Length > maximumEventEntries;
        var entries = (JArray)result["entries"]!;
        DateTimeOffset? lastEvent = null;
        foreach (string value in nonEmptyValues.TakeLast(maximumEventEntries))
        {
            if (value.Length > 16 * 1024)
            {
                result["truncated"] = true;
            }
            entries.Add(DiagnosticReplyValidator.SanitizeEvidence(value.Length <= 16 * 1024 ? value : value[..(16 * 1024)] + " [truncated]"));
            if (TryTimestamp(value, out DateTimeOffset timestamp))
            {
                lastEvent = !lastEvent.HasValue || timestamp > lastEvent.Value ? timestamp : lastEvent;
            }
        }
        if (lastEvent.HasValue)
        {
            result["lastEventUtc"] = lastEvent.Value;
            double age = Math.Max(0, (DateTimeOffset.UtcNow - lastEvent.Value).TotalSeconds);
            result["lastEventAgeSeconds"] = Math.Floor(age);
        }
        return result;
    }

    private JObject ReadProcessObservation(CancellationToken cancellationToken, Stopwatch stopwatch, TimeSpan deadline)
    {
        if (_profile.GuardianProcessName is null)
        {
            return new JObject { ["status"] = "unsupported" };
        }
        if (cancellationToken.IsCancellationRequested || stopwatch.Elapsed > deadline)
        {
            return new JObject { ["status"] = "deadline" };
        }
        try
        {
            Process[] processes = Process.GetProcessesByName(_profile.GuardianProcessName);
            try
            {
                return new JObject
                {
                    ["status"] = processes.Length > 0 ? "observed" : "not_observed",
                    ["observedAtUtc"] = DateTimeOffset.UtcNow,
                    ["count"] = processes.Length
                };
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch (PlatformNotSupportedException)
        {
            return new JObject { ["status"] = "unsupported" };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new JObject { ["status"] = "unsupported" };
        }
    }

    private static bool TryTimestamp(string value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        try
        {
            Match match = Timestamp.Match(value);
            return match.Success && DateTimeOffset.TryParse(match.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static void EnforceResponseBudget(JObject result)
    {
        while (System.Text.Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > ResponseContentBudgetBytes)
        {
            JArray? entries = result["eventHistory"]?["entries"] as JArray;
            if (entries?.Count > 0)
            {
                entries.RemoveAt(0);
                result["responseTruncated"] = true;
                continue;
            }
            JValue? content = result["snapshot"]!["content"] as JValue;
            string? text = content?.Value<string>();
            if (!string.IsNullOrEmpty(text))
            {
                content!.Value = text[..Math.Max(0, text.Length / 2)];
                result["snapshot"]!["truncated"] = true;
                result["responseTruncated"] = true;
                continue;
            }
            result["responseTruncated"] = true;
            break;
        }
    }
}
