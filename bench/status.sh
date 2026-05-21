#!/bin/bash
# status — portable bench health check (bash 3.2 and zsh compatible).
#
# Output columns: slot, expected port, expected exchange, expected profile,
# UDP-bound status (UP / MISMATCH / DOWN), live PID, command-line snippet.

set -u

BENCH_ROOT="${BENCH_ROOT:-$HOME/mt-bench}"
CONF="$BENCH_ROOT/bench.conf"

if [ -f "$CONF" ]; then
  # shellcheck source=/dev/null
  source "$CONF"
fi

SLOTS=(${BENCH_SLOTS:-bench_01 bench_02 bench_03 bench_04})

resolve() {
  local slot_upper key var
  slot_upper=$(echo "$1" | tr '[:lower:]' '[:upper:]')
  key="$2"
  var="BENCH_${slot_upper}_${key}"
  echo "${!var:-}"
}

printf "%-10s %-6s %-12s %-18s %-8s %-7s %s\n" SLOT PORT EXCHANGE PROFILE STATUS PID CMD
printf "%-10s %-6s %-12s %-18s %-8s %-7s %s\n" ---- ---- -------- ------- ------ --- ---

for slot in "${SLOTS[@]}"; do
  port=$(resolve "$slot" PORT)
  exch=$(resolve "$slot" EXCHANGE)
  prof=$(resolve "$slot" PROFILE)
  if [ -z "$port" ]; then
    printf "%-10s %-6s %-12s %-18s %-8s %-7s %s\n" "$slot" "-" "${exch:--}" "${prof:--}" "UNCONFIG" "-" "-"
    continue
  fi
  pid=$(lsof -nP -iUDP:"$port" 2>/dev/null | awk '/MTCore/ {print $2; exit}')
  if [ -n "$pid" ]; then
    cmd=$(ps -p "$pid" -o command= 2>/dev/null | cut -c 1-80)
    if echo "$cmd" | grep -q -- "--profile $prof"; then
      status=UP
    else
      status=MISMATCH
    fi
    printf "%-10s %-6s %-12s %-18s %-8s %-7s %s\n" "$slot" "$port" "$exch" "$prof" "$status" "$pid" "$cmd"
  else
    printf "%-10s %-6s %-12s %-18s %-8s %-7s %s\n" "$slot" "$port" "$exch" "$prof" "DOWN" "-" "-"
  fi
done
