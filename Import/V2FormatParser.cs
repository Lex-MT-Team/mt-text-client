using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MTShared;
using MTShared.Network;
using MTShared.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace MTTextClient.Import;

/// <summary>
/// Parses MoonTrader V2 text format into AlgorithmData objects ready to send to Core.
///
/// V2 Format:
///   VERSION: 2
///   ###GROUP_START###          (optional — group metadata)
///   name=0=My Group;
///   groupType=0=1;
///   id=0=1772621585588;
///   ###START###
///   algorithmName=0=Shot;
///   version=0=7;
///   groupId=0=0;
///   paramName=argumentType=value;
///   ###START###
///   ... (next algorithm)
///
/// Each line: paramName=argType=value
///   - argType is the integer ArgumentType
///   - value is either a primitive (string/number/bool) or a JSON object
///   - Strings are quoted: "btcusdt"
///   - JSON objects start with { and end with };
///
/// The parser converts V2 text → AlgorithmData by:
///   1. Extracting ###GROUP_START### metadata (if present) for group creation
///   2. Looking up the template argsJson from algoConfigs.json
///   3. Overriding each parameter value from the V2 text
///   4. Setting metadata fields (algorithmName, version, groupId, etc.)
///
/// FIX HISTORY:
///   - BUG-14: Windows \r\n line endings caused trailing ";\r" in multi-line JSON values.
///             Fix: normalize all \r\n to \n at the start of Parse().
///   - BUG-11: ###GROUP_START### blocks were silently skipped (lost group metadata).
///             Fix: extract group info from preamble before first ###START###.
///   - BUG-15: Group IDs from V2 files don't match Core-assigned IDs after SAVE_GROUP.
///             Fix: parse result now includes GroupInfos list so ImportCommand can remap IDs.
///   - Improved: multi-line JSON accumulation now uses Trim() on continuation lines.
/// </summary>
public sealed class V2FormatParser
{
    private readonly JObject _templatesByName; // algorithmName → full argsJson JObject

    /// <summary>
    /// Parsed group metadata from ###GROUP_START### section.
    /// Available after Parse() completes.
    /// </summary>
    public class GroupInfo
    {
        public string Name { get; set; } = "";
        public int GroupType { get; set; }
        public long Id { get; set; }
    }

    /// <summary>
    /// Parse result containing algorithms, errors, and group information.
    /// </summary>
    public class ParseResult
    {
        public List<AlgorithmData> Algorithms { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public List<GroupInfo> Groups { get; set; } = new();
    }

    public V2FormatParser(string algoConfigsPath)
    {
        _templatesByName = new JObject();
        if (File.Exists(algoConfigsPath))
        {
            try
            {
                string? json = File.ReadAllText(algoConfigsPath);
                JObject? listData = JObject.Parse(json);
                JArray? algorithms = listData["algorithms"] as JArray;
                if (algorithms != null)
                {
                    foreach (JToken algo in algorithms)
                    {
                        string? name = algo["name"]?.Value<string>();
                        if (name != null)
                        {
                            _templatesByName[name] = algo;
                        }
                    }
                }
            }
            catch { /* failed to load templates */ }
        }
    }

