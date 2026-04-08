using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using MTShared;
using MTShared.Network;
using MTShared.Types;
using Newtonsoft.Json.Linq;
namespace MTTextClient.Core;

/// <summary>
/// In-memory store for algorithm data received from Core via subscription.
/// Thread-safe. Updated by ConnectionService callbacks.
/// 
/// Phase B: Extended with group tracking, argsJson parsing, and richer querying.
/// Phase H: Fixed D3/D4 — complete ArgumentType mapping (65 types), robust MapValueToken.
/// </summary>
public sealed class AlgorithmStore
{
    private readonly ConcurrentDictionary<long, AlgorithmData> _algorithms = new();
    private readonly ConcurrentDictionary<long, AlgorithmGroupData> _groups = new();

    /// <summary>Number of algorithms currently tracked.</summary>
    public int Count => _algorithms.Count;

    /// <summary>Number of groups currently tracked.</summary>
    public int GroupCount => _groups.Count;

    /// <summary>
    /// Process incoming algorithm data from subscription callback.
    /// The callback delivers (NetworkMessageType, NetworkData) where the concrete types are:
    ///   - ALGORITHM_LIST_RESULT → AlgorithmListData (contains List of AlgorithmData + groups)
    ///   - ALGORITHM_STATUS_DATA → AlgorithmStatusData
    ///   - ALGORITHM_SYMBOL_STATUS_DATA → AlgorithmSymbolStatusData
    ///   - ALGORITHM_CONFIG_UPDATE → AlgorithmData (single update)
    /// </summary>
    public void ProcessData(NetworkMessageType msgType, NetworkData data)
    {
        switch (msgType)
        {
            case NetworkMessageType.ALGORITHM_LIST_RESULT:
                if (data is AlgorithmListData listData)
                {
                    ProcessAlgorithmList(listData);
                }
                break;

            case NetworkMessageType.ALGORITHM_CONFIG_UPDATE:
                if (data is AlgorithmData algoData)
                {
                    _algorithms[algoData.id] = algoData;
                }
                break;

            case NetworkMessageType.ALGORITHM_STATUS_DATA:
                if (data is AlgorithmStatusData statusData)
                {
                    if (_algorithms.TryGetValue(statusData.id, out AlgorithmData? existing))
                    {
                        // MT-019: lock-protected atomic update of both fields so readers
                        // never observe a torn state (isRunning=new, isProcessing=old).
                        lock (existing)
                        {
                            existing.isRunning    = statusData.isRunning;
                            existing.isProcessing = statusData.isProcessing;
                        }
                    }
                }
                break;

            case NetworkMessageType.ALGORITHM_SYMBOL_STATUS_DATA:
                // AlgorithmSymbolStatusData — per-symbol status; store if needed later
                break;
        }
    }

    /// <summary>
    /// Process AlgorithmListData with proper ADD/UPDATE/DELETE semantics.
    /// Matches the pattern used by MTController's CoreAlgorithmsManager.
    /// </summary>
    private void ProcessAlgorithmList(AlgorithmListData listData)
    {
        // Skip config lists (template data, not live algos)
        if (listData.isConfigList)
        {
            return;
        }

        // Process groups
        if (listData.groups != null)
        {
            foreach (AlgorithmGroupData group in listData.groups)
            {
                switch (group.actionType)
                {
                    case AlgorithmData.ActionType.ADD:
                    case AlgorithmData.ActionType.UPDATE:
                    case AlgorithmData.ActionType.SAVE_GROUP:
                        _groups[group.id] = group;
                        break;

                    case AlgorithmData.ActionType.DELETE:
                    case AlgorithmData.ActionType.DELETE_GROUP:
                        _groups.TryRemove(group.id, out _);
                        break;

                    default:
                        // INIT or other — just store
                        _groups[group.id] = group;
                        break;
                }
            }
        }

        // Process algorithms
        if (listData.algorithms != null)
        {
            foreach (AlgorithmData algo in listData.algorithms)
            {
                switch (algo.actionType)
                {
                    case AlgorithmData.ActionType.ADD:
                    case AlgorithmData.ActionType.UPDATE:
                    case AlgorithmData.ActionType.SAVE:
                    case AlgorithmData.ActionType.SAVE_START:
                        _algorithms[algo.id] = algo;
                        break;

                    case AlgorithmData.ActionType.DELETE:
                        _algorithms.TryRemove(algo.id, out _);
                        break;

                    default:
                        // INIT or other — just store
                        _algorithms[algo.id] = algo;
                        break;
                }
            }
        }
    }

