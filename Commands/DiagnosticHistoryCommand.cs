using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using MTTextClient.Core;
using MTTextClient.Core.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Commands;

public sealed partial class DiagnosticCommand
{
    private CommandResult ExecuteHistory(string[] args)
    {
        if (args.Length < 2 || args[0] is not ("page" or "close" or "collect" or "fleet")) return CommandResult.Fail("Use diag history page|close|collect <profile> --source logs|guardian-events --since <offset-time> --until <offset-time> --query-id <32-hex> [--cursor <id> --sequence <n>].");
        try
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            int start = args[0] == "fleet" ? 1 : 2;
            for (int i = start; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length && args[i] == "--output") return CommandResult.Fail("--output requires a non-empty private directory.");
                if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i][2..], args[i + 1])) return CommandResult.Fail("Invalid or duplicate history option.");
            }
            if (options.Keys.Any(k => k is not ("source" or "since" or "until" or "unknown-time" or "page-lines" or "query-id" or "cursor" or "sequence" or "diag-port" or "timeout-ms" or "requests" or "concurrency" or "deadline-ms" or "output"))) return CommandResult.Fail("Unknown history option.");
            foreach (var bound in new[] { (Name: "diag-port", Max: 65535), (Name: "timeout-ms", Max: 15000), (Name: "deadline-ms", Max: 60000), (Name: "page-lines", Max: 500), (Name: "concurrency", Max: 16) })
                if (options.TryGetValue(bound.Name, out string? value) && !TryBoundedInteger(value, 1, bound.Max, out _))
                    return CommandResult.Fail("--" + bound.Name + " must be between 1 and " + bound.Max.ToString(CultureInfo.InvariantCulture) + ".");
            if (args[0] == "collect" && (!options.TryGetValue("output", out string? requestedOutput) || string.IsNullOrWhiteSpace(requestedOutput)))
                return CommandResult.Fail("--output requires a non-empty private directory; the same query and directory resume the checkpoint.");
            int? port = options.TryGetValue("diag-port", out string? portText) ? ParseHistoryInteger(portText, 1, 65535) : null;
            if (args[0] == "fleet") return ExecuteHistoryFleet(options, port);
            if (options.Keys.Any(k => k is "requests" or "concurrency" || k == "deadline-ms" && args[0] != "collect") || args[0] != "collect" && options.ContainsKey("output")) return CommandResult.Fail("History option does not apply to this operation.");
            if (!HistoryProfileName(args[1])) return CommandResult.Fail("Invalid saved profile name.");
            var raw = new JObject { ["source"] = options.GetValueOrDefault("source"), ["since"] = options.GetValueOrDefault("since"), ["until"] = options.GetValueOrDefault("until"), ["page_lines"] = options.TryGetValue("page-lines", out string? lines) ? ParseHistoryInteger(lines, 1, 500) : 200, ["query_id"] = options.GetValueOrDefault("query-id"), ["cursor"] = options.GetValueOrDefault("cursor"), ["sequence"] = options.TryGetValue("sequence", out string? sequence) ? long.Parse(sequence, NumberStyles.None, CultureInfo.InvariantCulture) : 0 };
            if (options.TryGetValue("unknown-time", out string? unknownTime)) raw["unknown_time"] = unknownTime;
            if (!DiagnosticHistoryQuery.TryParse(raw, out DiagnosticHistoryQuery? query) || args[0] == "close" && query!.Cursor is null) return CommandResult.Fail("History requires a source, increasing explicit-offset since/until, 32-hex query ID, and valid cursor/sequence.");
            int timeout = options.TryGetValue("timeout-ms", out string? timeoutText) ? ParseHistoryInteger(timeoutText, 1, 15000) : 5000;
            List<ServerProfile> profiles = _loadProfiles();
            ServerProfile[] selected = profiles.Where(p => p.Name == args[1]).ToArray();
            if (selected.Length != 1) return CommandResult.Fail("Diagnostic profile was not found or is ambiguous.");
            ServerProfile profile = selected[0];
            if (!TryResolveRoute(profile, new ParsedCommand(profile.Name, port, timeout, DiagnosticRequest.Health()), out DiagnosticRoute? route, out string error)) return CommandResult.Fail(error);
            if (profiles.Any(p => p.Address.Equals(route!.Address, StringComparison.OrdinalIgnoreCase) && p.Port == route.Port)) return CommandResult.Fail("Diagnostic port must not match a saved MTCore endpoint.");
            var identity = new DiagnosticIdentityEvidence(profile.Name, null, profiles.Select(p => p.Name).ToArray());
            if (args[0] == "collect")
            {
                if (!options.TryGetValue("output", out string? output)) return CommandResult.Fail("History collect requires a private output directory; repeating the same query and directory resumes it.");
                int deadline = options.TryGetValue("deadline-ms", out string? totalTime) ? ParseHistoryInteger(totalTime, 1, 60000) : 60000;
                JObject summary;
                try
                {
                    summary = DiagnosticHistoryExport.CollectAsync(_executor, profile, route!, identity, query!, TimeSpan.FromMilliseconds(timeout), TimeSpan.FromMilliseconds(deadline), output, _cancellationToken).GetAwaiter().GetResult();
                }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or InvalidOperationException or JsonException)
                {
                    return CommandResult.Fail("History --output directory or checkpoint cannot be used. Check owner-only permissions, non-symlink paths, write access, writer locks and the original query binding. No automatic cursor restart was performed.");
                }
                return new CommandResult { Success = summary["complete"]!.Value<bool>(), Message = "History export checkpoint saved; inspect coverage and continuation.", Data = summary };
            }
            DiagnosticResult result = _executor.ExecuteAsync(profile, route!, DiagnosticRequest.HistoryRequest(query!, args[0] == "close"), identity, TimeSpan.FromMilliseconds(timeout), _cancellationToken).GetAwaiter().GetResult();
            return ToCommandResult(result);
        }
        catch (Exception e) when (e is OperationCanceledException or ArgumentException or OverflowException or FormatException or JsonException or System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return CommandResult.Fail("History request or private output is invalid or unavailable; no automatic cursor restart was performed.");
        }
    }

    private CommandResult ExecuteHistoryFleet(Dictionary<string, string> options, int? port)
    {
        if (options.Keys.Any(k => k is not ("requests" or "diag-port" or "concurrency" or "deadline-ms")) || !options.TryGetValue("requests", out string? encoded) || encoded.Length > 128 * 1024) return CommandResult.Fail("History fleet requires bounded base64 JSON requests.");
        byte[] bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > 64 * 1024) return CommandResult.Fail("History fleet request is too large.");
        using var jsonReader = new JsonTextReader(new System.IO.StringReader(new UTF8Encoding(false, true).GetString(bytes))) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 };
        JArray rows = JArray.Load(jsonReader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (jsonReader.Read() || rows.Count is < 1 or > 128) return CommandResult.Fail("History fleet requires 1–128 explicit profiles.");
        var queries = new Dictionary<string, DiagnosticHistoryQuery>(StringComparer.Ordinal);
        foreach (JToken token in rows)
        {
            if (token is not JObject row || row["profile"]?.Type != JTokenType.String || !HistoryProfileName(row["profile"]!.Value<string>()!)) return CommandResult.Fail("Invalid history profile.");
            string name = row["profile"]!.Value<string>()!;
            JObject queryArgs = (JObject)row.DeepClone(); queryArgs.Remove("profile");
            if (!DiagnosticHistoryQuery.TryParse(queryArgs, out DiagnosticHistoryQuery? query) || !queries.TryAdd(name, query!)) return CommandResult.Fail("Invalid or duplicate history query.");
        }
        int concurrency = options.TryGetValue("concurrency", out string? workers) ? ParseHistoryInteger(workers, 1, 16) : 8;
        int deadline = options.TryGetValue("deadline-ms", out string? time) ? ParseHistoryInteger(time, 1, 60000) : 15000;
        DiagnosticFleetResult result = _fleetCollector.CollectAsync(new DiagnosticFleetRequest(queries.Keys.ToArray(), DiagnosticRequest.HistoryRequest(queries.Values.First()), port, concurrency, TimeSpan.FromMilliseconds(deadline), false) { HistoryQueries = queries }, _loadProfiles(), _cancellationToken).GetAwaiter().GetResult();
        JObject data = ToFleetData(result);
        data["history"] = new JObject
        {
            ["meaning"] = "One page per profile; validated pages are not completed time windows.",
            ["windows_exhausted"] = result.Rows.Count(r => r.Result?.Data?["has_more"]?.Type == JTokenType.Boolean && !r.Result.Data["has_more"]!.Value<bool>()),
            ["retained_windows_complete"] = result.Rows.Count(r => r.Result?.Data?["complete"]?.Type == JTokenType.Boolean && r.Result.Data["complete"]!.Value<bool>())
        };
        return new CommandResult { Success = result.Coverage.Validated == result.Coverage.Requested, Message = "History fleet page collection finished; inspect per-profile progress and coverage.", Data = data };
    }

    private static int ParseHistoryInteger(string text, int min, int max) => TryBoundedInteger(text, min, max, out int value) ? value : throw new ArgumentException("Invalid history number.");
    private static bool HistoryProfileName(string value) => value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}
