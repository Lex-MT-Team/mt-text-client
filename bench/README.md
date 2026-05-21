# bench scripts

Convenience scripts for running a small cluster of MTCore instances locally
so the `MTTextClient.Tests` Smoke / BenchAll / LiveTrade categories have a
real wire to talk to.

## What's here

| Script | Purpose |
|---|---|
| `start_all_cores.sh` | Launches one MTCore per configured bench slot with `nohup`, captures PIDs, verifies UDP ports bind within 10s, prints a health summary. |
| `stop_all_cores.sh`  | SIGTERMs every recorded PID, grace period, SIGKILLs stragglers by port, cleans .NET mutex sentinels, removes PID files. |
| `status.sh`          | bash-3.2-compatible health check. Prints expected port / exchange / profile + observed PID + command-line snippet for each bench. |

## Layout

The scripts assume each bench lives in its own cloned MTCore distribution
under `$BENCH_ROOT` (default `$HOME/mt-bench`):

```
$BENCH_ROOT/
  cores-cloned/
    bench_01/      MTCore + profile dir
    bench_02/      MTCore + profile dir
    …
  logs/            per-bench MTCore stdout/stderr (written by start_all_cores.sh)
  pids/            per-bench PID files (one per bench, named <slot>.pid)
  bench.conf       optional per-machine config (see below)
```

Per-bench clones exist for **filesystem isolation** — separate Firebird
Embedded lock files, separate `fbembed5` dylib paths, separate
`MoonTrader.app` resources. The isolation lets one bench be killed and
restarted without disturbing the others.

## Configuration

The scripts are data-driven: there are no hard-coded license ids, profile
names, ports, or exchanges. Provide them either via environment variables or
by sourcing `$BENCH_ROOT/bench.conf` (the start/stop/status scripts source it
automatically if present).

For every slot in `BENCH_SLOTS` (default `bench_01 bench_02 bench_03
bench_04`) define:

| Variable | Meaning |
|---|---|
| `BENCH_<SLOT>_LICENSE`  | MTCore license id |
| `BENCH_<SLOT>_PROFILE`  | MTCore profile directory name (under the clone) |
| `BENCH_<SLOT>_PORT`     | UDP port to bind |
| `BENCH_<SLOT>_EXCHANGE` | Exchange label (BYBIT / BINANCE / OKX / HYPERLIQUID) |

Optional:

| Variable | Default |
|---|---|
| `BENCH_ROOT`   | `$HOME/mt-bench` |
| `BENCH_SLOTS`  | `bench_01 bench_02 bench_03 bench_04` |

### Sample `bench.conf`

```bash
# $BENCH_ROOT/bench.conf
BENCH_SLOTS="bench_01 bench_02"

BENCH_BENCH_01_LICENSE="<your license id>"
BENCH_BENCH_01_PROFILE="<your profile name>"
BENCH_BENCH_01_PORT=4242
BENCH_BENCH_01_EXCHANGE=BYBIT

BENCH_BENCH_02_LICENSE="<your license id>"
BENCH_BENCH_02_PROFILE="<your profile name>"
BENCH_BENCH_02_PORT=4243
BENCH_BENCH_02_EXCHANGE=BINANCE
```

Once `bench.conf` is in place, running `start_all_cores.sh` without any extra
environment is enough.

## Quick smoke

```bash
./status.sh          # what's running right now
./start_all_cores.sh # launch everything; exits 1 if any port doesn't bind
./status.sh          # re-confirm
# … run tests …
./stop_all_cores.sh  # clean shutdown; exits 1 if anything stuck
```

## Known wedge behaviour

After running multiple order placements against a bench, MTCore's UDP
receive loop can wedge — the process stays alive (and bound), CPU may
climb, but `mt_status` reports `DISCONNECTED`. The fix is to kill the bench
and re-run `start_all_cores.sh`. Clone isolation does **not** prevent this
wedge; it is internal to MTCore (vendor-side). Clones are kept because they
make individual bench restarts cleaner.

## Mutex sentinels

When MTCore is SIGKILLed (vs. exits cleanly), the .NET runtime leaves a
sentinel at `/tmp/.dotnet/shm/global/MTProfile-<sha1(profile)>` with the
dead PID encoded in the first 16 bytes. The next start aborts with
`Global profile mutex can not be created` until the sentinel is removed.
Both `start_all_cores.sh` and `stop_all_cores.sh` clean these automatically.

## Portability

All three scripts target bash 3.2 (macOS's system `/bin/bash`). They use
plain parallel arrays — no `declare -A` anywhere — so they work on stock
macOS without requiring a newer bash from Homebrew.
