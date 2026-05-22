# RequestExecutor policy

The MTCore UDP request/response cycle has a recurring failure mode: a
cold or empty Firebird database, an unwarmed exchange-info cache, or a
rate-limit pushback responds with silence rather than an explicit empty
result.  At the wire layer `SendAndWait` faithfully returns
`T?` = `null` when the timeout expires.  At the command layer that
`null` must be translated into an empty envelope (`success: true,
data: []`) so callers can distinguish "nothing matched" from
"the wire broke".

This is the **empty-array pattern**.  Before this policy existed, every
affected call-site open-coded the same null-check + empty-envelope
branch.  A single seam now consolidates that:

```csharp
// Before — repeated at every affected site:
ReportsFieldData? data = conn.RequestReportComments();
List<string> comments = data?.reportComments ?? new List<string>();
if (comments.Count == 0)
    return CommandResult.Ok("...", new { Comments = new List<string>() });

// After — RequestExecutor policy:
ReportsFieldData data = _executor.ExecuteWithFallback(
    () => conn.RequestReportComments(),
    () => new ReportsFieldData { reportComments = new List<string>() });
```

## What `_executor.ExecuteWithFallback` provides

`Core.RequestExecutor.ExecuteWithFallback<T>(Func<T?> getter, Func<T> fallback)`
runs the getter and:

* returns the result when non-null,
* returns `fallback()` when the getter returns null **or throws** —
  exception-on-getter is treated the same as timeout (silently swallowed
  + fallback invoked).  This matches the "silent on timeout" semantics
  callers already rely on.

The fallback factory is typed: each call-site provides the empty
envelope shape that downstream code expects (an empty `List<string>`,
an empty `List<long>`, an empty ticker list).  No type erasure,
no `object` unboxing.

## Where the policy applies

The empty-array cluster:

| Site                                          | Migrated | Empty fallback shape                                                |
|-----------------------------------------------|----------|---------------------------------------------------------------------|
| `ReportsCommand.GetReportComments`            | yes      | `new ReportsFieldData { reportComments = new List<string>() }`      |
| `ReportsCommand.GetReportDates`               | yes      | `new ReportsFieldData { reportsDate = new List<long>() }`           |
| `ExchangeCommand.Ticker24`                    | yes      | `new TickerPrice24ListData { tickerPriceList = new List<…>() }`     |

Future sites in the same cluster should follow the same shape: a static
`RequestExecutor` field on the command class, then
`_executor.ExecuteWithFallback(...)` at the call-site.

## What it does NOT replace

`SendAndWait` inside `CoreConnection` still owns the circuit-breaker
and rate-limit interaction.  `RequestExecutor` is a command-layer
shim on top of `RequestX` getters, not a replacement for the wire
plumbing below them.

## Regression harness

`RequestExecutorPolicyTests` verifies:

1. Every site that historically used the inline empty-array pattern in
   ReportsCommand / ExchangeCommand has been migrated to
   `_executor.ExecuteWithFallback`.
2. The `RequestExecutor` field is present on the affected command
   classes.
3. The lineage marker comments survive at the new call-sites
   (preserved for searchability).

If a future change drops the executor adoption at one of these sites
without migrating the call-site to another central abstraction, the
Static test fails.
