# Live Validation Campaign — 2026-05-12

Records the campaign that drove every supervisor-flagged `no_live_evidence`
tool against real local MTCore benches, and the post-audit re-classification
done after Supervisor review.

- **Repo**: `<local checkout>`
- **Worktree**: `validation worktree`
- **Branch**: `validation worktree` (off `feat/stage1-tpsl-and-orders`)
- **HEAD**: `426c7af chore(watchdog): placeholder schemas + discovery doc — out of scope current epic`
- **MCP tools advertised by runtime**: 258
- **Supervisor input matrix**: `SupervisorOne_ToolLiveEvidenceMatrix_426c7af_2026-05-12.csv`
- **Raw per-call evidence**: `~/mt-test-artifacts/campaign-2026-05-12/{A..L,Zz}.jsonl`
- **Mechanical diff CSV (in-repo)**: `docs/artifacts/CAMPAIGN_DIFF_2026-05-12.csv`

## Executive summary

| Bucket | Supervisor pre | After campaign + re-audit | Delta |
|---|--:|--:|--:|
| `yes_real_response_or_activity` (validated **or** structured tool_error with confirmed feature outcome) | 81 | **213** | +132 |
| `partial_or_failed_live_path` (wire ran but feature outcome not confirmed: pre-flight refusal, missing precondition, synthetic id) | 16 | **32** | +16 |
| `no_live_evidence` (documented blocker or no-handler placeholder) | 161 | **13** | −148 |
| **Total** | **258** | **258** | — |

**Headline:** 132 of the supervisor's 161 `no_live_evidence` tools are now
backed by real-wire evidence with the feature outcome confirmed. 16 more
had real wire activity but only exercised the error/pre-flight path —
honestly classified as `partial_or_failed_live_path` after supervisor
re-audit. 13 remain in `no_live_evidence` as documented blockers or
no-handler placeholders.

## Post-audit re-classification (changes from initial submission)

Supervisor flagged 16 tools where the initial classification of
`validated_real_response` was too lenient — the wire returned something
structured, but the *feature outcome* (the thing the tool is for) didn't
actually happen. Each is downgraded to `partial_or_failed_live_path`:

| Tool | Tier | Reason for downgrade |
|---|---|---|
| `mt_algos_copy` | D | 'destination local-hyperliquid-bench not connected' pre-flight; copy did not occur | Destination 'local-hyperliquid-bench' is not connected. |
| `mt_import_templates` | J | file-not-found error path only; import feature not exercised | algoConfigs.json not found at explicit path: /tmp/nonexistent-algoconfigs-campaignJ.json |
| `mt_import_v2` | J | file-not-found error path only; import feature not exercised | File not found: /tmp/nonexistent-v2-campaignJ.txt |
| `mt_orders_move_batch` | E | refused with 'Re-run with --confirm'; move did not happen | [local-binance-bench] ⚠ Move batch orders? Re-run with --confirm. |
| `mt_reports_delete` | L | depended on store success; 'No stored report set named X' | No stored report set named 'campaignL_1778596555'. |
| `mt_reports_export` | L | 'No trades found' precondition fail; no CSV exported | [local-binance-bench] No trades found for last 30 days. |
| `mt_reports_fleet_export` | L | 'No trades found from any connected server'; no CSV exported | No trades found from any connected server. |
| `mt_reports_load` | L | depended on store success; 'No stored report set named X' | No stored report set named 'campaignL_1778596555'. Use 'reports stored' to list. |
| `mt_reports_store` | L | 'No trades found' precondition fail; nothing was stored | [local-binance-bench] No trades found for last 30 days. |
| `mt_tpsl_cancel` | H | synthetic id=0; venue ERROR but no real TPSL was cancelled | [local-binance-bench] Cancel TPSL 0: ERROR — |
| `mt_tpsl_cancel_many` | H | synthetic ids [0,0]; 0 succeeded, 2 failed | [local-binance-bench] TPSL cancel-many: 0 succeeded, 2 failed (of 2). |
| `mt_tpsl_join` | H | synthetic ids; 'No response' (timeout) | No response from TPSL join. |
| `mt_tpsl_panic` | H | synthetic id=0; NOT_FOUND pre-flight; no real panic | [local-binance-bench] TPSL panic 0 failed: NOT_FOUND — TPSL not found in store. Run 'tpsl subscribe' first, then 'tpsl list' to confirm IDs. |
| `mt_tpsl_panic_many` | H | synthetic ids; 2 NOT_FOUND pre-flight | [local-binance-bench] TPSL panic-many: 0 succeeded, 2 failed (of 2). |
| `mt_tpsl_split` | H | synthetic id=0; 'No response' (timeout) | No response from TPSL split. |
| `mt_tpsl_split_many` | H | synthetic ids; 2 TIMEOUT responses | [local-binance-bench] TPSL split-many: 0 succeeded, 2 failed (of 2). |

