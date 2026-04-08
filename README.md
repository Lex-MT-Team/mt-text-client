# MTTextClient

Text-first interface and MCP server for [MoonTrader](https://moontrader.com) Core (MTCore). Connects to one or more MTCore instances over encrypted UDP and exposes 81 tools for algorithm lifecycle management, order execution, account monitoring, real-time core monitoring, and more.

## Architecture

```
┌─────────────────────┐         ┌─────────────────┐
│  MTTextClient       │  UDP    │  MTCore          │
│  (this project)     │◄───────►│  (trading engine) │
│  REPL / MCP server  │ AES256  │  Bybit exchange   │
└─────────────────────┘         └─────────────────┘
```

MTTextClient communicates with MTCore over LiteNetLib UDP (default port 4242) with AES-256 encryption derived from a per-profile client token. **All features work remotely** — no filesystem access to the MTCore machine is required.

## Requirements

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- `MTShared.dll` and `LiteNetLib.dll` (included in `lib/`)

## Build

```bash
dotnet build -c Release
```

Output: `bin/Release/net8.0/MTTextClient.dll`

## Usage

MTTextClient runs in three modes:

### 1. Interactive REPL (default)

```bash
dotnet run
```

Provides a command-line interface for managing MTCore connections, algorithms, orders, positions, and monitoring.

### 2. Single Command

```bash
dotnet run -- status
dotnet run -- algos list
```

Executes a single command and exits.

### 3. MCP Server (for AI agents)

```bash
dotnet run -- --mcp
```

Runs a [Model Context Protocol](https://modelcontextprotocol.io) server over stdio (JSON-RPC). Typically fronted by an SSE proxy:

```bash
mcp-proxy --sse-port 8585 -- dotnet run -- --mcp
```

## Server Profiles

Connection profiles are stored at `~/.config/mt-textclient/profiles.json`:

```json
[
  {
    "Name": "my_core",
    "Address": "203.0.113.50",
    "Port": 4242,
    "ClientToken": "<your-client-token>",
    "Exchange": 4
  }
]
```

| Field | Description |
|-------|-------------|
| `Name` | Profile identifier (used in `connect <name>`) |
| `Address` | MTCore IP address or hostname |
| `Port` | MTCore UDP port (default: 4242) |
| `ClientToken` | Authentication token from MTCore |
| `Exchange` | Exchange type enum: 1 = Binance, 2 = OKX, 4 = Bybit |

Manage profiles via the REPL:
```
profile list
profile add <name> <ip> <port> <token> [exchange]
profile remove <name>
```

## Project Structure

```
MTTextClient/
├── Program.cs                  # Entry point (REPL, single-cmd, MCP modes)
├── MTTextClient.csproj         # Project file
├── lib/                        # Binary dependencies
│   ├── MTShared.dll            # MoonTrader shared library
│   └── LiteNetLib.dll          # UDP networking library
├── Core/                       # Connection and data management
│   ├── CoreConnection.cs       # Single MTCore connection wrapper
│   ├── ConnectionManager.cs    # Multi-connection orchestrator
│   ├── ConnectionPump.cs       # Network event pump
│   ├── ProfileManager.cs       # Profile load/save
│   ├── ServerProfile.cs        # Profile data model
│   ├── AlgorithmStore.cs       # Algorithm state cache
│   ├── AccountStore.cs         # Account/balance state cache
│   ├── ExchangeInfoStore.cs    # Trading pair info cache
│   ├── CoreStatusStore.cs      # Core status/license cache
│   └── ProfileSettingsStore.cs # Per-profile settings cache
├── Commands/                   # REPL command implementations
│   ├── CommandRegistry.cs      # Command routing
│   ├── ICommand.cs             # Command interface
│   ├── ConnectionCommands.cs   # connect, disconnect, status, use
│   ├── AlgosCommand.cs         # Algorithm lifecycle (945 LOC)
│   ├── OrdersCommand.cs        # Order/position management (800 LOC)
│   ├── AccountCommands.cs      # Balance, summary, executions
│   ├── ExchangeCommand.cs      # Market data, klines, ticker
│   ├── MonitorCommand.cs       # Real-time core monitoring (UDP-based)
│   ├── SettingsCommand.cs      # Core settings get/set
│   ├── FleetCommand.cs         # Multi-server fleet operations
│   ├── ImportCommand.cs        # V2 algorithm import
│   ├── ReportsCommand.cs       # Trade reports
│   ├── CoreStatusCommand.cs    # License, health dashboard
│   ├── ProfileCommand.cs       # Profile CRUD
│   └── HelpCommand.cs          # Help text
├── MCP/                        # MCP server implementation
│   └── McpServer.cs            # JSON-RPC stdio server
├── Monitoring/                 # Real-time core monitoring (UDP-based)
│   ├── MonitorBuffer.cs        # Time-series ring buffer of status snapshots
│   └── MonitorAnalyzer.cs      # Trend analysis, health assessment
├── Import/                     # Algorithm import
│   └── V2FormatParser.cs       # V2 format parser
├── Output/                     # Display formatting
│   ├── OutputManager.cs        # JSON/table/text output
│   └── TableBuilder.cs         # ASCII table renderer
└── web/                        # Browser dashboard
    └── index.html              # Zero-dependency MCP dashboard
```

## MCP Tools

When running in `--mcp` mode, the following tool categories are available:

| Category | Tools | Description |
|----------|-------|-------------|
| Connection | `mt_connect`, `mt_disconnect`, `mt_status`, `mt_use` | Server lifecycle |
| Algorithms | `mt_algos_list`, `mt_algos_get`, `mt_algos_start`, `mt_algos_stop`, `mt_algos_save`, `mt_algos_delete`, `mt_algos_clone_group`, ... | Full algo lifecycle |
| Orders | `mt_orders_place`, `mt_orders_cancel`, `mt_orders_close`, `mt_orders_move`, `mt_orders_set_leverage`, ... | Order management |
| Account | `mt_account_balance`, `mt_account_summary`, `mt_account_positions`, `mt_account_orders` | Account data |
| Exchange | `mt_exchange_pairs`, `mt_exchange_klines`, `mt_exchange_ticker24`, `mt_exchange_trades` | Market data |
| Monitor | `mt_monitor_start`, `mt_monitor_stop`, `mt_monitor_status`, `mt_monitor_health`, `mt_monitor_performance`, `mt_monitor_stats` | Real-time core monitoring via UDP |
| Settings | `mt_settings_get`, `mt_settings_set`, `mt_settings_search` | Core configuration |
| Fleet | `mt_fleet_connect`, `mt_fleet_status`, `mt_fleet_algos`, `mt_fleet_positions` | Multi-server ops |
| Import | `mt_import_v2`, `mt_import_templates` | Algorithm import |

### Monitor Tools

The monitor subsystem collects real-time performance data from MTCore via the UDP CoreStatusSubscription. Unlike the previous log-based system, **it works with remote cores** — no filesystem access needed.

| Tool | Description |
|------|-------------|
| `mt_monitor_start` | Begin collecting status snapshots into a ring buffer |
| `mt_monitor_stop` | Stop monitoring and release the buffer |
| `mt_monitor_status` | Current state: running, snapshots collected, latest metrics |
| `mt_monitor_health` | Health assessment (HEALTHY/WARNING/CRITICAL) with trend analysis |
| `mt_monitor_performance` | Time-series snapshots: CPU, RAM, threads, latency |
| `mt_monitor_stats` | Aggregate min/max/avg statistics over the monitoring window |

Data collected per snapshot: core CPU%, system CPU%, core memory, system memory, free memory, thread count, exchange latency, peer latency, UDS data stream status, API loading.

### Future Monitor Enhancements

The following MTCore notification types can be added as monitoring features when proper test coverage is available:

| Notification | Description |
|-------------|-------------|
| CPU Alert | Triggered when core CPU exceeds threshold |
| RAM Alert | Triggered when memory exceeds threshold |
| Algorithm Error | Per-algorithm error notifications |
| System Overload Stop | When core auto-stops algorithms due to overload |
| Trading Performance | Per-algorithm daily/weekly PnL analytics |
| Watchdog Events | Debug/restart event notifications |

## Web Dashboard

A zero-dependency browser dashboard is included in `web/index.html`. It connects directly to the MCP server over SSE.

### Quick Start

```bash
# 1. Start MCP server with SSE proxy (requires mcp-proxy: pip install mcp-proxy)
mcp-proxy --sse-port 8585 --allow-origin '*' -- dotnet run -- --mcp

# 2. Serve the dashboard (separate terminal)
python3 -m http.server 9090 -d web
```

Access at: **http://localhost:9090**

### Features

- Auto-discovers all MCP tools from the running server
- Categorized tool guide with architecture diagram
- Server profile management (connect, disconnect, switch active)
- Execute any tool with form-based parameter input
- Three response views: Beautified, Raw JSON, and How It Works
- Batch execution across multiple profiles
- Dark theme, fully responsive, localStorage persistence

## License

MIT — see [LICENSE](LICENSE).