    /// <summary>
    /// Parse V2 text into a list of AlgorithmData objects.
    /// Each ###START### block becomes one AlgorithmData.
    /// Returns ParseResult with algorithms, errors, and group info.
    /// </summary>
    public ParseResult Parse(string v2Text)
    {
        var result = new ParseResult();

        // FIX BUG-14: Normalize Windows line endings before any processing.
        // V2 files from MTController often have \r\n which causes TrimEnd(';')
        // to fail when the line ends with ";\r".
        v2Text = v2Text.Replace("\r\n", "\n").Replace("\r", "\n");

        // FIX BUG-11: Extract ###GROUP_START### metadata before splitting by ###START###.
        // The group block sits between "VERSION: 2" and the first "###START###".
        GroupInfo? groupInfo = null;
        int groupStartIdx = v2Text.IndexOf("###GROUP_START###", StringComparison.OrdinalIgnoreCase);
        if (groupStartIdx >= 0)
        {
            string? afterGroupStart = v2Text[(groupStartIdx + "###GROUP_START###".Length)..];
            int nextStartIdx = afterGroupStart.IndexOf("###START###", StringComparison.OrdinalIgnoreCase);
            if (nextStartIdx > 0)
            {
                string? groupBlock = afterGroupStart[..nextStartIdx].Trim();
                groupInfo = ParseGroupBlock(groupBlock, result.Errors);
                if (groupInfo != null)
                {
                    result.Groups.Add(groupInfo);
                }
            }
        }

        // Split into blocks by ###START###
        string[]? blocks = v2Text.Split("###START###", StringSplitOptions.RemoveEmptyEntries);

        int blockIndex = 0;
        foreach (string block in blocks)
        {
            blockIndex++;
            string trimmed = block.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            // Skip preamble blocks (VERSION header + optional GROUP_START).
            // These contain "VERSION:" or "###GROUP_START###" but no "algorithmName=".
            if (trimmed.StartsWith("VERSION:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("###GROUP_START###", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                AlgorithmData? algo = ParseBlock(trimmed, blockIndex, result.Errors, groupInfo);
                if (algo != null)
                {
                    result.Algorithms.Add(algo);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Block {blockIndex}: parse error — {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Legacy compatibility: returns (Algorithms, Errors) tuple.
    /// Use Parse() returning ParseResult for full group info.
    /// </summary>
    public (List<AlgorithmData> Algorithms, List<string> Errors) ParseLegacy(string v2Text)
    {
        ParseResult result = Parse(v2Text);
        return (result.Algorithms, result.Errors);
    }

    /// <summary>
    /// Parse the ###GROUP_START### block to extract group metadata.
    /// Format:
    ///   name=0=3 WL SHOT;
    ///   groupType=0=1;
    ///   id=0=1772621585588;
    /// </summary>
    private static GroupInfo? ParseGroupBlock(string block, List<string> errors)
    {
        var info = new GroupInfo();
        bool hasName = false;

        string[]? lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim().TrimEnd(';');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            (string ParamName, int ArgType, string RawValue)? parsed = ParseLine(line);
            if (parsed == null)
            {
                continue;
            }

            (string paramName, int _, string rawValue) = parsed.Value;
            switch (paramName)
            {
                case "name":
                    info.Name = UnquoteString(rawValue);
                    hasName = true;
                    break;
                case "groupType":
                    int.TryParse(rawValue, out int gt);
                    info.GroupType = gt;
                    break;
                case "id":
                    long.TryParse(rawValue, out long gid);
                    info.Id = gid;
                    break;
            }
        }

        if (!hasName)
        {
            errors.Add("GROUP_START block: missing 'name' field.");
            return null;
        }

        return info;
    }

    private AlgorithmData? ParseBlock(string block, int blockIndex, List<string> errors, GroupInfo? groupInfo)
    {
        // Parse all key=argType=value lines
        var parameters = new Dictionary<string, (int ArgType, string RawValue)>();
        string? algorithmName = null;
        int version = 7;
        long groupId = 0;

        string[]? lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        int i = 0;
        while (i < lines.Length)
        {
            string? line = lines[i].Trim().TrimEnd(';');
            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            // Handle multi-line JSON values: accumulate lines until we have balanced braces
            string fullLine = line;
            if (ContainsOpenBrace(line))
            {
                while (!HasBalancedBraces(fullLine) && i + 1 < lines.Length)
                {
                    i++;
                    // FIX: Use Trim() to strip all whitespace including any stray \r,
                    // then TrimEnd(';') for trailing semicolons on JSON continuation lines.
                    fullLine += "\n" + lines[i].Trim().TrimEnd(';');
                }
            }
            // Final cleanup: strip any trailing semicolons from the accumulated value
            fullLine = fullLine.TrimEnd(';');

            // Parse: paramName=argType=value
            (string ParamName, int ArgType, string RawValue)? parsed = ParseLine(fullLine);
            if (parsed == null)
            {
                i++;
                continue;
            }

            (string paramName, int argType, string rawValue) = parsed.Value;

            // Extract metadata
            switch (paramName)
            {
                case "algorithmName":
                    algorithmName = UnquoteString(rawValue);
                    break;
                case "version":
                    int.TryParse(rawValue, out version);
                    break;
                case "groupId":
                    long.TryParse(rawValue, out groupId);
                    break;
                default:
                    parameters[paramName] = (argType, rawValue);
                    break;
            }

            i++;
        }

        if (algorithmName == null)
        {
            errors.Add($"Block {blockIndex}: missing algorithmName.");
            return null;
        }

        // Look up template
        JObject? template = _templatesByName[algorithmName] as JObject;
        if (template == null)
        {
            errors.Add($"Block {blockIndex}: no template found for algorithmName='{algorithmName}'.");
            return null;
        }

        // Deep-copy template argsJson and override with V2 values
        string? templateArgsJson = template["argsJson"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(templateArgsJson))
        {
            errors.Add($"Block {blockIndex}: template '{algorithmName}' has no argsJson.");
            return null;
        }

        JObject? argsObj = JObject.Parse(templateArgsJson);
        JObject? arguments = argsObj["Arguments"] as JObject;
        if (arguments == null)
        {
            errors.Add($"Block {blockIndex}: template '{algorithmName}' argsJson has no Arguments.");
            return null;
        }

        // Apply V2 overrides
        foreach ((string paramName, (int argType, string rawValue)) in parameters)
        {
            if (!arguments.TryGetValue(paramName, out JToken? argToken) || argToken is not JObject argObj)
            {
                // Parameter not in template — add it if it has a valid type
                if (argType > 0)
                {
                    var newArg = new JObject
                    {
                        ["name"] = paramName,
                        ["argumentType"] = argType,
                        ["value"] = ParseValue(argType, rawValue)
                    };
                    arguments[paramName] = newArg;
                }
                continue;
            }

            // Override the value
            argObj["value"] = ParseValue(argType, rawValue);
        }

        // Get groupType and signature from template
        int groupType = template["groupType"]?.Value<int>() ?? 0;
        string? signature = template["signature"]?.Value<string>() ?? "";
        bool isTradingAlgo = template["isTradingAlgo"]?.Value<bool>() ?? false;

        // Build AlgorithmData
        // Validate marketType — common mistake: using 2 (MARGIN) instead of 3 (FUTURES)
        if (parameters.TryGetValue("marketType", out (int ArgType, string RawValue) mtCheck))
        {
            if (int.TryParse(mtCheck.RawValue, out int mtCheckVal))
            {
                if (mtCheckVal == (int)MarketType.MARGIN)
                {
                    errors.Add($"Block {blockIndex}: WARNING — marketType=2 (MARGIN) is likely incorrect for trading algos. Did you mean 3 (FUTURES)? Core's BalanceLimitChecker does not support MARGIN.");
                }
                else if (mtCheckVal < 0 || mtCheckVal > 4)
                {
                    errors.Add($"Block {blockIndex}: marketType={mtCheckVal} is out of range. Valid: 0=UNKNOWN, 1=SPOT, 2=MARGIN, 3=FUTURES, 4=DELIVERY.");
                }
            }
        }

        // Use group info from ###GROUP_START### if the algo references the same groupId,
        // OR if the algo has groupId=0 and we have group info (auto-assign).
        // If groupInfo is available and algo's groupId matches (or is zero), use the group's id.
        if (groupInfo != null && (groupId == 0 || groupId == groupInfo.Id))
        {
            groupId = groupInfo.Id;
        }

        var algoData = new AlgorithmData
        {
            id = -1, // Signal new algorithm to Core
            version = version,
            name = algorithmName,
            signature = signature,
            description = "",
            groupID = groupId,
            groupType = (AlgorithmGroupType)groupType,
            isTradingAlgo = isTradingAlgo,
            isRunning = false,
            isProcessing = false,
            actionType = AlgorithmData.ActionType.SAVE,
            argsJson = argsObj.ToString(Formatting.None),
            marketType = MarketType.FUTURES, // Default; overridden below
            symbol = ""
        };

        // Extract marketType and symbol from parameters if present
        if (parameters.TryGetValue("marketType", out (int ArgType, string RawValue) mt))
        {
            if (int.TryParse(mt.RawValue, out int mtVal))
            {
                algoData.marketType = (MarketType)mtVal;
            }
        }
        if (parameters.TryGetValue("symbol", out (int ArgType, string RawValue) sym))
        {
            algoData.symbol = UnquoteString(sym.RawValue);
        }

        return algoData;
    }

    /// <summary>Parse a single V2 line: paramName=argType=value</summary>
    private static (string ParamName, int ArgType, string RawValue)? ParseLine(string line)
    {
        // Find first '=' 
        int firstEq = line.IndexOf('=');
        if (firstEq < 0)
        {
            return null;
        }

        string? paramName = line[..firstEq].Trim();

        // Find second '='
        int secondEq = line.IndexOf('=', firstEq + 1);
        if (secondEq < 0)
        {
            return null;
        }

        string? argTypeStr = line[(firstEq + 1)..secondEq].Trim();
        if (!int.TryParse(argTypeStr, out int argType))
        {
            return null;
        }

        string? rawValue = line[(secondEq + 1)..].Trim();
        return (paramName, argType, rawValue);
    }

    /// <summary>Parse a raw value string into a JToken based on argument type.</summary>
    private static JToken ParseValue(int argType, string rawValue)
    {
        // JSON object/array
        if (rawValue.StartsWith('{') || rawValue.StartsWith('['))
        {
            try { return JToken.Parse(rawValue); }
            catch { return JToken.FromObject(rawValue); }
        }

        // Quoted string
        if (rawValue.StartsWith('"') && rawValue.EndsWith('"'))
        {
            return JToken.FromObject(rawValue[1..^1]);
        }

        // Boolean
        if (rawValue.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return JToken.FromObject(true);
        }

        if (rawValue.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return JToken.FromObject(false);
        }

        // Numeric — try int first, then double
        if (int.TryParse(rawValue, out int intVal))
        {
            return JToken.FromObject(intVal);
        }

        if (double.TryParse(rawValue, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double dblVal))
        {
            return JToken.FromObject(dblVal);
        }

        // Fallback: string
        return JToken.FromObject(rawValue);
    }

    private static string UnquoteString(string s)
    {
        if (s.StartsWith('"') && s.EndsWith('"') && s.Length >= 2)
        {
            return s[1..^1];
        }

        return s;
    }

    private static bool ContainsOpenBrace(string line)
    {
        return line.Contains('{') && !HasBalancedBraces(line);
    }

    private static bool HasBalancedBraces(string text)
    {
        int depth = 0;
        foreach (char c in text)
        {
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
            }
        }
        return depth <= 0;
    }
}
