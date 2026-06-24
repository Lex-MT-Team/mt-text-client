# Changelog

All user-visible changes to MTTextClient are recorded here.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/).
Versions follow [SemVer](https://semver.org).

---

## Unreleased

### MoonTrader 0.7.24554 new-feature surfacing

* **Shot Detect algorithm** — `SHOT_DETECT` added to the `mt_algos_create`
  `algo_type` enumerations (registry, CLI usage, error text, README); the new
  `SHOT_DETECT_ORDER_BEHAVIOR` / `SHOT_DETECT_BUFFER_TYPE` argument types now
  render as `enum` (not `complex`) in `mt_algos_config`; and the `mt_algos_verify`
  silent-init (BUG13) carve-out now exempts `SHOT_DETECT` group parents the same
  way it exempts SHOTS scanners.
* **Risk Limit** — `mt_core_license` now surfaces the per-account license caps
  added in 0.7.24554: `ManualOrderLimits`, `AlgoOrderLimits`, the balance-limit
  policy (`BalanceLimitInfo`: percent/fixed/asset), and `ExchangeUID`.
* **Wire-version handshake guard** — the client pins an expected core build
  (`CoreStatusStore.ExpectedCoreBuild`, bumped with the vendor DLL) and compares
  it to the connected core's reported `buildVersion` on the initial status
  update. `mt_core_license` reports `BuildVersionMatch` and `mt_core_health`
  raises a warning on mismatch, since a build skew means struct layouts can
  silently disagree. buildVersion is serialized ahead of the changed
  CoreStatusData fields, so it reads correctly even under skew.
* Reference Price for the Averages algorithm (`priceDistanceType` /
  `klineInterval`) remains reachable via the `algos config set
  algorithmParameters` escape hatch; a first-class set path + template default
  are deferred pending a live-confirmed default.

### Vendor upgrade to MoonTrader 0.7.24554

* **Bumped the vendored protocol stack to MTCore 0.7.24554** —
  `lib/MTShared.dll` and `lib/LiteNetLib.dll` (LiteNetLib v2.1.2.0; the new
  MTShared requires its `OnNtpResponse` entrypoint) refreshed together, and
  `scripts/fetch_vendor_libs.py` `VENDOR_VERSION` bumped to `724554`. These must
  move in lockstep with the core — mixing a 723902 client DLL with a 724554 core
  silently mis-reads several wire structs.
* **Auto-stops rewritten onto the new AUTO_STOP request/event subsystem.**
  0.7.24554 removed the `AutoStopAlgorithmData` type and the
  `AutoStopAlgorithm.Balance.Filters` settings-blob model that
  `mt_autostops_*` wrote to (the model that PR #41 had aligned to the old
  vendor shape). Balance auto-stops are now read from a live AUTO_STOP
  subscription snapshot (`AutoStopStore`, fed by `AutoStopListEvent` +
  incremental Added/Updated/Removed events) and mutated via
  `AUTO_STOP_REQUEST` (Add/Update/Run/Stop/Remove) carrying the real
  `AutoStopOnBalanceData`. `mt_autostops_add`/`edit` field set follows the
  new type: `max_loss` (maxLoss), `name`, `market`, `asset`, `keywords`
  (+`exclude_keywords`), `panic_sell` (panicSellIfTriggered); the removed
  filter_type/source_type/timeframe/comment fields are gone. `start`/`stop`/
  `edit`/`delete` index args are positions in the `mt_autostops_list`
  snapshot. `baseline` and `reports` are unchanged (they ride the surviving
  AUTO_STOPS_ALGORITHM family).

### Build

* **Linux test runs can now load the vendor assembly.** The committed
  `lib/MTShared.dll` carries PE Machine=ARM64 (Apple Silicon hosts load
  it natively), which the CLR on linux-x64 rejects during probing — the
  unit tests that construct vendor wire types failed on Linux CI with
  `FileNotFoundException`. The patch script now takes `--machine
  x64|arm64`, and Linux builds flip the *build-output* copy back to x64
  post-build (the committed file is never modified). The test project's
  vendor reference also uses a cross-platform path and explicit
  copy-local, fixing the DLL missing from the Linux test output
  entirely.

### Snapshot freshness (issue #40)

* **`mt_algos_snapshot` no longer serves a possibly-stale cache.** The
  long-lived algorithms subscription does not always deliver an initial
  snapshot, so on a quiet or freshly-connected profile the snapshot tool
  could return empty/stale data unless `mt_algos_list` happened to run
  first. The handler now pulls a fresh algorithm read per connected
  profile (the same transient-subscribe prime the list path uses) and
  reports per-profile freshness metadata: `source` (`fresh`|`cache`),
  `age_ms`, and `last_update`. `captured_at` remains the serialization
  timestamp and is documented as such. `mt_algos_group_by_name` gets the
  same cold-cache prime so lookups on quiet profiles no longer yield
  false not-found.

### Display-name completeness (issue #15)

* **`algos search` / `mt_algos_search`** now resolve the display name
  through the same `info parameter → description → name` priority the
  other algorithm list views use (synthetic auto-generated names
  filtered), and carry the raw on-wire name in a separate `CoreName`
  field — the last list-shaped surface that still emitted the raw
  synthetic name.

### Report field completeness (issue #17)

* **`mt_reports_trades` per-trade JSON records and the CSV exports**
  (`mt_reports_export`, `mt_reports_fleet_export`, `reports export`) now
  carry `AlgoInfo` (operator-set algorithm label), `OrderComment`,
  `OrderOpenByComment`, and a composite `AlgoSource` in the
  `{signature}: {info|openComment|name}` form (blank/`00` signatures
  normalized to `Manual`). The per-trade JSON records additionally gain
  `DistanceAtOrder` (previously only present in the opt-in `--metrics`
  context and the CSV). Regression tests pin the new CSV columns, row
  values, and the `AlgoSource` fallback chain.

### V2 import throughput (issue #14)

* **`mt_import_v2` no longer serializes one blocking acknowledgement
  round-trip per algorithm** (~20s each on cores that throttle
  notification replies — a 26-algo package took ~8 minutes). Algorithm
  creates are now queued fire-and-forget with light pacing, then a
  verification pass polls a fresh algorithm snapshot until every queued
  create is observed on the core (or a bounded budget elapses). The
  result reports `queued` per algo plus a `Verification: X/N queued
  algos observed on Core` summary, so success now reflects on-core
  creation rather than per-send acknowledgements. Group creation still
  uses the acknowledged path (the server-assigned group id is needed for
  remapping).

### Order cache correctness (issue #36)

* **Order-list batches now merge into the cache instead of replacing it.**
  MTCore delivers `UDS_ORDER_LIST_RESULT` as an incremental batch of order
  states (frequently one order per message), matching the official
  client's handling; the previous treat-as-full-snapshot logic cleared
  every cached order for the market type on each batch, which collapsed
  the active-order count to 0/1 between fuller batches — most visibly on
  venues that fragment their batches (Bybit/OKX).
* **Terminal orders are evicted from the active cache** (a long-lived
  session no longer accumulates every order it has ever seen). A bounded
  window of recent terminal snapshots is kept so
  `mt_account_orders`/`mt_orders_list` with `show_all` still return
  recent closed orders; fills also persist in the recent-executions ring
  and the reports store.
* **Order tools force a fresh read before reporting.** `mt_orders_list`,
  `mt_orders_cancel`/`_cancel_all`/`_place`/`_move`/`_update_tpsl`,
  `mt_account_orders`, `mt_account_summary`, `mt_fleet_balances`/
  `_summary`, and the core dashboard now refresh the order cache via a
  transient read first, and "no fresh order data received" is reported
  distinctly from "no active orders".
* **Order attribution:** each order/execution row now carries
  `OrderSource` (`ALGORITHM` / `MANUAL` / `TPSL` / `UNKNOWN`) and
  `DerivedAlgoSignature` — the algo signature recovered from the
  client-order-id when the wire's own signature field is blank.

### Leverage info

* **New `mt_exchange_leverage_info` tool** (CLI: `exchange leverage-info`,
  aliases `leverage` / `get-leverage`): surfaces configured/effective
  leverage per leverage type (Cross / IsolatedNet / IsolatedLong /
  IsolatedShort), max leverage, and risk-limit data for a symbol from
  MTCore's `LeverageInfoUpdateData` cache — no open position required.
  Each call primes the cache via a transient fresh read; open-position
  leverage is kept only as a fallback source.
* `mt_exchange_leverage_brackets` is now a compatibility alias backed by
  the same cache (bracket-tier tables are still not modelled as separate
  rows by the shared library). MCP tool catalog: 260 → 261.

### Autostops schema fix (issue #34)

* **`mt_autostops_*` / `autostops` now read and write the real
  `AutoStopAlgorithmData[]` array shape** under
  `AutoStopAlgorithm.Balance.Filters`, replacing the previous
  client-invented `{isEnabled, Values:[...]}` wrapper that crashed the
  official UI's balance-autostops panel on deserialize and meant filters
  written by this client never armed on the core. Field mapping:
  `max_loss` → `minMargin`, `symbols` → `symbolFilter`, `quotes`/`asset` →
  `asset`, `pause_algo` → `panicIfTriggered`, `timeframe_ms` → the
  timeframe enum (snapped to the nearest bucket), `enabled` → `isRunning`.
* `value_max` / `is_range` are removed from the `mt_autostops_add`/`_edit`
  schemas (the real type has no range concept); passing the old CLI flags
  now returns an explanatory error. New optional fields: `info`, `asset`,
  `algorithm_comment`, `report_comment`.
* The "master switch" concept is gone — `mt_autostops_start`/`_stop`
  without an index now enable/disable **all** filters.
* Settings blobs still holding the legacy wrapper are detected and
  reported as an explicit error on every mutating subcommand (no silent
  rewrite). Remediation for affected profiles: reset the
  `AutoStopAlgorithm.Balance.Filters` key to `[]` via `mt_settings_set`.
* Regression tests round-trip a real `AutoStopAlgorithmData` through the
  parser and pin the legacy-wrapper rejection.

### Algos lifecycle refresh

* **`mt_algos_start`/`mt_algos_stop` request wire-shape** now mirrors the
  vendor request shape: the request is built via `new AlgorithmData() +
  Deserialize(Serialize())` instead of the shared-library copy constructor,
  so collection fields land in the on-wire normal form. Necessary but not
  sufficient for the template-derived-NRE case; durable persistence-side
  fix queued separately.
* **SHOTS-group verification carve-out.** The start-time silent-init
  heuristic in `EvaluateVerification` now excludes signature=SG /
  groupType=SHOTS algorithms whose parent row legitimately stays
  symbol-less while child markets run beneath. Clears the
  silent-init-suspected false-flag on every Shots Group START.
* **Dual-name template support.** Build output ships
  `templates/algoConfigs.json` under both `algorithms.json` (canonical)
  and the legacy `algoConfigs.json` names. `mt_import_templates` and the
  algo-create fallback (`AlgosCommand.FindAlgoTemplate`) search both
  filenames under each of `<app-dir>` / `~/Documents/` / `<tmp>`.

### TPSL lifecycle

* **`ForceRefreshTPSL` sibling of `ForceRefreshAlgos` / `ForceRefreshAccount`.**
  Mirrors the vendor `GetTPSLInfoListData` shape: open a transient
  `SendAlgorithmTPSLsSubscribe` on every read, take the first list,
  unsubscribe in finally. Wired into `mt_tpsl_list`,
  `mt_tpsl_cancel_many`, `mt_tpsl_split_many`, `mt_tpsl_panic_many` —
  callers no longer need an explicit `mt_tpsl_subscribe` first.

### Core

* **`mt_core_advanced_restart`** — new composite restart tool. Combines
  `--update` / `--clear-orders` / `--clear-archive` in a single restart
  cycle (vendor `CommandAdvancedRestart` payload: one
  `CoreServiceControllerData` with a `CoreServiceCommand` HashSet).
  Saves the two-restart workaround when a wedge requires both feed
  recovery and archive clearing.

### Registry

* `mt_reports_trades` `market` filter doc now mentions MARGIN and the
  vendor `[SPOT, FUTURES, MARGIN]` empty-list default. Wire shape
  unchanged.

### Notes

* MCP tool catalog: 259 → 260.

### Added

* Algorithm-request path now waits on a `TaskCompletionSource` queue and
  signals on the notification subscription, matching the MTCore 0.7.23902
  push model. Previously `SendAlgorithmRequest` / `SendAlgorithmListRequest`
  expected the response on the inline send callback, which the newer MTCore
  no longer invokes — every algorithm-management tool call blocked until its
  timeout even though the response arrived on the wire within tens of
  milliseconds. The wrapper enqueues a TCS, fires the request, and blocks
  until the notification dispatcher signals it.
* `scripts/fetch_vendor_libs.py` — downloads `MTShared.dll` and
  `LiteNetLib.dll` for the host runtime identifier from the public MoonTrader
  CDN (`https://cdn3.moontrader.com/beta/<channel>/`), verifies the SHA-256
  against the vendor-published `version.txt` manifest, extracts both DLLs
  into `lib/<rid>/`, and applies the PE Machine-field flip (x64 → ARM64) on
  `osx-arm64`. Caches the tarball under `lib/.cache/` so subsequent builds
  are offline. Supported RIDs: `osx-arm64`, `osx-x64`, `linux-x64`,
  `linux-arm64`.
* MSBuild target `FetchVendorLibs` (in `MTTextClient.csproj`) auto-runs the
  fetch script before reference resolution when no vendor lib is present,
  or when `-p:FetchVendorLibs=true` is passed to refresh. The csproj
  resolves the references in the order `lib/MTShared.dll` →
  `lib/<rid>/MTShared.dll` (legacy baseline wins; RID copy is the
  fallback).
* `tests/MTTextClient.Tests/` — xUnit + FluentAssertions test project.
  PR-gate CI runs `Category=Static|Category=Unit` on every PR (Linux +
  macOS arm64, no MTCore subprocess). Smoke + LiveTrade run in
  `.github/workflows/testing-environment.yml` against a real bench MTCore
  (manual dispatch, gated by `MTC_TESTING_ENV=1`).
* `tests/MTTextClient.Tests/_expected/tools.minimum.json` — locked tool
  catalog baseline. Static tests fail if any baseline tool disappears or
  loses a required arg.
* `tests/MTTextClient.Tests/Static/ConfirmGateStaticTests.cs` — declarative
  audit of which tools must declare `confirm` in `inputSchema.required`.
  Catches regressions on the destructive-action gate at PR time.

### Changed

* Repo-wide sanitisation sweep over committed artefacts (code comments,
  generated registry description strings, tests + their filenames, bench
  scripts, CI configs, CHANGELOG, docs): removed local filesystem paths,
  role-term terminology, internal staging labels, internal feature-tracker
  IDs, and cross-references to private planning documents. README's
  autogenerated tool table was regenerated.
* `bench/{start,stop,status}_all_cores.sh` are now data-driven — read
  license ids, profile names, ports, and exchange labels from
  `$BENCH_ROOT/bench.conf` or per-slot environment variables, instead of
  embedding values in the script. See `bench/README.md`.
* `tests/` — `StageN<X>Tests.cs` files renamed to behaviour-described
  filenames (e.g. `AutoStopsCrudSmokeTests`, `AlgosClipboardImportSmokeTests`).
  Test classes renamed to match.
* `lib/` — vendor `MTShared.dll` / `LiteNetLib.dll` resolution is now
  per-RID with a download-at-build helper (see Added). The previous
  `PatchMTSharedArm64` post-build target is removed; the patch step is
  built into the fetch script and only runs at vendor-fetch time on
  `osx-arm64`.

### Removed

* Dated internal-process artefacts under `docs/` (validation-campaign
  notes, PR-readiness audits, test-foundation transition notes).

### Build

* `dotnet build -c Release` — 0 errors.
* `dotnet test --filter "Category=Static|Category=Unit"` — 2214 / 2214 pass.

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
  `mt-algo-XXXXXX` values filtered out so user-set labels surface
  consistently.
* V2 import remap (`Commands/ImportCommand.cs` and `MCP/McpServer.cs`) no
  longer skips entries whose `groupID` happens to be `0`. Algorithms
  belonging to a `GROUP_START 0` block now retain their group binding on
  the destination core.

### Fixed

* Silent ungrouped-import data corruption when V2 bundles contained a
  `GROUP_START 0` block.
* `algos list` output hiding user-set labels behind the synthetic
  `mt-algo-XXXXXX` name.
* MCP wrappers offering no way to access dust balances, archived orders,
  closed positions, custom execution tails, or the FUTURES/SPOT side of a
  24h ticker.

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
