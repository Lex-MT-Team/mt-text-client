# MTTextClient.Tests

xUnit / .NET test suite for `mt-text-client`.

---

## 1. Test categories

Tests are tagged with `[Trait("Category", "…")]` (see
`Infrastructure/TraitCategories.cs`):

| Category    | Spawns MCP subprocess | Needs MTCore | All bench profiles | Runs in PR-gate CI | Env vars |
|-------------|:---------------------:|:------------:|:------------------:|:------------------:|----------|
| `Static`    |          no           |      no      |        no          |        yes         | none |
| `Unit`      |          no           |      no      |        no          |        yes         | none |
| `Smoke`     |       yes (1×)        |     yes      |        no          |         no         | `MTC_TESTING_ENV=1` |
| `BenchAll`  |       yes (1×)        |     yes      |       yes          |         no         | `MTC_TESTING_ENV=1` |
| `LiveTrade` |       yes (1×)        |     yes      |        no          |         no         | `MTC_TESTING_ENV=1` AND `MTC_LIVE_TRADES=1` |

- **`Static`** — schema / registry / catalog assertions. Runs in-process
  against the live `ToolRegistry`; no subprocess spawn needed.
- **`Unit`** — in-process logic tests for `ConfirmGate`, `RequestExecutor`,
  `ConnectionStateObservable`, and similar helpers. No subprocess, no MTCore.
- **`Smoke`** — read-only or non-mutating calls against the first configured
  bench profile. Skips cleanly when `MTC_TESTING_ENV` isn't set.
- **`BenchAll`** — parameterised theories that iterate every configured
  bench profile. Profiles whose UDP port isn't bound skip cleanly with a
  diagnostic, so partial bench coverage is still useful.
- **`LiveTrade`** — places real orders on a designated disposable test
  account. Skips unless **both** `MTC_TESTING_ENV=1` and `MTC_LIVE_TRADES=1`
  are set. Never set `MTC_LIVE_TRADES=1` against a non-disposable profile.

---

## 2. Bench requirements

Smoke / BenchAll / LiveTrade need a real MTCore reachable over UDP at the
ports configured in `~/.config/mt-textclient/profiles.json`. The bench
scripts under `bench/` launch one MTCore per profile on UDP 4242–4245 and
verify each port binds within 10s. See [`bench/README.md`](../bench/README.md)
for the script-driven workflow.

`BenchFixture` probes each port; profiles whose UDP port isn't bound are
skipped, not failed.

### Real-bench failure modes

MTCore talks to upstream exchange APIs, so a "port up" probe does not
guarantee every test will pass:

- **Connection flap under sustained load** — after running BenchAll for
  20–30 minutes the bench can stop responding. Symptom: previously-green
  tests start failing on `InnerSuccess=false` or `connected=false`. Fix:
  kill MTCore and restart fresh.
- **Vendor credential / API failures** — exchange-side auth or API issues
  surface as repeated test failures on a single bench. These are
  environmental, not test-infrastructure bugs; check the bench's
  per-profile log before treating the failure as a code regression.

---

## 3. Running tests locally

### PR-gate slice (no MTCore needed)

```bash
dotnet build MTTextClient.sln -c Release
dotnet test MTTextClient.sln -c Release --filter "Category=Static|Category=Unit"
```

Runtime: under a minute on a modern laptop.

### Smoke run (first bench profile)

```bash
bench/start_all_cores.sh   # or restart only the first bench if you want minimum CPU
MTC_TESTING_ENV=1 dotnet test MTTextClient.sln -c Release --filter "Category=Smoke"
```

### BenchAll run (every configured bench profile)

```bash
bench/start_all_cores.sh
MTC_TESTING_ENV=1 dotnet test MTTextClient.sln -c Release --filter "Category=BenchAll"
```

Each `BenchAll` theory runs once per available bench profile. Profiles
whose port isn't bound skip cleanly.

### LiveTrade run (real orders on a disposable test account)

```bash
bench/start_all_cores.sh
MTC_TESTING_ENV=1 MTC_LIVE_TRADES=1 dotnet test MTTextClient.sln -c Release --filter "Category=LiveTrade"
```

LiveTrade places LIMIT orders at or near market so they actually fill on
the venue, and **cleanup is absent by design** — the resulting fills
populate MTCore's Firebird DB, which the reports-family tools read from.
Wiping the DB resets that foundation. Assertions target FILLED /
PARTIALLY_FILLED and the matching `mt_reports_trades` entry, not a
"cancelled" terminus.

---

## 4. CI workflows

