#!/bin/bash
# mt-bench start_all_cores — durable per-process launch with PID capture and
# post-launch port verification.
#
# Stage Supervisor P1 fix: the previous version started cores as background
# jobs under the launching shell.  Under a non-interactive shell (agent,
# cron, sub-shell) those jobs died when the parent exited, leaving the
# bench unreliable.  This version uses nohup + redirected stdio so each
# MTCore detaches cleanly and persists past parent shell exit.
#
# Layout:
#   ~/mt-bench/cores-cloned/bench_XX/   per-bench MTCore clone
#   ~/mt-bench/logs/bench_XX.log         per-bench stdout/stderr
#   ~/mt-bench/pids/bench_XX.pid         written with the actual MTCore PID
#
# Per-bench clone layout exists for filesystem isolation (separate Firebird
# Embedded lock files, fbembed5 dylib symlink, MoonTrader.app resources).
# IMPORTANT: clone isolation does NOT solve DEFECT-11 / MTCORE-FREEZE; the
# freeze is MTCore-internal and reproduces inside clones.  Clones are kept
# because they make individual bench restarts cleaner, not because they fix
# the freeze.

set -uo pipefail

BASE="$HOME/mt-bench"
CLONES="$BASE/cores-cloned"
LOGS="$BASE/logs"
PIDS="$BASE/pids"

mkdir -p "$LOGS" "$PIDS"

# Slot definitions — parallel arrays (bash 3.2 compatible).
SLOTS=(bench_01 bench_02 bench_03 bench_04)
LICENSES=(15557344036 15557413425 15557471470 15557490493)
PROFILES=(Tour_CORP_001 Lex_002 hl_002 Lex_okx_001)
PORTS=(4242 4243 4244 4245)
EXCHANGES=(BYBIT BINANCE HYPERLIQUID OKX)

# Stop any leftover MTCore + clean .NET mutex sentinels.
# When MTCore is SIGKILLed the runtime leaves behind a sentinel at
# /tmp/.dotnet/shm/global/MTProfile-<sha1(profile)> with the dead PID in
# the first 16 bytes.  The next start aborts with "Global profile mutex
# can not be created - MTCore with this profile already running" until
# the sentinel is removed.  This is DEFECT-12 — documented in memory.
echo "[*] Stopping any running MTCore instances..."
pkill -9 -f "MTCore --license-id" 2>/dev/null || true
pkill -9 -f "./MTCore" 2>/dev/null || true
sleep 2
echo "[*] Cleaning stale .NET mutex sentinels..."
rm -f /tmp/.dotnet/shm/global/MTProfile-* 2>/dev/null || true

start_core() {
  local slot="$1"; local license="$2"; local profile="$3"; local port="$4"; local exchange="$5"
  local clone_dir="$CLONES/$slot"
  local bin="$clone_dir/MTCore"
  local log="$LOGS/${slot}.log"
  local pid_file="$PIDS/${slot}.pid"

  if [ ! -x "$bin" ]; then
    echo "ERROR: MTCore not found at $bin (clone the distribution into $clone_dir first)" >&2
    return 1
  fi
  if [ ! -d "$clone_dir/$profile" ]; then
    echo "ERROR: profile dir $clone_dir/$profile not found" >&2
    return 1
  fi

  echo "[*] starting $slot ($exchange, license=$license, profile=$profile, port=$port)"
  echo "    clone=$clone_dir log=$log"

  # nohup + detached stdio so the process survives the parent shell exit.
  # cd into the clone so any process-relative resource lookups (Firebird
  # tmp files, MoonTrader.app resources) bind to this clone's footprint.
  (
    cd "$clone_dir" || exit 1
    nohup arch -x86_64 "./MTCore" \
      --license-id "$license" \
      --profile "$profile" \
      --core-data-dir "$clone_dir" \
      --address 127.0.0.1 \
      --port "$port" \
      --no-update \
      </dev/null >>"$log" 2>&1 &
    echo $! > "$pid_file"
  )
}

i=0
while [ $i -lt ${#SLOTS[@]} ]; do
  start_core "${SLOTS[$i]}" "${LICENSES[$i]}" "${PROFILES[$i]}" "${PORTS[$i]}" "${EXCHANGES[$i]}" || true
  i=$((i+1))
done

# Post-launch port verification with a 10s budget per the Supervisor spec.
echo ""
echo "[*] Waiting up to 10s for all 4 cores to bind UDP ports..."
bound_at=()
i=0
while [ $i -lt ${#SLOTS[@]} ]; do bound_at+=(0); i=$((i+1)); done

deadline_step=0
while [ $deadline_step -lt 10 ]; do
  all_bound=1
  i=0
  while [ $i -lt ${#PORTS[@]} ]; do
    if [ "${bound_at[$i]}" = "0" ]; then
      if lsof -nP -iUDP:"${PORTS[$i]}" 2>/dev/null | grep -q MTCore; then
        bound_at[$i]=$deadline_step
      else
        all_bound=0
      fi
    fi
    i=$((i+1))
  done
  if [ $all_bound -eq 1 ]; then break; fi
  sleep 1
  deadline_step=$((deadline_step+1))
done

# Post-launch health summary.
echo ""
echo "=== Post-launch health summary ==="
printf "%-10s %-6s %-12s %-7s %-9s %s\n" SLOT PORT EXCHANGE PID BOUND_AT CMD
printf "%-10s %-6s %-12s %-7s %-9s %s\n" ---- ---- -------- --- -------- ---
warnings=0
i=0
while [ $i -lt ${#SLOTS[@]} ]; do
  slot=${SLOTS[$i]}
  port=${PORTS[$i]}
  exch=${EXCHANGES[$i]}
  pid=$(lsof -nP -iUDP:"$port" 2>/dev/null | awk '/MTCore/ {print $2; exit}')
  if [ -n "$pid" ]; then
    cmd=$(ps -p "$pid" -o command= 2>/dev/null | cut -c 1-60)
    printf "%-10s %-6s %-12s %-7s %-9s %s\n" "$slot" "$port" "$exch" "$pid" "${bound_at[$i]}s" "$cmd"
  else
    printf "%-10s %-6s %-12s %-7s %-9s %s\n" "$slot" "$port" "$exch" "-" ">10s" "(NOT BOUND — check $LOGS/$slot.log)"
    warnings=$((warnings+1))
  fi
  i=$((i+1))
done

echo ""
if [ $warnings -gt 0 ]; then
  echo "⚠ WARNING: $warnings bench(es) did not bind within 10s.  Inspect the log files above."
  echo "  Common causes: DEFECT-11 / MTCORE-FREEZE on BYBIT/OKX; OKX IP-whitelist; HL REST RTT > 10s."
  exit 1
fi
echo "[+] All 4 benches bound."
echo "Logs: $LOGS/"
echo "Stop: $(dirname "$0")/stop_all_cores.sh"