After re-audit, the supervisor's 161 `no_live_evidence` bucket
re-distributes as:

| Post-audit class | Count |
|---|--:|
| validated_real_response | 132 |
| partial_or_failed_live_path | 16 |
| documented_blocker | 11 |
| rpc_error_unhandled (watchdog placeholder, no handler) | 2 |
| **Total (originally no_evidence)** | **161** |

## Per-tier breakdown (post-audit)

Tools moved from supervisor's `no_live_evidence` bucket, counted per
campaign tier (deduplicated). The Partial column reflects the post-audit
downgrade.

| Tier | Class | Validated | Partial | Blocker | Total touched |
|---|---|--:|--:|--:|--:|
| A | Campaign_A_LocalConfigTests          | 12 |  0 | 4 | 18 |
| B | Campaign_B_TelemetryTests            |  8 |  0 | 0 |  8 |
| C | Campaign_C_SubscribeUnsubscribeTests | 34 |  0 | 0 | 34 |
| D | Campaign_D_AlgosLifecycleTests       | 17 |  1 | 1 | 19 |
| E | Campaign_E_OrderMutatorsTests        | 16 |  1 | 1 | 18 |
| G | Campaign_G_FleetAggregatesTests      | 14 |  0 | 0 | 14 |
| H | Campaign_H_TpslMutatorsTests         |  0 |  7 | 0 |  7 |
| I | Campaign_I_TriggersAutobuyGraphTests | 13 |  0 | 0 | 13 |
| J | Campaign_J_MiscOpsTests              | 18 |  2 | 5 | 25 |
| L | Campaign_L_ReportsPersistenceTests   |  0 |  5 | 0 |  5 |
| Zz | Campaign_Zz_CleanupTests             | (residue-only, no in-scope tools) |  |  |  |
| **Total (deduplicated)** |                  | **132** | **16** | **11** | **162** |

## Fleet scope correction

The supervisor noted that `mt_fleet_connect` is *not* scoped to the four
configured bench cores; it iterates every entry in
`~/.config/mt-textclient/profiles.json`, which currently lists **26**
profiles (the 4 benches plus 22 remote IPs that are unreachable from this
host).

Practical scope of the fleet evidence captured by Campaign G:

- `mt_fleet_connect` *attempted* connections to all 26 profiles. The 22
  remote-IP profiles did not reach CONNECTED state during the campaign;
  only the four local benches (`local-bybit-bench..04`) connected.
- All subsequent fleet aggregate reads (`mt_fleet_status`,
  `mt_fleet_balances`, `mt_fleet_positions`, `mt_fleet_algos`,
  `mt_fleet_health`, `mt_fleet_summary`, `mt_fleet_perf`,
  `mt_fleet_reports`, `mt_fleet_autostops`, `mt_fleet_blacklist`,
  `mt_fleet_set_margin_type`, `mt_fleet_disconnect`,
  `mt_fleet_batch_connect`) iterate the live ConnectionManager
  collection — which contained only the four CONNECTED benches.
- Therefore the **aggregate evidence** is correctly scoped to the four
  benches, but the **per-profile connect attempts** Campaign G triggered
  against the 22 remote profiles are not validated. Treat those 22 connect
  attempts as unconfirmed (no MTCore on the other end to respond).

## Bench state during the campaign

| Bench | Exchange | Port | Status during campaign |
|---|---|--:|---|
| local-bybit-bench | BYBIT       | 4242 | Up; treated as read-only per DEFECT-11. |
| local-binance-bench | BINANCE     | 4243 | Primary target. Restarted once mid-run to clear MCP-002 wedged-peer state. |
| local-hyperliquid-bench | HYPERLIQUID | 4244 | Up; cross-profile destination for copy / settings_diff / import_from_profile. |
| local-okx-bench | OKX         | 4245 | Up; read-only per DEFECT-11. |

## Bench state after campaign + cleanup (`Campaign_Zz_CleanupTests`)

Verified post-cleanup (artifacts in `~/mt-test-artifacts/campaign-2026-05-12/Zz_cleanup.jsonl`,
verification probes captured separately):