| Workflow file | Trigger | What runs | Where |
|---|---|---|---|
| `.github/workflows/ci.yml` | PR + push to main | Static + Unit only | ubuntu-latest + macos-latest |
| `.github/workflows/testing-environment.yml` | manual dispatch + nightly schedule | Smoke + BenchAll, optional LiveTrade | self-hosted bench runner |

The testing-environment workflow has a preflight that fails fast when no
MTCore is reachable on any bench port. The nightly schedule
(`cron: '30 3 * * *'`) never enables LiveTrade — that requires explicit
manual dispatch with `enable_live_trades=true`.

---

## 5. Adding a new MCP tool

Every new tool ships with the full slate. Concrete steps:

1. **Registry entry** — add a `yield return Tool(...)` in
   `Core/ToolRegistry.cs`. Place it in `GetToolDefinitions`,
   `GetInternalToolDefinitions`, or `GetEventToolDefinitions` depending on
   whether it routes through `MapToolToCommand`, `HandleInternalTool`, or
   `HandleEventTool`.
2. **Dispatcher entry** — if it goes through `MapToolToCommand`, add the
   `"mt_xxx" => …` case in `MCP/McpServer.cs`. `RegistrationConsistencyTests`
   catches mismatches between registry and dispatcher.
3. **Schema baseline** — add the tool's `{name, required[]}` to
   `tests/MTTextClient.Tests/_expected/tools.minimum.json`. The tool-floor
   assertion in `ToolCatalogStaticTests` then keeps the name and required
   args present forever.
4. **Snapshot baseline** — re-run
   `dotnet run --project tools/DispatcherSnapshotGenerator -c Release`
   and commit the updated `_expected/commandlines.snapshot.json`.
   `DispatcherSnapshotTests` re-runs the generator in-process and asserts
   byte-equality.
5. **README** — re-run
   `dotnet run --project tools/RegistryReadmeGenerator -c Release`
   and commit the updated `README.md`. `ReadmeParityTests` validates the
   autogenerated section against the live registry.
6. **Smoke test** — add a `[SkippableFact]` to the appropriate
   `Tools/*Tests.cs` covering the happy path. Use the existing tests as a
   template.
7. **ConfirmGate** — if destructive, declare `confirm` in the schema's
   `required` array and add the name to `ConfirmRequiredTools` in
   `Static/ConfirmGateStaticTests.cs`.
8. **BenchAll** — if the tool is exchange-agnostic and read-only, add it
   to `Integration/BenchAllTests.cs` so it runs against every configured
   bench profile.
9. **LiveTrade** — if it places orders, add a
   `[Trait("Category", "LiveTrade")]` test guarded with
   `Skip.IfNot(EnvFlags.LiveTrades, "...")`.

The Static `[Theory]` over the whole catalog automatically picks up new
tools without code changes.

---

## 6. Apple Silicon notes

The MSBuild post-build target `PatchMTSharedArm64` re-applies the ARM64 PE
patch to `lib/MTShared.dll` on every macOS build via
`scripts/patch_mtshared_arm64.py` (idempotent; no-op on Linux/Windows). The
repo ships the DLL already patched; the post-build run is a safety net for
vendor refreshes. `McpFixture.EnsurePatched()` is a runtime safety guard
and is a no-op on every well-formed build.

If `python3` is missing on a developer machine, the post-build target
emits a warning instead of failing
(`ContinueOnError="WarnAndContinue"`).

---

## 7. Repo paths the harness assumes

| Path                                                    | Found by                                                    | Used for                                                  |
|---------------------------------------------------------|-------------------------------------------------------------|-----------------------------------------------------------|
| `<repo>/MTTextClient.csproj`                            | `RepoPaths.Root` walks up from `AppContext.BaseDirectory`   | locating the repo root                                    |
| `<repo>/bin/Release/net8.0/MTTextClient`                | `RepoPaths.McpBinary`                                       | `McpFixture` subprocess spawn                             |
| `<repo>/lib/MTShared.dll`                               | `RepoPaths.MTSharedSource`                                  | source-of-truth copy (ships ARM64-patched)                |
| `<repo>/bin/Release/net8.0/MTShared.dll`                | `RepoPaths.MTSharedBuilt`                                   | runtime safety-net patch target                           |
| `<test-project>/_expected/tools.minimum.json`           | `RepoPaths.ToolsMinimumFixture`                             | locked tool baseline                                      |
| `<test-project>/_expected/commandlines.snapshot.json`   | loaded directly by `DispatcherSnapshotTests`                | per-tool CLI-string snapshot                              |
| `<repo>/scripts/patch_mtshared_arm64.py`                | invoked by `MTTextClient.csproj` post-build target on macOS | ARM64 PE Machine-field flip                               |
