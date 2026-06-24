using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace MTTextClient.Core;

/// <summary>
/// Tool registry — single source of truth for all MCP tool schemas.
///
/// The registry is the spine of automatic schema / docs / static-test iteration.
/// <see cref="MCP.McpServer"/>'s <c>HandleToolsList</c> iterates <see cref="AllTools"/>
/// to emit the MCP <c>tools/list</c> response. Static tests iterate the same
/// enumeration directly — no subprocess needed.
///
/// The locked tool baseline in
/// <c>tests/MTTextClient.Tests/_expected/tools.minimum.json</c> asserts the
/// floor; <c>ToolCatalogStaticTests</c> asserts every baseline name and
/// required-args set survives.
///
/// Dispatcher CLI-string templating (the <c>MapToolToCommand</c> switch and
/// <c>Build*Command</c> helpers) stays in <c>McpServer</c>; the registry
/// emits opaque <see cref="JObject"/> values.
/// </summary>
public static class ToolRegistry
{
    /// <summary>
    /// All MCP tool schemas, in the catalog order asserted by the baseline.
    /// </summary>
    public static IEnumerable<JObject> AllTools()
    {
        // ── Event streaming tools ──
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
        yield return Tool("mt_account_balance", "Get account balances (set show_all=true to include dust/zero balances)",
            Prop("show_all", "boolean", "Include dust and zero balances"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_account_orders", "Get active orders (set show_all=true to include archived/non-active)",
            Prop("show_all", "boolean", "Include archived/non-active orders"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_account_positions", "Get open positions (set show_all=true to include closed)",
            Prop("show_all", "boolean", "Include closed positions"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_account_executions", "Get recent trade executions (count overrides default tail size)",
            Prop("count", "integer", "Number of executions to return"),
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

        // ── Exchange Data ──
        yield return Tool("mt_exchange_ticker24",
            "Get 24h ticker price statistics for a symbol. Returns price change, high/low, volume, trade count. market_type FUTURES or SPOT (default: exchange-dependent).",
            Prop("symbol", "string", "Symbol (e.g. BTCUSDT)", required: true),
            Prop("market_type", "string", "FUTURES or SPOT"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_exchange_klines",
            "Get candlestick/kline data for a symbol. Returns OHLCV data. " +
            "market (FUTURES|SPOT) lets you force the market type when a symbol exists on both; " +
            "without it, the server falls back to its exchange-info pair cache and may pick the wrong one " +
            "(e.g. BTCUSDT routes to SPOT on Binance unless overridden).",
            Prop("symbol", "string", "Symbol (e.g. BTCUSDT)", required: true),
            Prop("interval", "string", "Candle interval: 1s,1m,3m,5m,15m,30m,1h,2h,4h,6h,12h,1d,3d,1w,1M (default: 1h)"),
            Prop("limit", "string", "Number of candles to return, 1-1000 (default: 100)"),
            Prop("market", "string", "Market type override: FUTURES or SPOT"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_exchange_trades",
            "Get recent trades for a symbol from the exchange.",
            Prop("symbol", "string", "Symbol (e.g. BTCUSDT)", required: true),
            Prop("profile", "string", "Target server profile"));
        // Funding rate + leverage info (read-only from cached subscription state).
        yield return Tool("mt_exchange_funding_rate",
            "Get the funding-rate fields for a symbol (last funding rate/time, next funding rate/time, mark price, last price). " +
            "Read-only; no confirm. Returns whatever the symbol's live-markets cache currently holds, " +
            "with up to a few seconds of warm-up after first subscription.",
            Prop("symbol", "string", "Trading symbol (lowercase, e.g. btcusdt)", required: true),
            Prop("market_type", "string", "Market type (default FUTURES — SPOT typically has no funding rate)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_exchange_leverage_info",
            "Get configured/effective leverage, max leverage, and risk-limit data for a symbol from MTCore's " +
            "LeverageInfoUpdateData cache. Read-only; no confirm. Does not require an open position, but the " +
            "cache must have observed a core leverage refresh in this mt-text-client session.",
            Prop("symbol", "string", "Trading symbol (lowercase, e.g. btcusdt)", required: true),
            Prop("market_type", "string", "Market type (default FUTURES)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_exchange_leverage_brackets",
            "Compatibility alias for leverage info. Returns configured/effective leverage, max leverage, and risk-limit " +
            "data from MTCore's LeverageInfoUpdateData cache when available, with open-position leverage only as " +
            "a fallback. Read-only; no confirm. NOTE: full bracket-tier tables (notional-range → max-leverage map) " +
            "are not exposed as separate MTShared rows.",
            Prop("symbol", "string", "Trading symbol (lowercase, e.g. btcusdt)", required: true),
            Prop("market_type", "string", "Market type (default FUTURES)"),
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

        // Algo verification — silent-init-failure detection
        yield return Tool("mt_algos_start_verified",
            "Start an algorithm and verify it initialized successfully. " +
            "Waits wait_secs seconds then checks isRunning, symbol, and marketType. " +
            "Returns status: VERIFIED | INIT_FAILURE_SUSPECTED | RUNNING_UNCONFIRMED | NOT_RUNNING.",
            Prop("id",         "string", "Algorithm ID to start",                  required: true),
            Prop("wait_secs",  "string", "Seconds to wait for init (1-30, default 4)"),
            Prop("profile",    "string", "Target server profile"));
        yield return Tool("mt_algos_verify",
            "Verify current state of a running algorithm — checks for the silent-init-failure pattern " +
            "(isRunning=true but symbol/market unresolved). Does NOT start the algo.",
            Prop("id",      "string", "Algorithm ID to inspect", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_stop_all",
            "Stop all algorithms (requires confirm=true). Bulk operation: stops every running algo on the target server.",
            Prop("confirm", "boolean", "Must be true to actually stop all", required: true),
            Prop("profile", "string", "Target server profile"));

        // Batch algo operations — start/stop/config across multiple servers
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
        // File-backed clipboard for cross-profile / cross-exchange paste.
        yield return Tool("mt_algos_copy_to_clipboard",
            "Serialise an algorithm to the schema-versioned clipboard file at " +
            "~/mt-clipboard/algo-clipboard.json.  Read-only on the source; never mutates the bench. " +
            "Use mt_algos_paste_from_clipboard to apply on a destination.",
            Prop("id", "string", "Algorithm ID to copy", required: true),
            Prop("profile", "string", "Source server profile"));
        yield return Tool("mt_algos_paste_from_clipboard",
            "Read the clipboard JSON, run cross-exchange pre-flight, and paste the algorithm onto the " +
            "destination profile.  Without confirm=true, returns a DRY RUN preview listing every detected edge " +
            "case (symbol mismatch, market change, duplicate, blacklist conflict).  override_symbol / override_market " +
            "let the caller force the paste past a known mismatch.  Schema-version-mismatched payloads are " +
            "REJECTED before any wire call.",
            Prop("destination_profile", "string", "Destination server profile", required: true),
            Prop("override_symbol", "string", "Override the source symbol (use the suggested_symbol from a prior dry-run)"),
            Prop("override_market", "string", "Override the source market type: SPOT, MARGIN, FUTURES, DELIVERY"),
            Prop("force", "boolean", "Skip dry-run preview and accept market mismatch / duplicate warnings"),
            Prop("confirm", "boolean", "Must be true to actually apply", required: true));
        yield return Tool("mt_algos_create",
            "Create a new algorithm on a target profile by cloning a source algorithm's argsJson and applying " +
            "user overrides on top.  Creation is clone-from-source: " +
            "either pin an explicit source_algo_id, or specify algo_type to auto-discover a matching algorithm " +
            "on the target profile (optionally filtered by preset_name=signature).  " +
            "The source algorithm's argument template is cloned verbatim, with caller-supplied overrides applied on top — " +
            "so any new vendor fields flow through without an MCP-side update. " +
            "A small _mcp_metadata block is injected into the new algorithm's arguments " +
            "(schema_version, source_algo_id, source_profile, created_at_utc) and is observable via mt_algos_config. " +
            "DRY-RUN by default (omit no_dry_run to preview); commit requires no_dry_run=true AND confirm=true.",
            Prop("profile", "string", "Target server profile", required: true),
            Prop("algo_type", "string",
                "Algorithm group_type: SHOTS, AVERAGES, WATCHERS, SIGNALS, SAVER, DEPTHSHOTS, VECTOR. " +
                "Required when source_algo_id is omitted (auto-discover path)."),
            Prop("preset_name", "string",
                "Optional signature filter (e.g. 'SG' for Shots Group) — narrows auto-discovery " +
                "when multiple algos of the same group_type exist on the target profile"),
            Prop("source_algo_id", "string",
                "Explicit clone source — overrides auto-discovery.  Use this when you want " +
                "a specific algorithm's argsJson as the template (deterministic)."),
            Prop("new_name", "string",
                "Name for the new algorithm.  Default: '<source_name>_copy_<unix_ts>'."),
            Prop("overrides_json", "string",
                "Optional JSON object of {paramKey: newValue} field overrides applied on top of the cloned template. " +
                "Unknown keys are warned (unknown_override_fields) but accepted — a caller may know about a new MT " +
                "field before the MCP layer does."),
            Prop("force", "boolean", "Accept the duplicate_name warning and create a separate row anyway"),
            Prop("no_dry_run", "boolean", "When true, commit; when false/omitted, return a dry-run preview"),
            Prop("confirm", "boolean", "Must be true to commit (no_dry_run + confirm); dry-run runs without commit",
                required: true));

        yield return Tool("mt_algos_bulk_edit",
            "Fan a single field-level mutation across many algorithms in one call. " +
            "Filter selects which algos (by ids, group_id, or all). Mutation is one of: " +
            "whitelist_add (array of symbols to append to each algo's whiteList parameter), " +
            "whitelist_remove (array of symbols to drop), or set (an object of {paramKey: newValue} pairs). " +
            "Without confirm=true the response is a DRY RUN preview showing per-algo current → proposed diffs " +
            "and any warnings (schema_mismatch, blacklist_conflict, no_change). " +
            "With confirm=true each affected algo is SAVE-dispatched individually; failures DO NOT abort the " +
            "batch — every row's success/error is surfaced in partial_result.",
            Prop("filter_json", "string", "JSON: {\"ids\":[...]} | {\"group_id\":N} | {\"all\":true}", required: true),
            Prop("mutation_json", "string", "JSON: {\"whitelist_add\":[...]} | {\"whitelist_remove\":[...]} | {\"set\":{key:value}}", required: true),
            Prop("confirm", "boolean", "Must be true to actually apply (false/omitted → dry-run preview)", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_algos_import_json",
            "Direct inline JSON paste (automation-friendly form; avoids the clipboard-file hop). " +
            "Same edge-case pre-flight as mt_algos_paste_from_clipboard. The 'path' argument: when path is provided, " +
            "the payload is read from that file instead of taken from the 'payload' arg.",
            Prop("payload", "string", "JSON payload matching schema_version=v1 (omit when path is provided)"),
            Prop("path", "string", "Path to a JSON file containing the payload. Overrides payload."),
            Prop("destination_profile", "string", "Destination server profile", required: true),
            Prop("override_symbol", "string", "Override the embedded symbol"),
            Prop("override_market", "string", "Override the embedded market type"),
            Prop("force", "boolean", "Accept market mismatch / duplicate without preview"),
            Prop("confirm", "boolean", "Must be true to actually apply", required: true));

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
            Prop("confirm", "boolean", "Must be true to actually change", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_settings_groups", "List settings grouped by prefix",
            Prop("profile", "string", "Target server profile"));

        // ── Import ──
        yield return Tool("mt_import_templates",
            "List available algorithm templates from algorithms.json / algoConfigs.json. " +
            "If 'path' is provided, reads from that file. Otherwise searches the default locations: " +
            "<app-dir>/algorithms.json, <app-dir>/algoConfigs.json, ~/Documents/algorithms.json, " +
            "~/Documents/algoConfigs.json, /tmp/algorithms.json, /tmp/algoConfigs.json.",
            Prop("path", "string", "Explicit path to algorithm template JSON (overrides default search)"));
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
        // Cross-profile algorithm import survey.  This is a
        // structured "what would be imported" preview, not a bulk-mutation
        // tool.  Surfaces every algorithm on the source profile that would
        // be a candidate for copy to destination, including any duplicates
        // (by name) already present on the destination so the caller can
        // make an informed decision before driving mt_algos_copy in a loop
        // (or feeding the listed payloads into mt_algos_import_json).
        yield return Tool("mt_import_from_profile",
            "Survey what would be imported from source_profile to destination_profile. " +
            "Returns one entry per source algorithm with its name, group, symbol, market, and a " +
            "duplicate_on_destination flag.  Read-only — no mutation.  Use mt_algos_copy per-id or " +
            "mt_algos_paste_from_clipboard / mt_algos_import_json to actually transfer.",
            Prop("source_profile",      "string", "Source server profile to read algos from", required: true),
            Prop("destination_profile", "string", "Destination server profile to compare against", required: true),
            Prop("filter_group_id",     "string", "Optional: only include algos with this group_id"),
            Prop("filter_symbol",       "string", "Optional: only include algos for this symbol (case-insensitive)"));

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

        // ── Order Operations ──
        yield return Tool("mt_orders_place",
            "Place a new order (market or limit). Requires confirm=true. " +
            "If price is omitted, places a MARKET order. If price is set, places a LIMIT order. " +
            "On hedge-mode FUTURES accounts, position_side must match the side book (LONG for BUY, SHORT for SELL); " +
            "for SPOT/one-way leave position_side unset or BOTH. " +
            "market (FUTURES|SPOT) lets callers force the venue when a symbol exists in both — without it the " +
            "server picks whichever pair-cache entry comes back first (BTCUSDT on Binance hits SPOT, which on " +
            "this build refuses orders while SPOT UDS is offline). " +
            "TPSL on placement: set tp_percent and/or sl_percent (with optional tp_type/sl_type/trailing_stop/" +
            "trailing_spread) to attach take-profit and stop-loss settings to the new order at placement time. " +
            "This is the safe pattern — orders placed without TPSL are not recorded in the reports DB " +
            "even after they close. Use mt_orders_update_tpsl only to MODIFY an existing order's TPSL, " +
            "not to attach it to a new one. See docs/TPSL_SAFETY_GUIDE.md.",
            Prop("symbol", "string", "Symbol (e.g. BTCUSDT)", required: true),
            Prop("side", "string", "Order side: BUY or SELL", required: true),
            Prop("qty", "string", "Order quantity", required: true),
            Prop("price", "string", "Limit price (omit for market order)"),
            Prop("type", "string", "Order type: MARKET or LIMIT (auto-detected from price)"),
            Prop("reduce_only", "boolean", "Reduce-only order"),
            Prop("position_side", "string", "Position side override: BOTH (one-way / SPOT), LONG, SHORT. " +
                "If omitted, derived from account position mode + order side."),
            Prop("market", "string", "Market type override: FUTURES or SPOT. " +
                "Wins over the symbol's pair-cache hit when both are set."),
            Prop("client_order_id", "string",
                "Optional client-supplied order id.  If absent, MTCore generates one " +
                "(Binance and OKX prefix it with their broker tag).  Tests pass this " +
                "to track a fill back to a specific order without diffing the open-order set."),
            Prop("emulated", "boolean", "Place as emulated/paper order (held client-side, isEmulationOn=true). Coexists with real orders on the same connection."),
            Prop("iceberg", "boolean",
                "Submit as an iceberg order. The flag is a simple on/off toggle; there is no separate " +
                "visible-quantity field. Whether the venue honours iceberg is venue-specific — the caller " +
                "is responsible for ensuring the destination exchange supports iceberg on this symbol/market."),
            Prop("tp_percent", "string",
                "Take-profit distance from entry as a percent (e.g. \"0.3\" = +0.3%). " +
                "When set, populates OrderRequestData.takeProfitSettings.{isOn=true, percentage, orderType} " +
                "so MTCore tracks the round-trip through TPSL bookkeeping and reports records it on close."),
            Prop("tp_type", "string",
                "Take-profit exit order type: LIMIT (default) or MARKET. Maps to takeProfitSettings.orderType. " +
                "Ignored when tp_percent is unset."),
            Prop("sl_percent", "string",
                "Stop-loss distance from entry as a percent (e.g. \"0.5\" = -0.5%). " +
                "When set, populates OrderRequestData.stopLossSettings.{isOn=true, percentage, orderType} " +
                "so MTCore tracks the round-trip through TPSL bookkeeping."),
            Prop("sl_type", "string",
                "Stop-loss exit order type: MARKET (default) or LIMIT. Maps to stopLossSettings.orderType. " +
                "Ignored when sl_percent is unset."),
            Prop("trailing_stop", "boolean",
                "Enable trailing stop-loss. Requires sl_percent (trigger level) and trailing_spread. " +
                "Maps to stopLossSettings.tralingIsOn."),
            Prop("trailing_spread", "string",
                "Trailing distance as a percent (e.g. \"0.2\" = 0.2% trailing). Maps to " +
                "stopLossSettings.trailingSpread. Ignored when trailing_stop is false."),
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
            "Get current position mode (HEDGE/ONE_WAY) for a SYMBOL. " +
            "Per-symbol query — use this on Binance/OKX where position mode is configured per pair. " +
            "On Bybit, position mode is account-wide and the per-symbol query is not supported by the " +
            "vendor SDK; this tool returns a clear redirect to mt_orders_get_position_mode_account.",
            Prop("symbol", "string", "Symbol (e.g. BTCUSDT) — required for per-symbol exchanges (Binance/OKX)", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_orders_get_position_mode_account",
            "Get current account-wide position mode (HEDGE/ONE_WAY). " +
            "Use this on Bybit, where position mode is configured per account rather than per symbol. " +
            "Reads the cached AccountInfo (priming the cache on first call so a cold connect still " +
            "returns the real mode). On per-symbol exchanges (Binance/OKX) this tool returns a clear " +
            "redirect to mt_orders_get_position_mode <symbol>.",
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
            Prop("market", "string", "Filter by market type: FUTURES,SPOT,MARGIN (comma-separated). Empty/omitted defaults to all three (vendor: CommandReports.cs:195-200)."),
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

        yield return Tool("mt_fleet_batch_connect",
            "Connect to a specific set of named profiles in parallel (max 10 concurrent). " +
            "Unlike mt_fleet_connect (which connects ALL configured profiles), this accepts an " +
            "explicit list — suited for targeted fleet orchestration by automation clients.",
            Prop("profiles", "array", "Array of profile names to connect to", required: true));

        yield return Tool("mt_connection_health",
            "Connection pool health report — per-profile latency, error count, reconnect history, " +
            "and backoff state. Use this to diagnose unstable connections and route around degraded servers.");


        // Server tagging — fleet orchestration labels per connection
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
        // ── Monitor — real-time core monitoring via UDP ──
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
            "List balance and report auto-stops with status. Pulls a fresh AUTO_STOP " +
            "subscription snapshot from Core (MTCore 0.7.24554+). The list index of each " +
            "balance auto-stop is the value to pass to edit/start/stop/delete.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autostops_baseline",
            "Request auto-stop baseline recalculation on Core (fire-and-forget).",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autostops_reports",
            "Get report data for auto-stop algorithms. Optionally filter by algorithm IDs.",
            Prop("ids", "string", "Comma-separated algorithm IDs (optional — omit for all)"),
            Prop("profile", "string", "Target server profile"));

        // AutoStops balance CRUD over the MTCore 0.7.24554 AUTO_STOP request/event
        // subsystem (the pre-24554 AutoStopAlgorithm.Balance.Filters settings-blob and
        // AutoStopAlgorithmData type were removed). Fields map to AutoStopOnBalanceData;
        // index args are positions in the mt_autostops_list snapshot.
        yield return Tool("mt_autostops_add",
            "Create a new balance auto-stop (AutoStopOnBalanceData). Created disabled — call " +
            "mt_autostops_start to activate. Sends AUTO_STOP_REQUEST(AddRequest) to Core.",
            Prop("max_loss", "string", "maxLoss threshold (e.g. -0.1, -5.0)", required: true),
            Prop("name", "string", "Optional display name"),
            Prop("market", "string", "Market type (default FUTURES)"),
            Prop("asset", "string", "Balance asset to watch (default usdt)"),
            Prop("keywords", "string", "Optional algo-name keyword filter"),
            Prop("exclude_keywords", "boolean", "Treat keywords as an exclusion filter"),
            Prop("panic_sell", "boolean", "Panic-sell positions when triggered (panicSellIfTriggered)"),
            Prop("confirm", "boolean", "Must be true to proceed", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autostops_edit",
            "Mutate an existing balance auto-stop by its mt_autostops_list index. " +
            "Only the fields you pass are updated. Sends AUTO_STOP_REQUEST(UpdateRequest).",
            Prop("index", "string", "Zero-based list index (use mt_autostops_list to discover)", required: true),
            Prop("max_loss", "string", "New maxLoss threshold"),
            Prop("name", "string", "New display name"),
            Prop("market", "string", "Market type"),
            Prop("asset", "string", "New balance asset"),
            Prop("keywords", "string", "New keyword filter"),
            Prop("exclude_keywords", "boolean", "Set keyword exclusion on"),
            Prop("include_keywords", "boolean", "Set keyword exclusion off"),
            Prop("panic_sell", "boolean", "Set panicSellIfTriggered=true"),
            Prop("no_panic_sell", "boolean", "Set panicSellIfTriggered=false"),
            Prop("enabled", "boolean", "Set isRunning explicitly"),
            Prop("confirm", "boolean", "Must be true to proceed", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autostops_start",
            "Enable one balance auto-stop by list index, or all if index is omitted. " +
            "Sends AUTO_STOP_REQUEST(RunRequest).",
            Prop("index", "string", "Zero-based list index (optional — omit to enable all)"),
            Prop("confirm", "boolean", "Must be true to proceed", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autostops_stop",
            "Disable one balance auto-stop by list index, or all if index is omitted. " +
            "Sends AUTO_STOP_REQUEST(StopRequest).",
            Prop("index", "string", "Zero-based list index (optional — omit to disable all)"),
            Prop("confirm", "boolean", "Must be true to proceed", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_autostops_delete",
            "Remove a balance auto-stop filter at the given index.",
            Prop("index", "string", "Zero-based filter index", required: true),
            Prop("confirm", "boolean", "Must be true to proceed", required: true),
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

        // Profile-level WhiteList typed CRUD.  Distinct from each
        // algo's per-algo whiteList (mt_algos_bulk_edit's target).  Storage
        // shape mirrors BlackList: WhiteList.Symbols is a JArray of typed
        // entries {MarketType, QuoteAsset, Symbol, TimeFilter}; WhiteList.Quotes
        // omits the Symbol field.
        yield return Tool("mt_whitelist_list",
            "List profile-level WhiteList contents: WhiteList.Symbols (typed entries: market/quote/symbol), " +
            "WhiteList.Quotes (typed entries: market/quote), WhiteList.Only toggle. This is the PROFILE-level " +
            "whitelist (which pairs the profile is allowed to trade), distinct from per-algo whiteList that " +
            "mt_algos_bulk_edit mutates.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_whitelist_add",
            "Add ONE typed whitelist entry. type=symbol needs market+quote+symbol; type=quote needs market+quote. " +
            "If the entry is already present the call is a no-op (already_present warning).  For type=symbol the tool " +
            "also warns when the value isn't in the destination's ExchangeInfoStore pair cache (not_tradable).",
            Prop("type", "string", "symbol or quote", required: true),
            Prop("market", "string", "Market type: SPOT, MARGIN, FUTURES, DELIVERY", required: true),
            Prop("quote", "string", "Quote asset (e.g. usdt, usdc)", required: true),
            Prop("symbol", "string", "Symbol — required only for type=symbol (e.g. ethusdt)"),
            Prop("confirm", "boolean", "Must be true to proceed", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_whitelist_remove",
            "Remove ONE typed whitelist entry. Removing a value that isn't present surfaces a " +
            "structured 'not_found' error.",
            Prop("type", "string", "symbol or quote", required: true),
            Prop("market", "string", "Market type", required: true),
            Prop("quote", "string", "Quote asset", required: true),
            Prop("symbol", "string", "Symbol — required only for type=symbol"),
            Prop("confirm", "boolean", "Must be true to proceed", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_whitelist_bulk_add",
            "Add MANY whitelist entries in one call. For type=symbol, the (market, quote) prefix is " +
            "constant and 'symbols' is a comma-separated list; for type=quote, 'quotes' is a comma-separated list " +
            "of quote assets under the given market. Items already present surface as already_present warnings; " +
            "for type=symbol any item not resolvable on the exchange surfaces as not_tradable (item still lands; " +
            "MTCore decides at order-place time).",
            Prop("type", "string", "symbol or quote", required: true),
            Prop("market", "string", "Market type", required: true),
            Prop("quote", "string", "Quote asset — required only for type=symbol"),
            Prop("symbols", "string", "Comma-separated symbols (type=symbol only)"),
            Prop("quotes", "string", "Comma-separated quote assets (type=quote only)"),
            Prop("confirm", "boolean", "Must be true to proceed", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_whitelist_bulk_remove",
            "Remove MANY whitelist entries in one call. Items not present surface as not_found warnings; " +
            "an all-not-found call fails with a structured error.",
            Prop("type", "string", "symbol or quote", required: true),
            Prop("market", "string", "Market type", required: true),
            Prop("quote", "string", "Quote asset — required only for type=symbol"),
            Prop("symbols", "string", "Comma-separated symbols (type=symbol only)"),
            Prop("quotes", "string", "Comma-separated quote assets (type=quote only)"),
            Prop("confirm", "boolean", "Must be true to proceed", required: true),
            Prop("profile", "string", "Target server profile"));

        // ── TPSL (Take Profit / Stop Loss) ──
        yield return Tool("mt_tpsl_list",
            "List all TPSL (Take Profit / Stop Loss) positions. Auto-primes the TPSL cache via a " +
            "transient AlgorithmTPSLs subscribe on each call (vendor V2 pattern), so no explicit " +
            "mt_tpsl_subscribe is required for read-only listing.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_tpsl_subscribe",
            "Subscribe to TPSL position updates from Core. Data available via mt_tpsl_list.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_tpsl_unsubscribe",
            "Unsubscribe from TPSL position updates.",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_tpsl_cancel",
            "Cancel a TPSL (Take Profit / Stop Loss) position by ID. " +
            "Requires an active TPSL subscription (call mt_tpsl_subscribe first). " +
            "The id can be obtained from mt_tpsl_list. Requires confirm=true.",
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
            "Load a previously stored report set by name and display its trade table and summary stats. " +
            "The tool returns formatted text output; it does not return raw rows for further processing. " +
            "Use mt_reports_stored to list available stored sets first.",
            Prop("name", "string", "Name of the stored report set", required: true));
        yield return Tool("mt_reports_delete",
            "Delete a stored report set by name.",
            Prop("name", "string", "Name of the stored report set to delete", required: true));

        // Automation-friendly rich-filter reports with structured rows,
        // inline CSV, and request-id observability for the synchronous wire.
        yield return Tool("mt_reports_query",
            "Run a trade-report query and return structured JSON rows. " +
            "Each row carries id, open/close prices, qty, USDT-denominated profit/commission/total, " +
            "symbol, market_type, order side, closed_by, and timestamps.  The same rich filters " +
            "as mt_reports_trades are supported.  This is the structured-client variant — " +
            "mt_reports_trades formats text; mt_reports_query is consumable as JSON.",
            Prop("period",            "string",  "today | 24h | 7d | 30d | 90d (default 24h)"),
            Prop("from",              "string",  "ISO-8601 start (YYYY-MM-DD), overrides period"),
            Prop("to",                "string",  "ISO-8601 end (YYYY-MM-DD)"),
            Prop("symbol",            "string",  "Filter by symbol (case-insensitive)"),
            Prop("algo",              "string",  "Filter by algorithm name"),
            Prop("sig",               "string",  "Filter by algorithm signature"),
            Prop("exclude_emulated",  "boolean", "Exclude emulated/paper trades"),
            Prop("closed_by",         "string",  "Comma-separated close reason filter: TP,SL,TS,LIQ,PANIC,AUTO,MARKET,LIMIT,FUNDING,LICENSE"),
            Prop("market",            "string",  "Comma-separated market type filter: FUTURES,SPOT"),
            Prop("side",              "string",  "Order side filter: BUY,SELL (comma OK)"),
            Prop("mode",              "string",  "REAL or EMULATED"),
            Prop("max_rows",          "integer", "Cap rows in the response (default: 200, max: 5000)"),
            Prop("profile",           "string",  "Target server profile"));
        yield return Tool("mt_reports_csv_inline",
            "Same filters as mt_reports_query but returns a CSV string in the response body " +
            "(no file written).  Useful when the agent wants to feed CSV directly into another tool " +
            "without round-tripping through the filesystem.",
            Prop("period",            "string",  "today | 24h | 7d | 30d | 90d (default 24h)"),
            Prop("from",              "string",  "ISO-8601 start (YYYY-MM-DD), overrides period"),
            Prop("to",                "string",  "ISO-8601 end (YYYY-MM-DD)"),
            Prop("symbol",            "string",  "Filter by symbol"),
            Prop("algo",              "string",  "Filter by algorithm name"),
            Prop("sig",               "string",  "Filter by algorithm signature"),
            Prop("exclude_emulated",  "boolean", "Exclude emulated trades"),
            Prop("closed_by",         "string",  "Comma-separated close reason filter"),
            Prop("market",            "string",  "Comma-separated market filter"),
            Prop("side",              "string",  "Order side filter"),
            Prop("mode",              "string",  "REAL or EMULATED"),
            Prop("max_rows",          "integer", "Cap rows (default 200, max 5000)"),
            Prop("profile",           "string",  "Target server profile"));
        yield return Tool("mt_reports_cancel",
            "Cancel a query by request_id. The underlying report-query call is currently synchronous " +
            "(~30s wire time), so cancellation cannot interrupt an in-flight request — this tool " +
            "acknowledges and records the cancellation intent so the caller can observe it.",
            Prop("request_id", "string", "request_id returned by a previous mt_reports_query or _csv_inline call", required: true));
        yield return Tool("mt_reports_status",
            "Look up a query by request_id and return its status, filter " +
            "summary, latency, row_count, and end state.  Returns a structured 'not_found' " +
            "envelope when the request_id is unknown.",
            Prop("request_id", "string", "request_id to look up", required: true));

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
        // Fleet margin-type campaign.  Mandatory dry-run by
        // default; confirm=true commits.
        yield return Tool("mt_fleet_set_margin_type",
            "Apply a CROSS/ISOLATED margin-type change to <symbol> across the entire fleet " +
            "(or a filtered subset).  WITHOUT confirm=true the response is a DRY RUN preview: per-profile " +
            "current_margin (where observable), proposed_margin, would_change flag, and any skip_reason " +
            "(disconnected, symbol_not_in_pair_cache).  Touches venue-side state on commit and is only reversible " +
            "by another call — the dry_run contract is the safety boundary.",
            Prop("symbol", "string", "Trading symbol (e.g. BTCUSDT, BTC-USDT-SWAP)", required: true),
            Prop("margin_type", "string", "Target margin type: CROSS or ISOLATED", required: true),
            Prop("market", "string", "Market type (default FUTURES)"),
            Prop("profiles", "string", "Optional comma-separated profile filter (default: all connected)"),
            Prop("exclude", "string", "Optional comma-separated profile exclude list"),
            Prop("confirm", "boolean", "Must be true to commit; otherwise DRY RUN", required: true));


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

        // Typed notification-config introspection (read-only).
        // MTShared's NotificationSettingsEditor mutation surface is NOT wired
        // through CoreConnection on this build (it needs a ProfileManager +
        // per-profile CommonProfileSettings).  These four tools surface the
        // typed catalog so callers can introspect what notifications exist,
        // their groups, defaults, and target channels.  The capabilities tool
        // also reports the mutation gap honestly.
        yield return Tool("mt_notifications_config_groups",
            "List all notification-group values (TRADE, SYSTEM, …).");
        yield return Tool("mt_notifications_config_targets",
            "List all notification-target values (CLIENT_NOTIFICATIONS, CLIENT_LOG, TELEGRAM).");
        yield return Tool("mt_notifications_config_descriptors",
            "List every toggleable notification descriptor with its group, id, data type, " +
            "and per-target default-enabled flags.");
        yield return Tool("mt_notifications_config_capabilities",
            "Combined notifications-config envelope: groups + targets + descriptors + a " +
            "mutation_supported flag and an honest notice when notification-config mutation is not yet available.");

        // ── Market Data ──
        yield return Tool("mt_marketdata_status",
            "Show all active market data subscriptions (trades, depth, mark price, klines, tickers).",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_marketdata_trades",
            "View recent trade data for a symbol. Requires active trade subscription " +
            "(use mt_marketdata_trades_subscribe first, or mt_exchange_trades for a " +
            "one-shot snapshot without subscription).",
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
            "View order book (top 10 bids/asks) for a symbol. Requires active depth subscription " +
            "(use mt_marketdata_depth_subscribe first; there is no one-shot snapshot equivalent " +
            "in the exchange family for orderbook depth).",
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
            "View mark price, funding rate, and next funding time for a symbol. Requires " +
            "active mark price subscription (use mt_marketdata_markprice_subscribe first, " +
            "or mt_exchange_funding_rate for a one-shot snapshot without subscription).",
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
            "View last kline (candlestick) data for a symbol and interval. Requires active " +
            "kline subscription (use mt_marketdata_klines_subscribe first, or mt_exchange_klines " +
            "for a one-shot OHLCV snapshot without subscription).",
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
            "View ticker data (last price, 24h volume, OHLC) for a symbol or for all " +
            "symbols on a market. Two modes:\n" +
            " - symbol=BTCUSDT  → per-symbol one-shot via ticker24; ALWAYS returns " +
            "fresh data without subscription (uses the same wire path as " +
            "mt_exchange_ticker24).  This is the path that works on every vendor " +
            "build, including BYBIT bench where the bulk SUBSCRIBE_TICKER stream " +
            "does not push frames.\n" +
            " - no symbol      → bulk cache-read for ALL symbols on the requested " +
            "market type; auto-primes the cache with a transient subscribe if cold. " +
            "Returns a clear FAIL response (isError=true) when the bulk subscribe " +
            "yields no data within the wire timeout.",
            Prop("symbol", "string", "Trading pair (e.g. BTCUSDT). When set, the tool returns a per-symbol one-shot snapshot."),
            Prop("market", "string", "Market type: FUTURES, SPOT, MARGIN, DELIVERY (default: FUTURES)"),
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

        // Alerts CRUD (save / delete / set-running).
        // Mutation requires a real subtype payload (AlertRequestSaveData /
        // DeleteData / StartData / StopData).  These tools build the
        // structured object from typed args and send via SendAlertsRequest.
        yield return Tool("mt_alerts_save",
            "Create or update a single price alert. " +
            "When alert_id is omitted (or 0), a new alert is created; non-zero alert_id updates that alert.",
            Prop("name",            "string",  "Display name (e.g. 'BTC drops to 70k')",                    required: true),
            Prop("symbol",          "string",  "Symbol (e.g. 'btcusdt')",                                    required: true),
            Prop("market_type",     "string",  "Market type: FUTURES or SPOT",                              required: true),
            Prop("condition_type",  "string",  "CROSSING | PERCENTAGE_CHANGE | VALUE_CHANGE",               required: true),
            Prop("ref_price",       "number",  "Reference price for the condition",                          required: true),
            Prop("direction",       "string",  "Direction: BOTH | UP | DOWN (default BOTH)"),
            Prop("change_value",    "number",  "Change-value (only for PERCENTAGE_CHANGE / VALUE_CHANGE)"),
            Prop("alert_id",        "integer", "Existing alert id to update (omit/0 for create)"),
            Prop("repeat_type",     "string",  "ONLY_ONCE | EVERY_TIME (default ONLY_ONCE)"),
            Prop("profile",         "string",  "Target server profile"));
        yield return Tool("mt_alerts_delete",
            "Delete alert(s) by id, or delete ALL alerts on the profile. Requires confirm=true.",
            Prop("alert_ids",       "string",  "Comma-separated alert id list (ignored when apply_to_all=true)"),
            Prop("apply_to_all",    "boolean", "Delete every alert on the profile"),
            Prop("confirm",         "boolean", "Must be true to actually delete",                            required: true),
            Prop("profile",         "string",  "Target server profile"));
        yield return Tool("mt_alerts_set_running",
            "Start (running=true) or stop (running=false) alert(s). Requires confirm=true.",
            Prop("running",         "boolean", "true → START, false → STOP",                                 required: true),
            Prop("alert_ids",       "string",  "Comma-separated alert id list (ignored when apply_to_all=true)"),
            Prop("apply_to_all",    "boolean", "Apply to every alert on the profile"),
            Prop("confirm",         "boolean", "Must be true to commit",                                     required: true),
            Prop("profile",         "string",  "Target server profile"));

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
        // List keys + bulk delete by key.
        yield return Tool("mt_profile_settings_list",
            "List the KEYS of the connected profile's settings (read-only). " +
            "Enumerates the keys of the CURRENT profile on the connection — the underlying call " +
            "does not expose a list-named-profiles surface. Optional substring filter via 'grep'.",
            Prop("grep", "string", "Optional substring filter (case-insensitive)"),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_profile_settings_delete",
            "Delete one or more profile-settings keys. Accepts a single comma-separated 'keys' string. " +
            "Edge cases: not_found surfaces as a structured top-level error when ALL keys are absent; " +
            "if some keys exist and others don't, the present ones are deleted and the absent ones surface in NotFound.",
            Prop("keys", "string", "Comma-separated profile-settings key(s) to delete", required: true),
            Prop("confirm", "boolean", "Must be true to actually delete", required: true),
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_profile_settings_update",
            "Update one or more profile settings on the connected MTCore. " +
            "updates_json is a flat JSON object mapping setting keys to string values, e.g. " +
            "{\"BlackList.FirstInitialization\":\"1\",\"NewListedMarket.AddToBlacklistEnabled\":\"1\"}. " +
            "Setting values are always strings on the wire; numbers and booleans must be quoted. " +
            "Some keys (e.g. blacklist arrays) require typed-object JSON values — see mt_blacklist_* tools. " +
            "Some changes require a Core restart to take full effect (this is reported in the tool response). " +
            "Requires confirm=true.",
            Prop("profile_name", "string", "Profile name to update", true),
            Prop("updates_json", "string", "JSON object of key-value updates (string-valued)", true),
            Prop("confirm", "boolean", "Must be true to actually update", required: true),
            Prop("profile", "string", "Target server profile"));

        // Local profiles.json / folders.json CRUD.  All tools
        // operate on the on-disk config; no wire calls to MTCore.
        yield return Tool("mt_profiles_list",
            "List every profile in ~/.config/mt-textclient/profiles.json with its folder + connection data.");
        yield return Tool("mt_profiles_add",
            "Add a new profile to profiles.json.  Required: name, address, port, token, exchange. " +
            "Optional: folder (must already exist via mt_folders_add).  Idempotent on name: duplicate names refused.",
            Prop("name", "string", "Profile name (unique within profiles.json)", required: true),
            Prop("address", "string", "MTCore host (e.g. 127.0.0.1)", required: true),
            Prop("port", "string", "MTCore port (1-65535)", required: true),
            Prop("token", "string", "Client token (kept in profiles.json; treat as a secret)", required: true),
            Prop("exchange", "string", "Exchange enum: BINANCE, OKX, BYBIT, HYPERLIQUID", required: true),
            Prop("folder", "string", "Optional folder label (must exist in folders.json)"),
            Prop("confirm", "boolean", "Must be true to proceed", required: true));
        yield return Tool("mt_profiles_edit",
            "Edit a profile by name.  Pass any subset of --address / --port / --token / --exchange / --folder / --rename.  Empty mutation block = no_change.",
            Prop("name", "string", "Profile to edit", required: true),
            Prop("address", "string", "New address"),
            Prop("port", "string", "New port (1-65535)"),
            Prop("token", "string", "New client token"),
            Prop("exchange", "string", "New exchange"),
            Prop("folder", "string", "New folder label"),
            Prop("rename", "string", "Rename profile (must not collide with another existing name)"),
            Prop("confirm", "boolean", "Must be true to proceed", required: true));
        yield return Tool("mt_profiles_delete",
            "Remove a profile from profiles.json.  Surfaces 'not_found' if the name doesn't exist.",
            Prop("name", "string", "Profile to delete", required: true),
            Prop("confirm", "boolean", "Must be true to proceed", required: true));
        yield return Tool("mt_profiles_move",
            "Move a profile to a different folder.  The destination folder must already exist " +
            "(use mt_folders_add to create it first); empty 'folder' moves the profile to root.",
            Prop("name", "string", "Profile to move", required: true),
            Prop("folder", "string", "Destination folder (must exist; empty string = root)", required: true),
            Prop("confirm", "boolean", "Must be true to proceed", required: true));
        yield return Tool("mt_profiles_import_csv",
            "Bulk-import profiles from a CSV file.  CSV must declare a header row with at minimum the " +
            "columns: name, address, port, token, exchange.  Optional column: folder.  Duplicate names and " +
            "parse errors (bad port / unknown exchange) surface per-row in the response; the operation is " +
            "additive (existing profiles are not touched).",
            Prop("path", "string", "Absolute path to the CSV file", required: true),
            Prop("confirm", "boolean", "Must be true to proceed", required: true));
        yield return Tool("mt_folders_list",
            "List every known folder in ~/.config/mt-textclient/folders.json with the count of profiles " +
            "currently in each.  Also surfaces ORPHAN folders: folder names that profiles reference but " +
            "that are missing from folders.json (use mt_folders_add to canonicalise).");
        yield return Tool("mt_folders_add",
            "Add a new known folder name.  Idempotent: adding an existing folder is a no_change.",
            Prop("name", "string", "Folder name", required: true),
            Prop("confirm", "boolean", "Must be true to proceed", required: true));
        yield return Tool("mt_folders_edit",
            "Rename a folder.  Renames the entry in folders.json AND cascades the rename to every profile " +
            "currently in that folder (profiles.json is rewritten).",
            Prop("old_name", "string", "Existing folder name", required: true),
            Prop("new_name", "string", "New folder name (must not collide with another existing folder)", required: true),
            Prop("confirm", "boolean", "Must be true to proceed", required: true));
        yield return Tool("mt_folders_delete",
            "Delete a folder from folders.json.  WARNING: if any profiles are still in this folder they " +
            "become ORPHAN (their Folder field still references the now-deleted name).  Use mt_profiles_move " +
            "to re-bind them first.",
            Prop("name", "string", "Folder to delete", required: true),
            Prop("confirm", "boolean", "Must be true to proceed", required: true));
    }
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
    private static IEnumerable<JObject> GetInternalToolDefinitions()
    {
        yield return Tool("mt_metrics_get",
            "Get Prometheus-compatible metrics for tool calls, errors, events, and connections");

        yield return Tool("mt_rate_status",
            "Return sliding-window rate limit status per category (orders/market/account). " +
            "Shows limit, used, and remaining capacity within the current window.");

        yield return Tool("mt_vault_store_profile",
            "Store an exchange API profile (api_key + api_secret) in HashiCorp Vault. " +
            "Credentials are stored securely and never written to disk.",
            Prop("name",        "string", "Profile name (e.g. bybit_main)",   required: true),
            Prop("api_key",     "string", "Exchange API key",                  required: true),
            Prop("api_secret",  "string", "Exchange API secret",               required: true),
            Prop("vault_addr",  "string", "Vault address (default: dev server)"),
            Prop("vault_token", "string", "Vault token (default: dev token)"));
        yield return Tool("mt_vault_list_profiles",
            "List all API profiles stored in HashiCorp Vault.",
            Prop("vault_addr",  "string", "Vault address (default: dev server)"),
            Prop("vault_token", "string", "Vault token (default: dev token)"));
        // Read + delete for vault credentials.
        yield return Tool("mt_vault_get_profile",
            "Retrieve a stored API profile from HashiCorp Vault. " +
            "Returns api_key, api_secret, and the stored_at timestamp.",
            Prop("name",        "string", "Profile name to fetch", required: true),
            Prop("vault_addr",  "string", "Vault address (default: dev server)"),
            Prop("vault_token", "string", "Vault token (default: dev token)"));
        yield return Tool("mt_vault_delete_profile",
            "Permanently delete an API profile from HashiCorp Vault " +
            "(KV v2 destroy-all-versions). Requires confirm=true.",
            Prop("name",        "string", "Profile name to delete",                                       required: true),
            Prop("confirm",     "boolean", "Must be true to actually delete (confirm gate enforced)",     required: true),
            Prop("vault_addr",  "string", "Vault address (default: dev server)"),
            Prop("vault_token", "string", "Vault token (default: dev token)"));

        yield return Tool("mt_config_snapshot",
            "Snapshot all settings + algo list for a profile to a timestamped JSON file",
            Prop("profile", "string", "Target server profile"));
        yield return Tool("mt_config_restore",
            "Restore profile settings from a snapshot file (requires confirm=true)",
            Prop("path", "string", "Path to snapshot JSON file", required: true),
            Prop("confirm", "boolean", "Must be true to actually apply"),
            Prop("profile", "string", "Target server profile"));

        yield return Tool("mt_settings_diff",
            "Diff settings between two profiles — shows added, removed, changed keys",
            Prop("profile_a", "string", "First server profile", required: true),
            Prop("profile_b", "string", "Second server profile", required: true));
        // Snapshot-to-snapshot diff (pure client-side).
        yield return Tool("mt_settings_diff_snapshots",
            "Diff two snapshot files written by mt_config_snapshot.  Pure client-side; no MTCore " +
            "wire calls.  Each snapshot path may be absolute or a bare filename under ~/.mt-snapshots/.  " +
            "Reports added/removed/changed keys in the snapshot's settings block + the two snapshots' " +
            "captured_at timestamps and profile names.",
            Prop("snapshot_a", "string", "Path or filename of first snapshot", required: true),
            Prop("snapshot_b", "string", "Path or filename of second snapshot", required: true));

        yield return Tool("mt_core_shutdown",
            "Send a service command to MTCore (shutdown or restart). Requires confirm=true.",
            Prop("command",  "string",  "Command: shutdown | restart | restart_update | restart_clear_orders | restart_clear_archive (default: shutdown)"),
            Prop("confirm",  "boolean", "Must be true to proceed",  required: true),
            Prop("profile",  "string",  "Target server profile (default: first active)"));

        yield return Tool("mt_algos_tpsl_change",
            "Send a TP/SL algorithm change request to MT-Core (fire-and-forget).",
            Prop("tp_enabled",       "boolean", "Enable take profit"),
            Prop("tp_pct",           "number",  "Take profit percentage"),
            Prop("sl_enabled",       "boolean", "Enable stop loss"),
            Prop("sl_pct",           "number",  "Stop loss percentage"),
            Prop("trailing_enabled", "boolean", "Enable trailing stop loss"),
            Prop("trailing_spread",  "number",  "Trailing stop spread percentage"),
            Prop("profile",          "string",  "Target server profile"));

        yield return Tool("mt_algos_profiling",
            "Request algorithm profiling data from MT-Core. Result is delivered asynchronously via mt_events_poll.",
            Prop("symbol",   "string",  "Trading symbol (e.g. BTCUSDT)", required: true),
            Prop("algo_id",  "integer", "Algorithm ID (0 = all algos for symbol)"),
            Prop("market",   "string",  "Market type: FUTURES | INVERSE | SPOT (default: FUTURES)"),
            Prop("profile",  "string",  "Target server profile"));

        // State reconciliation tools
        yield return Tool("mt_algos_snapshot",
            "Return a structured snapshot of all groups and algorithms across all connected profiles. " +
            "Includes group names, algo IDs, names, symbols, running state, and signatures. " +
            "Pulls a fresh algorithm read per profile before answering and reports per-profile " +
            "freshness (source: fresh|cache, age_ms, last_update) — captured_at is the serialization " +
            "time, not data freshness. Designed for state reconciliation — compare desired vs actual state.",
            Prop("profile", "string", "Target server profile (omit for all connected)"));
        yield return Tool("mt_algos_group_by_name",
            "Find a group by name (case-insensitive). Returns group ID, name, type, and contained algorithms. " +
            "Pulls a fresh algorithm read before answering so cold caches do not yield false not-found.",
            Prop("name",    "string", "Group name to search for", required: true),
            Prop("profile", "string", "Target server profile"));
    }
    private static IEnumerable<JObject> GetEventToolDefinitions()
    {
        yield return Tool("mt_events_poll",
            "Poll buffered events (algo state changes, connection events, errors). " +
            "Returns events since 'since_seq' (or last N if omitted). Use 'current_seq' from response as next 'since_seq'.",
            Prop("since_seq", "integer", "Return events with seq > since_seq (0 = last N events)"),
            Prop("n",         "integer", "Max events to return when since_seq=0 (default: 50)"));
        yield return Tool("mt_events_status",
            "Show event stream status — current sequence number, SSE server port, URLs.",
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
        yield return Tool("mt_core_advanced_restart",
            "Composite restart that combines update behaviour with cache-clearing in one cycle. " +
            "Mirrors vendor BotClient's CommandAdvancedRestart (single CORE_ADVANCED_RESTART payload " +
            "carrying a CoreServiceCommand HashSet). Use when a feed wedge AND archive staleness " +
            "need to be cleared in one operator action — saves two separate restart cycles. " +
            "Requires --confirm.",
            Prop("include_update", "boolean", "If true, the restart includes a software-update step (RESTART_WITH_UPDATE); otherwise plain RESTART."),
            Prop("clear_orders_cache", "boolean", "If true, also clears the orders cache as part of the restart cycle (RESTART_WITH_CLEAR_ORDERS_CACHE)."),
            Prop("clear_data_archive", "boolean", "If true, also clears archived data as part of the restart cycle (RESTART_WITH_CLEAR_ARCHIVE_DATA)."),
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));

        // Position Close/Reset by TPSL
        yield return Tool("mt_orders_close_by_tpsl",
            "Close a position using TPSL mechanism (requires --confirm). " +
            "The order_type argument (MARKET|LIMIT) selects whether the closing leg is filled at " +
            "market (default) or as a LIMIT order at the last price.",
            Prop("symbol", "string", "Trading pair symbol", required: true),
            Prop("market", "string", "Market type: FUTURES, SPOT"),
            Prop("side", "string", "Position side: LONG, SHORT, BOTH"),
            Prop("order_type", "string", "Closing leg order type: MARKET (default) or LIMIT"),
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));
        yield return Tool("mt_orders_reset_tpsl",
            "Reset TP/SL settings on a position (requires --confirm)",
            Prop("symbol", "string", "Trading pair symbol", required: true),
            Prop("market", "string", "Market type"),
            Prop("side", "string", "Position side: LONG, SHORT, BOTH"),
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));

        // Active Order TP/SL/TS Update
        // Wires the MTShared SendOrderTPSLUpdateRequest wire method. Operates on
        // an already-placed order / open position identified by (symbol, side,
        // market, position_side).  Sets Take-Profit % and Stop-Loss % (each
        // measured from entry), with an optional trailing-stop spread.
        // Pass 0 (or omit) for take_profit_percent / stop_loss_percent to
        // leave that side untouched; pass an explicit value to (re-)arm it.
        yield return Tool("mt_orders_update_tpsl",
            "Update Take-Profit / Stop-Loss on an active order or open position. " +
            "Identifies the position by symbol + side + market + position_side. Pass take_profit_percent " +
            "and/or stop_loss_percent as positive percentages from entry; omit to leave that side disabled. " +
            "trailing_spread (optional) attaches a trailing stop on the SL leg. " +
            "Requires confirm=true — this mutates open position state.",
            Prop("symbol", "string", "Trading pair symbol (e.g. BTCUSDT)", required: true),
            Prop("side", "string", "Order side: BUY (LONG position) or SELL (SHORT position)", required: true),
            Prop("market", "string", "Market type override: FUTURES (default), SPOT, MARGIN, DELIVERY"),
            Prop("position_side", "string", "Position side: BOTH (one-way / SPOT), LONG, SHORT. " +
                "Defaults to BOTH; the venue's accepted value depends on its position-mode."),
            Prop("take_profit_percent", "string", "Take-profit trigger as % from entry. Omit / 0 to leave TP disabled."),
            Prop("stop_loss_percent", "string", "Stop-loss trigger as % from entry. Omit / 0 to leave SL disabled."),
            Prop("trailing_spread", "string", "Optional trailing-stop spread as % (only meaningful when stop_loss_percent is set)."),
            Prop("client_order_id", "string", "Optional clientOrderId of the target order; helps the venue route the update to the right working order."),
            Prop("confirm", "boolean", "Must be true to actually apply the update", required: true),
            Prop("profile", "string", "Target server profile"));

        // TPSL Join/Split
        yield return Tool("mt_tpsl_join",
            "Join multiple TPSL (Take Profit / Stop Loss) positions into a single position. " +
            "Requires an active TPSL subscription and at least 2 valid tpsl_ids (see mt_tpsl_list). " +
            "Requires confirm=true.",
            Prop("tpsl_ids", "array", "Array of TPSL ID strings to join (at least 2 required)", required: true),
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));
        yield return Tool("mt_tpsl_split",
            "Split a TPSL (Take Profit / Stop Loss) position into two halves. " +
            "Requires an active TPSL subscription and a valid tpsl_id (see mt_tpsl_list). " +
            "Requires confirm=true.",
            Prop("tpsl_id", "string", "TPSL ID to split", required: true),
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));

        // TPSL bulk operations
        yield return Tool("mt_tpsl_cancel_many",
            "Cancel multiple TPSL positions by ID. Loops the existing single-item cancel wire " +
            "method; per-ID results are returned in the response so a single bad ID doesn't abort " +
            "the rest. Auto-primes the TPSL cache so per-ID lookups find the full vendor payload " +
            "(server binds by full identity tuple, not just id). Requires confirm=true.",
            Prop("tpsl_ids", "array", "Array of TPSL ID strings to cancel", required: true),
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));
        yield return Tool("mt_tpsl_split_many",
            "Split multiple TPSL positions, one wire call per ID. Per-ID results returned. " +
            "Auto-primes the TPSL cache so per-ID lookups carry the full vendor payload " +
            "(prevents silent split-rejection on cold connections). Requires confirm=true.",
            Prop("tpsl_ids", "array", "Array of TPSL ID strings to split", required: true),
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));

        // TPSL panic close
        yield return Tool("mt_tpsl_panic",
            "Immediately MARKET-close the position underlying the named TPSL. Looks the TPSL up " +
            "in the local store (requires an active TPSL subscription) and routes through " +
            "ClosePositionByTPSL with OrderType=MARKET. Requires confirm=true.",
            Prop("tpsl_id", "string", "TPSL ID whose underlying position to MARKET-close", required: true),
            Prop("profile", "string", "Target server profile"),
            Prop("confirm", "boolean", "Safety confirmation", required: true));
        yield return Tool("mt_tpsl_panic_many",
            "Bulk panic-close: MARKET-close each named TPSL's underlying position. Loop wrapper " +
            "with per-ID results. Auto-primes the TPSL cache so per-ID lookups resolve. " +
            "Requires confirm=true.",
            Prop("tpsl_ids", "array", "Array of TPSL ID strings to MARKET-close", required: true),
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

        // ── Watchdog (placeholder — out of scope for current epic) ──
        // The two watchdog tools below are advertised in the registry so callers
        // see the eventual surface, but have no handler in this build. See
        // docs/watchdog-integration.md for the picked-up-later workstream.
        const string PlaceholderNote =
            "status: placeholder — not operational in the current deployment. Requires a watchdog-mode " +
            "MTCore instance and a client-side watchdog connection that is not yet implemented in this build. " +
            "Calling this tool returns an 'unknown_tool' error. See docs/watchdog-integration.md " +
            "for the planned design.";
        yield return Tool("mt_watchdog_status",
            "[PLACEHOLDER — NOT AVAILABLE] Intended to query a watchdog's per-core status map " +
            "(monitored cores, addresses, statuses). " + PlaceholderNote,
            Prop("profile", "string", "Target watchdog profile (reserved for the future implementation)"));
        yield return Tool("mt_watchdog_token_update",
            "[PLACEHOLDER — NOT AVAILABLE] Intended to update the watchdog client token used to " +
            "authenticate against a watchdog session. " + PlaceholderNote,
            Prop("token",   "string",  "New watchdog client token", required: true),
            Prop("confirm", "boolean", "Must be true to rotate the token",  required: true),
            Prop("profile", "string", "Target watchdog profile (reserved for the future implementation)"));
    }
}