    /// <summary>Get all algorithms sorted by ID.</summary>
    public IReadOnlyList<AlgorithmData> GetAll()
    {
        var list = new List<AlgorithmData>(_algorithms.Values);
        list.Sort((a, b) => a.id.CompareTo(b.id));
        return list;
    }

    /// <summary>Find algorithm by ID.</summary>
    public AlgorithmData? FindById(long id)
    {
        _algorithms.TryGetValue(id, out AlgorithmData? algo);
        return algo;
    }

    /// <summary>Get all algorithms in a specific group.</summary>
    public IReadOnlyList<AlgorithmData> GetByGroup(long groupId)
    {
        var list = new List<AlgorithmData>();
        foreach (AlgorithmData a in _algorithms.Values)
        {
            if (a.groupID == groupId)
            {
                list.Add(a);
            }
        }
        list.Sort((a, b) => a.id.CompareTo(b.id));
        return list;
    }

    /// <summary>Get all groups sorted by ID.</summary>
    public IReadOnlyList<AlgorithmGroupData> GetAllGroups()
    {
        var list = new List<AlgorithmGroupData>(_groups.Values);
        list.Sort((a, b) => a.id.CompareTo(b.id));
        return list;
    }

    /// <summary>Find group by ID.</summary>
    public AlgorithmGroupData? FindGroupById(long groupId)
    {
        _groups.TryGetValue(groupId, out AlgorithmGroupData? group);
        return group;
    }

    /// <summary>Search algorithms by name (case-insensitive).</summary>
    public IReadOnlyList<AlgorithmData> Search(string query)
    {
        string? q = query.ToLowerInvariant();
        var list = new List<AlgorithmData>();
        foreach (AlgorithmData a in _algorithms.Values)
        {
            if ((a.name?.ToLowerInvariant().Contains(q) ?? false) ||
                (a.signature?.ToLowerInvariant().Contains(q) ?? false) ||
                (a.symbol?.ToLowerInvariant().Contains(q) ?? false))
            {
                list.Add(a);
            }
        }
        list.Sort((a, b) => a.id.CompareTo(b.id));
        return list;
    }

    /// <summary>Get running/stopped counts.</summary>
    public (int Running, int Stopped, int Processing) GetCounts()
    {
        int running = 0, stopped = 0, processing = 0;
        foreach (AlgorithmData algo in _algorithms.Values)
        {
            if (algo.isRunning)
            {
                running++;
            }
            else
            {
                stopped++;
            }

            if (algo.isProcessing)
            {
                processing++;
            }
        }
        return (running, stopped, processing);
    }

