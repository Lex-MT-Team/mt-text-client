# PR Readiness Audit — feat/stage1-tpsl-and-orders — 2026-05-12

Audits the entire `feat/stage1-tpsl-and-orders` branch (43 commits, 134 files
changed vs `main`) against the supervisor's seven leak-audit rules. Every
violation found has been fixed inside this audit pass; pre-existing flags
that the supervisor said to record-but-not-rewrite are listed verbatim.

- **Repo**: `<local checkout>`
- **Branch**: `feat/stage1-tpsl-and-orders` (post fast-forward of `validation worktree`)
- **Range audited**: `main..feat/stage1-tpsl-and-orders` (43 commits, head 415792f → after fixes commit)
- **PR-gate**: 2216 Static + Unit tests pass on the audited state.

## Rule 1 — Personal names / identifiers in production source

Scope checked: `Core/`, `MCP/`, `Commands/`, `Program.cs`, `scripts/`.

Greps: `Lex`, local user, local user, `bench_0[1-4]`.

| Violation | Location | Fix |
|---|---|---|
| Comment references hardcoded profile names `"local-binance-bench"` and `"local-profile"` as illustrative examples of the connection-name vs current-profile-name divergence | `Commands/SettingsCommand.cs:382-388` | Rewrote the comment to describe the divergence in generic terms ("connection alias" vs "bench-side current profile name") without naming specific bench/profile values |

Re-scan after fix: 0 hits in production source.

Test fixtures (`tests/MTTextClient.Tests/Infrastructure/EnvFlags.cs` etc.) DO
reference `local-bybit-bench..04` by name — that is the documented bench naming used
by `<artifact directory>/scripts/` and is exempt under the supervisor's rule ("test
fixtures can reference bench names"). No fix required.

## Rule 2 — Local filesystem paths in production source

Scope checked: `Core/`, `MCP/`, `Commands/`, `Program.cs`.

Greps: `/Users/`, `<artifact directory>/`, `<artifact directory>/`, `<artifact directory>/`, absolute Mac paths.

| Violation | Location | Verdict |
|---|---|---|
| (none) | — | — |

Production source contains tilde-prefixed paths only for standard
application-owned directories: `~/.config/mt-textclient/`, `~/.mt-snapshots/`,
`~/mt-clipboard/`, `~/mt-reports/`, `~/Documents/algoConfigs.json` (an
established Moontrader convention for cross-platform algo template
fallback). None of these are operator-machine-specific paths; all are
documented in the corresponding command's help text. **No fix required.**

## Rule 3 — Internal labels / debug markers

Scope checked: `Core/`, `MCP/`, `Commands/`, `Program.cs`, `tests/`.

Greps: `HACK`, `TEMP`, `DEBUG ONLY`, `FIXME`, `XXX`, TODO comments
referencing personal names.

| Violation | Location | Verdict |
|---|---|---|
| (none in production source) | — | — |
| `[Trait("TriggerProven", "false")]` on `Stage3AutoStopsLifecycleLiveTradeTests` | `tests/MTTextClient.Tests/LiveTrade/Stage3AutoStopsLifecycleLiveTradeTests.cs:51` | **Documented exception — kept.** Lines 45-51 explain the trait exists to distinguish CRUD-only validation from trigger-execution validation; previous supervisor handoff accepted this as an honest signal ("The Stage 3 artifact now honestly records `TriggerProven: false`"). Per rule 3's allowance for "intentional and documented" exceptions, the trait stays. |

All other `[Trait(...)]` attributes are standard `Category` traits
(`Static`/`Unit`/`Smoke`/`BenchAll`/`LiveTrade`) used by the test runner
filter; not internal debug markers.

## Rule 4 — Implementation hints in tool descriptions

Scope checked: `Core/ToolRegistry.cs` — every `Tool(name, description, …)`
description string.

Greps for terms that should NOT appear in caller-facing descriptions:
`UpdateProfileSettings`, `LiteNetLib`, `Firebird`, `MTShared`, `CoreConnection`,
`SendAndWait`, `AlgorithmStore`, `OrderRequestData`, `SendReportListRequest`,
`SendOrderTPSL`, `SwitchableNotificationDescriptor`, `WatchdogConnection`,
`AutoStopAlgorithm`.

