# MTTextClient.Tests — operator runbook

xUnit / .NET test suite for mt-text-client. The canonical plan is
`~/Documents/SharedFolder/UpgradePlan/UnifiedDevelopmentPlan.md`. This file
is the operator-facing runbook: how to start the bench, how to run each
category, what counts as a known issue, and what to update when adding a
new tool.

---

## 1. Test categories

Tests are tagged with `[Trait("Category", "...")]` (see
`Infrastructure/TraitCategories.cs`):

| Category    | Spawns MCP subprocess | Needs MTCore | All 4 benches | Runs in PR-gate CI | Env vars |
|-------------|:---------------------:|:------------:|:-------------:|:------------------:|----------|
| `Static`    |       yes (1×)        |      no      |      no       |        yes         | none |
| `Unit`      |          no           |      no      |      no       |        yes         | none |
| `Smoke`     |       yes (1×)        |     yes      |      no       |         no         | `MTC_TESTING_ENV=1` |
| `BenchAll`  |       yes (1×)        |     yes      |     yes       |         no         | `MTC_TESTING_ENV=1` |
| `LiveTrade` |       yes (1×)        |     yes      |      no       |         no         | `MTC_TESTING_ENV=1` AND `MTC_LIVE_TRADES=1` |

- **`Static`** — schema / registry / catalog assertions. The MCP server is
  spawned once for the test session (`McpFixture` is a collection fixture)
  to capture the live `tools/list`; in-process tests added in Stage 0.3
  (DispatcherSnapshotTests, ReadmeParityTests) call the registry directly
  without a subprocess.
- **`Unit`** — in-process logic tests added in Stage 0.4 (ConfirmGate,
  RequestExecutor, ConnectionStateObservable). No subprocess, no MTCore.
- **`Smoke`** — read-only or non-mutating calls against bench_01 (UDP:4242,
  BYBIT). Skips cleanly when `MTC_TESTING_ENV` isn't set.
- **`BenchAll`** — Stage 0.4 addition. Parameterised theories that iterate
  the four configured bench profiles (bench_01…bench_04). Each test skips
  the profiles whose UDP port isn't bound, so partial bench coverage is
  still useful.
- **`LiveTrade`** — places real orders on the disposable test account.
  Scaffolded today; full LiveTrade coverage lands with Stages 1-3.

---

## 2. Starting the bench

The four MTCore processes that back Smoke/BenchAll/LiveTrade are
operator-managed, not test-owned. The fixture (`BenchFixture`) probes
each UDP port and gates tests on reachability.

```bash
# Start all four cores (bench_01..bench_04 on UDP 4242..4245).
~/mt-bench/scripts/start_all_cores.sh

# Health check.
~/mt-bench/scripts/status.sh

# Stop everything.
~/mt-bench/scripts/stop_all_cores.sh
```

The four bench identities:

| Profile  | Port  | Exchange     | License            | MTCore profile dir   |
|----------|------:|--------------|--------------------|----------------------|
| bench_01 | 4242  | BYBIT        | <bench-01-license>        | `<bench-01-profile>`      |
| bench_02 | 4243  | BINANCE      | <bench-02-license>        | `<bench-02-profile>`            |
| bench_03 | 4244  | HYPERLIQUID  | <bench-03-license>        | `<bench-03-profile>`             |
| bench_04 | 4245  | OKX          | <bench-04-license>        | `<bench-04-profile>`        |

Profile-to-IP/port mapping for the mt-text-client side lives in
`~/.config/mt-textclient/profiles.json` (all four bench profiles point at
`127.0.0.1:42XX`).

### Bench flakiness — read this before debugging

Bench cores are real MTCore instances calling upstream exchange APIs. The
fixture's UDP-bind probe reports "port up", but a few real-world failure
modes look like test bugs and aren't:

- **MCP-002 (connection flap)** — after the bench has been running for
  20-30 min under repeated test load, MTCore can stop responding to
  `mt_status`. Symptom: previously-green tests start failing on
  `InnerSuccess=false` or `connected=false`. Fix: kill MTCore and
  restart fresh. The first 5-10 minutes after restart are reliable.
- **bench_02 / BINANCE — vendor UDS-key issue** — `core_02.log` shows
  `GetUserDataStreamKey(SPOT) | Gone` and the UDS auth loop restarts
  endlessly. The exchange-side credential pair is upstream-broken.
  BenchAll on bench_02 fails on every test with this signal; not a
  test-infrastructure bug. Tracked separately.
