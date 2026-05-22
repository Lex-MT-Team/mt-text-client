# Orders + TPSL — the safe trading pattern

> **Non-obvious behaviour**: closing a position with `mt_orders_close` /
> `mt_orders_panic_sell` / `mt_orders_close_all` leaves **nothing in the
> reports DB**. Reports record completed *trades*, not raw position deltas.
> A trade is only completed when the close leg is routed through a TPSL
> pathway. This guide describes the correct order-management pattern so
> the reports family (`mt_reports_trades`, `mt_reports_query`,
> `mt_reports_csv_inline`, `mt_reports_export`) returns rows.

## TL;DR

```
1. Place order WITH TPSL inline   mt_orders_place tp_percent=… sl_percent=…  ← preferred
2. Subscribe to TPSL feed         mt_tpsl_subscribe         ← once per session
3. Close via TPSL pathway         mt_orders_close_by_tpsl   ← NOT mt_orders_close
```

The new-order request (`OrderRequestData`) carries `takeProfitSettings`
and `stopLossSettings` natively. Setting them at placement time is the
correct vendor pattern — MTCore tracks the trade through TPSL bookkeeping
from the first wire byte. `mt_orders_update_tpsl` is for **modifying** an
existing order's TPSL (moving the TP percent, switching trailing on),
not for attaching it after the fact.

Skip step 1 or 3, and the trade exists on the exchange but not in MTCore's
reports DB. Operators inspecting `mt_reports_trades` will see "no rows"
even though the position cycled through OPEN → CLOSED.

## Why this matters

MTCore's reports family reads from the local Firebird DB. The DB only
records trades that flow through the algorithm / TPSL bookkeeping path
— the same path that produces P&L attribution, signature grouping, and
performance metrics. A raw `mt_orders_close` triggers a market sell at
the venue but never marks the round-trip "complete" in the DB.

Visible symptoms when this rule is broken:

| Tool | Symptom |
|---|---|
| `mt_reports_trades` | "No report rows returned" (fallback message) |
| `mt_reports_query` | Same |
| `mt_reports_dates` | "No report dates" |
| `mt_account_executions` | "No recent executions" — even though the venue filled the close |
| `mt_tpsl_list` | "TPSL Positions (0)" — no TPSL was ever attached |

Visible symptoms when the rule is followed:

| Tool | Expected |
|---|---|
| `mt_reports_trades` | Returns the closed trade with entry/exit, P&L, fees |
| `mt_reports_query` | Same, with filters |
| `mt_reports_dates` | Date range covering the trade |
| `mt_account_executions` | Shows both legs (open + close) |
| `mt_tpsl_list` | Shows the active TPSL while position is open |

## Step-by-step

### Step 1 — Place the entry order **with TPSL inline**

```bash
mt_orders_place \
  symbol=BTCUSDT \
  side=BUY \
  qty=0.001 \
  price=77725.20 \
  type=LIMIT \
  market=FUTURES \
  position_side=BOTH \
  tp_percent=0.3 \
  tp_type=LIMIT \
  sl_percent=0.5 \
  sl_type=MARKET \
  confirm=true \
  profile=bench_01
```

The TPSL params populate `OrderRequestData.takeProfitSettings` and
`stopLossSettings` on the wire request itself. MTCore activates its
TPSL bookkeeping path from the first byte — there is no race between
placement and a separate update call.

Optional trailing stop:

```bash
mt_orders_place ... sl_percent=0.5 trailing_stop=true trailing_spread=0.2 ...
```

Notes:
- `position_side=BOTH` for ONE_WAY accounts; `LONG`/`SHORT` for HEDGE accounts.
  The `AccountInfo.PositionMode` field on some profiles may be stale — if
  the venue rejects with `position idx not match position mode`, override
  explicitly to `BOTH`.
- `mt_orders_place` returns "sent (response timed out)" on builds where
  the wire callback is broken (MTCore 0.7.23902 and similar). That is
  NOT a placement failure — verify by reading `mt_account_positions`
  ~2 seconds later. If the position appears, the order filled via
  WebSocket.

### When to use `mt_orders_update_tpsl` instead

`mt_orders_update_tpsl` is for **modifying** TPSL on an already-placed
order — e.g. moving the take-profit from +0.3% to +0.5% mid-trade, or
switching trailing on after entry. Do NOT use it as a substitute for
the inline params at placement; that creates a window where the order
is alive without TPSL, and on some builds the update wire call's ack
is unreliable.