Violations found and fixed (all in `Core/ToolRegistry.cs`):

| Tool | Term leaked in description | Fix |
|---|---|---|
| `mt_exchange_funding_rate` | "LiveMarketMetrics", "MTShared exposes no standalone SendFundingRateRequest RPC", "live-markets subscription" | Rewrote to describe outputs only (last funding rate/time, next funding rate/time, mark/last price) + warm-up timing |
| `mt_exchange_leverage_brackets` | "MTShared has NO LeverageBracket* type", "from AccountStore", "extend CoreConnection with a read wrapper for LeverageInfoUpdateData" | Rewrote to describe outputs (current effective leverage on open position) with a generic "not exposed by the current vendor library" caveat |
| `mt_algos_create` (top-level description) | "AlgorithmStore.UpdateParameter", "Arguments.<key>.value", internal three-layer-resilience implementation breakdown | Rewrote to describe clone-from-source behaviour and the `_mcp_metadata` block, without naming internal classes |
| `mt_orders_place` (iceberg prop description) | "OrderSettings.isIceberg=true", "MTShared's iceberg surface", "OrderRequestData" | Rewrote to describe the flag's user-facing behaviour and venue-specific caveat |
| `mt_autostops_start` | "(AutoStopAlgorithm.Balance.isEnabled)" | Rewrote as "master balance auto-stop switch" |
| `mt_reports_cancel` | "MTShared wire is currently synchronous (SendReportListRequest blocks for ~30s)" | Kept the 30s synchronous caveat but removed the internal wire-method name |
| `mt_notifications_config_groups` | "MTShared NotificationGroupType values" | Rewrote as "notification-group values" |
| `mt_notifications_config_targets` | "MTShared NotificationTarget values" | Rewrote as "notification-target values" |
| `mt_notifications_config_descriptors` | "MTShared SwitchableNotificationDescriptor" | Rewrote as "toggleable notification descriptor" |
| `mt_notifications_config_capabilities` | "editor is not yet wired through CoreConnection" | Rewrote as "when notification-config mutation is not yet available" |
| `mt_profile_settings_list` | "MTShared does not expose a list-named-profiles RPC", three `Send*Request` wire method names | Rewrote to describe outputs (keys of CURRENT profile) + a generic "underlying call" caveat |
| `mt_profile_settings_delete` | "via the existing SendUpdateProfileSettingsRequest wire method's 'deleted' parameter" | Rewrote to describe the user-facing edge cases only |
| `mt_orders_update_tpsl` | "(Stage 2.1 — wires SendOrderTPSLUpdateRequest)" | Removed the wire-method name; kept the user-facing description |

Hits remaining after fix: implementation terms (`UpdateProfileSettings`,
`SendOrderTPSLUpdateRequest`, `MTShared.NotificationSettingsEditor`,
`CoreConnection`, `SendAlertsRequest`, `AutoStopAlgorithm.Balance.Filters`)
still appear in `Core/ToolRegistry.cs` — but **only in code comments**
(lines starting with `//`), not in any description string passed to
`Tool(...)`. Those comments are internal developer notes and do not get
exposed in the MCP schema. **No further fix required** per rule 4's
"in tool descriptions" scope.

## Rule 5 — Placeholder tools with misleading descriptions

Audited tools: `mt_watchdog_status`, `mt_watchdog_token_update`.

| Violation | Location | Fix |
|---|---|---|
| Descriptions started with `[placeholder]` (lowercase, no emphasis) and then leaked implementation type names (`WatchdogCommandRequestData`, `WatchdogCommandType`, `WatchdogStatusInfo`, `WatchdogProfileCoreInfo`, `WatchdogConnection.cs`, `MTShared.WatchdogUDPClient`) | `Core/ToolRegistry.cs:1614-1640` | Both descriptions now prefixed `[PLACEHOLDER — NOT AVAILABLE]` and end in the supervisor-suggested wording "status: placeholder — not operational in the current deployment. Requires a watchdog-mode MTCore instance and a client-side watchdog connection that is not yet implemented in this build. Calling this tool returns an 'unknown_tool' error. See docs/watchdog-integration.md for the planned design." No internal type names remain. |

