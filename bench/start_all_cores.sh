#!/bin/bash
# start_all_cores — durable per-bench launch with PID capture and post-launch
# port-bind verification.
#
# Uses `nohup` + redirected stdio so each MTCore detaches cleanly and persists
# past parent shell exit (necessary when invoked from a non-interactive shell
# such as a cron job or test harness).
#
# Per-bench layout (configurable per user via $BENCH_ROOT, default $HOME/mt-bench):
#   $BENCH_ROOT/cores-cloned/<slot>/      per-bench MTCore clone
#   $BENCH_ROOT/logs/<slot>.log           per-bench stdout/stderr
#   $BENCH_ROOT/pids/<slot>.pid           per-bench PID file
#
# Cloned distributions provide filesystem isolation (separate Firebird
# Embedded lock files, fbembed5 dylib symlink, MoonTrader.app resources),
# which makes individual bench restarts cleaner. Clone isolation does NOT
# prevent vendor-side MTCore wedges; if a bench stops responding, kill it
# and restart.
#
# Configuration:
#   Source $BENCH_ROOT/bench.conf (if present) to populate per-slot variables,
#   or export them in the environment before running this script. For each
#   slot bench_NN the following are required:
#     BENCH_NN_LICENSE   — MTCore license id
#     BENCH_NN_PROFILE   — MTCore profile directory name (under the clone)
#     BENCH_NN_PORT      — UDP port to bind
#     BENCH_NN_EXCHANGE  — Exchange label (BYBIT / BINANCE / OKX / HYPERLIQUID)
#   BENCH_SLOTS — space-separated slot list (default: "bench_01 bench_02 bench_03 bench_04")
#   BENCH_ROOT  — base directory (default: $HOME/mt-bench)

set -uo pipefail

BENCH_ROOT="${BENCH_ROOT:-$HOME/mt-bench}"
CLONES="$BENCH_ROOT/cores-cloned"
LOGS="$BENCH_ROOT/logs"
PIDS="$BENCH_ROOT/pids"
CONF="$BENCH_ROOT/bench.conf"

# Optional: source a per-machine configuration file.
if [ -f "$CONF" ]; then
  # shellcheck source=/dev/null
  source "$CONF"
fi

SLOTS=(${BENCH_SLOTS:-bench_01 bench_02 bench_03 bench_04})

mkdir -p "$LOGS" "$PIDS"

# Resolve per-slot config from BENCH_<UPPER_SLOT>_<KEY> env variables.
resolve() {
  local slot_upper key var
  slot_upper=$(echo "$1" | tr '[:lower:]' '[:upper:]')
  key="$2"
  var="BENCH_${slot_upper}_${key}"
  echo "${!var:-}"
}

LICENSES=()
PROFILES=()
PORTS=()
EXCHANGES=()
for slot in "${SLOTS[@]}"; do
  LICENSES+=("$(resolve "$slot" LICENSE)")
  PROFILES+=("$(resolve "$slot" PROFILE)")
  PORTS+=("$(resolve "$slot" PORT)")
  EXCHANGES+=("$(resolve "$slot" EXCHANGE)")
done

missing=0
for i in "${!SLOTS[@]}"; do
  slot=${SLOTS[$i]}
  if [ -z "${LICENSES[$i]}" ] || [ -z "${PROFILES[$i]}" ] || [ -z "${PORTS[$i]}" ] || [ -z "${EXCHANGES[$i]}" ]; then
    echo "ERROR: $slot is missing one of BENCH_${slot^^}_{LICENSE,PROFILE,PORT,EXCHANGE}." >&2
    missing=1
  fi
done
if [ $missing -ne 0 ]; then
  echo "Define the missing variables in $CONF or export them before running." >&2
  exit 1
fi

# Stop any leftover MTCore + clean .NET mutex sentinels.
# When MTCore is SIGKILLed the runtime leaves behind a sentinel at
# /tmp/.dotnet/shm/global/MTProfile-<sha1(profile)> with the dead PID in
# the first 16 bytes. The next start aborts with "Global profile mutex can
# not be created - MTCore with this profile already running" until the
# sentinel is removed.
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

  echo "[*] starting $slot ($exchange, profile=$profile, port=$port)"
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

for i in "${!SLOTS[@]}"; do
  start_core "${SLOTS[$i]}" "${LICENSES[$i]}" "${PROFILES[$i]}" "${PORTS[$i]}" "${EXCHANGES[$i]}" || true
done

# Post-launch port verification with a 10s budget.
echo ""
echo "[*] Waiting up to 10s for cores to bind UDP ports..."
bound_at=()
for _ in "${SLOTS[@]}"; do bound_at+=(0); done

deadline_step=0
while [ $deadline_step -lt 10 ]; do
  all_bound=1
  for i in "${!PORTS[@]}"; do
    if [ "${bound_at[$i]}" = "0" ]; then
      if lsof -nP -iUDP:"${PORTS[$i]}" 2>/dev/null | grep -q MTCore; then
        bound_at[$i]=$deadline_step
      else
        all_bound=0
      fi
    fi
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
for i in "${!SLOTS[@]}"; do
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
done

echo ""
if [ $warnings -gt 0 ]; then
  echo "WARNING: $warnings bench(es) did not bind within 10s. Inspect the log files above."
  exit 1
fi
echo "[+] All benches bound."
echo "Logs: $LOGS/"
echo "Stop: $(dirname "$0")/stop_all_cores.sh"
