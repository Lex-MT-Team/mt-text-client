# AutoBuy Parity Audit

The MCP tool surface for AutoBuy (DCA / recurring buy) operations must
cover every action the vendor's MTShared library exposes via
`AutoBuyRequestData.RequestActionType`.  This document is the audit
record, and `AutoBuyParityTests.cs` is the regression
harness that fails if MTShared adds a new action without a
corresponding MCP tool.

## Reflection snapshot

Reflected from `lib/MTShared.dll` on this build:

### `MTShared.Network.AutoBuyRequestData.RequestActionType`

| Value                   | Underlying | Meaning                                       |
|-------------------------|------------|-----------------------------------------------|
| `UNKNOWN`               | `0`        | Sentinel; not a real request.                 |
| `SUBSCRIBE`             | `1`        | Begin streaming AutoBuy events for a profile. |
| `SAVE`                  | `2`        | Create or update an AutoBuy configuration.    |
| `DELETE`                | `3`        | Delete one or more AutoBuy configurations.    |
| `START`                 | `4`        | Start (resume) configured AutoBuys.           |
| `STOP`                  | `5`        | Stop (pause) configured AutoBuys.             |
| `REFRESH_ASSET_PAIRS`   | `6`        | Re-pull the per-exchange asset-pair lists.    |

### `MTShared.Network.AutoBuyResultData.ResultActionType`

Result envelopes that may arrive on the AutoBuy subscription stream:
`UNKNOWN`, `SUBSCRIBED`, `ADDED`, `UPDATED`, `DELETED`,
`ASSET_PAIRS_REFRESHED`.  Captured by `AutoBuyStore` in the client
and exposed read-only via `mt_autobuy_list`.

## MCP tool surface

| MCP tool name              | Client subcommand    | MTShared wire path                                                                                |
|----------------------------|----------------------|---------------------------------------------------------------------------------------------------|
| `mt_autobuy_list`          | `autobuy list`       | Reads `CoreConnection.AutoBuyStore` (local).                                                      |
| `mt_autobuy_subscribe`     | `autobuy subscribe`  | `_udpClient.SendAutoBuySubscribe(...)` — independent RPC, not `AutoBuyRequestData`.               |
| `mt_autobuy_unsubscribe`   | `autobuy unsubscribe`| `_udpClient.SendAutoBuyUnsubscribe(...)` — independent RPC.                                       |
| `mt_autobuy_save`          | `autobuy save`       | `SendAutoBuyRequest("SAVE", json)` → `AutoBuyRequestData.RequestActionType.SAVE`.                 |
| `mt_autobuy_delete`        | `autobuy delete`     | `SendAutoBuyRequest("DELETE", json)` → `RequestActionType.DELETE`.                                |
| `mt_autobuy_start`         | `autobuy start`      | `SendAutoBuyRequest("START", json)` → `RequestActionType.START`.                                  |
| `mt_autobuy_stop`          | `autobuy stop`       | `SendAutoBuyRequest("STOP", json)` → `RequestActionType.STOP`.                                    |
| `mt_autobuy_refresh_pairs` | `autobuy refresh-pairs` | `SendAutoBuyRequest("REFRESH_ASSET_PAIRS", json)` → `RequestActionType.REFRESH_ASSET_PAIRS`.   |

## Parity

| Vendor action          | Client coverage                         |
|------------------------|-----------------------------------------|
| `UNKNOWN`              | (sentinel — intentionally not wired)    |
| `SUBSCRIBE`            | `mt_autobuy_subscribe` via `SendAutoBuySubscribe` (separate RPC, same intent) |
| `SAVE`                 | `mt_autobuy_save`                       |
| `DELETE`               | `mt_autobuy_delete`                     |
| `START`                | `mt_autobuy_start`                      |
| `STOP`                 | `mt_autobuy_stop`                       |
| `REFRESH_ASSET_PAIRS`  | `mt_autobuy_refresh_pairs`              |

Every non-sentinel vendor action has client and MCP coverage.

### Honest gaps

1. **Confirm gating is NOT applied** to `mt_autobuy_save`,
   `mt_autobuy_delete`, `mt_autobuy_start`, `mt_autobuy_stop`, or
   `mt_autobuy_refresh_pairs`.  These mutate per-profile DCA
   configuration on the running MTCore.  An automation-driven caller can
   call them without an explicit destructive-intent acknowledgement.
   This is intentional for the audit phase — the mandate here is
   *documentation*, not new code — but is recorded as a follow-up.
2. The `data` argument on save/delete/start/stop is a JSON string
   passed through verbatim.  There is no client-side schema
   validation; malformed JSON produces an MTCore-side rejection (the
   exact wording depends on which `AutoBuyRequest*Data` subclass the
   action expects: `AutoBuyRequestSaveData.autoBuys`,
   `AutoBuyRequestDeleteData.autoBuyIds`, …).  Callers using these
   tools should consult the MTShared types directly.
3. AutoBuy events are NOT delivered through `notification_push`.  They
   flow on their own subscription wired in
   `CoreConnection.SubscribeAutoBuy`.

## Regression harness

`AutoBuyParityTests` (Static, in-process) loads the same
`MTShared.dll` and enumerates `RequestActionType`.  It asserts:

1. Every action other than `UNKNOWN` has a corresponding MCP tool
   listed in this audit.
2. Every `mt_autobuy_*` tool currently in the registry maps to either
   (a) a known MTShared action, (b) the local `mt_autobuy_list`
   reader, or (c) one of the standalone subscribe RPCs.

If MTShared adds a new enum value, (1) fails until a tool is added.
If the client adds a new tool, (2) fails until the audit is updated.

## Follow-up tracker

- [ ] Add `confirm=true` requirement to `mt_autobuy_save`,
      `mt_autobuy_delete`, `mt_autobuy_start`, `mt_autobuy_stop`,
      `mt_autobuy_refresh_pairs` — these are destructive in the
      ConfirmGate sense.
- [ ] Type the `data` argument: explicit fields per action instead
      of pass-through JSON.
- [ ] Hook AutoBuy notifications into the unified notification surface
      for consistent observability.

These are out of scope for the audit but are the natural
next-iteration follow-ups.