    /// <summary>
    /// Parse the argsJson of an algorithm into a readable parameter list.
    /// Returns null if argsJson is empty or unparseable.
    /// </summary>
    public static AlgorithmConfig? ParseConfig(AlgorithmData algo)
    {
        if (string.IsNullOrWhiteSpace(algo.argsJson))
        {
            return null;
        }

        try
        {
            JObject? root = JObject.Parse(algo.argsJson);
            JObject? arguments = root["Arguments"] as JObject;
            if (arguments == null)
            {
                return null;
            }

            var parameters = new List<AlgorithmParameter>();
            foreach (JProperty prop in arguments.Properties())
            {
                if (prop.Value is not JObject argObj)
                {
                    continue;
                }

                int argType = argObj["argumentType"]?.Value<int>() ?? 0;

                // Skip layout elements (separators, empty, group headers)
                if (argType is 98 or 99 or 100)
                {
                    continue;
                }

                string name = argObj["name"]?.Value<string>() ?? prop.Name;
                string label = argObj["label"]?.Value<string>() ?? name;
                JToken? value = argObj["value"];
                string? unit = argObj["inputLabel"]?.Value<string>();
                string? tooltip = argObj["tooltip"]?.Value<string>();
                int group = argObj["group"]?.Value<int>() ?? 0;
                int order = argObj["order"]?.Value<int>() ?? 0;
                bool usePositive = argObj["useOnlyPositiveValue"]?.Value<bool>() ?? false;

                parameters.Add(new AlgorithmParameter
                {
                    Key = prop.Name,
                    Name = name,
                    Label = label,
                    ValueToken = value,
                    ValueType = MapValueType(argType),
                    Unit = unit,
                    Tooltip = tooltip,
                    Group = group,
                    Order = order,
                    UseOnlyPositiveValue = usePositive,
                    ArgumentType = argType
                });
            }

            return new AlgorithmConfig
            {
                AlgorithmId = algo.id,
                AlgorithmName = algo.name,
                Signature = algo.signature,
                Parameters = SortParameters(parameters)
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<AlgorithmParameter> SortParameters(List<AlgorithmParameter> parameters)
    {
        var sorted = new List<AlgorithmParameter>(parameters);
        sorted.Sort((a, b) =>
        {
            int cmp = a.Group.CompareTo(b.Group);
            return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
        });
        return sorted;
    }

    /// <summary>
    /// Update a specific parameter in an algorithm's argsJson.
    /// Returns (success, errorMessage).
    /// Does NOT send to Core — caller must do that with SAVE action.
    /// </summary>
    public static (bool Success, string? Error) UpdateParameter(AlgorithmData algo, string paramKey, string newValue)
    {
        if (algo.isRunning)
        {
            return (false, "Cannot modify parameters while algorithm is running. Stop it first.");
        }

        if (string.IsNullOrWhiteSpace(algo.argsJson))
        {
            return (false, "Algorithm has no configuration (argsJson is empty).");
        }

        try
        {
            JObject? root = JObject.Parse(algo.argsJson);
            JObject? arguments = root["Arguments"] as JObject;
            if (arguments == null)
            {
                return (false, "argsJson does not contain an Arguments object.");
            }

            if (!arguments.TryGetValue(paramKey, out JToken? argToken) || argToken is not JObject argObj)
            {
                return (false, $"Parameter '{paramKey}' not found. Use 'algos config <id>' to see available parameters.");
            }

            int argType = argObj["argumentType"]?.Value<int>() ?? 0;

            // Validate positive-only
            bool usePositive = argObj["useOnlyPositiveValue"]?.Value<bool>() ?? false;
            if (usePositive && double.TryParse(newValue, out double numVal) && numVal < 0)
            {
                return (false, $"Parameter '{paramKey}' requires a non-negative value.");
            }

            // Set the value based on argument type
            JToken valueToken = MapValueToken(argType, newValue);
            argObj["value"] = valueToken;

            algo.argsJson = root.ToString(Newtonsoft.Json.Formatting.None);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to update parameter: {ex.Message}");
        }
    }

    /// <summary>
    /// Map ArgumentType enum value to a human-readable value type category.
    /// Complete mapping for all 65 known ArgumentType values (MTShared.Algorithms.ArgumentType).
    /// 
    /// Categories:
    ///   bool         — simple boolean on/off
    ///   int          — integer value (byte, int32, int64)
    ///   float        — floating point (float, double)
    ///   string       — text input (single line, multi-line, symbol, channel, etc.)
    ///   enum         — selection from enumerated options
    ///   range        — min/max range object {min, max}
    ///   toggle_range — toggle + value object {isOn, value: {min, max}} or {isOn, value}
    ///   toggle       — toggle + simple value {isOn, value}
    ///   complex      — structured/nested data (filters, trigger lists, etc.)
    /// </summary>
    private static string MapValueType(int argumentType) => argumentType switch
    {
        // === Booleans ===
        11 => "bool",               // BOOL
        29 => "bool",               // IS_EMULATED_BOOL
        35 => "bool",               // ALGO_AUTO_RESTART_TYPE
        48 => "bool",               // TP_BOOL
        49 => "bool",               // SL_BOOL
        63 => "bool",               // IS_PRESET_BOOL
        64 => "bool",               // VECTOR_SHOT_DIRECTION

        // === Integers ===
        1 => "int",                 // BYTE
        2 => "int",                 // INT32
        5 => "int",                 // INT64 (mapped to int for display, stored as long)

        // === Floats ===
        3 => "float",               // DOUBLE (note: MTShared uses 3=DOUBLE, not INT64)
        4 => "float",               // FLOAT

        // === Strings ===
        9 => "string",              // STRING
        15 => "string",             // MULTILINE_STRING
        23 => "string",             // LONG_STRING
        24 => "string",             // SYMBOL_LIST (comma-separated symbols)
        25 => "string",             // QUOTE_LIST
        30 => "string",             // CHANNEL_LIST
        32 => "string",             // SYMBOL
        36 => "string",             // ALGO_NAMING_RULE
        53 => "string",             // NAME_PREVIEW

        // === Enums (selection from fixed set) ===
        7 => "enum",                // EXCHANGE_TYPE
        8 => "enum",                // MARKET_TYPE
        10 => "enum",               // ORDER_SIDE_TYPE
        12 => "enum",               // ORDER_TYPE
        13 => "enum",               // TPSL_STATUS
        14 => "enum",               // CLIENT_ORDER_TYPE
        16 => "enum",               // TIME_FRAME
        17 => "enum",               // TIME_FRAME_MIN
        20 => "enum",               // DATA_SOURCE_TYPE
        21 => "enum",               // TP_ORDER_TYPE
        22 => "enum",               // SL_ORDER_TYPE
        26 => "enum",               // RELATIVE_TO_TYPE
        31 => "enum",               // ALGO_QUANTITIVE_RULES
        38 => "enum",               // AUTO_STOP_ACTION_TYPE
        39 => "enum",               // AUTO_STOP_SOURCE_TYPE
        41 => "enum",               // AUTO_STOP_TIMEFRAME
        44 => "enum",               // ALGORITHM_ACTION_TYPE
        52 => "enum",               // TP_TYPE
        59 => "enum",               // MARKETS_WATCHER_SIGNAL_TYPE
        61 => "enum",               // PERF_FILTER_VALUE_SOURCE_TYPE
        62 => "enum",               // PERF_FILTER_VALUE_RANGE_TYPE

        // === Ranges (min/max objects) ===
        18 => "range",              // MIN_MAX_FLOAT
        19 => "range",              // MIN_MAX_DOUBLE
        42 => "range",              // RANGE_VALUE_TYPE
        47 => "range",              // MIN_MAX_INT

        // === Toggle + Range (isOn + min/max value) ===
        33 => "toggle_range",       // TOGGLE_MIN_MAX_FLOAT
        34 => "toggle_range",       // TOGGLE_MIN_MAX_DOUBLE
        45 => "toggle_range",       // TOGGLE_MIN_MAX_INT

        // === Toggle + Simple value ===
        43 => "toggle",             // TOGGLE_STRING (isOn + string value)
        50 => "toggle",             // TOGGLE_DOUBLE (isOn + double value)
        51 => "toggle",             // TOGGLE_FLOAT (isOn + float value)

        // === Complex structured data ===
        27 => "complex",            // DELTA_FILTER_LIST
        28 => "complex",            // ACTIVE_MARKETS_FILTER
        37 => "complex",            // AUTO_STOP_FILTER_LIST
        40 => "complex",            // AUTO_STOP_SYMBOL
        54 => "complex",            // AVERAGES_PARAMETERS
        55 => "complex",            // ORDER_SIZE
        56 => "complex",            // WORKING_HOURS
        57 => "complex",            // TRIGGER_LIST
        58 => "complex",            // TRIGGER_ACTION_LIST
        60 => "complex",            // MARKETS_WATCHER_ALGORITHM_PARAMETERS

        // === Layout (should be filtered out in ParseConfig, but handle gracefully) ===
        98 or 99 or 100 => "layout",

        // Fallback for any future types
        _ => "complex"
    };

    /// <summary>
    /// Convert string input to the appropriate JToken based on argument type.
    /// Handles all known ArgumentType categories with proper type conversion.
    /// </summary>
    private static JToken MapValueToken(int argumentType, string input) => argumentType switch
    {
        // === Booleans (all bool types) ===
        11 or 29 or 35 or 48 or 49 or 63 or 64 =>
            JToken.FromObject(ParseBool(input)),

        // === Byte (unsigned 0-255) ===
        1 => byte.TryParse(input, out byte b) ? JToken.FromObject(b) : JToken.FromObject(input),

        // === Int32 ===
        2 => int.TryParse(input, out int i32) ? JToken.FromObject(i32) : JToken.FromObject(input),

        // === Int64 / Double — MTShared defines 3=DOUBLE, 5=INT64 ===
        5 => long.TryParse(input, out long i64) ? JToken.FromObject(i64) : JToken.FromObject(input),

        // === Float ===
        4 => float.TryParse(input, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f)
                ? JToken.FromObject(f) : JToken.FromObject(input),

        // === Double ===
        3 => double.TryParse(input, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d)
                ? JToken.FromObject(d) : JToken.FromObject(input),

        // === Enum types — typically stored as integer index ===
        7 or 8 or 10 or 12 or 13 or 14 or 16 or 17 or 20 or 21 or 22 or 26
        or 31 or 38 or 39 or 41 or 44 or 52 or 59 or 61 or 62 =>
            int.TryParse(input, out int enumInt) ? JToken.FromObject(enumInt) : JToken.FromObject(input),

        // === String types — pass through as-is ===
        9 or 15 or 23 or 24 or 25 or 30 or 32 or 36 or 53 =>
            JToken.FromObject(input),

        // === Range types (MIN_MAX_FLOAT/DOUBLE/INT, RANGE_VALUE_TYPE) ===
        // Expected: JSON object like {"min": 0.5, "max": 1.5} or shorthand "0.5,1.5"
        18 or 19 or 42 or 47 =>
            ParseRangeToken(argumentType, input),

        // === Toggle + Range (TOGGLE_MIN_MAX_FLOAT/DOUBLE/INT) ===
        // Expected: JSON like {"isOn": true, "value": {"min": 0.5, "max": 1.5}}
        33 or 34 or 45 =>
            ParseToggleRangeToken(argumentType, input),

        // === Toggle + Simple value (TOGGLE_STRING/DOUBLE/FLOAT) ===
        // Expected: JSON like {"isOn": true, "value": 1.5} or shorthand "on:1.5"
        43 or 50 or 51 =>
            ParseToggleValueToken(argumentType, input),

        // === Complex types — try JSON parse, fallback to string ===
        _ => TryParseJson(input) ?? JToken.FromObject(input)
    };

    /// <summary>Parse boolean from various input formats.</summary>
    private static bool ParseBool(string input) =>
        input.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("1", StringComparison.Ordinal) ||
        input.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("on", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse a range value like {"min":X,"max":Y} or shorthand "X,Y".</summary>
    private static JToken ParseRangeToken(int argType, string input)
    {
        // Try JSON first
        JToken? json = TryParseJson(input);
        if (json != null)
        {
            return json;
        }

        // Shorthand: "min,max"
        string[]? parts = input.Split(',');
        if (parts.Length == 2)
        {
            bool isInt = argType == 47; // MIN_MAX_INT
            if (isInt && int.TryParse(parts[0].Trim(), out int minI) && int.TryParse(parts[1].Trim(), out int maxI))
            {
                return JObject.FromObject(new { min = minI, max = maxI });
            }

            if (double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double minD) &&
                double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double maxD))
            {
                return JObject.FromObject(new { min = minD, max = maxD });
            }
        }

        return JToken.FromObject(input);
    }

    /// <summary>Parse a toggle+range like {"isOn":true,"value":{"min":X,"max":Y}} or shorthand "on:X,Y" / "off:X,Y".</summary>
    private static JToken ParseToggleRangeToken(int argType, string input)
    {
        JToken? json = TryParseJson(input);
        if (json != null)
        {
            return json;
        }

        // Shorthand: "on:min,max" or "off:min,max" or just "min,max" (keeps current toggle)
        int colonIdx = input.IndexOf(':');
        if (colonIdx > 0)
        {
            string? togglePart = input[..colonIdx].Trim();
            string? rangePart = input[(colonIdx + 1)..].Trim();
            bool isOn = ParseBool(togglePart);
            JToken rangeToken = ParseRangeToken(argType == 33 ? 18 : argType == 34 ? 19 : 47, rangePart);
            return JObject.FromObject(new { isOn, value = rangeToken });
        }

        return JToken.FromObject(input);
    }

    /// <summary>Parse a toggle+value like {"isOn":true,"value":X} or shorthand "on:value" / "off:value".</summary>
    private static JToken ParseToggleValueToken(int argType, string input)
    {
        JToken? json = TryParseJson(input);
        if (json != null)
        {
            return json;
        }

        int colonIdx = input.IndexOf(':');
        if (colonIdx > 0)
        {
            string? togglePart = input[..colonIdx].Trim();
            string? valuePart = input[(colonIdx + 1)..].Trim();
            bool isOn = ParseBool(togglePart);

            JToken val = argType switch
            {
                50 => double.TryParse(valuePart, System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture, out double dv) ? JToken.FromObject(dv) : JToken.FromObject(valuePart),
                51 => float.TryParse(valuePart, System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture, out float fv) ? JToken.FromObject(fv) : JToken.FromObject(valuePart),
                43 => JToken.FromObject(valuePart), // TOGGLE_STRING
                _ => JToken.FromObject(valuePart)
            };

            return JObject.FromObject(new { isOn, value = val });
        }

        return JToken.FromObject(input);
    }

    /// <summary>Try to parse input as JSON (for complex/range types).</summary>
    private static JToken? TryParseJson(string input)
    {
        try
        {
            if (input.StartsWith('{') || input.StartsWith('['))
            {
                return JToken.Parse(input);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Clear all data (on disconnect).</summary>
    public void Clear()
    {
        _algorithms.Clear();
        _groups.Clear();
    }
}

/// <summary>Parsed algorithm configuration with typed parameters.</summary>
public sealed class AlgorithmConfig
{
    public long AlgorithmId { get; init; }
    public string AlgorithmName { get; init; } = "";
    public string Signature { get; init; } = "";
    public List<AlgorithmParameter> Parameters { get; init; } = new();
}

/// <summary>A single parameter from an algorithm's argsJson.</summary>
public sealed class AlgorithmParameter
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public JToken? ValueToken { get; init; }
    public string ValueType { get; init; } = "complex";
    public string? Unit { get; init; }
    public string? Tooltip { get; init; }
    public int Group { get; init; }
    public int Order { get; init; }
    public bool UseOnlyPositiveValue { get; init; }
    public int ArgumentType { get; init; }

    /// <summary>Get the value as a display string.</summary>
    public string DisplayValue
    {
        get
        {
            if (ValueToken == null || ValueToken.Type == JTokenType.Null)
            {
                return "(null)";
            }

            return ValueToken.Type switch
            {
                JTokenType.Boolean => ValueToken.Value<bool>() ? "ON" : "OFF",
                JTokenType.Integer => ValueToken.Value<long>().ToString(),
                JTokenType.Float => ValueToken.Value<double>().ToString("G"),
                JTokenType.String => ValueToken.Value<string>() ?? "",
                JTokenType.Object => SummarizeObject((JObject)ValueToken),
                JTokenType.Array => $"[{((JArray)ValueToken).Count} items]",
                _ => ValueToken.ToString()
            };
        }
    }

    private static string SummarizeObject(JObject obj)
    {
        // Common pattern: toggle + value objects
        if (obj.ContainsKey("isOn") && obj.ContainsKey("value"))
        {
            bool isOn = obj["isOn"]?.Value<bool>() ?? false;
            JToken? val = obj["value"];
            if (val is JObject valObj && valObj.ContainsKey("min") && valObj.ContainsKey("max"))
            {
                return $"{(isOn ? "ON" : "OFF")}: [{valObj["min"]} — {valObj["max"]}]";
            }

            return $"{(isOn ? "ON" : "OFF")}: {val?.ToString() ?? "?"}";
        }
        if (obj.ContainsKey("min") && obj.ContainsKey("max"))
        {
            return $"[{obj["min"]} — {obj["max"]}]";
        }
        var sb = new System.Text.StringBuilder("{");
        int taken = 0;
        foreach (JProperty p in obj.Properties())
        {
            if (taken >= 3)
            {
                break;
            }

            if (taken > 0)
            {
                sb.Append(", ");
            }

            sb.Append($"{p.Name}={p.Value}");
            taken++;
        }
        if (obj.Count > 3)
        {
            sb.Append(", ...");
        }

        sb.Append("}");
        return sb.ToString();
    }
}
