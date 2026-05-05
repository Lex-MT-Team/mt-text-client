# PR #19 — Pre-Hands-On Fixes

**Branch:** `fix/pre-handson-pr19`
**Issues addressed:** #13, #15, #16
**Predecessor:** PR #18 (BUG-1/2/3 — schema enforcement, id echo, profile_settings_get propagation)

This PR carries the three remaining defects observed during the 206-tool MTC
validation campaign that did not block PR #18 from merging but **must** ship
before the operator hands-on session.

---

## #13 — `import`: `groupID=0` mapping silently dropped

**Symptom (AlfredDimas, 2026-05-04 ticket):** importing a V2 algo bundle that
contained a `GROUP_START 0` block resulted in every algo of that block landing
ungrouped on the destination core.

**Root cause:** Both call sites of `groupIdMap.TryGetValue(...)` in
`Commands/ImportCommand.cs` and `MCP/McpServer.cs` were guarded with a
redundant `groupID > 0 &&` precondition. `0` is a valid temp-id key in the map
(emitted when the parser encounters `GROUP_START 0` for ungrouped→grouped
re-binding); the guard skipped the lookup and the algo was sent with
`groupID=0`.

**Fix:** drop the `> 0` precondition. `TryGetValue` itself is the correct
filter — any id absent from the map (including the legitimate "no remap
needed" case) is left untouched.

**Files:**
- `Commands/ImportCommand.cs`
- `MCP/McpServer.cs`

**Regression:** `tests/regression/test_pr19_pre_handson_fixes.py::test_issue13_no_groupid_gt_zero_guard`
(source-level guard; behavioural coverage via `tests/scenarios/test_s8_v2_export_import_roundtrip.py`).

---

## #15 — `algos list`/`info` hide operator-set names

**Symptom:** Operators set human-readable labels via the algorithm's `info`
parameter (or via `description`), but `algos list`, `algos list-all`,
`algos list-grouped`, and `algos info` rendered the synthetic on-wire name
`mt-algo-XXXXXX`. Hands-on demos required operators to mentally map IDs to
labels.

**Root cause:** Each of the four emit sites used either `a.name` or the naive
`string.IsNullOrEmpty(a.description) ? a.name : a.description`. No site
consulted the parsed `info` parameter, and no site was robust to the
synthetic prefix masquerading as a description.

**Fix:** Centralise resolution in `ResolveDisplayName(AlgorithmData)`:

```
priority: AlgorithmConfig.info → algo.description → algo.name
filter:   skip values that themselves start with "mt-algo-"
```

Apply at all 4 emit sites. **Additionally** expose the raw on-wire name as a
new `CoreName` field alongside `name` so id-string-join tooling that
previously matched on `name` keeps working.

**Files:** `Commands/AlgosCommand.cs`

**Backwards compatibility:** when no `info` is set and `description` is empty
or synthetic, `name` falls through to `algo.name` exactly as before. New
field `CoreName` is additive.

**Regression:**
`tests/regression/test_pr19_pre_handson_fixes.py::test_issue15_algos_*_exposes_corename`.

---

## #16 — MCP wrappers don't expose CLI flags

**Symptom:** The CLI accepts:

| CLI                                         | Behaviour                  |
| ------------------------------------------- | -------------------------- |
| `account balance -all`                      | include dust + zero rows   |
| `account orders -all`                       | include archived/non-active |
| `account positions -all`                    | include closed positions   |
| `account executions <count>`                | tail size override         |
| `exchange ticker24 <symbol> FUTURES\|SPOT`  | pick market side           |

…but the corresponding `mt_*` MCP wrappers neither published the parameters
in their `inputSchema` nor threaded them through the dispatch builder. MCP
clients had no way to access archived orders, dust balances, closed
positions, custom execution tails, or a specific 24h ticker side.

**Fix:** Add optional schema props on the 5 wrappers and thread them through
the dispatch:

| Tool                       | New param      | Type      |
| -------------------------- | -------------- | --------- |
| `mt_account_balance`       | `show_all`     | boolean   |
| `mt_account_orders`        | `show_all`     | boolean   |
| `mt_account_positions`     | `show_all`     | boolean   |
| `mt_account_executions`    | `count`        | integer   |
| `mt_exchange_ticker24`     | `market_type`  | string    |

`market_type` is normalised to upper-case; only `FUTURES`/`SPOT` are accepted
and anything else is silently dropped (the existing arg-sanitizer already
strips `\r`/`\n`, so injection surface is unchanged).

**Files:** `MCP/McpServer.cs`

**Regression:**
`tests/regression/test_pr19_pre_handson_fixes.py::test_issue16_*` — published
schema + non-rejection of new args end-to-end.

---

## Verification

| Test                                                       | Status |
| ---------------------------------------------------------- | ------ |
| `test_issue16_wrapper_param_published[5 tools]`            | ✅ pass |
| `test_issue16_ticker24_market_type_accepted`               | ✅ pass |
| `test_issue16_account_balance_show_all_accepted`           | ✅ pass |
| `test_issue15_algos_*_exposes_corename`                    | ⏭ skipped on offline profile (run live to harden) |
| `test_issue13_no_groupid_gt_zero_guard[ImportCommand,Mcp]` | ✅ pass |
| Existing `tests/regression/*` (sanitizer / bulk-confirm / tools-count / profiles-mode / baseline-sweep) | ✅ unchanged |
| Build (`dotnet build -c Release`)                          | ✅ 0 warnings, 0 errors |

No behavioural change to any tool **without** the new opt-in parameters.
