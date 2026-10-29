using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using MTTextClient.Core;
using MTTextClient.Core.Diagnostics;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Commands;

/// <summary>Runs bounded read-only diagnostics against a separately configured companion endpoint.</summary>
public sealed partial class DiagnosticCommand : ICommand, IDisposable
{
    private readonly IDiagnosticExecutor _executor;
    private readonly bool _ownsExecutor;
    private readonly bool _ownsInvestigations;
    private readonly CancellationToken _cancellationToken;
    private readonly Func<List<ServerProfile>> _loadProfiles;
    private readonly DiagnosticInvestigationManager _investigations;
    private readonly bool _supportsPersistentSessions;
    private readonly DiagnosticFleetCollector _fleetCollector;

    /// <summary>Creates the command with injectable local seams for tests.</summary>
    public DiagnosticCommand(IDiagnosticExecutor? executor = null, Func<List<ServerProfile>>? loadProfiles = null, DiagnosticInvestigationManager? investigations = null, bool supportsPersistentSessions = true, DiagnosticFleetCollector? fleetCollector = null, CancellationToken cancellationToken = default)
    {
        _ownsExecutor = executor is null;
        _ownsInvestigations = investigations is null;
        _executor = executor ?? new MtDiagClient();
        _cancellationToken = cancellationToken;
        _loadProfiles = loadProfiles ?? ProfileManager.LoadProfiles;
        _investigations = investigations ?? new DiagnosticInvestigationManager();
        _supportsPersistentSessions = supportsPersistentSessions;
        _fleetCollector = fleetCollector ?? new DiagnosticFleetCollector(_executor);
        _investigations.ConfigureFollowUp(CollectFollowUpAsync);
    }

    /// <summary>Releases only resources created by this command.</summary>
    public void Dispose()
    {
        if (_ownsInvestigations) _investigations.Dispose();
        if (_ownsExecutor && _executor is IDisposable disposable) disposable.Dispose();
    }

    /// <inheritdoc />
    public string Name => "diag";

    /// <inheritdoc />
    public string Description => "Query a configured read-only diagnostic companion.";

    /// <inheritdoc />
    public string Usage => "diag <capabilities|health|logs|guardian-status|guardian-events> <profile> [--diag-port <1-65535>] [--timeout-ms <1-15000>] [--lines <1-500>] [--grep <text>] | diag fleet collect --profiles <saved-names> --operation <operation> [--concurrency <1-16>] [--deadline-ms <1-60000>] [--retry-once] | diag investigation <collect|start|export|stop> ... (start/export/stop require one persistent REPL, MCP, or daemon process)";