- **bench_03 / HYPERLIQUID & bench_04 / OKX startup abort** — vendor
  APIv2 503 ("Service Temporarily Unavailable") for license retrieval.
  The MTCore process aborts with `Abort trap: 6` at startup. The bench
  has no UDP port to probe and the fixture skips cleanly.

---

## 3. Running tests locally

### 3.1 PR-gate run (no MTCore needed)

```bash
cd ~/mt-dev/mt-text-client
dotnet build MTTextClient.sln -c Release
dotnet test MTTextClient.sln -c Release --filter "Category=Static|Category=Unit"
```

Expected: ~1,712 test cases (catalog `[Theory]` over 206 tools + Stage 0.3
snapshot + Stage 0.4 unit + Stage 0.6 build-hygiene tests), all green.
Total runtime <60s.

### 3.2 Smoke run (bench_01 only)

```bash
~/mt-bench/scripts/start_all_cores.sh        # or restart just core_01 if you want minimum CPU
cd ~/mt-dev/mt-text-client
MTC_TESTING_ENV=1 dotnet test MTTextClient.sln -c Release --filter "Category=Smoke"
```

Expected: 63/63 pass against a fresh bench_01. Runtime ~55s-1m30s.

### 3.3 BenchAll run (all four bench profiles)

```bash
~/mt-bench/scripts/start_all_cores.sh
cd ~/mt-dev/mt-text-client
MTC_TESTING_ENV=1 dotnet test MTTextClient.sln -c Release --filter "Category=BenchAll"
```

Each of the 7 BenchAll theory methods runs once per available bench
profile. Profiles whose port isn't bound (e.g. when a core aborted at
startup) skip cleanly with a diagnostic naming the exchange. Expect
~5-7 minutes if all 4 cores are up.

### 3.4 LiveTrade run (real orders on the disposable account)

```bash
~/mt-bench/scripts/start_all_cores.sh
cd ~/mt-dev/mt-text-client
MTC_TESTING_ENV=1 MTC_LIVE_TRADES=1 dotnet test MTTextClient.sln -c Release --filter "Category=LiveTrade"
```

Today LiveTrade tests verify only schema reachability (no actual orders);
Stages 1-3 expand them. **Do not** set `MTC_LIVE_TRADES=1` against a
non-disposable profile.

---

## 4. CI workflows

| Workflow file | Trigger | What runs | Where |
|---|---|---|---|
| `.github/workflows/ci.yml` | PR + push to main | Static + Unit only | ubuntu-latest + macos-latest |
| `.github/workflows/testing-environment.yml` | manual dispatch + nightly schedule | Smoke + BenchAll, optional LiveTrade | self-hosted (mt-bench label) |

The testing-environment workflow has a preflight that fails fast when no
MTCore is reachable on any bench port. The nightly schedule
(`cron: '30 3 * * *'`) never enables LiveTrade — operators must
explicitly dispatch with `enable_live_trades=true`.

---

## 5. Known issues

Tests that assert the *current broken* behaviour are tagged
`[Trait("KnownIssue", "MCP-NNN")]`. When a fix lands, search for the bug
ID across `tests/` and invert the assertion or move the tool from
`ConfirmKnownGaps` to `ConfirmRequiredTools`.

| Bug ID         | Tools affected                                                                       | Where it's asserted today                                                  |
|----------------|--------------------------------------------------------------------------------------|----------------------------------------------------------------------------|
| MCP-002        | (transient bench flap; not a test, an env issue)                                     | comment in `BenchFixture` + this README §2                                  |
| MCP-003        | `mt_reports_dates`, `mt_reports_comments`, `mt_autostops_reports`, `mt_exchange_ticker24` | `ReportsTests`, `AutoStopsTests`, `ExchangeTests` (PR1 fix on `fix/known-defects-batch-1`) |
| MCP-005        | `mt_import_templates`                                                                | `ImportTests` (PR1 fix)                                                    |
| MCP-006        | `mt_vault_list_profiles`, `mt_vault_store_profile`                                   | `VaultTests`                                                                |
| MCP-009        | `mt_core_restart` (vendor-side Firebird crash; tests never call it)                  | `CoreTests` static reminder                                                 |
| MCP-010-ext    | `mt_profile_settings_update`                                                         | `ProfileSettingsTests`, `ConfirmGateStaticTests` (PR1 fix)                  |
| MCP-010-set    | `mt_settings_set`                                                                    | `ConfirmGateStaticTests` (PR1 fix)                                          |
| bench_02 UDS   | every BenchAll test on bench_02 BINANCE                                              | environmental — vendor-side credential issue, not test infra                |

