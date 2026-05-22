# Watchdog integration (discovery)

**Status:** Not yet implemented.  Schema placeholders are
registered (`mt_watchdog_status`, `mt_watchdog_token_update`) so the
workstream is grep-discoverable, but no client wiring exists yet and
there is no bench coverage.

This document captures the MTShared type inventory and the
WatchdogConnection.cs requirements so the next engineer can pick up
where reflection left off, without re-doing the discovery.

## MTShared type inventory (this build)

Reflected from `lib/MTShared.dll`.

### Network types — `MTShared.Network.Watchdog`

#### `WatchdogCommandRequestData`

The single request envelope sent over the watchdog UDP channel:

| Field            | Type                                            |
|------------------|-------------------------------------------------|
| `commandType`    | `WatchdogCommandType`                           |
| `coreInfo`       | `MTShared.Structs.WatchdogProfileCoreInfo`      |
| `pingStatus`     | `String`                                        |
| `errorType`      | `WatchdogErrorType`                             |
| `errorMessage`   | `String`                                        |
| `infoJson`       | `String`                                        |

#### `WatchdogCommandType` (Int32 enum)

| Value                                | Underlying |
|--------------------------------------|------------|
| `UNKNOWN`                            | `0`        |
| `ADD_CORE_AND_START_MONITORING`      | `1`        |
| `START_CORE_MONITORING`              | `2`        |
| `STOP_CORE_MONITORING`               | `3`        |
| `CORE_CREDENTIALS`                   | `4`        |
| `REFRESH_STATUS_INFO`                | `5`        |
| `REMOVE_CORE_AND_STOP_MONITORING`    | `6`        |

#### `WatchdogErrorType` (Byte enum)

| Value                                  | Underlying |
|----------------------------------------|------------|
| `UNKNOWN`                              | `0`        |
| `CONNECTION_FAIL`                      | `1`        |
| `GET_CREDENTIALS_FAIL`                 | `2`        |
| `START_MONITORING_FAIL`                | `3`        |
| `STOP_MONITORING_FAIL`                 | `4`        |
| `SHUTDOWN_IN_PROGRESS`                 | `5`        |
| `FAILED_LOADING_CORE_PROFILE_INFO`     | `6`        |
| `WATCHDOG_SESSION_ALREADY_STARTED`     | `7`        |
| `WATCHDOG_SESSION_NOT_RUNNING`         | `8`        |
| `UNKNOWN_ERROR`                        | `255`      |

#### `WatchdogStatusType` (Int32 enum)

| Value                | Underlying |
|----------------------|------------|
| `UNKNOWN`            | `0`        |
| `ACTIVE`             | `1`        |
| `MONITORING`         | `2`        |
| `MONITORING_DISABLED`| `3`        |
| `FAILED_TO_CHECK`    | `4`        |

### Struct types — `MTShared.Structs`

#### `WatchdogProfileCoreInfo`

One row per monitored MTCore.

| Field         | Type                  |
|---------------|-----------------------|
| `parentName`  | `String`              |
| `name`        | `String`              |
| `address`     | `String`              |
| `port`        | `Int32`               |
| `token`       | `String`              |
| `isMonitored` | `Boolean`             |
| `status`      | `WatchdogStatusType`  |

#### `WatchdogStatusInfo`

`: Dictionary<String, WatchdogProfileCoreInfo>` — the per-core map
returned by `REFRESH_STATUS_INFO`.  The dictionary key is the core's
profile name (matches `WatchdogProfileCoreInfo.name`).

#### `WatchdogProfileCoreProfiles`

`: List<WatchdogProfileCoreInfo>` — ordered list used inside
`WatchdogProfileSettings.CoreProfiles` JSON.

#### `WatchdogProfileOrderLifetimeParams`

| Field        | Type    |
|--------------|---------|
| `isEnabled`  | `Boolean` |
| `lifetime`   | `Int64`   |

#### `WatchdogProfileOrderLifetimeSettings`

`: Dictionary<String, WatchdogProfileOrderLifetimeParams>` — keyed by
core profile name.

#### `WatchdogProfilePositionsParams`

| Field        | Type    |
|--------------|---------|
| `isEnabled`  | `Boolean` |
| `timeperiod` | `Int64`   |

#### `WatchdogProfilePositionsSettings`

`: Dictionary<String, WatchdogProfilePositionsParams>` — keyed by core
profile name.

### Client-side wire surface — `MTShared.WatchdogUDPClient`

Separate UDP client from `MTShared.UDPClient` (the regular core
client).  Key methods:

```
public void Reconnect(string address, int port, string token, int connectionKeySeed);
public void Run();
public void Stop();
public void ProcessEventData();
public void ProcessOtherEventData();
public void SendWatchdogCommandRequest(WatchdogCommandRequestData, Action<…>);
public void SendClientToWatchdogDebugRequest(DebugRequestData, Action<…>);
```

Key callbacks (action fields on the client):

| Field                          | Signature                                     |
|--------------------------------|-----------------------------------------------|
| `onConnect`                    | `Action<…>`                                   |
| `onDisconnect`                 | `Action<…, …>`                                |
| `onReconnectStart`             | `Action<…, …, …>`                             |
| `onConnectionInfoResult`       | `Action<ConnectionInfoData>`                  |
| `onCoreRestartNotification`    | `Action`                                      |

State properties: `Address`, `Port`, `ConnectionKeySeed`,
`EventCount: (Int, Int)`, `IsConnected`, `ConnectionInfo`,
`OldConnectionInfo`, `HasConnectionId`.

### Profile + settings — `MTShared.WatchdogProfile`

