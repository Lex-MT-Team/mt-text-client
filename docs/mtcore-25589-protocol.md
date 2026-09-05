# MTCore 0.7.25589 — algorithm and trading-performance protocol

Reference for the vendor break between MTShared `0.7.24637` and `0.7.25589`, and
for the shape of the trading-performance feed the client now exposes. Everything
below was read off `lib/MTShared.dll` and the matching `MTCore.dll` from the
vendor distribution (`MTCore-version_725589.tar.xz`), not inferred from behaviour.

---

## 1. Algorithms: one request type per verb

`AlgorithmData.actionType` is gone. A request is now a subclass of
`AlgorithmRequestData`, and the base class serializes `exchangeType` plus a
`RequestType` string it takes from `GetType().Name` — that string is what the
core dispatches on. All of them travel over `ALGORITHMS_REQUEST` via
`UDPClient.SendAlgorithmRequest(AlgorithmRequestData, NetworkMessagePriority)`;
`SendAlgorithmListRequest` no longer exists.

| Verb (client `AlgoActionType`) | Request type | Payload |
|---|---|---|
| `START` | `AlgorithmRunRequestData` | `algorithmID` |
| `STOP` | `AlgorithmStopRequestData` | `algorithmID` |
| `START_ALL` | `AlgorithmsRunAllRequestData` | — |
| `STOP_ALL` | `AlgorithmsStopAllRequestData` | — |
| `SAVE` / `SAVE_START` (new algo, `id <= 0`) | `AlgorithmAddRequestData` | `algorithm`, `runAlgorithm` |
| `SAVE` / `SAVE_START` (existing algo) | `AlgorithmUpdateRequestData` | `algorithm`, `runAlgorithm` |
| `DELETE` | `AlgorithmRemoveRequestData` | `algorithmID` |
| `TOGGLE_DEBUG` | `AlgorithmToggleDebagRequestData` | `algorithmID` |
| `SAVE_GROUP` (new folder) | `AlgorithmFolderAddRequestData` | `folder` |
| `SAVE_GROUP` (existing folder) | `AlgorithmFolderUpdateRequestData` | `folder` |
| `CLONE_GROUP` | `AlgorithmFolderCloneRequestData` | `folderID` |
| `DELETE_GROUP` | `AlgorithmFolderRemoveRequestData` | `folderID` |

The core exposes one handler per type on `MTCore.Algorithms.AlgorithmManager`
(`RunAlgorithm`, `StopAlgorithm`, `AddAlgorithm`, `UpdateAlgorithm`,
`RemoveAlgorithm`, `AddFolder`, `CloneFolder`, `RemoveFolder`, …), so the mapping
is one-to-one rather than a guess. The vendor's own `BotClient` references the
same set. Replies still arrive on the notification channel as
`AlgorithmUpdateNotificationData` (single) / `AlgorithmListUpdateNotificationData`
(folder and list operations).

Bulk variants exist and are not wired up here yet: `AlgorithmList{Add,Update,
Remove,Run,Stop}RequestData` (by id list), `AlgorithmFolderList{Add,Update,
Remove}RequestData`, `AlgorithmFolder{Run,Stop}RequestData`,
`AlgorithmQuickRun{,List}RequestData`, `AlgorithmPasteRequestData`.

### 1.1 Inbound: everything is an event now

`NetworkMessageType.ALGORITHM_LIST_RESULT` no longer exists. Algorithm drops
arrive as `ALGORITHMS_RESULT` carrying an `AlgorithmEventData` subtype, chosen by
its `EventType` discriminator (`AlgorithmEventData.DeserializeEvent`):

| Event type | Payload |
|---|---|
| `AlgorithmListEventData` | `Data : AlgorithmListData` — the full snapshot (`isConfigList` marks the per-type default templates) |
| `AlgorithmsAddedEventData` / `AlgorithmsUpdatedEventData` | `Algorithms : List<AlgorithmData>` |
| `AlgorithmsRemovedEventData` | `Algorithms : List<AlgorithmData>` |
| `AlgorithmFolders{Added,Updated,Removed}EventData` | `Folders : List<AlgorithmGroupData>` |

`ALGORITHM_STATUS_DATA` (`AlgorithmStatusData`) and `ALGORITHM_SYMBOL_STATUS_DATA`
still arrive on their own message types. `AlgorithmStore` therefore dispatches on
the **payload type**, not the message type.

`AlgorithmData` itself is unchanged apart from losing `actionType` and gaining
`isProfilingOn`; `AlgorithmGroupData` lost `actionType` too, so add/update/remove
now comes from the event rather than from a field on the record.

**Why a stale client sees zero algorithms:** the numeric `NetworkMessageType`
values shifted with the member changes, so a client built against 24637 decodes
25589 algorithm traffic under the wrong case and drops it. Every core build needs
a client rebuilt against the matching `MTShared.dll`.

### 1.2 START instantiates from the *stored* name