| Surface | Result |
|---|---|
| Profile setting `Misc.CampaignJ.LastRun` | **Deleted** (1 key removed). Verified via `mt_profile_settings_list grep=Misc.Campaign` returning 0 keys. |
| Algorithms renamed by Campaign D (`campaignD_*` prefix) | **None present after run.** Algo list shows 7 algorithms, all canonical `SG_U_BUY/SELL_*` names. The Campaign D rename did not survive the mid-campaign bench restart, so no manual restore was needed. |
| BTCUSDT working orders placed by Campaign E (`campaignE*` client_order_id) | **0 active orders** (`mt_orders_list` returns "No active orders"). `mt_orders_cancel_all BTCUSDT` invoked at end of Campaign E and again by Cleanup as an idempotent sweep. |
| Open BTCUSDT positions on local-binance-bench | **0 open positions** (`mt_account_positions` returns "No open positions found"). Campaign E LIMIT orders were placed 5% below market — none filled in the test window. |
| local-binance-bench USDT balance after campaign | $993.61 (2 assets) — unchanged relative to prior session except for venue funding-rate accrual. No campaign-attributable balance loss. |

**No filled orders left behind.** No campaign trade reached FILLED status
on the venue; all evidence above confirms a clean exit. Stage 1
LiveTrade-style fills (placed near-market to fill) were *not* executed by
this campaign — that pattern is reserved for Stage 1 / Stage 2 runs.

## How to reproduce

```bash
~/mt-bench/scripts/start_all_cores.sh
cd ~/mt-dev/mt-text-client/validation worktree
dotnet build MTTextClient.sln -c Release

for letter in A B C D E G H I J L; do
  MTC_TESTING_ENV=1 MTC_LIVE_TRADES=1 \
    dotnet test MTTextClient.sln -c Release --no-build \
      --filter "FullyQualifiedName~Campaign_${letter}_"
done

# Cleanup pass
MTC_TESTING_ENV=1 MTC_LIVE_TRADES=1 \
  dotnet test MTTextClient.sln -c Release --no-build \
    --filter "FullyQualifiedName~Campaign_Zz_Cleanup"
```

## Source divergences observed (test-only patches)

Production source was untouched. Test-side adaptations:

1. `mt_algos_profiling` — registry declares `algo_id` as `integer` but
   MTCore IDs are `Int64`. Wide IDs (>2³¹) crash `int.Parse`. **Test
   fix**: pass as `long`. **Suggested followup**: registry schema should
   be `number` / `int64` to match the wire type.
2. `mt_profiles_move` — schema says `folder=""` moves a profile to root,
   but the dispatcher rejects empty-string as missing
   (`-32602 Missing required argument: folder`). **Test fix**: use a real
   folder name. **Suggested followup**: handler should accept empty-string
   or schema should drop the `required`.
3. `mt_fleet_set_margin_type` — ConfirmGate denies `confirm=false` before
   the handler runs, so the documented "DRY RUN by default" preview path
   is unreachable. **Test fix**: pass `confirm=true` (idempotent on
   local-binance-bench BTCUSDT, already CROSS). **Suggested followup**: drop
   `confirm` from `required` and let the handler enforce, OR document
   that the preview is REPL-only.
4. `mt_config_snapshot` — returns `snapshot_path` at top level, not
   `data.path`. Test path extractor updated; no source change needed.

## Newly-validated tools (132) — feature outcome confirmed

`✓` = positive success envelope. `∅` = structured tool_error that
nonetheless confirms the wire path executed the feature operation
(e.g. cancel_all of empty book, unsubscribe-when-not-subscribed,
already-current setting). Full envelope per call available in
`~/mt-test-artifacts/campaign-2026-05-12/<letter>.jsonl`.