Inherits from `BaseProfileRecord` (NOT `Profile` — the regular core
profile class).  Notable fields:

- `watchdogBindAddress` / `watchdogBindPort` — where the watchdog
  process listens.
- `watchdogClientToken` — auth token a client must present.
- `clientConnect2Address` / `clientConnect2Port` — where the client
  reaches the watchdog from.
- `Settings: WatchdogProfileSettings` — the typed settings bundle.

`WatchdogProfileSettings : CommonProfileSettings` defines these
documented keys (with `_KEY` constants and `_DEFAULT` constants on the
class):

- `LOG_LEVEL_KEY` → `LogLevel: MTLogLevel`
- `KEEP_LOGS_DAYS_KEY` → `KeepLogsDays: Int32`
- `CORE_PROFILES_KEY` → `CoreProfiles: String` (JSON of
  `WatchdogProfileCoreProfiles`)
- `CORE_PROFILES_ORDER_LIFETIME_KEY` → `OrderLifetime: String` (JSON of
  `WatchdogProfileOrderLifetimeSettings`)
- `CORE_PROFILES_POSITIONS_KEY` → `Positions: String` (JSON of
  `WatchdogProfilePositionsSettings`)

Additional read-only / write-through properties: `CorePauseAlertNotify`,
`CoreMaxThreadsPerCpuCore`, `CoreAllowManualOrdersWhenOverloaded`,
`SmartAutoStartAlgos`, `CoreIgnoreStopCpuSlowdown`,
`CoreMaxCpuSlowdownSpikes`, `CoreMaxRamUsage`,
`CoreMaxEmergencyRestartAttempts`, `CoreStopRamFree`,
`CoreStopDriveFreeLogs`.

### Other related types

- `MTShared.WatchdogClientMessageProcessor` — internal UDP message
  decoder.  Not directly used by clients.
- `MTShared.Managers.WatchDogProfileUpdater` (note the capitalisation
  drift: `WatchDog` here, `Watchdog` elsewhere) — handles profile
  schema migrations between watchdog format versions.

## What a `Core/WatchdogConnection.cs` would need

To bring the placeholder tools to life, a new client class analogous to
`Core/CoreConnection.cs` but wrapping `WatchdogUDPClient` instead of
`UDPClient`:

1. **Construction:** take a `WatchdogProfile` (or the address / port /
   token triple it carries) and instantiate `WatchdogUDPClient`.
2. **Lifecycle:** `Start()`/`Stop()` mapping to
   `WatchdogUDPClient.Run/Stop`.  Periodic
   `ProcessEventData()` / `ProcessOtherEventData()` calls from a worker
   thread (same pattern `CoreConnection` uses).
3. **Status cache:** subscribe to the `REFRESH_STATUS_INFO` response
   stream and maintain a thread-safe `WatchdogStatusInfo` snapshot.
   Backs `mt_watchdog_status`.
4. **Command send wrappers** — one method per useful
   `WatchdogCommandType`:
   * `SendStatusRefresh()` → `REFRESH_STATUS_INFO`
   * `SendStartMonitoring(coreInfo)` → `START_CORE_MONITORING`
   * `SendStopMonitoring(coreInfo)` → `STOP_CORE_MONITORING`
   * `SendAddCore(coreInfo)` → `ADD_CORE_AND_START_MONITORING`
   * `SendRemoveCore(coreInfo)` → `REMOVE_CORE_AND_STOP_MONITORING`
   * `SendUpdateCredentials(coreInfo)` → `CORE_CREDENTIALS`
   Each constructs a `WatchdogCommandRequestData`, calls
   `_watchdogClient.SendWatchdogCommandRequest`, and waits for the
   callback via the existing `RequestExecutor` / `SendAndWait` pattern.
5. **Token rotation:** rotating
   `WatchdogProfile.watchdogClientToken` requires reconnecting via
   `WatchdogUDPClient.Reconnect(address, port, newToken, seed)`.
   Backs `mt_watchdog_token_update`.  Note: this severs active
   monitoring until every other client re-authenticates — that's why
   the placeholder is `confirm`-gated.

A `ConnectionManager.ResolveWatchdog(profile)` lookup mirrors the
existing `Resolve(profile)` for cores.

## Bench requirement

The standard bench cluster runs **only the MTCore process**, not the
watchdog.  A real bench for this workstream needs:

- A separate watchdog process listening on its own UDP port (not
  colliding with any MTCore port already in use).  Run the watchdog
  assembly shipped alongside the MTCore binary directly with
  `--watchdog` mode.
- Its own auth token configured in a `WatchdogProfile`.  The token is
  what `mt_watchdog_token_update` would rotate.
- The watchdog must already be configured to monitor a real MTCore (one
  row in `WatchdogStatusInfo` per monitored core).

## Placeholder contract (regression harness)

`tests/MTTextClient.Tests/Static/WatchdogPlaceholderStaticTests.cs`
pins the contract:

1. Both `mt_watchdog_status` and `mt_watchdog_token_update` are
   registered.
2. Each description contains the `status: placeholder` marker.
3. Each description points at this doc
   (`docs/watchdog-integration.md`).

When the real implementation lands, the marker is removed in the SAME
commit that ships the handler + LiveTrade — that's the trigger to
update the Static test (either delete it or repurpose it as a real
behaviour check).

`mt_watchdog_token_update` is registered with `confirm` in
`inputSchema.required`, and is listed in
`ConfirmGateStaticTests.ConfirmRequiredTools` /
`ConfirmGateUnitTests`'s destructive-tools table.  The gate fires
before any dispatch even reaches the (currently-missing) handler.
