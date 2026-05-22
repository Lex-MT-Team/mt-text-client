#!/bin/bash
# stop_all_cores — graceful TERM, grace period, then SIGKILL stragglers.
# Reads PIDs from $BENCH_ROOT/pids/<slot>.pid (written by start_all_cores.sh).
# Cleans .NET mutex sentinels so a future start_all_cores can rebind.
# Verifies UDP ports are unbound at exit.

set -u

BENCH_ROOT="${BENCH_ROOT:-$HOME/mt-bench}"
PIDS="$BENCH_ROOT/pids"
CONF="$BENCH_ROOT/bench.conf"

if [ -f "$CONF" ]; then
  # shellcheck source=/dev/null
  source "$CONF"
fi

SLOTS=(${BENCH_SLOTS:-bench_01 bench_02 bench_03 bench_04})

resolve_port() {
  local slot_upper var
  slot_upper=$(echo "$1" | tr '[:lower:]' '[:upper:]')
  var="BENCH_${slot_upper}_PORT"
  echo "${!var:-}"
}

PORTS=()
for slot in "${SLOTS[@]}"; do
  PORTS+=("$(resolve_port "$slot")")
done

# Phase 1 — graceful SIGTERM via recorded PID files.
echo "[*] Sending SIGTERM via PID files..."
for slot in "${SLOTS[@]}"; do
  pid_file="$PIDS/${slot}.pid"
  if [ -f "$pid_file" ]; then
    pid=$(cat "$pid_file" 2>/dev/null || true)
    if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
      echo "    $slot (PID $pid): SIGTERM"
      kill "$pid" 2>/dev/null || true
    fi
  fi
done

# Grace period.
sleep 3

# Phase 2 — SIGKILL anything still bound to a bench port (covers PID-file gaps).
echo "[*] Force-killing anything still bound to bench ports..."
for port in "${PORTS[@]}"; do
  [ -z "$port" ] && continue
  pid=$(lsof -nP -iUDP:"$port" 2>/dev/null | awk '/MTCore/ {print $2; exit}')
  if [ -n "$pid" ]; then
    echo "    port $port → PID $pid: SIGKILL"
    kill -9 "$pid" 2>/dev/null || true
  fi
done

# Phase 3 — clean .NET mutex sentinels left behind by SIGKILLed processes.
echo "[*] Cleaning .NET mutex sentinels..."
rm -f /tmp/.dotnet/shm/global/MTProfile-* 2>/dev/null || true

# Phase 4 — remove PID files so the next start writes fresh ones.
rm -f "$PIDS"/bench_*.pid 2>/dev/null || true

# Final verification.
echo ""
echo "=== Final state ==="
all_clear=1
for i in "${!SLOTS[@]}"; do
  slot=${SLOTS[$i]}
  port=${PORTS[$i]}
  [ -z "$port" ] && { echo "  $slot: no port configured (skipped)"; continue; }
  if lsof -nP -iUDP:"$port" 2>/dev/null | grep -q MTCore; then
    echo "  $slot (port $port): STILL UP"
    all_clear=0
  else
    echo "  $slot (port $port): stopped"
  fi
done

if [ $all_clear -eq 1 ]; then
  echo ""
  echo "[+] All benches stopped, sentinels cleaned, PID files removed."
  exit 0
else
  echo ""
  echo "Some MTCore processes still bound; investigate manually."
  exit 1
fi