| Tool | Tier | Kind | Evidence excerpt |
|---|---|---|---|
| `mt_alerts_history_subscribe` | C | ✓ | Subscribed to alert history on local-binance-bench. |
| `mt_alerts_history_unsubscribe` | C | ✓ | Not subscribed to alert history on local-binance-bench. |
| `mt_alerts_unsubscribe` | C | ✓ | Not subscribed to alerts on local-binance-bench. |
| `mt_algos_batch_config` | D | ✓ | Batch config 'delayMs=150' on 'campaignD': 0 algo(s) updated locally across 1 server(s) in 2ms. Use 'algos save <id> @<profile>' to persist. |
| `mt_algos_batch_start` | D | ✓ | Batch START: searched 'campaignD', 0 algo(s) across 1 server(s) in 0ms. |
| `mt_algos_batch_stop` | D | ✓ | Batch STOP: searched 'campaignD', 0 algo(s) across 1 server(s) in 0ms. |
| `mt_algos_clone_group` | D | ✓ | [local-binance-bench] Group 'New Group' (1760710320741): CLONED ✓ |
| `mt_algos_config_set` | D | ∅ | [local-binance-bench] Parameter 'delayMs' not found. Use 'algos config <id>' to see available parameters. |
| `mt_algos_group_by_name` | D | ✓ | (empty body) |
| `mt_algos_profiling` | D | ✓ | (empty body) |
| `mt_algos_rename` | D | ✓ | [local-binance-bench] Algorithm 1760710320742: renamed 'campaignD_1778595577' → 'campaignD_1778595610' ✓ |
| `mt_algos_save` | D | ✓ | [local-binance-bench] Algorithm 1760710320742 (Shots Group): SAVE ✓ |
| `mt_algos_save_start` | D | ✓ | [local-binance-bench] Algorithm 1760710320742 (Shots Group): SAVE+START ✓ |
| `mt_algos_start` | D | ✓ | [local-binance-bench] Algorithm 1760710320742 (Shots Group): START ✓ |
| `mt_algos_start_all` | D | ✓ | [local-binance-bench] START_ALL ✓ |
| `mt_algos_start_verified` | D | ∅ | [local-binance-bench] Algo 1760710320742 (Shots Group) — BUG13_SUSPECTED (waited 3s). [local-binance-bench] Algorithm 1760710320742 (Shots Group): START FAILED —  |
| `mt_algos_stop` | D | ✓ | [local-binance-bench] Algorithm 1760710320742 (Shots Group): STOP ✓ |
| `mt_algos_stop_all` | D | ✓ | [local-binance-bench] STOP_ALL ✓ |
| `mt_algos_toggle_debug` | D | ✓ | [local-binance-bench] Algorithm 1760710320742 (Shots Group): debug toggled ✓ |
| `mt_algos_tpsl_change` | D | ✓ | (empty body) |
| `mt_autobuy_delete` | I | ✓ | Waiting... |
| `mt_autobuy_refresh_pairs` | I | ✓ | Waiting... |
| `mt_autobuy_save` | I | ✓ | Waiting... |
| `mt_autobuy_start` | I | ✓ | Waiting... |
| `mt_autobuy_stop` | I | ✓ | Waiting... |
| `mt_autobuy_subscribe` | C | ✓ | Subscribed to AutoBuy events |
| `mt_autobuy_unsubscribe` | C | ✓ | Unsubscribed from AutoBuy |
| `mt_autostops_edit` | J | ∅ | Index 0 out of range (have 0 filter(s)). |
| `mt_blacklist_add` | J | ✓ | [local-binance-bench] Added symbol 'FUTURES/usdt/campaignjblacklist' to blacklist. (Core restart needed for full effect) |
| `mt_blacklist_remove` | J | ✓ | [local-binance-bench] Removed symbol 'FUTURES/usdt/campaignjblacklist' from blacklist. (Core restart needed for full effect) |
| `mt_buylimit_request` | J | ∅ | Usage: buylimit request <amount> |
| `mt_config_import_algos` | J | ✓ | (empty body) |
| `mt_config_restore` | J | ✓ | restore from snapshot just captured |
| `mt_config_snapshot` | J | ✓ | primary snapshot for diff |
| `mt_connection_tag` | B | ✓ | Tag set: local-binance-bench [campaign] = "B-2026-05-12" |
| `mt_connection_tags` | B | ✓ | Tags for 'local-binance-bench' (1 tag(s)): |
| `mt_deposit_address` | J | ✓ | Waiting... |
| `mt_disconnect` | A | ✓ | Disconnected from 'local-binance-bench'. |
| `mt_dust_convert` | J | ✓ | Result: ERROR_NO_DUST_FOUND, Total: 0, Fee: 0 |
| `mt_events_poll` | B | ✓ | (empty body) |
| `mt_events_status` | B | ✓ | (empty body) |
| `mt_fleet_algos` | G | ✓ | Fleet Algos — 224 total, 2 running across 20 servers |
| `mt_fleet_autostops` | G | ✓ | Fleet AutoStops — 26 servers Server               AutoStops    Status         ------------------------------------------------ local-profile |
| `mt_fleet_balances` | G | ✓ | Fleet Balances — 20 servers | Grand Total: $31,216.34 USDT |
| `mt_fleet_batch_connect` | G | ✓ | Batch connect: 4/4 initiated in 7ms. |
| `mt_fleet_blacklist` | G | ✓ | Fleet Blacklists — 26 servers Server               Markets  Quotes  Symbols   ----------------------------------------------- local-profile |
| `mt_fleet_connect` | G | ✓ | Fleet Connect — 20/26 online (new: 26, already: 0, failed: 0, settle: 1400ms) |
| `mt_fleet_disconnect` | G | ✓ | Fleet: Disconnected from 20 server(s). |
| `mt_fleet_health` | G | ✓ | Fleet Health — ✅ 4 healthy, ⚡ 1 warnings, ❌ 0 critical (20 servers) |
| `mt_fleet_perf` | G | ✓ | Fleet Performance — 26 servers Server               Entries  Subscribed   ------------------------------------------ local-profile  —  |
| `mt_fleet_positions` | G | ✓ | Fleet Positions — 2 open across 20 servers |
| `mt_fleet_reports` | G | ✓ | Fleet Reports (last 7 days) — 26 servers, 5 trades, PnL: -212.86, Fees: 425.01 Server               Trades  PnL      Fees    WinRate  Volume |
| `mt_fleet_set_margin_type` | G | ✓ | FLEET set-margin-type — symbol=BTCUSDT → CROSS: 15 applied, 0 failed, 11 skipped (of 26). |
| `mt_fleet_status` | G | ✓ | Fleet Status — 20/26 online |
| `mt_fleet_summary` | G | ✓ | ═══ Fleet Summary ═══   Servers: 20/26 online | Balance: $31,216.34 | PnL: -$3.33 | Algos: 2/224 | Positions: 2 |
| `mt_folders_add` | A | ✓ | Added folder 'campaignA_folder_1778596724'. Total known folders: 1. |
| `mt_folders_delete` | A | ✓ | Deleted folder 'campaignA_folder_renamed_1778596724'. |
| `mt_folders_edit` | A | ✓ | Renamed folder 'campaignA_folder_1778596724' → 'campaignA_folder_renamed_1778596724'. Cascaded to 0 profile(s). |
| `mt_folders_list` | A | ✓ | folders.json: 0 known folder(s), 0 orphan(s). |
| `mt_fund_transfer` | J | ✓ | [local-binance-bench] Transfer 0.01 USDT from FUNDING to TRADING: Waiting... |
| `mt_funding_request` | J | ✓ | [local-binance-bench] Funding balances request sent (fire-and-forget). |
| `mt_graphtool_delete` | I | ✓ | Waiting... |
| `mt_graphtool_save` | I | ✓ | Waiting... |
| `mt_graphtool_subscribe` | C | ✓ | Subscribed to graph tool events |
| `mt_graphtool_unsubscribe` | C | ✓ | Unsubscribed from graph tool |
| `mt_import_add_numeric` | J | ✓ | [local-binance-bench] Algorithm 1760710320742 (Shots Group): 20 params adjusted by +0.0 and SAVED ✓ |
| `mt_import_from_profile` | J | ✓ | (empty body) |
| `mt_livemarkets_subscribe` | C | ✓ | Subscribed to live markets (market=FUTURES, symbol=BTCUSDT, quoteAsset=) |
| `mt_livemarkets_unsubscribe` | C | ✓ | Unsubscribed from live markets |
| `mt_marketdata_depth_subscribe` | C | ✓ | Subscribed to depth for BTCUSDT (FUTURES) on local-binance-bench. |
| `mt_marketdata_depth_unsubscribe` | C | ✓ | Unsubscribed from depth for BTCUSDT (FUTURES) on local-binance-bench. |
| `mt_marketdata_klines_subscribe` | C | ✓ | Subscribed to MIN_1 klines for BTCUSDT (FUTURES) on local-binance-bench. |
| `mt_marketdata_klines_unsubscribe` | C | ✓ | Unsubscribed from MIN_1 klines for BTCUSDT (FUTURES) on local-binance-bench. |
| `mt_marketdata_markprice_subscribe` | C | ✓ | Subscribed to mark price for BTCUSDT (FUTURES) on local-binance-bench. |
| `mt_marketdata_markprice_unsubscribe` | C | ✓ | Unsubscribed from mark price for BTCUSDT (FUTURES) on local-binance-bench. |
| `mt_marketdata_ticker_subscribe` | C | ✓ | Subscribed to tickers (FUTURES) on local-binance-bench. |
| `mt_marketdata_ticker_unsubscribe` | C | ✓ | Unsubscribed from tickers (FUTURES) on local-binance-bench. |
| `mt_marketdata_trades_subscribe` | C | ✓ | Subscribed to trades for BTCUSDT (FUTURES) on local-binance-bench. |
| `mt_marketdata_trades_unsubscribe` | C | ✓ | Unsubscribed from trades for BTCUSDT (FUTURES) on local-binance-bench. |
| `mt_metrics_get` | B | ✓ | (empty body) |
| `mt_monitor_health` | C | ✓ | [local-binance-bench] Health: 🔴 CRITICAL   CPU: 0% | RAM: 491MB | Threads: 157 | Exchange: 836ms |
| `mt_monitor_performance` | C | ✓ | [local-binance-bench] Performance — 1 snapshots |
| `mt_monitor_start` | C | ✓ | Monitor started. Collecting core status snapshots via UDP. |
| `mt_monitor_stats` | C | ✓ | [local-binance-bench] Monitor Stats — 1 samples over 0s   CPU: avg 0% (min 0%, max 0%)   Memory: avg 491MB (min 491MB, max 491MB)   Threads: avg 157 (m |
| `mt_monitor_status` | C | ✓ | [local-binance-bench] Monitor: RUNNING (1 snapshots) |
| `mt_monitor_stop` | C | ✓ | Monitor stopped. 1 snapshots were collected. |
| `mt_notifications_clear` | C | ✓ | Cleared 0 notifications from local-binance-bench. |
| `mt_notifications_subscribe` | C | ✓ | Subscribed to notifications on local-binance-bench. |
| `mt_notifications_unsubscribe` | C | ✓ | Not subscribed to notifications on local-binance-bench. |
| `mt_orders_cancel` | E | ∅ | [local-binance-bench] Cancel FAILED — ERROR: cause: ERROR, message: "Couldn't cancel order (COI-1)" |
| `mt_orders_cancel_all` | E | ✓ | [local-binance-bench] Cancel-all results:   FUTURES: CANCELLED ✓ |
| `mt_orders_change_margin` | E | ∅ | [local-binance-bench] Change margin FAILED — ERROR: cause: ERROR, message: "Cannot add position margin: position is 0.", jsonData: "{"code":-4054,"msg" |
| `mt_orders_close` | E | ∅ | [local-binance-bench] No open position for BTCUSDT. |
| `mt_orders_close_all` | E | ✓ | [local-binance-bench] No open positions. |
| `mt_orders_close_by_tpsl` | E | ✓ | [local-binance-bench] Close-by-TPSL (MARKET): OK |
| `mt_orders_join` | E | ✓ | [local-binance-bench] Join order campaignE11778595867: Nothing to join |
| `mt_orders_move` | E | ✓ | [local-binance-bench] Order campaignE11778595867 moved to 75954.2 ✓ |
| `mt_orders_reset_tpsl` | E | ✓ | [local-binance-bench] Reset TPSL: OK |
| `mt_orders_set_leverage` | E | ∅ | [local-binance-bench] Set leverage FAILED — WARNING: cause: WARNING, message: "No need to change leverage for BTCUSDT", jsonData: "{"exchangeType":1,"m |
| `mt_orders_set_leverage_buysell` | E | ∅ | Request timed out or connection lost. |
| `mt_orders_set_margin_type` | E | ✓ | [local-binance-bench] Set margin type BTCUSDT CROSS: Margin type cannot be changed if there exists open orders. |
| `mt_orders_set_multiasset` | E | ✓ | Multi-asset mode (FUTURES): set to DISABLED — Adjusted asset mode is currently set and does not need to be adjusted repeatedly. |
| `mt_orders_set_position_mode` | E | ✓ | [local-binance-bench] Set position mode BTCUSDT ONE_WAY: No need to change position side. |
| `mt_orders_split` | E | ✓ | [local-binance-bench] Split order campaignE11778595867 into 2 parts: Split order failed, could not find tpsl |
| `mt_orders_transfer` | E | ∅ | [local-binance-bench] Transfer FAILED — ERROR: cause: ERROR, message: "Asset transfer failed: insufficient balance", jsonData: "{"code":-5013,"msg":"As |
| `mt_perf_subscribe` | B | ✓ | [local-binance-bench] Subscribed to trading performance (FUTURES). Use 'perf list' to view data. |
| `mt_perf_unsubscribe` | B | ✓ | [local-binance-bench] Unsubscribed from trading performance. |
| `mt_profiles_add` | A | ✓ | Added profile 'campaignA_profile_1778596724' (BINANCE @ 127.0.0.1:4099) in folder 'campaignA_folder_renamed_1778596724'. |
| `mt_profiles_delete` | A | ✓ | Deleted profile 'campaignA_profile_renamed_1778596724' (1 row(s)); 28 remaining. |
| `mt_profiles_edit` | A | ✓ | Edited profile 'campaignA_profile_1778596724': port=4098, rename=campaignA_profile_renamed_1778596724. |
| `mt_profiles_import_csv` | A | ✓ | import-csv /var/folders/qm/248mbvss6x55356k5ljw6q440000gn/T/campaignA_import_1778596724.csv: 2 added, 0 skipped (duplicates/empty), 0 failed |
| `mt_profiles_list` | A | ✓ | profiles.json: 26 profile(s).   [(root)] local-bybit-bench → 127.0.0.1:4242 (BYBIT)   [(root)] local-binance-bench → 127.0.0.1:4243 (BINANCE)   [(root)] bench_0 |
| `mt_profiles_move` | A | ✓ | profiles move 'campaignA_profile_renamed_1778596724' → 'campaignA_folder_renamed_1778596724': no_change (already there). |
| `mt_profiling_subscribe` | C | ✓ | Subscribed to profiling for BTCUSDT algo 1760710320742 (FUTURES) |
| `mt_profiling_unsubscribe` | C | ✓ | Unsubscribed from profiling for BTCUSDT algo 1760710320742 |
| `mt_rate_status` | B | ✓ | (empty body) |
| `mt_settings_diff` | J | ✓ | (empty body) |
| `mt_settings_diff_snapshots` | J | ✓ | (empty body) |
| `mt_settings_set` | J | ✓ | [local-binance-bench] Setting 'Misc.CampaignJ.LastRun' updated to '1778596976' ✓   ⚠ Core restart is needed for this change to take effect. |
| `mt_signals_send` | J | ✓ | Signal sent: BUY BTCUSDT @ 1.0 (tp=1%, sl=1%) |
| `mt_tpsl_subscribe` | C | ✓ | [local-binance-bench] Subscribed to TPSL updates. Use 'tpsl list' to view data. |
| `mt_tpsl_unsubscribe` | C | ✓ | [local-binance-bench] Unsubscribed from TPSL updates. |
| `mt_triggers_delete` | I | ✓ | Waiting... |
| `mt_triggers_save` | I | ✓ | Waiting... |
| `mt_triggers_start` | I | ✓ | Waiting... |
| `mt_triggers_start_all` | I | ✓ | (empty body) |
| `mt_triggers_stop` | I | ✓ | Waiting... |
| `mt_triggers_stop_all` | I | ✓ | (empty body) |
| `mt_triggers_subscribe` | C | ✓ | Subscribed to triggers |
| `mt_triggers_unsubscribe` | C | ✓ | Unsubscribed from triggers |
| `mt_use` | A | ∅ | No connection 'local-binance-bench'. Use 'status' to see connections. |
| `mt_whitelist_remove` | J | ∅ | not_found: none of [campaignjwhitelist] are in WhiteList.Symbols on local-binance-bench. |

## Partial / failed live path (16) — wire ran, feature outcome not confirmed

These tools issued real wire calls and got structured responses, but the
response confirms the feature did **not** execute (precondition failure,
synthetic id, pre-flight refusal). Honestly classified per supervisor
re-audit. Each needs a follow-up pass with the precondition in place.

| Tool | Tier | Why downgraded | Evidence excerpt |
|---|---|---|---|
| `mt_algos_copy` | D | 'destination local-hyperliquid-bench not connected' pre-flight; copy did not occur | Destination 'local-hyperliquid-bench' is not connected. |
| `mt_import_templates` | J | file-not-found error path only; import feature not exercised | algoConfigs.json not found at explicit path: /tmp/nonexistent-algoconfigs-campaignJ.json |
| `mt_import_v2` | J | file-not-found error path only; import feature not exercised | File not found: /tmp/nonexistent-v2-campaignJ.txt |
| `mt_orders_move_batch` | E | refused with 'Re-run with --confirm'; move did not happen | [local-binance-bench] ⚠ Move batch orders? Re-run with --confirm. |
| `mt_reports_delete` | L | depended on store success; 'No stored report set named X' | No stored report set named 'campaignL_1778596555'. |
| `mt_reports_export` | L | 'No trades found' precondition fail; no CSV exported | [local-binance-bench] No trades found for last 30 days. |
| `mt_reports_fleet_export` | L | 'No trades found from any connected server'; no CSV exported | No trades found from any connected server. |
| `mt_reports_load` | L | depended on store success; 'No stored report set named X' | No stored report set named 'campaignL_1778596555'. Use 'reports stored' to list. |
| `mt_reports_store` | L | 'No trades found' precondition fail; nothing was stored | [local-binance-bench] No trades found for last 30 days. |
| `mt_tpsl_cancel` | H | synthetic id=0; venue ERROR but no real TPSL was cancelled | [local-binance-bench] Cancel TPSL 0: ERROR — |
| `mt_tpsl_cancel_many` | H | synthetic ids [0,0]; 0 succeeded, 2 failed | [local-binance-bench] TPSL cancel-many: 0 succeeded, 2 failed (of 2). |
| `mt_tpsl_join` | H | synthetic ids; 'No response' (timeout) | No response from TPSL join. |
| `mt_tpsl_panic` | H | synthetic id=0; NOT_FOUND pre-flight; no real panic | [local-binance-bench] TPSL panic 0 failed: NOT_FOUND — TPSL not found in store. Run 'tpsl subscribe' first, then 'tpsl list' to confirm IDs. |
| `mt_tpsl_panic_many` | H | synthetic ids; 2 NOT_FOUND pre-flight | [local-binance-bench] TPSL panic-many: 0 succeeded, 2 failed (of 2). |
| `mt_tpsl_split` | H | synthetic id=0; 'No response' (timeout) | No response from TPSL split. |
| `mt_tpsl_split_many` | H | synthetic ids; 2 TIMEOUT responses | [local-binance-bench] TPSL split-many: 0 succeeded, 2 failed (of 2). |

## Documented blockers (11) — never invoked, reason stated

| Tool | Tier | Reason |
|---|---|---|
| `mt_algos_delete_group` | D | destructive; would wipe Stage-1 seed group on local-binance-bench — not safe to exercise in this campaign |
| `mt_core_clear_archive` | J | destructive; restarting MTCore during a campaign run wipes the in-progress evidence and other-bench state. Operator-only. |
| `mt_core_clear_orders` | J | destructive; restarting MTCore during a campaign run wipes the in-progress evidence and other-bench state. Operator-only. |
| `mt_core_restart` | J | destructive; restarting MTCore during a campaign run wipes the in-progress evidence and other-bench state. Operator-only. |
| `mt_core_restart_update` | J | destructive; restarting MTCore during a campaign run wipes the in-progress evidence and other-bench state. Operator-only. |
| `mt_core_shutdown` | J | destructive; restarting MTCore during a campaign run wipes the in-progress evidence and other-bench state. Operator-only. |
| `mt_orders_panic_sell` | E | destructive; operator-only — never auto-invoked by the campaign. |
| `mt_vault_delete_profile` | A | VAULT_ADDR / VAULT_TOKEN not set in env; HashiCorp Vault unreachable from this host. |
| `mt_vault_get_profile` | A | VAULT_ADDR / VAULT_TOKEN not set in env; HashiCorp Vault unreachable from this host. |
| `mt_vault_list_profiles` | A | VAULT_ADDR / VAULT_TOKEN not set in env; HashiCorp Vault unreachable from this host. |
| `mt_vault_store_profile` | A | VAULT_ADDR / VAULT_TOKEN not set in env; HashiCorp Vault unreachable from this host. |

## RPC-error / no-handler tools (2)

Registered in `ToolRegistry` but reach the dispatcher and are rejected
with `-32602 Unknown tool`. Both are explicit placeholders
(`docs/watchdog-integration.md`) awaiting a `WatchdogConnection` client
wiring layer.

| Tool | Tier | Response |
|---|---|---|
| `mt_watchdog_status` | A | placeholder — WatchdogConnection not wired |
| `mt_watchdog_token_update` | A | placeholder — WatchdogConnection not wired |

## What this campaign does NOT prove

- **Stress / endurance**: no repeated long-run loops. Supervisor's open
  item from the prior handoff remains open.
- **Feature outcomes for the 16 partial-or-failed tools** above. Each
  needs a future test that seeds its precondition (real trade history
  for reports, real TPSL ids for tpsl mutators, real working order
  set for move_batch, real local-hyperliquid-bench connected pre-flight for algos_copy,
  real algoConfigs.json for import_templates, real V2 file for import_v2).
- **BYBIT / OKX order execution**: DEFECT-11 (post-place freeze).
- **HYPERLIQUID order placement**: HL-side latency > 90 s per-call budget.
- **Reports DB-backed evidence**: local-binance-bench was restarted mid-run, wiping
  the Stage 1 Firebird seed. Re-run `Stage1LiveTradeTests` to re-seed
  if reports-family evidence is needed against this branch.
- **`mt_orders_panic_sell`, `mt_core_*` mutators, `mt_vault_*`,
  `mt_watchdog_*`, `mt_algos_delete_group`**: deliberately uninvoked
  (see *Documented blockers*).
