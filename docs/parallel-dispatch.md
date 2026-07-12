# Parallel dispatch and the shared-connection daemon

The MCP server can process requests concurrently instead of one at a time. This
document describes the two dispatch modes, the concurrency model, client
compatibility, and how to roll it out safely.

## Why

The stdio MCP loop historically read one request, handled it to completion, then
read the next. A single slow request — for example a fleet operation that
touches many profiles — therefore blocked every other request behind it, and the
only way to get parallelism was to run multiple server processes, each of which
rebuilds the full per-profile connection stack (memory, sockets, and reconnect
pressure multiplied by the number of processes).

Parallelism and connection state are separable. The dispatch loop can serve
requests concurrently within a single process that owns one connection stack.

## Two modes

### Serial (default)

With no flag set, dispatch is **strictly serial**: one request is handled to
completion before the next is read, and responses are emitted in request order.
This is the historical behavior, byte-identical on the wire, and remains the
default so existing integrations are unaffected.

### Parallel (`MTC_MCP_PARALLEL=1`)

With the flag set, the read loop only reads and dispatches; each request is
handled on the thread pool. Requests are serialized by a **per-profile gate**:

| Request kind | Gate | Effect |
|---|---|---|
| A tool call carrying a `profile` | per-profile lock | different profiles run in parallel; requests to the same profile are serialized — one operation per connection at a time |
| A fleet tool (`mt_fleet_*`) or a connection tool with no profile | exclusive lock | runs alone, excluding all per-profile work (fleet commands fan out over every connection at once) |
| In-process tools (`mt_events_*`, `mt_metrics_get`, `mt_rate_status`) and non-`tools/call` methods | ungated | never blocked |

The per-profile lock preserves the invariant the serial loop provided — at most
one operation touches a given connection at a time — so per-connection code that
was correct under serial dispatch is unchanged. The per-profile locks are a
fixed stripe set, so arbitrary profile names cannot grow server state without
bound.

**Same-profile ordering.** The lock guarantees *mutual exclusion*, not arrival
order: two same-profile requests that are concurrently in flight are handled one
at a time, but may execute in either order (whichever thread acquires the lock
first). Serial mode, by contrast, always executes requests in the order they
were read. A client that needs a strict order between two dependent same-profile
requests (for example create-then-start) must **await each response before
sending the next** — which is the normal request/response pattern and is
unaffected. Only pipelining multiple un-awaited same-profile requests is exposed
to reordering.

## Client compatibility — responses may arrive out of order

In parallel mode a fast request can complete before a slow one that was issued
earlier, so responses are **not** emitted in request order. This is valid
JSON-RPC 2.0: every response carries the `id` of its request, and clients are
expected to correlate by `id`. The MCP SDK's `ClientSession` and `mcp-proxy` do
exactly this — each request is assigned an `id`, stored against a per-`id`
response slot, and matched when the response arrives — so out-of-order responses
are handled transparently. Stdout framing is serialized, so response frames are
never interleaved on the wire.

A client that instead assumes responses arrive strictly in request order should
keep the default serial mode.

## Shared-connection daemon (`--daemon <socket-path>`)

`--daemon` runs the server as a long-lived process that owns a single
`ConnectionManager` and exposes the same request API over a Unix domain socket.
Many clients connect to that one socket and multiplex over the shared connection
stack, instead of each client spawning its own server process and rebuilding the
whole per-profile stack. Requests flow through the same per-profile gate as
stdio. This is the path to eliminating duplicated connection stacks across
multiple callers; the stdio server (`--mcp`) is unchanged.

Because the daemon is a new mode with no legacy behavior to preserve, it
**always dispatches concurrently** — it does not read `MTC_MCP_PARALLEL` (that
flag governs only the stdio `--mcp` path). Daemon clients therefore must
correlate responses by `id`; the same out-of-order and same-profile-ordering
notes above apply unconditionally.

## Running the daemon in production

The daemon is a long-lived process. Its lifecycle, socket, and resource limits
are hardened as follows.

### Startup — one daemon per socket

On start the daemon inspects the socket path:

- **A live daemon is already listening** (a probe connection is accepted) → it
  logs and **exits `3`** rather than clobbering the running instance. Supervisors
  that restart on non-zero exit should treat `3` as "already running", not a
  crash loop.
- **A stale socket file** (nothing accepts a connection — a leftover from a
  previous run, or an unrelated file) → it is unlinked and replaced.

### Graceful shutdown

`SIGTERM` and `SIGINT` cancel the accept loop and drive an ordered shutdown: the
SSE server is stopped, the listener is closed, the **socket file is unlinked**,
and the shared `ConnectionManager` is disposed (closing every core connection).
A restart therefore always finds a clean path — no manual socket cleanup between
runs. On platforms without POSIX signals the registration is skipped silently.

### Socket location and permissions

The daemon `chmod`s its socket to **`0660`** (owner + group read/write, no world
access), so only the owning user and group can connect. Place the socket in a
directory only trusted principals can traverse — a per-service runtime directory
such as `/run/<service>/` owned by the service user with mode `0770`, rather than
a world-traversable temp dir. The socket path is the only access-control surface;
there is no in-band authentication.

### Event stream (SSE) — loopback-only, opt-out

The optional SSE event server binds **loopback only** (`127.0.0.1` / `localhost`)
— it is never exposed off-host. It is on by default and can be turned off with
`MTC_SSE_DISABLE=1` (the Unix socket remains the primary surface; disabling SSE
just drops the optional local event channel and frees its port).

### Bounding in-flight work

Total concurrently-dispatched requests across **all** clients are bounded by a
semaphore; the per-client read loop blocks when the bound is reached, which
backpressures the client rather than spawning unbounded work (and unbounded
thread-pool threads) under a burst. The bound defaults to **256** and is set with
`MTC_DAEMON_MAX_INFLIGHT`. The same bound applies to the parallel stdio path.

### Environment variables

| Variable | Default | Effect |
|---|---|---|
| `MTC_DAEMON_MAX_INFLIGHT` | `256` | Max requests dispatched concurrently across all clients (also bounds the parallel stdio path). |
| `MTC_SSE_DISABLE` | unset | `1` disables the loopback SSE event server. |

## Concurrency-safety

Enabling concurrent dispatch was gated on a review of every piece of process-wide
mutable state that two concurrently-handled requests could touch. The following
were made safe (the serial default's wire output is unchanged either way — the
added locks are uncontended and behavior-preserving):

- **V2 import parser** is now constructed per call, and the **algorithm
  clipboard file** access is synchronized — both close races newly possible only
  under concurrent dispatch (two imports / copies on different profiles at once).
- **`_activeConnectionName` failover**, the **connection-array cache**, and
  **per-client SSE writes** close pre-existing races driven by the always-on
  multi-worker connection pump and the SSE HTTP thread — reachable in serial mode
  too, now fixed.

## Rollout

`MTC_MCP_PARALLEL` is opt-in so it can be enabled per environment without a code
change and rolled back instantly. A safe sequence:

1. Ship with the default (serial) — no behavior change.
2. Enable `MTC_MCP_PARALLEL=1` for a client stack that correlates responses by
   `id` (the MCP SDK / `mcp-proxy`), and observe.
3. Promote to default once soaked, if desired.
