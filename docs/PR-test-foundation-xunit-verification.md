# PR — `chore/test-foundation-xunit` (Stage 0.1)

**Plan reference:** `~/Documents/SharedFolder/UpgradePlan/UnifiedDevelopmentPlan.md` §6 Stage 0.1
**Owner overrides honoured:** OV-1 (xUnit/.NET only, no Python), OV-3 (no Mock MTCore — Smoke uses real MTCore), OV-4 (two CI workflows; PR-gate runs Static+Unit only), OV-5 (`confirm:true` hardcoded in tests; gate is audit aid + injection-defense, not a security boundary)

## Summary

Adds an xUnit test project under `tests/MTTextClient.Tests/` plus two CI workflows:

- `.github/workflows/ci.yml` — PR-gate; `Category=Static|Category=Unit`; runs on every PR; Linux + macOS arm64; no MTCore subprocess.
- `.github/workflows/testing-environment.yml` — manual / scheduled; `Category=Smoke` (and optionally `Category=LiveTrade`); requires a self-hosted runner with the bench wired up.

Test composition (this PR):

| Category | Test count (theory cases) | Drives |
|---|---:|---|
| Static | ~1240 | one collection fixture spawns the MCP server; `[Theory]` parametrises ~6 catalog assertions × 206 tools |
| Smoke | ~50 | per-area read/subscribe/roundtrip, plus KnownIssue regression-pinned tests |
| LiveTrade | 2 | placeholder schema-reachability scaffolding (deeper coverage lands with feature iterations) |

## What is intentionally not in this PR

| Item | Owning iteration |
|---|---|
| BouncyCastle.Cryptography csproj dep | Stage 0.5 (`chore/dependency-bouncycastle-review`) |
| ARM64 PE patch as MSBuild target | Stage 0.6 (`chore/build-apple-silicon-hygiene`) — `McpFixture.EnsurePatched()` self-heals at test time as a stop-gap |
| Tool registry refactor | Stage 0.3 (`chore/registry-deep-refactor`) |
| ConfirmGate / RequestExecutor / ConnectionStateObservable helpers | Stage 0.4 |
| Per-tool deep Smoke coverage of all 206 tools | feature iterations Stage 1+ (this PR is *foundation*) |

The README in `tests/README.md` documents the conventions iterations follow when adding tests.

## Verification matrix

### Static + Unit (PR-gate, no MTCore)

| Check | Where | Pass criterion |
|---|---|---|
| `dotnet build MTTextClient.sln -c Release` succeeds | local + CI matrix Linux + macOS arm64 | `0 Error(s)` |
| `dotnet test --filter "Category=Static|Category=Unit"` is green | local + CI | all asserts pass; runtime <60s |
| 206 baseline tools all advertised | `Static/ToolCatalogStaticTests.EveryBaselineTool_StillExistsInLiveCatalog` | `[Theory]` over `tools.minimum.json`; all green |
| Tool naming convention | `Static/ToolCatalogStaticTests.EveryLiveToolByName_HasValidName` | every name matches `mt_<area>_<verb>` regex |
| `inputSchema.required` shape valid | `Static/ToolCatalogStaticTests.EveryLiveToolByName_HasInputSchemaWithRequiredArrayWhenPresent` | array when present; absent when no required |
| No duplicate `required` entries | `Static/ToolCatalogStaticTests.EveryLiveToolByName_HasNoDuplicateSchemaFields` | catches the `mt_settings_set` duplicate-confirm class |
| Confirm-required tools declare `confirm` | `Static/ConfirmGateStaticTests.ConfirmRequiredTool_DeclaresConfirmInSchema` | curated list; all green |
| Known confirm-gate gaps documented | `Static/ConfirmGateStaticTests.ConfirmGap_StillBroken_DocumentsBugId` | `[Trait("KnownIssue", "MCP-010-ext")]`; asserts current broken state |

### Smoke (real MTCore on local-bybit-bench)

| Check | Where | Status |
|---|---|---|
| `mt_status` returns valid envelope before connect | `CoreTests.mt_status_BeforeConnect_ReturnsZeroConnected` | Smoke |
| `mt_connect local-bybit-bench` acks request | `CoreTests.mt_connect_local-bybit-bench_AcceptsRequest` | Smoke |
| `mt_status` reports CONNECTED within 30s | `CoreTests.mt_status_after_connect_eventually_reports_connected` | Smoke (handles MCP-002 race) |
| `mt_account_*` (6 tools) all `success:true` | `AccountTests` | Smoke |
| `mt_algos_list` returns data array | `AlgosTests.mt_algos_list_returns_data_array` | Smoke |
| Settings get/set round-trip with `confirm:true` | `SettingsTests.mt_settings_set_with_confirm_roundtrips_log_level` | Smoke |
| Settings set without confirm rejected | `SettingsTests.mt_settings_set_without_confirm_is_rejected` | Smoke (proves OV-5 gate works) |
| Blacklist add/remove typed-storage roundtrip | `BlacklistTests.mt_blacklist_add_remove_roundtrip_with_bogus_symbol` | Smoke (PR #3 regression pin) |
| Subscribe/unsubscribe pairs (alerts, notifications, marketdata, tpsl) | per-area tests | Smoke |
| MCP-003 cluster asserts current broken state | `ReportsTests`, `AutoStopsTests`, `ExchangeTests` | Smoke + KnownIssue |
| MCP-005 / MCP-006 / MCP-010-ext asserted | `ImportTests`, `VaultTests`, `ProfileSettingsTests` | Smoke + KnownIssue |

### LiveTrade

LiveTrade tests are scaffolded but minimal. `mt_orders_place` and `mt_orders_cancel` have schema-reachability tests; deeper coverage lands with Stage 1 / Stage 2 of the upgrade plan.

## Local verification log

(To be filled in by the operator running this PR's verification.)

```text
$ cd ~/mt-dev/mt-text-client
$ dotnet build MTTextClient.sln -c Release
  → 0 Error(s)

$ dotnet test MTTextClient.sln -c Release --filter "Category=Static|Category=Unit"
  → Passed: <N>, Failed: 0, Skipped: 0
  → Total time: <T>s

$ ~/mt-bench/scripts/start_all_cores.sh
  → core_01..04 UP

$ MTC_TESTING_ENV=1 dotnet test MTTextClient.sln -c Release --filter "Category=Smoke"
  → Passed: <N>, Failed: 0, Skipped: 0 (with bench)
  → KnownIssue tests: <K> passed (asserting current broken state)
```
