using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using MTShared;
using MTShared.Network;
using MTShared.Types;
using MTTextClient.Core;
using MTTextClient.Import;
using System.Threading;
namespace MTTextClient.Commands;

/// <summary>
/// Import algorithms from V2 text format and manage algorithm templates.
///
/// import v2 <file-or-text>            — parse V2 format, show what would be created
/// import v2 <file-or-text> --confirm  — actually create and save to Core
/// import templates                     — list available algorithm templates
/// import add-numeric <id> <delta>      — add delta to all numeric params of an algo (--confirm)
///
/// FIX HISTORY:
///   - Group creation on import via SAVE_GROUP action.
///   - Group ID remapping — Core reassigns IDs on SAVE_GROUP, so algo groupIDs
///     must be remapped to the new server-assigned IDs before SAVE.
/// </summary>
public sealed class ImportCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public ImportCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public string Name => "import";
    public string Description => "Import algorithms from V2 format, manage templates";
    public string Usage => @"import v2 <file-path>|import templates|import add-numeric <algoId> <delta>";

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(Usage);
        }

        string? targetProfile = null;
        bool confirmFlag = false;
        var cleanArgs = new List<string>();
        foreach (string arg in args)
        {
            if (arg.StartsWith('@'))
            {
                targetProfile = arg[1..];
            }
            else if (arg.Equals("--confirm", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-y", StringComparison.OrdinalIgnoreCase))
            {
                confirmFlag = true;
            }
            else
            {
                cleanArgs.Add(arg);
            }
        }

        string? subCmd = cleanArgs[0].ToLowerInvariant();
        string[]? subArgs = cleanArgs.Count > 1 ? cleanArgs.GetRange(1, cleanArgs.Count - 1).ToArray() : Array.Empty<string>();

        return subCmd switch
        {
            "v2" => ImportV2(subArgs, targetProfile, confirmFlag),
            "templates" or "tpl" => ListTemplates(subArgs.Length > 0 ? subArgs[0] : null),
            "add-numeric" or "add-num" => AddNumericDelta(subArgs, targetProfile, confirmFlag),
            _ => CommandResult.Fail($"Unknown subcommand: {subCmd}. {Usage}")
        };
    }

    private CommandResult ImportV2(string[] args, string? targetProfile, bool confirmed)
    {
        if (args.Length < 1)
        {
            return CommandResult.Fail("Usage: import v2 <file-path> [--confirm] [@profile]\n  Also accepts inline V2 text if it contains ###START###");
        }

        string? input = string.Join(" ", args);
        string v2Text;

        // Check if it's a file path or inline text
        if (File.Exists(input))
        {
            v2Text = File.ReadAllText(input);
        }
        else if (input.Contains("###START###") || input.Contains("algorithmName="))
        {
            v2Text = input;
        }
        else if (File.Exists(args[0]))
        {
            v2Text = File.ReadAllText(args[0]);
        }
        else
        {
            return CommandResult.Fail($"File not found: {args[0]}");
        }

        // Per-call parser: V2FormatParser holds mutable template state
        // (SetTemplates below reseeds it from the connected core). A shared
        // instance would let two concurrent different-profile imports clobber
        // each other's templates, saving one venue's argument set onto another
        // (reintroducing #44). A local per invocation keeps that state confined
        // to this request, which the per-profile gate then fully serializes.
        var parser = new V2FormatParser(FindAlgoConfigs() ?? "");
        V2FormatParser.ParseResult parseResult = parser.Parse(v2Text);
        List<AlgorithmData>? algorithms = parseResult.Algorithms;
        List<string>? errors = parseResult.Errors;
        List<V2FormatParser.GroupInfo>? groups = parseResult.Groups;

        if (errors.Count > 0 && algorithms.Count == 0)
        {
            return CommandResult.Fail(
                $"Failed to parse V2 format:\n" +
                string.Join("\n", BuildPrefixedList(errors, "  ✗ ")));
        }

        if (algorithms.Count == 0)
        {
            return CommandResult.Fail("No algorithms found in V2 text.");
        }

        // Dry run — show what would be created
        if (!confirmed)
        {
            var preview = new List<object>(algorithms.Count);
            foreach (AlgorithmData a in algorithms)
            {
                preview.Add(new
                {
                    a.name,
                    a.signature,
                    GroupType = a.groupType.ToString(),
                    Market = a.marketType.ToString(),
                    a.symbol,
                    GroupId = a.groupID,
                    ArgsLen = a.argsJson?.Length ?? 0
                });
            }

            string? groupMsg = "";
            if (groups.Count > 0)
            {
                groupMsg = $"\nGroup(s) to create: {BuildGroupSummary(groups)}";
            }

            string? errorMsg = errors.Count > 0
                ? $"\n\nWarnings:\n{string.Join("\n", BuildPrefixedList(errors, "  ⚠ "))}"
                : "";

            return CommandResult.Ok(
                $"Parsed {algorithms.Count} algorithm(s) from V2 format. DRY RUN — nothing sent to Core.{groupMsg}{errorMsg}\n" +
                $"Re-run with --confirm to create on server:\n" +
                $"  import v2 <path> --confirm",
                preview);
        }

        // Confirmed — send to Core
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null || !conn.IsConnected)
        {
            return CommandResult.Fail("Not connected. Use 'connect <profile>' first.");
        }

        var results = new List<string>();
        int successCount = 0;

        // Re-seed the parser from the connected core's current default templates
        // (the isConfigList broadcast) and re-parse, so each algorithm's argument
        // set matches the connected core. A V2 file exported from an older core
        // otherwise carries a stale argument set the core rejects on start
        // (issue #44). Falls back to the initial bundled-template parse if the
        // core has not broadcast its config-list yet.
        if (conn.AlgoStore.ConfigTemplateCount == 0)
        {
            conn.ForceRefreshAlgos();
        }
        if (conn.AlgoStore.ConfigTemplateCount > 0)
        {
            parser.SetTemplates(conn.AlgoStore.ConfigTemplates);
            V2FormatParser.ParseResult liveParse = parser.Parse(v2Text);
            if (liveParse.Algorithms.Count > 0)
            {
                algorithms = liveParse.Algorithms;
                groups = liveParse.Groups;
                results.Add($"  Templated {algorithms.Count} algo(s) from the core's current-version defaults.");
            }
        }

        // Create groups FIRST, then remap algo groupIDs.
        // Core reassigns group IDs via GetNextID() in AddFolder(), so we must:
        //   1. Send SAVE_GROUP with old ID
        //   2. Wait for Core to broadcast new group data via AlgorithmListData
        //   3. Look up the new group by name in AlgoStore to find the server-assigned ID
        //   4. Remap all algo groupIDs from old → new before SAVE
        var groupIdMap = new Dictionary<long, long>(); // oldId → newId

        if (groups.Count > 0)
        {
            foreach (V2FormatParser.GroupInfo group in groups)
            {
                // Create the folder (ex-group). 0.7.25267: AlgorithmFolderAddRequestData
                // carries an AlgorithmGroupData; the core reassigns the id, which we
                // remap below by matching on name.
                var folder = new AlgorithmGroupData
                {
                    id = group.Id,
                    name = group.Name,
                    groupType = (AlgorithmGroupType)group.GroupType,
                };

                NotificationMessageData? groupNotification = conn.SendFolderAdd(folder);

                if (groupNotification == null)
                {
                    results.Add($"  Group '{group.Name}' ({group.Id}): sent (timed out)");
                }
                else if (groupNotification.IsOk)
                {
                    results.Add($"  Group '{group.Name}' ({group.Id}): CREATED ✓");
                }
                else
                {
                    results.Add($"  Group '{group.Name}' ({group.Id}): FAILED — {groupNotification.msgString}");
                    continue; // Skip ID lookup for failed groups
                }

                // Wait briefly for AlgorithmListData broadcast to arrive and update AlgoStore
                Thread.Sleep(300);

                // Find the new server-assigned group ID by matching name
                IReadOnlyList<AlgorithmGroupData> serverGroups = conn.AlgoStore.GetAllGroups();
                bool groupFound = false;
                foreach (AlgorithmGroupData g in serverGroups)
                {
                    if (g.name == group.Name && g.groupType == (AlgorithmGroupType)group.GroupType)
                    {
                        if (g.id != group.Id)
                        {
                            groupIdMap[group.Id] = g.id;
                            results.Add($"    → Remapped group ID: {group.Id} → {g.id}");
                        }
                        else
                        {
                            // ID wasn't changed (unlikely but possible)
                            groupIdMap[group.Id] = g.id;
                        }
                        groupFound = true;
                        break;
                    }
                }

                if (!groupFound)
                {
                    // Could not find the new group — algos will reference orphaned ID
                    results.Add($"    ⚠ Could not find server-assigned ID for group '{group.Name}'");
                }
            }
        }

        HashSet<long> beforeAlgoIds = conn.AlgoStore.GetAll().Select(a => a.id).ToHashSet();
        int queuedCount = 0;
        foreach (AlgorithmData algo in algorithms)
        {
            // Remap groupID if we have a mapping.
            // (No > 0 guard — TryGetValue is itself the filter; groupID=0 is a legal map key.)
            if (groupIdMap.TryGetValue(algo.groupID, out long newGroupId))
            {
                algo.groupID = newGroupId;
            }

            if (conn.TrySendAlgorithmSaveNoWait(algo))
            {
                queuedCount++;
                results.Add($"  {algo.name} ({algo.signature}): queued");
                if (queuedCount % 25 == 0)
                    Thread.Sleep(10);
            }
            else
            {
                results.Add($"  {algo.name} ({algo.signature}): FAILED — queue send failed");
            }
        }

        successCount = VerifyQueuedAlgorithmCreates(conn, beforeAlgoIds, queuedCount);
        results.Add($"  Verification: {successCount}/{queuedCount} queued algos observed on Core.");

        if (errors.Count > 0)
        {
            results.Add($"\nParse warnings: {string.Join(", ", errors)}");
        }

        return CommandResult.Ok(
            $"[{conn.Name}] Import results: {successCount}/{algorithms.Count} created.\n{string.Join("\n", results)}",
            new { Server = conn.Name, Total = algorithms.Count, Created = successCount });
    }

    private static int VerifyQueuedAlgorithmCreates(CoreConnection conn, HashSet<long> beforeAlgoIds, int queuedCount)
    {
        if (queuedCount <= 0) return 0;

        int timeoutMs = Math.Min(30_000, Math.Max(5_000, queuedCount * 250));
        var sw = Stopwatch.StartNew();
        int best = 0;
        int stablePasses = 0;

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            conn.ForceRefreshAlgos();
            int current = conn.AlgoStore.GetAll().Count(a => !beforeAlgoIds.Contains(a.id));
            if (current >= queuedCount)
                return current;

            if (current > best)
            {
                best = current;
                stablePasses = 0;
            }
            else if (current > 0)
            {
                stablePasses++;
                if (stablePasses >= 6)
                    return current;
            }

            Thread.Sleep(250);
        }

        conn.ForceRefreshAlgos();
        return Math.Max(best, conn.AlgoStore.GetAll().Count(a => !beforeAlgoIds.Contains(a.id)));
    }

    private CommandResult ListTemplates(string? explicitPath = null)
    {
        string? configPath;
        if (!string.IsNullOrEmpty(explicitPath))
        {
            if (!File.Exists(explicitPath))
            {
                return CommandResult.Fail($"algorithm template JSON not found at explicit path: {explicitPath}");
            }
            configPath = explicitPath;
        }
        else
        {
            configPath = FindAlgoConfigs();
            if (configPath == null || !File.Exists(configPath))
            {
                return CommandResult.Fail("Algorithm template JSON not found. Searched: " +
                    "(1) <app-dir>/algorithms.json, <app-dir>/algoConfigs.json, " +
                    "(2) ~/Documents/algorithms.json, ~/Documents/algoConfigs.json, " +
                    "(3) " + System.IO.Path.GetTempPath() + "algorithms.json, " +
                    System.IO.Path.GetTempPath() + "algoConfigs.json. " +
                    "Pass an explicit path via --path, or copy the file into one of those locations.");
            }
        }

        try
        {
            string? json = File.ReadAllText(configPath);
            Newtonsoft.Json.Linq.JObject? obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            Newtonsoft.Json.Linq.JArray? algos = obj["algorithms"] as Newtonsoft.Json.Linq.JArray;
            if (algos == null)
            {
                return CommandResult.Fail("Invalid algorithm template JSON format: missing top-level 'algorithms' array.");
            }

            var data = new List<object>(algos.Count);
            foreach (Newtonsoft.Json.Linq.JToken a in algos)
            {
                data.Add(new
                {
                    Name = (string?)a["name"] ?? "?",
                    Signature = (string?)a["signature"] ?? "?",
                    GroupType = ((AlgorithmGroupType)((int?)a["groupType"] ?? 0)).ToString(),
                    Trading = (bool?)a["isTradingAlgo"] ?? false ? "YES" : "no"
                });
            }

            return CommandResult.Ok($"Available algorithm templates ({data.Count}):", data);
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Failed to read templates: {ex.Message}");
        }
    }

    /// <summary>
    /// Add a numeric delta to ALL numeric parameters of an algorithm.
    /// Useful for batch-adjusting distances, percentages, etc.
    /// </summary>
    private CommandResult AddNumericDelta(string[] args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null || !conn.IsConnected)
        {
            return CommandResult.Fail("Not connected. Use 'connect <profile>' first.");
        }

        if (args.Length < 2 || !long.TryParse(args[0], out long algoId) ||
            !double.TryParse(args[1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double delta))
        {
            return CommandResult.Fail("Usage: import add-numeric <algoId> <delta> [--confirm]\n  Example: import add-numeric 123456 1.0 --confirm");
        }

        AlgorithmData? algo = conn.AlgoStore.FindById(algoId);
        if (algo == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Algorithm {algoId} not found.");
        }

        if (algo.isRunning)
        {
            return CommandResult.Fail($"[{conn.Name}] Algorithm {algoId} is RUNNING. Stop it first.");
        }

        AlgorithmConfig? config = AlgorithmStore.ParseConfig(algo);
        if (config == null)
        {
            return CommandResult.Fail($"[{conn.Name}] Algorithm {algoId}: no configuration data.");
        }

        // Find all float/int params and compute new values
        var changes = new List<(string Key, string Label, double OldVal, double NewVal)>();
        foreach (AlgorithmParameter param in config.Parameters)
        {
            if (param.ValueType is not ("float" or "int"))
            {
                continue;
            }

            if (param.ValueToken == null)
            {
                continue;
            }

            double oldVal;
            try
            {
                oldVal = param.ValueToken.ToObject<double>();
            }
            catch { continue; }

            double newVal = oldVal + delta;

            // Respect positive-only constraint
            if (param.UseOnlyPositiveValue && newVal < 0)
            {
                continue;
            }

            changes.Add((param.Key, param.Label, oldVal, newVal));
        }

        if (changes.Count == 0)
        {
            return CommandResult.Ok($"[{conn.Name}] Algorithm {algoId}: no numeric parameters to adjust.");
        }

        // Dry run
        if (!confirmed)
        {
            var preview = new List<object>(changes.Count);
            foreach ((string Key, string Label, double OldVal, double NewVal) c in changes)
            {
                preview.Add(new
                {
                    c.Key,
                    c.Label,
                    OldValue = c.OldVal.ToString("G"),
                    NewValue = c.NewVal.ToString("G"),
                    Delta = delta > 0 ? $"+{delta}" : delta.ToString()
                });
            }

            return CommandResult.Ok(
                $"[{conn.Name}] Algorithm {algoId} ({algo.name}): {changes.Count} numeric parameter(s) would be adjusted by {delta:+0.0;-0.0}.\n" +
                $"DRY RUN — Re-run with --confirm:\n" +
                $"  import add-numeric {algoId} {delta} --confirm",
                preview);
        }

        // Apply changes
        int applied = 0;
        foreach ((string key, string label, double oldVal, double newVal) in changes)
        {
            (bool success, string? error) = AlgorithmStore.UpdateParameter(algo, key, newVal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (success)
            {
                applied++;
            }
        }

        // Save to Core
        var request = new AlgorithmData(algo);
        NotificationMessageData? notification = conn.SendAlgorithmSave(request);

        if (notification == null)
        {
            return CommandResult.Ok($"[{conn.Name}] Algorithm {algoId}: {applied} params adjusted by {delta:+0.0;-0.0}, save sent (timed out).");
        }

        return notification.IsOk
            ? CommandResult.Ok(
                $"[{conn.Name}] Algorithm {algoId} ({algo.name}): {applied} params adjusted by {delta:+0.0;-0.0} and SAVED ✓",
                new { Server = conn.Name, AlgoId = algoId, AlgoName = algo.name, ParamsChanged = applied, Delta = delta })
            : CommandResult.Fail($"[{conn.Name}] Save FAILED after adjusting params — {notification.msgString}");
    }

    private static string? FindAlgoConfigs()
    {
        string[] candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "algorithms.json"),
            Path.Combine(AppContext.BaseDirectory, "algoConfigs.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "algorithms.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "algoConfigs.json"),
            Path.Combine(Path.GetTempPath(), "algorithms.json"),
            Path.Combine(Path.GetTempPath(), "algoConfigs.json")
        };

        foreach (string c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }
        return null;
    }

    private static List<string> BuildPrefixedList(List<string> items, string prefix)
    {
        var result = new List<string>(items.Count);
        foreach (string item in items)
        {
            result.Add($"{prefix}{item}");
        }

        return result;
    }

    private static string BuildGroupSummary(List<V2FormatParser.GroupInfo> groups)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < groups.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append($"'{groups[i].Name}' (type={groups[i].GroupType}, id={groups[i].Id})");
        }
        return sb.ToString();
    }

}
