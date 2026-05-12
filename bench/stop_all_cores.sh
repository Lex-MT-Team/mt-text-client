#!/bin/bash
# mt-bench stop_all_cores — graceful TERM, grace period, then SIGKILL stragglers.
# Reads PIDs from ~/mt-bench/pids/bench_XX.pid (written by start_all_cores.sh).
# Cleans the .NET mutex sentinels so a future start_all_cores can rebind.
# Verifies UDP ports are unbound at exit.
#
# Stage Supervisor P1 fix: the previous version read PIDs from core_XX.pid
# (stale slot naming) which didn't match the start script's bench_XX.pid
# files, so it silently skipped every PID and fell through to lsof-port
# scanning only.  Slot names are now consistent across all three scripts.

set -u

BASE="$HOME/mt-bench"
PIDS="$BASE/pids"

SLOTS=(bench_01 bench_02 bench_03 bench_04)
PORTS=(4242 4243 4244 4245)

# Phase 1 — graceful SIGTERM via recorded PID files.
echo "[*] Sending SIGTERM via PID files..."
stopped=0
i=0
while [ $i -lt ${#SLOTS[@]} ]; do
  slot=${SLOTS[$i]}
  pid_file="$PIDS/${slot}.pid"
  if [ -f "$pid_file" ]; then
    pid=$(cat "$pid_file" 2>/dev/null || true)
    if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
      echo "    $slot (PID $pid): SIGTERM"
      kill "$pid" 2>/dev/null || true
      stopped=$((stopped+1))
    fi
  fi
  i=$((i+1))
done

# Grace period.
sleep 3

# Phase 2 — SIGKILL anything still bound to a bench port (covers PID-file gaps).
echo "[*] Force-killing anything still bound to bench ports..."
i=0
while [ $i -lt ${#PORTS[@]} ]; do
  port=${PORTS[$i]}
  pid=$(lsof -nP -iUDP:"$port" 2>/dev/null | awk '/MTCore/ {print $2; exit}')
  if [ -n "$pid" ]; then
    echo "    port $port → PID $pid: SIGKILL"
    kill -9 "$pid" 2>/dev/null || true
  fi
  i=$((i+1))
done

# Phase 3 — clean .NET mutex sentinels (DEFECT-12).
echo "[*] Cleaning .NET mutex sentinels..."
rm -f /tmp/.dotnet/shm/global/MTProfile-* 2>/dev/null || true

# Phase 4 — remove PID files so the next start writes fresh ones.
rm -f "$PIDS"/bench_*.pid 2>/dev/null || true

# Final verification.
echo ""
echo "=== Final state ==="
all_clear=1
i=0
while [ $i -lt ${#SLOTS[@]} ]; do
  slot=${SLOTS[$i]}
  port=${PORTS[$i]}
  if lsof -nP -iUDP:"$port" 2>/dev/null | grep -q MTCore; then
    echo "  $slot (port $port): STILL UP ⚠"
    all_clear=0
  else
    echo "  $slot (port $port): stopped"
  fi
  i=$((i+1))
done

if [ $all_clear -eq 1 ]; then
  echo ""
  echo "[+] All benches stopped, sentinels cleaned, PID files removed."
  exit 0
else
  echo ""
  echo "⚠ Some MTCore processes still bound; investigate manually."
  exit 1
fi