The lowercase `status: placeholder` substring is preserved as a grep marker
for future engineers — `Static/WatchdogPlaceholderStaticTests.cs:57` pins
that contract. Both static tests still pass.

## Rule 6 — Staging / test data committed

Scope checked: `Core/`, `MCP/`, `Commands/`, `Program.cs`, `tests/`,
`scripts/`, `docs/`.

Greps: API key/secret/token assignments, base64/hex strings ≥30 chars,
integer literals ≥15 digits (real venue order/trade IDs), Stage 1 / Stage 2
client_order_id prefixes.

| Type | Result |
|---|---|
| API keys / secrets / tokens in source | **No real values.** The only hits are `apiKey`/`apiSec` variable references in `tests/.../VaultTests.cs` (read from env) and the literal strings `"DUMMY_KEY"` / `"DUMMY_SECRET"` in `Campaign_A_LocalConfigTests.cs` (obviously-dummy values for the vault round-trip test). |
| Base64 / hex ≥30 chars in production source | **None found.** |
| Real venue order/trade IDs in campaign tests | **None found.** The campaign tests construct synthetic `client_order_id` values at runtime (`campaignE1<unix_ts>`, etc.); no captured-from-live-run venue order id is hardcoded. |
| Real IDs in committed evidence doc | `docs/LiveValidationCampaign_2026-05-12.md` contains synthetic `campaignE1<ts>` client_order_ids and public market prices (e.g. `75954.2`) inside response-excerpt cells. These are the campaign's own synthetic identifiers + public BTCUSDT price data, not venue-side order IDs or trade IDs. **Acceptable.** |
| Real IDs in committed diff CSV | `docs/artifacts/CAMPAIGN_DIFF_2026-05-12.csv` contains no captured numerics — only tool names, classification labels, and campaign letters. **Clean.** |
| Test trade IDs in JSONL artifacts | **Not committed** — `<artifact directory>/campaign-2026-05-12/*.jsonl` lives outside the repo. |

## Rule 7 — Commit history (flag only, do not rewrite)

`git log --oneline main..feat/stage1-tpsl-and-orders` — 43 commits total.

| Commit | Issue | Action |
|---|---|---|
| `9cb8afb port: PR1 known-defects batch onto Stage 0.7 stack` | Subject uses bare `port:` instead of a conventional `chore(port):` / `refactor(port):` prefix. Cosmetic only — the message itself is descriptive and contains no personal context. | **Flagged. No rewrite** (supervisor instruction: do not rewrite history). Recommend addressing in a future history-cleanup pass if the team adopts strict conventional-commits enforcement. |

All other 42 commits use the conventional `type(scope): subject` form.

Scans for personal context in commit messages and bodies:

| Greps | Result |
|---|---|
| local user identifiers (case-insensitive) in subject or body of any commit | **0 hits.** |
| `claude`, `agent`, `wip`, `todo`, `fixme` (case-insensitive) in subject or body | **0 hits.** |
| `supervisor` in body | 1 hit in the latest commit's body (`415792f`) — used as a role term ("supervisor re-audit"), not a personal name. Acceptable per the project's documented terminology (CLAUDE.md references "Supervisor One" as a project role). |

## After-audit verification

| Check | Result |
|---|---|
| `dotnet build MTTextClient.sln -c Release` | 0 errors (BouncyCastle vulnerability warnings unchanged from baseline) |
| `RegistryReadmeGenerator` regenerated `README.md` | Wrote 258 tools |
| `DispatcherSnapshotGenerator` regenerated `tests/.../commandlines.snapshot.json` | Wrote 258 tools |
| `dotnet test --filter "Category=Static\|Category=Unit"` | **2216 passed, 0 failed** |
| Diff in this audit pass | 3 files: `Commands/SettingsCommand.cs`, `Core/ToolRegistry.cs`, `README.md` (regenerated), `tests/.../commandlines.snapshot.json` (regenerated) |

Branch is **clean and push-ready**.
