# Changelog

All user-visible changes to MTTextClient are recorded here.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/).
Versions follow [SemVer](https://semver.org).

---

## 0.9.0 — 2026-05-05

### Added

* `mt_account_balance`, `mt_account_orders`, `mt_account_positions` — optional
  `show_all` boolean to include dust / archived / closed rows that the CLI
  previously surfaced via the `-all` flag.
* `mt_account_executions` — optional `count` integer to override the default
  tail size (mirrors `account executions <n>` in the REPL).
* `mt_exchange_ticker24` — optional `market_type` of `FUTURES` or `SPOT`;
  garbage values are silently ignored, control characters are stripped at
  the gateway sanitizer.
* `algos list`, `algos list-all`, `algos list-grouped`, `algos info` —
  additional `CoreName` field carrying the raw on-wire algorithm name
  alongside the human-readable `name`.
* MCP gateway now enforces `inputSchema.required` at the JSON-RPC boundary
  with a `-32602` error that names the missing field.
* `inputSchema.required` for `mt_fleet_batch_connect.profiles` is now
  declared (server-side enforcement closes the previously silent omission).
* JSON-RPC error envelopes echo the original request `id` so async batches
  can reconcile failures.
* `mt_profile_settings_get` now propagates underlying core failure markers
  instead of returning `success:true` with an empty payload.

### Changed

* `algos list` and friends now resolve display name in priority order
  `info parameter` → `description` → `name`, with synthetic
  `mt-algo-XXXXXX` values filtered out so operator-set labels surface
  consistently.
* V2 import remap (`Commands/ImportCommand.cs` and `MCP/McpServer.cs`) no
  longer skips entries whose `groupID` happens to be `0`. Algorithms
  belonging to a `GROUP_START 0` block now retain their group binding on
  the destination core.

### Fixed

* Silent ungrouped-import data corruption when V2 bundles contained a
  `GROUP_START 0` block (issue #13).
* `algos list` output hiding operator-set labels behind the synthetic
  `mt-algo-XXXXXX` name (issue #15).
* MCP wrappers offering no way to access dust balances, archived orders,
  closed positions, custom execution tails, or the FUTURES/SPOT side of a
  24h ticker (issue #16).

### Build

* `dotnet build -c Release` — 0 warnings, 0 errors.
* Tool count: **206** published via `tools/list`.

---

## 0.8.0 — 2026-05-04

### Added

* 18 previously unregistered MCP commands wired up: AutoStops, Blacklist,
  TPSL, Performance, Notifications, MarketData, Alerts, Profiling, Triggers,
  LiveMarkets, AutoBuy, GraphTool, Signals, Dust, Deposit, Funding,
  BuyApiLimit, Help. Tool count rose from 188 to 206.
* `mt_status` now reports `STALE` for connections idle longer than 60 s
  on the UDP heartbeat, instead of rendering them as healthy.
* Synchronous post-restart probe on `mt_core_restart`,
  `mt_core_restart_update`, `mt_core_clear_orders`. Calls wait up to 12 s
  for the core to come back and report real success/failure.
* `confirm=true` is now required for every destructive tool:
  `mt_algos_delete*`, `mt_orders_cancel*`, `mt_orders_close*`,
  `mt_algos_start_all`, `mt_algos_stop_all`, `mt_fleet_disconnect`,
  `mt_core_restart*`, `mt_core_clear_orders`.
* MCP gateway argument sanitizer rejects `\r` / `\n` and malformed `profile`
  values with `-32602` before they can reach the REPL dispatcher.
* `OutputManager.RenderTable` is null-safe — null elements degrade to
  `<null>` placeholder instead of NRE.
* Vault `HttpClient` honours `VAULT_HTTP_TIMEOUT_SEC` (clamped 1–120,
  default 10 s) instead of the .NET default 100 s.
* Blacklist storage rewritten to typed JSON objects
  (`MarketTypes`, `Quotes`, `Symbols`); typed reads stop the previous
  silent CSV corruption of MTCore risk-management config.
* `CircuitBreaker` state transitions now use compare-and-swap; phantom
  trip-count inflation under concurrent failures resolved.
* `profiles.json` is written atomically at mode `0600`; parent
  `~/.config/mt-textclient/` is created at mode `0700`. Existing files are
  auto-tightened on load with a warning.

### Changed

* `V2FormatParser.HasBalancedBraces` is now quote-aware so imported algo
  groups containing `{` inside string values no longer silently corrupt.
* `mt_algos_delete` / `mt_algos_delete_group` are now idempotent: deleting
  an already-absent id returns `ok` instead of failing.

### Fixed

* `Unknown command` failure for 18 MCP tool families that were missing
  from `McpServer.InitializeCommands` (the registration was a hand-edited
  subset of the REPL list).
* CORS-permissive sample command in the dashboard docs is now flagged as
  a development-only setting.

---

For pre-0.8.0 history, see the merged PR list on GitHub.
