using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using MTTextClient.Commands;
using MTTextClient.Core;
using MTTextClient.Output;
using MTShared;
using MTShared.Network;
using MTShared.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace MTTextClient.MCP;

/// <summary>
/// MCP (Model Context Protocol) server for MTTextClient.
/// Communicates over stdio using JSON-RPC 2.0 messages.
///
/// Maps every REPL command to an MCP tool, providing automation clients
/// with full access to all MT-Core operations.
///
/// Protocol:
///   - Reads JSON-RPC requests from stdin (one per line)
///   - Writes JSON-RPC responses to stdout
///   - Uses stderr for logging (doesn't interfere with protocol)
///
/// Tools exposed:
///   mt_connect, mt_disconnect, mt_status, mt_use,
///   mt_algos, mt_account, mt_core_status, mt_exchange,
///   mt_settings, mt_import, mt_orders, mt_output,
///   mt_fleet (fleet-wide operations),
///   mt_monitor (real-time core monitoring via UDP)
/// </summary>
public sealed class McpServer
{
    private readonly ConnectionManager _manager;
    private readonly OutputManager _output;
    private readonly CommandRegistry _registry;
    private TextWriter _stdoutWriter = Console.Out;

    // Event streaming
    private readonly EventBroadcaster _events = new();
    private SseEventServer? _sseServer;

    // Prometheus metrics
    private readonly MetricsCollector _metrics = new();

    // Rate limit tracker
    private readonly RateLimitTracker _rateLimits = new();

    /// <summary>MCP protocol version.</summary>
    private const string PROTOCOL_VERSION = "2024-11-05";
    private const string SERVER_NAME = "mt-text-client";
    private const string SERVER_VERSION = "0.8.0";

    public McpServer()
    {
        _manager = new ConnectionManager();
        _output = new OutputManager { Mode = OutputMode.Json }; // MCP always returns JSON
        _registry = new CommandRegistry();

        InitializeCommands();
        WireEvents();
    }

    private void InitializeCommands()
    {
        // Connection management
        _registry.Register(new ConnectCommand(_manager));
        _registry.Register(new DisconnectCommand(_manager));
        _registry.Register(new UseCommand(_manager));
        _registry.Register(new StatusCommand(_manager));

        // Account data
        _registry.Register(new AccountCommand(_manager));
        _registry.Register(new CoreStatusCommand(_manager));
        _registry.Register(new ExchangeCommand(_manager));

        // Algorithm management
        _registry.Register(new AlgosCommand(_manager));

        // Server profile settings
        _registry.Register(new SettingsCommand(_manager));

        // Import
        _registry.Register(new ImportCommand(_manager));

        // Orders
        _registry.Register(new OrdersCommand(_manager));

        // Trade Reports
        _registry.Register(new ReportsCommand(_manager, new ReportStore()));

        // Fleet-wide operations
        _registry.Register(new FleetCommand(_manager));
        _registry.Register(new TagCommand(_manager));

        // Monitor — real-time core monitoring via UDP
        _registry.Register(new MonitorCommand(_manager));

        // Configuration
        _registry.Register(new ProfileCommand());
        _registry.Register(new OutputCommand(_output));

        // ── Feature command parity with REPL ──
        // (These were registered in Program.cs but missing here, causing
        //  MCP tools to fail with "Unknown command: '<verb>'" at dispatch.)
        _registry.Register(new AutoStopsCommand(_manager));
        _registry.Register(new BlacklistCommand(_manager));
        _registry.Register(new WhitelistCommand(_manager));
        _registry.Register(new ProfilesCommand());
        _registry.Register(new FoldersCommand());
        _registry.Register(new TPSLCommand(_manager));
        _registry.Register(new PerformanceCommand(_manager));
        _registry.Register(new NotificationsCommand(_manager));
        _registry.Register(new MarketDataCommand(_manager));
        _registry.Register(new AlertsCommand(_manager));
        _registry.Register(new ProfilingCommand(_manager));
        _registry.Register(new TriggersCommand(_manager));
        _registry.Register(new LiveMarketsCommand(_manager));
        _registry.Register(new AutoBuyCommand(_manager));
        _registry.Register(new GraphToolCommand(_manager));
        _registry.Register(new SignalsCommand(_manager));
        _registry.Register(new DustCommand(_manager));
        _registry.Register(new DepositCommand(_manager));
        _registry.Register(new FundingCommand(_manager));
        _registry.Register(new BuyApiLimitCommand(_manager));
        _registry.Register(new HelpCommand(_registry));
    }

    private void WireEvents()
    {
        _manager.OnConnectionEstablished += conn =>
        {
            LogStderr($"[CONNECTED] {conn.Name}");
            _events.Publish("connection_established", conn.Name);
        };
        _manager.OnConnectionLost += conn =>
        {
            LogStderr($"[DISCONNECTED] {conn.Name}");
            _events.Publish("connection_lost", conn.Name);
        };
        _manager.OnConnectionError += (conn, msg) =>
            _events.Publish("connection_error", conn.Name, new { message = msg });
        _manager.OnAlgorithmsLoaded += (conn, count) =>
        {
            LogStderr($"[SYNC] {conn.Name}: {count} algorithm(s)");
            _events.Publish("algorithms_synced", conn.Name, new { count });
        };
        _manager.OnCoreStatusReceived += conn =>
            _events.Publish("core_status_received", conn.Name);
        _manager.OnAccountDataReceived += conn =>
            _events.Publish("account_data_received", conn.Name);
    }

    /// <summary>Run the MCP server loop over stdio.</summary>
    public void Run()
    {
        // Redirect Console.Out -> stderr so LiteNetLib log noise
        // does not corrupt the JSON-RPC stdio channel.
        _stdoutWriter = Console.Out;
        Console.SetOut(Console.Error);
        LogStderr($"MCP Server {SERVER_VERSION} starting on stdio...");

        // start SSE event server
        _sseServer = new SseEventServer(_events, _metrics);
        _sseServer.Start();

        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

        while (true)
        {
            string? line = reader.ReadLine();
            if (line == null)
            {
                break; // EOF
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                JObject? request = JObject.Parse(line);
                JObject? response = HandleRequest(request);
                if (response != null)
                {
                    WriteStdout(response);
                }
            }
            catch (Exception ex)
            {
                // Recover the request id from the raw line so the
                // JSON-RPC error envelope echoes the caller's id instead of
                // null. A null id breaks request/response correlation in
                // compliant clients.
                JToken? recoveredId = null;
                try { recoveredId = JObject.Parse(line)["id"]; } catch { /* truly malformed */ }
                LogStderr($"Error processing request: {ex.Message}");
                WriteStdout(MakeErrorResponse(recoveredId, -32700, $"Parse error: {ex.Message}"));
            }
        }

        _manager.Dispose();
        LogStderr("MCP Server shutting down.");
    }

    private JObject? HandleRequest(JObject request)
    {
        string? method = request["method"]?.Value<string>();
        JToken? id = request["id"];

        // JSON-RPC notifications have no "id" — never respond to them
        bool isNotification = id == null || id.Type == JTokenType.Null;
        if (isNotification && method != "initialize")
        {
            return null;
        }

        return method switch
        {
            "initialize" => HandleInitialize(id),
            "notifications/initialized" or "initialized" => null,
            "tools/list" => HandleToolsList(id),
            "tools/call" => HandleToolCall(request, id),
            "ping" => MakeResult(id, new JObject { ["pong"] = true }),
            _ => id != null && id.Type != JTokenType.Null
                ? MakeErrorResponse(id, -32601, $"Method not found: {method}")
                : null // Don't respond to unknown notifications
        };
    }

    private JObject HandleInitialize(JToken? id)
    {
        var result = new JObject
        {
            ["protocolVersion"] = PROTOCOL_VERSION,
            ["capabilities"] = new JObject
            {
                ["tools"] = new JObject { }
            },
            ["serverInfo"] = new JObject
            {
                ["name"] = SERVER_NAME,
                ["version"] = SERVER_VERSION
            }
        };
        return MakeResult(id, result);
    }

    private JObject HandleToolsList(JToken? id)
    {
        var tools = new JArray();
        foreach (JObject tool in ToolRegistry.AllTools())
        {
            tools.Add(tool);
        }
        return MakeResult(id, new JObject { ["tools"] = tools });
    }

    // Lazy cache of tool schemas keyed by tool name. Used by
    // ValidateRequiredArguments() to translate missing-required-field calls
    // into proper -32602 errors instead of misleading "Unknown tool" /
    // null-deref crashes inside Build*Command helpers (see commit messages on this branch).
    //
    private Dictionary<string, JObject>? _toolSchemaCache;

    private Dictionary<string, JObject> GetToolSchemaMap()
    {
        if (_toolSchemaCache != null) return _toolSchemaCache;
        var map = new Dictionary<string, JObject>(StringComparer.Ordinal);
        foreach (JObject tool in ToolRegistry.AllTools())
        {
            string? n = tool["name"]?.Value<string>();
            if (string.IsNullOrEmpty(n)) continue;
            JObject? schema = tool["inputSchema"] as JObject;
            if (schema != null) map[n] = schema;
        }
        _toolSchemaCache = map;
        return map;
    }

    /// <summary>
    /// Walks <c>inputSchema.required</c> for the named tool and returns a
    /// human-readable error string when any required argument is missing or
    /// empty. Returns <c>null</c> when validation passes (or the tool has no
    /// schema entry — those are dispatched as before).
    /// </summary>
    private string? ValidateRequiredArguments(string toolName, JObject arguments)
    {
        if (!GetToolSchemaMap().TryGetValue(toolName, out JObject? schema))
            return null; // unknown tool — let MapToolToCommand emit -32602
        JArray? required = schema["required"] as JArray;
        if (required == null || required.Count == 0) return null;

        foreach (JToken r in required)
        {
            string? key = r.Value<string>();
            if (string.IsNullOrEmpty(key)) continue;
            JToken? v = arguments[key];
            bool missing = v == null || v.Type == JTokenType.Null;
            if (!missing && v!.Type == JTokenType.String)
            {
                string s = v.Value<string>() ?? string.Empty;
                if (s.Length == 0) missing = true;
            }
            if (missing)
                return $"Missing required argument: {key} (tool: {toolName})";
        }
        return null;
    }


    private JObject HandleToolCall(JObject request, JToken? id)
    {
        JObject? paramsObj = request["params"] as JObject;
        string? toolName = paramsObj?["name"]?.Value<string>();
        JObject? arguments = paramsObj?["arguments"] as JObject ?? new JObject();

        if (string.IsNullOrEmpty(toolName))
        {
            return MakeErrorResponse(id, -32602, "Missing tool name.");
        }
        _metrics.RecordCall(toolName);
        _rateLimits.RecordCall(toolName);
        var _latencySw = System.Diagnostics.Stopwatch.StartNew();

        // Event streaming tools — handled directly (no REPL dispatch)
        JObject? evtResponse = HandleEventTool(toolName, arguments);
        if (evtResponse != null)
        {
            var evtContent = new JArray { new JObject { ["type"] = "text", ["text"] = evtResponse.ToString(Newtonsoft.Json.Formatting.None) } };
            return MakeResult(id, new JObject { ["content"] = evtContent, ["isError"] = false });
        }

        // Registry-driven ConfirmGate. Confirm-required tools (start_all /
        // stop_all / fleet_disconnect and every other destructive tool)
        // declare confirm in inputSchema.required, so the gate catches them
        // by virtue of registry lookup. We surface rejections as -32602
        // (same shape ValidateRequiredArguments uses for missing-required-field
        // errors) so every confirm-required tool returns a uniform JSON-RPC
        // error envelope to callers.
        //
        // ConfirmGate runs BEFORE HandleInternalTool so destructive internal
        // tools (mt_core_shutdown, mt_vault_delete_profile, …) surface the
        // same -32602 envelope as REPL-dispatched tools, instead of an
        // inner-body { "error": ... } envelope that would violate the
        // uniform-shape contract.
        string? confirmReject = ConfirmGate.RejectIfMissing(toolName, arguments);
        if (confirmReject != null)
        {
            _metrics.RecordError(toolName);
            _metrics.RecordLatency(toolName, _latencySw.ElapsedMilliseconds);
            return MakeErrorResponse(id, -32602, confirmReject);
        }

        // Internal tools with multi-step logic
        JObject? internalResponse = HandleInternalTool(toolName, arguments);
        if (internalResponse != null)
        {
            _metrics.RecordLatency(toolName, _latencySw.ElapsedMilliseconds);
            var internalContent = new JArray { new JObject { ["type"] = "text", ["text"] = internalResponse.ToString(Newtonsoft.Json.Formatting.None) } };
            return MakeResult(id, new JObject { ["content"] = internalContent, ["isError"] = false });
        }

        // EN review #8 — argument sanitization at the MCP boundary.
        // Tool arguments are interpolated verbatim into a re-parsed REPL line
        // (e.g. " @{profile}", "exchange search {query}"). Two classes of
        // argument values are treated as REPL meta-syntax and would break out
        // of the intended command:
        //   * any string containing \r or \n  -> would inject a second
        //     REPL line, executing an arbitrary follow-up command.
        //   * the 'profile' argument starting with '@' or containing
        //     whitespace -> would shadow / replace the implicit profile
        //     suffix.
        // The fix below rejects such values at the gateway with a clear
        // error, before any REPL parsing or command dispatch occurs.
        string? sanitizationError = ValidateArguments(toolName, arguments);
        if (sanitizationError != null)
        {
            return MakeErrorResponse(id, -32602, sanitizationError);
        }

        // Reject missing required arguments BEFORE dispatch so we emit
        // "-32602 Missing required argument: <name>" instead of the misleading
        // "Unknown tool: …" produced by Build*Command helpers returning null.
        string? requiredError = ValidateRequiredArguments(toolName, arguments);
        if (requiredError != null)
        {
            _metrics.RecordError(toolName);
            _metrics.RecordLatency(toolName, _latencySw.ElapsedMilliseconds);
            return MakeErrorResponse(id, -32602, requiredError);
        }

        // Map tool name to REPL command
        string? commandLine = MapToolToCommand(toolName, arguments);
        if (commandLine == null)
        {
            return MakeErrorResponse(id, -32602, $"Unknown tool: {toolName}");
        }

        // Execute via CommandRegistry
        CommandResult result = _registry.Dispatch(commandLine);
        _metrics.RecordLatency(toolName, _latencySw.ElapsedMilliseconds);
        if (!result.Success) _metrics.RecordError(toolName);

        // Format response
        var content = new JArray
        {
            new JObject
            {
                ["type"] = "text",
                ["text"] = _output.Format(result)
            }
        };

        var resultObj = new JObject
        {
            ["content"] = content,
            ["isError"] = !result.Success
        };

        return MakeResult(id, resultObj);
    }


    /// <summary>
    /// MCP-only safety gate: tools listed here refuse to execute unless
    /// the caller supplied <c>confirm=true</c>. They are bulk / fleet-wide
    /// operations whose accidental invocation can be costly. The underlying
    /// REPL commands are NOT modified — interactive TUI users keep their
    /// existing semantics.
    /// </summary>
    /// <summary>Issue #16: optional market_type for mt_exchange_ticker24 ("FUTURES" or "SPOT").</summary>
    private static string ResolveTicker24Market(JObject arguments)
    {
        string? mt = arguments["market_type"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(mt)) return "";
        string norm = mt.Trim().ToUpperInvariant();
        if (norm == "FUTURES" || norm == "SPOT") return $" {norm}";
        return ""; // silently ignore garbage; sanitizer handles \r/\n already
    }

    /// <summary>Two-mode dispatch for mt_marketdata_ticker:
    ///   - symbol present → "marketdata ticker &lt;SYMBOL&gt; [&lt;MARKET&gt;]"
    ///     routes HandleTicker through the per-symbol one-shot ticker24 path
    ///     which works on every vendor build.
    ///   - no symbol     → "marketdata ticker [&lt;MARKET&gt;]" bulk cache-read
    ///     for ALL symbols on the requested market type.</summary>
    private static string BuildMarketdataTicker(JObject arguments, string profileSuffix)
    {
        string symbol = (arguments["symbol"]?.Value<string>() ?? "").Trim();
        string market = (arguments["market"]?.Value<string>() ?? "").Trim();
        if (symbol.Length > 0)
        {
            return market.Length > 0
                ? $"marketdata ticker {symbol} {market}{profileSuffix}"
                : $"marketdata ticker {symbol}{profileSuffix}";
        }
        return market.Length > 0
            ? $"marketdata ticker {market}{profileSuffix}"
            : $"marketdata ticker{profileSuffix}";
    }

    // Confirm checks for destructive tools are handled by the registry-driven
    // ConfirmGate.IsConfirmRequired() (Core/ConfirmGate.cs). Tools like
    // mt_algos_start_all, mt_algos_stop_all, mt_fleet_disconnect declare
    // confirm in inputSchema.required, so the registry-driven gate catches
    // them uniformly with every other destructive tool.

