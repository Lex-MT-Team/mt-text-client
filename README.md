# MTTextClient

A text-first interface and [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server for [MoonTrader](https://moontrader.com) Core. Connects to one or more MTCore instances over encrypted UDP and exposes **258 MCP tools** across 30+ domains — covering algorithm lifecycle, order execution, market data streaming, fleet management, monitoring, and more.

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
│  MCP-compatible          │            │  Crypto Exchanges    │
│  clients & automation    │            │                      │
└──────────────────────────┘            └──────────────────────┘
```

MTTextClient communicates with MTCore over [LiteNetLib](https://github.com/RevenantX/LiteNetLib) UDP (default port 4242) with AES-256 encryption derived from a per-profile client token. **All features work remotely** — no filesystem access to the MTCore machine is required.

## Requirements

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or later)
- Python 3 (only when refreshing vendor libs; see [`lib/README.md`](lib/README.md))
- `MTShared.dll` and `LiteNetLib.dll` — committed as the active baseline in
  [`lib/`](lib/) and fetchable from the public MoonTrader CDN per host RID
  via `scripts/fetch_vendor_libs.py`. The build picks per-RID copies when
  present, falls back to the committed baseline. See [`lib/README.md`](lib/README.md)
  for the full layout, supported RIDs, and refresh workflow.

## Quick Start

```bash
# Build
dotnet build -c Release

# Run interactive REPL
dotnet run

# Run as MCP server (for MCP-compatible client integration)
dotnet run -- --mcp
# NOTE: On Windows, prefer pre-built binary to avoid build output polluting MCP stdio:
#   dotnet build -c Release
#   MTTextClient.exe --mcp

# Run as MCP server with SSE proxy (recommended for MCP-compatible clients)
pip install mcp-proxy
mcp-proxy --port 8585 -- "path/to/MTTextClient.exe" --mcp
```

## Usage Modes

> **Before placing real orders — read [docs/TPSL_SAFETY_GUIDE.md](docs/TPSL_SAFETY_GUIDE.md).**
> Closing positions outside the TPSL pathway leaves no row in the reports DB,
> even when the trade fills cleanly at the venue. The guide describes the
> correct place → attach-TPSL → close-via-TPSL sequence and the venue/wire
> quirks that compound the rule.

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

### 3. MCP Server (for MCP-compatible clients)

```bash
dotnet run -- --mcp
# On Windows, use pre-built binary: bin\Release\net8.0\MTTextClient.exe --mcp
```

Runs a Model Context Protocol server over stdio (JSON-RPC 2.0). This is the primary integration point for any MCP-compatible client or automation tool.

**With SSE proxy** (recommended):
```bash
mcp-proxy --port 8585 --allow-origin '*' -- "path/to/MTTextClient.exe" --mcp
```

Then point your MCP-compatible client to `http://localhost:8585/sse`.

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

## MCP Tools — Complete Reference (258 tools)

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

### Algorithm Lifecycle (30 tools)

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
| `mt_algos_delete` | Delete an algorithm (idempotent — returns ok if already deleted) |
| `mt_algos_delete_group` | Delete an entire algorithm group (idempotent — returns ok if already deleted) |
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
| `mt_algos_snapshot` | Full structured snapshot of all groups/algos across profiles (for state reconciliation) |
| `mt_algos_group_by_name` | Find a group by name (case-insensitive) with all contained algorithms |
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
│   ├── McpServer.cs              #   JSON-RPC stdio server (258 tools)
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

- Auto-discovers all 258 MCP tools from the running server
- Categorized tool guide with architecture diagram
- Server profile management (connect, disconnect, switch active)
- Execute any tool with form-based parameter input
- Three response views: Beautified, Raw JSON, and How It Works
- Batch execution across multiple profiles
- Dark theme with multiple color schemes (dark / light / solarized / nord)
- Fully responsive, localStorage persistence

## Scenarios

Worked end-to-end examples. Each one assumes you have a profile defined in
`~/.config/mt-textclient/profiles.json` and `MTTextClient` built (`dotnet build -c Release`).

### Scenario A — Connect, inspect, place a limit order, cancel, disconnect

```bash
dotnet run -- --mcp <<'EOF'
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"demo","version":"1.0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"mt_connect","arguments":{"profile":"my_core"}}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"mt_account_balance","arguments":{"profile":"my_core"}}}
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"mt_orders_place","arguments":{"profile":"my_core","symbol":"BTCUSDT","side":"BUY","type":"LIMIT","quantity":"0.001","price":"30000"}}}
{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"mt_orders_cancel_all","arguments":{"profile":"my_core","confirm":true}}}
{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"mt_disconnect","arguments":{"profile":"my_core"}}}
EOF
```

Or in REPL:
```
mt> connect my_core
mt> account balance
mt> orders place BTCUSDT BUY LIMIT 0.001 30000
mt> orders cancel-all --confirm
mt> disconnect my_core
```

### Scenario B — Multi-server fleet inventory

```
mt> connect us_east_prod
mt> connect eu_west_prod
mt> connect ap_southeast_prod
mt> fleet inventory
```

`fleet inventory` calls `mt_algos_snapshot` across every connected profile and
returns a unified group/algo tree suitable for state reconciliation.

### Scenario C — V2 algorithm export → import round-trip

```
mt> connect source_core
mt> algos export 1234567 > /tmp/algo.json
mt> connect dest_core
mt> import /tmp/algo.json @dest_core
mt> algos list @dest_core
```

The V2 import path preserves group bindings — including ungrouped (group-id 0)
membership.

### Scenario D — Restart MTCore with synchronous probe

```
mt> connect my_core
mt> core restart
[my_core] Core reconnected (3.2s)
```

If MTCore fails to come back the call returns `success:false` with a
diagnostic naming the log location, instead of fire-and-forget.

### Scenario E — 24h ticker on a specific market side

```
mt> exchange ticker24 BTCUSDT FUTURES
```

Or via MCP:
```bash
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mt_exchange_ticker24","arguments":{"profile":"my_core","symbol":"BTCUSDT","market_type":"FUTURES"}}}
```

### Scenario F — Bulk operation with confirmation gate

Destructive bulk tools (`mt_algos_start_all`, `mt_algos_stop_all`,
`mt_algos_delete`, `mt_orders_cancel_all`, `mt_fleet_disconnect`,
`mt_core_restart*`, `mt_core_clear_orders`) reject calls without
`confirm:true`:

```
mt> algos stop-all                 # rejected: requires --confirm
mt> algos stop-all --confirm        # proceeds
```

---

## Testing

`MTTextClient` ships with three layers of self-verification.

### 1. Build smoke test

```bash
dotnet build -c Release
# expect: Build succeeded.  0 Warning(s)  0 Error(s)
```

### 2. MCP `tools/list` enumeration

Confirms the server is reachable and publishes the expected tool surface:

```bash
# Stdio
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | dotnet run -- --mcp 2>/dev/null | grep -o '"name":"mt_[^"]*"' | sort -u | wc -l
# expect: 258
```

Or via the SSE bridge:

```bash
mcp-proxy --port 8585 -- "bin/Release/net8.0/MTTextClient" --mcp &
curl -sS -N http://localhost:8585/sse &  # opens the event stream
curl -sS -X POST http://localhost:8585/messages -H 'Content-Type: application/json'   -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | jq '.result.tools | length'
```

### 3. Web dashboard sanity sweep

Open `web/index.html` against a running SSE proxy. The dashboard auto-runs a
read-only sweep across all 258 tools and prints pass/fail counts in the top
bar. A clean run on a connected profile reports:

* All 258 tools reachable
* 0 schema-validation errors
* All confirm-gated destructive tools refuse calls without `confirm:true`

### 4. Single-tool invocation from the shell

For quick checks without a client:

```bash
dotnet run -- status                          # connection state across all profiles
dotnet run -- algos list @my_core             # one profile
dotnet run -- mt_metrics_get | grep tools_total
```

### 5. Schema-required enforcement

The MCP gateway enforces `inputSchema.required` server-side and rejects
requests with a JSON-RPC `-32602` error naming the missing field:

```bash
echo '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mt_orders_place","arguments":{}}}' | dotnet run -- --mcp 2>/dev/null | jq '.error'
# expect: code -32602, message includes "Missing required argument: profile"
```

The error response always echoes the original `id` so async batches can
reconcile failures.

---

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

<!-- BEGIN AUTOGENERATED REGISTRY TABLE -->

> Auto-generated from `Core/ToolRegistry.cs` by `tools/RegistryReadmeGenerator`. Do not edit by hand — re-run the generator. Static tests fail if this table drifts from the registry.

| Tool | Description | Required args | Confirm |
|------|-------------|---------------|---------|
| `mt_events_poll` | Poll buffered events (algo state changes, connection events, errors). Returns events since 'since_seq' (or last N if omitted). Use 'current_seq' from response as next 'since_seq'. | — | — |
| `mt_events_status` | Show event stream status — current sequence number, SSE server port, URLs. | — | — |
| `mt_config_import_algos` | Import algorithms from algorithms.config JSON (native MTCore format). Bypasses V2 text parsing. | — | — |
| `mt_core_restart` | Restart the trading core server (requires --confirm) | `confirm` | ✓ |
| `mt_core_restart_update` | Restart the core with software update (requires --confirm) | `confirm` | ✓ |
| `mt_core_clear_orders` | Restart core and clear orders cache (requires --confirm) | `confirm` | ✓ |
| `mt_core_clear_archive` | Restart core and clear archive data (requires --confirm) | `confirm` | ✓ |
| `mt_core_advanced_restart` | Composite restart that combines update behaviour with cache-clearing in one cycle. Mirrors vendor BotClient's CommandAdvancedRestart (single CORE_ADVANCED_RESTART payload carrying a CoreServiceCommand HashSet). Use when a feed wedge AND archive staleness need to be cleared in one operator action — saves two separate restart cycles. Requires --confirm. | `confirm` | ✓ |
| `mt_orders_close_by_tpsl` | Close a position using TPSL mechanism (requires --confirm). The order_type argument (MARKET\|LIMIT) selects whether the closing leg is filled at market (default) or as a LIMIT order at the last price. | `symbol`, `confirm` | ✓ |
| `mt_orders_reset_tpsl` | Reset TP/SL settings on a position (requires --confirm) | `symbol`, `confirm` | ✓ |
| `mt_orders_update_tpsl` | Update Take-Profit / Stop-Loss on an active order or open position. Identifies the position by symbol + side + market + position_side. Pass take_profit_percent and/or stop_loss_percent as positive percentages from entry; omit to leave that side disabled. trailing_spread (optional) attaches a trailing stop on the SL leg. Requires confirm=true — this mutates open position state. | `symbol`, `side`, `confirm` | ✓ |
| `mt_tpsl_join` | Join multiple TPSL (Take Profit / Stop Loss) positions into a single position. Requires an active TPSL subscription and at least 2 valid tpsl_ids (see mt_tpsl_list). Requires confirm=true. | `tpsl_ids`, `confirm` | ✓ |
| `mt_tpsl_split` | Split a TPSL (Take Profit / Stop Loss) position into two halves. Requires an active TPSL subscription and a valid tpsl_id (see mt_tpsl_list). Requires confirm=true. | `tpsl_id`, `confirm` | ✓ |
| `mt_tpsl_cancel_many` | Cancel multiple TPSL positions by ID. Loops the existing single-item cancel wire method; per-ID results are returned in the response so a single bad ID doesn't abort the rest. Auto-primes the TPSL cache so per-ID lookups find the full vendor payload (server binds by full identity tuple, not just id). Requires confirm=true. | `tpsl_ids`, `confirm` | ✓ |
| `mt_tpsl_split_many` | Split multiple TPSL positions, one wire call per ID. Per-ID results returned. Auto-primes the TPSL cache so per-ID lookups carry the full vendor payload (prevents silent split-rejection on cold connections). Requires confirm=true. | `tpsl_ids`, `confirm` | ✓ |
| `mt_tpsl_panic` | Immediately MARKET-close the position underlying the named TPSL. Looks the TPSL up in the local store (requires an active TPSL subscription) and routes through ClosePositionByTPSL with OrderType=MARKET. Requires confirm=true. | `tpsl_id`, `confirm` | ✓ |
| `mt_tpsl_panic_many` | Bulk panic-close: MARKET-close each named TPSL's underlying position. Loop wrapper with per-ID results. Auto-primes the TPSL cache so per-ID lookups resolve. Requires confirm=true. | `tpsl_ids`, `confirm` | ✓ |
| `mt_funding_request` | Request funding account balances (fire-and-forget) | — | — |
| `mt_buylimit_request` | Check buy API rate limit for given amount | `amount` | — |
| `mt_watchdog_status` | [PLACEHOLDER — NOT AVAILABLE] Intended to query a watchdog's per-core status map (monitored cores, addresses, statuses). status: placeholder — not operational in the current deployment. Requires a watchdog-mode MTCore instance and a client-side watchdog connection that is not yet implemented in this build. Calling this tool returns an 'unknown_tool' error. See docs/watchdog-integration.md for the planned design. | — | — |
| `mt_watchdog_token_update` | [PLACEHOLDER — NOT AVAILABLE] Intended to update the watchdog client token used to authenticate against a watchdog session. status: placeholder — not operational in the current deployment. Requires a watchdog-mode MTCore instance and a client-side watchdog connection that is not yet implemented in this build. Calling this tool returns an 'unknown_tool' error. See docs/watchdog-integration.md for the planned design. | `token`, `confirm` | ✓ |
| `mt_metrics_get` | Get Prometheus-compatible metrics for tool calls, errors, events, and connections | — | — |
| `mt_rate_status` | Return sliding-window rate limit status per category (orders/market/account). Shows limit, used, and remaining capacity within the current window. | — | — |
| `mt_vault_store_profile` | Store an exchange API profile (api_key + api_secret) in HashiCorp Vault. Credentials are stored securely and never written to disk. | `name`, `api_key`, `api_secret` | — |
| `mt_vault_list_profiles` | List all API profiles stored in HashiCorp Vault. | — | — |
| `mt_vault_get_profile` | Retrieve a stored API profile from HashiCorp Vault. Returns api_key, api_secret, and the stored_at timestamp. | `name` | — |
| `mt_vault_delete_profile` | Permanently delete an API profile from HashiCorp Vault (KV v2 destroy-all-versions). Requires confirm=true. | `name`, `confirm` | ✓ |
| `mt_config_snapshot` | Snapshot all settings + algo list for a profile to a timestamped JSON file | — | — |
| `mt_config_restore` | Restore profile settings from a snapshot file (requires confirm=true) | `path` | — |
| `mt_settings_diff` | Diff settings between two profiles — shows added, removed, changed keys | `profile_a`, `profile_b` | — |
| `mt_settings_diff_snapshots` | Diff two snapshot files written by mt_config_snapshot.  Pure client-side; no MTCore wire calls.  Each snapshot path may be absolute or a bare filename under ~/.mt-snapshots/.  Reports added/removed/changed keys in the snapshot's settings block + the two snapshots' captured_at timestamps and profile names. | `snapshot_a`, `snapshot_b` | — |
| `mt_core_shutdown` | Send a service command to MTCore (shutdown or restart). Requires confirm=true. | `confirm` | ✓ |
| `mt_algos_tpsl_change` | Send a TP/SL algorithm change request to MT-Core (fire-and-forget). | — | — |
| `mt_algos_profiling` | Request algorithm profiling data from MT-Core. Result is delivered asynchronously via mt_events_poll. | `symbol` | — |
| `mt_market_live_algorithms` | Return which algorithms are currently active on which market symbols (Markets Overview mapping). Synchronous request to MT-Core. | — | — |
| `mt_algos_snapshot` | Return a structured snapshot of all groups and algorithms across all connected profiles. Includes group names, algo IDs, names, symbols, running state, and signatures. Pulls a fresh algorithm read per profile before answering and reports per-profile freshness (source: fresh\|cache, age_ms, last_update) — captured_at is the serialization time, not data freshness. Designed for state reconciliation — compare desired vs actual state. | — | — |
| `mt_algos_group_by_name` | Find a group by name (case-insensitive). Returns group ID, name, type, and contained algorithms. Pulls a fresh algorithm read before answering so cold caches do not yield false not-found. | `name` | — |
| `mt_connect` | Connect to an MT-Core server using a saved profile | `profile` | — |
| `mt_disconnect` | Disconnect from a server | `profile` | — |
| `mt_status` | Show all connection statuses | — | — |
| `mt_use` | Switch active connection | `profile` | — |
| `mt_account_balance` | Get account balances (set show_all=true to include dust/zero balances) | — | — |
| `mt_account_orders` | Get active orders (set show_all=true to include archived/non-active) | — | — |
| `mt_account_positions` | Get open positions (set show_all=true to include closed) | — | — |
| `mt_account_executions` | Get recent trade executions (count overrides default tail size) | — | — |
| `mt_account_info` | Get account info | — | — |
| `mt_account_summary` | Get account summary | — | — |
| `mt_core_status` | Get core server status (CPU, memory, latency) | — | — |
| `mt_core_license` | Get license info | — | — |
| `mt_core_health` | Get server health assessment | — | — |
| `mt_core_dashboard` | Get multi-server dashboard | — | — |
| `mt_exchange_summary` | Get exchange info summary | — | — |
| `mt_exchange_pairs` | List trade pairs | — | — |
| `mt_exchange_search` | Search trade pairs | `query` | — |
| `mt_exchange_pair_detail` | Get detailed info for a specific trade pair | `symbol` | — |
| `mt_exchange_ticker24` | Get 24h ticker price statistics for a symbol. Returns price change, high/low, volume, trade count. market_type FUTURES or SPOT (default: exchange-dependent). | `symbol` | — |
| `mt_exchange_klines` | Get candlestick/kline data for a symbol. Returns OHLCV data. market (FUTURES\|SPOT) lets you force the market type when a symbol exists on both; without it, the server falls back to its exchange-info pair cache and may pick the wrong one (e.g. BTCUSDT routes to SPOT on Binance unless overridden). | `symbol` | — |
| `mt_exchange_trades` | Get recent trades for a symbol from the exchange. | `symbol` | — |
| `mt_exchange_funding_rate` | Get the funding-rate fields for a symbol (last funding rate/time, next funding rate/time, mark price, last price). Read-only; no confirm. Returns whatever the symbol's live-markets cache currently holds, with up to a few seconds of warm-up after first subscription. | `symbol` | — |
| `mt_exchange_leverage_info` | Get configured/effective leverage, max leverage, and risk-limit data for a symbol from MTCore's LeverageInfoUpdateData cache. Read-only; no confirm. Does not require an open position, but the cache must have observed a core leverage refresh in this mt-text-client session. | `symbol` | — |
| `mt_exchange_leverage_brackets` | Compatibility alias for leverage info. Returns configured/effective leverage, max leverage, and risk-limit data from MTCore's LeverageInfoUpdateData cache when available, with open-position leverage only as a fallback. Read-only; no confirm. NOTE: full bracket-tier tables (notional-range → max-leverage map) are not exposed as separate MTShared rows. | `symbol` | — |
| `mt_algos_list` | List algorithms on active connection | — | — |
| `mt_algos_list_all` | List algorithms across ALL connections | — | — |
| `mt_algos_search` | Search algorithms by name/signature/symbol | `query` | — |
| `mt_algos_get` | Get algorithm details | `id` | — |
| `mt_algos_start` | Start an algorithm | `id` | — |
| `mt_algos_stop` | Stop an algorithm | `id` | — |
| `mt_algos_start_all` | Start all algorithms (requires confirm=true). Bulk operation: starts every algo on the target server. | `confirm` | ✓ |
| `mt_algos_start_verified` | Start an algorithm and verify it initialized successfully. Waits wait_secs seconds then checks isRunning, symbol, and marketType. Returns status: VERIFIED \| INIT_FAILURE_SUSPECTED \| RUNNING_UNCONFIRMED \| NOT_RUNNING. | `id` | — |
| `mt_algos_verify` | Verify current state of a running algorithm — checks for the silent-init-failure pattern (isRunning=true but symbol/market unresolved). Does NOT start the algo. | `id` | — |
| `mt_algos_stop_all` | Stop all algorithms (requires confirm=true). Bulk operation: stops every running algo on the target server. | `confirm` | ✓ |
| `mt_algos_batch_start` | Start an algorithm (matched by name/signature/symbol pattern) across multiple servers in parallel. Searches each server for algos matching the pattern and starts all matches. Use mt_algos_batch_stop to reverse. SAFETY: requires either explicit profiles or all_servers=true. | `algo` | — |
| `mt_algos_batch_stop` | Stop an algorithm (matched by name/signature/symbol pattern) across multiple servers in parallel. | `algo` | — |
| `mt_algos_batch_config` | Set a config parameter on matching algorithms across multiple servers. Changes are LOCAL — call algos save <id> @<profile> to persist to each Core. | `algo`, `key`, `value` | — |
| `mt_algos_save` | Save algorithm config changes | `id` | — |
| `mt_algos_save_start` | Save and start an algorithm | `id` | — |
| `mt_algos_delete` | Delete an algorithm (requires confirm=true) | `id`, `confirm` | ✓ |
| `mt_algos_toggle_debug` | Toggle debug/profiling mode | `id` | — |
| `mt_algos_rename` | Rename an algorithm | `id`, `name` | — |
| `mt_algos_config` | View algorithm configuration parameters | `id` | — |
| `mt_algos_config_set` | Set an algorithm config parameter | `id`, `key`, `value` | — |
| `mt_algos_groups` | List algorithm groups | — | — |
| `mt_algos_group` | List algorithms in a group | `group_id` | — |
| `mt_algos_clone_group` | Clone an algorithm group | `group_id` | — |
| `mt_algos_delete_group` | Delete an algorithm group (requires confirm=true) | `group_id`, `confirm` | ✓ |
| `mt_algos_copy` | Copy an algorithm from one server to another (requires confirm=true) | `id`, `destination_profile` | — |
| `mt_algos_export` | Export an algorithm as portable JSON for cross-server transfer | `id` | — |
| `mt_algos_copy_to_clipboard` | Serialise an algorithm to the schema-versioned clipboard file at ~/mt-clipboard/algo-clipboard.json.  Read-only on the source; never mutates the bench. Use mt_algos_paste_from_clipboard to apply on a destination. | `id` | — |
| `mt_algos_paste_from_clipboard` | Read the clipboard JSON, run cross-exchange pre-flight, and paste the algorithm onto the destination profile.  Without confirm=true, returns a DRY RUN preview listing every detected edge case (symbol mismatch, market change, duplicate, blacklist conflict).  override_symbol / override_market let the caller force the paste past a known mismatch.  Schema-version-mismatched payloads are REJECTED before any wire call. | `destination_profile`, `confirm` | ✓ |
| `mt_algos_create` | Create a new algorithm on a target profile by cloning a source algorithm's argsJson and applying user overrides on top.  Creation is clone-from-source: either pin an explicit source_algo_id, or specify algo_type to auto-discover a matching algorithm on the target profile (optionally filtered by preset_name=signature).  The source algorithm's argument template is cloned verbatim, with caller-supplied overrides applied on top — so any new vendor fields flow through without an MCP-side update. A small _mcp_metadata block is injected into the new algorithm's arguments (schema_version, source_algo_id, source_profile, created_at_utc) and is observable via mt_algos_config. DRY-RUN by default (omit no_dry_run to preview); commit requires no_dry_run=true AND confirm=true. | `profile`, `confirm` | ✓ |
| `mt_algos_bulk_edit` | Fan a single field-level mutation across many algorithms in one call. Filter selects which algos (by ids, group_id, or all). Mutation is one of: whitelist_add (array of symbols to append to each algo's whiteList parameter), whitelist_remove (array of symbols to drop), or set (an object of {paramKey: newValue} pairs). Without confirm=true the response is a DRY RUN preview showing per-algo current → proposed diffs and any warnings (schema_mismatch, blacklist_conflict, no_change). With confirm=true each affected algo is SAVE-dispatched individually; failures DO NOT abort the batch — every row's success/error is surfaced in partial_result. | `filter_json`, `mutation_json`, `confirm` | ✓ |
| `mt_algos_import_json` | Direct inline JSON paste (automation-friendly form; avoids the clipboard-file hop). Same edge-case pre-flight as mt_algos_paste_from_clipboard. The 'path' argument: when path is provided, the payload is read from that file instead of taken from the 'payload' arg. | `destination_profile`, `confirm` | ✓ |
| `mt_settings_get` | Get profile settings (all or specific key) | — | — |
| `mt_settings_search` | Search settings by keyword | `query` | — |
| `mt_settings_set` | Set a profile setting (requires confirm=true) | `key`, `value`, `confirm` | ✓ |
| `mt_settings_groups` | List settings grouped by prefix | — | — |
| `mt_import_templates` | List available algorithm templates from algorithms.json / algoConfigs.json. If 'path' is provided, reads from that file. Otherwise searches the default locations: <app-dir>/algorithms.json, <app-dir>/algoConfigs.json, ~/Documents/algorithms.json, ~/Documents/algoConfigs.json, /tmp/algorithms.json, /tmp/algoConfigs.json. | — | — |
| `mt_import_v2` | Import algorithms from V2 text format file | `path` | — |
| `mt_import_add_numeric` | Add numeric delta to all numeric params of an algorithm | `id`, `delta` | — |
| `mt_import_from_profile` | Survey what would be imported from source_profile to destination_profile. Returns one entry per source algorithm with its name, group, symbol, market, and a duplicate_on_destination flag.  Read-only — no mutation.  Use mt_algos_copy per-id or mt_algos_paste_from_clipboard / mt_algos_import_json to actually transfer. | `source_profile`, `destination_profile` | — |
| `mt_orders_list` | List active orders | — | — |
| `mt_orders_positions` | List open positions with PnL | — | — |
| `mt_orders_cancel` | Cancel a specific order (requires confirm=true) | `client_order_id` | — |
| `mt_orders_cancel_all` | Cancel all orders (requires confirm=true) | — | — |
| `mt_orders_close` | Close a position (requires confirm=true) | `symbol` | — |
| `mt_orders_close_all` | Close all positions (requires confirm=true) | — | — |
| `mt_orders_place` | Place a new order (market or limit). Requires confirm=true. If price is omitted, places a MARKET order. If price is set, places a LIMIT order. On hedge-mode FUTURES accounts, position_side must match the side book (LONG for BUY, SHORT for SELL); for SPOT/one-way leave position_side unset or BOTH. market (FUTURES\|SPOT) lets callers force the venue when a symbol exists in both — without it the server picks whichever pair-cache entry comes back first (BTCUSDT on Binance hits SPOT, which on this build refuses orders while SPOT UDS is offline). TPSL on placement: set tp_percent and/or sl_percent (with optional tp_type/sl_type/trailing_stop/trailing_spread) to attach take-profit and stop-loss settings to the new order at placement time. This is the safe pattern — orders placed without TPSL are not recorded in the reports DB even after they close. Use mt_orders_update_tpsl only to MODIFY an existing order's TPSL, not to attach it to a new one. See docs/TPSL_SAFETY_GUIDE.md. | `symbol`, `side`, `qty` | — |
| `mt_orders_move` | Move/modify price of an existing order (requires confirm=true) | `client_order_id`, `new_price` | — |
| `mt_orders_set_leverage` | Set leverage for a symbol (requires confirm=true) | `symbol`, `leverage` | — |
| `mt_orders_set_margin_type` | Set margin type CROSS or ISOLATED for a symbol (requires confirm=true) | `symbol`, `margin_type` | — |
| `mt_orders_set_position_mode` | Set position mode HEDGE or ONE_WAY for a symbol (requires confirm=true) | `symbol`, `mode` | — |
| `mt_orders_get_position_mode` | Get current position mode (HEDGE/ONE_WAY) for a SYMBOL. Per-symbol query — use this on Binance/OKX where position mode is configured per pair. On Bybit, position mode is account-wide and the per-symbol query is not supported by the vendor SDK; this tool returns a clear redirect to mt_orders_get_position_mode_account. | `symbol` | — |
| `mt_orders_get_position_mode_account` | Get current account-wide position mode (HEDGE/ONE_WAY). Use this on Bybit, where position mode is configured per account rather than per symbol. Reads the cached AccountInfo (priming the cache on first call so a cold connect still returns the real mode). On per-symbol exchanges (Binance/OKX) this tool returns a clear redirect to mt_orders_get_position_mode <symbol>. | — | — |
| `mt_orders_panic_sell` | EMERGENCY: Market-close all positions for a symbol immediately (requires confirm=true) | `symbol` | — |
| `mt_orders_change_margin` | Add or reduce isolated margin on a position (requires confirm=true) | `symbol`, `position_side`, `amount` | — |
| `mt_orders_transfer` | Transfer funds between SPOT and FUTURES accounts (requires confirm=true) | `asset`, `amount`, `from`, `to` | — |
| `mt_orders_set_leverage_buysell` | Set different buy and sell leverage for an asset (Bybit split leverage). Requires confirm=true. | `asset`, `buy_leverage`, `sell_leverage`, `confirm` | ✓ |
| `mt_orders_get_multiasset` | Query multi-asset margin mode status (enabled/disabled). | — | — |
| `mt_orders_set_multiasset` | Enable or disable multi-asset margin mode. Requires confirm=true. | `enabled`, `confirm` | ✓ |
| `mt_reports_trades` | Get trade reports: closed positions with P&L, fees, entry/exit prices. This is the HISTORICAL trading data — completed trades, not live fills. Use period shortcuts (today/24h/7d/30d/90d) or custom date range. | — | — |
| `mt_reports_comments` | Get report comment labels used in trade reports | — | — |
| `mt_reports_dates` | Get available report date markers | — | — |
| `mt_fleet_connect` | Connect to ALL configured server profiles at once (or filter by exchange/name). Returns connection status for each. Use this instead of multiple mt_connect calls. | — | — |
| `mt_fleet_status` | Get connection status overview for ALL servers in one call. Shows online/offline, uptime, algo counts per server. | — | — |
| `mt_fleet_balances` | Get balances across ALL connected servers in one call. Shows per-server USDT totals, asset counts, top holdings, and grand total. | — | — |
| `mt_fleet_positions` | Get ALL open positions across ALL connected servers. Shows symbol, side, entry, PnL per position with server attribution. | — | — |
| `mt_fleet_algos` | Get algorithm summary across ALL connected servers. Shows total/running counts per server, grouped by algo type. | — | — |
| `mt_fleet_health` | Health check ALL connected servers. Shows CPU, RAM, latency, UDS status, license per server with issue flags. | — | — |
| `mt_fleet_summary` | Comprehensive fleet overview in ONE call — the mega-dashboard. Grand total balance, PnL, algos, positions, per-exchange breakdown. Use this for periodic fleet status reports. | — | — |
| `mt_fleet_disconnect` | Disconnect from ALL servers at once (requires confirm=true). Fleet-wide operation. | `confirm` | ✓ |
| `mt_fleet_batch_connect` | Connect to a specific set of named profiles in parallel (max 10 concurrent). Unlike mt_fleet_connect (which connects ALL configured profiles), this accepts an explicit list — suited for targeted fleet orchestration by automation clients. | `profiles` | — |
| `mt_connection_health` | Connection pool health report — per-profile latency, error count, reconnect history, and backoff state. Use this to diagnose unstable connections and route around degraded servers. | — | — |
| `mt_connection_tag` | Set a fleet orchestration tag (key/value) on a named connection. Tags are in-memory labels like role=coordinator, strategy=scalper, group=us-east. Use mt_connection_tags to read them back. | `profile`, `key`, `value` | — |
| `mt_connection_tags` | List fleet orchestration tags for a connection or all connections. Returns a map of key→value labels set via mt_connection_tag. | — | — |
| `mt_monitor_start` | Start real-time core monitoring. Collects CPU, memory, threads, latency snapshots via UDP CoreStatusSubscription. Works with remote cores — no filesystem access needed. | — | — |
| `mt_monitor_stop` | Stop core monitoring and release the snapshot buffer. | — | — |
| `mt_monitor_status` | Get monitor status: running state, snapshots collected, buffer capacity, latest metrics. | — | — |
| `mt_monitor_health` | Health assessment with trend analysis. Checks CPU, memory, threads, exchange latency, UDS data streams, and detects memory/thread growth trends. Returns HEALTHY/WARNING/CRITICAL. | — | — |
| `mt_monitor_performance` | Get time-series performance snapshots. Each snapshot includes CPU, memory, threads, latency, UDS status. Start monitor first for history. | — | — |
| `mt_monitor_stats` | Aggregate statistics over the monitoring window: min/max/avg for CPU, memory, threads, latency. Shows trends and sample count. | — | — |
| `mt_autostops_list` | List auto-stop algorithm configurations and status. Shows balance/report filters and thresholds. | — | — |
| `mt_autostops_baseline` | Request auto-stop baseline recalculation on Core (fire-and-forget). | — | — |
| `mt_autostops_reports` | Get report data for auto-stop algorithms. Optionally filter by algorithm IDs. | — | — |
| `mt_autostops_add` | Append a new balance auto-stop filter. Created disabled — call mt_autostops_start to activate. Writes the real MTShared AutoStopAlgorithmData[] shape: max_loss → minMargin, symbols → symbolFilter, quotes/asset → asset, pause_algo → panicIfTriggered, timeframe_ms → AutoStopsTimeFrame. | `max_loss`, `confirm` | ✓ |
| `mt_autostops_edit` | Mutate an existing balance auto-stop filter at the given index. Every other field is optional — only the ones you pass are updated. | `index`, `confirm` | ✓ |
| `mt_autostops_start` | Enable one balance auto-stop filter, or all filters if index is omitted. | `confirm` | ✓ |
| `mt_autostops_stop` | Disable one balance auto-stop filter, or all filters if index is omitted. | `confirm` | ✓ |
| `mt_autostops_delete` | Remove a balance auto-stop filter at the given index. | `index`, `confirm` | ✓ |
| `mt_blacklist_list` | List current blacklist configuration: blocked markets, quote assets, and symbols. | — | — |
| `mt_blacklist_add` | Add an item to the blacklist. type=market needs market_type only; type=quote needs market_type+quote_asset; type=symbol needs market_type+quote_asset+symbol. Requires confirm=true. | `type`, `market_type`, `confirm` | ✓ |
| `mt_blacklist_remove` | Remove an item from the blacklist. type=market needs market_type only; type=quote needs market_type+quote_asset; type=symbol needs market_type+quote_asset+symbol. Requires confirm=true. | `type`, `market_type`, `confirm` | ✓ |
| `mt_whitelist_list` | List profile-level WhiteList contents: WhiteList.Symbols (typed entries: market/quote/symbol), WhiteList.Quotes (typed entries: market/quote), WhiteList.Only toggle. This is the PROFILE-level whitelist (which pairs the profile is allowed to trade), distinct from per-algo whiteList that mt_algos_bulk_edit mutates. | — | — |
| `mt_whitelist_add` | Add ONE typed whitelist entry. type=symbol needs market+quote+symbol; type=quote needs market+quote. If the entry is already present the call is a no-op (already_present warning).  For type=symbol the tool also warns when the value isn't in the destination's ExchangeInfoStore pair cache (not_tradable). | `type`, `market`, `quote`, `confirm` | ✓ |
| `mt_whitelist_remove` | Remove ONE typed whitelist entry. Removing a value that isn't present surfaces a structured 'not_found' error. | `type`, `market`, `quote`, `confirm` | ✓ |
| `mt_whitelist_bulk_add` | Add MANY whitelist entries in one call. For type=symbol, the (market, quote) prefix is constant and 'symbols' is a comma-separated list; for type=quote, 'quotes' is a comma-separated list of quote assets under the given market. Items already present surface as already_present warnings; for type=symbol any item not resolvable on the exchange surfaces as not_tradable (item still lands; MTCore decides at order-place time). | `type`, `market`, `confirm` | ✓ |
| `mt_whitelist_bulk_remove` | Remove MANY whitelist entries in one call. Items not present surface as not_found warnings; an all-not-found call fails with a structured error. | `type`, `market`, `confirm` | ✓ |
| `mt_tpsl_list` | List all TPSL (Take Profit / Stop Loss) positions. Auto-primes the TPSL cache via a transient AlgorithmTPSLs subscribe on each call (vendor V2 pattern), so no explicit mt_tpsl_subscribe is required for read-only listing. | — | — |
| `mt_tpsl_subscribe` | Subscribe to TPSL position updates from Core. Data available via mt_tpsl_list. | — | — |
| `mt_tpsl_unsubscribe` | Unsubscribe from TPSL position updates. | — | — |
| `mt_tpsl_cancel` | Cancel a TPSL (Take Profit / Stop Loss) position by ID. Requires an active TPSL subscription (call mt_tpsl_subscribe first). The id can be obtained from mt_tpsl_list. Requires confirm=true. | `id`, `confirm` | ✓ |
| `mt_perf_list` | List trading performance data. Requires active performance subscription. | — | — |
| `mt_perf_subscribe` | Subscribe to trading performance updates from Core. | — | — |
| `mt_perf_unsubscribe` | Unsubscribe from trading performance updates. | — | — |
| `mt_perf_request` | Request trading performance data refresh or reset. | — | — |
| `mt_reports_export` | Export trade reports to CSV file. Supports all standard report filters. Returns the file path of the exported CSV. | — | — |
| `mt_reports_fleet_export` | Export trade reports from ALL connected servers merged into a single CSV file. Trades are sorted by close time across all servers. Ideal for consolidated P&L analysis. | — | — |
| `mt_reports_store` | Store trade report query results locally with a name. Stored sets can be retrieved, displayed, and exported later without re-querying Core. | `name` | — |
| `mt_reports_stored` | List all locally stored report sets with summary statistics. Shows name, server, trade count, PnL, win rate, capture time. | — | — |
| `mt_reports_load` | Load a previously stored report set by name and display its trade table and summary stats. The tool returns formatted text output; it does not return raw rows for further processing. Use mt_reports_stored to list available stored sets first. | `name` | — |
| `mt_reports_delete` | Delete a stored report set by name. | `name` | — |
| `mt_reports_query` | Run a trade-report query and return structured JSON rows. Each row carries id, open/close prices, qty, USDT-denominated profit/commission/total, symbol, market_type, order side, closed_by, and timestamps.  The same rich filters as mt_reports_trades are supported.  This is the structured-client variant — mt_reports_trades formats text; mt_reports_query is consumable as JSON. | — | — |
| `mt_reports_csv_inline` | Same filters as mt_reports_query but returns a CSV string in the response body (no file written).  Useful when the agent wants to feed CSV directly into another tool without round-tripping through the filesystem. | — | — |
| `mt_reports_cancel` | Cancel a query by request_id. The underlying report-query call is currently synchronous (~30s wire time), so cancellation cannot interrupt an in-flight request — this tool acknowledges and records the cancellation intent so the caller can observe it. | `request_id` | — |
| `mt_reports_status` | Look up a query by request_id and return its status, filter summary, latency, row_count, and end state.  Returns a structured 'not_found' envelope when the request_id is unknown. | `request_id` | — |
| `mt_fleet_autostops` | Query auto-stop configuration across ALL connected servers. Shows which servers have balance/report filters configured. | — | — |
| `mt_fleet_blacklist` | Query blacklist configuration across ALL connected servers. Shows market/quote/symbol filter counts per server. | — | — |
| `mt_fleet_perf` | Query trading performance subscription status across ALL connected servers. Shows entry counts and subscription state per server. | — | — |
| `mt_fleet_reports` | Query trade reports across ALL connected servers with per-server P&L breakdown. Shows trades, PnL, fees, win rate, volume per server with fleet totals. | — | — |
| `mt_fleet_set_margin_type` | Apply a CROSS/ISOLATED margin-type change to <symbol> across the entire fleet (or a filtered subset).  WITHOUT confirm=true the response is a DRY RUN preview: per-profile current_margin (where observable), proposed_margin, would_change flag, and any skip_reason (disconnected, symbol_not_in_pair_cache).  Touches venue-side state on commit and is only reversible by another call — the dry_run contract is the safety boundary. | `symbol`, `margin_type`, `confirm` | ✓ |
| `mt_notifications_list` | List cached notifications from Core. Shows type, time, and message. | — | — |
| `mt_notifications_subscribe` | Subscribe to real-time notifications from Core (deal complete, order fill, liquidation, alerts, errors). | — | — |
| `mt_notifications_unsubscribe` | Unsubscribe from notifications. | — | — |
| `mt_notifications_clear` | Clear cached notification history for a connection. | — | — |
| `mt_notifications_config_groups` | List all notification-group values (TRADE, SYSTEM, …). | — | — |
| `mt_notifications_config_targets` | List all notification-target values (CLIENT_NOTIFICATIONS, CLIENT_LOG, TELEGRAM). | — | — |
| `mt_notifications_config_descriptors` | List every toggleable notification descriptor with its group, id, data type, and per-target default-enabled flags. | — | — |
| `mt_notifications_config_capabilities` | Combined notifications-config envelope: groups + targets + descriptors + a mutation_supported flag and an honest notice when notification-config mutation is not yet available. | — | — |
| `mt_marketdata_status` | Show all active market data subscriptions (trades, depth, mark price, klines, tickers). | — | — |
| `mt_marketdata_trades` | View recent trade data for a symbol. Requires active trade subscription (use mt_marketdata_trades_subscribe first, or mt_exchange_trades for a one-shot snapshot without subscription). | `symbol` | — |
| `mt_marketdata_trades_subscribe` | Subscribe to real-time trade feed for a symbol. | `symbol` | — |
| `mt_marketdata_trades_unsubscribe` | Unsubscribe from trade feed for a symbol. | `symbol` | — |
| `mt_marketdata_depth` | View order book (top 10 bids/asks) for a symbol. Requires active depth subscription (use mt_marketdata_depth_subscribe first; there is no one-shot snapshot equivalent in the exchange family for orderbook depth). | `symbol` | — |
| `mt_marketdata_depth_subscribe` | Subscribe to real-time order book (depth) feed for a symbol. | `symbol` | — |
| `mt_marketdata_depth_unsubscribe` | Unsubscribe from depth feed for a symbol. | `symbol` | — |
| `mt_marketdata_markprice` | View mark price, funding rate, and next funding time for a symbol. Requires active mark price subscription (use mt_marketdata_markprice_subscribe first, or mt_exchange_funding_rate for a one-shot snapshot without subscription). | `symbol` | — |
| `mt_marketdata_markprice_subscribe` | Subscribe to real-time mark price and funding rate updates for a symbol. | `symbol` | — |
| `mt_marketdata_markprice_unsubscribe` | Unsubscribe from mark price feed for a symbol. | `symbol` | — |
| `mt_marketdata_klines` | View last kline (candlestick) data for a symbol and interval. Requires active kline subscription (use mt_marketdata_klines_subscribe first, or mt_exchange_klines for a one-shot OHLCV snapshot without subscription). | `symbol`, `interval` | — |
| `mt_marketdata_klines_subscribe` | Subscribe to real-time kline (candlestick) updates for a symbol and interval. | `symbol`, `interval` | — |
| `mt_marketdata_klines_unsubscribe` | Unsubscribe from kline feed for a symbol and interval. | `symbol`, `interval` | — |
| `mt_marketdata_ticker` | View ticker data (last price, 24h volume, OHLC) for a symbol or for all symbols on a market. Two modes:  - symbol=BTCUSDT  → per-symbol one-shot via ticker24; ALWAYS returns fresh data without subscription (uses the same wire path as mt_exchange_ticker24).  This is the path that works on every vendor build, including BYBIT bench where the bulk SUBSCRIBE_TICKER stream does not push frames.  - no symbol      → bulk cache-read for ALL symbols on the requested market type; auto-primes the cache with a transient subscribe if cold. Returns a clear FAIL response (isError=true) when the bulk subscribe yields no data within the wire timeout. | — | — |
| `mt_marketdata_ticker_subscribe` | Subscribe to real-time ticker updates for ALL symbols on a market. | — | — |
| `mt_marketdata_ticker_unsubscribe` | Unsubscribe from ticker feed. | — | — |
| `mt_alerts_list` | List active price alerts with conditions and status. | — | — |
| `mt_alerts_subscribe` | Subscribe to real-time alert updates (new, modified, deleted alerts). | — | — |
| `mt_alerts_unsubscribe` | Unsubscribe from alert updates. | — | — |
| `mt_alerts_history` | View alert trigger history. | — | — |
| `mt_alerts_history_subscribe` | Subscribe to alert history updates. | — | — |
| `mt_alerts_history_unsubscribe` | Unsubscribe from alert history updates. | — | — |
| `mt_alerts_save` | Create or update a single price alert. When alert_id is omitted (or 0), a new alert is created; non-zero alert_id updates that alert. | `name`, `symbol`, `market_type`, `condition_type`, `ref_price` | — |
| `mt_alerts_delete` | Delete alert(s) by id, or delete ALL alerts on the profile. Requires confirm=true. | `confirm` | ✓ |
| `mt_alerts_set_running` | Start (running=true) or stop (running=false) alert(s). Requires confirm=true. | `running`, `confirm` | ✓ |
| `mt_profiling_subscribe` | Subscribe to real-time algorithm profiling data stream. | `symbol`, `algo_id` | — |
| `mt_profiling_unsubscribe` | Unsubscribe from algorithm profiling data stream. | `symbol`, `algo_id` | — |
| `mt_triggers_list` | List received trigger events. | — | — |
| `mt_triggers_subscribe` | Subscribe to trigger events. | — | — |
| `mt_triggers_unsubscribe` | Unsubscribe from trigger events. | — | — |
| `mt_triggers_save` | Save/create a trigger action. | `data` | — |
| `mt_triggers_delete` | Delete a trigger action. | `data` | — |
| `mt_triggers_start` | Start a trigger action. | `data` | — |
| `mt_triggers_stop` | Stop a trigger action. | `data` | — |
| `mt_triggers_start_all` | Start all trigger actions. | — | — |
| `mt_triggers_stop_all` | Stop all trigger actions. | — | — |
| `mt_livemarkets_list` | List live market metrics data. | — | — |
| `mt_livemarkets_subscribe` | Subscribe to live market metrics streaming. | — | — |
| `mt_livemarkets_unsubscribe` | Unsubscribe from live market metrics. | — | — |
| `mt_autobuy_list` | List AutoBuy (DCA/recurring buy) events. | — | — |
| `mt_autobuy_subscribe` | Subscribe to AutoBuy events. | — | — |
| `mt_autobuy_unsubscribe` | Unsubscribe from AutoBuy events. | — | — |
| `mt_autobuy_save` | Save/create an AutoBuy configuration. | `data` | — |
| `mt_autobuy_delete` | Delete an AutoBuy configuration. | `data` | — |
| `mt_autobuy_start` | Start an AutoBuy configuration. | `data` | — |
| `mt_autobuy_stop` | Stop an AutoBuy configuration. | `data` | — |
| `mt_autobuy_refresh_pairs` | Refresh AutoBuy asset pair lists. | — | — |
| `mt_graphtool_list` | List graph tool (chart drawing) events. | — | — |
| `mt_graphtool_subscribe` | Subscribe to graph tool events. | — | — |
| `mt_graphtool_unsubscribe` | Unsubscribe from graph tool events. | — | — |
| `mt_graphtool_save` | Save a graph tool (chart drawing). | `data` | — |
| `mt_graphtool_delete` | Delete a graph tool (chart drawing). | `data` | — |
| `mt_signals_send` | Send an external trading signal to MTCore for automated execution. | `symbol`, `side`, `price` | — |
| `mt_dust_get` | Get dust (small balance) information for potential conversion. | — | — |
| `mt_dust_convert` | Convert dust (small balances) to main asset. | — | — |
| `mt_deposit_info` | Get deposit information for a coin (networks, limits). | `coin` | — |
| `mt_deposit_address` | Get deposit address for a coin and network. | `coin`, `network` | — |
| `mt_orders_move_batch` | Move multiple orders to new prices in a single batch. | `orders_json` | — |
| `mt_orders_join` | Join (merge) split orders back into one. | `client_order_id` | — |
| `mt_orders_split` | Split an order into multiple smaller orders. | `client_order_id` | — |
| `mt_fund_transfer` | Transfer funds between accounts (FUNDING <-> TRADING). | `from_account`, `asset`, `amount`, `to_account` | — |
| `mt_profile_settings_get` | Get profile-level settings (all server configuration key-values). | — | — |
| `mt_profile_settings_list` | List the KEYS of the connected profile's settings (read-only). Enumerates the keys of the CURRENT profile on the connection — the underlying call does not expose a list-named-profiles surface. Optional substring filter via 'grep'. | — | — |
| `mt_profile_settings_delete` | Delete one or more profile-settings keys. Accepts a single comma-separated 'keys' string. Edge cases: not_found surfaces as a structured top-level error when ALL keys are absent; if some keys exist and others don't, the present ones are deleted and the absent ones surface in NotFound. | `keys`, `confirm` | ✓ |
| `mt_profile_settings_update` | Update one or more profile settings on the connected MTCore. updates_json is a flat JSON object mapping setting keys to string values, e.g. {"BlackList.FirstInitialization":"1","NewListedMarket.AddToBlacklistEnabled":"1"}. Setting values are always strings on the wire; numbers and booleans must be quoted. Some keys (e.g. blacklist arrays) require typed-object JSON values — see mt_blacklist_* tools. Some changes require a Core restart to take full effect (this is reported in the tool response). Requires confirm=true. | `profile_name`, `updates_json`, `confirm` | ✓ |
| `mt_profiles_list` | List every profile in ~/.config/mt-textclient/profiles.json with its folder + connection data. | — | — |
| `mt_profiles_add` | Add a new profile to profiles.json.  Required: name, address, port, token, exchange. Optional: folder (must already exist via mt_folders_add).  Idempotent on name: duplicate names refused. | `name`, `address`, `port`, `token`, `exchange`, `confirm` | ✓ |
| `mt_profiles_edit` | Edit a profile by name.  Pass any subset of --address / --port / --token / --exchange / --folder / --rename.  Empty mutation block = no_change. | `name`, `confirm` | ✓ |
| `mt_profiles_delete` | Remove a profile from profiles.json.  Surfaces 'not_found' if the name doesn't exist. | `name`, `confirm` | ✓ |
| `mt_profiles_move` | Move a profile to a different folder.  The destination folder must already exist (use mt_folders_add to create it first); empty 'folder' moves the profile to root. | `name`, `folder`, `confirm` | ✓ |
| `mt_profiles_import_csv` | Bulk-import profiles from a CSV file.  CSV must declare a header row with at minimum the columns: name, address, port, token, exchange.  Optional column: folder.  Duplicate names and parse errors (bad port / unknown exchange) surface per-row in the response; the operation is additive (existing profiles are not touched). | `path`, `confirm` | ✓ |
| `mt_folders_list` | List every known folder in ~/.config/mt-textclient/folders.json with the count of profiles currently in each.  Also surfaces ORPHAN folders: folder names that profiles reference but that are missing from folders.json (use mt_folders_add to canonicalise). | — | — |
| `mt_folders_add` | Add a new known folder name.  Idempotent: adding an existing folder is a no_change. | `name`, `confirm` | ✓ |
| `mt_folders_edit` | Rename a folder.  Renames the entry in folders.json AND cascades the rename to every profile currently in that folder (profiles.json is rewritten). | `old_name`, `new_name`, `confirm` | ✓ |
| `mt_folders_delete` | Delete a folder from folders.json.  WARNING: if any profiles are still in this folder they become ORPHAN (their Folder field still references the now-deleted name).  Use mt_profiles_move to re-bind them first. | `name`, `confirm` | ✓ |

_Total: 262 tools._

<!-- END AUTOGENERATED REGISTRY TABLE -->
