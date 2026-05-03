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
/// Maps every REPL command to an MCP tool, providing AI agents
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

    // MT-005: Event streaming
    private readonly EventBroadcaster _events = new();
    private SseEventServer? _sseServer;

    // MT-006: Prometheus metrics
    private readonly MetricsCollector _metrics = new();

    // MT-011: Rate limit tracker
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

        // Account data (Phase A)
        _registry.Register(new AccountCommand(_manager));
        _registry.Register(new CoreStatusCommand(_manager));
        _registry.Register(new ExchangeCommand(_manager));

        // Algorithm management (Phase B)
        _registry.Register(new AlgosCommand(_manager));

        // Server profile settings (Phase B)
        _registry.Register(new SettingsCommand(_manager));

        // Import (Phase C)
        _registry.Register(new ImportCommand(_manager));

        // Orders (Phase D)
        _registry.Register(new OrdersCommand(_manager));

        // Trade Reports (Phase F)
        _registry.Register(new ReportsCommand(_manager, new ReportStore()));

        // Fleet (Phase E) — fleet-wide operations
        _registry.Register(new FleetCommand(_manager));
        _registry.Register(new TagCommand(_manager));  // MT-007

        // Monitor — real-time core monitoring via UDP (Phase G)
        _registry.Register(new MonitorCommand(_manager));

        // Configuration
        _registry.Register(new ProfileCommand());
        _registry.Register(new OutputCommand(_output));

        // ── Phase H — feature command parity with REPL ──
        // (These were registered in Program.cs but missing here, causing
        //  MCP tools to fail with "Unknown command: '<verb>'" at dispatch.)
        _registry.Register(new AutoStopsCommand(_manager));
        _registry.Register(new BlacklistCommand(_manager));
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

        // MT-005: start SSE event server
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
                LogStderr($"Error processing request: {ex.Message}");
                WriteStdout(MakeErrorResponse(null, -32700, $"Parse error: {ex.Message}"));
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
        foreach (JObject tool in GetToolDefinitions())
        {
            tools.Add(tool);
        }
        return MakeResult(id, new JObject { ["tools"] = tools });
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
        _rateLimits.RecordCall(toolName);       // MT-011
        var _latencySw = System.Diagnostics.Stopwatch.StartNew(); // MT-022

        // MT-005: Event streaming tools — handled directly (no REPL dispatch)
        JObject? evtResponse = HandleEventTool(toolName, arguments);
        if (evtResponse != null)
        {
            var evtContent = new JArray { new JObject { ["type"] = "text", ["text"] = evtResponse.ToString(Newtonsoft.Json.Formatting.None) } };
            return MakeResult(id, new JObject { ["content"] = evtContent, ["isError"] = false });
        }

        // MT-006/MT-009/MT-010: Internal tools with multi-step logic
        JObject? internalResponse = HandleInternalTool(toolName, arguments);
        if (internalResponse != null)
        {
            _metrics.RecordLatency(toolName, _latencySw.ElapsedMilliseconds); // MT-022
            var internalContent = new JArray { new JObject { ["type"] = "text", ["text"] = internalResponse.ToString(Newtonsoft.Json.Formatting.None) } };
            return MakeResult(id, new JObject { ["content"] = internalContent, ["isError"] = false });
        }

        // Bulk-operation safety gate (MCP-only): refuse start_all / stop_all /
        // fleet_disconnect unless confirm=true was explicitly supplied. Mirrors
        // the gating already in place at the REPL layer for delete / cancel-all
        // / close-all. The REPL TUI is unaffected — these REPL commands keep
        // their existing (no --confirm) semantics for direct human operators.
        if (RequiresMcpConfirm(toolName) && arguments["confirm"]?.Value<bool>() != true)
        {
            var bulkContent = new JArray
            {
                new JObject
                {
                    ["type"] = "text",
                    ["text"] = $"{{\"error\":\"{toolName} is a bulk operation; pass confirm=true to execute.\"}}"
                }
            };
            _metrics.RecordLatency(toolName, _latencySw.ElapsedMilliseconds);
            return MakeResult(id, new JObject { ["content"] = bulkContent, ["isError"] = true });
        }

        // Map tool name to REPL command
        string? commandLine = MapToolToCommand(toolName, arguments);
        if (commandLine == null)
        {
            return MakeErrorResponse(id, -32602, $"Unknown tool: {toolName}");
        }

        // Execute via CommandRegistry
        CommandResult result = _registry.Dispatch(commandLine);
        _metrics.RecordLatency(toolName, _latencySw.ElapsedMilliseconds); // MT-022
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
    private static bool RequiresMcpConfirm(string toolName) => toolName switch
    {
        "mt_algos_start_all"   => true,
        "mt_algos_stop_all"    => true,
        "mt_fleet_disconnect"  => true,
        _ => false
    };

    /// <summary>Map an MCP tool name + arguments to a REPL command string.</summary>
    private static string? MapToolToCommand(string toolName, JObject arguments)
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

            // Account (Phase A)
            "mt_account_balance" => $"account balance{profileSuffix}",
            "mt_account_orders" => $"account orders{profileSuffix}",
            "mt_account_positions" => $"account positions{profileSuffix}",
            "mt_account_executions" => $"account executions{profileSuffix}",
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

            // Exchange data queries (Phase K)
            "mt_exchange_ticker24" => $"exchange ticker24 {arguments["symbol"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_exchange_klines" => BuildKlinesCommand(arguments, profileSuffix),
            "mt_exchange_trades" => $"exchange trades {arguments["symbol"]?.Value<string>() ?? ""}{profileSuffix}",

            // Algorithms (Phase B)
            "mt_algos_list" => $"algos list{profileSuffix}",
            "mt_algos_list_all" => "algos list-all",
            "mt_algos_search" => $"algos search {arguments["query"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_get" => $"algos get {arguments["id"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_start" => $"algos start {arguments["id"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_stop" => $"algos stop {arguments["id"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_algos_start_all" => $"algos start-all{profileSuffix}",

            // MT-012: Algo verification (BUG-13 detection)
            "mt_algos_start_verified" => BuildStartVerifyCommand(arguments, profileSuffix),
            "mt_algos_verify"         => BuildVerifyCommand(arguments, profileSuffix),
            "mt_algos_stop_all" => $"algos stop-all{profileSuffix}",

            // MT-008: Batch algo operations — start/stop/config across multiple servers
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

            // Settings (Phase B)
            "mt_settings_get" => arguments.ContainsKey("key")
                ? $"settings get {arguments["key"]?.Value<string>()}{profileSuffix}"
                : $"settings get{profileSuffix}",
            "mt_settings_search" => $"settings search {arguments["query"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_settings_set" => $"settings set {arguments["key"]?.Value<string>() ?? ""} {arguments["value"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_settings_groups" => $"settings groups{profileSuffix}",

            // Import (Phase C)
            "mt_import_v2" => $"import v2 {arguments["path"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_import_templates" => "import templates",
            "mt_import_add_numeric" =>
                $"import add-numeric {arguments["id"]?.Value<string>() ?? ""} {arguments["delta"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",

            // Orders (Phase D)
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

            // Order operations (Phase K)
            "mt_orders_place" => BuildPlaceOrderCommand(arguments, profileSuffix, confirm),
            "mt_orders_move" => $"orders move {arguments["client_order_id"]?.Value<string>() ?? ""} {arguments["new_price"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_set_leverage" => $"orders set-leverage {arguments["symbol"]?.Value<string>() ?? ""} {arguments["leverage"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_set_margin_type" => $"orders set-margin-type {arguments["symbol"]?.Value<string>() ?? ""} {arguments["margin_type"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_set_position_mode" => $"orders set-position-mode {arguments["symbol"]?.Value<string>() ?? ""} {arguments["mode"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_get_position_mode" => $"orders get-position-mode {arguments["symbol"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_orders_panic_sell" => $"orders panic-sell {arguments["symbol"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_change_margin" => $"orders change-margin {arguments["symbol"]?.Value<string>() ?? ""} {arguments["position_side"]?.Value<string>() ?? "BOTH"} {arguments["amount"]?.Value<string>() ?? ""} {arguments["action"]?.Value<string>() ?? "add"}{profileSuffix}{confirm}",
            "mt_orders_transfer" => $"orders transfer {arguments["asset"]?.Value<string>() ?? ""} {arguments["amount"]?.Value<string>() ?? ""} {arguments["from"]?.Value<string>() ?? ""} {arguments["to"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_set_leverage_buysell" => $"orders set-leverage-buysell {arguments["asset"]?.Value<string>() ?? ""} {arguments["buy_leverage"]?.Value<string>() ?? ""} {arguments["sell_leverage"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_get_multiasset" => $"orders get-multiasset {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_orders_set_multiasset" => $"orders set-multiasset {arguments["enabled"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",


            // Reports (Phase F) — historical trade data
            "mt_reports_trades" => BuildReportsCommand(arguments, profileSuffix),
            "mt_reports_comments" => $"reports comments{profileSuffix}",
            "mt_reports_dates" => $"reports dates{profileSuffix}",

            // Fleet (Phase E) — fleet-wide operations
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

            // MT-004: Batch connect to specific named profiles in parallel
            "mt_fleet_batch_connect" => BuildBatchConnectCommand(arguments),

            // MT-003: Connection pool health — latency/error/reconnect metrics per profile
            "mt_connection_health" => "fleet connhealth",

            // MT-007: Server tagging — set/get fleet orchestration labels
            "mt_connection_tag" => BuildTagCommand(arguments),
            "mt_connection_tags" => profile != null ? $"tag {profile}" : "tag",

            // Monitor (Phase G) — real-time core monitoring via UDP
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

            // Blacklist (Risk Management)
            "mt_blacklist_list" => $"blacklist list{profileSuffix}",
            "mt_blacklist_add" => BuildBlacklistMutationCommand("add", arguments, profileSuffix, confirm),
            "mt_blacklist_remove" => BuildBlacklistMutationCommand("remove", arguments, profileSuffix, confirm),

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
            "mt_marketdata_ticker" => $"marketdata ticker {arguments["market"]?.Value<string>() ?? ""}{profileSuffix}",
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
            "mt_profile_settings_update" => $"settings profile-update {arguments["profile_name"]?.Value<string>() ?? ""} {arguments["updates_json"]?.Value<string>() ?? ""}{profileSuffix}",
            "mt_core_restart" => $"core restart{profileSuffix}{confirm}",
            "mt_core_restart_update" => $"core restart-update{profileSuffix}{confirm}",
            "mt_core_clear_orders" => $"core clear-orders{profileSuffix}{confirm}",
            "mt_core_clear_archive" => $"core clear-archive{profileSuffix}{confirm}",

            "mt_orders_close_by_tpsl" => $"orders close-by-tpsl {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""} {arguments["side"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_orders_reset_tpsl" => $"orders reset-tpsl {arguments["symbol"]?.Value<string>() ?? ""} {arguments["market"]?.Value<string>() ?? ""} {arguments["side"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",

            "mt_tpsl_join" => $"tpsl join {arguments["tpsl_ids"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",
            "mt_tpsl_split" => $"tpsl split {arguments["tpsl_id"]?.Value<string>() ?? ""}{profileSuffix}{confirm}",

            "mt_funding_request" => $"funding request{profileSuffix}",

            "mt_buylimit_request" => $"buylimit request {arguments["amount"]?.Value<string>() ?? ""}{profileSuffix}",

            _ => null
        };
    }
    /// <summary>
    /// Build the REPL command string for mt_connection_tag (MT-007).
    /// Format: tag <profile> <key> <value>
    /// </summary>
    /// <summary>Build: algos start-verify <id> [wait_secs] [@profile] (MT-012)</summary>
    /// <summary>Build: algos verify <id> [@profile] (MT-012)</summary>
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
    /// Build: fleet batchstart/batchstop <algo> [profile1 ...] (MT-008)
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
    /// Build: fleet batchconfig <algo> <key> <value> [profile1 ...] (MT-008)
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
    private static IEnumerable<JObject> GetToolDefinitions()
    {
        // ── MT-005: Event streaming tools ──
        foreach (var t in GetEventToolDefinitions()) yield return t;
        foreach (var t in GetInternalToolDefinitions()) yield return t;

        // ── Connection ──
        yield return Tool("mt_connect", "Connect to an MT-Core server using a saved profile",
            Prop("profile", "string", "Profile name (e.g. bnc_001)", required: true));
        yield return Tool("mt_disconnect", "Disconnect from a server",
            Prop("profile", "string", "Profile name to disconnect", required: true));
        yield return Tool("mt_status", "Show all connection statuses");
        yield return Tool("mt_use", "Switch active connection",
            Prop("profile", "string", "Profile name to activate", required: true));

        // ── Account ──
        yield return Tool("mt_account_balance", "Get account balances",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_account_orders", "Get active orders",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_account_positions", "Get open positions",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_account_executions", "Get recent trade executions",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_account_info", "Get account info",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_account_summary", "Get account summary",
            Prop("profile", "string", "Target server profile"));

        // ── Core Status ──
        yield return Tool("mt_core_status", "Get core server status (CPU, memory, latency)",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_core_license", "Get license info",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_core_health", "Get server health assessment",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_core_dashboard", "Get multi-server dashboard",
            Prop("profile", "string", "Target server profile"));

        // ── Exchange ──
        yield return Tool("mt_exchange_summary", "Get exchange info summary",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_exchange_pairs", "List trade pairs",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_exchange_search", "Search trade pairs",
            Prop("query", "string", "Search query", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_exchange_pair_detail", "Get detailed info for a specific trade pair",
            Prop("symbol", "string", "Symbol name (e.g. BTCUSDT)", required: true),
            Prop("profile", "string", "Target server profile"));

        // ── Exchange Data (Phase K) ──
        yield return Tool("mt_exchange_ticker24",
            "Get 24h ticker price statistics for a symbol. Returns price change, high/low, volume, trade count.",
            Prop("symbol", "string", "Symbol (e.g. BTCUSDT)", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_exchange_klines",
            "Get candlestick/kline data for a symbol. Returns OHLCV data.",
            Prop("symbol", "string", "Symbol (e.g. BTCUSDT)", required: true),
            Prop("interval", "string", "Candle interval: 1s,1m,3m,5m,15m,30m,1h,2h,4h,6h,12h,1d,3d,1w,1M (default: 1h)"),
            Prop("limit", "string", "Number of candles to return, 1-1000 (default: 100)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_exchange_trades",
            "Get recent trades for a symbol from the exchange.",
            Prop("symbol", "string", "Symbol (e.g. BTCUSDT)", required: true),
            Prop("profile", "string", "Target server profile"));

        // ── Algorithms ──
        yield return Tool("mt_algos_list", "List algorithms on active connection",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_list_all", "List algorithms across ALL connections");
        yield return Tool("mt_algos_search", "Search algorithms by name/signature/symbol",
            Prop("query", "string", "Search query", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_get", "Get algorithm details",
            Prop("id", "string", "Algorithm ID", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_start", "Start an algorithm",
            Prop("id", "string", "Algorithm ID", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_stop", "Stop an algorithm",
            Prop("id", "string", "Algorithm ID", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_start_all",
            "Start all algorithms (requires confirm=true). Bulk operation: starts every algo on the target server.",
            Prop("confirm", "boolean", "Must be true to actually start all", required: true),
            Prop("profile", "string", "Target server profile"));

        // MT-012: Algo verification — BUG-13 (Silent Init Failure) detection
        yield return Tool("mt_algos_start_verified",
            "Start an algorithm and verify it initialized successfully (MT-012 / BUG-13 mitigation). " +
            "Waits wait_secs seconds then checks isRunning, symbol, and marketType. " +
            "Returns status: VERIFIED | BUG13_SUSPECTED | RUNNING_UNCONFIRMED | NOT_RUNNING.",
            Prop("id",         "string", "Algorithm ID to start",                  required: true),
            Prop("wait_secs",  "string", "Seconds to wait for init (1-30, default 4)"),
            Prop("profile",    "string", "Target server profile"));
        yield return Tool("mt_algos_verify",
            "Verify current state of a running algorithm — checks for BUG-13 pattern " +
            "(isRunning=true but symbol/market unresolved). Does NOT start the algo.",
            Prop("id",      "string", "Algorithm ID to inspect", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_stop_all",
            "Stop all algorithms (requires confirm=true). Bulk operation: stops every running algo on the target server.",
            Prop("confirm", "boolean", "Must be true to actually stop all", required: true),
            Prop("profile", "string", "Target server profile"));

        // MT-008: Batch algo operations — start/stop/config across multiple servers
        yield return Tool("mt_algos_batch_start",
            "Start an algorithm (matched by name/signature/symbol pattern) across multiple servers in parallel. " +
            "Searches each server for algos matching the pattern and starts all matches. " +
            "Use mt_algos_batch_stop to reverse. SAFETY: requires either explicit profiles or all_servers=true.",
            Prop("algo",        "string",  "Algo name or pattern to match (name/signature/symbol substring)", required: true),
            Prop("profiles",    "array",   "List of profile names to target (string or array)"),
            Prop("all_servers", "boolean", "Must be true to target ALL connected servers when profiles is omitted"));
        yield return Tool("mt_algos_batch_stop",
            "Stop an algorithm (matched by name/signature/symbol pattern) across multiple servers in parallel.",
            Prop("algo",     "string", "Algo name or pattern to match", required: true),
            Prop("profiles", "array",  "List of profile names (optional — omit for ALL connected servers)"));
        yield return Tool("mt_algos_batch_config",
            "Set a config parameter on matching algorithms across multiple servers. " +
            "Changes are LOCAL — call algos save <id> @<profile> to persist to each Core.",
            Prop("algo",     "string", "Algo name or pattern to match", required: true),
            Prop("key",      "string", "Config parameter key",                  required: true),
            Prop("value",    "string", "New value for the config parameter",     required: true),
            Prop("profiles", "array",  "List of profile names (optional — omit for ALL connected servers)"));
        yield return Tool("mt_algos_save", "Save algorithm config changes",
            Prop("id", "string", "Algorithm ID", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_save_start", "Save and start an algorithm",
            Prop("id", "string", "Algorithm ID", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_delete", "Delete an algorithm (requires confirm=true)",
            Prop("id", "string", "Algorithm ID", required: true),
            Prop("confirm", "boolean", "Must be true to actually delete", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_toggle_debug", "Toggle debug/profiling mode",
            Prop("id", "string", "Algorithm ID", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_rename", "Rename an algorithm",
            Prop("id", "string", "Algorithm ID", required: true),
            Prop("name", "string", "New name", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_config", "View algorithm configuration parameters",
            Prop("id", "string", "Algorithm ID", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_config_set", "Set an algorithm config parameter",
            Prop("id", "string", "Algorithm ID", required: true),
            Prop("key", "string", "Parameter key", required: true),
            Prop("value", "string", "New value", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_groups", "List algorithm groups",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_group", "List algorithms in a group",
            Prop("group_id", "string", "Group ID", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_clone_group", "Clone an algorithm group",
            Prop("group_id", "string", "Group ID", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_delete_group", "Delete an algorithm group (requires confirm=true)",
            Prop("group_id", "string", "Group ID", required: true),
            Prop("confirm", "boolean", "Must be true to actually delete", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_copy",
            "Copy an algorithm from one server to another (requires confirm=true)",
            Prop("id", "string", "Algorithm ID to copy", required: true),
            Prop("source_profile", "string", "Source server profile (default: active connection)"),
            Prop("destination_profile", "string", "Destination server profile", required: true),
            Prop("confirm", "boolean", "Must be true to actually copy"));
        yield return Tool("mt_algos_export",
            "Export an algorithm as portable JSON for cross-server transfer",
            Prop("id", "string", "Algorithm ID", required: true),
            Prop("profile", "string", "Target server profile"));

        // ── Settings ──
        yield return Tool("mt_settings_get", "Get profile settings (all or specific key)",
            Prop("key", "string", "Specific setting key (optional)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_settings_search", "Search settings by keyword",
            Prop("query", "string", "Search query", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_settings_set", "Set a profile setting (requires confirm=true)",
            Prop("key", "string", "Setting key", required: true),
            Prop("value", "string", "New value", required: true),
            Prop("confirm", "boolean", "Must be true to actually change"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_settings_groups", "List settings grouped by prefix",
            Prop("profile", "string", "Target server profile"));

        // ── Import ──
        yield return Tool("mt_import_templates", "List available algorithm templates");
        yield return Tool("mt_import_v2", "Import algorithms from V2 text format file",
            Prop("path", "string", "Path to V2 format file", required: true),
            Prop("confirm", "boolean", "Must be true to actually create on server"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_import_add_numeric",
            "Add numeric delta to all numeric params of an algorithm",
            Prop("id", "string", "Algorithm ID", required: true),
            Prop("delta", "string", "Numeric delta (e.g. 1.0 or -0.5)", required: true),
            Prop("confirm", "boolean", "Must be true to actually modify and save"),
            Prop("profile", "string", "Target server profile"));

        // ── Orders ──
        yield return Tool("mt_orders_list", "List active orders",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_positions", "List open positions with PnL",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_cancel", "Cancel a specific order (requires confirm=true)",
            Prop("client_order_id", "string", "Client order ID", required: true),
            Prop("confirm", "boolean", "Must be true to actually cancel"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_cancel_all", "Cancel all orders (requires confirm=true)",
            Prop("symbol", "string", "Specific symbol (optional, all if omitted)"),
            Prop("confirm", "boolean", "Must be true to actually cancel"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_close", "Close a position (requires confirm=true)",
            Prop("symbol", "string", "Position symbol", required: true),
            Prop("percentage", "string", "Percentage to close (0-100, default 100)"),
            Prop("confirm", "boolean", "Must be true to actually close"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_close_all", "Close all positions (requires confirm=true)",
            Prop("confirm", "boolean", "Must be true to actually close all"),
            Prop("profile", "string", "Target server profile"));

        // ── Order Operations (Phase K) ──
        yield return Tool("mt_orders_place",
            "Place a new order (market or limit). Requires confirm=true. " +
            "If price is omitted, places a MARKET order. If price is set, places a LIMIT order.",
            Prop("symbol", "string", "Symbol (e.g. BTCUSDT)", required: true),
            Prop("side", "string", "Order side: BUY or SELL", required: true),
            Prop("qty", "string", "Order quantity", required: true),
            Prop("price", "string", "Limit price (omit for market order)"),
            Prop("type", "string", "Order type: MARKET or LIMIT (auto-detected from price)"),
            Prop("tif", "string", "Time in force: GTC, IOC, FOK (default: GTC)"),
            Prop("reduce_only", "boolean", "Reduce-only order"),
            Prop("confirm", "boolean", "Must be true to actually place"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_move",
            "Move/modify price of an existing order (requires confirm=true)",
            Prop("client_order_id", "string", "Client order ID of the order to move", required: true),
            Prop("new_price", "string", "New price for the order", required: true),
            Prop("confirm", "boolean", "Must be true to actually move"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_set_leverage",
            "Set leverage for a symbol (requires confirm=true)",
            Prop("symbol", "string", "Symbol (e.g. BTCUSDT)", required: true),
            Prop("leverage", "string", "Leverage value (1-125)", required: true),
            Prop("confirm", "boolean", "Must be true to actually change"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_set_margin_type",
            "Set margin type CROSS or ISOLATED for a symbol (requires confirm=true)",
            Prop("symbol", "string", "Symbol (e.g. BTCUSDT)", required: true),
            Prop("margin_type", "string", "Margin type: CROSS or ISOLATED", required: true),
            Prop("confirm", "boolean", "Must be true to actually change"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_set_position_mode",
            "Set position mode HEDGE or ONE_WAY for a symbol (requires confirm=true)",
            Prop("symbol", "string", "Symbol (e.g. BTCUSDT)", required: true),
            Prop("mode", "string", "Position mode: HEDGE or ONE_WAY", required: true),
            Prop("confirm", "boolean", "Must be true to actually change"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_get_position_mode",
            "Get current position mode (HEDGE/ONE_WAY) for a symbol",
            Prop("symbol", "string", "Symbol (e.g. BTCUSDT)", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_panic_sell",
            "EMERGENCY: Market-close all positions for a symbol immediately (requires confirm=true)",
            Prop("symbol", "string", "Symbol to panic sell", required: true),
            Prop("confirm", "boolean", "Must be true to execute panic sell"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_change_margin",
            "Add or reduce isolated margin on a position (requires confirm=true)",
            Prop("symbol", "string", "Symbol (e.g. BTCUSDT)", required: true),
            Prop("position_side", "string", "Position side: LONG, SHORT, or BOTH", required: true),
            Prop("amount", "string", "Margin amount to add/reduce", required: true),
            Prop("action", "string", "Action: add or reduce (default: add)"),
            Prop("confirm", "boolean", "Must be true to actually change"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_transfer",
            "Transfer funds between SPOT and FUTURES accounts (requires confirm=true)",
            Prop("asset", "string", "Asset to transfer (e.g. USDT)", required: true),
            Prop("amount", "string", "Amount to transfer", required: true),
            Prop("from", "string", "Source: SPOT or FUTURES", required: true),
            Prop("to", "string", "Destination: SPOT or FUTURES", required: true),
            Prop("confirm", "boolean", "Must be true to actually transfer"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_set_leverage_buysell",
            "Set different buy and sell leverage for an asset (Bybit split leverage). Requires confirm=true.",
            Prop("asset", "string", "Asset/symbol (e.g. BTCUSDT)", required: true),
            Prop("buy_leverage", "string", "Buy leverage (e.g. 10)", required: true),
            Prop("sell_leverage", "string", "Sell leverage (e.g. 5)", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("confirm", "boolean", "Must be true to proceed", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_get_multiasset",
            "Query multi-asset margin mode status (enabled/disabled).",
            Prop("market", "string", "Market type: FUTURES (default)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_set_multiasset",
            "Enable or disable multi-asset margin mode. Requires confirm=true.",
            Prop("enabled", "string", "true or false", required: true),
            Prop("market", "string", "Market type: FUTURES (default)"),
            Prop("confirm", "boolean", "Must be true to proceed", required: true),
            Prop("profile", "string", "Target server profile"));

        // ── Reports ──
        yield return Tool("mt_reports_trades",
            "Get trade reports: closed positions with P&L, fees, entry/exit prices. " +
            "This is the HISTORICAL trading data — completed trades, not live fills. " +
            "Use period shortcuts (today/24h/7d/30d/90d) or custom date range.",
            Prop("period", "string", "Time period: today, 24h, 7d, 30d, or 90d (default: 24h)"),
            Prop("from", "string", "Custom start date (YYYY-MM-DD), overrides period"),
            Prop("to", "string", "Custom end date (YYYY-MM-DD)"),
            Prop("symbol", "string", "Filter by symbol (e.g. BTCUSDT)"),
            Prop("algo", "string", "Filter by algorithm name"),
            Prop("sig", "string", "Filter by algorithm signature"),
            Prop("metrics", "boolean", "Include market context snapshots per trade (depth, deltas, funding, mark price at trigger/fill time)"),
            Prop("exclude_emulated", "boolean", "Exclude emulated/paper trades"),
            Prop("closed_by", "string", "Filter by close reason: TP,SL,TS,LIQ,PANIC,AUTO,MARKET,LIMIT,FUNDING,LICENSE (comma-separated)"),
            Prop("market", "string", "Filter by market type: FUTURES,SPOT (comma-separated)"),
            Prop("side", "string", "Filter by order side: BUY,SELL"),
            Prop("mode", "string", "Filter by trade mode: REAL or EMULATED"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_reports_comments",
            "Get report comment labels used in trade reports",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_reports_dates",
            "Get available report date markers",
            Prop("profile", "string", "Target server profile"));

        // ── Fleet ──
        yield return Tool("mt_fleet_connect",
            "Connect to ALL configured server profiles at once (or filter by exchange/name). " +
            "Returns connection status for each. Use this instead of multiple mt_connect calls.",
            Prop("filter", "string", "Optional filter: exchange name (e.g. 'BINANCE') or profile name pattern (e.g. 'bnc')"));
        yield return Tool("mt_fleet_status",
            "Get connection status overview for ALL servers in one call. " +
            "Shows online/offline, uptime, algo counts per server.");
        yield return Tool("mt_fleet_balances",
            "Get balances across ALL connected servers in one call. " +
            "Shows per-server USDT totals, asset counts, top holdings, and grand total.");
        yield return Tool("mt_fleet_positions",
            "Get ALL open positions across ALL connected servers. " +
            "Shows symbol, side, entry, PnL per position with server attribution.");
        yield return Tool("mt_fleet_algos",
            "Get algorithm summary across ALL connected servers. " +
            "Shows total/running counts per server, grouped by algo type.");
        yield return Tool("mt_fleet_health",
            "Health check ALL connected servers. " +
            "Shows CPU, RAM, latency, UDS status, license per server with issue flags.");
        yield return Tool("mt_fleet_summary",
            "Comprehensive fleet overview in ONE call — the mega-dashboard. " +
            "Grand total balance, PnL, algos, positions, per-exchange breakdown. " +
            "Use this for periodic fleet status reports.");
        yield return Tool("mt_fleet_disconnect",
            "Disconnect from ALL servers at once (requires confirm=true). Fleet-wide operation.",
            Prop("confirm", "boolean", "Must be true to actually disconnect all", required: true));

        // MT-004
        yield return Tool("mt_fleet_batch_connect",
            "Connect to a specific set of named profiles in parallel (max 10 concurrent). " +
            "Unlike mt_fleet_connect (which connects ALL configured profiles), this accepts an " +
            "explicit list — suited for targeted fleet orchestration by AI agents.",
            Prop("profiles", "array", "Array of profile names to connect to"));

        // MT-003
        yield return Tool("mt_connection_health",
            "Connection pool health report — per-profile latency, error count, reconnect history, " +
            "and backoff state. Use this to diagnose unstable connections and route around degraded servers.");


        // MT-007: Server tagging — fleet orchestration labels per connection
        yield return Tool("mt_connection_tag",
            "Set a fleet orchestration tag (key/value) on a named connection. " +
            "Tags are in-memory labels like role=coordinator, strategy=scalper, group=us-east. " +
            "Use mt_connection_tags to read them back.",
            Prop("profile", "string", "Connection profile name", required: true),
            Prop("key",     "string", "Tag key (e.g. role, strategy, group, region)", required: true),
            Prop("value",   "string", "Tag value (e.g. coordinator, scalper, us-east, prod)", required: true));
        yield return Tool("mt_connection_tags",
            "List fleet orchestration tags for a connection or all connections. " +
            "Returns a map of key→value labels set via mt_connection_tag.",
            Prop("profile", "string", "Connection profile name (optional — omit for all connections)"));
        // ── Monitor (Phase G) — real-time core monitoring via UDP ──
        yield return Tool("mt_monitor_start",
            "Start real-time core monitoring. Collects CPU, memory, threads, latency snapshots " +
            "via UDP CoreStatusSubscription. Works with remote cores — no filesystem access needed.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_monitor_stop",
            "Stop core monitoring and release the snapshot buffer.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_monitor_status",
            "Get monitor status: running state, snapshots collected, buffer capacity, latest metrics.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_monitor_health",
            "Health assessment with trend analysis. Checks CPU, memory, threads, exchange latency, " +
            "UDS data streams, and detects memory/thread growth trends. Returns HEALTHY/WARNING/CRITICAL.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_monitor_performance",
            "Get time-series performance snapshots. Each snapshot includes CPU, memory, threads, " +
            "latency, UDS status. Start monitor first for history.",
            Prop("count", "string", "Number of snapshots to return (default: 10, max: 100)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_monitor_stats",
            "Aggregate statistics over the monitoring window: min/max/avg for CPU, memory, threads, " +
            "latency. Shows trends and sample count.",
            Prop("profile", "string", "Target server profile"));

        // ── AutoStops (Risk Management) ──
        yield return Tool("mt_autostops_list",
            "List auto-stop algorithm configurations and status. Shows balance/report filters and thresholds.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autostops_baseline",
            "Request auto-stop baseline recalculation on Core (fire-and-forget).",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autostops_reports",
            "Get report data for auto-stop algorithms. Optionally filter by algorithm IDs.",
            Prop("ids", "string", "Comma-separated algorithm IDs (optional — omit for all)"),
            Prop("profile", "string", "Target server profile"));

        // ── Blacklist (Risk Management) ──
        yield return Tool("mt_blacklist_list",
            "List current blacklist configuration: blocked markets, quote assets, and symbols.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_blacklist_add",
            "Add an item to the blacklist. type=market needs market_type only; " +
            "type=quote needs market_type+quote_asset; type=symbol needs market_type+quote_asset+symbol. " +
            "Requires confirm=true.",
            Prop("type", "string", "Filter type: market, quote, or symbol", required: true),
            Prop("market_type", "string", "Market type: SPOT, MARGIN, FUTURES, or DELIVERY", required: true),
            Prop("quote_asset", "string", "Quote asset (e.g. usdt, busd) — required for type=quote and type=symbol"),
            Prop("symbol", "string", "Symbol (e.g. btcusdt) — required for type=symbol"),
            Prop("confirm", "boolean", "Must be true to proceed", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_blacklist_remove",
            "Remove an item from the blacklist. type=market needs market_type only; " +
            "type=quote needs market_type+quote_asset; type=symbol needs market_type+quote_asset+symbol. " +
            "Requires confirm=true.",
            Prop("type", "string", "Filter type: market, quote, or symbol", required: true),
            Prop("market_type", "string", "Market type: SPOT, MARGIN, FUTURES, or DELIVERY", required: true),
            Prop("quote_asset", "string", "Quote asset — required for type=quote and type=symbol"),
            Prop("symbol", "string", "Symbol — required for type=symbol"),
            Prop("confirm", "boolean", "Must be true to proceed", required: true),
            Prop("profile", "string", "Target server profile"));

        // ── TPSL (Take Profit / Stop Loss) ──
        yield return Tool("mt_tpsl_list",
            "List all TPSL (Take Profit / Stop Loss) positions. Requires active TPSL subscription.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_tpsl_subscribe",
            "Subscribe to TPSL position updates from Core. Data available via mt_tpsl_list.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_tpsl_unsubscribe",
            "Unsubscribe from TPSL position updates.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_tpsl_cancel",
            "Cancel a TPSL position by ID. Requires confirm=true.",
            Prop("id", "string", "TPSL position ID", required: true),
            Prop("confirm", "boolean", "Must be true to proceed", required: true),
            Prop("profile", "string", "Target server profile"));

        // ── Trading Performance ──
        yield return Tool("mt_perf_list",
            "List trading performance data. Requires active performance subscription.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_perf_subscribe",
            "Subscribe to trading performance updates from Core.",
            Prop("market", "string", "Market type: FUTURES, SPOT, INVERSE (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_perf_unsubscribe",
            "Unsubscribe from trading performance updates.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_perf_request",
            "Request trading performance data refresh or reset.",
            Prop("action", "string", "Action: refresh or reset (default: refresh)"),
            Prop("profile", "string", "Target server profile"));

        // ── Reports Enhancement (CSV Export & Store) ──
        yield return Tool("mt_reports_export",
            "Export trade reports to CSV file. Supports all standard report filters. " +
            "Returns the file path of the exported CSV.",
            Prop("period", "string", "Time period: today, 24h, 7d, 30d, 90d (default: 24h)"),
            Prop("symbol", "string", "Symbol filter (e.g. BTCUSDT)"),
            Prop("algo", "string", "Algorithm name filter"),
            Prop("sig", "string", "Signature filter"),
            Prop("path", "string", "Output file path (default: ~/mt-reports/reports_TIMESTAMP.csv)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_reports_fleet_export",
            "Export trade reports from ALL connected servers merged into a single CSV file. " +
            "Trades are sorted by close time across all servers. Ideal for consolidated P&L analysis.",
            Prop("period", "string", "Time period: today, 24h, 7d, 30d, 90d (default: 24h)"),
            Prop("symbol", "string", "Symbol filter (e.g. BTCUSDT)"),
            Prop("path", "string", "Output file path (default: ~/mt-reports/reports_TIMESTAMP.csv)"));
        yield return Tool("mt_reports_store",
            "Store trade report query results locally with a name. " +
            "Stored sets can be retrieved, displayed, and exported later without re-querying Core.",
            Prop("name", "string", "Name for the stored report set", required: true),
            Prop("period", "string", "Time period: today, 24h, 7d, 30d, 90d (default: 24h)"),
            Prop("symbol", "string", "Symbol filter"),
            Prop("all_servers", "string", "Set to true to query all connected servers"),
            Prop("profile", "string", "Target server profile (ignored if all_servers=true)"));
        yield return Tool("mt_reports_stored",
            "List all locally stored report sets with summary statistics. " +
            "Shows name, server, trade count, PnL, win rate, capture time.");
        yield return Tool("mt_reports_load",
            "Load and display a stored report set by name. Shows trade table and summary stats.",
            Prop("name", "string", "Name of the stored report set", required: true));
        yield return Tool("mt_reports_delete",
            "Delete a stored report set by name.",
            Prop("name", "string", "Name of the stored report set to delete", required: true));

        // ── Fleet P4 Extensions ──
        yield return Tool("mt_fleet_autostops",
            "Query auto-stop configuration across ALL connected servers. " +
            "Shows which servers have balance/report filters configured.");
        yield return Tool("mt_fleet_blacklist",
            "Query blacklist configuration across ALL connected servers. " +
            "Shows market/quote/symbol filter counts per server.");
        yield return Tool("mt_fleet_perf",
            "Query trading performance subscription status across ALL connected servers. " +
            "Shows entry counts and subscription state per server.");
        yield return Tool("mt_fleet_reports",
            "Query trade reports across ALL connected servers with per-server P&L breakdown. " +
            "Shows trades, PnL, fees, win rate, volume per server with fleet totals.",
            Prop("period", "string", "Time period: today, 7d, 30d (default: 24h)"));


        // ── Notifications ──
        yield return Tool("mt_notifications_list",
            "List cached notifications from Core. Shows type, time, and message.",
            Prop("count", "string", "Number of notifications to show (default: 50)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_notifications_subscribe",
            "Subscribe to real-time notifications from Core (deal complete, order fill, liquidation, alerts, errors).",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_notifications_unsubscribe",
            "Unsubscribe from notifications.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_notifications_clear",
            "Clear cached notification history for a connection.",
            Prop("profile", "string", "Target server profile"));

        // ── Market Data ──
        yield return Tool("mt_marketdata_status",
            "Show all active market data subscriptions (trades, depth, mark price, klines, tickers).",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_trades",
            "View recent trade data for a symbol. Requires active trade subscription.",
            Prop("symbol", "string", "Trading pair (e.g. BTCUSDT)", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_trades_subscribe",
            "Subscribe to real-time trade feed for a symbol.",
            Prop("symbol", "string", "Trading pair (e.g. BTCUSDT)", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_trades_unsubscribe",
            "Unsubscribe from trade feed for a symbol.",
            Prop("symbol", "string", "Trading pair (e.g. BTCUSDT)", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_depth",
            "View order book (top 10 bids/asks) for a symbol. Requires active depth subscription.",
            Prop("symbol", "string", "Trading pair (e.g. BTCUSDT)", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_depth_subscribe",
            "Subscribe to real-time order book (depth) feed for a symbol.",
            Prop("symbol", "string", "Trading pair (e.g. BTCUSDT)", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_depth_unsubscribe",
            "Unsubscribe from depth feed for a symbol.",
            Prop("symbol", "string", "Trading pair (e.g. BTCUSDT)", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_markprice",
            "View mark price, funding rate, and next funding time for a symbol.",
            Prop("symbol", "string", "Trading pair (e.g. BTCUSDT)", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_markprice_subscribe",
            "Subscribe to real-time mark price and funding rate updates for a symbol.",
            Prop("symbol", "string", "Trading pair (e.g. BTCUSDT)", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_markprice_unsubscribe",
            "Unsubscribe from mark price feed for a symbol.",
            Prop("symbol", "string", "Trading pair (e.g. BTCUSDT)", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_klines",
            "View last kline (candlestick) data for a symbol and interval.",
            Prop("symbol", "string", "Trading pair (e.g. BTCUSDT)", required: true),
            Prop("interval", "string", "Kline interval: 1s, 1m, 3m, 5m, 15m, 30m, 1h, 2h, 4h, 6h, 12h, 1d, 3d, 1w, 1mo", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_klines_subscribe",
            "Subscribe to real-time kline (candlestick) updates for a symbol and interval.",
            Prop("symbol", "string", "Trading pair (e.g. BTCUSDT)", required: true),
            Prop("interval", "string", "Kline interval: 1s, 1m, 5m, 15m, 1h, 4h, 1d", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_klines_unsubscribe",
            "Unsubscribe from kline feed for a symbol and interval.",
            Prop("symbol", "string", "Trading pair (e.g. BTCUSDT)", required: true),
            Prop("interval", "string", "Kline interval: 1s, 1m, 5m, 15m, 1h, 4h, 1d", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_ticker",
            "View ticker data (last price, volume) for all symbols. Requires active ticker subscription.",
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_ticker_subscribe",
            "Subscribe to real-time ticker updates for ALL symbols on a market.",
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_ticker_unsubscribe",
            "Unsubscribe from ticker feed.",
            Prop("market", "string", "Market type: FUTURES, SPOT (default: FUTURES)"),
            Prop("profile", "string", "Target server profile"));

        // ── Alerts ──
        yield return Tool("mt_alerts_list",
            "List active price alerts with conditions and status.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_alerts_subscribe",
            "Subscribe to real-time alert updates (new, modified, deleted alerts).",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_alerts_unsubscribe",
            "Unsubscribe from alert updates.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_alerts_history",
            "View alert trigger history.",
            Prop("count", "string", "Number of history entries to show (default: 50)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_alerts_history_subscribe",
            "Subscribe to alert history updates.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_alerts_history_unsubscribe",
            "Unsubscribe from alert history updates.",
            Prop("profile", "string", "Target server profile"));

        // Profiling
        yield return Tool("mt_profiling_subscribe",
            "Subscribe to real-time algorithm profiling data stream.",
            Prop("symbol", "string", "Trading pair symbol", true),
            Prop("algo_id", "string", "Algorithm ID", true),
            Prop("market", "string", "Market type (FUTURES/SPOT)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_profiling_unsubscribe",
            "Unsubscribe from algorithm profiling data stream.",
            Prop("symbol", "string", "Trading pair symbol", true),
            Prop("algo_id", "string", "Algorithm ID", true),
            Prop("market", "string", "Market type (FUTURES/SPOT)"),
            Prop("profile", "string", "Target server profile"));

        // Triggers
        yield return Tool("mt_triggers_list",
            "List received trigger events.",
            Prop("count", "integer", "Number of recent entries to show"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_triggers_subscribe",
            "Subscribe to trigger events.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_triggers_unsubscribe",
            "Unsubscribe from trigger events.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_triggers_save",
            "Save/create a trigger action.",
            Prop("data", "string", "Trigger action JSON data", true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_triggers_delete",
            "Delete a trigger action.",
            Prop("data", "string", "Trigger action JSON data", true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_triggers_start",
            "Start a trigger action.",
            Prop("data", "string", "Trigger action JSON data", true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_triggers_stop",
            "Stop a trigger action.",
            Prop("data", "string", "Trigger action JSON data", true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_triggers_start_all",
            "Start all trigger actions.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_triggers_stop_all",
            "Stop all trigger actions.",
            Prop("profile", "string", "Target server profile"));

        // LiveMarkets
        yield return Tool("mt_livemarkets_list",
            "List live market metrics data.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_livemarkets_subscribe",
            "Subscribe to live market metrics streaming.",
            Prop("symbol", "string", "Filter by symbol (optional)"),
            Prop("market", "string", "Market type (FUTURES/SPOT)"),
            Prop("quote_asset", "string", "Filter by quote asset (e.g. USDT)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_livemarkets_unsubscribe",
            "Unsubscribe from live market metrics.",
            Prop("symbol", "string", "Symbol to unsubscribe"),
            Prop("market", "string", "Market type"),
            Prop("quote_asset", "string", "Quote asset"),
            Prop("profile", "string", "Target server profile"));

        // AutoBuy
        yield return Tool("mt_autobuy_list",
            "List AutoBuy (DCA/recurring buy) events.",
            Prop("count", "integer", "Number of recent entries"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autobuy_subscribe",
            "Subscribe to AutoBuy events.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autobuy_unsubscribe",
            "Unsubscribe from AutoBuy events.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autobuy_save",
            "Save/create an AutoBuy configuration.",
            Prop("data", "string", "AutoBuy config JSON data", true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autobuy_delete",
            "Delete an AutoBuy configuration.",
            Prop("data", "string", "AutoBuy ID JSON data", true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autobuy_start",
            "Start an AutoBuy configuration.",
            Prop("data", "string", "AutoBuy ID JSON data", true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autobuy_stop",
            "Stop an AutoBuy configuration.",
            Prop("data", "string", "AutoBuy ID JSON data", true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autobuy_refresh_pairs",
            "Refresh AutoBuy asset pair lists.",
            Prop("profile", "string", "Target server profile"));

        // GraphTool
        yield return Tool("mt_graphtool_list",
            "List graph tool (chart drawing) events.",
            Prop("count", "integer", "Number of recent entries"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_graphtool_subscribe",
            "Subscribe to graph tool events.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_graphtool_unsubscribe",
            "Unsubscribe from graph tool events.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_graphtool_save",
            "Save a graph tool (chart drawing).",
            Prop("data", "string", "Graph tool JSON data", true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_graphtool_delete",
            "Delete a graph tool (chart drawing).",
            Prop("data", "string", "Graph tool JSON data", true),
            Prop("profile", "string", "Target server profile"));

        // Signals
        yield return Tool("mt_signals_send",
            "Send an external trading signal to MTCore for automated execution.",
            Prop("symbol", "string", "Trading pair symbol", true),
            Prop("side", "string", "Order side: BUY or SELL", true),
            Prop("price", "string", "Signal price", true),
            Prop("market", "string", "Market type (FUTURES/SPOT)"),
            Prop("take_profit", "string", "Take profit percentage"),
            Prop("stop_loss", "string", "Stop loss percentage"),
            Prop("channel", "string", "Signal channel ID"),
            Prop("profile", "string", "Target server profile"));

        // Dust
        yield return Tool("mt_dust_get",
            "Get dust (small balance) information for potential conversion.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_dust_convert",
            "Convert dust (small balances) to main asset.",
            Prop("profile", "string", "Target server profile"));

        // Deposit
        yield return Tool("mt_deposit_info",
            "Get deposit information for a coin (networks, limits).",
            Prop("coin", "string", "Coin symbol (e.g. BTC, ETH)", true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_deposit_address",
            "Get deposit address for a coin and network.",
            Prop("coin", "string", "Coin symbol (e.g. BTC, ETH)", true),
            Prop("network", "string", "Network name", true),
            Prop("profile", "string", "Target server profile"));

        // Extended Order Operations
        yield return Tool("mt_orders_move_batch",
            "Move multiple orders to new prices in a single batch.",
            Prop("orders_json", "string", "JSON object: {clientOrderId: newPrice, ...}", true),
            Prop("market", "string", "Market type (FUTURES/SPOT)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_join",
            "Join (merge) split orders back into one.",
            Prop("client_order_id", "string", "Client order ID to join", true),
            Prop("market", "string", "Market type (FUTURES/SPOT)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_split",
            "Split an order into multiple smaller orders.",
            Prop("client_order_id", "string", "Client order ID to split", true),
            Prop("count", "string", "Number of parts to split into"),
            Prop("percentage", "string", "Percentage distribution per split"),
            Prop("market", "string", "Market type (FUTURES/SPOT)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_fund_transfer",
            "Transfer funds between accounts (FUNDING <-> TRADING).",
            Prop("from_account", "string", "Source: FUNDING or TRADING", true),
            Prop("asset", "string", "Asset to transfer (e.g. USDT)", true),
            Prop("amount", "string", "Amount to transfer", true),
            Prop("to_account", "string", "Destination: FUNDING or TRADING", true),
            Prop("confirm", "boolean", "Must be true to apply"),
            Prop("profile", "string", "Target server profile"));

        // Extended Exchange Data

        // Extended Reports

        // Profile Settings
        yield return Tool("mt_profile_settings_get",
            "Get profile-level settings (all server configuration key-values).",
            Prop("profile_name", "string", "Profile name (empty for current)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_profile_settings_update",
            "Update profile settings (server configuration).",
            Prop("profile_name", "string", "Profile name to update", true),
            Prop("updates_json", "string", "JSON object of key-value updates", true),
            Prop("profile", "string", "Target server profile"));
    }


    #region MCP Tool Builder Helpers

    private static JObject Tool(string name, string description, params JObject[] properties)
    {
        var props = new JObject();
        var required = new JArray();

        foreach (JObject p in properties)
        {
            string? propName = p["_name"]!.Value<string>()!;
            var propDef = new JObject
            {
                ["type"] = p["type"],
                ["description"] = p["description"]
            };
            if (p["items"] != null)
                propDef["items"] = p["items"];
            props[propName] = propDef;

            if (p["_required"]?.Value<bool>() == true)
            {
                required.Add(propName);
            }
        }

        return new JObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = required
            }
        };
    }

    private static JObject Prop(string name, string type, string description, bool required = false)
    {
        var prop = new JObject
        {
            ["_name"] = name,
            ["type"] = type,
            ["description"] = description,
            ["_required"] = required
        };
        if (type == "array")
            prop["items"] = new JObject { ["type"] = "string" };
        return prop;
    }

    #endregion

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


    private static string BuildCountArg(JObject arguments)
    {
        string? count = arguments["count"]?.Value<string>();
        return count != null ? $" --count {count}" : "";
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
        return $"exchange klines {symbol} {interval} {limit}" + profileSuffix;
    }

    private static string BuildPlaceOrderCommand(JObject arguments, string profileSuffix, string confirm)
    {
        var parts = new List<string> { "orders place" };
        parts.Add(arguments["symbol"]!.Value<string>()!);
        parts.Add(arguments["side"]!.Value<string>()!);
        parts.Add(arguments["qty"]!.Value<string>()!);

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

        string? tif = arguments["tif"]?.Value<string>();
        if (!string.IsNullOrEmpty(tif))
        {
            parts.Add($"--tif {tif}");
        }

        bool reduceOnly = arguments["reduce_only"]?.Value<bool>() ?? false;
        if (reduceOnly)
        {
            parts.Add("--reduce-only");
        }

        return string.Join(" ", parts) + profileSuffix + confirm;
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

    // ── MT-005: Event streaming tool handler ────────────────────────────────

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


    // ── MT-006/MT-009/MT-010: Internal multi-step tools ──────────────────────

    private JObject? HandleInternalTool(string toolName, JObject arguments)
    {
        return toolName switch
        {
            "mt_metrics_get"        => HandleMetricsGet(),
            "mt_config_snapshot"    => HandleConfigSnapshot(arguments),
            "mt_config_restore"     => HandleConfigRestore(arguments),
            "mt_settings_diff"      => HandleSettingsDiff(arguments),
            "mt_rate_status"        => HandleRateStatus(),    // MT-011
            "mt_vault_store_profile" => HandleVaultStoreProfile(arguments),  // HK-001
            "mt_vault_list_profiles" => HandleVaultListProfiles(arguments),  // HK-001
            "mt_core_shutdown"       => HandleCoreShutdown(arguments),         // MT-023
            "mt_algos_tpsl_change"   => HandleAlgosTpslChange(arguments),       // MT-024
            "mt_algos_profiling"     => HandleAlgosProfiling(arguments),        // MT-024
            "mt_config_import_algos" => HandleConfigImportAlgos(arguments),      // Direct JSON import
            "mt_algos_snapshot"      => HandleAlgosSnapshot(arguments),         // State reconciliation
            "mt_algos_group_by_name" => HandleAlgosGroupByName(arguments),      // State reconciliation
            _ => null
        };
    }

    // MT-006: Return metrics as JSON
    private JObject HandleMetricsGet() => _metrics.ToJson();

    // MT-011: Return rate limit status
    private JObject HandleRateStatus() => _rateLimits.GetStatus();

    // HK-001: Store an API profile in HashiCorp Vault
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

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("X-Vault-Token", vaultToken);   // token in header only
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

    // HK-001: List Vault profiles
    private JObject HandleVaultListProfiles(JObject arguments)
    {
        string vaultAddr  = arguments["vault_addr"]?.Value<string>()  ?? Environment.GetEnvironmentVariable("VAULT_ADDR") ?? "http://127.0.0.1:8200";
        string vaultToken = arguments["vault_token"]?.Value<string>() ?? Environment.GetEnvironmentVariable("VAULT_TOKEN") ?? "";

        try
        {
            string url = $"{vaultAddr.TrimEnd('/')}/v1/secret/metadata/mt/profiles/";
            var reqMsg = new System.Net.Http.HttpRequestMessage(
                new System.Net.Http.HttpMethod("LIST"), url);

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("X-Vault-Token", vaultToken);   // token in header only
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

    // MT-023: Send shutdown/restart service command to MTCore
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

    // MT-024: Send a TP/SL algorithm change request
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

    // MT-024: Request algorithm profiling data (fire-and-forget; result comes via event stream)
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
    // MT-009: Snapshot all settings for a profile to a timestamped JSON file
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

    // MT-009: Restore settings from a snapshot file
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
            if (groupId > 0 && groupIdMap.TryGetValue(groupId, out long newGroupId))
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

    // MT-010: Diff settings between two profiles
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

    // MT-006/MT-009/MT-010: Tool definitions
    private static IEnumerable<JObject> GetInternalToolDefinitions()
    {
        // MT-006
        yield return Tool("mt_metrics_get",
            "Get Prometheus-compatible metrics for tool calls, errors, events, and connections");

        // MT-011
        yield return Tool("mt_rate_status",
            "MT-011: Return sliding-window rate limit status per category (orders/market/account). " +
            "Shows limit, used, and remaining capacity within the current window.");

        // HK-001
        yield return Tool("mt_vault_store_profile",
            "HK-001: Store an exchange API profile (api_key + api_secret) in HashiCorp Vault. " +
            "Credentials are stored securely and never written to disk.",
            Prop("name",        "string", "Profile name (e.g. bybit_main)",   required: true),
            Prop("api_key",     "string", "Exchange API key",                  required: true),
            Prop("api_secret",  "string", "Exchange API secret",               required: true),
            Prop("vault_addr",  "string", "Vault address (default: dev server)"),
            Prop("vault_token", "string", "Vault token (default: dev token)"));
        yield return Tool("mt_vault_list_profiles",
            "HK-001: List all API profiles stored in HashiCorp Vault.",
            Prop("vault_addr",  "string", "Vault address (default: dev server)"),
            Prop("vault_token", "string", "Vault token (default: dev token)"));

        // MT-009
        yield return Tool("mt_config_snapshot",
            "Snapshot all settings + algo list for a profile to a timestamped JSON file",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_config_restore",
            "Restore profile settings from a snapshot file (requires confirm=true)",
            Prop("path", "string", "Path to snapshot JSON file", required: true),
            Prop("confirm", "boolean", "Must be true to actually apply"),
            Prop("profile", "string", "Target server profile"));

        // MT-010
        yield return Tool("mt_settings_diff",
            "Diff settings between two profiles — shows added, removed, changed keys",
            Prop("profile_a", "string", "First server profile", required: true),
            Prop("profile_b", "string", "Second server profile", required: true));

        // MT-023
        yield return Tool("mt_core_shutdown",
            "MT-023: Send a service command to MTCore (shutdown or restart). Requires confirm=true.",
            Prop("command",  "string",  "Command: shutdown | restart | restart_update | restart_clear_orders | restart_clear_archive (default: shutdown)"),
            Prop("confirm",  "boolean", "Must be true to proceed",  required: true),
            Prop("profile",  "string",  "Target server profile (default: first active)"));

        // MT-024
        yield return Tool("mt_algos_tpsl_change",
            "MT-024: Send a TP/SL algorithm change request to MT-Core (fire-and-forget).",
            Prop("tp_enabled",       "boolean", "Enable take profit"),
            Prop("tp_pct",           "number",  "Take profit percentage"),
            Prop("sl_enabled",       "boolean", "Enable stop loss"),
            Prop("sl_pct",           "number",  "Stop loss percentage"),
            Prop("trailing_enabled", "boolean", "Enable trailing stop loss"),
            Prop("trailing_spread",  "number",  "Trailing stop spread percentage"),
            Prop("profile",          "string",  "Target server profile"));

        yield return Tool("mt_algos_profiling",
            "MT-024: Request algorithm profiling data from MT-Core. Result is delivered asynchronously via mt_events_poll.",
            Prop("symbol",   "string",  "Trading symbol (e.g. BTCUSDT)", required: true),
            Prop("algo_id",  "integer", "Algorithm ID (0 = all algos for symbol)"),
            Prop("market",   "string",  "Market type: FUTURES | INVERSE | SPOT (default: FUTURES)"),
            Prop("profile",  "string",  "Target server profile"));

        // State reconciliation tools
        yield return Tool("mt_algos_snapshot",
            "Return a structured snapshot of all groups and algorithms across all connected profiles. " +
            "Includes group names, algo IDs, names, symbols, running state, and signatures. " +
            "Designed for state reconciliation — compare desired vs actual state.",
            Prop("profile", "string", "Target server profile (omit for all connected)"));
        yield return Tool("mt_algos_group_by_name",
            "Find a group by name (case-insensitive). Returns group ID, name, type, and contained algorithms.",
            Prop("name",    "string", "Group name to search for", required: true),
            Prop("profile", "string", "Target server profile"));
    }


    private static IEnumerable<JObject> GetEventToolDefinitions()
    {
        yield return Tool("mt_events_poll",
            "MT-005: Poll buffered events (algo state changes, connection events, errors). " +
            "Returns events since 'since_seq' (or last N if omitted). Use 'current_seq' from response as next 'since_seq'.",
            Prop("since_seq", "integer", "Return events with seq > since_seq (0 = last N events)"),
            Prop("n",         "integer", "Max events to return when since_seq=0 (default: 50)"));
        yield return Tool("mt_events_status",
            "MT-005: Show event stream status — current sequence number, SSE server port, URLs.",
            /* no fields */ Prop("_", "string", "unused", required: false));
        yield return Tool("mt_config_import_algos",
            "Import algorithms from algorithms.config JSON (native MTCore format). Bypasses V2 text parsing.",
            Prop("path", "string", "Path to algorithms.config JSON file"),
            Prop("confirm", "boolean", "Must be true to apply"),
            Prop("emulated", "boolean", "Set isEmulated=true on all trading algos", false),
            Prop("profile", "string", "Target server profile"));

        // Core Service Extended
        yield return Tool("mt_core_restart",
            "Restart the trading core server (requires --confirm)",
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));
        yield return Tool("mt_core_restart_update",
            "Restart the core with software update (requires --confirm)",
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));
        yield return Tool("mt_core_clear_orders",
            "Restart core and clear orders cache (requires --confirm)",
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));
        yield return Tool("mt_core_clear_archive",
            "Restart core and clear archive data (requires --confirm)",
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));

        // Position Close/Reset by TPSL
        yield return Tool("mt_orders_close_by_tpsl",
            "Close a position using TPSL mechanism (requires --confirm)",
            Prop("symbol", "string", "Trading pair symbol", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT"),
            Prop("side", "string", "Position side: LONG, SHORT, BOTH"),
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));
        yield return Tool("mt_orders_reset_tpsl",
            "Reset TP/SL settings on a position (requires --confirm)",
            Prop("symbol", "string", "Trading pair symbol", required: true),
            Prop("market", "string", "Market type"),
            Prop("side", "string", "Position side: LONG, SHORT, BOTH"),
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));

        // TPSL Join/Split
        yield return Tool("mt_tpsl_join",
            "Join multiple TPSL positions into one (requires --confirm)",
            Prop("tpsl_ids", "string", "Space-separated TPSL IDs to join", required: true),
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));
        yield return Tool("mt_tpsl_split",
            "Split a TPSL position (requires --confirm)",
            Prop("tpsl_id", "string", "TPSL ID to split", required: true),
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));

        // Funding
        yield return Tool("mt_funding_request",
            "Request funding account balances (fire-and-forget)",
            Prop("profile", "string", "Target server profile"));

        // BuyApiLimit
        yield return Tool("mt_buylimit_request",
            "Check buy API rate limit for given amount",
            Prop("amount", "string", "Amount to check limit for", required: true),
            Prop("profile", "string", "Target server profile"));


    }

    // ── MT-011: Sliding-window exchange rate limit tracker ──────────────────
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