---

## 6. Adding a new MCP tool

Per UnifiedDevelopmentPlan §4 Definition of Done, every new tool ships
with the full slate. Concrete steps after Stage 0.3 / 0.4:

1. **Registry entry** — add a `yield return Tool(...)` in
   `Core/ToolRegistry.cs`. Either `GetToolDefinitions`, `GetInternalToolDefinitions`,
   or `GetEventToolDefinitions` depending on whether it routes through
   `MapToolToCommand`, `HandleInternalTool`, or `HandleEventTool`.
2. **Dispatcher entry** — if it goes through `MapToolToCommand`, add the
   `"mt_xxx" => …` case in `MCP/McpServer.cs`. The new
   `RegistrationConsistencyTests` catches mismatches between registry and
   dispatcher.
3. **Schema baseline** — add the tool's `{name, required[]}` to
   `tests/MTTextClient.Tests/_expected/tools.minimum.json`. The 206-tool
   floor in `ToolCatalogStaticTests` then asserts this name forever stays
   present.
4. **Snapshot baseline** — re-run
   `dotnet run --project tools/DispatcherSnapshotGenerator -c Release`
   and commit the updated `_expected/commandlines.snapshot.json`. The
   `DispatcherSnapshotTests` re-runs the generator in-process and asserts
   byte-equality, so any future CLI-string drift breaks CI.
5. **README** — re-run
   `dotnet run --project tools/RegistryReadmeGenerator -c Release`
   and commit the updated `README.md`. The `ReadmeParityTests` validates
   the autogenerated section against the live registry.
6. **Smoke test** — add a `[SkippableFact]` to the appropriate
   `Tools/*Tests.cs` covering the happy path. Use the patterns in
   `AccountTests` (real-shape assertions on Stage 0.3 / PR2 lines) as a
   template.
7. **ConfirmGate** — if destructive, declare `confirm` in the schema's
   `required` array (Stage 0.4 ConfirmGate picks it up automatically) and
   add the name to `ConfirmRequiredTools` in
   `Static/ConfirmGateStaticTests.cs`.
8. **BenchAll** — if the tool is exchange-agnostic and read-only, add it
   to `Integration/BenchAllTests.cs` so it runs against all four bench
   profiles.
9. **LiveTrade** — if it places orders, add a
   `[Trait("Category", "LiveTrade")]` test guarded with
   `Skip.IfNot(EnvFlags.LiveTrades, "...")`.

The Static `[Theory]` over the whole catalog automatically picks up new
tools without code changes.

---

## 7. Apple Silicon notes

Stage 0.6 made the PE patch automatic. The repo ships `lib/MTShared.dll`
with `Machine=0xAA64` already; the MSBuild post-build target re-applies
the patch (idempotent) on every macOS build via
`scripts/patch_mtshared_arm64.py`. Linux / Windows builds skip the
target entirely. `McpFixture.EnsurePatched()` remains as a runtime
safety guard but is a no-op on every well-formed build.

If `python3` is missing on a developer laptop, the post-build target
emits a warning instead of failing (`ContinueOnError="WarnAndContinue"`).
The in-repo DLL is already patched, so this just disables the
vendor-refresh safety net.

---

## 8. Repo paths the harness assumes

| Path                                                | Found by                                                                 | Used for                                                  |
|-----------------------------------------------------|--------------------------------------------------------------------------|-----------------------------------------------------------|
| `<repo>/MTTextClient.csproj`                        | `RepoPaths.Root` walks up from `AppContext.BaseDirectory`                | locating the repo root                                    |
| `<repo>/bin/Release/net8.0/MTTextClient`            | `RepoPaths.McpBinary`                                                    | `McpFixture` subprocess spawn                             |
| `<repo>/lib/MTShared.dll`                           | `RepoPaths.MTSharedSource`                                               | source-of-truth copy (ships ARM64-patched post-Stage-0.6) |
| `<repo>/bin/Release/net8.0/MTShared.dll`            | `RepoPaths.MTSharedBuilt`                                                | runtime safety-net patch target                           |
| `<test-project>/_expected/tools.minimum.json`       | `RepoPaths.ToolsMinimumFixture`                                          | locked 206-tool baseline                                  |
| `<test-project>/_expected/commandlines.snapshot.json` | (loaded directly by `DispatcherSnapshotTests`)                         | per-tool CLI-string snapshot                              |
| `<repo>/scripts/patch_mtshared_arm64.py`            | invoked by `MTTextClient.csproj` post-build target on macOS              | ARM64 PE Machine-field flip                               |
