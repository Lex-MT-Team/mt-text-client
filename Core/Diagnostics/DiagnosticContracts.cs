using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core.Diagnostics;

/// <summary>Read-only operations supported by the bounded native diagnostic client.</summary>
public enum DiagnosticOperation
{
    /// <summary>Negotiates the fixed diagnostic capability surface.</summary>
    Capabilities,

    /// <summary>Requests the sidecar health evidence.</summary>
    Health,

    /// <summary>Requests a bounded selection of sidecar log evidence.</summary>
    Logs,

    /// <summary>Requests the bounded Guardian status evidence.</summary>
    GuardianStatus,

    /// <summary>Requests bounded Guardian event evidence.</summary>
    GuardianEvents,

    /// <summary>Reads a resumable retained-history page.</summary>
    HistoryPage,

    /// <summary>Releases a retained-history cursor.</summary>
    HistoryClose
}

/// <summary>Classifies a diagnostic call without collapsing distinct failures.</summary>
public enum DiagnosticResultKind
{
    /// <summary>The validated reply was accepted.</summary>
    Success,

    /// <summary>The peer refused the request without a recognized capability signal.</summary>
    ServerError,

    /// <summary>The peer explicitly reported an unsupported capability.</summary>
    UnsupportedCapability,

    /// <summary>The transport could not complete the single exchange.</summary>
    TransportFailure,

    /// <summary>Local input or authenticated peer evidence failed validation.</summary>
    ValidationFailure,

    /// <summary>The one absolute exchange deadline elapsed.</summary>
    Timeout,

    /// <summary>The caller cancelled the exchange.</summary>
    Cancelled
}

/// <summary>Immutable, bounded parameters for one native diagnostic request.</summary>
public sealed record DiagnosticRequest(DiagnosticOperation Operation, int? Lines, string? Grep)
{
    /// <summary>Optional immutable retained-history parameters.</summary>
    public DiagnosticHistoryQuery? History { get; init; }

    /// <summary>Creates an authenticated history page or close request.</summary>
    public static DiagnosticRequest HistoryRequest(DiagnosticHistoryQuery query, bool close = false) => new(close ? DiagnosticOperation.HistoryClose : DiagnosticOperation.HistoryPage, null, null) { History = query };

    /// <summary>Maximum evidence lines accepted from a companion.</summary>
    public const int MaximumLines = 500;

    /// <summary>Maximum UTF-8 filter length, keeping one request safely bounded.</summary>
    public const int MaximumGrepUtf8Bytes = 512;

    /// <summary>Creates a request that carries no log-specific parameters.</summary>
    public static DiagnosticRequest Health() => new(DiagnosticOperation.Health, null, null);

    /// <summary>Creates a capability request.</summary>
    public static DiagnosticRequest Capabilities() => new(DiagnosticOperation.Capabilities, null, null);

    /// <summary>Creates a Guardian status request.</summary>
    public static DiagnosticRequest GuardianStatus() => new(DiagnosticOperation.GuardianStatus, null, null);

    /// <summary>Creates a bounded Guardian event request.</summary>
    public static DiagnosticRequest GuardianEvents(int lines) => new(DiagnosticOperation.GuardianEvents, lines, null);

    /// <summary>Creates a bounded log request with an optional local filter.</summary>
    public static DiagnosticRequest Logs(int lines, string? grep) => new(DiagnosticOperation.Logs, lines, grep);
}

/// <summary>Immutable, caller-provided profile binding evidence for one reply.</summary>
public sealed record DiagnosticIdentityEvidence
{
    /// <summary>Snapshots local profile names used to bind the next peer reply.</summary>
    public DiagnosticIdentityEvidence(string selectedProfile, string? ephemeralAlias, IReadOnlyCollection<string>? currentRegistryNames)
    {
        SelectedProfile = selectedProfile;
        EphemeralAlias = ephemeralAlias;
        CurrentRegistryNames = Array.AsReadOnly((currentRegistryNames ?? Array.Empty<string>()).ToArray());
    }

    /// <summary>Gets the selected local profile name.</summary>
    public string SelectedProfile { get; }

