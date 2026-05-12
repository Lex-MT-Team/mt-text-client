#!/bin/bash
# mt-bench status check — portable to macOS bash 3.2 and zsh.
#
# Stage Supervisor P1 fix: the previous version used `declare -A` which
# silently fails under bash 3.2 (every row then printed the same final
# associative-array entry).  This version uses plain parallel arrays so
# all four benches show correct distinct data on any shell.
#
# Output columns: slot, expected port, expected exchange, expected license,
# UDP-bound status (UP/DOWN), live PID, command-line snippet (truncated).

set -u

# Slot definitions — parallel arrays keyed by index 0..3.
SLOTS=(bench_01 bench_02 bench_03 bench_04)
PORTS=(4242     4243     4244     4245)
EXCH=(BYBIT    BINANCE  HYPERLIQUID OKX)
LIC=(15557344036 15557413425 15557471470 15557490493)
PROF=(Tour_CORP_001 Lex_002 hl_002 Lex_okx_001)

printf "%-10s %-6s %-12s %-13s %-8s %-7s %s\n" SLOT PORT EXCHANGE LICENSE STATUS PID CMD
printf "%-10s %-6s %-12s %-13s %-8s %-7s %s\n" ---- ---- -------- ------- ------ --- ---

i=0
while [ $i -lt ${#SLOTS[@]} ]; do
  slot=${SLOTS[$i]}
  port=${PORTS[$i]}
  exch=${EXCH[$i]}
  lic=${LIC[$i]}
  prof=${PROF[$i]}

  # Find the PID bound on this UDP port, if any.  awk filters for MTCore
  # so we don't false-positive on some other process.
  pid=$(lsof -nP -iUDP:"$port" 2>/dev/null | awk '/MTCore/ {print $2; exit}')

  if [ -n "$pid" ]; then
    # Get the command line for that PID.  ps -p PID -o command= prints
    # only the command without the header.  Truncate to ~80 chars.
    cmd=$(ps -p "$pid" -o command= 2>/dev/null | cut -c 1-80)
    # Sanity: verify the running cmd actually matches the expected profile.
    if echo "$cmd" | grep -q -- "--profile $prof"; then
      status=UP
    else
      status=MISMATCH
    fi
    printf "%-10s %-6s %-12s %-13s %-8s %-7s %s\n" "$slot" "$port" "$exch" "$lic" "$status" "$pid" "$cmd"
  else
    printf "%-10s %-6s %-12s %-13s %-8s %-7s %s\n" "$slot" "$port" "$exch" "$lic" "DOWN" "-" "-"
  fi
  i=$((i+1))
done
