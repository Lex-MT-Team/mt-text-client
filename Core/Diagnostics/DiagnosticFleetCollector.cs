using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MTShared.Types;
using MTTextClient.Core;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core.Diagnostics;

/// <summary>
/// Collects one read-only native operation across an explicit saved-profile
/// allowlist. Queueing, retries, and exchanges consume one absolute deadline;
/// no row can become an implicit connection or ordinary fleet operation.
/// </summary>
public sealed class DiagnosticFleetCollector
{
    private const int MaximumProfileNameUtf8Bytes = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IDiagnosticExecutor _executor;

    /// <summary>Creates a collector over the existing single-profile native executor.</summary>
    public DiagnosticFleetCollector(IDiagnosticExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>Runs every caller-supplied row while retaining local refusal and duplicate attribution.</summary>
    public async Task<DiagnosticFleetResult> CollectAsync(DiagnosticFleetRequest request, IReadOnlyCollection<ServerProfile> savedProfiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(savedProfiles);
        string[] requestedProfiles = request.Profiles?.ToArray() ?? throw new ArgumentOutOfRangeException(nameof(request));
        if (request.HistoryQueries is not null) request = request with { HistoryQueries = request.HistoryQueries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) };
        ValidateRequest(requestedProfiles, request);

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Stopwatch elapsed = Stopwatch.StartNew();
        List<ServerProfile> snapshot = savedProfiles.Select(CloneProfile).ToList();
        var profileGroups = snapshot.GroupBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var rows = new DiagnosticFleetRow?[requestedProfiles.Length];
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var work = new List<Task>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Deadline);
        using var slots = new SemaphoreSlim(request.Concurrency, request.Concurrency);

        for (int index = 0; index < requestedProfiles.Length; index++)
        {
            int rowIndex = index;
            string requested = SafeProfileLabel(requestedProfiles[index]);
            DateTimeOffset collectedAt = DateTimeOffset.UtcNow;
            if (!accepted.Add(requested))
            {
                rows[rowIndex] = LocalRow(requested, "duplicate", collectedAt);
                continue;
            }
            if (!profileGroups.TryGetValue(requested, out ServerProfile[]? candidates))
            {
                rows[rowIndex] = LocalRow(requested, "not_configured", collectedAt);
                continue;
            }
            if (candidates.Length != 1)
            {
                rows[rowIndex] = LocalRow(requested, "refused", collectedAt);
                continue;
            }
            ServerProfile profile = candidates[0];
            RouteResolution resolution = TryResolveRoute(profile, request.DiagnosticPort, out DiagnosticRoute? route);
            if (resolution == RouteResolution.Missing)
            {
                rows[rowIndex] = LocalRow(requested, "not_configured", collectedAt);
                continue;
            }
            if (resolution != RouteResolution.Success || IsSavedCoreEndpoint(snapshot, route!))
            {
                rows[rowIndex] = LocalRow(requested, "refused", collectedAt);
                continue;
            }

            DiagnosticFleetRequest rowRequest = request.HistoryQueries is null ? request : request with { Request = DiagnosticRequest.HistoryRequest(request.HistoryQueries[requested]) };
            work.Add(CollectRowAsync(rowIndex, requested, profile, route!, snapshot, rowRequest, elapsed, slots, cancellationToken, deadline.Token, rows));
        }