    /// <summary>Gets an optional unpersisted alias accepted for this call only.</summary>
    public string? EphemeralAlias { get; }

    /// <summary>Gets the immutable current registry names used for contradiction checks.</summary>
    public IReadOnlyCollection<string> CurrentRegistryNames { get; }
}

/// <summary>Describes one explicit companion route without modifying the saved core profile.</summary>
public sealed record DiagnosticRoute(string Address, int Port, string Source);

/// <summary>Executes one bounded native diagnostic operation over an explicit companion route.</summary>
public interface IDiagnosticExecutor
{
    /// <summary>Completes one native companion exchange without using MTCore control state.</summary>
    Task<DiagnosticResult> ExecuteAsync(ServerProfile profile, DiagnosticRoute route, DiagnosticRequest request, DiagnosticIdentityEvidence evidence, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Immutable outcome whose data and metadata are available only on success.</summary>
public sealed class DiagnosticResult
{
    private DiagnosticResult(DiagnosticResultKind kind, string code, string? error, string? profile, string? source, JToken? data, JToken? metadata)
    {
        Kind = kind; Code = code; Error = error; Profile = profile; Source = source; Data = data; Metadata = metadata;
    }

    /// <summary>Gets the non-overlapping outcome kind.</summary>
    public DiagnosticResultKind Kind { get; }

    /// <summary>Gets the stable non-secret outcome code.</summary>
    public string Code { get; }

    /// <summary>Gets an optional sanitized peer error for server errors only.</summary>
    public string? Error { get; }

    /// <summary>Gets the accepted profile identity on success only.</summary>
    public string? Profile { get; }

    /// <summary>Gets the accepted source identity on success only.</summary>
    public string? Source { get; }

    /// <summary>Gets sanitized peer data on success only.</summary>
    public JToken? Data { get; }

    /// <summary>Gets sanitized peer metadata on success only.</summary>
    public JToken? Metadata { get; }

    internal static DiagnosticResult Success(string profile, string source, JToken? data, JToken? metadata, string code = "success") => new(DiagnosticResultKind.Success, code, null, profile, source, data, metadata);
    internal static DiagnosticResult Failure(DiagnosticResultKind kind, string code, string? error = null) => new(kind, code, error, null, null, null, null);
}

/// <summary>Immutable limits and requested route inputs for one explicit fleet operation.</summary>
public sealed record DiagnosticFleetRequest(
    IReadOnlyList<string> Profiles,
    DiagnosticRequest Request,
    int? DiagnosticPort,
    int Concurrency,
    TimeSpan Deadline,
    bool RetryOnce)
{
    /// <summary>Optional immutable per-profile history continuations.</summary>
    public IReadOnlyDictionary<string, DiagnosticHistoryQuery>? HistoryQueries { get; init; }

    /// <summary>Default number of concurrent native exchanges.</summary>
    public const int DefaultConcurrency = 8;

    /// <summary>Largest number of concurrent native exchanges accepted from callers.</summary>
    public const int MaximumConcurrency = 16;

    /// <summary>Default total deadline shared by queued and active fleet rows.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(15);

    /// <summary>Largest total deadline accepted from callers.</summary>
    public static readonly TimeSpan MaximumDeadline = TimeSpan.FromSeconds(60);

    /// <summary>Largest explicit fleet allowlist accepted by one operation.</summary>
    public const int MaximumProfiles = 128;
}

/// <summary>One immutable attempt made while collecting an explicitly requested profile.</summary>
public sealed record DiagnosticFleetAttempt(int Number, string Code, DiagnosticResultKind? Kind, DateTimeOffset CollectedAt);

/// <summary>One complete row for a caller-supplied allowlist item, including local refusal rows.</summary>
public sealed record DiagnosticFleetRow(
    string RequestedProfile,
    string Code,
    DiagnosticResultKind? Kind,
    bool Recovered,
    IReadOnlyList<DiagnosticFleetAttempt> Attempts,
    DateTimeOffset CollectedAt,
    DiagnosticResult? Result);

/// <summary>
/// Completed counts profiles with a finished exchange attempt, including failures;
/// Attempted counts all exchanges including retries. Neither implies success.
/// </summary>
public sealed record DiagnosticFleetCoverage(int Requested, int Completed, int Validated, int SourceAvailable, int SourceUnavailable, int Recovered, int Attempted)
{
    /// <summary>Gets profiles with no exchange attempt, including local refusals and expired queue entries.</summary>
    public int NotAttempted => Requested - Completed;
}

/// <summary>Immutable result of one bounded fleet collection operation.</summary>
public sealed record DiagnosticFleetResult(IReadOnlyList<DiagnosticFleetRow> Rows, DiagnosticFleetCoverage Coverage, DateTimeOffset StartedAt, DateTimeOffset CompletedAt);


/// <summary>Immutable query binding carried on every history continuation.</summary>
public sealed record DiagnosticHistoryQuery(string Source, DateTimeOffset Since, DateTimeOffset Until, int PageLines, string QueryId, string? Cursor = null, long Sequence = 0, string UnknownTime = "include")
{
    /// <summary>Validates bounds without limiting the total retained time range.</summary>
    public bool IsValid => (UnknownTime is "include" or "report-only") && (Source is "logs" or "guardian-events") && Since < Until && PageLines is >= 1 and <= 500 && IsId(QueryId) && (Cursor is null || IsId(Cursor)) && Sequence >= 0 && Sequence < long.MaxValue && (Cursor is not null || Sequence == 0);

    /// <summary>Validates an opaque identifier; it is never a file path or credential.</summary>
    public static bool IsId(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigit);

    /// <summary>Requires an explicit timezone offset for a request boundary.</summary>
    public static bool TryTime(string? value, out DateTimeOffset time)
    {
        time = default;
        return value is { Length: >= 20 and <= 40 } &&
            (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || (value.Length >= 6 && value[^3] == ':' && value[^6] is '+' or '-')) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    /// <summary>Serializes only bounded, typed native history arguments.</summary>
    public JObject ToArgs()
    {
        var args = new JObject
        {
            ["source"] = Source, ["since"] = Since.ToString("O", CultureInfo.InvariantCulture),
            ["until"] = Until.ToString("O", CultureInfo.InvariantCulture), ["page_lines"] = PageLines,
            ["query_id"] = QueryId, ["cursor"] = Cursor is null ? JValue.CreateNull() : new JValue(Cursor), ["sequence"] = Sequence
        };
        if (UnknownTime != "include") args["unknown_time"] = UnknownTime;
        return args;
    }

    /// <summary>Parses a strict native history request before any file access.</summary>
    public static bool TryParse(JObject args, out DiagnosticHistoryQuery? query)
    {
        query = null;
        if (args.Properties().Any(p => p.Name is not ("source" or "since" or "until" or "page_lines" or "query_id" or "cursor" or "sequence" or "unknown_time")) ||
            args["source"]?.Type != JTokenType.String || args["since"]?.Type != JTokenType.String || args["until"]?.Type != JTokenType.String ||
            args["page_lines"]?.Type != JTokenType.Integer || args["sequence"]?.Type != JTokenType.Integer || args["query_id"]?.Type != JTokenType.String || args["unknown_time"] is not null && args["unknown_time"]!.Type != JTokenType.String ||
            (args["cursor"] is not null && args["cursor"]!.Type is not (JTokenType.Null or JTokenType.String)) ||
            !TryTime(args["since"]!.Value<string>(), out DateTimeOffset since) || !TryTime(args["until"]!.Value<string>(), out DateTimeOffset until)) return false;
        try
        {
            query = new DiagnosticHistoryQuery(args["source"]!.Value<string>()!, since, until, args["page_lines"]!.Value<int>(), args["query_id"]!.Value<string>()!, args["cursor"]?.Value<string>(), args["sequence"]!.Value<long>(), args["unknown_time"]?.Value<string>() ?? "include");
            return query.IsValid;
        }
        catch (Exception e) when (e is OverflowException or FormatException or InvalidCastException) { return false; }
    }
}