    /// <summary>
    /// EN review #8: validate tool arguments at the MCP boundary before they
    /// are interpolated into a REPL line. Returns null when arguments are
    /// safe, otherwise returns a JSON-RPC-friendly error message.
    ///
    /// Rules enforced:
    ///   * No string argument may contain '\r' or '\n' (REPL-line injection).
    ///   * The 'profile' argument additionally:
    ///       - must not start with '@' (would collide with the implicit
    ///         profile suffix syntax),
    ///       - must not contain whitespace,
    ///       - must not contain '"' (the interactive REPL parser treats it
    ///         as a quoted-string boundary).
    /// </summary>
    private static string? ValidateArguments(string toolName, Newtonsoft.Json.Linq.JObject arguments)
    {
        foreach (var prop in arguments.Properties())
        {
            if (prop.Value.Type != Newtonsoft.Json.Linq.JTokenType.String) continue;
            string? val = prop.Value.Value<string>();
            if (val == null) continue;

            if (val.IndexOf('\n') >= 0 || val.IndexOf('\r') >= 0)
            {
                return $"Argument '{prop.Name}' contains a newline; rejected (would inject a second REPL command).";
            }

            if (prop.Name == "profile")
            {
                if (val.Length > 0 && val[0] == '@')
                {
                    return $"Argument 'profile' must not start with '@'.";
                }
                for (int i = 0; i < val.Length; i++)
                {
                    char c = val[i];
                    if (c == ' ' || c == '\t' || c == '"')
                    {
                        return $"Argument 'profile' must not contain whitespace or quotes (got: {Newtonsoft.Json.JsonConvert.ToString(val)}).";
                    }
                }
            }
        }
        return null;
    }


