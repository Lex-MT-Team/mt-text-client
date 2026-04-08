# MTTextClient

A text-first interface and [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server for [MoonTrader](https://moontrader.com) Core. Connects to one or more MTCore instances over encrypted UDP and exposes **202 MCP tools** across 30+ domains — covering algorithm lifecycle, order execution, market data streaming, fleet management, monitoring, and more.

Built in C# / .NET 8.0. ~24,000 lines of code. Zero external service dependencies.

## Architecture

```
┌──────────────────────────┐            ┌──────────────────────┐
│  MTTextClient            │   UDP      │  MTCore              │
│                          │◄──────────►│  (trading engine)    │
│  • Interactive REPL      │  AES-256   │  Bybit / Binance /   │
│  • MCP Server (stdio)    │  LiteNet   │  OKX / HyperLiquid   │
│  • Web Dashboard         │            │                      │
└──────────────────────────┘            └──────────────────────┘
         ▲                                       ▲
         │ SSE (via mcp-proxy)                   │ Exchange APIs
         ▼                                       ▼
┌──────────────────────────┐            ┌──────────────────────┐
│  AI Agents / LLMs        │            │  Crypto Exchanges    │
│  Claude, GPT, etc.       │            │                      │
└──────────────────────────┘            └──────────────────────┘
```

MTTextClient communicates with MTCore over [LiteNetLib](https://github.com/RevenantX/LiteNetLib) UDP (default port 4242) with AES-256 encryption derived from a per-profile client token. **All features work remotely** — no filesystem access to the MTCore machine is required.

## Requirements

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or later)
- `MTShared.dll` and `LiteNetLib.dll` (included in `lib/`)

## Quick Start

```bash
# Build
dotnet build -c Release

# Run interactive REPL
dotnet run

# Run as MCP server (for AI agent integration)
dotnet run -- --mcp
# NOTE: On Windows, prefer pre-built binary to avoid build output polluting MCP stdio:
#   dotnet build -c Release
#   MTTextClient.exe --mcp

# Run as MCP server with SSE proxy (recommended for AI agents)
pip install mcp-proxy
mcp-proxy --port 8585 -- "path/to/MTTextClient.exe" --mcp
```

## Usage Modes

### 1. Interactive REPL (default)

```bash
dotnet run
```

Full command-line interface with tab completion. Manage connections, algorithms, orders, positions, monitoring — everything.

```
mt> connect my_core
Connected to my_core (203.0.113.50:4242)
mt> algos list
ID       | Type          | Symbol   | Status
---------|---------------|----------|--------
1234567  | 3 ACT AVERAGE | BTCUSDT  | Running
1234568  | WL VECTOR     | ETHUSDT  | Stopped
mt> orders positions
Symbol   | Side | Size    | Entry   | PnL
---------|------|---------|---------|--------
BTCUSDT  | Long | 0.1 BTC | 67,450  | +$124.50
```

### 2. Single Command

```bash
dotnet run -- status
dotnet run -- algos list
dotnet run -- account balance
```

Executes a single command and exits. Useful for scripting.

### 3. MCP Server (for AI agents)

```bash
dotnet run -- --mcp
# On Windows, use pre-built binary: bin\Release\net8.0\MTTextClient.exe --mcp
```

Runs a Model Context Protocol server over stdio (JSON-RPC 2.0). This is the primary integration point for LLMs and autonomous agents.

**With SSE proxy** (recommended):
```bash
mcp-proxy --port 8585 --allow-origin '*' -- "path/to/MTTextClient.exe" --mcp
```

Then point your AI agent to `http://localhost:8585/sse`.

## Server Profiles

Connection profiles are stored in `~/.config/mt-textclient/profiles.json`:

```json
[
  {
    "Name": "my_core",
    "Address": "203.0.113.50",
    "Port": 4242,
    "ClientToken": "<your-client-token>",
    "Exchange": 4,
    "Tags": { "env": "production", "region": "us-east" }
  }
]
```

| Field | Description |
|-------|-------------|
| `Name` | Profile identifier used in `connect <name>` and `@name` syntax |
| `Address` | MTCore IP address or hostname |
| `Port` | MTCore UDP port (default: `4242`) |
| `ClientToken` | Authentication token from MTCore |
| `Exchange` | Exchange enum: `1` = Binance, `2` = OKX, `4` = Bybit, `6` = HyperLiquid |
| `Tags` | Optional key-value metadata for fleet filtering |

Manage profiles via REPL or MCP tools:
```
profile list
profile add <name> <ip> <port> <token> [exchange]
profile remove <name>
```

### Multi-Server Addressing

Any command can target a specific server with the `@profile` suffix:

```
algos list @my_core_2
account balance @us_east_prod
orders positions @eu_west_staging
```

Fleet commands operate across all connected servers simultaneously.

## MCP Tools — Complete Reference (202 tools)

### Connection & Server (4 tools)

| Tool | Description |
|------|-------------|
| `mt_connect` | Connect to an MTCore server by profile name |
| `mt_disconnect` | Disconnect from a server |
| `mt_status` | Show all connection statuses |
| `mt_use` | Switch the active (default) server |

### Connection Management (3 tools)

| Tool | Description |
|------|-------------|
| `mt_connection_health` | Detailed connection health metrics (latency, uptime, reconnects) |
| `mt_connection_tag` | Set a tag on a connection profile |
| `mt_connection_tags` | List all tags for a connection |

### Core Administration (9 tools)

| Tool | Description |
|------|-------------|
| `mt_core_status` | Core version, uptime, exchange, algorithm count |
| `mt_core_license` | License details and expiry |
| `mt_core_health` | Health check (CPU, memory, latency) |
| `mt_core_dashboard` | Combined health + license + status overview |
| `mt_core_restart` | Restart MTCore |
| `mt_core_restart_update` | Restart with update |
| `mt_core_shutdown` | Shut down MTCore |
| `mt_core_clear_archive` | Clear archived algorithms |
| `mt_core_clear_orders` | Clear pending orders |

### Algorithm Lifecycle (28 tools)

| Tool | Description |
|------|-------------|
| `mt_algos_list` | List all algorithms on active server |
| `mt_algos_list_all` | List algorithms across all connected servers |
| `mt_algos_get` | Get detailed algorithm state by ID |
| `mt_algos_search` | Search algorithms by name, symbol, or type |
| `mt_algos_start` | Start an algorithm |
| `mt_algos_stop` | Stop an algorithm |
| `mt_algos_start_all` | Start all algorithms on server |
| `mt_algos_stop_all` | Stop all algorithms on server |
| `mt_algos_start_verified` | Start with post-start verification (detects silent failures) |
| `mt_algos_verify` | Verify algorithm actually initialized correctly |
| `mt_algos_save` | Save algorithm to persistent storage |
| `mt_algos_save_start` | Save and start in one operation |
| `mt_algos_delete` | Delete an algorithm |
| `mt_algos_delete_group` | Delete an entire algorithm group |
| `mt_algos_config` | Get algorithm configuration (all parameters) |
| `mt_algos_config_set` | Set algorithm configuration parameters |
| `mt_algos_rename` | Rename an algorithm |
| `mt_algos_export` | Export algorithm config as portable format |
| `mt_algos_copy` | Copy algorithm to another server |
| `mt_algos_clone_group` | Clone an algorithm group across servers |
| `mt_algos_group` | Get algorithms in a specific group |
| `mt_algos_groups` | List all algorithm groups |
| `mt_algos_toggle_debug` | Toggle debug mode on an algorithm |
| `mt_algos_tpsl_change` | Change TP/SL on a running algorithm |
| `mt_algos_profiling` | Get algorithm profiling data |
| `mt_algos_batch_start` | Start multiple algorithms across servers |
| `mt_algos_batch_stop` | Stop multiple algorithms across servers |
| `mt_algos_batch_config` | Configure multiple algorithms across servers |

### Order & Position Management (23 tools)

| Tool | Description |
|------|-------------|
| `mt_orders_list` | List open orders |
| `mt_orders_positions` | List open positions |
| `mt_orders_place` | Place a new order (limit/market) |
| `mt_orders_cancel` | Cancel an order by ID |
| `mt_orders_cancel_all` | Cancel all open orders |
| `mt_orders_close` | Close a position (partial or full) |
| `mt_orders_close_all` | Close all positions |
| `mt_orders_close_by_tpsl` | Close position using TP/SL mechanism |
| `mt_orders_move` | Move an order to a new price |
| `mt_orders_move_batch` | Move multiple orders |
| `mt_orders_panic_sell` | Emergency close all positions |
| `mt_orders_set_leverage` | Set leverage for a symbol |
| `mt_orders_set_leverage_buysell` | Set separate buy/sell leverage |
| `mt_orders_set_margin_type` | Switch cross/isolated margin |
| `mt_orders_set_position_mode` | Switch one-way/hedge position mode |
| `mt_orders_get_position_mode` | Get current position mode |
| `mt_orders_change_margin` | Add or remove position margin |
| `mt_orders_set_multiasset` | Enable/disable multi-asset mode |
| `mt_orders_get_multiasset` | Get multi-asset mode status |
| `mt_orders_join` | Join (merge) positions |
| `mt_orders_split` | Split a position |
| `mt_orders_transfer` | Transfer between accounts (e.g., spot ↔ derivatives) |
| `mt_orders_reset_tpsl` | Reset TP/SL on a position |

### Account (6 tools)

| Tool | Description |
|------|-------------|
| `mt_account_balance` | Account balance breakdown |
| `mt_account_summary` | PnL summary, margin usage, equity |
| `mt_account_positions` | Account position details |
| `mt_account_orders` | Account order details |
| `mt_account_executions` | Recent trade executions |
| `mt_account_info` | Account-level info (UID, VIP level, etc.) |

### Exchange & Market Info (6 tools)

| Tool | Description |
|------|-------------|
| `mt_exchange_summary` | Exchange overview (total pairs, categories) |
| `mt_exchange_pairs` | List all trading pairs |
| `mt_exchange_search` | Search pairs by name, base, or quote asset |
| `mt_exchange_pair_detail` | Detailed pair info (lot size, tick, min notional) |
| `mt_exchange_klines` | Historical candlestick data |
| `mt_exchange_trades` | Recent trades for a symbol |

### Real-Time Market Data (16 tools)

| Tool | Description |
|------|-------------|
| `mt_marketdata_ticker` | Latest ticker data for a symbol |
| `mt_marketdata_ticker_subscribe` | Subscribe to ticker updates |
| `mt_marketdata_ticker_unsubscribe` | Unsubscribe from ticker |
| `mt_marketdata_depth` | Order book depth snapshot |
| `mt_marketdata_depth_subscribe` | Subscribe to depth updates |
| `mt_marketdata_depth_unsubscribe` | Unsubscribe from depth |
| `mt_marketdata_trades` | Real-time trade stream |
| `mt_marketdata_trades_subscribe` | Subscribe to trades |
| `mt_marketdata_trades_unsubscribe` | Unsubscribe from trades |
| `mt_marketdata_klines` | Real-time kline/candlestick data |
| `mt_marketdata_klines_subscribe` | Subscribe to kline updates |
| `mt_marketdata_klines_unsubscribe` | Unsubscribe from klines |
| `mt_marketdata_markprice` | Mark price and funding rate |
| `mt_marketdata_markprice_subscribe` | Subscribe to mark price updates |
| `mt_marketdata_markprice_unsubscribe` | Unsubscribe from mark price |
| `mt_marketdata_status` | Active subscription status |

### TP/SL Management (6 tools)

| Tool | Description |
|------|-------------|
| `mt_tpsl_list` | List TP/SL orders |
| `mt_tpsl_join` | Join TP/SL groups |
| `mt_tpsl_split` | Split a TP/SL order |
| `mt_tpsl_cancel` | Cancel a TP/SL order |
| `mt_tpsl_subscribe` | Subscribe to TP/SL updates |
| `mt_tpsl_unsubscribe` | Unsubscribe from TP/SL |

### Triggers (9 tools)

| Tool | Description |
|------|-------------|
| `mt_triggers_list` | List all triggers |
| `mt_triggers_save` | Create/update a trigger |
| `mt_triggers_delete` | Delete a trigger |
| `mt_triggers_start` | Start a trigger |
| `mt_triggers_stop` | Stop a trigger |
| `mt_triggers_start_all` | Start all triggers |
| `mt_triggers_stop_all` | Stop all triggers |
| `mt_triggers_subscribe` | Subscribe to trigger events |
| `mt_triggers_unsubscribe` | Unsubscribe from trigger events |

### Alerts (6 tools)

| Tool | Description |
|------|-------------|
| `mt_alerts_list` | List active alerts |
| `mt_alerts_subscribe` | Subscribe to alert notifications |
| `mt_alerts_unsubscribe` | Unsubscribe from alerts |
| `mt_alerts_history` | Get alert history |
| `mt_alerts_history_subscribe` | Subscribe to alert history updates |
| `mt_alerts_history_unsubscribe` | Unsubscribe from alert history |

### AutoBuy (8 tools)

| Tool | Description |
|------|-------------|
| `mt_autobuy_list` | List AutoBuy configurations |
| `mt_autobuy_save` | Create/update AutoBuy config |
| `mt_autobuy_delete` | Delete AutoBuy config |
| `mt_autobuy_start` | Start an AutoBuy |
| `mt_autobuy_stop` | Stop an AutoBuy |
| `mt_autobuy_refresh_pairs` | Refresh AutoBuy pair selection |
| `mt_autobuy_subscribe` | Subscribe to AutoBuy updates |
| `mt_autobuy_unsubscribe` | Unsubscribe from AutoBuy |

### AutoStops (3 tools)

| Tool | Description |
|------|-------------|
| `mt_autostops_list` | List AutoStop configurations |
| `mt_autostops_baseline` | Get AutoStop baseline data |
| `mt_autostops_reports` | Get AutoStop execution reports |

### Blacklist (3 tools)

| Tool | Description |
|------|-------------|
| `mt_blacklist_list` | List blacklisted symbols |
| `mt_blacklist_add` | Add symbol to blacklist |
| `mt_blacklist_remove` | Remove symbol from blacklist |

### Graph Tools (5 tools)

| Tool | Description |
|------|-------------|
| `mt_graphtool_list` | List graph tool configurations |
| `mt_graphtool_save` | Create/update graph tool |
| `mt_graphtool_delete` | Delete graph tool |
| `mt_graphtool_subscribe` | Subscribe to graph tool data |
| `mt_graphtool_unsubscribe` | Unsubscribe from graph tool |

### Live Markets (3 tools)

| Tool | Description |
|------|-------------|
| `mt_livemarkets_list` | List live market feeds |
| `mt_livemarkets_subscribe` | Subscribe to live market updates |
| `mt_livemarkets_unsubscribe` | Unsubscribe from live markets |

### Signals (1 tool)

| Tool | Description |
|------|-------------|
| `mt_signals_send` | Send a trading signal to MTCore |

### Notifications (4 tools)

| Tool | Description |
|------|-------------|
| `mt_notifications_list` | List notifications |
| `mt_notifications_clear` | Clear notifications |
| `mt_notifications_subscribe` | Subscribe to notification stream |
| `mt_notifications_unsubscribe` | Unsubscribe from notifications |

### Performance & Profiling (6 tools)

| Tool | Description |
|------|-------------|
| `mt_perf_list` | List trading performance summaries |
| `mt_perf_request` | Request performance calculation |
| `mt_perf_subscribe` | Subscribe to performance updates |
| `mt_perf_unsubscribe` | Unsubscribe from performance |
| `mt_profiling_subscribe` | Subscribe to profiling data |
| `mt_profiling_unsubscribe` | Unsubscribe from profiling |

### Reports (9 tools)

| Tool | Description |
|------|-------------|
| `mt_reports_trades` | Get trade report |
| `mt_reports_dates` | List available report dates |
| `mt_reports_load` | Load a specific report |
| `mt_reports_store` | Store a report |
| `mt_reports_stored` | List stored reports |
| `mt_reports_delete` | Delete a report |
| `mt_reports_export` | Export report as CSV |
| `mt_reports_fleet_export` | Export fleet-wide report |
| `mt_reports_comments` | Get/set report comments |

### Settings (5 tools)

| Tool | Description |
|------|-------------|
| `mt_settings_get` | Get a core setting value |
| `mt_settings_set` | Set a core setting |
| `mt_settings_search` | Search settings by keyword |
| `mt_settings_groups` | List setting groups |
| `mt_settings_diff` | Compare settings between snapshot and current state |

### Profile Settings (2 tools)

| Tool | Description |
|------|-------------|
| `mt_profile_settings_get` | Get per-profile settings |
| `mt_profile_settings_update` | Update per-profile settings |

### Config Management (3 tools)

| Tool | Description |
|------|-------------|
| `mt_config_snapshot` | Take a full config snapshot (all settings + algo configs) |
| `mt_config_restore` | Restore from a snapshot |
| `mt_config_import_algos` | Import algorithm configurations |

### Import (2 tools)

| Tool | Description |
|------|-------------|
| `mt_import_templates` | List available algorithm templates |
| `mt_import_add_numeric` | Import algorithm with numeric parameters |

### Fleet Operations (13 tools)

| Tool | Description |
|------|-------------|
| `mt_fleet_connect` | Connect to all configured profiles |
| `mt_fleet_batch_connect` | Parallel connect to multiple servers |
| `mt_fleet_disconnect` | Disconnect all servers |
| `mt_fleet_status` | Status of all connections |
| `mt_fleet_summary` | Aggregate fleet summary (servers, algos, balance) |
| `mt_fleet_health` | Health status across all servers |
| `mt_fleet_algos` | List algorithms across all servers |
| `mt_fleet_positions` | Positions across all servers |
| `mt_fleet_balances` | Balances across all servers |
| `mt_fleet_reports` | Reports across all servers |
| `mt_fleet_perf` | Performance metrics across fleet |
| `mt_fleet_autostops` | AutoStop status across fleet |
| `mt_fleet_blacklist` | Blacklists across fleet |

### Monitoring (6 tools)

Real-time performance monitoring via UDP CoreStatusSubscription. Works fully remotely — no server filesystem access needed.

| Tool | Description |
|------|-------------|
| `mt_monitor_start` | Begin collecting status snapshots |
| `mt_monitor_stop` | Stop monitoring |
| `mt_monitor_status` | Current monitoring state, latest metrics |
| `mt_monitor_health` | Health assessment (HEALTHY / WARNING / CRITICAL) with trend analysis |
| `mt_monitor_performance` | Time-series: CPU, RAM, threads, latency |
| `mt_monitor_stats` | Aggregate min/max/avg statistics |

**Data collected per snapshot:** core CPU%, system CPU%, core memory, system memory, free memory, thread count, exchange latency, peer latency, UDS data stream status, API loading.

### Event Streaming (2 tools)

| Tool | Description |
|------|-------------|
| `mt_events_poll` | Poll buffered events (algo state changes, errors, connection events) |
| `mt_events_status` | Event stream status and buffer depth |

Events are also available via Server-Sent Events (SSE) at the `/sse` endpoint when running with `mcp-proxy`. Supports an optional UDP bridge for external consumption.

### Funding & Transfers (2 tools)

| Tool | Description |
|------|-------------|
| `mt_funding_request` | Request funding rate data |
| `mt_fund_transfer` | Transfer between sub-accounts |

### Deposit (2 tools)

| Tool | Description |
|------|-------------|
| `mt_deposit_address` | Get deposit address for an asset |
| `mt_deposit_info` | Get deposit chain info |

### Dust (2 tools)

| Tool | Description |
|------|-------------|
| `mt_dust_get` | Get small balance ("dust") assets |
| `mt_dust_convert` | Convert dust to main asset |

### Buy API Limit (1 tool)

| Tool | Description |
|------|-------------|
| `mt_buylimit_request` | Request buy API limit info |

### Vault (2 tools)

Secure credential management via HashiCorp Vault integration.

| Tool | Description |
|------|-------------|
| `mt_vault_store_profile` | Store a connection profile in Vault |
| `mt_vault_list_profiles` | List profiles stored in Vault |

Requires a running Vault instance. Configure via `VAULT_ADDR` and `VAULT_TOKEN` environment variables.

### Metrics (1 tool)

| Tool | Description |
|------|-------------|
| `mt_metrics_get` | Prometheus-format metrics (connections, requests, errors, latencies) |

### Rate Limiting (1 tool)

| Tool | Description |
|------|-------------|
| `mt_rate_status` | Current rate limit status (used/remaining, window stats) |

## Infrastructure Features

### Circuit Breaker

Per-connection circuit breaker with CAS (compare-and-swap) state transitions:

- **Closed**: Normal operation
- **Open**: Connection has failed too many times, requests are rejected
- **Half-Open**: Testing recovery after cooldown period

Prevents cascading failures when MTCore instances become unresponsive.

### Rate Limiter

Token bucket rate limiter with configurable burst and refill rates:

- Default: 600 burst capacity, 120 tokens/second refill
- Prevents overwhelming MTCore or exchange API limits
- Applied per-connection

### Connection Pump

Multi-worker striped polling with adaptive sleep:

- Distributes network polling across worker threads
- Automatically adjusts poll frequency based on activity
- Handles reconnection with storm protection (capped concurrent reconnects)

### Event Streaming

Push-based event delivery for state changes:

- Algorithm state transitions (started, stopped, error)
- Connection events (connected, disconnected, reconnecting)
- Available via SSE endpoint and MCP poll tools
- Optional UDP bridge for external event consumers

### Prometheus Metrics

Exposed via `mt_metrics_get` in Prometheus text format:

- Connection count, active/failed/total
- Request count by tool, success/error
- Latency histograms per connection
- Circuit breaker state transitions
- Rate limiter utilization

## Project Structure

```
MTTextClient/
├── Program.cs                    # Entry point (REPL, single-cmd, MCP modes)
├── MTTextClient.csproj           # .NET 8.0 project file
├── lib/                          # Binary dependencies
│   ├── MTShared.dll              #   MoonTrader shared protocol library
│   └── LiteNetLib.dll            #   UDP networking
├── Core/                         # Connection & state management
│   ├── CoreConnection.cs         #   Single MTCore connection (protocol impl)
│   ├── ConnectionManager.cs      #   Multi-connection orchestrator
│   ├── ConnectionPump.cs         #   Multi-worker network event pump
│   ├── CircuitBreaker.cs         #   Per-connection circuit breaker
│   ├── RateLimiter.cs            #   Token bucket rate limiter
│   ├── ProfileManager.cs         #   Profile load/save
│   ├── ServerProfile.cs          #   Profile data model with tags
│   ├── ConnectionHealthRecord.cs #   Health metrics tracking
│   ├── AlgorithmStore.cs         #   Algorithm state cache
│   ├── AccountStore.cs           #   Account/balance state cache
│   ├── ExchangeInfoStore.cs      #   Trading pair info cache
│   ├── CoreStatusStore.cs        #   Core status/license cache
│   ├── ProfileSettingsStore.cs   #   Per-profile settings cache
│   ├── AutoBuyStore.cs           #   AutoBuy state cache
│   ├── GraphToolStore.cs         #   Graph tool data cache
│   ├── LiveMarketStore.cs        #   Live market feed cache
│   ├── MarketDataStore.cs        #   Market data subscription cache
│   ├── NotificationStore.cs      #   Notification buffer
│   ├── ReportStore.cs            #   Trade report cache
│   ├── ReportCsvExporter.cs      #   CSV export utility
│   ├── TPSLStore.cs              #   TP/SL state cache
│   ├── TradingPerformanceStore.cs #  Performance metrics cache
│   └── TriggerStore.cs           #   Trigger state cache
├── Commands/                     # REPL command implementations (32 files)
│   ├── CommandRegistry.cs        #   Command routing
│   ├── ICommand.cs               #   Command interface
│   ├── ConnectionCommands.cs     #   connect, disconnect, status, use
│   ├── AlgosCommand.cs           #   Algorithm lifecycle
│   ├── OrdersCommand.cs          #   Order/position management
│   ├── AccountCommands.cs        #   Balance, summary, executions
│   ├── ExchangeCommand.cs        #   Market data, klines, ticker
│   ├── FleetCommand.cs           #   Multi-server fleet operations
│   ├── MonitorCommand.cs         #   Real-time core monitoring
│   ├── SettingsCommand.cs        #   Core settings get/set
│   ├── ReportsCommand.cs         #   Trade reports, CSV export
│   ├── CoreStatusCommand.cs      #   License, health dashboard
│   ├── ProfileCommand.cs         #   Profile CRUD
│   ├── ImportCommand.cs          #   V2 algorithm import
│   ├── MarketDataCommand.cs      #   Real-time market data streams
│   ├── TPSLCommand.cs            #   Take-profit / stop-loss management
│   ├── TriggersCommand.cs        #   Price / condition triggers
│   ├── AlertsCommand.cs          #   Alert management
│   ├── AutoBuyCommand.cs         #   Automatic buying rules
│   ├── AutoStopsCommand.cs       #   Automatic stop-loss rules
│   ├── BlacklistCommand.cs       #   Symbol blacklisting
│   ├── GraphToolCommand.cs       #   Graph tool management
│   ├── LiveMarketsCommand.cs     #   Live market feeds
│   ├── SignalsCommand.cs         #   Trading signal dispatch
│   ├── NotificationsCommand.cs   #   Notification management
│   ├── PerformanceCommand.cs     #   Trading performance analytics
│   ├── ProfilingCommand.cs       #   Algorithm profiling
│   ├── DustCommand.cs            #   Dust conversion
│   ├── DepositCommand.cs         #   Deposit info
│   ├── FundingCommand.cs         #   Funding rates
│   ├── BuyApiLimitCommand.cs     #   Buy limit queries
│   └── HelpCommand.cs            #   Help text
├── MCP/                          # MCP server implementation
│   ├── McpServer.cs              #   JSON-RPC stdio server (202 tools)
│   └── EventStreaming.cs         #   SSE + UDP event bridge
├── Monitoring/                   # Real-time core monitoring
│   ├── MonitorBuffer.cs          #   Ring buffer for status snapshots
│   └── MonitorAnalyzer.cs        #   Trend analysis, health assessment
├── Import/                       # Algorithm import
│   └── V2FormatParser.cs         #   V2 format parser
├── Output/                       # Display formatting
│   ├── OutputManager.cs          #   JSON/table/text output
│   └── TableBuilder.cs           #   ASCII table renderer
└── web/                          # Browser dashboard
    └── index.html                #   Zero-dependency MCP dashboard
```

## Web Dashboard

A zero-dependency browser dashboard is included in `web/index.html`. Connects directly to the MCP server via SSE.

```bash
# 1. Start MCP server with SSE proxy
mcp-proxy --port 8585 --allow-origin '*' -- "path/to/MTTextClient.exe" --mcp

# 2. Serve the dashboard
python3 -m http.server 9090 -d web

# 3. Open http://localhost:9090
```

### Features

- Auto-discovers all 202 MCP tools from the running server
- Categorized tool guide with architecture diagram
- Server profile management (connect, disconnect, switch active)
- Execute any tool with form-based parameter input
- Three response views: Beautified, Raw JSON, and How It Works
- Batch execution across multiple profiles
- Dark theme with multiple color schemes (dark / light / solarized / nord)
- Fully responsive, localStorage persistence

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `VAULT_ADDR` | `http://127.0.0.1:8200` | HashiCorp Vault address (for `mt_vault_*` tools) |
| `VAULT_TOKEN` | *(none)* | Vault authentication token |
| `MT_NATS_UDP_HOST` | *(none)* | Optional NATS UDP bridge host for event forwarding |

## Supported Exchanges

| Exchange | ID | Status |
|----------|:--:|--------|
| Binance  | 1  | Full support |
| OKX      | 2  | Full support |
| Bybit    | 4  | Full support |
| HyperLiquid | 6 | Full support |

## License

MIT — see [LICENSE](LICENSE).