`AlgorithmManager.RunAlgorithm` looks the config up by id and calls
`StartAlgorithm(config.name, config.args, config.id, …)`. `StartAlgorithm`
switches on the exact type name — `"Shot"`, `"Shots Group"`, `"Shot Detect"`,
`"Shot Detect Group"`, `"Depth Shot"`, `"Depth Shots Group"`, `"Averages"`,
`"Averages Group"`, `"Vector"`, `"Vector Group"`, `"Markets Watcher"`,
`"Signal"`, `"Markets Saver"` — and falls through to `null` for anything else,
**without raising an error**. `RunAlgorithm` still returns `OK`, so the client
sees a successful start and nothing runs.

Until 24637 the client hid this by sending a signature-derived `name` with every
START. `AlgorithmRunRequestData` carries only the id, so that hook is gone: an
algorithm whose stored `name` is a display label (rather than the type name, with
the label in `description`) cannot be started at all until the stored record is
repaired. `algos start` checks the stored name against the factory's vocabulary
and refuses up front rather than reporting a start that did not happen;
`algos rename <id> <label>` performs the repair (it writes `name = <type name>`
and keeps the label in `description`).

### 1.3 Config schema version

`MTShared.Utils.AlgorithmValidator.VERSION` is the current config-schema version
(15 in both 24637 and 25589). `algos save` stamps it on the saved record; a
literal that falls behind downgrades the stored config and invites the core to
re-run its legacy `Parse_2_*` / `Parse_3_*` parameter migrations.

---

## 2. Trading performance

`TradingPerformanceData` is gone. The feed is now a snapshot/delta list.

```
TradingPerformanceListData          (NetworkMessageType.TRADING_PERFORMANCE_RESULT)
├── isSnapshot     : bool                              — true = replaces the client's view
├── metricChanges  : List<TradingPerformanceMetricData> — upserts, keyed by `key`
└── deletedKeys    : List<TradingPerformanceKey>        — keys to drop

TradingPerformanceMetricData
├── key       : TradingPerformanceKey { marketType : byte, symbol : string, algorithmId : long }
├── startTime : long   — epoch ms the window is measured from
├── comment   : string
└── metrics   : TradingPerformanceMetrics[]  — one per timeframe

TradingPerformanceMetrics (struct)
├── total        : double   — net result
├── priceDelta   : float
├── profitFactor : float    — sentinels: PROFIT_FACTOR_NO_LOSSES, PROFIT_FACTOR_NO_DATA
├── profitTotal  : double
└── lossTotal    : double
```

**Indexing `metrics` is not a cast.** `TradingPerformanceTimeFrame` values are
millisecond magnitudes (`M1 = 60000` … `D30 = 2592000000`, `ALL_TIME` and
`BASE_LINE` at the `long` extremes), and the array is indexed by *position* in
`TradingPerformanceTimeFrames.AllValues`. Use
`TradingPerformanceTimeFrames.GetIndex(tf)` (or the metric's own
`GetMetrics(tf)`), and bound-check: the array length comes off the wire, so a
peer built against a different timeframe set may send fewer entries.

Key groups (`TradingPerformanceKeyGroup`: `ALL_MARKETS_ALL_SYMBOLS`,
`ALL_SYMBOLS_PER_MARKET`, `ALGORITHM_TOTAL`, `ALGORITHM_PER_SYMBOL`,
`GLOBAL_PER_SYMBOL`, the `MANUAL_*` and `LIQUIDATION_*` pairs) are a property of
the key shape — the wire record does not name its group.

### 2.1 Subscribing and requesting

* `SendTradingPerformanceSubscribe(exchangeType, marketType, callback, requestID)`
  → `TradingPerformanceSubscribeData { exchangeType, marketType }`.
* `SendTradingPerformanceRequest(TradingPerformanceRequestData { exchangeType,
  actionType : REFRESH | RESET, newStartTime, keys })`. `RESET` uses `keys` and
  `newStartTime` to restart the window for specific keys; `REFRESH` republishes.

`MTCore.Data.TradingPerformanceDataSource` publishes from a worker queue: it
pushes when a metric actually changes, and sends an (empty) snapshot to a new
subscriber only when the data source is unavailable. **An idle core therefore
sends nothing at all on subscribe** — an empty `perf list` is not by itself
evidence of a broken subscription. The metrics are what the per-algorithm
performance breakers (`autoStopFilterList`) act on, so a core with no enabled
breakers and no closing trades has nothing to report.

Client surface: `perf subscribe [market]`, `perf list`, `perf request
[refresh|reset]`, `perf unsubscribe` (MCP: `mt_perf_subscribe`, `mt_perf_list`,
`mt_perf_request`, `mt_perf_unsubscribe`). `perf list` prints one line per
non-empty timeframe per key. `TradingPerformanceStoreUnitTests` pins the parse —
including the timeframe indexing — through a vendor serializer round-trip.
