# mt-bench scripts

Operator scripts for the four-bench MTCore test environment used by
`MTTextClient.Tests` Smoke and LiveTrade categories.

## What's here

| Script | Purpose |
|---|---|
| `start_all_cores.sh` | Launches all 4 MTCore benches with `nohup`, captures PIDs, verifies UDP ports bind within 10s, prints a health summary. |
| `stop_all_cores.sh` | SIGTERMs every recorded PID, grace period, SIGKILLs stragglers by port, cleans .NET mutex sentinels, removes PID files. |
| `status.sh` | bash-3.2-compatible health check. Prints expected port/exchange/license + observed PID + command-line snippet for each bench. |

## Bench layout

The scripts expect each bench to live in its own cloned MTCore distribution:

```
~/mt-bench/
  cores-cloned/
    bench_01/    BYBIT   port 4242 license <bench-01-license>  profile <bench-01-profile>
    bench_02/    BINANCE port 4243 license <bench-02-license>  profile <bench-02-profile>
    bench_03/    HYPERLIQUID port 4244 license <bench-03-license> profile <bench-03-profile>
    bench_04/    OKX     port 4245 license <bench-04-license>  profile <bench-04-profile>
  logs/          per-bench MTCore stdout/stderr (written by start_all_cores.sh)
  pids/          per-bench PID files (one per bench, named bench_XX.pid)
  scripts/       (optional) symlinks back to this folder
```

Per-bench clones exist for **filesystem isolation** — separate Firebird Embedded
lock files, separate `fbembed5` dylib paths, separate `MoonTrader.app` resources.
This isolation lets one bench be killed and restarted without disturbing the
others.

## Symlinking from `~/mt-bench/scripts/`

If you have legacy callers that expect the scripts at `~/mt-bench/scripts/`,
point that directory at this folder:

```bash
ln -sfn "$HOME/mt-dev/mt-text-client/bench/start_all_cores.sh" \
        "$HOME/mt-bench/scripts/start_all_cores.sh"
ln -sfn "$HOME/mt-dev/mt-text-client/bench/stop_all_cores.sh" \
        "$HOME/mt-bench/scripts/stop_all_cores.sh"
ln -sfn "$HOME/mt-dev/mt-text-client/bench/status.sh" \
        "$HOME/mt-bench/scripts/status.sh"
```

## DEFECT-11 / MTCORE-FREEZE

After the first order placement on BYBIT or OKX benches, MTCore's UDP receive
loop can wedge — the process stays alive (and bound), CPU may climb, but
`mt_status` reports `DISCONNECTED`. The fix is to kill the bench and
`start_all_cores.sh` re-runs.

**Clone isolation does NOT solve DEFECT-11.** Earlier mitigations assumed
separate filesystem footprints would prevent the freeze. They don't. The freeze
is internal to MTCore (vendor-side). Clones are kept because they make
individual bench restarts cleaner, not because they fix the freeze.

## DEFECT-12 / .NET mutex sentinels

When MTCore is killed (vs. exits cleanly), the .NET runtime leaves a sentinel
at `/tmp/.dotnet/shm/global/MTProfile-<sha1(profile)>` with the dead PID
encoded in the first 16 bytes. The next start aborts with `Global profile mutex
can not be created` until the sentinel is removed. Both `start_all_cores.sh`
and `stop_all_cores.sh` clean these automatically.

## Portability

All three scripts are bash 3.2 compatible (macOS's system `/bin/bash`).
They use plain parallel arrays — **no `declare -A`** anywhere — so they
report correct results on macOS without requiring a newer bash from Homebrew.

## Quick smoke

```bash
./status.sh          # what's running right now
./start_all_cores.sh # launch everything; exits 1 if any port doesn't bind
./status.sh          # re-confirm
# ... run tests ...
./stop_all_cores.sh  # clean shutdown; exits 1 if anything stuck
```