        await Task.WhenAll(work).ConfigureAwait(false);
        DateTimeOffset completedAt = DateTimeOffset.UtcNow;
        IReadOnlyList<DiagnosticFleetRow> completeRows = rows.Select((row, index) => row ?? LocalRow(SafeProfileLabel(requestedProfiles[index]), cancellationToken.IsCancellationRequested ? "cancelled" : "timeout", completedAt)).ToArray();
        int validated = completeRows.Count(row => row.Result?.Kind == DiagnosticResultKind.Success);
        int sourceUnavailable = completeRows.Count(row => IsSourceUnavailable(row.Result));
        int sourceAvailable = validated - sourceUnavailable;
        int recovered = completeRows.Count(row => row.Recovered);
        int attempted = completeRows.Sum(row => row.Attempts.Count);
        int completed = completeRows.Count(row => row.Attempts.Count > 0);
        return new DiagnosticFleetResult(completeRows, new DiagnosticFleetCoverage(completeRows.Count, completed, validated, sourceAvailable, sourceUnavailable, recovered, attempted), startedAt, completedAt);
    }

    private async Task CollectRowAsync(int index, string requested, ServerProfile profile, DiagnosticRoute route, IReadOnlyCollection<ServerProfile> snapshot, DiagnosticFleetRequest request, Stopwatch elapsed, SemaphoreSlim slots, CancellationToken callerCancellationToken, CancellationToken deadlineToken, DiagnosticFleetRow?[] rows)
    {
        var attempts = new List<DiagnosticFleetAttempt>(2);
        DiagnosticResult result;
        bool slotAcquired = false;
        try
        {
            await slots.WaitAsync(deadlineToken).ConfigureAwait(false);
            slotAcquired = true;
            try
            {
                AttemptExecution attempt = await ExecuteAttemptAsync(profile, route, snapshot, request.Request, request.Deadline, elapsed, attempts, callerCancellationToken, deadlineToken).ConfigureAwait(false);
                result = attempt.Result;
                if (request.RetryOnce && attempt.RetryEligible && Remaining(request.Deadline, elapsed) > TimeSpan.Zero)
                {
                    result = (await ExecuteAttemptAsync(profile, route, snapshot, request.Request, request.Deadline, elapsed, attempts, callerCancellationToken, deadlineToken).ConfigureAwait(false)).Result;
                }
            }
            finally
            {
                if (slotAcquired) slots.Release();
            }
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            result = DiagnosticResult.Failure(DiagnosticResultKind.Cancelled, "cancelled");
        }
        catch (OperationCanceledException)
        {
            result = DiagnosticResult.Failure(DiagnosticResultKind.Timeout, "deadline-exceeded");
        }
        catch (Exception)
        {
            result = DiagnosticResult.Failure(DiagnosticResultKind.TransportFailure, "native-transport-failure");
        }

        DateTimeOffset collectedAt = DateTimeOffset.UtcNow;
        bool recovered = attempts.Count == 2 && result.Kind == DiagnosticResultKind.Success;
        rows[index] = new DiagnosticFleetRow(requested, result.Code, result.Kind, recovered, attempts.AsReadOnly(), collectedAt, result);
    }

    private async Task<AttemptExecution> ExecuteAttemptAsync(ServerProfile profile, DiagnosticRoute route, IReadOnlyCollection<ServerProfile> snapshot, DiagnosticRequest request, TimeSpan totalDeadline, Stopwatch elapsed, List<DiagnosticFleetAttempt> attempts, CancellationToken callerCancellationToken, CancellationToken deadlineToken)
    {
        TimeSpan remaining = Remaining(totalDeadline, elapsed);
        if (remaining <= TimeSpan.Zero)
        {
            return new AttemptExecution(DiagnosticResult.Failure(DiagnosticResultKind.Timeout, "deadline-exceeded"), false);
        }
        TimeSpan timeout = remaining > MtDiagClient.MaximumTimeout ? MtDiagClient.MaximumTimeout : remaining;
        DiagnosticResult result;
        bool retryEligible;
        try
        {
            result = await _executor.ExecuteAsync(profile, route, request, new DiagnosticIdentityEvidence(profile.Name, null, snapshot.Select(candidate => candidate.Name).ToArray()), timeout, deadlineToken).ConfigureAwait(false);
            if (deadlineToken.IsCancellationRequested && !callerCancellationToken.IsCancellationRequested && result.Kind == DiagnosticResultKind.Cancelled)
            {
                result = DiagnosticResult.Failure(DiagnosticResultKind.Timeout, "deadline-exceeded");
                retryEligible = false;
            }
            else retryEligible = IsTransient(result);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            result = DiagnosticResult.Failure(DiagnosticResultKind.Cancelled, "cancelled");
            retryEligible = false;
        }
        catch (OperationCanceledException)
        {
            result = DiagnosticResult.Failure(DiagnosticResultKind.Timeout, "deadline-exceeded");
            retryEligible = false;
        }
        catch (Exception)
        {
            result = DiagnosticResult.Failure(DiagnosticResultKind.TransportFailure, "native-transport-failure");
            retryEligible = false;
        }
        attempts.Add(new DiagnosticFleetAttempt(attempts.Count + 1, result.Code, result.Kind, DateTimeOffset.UtcNow));
        return new AttemptExecution(result, retryEligible);
    }

    private static bool IsTransient(DiagnosticResult result) =>
        result.Code != "native-connection-rejected" && result.Kind is DiagnosticResultKind.Timeout or DiagnosticResultKind.TransportFailure;

    private static bool IsSourceUnavailable(DiagnosticResult? result) =>
        result?.Kind == DiagnosticResultKind.Success &&
        result.Data is JObject data &&
        data["status"]?.Type == JTokenType.String &&
        data["status"]!.Value<string>() is "missing" or "denied" or "deadline" or "unsupported" or "blocked" or "unavailable";

    private static TimeSpan Remaining(TimeSpan totalDeadline, Stopwatch elapsed) => totalDeadline - elapsed.Elapsed;

    private static DiagnosticFleetRow LocalRow(string requested, string code, DateTimeOffset collectedAt) => new(requested, code, null, false, Array.Empty<DiagnosticFleetAttempt>(), collectedAt, null);

    private static RouteResolution TryResolveRoute(ServerProfile profile, int? explicitPort, out DiagnosticRoute? route)
    {
        route = null;
        int port;
        string source;
        if (explicitPort is not null)
        {
            port = explicitPort.Value;
            source = "explicit";
        }
        else if (profile.Tags is not null && profile.Tags.TryGetValue("mtdiag_port", out string? tag) && int.TryParse(tag, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535)
        {
            source = "tag";
        }
        else
        {
            return profile.Tags is null || !profile.Tags.ContainsKey("mtdiag_port") ? RouteResolution.Missing : RouteResolution.Refused;
        }
        route = new DiagnosticRoute(profile.Address, port, source);
        return RouteResolution.Success;
    }

    private static bool IsSavedCoreEndpoint(IEnumerable<ServerProfile> profiles, DiagnosticRoute route) => profiles.Any(candidate => string.Equals(candidate.Address, route.Address, StringComparison.OrdinalIgnoreCase) && candidate.Port == route.Port);

    private static void ValidateRequest(string[] profiles, DiagnosticFleetRequest request)
    {
        if (request.HistoryQueries is not null && (request.HistoryQueries.Count != profiles.Distinct(StringComparer.Ordinal).Count() || profiles.Any(p => !request.HistoryQueries.TryGetValue(p, out DiagnosticHistoryQuery? q) || !q.IsValid))) throw new ArgumentOutOfRangeException(nameof(request));
        if (profiles.Length is < 1 or > DiagnosticFleetRequest.MaximumProfiles || profiles.Any(name => !IsSafeProfileName(name))) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Concurrency is < 1 or > DiagnosticFleetRequest.MaximumConcurrency) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Deadline <= TimeSpan.Zero || request.Deadline > DiagnosticFleetRequest.MaximumDeadline) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.DiagnosticPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(request));
    }

    private static string SafeProfileLabel(string? value) => IsSafeProfileName(value) ? value! : "invalid-profile";

    private static bool IsSafeProfileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            return StrictUtf8.GetByteCount(value) <= MaximumProfileNameUtf8Bytes && value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.');
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private enum RouteResolution { Success, Missing, Refused }

    private readonly record struct AttemptExecution(DiagnosticResult Result, bool RetryEligible);

    private static ServerProfile CloneProfile(ServerProfile source) => new()
    {
        Name = source.Name,
        Address = source.Address,
        Port = source.Port,
        ClientToken = source.ClientToken,
        Exchange = source.Exchange,
        Tags = source.Tags is null ? new Dictionary<string, string>() : new Dictionary<string, string>(source.Tags, StringComparer.Ordinal),
        Folder = source.Folder
    };
}