    /// <summary>
    /// Build a HttpClient configured for HashiCorp Vault calls. Centralized so
    /// the timeout (default 10 s) and X-Vault-Token header are applied
    /// uniformly to every Vault site. Without an explicit timeout, .NET's
    /// HttpClient defaults to 100 s — long enough that a network black-hole
    /// or unresponsive Vault instance can wedge the MCP request thread
    /// (which is invoked synchronously via .GetAwaiter().GetResult()).
    /// </summary>
    private static System.Net.Http.HttpClient BuildVaultHttpClient(string vaultToken)
    {
        // Allow override via VAULT_HTTP_TIMEOUT_SEC (clamped to [1, 120]).
        int timeoutSec = 10;
        string? env = Environment.GetEnvironmentVariable("VAULT_HTTP_TIMEOUT_SEC");
        if (int.TryParse(env, out int parsed) && parsed >= 1 && parsed <= 120)
        {
            timeoutSec = parsed;
        }
        var http = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(timeoutSec)
        };
        http.DefaultRequestHeaders.Add("X-Vault-Token", vaultToken);   // token in header only
        return http;
    }

    /// <summary>Map an MCP tool name + arguments to a REPL command string.</summary>
    /// <summary>
    /// Exposed so the DispatcherSnapshotGenerator and the Static
    /// DispatcherSnapshotTests can probe the CLI string each registry tool
    /// dispatches to with a deterministic argument set. Stays a static method
    /// — no McpServer instance is required to translate a tool/args pair to
    /// the CLI command string. Build*Command helpers it calls stay private.
    /// </summary>
    public static string? MapToolToCommand(string toolName, JObject arguments)
    {
        string? profile = arguments["profile"]?.Value<string>();
        string? profileSuffix = profile != null ? $" @{profile}" : "";
        string? confirm = arguments["confirm"]?.Value<bool>() == true ? " --confirm" : "";

        return toolName switch
        {
            // Connection
            "mt_connect" => $"connect {arguments["profile"]?.Value<string>() ?? ""}",
            "mt_disconnect" => $"disconnect {arguments["profile"]?.Value<string>() ?? ""}",
            "mt_status" => "status",
            "mt_use" => $"use {arguments["profile"]?.Value<string>() ?? ""}",

            // Account
            // Issue #16: surface CLI flags (-all / count) on MCP wrappers
            "mt_account_balance" => arguments["show_all"]?.Value<bool>() == true
                ? $"account balance -all{profileSuffix}"
                : $"account balance{profileSuffix}",
            "mt_account_orders" => arguments["show_all"]?.Value<bool>() == true
                ? $"account orders -all{profileSuffix}"
                : $"account orders{profileSuffix}",
            "mt_account_positions" => arguments["show_all"]?.Value<bool>() == true
                ? $"account positions -all{profileSuffix}"
                : $"account positions{profileSuffix}",
            "mt_account_executions" => arguments["count"]?.Value<int?>() is int execCount && execCount > 0
                ? $"account executions {execCount}{profileSuffix}"
                : $"account executions{profileSuffix}",
            "mt_account_info" => $"account info{profileSuffix}",
            "mt_account_summary" => $"account summary{profileSuffix}",

            // Core status
            "mt_core_status" => $"core status{profileSuffix}",
            "mt_core_license" => $"core license{profileSuffix}",
            "mt_core_health" => $"core health{profileSuffix}",
            "mt_core_dashboard" => $"core dashboard{profileSuffix}",

            // Exchange
            "mt_exchange_summary" => $"exchange summary{profileSuffix}",
            "mt_exchange_pairs" => $"exchange pairs{profileSuffix}",
            "mt_exchange_search" => $"exchange search {arguments["query"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_exchange_pair_detail" => $"exchange detail {arguments["symbol"]?.Value<string>() ?? ""}{profileSuffix}",

            // Exchange data queries
            "mt_exchange_ticker24" => $"exchange ticker24 {arguments["symbol"]?.Value<string>() ?? ""}{ResolveTicker24Market(arguments)}{profileSuffix}",
            "mt_exchange_klines" => BuildKlinesCommand(arguments, profileSuffix),
            "mt_exchange_trades" => $"exchange trades {arguments["symbol"]?.Value<string>() ?? ""}{profileSuffix}",
            // Funding rate + leverage brackets (read-only).
            "mt_exchange_funding_rate" => BuildExchangeFundingRateCommand(arguments, profileSuffix),
            "mt_exchange_leverage_brackets" => BuildExchangeLeverageBracketsCommand(arguments, profileSuffix),

            // Algorithms
            "mt_algos_list" => $"algos list{profileSuffix}",
            "mt_algos_list_all" => "algos list-all",
            "mt_algos_search" => $"algos search {arguments["query"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_get" => $"algos get {arguments["id"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_start" => $"algos start {arguments["id"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_stop" => $"algos stop {arguments["id"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_start_all" => $"algos start-all{profileSuffix}",

            // Algo verification
            "mt_algos_start_verified" => BuildStartVerifyCommand(arguments, profileSuffix),
            "mt_algos_verify"         => BuildVerifyCommand(arguments, profileSuffix),
            "mt_algos_stop_all" => $"algos stop-all{profileSuffix}",

            // Batch algo operations — start/stop/config across multiple servers
            "mt_algos_batch_start"  => BuildBatchAlgoCommand("batchstart", arguments),
            "mt_algos_batch_stop"   => BuildBatchAlgoCommand("batchstop",  arguments),
            "mt_algos_batch_config" => BuildBatchAlgoConfigCommand(arguments),
            "mt_algos_save" => $"algos save {arguments["id"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_save_start" => $"algos save-start {arguments["id"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_delete" => $"algos delete {arguments["id"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_algos_toggle_debug" => $"algos toggle-debug {arguments["id"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_rename" => $"algos rename {arguments["id"]?.Value<string>() ?? ""} {arguments["name"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_config" => $"algos config {arguments["id"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_config_set" => $"algos config {arguments["id"]?.Value<string>() ?? ""} set {arguments["key"]?.Value<string>() ?? ""} {arguments["value"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_groups" => $"algos groups{profileSuffix}",
            "mt_algos_group" => $"algos group {arguments["group_id"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_clone_group" => $"algos clone-group {arguments["group_id"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_delete_group" => $"algos delete-group {arguments["group_id"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_algos_copy" => BuildCopyCommand(arguments, confirm),
            "mt_algos_export" => $"algos export {arguments["id"]?.Value<string>() ?? ""}{profileSuffix}",
            // Clipboard / paste / import-json.
            "mt_algos_copy_to_clipboard" => $"algos copy-to-clipboard {arguments["id"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_paste_from_clipboard" => BuildPasteFromClipboardCommand(arguments, confirm),
            "mt_algos_import_json" => BuildImportJsonCommand(arguments, confirm),
            "mt_algos_bulk_edit" => BuildBulkEditCommand(arguments, profileSuffix, confirm),
            "mt_algos_create" => BuildAlgosCreateCommand(arguments, profileSuffix, confirm),

            // Settings
            "mt_settings_get" => arguments.ContainsKey("key")
                ? $"settings get {arguments["key"]?.Value<string>()}{profileSuffix}"
                : $"settings get{profileSuffix}",
            "mt_settings_search" => $"settings search {arguments["query"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_settings_set" => $"settings set {arguments["key"]?.Value<string>() ?? ""} {arguments["value"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_settings_groups" => $"settings groups{profileSuffix}",

            // Import
            "mt_import_v2" => $"import v2 {arguments["path"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_import_templates" => arguments["path"]?.Value<string>() is { Length: > 0 } templPath
                ? $"import templates {templPath}"
                : "import templates",
            "mt_import_add_numeric" =>
                $"import add-numeric {arguments["id"]?.Value<string>() ?? ""} {arguments["delta"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",

            // Orders
            "mt_orders_list" => $"orders list{profileSuffix}",
            "mt_orders_positions" => $"orders positions{profileSuffix}",
            "mt_orders_cancel" => $"orders cancel {arguments["client_order_id"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_cancel_all" =>
                arguments.ContainsKey("symbol")
                    ? $"orders cancel-all {arguments["symbol"]?.Value<string>()}{profileSuffix}{confirm}"
                    : $"orders cancel-all{profileSuffix}{confirm}",
            "mt_orders_close" =>
                arguments.ContainsKey("percentage")
                    ? $"orders close {arguments["symbol"]?.Value<string>() ?? ""} {arguments["percentage"]?.Value<string>()}{profileSuffix}{confirm}"
                    : $"orders close {arguments["symbol"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_close_all" => $"orders close-all{profileSuffix}{confirm}",

            // Order operations
            "mt_orders_place" => BuildPlaceOrderCommand(arguments, profileSuffix, confirm),
            "mt_orders_move" => $"orders move {arguments["client_order_id"]?.Value<string>() ?? ""} {arguments["new_price"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_set_leverage" => $"orders set-leverage {arguments["symbol"]?.Value<string>() ?? ""} {arguments["leverage"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_set_margin_type" => $"orders set-margin-type {arguments["symbol"]?.Value<string>() ?? ""} {arguments["margin_type"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_set_position_mode" => $"orders set-position-mode {arguments["symbol"]?.Value<string>() ?? ""} {arguments["mode"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_get_position_mode" => $"orders get-position-mode {arguments["symbol"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_orders_get_position_mode_account" => $"orders get-position-mode-account{profileSuffix}",
            "mt_orders_panic_sell" => $"orders panic-sell {arguments["symbol"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_change_margin" => $"orders change-margin {arguments["symbol"]?.Value<string>() ?? ""} {arguments["position_side"]?.Value<string>() ?? "BOTH"} {arguments["amount"]?.Value<string>() ?? ""} {arguments["action"]?.Value<string>() ?? "add"}{profileSuffix}{confirm}",
            "mt_orders_transfer" => $"orders transfer {arguments["asset"]?.Value<string>() ?? ""} {arguments["amount"]?.Value<string>() ?? ""} {arguments["from"]?.Value<string>() ?? ""} {arguments["to"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_set_leverage_buysell" => $"orders set-leverage-buysell {arguments["asset"]?.Value<string>() ?? ""} {arguments["buy_leverage"]?.Value<string>() ?? ""} {arguments["sell_leverage"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_get_multiasset" => $"orders get-multiasset {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_orders_set_multiasset" => $"orders set-multiasset {arguments["enabled"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",


            // Reports — historical trade data
            "mt_reports_trades" => BuildReportsCommand(arguments, profileSuffix),
            "mt_reports_comments" => $"reports comments{profileSuffix}",
            "mt_reports_dates" => $"reports dates{profileSuffix}",

            // Fleet-wide operations
            "mt_fleet_connect" => arguments.ContainsKey("filter")
                ? $"fleet connect {arguments["filter"]?.Value<string>()}"
                : "fleet connect",
            "mt_fleet_status" => "fleet status",
            "mt_fleet_balances" => "fleet balances",
            "mt_fleet_positions" => "fleet positions",
            "mt_fleet_algos" => "fleet algos",
            "mt_fleet_health" => "fleet health",
            "mt_fleet_summary" => "fleet summary",
            "mt_fleet_disconnect" => "fleet disconnect",

            // Batch connect to specific named profiles in parallel
            "mt_fleet_batch_connect" => BuildBatchConnectCommand(arguments),

            // Connection pool health — latency/error/reconnect metrics per profile
            "mt_connection_health" => "fleet connhealth",

            // Server tagging — set/get fleet orchestration labels
            "mt_connection_tag" => BuildTagCommand(arguments),
            "mt_connection_tags" => profile != null ? $"tag {profile}" : "tag",

            // Monitor — real-time core monitoring via UDP
            "mt_monitor_start" => $"monitor start{profileSuffix}",
            "mt_monitor_stop" => $"monitor stop{profileSuffix}",
            "mt_monitor_status" => $"monitor status{profileSuffix}",
            "mt_monitor_health" => $"monitor health{profileSuffix}",
            "mt_monitor_performance" => BuildMonitorSimpleCommand("performance", arguments, profileSuffix),
            "mt_monitor_stats" => $"monitor stats{profileSuffix}",


            // AutoStops (Risk Management)
            "mt_autostops_list" => $"autostops list{profileSuffix}",
            "mt_autostops_baseline" => $"autostops baseline{profileSuffix}",
            "mt_autostops_reports" => $"autostops reports {arguments["ids"]?.Value<string>() ?? ""}{profileSuffix}",
            // Balance-filter CRUD
            "mt_autostops_add" => BuildAutoStopAddCommand(arguments, profileSuffix, confirm),
            "mt_autostops_edit" => BuildAutoStopEditCommand(arguments, profileSuffix, confirm),
            "mt_autostops_start" => BuildAutoStopToggleCommand("start", arguments, profileSuffix, confirm),
            "mt_autostops_stop" => BuildAutoStopToggleCommand("stop", arguments, profileSuffix, confirm),
            "mt_autostops_delete" => BuildAutoStopDeleteCommand(arguments, profileSuffix, confirm),

            // Blacklist (Risk Management)
            "mt_blacklist_list" => $"blacklist list{profileSuffix}",
            "mt_blacklist_add" => BuildBlacklistMutationCommand("add", arguments, profileSuffix, confirm),
            "mt_blacklist_remove" => BuildBlacklistMutationCommand("remove", arguments, profileSuffix, confirm),
            // Profile-level whitelist CRUD.
            "mt_whitelist_list" => $"whitelist list{profileSuffix}",
            "mt_whitelist_add" => BuildWhitelistMutationCommand("add", arguments, profileSuffix, confirm, bulk: false),
            "mt_whitelist_remove" => BuildWhitelistMutationCommand("remove", arguments, profileSuffix, confirm, bulk: false),
            "mt_whitelist_bulk_add" => BuildWhitelistMutationCommand("bulk-add", arguments, profileSuffix, confirm, bulk: true),
            "mt_whitelist_bulk_remove" => BuildWhitelistMutationCommand("bulk-remove", arguments, profileSuffix, confirm, bulk: true),

            // TPSL (Take Profit / Stop Loss)
            "mt_tpsl_list" => $"tpsl list{profileSuffix}",
            "mt_tpsl_subscribe" => $"tpsl subscribe{profileSuffix}",
            "mt_tpsl_unsubscribe" => $"tpsl unsubscribe{profileSuffix}",
            "mt_tpsl_cancel" => $"tpsl cancel {arguments["id"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",

            // Trading Performance
            "mt_perf_list" => $"perf list{profileSuffix}",
            "mt_perf_subscribe" => $"perf subscribe {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_perf_unsubscribe" => $"perf unsubscribe{profileSuffix}",
            "mt_perf_request" => $"perf request {arguments["action"]?.Value<string>() ?? "refresh"}{profileSuffix}",

            // Reports Enhancement
            "mt_reports_export" => BuildReportsExportCommand(arguments, profileSuffix),
            "mt_reports_fleet_export" => BuildReportsFleetExportCommand(arguments),
            "mt_reports_store" => BuildReportsStoreCommand(arguments, profileSuffix),
            "mt_reports_stored" => "reports stored",
            "mt_reports_load" => $"reports load {arguments["name"]?.Value<string>() ?? ""}",
            "mt_reports_delete" => $"reports delete {arguments["name"]?.Value<string>() ?? ""}",

            // Fleet P4 Extensions
            "mt_fleet_autostops" => "fleet autostops",
            "mt_fleet_blacklist" => "fleet blacklist",
            "mt_fleet_set_margin_type" => BuildFleetSetMarginTypeCommand(arguments, confirm),
            "mt_fleet_perf" => "fleet perf",
            "mt_fleet_reports" => $"fleet reports {arguments["period"]?.Value<string>() ?? ""}",

            // Notifications
            "mt_notifications_list" => $"notifications list{BuildCountArg(arguments)}{profileSuffix}",
            "mt_notifications_subscribe" => $"notifications subscribe{profileSuffix}",
            "mt_notifications_unsubscribe" => $"notifications unsubscribe{profileSuffix}",
            "mt_notifications_clear" => $"notifications clear{profileSuffix}",

            // Market Data
            "mt_marketdata_status" => $"marketdata status{profileSuffix}",
            "mt_marketdata_trades" => $"marketdata trades {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_marketdata_trades_subscribe" => $"marketdata trades-subscribe {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_marketdata_trades_unsubscribe" => $"marketdata trades-unsubscribe {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_marketdata_depth" => $"marketdata depth {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_marketdata_depth_subscribe" => $"marketdata depth-subscribe {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_marketdata_depth_unsubscribe" => $"marketdata depth-unsubscribe {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_marketdata_markprice" => $"marketdata markprice {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_marketdata_markprice_subscribe" => $"marketdata markprice-subscribe {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_marketdata_markprice_unsubscribe" => $"marketdata markprice-unsubscribe {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_marketdata_klines" => $"marketdata klines {arguments["symbol"]?.Value<string>() ?? ""} {arguments["interval"]?.Value<string>() ?? "1m"} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_marketdata_klines_subscribe" => $"marketdata klines-subscribe {arguments["symbol"]?.Value<string>() ?? ""} {arguments["interval"]?.Value<string>() ?? "1m"} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_marketdata_klines_unsubscribe" => $"marketdata klines-unsubscribe {arguments["symbol"]?.Value<string>() ?? ""} {arguments["interval"]?.Value<string>() ?? "1m"} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_marketdata_ticker" => BuildMarketdataTicker(arguments, profileSuffix),
            "mt_marketdata_ticker_subscribe" => $"marketdata ticker-subscribe {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_marketdata_ticker_unsubscribe" => $"marketdata ticker-unsubscribe {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",

            // Alerts
            "mt_alerts_list" => $"alerts list{profileSuffix}",
            "mt_alerts_subscribe" => $"alerts subscribe{profileSuffix}",
            "mt_alerts_unsubscribe" => $"alerts unsubscribe{profileSuffix}",
            "mt_alerts_history" => $"alerts history{BuildCountArg(arguments)}{profileSuffix}",
            "mt_alerts_history_subscribe" => $"alerts history-subscribe{profileSuffix}",
            "mt_alerts_history_unsubscribe" => $"alerts history-unsubscribe{profileSuffix}",

            // Profiling
            "mt_profiling_subscribe" => $"profiling subscribe {arguments["symbol"]?.Value<string>() ?? ""} {arguments["algo_id"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_profiling_unsubscribe" => $"profiling unsubscribe {arguments["symbol"]?.Value<string>() ?? ""} {arguments["algo_id"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",

            // Triggers
            "mt_triggers_list" => $"triggers list{BuildCountArg(arguments)}{profileSuffix}",
            "mt_triggers_subscribe" => $"triggers subscribe{profileSuffix}",
            "mt_triggers_unsubscribe" => $"triggers unsubscribe{profileSuffix}",
            "mt_triggers_save" => $"triggers save {arguments["data"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_triggers_delete" => $"triggers delete {arguments["data"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_triggers_start" => $"triggers start {arguments["data"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_triggers_stop" => $"triggers stop {arguments["data"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_triggers_start_all" => $"triggers start-all{profileSuffix}",
            "mt_triggers_stop_all" => $"triggers stop-all{profileSuffix}",

            // LiveMarkets
            "mt_livemarkets_list" => $"livemarkets list{profileSuffix}",
            "mt_livemarkets_subscribe" => $"livemarkets subscribe {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""} {arguments["quote_asset"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_livemarkets_unsubscribe" => $"livemarkets unsubscribe {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""} {arguments["quote_asset"]?.Value<string>() ?? ""}{profileSuffix}",

            // AutoBuy
            "mt_autobuy_list" => $"autobuy list{BuildCountArg(arguments)}{profileSuffix}",
            "mt_autobuy_subscribe" => $"autobuy subscribe{profileSuffix}",
            "mt_autobuy_unsubscribe" => $"autobuy unsubscribe{profileSuffix}",
            "mt_autobuy_save" => $"autobuy save {arguments["data"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_autobuy_delete" => $"autobuy delete {arguments["data"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_autobuy_start" => $"autobuy start {arguments["data"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_autobuy_stop" => $"autobuy stop {arguments["data"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_autobuy_refresh_pairs" => $"autobuy refresh-pairs{profileSuffix}",

            // GraphTool
            "mt_graphtool_list" => $"graphtool list{BuildCountArg(arguments)}{profileSuffix}",
            "mt_graphtool_subscribe" => $"graphtool subscribe{profileSuffix}",
            "mt_graphtool_unsubscribe" => $"graphtool unsubscribe{profileSuffix}",
            "mt_graphtool_save" => $"graphtool save {arguments["data"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_graphtool_delete" => $"graphtool delete {arguments["data"]?.Value<string>() ?? ""}{profileSuffix}",

            // Signals
            "mt_signals_send" => $"signals send {arguments["symbol"]?.Value<string>() ?? ""} {arguments["side"]?.Value<string>() ?? ""} {arguments["price"]?.Value<string>() ?? ""} --market={arguments["market"]?.Value<string>() ?? "FUTURES"} --tp={arguments["take_profit"]?.Value<string>() ?? "0"} --sl={arguments["stop_loss"]?.Value<string>() ?? "0"} --channel={arguments["channel"]?.Value<string>() ?? "default"}{profileSuffix}",

            // Dust
            "mt_dust_get" => $"dust get{profileSuffix}",
            "mt_dust_convert" => $"dust convert{profileSuffix}",

            // Deposit
            "mt_deposit_info" => $"deposit info {arguments["coin"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_deposit_address" => $"deposit address {arguments["coin"]?.Value<string>() ?? ""} {arguments["network"]?.Value<string>() ?? ""}{profileSuffix}",

            // Extended Orders
            "mt_orders_move_batch" => $"orders move-batch {arguments["orders_json"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_orders_join" => $"orders join {arguments["client_order_id"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_orders_split" => $"orders split {arguments["client_order_id"]?.Value<string>() ?? ""} {arguments["count"]?.Value<string>() ?? "2"} {arguments["percentage"]?.Value<string>() ?? "50"} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_fund_transfer" => $"orders fund-transfer {arguments["from_account"]?.Value<string>() ?? ""} {arguments["asset"]?.Value<string>() ?? ""} {arguments["amount"]?.Value<string>() ?? ""} {arguments["to_account"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_profile_settings_get" => $"settings profile-get {arguments["profile_name"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_profile_settings_update" => BuildProfileSettingsUpdateCommand(arguments, profileSuffix, confirm),
            // List keys + bulk delete.
            "mt_profile_settings_list" => BuildProfileSettingsListCommand(arguments, profileSuffix),
            "mt_profile_settings_delete" => BuildProfileSettingsDeleteCommand(arguments, profileSuffix, confirm),
            // Local profiles.json CRUD.
            "mt_profiles_list" => "profiles list",
            "mt_profiles_add" => BuildProfilesAddCommand(arguments, confirm),
            "mt_profiles_edit" => BuildProfilesEditCommand(arguments, confirm),
            "mt_profiles_delete" => $"profiles delete {SanitiseToken(arguments["name"]?.Value<string>())}{confirm}",
            "mt_profiles_move" => $"profiles move {SanitiseToken(arguments["name"]?.Value<string>())} {SanitiseToken(arguments["folder"]?.Value<string>())}{confirm}",
            "mt_profiles_import_csv" => $"profiles import-csv {SanitisePath(arguments["path"]?.Value<string>())}{confirm}",
            // Local folders.json CRUD.
            "mt_folders_list" => "folders list",
            "mt_folders_add" => $"folders add {SanitiseToken(arguments["name"]?.Value<string>())}{confirm}",
            "mt_folders_edit" => $"folders edit {SanitiseToken(arguments["old_name"]?.Value<string>())} {SanitiseToken(arguments["new_name"]?.Value<string>())}{confirm}",
            "mt_folders_delete" => $"folders delete {SanitiseToken(arguments["name"]?.Value<string>())}{confirm}",
            "mt_core_restart" => $"core restart{profileSuffix}{confirm}",
            "mt_core_restart_update" => $"core restart-update{profileSuffix}{confirm}",
            "mt_core_clear_orders" => $"core clear-orders{profileSuffix}{confirm}",
            "mt_core_clear_archive" => $"core clear-archive{profileSuffix}{confirm}",
            "mt_core_advanced_restart" => $"core advanced-restart" +
                (arguments["include_update"]?.Value<bool>() == true ? " --update" : string.Empty) +
                (arguments["clear_orders_cache"]?.Value<bool>() == true ? " --clear-orders" : string.Empty) +
                (arguments["clear_data_archive"]?.Value<bool>() == true ? " --clear-archive" : string.Empty) +
                profileSuffix + confirm,

            // order_type is appended as `--order-type <type>` when provided.
            // The handler defaults to MARKET when the flag is absent — back-compat with
            // earlier callers that didn't pass order_type.
            "mt_orders_close_by_tpsl" => $"orders close-by-tpsl {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""} {arguments["side"]?.Value<string>() ?? ""}{BuildOrderTypeArg(arguments)}{profileSuffix}{confirm}",
            "mt_orders_reset_tpsl" => $"orders reset-tpsl {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""} {arguments["side"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            // Active Order TP/SL/TS Update
            "mt_orders_update_tpsl" => BuildUpdateOrderTpslCommand(arguments, profileSuffix, confirm),

            "mt_tpsl_join" => $"tpsl join {BuildTpslJoinIds(arguments)}{profileSuffix}{confirm}",
            "mt_tpsl_split" => $"tpsl split {arguments["tpsl_id"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            // TPSL bulk operations. The "many" tools accept tpsl_ids as an
            // array; BuildTpslJoinIds happens to do exactly the right thing here too
            // (JArray → space-joined, with legacy string-form fallback).
            "mt_tpsl_cancel_many" => $"tpsl cancel-many {BuildTpslJoinIds(arguments)}{profileSuffix}{confirm}",
            "mt_tpsl_split_many"  => $"tpsl split-many {BuildTpslJoinIds(arguments)}{profileSuffix}{confirm}",
            // TPSL panic close (single + bulk).
            "mt_tpsl_panic"       => $"tpsl panic {arguments["tpsl_id"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_tpsl_panic_many"  => $"tpsl panic-many {BuildTpslJoinIds(arguments)}{profileSuffix}{confirm}",

            "mt_funding_request" => $"funding request{profileSuffix}",

            "mt_buylimit_request" => $"buylimit request {arguments["amount"]?.Value<string>() ?? ""}{profileSuffix}",

            _ => null
        };
    }
    /// <summary>
    /// Build the REPL command string for mt_connection_tag.
    /// Format: tag <profile> <key> <value>
    /// </summary>
    /// <summary>Build: algos start-verify <id> [wait_secs] [@profile]</summary>
    /// <summary>Build: algos verify <id> [@profile]</summary>
    private static string? BuildVerifyCommand(JObject arguments, string profileSuffix)
    {
        string? id = arguments["id"]?.Value<string>();






        return string.IsNullOrWhiteSpace(id) ? null : $"algos verify {id}{profileSuffix}";
    }

    private static string? BuildStartVerifyCommand(JObject arguments, string profileSuffix)
    {
        string? id = arguments["id"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(id)) return null;
        string waitSecs = arguments["wait_secs"]?.Value<string>() ?? "";
        string waitArg  = !string.IsNullOrWhiteSpace(waitSecs) ? $" {waitSecs}" : "";
        return $"algos start-verify {id}{waitArg}{profileSuffix}";
    }

    private static string? BuildTagCommand(JObject arguments)
    {
        string? p    = arguments["profile"]?.Value<string>();
        string? key  = arguments["key"]?.Value<string>();
        string? val  = arguments["value"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(p) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(val))
            return null;
        return $"tag {p} {key} {val}";
    }


    /// <summary>
    /// <summary>
    /// Build: fleet batchstart/batchstop <algo> [profile1 ...]
    /// </summary>
    private static string? BuildBatchAlgoCommand(string subcommand, JObject arguments)
    {
        string? algo = arguments["algo"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(algo)) return null;
        var sb = new System.Text.StringBuilder($"fleet {subcommand} {algo}");
        bool hasProfiles = AppendProfilesFromArguments(sb, arguments);
        // ISS-4 safety: require explicit profiles or all_servers=true for batch start
        if (!hasProfiles && subcommand == "batchstart")
        {
            bool allServers = arguments["all_servers"]?.Value<bool>() == true
                           || arguments["all_servers"]?.Value<string>()?.ToLower() == "true";
            if (!allServers) return null; // caller must pass all_servers=true or explicit profiles
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build: fleet batchconfig <algo> <key> <value> [profile1 ...]
    /// </summary>
    private static string? BuildBatchAlgoConfigCommand(JObject arguments)
    {
        string? algo  = arguments["algo"]?.Value<string>();
        string? key   = arguments["key"]?.Value<string>();
        string? value = arguments["value"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(algo) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) return null;
        var sb = new System.Text.StringBuilder($"fleet batchconfig {algo} {key} {value}");
        AppendProfilesFromArguments(sb, arguments);
        return sb.ToString();
    }
    /// <summary>
    /// ISS-3 fix: Parse profiles from arguments, handling both JArray and string types.
    /// Returns true if any profiles were appended to the command.
    /// </summary>
    private static bool AppendProfilesFromArguments(System.Text.StringBuilder sb, JObject arguments)
    {
        JToken? profilesToken = arguments["profiles"];
        if (profilesToken is JArray profilesArray)
        {
            bool any = false;
            foreach (JToken t in profilesArray)
            {
                string? p = t.Value<string>();
                if (!string.IsNullOrWhiteSpace(p)) { sb.Append(" "); sb.Append(p); any = true; }
            }
            return any;
        }
        else if (profilesToken != null && profilesToken.Type == JTokenType.String)
        {
            string? profileStr = profilesToken.Value<string>();
            if (!string.IsNullOrWhiteSpace(profileStr))
            {
                // Handle comma-separated or single profile string
                foreach (string p in profileStr.Split(',', ' '))
                {
                    string trimmed = p.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed)) { sb.Append(" "); sb.Append(trimmed); }
                }
                return true;
            }
        }
        return false;
    }

    /// Build the REPL command string for fleet batchconnect from a JSON profiles array.
    /// </summary>
    private static string? BuildBatchConnectCommand(JObject arguments)
    {
        JArray? profilesArray = arguments["profiles"] as JArray;
        if (profilesArray == null || profilesArray.Count == 0)
        {
            return null;
        }

        var profileNames = new System.Text.StringBuilder("fleet batchconnect");
        foreach (JToken token in profilesArray)
        {
            string? name = token.Value<string>();
            if (!string.IsNullOrWhiteSpace(name))
            {
                profileNames.Append(' ');
                profileNames.Append(name);
            }
        }

        return profileNames.ToString();
    }

    /// <summary>Generate the complete list of MCP tool definitions.</summary>

    #region Command Builder Helpers

    /// <summary>Build simple monitor subcommand with optional count arg.</summary>
    private static string BuildMonitorSimpleCommand(string subcommand, JObject arguments, string profileSuffix)
    {
        string? count = arguments["count"]?.Value<string>();
        if (!string.IsNullOrEmpty(count))
        {
            return $"monitor {subcommand} {count}{profileSuffix}";
        }

        return $"monitor {subcommand}{profileSuffix}";
    }

    /// <summary>
    /// Render the optional <c>order_type</c> MCP arg as a
    /// <c>--order-type &lt;type&gt;</c> flag for <c>orders close-by-tpsl</c>.
    /// Returns empty string when absent so back-compat callers keep the
    /// MARKET default.
    /// </summary>
    private static string BuildOrderTypeArg(JObject arguments)
    {
        string? ot = arguments["order_type"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(ot)) return "";
        string norm = ot.Trim().ToUpperInvariant();
        if (norm != "MARKET" && norm != "LIMIT") return "";
        return $" --order-type {norm}";
    }

    /// <summary>
    /// Render the REPL command line for
    /// <c>orders update-tpsl &lt;symbol&gt; &lt;side&gt; …</c>.  Mirrors the
    /// schema in ToolRegistry; tolerates missing optional fields by omitting
    /// the corresponding flag.  Numeric fields are forwarded as strings; the
    /// underlying command handler parses them with InvariantCulture.
    /// </summary>
    private static string BuildUpdateOrderTpslCommand(JObject arguments, string profileSuffix, string confirm)
    {
        string symbol = arguments["symbol"]?.Value<string>() ?? "";
        string side   = arguments["side"]?.Value<string>() ?? "";
        var parts = new List<string> { "orders update-tpsl", symbol, side };

        string? market = arguments["market"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(market))
        {
            string norm = market.Trim().ToUpperInvariant();
            if (norm == "FUTURES" || norm == "SPOT" || norm == "MARGIN" || norm == "DELIVERY")
                parts.Add($"--market {norm}");
        }

        string? positionSide = arguments["position_side"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(positionSide))
        {
            string norm = positionSide.Trim().ToUpperInvariant();
            if (norm == "BOTH" || norm == "LONG" || norm == "SHORT")
                parts.Add($"--position-side {norm}");
        }

        string? tp = arguments["take_profit_percent"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(tp)) parts.Add($"--tp {tp.Trim()}");

        string? sl = arguments["stop_loss_percent"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(sl)) parts.Add($"--sl {sl.Trim()}");

        string? trail = arguments["trailing_spread"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(trail)) parts.Add($"--trailing-spread {trail.Trim()}");

        string? coid = arguments["client_order_id"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(coid))
        {
            string safe = new string(coid.Where(c =>
                char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.').ToArray());
            if (safe.Length > 0 && safe.Length <= 64)
                parts.Add($"--client-order-id {safe}");
        }

        return string.Join(" ", parts) + profileSuffix + confirm;
    }

    /// <summary>
    /// Render TPSL IDs as a space-separated argument list for the underlying
    /// `tpsl join` REPL command. The MCP schema declares <c>tpsl_ids</c> as an
    /// array of string, but tolerate the legacy string form (space- or
    /// comma-separated) for backwards compatibility with earlier clients.
    /// </summary>
    private static string BuildTpslJoinIds(JObject arguments)
    {
        JToken? raw = arguments["tpsl_ids"];
        if (raw == null) return "";
        if (raw is JArray arr)
        {
            var parts = new List<string>(arr.Count);
            foreach (JToken t in arr)
            {
                string? s = t.Value<string>();
                if (!string.IsNullOrWhiteSpace(s)) parts.Add(s.Trim());
            }
            return string.Join(" ", parts);
        }
        // Legacy string form: split on whitespace or comma.
        string flat = raw.Value<string>() ?? "";
        var split = flat.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", split);
    }

    /// <summary>Build the REPL command for cross-server algo copy.</summary>
    private static string BuildCopyCommand(JObject arguments, string confirm)
    {
        string? id = arguments["id"]?.Value<string>() ?? "";
        string? source = arguments["source_profile"]?.Value<string>();
        string? dest = arguments["destination_profile"]?.Value<string>() ?? "";

        // @source is parsed by ExecuteAsync as the targetProfile (source server)
        // to:dest uses a different prefix so it passes through to CopyAlgo in cleanArgs
        string? sourceSuffix = source != null ? $" @{source}" : "";
        return $"algos copy {id} to:{dest}{sourceSuffix}{confirm}";
    }

    // ── Paste-from-clipboard / import-json helpers ──

    private static string BuildPasteFromClipboardCommand(JObject arguments, string confirm)
    {
        var parts = new List<string> { "algos paste-from-clipboard" };
        string? dest = arguments["destination_profile"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(dest)) parts.Add($"@{dest}");
        AppendOverrideFlags(parts, arguments);
        if (arguments["force"]?.Value<bool>() == true) parts.Add("--force");
        return string.Join(" ", parts) + confirm;
    }

    private static string BuildImportJsonCommand(JObject arguments, string confirm)
    {
        var parts = new List<string> { "algos import-json" };
        string? dest = arguments["destination_profile"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(dest)) parts.Add($"@{dest}");
        string? payload = arguments["payload"]?.Value<string>();
        // path argument overrides payload.  When the file exists,
        // read its content and inject as the payload.  Error markers are
        // embedded into the schema_version field so AlgosCommand's existing
        // schema_version_mismatch failure path carries the path/reason to
        // the caller without needing a new parser branch.
        string? path = arguments["path"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                if (!System.IO.File.Exists(path))
                    payload = "{\"schema_version\":\"path-not-found:" +
                              path.Replace("\\", "\\\\").Replace("\"", "\\\"") +
                              "\"}";
                else
                    payload = System.IO.File.ReadAllText(path);
            }
            catch (System.Exception ex)
            {
                payload = "{\"schema_version\":\"read-failed:" +
                          ex.Message.Replace("\\", "\\\\").Replace("\"", "\\\"") +
                          "\"}";
            }
        }
        if (!string.IsNullOrWhiteSpace(payload))
        {
            // Base64-encode the payload so the REPL splitter doesn't choke on spaces / quotes;
            // AlgosCommand.ImportFromJson decodes the --payload arg.
            string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));
            parts.Add($"--payload {b64}");
        }
        AppendOverrideFlags(parts, arguments);
        if (arguments["force"]?.Value<bool>() == true) parts.Add("--force");
        return string.Join(" ", parts) + confirm;
    }

    // fleet set-margin-type with mandatory dry_run.
    private static string BuildFleetSetMarginTypeCommand(JObject arguments, string confirm)
    {
        var parts = new List<string> { "fleet set-margin-type" };
        string? symbol = arguments["symbol"]?.Value<string>();
        string? margin = arguments["margin_type"]?.Value<string>()?.ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(symbol) && !symbol.Contains(' ')) parts.Add(symbol);
        if (margin == "CROSS" || margin == "ISOLATED") parts.Add(margin);
        string? market = arguments["market"]?.Value<string>();
        if (market is "SPOT" or "MARGIN" or "FUTURES" or "DELIVERY") parts.Add($"--market {market}");
        string? profiles = arguments["profiles"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(profiles) && !profiles.Contains(' ')) parts.Add($"--profiles {profiles}");
        string? exclude = arguments["exclude"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(exclude) && !exclude.Contains(' ')) parts.Add($"--exclude {exclude}");
        return string.Join(" ", parts) + confirm;
    }

    private static string BuildAlgosCreateCommand(JObject arguments, string profileSuffix, string confirm)
    {
        var parts = new List<string> { "algos create" };
        string? algoType = arguments["algo_type"]?.Value<string>()?.ToUpperInvariant();
        if (algoType is "SHOTS" or "AVERAGES" or "WATCHERS" or "SIGNALS" or "SAVER" or "DEPTHSHOTS" or "VECTOR")
            parts.Add($"--algo-type {algoType}");
        string? presetName = arguments["preset_name"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(presetName) && !presetName.Contains(' '))
            parts.Add($"--signature {presetName}");
        string? sourceId = arguments["source_algo_id"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(sourceId) && long.TryParse(sourceId, out _))
            parts.Add($"--source-id {sourceId}");
        string? newName = arguments["new_name"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(newName))
        {
            string cleanName = new string(newName.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.').ToArray());
            if (cleanName.Length > 0) parts.Add($"--new-name {cleanName}");
        }
        string? overrides = arguments["overrides_json"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(overrides))
        {
            string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(overrides));
            parts.Add($"--overrides {b64}");
        }
        if (arguments["force"]?.Value<bool>() == true) parts.Add("--force");
        if (arguments["no_dry_run"]?.Value<bool>() == true) parts.Add("--no-dry-run");
        return string.Join(" ", parts) + profileSuffix + confirm;
    }

    private static string BuildBulkEditCommand(JObject arguments, string profileSuffix, string confirm)
    {
        var parts = new List<string> { "algos bulk-edit" };
        string? filter = arguments["filter_json"]?.Value<string>();
        string? mutation = arguments["mutation_json"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(filter))
            parts.Add($"--filter {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(filter))}");
        if (!string.IsNullOrWhiteSpace(mutation))
            parts.Add($"--mutation {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(mutation))}");
        return string.Join(" ", parts) + profileSuffix + confirm;
    }

    private static void AppendOverrideFlags(List<string> parts, JObject arguments)
    {
        string? overrideSymbol = arguments["override_symbol"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(overrideSymbol) && !overrideSymbol.Contains(' '))
            parts.Add($"--override-symbol {overrideSymbol}");
        string? overrideMarket = arguments["override_market"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(overrideMarket) &&
            (overrideMarket == "SPOT" || overrideMarket == "MARGIN" ||
             overrideMarket == "FUTURES" || overrideMarket == "DELIVERY"))
            parts.Add($"--override-market {overrideMarket}");
    }


    private static string BuildCountArg(JObject arguments)
    {
        string? count = arguments["count"]?.Value<string>();
        return count != null ? $" --count {count}" : "";
    }

    // ── AutoStops balance-filter helpers ──

    private static string BuildAutoStopAddCommand(JObject arguments, string profileSuffix, string confirm)
    {
        var parts = new List<string> { "autostops add" };
        AppendFlag(parts, "--max-loss", arguments["max_loss"]?.Value<string>());
        AppendFlag(parts, "--info", arguments["info"]?.Value<string>());
        AppendFlagSanitised(parts, "--filter-type", arguments["filter_type"]?.Value<string>(), AutoStopFilterTypes);
        AppendFlagSanitised(parts, "--source-type", arguments["source_type"]?.Value<string>(), AutoStopSourceTypes);
        AppendFlagSanitised(parts, "--market", arguments["market"]?.Value<string>(), AutoStopMarkets);
        AppendFlag(parts, "--timeframe-ms", arguments["timeframe_ms"]?.Value<string>());
        AppendFlag(parts, "--symbols", arguments["symbols"]?.Value<string>(), allowSpaces: false);
        AppendFlag(parts, "--quotes", arguments["quotes"]?.Value<string>(), allowSpaces: false);
        AppendFlag(parts, "--asset", arguments["asset"]?.Value<string>(), allowSpaces: false);
        AppendFlag(parts, "--algorithm-comment", arguments["algorithm_comment"]?.Value<string>());
        AppendFlag(parts, "--report-comment", arguments["report_comment"]?.Value<string>());
        if (arguments["pause_algo"]?.Value<bool>() == true) parts.Add("--pause-algo");
        return string.Join(" ", parts) + profileSuffix + confirm;
    }

    private static string BuildAutoStopEditCommand(JObject arguments, string profileSuffix, string confirm)
    {
        var parts = new List<string> { "autostops edit" };
        string idx = arguments["index"]?.Value<string>() ?? "";
        if (!int.TryParse(idx, out _)) idx = "0";
        parts.Add(idx);
        AppendFlag(parts, "--max-loss", arguments["max_loss"]?.Value<string>());
        AppendFlag(parts, "--info", arguments["info"]?.Value<string>());
        AppendFlagSanitised(parts, "--filter-type", arguments["filter_type"]?.Value<string>(), AutoStopFilterTypes);
        AppendFlagSanitised(parts, "--source-type", arguments["source_type"]?.Value<string>(), AutoStopSourceTypes);
        AppendFlagSanitised(parts, "--market", arguments["market"]?.Value<string>(), AutoStopMarkets);
        AppendFlag(parts, "--timeframe-ms", arguments["timeframe_ms"]?.Value<string>());
        AppendFlag(parts, "--symbols", arguments["symbols"]?.Value<string>(), allowSpaces: false);
        AppendFlag(parts, "--quotes", arguments["quotes"]?.Value<string>(), allowSpaces: false);
        AppendFlag(parts, "--asset", arguments["asset"]?.Value<string>(), allowSpaces: false);
        AppendFlag(parts, "--algorithm-comment", arguments["algorithm_comment"]?.Value<string>());
        AppendFlag(parts, "--report-comment", arguments["report_comment"]?.Value<string>());
        if (arguments["pause_algo"]?.Value<bool>() == true) parts.Add("--pause-algo");
        if (arguments["no_pause_algo"]?.Value<bool>() == true) parts.Add("--no-pause-algo");
        if (arguments["enabled"] is JValue enVal && enVal.Type == JTokenType.Boolean)
            parts.Add($"--enabled {enVal.Value<bool>().ToString().ToLowerInvariant()}");
        return string.Join(" ", parts) + profileSuffix + confirm;
    }

    private static string BuildAutoStopToggleCommand(string verb, JObject arguments, string profileSuffix, string confirm)
    {
        string idx = arguments["index"]?.Value<string>() ?? "";
        string idxPart = (!string.IsNullOrWhiteSpace(idx) && int.TryParse(idx, out int _)) ? $" {idx}" : "";
        return $"autostops {verb}{idxPart}{profileSuffix}{confirm}";
    }

    private static string BuildAutoStopDeleteCommand(JObject arguments, string profileSuffix, string confirm)
    {
        string idx = arguments["index"]?.Value<string>() ?? "";
        if (!int.TryParse(idx, out _)) idx = "0";
        return $"autostops delete {idx}{profileSuffix}{confirm}";
    }

    private static readonly HashSet<string> AutoStopFilterTypes = new(StringComparer.OrdinalIgnoreCase)
        { "GLOBAL_BY_SYMBOL", "ALGO_SYMBOLS", "ALGO_TOTAL", "CUSTOM" };
    private static readonly HashSet<string> AutoStopSourceTypes = new(StringComparer.OrdinalIgnoreCase)
        { "VALUE", "PRICE_DELTA_SUM", "PROFIT_FACTOR" };
    private static readonly HashSet<string> AutoStopMarkets = new(StringComparer.OrdinalIgnoreCase)
        { "SPOT", "MARGIN", "FUTURES", "DELIVERY" };

    private static void AppendFlag(List<string> parts, string flag, string? value, bool allowSpaces = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!allowSpaces && value.Contains(' ')) return;
        parts.Add($"{flag} {value}");
    }

    private static void AppendFlagSanitised(List<string> parts, string flag, string? value, HashSet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!allowed.Contains(value)) return;
        parts.Add($"{flag} {value}");
    }

    // Funding rate + leverage brackets builders.
    private static string BuildExchangeFundingRateCommand(JObject arguments, string profileSuffix)
    {
        var parts = new List<string> { "exchange funding-rate" };
        string? sym = arguments["symbol"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(sym))
        {
            string clean = new string(sym.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
            if (clean.Length > 0) parts.Add(clean);
        }
        string? mkt = arguments["market_type"]?.Value<string>();
        if (mkt is "SPOT" or "MARGIN" or "FUTURES" or "DELIVERY") parts.Add(mkt);
        return string.Join(" ", parts) + profileSuffix;
    }

    private static string BuildExchangeLeverageBracketsCommand(JObject arguments, string profileSuffix)
    {
        var parts = new List<string> { "exchange leverage-brackets" };
        string? sym = arguments["symbol"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(sym))
        {
            string clean = new string(sym.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
            if (clean.Length > 0) parts.Add(clean);
        }
        string? mkt = arguments["market_type"]?.Value<string>();
        if (mkt is "SPOT" or "MARGIN" or "FUTURES" or "DELIVERY") parts.Add(mkt);
        return string.Join(" ", parts) + profileSuffix;
    }

    // mt_profile_settings_update: base64-encode the JSON updates payload so
    // the REPL tokenizer doesn't strip its double-quotes (without quotes, JSON
    // keys with dots like "Core.LOG_LEVEL" parse as unquoted JS identifiers,
    // which Newtonsoft rejects).  Same pattern already used for inline-JSON
    // tools.
    private static string BuildProfileSettingsUpdateCommand(JObject arguments, string profileSuffix, string confirm)
    {
        string profileName = arguments["profile_name"]?.Value<string>() ?? "";
        string updates = arguments["updates_json"]?.Value<string>() ?? "";
        string encoded = string.IsNullOrEmpty(updates)
            ? ""
            : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(updates));
        return $"settings profile-update {profileName} {encoded}{profileSuffix}{confirm}";
    }

    // profile_settings list + delete builders.
    private static string BuildProfileSettingsListCommand(JObject arguments, string profileSuffix)
    {
        var parts = new List<string> { "settings profile-list" };
        string? grep = arguments["grep"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(grep))
        {
            // Sanitise: only alnum + dot + underscore + dash (typical setting key shape).
            string clean = new string(grep.Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-').ToArray());
            if (clean.Length > 0) parts.Add($"--grep {clean}");
        }
        return string.Join(" ", parts) + profileSuffix;
    }

    private static string BuildProfileSettingsDeleteCommand(JObject arguments, string profileSuffix, string confirm)
    {
        string? keys = arguments["keys"]?.Value<string>();
        // CSV-of-keys; allow alnum + dot + underscore + dash + comma.
        string clean = string.IsNullOrEmpty(keys)
            ? ""
            : new string(keys.Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == ',').ToArray());
        return $"settings profile-delete {clean}{profileSuffix}{confirm}";
    }

    // profiles.json command builders.
    private static string BuildProfilesAddCommand(JObject arguments, string confirm)
    {
        string name = SanitiseToken(arguments["name"]?.Value<string>());
        string address = SanitiseToken(arguments["address"]?.Value<string>(), allowDot: true);
        string port = SanitiseToken(arguments["port"]?.Value<string>());
        string token = SanitiseToken(arguments["token"]?.Value<string>());
        string exchange = SanitiseToken(arguments["exchange"]?.Value<string>());
        string folder = SanitiseToken(arguments["folder"]?.Value<string>());
        var parts = new List<string> { "profiles add", name, address, port, token, exchange };
        if (!string.IsNullOrEmpty(folder)) parts.Add(folder);
        return string.Join(" ", parts) + confirm;
    }

    private static string BuildProfilesEditCommand(JObject arguments, string confirm)
    {
        var parts = new List<string> { "profiles edit", SanitiseToken(arguments["name"]?.Value<string>()) };
        void Add(string key, string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return;
            string clean = SanitiseToken(val, allowDot: true);
            if (!string.IsNullOrEmpty(clean)) parts.Add($"--{key}={clean}");
        }
        Add("address", arguments["address"]?.Value<string>());
        Add("port", arguments["port"]?.Value<string>());
        Add("token", arguments["token"]?.Value<string>());
        Add("exchange", arguments["exchange"]?.Value<string>());
        Add("folder", arguments["folder"]?.Value<string>());
        Add("rename", arguments["rename"]?.Value<string>());
        return string.Join(" ", parts) + confirm;
    }

    private static string SanitiseToken(string? s, bool allowDot = false)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return new string(s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || (allowDot && c == '.')).ToArray());
    }

    private static string SanitisePath(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Allow alnum, dash, underscore, dot, slash for filesystem paths.
        return new string(s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == '/').ToArray());
    }

    // Whitelist CRUD command builder.  Mirrors BlackList shape:
    // typed entries {MarketType, QuoteAsset, Symbol?}.  bulk-variants take CSV.
    private static string BuildWhitelistMutationCommand(string verb, JObject arguments, string profileSuffix, string confirm, bool bulk)
    {
        string type = arguments["type"]?.Value<string>()?.ToLowerInvariant() ?? "symbol";
        if (type != "symbol" && type != "quote") type = "symbol";
        string market = arguments["market"]?.Value<string>() ?? "FUTURES";
        if (market is not ("SPOT" or "MARGIN" or "FUTURES" or "DELIVERY")) market = "FUTURES";
        string quote = arguments["quote"]?.Value<string>() ?? "";
        string subcmd = $"{verb}-{type}";
        // CLI form:
        //   whitelist add-symbol      <market> <quote> <symbol>
        //   whitelist bulk-add-symbol <market> <quote> <sym,sym,...>
        //   whitelist add-quote       <market> <quote>
        //   whitelist bulk-add-quote  <market> <quote,quote,...>
        var parts = new List<string> { "whitelist", subcmd, market };
        var clean = (string s) => new string(s.Where(c => char.IsLetterOrDigit(c) || c == ',' || c == '-' || c == '_').ToArray());
        if (type == "symbol")
        {
            parts.Add(clean(quote));
            string symbols = (bulk ? arguments["symbols"]?.Value<string>() : arguments["symbol"]?.Value<string>()) ?? "";
            parts.Add(clean(symbols));
        }
        else
        {
            string quotes = bulk ? (arguments["quotes"]?.Value<string>() ?? "") : quote;
            parts.Add(clean(quotes));
        }
        return string.Join(" ", parts) + profileSuffix + confirm;
    }

    private static string BuildBlacklistMutationCommand(string action, JObject arguments, string profileSuffix, string confirm)
    {
        string type = arguments["type"]?.Value<string>()?.ToLowerInvariant() ?? "symbol";
        string marketType = arguments["market_type"]?.Value<string>() ?? "";
        string quoteAsset = arguments["quote_asset"]?.Value<string>() ?? "";
        string symbol = arguments["symbol"]?.Value<string>() ?? "";

        string args = type switch
        {
            "market" => marketType,
            "quote"  => $"{marketType} {quoteAsset}".Trim(),
            "symbol" => $"{marketType} {quoteAsset} {symbol}".Trim(),
            _        => $"{marketType} {quoteAsset} {symbol}".Trim()
        };

        return $"blacklist {action}-{type} {args}{profileSuffix}{confirm}";
    }

    private static string BuildReportsCommand(JObject arguments, string profileSuffix)
    {
        var parts = new System.Collections.Generic.List<string> { "reports" };

        // Period shortcut (today/24h/7d/30d/90d)
        string? period = arguments["period"]?.Value<string>();
        string? fromDate = arguments["from"]?.Value<string>();
        string? toDate = arguments["to"]?.Value<string>();

        // Custom date range takes priority over period
        if (!string.IsNullOrEmpty(fromDate))
        {
            parts.Add($"--from {fromDate}");
        }
        else if (!string.IsNullOrEmpty(period))
        {
            parts.Add(period);
        }

        if (!string.IsNullOrEmpty(toDate))
        {
            parts.Add($"--to {toDate}");
        }

        // Filters
        string? symbol = arguments["symbol"]?.Value<string>();
        if (!string.IsNullOrEmpty(symbol))
        {
            parts.Add($"--symbol {symbol}");
        }

        string? algo = arguments["algo"]?.Value<string>();
        if (!string.IsNullOrEmpty(algo))
        {
            parts.Add($"--algo {algo}");
        }

        string? sig = arguments["sig"]?.Value<string>();
        if (!string.IsNullOrEmpty(sig))
        {
            parts.Add($"--sig {sig}");
        }

        // Metrics flag
        bool metrics = arguments["metrics"]?.Value<bool?>() ?? false;
        if (metrics)
        {
            parts.Add("--metrics");
        }


        // B6: Extended filters
        bool excludeEmulated = arguments["exclude_emulated"]?.Value<bool?>() ?? false;
        if (excludeEmulated)
        {
            parts.Add("--exclude-emulated");
        }

        string? closedBy = arguments["closed_by"]?.Value<string>();
        if (!string.IsNullOrEmpty(closedBy))
        {
            parts.Add($"--closed-by {closedBy}");
        }

        string? market = arguments["market"]?.Value<string>();
        if (!string.IsNullOrEmpty(market))
        {
            parts.Add($"--market {market}");
        }

        string? side = arguments["side"]?.Value<string>();
        if (!string.IsNullOrEmpty(side))
        {
            parts.Add($"--side {side}");
        }

        string? mode = arguments["mode"]?.Value<string>();
        if (!string.IsNullOrEmpty(mode))
        {
            parts.Add($"--mode {mode}");
        }

        return string.Join(" ", parts) + profileSuffix;
    }

    private static string BuildReportsExportCommand(JObject arguments, string profileSuffix)
    {
        var parts = new System.Collections.Generic.List<string> { "reports", "export" };

        string? period = arguments["period"]?.Value<string>();
        if (!string.IsNullOrEmpty(period))
        {
            parts.Add(period);
        }

        string? symbol = arguments["symbol"]?.Value<string>();
        if (!string.IsNullOrEmpty(symbol))
        {
            parts.Add($"--symbol {symbol}");
        }

        string? algo = arguments["algo"]?.Value<string>();
        if (!string.IsNullOrEmpty(algo))
        {
            parts.Add($"--algo {algo}");
        }

        string? sig = arguments["sig"]?.Value<string>();
        if (!string.IsNullOrEmpty(sig))
        {
            parts.Add($"--sig {sig}");
        }

        string? path = arguments["path"]?.Value<string>();
        if (!string.IsNullOrEmpty(path))
        {
            parts.Add($"--path {path}");
        }

        return string.Join(" ", parts) + profileSuffix;
    }

    private static string BuildReportsFleetExportCommand(JObject arguments)
    {
        var parts = new System.Collections.Generic.List<string> { "reports", "export", "--all" };

        string? period = arguments["period"]?.Value<string>();
        if (!string.IsNullOrEmpty(period))
        {
            parts.Add(period);
        }

        string? symbol = arguments["symbol"]?.Value<string>();
        if (!string.IsNullOrEmpty(symbol))
        {
            parts.Add($"--symbol {symbol}");
        }

        string? path = arguments["path"]?.Value<string>();
        if (!string.IsNullOrEmpty(path))
        {
            parts.Add($"--path {path}");
        }

        return string.Join(" ", parts);
    }

    private static string BuildReportsStoreCommand(JObject arguments, string profileSuffix)
    {
        string? name = arguments["name"]?.Value<string>() ?? "unnamed";
        var parts = new System.Collections.Generic.List<string> { "reports", "store", name };

        string? allServers = arguments["all_servers"]?.Value<string>();
        if (!string.IsNullOrEmpty(allServers) &&
            string.Equals(allServers, "true", System.StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("--all");
            profileSuffix = "";  // Ignore profile when querying all
        }

        string? period = arguments["period"]?.Value<string>();
        if (!string.IsNullOrEmpty(period))
        {
            parts.Add(period);
        }

        string? symbol = arguments["symbol"]?.Value<string>();
        if (!string.IsNullOrEmpty(symbol))
        {
            parts.Add($"--symbol {symbol}");
        }

        return string.Join(" ", parts) + profileSuffix;
    }


    private static string BuildKlinesCommand(JObject arguments, string profileSuffix)
    {
        string? symbol = arguments["symbol"]!.Value<string>();
        string? interval = arguments["interval"]?.Value<string>() ?? "1h";
        string? limit = arguments["limit"]?.Value<string>() ?? "100";
        string marketSuffix = "";
        string? marketArg = arguments["market"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(marketArg))
        {
            string norm = marketArg.Trim().ToUpperInvariant();
            if (norm == "FUTURES" || norm == "SPOT" || norm == "MARGIN" || norm == "DELIVERY")
                marketSuffix = $" --market {norm}";
        }
        return $"exchange klines {symbol} {interval} {limit}{marketSuffix}" + profileSuffix;
    }

    private static string? BuildPlaceOrderCommand(JObject arguments, string profileSuffix, string confirm)
    {
        // Defense in depth — the required-args gate already rejects this
        // path, but never NRE here even if a future code path skips the gate.
        string? symbol = arguments["symbol"]?.Value<string>();
        string? side   = arguments["side"]?.Value<string>();
        string? qty    = arguments["qty"]?.Value<string>();
        if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(side) || string.IsNullOrEmpty(qty))
            return null;
        var parts = new List<string> { "orders place", symbol, side, qty };

        string? price = arguments["price"]?.Value<string>();
        if (!string.IsNullOrEmpty(price))
        {
            parts.Add(price);
        }

        string? orderType = arguments["type"]?.Value<string>();
        if (!string.IsNullOrEmpty(orderType))
        {
            parts.Add($"--type {orderType}");
        }

        bool reduceOnly = arguments["reduce_only"]?.Value<bool>() ?? false;
        if (reduceOnly)
        {
            parts.Add("--reduce-only");
        }

        string? positionSide = arguments["position_side"]?.Value<string>();
        if (!string.IsNullOrEmpty(positionSide))
        {
            parts.Add($"--position-side {positionSide}");
        }

        bool emulated = arguments["emulated"]?.Value<bool>() ?? false;
        if (emulated)
        {
            parts.Add("--emulated");
        }

        // Iceberg toggle (wires to OrderSettings.isIceberg).
        bool iceberg = arguments["iceberg"]?.Value<bool>() ?? false;
        if (iceberg)
        {
            parts.Add("--iceberg");
        }

        string? marketArg = arguments["market"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(marketArg))
        {
            string norm = marketArg.Trim().ToUpperInvariant();
            if (norm == "FUTURES" || norm == "SPOT" || norm == "MARGIN" || norm == "DELIVERY")
                parts.Add($"--market {norm}");
        }

        string? coid = arguments["client_order_id"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(coid))
        {
            // Reject any whitespace / newline / shell metachar; only allow id-safe runs.
            // ConfirmGate / RequestSanitizer already strips \r\n upstream, but defence in depth.
            string safe = new string(coid.Where(c =>
                char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.').ToArray());
            if (safe.Length > 0 && safe.Length <= 64)
                parts.Add($"--client-order-id {safe}");
        }

        // TPSL inline params — attached to OrderRequestData.takeProfitSettings /
        // stopLossSettings on placement. The safe pattern: place + TPSL in one
        // wire call. See docs/TPSL_SAFETY_GUIDE.md.
        string? tpPercent = arguments["tp_percent"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(tpPercent))
        {
            parts.Add($"--tp-percent {SanitizeNumeric(tpPercent)}");
            string? tpType = arguments["tp_type"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(tpType))
            {
                string norm = tpType.Trim().ToUpperInvariant();
                if (norm == "LIMIT" || norm == "MARKET")
                    parts.Add($"--tp-type {norm}");
            }
        }
        string? slPercent = arguments["sl_percent"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(slPercent))
        {
            parts.Add($"--sl-percent {SanitizeNumeric(slPercent)}");
            string? slType = arguments["sl_type"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(slType))
            {
                string norm = slType.Trim().ToUpperInvariant();
                if (norm == "LIMIT" || norm == "MARKET")
                    parts.Add($"--sl-type {norm}");
            }
            bool trailing = arguments["trailing_stop"]?.Value<bool>() ?? false;
            if (trailing)
            {
                parts.Add("--trailing-stop");
                string? trailSpread = arguments["trailing_spread"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(trailSpread))
                    parts.Add($"--trailing-spread {SanitizeNumeric(trailSpread)}");
            }
        }

        return string.Join(" ", parts) + profileSuffix + confirm;
    }

    /// <summary>Strip non-numeric characters so the CLI fragment can't be
    /// hijacked through whitespace or shell metacharacters embedded in a
    /// numeric MCP argument. Allows digits, '.', and a single leading '-'.</summary>
    private static string SanitizeNumeric(string s)
    {
        var sb = new System.Text.StringBuilder();
        bool sawDot = false;
        for (int i = 0; i < s.Length && sb.Length < 16; i++)
        {
            char c = s[i];
            if (char.IsDigit(c)) sb.Append(c);
            else if (c == '.' && !sawDot) { sb.Append(c); sawDot = true; }
            else if (c == '-' && sb.Length == 0) sb.Append(c);
        }
        return sb.Length == 0 ? "0" : sb.ToString();
    }
    #endregion

    #region JSON-RPC Helpers

    private static JObject MakeResult(JToken? id, JObject result)
    {
        return new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };
    }

    private static JObject MakeErrorResponse(JToken? id, int code, string message)
    {
        return new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
    }

    private void WriteStdout(JObject response)
    {
        string? json = response.ToString(Formatting.None);
        _stdoutWriter.WriteLine(json);
        _stdoutWriter.Flush();
    }

    // ── Event streaming tool handler ────────────────────────────────────────

    private JObject? HandleEventTool(string toolName, JObject arguments)
    {
        return toolName switch
        {
            "mt_events_poll" => HandleEventsPoll(arguments),
            "mt_events_status" => HandleEventsStatus(),
            _ => null
        };
    }

    private JObject HandleEventsPoll(JObject arguments)
    {
        long sinceSeq = arguments["since_seq"]?.Value<long>() ?? 0L;
        int n = arguments["n"]?.Value<int>() ?? 50;

        var events = sinceSeq > 0
            ? _events.GetSince(sinceSeq)
            : _events.GetLast(n);

        return new JObject
        {
            ["events"] = new Newtonsoft.Json.Linq.JArray(
                events.Select(e => Newtonsoft.Json.Linq.JToken.FromObject(e))
                      .ToArray()),
            ["current_seq"] = _events.CurrentSeq,
            ["count"] = events.Count
        };
    }

    private JObject HandleEventsStatus()
    {
        return new JObject
        {
            ["current_seq"] = _events.CurrentSeq,
            ["sse_port"] = 8587,
            ["sse_url"] = "http://localhost:8587/events",
            ["poll_url"] = "http://localhost:8587/events/poll",
            ["status"] = "ok"
        };
    }


    // ── Internal multi-step tools ───────────────────────────────────────────

    private JObject? HandleInternalTool(string toolName, JObject arguments)
    {
        return toolName switch
        {
            "mt_metrics_get"        => HandleMetricsGet(),
            "mt_config_snapshot"    => HandleConfigSnapshot(arguments),
            "mt_config_restore"     => HandleConfigRestore(arguments),
            "mt_settings_diff"      => HandleSettingsDiff(arguments),
            "mt_settings_diff_snapshots" => HandleSettingsDiffSnapshots(arguments),
            "mt_rate_status"        => HandleRateStatus(),
            "mt_vault_store_profile" => HandleVaultStoreProfile(arguments),
            "mt_vault_list_profiles" => HandleVaultListProfiles(arguments),
            "mt_vault_get_profile"   => HandleVaultGetProfile(arguments),
            "mt_vault_delete_profile" => HandleVaultDeleteProfile(arguments),
            "mt_notifications_config_groups"        => Core.NotificationConfigReflector.GroupsCatalog(),
            "mt_notifications_config_targets"       => Core.NotificationConfigReflector.TargetsCatalog(),
            "mt_notifications_config_descriptors"   => Core.NotificationConfigReflector.DescriptorsCatalog(),
            "mt_notifications_config_capabilities"  => Core.NotificationConfigReflector.CapabilitiesCatalog(),
            "mt_alerts_save"                        => HandleAlertsSave(arguments),
            "mt_alerts_delete"                      => HandleAlertsDelete(arguments),
            "mt_alerts_set_running"                 => HandleAlertsSetRunning(arguments),
            "mt_import_from_profile"                => HandleImportFromProfile(arguments),
            "mt_reports_query"                      => HandleReportsQuery(arguments, asCsv: false),
            "mt_reports_csv_inline"                 => HandleReportsQuery(arguments, asCsv: true),
            "mt_reports_cancel"                     => HandleReportsCancel(arguments),
            "mt_reports_status"                     => HandleReportsStatus(arguments),
            "mt_core_shutdown"       => HandleCoreShutdown(arguments),
            "mt_algos_tpsl_change"   => HandleAlgosTpslChange(arguments),
            "mt_algos_profiling"     => HandleAlgosProfiling(arguments),
            "mt_config_import_algos" => HandleConfigImportAlgos(arguments),      // Direct JSON import
            "mt_algos_snapshot"      => HandleAlgosSnapshot(arguments),         // State reconciliation
            "mt_algos_group_by_name" => HandleAlgosGroupByName(arguments),      // State reconciliation
            _ => null
        };
    }

    // Return metrics as JSON
    private JObject HandleMetricsGet() => _metrics.ToJson();

    // Return rate limit status
    private JObject HandleRateStatus() => _rateLimits.GetStatus();

    // Store an API profile in HashiCorp Vault
    private JObject HandleVaultStoreProfile(JObject arguments)
    {
        string? name      = arguments["name"]?.Value<string>();
        string? apiKey    = arguments["api_key"]?.Value<string>();
        string? apiSecret = arguments["api_secret"]?.Value<string>();
        string? vaultAddr = arguments["vault_addr"]?.Value<string>() ?? Environment.GetEnvironmentVariable("VAULT_ADDR") ?? "http://127.0.0.1:8200";
        string? vaultToken = arguments["vault_token"]?.Value<string>() ?? Environment.GetEnvironmentVariable("VAULT_TOKEN") ?? "";

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
            return new JObject { ["error"] = "name, api_key, and api_secret are required" };

        try
        {
            string url = $"{vaultAddr.TrimEnd('/')}/v1/secret/data/mt/profiles/{name}";
            var payload = new JObject
            {
                ["data"] = new JObject
                {
                    ["api_key"]    = apiKey,
                    ["api_secret"] = apiSecret,
                    ["stored_at"]  = DateTime.UtcNow.ToString("o"),
                }
            };
            var content = new System.Net.Http.StringContent(
                payload.ToString(Formatting.None),
                System.Text.Encoding.UTF8,
                "application/json");

            using var http = BuildVaultHttpClient(vaultToken);
            var resp = http.PostAsync(url, content).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return new JObject { ["error"] = $"Vault HTTP {(int)resp.StatusCode}: {body}" };
            }
            return new JObject { ["status"] = "ok", ["profile"] = name };
        }
        catch (Exception ex)
        {
            return new JObject { ["error"] = ex.Message };
        }
    }

    // List Vault profiles
    private JObject HandleVaultListProfiles(JObject arguments)
    {
        string vaultAddr  = arguments["vault_addr"]?.Value<string>()  ?? Environment.GetEnvironmentVariable("VAULT_ADDR") ?? "http://127.0.0.1:8200";
        string vaultToken = arguments["vault_token"]?.Value<string>() ?? Environment.GetEnvironmentVariable("VAULT_TOKEN") ?? "";

        try
        {
            string url = $"{vaultAddr.TrimEnd('/')}/v1/secret/metadata/mt/profiles/";
            var reqMsg = new System.Net.Http.HttpRequestMessage(
                new System.Net.Http.HttpMethod("LIST"), url);

            using var http = BuildVaultHttpClient(vaultToken);
            var resp = http.SendAsync(reqMsg).GetAwaiter().GetResult();
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new JObject { ["profiles"] = new JArray() };

            if (!resp.IsSuccessStatusCode)
                return new JObject { ["error"] = $"Vault HTTP {(int)resp.StatusCode}: {body}" };

            var parsed  = JObject.Parse(body);
            var keys    = parsed["data"]?["keys"] as JArray ?? new JArray();
            return new JObject { ["profiles"] = keys, ["count"] = keys.Count };
        }
        catch (Exception ex)
        {
            return new JObject { ["error"] = ex.Message };
        }
    }

    // Retrieve a stored API profile from Vault KV v2.
    //   GET /v1/secret/data/mt/profiles/{name} → { data: { data: {api_key, api_secret, stored_at}, metadata: ... } }
    // Returns: { name, api_key, api_secret, stored_at, version }.  api_secret
    // is surfaced in cleartext — Vault is the secret store; the caller's
    // responsibility is to handle the response securely.
    private JObject HandleVaultGetProfile(JObject arguments)
    {
        string? name      = arguments["name"]?.Value<string>();
        string vaultAddr  = arguments["vault_addr"]?.Value<string>()  ?? Environment.GetEnvironmentVariable("VAULT_ADDR") ?? "http://127.0.0.1:8200";
        string vaultToken = arguments["vault_token"]?.Value<string>() ?? Environment.GetEnvironmentVariable("VAULT_TOKEN") ?? "";

        if (string.IsNullOrEmpty(name))
            return new JObject { ["error"] = "name is required" };

        try
        {
            string url = $"{vaultAddr.TrimEnd('/')}/v1/secret/data/mt/profiles/{name}";
            using var http = BuildVaultHttpClient(vaultToken);
            var resp = http.GetAsync(url).GetAwaiter().GetResult();
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new JObject { ["error"] = $"profile_not_found: '{name}' has no record in Vault at secret/mt/profiles/{name}" };
            if (!resp.IsSuccessStatusCode)
                return new JObject { ["error"] = $"Vault HTTP {(int)resp.StatusCode}: {body}" };

            var parsed = JObject.Parse(body);
            var data   = parsed["data"]?["data"] as JObject;
            var meta   = parsed["data"]?["metadata"] as JObject;
            if (data == null)
                return new JObject { ["error"] = $"profile_present_but_empty: '{name}' returned no data envelope" };

            return new JObject
            {
                ["name"]       = name,
                ["api_key"]    = data["api_key"],
                ["api_secret"] = data["api_secret"],
                ["stored_at"]  = data["stored_at"],
                ["version"]    = meta?["version"],
            };
        }
        catch (Exception ex)
        {
            return new JObject { ["error"] = ex.Message };
        }
    }

    // Permanently delete an API profile via Vault KV v2 destroy-all-versions.
    //   DELETE /v1/secret/metadata/mt/profiles/{name}   (removes versions + metadata)
    // Requires confirm=true (also enforced by ConfirmGate at the registry layer).
    private JObject HandleVaultDeleteProfile(JObject arguments)
    {
        string? name      = arguments["name"]?.Value<string>();
        bool confirm      = arguments["confirm"]?.Value<bool>() ?? false;
        string vaultAddr  = arguments["vault_addr"]?.Value<string>()  ?? Environment.GetEnvironmentVariable("VAULT_ADDR") ?? "http://127.0.0.1:8200";
        string vaultToken = arguments["vault_token"]?.Value<string>() ?? Environment.GetEnvironmentVariable("VAULT_TOKEN") ?? "";

        if (string.IsNullOrEmpty(name))
            return new JObject { ["error"] = "name is required" };
        if (!confirm)
            return new JObject { ["error"] = "confirm=true is required to permanently destroy the Vault entry" };

        try
        {
            string url = $"{vaultAddr.TrimEnd('/')}/v1/secret/metadata/mt/profiles/{name}";
            using var http = BuildVaultHttpClient(vaultToken);
            var resp = http.DeleteAsync(url).GetAwaiter().GetResult();
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new JObject { ["status"] = "not_found", ["profile"] = name };
            if (!resp.IsSuccessStatusCode)
                return new JObject { ["error"] = $"Vault HTTP {(int)resp.StatusCode}: {body}" };

            return new JObject { ["status"] = "deleted", ["profile"] = name };
        }
        catch (Exception ex)
        {
            return new JObject { ["error"] = ex.Message };
        }
    }

    // Automation-friendly trade-reports query.  Returns either a
    // structured rows envelope or a CSV string in the body, with the same
    // rich-filter surface as mt_reports_trades (which only returns text).
    private JObject HandleReportsQuery(JObject arguments, bool asCsv)
    {
        string? profile = arguments["profile"]?.Value<string>();
        var conn = _manager.Resolve(profile);
        if (conn == null)
            return new JObject { ["error"] = $"no_connection: profile '{profile ?? "(default)"}' is not connected" };

        // ── Date range.
        string period = arguments["period"]?.Value<string>() ?? "24h";
        string? from = arguments["from"]?.Value<string>();
        string? to   = arguments["to"]?.Value<string>();
        (long unixFrom, long unixTo, string rangeLabel) = ResolveDateRange(period, from, to);

        // ── Optional filters.
        string symbol     = arguments["symbol"]?.Value<string>() ?? "";
        string algo       = arguments["algo"]?.Value<string>() ?? "";
        string sig        = arguments["sig"]?.Value<string>() ?? "";
        bool excludeEmu   = arguments["exclude_emulated"]?.Value<bool>() ?? false;
        var closedBy      = ParseEnumList<MTShared.Types.ReportClosedByType>(arguments["closed_by"]?.Value<string>());
        var marketTypes   = ParseEnumList<MTShared.Types.MarketType>(arguments["market"]?.Value<string>());
        var orderSides    = ParseEnumList<MTShared.Types.OrderSideType>(arguments["side"]?.Value<string>());
        var tradeMode     = ParseTradeMode(arguments["mode"]?.Value<string>());
        int maxRows       = Math.Clamp(arguments["max_rows"]?.Value<int>() ?? 200, 1, 5000);

        string filterSummary =
            $"range={rangeLabel} sym={symbol} algo={algo} sig={sig} closedBy={closedBy.Count} " +
            $"market={marketTypes.Count} side={orderSides.Count} mode={tradeMode} excludeEmu={excludeEmu} maxRows={maxRows}";

        var entry = Core.ReportsRequestRegistry.Begin(profile ?? "(default)", filterSummary);

        MTShared.Network.ReportListData? reportList;
        try
        {
            reportList = conn.RequestReports(
                unixFrom, unixTo, symbol, algo, sig,
                includeMetrics: false, excludeEmulated: excludeEmu,
                closedBy: closedBy.Count > 0 ? closedBy : null,
                marketTypes: marketTypes.Count > 0 ? marketTypes : null,
                orderSideTypes: orderSides.Count > 0 ? orderSides : null,
                tradeModeType: tradeMode);
        }
        catch (Exception ex)
        {
            Core.ReportsRequestRegistry.Error(entry.RequestId, ex.Message);
            return new JObject
            {
                ["error"] = $"reports_query_failed: {ex.Message}",
                ["request_id"] = entry.RequestId,
            };
        }

        if (reportList == null)
        {
            Core.ReportsRequestRegistry.Error(entry.RequestId, "wire_returned_null");
            return new JObject
            {
                ["error"] = "reports_query_failed: MTCore did not respond on this profile. " +
                            "Some MTCore builds (observed on freshly-initialised BYBIT bench) drop " +
                            "ReportListRequest without firing a callback or push notification. " +
                            "Fall back to: mt_reports_dates (lists available dates), " +
                            "mt_account_executions (live fill stream), or mt_marketdata_trades_subscribe " +
                            "for symbol-level trade history. Phase 4 (real-order placement) will populate " +
                            "this profile's Firebird DB so future ReportListRequest calls succeed.",
                ["request_id"] = entry.RequestId,
            };
        }

        var reports = reportList.reports ?? new List<MTShared.Network.ReportData>();
        int totalRows = reports.Count;
        int includedRows = Math.Min(totalRows, maxRows);

        if (asCsv)
        {
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("id,reportOpenTime,reportTime,marketType,symbol,side,priceOpen,priceClose,qty,executedQty,profit,profitPercentage,commissionUSDT,profitUSDT,totalUSDT,closedBy,isEmulated");
            for (int i = 0; i < includedRows; i++)
            {
                var r = reports[i];
                csv.Append(r.id).Append(',')
                   .Append(r.reportOpenTime).Append(',')
                   .Append(r.reportTime).Append(',')
                   .Append(r.marketType).Append(',')
                   .Append(CsvEscape(r.symbol)).Append(',')
                   .Append(r.orderSideType).Append(',')
                   .Append(r.priceOpen.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                   .Append(r.priceClose.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                   .Append(r.qty.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                   .Append(r.executedQty.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                   .Append(r.profit.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                   .Append(r.profitPercentage.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                   .Append(r.commissionUSDT.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                   .Append(r.profitUSDT.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                   .Append(r.totalUSDT.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                   .Append(r.closedBy).Append(',')
                   .Append(r.isEmulated ? "true" : "false")
                   .AppendLine();
            }
            Core.ReportsRequestRegistry.Complete(entry.RequestId, includedRows);
            return new JObject
            {
                ["request_id"] = entry.RequestId,
                ["row_count"] = includedRows,
                ["total_rows_on_wire"] = totalRows,
                ["truncated"] = totalRows > includedRows,
                ["range"] = rangeLabel,
                ["csv"] = csv.ToString(),
            };
        }

        var rows = new JArray();
        for (int i = 0; i < includedRows; i++)
        {
            var r = reports[i];
            rows.Add(new JObject
            {
                ["id"]                = r.id,
                ["report_open_time"]  = r.reportOpenTime,
                ["report_time"]       = r.reportTime,
                ["market_type"]       = r.marketType.ToString(),
                ["symbol"]            = r.symbol,
                ["side"]              = r.orderSideType.ToString(),
                ["price_open"]        = r.priceOpen,
                ["price_close"]       = r.priceClose,
                ["qty"]               = r.qty,
                ["executed_qty"]      = r.executedQty,
                ["profit"]            = r.profit,
                ["profit_percentage"] = r.profitPercentage,
                ["commission_usdt"]   = r.commissionUSDT,
                ["profit_usdt"]       = r.profitUSDT,
                ["total_usdt"]        = r.totalUSDT,
                ["executed_qty_usdt"] = r.executedQtyUSDT,
                ["closed_by"]         = r.closedBy.ToString(),
                ["is_emulated"]       = r.isEmulated,
            });
        }
        Core.ReportsRequestRegistry.Complete(entry.RequestId, includedRows);
        return new JObject
        {
            ["request_id"] = entry.RequestId,
            ["row_count"] = includedRows,
            ["total_rows_on_wire"] = totalRows,
            ["truncated"] = totalRows > includedRows,
            ["range"] = rangeLabel,
            ["filter_summary"] = filterSummary,
            ["summary"] = new JObject
            {
                ["total"]        = reportList.total,
                ["order_count"]  = reportList.orderCount,
                ["deleted_count"] = reportList.deletedCount,
            },
            ["rows"] = rows,
        };
    }

    private JObject HandleReportsCancel(JObject arguments)
    {
        string? requestId = arguments["request_id"]?.Value<string>();
        if (string.IsNullOrEmpty(requestId))
            return new JObject { ["error"] = "request_id is required" };

        var entry = Core.ReportsRequestRegistry.Get(requestId);
        if (entry == null)
            return new JObject
            {
                ["status"] = "not_found",
                ["request_id"] = requestId,
                ["notice"] = "request_id_not_found: no recent reports query matches this id",
            };

        Core.ReportsRequestRegistry.RequestCancel(requestId);
        return new JObject
        {
            ["status"] = entry.Status == "completed" ? "already_completed" : "cancel_requested",
            ["request_id"] = requestId,
            ["wire_is_synchronous"] = true,
            ["notice"] = "reports_cancel_acknowledged: the SendReportListRequest wire on this build " +
                         "blocks until completion or timeout; cancellation intent is recorded for " +
                         "observability but does not interrupt the in-flight RPC.",
        };
    }

    private JObject HandleReportsStatus(JObject arguments)
    {
        string? requestId = arguments["request_id"]?.Value<string>();
        if (string.IsNullOrEmpty(requestId))
            return new JObject { ["error"] = "request_id is required" };

        var entry = Core.ReportsRequestRegistry.Get(requestId);
        if (entry == null)
            return new JObject
            {
                ["status"] = "not_found",
                ["request_id"] = requestId,
            };

        double? latencyMs = null;
        if (entry.CompletedAtUtc.HasValue)
            latencyMs = (entry.CompletedAtUtc.Value - entry.StartedAtUtc).TotalMilliseconds;

        return new JObject
        {
            ["request_id"]            = entry.RequestId,
            ["profile"]               = entry.Profile,
            ["filter_summary"]        = entry.FilterSummary,
            ["status"]                = entry.Status,
            ["started_at_utc"]        = entry.StartedAtUtc.ToString("o"),
            ["completed_at_utc"]      = entry.CompletedAtUtc?.ToString("o"),
            ["latency_ms"]            = latencyMs,
            ["row_count"]             = entry.RowCount,
            ["cancellation_requested"] = entry.CancellationRequested,
            ["error_message"]         = entry.ErrorMessage,
        };
    }

    // Reports-query helpers.
    private static (long unixFrom, long unixTo, string label) ResolveDateRange(string period, string? from, string? to)
    {
        DateTime utcNow = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(from))
        {
            DateTime fDt = DateTime.SpecifyKind(DateTime.Parse(from!, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
            DateTime tDt = !string.IsNullOrEmpty(to)
                ? DateTime.SpecifyKind(DateTime.Parse(to!, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc)
                : utcNow;
            return (new DateTimeOffset(fDt).ToUnixTimeSeconds(),
                    new DateTimeOffset(tDt).ToUnixTimeSeconds(),
                    $"{from} .. {to ?? utcNow.ToString("yyyy-MM-dd")}");
        }
        long nowSec = new DateTimeOffset(utcNow).ToUnixTimeSeconds();
        long windowSec = period.ToLowerInvariant() switch
        {
            "today" => 86400,
            "24h"   => 86400,
            "7d"    => 7 * 86400,
            "30d"   => 30 * 86400,
            "90d"   => 90 * 86400,
            _       => 86400,
        };
        return (nowSec - windowSec, nowSec, period);
    }

    private static List<T> ParseEnumList<T>(string? csv) where T : struct, Enum
    {
        var result = new List<T>();
        if (string.IsNullOrWhiteSpace(csv)) return result;
        foreach (var part in csv.Split(','))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            if (Enum.TryParse<T>(part.Trim(), true, out var v)) result.Add(v);
        }
        return result;
    }

    private static MTShared.Types.TradeModeType ParseTradeMode(string? mode)
        => mode?.ToUpperInvariant() switch
        {
            "REAL"     => MTShared.Types.TradeModeType.REAL,
            "EMULATED" => MTShared.Types.TradeModeType.EMULATED,
            _          => MTShared.Types.TradeModeType.UNKNOWN,
        };

    private static string CsvEscape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    // Survey what would be imported from source_profile into
    // destination_profile.  Read-only on both sides; emits a structured list
    // entry per source algo with name/group/symbol/market and a duplicate
    // flag (same name present on destination).
    private JObject HandleImportFromProfile(JObject arguments)
    {
        string? src = arguments["source_profile"]?.Value<string>();
        string? dst = arguments["destination_profile"]?.Value<string>();
        string? filterGroup = arguments["filter_group_id"]?.Value<string>();
        string? filterSymbol = arguments["filter_symbol"]?.Value<string>()?.ToLowerInvariant();

        if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst))
            return new JObject { ["error"] = "source_profile and destination_profile are required" };

        var srcConn = _manager.Resolve(src);
        var dstConn = _manager.Resolve(dst);
        if (srcConn == null)
            return new JObject { ["error"] = $"no_connection: source_profile '{src}' is not connected" };
        if (dstConn == null)
            return new JObject { ["error"] = $"no_connection: destination_profile '{dst}' is not connected" };

        var srcAlgos = srcConn.AlgoStore.GetAll();
        var dstAlgos = dstConn.AlgoStore.GetAll();
        var dstNames = new HashSet<string>(
            dstAlgos.Select(a => a.name ?? "").Where(n => n.Length > 0),
            System.StringComparer.OrdinalIgnoreCase);

        var entries = new JArray();
        int totalEligible = 0, duplicateCount = 0;
        foreach (var algo in srcAlgos)
        {
            // Optional filters.
            if (!string.IsNullOrEmpty(filterGroup) &&
                algo.groupID.ToString() != filterGroup) continue;
            if (!string.IsNullOrEmpty(filterSymbol) &&
                !string.Equals(algo.symbol?.ToLowerInvariant(), filterSymbol, System.StringComparison.Ordinal)) continue;

            totalEligible++;
            bool isDup = !string.IsNullOrEmpty(algo.name) && dstNames.Contains(algo.name);
            if (isDup) duplicateCount++;
            entries.Add(new JObject
            {
                ["id"] = algo.id,
                ["name"] = algo.name,
                ["group_id"] = algo.groupID,
                ["symbol"] = algo.symbol,
                ["market"] = algo.marketType.ToString(),
                ["duplicate_on_destination"] = isDup,
                ["status"] = algo.actionType.ToString(),
            });
        }

        return new JObject
        {
            ["source_profile"] = src,
            ["destination_profile"] = dst,
            ["source_total_algos"] = srcAlgos.Count,
            ["destination_total_algos"] = dstAlgos.Count,
            ["eligible_for_import"] = totalEligible,
            ["duplicate_count"] = duplicateCount,
            ["filter_group_id"] = filterGroup,
            ["filter_symbol"] = filterSymbol,
            ["entries"] = entries,
            ["mutation_supported"] = false,
            ["mutation_notice"] =
                "import_from_profile_dry_run_only: this tool is read-only. " +
                "To actually copy, drive mt_algos_copy per id (one round-trip per algo) " +
                "or use mt_algos_paste_from_clipboard / mt_algos_import_json for the " +
                "clipboard-based flow.  Bulk-mutation as a single MCP call is deferred " +
                "to a follow-up workstream.",
        };
    }

    // Build a populated AlertInfoData from typed MCP args and send.
    // Returns the structured envelope { ok, message, alert: { name, symbol, … } }
    // when the wire call resolves; surfaces 'no_connection' if the profile isn't connected.
    private JObject HandleAlertsSave(JObject arguments)
    {
        string? profile = arguments["profile"]?.Value<string>();
        var conn = _manager.Resolve(profile);
        if (conn == null)
            return new JObject { ["error"] = $"no_connection: profile '{profile ?? "(default)"}' is not connected" };

        string? name   = arguments["name"]?.Value<string>();
        string? symbol = arguments["symbol"]?.Value<string>();
        string? marketStr = arguments["market_type"]?.Value<string>();
        string? condStr   = arguments["condition_type"]?.Value<string>();
        double  refPrice  = arguments["ref_price"]?.Value<double>() ?? 0;
        string? dirStr    = arguments["direction"]?.Value<string>() ?? "BOTH";
        double  changeVal = arguments["change_value"]?.Value<double>() ?? 0;
        long    alertId   = arguments["alert_id"]?.Value<long?>() ?? 0;
        string? repeatStr = arguments["repeat_type"]?.Value<string>() ?? "ONLY_ONCE";

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(symbol) ||
            string.IsNullOrEmpty(marketStr) || string.IsNullOrEmpty(condStr))
            return new JObject { ["error"] = "name, symbol, market_type, condition_type are required" };

        if (!Enum.TryParse<MTShared.Types.MarketType>(marketStr, true, out var marketType))
            return new JObject { ["error"] = $"invalid market_type '{marketStr}' — expected FUTURES or SPOT" };
        if (!Enum.TryParse<MTShared.Types.AlertConditionType>(condStr, true, out var condType))
            return new JObject { ["error"] = $"invalid condition_type '{condStr}' — expected CROSSING|PERCENTAGE_CHANGE|VALUE_CHANGE" };
        if (!Enum.TryParse<MTShared.Types.AlertDirectionType>(dirStr, true, out var dirType))
            return new JObject { ["error"] = $"invalid direction '{dirStr}' — expected BOTH|UP|DOWN" };
        if (!Enum.TryParse<MTShared.Types.AlertRepeatType>(repeatStr, true, out var repeatType))
            return new JObject { ["error"] = $"invalid repeat_type '{repeatStr}' — expected ONLY_ONCE|EVERY_TIME" };

        var settings = new MTShared.Structs.AlertConditionSettingsData
        {
            refPrice = refPrice,
            changeValue = changeVal,
            graphToolId = "",
            directionType = dirType,
        };
        var condition = new MTShared.Structs.AlertConditionData
        {
            type = condType,
            crossing          = condType == MTShared.Types.AlertConditionType.CROSSING          ? settings : new MTShared.Structs.AlertConditionSettingsData(),
            percentageChange  = condType == MTShared.Types.AlertConditionType.PERCENTAGE_CHANGE ? settings : new MTShared.Structs.AlertConditionSettingsData(),
            valueChange       = condType == MTShared.Types.AlertConditionType.VALUE_CHANGE      ? settings : new MTShared.Structs.AlertConditionSettingsData(),
        };
        var options = new MTShared.Structs.AlertOptionsData
        {
            expirationType = MTShared.Types.AlertExpirationType.NEVER,
            expirationTime = 0,
            repeatType = repeatType,
            repeatFrequency = 0,
            bufferPercentage = 0,
        };
        var alert = new MTShared.Network.AlertInfoData
        {
            isRunning = true,
            id = alertId,
            name = name,
            marketType = marketType,
            symbol = symbol,
            condition = condition,
            options = options,
            trigger = new MTShared.Structs.TriggerInfoData(),
            lastAlertTime = 0,
        };

        string serverMsg = conn.SendAlertsSave(new List<MTShared.Network.AlertInfoData> { alert });
        return new JObject
        {
            ["ok"] = true,
            ["message"] = serverMsg,
            ["alert"] = new JObject
            {
                ["name"] = name, ["symbol"] = symbol, ["market_type"] = marketType.ToString(),
                ["condition_type"] = condType.ToString(), ["direction"] = dirType.ToString(),
                ["ref_price"] = refPrice, ["change_value"] = changeVal, ["alert_id_in"] = alertId,
            },
        };
    }

    private JObject HandleAlertsDelete(JObject arguments)
    {
        string? profile = arguments["profile"]?.Value<string>();
        var conn = _manager.Resolve(profile);
        if (conn == null)
            return new JObject { ["error"] = $"no_connection: profile '{profile ?? "(default)"}' is not connected" };

        bool applyToAll = arguments["apply_to_all"]?.Value<bool>() ?? false;
        var ids = ParseAlertIds(arguments["alert_ids"]?.Value<string>());
        if (!applyToAll && ids.Count == 0)
            return new JObject { ["error"] = "either alert_ids (csv) or apply_to_all=true is required" };

        string serverMsg = conn.SendAlertsDelete(ids, applyToAll);
        return new JObject
        {
            ["ok"] = true,
            ["message"] = serverMsg,
            ["deleted_ids"] = new JArray(ids.ConvertAll(x => (JToken)x)),
            ["apply_to_all"] = applyToAll,
        };
    }

    private JObject HandleAlertsSetRunning(JObject arguments)
    {
        string? profile = arguments["profile"]?.Value<string>();
        var conn = _manager.Resolve(profile);
        if (conn == null)
            return new JObject { ["error"] = $"no_connection: profile '{profile ?? "(default)"}' is not connected" };

        bool running = arguments["running"]?.Value<bool>() ?? throw new InvalidOperationException("running is required");
        bool applyToAll = arguments["apply_to_all"]?.Value<bool>() ?? false;
        var ids = ParseAlertIds(arguments["alert_ids"]?.Value<string>());
        if (!applyToAll && ids.Count == 0)
            return new JObject { ["error"] = "either alert_ids (csv) or apply_to_all=true is required" };

        string serverMsg = conn.SendAlertsSetRunning(ids, running, applyToAll);
        return new JObject
        {
            ["ok"] = true,
            ["message"] = serverMsg,
            ["targeted_ids"] = new JArray(ids.ConvertAll(x => (JToken)x)),
            ["apply_to_all"] = applyToAll,
            ["running"] = running,
        };
    }

    private static List<long> ParseAlertIds(string? csv)
    {
        var result = new List<long>();
        if (string.IsNullOrWhiteSpace(csv)) return result;
        foreach (var part in csv.Split(','))
        {
            string t = part.Trim();
            if (t.Length == 0) continue;
            if (long.TryParse(t, out long id) && id > 0) result.Add(id);
        }
        return result;
    }

    // Send shutdown/restart service command to MTCore
    // command: "shutdown" | "restart" | "restart_update" | "restart_clear_orders" | "restart_clear_archive"
    private JObject HandleCoreShutdown(JObject arguments)
    {
        string? profile = arguments["profile"]?.Value<string>();
        string cmd = arguments["command"]?.Value<string>()?.ToLowerInvariant() ?? "shutdown";
        bool confirm = arguments["confirm"]?.Value<bool>() ?? false;

        if (!confirm)
            return new JObject { ["error"] = "confirm=true is required to send a service command to MTCore" };

        CoreConnection? conn = _manager.Resolve(profile);
        if (conn == null)
            return new JObject { ["error"] = $"No active connection{(profile != null ? $" for profile '{profile}'" : "")}" };

        CoreServiceCommand command = cmd switch
        {
            "restart"              => CoreServiceCommand.RESTART,
            "restart_update"       => CoreServiceCommand.RESTART_WITH_UPDATE,
            "restart_clear_orders" => CoreServiceCommand.RESTART_WITH_CLEAR_ORDERS_CACHE,
            "restart_clear_archive"=> CoreServiceCommand.RESTART_WITH_CLEAR_ARCHIVE_DATA,
            _                      => CoreServiceCommand.SHUTDOWN,
        };

        conn.SendServiceCommand(command);
        return new JObject
        {
            ["status"]  = "sent",
            ["command"] = command.ToString(),
            ["profile"] = conn.Profile.Name,
        };
    }

    // Send a TP/SL algorithm change request
    // Builds a TPSLInfoData struct from the JSON arguments and sends it to MT-Core.
    private JObject HandleAlgosTpslChange(JObject arguments)
    {
        string? profile = arguments["profile"]?.Value<string>();
        CoreConnection? conn = _manager.Resolve(profile);
        if (conn == null)
            return new JObject { ["error"] = "No active connection" };

        // TP settings
        bool   tpOn          = arguments["tp_enabled"]?.Value<bool>()   ?? false;
        double tpPct         = arguments["tp_pct"]?.Value<double>()       ?? 0.0;
        // SL settings
        bool   slOn          = arguments["sl_enabled"]?.Value<bool>()   ?? false;
        double slPct         = arguments["sl_pct"]?.Value<double>()       ?? 0.0;
        bool   trailingOn    = arguments["trailing_enabled"]?.Value<bool>() ?? false;
        double trailingSpread= arguments["trailing_spread"]?.Value<double>() ?? 0.0;

        var msgData = new TPSLInfoData();
        msgData.requestExchangeType = conn.Profile.Exchange;
        msgData.takeProfitSettings.isOn        = tpOn;
        msgData.takeProfitSettings.percentage  = (float)tpPct;
        msgData.stopLossSettings.isOn          = slOn;
        msgData.stopLossSettings.percentage    = (float)slPct;
        msgData.stopLossSettings.tralingIsOn   = trailingOn;
        msgData.stopLossSettings.trailingSpread= (float)trailingSpread;

        conn.SendTpSlAlgorithmChangeRequest(msgData, NetworkMessagePriority.MEDIUM);
        return new JObject
        {
            ["status"]   = "sent",
            ["tp_on"]    = tpOn,
            ["tp_pct"]   = tpPct,
            ["sl_on"]    = slOn,
            ["sl_pct"]   = slPct,
            ["trailing"] = trailingOn,
        };
    }

    // Request algorithm profiling data (fire-and-forget; result comes via event stream)
    private JObject HandleAlgosProfiling(JObject arguments)
    {
        string? profile    = arguments["profile"]?.Value<string>();
        string? symbol     = arguments["symbol"]?.Value<string>();
        long algoId        = arguments["algo_id"]?.Value<long>() ?? 0L;

        if (string.IsNullOrEmpty(symbol))
            return new JObject { ["error"] = "symbol is required" };

        CoreConnection? conn = _manager.Resolve(profile);
        if (conn == null)
            return new JObject { ["error"] = "No active connection" };

        string marketStr = arguments["market"]?.Value<string>() ?? "LINEAR";
        if (!Enum.TryParse<MarketType>(marketStr, ignoreCase: true, out var market))
            market = MarketType.FUTURES;

        conn.SendAlgorithmProfilingDataRequest(conn.Profile.Exchange, market, symbol, algoId);
        return new JObject
        {
            ["status"]  = "requested",
            ["symbol"]  = symbol,
            ["algo_id"] = algoId,
            ["market"]  = market.ToString(),
            ["note"]    = "Results will be delivered via mt_events_poll when Core responds",
        };
    }


    // State reconciliation: Full snapshot of all groups and algorithms
    private JObject HandleAlgosSnapshot(JObject arguments)
    {
        string? profile = arguments["profile"]?.Value<string>();
        var result = new JArray();

        IReadOnlyList<CoreConnection> connections = string.IsNullOrEmpty(profile)
            ? _manager.GetAll()
            : new[] { _manager.Resolve(profile) }.Where(c => c != null).ToList()!;

        foreach (CoreConnection conn in connections)
        {
            if (!conn.IsConnected) continue;

            var serverObj = new JObject
            {
                ["profile"] = conn.Name,
                ["groups"] = new JArray()
            };

            IReadOnlyList<AlgorithmGroupData> groups = conn.AlgoStore.GetAllGroups();
            foreach (AlgorithmGroupData g in groups)
            {
                var groupObj = new JObject
                {
                    ["id"] = g.id,
                    ["name"] = g.name,
                    ["type"] = g.groupType.ToString(),
                    ["algos"] = new JArray()
                };

                IReadOnlyList<AlgorithmData> algos = conn.AlgoStore.GetByGroup(g.id);
                foreach (AlgorithmData a in algos)
                {
                    ((JArray)groupObj["algos"]!).Add(new JObject
                    {
                        ["id"] = a.id,
                        ["name"] = a.name,
                        ["symbol"] = a.symbol,
                        ["signature"] = a.signature,
                        ["running"] = a.isRunning,
                        ["market"] = a.marketType.ToString(),
                        ["group_id"] = a.groupID
                    });
                }

                ((JArray)serverObj["groups"]!).Add(groupObj);
            }

            // Add ungrouped algos (groupID == 0 or group not found)
            IReadOnlyList<AlgorithmData> allAlgos = conn.AlgoStore.GetAll();
            var ungrouped = new JArray();
            foreach (AlgorithmData a in allAlgos)
            {
                if (a.groupID == 0 || conn.AlgoStore.FindGroupById(a.groupID) == null)
                {
                    ungrouped.Add(new JObject
                    {
                        ["id"] = a.id,
                        ["name"] = a.name,
                        ["symbol"] = a.symbol,
                        ["signature"] = a.signature,
                        ["running"] = a.isRunning,
                        ["market"] = a.marketType.ToString(),
                        ["group_id"] = a.groupID
                    });
                }
            }
            if (ungrouped.Count > 0)
                serverObj["ungrouped"] = ungrouped;

            serverObj["total_algos"] = allAlgos.Count;
            serverObj["total_groups"] = groups.Count;
            result.Add(serverObj);
        }

        return new JObject
        {
            ["snapshot"] = result,
            ["captured_at"] = DateTime.UtcNow.ToString("o"),
            ["server_count"] = result.Count
        };
    }

    // State reconciliation: Find group by name
    private JObject HandleAlgosGroupByName(JObject arguments)
    {
        string? name = arguments["name"]?.Value<string>();
        string? profile = arguments["profile"]?.Value<string>();

        if (string.IsNullOrEmpty(name))
            return new JObject { ["error"] = "name is required" };

        CoreConnection? conn = _manager.Resolve(profile);
        if (conn == null)
            return new JObject { ["error"] = "No active connection" };

        AlgorithmGroupData? group = conn.AlgoStore.FindGroupByName(name);
        if (group == null)
        {
            return new JObject
            {
                ["found"] = false,
                ["name"] = name,
                ["profile"] = conn.Name
            };
        }

        IReadOnlyList<AlgorithmData> algos = conn.AlgoStore.GetByGroup(group.id);
        var algosArr = new JArray();
        foreach (AlgorithmData a in algos)
        {
            algosArr.Add(new JObject
            {
                ["id"] = a.id,
                ["name"] = a.name,
                ["symbol"] = a.symbol,
                ["signature"] = a.signature,
                ["running"] = a.isRunning,
                ["market"] = a.marketType.ToString()
            });
        }

        return new JObject
        {
            ["found"] = true,
            ["group_id"] = group.id,
            ["name"] = group.name,
            ["type"] = group.groupType.ToString(),
            ["algo_count"] = algos.Count,
            ["algos"] = algosArr,
            ["profile"] = conn.Name
        };
    }
    // Snapshot all settings for a profile to a timestamped JSON file
    private JObject HandleConfigSnapshot(JObject arguments)
    {
        string? profile = arguments["profile"]?.Value<string>();
        string profileSuffix = profile != null ? $" @{profile}" : "";

        // Fetch settings
        CommandResult settingsResult = _registry.Dispatch($"settings get{profileSuffix}");
        if (!settingsResult.Success)
        {
            return new JObject { ["error"] = $"Failed to get settings: {settingsResult.Message}" };
        }

        // Fetch algos list
        CommandResult algosResult = _registry.Dispatch($"algos list{profileSuffix}");

        string snapshotDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mt-snapshots"
        );
        Directory.CreateDirectory(snapshotDir);

        string ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        string safeName = profile?.Replace("/", "_").Replace(".", "_") ?? "default";
        string snapshotPath = Path.Combine(snapshotDir, $"snapshot_{safeName}_{ts}.json");

        var snapshot = new JObject
        {
            ["profile"] = profile ?? "default",
            ["captured_at"] = DateTime.UtcNow.ToString("o"),
            ["settings"] = settingsResult.Data != null
                ? JToken.FromObject(settingsResult.Data)
                : new JObject(),
            ["algos_count"] = algosResult.Data != null
                ? (algosResult.Data is System.Collections.ICollection coll ? coll.Count : 0)
                : 0,
        };

        File.WriteAllText(snapshotPath, snapshot.ToString(Newtonsoft.Json.Formatting.Indented));

        return new JObject
        {
            ["snapshot_path"] = snapshotPath,
            ["profile"] = profile ?? "default",
            ["captured_at"] = snapshot["captured_at"],
            ["status"] = "ok",
        };
    }

    // Restore settings from a snapshot file
    private JObject HandleConfigRestore(JObject arguments)
    {
        string? path = arguments["path"]?.Value<string>();
        bool confirm = arguments["confirm"]?.Value<bool>() == true;
        string? profile = arguments["profile"]?.Value<string>();
        string profileSuffix = profile != null ? $" @{profile}" : "";

        if (string.IsNullOrEmpty(path))
            return new JObject { ["error"] = "path is required" };

        if (!File.Exists(path))
            return new JObject { ["error"] = $"Snapshot file not found: {path}" };

        if (!confirm)
            return new JObject
            {
                ["status"] = "dry_run",
                ["message"] = "Set confirm=true to apply the restore",
                ["snapshot_path"] = path,
            };

        JObject snapshot;
        try
        {
            snapshot = JObject.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return new JObject { ["error"] = $"Failed to parse snapshot: {ex.Message}" };
        }

        var settings = snapshot["settings"] as JObject;
        if (settings == null)
            return new JObject { ["error"] = "Snapshot has no settings block" };

        var results = new System.Collections.Generic.List<JObject>();
        foreach (var kv in settings)
        {
            string key = kv.Key;
            string value = kv.Value?.ToString() ?? "";
            CommandResult r = _registry.Dispatch($"settings set {key} {value}{profileSuffix} --confirm");
            results.Add(new JObject { ["key"] = key, ["success"] = r.Success, ["msg"] = r.Message });
        }

        int ok = results.Count(r => r["success"]?.Value<bool>() == true);
        return new JObject
        {
            ["status"] = "restored",
            ["settings_applied"] = ok,
            ["settings_total"] = results.Count,
            ["details"] = new JArray(results.Cast<object>().ToArray()),
        };
    }

    /// <summary>
    /// Import algorithms from algorithms.config JSON format (native MTCore format).
    /// Creates groups and algos directly from the JSON, avoiding V2 text parsing.
    /// </summary>
    private JObject HandleConfigImportAlgos(JObject arguments)
    {
        string? path = arguments["path"]?.Value<string>();
        bool confirm = arguments["confirm"]?.Value<bool>() == true;
        bool emulated = arguments["emulated"]?.Value<bool>() == true;
        string? profile = arguments["profile"]?.Value<string>();

        if (string.IsNullOrEmpty(path))
            return new JObject { ["error"] = "path is required" };

        if (!File.Exists(path))
            return new JObject { ["error"] = $"File not found: {path}" };

        // Parse the algorithms.config JSON
        JObject configJson;
        try
        {
            string raw = File.ReadAllText(path);
            // Handle BOM
            if (raw.Length > 0 && raw[0] == '\uFEFF')
                raw = raw.Substring(1);
            configJson = JObject.Parse(raw);
        }
        catch (Exception ex)
        {
            return new JObject { ["error"] = $"Failed to parse JSON: {ex.Message}" };
        }

        JArray? groups = configJson["groups"] as JArray;
        JArray? configs = configJson["configs"] as JArray;

        if (configs == null || configs.Count == 0)
            return new JObject { ["error"] = "No 'configs' array found in file" };

        if (!confirm)
        {
            return new JObject
            {
                ["status"] = "dry_run",
                ["message"] = $"Would import {configs.Count} algos in {groups?.Count ?? 0} groups. Set confirm=true to apply.",
                ["groups"] = groups?.Count ?? 0,
                ["algos"] = configs.Count,
                ["emulated"] = emulated
            };
        }

        CoreConnection? conn = _manager.Resolve(profile);
        if (conn == null || !conn.IsConnected)
            return new JObject { ["error"] = $"Not connected to '{profile}'. Connect first." };

        var results = new System.Collections.Generic.List<string>();
        int successCount = 0;
        var groupIdMap = new Dictionary<long, long>();

        // Step 1: Create groups
        if (groups != null)
        {
            foreach (JToken groupToken in groups)
            {
                long groupId = groupToken["id"]?.Value<long>() ?? 0;
                string groupName = groupToken["name"]?.Value<string>() ?? "";
                int groupType = groupToken["groupType"]?.Value<int>() ?? 0;

                var groupRequest = new AlgorithmData
                {
                    groupID = groupId,
                    name = groupName,
                    groupType = (AlgorithmGroupType)groupType,
                    actionType = AlgorithmData.ActionType.SAVE_GROUP
                };

                NotificationMessageData? notification = conn.SendAlgorithmRequest(groupRequest);
                if (notification == null)
                    results.Add($"  Group '{groupName}': sent (timed out)");
                else if (notification.IsOk)
                    results.Add($"  Group '{groupName}': CREATED ✓");
                else
                    results.Add($"  Group '{groupName}': FAILED — {notification.msgString}");

                // Wait for Core to process and remap group IDs
                System.Threading.Thread.Sleep(300);

                IReadOnlyList<AlgorithmGroupData> serverGroups = conn.AlgoStore.GetAllGroups();
                foreach (AlgorithmGroupData g in serverGroups)
                {
                    if (g.name == groupName && g.groupType == (AlgorithmGroupType)groupType)
                    {
                        groupIdMap[groupId] = g.id;
                        if (g.id != groupId)
                            results.Add($"    → Remapped: {groupId} → {g.id}");
                        break;
                    }
                }
            }
        }

        // Step 2: Create algos from configs
        foreach (JToken configToken in configs)
        {
            string algoName = configToken["name"]?.Value<string>() ?? "Unknown";
            string signature = configToken["signature"]?.Value<string>() ?? "";
            int version = configToken["version"]?.Value<int>() ?? 7;
            long groupId = configToken["groupID"]?.Value<long>() ?? 0;
            int groupType = configToken["groupType"]?.Value<int>() ?? 0;
            bool isTradingAlgo = configToken["isTradingAlgo"]?.Value<bool>() ?? false;
            bool isClone = configToken["isClone"]?.Value<bool>() ?? false;
            string description = configToken["description"]?.Value<string>() ?? "";

            // Get the args JSON — this is the key difference from V2 import
            JObject? argsObj = configToken["args"] as JObject;
            if (argsObj == null)
            {
                results.Add($"  {algoName} ({signature}): SKIPPED — no args");
                continue;
            }

            // Override isEmulated if requested
            if (emulated)
            {
                JObject? arguments2 = argsObj["Arguments"] as JObject;
                if (arguments2 != null)
                {
                    JObject? emuArg = arguments2["isEmulated"] as JObject;
                    if (emuArg != null)
                    {
                        emuArg["value"] = true;
                    }
                }
            }

            // Remap groupID if core reassigned it
            // Fix #13: drop > 0 guard — TryGetValue is itself the filter; groupId=0 is a legal map key
            if (groupIdMap.TryGetValue(groupId, out long newGroupId))
                groupId = newGroupId;

            // Extract marketType from args
            MarketType marketType = MarketType.FUTURES;
            string algoSymbol = "";
            JObject? argsArguments = argsObj["Arguments"] as JObject;
            if (argsArguments != null)
            {
                JObject? mtArg = argsArguments["marketType"] as JObject;
                if (mtArg != null)
                {
                    int mtVal = mtArg["value"]?.Value<int>() ?? 3;
                    marketType = (MarketType)mtVal;
                }
                JObject? symArg = argsArguments["symbol"] as JObject;
                if (symArg != null)
                {
                    algoSymbol = symArg["value"]?.Value<string>() ?? "";
                }
            }

            var algoData = new AlgorithmData
            {
                id = -1,
                version = version,
                name = algoName,
                signature = signature,
                description = description,
                groupID = groupId,
                groupType = (AlgorithmGroupType)groupType,
                isTradingAlgo = isTradingAlgo,
                isClone = isClone,
                isRunning = false,
                isProcessing = false,
                actionType = AlgorithmData.ActionType.SAVE,
                argsJson = argsObj.ToString(Formatting.None),
                marketType = marketType,
                symbol = algoSymbol
            };

            NotificationMessageData? notification = conn.SendAlgorithmRequest(algoData);
            if (notification == null)
            {
                results.Add($"  {algoName} ({signature}): sent (timed out)");
                successCount++; // Assume success on timeout
            }
            else if (notification.IsOk)
            {
                results.Add($"  {algoName} ({signature}): CREATED ✓");
                successCount++;
            }
            else
            {
                results.Add($"  {algoName} ({signature}): FAILED — {notification.msgString}");
            }
        }

        return new JObject
        {
            ["success"] = successCount > 0,
            ["message"] = $"[{conn.Name}] Import results: {successCount}/{configs.Count} created.\n{string.Join("\n", results)}",
            ["data"] = new JObject
            {
                ["server"] = conn.Name,
                ["total"] = configs.Count,
                ["created"] = successCount,
                ["groups"] = groups?.Count ?? 0,
                ["emulated"] = emulated
            }
        };
    }

    // Diff settings between two profiles
    /// <summary>
    /// Diff two snapshot files written by <c>mt_config_snapshot</c>.
    /// Pure client-side, no MTCore wire calls.  Snapshot path may be absolute
    /// or a bare filename relative to <c>~/.mt-snapshots/</c>.  Diffs the
    /// <c>settings</c> section of each snapshot (added / removed / changed).
    /// </summary>
    private JObject HandleSettingsDiffSnapshots(JObject arguments)
    {
        string? rawA = arguments["snapshot_a"]?.Value<string>();
        string? rawB = arguments["snapshot_b"]?.Value<string>();
        if (string.IsNullOrEmpty(rawA) || string.IsNullOrEmpty(rawB))
            return new JObject { ["error"] = "snapshot_a and snapshot_b are required (snapshot file paths or filenames under ~/.mt-snapshots/)" };

        string Resolve(string p)
        {
            if (Path.IsPathRooted(p)) return p;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mt-snapshots", p);
        }
        string pathA = Resolve(rawA);
        string pathB = Resolve(rawB);
        if (!File.Exists(pathA)) return new JObject { ["error"] = $"snapshot_not_found: {pathA}" };
        if (!File.Exists(pathB)) return new JObject { ["error"] = $"snapshot_not_found: {pathB}" };

        JObject snapA, snapB;
        try { snapA = JObject.Parse(File.ReadAllText(pathA)); }
        catch (Exception ex) { return new JObject { ["error"] = $"snapshot_a parse error: {ex.Message}" }; }
        try { snapB = JObject.Parse(File.ReadAllText(pathB)); }
        catch (Exception ex) { return new JObject { ["error"] = $"snapshot_b parse error: {ex.Message}" }; }

        // Each snapshot has shape {profile, captured_at, settings: {...}, algos_count}.
        // The 'settings' block is what we diff.  Tolerate the section being
        // present as object OR array (legacy snapshots may use either shape).
        var dictA = SnapshotToDict(snapA["settings"]);
        var dictB = SnapshotToDict(snapB["settings"]);

        var allKeys = dictA.Keys.Union(dictB.Keys).OrderBy(k => k).ToList();
        var diffs = new JArray();
        int sameCount = 0;
        foreach (string key in allKeys)
        {
            bool inA = dictA.TryGetValue(key, out string? va);
            bool inB = dictB.TryGetValue(key, out string? vb);
            if (!inA)        diffs.Add(new JObject { ["key"] = key, ["a"] = null, ["b"] = vb, ["change"] = "added" });
            else if (!inB)   diffs.Add(new JObject { ["key"] = key, ["a"] = va, ["b"] = null, ["change"] = "removed" });
            else if (va != vb) diffs.Add(new JObject { ["key"] = key, ["a"] = va, ["b"] = vb, ["change"] = "changed" });
            else sameCount++;
        }

        return new JObject
        {
            ["snapshot_a_path"] = pathA,
            ["snapshot_b_path"] = pathB,
            ["snapshot_a_profile"] = snapA["profile"]?.ToString() ?? "?",
            ["snapshot_b_profile"] = snapB["profile"]?.ToString() ?? "?",
            ["snapshot_a_captured_at"] = snapA["captured_at"]?.ToString() ?? "?",
            ["snapshot_b_captured_at"] = snapB["captured_at"]?.ToString() ?? "?",
            ["diff_count"] = diffs.Count,
            ["same_count"] = sameCount,
            ["diffs"] = diffs,
        };
    }

    private static Dictionary<string, string> SnapshotToDict(JToken? settingsToken)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (settingsToken == null) return d;
        if (settingsToken is JObject obj)
        {
            foreach (var kv in obj)
                d[kv.Key] = kv.Value?.ToString() ?? "";
        }
        else if (settingsToken is JArray arr)
        {
            // Legacy snapshots may serialise settings as an array of {Key, Value}.
            foreach (var item in arr)
            {
                if (item is JObject row)
                {
                    string? k = row["Key"]?.Value<string>() ?? row["key"]?.Value<string>();
                    if (!string.IsNullOrEmpty(k))
                        d[k] = row["Value"]?.ToString() ?? row["value"]?.ToString() ?? "";
                }
            }
        }
        return d;
    }

    private JObject HandleSettingsDiff(JObject arguments)
    {
        string? profileA = arguments["profile_a"]?.Value<string>();
        string? profileB = arguments["profile_b"]?.Value<string>();

        if (string.IsNullOrEmpty(profileA) || string.IsNullOrEmpty(profileB))
            return new JObject { ["error"] = "profile_a and profile_b are required" };

        CommandResult ra = _registry.Dispatch($"settings get @{profileA}");
        CommandResult rb = _registry.Dispatch($"settings get @{profileB}");

        if (!ra.Success)
            return new JObject { ["error"] = $"Failed to get settings for {profileA}: {ra.Message}" };
        if (!rb.Success)
            return new JObject { ["error"] = $"Failed to get settings for {profileB}: {rb.Message}" };

        // Extract flat key-value from Data
        var dictA = ExtractSettingsDict(ra.Data);
        var dictB = ExtractSettingsDict(rb.Data);

        var allKeys = dictA.Keys.Union(dictB.Keys).OrderBy(k => k).ToList();
        var diffs = new JArray();
        var same = new JArray();

        foreach (string key in allKeys)
        {
            bool inA = dictA.TryGetValue(key, out string? valA);
            bool inB = dictB.TryGetValue(key, out string? valB);

            if (!inA)
                diffs.Add(new JObject { ["key"] = key, ["a"] = null, ["b"] = valB, ["change"] = "added" });
            else if (!inB)
                diffs.Add(new JObject { ["key"] = key, ["a"] = valA, ["b"] = null, ["change"] = "removed" });
            else if (valA != valB)
                diffs.Add(new JObject { ["key"] = key, ["a"] = valA, ["b"] = valB, ["change"] = "changed" });
            else
                same.Add(key);
        }

        return new JObject
        {
            ["profile_a"] = profileA,
            ["profile_b"] = profileB,
            ["diff_count"] = diffs.Count,
            ["same_count"] = same.Count,
            ["diffs"] = diffs,
        };
    }

    private static System.Collections.Generic.Dictionary<string, string> ExtractSettingsDict(object? data)
    {
        var result = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data == null) return result;

        try
        {
            var token = JToken.FromObject(data);
            if (token is JObject obj)
            {
                foreach (var kv in obj)
                    result[kv.Key] = kv.Value?.ToString() ?? "";
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JObject entry)
                    {
                        string? k = entry["key"]?.Value<string>() ?? entry["Key"]?.Value<string>();
                        string? v = entry["value"]?.Value<string>() ?? entry["Value"]?.Value<string>() ?? entry["current_value"]?.Value<string>();
                        if (k != null) result[k] = v ?? "";
                    }
                }
            }
        }
        catch { }

        return result;
    }

    // Tool definitions

    // ── Sliding-window exchange rate limit tracker ──────────────────────────
    private sealed class RateLimitTracker
    {
        private readonly ConcurrentDictionary<string, Queue<long>> _windows =
            new(StringComparer.OrdinalIgnoreCase);

        // Bybit conservative limits — orders: 300/5min, market data: 120/min, account: 120/min
        private static readonly Dictionary<string, (int Limit, long WindowMs)> _specs =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["orders"]  = (300, 300_000),
                ["market"]  = (120,  60_000),
                ["account"] = (120,  60_000),
            };

        public void RecordCall(string toolName)
        {
            string? cat = Categorize(toolName);
            if (cat == null) return;
            var q = _windows.GetOrAdd(cat, _ => new Queue<long>());
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (q) { Prune(q, _specs[cat].WindowMs, now); q.Enqueue(now); }
        }

        public JObject GetStatus()
        {
            var result = new JObject();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var (cat, spec) in _specs)
            {
                var q = _windows.GetOrAdd(cat, _ => new Queue<long>());
                int used;
                lock (q) { Prune(q, spec.WindowMs, now); used = q.Count; }
                result[cat] = new JObject
                {
                    ["limit"]     = spec.Limit,
                    ["window_ms"] = spec.WindowMs,
                    ["used"]      = used,
                    ["remaining"] = spec.Limit - used,
                };
            }
            return result;
        }

        private static string? Categorize(string toolName) =>
            toolName.StartsWith("mt_orders_",  StringComparison.OrdinalIgnoreCase) ? "orders"  :
            toolName.StartsWith("mt_exchange_", StringComparison.OrdinalIgnoreCase) ? "market"  :
            toolName.StartsWith("mt_account_", StringComparison.OrdinalIgnoreCase) ? "account" :
            null;

        private static void Prune(Queue<long> q, long windowMs, long now)
        {
            long cutoff = now - windowMs;
            while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
        }
    }

        private static void LogStderr(string message)
    {
        Console.Error.WriteLine($"[MCP] {message}");
        Console.Error.Flush();
    }

    #endregion
}