### Step 2 — Subscribe to the TPSL feed (once per session)

```bash
mt_tpsl_subscribe profile=bench_01
mt_tpsl_list      profile=bench_01   # should show the TPSL attached at placement
```

`mt_tpsl_list` returns "No TPSL data. Use 'tpsl subscribe' first." until
you explicitly subscribe. The subscribe is a one-shot per profile per
process — no need to re-subscribe before every list.

### Step 3 — Close via the TPSL pathway

Two valid close paths exist, both go through MTCore's TPSL bookkeeping:

```bash
# Manual close (immediate, MARKET):
mt_orders_close_by_tpsl \
  symbol=BTCUSDT \
  market=FUTURES \
  side=BOTH \
  order_type=MARKET \
  confirm=true \
  profile=bench_01

# Or let the TPSL trigger fire naturally on TP / SL hit (no operator action needed).
```

`mt_tpsl_panic id=<tpsl_id> confirm=true` is the same pathway accessed
by TPSL ID instead of by symbol — useful when you have multiple
positions on the same symbol distinguished by their TPSL state.

### What NOT to do — these break reporting

| Tool | Effect on reports |
|---|---|
| `mt_orders_close` | Position closes at venue, **no Firebird record** |
| `mt_orders_close_all` | Same, for every open position |
| `mt_orders_panic_sell` | Same, even though it executes immediately |

These are still valid in emergencies (e.g., venue connectivity loss, panic exit)
but the operator must accept that the reports family will not show the trade.

## Verifying the pattern worked

After a complete round-trip done through TPSL:

```bash
mt_account_positions profile=bench_01      # expect: "No open positions"
mt_reports_trades    profile=bench_01      # expect: 1 row, with entry+exit
mt_reports_dates     profile=bench_01      # expect: date of the trade
```

If `mt_reports_trades` still returns the timeout-fallback message even
after a known TPSL close, the bench MTCore may have the
[`ReportRequestData` wire schema mismatch](#known-wire-issues) — the row
exists in Firebird but the request itself is rejected at deserialization.

## Known wire issues that compound this

These vendor-side gaps make the "without TPSL no reports" rule appear
to also apply WITH TPSL on some profiles. Mitigate by knowing the
combinations:

1. **`ReportRequestData` deserialization fails** on some MTCore builds
   (visible in MTCore logs as
   `NetworkData.LogDeserializeError ... Unable to read beyond the end of the stream`).
   The wire library shipped with the client is older than the build
   running in MTCore. Reports tools return the operator-friendly
   fallback message until the vendor DLL is refreshed.

2. **`BybitExchange.GetTickerPrice24: NotImplementedException`** —
   vendor did not implement the one-shot ticker24 call for BYBIT.
   Use `mt_marketdata_ticker_subscribe + mt_marketdata_ticker` (live
   feed) or `mt_exchange_pair_detail` (snapshot) for current price.

3. **`BybitExchange.GetPositionModeType: NotImplementedException`** —
   `mt_orders_get_position_mode` returns "Waiting..." indefinitely on
   BYBIT profiles. Read `AccountInfo.PositionMode` instead (note: this
   cached value can be stale).

4. **Market-wide ticker subscription delivers no data on freshly
   restarted BYBIT profiles**. Per-symbol subscriptions
   (`mt_marketdata_trades_subscribe BTCUSDT`) deliver fine. Subscribing
   trades for the symbols you trade BEFORE placing an order also
   primes MTCore's internal USDT-notional estimator — without it, some
   profiles emit
   `BybitExchange.PlaceOrder: Could not estimate order value in USDT`
   and silently reject orders.

## Tool reference

The tools mentioned in this guide:

| Tool | Purpose |
|---|---|
| [`mt_orders_place`](../README.md#orders) | Open an order. |
| `mt_orders_update_tpsl` | Attach TP/SL percentages to an existing order/position. |
| `mt_tpsl_subscribe` | Start the TPSL push feed for a profile. |
| `mt_tpsl_list` | Read the TPSL state cache (requires subscribe). |
| `mt_orders_close_by_tpsl` | Close a position via the TPSL bookkeeping pathway. |
| `mt_tpsl_panic` | Same as close_by_tpsl, addressed by TPSL ID. |
| `mt_orders_close` / `_close_all` / `_panic_sell` | **Bypass** TPSL bookkeeping. Reports will not record. |