    /// <inheritdoc />
    public CommandResult Execute(string[] args)
    {
        if (args is not null && args.Length > 0 && string.Equals(args[0], "history", StringComparison.OrdinalIgnoreCase)) return ExecuteHistory(args[1..]);
        if (args is not null && args.Length > 0 && string.Equals(args[0], "investigation", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteInvestigation(args[1..]);
        }
        if (args is not null && args.Length > 0 && string.Equals(args[0], "fleet", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteFleet(args[1..]);
        }

        if (!TryParse(args, out ParsedCommand? parsed, out string error))
        {
            return CommandResult.Fail(error);
        }

        List<ServerProfile> profiles = _loadProfiles();
        ServerProfile? profile = profiles.SingleOrDefault(candidate => string.Equals(candidate.Name, parsed!.ProfileName, StringComparison.Ordinal));
        if (profile is null)
        {
            return CommandResult.Fail("Diagnostic profile was not found.");
        }

        if (!TryResolveRoute(profile, parsed!, out DiagnosticRoute? route, out error))
        {
            return CommandResult.Fail(error);
        }
        if (profiles.Any(candidate =>
            string.Equals(candidate.Address, profile.Address, StringComparison.OrdinalIgnoreCase) &&
            candidate.Port == route!.Port))
        {
            return CommandResult.Fail("Diagnostic port must not match a saved MTCore endpoint.");
        }

        var evidence = new DiagnosticIdentityEvidence(profile.Name, null, profiles.Select(candidate => candidate.Name).ToArray());
        DiagnosticResult result = _executor.ExecuteAsync(profile, route!, parsed!.Request, evidence, TimeSpan.FromMilliseconds(parsed.TimeoutMilliseconds), CancellationToken.None).GetAwaiter().GetResult();
        return ToCommandResult(result);
    }

    private CommandResult ExecuteFleet(string[] args)
    {
        if (!TryParseFleet(args, out FleetCommand? parsed, out string error)) return CommandResult.Fail(error);
        try
        {
            DiagnosticFleetResult result = _fleetCollector.CollectAsync(
                new DiagnosticFleetRequest(parsed!.Profiles, parsed.Request, parsed.Port, parsed.Concurrency, parsed.Deadline, parsed.RetryOnce),
                _loadProfiles(), CancellationToken.None).GetAwaiter().GetResult();
            bool success = result.Coverage.Validated == result.Coverage.Requested;
            return new CommandResult
            {
                Success = success,
                Message = success ? "Diagnostic fleet collection completed." : "Diagnostic fleet collection contains unvalidated rows.",
                Data = ToFleetData(result)
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return CommandResult.Fail("Diagnostic fleet inputs were outside their allowed bounds.");
        }
    }

    private CommandResult ExecuteInvestigation(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "start", StringComparison.OrdinalIgnoreCase))
        {
            return StartInvestigation(args[1..]);
        }
        if (args.Length > 0 && string.Equals(args[0], "stop", StringComparison.OrdinalIgnoreCase))
        {
            return args.Length == 2 && DiagnosticInvestigationManager.IsSessionId(args[1]) && _investigations.Stop(args[1])
                ? CommandResult.Ok("Diagnostic investigation stopped.", new JObject { ["session_id"] = args[1] })
                : CommandResult.Fail("Diagnostic investigation session was not found.");
        }
        if (args.Length > 0 && string.Equals(args[0], "export", StringComparison.OrdinalIgnoreCase))
        {
            return ExportInvestigation(args[1..]);
        }
        if (!TryParseCollection(args, out InvestigationCommand? parsed, out string error))
        {
            return CommandResult.Fail(error);
        }

        List<ServerProfile> profiles = _loadProfiles();
        ServerProfile? profile = profiles.SingleOrDefault(candidate => string.Equals(candidate.Name, parsed!.ProfileName, StringComparison.Ordinal));
        if (profile is null)
        {
            return CommandResult.Fail("Diagnostic profile was not found.");
        }

        var routeCommand = new ParsedCommand(parsed!.ProfileName, parsed.Port, (int)MtDiagClient.DefaultTimeout.TotalMilliseconds, DiagnosticRequest.Health());
        if (!TryResolveRoute(profile, routeCommand, out DiagnosticRoute? route, out error))
        {
            return CommandResult.Fail(error);
        }
        if (profiles.Any(candidate => string.Equals(candidate.Address, profile.Address, StringComparison.OrdinalIgnoreCase) && candidate.Port == route!.Port))
        {
            return CommandResult.Fail("Diagnostic port must not match a saved MTCore endpoint.");
        }

        var identity = new DiagnosticIdentityEvidence(profile.Name, null, profiles.Select(candidate => candidate.Name).ToArray());
        try
        {
            var collector = new DiagnosticInvestigationCollector(_executor);
            DiagnosticEvidenceBundle bundle = collector.CollectAsync(profile, route!, identity, parsed.Deadline, parsed.IncludeHashes, CancellationToken.None).GetAwaiter().GetResult();
            return CommandResult.Ok("Diagnostic evidence collection completed.", JObject.Parse(DiagnosticEvidenceBundleSerializer.Serialize(bundle)));
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Fail("Diagnostic evidence collection was cancelled.");
        }
    }

    private CommandResult StartInvestigation(string[] args)
    {
        if (!_supportsPersistentSessions)
        {
            return CommandResult.Fail("Diagnostic investigation sessions require a persistent REPL, MCP, or daemon process; one-shot CLI supports collect only.");
        }
        if (!TryParseStart(args, out string? profile, out int ttlSeconds, out string[]? eventTypes, out string error)) return CommandResult.Fail(error);
        if (!_loadProfiles().Any(candidate => string.Equals(candidate.Name, profile, StringComparison.Ordinal))) return CommandResult.Fail("Diagnostic profile was not found.");
        try
        {
            DiagnosticInvestigationSession session = _investigations.Start(profile!, TimeSpan.FromSeconds(ttlSeconds), eventTypes!);
            return CommandResult.Ok("Diagnostic investigation started.", DiagnosticInvestigationExportSerializer.SerializeStart(session));
        }
        catch (ArgumentException exception)
        {
            return CommandResult.Fail(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return CommandResult.Fail(exception.Message);
        }
    }

    private CommandResult ExportInvestigation(string[] args)
    {
        if (args.Length != 1 || !_investigations.TryGetSnapshot(args[0], out DiagnosticInvestigationSnapshot? snapshot)) return CommandResult.Fail("Diagnostic investigation session was not found.");
        return CommandResult.Ok("Diagnostic investigation evidence exported in the response.", DiagnosticInvestigationExportSerializer.Serialize(snapshot!));
    }

    private async System.Threading.Tasks.Task<DiagnosticEvidenceBundle> CollectFollowUpAsync(string profileName, string trigger, CancellationToken cancellationToken)
    {
        List<ServerProfile> profiles = _loadProfiles();
        ServerProfile? profile = profiles.SingleOrDefault(candidate => string.Equals(candidate.Name, profileName, StringComparison.Ordinal));
        if (profile is null || !TryResolveRoute(profile, new ParsedCommand(profileName, null, (int)MtDiagClient.DefaultTimeout.TotalMilliseconds, DiagnosticRequest.Health()), out DiagnosticRoute? route, out _) || profiles.Any(candidate => string.Equals(candidate.Address, profile.Address, StringComparison.OrdinalIgnoreCase) && candidate.Port == route!.Port))
        {
            return DiagnosticEvidenceBundle.Create(profileName, trigger, DateTimeOffset.UtcNow,
                new[] { new DiagnosticEvidenceObservation("collection", "not_configured", null, DateTimeOffset.UtcNow, null, true, false, null) }, false);
        }
        var identity = new DiagnosticIdentityEvidence(profile.Name, null, profiles.Select(candidate => candidate.Name).ToArray());
        var collector = new DiagnosticInvestigationCollector(_executor);
        return await collector.CollectAsync(profile, route!, identity, DiagnosticInvestigationCollector.DefaultDeadline, false, cancellationToken, trigger).ConfigureAwait(false);
    }

    private static bool TryParseCollection(string[] args, out InvestigationCommand? parsed, out string error)
    {
        parsed = null;
        error = "Use: diag investigation collect <profile> [--diag-port <1-65535>] [--deadline-ms <1-60000>] [--hash].";
        if (args.Length < 2 || !string.Equals(args[0], "collect", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(args[1]))
        {
            return false;
        }

        int? port = null;
        int deadlineMilliseconds = (int)DiagnosticInvestigationCollector.DefaultDeadline.TotalMilliseconds;
        bool includeHashes = false;
        for (int index = 2; index < args.Length; index++)
        {
            string option = args[index];
            if (option == "--hash")
            {
                if (includeHashes) return false;
                includeHashes = true;
                continue;
            }
            if (index + 1 >= args.Length) return false;
            string value = args[++index];
            if (option == "--diag-port")
            {
                if (port is not null || !TryBoundedInteger(value, 1, 65535, out int parsedPort)) { error = "Diagnostic port must be between 1 and 65535."; return false; }
                port = parsedPort;
            }
            else if (option == "--deadline-ms")
            {
                if (!TryBoundedInteger(value, 1, (int)DiagnosticInvestigationCollector.MaximumDeadline.TotalMilliseconds, out deadlineMilliseconds)) { error = "Diagnostic deadline must be between 1 and 60000 milliseconds."; return false; }
            }
            else
            {
                return false;
            }
        }
        parsed = new InvestigationCommand(args[1], port, TimeSpan.FromMilliseconds(deadlineMilliseconds), includeHashes);
        return true;
    }

    private static bool TryParseStart(string[] args, out string? profile, out int ttlSeconds, out string[]? eventTypes, out string error)
    {
        profile = null;
        ttlSeconds = 0;
        eventTypes = null;
        error = "Use: diag investigation start <profile> --ttl-seconds <1-300> --event-types <types>.";
        if (args.Length < 5 || string.IsNullOrWhiteSpace(args[0])) return false;
        profile = args[0];
        for (int index = 1; index < args.Length; index++)
        {
            if (index + 1 >= args.Length) return false;
            string option = args[index];
            string value = args[++index];
            if (option == "--ttl-seconds")
            {
                if (ttlSeconds != 0 || !TryBoundedInteger(value, 1, 300, out ttlSeconds)) return false;
            }
            else if (option == "--event-types")
            {
                if (eventTypes is not null || string.IsNullOrWhiteSpace(value)) return false;
                eventTypes = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            else return false;
        }
        return ttlSeconds != 0 && eventTypes is { Length: > 0 };
    }

    private static bool TryParse(string[]? args, out ParsedCommand? parsed, out string error)
    {
        parsed = null;
        error = "Invalid diagnostic command. " + "Use: diag <operation> <profile> --diag-port <port> [options].";
        if (args is null || args.Length < 2 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1]))
        {
            return false;
        }

        if (!TryOperation(args[0], out DiagnosticOperation operation))
        {
            error = "Unknown diagnostic operation.";
            return false;
        }

        int? port = null;
        int timeoutMilliseconds = (int)MtDiagClient.DefaultTimeout.TotalMilliseconds;
        int? lines = null;
        string? grep = null;
        for (int index = 2; index < args.Length; index++)
        {
            string option = args[index];
            if (option is not "--diag-port" and not "--timeout-ms" and not "--lines" and not "--grep" || index + 1 >= args.Length)
            {
                error = "Unsupported or incomplete diagnostic option.";
                return false;
            }
            string value = args[++index];
            if (option == "--diag-port")
            {
                if (port is not null || !TryBoundedInteger(value, 1, 65535, out int parsedPort)) { error = "Diagnostic port must be between 1 and 65535."; return false; }
                port = parsedPort;
            }
            else if (option == "--timeout-ms")
            {
                if (!TryBoundedInteger(value, 1, (int)MtDiagClient.MaximumTimeout.TotalMilliseconds, out timeoutMilliseconds)) { error = "Diagnostic timeout must be between 1 and 15000 milliseconds."; return false; }
            }
            else if (option == "--lines")
            {
                if (lines is not null || !TryBoundedInteger(value, 1, DiagnosticRequest.MaximumLines, out int parsedLines)) { error = "Diagnostic lines must be between 1 and 500."; return false; }
                lines = parsedLines;
            }
            else
            {
                if (grep is not null || !TryValidGrep(value)) { error = "Diagnostic grep must be valid UTF-8 text up to 512 bytes."; return false; }
                grep = value;
            }
        }

        if (operation is DiagnosticOperation.Logs or DiagnosticOperation.GuardianEvents)
        {
            if (lines is null || (operation == DiagnosticOperation.GuardianEvents && grep is not null)) { error = operation == DiagnosticOperation.Logs ? "Logs requires --lines <1-500>." : "Guardian events requires --lines <1-500> and does not accept --grep."; return false; }
        }
        else if (lines is not null || grep is not null)
        {
            error = "This diagnostic operation does not accept --lines or --grep.";
            return false;
        }

        parsed = new ParsedCommand(args[1], port, timeoutMilliseconds, operation switch
        {
            DiagnosticOperation.Capabilities => DiagnosticRequest.Capabilities(),
            DiagnosticOperation.Health => DiagnosticRequest.Health(),
            DiagnosticOperation.Logs => DiagnosticRequest.Logs(lines!.Value, grep),
            DiagnosticOperation.GuardianStatus => DiagnosticRequest.GuardianStatus(),
            DiagnosticOperation.GuardianEvents => DiagnosticRequest.GuardianEvents(lines!.Value),
            _ => throw new InvalidOperationException("Unsupported diagnostic operation.")
        });
        return true;
    }

    private static bool TryParseFleet(string[] args, out FleetCommand? parsed, out string error)
    {
        parsed = null;
        error = "Use: diag fleet collect --profiles <saved-name,...> --operation <capabilities|health|logs|guardian-status|guardian-events> [--diag-port <1-65535>] [--concurrency <1-16>] [--deadline-ms <1-60000>] [--retry-once] [--lines <1-500>] [--grep <text>].";
        if (args.Length < 5 || !string.Equals(args[0], "collect", StringComparison.OrdinalIgnoreCase)) return false;

        string[]? profiles = null;
        DiagnosticOperation? operation = null;
        int? port = null;
        int concurrency = DiagnosticFleetRequest.DefaultConcurrency;
        int deadlineMilliseconds = (int)DiagnosticFleetRequest.DefaultDeadline.TotalMilliseconds;
        bool retryOnce = false;
        int? lines = null;
        string? grep = null;
        for (int index = 1; index < args.Length; index++)
        {
            string option = args[index];
            if (option == "--retry-once")
            {
                if (retryOnce) return false;
                retryOnce = true;
                continue;
            }
            if (index + 1 >= args.Length) return false;
            string value = args[++index];
            if (option == "--profiles")
            {
                if (profiles is not null) return false;
                profiles = value.Split(',', StringSplitOptions.TrimEntries);
                if (profiles.Length is < 1 or > DiagnosticFleetRequest.MaximumProfiles || profiles.Any(string.IsNullOrWhiteSpace)) return false;
            }
            else if (option == "--operation")
            {
                if (operation is not null || !TryOperation(value, out DiagnosticOperation parsedOperation)) return false;
                operation = parsedOperation;
            }
            else if (option == "--diag-port")
            {
                if (port is not null || !TryBoundedInteger(value, 1, 65535, out int parsedPort)) return false;
                port = parsedPort;
            }
            else if (option == "--concurrency")
            {
                if (!TryBoundedInteger(value, 1, DiagnosticFleetRequest.MaximumConcurrency, out concurrency)) return false;
            }
            else if (option == "--deadline-ms")
            {
                if (!TryBoundedInteger(value, 1, (int)DiagnosticFleetRequest.MaximumDeadline.TotalMilliseconds, out deadlineMilliseconds)) return false;
            }
            else if (option == "--lines")
            {
                if (lines is not null || !TryBoundedInteger(value, 1, DiagnosticRequest.MaximumLines, out int parsedLines)) return false;
                lines = parsedLines;
            }
            else if (option == "--grep")
            {
                if (grep is not null || !TryValidGrep(value)) return false;
                grep = value;
            }
            else return false;
        }
        if (profiles is null || operation is null) return false;
        if (operation is DiagnosticOperation.Logs or DiagnosticOperation.GuardianEvents)
        {
            if (lines is null || (operation == DiagnosticOperation.GuardianEvents && grep is not null)) return false;
        }
        else if (lines is not null || grep is not null) return false;
        DiagnosticRequest request = operation.Value switch
        {
            DiagnosticOperation.Capabilities => DiagnosticRequest.Capabilities(),
            DiagnosticOperation.Health => DiagnosticRequest.Health(),
            DiagnosticOperation.Logs => DiagnosticRequest.Logs(lines!.Value, grep),
            DiagnosticOperation.GuardianStatus => DiagnosticRequest.GuardianStatus(),
            DiagnosticOperation.GuardianEvents => DiagnosticRequest.GuardianEvents(lines!.Value),
            _ => throw new InvalidOperationException("Unsupported diagnostic operation.")
        };
        parsed = new FleetCommand(profiles, request, port, concurrency, TimeSpan.FromMilliseconds(deadlineMilliseconds), retryOnce);
        return true;
    }

    private static bool TryResolveRoute(ServerProfile profile, ParsedCommand command, out DiagnosticRoute? route, out string error)
    {
        route = null;
        error = "A diagnostic port is required via --diag-port or the mtdiag_port profile tag.";
        int port;
        string source;
        if (command.Port is not null)
        {
            port = command.Port.Value;
            source = "explicit";
        }
        else if (profile.Tags is not null && profile.Tags.TryGetValue("mtdiag_port", out string? tag) && TryBoundedInteger(tag, 1, 65535, out port))
        {
            source = "tag";
        }
        else
        {
            return false;
        }
        if (port == profile.Port)
        {
            error = "Diagnostic port must not equal the MTCore port.";
            return false;
        }
        route = new DiagnosticRoute(profile.Address, port, source);
        return true;
    }

    private static bool TryOperation(string value, out DiagnosticOperation operation)
    {
        operation = value switch
        {
            "capabilities" => DiagnosticOperation.Capabilities,
            "health" => DiagnosticOperation.Health,
            "logs" => DiagnosticOperation.Logs,
            "guardian-status" => DiagnosticOperation.GuardianStatus,
            "guardian-events" => DiagnosticOperation.GuardianEvents,
            _ => default
        };
        return value is "capabilities" or "health" or "logs" or "guardian-status" or "guardian-events";
    }

    private static bool TryBoundedInteger(string value, int minimum, int maximum, out int result) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= minimum && result <= maximum;

    private static bool TryValidGrep(string value)
    {
        try { return new System.Text.UTF8Encoding(false, true).GetByteCount(value) <= DiagnosticRequest.MaximumGrepUtf8Bytes; }
        catch (System.Text.EncoderFallbackException) { return false; }
    }

    private static CommandResult ToCommandResult(DiagnosticResult result)
    {
        var data = new JObject { ["code"] = result.Code, ["kind"] = result.Kind.ToString() };
        if (result.Kind == DiagnosticResultKind.Success)
        {
            data["profile"] = result.Profile;
            data["source"] = result.Source;
            data["data"] = result.Data;
            data["metadata"] = result.Metadata;
            return CommandResult.Ok("Diagnostic request completed.", data);
        }
        if (result.Error is not null) data["error"] = result.Error;
        if (DiagnosticFailureGuidance(result) is string guidance) data["next_action"] = guidance;
        return new CommandResult { Success = false, Message = "Diagnostic request failed: " + result.Code + ".", Data = data };
    }

    private static string? DiagnosticFailureGuidance(DiagnosticResult result) => result switch
    {
        { Error: "busy" or "history-busy" or "history-cursor-capacity" } => "Back off and retry the same query/cursor/sequence; do not advance. Reduce concurrency or close unused active cursors.",
        { Error: "history-restart-required" or "history-source-changed" } => "Preserve committed evidence. Start a fresh query explicitly; the old cursor cannot continue. Do not silently reset the export.",
        { Error: "history-query-mismatch" or "history-sequence-mismatch" } => "Restore the original query binding and expected sequence; only the last page can be replayed.",
        { Code: "native-client-capacity" } => "The local diagnostic session pool is busy. Reduce concurrency and retry without advancing history.",
        { Error: "profile-unbound" } => "Match the saved profile name exactly to the companion's configured profile name.",
        { Code: "native-connection-rejected" } => "The endpoint refused admission. Listener capacity, credentials/exchange or network policy can cause this; rejection is not proof of a bad token. Back off and reduce concurrent clients before checking the saved token and exchange. The endpoint identity is not authenticated.",
        { Kind: DiagnosticResultKind.Timeout } => "No authenticated diagnostic reply before the deadline. Check companion startup, the diagnostic UDP route from this caller, and the saved profile credentials. Silence alone cannot distinguish filtering, an unavailable companion, or invalid credentials.",
        _ => null
    };

    private static JObject ToFleetData(DiagnosticFleetResult result)
    {
        var rows = new JArray();
        foreach (DiagnosticFleetRow row in result.Rows)
        {
            var value = new JObject
            {
                ["requested_profile"] = row.RequestedProfile,
                ["code"] = row.Code,
                ["attempted"] = row.Attempts.Count > 0,
                ["recovered"] = row.Recovered,
                ["collected_at"] = row.CollectedAt.ToUnixTimeSeconds(),
                ["attempts"] = new JArray(row.Attempts.Select(attempt => new JObject
                {
                    ["number"] = attempt.Number,
                    ["code"] = attempt.Code,
                    ["kind"] = attempt.Kind?.ToString(),
                    ["collected_at"] = attempt.CollectedAt.ToUnixTimeSeconds()
                }))
            };
            if (row.Result?.Error is not null) value["error"] = row.Result.Error;
            if (row.Kind is not null) value["kind"] = row.Kind.ToString();
            if (row.Attempts.Count == 0 && row.Kind is DiagnosticResultKind.Timeout or DiagnosticResultKind.Cancelled)
            {
                value["next_action"] = "No exchange was attempted before the shared deadline or cancellation. This row provides no evidence about remote reachability.";
            }
            else if (row.Result is not null && DiagnosticFailureGuidance(row.Result) is string guidance)
            {
                value["next_action"] = guidance;
            }
            if (row.Result?.Kind == DiagnosticResultKind.Success)
            {
                value["profile"] = row.Result.Profile;
                value["source"] = row.Result.Source;
                value["data"] = row.Result.Data;
                value["metadata"] = row.Result.Metadata;
            }
            rows.Add(value);
        }
        return new JObject
        {
            ["rows"] = rows,
            ["coverage"] = new JObject
            {
                ["requested"] = result.Coverage.Requested,
                ["completed"] = result.Coverage.Completed,
                ["not_attempted"] = result.Coverage.NotAttempted,
                ["rows_returned"] = result.Rows.Count,
                ["validated"] = result.Coverage.Validated,
                ["source_available"] = result.Coverage.SourceAvailable,
                ["source_unavailable"] = result.Coverage.SourceUnavailable,
                ["recovered"] = result.Coverage.Recovered,
                ["attempted"] = result.Coverage.Attempted
            },
            ["started_at"] = result.StartedAt.ToUnixTimeSeconds(),
            ["completed_at"] = result.CompletedAt.ToUnixTimeSeconds()
        };
    }

    private sealed record ParsedCommand(string ProfileName, int? Port, int TimeoutMilliseconds, DiagnosticRequest Request);

    private sealed record InvestigationCommand(string ProfileName, int? Port, TimeSpan Deadline, bool IncludeHashes);

    private sealed record FleetCommand(string[] Profiles, DiagnosticRequest Request, int? Port, int Concurrency, TimeSpan Deadline, bool RetryOnce);
}
